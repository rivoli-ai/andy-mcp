using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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
    private readonly Func<string> _stateGenerator;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _scopeStepUpLocks = new(StringComparer.Ordinal);

    public OAuthClient(HttpClient? httpClient = null, ITokenStore? tokenStore = null, Func<string>? stateGenerator = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _tokenStore = tokenStore ?? new InMemoryTokenStore();
        _stateGenerator = stateGenerator ?? PkceHelper.GenerateState;
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
        var tokens = await ExchangeAuthorizationCodeAsync(
            metadata, code, codeVerifier, clientId, redirectUri, resource, requestedScopes: null, ct);
        await _tokenStore.SaveTokensAsync(resource, tokens);
        return tokens;
    }

    /// <summary>
    /// Run authorization-code interaction while retaining PKCE, state, callback validation, code
    /// exchange, and token persistence inside the library.
    /// </summary>
    public async Task<OAuthTokens?> AuthorizeInteractiveAsync(
        AuthorizationServerMetadata metadata,
        string clientId,
        string redirectUri,
        string resource,
        IReadOnlyList<string> requestedScopes,
        IOAuthAuthorizationProvider provider,
        CancellationToken ct = default)
    {
        var tokens = await AcquireInteractiveTokensAsync(
            metadata, clientId, redirectUri, resource, requestedScopes, provider, ct);
        if (tokens is not null)
            await _tokenStore.SaveTokensAsync(resource, tokens);
        return tokens;
    }

    /// <summary>
    /// Performs one per-resource interactive authorization upgrade for a qualifying insufficient-scope
    /// challenge. A token is persisted only after it is proven new and covers the complete scope union.
    /// </summary>
    public async Task<string?> HandleInsufficientScopeAsync(
        string resource,
        string rejectedToken,
        AuthorizationServerMetadata metadata,
        string clientId,
        string redirectUri,
        IReadOnlyList<string> challengedScopes,
        IOAuthAuthorizationProvider provider,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rejectedToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        var gate = _scopeStepUpLocks.GetOrAdd(resource, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var current = await _tokenStore.LoadTokensAsync(resource);
            var requiredScopes = CreateScopeUnion(current?.Scope, challengedScopes);
            if (current is not null && !string.Equals(current.AccessToken, rejectedToken, StringComparison.Ordinal) &&
                TokenCoversScopes(current, requiredScopes))
            {
                return current.AccessToken;
            }

            var candidate = await AcquireInteractiveTokensAsync(
                metadata, clientId, redirectUri, resource, requiredScopes, provider, ct);
            if (candidate is null || string.Equals(candidate.AccessToken, rejectedToken, StringComparison.Ordinal) ||
                !TokenCoversScopes(candidate, requiredScopes))
            {
                return null;
            }

            await _tokenStore.SaveTokensAsync(resource, candidate);
            return candidate.AccessToken;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            gate.Release();
            if (gate.CurrentCount == 1)
                _scopeStepUpLocks.TryRemove(new KeyValuePair<string, SemaphoreSlim>(resource, gate));
        }
    }

    private async Task<OAuthTokens?> AcquireInteractiveTokensAsync(
        AuthorizationServerMetadata metadata,
        string clientId,
        string redirectUri,
        string resource,
        IReadOnlyList<string> requestedScopes,
        IOAuthAuthorizationProvider provider,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ValidateInteractiveInputs(metadata, redirectUri, requestedScopes);
        var (verifier, challenge) = PkceHelper.Generate();
        var state = _stateGenerator();
        if (string.IsNullOrWhiteSpace(state))
            throw new InvalidOperationException("Authorization state generation failed.");

        var authorizationUrl = BuildAuthorizationUrl(
            metadata, clientId, redirectUri, challenge, state, resource, string.Join(' ', requestedScopes));
        var callback = await provider.AuthorizeAsync(new OAuthAuthorizationInteraction
        {
            AuthorizationUri = new Uri(authorizationUrl),
            RedirectUri = new Uri(redirectUri)
        }, ct);
        if (callback is null || !TryValidateCallback(callback.CallbackUri, redirectUri, state, out var code))
            return null;

        return await ExchangeAuthorizationCodeAsync(
            metadata, code, verifier, clientId, redirectUri, resource, requestedScopes, ct);
    }

    /// <summary>Create the exact ordinal, case-sensitive union for an insufficient-scope upgrade.</summary>
    public static IReadOnlyList<string> CreateScopeUnion(string? existingScope, IReadOnlyList<string> challengedScopes)
    {
        if (challengedScopes is null || challengedScopes.Count == 0)
            throw new ArgumentException("At least one challenged scope is required.", nameof(challengedScopes));

        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var scope in (existingScope ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            AddScope(result, scope);
        foreach (var scope in challengedScopes)
            AddScope(result, scope);
        return result.ToArray();
    }

    private static void ValidateInteractiveInputs(
        AuthorizationServerMetadata metadata, string redirectUri, IReadOnlyList<string> requestedScopes)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.CodeChallengeMethodsSupported is null ||
            !metadata.CodeChallengeMethodsSupported.Contains("S256", StringComparer.Ordinal))
            throw new InvalidOperationException("Authorization server does not support PKCE S256.");
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo) || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
            throw new ArgumentException("A fragment-free HTTPS or loopback redirect URI is required.", nameof(redirectUri));
        if (requestedScopes is null || requestedScopes.Count == 0)
            throw new ArgumentException("At least one requested scope is required.", nameof(requestedScopes));
        foreach (var scope in requestedScopes)
            AddScope(new SortedSet<string>(StringComparer.Ordinal), scope);
    }

    private static void AddScope(ISet<string> scopes, string scope)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope.Any(char.IsWhiteSpace) || scope.Any(c => c is '"' or '\\' || c < 0x21 || c > 0x7e))
            throw new ArgumentException("Scopes must be non-empty printable ASCII tokens.", nameof(scope));
        scopes.Add(scope);
    }

    private static bool TryValidateCallback(Uri callback, string redirectUri, string state, out string code)
    {
        code = string.Empty;
        var expected = new Uri(redirectUri);
        if (!string.Equals(callback.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(callback.Host, expected.Host, StringComparison.OrdinalIgnoreCase) ||
            callback.Port != expected.Port ||
            !string.Equals(callback.AbsolutePath, expected.AbsolutePath, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(callback.Fragment) || !string.IsNullOrEmpty(callback.UserInfo))
            return false;
        var query = HttpUtility.ParseQueryString(callback.Query);
        if (!HasExpectedRedirectQuery(expected, query))
            return false;
        var callbackState = query.GetValues("state");
        var callbackCode = query.GetValues("code");
        if (!string.IsNullOrEmpty(query["error"]) || callbackState is not [var returnedState] ||
            callbackCode is not [var returnedCode] || string.IsNullOrWhiteSpace(returnedState) ||
            !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(state), System.Text.Encoding.UTF8.GetBytes(returnedState)) ||
            string.IsNullOrWhiteSpace(returnedCode))
            return false;
        code = returnedCode;
        return true;
    }

    private static bool HasExpectedRedirectQuery(Uri expected, System.Collections.Specialized.NameValueCollection callbackQuery)
    {
        var expectedQuery = HttpUtility.ParseQueryString(expected.Query);
        foreach (var name in expectedQuery.AllKeys)
        {
            if (name is null)
                return false;

            var expectedValues = expectedQuery.GetValues(name)!;
            var callbackValues = callbackQuery.GetValues(name);
            if (callbackValues is null || !expectedValues.OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(callbackValues.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<OAuthTokens> ExchangeAuthorizationCodeAsync(
        AuthorizationServerMetadata metadata,
        string code,
        string codeVerifier,
        string clientId,
        string redirectUri,
        string resource,
        IReadOnlyList<string>? requestedScopes,
        CancellationToken ct)
    {
        ValidateResourceParameter(resource);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["resource"] = resource
        });
        using var response = await _httpClient.PostAsync(metadata.TokenEndpoint, content, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var tokenResponse = JsonSerializer.Deserialize<OAuthTokenResponse>(json)
            ?? throw new InvalidOperationException("Failed to parse the token response.");
        if (string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            throw new InvalidOperationException("The token response did not contain an access token.");

        var scope = tokenResponse.Scope;
        if (requestedScopes is not null)
        {
            var effectiveScopes = scope is null
                ? requestedScopes
                : CreateScopeUnion(null, scope.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (!CoversScopes(effectiveScopes, requestedScopes))
                throw new InvalidOperationException("The token response did not grant the requested scopes.");
            scope = string.Join(' ', effectiveScopes);
        }

        return new OAuthTokens
        {
            AccessToken = tokenResponse.AccessToken,
            RefreshToken = tokenResponse.RefreshToken,
            ExpiresAt = tokenResponse.ExpiresIn.HasValue
                ? DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn.Value)
                : null,
            Scope = scope
        };
    }

    private static bool TokenCoversScopes(OAuthTokens tokens, IReadOnlyList<string> requiredScopes) =>
        tokens.Scope is not null && CoversScopes(
            CreateScopeUnion(null, tokens.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)), requiredScopes);

    private static bool CoversScopes(IReadOnlyList<string> grantedScopes, IReadOnlyList<string> requiredScopes)
    {
        var granted = new HashSet<string>(grantedScopes, StringComparer.Ordinal);
        return requiredScopes.All(granted.Contains);
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
