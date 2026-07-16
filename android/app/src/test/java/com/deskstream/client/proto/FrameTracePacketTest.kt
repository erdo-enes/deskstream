package com.deskstream.client.proto

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder

class FrameTracePacketTest {
    @Test
    fun parsesUnsignedFrameId_andAllBigEndianTimestamps() {
        val bytes = ByteBuffer.allocate(FrameTracePacket.SIZE).order(ByteOrder.BIG_ENDIAN).apply {
            put("DSTR".toByteArray(Charsets.US_ASCII))
            put(1.toByte())
            put(0.toByte())
            putShort(0)
            putInt(0xF1020304.toInt())
            repeat(7) { putLong(10_000L + it) }
        }.array()

        val trace = FrameTracePacket.parse(bytes, bytes.size)!!

        assertEquals(0xF1020304L, trace.frameId)
        assertEquals(10_000L, trace.captureStartUs)
        assertEquals(10_006L, trace.packetEndUs)
    }

    @Test
    fun rejectsWrongVersionAndTruncatedSidecar() {
        val bytes = ByteArray(FrameTracePacket.SIZE)
        "DSTR".toByteArray(Charsets.US_ASCII).copyInto(bytes)
        bytes[4] = 2

        assertNull(FrameTracePacket.parse(bytes, bytes.size))
        assertNull(FrameTracePacket.parse(bytes, bytes.size - 1))
    }
}
