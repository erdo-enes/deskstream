package com.deskstream.client.input

import android.os.SystemClock
import android.view.MotionEvent
import android.view.View
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
        resetGestureOnly()
        onModeChanged(mode)
        return mode
    }

    fun currentMode(): MouseMode = mode

    fun clickLeft() {
        if (enabled) sendClick("left")
    }

    fun clickRight() {
        if (enabled) sendClick("right")
    }

    fun reset() {
        if (leftHeld) sendButton("left", false)
        leftHeld = false
        ControlClient.resetMouse()
        resetGestureOnly()
    }

    override fun onTouch(view: View, event: MotionEvent): Boolean {
        if (!enabled) return false
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                view.requestUnbufferedDispatch(event)
                downAt = SystemClock.elapsedRealtime()
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
                maxPointers = maxOf(maxPointers, event.pointerCount)
                lastY = averageY(event)
            }

            MotionEvent.ACTION_MOVE -> {
                maxPointers = maxOf(maxPointers, event.pointerCount)
                if (event.pointerCount >= 2) {
                    val y = averageY(event)
                    val deltaY = lastY - y
                    twoFingerTravel += abs(deltaY)
                    if (twoFingerTravel >= MOVE_SLOP_PX.toFloat()) moved = true
                    scrollRemainder += deltaY * SCROLL_SCALE
                    val wheel = scrollRemainder.toInt()
                    if (wheel != 0) {
                        send(MousePacket.MODE_RELATIVE, 0, 0, 0, wheel)
                        scrollRemainder -= wheel
                    }
                    lastY = y
                } else {
                    val dx = event.x - lastX
                    val dy = event.y - lastY
                    if (hypot((event.x - downX).toDouble(), (event.y - downY).toDouble()) >= MOVE_SLOP_PX)
                        moved = true

                    if (mode == MouseMode.TOUCHPAD) {
                        if (!leftHeld && moved && SystemClock.elapsedRealtime() - downAt >= HOLD_TO_DRAG_MS) {
                            sendButton("left", true)
                            leftHeld = true
                        }
                        sendRelative(dx, dy)
                    } else {
                        // Direct mode moves the pointer under the finger. Movement alone must
                        // not drag desktop icons; a deliberate hold enables dragging.
                        if (!leftHeld && moved && SystemClock.elapsedRealtime() - downAt >= HOLD_TO_DRAG_MS) {
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
                if (leftHeld) {
                    sendButton("left", false)
                    leftHeld = false
                } else if (!moved && maxPointers >= 2) {
                    sendClick("right")
                } else if (!moved) {
                    if (mode == MouseMode.DIRECT) sendAbsolute(event.x, event.y, view)
                    sendClick("left")
                }
                resetGestureOnly()
            }

            MotionEvent.ACTION_CANCEL -> {
                if (leftHeld) sendButton("left", false)
                leftHeld = false
                ControlClient.resetMouse()
                resetGestureOnly()
            }
        }
        return true
    }

    private fun sendRelative(dx: Float, dy: Float) {
        pendingDx += dx * TOUCHPAD_SENSITIVITY
        pendingDy += dy * TOUCHPAD_SENSITIVITY
        val now = SystemClock.elapsedRealtimeNanos()
        if (now - lastMotionSentAtNanos < MIN_SEND_INTERVAL_NANOS) return
        val x = pendingDx.roundToInt()
        val y = pendingDy.roundToInt()
        pendingDx -= x
        pendingDy -= y
        if (x != 0 || y != 0) send(MousePacket.MODE_RELATIVE, x, y, 0, 0)
    }

    private fun sendAbsolute(x: Float, y: Float, view: View) {
        if (view.width <= 1 || view.height <= 1) return
        val nx = (x / (view.width - 1) * 65535f).roundToInt().coerceIn(0, 65535)
        val ny = (y / (view.height - 1) * 65535f).roundToInt().coerceIn(0, 65535)
        send(MousePacket.MODE_ABSOLUTE, nx, ny, 0, 0)
    }

    private fun send(mode: Int, x: Int, y: Int, horizontalWheel: Int, verticalWheel: Int) {
        val now = SystemClock.elapsedRealtimeNanos()
        if ((x != 0 || y != 0) && now - lastMotionSentAtNanos < MIN_SEND_INTERVAL_NANOS) return
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

    private fun resetGestureOnly() {
        moved = false
        maxPointers = 1
        scrollRemainder = 0f
        pendingDx = 0f
        pendingDy = 0f
        twoFingerTravel = 0f
    }

    private fun averageY(event: MotionEvent): Float {
        var total = 0f
        for (i in 0 until event.pointerCount) total += event.getY(i)
        return total / event.pointerCount
    }

    companion object {
        private const val TOUCHPAD_SENSITIVITY = 1.35f
        private const val SCROLL_SCALE = 4.0f
        private const val MOVE_SLOP_PX = 12.0
        private const val HOLD_TO_DRAG_MS = 350L
        private const val MIN_SEND_INTERVAL_NANOS = 1_000_000_000L / 120L
    }
}
