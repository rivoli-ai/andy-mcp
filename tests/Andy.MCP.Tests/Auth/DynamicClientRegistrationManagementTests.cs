using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Andy.MCP.Auth;

namespace Andy.MCP.Tests.Auth;

public class DynamicClientRegistrationManagementTests
{
    private const string RegistrationEndpoint = "https://auth.example.com/register";
    private const string ConfigurationEndpoint = "https://auth.example.com/register/client-1";

    [Fact]
    public async Task RegisterAsync_UsesOptionalInitialAccessTokenOnlyForRegistration()
    {
        using var httpClient = new HttpClient(new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(RegistrationEndpoint, request.RequestUri!.AbsoluteUri);
            Assert.Equal("initial-access-token", request.Headers.Authorization?.Parameter);
            return Json("""{"client_id":"client-1"}""");
        }));
        var client = new DynamicClientRegistrationClient(httpClient);

        var registration = await client.RegisterAsync(
            RegistrationEndpoint,
            new ClientRegistrationRequest { ClientName = "Andy MCP" },
            "initial-access-token");

        Assert.Equal("client-1", registration.ClientId);
    }

    [Fact]
    public async Task GetConfigurationAsync_UsesRegistrationAccessTokenAndAtomicallyRotatesCredentials()
    {
        using var httpClient = new HttpClient(new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("registration-token-1", request.Headers.Authorization?.Parameter);
            return Json(ResponseJson("registration-token-2", "client-secret-2"));
        }));
        var client = new DynamicClientRegistrationClient(httpClient);
        var current = Registration();

        var updated = await client.GetConfigurationAsync(current);

        Assert.Equal("registration-token-2", updated.RegistrationAccessToken);
        Assert.Equal("client-secret-2", updated.ClientSecret);
        Assert.Equal("registration-token-1", current.RegistrationAccessToken);
        Assert.Equal("client-secret-1", current.ClientSecret);
    }

    [Fact]
    public async Task GetConfigurationAsync_RejectsChangedClientIdWithoutCredentialLeakage()
    {
        using var httpClient = new HttpClient(new RecordingHandler(_ => Json("""{"client_id":"other-client"}""")));
        var client = new DynamicClientRegistrationClient(httpClient);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetConfigurationAsync(Registration()));

        Assert.DoesNotContain("registration-token-1", exception.Message);
        Assert.DoesNotContain("client-secret-1", exception.Message);
    }

    [Fact]
    public async Task GetConfigurationAsync_RejectsMalformedSuccessfulResponse()
    {
        using var httpClient = new HttpClient(new RecordingHandler(_ => Json("{")));
        var client = new DynamicClientRegistrationClient(httpClient);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetConfigurationAsync(Registration()));

        Assert.DoesNotContain("registration-token-1", exception.Message);
    }

    [Fact]
    public async Task UpdateConfigurationAsync_UsesPutWithCompleteReplacementMetadata()
    {
        HttpRequestMessage? sent = null;
        using var httpClient = new HttpClient(new RecordingHandler(async request =>
        {
            sent = await CopyAsync(request);
            return Json(ResponseJson("registration-token-2", "client-secret-2"));
        }));
        var client = new DynamicClientRegistrationClient(httpClient);

        var updated = await client.UpdateConfigurationAsync(Registration(), ReplacementMetadata());

        Assert.NotNull(sent);
        Assert.Equal(HttpMethod.Put, sent!.Method);
        Assert.Equal("application/json", sent.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("registration-token-1", sent.Headers.Authorization?.Parameter);
        var body = await sent.Content.ReadAsStringAsync();
        using var payload = JsonDocument.Parse(body);
        Assert.Equal("client-1", payload.RootElement.GetProperty("client_id").GetString());
        Assert.Equal("Andy MCP", payload.RootElement.GetProperty("client_name").GetString());
        Assert.Equal("https://client.example.com/callback", payload.RootElement.GetProperty("redirect_uris")[0].GetString());
        Assert.Equal("authorization_code", payload.RootElement.GetProperty("grant_types")[0].GetString());
        Assert.False(payload.RootElement.TryGetProperty("registration_access_token", out _));
        Assert.False(payload.RootElement.TryGetProperty("registration_client_uri", out _));
        Assert.False(payload.RootElement.TryGetProperty("client_id_issued_at", out _));
        Assert.False(payload.RootElement.TryGetProperty("client_secret_expires_at", out _));
        Assert.False(payload.RootElement.TryGetProperty("client_secret", out _));
        Assert.Equal("registration-token-2", updated.RegistrationAccessToken);
        Assert.Equal("client-secret-2", updated.ClientSecret);
    }

    [Fact]
    public async Task UpdateConfigurationAsync_RejectsIncompleteRedirectFlowMetadataBeforeSending()
    {
        var sendCount = 0;
        using var httpClient = new HttpClient(new RecordingHandler(_ =>
        {
            sendCount++;
            return Json(ResponseJson("registration-token-2", "client-secret-2"));
        }));
        var client = new DynamicClientRegistrationClient(httpClient);

        await Assert.ThrowsAsync<ArgumentException>(() => client.UpdateConfigurationAsync(
            Registration(),
            new ClientRegistrationMetadata
            {
                GrantTypes = ["authorization_code"],
                ResponseTypes = ["code"]
            }));

        Assert.Equal(0, sendCount);
    }

    [Fact]
    public async Task UpdateConfigurationAsync_RejectsChangedClientId()
    {
        using var httpClient = new HttpClient(new RecordingHandler(_ => Json(ResponseJson("registration-token-2", "client-secret-2", "other-client"))));
        var client = new DynamicClientRegistrationClient(httpClient);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.UpdateConfigurationAsync(Registration(), ReplacementMetadata()));
    }

    [Fact]
    public async Task DeleteAsync_UsesCurrentRotatedRegistrationAccessTokenAndAcceptsNoContent()
    {
        using var httpClient = new HttpClient(new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
                return Json(ResponseJson("registration-token-2", "client-secret-2"));

            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal("registration-token-2", request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        var client = new DynamicClientRegistrationClient(httpClient);

        var rotatedRegistration = await client.GetConfigurationAsync(Registration());
        await client.DeleteAsync(rotatedRegistration);
    }

    [Theory]
    [InlineData("http://auth.example.com/register/client-1")]
    [InlineData("https://auth.example.com/register/client-1#fragment")]
    public async Task ManagementOperations_RejectUnsafeConfigurationEndpoints(string endpoint)
    {
        using var httpClient = new HttpClient(new RecordingHandler(
            (Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new Xunit.Sdk.XunitException("No request expected"))));
        var client = new DynamicClientRegistrationClient(httpClient);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetConfigurationAsync(Registration() with { RegistrationClientUri = endpoint }));

        Assert.DoesNotContain("registration-token-1", exception.Message);
    }

    [Fact]
    public async Task GetConfigurationAsync_RejectsUnsafeReturnedConfigurationEndpoint()
    {
        using var httpClient = new HttpClient(new RecordingHandler(_ => Json(
            ResponseJson("registration-token-2", "client-secret-2").Replace(ConfigurationEndpoint, "http://auth.example.com/register/client-1", StringComparison.Ordinal))));
        var client = new DynamicClientRegistrationClient(httpClient);

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetConfigurationAsync(Registration()));
    }

    [Fact]
    public async Task DeleteAsync_PropagatesNonSuccessWithoutCredentialLeakage()
    {
        using var httpClient = new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var client = new DynamicClientRegistrationClient(httpClient);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.DeleteAsync(Registration()));

        Assert.DoesNotContain("registration-token-1", exception.Message);
    }

    private static ClientRegistrationResponse Registration() => new()
    {
        ClientId = "client-1",
        ClientSecret = "client-secret-1",
        RegistrationAccessToken = "registration-token-1",
        RegistrationClientUri = ConfigurationEndpoint,
        RedirectUris = ["https://client.example.com/callback"],
        GrantTypes = ["authorization_code"],
        ResponseTypes = ["code"],
        ClientName = "Andy MCP"
    };

    private static ClientRegistrationMetadata ReplacementMetadata() => new()
    {
        RedirectUris = ["https://client.example.com/callback"],
        GrantTypes = ["authorization_code"],
        ResponseTypes = ["code"],
        ClientName = "Andy MCP"
    };

    private static string ResponseJson(string registrationAccessToken, string clientSecret, string clientId = "client-1") => $$"""
        {
          "client_id": "{{clientId}}",
          "client_secret": "{{clientSecret}}",
          "registration_access_token": "{{registrationAccessToken}}",
          "registration_client_uri": "{{ConfigurationEndpoint}}",
          "redirect_uris": ["https://client.example.com/callback"],
          "grant_types": ["authorization_code"],
          "response_types": ["code"],
          "client_name": "Andy MCP"
        }
        """;

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<HttpRequestMessage> CopyAsync(HttpRequestMessage request)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (request.Content is not null)
        {
            copy.Content = new StringContent(
                await request.Content.ReadAsStringAsync(),
                Encoding.UTF8,
                request.Content.Headers.ContentType?.MediaType ?? "text/plain");
            foreach (var header in request.Content.Headers)
                copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return copy;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
            _handler = request => Task.FromResult(handler(request));

        public RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _handler(request);
    }
}
