using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;

namespace Andy.MCP.Tests;

/// <summary>
/// Verifies that the reserved <c>_meta</c> field round-trips (preserving unknown metadata) on
/// every request-params, result, and object type where the MCP 2025-11-25 schema permits it
/// (issue #41).
/// </summary>
public class MetaRoundTripTests
{
    private static readonly JsonSerializerOptions Options = McpJsonDefaults.Options;

    private static JsonElement SampleMeta() =>
        JsonSerializer.SerializeToElement(new { trace = "abc-123", extra = new { n = 7 } });

    private static void AssertMetaPreserved(JsonElement? meta)
    {
        Assert.NotNull(meta);
        Assert.Equal("abc-123", meta!.Value.GetProperty("trace").GetString());
        Assert.Equal(7, meta.Value.GetProperty("extra").GetProperty("n").GetInt32());
    }

    private static T RoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;

    [Fact]
    public void CallToolRequest_Meta_RoundTrips()
    {
        var back = RoundTrip(new CallToolRequest { Name = "t", Meta = SampleMeta() });
        AssertMetaPreserved(back.Meta);
    }

    [Fact]
    public void CallToolResult_Meta_RoundTrips()
    {
        var back = RoundTrip(new CallToolResult { Content = [new TextContent { Text = "hi" }], Meta = SampleMeta() });
        AssertMetaPreserved(back.Meta);
    }

    [Fact]
    public void ReadResourceResult_Meta_RoundTrips()
    {
        var back = RoundTrip(new ReadResourceResult { Meta = SampleMeta() });
        AssertMetaPreserved(back.Meta);
    }

    [Fact]
    public void GetPromptResult_Meta_RoundTrips()
    {
        var back = RoundTrip(new GetPromptResult { Meta = SampleMeta() });
        AssertMetaPreserved(back.Meta);
    }

    [Fact]
    public void InitializeParams_Meta_RoundTrips()
    {
        var back = RoundTrip(new InitializeParams
        {
            ProtocolVersion = McpSession.LatestProtocolVersion,
            Capabilities = new ClientCapabilities(),
            ClientInfo = new Implementation("c", "1.0.0"),
            Meta = SampleMeta()
        });
        AssertMetaPreserved(back.Meta);
    }

    [Fact]
    public void InitializeResult_Meta_RoundTrips()
    {
        var back = RoundTrip(new InitializeResult
        {
            ProtocolVersion = McpSession.LatestProtocolVersion,
            Capabilities = new ServerCapabilities(),
            ServerInfo = new Implementation("s", "1.0.0"),
            Meta = SampleMeta()
        });
        AssertMetaPreserved(back.Meta);
    }

    [Fact]
    public void PaginatedResult_Meta_RoundTrips()
    {
        var back = RoundTrip(new PaginatedResult { NextCursor = "c", Meta = SampleMeta() });
        AssertMetaPreserved(back.Meta);
    }

    [Fact]
    public void PaginatedRequest_Meta_RoundTrips()
    {
        var back = RoundTrip(new PaginatedRequest { Meta = SampleMeta() });
        AssertMetaPreserved(back.Meta);
    }

    [Fact]
    public void ListRootsResult_Meta_RoundTrips()
    {
        var back = RoundTrip(new ListRootsResult { Roots = [], Meta = SampleMeta() });
        AssertMetaPreserved(back.Meta);
    }

    [Fact]
    public void Root_Meta_RoundTrips()
    {
        var back = RoundTrip(new Root { Uri = "file:///x", Meta = SampleMeta() });
        AssertMetaPreserved(back.Meta);
    }

    [Fact]
    public void CreateMessageRequest_Meta_RoundTrips()
    {
        var back = RoundTrip(new CreateMessageRequest
        {
            Messages = [new SamplingMessage { Role = Role.User, Content = [new TextContent { Text = "hi" }] }],
            MaxTokens = 10,
            Meta = SampleMeta()
        });
        AssertMetaPreserved(back.Meta);
    }

    [Fact]
    public void ElicitResult_Meta_RoundTrips()
    {
        var back = RoundTrip(new ElicitResult { Action = "decline", Meta = SampleMeta() });
        AssertMetaPreserved(back.Meta);
    }
}
