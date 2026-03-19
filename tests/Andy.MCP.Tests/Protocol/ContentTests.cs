using System.Text.Json;
using Andy.MCP.Protocol;

namespace Andy.MCP.Tests.Protocol;

public class ContentTests
{
    private static readonly JsonSerializerOptions Options = McpJsonDefaults.Options;

    #region TextContent

    [Fact]
    public void TextContent_RoundTrips()
    {
        Content original = new TextContent { Text = "Hello, world!" };
        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<Content>(json, Options);

        var result = Assert.IsType<TextContent>(deserialized);
        Assert.Equal("Hello, world!", result.Text);
    }

    [Fact]
    public void TextContent_WithAnnotations_RoundTrips()
    {
        Content original = new TextContent
        {
            Text = "Important message",
            Annotations = new Annotations
            {
                Audience = [Role.User],
                Priority = 0.9,
                LastModified = "2025-01-15T10:30:00Z"
            }
        };

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = Assert.IsType<TextContent>(JsonSerializer.Deserialize<Content>(json, Options));

        Assert.Equal("Important message", deserialized.Text);
        Assert.NotNull(deserialized.Annotations);
        Assert.Single(deserialized.Annotations!.Audience!);
        Assert.Equal(Role.User, deserialized.Annotations.Audience![0]);
        Assert.Equal(0.9, deserialized.Annotations.Priority);
        Assert.Equal("2025-01-15T10:30:00Z", deserialized.Annotations.LastModified);
    }

    [Fact]
    public void TextContent_JsonContainsTypeDiscriminator()
    {
        Content content = new TextContent { Text = "test" };
        var json = JsonSerializer.Serialize(content, Options);
        Assert.Contains("\"type\":\"text\"", json);
    }

    #endregion

    #region ImageContent

    [Fact]
    public void ImageContent_RoundTrips()
    {
        Content original = new ImageContent
        {
            Data = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47]),
            MimeType = "image/png"
        };

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = Assert.IsType<ImageContent>(JsonSerializer.Deserialize<Content>(json, Options));

        Assert.Equal("image/png", deserialized.MimeType);
        Assert.NotEmpty(deserialized.Data);
    }

    [Fact]
    public void ImageContent_FromBytes_EncodesCorrectly()
    {
        byte[] pngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var content = ImageContent.FromBytes(pngHeader, "image/png");

        Assert.Equal("image/png", content.MimeType);
        Assert.Equal(pngHeader, content.ToBytes());
    }

    [Fact]
    public void ImageContent_ToBytes_DecodesCorrectly()
    {
        byte[] original = [1, 2, 3, 4, 5];
        var content = ImageContent.FromBytes(original, "image/jpeg");
        var decoded = content.ToBytes();

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void ImageContent_LargePayload_RoundTrips()
    {
        var largeImage = new byte[5 * 1024 * 1024]; // 5MB
        Random.Shared.NextBytes(largeImage);

        var content = ImageContent.FromBytes(largeImage, "image/png");
        Content boxed = content;
        var json = JsonSerializer.Serialize(boxed, Options);
        var deserialized = Assert.IsType<ImageContent>(JsonSerializer.Deserialize<Content>(json, Options));

        Assert.Equal(largeImage, deserialized.ToBytes());
    }

    [Fact]
    public void ImageContent_JsonContainsTypeDiscriminator()
    {
        Content content = new ImageContent { Data = "AAAA", MimeType = "image/png" };
        var json = JsonSerializer.Serialize(content, Options);
        Assert.Contains("\"type\":\"image\"", json);
    }

    #endregion

    #region AudioContent

    [Fact]
    public void AudioContent_RoundTrips()
    {
        byte[] wavData = [0x52, 0x49, 0x46, 0x46]; // RIFF header
        Content original = AudioContent.FromBytes(wavData, "audio/wav");

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = Assert.IsType<AudioContent>(JsonSerializer.Deserialize<Content>(json, Options));

        Assert.Equal("audio/wav", deserialized.MimeType);
        Assert.Equal(wavData, deserialized.ToBytes());
    }

    [Fact]
    public void AudioContent_JsonContainsTypeDiscriminator()
    {
        Content content = new AudioContent { Data = "AAAA", MimeType = "audio/mp3" };
        var json = JsonSerializer.Serialize(content, Options);
        Assert.Contains("\"type\":\"audio\"", json);
    }

    #endregion

    #region ResourceLink

    [Fact]
    public void ResourceLink_WithAllFields_RoundTrips()
    {
        Content original = new ResourceLink
        {
            Uri = "https://example.com/doc.pdf",
            Name = "Documentation",
            Description = "API reference documentation",
            MimeType = "application/pdf",
            Annotations = new Annotations { Priority = 0.5 }
        };

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = Assert.IsType<ResourceLink>(JsonSerializer.Deserialize<Content>(json, Options));

        Assert.Equal("https://example.com/doc.pdf", deserialized.Uri);
        Assert.Equal("Documentation", deserialized.Name);
        Assert.Equal("API reference documentation", deserialized.Description);
        Assert.Equal("application/pdf", deserialized.MimeType);
        Assert.Equal(0.5, deserialized.Annotations?.Priority);
    }

    [Fact]
    public void ResourceLink_WithMinimalFields_RoundTrips()
    {
        Content original = new ResourceLink { Uri = "file:///path", Name = "file" };

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = Assert.IsType<ResourceLink>(JsonSerializer.Deserialize<Content>(json, Options));

        Assert.Equal("file:///path", deserialized.Uri);
        Assert.Equal("file", deserialized.Name);
        Assert.Null(deserialized.Description);
        Assert.Null(deserialized.MimeType);
        Assert.Null(deserialized.Annotations);
    }

    [Fact]
    public void ResourceLink_OptionalFieldsOmittedInJson()
    {
        Content content = new ResourceLink { Uri = "x", Name = "y" };
        var json = JsonSerializer.Serialize(content, Options);

        Assert.DoesNotContain("description", json);
        Assert.DoesNotContain("mimeType", json);
        Assert.DoesNotContain("annotations", json);
    }

    [Fact]
    public void ResourceLink_JsonContainsTypeDiscriminator()
    {
        Content content = new ResourceLink { Uri = "x", Name = "y" };
        var json = JsonSerializer.Serialize(content, Options);
        Assert.Contains("\"type\":\"resource_link\"", json);
    }

    #endregion

    #region EmbeddedResource

    [Fact]
    public void EmbeddedResource_WithTextContents_RoundTrips()
    {
        Content original = new EmbeddedResource
        {
            Resource = new TextResourceContents
            {
                Uri = "file:///readme.md",
                MimeType = "text/markdown",
                Text = "# Hello"
            }
        };

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = Assert.IsType<EmbeddedResource>(JsonSerializer.Deserialize<Content>(json, Options));
        var resource = Assert.IsType<TextResourceContents>(deserialized.Resource);

        Assert.Equal("file:///readme.md", resource.Uri);
        Assert.Equal("text/markdown", resource.MimeType);
        Assert.Equal("# Hello", resource.Text);
    }

    [Fact]
    public void EmbeddedResource_WithBlobContents_RoundTrips()
    {
        byte[] data = [10, 20, 30, 40, 50];
        Content original = new EmbeddedResource
        {
            Resource = BlobResourceContents.FromBytes("file:///image.bin", data, "application/octet-stream")
        };

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = Assert.IsType<EmbeddedResource>(JsonSerializer.Deserialize<Content>(json, Options));
        var resource = Assert.IsType<BlobResourceContents>(deserialized.Resource);

        Assert.Equal("file:///image.bin", resource.Uri);
        Assert.Equal("application/octet-stream", resource.MimeType);
        Assert.Equal(data, resource.ToBytes());
    }

    [Fact]
    public void EmbeddedResource_JsonContainsTypeDiscriminator()
    {
        Content content = new EmbeddedResource
        {
            Resource = new TextResourceContents { Uri = "x", Text = "y" }
        };
        var json = JsonSerializer.Serialize(content, Options);
        Assert.Contains("\"type\":\"resource\"", json);
    }

    #endregion

    #region Polymorphic Deserialization of Mixed Arrays

    [Fact]
    public void MixedContentArray_DeserializesCorrectTypes()
    {
        var array = new Content[]
        {
            new TextContent { Text = "hello" },
            new ImageContent { Data = "AAAA", MimeType = "image/png" },
            new AudioContent { Data = "BBBB", MimeType = "audio/wav" },
            new ResourceLink { Uri = "https://example.com", Name = "link" },
            new EmbeddedResource { Resource = new TextResourceContents { Uri = "x", Text = "y" } }
        };

        var json = JsonSerializer.Serialize(array, Options);
        var deserialized = JsonSerializer.Deserialize<Content[]>(json, Options)!;

        Assert.Equal(5, deserialized.Length);
        Assert.IsType<TextContent>(deserialized[0]);
        Assert.IsType<ImageContent>(deserialized[1]);
        Assert.IsType<AudioContent>(deserialized[2]);
        Assert.IsType<ResourceLink>(deserialized[3]);
        Assert.IsType<EmbeddedResource>(deserialized[4]);
    }

    #endregion

    #region Invalid Base64

    [Fact]
    public void ImageContent_InvalidBase64_ThrowsOnDecode()
    {
        var content = new ImageContent { Data = "not-valid-base64!!!", MimeType = "image/png" };
        Assert.Throws<FormatException>(() => content.ToBytes());
    }

    #endregion
}
