package com.deskstream.client.net

import android.os.Process
import android.os.SystemClock
import android.util.Log
import com.deskstream.client.proto.MediaPacketHeader
import com.deskstream.client.proto.FrameTracePacket
import com.deskstream.client.proto.GamepadPacket
import com.deskstream.client.proto.MousePacket
import com.deskstream.client.proto.CursorPacket
import com.deskstream.client.proto.CursorPosition
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.channels.Channel
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
import java.util.TreeMap
import java.util.concurrent.atomic.AtomicBoolean
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
    val assembledFps: Int,
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
    private val onCursorPosition: (CursorPosition) -> Unit = {},
    private val onStartupError: (String) -> Unit = {}
) {
    private enum class InputPacketKind { GAMEPAD, MOUSE }

    private data class OutboundInputPacket(
        val kind: InputPacketKind,
        val data: ByteArray
    )

    val bufferPool = BufferPool()

    private val framesAssembled = AtomicInteger(0)
    private val framesDecoded = AtomicInteger(0)
    private val framesDropped = AtomicInteger(0)
    private val assemblyFramesDropped = AtomicInteger(0)
    private val decoderFramesDropped = AtomicInteger(0)
    private val videoPacketsReceived = AtomicInteger(0)
    private val fecPacketsReceived = AtomicInteger(0)
    private val bytesReceived = AtomicLong(0)

    private val frameAssembler = FrameAssembler(
        bufferPool = bufferPool,
        nowUs = { SystemClock.elapsedRealtimeNanos() / 1000L },
        onFrameComplete = { buf, len, keyframe, frameId, ptsMs, pipelineDelayMs, timing ->
            framesAssembled.incrementAndGet()
            lastCompletedFrameAt = timing.completedUs / 1000L
            hasCompletedFrame = true
            frameTrace.recordReceive(frameId, timing.firstReceiveUs, timing.lastReceiveUs)
            frameTrace.recordAssemble(frameId, timing.completedUs)
            val latencyMs = captureToReceiveLatencyMs(ptsMs)
            if (latencyMs >= 0) captureLatency.add(latencyMs)
            encoderLatency.add(pipelineDelayMs)
            onFrame(buf, len, keyframe, frameId, latencyMs)
        },
        onFrameDropped = { requestIdr, droppedFrames ->
            framesDropped.addAndGet(droppedFrames)
            assemblyFramesDropped.addAndGet(droppedFrames)
            // One missing H.264 reference invalidates every following predictive frame. Ask for
            // the replacement IDR immediately; ControlClient coalesces duplicate requests, while
            // waiting for a second drop can deadlock because the assembler now discards P-frames.
            if (requestIdr) ControlClient.requestIdr()
        }
    )

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private var statsJob: Job? = null
    private var watchdogJob: Job? = null
    private var socket: DatagramSocket? = null
    private var thread: Thread? = null
    private val inputSendGate = Any()
    private val inputSendQueue = Channel<OutboundInputPacket>(
        capacity = INPUT_SEND_QUEUE_CAPACITY,
        onBufferOverflow = BufferOverflow.DROP_OLDEST
    )
    private var inputSendJob: Job? = null
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
    /** Last syntactically valid video/FEC datagram; DSHB heartbeats do not update this. */
    @Volatile private var lastValidVideoPacketAt = 0L
    /** Complete access units, unlike raw packets, prove reassembly/keyframe recovery is healthy. */
    @Volatile private var lastCompletedFrameAt = 0L
    @Volatile private var hasCompletedFrame = false
    private val captureLatency = LatencyWindow()
    private val decoderLatency = LatencyWindow()
    private val encoderLatency = LatencyWindow()
    private val frameTrace = FrameTraceCollector(
        clockOffsetUs = {
            if (ControlClient.clockSynchronized) ControlClient.serverClockOffsetUs else null
        }
    )

    fun start(serverIp: String, mediaPort: Int, streamClockBaseUs: Long): Boolean {
        this.serverIp = serverIp
        this.mediaPort = mediaPort
        this.streamClockBaseUs = streamClockBaseUs
        lastPtsRawMs = -1L
        ptsEpochMs = 0L
        running = true
        val now = SystemClock.elapsedRealtime()
        lastPacketAt = now
        lastValidVideoPacketAt = now
        lastCompletedFrameAt = now
        hasCompletedFrame = false

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
            onStartupError(e.message ?: "Media socket could not be opened")
            running = false
            return false
        }
        socket = sock

        val address = try {
            InetAddress.getByName(serverIp)
        } catch (e: Exception) {
            Log.e(TAG, "failed to resolve media server", e)
            onStartupError(e.message ?: "Media server address could not be resolved")
            sock.close()
            socket = null
            running = false
            return false
        }
        // Keep the UDP socket unconnected, as in the stable v0.2 receiver. Some Samsung/OEM
        // network stacks reject or later invalidate connected datagram sockets during Wi-Fi
        // route changes. We validate the source endpoint explicitly in receiveLoop instead.
        gamepadDatagram = DatagramPacket(ByteArray(GamepadPacket.SIZE), GamepadPacket.SIZE, address, mediaPort)
        mouseDatagram = DatagramPacket(ByteArray(MousePacket.SIZE), MousePacket.SIZE, address, mediaPort)
        // Mouse callbacks run on Android's main thread, where DatagramSocket.send() throws
        // NetworkOnMainThreadException. The controller producer also must not block on the
        // network. One bounded IO sender keeps both streams ordered and low-latency without
        // creating an unbounded backlog.
        inputSendJob = scope.launch(Dispatchers.IO) { inputSendLoop() }
        // TCP announces the bound port reliably; DSMH remains a NAT/firewall fallback.
        ControlClient.prepareForMediaEpoch()
        ControlClient.mediaReady(sock.localPort)

        val actualReceiveBuffer = try { sock.receiveBufferSize } catch (_: Exception) { -1 }
        Log.i(TAG, "media UDP receive buffer: requested=${RECV_SOCKET_BUFFER_BYTES}B actual=${actualReceiveBuffer}B")
        thread = Thread({ receiveLoop(sock, address) }, "MediaReceiver").apply { start() }
        statsJob = scope.launch { statsLoop() }
        watchdogJob = scope.launch { watchdogLoop(sock, address) }
        return true
    }

    fun stop() {
        running = false
        statsJob?.cancel()
        statsJob = null
        watchdogJob?.cancel()
        watchdogJob = null
        inputSendJob?.cancel()
        inputSendJob = null
        inputSendQueue.cancel()
        socket?.close() // unblocks the blocking receive() in the worker thread
        socket = null
        synchronized(inputSendGate) {
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
        frameTrace.clear()
    }

    /** Called by the video decoder when it had to drop a frame from its feed queue (its own
     * pipeline fell behind the network) or hit a decode error. Same remedy as an assembly-level
     * drop: count it, ask the server for a fresh IDR, and stop feeding anything until the next
     * keyframe so we never decode across a reference gap. */
    fun notifyExternalDrop() {
        framesDropped.incrementAndGet()
        decoderFramesDropped.incrementAndGet()
        ControlClient.requestIdr()
        frameAssembler.requestDiscardUntilKeyframe()
    }

    /** Sends one already-serialized controller snapshot without allocating a DatagramPacket. */
    fun sendGamepadPacket(data: ByteArray) {
        if (!running || data.size != GamepadPacket.SIZE) return
        enqueueInput(InputPacketKind.GAMEPAD, data)
    }

    fun sendMousePacket(data: ByteArray) {
        if (!running || data.size != MousePacket.SIZE) return
        enqueueInput(InputPacketKind.MOUSE, data)
    }

    private fun enqueueInput(kind: InputPacketKind, data: ByteArray) {
        // Both producers reuse their serialization buffer, so the queued snapshot must own
        // its bytes. The queue is bounded and drops stale input rather than adding latency.
        inputSendQueue.trySend(OutboundInputPacket(kind, data.copyOf()))
    }

    private suspend fun inputSendLoop() {
        for (outbound in inputSendQueue) {
            if (!running) break
            synchronized(inputSendGate) {
                val sock = socket ?: return@synchronized
                val packet = when (outbound.kind) {
                    InputPacketKind.GAMEPAD -> gamepadDatagram
                    InputPacketKind.MOUSE -> mouseDatagram
                } ?: return@synchronized
                try {
                    packet.setData(outbound.data, 0, outbound.data.size)
                    sock.send(packet)
                } catch (e: IOException) {
                    if (running) Log.w(TAG, "input packet send failed", e)
                }
            }
        }
    }

    fun recordDecodedFrame(frameId: Long, decodeUs: Long, presentUs: Long, latencyMs: Int) {
        framesDecoded.incrementAndGet()
        if (latencyMs >= 0) decoderLatency.add(latencyMs)
        frameTrace.recordDecode(frameId, decodeUs)
        frameTrace.recordPresent(frameId, presentUs)
    }

    private fun receiveLoop(sock: DatagramSocket, serverAddress: InetAddress) {
        try {
            Process.setThreadPriority(Process.THREAD_PRIORITY_FOREGROUND)
        } catch (_: Exception) { }
        val buf = ByteArray(RECV_PACKET_BUFFER_BYTES)
        val packet = DatagramPacket(buf, buf.size)
        val mediaHeader = MediaPacketHeader()
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

            if (packet.address != serverAddress || packet.port != mediaPort) {
                continue
            }

            if (!receivedAny) {
                receivedAny = true // any datagram on this socket means the punch landed
            }

            val length = packet.length
            val receivedAtUs = SystemClock.elapsedRealtimeNanos() / 1000L
            val receivedAt = receivedAtUs / 1000L
            lastPacketAt = receivedAt
            bytesReceived.addAndGet(length.toLong())

            val cursor = CursorPacket.parse(buf, length)
            if (cursor != null) {
                onCursorPosition(cursor)
                frameAssembler.onTick(receivedAt)
                continue
            }

            val trace = FrameTracePacket.parse(buf, length)
            if (trace != null) {
                frameTrace.recordServer(
                    trace.copy(frameId = frameAssembler.extendWireFrameId(trace.frameId))
                )
                frameAssembler.onTick(receivedAt)
                continue
            }

            if (!mediaHeader.parseFrom(buf, length)) {
                frameAssembler.onTick(receivedAt)
                continue
            }
            if (mediaHeader.version != 1) continue
            lastValidVideoPacketAt = receivedAt
            videoPacketsReceived.incrementAndGet()
            if (mediaHeader.fec) fecPacketsReceived.incrementAndGet()
            // Completion, not mere packet arrival, advances the recovery watchdog. This catches
            // a lost startup IDR even while unusable P-frame packets continue arriving.
            frameAssembler.onPacket(
                mediaHeader,
                buf,
                MediaPacketHeader.HEADER_SIZE,
                receivedAt,
                receivedAtUs
            )
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
            val assembled = framesAssembled.getAndSet(0)
            val ok = framesDecoded.getAndSet(0)
            val dropped = framesDropped.getAndSet(0)
            val assemblyDropped = assemblyFramesDropped.getAndSet(0)
            val decoderDropped = decoderFramesDropped.getAndSet(0)
            val packets = videoPacketsReceived.getAndSet(0)
            val fecPackets = fecPacketsReceived.getAndSet(0)
            val fecRecovered = frameAssembler.consumeFecRecoveredPackets()
            val bytes = bytesReceived.getAndSet(0)
            val captureP95 = captureLatency.drainP95()
            val decoderP95 = decoderLatency.drainP95()
            val encoderP95 = encoderLatency.drainP95()
            ControlClient.sendStats(
                framesOk = ok,
                framesAssembled = assembled,
                framesDropped = dropped,
                assemblyFramesDropped = assemblyDropped,
                decoderFramesDropped = decoderDropped,
                fecPacketsRecovered = fecRecovered,
                videoPacketsReceived = packets,
                fecPacketsReceived = fecPackets,
                bytes = bytes,
                intervalMs = intervalMs,
                serverPipelineP95Ms = encoderP95,
                captureToReceiveP95Ms = captureP95,
                decodeToSurfaceP95Ms = decoderP95
            )

            val total = ok + dropped
            val lossPercent = if (total > 0) dropped * 100f / total else 0f
            val kbps = if (intervalMs > 0) (bytes * 8 / intervalMs).toInt() else 0
            onStats(StreamStats(
                fps = normalizeRate(ok, intervalMs),
                assembledFps = normalizeRate(assembled, intervalMs),
                kbps = kbps,
                framesDropped = normalizeRate(dropped, intervalMs),
                lossPercent = lossPercent,
                serverPipelineP95Ms = encoderP95,
                captureToReceiveP95Ms = captureP95,
                decodeToSurfaceP95Ms = decoderP95
            ))
        }
    }

    private suspend fun watchdogLoop(sock: DatagramSocket, serverAddress: InetAddress) {
        var stallReported = false
        var lastVideoRecoveryAt = 0L
        while (currentCoroutineContext().isActive) {
            delay(500)
            val now = SystemClock.elapsedRealtime()
            val completedFrameSilenceMs = now - lastCompletedFrameAt
            val validVideoPacketSilenceMs = now - lastValidVideoPacketAt
            // Before the first complete IDR, retry recovery even if only heartbeats arrive.
            // After a frame has completed, heartbeat-only silence is a healthy static desktop;
            // request an IDR only when video datagrams continue arriving without a complete AU.
            if (MediaRecoveryPolicy.shouldRequestIdr(
                    hasCompletedFrame,
                    completedFrameSilenceMs,
                    validVideoPacketSilenceMs,
                    VIDEO_REPUNCH_MS
                ) &&
                now - lastVideoRecoveryAt >= VIDEO_RECOVERY_INTERVAL_MS
            ) {
                sendHolePunch(sock, serverAddress)
                ControlClient.requestIdr()
                Log.w(
                    TAG,
                    "video recovery IDR: completedAge=${completedFrameSilenceMs}ms " +
                        "videoPacketAge=${validVideoPacketSilenceMs}ms firstFrame=$hasCompletedFrame"
                )
                lastVideoRecoveryAt = now
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

    private fun normalizeRate(count: Int, intervalMs: Long): Int =
        if (intervalMs > 0) ((count * 1000L + intervalMs / 2) / intervalMs).toInt() else 0

    companion object {
        private const val TAG = "MediaReceiver"
        private const val HOLE_PUNCH_MESSAGE = "DSMH"
        private const val INPUT_SEND_QUEUE_CAPACITY = 16
        private const val RECV_PACKET_BUFFER_BYTES = 1500
        // Pacing lets the receiver drain a large IDR while it arrives, so 256 KiB is sufficient
        // without allowing the kernel to hide roughly 200 ms of stale 720p media.
        private const val RECV_SOCKET_BUFFER_BYTES = 256 * 1024
        private const val SOCKET_TIMEOUT_MS = 1000
        private const val VIDEO_REPUNCH_MS = 1500L
        private const val VIDEO_RECOVERY_INTERVAL_MS = 5000L
        private const val VIDEO_STALL_MS = 4000L
    }
}

/** Pure watchdog decision kept separate so static-desktop and broken-assembly behavior remain
 * regression-testable without sockets, clocks, or Android framework objects. */
internal object MediaRecoveryPolicy {
    fun shouldRequestIdr(
        hasCompletedFrame: Boolean,
        completedFrameSilenceMs: Long,
        validVideoPacketSilenceMs: Long,
        recoveryThresholdMs: Long
    ): Boolean = completedFrameSilenceMs >= recoveryThresholdMs &&
        (!hasCompletedFrame || validVideoPacketSilenceMs < recoveryThresholdMs)
}

private class LatencyWindow {
    private val values = ArrayDeque<Int>()
    private val lock = Any()

    fun add(value: Int) = synchronized(lock) {
        if (values.size >= 256) values.removeFirst()
        values.addLast(value)
    }

    fun drainP95(): Int = synchronized(lock) {
        if (values.isEmpty()) return@synchronized -1
        val sorted = values.sorted()
        val index = ((sorted.size - 1) * 0.95).toInt()
        val result = sorted[index]
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
private const val REORDER_GRACE_MS = 20L
/** Sanity ceiling on packetCount to refuse to allocate absurd buffers for a corrupt header. */
private const val MAX_REASONABLE_PACKET_COUNT = 4096
/** Number of interleaved FEC groups. Must match the server's FecInterleave constant. */
private const val FEC_INTERLEAVE = 4

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
internal class FrameAssembler(
    private val bufferPool: BufferPool,
    private val nowUs: () -> Long = { System.nanoTime() / 1000L },
    private val onFrameComplete: (
        buf: ByteArray,
        length: Int,
        keyframe: Boolean,
        frameId: Long,
        ptsMs: Long,
        pipelineDelayMs: Int,
        timing: FrameAssemblyTiming
    ) -> Unit,
    private val onFrameDropped: (requestIdr: Boolean, droppedFrames: Int) -> Unit
) {
    private val fecRecoveredPackets = AtomicInteger(0)
    private val inFlight = LinkedHashMap<Long, InFlightFrame>()
    private val ready = TreeMap<Long, InFlightFrame>()
    private var dropWatermark = -1L
    private var nextFrameId = -1L
    private var latestExtendedFrameId = -1L
    // A new stream has no valid H.264 reference state until its first IDR completes.
    private var discardingUntilKeyframe = true
    private val externalDiscardRequested = AtomicBoolean(false)

    fun requestDiscardUntilKeyframe() {
        // Decoder callbacks run on a different thread. The receive thread owns all maps and
        // applies the reset before processing the next media packet/tick.
        externalDiscardRequested.set(true)
    }

    fun consumeFecRecoveredPackets(): Int = fecRecoveredPackets.getAndSet(0)

    /** Correlates a uint32 timing sidecar with the same extended ID space as assembled frames. */
    fun extendWireFrameId(rawId: Long): Long = extendFrameId(rawId)

    fun reset() {
        clearBufferedFrames()
        dropWatermark = -1L
        nextFrameId = -1L
        latestExtendedFrameId = -1L
        discardingUntilKeyframe = true
        externalDiscardRequested.set(false)
    }

    fun onTick(nowMs: Long) {
        applyExternalDiscardIfNeeded()
        drainReady(nowMs)
    }

    fun onPacket(
        header: MediaPacketHeader,
        buf: ByteArray,
        payloadOffset: Int,
        nowMs: Long,
        receivedAtUs: Long = nowMs * 1000L
    ) {
        applyExternalDiscardIfNeeded()
        if (header.packetCount <= 0 || header.packetCount > MAX_REASONABLE_PACKET_COUNT) return
        // fecCount is attacker/corruption-controlled independent of packetCount; bound it too
        // so a bogus header can't force a huge array allocation.
        if (header.fecCount < 0 || header.fecCount > MAX_REASONABLE_PACKET_COUNT) return
        // Interleaved FEC: fecCount = min(FEC_INTERLEAVE, packetCount)
        if (header.fecCount != minOf(FEC_INTERLEAVE, header.packetCount)) return
        if (header.fec && header.packetIndex >= header.fecCount) return
        if (!header.fec && header.packetIndex >= header.packetCount) return
        // Every media datagram's payload is <=1200 bytes per §3; a larger declared payloadLen
        // (corrupt header or non-conformant sender) would overflow into the next packet's slot
        // in the assembly buffer, since slots are fixed at PACKET_PAYLOAD_MAX apart.
        if (header.payloadLen <= 0 || header.payloadLen > PACKET_PAYLOAD_MAX) return
        if (!header.fec && header.packetIndex < header.packetCount - 1 &&
            header.payloadLen != PACKET_PAYLOAD_MAX
        ) return
        if (header.fec) {
            // Interleaved: group g contains packets at g, g+FEC_INTERLEAVE, g+2*FEC_INTERLEAVE, ...
            val g = header.packetIndex
            if (g >= header.packetCount) return
            val memberCount = (header.packetCount - g + FEC_INTERLEAVE - 1) / FEC_INTERLEAVE
            // Parity is as long as the group's largest member. A multi-member group always has
            // at least one non-final 1200-byte packet, even when it also contains the short final
            // packet. Rejecting truncated parity prevents silently fabricating a corrupted AU.
            if (memberCount > 1 && header.payloadLen != PACKET_PAYLOAD_MAX) return
        }
        val frameId = extendFrameId(header.frameId)
        if (frameId <= dropWatermark) return // stale/duplicate/late

        if (discardingUntilKeyframe) {
            // Ignore non-keyframe packets, but do NOT advance dropWatermark for them: a
            // keyframe with a *lower* frameId may still be assembling (UDP reordering), and
            // raising the watermark past it would reject that keyframe's remaining packets
            // and stall recovery. The watermark only advances when a frame completes or is
            // definitively dropped from assembly.
            // P-frames that arrive while an IDR is actively assembling may be held in the tiny
            // reorder window; they become valid if that IDR completes. Without an active IDR,
            // predictive frames are unusable and are ignored allocation-free.
            if (!header.keyframe && inFlight.values.none { it.keyframe }) return
            // A keyframe packet arrived; fall through to normal processing. The
            // discardingUntilKeyframe flag itself is cleared once the frame *completes*
            // (see completeFrame), not on the first packet, in case this keyframe is itself
            // incomplete and needs to be dropped too.
        }

        if (ready.containsKey(frameId)) return // duplicate packet for an already-ready AU

        var frame = inFlight[frameId]
        if (frame == null) {
            // First give a delayed packet its grace-window opportunity, then enforce the hard
            // four-frame state bound. This is a reorder window, not a render/jitter queue.
            drainReady(nowMs)
            if (discardingUntilKeyframe && !header.keyframe &&
                inFlight.values.none { it.keyframe }
            ) return
            if (bufferedFrameCount() >= MAX_INFLIGHT_FRAMES) {
                triggerDrop()
                if (!header.keyframe) return
            }
            val assemblyBuf = bufferPool.acquire(header.packetCount * PACKET_PAYLOAD_MAX)
            frame = InFlightFrame(
                frameId,
                header.packetCount,
                header.fecCount,
                header.keyframe,
                header.ptsMs,
                header.pipelineDelayMs,
                assemblyBuf,
                bufferPool,
                nowMs,
                receivedAtUs,
                onFecRecovered = { fecRecoveredPackets.incrementAndGet() }
            )
            inFlight[frameId] = frame
            if (nextFrameId < 0L || (dropWatermark < 0L && frameId < nextFrameId)) {
                nextFrameId = frameId
            }
        } else if (frame.packetCount != header.packetCount || frame.fecCount != header.fecCount) {
            // Inconsistent header for a frameId we're already assembling -- ignore the packet.
            return
        }

        if (header.fec) {
            frame.onFecPacket(header, buf, payloadOffset)
        } else {
            frame.onDataPacket(header, buf, payloadOffset)
        }
        frame.lastPacketAtUs = receivedAtUs

        if (frame.isComplete) {
            inFlight.remove(frame.frameId)
            frame.completedAtUs = nowUs()
            // Reorder expiry must stay in the caller-supplied receive-loop clock. Tests and
            // alternate clients may inject a different high-resolution completion clock.
            frame.completedAtMs = nowMs
            frame.releaseFecBuffers()
            ready[frame.frameId] = frame
            drainReady(nowMs)
        }
    }

    private fun drainReady(nowMs: Long) {
        while (nextFrameId >= 0L) {
            // onFrameComplete enters VideoDecoder synchronously. If that reports an overflow,
            // consume its cross-thread recovery request before delivering another ready AU.
            if (externalDiscardRequested.get()) applyExternalDiscardIfNeeded()
            val frame = ready.remove(nextFrameId) ?: break
            if (discardingUntilKeyframe && !frame.keyframe) {
                bufferPool.release(frame.assemblyBuf)
                nextFrameId = nextFrameId(nextFrameId)
                continue
            }

            if (discardingUntilKeyframe) {
                discardingUntilKeyframe = false
            }
            dropWatermark = maxOf(dropWatermark, frame.frameId)
            nextFrameId = nextFrameId(frame.frameId)
            onFrameComplete(
                frame.assemblyBuf,
                frame.totalLength,
                frame.keyframe,
                frame.frameId,
                frame.ptsMs,
                frame.pipelineDelayMs,
                FrameAssemblyTiming(
                    frame.firstPacketAtUs,
                    frame.lastPacketAtUs,
                    frame.completedAtUs
                )
            )
        }

        if (ready.isEmpty() || nextFrameId < 0L) return

        // A later AU may complete before the expected AU because UDP datagrams/FEC can reorder.
        // Wait one frame period before declaring a reference gap. This fixes false loss under
        // large game-frame bursts while adding at most 20 ms and never buffering for rendering.
        // The grace begins only when a *newer completed* AU proves there is a gap. Starting at
        // the expected AU's first packet would consume almost the full allowance during a normal
        // 16.7 ms frame interval and still false-drop at 60 fps.
        val waitStartedAt = ready.firstEntry()?.value?.completedAtMs ?: return
        if (nowMs - waitStartedAt >= REORDER_GRACE_MS ||
            bufferedFrameCount() >= MAX_INFLIGHT_FRAMES
        ) {
            recoverGap()
        }
    }

    private fun recoverGap() {
        // If a later complete IDR is already buffered, it is the clean reference boundary we
        // need. Keep it (and any subsequent frames), count the skipped gap, and avoid asking the
        // server for a redundant IDR that would feed the bitrate-down loop.
        val readyKeyframeId = ready.entries.firstOrNull { it.value.keyframe }?.key
        if (readyKeyframeId != null) {
            val droppedFrames = discardBeforeReadyKeyframe(readyKeyframeId)
            onFrameDropped(false, maxOf(1, droppedFrames))
            drainReady(ready[readyKeyframeId]?.completedAtMs ?: 0L)
            return
        }
        triggerDrop()
    }

    private fun triggerDrop() {
        val droppedFrames = maxOf(1, bufferedFrameCount())
        val highestBufferedId = sequenceOf(inFlight.keys.maxOrNull(), ready.keys.maxOrNull())
            .filterNotNull()
            .maxOrNull()
        if (highestBufferedId != null) dropWatermark = maxOf(dropWatermark, highestBufferedId)
        clearBufferedFrames()
        nextFrameId = -1L
        discardingUntilKeyframe = true
        // A partial recovery/startup IDR needs a replacement too. ControlClient rate-limits
        // duplicate requests, while suppressing this callback could leave a black screen until
        // the slower completed-frame watchdog fires.
        onFrameDropped(true, droppedFrames)
    }

    private fun applyExternalDiscardIfNeeded() {
        if (!externalDiscardRequested.getAndSet(false)) return
        val readyKeyframeId = ready.entries.firstOrNull { it.value.keyframe }?.key
        if (readyKeyframeId != null) {
            discardBeforeReadyKeyframe(readyKeyframeId)
            return
        }
        val highestBufferedId = sequenceOf(inFlight.keys.maxOrNull(), ready.keys.maxOrNull())
            .filterNotNull()
            .maxOrNull()
        if (highestBufferedId != null) dropWatermark = maxOf(dropWatermark, highestBufferedId)
        clearBufferedFrames()
        nextFrameId = -1L
        discardingUntilKeyframe = true
    }

    private fun discardBeforeReadyKeyframe(keyframeId: Long): Int {
        var droppedFrames = 0
        val inFlightIterator = inFlight.entries.iterator()
        while (inFlightIterator.hasNext()) {
            val entry = inFlightIterator.next()
            if (entry.key < keyframeId) {
                releaseFrame(entry.value)
                inFlightIterator.remove()
                droppedFrames++
            }
        }
        val readyIterator = ready.entries.iterator()
        while (readyIterator.hasNext()) {
            val entry = readyIterator.next()
            if (entry.key < keyframeId) {
                releaseFrame(entry.value)
                readyIterator.remove()
                droppedFrames++
            }
        }
        dropWatermark = maxOf(dropWatermark, keyframeId - 1L)
        nextFrameId = keyframeId
        discardingUntilKeyframe = true
        return droppedFrames
    }

    private fun clearBufferedFrames() {
        for (frame in inFlight.values) releaseFrame(frame)
        for (frame in ready.values) releaseFrame(frame)
        inFlight.clear()
        ready.clear()
    }

    private fun releaseFrame(frame: InFlightFrame) {
        bufferPool.release(frame.assemblyBuf)
        frame.releaseFecBuffers()
    }

    private fun bufferedFrameCount(): Int = inFlight.size + ready.size

    private fun nextFrameId(id: Long): Long = id + 1L

    /** Extends uint32 wire IDs into a monotonic long so a multi-day session survives wrap. */
    private fun extendFrameId(rawId: Long): Long {
        if (latestExtendedFrameId < 0L) {
            latestExtendedFrameId = rawId
            return rawId
        }
        val epoch = latestExtendedFrameId and -0x1_0000_0000L
        var candidate = epoch or rawId
        if (candidate - latestExtendedFrameId > 0x8000_0000L) {
            candidate -= 0x1_0000_0000L
        } else if (latestExtendedFrameId - candidate > 0x8000_0000L) {
            candidate += 0x1_0000_0000L
        }
        if (candidate > latestExtendedFrameId) latestExtendedFrameId = candidate
        return candidate
    }
}

internal data class FrameAssemblyTiming(
    val firstReceiveUs: Long,
    val lastReceiveUs: Long,
    val completedUs: Long
)

/** Assembly state for a single frameId. */
private class InFlightFrame(
    val frameId: Long,
    val packetCount: Int,
    val fecCount: Int,
    val keyframe: Boolean,
    val ptsMs: Long,
    val pipelineDelayMs: Int,
    val assemblyBuf: ByteArray,
    private val pool: BufferPool,
    val firstPacketAtMs: Long,
    val firstPacketAtUs: Long,
    private val onFecRecovered: () -> Unit
) {
    private val dataPresent = BooleanArray(packetCount)
    private var dataPresentCount = 0
    var lastPacketLen = -1
        private set
    var completedAtMs = 0L
    var completedAtUs = 0L
    var lastPacketAtUs = firstPacketAtUs

    val totalLength: Int
        get() = (packetCount - 1) * PACKET_PAYLOAD_MAX + lastPacketLen

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

    /** Interleaved group mapping: group g contains packets g, g+FEC_INTERLEAVE, g+2*FEC_INTERLEAVE, ... */
    private fun groupOf(dataIndex: Int) = dataIndex % FEC_INTERLEAVE

    /** XOR recovery per §3.2: if exactly one data packet in group [g] is missing and its
     *  parity packet is present, reconstruct it. With interleaved groups, a burst of up to
     *  FEC_INTERLEAVE consecutive packets hits different groups → all recoverable. */
    private fun tryRecoverGroup(g: Int) {
        if (g < 0 || g >= fecCount) return
        val parityLen = fecLen[g]
        if (parityLen < 0) return // no parity for this group yet

        // Interleaved: group g contains packets at g, g+FEC_INTERLEAVE, g+2*FEC_INTERLEAVE, ...
        var missingIdx = -1
        var missingCount = 0
        var i = g
        while (i < packetCount) {
            if (!dataPresent[i]) {
                missingCount++
                missingIdx = i
            }
            i += FEC_INTERLEAVE
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
            var j = g
            while (j < packetCount) {
                if (j != missingIdx && dataPresent[j]) {
                    val jLen = if (j == packetCount - 1) lastPacketLen else PACKET_PAYLOAD_MAX
                    val jOffset = j * PACKET_PAYLOAD_MAX
                    if (k < jLen) v = (v.toInt() xor assemblyBuf[jOffset + k].toInt()).toByte()
                }
                j += FEC_INTERLEAVE
            }
            assemblyBuf[destOffset + k] = v
        }

        dataPresent[missingIdx] = true
        dataPresentCount++
        onFecRecovered()
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
