using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;

namespace Andy.MCP.Tests;

/// <summary>
/// Tests for MCP 2025-11-25 elicitation additions (issue #41): URL-mode requests, typed
/// primitive schema definitions, and enum/default support.
/// </summary>
public class ElicitationSchemaTests
{
    private static readonly JsonSerializerOptions Options = McpJsonDefaults.Options;

    [Fact]
    public void UrlModeRequest_RoundTrips_WithoutRequestedSchema()
    {
        var request = ElicitRequest.ForUrl("Authorize access", "elicit-123", "https://example.com/authorize");

        var json = JsonSerializer.Serialize(request, Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("url", root.GetProperty("mode").GetString());
        Assert.Equal("elicit-123", root.GetProperty("elicitationId").GetString());
        Assert.Equal("https://example.com/authorize", root.GetProperty("url").GetString());
        Assert.False(root.TryGetProperty("requestedSchema", out _));

        var back = JsonSerializer.Deserialize<ElicitRequest>(json, Options)!;
        Assert.True(back.IsUrlMode);
        Assert.Equal("elicit-123", back.ElicitationId);
    }

    [Fact]
    public void FormRequest_FromTypedSchema_HasRequestedSchema()
    {
        var schema = new ElicitationSchema
        {
            Properties = new Dictionary<string, PrimitiveSchemaDefinition>
            {
                ["name"] = PrimitiveSchemaDefinition.StringField(title: "Name", maxLength: 50),
            },
            Required = new[] { "name" }
        };

        var request = ElicitRequest.Form("Enter your name", schema);

        Assert.Equal("form", request.Mode);
        Assert.False(request.IsUrlMode);
        Assert.NotNull(request.RequestedSchema);
        Assert.Equal("object", request.RequestedSchema!.Value.GetProperty("type").GetString());
        Assert.Equal("Name",
            request.RequestedSchema.Value.GetProperty("properties").GetProperty("name").GetProperty("title").GetString());
    }

    [Fact]
    public void ElicitationSchema_RoundTrips_AllPrimitiveKinds()
    {
        var schema = new ElicitationSchema
        {
            Properties = new Dictionary<string, PrimitiveSchemaDefinition>
            {
                ["fullName"] = PrimitiveSchemaDefinition.StringField(format: "email", @default: "a@b.com"),
                ["age"] = PrimitiveSchemaDefinition.NumberField(integer: true, minimum: 0, maximum: 120, @default: 30),
                ["subscribe"] = PrimitiveSchemaDefinition.BooleanField(@default: true),
                ["tier"] = PrimitiveSchemaDefinition.EnumField(new[] { "free", "pro" }, @default: "free"),
            },
            Required = new[] { "fullName" }
        };

        var json = JsonSerializer.Serialize(schema, Options);
        var back = JsonSerializer.Deserialize<ElicitationSchema>(json, Options)!;

        Assert.Equal("object", back.Type);
        Assert.Equal(4, back.Properties.Count);

        Assert.Equal("email", back.Properties["fullName"].Format);
        Assert.Equal("a@b.com", back.Properties["fullName"].Default!.Value.GetString());

        Assert.Equal("integer", back.Properties["age"].Type);
        Assert.Equal(30, back.Properties["age"].Default!.Value.GetInt32());
        Assert.Equal(0, back.Properties["age"].Minimum);

        Assert.Equal("boolean", back.Properties["subscribe"].Type);
        Assert.True(back.Properties["subscribe"].Default!.Value.GetBoolean());

        Assert.Equal(new[] { "free", "pro" }, back.Properties["tier"].Enum);
        Assert.Equal("free", back.Properties["tier"].Default!.Value.GetString());
        Assert.Equal(new[] { "fullName" }, back.Required);
    }

    [Fact]
    public void EnumField_EmitsEnumAndDefault()
    {
        var field = PrimitiveSchemaDefinition.EnumField(new[] { "red", "green", "blue" }, @default: "green");

        var json = JsonSerializer.Serialize(field, Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("string", root.GetProperty("type").GetString());
        Assert.Equal(3, root.GetProperty("enum").GetArrayLength());
        Assert.Equal("green", root.GetProperty("default").GetString());
    }

    [Fact]
    public void TitledEnumField_EmitsOneOfWithConstAndTitle()
    {
        var field = PrimitiveSchemaDefinition.TitledEnumField(
            new[] { new EnumOption("free", "Free tier"), new EnumOption("pro", "Pro tier") },
            @default: "free");

        var json = JsonSerializer.Serialize(field, Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("string", root.GetProperty("type").GetString());
        var oneOf = root.GetProperty("oneOf");
        Assert.Equal(2, oneOf.GetArrayLength());
        Assert.Equal("free", oneOf[0].GetProperty("const").GetString());
        Assert.Equal("Free tier", oneOf[0].GetProperty("title").GetString());
        Assert.Equal("free", root.GetProperty("default").GetString());
        Assert.False(root.TryGetProperty("enum", out _));

        var back = JsonSerializer.Deserialize<PrimitiveSchemaDefinition>(json, Options)!;
        Assert.Equal("pro", back.OneOf![1].Const);
        Assert.Equal("Pro tier", back.OneOf[1].Title);
    }

    [Fact]
    public void MultiSelectEnumField_EmitsArrayWithItemsEnum()
    {
        var field = PrimitiveSchemaDefinition.MultiSelectEnumField(
            new[] { "a", "b", "c" }, minItems: 1, maxItems: 2, @default: new[] { "a" });

        var json = JsonSerializer.Serialize(field, Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("array", root.GetProperty("type").GetString());
        Assert.Equal(1, root.GetProperty("minItems").GetInt32());
        Assert.Equal(2, root.GetProperty("maxItems").GetInt32());
        var itemsEnum = root.GetProperty("items").GetProperty("enum");
        Assert.Equal(3, itemsEnum.GetArrayLength());
        Assert.Equal("string", root.GetProperty("items").GetProperty("type").GetString());
        Assert.Equal("a", root.GetProperty("default")[0].GetString());

        var back = JsonSerializer.Deserialize<PrimitiveSchemaDefinition>(json, Options)!;
        Assert.Equal("array", back.Type);
        Assert.Equal(1, back.MinItems);
    }

    [Fact]
    public void EnumSchema_InFullSchema_RoundTrips()
    {
        var schema = new ElicitationSchema
        {
            Properties = new Dictionary<string, PrimitiveSchemaDefinition>
            {
                ["tier"] = PrimitiveSchemaDefinition.TitledEnumField(
                    new[] { new EnumOption("free", "Free"), new EnumOption("pro", "Pro") }),
                ["tags"] = PrimitiveSchemaDefinition.MultiSelectEnumField(new[] { "x", "y" }),
            }
        };

        var back = JsonSerializer.Deserialize<ElicitationSchema>(
            JsonSerializer.Serialize(schema, Options), Options)!;

        Assert.Equal(2, back.Properties["tier"].OneOf!.Count);
        Assert.Equal("array", back.Properties["tags"].Type);
    }

    [Fact]
    public void Implementation_2025_11_25_Fields_RoundTrip()
    {
        var impl = new Implementation("MyServer", "1.2.3")
        {
            Title = "My Server",
            Description = "Provides widgets.",
            WebsiteUrl = "https://example.com",
            Icons = new[] { new Icon { Source = "https://example.com/icon.png", MimeType = "image/png" } }
        };

        var json = JsonSerializer.Serialize(impl, Options);
        var back = JsonSerializer.Deserialize<Implementation>(json, Options)!;

        Assert.Equal("Provides widgets.", back.Description);
        Assert.Equal("https://example.com", back.WebsiteUrl);
        Assert.Equal("https://example.com/icon.png", Assert.Single(back.Icons!).Source);
    }
}
