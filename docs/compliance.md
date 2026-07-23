# Andy.MCP compliance matrix

This document states, honestly and with test evidence, what Andy.MCP implements against the
[Model Context Protocol](https://modelcontextprotocol.io) specification. Status is deliberately
granular rather than a single "compliant" claim.

- **Stable** — implemented, reachable through the high-level `McpClient`/`McpServer` APIs, and
  covered by tests.
- **Experimental** — implemented but the spec marks it experimental and/or the surface may change.
- **Partial** — core is implemented; specific sub-behaviors listed below are not yet complete.
- **Not implemented** — modeled or planned only.

Test links are relative to `tests/Andy.MCP.Tests/`.

## Protocol revisions

| Revision | Support | Notes |
|----------|---------|-------|
| **2025-11-25** | Stable (latest, negotiated) | Feature set below; revision-aware serialization strips newer fields from older sessions |
| 2025-06-18 | Stable (negotiated down) | Core wire types; 2025-11-25-only fields omitted |
| 2025-03-26 | Stable (negotiated down) | Core wire types |
| 2024-11-05 | Stable (negotiated down) | Core wire types |

Evidence: `Protocol/ProtocolRevisionTests.cs`, `Protocol/RevisionAwareJsonTests.cs`,
`Protocol/LifecycleTests.cs`.

## Feature matrix (MCP 2025-11-25)

| Feature | Status | Tests |
|---------|--------|-------|
| Lifecycle: initialize / initialized / enforcement / duplicate rejection | Stable | `Server/ServerLifecycleEnforcementTests.cs`, `Protocol/LifecycleTests.cs` |
| Bidirectional JSON-RPC (server-initiated ping/roots/sampling/elicitation, response correlation) | Stable | `Server/ServerInitiatedRequestTests.cs` |
| JSON-RPC error classification (parse / invalid request / invalid params / method not found) | Stable | `Server/ServerErrorClassificationTests.cs` |
| Concurrent request dispatch; `notifications/cancelled`; client cancel/timeout cleanup | Stable | `Server/ServerConcurrencyCancellationTests.cs`, `Client/ClientCancellationTimeoutTests.cs` |
| Progress (`notifications/progress`) end-to-end for tools | Partial — fluent `IProgress` only; attribute-based injection not wired | `Server/ProgressEndToEndTests.cs` |
| Tools: list / call; recursive JSON Schema input validation; schema self-validation | Stable | `Server/McpServerTests.cs`, `Server/JsonSchemaValidatorRecursiveTests.cs`, `Server/ToolSchemaRegistrationTests.cs` |
| Structured tool output (`outputSchema` + `structuredContent` enforcement) | Stable | `Server/StructuredOutputTests.cs` |
| Resources: list / read; subscribe / unsubscribe (sub-capability gated) | Stable | `Client/ClientHighLevelApiTests.cs`, `Server/Phase3Tests.cs` |
| Resource templates | Partial — advertised/listed; no template-handler binding or URI-template resolution | — |
| Prompts: list / get | Stable | `Server/McpServerTests.cs` |
| Completion (`completion/complete`) | Stable | `Client/ClientHighLevelApiTests.cs` |
| Sampling content scalar-or-array union; sampling capability sub-fields | Stable (models) | `Protocol/SamplingContentTests.cs`, `Protocol/SamplingCapabilityTests.cs` |
| Sampling tool-calling (`tools`/`toolChoice`) | Partial — modeled and revision-gated; no server-driven tool loop | `Protocol/RevisionAwareJsonTests.cs` |
| Elicitation: form + URL mode; typed schemas incl. enum/default | Stable | `Protocol/ElicitationSchemaTests.cs` |
| `_meta` / extension-data round-trip across request/result/object types | Stable | `Protocol/MetaRoundTripTests.cs` |
| Icons; Implementation description/websiteUrl | Stable (models) | `Protocol/ElicitationSchemaTests.cs`, `Protocol/RevisionAwareJsonTests.cs` |
| Capability objects (exact sub-capabilities; template-only resource advertisement) | Stable | `Server/ServerCapabilityExactnessTests.cs` |
| Experimental tasks (model + store; task-augmented `tools/call`; `tasks/*`) | Experimental — tools only; sampling/elicitation augmentation and HTTP session binding pending | `Server/TaskStoreTests.cs`, `Server/TaskAugmentedToolTests.cs` |

## Transports

| Transport | Status | Tests |
|-----------|--------|-------|
| stdio (LF framing, parse-error response, UTF-8) | Stable | `Transport/StdioServerTransportTests.cs`, `Transport/StdioClientTransportTests.cs` |
| stdio graceful shutdown (SIGTERM grace → SIGKILL) | Not implemented | — |
| Streamable HTTP server (POST JSON, response correlation, Content-Type/Accept validation) | Stable | `Transport/StreamableHttpServerTransportTests.cs`, `Transport/StreamableHttpValidationTests.cs` |
| Streamable HTTP client (negotiated `MCP-Protocol-Version`, session id, 404 handling) | Stable | `Transport/StreamableHttpClientTransportTests.cs` |
| SSE: GET stream, event ids | Partial | `Transport/SseParserTests.cs` |
| SSE: bounded replay, `Last-Event-ID` resumption, multiple streams, polling | Not implemented | — |
| 2024-11-05 HTTP+SSE fallback | Not implemented (not advertised as a separate transport) | — |

## Security

| Concern | Status | Tests |
|---------|--------|-------|
| Origin validation (403 on invalid present origin) | Stable (opt-in validator) | `Transport/StreamableHttpValidationTests.cs` |
| Session ↔ authenticated principal binding; cross-user rejection (403) | Stable | `Transport/StreamableHttpSessionBindingTests.cs` |
| Unguessable session ids (no identity leakage) | Stable | `Transport/StreamableHttpSessionBindingTests.cs` |
| Fail-closed request body (413) and session (503) limits | Stable | `Transport/StreamableHttpLimitsTests.cs` |
| OAuth: WWW-Authenticate parsing; correct 401 (no blind retry); safe concurrent refresh; expiry skew | Partial | `Auth/OAuthAuthorizationTests.cs` |
| OAuth: PRM/authorization-server discovery, DCR, Client ID Metadata Documents, scope step-up | Not implemented / partial | — |

See **[Security configuration](#security-configuration)** below for required application settings.

## Conformance

Golden fixtures (valid messages round-trip) and negative fixtures (malformed messages rejected):
`Conformance/MessageFixtureTests.cs`, `Conformance/ConformanceTests.cs`. CI collects code coverage
on Linux/macOS/Windows and fails on known-vulnerable dependencies.

Serialized output is validated against the **official MCP 2025-11-25 JSON schema** with a JSON
Schema 2020-12 validator: `Conformance/OfficialSchemaConformanceTests.cs` checks the library's
serialization of the core types (Tool, Implementation, content blocks, CallToolResult, Resource,
ResourceTemplate, Prompt, Icon, InitializeResult, ReadResourceResult, CreateMessageResult) against
the committed `schema/2025-11-25/schema.json` `$defs`, plus a negative case proving the gate rejects
non-conforming output. CI therefore fails if output violates the official schema.

Not yet in place: full-corpus official-schema validation for every advertised revision, interop
against independent reference implementations, and a coverage-threshold gate.

## Security configuration

Andy.MCP's HTTP handler defaults are permissive where the framework is expected to supply the
control (e.g. Origin, authentication). Configure the following for a secure deployment:

- **Origin** — set `StreamableHttpServerOptions.ValidateOrigin` to an allow-list; requests with a
  present, disallowed Origin receive `403`. (No validator = all origins allowed — do not use in
  production browser contexts.)
- **Authentication** — place the MCP endpoint behind ASP.NET Core authentication so
  `HttpContext.User` is populated; sessions then bind to the authenticated principal and reject
  cross-user reuse with `403`.
- **Resource limits** — `MaxRequestBodyBytes` (default 4 MB → `413`) and `MaxSessions` (default
  10,000 → `503`) bound resource use; tune for your deployment.
- **OAuth** — supply the resource, authorization-server metadata, and client id to
  `OAuthDelegatingHandler`; a `401` triggers a genuine refresh or surfaces the challenge, never a
  blind retry. Do **not** forward user tokens upstream (no token passthrough).

## Local development

For a local, non-browser deployment (stdio or trusted-host HTTP): authentication and Origin
validation may be omitted, but the resource limits above still apply. See the README quick-start.

## Migration (2025-06-18 → 2025-11-25)

The library now negotiates **2025-11-25** by default and negotiates down for older clients. If you
pinned behavior to 2025-06-18, no code change is required — older sessions still receive only the
fields their revision defines (revision-aware serialization). New surfaces (elicitation URL mode,
sampling capability sub-fields, icons, experimental tasks) are additive.
