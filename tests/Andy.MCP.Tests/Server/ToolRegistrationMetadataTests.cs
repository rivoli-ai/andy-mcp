using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Server;

public class ToolRegistrationMetadataTests
{
    public sealed class Services
    {
        public int Calls;
        [McpTool(Title = "Sum", OpenWorld = false, TaskSupport = "forbidden",
            OutputSchemaJson = "{\"type\":\"object\",\"required\":[\"sum\"],\"properties\":{\"sum\":{\"type\":\"integer\"}}}",
            IconsJson = "[{\"src\":\"https://example.com/icon.png\"}]", MetaJson = "{\"owner\":\"test\"}")]
        public async ValueTask<object> Sum(int a, int b)
        {
            await Task.Yield();
            Calls++;
            return new { sum = a + b };
        }

        [McpTool] public ValueTask Nothing() => ValueTask.CompletedTask;
        [McpTool] public string Fails() => throw new InvalidOperationException("Useful domain failure");
        [McpTool(DefinitionJson = "{\"name\":\"custom\",\"inputSchema\":{\"type\":\"object\"},\"annotations\":{\"x-hint\":true},\"x-vendor\":42}")]
        public string Custom() => "custom";
    }

    [Fact]
    public async Task Attributes_PreserveMetadataLifetimeAndAsyncResults()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (transport, peer) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(peer);
        var services = new Services();
        server.AddToolsFromType(typeof(Services), services);
        var run = server.RunAsync(deadline.Token);
        await using var client = await McpClient.ConnectAsync(transport, cancellationToken: deadline.Token);
        var tools = await client.ListToolsAsync(deadline.Token);
        var sum = Assert.Single(tools, t => t.Name == "sum");
        Assert.Equal("Sum", sum.Title);
        Assert.False(sum.Annotations!.OpenWorldHint);
        Assert.Equal("forbidden", sum.Execution!.TaskSupport);
        Assert.Equal("test", sum.Meta!.Value.GetProperty("owner").GetString());
        Assert.Equal("https://example.com/icon.png", Assert.Single(sum.Icons!).Source);
        Assert.Equal(42, tools.Single(t => t.Name == "custom").ExtensionData!["x-vendor"].GetInt32());
        var result = await client.CallToolAsync("sum", new { a = 1, b = 2 }, deadline.Token);
        Assert.Equal(3, result.StructuredContent!.Value.GetProperty("sum").GetInt32());
        Assert.Equal(result.StructuredContent.Value.GetRawText(), Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
        Assert.Equal(1, services.Calls);
        Assert.False((await client.CallToolAsync("nothing", ct: deadline.Token)).IsError == true);
        var failed = await client.CallToolAsync("fails", ct: deadline.Token);
        Assert.True(failed.IsError);
        Assert.Equal("Useful domain failure", Assert.IsType<TextContent>(Assert.Single(failed.Content)).Text);
        deadline.Cancel();
        await run;
    }

    [Theory]
    [InlineData("forbidden", true, false)]
    [InlineData("required", false, false)]
    [InlineData("optional", true, true)]
    [InlineData("optional", false, true)]
    [InlineData("required", true, true)]
    public async Task ExecutionPolicy_IsEnforced(string support, bool augmented, bool allowed)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (transport, peer) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(peer);
        server.AddTool(new Tool
        {
            Name = "test",
            InputSchema = McpJsonDefaults.ToElement(new { type = "object" }),
            Execution = new ToolExecution { TaskSupport = support }
        }, (_, _) => Task.FromResult(CallToolResult.Text("ok")));
        var run = server.RunAsync(deadline.Token);
        await using var client = await McpClient.ConnectAsync(transport, cancellationToken: deadline.Token);
        async Task Invoke()
        {
            if (augmented) await client.CallToolAsTaskAsync("test", ct: deadline.Token);
            else await client.CallToolAsync("test", ct: deadline.Token);
        }
        if (allowed) await Invoke();
        else if (augmented) await Assert.ThrowsAsync<McpCapabilityNotAvailableException>(Invoke);
        else Assert.Equal(McpErrorCodes.MethodNotFound, (await Assert.ThrowsAsync<McpException>(Invoke)).ErrorCode);
        deadline.Cancel();
        await run;
    }

    [Theory]
    [InlineData("true")]
    [InlineData("{\"type\":\"array\"}")]
    [InlineData("{}")]
    public void ToolSchema_MustDescribeObject(string json)
    {
        var (_, peer) = InMemoryTransport.CreatePair();
        var server = new McpServer(peer);
        Assert.Throws<ArgumentException>(() => server.AddTool(new Tool
        {
            Name = "invalid",
            InputSchema = JsonSerializer.Deserialize<JsonElement>(json)
        }, (_, _) => Task.FromResult(CallToolResult.Text(""))));
    }

    [Theory]
    [InlineData("2024-11-05", false)]
    [InlineData("2025-11-25", true)]
    public async Task Validation_UsesNegotiatedErrorChannel(string revision, bool toolError)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (transport, peer) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(peer);
        server.AddTool("test", "", JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\",\"required\":[\"name\"]}"),
            (_, _) => Task.FromResult(CallToolResult.Text("never")));
        var run = server.RunAsync(deadline.Token);
        await transport.ConnectAsync(deadline.Token);
        await transport.SendAsync(new JsonRpcRequest
        {
            Id = 1,
            Method = "initialize",
            Params = McpJsonDefaults.ToElement(new
            { protocolVersion = revision, capabilities = new { }, clientInfo = new { name = "test", version = "1" } })
        }, deadline.Token);
        await using var messages = transport.Messages.GetAsyncEnumerator(deadline.Token);
        Assert.True(await messages.MoveNextAsync());
        await transport.SendAsync(new JsonRpcNotification { Method = "notifications/initialized" }, deadline.Token);
        await transport.SendAsync(new JsonRpcRequest { Id = 2, Method = "tools/call", Params = McpJsonDefaults.ToElement(new { name = "test" }) }, deadline.Token);
        Assert.True(await messages.MoveNextAsync());
        var response = Assert.IsType<JsonRpcResponse>(messages.Current);
        if (toolError) Assert.True(response.Result!.Value.GetProperty("isError").GetBoolean());
        else Assert.Equal(McpErrorCodes.InvalidParams, response.Error!.Code);
        deadline.Cancel();
        await run;
    }
}
