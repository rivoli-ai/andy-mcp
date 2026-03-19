using System.Text.Json;
using Andy.MCP.Protocol;

namespace Andy.MCP.Tests.Protocol;

public class RequestIdTests
{
    [Fact]
    public void ImplicitConversion_FromString()
    {
        RequestId id = "abc-123";
        Assert.True(id.IsString);
        Assert.False(id.IsNumber);
        Assert.Equal("abc-123", id.AsString());
    }

    [Fact]
    public void ImplicitConversion_FromInt()
    {
        RequestId id = 42;
        Assert.True(id.IsNumber);
        Assert.False(id.IsString);
        Assert.Equal(42L, id.AsNumber());
    }

    [Fact]
    public void ImplicitConversion_FromLong()
    {
        RequestId id = 9999999999L;
        Assert.True(id.IsNumber);
        Assert.Equal(9999999999L, id.AsNumber());
    }

    [Fact]
    public void AsString_ThrowsForNumber()
    {
        RequestId id = 1;
        Assert.Throws<InvalidOperationException>(() => id.AsString());
    }

    [Fact]
    public void AsNumber_ThrowsForString()
    {
        RequestId id = "test";
        Assert.Throws<InvalidOperationException>(() => id.AsNumber());
    }

    [Fact]
    public void Equality_SameStrings()
    {
        RequestId a = "test";
        RequestId b = "test";
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void Equality_SameNumbers()
    {
        RequestId a = 42;
        RequestId b = 42;
        Assert.Equal(a, b);
    }

    [Fact]
    public void Inequality_StringVsNumber()
    {
        // "1" (string) != 1 (number) — different types
        RequestId a = "1";
        RequestId b = 1;
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Inequality_DifferentStrings()
    {
        RequestId a = "abc";
        RequestId b = "xyz";
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ToString_String()
    {
        RequestId id = "hello";
        Assert.Equal("hello", id.ToString());
    }

    [Fact]
    public void ToString_Number()
    {
        RequestId id = 99;
        Assert.Equal("99", id.ToString());
    }

    [Fact]
    public void JsonSerialize_String()
    {
        RequestId id = "abc";
        var json = JsonSerializer.Serialize(id, McpJsonDefaults.Options);
        Assert.Equal("\"abc\"", json);
    }

    [Fact]
    public void JsonSerialize_Number()
    {
        RequestId id = 42;
        var json = JsonSerializer.Serialize(id, McpJsonDefaults.Options);
        Assert.Equal("42", json);
    }

    [Fact]
    public void JsonDeserialize_String()
    {
        var id = JsonSerializer.Deserialize<RequestId>("\"test-id\"", McpJsonDefaults.Options);
        Assert.True(id.IsString);
        Assert.Equal("test-id", id.AsString());
    }

    [Fact]
    public void JsonDeserialize_Number()
    {
        var id = JsonSerializer.Deserialize<RequestId>("123", McpJsonDefaults.Options);
        Assert.True(id.IsNumber);
        Assert.Equal(123L, id.AsNumber());
    }

    [Fact]
    public void JsonDeserialize_Null_Throws()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<RequestId>("null", McpJsonDefaults.Options));
    }

    [Fact]
    public void JsonRoundTrip_String()
    {
        RequestId original = "round-trip";
        var json = JsonSerializer.Serialize(original, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<RequestId>(json, McpJsonDefaults.Options);
        Assert.Equal(original, deserialized);
    }

    [Fact]
    public void JsonRoundTrip_Number()
    {
        RequestId original = 12345L;
        var json = JsonSerializer.Serialize(original, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<RequestId>(json, McpJsonDefaults.Options);
        Assert.Equal(original, deserialized);
    }

    [Fact]
    public void GetHashCode_ConsistentForEqualValues()
    {
        RequestId a = "same";
        RequestId b = "same";
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        RequestId c = 100;
        RequestId d = 100;
        Assert.Equal(c.GetHashCode(), d.GetHashCode());
    }
}
