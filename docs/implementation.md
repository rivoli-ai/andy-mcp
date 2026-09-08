# Implementation — 2026-09-08

## Structure and contracts

| Area | Implementation |
|---|---|
| Core library | [src/Andy.MCP](../src/Andy.MCP): protocol, transports, high-level client/server, auth, configuration and diagnostics |
| HTTP server | [src/Andy.MCP.AspNetCore](../src/Andy.MCP.AspNetCore): authorization, session ownership, POST/GET SSE and endpoint mapping |
| Tests | [tests/Andy.MCP.Tests](../tests/Andy.MCP.Tests): .NET unit, integration, schema, security, concurrency and independent interop tests |
| Examples | [examples/Andy.MCP.Examples](../examples/Andy.MCP.Examples): executable stdio/in-process examples and compiled OAuth discovery/registration |
| Independent implementation | [tests/interop](../tests/interop): official SDK client/server harness with an npm lockfile |

Both packages target .NET 10. Central package versions and project lockfiles are checked in.
The complete official-type to public-model mapping is generated alongside the
[schema corpus](../tests/Andy.MCP.Tests/Conformance/protocol-corpus/README.md), replacing
hand-maintained type counts. Generic request envelopes and open result unions share models.

`ProtocolRevision.Supported` controls negotiation; `All` includes the non-negotiable March
2025 descriptor for conversion. `RevisionAwareJson` removes newer fields and rejects
unrepresentable legacy values. Standard message boundaries validate frozen official schemas
without network resolution. `SamplingContentConverter` accepts scalar/array content and
writes a scalar for one block. Tool argument/output schemas use JSON Schema 2020-12.

Client and server message loops correlate responses while independently tracking inbound
handler cancellation. Request trackers enforce progress-aware idle timeouts and absolute
limits. Server registration freezes under a lock at RunAsync; handlers execute concurrently.
HTTP sessions own bounded queues, stream/replay state, authenticated identity and task scope.
Origin and resource authorization are checked before session access. Stdio owns process
shutdown and framing. See [design](design.md), [transports](transports.md),
[high-level contracts](high-level-apis.md), and [security](http-security.md).

## Phase 7 remediation completion record

2026-09-08: the stable P1/P2 remediation covers strict RPC/lifecycle, full negotiated wire
definition coverage, tool schemas, request lifetimes, stdio/HTTP/SSE, OAuth and HTTP security,
high-level operations, URI templates, attributes, independent interop and release gates.
The suite contains 2,830 tests before the final documentation/example additions, including
1,002 schema-derived fixtures, 66 sourced official examples and six independent SDK cases.
Use CI artifacts for the exact current count and measured coverage.

**Phase 7 and Phase 8 full-compliance epics remain in progress.** Experimental tasks (#49/#72)
and the outstanding full-compliance acceptance under #39/#68 remain open. Phase 6 ecosystem
integration (#19/#20/#21/#30) is also open. This is a completion record for stable remediation,
not a claim that every phase task or experimental protocol requirement is complete.
The [prioritized plan](remediation-plan.md) retains the main/sub-task checklist.

## Verification and release

[Conformance documentation](conformance.md) defines the reproducible commands, test mapping
and critical line/branch thresholds. Linux/macOS/Windows CI verifies .NET tests and the
documented in-process example; mandatory Node interop also runs the documented stdio server.
The compiled OAuth example is exercised by a deterministic .NET test with a fake HTTP peer.
No browser, live identity provider or production credentials are needed for CI.

The reusable validation workflow must pass on the same commit before packaging. Release
builds additionally check public API compatibility and both NuGet packages' contents.
Publishing follows configured tag/main prerelease rules and does not assert full compliance.
See [package maintenance](package-maintenance.md) for runtime, dependency and trimming policy.
