package com.deskstream.client.net

import android.util.Log
import com.deskstream.client.proto.ServerFrameTrace
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.coroutines.launch

/** A fully correlated per-frame trace. Client timestamps use elapsed-realtime microseconds. */
internal data class CompletedFrameTrace(
    val server: ServerFrameTrace,
    val firstReceiveUs: Long,
    val lastReceiveUs: Long,
    val assembleUs: Long,
    val decodeUs: Long,
    val presentUs: Long,
    /** Positive when the server clock is ahead of Android's elapsed-realtime clock. */
    val serverClockOffsetUs: Long?
) {
    private fun serverOnClientClock(serverUs: Long): Long? =
        serverClockOffsetUs?.let { serverUs - it }

    /** Transit time of the leading media datagram. */
    val firstTransitUs: Long?
        get() = serverOnClientClock(server.packetStartUs)?.let { firstReceiveUs - it }

    /** Transit delta of the final datagram needed to complete the AU.
     *
     * This may be negative when the AU completes before optional trailing FEC has finished
     * sending. It is intentionally not clamped: that overlap is useful pacing information.
     */
    val tailTransitUs: Long?
        get() = serverOnClientClock(server.packetEndUs)?.let { lastReceiveUs - it }

    val totalUs: Long?
        get() = serverOnClientClock(server.captureStartUs)?.let { presentUs - it }

    fun format(): String = buildString(384) {
        append("[frame-trace] id=").append(server.frameId)
        append(" ts=")
        append(server.captureStartUs).append(',')
        append(server.captureEndUs).append(',')
        append(server.convertEndUs).append(',')
        append(server.encodeSubmitUs).append(',')
        append(server.encodeFinishUs).append(',')
        append(server.packetStartUs).append(',')
        append(server.packetEndUs).append(',')
        append(firstReceiveUs).append(',')
        append(lastReceiveUs).append(',')
        append(assembleUs).append(',')
        append(decodeUs).append(',')
        append(presentUs)
        append(" dur_us=cap:").append(server.captureEndUs - server.captureStartUs)
        append(",convert:").append(server.convertEndUs - server.captureEndUs)
        append(",encode:").append(server.encodeFinishUs - server.encodeSubmitUs)
        append(",packet:").append(server.packetEndUs - server.packetStartUs)
        append(",firstTransit:").append(firstTransitUs ?: -1)
        append(",tailTransit:").append(tailTransitUs ?: -1)
        append(",receiveSpan:").append(lastReceiveUs - firstReceiveUs)
        append(",assemble:").append(assembleUs - lastReceiveUs)
        append(",decode:").append(decodeUs - assembleUs)
        append(",present:").append(presentUs - decodeUs)
        append(",total:").append(totalUs ?: -1)
    }
}

/**
 * Bounded correlation of the optional server timing sidecar with Android's frame lifecycle.
 * All map work is tiny and synchronized; formatting/logging is delegated after the entry has
 * been removed so neither the UDP receive thread nor MediaCodec callback thread performs I/O.
 */
internal class FrameTraceCollector(
    private val clockOffsetUs: () -> Long?,
    private val sink: (CompletedFrameTrace) -> Unit = AsyncFrameTraceLogger::enqueue,
    private val maxEntries: Int = MAX_ENTRIES
) {
    private data class Entry(
        var server: ServerFrameTrace? = null,
        var firstReceiveUs: Long? = null,
        var lastReceiveUs: Long? = null,
        var assembleUs: Long? = null,
        var decodeUs: Long? = null,
        var presentUs: Long? = null
    )

    private val entries = LinkedHashMap<Long, Entry>()

    fun recordServer(trace: ServerFrameTrace) = update(trace.frameId) {
        it.server = trace
    }

    fun recordReceive(frameId: Long, firstTimestampUs: Long, lastTimestampUs: Long) =
        update(frameId) { entry ->
            entry.firstReceiveUs = entry.firstReceiveUs?.let { minOf(it, firstTimestampUs) }
                ?: firstTimestampUs
            entry.lastReceiveUs = entry.lastReceiveUs?.let { maxOf(it, lastTimestampUs) }
                ?: lastTimestampUs
        }

    fun recordAssemble(frameId: Long, timestampUs: Long) = update(frameId) {
        it.assembleUs = timestampUs
    }

    fun recordDecode(frameId: Long, timestampUs: Long) = update(frameId) {
        it.decodeUs = timestampUs
    }

    fun recordPresent(frameId: Long, timestampUs: Long) = update(frameId) {
        it.presentUs = timestampUs
    }

    @Synchronized fun clear() = entries.clear()

    private fun update(frameId: Long, mutate: (Entry) -> Unit) {
        val complete = synchronized(this) {
            val entry = entryLocked(frameId)
            mutate(entry)
            takeCompleteLocked(frameId, entry)
        }
        if (complete != null) sink(complete)
    }

    private fun entryLocked(frameId: Long): Entry {
        entries[frameId]?.let { return it }
        while (entries.size >= maxEntries.coerceAtLeast(1)) {
            entries.remove(entries.keys.first())
        }
        return Entry().also { entries[frameId] = it }
    }

    private fun takeCompleteLocked(frameId: Long, entry: Entry): CompletedFrameTrace? {
        val complete = CompletedFrameTrace(
            server = entry.server ?: return null,
            firstReceiveUs = entry.firstReceiveUs ?: return null,
            lastReceiveUs = entry.lastReceiveUs ?: return null,
            assembleUs = entry.assembleUs ?: return null,
            decodeUs = entry.decodeUs ?: return null,
            presentUs = entry.presentUs ?: return null,
            serverClockOffsetUs = clockOffsetUs()
        )
        entries.remove(frameId)
        return complete
    }

    companion object {
        private const val MAX_ENTRIES = 256
    }
}

/** Process-lifetime, bounded logger. It batches lines so tracing cannot stall codec callbacks. */
private object AsyncFrameTraceLogger {
    private const val TAG = "DeskStreamFrameTrace"
    private const val QUEUE_CAPACITY = 256
    private const val MAX_BATCH = 16
    private const val BATCH_WINDOW_MS = 100L

    private val queue = Channel<CompletedFrameTrace>(
        capacity = QUEUE_CAPACITY,
        onBufferOverflow = BufferOverflow.DROP_OLDEST
    )
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    init {
        scope.launch {
            while (true) {
                val first = queue.receiveCatching().getOrNull() ?: return@launch
                val batch = ArrayList<CompletedFrameTrace>(MAX_BATCH)
                batch += first
                while (batch.size < MAX_BATCH) {
                    val next = withTimeoutOrNull(BATCH_WINDOW_MS) {
                        queue.receiveCatching().getOrNull()
                    } ?: break
                    batch += next
                }
                Log.i(TAG, batch.joinToString(separator = "\n") { it.format() })
            }
        }
    }

    fun enqueue(trace: CompletedFrameTrace) {
        queue.trySend(trace)
    }
}
