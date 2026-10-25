# mssql-adbc Substrait → T-SQL translator (vendored)

The Substrait plan decoder and T-SQL emitter from clast-project's **mssql-adbc**
(`src/Clast.Drivers.SqlServer/Substrait`, commit `677676c`, 2026-10-21), Apache License 2.0 (see `LICENSE`).
Some files derive from the Kusto ADBC driver under the MIT License (see `THIRD-PARTY-NOTICES.md`); every file keeps
its original copyright header.

Only the translation is taken: no ADBC driver, no dialect YAML resource. Every change against the original is
marked `// [FABRICATOR-PATCH: ...]` with its reason, so the copy can be diffed against upstream. Used by the remote
SELECT pushdown (docs/duckdb-2.0-remote-execution.md §3.4–§3.7).
