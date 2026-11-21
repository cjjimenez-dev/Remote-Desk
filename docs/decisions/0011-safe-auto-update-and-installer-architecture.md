# 11. Safe Auto-Update, Signature Verification, and Installer Packaging Architecture

Date: 2026-11-21

## Status

Accepted

## Context

Remote access software runs with elevated privileges on Windows (as `LocalSystem` in Session 0 for unattended access and interactive console tokens for input injection). Consequently, the auto-update and installation mechanisms are critical security boundaries:
1. An unverified update feed or unsigned binary would expose users to man-in-the-middle attacks and privilege escalation.
2. Installing and updating a background Windows Service across unattended hosts requires deterministic service lifecycle management.
3. The desktop Viewer needs seamless update discovery without interrupting active remote sessions.

## Decision

1. **Cryptographic Manifest Verification (`RemoteDesk.Updater.Security`)**:
   - `UpdateSigner`: Uses asymmetric ECDSA P-256 with SHA-256 to sign release manifests (`version:sha256`).
   - The application embeds the trusted release public key.
   - Any update payload with a modified version string, mismatched SHA-256 hash, or invalid signature is rejected prior to execution.

2. **Update Manager Lifecycle (`RemoteDesk.Updater`)**:
   - `UpdateManager`:
     - Checks the update feed endpoint (`/api/updates/check`).
     - Compares semver versions (`Version.TryParse`).
     - Streams downloads in 80 KB chunks with download progress notifications.
     - Validates the local file hash against `Sha256Checksum`.
     - Validates digital signature with `UpdateSigner.VerifyManifestSignature`.
     - Hands over execution to the verified setup executable (`/SILENT`) and exits cleanly.

3. **Management API Update Feed (`RemoteDesk.Server.Api`)**:
   - Provides REST endpoints:
     - `GET /api/updates/check?version={ver}`
     - `GET /api/updates/public-key`
     - `GET /api/updates/releases`
   - Dynamically signs release manifests using the release private key.

4. **WPF Viewer Notification Banner (`RemoteDesk.Viewer`)**:
   - Background update check at application startup.
   - Displays an emerald update notification banner in `MainWindow.xaml` featuring the new version tag, release notes, live download progress, and an instant "⚡ Update Now" action.

5. **Deployment & Service Automation (`deploy/`)**:
   - `deploy/install-service.ps1`: Elevates with administrator privileges, stops existing service instances, registers `RemoteDesk.Host.Service` using `sc.exe create` with `start= auto`, configures automatic recovery actions (`sc.exe failure restart/5000/restart/10000/restart/60000`), and starts the service.
   - `deploy/uninstall-service.ps1`: Gracefully stops and deletes the Windows Service registration.
   - `deploy/build-installer.ps1`: Automates `dotnet publish` for all components (`Viewer`, `Host.Service`, `Host.Agent`, `Updater`, `Server.Api`, `Server.Signaling`), stages deployment scripts, and computes SHA-256 checksums into `SHA256SUMS.txt`.

## Consequences

- Full unattended update safety is guaranteed through dual-layer verification (SHA-256 + ECDSA P-256).
- Complete packaging and service automation enables single-command installation for enterprise fleet rollouts.
