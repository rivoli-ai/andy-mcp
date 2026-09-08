using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;

namespace Andy.MCP.Tests.Client;

public class ClientRevisionReplyTests
{
    [Theory]
    [InlineData("2024-11-05", JsonValueKind.Object)]
    [InlineData("2025-03-26", JsonValueKind.Object)]
    [InlineData("2025-06-18", JsonValueKind.Object)]
    [InlineData("2025-11-25", JsonValueKind.Array)]
    public async Task SamplingReply_UsesNegotiatedRevision(string version, JsonValueKind expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (ct, st) = InMemoryTransport.CreatePair();
        await using var server = st;
        var reply = new TaskCompletionSource<JsonRpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loop = Task.Run(async () =>
        {
            await st.StartAsync(timeout.Token);
            await foreach (var message in st.Messages.WithCancellation(timeout.Token))
            {
                if (message is JsonRpcRequest { Method: McpMethods.Initialize } init)
                    await st.SendAsync(JsonRpcResponse.Success(init.Id, McpJsonDefaults.ToElement(new InitializeResult
                    {
                        ProtocolVersion = version,
                        Capabilities = new(),
                        ServerInfo = new("server", "1")
                    })), timeout.Token);
                else if (message is JsonRpcNotification { Method: McpMethods.NotificationsInitialized })
                    await st.SendAsync(new JsonRpcRequest
                    {
                        Id = 77,
                        Method = McpMethods.SamplingCreateMessage,
                        Params = RevisionAwareJson.ToElementForRevision(new CreateMessageRequest
                        {
                            MaxTokens = 5,
                            Messages = [new SamplingMessage { Role = Role.User, Content = [new TextContent("hi")] }]
                        }, ProtocolRevision.TryGet(version)!)
                    }, timeout.Token);
                else if (message is JsonRpcResponse response) { reply.SetResult(response); return; }
            }
        }, timeout.Token);
        await using var client = await McpClient.ConnectAsync(ct,
            new McpClientOptions { SamplingHandler = new Sampling() }, cancellationToken: timeout.Token);
        var response = await reply.Task.WaitAsync(timeout.Token);
        Assert.False(response.IsError);
        Assert.Equal(expected, response.Result!.Value.GetProperty("content").ValueKind);
        await loop;
    }

    [Fact]
    public void Capabilities_PreserveExtensions_AndOnlyAdvertiseConfiguredHandlers()
    {
        var options = new McpClientOptions
        {
            Capabilities = new ClientCapabilities
            {
                Roots = new(),
                Sampling = new(),
                Elicitation = new(),
                ExtensionData = new() { ["vendor/support"] = McpJsonDefaults.ToElement(new { enabled = true }) }
            }
        };
        var caps = options.BuildCapabilities();
        Assert.Null(caps.Roots); Assert.Null(caps.Sampling); Assert.Null(caps.Elicitation); Assert.Null(caps.Tasks);
        Assert.True(McpJsonDefaults.ToElement(caps).GetProperty("vendor/support").GetProperty("enabled").GetBoolean());
    }

    private sealed class Sampling : ISamplingHandler
    {
        public Task<CreateMessageResult> HandleAsync(CreateMessageRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new CreateMessageResult { Role = Role.Assistant, Model = "test", Content = [new TextContent("ok")] });
    }
}
