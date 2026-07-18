using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Server;

/// <summary>
/// Tests that the server's advertised capabilities exactly describe what it implements
/// (issue #41): sub-capability flags are configurable, and the resources capability is
/// advertised for template-only servers.
/// </summary>
public class ServerCapabilityExactnessTests
{
    private static async Task<ServerCapabilities> NegotiateCapabilitiesAsync(
        Action<McpServer> configure, McpServerOptions? options = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var server = new McpServer(serverTransport, options ?? new McpServerOptions());
        configure(server);
        _ = server.RunAsync(cts.Token);

        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);
        return client.Session.ServerCapabilities!;
    }

    [Fact]
    public async Task Defaults_AdvertiseListChangedAndSubscribe()
    {
        var caps = await NegotiateCapabilitiesAsync(s =>
        {
            s.AddTool("t", "d", (_, _) => Task.FromResult(CallToolResult.Text("ok")));
            s.AddResource("file:///a", "a", (_, _) => Task.FromResult<ResourceContents>(
                new TextResourceContents { Uri = "file:///a", Text = "x" }));
            s.AddPrompt("p", "d", (_, _, _) => Task.FromResult(new GetPromptResult()));
        });

        Assert.True(caps.Tools?.ListChanged);
        Assert.True(caps.Resources?.Subscribe);
        Assert.True(caps.Resources?.ListChanged);
        Assert.True(caps.Prompts?.ListChanged);
    }

    [Fact]
    public async Task TemplateOnlyServer_AdvertisesResourcesCapability()
    {
        var caps = await NegotiateCapabilitiesAsync(s =>
            s.AddResourceTemplate("file:///{path}", "files"));

        // Previously this was null because only static resources were considered.
        Assert.NotNull(caps.Resources);
    }

    [Fact]
    public async Task NoResources_OmitsResourcesCapability()
    {
        var caps = await NegotiateCapabilitiesAsync(s =>
            s.AddTool("t", "d", (_, _) => Task.FromResult(CallToolResult.Text("ok"))));

        Assert.Null(caps.Resources);
    }

    [Fact]
    public async Task DisabledFlags_AreReflectedExactly()
    {
        var options = new McpServerOptions
        {
            ToolsListChanged = false,
            ResourcesSubscribe = false,
            ResourcesListChanged = false,
            PromptsListChanged = false,
        };

        var caps = await NegotiateCapabilitiesAsync(s =>
        {
            s.AddTool("t", "d", (_, _) => Task.FromResult(CallToolResult.Text("ok")));
            s.AddResource("file:///a", "a", (_, _) => Task.FromResult<ResourceContents>(
                new TextResourceContents { Uri = "file:///a", Text = "x" }));
            s.AddPrompt("p", "d", (_, _, _) => Task.FromResult(new GetPromptResult()));
        }, options);

        // Capabilities are still present (the features exist) but the sub-flags are exact.
        Assert.NotNull(caps.Tools);
        Assert.False(caps.Tools?.ListChanged);
        Assert.NotNull(caps.Resources);
        Assert.False(caps.Resources?.Subscribe);
        Assert.False(caps.Resources?.ListChanged);
        Assert.NotNull(caps.Prompts);
        Assert.False(caps.Prompts?.ListChanged);
    }
}
