using System.Text.Json;
using Andy.MCP.Protocol;

namespace Andy.MCP.Tests.Protocol;

public class ResourceContentsTests
{
    private static readonly JsonSerializerOptions Options = McpJsonDefaults.Options;

    [Fact]
    public void TextResourceContents_RoundTrips()
    {
        ResourceContents original = new TextResourceContents
        {
            Uri = "file:///readme.md",
            MimeType = "text/markdown",
            Text = "# Hello World"
        };

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<ResourceContents>(json, Options);

        var result = Assert.IsType<TextResourceContents>(deserialized);
        Assert.Equal("file:///readme.md", result.Uri);
        Assert.Equal("text/markdown", result.MimeType);
        Assert.Equal("# Hello World", result.Text);
    }

    [Fact]
    public void BlobResourceContents_RoundTrips()
    {
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8];
        ResourceContents original = BlobResourceContents.FromBytes(
            "file:///data.bin", data, "application/octet-stream");

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<ResourceContents>(json, Options);

        var result = Assert.IsType<BlobResourceContents>(deserialized);
        Assert.Equal("file:///data.bin", result.Uri);
        Assert.Equal("application/octet-stream", result.MimeType);
        Assert.Equal(data, result.ToBytes());
    }

    [Fact]
    public void TextResourceContents_WithoutMimeType()
    {
        ResourceContents original = new TextResourceContents
        {
            Uri = "file:///test.txt",
            Text = "content"
        };

        var json = JsonSerializer.Serialize(original, Options);
        Assert.DoesNotContain("mimeType", json);

        var deserialized = Assert.IsType<TextResourceContents>(
            JsonSerializer.Deserialize<ResourceContents>(json, Options));
        Assert.Null(deserialized.MimeType);
    }

    [Fact]
    public void BlobResourceContents_WithoutMimeType()
    {
        ResourceContents original = new BlobResourceContents
        {
            Uri = "file:///data.bin",
            Blob = Convert.ToBase64String([1, 2, 3])
        };

        var json = JsonSerializer.Serialize(original, Options);
        Assert.DoesNotContain("mimeType", json);

        var deserialized = Assert.IsType<BlobResourceContents>(
            JsonSerializer.Deserialize<ResourceContents>(json, Options));
        Assert.Null(deserialized.MimeType);
    }

    [Fact]
    public void Deserialize_WithBothTextAndBlob_Throws()
    {
        var json = """{"uri":"x","text":"hello","blob":"AAAA"}""";
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ResourceContents>(json, Options));
    }

    [Fact]
    public void Deserialize_WithNeitherTextNorBlob_Throws()
    {
        var json = """{"uri":"x","mimeType":"text/plain"}""";
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ResourceContents>(json, Options));
    }

    [Fact]
    public void BlobResourceContents_FromBytes_Factory()
    {
        byte[] data = [0xFF, 0xD8, 0xFF, 0xE0]; // JPEG header
        var contents = BlobResourceContents.FromBytes("file:///photo.jpg", data, "image/jpeg");

        Assert.Equal("file:///photo.jpg", contents.Uri);
        Assert.Equal("image/jpeg", contents.MimeType);
        Assert.Equal(data, contents.ToBytes());
    }

    [Fact]
    public void TextResourceContents_LargeText_RoundTrips()
    {
        var largeText = new string('A', 1_000_000);
        ResourceContents original = new TextResourceContents
        {
            Uri = "file:///large.txt",
            Text = largeText
        };

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = Assert.IsType<TextResourceContents>(
            JsonSerializer.Deserialize<ResourceContents>(json, Options));
        Assert.Equal(1_000_000, deserialized.Text.Length);
    }

    [Fact]
    public void TextResourceContents_UnicodeContent()
    {
        ResourceContents original = new TextResourceContents
        {
            Uri = "file:///unicode.txt",
            Text = "日本語テスト 🎉 café"
        };

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = Assert.IsType<TextResourceContents>(
            JsonSerializer.Deserialize<ResourceContents>(json, Options));
        Assert.Equal("日本語テスト 🎉 café", deserialized.Text);
    }

    [Fact]
    public void BlobResourceContents_LargeBlob_RoundTrips()
    {
        var largeData = new byte[5 * 1024 * 1024]; // 5MB
        Random.Shared.NextBytes(largeData);

        ResourceContents original = BlobResourceContents.FromBytes(
            "file:///large.bin", largeData, "application/octet-stream");

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = Assert.IsType<BlobResourceContents>(
            JsonSerializer.Deserialize<ResourceContents>(json, Options));
        Assert.Equal(largeData, deserialized.ToBytes());
    }
}
