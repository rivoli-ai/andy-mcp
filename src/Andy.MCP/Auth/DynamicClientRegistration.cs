using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.MCP.Auth;

/// <summary>
/// RFC 7591 Dynamic Client Registration request.
/// </summary>
public sealed record ClientRegistrationRequest
{
    [JsonPropertyName("redirect_uris")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RedirectUris { get; init; }

    [JsonPropertyName("grant_types")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? GrantTypes { get; init; }

    [JsonPropertyName("response_types")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ResponseTypes { get; init; }

    [JsonPropertyName("application_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApplicationType { get; init; }

    [JsonPropertyName("token_endpoint_auth_method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TokenEndpointAuthMethod { get; init; }

    [JsonPropertyName("scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scope { get; init; }

    [JsonPropertyName("client_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientName { get; init; }

    [JsonPropertyName("client_uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientUri { get; init; }

    [JsonPropertyName("logo_uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogoUri { get; init; }
}

/// <summary>
/// RFC 7591 Dynamic Client Registration response.
/// </summary>
public sealed record ClientRegistrationResponse
{
    [JsonPropertyName("client_id")]
    public required string ClientId { get; init; }

    [JsonPropertyName("client_secret")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientSecret { get; init; }

    [JsonPropertyName("client_id_issued_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ClientIdIssuedAt { get; init; }

    [JsonPropertyName("client_secret_expires_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ClientSecretExpiresAt { get; init; }

    [JsonPropertyName("registration_access_token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RegistrationAccessToken { get; init; }

    [JsonPropertyName("registration_client_uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RegistrationClientUri { get; init; }
}

/// <summary>
/// Client for RFC 7591/7592 Dynamic Client Registration.
/// </summary>
public sealed class DynamicClientRegistrationClient
{
    private readonly HttpClient _httpClient;

    public DynamicClientRegistrationClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// Register a new client at the registration endpoint (RFC 7591).
    /// </summary>
    public async Task<ClientRegistrationResponse> RegisterAsync(
        string registrationEndpoint,
        ClientRegistrationRequest request,
        string? initialAccessToken = null,
        CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(request);
        var httpReq = new HttpRequestMessage(HttpMethod.Post, registrationEndpoint)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

        if (initialAccessToken is not null)
            httpReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", initialAccessToken);

        var httpResp = await _httpClient.SendAsync(httpReq, ct);
        httpResp.EnsureSuccessStatusCode();

        var respJson = await httpResp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ClientRegistrationResponse>(respJson)
            ?? throw new InvalidOperationException("Failed to parse registration response.");
    }

    /// <summary>
    /// Read client configuration (RFC 7592).
    /// </summary>
    public async Task<ClientRegistrationResponse> GetConfigurationAsync(
        string registrationClientUri,
        string registrationAccessToken,
        CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, registrationClientUri);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", registrationAccessToken);

        var resp = await _httpClient.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ClientRegistrationResponse>(json)!;
    }

    /// <summary>
    /// Delete client registration (RFC 7592).
    /// </summary>
    public async Task DeleteAsync(
        string registrationClientUri,
        string registrationAccessToken,
        CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, registrationClientUri);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", registrationAccessToken);

        var resp = await _httpClient.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }
}
