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
  → Media Foundation hardware H.264 (NVENC/AMF/QuickSync, low-latency CBR) → UDP with
  XOR FEC.
- `android/` — Android client (Kotlin, minSdk 26). UDP receive + FEC → MediaCodec
  async low-latency decode → SurfaceView. No jitter buffer.
- `docs/` — architecture and normative protocol spec.

## Quick start

**PC (Windows 10/11, a GPU with an H.264 hardware encoder):**
1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
2. `cd server && dotnet run -c Release`
3. Allow the firewall prompt (or pre-allow TCP 47801, UDP 47800/47802 on private networks).

**Android (8.0+):**
1. Open `android/` in Android Studio, run on your device.
2. Make sure the phone is on the **same WiFi** as the PC (5 GHz strongly recommended).
3. Your PC appears in the list — tap it, type the 6-digit PIN shown in the PC console
   once. After that it auto-connects.

## Why it's fast

- Present-driven GPU capture; the frame never touches the CPU before the encoder.
- Hardware H.264 tuned for latency: CBR, no B-frames, ~1-frame VBV, IDR only on demand.
- Custom UDP transport: ≤1200-byte packets, forward error correction instead of
  retransmission, stale frames dropped rather than queued.
- Android decodes straight to the screen surface in async low-latency mode; frames
  render the moment they decode — there is no buffer anywhere that can grow.
- Closed-loop bitrate adaptation so WiFi congestion degrades quality, not latency.
