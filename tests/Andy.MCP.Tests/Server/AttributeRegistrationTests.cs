using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Server;

#region Test Tool Classes

public class SampleTools
{
    [McpTool(Description = "Greet someone")]
    public async Task<CallToolResult> Greet(
        [McpParam(Description = "Person to greet", Required = true)] string name,
        [McpParam(Description = "Greeting style")] string style = "friendly",
        CancellationToken cancellationToken = default)
    {
        return CallToolResult.Text($"Hello, {name}! ({style})");
    }

    [McpTool(Name = "add_numbers", Description = "Add two numbers")]
    public async Task<CallToolResult> AddNumbers(
        [McpParam(Required = true)] double a,
        [McpParam(Required = true)] double b)
    {
        return CallToolResult.Text((a + b).ToString());
    }

    [McpTool(Description = "Returns a string directly")]
    public async Task<string> GetMessage()
    {
        return "Direct string result";
    }

    [McpTool(Description = "Read-only tool", ReadOnly = true, Destructive = false, Idempotent = true)]
    public async Task<CallToolResult> ReadOnlyTool()
    {
        return CallToolResult.Text("read-only");
    }
}

public class StaticTools
{
    [McpTool(Description = "A static tool")]
    public static async Task<CallToolResult> StaticGreet(
        [McpParam(Required = true)] string name)
    {
        return CallToolResult.Text($"Static hello, {name}!");
    }
}

public class SampleResources
{
    [McpResource(Uri = "config://app", Name = "App Config", Description = "Application configuration", MimeType = "application/json")]
    public async Task<ResourceContents> GetConfig(string uri, CancellationToken ct)
    {
        return new TextResourceContents { Uri = uri, Text = "{\"version\": 1}", MimeType = "application/json" };
    }
}

public class SamplePrompts
{
    [McpPrompt(Description = "Review code")]
    public async Task<GetPromptResult> CodeReview(
        [McpParam(Description = "Programming language", Required = true)] string language,
        [McpParam(Description = "Review style")] string? style = null)
    {
        return new GetPromptResult
        {
            Messages = [new PromptMessage
            {
                Role = Role.User,
                Content = new TextContent { Text = $"Review {language} code. Style: {style ?? "thorough"}" }
            }]
        };
    }
}

#endregion

public class SnakeCaseTests
{
    [Theory]
    [InlineData("GetWeather", "get_weather")]
    [InlineData("Greet", "greet")]
    [InlineData("AddNumbers", "add_numbers")]
    [InlineData("ReadOnlyTool", "read_only_tool")]
    [InlineData("HTTPClient", "h_t_t_p_client")]
    [InlineData("getItems", "get_items")]
    [InlineData("A", "a")]
    [InlineData("", "")]
    public void PascalToSnake(string input, string expected)
    {
        Assert.Equal(expected, AttributeDiscovery.ToSnakeCase(input));
    }
}

public class SchemaGenerationTests
{
    [Fact]
    public async Task Tool_SchemaGenerated_FromParameters()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.AddToolsFromType<SampleTools>();
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);
        var tools = await client.ListToolsAsync(cts.Token);

        // "greet" tool should have name (required string) and style (optional string with default)
        var greet = tools.First(t => t.Name == "greet");
        Assert.NotNull(greet.Description);

        var schema = greet.InputSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());

        var props = schema.GetProperty("properties");
        Assert.Equal("string", props.GetProperty("name").GetProperty("type").GetString());
        Assert.Equal("string", props.GetProperty("style").GetProperty("type").GetString());

        // "name" should be required, "style" should not
        var required = schema.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Contains("name", required);
        Assert.DoesNotContain("style", required);
    }

    [Fact]
    public async Task Tool_NumberParams_SchemaCorrect()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.AddToolsFromType<SampleTools>();
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);
        var tools = await client.ListToolsAsync(cts.Token);

        var add = tools.First(t => t.Name == "add_numbers");
        var props = add.InputSchema.GetProperty("properties");
        Assert.Equal("number", props.GetProperty("a").GetProperty("type").GetString());
        Assert.Equal("number", props.GetProperty("b").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Tool_CancellationToken_ExcludedFromSchema()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.AddToolsFromType<SampleTools>();
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);
        var tools = await client.ListToolsAsync(cts.Token);

        var greet = tools.First(t => t.Name == "greet");
        var props = greet.InputSchema.GetProperty("properties");

        // CancellationToken should NOT appear as a property
        Assert.False(props.TryGetProperty("cancellationToken", out _));
    }
}

public class ToolInvocationTests
{
    [Fact]
    public async Task CallAttributeTool_WithArgs()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.AddToolsFromType<SampleTools>();
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);
        var result = await client.CallToolAsync("greet", new { name = "Alice", style = "formal" }, ct: cts.Token);

        var text = Assert.IsType<TextContent>(result.Content[0]);
        Assert.Equal("Hello, Alice! (formal)", text.Text);
    }

    [Fact]
    public async Task CallAttributeTool_DefaultParam()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.AddToolsFromType<SampleTools>();
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);
        var result = await client.CallToolAsync("greet", new { name = "Bob" }, ct: cts.Token);

        var text = Assert.IsType<TextContent>(result.Content[0]);
        Assert.Equal("Hello, Bob! (friendly)", text.Text);
    }

    [Fact]
    public async Task CallAttributeTool_StringReturn_AutoWrapped()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.AddToolsFromType<SampleTools>();
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);
        var result = await client.CallToolAsync("get_message", ct: cts.Token);

        var text = Assert.IsType<TextContent>(result.Content[0]);
        Assert.Equal("Direct string result", text.Text);
    }

    [Fact]
    public async Task CallAttributeTool_ExplicitName()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.AddToolsFromType<SampleTools>();
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);
        var result = await client.CallToolAsync("add_numbers", new { a = 3.0, b = 4.0 }, ct: cts.Token);

        var text = Assert.IsType<TextContent>(result.Content[0]);
        Assert.Equal("7", text.Text);
    }

    [Fact]
    public async Task StaticTool_Works()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.AddToolsFromType<StaticTools>();
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);
        var result = await client.CallToolAsync("static_greet", new { name = "World" }, ct: cts.Token);

        var text = Assert.IsType<TextContent>(result.Content[0]);
        Assert.Equal("Static hello, World!", text.Text);
    }

    [Fact]
    public async Task Tool_Annotations_FromAttribute()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.AddToolsFromType<SampleTools>();
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);
        var tools = await client.ListToolsAsync(cts.Token);

        var readOnly = tools.First(t => t.Name == "read_only_tool");
        Assert.NotNull(readOnly.Annotations);
        Assert.True(readOnly.Annotations!.ReadOnlyHint);
        Assert.True(readOnly.Annotations.IdempotentHint);
    }
}

public class ResourceRegistrationTests
{
    [Fact]
    public async Task AttributeResource_Registered()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.AddToolsFromType<SampleResources>();
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);
        var resources = await client.ListResourcesAsync(cts.Token);

        Assert.Single(resources);
        Assert.Equal("config://app", resources[0].Uri);
        Assert.Equal("App Config", resources[0].Name);
    }

    [Fact]
    public async Task AttributeResource_Readable()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.AddToolsFromType<SampleResources>();
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);
        var result = await client.ReadResourceAsync("config://app", cts.Token);

        var text = Assert.IsType<TextResourceContents>(result.Contents[0]);
        Assert.Contains("version", text.Text);
    }
}

public class PromptRegistrationTests
{
    [Fact]
    public async Task AttributePrompt_Registered()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.AddToolsFromType<SamplePrompts>();
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);
        var prompts = await client.ListPromptsAsync(cts.Token);

        Assert.Single(prompts);
        Assert.Equal("code_review", prompts[0].Name);
        Assert.NotNull(prompts[0].Arguments);
        Assert.Equal(2, prompts[0].Arguments!.Count);
        Assert.Equal("language", prompts[0].Arguments[0].Name);
        Assert.True(prompts[0].Arguments[0].Required);
    }

    [Fact]
    public async Task AttributePrompt_Callable()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.AddToolsFromType<SamplePrompts>();
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);
        var result = await client.GetPromptAsync("code_review",
            new Dictionary<string, string> { ["language"] = "csharp" }, cts.Token);

        var text = Assert.IsType<TextContent>(result.Messages[0].Content);
        Assert.Contains("csharp", text.Text);
    }
}

public class MixedRegistrationTests
{
    [Fact]
    public async Task FluentAndAttribute_Coexist()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.AddTool("manual_tool", "A manual tool", async (a, c) => CallToolResult.Text("manual"));
        server.AddToolsFromType<SampleTools>();
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);
        var tools = await client.ListToolsAsync(cts.Token);

        Assert.Contains(tools, t => t.Name == "manual_tool");
        Assert.Contains(tools, t => t.Name == "greet");
        Assert.Contains(tools, t => t.Name == "add_numbers");
    }
}
