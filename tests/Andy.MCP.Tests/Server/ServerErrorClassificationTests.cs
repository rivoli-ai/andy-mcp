using Andy.MCP.Protocol;
using Andy.MCP.Server;
using Andy.MCP.Transport;

namespace Andy.MCP.Tests.Server;

/// <summary>
/// Tests that the server returns correct JSON-RPC error codes and does not turn caller errors
/// (malformed params) into internal errors (issue #42).
/// </summary>
public class ServerErrorClassificationTests
{
    private static async Task<JsonRpcResponse> SendAfterInitAsync(
        JsonRpcRequest request, Action<McpServer>? configure = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (client, serverTransport) = InMemoryTransport.CreatePair();
        var server = new McpServer(serverTransport);
        configure?.Invoke(server);
        _ = server.RunAsync(cts.Token);
        await client.ConnectAsync(cts.Token);

        await client.SendAsync(new JsonRpcRequest
        {
            Id = 1,
            Method = McpMethods.Initialize,
            Params = McpJsonDefaults.ToElement(new InitializeParams
            {
                ProtocolVersion = McpSession.LatestProtocolVersion,
                Capabilities = new ClientCapabilities(),
                ClientInfo = new Implementation("c", "1.0")
            })
        }, cts.Token);
        await ReadResponseAsync(client, cts.Token); // init response
        await client.SendAsync(new JsonRpcNotification { Method = McpMethods.NotificationsInitialized }, cts.Token);

        await client.SendAsync(request, cts.Token);
        return await ReadResponseAsync(client, cts.Token);
    }

    private static async Task<JsonRpcResponse> ReadResponseAsync(IClientTransport client, CancellationToken ct)
    {
        await foreach (var msg in client.Messages.WithCancellation(ct))
            if (msg is JsonRpcResponse r) return r;
        throw new InvalidOperationException("No response received.");
    }

    [Fact]
    public async Task ToolsCall_MissingRequiredName_ReturnsInvalidParams()
    {
        var response = await SendAfterInitAsync(new JsonRpcRequest
        {
            Id = 2,
            Method = McpMethods.ToolsCall,
            Params = McpJsonDefaults.ToElement(new { arguments = new { } }) // no "name"
        });

        Assert.True(response.IsError);
        Assert.Equal(McpErrorCodes.InvalidParams, response.Error!.Code);
    }

    [Fact]
    public async Task ToolsCall_WronglyTypedName_ReturnsInvalidParams()
    {
        var response = await SendAfterInitAsync(new JsonRpcRequest
        {
            Id = 2,
            Method = McpMethods.ToolsCall,
            Params = McpJsonDefaults.ToElement(new { name = 123 }) // name must be a string
        });

        Assert.True(response.IsError);
        Assert.Equal(McpErrorCodes.InvalidParams, response.Error!.Code);
    }

    [Fact]
    public async Task UnknownMethod_ReturnsMethodNotFound()
    {
        var response = await SendAfterInitAsync(new JsonRpcRequest { Id = 2, Method = "no/such/method" });

        Assert.True(response.IsError);
        Assert.Equal(McpErrorCodes.MethodNotFound, response.Error!.Code);
    }
}
