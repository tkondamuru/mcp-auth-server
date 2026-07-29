# PGW MCP OIDC Authentication Server

A Model Context Protocol (MCP) server gateway integrated with an OAuth 2.0 / OpenID Connect (OIDC) authentication server.

---

## Features
- **OIDC/OAuth 2.0 Authorization Server:** Powered by [OpenIddict](https://github.com/openiddict/openiddict-core) to issue access tokens, identity tokens, and refresh tokens.
- **Unified Remote MCP Endpoint:** Exposes a unified `/mcp` EventSource/Server-Sent Events (SSE) route secured via OIDC Bearer tokens.
- **Dynamic Client Management Portal:** A secure, glassmorphic admin panel (`/admin.html`) to dynamically register client applications, manage Client IDs, and configure allowed redirect callback URLs.
- **SQLite Storage:** Full Entity Framework Core integration with SQLite for local persistence across restarts, dynamically configured to support persistent volume mounts (e.g. `/app/data/mcp.db`).
- **Preconfigured Scopes:** Supports standard `openid`, `profile`, `email`, `offline_access` (refresh tokens), and a dedicated `mcp` scope for remote client tool validation.

---

## Prerequisites
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) or later.
- Python 3.10+ (for verifying the server using the official MCP client SDK).

---

## Running Locally

1. **Build and Run the Server:**
   ```bash
   dotnet build
   dotnet run --project src/McpServer
   ```
   The OIDC authentication server will start and listen at `http://localhost:5000/`.

2. **Access the Client Management Panel:**
   - Navigate to `http://localhost:5000/admin.html`.
   - Log in using the seeded test credentials:
     * **Username:** `CUS9999`
     * **Password:** `test5PGW`
   - Register Client IDs or configure allowed OIDC Redirect URIs.

---

## Verification by the Official Reference Client

To prove that the C# server's SSE implementation is fully compliant with the protocol standard, we use the **official Python `@modelcontextprotocol` SDK** to fetch an OIDC token, connect via SSE, and execute a tool.

A script [test_oidc_mcp.py](file:///c:/Development/labs/mcp/test_oidc_mcp.py) is included in the repository root to verify this flow.

### Verification Steps (using Python Virtual Environment)

1. **Create and Activate a Virtual Environment:**
   ```bash
   python -m venv mcp-venv
   
   # Linux/macOS/Azure VM:
   source mcp-venv/bin/activate
   
   # Windows (PowerShell):
   .\mcp-venv\Scripts\Activate.ps1
   ```

2. **Install Dependencies:**
   ```bash
   pip install mcp requests
   ```

3. **Run the Verification Script:**
   ```bash
   python test_oidc_mcp.py
   ```

4. **Verify Interactive Tool Calling:**
   The script will perform the handshake, list the discovered `get_customer_info` tool, and prompt you to input a customer ID:
   ```text
   Requesting OIDC access token from https://mcp.kondulabs.com/connect/token...
   Token retrieved successfully!

   Connecting to MCP SSE endpoint https://mcp.kondulabs.com/mcp...
   Initiating protocol handshake (initialize)...
   Handshake Completed! Server info:
    - Name: PGW-MCP-Auth-Server
    - Version: 1.0.0
    - Protocol Version: 2024-11-05

   Fetching tools from the server...

   Verification Success! Discovered 1 tools:
    - Name: get_customer_info
      Description: Returns PGW Auto Glass customer information and account details for a specified customer code.
      Input Schema: {"type": "object", "properties": {"customerId": {"type": "string", "description": "The customer code (e.g., CUS9999)"}}, "required": ["customerId"]}

   Enter customer ID to query (or press Enter for 'CUS9999'): CUS9999

   Calling tool 'get_customer_info' with arguments: {'customerId': 'CUS9999'}...

   Tool Execution Response:
   [Customer CUS9999] Name: PGW Auto Glass Corporate HQ, Status: Active, Balance: $0.00
   ```

5. **Clean Up:**
   ```bash
   deactivate
   
   # On Linux/macOS:
   rm -rf mcp-venv
   # On Windows (PowerShell):
   Remove-Item -Recurse -Force mcp-venv
   ```
