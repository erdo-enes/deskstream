package com.deskstream.client.proto

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
class MediaPacketHeader {
    var version: Int = 0
        private set
    var keyframe: Boolean = false
        private set
    var fec: Boolean = false
        private set
    var payloadLen: Int = 0
        private set
    var frameId: Long = 0
        private set
    var packetIndex: Int = 0
        private set
    var packetCount: Int = 0
        private set
    var fecCount: Int = 0
        private set
    var ptsMs: Long = 0
        private set
    var pipelineDelayMs: Int = 0
        private set

    /**
     * Allocation-free parser for the UDP hot path. One instance is reused by MediaReceiver for
     * every datagram; FrameAssembler copies the scalar fields it needs and never retains this
     * object. Avoiding ByteBuffer + data-class allocation for every 1200-byte payload materially
     * reduces GC pauses at 1080p60 game bitrates.
     */
    fun parseFrom(buf: ByteArray, length: Int): Boolean {
        if (length < HEADER_SIZE) return false

        val parsedPayloadLen = u16(buf, 2)
        if (HEADER_SIZE + parsedPayloadLen > length) return false

        version = buf[0].toInt() and 0xFF
        val flags = buf[1].toInt() and 0xFF
        keyframe = (flags and FLAG_KEYFRAME) != 0
        fec = (flags and FLAG_FEC) != 0
        payloadLen = parsedPayloadLen
        frameId = u32(buf, 4)
        packetIndex = u16(buf, 8)
        packetCount = u16(buf, 10)
        fecCount = u16(buf, 12)
        ptsMs = u32(buf, 14)
        pipelineDelayMs = u16(buf, 18)
        return true
    }

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
            return MediaPacketHeader().takeIf { it.parseFrom(buf, length) }
        }

        private fun u16(data: ByteArray, offset: Int): Int =
            ((data[offset].toInt() and 0xFF) shl 8) or
                (data[offset + 1].toInt() and 0xFF)

        private fun u32(data: ByteArray, offset: Int): Long =
            ((data[offset].toLong() and 0xFF) shl 24) or
                ((data[offset + 1].toLong() and 0xFF) shl 16) or
                ((data[offset + 2].toLong() and 0xFF) shl 8) or
                (data[offset + 3].toLong() and 0xFF)
    }
}
