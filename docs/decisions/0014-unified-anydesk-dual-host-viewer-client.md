# ADR 0014: Unified Dual-Role Host/Viewer Client Architecture

## Context
Originally, RemoteDesk separated the application into `RemoteDesk.Host` (the headless capture/injection service) and `RemoteDesk.Viewer` (the WPF desktop client for viewing partner screens).
However, modern remote desktop applications like AnyDesk and TeamViewer operate as a unified, dual-role single executable:
1. When launched on any machine, the client displays **"This Desk" (Your ID)** and registers itself with the signaling server to receive incoming connections.
2. Simultaneously, it displays **"Remote Desk" (Partner ID)**, enabling the user to enter a remote partner ID and initiate an outgoing remote control session.
3. Users expect to copy the same executable or zip package to both computers (Device A and Device B) and run them identically without having to configure different roles or launch separate background services manually.

## Decision
We integrated an in-process background host session server directly into `RemoteDesk.Viewer`:
1. **Embedded Background Host Engine (`HostSessionServer`)**:
   - `ViewerViewModel` instantiates and manages `HostSessionServer` bound to its own unique `LocalDeviceId`.
   - `HostSessionServer` connects to the signaling server (`/hubs/signaling`), registers its ID, and listens for `ConnectionRequested`.
   - When an incoming connection request is received, it triggers UI notifications (Accept / Reject banner with timeout or Auto-Accept toggle).
   - Upon acceptance, it spins up an incoming `WebRtcTransport` (`isInitiator: false`), exchanges SDP offer/answer, completes ICE discovery, and launches DXGI desktop capture + FFmpeg H.264 streaming with low-latency hardware input injection.
2. **Dynamic Signaling Server Configuration**:
   - Added an editable `SERVER:` capsule input in the header bar of `MainWindow.xaml`.
   - On LAN or cross-machine setups, one PC (or a dedicated machine) runs the integrated signaling server, and both PCs point their `SERVER:` address to that IP (e.g. `192.168.1.100` or `my-server.com`).
3. **Emil Kowalski Design Language**:
   - Overhauled UI styling with dark obsidian palette (`#09090B`), subtle 1px border highlights (`#27272A`), tactile interactive micro-interactions, elevated modal access prompts, and live streaming indicator banners with quick disconnect buttons.
4. **Single Executable Deployment**:
   - The installer/zip bundles `RemoteDesk.Viewer.exe` alongside the integrated background server manager (`IntegratedServerManager.cs`), allowing the application to serve as an all-in-one client or standalone host on any device.

## Consequences
- **Positive**:
  - Seamless AnyDesk-like user experience: Same `.exe` runs on any number of machines.
  - No complex command line setup or manual service installs needed for basic remote desktop operations.
  - Flexible networking: Works over localhost, LAN, VPN (Tailscale/ZeroTier), or public server URLs.
- **Trade-offs**:
  - Unattended access requiring UAC elevation before login still benefits from the dedicated Windows Service (`RemoteDesk.Host.exe` registered via Service Control Manager), but ad-hoc screen sharing and user-prompted control work instantly out of the box in `RemoteDesk.Viewer.exe`.
