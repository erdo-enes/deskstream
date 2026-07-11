# DeskStream

Low-latency Windows → Android and Apple-silicon macOS screen streaming over LAN WiFi.
No cloud, no account, no virtual display drivers — the PC mirrors its screen to your
phone, tablet, or Mac at 1080p60 with a target glass-to-glass latency under 50 ms.

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
- `macos/` — native AppKit client for Apple silicon (macOS 13+). Hardware H.264 display,
  bounded PCM playback, Keychain pairing, LAN discovery, reconnect/stall recovery, and
  foreground mouse, full physical-keyboard, and up-to-four-gamepad forwarding with rumble.
- `switch/` — Nintendo Switch homebrew client (C++, SDL2, libnx). UDP receive + FEC -> FFmpeg low-latency H.264 software decode -> SDL2 YUV Texture; PCM audio -> low-latency SDL2 Queued Audio; Joy-Con / Pro Controllers are mapped and forwarded as virtual Xbox 360 controllers.
- `docs/` — architecture and normative protocol spec.

## Quick start

**PC (Windows 10/11, a GPU with an H.264 hardware encoder):**
1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
2. `cd server && dotnet run -c Release`
3. Allow the firewall prompt (or pre-allow TCP 47801, UDP 47800/47802/47803 on private networks).
4. Optional for gamepad forwarding: install the official
   [ViGEmBus 1.22 driver](https://github.com/nefarius/ViGEmBus/releases/latest), then restart
   DeskStream. Video and audio work without it.
5. A web dashboard runs at `http://127.0.0.1:47810/` — pairing PIN, live stats, stream
   restart, and the default quality (Native / 720p). `--web-lan` exposes it to the LAN
   (unauthenticated — it shows the PIN), `--no-web` disables it.
6. To run DeskStream automatically like a service: `DeskStreamer.Server.exe --install-autostart`
   registers a logon Scheduled Task that starts the server headless in your session
   (console output, including the PIN, goes to `deskstream.log`, while application diagnostics go to `deskstream.app.log`; manage it from the web
   dashboard). `--uninstall-autostart` removes it. A true session-0 Windows service cannot
   capture the desktop, so this is deliberately a user-session autostart.
7. Optional: cap the encoder target with `--max-bitrate-kbps 12000` on congested WiFi
   (default 20000; minimum 2000). FEC, packet headers, and PCM audio add wire overhead.

**Android (8.0+):**
1. Open `android/` in Android Studio, run on your device.
2. Make sure the phone is on the **same WiFi** as the PC (5 GHz strongly recommended).
3. Your PC appears in the list — tap it, type the 6-digit PIN shown in the PC console
   once. After that it auto-connects.
4. In a stream, **Hide** removes every overlay. Hold three fingers for 600 ms, press Back,
   or press F11 on a hardware keyboard to restore controls without clicking the host.

**macOS (13+, Apple silicon):**
1. `cd macos && make app` using Apple's Command Line Tools.
2. Open `build/DeskStream.app`; select the discovered PC or enter its LAN address.
3. Pair once with the server PIN. Click the video for direct pointer control, or choose
   **Capture Input** for relative game input. `Control-Option-Escape` releases capture.

**Nintendo Switch (Homebrew-enabled console):**
1. Make sure you have the official `devkitPro` toolchain and the `switch-dev` packages installed.
2. `cd switch && make` to compile the app.
3. Copy `DeskStream.nro` to the `/switch/DeskStream/` folder on your SD card.
4. Launch the Switch homebrew menu, start the DeskStream client, and select/pair your server.


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
- The native macOS client displays H.264 immediately through AVFoundation's hardware path,
  forwards window-focused USB-HID keyboard positions as Windows scan codes, and reuses the
  same authenticated mouse/gamepad transports without system-wide input permissions.
- Clock-synchronized p95 pipeline telemetry, a media heartbeat, UDP re-punching, bounded
  pools/queues, and automatic stream restart keep long sessions from accumulating delay.
- Closed-loop bitrate adaptation so WiFi congestion degrades quality, not latency.
- Optional 720p60 mode downscales on the server GPU before encoding and uses a 10 Mbps client
  ceiling — fewer pixels and packets to encode, send, and decode on constrained networks/devices.
