# Andy.MCP

> **ALPHA** -- This library is in early development. APIs may change without notice. Not recommended for production use. Use at your own risk.

## Overview

Andy.MCP is a .NET 10 library implementing the [Model Context Protocol](https://modelcontextprotocol.io/) (MCP). It negotiates **2025-11-25** (the latest revision) by default and negotiates down to 2025-06-18 and 2024-11-05 over stdio. Streamable HTTP supports 2025-11-25 and 2025-06-18. Legacy 2024-11-05 HTTP+SSE and the mandatory JSON-RPC batching in 2025-03-26 are not implemented; those revisions are not offered by the applicable transport. It provides both client and server capabilities for building MCP-compatible applications.

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
- [Transports](docs/transports.md): UTF-8/LF stdio with graceful shutdown; Streamable HTTP with JSON or POST SSE, bounded polling/replay, concurrent streams and automatic session recovery
- High-level client API: tools, resources (+ subscribe), prompts, completion, roots, sampling, elicitation, auto-pagination and explicit pages, per-call deadlines, custom methods, capability gating
- High-level server API: registration frozen at startup; RFC 6570 templates, multi-content resources, required prompt arguments, cancellation and progress
- JSON Schema 2020-12 input validation, structured output enforcement, and complete tool metadata registration
- Experimental tasks (task-augmented `tools/call` + `tasks/*`), with terminal-state and TTL enforcement, blocking result retrieval, handler cancellation and resumable peer input; injectable stores retain outcomes across connections, with disk-backed restart tests; end-to-end lifecycle work remains in progress (#49/#72)
- Security: fail-closed HTTP authorization and present-Origin validation, issuer/subject-bound sessions, audience/scope checks and bounded resources
- OAuth: challenge parsing, correct 401 handling, safe concurrent refresh, PRM/RFC 8414/OIDC metadata discovery wired into the 401 flow, RFC 7592 managed registration, Client ID Metadata Documents, and opt-in PKCE/403 scope step-up (not a complete OAuth claim — see matrix)
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

> **Alpha:** experimental task lifecycle and full-compliance epics remain open. Model/tool execution, user approval and identity-provider integration belong to the application. See the [evidence-backed matrix](docs/compliance.md).

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
  Andy.MCP.Tests/            unit + conformance + interop suites
examples/
  Andy.MCP.Examples/         Getting-started examples
docs/
  requirements.md            Specification compliance matrix
  design.md                  Architecture and design decisions
  implementation.md          Implementation details and type inventory
```

## Building

```bash
npm ci --ignore-scripts --prefix tests/interop  # Node 24, for independent interop
dotnet build -p:RestoreLockedMode=true
dotnet test
```

## .NET support policy

Andy.MCP targets `net10.0`, the active LTS release supported through November 2028.

- Building requires the **.NET 10 SDK**; running the assemblies needs the .NET 10 runtime.
  `global.json` rolls forward within the latest installed .NET 10 feature band.
- Package versions are managed centrally via [`Directory.Packages.props`](Directory.Packages.props)
  (Central Package Management).
- CI builds and tests `net10.0` on Linux, macOS, and Windows, collects code
  coverage, and **fails the build on any known-vulnerable dependency** (`dotnet list package
  --vulnerable`).

See the [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy) for details.

## Documentation

See the `docs/` directory:

- [Compliance matrix](docs/compliance.md) -- test-linked, revision-specific feature status
- [Requirements](docs/requirements.md) -- MCP spec requirement tracking
- [Design](docs/design.md) -- architecture, design decisions, extensibility
- [Implementation](docs/implementation.md) -- project structure, type inventory, test breakdown

## Project status

**2026-09-08: stable P1/P2 remediation implemented and verified.** The work includes full
negotiated schema coverage, strict bidirectional RPC, request lifetimes, high-level APIs,
stdio and HTTP JSON/SSE recovery, OAuth/HTTP security and same-commit release gates.
[Conformance](docs/conformance.md) includes pinned independent client/server interop,
official examples and per-surface line/branch coverage thresholds. The
[remediation plan](docs/remediation-plan.md) records the task-level evidence.

**Phase 7/8 full compliance remains in progress; the project remains alpha.** Open P3 work
covers experimental tasks (#49/#72), ecosystem integration (#19/#20/#21/#30) and the
full-compliance epics (#39/#68). Legacy HTTP+SSE and March 2025 batch reception are unsupported.
See [migration/API guidance](docs/high-level-apis.md), [HTTP security](docs/http-security.md),
[OAuth examples](docs/oauth.md) and [runtime maintenance](docs/package-maintenance.md).

## License

Licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE) for details.
