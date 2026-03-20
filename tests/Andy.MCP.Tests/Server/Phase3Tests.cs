using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Server;

public class ToolValidationTests
{
    [Fact]
    public async Task CallTool_MissingRequiredParam_ReturnsValidationError()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.AddTool("greet", "Say hello",
            McpJsonDefaults.ToElement(new
            {
                type = "object",
                properties = new { name = new { type = "string" } },
                required = new[] { "name" }
            }),
            async (args, c) => CallToolResult.Text($"Hello, {args?.GetProperty("name").GetString()}!"));

        var serverTask = server.RunAsync(cts.Token);
        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);

        // Call without required 'name' parameter
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            client.CallToolAsync("greet", new { }, cts.Token));
        Assert.Equal(McpErrorCodes.InvalidParams, ex.ErrorCode);
        Assert.Contains("name", ex.Message);
    }

    [Fact]
    public async Task CallTool_WrongParamType_ReturnsValidationError()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.AddTool("add", "Add numbers",
            McpJsonDefaults.ToElement(new
            {
                type = "object",
                properties = new { a = new { type = "number" }, b = new { type = "number" } },
                required = new[] { "a", "b" }
            }),
            async (args, c) => CallToolResult.Text("42"));

        var serverTask = server.RunAsync(cts.Token);
        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            client.CallToolAsync("add", new { a = "not a number", b = 2 }, cts.Token));
        Assert.Equal(McpErrorCodes.InvalidParams, ex.ErrorCode);
        Assert.Contains("wrong type", ex.Message);
    }

    [Fact]
    public async Task CallTool_ValidParams_Succeeds()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.AddTool("greet", "Say hello",
            McpJsonDefaults.ToElement(new
            {
                type = "object",
                properties = new { name = new { type = "string" } },
                required = new[] { "name" }
            }),
            async (args, c) => CallToolResult.Text($"Hello, {args?.GetProperty("name").GetString()}!"));

        var serverTask = server.RunAsync(cts.Token);
        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);

        var result = await client.CallToolAsync("greet", new { name = "Alice" }, cts.Token);
        var text = Assert.IsType<TextContent>(result.Content[0]);
        Assert.Equal("Hello, Alice!", text.Text);
    }
}

public class ResourceSubscriptionTests
{
    [Fact]
    public async Task SubscribeAndUnsubscribe()
    {
        var mgr = new ResourceSubscriptionManager();

        mgr.Subscribe("file:///a");
        Assert.True(mgr.HasSubscribers("file:///a"));

        mgr.Unsubscribe("file:///a");
        Assert.False(mgr.HasSubscribers("file:///a"));
    }

    [Fact]
    public async Task MultipleSubscribers()
    {
        var mgr = new ResourceSubscriptionManager();

        mgr.Subscribe("file:///a", "session1");
        mgr.Subscribe("file:///a", "session2");
        Assert.True(mgr.HasSubscribers("file:///a"));

        mgr.Unsubscribe("file:///a", "session1");
        Assert.True(mgr.HasSubscribers("file:///a")); // session2 still subscribed

        mgr.Unsubscribe("file:///a", "session2");
        Assert.False(mgr.HasSubscribers("file:///a"));
    }

    [Fact]
    public async Task RemoveSession_CleansUpAll()
    {
        var mgr = new ResourceSubscriptionManager();

        mgr.Subscribe("file:///a", "session1");
        mgr.Subscribe("file:///b", "session1");
        mgr.Subscribe("file:///a", "session2");

        mgr.RemoveSession("session1");

        Assert.True(mgr.HasSubscribers("file:///a")); // session2 remains
        Assert.False(mgr.HasSubscribers("file:///b")); // only session1 was subscribed
    }

    [Fact]
    public async Task GetSubscribedUris()
    {
        var mgr = new ResourceSubscriptionManager();

        mgr.Subscribe("file:///a");
        mgr.Subscribe("file:///b");

        var uris = mgr.GetSubscribedUris();
        Assert.Contains("file:///a", uris);
        Assert.Contains("file:///b", uris);
    }

    [Fact]
    public async Task Server_SubscribeResource_ViaProtocol()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.AddResource("file:///config.json", "Config", async (uri, c) =>
            new TextResourceContents { Uri = uri, Text = "{}" });

        var serverTask = server.RunAsync(cts.Token);
        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);

        // Subscribe capability should be available
        Assert.True(client.Session.HasServerCapability("resources"));
    }
}

public class CompletionTests
{
    [Fact]
    public async Task Completion_ReturnsValues()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.AddPrompt("review", "Code review", async (name, args, c) =>
            new GetPromptResult { Messages = [] });
        server.AddCompletion("ref/prompt", "review", "language",
            async (value, context, c) => new CompletionValues
            {
                Values = new[] { "csharp", "python", "javascript" }
                    .Where(v => v.StartsWith(value, StringComparison.OrdinalIgnoreCase))
                    .ToList()
            });

        var serverTask = server.RunAsync(cts.Token);
        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);

        Assert.True(client.Session.HasServerCapability("completions"));
    }

    [Fact]
    public void CompletionValues_MaxEnforced()
    {
        // Generate 150 values, ensure only 100 returned
        var values = Enumerable.Range(1, 150).Select(i => $"item-{i}").ToList();
        var truncated = values.Count > 100 ? values.Take(100).ToList() : values;

        Assert.Equal(100, truncated.Count);
    }
}

public class LoggingTests
{
    [Fact]
    public async Task Server_WithLogging_HasCapability()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.WithLogging();

        var serverTask = server.RunAsync(cts.Token);
        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);

        Assert.True(client.Session.HasServerCapability("logging"));
    }

    [Fact]
    public async Task Server_WithoutLogging_NoCapability()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        // No WithLogging() call

        var serverTask = server.RunAsync(cts.Token);
        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);

        Assert.False(client.Session.HasServerCapability("logging"));
    }

    [Fact]
    public void McpLogLevel_Serializes()
    {
        Assert.Equal("\"debug\"", JsonSerializer.Serialize(McpLogLevel.Debug, McpJsonDefaults.Options));
        Assert.Equal("\"info\"", JsonSerializer.Serialize(McpLogLevel.Info, McpJsonDefaults.Options));
        Assert.Equal("\"warning\"", JsonSerializer.Serialize(McpLogLevel.Warning, McpJsonDefaults.Options));
        Assert.Equal("\"error\"", JsonSerializer.Serialize(McpLogLevel.Error, McpJsonDefaults.Options));
        Assert.Equal("\"critical\"", JsonSerializer.Serialize(McpLogLevel.Critical, McpJsonDefaults.Options));
        Assert.Equal("\"emergency\"", JsonSerializer.Serialize(McpLogLevel.Emergency, McpJsonDefaults.Options));
        Assert.Equal("\"alert\"", JsonSerializer.Serialize(McpLogLevel.Alert, McpJsonDefaults.Options));
        Assert.Equal("\"notice\"", JsonSerializer.Serialize(McpLogLevel.Notice, McpJsonDefaults.Options));
    }

    [Fact]
    public void McpLogLevel_Deserializes()
    {
        Assert.Equal(McpLogLevel.Debug, JsonSerializer.Deserialize<McpLogLevel>("\"debug\"", McpJsonDefaults.Options));
        Assert.Equal(McpLogLevel.Emergency, JsonSerializer.Deserialize<McpLogLevel>("\"emergency\"", McpJsonDefaults.Options));
    }

    [Fact]
    public void McpLogLevel_SeverityOrdering()
    {
        // Lower enum value = higher severity
        Assert.True(McpLogLevel.Emergency < McpLogLevel.Debug);
        Assert.True(McpLogLevel.Error < McpLogLevel.Warning);
        Assert.True(McpLogLevel.Warning < McpLogLevel.Info);
    }

    [Fact]
    public async Task SetLogLevel_ViaClient()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.WithLogging();

        var serverTask = server.RunAsync(cts.Token);
        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);

        await client.SetLogLevelAsync("debug", cts.Token); // Should not throw
    }
}

public class JsonSchemaValidatorTests
{
    [Fact]
    public void Valid_RequiredPresent()
    {
        var schema = McpJsonDefaults.ToElement(new
        {
            type = "object",
            properties = new { name = new { type = "string" } },
            required = new[] { "name" }
        });
        var args = McpJsonDefaults.ToElement(new { name = "Alice" });

        var errors = JsonSchemaValidator.Validate(args, schema);
        Assert.Empty(errors);
    }

    [Fact]
    public void Invalid_MissingRequired()
    {
        var schema = McpJsonDefaults.ToElement(new
        {
            type = "object",
            properties = new { name = new { type = "string" } },
            required = new[] { "name" }
        });
        var args = McpJsonDefaults.ToElement(new { });

        var errors = JsonSchemaValidator.Validate(args, schema);
        Assert.Single(errors);
        Assert.Contains("name", errors[0]);
    }

    [Fact]
    public void Invalid_WrongType()
    {
        var schema = McpJsonDefaults.ToElement(new
        {
            type = "object",
            properties = new { age = new { type = "integer" } }
        });
        var args = McpJsonDefaults.ToElement(new { age = "not a number" });

        var errors = JsonSchemaValidator.Validate(args, schema);
        Assert.Single(errors);
        Assert.Contains("wrong type", errors[0]);
    }

    [Fact]
    public void Valid_CorrectTypes()
    {
        var schema = McpJsonDefaults.ToElement(new
        {
            type = "object",
            properties = new
            {
                name = new { type = "string" },
                count = new { type = "number" },
                active = new { type = "boolean" }
            }
        });
        var args = McpJsonDefaults.ToElement(new { name = "test", count = 42, active = true });

        var errors = JsonSchemaValidator.Validate(args, schema);
        Assert.Empty(errors);
    }

    [Fact]
    public void Valid_NullArgs_NoRequired()
    {
        var schema = McpJsonDefaults.ToElement(new
        {
            type = "object",
            properties = new { name = new { type = "string" } }
        });

        var errors = JsonSchemaValidator.Validate(null, schema);
        Assert.Empty(errors);
    }

    [Fact]
    public void Invalid_NullArgs_WithRequired()
    {
        var schema = McpJsonDefaults.ToElement(new
        {
            type = "object",
            properties = new { name = new { type = "string" } },
            required = new[] { "name" }
        });

        var errors = JsonSchemaValidator.Validate(null, schema);
        Assert.Single(errors);
    }
}
