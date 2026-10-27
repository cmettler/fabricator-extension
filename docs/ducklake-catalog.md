# DuckLake with a SQL Server metadata catalog

Status: **works on box SQL Server AND on a live Fabric SQL database with `META_HIDDEN_ROWID true`** (2026-10-27,
branch `feat/ducklake-catalog`; §6 is the as-built record, §10 the Fabric run). Goal: run DuckLake with its metadata in SQL Server, reached through our extension,
**without changing the DuckLake extension**.

Measured with the official signed DuckLake build `417a9117f5` (`INSTALL ducklake`) loaded into our
`v2.0.0` build. The version string matters: a dev build (`v2.0.0-devNNN`) cannot load signed extensions.
DuckLake source read at the same commit.

## 1. How DuckLake reaches the metadata catalog

- `ATTACH 'ducklake:fabricator:<SqlClient connstr>' AS lake (DATA_PATH '…', METADATA_SCHEMA 'dbo')`.
  DuckLake strips `ducklake:`, takes `fabricator` as the metadata TYPE and issues
  `ATTACH OR REPLACE 'fabricator:<connstr>' AS __ducklake_metadata_lake (…)`, which reaches our storage
  extension unchanged. Every `META_<x>` option on the outer ATTACH becomes `<x>` on the inner one, so our own
  ATTACH options (PROVIDER, SECRET, …) can be passed.
- DuckLake keeps a registry of metadata managers by catalog type (`postgres`, `sqlite`, `quack`). The Postgres
  manager sends every statement as text through `postgres_execute`/`postgres_query`. **An unknown type gets the
  default manager, which runs plain DuckDB SQL against the attached catalog** — so for us every metadata
  statement goes through our binder-level DML/DDL. `DuckLakeMetadataManager::Register` exists, but it is C++
  inside the DuckLake binary and cannot be called from another loadable extension.

## 2. What works (measured)

- ATTACH creates the metadata schema in an empty SQL Server database (30 tables; the five that DuckLake
  declares with a PRIMARY KEY get one).
- CREATE TABLE, INSERT, SELECT, re-attach, snapshot history (`ducklake_snapshots`).
- **With `DATA_INLINING_ROW_LIMIT 0`: UPDATE and DELETE are correct** (delete files written, snapshots right).
  The single-table UPDATEs on stats tables (`… WHERE table_id = n`) run through our predicate shortcut
  (ABI v93/v94, logged as `update-where`), although those tables have no key.

## 3. What fails, and why it is one class

Every failure is a **set-based UPDATE/DELETE on a keyless metadata table whose WHERE our exact renderer does
not take** (a join or a subquery). Our row-id path then needs a key and there is none:

| trigger | statement shape | table |
|---|---|---|
| UPDATE/DELETE with data inlining (the default) | `UPDATE inlined SET end_snapshot = … FROM (VALUES …) WHERE row_id = …` | `ducklake_inlined_data_<t>_<v>` |
| `ducklake_merge_adjacent_files`, `CHECKPOINT` | `UPDATE ducklake_table_stats SET file_size_bytes = (SELECT SUM(…) …) WHERE table_id = n` | `ducklake_table_stats` |
| `ducklake_expire_snapshots` | `DELETE FROM t WHERE NOT EXISTS (SELECT … WHERE … = t.x)` | `ducklake_view`, `ducklake_macro_*`, … |

The error surfaces as DuckLake's commit retry loop exhausting its 10 attempts, which reads like contention.

## 4. Other findings

- **Our catalog reports DuckDB's default schema `main`**, because `FabricatorCatalog` does not override
  `Catalog::GetDefaultSchema()`; SQL Server's is `dbo`. DuckLake asks for it when `METADATA_SCHEMA` is not
  given and fails with *schema "main" not found*. Workaround: `METADATA_SCHEMA 'dbo'`. Overriding it would also
  change how `db.t` resolves for every SQL Server catalog, so it is a decision of its own.
- Not yet measured: two writers committing concurrently (DuckLake detects a conflict through a PRIMARY KEY
  violation on `ducklake_snapshot`), Fabric Warehouse as the metadata host, cloud data paths.

## 5. Options for the failing class

1. **A hidden row id for keyless tables our catalog creates** (generic, not DuckLake-specific): an
   `IDENTITY` column the catalog uses as row id but does not report in the table schema. Our row-id code
   already accepts an IDENTITY column as row id. Every failing statement then takes the existing row-id path.
   Opt-in per ATTACH, reachable through DuckLake as a `META_` option.
2. **Keys from DuckLake's schema knowledge**, added by a CREATE TABLE hook (e.g. `(_ducklake_row_id,
   _ducklake_begin_snapshot)` on inlined tables). Couples us to DuckLake's schema version by version.
3. **Push the whole DML statement to SQL Server** (2.0 remote optimizer + T-SQL for DELETE/UPDATE with
   subqueries). Largest, and our Substrait path covers SELECT only.

## 6. As built: `hidden_rowid` (option 1)

`ATTACH 'ducklake:fabricator:<connstr>' AS lake (DATA_PATH '…', METADATA_SCHEMA 'dbo', META_HIDDEN_ROWID true)`.
DuckLake hands `META_HIDDEN_ROWID` to our inner ATTACH as `hidden_rowid`, so every metadata table it creates
without a key gets a `BIGINT IDENTITY` column `[__fabricator_rowid]`. C#-only, no ABI change, gate
`verify_hidden_rowid` (service tier).

- **Hidden, not visible — measured, not a preference.** DuckLake writes its commit with positional
  `INSERT INTO … VALUES (…)` (no column list) in about a dozen places (`ducklake_staged_commit.cpp`), e.g. into
  the keyless `ducklake_table_stats`. DuckDB refuses a missing trailing value even when the column has a
  default (*"table "t" has 3 columns but 2 values were supplied"*), so a visible identity breaks every commit.
- **Why not a UNIQUE index instead.** It needs to know which columns ARE unique, which only DuckLake knows
  (option 2's coupling); SQL Server cannot index `VARCHAR(MAX)`, our default text type; a unique index admits one
  NULL; and DuckLake updates exactly the columns (`end_snapshot`) a natural key would contain.
- **What makes it small:** the host already treats a rowid name that matches no visible column as a VIRTUAL
  BIGINT rowid (the Delta `_metadata.row_id` path), and `RowIdSql` already picks an IDENTITY column on a keyless
  table. The provider only had to add the column at CREATE/CTAS and leave it out of what DuckDB sees.
- **Three places hide it, and the third was found by the gate:** the table schema (`ColumnsSchemaCore`), the
  `RETURNING` result (`OUTPUT INSERTED.*`), and every catalog scan that projects `*` — the bind-time describe
  among them. ⚠ The host maps those columns BY POSITION, so with the hidden column anywhere but last (an
  `ALTER … ADD COLUMN` puts new columns after it) every later column shifted and DuckDB raised
  *"INTERNAL Error: Vector::Reference used on vector of different type"*. The same holds for `RETURNING`, and
  the first version of the gate missed it because its RETURNING ran before the ALTER, where the column is last.
- It is created only with the option and only on a table with no PRIMARY KEY / UNIQUE / IDENTITY marker; it is
  hidden on every catalog, so an attach without the option sees the same columns and still uses it as rowid.

**Measured with DuckLake `417a9117f5`, default data inlining, fresh metadata database:**

- CREATE, inlined INSERT/UPDATE/DELETE, 2000-row INSERT + range DELETE/UPDATE: counts and sums exact
  (1902 rows, sum 2004053, by hand).
- `ducklake_flush_inlined_data` (14 rows), `ducklake_merge_adjacent_files` (4 files → 1), `rewrite_data_files`,
  `expire_snapshots`, `cleanup_old_files`, `delete_orphaned_files`, `CHECKPOINT`: all succeed, data exact
  afterwards (4904 rows) and after a re-attach.
- The statement that used to fail now takes the row-id path:
  `UPDATE [dbo].[ducklake_table_stats] SET [file_size_bytes] = @s0 WHERE [__fabricator_rowid] = @k1`. Simple
  single-table UPDATE/DELETEs still take the predicate shortcut (`update-where`, `delete-where`).

**Still open:** the default schema (`METADATA_SCHEMA 'dbo'` is still needed,
§4); Delta metadata over DuckLake's data files.

## 7. Concurrent writers (measured 2026-10-27)

Several duckdb PROCESSES commit to one DuckLake catalog at once (scratchpad harnesses `dl_race.sh`,
`dl_race_dml.sh`). Three local variants of the metadata database: **box** (`SQL_Latin1_General_CP1_CI_AS`, RCSI
off, i.e. locking READ COMMITTED), **Fabric-like** (`Latin1_General_100_CI_AS_SC_UTF8`, `READ_COMMITTED_SNAPSHOT
ON`, `ALLOW_SNAPSHOT_ISOLATION ON` — the Azure SQL defaults a Fabric SQL database is expected to have), and
Fabric-like with `META_READ_ISOLATION 'snapshot'` (the DuckDB transaction — every read and the writes — on one
SNAPSHOT transaction).

- **How DuckLake detects a conflict:** every commit inserts the next `snapshot_id`; a concurrent commit makes that a
  PRIMARY KEY violation (SQL Server 2627), and DuckLake retries any error whose text contains `primary key`,
  `unique`, `conflict` or `concurrent`. ⚠ A DEADLOCK (1205, "chosen as the deadlock victim") contains none of
  them and would NOT be retried — none occurred in any run.
- **Appends, 8 writers × 25 single-row INSERTs:** ~90 retried PK conflicts per run. With the default
  `ducklake_max_retry_count` (10) about 4 commits fail LOUDLY (*"Exceeded the maximum retry count"*), with
  `SET ducklake_max_retry_count = 100` all 200 land, `ducklake_snapshot` contiguous, no duplicates.
  **Identical in all three variants.**
- **DELETE + UPDATE of each writer's own rows in one shared table** (data files, and with 8 rows per writer the
  INLINED table — 0 data files, so the hidden-rowid UPDATE path under concurrency): most commits fail with DuckLake's
  own *"Transaction conflict - attempting to delete from table … but another transaction has inserted into it"*
  (an UPDATE is a delete plus an insert, and DuckLake does not retry that). **DuckLake's SQLite backend under the
  same load loses the same share with the same message** (e.g. 116 vs 120 of 160), so it is DuckLake's semantics,
  not this catalog. Every run balances exactly: failed + applied = issued; nothing silently lost or doubled.
- **⇒ The isolation level made no measurable difference**, and none was needed: READ COMMITTED, RCSI and
  snapshot reads all behaved the same, with no deadlocks, no lock timeouts and no update conflicts (3960). Leave the
  database at its defaults; do not set `read_isolation 'snapshot'` for DuckLake (no gain, and it pins a
  connection per transaction).
- **Which option sets which level (by design, not a defect):** `isolation_level` / `SET mssql_isolation_level`
  scope table-in-out sessions ONLY. The DuckDB transaction's pinned connection — writes, and reads under the opt-in —
  takes its level from ONE resolver (`ResolveTxnIsolation`): `read_isolation` when set, else the profile default
  (`snapshot` on Fabric Warehouse / Synapse serverless, the server default on box / Azure SQL). Kept apart on
  purpose: reusing `isolation_level` would have switched the per-transaction read pin on for anyone who had set it.
  ⚠ So a first run with `META_ISOLATION_LEVEL 'snapshot'` was VOID (it changed nothing here), while the
  `META_READ_ISOLATION 'snapshot'` run put reads AND writes under SNAPSHOT — confirmed in the trace
  (`route=read_isolation pin`).
- **Fabric SQL database, not Fabric Warehouse.** The Warehouse emits PK/UNIQUE as `NOT ENFORCED` hints, which
  would remove the very PRIMARY KEY violation DuckLake's conflict detection rests on (reasoned, not measured).
  A Fabric SQL database is Azure SQL underneath and enforces keys. ⚠ Unverified live: that its
  `SERVERPROPERTY('EngineEdition')` is outside `ServerProfile.IsWarehouse` (editions 6 and 11), which decides
  whether we emit enforced keys.

## 8. Round trips per commit, and the bind-time describe cache (2026-10-27)

One writer, 100 single-row commits + 20 reads, Fabric-like database, local docker (so latency is near zero and
these are LOWER bounds for a remote server):

| configuration | wall | SQL statements | of which `WHERE 1 = 0` describes |
|---|---|---|---|
| default, before the cache | 7.2–7.4 s | 3210 (~26 per operation) | 1264 |
| `META_READ_ISOLATION 'snapshot'` | 8.0–8.7 s | 3210 | — |
| `META_REMOTE_PUSHDOWN 'true'` | 7.3–7.5 s | 2802 (120 of DuckLake's joins pushed as one statement) | — |
| **describe cache (now the default)** | **6.35 s** | **1995 (~16 per operation)** | **49** |

- `read_isolation` is SLOWER: by default reads already run on POOLED connections in parallel, without MARS; the
  opt-in moves every read of the transaction onto ONE pinned connection.
- Remote pushdown was off by default when this was measured (it is ON since 2026-10-27, §9); with these tiny tables it saves
  statements but no time — but see §9: it is what keeps a commit's cost flat as the snapshot history grows.
- **The describe cache.** Every table reference in every statement asked SQL Server for the table's Arrow schema
  (`SELECT * FROM t WHERE 1 = 0`) — 40% of all statements. A table entry now reuses its FIRST describe for later
  binds, on a provider declaring the describe side-effect-free (capability `bind_schema_cacheable`, SQL Server
  only; the Delta describe seeds the statement's snapshot pin and keeps running). Never for an AT reference.
  Kill switch `SET fabricator_bind_schema_cache = false`. It lives exactly as long as the entry, and every DDL
  through the catalog, a ROLLBACK and the cache functions replace the entry.
- **Another process changing the table.** The entry's column list was ALWAYS stale until a refresh — the
  per-bind describe never refreshed it, it only produced a second, disagreeing schema, and a changed column type
  (and, without the cache, a dropped column) then failed inside `Vector::Reference` with an INTERNAL error that
  INVALIDATED THE DATABASE. The scan now refuses a type mismatch with a clear error; the failed statement's
  rollback drops the entries, so the next statement sees the new schema. Gate `verify_bind_schema_cache` (85,
  service tier; three mutants: a cache outliving the entry dies at the first ALTER, an ignored kill switch at
  its positive control, the missing type guard at the out-of-band type change).

## 9. Full reads, and why `META_REMOTE_PUSHDOWN 'true'` is the recommendation (2026-10-27)

Which metadata scans carry a WHERE, from the trace of the §8 run (100 commits + 20 reads, no pushdown):

- **Filtered:** `ducklake_data_file`, `ducklake_delete_file`, the inlined-data tables, `ducklake_metadata`,
  `ducklake_file_column_stats`, `ducklake_inlined_data_tables` — DuckLake's simple comparisons are pushed by the
  ordinary scan pushdown.
- **Read in FULL on every commit:** **`ducklake_snapshot`, twice** (240 full scans for 120 operations), plus
  `ducklake_table_stats` and `ducklake_table_column_stats` once each. The snapshot read is DuckLake's
  `… WHERE snapshot_id = (SELECT MAX(snapshot_id) FROM ducklake_snapshot)`: a comparison against a SUBQUERY is not
  a scan filter, so both the outer scan and the MAX are full reads. The two stats tables grow with tables and
  columns; `ducklake_snapshot` grows by ONE ROW PER COMMIT, so without pushdown every commit is linearly dearer.
- **With `META_REMOTE_PUSHDOWN 'true'`** the whole query runs as one T-SQL statement (`… INNER JOIN (SELECT
  MAX([snapshot_id]) …)`), which SQL Server answers from the primary key.

Measured with synthetic history rows (negative ids, so MAX and the live chain are untouched), 50 commits each:

| rows in `ducklake_snapshot` | no pushdown | `META_REMOTE_PUSHDOWN 'true'` |
|---|---|---|
| ~50 | 78 ms/commit | 79 ms/commit |
| 50,000 | 165 ms/commit | 81 ms/commit |
| 200,000 | **335 ms/commit** | **81 ms/commit** |

Pushdown also holds under concurrency: the 8 × 25 append race lands 200/200 (21 s, against 37–40 s without it),
and the inlined DELETE/UPDATE race balances exactly (failed + applied = issued, same DuckLake conflicts as §7).
⇒ **`remote_pushdown` is therefore ON BY DEFAULT since 2026-10-27** (opt out with `remote_pushdown 'false'`,
through DuckLake `META_REMOTE_PUSHDOWN 'false'`); a query the translator cannot write exactly still runs locally.
Local docker, so remote latency comes on top of both columns; the slope is the point.

## 10. A live Fabric SQL database as the metadata host (measured 2026-10-27)

A Fabric SQL database (`<id>.database.fabric.microsoft.com`) reached by the service principal of the
`fabric_sp` secret (`TYPE azure`, so `LOAD azure` first; passed as `META_SECRET 'fabric_sp'`), data files on a
local path:

```sql
ATTACH 'ducklake:fabricator:Server=<host>,1433;Database=<db>;Encrypt=True' AS lake
  (DATA_PATH '…', METADATA_SCHEMA 'dbo', META_SECRET 'fabric_sp', META_HIDDEN_ROWID true);
```

- **Profile:** `EngineEdition` 12 (SQL Azure, 12.0.2000.8) ⇒ `is_warehouse = false`, so primary keys are created
  ENFORCED, which DuckLake's conflict detection needs (§7). The database has `SQL_Latin1_General_CP1_CI_AS`
  (not a UTF-8 collation, as §7 assumed), RCSI on, snapshot isolation allowed, compatibility level 170.
- **The §6 sequence gives the same answers as on box:** inlined UPDATE/DELETE, 1902 rows / sum 2004053, flush,
  merge, rewrite, expire, cleanup, orphan delete, CHECKPOINT, final 1904 / 2014054, the same after a re-attach.
  31 metadata tables, 5 with a primary key. Single statements took 0.06–1.0 s (network latency included).
- **8 writers × 25 inserts** (`SET ducklake_max_retry_count = 100`): 87 PK conflicts retried, 200/200 rows,
  no duplicates, snapshots 0..201 without a gap, no deadlock, no timeout; 100 s (21 s against local docker).
- **Found and fixed: `CREATE SCHEMA` failed for the service principal** — 33134 *"Principal … could not be
  resolved … Server identity is not configured"*. The principal has no database user of its own (it is admitted
  through its workspace role and runs as principal 0, `public`, while being a `db_owner` member), so the schema's
  default owner cannot be resolved. It hit DuckLake as soon as `METADATA_SCHEMA` named a schema that did not
  exist yet. `CreateSchema` now adds `AUTHORIZATION [dbo]` when `DATABASE_PRINCIPAL_ID() = 0`; any other caller
  is unchanged (box with `sa` is principal 1 and still gets `dbo` the ordinary way). Not gated: no CI tier
  reaches Fabric.
