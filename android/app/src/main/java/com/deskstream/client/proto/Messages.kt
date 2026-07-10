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

    fun startStream(maxBitrateKbps: Int, fps: Int): String =
        JSONObject().apply {
            put("type", "START_STREAM")
            put("maxBitrateKbps", maxBitrateKbps)
            put("fps", fps)
        }.toString()

    fun stopStream(): String =
        JSONObject().apply {
            put("type", "STOP_STREAM")
        }.toString()

    fun requestIdr(): String =
        JSONObject().apply {
            put("type", "REQUEST_IDR")
        }.toString()

    fun stats(framesOk: Int, framesDropped: Int, bytes: Long, intervalMs: Long): String =
        JSONObject().apply {
            put("type", "STATS")
            put("framesOk", framesOk)
            put("framesDropped", framesDropped)
            put("bytes", bytes)
            put("intervalMs", intervalMs)
        }.toString()

    fun ping(): String =
        JSONObject().apply {
            put("type", "PING")
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
        val codec: String
    ) : ServerMessage()
    data object StreamStopped : ServerMessage()
    data class Bitrate(val kbps: Int) : ServerMessage()
    data object Pong : ServerMessage()

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
                    codec = obj.optString("codec", "h264")
                )
                "STREAM_STOPPED" -> StreamStopped
                "BITRATE" -> Bitrate(kbps = obj.optInt("kbps", 0))
                "PONG" -> Pong
                else -> Unknown(type)
            }
        }
    }
}
