# Using Proxmox with Player VM API

[Proxmox Virtual Environment (PVE)](https://pve.proxmox.com/wiki/Main_Page) is an open source server virtualization management solution based on QEMU/KVM and LXC. Player VM API can be configured to use a PVE cluster to deploy QEMU virtual machines rather than its traditional VMware vSphere based virtual machines.

## Proxmox Setup

There are a few things you will need to do within Proxmox to prepare it for use with Player VM API.

### Installation

- Install Proxmox on one or more nodes.
- Add all of the nodes that you want to be used by Player to a single Proxmox cluster.

### Generate an Access Token

Player VM API requires a Proxmox Access Token in order to authenticate with the Proxmox API.

- From the Proxmox Web UI, generate an API Token by clicking on Datacenter and navigating to Permissions -> API Tokens.
- Ensure **Privilege Separation is unchecked** if you want to use the privileges of the token user. Otherwise, you will need to select individual permissions to give to the token.
- Copy the Secret and the Token ID. This will need to be added to appsettings later.
- Token format: `user@system!TokenId=Secret` (e.g., `root@pam!crucible=xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`)

### Configure NGINX

You will need to configure a reverse proxy on the Proxmox node that Player VM API will communicate with in order to access the API and allow viewing of consoles. This will allow the Proxmox API to be accessed over port 443 as well as provide the required authentication headers for accessing consoles through an external application.

**Note:** This NGINX configuration is compatible with both Player VM API and TopoMojo, allowing both applications to use the same Proxmox infrastructure.

#### Automated Setup (Recommended)

Use the provided setup script from the Crucible development repository:

```bash
# Set environment variables
export PROXMOX_HOST="172.22.71.38"  # Your Proxmox IP or hostname
export PROXMOX_API_TOKEN="root@pam!crucible=<your-secret>"

# Run the setup script
bash scripts/setup-proxmox-nginx.sh
```

This script will:
- Install NGINX on the Proxmox node
- Configure NGINX to listen on ports 80 (HTTP redirect) and 443 (HTTPS)
- Set up reverse proxy for all Proxmox API and console traffic
- Inject the API token for WebSocket console connections
- Configure systemd dependencies to ensure NGINX starts after pve-cluster

#### Manual Setup

If you prefer to configure NGINX manually, follow these steps:

1. Install NGINX on your Proxmox node and configure it to run on startup:
   ```bash
   sudo apt install nginx
   sudo systemctl enable nginx
   ```

2. Create the NGINX configuration file at `/etc/nginx/sites-available/proxmox-reverse-proxy`:
   - Replace `pve` with your Node's hostname
   - Replace `<api_token>` with the API Token you generated earlier in the format `user@system!TokenId=Secret`

   ```nginx
   upstream proxmox {
       server "pve";
   }

   server {
       listen 80 default_server;
       rewrite ^(.*) https://$host$1 permanent;
   }

   server {
       listen 443 ssl;
       server_name _;
       ssl_certificate /etc/pve/local/pve-ssl.pem;
       ssl_certificate_key /etc/pve/local/pve-ssl.key;
       proxy_redirect off;

       # VNC WebSocket with API token injection
       location ~ /api2/json/nodes/.+/qemu/.+/vncwebsocket.* {
           proxy_set_header "Authorization" "PVEAPIToken=<api_token>";
           proxy_http_version 1.1;
           proxy_set_header Upgrade $http_upgrade;
           proxy_set_header Connection "upgrade";
           proxy_pass https://localhost:8006;
           proxy_buffering off;
           client_max_body_size 0;
           proxy_connect_timeout 3600s;
           proxy_read_timeout 3600s;
           proxy_send_timeout 3600s;
           send_timeout 3600s;
       }

       # All other traffic (Web UI, API)
       location / {
           proxy_http_version 1.1;
           proxy_set_header Upgrade $http_upgrade;
           proxy_set_header Connection "upgrade";
           proxy_pass https://localhost:8006;
           proxy_buffering off;
           client_max_body_size 0;
           proxy_connect_timeout 3600s;
           proxy_read_timeout 3600s;
           proxy_send_timeout 3600s;
           send_timeout 3600s;
       }
   }
   ```

3. Enable the NGINX site:
   ```bash
   sudo ln -sf /etc/nginx/sites-available/proxmox-reverse-proxy /etc/nginx/sites-enabled/proxmox-reverse-proxy
   sudo rm -f /etc/nginx/sites-enabled/default
   ```

4. Configure NGINX service dependencies:

   The certificate location `/etc/pve/local` gets mounted by Proxmox and the nginx service may fail if it tries to start before the pve-cluster.service loads.

   ```bash
   sudo mkdir -p /etc/systemd/system/nginx.service.d
   
   sudo tee /etc/systemd/system/nginx.service.d/pve-cluster.conf > /dev/null <<'EOF'
   [Unit]
   Requires=pve-cluster.service
   After=pve-cluster.service
   EOF
   
   sudo systemctl daemon-reload
   ```

5. Test and restart NGINX:
   ```bash
   sudo nginx -t
   sudo systemctl restart nginx
   ```

## Player VM API Setup

This section describes the appsettings that will need to be set to configure Player VM API to use Proxmox.

### Required Settings

Configure the following in `appsettings.Development.json` or your deployment configuration:

```json
{
  "Proxmox": {
    "Enabled": true,
    "Host": "172.22.71.38",
    "Port": 443,
    "Token": "root@pam!crucible=<your-secret-here>",
    "StateRefreshIntervalSeconds": 60,
    "UseSecureWebSocket": true
  }
}
```

#### Configuration Details

- **Enabled**: Set to `true` to enable Proxmox mode (disable vSphere)
- **Host**: The IP address or hostname of your Proxmox node
  - Use the IP/hostname that has NGINX configured
  - Can be IP address (e.g., `172.22.71.38`) or hostname (e.g., `pve.mshome.net`)
- **Port**: `443` (HTTPS through NGINX reverse proxy)
  - Do not use `8006` (direct Proxmox access) - the NGINX proxy is required for console authentication
- **Token**: Your Proxmox API token in the format `user@system!TokenId=Secret`
  - Must match the token configured in the NGINX proxy
- **StateRefreshIntervalSeconds**: Interval for polling VM state changes (default: 60)
- **UseSecureWebSocket**: `true` to use `wss://` for WebSocket connections

## Console Access

Player VM API uses NoVNC to provide browser-based VNC console access to Proxmox virtual machines. The console workflow is:

1. User clicks on a VM in Player VM UI
2. Player VM UI requests console credentials from Player VM API
3. Player VM API calls Proxmox API to get VNC ticket
4. Console UI (separate app) connects to Proxmox via WebSocket using the ticket
5. NGINX proxy intercepts the WebSocket connection and injects the API token for authentication
6. Proxmox validates both the VNC ticket and API token, then establishes the console session

### Console Architecture

```
Browser (Console UI)
  ↓ wss://172.22.71.38:443/api2/json/nodes/.../vncwebsocket
NGINX (Port 443)
  ↓ Injects: Authorization: PVEAPIToken=root@pam!crucible=...
  ↓ Proxies to: https://localhost:8006
Proxmox VE (Port 8006)
  ↓ Validates ticket + token
VNC Console Session
```

### Why NGINX is Required

Proxmox VNC WebSocket connections require API token authentication in the HTTP headers. Browser-based WebSocket clients (NoVNC) cannot set custom authentication headers, so the NGINX proxy injects the token transparently.

Without NGINX, console connections will fail with:
- WebSocket error code 1006 (abnormal closure)
- "invalid token value!" errors in Player VM API logs

## Troubleshooting

### Console Connection Fails

**Symptoms:**
- "Internal Server Error" when clicking on VM
- WebSocket connection closes immediately
- Error in logs: "invalid token value!"

**Solutions:**
1. Verify the API token in `appsettings.Development.json` matches the token in NGINX config
2. Check NGINX is running: `sudo systemctl status nginx`
3. Verify NGINX config: `sudo nginx -t`
4. Check NGINX logs: `sudo tail -f /var/log/nginx/error.log`
5. Ensure Player VM API is using port 443, not 8006

### Certificate Warnings

**Symptom:** Browser shows SSL certificate warning when accessing Proxmox

**Solution:** Proxmox uses self-signed certificates by default. Either:
1. Accept the certificate warning (development only)
2. Install a proper SSL certificate on Proxmox
3. Add the Proxmox certificate to your system's trusted certificates

### NGINX Won't Start

**Symptom:** `systemctl start nginx` fails

**Solution:**
1. Check if pve-cluster is running: `systemctl status pve-cluster`
2. Verify NGINX service dependencies: `systemctl cat nginx.service | grep -A 2 "\[Unit\]"`
3. Check port conflicts: `ss -tulpn | grep :443`
4. Review NGINX error log: `journalctl -xeu nginx.service`

### VM State Not Updating

**Symptom:** VM power state or details not refreshing in Player UI

**Solution:**
1. Check `StateRefreshIntervalSeconds` in appsettings (increase if network is slow)
2. Verify Player VM API can reach Proxmox: `curl -k https://172.22.71.38/api2/json/version`
3. Check Player VM API logs for connection errors

## Compatibility Notes

- **Proxmox Version**: Tested with Proxmox VE 8.x
- **VM Types**: Supports QEMU virtual machines (KVM)
- **Console Protocol**: VNC only (SPICE not supported)
- **Shared Infrastructure**: NGINX configuration is compatible with both Player and TopoMojo

## Additional Resources

- [Proxmox VE API Documentation](https://pve.proxmox.com/pve-docs/api-viewer/)
- [Proxmox VE Administration Guide](https://pve.proxmox.com/pve-docs/pve-admin-guide.html)
- [TopoMojo Proxmox Documentation](../../../topomojo/topomojo/docs/Proxmox.md) (for SDN and advanced features)
