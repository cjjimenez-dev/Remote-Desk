# 4. SignalR Signaling and WebRTC P2P Data Pipeline

Date: 2026-11-20

## Status

Accepted

## Context

Phase 3 transitions RemoteDesk from a single-machine loopback pipeline to a distributed peer-to-peer remote desktop architecture over local networks (and paving the path for Phase 4 internet NAT traversal).

We needed:
1. An efficient, low-overhead signaling protocol for host registration, presence, session requests, and SDP/ICE candidate exchange.
2. An `ITransport` implementation encapsulating SIPSorcery's `RTCPeerConnection` without leaking library specifics into Viewer UI or Host domain logic.
3. A reliable mechanism for streaming video frames and control/input messages across WebRTC data channels without hitting SCTP packet size or fragmentation issues.

## Decision

1. **Signaling Server via ASP.NET Core SignalR**:
   - Implemented `RemoteDesk.Server.Signaling` using ASP.NET Core and SignalR with strongly-typed protocol messages.
   - Hosted a central `SignalingHub` supporting `RegisterHost`, `RequestConnection`, `AcceptConnection`, `RejectConnection`, `SendOffer`, `SendAnswer`, and `SendIceCandidate`.
   - Built `SignalingClient` in `RemoteDesk.Transport.WebRtc` featuring automatic reconnect policies with exponential backoff.

2. **WebRtcTransport implementing ITransport**:
   - `WebRtcTransport` encapsulates SIPSorcery `RTCPeerConnection` with STUN discovery and ICE candidate gathering.
   - Standardized data channels: `control` (ID 1, reliable/ordered), `input` (ID 2, reliable/ordered + unordered capability), `clipboard` (ID 3), `files` (ID 4), and `video` (ID 6).
   - Designed a 32 KB chunked frame reassembly envelope for video frames to guarantee full transport compatibility across SCTP buffer boundaries without risking dropped keyframes or truncated NAL units.

3. **Remote Input & Frame Dispatch**:
   - Viewer intercepts WPF input events, wraps them into `ProtocolEnvelope` with `MessageType` (`MouseMove`, `MouseButton`, `MouseWheel`, `KeyState`, `TextUnicode`), and transmits them over WebRTC data channels.
   - Host listens on the WebRTC data channel, unpacks the envelope, and forwards directly into `WindowsInputInjector` while respecting the host-side `EmergencyKillSwitch` hotkey (`Ctrl+Alt+Shift+Esc`).

## Consequences

- The application now fully operates in true two-node distributed mode: a Host agent running on one PC and a Viewer client running on another PC on the same LAN or network.
- Complete decoupled testability: integration tests can spin up an ephemeral SignalR server and two virtual WebRTC peers to verify handshake, video transmission, and input events end-to-end in CI without requiring physical displays or human interaction.
- Phase 4 (Internet Connectivity & coturn) can seamlessly build upon this transport by provisioning coturn STUN/TURN credentials in `SignalingClient` and `WebRtcTransport`.
