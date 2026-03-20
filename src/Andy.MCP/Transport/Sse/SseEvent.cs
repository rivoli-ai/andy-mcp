namespace Andy.MCP.Transport.Sse;

/// <summary>
/// A parsed Server-Sent Event.
/// </summary>
public sealed record SseEvent
{
    /// <summary>
    /// Event type (from "event:" field). Defaults to "message" if not specified.
    /// </summary>
    public string EventType { get; init; } = "message";

    /// <summary>
    /// Event data (from "data:" field(s)). Multiple data lines are joined with newlines.
    /// </summary>
    public required string Data { get; init; }

    /// <summary>
    /// Event ID (from "id:" field), used for resumability via Last-Event-ID.
    /// </summary>
    public string? Id { get; init; }

    /// <summary>
    /// Reconnection time hint in milliseconds (from "retry:" field).
    /// </summary>
    public int? Retry { get; init; }
}
