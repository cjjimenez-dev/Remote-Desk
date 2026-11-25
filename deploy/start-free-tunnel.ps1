<#
.SYNOPSIS
    Starts a 100% FREE Cloudflare Tunnel for RemoteDesk.
    NO credit card, NO paid VPS, NO account login required!
#>

$ErrorActionPreference = "Stop"

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "   RemoteDesk FREE Internet Tunnel (No Credit Card)     " -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host ""

$binDir = Join-Path $PSScriptRoot "bin"
if (-not (Test-Path $binDir)) {
    New-Item -ItemType Directory -Path $binDir -Force | Out-Null
}

$cfExe = Get-Command cloudflared -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue
if (-not $cfExe) {
    $localExe = Join-Path $binDir "cloudflared.exe"
    if (-not (Test-Path $localExe)) {
        Write-Host "[1/2] Downloading free cloudflared tool..." -ForegroundColor Yellow
        Write-Host "      (This only happens once - 100% free from Cloudflare)" -ForegroundColor Gray
        try {
            Invoke-WebRequest -Uri "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe" -OutFile $localExe -UseBasicParsing
            Write-Host "      ✓ Download complete." -ForegroundColor Green
        }
        catch {
            Write-Warning "Direct download failed, trying winget..."
            winget install --id Cloudflare.cloudflared --accept-source-agreements --accept-package-agreements
            $cfExe = Get-Command cloudflared -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue
        }
    }
    if (Test-Path $localExe) {
        $cfExe = $localExe
    }
}

if (-not $cfExe) {
    Write-Error "Could not find or download cloudflared.exe. Please install winget or download cloudflared manually."
    exit 1
}

Write-Host "[2/2] Launching free Cloudflare Quick Tunnel..." -ForegroundColor Yellow
Write-Host ""
Write-Host "========================================================" -ForegroundColor Green
Write-Host "  HOW TO CONNECT BOTH COMPUTERS ACROSS THE INTERNET:    " -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Green
Write-Host "  1. Look below for a URL ending in: .trycloudflare.com " -ForegroundColor White
Write-Host "     (e.g., https://apple-banana-123.trycloudflare.com) " -ForegroundColor Gray
Write-Host ""
Write-Host "  2. On BOTH computers, launch RemoteDesk Viewer.       " -ForegroundColor White
Write-Host "     In the top-right SERVER: box, enter that address:  " -ForegroundColor White
Write-Host "     Example: apple-banana-123.trycloudflare.com       " -ForegroundColor Yellow
Write-Host "     and press ENTER (or click the ✓ button).           " -ForegroundColor White
Write-Host ""
Write-Host "  3. Enter the partner's Desk ID in REMOTE DESK and connect!" -ForegroundColor White
Write-Host "     Traffic connects directly P2P via Cloudflare STUN. " -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Green
Write-Host "  KEEP THIS WINDOW OPEN WHILE USING REMOTEDESK!         " -ForegroundColor Magenta
Write-Host "========================================================" -ForegroundColor Green
Write-Host ""

& $cfExe tunnel --url http://localhost:5000
