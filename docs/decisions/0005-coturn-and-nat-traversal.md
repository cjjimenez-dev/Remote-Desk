# 5. STUN/TURN Relaying, Dynamic Credentials, and NAT Traversal

Date: 2026-11-20

## Status

Accepted

## Context

Phase 4 enables RemoteDesk to establish connections beyond local area networks over the public Internet, across residential/carrier-grade NATs (CGNAT), and through symmetric/restrictive corporate firewalls that block incoming UDP.

We needed:
1. A standard RFC 5766 TURN server configuration capable of relaying WebRTC traffic over UDP, TCP, and TLS (TURNS).
2. A secure mechanism to generate short-lived, time-limited credentials without exposing static secrets to untrusted clients.
3. Client-side dynamic ICE server discovery and real-time connection path classification (Direct LAN vs. Direct STUN Hole-Punch vs. TURN Relayed).
4. A forced relay diagnostic mode to simulate restrictive enterprise networks.

## Decision

1. **coturn Server Deployment**:
   - Packaged production-ready configuration in `deploy/coturn/turnserver.conf` and `docker-compose.yml`.
   - Recommended deployment to low-latency regions (e.g., Singapore VPS with ~30-45 ms latency from Philippine ISPs).
   - Configured listening on ports 3478 (UDP/TCP), 5349 (TLS), and fallback on 443 (TCP/TLS) to bypass strict proxy firewalls.

2. **Time-Limited HMAC-SHA1 Credentials**:
   - Implemented `TurnCredentialGenerator` in `RemoteDesk.Core.Security`.
   - Credentials format: `username = <timestamp>:<deviceId>`, `password = Base64(HMAC-SHA1(static_auth_secret, username))`.
   - Exposed via both SignalR `GetIceServers(deviceId)` and REST endpoint `GET /api/ice-servers?deviceId={id}`.

3. **Connection Path Detection & UI Indicator**:
   - Added `ConnectionPath` enum (`DirectLocal`, `DirectStunHolePunch`, `TurnRelayed`).
   - Extended `WebRtcTransport` to analyze local and remote candidate attributes (`typ host`, `typ srflx`, `typ prflx`, `typ relay`) and IP addresses upon connection.
   - Surfaced the active path as a color-coded status badge in the WPF Viewer HUD and status bar:
     - 🟢 `Direct LAN` (Green)
     - 🟢 `Direct P2P (STUN)` (Emerald)
     - 🟠 `TURN Relayed` (Amber)

4. **Forced Relay Testing**:
   - Added `forceRelay` capability setting `RTCIceTransportPolicy.relay` in `WebRtcTransport`.
   - Exposed via `--force-relay` CLI flag in `RemoteDesk.Host` and as a UI checkbox toggle in `RemoteDesk.Viewer`.

## Consequences

- Sessions seamlessly transition from direct LAN to STUN NAT hole-punching, or fall back gracefully to TURN relaying when direct P2P is blocked.
- Short-lived credentials prevent unauthorized TURN relay bandwidth abuse.
- Complete observability: users and operators can immediately see whether traffic is flowing direct or relayed.
