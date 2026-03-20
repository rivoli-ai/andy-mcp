namespace Andy.MCP.Transport;

using Andy.MCP.Protocol;

/// <summary>
/// A message-oriented transport for MCP communication.
/// Transports handle framing and delivery; the session layer handles request/response correlation.
/// </summary>
public interface ITransport : IAsyncDisposable
{
    /// <summary>
    /// Send a JSON-RPC message through the transport.
    /// </summary>
    Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stream of incoming JSON-RPC messages from the remote peer.
    /// </summary>
    IAsyncEnumerable<JsonRpcMessage> Messages { get; }

    /// <summary>
    /// Whether the transport is currently connected.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Fired when the transport disconnects (process exit, connection close, error).
    /// </summary>
    event EventHandler<TransportDisconnectedEventArgs>? Disconnected;
}

/// <summary>
/// Client-side transport: connects to an MCP server.
/// </summary>
public interface IClientTransport : ITransport
{
    /// <summary>
    /// Establish the transport connection (e.g., launch subprocess, open HTTP connection).
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Server-side transport: accepts connections from MCP clients.
/// </summary>
public interface IServerTransport : ITransport
{
    /// <summary>
    /// Start accepting connections.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Event args for transport disconnection.
/// </summary>
public sealed class TransportDisconnectedEventArgs : EventArgs
{
    /// <summary>
    /// The reason for disconnection, if known.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// The exception that caused disconnection, if any.
    /// </summary>
    public Exception? Exception { get; init; }

    /// <summary>
    /// Process exit code, for stdio transports.
    /// </summary>
    public int? ExitCode { get; init; }
}
