# Independent reference interoperability

The client and server use official `@modelcontextprotocol/sdk` 1.30.0 with a complete
npm lockfile. The harness supplies deterministic tools/resources/prompts and sampling
handlers; transport, framing, negotiation, validation and response correlation belong
to the independent SDK. These are integration fixtures, not a claim to implement an
upstream conformance runner.

From the repository root:

```sh
npm ci --ignore-scripts --prefix tests/interop
dotnet build -p:RestoreLockedMode=true
dotnet test --no-build --filter "Category=Interop"
```

Node 24 is installed by the mandatory interop CI job. Missing Node or dependencies
fail the tests; nothing silently skips or fetches an unpinned package during execution.
The six cases cover both directions over stdio and HTTP JSON/SSE; HTTP cases include
bidirectional nested sampling, tools, Unicode/newlines, resources, prompts and shutdown.
The official client also executes the documented .NET stdio example. Each subprocess
has a deadline, captures diagnostics, and is terminated with its process tree on failure.

The ordinary three-platform test job excludes Category=Interop but independently
enforces the protocol/schema corpus and critical coverage. Local full `dotnet test`
runs require the Node setup above.
