package com.deskstream.client.proto

data class ServerFrameTrace(
    val frameId: Long,
    val captureStartUs: Long,
    val captureEndUs: Long,
    val convertEndUs: Long,
    val encodeSubmitUs: Long,
    val encodeFinishUs: Long,
    val packetStartUs: Long,
    val packetEndUs: Long
)

object FrameTracePacket {
    const val SIZE = 68

    fun parse(bytes: ByteArray, length: Int): ServerFrameTrace? {
        if (length != SIZE || bytes.size < SIZE ||
            bytes[0] != 'D'.code.toByte() || bytes[1] != 'S'.code.toByte() ||
            bytes[2] != 'T'.code.toByte() || bytes[3] != 'R'.code.toByte() ||
            bytes[4].toInt() != 1 || bytes[5].toInt() != 0 ||
            bytes[6].toInt() != 0 || bytes[7].toInt() != 0
        ) return null

        return ServerFrameTrace(
            frameId = u32(bytes, 8),
            captureStartUs = i64(bytes, 12),
            captureEndUs = i64(bytes, 20),
            convertEndUs = i64(bytes, 28),
            encodeSubmitUs = i64(bytes, 36),
            encodeFinishUs = i64(bytes, 44),
            packetStartUs = i64(bytes, 52),
            packetEndUs = i64(bytes, 60)
        )
    }

    private fun u32(bytes: ByteArray, offset: Int): Long =
        ((bytes[offset].toLong() and 0xff) shl 24) or
            ((bytes[offset + 1].toLong() and 0xff) shl 16) or
            ((bytes[offset + 2].toLong() and 0xff) shl 8) or
            (bytes[offset + 3].toLong() and 0xff)

    private fun i64(bytes: ByteArray, offset: Int): Long {
        var value = 0L
        for (i in 0 until 8) value = (value shl 8) or (bytes[offset + i].toLong() and 0xff)
        return value
    }
}
