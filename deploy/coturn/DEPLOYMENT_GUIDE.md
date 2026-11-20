# RemoteDesk STUN/TURN (coturn) Deployment Guide

This guide details the deployment of `coturn` for RemoteDesk to enable NAT traversal and relaying across restrictive firewalls, mobile carrier CGNAT, and enterprise networks.

---

## 1. Region & Server Sizing

- **Recommended Region**: Singapore (e.g., Linode `ap-south-1`, DigitalOcean `sgp1`, AWS `ap-southeast-1`).
  - *Latency benchmark*: ~30–45 ms ping from Philippine broadband/mobile networks, compared to ~180–220 ms to US West/East.
- **Minimum Specs**: 1 vCPU, 1 GB RAM, 1 Gbps network port, Ubuntu 24.04 LTS.

---

## 2. Firewall / Security Group Configuration

Open the following ports on your cloud provider's firewall (AWS Security Group, Linode Cloud Firewall, etc.):

| Port | Protocol | Purpose |
|------|----------|---------|
| `3478` | UDP & TCP | Standard STUN and TURN listening port |
| `5349` | TCP | Standard TURNS (TLS encrypted TURN) port |
| `443` | TCP | Fallback TURNS port (bypasses strict corporate firewalls) |
| `49152-65535` | UDP | Dynamic WebRTC media relay port range |
| `80` | TCP | Let's Encrypt Certbot ACME challenge |

---

## 3. DNS Configuration

Point an `A` record for your TURN subdomain to your VPS public IPv4:
```text
turn.yourdomain.com  A  <YOUR_VPS_PUBLIC_IP>
```

---

## 4. Install Docker & Certbot for TLS

```bash
# 1. Update and install packages
sudo apt update && sudo apt upgrade -y
sudo apt install -y docker.io docker-compose certbot

# 2. Obtain Let's Encrypt TLS Certificate
sudo certbot certonly --standalone -d turn.yourdomain.com --non-interactive --agree-tos -m admin@yourdomain.com

# 3. Create renewal hook to keep permissions accessible to coturn
sudo chmod -R 755 /etc/letsencrypt/live/
sudo chmod -R 755 /etc/letsencrypt/archive/
```

---

## 5. Deploy coturn via Docker Compose

1. Clone or copy `deploy/coturn/` to the server:
   ```bash
   mkdir -p /opt/remotedesk-coturn
   cd /opt/remotedesk-coturn
   ```
2. Edit `turnserver.conf`:
   - Replace `static-auth-secret` with your production secret key (e.g. generate via `openssl rand -hex 32`).
   - Replace `realm` with your domain (`remotedesk.net`).
   - Ensure certificate paths point to `/etc/coturn/certs/fullchain.pem` and `privkey.pem`.
3. Launch with Docker Compose:
   ```bash
   sudo docker compose up -d
   ```

---

## 6. Verification

Verify using the `turnutils_uclient` CLI tool:
```bash
turnutils_uclient -v -u "<timestamp>:<test_user>" -w "<generated_hmac>" -p 3478 <YOUR_VPS_PUBLIC_IP>
```
Or test via Google Chrome WebRTC Trickle ICE test:
`https://webrtc.github.io/samples/src/content/peerconnection/trickle-ice/`
Enter:
- STUN: `stun:turn.yourdomain.com:3478`
- TURN: `turn:turn.yourdomain.com:3478?transport=udp` with generated username and password.
- Verify `relay` candidate with `typ relay` is gathered successfully!
