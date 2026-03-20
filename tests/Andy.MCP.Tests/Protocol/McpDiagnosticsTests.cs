using System.Diagnostics;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Protocol;

[Collection("Diagnostics")] // Prevent parallel execution with other diagnostics tests
public class McpDiagnosticsTests
{
    [Fact]
    public void Source_HasCorrectName()
    {
        Assert.Equal("Andy.MCP", McpDiagnostics.SourceName);
        Assert.Equal("Andy.MCP", McpDiagnostics.Source.Name);
    }

    [Fact]
    public void Source_HasVersion()
    {
        Assert.NotNull(McpDiagnostics.Source.Version);
        Assert.NotEmpty(McpDiagnostics.Source.Version!);
    }

    [Fact]
    public void NoListener_ReturnsNull()
    {
        // Without an ActivityListener, StartActivity returns null (zero cost)
        var activity = McpDiagnostics.StartClientRequest("ping", (RequestId)1);
        Assert.Null(activity);
    }

    [Fact]
    public void WithListener_CreatesClientActivity()
    {
        using var listener = CreateListener();

        var activity = McpDiagnostics.StartClientRequest("tools/call", (RequestId)42, "TestServer", "2025-06-18");
        Assert.NotNull(activity);

        Assert.Equal("mcp.tools/call", activity!.DisplayName);
        Assert.Equal(ActivityKind.Client, activity.Kind);
        Assert.Equal("tools/call", activity.GetTagItem("mcp.method"));
        Assert.Equal("42", activity.GetTagItem("mcp.request_id"));
        Assert.Equal("TestServer", activity.GetTagItem("mcp.server.name"));
        Assert.Equal("2025-06-18", activity.GetTagItem("mcp.protocol_version"));

        activity.Dispose();
    }

    [Fact]
    public void WithListener_CreatesServerActivity()
    {
        using var listener = CreateListener();

        var activity = McpDiagnostics.StartServerRequest("resources/read", (RequestId)"req-5");
        Assert.NotNull(activity);

        Assert.Equal("mcp.resources/read", activity!.DisplayName);
        Assert.Equal(ActivityKind.Server, activity.Kind);
        Assert.Equal("resources/read", activity.GetTagItem("mcp.method"));
        Assert.Equal("req-5", activity.GetTagItem("mcp.request_id"));

        activity.Dispose();
    }

    [Fact]
    public void SetSuccess_SetsOkStatus()
    {
        using var listener = CreateListener();
        var activity = McpDiagnostics.StartClientRequest("ping", (RequestId)1);

        McpDiagnostics.SetSuccess(activity);

        Assert.Equal(ActivityStatusCode.Ok, activity!.Status);
        activity.Dispose();
    }

    [Fact]
    public void SetError_SetsErrorStatus()
    {
        using var listener = CreateListener();
        var activity = McpDiagnostics.StartClientRequest("tools/call", (RequestId)1);

        var ex = new McpException(-32602, "Unknown tool");
        McpDiagnostics.SetError(activity, ex, -32602);

        Assert.Equal(ActivityStatusCode.Error, activity!.Status);
        Assert.Equal(-32602, activity.GetTagItem("mcp.error_code"));
        Assert.Contains(activity.Events, e => e.Name == "exception");

        activity.Dispose();
    }

    [Fact]
    public void SetError_NullActivity_NoOp()
    {
        // Should not throw
        McpDiagnostics.SetError(null, new Exception("test"));
        McpDiagnostics.SetSuccess(null);
    }

    [Fact]
    public async Task ClientIntegration_SpansCaptured()
    {
        var activities = new List<Activity>();
        using var listener = CreateListener(a => { lock (activities) activities.Add(a); });

        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.AddTool("test", "Test tool", (a, c) => Task.FromResult(CallToolResult.Text("ok")));
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);
        await client.PingAsync(cts.Token);
        await client.CallToolAsync("test", ct: cts.Token);

        // Allow spans to flush
        await Task.Delay(50);

        lock (activities)
        {
            Assert.Contains(activities, a => a.DisplayName == "mcp.initialize" && a.Kind == ActivityKind.Client);
            Assert.Contains(activities, a => a.DisplayName == "mcp.ping" && a.Kind == ActivityKind.Client);
            Assert.Contains(activities, a => a.DisplayName == "mcp.tools/call" && a.Kind == ActivityKind.Client);
            Assert.Contains(activities, a => a.DisplayName == "mcp.initialize" && a.Kind == ActivityKind.Server);
            Assert.Contains(activities, a => a.DisplayName == "mcp.ping" && a.Kind == ActivityKind.Server);
            Assert.Contains(activities, a => a.DisplayName == "mcp.tools/call" && a.Kind == ActivityKind.Server);
        }
    }

    [Fact]
    public async Task ServerIntegration_ToolNameAttribute()
    {
        var activities = new List<Activity>();
        using var listener = CreateListener(a => activities.Add(a));

        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.AddTool("my_tool", "A tool", (a, c) => Task.FromResult(CallToolResult.Text("done")));
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);
        await client.CallToolAsync("my_tool", ct: cts.Token);

        var serverSpan = activities.First(a =>
            a.DisplayName == "mcp.tools/call" && a.Kind == ActivityKind.Server);
        Assert.Equal("my_tool", serverSpan.GetTagItem("mcp.tool.name"));
    }

    [Fact]
    public async Task ServerIntegration_ErrorSpan()
    {
        var activities = new List<Activity>();
        using var listener = CreateListener(a => activities.Add(a));

        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.AddTool("tool", "t", (a, c) => Task.FromResult(CallToolResult.Text("ok")));
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);

        try { await client.CallToolAsync("nonexistent", ct: cts.Token); } catch { }

        var serverSpan = activities.FirstOrDefault(a =>
            a.DisplayName == "mcp.tools/call" && a.Kind == ActivityKind.Server);
        Assert.NotNull(serverSpan);
        Assert.Equal(ActivityStatusCode.Error, serverSpan!.Status);
    }

    [Fact]
    public async Task ConcurrentCalls_DistinctSpans()
    {
        var activities = new List<Activity>();
        using var listener = CreateListener(a =>
        {
            lock (activities) activities.Add(a);
        });

        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.AddTool("concurrent_echo", "e", (a, c) => Task.FromResult(CallToolResult.Text("ok")));
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => client.CallToolAsync("concurrent_echo", ct: cts.Token))
            .ToArray();
        await Task.WhenAll(tasks);

        await Task.Delay(100);

        List<Activity> clientSpans;
        lock (activities)
        {
            clientSpans = activities.Where(a =>
                a.DisplayName == "mcp.tools/call" && a.Kind == ActivityKind.Client).ToList();
        }
        Assert.True(clientSpans.Count >= 5, $"Expected >=5 client tool spans, got {clientSpans.Count}");

        var ids = clientSpans.Select(a => a.GetTagItem("mcp.request_id")).Distinct().ToList();
        Assert.True(ids.Count >= 5, $"Expected >=5 distinct IDs, got {ids.Count}");
    }

    private static ActivityListener CreateListener(Action<Activity>? onStopped = null)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Andy.MCP",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => onStopped?.Invoke(activity)
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
