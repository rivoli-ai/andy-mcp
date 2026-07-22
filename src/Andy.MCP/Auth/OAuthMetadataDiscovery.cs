using System.Text.Json;

namespace Andy.MCP.Auth;

/// <summary>
/// MCP 2025-11-25 OAuth metadata discovery: protected-resource metadata (RFC 9728) and
/// authorization-server metadata (RFC 8414 and OpenID Connect Discovery), with the specified
/// well-known fallback order, path-aware issuers, issuer-identity validation, and SSRF-guarded
/// fetches.
/// </summary>
public sealed class OAuthMetadataDiscovery
{
    private readonly HttpClient _httpClient;
    private readonly bool _requireHttps;

    public OAuthMetadataDiscovery(HttpClient? httpClient = null, bool requireHttps = true)
    {
        _httpClient = httpClient ?? new HttpClient();
        _requireHttps = requireHttps;
    }

    // ---- Well-known URL construction (pure, in fallback order) ----

    /// <summary>
    /// Candidate protected-resource-metadata URLs for a resource, in RFC 9728 fallback order:
    /// the path-aware location first (when the resource has a path), then the root well-known.
    /// </summary>
    public static IReadOnlyList<Uri> ProtectedResourceMetadataUrls(Uri resource)
    {
        var path = resource.AbsolutePath.Trim('/');
        var urls = new List<Uri>();
        if (path.Length > 0)
            urls.Add(new Uri($"{resource.Scheme}://{resource.Authority}/.well-known/oauth-protected-resource/{path}"));
        urls.Add(new Uri($"{resource.Scheme}://{resource.Authority}/.well-known/oauth-protected-resource"));
        return urls;
    }

    /// <summary>
    /// Candidate authorization-server-metadata URLs for an issuer, in fallback order: RFC 8414
    /// (well-known inserted before any issuer path), then OpenID Connect Discovery (well-known
    /// appended after the issuer path), then the root variants of each.
    /// </summary>
    public static IReadOnlyList<Uri> AuthorizationServerMetadataUrls(Uri issuer)
    {
        var prefix = $"{issuer.Scheme}://{issuer.Authority}";
        var path = issuer.AbsolutePath.Trim('/');
        var urls = new List<Uri>();
        if (path.Length > 0)
        {
            urls.Add(new Uri($"{prefix}/.well-known/oauth-authorization-server/{path}"));
            urls.Add(new Uri($"{prefix}/{path}/.well-known/openid-configuration"));
        }
        urls.Add(new Uri($"{prefix}/.well-known/oauth-authorization-server"));
        urls.Add(new Uri($"{prefix}/.well-known/openid-configuration"));
        return urls;
    }

    // ---- Discovery ----

    public async Task<ProtectedResourceMetadata> DiscoverProtectedResourceMetadataAsync(
        Uri resource, CancellationToken ct = default)
    {
        foreach (var url in ProtectedResourceMetadataUrls(resource))
        {
            var metadata = await TryFetchAsync<ProtectedResourceMetadata>(url, ct);
            if (metadata is not null && metadata.AuthorizationServers.Count > 0)
                return metadata;
        }
        throw new InvalidOperationException($"No protected resource metadata found for '{resource}'.");
    }

    /// <summary>
    /// Fetch protected-resource metadata from a URL given directly (e.g. the <c>resource_metadata</c>
    /// parameter of a WWW-Authenticate challenge), rather than from well-known construction.
    /// </summary>
    public async Task<ProtectedResourceMetadata> FetchProtectedResourceMetadataAsync(
        Uri metadataUrl, CancellationToken ct = default)
    {
        var metadata = await TryFetchAsync<ProtectedResourceMetadata>(metadataUrl, ct);
        if (metadata is null || metadata.AuthorizationServers.Count == 0)
            throw new InvalidOperationException($"No usable protected resource metadata at '{metadataUrl}'.");
        return metadata;
    }

    public async Task<AuthorizationServerMetadata> DiscoverAuthorizationServerMetadataAsync(
        Uri issuer, CancellationToken ct = default)
    {
        foreach (var url in AuthorizationServerMetadataUrls(issuer))
        {
            var metadata = await TryFetchAsync<AuthorizationServerMetadata>(url, ct);
            if (metadata is not null)
            {
                ValidateAuthorizationServerMetadata(metadata, issuer);
                return metadata;
            }
        }
        throw new InvalidOperationException($"No authorization server metadata found for '{issuer}'.");
    }

    /// <summary>
    /// Validate authorization-server metadata: the advertised issuer must match the issuer used for
    /// discovery, the endpoints must be present and HTTPS, and PKCE S256 must be supported.
    /// </summary>
    public void ValidateAuthorizationServerMetadata(AuthorizationServerMetadata metadata, Uri expectedIssuer)
    {
        if (!IssuerMatches(metadata.Issuer, expectedIssuer))
            throw new InvalidOperationException(
                $"Authorization server issuer '{metadata.Issuer}' does not match '{expectedIssuer}'.");

        RequireSecureEndpoint(metadata.AuthorizationEndpoint, "authorization_endpoint");
        RequireSecureEndpoint(metadata.TokenEndpoint, "token_endpoint");

        var methods = metadata.CodeChallengeMethodsSupported;
        if (methods is not null && methods.Count > 0 && !methods.Contains("S256"))
            throw new InvalidOperationException("Authorization server does not support PKCE S256.");
    }

    private async Task<T?> TryFetchAsync<T>(Uri url, CancellationToken ct) where T : class
    {
        // SSRF guard: reject non-HTTPS and literal private/reserved hosts before any request.
        var validation = SecurityHelpers.ValidateUrl(url.ToString(), _requireHttps);
        if (!validation.IsValid)
            throw new InvalidOperationException($"Refusing to fetch metadata from '{url}': {validation.Error}");

        try
        {
            using var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return null; // try the next candidate

            var json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (HttpRequestException)
        {
            return null; // try the next candidate
        }
    }

    private void RequireSecureEndpoint(string endpoint, string name)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"{name} is not an absolute URI: '{endpoint}'.");
        if (_requireHttps && uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
            throw new InvalidOperationException($"{name} must use HTTPS: '{endpoint}'.");
    }

    private static bool IssuerMatches(string metadataIssuer, Uri expectedIssuer)
    {
        if (!Uri.TryCreate(metadataIssuer, UriKind.Absolute, out var actual))
            return false;
        // Compare scheme/host/port/path, ignoring a trailing slash.
        static string Normalize(Uri u) => $"{u.Scheme}://{u.Authority}{u.AbsolutePath.TrimEnd('/')}";
        return string.Equals(Normalize(actual), Normalize(expectedIssuer), StringComparison.Ordinal);
    }
}
