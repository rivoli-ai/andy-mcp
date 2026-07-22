using System.Text.Json.Serialization;

namespace Andy.MCP.Auth;

/// <summary>
/// Protected Resource Metadata per RFC 9728.
/// Fetched from /.well-known/oauth-protected-resource on the MCP server.
/// </summary>
public sealed record ProtectedResourceMetadata
{
    [JsonPropertyName("resource")]
    public string? Resource { get; init; }

    [JsonPropertyName("authorization_servers")]
    public required IReadOnlyList<string> AuthorizationServers { get; init; }

    [JsonPropertyName("scopes_supported")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ScopesSupported { get; init; }
}

/// <summary>
/// Authorization Server Metadata per RFC 8414.
/// Fetched from /.well-known/oauth-authorization-server on the auth server.
/// </summary>
public sealed record AuthorizationServerMetadata
{
    [JsonPropertyName("issuer")]
    public required string Issuer { get; init; }

    [JsonPropertyName("authorization_endpoint")]
    public required string AuthorizationEndpoint { get; init; }

    [JsonPropertyName("token_endpoint")]
    public required string TokenEndpoint { get; init; }

    [JsonPropertyName("registration_endpoint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RegistrationEndpoint { get; init; }

    [JsonPropertyName("scopes_supported")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ScopesSupported { get; init; }

    [JsonPropertyName("response_types_supported")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ResponseTypesSupported { get; init; }

    [JsonPropertyName("grant_types_supported")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? GrantTypesSupported { get; init; }

    [JsonPropertyName("code_challenge_methods_supported")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? CodeChallengeMethodsSupported { get; init; }
}

/// <summary>
/// OAuth token response from the token endpoint.
/// </summary>
public sealed record OAuthTokenResponse
{
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("token_type")]
    public required string TokenType { get; init; }

    [JsonPropertyName("expires_in")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExpiresIn { get; init; }

    [JsonPropertyName("refresh_token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scope { get; init; }
}

/// <summary>
/// Stored OAuth tokens for a server.
/// </summary>
public sealed record OAuthTokens
{
    public required string AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? Scope { get; init; }

    /// <summary>Clock-skew margin: a token is treated as expired shortly before its actual expiry.</summary>
    public static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(30);

    public bool IsExpired => ExpiresAt.HasValue && DateTimeOffset.UtcNow >= ExpiresAt.Value - ExpirySkew;
}
