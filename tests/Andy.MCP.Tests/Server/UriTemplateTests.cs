using Andy.MCP.Server;

namespace Andy.MCP.Tests.Server;

/// <summary>
/// Tests the RFC 6570 URI template matcher used for resource templates (issue #71).
/// </summary>
public class UriTemplateTests
{
    [Fact]
    public void Matches_SimpleVariables_AndExtracts()
    {
        var template = new UriTemplate("file:///users/{userId}/docs/{docId}");

        Assert.True(template.TryMatch("file:///users/alice/docs/42", out var vars));
        Assert.Equal("alice", vars["userId"]);
        Assert.Equal("42", vars["docId"]);
    }

    [Fact]
    public void SimpleVariable_DoesNotMatchAcrossSlashes()
    {
        var template = new UriTemplate("file:///users/{userId}");
        Assert.False(template.TryMatch("file:///users/alice/extra", out _));
    }

    [Fact]
    public void Missing_Segment_DoesNotMatch()
    {
        var template = new UriTemplate("file:///users/{userId}/docs/{docId}");
        Assert.False(template.TryMatch("file:///users/alice", out _));
    }

    [Fact]
    public void ReservedExpansion_MatchesAcrossSlashes()
    {
        var template = new UriTemplate("file:///{+path}");
        Assert.True(template.TryMatch("file:///a/b/c.txt", out var vars));
        Assert.Equal("a/b/c.txt", vars["path"]);
    }

    [Fact]
    public void ExtractedValues_AreUrlDecoded()
    {
        var template = new UriTemplate("file:///docs/{name}");
        Assert.True(template.TryMatch("file:///docs/my%20file", out var vars));
        Assert.Equal("my file", vars["name"]);
    }

    [Fact]
    public void DeclaresVariables_InOrder()
    {
        var template = new UriTemplate("x://{a}/{b}");
        Assert.Equal(new[] { "a", "b" }, template.Variables);
    }
}
