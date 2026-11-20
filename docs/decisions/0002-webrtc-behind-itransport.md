# 2. WebRTC Transport Behind ITransport Interface

Date: 2026-11-20

## Status

Accepted

## Context

Remote desktop software requires low-latency, NAT-traversing, peer-to-peer audio/video streaming and bidirectional data channels. WebRTC is the proven industry standard for peer-to-peer media and data over STUN/TURN (ICE).

However, tying the core domain and session logic directly to a specific WebRTC library (such as SIPSorcery) introduces tight coupling and makes it difficult to:
- Test session logic headlessly in automated CI without spinning up full ICE/DTLS peers.
- Swap out SIPSorcery in the future for a custom UDP/QUIC protocol, WebRTC native C++ wrapper, or another implementation if performance limits are hit.
- Support mock transports for fuzz testing and automated regression testing.

## Decision

All transport operations (connecting, state changes, sending raw video/audio frames, sending reliable or unreliable data channel messages) must be abstracted behind the `ITransport` interface in `RemoteDesk.Core`.
`RemoteDesk.Transport.WebRtc` will provide the SIPSorcery implementation.

## Consequences

- Domain, host, and viewer logic remain decoupled from WebRTC internals.
- Headless testing in CI is straightforward with a mock `ITransport`.
- Alternative transport protocols (QUIC / custom UDP with Noise protocol) can be slotted in as drop-in replacements.
