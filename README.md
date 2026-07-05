# MinIO Object Storage — Server Setup Guide

### Windows Server & Ubuntu Server (with Nginx)

A step-by-step guide to installing, configuring, and running MinIO as a
production-ready object storage service on either Windows Server or Ubuntu.
Covers running MinIO as a background service that survives reboots, firewall
hardening, bucket policies, and an Nginx reverse proxy for Ubuntu deployments.

---

## Table of Contents

### Windows Server

1. [Requirements](#1-windows--requirements)
2. [Directory Structure](#2-windows--directory-structure)
3. [Install MinIO](#3-windows--install-minio)
4. [Configure Credentials](#4-windows--configure-credentials)
5. [First Run & Bucket Setup](#5-windows--first-run--bucket-setup)
6. [Run as a Windows Service (NSSM)](#6-windows--run-as-a-windows-service-nssm)
7. [Firewall Rules](#7-windows--firewall-rules)
8. [Verify](#8-windows--verify)
9. [Day-to-Day Management](#9-windows--day-to-day-management)

### Ubuntu Server

10. [Requirements](#10-ubuntu--requirements)
11. [Directory Structure](#11-ubuntu--directory-structure)
12. [Install MinIO](#12-ubuntu--install-minio)
13. [Configure Credentials](#13-ubuntu--configure-credentials)
14. [First Run & Bucket Setup](#14-ubuntu--first-run--bucket-setup)
15. [Run as a systemd Service](#15-ubuntu--run-as-a-systemd-service)
16. [Nginx Reverse Proxy](#16-ubuntu--nginx-reverse-proxy)
17. [SSL — Let's Encrypt](#17-ubuntu--ssl--lets-encrypt)
18. [UFW Firewall Rules](#18-ubuntu--ufw-firewall-rules)
19. [Verify](#19-ubuntu--verify)
20. [Day-to-Day Management](#20-ubuntu--day-to-day-management)

### Reference

21. [Bucket Policy Examples](#21-bucket-policy-examples)
22. [Troubleshooting](#22-troubleshooting)
23. [Quick Reference Cheatsheet](#23-quick-reference-cheatsheet)

---

# WINDOWS SERVER

---

## 1. Windows — Requirements

| Component  | Minimum             | Recommended            |
| ---------- | ------------------- | ---------------------- |
| OS         | Windows Server 2016 | Windows Server 2022    |
| RAM        | 4 GB                | 16+ GB                 |
| CPU        | 2 cores             | 8+ cores               |
| Storage    | 100 GB              | 1+ TB (NVMe preferred) |
| PowerShell | 5.1                 | 7.x                    |

> All commands in this section are run in **PowerShell as Administrator**.

---

## 2. Windows — Directory Structure

Choose a layout that suits your environment. The following is a recommended
baseline:

```
C:\MinIO\
  minio.exe       ← server binary
  mc.exe          ← admin client binary

C:\MinIO\Data\    ← storage root (point this at your large drive)

C:\MinIO\Logs\
  minio.log
  minio-error.log

C:\NSSM\          ← service manager binary
```

Create the directories:

```powershell
$dirs = @(
    "C:\MinIO",
    "C:\MinIO\Data",
    "C:\MinIO\Logs",
    "C:\NSSM"
)
$dirs | ForEach-Object { New-Item -ItemType Directory -Path $_ -Force }
```

> **Tip:** If your storage drive is not `C:`, point the data directory at the
> larger volume, e.g. `D:\MinIO\Data`.

---

## 3. Windows — Install MinIO

```powershell
# Download MinIO server binary
Invoke-WebRequest `
    -Uri "https://dl.min.io/server/minio/release/windows-amd64/minio.exe" `
    -OutFile "C:\MinIO\minio.exe"

# Download mc (MinIO Client) for administration
Invoke-WebRequest `
    -Uri "https://dl.min.io/client/mc/release/windows-amd64/mc.exe" `
    -OutFile "C:\MinIO\mc.exe"

# Add to system PATH (optional but convenient)
[System.Environment]::SetEnvironmentVariable(
    "PATH",
    $env:PATH + ";C:\MinIO",
    "Machine"
)
$env:PATH += ";C:\MinIO"

# Verify
minio.exe --version
mc.exe --version
```

---

## 4. Windows — Configure Credentials

**Never hardcode credentials in scripts.** Store them as machine-level
environment variables so they are encrypted by Windows and persist across
reboots.

```powershell
# Set root credentials
[System.Environment]::SetEnvironmentVariable("MINIO_ROOT_USER",     "your-admin-username", "Machine")
[System.Environment]::SetEnvironmentVariable("MINIO_ROOT_PASSWORD", "your-strong-password", "Machine")

# Load into the current session
$env:MINIO_ROOT_USER     = "your-admin-username"
$env:MINIO_ROOT_PASSWORD = "your-strong-password"
```

**Password requirements:**

- Minimum 8 characters (MinIO enforces this)
- Use 16+ characters with mixed case, numbers, and symbols for production

---

## 5. Windows — First Run & Bucket Setup

### Start MinIO Once (to verify it works)

```powershell
minio.exe server C:\MinIO\Data `
    --address ":9000" `
    --console-address ":9001"
```

You should see:

```
MinIO Object Storage Server
API:    http://<your-ip>:9000
WebUI:  http://<your-ip>:9001
```

Leave this running and open a **new PowerShell window** for the next steps.

### Configure mc Client

```powershell
mc.exe alias set myminio http://127.0.0.1:9000 your-admin-username your-strong-password
```

### Create a Bucket

```powershell
mc.exe mb myminio/my-bucket

# Verify
mc.exe ls myminio
```

### Apply a Bucket Policy

See [Section 21](#21-bucket-policy-examples) for policy examples including
public read for specific paths.

```powershell
# Example: apply a policy from a JSON file
mc.exe anonymous set-json C:\MinIO\my-policy.json myminio/my-bucket

# Verify
mc.exe anonymous get myminio/my-bucket
```

Once verified, press `Ctrl+C` to stop MinIO and proceed to install it as a
service.

---

## 6. Windows — Run as a Windows Service (NSSM)

NSSM (Non-Sucking Service Manager) wraps any executable as a proper Windows
service with auto-start, auto-restart, and log rotation.

### Install NSSM

```powershell
Invoke-WebRequest -Uri "https://nssm.cc/release/nssm-2.24.zip" `
    -OutFile "C:\NSSM\nssm.zip"

Expand-Archive "C:\NSSM\nssm.zip" -DestinationPath "C:\NSSM\"

# Use the 64-bit binary
$nssm = "C:\NSSM\nssm-2.24\win64\nssm.exe"
```

### Register MinIO as a Service

```powershell
$nssm = "C:\NSSM\nssm-2.24\win64\nssm.exe"

# Install
& $nssm install MinIO "C:\MinIO\minio.exe"

# Startup parameters
& $nssm set MinIO AppParameters "server C:\MinIO\Data --address :9000 --console-address :9001"

# Working directory
& $nssm set MinIO AppDirectory "C:\MinIO"

# Inject credentials (so they are available to the service process)
& $nssm set MinIO AppEnvironmentExtra `
    "MINIO_ROOT_USER=your-admin-username" `
    "MINIO_ROOT_PASSWORD=your-strong-password"

# Logging
& $nssm set MinIO AppStdout      "C:\MinIO\Logs\minio.log"
& $nssm set MinIO AppStderr      "C:\MinIO\Logs\minio-error.log"
& $nssm set MinIO AppRotateFiles 1
& $nssm set MinIO AppRotateBytes 52428800   # rotate at 50 MB

# Restart automatically if the process exits
& $nssm set MinIO AppRestartDelay 5000      # wait 5 s before restart

# Start the service now
Start-Service MinIO

# Confirm
Get-Service MinIO | Select-Object Name, Status, StartType
# Expected: MinIO  Running  Automatic
```

### Remove the Service (if needed)

```powershell
Stop-Service MinIO
& $nssm remove MinIO confirm
```

---

## 7. Windows — Firewall Rules

MinIO's API (9000) and Console (9001) ports should **never** be exposed
directly to the internet. All external traffic should go through a reverse
proxy (IIS, Nginx on Windows, or Caddy).

```powershell
# Block MinIO ports from the internet
New-NetFirewallRule -DisplayName "Block MinIO API"     -Direction Inbound `
    -Protocol TCP -LocalPort 9000 -RemoteAddress Internet -Action Block

New-NetFirewallRule -DisplayName "Block MinIO Console" -Direction Inbound `
    -Protocol TCP -LocalPort 9001 -RemoteAddress Internet -Action Block

# Allow HTTP and HTTPS for your reverse proxy
New-NetFirewallRule -DisplayName "Allow HTTP"  -Direction Inbound `
    -Protocol TCP -LocalPort 80  -Action Allow
New-NetFirewallRule -DisplayName "Allow HTTPS" -Direction Inbound `
    -Protocol TCP -LocalPort 443 -Action Allow

# (Optional) Allow MinIO API from a specific trusted IP only
New-NetFirewallRule -DisplayName "MinIO Trusted Access" -Direction Inbound `
    -Protocol TCP -LocalPort 9000 -RemoteAddress "TRUSTED.IP.ADDRESS" -Action Allow

# Verify
Get-NetFirewallRule -DisplayName "Block MinIO*" | Select-Object DisplayName, Enabled, Action
```

---

## 8. Windows — Verify

```powershell
# MinIO health endpoint
Invoke-WebRequest "http://127.0.0.1:9000/minio/health/live" -UseBasicParsing
# Expected: StatusCode 200

# List buckets
mc.exe ls myminio

# Service status
Get-Service MinIO | Select-Object Name, Status, StartType
```

---

## 9. Windows — Day-to-Day Management

```powershell
# Start / Stop / Restart
Start-Service   MinIO
Stop-Service    MinIO
Restart-Service MinIO

# Tail logs
Get-Content "C:\MinIO\Logs\minio.log"       -Wait -Tail 50
Get-Content "C:\MinIO\Logs\minio-error.log" -Wait -Tail 50

# Check disk usage
Get-PSDrive C | Select-Object Name,
    @{N="Used GB"; E={[math]::Round($_.Used/1GB,1)}},
    @{N="Free GB"; E={[math]::Round($_.Free/1GB,1)}}

# Update MinIO binary
Stop-Service MinIO
Invoke-WebRequest `
    -Uri "https://dl.min.io/server/minio/release/windows-amd64/minio.exe" `
    -OutFile "C:\MinIO\minio.exe"
Start-Service MinIO
minio.exe --version

# Rotate credentials
$nssm = "C:\NSSM\nssm-2.24\win64\nssm.exe"
Stop-Service MinIO
& $nssm set MinIO AppEnvironmentExtra `
    "MINIO_ROOT_USER=your-admin-username" `
    "MINIO_ROOT_PASSWORD=your-new-password"
[System.Environment]::SetEnvironmentVariable("MINIO_ROOT_PASSWORD","your-new-password","Machine")
Start-Service MinIO
mc.exe alias set myminio http://127.0.0.1:9000 your-admin-username your-new-password
```

---

# UBUNTU SERVER

---

## 10. Ubuntu — Requirements

| Component | Minimum          | Recommended              |
| --------- | ---------------- | ------------------------ |
| OS        | Ubuntu 20.04 LTS | Ubuntu 22.04 / 24.04 LTS |
| RAM       | 4 GB             | 16+ GB                   |
| CPU       | 2 cores          | 8+ cores                 |
| Storage   | 100 GB           | 1+ TB (NVMe preferred)   |

```bash
# Update the system first
sudo apt update && sudo apt upgrade -y
```

---

## 11. Ubuntu — Directory Structure

```bash
# Create directories
sudo mkdir -p /opt/minio
sudo mkdir -p /var/log/minio
sudo mkdir -p /mnt/minio-data    # point this at your storage volume

# Create a dedicated non-login service user
sudo useradd -r -s /sbin/nologin minio-user

# Set ownership
sudo chown -R minio-user:minio-user /opt/minio
sudo chown -R minio-user:minio-user /var/log/minio
sudo chown -R minio-user:minio-user /mnt/minio-data
```

```
/opt/minio/
  minio           ← server binary
  mc              ← admin client binary

/mnt/minio-data/  ← storage root (mount your large volume here)

/var/log/minio/
  minio.log
  minio-error.log
```

> **Tip:** Mount your storage volume at `/mnt/minio-data` before proceeding.
> Use `lsblk` to identify the device and add it to `/etc/fstab` for
> persistence across reboots.

---

## 12. Ubuntu — Install MinIO

```bash
# Download MinIO server binary
sudo wget -O /opt/minio/minio \
    https://dl.min.io/server/minio/release/linux-amd64/minio

# Download mc (MinIO Client)
sudo wget -O /opt/minio/mc \
    https://dl.min.io/client/mc/release/linux-amd64/mc

# Make both executable
sudo chmod +x /opt/minio/minio
sudo chmod +x /opt/minio/mc

# Add to PATH
echo 'export PATH=$PATH:/opt/minio' | sudo tee /etc/profile.d/minio.sh
source /etc/profile.d/minio.sh

# Verify
minio --version
mc --version
```

---

## 13. Ubuntu — Configure Credentials

Store credentials in a dedicated environment file with restricted permissions.

```bash
sudo nano /etc/default/minio
```

```bash
# /etc/default/minio

# Root credentials
MINIO_ROOT_USER=your-admin-username
MINIO_ROOT_PASSWORD=your-strong-password

# Storage path
MINIO_VOLUMES=/mnt/minio-data

# Server options
MINIO_OPTS="--address :9000 --console-address :9001"
```

```bash
# Restrict access to root only
sudo chmod 600 /etc/default/minio
sudo chown root:root /etc/default/minio
```

---

## 14. Ubuntu — First Run & Bucket Setup

### Start MinIO Once (to verify it works)

```bash
sudo -u minio-user \
    MINIO_ROOT_USER=your-admin-username \
    MINIO_ROOT_PASSWORD=your-strong-password \
    /opt/minio/minio server /mnt/minio-data \
    --address :9000 --console-address :9001
# Ctrl+C once verified
```

### Configure mc Client

```bash
mc alias set myminio http://127.0.0.1:9000 your-admin-username your-strong-password
```

### Create a Bucket

```bash
mc mb myminio/my-bucket

# Verify
mc ls myminio
```

### Apply a Bucket Policy

See [Section 21](#21-bucket-policy-examples) for policy examples.

```bash
mc anonymous set-json /opt/minio/my-policy.json myminio/my-bucket
mc anonymous get myminio/my-bucket
```

---

## 15. Ubuntu — Run as a systemd Service

```bash
sudo nano /etc/systemd/system/minio.service
```

```ini
[Unit]
Description=MinIO Object Storage
Documentation=https://docs.min.io
Wants=network-online.target
After=network-online.target
AssertFileIsExecutable=/opt/minio/minio

[Service]
Type=notify
WorkingDirectory=/opt/minio

# Load credentials and options from env file
EnvironmentFile=/etc/default/minio

User=minio-user
Group=minio-user

ExecStart=/opt/minio/minio server $MINIO_VOLUMES $MINIO_OPTS

# Always restart on exit
Restart=always
RestartSec=5s

# Logging
StandardOutput=append:/var/log/minio/minio.log
StandardError=append:/var/log/minio/minio-error.log

# Resource limits
LimitNOFILE=65536
TasksMax=infinity
TimeoutStopSec=infinity
SendSIGKILL=no

[Install]
WantedBy=multi-user.target
```

```bash
# Reload systemd and enable the service
sudo systemctl daemon-reload
sudo systemctl enable minio
sudo systemctl start minio

# Check status
sudo systemctl status minio
# Expected: Active: active (running)
```

---

## 16. Ubuntu — Nginx Reverse Proxy

Nginx acts as the single public entry point — it terminates SSL and proxies
requests to your internal services.

### Install Nginx

```bash
sudo apt install nginx -y
sudo systemctl enable nginx
sudo systemctl start nginx
```

### Site Configuration

```bash
# Remove default site
sudo rm /etc/nginx/sites-enabled/default

# Create your site config
sudo nano /etc/nginx/sites-available/myapp
```

```nginx
# ── Upstreams ──────────────────────────────────────────────────────────────
# Add or remove upstream blocks to match your application services.

upstream app_api {
    server 127.0.0.1:5000;    # e.g. .NET / Node / Python API
    keepalive 32;
}

upstream app_frontend {
    server 127.0.0.1:3000;    # e.g. Next.js / React frontend
    keepalive 32;
}

# ── HTTP → HTTPS redirect ──────────────────────────────────────────────────
server {
    listen 80;
    listen [::]:80;
    server_name yourdomain.com;

    # Required for Certbot domain verification
    location /.well-known/acme-challenge/ {
        root /var/www/certbot;
    }

    location / {
        return 301 https://$host$request_uri;
    }
}

# ── Main HTTPS server ──────────────────────────────────────────────────────
server {
    listen 443 ssl http2;
    listen [::]:443 ssl http2;
    server_name yourdomain.com;

    # SSL certificates — filled in by Certbot (see Section 17)
    ssl_certificate     /etc/letsencrypt/live/yourdomain.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/yourdomain.com/privkey.pem;
    ssl_protocols       TLSv1.2 TLSv1.3;
    ssl_ciphers         HIGH:!aNULL:!MD5;
    ssl_session_cache   shared:SSL:10m;
    ssl_session_timeout 10m;

    # Security headers
    add_header X-Content-Type-Options   nosniff                          always;
    add_header X-Frame-Options          SAMEORIGIN                       always;
    add_header X-XSS-Protection         "1; mode=block"                  always;
    add_header Strict-Transport-Security "max-age=31536000; includeSubDomains" always;
    add_header Referrer-Policy          "strict-origin-when-cross-origin" always;

    # Allow large file uploads (adjust to your max upload size)
    client_max_body_size 4096m;
    client_body_timeout  300s;
    proxy_read_timeout   300s;
    proxy_send_timeout   300s;

    # ── API ──────────────────────────────────────────────────────────────
    # Adjust the location prefix to match your API route (e.g. /api/)
    location /api/ {
        proxy_pass         http://app_api;
        proxy_http_version 1.1;
        proxy_set_header   Host              $host;
        proxy_set_header   X-Real-IP         $remote_addr;
        proxy_set_header   X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
        proxy_set_header   Connection        "";
        proxy_buffering    off;
    }

    # ── Frontend ─────────────────────────────────────────────────────────
    location / {
        proxy_pass         http://app_frontend;
        proxy_http_version 1.1;
        proxy_set_header   Upgrade    $http_upgrade;
        proxy_set_header   Connection "upgrade";
        proxy_set_header   Host       $host;
        proxy_cache_bypass $http_upgrade;
    }
}

# ── MinIO Console (admin only) ────────────────────────────────────────────
# Expose the MinIO web console on a separate port, restricted by IP.
# Remove this block if you do not need remote console access.
server {
    listen 9001 ssl http2;
    server_name yourdomain.com;

    ssl_certificate     /etc/letsencrypt/live/yourdomain.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/yourdomain.com/privkey.pem;

    # Only allow access from trusted IP addresses
    allow YOUR.TRUSTED.IP.ADDRESS;
    deny  all;

    location / {
        proxy_pass             http://127.0.0.1:9001;
        proxy_http_version     1.1;
        proxy_set_header       Host              $http_host;
        proxy_set_header       X-Real-IP         $remote_addr;
        proxy_set_header       X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header       X-Forwarded-Proto $scheme;
        proxy_set_header       Upgrade           $http_upgrade;
        proxy_set_header       Connection        "upgrade";
        proxy_connect_timeout  300;
        chunked_transfer_encoding off;
    }
}
```

```bash
# Enable site
sudo ln -s /etc/nginx/sites-available/myapp /etc/nginx/sites-enabled/myapp

# Test configuration
sudo nginx -t
# Expected: syntax is ok / test is successful

# Reload Nginx
sudo systemctl reload nginx
```

---

## 17. Ubuntu — SSL — Let's Encrypt

```bash
# Install Certbot
sudo apt install certbot python3-certbot-nginx -y

# Obtain and install certificate
sudo certbot --nginx -d yourdomain.com

# Certbot automatically configures Nginx and schedules renewal.
# Test the renewal process:
sudo certbot renew --dry-run

# Check the renewal timer
sudo systemctl status certbot.timer
```

### Self-Signed Certificate (for internal / dev servers)

```bash
sudo openssl req -x509 -nodes -days 365 -newkey rsa:2048 \
    -keyout /etc/ssl/private/server-selfsigned.key \
    -out    /etc/ssl/certs/server-selfsigned.crt \
    -subj   "/CN=localhost"
```

Update your Nginx config to point `ssl_certificate` and `ssl_certificate_key`
at these files instead of the Let's Encrypt paths.

---

## 18. Ubuntu — UFW Firewall Rules

```bash
# Enable UFW
sudo ufw enable

# Always allow SSH first to avoid locking yourself out
sudo ufw allow OpenSSH

# Allow public web traffic through Nginx
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp

# Block MinIO API and Console from public internet
# (they are accessed only via Nginx proxy or loopback)
sudo ufw deny 9000/tcp
sudo ufw deny 9001/tcp

# (Optional) Allow MinIO API from a specific trusted IP
sudo ufw allow from YOUR.TRUSTED.IP.ADDRESS to any port 9000

# (Optional) Allow MinIO Console from a specific trusted IP
sudo ufw allow from YOUR.TRUSTED.IP.ADDRESS to any port 9001

# Review rules
sudo ufw status verbose
```

---

## 19. Ubuntu — Verify

```bash
# MinIO health check
curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:9000/minio/health/live
# Expected: 200

# Service status
sudo systemctl status minio

# List buckets
mc ls myminio

# Nginx status and config test
sudo systemctl status nginx
sudo nginx -t

# Check listening ports
ss -tlnp | grep -E "9000|9001|80|443"

# Tail logs
sudo tail -f /var/log/minio/minio.log
sudo tail -f /var/log/nginx/error.log
```

---

## 20. Ubuntu — Day-to-Day Management

```bash
# Start / Stop / Restart MinIO
sudo systemctl start   minio
sudo systemctl stop    minio
sudo systemctl restart minio

# Reload Nginx config without downtime
sudo systemctl reload nginx

# Tail MinIO logs
sudo journalctl -u minio -f
sudo tail -f /var/log/minio/minio.log

# Update MinIO binary
sudo systemctl stop minio
sudo wget -O /opt/minio/minio \
    https://dl.min.io/server/minio/release/linux-amd64/minio
sudo chmod +x /opt/minio/minio
sudo systemctl start minio
minio --version

# Rotate credentials
sudo nano /etc/default/minio
# Update MINIO_ROOT_PASSWORD to a new value, save, then:
sudo systemctl restart minio
mc alias set myminio http://127.0.0.1:9000 your-admin-username your-new-password

# Check disk usage
df -h /mnt/minio-data
du -sh /mnt/minio-data/*
```

---

# REFERENCE

---

## 21. Bucket Policy Examples

Save any of the following as a `.json` file and apply with:

```bash
# Ubuntu
mc anonymous set-json /path/to/policy.json myminio/my-bucket

# Windows
mc.exe anonymous set-json C:\path\to\policy.json myminio/my-bucket
```

### Public Read — Entire Bucket

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Principal": { "AWS": ["*"] },
      "Action": ["s3:GetObject"],
      "Resource": ["arn:aws:s3:::my-bucket/*"]
    }
  ]
}
```

### Public Read — Specific Path Only

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Principal": { "AWS": ["*"] },
      "Action": ["s3:GetObject"],
      "Resource": ["arn:aws:s3:::my-bucket/public/*"]
    }
  ]
}
```

### Public Read — Multiple Paths

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Principal": { "AWS": ["*"] },
      "Action": ["s3:GetObject"],
      "Resource": [
        "arn:aws:s3:::my-bucket/images/*",
        "arn:aws:s3:::my-bucket/videos/hls/*"
      ]
    }
  ]
}
```

### Private Bucket (default — no public access)

Simply do not apply any anonymous policy. All access requires credentials.

```bash
# Remove any existing policy
mc anonymous set none myminio/my-bucket
```

---

## 22. Troubleshooting

### MinIO won't start

**Windows:**

```powershell
Get-Content "C:\MinIO\Logs\minio-error.log" -Tail 30
Get-EventLog -LogName Application -Source "nssm*" -Newest 10
# Try running manually to see raw output:
$env:MINIO_ROOT_USER = "your-admin-username"
$env:MINIO_ROOT_PASSWORD = "your-strong-password"
minio.exe server C:\MinIO\Data
```

**Ubuntu:**

```bash
sudo journalctl -u minio -n 50 --no-pager
sudo systemctl status minio
# Try running manually:
sudo -u minio-user MINIO_ROOT_USER=x MINIO_ROOT_PASSWORD=y \
    /opt/minio/minio server /mnt/minio-data
```

---

### Connection refused on port 9000

**Windows:**

```powershell
netstat -ano | findstr "9000"
Get-Service MinIO
```

**Ubuntu:**

```bash
ss -tlnp | grep 9000
sudo systemctl status minio
```

---

### Authentication / signature error

Credentials in your client do not match what the server was started with.

**Windows:**

```powershell
[System.Environment]::GetEnvironmentVariable("MINIO_ROOT_USER",     "Machine")
[System.Environment]::GetEnvironmentVariable("MINIO_ROOT_PASSWORD", "Machine")
mc.exe alias set myminio http://127.0.0.1:9000 <user> <password>
```

**Ubuntu:**

```bash
sudo grep MINIO_ROOT /etc/default/minio
mc alias set myminio http://127.0.0.1:9000 <user> <password>
```

---

### Nginx 502 Bad Gateway

```bash
# Check the upstream service is running and on the expected port
sudo systemctl status minio
ss -tlnp | grep 5000    # or whichever port your app uses

# Check Nginx error log
sudo tail -f /var/log/nginx/error.log

# Validate Nginx config
sudo nginx -t
```

---

### Object returns 403 Forbidden

The bucket policy does not permit public access to that path.

```bash
# Check current policy
mc anonymous get myminio/my-bucket

# Re-apply policy
mc anonymous set-json /path/to/policy.json myminio/my-bucket
```

---

### Disk space

**Windows:**

```powershell
Get-PSDrive | Where-Object { $_.Provider -like "*FileSystem*" } |
    Select-Object Name,
    @{N="Used GB"; E={[math]::Round($_.Used/1GB,1)}},
    @{N="Free GB"; E={[math]::Round($_.Free/1GB,1)}}
```

**Ubuntu:**

```bash
df -h
du -sh /mnt/minio-data/*
```

---

## 23. Quick Reference Cheatsheet

### Service Commands

| Action       | Windows (PowerShell)                        | Ubuntu (bash)                  |
| ------------ | ------------------------------------------- | ------------------------------ |
| Start        | `Start-Service MinIO`                       | `sudo systemctl start minio`   |
| Stop         | `Stop-Service MinIO`                        | `sudo systemctl stop minio`    |
| Restart      | `Restart-Service MinIO`                     | `sudo systemctl restart minio` |
| Status       | `Get-Service MinIO`                         | `sudo systemctl status minio`  |
| Logs         | `Get-Content C:\MinIO\Logs\minio.log -Wait` | `sudo journalctl -u minio -f`  |
| Reload Nginx | N/A                                         | `sudo systemctl reload nginx`  |

### mc Client Commands

```bash
# These work on both platforms (use mc.exe on Windows)

mc alias set <alias> http://127.0.0.1:9000 <user> <password>  # connect
mc ls <alias>                                                   # list buckets
mc mb <alias>/<bucket>                                          # create bucket
mc rm --recursive --force <alias>/<bucket>/<prefix>            # delete objects
mc cp <local-file> <alias>/<bucket>/<key>                      # upload file
mc anonymous get <alias>/<bucket>                               # check policy
mc anonymous set-json policy.json <alias>/<bucket>             # apply policy
mc anonymous set none <alias>/<bucket>                         # remove policy
```

### Key File Locations

| Item            | Windows                         | Ubuntu                           |
| --------------- | ------------------------------- | -------------------------------- |
| Server binary   | `C:\MinIO\minio.exe`            | `/opt/minio/minio`               |
| mc client       | `C:\MinIO\mc.exe`               | `/opt/minio/mc`                  |
| Data root       | `C:\MinIO\Data\`                | `/mnt/minio-data/`               |
| Server log      | `C:\MinIO\Logs\minio.log`       | `/var/log/minio/minio.log`       |
| Error log       | `C:\MinIO\Logs\minio-error.log` | `/var/log/minio/minio-error.log` |
| Credentials     | Machine env vars                | `/etc/default/minio`             |
| Service manager | NSSM                            | systemd                          |
| Nginx config    | —                               | `/etc/nginx/sites-available/`    |

### Default Ports

| Port | Service           | Recommended Access          |
| ---- | ----------------- | --------------------------- |
| 9000 | MinIO API         | Internal / trusted IPs only |
| 9001 | MinIO Console     | Internal / trusted IPs only |
| 80   | HTTP (Nginx/IIS)  | Public                      |
| 443  | HTTPS (Nginx/IIS) | Public                      |

---

## Further Reading

- [MinIO Docs](https://min.io/docs/minio/linux/index.html)
- [MinIO GitHub](https://github.com/minio/minio)
- [mc Client Reference](https://min.io/docs/minio/linux/reference/minio-mc.html)
- [NSSM](https://nssm.cc)
- [Nginx Docs](https://nginx.org/en/docs/)
- [Let's Encrypt / Certbot](https://certbot.eff.org)
- [systemd Service Files](https://www.freedesktop.org/software/systemd/man/systemd.service.html)

---

_MinIO · NSSM 2.24 · Nginx 1.24+ · Ubuntu 20.04/22.04/24.04 · Windows Server 2016/2019/2022_
