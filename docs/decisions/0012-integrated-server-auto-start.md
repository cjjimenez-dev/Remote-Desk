# 12. Integrated In-Process Server Auto-Start Architecture

Date: 2026-11-21

## Status

Accepted

## Context

In standard enterprise and local deployments of RemoteDesk, the architecture features dedicated backend servers:
1. `RemoteDesk.Server.Signaling` on port 5000 (ASP.NET Core SignalR hub and STUN/TURN credential broker).
2. `RemoteDesk.Server.Api` on port 5001 (Fleet Management REST API and HTML5/Tailwind Web Dashboard).
3. `RemoteDesk.Viewer` (WPF desktop client).

While enterprise multi-node deployments run the signaling hub and fleet manager on centralized Linux/Windows servers, single-user and local testing environments require users to manually start multiple command-line processes before launching the Viewer. The user requested:
> *"can you make the exe will automatically start the servers too once it opened"*

## Decision

1. **WPF In-Process ASP.NET Core Hosting (`RemoteDesk.Viewer.csproj`)**:
   - Added `<FrameworkReference Include="Microsoft.AspNetCore.App" />` to `RemoteDesk.Viewer.csproj`.
   - Explicitly configured `<StartupObject>RemoteDesk.Viewer.App</StartupObject>` to resolve entry-point ambiguity (`CS0017`) when referencing ASP.NET Core Web SDK projects.
   - Added project references to `RemoteDesk.Server.Signaling` and `RemoteDesk.Server.Api`.

2. **Autonomous Server Lifecycle Manager (`IntegratedServerManager`)**:
   - Implemented `RemoteDesk.Viewer.Servers.IntegratedServerManager`:
     - Uses fast non-blocking TCP socket probes (`TcpClient.BeginConnect` with a 200 ms timeout) to detect whether ports 5000 and 5001 are already occupied by external dedicated servers.
     - If port 5000 is open, starts an in-process Kestrel instance hosting the SignalR `SignalingHub` on `http://0.0.0.0:5000/signaling` and the STUN/TURN credential broker on `/api/ice-servers`.
     - If port 5001 is open, starts an in-process Kestrel instance hosting the `FileBackedFleetRepository` REST API endpoints (`/api/fleet/*`), the auto-update manifest feed (`/api/updates/*`), and the HTML5/Tailwind responsive Web Dashboard (`/` and `/dashboard`).
     - If either port is already bound by an external dedicated server, the manager gracefully logs the condition and defers to the existing instance without crashing or throwing port conflict errors.
     - Implements `IAsyncDisposable` to gracefully stop Kestrel web hosts and cancel cancellation tokens on application exit.

3. **ViewModel Integration & HUD Status (`ViewerViewModel`)**:
   - Asynchronously launches the integrated servers on application startup (`InitializeIntegratedServersAsync`) without blocking the WPF dispatcher or UI thread.
   - Exposes dynamic observable properties:
     - `ServerStatusBadge`: Displays `🟢 Local Servers (5000/5001)`, `🟡 Hybrid Server (Local/Ext)`, or `🌐 External Servers Active`.
     - `ServerStatusColor`: Dynamic theme color matching the server state.
   - Added `OpenWebDashboardCommand` which launches `http://localhost:5001/dashboard` in the user's default browser via `Process.Start(..., UseShellExecute = true)`.

4. **UI Presentation (`MainWindow.xaml`)**:
   - Bottom Status Bar: Surfaces the live server status badge alongside a quick-action button `🌐 Web Dashboard` linking directly to the local web dashboard.
   - Fleet Management Drawer: Added a header action button `🌐 Open in Browser` allowing one-click transition from the WPF drawer to the full browser view.

## Consequences

- **Single-Click Out-of-the-Box Experience**: Users can run `RemoteDesk.Viewer.exe` standalone and immediately establish local/remote connections, perform fleet inventory monitoring, and access the web dashboard without launching separate console windows.
- **Enterprise Compatibility**: Seamlessly coexists with dedicated central servers; when external servers are detected on ports 5000/5001, the client automatically defaults to external server mode.
- **Clean Architecture Boundary**: `RemoteDesk.Core` remains pure cross-platform without Web SDK or WPF dependencies; the server hosting logic is encapsulated within `RemoteDesk.Viewer.Servers`.
