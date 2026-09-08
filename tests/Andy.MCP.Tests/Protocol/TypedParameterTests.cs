using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;
using Andy.MCP.Transport;
namespace Andy.MCP.Tests.Protocol;

public class TypedParameterTests
{
    public static IEnumerable<object[]> Revisions => ProtocolRevision.All.Select(r => new object[] { r.Version });

    [Theory]
    [MemberData(nameof(Revisions))]
    public void BaseRequestAndNotificationMetadata_IsPreservedForEveryRevision(string version)
    {
        var metadata = McpJsonDefaults.ToElement(new { progressToken = "p", vendor = 7 });
        object[] models =
        [
            new CallToolRequest { Name = "t", Meta = metadata },
            new CreateMessageRequest { Messages = [], MaxTokens = 1, Meta = metadata },
            new InitializeParams { ProtocolVersion = version, Capabilities = new(), ClientInfo = new("test", "1"), Meta = metadata },
            new PaginatedRequest { Meta = metadata },
            new CancelledParams { RequestId = 1, Meta = metadata },
            new ProgressParams { ProgressToken = "p", Progress = 1, Meta = metadata },
            new ReadResourceRequestParams { Uri = "file:///test", Meta = metadata },
            new GetPromptRequestParams { Name = "p", Meta = metadata },
            new ResourceUpdatedParams { Uri = "file:///test", Meta = metadata }
        ];
        foreach (var model in models)
        {
            var json = RevisionAwareJson.ToElementForRevision(model, ProtocolRevision.TryGet(version)!);
            Assert.Equal(7, json.GetProperty("_meta").GetProperty("vendor").GetInt32());
        }
    }

    [Fact]
    public void TypedParameters_RoundTripUnknownFields_AndTitledMultiSelectUsesAnyOf()
    {
        var parameters = JsonSerializer.Deserialize<ReadResourceRequestParams>("""{"uri":"file:///x","_meta":{"vendor":true},"vendor/extra":[1,2]}""", McpJsonDefaults.Options)!;
        var json = McpJsonDefaults.ToElement(parameters);
        Assert.Equal(2, json.GetProperty("vendor/extra").GetArrayLength());
        Assert.True(json.GetProperty("_meta").GetProperty("vendor").GetBoolean());
        var schema = PrimitiveSchemaDefinition.TitledMultiSelectEnumField([new("one", "First"), new("two", "Second")], @default: ["one"]);
        var wire = McpJsonDefaults.ToElement(schema);
        Assert.Equal("First", wire.GetProperty("items").GetProperty("anyOf")[0].GetProperty("title").GetString());
        Assert.Equal("one", wire.GetProperty("default")[0].GetString());
    }

    [Fact]
    public void UrlElicitationRequiredError_PreservesTypedData_AndRejectsFormRequests()
    {
        var error = JsonRpcError.UrlElicitationRequired(new UrlElicitationRequiredData
        { Elicitations = [ElicitRequest.ForUrl("authorize", "one", "https://example.com/authorize")] });
        Assert.Equal(-32042, error.Code);
        var data = error.Data!.Value.Deserialize<UrlElicitationRequiredData>(McpJsonDefaults.Options)!;
        Assert.Equal("one", Assert.Single(data.Elicitations).ElicitationId);
        Assert.Throws<ArgumentException>(() => JsonRpcError.UrlElicitationRequired(new UrlElicitationRequiredData
        { Elicitations = [ElicitRequest.Form("input", new ElicitationSchema { Properties = new Dictionary<string, PrimitiveSchemaDefinition>() })] }));
    }

    private sealed class Observe(IClientTransport inner) : IClientTransport
    {
        public readonly List<JsonRpcRequest> Requests = new();
        public bool IsConnected => inner.IsConnected;
        public event EventHandler<TransportDisconnectedEventArgs>? Disconnected { add => inner.Disconnected += value; remove => inner.Disconnected -= value; }
        public IAsyncEnumerable<JsonRpcMessage> Messages => inner.Messages;
        public Task ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);
        public Task SendAsync(JsonRpcMessage message, CancellationToken ct = default)
        { if (message is JsonRpcRequest request) Requests.Add(request); return inner.SendAsync(message, ct); }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Fact]
    public async Task TypedResourcePromptAndLogApis_PreserveRequestAndResultMetadata()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (transport, peer) = InMemoryTransport.CreatePair();
        var observe = new Observe(transport);
        var metadata = McpJsonDefaults.ToElement(new { vendor = "kept" });
        await using var server = new McpServer(peer);
        server.WithLogging();
        server.AddResource("file:///x", "x", (_, _) => Task.FromResult<ResourceContents>(new TextResourceContents { Uri = "file:///x", Text = "text" }));
        server.AddPrompt("prompt", "test", (_, args, _) => Task.FromResult(new GetPromptResult
        { Messages = [new PromptMessage { Role = Role.User, Content = new TextContent(args!["literal"]) }], Meta = metadata }));
        var run = server.RunAsync(timeout.Token);
        await using var client = await McpClient.ConnectAsync(observe, cancellationToken: timeout.Token);
        Assert.Single((await client.ReadResourceAsync(new ReadResourceRequestParams { Uri = "file:///x", Meta = metadata }, ct: timeout.Token)).Contents);
        var prompt = await client.GetPromptAsync(new GetPromptRequestParams
        { Name = "prompt", Arguments = new Dictionary<string, string> { ["literal"] = "${unchanged}" }, Meta = metadata }, ct: timeout.Token);
        Assert.Equal("${unchanged}", Assert.IsType<TextContent>(Assert.Single(prompt.Messages).Content).Text);
        Assert.Equal("kept", prompt.Meta!.Value.GetProperty("vendor").GetString());
        await client.SetLogLevelAsync(new SetLogLevelParams { Level = McpLogLevel.Debug, Meta = metadata }, ct: timeout.Token);
        await client.SubscribeResourceAsync(new SubscribeRequestParams { Uri = "file:///x", Meta = metadata }, ct: timeout.Token);
        await client.UnsubscribeResourceAsync(new UnsubscribeRequestParams { Uri = "file:///x", Meta = metadata }, ct: timeout.Token);
        foreach (var request in observe.Requests.Where(r => r.Method is "resources/read" or "prompts/get" or "logging/setLevel" or "resources/subscribe" or "resources/unsubscribe"))
            Assert.Equal("kept", request.Params!.Value.GetProperty("_meta").GetProperty("vendor").GetString());
        timeout.Cancel(); await run;
    }
}
