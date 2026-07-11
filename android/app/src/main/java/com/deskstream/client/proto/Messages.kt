package com.deskstream.client.proto

import org.json.JSONObject

/**
 * Control-channel JSON message models per docs/PROTOCOL.md §2.
 *
 * We use org.json (bundled in the Android platform) rather than kotlinx.serialization to
 * avoid pulling in a KSP/serialization plugin for a handful of small, flat messages.
 */
const val PROTOCOL_VERSION = 1

// ---------------------------------------------------------------------------------------
// Outgoing (client -> server)
// ---------------------------------------------------------------------------------------

object ClientMessages {

    fun hello(clientId: String, clientName: String, token: String): String =
        JSONObject().apply {
            put("type", "HELLO")
            put("ver", PROTOCOL_VERSION)
            put("clientId", clientId)
            put("clientName", clientName)
            put("token", token)
        }.toString()

    fun pairRequest(): String =
        JSONObject().apply {
            put("type", "PAIR_REQUEST")
        }.toString()

    fun pairCode(pin: String): String =
        JSONObject().apply {
            put("type", "PAIR_CODE")
            put("pin", pin)
        }.toString()

    /**
     * [quality] is `"native"` (default) or `"720p"`. Servers older than v0.5.0 ignore the
     * unknown field and stream native resolution, which keeps this change backward-compatible.
     * The client always sizes its decoder from `STREAM_STARTED.width/height`, never from this
     * request, so a server that ignores or rejects the field cannot desync the client.
     */
    fun startStream(maxBitrateKbps: Int, fps: Int, quality: String = "native"): String =
        JSONObject().apply {
            put("type", "START_STREAM")
            put("maxBitrateKbps", maxBitrateKbps)
            put("fps", fps)
            put("quality", quality)
        }.toString()

    fun mediaReady(port: Int): String =
        JSONObject().apply {
            put("type", "MEDIA_READY")
            put("port", port.coerceIn(1, 65535))
        }.toString()

    fun audioReady(port: Int): String =
        JSONObject().apply {
            put("type", "AUDIO_READY")
            put("port", port.coerceIn(1, 65535))
        }.toString()

    fun stopStream(): String =
        JSONObject().apply {
            put("type", "STOP_STREAM")
        }.toString()

    fun startAudio(): String =
        JSONObject().apply {
            put("type", "AUDIO_START")
        }.toString()

    fun startGamepads(controllers: Int): String =
        JSONObject().apply {
            put("type", "GAMEPAD_START")
            put("controllers", controllers.coerceIn(1, 4))
        }.toString()

    fun stopGamepads(): String =
        JSONObject().apply {
            put("type", "GAMEPAD_STOP")
        }.toString()

    fun startMouseInput(): String =
        JSONObject().apply {
            put("type", "INPUT_START")
            put("mouse", true)
        }.toString()

    fun stopInput(): String =
        JSONObject().apply { put("type", "INPUT_STOP") }.toString()

    fun resetMouse(): String =
        JSONObject().apply { put("type", "MOUSE_RESET") }.toString()

    fun mouseButton(sequence: Long, button: String, down: Boolean): String =
        JSONObject().apply {
            put("type", "MOUSE_BUTTON")
            put("sequence", sequence and 0xFFFFFFFFL)
            put("button", button)
            put("down", down)
        }.toString()

    fun requestIdr(): String =
        JSONObject().apply {
            put("type", "REQUEST_IDR")
        }.toString()

    fun stats(
        framesOk: Int,
        framesDropped: Int,
        bytes: Long,
        intervalMs: Long,
        captureToReceiveP95Ms: Int,
        decodeToSurfaceP95Ms: Int
    ): String =
        JSONObject().apply {
            put("type", "STATS")
            put("framesOk", framesOk)
            put("framesDropped", framesDropped)
            put("bytes", bytes)
            put("intervalMs", intervalMs)
            put("captureToReceiveP95Ms", captureToReceiveP95Ms)
            put("decodeToSurfaceP95Ms", decodeToSurfaceP95Ms)
        }.toString()

    fun ping(t0Us: Long): String =
        JSONObject().apply {
            put("type", "PING")
            put("t0Us", t0Us)
        }.toString()
}

// ---------------------------------------------------------------------------------------
// Incoming (server -> client)
// ---------------------------------------------------------------------------------------

sealed class ServerMessage {
    data class HelloOk(val serverName: String, val width: Int, val height: Int) : ServerMessage()
    data object PairRequired : ServerMessage()
    data class Error(val code: String, val message: String) : ServerMessage()
    data class PairOk(val token: String) : ServerMessage()
    data class PairFail(val attemptsLeft: Int) : ServerMessage()
    data class StreamStarted(
        val mediaPort: Int,
        val width: Int,
        val height: Int,
        val fps: Int,
        val codec: String,
        val encoderBackend: String,
        val clockBaseUs: Long
    ) : ServerMessage()
    data object StreamStopped : ServerMessage()
    data class AudioStarted(
        val audioPort: Int,
        val sampleRate: Int,
        val channels: Int,
        val format: String,
        val packetSamples: Int
    ) : ServerMessage()
    data class AudioUnavailable(val message: String) : ServerMessage()
    data class GamepadStarted(val controllers: Int, val controllerType: String) : ServerMessage()
    data class GamepadUnavailable(val message: String, val driverUrl: String) : ServerMessage()
    data class GamepadRumble(
        val controllerId: Int,
        val largeMotor: Int,
        val smallMotor: Int
    ) : ServerMessage()
    data object InputStarted : ServerMessage()
    data class InputUnavailable(val message: String) : ServerMessage()
    data class Bitrate(val kbps: Int) : ServerMessage()
    data class Pong(val t0Us: Long, val t1Us: Long, val t2Us: Long) : ServerMessage()

    /** Any type not recognized above; per spec, unknown types MUST be ignored. */
    data class Unknown(val type: String) : ServerMessage()

    companion object {
        /** Returns null only on malformed JSON / missing "type" — caller should treat that as
         * a protocol violation and close the socket. A recognized-but-unhandled type still
         * parses successfully as [Unknown]. */
        fun parse(json: String): ServerMessage? {
            val obj = try {
                JSONObject(json)
            } catch (e: Exception) {
                return null
            }
            val type = obj.optString("type", "")
            if (type.isEmpty()) return null

            return when (type) {
                "HELLO_OK" -> HelloOk(
                    serverName = obj.optString("serverName", ""),
                    width = obj.optInt("width", 0),
                    height = obj.optInt("height", 0)
                )
                "PAIR_REQUIRED" -> PairRequired
                "ERROR" -> Error(
                    code = obj.optString("code", ""),
                    message = obj.optString("message", "")
                )
                "PAIR_OK" -> PairOk(token = obj.optString("token", ""))
                "PAIR_FAIL" -> PairFail(attemptsLeft = obj.optInt("attemptsLeft", 0))
                "STREAM_STARTED" -> StreamStarted(
                    mediaPort = obj.optInt("mediaPort", 0),
                    width = obj.optInt("width", 0),
                    height = obj.optInt("height", 0),
                    fps = obj.optInt("fps", 0),
                    codec = obj.optString("codec", "h264"),
                    encoderBackend = obj.optString("encoderBackend", "media-foundation"),
                    clockBaseUs = obj.optLong("clockBaseUs", 0L)
                )
                "STREAM_STOPPED" -> StreamStopped
                "AUDIO_STARTED" -> AudioStarted(
                    audioPort = obj.optInt("audioPort", 0),
                    sampleRate = obj.optInt("sampleRate", 0),
                    channels = obj.optInt("channels", 0),
                    format = obj.optString("format", ""),
                    packetSamples = obj.optInt("packetSamples", 0)
                )
                "AUDIO_UNAVAILABLE" -> AudioUnavailable(
                    message = obj.optString("message", "System audio is unavailable")
                )
                "GAMEPAD_STARTED" -> GamepadStarted(
                    controllers = obj.optInt("controllers", 0),
                    controllerType = obj.optString("controllerType", "xbox360")
                )
                "GAMEPAD_UNAVAILABLE" -> GamepadUnavailable(
                    message = obj.optString("message", "Virtual controller is unavailable"),
                    driverUrl = obj.optString("driverUrl", "")
                )
                "GAMEPAD_RUMBLE" -> GamepadRumble(
                    controllerId = obj.optInt("controllerId", 0),
                    largeMotor = obj.optInt("largeMotor", 0).coerceIn(0, 255),
                    smallMotor = obj.optInt("smallMotor", 0).coerceIn(0, 255)
                )
                "INPUT_STARTED" -> InputStarted
                "INPUT_UNAVAILABLE" -> InputUnavailable(
                    message = obj.optString("message", "Remote mouse is unavailable")
                )
                "BITRATE" -> Bitrate(kbps = obj.optInt("kbps", 0))
                "PONG" -> Pong(
                    t0Us = obj.optLong("t0Us", 0L),
                    t1Us = obj.optLong("t1Us", 0L),
                    t2Us = obj.optLong("t2Us", 0L)
                )
                else -> Unknown(type)
            }
        }
    }
}
