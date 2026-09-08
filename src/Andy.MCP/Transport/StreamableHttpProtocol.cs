namespace Andy.MCP.Transport;

/// <summary>Revisions supported by Streamable HTTP; legacy 2024 HTTP+SSE is a different transport.</summary>
public static class StreamableHttpProtocol
{
    public static IReadOnlyList<string> SupportedVersions { get; } = Array.AsReadOnly(new[] { "2025-11-25", "2025-06-18", "2025-03-26" });
}
