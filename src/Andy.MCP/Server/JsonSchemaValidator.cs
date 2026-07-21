using System.Text.Json;
using System.Text.RegularExpressions;

namespace Andy.MCP.Server;

/// <summary>
/// JSON Schema validator for MCP tool input/output validation. Validates recursively against a
/// practical subset of JSON Schema 2020-12: types, required, nested objects/properties,
/// additionalProperties, arrays/items with bounds and uniqueness, enum/const, string length and
/// pattern, numeric bounds and multipleOf, and the allOf/anyOf/oneOf/not combinators.
/// </summary>
public static class JsonSchemaValidator
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Validate a JSON value against a JSON Schema. Returns a list of validation errors
    /// (empty if valid).
    /// </summary>
    public static IReadOnlyList<string> Validate(JsonElement? arguments, JsonElement schema)
    {
        var errors = new List<string>();
        ValidateValue(arguments, schema, "", errors);
        return errors;
    }

    private static readonly HashSet<string> ValidTypes =
        new(StringComparer.Ordinal) { "string", "number", "integer", "boolean", "array", "object", "null" };

    /// <summary>
    /// Validate that a value is itself a well-formed JSON Schema (used to check tool input/output
    /// schemas at registration). Returns structural errors, empty if the schema is well-formed.
    /// </summary>
    public static IReadOnlyList<string> ValidateSchema(JsonElement schema)
    {
        var errors = new List<string>();
        ValidateSchemaNode(schema, "", errors);
        return errors;
    }

    private static void ValidateSchemaNode(JsonElement schema, string path, List<string> errors)
    {
        // A boolean is a valid schema.
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return;
        if (schema.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{SchemaLabel(path)} must be an object or boolean schema.");
            return;
        }

        if (schema.TryGetProperty("type", out var type))
        {
            var typesValid = type.ValueKind switch
            {
                JsonValueKind.String => ValidTypes.Contains(type.GetString()!),
                JsonValueKind.Array => type.EnumerateArray().All(t => t.ValueKind == JsonValueKind.String && ValidTypes.Contains(t.GetString()!)),
                _ => false
            };
            if (!typesValid)
                errors.Add($"{SchemaLabel(path)} has an invalid 'type'.");
        }

        if (schema.TryGetProperty("properties", out var properties))
        {
            if (properties.ValueKind != JsonValueKind.Object)
                errors.Add($"{SchemaLabel(path)} 'properties' must be an object.");
            else
                foreach (var prop in properties.EnumerateObject())
                    ValidateSchemaNode(prop.Value, path.Length == 0 ? prop.Name : $"{path}.{prop.Name}", errors);
        }

        if (schema.TryGetProperty("required", out var required))
        {
            if (required.ValueKind != JsonValueKind.Array || required.EnumerateArray().Any(r => r.ValueKind != JsonValueKind.String))
                errors.Add($"{SchemaLabel(path)} 'required' must be an array of strings.");
        }

        if (schema.TryGetProperty("items", out var items))
            ValidateSchemaNode(items, path.Length == 0 ? "items" : $"{path}.items", errors);

        foreach (var combinator in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (schema.TryGetProperty(combinator, out var sub))
            {
                if (sub.ValueKind != JsonValueKind.Array)
                    errors.Add($"{SchemaLabel(path)} '{combinator}' must be an array of schemas.");
                else
                    foreach (var s in sub.EnumerateArray())
                        ValidateSchemaNode(s, $"{path}.{combinator}", errors);
            }
        }

        if (schema.TryGetProperty("not", out var not))
            ValidateSchemaNode(not, path.Length == 0 ? "not" : $"{path}.not", errors);
    }

    private static string SchemaLabel(string path) => path.Length == 0 ? "Schema" : $"Schema at '{path}'";

    private static void ValidateValue(JsonElement? maybeValue, JsonElement schema, string path, List<string> errors)
    {
        // Boolean schemas: true accepts everything, false rejects everything.
        if (schema.ValueKind == JsonValueKind.True)
            return;
        if (schema.ValueKind == JsonValueKind.False)
        {
            errors.Add($"{Label(path)} is not allowed.");
            return;
        }
        if (schema.ValueKind != JsonValueKind.Object)
            return;

        // 'required' is an object-level keyword and applies even to an absent/null value.
        ValidateRequired(maybeValue, schema, errors);

        if (maybeValue is not { } value || value.ValueKind == JsonValueKind.Null)
            return; // Nothing further to validate for an absent or null value.

        // type
        if (schema.TryGetProperty("type", out var typeEl) && !TypeMatches(value, typeEl))
        {
            errors.Add($"{Label(path)} has wrong type: expected '{TypeName(typeEl)}', got '{GetJsonType(value)}'");
            return; // remaining keyword checks assume the correct type
        }

        // enum / const
        if (schema.TryGetProperty("enum", out var enumEl) && enumEl.ValueKind == JsonValueKind.Array &&
            !enumEl.EnumerateArray().Any(e => JsonEquals(e, value)))
        {
            errors.Add($"{Label(path)} is not one of the allowed values.");
        }
        if (schema.TryGetProperty("const", out var constEl) && !JsonEquals(constEl, value))
        {
            errors.Add($"{Label(path)} must equal the required constant.");
        }

        ValidateCombinators(value, schema, path, errors);

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                ValidateObjectKeywords(value, schema, path, errors);
                break;
            case JsonValueKind.Array:
                ValidateArrayKeywords(value, schema, path, errors);
                break;
            case JsonValueKind.String:
                ValidateStringKeywords(value, schema, path, errors);
                break;
            case JsonValueKind.Number:
                ValidateNumberKeywords(value, schema, path, errors);
                break;
        }
    }

    private static void ValidateRequired(JsonElement? maybeValue, JsonElement schema, List<string> errors)
    {
        if (!schema.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array)
            return;

        foreach (var req in required.EnumerateArray())
        {
            var name = req.GetString();
            if (name is null)
                continue;

            var present = maybeValue is { ValueKind: JsonValueKind.Object } obj &&
                          obj.TryGetProperty(name, out var pv) && pv.ValueKind != JsonValueKind.Null;
            if (!present)
                errors.Add($"Missing required parameter: '{name}'");
        }
    }

    private static void ValidateObjectKeywords(JsonElement value, JsonElement schema, string path, List<string> errors)
    {
        var hasProps = schema.TryGetProperty("properties", out var props);
        var hasAdditional = schema.TryGetProperty("additionalProperties", out var additional);

        foreach (var member in value.EnumerateObject())
        {
            var childPath = path.Length == 0 ? member.Name : $"{path}.{member.Name}";
            if (hasProps && props.TryGetProperty(member.Name, out var childSchema))
            {
                ValidateValue(member.Value, childSchema, childPath, errors);
            }
            else if (hasAdditional)
            {
                if (additional.ValueKind == JsonValueKind.False)
                    errors.Add($"Additional property '{member.Name}' is not allowed.");
                else if (additional.ValueKind == JsonValueKind.Object)
                    ValidateValue(member.Value, additional, childPath, errors);
            }
        }
    }

    private static void ValidateArrayKeywords(JsonElement value, JsonElement schema, string path, List<string> errors)
    {
        var length = value.GetArrayLength();

        if (schema.TryGetProperty("minItems", out var min) && min.TryGetInt32(out var minItems) && length < minItems)
            errors.Add($"{Label(path)} has too few items: minimum {minItems}.");
        if (schema.TryGetProperty("maxItems", out var max) && max.TryGetInt32(out var maxItems) && length > maxItems)
            errors.Add($"{Label(path)} has too many items: maximum {maxItems}.");

        if (schema.TryGetProperty("uniqueItems", out var unique) && unique.ValueKind == JsonValueKind.True)
        {
            var seen = new List<JsonElement>();
            foreach (var item in value.EnumerateArray())
            {
                if (seen.Any(s => JsonEquals(s, item)))
                {
                    errors.Add($"{Label(path)} must have unique items.");
                    break;
                }
                seen.Add(item);
            }
        }

        if (schema.TryGetProperty("items", out var items) &&
            (items.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                ValidateValue(item, items, $"{(path.Length == 0 ? "$" : path)}[{index++}]", errors);
        }
    }

    private static void ValidateStringKeywords(JsonElement value, JsonElement schema, string path, List<string> errors)
    {
        var str = value.GetString() ?? "";

        if (schema.TryGetProperty("minLength", out var min) && min.TryGetInt32(out var minLen) && str.Length < minLen)
            errors.Add($"{Label(path)} is too short: minimum length {minLen}.");
        if (schema.TryGetProperty("maxLength", out var max) && max.TryGetInt32(out var maxLen) && str.Length > maxLen)
            errors.Add($"{Label(path)} is too long: maximum length {maxLen}.");

        if (schema.TryGetProperty("pattern", out var pattern) && pattern.GetString() is { } regex)
        {
            try
            {
                if (!Regex.IsMatch(str, regex, RegexOptions.None, PatternTimeout))
                    errors.Add($"{Label(path)} does not match the required pattern.");
            }
            catch (RegexMatchTimeoutException)
            {
                errors.Add($"{Label(path)} pattern evaluation timed out.");
            }
        }
    }

    private static void ValidateNumberKeywords(JsonElement value, JsonElement schema, string path, List<string> errors)
    {
        var number = value.GetDouble();

        if (schema.TryGetProperty("minimum", out var min) && min.TryGetDouble(out var minimum) && number < minimum)
            errors.Add($"{Label(path)} is below the minimum {minimum}.");
        if (schema.TryGetProperty("maximum", out var max) && max.TryGetDouble(out var maximum) && number > maximum)
            errors.Add($"{Label(path)} is above the maximum {maximum}.");
        if (schema.TryGetProperty("exclusiveMinimum", out var exMin) && exMin.TryGetDouble(out var exclusiveMin) && number <= exclusiveMin)
            errors.Add($"{Label(path)} must be greater than {exclusiveMin}.");
        if (schema.TryGetProperty("exclusiveMaximum", out var exMax) && exMax.TryGetDouble(out var exclusiveMax) && number >= exclusiveMax)
            errors.Add($"{Label(path)} must be less than {exclusiveMax}.");
        if (schema.TryGetProperty("multipleOf", out var mult) && mult.TryGetDouble(out var multipleOf) && multipleOf != 0)
        {
            var ratio = number / multipleOf;
            if (Math.Abs(ratio - Math.Round(ratio)) > 1e-9)
                errors.Add($"{Label(path)} is not a multiple of {multipleOf}.");
        }
    }

    private static void ValidateCombinators(JsonElement value, JsonElement schema, string path, List<string> errors)
    {
        if (schema.TryGetProperty("allOf", out var allOf) && allOf.ValueKind == JsonValueKind.Array)
            foreach (var sub in allOf.EnumerateArray())
                ValidateValue(value, sub, path, errors);

        if (schema.TryGetProperty("anyOf", out var anyOf) && anyOf.ValueKind == JsonValueKind.Array &&
            !anyOf.EnumerateArray().Any(sub => CountErrors(value, sub) == 0))
        {
            errors.Add($"{Label(path)} does not match any of the allowed schemas (anyOf).");
        }

        if (schema.TryGetProperty("oneOf", out var oneOf) && oneOf.ValueKind == JsonValueKind.Array)
        {
            var matches = oneOf.EnumerateArray().Count(sub => CountErrors(value, sub) == 0);
            if (matches != 1)
                errors.Add($"{Label(path)} must match exactly one schema (oneOf), but matched {matches}.");
        }

        if (schema.TryGetProperty("not", out var not) && CountErrors(value, not) == 0)
            errors.Add($"{Label(path)} must not match the 'not' schema.");
    }

    private static int CountErrors(JsonElement value, JsonElement schema)
    {
        var temp = new List<string>();
        ValidateValue(value, schema, "", temp);
        return temp.Count;
    }

    private static string Label(string path) => path.Length == 0 ? "Value" : $"Parameter '{path}'";

    private static bool TypeMatches(JsonElement value, JsonElement typeEl) => typeEl.ValueKind switch
    {
        JsonValueKind.String => IsTypeMatch(value, typeEl.GetString()),
        JsonValueKind.Array => typeEl.EnumerateArray().Any(t => IsTypeMatch(value, t.GetString())),
        _ => true
    };

    private static string TypeName(JsonElement typeEl) => typeEl.ValueKind == JsonValueKind.Array
        ? string.Join("|", typeEl.EnumerateArray().Select(t => t.GetString()))
        : typeEl.GetString() ?? "";

    private static bool JsonEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
            return false;
        return a.ValueKind switch
        {
            JsonValueKind.String => a.GetString() == b.GetString(),
            JsonValueKind.Number => a.GetDouble() == b.GetDouble(),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => a.GetRawText() == b.GetRawText()
        };
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
