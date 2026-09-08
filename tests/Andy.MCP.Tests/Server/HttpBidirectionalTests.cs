using Andy.MCP.AspNetCore;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;
using Andy.MCP.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Andy.MCP.Tests.Server;

public class HttpBidirectionalTests
{
    [Fact]
    public async Task RealHttp_ConcurrentRequestsInBothDirections_AreCorrelated()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        McpServer? server = null;
        app.MapMcp("/mcp", s =>
        {
            server = s;
            s.AddTool("echo", "echo", (_, _) => Task.FromResult(CallToolResult.Text("ok")));
        }, new StreamableHttpServerOptions { AllowAnonymous = true });
        await app.StartAsync(timeout.Token);
        await using var transport = new StreamableHttpClientTransport(new StreamableHttpClientTransportOptions { Endpoint = new Uri(app.Urls.Single() + "/mcp") });
        await using var client = await McpClient.ConnectAsync(transport, cancellationToken: timeout.Token);
        Assert.NotNull(server);
        // A round trip establishes receipt of notifications/initialized before server requests.
        await client.ListToolsAsync(timeout.Token);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            var list = client.ListToolsAsync(timeout.Token);
            var ping = server.PingClientAsync(timeout.Token);
            await Task.WhenAll(list, ping);
            Assert.Contains(await list, t => t.Name == "echo");
        }));
        await app.StopAsync(timeout.Token);
    }
}
