# 13. Brand Identity, Application Icon, and Emil Kowalski Design System

Date: 2026-11-21

## Status

Accepted

## Context

Previous iterations focused on high-performance network framing, DXGI/FFmpeg streaming, low-latency input, ConPTY, Fleet management, and in-process server auto-start. The user requested:
> *"add logo on icon and make the design better use Emil skill design"*

Prior to this change:
1. The application lacked a custom icon and logo, displaying the default Windows generic executable icon.
2. The UI relied on standard flat gray backgrounds (`#18181B` / `#27272A`), harsh borders, and plain buttons.

## Decision

1. **Brand Identity & Multi-Resolution Application Icon (`assets/`)**:
   - Designed a modern geometric RemoteDesk symbol:
     - Deep obsidian squircle badge with an Electric Indigo (`#6366F1`) to Vivid Cyan (`#06B6D4`) gradient rim.
     - Two interconnected display frames (Host and Viewer) bridged by an ultra-fast radiant connection wave / lightning pulse (`#38BDF8` / `#FFFFFF`).
     - Live emerald status beacon (`#10B981`) with soft glow ring.
   - Built a high-DPI generator rendering pixel-perfect vector visuals across 256x256, 128x128, 64x64, 48x48, 32x32, and 16x16.
   - Produced `assets/icon.ico`, `assets/logo.png`, `assets/logo-64.png`, and `assets/logo-32.png`.
   - Wired `<ApplicationIcon>assets\icon.ico</ApplicationIcon>` into `RemoteDesk.Viewer.csproj` and set `Icon="assets/icon.ico"` in `MainWindow.xaml`.

2. **Emil Kowalski Design Philosophy Implementation**:
   - **Surface & Depth Hierarchy**:
     - Base canvas: Pure deep dark obsidian (`#09090B`).
     - App header & status bar: Subtly elevated `#0C0C0F` with delicate 1px boundary lines (`#1F1F26`).
     - Control capsules & cards: `#121216` / `#16161D` with subtle 1px borders (`#22222A` / `#252532`).
     - Floating modal dialogs (Fleet, Address Book, ConPTY Terminal): Elevated `#111116` surfaces with deep 36-40px drop shadows (`Opacity="0.75"`), rounded corners (`CornerRadius="12"`), and dark backdrop dimming.
   - **Tactile Buttons & Micro-Interactions**:
     - Reusable WPF ControlTemplates with `CornerRadius="6"`, 1px borders, and soft hover/pressed feedback:
       - `EmilHeaderButton`: Translucent `#18181D` surface with `#2A2A34` border.
       - `EmilConnectButton`: Tactile emerald (`#059669` / `#10B981`) with amber connecting pulse.
       - `EmilAccentButton`: Vivid sky blue (`#0284C7` / `#38BDF8`).
   - **Branding Header**:
     - Displays the vector RemoteDesk logo icon (24x24) next to high-contrast `#F4F4F5` title typography.
     - Refined `PRO v1.0` pill badge.
     - Compact `MY ID` device capsule with Cascadia/Consolas monospace font.
     - Inset `HOST ID` input with subtle focus glow.
   - **Minimalist Inactive Canvas State**:
     - Centered squircle logo badge with soft indigo radial glow.
     - Crisp typography hierarchy: "Ultra-Fast Remote Desktop" heading in `#F4F4F5`, muted description in `#71717A`.
     - Linear-style keyboard shortcut pills: `[⚡ Connect]`, `[📖 Saved PCs]`, `[🛑 Ctrl+Alt+Shift+Esc]`.
   - **Floating Telemetry HUD Overlay**:
     - Frosted glass container (`#E60D0D11`) with 24px drop shadow and chip metrics for security, connection path, FPS, latency, bitrate, timings, and resolution.
   - **Web Dashboard Overhaul (`IntegratedServerManager.cs`)**:
     - Refreshed HTML5/Tailwind dashboard at `http://localhost:5001/dashboard`:
     - Deep `#09090B` theme with `bg-[#121215]` cards and `border-white/[0.08]`.
     - Pulsing green live ping animation.
     - Tactile refresh button with `F5` keyboard pill.
     - Modern KPI cards and responsive monospace table.

3. **IPC Disposed Semaphore Hardening**:
   - Guarded `_sendLock.Release()` in `NamedPipeIpcServer` and `NamedPipeIpcClient` with `catch (ObjectDisposedException)` to ensure background teardown never throws unhandled exceptions during rapid test execution.

## Consequences

- The application presents a modern, cohesive, designer-crafted visual aesthetic with clear visual hierarchy, tactile controls, and an unmistakable brand icon in Windows Explorer, the taskbar, and the window title bar.
- Retains 100% test passing rate (68/68 tests) and 0 compilation errors across all projects.
