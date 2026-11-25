# The adbc provider against a quack server (manual)

Differential checks of fabricator → adbc provider → quack-net's ADBC driver (`quack_adbc`, branch `substrait`) → a
DuckDB 2.0.1 server serving quack, against the same statements in the local DuckDB (docs/adbc-provider.md, "quack").
Each script starts its own server (a DuckDB CLI on a free port) and needs a Python with `duckdb==2.0.1`:

| script | what |
|---|---|
| `quack_edges.py` | the pushdown edge cases (`scripts/pushdown_edge_cases.py`) plus DuckDB-only ones; reads, pushed queries, a TimeZone control |
| `quack_dml.py` | DELETE / UPDATE / INSERT … SELECT, a transaction, read-your-writes + ROLLBACK |
| `quack_ddl.py` | CREATE TABLE with 16 types, INSERT VALUES, CTAS, DDL in a transaction, schemas, DROP |
| `quack_alter_merge.py` | ALTER TABLE, MERGE, the key path, RETURNING, CREATE TABLE defaults |
| `quack_txn.py` | transactions: one server snapshot per explicit transaction (and the `transaction_snapshot false` control), read-your-writes, CREATE TABLE after a write, buffering only on overlap (with a positive control), a write conflict, concurrent readers |
| `quack_overlap.py` | statements that read and write the same session (non-pushed INSERT … SELECT, CTAS, UPDATE … FROM, UNION ALL of local and remote in a transaction): finish, right rows; `PROBE_REPEAT=n` |
| `capture_plans.py` | captures the host's Substrait plan of one query (quack-net's test fixtures) |

```bash
export QUACK_ADBC_DRIVER=…/quack-net/src/Quack.Adbc.Native/bin/Release/net10.0/win-x64/publish/quack_adbc.dll
export QUACK_EDGES_LOADABLE=…/fabricator.duckdb_extension   # default: build/release-v2
export QUACK_DUCKDB_CLI=…/duckdb.exe                         # default: build/release-v2/duckdb.exe
python -I scripts/adbc_quack/quack_edges.py <managed dir> [case ...]
```
