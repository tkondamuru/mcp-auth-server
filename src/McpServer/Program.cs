using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Http;
using McpServer.Data;
using McpServer.Services;
using McpServer.Mcp;
using static OpenIddict.Abstractions.OpenIddictConstants;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5000");

// Helper to locate files robustly (certificates, wwwroot/login.html, etc.)
string FindFilePath(string filename)
{
    // Check direct certificate path env var
    var envCertPath = Environment.GetEnvironmentVariable("CERTIFICATE_PATH");
    if (!string.IsNullOrEmpty(envCertPath) && filename.Contains("pgwintraapps"))
    {
        if (Directory.Exists(envCertPath))
        {
            var targetPath = Path.Combine(envCertPath, filename);
            if (File.Exists(targetPath)) return targetPath;
        }
        else if (File.Exists(envCertPath) && envCertPath.EndsWith(Path.GetExtension(filename), StringComparison.OrdinalIgnoreCase))
        {
            return envCertPath;
        }
    }

    // Check database path directory (since it is mounted on /data)
    var envDbPath = Environment.GetEnvironmentVariable("DATABASE_PATH");
    if (!string.IsNullOrEmpty(envDbPath))
    {
        var dbDir = Path.GetDirectoryName(envDbPath);
        if (!string.IsNullOrEmpty(dbDir))
        {
            var targetPath = Path.Combine(dbDir, filename);
            if (File.Exists(targetPath)) return targetPath;
        }
    }

    var path1 = Path.Combine(builder.Environment.ContentRootPath, filename);
    if (File.Exists(path1)) return path1;
    
    var path2 = Path.Combine(Directory.GetCurrentDirectory(), filename);
    if (File.Exists(path2)) return path2;

    var path3 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filename);
    if (File.Exists(path3)) return path3;

    var pathSub1 = Path.Combine(builder.Environment.ContentRootPath, "src", "McpServer", filename);
    if (File.Exists(pathSub1)) return pathSub1;

    var pathSub2 = Path.Combine(Directory.GetCurrentDirectory(), "src", "McpServer", filename);
    if (File.Exists(pathSub2)) return pathSub2;

    // Search up parent directories of ContentRootPath
    var dir = new DirectoryInfo(builder.Environment.ContentRootPath);
    while (dir != null)
    {
        var parentPath = Path.Combine(dir.FullName, filename);
        if (File.Exists(parentPath)) return parentPath;

        var parentSubPath = Path.Combine(dir.FullName, "src", "McpServer", filename);
        if (File.Exists(parentSubPath)) return parentSubPath;

        dir = dir.Parent;
    }

    return path1;
}

// 1. Configure EF Core SQLite Database for OpenIddict State (clients, tokens, scopes)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=mcp.db";

// If running in cloud containers (e.g. Render, ACA) or Azure App Service, ensure SQLite database path is configurable
var envDbPath = Environment.GetEnvironmentVariable("DATABASE_PATH");
if (!string.IsNullOrEmpty(envDbPath))
{
    connectionString = $"Data Source={envDbPath}";
}
else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID")))
{
    var homeDir = Environment.GetEnvironmentVariable("HOME") ?? "/home";
    connectionString = $"Data Source={Path.Combine(homeDir, "mcp.db")}";
}

builder.Services.AddDbContext<ApplicationDbContext>(options =>
{
    options.UseSqlite(connectionString);
    options.UseOpenIddict();
});

// 2. Register OpenIddict Server & Local Validation services
builder.Services.AddOpenIddict()
    .AddCore(options =>
    {
        options.UseEntityFrameworkCore()
               .UseDbContext<ApplicationDbContext>();
    })
    .AddServer(options =>
    {
        // Define standard OIDC endpoints & set access token lifespan to 1 hour
        options.SetAuthorizationEndpointUris("/connect/authorize")
               .SetTokenEndpointUris("/connect/token")
               .SetUserInfoEndpointUris("/connect/userinfo")
               .SetAccessTokenLifetime(TimeSpan.FromHours(1))
               .DisableAccessTokenEncryption();

        // Allow flows
        options.AllowAuthorizationCodeFlow()
               .RequireProofKeyForCodeExchange(); // PKCE is enforced for public clients
        options.AllowPasswordFlow();
        options.AllowRefreshTokenFlow();

        // Register custom scopes
        options.RegisterScopes(Scopes.OpenId, Scopes.Profile, Scopes.Email, "mcp");

        // Configure custom signing and encryption certificates
        var certPath = FindFilePath("pgwintraapps.pfx");
        var certPassword = builder.Configuration["Certificates:PfxPassword"] ?? "";
        var cerPath = FindFilePath("pgwintraapps.cer");
        var keyPath = FindFilePath("pgwintraapps.key");
        var certificateLoaded = false;
        
        if (File.Exists(certPath) && !string.IsNullOrEmpty(certPassword))
        {
            try
            {
                var certificate = X509CertificateLoader.LoadPkcs12FromFile(certPath, certPassword, 
                    X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.EphemeralKeySet);
                
                options.AddSigningCertificate(certificate)
                       .AddEncryptionCertificate(certificate);
                certificateLoaded = true;
                Console.WriteLine("[INFO] Successfully loaded and configured custom PFX certificate.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] Failed to load PFX certificate: {ex.Message}");
            }
        }

        if (!certificateLoaded && File.Exists(cerPath) && File.Exists(keyPath))
        {
            try
            {
                // Load the public certificate (supports PEM or binary DER certificates)
                var publicCert = X509CertificateLoader.LoadCertificateFromFile(cerPath);

                // Load the private key from the PEM key file
                var rsa = System.Security.Cryptography.RSA.Create();
                var pemContent = File.ReadAllText(keyPath);
                rsa.ImportFromPem(pemContent);

                // Pair the public certificate with its private key
                var certificate = publicCert.CopyWithPrivateKey(rsa);

                options.AddSigningCertificate(certificate)
                       .AddEncryptionCertificate(certificate);
                certificateLoaded = true;
                Console.WriteLine("[INFO] Successfully loaded and configured custom CER+KEY certificate.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] Failed to load CER+KEY certificate: {ex.Message}");
            }
        }

        if (!certificateLoaded)
        {
            throw new InvalidOperationException("Failed to load a valid OIDC signing and encryption certificate. Development certificates fallback is disabled.");
        }

        // Enable ASP.NET Core passthrough for route handlers
        options.UseAspNetCore()
               .DisableTransportSecurityRequirement()
               .EnableAuthorizationEndpointPassthrough()
               .EnableTokenEndpointPassthrough()
               .EnableUserInfoEndpointPassthrough();
    })
    .AddValidation(options =>
    {
        // Import settings from local OpenIddict server instance for local JWT verification
        options.UseLocalServer();
        options.UseAspNetCore();
    });

// 3. Register standard Cookie Authentication (for login/consent UI)
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.LoginPath = "/login";
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
});

builder.Services.AddAuthorization();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// 4. Register Custom Services
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IUserAuthenticationService, UserAuthenticationService>();
builder.Services.AddHostedService<DbSeeder>();

var app = builder.Build();

app.UseRouting();
app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

// --- 6. Endpoints Mappings ---

// Serve the premium login UI (forcing no-cache headers to bypass browser caching of static login.html)
app.MapGet("/login", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
    context.Response.Headers.Pragma = "no-cache";
    
    var path = FindFilePath(Path.Combine("wwwroot", "login.html"));
    return Results.File(path, "text/html");
});


// Process login post
app.MapPost("/login", async (HttpContext context, IUserAuthenticationService authService) =>
{
    var username = context.Request.Form["username"].ToString();
    var password = context.Request.Form["password"].ToString();
    var returnUrl = context.Request.Query["ReturnUrl"].ToString();

    var authResult = await authService.ValidateCredentialsAsync(username, password);
    if (!authResult.Success)
    {
        var redirectUrl = $"/login?error=invalid_credentials&ReturnUrl={HttpUtility.UrlEncode(returnUrl)}";
        return Results.Redirect(redirectUrl);
    }

    // Set authentication cookie carrying external authentication claims
    var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
    identity.AddClaim(new System.Security.Claims.Claim(ClaimTypes.NameIdentifier, username));
    identity.AddClaim(new System.Security.Claims.Claim(ClaimTypes.Name, username));
    
    if (!string.IsNullOrEmpty(authResult.Token))
    {
        identity.AddClaim(new System.Security.Claims.Claim("external_token", authResult.Token));
        identity.AddClaim(new System.Security.Claims.Claim("jwt_token", authResult.Token));
        identity.AddClaim(new System.Security.Claims.Claim("jwt_key", authResult.Token));
    }
    if (!string.IsNullOrEmpty(authResult.SessionKey))
    {
        identity.AddClaim(new System.Security.Claims.Claim("session_key", authResult.SessionKey));
        identity.AddClaim(new System.Security.Claims.Claim("sessionId", authResult.SessionKey));
    }
    
    var principal = new ClaimsPrincipal(identity);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

    if (string.IsNullOrEmpty(returnUrl))
    {
        returnUrl = "/";
    }

    return Results.Redirect(returnUrl);
});

// Clear local authentication cookie and session
app.MapGet("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    var returnUrl = context.Request.Query["ReturnUrl"].ToString();
    if (string.IsNullOrEmpty(returnUrl))
    {
        returnUrl = "/login";
    }
    return Results.Redirect(returnUrl);
});

// OIDC /connect/authorize handler
app.MapMethods("/connect/authorize", new[] { "GET", "POST" }, async (HttpContext context) =>
{
    var request = context.GetOpenIddictServerRequest() ??
        throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

    var result = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);

    // If user is not logged in via cookie, redirect to /login
    if (!result.Succeeded || result.Principal == null)
    {
        return Results.Challenge(
            properties: new AuthenticationProperties
            {
                RedirectUri = context.Request.Path + context.Request.QueryString
            },
            authenticationSchemes: new[] { CookieAuthenticationDefaults.AuthenticationScheme });
    }

    // User is logged in, create OIDC Principal
    var username = result.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? result.Principal.Identity?.Name ?? "user";
    
    var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    identity.AddClaim(new System.Security.Claims.Claim(OpenIddictConstants.Claims.Subject, username));
    identity.AddClaim(new System.Security.Claims.Claim(OpenIddictConstants.Claims.Name, username)
        .SetDestinations(OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken));

    // Copy external authentication claims from cookie principal into the OIDC token context
    var extToken = result.Principal.FindFirst("external_token")?.Value ?? result.Principal.FindFirst("jwt_token")?.Value;
    if (!string.IsNullOrEmpty(extToken))
    {
        identity.AddClaim(new System.Security.Claims.Claim("external_token", extToken)
            .SetDestinations(OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken));
        identity.AddClaim(new System.Security.Claims.Claim("jwt_token", extToken)
            .SetDestinations(OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken));
        identity.AddClaim(new System.Security.Claims.Claim("jwt_key", extToken)
            .SetDestinations(OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken));
    }

    var sessKey = result.Principal.FindFirst("session_key")?.Value ?? result.Principal.FindFirst("sessionId")?.Value;
    if (!string.IsNullOrEmpty(sessKey))
    {
        identity.AddClaim(new System.Security.Claims.Claim("session_key", sessKey)
            .SetDestinations(OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken));
        identity.AddClaim(new System.Security.Claims.Claim("sessionId", sessKey)
            .SetDestinations(OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken));
    }

    // Configure dynamic Refresh Token expiration matching the external token
    var extExpiration = GetJwtExpiration(extToken);
    if (extExpiration.HasValue)
    {
        var remainingTime = extExpiration.Value - DateTime.UtcNow;
        if (remainingTime <= TimeSpan.Zero)
        {
            remainingTime = TimeSpan.FromMinutes(1);
        }
        identity.SetRefreshTokenLifetime(remainingTime);
    }

    // Dynamic claim assignment based on requested scopes
    if (request.HasScope(OpenIddictConstants.Scopes.Email))
    {
        var userEmail = username == "CUS9999" ? "tkondamuru@pgwglass.com" : $"{username}@pgwglass.com";
        identity.AddClaim(new System.Security.Claims.Claim(OpenIddictConstants.Claims.Email, userEmail)
            .SetDestinations(OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken));
    }

    if (request.HasScope(OpenIddictConstants.Scopes.Profile))
    {
        var givenName = username == "CUS9999" ? "TJ" : username;
        identity.AddClaim(new System.Security.Claims.Claim(OpenIddictConstants.Claims.GivenName, givenName)
            .SetDestinations(OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken));
    }

    var principal = new ClaimsPrincipal(identity);
    principal.SetScopes(request.GetScopes());

    // Complete the OIDC challenge
    return Results.SignIn(principal, properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
});

// OIDC /connect/token handler
app.MapPost("/connect/token", async (HttpContext context, IUserAuthenticationService authService) =>
{
    var request = context.GetOpenIddictServerRequest() ??
        throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

    if (request.IsPasswordGrantType())
    {
        var authResult = await authService.ValidateCredentialsAsync(request.Username ?? "", request.Password ?? "");
        if (!authResult.Success)
        {
            var properties = new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = authResult.ErrorMessage ?? "The username/password combination is invalid."
            });
            return Results.Challenge(properties, new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
        }

        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        identity.AddClaim(new System.Security.Claims.Claim(OpenIddictConstants.Claims.Subject, request.Username!));
        identity.AddClaim(new System.Security.Claims.Claim(OpenIddictConstants.Claims.Name, request.Username!)
            .SetDestinations(OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken));

        // Inject external auth claims into the issued tokens
        if (!string.IsNullOrEmpty(authResult.Token))
        {
            identity.AddClaim(new System.Security.Claims.Claim("external_token", authResult.Token)
                .SetDestinations(OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken));
            identity.AddClaim(new System.Security.Claims.Claim("jwt_token", authResult.Token)
                .SetDestinations(OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken));
            identity.AddClaim(new System.Security.Claims.Claim("jwt_key", authResult.Token)
                .SetDestinations(OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken));
        }

        if (!string.IsNullOrEmpty(authResult.SessionKey))
        {
            identity.AddClaim(new System.Security.Claims.Claim("session_key", authResult.SessionKey)
                .SetDestinations(OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken));
            identity.AddClaim(new System.Security.Claims.Claim("sessionId", authResult.SessionKey)
                .SetDestinations(OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken));
        }

        // Dynamic claim assignment based on requested scopes
        if (request.HasScope(OpenIddictConstants.Scopes.Email))
        {
            var userEmail = request.Username == "CUS9999" ? "tkondamuru@pgwglass.com" : $"{request.Username}@pgwglass.com";
            identity.AddClaim(new System.Security.Claims.Claim(OpenIddictConstants.Claims.Email, userEmail)
                .SetDestinations(OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken));
        }

        if (request.HasScope(OpenIddictConstants.Scopes.Profile))
        {
            var givenName = request.Username == "CUS9999" ? "TJ" : request.Username!;
            identity.AddClaim(new System.Security.Claims.Claim(OpenIddictConstants.Claims.GivenName, givenName)
                .SetDestinations(OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken));
        }

        if (authResult.TokenExpiration.HasValue)
        {
            var remainingTime = authResult.TokenExpiration.Value - DateTime.UtcNow;
            if (remainingTime <= TimeSpan.Zero)
            {
                remainingTime = TimeSpan.FromMinutes(1); // Minimal fallback if already expired
            }
            
            // Set Refresh Token lifetime directly on the identity to match the external token's expiration
            identity.SetRefreshTokenLifetime(remainingTime);
        }

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(request.GetScopes());

        return Results.SignIn(principal, properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
    else if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
    {
        var result = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (result.Principal == null)
        {
            var properties = new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The token is no longer valid."
            });
            return Results.Challenge(properties, new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
        }

        var principal = result.Principal;
        return Results.SignIn(principal, properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    throw new InvalidOperationException("The specified grant type is not supported.");
});

// OIDC /connect/userinfo handler
app.MapMethods("/connect/userinfo", new[] { "GET", "POST" }, async (HttpContext context) =>
{
    var user = context.User;
    var subject = user.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;
    if (string.IsNullOrEmpty(subject))
    {
        return Results.Challenge(
            properties: null,
            authenticationSchemes: new[] { OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme });
    }

    return Results.Ok(new
    {
        sub = subject,
        name = subject,
        preferred_username = subject
    });
})
.RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme });

// --- Map MCP Routing & Endpoints ---
app.MapMcpEndpoints();

// --- Administrative API Endpoints ---
app.MapPost("/admin/api/login-pin", async (AdminPinRequest request, HttpContext context) =>
{
    var expectedPin = Environment.GetEnvironmentVariable("ADMIN_PIN") ?? "052512";
    if (string.Equals(request.Pin?.Trim(), expectedPin, StringComparison.Ordinal))
    {
        var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
        identity.AddClaim(new System.Security.Claims.Claim(ClaimTypes.NameIdentifier, "admin"));
        identity.AddClaim(new System.Security.Claims.Claim(ClaimTypes.Name, "McpAdmin"));
        identity.AddClaim(new System.Security.Claims.Claim(ClaimTypes.Role, "admin"));

        var principal = new ClaimsPrincipal(identity);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

        return Results.Ok(new { success = true, message = "Admin authentication successful." });
    }

    return Results.Json(new { success = false, message = "Invalid Admin PIN." }, statusCode: 401);
});

app.MapGet("/admin/api/clients", async (OpenIddict.Abstractions.IOpenIddictApplicationManager manager) =>
{
    var clients = new List<object>();
    await foreach (var app in manager.ListAsync())
    {
        var descriptor = new OpenIddictApplicationDescriptor();
        await manager.PopulateAsync(descriptor, app);
        clients.Add(new
        {
            clientId = descriptor.ClientId,
            displayName = descriptor.DisplayName,
            redirectUris = descriptor.RedirectUris.Select(u => u.ToString()).ToList()
        });
    }
    return Results.Json(clients);
})
.RequireAuthorization(new AuthorizeAttribute { Roles = "admin", AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme });

app.MapPost("/admin/api/clients/create", async (CreateClientRequest request, OpenIddict.Abstractions.IOpenIddictApplicationManager manager) =>
{
    if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.DisplayName))
    {
        return Results.BadRequest("Client ID and Display Name are required.");
    }

    var existing = await manager.FindByClientIdAsync(request.ClientId);
    if (existing != null)
    {
        return Results.BadRequest($"Client with ID '{request.ClientId}' already exists.");
    }

    var descriptor = new OpenIddictApplicationDescriptor
    {
        ClientId = request.ClientId,
        DisplayName = request.DisplayName,
        ClientType = OpenIddictConstants.ClientTypes.Public,
        Permissions =
        {
            OpenIddictConstants.Permissions.Endpoints.Authorization,
            OpenIddictConstants.Permissions.Endpoints.Token,
            OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
            OpenIddictConstants.Permissions.GrantTypes.Password,
            OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
            OpenIddictConstants.Permissions.ResponseTypes.Code,
            OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OpenId,
            OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.Profile,
            OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.Email,
            OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OfflineAccess,
            OpenIddictConstants.Permissions.Prefixes.Scope + "mcp"
        }
    };

    foreach (var uri in request.RedirectUris)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri))
        {
            descriptor.RedirectUris.Add(parsedUri);
        }
    }

    await manager.CreateAsync(descriptor);
    return Results.Ok();
})
.RequireAuthorization(new AuthorizeAttribute { Roles = "admin", AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme });

app.MapPost("/admin/api/clients/update", async (UpdateClientRequest request, OpenIddict.Abstractions.IOpenIddictApplicationManager manager) =>
{
    if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.DisplayName))
    {
        return Results.BadRequest("Client ID and Display Name are required.");
    }

    var app = await manager.FindByClientIdAsync(request.ClientId);
    if (app == null)
    {
        return Results.NotFound();
    }

    var descriptor = new OpenIddictApplicationDescriptor();
    await manager.PopulateAsync(descriptor, app);

    descriptor.DisplayName = request.DisplayName;
    descriptor.RedirectUris.Clear();
    foreach (var uri in request.RedirectUris)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri))
        {
            descriptor.RedirectUris.Add(parsedUri);
        }
    }

    await manager.UpdateAsync(app, descriptor);
    return Results.Ok();
})
.RequireAuthorization(new AuthorizeAttribute { Roles = "admin", AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme });

app.MapPost("/admin/api/clients/delete", async (DeleteClientRequest request, OpenIddict.Abstractions.IOpenIddictApplicationManager manager) =>
{
    if (string.IsNullOrWhiteSpace(request.ClientId))
    {
        return Results.BadRequest("Client ID is required.");
    }

    var app = await manager.FindByClientIdAsync(request.ClientId);
    if (app == null)
    {
        return Results.NotFound();
    }

    await manager.DeleteAsync(app);
    return Results.Ok();
})
.RequireAuthorization(new AuthorizeAttribute { Roles = "admin", AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme });



// Map static admin page directly (unauthenticated users will be prompted for PIN by the page script)
app.MapGet("/admin.html", () =>
{
    var path = FindFilePath(Path.Combine("wwwroot", "admin.html"));
    return Results.File(path, "text/html");
});

// Default root route serves the admin page
app.MapGet("/", () =>
{
    var path = FindFilePath(Path.Combine("wwwroot", "admin.html"));
    return Results.File(path, "text/html");
});

// Helper to decode JWT expiration from payload without signature verification
static DateTime? GetJwtExpiration(string? jwtToken)
{
    if (string.IsNullOrEmpty(jwtToken)) return null;

    try
    {
        var parts = jwtToken.Split('.');
        if (parts.Length < 2) return null;

        var payloadBase64 = parts[1];
        payloadBase64 = payloadBase64.Replace('-', '+').Replace('_', '/');
        switch (payloadBase64.Length % 4)
        {
            case 2: payloadBase64 += "=="; break;
            case 3: payloadBase64 += "="; break;
        }

        var payloadBytes = Convert.FromBase64String(payloadBase64);
        var payloadJson = System.Text.Encoding.UTF8.GetString(payloadBytes);
        
        using var doc = JsonDocument.Parse(payloadJson);
        if (doc.RootElement.TryGetProperty("exp", out var expProp))
        {
            var expSeconds = expProp.GetInt64();
            return DateTimeOffset.FromUnixTimeSeconds(expSeconds).UtcDateTime;
        }
    }
    catch
    {
        // Ignore parsing errors
    }
    return null;
}

app.Run();

public record AdminPinRequest(string Pin);
public record CreateClientRequest(string ClientId, string DisplayName, List<string> RedirectUris);
public record UpdateClientRequest(string ClientId, string DisplayName, List<string> RedirectUris);
public record DeleteClientRequest(string ClientId);

