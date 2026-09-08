using System.Text.Json;
using Andy.MCP.Client;

namespace Andy.MCP.Protocol;

internal static class PeerRequestValidation
{
    public static void Sampling(CreateMessageRequest request, ClientCapabilities? capabilities, ProtocolRevision revision)
    {
        if (capabilities?.Sampling is not { } sampling) throw new McpCapabilityNotAvailableException("sampling");
        if (request.Tools is not null || request.ToolChoice is not null || request.Messages.Any(m => m.Content.Any(c => c is ToolUseContent or ToolResultContent)))
            if (!revision.AtLeast(ProtocolRevision.V2025_11_25) || sampling.Tools is null)
                throw new McpCapabilityNotAvailableException("sampling.tools");
        if (request.IncludeContext is not (null or "none" or "thisServer" or "allServers"))
            throw new ArgumentException("Invalid sampling includeContext.");
        if (request.IncludeContext is "thisServer" or "allServers" && revision.AtLeast(ProtocolRevision.V2025_11_25) && sampling.Context is null)
            throw new McpCapabilityNotAvailableException("sampling.context");
    }

    public static void Elicitation(ElicitRequest request, ClientCapabilities? capabilities, ProtocolRevision revision)
    {
        if (!revision.AtLeast(ProtocolRevision.V2025_06_18) || capabilities?.Elicitation is not { } elicitation)
            throw new McpCapabilityNotAvailableException("elicitation");
        if (request.Mode is not (null or "form" or "url")) throw new ArgumentException("Invalid elicitation mode.");
        if (request.IsUrlMode)
        {
            if (!revision.AtLeast(ProtocolRevision.V2025_11_25) || elicitation.Url is null)
                throw new McpCapabilityNotAvailableException("elicitation.url");
            if (string.IsNullOrEmpty(request.ElicitationId) || !Uri.TryCreate(request.Url, UriKind.Absolute, out _) || request.RequestedSchema is not null)
                throw new ArgumentException("URL elicitation requires an elicitationId and absolute URL, without a form schema.");
        }
        else
        {
            // An empty capability object has always meant form support.
            if (elicitation.Form is null && elicitation.Url is not null)
                throw new McpCapabilityNotAvailableException("elicitation.form");
            if (request.Url is not null || request.ElicitationId is not null || request.RequestedSchema is not { ValueKind: JsonValueKind.Object })
                throw new ArgumentException("Form elicitation requires requestedSchema and cannot contain URL fields.");
        }
    }
}
