package com.deskstream.client.proto

import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * 20-byte media packet header, per docs/PROTOCOL.md §3:
 *
 * ```
 * offset  size  field
 * 0       1     version        = 1
 * 1       1     flags          bit0 KEYFRAME, bit1 FEC
 * 2       2     uint16 payloadLen
 * 4       4     uint32 frameId
 * 8       2     uint16 packetIndex
 * 10      2     uint16 packetCount
 * 12      2     uint16 fecCount
 * 14      4     uint32 ptsMs
 * 18      2     uint16 pipelineDelayMs
 * 20      ...   payload
 * ```
 *
 * All multi-byte integers are big-endian.
 */
data class MediaPacketHeader(
    val version: Int,
    val keyframe: Boolean,
    val fec: Boolean,
    val payloadLen: Int,
    val frameId: Long,
    val packetIndex: Int,
    val packetCount: Int,
    val fecCount: Int,
    val ptsMs: Long,
    val pipelineDelayMs: Int
) {
    companion object {
        const val HEADER_SIZE = 20
        private const val FLAG_KEYFRAME = 0x01
        private const val FLAG_FEC = 0x02

        /**
         * Parses the header from [buf] (which must have at least [HEADER_SIZE] valid bytes
         * starting at index 0, e.g. a DatagramPacket buffer). Returns null if [length] is
         * too short to contain a full header or the declared payloadLen overruns [length].
         */
        fun parse(buf: ByteArray, length: Int): MediaPacketHeader? {
            if (length < HEADER_SIZE) return null

            val bb = ByteBuffer.wrap(buf, 0, HEADER_SIZE).order(ByteOrder.BIG_ENDIAN)
            val version = bb.get().toInt() and 0xFF
            val flags = bb.get().toInt() and 0xFF
            val payloadLen = bb.short.toInt() and 0xFFFF
            val frameId = bb.int.toLong() and 0xFFFFFFFFL
            val packetIndex = bb.short.toInt() and 0xFFFF
            val packetCount = bb.short.toInt() and 0xFFFF
            val fecCount = bb.short.toInt() and 0xFFFF
            val ptsMs = bb.int.toLong() and 0xFFFFFFFFL
            val pipelineDelayMs = bb.short.toInt() and 0xFFFF

            if (HEADER_SIZE + payloadLen > length) return null

            return MediaPacketHeader(
                version = version,
                keyframe = (flags and FLAG_KEYFRAME) != 0,
                fec = (flags and FLAG_FEC) != 0,
                payloadLen = payloadLen,
                frameId = frameId,
                packetIndex = packetIndex,
                packetCount = packetCount,
                fecCount = fecCount,
                ptsMs = ptsMs,
                pipelineDelayMs = pipelineDelayMs
            )
        }
    }
}
