using System.Text.Json;
using System.Text.Json.Nodes;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Json.Schema;

namespace Andy.MCP.Tests.Conformance;

public class RevisionWireSchemaTests
{
    public static IEnumerable<object[]> Revisions => ProtocolRevision.All.Select(r => new object[] { r.Version });

    private static void Valid(string version, string definition, JsonElement instance)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Conformance", "schemas", $"schema-{version}.json"));
        var schema = JsonSchema.FromText(text);
        var root = JsonNode.Parse(text)!;
        var prefix = root["$defs"] is null ? "definitions" : "$defs";
        var uri = new Uri($"https://mcp.test/{version}/schema");
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List };
        options.SchemaRegistry.Register(uri, schema);
        var wrapper = new JsonSchemaBuilder().Ref($"{uri}#/{prefix}/{definition}").Build();
        var result = wrapper.Evaluate(JsonNode.Parse(instance.GetRawText()), options);
        Assert.True(result.IsValid, JsonSerializer.Serialize(result));
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public void SamplingResponse_MatchesEveryAdvertisedRevision(string version)
    {
        var revision = ProtocolRevision.TryGet(version)!;
        var value = new CreateMessageResult { Role = Role.Assistant, Model = "test", Content = [new TextContent("ok")] };
        var json = RevisionAwareJson.ToElementForRevision(value, revision);
        Assert.Equal(revision == ProtocolRevision.Latest ? JsonValueKind.Array : JsonValueKind.Object,
            json.GetProperty("content").ValueKind);
        Valid(version, "CreateMessageResult", json);
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public void ToolMetadata_GatesOnlyNewerFields(string version)
    {
        var revision = ProtocolRevision.TryGet(version)!;
        var tool = new Tool
        {
            Name = "t",
            Title = "Title",
            InputSchema = McpJsonDefaults.ToElement(new { type = "object" }),
            OutputSchema = McpJsonDefaults.ToElement(new { type = "object" }),
            Annotations = new() { ReadOnlyHint = true },
            Execution = new() { TaskSupport = "optional" },
            Icons = [new Icon { Source = "https://example.com/icon.png" }]
        };
        var json = RevisionAwareJson.ToElementForRevision(tool, revision);
        Assert.Equal(revision.Ordinal >= 1, json.TryGetProperty("annotations", out _));
        Assert.Equal(revision.Ordinal >= 2, json.TryGetProperty("outputSchema", out _));
        Assert.Equal(revision.Ordinal >= 3, json.TryGetProperty("execution", out _));
        Valid(version, "Tool", json);
    }

    [Fact]
    public void UnlimitedTaskRetention_SerializesRequiredNullTtl()
    {
        var task = new McpTask
        {
            TaskId = "t",
            Status = McpTaskStatus.Working,
            CreatedAt = "2026-09-08T00:00:00Z",
            LastUpdatedAt = "2026-09-08T00:00:00Z",
            Ttl = null
        };
        var json = McpJsonDefaults.ToElement(task);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("ttl").ValueKind);
        Valid("2025-11-25", "Task", json);
    }

    [Fact]
    public void OlderSampling_DoesNotSilentlyDropExtraContent()
    {
        var value = new SamplingMessage { Role = Role.User, Content = [new TextContent("one"), new TextContent("two")] };
        Assert.Throws<JsonException>(() => RevisionAwareJson.SerializeForRevision(value, ProtocolRevision.V2025_06_18));
    }

    [Theory]
    [InlineData("{\"name\":\"t\",\"inputSchema\":{\"type\":\"object\"},\"_meta\":{\"vendor\":1},\"vendor/field\":{\"value\":42}}")]
    public void Tool_RetainsUnknownMetadataAndExtensions(string input)
    {
        var tool = JsonSerializer.Deserialize<Tool>(input, McpJsonDefaults.Options)!;
        var output = McpJsonDefaults.ToElement(tool);
        Assert.Equal(42, output.GetProperty("vendor/field").GetProperty("value").GetInt32());
        Assert.Equal(1, output.GetProperty("_meta").GetProperty("vendor").GetInt32());
    }

    [Fact]
    public void ContentAndRpc_RetainUnknownFields()
    {
        var content = JsonSerializer.Deserialize<Content>("""{"type":"text","text":"ok","vendor":42}""", McpJsonDefaults.Options)!;
        Assert.Equal(42, McpJsonDefaults.ToElement(content).GetProperty("vendor").GetInt32());
        var message = McpJsonDefaults.Deserialize("""{"jsonrpc":"2.0","method":"ping","id":1,"vendor":42}""")!;
        Assert.Equal(42, JsonDocument.Parse(McpJsonDefaults.Serialize(message)).RootElement.GetProperty("vendor").GetInt32());
    }
}
