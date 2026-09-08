using System.Net;
using Andy.MCP.Auth;

namespace Andy.MCP.Tests.Auth;

public class OAuthEndpointSecurityTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("100.64.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("224.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    public async Task PrivateResolution_IsRejectedBeforeConnect(string address)
    {
        var connected = false;
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await OAuthHttpTransport.ConnectAsync(new DnsEndPoint("attacker.example", 443),
                (_, _) => Task.FromResult(new[] { IPAddress.Parse(address) }),
                (_, _) => { connected = true; return ValueTask.FromResult<Stream>(new MemoryStream()); },
                CancellationToken.None));
        Assert.False(connected);
    }

    [Fact]
    public async Task Connection_UsesOnlyTheVettedIp_WithoutSecondDnsLookup()
    {
        var resolutions = 0;
        var expected = IPAddress.Parse("8.8.8.8");
        using var stream = await OAuthHttpTransport.ConnectAsync(new DnsEndPoint("rebind.example", 443),
            (_, _) => Task.FromResult(new[] { ++resolutions == 1 ? expected : IPAddress.Loopback }),
            (endpoint, _) =>
            {
                Assert.Equal(expected, endpoint.Address);
                Assert.Equal(443, endpoint.Port);
                return ValueTask.FromResult<Stream>(new MemoryStream());
            }, CancellationToken.None);
        Assert.Equal(1, resolutions);
    }

    [Fact]
    public async Task MixedPublicAndPrivateDnsAnswers_AreRejected()
    {
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await OAuthHttpTransport.ConnectAsync(new DnsEndPoint("mixed.example", 443),
                (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Loopback }),
                (_, _) => throw new Exception("Must not connect"), CancellationToken.None));
    }

    [Theory]
    [InlineData("https://127.0.0.1/metadata")]
    [InlineData("https://[::ffff:127.0.0.1]/metadata")]
    [InlineData("https://localhost./metadata")]
    [InlineData("https://user:secret@example.com/metadata")]
    [InlineData("https://example.com/metadata#fragment")]
    public void UnsafeLiteralEndpoints_AreRejected(string url) =>
        Assert.False(SecurityHelpers.ValidateUrl(url).IsValid);

    [Theory]
    [InlineData("BearerEvil scope=admin")]
    [InlineData("Bearers scope=admin")]
    public void LookalikeChallengeSchemes_AreRejected(string header) =>
        Assert.False(WwwAuthenticateChallenge.TryParse(header, out _));

    [Theory]
    [InlineData("http://example.com/token")]
    [InlineData("https://127.0.0.1/token")]
    public async Task DefaultTransport_RejectsUnsafeUrlsBeforeNetwork(string url)
    {
        using var client = OAuthHttpTransport.CreateClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url));
    }

    [Fact]
    public void MissingPkceAdvertisement_IsRejected()
    {
        var metadata = new AuthorizationServerMetadata
        {
            Issuer = "https://auth.example.com",
            AuthorizationEndpoint = "https://auth.example.com/a",
            TokenEndpoint = "https://auth.example.com/t"
        };
        Assert.Throws<InvalidOperationException>(() =>
            new OAuthMetadataDiscovery().ValidateAuthorizationServerMetadata(metadata, new Uri(metadata.Issuer)));
    }

    [Fact]
    public void WrongResourceMetadata_IsRejected()
    {
        var metadata = new ProtectedResourceMetadata { Resource = "https://different.example/mcp", AuthorizationServers = [] };
        Assert.Throws<InvalidOperationException>(() =>
            OAuthMetadataDiscovery.ValidateResource(metadata, new Uri("https://api.example/mcp")));
    }
}
