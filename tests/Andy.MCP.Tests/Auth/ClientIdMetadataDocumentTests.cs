using System.Net;
using System.Text;
using System.Text.Json;
using Andy.MCP.Auth;

namespace Andy.MCP.Tests.Auth;

public class ClientIdMetadataDocumentTests
{
    private const string DocumentUrl = "https://client.example.com/oauth/client.json";

    [Fact]
    public void Validate_AcceptsHttpsUrlWithPathAndExactClientId()
    {
        CreateDocument().Validate(DocumentUrl);
    }

    [Theory]
    [InlineData("http://client.example.com/oauth/client.json")]
    [InlineData("https://client.example.com/")]
    [InlineData("https://user:password@client.example.com/oauth/client.json")]
    [InlineData("https://client.example.com/oauth/client.json#fragment")]
    [InlineData("https://client.example.com/oauth/./client.json")]
    [InlineData("https://client.example.com/oauth/../client.json")]
    public void Validate_RejectsInvalidClientIdentifierUrl(string documentUrl)
    {
        Assert.Throws<ArgumentException>(() => (CreateDocument() with { ClientId = documentUrl }).Validate(documentUrl));
    }

    [Theory]
    [InlineData("https://client.example.com:443/oauth/client.json")]
    [InlineData("https://client.example.com/oauth/client.json/")]
    [InlineData("https://CLIENT.example.com/oauth/client.json")]
    [InlineData("https://client.example.com/oauth/%63lient.json")]
    public void Validate_RejectsNonOrdinalClientIdEquivalence(string clientId)
    {
        Assert.Throws<ArgumentException>(() => (CreateDocument() with { ClientId = clientId }).Validate(DocumentUrl));
    }

    [Fact]
    public void Validate_AllowsQueryBecauseDraftOnlyDiscouragesIt()
    {
        const string documentUrl = "https://client.example.com/oauth/client.json?v=1";
        (CreateDocument() with { ClientId = documentUrl }).Validate(documentUrl);
    }

    [Fact]
    public void Validate_RejectsMissingClientNameAndRedirectUris()
    {
        Assert.Throws<ArgumentException>(() => (CreateDocument() with { ClientName = " " }).Validate(DocumentUrl));
        Assert.Throws<ArgumentException>(() => (CreateDocument() with { RedirectUris = [] }).Validate(DocumentUrl));
    }

    [Theory]
    [InlineData("http://client.example.com/callback")]
    [InlineData("https://client.example.com/callback#fragment")]
    public void Validate_RejectsUnsafeRedirectUri(string redirectUri)
    {
        Assert.Throws<ArgumentException>(() => (CreateDocument() with { RedirectUris = [redirectUri] }).Validate(DocumentUrl));
    }

    [Theory]
    [InlineData("client_secret_basic")]
    [InlineData("client_secret_post")]
    [InlineData("client_secret_jwt")]
    public void Validate_RejectsSharedSecretAuthentication(string method)
    {
        Assert.Throws<ArgumentException>(() => (CreateDocument() with { TokenEndpointAuthMethod = method }).Validate(DocumentUrl));
    }

    [Fact]
    public void Validate_AcceptsPrivateKeyJwtWithPublicJwksUri()
    {
        (CreateDocument() with
        {
            TokenEndpointAuthMethod = "private_key_jwt",
            JwksUri = "https://client.example.com/oauth/jwks.json"
        }).Validate(DocumentUrl);
    }

    [Fact]
    public void Serialization_UsesDraftPropertyNamesAndCannotSerializeSharedSecrets()
    {
        var json = JsonSerializer.Serialize(CreateDocument() with { JwksUri = "https://client.example.com/oauth/jwks.json" });

        Assert.Contains("\"client_id\"", json);
        Assert.Contains("\"client_name\"", json);
        Assert.Contains("\"redirect_uris\"", json);
        Assert.Contains("\"jwks_uri\"", json);
        Assert.DoesNotContain("client_secret", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthorizationServerMetadata_DeserializesCimdCapability()
    {
        var metadata = JsonSerializer.Deserialize<AuthorizationServerMetadata>("""
            {
              "issuer": "https://auth.example.com",
              "authorization_endpoint": "https://auth.example.com/authorize",
              "token_endpoint": "https://auth.example.com/token",
              "client_id_metadata_document_supported": true
            }
            """)!;

        Assert.True(metadata.ClientIdMetadataDocumentSupported);
    }

    [Fact]
    public async Task Resolver_SelectsAdvertisedCimdWithoutDcrNetworkRequest()
    {
        var dcrCalls = 0;
        var resolver = new OAuthClientRegistrationResolver(new DynamicClientRegistrationClient(
            new HttpClient(new CimDTestHandler(_ =>
            {
                dcrCalls++;
                return Json("""{"client_id":"dcr-client"}""");
            }))));

        var registration = await resolver.ResolveAsync(Metadata(cimdSupported: true), Options(withDcr: true));

        Assert.Equal(OAuthClientRegistrationMethod.ClientIdMetadataDocument, registration.Method);
        Assert.Equal(DocumentUrl, registration.ClientId);
        Assert.Null(registration.DynamicRegistration);
        Assert.Equal(0, dcrCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Resolver_UsesExplicitDcrFallbackWhenCimdIsUnavailable(bool? cimdSupported)
    {
        HttpRequestMessage? sent = null;
        var resolver = new OAuthClientRegistrationResolver(new DynamicClientRegistrationClient(
            new HttpClient(new CimDTestHandler(async request =>
            {
                sent = await CopyAsync(request);
                return Json("""{"client_id":"dcr-client"}""");
            }))));

        var registration = await resolver.ResolveAsync(Metadata(cimdSupported), Options(withDcr: true));

        Assert.Equal(OAuthClientRegistrationMethod.DynamicClientRegistration, registration.Method);
        Assert.Equal("dcr-client", registration.ClientId);
        Assert.NotNull(registration.DynamicRegistration);
        Assert.Equal(HttpMethod.Post, sent!.Method);
        Assert.Equal("initial-access-token", sent.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task Resolver_RequiresAdvertisedEndpointAndExplicitDcrConfiguration()
    {
        var resolver = new OAuthClientRegistrationResolver(new DynamicClientRegistrationClient(
            new HttpClient(new CimDTestHandler(
                (Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new Xunit.Sdk.XunitException("No request expected"))))));

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(Metadata(cimdSupported: false, registrationEndpoint: null), Options(withDcr: true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(Metadata(cimdSupported: false), Options(withDcr: false)));
    }

    [Fact]
    public async Task Resolver_DoesNotFallBackAfterInvalidCimdConfiguration()
    {
        var resolver = new OAuthClientRegistrationResolver(new DynamicClientRegistrationClient(
            new HttpClient(new CimDTestHandler(
                (Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new Xunit.Sdk.XunitException("DCR must not run"))))));

        await Assert.ThrowsAsync<ArgumentException>(() => resolver.ResolveAsync(Metadata(cimdSupported: true), new OAuthClientRegistrationOptions
        {
            ClientIdMetadataDocumentUri = DocumentUrl,
            ClientIdMetadataDocument = CreateDocument() with { ClientId = "https://client.example.com/other.json" },
            DynamicClientRegistration = new ClientRegistrationRequest { ClientName = "fallback" }
        }));
    }

    private static ClientIdMetadataDocument CreateDocument() => new()
    {
        ClientId = DocumentUrl,
        ClientName = "Andy MCP",
        RedirectUris = ["https://client.example.com/oauth/callback"],
        GrantTypes = ["authorization_code"],
        ResponseTypes = ["code"],
        TokenEndpointAuthMethod = "none"
    };

    private static OAuthClientRegistrationOptions Options(bool withDcr) => new()
    {
        ClientIdMetadataDocumentUri = DocumentUrl,
        ClientIdMetadataDocument = CreateDocument(),
        DynamicClientRegistration = withDcr ? new ClientRegistrationRequest { ClientName = "Andy MCP" } : null,
        InitialAccessToken = "initial-access-token"
    };

    private static AuthorizationServerMetadata Metadata(bool? cimdSupported, string? registrationEndpoint = "https://auth.example.com/register")
    {
        var metadata = new AuthorizationServerMetadata
        {
            Issuer = "https://auth.example.com",
            AuthorizationEndpoint = "https://auth.example.com/authorize",
            TokenEndpoint = "https://auth.example.com/token",
            RegistrationEndpoint = registrationEndpoint
        };

        return cimdSupported is null ? metadata : metadata with { ClientIdMetadataDocumentSupported = cimdSupported.Value };
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<HttpRequestMessage> CopyAsync(HttpRequestMessage request)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (request.Content is not null)
            copy.Content = new StringContent(await request.Content.ReadAsStringAsync());
        return copy;
    }

    private sealed class CimDTestHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public CimDTestHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
            _handler = request => Task.FromResult(handler(request));

        public CimDTestHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _handler(request);
    }
}
