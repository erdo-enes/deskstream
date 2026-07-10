package com.deskstream.client.input

import android.os.SystemClock
import android.view.MotionEvent
import android.view.View
import android.view.ViewConfiguration
import com.deskstream.client.net.ControlClient
import com.deskstream.client.proto.MousePacket
import kotlin.math.abs
import kotlin.math.hypot
import kotlin.math.roundToInt

enum class MouseMode { TOUCHPAD, DIRECT }

/** Converts SurfaceView touch gestures into low-latency remote mouse input. */
class RemoteMouseController(
    private val target: View,
    private val sendMotion: (ByteArray) -> Unit,
    private val onModeChanged: (MouseMode) -> Unit = {}
) : View.OnTouchListener {
    private val packet = ByteArray(MousePacket.SIZE)
    private var enabled = false
    private var mode = MouseMode.TOUCHPAD
    private var motionSequence = 0L
    private var buttonSequence = 0L
    private var lastX = 0f
    private var lastY = 0f
    private var downX = 0f
    private var downY = 0f
    private var downAt = 0L
    private val displayDensity = target.resources.displayMetrics.density.coerceAtLeast(0.1f)
    private val touchSlopPx = ViewConfiguration.get(target.context).scaledTouchSlop.toFloat()
    private val doubleTapSlopPx = ViewConfiguration.get(target.context).scaledDoubleTapSlop.toFloat()
    private var pendingTapAt = 0L
    private var pendingTapX = 0f
    private var pendingTapY = 0f
    private var doubleTapInProgress = false
    private var lastMotionSentAtNanos = 0L
    private var moved = false
    private var leftHeld = false
    private var maxPointers = 1
    private var scrollRemainder = 0f
    private var pendingDx = 0f
    private var pendingDy = 0f
    private var twoFingerTravel = 0f

    init { target.setOnTouchListener(this) }

    fun setEnabled(value: Boolean) {
        if (enabled == value) return
        enabled = value
        if (!value) reset()
    }

    fun toggleMode(): MouseMode {
        mode = if (mode == MouseMode.TOUCHPAD) MouseMode.DIRECT else MouseMode.TOUCHPAD
        clearPendingTap()
        resetGestureOnly()
        onModeChanged(mode)
        return mode
    }

    fun currentMode(): MouseMode = mode

    fun clickLeft() {
        if (enabled) {
            clearPendingTap()
            sendClick("left")
        }
    }

    fun clickRight() {
        if (enabled) {
            clearPendingTap()
            sendClick("right")
        }
    }

    fun reset() {
        if (leftHeld) sendButton("left", false)
        leftHeld = false
        clearPendingTap()
        ControlClient.resetMouse()
        resetGestureOnly()
    }

    override fun onTouch(view: View, event: MotionEvent): Boolean {
        if (!enabled) return false
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                view.requestUnbufferedDispatch(event)
                downAt = event.eventTime
                doubleTapInProgress = isDoubleTapStart(downAt, event.x, event.y)
                // A second tap owns the pending first tap. An unrelated gesture starts a
                // fresh sequence instead of allowing a later accidental click.
                clearPendingTap()
                downX = event.x
                downY = event.y
                lastX = event.x
                lastY = event.y
                moved = false
                maxPointers = 1
                scrollRemainder = 0f
                twoFingerTravel = 0f
                if (mode == MouseMode.DIRECT) sendAbsolute(event.x, event.y, view)
            }

            MotionEvent.ACTION_POINTER_DOWN -> {
                doubleTapInProgress = false
                clearPendingTap()
                maxPointers = maxOf(maxPointers, event.pointerCount)
                lastY = averageY(event)
            }

            MotionEvent.ACTION_MOVE -> {
                maxPointers = maxOf(maxPointers, event.pointerCount)
                if (event.pointerCount >= 2) {
                    val y = averageY(event)
                    val deltaY = lastY - y
                    twoFingerTravel += abs(deltaY)
                    // MotionEvent coordinates are physical pixels. Convert to dp before
                    // producing wheel units so scrolling feels the same on phones with
                    // different display densities.
                    scrollRemainder += deltaY / displayDensity * SCROLL_UNITS_PER_DP
                    if (twoFingerTravel >= touchSlopPx) {
                        moved = true
                        val wheel = scrollRemainder.toInt()
                        if (wheel != 0) {
                            send(MousePacket.MODE_RELATIVE, 0, 0, 0, wheel)
                            scrollRemainder -= wheel
                        }
                    }
                    lastY = y
                } else {
                    val dx = event.x - lastX
                    val dy = event.y - lastY
                    if (hypot((event.x - downX).toDouble(), (event.y - downY).toDouble()) >= touchSlopPx)
                        moved = true

                    if (mode == MouseMode.TOUCHPAD) {
                        if (!leftHeld && moved && doubleTapInProgress) {
                            sendButton("left", true)
                            leftHeld = true
                        }
                        sendRelative(dx, dy)
                    } else {
                        // Movement alone never presses a button. A double-tap followed by a
                        // move is the deliberate drag gesture in either pointer mode.
                        if (!leftHeld && moved && doubleTapInProgress) {
                            sendButton("left", true)
                            leftHeld = true
                        }
                        sendAbsolute(event.x, event.y, view)
                    }
                    lastX = event.x
                    lastY = event.y
                }
            }

            MotionEvent.ACTION_POINTER_UP -> {
                // Continue smoothly with the remaining finger after a two-finger gesture.
                if (event.pointerCount == 2) {
                    val remaining = if (event.actionIndex == 0) 1 else 0
                    lastX = event.getX(remaining)
                    lastY = event.getY(remaining)
                }
            }

            MotionEvent.ACTION_UP -> {
                val upAt = event.eventTime

                // A gesture may end inside the 120 Hz coalescing window. Flush the final
                // position before clearing the accumulators so short swipes do not stop
                // short and the last part of a drag is not lost.
                if (moved && maxPointers == 1) {
                    if (mode == MouseMode.TOUCHPAD) {
                        sendRelative(event.x - lastX, event.y - lastY, force = true)
                    } else {
                        sendAbsolute(event.x, event.y, view, force = true)
                    }
                    lastX = event.x
                    lastY = event.y
                }

                if (leftHeld) {
                    sendButton("left", false)
                    leftHeld = false
                } else if (!moved && maxPointers >= 2) {
                    clearPendingTap()
                    sendClick("right")
                } else if (!moved && upAt - downAt <= MAX_TAP_DURATION_MS) {
                    if (mode == MouseMode.DIRECT) sendAbsolute(event.x, event.y, view)
                    if (doubleTapInProgress) {
                        // A single tap is intentionally movement-only. This second tap is
                        // the first point at which the surface itself may left-click.
                        sendClick("left")
                    } else {
                        rememberTap(upAt, event.x, event.y)
                    }
                } else {
                    clearPendingTap()
                }
                resetGestureOnly()
            }

            MotionEvent.ACTION_CANCEL -> {
                if (leftHeld) sendButton("left", false)
                leftHeld = false
                clearPendingTap()
                ControlClient.resetMouse()
                resetGestureOnly()
            }
        }
        return true
    }

    private fun sendRelative(dx: Float, dy: Float, force: Boolean = false) {
        val dxDp = dx / displayDensity
        val dyDp = dy / displayDensity
        val distanceDp = hypot(dxDp.toDouble(), dyDp.toDouble()).toFloat()
        val gain = when {
            distanceDp < PRECISE_MOTION_THRESHOLD_DP -> PRECISE_TOUCHPAD_GAIN
            distanceDp < FAST_MOTION_THRESHOLD_DP -> NORMAL_TOUCHPAD_GAIN
            else -> FAST_TOUCHPAD_GAIN
        }
        pendingDx += dxDp * gain
        pendingDy += dyDp * gain
        val now = SystemClock.elapsedRealtimeNanos()
        if (!force && now - lastMotionSentAtNanos < MIN_SEND_INTERVAL_NANOS) return
        val x = pendingDx.roundToInt()
        val y = pendingDy.roundToInt()
        pendingDx -= x
        pendingDy -= y
        if (x != 0 || y != 0) {
            send(MousePacket.MODE_RELATIVE, x, y, 0, 0, force = force)
        }
    }

    private fun sendAbsolute(x: Float, y: Float, view: View, force: Boolean = false) {
        if (view.width <= 1 || view.height <= 1) return
        val nx = (x / (view.width - 1) * 65535f).roundToInt().coerceIn(0, 65535)
        val ny = (y / (view.height - 1) * 65535f).roundToInt().coerceIn(0, 65535)
        send(MousePacket.MODE_ABSOLUTE, nx, ny, 0, 0, force = force)
    }

    private fun send(
        mode: Int,
        x: Int,
        y: Int,
        horizontalWheel: Int,
        verticalWheel: Int,
        force: Boolean = false
    ) {
        val now = SystemClock.elapsedRealtimeNanos()
        if (!force && (x != 0 || y != 0) &&
            now - lastMotionSentAtNanos < MIN_SEND_INTERVAL_NANOS
        ) return
        lastMotionSentAtNanos = now
        MousePacket.write(
            packet,
            motionSequence++,
            mode,
            x,
            y,
            horizontalWheel,
            verticalWheel
        )
        sendMotion(packet)
    }

    private fun sendButton(button: String, down: Boolean) {
        ControlClient.sendMouseButton(buttonSequence++, button, down)
    }

    private fun sendClick(button: String) {
        ControlClient.sendMouseClick(buttonSequence, button)
        buttonSequence += 2
    }

    private fun isDoubleTapStart(now: Long, x: Float, y: Float): Boolean {
        if (pendingTapAt == 0L || now - pendingTapAt > DOUBLE_TAP_TIMEOUT_MS) return false
        return hypot((x - pendingTapX).toDouble(), (y - pendingTapY).toDouble()) <= doubleTapSlopPx
    }

    private fun rememberTap(now: Long, x: Float, y: Float) {
        pendingTapAt = now
        pendingTapX = x
        pendingTapY = y
    }

    private fun clearPendingTap() {
        pendingTapAt = 0L
        pendingTapX = 0f
        pendingTapY = 0f
    }

    private fun resetGestureOnly() {
        moved = false
        maxPointers = 1
        scrollRemainder = 0f
        pendingDx = 0f
        pendingDy = 0f
        twoFingerTravel = 0f
        doubleTapInProgress = false
    }

    private fun averageY(event: MotionEvent): Float {
        var total = 0f
        for (i in 0 until event.pointerCount) total += event.getY(i)
        return total / event.pointerCount
    }

    companion object {
        private const val PRECISE_MOTION_THRESHOLD_DP = 1.0f
        private const val FAST_MOTION_THRESHOLD_DP = 4.0f
        private const val PRECISE_TOUCHPAD_GAIN = 1.5f
        private const val NORMAL_TOUCHPAD_GAIN = 2.5f
        private const val FAST_TOUCHPAD_GAIN = 3.5f
        private const val SCROLL_UNITS_PER_DP = 6.0f
        private val DOUBLE_TAP_TIMEOUT_MS = ViewConfiguration.getDoubleTapTimeout().toLong()
        private val MAX_TAP_DURATION_MS = ViewConfiguration.getLongPressTimeout().toLong()
        private const val MIN_SEND_INTERVAL_NANOS = 1_000_000_000L / 120L
    }
}
