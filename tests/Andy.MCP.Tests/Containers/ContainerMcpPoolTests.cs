using Andy.MCP.Client;
using Andy.MCP.Containers;
using Andy.MCP.Protocol;
using Andy.MCP.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Andy.MCP.Tests.Containers;

public class ContainerMcpPoolTests
{
    private sealed class Provider : IContainerMcpServerProvider, IAsyncDisposable
    {
        public readonly Dictionary<Guid, ContainerMcpServer> Servers = [];
        public readonly Dictionary<Guid, int> Active = [];
        public readonly HashSet<Guid> Unhealthy = [];
        public readonly List<string> Names = [];
        public bool FailSession;
        private readonly List<McpServer> _servers = [];
        private readonly List<Task> _running = [];
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(20));

        public Task<ContainerMcpServer> ProvisionAsync(string templateCode, ContainerMcpProvisionOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("mcp", templateCode);
            Names.Add(options.Name);
            var id = Guid.NewGuid();
            var server = new ContainerMcpServer(id, options.Name, new Uri("http://localhost/mcp"), "Running", true);
            Servers.Add(id, server);
            Active.Add(id, 0);
            return Task.FromResult(server);
        }
        public async Task<ContainerMcpSession> OpenSessionAsync(Guid id, CancellationToken cancellationToken = default)
        {
            if (FailSession) throw new InvalidOperationException("session failed");
            var (transport, serverTransport) = InMemoryTransport.CreatePair();
            var server = new McpServer(serverTransport);
            server.AddTool("id", "Container id", (_, _) => Task.FromResult(CallToolResult.Text(id.ToString())));
            _servers.Add(server);
            _running.Add(server.RunAsync(_stop.Token));
            var client = await McpClient.ConnectAsync(transport, cancellationToken: cancellationToken);
            Active[id]++;
            return new(client, () => Active[id]--);
        }
        public Task DestroyAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Assert.Equal(0, Active[id]);
            Servers.Remove(id);
            return Task.CompletedTask;
        }
        public Task<bool> GetHealthAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Servers.ContainsKey(id) && !Unhealthy.Contains(id));
        public Task<IReadOnlyList<ContainerMcpServer>> ListRunningAsync(ContainerMcpFilter? filter = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ContainerMcpServer>>(Servers.Values.ToArray());
        public Task CleanupIdleAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            foreach (var server in _servers) await server.DisposeAsync();
            foreach (var running in _running) { try { await running; } catch (OperationCanceledException) { } }
            _stop.Dispose();
        }
    }

    private static ContainerMcpPoolOptions Options(int minimum = 2, int maximum = 3) => new()
    {
        TemplateCode = "mcp",
        ProvisionOptions = new() { Name = "pool" },
        MinimumSize = minimum,
        MaximumSize = maximum,
        IdleTimeout = TimeSpan.FromMilliseconds(30),
        PollInterval = TimeSpan.FromMilliseconds(10)
    };

    [Fact]
    public async Task WarmsGrowsAtDemandBoundsCapacityAndScalesDownOnlyIdleContainers()
    {
        await using var provider = new Provider();
        await using var pool = new ContainerMcpPool(provider, Options());
        await pool.WarmAsync();
        Assert.Equal(2, provider.Servers.Count);
        await using var first = await pool.RentAsync();
        await using var second = await pool.RentAsync();
        await using var third = await pool.RentAsync();
        Assert.Equal(3, provider.Servers.Count);
        Assert.Equal(3, provider.Names.Distinct().Count());
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.RentAsync());
        Assert.Equal(third.Server.Id.ToString(), ((TextContent)(await third.Client.CallToolAsync("id")).Content[0]).Text);
        await first.DisposeAsync();
        await first.DisposeAsync();
        await second.DisposeAsync();
        await Task.Delay(60);
        await pool.MaintainAsync();
        Assert.Equal(2, provider.Servers.Count);
        Assert.Contains(third.Server.Id, provider.Servers.Keys);
        Assert.All(provider.Servers.Keys, id => Assert.Equal(1, provider.Active[id]));
        await pool.DisposeAsync();
        Assert.Empty(provider.Servers);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => third.Client.PingAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.RentAsync());
    }

    [Fact]
    public async Task ReturnedCapacityGetsFreshSessionAndUnhealthyIdleContainersAreReplaced()
    {
        await using var provider = new Provider();
        await using var pool = new ContainerMcpPool(provider, Options(1, 1));
        await using var first = await pool.RentAsync();
        await first.DisposeAsync();
        await using var next = await pool.RentAsync();
        Assert.Equal(first.Server.Id, next.Server.Id);
        Assert.NotSame(first.Client, next.Client);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.Client.PingAsync());
        await next.DisposeAsync();
        provider.Unhealthy.Add(next.Server.Id);
        await pool.MaintainAsync();
        Assert.Single(provider.Servers);
        Assert.DoesNotContain(next.Server.Id, provider.Servers.Keys);
        await using var replacement = await pool.RentAsync();
        Assert.NotEqual(next.Server.Id, replacement.Server.Id);
    }

    [Fact]
    public async Task FailedSessionCreationReclaimsContainerAndCancellationDoesNotAllocate()
    {
        await using var provider = new Provider { FailSession = true };
        await using var pool = new ContainerMcpPool(provider, Options(1, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.WarmAsync());
        Assert.Empty(provider.Servers);
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.RentAsync(stop.Token));
        Assert.Single(provider.Names);
    }

    [Fact]
    public async Task HostedPoolWarmsCapacityAndDestroysItOnShutdown()
    {
        await using var provider = new Provider();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IContainerMcpServerProvider>(provider);
        services.AddContainerMcpPool(Options(1, 1));
        await using var servicesProvider = services.BuildServiceProvider();
        var pool = servicesProvider.GetRequiredService<ContainerMcpPool>();
        var hosted = Assert.Single(servicesProvider.GetServices<IHostedService>());
        await hosted.StartAsync(CancellationToken.None);
        // WarmAsync serializes with the initial hosted maintenance pass.
        await pool.WarmAsync();
        Assert.Single(provider.Servers);
        await hosted.StopAsync(CancellationToken.None);
        Assert.Empty(provider.Servers);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(2, 1)]
    [InlineData(0, 0)]
    public async Task RejectsInvalidPoolBounds(int minimum, int maximum)
    {
        await using var provider = new Provider();
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContainerMcpPool(provider, Options(minimum, maximum)));
    }
}
