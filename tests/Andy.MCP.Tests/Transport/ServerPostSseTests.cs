using System.Collections.Concurrent;
using Andy.MCP.AspNetCore;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
namespace Andy.MCP.Tests.Transport;

public class ServerPostSseTests
{
    private sealed class Sampler : ISamplingHandler
    {
        public Task<CreateMessageResult> HandleAsync(CreateMessageRequest request, CancellationToken ct) => Task.FromResult(new CreateMessageResult
        { Role = Role.Assistant, Content = [new TextContent { Text = Assert.IsType<TextContent>(Assert.Single(request.Messages[0].Content)).Text }], Model = "test" });
    }

    private sealed class ObserveHttp : DelegatingHandler
    {
        public readonly ConcurrentBag<string> Cursors = new();
        public int ToolPosts;
        public int SseResponses;
        public ObserveHttp() : base(new HttpClientHandler()) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get && request.Headers.TryGetValues("Last-Event-ID", out var ids)) Cursors.Add(ids.Single());
            if (request.Content is not null && McpJsonDefaults.Deserialize(await request.Content.ReadAsStringAsync(ct)) is JsonRpcRequest { Method: "tools/call" })
                Interlocked.Increment(ref ToolPosts);
            var response = await base.SendAsync(request, ct);
            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream") Interlocked.Increment(ref SseResponses);
            return response;
        }
    }

    [Fact]
    public async Task ConcurrentPostSse_RoutesSamplingWithoutGlobalGet_AndResumesAfterPolling()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapMcp("/mcp", server => server.AddTool("sample", "test", async (args, ct) =>
        {
            var result = await server.CreateMessageAsync(new CreateMessageRequest
            { Messages = [new SamplingMessage { Role = Role.User, Content = [new TextContent { Text = args!.Value.GetProperty("text").GetString()! }] }], MaxTokens = 10 }, ct);
            await Task.Delay(250, ct);
            return CallToolResult.Text(Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
        }), new StreamableHttpServerOptions { AllowAnonymous = true, UseSseResponses = true, SsePollTimeout = TimeSpan.FromMilliseconds(50), SseRetryMilliseconds = 10 });
        await app.StartAsync(timeout.Token);
        using var observer = new ObserveHttp();
        using var http = new HttpClient(observer);
        await using var client = await McpClient.ConnectAsync(new StreamableHttpClientTransport(new()
        { Endpoint = new Uri(app.Urls.Single() + "/mcp"), HttpClient = http, EnableServerSseStream = false }),
            new McpClientOptions { SamplingHandler = new Sampler() }, cancellationToken: timeout.Token);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => client.CallToolAsync("sample", new { text = "item" + i }, timeout.Token)));
        Assert.Equal(Enumerable.Range(0, 8).Select(i => "item" + i), results.Select(r => Assert.IsType<TextContent>(Assert.Single(r.Content)).Text));
        Assert.Equal(8, observer.ToolPosts);
        Assert.True(observer.SseResponses > 8);
        Assert.Equal(8, observer.Cursors.Select(cursor => cursor[..cursor.LastIndexOf('.')]).Distinct().Count());
        Assert.Equal(0, client.PendingRequestCount);
        await app.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task PostOwnedEvents_AreExcludedFromGlobalGet_AndTerminalReplayEndsStream()
    {
        await using var session = new StreamableHttpSession("test");
        var post = session.CreatePostStream((RequestId)1, out var close);
        await session.SendAsync(new JsonRpcNotification { Method = "vendor/global" });
        using (session.EnterRequestScope((RequestId)1))
        {
            await session.SendAsync(new JsonRpcNotification { Method = "vendor/related" });
            await session.SendAsync(JsonRpcResponse.Success((RequestId)1));
        }
        Assert.Equal(0, session.PendingPostCount);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using (var global = session.ReadServerEventsAsync("global", 0, timeout.Token).GetAsyncEnumerator())
        {
            Assert.True(await global.MoveNextAsync());
            Assert.Equal("vendor/global", Assert.IsType<JsonRpcNotification>(global.Current.Message).Method);
        }
        var events = new List<(long Seq, JsonRpcMessage Message)>();
        await foreach (var item in session.ReadServerEventsCoreAsync(post, 0, timeout.Token)) events.Add(item);
        Assert.Equal("vendor/related", Assert.IsType<JsonRpcNotification>(events[0].Message).Method);
        Assert.IsType<JsonRpcResponse>(events[1].Message);
        Assert.Equal(2, events.Count);
        session.ReleaseServerStream(post, close);
        var replay = new List<JsonRpcMessage>();
        await foreach (var item in session.ReadServerEventsAsync(post, events[0].Seq, timeout.Token)) replay.Add(item.Message);
        Assert.IsType<JsonRpcResponse>(Assert.Single(replay));
    }

    [Fact]
    public async Task AbandonedHandler_ReleasesPendingPostAndEndsItsStream()
    {
        await using var session = new StreamableHttpSession("test");
        var post = session.CreatePostStream((RequestId)1, out var close);
        using (session.EnterRequestScope((RequestId)1))
            await session.SendAsync(new JsonRpcNotification { Method = "vendor/pending" });
        Assert.Equal(0, session.PendingPostCount);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var reader = session.ReadServerEventsCoreAsync(post, 0, timeout.Token).GetAsyncEnumerator();
        Assert.False(await reader.MoveNextAsync());
        session.ReleaseServerStream(post, close);
        var next = session.CreatePostStream((RequestId)1, out var nextClose);
        session.DiscardPostStream(next, nextClose);
    }

    [Fact]
    public async Task PendingPost_ReservesTerminalCapacity_AndDuplicateIdsAreRejected()
    {
        await using var session = new StreamableHttpSession("test");
        var post = session.CreatePostStream((RequestId)1, out var close);
        Assert.Equal(409, Assert.Throws<McpHttpRequestRejectedException>(() => session.CreatePostStream((RequestId)1, out _)).StatusCode);
        for (var i = 0; i < 255; i++) await session.SendAsync(new JsonRpcNotification { Method = "vendor/event" });
        await Assert.ThrowsAsync<McpHttpRequestRejectedException>(() => session.SendAsync(new JsonRpcNotification { Method = "vendor/overflow" }));
        await session.SendAsync(JsonRpcResponse.Success((RequestId)1));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var events = new List<JsonRpcMessage>();
        await foreach (var item in session.ReadServerEventsCoreAsync(post, 0, timeout.Token)) events.Add(item.Message);
        Assert.IsType<JsonRpcResponse>(Assert.Single(events));
        session.ReleaseServerStream(post, close);
    }
}
