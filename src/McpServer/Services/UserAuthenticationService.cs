using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace McpServer.Services
{
    public class UserAuthenticationService : IUserAuthenticationService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<UserAuthenticationService> _logger;

        public UserAuthenticationService(
            IHttpClientFactory httpClientFactory, 
            IConfiguration configuration,
            ILogger<UserAuthenticationService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<ExternalAuthResult> ValidateCredentialsAsync(string username, string password)
        {
            _logger.LogInformation("Starting authentication process for user: {Username}", username);

            // Retrieve base URL from environment variable or appsettings config
            var baseUrl = _configuration["ExternalAuth:Endpoint"] ?? Environment.GetEnvironmentVariable("EXTERNAL_AUTH_ENDPOINT");
            
            if (string.IsNullOrEmpty(baseUrl))
            {
                _logger.LogError("External authentication base URL is not configured.");
                return new ExternalAuthResult
                {
                    Success = false,
                    ErrorMessage = "External authentication endpoint is not configured. Please set ExternalAuth:Endpoint configuration or EXTERNAL_AUTH_ENDPOINT environment variable."
                };
            }

            // Ensure the base URL ends with a trailing slash for clean combining
            if (!baseUrl.EndsWith("/"))
            {
                baseUrl += "/";
            }

            var authUrl = baseUrl + "mobileauth/authenticate";
            var getSessionUrl = baseUrl + "mobilesession/getsession";
            var saveSessionUrl = baseUrl + "mobilesession/session";

            _logger.LogInformation("Auth Endpoint: {AuthUrl}", authUrl);
            _logger.LogInformation("GetSession Endpoint: {GetSessionUrl}", getSessionUrl);
            _logger.LogInformation("SaveSession Endpoint: {SaveSessionUrl}", saveSessionUrl);

            var client = _httpClientFactory.CreateClient();
            var payload = new
            {
                customerId = username,
                password = password
            };

            try
            {
                var response = await client.PostAsJsonAsync(authUrl, payload);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Authentication API returned error status: {StatusCode}", response.StatusCode);
                    return new ExternalAuthResult 
                    { 
                        Success = false, 
                        ErrorMessage = $"External server returned HTTP status: {response.StatusCode}" 
                    };
                }

                var apiResponse = await response.Content.ReadFromJsonAsync<ApiResponse>();
                if (apiResponse == null || !apiResponse.Success || apiResponse.Data == null)
                {
                    var errMsg = apiResponse?.Data?.ErrorMessage ?? "Authentication failed on external server.";
                    _logger.LogWarning("External authentication rejected: {ErrorMessage}", errMsg);
                    return new ExternalAuthResult
                    {
                        Success = false,
                        ErrorMessage = errMsg
                    };
                }

                if (!apiResponse.Data.IsAuthenticated)
                {
                    var errMsg = apiResponse.Data.ErrorMessage ?? "User is not authenticated.";
                    _logger.LogWarning("External authentication reports not authenticated: {ErrorMessage}", errMsg);
                    return new ExternalAuthResult
                    {
                        Success = false,
                        ErrorMessage = errMsg
                    };
                }

                _logger.LogInformation("External authentication successful for user {Username}. Initiating T&C session sync...", username);

                // --- Terms & Conditions (T&C) Verification Loop ---
                try
                {
                    // Configure HttpClient with Bearer token authentication header
                    var sessionClient = _httpClientFactory.CreateClient();
                    sessionClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiResponse.Data.Token);

                    // Step 1: Fetch the current user session
                    var sessionRequestPayload = new { SessionKey = apiResponse.Data.SessionKey };
                    _logger.LogInformation("Fetching user session using SessionKey: {SessionKey}", apiResponse.Data.SessionKey);
                    
                    var sessionResponse = await sessionClient.PostAsJsonAsync(getSessionUrl, sessionRequestPayload);
                    if (!sessionResponse.IsSuccessStatusCode)
                    {
                        _logger.LogError("GetSession API failed with HTTP status: {StatusCode}", sessionResponse.StatusCode);
                        return new ExternalAuthResult
                        {
                            Success = false,
                            ErrorMessage = $"Failed to retrieve user session from external service. HTTP status: {sessionResponse.StatusCode}"
                        };
                    }

                    var sessionString = await sessionResponse.Content.ReadAsStringAsync();
                    _logger.LogInformation("User session retrieved successfully. Raw JSON: {RawJson}", sessionString);

                    var sessionNode = System.Text.Json.Nodes.JsonNode.Parse(sessionString);
                    if (sessionNode == null)
                    {
                        _logger.LogError("User session JSON parsing returned null.");
                        return new ExternalAuthResult
                        {
                            Success = false,
                            ErrorMessage = "Retrieved user session response is empty or invalid JSON."
                        };
                    }

                    // Extract the session object, handling arrays, envelopes, or raw objects
                    System.Text.Json.Nodes.JsonObject sessionObj;
                    if (sessionNode is System.Text.Json.Nodes.JsonArray rootArray && rootArray.Count > 0 && rootArray[0] is System.Text.Json.Nodes.JsonObject firstObj)
                    {
                        sessionObj = firstObj;
                        _logger.LogInformation("Successfully extracted session object from first element of JSON Array.");
                    }
                    else if (sessionNode is System.Text.Json.Nodes.JsonObject rootObj)
                    {
                        var hasSuccess = rootObj.TryGetPropertyValue("success", out var successVal) || rootObj.TryGetPropertyValue("Success", out successVal);
                        var hasData = rootObj.TryGetPropertyValue("data", out var dataVal) || rootObj.TryGetPropertyValue("Data", out dataVal);

                        if (hasSuccess && hasData && dataVal is System.Text.Json.Nodes.JsonObject dataObj)
                        {
                            sessionObj = dataObj;
                            _logger.LogInformation("Successfully unpacked session object from envelope data.");
                        }
                        else
                        {
                            sessionObj = rootObj;
                            _logger.LogInformation("Using root JSON object as the session payload.");
                        }
                    }
                    else
                    {
                        _logger.LogError("Session payload is not a valid JSON Array or JSON Object. Type: {NodeType}", sessionNode.GetType().Name);
                        return new ExternalAuthResult
                        {
                            Success = false,
                            ErrorMessage = "Failed to parse retrieved user session."
                        };
                    }

                    // Step 2: Modify session data locally (supporting case-insensitive properties)
                    if (sessionObj.ContainsKey("termsOfUse")) sessionObj["termsOfUse"] = true;
                    else sessionObj["TermsOfUse"] = true;
                    _logger.LogInformation("T&C value set: termsOfUse = true");

                    // Clean up properties that don't need to be resent (case-insensitively)
                    int removedCount = 0;
                    if (sessionObj.Remove("currentShipTo") || sessionObj.Remove("CurrentShipTo")) removedCount++;
                    if (sessionObj.Remove("localIP") || sessionObj.Remove("LocalIP")) removedCount++;
                    if (sessionObj.Remove("blockCount") || sessionObj.Remove("BlockCount")) removedCount++;
                    _logger.LogInformation("Cleaned up {Count} session-specific read-only properties.", removedCount);

                    // Step 3: Save updated session back to the server
                    _logger.LogInformation("Saving updated session back to the server...");
                    var saveResponse = await sessionClient.PostAsJsonAsync(saveSessionUrl, sessionObj);
                    if (!saveResponse.IsSuccessStatusCode)
                    {
                        _logger.LogError("SaveSession API failed with HTTP status: {StatusCode}", saveResponse.StatusCode);
                        return new ExternalAuthResult
                        {
                            Success = false,
                            ErrorMessage = $"Failed to save updated terms agreement session on external service. HTTP status: {saveResponse.StatusCode}"
                        };
                    }

                    _logger.LogInformation("Successfully saved terms agreement session on external server.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "T&C session orchestration failed with exception.");
                    return new ExternalAuthResult
                    {
                        Success = false,
                        ErrorMessage = $"T&C session orchestration failed: {ex.Message}"
                    };
                }

                // Decode token expiration from the JWT payload claim "exp"
                var tokenExpiration = GetJwtExpiration(apiResponse.Data.Token);

                return new ExternalAuthResult
                {
                    Success = true,
                    Token = apiResponse.Data.Token,
                    SessionKey = apiResponse.Data.SessionKey,
                    CustomerId = apiResponse.Data.CustomerId,
                    TokenExpiration = tokenExpiration,
                    ErrorMessage = apiResponse.Data.ErrorMessage
                };
            }
            catch (Exception ex)
            {
                return new ExternalAuthResult
                {
                    Success = false,
                    ErrorMessage = $"Connection to external authentication service failed: {ex.Message}"
                };
            }
        }

        // Decodes the payload section of the JWT without verification to read the 'exp' claim
        private static DateTime? GetJwtExpiration(string? jwtToken)
        {
            if (string.IsNullOrEmpty(jwtToken)) return null;

            try
            {
                var parts = jwtToken.Split('.');
                if (parts.Length < 2) return null;

                var payloadBase64 = parts[1];
                // Replace Base64Url padding characters
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

        // Inner classes representing the JSON structure of the external API response
        private class ApiResponse
        {
            public bool Success { get; set; }
            public ApiData? Data { get; set; }
        }

        private class ApiData
        {
            public string? CustomerId { get; set; }
            public string? Token { get; set; }
            public string? SessionKey { get; set; }
            public bool IsAuthenticated { get; set; }
            public bool IsLocked { get; set; }
            public bool IsFirstTimeLogin { get; set; }
            public string? ErrorMessage { get; set; }
        }
    }
}
