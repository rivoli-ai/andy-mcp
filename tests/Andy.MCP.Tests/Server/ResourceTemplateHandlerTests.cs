using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Server;

/// <summary>
/// End-to-end tests for resource-template handlers with URI-template resolution and multi-content
/// reads (issue #71).
/// </summary>
public class ResourceTemplateHandlerTests
{
    private static async Task<McpClient> ConnectAsync(Action<McpServer> configure, CancellationToken ct)
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new McpServer(serverTransport);
        configure(server);
        _ = server.RunAsync(ct);
        return await McpClient.ConnectAsync(clientTransport, cancellationToken: ct);
    }

    [Fact]
    public async Task TemplateRead_ResolvesVariables_AndReturnsContent()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await ConnectAsync(s =>
            s.AddResourceTemplate("file:///users/{userId}/docs/{docId}", "user docs",
                (uri, vars, _) => Task.FromResult<IReadOnlyList<ResourceContents>>(
                    new[] { new TextResourceContents { Uri = uri, Text = $"{vars["userId"]}:{vars["docId"]}" } })),
            cts.Token);

        var result = await client.ReadResourceAsync("file:///users/alice/docs/42", cts.Token);

        var content = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents));
        Assert.Equal("alice:42", content.Text);
    }

    [Fact]
    public async Task TemplateRead_SupportsMultipleContents()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await ConnectAsync(s =>
            s.AddResourceTemplate("file:///bundle/{id}", "bundle",
                (uri, vars, _) => Task.FromResult<IReadOnlyList<ResourceContents>>(new ResourceContents[]
                {
                    new TextResourceContents { Uri = uri + "/a", Text = "a" },
                    new TextResourceContents { Uri = uri + "/b", Text = "b" }
                })),
            cts.Token);

        var result = await client.ReadResourceAsync("file:///bundle/x", cts.Token);
        Assert.Equal(2, result.Contents.Count);
    }

    [Fact]
    public async Task NonMatchingUri_ReturnsResourceNotFound()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await ConnectAsync(s =>
            s.AddResourceTemplate("file:///users/{userId}", "users",
                (uri, vars, _) => Task.FromResult<IReadOnlyList<ResourceContents>>(
                    new[] { new TextResourceContents { Uri = uri, Text = "x" } })),
            cts.Token);

        await Assert.ThrowsAsync<McpException>(() =>
            client.ReadResourceAsync("https://other/thing", cts.Token));
    }

    [Fact]
    public async Task StaticResource_TakesPrecedenceOverTemplate()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await ConnectAsync(s =>
        {
            s.AddResource("file:///users/alice", "alice",
                (_, _) => Task.FromResult<ResourceContents>(new TextResourceContents { Uri = "file:///users/alice", Text = "static" }));
            s.AddResourceTemplate("file:///users/{userId}", "users",
                (uri, vars, _) => Task.FromResult<IReadOnlyList<ResourceContents>>(
                    new[] { new TextResourceContents { Uri = uri, Text = "template" } }));
        }, cts.Token);

        var result = await client.ReadResourceAsync("file:///users/alice", cts.Token);
        Assert.Equal("static", ((TextResourceContents)result.Contents[0]).Text);
    }
}
