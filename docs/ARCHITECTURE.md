# DeskStream — Architecture

A Windows → Android/macOS LAN screen streamer engineered for minimal glass-to-glass latency
(target: <50 ms median at 1080p60 on 5 GHz WiFi), designed around the documented failure
modes of Sunshine/Moonlight, Parsec, spacedesk, Steam Link, Miracast, and Chromecast.

## Design principles (what we fix)

| Existing pain | Our decision |
|---|---|
| Parsec needs an account + cloud even on LAN | **Zero cloud, zero account, fully offline.** LAN only. |
| Sunshine/Moonlight pairing hell (PIN desync, cert files) | **TOFU pairing**: server displays a 6-digit PIN once, client enters it, keys pinned. Auto-connect afterwards. |
| spacedesk resolution/ghost-display bugs | **Mirror the primary display only.** No virtual display driver, ever (v1). |
| NVIDIA-only encoder coupling (GameStream/Sunshine quirks) | **Vendor-neutral stable default** — Media Foundation hardware encode for NVENC/AMF/QuickSync; direct NVENC remains an explicit experimental opt-in. |
| Steam Link/Miracast stutter-then-collapse | **Closed-loop adaptation**: client stats drive a bitrate-first ladder; frames are dropped, never queued. |
| Sessions die on sleep/background | **Session tokens independent of sockets** + reconnect state machine. |

## Latency pipeline (both research tracks converged on this)

```
DXGI Desktop Duplication (present-driven, GPU texture)     1–3 ms
  → GPU BGRA→NV12 convert (D3D11 video processor)
  → Media Foundation hardware H.264 (or opt-in NVENC ULL)  3–8 ms
    (CBR, no B-frames/lookahead, IDR on demand,
    zero reorder delay, VBV ≈ 1 frame)
  → Packetize ≤1200 B + XOR FEC → UDP                      <1 ms
  → WiFi (5 GHz)                                            1–5 ms
  → Kotlin UDP receive + FEC recover + frame assemble      1–2 ms
  → MediaCodec async (KEY_LOW_LATENCY) → SurfaceView       3–8 ms
  → panel scan-out                                          8–16 ms
                                          glass-to-glass ~25–45 ms

WASAPI system-output loopback → shared-mode 48 kHz PCM16
  → fixed 5 ms UDP blocks → Android low-latency AudioTrack
  → non-blocking playback (no network jitter queue)        ~20–40 ms
```

Rules that keep it low:
- **Every stage is tightly bounded.** At most one decoder frame is submitted and one is
  pending; media assembly holds at most two frames so limited UDP reordering can heal.
- **No jitter buffer on the client.** Render as decoded; PTS is for stats only.
- **Never retransmit video.** FEC recovers isolated loss; unrecoverable frame → drop it
  and send `REQUEST_IDR` on the control channel.
- **UDP payloads ≤ 1200 bytes** — no IP fragmentation.

## Components

- `server/` — C#/.NET 8 (`net8.0-windows`) console app. Vortice.Windows for
  D3D11/DXGI/Media Foundation interop. Hot path is allocation-free per frame
  (pooled buffers, GC SustainedLowLatency).
- `android/` — Kotlin app. UDP receiver + FEC + assembler feed MediaCodec in async
  mode rendering directly to a SurfaceView. `KEY_LOW_LATENCY` when supported. A separate
  receiver feeds 5 ms PCM blocks to a low-latency `AudioTrack` with local mute. Physical
  Android gamepads are reduced to newest-state snapshots and forwarded at up to 120 Hz.
  Touch becomes either relative touchpad motion or direct absolute primary-display motion.
- `macos/` — native Objective-C/AppKit Apple-silicon app (macOS 13+). Its bounded two-frame
  FEC assembler feeds H.264 access units directly to AVSampleBufferDisplayLayer with
  display-immediately semantics; separate bounded PCM output and foreground AppKit/
  GameController capture forward mouse, physical keyboard, and up to four gamepads.
- `docs/PROTOCOL.md` — the wire contract both sides implement. **Normative.**

## Networking model

- **Discovery** — UDP broadcast probe/response on port 47800 (works where mDNS
  multicast is filtered), plus manual IP entry as a never-dead-end fallback.
- **Control** — TCP port 47801, length-prefixed JSON. Pairing, session start/stop,
  IDR requests, stats, keepalive.
- **Video** — UDP (server → client, port negotiated over control). 20-byte binary
  header + H.264 Annex-B payload, per-frame XOR FEC groups.
- **Audio** — separate negotiated UDP port. Opt-in `AUDIO_START` negotiation, `DSAH`
  address learning, and 5 ms 48 kHz stereo PCM16 datagrams. It is separate so audio can
  never block video assembly and old clients remain compatible.
- **Gamepad** — client→server state snapshots share the video UDP socket after optional
  control-channel negotiation. Windows exposes up to four Xbox 360 controllers through
  ViGEmBus; rumble returns over the control channel.
- **Mouse** — newest-wins motion/scroll datagrams share the authenticated media endpoint;
  ordered button transitions stay on TCP. Windows `SendInput` injects into the interactive
  desktop and every stop/reset path releases held buttons.
- **Keyboard** — ordered USB HID keyboard-page positions use TCP and become Windows Set-1
  scan codes. Resets on focus loss, capture release, stream stop, or disconnect prevent
  retained keys; unsupported usages are ignored for backward compatibility.

## Adaptation (bitrate → fps → resolution)

Client reports stats every second (received/dropped frames, bytes, IDR requests, and
clock-synchronized capture→receive plus decode→surface p95 latency). Server controller:
on loss, rising latency, or IDR-request pressure, cut bitrate 30% immediately (floor
2 Mbps); on 5 s of clean stats, ramp up 10% (slow-start, ceiling = configured max).
Framerate/resolution steps are v1.1 — the control messages already carry the fields.

## Deliberate non-goals (v1)

No WAN/relay/NAT traversal. No accounts. No virtual/extended display. No multi-client,
no multi-monitor, and no iOS client. No HEVC/AV1 (multiplies encoder quirk surface;
H.264 has the widest low-latency hardware decoder support). Physical gamepads, mouse, and
foreground keyboard forwarding are supported.

## v2 upgrade path (documented, not built)

Reference-frame invalidation/intra-refresh can replace some IDR-on-loss recovery; Reed-Solomon
FEC replaces XOR parity; TLS on control + AES-GCM on media;
mDNS advertisement alongside broadcast discovery; QR pairing. Opus can replace PCM audio
when conserving ~1.5 Mbps matters more than codec-free startup latency.
