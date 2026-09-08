using System.Text.Json;
using Andy.MCP.AspNetCore;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;
using Andy.MCP.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Andy.MCP.Tests.Server;

public class TaskInputFlowTests
{
    private sealed class InputHandler : IElicitationHandler
    {
        public TaskCompletionSource<ElicitRequest> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ElicitResult> HandleAsync(ElicitRequest request, CancellationToken ct)
        {
            Entered.TrySetResult(request);
            await Release.Task.WaitAsync(ct);
            return ElicitResult.Accept(McpJsonDefaults.ToElement(new { confirmed = true }));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpToolInput_IsRelatedObservableAndResumable(bool sse)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var handler = new InputHandler();
        var continueExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedInput = new TaskCompletionSource<ElicitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapMcp("/mcp", server => server.AddTool("confirm", "confirm", async (_, ct) =>
        {
            var result = await server.ElicitAsync(ElicitRequest.Form("Continue?", new ElicitationSchema
            {
                Properties = new Dictionary<string, PrimitiveSchemaDefinition>()
            }) with
            { Meta = McpJsonDefaults.ToElement(new { vendor = 42 }) }, ct);
            receivedInput.TrySetResult(result);
            await continueExecution.Task.WaitAsync(ct);
            return CallToolResult.Text(result.Action);
        }), new StreamableHttpServerOptions { AllowAnonymous = true, UseSseResponses = sse });
        await app.StartAsync(timeout.Token);
        await using var client = await McpClient.ConnectAsync(new StreamableHttpClientTransport(new()
        {
            Endpoint = new Uri(app.Urls.Single() + "/mcp")
        }), new McpClientOptions { ElicitationHandler = handler }, cancellationToken: timeout.Token);
        var created = await client.CallToolAsTaskAsync("confirm", ct: timeout.Token);
        var request = await handler.Entered.Task.WaitAsync(timeout.Token);
        Assert.Equal(created.Task.TaskId, request.Meta!.Value.GetProperty("io.modelcontextprotocol/related-task").GetProperty("taskId").GetString());
        Assert.Equal(42, request.Meta.Value.GetProperty("vendor").GetInt32());
        Assert.Equal(McpTaskStatus.InputRequired, (await client.GetTaskAsync(created.Task.TaskId, timeout.Token)).Status);
        var pending = client.GetTaskResultAsync(created.Task.TaskId, timeout.Token);
        Assert.False(pending.IsCompleted);
        handler.Release.SetResult();
        var input = await receivedInput.Task.WaitAsync(timeout.Token);
        Assert.Equal(created.Task.TaskId, input.Meta!.Value.GetProperty("io.modelcontextprotocol/related-task").GetProperty("taskId").GetString());
        Assert.Equal(McpTaskStatus.Working, (await client.GetTaskAsync(created.Task.TaskId, timeout.Token)).Status);
        continueExecution.SetResult();
        var resultPayload = await pending;
        Assert.Equal("accept", resultPayload.Deserialize<CallToolResult>(McpJsonDefaults.Options)!.Content.OfType<TextContent>().Single().Text);
        await app.StopAsync(timeout.Token);
    }

    [Fact]
    public void ParallelInput_RequiresEveryResponse_AndCannotReviveCancelledTask()
    {
        var store = new InMemoryTaskStore();
        var task = store.Create(null, null);
        using (var context = new TaskExecutionContext(store, task.TaskId))
        {
            Assert.Same(context, TaskExecutionContext.Current);
            var first = context.BeginInput();
            var second = context.BeginInput();
            Assert.Equal(McpTaskStatus.InputRequired, store.Get(task.TaskId, null)!.Status);
            first.Dispose();
            first.Dispose();
            Assert.Equal(McpTaskStatus.InputRequired, store.Get(task.TaskId, null)!.Status);
            second.Dispose();
            Assert.Equal(McpTaskStatus.Working, store.Get(task.TaskId, null)!.Status);
            using var final = context.BeginInput();
            store.Cancel(task.TaskId, null);
        }
        Assert.Null(TaskExecutionContext.Current);
        Assert.Equal(McpTaskStatus.Cancelled, store.Get(task.TaskId, null)!.Status);
    }

    private sealed class NestedSampling : ISamplingHandler
    {
        public McpClient Client { get; set; } = null!;
        public async Task<CreateMessageResult> HandleAsync(CreateMessageRequest request, CancellationToken ct)
        {
            var result = await Client.CallToolAsync("input", ct: ct);
            return new CreateMessageResult { Role = Role.Assistant, Content = result.Content, Model = "nested" };
        }
    }

    [Fact]
    public async Task ClientSamplingInput_TransitionsAndResumes()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var handler = new NestedSampling();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(serverTransport);
        server.AddTool("input", "input", async (_, ct) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return CallToolResult.Text("input received");
        });
        var run = server.RunAsync(timeout.Token);
        await using var client = await McpClient.ConnectAsync(clientTransport,
            new McpClientOptions { SamplingHandler = handler }, cancellationToken: timeout.Token);
        handler.Client = client;
        var created = await server.CreateMessageAsTaskAsync(new CreateMessageRequest
        {
            Messages = [new SamplingMessage { Role = Role.User, Content = [new TextContent("hi")] }],
            MaxTokens = 10
        }, cancellationToken: timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        Assert.Equal(McpTaskStatus.InputRequired, (await server.GetClientTaskAsync(created.Task.TaskId, timeout.Token)).Status);
        var pending = server.GetClientTaskResultAsync(created.Task.TaskId, timeout.Token);
        release.SetResult();
        var result = await pending;
        Assert.Equal("nested", result.GetProperty("model").GetString());
        Assert.Equal(McpTaskStatus.Completed, (await server.GetClientTaskAsync(created.Task.TaskId, timeout.Token)).Status);
    }
}
