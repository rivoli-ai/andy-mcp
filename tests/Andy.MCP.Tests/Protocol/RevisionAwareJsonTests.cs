using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests;

/// <summary>
/// Tests for revision-aware serialization (issue #41): fields introduced in a newer protocol
/// revision must not be emitted when serializing for an older negotiated revision.
/// </summary>
public class RevisionAwareJsonTests
{
    private static bool HasProp(JsonElement element, string name) =>
        element.TryGetProperty(name, out _);

    // ---- Mechanism-level tests ----

    [Fact]
    public void Implementation_NewerFields_KeptForLatest_DroppedForOlder()
    {
        var impl = new Implementation("s", "1.0.0")
        {
            Title = "S",
            Description = "desc",
            WebsiteUrl = "https://x",
            Icons = new[] { new Icon { Source = "https://x/i.png" } }
        };

        var latest = RevisionAwareJson.ToElementForRevision(impl, ProtocolRevision.V2025_11_25);
        Assert.True(HasProp(latest, "description"));
        Assert.True(HasProp(latest, "websiteUrl"));
        Assert.True(HasProp(latest, "icons"));

        var older = RevisionAwareJson.ToElementForRevision(impl, ProtocolRevision.V2025_06_18);
        Assert.False(HasProp(older, "description"));
        Assert.False(HasProp(older, "websiteUrl"));
        Assert.False(HasProp(older, "icons"));
        // Baseline fields survive.
        Assert.Equal("s", older.GetProperty("name").GetString());
        Assert.Equal("S", older.GetProperty("title").GetString());
    }

    [Fact]
    public void Tool_Icons_DroppedForOlderRevision()
    {
        var tool = new Tool
        {
            Name = "t",
            Description = "d",
            InputSchema = McpJsonDefaults.ToElement(new { type = "object" }),
            Icons = new[] { new Icon { Source = "https://x/i.png" } }
        };

        Assert.True(HasProp(RevisionAwareJson.ToElementForRevision(tool, ProtocolRevision.V2025_11_25), "icons"));
        Assert.False(HasProp(RevisionAwareJson.ToElementForRevision(tool, ProtocolRevision.V2025_06_18), "icons"));
    }

    [Fact]
    public void SamplingToolCalling_DroppedForOlderRevision()
    {
        var request = new CreateMessageRequest
        {
            Messages = [new SamplingMessage { Role = Role.User, Content = [new TextContent { Text = "hi" }] }],
            MaxTokens = 10,
            Tools = new[] { new Tool { Name = "t", Description = "d", InputSchema = McpJsonDefaults.ToElement(new { }) } },
            ToolChoice = new ToolChoice { Mode = "auto" }
        };

        var older = RevisionAwareJson.ToElementForRevision(request, ProtocolRevision.V2025_06_18);
        Assert.False(HasProp(older, "tools"));
        Assert.False(HasProp(older, "toolChoice"));

        var latest = RevisionAwareJson.ToElementForRevision(request, ProtocolRevision.V2025_11_25);
        Assert.True(HasProp(latest, "tools"));
        Assert.True(HasProp(latest, "toolChoice"));
    }

    [Fact]
    public void ElicitationUrlMode_DroppedForOlderRevision()
    {
        var request = ElicitRequest.ForUrl("Authorize", "id-1", "https://x/auth");

        var older = RevisionAwareJson.ToElementForRevision(request, ProtocolRevision.V2025_06_18);
        Assert.False(HasProp(older, "mode"));
        Assert.False(HasProp(older, "url"));
        Assert.False(HasProp(older, "elicitationId"));
        Assert.Equal("Authorize", older.GetProperty("message").GetString());
    }

    [Fact]
    public void NestedImplementation_InInitializeResult_GatedByRevision()
    {
        var result = new InitializeResult
        {
            ProtocolVersion = "2025-06-18",
            Capabilities = new ServerCapabilities(),
            ServerInfo = new Implementation("s", "1.0.0") { Description = "desc" }
        };

        var older = RevisionAwareJson.ToElementForRevision(result, ProtocolRevision.V2025_06_18);
        Assert.False(HasProp(older.GetProperty("serverInfo"), "description"));
    }

    // ---- End-to-end server test ----

    [Theory]
    [InlineData("2025-11-25", true)]
    [InlineData("2025-06-18", false)]
    public async Task Server_InitializeResult_GatesServerInfoByNegotiatedRevision(string version, bool expectNewerFields)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var server = new McpServer(serverTransport, new McpServerOptions
        {
            ServerInfo = new Implementation("TestServer", "1.0.0")
            {
                Description = "A test server",
                Icons = new[] { new Icon { Source = "https://x/i.png" } }
            }
        });
        _ = server.RunAsync(cts.Token);
        await clientTransport.ConnectAsync(cts.Token);

        await clientTransport.SendAsync(new JsonRpcRequest
        {
            Id = 1,
            Method = McpMethods.Initialize,
            Params = McpJsonDefaults.ToElement(new InitializeParams
            {
                ProtocolVersion = version,
                Capabilities = new ClientCapabilities(),
                ClientInfo = new Implementation("c", "1.0.0")
            })
        }, cts.Token);

        JsonRpcResponse? response = null;
        await foreach (var msg in clientTransport.Messages.WithCancellation(cts.Token))
        {
            if (msg is JsonRpcResponse r) { response = r; break; }
        }

        Assert.NotNull(response);
        var serverInfo = response!.Result!.Value.GetProperty("serverInfo");
        Assert.Equal("TestServer", serverInfo.GetProperty("name").GetString());
        Assert.Equal(expectNewerFields, HasProp(serverInfo, "description"));
        Assert.Equal(expectNewerFields, HasProp(serverInfo, "icons"));
    }
}
