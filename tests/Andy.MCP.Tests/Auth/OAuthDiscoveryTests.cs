using System.Net;
using System.Text;
using Andy.MCP.Auth;

namespace Andy.MCP.Tests.Auth;

/// <summary>
/// Tests OAuth metadata discovery (issue #45): RFC 9728 protected-resource metadata and RFC 8414 /
/// OIDC authorization-server metadata URL construction and fallback order, issuer/PKCE validation,
/// and SSRF refusal.
/// </summary>
public class OAuthDiscoveryTests
{
    // ---- URL construction ----

    [Fact]
    public void ProtectedResourceUrls_PathAware_ThenRoot()
    {
        var urls = OAuthMetadataDiscovery.ProtectedResourceMetadataUrls(new Uri("https://api.example.com/mcp"));
        Assert.Equal(new[]
        {
            "https://api.example.com/.well-known/oauth-protected-resource/mcp",
            "https://api.example.com/.well-known/oauth-protected-resource",
        }, urls.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public void ProtectedResourceUrls_RootResource_OnlyRoot()
    {
        var urls = OAuthMetadataDiscovery.ProtectedResourceMetadataUrls(new Uri("https://api.example.com"));
        Assert.Equal(new[] { "https://api.example.com/.well-known/oauth-protected-resource" },
            urls.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public void AuthorizationServerUrls_PathAware_CoverRfc8414AndOidc()
    {
        var urls = OAuthMetadataDiscovery.AuthorizationServerMetadataUrls(new Uri("https://auth.example.com/tenant1"));
        Assert.Equal(new[]
        {
            "https://auth.example.com/.well-known/oauth-authorization-server/tenant1",
            "https://auth.example.com/tenant1/.well-known/openid-configuration",
            "https://auth.example.com/.well-known/oauth-authorization-server",
            "https://auth.example.com/.well-known/openid-configuration",
        }, urls.Select(u => u.AbsoluteUri));
    }

    // ---- Discovery + fallback ----

    [Fact]
    public async Task ProtectedResource_FallsBackToRoot_WhenPathAware404s()
    {
        var handler = new StubHandler(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/mcp")
                ? new HttpResponseMessage(HttpStatusCode.NotFound)                       // path-aware candidate
                : Json("""{"resource":"https://api.example.com/mcp","authorization_servers":["https://auth.example.com"]}"""));

        var discovery = new OAuthMetadataDiscovery(new HttpClient(handler));
        var prm = await discovery.DiscoverProtectedResourceMetadataAsync(new Uri("https://api.example.com/mcp"));

        Assert.Contains("https://auth.example.com", prm.AuthorizationServers);
    }

    [Fact]
    public async Task AuthorizationServer_Discovered_WhenIssuerMatches()
    {
        var handler = new StubHandler(_ => Json(
            """{"issuer":"https://auth.example.com","authorization_endpoint":"https://auth.example.com/authorize","token_endpoint":"https://auth.example.com/token","code_challenge_methods_supported":["S256"]}"""));

        var discovery = new OAuthMetadataDiscovery(new HttpClient(handler));
        var metadata = await discovery.DiscoverAuthorizationServerMetadataAsync(new Uri("https://auth.example.com"));

        Assert.Equal("https://auth.example.com/token", metadata.TokenEndpoint);
    }

    // ---- Validation ----

    [Fact]
    public async Task AuthorizationServer_IssuerMismatch_Throws()
    {
        var handler = new StubHandler(_ => Json(
            """{"issuer":"https://evil.example.com","authorization_endpoint":"https://evil.example.com/a","token_endpoint":"https://evil.example.com/t"}"""));

        var discovery = new OAuthMetadataDiscovery(new HttpClient(handler));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            discovery.DiscoverAuthorizationServerMetadataAsync(new Uri("https://auth.example.com")));
    }

    [Fact]
    public async Task AuthorizationServer_WithoutPkceS256_Throws()
    {
        var handler = new StubHandler(_ => Json(
            """{"issuer":"https://auth.example.com","authorization_endpoint":"https://auth.example.com/a","token_endpoint":"https://auth.example.com/t","code_challenge_methods_supported":["plain"]}"""));

        var discovery = new OAuthMetadataDiscovery(new HttpClient(handler));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            discovery.DiscoverAuthorizationServerMetadataAsync(new Uri("https://auth.example.com")));
    }

    [Fact]
    public async Task NonHttpsResource_IsRefused()
    {
        var discovery = new OAuthMetadataDiscovery(new HttpClient(new StubHandler(_ => Json("{}"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            discovery.DiscoverProtectedResourceMetadataAsync(new Uri("http://api.example.com/mcp")));
    }

    // ---- helpers ----

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_respond(request));
    }
}
