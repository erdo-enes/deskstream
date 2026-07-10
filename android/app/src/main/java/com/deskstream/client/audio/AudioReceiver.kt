package com.deskstream.client.audio

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.os.SystemClock
import android.util.Log
import com.deskstream.client.proto.AudioPacketHeader
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketTimeoutException

enum class AudioPlaybackState { STARTING, READY, PLAYING, ERROR }

data class AudioStats(
    val kbps: Int,
    val packetLossPercent: Float,
    val packetsLost: Long,
    val outputDrops: Int,
    val underruns: Int,
    val receivingAudio: Boolean,
    val outputBufferMs: Int
)

/**
 * Receives the independent audio UDP stream and feeds it directly to a low-latency
 * [AudioTrack]. There is no network jitter queue: non-blocking writes drop instead of
 * allowing latency to grow. A few missing 5 ms packets are replaced with silence so a
 * transient Wi-Fi loss does not permanently pull audio ahead of video.
 */
class AudioReceiver(
    private val onState: (AudioPlaybackState, String) -> Unit = { _, _ -> },
    private val onStats: (AudioStats) -> Unit = {}
) {
    @Volatile private var running = false
    @Volatile private var muted = false
    @Volatile private var socket: DatagramSocket? = null
    @Volatile private var audioTrack: AudioTrack? = null
    private var thread: Thread? = null

    fun start(
        serverIp: String,
        audioPort: Int,
        sampleRate: Int,
        channels: Int,
        format: String,
        packetSamples: Int
    ) {
        if (running) return
        if (audioPort !in 1..65535 || sampleRate <= 0 || channels != 2 ||
            format != "pcm_s16le" || packetSamples <= 0
        ) {
            onState(AudioPlaybackState.ERROR, "Unsupported server audio format")
            return
        }

        val serverAddress = try {
            InetAddress.getByName(serverIp)
        } catch (e: Exception) {
            onState(AudioPlaybackState.ERROR, "Audio host could not be resolved")
            return
        }

        val sock = try {
            DatagramSocket(null).apply {
                reuseAddress = true
                bind(InetSocketAddress(0))
                try { receiveBufferSize = RECEIVE_BUFFER_BYTES } catch (_: Exception) { }
                soTimeout = SOCKET_TIMEOUT_MS
            }
        } catch (e: IOException) {
            onState(AudioPlaybackState.ERROR, e.message ?: "Audio socket could not be opened")
            return
        }

        running = true
        socket = sock
        onState(AudioPlaybackState.STARTING, "Starting PC audio…")
        thread = Thread(
            {
                receiveLoop(sock, serverAddress, audioPort, sampleRate, channels, packetSamples)
            },
            "AudioReceiver"
        ).apply { start() }
    }

    fun setMuted(isMuted: Boolean) {
        muted = isMuted
        try { audioTrack?.setVolume(if (isMuted) 0f else 1f) } catch (_: Exception) { }
    }

    fun stop() {
        running = false
        socket?.close()
        socket = null
        thread?.let { worker ->
            try {
                worker.join(750)
            } catch (_: InterruptedException) {
                Thread.currentThread().interrupt()
            }
        }
        thread = null
    }

    private fun receiveLoop(
        sock: DatagramSocket,
        serverAddress: InetAddress,
        audioPort: Int,
        sampleRate: Int,
        channels: Int,
        packetSamples: Int
    ) {
        var track: AudioTrack? = null
        try {
            val payloadBytes = packetSamples * channels * AudioPacketHeader.BYTES_PER_SAMPLE
            if (payloadBytes <= 0 || payloadBytes > MAX_PAYLOAD_BYTES) {
                throw IllegalArgumentException("Invalid audio packet size")
            }

            val playback = createAudioTrack(sampleRate, channels, payloadBytes)
            track = playback
            audioTrack = playback
            playback.setVolume(if (muted) 0f else 1f)
            playback.play()
            val outputBufferMs = playback.bufferSizeInFrames * 1000 / sampleRate
            onState(
                AudioPlaybackState.READY,
                "Audio ready · ${sampleRate / 1000} kHz stereo · ${outputBufferMs} ms output buffer"
            )

            val receiveBuffer = ByteArray(RECEIVE_PACKET_BYTES)
            val packet = DatagramPacket(receiveBuffer, receiveBuffer.size)
            val header = AudioPacketHeader()
            val silence = ByteArray(payloadBytes)
            val punchPacket = DatagramPacket(HOLE_PUNCH, HOLE_PUNCH.size, serverAddress, audioPort)

            var expectedSequence = -1L
            var receivedAny = false
            var reportedPlaying = false
            var lastHolePunchAt = 0L
            var lastStatsAt = SystemClock.elapsedRealtime()
            var intervalPackets = 0
            var intervalLost = 0L
            var intervalOutputDrops = 0
            var intervalBytes = 0L

            fun sendHolePunch(now: Long) {
                try {
                    sock.send(punchPacket)
                    lastHolePunchAt = now
                } catch (e: IOException) {
                    if (running) Log.w(TAG, "audio hole punch failed", e)
                }
            }

            fun flushStats(now: Long) {
                val intervalMs = now - lastStatsAt
                if (intervalMs < 1000) return
                val total = intervalPackets.toLong() + intervalLost
                val loss = if (total > 0) intervalLost * 100f / total else 0f
                val kbps = if (intervalMs > 0) (intervalBytes * 8 / intervalMs).toInt() else 0
                onStats(
                    AudioStats(
                        kbps = kbps,
                        packetLossPercent = loss,
                        packetsLost = intervalLost,
                        outputDrops = intervalOutputDrops,
                        underruns = playback.underrunCount,
                        receivingAudio = intervalPackets > 0,
                        outputBufferMs = outputBufferMs
                    )
                )
                intervalPackets = 0
                intervalLost = 0
                intervalOutputDrops = 0
                intervalBytes = 0
                lastStatsAt = now
            }

            sendHolePunch(SystemClock.elapsedRealtime())
            while (running) {
                packet.length = receiveBuffer.size
                try {
                    sock.receive(packet)
                } catch (_: SocketTimeoutException) {
                    val now = SystemClock.elapsedRealtime()
                    if (!receivedAny && now - lastHolePunchAt >= HOLE_PUNCH_INTERVAL_MS) {
                        sendHolePunch(now)
                    }
                    flushStats(now)
                    continue
                } catch (e: IOException) {
                    if (running) throw e
                    break
                }

                val now = SystemClock.elapsedRealtime()
                val length = packet.length
                if (!header.parse(receiveBuffer, length) ||
                    header.version != AudioPacketHeader.VERSION ||
                    header.format != AudioPacketHeader.FORMAT_PCM_S16LE ||
                    header.sampleCount != packetSamples ||
                    header.payloadLen != payloadBytes
                ) {
                    flushStats(now)
                    continue
                }

                val sequence = header.sequence
                if (expectedSequence >= 0) {
                    val forward = (sequence - expectedSequence) and UINT32_MASK
                    if (forward >= UINT32_HALF) {
                        // Duplicate, late, or reordered behind the play head.
                        flushStats(now)
                        continue
                    }
                    if (forward > 0) {
                        intervalLost += forward
                        if (forward <= MAX_SILENCE_PACKETS) {
                            repeat(forward.toInt()) {
                                val written = playback.write(
                                    silence, 0, silence.size, AudioTrack.WRITE_NON_BLOCKING
                                )
                                if (written != silence.size) intervalOutputDrops++
                            }
                        } else {
                            // A large discontinuity means queued audio is stale. Re-anchor to
                            // the newest packet instead of spending time filling the gap.
                            playback.pause()
                            playback.flush()
                            playback.play()
                            intervalOutputDrops++
                        }
                    }
                }
                expectedSequence = (sequence + 1) and UINT32_MASK

                receivedAny = true
                intervalPackets++
                intervalBytes += length
                val written = playback.write(
                    receiveBuffer,
                    AudioPacketHeader.HEADER_SIZE,
                    header.payloadLen,
                    AudioTrack.WRITE_NON_BLOCKING
                )
                if (written != header.payloadLen) intervalOutputDrops++

                if (!reportedPlaying) {
                    reportedPlaying = true
                    onState(AudioPlaybackState.PLAYING, "PC audio is live")
                }
                flushStats(now)
            }
        } catch (e: Exception) {
            if (running) {
                Log.e(TAG, "audio receiver failed", e)
                onState(AudioPlaybackState.ERROR, e.message ?: "Audio playback failed")
            }
        } finally {
            running = false
            socket = null
            audioTrack = null
            try { sock.close() } catch (_: Exception) { }
            try { track?.stop() } catch (_: Exception) { }
            try { track?.release() } catch (_: Exception) { }
        }
    }

    private fun createAudioTrack(sampleRate: Int, channels: Int, packetBytes: Int): AudioTrack {
        val channelMask = when (channels) {
            2 -> AudioFormat.CHANNEL_OUT_STEREO
            else -> throw IllegalArgumentException("Only stereo audio is supported")
        }
        val minBuffer = AudioTrack.getMinBufferSize(
            sampleRate,
            channelMask,
            AudioFormat.ENCODING_PCM_16BIT
        )
        if (minBuffer <= 0) throw IllegalStateException("Device rejected the audio format")

        val audioFormat = AudioFormat.Builder()
            .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
            .setSampleRate(sampleRate)
            .setChannelMask(channelMask)
            .build()
        val attributes = AudioAttributes.Builder()
            .setUsage(AudioAttributes.USAGE_GAME)
            .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
            .build()
        val track = AudioTrack.Builder()
            .setAudioAttributes(attributes)
            .setAudioFormat(audioFormat)
            .setTransferMode(AudioTrack.MODE_STREAM)
            .setBufferSizeInBytes(maxOf(minBuffer, packetBytes * TARGET_BUFFER_PACKETS))
            .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
            .build()
        if (track.state != AudioTrack.STATE_INITIALIZED) {
            track.release()
            throw IllegalStateException("Audio output could not be initialized")
        }
        return track
    }

    companion object {
        private const val TAG = "AudioReceiver"
        private val HOLE_PUNCH = byteArrayOf('D'.code.toByte(), 'S'.code.toByte(), 'A'.code.toByte(), 'H'.code.toByte())
        private const val SOCKET_TIMEOUT_MS = 500
        private const val HOLE_PUNCH_INTERVAL_MS = 1000L
        private const val RECEIVE_PACKET_BYTES = 1500
        private const val RECEIVE_BUFFER_BYTES = 64 * 1024
        private const val MAX_PAYLOAD_BYTES = 1200
        private const val TARGET_BUFFER_PACKETS = 4
        private const val MAX_SILENCE_PACKETS = 4L
        private const val UINT32_MASK = 0xFFFFFFFFL
        private const val UINT32_HALF = 0x80000000L
    }
}
