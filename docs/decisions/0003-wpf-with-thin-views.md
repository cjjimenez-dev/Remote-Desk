# 3. WPF with Thin MVVM Views for Windows-First Viewer

Date: 2026-11-20

## Status

Accepted

## Context

RemoteDesk is a Windows-first application targeting Windows 10 and 11.
We need a viewer UI framework that:
- Seamlessly integrates with Windows desktop APIs, direct DirectX/D3D rendering (`D3DImage`), and HWND hosting.
- Has high performance and mature tooling.
- Allows modern reactive architecture while isolating UI presentation from session and streaming logic.

## Decision

We use WPF (.NET 10) paired with `CommunityToolkit.Mvvm` for the Windows Viewer.
To keep the presentation layer replaceable:
- Views (XAML and code-behind) are strictly thin wrappers over ViewModels.
- Business logic, session management, protocol decoding, and transport handling live in ViewModels, Core, and Services—never in view code-behind.
- Video frame rendering is abstracted behind an `IVideoRenderer` interface.

## Consequences

- Maximum native Windows performance and reliability for v1.
- If cross-platform support (Avalonia or MAUI) is implemented for macOS or Linux viewers in the future, the ViewModels, Core protocol, and transport layers can be reused with minimal rework.
