package com.deskstream.client.net

import java.util.ArrayDeque

/**
 * A tiny size-bucketed byte-array pool so frame assembly and FEC recovery don't allocate on
 * every packet/frame. Buffers are rounded up to [BUCKET_SIZE] so a handful of recurring sizes
 * (roughly steady per bitrate/resolution) get reused instead of fragmenting into many
 * distinct array lengths. Bounded per-bucket depth caps worst-case retained memory.
 *
 * Thread-safe: acquire/release may be called from different threads (the media receive
 * thread produces frames; the MediaCodec callback thread consumes and releases them).
 */
class BufferPool(
    private val bucketSize: Int = BUCKET_SIZE,
    private val maxPerBucket: Int = MAX_PER_BUCKET
) {
    private val buckets = HashMap<Int, ArrayDeque<ByteArray>>()
    private val lock = Any()
    private var retainedBytes = 0

    fun acquire(minSize: Int): ByteArray {
        val size = roundUp(minSize)
        synchronized(lock) {
            val q = buckets[size]
            val buf = q?.pollFirst()
            if (buf != null) {
                retainedBytes -= buf.size
                if (q.isEmpty()) buckets.remove(size)
                return buf
            }
        }
        return ByteArray(size)
    }

    fun release(buf: ByteArray) {
        synchronized(lock) {
            if (buf.size > MAX_POOLED_BUFFER_BYTES ||
                retainedBytes + buf.size > MAX_RETAINED_BYTES ||
                (!buckets.containsKey(buf.size) && buckets.size >= MAX_BUCKETS)
            ) {
                return
            }
            val q = buckets.getOrPut(buf.size) { ArrayDeque() }
            if (q.size < maxPerBucket) {
                q.addLast(buf)
                retainedBytes += buf.size
            }
            // else: let it be garbage collected, we're already at the cap for this bucket
        }
    }

    private fun roundUp(size: Int): Int {
        if (size <= 0) return bucketSize
        return ((size + bucketSize - 1) / bucketSize) * bucketSize
    }

    companion object {
        private const val BUCKET_SIZE = 16 * 1024
        private const val MAX_PER_BUCKET = 4
        private const val MAX_BUCKETS = 64
        private const val MAX_POOLED_BUFFER_BYTES = 2 * 1024 * 1024
        private const val MAX_RETAINED_BYTES = 32 * 1024 * 1024
    }
}
