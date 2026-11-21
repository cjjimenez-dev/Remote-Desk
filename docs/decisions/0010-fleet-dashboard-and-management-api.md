# 10. Fleet Dashboard and Management API

Date: 2026-11-21

## Status

Accepted

## Context

A primary differentiating feature for IT administration in RemoteDesk is a centralized fleet dashboard. Without a fleet registry, operators must manually exchange device IDs, cannot see whether remote workstations are online, idle, or in session, and cannot push administrative commands (such as locking a workstation or triggering a reboot).

To meet this goal, the solution requires:
1. Centralized device registry and hardware telemetry collection (OS, CPU, RAM, local IP, tags).
2. Presence and heartbeat tracking (online, in-session, offline detection via timeouts).
3. Out-of-band remote command dispatch and status reporting (`LockWorkstation`, `Reboot`, `RestartAgent`, `GetDiagnostics`).
4. Session and administrative audit logging.
5. Dual dashboard interfaces:
   - Built-in WPF Fleet Dashboard drawer inside `RemoteDesk.Viewer`.
   - Embedded responsive web dashboard served directly by `RemoteDesk.Server.Api` for browser access.

## Decision

1. **Fleet Models & Client Service (`RemoteDesk.Core`)**:
   - `DeviceRecord`, `DeviceStatus` (`Offline`, `Online`, `InSession`), `FleetCommand`, `FleetCommandType`, `FleetCommandStatus`, `FleetAuditRecord`, and `FleetSummary`.
   - `FleetClientService`: Cross-platform HTTP client for device registration, heartbeats, command queuing, polling, and audit logs.

2. **Management API & Repository (`RemoteDesk.Server.Api`)**:
   - ASP.NET Core service listening on port 5001.
   - `IFleetRepository` with `FileBackedFleetRepository`:
     - Thread-safe memory cache persisted to `fleet_store.json`.
     - Automatic stale status detection: machines that fail to heartbeat within 60 seconds transition to `DeviceStatus.Offline`.
   - REST Endpoints:
     - `GET /api/fleet/summary`: Aggregate counts (total, online, in-session, offline).
     - `GET /api/fleet/devices`: Search and filter by tag or text query.
     - `POST /api/fleet/devices/register`: Host registration with hardware/OS telemetry.
     - `POST /api/fleet/devices/{id}/heartbeat`: Periodic heartbeat.
     - `POST /api/fleet/devices/{id}/commands`: Queue remote administrative command.
     - `GET /api/fleet/devices/{id}/commands/pending`: Agent command polling.
     - `POST /api/fleet/commands/{id}/result`: Command execution status update.
     - `GET /api/fleet/audit` & `POST /api/fleet/audit`: Central audit trail.
   - Embedded Web Dashboard: Self-contained dark-themed HTML5/Tailwind dashboard at `/` and `/dashboard` with auto-refresh every 5 seconds.

3. **Telemetry & Win32 Command Execution (`RemoteDesk.Platform.Windows.Fleet`)**:
   - `WindowsSystemTelemetry`: Queries `kernel32.dll` `GlobalMemoryStatusEx` for total physical memory, registry for CPU processor model, and `NetworkInterface` for active IPv4.
   - `WindowsFleetCommandHandler`: Dispatches `user32.dll` `LockWorkStation()`, `shutdown.exe` for scheduled reboots, and agent process restart.

4. **Host Agent Integration (`RemoteDesk.Host.Agent`)**:
   - On startup, publishes telemetry to Fleet API and registers host.
   - Background loop every 10s sends heartbeats, polls pending commands, executes them via `WindowsFleetCommandHandler`, and reports execution results.
   - Updates status dynamically to `InSession` upon viewer connection and `Online` upon session end.

5. **WPF Viewer Integration (`RemoteDesk.Viewer`)**:
   - Added "🏢 Fleet" button to the main toolbar.
   - Interactive dark modal with 4 metric cards (Total, Online, In Session, Offline), search bar, status filters, and device inventory list.
   - One-click actions: "⚡ Connect" (instantly initiates desktop remote control) and "💻 Terminal" (connects and opens ConPTY terminal drawer).

## Consequences

- Full IT fleet visibility is available without third-party cloud dependencies.
- Dual interface support ensures IT operators can manage devices either inside the desktop Viewer app or through a web browser.
- The Core protocol remains cross-platform while Windows-specific command execution is isolated in `RemoteDesk.Platform.Windows`.
