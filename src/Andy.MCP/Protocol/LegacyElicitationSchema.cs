using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
namespace Andy.MCP.Protocol;

internal static class LegacyElicitationSchema
{
    internal static JsonObject Field(JsonObject field)
    {
        var type = field["type"]?.GetValue<string>();
        if (type == "array") throw new JsonException("Multi-select elicitation requires protocol 2025-11-25.");
        // Boolean defaults were already part of the 2025-06-18 schema.
        if (type != "boolean") field.Remove("default");
        if (field["oneOf"] is JsonArray options)
        {
            var values = new JsonArray();
            var names = new JsonArray();
            foreach (var option in options)
            {
                values.Add(option?["const"]?.GetValue<string>() ?? throw new JsonException("Enum option requires const."));
                names.Add(option?["title"]?.GetValue<string>() ?? throw new JsonException("Enum option requires title."));
            }
            field.Remove("oneOf");
            field["enum"] = values;
            field["enumNames"] = names;
        }
        return field;
    }

    internal static JsonNode Schema(JsonElement value)
    {
        var schema = JsonNode.Parse(value.GetRawText())?.AsObject() ?? throw new JsonException("Elicitation requires an object schema.");
        if (schema["properties"] is JsonObject properties)
            foreach (var property in properties.ToArray())
                if (property.Value is JsonObject field) Field(field);
        return schema;
    }
}

internal sealed class LegacyRequestedSchemaConverter : JsonConverter<JsonElement?>
{
    public override JsonElement? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.Clone();
    }
    public override void Write(Utf8JsonWriter writer, JsonElement? value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        LegacyElicitationSchema.Schema(value.Value).WriteTo(writer);
    }
}

internal sealed class LegacyPrimitiveSchemaConverter(int ordinal) : JsonConverter<PrimitiveSchemaDefinition>
{
    public override PrimitiveSchemaDefinition? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<PrimitiveSchemaDefinition>(ref reader, McpJsonDefaults.Options);
    public override void Write(Utf8JsonWriter writer, PrimitiveSchemaDefinition value, JsonSerializerOptions options)
    {
        if (ordinal < 2) throw new JsonException("Elicitation requires protocol 2025-06-18 or later.");
        LegacyElicitationSchema.Field(JsonSerializer.SerializeToNode(value, McpJsonDefaults.Options)!.AsObject()).WriteTo(writer);
    }
}
