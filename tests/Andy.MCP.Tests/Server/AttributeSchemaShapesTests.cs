using System.ComponentModel;
using System.Text.Json.Serialization;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Server;

public class AttributeSchemaShapesTests
{
    public enum Mode { First, Second }
    public sealed record Payload
    {
        [JsonPropertyName("display_name")]
        [Description("Visible name")]
        public required string Name { get; init; }
        public IReadOnlyList<int> Values { get; init; } = [];
    }
    public sealed class Tools
    {
        [McpTool]
        public string Save(Payload payload, IEnumerable<string> tags, string requiredText, string? optionalText, Mode mode = Mode.Second) =>
            $"{payload.Name}:{string.Join(',', tags)}:{requiredText}:{optionalText}:{mode}";
    }

    [Fact]
    public async Task ExportedShapes_UseJsonNames_Nullability_Collections_AndDefaults()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(serverTransport);
        server.AddToolsFromType<Tools>();
        var running = server.RunAsync(cts.Token);
        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);
        var tool = Assert.Single(await client.ListToolsAsync(cts.Token));
        var props = tool.InputSchema.GetProperty("properties");
        var payload = props.GetProperty("payload");
        Assert.True(payload.GetProperty("properties").TryGetProperty("display_name", out var name));
        Assert.Equal("Visible name", name.GetProperty("description").GetString());
        Assert.Contains(payload.GetProperty("required").EnumerateArray(), p => p.GetString() == "display_name");
        Assert.Equal("array", props.GetProperty("tags").GetProperty("type").GetString());
        var required = tool.InputSchema.GetProperty("required").EnumerateArray().Select(v => v.GetString()).ToArray();
        Assert.Contains("requiredText", required);
        Assert.DoesNotContain("optionalText", required);
        Assert.DoesNotContain("mode", required);
        Assert.Equal("Second", props.GetProperty("mode").GetProperty("default").GetString());

        var result = await client.CallToolAsync("save", new
        {
            payload = new { display_name = "Alice", values = new[] { 1, 2 } },
            tags = new[] { "one", "two" },
            requiredText = "required",
            optionalText = (string?)null,
            mode = "First"
        }, cts.Token);
        Assert.False(result.IsError ?? false);
        Assert.Equal("Alice:one,two:required::First", ((TextContent)result.Content[0]).Text);
        cts.Cancel();
        await running;
    }
}
