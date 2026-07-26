# Technical Q&A: OIDC & MCP Server Integration

Quick-reference log of key architectural questions, decisions, and concepts.

---

### Q1: In MCP transport, what is the difference between STDIO, SSE, and Streamable HTTP?
*   **STDIO (Standard I/O):**
    *   *Mechanism:* Local subprocess communication using standard input (`stdin`) and standard output (`stdout`).
    *   *Key Benefit:* Zero network configuration (no ports, CORS, or SSL).
    *   *Constraint:* Local-only; client and server must run on the same machine.
*   **SSE (Server-Sent Events):**
    *   *Mechanism:* Remote HTTP transport using split endpoints: a persistent GET stream `/sse` (Server -> Client) and HTTP POST `/message?sessionId=xxx` (Client -> Server).
    *   *Key Benefit:* Allows remote cloud hosting.
    *   *Constraint:* Architecturally complex; requires maintaining session IDs in-memory.
*   **Streamable HTTP (Modern Remote Standard):**
    *   *Mechanism:* A single HTTP endpoint `/mcp` handling both GET and POST requests over HTTP/2 or HTTP/3 streams.
    *   *Key Benefit:* Consolidates routing into a single path; highly compatible with reverse proxies and load balancers.
    *   *Constraint:* Requires client/server support for full-duplex HTTP/2.

---

### Q2: For a web application hosted behind IIS in a load balancer environment, what is the best way to host an MCP server?
*   **Enable Sticky Sessions (Session Affinity):** Configure the load balancer to route all requests from a client to the same IIS server. Required so POST requests land on the server holding the client's GET connection.
*   **Shared Redis Backplane:** If sticky sessions are disabled, use Redis Pub/Sub to sync messages statelessly across IIS servers.
*   **Disable IIS Response Buffering:** Set `responseBufferLimit` to `0` in `web.config` (or call `DisableBuffering()` in C#) so SSE/Streamable HTTP messages aren't queued in IIS memory.
*   **ARR Timeout Heartbeats:** Send SSE heartbeat comments (`:\n\n`) every 15-30 seconds to prevent IIS Application Request Routing from dropping idle streams.

---

### Q3: Will Streamable HTTP work behind IIS?
*   **Yes, but only under strict requirements:**
    1.  **Windows Server 2022+ / Windows 11+:** Required for kernel-level HTTP/2 full-duplex support.
    2.  **In-Process Hosting:** The application must run inside the IIS worker process. Out-of-process proxies downgrade backend traffic to HTTP/1.1.
    3.  **HTTPS Enabled:** IIS restricts HTTP/2 to TLS/SSL connections.
*   **Recommendation:** If hosting on older OS versions, bypass IIS (run Kestrel directly) or use the legacy SSE transport (works natively over HTTP/1.1).

---

### Q4: If we host on a Linux machine behind Apache (httpd), is the Streamable HTTP transport an option?
*   **Yes, but it is complex and not ideal:**
    *   Must enable `mod_proxy_http2` and explicitly proxy using `h2c://` (HTTP/2 cleartext) to avoid downgrading backend traffic to HTTP/1.1.
    *   Must use the `event` Multi-Processing Module (MPM) to prevent long-lived streams from exhausting Apache's thread pool.
*   **Recommendation:** **Nginx** is highly preferred over Apache on Linux because its event-driven model handles thousands of persistent HTTP/2 connections with minimal RAM and CPU.

---

### Q5: Does Kestrel bind to a DNS name? Do we need to run it as a daemon/container? Is Nginx a better front-end?
*   **Kestrel Binding:** Binds only to network sockets (IP/Port, like `127.0.0.1:5000`). It does not handle DNS resolution or SSL certificate auto-renewals.
*   **Execution:** Run Kestrel in the background as a **Systemd Service (daemon)** or inside a **Docker Container** exposing the port.
*   **Nginx Reverse Proxy:** Yes, putting Nginx in front of Kestrel is best-practice because Nginx:
    *   Handles SSL/TLS termination and auto-renewals (Certbot).
    *   Shields Kestrel (rate-limiting, security headers, DDoS protection).
    *   Offloads static file delivery (directly serves files like `login.html`).
    *   Proxies HTTP/2 streams natively to the backend daemon/container.
