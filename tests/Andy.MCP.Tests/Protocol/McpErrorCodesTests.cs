using Andy.MCP.Protocol;

namespace Andy.MCP.Tests.Protocol;

public class McpErrorCodesTests
{
    [Fact]
    public void StandardErrorCodes_HaveCorrectValues()
    {
        Assert.Equal(-32700, McpErrorCodes.ParseError);
        Assert.Equal(-32600, McpErrorCodes.InvalidRequest);
        Assert.Equal(-32601, McpErrorCodes.MethodNotFound);
        Assert.Equal(-32602, McpErrorCodes.InvalidParams);
        Assert.Equal(-32603, McpErrorCodes.InternalError);
    }

    [Fact]
    public void McpErrorCodes_HaveCorrectValues()
    {
        Assert.Equal(-32002, McpErrorCodes.ResourceNotFound);
    }

    [Fact]
    public void JsonRpcError_FactoryMethods_ProduceCorrectCodes()
    {
        Assert.Equal(McpErrorCodes.ParseError, JsonRpcError.ParseError().Code);
        Assert.Equal(McpErrorCodes.InvalidRequest, JsonRpcError.InvalidRequest().Code);
        Assert.Equal(McpErrorCodes.MethodNotFound, JsonRpcError.MethodNotFound().Code);
        Assert.Equal(McpErrorCodes.InvalidParams, JsonRpcError.InvalidParams().Code);
        Assert.Equal(McpErrorCodes.InternalError, JsonRpcError.InternalError().Code);
        Assert.Equal(McpErrorCodes.ResourceNotFound, JsonRpcError.ResourceNotFound().Code);
    }

    [Fact]
    public void JsonRpcError_FactoryMethods_WithCustomMessage()
    {
        var error = JsonRpcError.MethodNotFound("No such method: foo");
        Assert.Equal(McpErrorCodes.MethodNotFound, error.Code);
        Assert.Equal("No such method: foo", error.Message);
    }

    [Fact]
    public void JsonRpcError_FactoryMethods_WithDefaultMessage()
    {
        var error = JsonRpcError.InternalError();
        Assert.Equal("Internal error", error.Message);
    }
}
