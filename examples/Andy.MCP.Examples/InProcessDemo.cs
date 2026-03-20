using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;
using Andy.MCP.Transport;

namespace Andy.MCP.Examples;

/// <summary>
/// Demonstrates in-process MCP client ↔ server communication without any subprocess.
/// Uses linked streams (PipeStream) to connect client and server in the same process.
/// Run with: dotnet run --project examples/Andy.MCP.Examples
/// </summary>
public static class InProcessDemo
{
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Andy.MCP In-Process Demo ===");
        Console.WriteLine();

        // Create linked pipes for in-process communication
        var (serverInput, clientOutput) = CreateLinkedStreams();
        var (clientInput, serverOutput) = CreateLinkedStreams();

        // Set up server
        var serverTransport = new StdioServerTransport(
            new StreamReader(serverInput),
            new StreamWriter(serverOutput) { AutoFlush = true });

        var server = new McpServer(serverTransport, new McpServerOptions
        {
            ServerInfo = new Implementation("InProcessServer", "1.0.0"),
            Instructions = "An in-process demo server."
        });

        server.AddTool("greet", "Say hello",
            McpJsonDefaults.ToElement(new
            {
                type = "object",
                properties = new { name = new { type = "string" } },
                required = new[] { "name" }
            }),
            async (args, ct) =>
            {
                var name = args?.GetProperty("name").GetString() ?? "World";
                return CallToolResult.Text($"Hello, {name}!");
            });

        server.AddTool("time", "Get current time", async (args, ct) =>
            CallToolResult.Text($"Current time: {DateTime.Now:HH:mm:ss}"));

        server.AddResource("demo://greeting", "Greeting",
            async (uri, ct) => new TextResourceContents
            {
                Uri = uri,
                Text = "Welcome to Andy.MCP!",
                MimeType = "text/plain"
            });

        server.AddPrompt("haiku", "Write a haiku",
            async (name, args, ct) => new GetPromptResult
            {
                Messages = [new PromptMessage
                {
                    Role = Role.User,
                    Content = new TextContent { Text = "Write a haiku about programming." }
                }]
            });

        server.WithLogging();

        // Start server in background
        using var cts = new CancellationTokenSource();
        var serverTask = server.RunAsync(cts.Token);

        // Set up client
        var clientTransport = new StdioClientTransportFromStreams(clientInput, clientOutput);
        await using var client = await McpClient.ConnectAsync(clientTransport, new McpClientOptions
        {
            ClientInfo = new Implementation("InProcessClient", "1.0.0")
        });

        // Use the client
        Console.WriteLine($"Connected to: {client.Session.RemoteInfo?.Name}");
        Console.WriteLine($"Protocol version: {client.Session.ProtocolVersion}");
        Console.WriteLine();

        // Ping
        await client.PingAsync();
        Console.WriteLine("Ping: OK");

        // Tools
        var tools = await client.ListToolsAsync();
        Console.WriteLine($"\nTools ({tools.Count}):");
        foreach (var t in tools) Console.WriteLine($"  {t.Name} - {t.Description}");

        var greetResult = await client.CallToolAsync("greet", new { name = "Developer" });
        Console.WriteLine($"\ngreet: {((TextContent)greetResult.Content[0]).Text}");

        var timeResult = await client.CallToolAsync("time");
        Console.WriteLine($"time: {((TextContent)timeResult.Content[0]).Text}");

        // Resources
        var resources = await client.ListResourcesAsync();
        Console.WriteLine($"\nResources ({resources.Count}):");
        foreach (var r in resources) Console.WriteLine($"  {r.Uri} - {r.Name}");

        var readResult = await client.ReadResourceAsync("demo://greeting");
        Console.WriteLine($"Read: {((TextResourceContents)readResult.Contents[0]).Text}");

        // Prompts
        var prompts = await client.ListPromptsAsync();
        Console.WriteLine($"\nPrompts ({prompts.Count}):");
        foreach (var p in prompts) Console.WriteLine($"  {p.Name} - {p.Description}");

        var haikuResult = await client.GetPromptAsync("haiku");
        Console.WriteLine($"Haiku prompt: {((TextContent)haikuResult.Messages[0].Content).Text}");

        Console.WriteLine("\n=== Demo Complete ===");

        // Cleanup
        cts.Cancel();
        try { await serverTask; } catch { }
    }

    /// <summary>
    /// Create a pair of linked streams where writes to the writer appear as reads on the reader.
    /// </summary>
    private static (Stream reader, Stream writer) CreateLinkedStreams()
    {
        var pipe = new System.IO.Pipes.AnonymousPipeServerStream(
            System.IO.Pipes.PipeDirection.Out, HandleInheritability.None);
        var client = new System.IO.Pipes.AnonymousPipeClientStream(
            System.IO.Pipes.PipeDirection.In, pipe.ClientSafePipeHandle);
        return (client, pipe);
    }
}

/// <summary>
/// A client transport that wraps existing streams (for in-process use).
/// </summary>
internal sealed class StdioClientTransportFromStreams : IClientTransport
{
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private volatile bool _connected;

    public bool IsConnected => _connected;
    public event EventHandler<TransportDisconnectedEventArgs>? Disconnected;

    public StdioClientTransportFromStreams(Stream input, Stream output)
    {
        _reader = new StreamReader(input);
        _writer = new StreamWriter(output) { AutoFlush = true };
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _connected = true;
        return Task.CompletedTask;
    }

    public async Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        var json = McpJsonDefaults.Serialize(message);
        await _writer.WriteLineAsync(json.AsMemory(), cancellationToken);
        await _writer.FlushAsync(cancellationToken);
    }

    public IAsyncEnumerable<JsonRpcMessage> Messages => ReadAsync();

    private async IAsyncEnumerable<JsonRpcMessage> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await _reader.ReadLineAsync(ct);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonRpcMessage? message = null;
            try { message = McpJsonDefaults.Deserialize(line); }
            catch { }

            if (message is not null) yield return message;
        }
    }

    public ValueTask DisposeAsync()
    {
        _connected = false;
        _reader.Dispose();
        _writer.Dispose();
        return ValueTask.CompletedTask;
    }
}
