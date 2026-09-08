using Andy.MCP.AspNetCore;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
namespace Andy.MCP.Tests.Transport;

public class HttpRevisionIntegrationTests
{
    [Theory]
    [InlineData("2025-03-26", false)]
    [InlineData("2025-06-18", false)]
    [InlineData("2025-11-25", false)]
    [InlineData("2025-03-26", true)]
    [InlineData("2025-06-18", true)]
    [InlineData("2025-11-25", true)]
    public async Task EveryHttpRevision_NegotiatesAndCompletesJsonOrSseRequests(string revision, bool sse)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapMcp("/mcp", server => server.AddTool("echo", "test", (_, _) => Task.FromResult(CallToolResult.Text("ok"))),
            new StreamableHttpServerOptions { AllowAnonymous = true, UseSseResponses = sse });
        await app.StartAsync(timeout.Token);
        await using var transport = new StreamableHttpClientTransport(new()
        { Endpoint = new Uri(app.Urls.Single() + "/mcp"), EnableServerSseStream = false });
        await transport.ConnectAsync(timeout.Token);
        await using var messages = transport.Messages.GetAsyncEnumerator(timeout.Token);
        await transport.SendAsync(new JsonRpcRequest
        {
            Id = 1,
            Method = "initialize",
            Params = McpJsonDefaults.ToElement(new InitializeParams
            { ProtocolVersion = revision, Capabilities = new ClientCapabilities(), ClientInfo = new Implementation("revision-test", "1") })
        }, timeout.Token);
        Assert.True(await messages.MoveNextAsync());
        var init = Assert.IsType<JsonRpcResponse>(messages.Current);
        Assert.Equal(revision, init.Result!.Value.GetProperty("protocolVersion").GetString());
        await transport.SendAsync(new JsonRpcNotification { Method = "notifications/initialized" }, timeout.Token);
        await transport.SendAsync(new JsonRpcRequest { Id = 2, Method = "tools/list" }, timeout.Token);
        Assert.True(await messages.MoveNextAsync());
        var tools = Assert.IsType<JsonRpcResponse>(messages.Current);
        Assert.Equal((RequestId)2, tools.Id);
        Assert.False(tools.IsError);
        Assert.Equal("echo", tools.Result!.Value.GetProperty("tools")[0].GetProperty("name").GetString());
        await transport.SendAsync(new JsonRpcRequest { Id = 3, Method = "tools/call", Params = McpJsonDefaults.ToElement(new { name = "echo" }) }, timeout.Token);
        Assert.True(await messages.MoveNextAsync());
        var result = Assert.IsType<JsonRpcResponse>(messages.Current);
        Assert.False(result.IsError);
        Assert.Equal("ok", result.Result!.Value.GetProperty("content")[0].GetProperty("text").GetString());
        await app.StopAsync(timeout.Token);
    }

    private sealed class NeverSend : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new InvalidOperationException("Legacy HTTP must not be sent.");
    }

    [Fact]
    public async Task LegacyHttpRevision_IsRejectedBeforeSending()
    {
        using var http = new HttpClient(new NeverSend());
        await using var transport = new StreamableHttpClientTransport(new()
        { Endpoint = new Uri("https://example.com/mcp"), HttpClient = http, EnableServerSseStream = false });
        await transport.ConnectAsync();
        await Assert.ThrowsAsync<NotSupportedException>(() => transport.SendAsync(new JsonRpcRequest
        { Id = 1, Method = "initialize", Params = McpJsonDefaults.ToElement(new { protocolVersion = "2024-11-05" }) }));
    }
}
