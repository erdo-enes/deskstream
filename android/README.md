# DeskStream — Android client

Kotlin client for the DeskStream low-latency LAN screen streamer. Implements the wire
contract in [`../docs/PROTOCOL.md`](../docs/PROTOCOL.md) (normative); design rationale in
[`../docs/ARCHITECTURE.md`](../docs/ARCHITECTURE.md).

## Building

Open this `android/` directory in Android Studio (Koala/2024.1 or newer recommended) and let
it sync. Requirements:

- AGP 8.5.2, Kotlin 2.0.21, Gradle 8.7 (wrapper included, `gradle-wrapper.jar` is committed;
  if it is ever missing, Android Studio regenerates it on sync, or run
  `gradle wrapper --gradle-version 8.7`)
- compileSdk/targetSdk 34, minSdk 26 (Android 8.0)
- JDK 17

Command line: `./gradlew assembleDebug` (or `assembleRelease` — the release build type is
deliberately signed with the debug key so it installs without a keystore).

`assembleRelease` is compile-verified with JDK 17 and Android SDK 34. Physical-device audio
and video latency still needs validation against a Windows host.

## App flow

1. **MainActivity** broadcasts the `DSPROBE1` discovery probe every second while visible
   (with a WiFi multicast lock held) and lists replying servers; manual IP entry is always
   available as fallback.
2. Tapping a server opens the TCP control channel and sends `HELLO` with the stored token
   (empty on first contact). `PAIR_REQUIRED` pops the PIN dialog ("Enter the PIN shown on
   your PC"); `PAIR_OK` persists the token; `HELLO_OK` launches the stream screen.
3. **StreamActivity** (fullscreen, landscape, keep-screen-on) sends `START_STREAM` once the
   surface exists, opens the UDP media socket on `STREAM_STARTED`, hole-punches with `DSMH`
   datagrams until video flows, and feeds reassembled H.264 access units to a MediaCodec
   async decoder rendering straight to the SurfaceView. It then negotiates system audio on
   a separate UDP socket, hole-punches with `DSAH`, and writes fixed 5 ms PCM blocks to a
   low-latency AudioTrack. Tap the LIVE status chip for detailed video/network/audio/latency
   diagnostics; the audio button locally mutes without stopping reception. Back sends
   `STOP_STREAM` and exits;
   backgrounding sends `STOP_STREAM` but keeps the control socket (protocol §5), then sends
   `START_STREAM` again on return.
4. A connected Bluetooth/USB gamepad is detected automatically. Buttons, D-pad, sticks,
   analog triggers, hot-plug events, and up to four controller slots are forwarded to the
   PC over UDP. The PC exposes Xbox 360 controllers and returns rumble to Android.
5. Touch input is negotiated after video starts. The top-right control switches between a
   relative **Touchpad** and absolute **Direct** pointer. Tap = left-click, hold + move =
   drag, two-finger move = scroll, and two-finger tap = right-click. Mouse buttons are reset
   whenever the stream stops or the app backgrounds.

## Code map

```
app/src/main/java/com/deskstream/client/
  ui/MainActivity.kt      discovery + connect + pairing UI
  ui/StreamActivity.kt    fullscreen video, aspect-ratio letterboxing, stats overlay
  ui/ServerAdapter.kt     discovered-server list
  net/DiscoveryClient.kt  UDP broadcast DSPROBE1 / DSREPLY parsing (§1)
  net/ControlClient.kt    process-wide control channel singleton: length-prefixed JSON,
                          PING keepalive, 6 s silence watchdog, reconnect backoff,
                          state machine per §5
  net/MediaReceiver.kt    dedicated UDP receive thread, 20-byte header parse (big-endian),
                          frame assembly per §3.1, XOR-FEC recovery per §3.2, DSMH hole
                          punch, 1 s STATS
  net/BufferPool.kt       size-bucketed byte[] pool for frame assembly buffers
  video/VideoDecoder.kt   MediaCodec video/avc async mode → Surface, KEY_LOW_LATENCY when
                          supported, immediate render, tiny bounded input queue with
                          drop + REQUEST_IDR + discard-until-keyframe on overflow/error
  audio/AudioReceiver.kt  DSAH audio UDP receiver, sequence-gap handling, immediate
                          non-blocking AudioTrack playback, local mute + audio stats
  input/GamepadForwarder.kt
                          physical controller detection/mapping, 120 Hz newest-state sender,
                          hot-plug neutralization and rumble
  input/RemoteMouseController.kt
                          touchpad/direct gestures, 120 Hz motion coalescing, safe reset
  proto/Messages.kt       control JSON models (org.json)
  proto/MediaPacket.kt    media header parser
  proto/AudioPacket.kt    allocation-free reusable audio-header parser
  data/Prefs.kt           clientId (UUID, generated once) + paired server {ip, token, name}
```

## Notes / first-run checklist

- The server must be running and reachable on UDP 47800 (discovery) and TCP 47801 (control);
  the video and audio UDP ports are negotiated and reported in `STREAM_STARTED` and
  `AUDIO_STARTED` (preferred ports 47802 and 47803).
- If discovery finds nothing (some APs filter broadcast even with the multicast lock), use
  manual IP entry — it is always shown.
- Decoder latency knobs applied: async mode, `KEY_LOW_LATENCY` (API 30+ where supported),
  `KEY_PRIORITY=0`, `KEY_OPERATING_RATE=240`, render-on-arrival (never paced by PTS).
- While foreground streaming, Android requests the Wi-Fi low-latency lock and a matching
  surface frame rate. Backgrounding stops video/audio/input but leaves control reconnectable.
- The client re-punches a silent media path, requests an IDR, and restarts a stream that does
  not recover. Buffer pools, frame assembly, codec input, and AudioTrack tuning are bounded.
- Pairing tokens are stored per server IP in SharedPreferences; a `PAIR_REQUIRED` in response
  to a non-empty token clears it and re-pairs automatically.
- Audio is optional and backward-compatible. If an older server ignores `AUDIO_START`, the
  stream screen reports that no audio reply arrived while video continues normally.
- Gamepad forwarding is also optional and requires ViGEmBus 1.22 on Windows. Connect the
  controller to Android before or during a stream; the bottom status chip reports detection,
  PC negotiation, driver errors, and live forwarding.
