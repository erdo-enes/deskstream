package com.deskstream.client.proto

import java.nio.ByteBuffer
import java.nio.ByteOrder

object MousePacket {
    const val SIZE = 28
    const val MODE_RELATIVE = 0
    const val MODE_ABSOLUTE = 1

    fun write(
        target: ByteArray,
        sequence: Long,
        mode: Int,
        x: Int,
        y: Int,
        horizontalWheel: Int = 0,
        verticalWheel: Int = 0
    ) {
        require(target.size == SIZE)
        ByteBuffer.wrap(target).order(ByteOrder.BIG_ENDIAN).apply {
            put('D'.code.toByte())
            put('S'.code.toByte())
            put('M'.code.toByte())
            put('I'.code.toByte())
            put(1)
            put(mode.toByte())
            putShort(0)
            putInt(sequence.toInt())
            putInt(x)
            putInt(y)
            putInt(horizontalWheel)
            putInt(verticalWheel)
        }
    }
}
