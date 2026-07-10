package com.deskstream.client.net

import android.content.Context
import android.os.SystemClock
import android.util.Log
import com.deskstream.client.data.Prefs
import com.deskstream.client.proto.ClientMessages
import com.deskstream.client.proto.ServerMessage
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.BufferedInputStream
import java.io.DataInputStream
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Control channel: TCP 47801, length-prefixed JSON, per docs/PROTOCOL.md §2.
 *
 * Implemented as a process-lifetime singleton (not tied to any single Activity) because the
 * protocol explicitly requires the control socket to survive Activity transitions: it is
 * opened from MainActivity during discovery/pairing and then handed off to StreamActivity
 * for START_STREAM/STOP_STREAM, and per §5 must also survive the app being backgrounded
 * (StreamActivity.onStop) without disconnecting.
 */
object ControlClient {

    enum class State { DISCONNECTED, CONNECTING, PAIRING, READY, STREAMING, RECONNECTING }

    private const val TAG = "ControlClient"
    private const val MAX_FRAME_LEN = 65536
    private const val CONNECT_TIMEOUT_MS = 5000
    private const val PING_INTERVAL_MS = 2000L
    private const val SILENCE_TIMEOUT_MS = 6000L
    private const val IDR_MIN_INTERVAL_MS = 300L
    private const val MAX_BACKOFF_MS = 5000L
    private const val INITIAL_BACKOFF_MS = 500L

    private lateinit var appContext: Context
    private val prefs: Prefs by lazy { Prefs(appContext) }

    private val _state = MutableStateFlow(State.DISCONNECTED)
    val state: StateFlow<State> = _state.asStateFlow()

    /** One-shot server messages (and locally synthesized connection errors) for UI consumption. */
    private val _events = MutableSharedFlow<ServerMessage>(extraBufferCapacity = 32)
    val events: SharedFlow<ServerMessage> = _events.asSharedFlow()

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val writeMutex = Mutex()

    // connect()/disconnect() are called directly from the main thread while doConnect(),
    // onSocketClosed() and the ping/watchdog loops mutate the same fields from IO-dispatched
    // coroutines -- @Volatile for cross-thread visibility. (These aren't compound atomic
    // updates, but every mutation here is idempotent/order-tolerant by construction; see the
    // class-level notes on scheduleReconnect() for the one place that matters.)
    @Volatile private var connectionJob: Job? = null
    @Volatile private var pingJob: Job? = null
    @Volatile private var watchdogJob: Job? = null
    @Volatile private var socket: Socket? = null

    @Volatile var serverIp: String = ""
        private set
    @Volatile var serverPort: Int = 0
        private set
    @Volatile private var lastReceivedAt = 0L
    @Volatile private var lastIdrRequestAt = 0L
    @Volatile private var explicitlyDisconnected = false
    @Volatile private var lastSentToken = ""
    @Volatile private var backoffMs = INITIAL_BACKOFF_MS
    @Volatile private var bestClockRttUs = Long.MAX_VALUE
    @Volatile var serverClockOffsetUs = 0L
        private set
    @Volatile var clockSynchronized = false
        private set

    fun init(context: Context) {
        if (!::appContext.isInitialized) {
            appContext = context.applicationContext
        }
    }

    /** Starts a fresh connection attempt, tearing down any previous one first. */
    fun connect(ip: String, port: Int) {
        explicitlyDisconnected = false
        connectionJob?.cancel()
        stopPingAndWatchdog()
        closeSocketQuietly()
        serverIp = ip
        serverPort = port
        backoffMs = INITIAL_BACKOFF_MS
        bestClockRttUs = Long.MAX_VALUE
        serverClockOffsetUs = 0L
        clockSynchronized = false
        _state.value = State.CONNECTING
        connectionJob = scope.launch { doConnect(ip, port, isReconnect = false) }
    }

    fun disconnect() {
        explicitlyDisconnected = true
        connectionJob?.cancel()
        stopPingAndWatchdog()
        closeSocketQuietly()
        _state.value = State.DISCONNECTED
    }

    fun sendHello(token: String) {
        lastSentToken = token
        scope.launch { writeFrame(ClientMessages.hello(prefs.clientId, prefs.clientName, token)) }
    }

    fun sendPairRequest() {
        scope.launch { writeFrame(ClientMessages.pairRequest()) }
    }

    fun sendPairCode(pin: String) {
        scope.launch { writeFrame(ClientMessages.pairCode(pin)) }
    }

    fun startStream(maxBitrateKbps: Int = 20000, fps: Int = 60) {
        scope.launch { writeFrame(ClientMessages.startStream(maxBitrateKbps, fps)) }
    }

    fun stopStream() {
        scope.launch { writeFrame(ClientMessages.stopStream()) }
    }

    fun startAudio() {
        scope.launch { writeFrame(ClientMessages.startAudio()) }
    }

    fun mediaReady(port: Int) {
        scope.launch { writeFrame(ClientMessages.mediaReady(port)) }
    }

    fun audioReady(port: Int) {
        scope.launch { writeFrame(ClientMessages.audioReady(port)) }
    }

    fun startGamepads(controllers: Int) {
        scope.launch { writeFrame(ClientMessages.startGamepads(controllers)) }
    }

    fun stopGamepads() {
        scope.launch { writeFrame(ClientMessages.stopGamepads()) }
    }

    fun startMouseInput() {
        scope.launch { writeFrame(ClientMessages.startMouseInput()) }
    }

    fun stopInput() {
        scope.launch { writeFrame(ClientMessages.stopInput()) }
    }

    fun resetMouse() {
        scope.launch { writeFrame(ClientMessages.resetMouse()) }
    }

    fun sendMouseButton(sequence: Long, button: String, down: Boolean) {
        scope.launch { writeFrame(ClientMessages.mouseButton(sequence, button, down)) }
    }

    fun sendMouseClick(firstSequence: Long, button: String) {
        scope.launch {
            // One coroutine + the write mutex preserves down/up ordering for a tap.
            writeFrame(ClientMessages.mouseButton(firstSequence, button, true))
            writeFrame(ClientMessages.mouseButton(firstSequence + 1, button, false))
        }
    }

    /** Client-side rate limit (300 ms) on top of the server's own rate limit, per §2.3. */
    fun requestIdr() {
        val now = SystemClock.elapsedRealtime()
        if (now - lastIdrRequestAt < IDR_MIN_INTERVAL_MS) return
        lastIdrRequestAt = now
        scope.launch { writeFrame(ClientMessages.requestIdr()) }
    }

    fun sendStats(
        framesOk: Int,
        framesDropped: Int,
        bytes: Long,
        intervalMs: Long,
        captureToReceiveP95Ms: Int,
        decodeToSurfaceP95Ms: Int
    ) {
        scope.launch {
            writeFrame(ClientMessages.stats(
                framesOk, framesDropped, bytes, intervalMs,
                captureToReceiveP95Ms, decodeToSurfaceP95Ms
            ))
        }
    }

    // -----------------------------------------------------------------------------------

    private suspend fun doConnect(ip: String, port: Int, isReconnect: Boolean) {
        _state.value = if (isReconnect) State.RECONNECTING else State.CONNECTING
        val sock = try {
            Socket().apply {
                tcpNoDelay = true
                connect(InetSocketAddress(ip, port), CONNECT_TIMEOUT_MS)
            }
        } catch (e: Exception) {
            Log.w(TAG, "connect to $ip:$port failed", e)
            if (isReconnect) {
                scheduleReconnect()
            } else {
                _state.value = State.DISCONNECTED
                _events.tryEmit(ServerMessage.Error("CONNECT_FAILED", e.message ?: "Connection failed"))
            }
            return
        }

        socket = sock
        backoffMs = INITIAL_BACKOFF_MS
        lastReceivedAt = SystemClock.elapsedRealtime()
        // A reconnect may land on a freshly restarted server whose monotonic-clock origin is
        // different. Never carry an old low-RTT estimate across TCP connections.
        bestClockRttUs = Long.MAX_VALUE
        serverClockOffsetUs = 0L
        clockSynchronized = false
        startPingAndWatchdog()

        scope.launch {
            try {
                readLoop(sock)
            } catch (e: Exception) {
                Log.i(TAG, "control read loop ended: ${e.message}")
            }
            onSocketClosed(sock)
        }

        sendHello(prefs.tokenForServer(ip))
    }

    private suspend fun readLoop(sock: Socket) {
        val input = DataInputStream(BufferedInputStream(sock.getInputStream(), 8192))
        while (true) {
            val len = input.readInt() // big-endian per DataInput contract, matches spec
            if (len < 0 || len > MAX_FRAME_LEN) {
                Log.w(TAG, "malformed control frame length=$len; closing")
                return
            }
            val buf = ByteArray(len)
            input.readFully(buf)
            lastReceivedAt = SystemClock.elapsedRealtime()
            val json = String(buf, Charsets.UTF_8)
            val msg = ServerMessage.parse(json)
            if (msg == null) {
                Log.w(TAG, "malformed control JSON; closing")
                return
            }
            handleMessage(msg)
        }
    }

    private fun handleMessage(msg: ServerMessage) {
        when (msg) {
            is ServerMessage.HelloOk -> {
                // PAIR_OK is persisted before serverName is known; refresh the display name.
                val token = prefs.tokenForServer(serverIp)
                if (token.isNotEmpty() && msg.serverName.isNotEmpty()) {
                    prefs.savePairedServer(serverIp, token, msg.serverName)
                }
                _state.value = State.READY
            }
            ServerMessage.PairRequired -> {
                // If the server no longer recognizes a token we sent, drop it so the UI
                // re-pairs cleanly instead of looping with a stale credential (§5).
                if (lastSentToken.isNotEmpty()) {
                    prefs.clearToken(serverIp)
                }
                // Initiate pairing right away (§2.2: on PAIR_REQUIRED the client sends
                // PAIR_REQUEST; the server then shows the PIN). Done here, not in the UI, so
                // the step can't be lost to Activity lifecycle timing -- the UI only needs to
                // collect the PIN from the user.
                sendPairRequest()
                _state.value = State.PAIRING
            }
            is ServerMessage.PairOk -> {
                val existingName = prefs.getPairedServer()?.name ?: ""
                prefs.savePairedServer(serverIp, msg.token, existingName)
                // Per §2.2 the client sends HELLO again with the fresh token. Done here (not
                // in the UI) so the protocol step can't be lost to Activity lifecycle timing.
                sendHello(msg.token)
            }
            is ServerMessage.PairFail -> {
                // stay in PAIRING; UI shows attemptsLeft and lets the user retry
            }
            is ServerMessage.Error -> {
                // surfaced to UI as-is
            }
            is ServerMessage.StreamStarted -> {
                _state.value = State.STREAMING
            }
            ServerMessage.StreamStopped -> {
                if (_state.value == State.STREAMING) _state.value = State.READY
            }
            is ServerMessage.AudioStarted, is ServerMessage.AudioUnavailable -> {
                // event only; audio is optional and does not change video session state
            }
            is ServerMessage.GamepadStarted,
            is ServerMessage.GamepadUnavailable,
            is ServerMessage.GamepadRumble -> {
                // event only; controller forwarding is optional
            }
            ServerMessage.InputStarted, is ServerMessage.InputUnavailable -> {
                // event only; authenticated remote input is optional
            }
            is ServerMessage.Bitrate -> {
                // event only, no state change
            }
            is ServerMessage.Pong -> {
                updateClockEstimate(msg)
            }
            is ServerMessage.Unknown -> {
                return // ignore unknown types per spec, don't forward as an event
            }
        }
        _events.tryEmit(msg)
    }

    private fun onSocketClosed(sock: Socket) {
        if (socket !== sock) return // stale callback from a superseded connection
        stopPingAndWatchdog()
        try {
            sock.close()
        } catch (e: Exception) {
            // ignore
        }
        val wasActive = _state.value == State.READY || _state.value == State.STREAMING
        socket = null

        if (explicitlyDisconnected) {
            _state.value = State.DISCONNECTED
            return
        }

        if (wasActive) {
            _state.value = State.RECONNECTING
            scheduleReconnect()
        } else {
            _state.value = State.DISCONNECTED
            _events.tryEmit(ServerMessage.Error("CONNECTION_LOST", "Connection lost"))
        }
    }

    private fun scheduleReconnect() {
        connectionJob?.cancel()
        val delayMs = backoffMs
        backoffMs = (backoffMs * 2).coerceAtMost(MAX_BACKOFF_MS)
        connectionJob = scope.launch {
            delay(delayMs)
            if (explicitlyDisconnected) return@launch
            doConnect(serverIp, serverPort, isReconnect = true)
        }
    }

    private fun startPingAndWatchdog() {
        stopPingAndWatchdog()
        pingJob = scope.launch {
            while (isActive) {
                delay(PING_INTERVAL_MS)
                writeFrame(ClientMessages.ping(nowUs()))
            }
        }
        watchdogJob = scope.launch {
            while (isActive) {
                delay(1000)
                val silence = SystemClock.elapsedRealtime() - lastReceivedAt
                if (silence > SILENCE_TIMEOUT_MS) {
                    Log.w(TAG, "control channel silent for ${silence}ms; closing")
                    // Closing here unblocks the reader's blocking read(), which then drives
                    // the single onSocketClosed() cleanup/reconnect path.
                    socket?.let { s -> try { s.close() } catch (e: Exception) {} }
                    return@launch
                }
            }
        }
    }

    private fun updateClockEstimate(pong: ServerMessage.Pong) {
        if (pong.t0Us <= 0L || pong.t1Us <= 0L || pong.t2Us < pong.t1Us) return
        val t3Us = nowUs()
        val rttUs = (t3Us - pong.t0Us) - (pong.t2Us - pong.t1Us)
        if (rttUs < 0L || rttUs >= bestClockRttUs) return
        bestClockRttUs = rttUs
        // NTP offset: positive means the server monotonic clock is ahead of Android's.
        serverClockOffsetUs = ((pong.t1Us - pong.t0Us) + (pong.t2Us - t3Us)) / 2L
        clockSynchronized = true
    }

    private fun nowUs(): Long = SystemClock.elapsedRealtimeNanos() / 1000L

    private fun stopPingAndWatchdog() {
        pingJob?.cancel(); pingJob = null
        watchdogJob?.cancel(); watchdogJob = null
    }

    private fun closeSocketQuietly() {
        socket?.let { try { it.close() } catch (e: Exception) {} }
        socket = null
    }

    private suspend fun writeFrame(json: String) {
        val sock = socket ?: return
        withContext(Dispatchers.IO) {
            writeMutex.withLock {
                try {
                    val bytes = json.toByteArray(Charsets.UTF_8)
                    val header = ByteBuffer.allocate(4).order(ByteOrder.BIG_ENDIAN).putInt(bytes.size).array()
                    val out = sock.getOutputStream()
                    out.write(header)
                    out.write(bytes)
                    out.flush()
                } catch (e: IOException) {
                    Log.w(TAG, "control write failed", e)
                    try { sock.close() } catch (e2: Exception) {}
                }
            }
        }
    }
}
