using System.Security.Cryptography;
using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;
using Andy.MCP.Transport;

namespace Andy.MCP.Tests.Conformance;

public class ProtocolBoundaryTests
{
    [Theory]
    [InlineData("CompleteRequestParams", "{\"ref\":{\"type\":\"invalid\"},\"argument\":{\"name\":\"x\",\"value\":\"x\"}}")]
    [InlineData("ElicitResult", "{\"action\":\"unknown\"}")]
    [InlineData("CreateMessageRequestParams", "{\"messages\":[],\"maxTokens\":1,\"toolChoice\":{\"mode\":\"unknown\"}}")]
    [InlineData("ModelPreferences", "{\"costPriority\":2}")]
    [InlineData("ContentBlock", "{\"type\":\"image\",\"data\":\"abc\"}")]
    public void InvalidStandardShapes_AreRejected(string definition, string json) =>
        Assert.Throws<JsonException>(() => ProtocolShapeValidation.Validate(definition, JsonDocument.Parse(json).RootElement, ProtocolRevision.Latest));

    [Fact]
    public void RuntimeSchemas_MatchFrozenProvenance()
    {
        using var sources = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Conformance", "schemas", "sources.json")));
        foreach (var revision in ProtocolRevision.Supported)
        {
            using var embedded = typeof(McpSession).Assembly.GetManifestResourceStream($"Andy.MCP.Schemas.schema-{revision.Version}.json");
            Assert.NotNull(embedded);
            Assert.Equal(sources.RootElement.GetProperty(revision.Version).GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(embedded)));
        }
    }

    [Fact]
    public void OldCompletions_DoNotRequireANonexistentCapabilityFlag()
    {
        var session = new McpSession();
        session.Transition(McpSessionState.Initializing);
        session.CompleteInitializationAsClient(new InitializeResult { ProtocolVersion = "2024-11-05", Capabilities = new(), ServerInfo = new("test", "1") });
        session.RequireServerCapability("completions");
    }

    private sealed class Mutate(IClientTransport inner, Func<JsonRpcMessage, JsonRpcMessage> transform) : IClientTransport
    {
        public bool IsConnected => inner.IsConnected;
        public event EventHandler<TransportDisconnectedEventArgs>? Disconnected { add => inner.Disconnected += value; remove => inner.Disconnected -= value; }
        public IAsyncEnumerable<JsonRpcMessage> Messages => inner.Messages;
        public Task ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);
        public Task SendAsync(JsonRpcMessage message, CancellationToken ct = default) => inner.SendAsync(transform(message), ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidParameters_NeverReachServerHandler(bool corruptAfterClientValidation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (transport, peer) = InMemoryTransport.CreatePair();
        var calls = 0;
        await using var server = new McpServer(peer);
        server.AddCompletion("ref/prompt", "p", "a", (_, _, _) => { calls++; return Task.FromResult(new CompletionValues { Values = [] }); });
        var run = server.RunAsync(timeout.Token);
        var mutated = new Mutate(transport, message => corruptAfterClientValidation && message is JsonRpcRequest { Method: "completion/complete" } request
            ? request with { Params = McpJsonDefaults.ToElement(new { @ref = new { type = "invalid" }, argument = new { name = "a", value = "" } }) } : message);
        await using var client = await McpClient.ConnectAsync(mutated, cancellationToken: timeout.Token);
        var parameters = new CompletionRequest { Ref = new CompletionRef { Type = corruptAfterClientValidation ? "ref/prompt" : "invalid", Name = "p" }, Argument = new CompletionArgument { Name = "a", Value = "" } };
        if (corruptAfterClientValidation)
        {
            var error = await Assert.ThrowsAsync<McpException>(() => client.CompleteAsync(parameters, timeout.Token));
            Assert.Equal(McpErrorCodes.InvalidParams, error.ErrorCode);
        }
        else await Assert.ThrowsAsync<JsonException>(() => client.CompleteAsync(parameters, timeout.Token));
        Assert.Equal(0, calls);
        await client.PingAsync(timeout.Token);
        timeout.Cancel(); await run;
    }

    private sealed class InvalidElicitor : IElicitationHandler
    {
        public Task<ElicitResult> HandleAsync(ElicitRequest request, CancellationToken ct) => Task.FromResult(new ElicitResult { Action = "invalid" });
    }

    [Fact]
    public async Task InvalidClientHandlerResult_BecomesInternalError_AndSessionRemainsUsable()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (transport, peer) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(peer);
        var run = server.RunAsync(timeout.Token);
        await using var client = await McpClient.ConnectAsync(transport, new McpClientOptions { ElicitationHandler = new InvalidElicitor() }, cancellationToken: timeout.Token);
        await client.PingAsync(timeout.Token);
        var error = await Assert.ThrowsAsync<McpException>(() => server.ElicitAsync(ElicitRequest.Form("test", new ElicitationSchema { Properties = new Dictionary<string, PrimitiveSchemaDefinition>() }), timeout.Token));
        Assert.Equal(McpErrorCodes.InternalError, error.ErrorCode);
        await client.PingAsync(timeout.Token);
        timeout.Cancel(); await run;
    }
}
