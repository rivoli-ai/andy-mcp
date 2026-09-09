using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Andy.MCP.AspNetCore;
using Andy.MCP.Containers;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Andy.MCP.Tests.Containers;

public class ContainerMcpTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public WebApplication App = null!;
        public HttpClient Http = null!;
        public readonly Guid Id = Guid.NewGuid();
        public string Status = "Running";
        public JsonElement Created;
        public bool Stopped, Destroyed;
        public bool HasPort = true;
        public bool MissingTemplate, McpUnavailable;
        public int ContainerCount = 1;
        public ContainerMcpOptions Options = null!;
        public readonly List<string> Requests = [];
        public ContainerMcpServerProvider Provider => new(Http, Options);

        public static async Task<Fixture> StartAsync()
        {
            var fixture = new Fixture();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            fixture.App = builder.Build();
            fixture.App.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/mcp"))
                {
                    Assert.False(context.Request.Headers.ContainsKey("Authorization"));
                    if (fixture.McpUnavailable) { context.Response.StatusCode = 503; return; }
                }
                else Assert.Equal("Bearer control-token", context.Request.Headers.Authorization.ToString());
                await next();
            });
            object Container() => new { id = fixture.Id, name = "test", status = fixture.Status };
            fixture.App.MapPost("/api/containers/", async (HttpContext context) =>
            {
                if (fixture.MissingTemplate) return Results.NotFound(new { error = "Template not found" });
                fixture.Created = (await context.Request.ReadFromJsonAsync<JsonElement>()).Clone();
                return Results.Json(Container(), statusCode: 201);
            });
            fixture.App.MapGet("/api/containers/{id:guid}", () => fixture.Destroyed ? Results.NotFound() : Results.Json(Container()));
            fixture.App.MapGet("/api/containers/", (HttpContext context) =>
            {
                fixture.Requests.Add(context.Request.QueryString.Value!);
                var skip = int.Parse(context.Request.Query["skip"].ToString());
                var items = skip < fixture.ContainerCount
                    ? new[] { new { id = skip == 0 ? fixture.Id : Guid.Parse($"00000000-0000-0000-0000-{skip:D12}"), name = "test", status = fixture.Status } }
                    : [];
                return Results.Json(new { items, totalCount = fixture.ContainerCount });
            });
            fixture.App.MapGet("/api/containers/{id:guid}/connection", () => Results.Json(new
            {
                ipAddress = "172.17.0.2",
                portMappings = fixture.HasPort ? new Dictionary<int, int> { [3000] = new Uri(fixture.App.Urls.Single()).Port } : []
            }));
            fixture.App.MapPost("/api/containers/{id:guid}/start", () => { fixture.Status = "Running"; return Results.NoContent(); });
            fixture.App.MapPost("/api/containers/{id:guid}/stop", () => { fixture.Stopped = true; return Results.NoContent(); });
            fixture.App.MapDelete("/api/containers/{id:guid}", () => { fixture.Destroyed = true; return Results.NoContent(); });
            fixture.App.MapMcp("/mcp", server => server.AddTool("echo", "Echo", (_, _) => Task.FromResult(CallToolResult.Text("container result"))), new StreamableHttpServerOptions { AllowAnonymous = true });
            await fixture.App.StartAsync();
            fixture.Http = new HttpClient();
            fixture.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "control-token");
            fixture.Options = new ContainerMcpOptions
            {
                ApiUri = new Uri(fixture.App.Urls.Single()),
                PollInterval = TimeSpan.FromMilliseconds(10),
                RequestTimeout = TimeSpan.FromSeconds(3),
                ProvisionTimeout = TimeSpan.FromSeconds(5),
                IdleTimeout = TimeSpan.FromMilliseconds(50)
            };
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    [Fact]
    public async Task ProvisionMapsOptionsConnectsExecutesAndProtectsActiveSessionsFromCleanup()
    {
        await using var fixture = await Fixture.StartAsync();
        var registered = false;
        var unregistered = false;
        fixture.Options.RegisterAsync = (server, _) => { registered = server.Id == fixture.Id; return Task.CompletedTask; };
        fixture.Options.UnregisterAsync = (id, _) => { unregistered = id == fixture.Id; return Task.CompletedTask; };
        var provider = fixture.Provider;
        var workspace = Guid.NewGuid();
        var server = await provider.ProvisionAsync("mcp-server", new()
        {
            Name = "test",
            WorkspaceId = workspace,
            ProviderCode = "docker",
            Resources = new(1, 512, 5),
            EnvironmentVariables = new() { ["MODE"] = "test" },
            ExpiresAfter = TimeSpan.FromHours(1)
        });
        Assert.True(server.IsHealthy);
        Assert.True(registered);
        Assert.Equal("mcp-server", fixture.Created.GetProperty("templateCode").GetString());
        Assert.Equal(workspace, fixture.Created.GetProperty("workspaceId").GetGuid());
        Assert.Equal(512, fixture.Created.GetProperty("resources").GetProperty("memoryMb").GetInt32());
        Assert.Equal("test", fixture.Created.GetProperty("environmentVariables").GetProperty("MODE").GetString());
        Assert.True(await provider.GetHealthAsync(server.Id));
        var session = await provider.OpenSessionAsync(server.Id);
        var result = await session.Client.CallToolAsync("echo");
        Assert.Equal("container result", ((TextContent)result.Content[0]).Text);
        await Task.Delay(80);
        await provider.CleanupIdleAsync();
        Assert.False(fixture.Destroyed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.DestroyAsync(server.Id));
        await session.DisposeAsync();
        await session.DisposeAsync();
        await Task.Delay(80);
        await provider.CleanupIdleAsync();
        Assert.True(fixture.Stopped);
        Assert.True(fixture.Destroyed);
        Assert.True(unregistered);
        Assert.False(await provider.GetHealthAsync(server.Id));
    }

    [Theory]
    [InlineData("Failed", true)]
    [InlineData("Running", false)]
    public async Task FailedProvisioningOrMissingPortCleansUp(string status, bool hasPort)
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Status = status;
        fixture.HasPort = hasPort;
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.Provider.ProvisionAsync("mcp", new() { Name = "test" }));
        Assert.True(fixture.Stopped);
        Assert.True(fixture.Destroyed);
    }

    [Fact]
    public async Task CancelledProvisioningUsesIndependentCleanupToken()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Status = "Creating";
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Provider.ProvisionAsync("mcp", new() { Name = "test" }, stop.Token));
        Assert.True(fixture.Destroyed);
    }

    [Fact]
    public async Task StartsStoppedContainerAndFiltersRunningList()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Status = "Stopped";
        var provider = fixture.Provider;
        var created = await provider.ProvisionAsync("mcp", new() { Name = "test" });
        Assert.True(created.IsHealthy);
        var workspace = Guid.NewGuid();
        var list = await provider.ListRunningAsync(new(workspace, "owner with spaces"));
        Assert.Single(list);
        Assert.Contains("status=Running", fixture.Requests.Single());
        Assert.Contains("workspaceId=" + workspace, fixture.Requests.Single());
        Assert.Contains("ownerId=owner%20with%20spaces", fixture.Requests.Single());
        fixture.Status = "Stopped";
        Assert.False(await provider.GetHealthAsync(created.Id));
        await provider.DestroyAsync(created.Id);
    }

    [Fact]
    public async Task FailedUnregistrationStillDestroysAndNonMcpContainersAreExcluded()
    {
        await using var fixture = await Fixture.StartAsync();
        var provider = fixture.Provider;
        var server = await provider.ProvisionAsync("mcp", new() { Name = "test" });
        fixture.Options.UnregisterAsync = (_, _) => throw new InvalidOperationException("registry unavailable");
        await Assert.ThrowsAsync<AggregateException>(() => provider.DestroyAsync(server.Id));
        Assert.True(fixture.Stopped);
        Assert.True(fixture.Destroyed);
        fixture.HasPort = false;
        Assert.Empty(await provider.ListRunningAsync());
    }

    [Fact]
    public async Task HealthPollingClosesStoppedContainerSessionsAndAllowsRestart()
    {
        await using var fixture = await Fixture.StartAsync();
        var provider = fixture.Provider;
        var server = await provider.ProvisionAsync("mcp", new() { Name = "test" });
        await using var first = await provider.OpenSessionAsync(server.Id);
        fixture.Status = "Stopped";
        await provider.CleanupIdleAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.Client.PingAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenSessionAsync(server.Id));
        fixture.Status = "Running";
        await using var restarted = await provider.OpenSessionAsync(server.Id);
        Assert.NotSame(first.Client, restarted.Client);
        Assert.Equal("container result", ((TextContent)(await restarted.Client.CallToolAsync("echo")).Content[0]).Text);
        await restarted.DisposeAsync();
        await provider.DestroyAsync(server.Id);
    }

    [Fact]
    public async Task TransportDisconnectReleasesLeaseOnlyOnce()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (transport, serverTransport) = InMemoryTransport.CreatePair();
        await using var server = new Andy.MCP.Server.McpServer(serverTransport);
        var running = server.RunAsync(timeout.Token);
        var client = await Andy.MCP.Client.McpClient.ConnectAsync(transport, cancellationToken: timeout.Token);
        var releases = 0;
        var lease = new ContainerMcpSession(client, () => Interlocked.Increment(ref releases));
        transport.SimulateDisconnect();
        Assert.Equal(1, Volatile.Read(ref releases));
        await lease.DisposeAsync();
        Assert.Equal(1, Volatile.Read(ref releases));
        await timeout.CancelAsync();
        try { await running; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task MissingTemplateReturnsNotFoundWithoutDestroyingAnything()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.MissingTemplate = true;
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Provider.ProvisionAsync("missing", new() { Name = "test" }));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, error.StatusCode);
        Assert.False(fixture.Stopped);
        Assert.False(fixture.Destroyed);
    }

    [Fact]
    public async Task DiscoveryFollowsPagesAndFiltersTemplateAndHandlesEmptyCatalog()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.ContainerCount = 3;
        var template = Guid.NewGuid();
        var list = await fixture.Provider.ListRunningAsync(new(TemplateId: template));
        Assert.Equal(3, list.Count);
        Assert.Equal(3, list.Select(s => s.Id).Distinct().Count());
        Assert.All(fixture.Requests, q => Assert.Contains("templateId=" + template, q));
        Assert.Contains("skip=2", fixture.Requests.Last());
        fixture.ContainerCount = 0;
        Assert.Empty(await fixture.Provider.ListRunningAsync());
    }

    [Fact]
    public async Task HostedHealthPollingClosesSessionsWhenRunningMcpApplicationFails()
    {
        await using var fixture = await Fixture.StartAsync();
        var services = new ServiceCollection().AddLogging();
        services.AddContainerMcpServers(fixture.Options, _ => fixture.Http);
        await using var servicesProvider = services.BuildServiceProvider();
        var provider = servicesProvider.GetRequiredService<IContainerMcpServerProvider>();
        var server = await provider.ProvisionAsync("mcp", new() { Name = "test" });
        await using var session = await provider.OpenSessionAsync(server.Id);
        fixture.McpUnavailable = true;
        Assert.False(await provider.GetHealthAsync(server.Id));
        var cleanup = Assert.Single(servicesProvider.GetServices<IHostedService>());
        await cleanup.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (!fixture.Destroyed) await Task.Delay(10, timeout.Token);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.Client.PingAsync());
        }
        finally { await cleanup.StopAsync(CancellationToken.None); }
        // The control-plane HttpClient is caller-owned and remains usable after service shutdown.
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await fixture.Http.GetAsync(new Uri(fixture.Options.ApiUri, "api/containers/" + server.Id))).StatusCode);
    }

    [Fact]
    public async Task PublicTransportFactoryConnectsToResolvedEndpoint()
    {
        await using var fixture = await Fixture.StartAsync();
        var transport = ContainerMcpServerProvider.CreateTransport(new Uri(fixture.Options.ApiUri, "/mcp"));
        await using var client = await Andy.MCP.Client.McpClient.ConnectAsync(transport);
        await client.PingAsync();
        Assert.Throws<ArgumentException>(() => ContainerMcpServerProvider.CreateTransport(new Uri("file:///tmp/mcp")));
    }

    [Fact]
    public async Task RegistrationFailureCleansUpCreatedContainer()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Options.RegisterAsync = (_, _) => throw new InvalidOperationException("registry unavailable");
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.Provider.ProvisionAsync("mcp", new() { Name = "test" }));
        Assert.True(fixture.Destroyed);
    }
}
