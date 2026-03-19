using System.Text.Json;
using Andy.MCP.Protocol;

namespace Andy.MCP.Tests.Protocol;

public class MetaTests
{
    [Fact]
    public void Meta_WithStringProgressToken_RoundTrips()
    {
        var meta = new Meta { ProgressToken = (RequestId)"token-1" };
        var json = JsonSerializer.Serialize(meta, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<Meta>(json, McpJsonDefaults.Options);

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized!.ProgressToken);
        Assert.True(deserialized.ProgressToken.Value.IsString);
        Assert.Equal("token-1", deserialized.ProgressToken.Value.AsString());
    }

    [Fact]
    public void Meta_WithNumberProgressToken_RoundTrips()
    {
        var meta = new Meta { ProgressToken = (RequestId)42 };
        var json = JsonSerializer.Serialize(meta, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<Meta>(json, McpJsonDefaults.Options);

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized!.ProgressToken);
        Assert.True(deserialized.ProgressToken.Value.IsNumber);
        Assert.Equal(42L, deserialized.ProgressToken.Value.AsNumber());
    }

    [Fact]
    public void Meta_WithNullProgressToken_OmittedInJson()
    {
        var meta = new Meta();
        var json = JsonSerializer.Serialize(meta, McpJsonDefaults.Options);

        Assert.DoesNotContain("progressToken", json);
    }

    [Fact]
    public void Meta_InRequestParams_RoundTrips()
    {
        // Simulate a request with _meta.progressToken in params
        var paramsObj = new
        {
            _meta = new { progressToken = "prog-1" },
            name = "test-tool"
        };

        var request = new JsonRpcRequest
        {
            Id = 1,
            Method = "tools/call",
            Params = McpJsonDefaults.ToElement(paramsObj)
        };

        var json = McpJsonDefaults.Serialize(request);
        Assert.Contains("\"_meta\"", json);
        Assert.Contains("\"progressToken\"", json);
        Assert.Contains("\"prog-1\"", json);

        var deserialized = Assert.IsType<JsonRpcRequest>(McpJsonDefaults.Deserialize(json));
        var meta = deserialized.Params!.Value.GetProperty("_meta");
        Assert.Equal("prog-1", meta.GetProperty("progressToken").GetString());
    }

    [Fact]
    public void Meta_WithExtensions_RoundTrips()
    {
        var meta = new Meta
        {
            ProgressToken = (RequestId)"t1",
            Extensions = new Dictionary<string, JsonElement>
            {
                ["customKey"] = JsonSerializer.SerializeToElement("customValue")
            }
        };

        var json = JsonSerializer.Serialize(meta, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<Meta>(json, McpJsonDefaults.Options);

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized!.Extensions);
        Assert.True(deserialized.Extensions.ContainsKey("customKey"));
        Assert.Equal("customValue", deserialized.Extensions["customKey"].GetString());
    }
}
