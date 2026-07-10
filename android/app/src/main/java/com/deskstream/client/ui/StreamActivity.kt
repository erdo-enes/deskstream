package com.deskstream.client.ui

import android.os.Bundle
import android.view.Gravity
import android.view.SurfaceHolder
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
import com.deskstream.client.databinding.ActivityStreamBinding
import com.deskstream.client.net.ControlClient
import com.deskstream.client.net.MediaReceiver
import com.deskstream.client.net.StreamStats
import com.deskstream.client.proto.ServerMessage
import com.deskstream.client.video.VideoDecoder
import com.google.android.material.snackbar.Snackbar
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

    private var surfaceReady = false
    /** True once START_STREAM has been sent for the current READY session; reset whenever we
     * leave READY/STREAMING or the surface goes away, so we don't send it twice. */
    private var streamRequested = false
    private var statsVisible = false
    /** Last dimensions received in STREAM_STARTED, so letterboxing can be re-applied when the
     * container is resized (fold/unfold, multi-window). */
    private var lastStreamWidth = 0
    private var lastStreamHeight = 0

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        binding = ActivityStreamBinding.inflate(layoutInflater)
        setContentView(binding.root)
        applyImmersiveMode()

        binding.surfaceView.holder.addCallback(this)
        binding.streamRoot.setOnClickListener {
            statsVisible = !statsVisible
            binding.tvStats.visibility = if (statsVisible) View.VISIBLE else View.GONE
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
        maybeStartStream()
    }

    override fun onStop() {
        super.onStop()
        // Per §5: backgrounding stops the stream but keeps the control socket alive.
        ControlClient.stopStream()
        streamRequested = false
    }

    override fun onDestroy() {
        super.onDestroy()
        mediaReceiver?.stop()
        mediaReceiver = null
        videoDecoder?.release()
        videoDecoder = null
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) applyImmersiveMode()
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
            onBufferRelease = { buf -> mediaReceiver?.bufferPool?.release(buf) }
        )
        maybeStartStream()
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
        // No-op: video dimensions come from STREAM_STARTED, not the surface's pixel size.
    }

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        surfaceReady = false
        streamRequested = false
        mediaReceiver?.stop()
        mediaReceiver = null
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
            ControlClient.State.RECONNECTING -> {
                streamRequested = false
                binding.tvConnecting.text = "Reconnecting..."
                binding.tvConnecting.visibility = View.VISIBLE
            }
            ControlClient.State.DISCONNECTED -> {
                binding.tvConnecting.text = "Disconnected"
                binding.tvConnecting.visibility = View.VISIBLE
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
            ServerMessage.StreamStopped -> {
                binding.tvConnecting.text = "Stream stopped"
                binding.tvConnecting.visibility = View.VISIBLE
            }
            is ServerMessage.Error -> {
                Snackbar.make(binding.streamRoot, msg.message.ifEmpty { msg.code }, Snackbar.LENGTH_LONG).show()
            }
            else -> {}
        }
    }

    private fun maybeStartStream() {
        if (surfaceReady && !streamRequested && ControlClient.state.value == ControlClient.State.READY) {
            streamRequested = true
            binding.tvConnecting.text = "Connecting..."
            binding.tvConnecting.visibility = View.VISIBLE
            ControlClient.startStream(MAX_BITRATE_KBPS, TARGET_FPS)
        }
    }

    private fun onStreamStarted(msg: ServerMessage.StreamStarted) {
        binding.tvConnecting.visibility = View.GONE
        lastStreamWidth = msg.width
        lastStreamHeight = msg.height
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

        val receiver = MediaReceiver(
            onFrame = { data, length, keyframe, frameId -> decoder.submitFrame(data, length, keyframe, frameId) },
            onStats = { stats -> runOnUiThread { updateStatsOverlay(stats) } }
        )
        mediaReceiver = receiver
        receiver.start(ControlClient.serverIp, msg.mediaPort)
    }

    private fun updateStatsOverlay(stats: StreamStats) {
        binding.tvStats.text = "fps: ${stats.fps}\n" +
            "bitrate: ${stats.kbps} kbps\n" +
            "dropped: ${stats.framesDropped}\n" +
            "loss: ${"%.1f".format(stats.lossPercent)}%"
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
    }
}
