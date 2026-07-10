package com.deskstream.client.net

import android.os.SystemClock
import android.util.Log
import com.deskstream.client.proto.MediaPacketHeader
import com.deskstream.client.proto.GamepadPacket
import com.deskstream.client.proto.MousePacket
import com.deskstream.client.proto.CursorPacket
import com.deskstream.client.proto.CursorPosition
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketTimeoutException
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

/**
 * Media channel: UDP, server -> client, per docs/PROTOCOL.md §3.
 *
 * Owns a dedicated receive thread reading into a single preallocated 1500-byte buffer,
 * parses the 20-byte big-endian header, feeds packets to a [FrameAssembler] (reassembly +
 * XOR-FEC recovery + drop/discard rules), and periodically reports STATS on the control
 * channel. Also performs the DSMH hole-punch handshake from the same socket used to
 * receive, per §2.3.
 *
 * One-shot: call [start] once, [stop] once. Create a new instance for a new stream.
 */
data class StreamStats(
    val fps: Int,
    val kbps: Int,
    val framesDropped: Int,
    val lossPercent: Float,
    val serverPipelineP95Ms: Int,
    val captureToReceiveP95Ms: Int,
    val decodeToSurfaceP95Ms: Int
)

class MediaReceiver(
    /** Invoked (on the receive thread) with a complete, ordered, decodable access unit.
     * The callee takes ownership of [data] and MUST eventually pass it back via
     * [BufferPool.release] on [bufferPool] once done with it (VideoDecoder does this right
     * after copying into a MediaCodec input buffer). */
    private val onFrame: (
        data: ByteArray,
        length: Int,
        keyframe: Boolean,
        frameId: Long,
        captureToReceiveMs: Int
    ) -> Unit,
    /** Optional: mirrors each 1 s STATS flush for a local UI overlay. Called off the main
     * thread -- the caller must post to the UI thread itself. */
    private val onStats: (StreamStats) -> Unit = {},
    private val onStalled: () -> Unit = {},
    private val onCursorPosition: (CursorPosition) -> Unit = {}
) {
    val bufferPool = BufferPool()

    private val framesOk = AtomicInteger(0)
    private val framesDropped = AtomicInteger(0)
    private val bytesReceived = AtomicLong(0)

    private val frameAssembler = FrameAssembler(
        bufferPool = bufferPool,
        onFrameComplete = { buf, len, keyframe, frameId, ptsMs, pipelineDelayMs ->
            framesOk.incrementAndGet()
            val latencyMs = captureToReceiveLatencyMs(ptsMs)
            if (latencyMs >= 0) captureLatency.add(latencyMs)
            encoderLatency.add(pipelineDelayMs)
            onFrame(buf, len, keyframe, frameId, latencyMs)
        },
        onFrameDropped = {
            framesDropped.incrementAndGet()
            ControlClient.requestIdr()
        }
    )

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private var statsJob: Job? = null
    private var watchdogJob: Job? = null
    private var socket: DatagramSocket? = null
    private var thread: Thread? = null
    private val gamepadSendGate = Any()
    private var gamepadDatagram: DatagramPacket? = null
    private var mouseDatagram: DatagramPacket? = null
    @Volatile private var running = false
    private var serverIp: String = ""
    private var mediaPort: Int = 0
    private var streamClockBaseUs: Long = 0
    private var lastPtsRawMs = -1L
    private var ptsEpochMs = 0L
    /** Any packet, including DSHB, proves the UDP mapping/server socket is still alive. */
    @Volatile private var lastPacketAt = 0L
    /** Valid media only; used to request an IDR without restarting a genuinely static desktop. */
    @Volatile private var lastVideoPacketAt = 0L
    private val captureLatency = LatencyWindow()
    private val decoderLatency = LatencyWindow()
    private val encoderLatency = LatencyWindow()

    fun start(serverIp: String, mediaPort: Int, streamClockBaseUs: Long) {
        this.serverIp = serverIp
        this.mediaPort = mediaPort
        this.streamClockBaseUs = streamClockBaseUs
        lastPtsRawMs = -1L
        ptsEpochMs = 0L
        running = true
        val now = SystemClock.elapsedRealtime()
        lastPacketAt = now
        lastVideoPacketAt = now

        val sock = try {
            DatagramSocket(null).apply {
                reuseAddress = true
                bind(InetSocketAddress(0))
                try {
                    receiveBufferSize = RECV_SOCKET_BUFFER_BYTES
                } catch (e: Exception) {
                    Log.w(TAG, "could not request ${RECV_SOCKET_BUFFER_BYTES}B receive buffer", e)
                }
                soTimeout = SOCKET_TIMEOUT_MS
            }
        } catch (e: Exception) {
            Log.e(TAG, "failed to open media socket", e)
            running = false
            return
        }
        socket = sock

        val address = try {
            InetAddress.getByName(serverIp)
        } catch (e: Exception) {
            Log.e(TAG, "failed to resolve media server", e)
            sock.close()
            socket = null
            running = false
            return
        }
        // Filter inbound datagrams in the kernel to the negotiated server endpoint. This also
        // prevents unrelated LAN UDP traffic from falsely satisfying the stall watchdog.
        try {
            sock.connect(address, mediaPort)
        } catch (e: Exception) {
            Log.e(TAG, "failed to connect media socket", e)
            sock.close()
            socket = null
            running = false
            return
        }
        gamepadDatagram = DatagramPacket(ByteArray(GamepadPacket.SIZE), GamepadPacket.SIZE, address, mediaPort)
        mouseDatagram = DatagramPacket(ByteArray(MousePacket.SIZE), MousePacket.SIZE, address, mediaPort)
        // TCP announces the bound port reliably; DSMH remains a NAT/firewall fallback.
        ControlClient.mediaReady(sock.localPort)

        thread = Thread({ receiveLoop(sock, address) }, "MediaReceiver").apply { start() }
        statsJob = scope.launch { statsLoop() }
        watchdogJob = scope.launch { watchdogLoop(sock, address) }
    }

    fun stop() {
        running = false
        statsJob?.cancel()
        statsJob = null
        watchdogJob?.cancel()
        watchdogJob = null
        socket?.close() // unblocks the blocking receive() in the worker thread
        socket = null
        synchronized(gamepadSendGate) {
            gamepadDatagram = null
            mouseDatagram = null
        }
        thread?.let { t ->
            try {
                t.join(500)
            } catch (e: InterruptedException) {
                Thread.currentThread().interrupt()
            }
        }
        thread = null
        frameAssembler.reset()
    }

    /** Called by the video decoder when it had to drop a frame from its feed queue (its own
     * pipeline fell behind the network) or hit a decode error. Same remedy as an assembly-level
     * drop: count it, ask the server for a fresh IDR, and stop feeding anything until the next
     * keyframe so we never decode across a reference gap. */
    fun notifyExternalDrop() {
        framesDropped.incrementAndGet()
        ControlClient.requestIdr()
        frameAssembler.requestDiscardUntilKeyframe()
    }

    /** Sends one already-serialized controller snapshot without allocating a DatagramPacket. */
    fun sendGamepadPacket(data: ByteArray) {
        if (!running || data.size != GamepadPacket.SIZE) return
        synchronized(gamepadSendGate) {
            val sock = socket ?: return
            val packet = gamepadDatagram ?: return
            try {
                packet.setData(data, 0, data.size)
                sock.send(packet)
            } catch (e: IOException) {
                if (running) Log.w(TAG, "gamepad packet send failed", e)
            }
        }
    }

    fun sendMousePacket(data: ByteArray) {
        if (!running || data.size != MousePacket.SIZE) return
        synchronized(gamepadSendGate) {
            val sock = socket ?: return
            val packet = mouseDatagram ?: return
            try {
                packet.setData(data, 0, data.size)
                sock.send(packet)
            } catch (e: IOException) {
                if (running) Log.w(TAG, "mouse packet send failed", e)
            }
        }
    }

    fun recordDecoderSurfaceLatency(latencyMs: Int) {
        if (latencyMs >= 0) decoderLatency.add(latencyMs)
    }

    private fun receiveLoop(sock: DatagramSocket, serverAddress: InetAddress) {
        val buf = ByteArray(RECV_PACKET_BUFFER_BYTES)
        val packet = DatagramPacket(buf, buf.size)
        var receivedAny = false
        var lastHolePunchAt = SystemClock.elapsedRealtime()
        sendHolePunch(sock, serverAddress)
        // The server may have produced its startup IDR before it learned this UDP endpoint.
        // Requesting another one immediately guarantees that the first frame the decoder sees
        // is independently decodable, even when UDP and TCP arrive in the opposite order.
        ControlClient.requestIdr()

        while (running) {
            packet.setLength(buf.size) // receive() shrinks this; must reset every iteration
            try {
                sock.receive(packet)
            } catch (e: SocketTimeoutException) {
                if (!receivedAny) {
                    val now = SystemClock.elapsedRealtime()
                    if (now - lastHolePunchAt >= 1000) {
                        sendHolePunch(sock, serverAddress)
                        lastHolePunchAt = now
                    }
                }
                continue
            } catch (e: IOException) {
                break // socket closed by stop()
            }

            if (!receivedAny) {
                receivedAny = true // any datagram on this socket means the punch landed
            }

            val length = packet.length
            lastPacketAt = SystemClock.elapsedRealtime()
            bytesReceived.addAndGet(length.toLong())

            val cursor = CursorPacket.parse(buf, length)
            if (cursor != null) {
                onCursorPosition(cursor)
                continue
            }

            val header = MediaPacketHeader.parse(buf, length) ?: continue
            if (header.version != 1) continue
            // Only valid video proves the encode/media path is alive. Heartbeats and cursor
            // feedback must not mask a frozen picture from the stall watchdog.
            lastVideoPacketAt = SystemClock.elapsedRealtime()

            frameAssembler.onPacket(header, buf, MediaPacketHeader.HEADER_SIZE)
        }
    }

    private fun sendHolePunch(sock: DatagramSocket, serverAddress: InetAddress) {
        try {
            val bytes = HOLE_PUNCH_MESSAGE.toByteArray(Charsets.US_ASCII)
            sock.send(DatagramPacket(bytes, bytes.size, serverAddress, mediaPort))
        } catch (e: IOException) {
            Log.w(TAG, "hole punch send failed", e)
        }
    }

    private suspend fun statsLoop() {
        var lastFlush = SystemClock.elapsedRealtime()
        while (currentCoroutineContext().isActive) {
            delay(1000)
            val now = SystemClock.elapsedRealtime()
            val intervalMs = now - lastFlush
            lastFlush = now
            val ok = framesOk.getAndSet(0)
            val dropped = framesDropped.getAndSet(0)
            val bytes = bytesReceived.getAndSet(0)
            val captureP95 = captureLatency.drainP95()
            val decoderP95 = decoderLatency.drainP95()
            val encoderP95 = encoderLatency.drainP95()
            ControlClient.sendStats(ok, dropped, bytes, intervalMs, captureP95, decoderP95)

            val total = ok + dropped
            val lossPercent = if (total > 0) dropped * 100f / total else 0f
            val kbps = if (intervalMs > 0) (bytes * 8 / intervalMs).toInt() else 0
            onStats(StreamStats(
                fps = ok,
                kbps = kbps,
                framesDropped = dropped,
                lossPercent = lossPercent,
                serverPipelineP95Ms = encoderP95,
                captureToReceiveP95Ms = captureP95,
                decodeToSurfaceP95Ms = decoderP95
            ))
        }
    }

    private suspend fun watchdogLoop(sock: DatagramSocket, serverAddress: InetAddress) {
        var stallReported = false
        while (currentCoroutineContext().isActive) {
            delay(500)
            val now = SystemClock.elapsedRealtime()
            val videoSilenceMs = now - lastVideoPacketAt
            if (videoSilenceMs >= VIDEO_REPUNCH_MS) {
                sendHolePunch(sock, serverAddress)
                ControlClient.requestIdr()
            }
            // DSHB is deliberately sent when the desktop is static. It should keep a still
            // image stable rather than causing an endless stop/start loop, but it must not
            // prevent an IDR request when fresh video ought to be arriving.
            val transportSilenceMs = now - lastPacketAt
            if (transportSilenceMs >= VIDEO_STALL_MS && !stallReported) {
                stallReported = true
                onStalled()
            } else if (transportSilenceMs < VIDEO_REPUNCH_MS) {
                stallReported = false
            }
        }
    }

    private fun captureToReceiveLatencyMs(ptsMs: Long): Int {
        if (streamClockBaseUs <= 0L || !ControlClient.clockSynchronized) return -1
        val serverCaptureUs = streamClockBaseUs + unwrapPtsMs(ptsMs) * 1000L
        val captureOnClientClockUs = serverCaptureUs - ControlClient.serverClockOffsetUs
        return ((SystemClock.elapsedRealtimeNanos() / 1000L - captureOnClientClockUs) / 1000L)
            .coerceIn(0L, 60_000L).toInt()
    }

    private fun unwrapPtsMs(raw: Long): Long {
        if (lastPtsRawMs >= 0L && raw < lastPtsRawMs && lastPtsRawMs - raw > 0x80000000L) {
            ptsEpochMs += 0x1_0000_0000L
        }
        lastPtsRawMs = raw
        return ptsEpochMs + raw
    }

    companion object {
        private const val TAG = "MediaReceiver"
        private const val HOLE_PUNCH_MESSAGE = "DSMH"
        private const val RECV_PACKET_BUFFER_BYTES = 1500
        // IDR access units arrive as a burst. 256 KiB caused a loss -> IDR -> larger burst
        // feedback loop on real Wi-Fi devices.
        private const val RECV_SOCKET_BUFFER_BYTES = 1024 * 1024
        private const val SOCKET_TIMEOUT_MS = 1000
        private const val VIDEO_REPUNCH_MS = 1500L
        private const val VIDEO_STALL_MS = 4000L
    }
}

private class LatencyWindow {
    private val values = ArrayList<Int>(256)
    private val lock = Any()

    fun add(value: Int) = synchronized(lock) {
        if (values.size >= 256) values.removeAt(0)
        values.add(value)
    }

    fun drainP95(): Int = synchronized(lock) {
        if (values.isEmpty()) return@synchronized -1
        values.sort()
        val index = ((values.size - 1) * 0.95).toInt()
        val result = values[index]
        values.clear()
        result
    }
}

// =========================================================================================
// Frame assembly (§3.1) + XOR FEC recovery (§3.2)
// =========================================================================================

private const val PACKET_PAYLOAD_MAX = 1200
// Tolerates Wi-Fi packet reordering without adding a render queue: completed frames still
// go directly to the separately bounded decoder input queue.
private const val MAX_INFLIGHT_FRAMES = 4
/** Sanity ceiling on packetCount to refuse to allocate absurd buffers for a corrupt header. */
private const val MAX_REASONABLE_PACKET_COUNT = 4096

/**
 * Reassembles the media stream by frameId, recovers isolated packet loss via XOR parity, and
 * enforces the "never decode across a reference gap" rule: any drop (assembly full, a newer
 * frame completing while an older one is still incomplete, or an external drop from the
 * decoder) puts the assembler into discard-until-keyframe mode.
 *
 * Not thread-safe by itself -- [onPacket] must only be called from a single thread (the
 * MediaReceiver's dedicated receive thread). [requestDiscardUntilKeyframe] may be called from
 * any thread (it just sets a volatile flag).
 */
private class FrameAssembler(
    private val bufferPool: BufferPool,
    private val onFrameComplete: (
        buf: ByteArray,
        length: Int,
        keyframe: Boolean,
        frameId: Long,
        ptsMs: Long,
        pipelineDelayMs: Int
    ) -> Unit,
    private val onFrameDropped: () -> Unit
) {
    private val inFlight = LinkedHashMap<Long, InFlightFrame>()
    private var dropWatermark = -1L
    @Volatile private var discardingUntilKeyframe = false

    fun requestDiscardUntilKeyframe() {
        discardingUntilKeyframe = true
    }

    fun reset() {
        for (f in inFlight.values) releaseFrame(f)
        inFlight.clear()
        dropWatermark = -1L
        discardingUntilKeyframe = false
    }

    fun onPacket(header: MediaPacketHeader, buf: ByteArray, payloadOffset: Int) {
        if (header.frameId <= dropWatermark) return // stale/duplicate/late
        if (header.packetCount <= 0 || header.packetCount > MAX_REASONABLE_PACKET_COUNT) return
        // fecCount is attacker/corruption-controlled independent of packetCount; bound it too
        // so a bogus header can't force a huge array allocation.
        if (header.fecCount < 0 || header.fecCount > MAX_REASONABLE_PACKET_COUNT) return
        if (header.fec && (header.packetIndex < 0)) return
        // Every media datagram's payload is <=1200 bytes per §3; a larger declared payloadLen
        // (corrupt header or non-conformant sender) would overflow into the next packet's slot
        // in the assembly buffer, since slots are fixed at PACKET_PAYLOAD_MAX apart.
        if (header.payloadLen < 0 || header.payloadLen > PACKET_PAYLOAD_MAX) return

        if (discardingUntilKeyframe) {
            // Ignore non-keyframe packets, but do NOT advance dropWatermark for them: a
            // keyframe with a *lower* frameId may still be assembling (UDP reordering), and
            // raising the watermark past it would reject that keyframe's remaining packets
            // and stall recovery. The watermark only advances when a frame completes or is
            // definitively dropped from assembly.
            if (!header.keyframe) return
            // A keyframe packet arrived; fall through to normal processing. The
            // discardingUntilKeyframe flag itself is cleared once the frame *completes*
            // (see completeFrame), not on the first packet, in case this keyframe is itself
            // incomplete and needs to be dropped too.
        }

        var frame = inFlight[header.frameId]
        if (frame == null) {
            if (inFlight.size >= MAX_INFLIGHT_FRAMES) {
                val oldestId = inFlight.keys.minOrNull()
                if (oldestId != null) {
                    val f = inFlight.remove(oldestId)
                    if (f != null) releaseFrame(f)
                    dropWatermark = maxOf(dropWatermark, oldestId)
                    triggerDrop()
                }
                // If dropping just put us into discard mode and this packet isn't a keyframe,
                // don't bother starting assembly for it. (No watermark advance -- see the
                // discard-mode comment above.)
                if (discardingUntilKeyframe && !header.keyframe) return
            }
            val assemblyBuf = bufferPool.acquire(header.packetCount * PACKET_PAYLOAD_MAX)
            frame = InFlightFrame(
                header.frameId,
                header.packetCount,
                header.fecCount,
                header.keyframe,
                header.ptsMs,
                header.pipelineDelayMs,
                assemblyBuf,
                bufferPool
            )
            inFlight[header.frameId] = frame
        } else if (frame.packetCount != header.packetCount || frame.fecCount != header.fecCount) {
            // Inconsistent header for a frameId we're already assembling -- ignore the packet.
            return
        }

        if (header.fec) {
            frame.onFecPacket(header, buf, payloadOffset)
        } else {
            frame.onDataPacket(header, buf, payloadOffset)
        }

        if (frame.isComplete) {
            inFlight.remove(frame.frameId)
            dropWatermark = maxOf(dropWatermark, frame.frameId)
            completeFrame(frame)
        }
    }

    private fun completeFrame(frame: InFlightFrame) {
        // A newer frame just became ready while older ones are still incomplete: those older
        // frames can never be usefully decoded now (their reference chain is about to be
        // skipped), so drop them.
        val olderIncomplete = inFlight.keys.filter { it < frame.frameId }
        if (olderIncomplete.isNotEmpty()) {
            for (fid in olderIncomplete) {
                val f = inFlight.remove(fid) ?: continue
                releaseFrame(f)
                dropWatermark = maxOf(dropWatermark, fid)
            }
            triggerDrop()
        }

        if (discardingUntilKeyframe) {
            if (frame.keyframe) {
                discardingUntilKeyframe = false
            } else {
                bufferPool.release(frame.assemblyBuf)
                return
            }
        }

        val totalLen = (frame.packetCount - 1) * PACKET_PAYLOAD_MAX + frame.lastPacketLen
        onFrameComplete(
            frame.assemblyBuf,
            totalLen,
            frame.keyframe,
            frame.frameId,
            frame.ptsMs,
            frame.pipelineDelayMs
        )
    }

    private fun triggerDrop() {
        discardingUntilKeyframe = true
        onFrameDropped()
    }

    private fun releaseFrame(frame: InFlightFrame) {
        bufferPool.release(frame.assemblyBuf)
        frame.releaseFecBuffers()
    }
}

/** Assembly state for a single frameId. */
private class InFlightFrame(
    val frameId: Long,
    val packetCount: Int,
    val fecCount: Int,
    val keyframe: Boolean,
    val ptsMs: Long,
    val pipelineDelayMs: Int,
    val assemblyBuf: ByteArray,
    private val pool: BufferPool
) {
    private val dataPresent = BooleanArray(packetCount)
    private var dataPresentCount = 0
    var lastPacketLen = -1
        private set

    private val fecPayload = arrayOfNulls<ByteArray>(fecCount)
    private val fecLen = IntArray(fecCount) { -1 }

    val isComplete: Boolean
        get() = dataPresentCount == packetCount && lastPacketLen >= 0

    fun onDataPacket(header: MediaPacketHeader, src: ByteArray, srcOffset: Int) {
        val idx = header.packetIndex
        if (idx < 0 || idx >= packetCount) return
        if (dataPresent[idx]) return // duplicate
        val destOffset = idx * PACKET_PAYLOAD_MAX
        System.arraycopy(src, srcOffset, assemblyBuf, destOffset, header.payloadLen)
        dataPresent[idx] = true
        dataPresentCount++
        if (idx == packetCount - 1) {
            lastPacketLen = header.payloadLen
        }
        tryRecoverGroup(groupOf(idx))
    }

    fun onFecPacket(header: MediaPacketHeader, src: ByteArray, srcOffset: Int) {
        val g = header.packetIndex
        if (g < 0 || g >= fecCount) return
        if (fecLen[g] >= 0) return // duplicate parity for this group
        val buf = pool.acquire(header.payloadLen)
        System.arraycopy(src, srcOffset, buf, 0, header.payloadLen)
        fecPayload[g] = buf
        fecLen[g] = header.payloadLen
        tryRecoverGroup(g)
    }

    fun releaseFecBuffers() {
        for (i in fecPayload.indices) {
            fecPayload[i]?.let { pool.release(it) }
            fecPayload[i] = null
        }
    }

    private fun groupOf(dataIndex: Int) = dataIndex / 8

    /** XOR recovery per §3.2: if exactly one data packet in group [g] is missing and its
     * parity packet is present, reconstruct it. If two or more are missing, this group simply
     * can't be recovered (frame stays incomplete -> eventually dropped upstream). */
    private fun tryRecoverGroup(g: Int) {
        if (g < 0 || g >= fecCount) return
        val parityLen = fecLen[g]
        if (parityLen < 0) return // no parity for this group yet

        val groupStart = g * 8
        val groupEnd = minOf(groupStart + 8, packetCount)
        var missingIdx = -1
        var missingCount = 0
        for (i in groupStart until groupEnd) {
            if (!dataPresent[i]) {
                missingCount++
                missingIdx = i
            }
        }

        if (missingCount == 0) {
            // Group already fully present from real data packets; parity no longer needed.
            releaseGroupParity(g)
            return
        }
        if (missingCount > 1) return // unrecoverable for now

        val parityBuf = fecPayload[g] ?: return
        val destOffset = missingIdx * PACKET_PAYLOAD_MAX
        for (k in 0 until parityLen) {
            var v = parityBuf[k]
            for (i in groupStart until groupEnd) {
                if (i == missingIdx || !dataPresent[i]) continue
                val iLen = if (i == packetCount - 1) lastPacketLen else PACKET_PAYLOAD_MAX
                val iOffset = i * PACKET_PAYLOAD_MAX
                val b: Byte = if (k < iLen) assemblyBuf[iOffset + k] else 0
                v = (v.toInt() xor b.toInt()).toByte()
            }
            assemblyBuf[destOffset + k] = v
        }

        dataPresent[missingIdx] = true
        dataPresentCount++
        if (missingIdx == packetCount - 1) {
            // Per §3.2: the recovered last packet's true length is the full parity length;
            // the extra bytes beyond the real NAL data are harmless trailing zeros.
            lastPacketLen = parityLen
        }
        releaseGroupParity(g)
    }

    private fun releaseGroupParity(g: Int) {
        fecPayload[g]?.let { pool.release(it) }
        fecPayload[g] = null
    }
}
