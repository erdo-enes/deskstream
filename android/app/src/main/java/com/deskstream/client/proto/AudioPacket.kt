package com.deskstream.client.proto

/**
 * Reusable parser for the fixed audio datagram in docs/PROTOCOL.md §3A. It is mutable so
 * the 200-packet/s receive loop does not allocate a data object for every 5 ms block.
 */
class AudioPacketHeader {
    var version: Int = 0
        private set
    var format: Int = 0
        private set
    var payloadLen: Int = 0
        private set
    var sequence: Long = 0
        private set
    var ptsMs: Long = 0
        private set
    var sampleCount: Int = 0
        private set

    fun parse(buf: ByteArray, length: Int): Boolean {
        if (length < HEADER_SIZE) return false
        version = buf[0].toInt() and 0xFF
        format = buf[1].toInt() and 0xFF
        payloadLen = readU16(buf, 2)
        sequence = readU32(buf, 4)
        ptsMs = readU32(buf, 8)
        sampleCount = readU16(buf, 12)
        return payloadLen >= 0 && HEADER_SIZE + payloadLen <= length
    }

    companion object {
        const val HEADER_SIZE = 16
        const val VERSION = 1
        const val FORMAT_PCM_S16LE = 1
        const val BYTES_PER_SAMPLE = 2

        private fun readU16(buf: ByteArray, offset: Int): Int =
            ((buf[offset].toInt() and 0xFF) shl 8) or
                (buf[offset + 1].toInt() and 0xFF)

        private fun readU32(buf: ByteArray, offset: Int): Long =
            ((buf[offset].toLong() and 0xFF) shl 24) or
                ((buf[offset + 1].toLong() and 0xFF) shl 16) or
                ((buf[offset + 2].toLong() and 0xFF) shl 8) or
                (buf[offset + 3].toLong() and 0xFF)
    }
}
