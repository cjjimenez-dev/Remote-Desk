# RemoteDesk Protocol Specification (v1 Draft)

## 1. Overview
The RemoteDesk protocol defines binary message framing for communication between Host and Viewer across WebRTC data channels and local IPC.

Sessions run two primary media/data paths:
1. **RTP Media Track**: Encoded video (H.264 / AV1) and audio (Opus) streamed via WebRTC peer connection.
2. **Data Channels**: Multiplexed binary message channels.

---

## 2. Data Channels

| Channel Name | Reliability | Ordering | Purpose |
|--------------|-------------|----------|---------|
| `control`    | Reliable    | Ordered  | Handshake, capabilities, session permissions, stats, ping/pong |
| `input`      | Mixed       | Ordered  | Mouse clicks & keystrokes are reliable; mouse moves may be unreliable for minimal latency |
| `clipboard`  | Reliable    | Ordered  | Clipboard text, image, and file list synchronization |
| `files`      | Reliable    | Ordered  | Chunked file transfers with flow control and SHA-256 verification |
| `terminal`   | Reliable    | Ordered  | Remote PowerShell/CMD interactive PTY session (Phase 9) |

---

## 3. Envelope Framing

Every control and data message on a data channel is preceded by a standardized binary envelope header:

```
+---------------+---------------+---------------+---------------+
|  Magic (2B)   | Version (1B)  | ChannelId(1B) | MsgType (2B)  |
+---------------+---------------+---------------+---------------+
| Flags (2B)    | Payload Length (4B, Little-Endian)            |
+---------------+---------------+---------------+---------------+
| Payload bytes ...                                             |
+---------------------------------------------------------------+
```

- **Magic** (2 bytes): `0x52, 0x44` (`'R'`, `'D'`).
- **Version** (1 byte): Protocol major version (e.g. `0x01`).
- **ChannelId** (1 byte):
  - `0x01`: Control
  - `0x02`: Input
  - `0x03`: Clipboard
  - `0x04`: FileTransfer
  - `0x05`: Terminal
- **MessageType** (2 bytes):
  - `0x0001`: HandshakeRequest
  - `0x0002`: HandshakeResponse
  - `0x0003`: SessionPermissionUpdate
  - `0x0004`: HeartbeatPing
  - `0x0005`: HeartbeatPong
  - `0x0100`: MouseMove
  - `0x0101`: MouseButton
  - `0x0102`: MouseWheel
  - `0x0103`: KeyState
  - `0x0104`: TextUnicode
  - `0x0200`: ClipboardSync
  - `0x0300`: FileTransferRequest
  - `0x0301`: FileChunk
  - `0x0302`: FileTransferAck
- **Flags** (2 bytes): Reserved for compression, encryption, and chunking flags.
- **Payload Length** (4 bytes): 32-bit unsigned integer length of the following payload.
- **Payload**: Serialized MessagePack binary object or raw bytes (for file chunks / raw buffers).

---

## 4. Handshake & Capability Negotiation

Upon data channel connection:
1. Viewer sends `HandshakeRequest`:
   - `MinProtocolVersion`: 1
   - `MaxProtocolVersion`: 1
   - `AppVersion`: e.g. "0.1.0"
   - `OSInfo`: OS description
   - `SupportedCapabilities`: Bitmask or string set:
     - `video.h264`, `input.unicode`, `clipboard.text`, `clipboard.files`, `files.resume`, `audio.opus`, `terminal`
2. Host compares supported versions and computes the capability intersection:
   - Responds with `HandshakeResponse` containing `AgreedProtocolVersion`, `AgreedCapabilities`, and `SessionId`.
   - Unknown capabilities are ignored, ensuring forward and backward compatibility.

---

## 5. Session Permissions

Per-session permissions can be granted, restricted, or revoked at runtime by the Host:
- `View` (receive screen capture)
- `Control` (inject mouse and keyboard input)
- `Clipboard` (read/write clipboard)
- `Files` (initiate file transfer)
- `Audio` (listen to host audio)
- `Terminal` (access command line)

The host can update permissions at any point; updates take effect immediately and are logged.
