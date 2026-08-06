# Step-by-Step Azure VM Deployment Guide

This document tracks the deployment of the PGW OIDC MCP server to an Azure Virtual Machine (Ubuntu Linux).

---

## Progress Tracker

- `[x]` Step 1: Create Resource Group & Ubuntu Virtual Machine
- `[x]` Step 2: Configure Public DNS Name & Open Network Ports (80/443)
- `[x]` Step 3: Connect via SSH & Install Docker
- `[x]` Step 4: Clone Code, Build Container, and Run with SQLite Persistence
- `[x]` Step 5: Install Nginx, Set Up Reverse Proxy, and Bind SSL with Certbot
- `[x]` Step 6: Procedure for Deploying New Code Revisions & Testing


---

## Step 1: Create the Virtual Machine [COMPLETE]

To begin, we will deploy an Ubuntu Virtual Machine. You can run the following commands in the **Azure Cloud Shell** or your local CLI terminal.

### 1. Set Your Active Subscription
Make sure you are pointing to your Visual Studio Enterprise subscription:
```bash
az account set --subscription "Visual Studio Enterprise"
```

### 2. Create the Resource Group
If you haven't already, create a dedicated resource group in `eastus2` (or your preferred region):
```bash
az group create --name rg-mcp-apps --location eastus2
```

### 3. Create the Ubuntu VM
Run the following command to provision a lightweight Ubuntu VM (`Standard_B1s` tier, eligible for low cost/free credits):

```bash
az vm create \
  --resource-group rg-mcp-apps \
  --name vm-mcp-server \
  --image Canonical:ubuntu-24_04-lts:server:latest \
  --size Standard_B1s \
  --admin-username azureuser \
  --generate-ssh-keys \
  --location eastus2
```

> [!NOTE]
> *   `--generate-ssh-keys`: Automatically creates SSH keys and saves them on your local storage (`~/.ssh/id_rsa`). If you prefer password authentication instead of keys, replace `--generate-ssh-keys` with `--admin-password <YOUR_SECURE_PASSWORD>`.

---

## Step 2: Configure Public DNS Name & Network Ports [COMPLETE]

In this step, we will configure a public DNS label for your public IP and open ports 80/443 so your application is reachable from the web.

### 1. Assign a Public DNS Label

First, find the exact resource name of the public IP created for your VM (it varies by region and defaults, e.g., `vm-mcp-serverPublicIP`):
```bash
az network public-ip list \
  --resource-group rg-mcp-apps \
  --query "[].{Name:name, IpAddress:ipAddress}" \
  --output table
```

Use that resource name to bind a unique DNS name to your public IP. Replace `<PUBLIC_IP_RESOURCE_NAME>` with the name printed above, and replace `mcp-server-tejasvi` with your preferred domain prefix (it must be globally unique):
```bash
az network public-ip update \
  --resource-group rg-mcp-apps \
  --name <PUBLIC_IP_RESOURCE_NAME> \
  --dns-name mcp-server-tejasvi
```
*Your permanent domain will be: `mcp-server-tejasvi.eastus2.cloudapp.azure.com`*

### 2. Open HTTP and HTTPS Ports
Open inbound traffic on ports 80 and 443 in the VM's Network Security Group:
```bash
az vm open-port \
  --resource-group rg-mcp-apps \
  --name vm-mcp-server \
  --port 80,443 \
  --priority 1010
```

---

## Step 3: Connect via SSH & Install Docker [COMPLETE]

Now we will connect to the VM using SSH and install Docker.

### 1. Connect via SSH
Run this command in the same Cloud Shell where you created the VM (the SSH private key `id_rsa` is already stored in your Cloud Shell's local directory):
```bash
ssh azureuser@mcp-server-kondulabs.eastus2.cloudapp.azure.com
```
*Type `yes` when prompted to verify the host authenticity.*

### 2. Install Docker
Once logged into the VM, run the following commands to install Docker:
```bash
# Update local packages list
sudo apt-get update

# Install Docker
sudo apt-get install -y docker.io

# Enable and start the Docker daemon
sudo systemctl enable --now docker

# Test Docker installation
sudo docker --version
```

> [!TIP]
> **What happens if I close Cloud Shell or lose my keys?**
> 1. **Option A: Enable Password Authentication (Recommended):** Instead of keys, you can set a permanent password for the `azureuser` login on the VM. In the Azure CLI/Cloud Shell, run:
>    ```bash
>    az vm user update \
>      --resource-group rg-mcp-apps \
>      --name vm-mcp-server \
>      --username azureuser \
>      --password 'YourSecurePassword123!!'
>    ```
>    *Warning: Wrap your password in **single quotes `'`** to prevent bash from expanding exclamation marks (`!` or `!!`) as command history expansions.*
> 2. **Option B: Use SSH Keys:** Keys inside `~/.ssh/` survive Cloud Shell restarts if you have linked Cloud Shell to an Azure Storage account file share.
> 3. **Resetting/Recovering:** If you are locked out, run the `az vm user update` command above from any active Cloud Shell to reset your credentials.

---

## Step 4: Clone Code, Build Container, and Run

Now we will clone the repository on the VM, compile it into a Docker image, and run the container with persistent storage for SQLite.

### 1. Clone the GitHub Repository
Run this on your VM to clone your repository:
```bash
git clone https://github.com/tkondamuru/mcp-auth-server.git
cd mcp-auth-server
```

### 2. Build the Docker Image
Compile and build the application inside Docker:
```bash
sudo docker build -t mcp-server .
```

> [!NOTE]
> **Docker Layer Caching:**
> The initial build takes 1-2 minutes to download base SDKs and restore dependencies. Subsequent builds are extremely fast (10-15 seconds) because Docker caches the SDK layers and NuGet package restorations, only rebuilding modified source code files.

### 3. Run the Container with Volume Persistence and Environment Variables
Create a storage directory `/data` on the VM host. We will map this directory to the container so that your SQLite database `mcp.db` survives container updates and restarts, and pass the required environment variables:
```bash
# Create directory on the host machine
sudo mkdir -p /data

# Run the container in background
sudo docker run -d \
  -p 5000:5000 \
  -v /data:/app/data \
  -e DATABASE_PATH=/app/data/mcp.db \
  -e EXTERNAL_AUTH_ENDPOINT="https://<YOUR_SERVER_HOST>/mobile/mobileauth/authenticate" \
  -e ADMIN_PIN="052512" \
  --restart unless-stopped \
  --name mcp-app \
  mcp-server
```

### 4. Verify Local Connection
Verify that the .NET app is successfully running inside Docker and responding on port 5000:
```bash
curl -I http://localhost:5000
```
*(Should return HTTP 200 OK headers).*

---

## Step 5: Install Nginx, Set Up Reverse Proxy, and Bind SSL with Certbot

To expose the application securely over HTTPS (which is required by OIDC clients), we will configure Nginx as a reverse proxy and request a free SSL certificate from Let's Encrypt.

### 1. Install Nginx and Certbot
Run this command on your VM to install Nginx, Certbot, and the Nginx plugin:
```bash
sudo apt-get update
sudo apt-get install -y nginx certbot python3-certbot-nginx
```

### 2. Configure Nginx Reverse Proxy
Edit the default Nginx site configuration:
```bash
sudo nano /etc/nginx/sites-available/default
```

Wipe the default contents and paste the following server block configuration (replace `mcp-server-kondulabs.eastus2.cloudapp.azure.com` with your actual DNS domain):

```nginx
server {
    listen 80;
    server_name mcp-server-kondulabs.eastus2.cloudapp.azure.com;

    location / {
        proxy_pass http://localhost:5000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded-for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # SSE Keep-Alive and Buffering Disables (Critical for MCP streaming channels)
        proxy_set_header Connection '';
        proxy_http_version 1.1;
        chunked_transfer_encoding off;
        proxy_buffering off;
        proxy_cache off;
        proxy_read_timeout 24h;
    }
}
```

### 3. Verify Nginx Configuration & Restart
Test that your configuration file contains no syntax errors, then restart Nginx:
```bash
# Test syntax
sudo nginx -t

# Restart the service
sudo systemctl restart nginx
```

### 4. Bind Let's Encrypt SSL
Run Certbot to fetch an SSL certificate and automatically inject HTTPS redirection into your Nginx configurations:
```bash
sudo certbot --nginx -d mcp-server-kondulabs.eastus2.cloudapp.azure.com
```
*Follow the interactive prompts to register your email and agree to terms. Select **Redirect** if asked if you want all HTTP traffic routed to HTTPS.*

### 5. Verify Exposed Public Connection
Test that Kestrel is now successfully serving SSL traffic to the internet:
```bash
curl -I https://mcp-server-kondulabs.eastus2.cloudapp.azure.com
```
*(Should return HTTP 200 OK headers).*

---

## Step 6: Deploying a New Code Revision

Whenever you make code updates or bug fixes locally and push them to your GitHub repository, follow this procedure on your Azure VM to deploy the new revision with zero database data loss:

### 1. Connect to the Azure VM via SSH
```bash
ssh azureuser@mcp-server-kondulabs.eastus2.cloudapp.azure.com
```

### 2. Pull Latest Code Changes
Navigate to the repository folder and pull the latest commits:
```bash
cd ~/mcp-auth-server
git pull origin main
```

### 3. Rebuild the Docker Image
Recompile the Docker container using Docker layer caching:
```bash
sudo docker build -t mcp-server .
```

### 4. Replace the Running Container
Stop and remove the existing container, then launch the new container mapping the persistent volume and environment variables:
```bash
# Stop and remove current running instance
sudo docker stop mcp-app
sudo docker rm mcp-app

# Launch updated container instance
sudo docker run -d \
  -p 5000:5000 \
  -v /data:/app/data \
  -e DATABASE_PATH=/app/data/mcp.db \
  -e EXTERNAL_AUTH_ENDPOINT="https://<YOUR_SERVER_HOST>/mobile/mobileauth/authenticate" \
  -e ADMIN_PIN="052512" \
  --restart unless-stopped \
  --name mcp-app \
  mcp-server
```

### 5. Test & Verify Deployment
Run local and public `curl` checks to verify the container is responding and inspect the application logs:
```bash
# Test local container health
curl -I http://localhost:5000

# Test public HTTPS endpoint through Nginx
curl -I https://mcp-server-kondulabs.eastus2.cloudapp.azure.com

# Stream container logs to verify clean startup
sudo docker logs --tail 50 -f mcp-app
```
*(Press `Ctrl+C` to stop streaming logs).*

