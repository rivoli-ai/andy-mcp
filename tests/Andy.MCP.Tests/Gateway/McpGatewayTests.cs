using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Andy.MCP.AspNetCore;
using Andy.MCP.Configuration;
using Andy.MCP.Gateway;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Andy.MCP.Tests.Gateway;

public class McpGatewayTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handle(request, ct);
    }

    [Fact]
    public async Task TypedClientMatchesRegistryRoutesAndRefreshesBearerOnlyOnce()
    {
        var paths = new List<string>();
        var tokens = new List<bool>();
        var challenged = false;
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            paths.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            if (!challenged)
            {
                challenged = true;
                Assert.Equal("old", request.Headers.Authorization!.Parameter);
                return new(HttpStatusCode.Unauthorized);
            }
            if (paths.Count == 2) Assert.Equal("new", request.Headers.Authorization!.Parameter);
            if (request.Method == HttpMethod.Delete) return new(HttpStatusCode.NoContent);
            if (request.Content is not null) Assert.NotEmpty(await request.Content.ReadAsStringAsync(ct));
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(
                request.RequestUri!.AbsolutePath.EndsWith('/') || request.RequestUri.AbsolutePath.EndsWith("search")
                    ? (object)new[] { new GatewayRegistration { Id = "id", Name = "server" } }
                    : new GatewayRegistration { Id = "id", Name = "server" }) };
        }));
        var options = new McpGatewayOptions { RegistryUri = new Uri("https://registry.example/base"), TokenProvider = (refresh, _) => { tokens.Add(refresh); return Task.FromResult<string?>(refresh ? "new" : "old"); } };
        var client = new McpGatewayClient(http, options);
        Assert.Single(await client.ListAsync());
        Assert.Equal("id", (await client.GetAsync("id")).Id);
        Assert.Single(await client.SearchAsync(new GatewaySearch { SearchTerm = "server" }));
        await client.UpdateAsync("id", new GatewayRegistration { Name = "updated" });
        await client.DeleteAsync("id");
        Assert.Equal(new[] { false, true, false, false, false, false }, tokens);
        Assert.Contains("POST /base/api/GatewayRegistry/search", paths);
        Assert.Contains("PUT /base/api/GatewayRegistry/id", paths);
        Assert.Contains("DELETE /base/api/GatewayRegistry/id", paths);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(503)]
    public async Task HttpErrorsRetainStatusAndBoundAuthenticationRetries(int status)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)); }));
        var client = new McpGatewayClient(http, new McpGatewayOptions { RegistryUri = new("https://registry.example"), TokenProvider = (_, _) => Task.FromResult<string?>("token") });
        var error = await Assert.ThrowsAsync<McpGatewayException>(() => client.GetAsync("id"));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal(status == 401 ? 2 : 1, calls);
    }

    [Fact]
    public async Task CancellationAbortsRegistryRequest()
    {
        using var http = new HttpClient(new Handler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new(HttpStatusCode.OK); }));
        var client = new McpGatewayClient(http, new McpGatewayOptions { RegistryUri = new("https://registry.example") });
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListAsync(stop.Token));
    }

    [Fact]
    public async Task RealHttpRegistryDiscoveryConnectsPingsExecutesAndRemovesInactiveEndpoints()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var registrations = new List<GatewayRegistration>();
        var registryAvailable = true;
        app.MapGet("/api/GatewayRegistry/", () => registryAvailable ? Results.Json(registrations) : Results.StatusCode(503));
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/mcp")) Assert.False(context.Request.Headers.ContainsKey("Authorization"));
            await next();
        });
        app.MapMcp("/mcp", server => server.AddTool("echo", "Echo", (_, _) => Task.FromResult(CallToolResult.Text("gateway result"))), new StreamableHttpServerOptions { AllowAnonymous = true });
        await app.StartAsync(timeout.Token);
        var uri = new Uri(app.Urls.Single());
        registrations.Add(new() { Id = "one", Name = "echo", Endpoint = new Uri(uri, "/mcp").AbsoluteUri });
        using var http = new HttpClient();
        var options = new McpGatewayOptions { RegistryUri = uri, MaximumCacheAge = TimeSpan.FromMinutes(1), TokenProvider = (_, _) => Task.FromResult<string?>("registry-secret") };
        var registry = new McpGatewayClient(http, options);
        await using var manager = new McpConnectionManager(new());
        using var discovery = new McpGatewayDiscovery(registry, manager, options, NullLogger<McpGatewayDiscovery>.Instance);
        await discovery.RefreshAsync(timeout.Token);
        var client = manager.GetClient("gateway:one");
        Assert.NotNull(client);
        Assert.Single(await client.ListToolsAsync(timeout.Token));
        Assert.Equal("gateway result", ((TextContent)(await client.CallToolAsync("echo", ct: timeout.Token)).Content[0]).Text);
        Assert.True(await McpGatewayClient.CheckHealthAsync(registrations[0], TimeSpan.FromSeconds(3), timeout.Token));
        registryAvailable = false;
        await discovery.RefreshAsync(timeout.Token);
        Assert.Same(client, manager.GetClient("gateway:one"));
        registryAvailable = true;
        registrations[0] = registrations[0] with { Status = GatewayStatus.Maintenance };
        await discovery.RefreshAsync(timeout.Token);
        Assert.Empty(manager.ConnectedServers);
        registrations[0] = registrations[0] with { Status = GatewayStatus.Active };
        await discovery.RefreshAsync(timeout.Token);
        Assert.NotNull(manager.GetClient("gateway:one"));
        options.MaximumCacheAge = TimeSpan.Zero;
        registryAvailable = false;
        await discovery.RefreshAsync(timeout.Token);
        Assert.Empty(manager.ConnectedServers);
        registryAvailable = true;
        await manager.AddServerAsync(new McpServerConfig { Name = "configured", Transport = "gateway", GatewayUrl = uri.AbsoluteUri, AdapterName = "echo" }, timeout.Token);
        Assert.NotNull(manager.GetClient("configured"));
        await manager.DisconnectAllAsync();
        await app.StopAsync(timeout.Token);
    }
}
