using System.Text.Json;
using Andy.MCP.Server;
namespace Andy.MCP.Tests.Server;

public class OfficialJsonSchemaTests
{
    public static IEnumerable<object[]> Cases()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Conformance", "json-schema");
        foreach (var file in Directory.GetFiles(root, "*.json").Where(p => Path.GetFileName(p) != "sources.json").Order())
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var group in document.RootElement.EnumerateArray())
                foreach (var test in group.GetProperty("tests").EnumerateArray())
                    yield return new object[] { Path.GetFileName(file) + ": " + group.GetProperty("description").GetString() + " / " + test.GetProperty("description").GetString(),
                        group.GetProperty("schema").GetRawText(), test.GetProperty("data").GetRawText(), test.GetProperty("valid").GetBoolean() };
        }
    }
    [Theory]
    [MemberData(nameof(Cases))]
    public void OfficialDraft202012(string description, string schema, string instance, bool valid)
    {
        var errors = JsonSchemaValidator.Validate(JsonSerializer.Deserialize<JsonElement>(instance), JsonSerializer.Deserialize<JsonElement>(schema));
        Assert.True(valid == (errors.Count == 0), description + ": " + string.Join("; ", errors));
    }
}
