namespace Andy.MCP.Protocol;

/// <summary>
/// Thrown when a JSON-RPC message cannot be parsed or is structurally invalid.
/// </summary>
public class JsonRpcParseException : Exception
{
    public JsonRpcParseException(string message) : base(message) { }
    public JsonRpcParseException(string message, Exception innerException) : base(message, innerException) { }
}
