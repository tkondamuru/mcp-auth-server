# OIDC and MCP Server Integration Test Script
$ErrorActionPreference = "Stop"

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "1. Testing OIDC Discovery Endpoint" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan

$discovery = Invoke-RestMethod -Uri "http://localhost:5000/.well-known/openid-configuration" -Method Get
Write-Host "Issuer: $($discovery.issuer)" -ForegroundColor Green
Write-Host "Token Endpoint: $($discovery.token_endpoint)" -ForegroundColor Green
Write-Host "UserInfo Endpoint: $($discovery.userinfo_endpoint)" -ForegroundColor Green
Write-Host "Supported Grant Types: $($discovery.grant_types_supported -join ', ')" -ForegroundColor Green

Write-Host "`n=============================================" -ForegroundColor Cyan
Write-Host "2. Acquiring Access Token (Password Flow)" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan

$tokenBody = @{
    grant_type = "password"
    client_id  = "mcp-client"
    username   = "CUS9999"
    password   = "test5PGW"
    scope      = "openid profile mcp offline_access"
}

$tokenResponse = Invoke-RestMethod -Uri $discovery.token_endpoint -Method Post -Body $tokenBody -ContentType "application/x-www-form-urlencoded"
$accessToken = $tokenResponse.access_token
$refreshToken = $tokenResponse.refresh_token

Write-Host "Access Token (First 40 chars): $($accessToken.Substring(0, [Math]::Min(40, $accessToken.Length)))..." -ForegroundColor Green
Write-Host "Refresh Token (First 40 chars): $($refreshToken.Substring(0, [Math]::Min(40, $refreshToken.Length)))..." -ForegroundColor Green
Write-Host "Expires In: $($tokenResponse.expires_in) seconds" -ForegroundColor Green

Write-Host "`n=============================================" -ForegroundColor Cyan
Write-Host "3. Fetching User Information (UserInfo Endpoint)" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan

$headers = @{
    "Authorization" = "Bearer $accessToken"
}
$userInfo = Invoke-RestMethod -Uri $discovery.userinfo_endpoint -Method Get -Headers $headers
Write-Host "Authenticated Subject (sub): $($userInfo.sub)" -ForegroundColor Green
Write-Host "Preferred Username: $($userInfo.preferred_username)" -ForegroundColor Green

Write-Host "`n=============================================" -ForegroundColor Cyan
Write-Host "4. Testing Authenticated MCP SSE Stream Connection" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan

# Establish the SSE connection
$sseUri = "http://localhost:5000/mcp?access_token=$accessToken"
Write-Host "Connecting to: $sseUri" -ForegroundColor Yellow

$request = [System.Net.WebRequest]::Create($sseUri)
$request.Timeout = 10000
$response = $request.GetResponse()
$reader = New-Object System.IO.StreamReader($response.GetResponseStream())

# Read the initial endpoint event from the SSE stream
$line1 = $reader.ReadLine() # Should be event: endpoint
$line2 = $reader.ReadLine() # Should be data: http://localhost:5000/message?sessionId=xxx
$line3 = $reader.ReadLine() # Should be blank line separator

Write-Host "Received Event: $line1" -ForegroundColor Green
Write-Host "Received Data:  $line2" -ForegroundColor Green

# Extract the message endpoint URL from the data line
$endpointUrl = ""
if ($line2 -match "data: (https?://[^\s]+)") {
    $endpointUrl = $Matches[1]
    Write-Host "Parsed Target Message Endpoint: $endpointUrl" -ForegroundColor Green
} else {
    Write-Host "Failed to parse message endpoint!" -ForegroundColor Red
    $response.Close()
    exit 1
}

Write-Host "`n=============================================" -ForegroundColor Cyan
Write-Host "5. Sending MCP 'initialize' JSON-RPC Request" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan

# Prepare the initialize payload
$initializePayload = @{
    jsonrpc = "2.0"
    method = "initialize"
    id = 1
    params = @{
        protocolVersion = "2024-11-05"
        capabilities = @{}
        clientInfo = @{
            name = "verify-script-mock"
            version = "1.0.0"
        }
    }
} | ConvertTo-Json -Depth 5

Write-Host "POSTing payload to: $endpointUrl" -ForegroundColor Yellow
$postResponse = Invoke-WebRequest -Uri $endpointUrl -Method Post -Body $initializePayload -ContentType "application/json" -Headers $headers
Write-Host "POST Response Status Code: $($postResponse.StatusCode) ($($postResponse.StatusDescription))" -ForegroundColor Green

Write-Host "`nReading JSON-RPC Response from SSE Stream..." -ForegroundColor Yellow
$respLine1 = $reader.ReadLine() # event: message
$respLine2 = $reader.ReadLine() # data: { ... }
$respLine3 = $reader.ReadLine() # blank line separator

Write-Host "Received SSE Event: $respLine1" -ForegroundColor Green
Write-Host "Received SSE Data:  $respLine2" -ForegroundColor Green

# Parse JSON-RPC response
$rpcResponse = ConvertFrom-Json ($respLine2.Replace("data: ", ""))
Write-Host "MCP Protocol Version: $($rpcResponse.result.protocolVersion)" -ForegroundColor Green
Write-Host "MCP Server Name: $($rpcResponse.result.serverInfo.name)" -ForegroundColor Green

# Close connection
$response.Close()
Write-Host "`n=============================================" -ForegroundColor Cyan
Write-Host "OIDC + MCP End-To-End Verification SUCCESSFUL!" -ForegroundColor Green
Write-Host "=============================================" -ForegroundColor Cyan
