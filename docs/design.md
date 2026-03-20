# Andy.MCP — Design

## Architecture

The library is organized into layered namespaces with clear separation of concerns:

```
┌─────────────────────────────────────────────┐
│  Andy.MCP.Client                            │  High-level client API
│  McpClient, IRootProvider, ISamplingHandler  │
├─────────────────────────────────────────────┤
│  Andy.MCP.Server                            │  High-level server API
│  McpServer, JsonSchemaValidator, Logging     │
├─────────────────────────────────────────────┤
│  Andy.MCP.Configuration                     │  DI, options, hosting
│  McpClientOptions, McpConnectionManager     │
├─────────────────────────────────────────────┤
│  Andy.MCP.Auth                              │  OAuth, DRC, security
│  OAuthClient, PkceHelper, SecurityHelpers   │
├─────────────────────────────────────────────┤
│  Andy.MCP.Transport                         │  Transport abstraction
│  IClientTransport, StdioClientTransport     │
│  StreamableHttpClientTransport, SSE         │
├─────────────────────────────────────────────┤
│  Andy.MCP.Protocol                          │  Protocol types
│  JsonRpcMessage, Content, Features          │
│  Lifecycle, Pagination, Utilities           │
└─────────────────────────────────────────────┘

┌─────────────────────────────────────────────┐
│  Andy.MCP.AspNetCore (separate project)     │
│  StreamableHttpHandler, MapMcp()            │
└─────────────────────────────────────────────┘
```

## Key Design Decisions

### 1. Protocol Layer: Records with System.Text.Json

All protocol types are implemented as C# `record` types with `[JsonPropertyName]` attributes for serialization. This provides:
- Immutability by default
- Value-based equality
- `with` expressions for creating variants
- Compact JSON output with `JsonIgnoreCondition.WhenWritingNull`

**Polymorphic deserialization** uses two strategies:
- Content blocks: `[JsonPolymorphic]` with `[JsonDerivedType]` attributes and `type` discriminator
- ResourceContents: Custom `JsonConverter` that checks for `text` vs `blob` field presence
- JSON-RPC messages: Custom `JsonRpcMessageConverter` that checks for `id`, `method`, `result`, `error` fields

### 2. Transport Layer: Message-Oriented Abstraction

Transports are **message-oriented**, not request/response-oriented. The `ITransport` interface provides:
- `SendAsync(JsonRpcMessage)` — write a message
- `IAsyncEnumerable<JsonRpcMessage> Messages` — read incoming messages
- `IsConnected` / `Disconnected` event

The session layer (McpClient/McpServer) handles request/response correlation via `PendingRequestTracker`. This keeps transports simple and reusable.

**Channel-based async queues**: Both stdio and HTTP transports use `System.Threading.Channels.Channel<T>` for thread-safe async message passing between read/write loops.

### 3. Client API: Static Factory with Auto-Initialization

```csharp
await using var client = await McpClient.ConnectAsync(transport, options);
```

`ConnectAsync` is a static factory that:
1. Creates the transport connection
2. Sends `initialize` request
3. Validates version negotiation
4. Sends `notifications/initialized`
5. Returns a ready-to-use client

This prevents partially-initialized clients from being used.

### 4. Server API: Fluent Registration with Capability Auto-Detection

```csharp
var server = new McpServer(transport);
server.AddTool("name", "desc", handler)
      .AddResource("uri", "name", handler)
      .AddPrompt("name", "desc", handler)
      .WithLogging();
await server.RunAsync();
```

Capabilities are auto-detected from registrations:
- Any tools registered → `tools` capability declared
- Any resources registered → `resources` capability (with `subscribe: true`)
- Any prompts registered → `prompts` capability
- Any completions registered → `completions` capability
- `WithLogging()` called → `logging` capability

### 5. Pagination: HMAC-Signed Session-Bound Cursors

Cursors encode an offset as base64 JSON with an HMAC-SHA256 signature using a session-specific key. This prevents:
- Cross-session cursor reuse
- Cursor tampering
- Offset guessing

### 6. Client Features: Handler-Based Capability Detection

```csharp
var options = new McpClientOptions
{
    RootProvider = new StaticRootProvider(...),     // → roots capability
    SamplingHandler = new MySamplingHandler(),       // → sampling capability
    ElicitationHandler = new MyElicitHandler()       // → elicitation capability
};
```

Client capabilities are auto-declared based on which handlers are provided.

### 7. Separate ASP.NET Core Package

The `Andy.MCP.AspNetCore` project contains the server-side HTTP transport to avoid forcing ASP.NET Core dependencies on all consumers. The main `Andy.MCP` library uses only BCL types and `Microsoft.Extensions.*` abstractions.

### 8. Security Architecture

- **PKCE**: Cryptographically random code verifiers with S256 challenges
- **Token storage**: Pluggable `ITokenStore` interface (in-memory default)
- **SSRF prevention**: IP range blocking with async DNS rebinding checks
- **Session IDs**: `RandomNumberGenerator` with optional user binding
- **Origin validation**: Configurable per-server or match-server mode

## Error Handling Strategy

MCP defines two error channels for tools:
1. **Protocol errors**: JSON-RPC error response (e.g., unknown tool → `-32602`)
2. **Tool execution errors**: Success response with `isError: true`

The server handles this by:
- Validating inputs before calling the handler → protocol error
- Catching handler exceptions → wrapping in `CallToolResult.Error()` → isError response

## Threading Model

- `McpClient`: Single message loop task reads from transport, correlates responses via `PendingRequestTracker`, dispatches server requests to background tasks
- `McpServer`: Single message loop reads from transport, dispatches to handlers, sends responses
- `PendingRequestTracker`: `ConcurrentDictionary`-based, thread-safe for concurrent requests
- `McpSession`: State transitions via `Interlocked.CompareExchange`
- Transports: `Channel<T>` for write queuing, ensuring message ordering

## Extensibility Points

| Extension Point | Interface/Type |
|-----------------|---------------|
| Custom transport | `IClientTransport` / `IServerTransport` |
| Token storage | `ITokenStore` |
| Root provider | `IRootProvider` |
| Sampling handler | `ISamplingHandler` |
| Elicitation handler | `IElicitationHandler` |
| Tool handlers | `Func<JsonElement?, CancellationToken, Task<CallToolResult>>` |
| Resource handlers | `Func<string, CancellationToken, Task<ResourceContents>>` |
| Prompt handlers | `Func<string, IDictionary<string,string>?, CancellationToken, Task<GetPromptResult>>` |
| Completion handlers | `Func<string, IDictionary<string,string>?, CancellationToken, Task<CompletionValues>>` |
