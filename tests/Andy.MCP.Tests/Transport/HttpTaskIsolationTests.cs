using System.Security.Claims;
using Andy.MCP.AspNetCore;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;
using Andy.MCP.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Andy.MCP.Tests.Transport;

public class HttpTaskIsolationTests
{
    [Fact]
    public async Task SharedStore_DoesNotShareTasksAcrossHttpSessions()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        // Test authentication fixture. Production uses a signature-validating handler.
        app.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", context.Request.Headers["Test-Subject"].ToString()),
                new Claim("iss", "https://auth.example"), new Claim("aud", "https://api.example/mcp")], "test"));
            await next(context);
        });
        app.MapMcp("/mcp", s => s.AddTool("quick", "d", (_, _) => Task.FromResult(CallToolResult.Text("done"))),
            new StreamableHttpServerOptions
            {
                Authorization = new McpHttpAuthorizationOptions { Resource = "https://api.example/mcp", Issuer = "https://auth.example" }
            }, new McpServerOptions { TaskStore = new InMemoryTaskStore(), TaskOwnerKey = "must-be-overridden" });
        await app.StartAsync(timeout.Token);
        var endpoint = new Uri(app.Urls.Single() + "/mcp");
        await using var a = await McpClient.ConnectAsync(new StreamableHttpClientTransport(new()
        {
            Endpoint = endpoint,
            AdditionalHeaders = new Dictionary<string, string> { ["Test-Subject"] = "alice" }
        }), cancellationToken: timeout.Token);
        await using var b = await McpClient.ConnectAsync(new StreamableHttpClientTransport(new()
        {
            Endpoint = endpoint,
            AdditionalHeaders = new Dictionary<string, string> { ["Test-Subject"] = "bob" }
        }), cancellationToken: timeout.Token);
        var created = await a.CallToolAsTaskAsync("quick", ttlMs: 60_000, ct: timeout.Token);
        var id = created.Task.TaskId;
        Assert.Equal(id, (await a.GetTaskAsync(id, timeout.Token)).TaskId);
        Assert.DoesNotContain(await b.ListTasksAsync(timeout.Token), t => t.TaskId == id);
        await Assert.ThrowsAsync<McpException>(() => b.GetTaskAsync(id, timeout.Token));
        await Assert.ThrowsAsync<McpException>(() => b.GetTaskResultAsync(id, timeout.Token));
        await Assert.ThrowsAsync<McpException>(() => b.CancelTaskAsync(id, timeout.Token));
        await app.StopAsync(timeout.Token);
    }
}
