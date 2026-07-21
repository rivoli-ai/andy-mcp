using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Server;

/// <summary>
/// Tests structured tool output and output-schema enforcement (issue #47).
/// </summary>
public class StructuredOutputTests
{
    private static readonly JsonElement OutputSchema = McpJsonDefaults.ToElement(new
    {
        type = "object",
        properties = new { sum = new { type = "integer" } },
        required = new[] { "sum" }
    });

    private static async Task<McpClient> ConnectAsync(Action<McpServer> configure, CancellationToken ct)
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new McpServer(serverTransport);
        configure(server);
        _ = server.RunAsync(ct);
        return await McpClient.ConnectAsync(clientTransport, cancellationToken: ct);
    }

    [Fact]
    public async Task OutputSchema_IsAdvertised_OnToolsList()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await ConnectAsync(s =>
            s.AddTool("add", "adds", McpJsonDefaults.ToElement(new { type = "object" }),
                (JsonElement? _, CancellationToken _) => Task.FromResult(CallToolResult.Text("ok")),
                outputSchema: OutputSchema), cts.Token);

        var tools = await client.ListToolsAsync(cts.Token);
        var tool = Assert.Single(tools);
        Assert.NotNull(tool.OutputSchema);
        Assert.Equal("object", tool.OutputSchema!.Value.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ConformingStructuredContent_Succeeds()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await ConnectAsync(s =>
            s.AddTool("add", "adds", McpJsonDefaults.ToElement(new { type = "object" }),
                (JsonElement? _, CancellationToken _) => Task.FromResult(new CallToolResult
                {
                    Content = [new TextContent { Text = "3" }],
                    StructuredContent = McpJsonDefaults.ToElement(new { sum = 3 })
                }),
                outputSchema: OutputSchema), cts.Token);

        var result = await client.CallToolAsync("add", null, cts.Token);
        Assert.Equal(3, result.StructuredContent!.Value.GetProperty("sum").GetInt32());
    }

    [Fact]
    public async Task NonConformingStructuredContent_IsRejected()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await ConnectAsync(s =>
            s.AddTool("add", "adds", McpJsonDefaults.ToElement(new { type = "object" }),
                (JsonElement? _, CancellationToken _) => Task.FromResult(new CallToolResult
                {
                    Content = [new TextContent { Text = "oops" }],
                    StructuredContent = McpJsonDefaults.ToElement(new { sum = "not-an-int" })
                }),
                outputSchema: OutputSchema), cts.Token);

        // Invalid structured output must not come back as a successful conforming result.
        var ex = await Assert.ThrowsAsync<McpException>(() => client.CallToolAsync("add", null, cts.Token));
        Assert.Contains("output schema", ex.Message);
    }

    [Fact]
    public async Task DeclaredOutputSchema_WithoutStructuredContent_IsAllowed()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await ConnectAsync(s =>
            s.AddTool("add", "adds", McpJsonDefaults.ToElement(new { type = "object" }),
                (JsonElement? _, CancellationToken _) => Task.FromResult(CallToolResult.Text("text only")),
                outputSchema: OutputSchema), cts.Token);

        var result = await client.CallToolAsync("add", null, cts.Token);
        Assert.Equal("text only", ((TextContent)result.Content[0]).Text);
    }
}
