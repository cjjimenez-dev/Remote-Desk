<#
.SYNOPSIS
    Runs RemoteDesk locally with Cloudflare Tunnel (Signaling + TURN + coturn)
.DESCRIPTION
    Starts all services locally and exposes them via Cloudflare Tunnel.
    Run this AFTER running setup-cloudflare.ps1 once.
#>

param(
    [string]$TunnelName = "remotedesk"
)

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "  RemoteDesk - Local Stack with Cloudflare Tunnel" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host ""

# Check for .env.cloudflare
$envFile = "$PSScriptRoot\.env.cloudflare"
if (-not (Test-Path $envFile)) {
    Write-Warning "No .env.cloudflare found. Copy .env.cloudflare.example and edit it."
    Write-Host "  cp $PSScriptRoot\.env.cloudflare.example $envFile" -ForegroundColor Gray
    exit 1
}

# Load env
$envContent = Get-Content $envFile
foreach ($line in $envContent) {
    if ($line -match '^([^#=]+)=(.*)$') {
        $name = $matches[1].Trim()
        $value = $matches[2].Trim()
        Set-Item -Path "Env:$name" -Value $value
    }
}

if (-not $env:TURN_SECRET -or $env:TURN_SECRET -eq "your-super-secret-turn-key-change-this-2026") {
    Write-Error "Please edit .env.cloudflare and set a real TURN_SECRET"
    exit 1
}

$tunnelName = $env:TUNNEL_NAME ?? $TunnelName
Write-Host "Tunnel: $tunnelName" -ForegroundColor Cyan
Write-Host "TURN Secret: [hidden]" -ForegroundColor Cyan
Write-Host ""

# Check Docker
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Error "Docker not found. Install Docker Desktop: https://docker.com"
    exit 1
}

# 1. Start Docker Compose stack
Write-Host "[1/3] Starting Docker stack (signaling + coturn)..." -ForegroundColor Yellow
docker compose -f "$PSScriptRoot\docker-compose.cloudflare.yml" --env-file "$envFile" up -d --build

if ($LASTEXITCODE -ne 0) {
    Write-Error "Docker compose failed"
    exit 1
}
Write-Host "  ✓ Services starting..." -ForegroundColor Green

# Wait for services
Write-Host "  Waiting for services to be ready..." -ForegroundColor Gray
Start-Sleep 5

# 2. Check services
Write-Host "[2/3] Checking services..." -ForegroundColor Yellow
try {
    $sig = Invoke-RestMethod -Uri "http://localhost:5000/signaling/negotiate?negotiateVersion=1" -Method Post -TimeoutSec 5 -ErrorAction Stop
    Write-Host "  ✓ Signaling Server: OK" -ForegroundColor Green
}
catch {
    Write-Warning "  Signaling server not ready yet (may need more time)"
}

# 3. Start Cloudflare Tunnel
Write-Host "[3/3] Starting Cloudflare Tunnel..." -ForegroundColor Yellow
Write-Host ""
Write-Host "  Public Signaling URL:" -ForegroundColor Cyan
Write-Host "    https://$tunnelName.trycloudflare.com" -ForegroundColor White
Write-Host ""
Write-Host "  Configure RemoteDesk Viewer on both PCs:" -ForegroundColor Yellow
Write-Host "    SERVER: $tunnelName.trycloudflare.com" -ForegroundColor Green
Write-Host ""
Write-Host "  Press Ctrl+C to stop all services" -ForegroundColor Gray
Write-Host ""

# Run tunnel (blocks)
$configPath = "$env:USERPROFILE\.cloudflared\config.yml"
if (-not (Test-Path $configPath)) {
    Write-Error "Tunnel config not found. Run setup-cloudflare.ps1 first."
    exit 1
}

# Update config with actual tunnel name if different
$config = Get-Content $configPath -Raw
$config = $config -replace 'remotedesk', $tunnelName
Set-Content -Path $configPath -Value $config -Encoding UTF8

Write-Host "Starting tunnel... (keep this window open)" -ForegroundColor Green
& cloudflared tunnel run --config $configPath $tunnelName

# Cleanup on exit
Write-Host ""
Write-Host "Stopping Docker services..." -ForegroundColor Yellow
docker compose -f "$PSScriptRoot\docker-compose.cloudflare.yml" down
Write-Host "Done." -ForegroundColor Green