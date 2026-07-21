using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Client;

/// <summary>
/// End-to-end tests for high-level client APIs added in #48: completion and resource
/// subscribe/unsubscribe, each gated on the exact negotiated capability/sub-capability.
/// </summary>
public class ClientHighLevelApiTests
{
    private static async Task<McpClient> ConnectAsync(Action<McpServer> configure, CancellationToken ct,
        McpServerOptions? options = null)
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new McpServer(serverTransport, options);
        configure(server);
        _ = server.RunAsync(ct);
        return await McpClient.ConnectAsync(clientTransport, cancellationToken: ct);
    }

    [Fact]
    public async Task CompleteAsync_ReturnsCompletionValues()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await ConnectAsync(s =>
            s.AddCompletion("ref/prompt", "greet", "name",
                (value, args, ct) => Task.FromResult(new CompletionValues { Values = new[] { "Alice", "Alan" } })),
            cts.Token);

        var result = await client.CompleteAsync(new CompletionRequest
        {
            Ref = new CompletionRef { Type = "ref/prompt", Name = "greet" },
            Argument = new CompletionArgument { Name = "name", Value = "Al" }
        }, cts.Token);

        Assert.Contains("Alice", result.Completion.Values);
    }

    [Fact]
    public async Task Complete_Throws_WhenServerLacksCompletionsCapability()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await ConnectAsync(s =>
            s.AddTool("t", "d", (_, _) => Task.FromResult(CallToolResult.Text("ok"))), cts.Token);

        await Assert.ThrowsAsync<McpCapabilityNotAvailableException>(() =>
            client.CompleteAsync(new CompletionRequest
            {
                Ref = new CompletionRef { Type = "ref/prompt", Name = "x" },
                Argument = new CompletionArgument { Name = "a", Value = "b" }
            }, cts.Token));
    }

    [Fact]
    public async Task SubscribeAndUnsubscribe_Succeed_WhenSubscribeDeclared()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await ConnectAsync(s =>
            s.AddResource("file:///a", "a", (_, _) => Task.FromResult<ResourceContents>(
                new TextResourceContents { Uri = "file:///a", Text = "x" })), cts.Token);

        await client.SubscribeResourceAsync("file:///a", cts.Token);   // must not throw
        await client.UnsubscribeResourceAsync("file:///a", cts.Token); // must not throw
    }

    [Fact]
    public async Task Subscribe_Throws_WhenSubscribeSubCapabilityNotDeclared()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await ConnectAsync(s =>
            s.AddResource("file:///a", "a", (_, _) => Task.FromResult<ResourceContents>(
                new TextResourceContents { Uri = "file:///a", Text = "x" })),
            cts.Token,
            new McpServerOptions { ResourcesSubscribe = false });

        await Assert.ThrowsAsync<McpCapabilityNotAvailableException>(() =>
            client.SubscribeResourceAsync("file:///a", cts.Token));
    }
}
