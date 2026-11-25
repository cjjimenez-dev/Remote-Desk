# RemoteDesk

A self-contained Windows remote desktop application built with .NET 10, WPF, WebRTC (`SIPSorcery`), FFmpeg H.264 video streaming, and SignalR — featuring an AnyDesk-style dual Host/Viewer workflow, automatic LAN discovery, and zero-config Cloudflare Quick Tunnels for remoting across the internet.

---

## Key Features

- **Unified Host & Viewer (`RemoteDesk.Viewer`)** — Launches with embedded SignalR signaling (`:5000`), Fleet API/dashboard (`:5001`), and background `HostSessionServer` so any instance can share or control desktops immediately.
- **Zero-Config LAN Discovery** — Automatically discovers peers on the same Wi-Fi/LAN over UDP (`5004`) and offers 1-click connection or automatic ID-to-IP routing.
- **Built-In Cloudflare Internet Remoting** — Embeds `cloudflared` Quick Tunnels (`*.trycloudflare.com`) directly in the app to generate a shareable `DeviceId@tunnel-host.trycloudflare.com` Internet ID with no account, port forwarding, or paid VPS required.
- **Hardware-Accelerated Capture & Low-Latency Streaming** — DXGI Desktop Duplication with GDI fallback (`RemoteDesk.Platform.Windows`) and FFmpeg H.264 encoding/decoding over WebRTC data channels.
- **Cryptographic Identity & Anti-MITM Protection** — ECDSA (`nistP256`) device identities stored via DPAPI (`DpapiDeviceKeyStore`), signed connection requests, and signed SDP DTLS fingerprint verification (`DtlsFingerprintProtector`).
- **Session Tools** — Mouse/keyboard/Unicode input injection, emergency kill switch (`Ctrl+Alt+Shift+Esc`), multi-monitor selection, adaptive quality presets, drag-and-drop / chunked file transfer, clipboard sync, WASAPI audio playback, chat, remote terminal (ConPTY), and saved address book.
- **Light & Dark Themes** — Clean Light theme by default with live header toggle (`🌙` / `☀️`) to Dark theme.

---

## Solution Structure

| Project | Description |
|---|---|
| `src/RemoteDesk.Core` | Protocol envelopes (`MessagePack`), security/ECDSA identity, TURN credential generator, quality presets, endpoint parser |
| `src/RemoteDesk.Platform.Windows` | DXGI/GDI screen capture, FFmpeg H.264 encoder/decoder, Win32 input injection, clipboard, WASAPI audio, ConPTY, Named Pipe IPC |
| `src/RemoteDesk.Transport.WebRtc` | WebRTC (`SIPSorcery`) P2P transport, ICE buffering, chunked video framing, SignalR `SignalingClient` |
| `src/RemoteDesk.Server.Signaling` | Standalone SignalR signaling hub & `/api/ice-servers` broker |
| `src/RemoteDesk.Server.Api` | Fleet management REST API & web dashboard |
| `src/RemoteDesk.Viewer` | WPF desktop client (Light/Dark themes, embedded signaling/API/host engine, LAN discovery, Cloudflare tunnel orchestration) |
| `src/RemoteDesk.Host` | Standalone headless console host |
| `src/RemoteDesk.Host.Service` | Windows Service host for unattended Session 0 / elevated input (`SendSAS`) |
| `src/RemoteDesk.Host.Agent` | Session-level desktop agent paired with `Host.Service` via Named Pipes |
| `src/RemoteDesk.Updater` | Signed update manifest verification and handover |
| `tests/RemoteDesk.Core.Tests` | xUnit unit and WebRTC loopback integration test suite |

---

## Prerequisites

- **OS:** Windows 10/11 (x64, `net10.0-windows10.0.19041.0`)
- **SDK:** [.NET 10 SDK](https://dotnet.microsoft.com/)
- **Native FFmpeg DLLs** (for H.264 encode/decode at runtime): FFmpeg 8/9 shared build (`avcodec-63.dll`, etc. — discovered from `./ffmpeg`, app base directory, `PATH`, or WinGet `Gyan.FFmpeg.Shared`)
- **Optional for packaging:** WinRAR (`C:\Program Files\WinRAR\Rar.exe`) for `.rar` archive generation

---

## Quick Start

### 1. Build & Run Tests

```powershell
dotnet build RemoteDesk.slnx -c Release
dotnet test RemoteDesk.slnx -c Release
```

### 2. Run the Desktop App (Viewer + Host)

```powershell
dotnet run --project src/RemoteDesk.Viewer/RemoteDesk.Viewer.csproj -c Release
```

- **Local Wi-Fi / LAN:** Open RemoteDesk on both PCs, click **⚡ Connect to this LAN PC** in the banner (or enter the 6-digit partner ID and click **Connect**), then approve the incoming access prompt on the host PC.
- **Over the Internet (Cloudflare Quick Tunnel):** On the host PC, click **☁️ Enable Internet Remoting (Cloudflare)**, copy the generated `ID@*.trycloudflare.com` address, paste it into **Remote Desk** on the remote PC, and click **Connect**.

### 3. Build Self-Contained Portable Package

```powershell
powershell -ExecutionPolicy Bypass -File deploy\build-installer.ps1
```

Outputs are generated in `artifacts/` (gitignored):
- `artifacts/RemoteDesk.exe` — Native launcher executable
- `artifacts/dist/` — Self-contained `win-x64` binaries, FFmpeg DLLs, `cloudflared.exe`, `ALLOW-FIREWALL.bat`, and `SHA256SUMS.txt`
- `artifacts/RemoteDesk-v1.0-Windows.zip` and `artifacts/RemoteDesk-v1.0-Windows.rar`

---

## Ports & Deployment

| Port | Protocol | Service |
|---|---|---|
| `5000` | TCP / WebSocket | SignalR Signaling Hub (`/signaling`) & `/api/ice-servers` |
| `5001` | TCP / HTTP | Fleet Management API & Web Dashboard (`/dashboard`) |
| `5004` | UDP | LAN Auto-Discovery Broadcast |

For self-hosting signaling + `coturn` TURN relay across strict/symmetric NATs, see [deploy/vps/README.md](deploy/vps/README.md), [deploy/docker-compose.cloudflare.yml](deploy/docker-compose.cloudflare.yml), and [docs/decisions/0015-cloudflare-and-vps-internet-remoting.md](docs/decisions/0015-cloudflare-and-vps-internet-remoting.md).

---

## Architecture & Security Docs

- [Protocol Specification](docs/protocol.md)
- [Threat Model](docs/threat-model.md)
