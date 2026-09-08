using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;
namespace Andy.MCP.Tests.Server;

public class ExtensionApiTests
{
    private sealed class Observer : IProgress<McpProgress>
    {
        public readonly TaskCompletionSource Seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Report(McpProgress progress) { if (progress.Progress == 1) Seen.TrySetResult(); }
    }

    [Fact]
    public async Task Extensions_AreBidirectional_AndPreserveMetadataProgressAndNotifications()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (transport, peer) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(peer);
        server.AddCustomRequestHandler("vendor/echo", (parameters, progress, ct) =>
        {
            Assert.Equal("kept", parameters!.Value.GetProperty("_meta").GetProperty("vendor").GetString());
            progress.Report(new McpProgress(1, null, null));
            return Task.FromResult(McpJsonDefaults.ToElement(new { _meta = new { vendor = "result" }, value = parameters.Value.GetProperty("value").GetInt32() }));
        });
        var run = server.RunAsync(timeout.Token);
        await using var client = await McpClient.ConnectAsync(transport, new McpClientOptions
        {
            CustomRequestHandlers = new Dictionary<string, Func<JsonElement?, CancellationToken, Task<JsonElement>>>
            { ["vendor/client"] = (parameters, _) => Task.FromResult(parameters!.Value) }
        }, cancellationToken: timeout.Token);
        await client.PingAsync(timeout.Token);
        var progress = new Observer();
        var outgoing = client.RequestCustomAsync<JsonElement>("vendor/echo", new { value = 7, _meta = new { vendor = "kept" } }, new McpRequestOptions { Progress = progress }, timeout.Token);
        var incoming = server.RequestCustomAsync<JsonElement>("vendor/client", new { value = 8 }, ct: timeout.Token);
        await Task.WhenAll(outgoing, incoming);
        Assert.Equal("result", (await outgoing).GetProperty("_meta").GetProperty("vendor").GetString());
        Assert.Equal(8, (await incoming).GetProperty("value").GetInt32());
        await progress.Seen.Task.WaitAsync(timeout.Token);
        var serverNotice = new TaskCompletionSource<JsonRpcNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientNotice = new TaskCompletionSource<JsonRpcNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.CustomNotificationReceived += (_, notice) => serverNotice.TrySetResult(notice);
        client.CustomNotificationReceived += (_, notice) => clientNotice.TrySetResult(notice);
        await client.NotifyCustomAsync("vendor/changed", new { _meta = new { value = 1 } }, timeout.Token);
        await server.NotifyCustomAsync("vendor/changed", new { _meta = new { value = 2 } }, timeout.Token);
        Assert.Equal(1, (await serverNotice.Task.WaitAsync(timeout.Token)).Params!.Value.GetProperty("_meta").GetProperty("value").GetInt32());
        Assert.Equal(2, (await clientNotice.Task.WaitAsync(timeout.Token)).Params!.Value.GetProperty("_meta").GetProperty("value").GetInt32());
        timeout.Cancel(); await run;
    }

    [Fact]
    public async Task PerCallDeadline_CancelsHandler_WithoutChangingDefaultTimeout()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (transport, peer) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(peer);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AddCustomRequestHandler("vendor/wait", async (_, ct) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return McpJsonDefaults.ToElement(new { }); }
            finally { cancelled.TrySetResult(); }
        });
        server.AddCustomRequestHandler("vendor/invalid", (_, _) => Task.FromResult(McpJsonDefaults.ToElement("invalid scalar result")));
        Assert.Throws<ArgumentException>(() => server.AddCustomRequestHandler("tools/call", (_, _) => Task.FromResult(McpJsonDefaults.ToElement(new { }))));
        await Assert.ThrowsAsync<McpSessionException>(() => server.RequestCustomAsync<JsonElement>("vendor/client"));
        var run = server.RunAsync(timeout.Token);
        await using var client = await McpClient.ConnectAsync(transport, cancellationToken: timeout.Token);
        await Assert.ThrowsAsync<TimeoutException>(() => client.RequestCustomAsync<JsonElement>("vendor/wait", options: new McpRequestOptions { Timeout = TimeSpan.FromMilliseconds(75) }, ct: timeout.Token));
        await cancelled.Task.WaitAsync(timeout.Token);
        await client.PingAsync(timeout.Token);
        Assert.Equal(0, client.PendingRequestCount);
        await Assert.ThrowsAsync<ArgumentException>(() => client.RequestCustomAsync<JsonElement>("initialize", ct: timeout.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => client.NotifyCustomAsync("notifications/cancelled", ct: timeout.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => client.RequestCustomAsync<JsonElement>("vendor/echo", new[] { 1 }, ct: timeout.Token));
        Assert.Equal(McpErrorCodes.InternalError, (await Assert.ThrowsAsync<McpException>(() => client.RequestCustomAsync<JsonElement>("vendor/invalid", ct: timeout.Token))).ErrorCode);
        timeout.Cancel(); await run;
    }

    [Fact]
    public async Task PageAndTypedToolApis_PreserveCallerControlAndResultMetadata()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (transport, peer) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(peer, new McpServerOptions { PageSize = 1 });
        for (var i = 0; i < 3; i++)
            server.AddTool("tool" + i, "", (_, _) => Task.FromResult(new CallToolResult { Content = [new TextContent("ok")], Meta = McpJsonDefaults.ToElement(new { retained = true }) }));
        var run = server.RunAsync(timeout.Token);
        await using var client = await McpClient.ConnectAsync(transport, cancellationToken: timeout.Token);
        var first = await client.ListToolsPageAsync(ct: timeout.Token);
        Assert.Single(first.Tools); Assert.NotNull(first.NextCursor);
        var second = await client.ListToolsPageAsync(new PaginatedRequest { Cursor = first.NextCursor }, ct: timeout.Token);
        Assert.Single(second.Tools); Assert.NotEqual(first.Tools[0].Name, second.Tools[0].Name);
        var result = await client.CallToolAsync(new CallToolRequest { Name = first.Tools[0].Name }, new McpRequestOptions { Timeout = TimeSpan.FromSeconds(2) }, timeout.Token);
        Assert.True(result.Meta!.Value.GetProperty("retained").GetBoolean());
        timeout.Cancel(); await run;
    }
}
