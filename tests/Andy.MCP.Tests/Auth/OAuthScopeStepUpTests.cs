using System.Net;
using System.Text;
using System.Web;
using Andy.MCP.Auth;

namespace Andy.MCP.Tests.Auth;

public class OAuthScopeStepUpTests
{
    [Fact]
    public void ScopeUnion_IsOrdinalSortedCaseSensitiveAndExact()
    {
        var scopes = OAuthClient.CreateScopeUnion("openid profile", ["profile", "email", "Email"]);

        Assert.Equal(["Email", "email", "openid", "profile"], scopes);
    }

    [Fact]
    public async Task ForbiddenInsufficientScope_UsesInteractiveAuthorizationAndRetriesOnce()
    {
        var store = new InMemoryTokenStore();
        await store.SaveTokensAsync("https://api.example.com/mcp", new OAuthTokens { AccessToken = "old", Scope = "openid" });
        var handler = new StepUpTransport();
        using var tokenClient = new HttpClient(new TokenHandler());
        var provider = new CallbackProvider();
        using var client = new HttpClient(new OAuthDelegatingHandler(
            new OAuthClient(tokenClient, store, () => "state"),
            "https://api.example.com/mcp", Metadata(), "client-id", handler,
            authorizationProvider: provider,
            authorizationRedirectUri: "https://client.example.com/oauth/callback"));

        var response = await client.GetAsync("https://api.example.com/mcp");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Count);
        Assert.Equal("email openid", HttpUtility.ParseQueryString(provider.Interaction!.AuthorizationUri.Query)["scope"]);
    }

    [Fact]
    public async Task ForbiddenInsufficientScope_RetryPreservesReplaySafeRequestDetails()
    {
        var store = await StoreWithOldTokenAsync();
        var provider = new CallbackProvider();
        var transport = new StepUpTransport();
        using var client = CreateClient(store, provider, transport);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/mcp")
        {
            Content = new StringContent("request body", Encoding.UTF8, "text/plain"),
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };
        request.Headers.TryAddWithoutValidation("X-Test-Header", "expected");
        request.Options.Set(new HttpRequestOptionsKey<string>("step-up-test-option"), "expected");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("request body", transport.RetriedBody);
        Assert.Equal("expected", transport.RetriedHeader);
        Assert.Equal("expected", transport.RetriedOption);
        Assert.Equal(HttpVersion.Version20, transport.RetriedVersion);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "Bearer error=invalid_token, scope=\"email\"")]
    [InlineData(HttpStatusCode.Forbidden, "Bearer error=insufficient_scope")]
    [InlineData(HttpStatusCode.Unauthorized, "Bearer error=insufficient_scope, scope=\"email\"")]
    public async Task NonQualifyingChallenge_DoesNotStartInteractiveAuthorization(HttpStatusCode status, string challenge)
    {
        var store = await StoreWithOldTokenAsync();
        var provider = new CallbackProvider();
        var transport = new FixedResponseTransport(status, challenge);
        using var client = CreateClient(store, provider, transport);

        var response = await client.GetAsync("https://api.example.com/mcp");

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(0, provider.Count);
        Assert.Equal(1, transport.Count);
        if (status == HttpStatusCode.Forbidden)
            Assert.Equal("old", (await store.LoadTokensAsync("https://api.example.com/mcp"))!.AccessToken);
        else
            Assert.Null(await store.LoadTokensAsync("https://api.example.com/mcp")); // pre-existing 401 invalidation behavior
    }

    [Fact]
    public async Task ForbiddenInsufficientScope_UnchangedOrIncompleteTokenLeavesOriginalForbiddenResponse()
    {
        var store = await StoreWithOldTokenAsync();
        var provider = new CallbackProvider();
        var transport = new StepUpTransport();
        using var tokenClient = new HttpClient(new TokenHandler("""{"access_token":"old","token_type":"Bearer","scope":"email openid"}"""));
        using var client = new HttpClient(new OAuthDelegatingHandler(
            new OAuthClient(tokenClient, store, () => "state"), "https://api.example.com/mcp", Metadata(), "client-id", transport,
            authorizationProvider: provider, authorizationRedirectUri: "https://client.example.com/oauth/callback"));

        var response = await client.GetAsync("https://api.example.com/mcp");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, transport.Count);
        Assert.Equal("old", (await store.LoadTokensAsync("https://api.example.com/mcp"))!.AccessToken);
    }

    [Fact]
    public async Task ForbiddenInsufficientScope_NewTokenMissingRequiredScopeIsRejectedAndNotPersisted()
    {
        var store = await StoreWithOldTokenAsync();
        var provider = new CallbackProvider();
        var transport = new StepUpTransport();
        // The token endpoint returns a genuinely new access token, but its granted scope does not
        // cover the required union (existing "openid" + challenged "email openid").
        using var tokenClient = new HttpClient(new TokenHandler("""{"access_token":"new","token_type":"Bearer","scope":"email"}"""));
        using var client = new HttpClient(new OAuthDelegatingHandler(
            new OAuthClient(tokenClient, store, () => "state"), "https://api.example.com/mcp", Metadata(), "client-id", transport,
            authorizationProvider: provider, authorizationRedirectUri: "https://client.example.com/oauth/callback"));

        var response = await client.GetAsync("https://api.example.com/mcp");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, provider.Count);  // the interaction ran
        Assert.Equal(1, transport.Count); // but the under-scoped token was never used to retry
        Assert.Equal("old", (await store.LoadTokensAsync("https://api.example.com/mcp"))!.AccessToken);
    }

    [Fact]
    public async Task ConcurrentScopeStepUp_PerResourceRunsOneExternalInteraction()
    {
        var store = await StoreWithOldTokenAsync();
        var provider = new CallbackProvider(delay: TimeSpan.FromMilliseconds(30));
        var transport = new StepUpTransport();
        using var client = CreateClient(store, provider, transport);

        var responses = await Task.WhenAll(
            client.GetAsync("https://api.example.com/mcp"),
            client.GetAsync("https://api.example.com/mcp"));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(1, provider.Count);
    }

    private static async Task<InMemoryTokenStore> StoreWithOldTokenAsync()
    {
        var store = new InMemoryTokenStore();
        await store.SaveTokensAsync("https://api.example.com/mcp", new OAuthTokens { AccessToken = "old", Scope = "openid" });
        return store;
    }

    private static HttpClient CreateClient(InMemoryTokenStore store, CallbackProvider provider, HttpMessageHandler transport)
    {
        var tokenClient = new HttpClient(new TokenHandler());
        return new HttpClient(new OAuthDelegatingHandler(
            new OAuthClient(tokenClient, store, () => "state"), "https://api.example.com/mcp", Metadata(), "client-id", transport,
            authorizationProvider: provider, authorizationRedirectUri: "https://client.example.com/oauth/callback"));
    }

    private static AuthorizationServerMetadata Metadata() => new()
    {
        Issuer = "https://auth.example.com",
        AuthorizationEndpoint = "https://auth.example.com/authorize",
        TokenEndpoint = "https://auth.example.com/token",
        CodeChallengeMethodsSupported = ["S256"]
    };

    private sealed class CallbackProvider(TimeSpan? delay = null) : IOAuthAuthorizationProvider
    {
        public int Count { get; private set; }
        public OAuthAuthorizationInteraction? Interaction { get; private set; }

        public async Task<OAuthAuthorizationCallback?> AuthorizeAsync(OAuthAuthorizationInteraction interaction, CancellationToken cancellationToken = default)
        {
            Count++;
            Interaction = interaction;
            if (delay is not null)
                await Task.Delay(delay.Value, cancellationToken);
            return new OAuthAuthorizationCallback { CallbackUri = new Uri(interaction.RedirectUri + "?code=code&state=state") };
        }
    }

    private sealed class TokenHandler(string? json = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json ?? """{"access_token":"new","token_type":"Bearer","scope":"email openid"}""")
            });
    }

    private sealed class FixedResponseTransport(HttpStatusCode status, string challenge) : HttpMessageHandler
    {
        public int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            var response = new HttpResponseMessage(status);
            response.Headers.TryAddWithoutValidation("WWW-Authenticate", challenge);
            return Task.FromResult(response);
        }
    }

    private sealed class StepUpTransport : HttpMessageHandler
    {
        public int Count { get; private set; }
        public string? RetriedBody { get; private set; }
        public string? RetriedHeader { get; private set; }
        public string? RetriedOption { get; private set; }
        public Version? RetriedVersion { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            if (request.Headers.Authorization?.Parameter == "new")
            {
                RetriedBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                RetriedHeader = request.Headers.TryGetValues("X-Test-Header", out var values) ? values.SingleOrDefault() : null;
                request.Options.TryGetValue(new HttpRequestOptionsKey<string>("step-up-test-option"), out var option);
                RetriedOption = option;
                RetriedVersion = request.Version;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer error=\"insufficient_scope\", scope=\"email openid\"");
            return response;
        }
    }
}
