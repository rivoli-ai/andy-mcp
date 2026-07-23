# Andy.MCP

> **ALPHA** -- This library is in early development. APIs may change without notice. Not recommended for production use. Use at your own risk.

## Overview

Andy.MCP is a .NET 8 library implementing the [Model Context Protocol](https://modelcontextprotocol.io/) (MCP). It negotiates **2025-11-25** (the latest revision) by default and negotiates down to 2025-06-18, 2025-03-26, and 2024-11-05 for older peers. It provides both client and server capabilities for building MCP-compatible applications.

Feature support is granular (stable / experimental / partial) and documented with test evidence in the **[compliance matrix](docs/compliance.md)** — please read it before relying on any specific capability.

The library is designed for integration with the Andy ecosystem (Andy Engine, Andy MCP Gateway, Andy Containers) but can be used independently in any .NET application.

## Packages

| Package | Description |
|---------|-------------|
| `Andy.MCP` | Core library -- protocol types, transports, client, server, auth, configuration |
| `Andy.MCP.AspNetCore` | ASP.NET Core integration -- Streamable HTTP server transport |

## Features

See the **[compliance matrix](docs/compliance.md)** for exact, test-linked status. In summary:

- MCP 2025-11-25 negotiated by default, with revision-aware serialization for older peers
- JSON-RPC 2.0 with polymorphic serialization; bidirectional request/response correlation
- Transports: stdio and Streamable HTTP (SSE GET stream; replay/resumption not yet implemented)
- High-level client API: tools, resources (+ subscribe), prompts, completion, roots, sampling, elicitation, auto-pagination, capability gating
- High-level server API: fluent + attribute registration; concurrent dispatch, cancellation, progress
- Recursive JSON Schema input validation and structured tool output enforcement
- Experimental tasks (task-augmented `tools/call` + `tasks/*`)
- Security: opt-in Origin validation, principal-bound sessions with cross-user rejection, fail-closed body/session limits
- OAuth: challenge parsing, safe 401 refresh, managed registration, Client ID Metadata Documents, and opt-in PKCE/403 scope step-up (not a complete OAuth claim — see matrix)
- OpenTelemetry tracing, dependency injection, `IHostedService`, and `appsettings.json` binding

### OAuth registration and authorization boundaries

The supported registration paths are RFC 7592 management of an existing registration, Client ID
Metadata Documents (CIMD), and explicitly configured Dynamic Client Registration (DCR). For
initial registration selection, a validated CIMD is used when the authorization server advertises
`client_id_metadata_document_supported`; otherwise the library uses DCR only when the caller
explicitly supplies DCR metadata and the server advertises a registration endpoint. RFC 7592 then
manages the resulting registration state. CIMD documents remain host-provided: the library
validates and selects their URL identity but neither fetches, hosts, nor sends the document.

Interactive authorization is also host-integrated rather than browser-integrated. An
`IOAuthAuthorizationProvider` receives only authorization and redirect URIs; the library owns PKCE
S256, state, callback validation, code exchange, and token persistence. A step-up occurs only for
a Bearer `403` with `error="insufficient_scope"` and challenged scopes. It requests the ordinal,
case-sensitive union of existing and challenged scopes, coordinates an upgrade per resource, and
retries the original request once only after a new token covers that set. Failed interactions leave
the existing token and original `403` intact.

> **Not a full-compliance claim.** Several areas are partial or experimental (SSE replay, OAuth
> discovery, resource templates, sampling tool-calling, task augmentation beyond tools). The
> matrix marks each honestly and links to tests.

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

## .NET support policy

Andy.MCP currently targets **.NET 8 (LTS)**, supported by Microsoft through **November 2026**.
.NET 10 is the active LTS through November 2028.

- Package versions are managed centrally via [`Directory.Packages.props`](Directory.Packages.props)
  (Central Package Management).
- CI runs the test suite on Linux, macOS, and Windows, collects code coverage, and **fails the
  build on any known-vulnerable dependency** (`dotnet list package --vulnerable`).
- **Plan:** multi-target `net8.0` and `net10.0` (or migrate to `net10.0`) before .NET 8 reaches
  end of support in November 2026, then drop `net8.0` after that date. Multi-targeting is deferred
  until the .NET 10 SDK is available in the build/CI environment.

See the [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy) for details.

## Documentation

See the `docs/` directory:

- [Compliance matrix](docs/compliance.md) -- test-linked, revision-specific feature status
- [Requirements](docs/requirements.md) -- MCP spec requirement tracking
- [Design](docs/design.md) -- architecture, design decisions, extensibility
- [Implementation](docs/implementation.md) -- project structure, type inventory, test breakdown

## Project status

**Phase 7 (Full MCP 2025-11-25 compliance) — in progress (updated 2026-07-21).**

Landed and tested across this phase: 2025-11-25 schema + revision-aware serialization, bidirectional
JSON-RPC and lifecycle enforcement, concurrent dispatch with cancellation/progress/timeouts,
Streamable HTTP validation + negotiated version + stdio parse errors, principal-bound HTTP sessions
with fail-closed limits, recursive JSON Schema validation + structured outputs, client
completion/subscribe APIs, experimental tasks (tools), conformance fixtures + CI coverage/vulnerability
gates, and OAuth 401/refresh hardening. The full suite is green on Linux, macOS, and Windows.

Phase 7 is **not** marked complete: several matrix rows remain partial/experimental (see the
[compliance matrix](docs/compliance.md)), and the official-schema + cross-implementation conformance
gate is not yet in place. The **ALPHA** and non-full-compliance wording above stays until those gates
pass.

## License

Licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE) for details.
