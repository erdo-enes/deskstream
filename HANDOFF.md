# DeskStream — Agent Handoff Document

Purpose: this file lets another AI agent (or developer) continue exactly where the
previous agent left off. It records the current state, what is intentionally NOT
implemented yet, and where the extension points are. Read `docs/ARCHITECTURE.md` and
`docs/PROTOCOL.md` (normative wire spec) before changing anything.

## Project summary

Low-latency Windows → Android screen streamer over LAN WiFi. No cloud, no accounts.
Target: <50 ms glass-to-glass at 1080p60.

- `server/` — C#/.NET 8 (`net8.0-windows`) console app. DXGI Desktop Duplication →
  GPU BGRA→NV12 (D3D11 VideoProcessor) → Media Foundation hardware H.264 async MFT
  (low-latency CBR, no B-frames, ~infinite GOP, IDR on demand) → custom UDP packetizer
  (≤1200 B chunks, XOR FEC groups of 8) + WASAPI system-audio loopback → dedicated
  5 ms PCM UDP packets + TCP control channel + UDP discovery responder.
- `android/` — Kotlin app (minSdk 26, AGP 8.5.2, Kotlin 2.0.21). UDP broadcast
  discovery → TCP control client (PIN pairing, reconnect state machine) → UDP media
  receiver (FEC recovery, frame assembly, drop-until-keyframe) → MediaCodec async
  H.264 decode straight to SurfaceView (`KEY_LOW_LATENCY`, no jitter buffer) + separate
  PCM receiver feeding a low-latency `AudioTrack` + up to four physical Android gamepads
  forwarded as virtual Xbox 360 controllers with rumble.

## Current state (v0.2.0, released)

- GitHub: https://github.com/erdo-enes/deskstream (public). Tags `v0.1.0` and `v0.2.0`;
  releases contain `DeskStream-Server-win-x64.zip` (self-contained exe) and
  `DeskStream-Client.apk` (debug-signed installable release build).
- **Both sides compile clean with audio and gamepad support.** Server:
  `dotnet build -c Release` zero errors/warnings using .NET 8. Client:
  `gradlew assembleRelease` succeeds using JDK 17.
- **NOT yet run on real hardware.** Everything was developed and compile-verified on
  macOS. No end-to-end test on a physical Windows PC + Android device has happened yet.
  The riskiest first-run areas (in order):
  1. MF async-MFT event loop and D3D device-manager binding on real GPUs
     (`server/Encode/H264Encoder.cs`) — MFT quirks differ per vendor.
  2. VideoProcessor color range/space left at driver default
     (`server/Capture/Nv12Converter.cs`) — colors may look washed out; fix is setting
     stream/output color spaces explicitly.
  3. OEM Android decoders that dislike in-band-SPS/PPS startup without explicit CSD
     (`android/.../video/VideoDecoder.kt`) — symptom: black screen while
     `queueInputBuffer` succeeds.
  4. GPU texture handoff has no explicit fence (tiny tearing risk under extreme rates;
     3-texture pool mitigates).
  5. WASAPI loopback conversion and 20 ms capture-buffer behavior across real playback
     devices (`server/Audio/SystemAudioCapture.cs`).
  6. OEM Android low-latency `AudioTrack` buffer sizing. The diagnostics overlay reports
     actual output-buffer size, non-blocking output drops, and underruns.
  7. ViGEmBus virtual-controller behavior and controller mappings on real Windows/Android
     hardware. ViGEmBus 1.22 must be installed separately on Windows; video/audio remain
     usable if it is absent.

## Feature status

| Feature | Status |
|---|---|
| Screen capture, H.264 encode, UDP stream, decode/render | ✅ implemented |
| Discovery (UDP broadcast probe/reply) + manual IP fallback | ✅ implemented |
| TOFU pairing with 6-digit PIN, persisted tokens | ✅ implemented |
| Closed-loop bitrate adaptation (§4 of protocol) | ✅ implemented (bitrate only) |
| Reconnect state machine (background/foreground, socket death) | ✅ implemented |
| Stats overlay on client, 1 Hz stats line on server | ✅ implemented |
| **Input / remote control (touch → PC mouse/keyboard)** | ❌ NOT implemented |
| **Game controller / gamepad forwarding** | ✅ implemented (1–4 physical Android pads → Xbox 360 + rumble) |
| **Audio streaming** | ✅ implemented, compile-verified; hardware test pending |
| Encryption (TLS control / AES-GCM media) | ❌ NOT implemented (LAN plaintext) |
| mDNS advertisement, QR pairing | ❌ NOT implemented |
| Framerate/resolution adaptation steps | ❌ NOT implemented (bitrate ladder only) |
| HEVC/AV1, HDR, multi-monitor, multi-client, WAN | ❌ deliberate v1 non-goals |

## Remaining input work: touch/mouse/keyboard

Physical Android controllers can now control Windows games through virtual Xbox 360
controllers. Touch-as-mouse, on-screen virtual controls, and keyboard forwarding are not
implemented yet.

The protocol already reserves the hook: PROTOCOL.md §2.3 reserves the `INPUT` control
message type (both sides must ignore unknown types, so shipping input is
backward-compatible). Design guidance for whoever implements it:

1. **Transport:** input events client → server. Low-rate events (keys, buttons) can ride
   the existing TCP control channel; high-rate events (touch moves, analog sticks,
   mouse deltas) should NOT — TCP head-of-line blocking will add lag. Either add a
   client→server UDP path on the existing media socket (it's already bidirectional —
   the `DSMH` hole-punch proves the path) or accept TCP for v1 and measure.
2. **Server-side injection:** use Windows `SendInput` (user32) for mouse/keyboard.
   Absolute mouse positioning must map client-view coordinates → virtual-desktop
   coordinates (mind letterboxing: the client already knows the video aspect rect in
   `StreamActivity`). Gamepads already use the separate `VirtualGamepadManager` path.
3. **Client-side:** touch → pointer events from `StreamActivity`'s SurfaceView (map
   through the letterbox rect). Physical `InputDevice`/`MotionEvent` gamepads are done.
4. **Extend PROTOCOL.md first** — it is the normative contract. Define `INPUT` message
   schema(s) there before writing code, keep both implementations in lockstep, and bump
   nothing: unknown-type tolerance means version stays 1.

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
- Latency invariants are non-negotiable: single-frame pipeline (every stage holds at
  most one frame), never retransmit video, no jitter buffer, drop stale frames, UDP
  payloads ≤1200 B, PTS never gates rendering.
- Hot paths must stay allocation-free (server packetizer/encoder callback; client
  receive/assembly loop uses pooled buffers).
- v2 roadmap ideas already documented in ARCHITECTURE.md: direct NVENC + intra-refresh +
  reference-frame invalidation, Reed-Solomon FEC, TLS/AES-GCM, mDNS, QR pairing, audio
  compression with Opus, framerate/resolution adaptation steps.
