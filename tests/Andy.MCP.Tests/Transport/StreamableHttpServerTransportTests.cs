using System.Text;
using Andy.MCP.AspNetCore;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;
using Microsoft.AspNetCore.Http;

namespace Andy.MCP.Tests;

/// <summary>
/// Regression tests for the Streamable HTTP server transport response-correlation race
/// (issue #40). A fast server response must always be returned on its originating POST and
/// must never be leaked onto the GET SSE stream, even when it is produced before the POST
/// handler has registered its waiter.
/// </summary>
public class StreamableHttpServerTransportTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // ---- Session-level deterministic race tests ----

    [Fact]
    public async Task Response_ArrivingBeforeWaiterRegistered_IsDeliveredAndNotLeakedToSse()
    {
        var session = new StreamableHttpSession("s1");

        // Force the server response to occur at the earliest possible scheduling point:
        // before the POST handler has had any chance to register its waiter.
        await session.SendAsync(JsonRpcResponse.Success((RequestId)1));

        // The POST handler now registers its waiter and must still receive the response.
        var waiter = session.RegisterResponseWaiter((RequestId)1, CancellationToken.None);
        var response = await waiter.WaitAsync(Timeout);
        Assert.Equal((RequestId)1, response.Id);

        // The response must never have entered the SSE (server-message) stream.
        session.Close();
        var leaked = new List<JsonRpcMessage>();
        await foreach (var m in session.ServerMessages)
            leaked.Add(m);
        Assert.Empty(leaked);

        Assert.Equal(0, session.PendingResponseCount);
        Assert.Equal(0, session.BufferedResponseCount);
    }

    [Fact]
    public async Task Response_ArrivingAfterWaiterRegistered_CompletesWaiter()
    {
        var session = new StreamableHttpSession("s1");

        var waiter = session.RegisterResponseWaiter((RequestId)7, CancellationToken.None);
        Assert.False(waiter.IsCompleted);

        await session.SendAsync(JsonRpcResponse.Success((RequestId)7));

        var response = await waiter.WaitAsync(Timeout);
        Assert.Equal((RequestId)7, response.Id);
        Assert.Equal(0, session.PendingResponseCount);
        Assert.Equal(0, session.BufferedResponseCount);
    }

    [Fact]
    public void RegisterResponseWaiter_DuplicateInFlightId_Throws()
    {
        var session = new StreamableHttpSession("s1");
        _ = session.RegisterResponseWaiter((RequestId)1, CancellationToken.None);

        // The waiter is rejected synchronously (before any task is returned).
        Assert.Throws<InvalidOperationException>(
            () => { _ = session.RegisterResponseWaiter((RequestId)1, CancellationToken.None); });
    }

    [Fact]
    public async Task AbortedWait_LeavesNoPendingWaiter()
    {
        var session = new StreamableHttpSession("s1");
        using var cts = new CancellationTokenSource();

        var waiter = session.RegisterResponseWaiter((RequestId)1, cts.Token);
        Assert.Equal(1, session.PendingResponseCount);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiter);

        Assert.Equal(0, session.PendingResponseCount);
        Assert.Equal(0, session.BufferedResponseCount);
    }

    [Fact]
    public async Task Close_CancelsPendingWaiters_AndClearsState()
    {
        var session = new StreamableHttpSession("s1");
        var waiter = session.RegisterResponseWaiter((RequestId)1, CancellationToken.None);
        Assert.Equal(1, session.PendingResponseCount);

        session.Close();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiter);
        Assert.Equal(0, session.PendingResponseCount);
        Assert.Equal(0, session.BufferedResponseCount);

        // Registering after close returns a cancelled task rather than hanging forever.
        var afterClose = session.RegisterResponseWaiter((RequestId)2, CancellationToken.None);
        Assert.True(afterClose.IsCanceled);
    }

    // ---- Handler-level integration tests ----

    [Fact]
    public async Task Handler_Initialize_ReturnsResponseOnPost_WithSessionId()
    {
        var handler = new StreamableHttpHandler(EchoHandler());

        var (status, body, headers) = await PostAsync(handler, InitJson(1));

        Assert.Equal(200, status);
        Assert.False(string.IsNullOrEmpty(headers["Mcp-Session-Id"].ToString()));
        var response = Assert.IsType<JsonRpcResponse>(McpJsonDefaults.Deserialize(body));
        Assert.Equal((RequestId)1, response.Id);
    }

    /// <summary>
    /// The core regression: with a server that responds at the earliest possible point,
    /// the initialize POST and a following request must complete every time across many runs.
    /// Under the old ordering this would intermittently hang; here each POST is bounded by a
    /// timeout so a regression fails deterministically instead of hanging.
    /// </summary>
    [Fact]
    public async Task Handler_FastResponse_CompletesPost_UnderRepeatedRuns()
    {
        var handler = new StreamableHttpHandler(EchoHandler());

        for (int i = 0; i < 200; i++)
        {
            var (status, body, headers) = await PostAsync(handler, InitJson(i));
            Assert.Equal(200, status);
            var sid = headers["Mcp-Session-Id"].ToString();
            Assert.False(string.IsNullOrEmpty(sid));
            Assert.Equal((RequestId)i, ((JsonRpcResponse)McpJsonDefaults.Deserialize(body)).Id);

            var (status2, body2, _) = await PostAsync(handler, RequestJson(i + 1, "tools/list"), sid);
            Assert.Equal(200, status2);
            Assert.Equal((RequestId)(i + 1), ((JsonRpcResponse)McpJsonDefaults.Deserialize(body2)).Id);
        }
    }

    [Fact]
    public async Task Handler_ConcurrentInitialize_CorrectCorrelation_NoCrossRouting()
    {
        var handler = new StreamableHttpHandler(EchoHandler());

        var tasks = Enumerable.Range(0, 50).Select(async i =>
        {
            var (status, body, headers) = await PostAsync(handler, InitJson(i));
            Assert.Equal(200, status);
            // Each POST must receive the response correlated to its own id — never another's.
            Assert.Equal((RequestId)i, ((JsonRpcResponse)McpJsonDefaults.Deserialize(body)).Id);
            return headers["Mcp-Session-Id"].ToString();
        }).ToArray();

        var sessionIds = await Task.WhenAll(tasks);
        Assert.Equal(50, sessionIds.Where(s => !string.IsNullOrEmpty(s)).Distinct().Count());
    }

    // ---- Helpers ----

    /// <summary>A minimal server loop that immediately replies to every request.</summary>
    private static Func<IServerTransport, Task> EchoHandler() => async transport =>
    {
        await foreach (var message in transport.Messages)
        {
            if (message is JsonRpcRequest request)
                await transport.SendAsync(JsonRpcResponse.Success(request.Id));
        }
    };

    private static string InitJson(int id) =>
        $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"initialize\",\"params\":{{}}}}";

    private static string RequestJson(int id, string method) =>
        $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\",\"params\":{{}}}}";

    private static async Task<(int status, string body, IHeaderDictionary headers)> PostAsync(
        StreamableHttpHandler handler, string json, string? sessionId = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Headers.Accept = "application/json, text/event-stream";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        if (sessionId is not null)
            context.Request.Headers["Mcp-Session-Id"] = sessionId;

        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        // Bound each call so a correlation regression surfaces as a timeout, not a hang.
        await handler.HandleAsync(context).WaitAsync(Timeout);

        responseBody.Position = 0;
        var body = await new StreamReader(responseBody).ReadToEndAsync();
        return (context.Response.StatusCode, body, context.Response.Headers);
    }
}
