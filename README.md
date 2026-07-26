# PGW MCP OIDC Authentication Server

A Model Context Protocol (MCP) server gateway integrated with an OAuth 2.0 / OpenID Connect (OIDC) authentication server.

## Features
- **OIDC/OAuth 2.0 Authorization Server:** Powered by [OpenIddict](https://github.com/openiddict/openiddict-core) to issue JWT and Reference access tokens, identity tokens, and refresh tokens.
- **Unified MCP Endpoint:** Exposes a unified `/mcp` EventSource/Server-Sent Events (SSE) route secured via Bearer tokens.
- **Dynamic Client Management Portal:** A secure, glassmorphic admin panel (`/admin.html`) to dynamically register client applications, manage Client IDs, and configure allowed redirect callback URLs.
- **SQLite Storage:** Full Entity Framework Core integration with SQLite for local persistence across restarts, dynamically configured to run within `/home` for persistent Azure App Service mounts.
- **Preconfigured Scopes:** Supports standard `openid`, `profile`, `email`, `offline_access` (refresh tokens), and a dedicated `mcp` scope for tool validation.

## Prerequisites
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) or later.
- Node.js (for testing with the MCP Inspector).

## Running Locally

1. **Build and Run the Server:**
   ```bash
   dotnet build
   dotnet run --project src/McpServer
   ```
   The OIDC authentication server will listen at `http://localhost:5000/`.

2. **Access the Client Management Panel:**
   - Navigate to `http://localhost:5000/admin.html`.
   - Log in using seeded credentials (e.g. Username: `CUS9999`, Password: `test5PGW`).
   - Register Client IDs or configure allowed OIDC Redirect URIs.

3. **Verify/Test with MCP Inspector:**
   - Launch the inspector using the command line:
     ```bash
     npx @modelcontextprotocol/inspector
     ```
   - Open the full URL with the authentication token generated in the terminal.
   - Set **Transport Type** to `SSE` and **URL** to `http://localhost:5000/mcp`.
   - Toggle the `Authorization` header **ON** and populate it with `Bearer <your_access_token>`.
