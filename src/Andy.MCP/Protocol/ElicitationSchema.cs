using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.MCP.Protocol;

/// <summary>
/// A form-mode elicitation <c>requestedSchema</c> (MCP 2025-11-25): a restricted JSON Schema
/// object whose top-level properties are primitive schema definitions. This is a typed builder
/// for constructing well-formed requested schemas rather than assembling anonymous objects.
/// </summary>
public sealed record ElicitationSchema
{
    [JsonPropertyName("$schema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Schema { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "object";

    [JsonPropertyName("properties")]
    public required IReadOnlyDictionary<string, PrimitiveSchemaDefinition> Properties { get; init; }

    [JsonPropertyName("required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Required { get; init; }
}

/// <summary>
/// A primitive schema definition for a single elicitation field: a string, number/integer,
/// boolean, or single-select string enum, each optionally carrying a <c>default</c> value
/// (MCP 2025-11-25, SEP-1034/SEP-1330). Represented as a single flat record so it round-trips
/// without polymorphic discrimination; use the factory helpers to construct the common shapes.
/// </summary>
public sealed record PrimitiveSchemaDefinition
{
    /// <summary>The JSON Schema type: "string", "number", "integer", or "boolean".</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("minLength")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinLength { get; init; }

    [JsonPropertyName("maxLength")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxLength { get; init; }

    /// <summary>String format hint: "email", "uri", "date", or "date-time".</summary>
    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; init; }

    [JsonPropertyName("minimum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Minimum { get; init; }

    [JsonPropertyName("maximum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Maximum { get; init; }

    /// <summary>Allowed values for an untitled single-select string enum.</summary>
    [JsonPropertyName("enum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Enum { get; init; }

    /// <summary>Titled single-select enum options (each a value + display title).</summary>
    [JsonPropertyName("oneOf")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<EnumOption>? OneOf { get; init; }

    /// <summary>Minimum number of selected items for a multi-select (array) enum.</summary>
    [JsonPropertyName("minItems")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinItems { get; init; }

    /// <summary>Maximum number of selected items for a multi-select (array) enum.</summary>
    [JsonPropertyName("maxItems")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxItems { get; init; }

    /// <summary>Item schema for a multi-select (array) enum.</summary>
    [JsonPropertyName("items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Items { get; init; }

    /// <summary>
    /// The default value matching this field's type: a string, number, or boolean for scalar
    /// fields, or an array of strings for a multi-select enum.
    /// </summary>
    [JsonPropertyName("default")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Default { get; init; }

    public static PrimitiveSchemaDefinition StringField(
        string? title = null, string? description = null,
        int? minLength = null, int? maxLength = null,
        string? format = null, string? @default = null) =>
        new()
        {
            Type = "string",
            Title = title,
            Description = description,
            MinLength = minLength,
            MaxLength = maxLength,
            Format = format,
            Default = @default is null ? null : JsonSerializer.SerializeToElement(@default)
        };

    public static PrimitiveSchemaDefinition NumberField(
        bool integer = false, string? title = null, string? description = null,
        double? minimum = null, double? maximum = null, double? @default = null) =>
        new()
        {
            Type = integer ? "integer" : "number",
            Title = title,
            Description = description,
            Minimum = minimum,
            Maximum = maximum,
            Default = @default is null ? null : JsonSerializer.SerializeToElement(@default.Value)
        };

    public static PrimitiveSchemaDefinition BooleanField(
        string? title = null, string? description = null, bool? @default = null) =>
        new()
        {
            Type = "boolean",
            Title = title,
            Description = description,
            Default = @default is null ? null : JsonSerializer.SerializeToElement(@default.Value)
        };

    public static PrimitiveSchemaDefinition EnumField(
        IReadOnlyList<string> values, string? title = null,
        string? description = null, string? @default = null) =>
        new()
        {
            Type = "string",
            Title = title,
            Description = description,
            Enum = values,
            Default = @default is null ? null : JsonSerializer.SerializeToElement(@default)
        };

    /// <summary>A titled single-select string enum, where each option carries a display title.</summary>
    public static PrimitiveSchemaDefinition TitledEnumField(
        IReadOnlyList<EnumOption> options, string? title = null,
        string? description = null, string? @default = null) =>
        new()
        {
            Type = "string",
            Title = title,
            Description = description,
            OneOf = options,
            Default = @default is null ? null : JsonSerializer.SerializeToElement(@default)
        };

    /// <summary>An untitled multi-select enum (an array of strings drawn from a fixed set).</summary>
    public static PrimitiveSchemaDefinition MultiSelectEnumField(
        IReadOnlyList<string> values, string? title = null, string? description = null,
        int? minItems = null, int? maxItems = null, IReadOnlyList<string>? @default = null) =>
        new()
        {
            Type = "array",
            Title = title,
            Description = description,
            MinItems = minItems,
            MaxItems = maxItems,
            Items = JsonSerializer.SerializeToElement(new { type = "string", @enum = values }),
            Default = @default is null ? null : JsonSerializer.SerializeToElement(@default)
        };
}

/// <summary>
/// A single option in a titled enum: the stored value and its human-readable display title
/// (MCP 2025-11-25, SEP-1330).
/// </summary>
public sealed record EnumOption
{
    [JsonPropertyName("const")]
    public required string Const { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    public EnumOption() { }

    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public EnumOption(string @const, string title)
    {
        Const = @const;
        Title = title;
    }
}
