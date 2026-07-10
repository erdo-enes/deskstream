# DeskStream

Low-latency Windows → Android screen streaming over LAN WiFi. No cloud, no account,
no virtual display drivers — the PC mirrors its screen to your phone/tablet at
1080p60 with a target glass-to-glass latency under 50 ms.

Built by studying what breaks in Sunshine/Moonlight, Parsec, spacedesk, Steam Link,
Miracast, and Chromecast, and designing those failure modes out. See
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the rationale and
[docs/PROTOCOL.md](docs/PROTOCOL.md) for the wire protocol.

## Layout

- `server/` — Windows server, C#/.NET 8. DXGI Desktop Duplication → GPU NV12 convert
  → Media Foundation hardware H.264 for NVIDIA/AMD/Intel → UDP with XOR FEC, plus WASAPI
  system-audio loopback. The experimental direct-NVENC path is opt-in.
- `android/` — Android client (Kotlin, minSdk 26). UDP receive + FEC → MediaCodec
  async low-latency decode → SurfaceView; PCM audio → low-latency AudioTrack. No network
  jitter buffer. Touch controls the PC mouse, while Bluetooth/USB gamepads forward as
  virtual Xbox 360 controllers on the PC.
- `docs/` — architecture and normative protocol spec.

## Quick start

**PC (Windows 10/11, a GPU with an H.264 hardware encoder):**
1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
2. `cd server && dotnet run -c Release`
3. Allow the firewall prompt (or pre-allow TCP 47801, UDP 47800/47802/47803 on private networks).
4. Optional for gamepad forwarding: install the official
   [ViGEmBus 1.22 driver](https://github.com/nefarius/ViGEmBus/releases/latest), then restart
   DeskStream. Video and audio work without it.

**Android (8.0+):**
1. Open `android/` in Android Studio, run on your device.
2. Make sure the phone is on the **same WiFi** as the PC (5 GHz strongly recommended).
3. Your PC appears in the list — tap it, type the 6-digit PIN shown in the PC console
   once. After that it auto-connects.

## Why it's fast

- Present-driven GPU capture; the frame never touches the CPU before the encoder.
- Hardware H.264 tuned for latency: CBR, no B-frames, ~1-frame VBV, IDR only on demand.
- Media Foundation hardware encoding is the stable default for NVIDIA, AMD, and Intel GPUs.
  The direct NVIDIA NVENC ultra-low-latency path is available only with
  `DESKSTREAM_ENCODER=nvenc` while it receives broader hardware validation.
- Custom UDP transport: ≤1200-byte packets, forward error correction instead of
  retransmission, stale frames dropped rather than queued.
- Android decodes straight to the screen surface in async low-latency mode; frames
  render the moment they decode — there is no buffer anywhere that can grow.
- PC system audio uses fixed 5 ms PCM blocks and non-blocking Android playback. Audio is
  negotiated separately, so it can fail or be muted without interrupting video.
- Physical Android controllers forward complete state snapshots at up to 120 Hz over UDP,
  appear to Windows games as Xbox 360 controllers, and receive rumble feedback.
- Android touch offers touchpad and direct-pointer modes. Motion/scroll use newest-wins UDP;
  ordered mouse buttons use the authenticated TCP control channel and are released on stop.
- Clock-synchronized p95 pipeline telemetry, a media heartbeat, UDP re-punching, bounded
  pools/queues, and automatic stream restart keep long sessions from accumulating delay.
- Closed-loop bitrate adaptation so WiFi congestion degrades quality, not latency.
