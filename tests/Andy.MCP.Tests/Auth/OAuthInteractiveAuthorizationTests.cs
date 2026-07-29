using System.Net;
using System.Text;
using Andy.MCP.Auth;

namespace Andy.MCP.Tests.Auth;

public class OAuthInteractiveAuthorizationTests
{
    private const string Resource = "https://api.example.com/mcp";
    private const string RedirectUri = "https://client.example.com/oauth/callback";

    [Fact]
    public async Task AuthorizeInteractiveAsync_LibraryOwnsPkceStateCallbackValidationAndExchange()
    {
        var provider = new CallbackProvider(new Uri(RedirectUri + "?code=authorization-code&state=state-from-uri"));
        using var tokenClient = new HttpClient(new TokenHandler());
        var store = new InMemoryTokenStore();
        var client = new OAuthClient(tokenClient, store, () => "state-from-uri");

        var tokens = await client.AuthorizeInteractiveAsync(Metadata(), "client-id", RedirectUri, Resource, ["email", "openid"], provider);

        Assert.NotNull(tokens);
        Assert.Equal("new-token", tokens!.AccessToken);
        Assert.NotNull(provider.Interaction);
        Assert.Equal(RedirectUri, provider.Interaction!.RedirectUri.AbsoluteUri);
        Assert.Contains("code_challenge_method=S256", provider.Interaction.AuthorizationUri.Query);
        Assert.Contains("state=state-from-uri", provider.Interaction.AuthorizationUri.Query);
        Assert.Equal("new-token", (await store.LoadTokensAsync(Resource))!.AccessToken);
    }

    [Theory]
    [InlineData("?code=code&state=wrong")]
    [InlineData("?code=code")]
    [InlineData("?state=state-from-uri")]
    [InlineData("?error=access_denied&state=state-from-uri")]
    public async Task AuthorizeInteractiveAsync_InvalidCallbackLeavesStoredTokensUntouched(string query)
    {
        var store = new InMemoryTokenStore();
        await store.SaveTokensAsync(Resource, new OAuthTokens { AccessToken = "old", Scope = "openid" });
        var client = new OAuthClient(new HttpClient(new TokenHandler()), store, () => "state-from-uri");

        var result = await client.AuthorizeInteractiveAsync(
            Metadata(), "client-id", RedirectUri, Resource, ["openid"],
            new CallbackProvider(new Uri(RedirectUri + query)));

        Assert.Null(result);
        Assert.Equal("old", (await store.LoadTokensAsync(Resource))!.AccessToken);
    }

    [Fact]
    public async Task AuthorizeInteractiveAsync_CallbackForAnotherRedirectLeavesStoredTokensUntouched()
    {
        var store = new InMemoryTokenStore();
        await store.SaveTokensAsync(Resource, new OAuthTokens { AccessToken = "old", Scope = "openid" });
        var client = new OAuthClient(new HttpClient(new TokenHandler()), store, () => "state-from-uri");

        var result = await client.AuthorizeInteractiveAsync(
            Metadata(), "client-id", RedirectUri, Resource, ["openid"],
            new CallbackProvider(new Uri("https://other.example.com/oauth/callback?code=code&state=state-from-uri")));

        Assert.Null(result);
        Assert.Equal("old", (await store.LoadTokensAsync(Resource))!.AccessToken);
    }

    [Fact]
    public async Task AuthorizeInteractiveAsync_CallbackWithUnexpectedRedirectQueryLeavesStoredTokensUntouched()
    {
        const string redirectWithQuery = RedirectUri + "?tenant=expected";
        var store = new InMemoryTokenStore();
        await store.SaveTokensAsync(Resource, new OAuthTokens { AccessToken = "old", Scope = "openid" });
        var client = new OAuthClient(new HttpClient(new TokenHandler()), store, () => "state-from-uri");

        var result = await client.AuthorizeInteractiveAsync(
            Metadata(), "client-id", redirectWithQuery, Resource, ["openid"],
            new CallbackProvider(new Uri(RedirectUri + "?code=code&state=state-from-uri")));

        Assert.Null(result);
        Assert.Equal("old", (await store.LoadTokensAsync(Resource))!.AccessToken);
    }

    [Fact]
    public async Task AuthorizeInteractiveAsync_OmittedTokenScopeMeansRequestedScope()
    {
        using var tokenClient = new HttpClient(new TokenHandler("""{"access_token":"new-token","token_type":"Bearer"}"""));
        var store = new InMemoryTokenStore();
        var client = new OAuthClient(tokenClient, store, () => "state-from-uri");

        var tokens = await client.AuthorizeInteractiveAsync(
            Metadata(), "client-id", RedirectUri, Resource, ["email", "openid"],
            new CallbackProvider(new Uri(RedirectUri + "?code=code&state=state-from-uri")));

        Assert.Equal("email openid", tokens!.Scope);
        Assert.Equal("email openid", (await store.LoadTokensAsync(Resource))!.Scope);
    }

    [Theory]
    [InlineData("http://client.example.com/oauth/callback?code=code&state=state-from-uri")]        // scheme mismatch
    [InlineData("https://client.example.com:8443/oauth/callback?code=code&state=state-from-uri")]  // port mismatch
    [InlineData("https://client.example.com/oauth/other?code=code&state=state-from-uri")]          // path mismatch
    [InlineData("https://client.example.com/oauth/callback?code=code&state=state-from-uri#frag")]  // fragment present
    [InlineData("https://user@client.example.com/oauth/callback?code=code&state=state-from-uri")]  // userinfo present
    public async Task AuthorizeInteractiveAsync_CallbackWithMismatchedTargetLeavesStoredTokensUntouched(string callback)
    {
        var store = new InMemoryTokenStore();
        await store.SaveTokensAsync(Resource, new OAuthTokens { AccessToken = "old", Scope = "openid" });
        var client = new OAuthClient(new HttpClient(new TokenHandler()), store, () => "state-from-uri");

        var result = await client.AuthorizeInteractiveAsync(
            Metadata(), "client-id", RedirectUri, Resource, ["openid"],
            new CallbackProvider(new Uri(callback)));

        Assert.Null(result);
        Assert.Equal("old", (await store.LoadTokensAsync(Resource))!.AccessToken);
    }

    [Fact]
    public async Task AuthorizeInteractiveAsync_RejectsServerWithoutPkceS256()
    {
        var client = new OAuthClient(new HttpClient(new TokenHandler()), new InMemoryTokenStore(), () => "state-from-uri");
        var metadata = Metadata() with { CodeChallengeMethodsSupported = ["plain"] };

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.AuthorizeInteractiveAsync(
            metadata, "client-id", RedirectUri, Resource, ["openid"],
            new CallbackProvider(new Uri(RedirectUri + "?code=code&state=state-from-uri"))));
    }

    [Theory]
    [InlineData("http://client.example.com/callback")]        // non-loopback http
    [InlineData("https://client.example.com/callback#frag")]  // fragment present
    [InlineData("https://user@client.example.com/callback")]  // userinfo present
    public async Task AuthorizeInteractiveAsync_RejectsUnsafeRedirectUri(string redirectUri)
    {
        var client = new OAuthClient(new HttpClient(new TokenHandler()), new InMemoryTokenStore(), () => "state-from-uri");

        await Assert.ThrowsAsync<ArgumentException>(() => client.AuthorizeInteractiveAsync(
            Metadata(), "client-id", redirectUri, Resource, ["openid"],
            new CallbackProvider(new Uri(redirectUri.Split('#')[0] + "?code=code&state=state-from-uri"))));
    }

    [Theory]
    [InlineData("bad scope")]    // embedded whitespace
    [InlineData("bad\"quote")]   // quote character
    [InlineData("bad\\slash")]   // backslash
    public async Task AuthorizeInteractiveAsync_RejectsMalformedScopeTokens(string scope)
    {
        var client = new OAuthClient(new HttpClient(new TokenHandler()), new InMemoryTokenStore(), () => "state-from-uri");

        await Assert.ThrowsAsync<ArgumentException>(() => client.AuthorizeInteractiveAsync(
            Metadata(), "client-id", RedirectUri, Resource, [scope],
            new CallbackProvider(new Uri(RedirectUri + "?code=code&state=state-from-uri"))));
    }

    [Fact]
    public async Task AuthorizeInteractiveAsync_ProviderCancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new OAuthClient(new HttpClient(new TokenHandler()), new InMemoryTokenStore());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.AuthorizeInteractiveAsync(
            Metadata(), "client-id", RedirectUri, Resource, ["openid"], new CancellingProvider(), cts.Token));
    }

    private static AuthorizationServerMetadata Metadata() => new()
    {
        Issuer = "https://auth.example.com",
        AuthorizationEndpoint = "https://auth.example.com/authorize",
        TokenEndpoint = "https://auth.example.com/token",
        CodeChallengeMethodsSupported = ["S256"]
    };

    private sealed class CallbackProvider(Uri callback) : IOAuthAuthorizationProvider
    {
        public OAuthAuthorizationInteraction? Interaction { get; private set; }

        public Task<OAuthAuthorizationCallback?> AuthorizeAsync(OAuthAuthorizationInteraction interaction, CancellationToken cancellationToken = default)
        {
            Interaction = interaction;
            return Task.FromResult<OAuthAuthorizationCallback?>(new() { CallbackUri = callback });
        }
    }

    private sealed class CancellingProvider : IOAuthAuthorizationProvider
    {
        public Task<OAuthAuthorizationCallback?> AuthorizeAsync(OAuthAuthorizationInteraction interaction, CancellationToken cancellationToken = default) =>
            Task.FromCanceled<OAuthAuthorizationCallback?>(cancellationToken);
    }

    private sealed class TokenHandler(string? responseJson = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.Contains("code_verifier=", body);
            Assert.Contains("resource=https%3A%2F%2Fapi.example.com%2Fmcp", body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson ?? """{"access_token":"new-token","token_type":"Bearer","scope":"email openid"}""", Encoding.UTF8, "application/json")
            };
        }
    }
}
