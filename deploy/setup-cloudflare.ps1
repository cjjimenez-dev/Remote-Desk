<#
.SYNOPSIS
    Sets up Cloudflare Tunnel for RemoteDesk (Signaling + TURN server)
.DESCRIPTION
    Creates a free Cloudflare Tunnel that exposes:
    - Port 5000: Signaling Server (WebSocket)
    - Port 3478: TURN UDP/TCP
    - Port 5349: TURNS (TLS)
    - Port 443: TURNS Fallback (HTTPS)
.NOTES
    Run this on your HOME PC (the one that stays on 24/7)
    Requires: Windows, admin rights for firewall, free Cloudflare account
#>

param(
    [string]$TunnelName = "remotedesk",
    [string]$SignalingPort = "5000",
    [string]$TurnUdpPort = "3478",
    [string]$TurnTcpPort = "3478",
    [string]$TurnTlsPort = "5349",
    [string]$TurnFallbackPort = "443"
)

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "  RemoteDesk - Cloudflare Tunnel Setup" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host ""

# Check admin
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Warning "Please run PowerShell as Administrator for firewall rules"
    Write-Host "Re-run: Start-Process powershell -Verb RunAs -ArgumentList '-File $PSCommandPath'"
    exit 1
}

# 1. Install cloudflared if not present
Write-Host "[1/6] Checking cloudflared..." -ForegroundColor Yellow
$cloudflared = Get-Command cloudflared -ErrorAction SilentlyContinue
if (-not $cloudflared) {
    Write-Host "Installing cloudflared via winget..." -ForegroundColor Yellow
    winget install --id Cloudflare.cloudflared --source winget --accept-source-agreements --accept-package-agreements
    $cloudflared = Get-Command cloudflared -ErrorAction SilentlyContinue
    if (-not $cloudflared) {
        Write-Error "Failed to install cloudflared. Install manually: https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/"
        exit 1
    }
}
Write-Host "  ✓ cloudflared found: $($cloudflared.Source)" -ForegroundColor Green

# 2. Authenticate with Cloudflare
Write-Host "[2/6] Authenticating with Cloudflare..." -ForegroundColor Yellow
Write-Host "  A browser window will open - log in to your free Cloudflare account" -ForegroundColor Gray
try {
    & cloudflared tunnel login
    Write-Host "  ✓ Authenticated" -ForegroundColor Green
}
catch {
    Write-Error "Authentication failed. Run 'cloudflared tunnel login' manually first."
    exit 1
}

# 3. Create tunnel (or use existing)
Write-Host "[3/6] Creating tunnel '$TunnelName'..." -ForegroundColor Yellow
$tunnelOutput = & cloudflared tunnel create $TunnelName 2>&1
if ($LASTEXITCODE -ne 0) {
    if ($tunnelOutput -like "*already exists*") {
        Write-Host "  Tunnel already exists, continuing..." -ForegroundColor Yellow
    }
    else {
        Write-Error "Failed to create tunnel: $tunnelOutput"
        exit 1
    }
}
else {
    Write-Host "  ✓ Tunnel created" -ForegroundColor Green
}

# 4. Get tunnel ID
$tunnelInfo = & cloudflared tunnel list | Select-String $TunnelName
if (-not $tunnelInfo) {
    Write-Error "Could not find tunnel ID"
    exit 1
}
$tunnelId = ($tunnelInfo -split '\s+')[0]
Write-Host "  Tunnel ID: $tunnelId" -ForegroundColor Cyan

# 5. Create config file
Write-Host "[4/6] Creating tunnel configuration..." -ForegroundColor Yellow
$configDir = "$env:USERPROFILE\.cloudflared"
$configPath = "$configDir\config.yml"
New-Item -ItemType Directory -Force -Path $configDir | Out-Null

$config = @"
tunnel: $tunnelId
credentials-file: $configDir\$tunnelId.json

ingress:
  # Signaling Server (WebSocket) - HTTP/HTTPS
  - hostname: $TunnelName.trycloudflare.com
    service: http://localhost:$SignalingPort
    originRequest:
      httpHostHeader: localhost:$SignalingPort

  # TURN UDP - requires Cloudflare Spectrum (paid) or use TCP/TLS instead
  # Note: Free Cloudflare Tunnel doesn't proxy UDP. Use TCP/TLS ports below.

  # TURN TCP (plain) - for clients that support TCP TURN
  - hostname: turn-$TunnelName.trycloudflare.com
    service: tcp://localhost:$TurnTcpPort

  # TURNS (TLS on 5349) - RECOMMENDED for WebRTC
  - hostname: turns-$TunnelName.trycloudflare.com
    service: tcp://localhost:$TurnTlsPort

  # TURNS Fallback (TLS on 443) - works through corporate proxies
  - hostname: turns443-$TunnelName.trycloudflare.com
    service: tcp://localhost:$TurnFallbackPort

  # Catch-all (required)
  - service: http_status:404
"@

Set-Content -Path $configPath -Value $config -Encoding UTF8
Write-Host "  ✓ Config written to $configPath" -ForegroundColor Green

# 6. Configure Windows Firewall
Write-Host "[5/6] Configuring Windows Firewall..." -ForegroundColor Yellow
$ports = @($SignalingPort, $TurnTcpPort, $TurnTlsPort, $TurnFallbackPort)
foreach ($port in $ports) {
    $ruleName = "RemoteDesk-Tunnel-Port-$port"
    if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $port -Action Allow -Profile Any
        Write-Host "  ✓ Opened TCP port $port" -ForegroundColor Green
    }
}
# UDP for local TURN (if running coturn locally)
$udpRule = "RemoteDesk-Tunnel-UDP-$TurnUdpPort"
if (-not (Get-NetFirewallRule -DisplayName $udpRule -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $udpRule -Direction Inbound -Protocol UDP -LocalPort $TurnUdpPort -Action Allow -Profile Any
    Write-Host "  ✓ Opened UDP port $TurnUdpPort" -ForegroundColor Green
}

# 7. Create run script
Write-Host "[6/6] Creating launcher script..." -ForegroundColor Yellow
$runScript = @"
@echo off
title RemoteDesk Cloudflare Tunnel
echo ==================================================
echo   RemoteDesk Cloudflare Tunnel Active
echo ==================================================
echo.
echo Public Signaling URL: https://$TunnelName.trycloudflare.com
echo.
echo In RemoteDesk Viewer, set SERVER to:
echo   $TunnelName.trycloudflare.com
echo.
echo Keep this window OPEN while remoting over the internet.
echo.
cloudflared tunnel run --config $configPath $TunnelName
pause
"@

$runPath = "$PSScriptRoot\run-cloudflare-tunnel.bat"
Set-Content -Path $runPath -Value $runScript -Encoding ASCII
Write-Host "  ✓ Launcher: $runPath" -ForegroundColor Green

# Summary
Write-Host ""
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "  SETUP COMPLETE!" -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "YOUR PUBLIC SIGNALING URL (enter this in Viewer/Host):" -ForegroundColor Yellow
Write-Host ""
Write-Host "  In RemoteDesk Viewer, enter in the SERVER box:" -ForegroundColor Cyan
Write-Host "    $TunnelName.trycloudflare.com" -ForegroundColor Green
Write-Host "  (or full URL: https://$TunnelName.trycloudflare.com)" -ForegroundColor Gray
Write-Host ""
Write-Host "NEXT STEPS:" -ForegroundColor Yellow
Write-Host "  1. Run the tunnel:  .\run-cloudflare-tunnel.bat" -ForegroundColor White
Write-Host "  2. Start Signaling Server (or let Viewer auto-start it on port 5000)" -ForegroundColor White
Write-Host "  3. On both remote PCs, set SERVER to: $TunnelName.trycloudflare.com" -ForegroundColor White
Write-Host "  4. Enter the remote Desk ID and click Connect!" -ForegroundColor White
Write-Host ""
Write-Host "NOTES:" -ForegroundColor Cyan
Write-Host "  • Free Cloudflare Tunnel securely handles Signaling (SignalR WebSockets) with zero port forwarding." -ForegroundColor Gray
Write-Host "  • RemoteDesk automatically uses Google & Cloudflare STUN for direct P2P connections." -ForegroundColor Gray
Write-Host "  • For restrictive firewalls/symmetric NAT, deploy coturn to a cheap VPS (see deploy/vps/README.md)." -ForegroundColor Gray
Write-Host ""