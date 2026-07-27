# MCP Setup & Transport Q&A

This Q&A document captures architecture decisions, configuration patterns, and explanations regarding the Model Context Protocol (MCP) integration.

---

### Q1: How does `McpEndpoints.cs` implement MCP transport channels?

`McpEndpoints.cs` maps endpoints that implement the Model Context Protocol over HTTP, supporting both **Stateful SSE (Server-Sent Events)** and **Stateless HTTP Request-Response** transports:

#### 1. Token Middleware (`UseMcpTokenMiddleware`)
Standard OIDC validation expects the access token to be passed in the `Authorization: Bearer <token>` HTTP header. However, standard browser APIs initiating Server-Sent Events (`new EventSource(url)`) cannot natively customize headers. 
To resolve this, the client passes the token in the query string (`?access_token=xxx`). The middleware copies this value into the standard `Authorization` header before the validation layer processes the request.

#### 2. Session Management (`SseSessions`)
A thread-safe `ConcurrentDictionary<string, Channel<string>>` links an active SSE stream connection (GET) with subsequent command requests (POST) using a unique, random session ID.

#### 3. Stateful SSE Connection (`GET /mcp`)
*   Sets response headers to `text/event-stream`, `keep-alive`, and `no-cache`.
*   Generates a session ID and registers a thread-safe `Channel<string>`.
*   Pushes an initial `endpoint` event containing the POST endpoint URL parameterized with the session ID: `http://localhost:5000/mcp?sessionId=XYZ`.
*   Runs a continuous asynchronous loop reading from the channel and flushing events down the socket connection.

#### 4. Message Processing (`POST /mcp`)
*   If a `sessionId` is passed and matched, the server runs the command and writes the JSON-RPC response payload to the active session channel. The SSE channel streams it to the client, and the POST request returns `HTTP 202 Accepted` immediately.
*   If no `sessionId` is passed, the server operates in **Stateless Mode**, executing the request and returning the JSON-RPC body directly in the POST response as `HTTP 200 OK`.

---

### Q2: Does the initial `GET /mcp` request remain active without completing until the client decides to break it?

**Yes, precisely.**

The HTTP `GET /mcp` connection remains open and active. Unlike standard web request-response cycles that complete in milliseconds, this request turns into a long-lived connection that streams data over time.

Here is how the request lifecycle is controlled and terminated:

#### 1. Why it stays open
In [McpEndpoints.cs](file:///c:/Development/labs/mcp/src/McpServer/Mcp/McpEndpoints.cs#L60), the request thread runs a persistent loop:
```csharp
while (await channel.Reader.WaitToReadAsync(context.RequestAborted))
```
The thread blocks asynchronously on the channel reader. As long as the channel is open and the connection is active, the request handler does not return, preventing ASP.NET Core from completing the HTTP response cycle.

#### 2. Who can break/terminate the connection?
*   **The Client (Standard Case):**
    If the user closes the browser tab, navigates away, or the client application executes `.close()` on the event source listener, the client drops the HTTP socket. 
    This automatically triggers **`context.RequestAborted`** (a .NET `CancellationToken`). The loop immediately wakes up, throws an `OperationCanceledException` (which we catch safely), exits the handler, and runs the `finally` block to remove the session ID from memory.
*   **The Server:**
    The server can manually shut down the connection by completing the session channel (`channel.Writer.Complete()`). This causes `WaitToReadAsync` to return `false`, clean up, and close the stream.
*   **Network Intermediaries (Idle Timeout):**
    Cloud gateways, load balancers, and reverse proxies (like Nginx, Cloudflare, or Azure App Service ARR) often terminate connections if no bytes are sent for a certain duration (typically 60–120 seconds).
    *   *Mitigation:* To prevent this, the server or client sends periodic empty heartbeats (SSE comments like `:\n\n`) to keep the network pipeline active.

---

### Q3: Do LLM clients (like Claude Desktop or custom CLI/IDE plugins) know that `GET /mcp` is required for connection/reconnection, and do they manage closures and pings?

**Yes.** 

LLM clients that interface with remote MCP servers implement the **official MCP SSE Client specification** (established by Anthropic). Because they follow this protocol, they handle transport negotiation, reconnection, closures, and keep-alives natively.

Here is how they behave under the hood:

#### 1. How they handle the initial connection and reconnection
*   **Startup GET:** The LLM client is configured with your root SSE server URL (e.g. `http://localhost:5000/mcp`). Upon startup, the client automatically executes an HTTP `GET` request to that path.
*   **Endpoint Resolution:** It reads the very first event emitted by the stream (`event: endpoint`), extracts the POST URL (containing the `sessionId`), and uses that target URL for all subsequent JSON-RPC requests.
*   **Auto-Reconnection:** If the connection drops (due to network blips or a server reboot), standard SSE client libraries built into the LLM client automatically trigger a retry. They make a new `GET /mcp` request, parse the new `event: endpoint` payload (getting a new `sessionId`), and run the OIDC auth and MCP handshake initialization protocols again.

#### 2. How they handle closures
The client does not need to send an explicit "I am closing now" message:
*   When the LLM client shuts down or disconnects, the client process terminates the underlying TCP socket.
*   The host OS alerts Kestrel (your web server) that the socket was reset.
*   This instantly triggers the `.NET RequestAborted` token in Kestrel, which safely stops our SSE loop and deletes the user's session state.

#### 3. Heartbeats and Keep-Alives
*   **Keep-Alives:** The MCP specification suggests that servers send periodic heartbeats to prevent intermediate proxies (like IIS ARR or Nginx) from killing inactive connections. Our server can push silent comments (like `:\n\n`) down the stream every 15-30 seconds to keep the pipe warm.
*   **Protocol-Level Pings:** The JSON-RPC specification supports sending standard `ping` requests. If an LLM client wants to verify the server is still responsive, it will send a JSON-RPC ping method to `/mcp?sessionId=...` and wait for the response to arrive via the GET stream.

---

### Q4: What is the official Anthropic MCP SSE Client specification?

Here is a concise breakdown of each component of the specification:

*   **The Outstream (Server → Client):**
    A long-lived HTTP `GET` stream where the server pushes JSON-RPC responses and notifications asynchronously to the client using Server-Sent Events (SSE).
*   **The Instream (Client → Server):**
    Standard short-lived HTTP `POST` requests sent by the client containing JSON-RPC requests (like calling a tool or listing resources) targeted to the server's session endpoint.
*   **Client Request `/mcp`:**
    The initial connection request initiated by the client to open the SSE EventSource stream (e.g. `GET /mcp` or `GET /mcp?access_token=...`).
*   **Server SSE Response Header:**
    The headers returned by the server to lock in the persistent stream: `Content-Type: text/event-stream`, `Cache-Control: no-cache`, and `Connection: keep-alive`.
*   **The Endpoint Handshake Event:**
    *   *event:* `endpoint`
    *   *data:* The dynamic POST URL target parameterized with the session ID where the client must direct its instream commands (e.g. `http://localhost:5000/mcp?sessionId=920f3f38...`).
*   **Client initialize Request:**
    The client POSTs an `initialize` JSON-RPC method containing its name, protocol version, capabilities, and settings to the resolved session POST endpoint.
*   **Server initialize Response:**
    The server pushes the `initialize` result (detailing protocol version, capabilities, and server name/version) down the open GET stream to the client.
*   **Client initialized Notification:**
    A JSON-RPC notification (`initialized`) POSTed by the client to confirm it has successfully initialized and is ready to query tools and resources.
*   **Example: Invoking a Tool:**
    *   *Client POST:* Sends `{"jsonrpc":"2.0", "id": 2, "method":"tools/call", "params":{"name":"get_customer_info", "arguments":{"customerId":"CUS9999"}}}`.
    *   *Server GET stream output:* Pushes `event: message \n data: {"jsonrpc":"2.0", "id": 2, "result":{"content":[{"type":"text", "text":"Customer details..."}]}}`.
*   **Session Tracking:**
    The server generates a unique `sessionId` query key for the GET stream, maps it to a memory channel, and verifies all incoming client POST requests match an active, registered session key.
*   **Asynchronous Handling:**
    Because the GET stream and POST requests are decoupled, the client matches the incoming responses to its outstanding requests using the unique JSON-RPC **`id`** property.
*   **Disconnection Recovery:**
    If the SSE stream disconnects, the client re-establishes a new stream by making another `GET /mcp` request, obtains a new session ID from the handshake event, and repeats the initialization phase.

---

### Q5: Where in the codebase is the incoming JSON-RPC `id` preserved and sent back to the client?

The request `id` preservation is handled directly inside the `POST /mcp` route in [McpEndpoints.cs](file:///c:/Development/labs/mcp/src/McpServer/Mcp/McpEndpoints.cs#L78-L119). 

Here is how the data flows:

#### 1. DTO Mapping
The incoming request body is deserialized into the `JsonRpcRequest` DTO which declares the nullable `Id` property:
```csharp
public class JsonRpcRequest
{
    public string Jsonrpc { get; set; } = "2.0";
    public string Method { get; set; } = "";
    public object? Id { get; set; } // <--- Preserves the client-side ID (can be integer, string, or null)
    public JsonElement? Params { get; set; }
}
```

#### 2. Stateful Response (SSE Stream)
If a session is active, the response is pushed down the SSE GET channel. The server checks for an ID and passes it directly back:
```csharp
if (rpcRequest.Id != null)
{
    var jsonResponse = JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0",
        id = rpcRequest.Id, // <--- Preserves and outputs the matching client request ID
        result = resultPayload
    });

    var sseMessage = $"event: message\ndata: {jsonResponse}\n\n";
    await channel.Writer.WriteAsync(sseMessage);
}
```

#### 3. Stateless Response (Direct HTTP POST Response)
If no session is present, the server writes the payload directly to the HTTP body:
```csharp
var responseJson = JsonSerializer.Serialize(new
{
    jsonrpc = "2.0",
    id = rpcRequest.Id, // <--- Preserves and outputs the matching client request ID
    result = resultPayload
});
return Results.Content(responseJson, "application/json");
```
By mapping `id = rpcRequest.Id`, the client can successfully match the response with the request it sent.

---

### Q6: What is a JSON-RPC payload?

**JSON-RPC** is a lightweight, stateless, remote procedure call (RPC) protocol encoded in JSON. It defines simple, strict rules for how requests, responses, and notifications are formatted.

Here are the three types of payloads:

#### 1. Request Payload (Client → Server)
Sent by the client to execute a function on the server.
```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": {
    "name": "get_customer_info",
    "arguments": { "customerId": "CUS9999" }
  },
  "id": 105
}
```
*   **`jsonrpc`**: Must be exactly `"2.0"`.
*   **`method`**: The name of the action to execute.
*   **`params`**: Optional object or array containing arguments.
*   **`id`**: A unique identifier (integer or string) set by the client to map responses to requests. If `id` is omitted, it is considered a **Notification** (one-way message; server must not reply).

#### 2. Successful Response Payload (Server → Client)
Returned by the server after successfully processing a request.
```json
{
  "jsonrpc": "2.0",
  "result": {
    "content": [
      { "type": "text", "text": "Customer PGW Autoglass Test Account details..." }
    ]
  },
  "id": 105
}
```
*   **`jsonrpc`**: Must be exactly `"2.0"`.
*   **`result`**: The output data returned by the method.
*   **`id`**: Must match the exact `id` supplied in the matching request payload.

#### 3. Error Response Payload (Server → Client)
Returned by the server if validation fails, the method doesn't exist, or an exception occurs.
```json
{
  "jsonrpc": "2.0",
  "error": {
    "code": -32601,
    "message": "Method 'non_existent_tool' not found."
  },
  "id": 106
}
```
*   **`jsonrpc`**: Must be exactly `"2.0"`.
*   **`error`**: Object containing an integer `code` (e.g. standard JSON-RPC error codes), a string `message`, and optional `data`.
*   **`id`**: Must match the request ID, or be `null` if the request ID could not be resolved.

---

### Q7: Why doesn't `tools/list` declare a tool's return type? Must tool responses always be wrapped in `{"type": "text", "text": "..."}`? How do we return complex JSON?

Here is how the Model Context Protocol handles tool schemas and data output structures:

#### 1. Why `tools/list` has no return schema
In the MCP specification, **tools only specify input parameters (via JSON Schema)**. They do not specify output types.
*   **Reason:** The tool caller is an LLM. An LLM reads and interprets outputs dynamically like a human looking at a console screen. The protocol keeps tool outputs open-ended so any string, raw text, or structured dataset can be digested by the model.

#### 2. The standard response envelope (`CallToolResult`)
The MCP protocol mandates that a successful tool execution response must wrap its output inside a `content` array containing one or more of these standard block structures:
*   **Text Block:** `{"type": "text", "text": "..."}`
*   **Image Block:** `{"type": "image", "data": "base64...", "mimeType": "image/png"}`
*   **Resource Block:** `{"type": "resource", "resource": { ... }}`

#### 3. Returning Complex JSON
If you have a complex nested object (e.g., invoices, parts lists, customers), you cannot return the raw JSON object directly as a root response. Instead, you should:

**Serialize it to a JSON string inside a Text block (Standard/Recommended Way):**
```csharp
var myComplexData = new 
{
    invoiceId = 55432,
    amount = 145.50,
    items = new[] { "part_a", "part_b" }
};

return new
{
    content = new[]
    {
        new
        {
            type = "text",
            text = JsonSerializer.Serialize(myComplexData) // <--- Pushes JSON text directly to the LLM
        }
    },
    isError = false
};
```
*   **Why this is best:** LLMs are text-prediction models. Passing a clean JSON string inside the `text` field allows the model to parse, read, and write code based on your structured JSON data natively.

---

### Q8: How does the LLM know to call our specific MCP server? Does it identify it by server name (like `PGW-MCP-Auth-Server`), or by matching tool names and descriptions?

The LLM does **not** choose which server to call based on the server name. It is entirely driven by **Tool Names** and **Tool Descriptions**.

Here is how the tool discovery and orchestration process works:

#### 1. Consolidation
Upon startup, the client application (like Claude Desktop) queries all configured MCP servers for their available tools using the `tools/list` request. The client aggregates all responses into a single flat list (e.g. `[get_customer_info, search_web, compile_code]`).

#### 2. Prompt Insertion
The client injects this consolidated tool list (with their schemas and descriptions) directly into the LLM's system instructions. The LLM now has a global registry of all available functions.

#### 3. Semantic Description Matching
If you type: *"Get customer info for CUS9999 from PGW"*:
1.  The LLM parses your prompt and scans the **`description`** fields of all registered tools.
2.  It matches the intent *"Get customer info"* with the description we configured for our tool: `"Returns details about the specified customer code."`
3.  The LLM maps your variable `"CUS9999"` onto the `customerId` argument in our input schema:
    ```json
    "customerId": { "type": "string", "description": "The customer code (e.g., CUS9999)" }
    ```
4.  The LLM generates a JSON-RPC tool invocation request:
    ```json
    { "method": "tools/call", "params": { "name": "get_customer_info", "arguments": { "customerId": "CUS9999" } } }
    ```
5.  The client application intercepts this command, looks up which server exposed `get_customer_info`, finds our server connection, and forwards the POST request to us.

#### Why Tool Descriptions are Critical
*   If you write a detailed description (e.g., `"Fetches user profile, locations, and active balances from PGW's secure database for a given customer code"`), the LLM will reliably choose your tool.

---

### Q9: Should we put our company name (like "PGW") in tool names or descriptions to help the LLM route requests correctly?

**Yes, this is an industry best-practice known as "Domain Scoping" or "Namespacing."**

If your LLM client (e.g. Claude Desktop or a custom PGW Autoglass chatbot) has access to a mixed set of tools (some generic, like Google Search or local filesystem access, and some internal e-commerce tools), you must provide the LLM with unambiguous signals to identify your specific APIs.

Here are the two best ways to do this:

#### 1. Prefix Tool Names (Namespacing)
Instead of naming tools generically (which might collide with other servers or confuse the model), namespace them with a short prefix:
*   *Vague:* `get_customer_info`, `check_inventory`
*   *Scoped:* `pgw_get_customer_info`, `pgw_check_inventory`
*   *Why:* This ensures the LLM has a unique keyword target and eliminates naming collisions with other tools in the registry.

#### 2. Include Domain Keywords in Tool Descriptions
Explicitly write your company or system context in the semantic description block:
*   *Generic:* `"Checks inventory status for a part code."` (The LLM might call a public Google search or a mock database tool).
*   *Domain Scoped:* `"Checks stock availability for windshields and parts inside the PGW e-commerce inventory database."`
*   *Why:* When the user asks: *"Does PGW have part X in stock?"*, the LLM matches the keyword **"PGW"** and **"inventory"** to your tool's description, immediately choosing the correct database API over a general search tool.

#### When is this NOT required?
If you deploy a dedicated e-commerce chatbot where the system prompt restricts the LLM to *only* your database server, you do not need prefixes because there are no competing tools. However, for open-ended assistants, namespacing is highly recommended.

---

### Q10: What values should be entered in the MCP Inspector interface to test our OIDC-secured server?

To test your local server via the **MCP Inspector** UI (v0.22.0), enter the following values:

#### Method A: Manual Token Authentication (Easiest / Direct)
1.  **Transport Type:** `Streamable HTTP` (or `SSE`)
2.  **URL:** `http://localhost:5000/mcp`
3.  **Connection Type:** `Via Proxy`
4.  **Custom Headers Section:**
    *   Toggle the switch next to **`Authorization`** to **ON** (so it turns green/active).
    *   Set the value to: **`Bearer YOUR_ACCESS_TOKEN`** (copy the access token from your Python client/token exchange step).
5.  **Connect:** Click the **Connect** button at the bottom.

#### Method B: Integrated OAuth 2.0 Flow (Requires Rebuilding)
The MCP Inspector supports running the OIDC authorization code flow natively. However, to use this:
1.  We must first add the Inspector's redirect URL (e.g. `http://localhost:6274/oauth/` as shown in the screenshot) to the allowed redirect URIs list in [DbSeeder.cs](file:///c:/Development/labs/mcp/src/McpServer/Services/DbSeeder.cs#L38-L51).
2.  Once seeded and rebuilt, you can configure:
    *   **Client ID:** `mcp-client`
    *   **Client Secret:** (Leave blank)
    *   **Redirect URL:** `http://localhost:6274/oauth/`
    *   **Scope:** `openid profile mcp offline_access`

---

### Q11: Does Cloudflare Tunnel support the persistent "duplex" streaming required for the MCP SSE transport channel?

**Yes, Cloudflare Tunnels fully support long-lived HTTP streams, Server-Sent Events (SSE), and persistent connections.**

However, because Cloudflare acts as an optimizing reverse proxy, you must understand how it handles buffering and idle timeouts:

#### 1. Real-Time Streaming (Bypassing Buffering)
Cloudflare normally buffers HTTP responses to optimize page load speeds. However, for MCP Server-Sent Events (SSE), events must stream to the client instantly without buffering.
*   *How it is handled:* Our server sets the standard HTTP response headers:
    ```http
    Content-Type: text/event-stream
    Cache-Control: no-cache
    ```
    Cloudflare automatically detects these headers and disables response buffering, allowing events to pass through the tunnel in real-time.

#### 2. Idle Connection Timeouts (100-Second Rule)
Cloudflare has a strict **100-second idle timeout** limit. If a persistent HTTP connection (like our GET `/mcp` EventSource channel) does not transmit any data for 100 seconds, Cloudflare terminates it with a `524 Origin Time-out` error.
*   *How we prevent this:* The MCP protocol incorporates **Heartbeats/Pings**. Our C# EventSource implementation runs a background loop that regularly transmits ping comment lines (`:\n\n` or `event: ping`) to the client. This continuous transmission resets Cloudflare's 100-second idle timer, keeping the tunnel connection alive indefinitely.

#### 3. Client-to-Server Upstream (POST Requests)
Since MCP SSE transport decouples the channels (GET for server streaming to client, and separate POSTs for client writing back to server), Cloudflare processes the POST requests as normal, stateless HTTP calls. The proxy does not need to handle complex, low-level duplexing states, making it extremely robust and easy to scale.







