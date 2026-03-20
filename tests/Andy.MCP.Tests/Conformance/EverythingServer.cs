using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;
using Andy.MCP.Transport;

namespace Andy.MCP.Tests.Conformance;

/// <summary>
/// A reference MCP server that registers ALL features.
/// Used as the primary integration test target.
/// </summary>
public static class EverythingServer
{
    public static McpServer Create(IServerTransport transport)
    {
        var server = new McpServer(transport, new McpServerOptions
        {
            ServerInfo = new Implementation("EverythingServer", "1.0.0"),
            Instructions = "This server supports all MCP features for testing."
        });

        RegisterTools(server);
        RegisterResources(server);
        RegisterPrompts(server);
        RegisterCompletions(server);
        server.WithLogging();

        return server;
    }

    private static void RegisterTools(McpServer server)
    {
        // Simple echo tool
        server.AddTool("echo", "Echoes the input message",
            McpJsonDefaults.ToElement(new
            {
                type = "object",
                properties = new { message = new { type = "string" } },
                required = new[] { "message" }
            }),
            async (args, ct) =>
            {
                var msg = args?.GetProperty("message").GetString() ?? "";
                return CallToolResult.Text(msg);
            });

        // Add two numbers
        server.AddTool("add", "Add two numbers",
            McpJsonDefaults.ToElement(new
            {
                type = "object",
                properties = new { a = new { type = "number" }, b = new { type = "number" } },
                required = new[] { "a", "b" }
            }),
            async (args, ct) =>
            {
                var a = args!.Value.GetProperty("a").GetDouble();
                var b = args!.Value.GetProperty("b").GetDouble();
                return CallToolResult.Text((a + b).ToString());
            });

        // Tool returning image content
        server.AddTool("get_image", "Returns a tiny test image", async (args, ct) =>
        {
            // 1x1 red PNG
            var pngBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg==");
            return new CallToolResult
            {
                Content = [ImageContent.FromBytes(pngBytes, "image/png")]
            };
        });

        // Tool returning audio content
        server.AddTool("get_audio", "Returns test audio data", async (args, ct) =>
        {
            var audioBytes = new byte[] { 0x52, 0x49, 0x46, 0x46 }; // RIFF header stub
            return new CallToolResult
            {
                Content = [AudioContent.FromBytes(audioBytes, "audio/wav")]
            };
        });

        // Tool returning mixed content types
        server.AddTool("multi_content", "Returns multiple content types", async (args, ct) =>
        {
            return new CallToolResult
            {
                Content =
                [
                    new TextContent { Text = "Here's a summary" },
                    ImageContent.FromBytes([0xFF, 0xD8], "image/jpeg"),
                    new ResourceLink { Uri = "file:///readme.md", Name = "README" }
                ]
            };
        });

        // Long-running tool with progress
        server.AddTool("long_running", "Simulates work with progress", async (args, ct) =>
        {
            var steps = 5;
            for (int i = 1; i <= steps; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(10, ct); // Short delay for testing
            }
            return CallToolResult.Text($"Completed {steps} steps");
        });

        // Tool that always errors
        server.AddTool("error_tool", "Always throws an error", async (args, ct) =>
        {
            throw new InvalidOperationException("This tool always fails");
        });

        // Tool with annotations
        server.AddTool("annotated_tool", "A read-only tool",
            McpJsonDefaults.ToElement(new { type = "object", properties = new { } }),
            async (args, ct) => CallToolResult.Text("read-only result"),
            annotations: new ToolAnnotations
            {
                ReadOnlyHint = true,
                DestructiveHint = false,
                IdempotentHint = true,
                OpenWorldHint = false
            });
    }

    private static void RegisterResources(McpServer server)
    {
        server.AddResource("file:///readme.md", "README",
            async (uri, ct) => new TextResourceContents
            {
                Uri = uri, Text = "# Everything Server\nA reference MCP server.", MimeType = "text/markdown"
            },
            description: "Project README", mimeType: "text/markdown");

        server.AddResource("file:///logo.png", "Logo",
            async (uri, ct) => BlobResourceContents.FromBytes(uri,
                Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg=="),
                "image/png"),
            description: "Project logo", mimeType: "image/png");

        server.AddResource("file:///dynamic", "Dynamic Resource",
            async (uri, ct) => new TextResourceContents
            {
                Uri = uri, Text = $"Generated at {DateTimeOffset.UtcNow:O}", MimeType = "text/plain"
            });

        // Resource template
        server.AddResourceTemplate("file:///config/{key}", "Configuration",
            description: "Configuration values by key");
    }

    private static void RegisterPrompts(McpServer server)
    {
        server.AddPrompt("simple_greeting", "A simple greeting",
            async (name, args, ct) => new GetPromptResult
            {
                Description = "A friendly greeting",
                Messages = [new PromptMessage
                {
                    Role = Role.User,
                    Content = new TextContent { Text = "Hello! Please greet me warmly." }
                }]
            });

        server.AddPrompt("code_review", "Code review prompt",
            async (name, args, ct) =>
            {
                var lang = args?["language"] ?? "unknown";
                var style = args is not null && args.TryGetValue("style", out var s) ? s : "thorough";
                return new GetPromptResult
                {
                    Description = $"Code review in {lang}",
                    Messages = [new PromptMessage
                    {
                        Role = Role.User,
                        Content = new TextContent { Text = $"Review this {lang} code. Style: {style}." }
                    }]
                };
            },
            arguments:
            [
                new PromptArgument { Name = "language", Description = "Programming language", Required = true },
                new PromptArgument { Name = "style", Description = "Review style" }
            ]);

        server.AddPrompt("multi_turn", "Multi-turn conversation",
            async (name, args, ct) => new GetPromptResult
            {
                Description = "A multi-turn example",
                Messages =
                [
                    new PromptMessage { Role = Role.User, Content = new TextContent { Text = "What is 2+2?" } },
                    new PromptMessage { Role = Role.Assistant, Content = new TextContent { Text = "2+2 = 4" } },
                    new PromptMessage { Role = Role.User, Content = new TextContent { Text = "And 3+3?" } }
                ]
            });
    }

    private static void RegisterCompletions(McpServer server)
    {
        server.AddCompletion("ref/prompt", "code_review", "language",
            async (value, context, ct) =>
            {
                var languages = new[] { "csharp", "python", "javascript", "typescript", "go", "rust", "java" };
                var filtered = languages
                    .Where(l => l.StartsWith(value, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return new CompletionValues { Values = filtered };
            });
    }
}
