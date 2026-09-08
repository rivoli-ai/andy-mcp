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
- [ ] #41 Complete revision-aware wire models
  - [x] Preserve extension fields, add capability/tool execution metadata, and correct older sampling serialization
  - [ ] Complete remaining typed parameter/union and high-level sub-capability checks
- [x] #43 Cancellation, progress and timeout cleanup

- [x] #44 Transport compliance and process shutdown
  - [x] Stdio EOF/SIGTERM/kill escalation and bounded GET SSE polling
  - [x] POST SSE cursor isolation, resumption and terminal-response completion
  - [x] Bound HTTP queues/replay and state the three supported HTTP revisions
  - [x] Automatically recover expired HTTP sessions with a fresh handshake and capability refresh
  - [x] Complete server POST SSE with request-scoped bidirectional routing and bounded replay
- [x] #47 Full JSON Schema validation and registration surface
  - [x] Complete 2020-12 runtime/meta-schema validation with network fetches disabled
  - [x] Complete registration metadata fields
- [ ] #48 Complete high-level client/server operations
  - [x] Freeze registration, enforce prompt arguments and list-change capabilities, and support multi-content static resources
  - [x] Check sampling/elicitation sub-capabilities and expose roots/URL completion notifications
  - [x] Complete safe extension APIs, per-call controls and explicit pagination
  - [ ] Complete remaining protocol shape validation with #41
- [ ] #50 Conformance gates and coverage
- [ ] #51 Evidence-backed documentation
- [x] #71 URI-template resolution
- [x] #73 SSE stream closure and polling
- [ ] #74 Official-schema corpus and independent client/server interop
- [x] #75 Attribute schema generation (PR96; merged-state 769 tests passed)

## P3
#19, #20, #21, #30: optional ecosystem integration.
#49, #72: experimental task lifecycle.
#39, #68: full-compliance epics; remain open until all children and final gates pass.

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

2026-09-08: final transport audit exercises every supported HTTP revision against a real server
in JSON and POST SSE modes. Stdio uses explicit UTF-8 and LF framing in both directions, with
literal Unicode, escaped-newline and invalid-byte tests. HTTP legacy fallback is explicitly unsupported.
