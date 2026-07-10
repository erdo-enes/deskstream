package com.deskstream.client.proto

/** Fixed-size complete controller snapshot from docs/PROTOCOL.md §3B. */
object GamepadPacket {
    const val SIZE = 24
    const val VERSION = 1

    const val DPAD_UP = 0x0001
    const val DPAD_DOWN = 0x0002
    const val DPAD_LEFT = 0x0004
    const val DPAD_RIGHT = 0x0008
    const val START = 0x0010
    const val BACK = 0x0020
    const val LEFT_THUMB = 0x0040
    const val RIGHT_THUMB = 0x0080
    const val LEFT_SHOULDER = 0x0100
    const val RIGHT_SHOULDER = 0x0200
    const val GUIDE = 0x0400
    const val A = 0x1000
    const val B = 0x2000
    const val X = 0x4000
    const val Y = 0x8000

    fun write(
        dst: ByteArray,
        controllerId: Int,
        buttons: Int,
        leftTrigger: Int,
        rightTrigger: Int,
        leftX: Int,
        leftY: Int,
        rightX: Int,
        rightY: Int,
        sequence: Long
    ) {
        require(dst.size >= SIZE)
        dst[0] = 'D'.code.toByte()
        dst[1] = 'S'.code.toByte()
        dst[2] = 'G'.code.toByte()
        dst[3] = 'P'.code.toByte()
        dst[4] = VERSION.toByte()
        dst[5] = controllerId.coerceIn(0, 3).toByte()
        putU16(dst, 6, buttons)
        dst[8] = leftTrigger.coerceIn(0, 255).toByte()
        dst[9] = rightTrigger.coerceIn(0, 255).toByte()
        putU16(dst, 10, leftX.coerceIn(Short.MIN_VALUE.toInt(), Short.MAX_VALUE.toInt()))
        putU16(dst, 12, leftY.coerceIn(Short.MIN_VALUE.toInt(), Short.MAX_VALUE.toInt()))
        putU16(dst, 14, rightX.coerceIn(Short.MIN_VALUE.toInt(), Short.MAX_VALUE.toInt()))
        putU16(dst, 16, rightY.coerceIn(Short.MIN_VALUE.toInt(), Short.MAX_VALUE.toInt()))
        putU32(dst, 18, sequence)
        dst[22] = 0
        dst[23] = 0
    }

    private fun putU16(dst: ByteArray, offset: Int, value: Int) {
        dst[offset] = (value ushr 8).toByte()
        dst[offset + 1] = value.toByte()
    }

    private fun putU32(dst: ByteArray, offset: Int, value: Long) {
        dst[offset] = (value ushr 24).toByte()
        dst[offset + 1] = (value ushr 16).toByte()
        dst[offset + 2] = (value ushr 8).toByte()
        dst[offset + 3] = value.toByte()
    }
}
