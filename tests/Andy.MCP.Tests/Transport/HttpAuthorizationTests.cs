using System.Security.Claims;
using System.Text;
using Andy.MCP.AspNetCore;
using Andy.MCP.Protocol;
using Microsoft.AspNetCore.Http;

namespace Andy.MCP.Tests.Transport;

public class HttpAuthorizationTests
{
    private static readonly McpHttpAuthorizationOptions Policy = new()
    {
        Resource = "https://api.example/mcp", Issuer = "https://auth.example", RequiredScopes = ["tools:read"]
    };
    private static StreamableHttpHandler Handler(StreamableHttpServerOptions? options = null) =>
        new(async transport =>
        {
            await foreach (var message in transport.Messages)
                if (message is JsonRpcRequest request) await transport.SendAsync(JsonRpcResponse.Success(request.Id));
        }, options ?? new() { Authorization = Policy });

    private static DefaultHttpContext Context(string? subject = "alice", string issuer = "https://auth.example",
        string audience = "https://api.example/mcp", string scope = "tools:read", string? sid = null, string method = "POST")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.ContentType = "application/json";
        ctx.Request.Headers.Accept = "application/json, text/event-stream";
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            sid is null ? """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""" :
                """{"jsonrpc":"2.0","id":2,"method":"ping"}"""));
        ctx.Response.Body = new MemoryStream();
        if (sid is not null) ctx.Request.Headers["Mcp-Session-Id"] = sid;
        if (subject is not null)
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", subject), new Claim("iss", issuer), new Claim("aud", audience), new Claim("scope", scope)], "validated"));
        return ctx;
    }

    [Fact]
    public async Task MissingPolicy_FailsClosed()
    {
        using var handler = Handler(new());
        var ctx = Context();
        await handler.HandleAsync(ctx);
        Assert.Equal(503, ctx.Response.StatusCode);
    }

    [Theory]
    [InlineData(null, "https://auth.example", "https://api.example/mcp", "tools:read", 401)]
    [InlineData("alice", "https://evil.example", "https://api.example/mcp", "tools:read", 401)]
    [InlineData("alice", "https://auth.example", "https://different.example", "tools:read", 401)]
    [InlineData("alice", "https://auth.example", "https://api.example/mcp", "Tools:Read", 403)]
    public async Task InvalidAuthorization_ReturnsChallenge(string? subject, string issuer, string audience, string scope, int status)
    {
        using var handler = Handler();
        var ctx = Context(subject, issuer, audience, scope);
        await handler.HandleAsync(ctx);
        Assert.Equal(status, ctx.Response.StatusCode);
        Assert.Contains(Policy.MetadataUrl, ctx.Response.Headers.WWWAuthenticate.ToString());
        Assert.Contains(status == 403 ? "insufficient_scope" : "invalid_token", ctx.Response.Headers.WWWAuthenticate.ToString());
        Assert.False(ctx.Response.Headers.ContainsKey("Mcp-Session-Id"));
    }

    [Fact]
    public async Task PresentOrigin_IsDeniedUnlessExplicitlyAllowed()
    {
        using var denied = Handler();
        var ctx = Context();
        ctx.Request.Headers.Origin = "https://ui.example";
        await denied.HandleAsync(ctx);
        Assert.Equal(403, ctx.Response.StatusCode);
        using var allowed = Handler(new() { Authorization = Policy, ValidateOrigin = o => o == "https://ui.example" });
        var accepted = Context();
        accepted.Request.Headers.Origin = "https://ui.example";
        await allowed.HandleAsync(accepted);
        Assert.Equal(200, accepted.Response.StatusCode);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task DifferentUser_CannotReuseSession(string method)
    {
        using var handler = Handler();
        var init = Context();
        await handler.HandleAsync(init);
        var other = Context("bob", sid: init.Response.Headers["Mcp-Session-Id"].ToString(), method: method);
        await handler.HandleAsync(other);
        Assert.Equal(403, other.Response.StatusCode);
    }

    [Fact]
    public async Task ExpiredSession_CannotBeReused()
    {
        var clock = new TestClock();
        using var handler = Handler(new() { Authorization = Policy, TimeProvider = clock, SessionTimeout = TimeSpan.FromMinutes(1) });
        var init = Context();
        await handler.HandleAsync(init);
        clock.Now += TimeSpan.FromMinutes(2);
        var request = Context(sid: init.Response.Headers["Mcp-Session-Id"].ToString());
        await handler.HandleAsync(request);
        Assert.Equal(404, request.Response.StatusCode);
    }

    [Fact]
    public async Task LocalAnonymousUse_RequiresExplicitOptOut()
    {
        using var handler = Handler(new() { AllowAnonymous = true });
        var ctx = Context(null);
        await handler.HandleAsync(ctx);
        Assert.Equal(200, ctx.Response.StatusCode);
    }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
