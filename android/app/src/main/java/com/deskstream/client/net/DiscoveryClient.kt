package com.deskstream.client.net

import android.content.Context
import android.net.wifi.WifiManager
import android.util.Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.SocketException
import java.net.SocketTimeoutException

data class DiscoveredServer(val name: String, val ip: String, val controlPort: Int)

/**
 * UDP discovery per docs/PROTOCOL.md §1.
 *
 * Sends the 8-byte ASCII probe "DSPROBE1" to 255.255.255.255:47800 and to every local
 * interface's subnet broadcast address, once per second while active. Listens on the same
 * socket for unicast JSON replies of the form
 * {"type":"DSREPLY","ver":1,"name":"<hostname>","controlPort":47801}.
 *
 * Not thread-safe for concurrent start()/stop() calls; callers (MainActivity) drive this
 * from the main thread only.
 */
class DiscoveryClient(private val appContext: Context) {

    private var socket: DatagramSocket? = null
    private var job: Job? = null
    private var multicastLock: WifiManager.MulticastLock? = null
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    /** Begins broadcasting probes and delivers each parsed reply on the main thread. */
    fun start(onServerFound: (DiscoveredServer) -> Unit) {
        stop()

        try {
            val wifiManager = appContext.applicationContext
                .getSystemService(Context.WIFI_SERVICE) as? WifiManager
            multicastLock = wifiManager?.createMulticastLock("deskstream-discovery")?.apply {
                setReferenceCounted(true)
                acquire()
            }
        } catch (e: Exception) {
            Log.w(TAG, "Could not acquire multicast lock", e)
        }

        val sock = try {
            DatagramSocket(null).apply {
                reuseAddress = true
                broadcast = true
                bind(InetSocketAddress(0))
                soTimeout = 1100
            }
        } catch (e: SocketException) {
            Log.e(TAG, "Failed to open discovery socket", e)
            return
        }
        socket = sock

        job = scope.launch {
            val probeJob = launch { probeLoop(sock) }
            try {
                val buf = ByteArray(2048)
                while (currentCoroutineContext().isActive) {
                    val packet = DatagramPacket(buf, buf.size)
                    try {
                        sock.receive(packet)
                    } catch (e: SocketTimeoutException) {
                        continue
                    } catch (e: IOException) {
                        break // socket closed underneath us
                    }
                    val senderIp = packet.address?.hostAddress ?: continue
                    val text = String(packet.data, 0, packet.length, Charsets.UTF_8)
                    val server = parseReply(text, senderIp) ?: continue
                    withContext(Dispatchers.Main) { onServerFound(server) }
                }
            } finally {
                probeJob.cancel()
            }
        }
    }

    fun stop() {
        job?.cancel()
        job = null
        socket?.close()
        socket = null
        try {
            multicastLock?.let { if (it.isHeld) it.release() }
        } catch (e: Exception) {
            // ignore
        }
        multicastLock = null
    }

    private suspend fun probeLoop(sock: DatagramSocket) {
        val probeBytes = PROBE_MESSAGE.toByteArray(Charsets.US_ASCII)
        while (currentCoroutineContext().isActive) {
            sendProbe(sock, probeBytes)
            delay(1000)
        }
    }

    private fun sendProbe(sock: DatagramSocket, bytes: ByteArray) {
        val targets = mutableListOf<InetAddress>()
        try {
            targets.add(InetAddress.getByName("255.255.255.255"))
        } catch (e: Exception) {
            // ignore
        }
        try {
            val ifaces = NetworkInterface.getNetworkInterfaces()
            while (ifaces != null && ifaces.hasMoreElements()) {
                val iface = ifaces.nextElement()
                if (!iface.isUp || iface.isLoopback) continue
                for (ia in iface.interfaceAddresses) {
                    val bcast = ia.broadcast
                    if (bcast != null) targets.add(bcast)
                }
            }
        } catch (e: Exception) {
            Log.w(TAG, "Failed to enumerate interfaces for broadcast", e)
        }

        for (addr in targets.distinct()) {
            try {
                sock.send(DatagramPacket(bytes, bytes.size, addr, DISCOVERY_PORT))
            } catch (e: IOException) {
                // one target failing shouldn't stop the others
            }
        }
    }

    private fun parseReply(text: String, senderIp: String): DiscoveredServer? {
        return try {
            val obj = JSONObject(text)
            if (obj.optString("type") != "DSREPLY") return null
            val name = obj.optString("name").ifEmpty { senderIp }
            val port = obj.optInt("controlPort", DEFAULT_CONTROL_PORT)
            DiscoveredServer(name, senderIp, port)
        } catch (e: Exception) {
            null
        }
    }

    companion object {
        private const val TAG = "DiscoveryClient"
        private const val PROBE_MESSAGE = "DSPROBE1"
        const val DISCOVERY_PORT = 47800
        const val DEFAULT_CONTROL_PORT = 47801
    }
}
