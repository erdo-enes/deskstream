package com.deskstream.client.net

import com.deskstream.client.proto.MediaPacketHeader
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class FrameAssemblerTest {
    @Test
    fun delayedOlderFrameCompletesBeforeGrace_withoutFalseDrop() {
        val completed = mutableListOf<Long>()
        var drops = 0
        val assembler = assembler(completed) { drops++ }

        assembler.accept(
            packet(frameId = 10, packetIndex = 0, packetCount = 2, keyframe = true),
            nowMs = 0
        )
        assembler.accept(packet(frameId = 11, packetIndex = 0, packetCount = 1), nowMs = 25)

        assertTrue(completed.isEmpty())
        assertEquals(0, drops)

        assembler.accept(
            packet(frameId = 10, packetIndex = 1, packetCount = 2, keyframe = true),
            nowMs = 40
        )

        assertEquals(listOf(10L, 11L), completed)
        assertEquals(0, drops)
    }

    @Test
    fun expiredReferenceGap_requestsOneRecovery_andResumesAtKeyframe() {
        val completed = mutableListOf<Long>()
        var drops = 0
        val assembler = assembler(completed) { drops++ }

        assembler.accept(
            packet(frameId = 19, packetIndex = 0, packetCount = 1, keyframe = true),
            nowMs = 0
        )
        assembler.accept(packet(frameId = 20, packetIndex = 0, packetCount = 2), nowMs = 1)
        assembler.accept(packet(frameId = 21, packetIndex = 0, packetCount = 1), nowMs = 2)
        assembler.onTick(23)

        assembler.accept(packet(frameId = 22, packetIndex = 0, packetCount = 1), nowMs = 22)
        assembler.accept(
            packet(frameId = 23, packetIndex = 0, packetCount = 1, keyframe = true),
            nowMs = 23
        )

        assertEquals(1, drops)
        assertEquals(listOf(19L, 23L), completed)
    }

    @Test
    fun externalDecoderRecovery_clearsOldAssembly_withoutSecondDrop() {
        val completed = mutableListOf<Long>()
        var drops = 0
        val assembler = assembler(completed) { drops++ }

        assembler.accept(packet(frameId = 30, packetIndex = 0, packetCount = 2), nowMs = 0)
        assembler.requestDiscardUntilKeyframe()
        assembler.accept(
            packet(frameId = 31, packetIndex = 0, packetCount = 1, keyframe = true),
            nowMs = 1
        )

        assertEquals(0, drops)
        assertEquals(listOf(31L), completed)
    }

    @Test
    fun startupIgnoresPredictiveFrames_untilFirstKeyframe() {
        val completed = mutableListOf<Long>()
        var drops = 0
        val assembler = assembler(completed) { drops++ }

        assembler.accept(packet(frameId = 40, packetIndex = 0, packetCount = 1), nowMs = 0)
        assembler.accept(
            packet(frameId = 41, packetIndex = 0, packetCount = 1, keyframe = true),
            nowMs = 1
        )

        assertEquals(listOf(41L), completed)
        assertEquals(0, drops)
    }

    @Test
    fun partialStartupKeyframe_requestsReplacementWhenWindowFills() {
        val completed = mutableListOf<Long>()
        val recoveryRequests = mutableListOf<Boolean>()
        val assembler = assemblerWithRecovery(completed) { recoveryRequests += it }

        assembler.accept(
            packet(frameId = 80, packetIndex = 0, packetCount = 2, keyframe = true),
            nowMs = 0
        )
        assembler.accept(packet(frameId = 81, packetIndex = 0, packetCount = 1), nowMs = 1)
        assembler.accept(packet(frameId = 82, packetIndex = 0, packetCount = 1), nowMs = 2)
        assembler.accept(packet(frameId = 83, packetIndex = 0, packetCount = 1), nowMs = 3)

        assertTrue(completed.isEmpty())
        assertEquals(listOf(true), recoveryRequests)
    }

    @Test
    fun completedLaterKeyframe_recoversGap_withoutRequestingAnotherIdr() {
        val completed = mutableListOf<Long>()
        val recoveryRequests = mutableListOf<Boolean>()
        val assembler = assemblerWithRecovery(completed) { recoveryRequests += it }

        assembler.accept(
            packet(frameId = 50, packetIndex = 0, packetCount = 1, keyframe = true),
            nowMs = 0
        )
        assembler.accept(packet(frameId = 51, packetIndex = 0, packetCount = 2), nowMs = 1)
        assembler.accept(
            packet(frameId = 52, packetIndex = 0, packetCount = 1, keyframe = true),
            nowMs = 2
        )
        assembler.onTick(23)

        assertEquals(listOf(50L, 52L), completed)
        assertEquals(listOf(false), recoveryRequests)
    }

    @Test
    fun reentrantDecoderRecovery_preservesReadyKeyframeBoundary() {
        val completed = mutableListOf<Long>()
        lateinit var assembler: FrameAssembler
        assembler = FrameAssembler(
            bufferPool = BufferPool(),
            onFrameComplete = { _, _, _, frameId, _, _ ->
                completed += frameId
                if (frameId == 61L) assembler.requestDiscardUntilKeyframe()
            },
            onFrameDropped = { _, _ -> }
        )

        assembler.accept(
            packet(frameId = 60, packetIndex = 0, packetCount = 1, keyframe = true),
            nowMs = 0
        )
        assembler.accept(packet(frameId = 61, packetIndex = 0, packetCount = 2), nowMs = 1)
        assembler.accept(packet(frameId = 62, packetIndex = 0, packetCount = 1), nowMs = 2)
        assembler.accept(
            packet(frameId = 63, packetIndex = 0, packetCount = 1, keyframe = true),
            nowMs = 3
        )
        assembler.accept(packet(frameId = 61, packetIndex = 1, packetCount = 2), nowMs = 4)

        assertEquals(listOf(60L, 61L, 63L), completed)
    }

    @Test
    fun mediaHeaderParser_readsUnsignedBigEndianFields() {
        val encoded = packet(
            frameId = 0xF1020304L,
            packetIndex = 2,
            packetCount = 3,
            keyframe = true
        )

        assertEquals(1, encoded.header.version)
        assertTrue(encoded.header.keyframe)
        assertEquals(0xF1020304L, encoded.header.frameId)
        assertEquals(2, encoded.header.packetIndex)
        assertEquals(3, encoded.header.packetCount)
    }

    @Test
    fun xorFec_recoversOneMissingDataPacket() {
        var completedLength = 0
        var completedData: ByteArray? = null
        val assembler = FrameAssembler(
            bufferPool = BufferPool(),
            onFrameComplete = { buffer, length, _, _, _, _ ->
                completedLength = length
                completedData = buffer.copyOf(length)
            },
            onFrameDropped = { _, _ -> }
        )
        val first = ByteArray(1200) { 0x35 }
        val last = ByteArray(16) { 0x6A }
        val parity = ByteArray(1200) { index ->
            (first[index].toInt() xor if (index < last.size) last[index].toInt() else 0).toByte()
        }

        assembler.accept(
            packet(
                frameId = 70,
                packetIndex = 1,
                packetCount = 2,
                keyframe = true,
                payload = last
            ),
            nowMs = 0
        )
        assembler.accept(
            packet(
                frameId = 70,
                packetIndex = 0,
                packetCount = 2,
                keyframe = true,
                fec = true,
                payload = parity
            ),
            nowMs = 1
        )

        assertEquals(1216, completedLength)
        assertTrue(completedData!!.copyOfRange(0, 1200).all { it == 0x35.toByte() })
        assertTrue(completedData!!.copyOfRange(1200, 1216).all { it == 0x6A.toByte() })
    }

    @Test
    fun frameIds_continueAcrossUint32Wrap() {
        val completed = mutableListOf<Long>()
        val assembler = assembler(completed) { }

        assembler.accept(
            packet(
                frameId = 0xFFFF_FFFEL,
                packetIndex = 0,
                packetCount = 1,
                keyframe = true
            ),
            nowMs = 0
        )
        assembler.accept(
            packet(frameId = 0xFFFF_FFFFL, packetIndex = 0, packetCount = 1),
            nowMs = 1
        )
        assembler.accept(packet(frameId = 0, packetIndex = 0, packetCount = 1), nowMs = 2)

        assertEquals(
            listOf(0xFFFF_FFFEL, 0xFFFF_FFFFL, 0x1_0000_0000L),
            completed
        )
    }

    private fun assembler(
        completed: MutableList<Long>,
        onDrop: () -> Unit
    ): FrameAssembler = assemblerWithRecovery(completed) { onDrop() }

    private fun assemblerWithRecovery(
        completed: MutableList<Long>,
        onDrop: (Boolean) -> Unit
    ): FrameAssembler = FrameAssembler(
        bufferPool = BufferPool(),
        onFrameComplete = { _, _, _, frameId, _, _ ->
            completed += frameId
        },
        onFrameDropped = { requestIdr, _ -> onDrop(requestIdr) }
    )

    private fun FrameAssembler.accept(value: EncodedPacket, nowMs: Long) {
        onPacket(value.header, value.bytes, MediaPacketHeader.HEADER_SIZE, nowMs)
    }

    private data class EncodedPacket(val header: MediaPacketHeader, val bytes: ByteArray)

    private fun packet(
        frameId: Long,
        packetIndex: Int,
        packetCount: Int,
        keyframe: Boolean = false,
        fec: Boolean = false,
        payload: ByteArray? = null
    ): EncodedPacket {
        val payloadLength = payload?.size ?: if (fec || packetIndex < packetCount - 1) 1200 else 16
        val bytes = ByteArray(MediaPacketHeader.HEADER_SIZE + payloadLength)
        bytes[0] = 1
        bytes[1] = ((if (keyframe) 1 else 0) or (if (fec) 2 else 0)).toByte()
        putU16(bytes, 2, payloadLength)
        putU32(bytes, 4, frameId)
        putU16(bytes, 8, packetIndex)
        putU16(bytes, 10, packetCount)
        putU16(bytes, 12, (packetCount + 7) / 8)
        putU32(bytes, 14, frameId * 16)
        putU16(bytes, 18, 2)
        if (payload != null) {
            payload.copyInto(bytes, MediaPacketHeader.HEADER_SIZE)
        } else {
            for (i in MediaPacketHeader.HEADER_SIZE until bytes.size) {
                bytes[i] = (frameId + packetIndex).toByte()
            }
        }
        val header = MediaPacketHeader()
        assertTrue(header.parseFrom(bytes, bytes.size))
        return EncodedPacket(header, bytes)
    }

    private fun putU16(data: ByteArray, offset: Int, value: Int) {
        data[offset] = (value ushr 8).toByte()
        data[offset + 1] = value.toByte()
    }

    private fun putU32(data: ByteArray, offset: Int, value: Long) {
        data[offset] = (value ushr 24).toByte()
        data[offset + 1] = (value ushr 16).toByte()
        data[offset + 2] = (value ushr 8).toByte()
        data[offset + 3] = value.toByte()
    }
}
