using Andy.MCP.Auth;

namespace Andy.MCP.AspNetCore;

/// <summary>Policy applied after ASP.NET Core validates token signatures and lifetimes.</summary>
public sealed record McpHttpAuthorizationOptions
{
    public required string Resource { get; init; }
    public required string Issuer { get; init; }
    public IReadOnlyList<string> RequiredScopes { get; init; } = [];
    public string MetadataPath => "/.well-known/oauth-protected-resource" + new Uri(Resource).AbsolutePath.TrimEnd('/');
    public string MetadataUrl => new Uri(new Uri(Resource), MetadataPath).AbsoluteUri;
    public ProtectedResourceMetadata Metadata => new()
    {
        Resource = Resource, AuthorizationServers = [Issuer], ScopesSupported = RequiredScopes
    };

    internal void Validate()
    {
        foreach (var endpoint in new[] { Resource, Issuer })
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
                !string.IsNullOrEmpty(uri.Query))
                throw new ArgumentException("Resource and issuer must be absolute HTTPS URLs without query, credentials or fragment.");
        if (RequiredScopes.Any(s => string.IsNullOrEmpty(s) || s.Any(c => c < 0x21 || c > 0x7e || c is '"' or '\\')))
            throw new ArgumentException("Scopes must be valid OAuth scope tokens.");
    }

    internal string Challenge(string error) =>
        $"Bearer resource_metadata=\"{MetadataUrl}\", error=\"{error}\"" +
        (RequiredScopes.Count == 0 ? "" : $", scope=\"{string.Join(' ', RequiredScopes)}\"");
}
