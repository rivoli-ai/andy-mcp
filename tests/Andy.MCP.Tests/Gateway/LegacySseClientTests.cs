using System.Collections.Concurrent;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;
using Andy.MCP.Transport;
using Andy.MCP.Transport.Sse;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Andy.MCP.Tests.Gateway;

public class LegacySseClientTests
{
    [Fact]
    public async Task LegacySessionInitializesCallsToolsAndClosesOnStreamFailure()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var sessions = new ConcurrentDictionary<string, InMemoryClientTransport>();
        using var close = new CancellationTokenSource();
        Func<System.Text.Json.JsonElement?, CancellationToken, Task<CallToolResult>> echo = (_, _) => Task.FromResult(CallToolResult.Text("legacy result"));
        app.MapGet("/sse", async context =>
        {
            var id = Guid.NewGuid().ToString("N");
            var (transport, serverTransport) = InMemoryTransport.CreatePair();
            await using var server = new McpServer(serverTransport);
            server.AddTool("echo", "Echo", echo);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, close.Token);
            var running = server.RunAsync(stop.Token);
            await transport.ConnectAsync(stop.Token);
            sessions[id] = transport;
            context.Response.ContentType = "Text/Event-Stream";
            var writer = new SseWriter(context.Response.Body);
            try
            {
                await writer.WriteEventAsync(new() { EventType = "endpoint", Data = "/messages?session=" + id }, stop.Token);
                await foreach (var message in transport.Messages.WithCancellation(stop.Token))
                    await writer.WriteEventAsync(new() { Data = McpJsonDefaults.Serialize(message) }, stop.Token);
            }
            catch (OperationCanceledException) { }
            finally
            {
                sessions.TryRemove(id, out _);
                await stop.CancelAsync();
                await transport.DisposeAsync();
                try { await running; } catch (OperationCanceledException) { }
            }
        });
        app.MapPost("/messages", async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var message = McpJsonDefaults.Deserialize(await reader.ReadToEndAsync(context.RequestAborted))!;
            if (message is JsonRpcRequest { Method: "initialize" } initialize)
                message = new JsonRpcRequest
                {
                    Id = initialize.Id,
                    Method = initialize.Method,
                    Params = System.Text.Json.JsonSerializer.SerializeToElement(new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "legacy-test", version = "1" } })
                };
            await sessions[context.Request.Query["session"].ToString()].SendAsync(message, context.RequestAborted);
            context.Response.StatusCode = 202;
        });
        await app.StartAsync();
        try
        {
            var transport = new LegacySseClientTransport(new Uri(app.Urls.Single() + "/sse"));
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.Disconnected += (_, _) => disconnected.TrySetResult();
            await using var client = await McpClient.ConnectAsync(transport);
            Assert.Equal("2024-11-05", client.Session.ProtocolVersion);
            Assert.Single(await client.ListToolsAsync());
            Assert.Equal("legacy result", ((TextContent)(await client.CallToolAsync("echo")).Content[0]).Text);
            await close.CancelAsync();
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(transport.IsConnected);
        }
        finally { await app.StopAsync(); }
    }

    [Theory]
    [InlineData("https://other.test/messages", "text/event-stream")]
    [InlineData("/messages", "application/json")]
    public async Task RejectsCrossOriginEndpointAndWrongContentType(string endpoint, string contentType)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/sse", async context =>
        {
            context.Response.ContentType = contentType;
            await context.Response.WriteAsync("event: endpoint\ndata: " + endpoint + "\n\n");
        });
        await app.StartAsync();
        try
        {
            await using var transport = new LegacySseClientTransport(new Uri(app.Urls.Single() + "/sse"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => transport.ConnectAsync());
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task MissingEndpointIsBoundedByConnectionTimeout()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/sse", async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.StartAsync();
            try { await Task.Delay(Timeout.Infinite, context.RequestAborted); } catch (OperationCanceledException) { }
        });
        await app.StartAsync();
        try
        {
            await using var transport = new LegacySseClientTransport(new Uri(app.Urls.Single() + "/sse"), requestTimeout: TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.ConnectAsync());
            Assert.False(transport.IsConnected);
        }
        finally { await app.StopAsync(); }
    }
}
