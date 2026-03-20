using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;

namespace Andy.MCP.Examples;

/// <summary>
/// A simple MCP client that connects to a server via stdio and exercises its features.
/// Run with: dotnet run --project examples/Andy.MCP.Examples -- client dotnet "run --project examples/Andy.MCP.Examples -- server"
/// </summary>
public static class SimpleClient
{
    public static async Task RunAsync(string command, string arguments)
    {
        Console.WriteLine($"Connecting to MCP server: {command} {arguments}");

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = command,
            Arguments = arguments
        });

        await using var client = await McpClient.ConnectAsync(transport, new McpClientOptions
        {
            ClientInfo = new Implementation("SimpleClient", "1.0.0")
        });

        Console.WriteLine($"Connected to: {client.Session.RemoteInfo?.Name} v{client.Session.RemoteInfo?.Version}");
        Console.WriteLine($"Protocol: {client.Session.ProtocolVersion}");
        Console.WriteLine($"Instructions: {client.Session.Instructions}");
        Console.WriteLine();

        // Ping
        await client.PingAsync();
        Console.WriteLine("Ping: OK");
        Console.WriteLine();

        // List and call tools
        if (client.Session.HasServerCapability("tools"))
        {
            var tools = await client.ListToolsAsync();
            Console.WriteLine($"Tools ({tools.Count}):");
            foreach (var tool in tools)
                Console.WriteLine($"  - {tool.Name}: {tool.Description}");
            Console.WriteLine();

            // Call greet tool
            var greetResult = await client.CallToolAsync("greet", new { name = "Alice" });
            Console.WriteLine($"greet result: {((TextContent)greetResult.Content[0]).Text}");

            // Call calculator
            var calcResult = await client.CallToolAsync("calculate",
                new { operation = "multiply", a = 6, b = 7 });
            Console.WriteLine($"calculate result: {((TextContent)calcResult.Content[0]).Text}");
            Console.WriteLine();
        }

        // List and read resources
        if (client.Session.HasServerCapability("resources"))
        {
            var resources = await client.ListResourcesAsync();
            Console.WriteLine($"Resources ({resources.Count}):");
            foreach (var resource in resources)
                Console.WriteLine($"  - {resource.Uri}: {resource.Name}");

            if (resources.Count > 0)
            {
                var content = await client.ReadResourceAsync(resources[0].Uri);
                var text = content.Contents[0] as TextResourceContents;
                Console.WriteLine($"Read {resources[0].Uri}: {text?.Text}");
            }
            Console.WriteLine();
        }

        // List and get prompts
        if (client.Session.HasServerCapability("prompts"))
        {
            var prompts = await client.ListPromptsAsync();
            Console.WriteLine($"Prompts ({prompts.Count}):");
            foreach (var prompt in prompts)
            {
                var argList = prompt.Arguments is not null
                    ? string.Join(", ", prompt.Arguments.Select(a => a.Name))
                    : "none";
                Console.WriteLine($"  - {prompt.Name}: {prompt.Description} (args: {argList})");
            }

            var promptResult = await client.GetPromptAsync("explain",
                new Dictionary<string, string> { ["topic"] = "Model Context Protocol" });
            Console.WriteLine($"explain prompt: {((TextContent)promptResult.Messages[0].Content).Text}");
            Console.WriteLine();
        }

        Console.WriteLine("Done!");
    }
}
