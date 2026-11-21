# 6. Connection Flow and End-to-End Security

Date: 2026-11-21

## Status
Accepted

## Context
In Phase 3 and Phase 4, RemoteDesk established peer-to-peer and relayed WebRTC sessions over LAN and the Internet via SignalR signaling and coturn STUN/TURN servers. However, WebRTC SDP negotiation passes through the signaling server in clear text. If the signaling server is compromised, malicious, or intercepted, an attacker could tamper with SDP session descriptions—specifically substituting their own DTLS certificate fingerprint (`a=fingerprint:sha-256 ...`) to execute a transparent man-in-the-middle (MITM) attack against the media and data channels.

Furthermore, remote control sessions require strong access control:
1. Attended consent by the host user with granular permissions (View, Control, Clipboard, File Transfer).
2. Unattended access without transmitting passwords in clear text over the wire.
3. Brute-force protection and lockout for unauthorized connection attempts.
4. Always-visible session indicator on the host with immediate disconnect capabilities.
5. Cryptographic device identities independent of ephemeral IP addresses or server state.

## Decision
1. **Device Identity & Cryptography**:
   - Every host and viewer generates an **ECDSA P-256** keypair.
   - Keys are secured on Windows using the Data Protection API (**DPAPI** via `DpapiDeviceKeyStore`).
   - A short numeric, human-readable Device ID (`XXX-YYY`) is deterministically derived from the SHA-256 digest of the public key.
2. **Anti-MITM DTLS Fingerprint Protection**:
   - The DTLS fingerprint extracted from the SDP is bound to `SessionId`, `SenderDeviceId`, `TargetConnectionId`, and a UTC timestamp, and signed using the device's private key (`DtlsFingerprintProtector.SignSdp`).
   - The remote peer verifies the signature against the sender's public key before accepting the SDP (`DtlsFingerprintProtector.VerifySignedSdp`).
   - If the signaling server alters the SDP fingerprint, verification fails and the connection is aborted immediately.
3. **Challenge-Response Unattended Authentication**:
   - Unattended password authentication uses **PBKDF2-HMAC-SHA256** (100,000 iterations) and random 32-byte nonces (`SessionChallengeAuth`). Raw passwords and derived keys are never sent over the network.
4. **Rate Limiting & Lockout**:
   - `RateLimiter` enforces a 15-minute lockout when 5 consecutive failed authentication attempts occur for a given device ID.
5. **Host Floating Session Banner**:
   - An always-on-top, semi-transparent session banner (`SessionBannerService`) appears on the host screen during active connections, displaying the connected operator's identity, active permissions, and a one-click "Disconnect" button.
6. **Emergency Kill Switch & Audit Logging**:
   - The low-level `Ctrl + Alt + Shift + Esc` hotkey immediately disables remote input injection.
   - `AuditLogger` writes structured security logs for all session lifecycle and security events.

## Consequences
- **Positive**:
  - Zero-trust security model: Even a fully compromised signaling server cannot inspect or alter session traffic.
  - No plaintext credentials transmitted at any point in the session lifecycle.
  - Complete host transparency and control: host users can see active connections and terminate them instantly.
- **Negative / Trade-offs**:
  - Adds ~1-2ms to SDP exchange for ECDSA signature generation and verification (negligible).
  - Clock skew between peers must be within 5 minutes for timestamp validation.
