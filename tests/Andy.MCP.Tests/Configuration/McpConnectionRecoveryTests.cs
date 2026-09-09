using Andy.MCP.Configuration;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Configuration;

public class McpConnectionRecoveryTests
{
    [Fact]
    public async Task DisconnectReconnectsAndRemovePreventsResurrection()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transports = new List<InMemoryClientTransport>();
        var servers = new List<McpServer>();
        var runs = new List<Task>();
        var attempts = 0;
        var options = new McpClientOptions { AutoReconnect = true, ReconnectPolicy = new() { BaseDelay = TimeSpan.FromMilliseconds(30), MaxDelay = TimeSpan.FromMilliseconds(30), MaxRetries = 3 } };
        await using var manager = new McpConnectionManager(options, null, _ =>
        {
            Interlocked.Increment(ref attempts);
            var (transport, serverTransport) = InMemoryTransport.CreatePair();
            transports.Add(transport);
            var server = new McpServer(serverTransport);
            server.AddTool("echo", "Echo", (_, _) => Task.FromResult(CallToolResult.Text("ok")));
            servers.Add(server);
            runs.Add(server.RunAsync(stop.Token));
            return transport;
        });
        await manager.AddServerAsync(new() { Name = "srv" }, stop.Token);
        var first = manager.GetClient("srv");
        transports[0].SimulateDisconnect();
        while (manager.GetClient("srv") is not { } client || ReferenceEquals(client, first)) await Task.Delay(5, stop.Token);
        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.Single(await manager.ListAllToolsAsync(stop.Token));
        transports[1].SimulateDisconnect();
        await manager.RemoveServerAsync("srv");
        await Task.Delay(100, stop.Token);
        Assert.Null(manager.GetClient("srv"));
        Assert.Equal(2, Volatile.Read(ref attempts));
        await stop.CancelAsync();
        foreach (var server in servers) await server.DisposeAsync();
        try { await Task.WhenAll(runs); } catch (OperationCanceledException) { }
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 3)]
    public async Task RecoveryHonorsRetryLimitAndDisabledSetting(bool reconnect, int expectedAttempts)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (transport, serverTransport) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(serverTransport);
        var running = server.RunAsync(stop.Token);
        var attempts = 0;
        await using var manager = new McpConnectionManager(new()
        {
            AutoReconnect = reconnect,
            ReconnectPolicy = new() { MaxRetries = 2, BaseDelay = TimeSpan.FromMilliseconds(5), MaxDelay = TimeSpan.FromMilliseconds(5) }
        }, null, _ => Interlocked.Increment(ref attempts) == 1 ? transport : throw new InvalidOperationException("offline"));
        await manager.AddServerAsync(new() { Name = "srv" }, stop.Token);
        transport.SimulateDisconnect();
        while (manager.GetClient("srv") is not null || Volatile.Read(ref attempts) < expectedAttempts) await Task.Delay(5, stop.Token);
        await Task.Delay(50, stop.Token);
        Assert.Equal(expectedAttempts, Volatile.Read(ref attempts));
        await manager.DisconnectAllAsync();
        await stop.CancelAsync();
        try { await running; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task CancellationIsNotSwallowedAndDisposedManagerRejectsConnections()
    {
        var options = new McpClientOptions();
        options.AddStdioServer("cancelled", "unused");
        var attempts = 0;
        var manager = new McpConnectionManager(options, null, _ => { attempts++; throw new InvalidOperationException(); });
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.ConnectAllAsync(stop.Token));
        Assert.Equal(0, attempts);
        await manager.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.AddServerAsync(new() { Name = "disposed" }));
        Assert.Equal(0, attempts);
    }
}
