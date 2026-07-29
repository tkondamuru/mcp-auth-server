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
                // Echo the client's protocol version to negotiate the connection protocol
                string protocolVersion = "2024-11-05";
                if (rpcRequest.Params.HasValue && rpcRequest.Params.Value.TryGetProperty("protocolVersion", out var versionProp))
                {
                    var clientVersion = versionProp.GetString();
                    if (!string.IsNullOrEmpty(clientVersion))
                    {
                        protocolVersion = clientVersion;
                        Console.WriteLine($"[MCP Auth] Negotiated protocol version to client's preferred: '{protocolVersion}'");
                    }
                }

                return new
                {
                    protocolVersion = protocolVersion,
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
