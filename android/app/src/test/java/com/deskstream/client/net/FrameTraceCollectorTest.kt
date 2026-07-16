package com.deskstream.client.net

import com.deskstream.client.proto.ServerFrameTrace
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class FrameTraceCollectorTest {
    @Test
    fun correlatesOutOfOrderStages_andCalculatesLeadingAndTailTransit() {
        val completed = mutableListOf<CompletedFrameTrace>()
        val collector = FrameTraceCollector(
            clockOffsetUs = { 100L },
            sink = { completed += it }
        )

        collector.recordReceive(frameId = 4, firstTimestampUs = 950, lastTimestampUs = 2_050)
        collector.recordAssemble(frameId = 4, timestampUs = 2_075)
        collector.recordDecode(frameId = 4, timestampUs = 2_500)
        collector.recordPresent(frameId = 4, timestampUs = 2_650)
        assertTrue(completed.isEmpty())

        collector.recordServer(trace(frameId = 4))

        val value = completed.single()
        assertEquals(50L, value.firstTransitUs)
        assertEquals(150L, value.tailTransitUs)
        assertEquals(2_650L, value.totalUs)
        assertEquals(950L, value.firstReceiveUs)
        assertEquals(2_050L, value.lastReceiveUs)
    }

    @Test
    fun missingClockSync_preservesLocalDurations_withoutInventingTransit() {
        val completed = mutableListOf<CompletedFrameTrace>()
        val collector = FrameTraceCollector(clockOffsetUs = { null }, sink = { completed += it })

        collector.recordServer(trace(frameId = 8))
        collector.recordReceive(8, 10_000, 10_300)
        collector.recordAssemble(8, 10_350)
        collector.recordDecode(8, 11_000)
        collector.recordPresent(8, 11_200)

        val value = completed.single()
        assertEquals(null, value.firstTransitUs)
        assertEquals(null, value.tailTransitUs)
        assertEquals(null, value.totalUs)
        assertTrue(value.format().contains("firstTransit:-1"))
    }

    @Test
    fun boundedMap_evictsIncompleteOldestTrace() {
        val completed = mutableListOf<CompletedFrameTrace>()
        val collector = FrameTraceCollector(
            clockOffsetUs = { 0L },
            sink = { completed += it },
            maxEntries = 1
        )

        collector.recordReceive(1, 100, 110)
        collector.recordServer(trace(frameId = 2)) // evicts incomplete frame 1
        collector.recordServer(trace(frameId = 1))
        collector.recordAssemble(1, 120)
        collector.recordDecode(1, 130)
        collector.recordPresent(1, 140)

        assertTrue(completed.isEmpty()) // frame 1's receive timestamps were not retained
    }

    private fun trace(frameId: Long) = ServerFrameTrace(
        frameId = frameId,
        captureStartUs = 100,
        captureEndUs = 200,
        convertEndUs = 300,
        encodeSubmitUs = 400,
        encodeFinishUs = 500,
        packetStartUs = 1_000,
        packetEndUs = 2_000
    )
}
