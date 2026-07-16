package com.deskstream.client.video

import org.junit.Assert.assertEquals
import org.junit.Test

class FrameRenderClockTest {
    @Test
    fun convertsMediaCodecNanoTimeIntoElapsedRealtimeClock() {
        val clock = FrameRenderClock(elapsedMinusNanoNs = 7_500_000L)

        assertEquals(17_500L, clock.toElapsedRealtimeUs(10_000_000L))
    }
}
