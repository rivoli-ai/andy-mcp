using System.Net;
using System.Text;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;
namespace Andy.MCP.Tests.Transport;

public class HttpRequestDeadlineTests
{
    private sealed class BlockingServer : HttpMessageHandler
    {
        public readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var message = McpJsonDefaults.Deserialize(await request.Content!.ReadAsStringAsync(ct));
            if (message is JsonRpcRequest { Method: "initialize" } init)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(McpJsonDefaults.Serialize(JsonRpcResponse.Success(init.Id, McpJsonDefaults.ToElement(new InitializeResult
                    { ProtocolVersion = "2025-11-25", Capabilities = new ServerCapabilities { Tools = new() }, ServerInfo = new Implementation("test", "1") }))), Encoding.UTF8, "application/json")
                };
            if (message is JsonRpcNotification) return new HttpResponseMessage(HttpStatusCode.Accepted);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { Cancelled.TrySetResult(); }
            throw new InvalidOperationException("Unreachable");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrackedDeadline_InterruptsBlockingHttpPost(bool maximum)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var handler = new BlockingServer();
        using var http = new HttpClient(handler);
        await using var transport = new StreamableHttpClientTransport(new StreamableHttpClientTransportOptions
        { Endpoint = new Uri("https://example.com/mcp"), HttpClient = http, EnableServerSseStream = false });
        await using var client = await McpClient.ConnectAsync(transport, cancellationToken: timeout.Token);
        var options = maximum
            ? new McpRequestOptions { Timeout = Timeout.InfiniteTimeSpan, MaximumDuration = TimeSpan.FromMilliseconds(75) }
            : new McpRequestOptions { Timeout = TimeSpan.FromMilliseconds(75) };
        await Assert.ThrowsAsync<TimeoutException>(() => client.CallToolAsync(new CallToolRequest { Name = "blocked" }, options, timeout.Token));
        await handler.Cancelled.Task.WaitAsync(timeout.Token);
        Assert.Equal(0, client.PendingRequestCount);
    }
}
