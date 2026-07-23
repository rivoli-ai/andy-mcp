using System.Net;
using System.Net.Http.Headers;

namespace Andy.MCP.Auth;

/// <summary>
/// HttpClient delegating handler that injects Bearer tokens, handles 401 refresh, and performs one
/// interactive authorization retry for a qualifying 403 insufficient-scope challenge.
/// </summary>
public sealed class OAuthDelegatingHandler : DelegatingHandler
{
    private readonly OAuthClient _oauthClient;
    private readonly string _resource;
    private readonly AuthorizationServerMetadata? _authMetadata;
    private readonly string? _clientId;
    private readonly OAuthMetadataDiscovery? _discovery;
    private readonly IOAuthAuthorizationProvider? _authorizationProvider;
    private readonly string? _authorizationRedirectUri;
    private AuthorizationServerMetadata? _discoveredMetadata;

    public OAuthDelegatingHandler(
        OAuthClient oauthClient,
        string resource,
        AuthorizationServerMetadata? authMetadata = null,
        string? clientId = null,
        HttpMessageHandler? innerHandler = null,
        OAuthMetadataDiscovery? discovery = null,
        IOAuthAuthorizationProvider? authorizationProvider = null,
        string? authorizationRedirectUri = null)
        : base(innerHandler ?? new HttpClientHandler())
    {
        _oauthClient = oauthClient;
        _resource = resource;
        _authMetadata = authMetadata;
        _clientId = clientId;
        _discovery = discovery;
        _authorizationProvider = authorizationProvider;
        _authorizationRedirectUri = authorizationRedirectUri;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var replayable = await TryBufferContentAsync(request, cancellationToken);

        // Inject token
        var token = await _oauthClient.GetAccessTokenAsync(
            _resource, _authMetadata, _clientId, cancellationToken);

        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await base.SendAsync(request, cancellationToken);

        // Handle 401: obtain a genuinely new token and retry once. Never blindly retry the same
        // token — if no fresh token can be obtained, surface the response (with its challenge).
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var metadata = await ResolveAuthMetadataAsync(response, cancellationToken);

            var newToken = await _oauthClient.HandleUnauthorizedAsync(
                _resource, metadata, _clientId, cancellationToken);

            if (replayable && newToken is not null && newToken != token)
            {
                var retryRequest = await CloneRequestAsync(request);
                retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
                response.Dispose();
                return await base.SendAsync(retryRequest, cancellationToken);
            }
        }

        // MCP scope step-up is intentionally narrower than generic 403 handling: only an exact
        // Bearer insufficient_scope challenge with one or more scopes may initiate interaction.
        if (replayable && response.StatusCode == HttpStatusCode.Forbidden && token is not null &&
            _authorizationProvider is not null && !string.IsNullOrWhiteSpace(_authorizationRedirectUri) &&
            WwwAuthenticateChallenge.TryParse(response.Headers.WwwAuthenticate.FirstOrDefault()?.ToString(), out var challenge) &&
            string.Equals(challenge.Error, "insufficient_scope", StringComparison.Ordinal) && challenge.Scopes.Count > 0)
        {
            var metadata = await ResolveAuthMetadataAsync(response, cancellationToken);
            if (metadata is not null && !string.IsNullOrWhiteSpace(_clientId))
            {
                var newToken = await _oauthClient.HandleInsufficientScopeAsync(
                    _resource, token, metadata, _clientId, _authorizationRedirectUri, challenge.Scopes,
                    _authorizationProvider, cancellationToken);
                if (newToken is not null && !string.Equals(newToken, token, StringComparison.Ordinal))
                {
                    var retryRequest = await CloneRequestAsync(request);
                    retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
                    response.Dispose();
                    return await base.SendAsync(retryRequest, cancellationToken);
                }
            }
        }

        return response;
    }

    /// <summary>
    /// Determine the authorization-server metadata to use for a 401: the pre-configured metadata,
    /// a previously discovered one, or — following the challenge's <c>resource_metadata</c> —
    /// protected-resource metadata (RFC 9728) then authorization-server metadata (RFC 8414/OIDC).
    /// </summary>
    private async Task<AuthorizationServerMetadata?> ResolveAuthMetadataAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (_authMetadata is not null)
            return _authMetadata;
        if (_discoveredMetadata is not null)
            return _discoveredMetadata;
        if (_discovery is null)
            return null;

        if (!WwwAuthenticateChallenge.TryParse(
                response.Headers.WwwAuthenticate.FirstOrDefault()?.ToString(), out var challenge) ||
            challenge.ResourceMetadata is null ||
            !Uri.TryCreate(challenge.ResourceMetadata, UriKind.Absolute, out var prmUrl))
        {
            return null;
        }

        try
        {
            var prm = await _discovery.FetchProtectedResourceMetadataAsync(prmUrl, cancellationToken);
            if (prm.AuthorizationServers.Count == 0 ||
                !Uri.TryCreate(prm.AuthorizationServers[0], UriKind.Absolute, out var issuer))
            {
                return null;
            }

            _discoveredMetadata = await _discovery.DiscoverAuthorizationServerMetadataAsync(issuer, cancellationToken);
            return _discoveredMetadata;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return null; // discovery failed; surface the original 401
        }
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy
        };

        if (original.Content is not null)
        {
            var content = await original.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(content);
            foreach (var header in original.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var header in original.Headers)
        {
            if (!string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in original.Options)
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);

        return clone;
    }

    private static async Task<bool> TryBufferContentAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is null)
            return true;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await request.Content.LoadIntoBufferAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return false;
        }
    }
}
