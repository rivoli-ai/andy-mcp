using System.Net;
using System.Text;
using Andy.MCP.Auth;

namespace Andy.MCP.Tests.Auth;

/// <summary>
/// Tests for MCP 2025-11-25 OAuth client authorization improvements (issue #45): WWW-Authenticate
/// parsing, expiry skew, one-time refresh-token rotation under concurrency, and 401 handling that
/// never blindly retries a rejected token.
/// </summary>
public class OAuthAuthorizationTests
{
    // ---- WWW-Authenticate challenge parsing ----

    [Fact]
    public void Parse_ResourceMetadataAndScopeChallenge()
    {
        const string header =
            """Bearer realm="mcp", error="insufficient_scope", error_description="need more", resource_metadata="https://api.example.com/.well-known/oauth-protected-resource", scope="read write" """;

        Assert.True(WwwAuthenticateChallenge.TryParse(header, out var challenge));
        Assert.Equal("insufficient_scope", challenge.Error);
        Assert.Equal("need more", challenge.ErrorDescription);
        Assert.Equal("https://api.example.com/.well-known/oauth-protected-resource", challenge.ResourceMetadata);
        Assert.Equal(new[] { "read", "write" }, challenge.Scopes);
        Assert.Equal("mcp", challenge.Realm);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic realm=\"x\"")]
    public void Parse_NonBearer_ReturnsFalse(string? header)
    {
        Assert.False(WwwAuthenticateChallenge.TryParse(header, out _));
    }

    [Fact]
    public void Parse_UnquotedValues()
    {
        Assert.True(WwwAuthenticateChallenge.TryParse("Bearer error=invalid_token", out var challenge));
        Assert.Equal("invalid_token", challenge.Error);
    }

    // ---- Expiry skew ----

    [Fact]
    public void Token_IsExpired_WithinSkewWindow()
    {
        var almostExpired = new OAuthTokens { AccessToken = "t", ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(20) };
        var fresh = new OAuthTokens { AccessToken = "t", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };

        Assert.True(almostExpired.IsExpired);  // within the 30s skew
        Assert.False(fresh.IsExpired);
    }

    // ---- 401 handling and refresh ----

    [Fact]
    public async Task HandleUnauthorized_NoRefreshToken_ClearsRejectedToken()
    {
        var store = new InMemoryTokenStore();
        await store.SaveTokensAsync("https://api", new OAuthTokens { AccessToken = "rejected" });
        var client = new OAuthClient(new HttpClient(new StubHandler(_ => Fail())), store);

        var result = await client.HandleUnauthorizedAsync("https://api");

        Assert.Null(result);                                        // no fresh token
        Assert.Null(await store.LoadTokensAsync("https://api"));    // rejected token discarded
    }

    [Fact]
    public async Task ConcurrentRefresh_RotatesRefreshTokenExactlyOnce()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            var n = Interlocked.Increment(ref calls);
            // Only the first refresh succeeds; a second call with the now-rotated old token would fail.
            return n == 1
                ? Json("""{"access_token":"at-new","token_type":"Bearer","refresh_token":"rt-new","expires_in":3600}""")
                : new HttpResponseMessage(HttpStatusCode.BadRequest);
        });

        var store = new InMemoryTokenStore();
        await store.SaveTokensAsync("https://api", new OAuthTokens
        {
            AccessToken = "at-old",
            RefreshToken = "rt-old",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1)
        });
        var client = new OAuthClient(new HttpClient(handler), store);
        var metadata = new AuthorizationServerMetadata
        {
            Issuer = "https://auth",
            AuthorizationEndpoint = "https://auth/authorize",
            TokenEndpoint = "https://auth/token"
        };

        var t1 = client.RefreshTokenAsync(metadata, "rt-old", "cid", "https://api");
        var t2 = client.RefreshTokenAsync(metadata, "rt-old", "cid", "https://api");
        var r1 = await t1;
        var r2 = await t2;

        Assert.Equal(1, calls);                 // rotated exactly once
        Assert.Equal("at-new", r1.AccessToken);
        Assert.Equal("at-new", r2.AccessToken); // second caller reused the rotated token
    }

    // ---- helpers ----

    private static HttpResponseMessage Fail() => new(HttpStatusCode.BadRequest);

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
