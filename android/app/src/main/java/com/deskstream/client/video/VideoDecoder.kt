package com.deskstream.client.video

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.os.SystemClock
import android.util.Log
import android.view.Surface
import java.util.ArrayDeque

/**
 * H.264 decoding via MediaCodec in **async mode**, rendering straight to a [Surface]. No
 * MediaExtractor/MediaPlayer -- we build Annex-B access units ourselves from the media
 * channel (see MediaReceiver/FrameAssembler) and feed them directly.
 *
 * Latency rules (see docs/ARCHITECTURE.md):
 *  - Every pipeline stage holds at most one (or two, briefly) frames; we never build a
 *    jitter buffer. [submitFrame] keeps at most 2 frames queued; a 3rd arriving forces the
 *    oldest queued ones out, counted as dropped, and asks the caller to request a fresh IDR
 *    and discard until the next keyframe (since we just created a reference gap).
 *  - Output buffers are released with render=true immediately on arrival; we never pace by
 *    presentation time.
 *
 * Not started until the first complete keyframe access unit is submitted (SPS/PPS are
 * in-band in that access unit per protocol §3, so no explicit CSD buffer is needed).
 *
 * Thread-safety: [submitFrame] is expected to be called from the media receive thread;
 * MediaCodec async callbacks run on their own dedicated [HandlerThread]. All shared state is
 * guarded by [lock].
 */
class VideoDecoder(
    /** Called (off the UI thread) whenever a queued frame had to be dropped or the codec hit
     * an error -- the caller should request an IDR and tell the media receiver to discard
     * until the next keyframe. */
    private val onDropOrError: () -> Unit,
    /** Called to return a frame buffer to its pool once this decoder is done with it (either
     * fed to the codec, or dropped without ever being fed). */
    private val onBufferRelease: (ByteArray) -> Unit,
    private val onFrameRendered: (latencyMs: Int) -> Unit = {}
) {
    private data class PendingFrame(val data: ByteArray, val length: Int, val frameId: Long)

    private val lock = Any()
    private val handlerThread = HandlerThread("VideoDecoderCallback").apply { start() }
    private val handler = Handler(handlerThread.looper)

    private var codec: MediaCodec? = null
    private var surface: Surface? = null
    private var width = 0
    private var height = 0
    private var started = false

    private val pendingFrames = ArrayDeque<PendingFrame>()
    private val pendingInputIndices = ArrayDeque<Int>()
    private val submitTimesUs = HashMap<Long, Long>()
    private var codecFramesInFlight = 0

    private val callback = object : MediaCodec.Callback() {
        override fun onInputBufferAvailable(mc: MediaCodec, index: Int) {
            synchronized(lock) {
                if (mc !== codec) return
                pendingInputIndices.addLast(index)
                drainLocked()
            }
        }

        override fun onOutputBufferAvailable(mc: MediaCodec, index: Int, info: MediaCodec.BufferInfo) {
            // Render immediately, never pace by PTS -- lowest latency to glass.
            try {
                mc.releaseOutputBuffer(index, true)
            } catch (e: IllegalStateException) {
                // codec was torn down concurrently (e.g. surface destroyed); safe to ignore
            }
            synchronized(lock) {
                if (mc === codec) {
                    codecFramesInFlight = (codecFramesInFlight - 1).coerceAtLeast(0)
                    drainLocked()
                }
            }
        }

        override fun onError(mc: MediaCodec, e: MediaCodec.CodecException) {
            Log.e(TAG, "decoder error (recoverable=${e.isRecoverable}): ${e.diagnosticInfo}", e)
            val old = synchronized(lock) {
                if (mc === codec) detachCodecLocked() else null
            }
            if (old != null) {
                // We are ON the codec's own callback thread here; stop()/release() can block,
                // and blocking the callback thread risks wedging the codec teardown itself.
                // Destroy on a throwaway thread instead -- stale callbacks for the old codec
                // are already ignored via the `mc !== codec` identity checks.
                Thread({ destroyCodec(old) }, "VideoDecoderErrRelease").start()
                onDropOrError()
            }
        }

        override fun onOutputFormatChanged(mc: MediaCodec, format: MediaFormat) {
            Log.i(TAG, "decoder output format changed: $format")
        }
    }

    /**
     * Targets a new stream session (fresh call after every STREAM_STARTED, per protocol §5 --
     * dimensions may differ, and even if they don't, frameId numbering restarts on the server
     * so any in-flight decoder state must not carry over). Always releases the current codec
     * (if any); a new one is created lazily on the next keyframe submitted.
     */
    fun resetForNewStream(surface: Surface, width: Int, height: Int) {
        val old = synchronized(lock) {
            val c = detachCodecLocked()
            this.surface = surface
            this.width = width
            this.height = height
            c
        }
        old?.let { destroyCodec(it) }
    }

    /**
     * Feeds one complete, ordered Annex-B access unit. Takes ownership of [data]; it is
     * always eventually returned via [onBufferRelease], whether it's fed to the codec,
     * dropped for being stale, or dropped while waiting for a keyframe.
     */
    fun submitFrame(data: ByteArray, length: Int, keyframe: Boolean, frameId: Long) {
        synchronized(lock) {
            if (!started) {
                if (!keyframe || surface == null || width <= 0 || height <= 0) {
                    onBufferRelease(data)
                    return
                }
                if (!configureAndStartLocked()) {
                    onBufferRelease(data)
                    return
                }
            }
            enqueueFrameLocked(PendingFrame(data, length, frameId))
        }
    }

    /** Full teardown -- call when the owning Surface is destroyed or the activity is
     * finishing. This instance must not be used afterward; create a new one for a new
     * surface. */
    fun release() {
        val old = synchronized(lock) {
            val c = detachCodecLocked()
            surface = null
            c
        }
        old?.let { destroyCodec(it) }
        handlerThread.quitSafely()
    }

    // ---- must hold `lock` -------------------------------------------------------------

    private fun enqueueFrameLocked(frame: PendingFrame) {
        pendingFrames.addLast(frame)
        var dropped = false
        while (pendingFrames.size > 1) {
            val old = pendingFrames.removeFirst()
            onBufferRelease(old.data)
            dropped = true
        }
        if (dropped) {
            // Notify outside the lock to avoid any re-entrancy surprises from the caller.
            handler.post { onDropOrError() }
        }
        drainLocked()
    }

    private fun drainLocked() {
        while (pendingFrames.isNotEmpty() && pendingInputIndices.isNotEmpty() &&
            codecFramesInFlight < MAX_CODEC_FRAMES_IN_FLIGHT
        ) {
            val frame = pendingFrames.removeFirst()
            val index = pendingInputIndices.removeFirst()
            feedInputBufferLocked(index, frame)
        }
    }

    private fun feedInputBufferLocked(index: Int, frame: PendingFrame) {
        // `finally` is the single release point for frame.data -- every branch below just
        // falls through to it rather than releasing early, to avoid a double-release (which
        // would let two frames alias the same pooled buffer).
        val c = codec
        try {
            if (c == null) return
            val inputBuffer = c.getInputBuffer(index) ?: return
            inputBuffer.clear()
            inputBuffer.put(frame.data, 0, frame.length)
            // Monotonic local clock for PTS; the wire ptsMs is stats-only per protocol §3 and
            // must never be used to pace rendering.
            var ptsUs = SystemClock.elapsedRealtimeNanos() / 1000
            while (submitTimesUs.containsKey(ptsUs)) ptsUs++
            c.queueInputBuffer(index, 0, frame.length, ptsUs, 0)
            submitTimesUs[ptsUs] = ptsUs
            codecFramesInFlight++
        } catch (e: Exception) {
            Log.w(TAG, "queueInputBuffer failed", e)
        } finally {
            onBufferRelease(frame.data)
        }
    }

    private fun configureAndStartLocked(): Boolean {
        var c: MediaCodec? = null
        return try {
            c = MediaCodec.createDecoderByType(MIME_TYPE)
            c.setCallback(callback, handler) // must precede configure() for async mode
            val format = MediaFormat.createVideoFormat(MIME_TYPE, width, height)

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                try {
                    val caps = c.codecInfo.getCapabilitiesForType(MIME_TYPE)
                    if (caps.isFeatureSupported(MediaCodecInfo.CodecCapabilities.FEATURE_LowLatency)) {
                        format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
                    }
                } catch (e: Exception) {
                    Log.w(TAG, "low-latency capability query failed", e)
                }
            }
            format.setInteger(MediaFormat.KEY_PRIORITY, 0) // realtime
            // Hints the codec to run flat-out. Short.MAX_VALUE misbehaves on some devices;
            // 240 is a safe "faster than any real content" value.
            format.setInteger(MediaFormat.KEY_OPERATING_RATE, 240)

            c.configure(format, surface, null, 0)
            c.setOnFrameRenderedListener({ renderedCodec, presentationTimeUs, nanoTime ->
                val submittedUs = synchronized(lock) {
                    if (renderedCodec === codec) submitTimesUs.remove(presentationTimeUs) else null
                }
                if (submittedUs != null) {
                    val latencyMs = ((nanoTime / 1000L - submittedUs) / 1000L)
                        .coerceIn(0L, 60_000L).toInt()
                    onFrameRendered(latencyMs)
                }
            }, handler)
            c.start()
            codec = c
            started = true
            true
        } catch (e: Exception) {
            Log.e(TAG, "failed to configure/start decoder", e)
            try {
                c?.release()
            } catch (e2: Exception) {
                // ignore
            }
            false
        }
    }

    /** Detaches the current codec from this decoder's state (so all subsequent calls and
     * stale callbacks see "no codec") and drains the pending queues, WITHOUT the potentially
     * blocking stop()/release() -- callers do that outside the lock via [destroyCodec]. */
    private fun detachCodecLocked(): MediaCodec? {
        val c = codec
        codec = null
        started = false
        while (pendingFrames.isNotEmpty()) {
            onBufferRelease(pendingFrames.removeFirst().data)
        }
        pendingInputIndices.clear()
        submitTimesUs.clear()
        codecFramesInFlight = 0
        return c
    }

    /** Must be called OUTSIDE [lock]: stop() and release() may block, and holding the lock
     * across them would stall the receive thread (submitFrame) and codec callbacks. */
    private fun destroyCodec(c: MediaCodec) {
        try {
            c.stop()
        } catch (e: Exception) {
            // ignore
        }
        try {
            c.release()
        } catch (e: Exception) {
            // ignore
        }
    }

    companion object {
        private const val TAG = "VideoDecoder"
        private const val MIME_TYPE = "video/avc"
        private const val MAX_CODEC_FRAMES_IN_FLIGHT = 1
    }
}
