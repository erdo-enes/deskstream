# DeskStream Protocol v1 — NORMATIVE

Both the Windows server and the Android client implement exactly this. All multi-byte
integers are **big-endian**. All JSON is UTF-8. Protocol version is `1`.

Default ports: discovery **UDP 47800**, control **TCP 47801**, video **UDP negotiated**
(server picks, typically 47802, and reports it in `STREAM_STARTED`), audio **UDP
negotiated** (server picks, typically 47803, and reports it in `AUDIO_STARTED`).

---

## 1. Discovery (UDP 47800)

Client sends a broadcast datagram to `255.255.255.255:47800` (and per-interface subnet
broadcast) containing exactly the 8 ASCII bytes:

```
DSPROBE1
```

Server replies **unicast** to the sender with a single JSON datagram:

```json
{"type":"DSREPLY","ver":1,"name":"<hostname>","controlPort":47801}
```

Client retries the probe every 1 s while the discovery screen is open. Manual IP entry
must always be available as fallback.

## 2. Control channel (TCP 47801)

Framing: each message is `uint32 length` + `length` bytes of UTF-8 JSON. Max length
65536. Every message has a `"type"` field. Unknown message types MUST be ignored
(forward compatibility). Either side closes the socket on a malformed frame.

Keepalive: client sends `PING` every 2 s; server answers `PONG`. Either side treats
6 s of silence as a dead connection. Client then auto-reconnects (same token) with
exponential backoff (0.5 s, 1 s, 2 s, capped 5 s).

Latency-capable clients include their monotonic timestamp in microseconds:

```json
{"type":"PING","t0Us":1234567890}
{"type":"PONG","t0Us":1234567890,"t1Us":2234567000,"t2Us":2234567050}
```

`t1Us` and `t2Us` are the server receive/reply monotonic times. The client samples its
own `t3Us` on receipt and uses the lowest-RTT sample to estimate the server/client clock
offset. Old peers may continue sending fieldless `PING`/`PONG` messages.

### 2.1 Session establishment

Client connects and sends first:

```json
{"type":"HELLO","ver":1,"clientId":"<random-uuid-stable-per-install>","clientName":"<model>","token":"<paired-token-or-empty>"}
```

Server replies one of:

```json
{"type":"HELLO_OK","serverName":"<hostname>","width":1920,"height":1080}
{"type":"PAIR_REQUIRED"}
{"type":"ERROR","code":"BAD_VERSION","message":"..."}
```

`width`/`height` are the current primary-display dimensions.

### 2.2 Pairing (TOFU + PIN)

On `PAIR_REQUIRED`, client sends `{"type":"PAIR_REQUEST"}`. The server generates a
random 6-digit PIN, **displays it on the server console/UI**, and waits (60 s timeout).
The user reads it off the PC and types it into the Android app. Client sends:

```json
{"type":"PAIR_CODE","pin":"123456"}
```

Server verifies (3 attempts max), then:

```json
{"type":"PAIR_OK","token":"<32-byte-random-hex>"}
```

Server persists `clientId → token` (`paired_clients.json` next to the exe). Client
persists the token and the server IP. After `PAIR_OK` the client sends `HELLO` again
with the token and proceeds. `PAIR_FAIL` with `attemptsLeft` on wrong PIN.

### 2.3 Streaming control

Client → server:

```json
{"type":"START_STREAM","maxBitrateKbps":10000,"fps":60,"quality":"720p"}
{"type":"MEDIA_READY","port":53124}
{"type":"AUDIO_START"}
{"type":"AUDIO_READY","port":53125}
{"type":"GAMEPAD_START","controllers":1}
{"type":"GAMEPAD_STOP"}
{"type":"INPUT_START","mouse":true,"keyboard":true}
{"type":"MOUSE_BUTTON","sequence":7,"button":"left","down":true}
{"type":"MOUSE_RESET"}
{"type":"KEYBOARD_KEY","sequence":12,"usage":4,"down":true}
{"type":"KEYBOARD_RESET"}
{"type":"INPUT_STOP"}
{"type":"STOP_STREAM"}
{"type":"REQUEST_IDR"}
{"type":"STATS","framesOk":58,"framesDropped":2,"bytes":2411000,"intervalMs":1000,"captureToReceiveP95Ms":12,"decodeToSurfaceP95Ms":8}
```

Server → client:

```json
{"type":"STREAM_STARTED","mediaPort":47802,"width":1920,"height":1080,"fps":60,"codec":"h264","encoderBackend":"media-foundation","clockBaseUs":2234500000}
{"type":"AUDIO_STARTED","audioPort":47803,"sampleRate":48000,"channels":2,"format":"pcm_s16le","packetSamples":240}
{"type":"AUDIO_UNAVAILABLE","message":"No active Windows playback device"}
{"type":"GAMEPAD_STARTED","controllers":1,"controllerType":"xbox360"}
{"type":"GAMEPAD_UNAVAILABLE","message":"ViGEmBus is not installed","driverUrl":"https://github.com/nefarius/ViGEmBus/releases/latest"}
{"type":"GAMEPAD_RUMBLE","controllerId":0,"largeMotor":255,"smallMotor":128}
{"type":"INPUT_STARTED","mouse":true,"keyboard":true}
{"type":"INPUT_UNAVAILABLE","message":"Windows input injection is unavailable"}
{"type":"STREAM_STOPPED"}
{"type":"BITRATE","kbps":14000}
```

`START_STREAM.quality` is an OPTIONAL string selecting the streamed resolution. Allowed values
are `"native"` and `"720p"`; an unrecognized value becomes `"native"`. When the field is absent,
the server uses its configured default (`"native"` unless the operator selected otherwise).
`"720p"` downscales to 720 lines preserving aspect ratio: the streamed height is
`min(720, srcHeight)` and the streamed width is the source width scaled by that height ratio,
rounded to the nearest even value, clamped to at most the source width (never upscaled) and to a
minimum of 2; both dimensions are always even (NV12 requires it). When the client omits `quality`,
the server applies its configured server-wide default, so a v0.4.0 client that never sends the
field remains fully compatible. Quality is fixed for the lifetime of a stream; to change it the
client stops and restarts the stream (`STOP_STREAM` then `START_STREAM`). The authoritative
streamed dimensions are always those reported in `STREAM_STARTED.width`/`height`, regardless of
the `HELLO_OK` primary-display size.

After binding its media socket, the client sends `MEDIA_READY` over the authenticated TCP
connection with that socket's local UDP port. The server combines this port with the TCP peer
IP and sends media there. The client also sends one UDP datagram containing the 4 ASCII bytes
`DSMH` to `serverIp:mediaPort` every second until video packets arrive; this is a NAT/firewall
fallback and updates the learned endpoint. The stream always starts with an IDR frame carrying
SPS/PPS.

While the desktop is static the server sends the 4 ASCII bytes `DSHB` once per second on
the media socket. It is a liveness heartbeat, not a video packet; the client ignores its
payload but uses its arrival to distinguish an idle desktop from a dead UDP path.

After `STREAM_STARTED`, an audio-capable client sends `AUDIO_START`. The server either
starts Windows system-output loopback capture and replies `AUDIO_STARTED`, or replies
`AUDIO_UNAVAILABLE` without stopping video. `AUDIO_START` is idempotent and is ignored
unless the session is streaming. This opt-in makes old clients and servers interoperable:
an old server ignores the unknown request, and a new server does not open an audio device
for a client that never asks for it.

After binding its audio socket, the client sends `AUDIO_READY` over TCP with its local UDP
port. It also sends the 4 ASCII bytes `DSAH` from that socket to `serverIp:audioPort` every
second until an audio packet arrives as a fallback. The server sends audio to the most recent
authenticated endpoint. `STOP_STREAM`, socket death, or session disposal stops both video and
audio.

An Android client with one or more physical gamepad/joystick input devices sends
`GAMEPAD_START` with `controllers` clamped to 1..4. The server creates that many virtual
Xbox 360 controllers and replies `GAMEPAD_STARTED`, or replies `GAMEPAD_UNAVAILABLE`
without stopping video/audio (normally because the optional ViGEmBus driver is absent).
Repeating `GAMEPAD_START` changes the active count. `GAMEPAD_STOP`, `STOP_STREAM`, socket
death, or session disposal disconnects all virtual controllers.

While enabled, controller state snapshots use the video media socket and port described in
§3B. The server accepts input only from the source endpoint of the most recent valid `DSMH`
hole punch, preventing an unrelated LAN endpoint from injecting input into the session.
`GAMEPAD_RUMBLE` carries the current XInput motor strengths (0..255) back over TCP. The
client should stop vibration when both values are zero.

`STATS` is sent every 1 s during streaming. `REQUEST_IDR` is sent whenever a frame is
dropped as unrecoverable; server rate-limits IDR generation to at most one per 300 ms.

Remote input is opt-in and backward-compatible. After `STREAM_STARTED`, the client sends
`INPUT_START` with the devices it wants. Missing `mouse` or `keyboard` members mean `false`,
so an older Android client that sends only `{"mouse":true}` continues to work unchanged.
The server replies with `INPUT_STARTED`; its booleans are the capabilities that are active
for this session. A client MUST treat a missing response member as `false`. A request with no
supported device produces `INPUT_UNAVAILABLE`.

High-rate mouse motion uses §3C UDP packets. Ordered button transitions use `MOUSE_BUTTON`
on TCP. Supported button names are `left`, `right`, `middle`, `back`, and `forward`.
Duplicate/stale button sequences are ignored. `MOUSE_RESET` releases mouse buttons only.

Keyboard transitions use the ordered TCP messages in §2.4. `KEYBOARD_RESET` releases
keyboard keys only. `INPUT_STOP`, `STOP_STREAM`, socket death, or session disposal attempts
to release all injected mouse buttons and keyboard keys. Input is accepted only for the
authenticated, actively-streaming session.

### 2.4 Keyboard input

`KEYBOARD_KEY.usage` is an unsigned usage ID from the USB HID Usage Tables **Keyboard/Keypad
page (0x07)**. It describes the physical key position, not a Unicode character; for example,
usage `4` (`0x04`) is the Keyboard A position. The Windows host applies the position through
`SendInput` scan codes, so the host's active keyboard layout determines the resulting text.
Pause and the keyboard-page mute/volume usages are Windows-specific exceptions sent by
virtual-key value because they do not have a single official Set 1 scan-code translation.

The server supports the standard desktop keyboard/keypad set (`0x04..0x73`), the keyboard
page mute/volume keys (`0x7f..0x81`), and left/right modifiers (`0xe0..0xe7`). Unknown,
reserved, or error usages MUST be ignored without closing the session. The standard set
includes letters, number row, punctuation, Enter/Escape/Backspace/Tab, Caps Lock, F1..F24,
Print Screen/Scroll Lock/Pause, navigation, arrows, the numeric keypad, Application, and
Power where Windows exposes a corresponding scan code.

`sequence` is a per-keyboard uint32 counter. The server uses wrap-safe ordering and ignores
duplicate or stale values. A repeated `down:true` for an already-held usage and a repeated
`down:false` for an already-released usage are idempotent; clients SHOULD send physical
transitions rather than OS key-repeat events. A client MUST send `KEYBOARD_RESET` when its
stream view loses focus or keyboard capture is released. The server independently performs
the same reset on every input/session teardown path. If Windows temporarily rejects a live
reset, the server retains the unreleased key state and retries before accepting more keyboard
input. Final process/session teardown remains best-effort because Windows can reject all
injection across integrity-level or desktop boundaries.

## 3. Media channel (UDP, server → client)

Each datagram: **20-byte header + payload ≤ 1200 bytes**.

```
offset  size  field
0       1     version        = 1
1       1     flags          bit0 KEYFRAME (frame is IDR)
                             bit1 FEC (this packet is parity, not data)
2       2     uint16 payloadLen
4       4     uint32 frameId        (monotonic, starts at 0)
8       2     uint16 packetIndex    (data: 0..packetCount-1; FEC: group index, see §3.2)
10      2     uint16 packetCount    (number of DATA packets in this frame)
12      2     uint16 fecCount       (number of FEC packets in this frame)
14      4     uint32 ptsMs          (server steady-clock ms, wraps; stats only)
18      2     uint16 pipelineDelayMs (capture through encoder output, saturated at 65535)
20      ...   payload
```

The frame payload is one encoded H.264 access unit in **Annex-B** format (start codes
included). IDR frames MUST be preceded in the same access unit by SPS and PPS NALs
(encoder repeat-headers on). The access unit is split into consecutive ≤1200-byte
chunks; chunk `i` goes in the packet with `packetIndex = i`.

### 3.1 Client assembly rules — no jitter buffer

- Reassemble by `frameId`. A frame is complete when all `packetCount` chunks are
  present (after FEC recovery). Feed complete frames to the decoder **immediately**.
- Keep at most 2 frames in assembly. If a **newer** frame completes while an older one
  is incomplete, or assembly is full: drop the older frame. If the dropped frame might
  be referenced (i.e. any drop), send `REQUEST_IDR` and **discard everything** until the
  next `KEYFRAME` frame arrives (decoding a stream with a missing reference produces
  corruption — never feed across a gap in `frameId` except at a keyframe).
- Never delay rendering by PTS. Release decoder output to the surface as soon as it is
  produced.

### 3.2 FEC — XOR parity groups of 8

Data packets of a frame are split into consecutive groups of up to 8
(group `g` = data packets `8g .. 8g+7`). For each group the server sends one parity
packet: flags bit1 set, `packetIndex = g`, payload = byte-wise XOR of that group's
payloads, each zero-padded to the length of the group's longest payload;
`payloadLen` = that max length. `fecCount = ceil(packetCount / 8)`.

Chunking rule (server, normative): every data packet's payload is exactly **1200 bytes
except the frame's last packet** (`packetIndex == packetCount-1`), which carries the
remainder.

Client recovery: if exactly **one** data packet of a group is missing and the group's
parity packet is present, reconstruct it as the byte-wise XOR of the parity payload
with the group's other data payloads (each zero-padded to `parityLen`). The recovered
packet's true length is 1200 if it is not the frame's last packet; if it *is* the last
packet, use the full `parityLen` — the extra bytes are trailing zeros, which are
harmless after an H.264 Annex-B access unit (decoders consume NALs by start code).
If two or more packets of a group are missing, the frame is unrecoverable → drop per
§3.1.

### 3.3 Server send rules

- Encode → packetize → send immediately; do not pace across the frame interval.
- If encoding of frame N has not finished when frame N+1 is captured, drop N+1
  (capture side already coalesces).
- Socket send buffer small (≤256 KB). Set DSCP AF41 / TOS 0x88 on the media socket
  (best effort; ignore failures).

## 3A. Audio channel (UDP, server → client)

Audio uses a separate negotiated UDP socket so video loss/reassembly cannot delay audio,
and so an older video receiver never mistakes audio for an H.264 packet. v1.1 audio is the
Windows default playback device captured through WASAPI loopback and normalized to
**48,000 Hz, stereo, signed 16-bit little-endian interleaved PCM**. It deliberately avoids
a codec frame or decoder startup buffer; the tradeoff is a fixed 1,536 kbps audio payload
rate while sound is active.

Each datagram is a **16-byte header + exactly 960 payload bytes** (240 stereo sample frames,
5 ms), except that no packets need be emitted while the Windows output device is silent.
Header integers are big-endian; only the PCM samples in the payload are little-endian.

```
offset  size  field
0       1     version        = 1
1       1     format         = 1 (PCM_S16LE)
2       2     uint16 payloadLen   = 960
4       4     uint32 sequence     (monotonic audio-packet id, starts at 0)
8       4     uint32 ptsMs        (same server stream clock as video; stats only)
12      2     uint16 sampleCount  = 240 (sample frames per channel)
14      2     reserved = 0
16      ...   interleaved L, R PCM samples
```

Audio rules:

- Packetize and send each 5 ms block immediately. Keep at most one partial block on the
  server; never retransmit and do not add FEC.
- The client validates `payloadLen == sampleCount × channels × 2`. Stale or reordered
  packets are discarded. For a small forward sequence gap, it may write an equivalent
  number of zero-filled 5 ms blocks before the newest packet to preserve audio time.
- Feed accepted data to a low-latency streaming audio output immediately. There is no
  network jitter buffer, PTS pacing, or wait for the matching video timestamp. If the
  device playback buffer is full, drop audio rather than building a queue.
- Client mute is local: keep receiving and draining the stream while setting playback
  volume to zero, so unmute is immediate and does not require renegotiation.

## 3B. Gamepad input (UDP, client → server)

Gamepad messages are **complete state snapshots**, not deltas. Losing a datagram therefore
heals on the next snapshot without retransmission or TCP head-of-line blocking. Send on
state change at no more than 120 Hz and send an unchanged heartbeat at least every 250 ms
while a controller is present. The fixed datagram is 24 bytes:

```
offset  size  field
0       4     ASCII magic = "DSGP"
4       1     version = 1
5       1     controllerId = 0..3
6       2     uint16 buttons (XInput bit layout, big-endian)
8       1     leftTrigger  = 0..255
9       1     rightTrigger = 0..255
10      2     int16 leftX   (big-endian, -32768..32767)
12      2     int16 leftY   (big-endian, up is positive)
14      2     int16 rightX  (big-endian, -32768..32767)
16      2     int16 rightY  (big-endian, up is positive)
18      4     uint32 sequence (big-endian, monotonic per controller)
22      2     reserved = 0
```

Button bits match `XINPUT_GAMEPAD.wButtons`: D-pad up/down/left/right =
`0x0001/0x0002/0x0004/0x0008`; Start `0x0010`; Back `0x0020`; left/right stick click
`0x0040/0x0080`; left/right shoulder `0x0100/0x0200`; Guide `0x0400`; A/B/X/Y
`0x1000/0x2000/0x4000/0x8000`.

Server rules:

- Ignore malformed packets, unknown versions, controller ids outside the negotiated count,
  packets from any endpoint other than the current `DSMH` endpoint, and duplicate/stale
  sequence numbers (using wrap-safe uint32 comparison).
- Apply accepted snapshots immediately to the matching virtual controller. There is no
  queue. On controller/session stop, submit a neutral state before disconnecting it so no
  button or axis can remain stuck.
- Gamepad support is optional. Failure to create the virtual device must produce
  `GAMEPAD_UNAVAILABLE`; it must never stop screen or audio streaming.

## 3C. Mouse motion (UDP, client → server)

High-rate cursor motion and scrolling share the learned media endpoint. The fixed packet is
28 bytes; all integers are big-endian:

```
offset  size  field
0       4     ASCII magic = "DSMI"
4       1     version = 1
5       1     mode: 0 relative, 1 absolute
6       2     reserved = 0
8       4     uint32 sequence
12      4     int32 x (relative delta, or absolute 0..65535)
16      4     int32 y (relative delta, or absolute 0..65535)
20      4     int32 horizontalWheel (Windows wheel units)
24      4     int32 verticalWheel (Windows wheel units)
```

The Android client coalesces motion to at most 120 packets/s and uses unbuffered touch
dispatch where supported. Absolute coordinates map only the captured primary display.
The server rejects motion before `INPUT_STARTED`, from a source other than the learned media
endpoint, or with a stale sequence. Relative packets use newest-arrival order; lost deltas are
not retransmitted. Button transitions stay on TCP so packet loss cannot leave a button stuck.

After applying a motion packet the server may return a 16-byte authoritative cursor packet
on the media channel: ASCII `DSMC`, version byte `1`, three reserved zero bytes, the echoed
uint32 motion sequence, then uint16 normalized primary-display X and Y. The client uses this
for its cursor overlay; clients that do not recognize it ignore it.

## 4. Adaptation (server-side controller)

Inputs: `STATS` messages and IDR request rate.
- The effective session ceiling is the smaller of `START_STREAM.maxBitrateKbps` and the
  operator-configured server ceiling (20,000 kbps by default, never below 2,000 kbps).
- **Down:** if `framesDropped / (framesOk+framesDropped) > 1%` in a stats interval, latency
  grows materially above the session's best observed baseline, or
  ≥2 `REQUEST_IDR` in 1 s → new bitrate = max(2000, current × 0.7), apply to encoder,
  send `BITRATE`.
- **Up:** after 5 consecutive clean intervals (0 drops, 0 IDR requests) → new bitrate =
  min(maxBitrateKbps, current × 1.1).
- Start at min(8000, maxBitrateKbps).
- Optional latency fields do not break older peers. Sustained capture-to-receive or
  decode-to-surface growth should be treated as congestion before a queue can form.

## 5. Client connection state machine

`DISCOVERING → CONNECTING → PAIRING → READY → STREAMING → RECONNECTING → (READY | DISCOVERING)`

- Any socket death in READY/STREAMING → RECONNECTING (keep token; backoff per §2).
- Android app background → send `STOP_STREAM`, keep control socket; foreground →
  `START_STREAM` again. Control socket death while backgrounded is fine — reconnect on
  foreground.
- Auth failure with a stored token (server re-paired/reset) → clear token → PAIRING.
