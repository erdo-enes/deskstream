package com.deskstream.client.ui

import android.content.Context
import android.content.res.ColorStateList
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Bundle
import android.util.Log
import android.view.Gravity
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.SurfaceHolder
import android.view.Surface
import android.view.View
import android.view.WindowManager
import android.view.accessibility.AccessibilityManager
import android.widget.FrameLayout
import android.widget.Toast
import androidx.activity.OnBackPressedCallback
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.repeatOnLifecycle
import com.deskstream.client.R
import com.deskstream.client.audio.AudioPlaybackState
import com.deskstream.client.audio.AudioReceiver
import com.deskstream.client.audio.AudioStats
import com.deskstream.client.data.Prefs
import com.deskstream.client.databinding.ActivityStreamBinding
import com.deskstream.client.input.GamepadForwarder
import com.deskstream.client.input.GamepadInventory
import com.deskstream.client.input.MouseMode
import com.deskstream.client.input.RemoteMouseController
import com.deskstream.client.net.ControlClient
import com.deskstream.client.net.MediaReceiver
import com.deskstream.client.net.StreamStats
import com.deskstream.client.proto.ServerMessage
import com.deskstream.client.proto.CursorPosition
import com.deskstream.client.video.VideoDecoder
import com.google.android.material.snackbar.Snackbar
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * Fullscreen video playback. Owns the media (UDP) socket and decoder for the lifetime of the
 * SurfaceView's surface; the control channel itself belongs to the process-wide
 * [ControlClient] and survives well beyond this Activity, per docs/PROTOCOL.md §5.
 */
class StreamActivity : AppCompatActivity(), SurfaceHolder.Callback {

    private lateinit var binding: ActivityStreamBinding

    // Read from the MediaCodec callback thread (via the onDropOrError/onBufferRelease lambdas
    // handed to VideoDecoder) while written from the main thread -- @Volatile for visibility.
    @Volatile private var videoDecoder: VideoDecoder? = null
    @Volatile private var mediaReceiver: MediaReceiver? = null
    @Volatile private var audioReceiver: AudioReceiver? = null
    @Volatile private var gamepadForwardingEnabled = false
    /** Set from the decoder's own callback thread the first time a frame is actually rendered
     * to the surface; screenshots must not fire PixelCopy before this, since a never-drawn
     * surface reads back as solid black. */
    @Volatile private var frameRendered = false

    private lateinit var gamepadForwarder: GamepadForwarder
    private lateinit var remoteMouse: RemoteMouseController
    private lateinit var prefs: Prefs
    private var wifiLock: WifiManager.WifiLock? = null
    /** Preferred stream quality ("native" or "720p"), persisted via [Prefs.streamQuality] and
     * sent as START_STREAM.quality. Servers before v0.5.0 ignore the field and stream native. */
    private var streamQuality: String = Prefs.QUALITY_720P

    private var surfaceReady = false
    /** True once START_STREAM has been sent for the current READY session; reset whenever we
     * leave READY/STREAMING or the surface goes away, so we don't send it twice. */
    private var streamRequested = false
    private var statsVisible = false
    private var audioMuted = false
    private var audioNegotiationJob: Job? = null
    private var inputNegotiationJob: Job? = null
    private var mouseHintJob: Job? = null
    private var mouseToolbarCollapseJob: Job? = null
    private var stallRecoveryInProgress = false
    private var streamStartFailed = false
    private var audioStatus = "starting"
    private var audioDetail = "Waiting for the server"
    private var lastVideoStats: StreamStats? = null
    private var lastAudioStats: AudioStats? = null
    private var negotiatedBitrateKbps = 0
    /** Last dimensions received in STREAM_STARTED, so letterboxing can be re-applied when the
     * container is resized (fold/unfold, multi-window). */
    private var lastStreamWidth = 0
    private var lastStreamHeight = 0
    private var lastStreamFps = 0
    private var lastStreamCodec = "h264"
    private var lastEncoderBackend = "media-foundation"
    private var mouseStatus = "negotiating"
    private var mouseEnabledByUser = true
    /** The persistent mouse affordance is one compact pill. Its full action strip is revealed
     * only on demand and automatically collapses after a short idle period. */
    private var mouseToolbarExpanded = false
    private var gamepadInventory = GamepadInventory(0, 0, emptyList())
    private var gamepadStatus = "none detected"
    private var gamepadDetail = "Connect a Bluetooth or USB controller to Android"
    private var exitSnackbar: Snackbar? = null
    /** Snackbars are outside the XML overlay hierarchy, so keep track of them explicitly. Clean
     * screen dismisses existing bars and suppresses new ones until controls are restored. */
    private val activeSnackbars = mutableSetOf<Snackbar>()
    /** "Clean screen" mode hides the single overlay layer, leaving only the video surface.
     * Child status and cursor callbacks may continue updating safely because a visible child
     * cannot escape its hidden parent. */
    private var controlsHidden = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        binding = ActivityStreamBinding.inflate(layoutInflater)
        setContentView(binding.root)
        controlsHidden = savedInstanceState?.getBoolean(STATE_CONTROLS_HIDDEN) == true
        prefs = Prefs(applicationContext)
        streamQuality = prefs.streamQuality
        updateQualityButton()
        gamepadForwarder = GamepadForwarder(applicationContext, ::onGamepadInventoryChanged)
        remoteMouse = RemoteMouseController(
            binding.surfaceView,
            sendMotion = { packet -> mediaReceiver?.sendMousePacket(packet) },
            onModeChanged = {
                updatePointerButton(it)
                showMouseGestureHint()
            },
            shouldHandleCleanScreenGesture = { controlsHidden },
            onCleanScreenReveal = { showControls() }
        )
        createWifiLock()
        applyImmersiveMode()

        binding.surfaceView.holder.addCallback(this)
        binding.streamRoot.setOnClickListener {
            toggleDiagnostics()
        }
        binding.tvStreamStatus.setOnClickListener { toggleDiagnostics() }
        binding.btnAudio.setOnClickListener {
            audioMuted = !audioMuted
            audioReceiver?.setMuted(audioMuted)
            updateAudioButton()
            renderDiagnostics()
        }
        binding.btnPointerMode.setOnClickListener {
            remoteMouse.toggleMode()
            scheduleMouseToolbarCollapse()
        }
        binding.btnMouseToggle.setOnClickListener {
            mouseEnabledByUser = !mouseEnabledByUser
            applyMouseControls()
            if (mouseEnabledByUser) showMouseGestureHint()
            scheduleMouseToolbarCollapse()
        }
        binding.btnMouseClick.setOnClickListener {
            remoteMouse.clickLeft()
            scheduleMouseToolbarCollapse()
        }
        binding.btnMouseRight.setOnClickListener {
            remoteMouse.clickRight()
            scheduleMouseToolbarCollapse()
        }
        binding.btnMouseMenu.setOnClickListener {
            if (mouseToolbarExpanded) collapseMouseToolbar() else expandMouseToolbar()
        }
        binding.btnStats.setOnClickListener { toggleDiagnostics() }
        binding.btnQuality.setOnClickListener {
            val next = if (streamQuality == Prefs.QUALITY_720P) Prefs.QUALITY_NATIVE else Prefs.QUALITY_720P
            setStreamQuality(next)
        }
        binding.btnScreenshot.setOnClickListener { takeScreenshot() }
        binding.btnHideControls.setOnClickListener { hideControls() }
        binding.btnLeave.setOnClickListener { confirmLeave() }
        binding.streamRoot.addOnLayoutChangeListener { _, l, t, r, b, oldL, oldT, oldR, oldB ->
            if ((r - l) != (oldR - oldL) || (b - t) != (oldB - oldT)) {
                applyAspectRatio(lastStreamWidth, lastStreamHeight)
            }
        }

        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                if (controlsHidden) {
                    showControls()
                    return
                }
                // Android's edge-back gesture can be triggered while using the touchpad.
                // Leaving therefore requires an explicit action, never another gesture.
                confirmLeave()
            }
        })

        applyControlsVisibility()
        observeControlClient()
    }

    override fun onSaveInstanceState(outState: Bundle) {
        outState.putBoolean(STATE_CONTROLS_HIDDEN, controlsHidden)
        super.onSaveInstanceState(outState)
    }

    override fun onStart() {
        super.onStart()
        acquireWifiLock()
        gamepadForwarder.start { packet ->
            if (gamepadForwardingEnabled) mediaReceiver?.sendGamepadPacket(packet)
        }
        maybeStartStream()
    }

    override fun onStop() {
        super.onStop()
        // Per §5: backgrounding stops the stream but keeps the control socket alive.
        ControlClient.stopStream()
        ControlClient.stopInput()
        remoteMouse.setEnabled(false)
        releaseWifiLock()
        streamRequested = false
        gamepadForwardingEnabled = false
        gamepadForwarder.stop()
        stopReceivers()
    }

    override fun onDestroy() {
        dismissActiveSnackbars()
        hideMouseGestureHint()
        mouseToolbarCollapseJob?.cancel()
        mouseToolbarCollapseJob = null
        super.onDestroy()
        gamepadForwarder.stop()
        stopReceivers()
        videoDecoder?.release()
        videoDecoder = null
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) applyImmersiveMode()
    }

    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        // F11 is a hardware-keyboard fallback for clean screen. Consume both key edges so a
        // connected keyboard/gamepad can never receive a mismatched down/up pair.
        if (event.keyCode == KeyEvent.KEYCODE_F11) {
            if (event.action == KeyEvent.ACTION_UP && event.repeatCount == 0) {
                if (controlsHidden) showControls() else hideControls()
            }
            return true
        }
        return if (gamepadForwarder.handleKeyEvent(event)) true else super.dispatchKeyEvent(event)
    }

    override fun dispatchGenericMotionEvent(event: MotionEvent): Boolean {
        return if (gamepadForwarder.handleMotionEvent(event)) true else super.dispatchGenericMotionEvent(event)
    }

    private fun applyImmersiveMode() {
        WindowCompat.setDecorFitsSystemWindows(window, false)
        val controller = WindowInsetsControllerCompat(window, binding.root)
        controller.hide(WindowInsetsCompat.Type.systemBars())
        controller.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
    }

    // ---- SurfaceHolder.Callback ---------------------------------------------------------

    override fun surfaceCreated(holder: SurfaceHolder) {
        surfaceReady = true
        frameRendered = false
        videoDecoder?.release()
        videoDecoder = null
        maybeStartStream()
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
        // No-op: video dimensions come from STREAM_STARTED, not the surface's pixel size.
    }

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        surfaceReady = false
        streamRequested = false
        if (ControlClient.state.value == ControlClient.State.STREAMING) {
            ControlClient.stopStream()
        }
        stopReceivers()
        videoDecoder?.release()
        videoDecoder = null
    }

    // ---- Control channel plumbing --------------------------------------------------------

    private fun observeControlClient() {
        lifecycleScope.launch {
            repeatOnLifecycle(Lifecycle.State.STARTED) {
                launch { ControlClient.state.collect { state -> handleState(state) } }
                launch { ControlClient.events.collect { msg -> handleEvent(msg) } }
            }
        }
    }

    private fun handleState(state: ControlClient.State) {
        updateStatusChip()
        when (state) {
            ControlClient.State.READY -> {
                // Either a fresh session, or we just silently reconnected while this Activity
                // was in the foreground -- either way, (re)start the stream.
                if (!streamStartFailed) {
                    streamRequested = false
                    maybeStartStream()
                }
            }
            ControlClient.State.CONNECTING -> {
                showCenterStatus("Connecting to ${ControlClient.serverIp}…")
            }
            ControlClient.State.RECONNECTING -> {
                streamStartFailed = false
                streamRequested = false
                stopReceivers()
                showCenterStatus("Connection lost\nReconnecting to ${ControlClient.serverIp}…")
            }
            ControlClient.State.DISCONNECTED -> {
                streamStartFailed = false
                streamRequested = false
                stopReceivers()
                showCenterStatus("Disconnected\nCheck that the PC server is still running")
            }
            ControlClient.State.PAIRING -> {
                // The server no longer recognizes our token (it was re-paired or reset) and a
                // reconnect landed in PAIRING. The PIN dialog lives on MainActivity -- go back
                // there instead of dead-ending on a black screen.
                finish()
            }
            else -> {}
        }
    }

    private fun handleEvent(msg: ServerMessage) {
        try {
            when (msg) {
                is ServerMessage.StreamStarted -> onStreamStarted(msg)
                is ServerMessage.AudioStarted -> onAudioStarted(msg)
                is ServerMessage.AudioUnavailable -> onAudioUnavailable(msg.message)
                is ServerMessage.GamepadStarted -> onGamepadStarted(msg)
                is ServerMessage.GamepadUnavailable -> onGamepadUnavailable(msg)
                is ServerMessage.GamepadRumble -> gamepadForwarder.rumble(
                    msg.controllerId, msg.largeMotor, msg.smallMotor
                )
                ServerMessage.InputStarted -> onInputStarted()
                is ServerMessage.InputUnavailable -> onInputUnavailable(msg.message)
                ServerMessage.StreamStopped -> {
                    stopReceivers()
                    if (!stallRecoveryInProgress && !streamStartFailed) {
                        showCenterStatus("Stream stopped")
                    }
                }
                is ServerMessage.Bitrate -> {
                    negotiatedBitrateKbps = msg.kbps
                    renderDiagnostics()
                }
                is ServerMessage.Error -> {
                    if (msg.code == "STREAM_FAILED" || msg.code == "ENCODER_UNAVAILABLE") {
                        // Do not immediately spin START_STREAM after a real server-side fault.
                        // Keep the Activity visible with the actual error instead of looking as
                        // though it crashed or disappeared.
                        streamStartFailed = true
                        streamRequested = true
                        stopReceivers()
                    }
                    val description = describeStreamError(msg)
                    showCenterStatus(description)
                    showSnackbar(description, Snackbar.LENGTH_LONG)
                }
                else -> {}
            }
        } catch (e: Exception) {
            // A setup failure must leave the control socket alive and visible to the user.
            // It is also emitted to logcat with the full stack trace for device-specific fixes.
            Log.e(TAG, "stream event setup failed for ${msg::class.java.simpleName}", e)
            streamStartFailed = true
            streamRequested = true
            try { stopReceivers() } catch (_: Exception) { }
            val detail = e.message?.takeIf { it.isNotBlank() } ?: e.javaClass.simpleName
            showCenterStatus("Client setup failed: $detail")
            showSnackbar("Client setup failed; see logcat", Snackbar.LENGTH_LONG)
            ControlClient.stopStream()
        }
    }

    private fun maybeStartStream() {
        if (surfaceReady && !streamRequested && ControlClient.state.value == ControlClient.State.READY) {
            streamRequested = true
            showCenterStatus("Connected to ${ControlClient.serverIp}\nStarting video…")
            val maxBitrateKbps = if (streamQuality == Prefs.QUALITY_720P) {
                MAX_720P_BITRATE_KBPS
            } else {
                MAX_NATIVE_BITRATE_KBPS
            }
            ControlClient.startStream(maxBitrateKbps, TARGET_FPS, streamQuality)
        }
    }

    private fun onStreamStarted(msg: ServerMessage.StreamStarted) {
        stallRecoveryInProgress = false
        streamStartFailed = false
        binding.tvConnecting.visibility = View.GONE
        lastStreamWidth = msg.width
        lastStreamHeight = msg.height
        lastStreamFps = msg.fps
        lastStreamCodec = msg.codec
        lastEncoderBackend = msg.encoderBackend
        negotiatedBitrateKbps = 0
        lastVideoStats = null
        lastAudioStats = null
        audioStatus = "starting"
        audioDetail = "Negotiating with the PC"
        applyAspectRatio(msg.width, msg.height)
        updateStatusChip()

        val holder = binding.surfaceView.holder
        if (holder.surface == null || !holder.surface.isValid) {
            // The server has already entered STREAMING, while maybeStartStream only sends from
            // READY. Explicitly stop this receiver-less epoch so STREAM_STOPPED -> READY and a
            // later surfaceCreated can negotiate a fresh stream instead of staying black.
            streamRequested = false
            ControlClient.stopStream()
            return
        }

        // Always release + recreate on STREAM_STARTED: dimensions may differ, and frameId
        // numbering restarts server-side even if they don't (protocol §5).
        stopReceivers()
        frameRendered = false

        // Bind every callback to this exact stream epoch. A stale codec/receiver callback from
        // a prior restart can then only return its own buffer; it cannot poison the new epoch's
        // telemetry or force the new assembler back into IDR recovery.
        lateinit var receiver: MediaReceiver
        lateinit var decoder: VideoDecoder
        decoder = VideoDecoder(
            onDropOrError = {
                if (mediaReceiver === receiver && videoDecoder === decoder) {
                    receiver.notifyExternalDrop()
                }
            },
            onBufferRelease = { buffer -> receiver.bufferPool.release(buffer) },
            onFrameRendered = { frameId, decodeUs, presentUs, latencyMs ->
                if (mediaReceiver === receiver && videoDecoder === decoder) {
                    frameRendered = true
                    receiver.recordDecodedFrame(frameId, decodeUs, presentUs, latencyMs)
                }
            }
        )
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            try {
                holder.surface.setFrameRate(
                    msg.fps.toFloat(),
                    Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE
                )
            } catch (_: Exception) { }
        }

        var mediaStartupError = "Media socket could not start"
        receiver = MediaReceiver(
            onFrame = { data, length, keyframe, frameId, _ ->
                if (mediaReceiver === receiver && videoDecoder === decoder) {
                    decoder.submitFrame(data, length, keyframe, frameId)
                } else {
                    receiver.bufferPool.release(data)
                }
            },
            onStats = { stats ->
                runOnUiThread {
                    if (mediaReceiver === receiver && videoDecoder === decoder) {
                        updateStatsOverlay(stats)
                    }
                }
            },
            onStalled = {
                runOnUiThread {
                    if (mediaReceiver === receiver && videoDecoder === decoder) {
                        restartStalledStream()
                    }
                }
            },
            onCursorPosition = { position ->
                runOnUiThread {
                    if (mediaReceiver === receiver && videoDecoder === decoder) {
                        updateRemoteCursor(position)
                    }
                }
            },
            onStartupError = { detail -> mediaStartupError = detail }
        )
        mediaReceiver = receiver
        videoDecoder = decoder
        decoder.resetForNewStream(holder.surface, msg.width, msg.height, msg.fps)
        if (!receiver.start(ControlClient.serverIp, msg.mediaPort, msg.clockBaseUs)) {
            mediaReceiver = null
            videoDecoder = null
            decoder.release()
            streamStartFailed = true
            streamRequested = true
            showCenterStatus("Client media setup failed: $mediaStartupError")
            showSnackbar(mediaStartupError, Snackbar.LENGTH_LONG)
            ControlClient.stopStream()
            return
        }

        gamepadForwardingEnabled = false
        negotiateGamepads()
        negotiateMouseInput()

        binding.tvStreamStatus.visibility = View.VISIBLE
        binding.btnAudio.isEnabled = false
        updateAudioButton()
        renderDiagnostics()
        ControlClient.startAudio()
        audioNegotiationJob?.cancel()
        audioNegotiationJob = lifecycleScope.launch {
            delay(AUDIO_NEGOTIATION_TIMEOUT_MS)
            if (audioReceiver == null && audioStatus == "starting") {
                audioStatus = "unavailable"
                audioDetail = "No audio reply (the server may be an older version)"
                updateAudioButton()
                renderDiagnostics()
            }
        }
    }

    private fun negotiateMouseInput() {
        mouseStatus = "negotiating"
        applyMouseControls()
        ControlClient.startMouseInput()
        inputNegotiationJob?.cancel()
        inputNegotiationJob = lifecycleScope.launch {
            delay(INPUT_NEGOTIATION_TIMEOUT_MS)
            if (!binding.btnPointerMode.isEnabled) {
                mouseStatus = "unavailable"
                applyMouseControls()
            }
        }
    }

    private fun onInputStarted() {
        inputNegotiationJob?.cancel()
        inputNegotiationJob = null
        mouseStatus = "live"
        applyMouseControls()
        showMouseGestureHint()
        renderDiagnostics()
    }

    private fun onInputUnavailable(message: String) {
        inputNegotiationJob?.cancel()
        inputNegotiationJob = null
        mouseStatus = "unavailable"
        applyMouseControls()
        showSnackbar("Remote mouse unavailable: $message", Snackbar.LENGTH_LONG)
    }

    private fun updatePointerButton(mode: MouseMode) {
        binding.btnPointerMode.text = when (mouseStatus) {
            "negotiating" -> getString(R.string.mouse_mode_starting)
            "unavailable" -> getString(R.string.mouse_mode_unavailable)
            else -> if (mode == MouseMode.TOUCHPAD) {
                getString(R.string.mouse_mode_touchpad)
            } else {
                getString(R.string.mouse_mode_direct)
            }
        }
        binding.btnPointerMode.contentDescription = when (mouseStatus) {
            "negotiating" -> getString(R.string.mouse_mode_starting_description)
            "unavailable" -> getString(R.string.mouse_unavailable_description)
            else -> if (mode == MouseMode.TOUCHPAD) {
                getString(R.string.mouse_mode_pad_description)
            } else {
                getString(R.string.mouse_mode_direct_description)
            }
        }
    }

    private fun applyMouseControls() {
        val negotiated = mouseStatus == "live"
        val active = negotiated && mouseEnabledByUser
        remoteMouse.setEnabled(active)
        binding.btnMouseToggle.isEnabled = negotiated
        binding.btnMouseToggle.text = if (mouseEnabledByUser) {
            getString(R.string.mouse_disable)
        } else {
            getString(R.string.mouse_enable)
        }
        binding.btnMouseToggle.contentDescription = if (mouseEnabledByUser) {
            getString(R.string.mouse_disable_description)
        } else {
            getString(R.string.mouse_enable_description)
        }
        binding.btnPointerMode.isEnabled = active
        binding.btnMouseClick.isEnabled = active
        binding.btnMouseRight.isEnabled = active
        // Do not flash an unpositioned cursor in the top-left corner. It becomes visible
        // only after feedback arrives for the first motion packet.
        if (!active) binding.tvRemoteCursor.visibility = View.GONE
        if (!active) hideMouseGestureHint()
        updatePointerButton(remoteMouse.currentMode())
    }

    private fun updateRemoteCursor(position: CursorPosition) {
        if (mouseStatus != "live" || !mouseEnabledByUser) return
        val surface = binding.surfaceView
        val cursor = binding.tvRemoteCursor
        if (surface.width <= 0 || surface.height <= 0) return
        // The vector's top-left point is its hotspot, so no size-based centering offset is
        // needed. Use width/height - 1 to mirror the absolute-input normalization exactly.
        cursor.translationX = surface.x + position.x / 65535f * (surface.width - 1)
        cursor.translationY = surface.y + position.y / 65535f * (surface.height - 1)
        cursor.visibility = View.VISIBLE
    }

    private fun showMouseGestureHint() {
        if (mouseStatus != "live" || !mouseEnabledByUser) return
        mouseHintJob?.cancel()
        binding.tvGestureHint.animate().cancel()
        binding.tvGestureHint.alpha = 1f
        binding.tvGestureHint.visibility = View.VISIBLE
        mouseHintJob = lifecycleScope.launch {
            delay(MOUSE_HINT_VISIBLE_MS)
            binding.tvGestureHint.animate()
                .alpha(0f)
                .setDuration(MOUSE_HINT_FADE_MS)
                .withEndAction {
                    binding.tvGestureHint.visibility = View.GONE
                    binding.tvGestureHint.alpha = 1f
                }
                .start()
        }
    }

    private fun hideMouseGestureHint() {
        mouseHintJob?.cancel()
        mouseHintJob = null
        binding.tvGestureHint.animate().cancel()
        binding.tvGestureHint.alpha = 1f
        binding.tvGestureHint.visibility = View.GONE
    }

    private fun expandMouseToolbar() {
        if (controlsHidden || mouseToolbarExpanded) return
        mouseToolbarExpanded = true
        binding.mouseActions.visibility = View.VISIBLE
        binding.btnMouseMenu.contentDescription = getString(R.string.mouse_menu_hide_description)
        binding.mouseActions.announceForAccessibility(getString(R.string.mouse_controls_shown))
        scheduleMouseToolbarCollapse()
    }

    private fun collapseMouseToolbar() {
        mouseToolbarCollapseJob?.cancel()
        mouseToolbarCollapseJob = null
        mouseToolbarExpanded = false
        binding.mouseActions.visibility = View.GONE
        binding.btnMouseMenu.contentDescription = getString(R.string.mouse_menu_show_description)
    }

    private fun scheduleMouseToolbarCollapse() {
        mouseToolbarCollapseJob?.cancel()
        mouseToolbarCollapseJob = null
        if (!mouseToolbarExpanded) return

        // Auto-collapsing while TalkBack is traversing the newly revealed actions can strand
        // accessibility focus. Touch-exploration users close the anchored Mouse button
        // explicitly; everyone else gets the low-obstruction five-second idle behavior.
        val accessibility = getSystemService(Context.ACCESSIBILITY_SERVICE) as? AccessibilityManager
        if (accessibility?.isTouchExplorationEnabled == true) return

        mouseToolbarCollapseJob = lifecycleScope.launch {
            delay(MOUSE_TOOLBAR_EXPANDED_MS)
            collapseMouseToolbar()
        }
    }

    private fun restartStalledStream() {
        restartStream("Video stalled\nRecovering stream…")
    }

    /**
     * Shared stop -> STREAM_STOPPED -> READY -> start restart path. [handleState] restarts the
     * pipeline as soon as the control channel reports READY again (the same reconnect-driven
     * restart used after a background/foreground cycle), so quality changes reuse exactly the
     * mechanism stall recovery already relies on instead of inventing a second state machine.
     */
    private fun restartStream(statusMessage: String) {
        if (stallRecoveryInProgress || !surfaceReady ||
            ControlClient.state.value != ControlClient.State.STREAMING
        ) return
        stallRecoveryInProgress = true
        showCenterStatus(statusMessage)
        remoteMouse.setEnabled(false)
        hideMouseGestureHint()
        streamRequested = false
        // Wait for STREAM_STOPPED/READY before requesting the replacement pipeline. This
        // preserves STOP_STREAM -> START_STREAM ordering on the control connection.
        ControlClient.stopStream()
    }

    /** Persists the chosen quality and, if currently streaming, restarts the stream through
     * [restartStream] so the new value takes effect immediately. If not yet streaming, the
     * next [maybeStartStream] call picks it up naturally. */
    private fun setStreamQuality(newQuality: String) {
        if (newQuality == streamQuality) return
        streamQuality = newQuality
        prefs.streamQuality = newQuality
        updateQualityButton()
        if (ControlClient.state.value == ControlClient.State.STREAMING) {
            restartStream(getString(R.string.quality_switching, qualityLabel(newQuality)))
        }
    }

    private fun qualityLabel(quality: String): String =
        if (quality == Prefs.QUALITY_720P) getString(R.string.quality_720p) else getString(R.string.quality_native)

    private fun updateQualityButton() {
        val label = qualityLabel(streamQuality)
        binding.btnQuality.text = label
        binding.btnQuality.contentDescription = getString(R.string.cd_quality_button, label)
    }

    private fun takeScreenshot() {
        if (ControlClient.state.value != ControlClient.State.STREAMING || !frameRendered) {
            showSnackbar(getString(R.string.screenshot_no_frame), Snackbar.LENGTH_SHORT)
            return
        }
        ScreenshotSaver.capture(
            applicationContext,
            binding.surfaceView,
            lastStreamWidth,
            lastStreamHeight,
            frameRendered
        ) { success, message ->
            runOnUiThread {
                val text = if (success) getString(R.string.screenshot_saved, message) else message
                showSnackbar(text, Snackbar.LENGTH_LONG)
            }
        }
    }

    private fun confirmLeave() {
        if (exitSnackbar?.isShown == true) return
        exitSnackbar = createSnackbar(
            getString(R.string.leave_stream_prompt),
            Snackbar.LENGTH_LONG
        )?.setAction(R.string.leave_stream_action) {
            ControlClient.stopStream()
            finish()
        }
        exitSnackbar?.show()
    }

    /** Hides every non-video view. Three-finger hold, Back, or F11 restores the overlay without
     * sharing a gesture with remote mouse click/drag input. */
    private fun hideControls() {
        if (controlsHidden) return
        dismissActiveSnackbars()
        controlsHidden = true
        applyControlsVisibility()
        Toast.makeText(this, R.string.controls_hidden_hint, Toast.LENGTH_SHORT).show()
    }

    private fun showControls() {
        if (!controlsHidden) return
        controlsHidden = false
        applyControlsVisibility()
    }

    private fun showSnackbar(message: CharSequence, duration: Int) {
        createSnackbar(message, duration)?.show()
    }

    private fun createSnackbar(message: CharSequence, duration: Int): Snackbar? {
        if (controlsHidden) return null
        val snackbar = Snackbar.make(binding.streamRoot, message, duration)
        activeSnackbars += snackbar
        snackbar.addCallback(object : Snackbar.Callback() {
            override fun onDismissed(transientBottomBar: Snackbar?, event: Int) {
                activeSnackbars -= snackbar
                if (exitSnackbar === snackbar) exitSnackbar = null
            }
        })
        return snackbar
    }

    private fun dismissActiveSnackbars() {
        activeSnackbars.toList().forEach { it.dismiss() }
        activeSnackbars.clear()
        exitSnackbar = null
    }

    /** Hiding the parent layer is intentional: asynchronous reconnect, stats, and cursor updates
     * can change child visibility without breaking clean-screen mode. */
    private fun applyControlsVisibility() {
        binding.overlayLayer.visibility = if (controlsHidden) View.GONE else View.VISIBLE
        if (controlsHidden) {
            hideMouseGestureHint()
            collapseMouseToolbar()
        }
        // The activity is already sticky-immersive at all times (see applyImmersiveMode()); a
        // system-bar swipe-reveal while controls are hidden should still auto-hide again, so
        // re-assert it here rather than introducing a second immersive mode.
        applyImmersiveMode()
    }

    private fun updateStatusChip() {
        val state = ControlClient.state.value
        val streaming = state == ControlClient.State.STREAMING && lastStreamWidth > 0 && lastStreamHeight > 0
        val (text, colorRes) = when {
            streaming ->
                getString(R.string.status_streaming, lastStreamWidth, lastStreamHeight, lastStreamFps) to R.color.status_green
            state == ControlClient.State.RECONNECTING -> getString(R.string.status_reconnecting) to R.color.status_red
            state == ControlClient.State.DISCONNECTED -> getString(R.string.status_disconnected) to R.color.status_red
            else -> getString(R.string.status_connecting) to R.color.status_amber
        }
        binding.tvStreamStatus.text = text
        binding.viewStatusDot.backgroundTintList =
            ColorStateList.valueOf(ContextCompat.getColor(this, colorRes))
    }

    private fun updateStatsOverlay(stats: StreamStats) {
        lastVideoStats = stats
        renderDiagnostics()
    }

    private fun toggleDiagnostics() {
        statsVisible = !statsVisible
        binding.tvStats.visibility = if (statsVisible) View.VISIBLE else View.GONE
        hideMouseGestureHint()
        renderDiagnostics()
    }

    private fun onAudioStarted(msg: ServerMessage.AudioStarted) {
        audioNegotiationJob?.cancel()
        audioNegotiationJob = null
        audioReceiver?.stop()
        audioStatus = "starting"
        audioDetail = "Opening Android low-latency audio output"
        lastAudioStats = null

        lateinit var receiver: AudioReceiver
        receiver = AudioReceiver(
            onState = { state, detail ->
                runOnUiThread {
                    if (audioReceiver !== receiver) return@runOnUiThread
                    audioDetail = detail
                    audioStatus = when (state) {
                        AudioPlaybackState.STARTING -> "starting"
                        AudioPlaybackState.READY -> "ready"
                        AudioPlaybackState.PLAYING -> "live"
                        AudioPlaybackState.ERROR -> "unavailable"
                    }
                    if (state == AudioPlaybackState.ERROR) {
                        audioReceiver = null
                    }
                    binding.btnAudio.isEnabled = state == AudioPlaybackState.READY ||
                        state == AudioPlaybackState.PLAYING
                    updateAudioButton()
                    renderDiagnostics()
                }
            },
            onStats = { stats ->
                runOnUiThread {
                    if (audioReceiver !== receiver) return@runOnUiThread
                    lastAudioStats = stats
                    renderDiagnostics()
                }
            }
        )
        audioReceiver = receiver
        receiver.setMuted(audioMuted)
        receiver.start(
            serverIp = ControlClient.serverIp,
            audioPort = msg.audioPort,
            sampleRate = msg.sampleRate,
            channels = msg.channels,
            format = msg.format,
            packetSamples = msg.packetSamples
        )
    }

    private fun onAudioUnavailable(message: String) {
        audioNegotiationJob?.cancel()
        audioNegotiationJob = null
        audioReceiver?.stop()
        audioReceiver = null
        audioStatus = "unavailable"
        audioDetail = message.ifEmpty { "PC system audio is unavailable" }
        binding.btnAudio.isEnabled = false
        updateAudioButton()
        renderDiagnostics()
        showSnackbar("Video is live, but audio is unavailable: $audioDetail", Snackbar.LENGTH_LONG)
    }

    private fun onGamepadInventoryChanged(inventory: GamepadInventory) {
        gamepadInventory = inventory
        if (inventory.deviceCount == 0) {
            gamepadForwardingEnabled = false
            gamepadStatus = "none detected"
            gamepadDetail = "Connect a Bluetooth or USB controller to Android"
            if (ControlClient.state.value == ControlClient.State.STREAMING) {
                ControlClient.stopGamepads()
            }
        } else {
            val names = inventory.names.joinToString()
            gamepadStatus = "detected"
            gamepadDetail = "$names · waiting for PC virtual controller"
            if (ControlClient.state.value == ControlClient.State.STREAMING && lastStreamWidth > 0) {
                gamepadForwardingEnabled = false
                ControlClient.startGamepads(inventory.requiredSlots)
            }
        }
        updateGamepadStatus()
        renderDiagnostics()
    }

    private fun negotiateGamepads() {
        if (gamepadInventory.deviceCount <= 0) {
            gamepadStatus = "none detected"
            gamepadDetail = "Connect a Bluetooth or USB controller to Android"
            updateGamepadStatus()
            return
        }
        gamepadStatus = "connecting"
        gamepadDetail = "Creating Xbox 360 controller on the PC"
        updateGamepadStatus()
        ControlClient.startGamepads(gamepadInventory.requiredSlots)
    }

    private fun onGamepadStarted(message: ServerMessage.GamepadStarted) {
        if (gamepadInventory.deviceCount <= 0) {
            ControlClient.stopGamepads()
            return
        }
        gamepadForwardingEnabled = true
        gamepadForwarder.requestSnapshot()
        gamepadStatus = "live"
        val count = gamepadInventory.deviceCount
        gamepadDetail = "$count Android controller${if (count == 1) "" else "s"} → " +
            "${message.controllers} virtual Xbox 360 controller${if (message.controllers == 1) "" else "s"}"
        updateGamepadStatus()
        renderDiagnostics()
    }

    private fun onGamepadUnavailable(message: ServerMessage.GamepadUnavailable) {
        gamepadForwardingEnabled = false
        gamepadStatus = "PC driver required"
        gamepadDetail = message.message.ifEmpty { "Install ViGEmBus 1.22 on the PC" }
        updateGamepadStatus()
        renderDiagnostics()
        showSnackbar("Controller unavailable: $gamepadDetail", Snackbar.LENGTH_LONG)
    }

    private fun updateGamepadStatus() {
        binding.tvGamepadStatus.text = "Controller: $gamepadStatus"
    }

    private fun stopReceivers() {
        frameRendered = false
        audioNegotiationJob?.cancel()
        audioNegotiationJob = null
        inputNegotiationJob?.cancel()
        inputNegotiationJob = null
        hideMouseGestureHint()
        remoteMouse.setEnabled(false)
        binding.btnMouseToggle.isEnabled = false
        binding.btnPointerMode.isEnabled = false
        binding.btnMouseClick.isEnabled = false
        binding.btnMouseRight.isEnabled = false
        binding.tvRemoteCursor.visibility = View.GONE
        val stoppedReceiver = mediaReceiver
        mediaReceiver = null
        stoppedReceiver?.stop()
        val stoppedDecoder = videoDecoder
        videoDecoder = null
        stoppedDecoder?.release()
        audioReceiver?.stop()
        audioReceiver = null
        gamepadForwardingEnabled = false
        if (gamepadInventory.deviceCount > 0) {
            gamepadStatus = "paused"
            gamepadDetail = "Controller forwarding is paused until the stream reconnects"
            updateGamepadStatus()
        }
        binding.btnAudio.isEnabled = false
    }

    private fun showCenterStatus(message: String) {
        binding.tvConnecting.text = message
        binding.tvConnecting.visibility = View.VISIBLE
        // The connection-state chip (tvStreamStatus/viewStatusDot) is intentionally left alone
        // here: it now reflects Connecting/Streaming/Reconnecting continuously (see
        // updateStatusChip()), independent of this large center-screen message.
    }

    private fun updateAudioButton() {
        binding.btnAudio.text = when (audioStatus) {
            "starting" -> "Audio…"
            "unavailable" -> "No audio"
            else -> if (audioMuted) getString(R.string.toolbar_unmute) else getString(R.string.toolbar_mute)
        }
        binding.btnAudio.contentDescription = if (audioMuted) {
            getString(R.string.cd_unmute_button)
        } else {
            getString(R.string.cd_mute_button)
        }
    }

    private fun renderDiagnostics() {
        if (lastStreamWidth <= 0 || lastStreamHeight <= 0) return
        updateStatusChip()

        val video = lastVideoStats
        val audio = lastAudioStats
        binding.tvStats.text = buildString {
            append("STATE  LIVE · ${ControlClient.serverIp}\n")
            append("VIDEO  ${lastStreamWidth}×${lastStreamHeight} @ $lastStreamFps · ${lastStreamCodec.uppercase()}\n")
            append("ENC    $lastEncoderBackend")
            if (negotiatedBitrateKbps > 0) append(" · $negotiatedBitrateKbps kbps target")
            append('\n')
            if (video != null) {
                append("NET    ${video.kbps} kbps · ${"%.1f".format(video.lossPercent)}% loss\n")
                append(
                    "VIDEO  ${video.fps} fps decoded · ${video.assembledFps} assembled · " +
                        "${video.framesDropped} drops/s\n"
                )
                if (video.serverPipelineP95Ms >= 0 || video.captureToReceiveP95Ms >= 0 ||
                    video.decodeToSurfaceP95Ms >= 0
                ) {
                    append("LAT    ${video.serverPipelineP95Ms} ms server · ")
                    append("${video.captureToReceiveP95Ms} ms capture→receive · ")
                    append("${video.decodeToSurfaceP95Ms} ms decode→surface (p95)\n")
                }
            } else {
                append("NET    waiting for first video stats…\n")
            }
            append("AUDIO  $audioDetail")
            if (audioMuted) append(" · locally muted")
            append('\n')
            if (audio != null) {
                val activity = if (audio.receivingAudio) "receiving" else "source quiet"
                append("A-NET  ${audio.kbps} kbps · ${"%.1f".format(audio.packetLossPercent)}% loss · $activity\n")
                append("A-OUT  ${audio.outputBufferMs} ms buffer · ${audio.outputDrops} drops · ${audio.underruns} underruns")
            } else {
                append("A-NET  waiting for audio packets…")
            }
            append("\nPAD    $gamepadDetail")
            append("\nMOUSE  $mouseStatus · ${remoteMouse.currentMode().name.lowercase()}")
            append("\nQUAL   ${qualityLabel(streamQuality)}")
        }
    }

    private fun createWifiLock() {
        // This is only a latency optimization. Some Android TV and OEM Wi-Fi services reject
        // low-latency locks, so failure must never take down the streaming Activity.
        try {
            val wifi = applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
                ?: return
            val mode = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                WifiManager.WIFI_MODE_FULL_LOW_LATENCY
            } else {
                @Suppress("DEPRECATION")
                WifiManager.WIFI_MODE_FULL_HIGH_PERF
            }
            wifiLock = wifi.createWifiLock(mode, "DeskStream:low-latency").apply {
                setReferenceCounted(false)
            }
        } catch (_: Exception) {
            wifiLock = null
        }
    }

    private fun acquireWifiLock() {
        try { if (wifiLock?.isHeld != true) wifiLock?.acquire() } catch (_: Exception) { }
    }

    private fun releaseWifiLock() {
        try { if (wifiLock?.isHeld == true) wifiLock?.release() } catch (_: Exception) { }
    }

    private fun describeStreamError(msg: ServerMessage.Error): String = when (msg.code) {
        "ENCODER_UNAVAILABLE" -> "No supported hardware H.264 encoder was found on the PC"
        "STREAM_FAILED" -> "The PC could not start streaming: ${msg.message}"
        "CONNECTION_LOST" -> "Connection to the PC was lost"
        else -> msg.message.ifEmpty { "Streaming error (${msg.code})" }
    }

    private fun applyAspectRatio(streamWidth: Int, streamHeight: Int) {
        val container = binding.streamRoot
        if (container.width <= 0 || container.height <= 0) {
            container.post { applyAspectRatio(streamWidth, streamHeight) }
            return
        }
        if (streamWidth <= 0 || streamHeight <= 0) return

        val containerAspect = container.width.toFloat() / container.height.toFloat()
        val streamAspect = streamWidth.toFloat() / streamHeight.toFloat()

        val targetWidth: Int
        val targetHeight: Int
        if (containerAspect > streamAspect) {
            // Container is relatively wider than the stream -> pillarbox (bars on the sides).
            targetHeight = container.height
            targetWidth = (container.height * streamAspect).toInt()
        } else {
            // Container is relatively taller/narrower -> letterbox (bars top/bottom).
            targetWidth = container.width
            targetHeight = (container.width / streamAspect).toInt()
        }

        val lp = binding.surfaceView.layoutParams as FrameLayout.LayoutParams
        if (lp.width == targetWidth && lp.height == targetHeight && lp.gravity == Gravity.CENTER) {
            return // avoid a set-layout -> layout-change-listener -> set-layout loop
        }
        lp.width = targetWidth
        lp.height = targetHeight
        lp.gravity = Gravity.CENTER
        binding.surfaceView.layoutParams = lp
    }

    companion object {
        private const val TAG = "StreamActivity"
        private const val STATE_CONTROLS_HIDDEN = "controls_hidden"
        private const val MAX_NATIVE_BITRATE_KBPS = 20000
        private const val MAX_720P_BITRATE_KBPS = 8000
        private const val TARGET_FPS = 60
        private const val MOUSE_HINT_VISIBLE_MS = 4500L
        private const val MOUSE_HINT_FADE_MS = 250L
        private const val MOUSE_TOOLBAR_EXPANDED_MS = 5000L
        private const val AUDIO_NEGOTIATION_TIMEOUT_MS = 3500L
        private const val INPUT_NEGOTIATION_TIMEOUT_MS = 2500L
    }
}
