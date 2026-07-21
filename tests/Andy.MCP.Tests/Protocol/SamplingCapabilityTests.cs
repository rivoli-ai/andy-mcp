using System.Text.Json;
using Andy.MCP.Protocol;

namespace Andy.MCP.Tests;

/// <summary>
/// Tests for the MCP 2025-11-25 sampling client capability (issue #41): the sampling capability
/// carries optional <c>context</c> and <c>tools</c> sub-capabilities.
/// </summary>
public class SamplingCapabilityTests
{
    private static readonly JsonSerializerOptions Options = McpJsonDefaults.Options;

    [Fact]
    public void EmptySamplingCapability_SerializesAsEmptyObject()
    {
        var caps = new ClientCapabilities { Sampling = new SamplingCapability() };
        var json = JsonSerializer.Serialize(caps, Options);
        Assert.Contains("\"sampling\":{}", json);
    }

    [Fact]
    public void SamplingToolsAndContext_RoundTrip()
    {
        var caps = new ClientCapabilities
        {
            Sampling = new SamplingCapability
            {
                Context = new EmptyCapability(),
                Tools = new EmptyCapability()
            }
        };

        var json = JsonSerializer.Serialize(caps, Options);
        var back = JsonSerializer.Deserialize<ClientCapabilities>(json, Options)!;

        Assert.NotNull(back.Sampling);
        Assert.NotNull(back.Sampling!.Context);
        Assert.NotNull(back.Sampling.Tools);
    }

    [Fact]
    public void SamplingTools_DroppedForOlderRevision()
    {
        var caps = new ClientCapabilities
        {
            Sampling = new SamplingCapability { Tools = new EmptyCapability() }
        };

        var older = RevisionAwareJson.ToElementForRevision(caps, ProtocolRevision.V2025_06_18);
        var sampling = older.GetProperty("sampling");
        Assert.False(sampling.TryGetProperty("tools", out _));

        var latest = RevisionAwareJson.ToElementForRevision(caps, ProtocolRevision.V2025_11_25);
        Assert.True(latest.GetProperty("sampling").TryGetProperty("tools", out _));
    }
}
