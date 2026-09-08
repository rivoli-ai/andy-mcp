using Andy.MCP.Protocol;
using Andy.MCP.AspNetCore;
using Microsoft.AspNetCore.Http;
using System.Text;
using System.Text.Json;

namespace Andy.MCP.Tests.Protocol;

public class StrictMessageTests
{
    [Theory]
    [InlineData("""{"jsonrpc":2,"id":1,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":null}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":false}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":""}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"ping","result":{}}""")]
    [InlineData("""{"jsonrpc":"2.0","method":"ping","error":{"code":1,"message":"x"}}""")]
    [InlineData("""{"jsonrpc":"2.0","method":"ping","params":[]}""")]
    [InlineData("""{"jsonrpc":"2.0","method":"ping","params":null}""")]
    [InlineData("""{"jsonrpc":"2.0","id":false,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1.5,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":9223372036854775808,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"id":2,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"result":null}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"result":[] }""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"result":{},"params":{}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"error":null}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"error":{"code":"bad","message":"x"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"error":{"code":1.5,"message":"x"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"error":{"code":1,"message":null}}""")]
    public void InvalidEnvelope_IsAProtocolError(string json) =>
        Assert.Throws<JsonRpcParseException>(() => McpJsonDefaults.Deserialize(json));

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","error":{"code":-32700,"message":"Parse error"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":null,"error":{"code":-32700,"message":"Parse error"}}""")]
    public void UncorrelatedError_RoundTripsWithoutInventingAnId(string json)
    {
        var message = Assert.IsType<JsonRpcUncorrelatedError>(McpJsonDefaults.Deserialize(json));
        var result = Assert.IsType<JsonRpcUncorrelatedError>(
            McpJsonDefaults.Deserialize(McpJsonDefaults.Serialize(message)));
        Assert.Equal(-32700, result.Error.Code);
    }

    [Theory]
    [InlineData("{", McpErrorCodes.ParseError)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":null}""", McpErrorCodes.InvalidRequest)]
    public async Task HttpMalformedMessage_ReturnsClassifiedJsonError(string input, int code)
    {
        var handler = new StreamableHttpHandler(_ => Task.CompletedTask, new StreamableHttpServerOptions { AllowAnonymous = true });
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Headers.Accept = "application/json, text/event-stream";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(input));
        context.Response.Body = new MemoryStream();
        await handler.HandleAsync(context);
        Assert.Equal(400, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(code, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }
}
