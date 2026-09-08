using System.Globalization;
using System.Text.Json;
using Json.Schema;

namespace Andy.MCP.Server;

/// <summary>JSON Schema 2020-12 validation for MCP tool inputs and outputs.</summary>
public static class JsonSchemaValidator
{
    private static EvaluationOptions Options()
    {
        var options = new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            Culture = CultureInfo.InvariantCulture
        };
        return options;
    }

    /// <summary>Validate a JSON value. An omitted arguments object is treated as empty.</summary>
    public static IReadOnlyList<string> Validate(JsonElement? arguments, JsonElement schema)
    {
        try
        {
            var build = new BuildOptions { Dialect = Dialect.Draft202012, SchemaRegistry = new() };
            // Never fetch references supplied by a remote tool. Keep registrations local to this call.
            build.SchemaRegistry.Fetch = (uri, _) => throw new InvalidOperationException($"External schema reference is not registered: {uri}");
            var parsed = JsonSchema.Build(schema, build);
            var instance = arguments ?? JsonSerializer.SerializeToElement(new { });
            return Errors(parsed.Evaluate(instance, Options()));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or JsonSchemaException or RefResolutionException)
        {
            return [$"Schema evaluation failed: {ex.Message}"];
        }
    }

    /// <summary>Validate the schema against the complete 2020-12 meta-schema.</summary>
    public static IReadOnlyList<string> ValidateSchema(JsonElement schema)
    {
        try
        {
            return Errors(MetaSchemas.Draft202012.Evaluate(schema, Options()));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or JsonSchemaException)
        {
            return [$"Invalid schema: {ex.Message}"];
        }
    }

    private static IReadOnlyList<string> Errors(EvaluationResults result)
    {
        if (result.IsValid) return [];
        var errors = new List<string>();
        Collect(result, errors);
        return errors.Count == 0 ? ["Schema validation failed."] : errors;
    }

    private static void Collect(EvaluationResults result, List<string> errors)
    {
        if (result.IsValid) return;
        if (result.Errors is { } details)
            foreach (var error in details)
                errors.Add($"{result.InstanceLocation} ({error.Key}): {(error.Key == "type" ? "wrong type: " : "")}{error.Value}");
        if (result.Details is { } children)
            foreach (var child in children) Collect(child, errors);
    }
}
