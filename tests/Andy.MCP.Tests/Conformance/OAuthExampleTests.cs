using System.Net;
using System.Text;
using System.Text.Json;
using Andy.MCP.Auth;
using Andy.MCP.Examples;

namespace Andy.MCP.Tests.Conformance;

public class OAuthExampleTests
{
    private sealed class Peer : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            string json;
            if (request.RequestUri.AbsoluteUri == "https://api.example.com/.well-known/oauth-protected-resource/mcp")
                json = """{"resource":"https://api.example.com/mcp","authorization_servers":["https://auth.example.com"]}""";
            else if (request.RequestUri.AbsoluteUri == "https://auth.example.com/.well-known/oauth-authorization-server")
                json = """{"issuer":"https://auth.example.com","authorization_endpoint":"https://auth.example.com/authorize","token_endpoint":"https://auth.example.com/token","registration_endpoint":"https://auth.example.com/register","code_challenge_methods_supported":["S256"]}""";
            else
            {
                Assert.Equal("https://auth.example.com/register", request.RequestUri.AbsoluteUri);
                Assert.Equal(HttpMethod.Post, request.Method);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal("documented-client", body.RootElement.GetProperty("client_name").GetString());
                Assert.Equal("https://client.example.com/callback", body.RootElement.GetProperty("redirect_uris")[0].GetString());
                json = """{"client_id":"registered-example"}""";
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompiledDiscoveryExample_RegistersOnlyWithSelectedAdvertisedIssuer(bool trusted)
    {
        var peer = new Peer();
        using var http = new HttpClient(peer);
        var registration = new ClientRegistrationRequest { ClientName = "documented-client", RedirectUris = ["https://client.example.com/callback"], TokenEndpointAuthMethod = "none" };
        var call = OAuthDiscoveryExample.DiscoverAndRegisterAsync(new Uri("https://api.example.com/mcp"), trusted ? "https://auth.example.com" : "https://other.example.com", registration, new OAuthClient(http), new DynamicClientRegistrationClient(http));
        if (trusted)
        {
            Assert.Equal("registered-example", (await call).ClientId);
            Assert.Equal(3, peer.Requests.Count);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => call);
            Assert.Single(peer.Requests);
        }
    }
}
