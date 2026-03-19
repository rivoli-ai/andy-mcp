namespace Andy.MCP.Protocol;

/// <summary>
/// Standard JSON-RPC 2.0 error codes and MCP-specific error codes.
/// </summary>
public static class McpErrorCodes
{
    // Standard JSON-RPC 2.0 error codes
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    // MCP-specific error codes
    public const int ResourceNotFound = -32002;
}
