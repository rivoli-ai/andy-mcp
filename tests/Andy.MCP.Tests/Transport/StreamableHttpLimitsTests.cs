using System.Text;
using Andy.MCP.AspNetCore;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;
using Microsoft.AspNetCore.Http;

namespace Andy.MCP.Tests;

/// <summary>
/// Tests fail-closed resource limits on the Streamable HTTP handler (issue #46): request body
/// size and maximum concurrent sessions.
/// </summary>
public class StreamableHttpLimitsTests
{
    private static StreamableHttpHandler NewHandler(StreamableHttpServerOptions options) =>
        new(async transport =>
        {
            await foreach (var message in transport.Messages)
                if (message is JsonRpcRequest request)
                    await transport.SendAsync(JsonRpcResponse.Success(request.Id));
        }, options);

    private static DefaultHttpContext InitContext(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Headers.Accept = "application/json, text/event-stream";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    [Fact]
    public async Task OversizedBody_IsRejectedWith413()
    {
        var handler = NewHandler(new StreamableHttpServerOptions { MaxRequestBodyBytes = 64 });

        // Build a valid-but-large initialize request whose body exceeds 64 bytes.
        var big = new string('x', 200);
        var body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"note\":\"" + big + "\"}}";
        var context = InitContext(body);

        await handler.HandleAsync(context).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(413, context.Response.StatusCode);
    }

    [Fact]
    public async Task MaxSessions_Exceeded_IsRejectedWith503()
    {
        var handler = NewHandler(new StreamableHttpServerOptions { MaxSessions = 1 });
        const string init = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";

        var first = InitContext(init);
        await handler.HandleAsync(first).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(200, first.Response.StatusCode);

        var second = InitContext(init);
        await handler.HandleAsync(second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(503, second.Response.StatusCode);
    }

    [Fact]
    public async Task WithinLimits_Succeeds()
    {
        var handler = NewHandler(new StreamableHttpServerOptions());
        var context = InitContext("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");

        await handler.HandleAsync(context).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(200, context.Response.StatusCode);
    }
}
