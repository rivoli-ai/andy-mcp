using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Json.Schema;
namespace Andy.MCP.Tests.Protocol;

public class ElicitationRevisionTests
{
    [Fact]
    public void OlderForm_UsesLegacyTitledEnum_AndOnlyBooleanDefault()
    {
        var schema = new ElicitationSchema
        {
            Properties = new Dictionary<string, PrimitiveSchemaDefinition>
            {
                ["name"] = PrimitiveSchemaDefinition.StringField(@default: "name"),
                ["count"] = PrimitiveSchemaDefinition.NumberField(@default: 2),
                ["enabled"] = PrimitiveSchemaDefinition.BooleanField(@default: true),
                ["choice"] = PrimitiveSchemaDefinition.TitledEnumField([new("one", "First"), new("two", "Second")], @default: "one")
            }
        };
        var old = RevisionAwareJson.ToElementForRevision(ElicitRequest.Form("input", schema), ProtocolRevision.V2025_06_18);
        var fields = old.GetProperty("requestedSchema").GetProperty("properties");
        Assert.False(fields.GetProperty("name").TryGetProperty("default", out _));
        Assert.False(fields.GetProperty("count").TryGetProperty("default", out _));
        Assert.True(fields.GetProperty("enabled").GetProperty("default").GetBoolean());
        Assert.False(fields.GetProperty("choice").TryGetProperty("oneOf", out _));
        Assert.Equal("First", fields.GetProperty("choice").GetProperty("enumNames")[0].GetString());
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Conformance", "schemas", "schema-2025-06-18.json"));
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List };
        var build = new BuildOptions { Dialect = Dialect.Draft202012, SchemaRegistry = new() };
        build.SchemaRegistry.Register(new Uri("https://mcp.test/old"), JsonSchema.FromText(text, build));
        var validator = new JsonSchemaBuilder().Ref("https://mcp.test/old#/definitions/ElicitRequest").Build(build);
        var envelope = McpJsonDefaults.ToElement(new JsonRpcRequest { Id = 1, Method = "elicitation/create", Params = old });
        var result = validator.Evaluate(envelope, options);
        Assert.True(result.IsValid, JsonSerializer.Serialize(result));
        var current = RevisionAwareJson.ToElementForRevision(ElicitRequest.Form("input", schema), ProtocolRevision.Latest);
        Assert.True(current.GetProperty("requestedSchema").GetProperty("properties").GetProperty("choice").TryGetProperty("oneOf", out _));
    }

    [Fact]
    public void MultiSelect_CannotSilentlyDegradeToOlderElicitation()
    {
        var field = PrimitiveSchemaDefinition.MultiSelectEnumField(["one", "two"]);
        Assert.Throws<JsonException>(() => RevisionAwareJson.ToElementForRevision(field, ProtocolRevision.V2025_06_18));
        var request = ElicitRequest.Form("choose", new ElicitationSchema { Properties = new Dictionary<string, PrimitiveSchemaDefinition> { ["choice"] = field } });
        Assert.Throws<JsonException>(() => RevisionAwareJson.ToElementForRevision(request, ProtocolRevision.V2025_06_18));
        Assert.Throws<JsonException>(() => RevisionAwareJson.ToElementForRevision(request, ProtocolRevision.V2025_03_26));
    }
}
