# DeskStreamer.Server

The Windows half of **DeskStream**, a low-latency LAN screen streamer. It captures the
primary display with DXGI Desktop Duplication, converts BGRA→NV12 on the GPU, hardware-
encodes H.264 through native NVIDIA NVENC or a Media Foundation fallback, and streams per
[`../docs/PROTOCOL.md`](../docs/PROTOCOL.md). It also captures the default Windows playback
device through WASAPI loopback and streams normalized 48 kHz stereo PCM in fixed 5 ms blocks.

## Requirements

- Windows 10/11 (x64) with NVIDIA NVENC, AMD AMF, or Intel QuickSync hardware H.264.
  NVIDIA is attempted through the native NVENC API first; Media Foundation is the automatic
  fallback. There is **no CPU encoder** — the server exits clearly if both paths fail.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
- Optional for controller forwarding: the
  [ViGEmBus 1.22 driver](https://github.com/nefarius/ViGEmBus/releases/latest). Without it,
  the server reports controller support as unavailable while video/audio continue.

## Build & run

```powershell
cd server
dotnet run -c Release
```

On start it prints the local IP addresses and ports, then waits for a client. When a new
device pairs, a **6-digit PIN** is shown in a box — type it into the Android app. Once
streaming, a 1 Hz status line reports encoded fps, current bitrate, client-side dropped
frames, IDR requests per second, and the active audio payload rate.

Paired devices are remembered in `paired_clients.json` next to the built executable, so
subsequent connections auto-authenticate (TOFU). Delete that file to force re-pairing.

## Firewall

Allow these on the **Private** network profile (the first run usually triggers a Windows
Defender Firewall prompt — approve it for Private networks):

- **UDP 47800** — discovery
- **TCP 47801** — control
- **UDP 47802** — media (server → client; also receives the client's `DSMH` hole-punch)
- **UDP 47803** — audio (server → client; also receives the client's `DSAH` hole-punch)

```powershell
# Optional explicit rules (run in an elevated PowerShell):
New-NetFirewallRule -DisplayName "DeskStream discovery" -Direction Inbound -Protocol UDP -LocalPort 47800 -Profile Private -Action Allow
New-NetFirewallRule -DisplayName "DeskStream control"   -Direction Inbound -Protocol TCP -LocalPort 47801 -Profile Private -Action Allow
New-NetFirewallRule -DisplayName "DeskStream media"     -Direction Inbound -Protocol UDP -LocalPort 47802 -Profile Private -Action Allow
New-NetFirewallRule -DisplayName "DeskStream audio"     -Direction Inbound -Protocol UDP -LocalPort 47803 -Profile Private -Action Allow
```

## Architecture

See [`../docs/ARCHITECTURE.md`](../docs/ARCHITECTURE.md). Source layout:

| Path | Responsibility |
|------|----------------|
| `Program.cs` | Wire-up, console UX, GC latency mode, 1 Hz stats |
| `Capture/DesktopDuplicator.cs` | DXGI Output Duplication + shared D3D11 device |
| `Capture/Nv12Converter.cs` | GPU BGRA→NV12 via `ID3D11VideoProcessor` |
| `Encode/H264Encoder.cs` | Async hardware H.264 MFT (D3D-managed input, CODECAPI controls) |
| `Encode/NvencH264Encoder.cs` | Native NVENC ULL, single-pass CBR, zero reorder, one-frame VBV |
| `Encode/MfGuids.cs` / `Encode/NalUtil.cs` | MF/CODECAPI GUIDs; Annex-B NAL scanning |
| `Net/DiscoveryResponder.cs` | UDP 47800 `DSPROBE1` → `DSREPLY` |
| `Net/ControlServer.cs` | TCP 47801 length-prefixed JSON, keepalive, single client |
| `Net/MediaSender.cs` | Packetizer (20-byte header, ≤1200 B) + XOR FEC + UDP send |
| `Audio/SystemAudioCapture.cs` | Low-latency WASAPI system-output loopback, normalized PCM |
| `Net/AudioSender.cs` | `DSAH` address learning + fixed 5 ms audio packetizer/UDP send |
| `Input/VirtualGamepadManager.cs` | Up to four ViGEm-backed virtual Xbox 360 controllers |
| `Input/RemoteMouseManager.cs` | Authenticated `SendInput` mouse motion/buttons with safe reset |
| `Session/StreamSession.cs` | Control state machine + adaptation controller (§4) |
| `Session/PairingManager.cs` | TOFU PIN pairing, `paired_clients.json` persistence |
| `Protocol/*.cs` | Wire DTOs and big-endian media header helpers |

## Notes

- The hot path (packetize → send) is allocation-free; GC runs in `SustainedLowLatency`.
- Every pipeline stage holds at most one frame; a stale capture is dropped when the encoder
  is busy (newest-wins), never queued.
- `EnableWindowsTargeting` is set so the project compiles on non-Windows CI, but it only
  **runs** on Windows.
- Windows UIPI blocks a normal process from injecting into an elevated game. Run the server
  at the same integrity level as the target app when remote mouse input is needed there.
