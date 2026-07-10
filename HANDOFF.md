# DeskStream — Agent Handoff Document

Purpose: this file lets another AI agent (or developer) continue exactly where the
previous agent left off. It records the current state, what is intentionally NOT
implemented yet, and where the extension points are. Read `docs/ARCHITECTURE.md` and
`docs/PROTOCOL.md` (normative wire spec) before changing anything.

## Project summary

Low-latency Windows → Android screen streamer over LAN WiFi. No cloud, no accounts.
Target: <50 ms glass-to-glass at 1080p60.

- `server/` — C#/.NET 8 (`net8.0-windows`) console app. DXGI Desktop Duplication →
  GPU BGRA→NV12 (D3D11 VideoProcessor) → Media Foundation hardware H.264 by default
  (native NVIDIA NVENC ULL is opt-in; CBR, no B-frames, one-frame VBV, IDR on demand) → UDP packetizer
  (≤1200 B chunks, XOR FEC groups of 8) + WASAPI system-audio loopback → dedicated
  5 ms PCM UDP packets + TCP control channel + UDP discovery responder.
- `android/` — Kotlin app (minSdk 26, AGP 8.5.2, Kotlin 2.0.21). UDP broadcast
  discovery → TCP control client (PIN pairing, reconnect state machine) → UDP media
  receiver (FEC recovery, frame assembly, drop-until-keyframe) → MediaCodec async
  H.264 decode straight to SurfaceView (`KEY_LOW_LATENCY`, no jitter buffer) + separate
  PCM receiver feeding an adaptively-sized low-latency `AudioTrack` + up to four physical
  Android gamepads forwarded as virtual Xbox 360 controllers with rumble + touchpad/direct
  mouse input, long-session recovery, and p95 latency telemetry.

## Current state (v0.3.6 released)

- GitHub: https://github.com/erdo-enes/deskstream (public). `v0.3.6` is the latest published
  release (tag `v0.3.6`, APK + win-x64 server zip attached). It fixes the real-device crash
  on first mouse motion: Android had called
  `DatagramSocket.send()` directly from the UI-thread touch callback. Mouse and gamepad UDP
  now pass through one bounded IO sender. It also makes single-tap movement-only, double-tap
  left-click, double-tap-drag intentional drag, and requires Back twice to exit the stream.
- **Both sides compile clean with audio, gamepad, mouse, telemetry, and recovery support.**
  Server `dotnet build -c Release --no-restore`: zero errors/warnings. Android
  `assembleRelease lintRelease`: successful with JDK 17. The verified v0.3.6 artifacts are
  attached to the GitHub release.
- **Real hardware testing is in progress** on Windows plus a Samsung SM-S911B. The supplied
  server logs proved the UDP endpoint and media sender were active, then the TCP control
  socket vanished as soon as touch input began. Static analysis found the matching uncaught
  Android `NetworkOnMainThreadException` path. The remaining risky areas are:
  1. Native NVENC D3D11 texture registration/mapping and dynamic bitrate reconfiguration on
     NVIDIA hardware (`server/Encode/NvencH264Encoder.cs`). It is now explicit opt-in via
     `DESKSTREAM_ENCODER=nvenc` rather than the default path.
  2. MF async-MFT event loop and D3D device-manager binding on real GPUs
     (`server/Encode/H264Encoder.cs`) — MFT quirks differ per vendor.
  3. VideoProcessor color range/space left at driver default
     (`server/Capture/Nv12Converter.cs`) — colors may look washed out; fix is setting
     stream/output color spaces explicitly.
  4. OEM Android decoders that dislike in-band-SPS/PPS startup without explicit CSD
     (`android/.../video/VideoDecoder.kt`) — symptom: black screen while
     `queueInputBuffer` succeeds.
  5. GPU texture handoff has no explicit fence (tiny tearing risk under extreme rates;
     3-texture pool mitigates).
  6. WASAPI event-driven loopback conversion across real playback
     devices (`server/Audio/SystemAudioCapture.cs`).
  7. OEM Android low-latency `AudioTrack` buffer sizing. The diagnostics overlay reports
     actual output-buffer size, non-blocking output drops, and underruns.
  8. ViGEmBus virtual-controller behavior and controller mappings on real Windows/Android
     hardware. ViGEmBus 1.22 must be installed separately on Windows; video/audio remain
     usable if it is absent.
  9. `SendInput` mouse behavior in games, Windows scaling/multi-monitor setups, and UIPI.
     Absolute input intentionally maps the captured primary display only; elevated games
     require the server to run at the same integrity level.

## Feature status

| Feature | Status |
|---|---|
| Screen capture, H.264 encode, UDP stream, decode/render | ✅ implemented |
| Discovery (UDP broadcast probe/reply) + manual IP fallback | ✅ implemented |
| TOFU pairing with 6-digit PIN, persisted tokens | ✅ implemented |
| Closed-loop bitrate adaptation (§4 of protocol) | ✅ implemented (bitrate only) |
| Reconnect state machine (background/foreground, socket death) | ✅ implemented |
| Stats overlay on client, 1 Hz stats line on server | ✅ implemented |
| **Touch → PC mouse** | ✅ movement-only tap, double-tap click/drag, right-click/scroll, safe reset |
| **Keyboard forwarding** | ❌ NOT implemented |
| **Game controller / gamepad forwarding** | ✅ implemented (1–4 physical Android pads → Xbox 360 + rumble) |
| **Audio streaming** | ✅ implemented, compile-verified; hardware test pending |
| Encryption (TLS control / AES-GCM media) | ❌ NOT implemented (LAN plaintext) |
| mDNS advertisement, QR pairing | ❌ NOT implemented |
| Framerate/resolution adaptation steps | ❌ NOT implemented (bitrate ladder only) |
| HEVC/AV1, HDR, multi-monitor, multi-client, WAN | ❌ deliberate v1 non-goals |

## Mouse/input implementation added for v0.3.0 and hardened for v0.3.6

- Android `RemoteMouseController` offers relative touchpad and absolute direct modes.
  A single finger moves without clicking, double-tap clicks, double-tap-drag drags, and
  two-finger gestures cover right-click and wheel scrolling; motion is coalesced to 120 Hz
  and uses unbuffered dispatch. The first Android Back/edge-Back only asks for confirmation.
- High-rate `DSMI` motion and `DSMC` authoritative cursor feedback share the learned media
  UDP endpoint. A bounded IO sender prevents touch callbacks from doing network operations
  on Android's main thread; ordered button transitions use a FIFO TCP writer. The server
  ignores UDP input until the authenticated session negotiates `INPUT_STARTED`.
- Windows `RemoteMouseManager` injects through `SendInput`, rejects stale sequences, tracks
  held buttons, and releases everything on reset, input stop, stream stop, or disconnect.
- Keyboard forwarding and on-screen virtual gamepad controls remain unimplemented.

## Latency and long-session work added for v0.3.0

- Direct NVENC uses ULL tuning, P3, single-pass CBR, no B-frames/lookahead, zero reorder,
  repeated SPS/PPS, and one-frame VBV. It is opt-in; `EncoderFactory` selects the established
  Media Foundation hardware MFT by default.
- Android requests Wi-Fi low-latency mode and display frame-rate matching while foreground;
  receive/assembly/decoder/buffer-pool queues are smaller and hard bounded.
- NTP-like `PING`/`PONG` timestamps plus stream clock origin produce capture→receive p95;
  MediaCodec frame callbacks produce decode→surface p95. Rising latency triggers bitrate
  reduction before a drop-only controller would react.
- Static-desktop media heartbeats, UDP re-punch + IDR, stream restart, server encoder-stall
  detection, clock reset after reconnect, and ordered background stop improve long sessions.
- WASAPI is event-driven; Android AudioTrack begins around 10 ms, targets around 15 ms,
  grows after underruns, and shrinks after clean playback.

## Audio implementation added after v0.1.0

- Audio is opt-in and backward-compatible: after `STREAM_STARTED`, Android sends
  `AUDIO_START`. A new server replies `AUDIO_STARTED`; an old server ignores it and video
  continues. A new server never opens WASAPI for an old client that does not ask.
- `SystemAudioCapture` uses NAudio 2.3.0/WASAPI loopback on the default playback device and
  asks Windows shared mode to normalize the mix to 48 kHz stereo signed PCM16.
- `AudioSender` uses a separate negotiated UDP socket (preferred 47803), learns the client
  endpoint from `DSAH`, and emits fixed 5 ms/960-byte blocks. There is no retransmission,
  FEC, or queue beyond one partial block.
- Android `AudioReceiver` writes packets immediately to a low-latency `AudioTrack` with
  non-blocking calls. Small sequence gaps become silence; large gaps flush stale playback.
  Local mute keeps reception drained for instant unmute.
- `StreamActivity` now shows connection stages, resolution/codec/bitrate, video loss, audio
  negotiation/source state, audio loss, output drops/underruns, and a mute control.
- PCM costs 1,536 kbps while sound is active. Opus remains a future bandwidth optimization;
  PCM was selected for codec-free startup and predictable 5 ms packetization.

## Gamepad implementation added for v0.2.0

- Android `GamepadForwarder` discovers up to four `SOURCE_GAMEPAD`/`SOURCE_JOYSTICK`
  devices, maps standard XInput buttons, D-pad/hat, sticks, and analog triggers, and emits
  complete 24-byte state snapshots at no more than 120 Hz with a 250 ms heartbeat.
- Gamepad snapshots travel client→server over the existing video UDP socket. Only the IP
  from the authenticated TCP control connection can claim the media/audio hole punches,
  and gamepad packets must match the current `DSMH` endpoint.
- `VirtualGamepadManager` uses `Nefarius.ViGEm.Client` 1.21.256 to expose up to four Xbox
  360 controllers. ViGEmBus 1.22 is an optional separate Windows driver requirement;
  missing/incompatible-driver errors are reported to Android without stopping streaming.
- Controller removal, stream stop, and disconnect all submit a neutral state before the
  virtual device is unplugged, preventing stuck buttons or axes.
- Xbox rumble motor values return over the TCP control channel and drive the physical
  Android controller vibrator when the device exposes one.

## Build / verify commands

- Server (any OS): `cd server && dotnet build -c Release`
  Publish exe: `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish`
  On this Mac, .NET 8 is keg-only at `/opt/homebrew/opt/dotnet@8/bin/dotnet`; add that
  directory to `PATH` or invoke the full path.
- Android: `cd android && ./gradlew assembleRelease` (needs Android SDK 34; on this Mac:
  SDK at `~/Library/Android/sdk`, JDK 17 at `/opt/homebrew/opt/openjdk@17`,
  `local.properties` already points at the SDK). Release build is debug-signed on purpose.
- Run on Windows: `DeskStreamer.Server.exe` prints IPs/ports/PIN; firewall must allow
  TCP 47801, UDP 47800/47802/47803 (private profile).

## Ground rules for continuing agents

- `docs/PROTOCOL.md` is normative. Any wire change goes there first; server and client
  must be updated together. Unknown JSON message types are ignored by both sides.
- Latency invariants are non-negotiable: tightly bounded one-in-flight/one-pending video
  pipeline, never retransmit video, no jitter buffer, drop stale frames, UDP payloads
  ≤1200 B, PTS never gates rendering.
- Hot paths must stay allocation-free (server packetizer/encoder callback; client
  receive/assembly loop uses pooled buffers).
- v2 roadmap ideas already documented in ARCHITECTURE.md: intra-refresh/reference-frame
  invalidation, Reed-Solomon FEC, TLS/AES-GCM, mDNS, QR pairing, audio
  compression with Opus, framerate/resolution adaptation steps.
