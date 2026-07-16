package com.deskstream.client.video

import org.junit.Assert.assertEquals
import org.junit.Test

class FrameRenderClockTest {
    @Test
    fun convertsMediaCodecNanoTimeIntoElapsedRealtimeClock() {
        val clock = FrameRenderClock(elapsedMinusNanoNs = 7_500_000L)

        assertEquals(17_500L, clock.toElapsedRealtimeUs(10_000_000L, 17_500L))
    }

    @Test
    fun acceptsVendorTimestampAlreadyInElapsedRealtimeDomain() {
        val clock = FrameRenderClock(elapsedMinusNanoNs = 120_000_000_000L)

        assertEquals(1_000_000L, clock.toElapsedRealtimeUs(1_000_000_000L, 1_000_100L))
    }

    @Test
    fun rejectsImpossibleDecoderLatencyInsteadOfPublishingSentinel() {
        assertEquals(-1, FrameRenderClock.validLatencyMs(1_000_000L, 61_000_000L))
        assertEquals(12, FrameRenderClock.validLatencyMs(1_000_000L, 1_012_999L))
    }
}
