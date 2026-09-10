# Andy.MCP

> **MCP 2025-11-25:** library implementation and Phase 7/8 compliance audit completed. See the [verified feature and transport matrix](docs/compliance.md) for supported revisions and host responsibilities. NuGet prereleases remain identified by their package versions; MCP tasks remain protocol-experimental.

## Overview

Andy.MCP is a .NET 10 library implementing the [Model Context Protocol](https://modelcontextprotocol.io/) (MCP). It negotiates **2025-11-25** (the library’s default revision) by default and negotiates down to 2025-06-18 and 2024-11-05 over stdio. Streamable HTTP supports 2025-11-25 and 2025-06-18. Legacy HTTP+SSE is available through an explicit compatibility client transport; automatic HTTP fallback and the mandatory JSON-RPC batching in 2025-03-26 are not implemented. It provides both client and server capabilities for building MCP-compatible applications.

Feature support is granular (stable / experimental / partial) and documented with test evidence in the **[compliance matrix](docs/compliance.md)** — please read it before relying on any specific capability.

The library is designed for integration with the Andy ecosystem (Andy Engine, Andy MCP Gateway, Andy Containers) but can be used independently in any .NET application.

## Integration update — 2026-09-08

Schema validation uses JsonSchema.Net 9.3.0 to match Andy CLI. Protocol and remote
tool schemas use local registries, preserving offline validation and isolation when
servers reuse schema IDs. The broader Engine/Tools integration remains in progress (#19).

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
- [Experimental tasks](docs/tasks.md): tools, sampling and elicitation; deferred results, cancellation, related input, pagination and injectable stores
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

> Task lifecycle implementation is verified and remains experimental in the MCP specification. Model/tool execution, user approval and identity-provider integration belong to the application. See the [evidence-backed matrix](docs/compliance.md).

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

**2026-09-08: experimental task lifecycle implemented and verified (#49/#72).**
The 2,894-test suite covers both directions, HTTP input flows, ownership and disk-backed
store recreation. Tasks remain experimental in the MCP specification.

**Phase 7/8 library compliance audit completed on 2026-09-09.** All child implementations
are merged, supported capabilities/revisions have linked conformance evidence, and release
requires same-commit platform, interoperability, coverage, security, API and package gates.
Engine, gateway and container integration (#19/#20/#21), including ecosystem epic #30,
are complete. The gateway adapter API and both proxy transports are verified against the published library. Automatic legacy HTTP fallback and March 2025 batch reception are unsupported; experimental tasks retain their upstream status.
See [migration/API guidance](docs/high-level-apis.md), [HTTP security](docs/http-security.md),
[OAuth examples](docs/oauth.md) and [runtime maintenance](docs/package-maintenance.md).

## License

Licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE) for details.

### Container MCP provisioning — 2026-09-09

`Andy.MCP.Containers` provides `IContainerMcpServerProvider` over the current Andy Containers
REST API. `ProvisionAsync` maps template/provider/workspace/owner, resources, environment and
expiry settings, waits for Running and a successful MCP handshake/ping, and cleans up failed
or cancelled provisioning. `ListRunningAsync` supports owner/workspace/template filters and
pagination, excluding containers without the configured published MCP port.

Register `AddContainerMcpServers(options, sp => authenticatedContainersHttpClient)` alongside
logging. The caller owns this HTTP client. API credentials are never forwarded to MCP endpoints.
The default endpoint uses the API host, the template's published MCP port (3000), plain HTTP
and `/mcp`. Set `ResolveEndpoint` for remote Docker hosts, HTTPS or provider-specific routing.
Use `OpenSessionAsync` and dispose its returned lease to protect active clients from idle
cleanup. Only containers provisioned by this provider instance are eligible for idle cleanup;
server-side `ExpiresAfter` remains useful across application restarts. Optional register and
unregister callbacks connect provisioning to a gateway or other catalog.

The [container example](examples/Andy.MCP.ContainerServer) includes a runnable .NET 10 MCP
HTTP server, Dockerfile and Andy Containers YAML template. Build from the repository root:
`docker build -f examples/Andy.MCP.ContainerServer/Dockerfile -t andy-mcp-example:local .`.
Import the template through your existing container catalog workflow. The example is an
anonymous echo server; production endpoints should use the existing MCP authorization setup.
`AddContainerMcpPool` adds bounded, instance-local pooling: pre-warm `MinimumSize`, rent
exclusive capacity with `RentAsync`, grow on demand to `MaximumSize`, and reclaim excess
idle containers after `IdleTimeout`. Capacity exhaustion returns a clear error. Returning a
lease creates a fresh MCP session; container filesystem/application state persists, so use
separate pools for callers that need isolation. Host shutdown destroys the pool's containers.
Durable ownership across provider restarts is not implemented; use control-plane TTLs.

```csharp
services.AddContainerMcpPool(new ContainerMcpPoolOptions
{
    TemplateCode = "mcp-server",
    ProvisionOptions = new() { Name = "mcp-worker", ExpiresAfter = TimeSpan.FromHours(2) },
    MinimumSize = 1,
    MaximumSize = 4,
    IdleTimeout = TimeSpan.FromMinutes(5)
});
// Start the host, then resolve ContainerMcpPool from its services.
await using var lease = await pool.RentAsync(cancellationToken);
var result = await lease.Client.CallToolAsync("echo", ct: cancellationToken);
```

Image rebuild policy belongs to the Andy Containers template catalog. Its dependency records
support `auto_update` and `update_policy` (`manual`, `patch`, `minor`, `major`, or
`security-only`). The local pre-built example is updated by rebuilding its Docker image;
configure catalog dependency policies when publishing a managed template.
### Gateway registry integration — 2026-09-09

`Andy.MCP.Gateway` connects to the current Andy MCP Gateway registry's
`/api/GatewayRegistry` API. `IMcpGatewayClient` supports list/get/search/create/update/delete,
HTTP status errors and optional registry bearer-token refresh (one retry after 401).
`AddMcpGatewayDiscovery()` connects active entries, probes MCP ping, removes inactive or
unhealthy clients, and retries them on later refreshes. Registry outages use a bounded cache.

```csharp
services.AddMcpClient(_ => { });
services.AddMcpGateway(new McpGatewayOptions
{
    RegistryUri = new Uri("https://registry.example"),
    RefreshInterval = TimeSpan.FromSeconds(30)
});
services.AddMcpGatewayDiscovery();
```

Import `Andy.MCP.Configuration` and `Andy.MCP.Gateway`. Discovery registers connections as
`gateway:<registration-id>`; use those names with `IMcpConnectionManager`. Alternatively,
`AddGatewayServer("name", "https://registry.example", "registered-name")` resolves exactly
one active registration at startup. MCP connections use the registered endpoint directly;
registry credentials are never forwarded to it. Configure endpoint authentication separately.

### Gateway adapter proxy — 2026-09-09

The typed client also supports `/api/adapters` list/enabled/name/search, CRUD, individual and
bulk health checks, reload, export and import. Set `UseAdapterProxy = true` to discover
healthy enabled adapters through the proxy instead of connecting to upstream URLs. The gateway
exposes this adapter contract through its adapter integration
([gateway PR30](https://github.com/rivoli-ai/andy-mcp-gateway/pull/30)); existing registry-only
installations continue to use the default mode.

```csharp
services.AddMcpGateway(new McpGatewayOptions
{
    RegistryUri = new Uri("https://gateway.example"),
    UseAdapterProxy = true,
    TokenProvider = (refresh, ct) => tokenSource.GetAccessTokenAsync(refresh, ct)
});
services.AddMcpGatewayDiscovery();
```

`McpGatewayTransport` targets `/adapters/{name}/mcp` or, for `McpAdapterType.Sse`,
`/adapters/{name}/sse`. Gateway credentials are required by default and refreshed once after
401; caller cancellation and request deadlines are honored. Registry mode retains its
optional authentication behavior. Gateways distinguish user access from administrator-only
configuration/export operations. Never configure a gateway token as an upstream credential.

`LegacySseClientTransport` supports the older SSE endpoint event plus message POST pattern.
It rejects message endpoints on another origin and closes on stream failure; it does not
silently fall back from Streamable HTTP or replay tool calls. Use explicit transport selection
and connection-manager recovery when compatibility with older servers is required.

Container cleanup polling also probes owned active sessions: a stopped/crashed container or
failed MCP health check closes its tracked clients. Transport disconnect releases the active
lease guard. Restarted containers can acquire a fresh session; stale clients are not reused.
### Shared tool execution and connection recovery — 2026-09-09

The published `Andy.Tools.Mcp` adapter (2026.9.9-rc.103 or later) supplies MCP tools through
Andy.Tools' existing `IToolRegistry` and `IToolExecutor`, which Andy Engine already consumes.
It supports full-schema input validation, structured/error payload retention, conservative
permissions, manual/notification refresh and cancellation statistics. Registry filtering and
executor running-call tracking use the existing framework interfaces.

`McpClientOptions.AutoReconnect = true` now activates recovery after a connected server
reports a transport disconnect. `ReconnectPolicy` bounds attempts and delays (fixed, linear,
exponential, or exponential with jitter). A recovered client replaces the disconnected one;
shared tool discovery observes the replacement. Explicit removal/disposal cancels and drains
recovery, preventing removed servers from being recreated. Initial startup connection failures
are logged; they do not silently turn a failed AddServerAsync into a later connection.
The manager propagates caller cancellation during connection and discovery.

### Ecosystem integration completion — 2026-09-09

- [x] Engine: shared `Andy.Tools.Mcp` adapters and real SimpleAgent tool execution (#19).
- [x] Gateway: authenticated adapter management, Streamable HTTP/legacy SSE proxies,
  session ownership and health-driven discovery recovery (#20).
- [x] Containers: provisioning, lifecycle health, reconnect and bounded warm pools (#21).

MCP PR130 passed 2,936 tests and the platform, interoperability, coverage, security,
API and package gates. Gateway acceptance uses published `Andy.MCP` and
`Andy.MCP.AspNetCore` 2026.9.10-rc.183 and 45 real-server/unit tests. Gateway authentication
requires a configured Azure AD/Andy Auth authority; in-memory sessions require sticky routing
across replicas. See the gateway [operator guide](https://github.com/rivoli-ai/andy-mcp-gateway/blob/main/docs/mcp-adapters.md).
