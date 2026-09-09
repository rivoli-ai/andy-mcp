# Conformance gates

Verified surfaces and the completed Phase 7/8 audit are described in [compliance.md](compliance.md). Tasks retain their upstream experimental status; ecosystem integrations have separate acceptance.

## Reproducible validation

```sh
npm ci --ignore-scripts --prefix tests/interop
dotnet restore --locked-mode
dotnet test --collect:"XPlat Code Coverage" --results-directory ./TestResults
python3 scripts/check-coverage.py "TestResults/**/coverage.cobertura.xml"
```

Use an empty results directory for each coverage run. The gate requires exactly one
report and fails on missing surfaces or missing branch data. Full solution coverage
includes both core and ASP.NET Core libraries. Three-platform CI runs the full suite with coverage, including the six independent SDK
cases using Node 24. A separate mandatory Node 24 job also isolates interop failures.
Both jobs gate the same-commit build/package workflow. Missing prerequisites fail interop.

| Surface | Minimum lines | Minimum branches |
|---|---:|---:|
| Protocol | 95% | 80% |
| Transport | 85% | 78% |
| Auth | 85% | 73% |
| ASP.NET Core | 90% | 78% |
| Client | 80% | 70% |
| Server | 85% | 75% |

`scripts/check-coverage.py --self-test` proves missing and zero coverage fail and Windows
paths are handled. CI uploads each platform's Cobertura artifact, including on failure.

## Evidence

| Gate | Evidence |
|---|---|
| Every official definition for all three negotiated revisions; 1,002 schema-derived cases and public type mapping | [CompleteProtocolSchemaTests](../tests/Andy.MCP.Tests/Conformance/CompleteProtocolSchemaTests.cs), [corpus provenance](../tests/Andy.MCP.Tests/Conformance/protocol-corpus/README.md) |
| 66 unchanged official JSON-RPC examples, frozen upstream commit and per-source hashes | [OfficialExampleTests](../tests/Andy.MCP.Tests/Conformance/OfficialExampleTests.cs), [example provenance](../tests/Andy.MCP.Tests/Conformance/official-examples/README.md) |
| Invalid parameters, handler results, enums/unions, schema byte hashes | [ProtocolBoundaryTests](../tests/Andy.MCP.Tests/Conformance/ProtocolBoundaryTests.cs), [MessageFixtureTests](../tests/Andy.MCP.Tests/Conformance/MessageFixtureTests.cs) |
| Independent official SDK client and server: stdio, HTTP JSON/SSE, nested sampling, tools/resources/prompts, Unicode and shutdown | [ReferenceSdkInteropTests](../tests/Andy.MCP.Tests/Conformance/ReferenceSdkInteropTests.cs), [locked harness setup](../tests/interop/README.md) |
| All supported HTTP revisions, real bidirectional traffic and POST stream ownership | [HttpRevisionIntegrationTests](../tests/Andy.MCP.Tests/Transport/HttpRevisionIntegrationTests.cs), [ServerPostSseTests](../tests/Andy.MCP.Tests/Transport/ServerPostSseTests.cs) |
| GET/POST replay, multiple streams, polling, overload, expiry and recovery | [SseReplayTests](../tests/Andy.MCP.Tests/Transport/SseReplayTests.cs), [SsePollingTests](../tests/Andy.MCP.Tests/Transport/SsePollingTests.cs), [HttpSessionRecoveryTests](../tests/Andy.MCP.Tests/Transport/HttpSessionRecoveryTests.cs), [HttpBoundedSessionTests](../tests/Andy.MCP.Tests/Transport/HttpBoundedSessionTests.cs) |
| RFC6570 official expansions and JSON Schema 2020-12 reference suite | [URI corpus](../tests/Andy.MCP.Tests/Conformance/uri-templates), [JSON Schema corpus](../tests/Andy.MCP.Tests/Conformance/json-schema) |

Concurrency, deadlines, cancellation, auth discovery and task isolation have direct .NET
tests in the Client, Server, Auth and Transport test folders; full task lifecycle remains
experimental. The official SDK harness is independently implemented protocol/transport
evidence, not the upstream all-features conformance runner or exhaustive external OAuth certification.

2026-09-08: replaced optional unpinned reference-server execution with a locked mandatory
client/server harness. Independent sampling exposed a singleton compatibility issue:
one sampling block now serializes as a scalar, while multiple blocks retain the array union.
