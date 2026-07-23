using System.Net.Http.Headers;
using System.Text;
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
/// Complete, explicit RFC 7591 client metadata to use for an RFC 7592 replacement update.
/// Omitted fields deliberately request their removal; this type is kept separate from the
/// initial registration request to make that replacement behavior visible to callers.
/// </summary>
public sealed record ClientRegistrationMetadata
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

    [JsonPropertyName("contacts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Contacts { get; init; }

    [JsonPropertyName("tos_uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TosUri { get; init; }

    [JsonPropertyName("policy_uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PolicyUri { get; init; }

    [JsonPropertyName("jwks_uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JwksUri { get; init; }

    [JsonPropertyName("jwks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Jwks { get; init; }

    [JsonPropertyName("software_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SoftwareId { get; init; }

    [JsonPropertyName("software_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SoftwareVersion { get; init; }

    /// <summary>Additional registered metadata returned by an authorization server.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalMetadata { get; init; }
}

/// <summary>
/// RFC 7591/7592 client information response, including both registered metadata and
/// registration-management state.
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

    [JsonPropertyName("contacts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Contacts { get; init; }

    [JsonPropertyName("tos_uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TosUri { get; init; }

    [JsonPropertyName("policy_uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PolicyUri { get; init; }

    [JsonPropertyName("jwks_uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JwksUri { get; init; }

    [JsonPropertyName("jwks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Jwks { get; init; }

    [JsonPropertyName("software_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SoftwareId { get; init; }

    [JsonPropertyName("software_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SoftwareVersion { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalMetadata { get; init; }

    /// <summary>Returns the complete registered metadata as an explicit replacement object.</summary>
    public ClientRegistrationMetadata ToMetadata() => new()
    {
        RedirectUris = RedirectUris,
        GrantTypes = GrantTypes,
        ResponseTypes = ResponseTypes,
        ApplicationType = ApplicationType,
        TokenEndpointAuthMethod = TokenEndpointAuthMethod,
        Scope = Scope,
        ClientName = ClientName,
        ClientUri = ClientUri,
        LogoUri = LogoUri,
        Contacts = Contacts,
        TosUri = TosUri,
        PolicyUri = PolicyUri,
        JwksUri = JwksUri,
        Jwks = Jwks,
        SoftwareId = SoftwareId,
        SoftwareVersion = SoftwareVersion,
        AdditionalMetadata = AdditionalMetadata is null ? null : new Dictionary<string, JsonElement>(AdditionalMetadata, StringComparer.Ordinal)
    };
}

/// <summary>
/// Client for RFC 7591/7592 Dynamic Client Registration.
/// </summary>
public sealed class DynamicClientRegistrationClient
{
    private static readonly HashSet<string> ReservedUpdateFields = new(StringComparer.Ordinal)
    {
        "client_id",
        "client_secret",
        "registration_access_token",
        "registration_client_uri",
        "client_id_issued_at",
        "client_secret_expires_at"
    };

    private readonly HttpClient _httpClient;

    public DynamicClientRegistrationClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    }

    /// <summary>Register a new client at the registration endpoint (RFC 7591).</summary>
    public async Task<ClientRegistrationResponse> RegisterAsync(
        string registrationEndpoint,
        ClientRegistrationRequest request,
        string? initialAccessToken = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var registrationUri = ValidateHttpsUri(registrationEndpoint, nameof(registrationEndpoint));
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, registrationUri)
        {
            Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(initialAccessToken))
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", initialAccessToken);

        using var response = await _httpClient.SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();
        return DeserializeRegistrationResponse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Read client configuration (RFC 7592).</summary>
    public async Task<ClientRegistrationResponse> GetConfigurationAsync(
        string registrationClientUri,
        string registrationAccessToken,
        CancellationToken ct = default)
    {
        var registrationUri = ValidateHttpsUri(registrationClientUri, nameof(registrationClientUri));
        ValidateRegistrationAccessToken(registrationAccessToken, nameof(registrationAccessToken));
        using var request = new HttpRequestMessage(HttpMethod.Get, registrationUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", registrationAccessToken);

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return DeserializeRegistrationResponse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Read configuration using the current registration state and atomically apply any credential rotation.
    /// </summary>
    public async Task<ClientRegistrationResponse> GetConfigurationAsync(
        ClientRegistrationResponse registration,
        CancellationToken ct = default)
    {
        ValidateManagementState(registration);
        var response = await GetConfigurationAsync(registration.RegistrationClientUri!, registration.RegistrationAccessToken!, ct);
        return ApplyManagementResponse(registration, response);
    }

    /// <summary>
    /// Replace a client configuration with an RFC 7592 PUT request. The caller must explicitly supply
    /// the complete replacement metadata; omitted values deliberately remove prior values.
    /// </summary>
    public async Task<ClientRegistrationResponse> UpdateConfigurationAsync(
        ClientRegistrationResponse registration,
        ClientRegistrationMetadata replacementMetadata,
        CancellationToken ct = default)
    {
        ValidateManagementState(registration);
        ArgumentNullException.ThrowIfNull(replacementMetadata);
        ValidateReplacementMetadata(replacementMetadata);

        var registrationUri = ValidateHttpsUri(registration.RegistrationClientUri!, nameof(registration));
        using var request = new HttpRequestMessage(HttpMethod.Put, registrationUri)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(CreateUpdatePayload(registration.ClientId, replacementMetadata)),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", registration.RegistrationAccessToken);

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return ApplyManagementResponse(registration, DeserializeRegistrationResponse(await response.Content.ReadAsStringAsync(ct)));
    }

    /// <summary>Delete a client registration (RFC 7592).</summary>
    public async Task DeleteAsync(
        string registrationClientUri,
        string registrationAccessToken,
        CancellationToken ct = default)
    {
        var registrationUri = ValidateHttpsUri(registrationClientUri, nameof(registrationClientUri));
        ValidateRegistrationAccessToken(registrationAccessToken, nameof(registrationAccessToken));
        using var request = new HttpRequestMessage(HttpMethod.Delete, registrationUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", registrationAccessToken);

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Delete a client registration using its current registration access token.</summary>
    public Task DeleteAsync(ClientRegistrationResponse registration, CancellationToken ct = default)
    {
        ValidateManagementState(registration);
        return DeleteAsync(registration.RegistrationClientUri!, registration.RegistrationAccessToken!, ct);
    }

    private static ClientRegistrationResponse ApplyManagementResponse(
        ClientRegistrationResponse current,
        ClientRegistrationResponse response)
    {
        if (!string.Equals(current.ClientId, response.ClientId, StringComparison.Ordinal))
            throw new InvalidOperationException("The registration management response changed client_id.");

        if (!string.IsNullOrWhiteSpace(response.RegistrationClientUri))
            _ = ValidateHttpsUri(response.RegistrationClientUri, nameof(response));

        return response with
        {
            ClientSecret = string.IsNullOrWhiteSpace(response.ClientSecret) ? current.ClientSecret : response.ClientSecret,
            RegistrationAccessToken = string.IsNullOrWhiteSpace(response.RegistrationAccessToken)
                ? current.RegistrationAccessToken
                : response.RegistrationAccessToken,
            RegistrationClientUri = string.IsNullOrWhiteSpace(response.RegistrationClientUri)
                ? current.RegistrationClientUri
                : response.RegistrationClientUri
        };
    }

    private static Dictionary<string, object> CreateUpdatePayload(
        string clientId,
        ClientRegistrationMetadata metadata)
    {
        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["client_id"] = clientId
        };

        Add(payload, "redirect_uris", metadata.RedirectUris);
        Add(payload, "grant_types", metadata.GrantTypes);
        Add(payload, "response_types", metadata.ResponseTypes);
        Add(payload, "application_type", metadata.ApplicationType);
        Add(payload, "token_endpoint_auth_method", metadata.TokenEndpointAuthMethod);
        Add(payload, "scope", metadata.Scope);
        Add(payload, "client_name", metadata.ClientName);
        Add(payload, "client_uri", metadata.ClientUri);
        Add(payload, "logo_uri", metadata.LogoUri);
        Add(payload, "contacts", metadata.Contacts);
        Add(payload, "tos_uri", metadata.TosUri);
        Add(payload, "policy_uri", metadata.PolicyUri);
        Add(payload, "jwks_uri", metadata.JwksUri);
        Add(payload, "jwks", metadata.Jwks);
        Add(payload, "software_id", metadata.SoftwareId);
        Add(payload, "software_version", metadata.SoftwareVersion);

        if (metadata.AdditionalMetadata is not null)
        {
            foreach (var (name, value) in metadata.AdditionalMetadata)
                payload.Add(name, value);
        }

        return payload;
    }

    private static void Add(Dictionary<string, object> payload, string name, object? value)
    {
        if (value is not null)
            payload.Add(name, value);
    }

    private static void ValidateReplacementMetadata(ClientRegistrationMetadata metadata)
    {
        var usesRedirectFlow = Contains(metadata.GrantTypes, "authorization_code") ||
            Contains(metadata.ResponseTypes, "code");
        if (usesRedirectFlow && (metadata.RedirectUris is null || metadata.RedirectUris.Count == 0))
            throw new ArgumentException("Replacement metadata for a redirect flow must include redirect_uris.", nameof(metadata));

        if (metadata.AdditionalMetadata is null)
            return;

        foreach (var name in metadata.AdditionalMetadata.Keys)
        {
            if (ReservedUpdateFields.Contains(name))
                throw new ArgumentException("Replacement metadata cannot include registration management fields.", nameof(metadata));
        }
    }

    private static bool Contains(IReadOnlyList<string>? values, string value) =>
        values?.Any(candidate => string.Equals(candidate, value, StringComparison.Ordinal)) == true;

    private static void ValidateManagementState(ClientRegistrationResponse registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (string.IsNullOrWhiteSpace(registration.ClientId) ||
            string.IsNullOrWhiteSpace(registration.RegistrationClientUri) ||
            string.IsNullOrWhiteSpace(registration.RegistrationAccessToken))
        {
            throw new ArgumentException("The registration must include a client ID, configuration endpoint, and registration access token.", nameof(registration));
        }

        _ = ValidateHttpsUri(registration.RegistrationClientUri, nameof(registration));
    }

    private static void ValidateRegistrationAccessToken(string registrationAccessToken, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(registrationAccessToken))
            throw new ArgumentException("A registration access token is required.", parameterName);
    }

    private static Uri ValidateHttpsUri(string value, string parameterName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("The registration endpoint must be an absolute HTTPS URI without a fragment or user info.", parameterName);
        }

        return uri;
    }

    private static ClientRegistrationResponse DeserializeRegistrationResponse(string json)
    {
        try
        {
            var registration = JsonSerializer.Deserialize<ClientRegistrationResponse>(json);
            if (registration is null || string.IsNullOrWhiteSpace(registration.ClientId))
                throw new InvalidOperationException("The registration response did not contain a client_id.");
            return registration;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Failed to parse registration response.", ex);
        }
    }
}
