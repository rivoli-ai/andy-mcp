using System.Text.Json;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Server;

/// <summary>
/// Tests that a tool's input/output schema is validated for well-formedness at registration
/// time (issue #47).
/// </summary>
public class ToolSchemaRegistrationTests
{
    private static readonly Func<JsonElement?, CancellationToken, Task<CallToolResult>> Noop =
        (_, _) => Task.FromResult(CallToolResult.Text("ok"));

    [Fact]
    public void ValidSchema_Registers()
    {
        var server = NewServer();

        var schema = McpJsonDefaults.ToElement(new
        {
            type = "object",
            properties = new { name = new { type = "string" } },
            required = new[] { "name" }
        });

        var ex = Record.Exception(() => server.AddTool("t", "d", schema, Noop));
        Assert.Null(ex);
    }

    [Fact]
    public void MalformedProperties_Throws()
    {
        var server = NewServer();
        var schema = McpJsonDefaults.ToElement(new { type = "object", properties = "not-an-object" });

        var ex = Assert.Throws<ArgumentException>(() => server.AddTool("t", "d", schema, Noop));
        Assert.Contains("properties", ex.Message);
    }

    [Fact]
    public void InvalidType_Throws()
    {
        var server = NewServer();
        var schema = McpJsonDefaults.ToElement(new { type = "banana" });

        var ex = Assert.Throws<ArgumentException>(() => server.AddTool("t", "d", schema, Noop));
        Assert.Contains("type", ex.Message);
    }

    [Fact]
    public void MalformedRequired_Throws()
    {
        var server = NewServer();
        var schema = McpJsonDefaults.ToElement(new { type = "object", required = new[] { 1, 2 } });

        var ex = Assert.Throws<ArgumentException>(() => server.AddTool("t", "d", schema, Noop));
        Assert.Contains("required", ex.Message);
    }

    [Fact]
    public void MalformedOutputSchema_Throws()
    {
        var server = NewServer();
        var input = McpJsonDefaults.ToElement(new { type = "object" });
        var output = McpJsonDefaults.ToElement(new { type = 42 });

        Assert.Throws<ArgumentException>(() =>
            server.AddTool("t", "d", input, Noop, annotations: null, outputSchema: output));
    }

    private static McpServer NewServer()
    {
        var (_, serverTransport) = InMemoryTransport.CreatePair();
        return new McpServer(serverTransport);
    }
}
