package com.deskstream.client.proto

import java.nio.ByteBuffer
import java.nio.ByteOrder

data class CursorPosition(val sequence: Long, val x: Int, val y: Int)

object CursorPacket {
    const val SIZE = 16

    fun parse(data: ByteArray, length: Int): CursorPosition? {
        if (length != SIZE || data[0] != 'D'.code.toByte() || data[1] != 'S'.code.toByte() ||
            data[2] != 'M'.code.toByte() || data[3] != 'C'.code.toByte() || data[4] != 1.toByte()
        ) return null
        val bb = ByteBuffer.wrap(data, 8, 8).order(ByteOrder.BIG_ENDIAN)
        return CursorPosition(
            sequence = bb.int.toLong() and 0xFFFFFFFFL,
            x = bb.short.toInt() and 0xFFFF,
            y = bb.short.toInt() and 0xFFFF
        )
    }
}
