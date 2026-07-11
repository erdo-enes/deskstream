# DeskStream macOS client

Native Apple-silicon DeskStream client for macOS 13 and newer. It uses only system
frameworks and builds with Apple's Command Line Tools; full Xcode is not required for a
local ad-hoc-signed app.

## Features

- LAN discovery plus manual IPv4 connection, PIN pairing, Keychain token storage, TCP
  keepalive, and automatic reconnect.
- Four-frame H.264 UDP reordering/assembly state with XOR-FEC recovery and drop-until-IDR
  behavior; completed frames still leave immediately, so this is not a jitter buffer.
- Hardware H.264 presentation through `AVSampleBufferDisplayLayer`, marked for immediate
  display without a playback timeline. On macOS 14, conversion and renderer enqueue use a
  dedicated user-interactive queue; flush recovery preserves the last image and resumes only
  from a fresh IDR.
- Separate bounded 48 kHz stereo PCM playback with local mute.
- Direct mouse mode and explicit relative pointer capture; `Control-Option-Escape`, focus
  loss, sleep, disconnect, and stream stop all release captured input.
- Physical keyboard forwarding by USB HID position, preserving the Windows host's active
  layout and game scan-code behavior.
- Up to four GameController devices forwarded as virtual Xbox 360 controllers through the
  existing server/ViGEm path, including best-effort controller haptics.
- Native (20 Mbps ceiling) / 720p (10 Mbps ceiling) quality selection, local PNG screenshots,
  live stats, and full-screen controls.
  macOS 14.4+ copies the displayed pixel directly; older supported systems use a one-frame
  fallback decode that tears down immediately after the screenshot.

## Build and test

```bash
make test
make app
open build/DeskStream.app
```

The default target is a thin `arm64` binary with a macOS 13 deployment target. `make app`
ad-hoc signs the bundle for local testing. Public Gatekeeper distribution requires a
Developer ID Application certificate and Apple notarization; no such identity is stored in
this repository.

The stream window captures only events delivered to the app. It does not install a global
event tap and therefore does not require Accessibility/Input Monitoring permission. macOS
reserved shortcuts remain local. The Windows server must run at the same integrity level as
an elevated target game for injected mouse/keyboard input to reach it.

## Source layout

- `Core/` — binary protocol, FEC assembler, UDP/TCP transports, discovery, Keychain store.
- `App/` — AppKit UI/session lifecycle, immediate video, bounded audio, mouse/keyboard and
  GameController forwarding.
- `Tests/` — deterministic protocol/FEC tests plus real loopback UDP/TCP tests.
- `Resources/Info.plist` — app metadata and local-network usage description.
