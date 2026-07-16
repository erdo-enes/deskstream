package com.deskstream.client.net

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class MediaRecoveryPolicyTest {
    private val thresholdMs = 1_500L

    @Test
    fun startupWithoutCompleteIdrRequestsRecovery() {
        assertTrue(MediaRecoveryPolicy.shouldRequestIdr(
            hasCompletedFrame = false,
            completedFrameSilenceMs = 1_500,
            validVideoPacketSilenceMs = 1_500,
            recoveryThresholdMs = thresholdMs
        ))
    }

    @Test
    fun staticDesktopAfterFirstFrameDoesNotRequestIdr() {
        assertFalse(MediaRecoveryPolicy.shouldRequestIdr(
            hasCompletedFrame = true,
            completedFrameSilenceMs = 30_000,
            validVideoPacketSilenceMs = 30_000,
            recoveryThresholdMs = thresholdMs
        ))
    }

    @Test
    fun incomingVideoWithoutCompleteFrameRequestsRecovery() {
        assertTrue(MediaRecoveryPolicy.shouldRequestIdr(
            hasCompletedFrame = true,
            completedFrameSilenceMs = 2_000,
            validVideoPacketSilenceMs = 100,
            recoveryThresholdMs = thresholdMs
        ))
    }

    @Test
    fun recentCompleteFrameDoesNotRequestRecovery() {
        assertFalse(MediaRecoveryPolicy.shouldRequestIdr(
            hasCompletedFrame = true,
            completedFrameSilenceMs = 1_499,
            validVideoPacketSilenceMs = 10,
            recoveryThresholdMs = thresholdMs
        ))
    }
}
