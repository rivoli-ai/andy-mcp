# Andy.MCP — Implementation

## Project Structure

```
andy-mcp/
├── src/
│   ├── Andy.MCP/                          # Core library (net8.0)
│   │   ├── Protocol/                      # Protocol types and serialization
│   │   │   ├── JsonRpcMessage.cs          # Request, Response, Notification
│   │   │   ├── JsonRpcMessageConverter.cs # Polymorphic deserialization
│   │   │   ├── JsonRpcError.cs            # Error type with factory methods
│   │   │   ├── RequestId.cs              # String|number union type
│   │   │   ├── McpErrorCodes.cs          # Error code constants
│   │   │   ├── McpJsonDefaults.cs        # Shared serializer options
│   │   │   ├── Meta.cs                   # _meta field with progressToken
│   │   │   ├── Content.cs               # 7 content block types (polymorphic)
│   │   │   ├── Annotations.cs           # Annotations + Role enum
│   │   │   ├── ResourceContents.cs      # Text/Blob resource contents
│   │   │   ├── Features.cs             # Tool, Resource, Prompt, ToolChoice types
│   │   │   ├── Icon.cs                 # Icon type for display metadata
│   │   │   ├── Lifecycle.cs            # Initialize types, capabilities
│   │   │   ├── Pagination.cs           # Cursor-based pagination with HMAC
│   │   │   ├── Utilities.cs            # Ping, Cancel, Progress + McpMethods
│   │   │   └── JsonRpcParseException.cs
│   │   ├── Transport/                    # Transport implementations
│   │   │   ├── ITransport.cs            # IClientTransport, IServerTransport
│   │   │   ├── StdioClientTransport.cs  # Process-based stdio client
│   │   │   ├── StdioServerTransport.cs  # Console stdin/stdout server
│   │   │   ├── StreamableHttpClientTransport.cs  # HTTP+SSE client
│   │   │   └── Sse/                     # Server-Sent Events
│   │   │       ├── SseEvent.cs
│   │   │       ├── SseParser.cs         # Streaming async parser
│   │   │       └── SseWriter.cs
│   │   ├── Client/                       # High-level client
│   │   │   ├── McpClient.cs             # Main client + DTOs
│   │   │   └── ClientFeatures.cs        # Roots, Sampling, Elicitation
│   │   ├── Server/                       # High-level server
│   │   │   ├── McpServer.cs             # Main server with fluent API
│   │   │   ├── JsonSchemaValidator.cs   # Input validation
│   │   │   ├── ResourceSubscriptionManager.cs
│   │   │   ├── CompletionProvider.cs    # Completion types
│   │   │   └── McpLogLevel.cs           # RFC 5424 log levels
│   │   ├── Auth/                         # Authentication & security
│   │   │   ├── OAuthClient.cs           # OAuth 2.1 with discovery
│   │   │   ├── OAuthTypes.cs            # Metadata, token types
│   │   │   ├── OAuthDelegatingHandler.cs # HttpClient auth handler
│   │   │   ├── PkceHelper.cs            # PKCE S256 generation
│   │   │   ├── ITokenStore.cs           # Pluggable token storage
│   │   │   ├── DynamicClientRegistration.cs # RFC 7591/7592
│   │   │   └── SecurityHelpers.cs       # SSRF, sessions, origin
│   │   └── Configuration/               # DI and hosting
│   │       ├── McpClientOptions.cs      # Config binding
│   │       ├── McpConnectionManager.cs  # Multi-server management
│   │       ├── McpHostedService.cs      # IHostedService
│   │       └── ServiceCollectionExtensions.cs
│   └── Andy.MCP.AspNetCore/             # ASP.NET Core integration
│       ├── StreamableHttpServerTransport.cs  # HTTP handler + sessions
│       └── McpEndpointExtensions.cs     # MapMcp() endpoint mapping
├── tests/
│   └── Andy.MCP.Tests/                   # 450 tests
│       ├── Protocol/                     # Type serialization, parsing
│       ├── Transport/                    # SSE, stdio, HTTP transport
│       ├── Client/                       # McpClient, client features
│       ├── Server/                       # McpServer, validation, Phase 3
│       ├── Auth/                         # OAuth, PKCE, security, DRC
│       ├── Configuration/               # Options, DI, connection manager
│       ├── Conformance/                 # End-to-end Everything Server tests
│       └── InMemoryTransport.cs         # Test helper for in-process testing
├── examples/
│   └── Andy.MCP.Examples/               # Getting-started examples
│       ├── Program.cs                   # Entry point (dispatches to examples)
│       ├── SimpleServer.cs             # Minimal stdio server
│       ├── SimpleClient.cs             # Connects to server, calls tools
│       └── InProcessDemo.cs            # Client↔Server in same process
└── docs/
    ├── requirements.md                  # This file's sibling
    ├── design.md                        # Architecture and decisions
    └── implementation.md                # This file
```

## Implementation Status

### Completed (Phases 1–5)

| Phase | Stories | Tests |
|-------|---------|-------|
| Phase 1: Core Protocol | #1 JSON-RPC, #2 Lifecycle, #3 Pagination, #4 Content, #15 Utilities, #22 Client, #23 Server, #24 DI | 179 |
| Phase 2: Transports | #5 stdio, #6 Streamable HTTP | 49 |
| Phase 3: Server Features | #7 Resources, #8 Tools, #9 Prompts, #10 Completions, #11 Logging | 22 |
| Phase 4: Client Features | #12 Roots, #13 Sampling, #14 Elicitation | 17 |
| Phase 5: Security & Auth | #16 OAuth, #17 DRC, #18 Security | 49 |
| Conformance & Examples | #32 Everything Server, #33 Conformance, #34 Examples, #35 Protocol completeness | 56 |
| OpenTelemetry & Attributes | #36 Tracing, #37 Attribute registration | 34 |
| **Total** | **33 stories closed** | **450 tests** |

### Open

| Story | Phase | Description |
|-------|-------|-------------|
| #19 | Phase 6 | Andy Engine IToolRegistry/IToolExecutor adapters |
| #20 | Phase 6 | Andy MCP Gateway discovery, proxy, health |
| #21 | Phase 6 | Andy Containers MCP server deployment |

## NuGet Packages

| Package | Description | Dependencies |
|---------|-------------|--------------|
| `Andy.MCP` | Core library — protocol, transports, client, server, auth, config | Microsoft.Extensions.Logging.Abstractions, Hosting.Abstractions, Options, Configuration.Binder |
| `Andy.MCP.AspNetCore` | ASP.NET Core HTTP server transport | Andy.MCP, Microsoft.AspNetCore.App (FrameworkReference) |

## Type Inventory

### Protocol Types (38 types)

**JSON-RPC**: `JsonRpcMessage`, `JsonRpcRequest`, `JsonRpcResponse`, `JsonRpcNotification`, `JsonRpcError`, `RequestId`, `JsonRpcMessageConverter`, `NullableRequestIdJsonConverter`, `RequestIdJsonConverter`, `JsonRpcParseException`

**Content** (9 types): `Content` (abstract), `TextContent`, `ImageContent`, `AudioContent`, `ResourceLink`, `EmbeddedResource`, `ToolUseContent`, `ToolResultContent`, `Annotations`, `Role`

**Features** (15 types): `Tool`, `ToolAnnotations`, `ToolChoice`, `CallToolRequest`, `CallToolResult`, `Resource`, `ResourceTemplate`, `ReadResourceResult`, `Prompt`, `PromptArgument`, `PromptMessage`, `GetPromptResult`, `Icon`, `TextResourceContents`, `BlobResourceContents`

**Lifecycle** (10 types): `InitializeParams`, `InitializeResult`, `Implementation`, `ClientCapabilities`, `ServerCapabilities`, `EmptyCapability`, `ListChangedCapability`, `RootsCapability`, `ResourcesCapability`

**Utilities** (8 types): `Meta`, `CancelledParams`, `ProgressParams`, `McpMethods`, `McpMessages`, `McpErrorCodes`, `McpJsonDefaults`, `PendingRequestTracker`, `PendingRequest`, `McpProgress`

**Pagination** (5 types): `PaginatedRequest`, `PaginatedResult`, `PaginationHelper`, `PaginatedSlice<T>`, `McpPaginationException`

### Transport Types (9 types)

`ITransport`, `IClientTransport`, `IServerTransport`, `TransportDisconnectedEventArgs`, `StdioClientTransport`, `StdioClientTransportOptions`, `StdioServerTransport`, `StreamableHttpClientTransport`, `StreamableHttpClientTransportOptions`, `McpSessionExpiredException`, `SseEvent`, `SseParser`, `SseWriter`

### Client Types (15 types)

`McpClient`, `McpClientOptions`, `McpException`, `LogMessageEventArgs`, `ToolsListResult`, `ResourcesListResult`, `ResourceTemplatesListResult`, `PromptsListResult`, `IRootProvider`, `StaticRootProvider`, `Root`, `ListRootsResult`, `ISamplingHandler`, `CreateMessageRequest`, `CreateMessageResult`, `SamplingMessage`, `ModelPreferences`, `ModelHint`, `IElicitationHandler`, `ElicitRequest`, `ElicitResult`

### Server Types (12 types)

`McpServer`, `McpServerOptions`, `JsonSchemaValidator`, `ResourceSubscriptionManager`, `CompletionRegistration`, `CompletionValues`, `CompletionRequest`, `CompletionRef`, `CompletionArgument`, `CompletionContext`, `CompletionResult`, `CompletionData`, `McpLogLevel`, `SetLogLevelParams`, `LogMessageParams`

### Auth Types (12 types)

`OAuthClient`, `ProtectedResourceMetadata`, `AuthorizationServerMetadata`, `OAuthTokenResponse`, `OAuthTokens`, `ITokenStore`, `InMemoryTokenStore`, `PkceHelper`, `OAuthDelegatingHandler`, `DynamicClientRegistrationClient`, `ClientRegistrationRequest`, `ClientRegistrationResponse`, `SecurityHelpers`, `UrlValidationResult`

### Configuration Types (7 types)

`McpClientOptions` (config), `ImplementationConfig`, `McpServerConfig`, `ReconnectPolicyConfig`, `IMcpConnectionManager`, `McpConnectionManager`, `McpHostedService`, `ServiceCollectionExtensions`, `McpClientOptionsExtensions`

### Diagnostics (1 type)

`McpDiagnostics` (static `ActivitySource` named `"Andy.MCP"` for OpenTelemetry tracing)

### Attribute Registration (5 types)

`McpToolAttribute`, `McpParamAttribute`, `McpResourceAttribute`, `McpPromptAttribute`, `AttributeDiscovery` (static extension methods: `AddToolsFromType<T>()`, `AddToolsFromAssembly()`)

### ASP.NET Core Types (4 types)

`StreamableHttpHandler`, `StreamableHttpSession`, `StreamableHttpServerOptions`, `McpEndpointExtensions`

## Test Organization

| Test File | Area | Test Count |
|-----------|------|------------|
| `Protocol/JsonRpcMessageTests.cs` | JSON-RPC discrimination, serialization | 25 |
| `Protocol/RequestIdTests.cs` | String/number union, equality | 21 |
| `Protocol/ContentTests.cs` | All 7 content types, polymorphism | 18 |
| `Protocol/AnnotationsTests.cs` | Audience, priority, role enum | 12 |
| `Protocol/ResourceContentsTests.cs` | Text/blob discrimination | 11 |
| `Protocol/LifecycleTests.cs` | Session state, capabilities, version negotiation | 35 |
| `Protocol/PaginationTests.cs` | Cursor HMAC, page slicing, IAsyncEnumerable | 21 |
| `Protocol/PendingRequestTrackerTests.cs` | Correlation, cancel, timeout, progress | 16 |
| `Protocol/UtilitiesTests.cs` | Ping, cancel, progress messages | 11 |
| `Protocol/MetaTests.cs` | Progress tokens, extensions | 5 |
| `Protocol/McpErrorCodesTests.cs` | Error code values | 4 |
| `Protocol/ProtocolCompletenessTests.cs` | New types: ToolUse, Icon, _meta, ToolChoice | 23 |
| `Transport/SseParserTests.cs` | SSE parsing + writing | 23 |
| `Transport/StdioClientTransportTests.cs` | Process-based integration | 8 |
| `Transport/StdioServerTransportTests.cs` | stdin/stdout framing | 10 |
| `Transport/StreamableHttpClientTransportTests.cs` | HTTP+SSE mock handler | 8 |
| `Client/McpClientTests.cs` | Initialization, tool calls, events | 9 |
| `Client/ClientFeaturesTests.cs` | Roots, sampling, elicitation | 17 |
| `Server/McpServerTests.cs` | Registration, dispatch, capabilities | 14 |
| `Server/Phase3Tests.cs` | Validation, subscriptions, completions, logging | 22 |
| `Auth/Phase5Tests.cs` | PKCE, OAuth types, tokens, DRC, security | 49 |
| `Configuration/McpClientOptionsTests.cs` | Config binding, fluent API | 8 |
| `Configuration/McpConnectionManagerTests.cs` | Multi-server lifecycle | 10 |
| `Configuration/ServiceCollectionExtensionsTests.cs` | DI registration | 4 |
| `Conformance/ConformanceTests.cs` | End-to-end Everything Server | 33 |
| `Protocol/McpDiagnosticsTests.cs` | OpenTelemetry tracing | 12 |
| `Server/AttributeRegistrationTests.cs` | Attribute-based registration | 22 |

## Comparison with Official C# SDK

| Area | Official `csharp-sdk` | Andy.MCP |
|------|----------------------|----------|
| Protocol types | Complete | Complete (aligned via #35) |
| Content types | 7 types | 7 types (matching) |
| Transports | stdio, HTTP, SSE | stdio, HTTP, SSE |
| Server API | DI + `[McpServerTool]` attributes | Fluent `AddTool()` + `[McpTool]` attributes (#37) |
| Client API | `McpClientFactory.CreateAsync()` | `McpClient.ConnectAsync()` |
| Auth | OAuth samples | Full OAuth/DRC/PKCE/SSRF |
| Testing | 800+ lines integration | 416 tests, Everything Server |
| ASP.NET Core | Separate package | Separate package |
| OpenTelemetry | Built-in | Planned (#36) |
| Attribute registration | `[McpServerTool]` | Planned (#37) |
| Tasks (experimental) | Supported | Not implemented |

## CI/CD

- **CI workflow**: Build + test on Ubuntu, Windows, macOS
- **Build and Release**: Pack NuGet, publish on tag
- **Target**: .NET 8.0
- **Test runner**: xUnit 2.5.3 with Coverlet
