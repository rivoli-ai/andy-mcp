using System.Text;
using Andy.MCP.AspNetCore;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;
using Microsoft.AspNetCore.Http;

namespace Andy.MCP.Tests;

/// <summary>
/// Tests Streamable HTTP request validation and status codes (issue #44): Content-Type, Accept,
/// and method are validated before dispatch.
/// </summary>
public class StreamableHttpValidationTests
{
    private static StreamableHttpHandler NewHandler() =>
        new(async transport =>
        {
            await foreach (var message in transport.Messages)
                if (message is JsonRpcRequest request)
                    await transport.SendAsync(JsonRpcResponse.Success(request.Id));
        });

    private static async Task<int> StatusForAsync(
        string method, string? contentType, string? accept, string body = "{}")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        if (contentType is not null)
            context.Request.ContentType = contentType;
        if (accept is not null)
            context.Request.Headers.Accept = accept;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();

        await NewHandler().HandleAsync(context).WaitAsync(TimeSpan.FromSeconds(5));
        return context.Response.StatusCode;
    }

    [Fact]
    public async Task Post_WithoutJsonContentType_Returns415()
    {
        Assert.Equal(415, await StatusForAsync("POST", "text/plain", "application/json, text/event-stream"));
    }

    [Fact]
    public async Task Post_WithoutContentType_Returns415()
    {
        Assert.Equal(415, await StatusForAsync("POST", null, "application/json, text/event-stream"));
    }

    [Fact]
    public async Task Post_WithoutRequiredAccept_Returns406()
    {
        // Missing text/event-stream in Accept.
        Assert.Equal(406, await StatusForAsync("POST", "application/json", "application/json"));
    }

    [Fact]
    public async Task Post_WithoutAcceptHeader_Returns406()
    {
        Assert.Equal(406, await StatusForAsync("POST", "application/json", null));
    }

    [Fact]
    public async Task Get_WithoutEventStreamAccept_Returns406()
    {
        Assert.Equal(406, await StatusForAsync("GET", null, "application/json"));
    }

    [Fact]
    public async Task Unsupported_Method_Returns405()
    {
        Assert.Equal(405, await StatusForAsync("PUT", "application/json", "application/json, text/event-stream"));
    }

    [Fact]
    public async Task Post_WithWildcardAccept_PassesValidation()
    {
        // */* satisfies the Accept requirement; a valid initialize then yields a 200 with a session.
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Headers.Accept = "*/*";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}"""));
        context.Response.Body = new MemoryStream();

        await NewHandler().HandleAsync(context).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(200, context.Response.StatusCode);
        Assert.False(string.IsNullOrEmpty(context.Response.Headers["Mcp-Session-Id"].ToString()));
    }
}
