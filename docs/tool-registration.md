# Tool registration

2026-09-08: fluent registration accepts a complete `Tool` with `AddTool(tool, handler)`;
the existing overloads remain available. Both ordinary and progress-aware handlers are supported.
`Tool` exposes title, description, input/output schemas, icons, annotations, execution, `_meta`
and extension fields. Complete definitions default to forbidden task augmentation when execution
metadata is absent; convenience registrations explicitly advertise optional support.

Attribute registration uses one supplied instance per type (or lazily constructs one). Supply
your DI-resolved instance to `AddToolsFromType(typeof(MyTools), instance)`; its lifetime remains
the caller's responsibility. Task and ValueTask results are awaited, cancellation and progress
parameters are injected, and synchronous reflection exceptions expose their original message.

`McpToolAttribute` accepts `InputSchemaJson`, `OutputSchemaJson`, `IconsJson`, `MetaJson`,
`TaskSupport`, title and behavioral hints. `DefinitionJson` supplies a complete Tool definition
including custom annotations and extension fields. Schemas must describe objects and are checked
against the 2020-12 meta-schema at registration. Network schema resolution is disabled; local
`$defs`/`$ref` remain supported. The default 2020-12 format vocabulary is annotation-only.

With an output schema, attribute methods returning ordinary objects automatically produce
structured content plus JSON text. Methods may instead return `CallToolResult` explicitly.
Successful results require conforming structured content when an output schema is declared.
The server adds a serialized JSON text block when it is missing, including for older peers.
Tool execution errors can omit structured output. Model-correctable input validation failures
return `isError: true` in 2025-11-25; older revisions retain JSON-RPC invalid-params errors.

Verification: `ToolRegistrationMetadataTests`, `StructuredOutputTests`,
`AttributeSchemaShapesTests`, `JsonSchema202012Tests`, and `OfficialJsonSchemaTests`.
The official fixture subset is frozen at the commit and SHA-256 hashes in
`tests/Andy.MCP.Tests/Conformance/json-schema/sources.json`; it is not a claim of running the
entire upstream test suite.
