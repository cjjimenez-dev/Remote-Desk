# RemoteDesk VPS Production Deployment Guide

Deploy a dedicated RemoteDesk Signaling and coturn (STUN/TURN) relay server to any cloud VPS (DigitalOcean, Hetzner, Linode, AWS Lightsail, or Oracle Cloud Free Tier).

This enables **global remote desktop connections** across different networks, different ISPs, CGNAT, and corporate firewalls.

---

## Architecture Overview

```
                      ┌───────────────────────────────────────┐
                      │         Cloudflare DNS / Edge         │
                      │   (relay.yourdomain.com -> VPS IP)    │
                      └──────────────────┬────────────────────┘
                                         │
                    ┌────────────────────┴────────────────────┐
                    │            Your Cloud VPS               │
                    │                                         │
                    │  ┌───────────────────────────────────┐  │
                    │  │ RemoteDesk Signaling Server       │  │
                    │  │ (ASP.NET Core SignalR / Port 5000)│  │
                    │  └───────────────────────────────────┘  │
                    │  ┌───────────────────────────────────┐  │
                    │  │ coturn STUN/TURN Media Relay      │  │
                    │  │ (UDP/TCP: 3478, 5349, 443, 49152+)│  │
                    │  └───────────────────────────────────┘  │
                    └─────────────────────────────────────────┘
                                ▲                   ▲
                         SignalR│            SignalR│
                         +WebRTC│            +WebRTC│
                                │                   │
                    ┌───────────┴────┐         ┌────┴───────────┐
                    │ PC 1: Host     │         │ PC 2: Viewer   │
                    │ (Home Network) │         │ (Office / Road)│
                    └────────────────┘         └────────────────┘
```

1. **Signaling Server** handles device registration, presence, and WebRTC SDP/ICE candidate exchange.
2. **STUN** allows Host and Viewer to discover their public IPs and establish **direct peer-to-peer (P2P)** connections when possible (zero server bandwidth consumption).
3. **coturn TURN Relay** automatically relays encrypted WebRTC audio, video, input, and files when P2P direct hole-punching is blocked by symmetric NAT, carrier CGNAT, or firewall restrictions.

---

## 1-Click Quickstart on VPS

### Step 1: Launch a cheap VPS
- Recommended providers:
  - **Hetzner Cloud** (CX22 ~€3.79/mo in Germany or US)
  - **DigitalOcean** ($4-6/mo Droplet in Singapore, Frankfurt, or NYC)
  - **Oracle Cloud Free Tier** (Always Free 4 OCPU ARM or 1 AMD instance)
- OS: **Ubuntu 24.04 LTS** or **Ubuntu 22.04 LTS**.

### Step 2: Run the installer script
SSH into your VPS and run:
```bash
git clone https://github.com/your-repo/RemoteDesk.git
cd RemoteDesk/deploy/vps
sudo bash setup-vps.sh
```
The script will:
- Automatically detect your public IP.
- Prompt for your optional domain name (e.g. `relay.yourdomain.com`).
- Install Docker and Docker Compose.
- Configure UFW firewall for signaling (5000), STUN/TURN (3478, 5349, 443), and media relay (49152-49252).
- Generate a cryptographically secure 32-byte TURN secret.
- Start the Docker stack in the background.

---

## Cloudflare DNS Configuration

In your Cloudflare dashboard:

1. Go to **DNS** -> **Records**.
2. Click **Add record**:
   - **Type**: `A`
   - **Name**: `relay` (or whatever subdomain you prefer, e.g. `remotedesk`)
   - **IPv4 address**: `<YOUR_VPS_PUBLIC_IP>`
   - **Proxy status**: **DNS only (Grey Cloud)**.
     > *Note:* TURN relaying requires UDP traffic. Standard Cloudflare Orange Cloud (proxied) only routes HTTP/HTTPS, which blocks UDP TURN relaying. Keep this record set to **DNS only (Grey Cloud)**.

---

## Connecting Clients in RemoteDesk Viewer

On **both PCs** (regardless of which Wi-Fi, LAN, or cellular network they are on):

1. Launch `RemoteDesk.Viewer.exe`.
2. In the top-right header, find the `SERVER:` box:
   - If using a domain: type `relay.yourdomain.com:5000` (or `https://relay.yourdomain.com` if using an HTTPS reverse proxy).
   - If using IP directly: type `<YOUR_VPS_IP>:5000`.
3. Press **Enter** or click the **✓** button.
   - Status updates to: `Signaling target updated to: http://<your-server>:5000/signaling`.
4. On the controlling PC, enter the remote computer's 9-digit **Desk ID** in `REMOTE DESK` and click **Connect**.
5. RemoteDesk will establish an instant low-latency session!
