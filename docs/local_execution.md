# Local Execution Guide

This guide outlines the step-by-step instructions to run the OIDC Authentication Server and the OIDC Client Application locally for testing and validation.

---

## 1. Prerequisites
- **.NET 10.0 SDK** (for the C# OIDC Server)
- **Python 3.10+** (for hosting the front-end OIDC Client and running tests)

---

## 2. Step-by-Step Launch Instructions

### Step 2.1: Start the OIDC Server
Open a terminal (e.g. PowerShell) in the root of the project (`c:\Development\labs\mcp`) and execute the following:

```powershell
# 1. Set the dynamic base URL for the external mobile authentication service
$env:EXTERNAL_AUTH_ENDPOINT="https://<YOUR_SERVER_HOST>/mobile"

# 2. (Optional) Customize Admin PIN (Default is 052512 if unset)
$env:ADMIN_PIN="052512"

# 3. Build the project to apply code changes
dotnet build

# 4. Launch the OIDC Server
dotnet run --project src/McpServer
```

The OIDC Server will start and bind to **`http://localhost:5000`**.

- **Admin Portal Access:** Navigating to `http://localhost:5000/` or `http://localhost:5000/admin.html` opens the Client Management Portal directly. Unauthenticated users are prompted for an Admin PIN (**`052512`**). Entering the PIN unlocks client registration and URI management without calling external authentication.
- **OIDC Client Authorization:** OIDC authorization redirects (`/login`) remain isolated and perform full external mobile authentication and T&C session sync.


### Step 2.2: Start the OIDC Client Portal
Open a **second** terminal window in the project root (`c:\Development\labs\mcp`) and start a local web server to host the front-end HTML assets:

```powershell
python -m http.server 8000 --directory src/OidcClient
```

This hosts the client portal on **`http://localhost:8000`**.

---

## 3. Testing the Authentication & T&C Orchestration

1. **Access the Client:** Open your browser and navigate to **`http://localhost:8000`**.
2. **Initiate OIDC Login:** Click **"Authenticate via OIDC"**.
   - This initiates the standard **OpenID Connect Authorization Code Flow with PKCE**.
   - It redirects your browser to the OIDC Server login page (`http://localhost:5000/login?ReturnUrl=...`).
3. **Log In:** Enter your user credentials.
   - *Backend Action:* The server intercepts the credentials, POSTs them to the external PGW mobile auth API, extracts the `SessionKey` and `token` on success, downloads the user's active session, sets `TermsOfUse = true`, cleans up read-only properties (`CurrentShipTo`, `LocalIP`, `BlockCount`), and saves the session back to the external server.
4. **Token Generation:** After a successful save, the OIDC server redirects the browser back to `http://localhost:8000/callback.html` with an authorization code. The client automatically exchanges this code at `/connect/token` for standard OIDC tokens.
5. **Inspect the Token:**
   - Copy the issued **Access Token** from the screen.
   - Go to [jwt.io](https://jwt.io) and paste it.
   - Since Access Token Encryption is disabled (`DisableAccessTokenEncryption()`), you can read the claims directly, including:
     - `external_token`: The raw JWT issued by the external server.
     - `session_key`: The session key corresponding to the session we validated and modified.

---

## 4. Troubleshooting
- **Build Errors (Locked files):** If `dotnet build` fails with file locking errors, it means a previous instance of the server is still running. In PowerShell, stop it with:
  ```powershell
  Stop-Process -Name McpServer -Force
  ```
- **Invalid Credentials:** Ensure the `EXTERNAL_AUTH_ENDPOINT` is correctly set to `https://<YOUR_SERVER_HOST>/mobile` (the base address) so the server can resolve the sub-endpoints.


