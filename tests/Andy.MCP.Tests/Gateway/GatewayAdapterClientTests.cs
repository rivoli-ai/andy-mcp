using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Andy.MCP.AspNetCore;
using Andy.MCP.Client;
using Andy.MCP.Gateway;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Andy.MCP.Tests.Gateway;

public class GatewayAdapterClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    [Fact]
    public async Task TypedAdapterMethodsUseExpectedRoutesAndRefreshBearerOnce()
    {
        var id = Guid.NewGuid();
        var adapter = new McpAdapterDto { Id = id, Name = "demo", Url = "http://localhost/mcp" };
        var routes = new List<string>();
        var first = true;
        using var http = new HttpClient(new Handler(request =>
        {
            if (first) { first = false; Assert.Equal("expired", request.Headers.Authorization?.Parameter); return new(HttpStatusCode.Unauthorized); }
            Assert.Equal("fresh", request.Headers.Authorization?.Parameter);
            var path = request.RequestUri!.PathAndQuery;
            routes.Add(request.Method + " " + path);
            if (path.EndsWith("/health")) return Json(new AdapterHealthDto(id, "demo", adapter.Url, "healthy", DateTimeOffset.UtcNow, 1, null));
            if (path.EndsWith("/health-check")) return Json(Array.Empty<AdapterHealthDto>());
            if (request.Method == HttpMethod.Delete || path.EndsWith("/reload") || path.EndsWith("/import")) return new(HttpStatusCode.NoContent);
            if (path.EndsWith("/enabled") || path.Contains("/search?") || path.EndsWith("/export")) return Json(new[] { adapter });
            if (path.EndsWith("/adapters/") && request.Method == HttpMethod.Get) return Json(new AdapterListDto { Adapters = [adapter], Total = 1 });
            return Json(adapter);
        }));
        var refreshed = false;
        var options = new McpGatewayOptions { RegistryUri = new Uri("https://gateway.test"), TokenProvider = (refresh, _) => { refreshed |= refresh; return Task.FromResult<string?>(refreshed ? "fresh" : "expired"); } };
        var services = new ServiceCollection();
        services.AddMcpGateway(options.RegistryUri, configured => { configured.UseAdapterProxy = true; configured.TokenProvider = options.TokenProvider; }, _ => http);
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IMcpGatewayClient>();
        Assert.Equal(1, (await client.ListAdaptersAsync()).Total);
        Assert.Single(await client.ListEnabledAdaptersAsync());
        Assert.Equal(id, (await client.GetAdapterAsync(id)).Id);
        Assert.Equal("demo", (await client.GetAdapterByNameAsync("demo")).Name);
        Assert.Single(await client.SearchAdaptersAsync("with space", true));
        await client.CreateAdapterAsync(new() { Name = "demo", Url = adapter.Url });
        await client.UpdateAdapterAsync(id, new() { Name = "demo", Url = adapter.Url });
        Assert.Equal("healthy", (await client.GetAdapterHealthAsync(id)).Status);
        Assert.Empty(await client.CheckAdaptersHealthAsync());
        await client.DeleteAdapterAsync(id);
        await client.ReloadAdaptersAsync();
        Assert.Single(await client.ExportAdaptersAsync());
        await client.ImportAdaptersAsync([]);
        Assert.Equal(13, routes.Count);
        Assert.Contains("GET /api/adapters/search?name=with%20space&enabled=true", routes);
        Assert.Contains("PUT /api/adapters/" + id, routes);
        Assert.Contains("GET /api/adapters/name/demo", routes);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(503)]
    public async Task MapsAdapterErrorsAndRejectsMissingCredentials(int status)
    {
        using var http = new HttpClient(new Handler(_ => new((HttpStatusCode)status)));
        var options = new McpGatewayOptions { RegistryUri = new Uri("https://gateway.test") };
        using var client = new McpGatewayClient(http, options);
        Assert.Contains("No gateway bearer token", (await Assert.ThrowsAsync<McpGatewayException>(() => client.ListAdaptersAsync())).Message);
        options.TokenProvider = (_, _) => Task.FromResult<string?>("token");
        var error = await Assert.ThrowsAsync<McpGatewayException>(() => client.GetAdapterByNameAsync("demo"));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Contains("demo", error.Message);
    }

    [Fact]
    public async Task ProxyTransportRefreshesTokenAndExchangesMcpMessages()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var denied = 0;
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.Authorization != "Bearer fresh") { Interlocked.Increment(ref denied); context.Response.StatusCode = 401; return; }
            await next();
        });
        app.MapMcp("/adapters/demo/mcp", server => server.AddTool("echo", "Echo", (_, _) => Task.FromResult(CallToolResult.Text("proxied"))), new StreamableHttpServerOptions { AllowAnonymous = true });
        await app.StartAsync();
        try
        {
            var refreshed = false;
            var options = new McpGatewayOptions { RegistryUri = new Uri(app.Urls.Single()), TokenProvider = (refresh, _) => { refreshed |= refresh; return Task.FromResult<string?>(refreshed ? "fresh" : "expired"); } };
            await using var transport = new McpGatewayTransport(options, "demo");
            await using var client = await McpClient.ConnectAsync(transport);
            Assert.Equal("proxied", ((TextContent)(await client.CallToolAsync("echo")).Content[0]).Text);
            Assert.True(denied >= 1);
            Assert.True(refreshed);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task ProxyTransportRequiresCredentialsAndValidatesAdapterNames()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.Unauthorized)));
        var options = new McpGatewayOptions { RegistryUri = new Uri("http://gateway.test") };
        await using var transport = new McpGatewayTransport(options, "demo", httpClient: http);
        await transport.ConnectAsync();
        var error = await Assert.ThrowsAsync<McpGatewayException>(() => transport.SendAsync(new JsonRpcRequest { Id = 1, Method = "ping" }));
        Assert.Contains("No gateway bearer token", error.Message);
        Assert.Throws<ArgumentException>(() => McpGatewayTransport.GetProxyEndpoint(options.RegistryUri, "../other"));
        Assert.Throws<ArgumentOutOfRangeException>(() => McpGatewayTransport.GetProxyEndpoint(options.RegistryUri, "demo", (McpAdapterType)3));
    }
}
