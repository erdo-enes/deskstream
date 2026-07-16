package com.deskstream.client.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class RollingClockOffsetEstimatorTest {
    @Test
    fun keepsLowestRttOnlyWithinRollingWindow() {
        val estimator = RollingClockOffsetEstimator(windowUs = 100, maxSamples = 8)

        val first = addSymmetric(estimator, clientStartUs = 1_000, oneWayUs = 10, offsetUs = 100)
        assertEquals(100L, first.offsetUs)
        assertEquals(20L, first.rttUs)

        val stillFirst = addSymmetric(
            estimator,
            clientStartUs = 1_050,
            oneWayUs = 20,
            offsetUs = 200
        )
        assertEquals(100L, stillFirst.offsetUs)

        // The first sample is now older than the rolling window. The 30 us RTT sample is better
        // than the still-live 40 us sample, so the estimate advances instead of staying stale.
        val advanced = addSymmetric(
            estimator,
            clientStartUs = 1_150,
            oneWayUs = 15,
            offsetUs = 300
        )
        assertEquals(300L, advanced.offsetUs)
        assertEquals(30L, advanced.rttUs)
    }

    @Test
    fun resetAndMalformedExchange_doNotLeakAnOldEstimate() {
        val estimator = RollingClockOffsetEstimator()
        addSymmetric(estimator, clientStartUs = 10_000, oneWayUs = 10, offsetUs = 500)
        estimator.reset()

        assertNull(estimator.add(t0Us = 20_000, t1Us = 20_100, t2Us = 20_090, t3Us = 20_200))
        val fresh = addSymmetric(
            estimator,
            clientStartUs = 30_000,
            oneWayUs = 20,
            offsetUs = 900
        )
        assertEquals(900L, fresh.offsetUs)
    }

    private fun addSymmetric(
        estimator: RollingClockOffsetEstimator,
        clientStartUs: Long,
        oneWayUs: Long,
        offsetUs: Long,
        serverWorkUs: Long = 5
    ): ClockOffsetEstimate {
        val t0 = clientStartUs
        val t1 = t0 + oneWayUs + offsetUs
        val t2 = t1 + serverWorkUs
        val t3 = t0 + oneWayUs + serverWorkUs + oneWayUs
        return estimator.add(t0, t1, t2, t3)!!
    }
}
