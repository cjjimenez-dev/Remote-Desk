# 15. Internet Remoting via Cloudflare and VPS Architecture

Date: 2026-11-24

## Status

Accepted

## Context

RemoteDesk needs to connect peers across different local networks (LANs), distinct public IP addresses, and across geographic distances (home-to-office, remote work, mobile networks) without requiring complex manual router port forwarding on client machines.

Key challenges for internet-scale remote desktop:
1. **Rendezvous & Discovery**: Two peers on different private subnets cannot connect via `localhost` or LAN broadcast. They must reach a mutual, publicly accessible rendezvous server (Signaling broker).
2. **WebRTC NAT Traversal (P2P vs. Relay)**: Most residential and mobile connections operate behind Network Address Translation (NAT) and stateful firewalls. Direct peer-to-peer data channels require STUN hole-punching. When symmetric NAT or corporate firewall policies block direct UDP, an encrypted TURN relay server is mandatory.
3. **Cloudflare Capabilities & Constraints**:
   - Cloudflare Tunnels (`cloudflared`) and Cloudflare reverse proxies operate primarily at Layer 7 (HTTP/HTTPS and WebSockets). This makes them ideal for securing and exposing the SignalR signaling server with automatic TLS and DDoS protection.
   - However, Cloudflare free proxies and standard tunnels do not proxy arbitrary Layer 4 UDP/TCP traffic (such as coturn WebRTC media relay packets), which requires Cloudflare Spectrum on enterprise plans.
   - Therefore, a dual architecture pattern is required depending on user deployment preferences.

## Decision

1. **Dual Deployment Paths**:
   - **Path A: Cloud VPS with Cloudflare DNS (Recommended for Full Production)**:
     - Deploys both `RemoteDesk.Server.Signaling` (ASP.NET Core SignalR) and `coturn` (STUN/TURN) onto an inexpensive cloud VPS (Ubuntu/Debian on Hetzner, DigitalOcean, Linode, AWS Lightsail, or Oracle Free Tier).
     - Configured in Cloudflare DNS with an `A` record set to **DNS-Only (Grey Cloud)** for `relay.yourdomain.com` so WebRTC UDP packets flow directly to the VPS coturn server.
     - Automated with a single command via `deploy/vps/setup-vps.sh` and `deploy/vps/docker-compose.yml`.
   - **Path B: Cloudflare Tunnel for Signaling + Cloud STUN/TURN (Zero Port Forwarding from Home)**:
     - Uses Cloudflare Tunnel (`cloudflared`) to expose the local Signaling Server on `https://<tunnel-name>.trycloudflare.com`.
     - Bundles Google STUN (`stun:stun.l.google.com:19302`) and Cloudflare Anycast STUN (`stun:stun.cloudflare.com:3478`) for direct NAT hole punching.
     - Pairs with cloud-hosted TURN for symmetric NAT fallback.

2. **Smart Server Endpoint Parsing (`ServerEndpointParser`)**:
   - Implemented `ServerEndpointParser` in `RemoteDesk.Core.Services` to parse user inputs seamlessly:
     - Localhost aliases (`localhost`, `127.0.0.1`) -> `http://localhost:5000/signaling`
     - Local & Public IPs (`192.168.1.100`, `203.0.113.42`) -> `http://{ip}:5000/signaling`
     - Explicit host:port (`vps.example.com:5000`, `1.2.3.4:8080`) -> `http://{host}:{port}/signaling`
     - Cloudflare Tunnels (`*.trycloudflare.com`, `*.cfargotunnel.com`) -> `https://{domain}/signaling` (port 443)
     - Domains (`relay.yourdomain.com`) -> `https://{domain}/signaling` (port 443)
     - Full URIs (`https://...`, `http://...`) -> preserves exact scheme, port, and `/signaling` routing.

3. **Dual Anycast STUN Architecture**:
   - Updated `TurnCredentialGenerator` and `WebRtcTransport` to configure dual Anycast STUN servers by default:
     - `stun:stun.l.google.com:19302` (Google)
     - `stun:stun.cloudflare.com:3478` (Cloudflare)
   - Ensures fast, redundant public IP and NAT mapping discovery worldwide.

4. **Viewer UX Improvements**:
   - Expanded the `SERVER:` input field in `MainWindow.xaml` with auto-expanding width (`MinWidth="85"`, `MaxWidth="190"`) to comfortably fit domains and Cloudflare tunnel URLs.
   - Enhanced real-time connection status messages to display the exact server URL attempted rather than assuming local `:5000`.

## Consequences

- Users can now remote between computers anywhere on the internet even when IPs are unknown or dynamic, and regardless of whether computers are on Wi-Fi, Ethernet, mobile data, or behind different routers.
- The same application client binary works seamlessly for local LAN, home Cloudflare Tunnels, and cloud VPS deployments.
