namespace Andy.MCP.Server;

/// <summary>
/// Marks a method as an MCP tool. The framework auto-generates the JSON Schema
/// from method parameters and wires up the handler.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class McpToolAttribute : Attribute
{
    /// <summary>
    /// Tool name. Defaults to PascalCase→snake_case conversion of the method name.
    /// </summary>
    public string? Name { get; set; }

    public string? Description { get; set; }
    public string? Title { get; set; }
    public bool ReadOnly { get; set; }
    public bool Destructive { get; set; } = true;
    public bool Idempotent { get; set; }
}

/// <summary>
/// Provides metadata for a tool or prompt parameter.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class McpParamAttribute : Attribute
{
    public string? Description { get; set; }
    public bool Required { get; set; }
}

/// <summary>
/// Marks a method as an MCP resource handler.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class McpResourceAttribute : Attribute
{
    public required string Uri { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? MimeType { get; set; }
}

/// <summary>
/// Marks a method as an MCP prompt handler.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class McpPromptAttribute : Attribute
{
    /// <summary>
    /// Prompt name. Defaults to PascalCase→snake_case conversion of the method name.
    /// </summary>
    public string? Name { get; set; }

    public string? Description { get; set; }
}
