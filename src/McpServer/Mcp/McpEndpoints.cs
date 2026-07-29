using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using McpServer.Data;

namespace McpServer.Mcp
{
    public static class McpEndpoints
    {
        // Thread-safe session mapping for SSE channels
        private static readonly ConcurrentDictionary<string, Channel<string>> SseSessions = new();

        public static IApplicationBuilder UseMcpTokenMiddleware(this IApplicationBuilder app)
        {
            return app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/mcp"))
                {
                    Console.WriteLine($"[MCP Auth] Intercepted request path: {context.Request.Path} ({context.Request.Method})");

                    // 1. Extract query-based Bearer token for SSE channels
                    if (context.Request.Query.TryGetValue("access_token", out var token))
                    {
                        Console.WriteLine("[MCP Auth] Found access_token in query string. Mapping to Authorization header.");
                        context.Request.Headers.Authorization = $"Bearer {token}";
                    }

                    // 2. Check for developer key in DB (X-Api-Key header or api_key query param)
                    var apiKey = context.Request.Headers["X-Api-Key"].ToString();
                    if (string.IsNullOrEmpty(apiKey))
                    {
                        apiKey = context.Request.Query["api_key"].ToString();
                    }

                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        var displayKey = apiKey.Length > 12 ? apiKey.Substring(0, 12) + "..." : apiKey;
                        Console.WriteLine($"[MCP Auth] Received X-Api-Key: {displayKey}");

                        try
                        {
                            var dbContext = context.RequestServices.GetRequiredService<ApplicationDbContext>();
                            var devKey = dbContext.DeveloperKeys
                                .FirstOrDefault(k => k.Key == apiKey && k.ExpiresAt > DateTime.UtcNow);

                            if (devKey != null)
                            {
                                Console.WriteLine($"[MCP Auth] API Key verified successfully for user '{devKey.Username}'.");

                                // Synthesize authenticated claims principal using the specific scheme expected by the endpoint authorization
                                var identity = new System.Security.Claims.ClaimsIdentity(
                                    new[]
                                    {
                                        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, devKey.Username),
                                        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, devKey.Username)
                                    },
                                    OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                                context.User = new System.Security.Claims.ClaimsPrincipal(identity);

                                // Dynamically append AllowAnonymousAttribute to the endpoint metadata
                                // to bypass OpenIddict's scheme-specific Authenticate check.
                                var endpoint = context.GetEndpoint();
                                if (endpoint != null)
                                {
                                    var metadata = new EndpointMetadataCollection(
                                        endpoint.Metadata.Append(new AllowAnonymousAttribute())
                                    );
                                    context.SetEndpoint(new Endpoint(
                                        endpoint.RequestDelegate,
                                        metadata,
                                        endpoint.DisplayName
                                    ));
                                    Console.WriteLine($"[MCP Auth] OIDC authorization policy bypassed on endpoint: {endpoint.DisplayName}");
                                }
                                else
                                {
                                    Console.WriteLine("[MCP Auth] Warning: Endpoint was not resolved prior to middleware execution.");
                                }
                            }
                            else
                            {
                                Console.WriteLine("[MCP Auth] API Key provided but was not found or has expired in the database.");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[MCP Auth] ERROR querying DeveloperKeys database table: {ex.Message}");
                        }
                    }
                    else
                    {
                        Console.WriteLine("[MCP Auth] No API Key provided in headers or query string.");
                    }
                }
                await next();
            });
        }

        /// <summary>
        /// Map all MCP connection and transport endpoints (Legacy SSE & Unified Streamable HTTP).
        /// </summary>
        public static IEndpointRouteBuilder MapMcpEndpoints(this IEndpointRouteBuilder app)
        {
            var mcpAuthScheme = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
            var authAttribute = new AuthorizeAttribute { AuthenticationSchemes = mcpAuthScheme };



            // 3. Modern Streamable HTTP GET /mcp (Establishes SSE stream connection)
            app.MapGet("/mcp", async (HttpContext context) =>
            {
                context.Response.Headers.ContentType = "text/event-stream";
                context.Response.Headers.CacheControl = "no-cache";
                context.Response.Headers.Connection = "keep-alive";
                context.Response.Headers["X-Accel-Buffering"] = "no";

                var sessionId = Guid.NewGuid().ToString("N");
                var channel = Channel.CreateUnbounded<string>();
                SseSessions[sessionId] = channel;

                var scheme = context.Request.Headers["X-Forwarded-Proto"].ToString();
                if (string.IsNullOrEmpty(scheme))
                {
                    scheme = context.Request.Scheme;
                }
                var baseUri = $"{scheme}://{context.Request.Host}";
                var endpointUrl = $"{baseUri}/mcp?sessionId={sessionId}";
                
                await context.Response.WriteAsync($"event: endpoint\ndata: {endpointUrl}\n\n");
                await context.Response.Body.FlushAsync();

                try
                {
                    while (!context.RequestAborted.IsCancellationRequested)
                    {
                        var readTask = channel.Reader.WaitToReadAsync(context.RequestAborted).AsTask();
                        var delayTask = Task.Delay(TimeSpan.FromSeconds(15), context.RequestAborted);

                        var completedTask = await Task.WhenAny(readTask, delayTask);
                        if (completedTask == readTask && await readTask)
                        {
                            while (channel.Reader.TryRead(out var message))
                            {
                                await context.Response.WriteAsync(message);
                                await context.Response.Body.FlushAsync();
                            }
                        }
                        else
                        {
                            // Send a keep-alive comment (":\n\n") to prevent proxy connection timeouts
                            await context.Response.WriteAsync(":\n\n");
                            await context.Response.Body.FlushAsync();
                        }
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    SseSessions.TryRemove(sessionId, out _);
                }
            })
            .RequireAuthorization(authAttribute);

            // 4. Modern Streamable HTTP POST /mcp (Processes JSON-RPC messages)
            app.MapPost("/mcp", async (HttpContext context) =>
            {
                var sessionId = context.Request.Query["sessionId"].ToString();
                
                using var reader = new StreamReader(context.Request.Body);
                var bodyText = await reader.ReadToEndAsync();
                
                var rpcRequest = DeserializeRpc(bodyText);
                if (rpcRequest == null || string.IsNullOrEmpty(rpcRequest.Method))
                {
                    return Results.BadRequest("Missing method or invalid format.");
                }

                var resultPayload = ProcessRpcRequest(rpcRequest, context);

                if (!string.IsNullOrEmpty(sessionId) && SseSessions.TryGetValue(sessionId, out var channel))
                {
                    if (rpcRequest.Id != null)
                    {
                        var jsonResponse = JsonSerializer.Serialize(new
                        {
                            jsonrpc = "2.0",
                            id = rpcRequest.Id,
                            result = resultPayload
                        });

                        var sseMessage = $"event: message\ndata: {jsonResponse}\n\n";
                        await channel.Writer.WriteAsync(sseMessage);
                    }
                    return Results.Accepted();
                }
                else
                {
                    var responseJson = JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id = rpcRequest.Id,
                        result = resultPayload
                    });
                    return Results.Content(responseJson, "application/json");
                }
            })
            .RequireAuthorization(authAttribute);

            return app;
        }

        private static JsonRpcRequest? DeserializeRpc(string bodyText)
        {
            try
            {
                return JsonSerializer.Deserialize<JsonRpcRequest>(bodyText, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch
            {
                return null;
            }
        }

        private static object ProcessRpcRequest(JsonRpcRequest rpcRequest, HttpContext context)
        {
            if (rpcRequest.Method == "initialize")
            {
                return new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new
                    {
                        tools = new { listChanged = false }
                    },
                    serverInfo = new
                    {
                        name = "PGW-MCP-Auth-Server",
                        version = "1.0.0"
                    }
                };
            }
            if (rpcRequest.Method == "tools/list")
            {
                return new
                {
                    tools = new[]
                    {
                        new
                        {
                            name = "get_customer_info",
                            description = "Returns PGW Auto Glass customer information and account details for a specified customer code.",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    customerId = new
                                    {
                                        type = "string",
                                        description = "The customer code (e.g., CUS9999)"
                                    }
                                },
                                required = new[] { "customerId" }
                            }
                        }
                    }
                };
            }
            if (rpcRequest.Method == "tools/call")
            {
                var parameters = rpcRequest.Params;
                string? customerId = null;
                if (parameters.HasValue && parameters.Value.TryGetProperty("arguments", out var arguments))
                {
                    if (arguments.TryGetProperty("customerId", out var idProp))
                    {
                        customerId = idProp.GetString();
                    }
                }

                if (customerId == "CUS9999")
                {
                    return new
                    {
                        content = new[]
                        {
                            new
                            {
                                type = "text",
                                text = $"Customer CUS9999 details:\n- Name: PGW Autoglass Test Account\n- Location: Pittsburgh, PA\n- Status: Active\n- Outstanding Balance: $0.00\n- Auth User Context: {context.User.Identity?.Name}\n- Notes: Hardcoded test account for OIDC verification."
                            }
                        },
                        isError = false
                    };
                }
                return new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = $"Customer {customerId} not found or access denied."
                        }
                    },
                    isError = true
                };
            }

            return new
            {
                error = new
                {
                    code = -32601,
                    message = $"Method '{rpcRequest.Method}' not found."
                }
            };
        }
    }

    public class JsonRpcRequest
    {
        public string Jsonrpc { get; set; } = "2.0";
        public string Method { get; set; } = "";
        public object? Id { get; set; }
        public JsonElement? Params { get; set; }
    }
}
