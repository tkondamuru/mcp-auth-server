# Step-by-Step Azure VM Deployment Guide

This document tracks the deployment of the PGW OIDC MCP server to an Azure Virtual Machine (Ubuntu Linux).

---

## Progress Tracker

- `[x]` Step 1: Create Resource Group & Ubuntu Virtual Machine
- `[/]` Step 2: Configure Public DNS Name & Open Network Ports (80/443)
- `[ ]` Step 3: Connect via SSH & Install Docker
- `[ ]` Step 4: Clone Code, Build Container, and Run with SQLite Persistence
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

## Step 2: Configure Public DNS Name & Network Ports

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
  --priority 1000
```
