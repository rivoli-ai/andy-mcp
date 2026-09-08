using System.Text.Json;
using System.Text.Json.Nodes;
using Andy.MCP.Protocol;

namespace Andy.MCP.Server;

/// <summary>Associates background execution with its nested peer requests.</summary>
internal sealed class TaskExecutionContext : IDisposable
{
    private static readonly AsyncLocal<TaskExecutionContext?> Ambient = new();
    private readonly TaskExecutionContext? _previous;
    private readonly ITaskStore _store;
    private readonly object _gate = new();
    private int _pendingInput;
    internal string TaskId { get; }
    internal static TaskExecutionContext? Current => Ambient.Value;

    internal TaskExecutionContext(ITaskStore store, string taskId)
    {
        _store = store;
        TaskId = taskId;
        _previous = Ambient.Value;
        Ambient.Value = this;
    }

    internal IDisposable BeginInput()
    {
        lock (_gate)
            if (++_pendingInput == 1) _store.UpdateStatus(TaskId, McpTaskStatus.InputRequired);
        return new InputLease(this);
    }

    private void EndInput()
    {
        lock (_gate)
            if (--_pendingInput == 0) _store.UpdateStatus(TaskId, McpTaskStatus.Working);
    }

    public void Dispose() => Ambient.Value = _previous;

    private sealed class InputLease(TaskExecutionContext context) : IDisposable
    {
        private TaskExecutionContext? _context = context;
        public void Dispose() => Interlocked.Exchange(ref _context, null)?.EndInput();
    }

    internal static JsonElement WithRelatedTask(JsonElement? payload, string taskId)
    {
        var body = payload is { } element ? JsonNode.Parse(element.GetRawText())!.AsObject() : new JsonObject();
        var meta = body["_meta"] as JsonObject ?? new JsonObject();
        meta["io.modelcontextprotocol/related-task"] = new JsonObject { ["taskId"] = taskId };
        body["_meta"] = meta;
        return JsonSerializer.SerializeToElement(body);
    }

    internal static JsonElement? AttachCurrent(JsonElement? payload) =>
        Current is { } context ? WithRelatedTask(payload, context.TaskId) : payload;

    internal static JsonRpcResponse RelateResponse(JsonRpcRequest request, JsonRpcResponse response)
    {
        if (request.Method is McpMethods.TasksGet or McpMethods.TasksList or McpMethods.TasksCancel or McpMethods.TasksResult)
            return response;
        if (request.Params is not { ValueKind: JsonValueKind.Object } p ||
            !p.TryGetProperty("_meta", out var meta) || meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("io.modelcontextprotocol/related-task", out var related) || related.ValueKind != JsonValueKind.Object ||
            !related.TryGetProperty("taskId", out var id) || id.ValueKind != JsonValueKind.String)
            return response;
        if (!response.IsError)
            return response with { Result = WithRelatedTask(response.Result, id.GetString()!) };
        var error = response.Error!;
        var extensions = error.ExtensionData is null ? new Dictionary<string, JsonElement>() : new(error.ExtensionData);
        extensions["_meta"] = WithRelatedTask(null, id.GetString()!).GetProperty("_meta");
        return response with { Error = error with { ExtensionData = extensions } };
    }
}
