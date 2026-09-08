# Experimental tasks

Tasks are available only for MCP 2025-11-25 and remain experimental. The ongoing
negotiation and pagination audit is tracked in #49; see the compliance matrix.

`CallToolAsTaskAsync`, `CreateMessageAsTaskAsync` and `ElicitAsTaskAsync` return a task
immediately. Poll its state with `GetTaskAsync`/`GetClientTaskAsync`, or await the deferred
result. Result retrieval blocks through `working` and `input_required`; cancelling a
retrieval cancels that waiter, while `CancelTaskAsync`/`CancelClientTaskAsync` cancels the
execution. Completed, failed and cancelled states cannot be overwritten by late handlers.
Nested peer requests carry related-task metadata and expose `input_required` until all
pending peer input arrives. HTTP supports these requests through its SSE streams.

## Stores and ownership

Both `McpServerOptions` and `McpClientOptions` accept `TaskStore` and `TaskOwnerKey`.
The default store is in-memory. When no key is supplied, each connection receives an
independent owner scope, even if connections share a store. HTTP server endpoints always
replace the supplied key with the authenticated session's scope. Client HTTP session
recovery rotates its default scope. Explicit keys are trusted host configuration: derive
them from authenticated context, never from untrusted request parameters. A host may reuse
an explicit key across connections when its authorization model permits retained retrieval.

Implement `ITaskStore` to use a durable database. Store task identity, timestamps, owner,
TTL, status and outcome atomically. Preserve the full result or RPC error, and reject writes
to terminal or expired tasks. Override `SetError` and `GetError` to retain error codes, data
and extensions. Their default interface implementations support older custom stores by
retaining only failure messages; those legacy stores need upgrading for exact RPC errors.
Result retrieval polls the store so it can observe completion by another process.

Persistence does not resume arbitrary .NET delegates after a process restart. Hosts own
external job execution and recovery, and may update retained tasks from their durable job
system. Connection-owned handlers are cancelled on shutdown. The disk-journal adapter in
`DurableTaskStoreTests` demonstrates store recreation and authorized result retrieval; it
is a single-writer test fixture, not a production database implementation.

## Verification (2026-09-08)

Task state, cancellation races, waiter independence, TTL, metadata, ordinary/deferred error
parity, injected ownership, HTTP isolation and input routing are exercised in the .NET test
assemblies. `DurableTaskStoreTests` recreates a store from disk and retrieves a completed
result from a new connection without registering or rerunning the original tool.
