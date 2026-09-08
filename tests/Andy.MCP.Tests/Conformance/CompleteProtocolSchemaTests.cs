using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Andy.MCP.Protocol;
using Json.Schema;
namespace Andy.MCP.Tests.Conformance;

public class CompleteProtocolSchemaTests
{
    public static IEnumerable<object[]> Fixtures()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Conformance", "protocol-corpus", "fixtures.json")));
        var index = 0;
        foreach (var fixture in document.RootElement.EnumerateArray())
            yield return [index++, fixture.GetProperty("revision").GetString()!, fixture.GetProperty("definition").GetString()!,
                fixture.GetProperty("model").GetString()!, fixture.GetProperty("value").GetRawText()];
    }

    private sealed class Validator
    {
        private readonly EvaluationOptions _options = new() { OutputFormat = OutputFormat.List };
        private readonly Dictionary<string, JsonSchema> _definitions = new();
        private readonly object _gate = new();
        public readonly string[] Names;
        public Validator(string version)
        {
            var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Conformance", "schemas", $"schema-{version}.json"));
            using var root = JsonDocument.Parse(text);
            var prefix = root.RootElement.TryGetProperty("$defs", out var definitions) ? "$defs" : "definitions";
            if (prefix == "definitions") definitions = root.RootElement.GetProperty(prefix);
            Names = definitions.EnumerateObject().Select(p => p.Name).ToArray();
            var uri = new Uri($"https://mcp.test/{version}/schema");
            var build = new BuildOptions { Dialect = Dialect.Draft202012, SchemaRegistry = new() };
            build.SchemaRegistry.Register(uri, JsonSchema.FromText(text, build, uri));
            build.SchemaRegistry.Fetch = (uri, _) => throw new InvalidOperationException("Unexpected schema fetch: " + uri);
            foreach (var name in Names) _definitions[name] = new JsonSchemaBuilder().Ref($"{uri}#/{prefix}/{name}").Build(build);
        }
        public void Valid(string name, string json)
        {
            lock (_gate)
            {
                var result = _definitions[name].Evaluate(JsonSerializer.Deserialize<JsonElement>(json), _options);
                Assert.True(result.IsValid, JsonSerializer.Serialize(result));
            }
        }
    }
    private static readonly ConcurrentDictionary<string, Validator> Validators = new();

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EveryOfficialShape_RoundTripsThroughItsModel(int index, string version, string definition, string model, string json)
    {
        Assert.True(index >= 0); // Stable case identity in test reports.
        var validator = Validators.GetOrAdd(version, v => new Validator(v));
        validator.Valid(definition, json); // The fixture itself must be valid independently of our model.
        var type = model == "String" ? typeof(string) : typeof(McpSession).Assembly.GetTypes().Single(t => t.IsPublic && t.Name == model);
        var value = JsonSerializer.Deserialize(json, type, McpJsonDefaults.Options);
        var output = JsonSerializer.SerializeToElement(value, type, RevisionAwareJson.OptionsFor(ProtocolRevision.TryGet(version)!));
        validator.Valid(definition, output.GetRawText());
        using var input = JsonDocument.Parse(json);
        if (input.RootElement.ValueKind == JsonValueKind.Object && input.RootElement.TryGetProperty("vendor/fixture", out var extension))
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(extension.GetRawText()), JsonNode.Parse(output.GetProperty("vendor/fixture").GetRawText())));
    }

    [Fact]
    public void Corpus_CoversEveryDefinitionOfEveryNegotiatedRevision()
    {
        var fixtures = Fixtures().ToArray();
        foreach (var revision in ProtocolRevision.Supported)
        {
            var expected = Validators.GetOrAdd(revision.Version, v => new Validator(v)).Names.Order().ToArray();
            var actual = fixtures.Where(f => (string)f[1] == revision.Version).Select(f => (string)f[2]).Distinct().Order().ToArray();
            Assert.Equal(expected, actual);
        }
    }
}
