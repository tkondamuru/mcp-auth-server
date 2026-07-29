using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpConsoleServer
{
    class Program
    {
        static void Main(string[] args)
        {
            // Force Console to use UTF-8 encoding
            Console.InputEncoding = System.Text.Encoding.UTF8;
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            while (Console.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                try
                {
                    var request = JsonSerializer.Deserialize<JsonRpcRequest>(line);
                    if (request == null) continue;

                    ProcessRequest(request);
                }
                catch (Exception ex)
                {
                    SendError(null, -32700, $"Parse error: {ex.Message}");
                }
            }
        }

        static void ProcessRequest(JsonRpcRequest request)
        {
            if (request.Method == "initialize")
            {
                var protocolVersion = "2024-11-05"; // Default fallback
                if (request.Params != null && request.Params.Value.TryGetProperty("protocolVersion", out var pv))
                {
                    protocolVersion = pv.GetString() ?? protocolVersion;
                }

                var response = new
                {
                    protocolVersion = protocolVersion,
                    capabilities = new
                    {
                        tools = new { listChanged = false }
                    },
                    serverInfo = new
                    {
                        name = "PGW-Local-Console-POC",
                        version = "1.0.0"
                    }
                };

                SendResult(request.Id, response);
            }
            else if (request.Method == "notifications/initialized")
            {
                // Standard MCP notifications do not return a result
            }
            else if (request.Method == "tools/list")
            {
                var response = new
                {
                    tools = new[]
                    {
                        new
                        {
                            name = "local_greet",
                            description = "A locally executed C# tool that greets a user by name.",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    name = new { type = "string", description = "The name of the person to greet" }
                                },
                                required = new[] { "name" }
                            }
                        }
                    }
                };

                SendResult(request.Id, response);
            }
            else if (request.Method == "tools/call")
            {
                if (request.Params == null)
                {
                    SendError(request.Id, -32602, "Invalid params. Params block is missing.");
                    return;
                }

                try
                {
                    var toolName = request.Params.Value.GetProperty("name").GetString();
                    if (toolName != "local_greet")
                    {
                        SendError(request.Id, -32601, $"Tool not found: {toolName}");
                        return;
                    }

                    var arguments = request.Params.Value.GetProperty("arguments");
                    var userName = arguments.GetProperty("name").GetString() ?? "Guest";

                    var result = new
                    {
                        content = new[]
                        {
                            new { type = "text", text = $"Hello {userName}! This greeting was generated dynamically by a C# compiled executable running locally on Windows over STDIO (Standard Input/Output) in user-mode." }
                        }
                    };

                    SendResult(request.Id, result);
                }
                catch (Exception ex)
                {
                    SendError(request.Id, -32602, $"Invalid call arguments: {ex.Message}");
                }
            }
            else
            {
                SendError(request.Id, -32601, $"Method not found: {request.Method}");
            }
        }

        static void SendResult(object? id, object result)
        {
            var response = new
            {
                jsonrpc = "2.0",
                id = id,
                result = result
            };
            // Output MUST be on a single line so the client reads it as a complete JSON packet
            Console.WriteLine(JsonSerializer.Serialize(response));
            Console.Out.Flush();
        }

        static void SendError(object? id, int code, string message)
        {
            var response = new
            {
                jsonrpc = "2.0",
                id = id,
                error = new { code = code, message = message }
            };
            Console.WriteLine(JsonSerializer.Serialize(response));
            Console.Out.Flush();
        }
    }

    public class JsonRpcRequest
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("id")]
        public object? Id { get; set; }

        [JsonPropertyName("method")]
        public string Method { get; set; } = "";

        [JsonPropertyName("params")]
        public JsonElement? Params { get; set; }
    }
}
