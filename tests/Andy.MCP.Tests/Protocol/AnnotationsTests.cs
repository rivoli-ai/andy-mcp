using System.Text.Json;
using Andy.MCP.Protocol;

namespace Andy.MCP.Tests.Protocol;

public class AnnotationsTests
{
    private static readonly JsonSerializerOptions Options = McpJsonDefaults.Options;

    [Fact]
    public void Annotations_AllFields_RoundTrips()
    {
        var annotations = new Annotations
        {
            Audience = [Role.User, Role.Assistant],
            Priority = 0.75,
            LastModified = "2025-06-01T12:00:00Z"
        };

        var json = JsonSerializer.Serialize(annotations, Options);
        var deserialized = JsonSerializer.Deserialize<Annotations>(json, Options)!;

        Assert.Equal(2, deserialized.Audience!.Count);
        Assert.Equal(Role.User, deserialized.Audience[0]);
        Assert.Equal(Role.Assistant, deserialized.Audience[1]);
        Assert.Equal(0.75, deserialized.Priority);
        Assert.Equal("2025-06-01T12:00:00Z", deserialized.LastModified);
    }

    [Fact]
    public void Annotations_NoFields_EmptyJson()
    {
        var annotations = new Annotations();
        var json = JsonSerializer.Serialize(annotations, Options);

        Assert.Equal("{}", json);
    }

    [Fact]
    public void Annotations_PriorityOnly()
    {
        var annotations = new Annotations { Priority = 0.0 };
        var json = JsonSerializer.Serialize(annotations, Options);
        var deserialized = JsonSerializer.Deserialize<Annotations>(json, Options)!;

        Assert.Equal(0.0, deserialized.Priority);
        Assert.Null(deserialized.Audience);
        Assert.Null(deserialized.LastModified);
    }

    [Fact]
    public void Annotations_PriorityMinimum()
    {
        var annotations = new Annotations { Priority = 0.0 };
        var json = JsonSerializer.Serialize(annotations, Options);
        Assert.Contains("0", json);
    }

    [Fact]
    public void Annotations_PriorityMaximum()
    {
        var annotations = new Annotations { Priority = 1.0 };
        var json = JsonSerializer.Serialize(annotations, Options);
        Assert.Contains("1", json);
    }

    [Fact]
    public void Annotations_AudienceUserOnly()
    {
        var annotations = new Annotations { Audience = [Role.User] };
        var json = JsonSerializer.Serialize(annotations, Options);

        Assert.Contains("\"user\"", json);
        Assert.DoesNotContain("\"assistant\"", json);
    }

    [Fact]
    public void Annotations_AudienceAssistantOnly()
    {
        var annotations = new Annotations { Audience = [Role.Assistant] };
        var json = JsonSerializer.Serialize(annotations, Options);

        Assert.Contains("\"assistant\"", json);
        Assert.DoesNotContain("\"user\"", json);
    }

    [Fact]
    public void Annotations_EmptyAudience()
    {
        var annotations = new Annotations { Audience = [] };
        var json = JsonSerializer.Serialize(annotations, Options);
        var deserialized = JsonSerializer.Deserialize<Annotations>(json, Options)!;

        Assert.NotNull(deserialized.Audience);
        Assert.Empty(deserialized.Audience);
    }

    [Fact]
    public void Role_SerializesAsLowercaseString()
    {
        var json = JsonSerializer.Serialize(Role.User, Options);
        Assert.Equal("\"user\"", json);

        json = JsonSerializer.Serialize(Role.Assistant, Options);
        Assert.Equal("\"assistant\"", json);
    }

    [Fact]
    public void Role_DeserializesFromLowercaseString()
    {
        Assert.Equal(Role.User, JsonSerializer.Deserialize<Role>("\"user\"", Options));
        Assert.Equal(Role.Assistant, JsonSerializer.Deserialize<Role>("\"assistant\"", Options));
    }

    [Fact]
    public void Annotations_LastModified_WithTimezone()
    {
        var annotations = new Annotations { LastModified = "2025-01-15T10:30:00+05:00" };
        var json = JsonSerializer.Serialize(annotations, Options);
        var deserialized = JsonSerializer.Deserialize<Annotations>(json, Options)!;
        Assert.Equal("2025-01-15T10:30:00+05:00", deserialized.LastModified);
    }

    [Fact]
    public void Annotations_LastModified_WithoutTimezone()
    {
        var annotations = new Annotations { LastModified = "2025-01-15T10:30:00" };
        var json = JsonSerializer.Serialize(annotations, Options);
        var deserialized = JsonSerializer.Deserialize<Annotations>(json, Options)!;
        Assert.Equal("2025-01-15T10:30:00", deserialized.LastModified);
    }
}
