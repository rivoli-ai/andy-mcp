using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;

namespace Andy.MCP.Tests.Conformance;

/// <summary>
/// Cross-implementation interop (issue #74): drives the Andy.MCP client against the official
/// reference server (@modelcontextprotocol/server-everything) over stdio, proving interoperability
/// with an independent MCP implementation. Requires Node/npx; the interop CI job provides them.
/// Tagged "Interop" so the normal test run can exclude it.
/// </summary>
[Trait("Category", "Interop")]
public class ReferenceServerInteropTests
{
    private static bool NpxAvailable()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator).Any(dir =>
            !string.IsNullOrEmpty(dir) &&
            (File.Exists(Path.Combine(dir, "npx")) || File.Exists(Path.Combine(dir, "npx.cmd"))));
    }

    [Fact]
    public async Task AndyClient_Interoperates_With_ReferenceEverythingServer()
    {
        if (!NpxAvailable())
            return; // Node/npx not installed — this runs in the dedicated interop CI job.

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "npx",
            Arguments = "-y @modelcontextprotocol/server-everything stdio"
        });

        await using var client = await McpClient.ConnectAsync(transport, cancellationToken: cts.Token);

        // Initialization negotiated with the independent reference server.
        Assert.Equal(McpSessionState.Ready, client.Session.State);
        Assert.Contains("everything", client.Session.RemoteInfo!.Name, StringComparison.OrdinalIgnoreCase);

        // Tools: list and call the reference server's echo tool.
        var tools = await client.ListToolsAsync(cts.Token);
        Assert.Contains(tools, t => t.Name == "echo");

        var echo = await client.CallToolAsync("echo", new { message = "interop-hello" }, cts.Token);
        Assert.Contains("interop-hello", ((TextContent)echo.Content[0]).Text);

        // Resources: list and read.
        var resources = await client.ListResourcesAsync(cts.Token);
        Assert.NotEmpty(resources);
        var read = await client.ReadResourceAsync(resources[0].Uri, cts.Token);
        Assert.NotEmpty(read.Contents);

        // Prompts: list.
        var prompts = await client.ListPromptsAsync(cts.Token);
        Assert.Contains(prompts, p => p.Name == "simple-prompt");
    }
}
