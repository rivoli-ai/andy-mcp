using System.Net;
using System.Text;
using Andy.MCP.Auth;

namespace Andy.MCP.Tests.Auth;

/// <summary>
/// End-to-end test that a 401 with a resource_metadata challenge drives protected-resource then
/// authorization-server discovery, a token refresh against the discovered endpoint, and a retry
/// (issue #69).
/// </summary>
public class OAuth401DiscoveryTests
{
    [Fact]
    public async Task Unauthorized_Challenge_DrivesDiscoveryRefreshAndRetry()
    {
        var stub = new RoutingStub();

        var store = new InMemoryTokenStore();
        await store.SaveTokensAsync("https://api.example.com/mcp", new OAuthTokens
        {
            AccessToken = "old-token",
            RefreshToken = "rt-old",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) // valid, but the server rejects it
        });

        var oauthClient = new OAuthClient(new HttpClient(stub), store);
        var discovery = new OAuthMetadataDiscovery(new HttpClient(stub));
        var handler = new OAuthDelegatingHandler(
            oauthClient, "https://api.example.com/mcp",
            authMetadata: null, clientId: "cid", innerHandler: stub, discovery: discovery);

        using var client = new HttpClient(handler);
        var response = await client.GetAsync("https://api.example.com/mcp");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(stub.DiscoveredPrm);
        Assert.True(stub.DiscoveredAuthServer);
        Assert.True(stub.Refreshed);
        Assert.Equal("new-token", (await store.LoadTokensAsync("https://api.example.com/mcp"))!.AccessToken);
    }

    /// <summary>Routes the API, PRM, authorization-server metadata, and token endpoints.</summary>
    private sealed class RoutingStub : HttpMessageHandler
    {
        public bool DiscoveredPrm { get; private set; }
        public bool DiscoveredAuthServer { get; private set; }
        public bool Refreshed { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;

            if (url == "https://api.example.com/mcp")
            {
                if (request.Headers.Authorization?.Parameter == "new-token")
                    return Ok("""{"ok":true}""");
                var unauthorized = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                unauthorized.Headers.TryAddWithoutValidation("WWW-Authenticate",
                    """Bearer resource_metadata="https://api.example.com/.well-known/oauth-protected-resource" """);
                return unauthorized;
            }

            if (url == "https://api.example.com/.well-known/oauth-protected-resource")
            {
                DiscoveredPrm = true;
                return Ok("""{"resource":"https://api.example.com/mcp","authorization_servers":["https://auth.example.com"]}""");
            }

            if (url == "https://auth.example.com/.well-known/oauth-authorization-server")
            {
                DiscoveredAuthServer = true;
                return Ok("""{"issuer":"https://auth.example.com","authorization_endpoint":"https://auth.example.com/authorize","token_endpoint":"https://auth.example.com/token","code_challenge_methods_supported":["S256"]}""");
            }

            if (url == "https://auth.example.com/token")
            {
                Refreshed = true;
                var body = await request.Content!.ReadAsStringAsync(ct);
                Assert.Contains("refresh_token=rt-old", body);
                return Ok("""{"access_token":"new-token","token_type":"Bearer","refresh_token":"rt-new","expires_in":3600}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Ok(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
