using System.Net;
using System.Net.Http.Headers;

namespace Andy.MCP.Auth;

/// <summary>
/// HttpClient delegating handler that injects Bearer token and handles 401 with auto-refresh.
/// </summary>
public sealed class OAuthDelegatingHandler : DelegatingHandler
{
    private readonly OAuthClient _oauthClient;
    private readonly string _resource;
    private readonly AuthorizationServerMetadata? _authMetadata;
    private readonly string? _clientId;
    private readonly OAuthMetadataDiscovery? _discovery;
    private AuthorizationServerMetadata? _discoveredMetadata;

    public OAuthDelegatingHandler(
        OAuthClient oauthClient,
        string resource,
        AuthorizationServerMetadata? authMetadata = null,
        string? clientId = null,
        HttpMessageHandler? innerHandler = null,
        OAuthMetadataDiscovery? discovery = null)
        : base(innerHandler ?? new HttpClientHandler())
    {
        _oauthClient = oauthClient;
        _resource = resource;
        _authMetadata = authMetadata;
        _clientId = clientId;
        _discovery = discovery;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
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

            if (newToken is not null && newToken != token)
            {
                var retryRequest = await CloneRequestAsync(request);
                retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
                response.Dispose();
                return await base.SendAsync(retryRequest, cancellationToken);
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
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);

        if (original.Content is not null)
        {
            var content = await original.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(content);
            if (original.Content.Headers.ContentType is not null)
                clone.Content.Headers.ContentType = original.Content.Headers.ContentType;
        }

        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        return clone;
    }
}
