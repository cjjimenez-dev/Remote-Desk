# 8. Core Features: Clipboard Sync, Chunked File Transfer, Audio Loopback, Multi-Monitor, and Adaptive Quality

Date: 2026-11-21

## Status
Accepted

## Context
With foundational transport, encryption, WebRTC P2P signaling, NAT traversal, and Windows Service mode established in Phases 0–6, Phase 7 focuses on essential day-to-day remote control capabilities required for a production-grade AnyDesk alternative:
1. **Clipboard Synchronization**: Seamless bi-directional text and image synchronization between host and viewer without infinite echo loops.
2. **Chunked File Transfer**: Reliable, bounded chunk streaming over WebRTC data channels with flow control so file transfers never starve real-time video or input packets.
3. **Audio Streaming**: Real-time system audio capture from the host via Windows Audio Session API (WASAPI) loopback and low-latency playback on the viewer.
4. **Multi-Monitor Display Switching**: Dynamic enumeration and switching between multiple host monitors or virtual desktop modes during an active session.
5. **Adaptive Quality & Congestion Control**: Real-time feedback loop adjusting bitrate and framerate based on measured RTT, packet loss, and decode timings.
6. **In-Session Chat & Address Book**: Text messaging between operator and host user, alongside persistent local contact storage with favorite pinning.

## Decision

1. **Clipboard Synchronization (`WindowsClipboardService`)**:
   - Implements Win32 `AddClipboardFormatListener` and `WM_CLIPBOARDUPDATE` running on an isolated background STA thread with a dedicated WPF `HwndSource`.
   - Supports Unicode text (`CF_UNICODETEXT`) and bitmap/PNG images (`CF_DIB`/PNG streams) with a 10 MB payload safety boundary.
   - **Echo-Loop Prevention**: Computes SHA-256 hashes of all outbound and incoming clipboard payloads. Recent injected hashes are cached; if a subsequent local clipboard event matches a recently injected remote hash, it is discarded.
2. **Chunked File Transfer (`FileTransferService`)**:
   - Operates on dedicated WebRTC data channel (`DataChannels.Files = 4`).
   - Uses 32 KB binary chunks with a sliding window flow-control threshold (`MaxInFlightBytes = 256 KB`), preventing SCTP buffer saturation.
   - Computes whole-file SHA-256 checksums to verify end-to-end file integrity.
   - Supports download resumption via `.part` temporary files, allowing interrupted transfers to resume from existing byte offsets.
3. **WASAPI Loopback Audio Capture & Playback (`WasapiAudioCapturer`, `WasapiAudioPlayer`)**:
   - Host captures system-wide audio using WASAPI loopback (`NAudio.Wasapi` 3.3.0) at 48 kHz stereo.
   - Audio packets are streamed over `DataChannels.Audio = 6` with sequence numbering and low-latency, unbuffered delivery.
   - Viewer plays back received PCM audio using `WasapiOut` with client-side volume and mute controls.
4. **Multi-Monitor Display Switching**:
   - Host enumerates all attached monitors via Win32 `EnumDisplayMonitors` / `GetMonitorInfo` and announces the display topology via `DisplayListMessage` over `DataChannels.Control = 1`.
   - Viewer toolbar provides a monitor selection dropdown. Choosing a display sends `SwitchDisplayMessage` to the host, dynamically updating `capturer.SetTargetDisplay(id)` and recalibrating `WindowsInputInjector` virtual desktop coordinate normalization.
5. **Adaptive Quality & Congestion Control (`AdaptiveQualityController`)**:
   - Viewer periodically emits `QualityMetricsReportMessage` containing measured FPS, bitrate, decode latency, RTT, and packet loss.
   - Controller provides presets (`Speed` 60 FPS @ 2 Mbps, `Balanced` 30 FPS @ 4 Mbps, `Quality` 30 FPS @ 8 Mbps).
   - If packet loss exceeds 5% or RTT > 200 ms, bitrate is throttled down by 20% down to an 800 Kbps floor. When network health stabilizes (< 1% loss, RTT < 100 ms for 3 consecutive intervals), bandwidth is probed upward.
6. **In-Session Chat & Address Book (`ChatService`, `AddressBookService`)**:
   - Real-time text messaging over `DataChannels.Chat = 7` with message IDs, timestamps, and sender aliases.
   - Address book stores saved host IDs, friendly aliases, favorite flags, and connection history in `%APPDATA%/RemoteDesk/addressbook.json`.

## Consequences
- **Positive**:
  - Full parity with AnyDesk core features: clipboard sync, file transfer, multi-monitor selection, audio, and chat.
  - Video and input responsiveness are protected against large file transfers via flow control.
  - Clean separation: core logic is cross-platform in `RemoteDesk.Core`, while Windows-specific APIs (GDI, WASAPI, Win32 Clipboard) remain isolated in `RemoteDesk.Platform.Windows`.
- **Negative / Trade-offs**:
  - WASAPI loopback requires an active Windows audio rendering endpoint (silent / uninitialized default audio devices will not produce samples until audio is played).
