using System.Collections.Concurrent;
using System.Text.Json;
using Json.Schema;
namespace Andy.MCP.Protocol;

/// <summary>Checks standard message shapes against the frozen official schema for the negotiated revision.</summary>
internal static class ProtocolShapeValidation
{
    private static readonly IReadOnlyDictionary<string, (string Request, string Result)> Methods = new Dictionary<string, (string, string)>
    {
        ["initialize"] = ("InitializeRequest", "InitializeResult"),
        ["ping"] = ("PingRequest", "EmptyResult"),
        ["tools/list"] = ("ListToolsRequest", "ListToolsResult"),
        ["tools/call"] = ("CallToolRequest", "CallToolResult"),
        ["resources/list"] = ("ListResourcesRequest", "ListResourcesResult"),
        ["resources/read"] = ("ReadResourceRequest", "ReadResourceResult"),
        ["resources/templates/list"] = ("ListResourceTemplatesRequest", "ListResourceTemplatesResult"),
        ["resources/subscribe"] = ("SubscribeRequest", "EmptyResult"),
        ["resources/unsubscribe"] = ("UnsubscribeRequest", "EmptyResult"),
        ["prompts/list"] = ("ListPromptsRequest", "ListPromptsResult"),
        ["prompts/get"] = ("GetPromptRequest", "GetPromptResult"),
        ["completion/complete"] = ("CompleteRequest", "CompleteResult"),
        ["logging/setLevel"] = ("SetLevelRequest", "EmptyResult"),
        ["roots/list"] = ("ListRootsRequest", "ListRootsResult"),
        ["sampling/createMessage"] = ("CreateMessageRequest", "CreateMessageResult"),
        ["elicitation/create"] = ("ElicitRequest", "ElicitResult"),
        ["tasks/get"] = ("GetTaskRequest", "GetTaskResult"),
        ["tasks/list"] = ("ListTasksRequest", "ListTasksResult"),
        ["tasks/result"] = ("GetTaskPayloadRequest", "GetTaskPayloadResult"),
        ["tasks/cancel"] = ("CancelTaskRequest", "CancelTaskResult")
    };
    private static readonly IReadOnlyDictionary<string, string> Notifications = new Dictionary<string, string>
    {
        ["notifications/initialized"] = "InitializedNotification",
        ["notifications/cancelled"] = "CancelledNotification",
        ["notifications/progress"] = "ProgressNotification",
        ["notifications/message"] = "LoggingMessageNotification",
        ["notifications/tools/list_changed"] = "ToolListChangedNotification",
        ["notifications/resources/list_changed"] = "ResourceListChangedNotification",
        ["notifications/prompts/list_changed"] = "PromptListChangedNotification",
        ["notifications/roots/list_changed"] = "RootsListChangedNotification",
        ["notifications/resources/updated"] = "ResourceUpdatedNotification",
        ["notifications/elicitation/complete"] = "ElicitationCompleteNotification",
        ["notifications/tasks/status"] = "TaskStatusNotification"
    };
    private static readonly ConcurrentDictionary<string, Schema> Schemas = new();

    internal static void Request(JsonRpcRequest request, ProtocolRevision revision)
    {
        if (Methods.TryGetValue(request.Method, out var method)) Validate(method.Request, McpJsonDefaults.ToElement(request), revision);
    }
    internal static void Result(JsonRpcRequest request, JsonElement? result, ProtocolRevision revision)
    {
        if (!Methods.TryGetValue(request.Method, out var method)) return;
        var task = request.Method is "tools/call" or "sampling/createMessage" or "elicitation/create" && request.Params is { } parameters && parameters.TryGetProperty("task", out var metadata) && metadata.ValueKind == JsonValueKind.Object;
        Validate(task ? "CreateTaskResult" : method.Result, result, revision);
    }
    internal static void Notification(JsonRpcNotification notification, ProtocolRevision revision)
    {
        if (Notifications.TryGetValue(notification.Method, out var definition)) Validate(definition, McpJsonDefaults.ToElement(notification), revision);
    }
    internal static void Validate(string definition, JsonElement? value, ProtocolRevision revision) =>
        Schemas.GetOrAdd(revision.Version, version => new Schema(version)).Validate(definition, value);

    private sealed class Schema
    {
        private readonly object _gate = new();
        private readonly EvaluationOptions _options = new() { OutputFormat = OutputFormat.List };
        private readonly Dictionary<string, JsonSchema> _definitions = new(StringComparer.Ordinal);
        private readonly string _version;
        public Schema(string version)
        {
            _version = version;
            using var stream = typeof(ProtocolShapeValidation).Assembly.GetManifestResourceStream($"Andy.MCP.Schemas.schema-{version}.json")
                ?? throw new JsonException($"No runtime schema is available for revision {version}.");
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            using var document = JsonDocument.Parse(text);
            var prefix = document.RootElement.TryGetProperty("$defs", out var definitions) ? "$defs" : "definitions";
            if (prefix == "definitions") definitions = document.RootElement.GetProperty(prefix);
            var uri = new Uri($"https://schemas.andy-mcp.invalid/{version}/schema");
            var build = new BuildOptions { Dialect = Dialect.Draft202012, SchemaRegistry = new() };
            build.SchemaRegistry.Register(uri, JsonSchema.FromText(text, build, uri));
            build.SchemaRegistry.Fetch = (uri, _) => throw new InvalidOperationException($"External protocol schema fetch is prohibited: {uri}");
            foreach (var definition in definitions.EnumerateObject())
                _definitions.Add(definition.Name, new JsonSchemaBuilder().Ref($"{uri}#/{prefix}/{definition.Name}").Build(build));
        }
        public void Validate(string definition, JsonElement? value)
        {
            lock (_gate)
            {
                if (!_definitions.TryGetValue(definition, out var schema)) throw new JsonException($"{definition} is unavailable in revision {_version}.");
                var result = schema.Evaluate(value ?? JsonSerializer.SerializeToElement<object?>(null), _options);
                if (!result.IsValid) throw new JsonException($"Invalid {definition} for {_version}: {string.Join("; ", Errors(result).Take(5))}");
            }
        }
        private static IEnumerable<string> Errors(EvaluationResults result)
        {
            if (result.IsValid) yield break;
            if (result.Errors is { } errors)
                foreach (var error in errors) yield return $"{result.InstanceLocation} ({error.Key}): {error.Value}";
            if (result.Details is { } children)
                foreach (var child in children)
                    foreach (var error in Errors(child)) yield return error;
        }
    }
}
