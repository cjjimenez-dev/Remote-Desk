#!/usr/bin/env bash
# ==============================================================================
# RemoteDesk 1-Click VPS Installer (Ubuntu / Debian)
# Sets up RemoteDesk Signaling Server + coturn STUN/TURN Relay Server
# ==============================================================================

set -euo pipefail

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

echo -e "${CYAN}======================================================${NC}"
echo -e "${CYAN}        RemoteDesk VPS Production Setup Script        ${NC}"
echo -e "${CYAN}======================================================${NC}"
echo ""

# Check root
if [ "$EUID" -ne 0 ]; then
  echo -e "${RED}[ERROR] Please run this script with sudo or as root:${NC}"
  echo "  sudo bash $0"
  exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# 1. Detect or ask for public IP
DETECTED_IP=$(curl -s4 ifconfig.me || curl -s4 icanhazip.com || echo "")
echo -e "${YELLOW}[1/6] Detecting Public IPv4 address...${NC}"
if [ -n "$DETECTED_IP" ]; then
  echo -e "  Detected Public IP: ${GREEN}$DETECTED_IP${NC}"
  read -r -p "  Press ENTER to use this IP or type your public IP: " INPUT_IP
  EXTERNAL_IP="${INPUT_IP:-$DETECTED_IP}"
else
  read -r -p "  Enter this VPS public IPv4 address: " EXTERNAL_IP
fi

# 2. Ask for optional Domain (Cloudflare DNS)
echo ""
echo -e "${YELLOW}[2/6] Domain Name Configuration (Optional)${NC}"
echo "  If you have a domain managed by Cloudflare (e.g., relay.mydomain.com),"
echo "  enter it here. Otherwise, leave blank to use the public IP directly."
read -r -p "  Domain name [leave empty if none]: " DOMAIN_NAME

TARGET_DOMAIN="${DOMAIN_NAME:-$EXTERNAL_IP}"

# 3. Install Docker if missing
echo ""
echo -e "${YELLOW}[3/6] Checking Docker & Docker Compose...${NC}"
if ! command -v docker &> /dev/null; then
  echo "  Installing Docker..."
  apt-get update -qq
  apt-get install -y -qq ca-certificates curl gnupg lsb-release
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL https://download.docker.com/linux/ubuntu/gpg | gpg --dearmor -o /etc/apt/keyrings/docker.gpg --yes 2>/dev/null || true
  chmod a+r /etc/apt/keyrings/docker.gpg 2>/dev/null || true
  echo \
    "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu \
    $(lsb_release -cs) stable" | tee /etc/apt/sources.list.d/docker.list > /dev/null
  apt-get update -qq
  apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-compose-plugin
  echo -e "  ${GREEN}✓ Docker installed successfully.${NC}"
else
  echo -e "  ${GREEN}✓ Docker is already installed.${NC}"
fi

# 4. Generate .env file
echo ""
echo -e "${YELLOW}[4/6] Creating environment configuration (.env)...${NC}"
TURN_SECRET=$(openssl rand -hex 32)

cat <<EOF > .env
# Auto-generated RemoteDesk VPS Configuration
EXTERNAL_IP=${EXTERNAL_IP}
DOMAIN=${TARGET_DOMAIN}
TURN_SECRET=${TURN_SECRET}
SIGNALING_PORT=5000
TURN_PORT=3478
TURNS_PORT=5349
TURN_FALLBACK_PORT=443
EOF

echo -e "  ${GREEN}✓ Created .env with secure random TURN secret.${NC}"

# 5. Configure UFW Firewall
echo ""
echo -e "${YELLOW}[5/6] Configuring Firewall (UFW)...${NC}"
if command -v ufw &> /dev/null; then
  ufw allow 22/tcp comment 'SSH' > /dev/null || true
  ufw allow 5000/tcp comment 'RemoteDesk Signaling Server' > /dev/null || true
  ufw allow 3478/tcp comment 'RemoteDesk TURN TCP' > /dev/null || true
  ufw allow 3478/udp comment 'RemoteDesk TURN/STUN UDP' > /dev/null || true
  ufw allow 5349/tcp comment 'RemoteDesk TURNS TLS' > /dev/null || true
  ufw allow 443/tcp comment 'RemoteDesk TURNS Fallback' > /dev/null || true
  ufw allow 49152:49252/udp comment 'RemoteDesk Media Relay Ports' > /dev/null || true
  echo "y" | ufw enable > /dev/null || true
  echo -e "  ${GREEN}✓ Firewall ports opened: 5000, 3478, 5349, 443, 49152-49252.${NC}"
fi

# 6. Build and launch Docker Compose stack
echo ""
echo -e "${YELLOW}[6/6] Launching RemoteDesk Docker Stack...${NC}"
docker compose down --remove-orphans 2>/dev/null || true
docker compose up -d --build

echo ""
echo -e "${GREEN}======================================================${NC}"
echo -e "${GREEN}            SETUP COMPLETED SUCCESSFULLY!             ${NC}"
echo -e "${GREEN}======================================================${NC}"
echo ""
echo -e "${CYAN}How to connect RemoteDesk clients across the internet:${NC}"
echo ""
if [ -n "$DOMAIN_NAME" ]; then
  echo -e "  1. In Cloudflare DNS, add an ${YELLOW}A${NC} record:"
  echo -e "       ${YELLOW}${DOMAIN_NAME}${NC} -> ${YELLOW}${EXTERNAL_IP}${NC} (DNS-Only / Grey Cloud)"
  echo ""
  echo -e "  2. On both PCs (anywhere in the world), launch RemoteDesk Viewer:"
  echo -e "     In the top-right ${CYAN}SERVER:${NC} box, enter:"
  echo -e "       ${GREEN}${DOMAIN_NAME}:5000${NC} (or ${GREEN}https://${DOMAIN_NAME}${NC} if behind reverse proxy)"
  echo -e "     and click ${GREEN}✓${NC} or press Enter."
else
  echo -e "  1. On both PCs (anywhere in the world), launch RemoteDesk Viewer:"
  echo -e "     In the top-right ${CYAN}SERVER:${NC} box, enter:"
  echo -e "       ${GREEN}${EXTERNAL_IP}:5000${NC}"
  echo -e "     and click ${GREEN}✓${NC} or press Enter."
fi
echo ""
echo -e "  3. Enter the remote partner's 9-digit Desk ID in REMOTE DESK and click Connect."
echo -e "     Traffic will direct-connect via WebRTC STUN hole-punching,"
echo -e "     or automatically relay through this VPS's coturn server if firewalled!"
echo ""
