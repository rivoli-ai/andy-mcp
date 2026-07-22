using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.MCP.Protocol;

/// <summary>
/// Parameters for the notifications/cancelled notification.
/// Sent to cancel an in-progress request. The initialize request MUST NOT be cancelled.
/// </summary>
public sealed record CancelledParams
{
    /// <summary>
    /// The ID of the request to cancel.
    /// </summary>
    [JsonPropertyName("requestId")]
    public required RequestId RequestId { get; init; }

    /// <summary>
    /// Optional human-readable reason for the cancellation.
    /// </summary>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
}

/// <summary>
/// Parameters for the notifications/progress notification.
/// Sent to report progress on a long-running operation that included a progressToken in _meta.
/// </summary>
public sealed record ProgressParams
{
    /// <summary>
    /// The progress token from the original request's _meta.progressToken.
    /// </summary>
    [JsonPropertyName("progressToken")]
    public required RequestId ProgressToken { get; init; }

    /// <summary>
    /// Current progress value. MUST increase monotonically with each notification.
    /// May be floating point.
    /// </summary>
    [JsonPropertyName("progress")]
    public required double Progress { get; init; }

    /// <summary>
    /// Total expected value, if known. Enables determinate progress bars.
    /// May be floating point.
    /// </summary>
    [JsonPropertyName("total")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Total { get; init; }

    /// <summary>
    /// Human-readable status message.
    /// </summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
}

/// <summary>
/// Well-known MCP method names for utilities.
/// </summary>
public static class McpMethods
{
    // Utilities
    public const string Ping = "ping";
    public const string NotificationsCancelled = "notifications/cancelled";
    public const string NotificationsProgress = "notifications/progress";

    // Lifecycle
    public const string Initialize = "initialize";
    public const string NotificationsInitialized = "notifications/initialized";

    // Tools
    public const string ToolsList = "tools/list";
    public const string ToolsCall = "tools/call";
    public const string NotificationsToolsListChanged = "notifications/tools/list_changed";

    // Resources
    public const string ResourcesList = "resources/list";
    public const string ResourcesRead = "resources/read";
    public const string ResourcesTemplatesList = "resources/templates/list";
    public const string ResourcesSubscribe = "resources/subscribe";
    public const string ResourcesUnsubscribe = "resources/unsubscribe";
    public const string NotificationsResourcesListChanged = "notifications/resources/list_changed";
    public const string NotificationsResourcesUpdated = "notifications/resources/updated";

    // Prompts
    public const string PromptsList = "prompts/list";
    public const string PromptsGet = "prompts/get";
    public const string NotificationsPromptsListChanged = "notifications/prompts/list_changed";

    // Completions
    public const string CompletionComplete = "completion/complete";

    // Logging
    public const string LoggingSetLevel = "logging/setLevel";
    public const string NotificationsMessage = "notifications/message";

    // Client features
    public const string RootsList = "roots/list";
    public const string NotificationsRootsListChanged = "notifications/roots/list_changed";
    public const string SamplingCreateMessage = "sampling/createMessage";
    public const string ElicitationCreate = "elicitation/create";

    // Experimental tasks (MCP 2025-11-25)
    public const string TasksGet = "tasks/get";
    public const string TasksList = "tasks/list";
    public const string TasksResult = "tasks/result";
    public const string TasksCancel = "tasks/cancel";
}

/// <summary>
/// Helper methods for building common JSON-RPC messages for MCP utilities.
/// </summary>
public static class McpMessages
{
    /// <summary>
    /// Create a ping request.
    /// </summary>
    public static JsonRpcRequest Ping(RequestId id) =>
        new() { Id = id, Method = McpMethods.Ping };

    /// <summary>
    /// Create an empty ping response.
    /// </summary>
    public static JsonRpcResponse PingResponse(RequestId id) =>
        JsonRpcResponse.Success(id);

    /// <summary>
    /// Create a cancellation notification.
    /// </summary>
    public static JsonRpcNotification Cancelled(RequestId requestId, string? reason = null) =>
        new()
        {
            Method = McpMethods.NotificationsCancelled,
            Params = McpJsonDefaults.ToElement(new CancelledParams
            {
                RequestId = requestId,
                Reason = reason
            })
        };

    /// <summary>
    /// Create a progress notification.
    /// </summary>
    public static JsonRpcNotification Progress(RequestId progressToken, double progress, double? total = null, string? message = null) =>
        new()
        {
            Method = McpMethods.NotificationsProgress,
            Params = McpJsonDefaults.ToElement(new ProgressParams
            {
                ProgressToken = progressToken,
                Progress = progress,
                Total = total,
                Message = message
            })
        };
}
