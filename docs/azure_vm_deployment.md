# Step-by-Step Azure VM Deployment Guide

This document tracks the deployment of the PGW OIDC MCP server to an Azure Virtual Machine (Ubuntu Linux).

---

## Progress Tracker

- `[x]` Step 1: Create Resource Group & Ubuntu Virtual Machine
- `[x]` Step 3: Connect via SSH & Install Docker
- `[/]` Step 4: Clone Code, Build Container, and Run with SQLite Persistence
- `[ ]` Step 5: Install Nginx, Set Up Reverse Proxy, and Bind SSL with Certbot

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
> 1. **Keys are Persistent:** Azure Cloud Shell mounts a persistent Azure File Share under `/home/<username>`. Your generated SSH keys inside `~/.ssh/` are saved on this share and will **not** be lost when you close the session.
> 2. **Connecting from local PC:** You can download your private key file from Cloud Shell (using the "Upload/Download" button in the Cloud Shell toolbar) to your local PC.
> 3. **Resetting Credentials:** If you ever lose your keys entirely, you can reset them without rebuilds. In the Azure Portal, navigate to your **Virtual Machine** -> scroll down to the **Help** section -> click **Reset password** -> select **Reset SSH public key** (or reset password) to write new login credentials to the VM.

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

### 3. Run the Container with Volume Persistence
Create a storage directory `/data` on the VM host. We will map this directory to the container so that your SQLite database `mcp.db` survives container updates and restarts:
```bash
# Create directory on the host machine
sudo mkdir -p /data

# Run the container in background
sudo docker run -d \
  -p 5000:80 \
  -v /data:/app/data \
  -e DATABASE_PATH=/app/data/mcp.db \
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

