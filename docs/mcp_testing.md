# MCP Security & Handshake Verification Guide

This guide details how to verify the core OIDC validation and MCP SSE streaming handshake using a temporary Python virtual environment. This process installs the official Python MCP SDK client, runs the test script, and cleans up the environment afterward to keep your system clean.

---

## Verification Steps (using Virtual Environment)

### Step 1: Create the Virtual Environment
Navigate to the root directory of your cloned repository and create a new virtual environment:

**Linux / macOS / Azure VM:**
```bash
python3 -m venv mcp-venv
```

**Windows (PowerShell):**
```powershell
python -m venv mcp-venv
```

---

### Step 2: Activate the Virtual Environment
Activate the environment to isolate the package installations:

**Linux / macOS / Azure VM:**
```bash
source mcp-venv/bin/activate
```

**Windows (PowerShell):**
```powershell
.\mcp-venv\Scripts\Activate.ps1
```

*(You should see `(mcp-venv)` prepended to your command prompt).*

---

### Step 3: Install SDK & Dependencies
Install requests (for token retrieval) and the official `@modelcontextprotocol` Python SDK:

```bash
pip install mcp requests
```

---

### Step 4: Run the Verification Script
Run the test script to obtain the OIDC token, start the SSE connection, and list the tools:

```bash
python test_oidc_mcp.py
```

#### Expected Output
Upon successful connection, you will see output confirming:
1.  **OIDC token retrieval** via password grant Credentials.
2.  **SSE connection establishment** using the Bearer token in the `Authorization` header.
3.  **Completion of the initialize handshake** with the server name and protocol version.
4.  **A list of discovered tools** (including `get_customer_info`).

---

### Step 5: Deactivate and Clean Up (Deletion)
Deactivate the environment and delete the folder to leave your system completely clean:

**1. Deactivate the shell:**
```bash
deactivate
```

**2. Delete the virtual environment directory:**

*   **Linux / macOS / Azure VM:**
    ```bash
    rm -rf mcp-venv
    ```
*   **Windows (PowerShell):**
    ```powershell
    Remove-Item -Recurse -Force mcp-venv
    ```
