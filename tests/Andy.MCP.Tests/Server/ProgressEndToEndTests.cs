using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Server;

/// <summary>
/// Tests that progress flows end-to-end (issue #43): a tool handler reports progress via
/// IProgress, the server forwards notifications/progress keyed to the request's progress token,
/// and the client routes them to the caller's IProgress. Progress must increase monotonically.
/// </summary>
public class ProgressEndToEndTests
{
    private sealed class Collector : IProgress<McpProgress>
    {
        private readonly double _completeAt;
        private readonly TaskCompletionSource _done = new();
        public List<McpProgress> Reports { get; } = new();
        public Task Done => _done.Task;

        public Collector(double completeAt) => _completeAt = completeAt;

        public void Report(McpProgress value)
        {
            lock (Reports)
                Reports.Add(value);
            if (value.Progress >= _completeAt)
                _done.TrySetResult();
        }
    }

    private static async Task<McpClient> ConnectAsync(Action<McpServer> configure, CancellationToken ct)
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new McpServer(serverTransport);
        configure(server);
        _ = server.RunAsync(ct);
        return await McpClient.ConnectAsync(clientTransport, cancellationToken: ct);
    }

    [Fact]
    public async Task ToolProgress_FlowsToClientCaller()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await ConnectAsync(s =>
            s.AddTool("work", "does work", async (JsonElement? _, IProgress<McpProgress> progress, CancellationToken _) =>
            {
                progress.Report(new McpProgress(0.25, 1.0, "quarter"));
                progress.Report(new McpProgress(0.5, 1.0, "half"));
                progress.Report(new McpProgress(1.0, 1.0, "done"));
                await Task.Yield();
                return CallToolResult.Text("finished");
            }), cts.Token);

        var collector = new Collector(1.0);
        var result = await client.CallToolAsync("work", null, collector, cts.Token);
        await collector.Done.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("finished", ((TextContent)result.Content[0]).Text);
        List<McpProgress> reports;
        lock (collector.Reports)
            reports = collector.Reports.ToList();

        Assert.Equal(new[] { 0.25, 0.5, 1.0 }, reports.Select(r => r.Progress));
        Assert.Equal("half", reports[1].Message);
        Assert.Equal(1.0, reports[2].Total);
    }

    [Fact]
    public async Task NonMonotonicProgress_IsDropped()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await ConnectAsync(s =>
            s.AddTool("work", "does work", async (JsonElement? _, IProgress<McpProgress> progress, CancellationToken _) =>
            {
                progress.Report(new McpProgress(0.5, 1.0, "half"));
                progress.Report(new McpProgress(0.25, 1.0, "backwards")); // must be dropped
                progress.Report(new McpProgress(1.0, 1.0, "done"));
                await Task.Yield();
                return CallToolResult.Text("finished");
            }), cts.Token);

        var collector = new Collector(1.0);
        await client.CallToolAsync("work", null, collector, cts.Token);
        await collector.Done.WaitAsync(TimeSpan.FromSeconds(5));

        List<McpProgress> reports;
        lock (collector.Reports)
            reports = collector.Reports.ToList();

        Assert.Equal(new[] { 0.5, 1.0 }, reports.Select(r => r.Progress));
    }

    [Fact]
    public async Task NoProgressRequested_ToolStillCompletes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await ConnectAsync(s =>
            s.AddTool("work", "does work", (JsonElement? _, IProgress<McpProgress> progress, CancellationToken _) =>
            {
                progress.Report(new McpProgress(0.5, 1.0, "half")); // discarded: no token on the request
                return Task.FromResult(CallToolResult.Text("finished"));
            }), cts.Token);

        var result = await client.CallToolAsync("work", null, cts.Token);
        Assert.Equal("finished", ((TextContent)result.Content[0]).Text);
    }
}
