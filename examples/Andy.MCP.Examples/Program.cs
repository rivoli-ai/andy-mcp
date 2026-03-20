// Andy.MCP Examples
// Run with: dotnet run --project examples/Andy.MCP.Examples [example-name]
//
// Available examples:
//   server     - Run a simple MCP server over stdio
//   client     - Connect to a server and call tools
//   inprocess  - In-process client ↔ server demo (no subprocess)

using Andy.MCP.Examples;

var example = args.Length > 0 ? args[0] : "inprocess";

switch (example.ToLowerInvariant())
{
    case "server":
        await SimpleServer.RunAsync();
        break;
    case "client":
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: dotnet run -- client <command> <args>");
            Console.Error.WriteLine("Example: dotnet run -- client dotnet \"run --project examples/Andy.MCP.Examples -- server\"");
            return;
        }
        await SimpleClient.RunAsync(args[1], args[2]);
        break;
    case "inprocess":
        await InProcessDemo.RunAsync();
        break;
    default:
        Console.Error.WriteLine($"Unknown example: {example}");
        Console.Error.WriteLine("Available: server, client, inprocess");
        break;
}
