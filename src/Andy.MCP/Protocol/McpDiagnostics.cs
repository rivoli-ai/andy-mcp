using System.Diagnostics;
using System.Reflection;

namespace Andy.MCP.Protocol;

/// <summary>
/// Shared diagnostics for Andy.MCP OpenTelemetry integration.
/// ActivitySource emits spans only when a listener is registered (zero-cost otherwise).
/// Consumers enable tracing with: .AddSource("Andy.MCP")
/// </summary>
public static class McpDiagnostics
{
    public static readonly string SourceName = "Andy.MCP";

    private static readonly string SourceVersion =
        typeof(McpDiagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.1.0";

    public static readonly ActivitySource Source = new(SourceName, SourceVersion);

    /// <summary>
    /// Start a client-side span for an outgoing MCP request.
    /// Returns null if no listener is registered (zero overhead).
    /// </summary>
    public static Activity? StartClientRequest(string method, RequestId requestId, string? serverName = null, string? protocolVersion = null)
    {
        var activity = Source.StartActivity($"mcp.{method}", ActivityKind.Client);
        if (activity is null) return null;

        activity.SetTag("mcp.method", method);
        activity.SetTag("mcp.request_id", requestId.ToString());
        if (serverName is not null) activity.SetTag("mcp.server.name", serverName);
        if (protocolVersion is not null) activity.SetTag("mcp.protocol_version", protocolVersion);

        return activity;
    }

    /// <summary>
    /// Start a server-side span for an incoming MCP request.
    /// </summary>
    public static Activity? StartServerRequest(string method, RequestId requestId)
    {
        var activity = Source.StartActivity($"mcp.{method}", ActivityKind.Server);
        if (activity is null) return null;

        activity.SetTag("mcp.method", method);
        activity.SetTag("mcp.request_id", requestId.ToString());

        return activity;
    }

    /// <summary>
    /// Mark an activity as successful.
    /// </summary>
    public static void SetSuccess(Activity? activity)
    {
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    /// <summary>
    /// Mark an activity as failed with an error.
    /// </summary>
    public static void SetError(Activity? activity, Exception? exception = null, int? errorCode = null)
    {
        if (activity is null) return;

        activity.SetStatus(ActivityStatusCode.Error, exception?.Message);
        if (exception is not null)
            activity.AddEvent(new ActivityEvent("exception",
                tags: new ActivityTagsCollection
                {
                    { "exception.type", exception.GetType().FullName },
                    { "exception.message", exception.Message }
                }));
        if (errorCode is not null)
            activity.SetTag("mcp.error_code", errorCode.Value);
    }
}
