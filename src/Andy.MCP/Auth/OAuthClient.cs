using System.Net.Http.Headers;
using System.Text.Json;
using System.Web;

namespace Andy.MCP.Auth;

/// <summary>
/// OAuth 2.1 client for MCP authorization.
/// Handles discovery (RFC 8414, RFC 9728), PKCE, token exchange, and refresh.
/// </summary>
public sealed class OAuthClient
{
    private readonly HttpClient _httpClient;
    private readonly ITokenStore _tokenStore;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public OAuthClient(HttpClient? httpClient = null, ITokenStore? tokenStore = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _tokenStore = tokenStore ?? new InMemoryTokenStore();
    }

    /// <summary>
    /// Discover Protected Resource Metadata from the MCP server (RFC 9728).
    /// </summary>
    public async Task<ProtectedResourceMetadata> DiscoverResourceMetadataAsync(
        Uri serverUri, CancellationToken ct = default)
    {
        var wellKnownUrl = new Uri(serverUri, "/.well-known/oauth-protected-resource");
        var json = await _httpClient.GetStringAsync(wellKnownUrl, ct);
        return JsonSerializer.Deserialize<ProtectedResourceMetadata>(json)
            ?? throw new InvalidOperationException("Failed to parse Protected Resource Metadata.");
    }

    /// <summary>
    /// Discover Authorization Server Metadata (RFC 8414).
    /// </summary>
    public async Task<AuthorizationServerMetadata> DiscoverAuthServerMetadataAsync(
        string authServerUrl, CancellationToken ct = default)
    {
        var uri = new Uri(authServerUrl.TrimEnd('/') + "/.well-known/oauth-authorization-server");
        var json = await _httpClient.GetStringAsync(uri, ct);
        return JsonSerializer.Deserialize<AuthorizationServerMetadata>(json)
            ?? throw new InvalidOperationException("Failed to parse Authorization Server Metadata.");
    }

    /// <summary>
    /// Build the authorization URL for the OAuth code flow.
    /// </summary>
    public static string BuildAuthorizationUrl(
        AuthorizationServerMetadata metadata,
        string clientId,
        string redirectUri,
        string codeChallenge,
        string state,
        string resource,
        string? scope = null)
    {
        ValidateResourceParameter(resource);

        var query = HttpUtility.ParseQueryString(string.Empty);
        query["response_type"] = "code";
        query["client_id"] = clientId;
        query["redirect_uri"] = redirectUri;
        query["code_challenge"] = codeChallenge;
        query["code_challenge_method"] = "S256";
        query["state"] = state;
        query["resource"] = resource;
        if (scope is not null) query["scope"] = scope;

        return $"{metadata.AuthorizationEndpoint}?{query}";
    }

    /// <summary>
    /// Exchange an authorization code for tokens.
    /// </summary>
    public async Task<OAuthTokens> ExchangeCodeAsync(
        AuthorizationServerMetadata metadata,
        string code,
        string codeVerifier,
        string clientId,
        string redirectUri,
        string resource,
        CancellationToken ct = default)
    {
        ValidateResourceParameter(resource);

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["resource"] = resource
        });

        var response = await _httpClient.PostAsync(metadata.TokenEndpoint, content, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var tokenResponse = JsonSerializer.Deserialize<OAuthTokenResponse>(json)!;

        var tokens = new OAuthTokens
        {
            AccessToken = tokenResponse.AccessToken,
            RefreshToken = tokenResponse.RefreshToken,
            ExpiresAt = tokenResponse.ExpiresIn.HasValue
                ? DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn.Value)
                : null,
            Scope = tokenResponse.Scope
        };

        await _tokenStore.SaveTokensAsync(resource, tokens);
        return tokens;
    }

    /// <summary>
    /// Refresh an access token using a refresh token.
    /// </summary>
    public async Task<OAuthTokens> RefreshTokenAsync(
        AuthorizationServerMetadata metadata,
        string refreshToken,
        string clientId,
        string resource,
        CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            // Re-read token state inside the lock: a concurrent request may already have rotated the
            // one-time refresh token, in which case reuse the freshly-stored token instead of
            // attempting a second refresh that would fail against a rotated token.
            var current = await _tokenStore.LoadTokensAsync(resource);
            if (current is not null && !current.IsExpired && current.RefreshToken != refreshToken)
                return current;

            var effectiveRefreshToken = current?.RefreshToken ?? refreshToken;

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = effectiveRefreshToken,
                ["client_id"] = clientId,
                ["resource"] = resource
            });

            var response = await _httpClient.PostAsync(metadata.TokenEndpoint, content, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var tokenResponse = JsonSerializer.Deserialize<OAuthTokenResponse>(json)!;

            var tokens = new OAuthTokens
            {
                AccessToken = tokenResponse.AccessToken,
                RefreshToken = tokenResponse.RefreshToken ?? effectiveRefreshToken, // Rotation: use new if provided
                ExpiresAt = tokenResponse.ExpiresIn.HasValue
                    ? DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn.Value)
                    : null,
                Scope = tokenResponse.Scope
            };

            await _tokenStore.SaveTokensAsync(resource, tokens);
            return tokens;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Get a valid access token, refreshing if expired.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(
        string resource,
        AuthorizationServerMetadata? metadata = null,
        string? clientId = null,
        CancellationToken ct = default)
    {
        var tokens = await _tokenStore.LoadTokensAsync(resource);
        if (tokens is null) return null;

        if (!tokens.IsExpired)
            return tokens.AccessToken;

        // Try refresh
        if (tokens.RefreshToken is not null && metadata is not null && clientId is not null)
        {
            var refreshed = await RefreshTokenAsync(metadata, tokens.RefreshToken, clientId, resource, ct);
            return refreshed.AccessToken;
        }

        return null; // Expired and can't refresh
    }

    /// <summary>
    /// React to a 401 for a token that the server rejected: attempt a refresh to obtain a genuinely
    /// new token, or clear the rejected token so it is not reused. Returns the new access token, or
    /// null when no fresh token could be obtained (the caller should surface the challenge).
    /// </summary>
    public async Task<string?> HandleUnauthorizedAsync(
        string resource,
        AuthorizationServerMetadata? metadata = null,
        string? clientId = null,
        CancellationToken ct = default)
    {
        var tokens = await _tokenStore.LoadTokensAsync(resource);
        if (tokens is null)
            return null;

        if (tokens.RefreshToken is not null && metadata is not null && clientId is not null)
        {
            try
            {
                var refreshed = await RefreshTokenAsync(metadata, tokens.RefreshToken, clientId, resource, ct);
                return refreshed.AccessToken;
            }
            catch
            {
                // Refresh failed; fall through to invalidate the rejected token.
            }
        }

        // No refresh available or it failed: discard the rejected token rather than reusing it.
        await _tokenStore.ClearTokensAsync(resource);
        return null;
    }

    /// <summary>
    /// Validate the resource parameter per RFC 8707.
    /// Must have a scheme and must not contain a fragment.
    /// </summary>
    public static void ValidateResourceParameter(string resource)
    {
        if (!Uri.TryCreate(resource, UriKind.Absolute, out var uri))
            throw new ArgumentException($"Resource must be an absolute URI: '{resource}'", nameof(resource));

        if (!string.IsNullOrEmpty(uri.Fragment) && uri.Fragment != "#")
            throw new ArgumentException($"Resource URI must not contain a fragment: '{resource}'", nameof(resource));
    }
}
