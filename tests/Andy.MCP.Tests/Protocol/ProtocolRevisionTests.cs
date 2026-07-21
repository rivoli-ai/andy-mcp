using Andy.MCP.Protocol;

namespace Andy.MCP.Tests;

/// <summary>
/// Tests for the protocol revision strategy (issue #41): 2025-11-25 is the latest supported
/// revision, and per-revision feature availability is modeled explicitly so revision-specific
/// wire behavior can be gated rather than inferred from version-string comparisons.
/// </summary>
public class ProtocolRevisionTests
{
    [Fact]
    public void Latest_Is_2025_11_25()
    {
        Assert.Equal("2025-11-25", ProtocolRevision.Latest.Version);
        Assert.Equal("2025-11-25", McpSession.LatestProtocolVersion);
    }

    [Fact]
    public void All_ListsSupportedRevisions_NewestFirst()
    {
        Assert.Equal(
            new[] { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" },
            ProtocolRevision.AllVersions);

        // Ordinals must be strictly decreasing in the newest-first list.
        for (int i = 1; i < ProtocolRevision.All.Count; i++)
            Assert.True(ProtocolRevision.All[i - 1].Ordinal > ProtocolRevision.All[i].Ordinal);
    }

    [Fact]
    public void SessionSupportedVersions_MatchRevisions()
    {
        Assert.Equal(ProtocolRevision.AllVersions, McpSession.SupportedProtocolVersions);
    }

    [Theory]
    [InlineData("2025-11-25")]
    [InlineData("2025-06-18")]
    [InlineData("2025-03-26")]
    [InlineData("2024-11-05")]
    public void TryGet_ResolvesSupportedRevisions(string version)
    {
        var revision = ProtocolRevision.TryGet(version);
        Assert.NotNull(revision);
        Assert.Equal(version, revision!.Version);
    }

    [Theory]
    [InlineData("2099-01-01")]
    [InlineData("1.0")]
    [InlineData("")]
    [InlineData(null)]
    public void TryGet_ReturnsNull_ForUnsupported(string? version)
    {
        Assert.Null(ProtocolRevision.TryGet(version));
    }

    [Fact]
    public void AtLeast_ComparesByOrdinal()
    {
        Assert.True(ProtocolRevision.V2025_11_25.AtLeast(ProtocolRevision.V2025_06_18));
        Assert.True(ProtocolRevision.V2025_06_18.AtLeast(ProtocolRevision.V2025_06_18));
        Assert.False(ProtocolRevision.V2025_03_26.AtLeast(ProtocolRevision.V2025_06_18));
    }

    [Fact]
    public void FeatureFlags_2025_11_25_HasAllAdditions()
    {
        var r = ProtocolRevision.V2025_11_25;
        Assert.True(r.SupportsIcons);
        Assert.True(r.SupportsElicitationUrlMode);
        Assert.True(r.SupportsElicitationEnumSchema);
        Assert.True(r.SupportsSchemaDefaultValues);
        Assert.True(r.SupportsSamplingToolCalling);
        Assert.True(r.SupportsExperimentalTasks);
        Assert.True(r.SupportsImplementationDescription);
        // Inherited from earlier revisions.
        Assert.True(r.SupportsElicitation);
        Assert.True(r.SupportsStructuredToolOutput);
        Assert.True(r.SupportsResourceLinks);
        Assert.True(r.SupportsAudioContent);
        Assert.True(r.SupportsToolAnnotations);
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", r.DefaultSchemaDialect);
    }

    [Fact]
    public void FeatureFlags_2025_06_18_ExcludesNewerAdditions()
    {
        var r = ProtocolRevision.V2025_06_18;
        Assert.False(r.SupportsIcons);
        Assert.False(r.SupportsElicitationUrlMode);
        Assert.False(r.SupportsSamplingToolCalling);
        Assert.False(r.SupportsExperimentalTasks);
        // But has its own additions.
        Assert.True(r.SupportsElicitation);
        Assert.True(r.SupportsStructuredToolOutput);
        Assert.True(r.SupportsResourceLinks);
    }

    [Fact]
    public void FeatureFlags_2025_03_26_ExcludesElicitation()
    {
        var r = ProtocolRevision.V2025_03_26;
        Assert.True(r.SupportsAudioContent);
        Assert.True(r.SupportsToolAnnotations);
        Assert.False(r.SupportsElicitation);
        Assert.False(r.SupportsStructuredToolOutput);
        Assert.False(r.SupportsIcons);
    }

    [Fact]
    public void FeatureFlags_2024_11_05_HasNoLaterAdditions()
    {
        var r = ProtocolRevision.V2024_11_05;
        Assert.False(r.SupportsAudioContent);
        Assert.False(r.SupportsToolAnnotations);
        Assert.False(r.SupportsElicitation);
        Assert.False(r.SupportsIcons);
    }

    [Fact]
    public void Negotiate_2025_11_25_IsHonored()
    {
        Assert.Equal("2025-11-25", McpSession.NegotiateVersion("2025-11-25"));
        Assert.True(McpSession.IsVersionAcceptable("2025-11-25"));
    }

    [Fact]
    public void Session_Revision_ResolvesNegotiatedVersion()
    {
        var session = new McpSession();
        session.TryTransition(McpSessionState.Initializing);
        session.CompleteInitializationAsServer(
            new InitializeParams
            {
                ProtocolVersion = "2025-11-25",
                Capabilities = new ClientCapabilities(),
                ClientInfo = new Implementation("c", "1.0.0")
            },
            "2025-11-25");

        Assert.Same(ProtocolRevision.V2025_11_25, session.Revision);
    }

    [Fact]
    public void Session_Revision_Null_BeforeInitialization()
    {
        Assert.Null(new McpSession().Revision);
    }
}
