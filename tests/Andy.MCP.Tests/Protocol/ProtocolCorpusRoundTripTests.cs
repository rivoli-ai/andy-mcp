using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;

namespace Andy.MCP.Tests;

/// <summary>
/// Round-trips a corpus of realistic MCP 2025-11-25 wire messages through the typed models
/// (issue #41): each deserializes and reserializes without losing known fields, extension data,
/// or <c>_meta</c>. Parses from JSON strings the way a peer would receive them.
/// </summary>
public class ProtocolCorpusRoundTripTests
{
    private static readonly JsonSerializerOptions Options = McpJsonDefaults.Options;

    /// <summary>Deserialize to <typeparamref name="T"/>, reserialize, and return the reparsed JSON DOM.</summary>
    private static JsonElement RoundTrip<T>(string json)
    {
        var model = JsonSerializer.Deserialize<T>(json, Options)!;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(model, Options));
        return doc.RootElement.Clone();
    }

    [Fact]
    public void InitializeResult_WithIconsAndMeta_RoundTrips()
    {
        const string json = """
        {
          "protocolVersion": "2025-11-25",
          "capabilities": { "tools": { "listChanged": true }, "logging": {} },
          "serverInfo": {
            "name": "everything", "version": "1.0.0", "title": "Everything",
            "description": "A demo server", "websiteUrl": "https://example.com",
            "icons": [ { "src": "https://example.com/i.png", "mimeType": "image/png", "sizes": ["48x48"], "theme": "dark" } ]
          },
          "instructions": "Use me.",
          "_meta": { "vendor/trace": "xyz" }
        }
        """;

        var el = RoundTrip<InitializeResult>(json);
        Assert.Equal("everything", el.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal("A demo server", el.GetProperty("serverInfo").GetProperty("description").GetString());
        Assert.Equal("dark", el.GetProperty("serverInfo").GetProperty("icons")[0].GetProperty("theme").GetString());
        Assert.Equal("xyz", el.GetProperty("_meta").GetProperty("vendor/trace").GetString());
    }

    [Fact]
    public void CallToolResult_WithStructuredContentAndResourceLink_RoundTrips()
    {
        const string json = """
        {
          "content": [
            { "type": "text", "text": "done" },
            { "type": "resource_link", "uri": "file:///out.txt", "name": "out.txt", "mimeType": "text/plain" }
          ],
          "structuredContent": { "rows": 3 },
          "isError": false,
          "_meta": { "vendor/duration_ms": 12 }
        }
        """;

        var el = RoundTrip<CallToolResult>(json);
        Assert.Equal(2, el.GetProperty("content").GetArrayLength());
        Assert.Equal("resource_link", el.GetProperty("content")[1].GetProperty("type").GetString());
        Assert.Equal(3, el.GetProperty("structuredContent").GetProperty("rows").GetInt32());
        Assert.Equal(12, el.GetProperty("_meta").GetProperty("vendor/duration_ms").GetInt32());
    }

    [Fact]
    public void ReadResourceResult_WithBlobAndMeta_RoundTrips()
    {
        const string json = """
        {
          "contents": [
            { "uri": "file:///a.txt", "mimeType": "text/plain", "text": "hello" },
            { "uri": "file:///b.bin", "mimeType": "application/octet-stream", "blob": "AQID" }
          ],
          "_meta": { "vendor/cached": true }
        }
        """;

        var el = RoundTrip<ReadResourceResult>(json);
        Assert.Equal(2, el.GetProperty("contents").GetArrayLength());
        Assert.Equal("hello", el.GetProperty("contents")[0].GetProperty("text").GetString());
        Assert.Equal("AQID", el.GetProperty("contents")[1].GetProperty("blob").GetString());
        Assert.True(el.GetProperty("_meta").GetProperty("vendor/cached").GetBoolean());
    }

    [Fact]
    public void SamplingCreateMessage_ScalarContentAndTools_RoundTrips()
    {
        const string json = """
        {
          "messages": [ { "role": "user", "content": { "type": "text", "text": "hi" } } ],
          "maxTokens": 100,
          "toolChoice": { "mode": "auto" },
          "tools": [ { "name": "calc", "description": "math", "inputSchema": { "type": "object" } } ],
          "_meta": { "vendor/session": "s1" }
        }
        """;

        var el = RoundTrip<CreateMessageRequest>(json);
        // Scalar content normalizes to a one-element array on the wire.
        Assert.Equal(JsonValueKind.Array, el.GetProperty("messages")[0].GetProperty("content").ValueKind);
        Assert.Equal("auto", el.GetProperty("toolChoice").GetProperty("mode").GetString());
        Assert.Equal("calc", el.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal("s1", el.GetProperty("_meta").GetProperty("vendor/session").GetString());
    }

    [Fact]
    public void ElicitationForm_WithTypedSchema_RoundTrips()
    {
        const string json = """
        {
          "mode": "form",
          "message": "Configure",
          "requestedSchema": {
            "type": "object",
            "properties": {
              "tier": { "type": "string", "enum": ["free", "pro"], "default": "free" },
              "count": { "type": "integer", "minimum": 1, "default": 3 }
            },
            "required": ["tier"]
          }
        }
        """;

        var el = RoundTrip<ElicitRequest>(json);
        Assert.Equal("form", el.GetProperty("mode").GetString());
        var props = el.GetProperty("requestedSchema").GetProperty("properties");
        Assert.Equal("free", props.GetProperty("tier").GetProperty("default").GetString());
        Assert.Equal(3, props.GetProperty("count").GetProperty("default").GetInt32());
    }

    [Fact]
    public void FullJsonRpcResponse_PreservesResultVerbatim()
    {
        const string json = """
        { "jsonrpc": "2.0", "id": 7, "result": { "value": 42, "_meta": { "vendor/x": "y" } } }
        """;

        var message = McpJsonDefaults.Deserialize(json);
        var response = Assert.IsType<JsonRpcResponse>(message);
        using var doc = JsonDocument.Parse(McpJsonDefaults.Serialize(response));

        var result = doc.RootElement.GetProperty("result");
        Assert.Equal(42, result.GetProperty("value").GetInt32());
        Assert.Equal("y", result.GetProperty("_meta").GetProperty("vendor/x").GetString());
        Assert.Equal(7, doc.RootElement.GetProperty("id").GetInt32());
    }
}
