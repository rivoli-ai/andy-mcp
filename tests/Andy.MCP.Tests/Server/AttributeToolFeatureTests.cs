using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Server;

public class ProgressTools
{
    [McpTool(Description = "reports progress")]
    public async Task<string> Work(IProgress<McpProgress> progress, CancellationToken ct)
    {
        progress.Report(new McpProgress(0.5, 1.0, "half"));
        progress.Report(new McpProgress(1.0, 1.0, "done"));
        await Task.Yield();
        return "finished";
    }
}

public class CountingTools
{
    public static int Instances;
    public CountingTools() => Interlocked.Increment(ref Instances);

    [McpTool] public string A() => "a";
    [McpTool] public string B() => "b";
    [McpTool] public string C() => "c";
}

public class ArraySchemaTools
{
    [McpTool]
    public string Tag([McpParam(Description = "the tags")] string[] tags) => string.Join(",", tags);
}

/// <summary>
/// Tests attribute-based registration improvements (issue #75): functional progress injection,
/// a single instance per type, and array parameter schemas.
/// </summary>
public class AttributeToolFeatureTests
{
    private sealed class Collector : IProgress<McpProgress>
    {
        private readonly TaskCompletionSource _done = new();
        public List<double> Values { get; } = new();
        public Task Done => _done.Task;

        public void Report(McpProgress value)
        {
            lock (Values)
                Values.Add(value.Progress);
            if (value.Progress >= 1.0)
                _done.TrySetResult();
        }
    }

    [Fact]
    public async Task Progress_IsInjected_IntoAttributeToolHandlers()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new McpServer(serverTransport);
        server.AddToolsFromType<ProgressTools>();
        _ = server.RunAsync(cts.Token);
        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);

        var collector = new Collector();
        var result = await client.CallToolAsync("work", null, collector, cts.Token);
        await collector.Done.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("finished", ((TextContent)result.Content[0]).Text);
        List<double> values;
        lock (collector.Values)
            values = collector.Values.ToList();
        Assert.Equal(new[] { 0.5, 1.0 }, values);
    }

    [Fact]
    public void OneInstance_PerType_NotPerMethod()
    {
        var before = CountingTools.Instances;
        var (_, serverTransport) = InMemoryTransport.CreatePair();
        new McpServer(serverTransport).AddToolsFromType<CountingTools>();

        // Three tool methods, but exactly one instance is constructed.
        Assert.Equal(1, CountingTools.Instances - before);
    }

    [Fact]
    public async Task ArrayParameter_GeneratesItemsSchema()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new McpServer(serverTransport);
        server.AddToolsFromType<ArraySchemaTools>();
        _ = server.RunAsync(cts.Token);
        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);

        var tool = Assert.Single(await client.ListToolsAsync(cts.Token));
        var tags = tool.InputSchema.GetProperty("properties").GetProperty("tags");
        Assert.Equal("array", tags.GetProperty("type").GetString());
        Assert.Equal("string", tags.GetProperty("items").GetProperty("type").GetString());
    }
}
