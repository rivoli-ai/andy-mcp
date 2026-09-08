# High-level operation contracts

2026-09-08: registration is serialized under a lock and freezes when `McpServer.RunAsync`
starts. Configure tools, resources, templates, prompts, completions and logging beforehand.
Starting the same server twice or mutating registration afterward throws. Runtime event
notifications remain available when their corresponding list-change capability was advertised.

`AddResource(Resource, handler)` accepts the full resource descriptor and a reader returning
multiple content entries. `AddPrompt(Prompt, handler)` accepts the full prompt descriptor.
Required prompt arguments are checked before invoking the handler. Values are passed literally;
handlers own substitution and must explicitly decide how values appear in prompt messages.

Server sampling checks `sampling.tools` when tool definitions, tool choice or tool content is
present and checks `sampling.context` for non-empty context in 2025-11-25. Older revisions
cannot receive sampling tool features. Elicitation checks the negotiated revision and selected
form/URL sub-capability; an empty elicitation capability retains legacy form-only behavior.
Client dispatch applies the same checks before invoking user handlers.

`McpServer.RootsChanged` receives permitted root-list notifications. A root provider's
`listChanged` setting is honored and event subscriptions are removed on client disposal.
`NotifyElicitationCompleteAsync(ElicitationCompleteParams, ct)` and
`McpClient.ElicitationCompleted` expose URL completion IDs and metadata.

Use a client ping round trip before server-originated work when constructing both peers in
one process; it establishes receipt of the initialization notification. Runtime notifications
require a ready session. Verification: `HighLevelContractTests` and existing bidirectional,
completion, subscription, progress and task augmentation tests.

Safe custom requests, per-call controls and additional shape validation remain tracked in #48.
