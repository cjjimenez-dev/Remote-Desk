# 7. Windows Service Mode, Session 0 Isolation, and Named-Pipe IPC

Date: 2026-11-21

## Status
Accepted

## Context
In Windows Vista and newer (including Windows 10/11), Windows Services execute in **Session 0**, which is isolated from interactive user desktops (Session 1, 2, etc.). A service running as `SYSTEM` in Session 0 cannot directly capture user displays or receive interactive mouse/keyboard input via standard GDI/DXGI or `SendInput`.

To provide unattended remote access across system reboots, user logons/logoffs, workstation lockouts, and elevated User Account Control (UAC) prompts, RemoteDesk requires a multi-process architecture:
1. `RemoteDesk.Host.Service`: Runs as `SYSTEM` in Session 0 as a background Windows Service (`Microsoft.Extensions.Hosting.WindowsServices`).
2. `RemoteDesk.Host.Agent`: Runs in the active user console session on `WinSta0\Default` (or `WinSta0\Winlogon` for secure desktop prompts).
3. A secure, low-latency Inter-Process Communication (IPC) channel between the Service and Agent.
4. Detection of Secure Desktop transitions and simulation of the Software Attention Sequence (Ctrl + Alt + Del).

## Decision

1. **Process Separation**:
   - Split `RemoteDesk.Host` into two focused binaries:
     - `RemoteDesk.Host.Service`: Manages device identity (`DpapiDeviceKeyStore` with `DataProtectionScope.LocalMachine`), maintains the persistent SignalR signaling connection, listens for session notifications, and supervises the Agent.
     - `RemoteDesk.Host.Agent`: The user-mode desktop engine. Performs screen capture, real-time H.264 encoding, input injection, and WebRTC peer connection.
2. **Interactive Console Launch via Win32 Token Duplication**:
   - `SessionProcessLauncher` detects the active console session ID via `WTSGetActiveConsoleSessionId`.
   - Obtains the primary user token via `WTSQueryUserToken` and `DuplicateTokenEx`.
   - If the system is at the lock or login screen (no interactive user logged on), the service duplicates the SYSTEM token with `TokenSessionId` set to the console session.
   - Launches `RemoteDesk.Host.Agent.exe` with `CreateProcessAsUser`, configuring `STARTUPINFO.lpDesktop = "WinSta0\\Default"` (or `"WinSta0\\Winlogon"`).
3. **Named Pipe IPC with Restricted DACL**:
   - `NamedPipeIpcServer` and `NamedPipeIpcClient` provide bidirectional, asynchronous message passing over `\\.\pipe\RemoteDesk_Service_Agent_Ipc`.
   - A custom `PipeSecurity` DACL is configured granting `FullControl` to `LocalSystem` and `BuiltinAdministrators`, and `ReadWrite` to `AuthenticatedUserSid`.
   - Messages are length-prefixed and serialized using `MessagePack`.
4. **Secure Desktop & UAC Elevation**:
   - `DesktopDetector` polls the active input desktop using `OpenInputDesktop` and `GetUserObjectInformation(..., UOI_NAME)`.
   - When the desktop switches to `"Winlogon"` (e.g. UAC elevation prompt or lock screen), `Host.Service` spawns a SYSTEM-level agent onto `WinSta0\Winlogon`.
5. **Ctrl + Alt + Del (SAS) Simulation**:
   - `SasSimulator` triggers the secure attention sequence using Win32 `SendSAS(false)` from `sas.dll`, with a fallback to software `SendInput` keystrokes.

## Consequences
- **Positive**:
  - RemoteDesk can run before user login, survive reboots, and remain connected across user switch and logout events.
  - Remote operators can view and interact with UAC elevation prompts on the secure desktop.
  - Device identity is pinned to the physical machine (`LocalMachine` DPAPI scope) rather than an individual Windows user profile.
- **Negative / Trade-offs**:
  - Requires local administrator privileges to install and run the Windows Service (`sc.exe create RemoteDesk.Host.Service binPath= ...`).
  - Spawning processes into Session 1 from Session 0 requires careful handle management (`CloseHandle` on tokens, environments, and process/thread handles).
