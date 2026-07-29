import subprocess
import sys
import json
import os

EXE_PATH = os.path.join("src", "McpConsoleServer", "bin", "Debug", "net10.0", "McpConsoleServer.exe")

if not os.path.exists(EXE_PATH):
    print(f"Error: Compiled binary not found at {EXE_PATH}")
    print("Please run 'dotnet build src/McpConsoleServer' first.")
    sys.exit(1)

print(f"Spawning local C# MCP Console Server: {EXE_PATH}")
process = subprocess.Popen(
    [EXE_PATH],
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    stderr=subprocess.PIPE,
    text=True,
    bufsize=1  # Line-buffered
)

def send_and_receive(payload):
    print(f"\n[CLIENT SEND] ---> {payload.strip()}")
    # Write to stdin and flush
    process.stdin.write(payload + "\n")
    process.stdin.flush()
    
    # Read response line from stdout
    line = process.stdout.readline()
    print(f"[SERVER RECV] <--- {line.strip()}")
    return line

try:
    # 1. Send initialize
    send_and_receive(json.dumps({
        "jsonrpc": "2.0",
        "method": "initialize",
        "params": {
            "protocolVersion": "2024-11-05",
            "clientInfo": {"name": "test-client", "version": "1.0.0"}
        },
        "id": 1
    }))

    # 2. Send tools/list
    send_and_receive(json.dumps({
        "jsonrpc": "2.0",
        "method": "tools/list",
        "id": 2
    }))

    # 3. Send tools/call
    send_and_receive(json.dumps({
        "jsonrpc": "2.0",
        "method": "tools/call",
        "params": {
            "name": "local_greet",
            "arguments": {"name": "TJ"}
        },
        "id": 3
    }))

    # 4. Terminate cleanly
    print("\nTerminating process...")
    process.stdin.write("exit\n")
    process.stdin.flush()
    process.wait(timeout=2)
    print("Process exited successfully!")

except Exception as e:
    print(f"Error during execution: {e}")
    process.kill()
