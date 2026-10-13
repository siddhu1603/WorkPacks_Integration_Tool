using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using WPIntegrationTool.Models;

namespace WPIntegrationTool.Services
{
    /// <summary>
    /// Handles posting payloads to the data integration API.
    /// Supports both NTLM and OIDC (Client Credentials) authentication.
    /// </summary>
    public class ApiService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ConnectionSettings _settings;

        // OIDC token cache
        private string? _accessToken;
        private DateTime _tokenExpiry = DateTime.MinValue;

        public ApiService(ConnectionSettings settings)
        {
            _settings = settings;

            var handler = new HttpClientHandler
            {
                PreAuthenticate = true
            };

            if (settings.AuthType == AuthType.NTLM)
            {
                // NTLM authentication
                if (!string.IsNullOrWhiteSpace(settings.Username))
                {
                    handler.Credentials = new NetworkCredential(
                        settings.Username,
                        settings.Password,
                        settings.Domain);
                }
                else
                {
                    // Fall back to current Windows identity
                    handler.UseDefaultCredentials = true;
                }
            }

            // Many internal servers use self-signed certs
            if (settings.IgnoreSslErrors)
            {
                handler.ServerCertificateCustomValidationCallback =
                    (_, _, _, _) => true;
            }

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(5)
            };
        }

        // ──────────────────────────────────────────────────────────────
        //  ENDPOINTS
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// The data publish endpoint.
        /// NTLM: {BaseUrl}/api/v1.2/datasources/Generic/{DatasourceName}/data
        /// OIDC: {BaseUrl}/api/v1.2/datasources/Generic/{DatasourceName}/data
        /// </summary>
        private string DataEndpoint =>
            $"{_settings.BaseUrl.TrimEnd('/')}/api/v1.2/datasources/Generic/{_settings.DatasourceName}/data";

        /// <summary>
        /// Acknowledgements endpoint for connection testing.
        /// </summary>
        private string AcknowledgementsEndpoint =>
            $"{_settings.BaseUrl.TrimEnd('/')}/api/v1.2/datasources/Generic/{_settings.DatasourceName}/acknowledgements";

        // ──────────────────────────────────────────────────────────────
        //  OIDC TOKEN ACQUISITION (Client Credentials Grant)
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Acquires an access token using the OAuth2 Client Credentials grant.
        /// Caches the token and refreshes it when expired.
        /// </summary>
        private async Task<string> GetOidcTokenAsync()
        {
            // Return cached token if still valid (with 60s safety margin)
            if (_accessToken != null && DateTime.UtcNow < _tokenExpiry.AddSeconds(-60))
                return _accessToken;

            string authority = _settings.OidcAuthority.TrimEnd('/');
            string tokenEndpoint = $"{authority}/oauth/token";

            var tokenRequest = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _settings.OidcClientId,
                ["client_secret"] = _settings.OidcClientSecret,
                ["audience"] = _settings.OidcAudience,
                ["scope"] = _settings.OidcScopes
            };

            // Use a separate handler for the token request (no NTLM, no default creds)
            using var tokenHandler = new HttpClientHandler();
            if (_settings.IgnoreSslErrors)
                tokenHandler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

            using var tokenClient = new HttpClient(tokenHandler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            var content = new FormUrlEncodedContent(tokenRequest);
            HttpResponseMessage response;

            try
            {
                response = await tokenClient.PostAsync(tokenEndpoint, content);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to connect to OIDC token endpoint.\n" +
                    $"  URL: {tokenEndpoint}\n" +
                    $"  Error: {ex.Message}\n\n" +
                    "Verify the Authority URL is correct and the server is reachable.", ex);
            }

            string responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                string detail = $"OIDC token request failed (HTTP {(int)response.StatusCode}).\n" +
                                $"  Token endpoint: {tokenEndpoint}\n" +
                                $"  Response: {responseBody}\n\n";

                if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
                    detail += "The Client ID or Client Secret is incorrect, or the client " +
                              "does not have permission for the requested audience/scope.";
                else if ((int)response.StatusCode == 404)
                    detail += "Token endpoint not found. Verify the Authority URL is correct. " +
                              "Expected endpoint: {Authority}/oauth/token";
                else
                    detail += "Check the Authority URL, Client ID, Client Secret, and Audience values.";

                throw new InvalidOperationException(detail);
            }

            // Parse the token response
            var tokenResponse = JsonConvert.DeserializeAnonymousType(responseBody, new
            {
                access_token = "",
                token_type = "",
                expires_in = 0,
                scope = ""
            });

            if (string.IsNullOrWhiteSpace(tokenResponse?.access_token))
            {
                throw new InvalidOperationException(
                    "OIDC token response did not contain an access_token.\n" +
                    $"  Response: {responseBody}");
            }

            _accessToken = tokenResponse.access_token;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(tokenResponse.expires_in > 0 ? tokenResponse.expires_in : 3600);

            return _accessToken;
        }

        /// <summary>
        /// Ensures the Authorization header is set for OIDC requests.
        /// </summary>
        private async Task ApplyOidcAuthAsync(HttpRequestMessage request)
        {
            if (_settings.AuthType != AuthType.OIDC)
                return;

            string token = await GetOidcTokenAsync();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        // ──────────────────────────────────────────────────────────────
        //  PUBLISH DATA
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Posts a single publish payload and returns the acknowledgement ID.
        /// </summary>
        public async Task<ApiResult> PublishAsync(PublishPayload payload)
        {
            try
            {
                string json = JsonConvert.SerializeObject(payload, Formatting.None);

                var request = new HttpRequestMessage(HttpMethod.Post, DataEndpoint);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                // Buffer content for NTLM challenge-response retry
                await request.Content.LoadIntoBufferAsync();

                // Apply OIDC Bearer token if applicable
                await ApplyOidcAuthAsync(request);

                var response = await _httpClient.SendAsync(request);
                string body = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var result = JsonConvert.DeserializeAnonymousType(body, new { acknowledgementId = "" });
                    return ApiResult.Success(
                        result?.acknowledgementId ?? "unknown",
                        (int)response.StatusCode,
                        body);
                }

                return ApiResult.Failure(
                    $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}",
                    (int)response.StatusCode,
                    body);
            }
            catch (Exception ex)
            {
                return ApiResult.Failure(ex.Message, 0, ex.ToString());
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  ACKNOWLEDGEMENT STATUS
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Fetches the downstream processing status of a previously published chunk.
        /// A chunk that stays unconsumed indicates no consumer is reading the datasource.
        /// </summary>
        public async Task<ApiResult> GetAcknowledgementAsync(string acknowledgementId)
        {
            try
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Get, $"{AcknowledgementsEndpoint}/{acknowledgementId}");

                await ApplyOidcAuthAsync(request);

                var response = await _httpClient.SendAsync(request);
                string body = await response.Content.ReadAsStringAsync();

                return response.IsSuccessStatusCode
                    ? ApiResult.Success(acknowledgementId, (int)response.StatusCode, body)
                    : ApiResult.Failure(
                        $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}",
                        (int)response.StatusCode, body);
            }
            catch (Exception ex)
            {
                return ApiResult.Failure(ex.Message, 0, ex.ToString());
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  TEST CONNECTION
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Tests connectivity and authentication.
        /// For NTLM: GET on the acknowledgements endpoint.
        /// For OIDC: First acquires a token, then GETs the acknowledgements endpoint.
        /// </summary>
        public async Task<ApiResult> TestConnectionAsync()
        {
            try
            {
                // For OIDC, test token acquisition first
                if (_settings.AuthType == AuthType.OIDC)
                {
                    try
                    {
                        string token = await GetOidcTokenAsync();
                        // Show first/last chars of token for confirmation without exposing it
                        string masked = token.Length > 10
                            ? $"{token[..5]}...{token[^5..]}"
                            : "***";
                        // Token acquired successfully, continue to endpoint test
                    }
                    catch (Exception ex)
                    {
                        return ApiResult.Failure(
                            $"OIDC token acquisition failed: {ex.Message}",
                            0, ex.ToString());
                    }
                }

                var request = new HttpRequestMessage(HttpMethod.Get, AcknowledgementsEndpoint);
                request.Content = new StringContent("", Encoding.UTF8, "application/json");

                await ApplyOidcAuthAsync(request);

                var response = await _httpClient.SendAsync(request);
                string body = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    string extra = _settings.AuthType == AuthType.OIDC
                        ? " (OIDC token acquired + API accessible)"
                        : "";
                    return ApiResult.Success($"Connected{extra}", (int)response.StatusCode, body);
                }

                return ApiResult.Failure(
                    $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}",
                    (int)response.StatusCode,
                    body);
            }
            catch (Exception ex)
            {
                return ApiResult.Failure(ex.Message, 0, ex.ToString());
            }
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  RESULT WRAPPER
    // ──────────────────────────────────────────────────────────────

    public class ApiResult
    {
        public bool IsSuccess { get; set; }
        public string AcknowledgementId { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public int StatusCode { get; set; }
        public string RawResponse { get; set; } = string.Empty;

        public static ApiResult Success(string ackId, int statusCode, string raw) =>
            new() { IsSuccess = true, AcknowledgementId = ackId, StatusCode = statusCode, RawResponse = raw };

        public static ApiResult Failure(string error, int statusCode, string raw) =>
            new() { IsSuccess = false, ErrorMessage = error, StatusCode = statusCode, RawResponse = raw };
    }
}
