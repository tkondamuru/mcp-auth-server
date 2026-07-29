import asyncio
import os
import sys
import requests
import json

# Configuration
TOKEN_URL = "https://mcp.kondulabs.com/connect/token"
MCP_URL = "https://mcp.kondulabs.com/mcp"
CLIENT_ID = "mcp-client"
USERNAME = "CUS9999"
PASSWORD = "test5PGW"

# Step 1: Fetch Access Token via OIDC password grant flow
def fetch_access_token():
    print(f"Requesting OIDC access token from {TOKEN_URL}...")
    payload = {
        "grant_type": "password",
        "client_id": CLIENT_ID,
        "username": USERNAME,
        "password": PASSWORD,
        "scope": "openid profile mcp"
    }
    
    try:
        response = requests.post(TOKEN_URL, data=payload)
        response.raise_for_status()
        data = response.json()
        
        access_token = data.get("access_token")
        if access_token:
            print("Token retrieved successfully!")
            print(f"Token type: {data.get('token_type')}")
            print(f"Expires in: {data.get('expires_in')} seconds")
            print(f"Scope: {data.get('scope')}")
            return access_token
        else:
            print("Error: access_token not found in response.", data)
            sys.exit(1)
    except Exception as e:
        print("Failed to retrieve access token:", e)
        sys.exit(1)

# Step 2: Establish SSE connection using the official Python MCP SDK client
async def verify_mcp_sse(access_token):
    try:
        # We import here to avoid import error if the library is not installed
        from mcp import ClientSession
        from mcp.client.sse import sse_client
    except ImportError:
        print("\nError: The official python MCP SDK is not installed.")
        print("Please install it by running: pip install mcp")
        sys.exit(1)

    headers = {
        "Authorization": f"Bearer {access_token}"
    }

    print(f"\nConnecting to MCP SSE endpoint {MCP_URL}...")
    try:
        async with sse_client(MCP_URL, headers=headers) as (read_stream, write_stream):
            async with ClientSession(read_stream, write_stream) as session:
                print("Initiating protocol handshake (initialize)...")
                init_result = await session.initialize()
                print("Handshake Completed! Server info:")
                print(f" - Name: {init_result.server_info.name}")
                print(f" - Version: {init_result.server_info.version}")
                print(f" - Protocol Version: {init_result.protocol_version}")

                print("\nFetching tools from the server...")
                tools = await session.list_tools()
                
                print(f"\nVerification Success! Discovered {len(tools.tools)} tools:")
                for tool in tools.tools:
                    print(f" - Name: {tool.name}")
                    print(f"   Description: {tool.description}")
                    print(f"   Input Schema: {json.dumps(tool.input_schema)}")

    except Exception as e:
        print("\nError establishing MCP session:", e)
        sys.exit(1)

if __name__ == "__main__":
    token = fetch_access_token()
    asyncio.run(verify_mcp_sse(token))
