package com.deskstream.client.ui

import android.content.Context
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Bundle
import android.view.Gravity
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.SurfaceHolder
import android.view.Surface
import android.view.View
import android.view.WindowManager
import android.widget.FrameLayout
import androidx.activity.OnBackPressedCallback
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.repeatOnLifecycle
import com.deskstream.client.audio.AudioPlaybackState
import com.deskstream.client.audio.AudioReceiver
import com.deskstream.client.audio.AudioStats
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

    private lateinit var gamepadForwarder: GamepadForwarder
    private lateinit var remoteMouse: RemoteMouseController
    private var wifiLock: WifiManager.WifiLock? = null

    private var surfaceReady = false
    /** True once START_STREAM has been sent for the current READY session; reset whenever we
     * leave READY/STREAMING or the surface goes away, so we don't send it twice. */
    private var streamRequested = false
    private var statsVisible = false
    private var audioMuted = false
    private var audioNegotiationJob: Job? = null
    private var inputNegotiationJob: Job? = null
    private var stallRecoveryInProgress = false
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
    private var gamepadInventory = GamepadInventory(0, 0, emptyList())
    private var gamepadStatus = "none detected"
    private var gamepadDetail = "Connect a Bluetooth or USB controller to Android"

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        binding = ActivityStreamBinding.inflate(layoutInflater)
        setContentView(binding.root)
        gamepadForwarder = GamepadForwarder(applicationContext, ::onGamepadInventoryChanged)
        remoteMouse = RemoteMouseController(
            binding.surfaceView,
            sendMotion = { packet -> mediaReceiver?.sendMousePacket(packet) },
            onModeChanged = { updatePointerButton(it) }
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
            updatePointerButton(remoteMouse.toggleMode())
        }
        binding.streamRoot.addOnLayoutChangeListener { _, l, t, r, b, oldL, oldT, oldR, oldB ->
            if ((r - l) != (oldR - oldL) || (b - t) != (oldB - oldT)) {
                applyAspectRatio(lastStreamWidth, lastStreamHeight)
            }
        }

        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                ControlClient.stopStream()
                finish()
            }
        })

        observeControlClient()
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
        videoDecoder?.release()
        videoDecoder = VideoDecoder(
            onDropOrError = { mediaReceiver?.notifyExternalDrop() },
            onBufferRelease = { buf -> mediaReceiver?.bufferPool?.release(buf) },
            onFrameRendered = { latencyMs -> mediaReceiver?.recordDecoderSurfaceLatency(latencyMs) }
        )
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
        when (state) {
            ControlClient.State.READY -> {
                // Either a fresh session, or we just silently reconnected while this Activity
                // was in the foreground -- either way, (re)start the stream.
                streamRequested = false
                maybeStartStream()
            }
            ControlClient.State.CONNECTING -> {
                showCenterStatus("Connecting to ${ControlClient.serverIp}…")
            }
            ControlClient.State.RECONNECTING -> {
                streamRequested = false
                stopReceivers()
                showCenterStatus("Connection lost\nReconnecting to ${ControlClient.serverIp}…")
            }
            ControlClient.State.DISCONNECTED -> {
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
                if (!stallRecoveryInProgress) showCenterStatus("Stream stopped")
            }
            is ServerMessage.Bitrate -> {
                negotiatedBitrateKbps = msg.kbps
                renderDiagnostics()
            }
            is ServerMessage.Error -> {
                val description = describeStreamError(msg)
                showCenterStatus(description)
                Snackbar.make(binding.streamRoot, description, Snackbar.LENGTH_LONG).show()
            }
            else -> {}
        }
    }

    private fun maybeStartStream() {
        if (surfaceReady && !streamRequested && ControlClient.state.value == ControlClient.State.READY) {
            streamRequested = true
            showCenterStatus("Connected to ${ControlClient.serverIp}\nStarting video…")
            ControlClient.startStream(MAX_BITRATE_KBPS, TARGET_FPS)
        }
    }

    private fun onStreamStarted(msg: ServerMessage.StreamStarted) {
        stallRecoveryInProgress = false
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

        val decoder = videoDecoder
        val holder = binding.surfaceView.holder
        if (decoder == null || holder.surface == null || !holder.surface.isValid) {
            // Surface went away in the tiny window between us requesting the stream and the
            // server answering; the next surfaceCreated -> maybeStartStream cycle recovers.
            return
        }

        // Always release + recreate on STREAM_STARTED: dimensions may differ, and frameId
        // numbering restarts server-side even if they don't (protocol §5).
        mediaReceiver?.stop()
        decoder.resetForNewStream(holder.surface, msg.width, msg.height)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            try {
                holder.surface.setFrameRate(
                    msg.fps.toFloat(),
                    Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE
                )
            } catch (_: Exception) { }
        }

        val receiver = MediaReceiver(
            onFrame = { data, length, keyframe, frameId, _ ->
                decoder.submitFrame(data, length, keyframe, frameId)
            },
            onStats = { stats -> runOnUiThread { updateStatsOverlay(stats) } },
            onStalled = { runOnUiThread { restartStalledStream() } },
            onCursorPosition = { position -> runOnUiThread { updateRemoteCursor(position) } }
        )
        mediaReceiver = receiver
        receiver.start(ControlClient.serverIp, msg.mediaPort, msg.clockBaseUs)

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
        remoteMouse.setEnabled(false)
        binding.btnPointerMode.isEnabled = false
        mouseStatus = "negotiating"
        updatePointerButton(remoteMouse.currentMode())
        ControlClient.startMouseInput()
        inputNegotiationJob?.cancel()
        inputNegotiationJob = lifecycleScope.launch {
            delay(INPUT_NEGOTIATION_TIMEOUT_MS)
            if (!binding.btnPointerMode.isEnabled) {
                mouseStatus = "unavailable"
                updatePointerButton(remoteMouse.currentMode())
            }
        }
    }

    private fun onInputStarted() {
        inputNegotiationJob?.cancel()
        inputNegotiationJob = null
        mouseStatus = "live"
        remoteMouse.setEnabled(true)
        binding.btnPointerMode.isEnabled = true
        binding.tvRemoteCursor.visibility = View.VISIBLE
        updatePointerButton(remoteMouse.currentMode())
        renderDiagnostics()
    }

    private fun onInputUnavailable(message: String) {
        inputNegotiationJob?.cancel()
        inputNegotiationJob = null
        mouseStatus = "unavailable"
        remoteMouse.setEnabled(false)
        binding.btnPointerMode.isEnabled = false
        binding.tvRemoteCursor.visibility = View.GONE
        updatePointerButton(remoteMouse.currentMode())
        Snackbar.make(binding.streamRoot, "Remote mouse unavailable: $message", Snackbar.LENGTH_LONG).show()
    }

    private fun updatePointerButton(mode: MouseMode) {
        binding.btnPointerMode.text = when (mouseStatus) {
            "negotiating" -> "Mouse…"
            "unavailable" -> "No mouse"
            else -> if (mode == MouseMode.TOUCHPAD) "Mouse: Touchpad" else "Mouse: Direct"
        }
    }

    private fun updateRemoteCursor(position: CursorPosition) {
        if (mouseStatus != "live") return
        val surface = binding.surfaceView
        val cursor = binding.tvRemoteCursor
        if (surface.width <= 0 || surface.height <= 0) return
        cursor.translationX = surface.x + position.x / 65535f * surface.width - cursor.width * 0.25f
        cursor.translationY = surface.y + position.y / 65535f * surface.height - cursor.height * 0.25f
        cursor.visibility = View.VISIBLE
    }

    private fun restartStalledStream() {
        if (stallRecoveryInProgress || !surfaceReady ||
            ControlClient.state.value != ControlClient.State.STREAMING
        ) return
        stallRecoveryInProgress = true
        showCenterStatus("Video stalled\nRecovering stream…")
        remoteMouse.setEnabled(false)
        streamRequested = false
        // Wait for STREAM_STOPPED/READY before requesting the replacement pipeline. This
        // preserves STOP_STREAM -> START_STREAM ordering on the control connection.
        ControlClient.stopStream()
    }

    private fun updateStatsOverlay(stats: StreamStats) {
        lastVideoStats = stats
        renderDiagnostics()
    }

    private fun toggleDiagnostics() {
        statsVisible = !statsVisible
        binding.tvStats.visibility = if (statsVisible) View.VISIBLE else View.GONE
        binding.tvGestureHint.visibility = View.GONE
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
        Snackbar.make(binding.streamRoot, "Video is live, but audio is unavailable: $audioDetail", Snackbar.LENGTH_LONG).show()
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
        Snackbar.make(
            binding.streamRoot,
            "Controller unavailable: $gamepadDetail",
            Snackbar.LENGTH_LONG
        ).show()
    }

    private fun updateGamepadStatus() {
        binding.tvGamepadStatus.text = "Controller: $gamepadStatus"
    }

    private fun stopReceivers() {
        audioNegotiationJob?.cancel()
        audioNegotiationJob = null
        inputNegotiationJob?.cancel()
        inputNegotiationJob = null
        remoteMouse.setEnabled(false)
        binding.btnPointerMode.isEnabled = false
        binding.tvRemoteCursor.visibility = View.GONE
        mediaReceiver?.stop()
        mediaReceiver = null
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
        binding.tvStreamStatus.visibility = View.GONE
    }

    private fun updateAudioButton() {
        binding.btnAudio.text = when (audioStatus) {
            "starting" -> "Audio…"
            "unavailable" -> "No audio"
            else -> if (audioMuted) "Unmute" else "Mute"
        }
    }

    private fun renderDiagnostics() {
        if (lastStreamWidth <= 0 || lastStreamHeight <= 0) return

        val audioLabel = when {
            audioStatus == "unavailable" -> "audio unavailable"
            audioStatus == "starting" -> "audio starting"
            audioMuted -> "audio muted"
            audioStatus == "live" -> "audio on"
            else -> "audio ready"
        }
        binding.tvStreamStatus.text =
            "LIVE · ${lastStreamWidth}×${lastStreamHeight} @ $lastStreamFps · $audioLabel"

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
                append("DROPS  ${video.framesDropped}/s · ${video.fps} fps received\n")
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
        private const val MAX_BITRATE_KBPS = 20000
        private const val TARGET_FPS = 60
        private const val AUDIO_NEGOTIATION_TIMEOUT_MS = 3500L
        private const val INPUT_NEGOTIATION_TIMEOUT_MS = 2500L
    }
}
