using System.Text.Json;

namespace Andy.MCP.Server;

/// <summary>
/// Lightweight JSON Schema validator for MCP tool input validation.
/// Validates required properties and basic type checks against a JSON Schema.
/// </summary>
public static class JsonSchemaValidator
{
    /// <summary>
    /// Validate a JSON arguments object against a JSON Schema.
    /// Returns a list of validation errors (empty if valid).
    /// </summary>
    public static IReadOnlyList<string> Validate(JsonElement? arguments, JsonElement schema)
    {
        var errors = new List<string>();

        // Schema must be an object type
        if (!schema.TryGetProperty("type", out var schemaType) || schemaType.GetString() != "object")
            return errors; // Can't validate non-object schemas

        // Check required properties
        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var req in required.EnumerateArray())
            {
                var propName = req.GetString()!;
                if (arguments is null ||
                    !arguments.Value.TryGetProperty(propName, out var propVal) ||
                    propVal.ValueKind == JsonValueKind.Null)
                {
                    errors.Add($"Missing required parameter: '{propName}'");
                }
            }
        }

        // Check property types
        if (arguments is not null && schema.TryGetProperty("properties", out var properties))
        {
            foreach (var prop in arguments.Value.EnumerateObject())
            {
                if (properties.TryGetProperty(prop.Name, out var propSchema) &&
                    propSchema.TryGetProperty("type", out var expectedType))
                {
                    var typeStr = expectedType.GetString();
                    if (!IsTypeMatch(prop.Value, typeStr))
                    {
                        errors.Add($"Parameter '{prop.Name}' has wrong type: expected '{typeStr}', got '{GetJsonType(prop.Value)}'");
                    }
                }
            }
        }

        return errors;
    }

    private static bool IsTypeMatch(JsonElement value, string? expectedType) => expectedType switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && IsInteger(value),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true // Unknown type — allow
    };

    private static bool IsInteger(JsonElement value)
    {
        if (value.TryGetInt64(out _)) return true;
        var d = value.GetDouble();
        return d == Math.Floor(d);
    }

    private static string GetJsonType(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        JsonValueKind.Null => "null",
        _ => "undefined"
    };
}
