using System.Text.Json;
using System.Text.Json.Nodes;

namespace Andy.MCP.Protocol;

internal static class TaskProtocol
{
    internal static bool Supports(ServerTasksCapability? caps, string method) => caps is not null && (method switch
    {
        McpMethods.ToolsCall => caps.Requests?.Tools?.Call is not null,
        McpMethods.TasksList => caps.List is not null,
        McpMethods.TasksCancel => caps.Cancel is not null,
        McpMethods.TasksGet or McpMethods.TasksResult => true,
        _ => false
    });

    internal static bool Supports(ClientTasksCapability? caps, string method) => caps is not null && (method switch
    {
        McpMethods.SamplingCreateMessage => caps.Requests?.Sampling?.CreateMessage is not null,
        McpMethods.ElicitationCreate => caps.Requests?.Elicitation?.Create is not null,
        McpMethods.TasksList => caps.List is not null,
        McpMethods.TasksCancel => caps.Cancel is not null,
        McpMethods.TasksGet or McpMethods.TasksResult => true,
        _ => false
    });

    internal static void Require(bool supported, ProtocolRevision? revision, string method)
    {
        if (!supported || revision is null || !revision.AtLeast(ProtocolRevision.V2025_11_25))
            throw new McpCapabilityNotAvailableException($"tasks ({method})");
    }

    internal static JsonRpcRequest WithoutAugmentation(JsonRpcRequest request)
    {
        if (request.Params is not { ValueKind: JsonValueKind.Object } p || !p.TryGetProperty("task", out _)) return request;
        var node = JsonNode.Parse(p.GetRawText())!.AsObject();
        node.Remove("task");
        return request with { Params = JsonSerializer.SerializeToElement(node) };
    }
}
