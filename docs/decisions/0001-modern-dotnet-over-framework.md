# 1. Choose Modern .NET over .NET Framework

Date: 2026-11-20

## Status

Accepted

## Context

RemoteDesk requires high-performance screen capture, real-time video encoding/streaming, low-latency asynchronous networking, and modern Windows API integration (WinRT Windows.Graphics.Capture, DirectX/DXGI desktop duplication, named pipes, etc.).

Historically, Windows desktop tools were built on .NET Framework 4.8. However:
- .NET Framework 4.8 only receives critical maintenance and security updates.
- Performance enhancements in runtime internals (Span<T>, Memory<T>, vectorization, P/Invoke source generators, asynchronous socket pipelines) are exclusive to modern .NET (.NET 8/10+).
- Modern .NET provides native support for `net10.0-windows10.0.19041.0` TFM, unlocking direct projection of WinRT APIs (Windows.Graphics.Capture, Windows.Graphics.DirectX) without legacy WinRT component wrappers.

## Decision

We target modern .NET (.NET 10 LTS / .NET 8 LTS) with C# 13/14 for all components:
- Core libraries without Windows dependencies target `net10.0`.
- Windows-specific libraries and host/viewer targets use `net10.0-windows10.0.19041.0` (or newer).
- ASP.NET Core SignalR signaling server targets `net10.0`.

## Consequences

- Direct access to high-performance zero-copy memory primitives (`ReadOnlyMemory<byte>`, `ArrayPool<byte>`).
- Direct access to modern Windows 10/11 WinRT screen capture APIs.
- Clean cross-compilation pathway if viewers are expanded to other platforms in the future (e.g. Avalonia).
- Requires distributing or self-contained packaging of the .NET runtime with installers (via Velopack or WiX).
