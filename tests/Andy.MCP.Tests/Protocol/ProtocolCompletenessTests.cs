using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;

namespace Andy.MCP.Tests.Protocol;

public class ToolUseContentTests
{
    [Fact]
    public void ToolUseContent_RoundTrips()
    {
        Content original = new ToolUseContent
        {
            Id = "call_123",
            Name = "get_weather",
            Input = McpJsonDefaults.ToElement(new { city = "Paris" })
        };

        var json = JsonSerializer.Serialize(original, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<Content>(json, McpJsonDefaults.Options);

        var result = Assert.IsType<ToolUseContent>(deserialized);
        Assert.Equal("call_123", result.Id);
        Assert.Equal("get_weather", result.Name);
        Assert.Equal("Paris", result.Input.GetProperty("city").GetString());
    }

    [Fact]
    public void ToolUseContent_TypeDiscriminator()
    {
        Content content = new ToolUseContent
        {
            Id = "x",
            Name = "test",
            Input = McpJsonDefaults.ToElement(new { })
        };
        var json = JsonSerializer.Serialize(content, McpJsonDefaults.Options);
        Assert.Contains("\"type\":\"tool_use\"", json);
    }
}

public class ToolResultContentTests
{
    [Fact]
    public void ToolResultContent_RoundTrips()
    {
        Content original = new ToolResultContent
        {
            ToolUseId = "call_123",
            Content = [new TextContent { Text = "Sunny, 22C" }]
        };

        var json = JsonSerializer.Serialize(original, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<Content>(json, McpJsonDefaults.Options);

        var result = Assert.IsType<ToolResultContent>(deserialized);
        Assert.Equal("call_123", result.ToolUseId);
        Assert.Single(result.Content!);
        Assert.IsType<TextContent>(result.Content![0]);
    }

    [Fact]
    public void ToolResultContent_WithError()
    {
        Content original = new ToolResultContent
        {
            ToolUseId = "call_456",
            Content = [new TextContent { Text = "Tool failed" }],
            IsError = true
        };

        var json = JsonSerializer.Serialize(original, McpJsonDefaults.Options);
        var deserialized = Assert.IsType<ToolResultContent>(
            JsonSerializer.Deserialize<Content>(json, McpJsonDefaults.Options));
        Assert.True(deserialized.IsError);
    }

    [Fact]
    public void ToolResultContent_TypeDiscriminator()
    {
        Content content = new ToolResultContent { ToolUseId = "x" };
        var json = JsonSerializer.Serialize(content, McpJsonDefaults.Options);
        Assert.Contains("\"type\":\"tool_result\"", json);
    }
}

public class IconTests
{
    [Fact]
    public void Icon_RoundTrips()
    {
        var icon = new Icon
        {
            Source = "https://example.com/icon.svg",
            MimeType = "image/svg+xml",
            Sizes = ["48x48", "96x96"],
            Theme = "dark"
        };

        var json = JsonSerializer.Serialize(icon, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<Icon>(json, McpJsonDefaults.Options)!;

        Assert.Equal("https://example.com/icon.svg", deserialized.Source);
        Assert.Equal("image/svg+xml", deserialized.MimeType);
        Assert.Equal(2, deserialized.Sizes!.Count);
        Assert.Equal("dark", deserialized.Theme);
    }

    [Fact]
    public void Icon_MinimalFields()
    {
        var icon = new Icon { Source = "icon.png" };
        var json = JsonSerializer.Serialize(icon, McpJsonDefaults.Options);

        Assert.DoesNotContain("mimeType", json);
        Assert.DoesNotContain("sizes", json);
        Assert.DoesNotContain("theme", json);
    }

    [Fact]
    public void Tool_WithIcons()
    {
        var tool = new Tool
        {
            Name = "test",
            InputSchema = McpJsonDefaults.ToElement(new { type = "object" }),
            Icons = [new Icon { Source = "tool.png", MimeType = "image/png" }]
        };

        var json = JsonSerializer.Serialize(tool, McpJsonDefaults.Options);
        Assert.Contains("\"icons\"", json);

        var deserialized = JsonSerializer.Deserialize<Tool>(json, McpJsonDefaults.Options)!;
        Assert.Single(deserialized.Icons!);
        Assert.Equal("tool.png", deserialized.Icons![0].Source);
    }
}

public class MetaFieldTests
{
    [Fact]
    public void Content_Meta_RoundTrips()
    {
        Content content = new TextContent
        {
            Text = "hello",
            Meta = McpJsonDefaults.ToElement(new { custom = "value" })
        };

        var json = JsonSerializer.Serialize(content, McpJsonDefaults.Options);
        var deserialized = Assert.IsType<TextContent>(
            JsonSerializer.Deserialize<Content>(json, McpJsonDefaults.Options));
        Assert.Equal("value", deserialized.Meta!.Value.GetProperty("custom").GetString());
    }

    [Fact]
    public void Tool_Meta_RoundTrips()
    {
        var tool = new Tool
        {
            Name = "test",
            InputSchema = McpJsonDefaults.ToElement(new { type = "object" }),
            Meta = McpJsonDefaults.ToElement(new { version = 2 })
        };

        var json = JsonSerializer.Serialize(tool, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<Tool>(json, McpJsonDefaults.Options)!;
        Assert.Equal(2, deserialized.Meta!.Value.GetProperty("version").GetInt32());
    }

    [Fact]
    public void Resource_Meta_RoundTrips()
    {
        var resource = new Resource
        {
            Uri = "file:///test",
            Name = "test",
            Meta = McpJsonDefaults.ToElement(new { source = "db" })
        };

        var json = JsonSerializer.Serialize(resource, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<Resource>(json, McpJsonDefaults.Options)!;
        Assert.Equal("db", deserialized.Meta!.Value.GetProperty("source").GetString());
    }

    [Fact]
    public void Prompt_Meta_RoundTrips()
    {
        var prompt = new Prompt
        {
            Name = "test",
            Meta = McpJsonDefaults.ToElement(new { category = "coding" })
        };

        var json = JsonSerializer.Serialize(prompt, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<Prompt>(json, McpJsonDefaults.Options)!;
        Assert.Equal("coding", deserialized.Meta!.Value.GetProperty("category").GetString());
    }

    [Fact]
    public void ResourceContents_Meta_RoundTrips()
    {
        ResourceContents contents = new TextResourceContents
        {
            Uri = "file:///test",
            Text = "hello",
            Meta = McpJsonDefaults.ToElement(new { encoding = "utf-8" })
        };

        var json = JsonSerializer.Serialize(contents, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<ResourceContents>(json, McpJsonDefaults.Options)!;
        Assert.Equal("utf-8", deserialized.Meta!.Value.GetProperty("encoding").GetString());
    }
}

public class ToolChoiceTests
{
    [Fact]
    public void ToolChoice_Serializes()
    {
        var json = JsonSerializer.Serialize(ToolChoice.Auto, McpJsonDefaults.Options);
        Assert.Contains("\"mode\":\"auto\"", json);

        json = JsonSerializer.Serialize(ToolChoice.Required, McpJsonDefaults.Options);
        Assert.Contains("\"mode\":\"required\"", json);

        json = JsonSerializer.Serialize(ToolChoice.None, McpJsonDefaults.Options);
        Assert.Contains("\"mode\":\"none\"", json);
    }

    [Fact]
    public void ToolChoice_RoundTrips()
    {
        var original = ToolChoice.Required;
        var json = JsonSerializer.Serialize(original, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<ToolChoice>(json, McpJsonDefaults.Options)!;
        Assert.Equal("required", deserialized.Mode);
    }
}

public class SamplingCompletenessTests
{
    [Fact]
    public void CreateMessageRequest_AllFields()
    {
        var req = new CreateMessageRequest
        {
            Messages = [new SamplingMessage
            {
                Role = Role.User,
                Content = [new TextContent { Text = "Hello" }]
            }],
            MaxTokens = 500,
            Temperature = 0.7f,
            StopSequences = ["END"],
            Metadata = McpJsonDefaults.ToElement(new { requestId = "abc" }),
            Tools = [new Tool
            {
                Name = "get_time",
                InputSchema = McpJsonDefaults.ToElement(new { type = "object" })
            }],
            ToolChoice = ToolChoice.Auto,
            SystemPrompt = "Be helpful",
            IncludeContext = "thisServer"
        };

        var json = JsonSerializer.Serialize(req, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<CreateMessageRequest>(json, McpJsonDefaults.Options)!;

        Assert.Equal(500, deserialized.MaxTokens);
        Assert.Equal(0.7f, deserialized.Temperature);
        Assert.Single(deserialized.StopSequences!);
        Assert.NotNull(deserialized.Metadata);
        Assert.Single(deserialized.Tools!);
        Assert.Equal("auto", deserialized.ToolChoice!.Mode);
        Assert.Equal("Be helpful", deserialized.SystemPrompt);
        Assert.Equal("thisServer", deserialized.IncludeContext);
    }

    [Fact]
    public void SamplingMessage_MultiContent()
    {
        var msg = new SamplingMessage
        {
            Role = Role.User,
            Content = [
                new TextContent { Text = "What's in this image?" },
                ImageContent.FromBytes([0xFF, 0xD8], "image/jpeg")
            ]
        };

        var json = JsonSerializer.Serialize(msg, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<SamplingMessage>(json, McpJsonDefaults.Options)!;

        Assert.Equal(2, deserialized.Content.Count);
        Assert.IsType<TextContent>(deserialized.Content[0]);
        Assert.IsType<ImageContent>(deserialized.Content[1]);
    }

    [Fact]
    public void CreateMessageResult_MultiContent()
    {
        var result = new CreateMessageResult
        {
            Role = Role.Assistant,
            Content = [
                new TextContent { Text = "Here's the answer" },
                new TextContent { Text = "With more detail" }
            ],
            Model = "claude-sonnet-4-20250514",
            StopReason = "endTurn"
        };

        var json = JsonSerializer.Serialize(result, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<CreateMessageResult>(json, McpJsonDefaults.Options)!;
        Assert.Equal(2, deserialized.Content.Count);
    }
}

public class ResourceLinkCompletenessTests
{
    [Fact]
    public void ResourceLink_NewFields()
    {
        Content link = new ResourceLink
        {
            Uri = "file:///doc.pdf",
            Name = "Document",
            Title = "Important Document",
            Size = 1024 * 1024,
            Description = "A PDF document",
            MimeType = "application/pdf"
        };

        var json = JsonSerializer.Serialize(link, McpJsonDefaults.Options);
        var deserialized = Assert.IsType<ResourceLink>(
            JsonSerializer.Deserialize<Content>(json, McpJsonDefaults.Options));

        Assert.Equal("Important Document", deserialized.Title);
        Assert.Equal(1024 * 1024, deserialized.Size);
    }
}

public class ToolAnnotationsCompletenessTests
{
    [Fact]
    public void ToolAnnotations_Title()
    {
        var annotations = new ToolAnnotations
        {
            Title = "Read-Only Query Tool",
            ReadOnlyHint = true
        };

        var json = JsonSerializer.Serialize(annotations, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<ToolAnnotations>(json, McpJsonDefaults.Options)!;
        Assert.Equal("Read-Only Query Tool", deserialized.Title);
    }
}

public class PromptArgumentCompletenessTests
{
    [Fact]
    public void PromptArgument_Title()
    {
        var arg = new PromptArgument
        {
            Name = "language",
            Title = "Programming Language",
            Description = "The language to review",
            Required = true
        };

        var json = JsonSerializer.Serialize(arg, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<PromptArgument>(json, McpJsonDefaults.Options)!;
        Assert.Equal("Programming Language", deserialized.Title);
    }
}

public class CapabilitiesExtensionsTests
{
    [Fact]
    public void ClientCapabilities_Extensions()
    {
        var caps = new ClientCapabilities
        {
            Extensions = new Dictionary<string, JsonElement>
            {
                ["com.example.feature"] = McpJsonDefaults.ToElement(new { enabled = true })
            }
        };

        var json = JsonSerializer.Serialize(caps, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<ClientCapabilities>(json, McpJsonDefaults.Options)!;
        Assert.True(deserialized.Extensions!["com.example.feature"].GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void ServerCapabilities_Extensions()
    {
        var caps = new ServerCapabilities
        {
            Extensions = new Dictionary<string, JsonElement>
            {
                ["com.example.tasks"] = McpJsonDefaults.ToElement(new { version = 1 })
            }
        };

        var json = JsonSerializer.Serialize(caps, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<ServerCapabilities>(json, McpJsonDefaults.Options)!;
        Assert.Equal(1, deserialized.Extensions!["com.example.tasks"].GetProperty("version").GetInt32());
    }
}
