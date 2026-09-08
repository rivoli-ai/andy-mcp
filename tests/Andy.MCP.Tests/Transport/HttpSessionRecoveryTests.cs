using System.Net;
using System.Text;
using Andy.MCP.AspNetCore;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Andy.MCP.Tests.Transport;

public class HttpSessionRecoveryTests
{
    private sealed class CaptureSession : DelegatingHandler
    {
        public string? Session;
        public CaptureSession() : base(new HttpClientHandler()) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids)) Session = ids.Single();
            return response;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletedSession_ReinitializesOnce_RefreshesCapabilities_AndRestartsGet(bool get)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var sessions = 0;
        app.MapMcp("/mcp", server =>
        {
            var number = Interlocked.Increment(ref sessions);
            server.AddTool("tool" + number, "test", (_, _) => Task.FromResult(CallToolResult.Text("ok")));
            if (number > 1) server.AddPrompt("recovered", "test", (_, _, _) => Task.FromResult(new GetPromptResult { Messages = [] }));
        }, new StreamableHttpServerOptions { AllowAnonymous = true, SsePollTimeout = TimeSpan.FromMilliseconds(50) });
        await app.StartAsync(timeout.Token);
        var endpoint = new Uri(app.Urls.Single() + "/mcp");
        using var capture = new CaptureSession();
        using var http = new HttpClient(capture);
        await using var transport = new StreamableHttpClientTransport(new()
        { Endpoint = endpoint, HttpClient = http, EnableServerSseStream = get, SseReconnectDelay = TimeSpan.FromMilliseconds(10) });
        await using var client = await McpClient.ConnectAsync(transport, cancellationToken: timeout.Token);
        Assert.Null(client.Session.ServerCapabilities!.Prompts);
        var oldSession = capture.Session;
        using var delete = new HttpRequestMessage(HttpMethod.Delete, endpoint);
        delete.Headers.TryAddWithoutValidation("Mcp-Session-Id", oldSession);
        delete.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-11-25");
        using var deleted = await http.SendAsync(delete, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        if (get)
            while (Volatile.Read(ref sessions) < 2 || client.Session.ServerCapabilities!.Prompts is null)
                await Task.Delay(10, timeout.Token);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.PingAsync(timeout.Token)));
        Assert.Equal(2, Volatile.Read(ref sessions));
        Assert.NotEqual(oldSession, capture.Session);
        Assert.NotNull(client.Session.ServerCapabilities!.Prompts);
        Assert.Equal("tool2", Assert.Single(await client.ListToolsAsync(timeout.Token)).Name);
        Assert.Equal("recovered", Assert.Single(await client.ListPromptsAsync(timeout.Token)).Name);
        Assert.Equal(0, client.PendingRequestCount);
        await app.StopAsync(timeout.Token);
    }

    private sealed class BlockingSampler : ISamplingHandler
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<CreateMessageResult> HandleAsync(CreateMessageRequest request, CancellationToken ct)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { Stopped.TrySetResult(); }
            throw new InvalidOperationException("Unreachable");
        }
    }

    [Fact]
    public async Task Recovery_CancelsOldInboundHandlers_WithoutRespondingIntoTheNewSession()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        Andy.MCP.Server.McpServer? activeServer = null;
        app.MapMcp("/mcp", server => activeServer = server,
            new StreamableHttpServerOptions { AllowAnonymous = true, SsePollTimeout = TimeSpan.FromMilliseconds(50) });
        await app.StartAsync(timeout.Token);
        var endpoint = new Uri(app.Urls.Single() + "/mcp");
        using var capture = new CaptureSession();
        using var http = new HttpClient(capture);
        var sampler = new BlockingSampler();
        await using var client = await McpClient.ConnectAsync(new StreamableHttpClientTransport(new()
        { Endpoint = endpoint, HttpClient = http, SseReconnectDelay = TimeSpan.FromMilliseconds(10) }),
            new McpClientOptions { SamplingHandler = sampler }, cancellationToken: timeout.Token);
        var oldServer = activeServer!;
        var sample = oldServer.CreateMessageAsync(new CreateMessageRequest
        { Messages = [new SamplingMessage { Role = Role.User, Content = [new TextContent { Text = "wait" }] }], MaxTokens = 1 }, timeout.Token);
        await sampler.Started.Task.WaitAsync(timeout.Token);
        using var delete = new HttpRequestMessage(HttpMethod.Delete, endpoint);
        delete.Headers.TryAddWithoutValidation("Mcp-Session-Id", capture.Session);
        using var deleted = await http.SendAsync(delete, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        await sampler.Stopped.Task.WaitAsync(timeout.Token);
        Assert.NotSame(oldServer, activeServer);
        await Assert.ThrowsAnyAsync<Exception>(async () => await sample);
        await client.PingAsync(timeout.Token);
        await activeServer!.PingClientAsync(timeout.Token);
        await app.StopAsync(timeout.Token);
    }

    private sealed class ScriptedServer(bool blockRecovery) : HttpMessageHandler
    {
        public int Initializations;
        public int Calls;
        public readonly TaskCompletionSource RecoveryCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Delete) return new(HttpStatusCode.OK);
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.NotFound);
            var message = McpJsonDefaults.Deserialize(await request.Content!.ReadAsStringAsync(ct));
            if (message is JsonRpcRequest { Method: "initialize" } init)
            {
                var n = Interlocked.Increment(ref Initializations);
                Assert.False(request.Headers.Contains("Mcp-Session-Id"));
                if (blockRecovery && n > 1)
                {
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                    finally { RecoveryCancelled.TrySetResult(); }
                }
                var response = Json(JsonRpcResponse.Success(init.Id, McpJsonDefaults.ToElement(new InitializeResult
                { ProtocolVersion = "2025-11-25", Capabilities = new ServerCapabilities { Tools = new() }, ServerInfo = new Implementation("reference", "1") })));
                response.Headers.TryAddWithoutValidation("Mcp-Session-Id", "session" + n);
                return response;
            }
            if (message is JsonRpcNotification) return new(HttpStatusCode.Accepted);
            var rpc = Assert.IsType<JsonRpcRequest>(message);
            if (rpc.Method == "tools/call")
            {
                Interlocked.Increment(ref Calls);
                if (blockRecovery) return new(HttpStatusCode.NotFound);
                return new(HttpStatusCode.OK) { Content = new StringContent("id: original.0\nretry: 10\ndata: \n\n", Encoding.UTF8, "text/event-stream") };
            }
            return Json(JsonRpcResponse.Success(rpc.Id));
        }
        private static HttpResponseMessage Json(JsonRpcResponse response) => new(HttpStatusCode.OK)
        { Content = new StringContent(McpJsonDefaults.Serialize(response), Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task ExpiredPostResume_RecoversForFutureCalls_WithoutRepeatingUnknownOutcome()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var handler = new ScriptedServer(false);
        using var http = new HttpClient(handler);
        await using var client = await McpClient.ConnectAsync(new StreamableHttpClientTransport(new()
        { Endpoint = new Uri("https://example.com/mcp"), HttpClient = http, EnableServerSseStream = false }), cancellationToken: timeout.Token);
        var error = await Assert.ThrowsAsync<McpSessionExpiredException>(() => client.CallToolAsync("action", ct: timeout.Token));
        Assert.Contains("outcome is unknown", error.Message);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(2, handler.Initializations);
        await client.PingAsync(timeout.Token);
        Assert.Equal(0, client.PendingRequestCount);
    }

    [Fact]
    public async Task RecoveryHandshake_ObeysOriginalCallDeadline()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var handler = new ScriptedServer(true);
        using var http = new HttpClient(handler);
        await using var client = await McpClient.ConnectAsync(new StreamableHttpClientTransport(new()
        { Endpoint = new Uri("https://example.com/mcp"), HttpClient = http, EnableServerSseStream = false }), cancellationToken: timeout.Token);
        await Assert.ThrowsAsync<TimeoutException>(() => client.CallToolAsync(new CallToolRequest { Name = "action" },
            new McpRequestOptions { Timeout = TimeSpan.FromMilliseconds(100) }, timeout.Token));
        await handler.RecoveryCancelled.Task.WaitAsync(timeout.Token);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, client.PendingRequestCount);
    }
}
