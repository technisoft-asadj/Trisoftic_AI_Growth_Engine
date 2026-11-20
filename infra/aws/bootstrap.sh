#!/bin/bash
# EC2 user-data for Amazon Linux 2023: installs Docker + Compose, adds swap (builds need memory), prepares /opt/growth.
set -euxo pipefail
dnf install -y docker git
systemctl enable --now docker
mkdir -p /usr/local/lib/docker/cli-plugins
curl -fsSL "https://github.com/docker/compose/releases/download/v2.29.7/docker-compose-linux-x86_64" -o /usr/local/lib/docker/cli-plugins/docker-compose
chmod +x /usr/local/lib/docker/cli-plugins/docker-compose
curl -fsSL "https://github.com/docker/buildx/releases/download/v0.17.1/buildx-v0.17.1.linux-amd64" -o /usr/local/lib/docker/cli-plugins/docker-buildx || true
chmod +x /usr/local/lib/docker/cli-plugins/docker-buildx || true
if ! swapon --show | grep -q swapfile; then
  fallocate -l 2G /swapfile && chmod 600 /swapfile && mkswap /swapfile && swapon /swapfile
  echo '/swapfile none swap sw 0 0' >> /etc/fstab
fi
mkdir -p /opt/growth && chown ec2-user:ec2-user /opt/growth
usermod -aG docker ec2-user
echo done > /opt/growth/.bootstrapped
