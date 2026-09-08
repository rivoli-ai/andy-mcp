using System.Text.Json;
using System.Text.Json.Nodes;
using Andy.MCP.Protocol;

namespace Andy.MCP.Server;

internal static class TaskResults
{
    internal static async Task<JsonRpcResponse> WaitAsync(ITaskStore store, string? owner,
        JsonRpcRequest request, CancellationToken cancellationToken)
    {
        var taskId = request.GetParams<TaskIdParams>()!.TaskId;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var task = store.Get(taskId, owner);
            if (task is null)
                return JsonRpcResponse.Failure(request.Id, JsonRpcError.InvalidParams($"Unknown task: '{taskId}'"));
            if (task.Status is McpTaskStatus.Working or McpTaskStatus.InputRequired)
            {
                // Polling also observes changes made by another process in a durable store.
                // Each waiter owns its cancellation; abandoning retrieval never cancels execution.
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
                continue;
            }
            var error = store.GetError(taskId, owner);
            if (error is not null) return JsonRpcResponse.Failure(request.Id, TaskExecutionContext.RelateError(error, taskId));
            var payload = store.GetResult(taskId, owner);
            if (payload is { } result)
            {
                var body = JsonNode.Parse(result.GetRawText())!.AsObject();
                var meta = body["_meta"] as JsonObject ?? new JsonObject();
                meta["io.modelcontextprotocol/related-task"] = new JsonObject { ["taskId"] = taskId };
                body["_meta"] = meta;
                return JsonRpcResponse.Success(request.Id, JsonSerializer.SerializeToElement(body));
            }
            // Expiry may race the terminal-state read.
            if (store.Get(taskId, owner) is null)
                return JsonRpcResponse.Failure(request.Id, JsonRpcError.InvalidParams($"Unknown task: '{taskId}'"));
            return JsonRpcResponse.Failure(request.Id, task.Status == McpTaskStatus.Cancelled
                ? JsonRpcError.InvalidRequest($"Task '{taskId}' was cancelled.")
                : JsonRpcError.InternalError(task.StatusMessage ?? $"Task '{taskId}' has no retained result."));
        }
    }
}
