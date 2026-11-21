# 9. Remote Terminal with Windows ConPTY

Date: 2026-11-21

## Status

Accepted

## Context

A key differentiating feature for IT and fleet administration in RemoteDesk is a remote command-line terminal. Remote desktop screen-scraping of console windows is bandwidth-heavy, incurs high latency, cannot easily handle font scaling or window resizing cleanly, and requires an interactive desktop session.

A true terminal requires:
1. Low-latency, bidirectional character streaming.
2. Direct integration with the Windows console subsystem without opening visible console windows on the host desktop.
3. Clean VT100 / ANSI escape sequence handling for colored output, cursor positioning, and line editing.
4. Terminal window resizing (`cols` x `rows`) synchronized with remote shell dimensions.
5. Strict permission enforcement (`SessionPermissions.Terminal`).

## Decision

1. **Windows Pseudo Console (ConPTY)**:
   - On Windows 10 (1809+) and Windows 11, we wrap `kernel32.dll` APIs:
     - `CreatePseudoConsole`
     - `ResizePseudoConsole`
     - `ClosePseudoConsole`
     - `InitializeProcThreadAttributeList` / `UpdateProcThreadAttribute` (`PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016`)
     - `CreateProcessW` with `EXTENDED_STARTUPINFO_PRESENT` (0x00080000).
   - Anonymous pipes (`CreatePipe`) provide bidirectional input/output streaming to the pseudo-terminal.
   - Host fallback: if ConPTY fails or runs in a legacy environment, gracefully fall back to `System.Diagnostics.Process` with redirected standard I/O streams.

2. **Protocol Framing on Dedicated Data Channel (`DataChannels.Terminal = 5`)**:
   - Ordered, reliable SCTP data channel.
   - Message types:
     - `TerminalStart = 0x0600` (`TerminalStartMessage`: `SessionId`, `ShellType`, `Columns`, `Rows`)
     - `TerminalOutput = 0x0601` (`TerminalOutputMessage`: `SessionId`, `OutputData`)
     - `TerminalInput = 0x0602` (`TerminalInputMessage`: `SessionId`, `InputData`)
     - `TerminalResize = 0x0603` (`TerminalResizeMessage`: `SessionId`, `Columns`, `Rows`)
     - `TerminalClose = 0x0604` (`TerminalCloseMessage`: `SessionId`, `Reason`)

3. **Security Gate**:
   - Host checks `activePermissions.HasFlag(SessionPermissions.Terminal)` before spawning any shell process.
   - Only approved shells (`powershell.exe`, `cmd.exe`) can be launched.
   - Terminating or disconnecting the remote session immediately kills the child process tree (`proc.Kill(entireProcessTree: true)`) and closes pipes and ConPTY handles.

4. **ANSI Parser & Terminal Buffer (`RemoteDesk.Core.Terminal`)**:
   - Pure C# cross-platform components:
     - `AnsiTerminalParser`: Strips VT100 escape codes or tokenizes into colored spans (`AnsiSpan`).
     - `TerminalBuffer`: Rolling buffer (up to 2000 lines) with carriage return (`\r`), newline (`\n`, `\r\n`), backspace (`\b`), and screen clear (`\x1b[2J`) processing.

5. **WPF Viewer Terminal Drawer**:
   - Integrated dark-themed monospace terminal panel in `MainWindow.xaml`.
   - Shell dropdown selector (PowerShell / Cmd).
   - Start / Stop / Clear controls.
   - Textbox input with `KeyBinding Key="Return"` for instant command execution.
   - Focus protection: `OnWindowKeyDown` ignores keystrokes originating from `TextBox` so typing in terminal/chat does not trigger remote desktop input injection.

## Consequences

- IT administrators can perform out-of-band maintenance, inspect processes, and run automation scripts with minimal bandwidth consumption.
- The Core protocol remains completely platform-independent, while Windows-specific ConPTY code is strictly confined to `RemoteDesk.Platform.Windows`.
