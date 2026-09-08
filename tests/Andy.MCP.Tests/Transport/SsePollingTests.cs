using System.Text;
using Andy.MCP.AspNetCore;
using Andy.MCP.Protocol;
using Microsoft.AspNetCore.Http;

namespace Andy.MCP.Tests;

public class SsePollingTests
{
    [Fact]
    public async Task ServerClosure_ReleasesStreams_AndAllowsResumption()
    {
        var session = new StreamableHttpSession("test");
        await using var reader = session.ReadServerEventsAsync("stream", 0).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        Assert.Equal(1, session.ActiveStreamCount);
        session.CloseServerStreams();
        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(0, session.ActiveStreamCount);
        Assert.True(session.IsConnected);
        await session.SendAsync(new JsonRpcNotification { Method = "ping" });
        await using var resumed = session.ReadServerEventsAsync("stream", 0).GetAsyncEnumerator();
        Assert.True(await resumed.MoveNextAsync());
    }

    [Fact]
    public async Task StreamLimits_DuplicateIdentityAndUnknownResume_AreRejected()
    {
        var session = new StreamableHttpSession("test") { MaxConcurrentStreams = 1 };
        await using var first = session.ReadServerEventsAsync("one", 0).GetAsyncEnumerator();
        var pending = first.MoveNextAsync().AsTask();
        Assert.False(session.TryOpenServerStream("one", 0, true, out _, out var duplicate));
        Assert.Equal(409, duplicate);
        Assert.False(session.TryOpenServerStream("two", 0, false, out _, out var limit));
        Assert.Equal(429, limit);
        Assert.False(session.TryOpenServerStream("unknown", 0, true, out _, out var unknown));
        Assert.Equal(400, unknown);
        session.CloseServerStreams();
        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(session.TryOpenServerStream("two", 0, false, out var close, out _));
        session.ReleaseServerStream("two", close!);
    }

    [Fact]
    public async Task HttpPolling_PrimesCursorAndRetry_ThenResumesSameStream()
    {
        using var handler = new StreamableHttpHandler(async session =>
        {
            await foreach (var message in session.Messages)
                if (message is JsonRpcRequest request) await session.SendAsync(JsonRpcResponse.Success(request.Id));
        }, new StreamableHttpServerOptions { AllowAnonymous = true, SsePollTimeout = TimeSpan.FromMilliseconds(30), SseRetryMilliseconds = 25 });
        var init = new DefaultHttpContext();
        init.Request.Method = "POST";
        init.Request.ContentType = "application/json";
        init.Request.Headers.Accept = "application/json, text/event-stream";
        init.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}"));
        init.Response.Body = new MemoryStream();
        await handler.HandleAsync(init).WaitAsync(TimeSpan.FromSeconds(3));
        var sessionId = init.Response.Headers["Mcp-Session-Id"].ToString();
        Assert.NotEmpty(sessionId);
        async Task<DefaultHttpContext> Poll(string? cursor)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Headers.Accept = "text/event-stream";
            context.Request.Headers["Mcp-Session-Id"] = sessionId;
            if (cursor is not null) context.Request.Headers["Last-Event-ID"] = cursor;
            context.Response.Body = new MemoryStream();
            await handler.HandleAsync(context).WaitAsync(TimeSpan.FromSeconds(3));
            return context;
        }
        static string Body(DefaultHttpContext context) => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        var first = await Poll(null);
        Assert.Equal(200, first.Response.StatusCode);
        Assert.Contains("retry: 25", Body(first));
        var id = Body(first).Split('\n').Single(line => line.StartsWith("id:")).Substring(3).Trim();
        Assert.True(StreamableHttpSession.TryParseEventId(id, out _, out _));
        var resumed = await Poll(id);
        Assert.Equal(200, resumed.Response.StatusCode);
        Assert.Contains(id, Body(resumed));
        Assert.Equal(400, (await Poll("unknown.0")).Response.StatusCode);
        Assert.Equal(400, (await Poll("stream.-1")).Response.StatusCode);
    }
}
