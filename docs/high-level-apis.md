# High-level operation contracts

2026-09-08: registration is serialized under a lock and freezes when `McpServer.RunAsync`
starts. Configure tools, resources, templates, prompts, completions and logging beforehand.
Starting the same server twice or mutating registration afterward throws. Runtime event
notifications remain available when their corresponding list-change capability was advertised.

`AddResource(Resource, handler)` accepts the full resource descriptor and a reader returning
multiple content entries. `AddPrompt(Prompt, handler)` accepts the full prompt descriptor.
Required prompt arguments are checked before invoking the handler. Values are passed literally;
handlers own substitution and must explicitly decide how values appear in prompt messages.

Server sampling checks `sampling.tools` when tool definitions, tool choice or tool content is
present and checks `sampling.context` for non-empty context in 2025-11-25. Older revisions
cannot receive sampling tool features. Elicitation checks the negotiated revision and selected
form/URL sub-capability; an empty elicitation capability retains legacy form-only behavior.
Client dispatch applies the same checks before invoking user handlers.

`McpServer.RootsChanged` receives permitted root-list notifications. A root provider's
`listChanged` setting is honored and event subscriptions are removed on client disposal.
`NotifyElicitationCompleteAsync(ElicitationCompleteParams, ct)` and
`McpClient.ElicitationCompleted` expose URL completion IDs and metadata.

Use a client ping round trip before server-originated work when constructing both peers in
one process; it establishes receipt of the initialization notification. Runtime notifications
require a ready session. Verification: `HighLevelContractTests` and existing bidirectional,
completion, subscription, progress and task augmentation tests.

`RequestCustomAsync<T>` and `NotifyCustomAsync` on both peers use the existing transport,
revision serializer, request correlation and cancellation tracking. Register server handlers
with `AddCustomRequestHandler`; configure client handlers with
`McpClientOptions.CustomRequestHandlers` before connecting. Reserved standard method namespaces
cannot be overridden. Custom parameters and results must remain JSON objects. Custom
notifications are exposed through `CustomNotificationReceived` with all metadata intact.

`McpRequestOptions` supplies an idle `Timeout`, `MaximumDuration` and progress observer.
Null timeout values inherit connection defaults; `Timeout.InfiniteTimeSpan` disables that
particular deadline. Options are available on extension requests, typed tool calls, explicit
page APIs and overloads for the other stable request operations. Timeouts interrupt blocked
HTTP writes/POSTs and send cancellation to the peer. A deadline is per protocol request;
use a caller cancellation token when imposing a budget across multiple requests.

The `ListToolsPageAsync`, `ListResourcesPageAsync`, `ListResourceTemplatesPageAsync` and
`ListPromptsPageAsync` APIs return the full page result, preserving cursor and metadata.
Existing list APIs still fetch all pages automatically. `ListRootsResultAsync` preserves the
full roots result. Supplying progress options adds a fresh progress token while preserving
other `_meta` entries. Server extension handlers may receive the functional progress reporter.

Tests: `ExtensionApiTests` and `HttpRequestDeadlineTests`. Standard requests, results and
notifications now validate against the frozen official schema for the negotiated revision
at both boundaries; malformed params cannot reach handlers and invalid handler results
become RPC errors. Custom methods retain lifecycle/correlation checks.

## Migration to the revision-aware API

- Upgrade the host to .NET 10. Package API compatibility checks preserve the previous public
  method signatures, but do not preserve .NET 8 runtime support.
- Default negotiation is 2025-11-25. 2025-06-18 remains supported; 2024-11-05 is stdio-only.
  March 2025 is no longer accepted because required batch reception is absent. Use
  `ProtocolRevision.Supported`, not `All`, when displaying negotiable versions.
- Sampling `Content` is a list in memory. Both wire union forms are accepted; singleton
  output is scalar for reference-SDK compatibility, and multiple blocks are arrays. Legacy
  revisions require exactly one supported block. Tool definitions/toolChoice require
  `sampling.tools`; the host executes tools/model loops and validates its interaction policy.
- Form elicitation remains available in June 2025. Latest URL mode and multi-select fields
  require latest capabilities. Legacy titled enums convert to enum/enumNames; unsupported
  newer values fail explicitly. Handle `ElicitationCompleted` for URL completion.
- Configure registrations before RunAsync. Required prompt arguments are literal strings;
  handlers perform any substitution. Use typed parameter overloads to retain `_meta` and
  explicit page APIs to retain result metadata/cursors.
- Configure HTTP authorization/resource/issuer and Origin allow-lists explicitly. Trusted
  local HTTP must opt into AllowAnonymous. Custom OAuth HttpClients own connection safety.
- CancellationToken limits caller work; per-call options control one protocol request.
  Progress can reset the idle timeout but never extends MaximumDuration. Observe cancellation
  in handlers and report monotonic progress; runtime cleanup cancels and drains owned handlers.
- Resource subscriptions are session-local. Handle `ResourceUpdated`, re-read when notified,
  and re-establish subscriptions after a fresh connection. Automatic HTTP session recovery
  starts with fresh subscriptions; interrupted POST SSE results can have unknown outcomes.
- Experimental tasks remain incomplete (#49/#72). Do not treat task model availability as a
  production-ready lifecycle or advertise unsupported task operations.
