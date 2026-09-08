# MCP compliance matrix — 2026-09-08

Andy.MCP remains **alpha**. Stable means a reachable, tested library surface; it does not
certify every application behavior or declare full MCP compliance. Experimental tasks and
the full-compliance epics #39/#68 remain open. Application policy, model execution and user
approval belong to the host.

## Negotiated revisions

| Revision | Stdio | Streamable HTTP | Evidence / limitation |
|---|---|---|---|
| 2025-11-25 | Supported; default | Supported | [Complete schema corpus](../tests/Andy.MCP.Tests/Conformance/CompleteProtocolSchemaTests.cs), [real HTTP matrix](../tests/Andy.MCP.Tests/Transport/HttpRevisionIntegrationTests.cs) |
| 2025-06-18 | Supported | Supported | [Schema corpus](../tests/Andy.MCP.Tests/Conformance/CompleteProtocolSchemaTests.cs) and [HTTP matrix](../tests/Andy.MCP.Tests/Transport/HttpRevisionIntegrationTests.cs); newer fields are removed or rejected |
| 2024-11-05 | Supported | Unsupported | [Revision replies](../tests/Andy.MCP.Tests/Client/ClientRevisionReplyTests.cs); legacy HTTP+SSE is absent |
| 2025-03-26 | Unsupported | Unsupported | Receiving batches is mandatory in this revision and absent; its descriptor is retained only for conversion/audit |

Every definition in the three negotiated revisions is exercised against frozen official
schemas. [Revision conversion](../tests/Andy.MCP.Tests/Protocol/RevisionAwareJsonTests.cs)
checks newer-field exclusion. Negotiation support is not a full-feature certification.

## Feature availability by revision

Availability still requires the negotiated capability. “Absent” fields are omitted or rejected
under that revision; unknown vendor extension data is retained independently.

| Feature | 2024-11-05 | 2025-06-18 | 2025-11-25 | Evidence |
|---|---|---|---|---|
| Core tools/resources/prompts, roots and completions | Available; completions has no flag | Available | Available | [Boundary/legacy completion tests](../tests/Andy.MCP.Tests/Conformance/ProtocolBoundaryTests.cs) |
| Tool annotations, titles, structured outputs, resource links and audio | Absent | Available | Available | [Revision serialization](../tests/Andy.MCP.Tests/Protocol/RevisionAwareJsonTests.cs) |
| Basic sampling | One text/image block | One text/image/audio block | Scalar or array of allowed blocks | [Sampling tests](../tests/Andy.MCP.Tests/Protocol/SamplingContentTests.cs), [revision replies](../tests/Andy.MCP.Tests/Client/ClientRevisionReplyTests.cs) |
| Sampling tools/toolChoice and context sub-capability | Absent | Absent | Available models and capability checks | [Peer contracts](../tests/Andy.MCP.Tests/Server/HighLevelContractTests.cs) |
| Form elicitation | Absent | Available with legacy enums/defaults | Available including richer enums/defaults | [Typed/legacy parameters](../tests/Andy.MCP.Tests/Protocol/TypedParameterTests.cs) |
| URL elicitation, icons and extended implementation metadata | Absent | Absent | Available | [Complete definition corpus](../tests/Andy.MCP.Tests/Conformance/CompleteProtocolSchemaTests.cs) |
| Task augmentation | Absent | Absent | Experimental/partial | [Task tests](../tests/Andy.MCP.Tests/Server/TaskAugmentedToolTests.cs) |

## Stable features and experimental boundaries

| Surface | Status / boundary | Passing test evidence |
|---|---|---|
| Lifecycle and bidirectional RPC | Stable | [ServerLifecycleEnforcementTests](../tests/Andy.MCP.Tests/Server/ServerLifecycleEnforcementTests.cs), [ServerInitiatedRequestTests](../tests/Andy.MCP.Tests/Server/ServerInitiatedRequestTests.cs) |
| Strict envelopes, error channels and standard-message shape validation | Stable | [ProtocolBoundaryTests](../tests/Andy.MCP.Tests/Conformance/ProtocolBoundaryTests.cs), [ServerErrorClassificationTests](../tests/Andy.MCP.Tests/Server/ServerErrorClassificationTests.cs) |
| Concurrent requests, cancellation, progress, idle and absolute deadlines | Stable | [ServerConcurrencyCancellationTests](../tests/Andy.MCP.Tests/Server/ServerConcurrencyCancellationTests.cs), [ClientCancellationTimeoutTests](../tests/Andy.MCP.Tests/Client/ClientCancellationTimeoutTests.cs), [ProgressEndToEndTests](../tests/Andy.MCP.Tests/Server/ProgressEndToEndTests.cs) |
| Tools, full registration metadata, JSON Schema 2020-12 inputs and structured outputs | Stable | [ToolSchemaRegistrationTests](../tests/Andy.MCP.Tests/Server/ToolSchemaRegistrationTests.cs), [StructuredOutputTests](../tests/Andy.MCP.Tests/Server/StructuredOutputTests.cs) |
| Resource reads, multi-content results, subscriptions and change notifications | Stable | [ClientHighLevelApiTests](../tests/Andy.MCP.Tests/Client/ClientHighLevelApiTests.cs), [HighLevelContractTests](../tests/Andy.MCP.Tests/Server/HighLevelContractTests.cs) |
| RFC6570 resource-template matching and bound handlers | Stable | [ResourceTemplateHandlerTests](../tests/Andy.MCP.Tests/Server/ResourceTemplateHandlerTests.cs), [UriTemplateTests](../tests/Andy.MCP.Tests/Server/UriTemplateTests.cs) |
| Prompts with required arguments passed literally to handlers | Stable | [HighLevelContractTests](../tests/Andy.MCP.Tests/Server/HighLevelContractTests.cs) |
| Completions, explicit pagination and custom request/notification APIs | Stable | [ClientHighLevelApiTests](../tests/Andy.MCP.Tests/Client/ClientHighLevelApiTests.cs), [ExtensionApiTests](../tests/Andy.MCP.Tests/Server/ExtensionApiTests.cs) |
| Roots, sampling, form/URL elicitation and exact peer sub-capability checks | Stable high-level operations; application supplies handlers and approvals | [HighLevelContractTests](../tests/Andy.MCP.Tests/Server/HighLevelContractTests.cs), [ServerInitiatedRequestTests](../tests/Andy.MCP.Tests/Server/ServerInitiatedRequestTests.cs) |
| Sampling scalar/array content, tool definitions and tool-choice wire models | Stable wire models; application owns model/tool execution loops | [SamplingContentTests](../tests/Andy.MCP.Tests/Protocol/SamplingContentTests.cs), [SamplingCapabilityTests](../tests/Andy.MCP.Tests/Protocol/SamplingCapabilityTests.cs) |
| Typed elicitation schema builders, legacy enums/defaults and URL completion | Stable | [ElicitationSchemaTests](../tests/Andy.MCP.Tests/Protocol/ElicitationSchemaTests.cs), [TypedParameterTests](../tests/Andy.MCP.Tests/Protocol/TypedParameterTests.cs), [HighLevelContractTests](../tests/Andy.MCP.Tests/Server/HighLevelContractTests.cs) |
| Metadata, unknown extensions, icons and implementation descriptors | Stable wire models | [CompleteProtocolSchemaTests](../tests/Andy.MCP.Tests/Conformance/CompleteProtocolSchemaTests.cs), [MetaRoundTripTests](../tests/Andy.MCP.Tests/Protocol/MetaRoundTripTests.cs) |
| Experimental tasks | Partial/experimental: stores, augmentation and isolation exist; lifecycle/advertisement work remains in #49/#72 | [TaskStoreTests](../tests/Andy.MCP.Tests/Server/TaskStoreTests.cs), [TaskAugmentedToolTests](../tests/Andy.MCP.Tests/Server/TaskAugmentedToolTests.cs), [TaskOwnershipTests](../tests/Andy.MCP.Tests/Server/TaskOwnershipTests.cs) |

## Transports

| Surface | Status / boundary | Passing test evidence |
|---|---|---|
| UTF-8/LF stdio and process shutdown | Stable; Unix stdin→SIGTERM→kill, Windows stdin→kill | [StdioFramingTests](../tests/Andy.MCP.Tests/Transport/StdioFramingTests.cs), [StdioShutdownTests](../tests/Andy.MCP.Tests/Transport/StdioShutdownTests.cs) |
| HTTP JSON and resumable POST SSE, including nested server requests | Stable | [HttpRevisionIntegrationTests](../tests/Andy.MCP.Tests/Transport/HttpRevisionIntegrationTests.cs), [ServerPostSseTests](../tests/Andy.MCP.Tests/Transport/ServerPostSseTests.cs) |
| GET SSE, replay, independent streams and bounded polling | Stable | [SseReplayTests](../tests/Andy.MCP.Tests/Transport/SseReplayTests.cs), [SsePollingTests](../tests/Andy.MCP.Tests/Transport/SsePollingTests.cs), [ConcurrentGetStreamTests](../tests/Andy.MCP.Tests/Transport/ConcurrentGetStreamTests.cs) |
| Bounded queues, overload, session expiry and coordinated recovery | Stable; expired in-flight outcomes are not replayed | [HttpBoundedSessionTests](../tests/Andy.MCP.Tests/Transport/HttpBoundedSessionTests.cs), [HttpSessionRecoveryTests](../tests/Andy.MCP.Tests/Transport/HttpSessionRecoveryTests.cs) |

See [transport behavior](transports.md) for headers, queue limits, replay, recovery and
unknown-outcome handling. Neither legacy HTTP+SSE fallback nor JSON-RPC batches are implemented.

## Security

| Surface | Status / boundary | Passing test evidence |
|---|---|---|
| Origin, issuer/audience/scopes and principal-bound sessions | Stable enforcement; host must validate token signatures/lifetimes | [StreamableHttpSessionBindingTests](../tests/Andy.MCP.Tests/Transport/StreamableHttpSessionBindingTests.cs), [HttpAuthorizationTests](../tests/Andy.MCP.Tests/Transport/HttpAuthorizationTests.cs) |
| OAuth challenge handling, refresh and metadata discovery | Stable library flow | [OAuthAuthorizationTests](../tests/Andy.MCP.Tests/Auth/OAuthAuthorizationTests.cs), [OAuthDiscoveryTests](../tests/Andy.MCP.Tests/Auth/OAuthDiscoveryTests.cs), [OAuth401DiscoveryTests](../tests/Andy.MCP.Tests/Auth/OAuth401DiscoveryTests.cs) |
| CIMD, explicit DCR and RFC7592 management | Stable host-integrated registration | [ClientIdMetadataDocumentTests](../tests/Andy.MCP.Tests/Auth/ClientIdMetadataDocumentTests.cs), [DynamicClientRegistrationManagementTests](../tests/Andy.MCP.Tests/Auth/DynamicClientRegistrationManagementTests.cs) |
| PKCE, callback validation and one scope step-up | Stable host-integrated interaction; no built-in browser or identity provider | [OAuthInteractiveAuthorizationTests](../tests/Andy.MCP.Tests/Auth/OAuthInteractiveAuthorizationTests.cs), [OAuthScopeStepUpTests](../tests/Andy.MCP.Tests/Auth/OAuthScopeStepUpTests.cs) |

Protected HTTP fails closed without an explicit resource/issuer policy. The application
must configure ASP.NET Core authentication with signature, lifetime and issuer validation.
Andy.MCP then checks audience/scopes on each POST/GET/DELETE and binds the session to issuer
and subject. Present Origin headers are denied unless an allow-list callback accepts them.
No Origin header does not bypass authorization. Trusted local HTTP requires explicit
`AllowAnonymous = true`; bind it to loopback. Stdio relies on process/OS access controls.

Default OAuth clients disable redirects and proxies and connect using vetted DNS results,
rejecting private/reserved destinations. Custom injected HttpClients must supply equivalent
connection controls. Never pass an incoming user token through to upstream tools; acquire
credentials for each resource. See [HTTP configuration](http-security.md) and
[OAuth discovery/registration](oauth.md) for host responsibilities and examples.

Connection-level SSRF evidence: [OAuthEndpointSecurityTests](../tests/Andy.MCP.Tests/Auth/OAuthEndpointSecurityTests.cs).

## Conformance and release evidence

[Conformance gates](conformance.md) cover all negotiated schema definitions, 66 official
examples, malformed messages, pinned independent client/server interop and per-surface
line/branch coverage. The independent SDK cases cover stdio and HTTP JSON/SSE with nested
sampling. Platform-sensitive tests run on Linux, macOS and Windows. All gates must pass for
the same commit before packaging; package-content and API compatibility checks also gate release.
Publication is separately restricted to configured release triggers. Passing CI does not
change the alpha/full-compliance boundary or complete experimental task work.

## Migration

See [migration and API contracts](high-level-apis.md#migration-to-the-revision-aware-api).
The host must run .NET 10; accepting a 2025-06-18 peer does not preserve .NET 8 runtime support.
