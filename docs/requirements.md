# Andy.MCP — Requirements

## Overview

Andy.MCP is a .NET 8 library implementing the Model Context Protocol (MCP) specification version 2025-06-18. It provides both client and server capabilities for building MCP-compatible applications within the Andy ecosystem.

## Specification Compliance

The library targets 100% compliance with the MCP 2025-06-18 specification:
https://modelcontextprotocol.io/specification/2025-06-18

### Protocol Requirements

| Requirement | Status |
|-------------|--------|
| JSON-RPC 2.0 message types (Request, Response, Notification) | Implemented |
| UTF-8 encoding, newline-delimited framing (stdio) | Implemented |
| Error codes: standard JSON-RPC + MCP-specific (-32002) | Implemented |
| `_meta` field with `progressToken` support | Implemented |
| Batch requests rejected (not supported by MCP) | Implemented |

### Lifecycle Requirements

| Requirement | Status |
|-------------|--------|
| `initialize` handshake with version negotiation | Implemented |
| `notifications/initialized` confirmation | Implemented |
| Capability negotiation (client + server) | Implemented |
| Supported versions: 2025-06-18, 2025-03-26, 2024-11-05 | Implemented |
| Graceful shutdown (stdio: stdin close → SIGTERM → SIGKILL) | Implemented |
| HTTP session termination via DELETE | Implemented |
| Request timeouts with cancellation | Implemented |

### Transport Requirements

| Requirement | Status |
|-------------|--------|
| stdio transport (newline-delimited JSON-RPC) | Implemented |
| Streamable HTTP transport (POST JSON/SSE, GET SSE) | Implemented |
| `Mcp-Session-Id` header management | Implemented |
| `MCP-Protocol-Version` header | Implemented |
| `Origin` header validation | Implemented |
| SSE event ID and `Last-Event-ID` resumability | Implemented |
| Backwards compatibility with 2024-11-05 HTTP+SSE | Partial (client fallback) |

### Server Feature Requirements

| Requirement | Status |
|-------------|--------|
| `tools/list` with pagination | Implemented |
| `tools/call` with input validation | Implemented |
| Tool annotations (readOnlyHint, destructiveHint, etc.) | Implemented |
| Structured output (`outputSchema` / `structuredContent`) | Types defined |
| `resources/list` with pagination | Implemented |
| `resources/read` (text and blob) | Implemented |
| `resources/templates/list` | Implemented |
| `resources/subscribe` / `resources/unsubscribe` | Implemented |
| `notifications/resources/updated` for subscribers | Implemented |
| `prompts/list` with pagination | Implemented |
| `prompts/get` with argument substitution | Implemented |
| `completion/complete` with max 100 values | Implemented |
| `logging/setLevel` with RFC 5424 levels | Implemented |
| `notifications/message` with level filtering | Implemented |

### Client Feature Requirements

| Requirement | Status |
|-------------|--------|
| `roots/list` response handler | Implemented |
| `notifications/roots/list_changed` | Implemented |
| `sampling/createMessage` handler | Implemented |
| Model preferences (hints, priorities) | Implemented |
| `elicitation/create` handler | Implemented |
| Accept/decline/cancel actions | Implemented |

### Content Type Requirements

| Requirement | Status |
|-------------|--------|
| TextContent | Implemented |
| ImageContent (base64) | Implemented |
| AudioContent (base64) | Implemented |
| ResourceLink (with title, size) | Implemented |
| EmbeddedResource (text/blob) | Implemented |
| ToolUseContent (for sampling conversations) | Implemented |
| ToolResultContent (for sampling conversations) | Implemented |
| Annotations (audience, priority, lastModified) | Implemented |
| `_meta` on all content blocks | Implemented |
| Icon type on tools, resources, prompts | Implemented |

### Authorization Requirements

| Requirement | Status |
|-------------|--------|
| OAuth 2.1 with PKCE (S256) | Implemented |
| Protected Resource Metadata discovery (RFC 9728) | Implemented |
| Authorization Server Metadata discovery (RFC 8414) | Implemented |
| Resource parameter (RFC 8707) | Implemented |
| Token refresh with rotation | Implemented |
| Dynamic Client Registration (RFC 7591/7592) | Implemented |
| Bearer token injection via DelegatingHandler | Implemented |

### Security Requirements

| Requirement | Status |
|-------------|--------|
| SSRF prevention (private IP blocking) | Implemented |
| DNS rebinding check | Implemented |
| Origin header validation | Implemented |
| Crypto-random session IDs | Implemented |
| Session-to-user binding | Implemented |
| Resource parameter validation (RFC 8707) | Implemented |

### Utility Requirements

| Requirement | Status |
|-------------|--------|
| `ping` (bidirectional, no capability needed) | Implemented |
| `notifications/cancelled` with CancellationToken bridge | Implemented |
| `notifications/progress` with IProgress bridge | Implemented |
| Cursor-based pagination (opaque, session-bound HMAC) | Implemented |

## Integration Requirements

| Requirement | Status |
|-------------|--------|
| Andy Engine IToolRegistry/IToolExecutor adapters | Open (#19) |
| Andy MCP Gateway discovery and proxy | Open (#20) |
| Andy Containers deployment | Open (#21) |

## Non-Functional Requirements

- Target framework: .NET 8.0
- Thread-safe for concurrent operations
- High-performance JSON serialization (System.Text.Json)
- ASP.NET Core integration via separate package (Andy.MCP.AspNetCore)
- Dependency injection support (Microsoft.Extensions.DependencyInjection)
- IHostedService for background connection management
- Configuration binding from appsettings.json
