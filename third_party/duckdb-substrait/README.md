# duckdb-substrait (vendored producer)

The DuckDB -> Substrait producer (`to_substrait`) from
[substrait-io/duckdb-substrait-extension](https://github.com/substrait-io/duckdb-substrait-extension),
commit `97a46c7` (2026-10-19), MIT licensed (see `LICENSE`). Only the producer is taken; the consumer
(`from_substrait`) and the extension entry are not.

Upstream pins DuckDB 1.5.6; this copy is ported to DuckDB 2.0. Every change against upstream is marked
`// [FABRICATOR-PATCH: ...]` with the reason, so the copy can be diffed against upstream and dropped once
upstream supports 2.0. Used by the remote SELECT pushdown (docs/duckdb-2.0-remote-execution.md §3.4).
