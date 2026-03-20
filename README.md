# Andy.MCP

> **ALPHA** -- This library is in early development. APIs may change without notice. Not recommended for production use. Use at your own risk.

## Overview

Andy.MCP is a .NET 8 library implementing the [Model Context Protocol](https://modelcontextprotocol.io/) (MCP) specification version 2025-06-18. It provides both client and server capabilities for building MCP-compatible applications.

The library is designed for integration with the Andy ecosystem (Andy Engine, Andy MCP Gateway, Andy Containers) but can be used independently in any .NET application.

## Packages

| Package | Description |
|---------|-------------|
| `Andy.MCP` | Core library -- protocol types, transports, client, server, auth, configuration |
| `Andy.MCP.AspNetCore` | ASP.NET Core integration -- Streamable HTTP server transport |

## Features

- Full MCP 2025-06-18 specification compliance
- JSON-RPC 2.0 protocol with polymorphic serialization
- Transports: stdio (local process), Streamable HTTP (remote), SSE
- High-level client API with auto-pagination and capability gating
- High-level server API with fluent and attribute-based registration
- All server features: tools, resources, prompts, completions, logging
- All client features: roots, sampling, elicitation
- All content types: text, image, audio, resource link, embedded resource, tool use, tool result
- OAuth 2.1 with PKCE, Dynamic Client Registration (RFC 7591/7592)
- Security: SSRF prevention, Origin validation, session management
- OpenTelemetry distributed tracing via `System.Diagnostics.ActivitySource`
- Dependency injection and `IHostedService` integration
- Configuration binding from `appsettings.json`

## Quick Start

### Server

```csharp
using Andy.MCP.Protocol;
using Andy.MCP.Server;
using Andy.MCP.Transport;

var server = new McpServer(new StdioServerTransport(), new McpServerOptions
{
    ServerInfo = new Implementation("MyServer", "1.0.0")
});

server.AddTool("greet", "Say hello",
    McpJsonDefaults.ToElement(new
    {
        type = "object",
        properties = new { name = new { type = "string" } },
        required = new[] { "name" }
    }),
    (args, ct) =>
    {
        var name = args?.GetProperty("name").GetString() ?? "World";
        return Task.FromResult(CallToolResult.Text($"Hello, {name}!"));
    });

await server.RunAsync();
```

### Server (attribute-based)

```csharp
using Andy.MCP.Protocol;
using Andy.MCP.Server;

public class MyTools
{
    [McpTool(Description = "Say hello")]
    public Task<CallToolResult> Greet(
        [McpParam(Description = "Person to greet", Required = true)] string name)
    {
        return Task.FromResult(CallToolResult.Text($"Hello, {name}!"));
    }
}

// Register:
server.AddToolsFromType<MyTools>();
```

### Client

```csharp
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;

await using var client = await McpClient.ConnectAsync(
    new StdioClientTransport(new StdioClientTransportOptions
    {
        Command = "dotnet",
        Arguments = "run --project path/to/server"
    }));

var tools = await client.ListToolsAsync();
var result = await client.CallToolAsync("greet", new { name = "Alice" });
```

### Dependency Injection

```csharp
builder.Services.AddMcpClient(options =>
{
    options.AddStdioServer("my-server", "/usr/bin/mcp-server", args: "--port 3000");
    options.AddHttpServer("remote", "https://mcp.example.com/mcp");
});
```

### OpenTelemetry

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource("Andy.MCP"));
```

## Examples

Run the in-process demo (no external dependencies):

```bash
dotnet run --project examples/Andy.MCP.Examples
```

Run client connecting to server over stdio:

```bash
dotnet run --project examples/Andy.MCP.Examples -- client dotnet "run --project examples/Andy.MCP.Examples -- server"
```

## Project Structure

```
src/
  Andy.MCP/                  Core library
  Andy.MCP.AspNetCore/       ASP.NET Core HTTP server transport
tests/
  Andy.MCP.Tests/            450 tests
examples/
  Andy.MCP.Examples/         Getting-started examples
docs/
  requirements.md            Specification compliance matrix
  design.md                  Architecture and design decisions
  implementation.md          Implementation details and type inventory
```

## Building

```bash
dotnet build
dotnet test
```

## Documentation

See the `docs/` directory:

- [Requirements](docs/requirements.md) -- full MCP spec compliance matrix
- [Design](docs/design.md) -- architecture, design decisions, extensibility
- [Implementation](docs/implementation.md) -- project structure, type inventory, test breakdown

## License

Licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE) for details.
