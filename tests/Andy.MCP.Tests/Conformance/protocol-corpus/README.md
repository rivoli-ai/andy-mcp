# Protocol definition corpus

Run `python3 scripts/generate-protocol-corpus.py` from the repository root to regenerate
these deterministic, schema-derived fixtures. They are not upstream example messages.
The frozen official inputs and their SHA-256 provenance are in `../schemas/sources.json`.

Every definition in every negotiated revision has minimal/full and applicable union,
enum and scalar-type cases (1,002 total). This is definition coverage, not an exhaustive
Cartesian product of all nested choices. The test independently validates each input
and its model round trip against the official schema, and checks extension preservation.
`models.json` records the official-definition to public-CLR-type mapping. Generic RPC
envelopes share the RPC models; open result unions use PaginatedResult's extension bag.
Base/mixin definitions are exercised through a concrete implementing model.

Runtime boundary validation embeds byte-identical copies of the three negotiated schemas.
It rejects invalid standard requests/results/notifications and unavailable definitions,
while preserving custom method extensions. Schema resolution cannot fetch from the network.
2025-03-26 remains frozen for conversion audits but is not negotiated: receiving batches
is mandatory in that revision and is not implemented.
