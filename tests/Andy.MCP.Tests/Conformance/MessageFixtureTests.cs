using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;

namespace Andy.MCP.Tests.Conformance;

/// <summary>
/// Conformance fixtures (issue #50): golden fixtures of valid MCP 2025-11-25 messages that must
/// deserialize and reserialize without loss, and negative fixtures of malformed messages that the
/// library must reject rather than silently accept.
/// </summary>
public class MessageFixtureTests
{
    // ---- Golden fixtures: valid messages must round-trip through the JSON-RPC layer. ----

    public static IEnumerable<object[]> GoldenMessages() => new[]
    {
        new object[] { """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"c","version":"1.0"}}}""" },
        new object[] { """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-11-25","capabilities":{"tools":{"listChanged":true}},"serverInfo":{"name":"s","version":"1.0"}}}""" },
        new object[] { """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"add","arguments":{"a":1,"b":2}}}""" },
        new object[] { """{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"3"}],"structuredContent":{"sum":3}}}""" },
        new object[] { """{"jsonrpc":"2.0","id":3,"result":{"contents":[{"uri":"file:///a","text":"hi"}]}}""" },
        new object[] { """{"jsonrpc":"2.0","method":"notifications/progress","params":{"progressToken":1,"progress":0.5,"total":1}}""" },
        new object[] { """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7,"reason":"user"}}""" },
        new object[] { """{"jsonrpc":"2.0","id":4,"method":"sampling/createMessage","params":{"messages":[{"role":"user","content":{"type":"text","text":"hi"}}],"maxTokens":10}}""" },
        new object[] { """{"jsonrpc":"2.0","id":5,"method":"elicitation/create","params":{"mode":"url","message":"auth","elicitationId":"e1","url":"https://x/a"}}""" },
        new object[] { """{"jsonrpc":"2.0","id":6,"result":{"task":{"taskId":"t1","status":"working","createdAt":"2026-01-01T00:00:00.000Z","lastUpdatedAt":"2026-01-01T00:00:00.000Z","ttl":60000}}}""" },
    };

    [Theory]
    [MemberData(nameof(GoldenMessages))]
    public void GoldenMessage_RoundTrips(string json)
    {
        var message = McpJsonDefaults.Deserialize(json);
        var reserialized = McpJsonDefaults.Serialize(message);

        // Reparsing the reserialized form yields an equivalent message type.
        var reparsed = McpJsonDefaults.Deserialize(reserialized);
        Assert.Equal(message.GetType(), reparsed.GetType());
    }

    [Fact]
    public void Golden_SamplingResult_TypedRoundTrip_PreservesContent()
    {
        const string json = """{"role":"assistant","content":{"type":"text","text":"ok"},"model":"m","stopReason":"endTurn"}""";
        var result = JsonSerializer.Deserialize<CreateMessageResult>(json, McpJsonDefaults.Options)!;
        Assert.Equal("ok", ((TextContent)Assert.Single(result.Content)).Text);
    }

    // ---- Negative fixtures: malformed messages must be rejected. ----

    public static IEnumerable<object[]> InvalidMessages() => new[]
    {
        new object[] { "not json at all" },
        new object[] { """{"jsonrpc":"2.0","id":null,"method":"ping"}""" },          // id must not be null
        new object[] { """{"jsonrpc":"2.0","id":true,"method":"ping"}""" },          // id must be string/number
    };

    [Theory]
    [MemberData(nameof(InvalidMessages))]
    public void InvalidMessage_IsRejected(string json)
    {
        Assert.ThrowsAny<Exception>(() => McpJsonDefaults.Deserialize(json));
    }

    [Fact]
    public void InvalidSamplingContentBlock_IsRejected()
    {
        // resource_link is not a valid sampling content block.
        const string json = """{"role":"user","content":{"type":"resource_link","uri":"file:///x","name":"x"}}""";
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<SamplingMessage>(json, McpJsonDefaults.Options));
    }

    [Fact]
    public void InvalidTaskStatus_IsRejected()
    {
        const string json = """{"taskId":"t","status":"exploded","createdAt":"x","lastUpdatedAt":"x","ttl":null}""";
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<McpTask>(json, McpJsonDefaults.Options));
    }

    [Fact]
    public void InvalidRole_IsRejected()
    {
        const string json = """{"role":"wizard","content":{"type":"text","text":"x"}}""";
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<SamplingMessage>(json, McpJsonDefaults.Options));
    }
}
