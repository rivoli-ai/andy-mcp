using System.Text;
using Andy.MCP.AspNetCore;
using Andy.MCP.Protocol;
using Microsoft.AspNetCore.Http;
namespace Andy.MCP.Tests;

public class HttpBoundedSessionTests
{
    [Fact]
    public async Task SessionQueues_AreBounded_AndWaitersAreReleased()
    {
        var session = new StreamableHttpSession("test") { MaxPendingResponses = 2 };
        var first = session.RegisterResponseWaiter((RequestId)1, CancellationToken.None);
        var second = session.RegisterResponseWaiter((RequestId)2, CancellationToken.None);
        Assert.Equal(429, Assert.Throws<McpHttpRequestRejectedException>(() => { _ = session.RegisterResponseWaiter((RequestId)3, CancellationToken.None); }).StatusCode);
        Assert.Equal(409, Assert.Throws<McpHttpRequestRejectedException>(() => { _ = session.RegisterResponseWaiter((RequestId)1, CancellationToken.None); }).StatusCode);
        session.CancelWaiter((RequestId)1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);
        await session.SendAsync(JsonRpcResponse.Success((RequestId)2));
        await second;
        Assert.Equal(0, session.PendingResponseCount);
        await session.SendAsync(JsonRpcResponse.Success((RequestId)4));
        await session.SendAsync(JsonRpcResponse.Success((RequestId)5));
        await Assert.ThrowsAsync<McpHttpRequestRejectedException>(() => session.SendAsync(JsonRpcResponse.Success((RequestId)6)));
        Assert.Equal(2, session.BufferedResponseCount);
        session.Close();
        Assert.Equal(0, session.BufferedResponseCount);
    }

    [Fact]
    public async Task ReplayOverflow_PreservesUndeliveredEvents_AndRejectsExpiredCursors()
    {
        var session = new StreamableHttpSession("test");
        for (var i = 0; i < 256; i++) await session.SendAsync(new JsonRpcNotification { Method = "event" });
        await Assert.ThrowsAsync<McpHttpRequestRejectedException>(() => session.SendAsync(new JsonRpcNotification { Method = "overflow" }));
        await using (var reader = session.ReadServerEventsAsync("one", 0).GetAsyncEnumerator())
            for (var i = 0; i < 256; i++) Assert.True(await reader.MoveNextAsync());
        await session.SendAsync(new JsonRpcNotification { Method = "later" });
        Assert.False(session.TryOpenServerStream("one", 0, true, out _, out var status));
        Assert.Equal(400, status);
        Assert.True(session.TryOpenServerStream("one", 1, true, out var close, out _));
        session.ReleaseServerStream("one", close!);
    }

    [Theory]
    [InlineData("2025-03-26", 200)]
    [InlineData("2025-06-18", 200)]
    [InlineData("2025-11-25", 200)]
    [InlineData("2024-11-05", 400)]
    public async Task HttpRevisionSupport_IsExplicit(string revision, int expected)
    {
        using var handler = new StreamableHttpHandler(async session =>
        {
            await foreach (var message in session.Messages)
                if (message is JsonRpcRequest request) await session.SendAsync(JsonRpcResponse.Success(request.Id));
        }, new StreamableHttpServerOptions { AllowAnonymous = true });
        var context = Post("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}", null);
        context.Request.Headers["MCP-Protocol-Version"] = revision;
        await handler.HandleAsync(context).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(expected, context.Response.StatusCode);
    }

    [Fact]
    public async Task QueueOverload_Returns429_WithoutLeakingRejectedRequestWaiter()
    {
        using var stop = new CancellationTokenSource();
        StreamableHttpSession? session = null;
        using var handler = new StreamableHttpHandler(async transport =>
        {
            session = Assert.IsType<StreamableHttpSession>(transport);
            await using var messages = transport.Messages.GetAsyncEnumerator(stop.Token);
            Assert.True(await messages.MoveNextAsync());
            await transport.SendAsync(JsonRpcResponse.Success(Assert.IsType<JsonRpcRequest>(messages.Current).Id));
            try { await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token); } catch (OperationCanceledException) { }
        }, new StreamableHttpServerOptions { AllowAnonymous = true });
        var init = Post("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}", null);
        await handler.HandleAsync(init).WaitAsync(TimeSpan.FromSeconds(3));
        var id = init.Response.Headers["Mcp-Session-Id"].ToString();
        for (var i = 0; i < 256; i++)
        {
            var notification = Post("{\"jsonrpc\":\"2.0\",\"method\":\"vendor/event\"}", id);
            await handler.HandleAsync(notification);
            Assert.Equal(202, notification.Response.StatusCode);
        }
        var overload = Post("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}", id);
        await handler.HandleAsync(overload).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(429, overload.Response.StatusCode);
        Assert.Equal(0, session!.PendingResponseCount);
        stop.Cancel();
    }

    [Fact]
    public async Task EstablishedSession_RejectsHeaderThatDiffersFromNegotiatedRevision()
    {
        using var handler = new StreamableHttpHandler(async session =>
        {
            await foreach (var message in session.Messages)
                if (message is JsonRpcRequest request)
                    await session.SendAsync(JsonRpcResponse.Success(request.Id, McpJsonDefaults.ToElement(new { protocolVersion = "2025-06-18" })));
        }, new StreamableHttpServerOptions { AllowAnonymous = true });
        var init = Post("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}", null);
        await handler.HandleAsync(init).WaitAsync(TimeSpan.FromSeconds(3));
        var id = init.Response.Headers["Mcp-Session-Id"].ToString();
        foreach (var revision in new[] { "2025-03-26", "2025-06-18", "2025-11-25" })
        {
            var request = Post("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}", id);
            request.Request.Headers["MCP-Protocol-Version"] = revision;
            await handler.HandleAsync(request).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(revision == "2025-06-18" ? 200 : 400, request.Response.StatusCode);
        }
    }

    private static DefaultHttpContext Post(string body, string? session)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST"; context.Request.ContentType = "application/json";
        context.Request.Headers.Accept = "application/json, text/event-stream";
        if (session is not null) context.Request.Headers["Mcp-Session-Id"] = session;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }
}
