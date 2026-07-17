using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;

namespace Andy.MCP.Tests;

/// <summary>
/// Tests for the MCP 2025-11-25 sampling content union (issue #41): sampling message/result
/// <c>content</c> accepts either a single content block or an array, and only text, image,
/// audio, tool_use, and tool_result blocks are valid.
/// </summary>
public class SamplingContentTests
{
    private static readonly JsonSerializerOptions Options = McpJsonDefaults.Options;

    [Fact]
    public void SamplingMessage_ScalarContent_Deserializes()
    {
        const string json = """
        { "role": "user", "content": { "type": "text", "text": "hi" } }
        """;

        var message = JsonSerializer.Deserialize<SamplingMessage>(json, Options)!;

        Assert.Equal(Role.User, message.Role);
        var block = Assert.Single(message.Content);
        Assert.Equal("hi", Assert.IsType<TextContent>(block).Text);
    }

    [Fact]
    public void SamplingMessage_ArrayContent_Deserializes()
    {
        const string json = """
        { "role": "user", "content": [ { "type": "text", "text": "a" }, { "type": "text", "text": "b" } ] }
        """;

        var message = JsonSerializer.Deserialize<SamplingMessage>(json, Options)!;

        Assert.Equal(2, message.Content.Count);
        Assert.Equal("a", ((TextContent)message.Content[0]).Text);
        Assert.Equal("b", ((TextContent)message.Content[1]).Text);
    }

    [Fact]
    public void SamplingMessage_ScalarContent_ReserializesAsArray()
    {
        var message = new SamplingMessage
        {
            Role = Role.User,
            Content = [new TextContent { Text = "hi" }]
        };

        using var doc = JsonSerializer.SerializeToDocument(message, Options);
        var content = doc.RootElement.GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal(1, content.GetArrayLength());
    }

    [Fact]
    public void CreateMessageResult_ScalarContent_Deserializes()
    {
        const string json = """
        { "role": "assistant", "content": { "type": "text", "text": "4" }, "model": "m", "stopReason": "endTurn" }
        """;

        var result = JsonSerializer.Deserialize<CreateMessageResult>(json, Options)!;

        Assert.Equal(Role.Assistant, result.Role);
        var block = Assert.Single(result.Content);
        Assert.Equal("4", Assert.IsType<TextContent>(block).Text);
    }

    [Fact]
    public void Sampling_ToolUseAndToolResult_AreValidBlocks()
    {
        const string json = """
        {
          "role": "assistant",
          "content": [
            { "type": "tool_use", "id": "1", "name": "calc", "input": { "x": 1 } },
            { "type": "tool_result", "toolUseId": "1", "content": [ { "type": "text", "text": "2" } ] }
          ]
        }
        """;

        var message = JsonSerializer.Deserialize<SamplingMessage>(json, Options)!;

        Assert.IsType<ToolUseContent>(message.Content[0]);
        Assert.IsType<ToolResultContent>(message.Content[1]);
    }

    [Theory]
    [InlineData("""{ "type": "resource_link", "uri": "file:///x", "name": "x" }""")]
    [InlineData("""{ "type": "resource", "resource": { "uri": "file:///x", "text": "x" } }""")]
    public void Sampling_InvalidContentBlock_Throws(string contentJson)
    {
        var json = $$"""{ "role": "user", "content": {{contentJson}} }""";

        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<SamplingMessage>(json, Options));
    }

    [Fact]
    public void Sampling_Write_RejectsInvalidBlock()
    {
        var message = new SamplingMessage
        {
            Role = Role.User,
            Content = [new EmbeddedResource { Resource = new TextResourceContents { Uri = "file:///x", Text = "x" } }]
        };

        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(message, Options));
    }

    [Fact]
    public void CreateMessageResult_Meta_RoundTrips()
    {
        var result = new CreateMessageResult
        {
            Role = Role.Assistant,
            Content = [new TextContent { Text = "hi" }],
            Model = "m",
            Meta = JsonSerializer.SerializeToElement(new { trace = "abc" })
        };

        var json = JsonSerializer.Serialize(result, Options);
        var roundTripped = JsonSerializer.Deserialize<CreateMessageResult>(json, Options)!;

        Assert.NotNull(roundTripped.Meta);
        Assert.Equal("abc", roundTripped.Meta!.Value.GetProperty("trace").GetString());
    }
}
