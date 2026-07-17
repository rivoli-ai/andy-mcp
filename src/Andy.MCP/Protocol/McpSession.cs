using System.Text.Json;

namespace Andy.MCP.Protocol;

/// <summary>
/// Lifecycle states for an MCP session.
/// </summary>
public enum McpSessionState
{
    Uninitialized,
    Initializing,
    Ready,
    ShuttingDown,
    Closed
}

/// <summary>
/// Tracks the lifecycle state and negotiated capabilities of an MCP session.
/// Thread-safe for concurrent access.
/// </summary>
public sealed class McpSession
{
    /// <summary>
    /// The latest protocol version supported by this implementation.
    /// </summary>
    public static readonly string LatestProtocolVersion = ProtocolRevision.Latest.Version;

    /// <summary>
    /// All protocol versions this implementation supports, newest first.
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedProtocolVersions = ProtocolRevision.AllVersions;

    private int _state = (int)McpSessionState.Uninitialized;

    public McpSessionState State => (McpSessionState)_state;

    /// <summary>
    /// The protocol version agreed upon during initialization.
    /// </summary>
    public string? ProtocolVersion { get; private set; }

    /// <summary>
    /// The negotiated protocol revision and its feature set, or null if not yet initialized
    /// or the negotiated version is not one this implementation models. Consult this to gate
    /// revision-specific wire behavior instead of comparing <see cref="ProtocolVersion"/>.
    /// </summary>
    public ProtocolRevision? Revision => ProtocolRevision.TryGet(ProtocolVersion);

    /// <summary>
    /// The remote peer's implementation info (server info if we're a client, client info if we're a server).
    /// </summary>
    public Implementation? RemoteInfo { get; private set; }

    /// <summary>
    /// Server capabilities (available after initialization).
    /// </summary>
    public ServerCapabilities? ServerCapabilities { get; private set; }

    /// <summary>
    /// Client capabilities (available after initialization).
    /// </summary>
    public ClientCapabilities? ClientCapabilities { get; private set; }

    /// <summary>
    /// Optional instructions from the server for the LLM.
    /// </summary>
    public string? Instructions { get; private set; }

    /// <summary>
    /// Attempt to transition to a new state. Returns true if the transition was valid.
    /// </summary>
    public bool TryTransition(McpSessionState newState)
    {
        var current = (McpSessionState)_state;

        if (!IsValidTransition(current, newState))
            return false;

        return Interlocked.CompareExchange(ref _state, (int)newState, (int)current) == (int)current;
    }

    /// <summary>
    /// Transition to a new state, throwing if the transition is invalid.
    /// </summary>
    public void Transition(McpSessionState newState)
    {
        if (!TryTransition(newState))
            throw new McpSessionException($"Invalid state transition: {State} → {newState}");
    }

    /// <summary>
    /// Mark initialization as complete from the client's perspective.
    /// Stores the server's response and transitions to Ready.
    /// </summary>
    public void CompleteInitializationAsClient(InitializeResult result)
    {
        ProtocolVersion = result.ProtocolVersion;
        ServerCapabilities = result.Capabilities;
        RemoteInfo = result.ServerInfo;
        Instructions = result.Instructions;
        Transition(McpSessionState.Ready);
    }

    /// <summary>
    /// Mark initialization as complete from the server's perspective.
    /// Stores the client's request info and transitions to Ready.
    /// </summary>
    public void CompleteInitializationAsServer(InitializeParams clientParams, string agreedVersion)
    {
        ProtocolVersion = agreedVersion;
        ClientCapabilities = clientParams.Capabilities;
        RemoteInfo = clientParams.ClientInfo;
        Transition(McpSessionState.Ready);
    }

    /// <summary>
    /// Check if a server capability is available.
    /// </summary>
    public bool HasServerCapability(string name) => name switch
    {
        "tools" => ServerCapabilities?.Tools is not null,
        "resources" => ServerCapabilities?.Resources is not null,
        "prompts" => ServerCapabilities?.Prompts is not null,
        "logging" => ServerCapabilities?.Logging is not null,
        "completions" => ServerCapabilities?.Completions is not null,
        _ => ServerCapabilities?.Experimental?.ContainsKey(name) == true
    };

    /// <summary>
    /// Check if a client capability is available.
    /// </summary>
    public bool HasClientCapability(string name) => name switch
    {
        "roots" => ClientCapabilities?.Roots is not null,
        "sampling" => ClientCapabilities?.Sampling is not null,
        "elicitation" => ClientCapabilities?.Elicitation is not null,
        _ => ClientCapabilities?.Experimental?.ContainsKey(name) == true
    };

    /// <summary>
    /// Require a server capability, throwing if not available.
    /// </summary>
    public void RequireServerCapability(string name)
    {
        if (!HasServerCapability(name))
            throw new McpCapabilityNotAvailableException(name);
    }

    /// <summary>
    /// Require a client capability, throwing if not available.
    /// </summary>
    public void RequireClientCapability(string name)
    {
        if (!HasClientCapability(name))
            throw new McpCapabilityNotAvailableException(name);
    }

    /// <summary>
    /// Negotiate a protocol version. Returns the highest mutually supported version,
    /// or null if no common version exists.
    /// </summary>
    public static string? NegotiateVersion(string clientVersion)
    {
        // If the client's requested revision is one we support, honor it.
        if (ProtocolRevision.TryGet(clientVersion) is not null)
            return clientVersion;

        // Otherwise offer our latest and let the client decide whether to proceed.
        return LatestProtocolVersion;
    }

    /// <summary>
    /// Check if a client can accept a server's offered version.
    /// </summary>
    public static bool IsVersionAcceptable(string serverVersion) =>
        ProtocolRevision.TryGet(serverVersion) is not null;

    private static bool IsValidTransition(McpSessionState from, McpSessionState to) => (from, to) switch
    {
        (McpSessionState.Uninitialized, McpSessionState.Initializing) => true,
        (McpSessionState.Initializing, McpSessionState.Ready) => true,
        (McpSessionState.Initializing, McpSessionState.Closed) => true, // Failed init
        (McpSessionState.Ready, McpSessionState.ShuttingDown) => true,
        (McpSessionState.ShuttingDown, McpSessionState.Closed) => true,
        (McpSessionState.Ready, McpSessionState.Closed) => true, // Abrupt close
        _ => false
    };
}

/// <summary>
/// Thrown when an invalid session state transition is attempted.
/// </summary>
public class McpSessionException : Exception
{
    public McpSessionException(string message) : base(message) { }
}

/// <summary>
/// Thrown when a requested capability was not negotiated.
/// </summary>
public class McpCapabilityNotAvailableException : Exception
{
    public string CapabilityName { get; }

    public McpCapabilityNotAvailableException(string capabilityName)
        : base($"Capability '{capabilityName}' is not available. It was not negotiated during initialization.")
    {
        CapabilityName = capabilityName;
    }
}
