# RemoteDesk Security Architecture & Threat Model

*Prepared for Phase 5: Connection Flow and Security*  
*Last Updated: November 2026*

---

## 1. System Overview & Trust Boundaries

RemoteDesk establishes end-to-end encrypted (E2EE) attended and unattended remote desktop sessions between Windows hosts and remote viewers.

```
+-----------------------------------------------------------------------------------+
|                                  TRUST BOUNDARY                                   |
|                                                                                   |
|   +-----------------------+                            +----------------------+   |
|   |      HOST PC          |                            |      VIEWER PC       |   |
|   |  - ECDSA P-256 Key    |                            |  - ECDSA P-256 Key   |   |
|   |  - DPAPI Protected    |                            |  - DPAPI Protected   |   |
|   |  - Session Banner     |                            |  - Identity Auth     |   |
|   |  - Kill Switch        |                            |  - Metrics HUD       |   |
|   +-----------+-----------+                            +----------+-----------+   |
|               |                                                   |               |
+---------------|---------------------------------------------------|---------------+
                |                                                   |
                |   UNTRUSTED NETWORK / RELAY LAYER                 |
                |                                                   |
                |   +-------------------------------------------+   |
                +-->|     SignalR Signaling Hub (Untrusted)     |<--+
                |   |   - Presence, Routing, SDP & ICE Relay    |   |
                |   +-------------------------------------------+   |
                |                                                   |
                |   +-------------------------------------------+   |
                +-->|     coturn STUN / TURN Relay (Untrusted)  |<--+
                    |   - Relays encrypted DTLS-SRTP / SCTP     |
                    +-------------------------------------------+
```

### Components & Trust Levels:
1. **Host Agent (Trusted)**: Executes in the target host's environment. Owns screen capture, input injection, local encryption keys, and session authorization.
2. **Viewer (Trusted)**: Runs on the remote operator's machine. Renders decoded frames, captures user input, and maintains operator credentials.
3. **Signaling Server (Untrusted / Semi-Trusted)**: Handles connection routing, presence registry, and SDP/ICE candidate exchange. **Design invariant**: The signaling server must NOT be able to inspect, decrypt, or tamper with active session media or control data without detection.
4. **STUN/TURN Relay (Untrusted)**: Relays encrypted UDP packets across NAT/firewalls. Cannot decrypt DTLS-SRTP or SCTP payloads.

---

## 2. Threat Analysis & Mitigations

### Threat 1: Malicious or Compromised Signaling Server (MITM Attack)
- **Vector**: A compromised signaling server modifies SDP Offer/Answer payloads to inject an attacker-controlled DTLS certificate fingerprint (`a=fingerprint:sha-256 ...`), terminating the DTLS handshake on an eavesdropping proxy.
- **Mitigation**:
  - Each endpoint creates an **ECDSA P-256** identity keypair at initial setup, persisted securely using Windows **DPAPI** (`DpapiDeviceKeyStore`).
  - Endpoints extract the DTLS certificate fingerprint from the SDP and cryptographically sign a payload combining `SessionId + SenderDeviceId + TargetConnectionId + DtlsFingerprint + Timestamp` with their private ECDSA key.
  - The receiving endpoint validates the signature against the sender's public key using `DtlsFingerprintProtector.VerifySignedSdp(...)`.
  - If the signaling server alters the fingerprint or SDP in transit, the signature check fails immediately, and the session is aborted before any media or input is transmitted.
  - *Verified by unit test*: `DtlsFingerprintAntiMitmTests.MaliciousSignalingServer_TamperingWithDtlsFingerprint_IsDetectedAndRejected`.

### Threat 2: Eavesdropping on Relayed Traffic (TURN Sniffing)
- **Vector**: An attacker monitors traffic passing through a public coturn TURN server or compromise the TURN relay machine.
- **Mitigation**:
  - All media streams (video, audio) flow over **DTLS-SRTP** (AES-128/256-GCM).
  - All data channels (input injection, clipboard, control, file transfer) flow over **SCTP encapsulated within DTLS**.
  - TURN only inspects standard STUN/TURN framing headers (e.g., `ChannelData`, `SendIndication`) and has no access to underlying DTLS keys.

### Threat 3: Unauthorized Connection Requests & Brute Force
- **Vector**: An unauthorized actor guesses a Host ID and attempts connection brute forcing.
- **Mitigation**:
  - **Attended Mode**: Host user must explicitly grant consent with granular permission toggles (`View`, `Control`, `Clipboard`, `Files`).
  - **Unattended Mode**: Password authentication uses **PBKDF2-HMAC-SHA256** (100,000 iterations) with salted challenge-response nonces (`SessionChallengeAuth`). Raw passwords and derived keys are never transmitted over the wire.
  - **Rate Limiting**: `RateLimiter` enforces lockout (15 minutes after 5 failed attempts) tracked per `ViewerDeviceId`.
  - **Cryptographic Request Signing**: Connection requests carry an ECDSA signature binding `SessionId:ViewerDeviceId:Timestamp`.

### Threat 4: Rogue Remote Control & Host User Lockout
- **Vector**: A remote operator with control permissions attempts malicious actions while the host user is watching.
- **Mitigation**:
  - **Always-Visible Session Banner**: `SessionBannerService` displays a floating, always-on-top banner on the host screen showing the connected operator's identity and permissions, with a red **"Disconnect"** button.
  - **Emergency Kill Switch**: Host user can press **`Ctrl + Alt + Shift + Esc`** at any time. The global low-level keyboard hook immediately revokes and terminates remote input injection.
  - **Host Input Precedence**: Local physical mouse and keyboard inputs override remote simulated inputs.

### Threat 5: Auditability & Non-Repudiation
- **Vector**: Actions performed during remote desktop sessions cannot be traced or analyzed post-incident.
- **Mitigation**:
  - `AuditLogger` records structured security audit events:
    - Session requests (`LogSessionRequested`)
    - Consent decisions and permissions granted (`LogConsentDecision`)
    - Session start with transport path and permissions (`LogSessionStart`)
    - Session termination and duration (`LogSessionEnd`)
    - Rate limit lockouts (`LogRateLimitTriggered`)
    - Emergency kill triggers (`LogEmergencyKill`)
    - Security tampering / MITM alerts (`LogTamperingDetected`)

---

## 3. Residual Risks & Next Phase Enhancements

1. **Secure Desktop & UAC Elevation (Phase 6)**:
   - When a UAC prompt appears or the workstation is locked (Winlogon / Secure Desktop), standard user-mode processes cannot capture or inject input. Phase 6 will implement the Windows Service mode (`RemoteDesk.Host.Service` running as SYSTEM) with named-pipe IPC and desktop switching (`OpenInputDesktop`).
2. **Password Authentication Evolution (Phase 7+)**:
   - Upgrade PBKDF2 challenge-response to a formal zero-knowledge PAKE protocol (e.g. OPAQUE or SPAKE2+) to protect against offline dictionary attacks even if challenge nonces are intercepted.
