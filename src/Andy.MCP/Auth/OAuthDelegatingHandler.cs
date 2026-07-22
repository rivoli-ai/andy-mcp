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

    public OAuthDelegatingHandler(
        OAuthClient oauthClient,
        string resource,
        AuthorizationServerMetadata? authMetadata = null,
        string? clientId = null,
        HttpMessageHandler? innerHandler = null)
        : base(innerHandler ?? new HttpClientHandler())
    {
        _oauthClient = oauthClient;
        _resource = resource;
        _authMetadata = authMetadata;
        _clientId = clientId;
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
            WwwAuthenticateChallenge.TryParse(
                response.Headers.WwwAuthenticate.FirstOrDefault()?.ToString(), out _);

            var newToken = await _oauthClient.HandleUnauthorizedAsync(
                _resource, _authMetadata, _clientId, cancellationToken);

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
