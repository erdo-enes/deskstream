# DeskStream Protocol v1 — NORMATIVE

Both the Windows server and the Android client implement exactly this. All multi-byte
integers are **big-endian**. All JSON is UTF-8. Protocol version is `1`.

Default ports: discovery **UDP 47800**, control **TCP 47801**, media **UDP negotiated**
(server picks, typically 47802, and reports it in `STREAM_STARTED`).

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
{"type":"START_STREAM","maxBitrateKbps":20000,"fps":60}
{"type":"STOP_STREAM"}
{"type":"REQUEST_IDR"}
{"type":"STATS","framesOk":58,"framesDropped":2,"bytes":2411000,"intervalMs":1000}
```

Server → client:

```json
{"type":"STREAM_STARTED","mediaPort":47802,"width":1920,"height":1080,"fps":60,"codec":"h264"}
{"type":"STREAM_STOPPED"}
{"type":"BITRATE","kbps":14000}
```

After `STREAM_STARTED` the client sends one UDP datagram containing the 4 ASCII bytes
`DSMH` from its media socket to `serverIp:mediaPort` every second until video packets
arrive (and stops after). This is a **hole punch / address learn**: the server sends all
media packets to the source address of the most recent `DSMH`. The stream always starts
with an IDR frame carrying SPS/PPS.

`STATS` is sent every 1 s during streaming. `REQUEST_IDR` is sent whenever a frame is
dropped as unrecoverable; server rate-limits IDR generation to at most one per 300 ms.

Reserved for future: `INPUT`, `AUDIO_START` (ignore if received).

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
18      2     reserved = 0
20      ...   payload
```

The frame payload is one encoded H.264 access unit in **Annex-B** format (start codes
included). IDR frames MUST be preceded in the same access unit by SPS and PPS NALs
(encoder repeat-headers on). The access unit is split into consecutive ≤1200-byte
chunks; chunk `i` goes in the packet with `packetIndex = i`.

### 3.1 Client assembly rules — no jitter buffer

- Reassemble by `frameId`. A frame is complete when all `packetCount` chunks are
  present (after FEC recovery). Feed complete frames to the decoder **immediately**.
- Keep at most 4 frames in assembly. If a **newer** frame completes while an older one
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

## 4. Adaptation (server-side controller)

Inputs: `STATS` messages and IDR request rate.
- **Down:** if `framesDropped / (framesOk+framesDropped) > 2%` in a stats interval, or
  ≥2 `REQUEST_IDR` in 1 s → new bitrate = max(2000, current × 0.7), apply to encoder,
  send `BITRATE`.
- **Up:** after 5 consecutive clean intervals (0 drops, 0 IDR requests) → new bitrate =
  min(maxBitrateKbps, current × 1.1).
- Start at min(8000, maxBitrateKbps).

## 5. Client connection state machine

`DISCOVERING → CONNECTING → PAIRING → READY → STREAMING → RECONNECTING → (READY | DISCOVERING)`

- Any socket death in READY/STREAMING → RECONNECTING (keep token; backoff per §2).
- Android app background → send `STOP_STREAM`, keep control socket; foreground →
  `START_STREAM` again. Control socket death while backgrounded is fine — reconnect on
  foreground.
- Auth failure with a stored token (server re-paired/reset) → clear token → PAIRING.
