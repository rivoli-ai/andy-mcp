using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;
namespace Andy.MCP.Tests.Transport;

public class PostSseResumptionTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    [Fact]
    public async Task ConcurrentPostStreams_ResumeTheirOwnCursors_AndStopAtTheirResponses()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var resumed = new ConcurrentBag<string>();
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            string data;
            if (request.Method == HttpMethod.Post)
            {
                var posted = Assert.IsType<JsonRpcRequest>(McpJsonDefaults.Deserialize(await request.Content!.ReadAsStringAsync(ct)));
                data = $"id: post-{posted.Id.AsNumber()}.0\nretry: 10\ndata: \n\n";
            }
            else
            {
                var cursor = Assert.Single(request.Headers.GetValues("Last-Event-ID"));
                resumed.Add(cursor);
                var id = long.Parse(cursor.Split('-')[1].Split('.')[0]);
                data = "data: " + McpJsonDefaults.Serialize(JsonRpcResponse.Success((RequestId)id)) + "\n\n" +
                    "data: {\"jsonrpc\":\"2.0\",\"method\":\"must-not-read-after-response\"}\n\n";
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "text/event-stream") };
        }));
        await using var transport = new StreamableHttpClientTransport(new StreamableHttpClientTransportOptions
        { Endpoint = new Uri("https://example.com/mcp"), HttpClient = http, EnableServerSseStream = false });
        await transport.ConnectAsync(timeout.Token);
        await Task.WhenAll(transport.SendAsync(new JsonRpcRequest { Id = 1, Method = "ping" }, timeout.Token),
            transport.SendAsync(new JsonRpcRequest { Id = 2, Method = "ping" }, timeout.Token));
        Assert.Equal(new[] { "post-1.0", "post-2.0" }, resumed.Order().ToArray());
        using var readTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await using var messages = transport.Messages.GetAsyncEnumerator(readTimeout.Token);
        var ids = new List<long>();
        for (var i = 0; i < 2; i++)
        {
            Assert.True(await messages.MoveNextAsync());
            ids.Add(Assert.IsType<JsonRpcResponse>(messages.Current).Id.AsNumber());
        }
        Assert.Equal(new long[] { 1, 2 }, ids.Order().ToArray());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await messages.MoveNextAsync());
    }

    [Fact]
    public async Task EofWithoutCursor_FailsInsteadOfReissuingTool()
    {
        var posts = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            posts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("", Encoding.UTF8, "text/event-stream") });
        }));
        await using var transport = new StreamableHttpClientTransport(new StreamableHttpClientTransportOptions
        { Endpoint = new Uri("https://example.com/mcp"), HttpClient = http, EnableServerSseStream = false });
        await transport.ConnectAsync();
        await Assert.ThrowsAsync<IOException>(() => transport.SendAsync(new JsonRpcRequest { Id = 1, Method = "tools/call" }));
        Assert.Equal(1, posts);
    }
}
