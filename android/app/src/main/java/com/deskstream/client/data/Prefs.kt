package com.deskstream.client.data

import android.content.Context
import android.os.Build
import java.util.UUID

/**
 * Thin wrapper around SharedPreferences.
 *
 * Stores:
 *  - clientId: a random UUID generated once on first launch, stable per install. Sent in
 *    every HELLO so the server can map clientId -> pairing token.
 *  - The single most-recently-paired server (ip, token, name). Pairing tokens are only
 *    valid against the server that issued them, so if the user connects to a different
 *    server IP we simply send an empty token and re-pair (per protocol §2.2).
 */
class Prefs(context: Context) {

    private val sp = context.applicationContext.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    /** Stable per-install client identifier, generated once. */
    val clientId: String
        get() {
            var id = sp.getString(KEY_CLIENT_ID, null)
            if (id == null) {
                id = UUID.randomUUID().toString()
                sp.edit().putString(KEY_CLIENT_ID, id).apply()
            }
            return id
        }

    /** Human-readable device model, sent as clientName in HELLO. */
    val clientName: String
        get() = "${Build.MANUFACTURER} ${Build.MODEL}".trim()

    data class PairedServer(val ip: String, val token: String, val name: String)

    fun getPairedServer(): PairedServer? {
        val ip = sp.getString(KEY_SERVER_IP, null) ?: return null
        val token = sp.getString(KEY_SERVER_TOKEN, null) ?: return null
        val name = sp.getString(KEY_SERVER_NAME, "") ?: ""
        return PairedServer(ip, token, name)
    }

    /** Returns the stored token only if it belongs to the given server IP, else empty string. */
    fun tokenForServer(ip: String): String {
        val paired = getPairedServer() ?: return ""
        return if (paired.ip == ip) paired.token else ""
    }

    fun savePairedServer(ip: String, token: String, name: String) {
        sp.edit()
            .putString(KEY_SERVER_IP, ip)
            .putString(KEY_SERVER_TOKEN, token)
            .putString(KEY_SERVER_NAME, name)
            .apply()
    }

    /** Clears the stored token for a server (e.g. after an auth failure) so pairing restarts. */
    fun clearToken(ip: String) {
        val paired = getPairedServer() ?: return
        if (paired.ip == ip) {
            sp.edit().remove(KEY_SERVER_TOKEN).apply()
        }
    }

    /** Preferred stream quality. Profile v1 deliberately migrates every existing install once
     * to the new stability-first 720p60 baseline; later explicit user choices are preserved. */
    var streamQuality: String
        get() {
            if (sp.getInt(KEY_STREAM_PROFILE_VERSION, 0) < STREAM_PROFILE_VERSION) {
                sp.edit()
                    .putString(KEY_STREAM_QUALITY, QUALITY_720P)
                    .putInt(KEY_STREAM_PROFILE_VERSION, STREAM_PROFILE_VERSION)
                    .apply()
                return QUALITY_720P
            }
            return sp.getString(KEY_STREAM_QUALITY, QUALITY_720P) ?: QUALITY_720P
        }
        set(value) {
            val normalized = if (value == QUALITY_NATIVE) QUALITY_NATIVE else QUALITY_720P
            sp.edit()
                .putString(KEY_STREAM_QUALITY, normalized)
                .putInt(KEY_STREAM_PROFILE_VERSION, STREAM_PROFILE_VERSION)
                .apply()
        }

    companion object {
        private const val PREFS_NAME = "deskstream_prefs"
        private const val KEY_CLIENT_ID = "client_id"
        private const val KEY_SERVER_IP = "server_ip"
        private const val KEY_SERVER_TOKEN = "server_token"
        private const val KEY_SERVER_NAME = "server_name"
        private const val KEY_STREAM_QUALITY = "stream_quality"
        private const val KEY_STREAM_PROFILE_VERSION = "stream_profile_version"
        private const val STREAM_PROFILE_VERSION = 1
        const val QUALITY_NATIVE = "native"
        const val QUALITY_720P = "720p"
    }
}
