# Prioritized remediation — 2026-09-08

User authorization: fix P1 and P2 issues and merge each verified increment.

Priorities reflect remaining work, not whether an earlier partial PR merged:
P1 = security, RPC correctness, release safety; P2 = stable protocol correctness and verification;
P3 = experimental features, ecosystem integrations, and long-term full-compliance epics.

## P1
- [x] #52 Release gating and .NET 10 package maintenance
  - [x] Require same-commit cross-platform tests, interop and security before pack/publish
  - [x] Run the documented example in CI and upload symbol packages
  - [x] Finish dependency, API compatibility and package-content validation
- [x] #42 Strict bidirectional JSON-RPC and lifecycle enforcement
- [x] #45 OAuth discovery and token lifecycle security
- [x] #46 ASP.NET Core authorization, Origin and principal-bound isolation


## P2
- [x] #41 Complete revision-aware wire models
  - [x] Preserve extension fields, add capability/tool execution metadata, and correct older sampling serialization
  - [x] Add remaining stable typed parameters, enum builders and legacy metadata/elicitation conversion
  - [x] Finish protocol shape validation and complete official-type coverage mapping
- [x] #43 Cancellation, progress and timeout cleanup

- [x] #44 Transport compliance and process shutdown (revision audit verified on merged 4a21813)
  - [x] Stdio EOF/SIGTERM/kill escalation and bounded GET SSE polling
  - [x] POST SSE cursor isolation, resumption and terminal-response completion
  - [x] Bound HTTP queues/replay and state the two supported HTTP revisions
  - [x] Automatically recover expired HTTP sessions with a fresh handshake and capability refresh
  - [x] Complete server POST SSE with request-scoped bidirectional routing and bounded replay
- [x] #47 Full JSON Schema validation and registration surface
  - [x] Complete 2020-12 runtime/meta-schema validation with network fetches disabled
  - [x] Complete registration metadata fields
- [x] #48 Complete high-level client/server operations
  - [x] Freeze registration, enforce prompt arguments and list-change capabilities, and support multi-content static resources
  - [x] Check sampling/elicitation sub-capabilities and expose roots/URL completion notifications
  - [x] Complete safe extension APIs, per-call controls and explicit pagination
  - [x] Complete remaining protocol shape validation with #41
- [x] #50 Conformance gates and coverage
- [x] #51 Evidence-backed documentation
- [x] #71 URI-template resolution
- [x] #73 SSE stream closure and polling
- [x] #74 Official-schema corpus and independent client/server interop
- [x] #75 Attribute schema generation (PR96; merged-state 769 tests passed)

## P3
#19, #20, #21, #30: optional ecosystem integration.
#49, #72: experimental task lifecycle implemented and verified.

- [x] Enforce terminal-state immutability, exact TTL expiry and detached failed-tool payload retention.
- [x] Block result retrieval until terminal state and propagate task cancellation to running handlers.
- [x] Route related-task input requests and resume after all pending peer input.
- [x] Retain exact RPC errors through durable stores and verify disk-backed store recreation.
- [x] Isolate default owners and inject stores in both directions.
- [x] Complete the task capability and pagination audit.
#39, #68: Phase 7/8 library implementation and completion audit finished on 2026-09-09; see the dated record in implementation.md. Tasks retain their upstream experimental label.

## Completion record
2026-09-08: verified the merged ancestors behind closures #40 (e10e6e6), #69 (98042ae)
and #76 (superseded by .NET 10-only migration 2f41cd6). Priority labels assigned to all
23 remaining issues. Release safety implementation recorded above; overall work is in progress.

2026-09-08: completed strict RPC/lifecycle and request deadline/cleanup checks, including real
HTTP bidirectional traffic, overlapping-ID cancellation and repeated concurrent shutdown.

2026-09-08: OAuth acceptance audit verifies actual path-aware RFC 8414 and both OIDC fallback
flows, challenge refresh/rotation, CIMD/DCR, PKCE callbacks and concurrent scope upgrades.


2026-09-08: verified graceful stdio escalation, server-directed GET SSE closure and polling,
stream identity/capacity enforcement, retry hints and independent POST SSE resumption.
2026-09-08: completed Tool-definition registration, attribute metadata and ValueTask support,
revision-specific validation errors, output-schema enforcement and structured text fallback.
Pinned official JSON Schema fixtures supplement adversarial validation tests.

2026-09-08: server registration freezes atomically at RunAsync; required prompt arguments
are checked before literal handler binding. Sampling/elicitation modes honor peer sub-capabilities.
Root subscriptions detach on disposal and URL elicitation completion is exposed as a typed event.
2026-09-08: resource templates recognize all RFC6570 operators, prefixes and composite expansions,
reject malformed templates before registration, and retain static-resource precedence.
Upstream RFC example corpus and multi-content/error-channel integration tests verify matching.
2026-09-08: verified two simultaneously active HTTP GET streams route live notifications once,
retain independent stream identities, and release reservations after polling. Resumption and
explicit server closure are covered by SseReplayTests and SsePollingTests.

2026-09-08: HTTP request/response and undelivered-event queues reject overload without leaking
waiters or losing pending events. Replay gaps and inconsistent session revision headers fail explicitly.
2026-09-08: extension requests/notifications use existing lifecycle and correlation safety,
manual page APIs preserve cursors and result metadata, and per-call deadlines interrupt blocked
HTTP POSTs as well as response waits. Reserved standard method namespaces cannot be overridden.

2026-09-08: automatic HTTP session recovery coordinates concurrent 404s, restarts GET polling,
refreshes peer capabilities, and cancels old inbound handlers. Recovery obeys call deadlines; an
expired in-progress POST SSE result reports unknown outcome without repeating the operation.

2026-09-08: optional server POST SSE routes nested sampling and notifications to their originating
stream, resumes across polling without reposting tools, and reserves space for terminal responses.
Abandoned handlers release their POST state; global GET streams cannot claim POST-owned events.

2026-09-08: typed resource/prompt/subscription/logging APIs retain request metadata; list responses
use typed models. Base request and notification metadata remain available for all four known schema revisions.
Older elicitation retains boolean defaults and legacy titled enums; URL and multi-select modes
fail explicitly when unavailable. Typed URL-elicitation-required errors validate their mode.
2026-09-08: final transport audit exercises every supported HTTP revision against a real server
in JSON and POST SSE modes. Stdio uses explicit UTF-8 and LF framing in both directions, with
literal Unicode, escaped-newline and invalid-byte tests. HTTP legacy fallback is explicitly unsupported.

2026-09-08: the full schema audit exposed mandatory batching in 2025-03-26. Since receiving
batches is not implemented, remove that revision from client acceptance and server negotiation,
retain its descriptor for schema conversion, and correct the transport matrix. Reverify #44 on merge.

2026-09-08: all negotiated schema definitions map to public models and pass 1,002 schema-derived
round trips. Runtime official-schema checks protect standard requests, results and notifications
in both directions. Invalid parameters never reach handlers; invalid handler results become RPC
errors. Metadata order and optional tool-choice defaults are corrected; 2024 completion APIs
no longer require a capability flag absent from that revision.

2026-09-08: added66 official examples from immutable upstream sources, mandatory pinned SDK
client/server interoperability over stdio and HTTP JSON/SSE, and per-surface line/branch
coverage gates. Independent tests verify nested sampling and prompted scalar singleton output
for reference-SDK compatibility. The CI gate rejects missing prerequisites and reports.

2026-09-08: completed the P1/P2 documentation audit after PR116. Replaced stale binary
claims/type counts with revision-specific evidence, corrected fail-closed security defaults,
documented migration/host responsibilities and experimental limits, and compiled/executed
the OAuth discovery/DCR example in .NET tests. Phase7/8 full-compliance epics remain open
with P3 tasks and ecosystem work; this completes stable remediation, not every epic child.

2026-09-08: task-store transitions now reject terminal writes and repeated cancellation.
TTL boundaries apply to reads and writes, UTC timestamps normalize offset clocks, and
failed tool payloads retain their metadata independently of caller-owned JSON documents.
Transition-matrix and concurrent cancellation/completion tests cover these invariants;
#49/#72 remain open for end-to-end lifecycle work.

2026-09-08: result retrieval now waits through working/input_required states in both
directions, keeps independent waiter cancellation, and returns failed tool payloads with
related-task metadata. Cancelling a task stops its background handler and releases pending
retrievals without allowing late completion to change the terminal state.

2026-09-08: background tasks now enter input_required during nested peer requests,
propagate related-task metadata without losing vendor metadata, and return to working
after every pending input completes. Real HTTP JSON/POST SSE elicitation and reverse
sampling-to-tool tests verify observable transitions and deferred completion.

2026-09-08: stores can retain RPC error codes, data and extensions; task execution
preserves ordinary call error semantics and validates deferred payloads against the original
request schema. Both peers accept injected stores and isolate default owner scopes.
Disk-journal tests recreate stores and connections to retrieve prior results without reexecution.

2026-09-08: completed the #49/#72 task acceptance audit: revision/capability gates,
per-tool task modes, unsupported augmentation fallback, signed pagination in both directions,
invalid-input outcome parity, immutable states, TTL and owner isolation, related input routing,
and disk-backed client/server result retrieval. Full local suite: 2,894 passed, zero skipped;
all coverage gates and both API checks passed. Phase 7/8 release/compliance audits remain open.

2026-09-08: #19 integration testing reproduced a JsonSchema.Net 7/9 runtime mismatch
that prevented current MCP clients from initializing in Andy CLI. Migrated validation
to 9.3.0 with local build registries and explicit Draft 2020-12 selection. Added
repeated-schema-ID isolation coverage; ecosystem adapter acceptance remains open.

2026-09-09: implemented the Andy Containers REST/MCP adapter: bounded provisioning,
published-port resolution, handshake/ping readiness, filtered paginated discovery, tracked
client leases, idle cleanup and optional catalog hooks. Added a .NET 10 HTTP container
example and template. Pool/autoscaling and durable ownership remain outside this increment;
#21/#30 remain open until their remaining acceptance is verified.
2026-09-09: shared tool registry/executor adapter implementation merged in andy-tools#117
with 1,126 passing tests across macOS, Linux and Windows, 35 MCP adapter regressions,
and synchronized package publication. Implemented the current gateway registry client,
name resolution and health-aware discovery; #20's obsolete adapter/proxy API requirements
remain separate from the verified registry contract. No ecosystem epic is marked complete.

2026-09-09: added container stop/crash health coupling and transport-disconnect lease release.
Tests verify stopped clients close, stopped containers reject new sessions, and restart opens
a fresh working MCP connection before final destruction.
2026-09-09: wired connection-manager recovery to the existing AutoReconnect/ReconnectPolicy
configuration. Added real MCP client/server regressions for replacement, disabled recovery,
retry limits, removal preventing resurrection, cancellation and disposal. Shared registry/
executor integration is published as Andy.Tools.Mcp 2026.9.9-rc.103 (andy-tools#117).
2026-09-09: completed the Phase 7/8 library-wide audit after verifying every child issue,
revision/capability evidence, real ASP.NET Core tests and same-commit release dependencies.
Expanded the platform matrix to run the full official-SDK interop suite on all three OSes.
Replaced obsolete library-wide alpha/incomplete statements with the verified scope and
preserved protocol-experimental tasks, unsupported legacy transports and host responsibilities.

2026-09-09: added bounded container pools with minimum warm capacity, maximum demand capacity,
idle scale-down, unhealthy idle replacement, fresh MCP sessions on lease return and hosted
shutdown cleanup. Tests exercise the pool through real MCP client/server sessions; catalog
image update policy and durable container TTL remain control-plane responsibilities.

2026-09-09: implemented typed gateway adapter CRUD/discovery/health/import/export, authenticated
proxy transport with bounded 401 refresh, explicit legacy SSE compatibility and proxy-aware
connection discovery. Preserved registry-only behavior. Gateway-side contract and end-to-end
acceptance are tracked in andy-mcp-gateway#29 before closing #20/#30.
