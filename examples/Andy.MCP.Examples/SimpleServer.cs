using System.Text.Json;
using Andy.MCP.Protocol;
using Andy.MCP.Server;
using Andy.MCP.Transport;

namespace Andy.MCP.Examples;

/// <summary>
/// A minimal MCP server that runs over stdio.
/// Run with: dotnet run --project examples/Andy.MCP.Examples -- server
/// </summary>
public static class SimpleServer
{
    public static async Task RunAsync()
    {
        var server = new McpServer(
            new StdioServerTransport(),
            new McpServerOptions
            {
                ServerInfo = new Implementation("SimpleServer", "1.0.0"),
                Instructions = "A simple example MCP server with a few tools."
            });

        // Register a greeting tool
        server.AddTool("greet", "Greet someone by name",
            McpJsonDefaults.ToElement(new
            {
                type = "object",
                properties = new
                {
                    name = new { type = "string", description = "Person to greet" }
                },
                required = new[] { "name" }
            }),
            async (args, ct) =>
            {
                var name = args?.GetProperty("name").GetString() ?? "World";
                return CallToolResult.Text($"Hello, {name}! Welcome to MCP.");
            });

        // Register a calculator tool
        server.AddTool("calculate", "Perform basic arithmetic",
            McpJsonDefaults.ToElement(new
            {
                type = "object",
                properties = new
                {
                    operation = new { type = "string", description = "add, subtract, multiply, divide" },
                    a = new { type = "number" },
                    b = new { type = "number" }
                },
                required = new[] { "operation", "a", "b" }
            }),
            async (args, ct) =>
            {
                var op = args!.Value.GetProperty("operation").GetString()!;
                var a = args.Value.GetProperty("a").GetDouble();
                var b = args.Value.GetProperty("b").GetDouble();

                var result = op switch
                {
                    "add" => a + b,
                    "subtract" => a - b,
                    "multiply" => a * b,
                    "divide" => b != 0 ? a / b : double.NaN,
                    _ => throw new ArgumentException($"Unknown operation: {op}")
                };

                return CallToolResult.Text($"{a} {op} {b} = {result}");
            });

        // Register a resource
        server.AddResource("info://server", "Server Info",
            async (uri, ct) => new TextResourceContents
            {
                Uri = uri,
                Text = "SimpleServer v1.0.0 — An example MCP server built with Andy.MCP",
                MimeType = "text/plain"
            },
            description: "Information about this server");

        // Register a prompt
        server.AddPrompt("explain", "Explain a concept",
            async (name, args, ct) =>
            {
                var topic = args?["topic"] ?? "MCP";
                return new GetPromptResult
                {
                    Description = $"Explain {topic}",
                    Messages =
                    [
                        new PromptMessage
                        {
                            Role = Role.User,
                            Content = new TextContent
                            {
                                Text = $"Please explain {topic} in simple terms, as if I'm a beginner."
                            }
                        }
                    ]
                };
            },
            arguments: [new PromptArgument { Name = "topic", Description = "Topic to explain", Required = true }]);

        // Enable logging
        server.WithLogging();

        // Run until stdin closes or process is terminated
        await server.RunAsync();
    }
}
