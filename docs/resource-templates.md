# Resource-template reads

2026-09-08: `AddResourceTemplate(template, name, handler)` registers a handler receiving
the original URI, decoded variables and a cancellation token. It may return multiple
`ResourceContents` entries. Static resources take precedence; otherwise the first matching
registered template wins. An unmatched or malformed expansion returns resource-not-found
(-32002) without invoking a template handler. Invalid template syntax fails at registration.

Matching supports simple, reserved, fragment, label, path, matrix, query and continuation
operators, multiple variables, prefix lengths and exploded expansions. Prefix length counts
Unicode scalar values. Repeated variable occurrences must agree, including prefix constraints.
Optional expressions can be absent. URI inputs are capped at 32768 characters and template
length at 4096; matching uses the non-backtracking regex engine with a timeout.

RFC6570 specifies expansion, which is not uniquely invertible. The handler contract returns
strings: composite values keep decoded separators (`red&green` for an exploded query list,
`first=red&second=green` for an exploded query map). Positional ambiguity is resolved from
left to right; the final variable receives remaining values. Use distinct named query variables
or literal delimiters when exact extraction is important. Matching does not recover a unique
original list/map type, validate application identifiers, or authorize access to the resource.

Tests: `UriTemplateOperatorsTests`, `ResourceTemplateHandlerTests` and `UriTemplateCorpusTests`.
The latter recognizes 331 upstream RFC example expansions, frozen with source URLs and hashes
in `tests/Andy.MCP.Tests/Conformance/uri-templates/sources.json`; it tests matching, not expansion.
