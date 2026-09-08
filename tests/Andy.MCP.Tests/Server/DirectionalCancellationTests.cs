using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Server;

public class DirectionalCancellationTests
{
    [Fact]
    public async Task CancellingInboundSampling_DoesNotCancelOverlappingOutboundTool()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var samplingCancellation = new CancellationTokenSource();
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(serverTransport);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AddTool("slow", "d", async (_, ct) =>
        {
            entered.SetResult(); await release.Task.WaitAsync(ct); return CallToolResult.Text("done");
        });
        _ = server.RunAsync(timeout.Token);
        var sampling = new CancellableSampling();
        await using var client = await McpClient.ConnectAsync(clientTransport,
            new McpClientOptions { SamplingHandler = sampling }, cancellationToken: timeout.Token);
        var tool = client.CallToolAsync("slow", ct: timeout.Token); // client id 2
        await entered.Task.WaitAsync(timeout.Token);
        await server.PingClientAsync(timeout.Token); // server id 1
        var sample = server.CreateMessageAsync(new CreateMessageRequest
        {
            Messages = [new SamplingMessage { Role = Role.User, Content = [new TextContent { Text = "hi" }] }],
            MaxTokens = 10
        }, samplingCancellation.Token); // server id 2
        await sampling.Started.Task.WaitAsync(timeout.Token);
        samplingCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sample);
        await sampling.Stopped.Task.WaitAsync(timeout.Token);
        Assert.False(tool.IsCompleted);
        release.SetResult();
        Assert.Equal("done", ((TextContent)(await tool).Content[0]).Text);
    }

    [Fact]
    public async Task ServerDisposal_AwaitsHandlerCleanup()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (ct, st) = InMemoryTransport.CreatePair();
        var server = new McpServer(st);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        server.AddTool("slow", "d", async (_, token) =>
        {
            try { started.SetResult(); await Task.Delay(Timeout.Infinite, token); return CallToolResult.Text("x"); }
            finally { await Task.Yield(); stopped = true; }
        });
        _ = server.RunAsync(timeout.Token);
        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: timeout.Token);
        var call = client.CallToolAsync("slow", ct: timeout.Token);
        await started.Task.WaitAsync(timeout.Token);
        await server.DisposeAsync();
        Assert.True(stopped);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    private sealed class CancellableSampling : ISamplingHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<CreateMessageResult> HandleAsync(CreateMessageRequest request, CancellationToken cancellationToken)
        {
            try { Started.SetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); throw new InvalidOperationException(); }
            finally { Stopped.SetResult(); }
        }
    }
}
