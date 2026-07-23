using System.Text.Json.Serialization;

namespace Andy.MCP.Auth;

/// <summary>
/// Host-published OAuth Client ID Metadata Document. The core library validates supplied metadata
/// but deliberately does not host or fetch it.
/// </summary>
public sealed record ClientIdMetadataDocument
{
    [JsonPropertyName("client_id")]
    public required string ClientId { get; init; }

    [JsonPropertyName("client_name")]
    public required string ClientName { get; init; }

    [JsonPropertyName("redirect_uris")]
    public required IReadOnlyList<string> RedirectUris { get; init; }

    [JsonPropertyName("grant_types")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? GrantTypes { get; init; }

    [JsonPropertyName("response_types")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ResponseTypes { get; init; }

    [JsonPropertyName("token_endpoint_auth_method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TokenEndpointAuthMethod { get; init; }

    [JsonPropertyName("client_uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientUri { get; init; }

    [JsonPropertyName("logo_uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogoUri { get; init; }

    /// <summary>Public JWK Set URL for asymmetric client authentication.</summary>
    [JsonPropertyName("jwks_uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JwksUri { get; init; }

    /// <summary>Validates the host-supplied document before using its URL as an OAuth client ID.</summary>
    public void Validate(string documentUri)
    {
        ValidateClientIdentifierUrl(documentUri, nameof(documentUri));
        ValidateClientIdentifierUrl(ClientId, nameof(ClientId));
        if (!string.Equals(documentUri, ClientId, StringComparison.Ordinal))
            throw new ArgumentException("The client_id must exactly match the metadata document URL.", nameof(documentUri));
        if (string.IsNullOrWhiteSpace(ClientName))
            throw new ArgumentException("The metadata document must include client_name.", nameof(ClientName));
        if (RedirectUris is null || RedirectUris.Count == 0)
            throw new ArgumentException("The metadata document must include at least one redirect_uri.", nameof(RedirectUris));

        foreach (var redirectUri in RedirectUris)
            ValidateRedirectUri(redirectUri, nameof(RedirectUris));

        if (IsSharedSecretAuthenticationMethod(TokenEndpointAuthMethod))
        {
            throw new ArgumentException(
                "Client ID Metadata Documents must not use shared-secret token endpoint authentication.",
                nameof(TokenEndpointAuthMethod));
        }

        if (ClientUri is not null)
            ValidateHttpsWebUrl(ClientUri, nameof(ClientUri));
        if (LogoUri is not null)
            ValidateHttpsWebUrl(LogoUri, nameof(LogoUri));
        if (JwksUri is not null)
            ValidateHttpsWebUrl(JwksUri, nameof(JwksUri));
    }

    private static bool IsSharedSecretAuthenticationMethod(string? method) =>
        method?.StartsWith("client_secret", StringComparison.Ordinal) == true;

    private static void ValidateClientIdentifierUrl(string value, string parameterName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            RawPath(value) is not { Length: > 1 } path ||
            HasDotSegment(path))
        {
            throw new ArgumentException(
                "A Client Identifier URL must be HTTPS, have a non-root path, and contain no user info, fragment, or dot segments.",
                parameterName);
        }
    }

    private static void ValidateHttpsWebUrl(string value, string parameterName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("A fragment-free HTTPS URL without user info is required.", parameterName);
        }
    }

    private static void ValidateRedirectUri(string value, string parameterName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && !uri.IsLoopback))
        {
            throw new ArgumentException("A fragment-free HTTPS or loopback redirect URI is required.", parameterName);
        }
    }

    private static string RawPath(string value)
    {
        var authorityStart = value.IndexOf("://", StringComparison.Ordinal);
        if (authorityStart < 0)
            return string.Empty;

        var pathStart = value.IndexOf('/', authorityStart + 3);
        if (pathStart < 0)
            return string.Empty;

        var pathEnd = value.IndexOfAny(['?', '#'], pathStart);
        return pathEnd < 0 ? value[pathStart..] : value[pathStart..pathEnd];
    }

    private static bool HasDotSegment(string path) =>
        path.Split('/', StringSplitOptions.None).Any(segment => segment is "." or "..");
}

/// <summary>Host-provided registration choices. CIMD is preferred when the server advertises it.</summary>
public sealed record OAuthClientRegistrationOptions
{
    public string? ClientIdMetadataDocumentUri { get; init; }
    public ClientIdMetadataDocument? ClientIdMetadataDocument { get; init; }
    public ClientRegistrationRequest? DynamicClientRegistration { get; init; }
    public string? InitialAccessToken { get; init; }
}

public enum OAuthClientRegistrationMethod
{
    ClientIdMetadataDocument,
    DynamicClientRegistration
}

/// <summary>Selected client identity, with DCR state kept separate from CIMD identity.</summary>
public sealed record OAuthClientRegistration
{
    public required string ClientId { get; init; }
    public required OAuthClientRegistrationMethod Method { get; init; }
    public ClientRegistrationResponse? DynamicRegistration { get; init; }
}

/// <summary>Deterministically selects CIMD or explicit RFC 7591 registration fallback.</summary>
public sealed class OAuthClientRegistrationResolver
{
    private readonly DynamicClientRegistrationClient _dynamicClientRegistration;

    public OAuthClientRegistrationResolver(DynamicClientRegistrationClient? dynamicClientRegistration = null)
    {
        _dynamicClientRegistration = dynamicClientRegistration ?? new DynamicClientRegistrationClient();
    }

    public async Task<OAuthClientRegistration> ResolveAsync(
        AuthorizationServerMetadata metadata,
        OAuthClientRegistrationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(options);

        var hasCimd = options.ClientIdMetadataDocumentUri is not null || options.ClientIdMetadataDocument is not null;
        if (hasCimd)
        {
            if (options.ClientIdMetadataDocumentUri is null || options.ClientIdMetadataDocument is null)
                throw new ArgumentException("Both the Client ID Metadata Document URL and document are required.", nameof(options));

            options.ClientIdMetadataDocument.Validate(options.ClientIdMetadataDocumentUri);
            if (metadata.ClientIdMetadataDocumentSupported)
            {
                return new OAuthClientRegistration
                {
                    ClientId = options.ClientIdMetadataDocumentUri,
                    Method = OAuthClientRegistrationMethod.ClientIdMetadataDocument
                };
            }
        }

        if (string.IsNullOrWhiteSpace(metadata.RegistrationEndpoint) || options.DynamicClientRegistration is null)
        {
            throw new InvalidOperationException(
                "The authorization server cannot use the configured Client ID Metadata Document and no explicit Dynamic Client Registration fallback is available.");
        }

        var registration = await _dynamicClientRegistration.RegisterAsync(
            metadata.RegistrationEndpoint,
            options.DynamicClientRegistration,
            options.InitialAccessToken,
            cancellationToken);
        return new OAuthClientRegistration
        {
            ClientId = registration.ClientId,
            Method = OAuthClientRegistrationMethod.DynamicClientRegistration,
            DynamicRegistration = registration
        };
    }
}
