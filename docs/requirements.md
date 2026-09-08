# Requirements and acceptance status — 2026-09-08

The current target is MCP 2025-11-25 on .NET 10. Supported older negotiations are
2025-06-18, plus 2024-11-05 over stdio. This is a requirements ledger, not a declaration
of 100% compliance. The [revision-specific matrix](compliance.md) is the status authority.

| Requirement | State and acceptance evidence |
|---|---|
| Strict JSON-RPC, lifecycle, metadata and typed revision-aware wire models | Implemented: [protocol/schema gates](conformance.md#evidence) |
| UTF-8/LF stdio, graceful process shutdown and bounded resources | Implemented: [transport matrix](compliance.md#transports) |
| HTTP JSON/POST SSE/GET SSE, headers, replay, concurrent streams and expiry recovery | Implemented: [transport matrix](compliance.md#transports) |
| Tools, full schema validation, metadata and structured output | Implemented: [feature evidence](compliance.md#stable-features-and-experimental-boundaries) |
| Resources, RFC6570 templates, multi-content reads and subscriptions | Implemented: [feature evidence](compliance.md#stable-features-and-experimental-boundaries) |
| Prompts with required literal arguments and completion values | Implemented: [high-level contracts](high-level-apis.md) and [feature evidence](compliance.md#stable-features-and-experimental-boundaries) |
| Bidirectional ping, roots, sampling and form/URL elicitation | Implemented sending/receiving paths: [feature evidence](compliance.md#stable-features-and-experimental-boundaries); host supplies execution/approval |
| Cancellation, progress, per-request deadlines and explicit pages | Implemented: [feature evidence](compliance.md#stable-features-and-experimental-boundaries) |
| Logging and capability-gated list-change notifications | Implemented: [high-level tests](../tests/Andy.MCP.Tests/Server/HighLevelContractTests.cs), [feature tests](../tests/Andy.MCP.Tests/Server/Phase3Tests.cs) |
| OAuth discovery, registration, PKCE, refresh and scope step-up | Implemented host-integrated flows: [security evidence](compliance.md#security) |
| Origin, authentication, audience/scopes, session binding and SSRF controls | Implemented library enforcement with required host configuration: [security evidence](compliance.md#security) |
| Official schemas/examples, independent client/server interoperability, critical coverage | Implemented CI gates: [reproduction and thresholds](conformance.md) |
| Dependency injection, hosted connections, tracing and attributes | Implemented: [configuration tests](../tests/Andy.MCP.Tests/Configuration), [attribute tests](../tests/Andy.MCP.Tests/Server/AttributeRegistrationTests.cs), [tracing tests](../tests/Andy.MCP.Tests/Protocol/McpDiagnosticsTests.cs) |
| Experimental task lifecycle and task-capability advertisement | Implemented and verified; remains experimental in the protocol. See [task guide](tasks.md) |
| Legacy 2024 HTTP+SSE; 2025-03-26 required batch reception | Unsupported; excluded from applicable transport negotiation |
| Native AOT and trimming certification | Unsupported; [maintenance limitations](package-maintenance.md) |
| Andy Engine, Gateway and Containers integration | Open P3 issues #19/#20/#21 under #30 |
| Full MCP compliance | Open P3 epics #39/#68; stable remediation is not phase-wide completion |

All production source changes require .NET tests. Builds restore locked central NuGet
dependencies; release requires same-commit three-platform tests, independent interop,
security, package-content and public API compatibility checks. Registration freezes at
server startup; runtime requests and supported notifications remain concurrent.

The [remediation plan](remediation-plan.md) records P1/P2/P3 decisions, completion checks
and dated evidence. No phase is complete until every main and sub-task in its epic is complete.
