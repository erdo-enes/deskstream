package com.deskstream.client.input

import android.content.Context
import android.hardware.input.InputManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.os.VibrationEffect
import android.os.Vibrator
import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent
import com.deskstream.client.proto.GamepadPacket
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.roundToInt

data class GamepadInventory(
    val deviceCount: Int,
    /** Includes any slot holes, so the server always provisions every transmitted id. */
    val requiredSlots: Int,
    val names: List<String>
)

/**
 * Tracks up to four Android Bluetooth/USB gamepads and emits newest-state snapshots from
 * one dedicated 120 Hz thread. Input callbacks only replace state; they never queue events.
 */
class GamepadForwarder(
    context: Context,
    private val onInventoryChanged: (GamepadInventory) -> Unit
) : InputManager.InputDeviceListener {

    private val inputManager = context.getSystemService(Context.INPUT_SERVICE) as InputManager
    private val handler = Handler(Looper.getMainLooper())
    private val gate = Any()
    private val controllers = mutableMapOf<Int, ControllerState>()

    @Volatile private var running = false
    private var sender: ((ByteArray) -> Unit)? = null
    private var sendThread: Thread? = null

    fun start(send: (ByteArray) -> Unit) {
        if (running) return
        sender = send
        running = true
        inputManager.registerInputDeviceListener(this, handler)
        rescanDevices()
        sendThread = Thread(::sendLoop, "GamepadForwarder").apply {
            isDaemon = true
            start()
        }
    }

    fun stop() {
        if (!running) return
        running = false
        try { inputManager.unregisterInputDeviceListener(this) } catch (_: Exception) { }
        sendThread?.interrupt()
        try { sendThread?.join(250) } catch (_: InterruptedException) {
            Thread.currentThread().interrupt()
        }
        sendThread = null

        val devices = synchronized(gate) {
            val result = controllers.keys.mapNotNull(inputManager::getInputDevice)
            controllers.clear()
            result
        }
        devices.forEach { vibratorFor(it)?.cancel() }
        sender = null
    }

    fun handleKeyEvent(event: KeyEvent): Boolean {
        if (!isGamepadSource(event.source) ||
            (event.action != KeyEvent.ACTION_DOWN && event.action != KeyEvent.ACTION_UP)
        ) return false

        val mask = buttonMask(event.keyCode)
        val isLeftTrigger = event.keyCode == KeyEvent.KEYCODE_BUTTON_L2
        val isRightTrigger = event.keyCode == KeyEvent.KEYCODE_BUTTON_R2
        if (mask == 0 && !isLeftTrigger && !isRightTrigger) return false

        val state = ensureDevice(event.device ?: return false) ?: return false
        val pressed = event.action == KeyEvent.ACTION_DOWN
        synchronized(gate) {
            if (mask != 0) {
                state.keyButtons = if (pressed) state.keyButtons or mask else state.keyButtons and mask.inv()
            }
            if (isLeftTrigger) state.leftTriggerKey = pressed
            if (isRightTrigger) state.rightTriggerKey = pressed
            state.dirty = true
        }
        return true
    }

    fun handleMotionEvent(event: MotionEvent): Boolean {
        if (event.action != MotionEvent.ACTION_MOVE || !isGamepadSource(event.source)) return false
        val device = event.device ?: return false
        val state = ensureDevice(device) ?: return false

        val leftX = stick(event, device, MotionEvent.AXIS_X)
        val leftY = -stick(event, device, MotionEvent.AXIS_Y)
        val rightX = stick(event, device, MotionEvent.AXIS_Z, MotionEvent.AXIS_RX)
        val rightY = -stick(event, device, MotionEvent.AXIS_RZ, MotionEvent.AXIS_RY)
        val leftTrigger = trigger(event, device, MotionEvent.AXIS_LTRIGGER, MotionEvent.AXIS_BRAKE)
        val rightTrigger = trigger(event, device, MotionEvent.AXIS_RTRIGGER, MotionEvent.AXIS_GAS)
        val hatX = stick(event, device, MotionEvent.AXIS_HAT_X)
        val hatY = stick(event, device, MotionEvent.AXIS_HAT_Y)
        var hatButtons = 0
        if (hatX < -0.5f) hatButtons = hatButtons or GamepadPacket.DPAD_LEFT
        if (hatX > 0.5f) hatButtons = hatButtons or GamepadPacket.DPAD_RIGHT
        if (hatY < -0.5f) hatButtons = hatButtons or GamepadPacket.DPAD_UP
        if (hatY > 0.5f) hatButtons = hatButtons or GamepadPacket.DPAD_DOWN

        synchronized(gate) {
            state.leftX = axisToShort(leftX)
            state.leftY = axisToShort(leftY)
            state.rightX = axisToShort(rightX)
            state.rightY = axisToShort(rightY)
            state.leftTriggerAxis = triggerToByte(leftTrigger)
            state.rightTriggerAxis = triggerToByte(rightTrigger)
            state.hatButtons = hatButtons
            state.dirty = true
        }
        return true
    }

    /** Forces existing states out immediately after GAMEPAD_STARTED arrives. */
    fun requestSnapshot() {
        synchronized(gate) { controllers.values.forEach { it.dirty = true; it.lastSentAt = 0 } }
        sendThread?.interrupt()
    }

    fun rumble(controllerId: Int, largeMotor: Int, smallMotor: Int) {
        val deviceId = synchronized(gate) {
            controllers.values.firstOrNull { it.slot == controllerId }?.deviceId
        } ?: return
        val device = inputManager.getInputDevice(deviceId) ?: return
        val vibrator = vibratorFor(device) ?: return
        if (!vibrator.hasVibrator()) return

        if (largeMotor <= 0 && smallMotor <= 0) {
            vibrator.cancel()
            return
        }
        val amplitude = max(largeMotor, smallMotor).coerceIn(1, 255)
        vibrator.vibrate(
            VibrationEffect.createWaveform(
                longArrayOf(0, 1000),
                intArrayOf(0, amplitude),
                1
            )
        )
    }

    override fun onInputDeviceAdded(deviceId: Int) {
        inputManager.getInputDevice(deviceId)?.let(::ensureDevice)
    }

    override fun onInputDeviceRemoved(deviceId: Int) {
        val removed = synchronized(gate) { controllers.remove(deviceId) } ?: return
        // Release every control before changing the negotiated controller count.
        removed.clear()
        removed.sequence = (removed.sequence + 1) and UINT32_MASK
        GamepadPacket.write(
            removed.packet, removed.slot, 0, 0, 0, 0, 0, 0, 0, removed.sequence
        )
        sender?.invoke(removed.packet)
        notifyInventoryChanged()
    }

    override fun onInputDeviceChanged(deviceId: Int) {
        val device = inputManager.getInputDevice(deviceId)
        if (device == null || !isGamepad(device)) onInputDeviceRemoved(deviceId)
        else ensureDevice(device)
    }

    private fun rescanDevices() {
        val liveIds = InputDevice.getDeviceIds().toSet()
        val removed = synchronized(gate) { controllers.keys.filter { it !in liveIds } }
        removed.forEach(::onInputDeviceRemoved)
        liveIds.forEach { id -> inputManager.getInputDevice(id)?.let(::ensureDevice) }
        notifyInventoryChanged()
    }

    private fun ensureDevice(device: InputDevice): ControllerState? {
        if (!isGamepad(device)) return null
        var added = false
        val state = synchronized(gate) {
            controllers[device.id] ?: run {
                val usedSlots = controllers.values.mapTo(mutableSetOf()) { it.slot }
                val slot = (0 until MAX_CONTROLLERS).firstOrNull { it !in usedSlots } ?: return@synchronized null
                added = true
                ControllerState(device.id, slot, device.name ?: "Android controller").also {
                    controllers[device.id] = it
                }
            }
        }
        if (added) notifyInventoryChanged()
        return state
    }

    private fun notifyInventoryChanged() {
        val inventory = synchronized(gate) {
            val ordered = controllers.values.sortedBy { it.slot }
            GamepadInventory(
                deviceCount = ordered.size,
                requiredSlots = (ordered.maxOfOrNull { it.slot } ?: -1) + 1,
                names = ordered.map { it.name }
            )
        }
        onInventoryChanged(inventory)
    }

    private fun sendLoop() {
        while (running) {
            val now = SystemClock.elapsedRealtime()
            synchronized(gate) {
                controllers.values.forEach { state ->
                    val changedDue = state.dirty && now - state.lastSentAt >= MIN_SEND_INTERVAL_MS
                    val heartbeatDue = now - state.lastSentAt >= HEARTBEAT_INTERVAL_MS
                    if (changedDue || heartbeatDue) {
                        state.sequence = (state.sequence + 1) and UINT32_MASK
                        GamepadPacket.write(
                            state.packet,
                            state.slot,
                            state.keyButtons or state.hatButtons,
                            if (state.leftTriggerKey) 255 else state.leftTriggerAxis,
                            if (state.rightTriggerKey) 255 else state.rightTriggerAxis,
                            state.leftX,
                            state.leftY,
                            state.rightX,
                            state.rightY,
                            state.sequence
                        )
                        sender?.invoke(state.packet)
                        state.dirty = false
                        state.lastSentAt = now
                    }
                }
            }
            try {
                Thread.sleep(SEND_LOOP_INTERVAL_MS)
            } catch (_: InterruptedException) {
                // Snapshot request or shutdown; reevaluate immediately.
            }
        }
    }

    private fun stick(event: MotionEvent, device: InputDevice, vararg axes: Int): Float {
        axes.forEach { axis ->
            val range = device.getMotionRange(axis) ?: return@forEach
            val scale = max(abs(range.min), abs(range.max)).takeIf { it > 0f } ?: 1f
            val normalized = (event.getAxisValue(axis) / scale).coerceIn(-1f, 1f)
            val flat = (range.flat / scale).coerceAtLeast(0.02f)
            return if (abs(normalized) <= flat) 0f else normalized
        }
        return 0f
    }

    private fun trigger(event: MotionEvent, device: InputDevice, vararg axes: Int): Float {
        axes.forEach { axis ->
            val range = device.getMotionRange(axis) ?: return@forEach
            val span = range.max - range.min
            if (span <= 0f) return@forEach
            return ((event.getAxisValue(axis) - range.min) / span).coerceIn(0f, 1f)
        }
        return 0f
    }

    private fun buttonMask(keyCode: Int): Int = when (keyCode) {
        KeyEvent.KEYCODE_DPAD_UP -> GamepadPacket.DPAD_UP
        KeyEvent.KEYCODE_DPAD_DOWN -> GamepadPacket.DPAD_DOWN
        KeyEvent.KEYCODE_DPAD_LEFT -> GamepadPacket.DPAD_LEFT
        KeyEvent.KEYCODE_DPAD_RIGHT -> GamepadPacket.DPAD_RIGHT
        KeyEvent.KEYCODE_BUTTON_START -> GamepadPacket.START
        KeyEvent.KEYCODE_BUTTON_SELECT -> GamepadPacket.BACK
        KeyEvent.KEYCODE_BUTTON_THUMBL -> GamepadPacket.LEFT_THUMB
        KeyEvent.KEYCODE_BUTTON_THUMBR -> GamepadPacket.RIGHT_THUMB
        KeyEvent.KEYCODE_BUTTON_L1 -> GamepadPacket.LEFT_SHOULDER
        KeyEvent.KEYCODE_BUTTON_R1 -> GamepadPacket.RIGHT_SHOULDER
        KeyEvent.KEYCODE_BUTTON_MODE -> GamepadPacket.GUIDE
        KeyEvent.KEYCODE_BUTTON_A -> GamepadPacket.A
        KeyEvent.KEYCODE_BUTTON_B -> GamepadPacket.B
        KeyEvent.KEYCODE_BUTTON_X -> GamepadPacket.X
        KeyEvent.KEYCODE_BUTTON_Y -> GamepadPacket.Y
        else -> 0
    }

    private fun isGamepad(device: InputDevice): Boolean = isGamepadSource(device.sources)

    private fun isGamepadSource(source: Int): Boolean =
        source and InputDevice.SOURCE_GAMEPAD == InputDevice.SOURCE_GAMEPAD ||
            source and InputDevice.SOURCE_JOYSTICK == InputDevice.SOURCE_JOYSTICK

    private fun axisToShort(value: Float): Int =
        (value.coerceIn(-1f, 1f) * Short.MAX_VALUE).roundToInt()

    private fun triggerToByte(value: Float): Int =
        (value.coerceIn(0f, 1f) * 255f).roundToInt()

    @Suppress("DEPRECATION")
    private fun vibratorFor(device: InputDevice): Vibrator? =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            device.vibratorManager.defaultVibrator
        } else {
            device.vibrator
        }

    private class ControllerState(val deviceId: Int, val slot: Int, val name: String) {
        val packet = ByteArray(GamepadPacket.SIZE)
        var keyButtons = 0
        var hatButtons = 0
        var leftTriggerAxis = 0
        var rightTriggerAxis = 0
        var leftTriggerKey = false
        var rightTriggerKey = false
        var leftX = 0
        var leftY = 0
        var rightX = 0
        var rightY = 0
        var sequence = 0L
        var lastSentAt = 0L
        var dirty = true

        fun clear() {
            keyButtons = 0
            hatButtons = 0
            leftTriggerAxis = 0
            rightTriggerAxis = 0
            leftTriggerKey = false
            rightTriggerKey = false
            leftX = 0
            leftY = 0
            rightX = 0
            rightY = 0
        }
    }

    companion object {
        private const val MAX_CONTROLLERS = 4
        private const val MIN_SEND_INTERVAL_MS = 8L
        private const val SEND_LOOP_INTERVAL_MS = 4L
        private const val HEARTBEAT_INTERVAL_MS = 250L
        private const val UINT32_MASK = 0xFFFFFFFFL
    }
}
