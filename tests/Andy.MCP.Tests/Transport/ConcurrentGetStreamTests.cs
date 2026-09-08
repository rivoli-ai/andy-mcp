using System.Text;
using Andy.MCP.AspNetCore;
using Andy.MCP.Protocol;
using Andy.MCP.Transport.Sse;
using Microsoft.AspNetCore.Http;

namespace Andy.MCP.Tests;

public class ConcurrentGetStreamTests
{
    [Fact]
    public async Task TwoActiveHttpGetStreams_RouteLiveMessagesOnce_AndCloseCleanly()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        StreamableHttpSession? session = null;
        using var handler = new StreamableHttpHandler(async transport =>
        {
            session = Assert.IsType<StreamableHttpSession>(transport);
            await foreach (var message in transport.Messages)
                if (message is JsonRpcRequest request) await transport.SendAsync(JsonRpcResponse.Success(request.Id));
        }, new StreamableHttpServerOptions { AllowAnonymous = true, SsePollTimeout = TimeSpan.FromMilliseconds(200) });
        var init = new DefaultHttpContext();
        init.RequestAborted = timeout.Token;
        init.Request.Method = "POST";
        init.Request.ContentType = "application/json";
        init.Request.Headers.Accept = "application/json, text/event-stream";
        init.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}"));
        init.Response.Body = new MemoryStream();
        await handler.HandleAsync(init);
        Assert.NotNull(session);
        DefaultHttpContext Get()
        {
            var context = new DefaultHttpContext();
            context.RequestAborted = timeout.Token;
            context.Request.Method = "GET";
            context.Request.Headers.Accept = "text/event-stream";
            context.Request.Headers["Mcp-Session-Id"] = init.Response.Headers["Mcp-Session-Id"];
            context.Response.Body = new MemoryStream();
            return context;
        }
        var first = Get(); var second = Get();
        var firstRun = handler.HandleAsync(first); var secondRun = handler.HandleAsync(second);
        Assert.Equal(2, session.ActiveStreamCount); // Both GETs are waiting before any application event.
        for (var i = 0; i < 20; i++)
            await session.SendAsync(new JsonRpcNotification { Method = "notifications/message", Params = McpJsonDefaults.ToElement(new { index = i }) }, timeout.Token);
        await Task.WhenAll(firstRun, secondRun).WaitAsync(timeout.Token);
        var indexes = new List<int>();
        var streamIds = new List<string>();
        foreach (var context in new[] { first, second })
        {
            Assert.Equal(200, context.Response.StatusCode);
            context.Response.Body.Position = 0;
            var ids = new HashSet<string>();
            await foreach (var evt in SseParser.ParseAsync(context.Response.Body, timeout.Token))
            {
                Assert.True(StreamableHttpSession.TryParseEventId(evt.Id, out var id, out _));
                ids.Add(id);
                if (evt.Data.Length == 0) continue;
                indexes.Add(Assert.IsType<JsonRpcNotification>(McpJsonDefaults.Deserialize(evt.Data)).Params!.Value.GetProperty("index").GetInt32());
            }
            streamIds.Add(Assert.Single(ids));
        }
        Assert.Equal(2, streamIds.Distinct().Count());
        Assert.Equal(Enumerable.Range(0, 20), indexes.Order());
        Assert.Equal(0, session.ActiveStreamCount);
        Assert.True(session.IsConnected);
    }
}
