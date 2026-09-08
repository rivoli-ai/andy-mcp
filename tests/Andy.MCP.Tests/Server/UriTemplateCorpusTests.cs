using System.Text.Json;
using Andy.MCP.Server;
namespace Andy.MCP.Tests.Server;

public class UriTemplateCorpusTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Conformance", "uri-templates"), "spec-*.json"))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var group in json.RootElement.EnumerateObject())
                foreach (var example in group.Value.GetProperty("testcases").EnumerateArray())
                {
                    var outputs = example[1].ValueKind == JsonValueKind.Array ? example[1].EnumerateArray().Select(v => v.GetString()!).ToArray() : new[] { example[1].GetString()! };
                    foreach (var output in outputs) yield return new object[] { Path.GetFileName(file) + ": " + group.Name, example[0].GetString()!, output };
                }
        }
    }
    [Theory]
    [MemberData(nameof(Cases))]
    public void RfcExamples_AreRecognized(string group, string template, string expanded) =>
        Assert.True(new UriTemplate(template).TryMatch(expanded, out _), group + ": " + template + " -> " + expanded);
}
