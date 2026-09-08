# Transport behavior

| Revision | Stdio | Streamable HTTP JSON / POST SSE / GET SSE |
| --- | --- | --- |
| 2025-11-25 | Supported | Supported |
| 2025-06-18 | Supported | Supported |
| 2025-03-26 | Supported | Supported |
| 2024-11-05 | Supported | Unsupported; legacy HTTP+SSE fallback is not implemented |

HTTP sends the negotiated revision after initialization and rejects conflicting session headers.
The server returns JSON by default. Set `StreamableHttpServerOptions.UseSseResponses = true`
for resumable non-initialize POST streams; initialization remains JSON. Related server requests
and notifications stay on the originating POST, so sampling can work with
`StreamableHttpClientTransportOptions.EnableServerSseStream = false`.
Unsolicited messages require the optional GET listener.

SSE priming events carry a stream-specific cursor and retry hint. Polling or server closure
ends the current HTTP stream; the client resumes using that stream's own `Last-Event-ID`.
A POST stream ends after its correlated response. Multiple streams never claim the same live
message. Expired replay cursors return 400; duplicate active stream identities return 409;
stream, request or undelivered-event overload returns 429. Session capacity returns 503.
`SsePollTimeout`, `SseRetryMilliseconds`, `MaxConcurrentStreamsPerSession` and
`MaxConcurrentRequestsPerSession` configure these limits. Replay and incoming server queues
are bounded to 256 entries; client incoming queue capacity defaults to 256.

A session 404 triggers a coordinated new initialize/initialized handshake and refreshes peer
capabilities. A request rejected before dispatch can retry once. An interrupted POST SSE
operation whose result can no longer be resumed fails with `McpSessionExpiredException` and
an unknown-outcome message; it is not repeated. Recovery observes the original call deadline.
The replacement session has fresh server state, including subscriptions. Disposal cancels
recovery and sends a bounded DELETE request. Server timeout/deletion cancels handlers and
releases session ownership and stream reservations.

Stdio uses UTF-8 without a byte-order mark and emits one JSON message per LF-delimited line.
Embedded newlines remain JSON escapes. Unix shutdown closes stdin, waits `ShutdownTimeout`,
sends SIGTERM, waits `KillGraceTimeout`, then kills the process tree if needed. Windows closes
stdin, waits, then terminates a remaining process tree. Standard output is reserved for protocol
messages; diagnostics belong on standard error. Custom text readers/writers must use UTF-8.

HTTP endpoints require explicit anonymous opt-in or application authentication configuration.
See [HTTP authorization](http-security.md) for principal, audience, scope and Origin rules.

## Verification

`HttpRevisionIntegrationTests` covers all three HTTP revisions with real JSON and SSE exchanges.
`ServerPostSseTests` covers concurrent nested sampling, polling, terminal replay and queue capacity.
`HttpSessionRecoveryTests` covers deletion, concurrent recovery, GET restart, capability refresh,
old-handler cancellation and deadlines. `HttpBoundedSessionTests`, `SseReplayTests`,
`ConcurrentGetStreamTests` and `SsePollingTests` cover bounds and independent stream identities.
`StdioFramingTests`, `StdioShutdownTests` and `StdioServerTransportTests` cover framing, graceful
shutdown and malformed input. The suite runs on Linux, Windows and macOS in CI.
