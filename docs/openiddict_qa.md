# OpenIddict Q&A Reference Document

This reference document tracks configuration concepts, security decisions, and API mechanics for OpenIddict in the PGW OIDC MCP server.

---

### Q1: What is the purpose of `/connect/authorize`, `/connect/token`, and `/connect/userinfo` endpoints?

*   **`/connect/authorize` (Authorization Endpoint):**
    *   *Role:* Used in interactive web-based login flows (e.g., Authorization Code Flow) to authenticate users and obtain consent.
    *   *Mechanism:* Redirects the user's browser to the login page. After successful login, it redirects the user back to the client application with a temporary **Authorization Code**.
*   **`/connect/token` (Token Endpoint):**
    *   *Role:* Exchanges authorization codes, user credentials, or refresh tokens for active cryptographic tokens.
    *   *Mechanism:* A secure machine-to-machine POST endpoint. It accepts client/user credentials and returns a JSON payload containing the **Access Token**, **ID Token**, and/or **Refresh Token**.
*   **`/connect/userinfo` (Userinfo Endpoint):**
    *   *Role:* Retrieves profile details and claims about the authenticated user.
    *   *Mechanism:* The client requests this endpoint by passing the acquired access token in the `Authorization: Bearer <token>` header. The server validates the token and returns a JSON payload containing user identity details (e.g. subject ID, username).

---

### Q2: What is the Authorization Code Flow (with PKCE)?

The **Authorization Code Flow** is the standard OAuth2/OIDC flow designed for web and mobile client applications.

*   **Step 1: Initiate Redirect:** The client redirects the user to `/connect/authorize`, passing a cryptographically generated challenge (`code_challenge`)—this is the **PKCE** protection.
*   **Step 2: User Login:** The user authenticates directly on the server's login page (the client application never sees the user's password).
*   **Step 3: Auth Code Issued:** The server redirects the user's browser back to the client application's callback URL, appending a short-lived, single-use **Authorization Code**.
*   **Step 4: Token Request:** The client makes a direct backend POST request to `/connect/token` sending the Authorization Code and the original secret verifier (`code_verifier`).
*   **Step 5: Tokens Issued:** The server validates the verifier against the original challenge. If matched, it returns the **Access Token**, **ID Token**, and **Refresh Token** directly to the client.

---

### Q3: What is Proof Key for Code Exchange (PKCE)? Do we expect clients to have a pre-configured secret?

No. Unlike traditional backend confidential clients that have a pre-configured, static `client_secret` hardcoded in config files, **PKCE (pronounced "pixie") generates a transient, dynamic secret at runtime for each login request.**

*   **How it works:**
    1.  **Generate `code_verifier`:** When a user clicks "Log In", the client application generates a cryptographically random string (the `code_verifier`). This is the "secret".
    2.  **Generate `code_challenge`:** The client hashes this verifier (using SHA-256) to create the `code_challenge`.
    3.  **Register Challenge:** The client sends the challenge to `/connect/authorize`. The OIDC server saves it.
    4.  **Exchange with Verifier:** When swapping the authorization code for tokens at `/connect/token`, the client sends the raw `code_verifier`. The server hashes it; if it matches the registered challenge, the request is approved.
*   **Why it's secure:** If a malicious app intercepts the Authorization Code in transit, they cannot exchange it for tokens because they do not know the `code_verifier` (which was never sent over the open redirect URL).
*   **Do developers manually manage this?** No. Modern client authentication libraries (like MSAL, AppAuth, or oidc-client-ts) generate the verifiers, hash the challenges, and manage the token swap exchange behind the scenes automatically.

---

### Q4: In PKCE, how do the client and server exchange the hash without a shared "hash key"?

They do not use a shared cryptographic key because **SHA-256 is a keyless, one-way hashing algorithm**. 

Any system in the world running SHA-256 on the string `"hello"` will get the exact same result: `2cf24dba...`. Because hashing is deterministic, no key exchange is required. The verification works purely through sequential data transmission over secure HTTPS:

1.  **Client Hashing (Initial Request):** The client generates the random `code_verifier` string, hashes it locally using SHA-256 to get the `code_challenge`, and sends the **challenge** via the browser URL to `/connect/authorize`.
2.  **Server Storage:** The server receives the `code_challenge` and saves it in the database, mapping it to the newly generated `authorization_code` it issues.
3.  **Direct Backend Exchange:** The client sends the raw **`code_verifier`** directly in the POST body to `/connect/token` (never in the browser URL).
4.  **Server Hashing & Validation:** The server runs the same SHA-256 algorithm on the incoming `code_verifier`. It compares the output with the `code_challenge` stored in its database. If they match, it proves the party requesting the tokens is the same party that initiated the login.

---

### Q5: What is the Resource Owner Password Credentials (Password) Flow?

The **Resource Owner Password Credentials Flow** allows client applications to directly collect a user's password and submit it to exchange for tokens.

*   **How it works:**
    *   The client app displays a custom login form, collects the raw `username` and `password`, and POSTs them directly to the `/connect/token` endpoint (`grant_type=password`).
    *   The server validates these credentials and returns the active JWT tokens directly.
*   **Security & Use Case:**
    *   *Deprecated in OAuth 2.1:* It should only be used for highly trusted first-party apps (e.g. CLI tools) because it requires users to trust the client app with their raw password.
    *   *Usage in this project:* We enabled it specifically to make API testing and verification (e.g. from PowerShell scripts) simple without requiring browser redirects.

---

### Q6: What is the Refresh Token Flow?

The **Refresh Token Flow** allows client applications to obtain new access tokens silently without prompting the user to re-authenticate.

*   **How it works:**
    *   Access tokens are short-lived (e.g. 1 hour) for security.
    *   When the access token expires, the client makes a secure POST request to `/connect/token` sending the long-lived **Refresh Token** (`grant_type=refresh_token`).
    *   The server validates the refresh token and returns a new Access Token (and optionally a new Refresh Token).
*   **Key Benefits:**
    *   *Seamless UX:* Keeps the user logged in without interruption.
    *   *Security Isolation:* Keeps Access Tokens short-lived, minimizing the risk if one is leaked.
    *   *Instant Revocation:* If a device is lost or compromised, the administrator can revoke the Refresh Token in the database, immediately blocking any new access tokens.

---

### Q7: If we redirect users to the server's login page, why is the Password Flow still considered a security risk?

Because **redirection only happens in the Authorization Code Flow, not in the Password Flow.** The two flows handle credentials completely differently:

*   **Authorization Code Flow (Secure Redirection):**
    *   The client application redirects the user’s browser to the OIDC server's login web page.
    *   The user inputs their password directly onto the server’s page.
    *   **Result:** The client application **never** sees, handles, or stores the password. It only receives a token after the login completes.
*   **Password Flow (Resource Owner Password Credentials):**
    *   **No redirection occurs.** The user types their password directly into the client application's own UI (like a form inside a mobile app, React frontend, or a CLI input).
    *   The client application collects this plain text password, packages it, and POSTs it directly to `/connect/token`.
    *   **Result:** The client application has **full access to the raw plain text password**. If a client app contains a malicious dependency or is compromised, it can easily capture and steal the user's password. This direct exposure is why the flow is deprecated in OAuth 2.1.

---

### Q8: Why are we enabling the Password Flow in this project? Is it specifically for the PowerShell test script?

**Yes. The primary reason is to allow headless, automated testing (like our `verify.ps1` script or cURL) to acquire access tokens easily.**

*   **Headless Automation:** Automated scripts, CI/CD runners, and background daemons do not have a graphical user interface (GUI) or browser capability. Implementing the Authorization Code Flow in a script is difficult because it requires rendering a browser login page, capturing redirects, and copying codes.
*   **Simplicity:** The Password Flow allows scripts to execute a single, standard HTTP POST request directly to `/connect/token` to acquire a valid token in seconds.
*   **Security Context (Trusted Client):** Since the `verify.ps1` script runs locally or inside your secure internal network, and is written by your own team (a trusted first-party environment), the credential theft risks associated with third-party client apps do not apply here. 
*   **Production Plan:** For production deployments, public-facing applications (like the actual MCP client or web interfaces) will be configured to use the **Authorization Code Flow with PKCE** for secure login, while the Password Flow can be restricted or disabled.

---

### Q9: How can we restrict the Password Flow so it is only available in CI/CD or staging environments, but disabled in production?

There are three main ways to enforce this restriction at different layers:

*   **1. Environment-Conditional Server Configuration (Recommended):**
    In ASP.NET Core, you can conditionally enable the grant flow depending on the host environment (e.g. `Development` or `Staging` vs `Production`):
    ```csharp
    builder.Services.AddOpenIddict()
        .AddServer(options =>
        {
            options.AllowAuthorizationCodeFlow();
            
            // Only allow password flow in non-production environments
            if (builder.Environment.IsDevelopment() || builder.Environment.IsStaging())
            {
                options.AllowPasswordFlow();
            }
        });
    ```
*   **2. Client-Specific Grant Permissions:**
    In your database seeder or setup script, only assign the `Permissions.GrantTypes.Password` capability to a dedicated, internal client ID (e.g., `mcp-cicd-client`), while restricting your primary client application (`mcp-client`) to `Permissions.GrantTypes.AuthorizationCode` and `Permissions.GrantTypes.RefreshToken`.
*   **3. Network / IP Whitelisting (API Gateway or Middleware):**
    Configure Nginx, IIS, or an API gateway to reject incoming POST requests to `/connect/token` containing the `grant_type=password` body parameter unless the request originates from the trusted IP address range of your CI/CD runner agents (e.g., GitHub Enterprise runners, local build nodes). Alternatively, this check can be implemented inside a custom ASP.NET Core endpoint filter.

---

### Q10: Why do we need a Certificate Authority (CA) certificate for token signing if PKCE already prevents code hijacking?

They protect against two entirely different stages of attack. **PKCE secures the transport of the code before tokens are issued, while the certificate secures the trust and integrity of the tokens after they are issued.**

Here is the breakdown of why both are essential:

*   **1. What PKCE (`code_challenge` / `code_verifier`) protects:**
    *   *Attack Vector:* **Authorization Code Interception.**
    *   *The Problem:* An attacker intercepts the redirect URL containing the temporary authorization code before the client app can read it.
    *   *The Protection:* PKCE prevents the attacker from swapping that intercepted code for tokens because the attacker doesn't know the client's dynamically generated `code_verifier`.
*   **2. What the Certificate (Private Key) protects:**
    *   *Attack Vector:* **Token Forgery and Tampering.**
    *   *The Problem:* Once the client obtains a JWT Access Token, it sends this token to downstream services (like your `/mcp` tool API). Without a signature, how does the API know the client didn't just write a fake token in Notepad (e.g., `{"username": "admin", "role": "root"}`) and send it?
    *   *The Protection:* The OIDC server signs the token payload using its private key (RS256/ES256). The API validates this signature using the OIDC server's public key (retrieved from `/.well-known/jwks`). If the signature matches, it guarantees the token:
        1.  Was generated by your trusted OIDC server (authenticity).
        2.  Was not modified in transit (integrity).
*   **3. Why use a Certificate Authority (CA) Cert?**
    *   Using a certificate signed by a trusted internal CA ensures that your internal APIs, clients, and reverse proxies (like IIS or Nginx) automatically trust the OIDC server's signing keys without throwing certificate validation errors, avoiding the security risks and warnings of self-signed certificates.

---

### Q11: Where in the code are endpoints like `/mcp` forced to authenticate and validate the OIDC token?

The authentication enforcement is configured inside [McpEndpoints.cs](file:///c:/Development/labs/mcp/src/McpServer/Mcp/McpEndpoints.cs) using ASP.NET Core’s native authorization routing.

Here is the exact code snippet that forces authentication:

```csharp
public static IEndpointRouteBuilder MapMcpEndpoints(this IEndpointRouteBuilder app)
{
    // Define the OIDC Validation authentication scheme
    var mcpAuthScheme = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
    var authAttribute = new AuthorizeAttribute { AuthenticationSchemes = mcpAuthScheme };

    // 3. Modern Streamable HTTP GET /mcp (SSE stream)
    app.MapGet("/mcp", async (HttpContext context) => { ... })
       .RequireAuthorization(authAttribute); // <-- Forces validation

    // 4. Modern Streamable HTTP POST /mcp (JSON-RPC processing)
    app.MapPost("/mcp", async (HttpContext context) => { ... })
       .RequireAuthorization(authAttribute); // <-- Forces validation

    return app;
}
```

*   **How it works:**
    1.  **Authentication Scheme:** `OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme` tells the server to look for an incoming `Authorization: Bearer <token>` header and validate it using the local OIDC token validation service.
    2.  **`RequireAuthorization(...)`:** When appended to a mapped route, ASP.NET Core intercepts any incoming request to `/mcp`. If a valid OIDC token is not present in the headers, the routing middleware terminates the request immediately and returns `401 Unauthorized` without running any of our MCP JSON-RPC logic.

---

### Q12: Where in the code is the OIDC token validation actually performed, and how does it use our custom certificate?

The validation configuration is defined in [Program.cs](file:///c:/Development/labs/mcp/src/McpServer/Program.cs) during the startup services configuration phase. The actual cryptographic signature verification happens automatically within OpenIddict's validation middleware.

Here is the exact code snippet that links the validation services:

```csharp
// Inside Program.cs -> builder.Services.AddOpenIddict()

// 1. Configure the Server options (where credentials are registered)
.AddServer(options =>
{
    // ...
    // Load custom CER+KEY certificate
    var certificate = publicCert.CopyWithPrivateKey(rsa);
    
    // Register the certificate for signing and encryption
    options.AddSigningCertificate(certificate)
           .AddEncryptionCertificate(certificate);
})

// 2. Register the Validation options
.AddValidation(options =>
{
    // Import configuration settings from the local OpenIddict server instance
    options.UseLocalServer();
    options.UseAspNetCore();
});
```

*   **How the validation process executes:**
    1.  **Shared Configuration (`UseLocalServer()`):** This tells OpenIddict's validation subsystem to look at the OIDC server running in the *same* process to get the signing credentials. This shares our loaded `pgwintraapps` certificate directly with the validation engine.
    2.  **Middleware Interception:** When a request hits `/mcp`, the ASP.NET Core authentication middleware extracts the JWT token from the headers and hands it to the OpenIddict Validation handler.
    3.  **Cryptographic Signature Verification:** Under the hood, OpenIddict reads the token header, extracts the signature, and uses the **public key** of our registered certificate (`pgwintraapps.cer`) to run the RSA decryption/verification algorithm (typically RS256).
    4.  **Security Result:** If the signature math matches the token payload, the token is verified. OpenIddict converts the JWT claims into a C# `ClaimsPrincipal` user profile (populating `context.User`) and permits the request to execute the `/mcp` endpoints. If it fails, the middleware returns `401 Unauthorized` automatically.

---

### Q13: If we have an external, separate Web API application, how would it validate tokens issued by our OIDC server?

Since `options.UseLocalServer()` only works for in-process validation, an external Web API (Resource Server) validates tokens using one of two standard architectures:

#### Method A: Stateless JWT Validation via OIDC Discovery (Recommended)
This method is fast, decentralized, and does not require a network trip for every API request. The remote API dynamically downloads and caches our OIDC server's **public keys**.

1.  **Configure JWT Bearer in the remote API:**
    ```csharp
    // In the remote API's Program.cs:
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = "https://mcp.mycompany.com/"; // URL of our OIDC Server
            options.Audience = "mcp";                         // Expected scope/audience
            options.RequireHttpsMetadata = true;
        });
    ```
2.  **How it executes:**
    *   **On Startup:** The remote API calls our OIDC server's discovery endpoint (`https://mcp.mycompany.com/.well-known/openid-configuration`) to locate the JWKS endpoint (Json Web Key Set).
    *   **Key Download:** It downloads the server's public certificate key (`pgwintraapps.cer`) and caches it in memory.
    *   **Request Verification:** When a JWT comes in, the remote API decodes it locally and verifies the signature using the cached public key. No database queries or network connections are made to the auth server during requests.

---

#### Method B: Centralized Token Introspection (Active Validation)
If you want to support instant revocation check of tokens, or if the tokens issued are opaque (not readable JWTs), the remote API calls our auth server to check validation status for *each* request.

1.  **Configure Introspection in the remote API:**
    ```csharp
    // In the remote API's Program.cs:
    builder.Services.AddOpenIddict()
        .AddValidation(options =>
        {
            options.SetIssuer("https://mcp.mycompany.com/");
            options.UseIntrospection(options =>
            {
                options.SetClientId("remote-api-service");
                options.SetClientSecret("api-shared-secret");
            });
            options.UseSystemNetHttp();
            options.UseAspNetCore();
        });
    ```
2.  **How it executes:**
    *   For **every** incoming request, the remote API sends a POST request to our OIDC server's introspection endpoint (`/connect/introspect`).
    *   The OIDC server verifies the token against its active database and returns `{ "active": true, "sub": "CUS9999", ... }`.
    *   *Pros:* The remote API knows instantly if a token was revoked.
    *   *Cons:* Significant network and database overhead on the OIDC server.

---

### Q14: What is the `/.well-known/openid-configuration` endpoint?

It is the standard **OpenID Connect Discovery Document** (RFC 8414 / OIDC Discovery 1.0 specification).

Every fully compliant OIDC Identity Provider (like Google, Okta, Keycloak, or our OpenIddict server) exposes this public JSON metadata endpoint at the root of its domain.

*   **What it contains:**
    It acts as a public directory of configuration data telling client apps and APIs exactly how to interact with the server. Key fields returned in the JSON payload include:
    *   `issuer`: The OIDC server's official URL.
    *   `authorization_endpoint`: The login redirect endpoint (e.g. `/connect/authorize`).
    *   `token_endpoint`: The token exchange endpoint (e.g. `/connect/token`).
    *   `userinfo_endpoint`: The user claims endpoint (e.g. `/connect/userinfo`).
    *   `jwks_uri`: The public keys endpoint containing the public certificates used to verify signatures (e.g. `/connect/jwks`).
    *   `scopes_supported`: List of claims/scopes available (e.g., `openid`, `profile`, `mcp`).
    *   `grant_types_supported`: Allowed flows (e.g. `authorization_code`, `password`, `refresh_token`).
*   **Why it is extremely useful:**
    It enables **Zero-Configuration Client Setup**. When configuring a client SDK or remote API, instead of manually copy-pasting 10 different endpoint paths and public certificate keys into config files, the developer only supplies the single base OIDC URL (the `Authority`). The client SDK automatically fetches this JSON document, parses all endpoints, downloads the public keys, and configures itself dynamically.

---

### Q15: Why does the server configure Cookie Authentication in addition to OIDC (Token) Authentication?

They are designed for two different actors and serve different purposes during the login lifecycle: **Cookies manage the browser session between the user and the OIDC server, while OIDC tokens manage access between client applications and downstream APIs.**

Here is the breakdown of why both are necessary:

*   **1. Cookie Authentication (For Humans & Browser Sessions):**
    *   *Where it is used:* Inside the OIDC server itself (on pages like `/login` and `/connect/authorize`).
    *   *The Purpose:* When a user enters their credentials on the `/login` screen, the server needs to "remember" who they are as they navigate between the login page, the consent page, and the redirection page. It issues a temporary **Session Cookie**.
    *   *Single Sign-On (SSO):* Because of this cookie, if the user opens a second client application that uses the same OIDC server, the server recognizes the session cookie and automatically logs the user in without prompting them for their password again.
*   **2. OIDC/Token Authentication (For Client Apps & Backend APIs):**
    *   *Where it is used:* On endpoints exposed to applications and services (like `/mcp` or `/connect/userinfo`).
    *   *The Purpose:* The client application does not have access to the user's session cookies. Instead, the client presents a short-lived **JWT Access Token** in the `Authorization: Bearer <token>` header of its API requests.
    *   *Security Benefit:* APIs do not need to manage browser cookie sessions, protecting them from CSRF (Cross-Site Request Forgery) attacks and allowing stateless execution.
*   **3. How they work together (Login Flow):**
    1.  User clicks login on the Client App -> Redirected to `/connect/authorize` on OIDC Server.
    2.  OIDC Server checks for a session cookie. None found -> Redirects user to `/login` page.
    3.  User inputs credentials -> Server validates them and sets a **Session Cookie** in the browser.
    4.  Browser redirects back to `/connect/authorize` (sending the session cookie).
    5.  OIDC Server validates the cookie, approves the user, and redirects the browser back to the Client App with an authorization code.
    6.  Client App exchanges the code for **JWT Tokens** at `/connect/token`.
    7.  Client App calls `/mcp` API using the **JWT Token** (cookies are no longer used).

---

### Q16: Can you trace the exact URL-by-URL cycle of the OIDC Authorization Code Flow, showing exactly where cookie authentication is used?

Here is the chronological lifecycle of a user logging in, starting from the client application and showing the transition between browser cookie sessions and JWT Bearer tokens:

#### 1. The Starting URL (Client App Initiation)
The user opens their web client app (running at `http://localhost:3000`) and clicks "Log In". The client app redirects the user's browser to the OIDC server's authorization endpoint:
*   **Request URL:**
    `http://localhost:5000/connect/authorize?client_id=mcp-client&redirect_uri=http://localhost:5000/callback&response_type=code&scope=openid profile mcp&code_challenge=challenge_xyz&code_challenge_method=S256`

#### 2. The Login Redirect (Triggered by Cookie Middleware)
The OIDC server receives the request. Since the browser has **no active session cookie** for `localhost:5000`, the server terminates the request and redirects the browser to the login page:
*   **Redirect URL:**
    `http://localhost:5000/login?ReturnUrl=%2Fconnect%2Fauthorize%3Fclient_id%3Dmcp-client%26...`

#### 3. Credentials Submission & Cookie Setting
The browser loads the `/login` page and renders the login form. The user enters their credentials (`CUS9999` / `test5PGW`) and clicks "Submit". The browser POSTs the form back to the server:
*   **POST Request:** `POST http://localhost:5000/login?ReturnUrl=...`
*   **Server Processing:** The server validates the password, creates a `ClaimsPrincipal`, and calls `await context.SignInAsync(...)`.
*   **Response Header (Cookie Set):** The server sends a response redirecting the user back to the `ReturnUrl`, appending a `Set-Cookie` header containing the encrypted ASP.NET Core session cookie:
    `Set-Cookie: .AspNetCore.Cookies=encrypted_session_data; path=/; HttpOnly; SameSite=Lax`

#### 4. The Authorized Access (Cookie Sent!)
The browser redirects back to the original authorization endpoint. Because the domain is `localhost:5000`, the browser automatically attaches the cookie:
*   **Request URL:**
    `http://localhost:5000/connect/authorize?client_id=mcp-client&...`
*   **Request Header:** `Cookie: .AspNetCore.Cookies=encrypted_session_data`
*   **Server Processing:** The server's cookie middleware decrypts the cookie, populates `context.User`, and passes the request to the endpoint handler. The handler generates a temporary **Authorization Code** (`AUTH_CODE_789`).

#### 5. Returning to the Client (Callback Redirect)
The OIDC server redirects the user's browser back to the client application's callback URI, appending the single-use code:
*   **Redirect URL:**
    `http://localhost:5000/callback?code=AUTH_CODE_789`

#### 6. Code to Token Exchange (No Cookies Used)
The client application reads the code from the URL and performs a direct, backend machine-to-machine POST request to exchange the code + PKCE `code_verifier` for JWT tokens:
*   **POST Request:** `POST http://localhost:5000/connect/token`
*   **POST Body:** `grant_type=authorization_code&code=AUTH_CODE_789&client_id=mcp-client&code_verifier=verifier_xyz`
*   **Response (Tokens Issued):** The server returns the cryptographic JSON payload:
    `{ "access_token": "JWT_ACCESS_TOKEN_ABC", "id_token": "JWT_ID_TOKEN_DEF" }`

#### 7. Accessing Backend APIs (JWT Bearer Auth)
Now authenticated, the client app calls secure API endpoints (like `/mcp` or `/connect/userinfo`), bypassing cookies and passing the JWT token instead:
*   **Request URL:** `POST http://localhost:5000/mcp`
*   **Request Header:** `Authorization: Bearer JWT_ACCESS_TOKEN_ABC`
*   **Server Processing:** The server validates the JWT signature using the certificate, authorizes the request, and returns the response. Cookies are no longer involved.

---

### Q17: Where are the Callback (Redirect) URLs defined? Are they part of the Authentication Cycle?

**Yes, they are a critical security validation step in the Authentication Cycle.** They are defined in the **Client Application Metadata** registered on the OIDC server.

In this project, they are defined in [DbSeeder.cs](file:///c:/Development/labs/mcp/src/McpServer/Services/DbSeeder.cs#L38-L45) when we register the `mcp-client` application details into the database:

```csharp
// Inside DbSeeder.cs
await manager.CreateAsync(new OpenIddictApplicationDescriptor
{
    ClientId = "mcp-client",
    DisplayName = "MCP Client Application",
    ClientType = ClientTypes.Public,
    RedirectUris =
    {
        new Uri("http://localhost:5000/callback"),
        new Uri("http://localhost:3000/callback"),
        new Uri("http://localhost:5080/callback")
    }
});
```

*   **How they participate in the Authentication Cycle (Security Check):**
    1.  **Client Request:** When the client app redirects the user in Step 1, it includes the parameter `redirect_uri=http://localhost:3000/callback`.
    2.  **OIDC Server Validation:** The OIDC server receives the request and immediately queries its database. It checks if the requested `redirect_uri` matches one of the pre-registered URIs in the seeder.
    3.  **Aborting on Mismatch:** If they do not match (even by one character), the server halts the flow immediately and returns an `invalid_client` error.
*   **Why this is crucial (Preventing Redirect Hijacking):**
    If the OIDC server did not validate the redirect URI, an attacker could build a phishing link pointing to your OIDC server with the query parameter:
    `?client_id=mcp-client&redirect_uri=https://attacker-evil-site.com/steal`
    The user, seeing the legitimate OIDC URL and login screen, would type their password and approve the login. The server would then redirect the browser (carrying the highly sensitive **Authorization Code**) directly to `attacker-evil-site.com`. By validating redirect URIs, the server guarantees OIDC codes are only sent to trusted, pre-registered client application endpoints.

---

### Q18: So the client application must include a `redirect_uri` in its request, and it has to match one of the URIs set in `DbSeeder.cs` exactly?

**Yes. The match must be exact, character-for-character.** 

If the client application requests authentication and passes a redirect URI that is not registered, OpenIddict will immediately block the request and return an error.

*   **Rule of Exact Matching:**
    *   **Case Sensitivity:** `http://localhost:3000/callback` vs `http://localhost:3000/Callback` will fail.
    *   **Trailing Slashes:** `http://localhost:3000/callback` vs `http://localhost:3000/callback/` will fail.
    *   **Protocol:** `http://...` vs `https://...` will fail.
*   **Why it's set in `DbSeeder.cs`:**
    In our system, OpenIddict stores client application configurations inside our EF Core database. The [DbSeeder.cs](file:///c:/Development/labs/mcp/src/McpServer/Services/DbSeeder.cs#L38-L45) acts as the initialization script that inserts these allowed redirect URIs into the database when the server first starts up.
*   **For Custom Client Ports:**
    If you change the port of your client web application (e.g. running on `http://localhost:5156` instead of `http://localhost:3000`), you must add that new URL to the `RedirectUris` list in [DbSeeder.cs](file:///c:/Development/labs/mcp/src/McpServer/Services/DbSeeder.cs#L43) and re-seed the database so the OIDC server recognizes and accepts it during the login cycle.

---

### Q19: When `/connect/token` returns `Results.SignIn()`, what are `access_token` and `id_token`, and do downstream APIs need both?

When `Results.SignIn(principal)` executes, OpenIddict packages two distinct JWT tokens into the JSON response: `access_token` and `id_token`.

They serve two completely different audiences and purposes:

*   **1. `access_token` (OAuth2 Authorization Token — For Downstream APIs):**
    *   *Intended Audience:* Downstream Resource Servers and APIs (like our `/mcp` endpoints).
    *   *Purpose:* Proves that the client application has **authorization** (permission) to perform actions on behalf of the user.
    *   *Usage:* The client attaches this token to the HTTP header of API calls: `Authorization: Bearer <access_token>`.
*   **2. `id_token` (OpenID Connect Identity Token — For the Client App UI):**
    *   *Intended Audience:* The Client Application itself (e.g. React frontend, Flutter app, or web portal).
    *   *Purpose:* Proves **authentication** (who the user is). It contains profile claims like `sub`, `name`, `email`, and `auth_time`.
    *   *Usage:* The client app decodes the `id_token` locally to display the user's name (e.g., *"Logged in as CUS9999"*) in the top navigation bar. The client app never sends this token to APIs.
*   **3. Do Downstream APIs need both?**
    *   **No. Downstream APIs ONLY need the `access_token`.**
    *   APIs reject or ignore `id_token` because `id_token` is meant for client UI identity verification, whereas `access_token` contains the scopes (`mcp`), audience, and permissions required to execute API methods.
*   **4. How C# `Results.SignIn` controls destination:**
    In [Program.cs](file:///c:/Development/labs/mcp/src/McpServer/Program.cs#L299), we explicitly dictate which token gets which claim using `.SetDestinations()`:
    ```csharp
    identity.AddClaim(new Claim(OpenIddictConstants.Claims.Name, username)
        .SetDestinations(
            OpenIddictConstants.Destinations.AccessToken,   // Includes claim in access_token for APIs
            OpenIddictConstants.Destinations.IdentityToken  // Includes claim in id_token for Client UI
        ));
    ```

---

### Q20: How do we configure expiration times for Access Tokens in OpenIddict, and how do we request a Refresh Token?

#### 1. Configuring Expiration Times (Lifespans)
Token lifespans are defined in the OIDC server configuration block inside [Program.cs](file:///c:/Development/labs/mcp/src/McpServer/Program.cs). You set them using `SetAccessTokenLifespan()`, `SetRefreshTokenLifespan()`, and `SetIdentityTokenLifespan()` on the server options.

Example configuration:
```csharp
builder.Services.AddOpenIddict()
    .AddServer(options =>
    {
        // ...
        // Configure Custom Token Lifespans
        options.SetAccessTokenLifespan(TimeSpan.FromMinutes(30))      // Access Token valid for 30 minutes
               .SetIdentityTokenLifespan(TimeSpan.FromMinutes(15))    // ID Token valid for 15 minutes
               .SetRefreshTokenLifespan(TimeSpan.FromDays(14));       // Refresh Token valid for 14 days
    });
```
*If left unconfigured, OpenIddict applies standard secure defaults (Access Tokens: 1 hour, Refresh Tokens: 14 days).*

#### 2. How to Request and Receive a Refresh Token
By default, the `/connect/token` endpoint **does not return a Refresh Token** unless the client explicitly requests it. To acquire a Refresh Token:

1.  **Request `offline_access` Scope:**
    During the initial authentication request to the `/connect/authorize` endpoint, the client must include the **`offline_access`** scope parameter:
    `scope=openid profile mcp offline_access`
2.  **Server Approves Request:**
    The OIDC Server verifies that:
    - The client is allowed to request `offline_access` (seeded in [DbSeeder.cs](file:///c:/Development/labs/mcp/src/McpServer/Services/DbSeeder.cs#L57) via `Permissions.Prefixes.Scope + Scopes.OfflineAccess`).
    - The client is permitted to use the Refresh Token flow (seeded via `Permissions.GrantTypes.RefreshToken`).
3.  **Response:**
    When the client swaps the authorization code at `/connect/token`, OpenIddict notes the granted `offline_access` scope and automatically adds the `"refresh_token": "..."` field to the response JSON payload.

---

### Q21: What is the `id_token` (Identity Token)? Is it used to call `/userinfo`? And why does it have such a short lifespan?

The `id_token` is a formatted statement of identity issued by the OIDC server. It is strictly for client application consumption.

Here is the breakdown of its role, validation, and security:

*   **1. Is it used to call `/userinfo`?**
    *   **No.** To request the `/connect/userinfo` endpoint, the client **must** pass the `access_token` in the `Authorization: Bearer <access_token>` header.
    *   If you send the `id_token` to `/connect/userinfo`, Kestrel/OpenIddict will return a `401 Unauthorized` response. The `/userinfo` endpoint is a backend API resource, meaning it requires the authorization authority of the `access_token`.
*   **2. What is the `id_token` actually used for?**
    *   The `id_token` exists to inform the client UI that the user successfully logged in and provides profile attributes directly.
    *   It contains claims showing **who** signed in (e.g. `sub: CUS9999`), **when** they signed in (`auth_time`), and **how** they authenticated (`amr`).
    *   Without an `id_token`, the client app would have to make an extra API request to `/userinfo` just to know the name of the user who logged in. The `id_token` allows the client to customize the UI instantly.
*   **3. Why does the `id_token` have such a short lifespan (e.g., 1 to 5 minutes)?**
    *   **Single-Use Proof:** The `id_token` is a "point-in-time" assertion of an authentication event. Once the client application receives it, parses it, validates the signature, and launches the user session (writing name and profile to local memory), the `id_token` has done its job. It is never needed again.
    *   **Not Used for API Calls:** Since the client uses the `access_token` for calling APIs and the `refresh_token` for renewing access, the `id_token` doesn't need to stay valid.
    *   **Security Mitigation:** Keeping the lifespan short ensures that even if an attacker intercepts the `id_token` later on (e.g. from browser cache, logs, or history), the token is already expired and cannot be replayed or used to impersonate the user.

---

### Q22: Is there a JavaScript library to decode the `id_token` into claims? Is this handled client-side or server-side?

**Yes.** Since the `id_token` is a standard JSON Web Token (JWT), it can be easily decoded on the client side using either a lightweight JS library or vanilla JavaScript.

#### 1. Popular Client-Side Libraries
*   **`jwt-decode` (Lightweight Decoder):**
    A tiny library (less than 1KB) built specifically to parse JWT payloads on the client side without validating the cryptographic signature.
    ```javascript
    import { jwtDecode } from "jwt-decode";
    const claims = jwtDecode(idToken);
    console.log(claims.name, claims.email);
    ```
*   **`oidc-client-ts` / `msal-browser` (Full OIDC Clients):**
    These are complete authentication libraries for Single Page Applications (SPAs). They handle the redirects, PKCE, token requests, **and** automatically decode/verify the `id_token` signature against the OIDC server's public keys.

#### 2. Native Vanilla JS Decoding (No Library Required)
A JWT payload is simply a Base64Url-encoded JSON string (the middle segment of the token). You can decode it in 1 line of vanilla JS:
```javascript
function decodeJwt(token) {
    const base64Url = token.split('.')[1];
    const base64 = base64Url.replace(/-/g, '+').replace(/_/g, '/');
    return JSON.parse(window.atob(base64));
}
```

#### 3. Client-Side vs. Server-Side Handling
*   **Client-Side (Self-Contained SPAs):**
    Decoded directly in the browser's JavaScript. The client app trusts the payload because the token was fetched directly from the secure `/connect/token` SSL endpoint.
*   **Server-Side (Backend for Frontend - BFF Pattern):**
    In highly secure environments, developers avoid exposing any tokens to browser memory (to prevent cross-site scripting/XSS thefts). Instead, a server-side backend acts as the OIDC client. It intercepts the `id_token`, validates it server-side, sets a secure `HttpOnly; Secure; SameSite=Strict` session cookie in the browser, and manages the user session on the server. The frontend never sees the raw JWT.

---

### Q23: Why does `Program.cs` set multiple claim names for the external token (`external_token`, `jwt_token`, `jwt_key`) and session key (`session_key`, `sessionId`)?

**For backwards compatibility and multi-client payload interoperability.**

Different downstream clients and legacy SDKs (Flutter mobile app, Python test scripts, web portals, MCP tools) parse claims using different naming conventions:

*   **Token Aliases (`authResult.Token`):**
    *   `external_token`: Used by current OIDC documentation and handlers to explicitly distinguish the external auth token from the OIDC token.
    *   `jwt_token`: Standard claim name expected by generic API clients.
    *   `jwt_key`: Legacy claim name expected by older mobile client builds.
*   **Session Key Aliases (`authResult.SessionKey`):**
    *   `session_key`: Used by C# and Python backend services (`snake_case`).
    *   `sessionId`: Used by JavaScript / web frontends (`camelCase`).

Populating all alias claims when constructing the `ClaimsIdentity` ensures that any client decoding the issued access or ID token finds its expected key without requiring synchronous updates across all client applications.

