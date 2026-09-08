using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;
namespace Andy.MCP.Tests.Server;

public class HighLevelContractTests
{
    private sealed class Elicitor : IElicitationHandler
    {
        public Task<ElicitResult> HandleAsync(ElicitRequest request, CancellationToken ct) => Task.FromResult(ElicitResult.Decline());
    }
    private sealed class Sampler : ISamplingHandler
    {
        public Task<CreateMessageResult> HandleAsync(CreateMessageRequest request, CancellationToken ct) => Task.FromResult(new CreateMessageResult
        { Role = Role.Assistant, Model = "test", Content = [new TextContent("sampled")] });
    }

    private sealed class Roots : IRootProvider
    {
        private EventHandler? _changed;
        public int Subscribers;
        public event EventHandler? RootsChanged { add { _changed += value; Subscribers++; } remove { _changed -= value; Subscribers--; } }
        public IReadOnlyList<Root> GetRoots() => [];
        public void Change() => _changed?.Invoke(this, EventArgs.Empty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RootNotifications_RespectCapability_AndDetachOnDisposal(bool enabled)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (transport, peer) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(peer);
        var changes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RootsChanged += (_, _) => changes.TrySetResult();
        var run = server.RunAsync(timeout.Token);
        var roots = new Roots();
        await using var client = await McpClient.ConnectAsync(transport, new McpClientOptions
        { RootProvider = roots, Capabilities = new ClientCapabilities { Roots = new RootsCapability { ListChanged = enabled } } }, cancellationToken: timeout.Token);
        await client.PingAsync(timeout.Token);
        roots.Change();
        if (enabled) await changes.Task.WaitAsync(timeout.Token);
        else
        {
            await client.PingAsync(timeout.Token);
            Assert.False(changes.Task.IsCompleted);
            await Assert.ThrowsAsync<McpCapabilityNotAvailableException>(() => client.NotifyRootsChangedAsync(timeout.Token));
        }
        Assert.Equal(1, roots.Subscribers);
        await client.DisposeAsync();
        Assert.Equal(0, roots.Subscribers);
        timeout.Cancel(); await run;
    }

    [Fact]
    public async Task UrlCompletionNotification_PreservesMetadata()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (transport, peer) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(peer);
        var run = server.RunAsync(timeout.Token);
        await using var client = await McpClient.ConnectAsync(transport, new McpClientOptions
        { ElicitationHandler = new Elicitor(), Capabilities = new ClientCapabilities { Elicitation = new ElicitationCapability { Url = new() } } }, cancellationToken: timeout.Token);
        var completed = new TaskCompletionSource<ElicitationCompleteParams>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ElicitationCompleted += (_, parameters) => completed.TrySetResult(parameters);
        await client.PingAsync(timeout.Token);
        await server.NotifyElicitationCompleteAsync(new ElicitationCompleteParams { ElicitationId = "id", Meta = McpJsonDefaults.ToElement(new { source = "test" }) }, timeout.Token);
        var result = await completed.Task.WaitAsync(timeout.Token);
        Assert.Equal("id", result.ElicitationId);
        Assert.Equal("test", result.Meta!.Value.GetProperty("source").GetString());
        timeout.Cancel(); await run;
    }

    [Fact]
    public async Task RegistrationFreezes_PromptArgumentsAreLiteral_AndResourcesReturnMultipleContents()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (transport, peer) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(peer, new McpServerOptions { PromptsListChanged = false, ResourcesListChanged = false });
        var calls = 0;
        server.AddPrompt(new Prompt { Name = "greet", Arguments = [new PromptArgument { Name = "name", Required = true }] },
            (_, args, _) => { calls++; return Task.FromResult(new GetPromptResult { Messages = [new PromptMessage { Role = Role.User, Content = new TextContent(args!["name"]) }] }); });
        server.AddResource(new Resource { Uri = "file:///bundle", Name = "bundle", Title = "Bundle" }, (_, _) =>
            Task.FromResult<IReadOnlyList<ResourceContents>>([new TextResourceContents { Uri = "file:///a", Text = "a" }, new TextResourceContents { Uri = "file:///b", Text = "b" }]));
        var run = server.RunAsync(timeout.Token);
        Assert.Throws<InvalidOperationException>(() => server.WithLogging());
        Assert.Throws<InvalidOperationException>(() => server.AddTool("late", "", (_, _) => Task.FromResult(CallToolResult.Text(""))));
        await using var client = await McpClient.ConnectAsync(transport, cancellationToken: timeout.Token);
        await client.PingAsync(timeout.Token);
        Assert.Equal(2, (await client.ReadResourceAsync("file:///bundle", timeout.Token)).Contents.Count);
        Assert.Equal("Bundle", Assert.Single(await client.ListResourcesAsync(timeout.Token)).Title);
        var error = await Assert.ThrowsAsync<McpException>(() => client.GetPromptAsync("greet", ct: timeout.Token));
        Assert.Equal(McpErrorCodes.InvalidParams, error.ErrorCode);
        Assert.Equal(0, calls);
        var result = await client.GetPromptAsync("greet", new Dictionary<string, string> { ["name"] = "${literal}{braces}" }, timeout.Token);
        Assert.Equal("${literal}{braces}", Assert.IsType<TextContent>(Assert.Single(result.Messages).Content).Text);
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<McpCapabilityNotAvailableException>(() => server.NotifyPromptsChangedAsync());
        await Assert.ThrowsAsync<McpCapabilityNotAvailableException>(() => server.NotifyResourcesChangedAsync());
        timeout.Cancel(); await run;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SamplingAndElicitation_RequireTheirAdvertisedSubCapabilities(bool enabled)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (transport, peer) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(peer);
        var run = server.RunAsync(timeout.Token);
        await using var client = await McpClient.ConnectAsync(transport, new McpClientOptions
        {
            SamplingHandler = new Sampler(),
            ElicitationHandler = new Elicitor(),
            Capabilities = new ClientCapabilities
            {
                Sampling = enabled ? new SamplingCapability { Tools = new(), Context = new() } : new(),
                Elicitation = enabled ? new ElicitationCapability { Url = new() } : new ElicitationCapability { Form = new() }
            }
        }, cancellationToken: timeout.Token);
        await client.PingAsync(timeout.Token);
        var sampling = new CreateMessageRequest { Messages = [new SamplingMessage { Role = Role.User, Content = [new TextContent("hi")] }], MaxTokens = 10, Tools = [] };
        var url = ElicitRequest.ForUrl("Authorize", "id", "https://example.com/authorize");
        var form = ElicitRequest.Form("Name", new ElicitationSchema { Properties = new Dictionary<string, PrimitiveSchemaDefinition>() });
        if (enabled)
        {
            Assert.Equal("sampled", Assert.IsType<TextContent>(Assert.Single((await server.CreateMessageAsync(sampling, timeout.Token)).Content)).Text);
            await server.CreateMessageAsync(sampling with { Tools = null, IncludeContext = "thisServer" }, timeout.Token);
            await server.ElicitAsync(url, timeout.Token);
            await Assert.ThrowsAsync<McpCapabilityNotAvailableException>(() => server.ElicitAsync(form, timeout.Token));
        }
        else
        {
            await Assert.ThrowsAsync<McpCapabilityNotAvailableException>(() => server.CreateMessageAsync(sampling, timeout.Token));
            await Assert.ThrowsAsync<McpCapabilityNotAvailableException>(() => server.CreateMessageAsync(sampling with { Tools = null, IncludeContext = "thisServer" }, timeout.Token));
            await Assert.ThrowsAsync<McpCapabilityNotAvailableException>(() => server.ElicitAsync(url, timeout.Token));
            await server.ElicitAsync(form, timeout.Token);
        }
        timeout.Cancel(); await run;
    }
}
