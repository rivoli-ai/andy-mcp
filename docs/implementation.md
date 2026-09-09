# Implementation — 2026-09-09

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

## Phase 7/8 completion audit — 2026-09-09

Reviewed every main/sub-task in #39 and #68 against merged child issues, current code,
linked conformance tests, and the release workflow. All children #40–#52 and #69–#76 are
closed with implementation evidence. The latest pre-audit verification is [PR #124](https://github.com/rivoli-ai/andy-mcp/pull/124):
2,902 passing tests, no skips, all platform/coverage/security/interop/API/package gates green.
The audit additionally makes the full suite, including the pinned independent SDK cases,
mandatory on Linux, macOS and Windows; its own PR must pass these gates before merge.

- [x] Every Phase 7 and Phase 8 implementation child is merged and closed.
- [x] Full .NET suite and independent SDK interop are required on all three platforms.
- [x] ASP.NET Core Streamable HTTP has real end-to-end JSON/SSE/bidirectional tests.
- [x] Every negotiated schema definition/revision and advertised capability has linked evidence in the compliance matrix.
- [x] README and requirements distinguish library compliance, upstream experimental status and host responsibilities.
- [x] Implementation plan and README contain this dated completion record.
- [x] Same-commit build, security, coverage, conformance, API and package checks gate release.

Phase 8's original instruction to mark experimental tasks stable is reconciled as
**implementation complete, protocol-experimental**. The November 2025 specification still
labels tasks experimental; removing the library-wide alpha warning does not change that.
Optional Phase 6 ecosystem integration (#19/#20/#21/#30) remains independently tracked.
The [prioritized plan](remediation-plan.md) retains the historical implementation record.

## Verification and release

[Conformance documentation](conformance.md) defines the reproducible commands, test mapping
and critical line/branch thresholds. Linux/macOS/Windows CI verifies .NET tests and the
documented in-process example; mandatory Node interop also runs the documented stdio server.
The compiled OAuth example is exercised by a deterministic .NET test with a fake HTTP peer.
No browser, live identity provider or production credentials are needed for CI.

The reusable validation workflow must pass on the same commit before packaging. Release
builds additionally check public API compatibility and both NuGet packages' contents.
Publishing follows configured tag/main prerelease rules. Compliance is scoped by the verified feature/transport matrix.
See [package maintenance](package-maintenance.md) for runtime, dependency and trimming policy.
