using System.Text.Json;
using System.Text.Json.Nodes;
using Andy.MCP.Protocol;

namespace Andy.MCP.Tests.Conformance;

public class OfficialExampleTests
{
    public static IEnumerable<object[]> Examples()
    {
        using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Conformance", "official-examples", "messages.json")));
        foreach (var fixture in source.RootElement.EnumerateArray())
            yield return [fixture.GetProperty("revision").GetString()!, fixture.GetProperty("method").GetString()!, fixture.GetProperty("message").GetRawText()];
    }
    [Theory]
    [MemberData(nameof(Examples))]
    public void OfficialMessage_RoundTripsAndMatchesItsRevision(string version, string method, string json)
    {
        var revision = ProtocolRevision.TryGet(version)!;
        var message = McpJsonDefaults.Deserialize(json);
        var output = McpJsonDefaults.Serialize(message);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(output)));
        switch (McpJsonDefaults.Deserialize(output))
        {
            case JsonRpcRequest request: ProtocolShapeValidation.Request(request, revision); break;
            case JsonRpcNotification notification: ProtocolShapeValidation.Notification(notification, revision); break;
            case JsonRpcResponse { IsError: false } response:
                ProtocolShapeValidation.Result(new JsonRpcRequest { Id = response.Id, Method = method }, response.Result, revision); break;
        }
    }
}
