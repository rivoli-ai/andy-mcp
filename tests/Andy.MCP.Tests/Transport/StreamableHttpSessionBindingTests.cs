using System.Security.Claims;
using System.Text;
using Andy.MCP.AspNetCore;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;
using Microsoft.AspNetCore.Http;

namespace Andy.MCP.Tests;

/// <summary>
/// Tests that Streamable HTTP sessions are bound to the authenticated principal that created them
/// and reject cross-user reuse (issue #46).
/// </summary>
public class StreamableHttpSessionBindingTests
{
    private static StreamableHttpHandler NewHandler() =>
        new(async transport =>
        {
            await foreach (var message in transport.Messages)
                if (message is JsonRpcRequest request)
                    await transport.SendAsync(JsonRpcResponse.Success(request.Id));
        });

    private static DefaultHttpContext BuildContext(string method, string json, string? sessionId, string? userId)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.ContentType = "application/json";
        context.Request.Headers.Accept = "application/json, text/event-stream";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        if (sessionId is not null)
            context.Request.Headers["Mcp-Session-Id"] = sessionId;
        if (userId is not null)
        {
            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "TestAuth");
            context.User = new ClaimsPrincipal(identity);
        }
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<(int status, string sessionId)> InitializeAsync(StreamableHttpHandler handler, string? userId)
    {
        var context = BuildContext("POST", """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""", null, userId);
        await handler.HandleAsync(context).WaitAsync(TimeSpan.FromSeconds(5));
        return (context.Response.StatusCode, context.Response.Headers["Mcp-Session-Id"].ToString());
    }

    private static async Task<int> PingAsync(StreamableHttpHandler handler, string sessionId, string? userId, string method = "POST")
    {
        var context = BuildContext(method, """{"jsonrpc":"2.0","id":2,"method":"ping"}""", sessionId, userId);
        await handler.HandleAsync(context).WaitAsync(TimeSpan.FromSeconds(5));
        return context.Response.StatusCode;
    }

    [Fact]
    public async Task SameUser_CanReuseSession()
    {
        var handler = NewHandler();
        var (status, sid) = await InitializeAsync(handler, "user-a");
        Assert.Equal(200, status);

        Assert.Equal(200, await PingAsync(handler, sid, "user-a"));
    }

    [Fact]
    public async Task DifferentUser_IsRejectedWith403()
    {
        var handler = NewHandler();
        var (_, sid) = await InitializeAsync(handler, "user-a");

        Assert.Equal(403, await PingAsync(handler, sid, "user-b"));
    }

    [Fact]
    public async Task AnonymousRequest_ToUserSession_IsRejectedWith403()
    {
        var handler = NewHandler();
        var (_, sid) = await InitializeAsync(handler, "user-a");

        Assert.Equal(403, await PingAsync(handler, sid, userId: null));
    }

    [Fact]
    public async Task AnonymousSession_AllowsAnonymousReuse()
    {
        var handler = NewHandler();
        var (_, sid) = await InitializeAsync(handler, userId: null);

        Assert.Equal(200, await PingAsync(handler, sid, userId: null));
    }

    [Fact]
    public async Task Delete_ByDifferentUser_IsRejectedWith403()
    {
        var handler = NewHandler();
        var (_, sid) = await InitializeAsync(handler, "user-a");

        var context = BuildContext("DELETE", "{}", sid, "user-b");
        await handler.HandleAsync(context).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(403, context.Response.StatusCode);
    }

    [Fact]
    public async Task SessionId_DoesNotContainUserIdentifier()
    {
        var handler = NewHandler();
        var (_, sid) = await InitializeAsync(handler, "user-a");

        Assert.DoesNotContain("user-a", sid);
        Assert.True(sid.Length >= 20); // 24 random bytes, base64url
    }
}
