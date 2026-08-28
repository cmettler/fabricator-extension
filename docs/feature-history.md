# Feature history — as-built records

> Moved VERBATIM out of `CLAUDE.md` on 2026-07-29 (conservative split — see the commit message).
> Append-only as-built history; the live summary + pointers stay in `CLAUDE.md`.
> Paths/links inside are REPO-ROOT-relative (the text was written for `CLAUDE.md`).

- **SINGLE-FILE DISTRIBUTION — BUILT AND WORKING ON WINDOWS *AND LINUX* (2026-07-25; phases 1-4 of 5
  — remaining: user-facing docs + CI): [docs/distribution-installer.md](docs/distribution-installer.md)
  §12 (spike) + §14 (Installer.Core) + §15 (AOT shell + packaging) + §16 (linux).** ONE
  `fabricator.duckdb_extension` now installs and runs itself: `LOAD` it into an EMPTY extension
  directory with ZERO env vars → ~2 s cold (extract + chain-load + CLR boot), **0.01 s warm**, then
  `fabricator_version()`, managed calls and a Delta CTAS round trip all work. **Both platforms pass
  the same 12 checks** (`test/distribution/smoke_distribution.py`, incl. both must-not-touch-disk
  rejections): **windows_amd64 61 MB standalone** (self-contained runtime) + **linux_amd64 40 MB
  standard** (framework-dependent — the Fabric-notebook-relevant SKU; the bridge booted on the
  preinstalled runtime with NO env vars). Build with `scripts/pack-distribution.ps1` (core → managed
  publish → AOT publish → pack → footer; each step skippable, `-WithNegatives` emits the harness's
  failure-path artifacts, `-CorePath`/`-ManagedPath` let ONE machine assemble another platform's
  artifact since only the C++ core + AOT shell must be built on-target). Ship ONE
  `fabricator.duckdb_extension` per (DuckDB version × platform × SKU): a **NativeAOT C#
  installer extension** (on the MIT `DuckDB.ExtensionKit`, `D:\repos\DuckDB.ExtensionKit` —
  C_STRUCT ABI ⇒ DuckDB-version-portable outer shell) carrying the real payload
  (`fabricator_core.duckdb_extension` = today's CPP-ABI loadable with an added forwarding
  `fabricator_core_duckdb_cpp_init` export, + the `fabricator/` managed dir, FDD ~20 MB or
  self-contained ~100 MB SKU) as a **polyglot append** (lib + payload + index + the DuckDB
  footer via `append_extension_metadata.py` — a plain file append, no resource compiler).
  At every load: version/platform gate (`PRAGMA version/platform` vs manifest, friendly
  error) → extract into `<extdir>/extensions/<ver>/<platform>/` (sha-marker idempotent,
  lock + staging) → nested `duckdb_query("LOAD '<extdir>/fabricator_core…'")`. Zero env
  vars: clr_host's default lookup is a hardcoded `fabricator/` next to the loaded module
  (clr_host.cpp:345). Source-verified keystones: nested LOAD is lock-safe (per-extension
  locks; we already autoload parquet during our own load), C_STRUCT footer checks the C API
  semver not the DuckDB version, name-LOAD works without an `.info`. Why C# not C++: resource
  embedding in C++ is 3 per-OS toolchain mechanisms (untestable off-platform, the macOS
  problem); the C# extraction/packaging logic is ONE code path, unit-testable as plain xunit
  anywhere — AOT only at publish (no cross-OS compile; per-OS machines still build+smoke).
  `allow_unsigned_extensions` stays required (both loads; unchanged vs today).
  **BUILT so far:** the spike validated every keystone on Windows against the OFFICIAL
  `duckdb==1.5.5` wheel — a C_STRUCT AOT kit extension loads, discovers its own
  `.duckdb_extension` path, reads a polyglot payload, and **chain-LOADs the CPP-ABI core, whose
  CLR boots with zero env vars** (managed calls `hilbert_index`/`bucket` prove it, not just
  `fabricator_version()`); a **138 MB artifact loads in 0.56 s** (R1 retired). Then
  **`dotnet/Fabricator.Installer.Core`** (net10.0;net8.0, `IsAotCompatible`, **zero package refs**)
  + `.Tests` — **91 tests green on both TFMs**: deterministic packer, polyglot writer/reader,
  extdir resolution, compatibility gate, extractor, staging/lock/promote state machine; plus a
  gated real-payload E2E (`FABRICATOR_E2E_CORE`/`_MANAGED`) that packs the built core + the 115 MB
  managed dir in ~4 s and whose install tree **loads in the official wheel with no env vars**.
  **Measured: the standalone artifact is 58 MB, not the estimated 90-110.**
  **FOUR FINDINGS (details in §14):** (1) the extension-directory rule in the design was WRONG —
  a custom `extension_directory` gets **NO `extensions/` component** (it exists only because the
  DEFAULT base string is `~/.duckdb/extensions`), and the plural `extension_directories` LIST
  setting is a second source; empirically pinned by watching where `INSTALL '<file>'` lands.
  (2) A zip's DOS timestamp has no timezone — .NET encodes the wall-clock part verbatim (so a
  fixed UTC epoch IS reproducible across build machines, which the payload-sha-as-marker scheme
  requires) but reattaches the READER's local offset on read; pinned at the byte level.
  (3) The payload must be packed at stream position 0 — zip offsets are stream-absolute, so an
  archive written at an offset is unreadable through the payload window. (4) Windows
  upgrade-in-use is largely SOLVED, not just reported: displaced files are RENAMED aside (the
  loader opens images with `FILE_SHARE_DELETE`, so rename is permitted where delete is not), so
  an upgrade succeeds while another session holds the old core loaded. Also **confirmed the
  forwarding export is genuinely required**: the same bytes that load as
  `fabricator.duckdb_extension` fail as `fabricator_core.duckdb_extension` (filename-derived
  entry symbol) — hence `CoreFileName` is a manifest field so today's name can be produced.
  **PHASE 3 (§15) added:** the core's **second entry point**
  `DUCKDB_CPP_EXTENSION_ENTRY(fabricator_core, loader)` (same `LoadInternal`; one binary, two file
  names), `dotnet/Fabricator.Installer` (the 2.9 MB NativeAOT shell — own-path discovery, gate,
  install, chain-LOAD; ~60 lines of flow), `dotnet/Fabricator.Installer.Pack` (build-time CLI over
  the Core so the ps1 is pure orchestration), `scripts/pack-distribution.ps1`, and
  `test/distribution/smoke_distribution.py`. **THREE MORE FINDINGS:** (1) the distinct core name is
  a HARD REQUIREMENT, not cosmetics — `BeginLoad` locks per extension NAME and a path-LOAD derives
  the name from the FILENAME, so an installer named `fabricator` chain-loading a file that also
  resolves to `fabricator` would block on its own load lock (this reordered the plan: the dual entry
  symbol had to land BEFORE the shell). (2) **`duckdb_fetch_chunk` takes `duckdb_result` BY VALUE**
  (a 48-byte struct) while the kit's mirror types it as a pointer — ABI-correct only where large
  structs pass indirectly (Windows x64, AArch64), WRONG on x64 SysV (linux/macOS-x64) where it is
  copied onto the stack; the shell re-types the function pointer so the compiler emits the right
  convention per platform (found by reading duckdb.h, not by debugging Linux). (3) the shell
  deliberately AVOIDS `duckdb_value_varchar`/`duckdb_row_count` (3 lines vs 40) because duckdb.h
  marks them "scheduled for removal" and the installer is the version-PORTABLE half — their removal
  would null the struct slot = a crash; also any deprecated accessor marks the result
  `CAPI_RESULT_TYPE_DEPRECATED`, after which `duckdb_fetch_chunk` returns null, so mixing is
  forbidden. **sqllogictest is the WRONG harness** for the distribution (our `unittest` embeds
  fabricator statically → the chain-loaded core would collide with registered functions) — hence the
  python harness against a stock wheel. Two build traps encoded in the scripts: an AOT project must
  CLEAR the repo-wide `TargetFrameworks` (a single-value plural still counts as cross-targeting), and
  `dotnet run` forwards an unrecognized `--nologo` to the program. **Loading BOTH spellings in one
  process is unsupported** (hostfxr initializes once per process; the second module's CLR fails and
  its global functions never register).
  **PHASE 4 (§16) — LINUX DONE + the by-value ABI fix CONFIRMED on x64 SysV** (the platform where the
  kit's pointer-shaped `fetch_chunk` signature would have been wrong: the gate + extdir resolution are
  built on those reads, so a bad ABI would have failed the load before extraction — it didn't). AOT on
  linux needed NOTHING beyond the distro clang 18 (no extra packages, and NOT
  `IlcUseEnvironmentalTools` — that's a Windows/vswhere workaround); `dladdr` own-path discovery works
  via the libc-then-libdl fallback. **THREE more findings:** (1) **negative test artifacts must be
  PER-PLATFORM** — DuckDB checks the footer's platform BEFORE any extension code runs, so a Windows
  negative on linux fails with "built for the platform windows_amd64" and proves nothing (it produced
  two falsely-green PASSes on the first linux run; now generated as siblings of the real artifact by
  `-WithNegatives`); (2) the publish output path is NOT stable across hosts — Windows lands under
  `bin/x64/Release/...`, WSL under `bin/Release/...` (the platform segment appears only when the build
  sets `Platform`), so the script PROBES both; (3) the harness's wheel must match the local interpreter
  — the cp310 wheel kept in `build/linux-payload/` for the Fabric flow will NOT install on Ubuntu
  24.04's python 3.12, and the harness must run from OUTSIDE the repo (the repo root's `duckdb/` source
  dir shadows the module). **The linux core was rebuilt (35 MB, now exports BOTH entry symbols, ABI
  v68) at `build/linux-dist/core/`** — NOTE `build/linux-payload/` (the separate Fabric-notebook
  payload: loadable + loose-root FDD zip + wheel) is STILL the Jul-23 v67 build and remains stale.
  Phasing: spike ✅ → `Fabricator.Installer.Core` ✅ → AOT shell + `pack-distribution.ps1` +
  the core dual entry symbol ✅ → linux artifact ✅ → user-facing install docs + CI matrix.

- **`CREATE TABLE … WITH (…)` options + SQL Server EXTERNAL TABLES —
  [docs/create-table-with-options.md](docs/create-table-with-options.md). ALL FOUR SLICES DONE
  (2026-07-19): A (ABI v67) + C + D + B.** DuckDB v1.5.4 parses the clause (`CreateTableInfo::options`).
  **A (DONE)** = `options_json` threaded through `create_table`+`begin_bulk` (v67; C++
  `TableOptionsArg` — constants only, flat string-valued JSON) → the Delta provider consumes THREE
  key kinds (`DeltaWithOptions.Parse`, guarded — unknown keys ERROR): per-statement **write tuning**
  (`parquet_compression`/`parquet_row_group_size`/`parquet_bloom_filter_columns`, DuckLake-parity
  names + bare aliases; precedence WITH > `delta_write_options` > ATTACH; CTAS-only — rejected on an
  empty CREATE), per-table **CREATE-flag overrides** (`deletion_vectors`/`column_mapping`/
  `row_tracking`/`change_data_feed`/`in_commit_timestamps` — the protocol-1.0 PolyBase recipe
  `WITH (deletion_vectors=false, column_mapping='none')` per table, no dedicated ATTACH), and
  `delta.*`/`fabricator.*` **TBLPROPERTIES stamped in the CREATE commit** (rides
  `DeltaWriteSpec.CreateProperties` → `CreateConfig` merge, WITH wins over derived keys;
  `delta.enable*`/`delta.columnMapping.*`/`fabricator.sortedBy` spellings rejected with pointers to
  the explicit keys/clauses). Buffered (explicit-txn) CREATE/CTAS parks the options on the txn
  buffer (`PendingAppends.CreateOptions`) and the flush applies them. **Same pass closed a
  PRE-EXISTING gap: the native_write COPY paths now carry the resolved tuning**
  (`NativeParquetDataFileWriter.CopyTuning` → `COMPRESSION`/`ROW_GROUP_SIZE` COPY options on
  RunCopy/RunCopyPartitioned + the per-file writer ctor) — previously `delta_write_options`
  compression only reached the EW codec writer (bloom COLUMNS stay codec-only; DuckDB blooms
  automatically). NOTE DuckDB's parquet writer has a row-group flush floor — tiny
  `parquet_row_group_size` values (< ~2048) coalesce. Two findings: the parser LOWERCASES all WITH
  keys (canonical re-casing of well-known `delta.*` keys C#-side; custom mixed-case keys → use
  `set_tblproperties`) and bools arrive as postgres `'t'/'f'` (accepted). SQL Server/DAX/deltars
  reject any options (never silent). `test/verify_with_options.test` (68 — tuning pins via
  parquet_metadata, precedence, both flag-override directions incl. empty CREATE + a DV-off catalog
  opting IN, property canonicalization + commit-0 stamp pin, buffered CTAS + ROLLBACK, 10 guards,
  Iceberg-shape no-ops) + `verify_with_options_mssql.test` (4). Regression: partition 54 / sorted_by
  30 / tblproperties 42 / native_write 147 / native_write_streaming 29 / copy_format 109 /
  transactions 941 / identity(delta) 38 / column_mapping 251 / dv_default 58 / columnstore 20 /
  identity(sql) 64 / cluster_by 18 / custom_functions 89 green.
  **C — DONE (2026-07-19, C#-only, no ABI): INSERT INTO an S3 EXTERNAL TABLE routes to STORAGE** —
  a capability SQL Server itself lacks (it can't INSERT into S3 external tables at all, and can't
  write Delta ever). `SqlServerCatalog.DetectExternalTable` (lazy `sys.external_tables` join probe,
  positive+negative cached per table, profile-tolerant, invalidated on DROP/CREATE) → Bridge-public
  **`ExternalTableRouting`**: DELTA = transient delta catalog over the parent folder
  (`BackendRegistry.Resolve("delta").OpenCatalog(root, native_write)` → BulkInsert parks →
  `CommitTransaction()` flushes ONE Delta commit — the C# mirror of the COPY(FORMAT delta) finalize);
  PARQUET = one `COPY … TO '<folder>/<uuid>.parquet'` host query. **Endpoint asymmetry**: SQL's
  LOCATION host (`minio:9000`) is discarded — the DuckDB s3 secret (bucket-scope match) supplies the
  client endpoint. Guards: explicit-txn rejection (`BeginTransaction(isExplicit)` now RECORDS explicit
  txn ids — `set_active_txn` precedes it, verified), `INSERT…RETURNING` rejection, non-s3/non-DELTA/
  PARQUET formats reject cleanly; appends never change table features so protocol-1.0 tables stay
  SQL-readable; `DROP TABLE` on a detected external table emits **`DROP EXTERNAL TABLE`**
  (metadata-only, data stays). `verify_mssql_s3_polybase` **167** (§6 Delta INSERT round-trip via
  OPENROWSET + catalog scan, guards; §6b parquet INSERT; §6c DROP routing); regression: SQL fn suites
  (26/33/24/31/63/13) + delta s3 161 / connection_mode 20 / time_travel 14 / orderby 7 green.
  **ENV REPAIR en route: the live SQL container was the PRE-compose `mssql-arrownet` while MinIO was
  on the new compose network (SQL couldn't resolve `minio:9000`, error 13807) — the compose
  `mssql-fabricator` service is now the live 1433 server (old container STOPPED, not removed;
  provision.ps1 re-run; fresh volume — suites self-provision).**
  **D — DONE (2026-07-19, C#-only): identity-keyed UPDATE/DELETE on detected external Delta tables.**
  A Delta IDENTITY column bridges the rowid domains — PolyBase-visible data column + standard stats,
  SNAPSHOT-INDEPENDENT (the only sound bridge; `_metadata.row_id` can NOT serve this: off-schema by
  spec + appends carry no physical id). The cached external probe also resolves the identity column
  (`ExternalTableRouting.FindDeltaIdentityColumn` — `delta.identity.*` field metadata); the entry's
  rowid OVERRIDES to it (`RowIdMetadata` branch — the standard identity-as-rowid machinery, zero
  C++); `ExecuteDelete`/`ExecuteUpdate` route to storage: identity→transient-rowid resolution via
  chunked PRUNED `ScanSpec` IN-scans (`{"columns":[id,"_metadata.row_id"],"filter":in}` + value
  batch, 500/chunk; superset-safe → exact client re-filter) → the delta provider's own rowid
  DELETE/UPDATE (CoW keeps protocol 1.0). UPDATE alignment trick: unresolved ids become NULL rowids,
  which the delta update parser SKIPS = concurrently-deleted-matches-nothing for free. Guards:
  SET-of-identity rejected, explicit-txn rejected (`GuardExternalDml`). `verify_mssql_s3_polybase`
  **209** (§6d: scan-via-SQL-Server + apply-via-Delta UPDATE incl. expression, DELETE, ids
  preserved, guards, zero-match DELETE); regression identity 64 / columnstore 20 / delta s3 161 /
  arrow_lossless 10 green.
  **B — DONE (2026-07-19, C#-only): `CREATE TABLE … WITH (location=…, table_type='DELTA'|'PARQUET'
  [, data_source=…, file_format=…]) [AS …]` on the SQL provider — the CETAS-analog, one DDL statement.**
  Data-first/DDL-second: `ExternalTableRouting.CreateDeltaAs` (CTAS, `copy_disposition:'error'`) /
  `CreateDeltaEmpty` (empty CREATE) write the client-side Delta table (protocol-1.0 plain, identity
  marker rides through → slice-D-capable from birth), then `ProvisionExternalTable` auto-creates the
  EXTERNAL FILE FORMAT (unless `file_format=` given) + EXTERNAL TABLE with a column list from the write
  schema (`BuildExternalColumnList` — one source of truth). `data_source=` REQUIRED (no-secret-material
  posture; `secret=` auto-provisioning rejected with a pointer). **Two type findings:** external text
  columns need explicit lengths with a TYPE-DEPENDENT cap — `VARCHAR(8000)` but `NVARCHAR(4000)` (8000
  is error 2717); the identity marker is ALLOWED with `location`+DELTA (declared plain BIGINT SQL-side).
  Guards: CREATE OR REPLACE / explicit-txn / PK-UNIQUE-DEFAULT / PARQUET-empty-create / ICEBERG rejected.
  **Pre-existing S3 bug found + fixed in the test:** an EMPTY `CREATE OR REPLACE TABLE t (cols)` over an
  EXISTING S3 delta table fails "version 0 already exists" (the DropTable+create-v0 path's post-delete
  `_delta_log` listing is stale within the one statement; a CTAS CREATE OR REPLACE is unaffected — it
  Overwrites). Workaround: separate `DROP TABLE IF EXISTS` + `CREATE` (the view settles across the
  statement boundary) — the polybase test's one empty-create-or-replace (iddml) uses the split.
  `verify_mssql_s3_polybase` **252** (§6e auto-provision DDL round-trip + INSERT compose + guards; §6f
  the FULL CIRCLE — empty CREATE + identity → INSERT → UPDATE → DELETE all through the SQL catalog),
  re-runnable back-to-back; `verify_with_options_mssql` 9; regression identity 64 / columnstore 20 /
  delta s3 161 / scalar 26 / procs 24 / native_write 147 green.

- **SQL-GENERATING TABLE FUNCTIONS — DONE (2026-07-24, ABI v68, global + catalog-bound; design + as-built:
  [docs/macros-and-sqlgen-functions.md](docs/macros-and-sqlgen-functions.md) §2).** The provider-authored
  twin of `query_table()`: a provider declares `ISqlTableFunction` (global, connection-free) or
  `ICatalogSqlTableFunction` (attach-time, `db.schema.fn(args)`) whose `GenerateSql(args)` returns a SQL
  statement; the C++ `bind_replace` hook (`FabricatorSqlGenBindReplace`) parses it and hands the binder a
  `SubqueryRef`, so **the function call DISAPPEARS at bind — the plan that executes is the generated SQL's,
  and NO data crosses the ABI at execution.** Two capabilities fall out for free: **the output schema is
  arg-dependent** (it's whatever the SQL binds to — nothing is declared C#-side), and **everything the SQL
  references keeps its own pushdown/parallelism**, including our own catalog scans. Registration =
  `TableFunction(name, args, nullptr /*scan*/, nullptr /*bind*/)` + `bind_replace` only; globals at load
  (decl kind `table_sql`), catalog-bound via discovery → `AddSqlTableFunction` → `GetOrCreateSqlTableFunction`
  (the entry carries `catalog.GetName()` = the ATTACH alias). **The catalog-bound generator gets a
  `SqlGenContext` (alias + the live `IBackendCatalog`), so it can LOOK THINGS UP at bind time** — which is
  what makes the flagship demo work: `db.dbo.cf_union_by_pattern('sg_sales_%')` queries `sys.tables` on the
  catalog's own connection and emits a `UNION ALL` over quoted three-part names. **Measured payoff:** a
  `WHERE yr = 2024` over that union reaches SQL Server as TWO statements, `(@p0 int)SELECT [id],[nm],[yr]
  FROM [dbo].[sg_sales_2024] WHERE [yr] = @p0` (+ the 2023 twin) — predicate AND projection pushed per member
  (pinned via `dm_exec_query_stats`); a marshaled `ITableFunction` would have streamed every row through C#.
  Globals: `fabricator_sql_seq(n[, cols := k])` (arg-dependent projection) + `fabricator_delta_union(paths[,
  by_name := …])` (UNION over Delta tables BY PATH — LIST/BOOLEAN constants cross fine).
  **Dividing rule vs macros: fixed SQL text + varying VALUES ⇒ macro; SQL TEXT depends on the args ⇒
  sqlgen.** Authoring contract (documented + enforced by shape): deterministic + side-effect-free (binds
  repeat: EXPLAIN/DESCRIBE/view re-bind), exactly ONE SELECT statement (a PIVOT without an explicit IN list
  is rejected — inherited from upstream's `ParseSubquery`), and quote what you splice
  (`DuckSql.QuoteIdent`/`QuoteName`/`Literal`). Errors all land at bind: NULL for a non-nullable parameter
  (per-parameter message — better than `query_table()`'s blanket rejection), the generator's own validation
  verbatim, unknown named param → DuckDB's candidate list, non-constant arg → "does not support lateral join
  column parameters". **Two gotchas worth keeping:** `EXPLAIN` is NOT usable as a subquery, so the
  "call vanished" pin uses `json_serialize_plan(sql, optimize := true)` (subquery-usable, inspects the
  optimized logical plan); and `Scan(TABLE_FUNCTION_ENTRY)` must enumerate `sql_table_functions_` or the
  function resolves by name but is invisible to `duckdb_functions()`. `test/verify_sqlgen.test` (59) +
  `test/verify_sqlgen_catalog.test` (30, needs `MSSQL_TESTDB_DSN`); regression: all 17 function suites +
  transactions 941 / native_write 147 / row_level_concurrency 70 green.

- **PROVIDER-DECLARED DuckDB MACROS — DONE (2026-07-24, C#+C++ lockstep, NO ABI bump; design +
  as-built: [docs/macros-and-sqlgen-functions.md](docs/macros-and-sqlgen-functions.md) §1).** A provider
  ships SQL TEMPLATES beside its marshaled functions: `IBackend.GlobalMacros` →
  `MacroDefinition(Name, CreateSql)` where `CreateSql` is a **complete `CREATE MACRO` statement**; the C++
  load-time registrar hands it to **DuckDB's OWN `Parser`** and registers the resulting `CreateMacroInfo`
  via `ExtensionLoader::RegisterFunction(CreateMacroInfo&)` into the **SYSTEM catalog** (like a built-in ⇒
  bare `fn(...)` / `FROM fn(...)` in EVERY database, no ATTACH). Because DuckDB parses it, the FULL grammar
  works with zero bespoke encoding: scalar AND table macros (the parsed statement carries the kind),
  named-parameter defaults, overload sets, no 8-param cap. **Nothing crosses the bridge at runtime** — the
  binder expands the macro, substituting parameters as EXPRESSIONS (structure/identifiers frozen at
  declaration ⇒ injection-free by construction). Declaration rides `list_global_functions` as kind `macro`
  + a new leading `body` string column (`ReadStringTable(stream, 4)`) — the `string_order` no-bump
  precedent. **Dividing rule vs the (planned) SQL-generating table functions: fixed SQL text + varying
  VALUES ⇒ macro; SQL TEXT depends on the args ⇒ sqlgen (§2, not built).** Flags = `internal = true`
  ONLY (`BuiltinFunctions` parity — not the `DefaultFunctionGenerator`'s `temporary` pair). Provider
  qualification (`somecat.sch.foo`) is REJECTED, not ignored (macros land in system `main`; namespace via
  the NAME prefix). Bad DDL ⇒ WARN + skip, never blocks the extension (**the load-time WARN only reaches
  `duckdb_logs` on the LOAD path** — a static binary loads before logging can be enabled; the sample plugin
  ships a deliberately malformed `plug_bad_macro` beside a good `plug_double` to pin skip-doesn't-block).
  **Two findings:** a DEFAULTED macro param also binds POSITIONALLY (`fabricator_bucket_of('alice', 16)`,
  not just `n := 16`) ⇒ overload sets are only for genuinely different arities; an overload set is ONE
  catalog entry but N rows in `duckdb_functions()`. Demos (SqlServer = the always-present default provider,
  the `fabricator_render` slot): `fabricator_rowid_parts(rid)` → `{file_ordinal, row_position}` (the
  TRANSIENT rowid splitter, docs/rowid-concepts.md), `fabricator_rowid_of(ord, pos)` / `(parts)` (overload
  set, the round-trip inverse), `fabricator_bucket_of(v, n := 8)` (named default over `bucket()`),
  `fabricator_delta_head(path, n := 100)` (TABLE macro over `fabricator_delta_scan`). Plugins ship macros
  too (`Fabricator.SamplePlugin`, zero bridge/ABI change). `test/verify_macros.test` (41) +
  `verify_plugin.test` (10); regression global_functions 63 / custom_functions 89 / hilbert 27 / bucket 34.

- **`hilbert_index` global scalar — DONE (2026-07-18, C#-only, no ABI): the liquid-clustering-style
  ordered-write primitive.** `hilbert_index(coords BIGINT[], bits) → BIGINT` (Bridge
  `HilbertIndexFunction`, declared in `CustomFunctions.GlobalScalar` — the fabricator_render slot):
  n-dim Hilbert-curve position via Skilling's transpose algorithm (the curve OSS delta-spark's
  `HilbertClustering` uses; LIST arg = per-row dimension count, sidestepping the fixed-arity global-scalar
  decl), coords CLAMPED into [0,2^bits) (layout-advisory, never correctness), n*bits<=63, n=1=identity,
  NULL→NULL. **Usage = `ORDER BY hilbert_index([...], b)` in a CTAS/COPY/dbt model → DuckDB's EXTERNAL
  (spilling) sort does the global reorder — the write pipeline stays streaming**; consecutive rows are
  neighbors in EVERY clustering key → tight per-file/row-group min-max on all keys → stats skipping for any
  predicate subset (pre-bucket with `width_bucket`). `test/verify_hilbert_index.test` (27 — full-grid
  unit-step property pins 2D+3D = the defining Hilbert property, U-order literals, clamp/NULL/errors, 100k
  ordered CTAS); global_functions 63 unregressed.
  **`bucket` global scalar + DECLARED SCALAR VOLATILITY — DONE (2026-07-19, C#+C++ lockstep, no ABI bump):
  the Iceberg/DuckLake bucket transform** (`bucket(num_buckets, value) → INTEGER`, DuckLake arg order;
  `BucketFunction`, Bridge, beside hilbert_index): Murmur3 x86-32 seed 0 over Iceberg's canonical byte
  encodings (spec Appendix B — ints/dates→LE 8-byte long, ts/time→µs, decimal→minimal BE two's-complement
  of the UNSCALED value, string→UTF-8, blob raw) then `(hash & Int32.Max) % n` — bucket values agree with
  DuckLake/Iceberg/Spark (spec known-answer vectors pinned + cross-checked vs an independent murmur3);
  NULL→NULL, float/double/bool rejected (Iceberg parity), value = the SQLNULL→ANY sentinel (runtime-type
  dispatch). **Delta has no transform partitioning → bucket partitioning = materialize the column**:
  `CREATE TABLE … PARTITIONED BY (user_bucket) AS SELECT *, bucket(8, user_name) AS user_bucket …`, prune
  with `WHERE user_bucket = bucket(8, 'alice')`. **That pruning required the general capability the user
  asked for: scalar functions now DECLARE volatility** — `IScalarFunction.IsVolatile` (default TRUE;
  override false = PURE), riding the return-schema FIELD metadata `fabricator.volatile="0"` (the variant
  metadata channel — absent=volatile, old bridges/plugins unchanged) → `FetchFunctionReturnType(out bool)`
  → `BuildFabricatorScalarFunction` registers `FunctionStability::CONSISTENT` ⇒ constant args FOLD at plan
  time (the folded literal is what reaches the scan as a partition filter; VOLATILE = never folded stays
  the default for discovered/remote UDFs). Applies to global AND catalog custom scalars; hilbert_index is
  now CONSISTENT too. `test/verify_bucket.test` (34 — vectors, distribution, guards, partitioned CTAS +
  duckdb_logs `pruned=2` pin proving fold→prune); regressions global_functions 63 / hilbert 27 / scalar 26
  / custom 89 / sorted_by 30 / clustered_optimize 138 green.
  **WRITES TO LIQUID-CLUSTERED TABLES — DONE (2026-07-18, EW-only, local commit): the `clustering` writer
  feature is allowlisted in EW `ProtocolVersions.SupportedWriterFeatures`** — previously EVERY write to a
  Databricks/Fabric-Spark `CREATE TABLE … CLUSTER BY` table failed with "unsupported writer features:
  [clustering]". The feature is advisory LAYOUT (the spec permits plain unclustered appends/DML; a later
  clustering OPTIMIZE reclusters them), and the preservation obligations were ALREADY met (the
  `delta.clustering` domainMetadata survives commits AND checkpoints — SnapshotBuilder/CheckpointWriter
  carry all domains; `add.clusteringProvider` round-trips). EW `ClusteredTableTests` (3 — synthetic
  clustered log append, domain-survives-checkpoint, provider round-trip; Table.Tests 185). **VALIDATED
  LIVE (Fabric Spark 4.1, sparkprobe `createclustered`/`verifyclustered`):** Spark `CLUSTER BY (grp,id)`
  create + OPTIMIZE (8 clustered files, reader v3/writer v7 with clustering+DV+domainMetadata) → OUR
  append (500 rows) + DV DELETE (id%100) on `lake.dbo.fabricator_clustered` → Spark reads 1485/1/1499
  exact, deleted ids invisible, **clusteringColumns intact in DESCRIBE DETAIL**, and a further Spark
  OPTIMIZE reclusters incl. our unclustered files. Remaining liquid follow-ups (not started): writing
  CLUSTERED files ourselves (hilbert_index layout in OPTIMIZE + `clusteringProvider` tagging),
  ZCube incremental recluster.
  **CLUSTERING-AWARE CREATE — DONE (2026-07-18, EW `13f7fce` local + Bridge pass-through): `SORTED BY`
  now also DECLARES the table liquid-clustered.** EW `CreateAsync`/`OpenOrCreateAsync` gained
  `clusteringColumns` (writer-v7 `clustering` + its `domainMetadata` dependency + the `delta.clustering`
  domain in commit-0, byte-shaped like Spark's captured live form
  `{"clusteringColumns":[["a"],["b"]],"domainName":"delta.clustering"}`); `DeltaWriter` passes
  `clusteringColumns: sortedBy` at all three `OpenOrCreateAsync` sites — so a SORTED BY create is both
  physically ordered AND advertises the clustering spec Spark's OPTIMIZE consumes. **SPEC FINDING (cost a
  live None.get crash): the domain stores PHYSICAL column names.** OSS Spark's `ClusteringColumnInfo.apply`
  (`ClusteringColumn.scala:97`, via `extractLogicalNames`) resolves the domain's paths against the schema's
  PHYSICAL names — under our default `column_mapping 'name'` a logical-named domain made `DESCRIBE DETAIL`
  AND `OPTIMIZE` crash with `None.get` (reads + INSERTs worked — only the clustering-info resolution
  failed; the Spark-created reference table had no mapping so logical==physical masked it). Fix in EW:
  callers supply LOGICAL names, CreateAsync resolves each through the mapping-assigned schema
  (`ColumnMapping.GetPhysicalName`; unknown column → clear DeltaFormatException). EW `ClusteredTableTests`
  now 6 (mapped-create physical-name pin + unknown-column throw; Table.Tests 188). **VALIDATED LIVE
  (sparkprobe `verifyoursorted`):** our `CREATE TABLE lake.dbo.fabricator_sorted SORTED BY (grp,id) AS …`
  (5000 rows, name-mapped, physical-name domain) → Spark `DESCRIBE DETAIL` shows
  `clusteringColumns ["grp","id"]` + tableFeatures incl. clustering, **Spark OPTIMIZE runs its CLUSTERING
  strategy** (clusteringStats + per-column clusteringQuality grp/id "ok"; no rewrite — the single file is
  already sorted), counts exact, Spark INSERT works.
  **CLUSTERED OPTIMIZE — DONE (2026-07-18, EW seam + Bridge, no ABI): OPTIMIZE on a clustering-declared
  table RECLUSTERS instead of bin-packing.** Detection (`DeltaReader.ResolveClusteringColumns`): the
  `delta.clustering` domain (authoritative, PHYSICAL names → resolved to logical via the mapped schema;
  nested paths/unknown columns → bin-pack) else `fabricator.sortedBy` (pre-domain tables). Mechanism =
  **ONE host query, data never crosses the C ABI**: `COPY (SELECT <logical→physical renames> FROM (<UNION
  ALL of per-file FileSql — DV rows excluded, mapping/schema-evolution/row-tracking handled>) ORDER BY …)
  TO <uuid>.parquet (RETURN_STATS, FIELD_IDS…)` — order = `hilbert_index` over per-key
  `ntile(2^bits) OVER (ORDER BY col)` range-buckets for 2+ keys (bits=min(15,63/k); rank-based bucketing
  is type-agnostic — strings/dates, no 63-bit truncation — and IS Spark's range_partition_id shape;
  1 key = plain ORDER BY), then ONE commit via EW `CommitDataFilesAsync(Overwrite, dataChange:false,
  clusteringProvider:"liquid", expectedVersion: <the listed version>, operation:"OPTIMIZE")` — new EW
  params: `dataChange` (removes+adds; `HonorWriterFeatures` treats dataChange=false as appendOnly-legal)
  + `clusteringProvider` stamped on adds. Row-tracking stable ids PRESERVED: when the table declares the
  materialized columns, `__delta_row_id`/`__delta_row_commit_version` are projected as data columns (the
  per-file COALESCE(materialized, baseRowId+position)) and ride the sort into the clustered file (out of
  stats; the add gets fresh baseRowId per compaction semantics). The listing runs against the OPEN
  table's own snapshot (`BuildNativeScanListAsync`, the extracted core of ListNativeScanFiles) so
  expectedVersion can't race; a concurrent commit → clean "retry" error. NO-OP when active = 1 DV-less
  file (Spark parity — no commit). Gates → bin-pack fallback: no native_write, identity/IcebergCompat,
  variant, nested columns under mapping. `test/verify_delta_clustered_optimize.test`
  (46 — 2-key hilbert order pinned via deterministic ntile recompute [unique keys] + lag monotonicity,
  DV rows not resurrected, stable id preserved, clusteringProvider+dataChange:false commit pins, no-op
  re-OPTIMIZE, VACUUM→1 file, 1-key lexicographic, sortedBy-only detection, partitioned+plain fallbacks);
  regression optimize 40/sorted_by 30/native_write 147/native_read 88/partition 54/row_tracking_virtual
  299/transactions 941/tblproperties 42/late_mat 57/dynamic_filter 21 + EW 189&168. **VALIDATED LIVE
  (OneLake `fabricator_sorted` + sparkprobe):** 2000-row unsorted append (3 files) → our OPTIMIZE → 1
  file, counts exact, the provider commit on OneLake; **Spark DESCRIBE DETAIL fine with our tagged file,
  Spark OPTIMIZE judges the hilbert file clusteringQuality "ok" (avgCoverage 100) and rewrites NOTHING**,
  Spark INSERT works.
  **MULTI-FILE clustered output — DONE (same day, C#-only):** the rewrite splits into TARGET-SIZED files,
  each a CONTIGUOUS cluster range (tight per-file min/max on all keys = the actual file-skipping payoff).
  Target = the `delta.targetFileSize` table property (Databricks; plain bytes or b/kb/mb/gb suffix) else
  128 MiB (== EW `CompactionOptions.TargetFileSize`, so clustered + bin-pack aim alike). **DuckDB's own
  COPY `FILE_SIZE_BYTES` rotation is UNUSABLE for this — a HARD incompatibility, not a tunable: the
  planner FORCE-disables order preservation for any rotated/per-thread/partitioned COPY regardless of
  `preserve_insertion_order`, and the explicit `PRESERVE_ORDER` COPY option THROWS with these parameters
  (`plan_copy_to_file.cpp:27-34`); probed: the parallel sink interleaves order across the rotating files**
  (in-file inversions + interleaved ranges + a 0-row trailing file) — so the split is OURS: single-file fast path when the estimated output fits
  (~1.25× target; the whole rewrite stays ONE zero-crossing COPY), else the sorted stream comes back over
  `HostFs.Query` and SEQUENTIAL per-file `RunCopy`s cut at BATCH boundaries (`BudgetedStream` — no
  slicing, no lifetime hazards; rows-per-file estimated from the source adds' own bytes/rows stats).
  **CRUX BUG FIXED en route (pre-existing, ALL Host.Query consumers): the host stream's `get_next` is NOT
  idempotent at EOF** — a second call after end re-`Fetch()`es the CLOSED StreamQueryResult ("Attempting
  to execute an unsuccessful or closed pending query result"; the DuckDB flavor of the ADOMD
  read-past-EOF gotcha). Latched centrally in `InterruptibleQueryStream` (`_done` — Arrow C stream end is
  sticky), protecting every consumer; my chunked loop was just the first to pull past EOF.
  `verify_delta_clustered_optimize.test` now 55 (§5 multi-file: >1 file, per-file order, NO strictly
  interleaving ranges, every add provider-tagged, counts exact). (The ZCube incremental recluster
  noted here as remaining is DONE — see the later bullet.)
  **PARTITIONED COMPACTION — SILENT CORRUPTION FOUND + FIXED AT THE EW ROOT (2026-07-18, EW `13f7fce`+
  local commit; the clustered-OPTIMIZE tests exposed it):** EW's `CompactionExecutor` compacted ALL
  candidates into ONE file at the TABLE ROOT stamped with the FIRST candidate's `partitionValues` — after
  a single OPTIMIZE on a partitioned table EVERY row read one partition's value (probed live: 400/400/400
  across three regions → 1200 rows all "US"; the reader derives partition columns from the add's
  partitionValues, so the corruption was total and silent on EVERY path — pure-EW included). The
  NULL-backfill of partition columns into the compacted file also misaligned the `IDataFileReader` seam
  (the index-out-of-bounds / SIGSEGV under native_read+native_write — the LOUD variant of the same gap).
  **Fix (EW):** candidates group BY PARTITION (`CanonicalPartitionKey`, now internal — tolerant of mixed
  logical/physical partitionValues vintages under mapping) and each ≥2-file group compacts independently
  (`CompactGroupAsync`): adds carry the group's partitionValues + land in the group's Hive dir (inherited
  encoded path prefix); the widening/backfill target schema EXCLUDES partition columns (data files never
  carry them). Unpartitioned = single group (behavior unchanged); single-file partitions untouched;
  all-DV-deleted groups left alone (conservative no-op parity). The Bridge's reopen-without-reader-seam
  guard is REMOVED (the seam now aligns). EW `CompactionTests` +2 (per-partition values/files/Hive-dirs
  pin + single-file-partition), Table.Tests 191; extension test §4 pins exact per-partition values + one
  file per partition through the once-crashing native_read+native_write path
  (`verify_delta_clustered_optimize` 68); sweep: optimize 40 / compaction_rowtracking 24 /
  materialize_rowtracking 17 / row_tracking 33 / row_tracking_virtual 299 / sorted_by 30 / native_write
  147 / native_read 88 / partition 54 / partition_overwrite 90 / transactions 941 / dv_default 58 /
  column_mapping 251 / variant 133 green.
  **PARTITIONED × CLUSTERED — DONE (2026-07-18, EW guard + Bridge): `PARTITIONED BY` + `SORTED BY`
  COMPOSE on one table, the Databricks ZORDER-on-partitioned analog.** (1) **A partitioned create
  declares NO `delta.clustering`** — liquid clustering and partitioning are mutually exclusive
  (Spark's CLUSTER BY REPLACES PARTITIONED BY; the combo put Spark's clustering-info paths in
  None.get territory — probed, we WERE writing it): EW `CreateAsync` now THROWS on
  clusteringColumns+partitionColumns (`ClusteredTableTests` 8) and `DeltaWriter` passes
  `clusteringColumns: null` when `spec.PartitionColumns` present — the partitioned SORTED BY table
  keeps only `fabricator.sortedBy`. (2) **Clustered OPTIMIZE now serves PARTITIONED tables as
  PER-PARTITION recluster**: the listing groups by canonical partition key (physical-normalized, EW
  parity), each fragmented group (≥2 files or any DV) rewrites into ordered file(s) in ITS Hive dir
  with ITS partitionValues, and — the payoff of hand-built removes — **PARTIAL recluster**: clean
  partitions' files stay ACTIVE, untouched (commit = Append + extraActions RemoveFile[dataChange:false]
  for rewritten groups only; the unpartitioned path moved to the same shape — was mode=Overwrite).
  Partitioned rewrites carry **NO clusteringProvider tag** (no liquid declaration exists for them);
  unpartitioned keeps "liquid". Per-group ntile = per-partition range buckets (correct hilbert
  locality within each partition); partition columns ride the INNER select as literals (a cluster key
  may be one) but are EXCLUDED from the written files/stats/FIELD_IDS. NOTE: SORTED BY on partitioned
  WRITES is approximate-only (the partitioned COPY is in the planner's force-unordered list — same
  plan_copy_to_file.cpp rule), so OPTIMIZE is also what RESTORES strict order per partition.
  `verify_delta_clustered_optimize` 80 (§4 rework: no-domain pin, per-partition order + exact values,
  partial-recluster commit shape "2 removes + 1 add", no provider tag).
  **ZCUBE INCREMENTAL RECLUSTER — DONE (2026-07-18, EW Tags seam + Bridge): OPTIMIZE cost tracks NEW
  data, not table size.** Clustered outputs now carry **`tags["ZCUBE_ID"]`** (one fresh uuid per group
  per run — Spark's incremental-clustering cube identity, same tag name) + **`tags["ZCUBE_ZORDER_BY"]`**
  (the JSON key list the cube was clustered by). On the next OPTIMIZE, per group: files of a **STABLE
  cube** (total size ≥ `fabricator.targetCubeSize`, default 100 GiB — Databricks' target cube size;
  parsed like targetFileSize) clustered by the CURRENT keys are NEVER rewritten; candidates = the
  UNCLUSTERED files (plain appends, pre-ZCube rewrites, **stale-key cubes** — `ZCUBE_ZORDER_BY`
  mismatch re-enters them, so changing `fabricator.sortedBy` self-heals incrementally) + at most ONE
  partial cube (the most recent by commit version; merging one per run bounds write amplification, OSS
  parity). A lone DV-less candidate skips (joins the next round's merge). Removes = the candidates'
  adds only (correlated by the add's encoded path — `NativeScanFile` gained `AddPath`/`ZCubeId`/
  `ZCubeBy`). **`OPTIMIZE <table> FULL`** (the Databricks dialect) ignores cube identities — full
  recluster; use it after changing keys on a DOMAIN-declared table (**the domain is the authoritative
  key source — changing `fabricator.sortedBy` does NOT re-key a SORTED BY-created table**; pinned).
  EW seam: `WrittenDataFile.Tags` → `CommitDataFilesAsync` stamps `add.tags` (round-trip pinned in
  `ClusteredTableTests`, 8). Incremental trade-off: cubes overlap in key ranges (point lookup ≤ N-cubes
  files) — same as Databricks. `verify_delta_clustered_optimize` 113 (§6: stable cube untouched across
  a fragmenting append cycle [2 active files = cube A + cube B], no-op when all stable, FULL → 1 file,
  property-only key-change invalidation reorders by the NEW key). **VALIDATED LIVE (sparkprobe
  `verifyzcube`, Fabric Spark 4.1):** OneLake `fabricator_partclust` (PARTITIONED BY + SORTED BY: our
  per-partition recluster 6→3 files, 2000/region exact, NO domain in commit-0) — Spark reads exact,
  DESCRIBE DETAIL shows partitionColumns [region] + empty clusteringColumns, Spark OPTIMIZE runs fine
  over our tagged files; `fabricator_sorted` (2 of our cubes: stable A untouched by the incremental run
  + merged B) — counts exact, clusteringColumns intact, and **Spark's clustering OPTIMIZE RECOGNIZED
  OUR CUBES AS ITS OWN: clusteringStats inputZCubeFiles=2/inputOtherFiles=0 (it parsed our ZCUBE_ID
  tags), filesPassedToZCubeFilter=2, per-column quality "ok", ZERO rewrites** — full incremental-
  clustering interop, both directions; Spark INSERT still works. Also pinned live: a lone DV-less
  append file correctly SKIPS (joins the next round's merge).
  **`ALTER TABLE … SET/RESET SORTED BY` — DONE (2026-07-19, C++ additive alter kinds 12/13 + EW seam +
  Bridge): the DOMAIN RE-KEY DDL (the ALTER CLUSTER BY analog).** DuckDB v1.5.4 parses
  `ALTER TABLE t SET SORTED BY (a, b)` / `RESET SORTED BY` natively (`AlterTableType::SET_SORTED_BY`;
  its own tables throw — the clause exists FOR catalog extensions; there's also SET/RESET PARTITIONED
  BY). C++ crosses both as additive `FABRICATOR_ALTER_SET_SORTED_BY=12`/`_SET_PARTITIONED_BY=13` (a1 =
  JSON array, [] = RESET; plain ASC column refs only — DESC/expressions rejected, clustering has no
  direction). Delta: ONE metadata commit updates the `fabricator.sortedBy` ordered-write property AND —
  unpartitioned — the `delta.clustering` domain via new EW **`SetClusteringColumnsAsync`** (declare/
  re-key/remove + `extraActions` fusion + **`UpgradeProtocolForWriterFeatures`** — the WRITER-ONLY twin
  of UpgradeProtocolForFeatures: clustering/domainMetadata must NOT enter readerFeatures, a legacy
  reader-1 table upgrades to writer-7 while staying reader-1). **This closes the earlier limitation:
  re-keying a SORTED BY-created (domain) table now works by DDL** — the old cubes go stale
  (ZCUBE_ZORDER_BY mismatch) and the next OPTIMIZE reclusters by the NEW key incrementally. A
  PARTITIONED table takes the property only (ordered writes; mutual exclusion); `SET PARTITIONED BY` →
  clean guidance error (repartitioning = COPY MODE 'overwrite' + PARTITION_COLUMNS); SQL Server/DAX
  reject the kinds. `_sortedByCache` invalidated. EW `ClusteredTableTests` 10 (declare/re-key/remove/
  no-op, partitioned throw, writer-only-upgrade pin: reader stays 1, legacy writer features
  enumerated); `verify_delta_clustered_optimize` 138 (§7: DDL declare on plain table + protocol upgrade
  + INSERT re-applies order, domain re-key → incremental reorder by new key, RESET removes property+
  domain [removed:true pin], unknown-column/DESC/SET PARTITIONED BY guards, partitioned property-only).
  **`SORTED_COLUMNS` COPY option — DONE (2026-07-19, C++ option-parse only, no ABI): DECLARATIVE
  clustering for the no-ATTACH COPY surface** (dbt `delta_external` etc. — the SORTED BY analog next to
  `PARTITION_COLUMNS`; deliberately NOT `ORDER_BY`, planner-intercepted). `COPY … TO '<path>/<t>'
  (FORMAT delta, SORTED_COLUMNS 'a,b')` orders every run's stream (the existing v52 `begin_bulk
  sort_columns` param — the C++ COPY passed "" before) and is **declarative, dbt-style convergent**: at
  create it persists `fabricator.sortedBy` + declares the `delta.clustering` domain (unpartitioned);
  on a run against an EXISTING table whose persisted spec DIFFERS, `DeltaCatalog.BulkInsert` RE-KEYS
  FIRST (the SetSortedBy machinery — one metadata commit, property+domain; old ZCubes go stale → next
  OPTIMIZE reclusters incrementally) then writes — so repeated runs are metadata-idempotent and a
  config change converges the table on the next run. Runs AFTER the MODE dispositions (no metadata side
  effects from 'error'/'ignore'); ABSENT option keeps the persisted spec (PARTITION_COLUMNS precedent);
  REMOVAL is DDL (`ALTER … RESET SORTED BY` — an empty option value can't cross the column-list ABI,
  `SplitColumnList` collapses it to null); changing the spec inside an explicit txn → clean error (the
  re-key is an immediate commit). Composes with `PARTITION_COLUMNS` (property-only, no domain). Also
  reaches the SQL Server provider (warehouse `CLUSTER BY` on CREATE_TABLE copies, v52 machinery — no
  re-key surface there, create-time only). `verify_delta_copy_format` 109 (create persists property+
  domain + ordered file, same-spec append = exactly one data commit, changed-spec run = SET SORTED BY
  commit + data commit with the property converged, partitioned = property-only). NOTE a create-COPY is
  TWO commits (v0 CREATE + v1 WRITE).

- **`SORTED BY` → Delta ORDERED (clustered) writes — DONE (2026-07-18, C#-only, no ABI).** The v52 native
  clause (`CREATE TABLE lake.s.t SORTED BY (a,b) [AS …]` — `sort_columns` already crossed the ABI to
  `create_table`/`begin_bulk`; Delta previously ignored it) now drives the Delta provider:
  **ONE up-front interception in `DeltaCatalog.BulkInsert`** wraps the live input stream in
  `SortStream` = `Host.Query("SELECT * FROM __fabricator_sort_input ORDER BY …", inputs)` — DuckDB's
  EXTERNAL (disk-spilling) sort does the global reorder, the bridge stays streaming, and EVERY downstream
  path (native COPY / codec collect / buffered CTAS / eager writes) consumes the already-sorted stream
  (crucially the sort runs on OUR side of the ABI, downstream of any DuckDB source parallelism). The spec
  PERSISTS as the **`fabricator.sortedBy` table property** (JSON array; threaded like the
  `delta.isolationLevel` create stamp through `DeltaWriter.Create/Write/TryWriteStreaming` → `CreateConfig`;
  buffered creates park `CreateSortColumns` on the txn buffer for the flush) — so **plain INSERTs re-apply
  the ORDER BY** (`SortedByFromConfig`: read once per catalog instance via `DeltaReader.GetTableConfig`,
  cached in `_sortedByCache`, invalidated on set_tblproperties/DROP/RENAME; persisted columns missing from
  a write's schema are tolerantly skipped). Change/disable per table via
  `fabricator_delta_set_tblproperties('lake','main.t','{"fabricator.sortedBy":null}')`. SORTED BY =
  LEXICOGRAPHIC order (knowledge-free — works on a first load); for multi-key clustering put
  `hilbert_index(...)` in an explicit ORDER BY (bucketing needs distribution knowledge — see the
  hilbert_index bullet). `test/verify_delta_sorted_by.test` (30 — native + codec CTAS file order via
  per-file rowid lag checks, append re-applies, UNSET disables, empty CREATE + first INSERT, buffered-txn
  CTAS, re-attach durability); native_write 147 / write 31 / tblproperties 42 / transactions 941
  unregressed. NOTE the SQL-Server-warehouse `SORTED BY → WITH (CLUSTER BY …)` mapping (v52) is untouched —
  the same clause now means the analogous thing on both providers.

- **dbt DAX→Delta pipeline — DONE + VALIDATED LIVE (2026-07-16): `dbt_dax_test/` (gitignored — NO creds in
  the project).** A second dbt-duckdb harness proving DAX EVALUATE results materialized as OneLake Delta
  tables: the `plugins/fabric_attach.py` plugin executes the repo-root `dax_secret.sql` per connection
  (secrets never enter the project) then ATTACHes BOTH catalogs — `lake` (LH lakehouse, `PROVIDER 'delta'`,
  `READ_ONLY false`) + `dax` (`PROVIDER 'dax'`, workspace XMLA `powerbi://…/Test;Initial Catalog=Test
  Warehouse Model2`). TWO model shapes, both PASS: (1) plain SQL wrapping the DAX —
  `SELECT … FROM dax."Model".daxeval(expression := 'EVALUATE …')`, ordinary `table` materialization CTASes
  into `lake.dbt.*` (NOT `fabricator_exec` — daxeval is the result-returning surface); (2) **PLAIN DAX as
  the whole model body** via the custom **`dax_table` materialization** (`macros/dax_table.sql`): dbt never
  parses model SQL — it renders jinja and hands the text to the materialization, which wraps `compiled_code`
  in `daxeval(expression := '<body, quotes doubled>')` + `CREATE OR REPLACE TABLE {{ this }} AS …`
  (config `dax_catalog`/`dax_model`; jinja caveat: literal `{{`/`{%` in DAX needs `{% raw %}` — single
  braces/table constructors fine). The FULL LOOP is validated: the user-created **`LH_semtest`** semantic
  model over the LH lakehouse (table `arrownet_bigdv`) → plain-DAX model `EVALUATE TOPN(100,
  'arrownet_bigdv')` → `lake.dbt.bigdv_top100` back on the SAME lakehouse (100 rows exact; the plugin
  attaches a LIST of models — `daxlh` = LH_semtest, `dax` = Test Warehouse Model2). GOTCHAS: the
  **LH lakehouse's DEFAULT semantic model is EMPTY** → daxeval errors ("needs at least one table") — use a
  user-created model (LH_semtest) or a populated one; a
  workspace XMLA endpoint WITHOUT `Initial Catalog` auto-binds the FIRST model with the schema named by
  GUID (give `Initial Catalog` → schema binds by name, e.g. `Model`); a `+schema:` in dbt_project.yml
  CONCATENATES with the profile schema (dbt default `generate_schema_name` → `dbt_dbt`) — set the schema in
  the PROFILE only. Run: `cd dbt_dax_test && ../dbt_mssql_test/.venv/Scripts/dbt.exe run` (~50s incl.
  introspection; models 4-6s each).

- **Eager-write DeltaTxnBuffer (PLANNED, analysis 2026-07-13 — nothing built).** Goal: the buffer holds
  Delta ACTIONS only; data files are ALWAYS written eagerly to storage at statement time (Spark's
  OptimisticTransaction shape — files land immediately, commit deferred, rollback = invisible orphans for
  VACUUM). Kills the three RAM-bound shapes (CTAS-in-txn, insert-under-pending-ALTER, UPDATE post-images)
  and unifies the read overlays. Slices, each independently shippable
  (`verify_delta_catalog_transactions` is the gate):
  - **A — DONE (2026-07-13, C#-only, no ABI):** (1) eager UPDATE post-images — on a `native_write`
    catalog `BufferUpdateRows` writes them as a parquet file at statement time
    (`table.WriteDataFilesAsync(postImages, schemaOverride: pending.PendingDeltaSchema)` → parks
    `WrittenDataFile`s into `pending.Files` instead of batches; NOT NULL validated at statement time
    since the flush only validates Batches; the existing pending-FILES ScanNative routing +
    `DeltaNativeReader(pendingFiles/pendingDeletes/pendingSchema)` serve read-your-writes; ROLLBACK
    orphans the file). (2) `TryWriteStreaming` gained a `pendingSchema` param (deferred-append-only,
    guarded) driving the NOT NULL wrap + mapping rename/FIELD_IDS/stats keying — so
    insert-under-pending-ALTER STREAMS (the `BulkInsert` gate no longer excludes `PendingMetadata`;
    a streaming fallback (identity/iceberg) under a pending ALTER still collects — the data must not
    commit before the schema). Codec catalogs keep in-memory batches (slice C).
    `verify_delta_catalog_transactions.test` §30 (629, +33 write-shape pins: post-image file on storage
    before COMMIT with the log unmoved, streamed 1000-row ALTER'd insert mid-txn, commit adds no data
    file, rollback leaves the orphan); full delta sweep green (native_write 147 / native_read 66 /
    update 63 / alter 116 / column_mapping 251 / constraints 50 / dv_default 58 / write 31 / variant 133
    / partition 54 / nested_alter 100).
  - **B — DONE (2026-07-13, C#-only + one EW param):** CTAS-in-transaction STREAMS on `native_write`
    catalogs. `DeltaWriter.TryStreamCreateFiles` writes the CTAS data to a parquet file in the (not yet
    existing) table folder via the native COPY — **no `_delta_log` is touched** — and parks
    `WrittenDataFile`s + the **pre-assigned column-mapping schema** (`ColumnMapping.AssignColumnMapping`
    runs at BUFFER time — physical names are RANDOM GUIDs, `GeneratePhysicalName` = `col-{Guid}`, so the
    correction to the plan: assignment is NOT deterministic and must happen once, before the files). The
    EW seam is `CreateAsync`/`OpenOrCreateAsync(preAssignedSchema:)` — the create adopts the parked
    schema instead of re-assigning (maxId via `GetMaxColumnId`). Flush = v0 CREATE (pre-assigned) + ONE
    `CommitDataFilesAsync` fusing the streamed files with any later collected batches;
    `ScanPendingCreated` reads the pending files back via host `read_parquet` +
    `ArrowColumnMappingRename.Wrap(toPhysical:false)` + the new `DeltaTxnBuffer.ProjectStream`
    (transient-source projector, ownership moves — unlike `ProjectPending`'s clone-and-keep). Rollback
    leaves orphan parquet in a `_delta_log`-less folder (not a table to any reader; same-name re-create
    works — pinned). Partitioned pending creates keep the collect path (read-back would need Hive-dir
    reconstruction). `verify_delta_catalog_transactions.test` §31 (653, +24 pins: file on storage with
    ZERO log entries mid-txn, read-your-writes incl. WHERE + later-INSERT overlay, v0+v1 commit shape,
    the all-NULL-on-physical-name-mismatch cross-check, rollback orphan + re-create);
    **delta-kernel reads the eager-CTAS commit exactly** (delta_scan probe); sweep green (native_write
    147 / write 31 / column_mapping 251 / update 63 / partition 54 / native_read 66 / txn_version 51 /
    copy_format 96).
  - **C1 — DONE (2026-07-13, C#-only): codec catalogs go eager-per-statement.** In EXPLICIT transactions
    (autocommit keeps the byte-identical park-batches shape) a statement's collected batches become a
    data file at statement end via the shared `TryEagerWriteBatches` (`WriteDataFilesAsync` — EW codec
    writer, or DuckDB's under native_write; write-tuning spec passed; NOT NULL validated at statement
    time) — codec-transaction memory caps at ONE statement. The A1 UPDATE post-image eager write now
    uses the same helper (codec included). Mid-txn reads route through the existing
    Files>0→`ScanNative` path — now exercised on pure-codec catalogs (read_parquet reads codec files,
    already broadly validated by the native_read suite). Fallbacks that still park batches:
    identity/iceberg (no external commit), `materialize_row_tracking` (codec appends must materialize
    `__delta_row_id`, only EW's committing writer does), pending-created tables (later INSERTs).
    §32 (+28 pins) — and the pre-existing codec-txn sections §1–9 (400+ assertions) pass unchanged on
    the new path.
  - **C2 — DONE (2026-07-13): CDF in explicit transactions** (was rejected — a real user-facing hole).
    Every buffered statement on an (unpartitioned) CDF table writes its `_change_data` file at
    STATEMENT time and parks the `CdcFile` action (`DeltaTxnBuffer.PendingCdc`), fused into the ONE
    commit at flush — **inserts included**: a commit carrying ANY cdc action is read cdc-ONLY by the
    CDF reader (inference disabled), so appends fused with DML would otherwise vanish from the feed.
    Mechanics: appends → insert-cdc from the statement's batches (CDF appends skip the streamed path —
    the rows must be in hand; probed once per (txn, table) via `TxnDmlProfile` + cached
    `CdfEnabled`, which also pins the base version so the CDF flush always has a rebase base);
    DELETE → one extra `ReadRowsByRowIds` read-back for the deleted rows' content (the position set has
    no values); UPDATE → pre-images (read-back, committed values) + post-images, the autocommit
    merge-on-read pair. EW seam: public `DeltaTable.WriteChangeDataFileAsync` (wraps the internal
    `CdfWriter.WriteAsync`; cdc actions carry `DataChange=false` so concurrent readers' dataChange
    checks ignore them; rebase-safe — delete-delete/deleteRead guarantee our touched files unchanged,
    so captured CDC content stays exact). Still guarded (clean errors): PARTITIONED CDF tables
    (cdc partitionValues/column re-add semantics deferred — their buffered appends stay cdc-less +
    inference-correct since their DML is rejected, commits never mix) and DML-after-buffered-ALTER on
    CDF (cdc files would be written against the pre-ALTER shape). §7 rewritten (positive: cdc file on
    storage pre-COMMIT, log unmoved, rollback orphans it) + §33 (fused-commit feed EXACT per type:
    2 inserts + delete + update pre/post with correct values; pure-append txn feed; autocommit
    inference intact) — `verify_delta_catalog_transactions` now 732; changes 73 / dv_default 58 / dv 48
    / update 63 / delete 28 / native_write 147 / write 31 / partition_overwrite 90 / txn_version 51 /
    constraints 50 green.
  - **Eager-write EDGE LIFTS — DONE (2026-07-13, follow-up pass):** (a) **materialize_row_tracking
    appends eager-write** (the C1 gate was over-conservative — a FRESH append needs no physical
    `__delta_row_id`; readers derive `baseRowId + position`, the validated streamed-native shape the
    flush's own `WriteDataFilesAsync` batch path already produced). (b) **IDENTITY appends eager-write
    with abort-on-concurrent-consumption**: values GENERATED at statement time from the pinned/chained
    HWM (`DeltaTxnBuffer.PendingIdentityHwm` chains across statements; read-your-writes now shows REAL
    ids — the batch park showed NULLs), files written via
    `WriteDataFilesAsync(identityValuesPreGenerated:)` and committed via
    `CommitDataFilesAsync(identityValuesPreGenerated:)` (both gates gained the bypass; Iceberg still
    rejected); the flush composes the HWM into the (single) metaData action
    (`BuildIdentityMetadataAction(marks, pending.PendingMetadata)` — never two metaData actions). EW
    seams: `DeltaTable.GenerateIdentityValues(batches, chainedHwm)` (wraps `IdentityColumnWriter.
    ProcessBatch` with seeded configs) + `BuildIdentityMetadataAction` + `HasIdentityColumns`/
    `IsIcebergCompat`. Concurrency = Spark's policy for FREE: a concurrent identity-consuming commit
    necessarily carries metaData → the rebase metadataChangedCheck aborts us (vs autocommit's
    regenerate-retry — a deliberate liveness trade on the rare same-table-concurrent-insert);
    non-consuming concurrent commits rebase fine. Identity×CDF in a txn is REJECTED (cdc capture would
    precede value generation → NULL ids in the feed; the CDF probe also excludes identity tables so
    their pure appends stay inference-correct). Kernel-validated (fused identity commit → 5 distinct
    ids 1..5 via delta_scan). (c) **materialize UPDATE post-images bake the ORIGINAL stable ids**:
    lifted the buffered-UPDATE materialize rejection (now partitioned-only); stable id =
    `baseRowId[ordinal] + position` against the ordinal-ordered active set (EW's own merge-on-read
    rule — line-checked: it does NOT read source materialized columns either), via new EW
    `OrderedActiveBaseRowIds()` + `WriteDataFilesAsync(materializedRowIds:)` (flat/aligned,
    unpartitioned-only like merge-on-read; appended after ToPhysical). An eager-write failure on a
    materialize UPDATE is a HARD error (the batch park can't bake ids — never silently degrade).
    §34+§35 (781 total): materialize txn-append file-on-storage; identity chained ids across
    statements + distinct-after-commit + HWM continuation + rollback-regeneration; the post-image
    parquet carries `__delta_row_id` with the ORIGINAL id (=1 for row 2). **The pre-existing
    read-back race is FIXED (same pass)**: `ReadRowsByRowIdsAsync` + `OrderedActiveBaseRowIdsAsync`
    gained `atVersion` (resolve the snapshot the rowids were SCANNED against — ordinals are path-sort
    positions in THAT active set), and all three consumers (buffered UPDATE read-back, CDF-DELETE
    read-back, materialize base-id resolution) pass `pending.PinnedVersion` — a concurrent commuting
    append between the transaction's first scan and its DML statement can no longer shift the ordinal
    resolution. §36 pins it with a two-connection racer (three appends land between pin and UPDATE;
    the correct pre-image is captured deterministically — previously luck-of-the-uuid-sort);
    `verify_delta_catalog_transactions` now 805.
  - **C3 same-txn-DML lift — DONE (2026-07-13): DELETE of rows inserted in the SAME transaction.**
    The eager-write architecture made it cheap: all DML-eligible tables park FILES now, so a
    same-transaction delete = an inline deletion vector BORN ON our own pending add. Routing: rowid
    ordinals ≥ `PendingFileOrdinalBase` (0x780000, = index into `pending.Files`) key straight into
    `DeletedByOrdinal` — the native reader's pending-file exclusion (`WithPendingDeletes` matches by
    ordinal, and pending files carry 0x780000+idx) serves read-your-writes with ZERO new read code; the
    flush splits high keys off to the new EW
    `CommitDataFilesAsync(deletedPositionsByFileIndex:)` (builds the inline DV via
    `DeletionVectorWriter.CreateAsync` + marks the add's stats `tightBounds:false` via the existing
    `StatsWithLooseBounds`) while committed-ordinal keys keep the pinned-snapshot DV-pair path. Mixed
    committed+pending deletes in one statement work. Still guarded (clean errors): UPDATE of same-txn
    rows; same-txn DELETE on CDF tables (the insert-cdc file already captured the row); in-memory
    BATCH rows (0x700000..0x77ffff — the identity-under-ALTER/iceberg fallbacks, practically
    unreachable). Buffered DML's DvEnabled requirement covers the add-DV's reader-v3 need. §37 (+ the
    old "rejected (later slice)" pin converted positive): codec + native_write catalogs, partial +
    mixed deletes, rollback discards both halves, guards — `verify_delta_catalog_transactions` now
    861; **delta-kernel reads the born-with-DV add exactly** (9 rows, deleted ids absent).
  - **C3 virtual-table refactor — DONE (2026-07-13, behavior-identical):** the codec read paths
    collapsed into ONE **`ScanCodec`** — the codec-path virtual-table read (pinned base stream ⊕
    pending-delete exclusion [rowid stream forced when needed] ⊕ pending-ALTER schema/reconcile ⊕
    pending-batch overlay; every step conditional, so no-pending scans pass straight through).
    `ScanCodecWithPendingDml` deleted (it was ~80% a copy of ScanTable's tail); shared
    `SchemaWithRowId` + `TranslateProjectionToCommitted` helpers; net −28 lines. **The EW-synthetic-
    Snapshot form ("pinned ⊕ pending actions" as a real `Snapshot`) was evaluated and REJECTED on an
    architectural finding:** EW's `OrderedActiveFiles` path-sorts the WHOLE active set, so uuid-named
    pending files would interleave into the committed ordinal range and break the transient-rowid
    contract every layer shares (committed ordinals < 0x700000, pending files at 0x780000+idx — scans,
    position parking, the flush's DV split, the same-txn-DML routing). The overlay composition IS the
    virtual table, with the ordinal spaces disjoint by construction (recorded on ScanCodec's doc
    comment). Gate: transactions 861 unchanged + the FULL delta sweep (36 suites) green.
  - **D — DONE (2026-07-13): S3 MULTI-WRITER COMMITS via our own conditional PUT.** httpfs never passes
    `If-None-Match`, so plain-ATTACH S3 stays documented single-writer; **ATTACH with an s3 `SECRET`**
    (`ATTACH 's3://…' (…, PROVIDER 'delta', SECRET minio_s3, READ_ONLY false)`) routes the COMMIT rename
    through a REAL put-if-absent: new Bridge `S3CommitFileSystem` (S3CommitFileSystem.cs) — the HYBRID
    FS: all data IO delegates to `DuckDbTableFileSystem` (opener secrets, host transport/caching), but
    `RenameAsync` = SDK **GetObject(temp) → PutObject(target, If-None-Match:"*") → Delete(temp)** (412 →
    false → `DeltaConflictException` → the OCC/rebase machinery). **CRITICAL PROBE FINDING: a
    conditional CopyObject is SILENTLY UNGUARDED on MinIO** (the copy succeeds over an existing target —
    AWS's documented conditional writes are PutObject/CompleteMultipartUpload only), so EW's
    `S3TableFileSystem.RenameAsync` copy-based primitive gave NO commit safety — **fixed in EW** to the
    same Get→conditional-Put→Delete shape. Wiring mirrors OneLake: `DeltaBackend.BuildConnectionString`
    s3-secret branch → `S3CommitCredential.AppendMarker` (`;FabricatorS3Cred=`) → catalog `_s3Credential`
    → `Opener()` publishes `AmbientS3Credential` → `TableFileSystems.Create` wraps s3:// roots.
    Credential mapping: key_id/secret/session_token/endpoint/region; `URL_STYLE 'path'` →
    `ForcePathStyle`; `USE_SSL`; a CUSTOM endpoint tolerates self-signed certs (the rig posture — AWS
    default endpoint keeps full validation); empty key_id → the SDK default chain. **Second finding:
    `S3CommitFileSystem.ReadAllBytesAsync` goes through the SDK too** — httpfs pins the etag recorded at
    open (and re-serves it from its caches), so a concurrent writer's IN-PLACE `_last_checkpoint`
    overwrite failed host reads with "ETag … has changed" EVEN ACROSS REOPENS; a plain GetObject always
    returns a consistent copy (small files only — data files stay host-path: immutable + cache-friendly;
    a bounded etag-retry also went into `DuckDbTableFileSystem.ReadAllBytesAsync` for the secretless
    path). **VALIDATED LIVE on MinIO:** 4 racing processes × 10 commits × 20 rows → **40/40 commits,
    800/800 rows, zero errors, across 4 checkpoint boundaries** (before the SDK-reads fix: loud
    checkpoint-read failures, NO silent loss — the guard held); a WRONG-key marker secret fails the
    commit with the SDK signature error while data IO succeeds (proving the SDK is in the loop);
    `verify_delta_catalog_s3.test` §9 (144 — SECRET-route lifecycle incl. fused txn; secretless
    sections unchanged). Outcome: S3 catalogs with a SECRET are safe multi-process/multi-engine.
    **The deltars provider could get the same for free via storage_options** (delta-rs enables native S3
    conditional-put locking with `conditional_put: "etag"`, no DynamoDB `AWS_S3_LOCKING_PROVIDER` needed —
    one line on `DeltaRsCatalog`'s `storage_options` when/if S3 discovery lands). **DECISION (2026-07-15): NOT
    NEEDED / won't prioritize.** engineeredwooddelta ALREADY ships S3 multi-writer (codec + native_write, via
    the hand-rolled `S3CommitFileSystem` conditional PUT, validated live on MinIO), so the etag option would
    only bring the OPT-IN deltars provider to parity on a capability we already have — no concurrency gap to
    close. deltars stays valuable for OTHER reasons (reference reader/writer, DataFusion pushdown, the
    maintenance ops EW lacks: OPTIMIZE/Z-ORDER/VACUUM/CHECKPOINT/MERGE); if S3 *discovery* is ever wired for
    those, adding the one storage-option line is trivial then. Do NOT re-propose it as an S3-concurrency item.
  - **Partitioned × native_read BUG FIXED + partitioned pending-CTAS lifted (2026-07-13).** Probing the
    "why keep partitioned pending-creates buffered" question exposed a REAL committed-read bug: under
    `native_read`, a PARTITIONED table's partition column read **all-NULL in BOTH mapping modes** — the
    per-file presence probe saw it absent from the parquet footer and NULL-backfilled it like schema
    evolution (hive auto-detection never engaged; the combination was untested). Fix is
    log-authoritative: `NativeScanFile` carries each add's `partitionValues` +
    `NativeScanList.PartitionColumns`, and `FileSql` renders partition columns as **typed literals**
    (`CAST('US' AS VARCHAR) AS "region"`; dual logical|physical key lookup — new mapped commits key
    physical, old EW commits logical; missing/sentinel ⇒ NULL) — never path parsing (partitionValues is
    the spec's authoritative source; paths are opaque). WHERE/pruning on the partition column work
    (outer WHERE sees the literal; DeltaFilePruner already pruned by partitionValues).
    `verify_delta_catalog_native_read` 66→88 (both modes, GROUP BY/filter pins). **The same literal
    machinery lifted the partitioned pending-CTAS collect gate**: `TryStreamCreateFiles` gained the
    partitioned branch (`RunCopyPartitioned` — Hive layout streamed in one COPY, physical dirs +
    FIELD_IDS-minus-partition-cols under mapping, per-file partitionValues on the `WrittenDataFile`s),
    and `ScanPendingCreated`'s read-back renders per-file partition literals (physical→logical via the
    pre-assigned schema; `TypeText`/`LookupPartitionValue` widened internal for reuse). §39 pins the
    streamed partitioned CTAS-in-txn (Hive dirs pre-COMMIT + zero log entries, read-your-writes incl.
    GROUP BY/filter on the partition column, v0+v1 commit); kernel reads the fused commit;
    `verify_delta_catalog_transactions` 912.
  - **Buffered IDENTITY create — LIFTED (2026-07-13, same pass):** `CREATE TABLE t (id BIGINT AS (0), …)`
    inside `BEGIN..COMMIT` is now TRANSACTIONAL (was immediate — a ROLLBACK couldn't undo it). Simpler
    than the committed-table identity case because a never-committed table has NO concurrent-HWM problem:
    the identity metadata attaches BEFORE the buffer gate (shared `WithIdentityMetadata`); INSERTs into
    the pending table generate values at STATEMENT time from the parked schema via the new EW **static**
    `DeltaTable.GenerateIdentityValuesForSchema(schema, batches, chained)` (the instance method now
    delegates; marks chain via `PendingIdentityHwm` — read-your-writes shows REAL ids, previously NULLs
    would have shown); the flush **bakes the final marks into commit-0's schema metadata**
    (`BakeIdentityMarks` — no separate metaData action) and writes the batches with
    `identityValuesPreGenerated: true` (regeneration would double-consume the mark). §40 (create leaves
    zero log entries pre-COMMIT, chained distinct ids, HWM continues post-commit in autocommit,
    ROLLBACK leaves nothing); kernel-exact (ids 1..3 buffered + 4 autocommit);
    `verify_delta_catalog_transactions` 934, identity 38 unchanged. Remaining batch-park cases:
    iceberg, identity-under-pending-ALTER, autocommit codec (by design).
  - **dbt REGRESSION found + FIXED by re-running the lakehouse harness (2026-07-13):** dbt's table
    materialization swaps via `CREATE <model>__dbt_tmp AS …; ALTER <model> RENAME TO <model>__dbt_backup;
    ALTER <model>__dbt_tmp RENAME TO <model>; COMMIT` — the second RENAME hits a table CREATED in the
    same transaction, which the buffered-CREATE work rejected ("uncommitted buffered changes"), failing
    EVERY dbt table model on Delta targets (and the already-executed immediate old→backup folder rename
    is NOT undone by the rollback — the pre-existing non-transactional-RENAME hole made it worse). Fix:
    **RENAME TABLE of a PENDING-CREATED table** re-keys the buffer (`DeltaTxnBuffer.RenameTable`) and
    moves any eagerly-streamed files to the new folder (OneLake DFS rename / `HostFs.MoveDir` / per-file
    copy+delete fallback for S3); the flush then creates the table at its FINAL path. §38 pins the dbt
    swap on codec + native_write catalogs (`verify_delta_catalog_transactions` 888).
    **dbt harness status (all re-validated):** `lakehouse` (OneLake) `dbt run --threads 4` PASS=4/4
    (~77s); NEW **`minio` target** (S3 Delta catalog; profile s3 secret + the onelake_attach plugin
    gained `curl_insecure` for the rig's self-signed TLS; the ATTACH `SECRET minio_s3` also engages the
    slice-D conditional commits) PASS=4/4 (~9s). NEW **`delta_external` materialization**
    (`macros/delta_external.sql`): dbt-duckdb's built-in `external` whitelists csv/parquet/json, so a
    custom materialization runs `COPY (model) TO '<location>' (FORMAT delta, MODE …)` (any location —
    s3://, onelake://, local; no ATTACH) + registers a view over `fabricator_delta_scan(location)` for
    downstream refs (model config: `database=target.database` so the view lands in the writable local
    db); demo `models/ext_delta.sql` aggregates a Delta-catalog model → standalone Delta table on
    MinIO, read-back verified. NEW **dbt SNAPSHOTS work on Delta catalogs** (`snapshots/customers_snap`,
    check strategy): the SCD-2 merge = staging CTAS (a buffered pending-create the post-snapshot DROP
    cancels) + UPDATE (close old versions) + INSERT (new versions) in ONE dbt transaction — the buffered
    DML machinery end-to-end. Crux: dbt-duckdb's `make_temp_relation` NULLS database/schema → the
    staging lands in the LOCAL db and DuckDB's one-write-database-per-transaction rule kills the merge
    ("a single transaction can only write to a single attached database") — project macro
    `build_snapshot_staging_table` override stages IN THE TARGET'S database
    (`macros/snapshot_staging.sql`). SCD-2 validated on MinIO (changed row: old version closed by the
    UPDATE + new current INSERTed; new key inserted) AND live OneLake (bronze→silver, two versions).
  - **Stays immediate:** identity creates (value generation + HWM update are coupled inside EW's
    committing writer — `WriteDataFilesAsync` rejects identity tables by design), DROP/OPTIMIZE/VACUUM
    (physical/administrative — no rollback semantics possible). CREATE-OR-REPLACE + partition-overwrite
    are REPRESENTABLE as actions later (snapshot-tied removes: delete-delete guards them; needs
    whole-table-read recording, and note Spark's WriteSerializable permits the concurrent-blind-append
    reorder past an overwrite) — keep guarded until after slice C.

- **Fabric-notebook AMBIENT AUTH — DONE + VALIDATED LIVE (2026-07-14, C#-only, no ABI).** In a Fabric
  notebook/Spark session ALL THREE providers work with ZERO credentials: **SQL**
  (`ATTACH 'Server=<endpoint>;Database=<wh>'` bare, or `authentication 'default'`) against Warehouse AND
  Lakehouse SQL endpoints; **Delta-on-OneLake** (`ATTACH 'abfss://…/Tables' (TYPE fabricator, PROVIDER
  'delta', READ_ONLY false)` — no SECRET); **DAX** (`ATTACH 'Data Source=powerbi://…;Initial
  Catalog=<model>' (PROVIDER 'dax')` — **AdomdClient PROVEN on Fabric Linux compute** (the old
  "Linux-TBD" is resolved; sempy uses Adomd.NET on the same image); at a WORKSPACE XMLA endpoint the
  model SCHEMA binds by the semantic-model GUID (not displayName), and an EMPTY default model errors
  "DAX Evaluate queries work only on databases which have at least one table" — iterate models).
  Mechanism = **`FabricNotebookCredential`** (Bridge): DefaultAzureCredential is SOURCELESS on Fabric
  compute (no IMDS, no AZURE_* env — every chain link fails, proven live), so NO SqlClient
  `Authentication=Active Directory *` keyword can work there; the ambient identity is brokered by the
  Fabric TOKEN SERVICE (the same local service `notebookutils.credentials.getToken` fronts):
  `GET {AZURE_FABRIC_TOKEN_SERVICE_URL}?resource=<audience>` with `x-ms-partner-token` = the spark-conf
  `trident.session.token` (env `MSNOTEBOOKUTILS_TRIDENT_SESSION_TOKEN` — **NOT**
  `/opt/token-service/tokenservice.config.json`'s sessionToken, a DIFFERENT cluster-level token that
  401s `SignedPayloadValidationException`; found by request ablation on the live runtime) + MANDATORY
  `x-ms-proxy-host` (host of `trident.lakehouse.tokenservice.endpoint`, 400 without) + cluster id
  (`AZURE_FABRIC_CLUSTER_IDENTIFIER`) + tenant (tid claim of the session token); response body = the
  raw AAD token; cached per resource, re-minted 5 min pre-expiry ⇒ **REFRESHING, PER-SCOPE tokens**
  (fabric-REST + storage + SQL + powerbi audiences — the multi-audience need one static token can't
  cover). Wiring: `FabricCredentialResolver.AmbientChain()` (FabricNotebookCredential when the Fabric
  env markers exist, else DefaultAzureCredential) replaced every direct DefaultAzureCredential fallback
  (resolver Build + ResolveForRemoteTarget, FabricLakehouse ×3, OneLakeForwardFs); **SQL ambient
  interception** in `SqlServerCatalog`: a NO-credential connstr (bare / AD-Default; never when any
  password/token/integrated/other-AD auth is present) on Fabric compute switches to
  `SqlConnection.AccessTokenCallback` minting database.windows.net tokens (SqlClient re-acquires per
  connection open); DAX refresh = the existing `AdomdConnection.OnAccessTokenExpired` → fresh mint.
  UNDOCUMENTED internal protocol — engaged ONLY when the env markers exist (off-Fabric byte-identical,
  local suites green); re-capture harness = scratchpad/fabricnb's token-service ablation step. **Drift
  risk is LOW: sempy's own leaf (`fabric.analytics.environment.notebook_plugin.token_service_client.
  TokenServiceClient`, source-verified 2026-07-14) uses the IDENTICAL request** — same URL composition,
  same `trident.session.token` partner token (config file read ONLY for tokenServiceEndpoint+clusterName),
  same proxy-host, JWT-exp expiry; the contract is load-bearing for sempy/SynapseML/notebookutils (their
  resource shortforms `pbi`/`storage`/`sql`/`keyvault`/`kusto`/`ml` also work beside full audience URLs;
  they send moniker=clusterName, we send the artifact id — both accepted). sempy also ships the SAME
  managed AdomdClient via pythonnet with the SAME AccessToken+OnAccessTokenExpired wiring — independent
  confirmation of our DAX stack on Fabric Linux.
  **Also: duckdb AZURE `PROVIDER access_token` secrets are now consumed** (the common Fabric-notebook
  blog pattern): the SQL provider maps the field onto `SqlConnection.AccessToken` (the token must be
  database.windows.net-audience — a storage-scoped one 18456s; validated live) and
  `FabricCredentialResolver.Resolve` serves it as a static credential (expiry from the JWT exp; NO
  refresh — re-create the secret). **Pinned gap:** an abfss OneLake ATTACH with a STATIC storage-token
  azure secret fails at the Fabric REST hop (401 InvalidToken — a single-token secret can't serve the
  fabric + storage audiences) — in notebooks use the ambient no-secret form instead. Identity context:
  interactive = the user; pipeline / on-demand job = the submitting SP / workspace identity (probe
  showed `idtyp: app`). Notebook probe: `scratchpad/fabricnb` (pbi-vs-database token audiences —
  the SQL endpoint REJECTS powerbi/api-audience tokens with 18456; bare/default ambient SQL; secretless
  delta abfss read; DAX daxeval → 2; azure-secret forms; `notebookutils` token-passing forms, now
  superseded by ambient). Local-catalog gotcha re-proven: `SHOW ALL TABLES`/`duckdb_tables()` scan EVERY
  attached catalog (the populated-LH fuse hang) — use catalog-qualified/targeted queries in notebooks.

- **Sync-over-async cleanup (Bridge) — ENABLER DONE (`0533eb7`), refactor DEFERRED.** The Bridge blocks the
  C++↔C# boundary with `.GetAwaiter().GetResult()` sprinkled at every `await` site. This is **correct + safe**
  here (the hostfxr CLR has NO `SynchronizationContext`, so sync-over-async can't deadlock; the ABI is
  synchronous), just ugly + slightly worse for exception unwrapping — so there's **no urgency**. The clean shape
  = a sync ABI-facing method that blocks ONCE on a private `async` core (`ConfigureAwait(false)` throughout).
  **The landmine** is the ambients (`AmbientOpener`/`AmbientTransaction`/`AmbientOneLakeCredential`): they were
  `[ThreadStatic]`, so a real-async core with `ConfigureAwait(false)` would hop pool threads and LOSE the
  opener/credential/txn mid-op (silent — passes local tests where opener=0 works, breaks on OneLake + explicit
  txns). **Fixed as the prerequisite: converted to `AsyncLocal<T>` (`0533eb7`)** — flows across await/pool-thread
  hops, behavior-identical for the current sync code (validated: full local delta catalog suite + a live OneLake
  CTAS/DELETE/readback/DROP, so the opener+credential still cross the `set_active_opener`→use ABI-call boundary).
  So the refactor is now UNBLOCKED. **When: later + incremental** (leaf-first — `DeltaReader` read/write have a
  clean single-blocking-point shape — one seam at a time, `verify_delta_catalog_*` after each; never interleaved
  with feature work), and adopt the sync-wrapper→async-core shape as a **convention for NEW code now**.
  `IAsyncDisposable` is lower value (the C++ boundary disposes synchronously → keep a sync `Dispose()` that blocks
  once on `DisposeAsync()`; use `await using` only INSIDE the async cores). Logging note: keep it OUT of the hot
  ambient accessors + per-row/scan paths (log-spam + file-sink serialization risk); the write/DML path logging
  (`3d60cb5`) + DDL logging (`0533eb7`) sit at low-frequency decision points only.

- **Discovered TVF/proc wrapper extraction — DONE** (`SqlServerProcedure.cs` `6da8033`,
  `SqlServerTableValuedFunction.cs` `eb6c34e`). The inline TVF/proc SQL moved out of `SqlServerCatalog`
  into top-level `internal` wrappers (like `SqlServerScalarFunction`/`SqlServerTvfEach`), so
  `ExecuteTable` + `GetFunctionOutputSchema` are now **thin custom/TVF/proc dispatchers**. **Two
  asymmetric shapes** (a real finding, not laziness): `SqlServerProcedure : ITableFunction` (proc
  EXEC has no pushdown → full batches match the bind-time `OutputSchema`, so the `IAsyncEnumerable` +
  `AsyncEnumerableArrowStream` shape is correct); `SqlServerTableValuedFunction` is a **bespoke** wrapper
  (`OutputSchema` property + `ExecuteScan` returning the stream **directly**) — a pushdown source is
  stream-native: `ScanFromSource`'s stream schema already reflects the PROJECTED columns, so it matches
  the projected batches and must NOT be re-wrapped with the full schema (doing so crashed `arrow_ingest`
  with SIGSEGV on `SELECT sq FROM tf_ms(4)`). `ProcResultColumns`/`ProcOutputParams`/`FunctionOutputColumns`/
  `ScanFromSource` widened to `internal`. The **table-function session ABI v29** (`c2e452f`+`1f9fe96`) then
  unified the dispatch under `ITableFunctionSession` (`tablefn_bind`/`tablefn_execute`/`tablefn_close`); see the Phase 5
  section. The bespoke TVF could now fold into `ITableFunction` (`tablefn_execute` returns a stream) but
  needn't — the dispatch is already unified.

- **Load-time global functions = the 4th provider-self-description capability** (**ALL FIVE kinds DONE — global
  SCALAR + IN-OUT + COLLECTOR + TABLE + AGGREGATE, ABI v46; + the host-FS table sub-case DONE, ABI v47**). The
  **host-FS global table** sub-case (secret-backed lakehouse readers) is now built: a global table reader that
  does IO through DuckDB's FileSystem needs the calling operator's opener (`ClientContext`, for secret
  resolution), threaded to the C# binding via **one appended ABI entry `set_active_opener`** — a per-thread
  ambient (`AmbientOpener`, mirroring `set_active_txn`) the host sets in the shared table bind/init hooks
  (`PopulateReturnSchema` + `ArrowStreamInitGlobal`), read by the host-FS binding in `Bind`/`Execute`. NO opener
  param + NO new operator (the v29 table session is reused verbatim). **`fabricator_delta_scan` migrated to a
  pure-C# global host-FS `ITableFunction`** (`DeltaGlobalTableFunction`, Bridge, over engineered-wood +
  `DuckDbTableFileSystem`, declared in `CustomFunctions.GlobalTable`); the bespoke `fabricator_delta.cpp` + the
  `delta_schema`/`delta_scan` ABI entries were **removed** — so a NEW lakehouse format (Iceberg/Lance/…) is added
  with **zero C++** (a pure-C# `ITableFunction` reading `AmbientOpener.Current` + files via the host `fs_*`
  callbacks, declared as a global). `test/verify_delta.test` (60), `test/verify_global_functions.test` (63), full
  SQL function suite unregressed. See [docs/global-functions.md](docs/global-functions.md) §"Host-FS global
  table functions". **The reader STREAMS lazily** (captures the opener — valid for the whole execution — and
  pulls one batch at a time, no materialization) **and pushes the FILTER into engineered-wood file + row-group
  skipping** (`DeltaFilterBuilder` maps the `FilterNode` → `EngineeredWood.Expressions.Predicate`; the
  superset-safe policy lives in the shared C++ `FilterSerializer` gated on `string_order_pushable` — string
  ordering + `BETWEEN` push only for a byte-ordered source. `DeltaGlobalTableFunction.StringOrderPushable=>true`
  (Parquet stats byte-ordered like DuckDB's default), so all comparisons `=`/`<>`/`<`/`<=`/`>`/`>=` + `IN` +
  `BETWEEN` push incl. strings. The SQL catalog scan shares the encoder + flag, so it ALSO pushes string
  ordering/`BETWEEN` under a binary `_BIN2` collation (latent win, `dm_exec`-proven in
  `verify_collation_pushdown`); discovered SQL TVFs stay equality-only (collation-dependent). The C# signal
  rides the `list_global_functions` metadata (a `string_order` column) → `FabricatorTableFunctionInfo` →
  `bind_data.string_order_pushable`; no ABI bump.
  The predicate drives the Delta file pruner `ReadAllAsync(columns, filter)` AND per-file Parquet row-group
  pruning via `ParquetReadOptions.Filter` on a per-scan `DeltaTableOptions` — no engineered-wood change). Column
  PROJECTION into the Parquet read stays deferred: the shared `TableFunctionBindingAdapter` wraps the result stream with
  the binding's FULL `OutputSchema`, so a projected subset mismatches it (arrow_ingest SIGSEGV) — DuckDB projects
  above the scan instead (a pushdown-native `ITableFunctionSession` would be needed; small follow-up). See
  docs/filesystem-bridge.md §"Streaming + filter pushdown".
  Connection-free, ATTACH-free functions registered at `Extension::Load`
  via `loader.RegisterFunction`. **Slice 1 built + verified**: `list_global_functions` enumerates the
  provider-union at load + a **`handle==0`** branch on `get_function_*_schema`/`execute_scalar` resolves a
  function by name against the C# `GlobalFunctions` registry; `IBackend.GlobalScalarFunctions` declares them;
  C++ `RegisterFabricatorGlobalFunctions` builds a `ScalarFunction` per scalar decl (shared
  `BuildFabricatorScalarFunction`, handle=0) at load (best-effort — skipped if the bridge can't boot). Demo
  **`fabricator_render(template, params)`** — the **Fluid/Liquid** template engine (secure-by-default,
  parse-once cached); `params` accepts a **DuckDB STRUCT** (`{'name':'x'}`, type-safe) OR a JSON string via the
  **`SQLNULL→ANY` sentinel now wired for scalars** (the scalar builder maps SQLNULL→ANY + marshals the exec chunk
  by its runtime `DataChunk::GetTypes()`, not the declared signature; `Invoke` reads a StructArray or StringArray).
  Resolves as a bare `fn(...)` with NO ATTACH (`test/verify_global_functions.test`;
  validated live via the shell). Unblocks the TMDL render step (render = pure global scalar; apply = table/
  collector). **Full plan: [docs/global-functions.md](docs/global-functions.md)** — covers **all four kinds** (scalar / table
  / in-out / collector) through **one mechanism**: +1 ABI entry `list_global_functions` (enumerate the
  provider-union at load) + a **`handle==0` marker** on the existing *bind* entries (`get_function_*_schema` +
  `execute_scalar`; `tablefn_bind`; `inout_bind`) so the per-call binding resolves against a global registry — the
  returned binding handle is concrete, so `tablefn_execute`/`inout_exchange_open`/etc. are unchanged. **Global
  table + in-out cost ZERO new ABI beyond the scalar entry** (arg-dependent output schema is already solved by the
  v29 `tablefn_bind` / v28 `inout_bind` sessions). C# = a base/derived interface split per kind (`IScalarFunction`
  + `ICatalogScalarFunction` [rename of `IArrowScalarFunction`], same for `ITableFunction`/`IInOutFunction`/
  `ICollectorFunction`) + `IBackend.GlobalScalarFunctions`/`GlobalTableFunctions`/`GlobalInOutFunctions`/
  `GlobalCollectorFunctions`; C++ `RegisterFabricatorGlobalFunctions` branches on `kind` at load →
  `loader.RegisterFunction`. Slices: (1) scalar **DONE** — template engine **`fabricator_render`** via **Fluid**
  (Liquid, secure-by-default); (2) in-out/collector **DONE** (pure-C#, **no opener**; demos `fabricator_tag`
  streaming + `fabricator_collect_sum` collector; `inout_bind` handle-0 → C# global registry; reuses the v28
  exchange ABI, no bump — enables the effectful global *apply* half, e.g. `fabricator_apply_tmdl` collector);
  (3) compute/connstr table **DONE** (`tablefn_bind` handle-0 → `GlobalFunctions.ResolveTable` over the v29
  session; the handle-0 `get_function_param_schema` is kind-agnostic via `GlobalFunctions.ParamSchema`;
  `TableFunctionBindingAdapter` moved to the Bridge; demos `fabricator_seq` fixed-schema + `fabricator_columns` arg-dependent
  schema); (4) aggregate **DONE** (`IAggregateFunction` base + `ICatalogAggregateFunction`; `AggSessionImpl` →
  the Bridge as public `AggregateSession` shared by catalog+global; `agg_open` handle-0 →
  `GlobalFunctions.ResolveAggregate`; `ParamSchema`/`ReturnField` kind-agnostic; shared
  `BuildFabricatorAggregateFunction`; reuses the v25/v26 `agg_*` ABI; demo `fabricator_product` — GROUP BY/parallel/
  OVER); (5) **deferred** host-FS table (secret-backed readers like delta) — needs an **opener arg** on
  `tablefn_bind`, delta stays bespoke until a 2nd such reader.
  Composes with TMDL = render-via-(global)scalar then apply-via-(global)table/collector. The rest of this bullet
  is the original table-case detail.
  Today provider functions are **attach-time catalog-bound** (4e/4f/4g — resolved as `db.schema.fn`, dispatched
  via the catalog `handle`). The deferred **Phase 3-A** alternative is **load-time global** functions
  (connection-free, bare `fn(...)`, registered at `Extension::Load`). **This does NOT break the catalog-only
  concept — the two are orthogonal scopes that coexist**: catalog-bound = needs an ATTACH'd catalog + its
  connection (discovered SQL UDFs/procs/TVFs, custom fns using the catalog's SQL conn); global = connection-free
  (`fabricator_delta_scan(path)`, future `fabricator_iceberg_scan`, lakehouse readers — they belong to no SQL Server
  catalog). **The original objection has dissolved:** Phase 3 deferred global functions to avoid booting the CLR
  at `Extension::Load()`, but the settings refactor (v33) + the fs/delta spike already boot the bridge
  best-effort at load. So global functions are now the natural **4th member of the "provider declares; core
  stays name-agnostic" family** (after settings v33 / ATTACH options v37 / secret fields v38): a
  `list_global_functions(provider)` ABI at load → C++ registers each declared scalar/table function
  **generically** (dispatch to C# by name/`decl_id`), the provider authoring them in C# with **zero per-function
  C++**. `fabricator_delta_scan` is **already a global function** (bespoke `RegisterDeltaScan` in
  `fabricator_delta.cpp`) — proof the scope exists. **Two wrinkles found while scoping the generic build (why it's
  deferred until a 2nd lakehouse format/provider lands, not justified by one function):** (1) **arg-dependent
  output schema** — a global table fn's columns depend on its args (delta's schema comes from the `path`), so the
  generic registration must use the v27/v29 `tablefn_bind`(args→schema+binding) shape, not the no-arg
  `get_function_output_schema`; (2) **the opener vs SQL-connection split** — `tablefn_bind`/`tablefn_execute` pass the
  **catalog handle** to C# (SQL fns use the catalog's `SqlConnection`), but `fabricator_delta_scan` needs the
  **host-FS opener (ClientContext)** for IO, which that path doesn't thread through. So **"build the generic
  global path" and "migrate delta onto it" are separable**: the generic path is cleanest for connection/
  connstr-style global functions; **delta is better kept bespoke** (its host-FS-opener need is special) unless the
  global table-fn bind/execute ABI gains an opener arg (SQL fns ignore it). Build it when the 2nd lakehouse
  format/provider arrives; until then delta stays the hand-written ~60-line `fabricator_delta.cpp`.

- **VARIANT for the Delta provider — V1 BUILT (2026-07-06): [docs/variant-support.md](docs/variant-support.md)
  §"AS BUILT"**. `CREATE TABLE lake.s.t (v VARIANT)` / CTAS / INSERT / SELECT / **DV-DELETE** work under
  `native_read true, native_write true` (`test/verify_delta_catalog_variant.test`, 55; **delta-kernel reads
  the result** — the unverified-kernel risk is resolved). The mechanism is NOT the planned per-operator
  pre-cast: **one `ArrowTypeExtension` registered at load** (`src/fabricator/fabricator_variant.cpp` —
  `RegisterFabricatorVariantExtension`; the exporter's `default:` branch consults the registry UNGATED by
  `arrow_lossless_conversion`) makes VARIANT cross EVERY Arrow boundary transparently (bulk/CTAS/COPY
  appenders, host-query result AND input streams — so the streaming COPY sees real VARIANT with no SQL cast —
  scan ingest, `FetchTableColumns` bind schema). Conversions = the parquet extension's scalars via
  `FunctionBinder`/`ExpressionExecutor` (`variant_to_parquet_variant` out / `variant_bytes_to_variant` in).
  **Transport = ONE self-delimiting BLOB per row** (metadata bytes ++ value bytes), marker
  **`ew.variant_transport`** — NOT the canonical struct: `ArrowAppender::FinalizeChild` walks the LOGICAL type's
  children (VARIANT = 4) against the internal-type appender (2) → a nested internal type crashes upstream
  ("index 2 within vector of size 2"; **FILED UPSTREAM 2026-07-25 as duckdb/duckdb#24157**, "Support VARIANT
  over the Arrow interface (arrow.parquet.variant)" — use `extension_data->GetInternalType()` there. If it
  lands, the leaf-blob transport below could in principle become the canonical struct, but do NOT plan on
  that: the transport is also what makes EW's internal model the canonical `VariantType`, and Spark/kernel
  interop is validated against the blob form);
  a LEAF internal type (the bool8/geoarrow shape) sidesteps it. EW: `"variant"` primitive ⇄ tagged blob
  (`SchemaConverter.VariantExtensionName`), `variantType` reader+writer feature at create + protocol upgrade
  on ADD COLUMN/SetSchema via the generalized `UpgradeProtocolForFeatures`/`RequiredSchemaFeatures` (replaced
  `UpgradeProtocolForTimestampNtz`), allowlists, stats leaf (nullCount only). Gates (clean errors): variant
  requires native_read+native_write (Bridge, at CREATE/INSERT/scan), CDF-on-create rejected, EW backstops
  reject codec data writes + CoW DELETE / **UPDATE** / **OPTIMIZE** ("would strip the VARIANT annotation" —
  the rewrite READ half is the codec reader even under native_write; DV DELETE is exempt = works, and DV is
  the default). Gotchas: NULL rows can arrive as VALID zero-length blobs (validity dropped in the crossing) —
  ingest maps empty→NULL (minimal `01 00 00 00` variant-null substitution, spec-exact `v IS NULL` round-trip);
  member access = dot `(v).a` (`variant_extract('$.a')` returns NULL in 1.5.4; `->` casts via JSON and fails);
  DuckDB's writer SHREDS small variants by default (read-transparent). Regression: delta 39/39, SQL fn suites
  11/11, EW 168+141. **Fabric Runtime 2.0 VALIDATED LIVE (Spark 4.1.1, both directions)**: we write
  (`lake.dbo.fabricator_varlive`, onelake:// streaming COPY) → Spark `to_json`/`variant_get` read it exactly;
  Spark writes (`fabricator_var_spark`, `parse_json`) → we read typed VARIANT + dot access + correct SQL-NULL.
  One nuance: a SQL-NULL variant WE write reads in Spark as variant JSON-null (`v IS NULL` false there;
  DuckDB reads the same file as SQL NULL — DuckDB-writer representation, not transport). The **SQL Server
  provider REJECTS variant columns** cleanly (`BuildCreateTable` guard — else the tagged blob would silently
  become VARBINARY; cast `v::JSON` to move variant into SQL Server). **V2 DONE — UPDATE/CoW-DELETE/OPTIMIZE
  LIFTED via the `IDataFileReader` codec seam** (docs/variant-support.md §"AS BUILT" second pass): EW gained
  the read-side counterpart of `IDataFileWriter` (`DeltaTableOptions.DataFileReader` — RAW physical batches
  in FILE ORDER, DV rows included; `ReadFileAsync` + `CompactionExecutor` route through it; the per-batch
  pipeline extracted as `ProcessFileBatchesAsync`); Bridge `NativeParquetDataFileReader` = `read_parquet(...,
  file_row_number => true) ORDER BY file_row_number` (explicit order — positions are correctness), wired on
  `native_read` into DeleteByRowIds/UpdateByRowIds/Optimize. With BOTH seams the EW variant gates return
  early → variant UPDATE (incl. SET of the variant value — `BuildArray` Binary case + the rewriter
  update-view keeps the marker metadata so the bound view types as VARIANT) + CoW DELETE + OPTIMIZE all work,
  kernel-validated post-DML (`verify_delta_catalog_variant.test` 72). **Crux fix: the clean-rebuild before
  every rewrite (`DeltaTable.CleanField`, 4 sites) now preserves `ARROW:extension:*` metadata** — it stripped
  ALL metadata, which would silently drop the variant tag on any rewrite. Seam caveat: id-mode projection
  under the seam resolves by physical NAME (field-id resolution needs the parquet footer the seam hides —
  exact for spec-written files). Regression: delta 39/39 + EW 141/168 green. **Third pass closed both
  loose ends** (`verify_delta_catalog_variant.test` now 87): (a) **mapped variant WORKS with zero code**
  (id-mode default: physical `col-<guid>` + field_id on the variant group in the parquet, RENAME COLUMN
  metadata-only, UPDATE of the variant value post-rename, kernel-reads — the FIELD_IDS-on-VARIANT concern
  didn't materialize); (b) **variant is TOP-LEVEL-only, enforced up front**: DuckDB's parquet writer
  REJECTS a non-root VARIANT ("requires a transform, but is not a root column" — upstream limitation), so
  `EnsureVariantWritable` rejects struct/list/map-nested variant at CREATE with that reason, and EW's
  `FromArrowType` throws on a variant list-element/map-entry marker instead of silently degrading it to
  `binary` (the degradation bug the probe found). Struct-nested variant maps fine at the EW schema layer
  (an external Spark table could carry it; reads may work) — only WRITES are gated.
  **Fourth pass — CODEC-PATH variant (tier 1, `VariantTransport`): the native-flag requirement for plain
  CREATE/INSERT/SELECT is GONE** (`verify_delta_catalog_variant.test` 102). Key discovery: EW's PARQUET
  layer already had complete, tested unshredded variant (the pinned **Apache.Arrow 23.0.0 nuget contains
  `VariantArray` + `VariantType` extension** — the release-content question is answered; EW's
  `ArrowToSchemaConverter` maps the extension → VARIANT-annotated group, `NestedLevelWriter` unwraps it,
  the reader wraps back via `ParquetReadOptions.ExtensionRegistry`, round-trip tests in
  `VariantArrayRoundTripTests` — EW's known-issues "VARIANT not supported" note is STALE). The missing
  piece was Delta-layer glue: **`EngineeredWood.DeltaLake.Table/VariantTransport`** converts the
  `ew.variant_transport` blob transport ⇄ `VariantArray`/bare storage struct — write side in `WriteCoreAsync`'s
  codec branch (replacing the gate; splits each blob via the self-delimiting metadata header), read side in
  `ProcessFileBatchesAsync` after the renames (struct→blob+tag; a host-reader blob passes through;
  **shredded files [typed_value child] → clean error** — reassembly needs a variant engine). Bridge:
  `EnsureVariantWritable` keeps only the top-level-only + CDF rejections; `ThrowIfVariantCodecRead` removed.
  Cross-codec matrix: codec-written (unshredded) is read by EVERYONE (kernel-validated + native reader);
  native-written is SHREDDED by default → codec read = the clean shredded error (native/Spark/kernel read
  it fine). Codec REWRITES (UPDATE/CoW/OPTIMIZE) stay gated by the EW backstop on codec-only catalogs
  (their write sites aren't transformed; DV DELETE works). Tier 2/3 (shredded read / shredded write in EW)
  deferred — blueprints: DuckDB in-tree + **the pinned Apache.Arrow.Scalars ships the FULL variant toolkit**
  (`VariantBuilder`/`VariantValueWriter`/`VariantMetadataBuilder` + readers — tier 2's engine exists).
  **Codec form VALIDATED LIVE on Fabric Spark 4.1 (fifth pass)** after one fix: EW emitted the parquet
  `VariantType` annotation as an EMPTY thrift struct — **Spark requires `specification_version` (= 1)**
  (generic `FAILED_READ_FILE`; kernel/DuckDB tolerate the empty form; DuckDB's writer sets it too). Isolated
  via an A/B/C matrix on OneLake (codec+variant failed, codec+rowTracking-no-variant read fine → the
  annotation, not encodings/row-id). With the version byte written (`MetadataEncoder` case 16), Spark reads
  BOTH codec-written OneLake variant tables (object/null/array exact, default DV+rowTracking config
  included). Fix recorded in EW doc/upstream-candidates.md slice 1.
  **Sixth pass — FULL SHREDDING for the EW codec (tiers 2+3, `verify_delta_catalog_variant.test` 133).**
  The entire engine came from **`Apache.Arrow.Operations` 23.0.0 (published nuget, now referenced by EW
  DeltaLake.Table)**: `ShredSchemaInferer` (per-file schema from the column's values), `VariantShredder`
  (rows → typed+residual with a SHARED metadata dictionary so residual field-name refs stay valid),
  `ShreddedVariantArrayBuilder` (the shredded Arrow storage), and
  `VariantArrayShreddingExtensions.GetLogicalVariantValue` (per-row reassembly of ANY spec layout).
  `VariantTransport` now: WRITE = parse blobs → infer → shred when a schema applies (uniform
  objects/primitives/arrays; mixed stays unshredded; **SQL-null rows re-applied as storage VALIDITY** after
  the builder — distinct from variant JSON null); READ = `typed_value` present (or no `value` column) →
  `GetLogicalVariantValue` + `VariantBuilder.Encode` per row (passthrough concat kept for unshredded).
  **Cross-validated: DuckDB native, delta-kernel AND raw `read_parquet` all decode the codec-SHREDDED file
  exactly** (incl. the residual-merge row with an extra field), and the codec reads DuckDB-native-shredded
  tables — the cross-codec matrix is now FULL (both write both forms' semantics, both read everything).
  **Spark VALIDATED LIVE on the codec-SHREDDED form** (`lake.dbo.fabricator_varshred`, sparkprobe
  `variantshred`): all rows exact via `to_json` incl. the residual merge; `variant_get` on a SHREDDED
  field works (Spark exploits the typed columns); and the SQL NULL round-trips as TRUE SQL NULL
  (`WHERE v IS NULL` matches — the storage-validity null; NOTE this is BETTER than the unshredded
  DuckDB-native-written form, whose SQL NULL reads as JSON null in Spark). The Operations builder's
  OPTIONAL-field-groups deviation (spec says REQUIRED) is tolerated by ALL strict readers
  (Spark/kernel/DuckDB) — arrow-dotnet upstream-mention material, not a blocker.
  **Fabric T-SQL endpoint verdicts (user-tested live 2026-07-06):** (a) **id-mode column mapping is
  UNSUPPORTED** — the table doesn't even LIST (`UnsupportedColumnMappingMode`); `name`/none are fine;
  our catalog DEFAULT is id → open decision: flip the default to 'name' for endpoint reach (name has the
  same metadata-only RENAME/DROP). (b) struct/list columns degrade GRACEFULLY (table reads, nested
  columns silently omitted — the endpoint projects scalars only). (c) **a VARIANT column errors the
  ENTIRE table at footer parse** ("Msg 15813 … Thrift LogicalType that is not recognized" — their parquet
  stack predates the VARIANT logical type; even scalar projections fail). Guidance: endpoint-reachable
  semi-structured data → JSON (VARCHAR) columns, NOT VARIANT; VARIANT = Spark/DuckDB/kernel pipelines.

- **NESTED STRUCT-field schema evolution — DONE (2026-07-07; additive alter kinds, NO ABI bump).**
  DuckDB's field DDL (`ALTER TABLE t ADD/DROP/RENAME COLUMN s.f ...` — `AddFieldInfo`/`RemoveFieldInfo`/
  `RenameFieldInfo` with a `column_path`, first-class in 1.5.4) now works on the Delta catalog as
  METADATA-ONLY commits. C++ `Alter` gained ADD_FIELD/REMOVE_FIELD/RENAME_FIELD cases → new
  `FABRICATOR_ALTER_ADD_FIELD/DROP_FIELD/RENAME_FIELD` kinds (additive enum values 9-11; paths cross as a
  JSON array of segments since names may contain dots; the new field rides the existing single-field
  Arrow stream). EW gained the nested analogs `AddFieldAsync`/`RenameFieldAsync`/`DropFieldAsync`
  (`TransformStructAt` schema rebuild at any depth; mapping assigns ids+physicalNames to an added field
  RECURSIVELY via the create-time `AssignColumnMapping` — struct/array/map-typed additions get ids on
  every descendant; the same helper FIXED a latent top-level `AddColumnAsync` bug that committed
  spec-violating metadata for struct-typed adds under mapping, kernel-validated; rename/drop
  require mapping, same rule as top-level; protocol upgrade fires for schema-driven features of the new
  type). **The crux: the read reconciliation is now RECURSIVE** — `BackfillMissingColumns` +
  `ReconcileColumn` rebuild a struct whose child set differs from the current schema (ADDed member -> a
  typed all-NULL child sized to the PHYSICAL child length [parent offset+len, the TakeRows convention],
  DROPped member removed, children recursed; parent validity/offset preserved) — shared by the reader,
  compaction and the rewrite paths, so DML on evolved tables just works. `MakeNullArray` extended
  (struct/list/Decimal256/Date64/Time32/Time64 + honest THROW on unknown types — it silently backfilled
  a StringArray before, a latent wrong-type corruption for non-primitive top-level adds).
  delta-kernel reads the evolved tables exactly (standard metadata commits).
  `test/verify_delta_catalog_nested_alter.test` (71 — two-level adds, mixed-vintage reads + predicates,
  rename/drop, DV DELETE + UPDATE on evolved tables, re-attach durability, unmapped guards, struct-typed
  add on plain AND mapped incl. top-level, + native_read over evolved mixed-vintage tables — now 100); delta suite green;
  EW 147+168. SQL Server/DAX reject the new kinds cleanly. **The native_read PRESENCE PROBE (second pass,
  same day) lifted the evolution limitations — and fixed the PRE-EXISTING top-level one:** `native_read`
  of a file predating ANY added column/member failed loudly in BOTH mapping modes (top-level: the alias/
  projection referenced a column absent from the old file — broken since slice 1, just never tested;
  nested: the struct rebuild referenced the new member). Now `ResolveFileMapping` ALWAYS footer-probes
  each file's `parquet_schema` (`ProbeFileNodes` — DFS path reconstruction via the num_children stack:
  node PATHS [PathSep=-joined, dot-safe], per-node field ids, direct-children map; the id-mode
  fid probe is subsumed; the footer bytes are cache-warm for the subsequent read_parquet). `FileSql` is
  now presence-aware per column: absent top-level -> `CAST(NULL AS <type>) AS "c"`; a struct whose
  CURRENT member tree differs from the file (`StructShapeDiffers`: mapped rename, ADDed member [absent
  by fid OR stored path], DROPped member [extra file children], recursive) -> the struct_pack REBUILD
  with `CAST(NULL AS ...)` for absent members; `TypeText(DeltaDataType)` renders the DuckDB cast targets
  (timestamp->TIMESTAMPTZ, timestamp_ntz->TIMESTAMP, decimal(p,s) passthrough, STRUCT(..) recursive
  with LOGICAL member names, arrays/maps; variant -> loud throw). This ALSO fixes the advertised-schema
  staleness (ProbeSchema's LIMIT-0 probe file could be an OLD vintage — its FileSql now emits the
  current shape). Cost: one footer-only host query per file on every native scan (was id-mode-only).
  `verify_delta_catalog_nested_alter.test` now 91 (native_read evolved sections, both modes).

- **Delta write-side NOT NULL enforcement — DONE (2026-07-07; C#-only, no ABI).** Found by adapting
  duckdb-delta's `non_nullable` test: our Delta INSERT happily wrote a NULL into a column whose Delta schema
  declared `nullable:false` (a spec violation — writers MUST enforce; Spark trusts non-nullable schemas on
  read). New Bridge `DeltaNullability` (driven by the table's authoritative Delta schema, active only when it
  carries a constraint): per-batch validation on the **collect path** (`DeltaWriter.Write`, Append — before any
  file is written) + a **lazy validating stream wrapper** on the streaming path (`TryWriteStreaming`, wraps
  BEFORE the physical rename; fallback `return null` leaves the stream unconsumed) + **UPDATE SET** validation
  (`ExecuteUpdate` — scalar + the ReadScalarDeep struct dictionary, recursive; missing key = implicit NULL).
  Covers NESTED constraints (struct fields incl. deep nesting, list `containsNull=false`, map
  `valueContainsNull=false` — parent-validity-masked per row, children indexed at `Data.Offset + i` per the
  TakeRows convention) — external Spark tables declare these even though DuckDB DDL can't. Errors match DuckDB
  wording: `NOT NULL constraint failed: <table>.<path>`. Overwrite/REPLACE/CTAS adopt the input schema →
  deliberately not validated (drop+recreate semantics). Partial-column INSERT omitting a non-nullable column =
  implicit NULL → error. **Adopted from duckdb-delta** (test survey 2026-07-07): the
  `null_constraints_structs` fixture (`test/fixtures/` — Spark-declared nested non-nullables; their
  `null_constraints_lists` fixture was dropped — its commits are pretty-printed multi-line JSON, spec says
  NDJSON, EW rightly rejects it), the issue-297 class (all-NULL column stats + prune safety), the issue-303
  class (partition equality / single-value IN must not over-prune), and **explicit-transaction semantics
  PINNED as a documented divergence**: our Delta writes commit PER STATEMENT — multiple INSERTs in a BEGIN are
  separate Delta commits and ROLLBACK does NOT undo them (duckdb-delta buffers appends until COMMIT; a
  per-transaction append buffer is a possible future). `test/verify_delta_catalog_constraints.test` (50).
  Still open from the survey: a DAT conformance test (the delta-incubator Delta Acceptance Testing corpus via
  `require-env FABRICATOR_DAT_PATH` — validates default/native_read/deltars readers against golden tables incl.
  Spark checkpoints).

- **Delta IDENTITY columns — DONE (2026-07-06)**: the v53 generated-column marker (`id BIGINT AS (0)`) now
  works on the Delta provider (`test/verify_delta_catalog_identity.test`, 38; kernel-reads). The heavy lifting
  ALREADY EXISTED in engineered-wood (`IdentityColumn` config/metadata keys + `IdentityColumnWriter.ProcessBatch`
  per-batch generation + same-commit `delta.identity.highWaterMark` update in `WriteCoreAsync`;
  `SupportsExternalDataFileCommit=false` for identity tables → the streaming COPY path falls back to collect,
  which under `native_write` STILL emits native parquet via the `IDataFileWriter` seam) — only two pieces were
  missing: (1) EW `CreateAsync` now declares the **`identityColumns` writer feature** (writer-only, v7 list)
  when the schema carries `delta.identity.*` metadata; (2) Bridge `DeltaCatalog.CreateTable` attaches
  `IdentityColumn.CreateMetadata(1,1,false)` (GENERATED ALWAYS) to the marked fields (previously ignored;
  kept nullable=true DuckDB-side — the INSERT stream carries NULLs that ProcessBatch replaces). Values
  continue across statements + re-attach (hwm in schema metadata); an explicit NULL is engine-assigned.
  **OCC retry is SAFE for identity appends** (better than Spark, which rejects concurrent identity txns):
  the retry reopens at the new version and ProcessBatch REGENERATES from the fresh snapshot's high-water
  mark (input batches unmutated) — no baked-values problem, no special-casing needed.

- **DAX / ADOMD 2nd provider** (the "one binary, many providers" goal) — **design + slices:
  [docs/dax-provider.md](docs/dax-provider.md)**. **Slice 1 DONE + validated against a live local Power BI
  Desktop instance**: new project `Fabricator.AnalysisServices` (`DaxBackend : IBackend` provider `"dax"`,
  aliases `adomd`/`powerbi`/`ssas`/`fabric`; `DaxCatalog : IBackendCatalog`; `PowerBiDesktop` port detection).
  `ATTACH 'pbidesktop://' AS pbi (TYPE fabricator, PROVIDER 'dax')` auto-detects the local msmdsrv port
  (Windows-only, newest workspace's `msmdsrv.port.txt`) → AdomdConnection; `GetMetadata(Schemas)` = model
  name(s) from `$SYSTEM.TMSCHEMA_MODEL` so the model shows as a DuckDB schema. Other targets pass through as
  an ADOMD connstr (SSAS/Fabric/AAS). **No ABI/C++ change — pure C# provider** reusing the catalog/scan/
  function machinery. `BackendRegistry` default is now `Fabricator.SqlServer,Fabricator.AnalysisServices` (missing
  assembly skipped; SqlServer stays default → existing ATTACHes unchanged); `publish-managed.ps1` publishes
  both into one `fabricator/` dir (Bridge + SqlClient + `Microsoft.AnalysisServices.AdomdClient` 19.96.1 — the
  plain managed package, **not** `.retail.amd64`). Connection round-trip de-risked via `scratchpad/dax-spike`
  (AdomdClient loads in net10, DMV + `EVALUATE` + `GetSchemaTable` all work). DAX is **read-only** (writes
  throw; BEGIN/COMMIT/ROLLBACK no-op). **Key analysis findings** (from `D:\repos\SqlServerFlights`): schema
  resolution = execute-at-bind + `GetSchemaTable` + stash the reader (NO `TOPN(0)`); params = ADOMD named
  binding ++ DATATABLE injection; **filter pushdown was never implemented** (projection-only); metadata = DMV
  `SELECT`s (NOT the UNION-ALL trick). Cross-platform AdomdClient (Linux/Fabric XMLA) is **Windows-first,
  Linux-TBD**. **Slices 2–3 DONE + validated** (live local model): slice 2 = DMV table/column discovery
  (`TMSCHEMA_TABLES`; columns via `EVALUATE TOPN(0,'T')`+`GetSchemaTable` = real engine types, no TOM-enum
  guessing; `DaxTypeMap` CLR→Arrow incl. `Decimal`→`Decimal128(p,s)`; `'T'[Col]`→`Col`); slice 3 = table
  scan via `EVALUATE SELECTCOLUMNS` projection + **filter pushdown** (`DaxFilterBuilder` wraps the table in
  `FILTER('T', <pred>)`; superset-safe — DuckDB re-applies; string `=`/`IN`/`ISBLANK` push for any type,
  `<>`/range push only for non-string since DAX strings are case-insensitive/collation-dependent; `and`
  drops unpushable children, `or` is all-or-nothing; constants inlined as DAX literals via the Bridge-shared
  `ArrowValueReader`), **TRUE incremental
  streaming** (`DaxArrowStream`, ≤1 batch buffered — validated to **10.5M rows**), a **`system` schema**
  exposing a curated set of VertiPaq/`$SYSTEM` DMVs as tables (`db.system."TMSCHEMA_TABLES"` etc. —
  `DaxCatalog.SystemTables`; bare `SELECT * FROM $SYSTEM.<dmv>`, no pushdown; metadata/scan branch on the
  `system` schema; 14 DMVs validated live), **`daxeval(expression := …, params := …)` function** (slice 4 +
  param binding — under the model schema; evaluates an arbitrary DAX `EVALUATE`/`DEFINE…EVALUATE` query,
  output schema resolved at bind via `GetSchemaTable` no-describe, `DaxEvalTableFunctionSession`, `SupportsPushdown=false`,
  streams; validated ROW/COUNTROWS/SUMMARIZECOLUMNS/full-table. **Registered `kind='proc'`** (not `'table'`)
  so args are NAMED params — that's what allows the optional `params` arg without breaking the no-arg call.
  **`params` accepts EITHER a DuckDB `STRUCT` (`{'a':40,'b':2}`, preferred — type-safe, no quoting) OR a
  JSON string (`'{"a":40}'`, for programmatic callers)** — each field/key bound as an ADOMD `@<name>` param
  the DAX references (`ReadStructParams`/`ParseDaxParams` → `BindDaxParams`, for both the bind probe + each
  execution; args read by field name). **The struct crosses with NO ABI change** via a generic marker: a
  provider declares an "accept any value" named param as the **`NullType` sentinel** → C++ registers a
  **FABRICATOR RENAME — LIVE-VALIDATED (2026-07-15):** the rename holds through the real tooling, not just the
static unittest. **dbt** (`dbt_mssql_test`, gitignored — loads the rebuilt loadable into the official duckdb
1.5.4 wheel): `box` target 10 models green (CTAS/incremental/index-post-hooks via `fabricator_exec`, threads=4);
`minio` target s3 Delta green (`fabricator_delta_scan`). **Fabric notebook** (`scratchpad/fabricnb`, gitignored):
25 probe steps pass — `load extension`, delta local/OneLake-fuse/create-read-txn/abfss-ambient, DAX ambient, and
every SQL-auth form (bare/authentication-default ambient + database-audience/lakehouse/warehouse token secrets);
the only 2 fails are documented-expected (pbi-audience token → 18456 reject; static-azure-secret abfss → the
pinned single-audience gap, ambient works). TWO harness bugs found+fixed from the ATTACH-vs-CREATE-SECRET
ambiguity (harnesses weren't in the tracked-code rename scope): probe `CREATE SECRET (TYPE fabricator)` →
`TYPE mssql`; dbt `ext_delta.sql` s3 bucket `s3://arrownet` → `s3://fabricator` (the compose + tracked s3 tests
use the `fabricator` bucket — `docker-compose.yml` was renamed too, so the MinIO bucket is `fabricator`).

`SQLNULL`-typed named param as `LogicalType::ANY` (`GetOrCreateTableFunction`) so DuckDB passes the literal
  UNCAST, and the shared table-bind marshaling keeps the value's **runtime** type for a `SQLNULL`-declared
  param (`FabricatorTableFunctionBind`) so a `STRUCT` marshals as a real Arrow struct. The guard is
  `SQLNULL`-only → every concrete-typed function is unaffected (full SQL fn suite green). Validated
  numeric/string/filter params, struct + JSON. No ABI change — reuses the proc named-param marshaling + v29
  table session), **`daxevaltable(<input>, expression
  := …)` in-out** (slice 5 — injects the input table as a DAX `DATATABLE` named `_input`, evaluates once,
  `DaxEvalTableBinding`/`DaxDataTable`; this required wiring **cost args (named params) through the shared
  exchange** — `GetOrCreateCustomInOutFunction` declares named params via a tolerant `FetchFunctionParamSchema`
  (empty for cf_tag → unchanged) + `FabricatorExchangeBind` marshals supplied named params into `inout_bind`
  args, else nullptr (`_each` unchanged); no ABI bump. Whole-table op, but the exchange has no emit-at-end
  hook [finalize drain discards trailing output] — **this single-chunk cap is now LIFTED: `daxevaltable` is a
  [collector](docs/inout-collector-mode.md)** (see below), so an arbitrarily large injected table works
  (validated live to 5000 rows). **The collector table-in-out (pipeline breaker) is BUILT + verified**: a
  second in-out execution shape (a Sink+Source: collect all input, emit at input-EOF) that coexists with the
  streaming exchange, picked by a new additive `kind='collector'`; reuses the v28
  `inout_bind`/`inout_exchange_open` ABI as-is (no bump). C# `ICollectorFunction`/
  `ICollectorFunctionBinding` (+ `StaticCollectorFunction` base, the `CollectorInOutBinding` adapter); C++
  `FabricatorCollector*` (in-out `Execute` buffers input into an `ArrowProducer` on the refcounted holder; the
  injected `FabricatorCollectorPhysical` Sink+Source opens the exchange at Finalize and **streams** the C# output
  — the Source pulls the `ArrowStreamReader` a vector-slice at a time, so **input is fully buffered (inherent)
  but output is never materialized**). SqlServer demo `dbo.cf_collect` (`test/verify_collector.test`, 40 —
  whole-table total, 5000-row multi-chunk, sequential-UNION threads=1, empty, NULLs, prepared re-exec; +50k-row
  streamed-output smoke). **`daxevaltable` migrated onto it**
  (`DaxEvalTableBinding : ICollectorFunctionBinding`, `kind='collector'`; reads the whole input into one DATATABLE
  → no 2048 cap; `daxeach` stays streaming `inout`) — validated live against Power BI Desktop
  (`test/verify_dax.test`, 29). In-out regression green: custom 89 / table_inout 63 /
  proc_inout 31 / isolation 17) + **`daxeach(<input>, expression := …)` in-out** (slice 5b — per-input-row
  ADOMD `@<col>` param binding, output = the DAX result per row, no echo; `DaxEachBinding` reuses one
  conn+command across rows, emits per chunk so NO input-size limit; the "each" analog of the SQL `_each`,
  renamed from the old `daxapply`). `verify_dax.test` 25/25. `WHERE`/`ORDER BY`/`LIMIT`,
  aggregation, exact decimals, `DESCRIBE`. **CRITICAL ADOMD GOTCHA (the real root cause):** `AdomdDataReader.Read()`
  called AFTER it already returned `false` (past end-of-data) does NOT return `false` again — it **throws**
  `AdomdUnknownResponseException` ("the server sent an unrecognizable response", `XmlaClient.ReadEndElementS`).
  Unlike `SqlDataReader` it is not idempotent at EOF. DuckDB pulls one batch AFTER the final (partial) one, so
  a batched reader must remember EOF (set `_done` when `Read()` first returns false) and never call `Read()`
  again — one-line fix in `DaxArrowStream`. **This invalidates the earlier (WRONG) "in-process Arrow-import
  interleaving / hosting-topology, must use `AdomdDataAdapter.Fill`/materialize/out-of-process sidecar"
  conclusion** — it was misdiagnosed as "fails on the 2nd chunk." Instrumentation (max-in-flight + per-call
  thread + rows-read) proved `maxInFlight=1` (no parallel access; `MaxThreads()==1` + `get_next` under mutex),
  single thread (no hopping), and the throw on the pull AFTER the last row (read-past-EOF), not the 2nd chunk.
  `Fill` and any tight `while(Read())` loop only "worked" because they stop at the first `false`. **Slice 6
  (Fabric/AAS Entra token auth) DONE + validated live against two Fabric semantic models on one warehouse.**
  ADOMD has no interactive auth in the CLR host, so `DaxTokenAuth` mints a Power BI-scoped token
  (`…/powerbi/api/.default`) and sets `AdomdConnection.AccessToken` (+ `OnAccessTokenExpired` refresh) — the
  principal is the **same azure SP secret the warehouse uses** (reused via the v39 foreign-secret path:
  `ATTACH 'Data Source=powerbi://…;Initial Catalog=<model>' (…, PROVIDER 'dax', SECRET <azure_sp>)` →
  `DaxBackend.BuildConnectionString` carries the secret fields to the catalog via a connstr marker → a
  `ClientSecretCredential`); a secretless remote XMLA endpoint falls back to `DefaultAzureCredential` (the
  "Active Directory Default" analog). New dep `Azure.Identity`. **Also fixed: honor the explicit `Initial
  Catalog`** — a workspace XMLA endpoint lists many models in `DBSCHEMA_CATALOGS` and we were binding to the
  FIRST (e.g. a lakehouse default model), so every DMV/metadata query came back empty; the connection's
  current catalog now wins (auto-discover only when none given). **Storage provenance via system tables
  validated:** `TMSCHEMA_PARTITIONS` (Mode/Type=5 = DirectLake Entity) → `ExpressionSourceID` →
  `TMSCHEMA_EXPRESSIONS.Expression` is the discriminator — `AzureStorage.DataLake(onelake…)` = DirectLake on
  OneLake vs `Sql.Database(…datawarehouse.fabric.microsoft.com)` = DirectLake on SQL (two models on one
  warehouse item). **DAX→SQL bypass PROVEN** (`SELECT count(*) FROM wh.dbo.Trip` via the SQL provider on the
  warehouse SQL endpoint = the DAX model scan, 2,838,927 rows) → a documented (NOT built) **DirectLake
  passthrough** idea: route base-table reads to the cheap SQL endpoint (measures/calc stay DAX), write
  warehouses via SQL / lakehouses via Delta + reframe, optional far-future TMSL model sync — design + honest
  good/bad triage in [docs/dax-provider.md](docs/dax-provider.md) ("DirectLake passthrough"). A follow-on
  **TMDL model-management** note (same doc) covers retrieve/apply a model definition via TOM: the
  generate-vs-apply split (pure scalar `render_tmdl` vs effectful table-function `apply_tmdl` — never a
  side-effecting scalar, optimizer purity) + bind-constant-vs-table-argument (dynamic per-row apply = the
  in-out `_each` form, fixed bind-time output schema) + three apply shapes: `apply_tmdl` (TVF, one commit) /
  `apply_tmdl_each` (in-out, N serialized commits) / **`apply_tmdl_agg` (4h aggregate — collect many
  fragments, ONE atomic commit at finalize; side-effect-safe since the effect is in Finalize, run once
  single-threaded)**. Then the **generic rename**
  (`fabricator_query`/`_exec`, catalog-type `"fabricator"`) + `BackendRegistry` multi-provider polish are due.

- **Multi-edition support** (Synapse / Fabric Warehouse / Lakehouse SQL endpoint) — **design:
  [docs/warehouse-support.md](docs/warehouse-support.md)**. **Slices 1–4 DONE + validated end-to-end against
  a real Fabric Warehouse** (edition 11, `BIN2_UTF8`): (1) `ServerProfile` (`ServerProfile.cs`) detected
  lazily on first connection via a **non-MARS probe** (so Fabric/Synapse, which reject a MARS connection, are
  classified before the MARS decision); **MARS gated on `profile.SupportsMars`** (the connection only works on
  Fabric because of this); (2) `fabricator_server_info(catalog)` diagnostic (`test/verify_server_profile.test`);
  (3) **profile-driven `MapArrowToSqlType`** — `NVARCHAR`→`VARCHAR` by `HasNVarchar`, `datetime2`/`time` scale
  by `MaxDateTime2Scale`, tz→`datetimeoffset`|UTC-`datetime2` by `HasDatetimeOffset`; box-preserving; CTAS to
  Fabric verified (`varchar(MAX)`+`datetime2(6)`, µs round-trip; **Fabric accepts `varchar(MAX)`**). Read +
  write paths (incl. `SqlBulkCopy`) both confirmed working on Fabric. (4) **connection mode** (C#-only, no ABI):
  `mssql_mars` tri-state provider setting (`auto`=`profile.SupportsMars` | `true` | `false`) resolved
  once at first connection; **MARS-off data SCANS take a fresh pooled connection** (no read-your-writes for
  scans in a write txn — `ExecuteQuery` gates the pinned-read branch on `_marsEnabled`). **Metadata reads are
  exempt** (`ExecuteMetadataQuery`, read-your-writes regardless of MARS): they're short (no held reader) and
  the pinned conn carries no concurrent scan on MARS-off, so a just-CREATEd table's column/rowid re-fetch sees
  the uncommitted `CREATE` on the pinned connection — without this the self-healing cache evicts the new table
  on Fabric (same-session `CREATE`+DML failed). **Fabric write transactions run at
  SNAPSHOT** (`ServerProfile.DefaultWriteIsolation`, edition-11-only — box/Synapse keep the server default).
  `mssql_mars` is **global** (`SET` before ATTACH); a per-catalog `mars` ATTACH option is now straightforward
  to add (the ATTACH-options→C# refactor landed, ABI v37 — `SqlServerCatalog` parses the options JSON; just
  add a `mars` key alongside `schema_filter`/`table_filter`/`isolation_level`). **Caveat:** `RESET` of an
  extension option does NOT fire its set-callback
  (`config.cpp ResetOption`), so it never clears the process-global `ProviderSettingsStore` — restore a setting
  with `SET name='<default>'`, not `RESET` (matters for `.test` hygiene across files). Verified:
  `test/verify_connection_mode.test` (20). (5) **collation-aware string `ORDER BY` pushdown** (no ABI):
  `FetchBinaryCollation` (reads the `SERVER_INFO` profile) caches a flag on the catalog at `LoadCatalog` →
  scan `bind_data.string_order_pushable` → `fabricator_optimizer` gate `is_string && !string_order_pushable`;
  string keys push only on a binary (`_BIN`/`_BIN2`) collation (byte-order sort == DuckDB).
  `test/verify_collation_pushdown.test` + `test/verify_orderby_pushdown.test`. (6) **JSON read-side gate**
  (C#-only): a SQL `json` column is tagged `arrow.json` in `SqlArrowMapping.ToArrowField` → DuckDB imports it
  as the `JSON` logical type (unregistered-extension fallback = `VARCHAR`, so it's safe + round-trips); the
  core `json` extension is **statically embedded** (`extension_config.cmake`) so the test binaries have the
  `JSON` type + functions (this build is v0.0.1 → json can't be autoloaded). `test/verify_json.test` (`require
  json`). **The box test DB is now SQL Server 2025** (major 17, native `json`). **§3.4 granular-types investigated
  (2026-06-24) → write-side DEFERRED** (`docs/warehouse-support.md` §3.4.1): `arrow_lossless_conversion` toggles
  an Arrow extension rep for 6 types (`BOOLEAN`→`arrow.bool8`/Int8, `HUGEINT`, `UUID`, `TIME_TZ`, `BIT`, `JSON`).
  The **read path** (C#-authored Arrow) is the principled, low-risk half — JSON read-side + UUID read-side done
  (`uniqueidentifier`→`FixedSizeBinary(16)`+`arrow.uuid`→DuckDB `UUID`, big-endian RFC-4122 bytes; scale-aware
  `time(7)`/`datetime2(7)`→ns; decimal already `(p,s)` — `verify_granular_types.test`). The
  **write path** flip is high blast radius (`ColumnAppender` has no `Int8` case → would corrupt every BOOLEAN
  write; also filter/UPDATE/DELETE value readers) AND **low warehouse value** (Fabric has no native `json`, so
  `varchar(MAX)` is already correct + the only option there; native-`json` write only helps box-2025/Azure where
  `nvarchar(max)` already holds the JSON). Recommendation: keep the STANDARD boundary; revisit via field-metadata
  injection or a json-column-indices ABI arg only if a box-2025/Azure target needs it (or with the DAX provider).
  (6) **tz validated (3b) — naive↔naive**: with `icu` statically embedded (`extension_config.cmake`), validated
  under a non-UTC session zone (`America/New_York`) that a `TIMESTAMPTZ` preserves its instant (stored UTC
  `datetimeoffset`, re-displayed in the session zone) and a naive `TIMESTAMP` is unshifted; no code change
  needed. The reinterpret-as-session-local semantic is deliberately NOT adopted. `test/verify_timezone.test`.
  **Warehouse read-side is now complete**; the only remaining warehouse item is write-side rich types (lossless
  flip). (`mssql_default_varchar_length` — done via the settings refactor;
  applies to all created text columns.) A `ServerProfile`
  (EngineEdition + product version + DB collation) detected at OpenCatalog drives connection behavior
  (no MARS → pooled reads + snapshot, see [docs/transactions.md](docs/transactions.md)) AND type mapping
  (no `NVARCHAR` → `VARCHAR`; no `DATETIMEOFFSET` → UTC `datetime2(6)`; `datetime2` scale ≤ 6; native
  `json` on 2025+). Collation is the principled `VARCHAR`/`NVARCHAR` driver (`_UTF8`) + gates string
  `ORDER BY` pushdown (`_BIN2`); the doc records the **no-universal-collation** cross-stack reality
  (DuckDB/SQL-endpoint = CS binary vs DAX/Vertipaq = CI). New `mssql_default_varchar_length` setting
  (length policy, separate from the varchar/nvarchar choice; existing `mssql_ctas_text_type` is the
  blunt whole-string escape hatch). Open: the naive-`datetime2` reinterpret-as-session-local semantic.

- **Settings refactor** (provider-declared, C#-accessible settings) — **design:
  [docs/settings-architecture.md](docs/settings-architecture.md)**. **Mechanism DONE (ABI v33, steps 1–3,
  behavior-preserving)**: a provider declares its settings in C# (`IBackend.Settings` → `ProviderSetting`),
  the host registers them as DuckDB extension options **at extension load** via the generic `list_settings`
  ABI (booting the bridge best-effort at load), and value changes **push** to C#'s `ProviderSettingsStore`
  via `set_setting` from a **per-slot trampoline** set-callback (`SetTrampoline<I>` — DuckDB's set-callback
  carries no setting name + fires before the store, so one generic callback can't work). The provider-agnostic
  core no longer names a setting; `RegisterCompatSettings` + the hardcoded `mssql_*` list are gone. Min-validation
  preserved (now names the setting). **`mssql_default_varchar_length` DONE** (the original motivator): declared
  in C#, read from `ProviderSettingsStore` inside `MapArrowToSqlType` (no ABI param), bounds **all** created
  text columns incl. CTAS/COPY (`NVARCHAR(n)`/`VARCHAR(n)` vs `(MAX)`); `mssql_ctas_text_type` whole-type
  override still wins (`test/verify_default_varchar_length.test`, 19). **Step 4 cutover — `ctas_text_type`
  DONE (ABI v34)**: `MapArrowToSqlType` reads `mssql_ctas_text_type` from the store; the `text_type` param is
  dropped from `create_table` across C#/ABI/C++ (proving a per-setting param can be removed); it now applies
  to CTAS/COPY too, not just explicit CREATE (closing the old gap). The C++11 trampoline array was also
  hardened (hand-rolled `IndexSeq` replacing `std::make_index_sequence`). The `isolation` entanglement is now
  resolved: the **ATTACH-options→C# refactor (ABI v37) landed** — the per-catalog `isolation_level` ATTACH
  option lives on the C# `SqlServerCatalog`, and in-out isolation resolves in C# (`mssql_isolation_level`
  setting ?? the catalog's `isolation_level`), so no global store holds a per-catalog value (see
  [docs/provider-extensibility.md](docs/provider-extensibility.md) §3). Replaces the old "hardcode `mssql_*` in
  C++ / read in C++ / pass each value
  through an ABI method param" model (O(settings × providers) churn): **net ABI reduction** — two generic
  entries replace the per-setting params. Trade-offs: boot the CLR at extension
  load (needed for `SET` before first ATTACH; aligns with Phase-3 load-time functions) + catalog/provider
  scope (not session-local). **Directly unblocks `mssql_default_varchar_length`** (C# reads the length from
  `Settings`, no `begin_bulk`/`create_table` signature changes). Prerequisite for the 2nd provider. The same
  provider-declared pattern also covers **ATTACH options** (DONE, ABI v37 — `open_catalog(options_json)`;
  C# parses `schema_filter`/`table_filter`/`isolation_level`, filtering applied in `get_metadata`,
  `PROVIDER`/`SECRET` stay C++ meta-options) and **secret fields** (DONE, ABI v38 — `list_secret_fields`;
  C# declares `SecretType`/`SecretFields`, C++ `RegisterProviderSecrets` registers the type + params
  generically, validation in `BuildConnectionString`). **All three flavors are built; the `mssql` provider is
  fully self-describing** — **design:
  [docs/provider-extensibility.md](docs/provider-extensibility.md)** (the unified "provider declares; core
  stays name-agnostic" model).

- **Plugin system (third-party backends + global functions)** — **default-context SPI BUILT + verified; ALC
  isolation deferred: [docs/plugin-system.md](docs/plugin-system.md)**. A plugin dropped into an
  **`FABRICATOR_PLUGIN_DIR`** folder is discovered at load (`BackendRegistry.ScanPluginDirectories`), its
  `IBackend`(s) registered + global functions surfaced as bare `fn(...)` with NO ATTACH — no ABI/C++ change (the
  scan runs in `Discover()` before the `list_global_functions` union). Demo `Fabricator.SamplePlugin`'s
  `plug_greet` (`test/verify_plugin.test`). **Key finding: plugins load into the BRIDGE's ALC**
  (`AssemblyLoadContext.GetLoadContext(typeof(BackendRegistry).Assembly)`), NOT `Default` — hostfxr loads the
  bridge into a non-default context, so loading into Default bound the plugin to a separate `Fabricator.Bridge`
  copy (different, non-assignable `IBackend` → 0 backends). The loader skips host-context-loaded assemblies (the
  shared set) + a `Resolving` hook probes plugin dirs for private deps. **Plugins must align their full
  dependency closure with the host (Apache.Arrow always)** — no version isolation without ALC. **The contract
  assembly `Fabricator.Abstractions` is extracted** (the `I*Function`/`IBackend`/`ITableFunctionSession`/`IAggregateSession`
  interfaces + `ProviderSetting`/`SecretField`/`TableFunctionScan`/`ScanSpec`/`FilterNode`, kept in the
  `Fabricator.Bridge` namespace — assembly split only, zero source churn; Bridge references it, the
  ABI/marshaling/`BackendRegistry`/Static-bases/adapters stay in Bridge). `Fabricator.SamplePlugin` references
  **Abstractions only** (+ Apache.Arrow) — Bridge-independent. Per-plugin `AssemblyLoadContext` isolation (for
  conflicting deps) is a deferred, non-breaking loader-internal upgrade. **Crux for that day:
  `Apache.Arrow`(+`.C`) MUST be SHARED (default context), never isolated** — every cross-boundary call traffics
  Arrow types, and cross-ALC types aren't assignable, so all plugins pin the bridge's Arrow version (isolation
  frees their OTHER deps only). The one fix over the textbook sketch: the `PluginLoadContext.Load` must return
  null for an explicit **shared-name allowlist** (`Fabricator.Abstractions` + `Apache.Arrow`/`.C`) BEFORE the
  resolver, else `AssemblyDependencyResolver` loads an isolated Arrow copy and breaks everything. Clean shape:
  extract a thin shared **`Fabricator.Abstractions`** (interfaces + Arrow-typed contracts) + non-collectible
  per-plugin ALCs, additive beside the default-context first-party providers (which gain nothing from isolation).
  Adopt isolation only when a real dependency conflict / third-party plugin lands.


### Callable scalar UDFs (4b)
- **Discovery**: `FabricatorCatalog::LoadCatalog`/`RefreshCache` call `DiscoverFunctions` (reads
  `FABRICATOR_META_FUNCTIONS`, first 3 string cols) and `AddScalarFunction(name)` for every `kind=='scalar'`
  in a matched schema. Names cached in `FabricatorSchemaEntry::scalar_functions_`; entries materialized lazily.
- **Registration**: `FabricatorSchemaEntry::LookupEntry`/`Scan` now handle `CatalogType::SCALAR_FUNCTION_ENTRY`
  → `GetOrCreateScalarFunction` fetches the param + return schemas (`FetchFunctionParamSchema` /
  `FetchFunctionReturnType`), builds a `ScalarFunction` with a **capturing-lambda** callback (no bind/
  function_info dance — `scalar_function_t` is `std::function`), and caches a `ScalarFunctionCatalogEntry`.
  Stale-on-fetch self-heals (evict → not-found), like the table path.
- **Callback**: marshals the arg `DataChunk` → Arrow (`ArrowAppender`+`ArrowProducer`), calls
  `execute_scalar`, ingests the single-column result via `ArrowStreamReader` + `VectorOperations::Copy`
  into the result `Vector`. Registered **VOLATILE** (never folded) + **SPECIAL_HANDLING** (sees NULL args —
  SQL Server semantics).
- **C# execution** (`SqlServerCatalog.ExecuteScalar`, option **B**): runs chunked, parameterized
  `SELECT [s].[f](@..) AS result UNION ALL …` — the result column inherits the UDF's return type (correctly
  typed Arrow, no hand-built array). Chunked to ≤ ~`2000/param_count` rows/query to stay under SQL Server's
  ~2100-parameter cap. Param/return schemas via typed-NULL `SELECT CAST(NULL AS <type>) …` reconstructed
  from `INFORMATION_SCHEMA.PARAMETERS` (shared `BuildSqlType` with `ColumnTypeInfo`).
- **Verified**: `db.dbo.vf_add(1,2)=3`, string returns, NULL handling (`vf_inc(NULL)=0`), NULL-in→NULL-out
  (`vf_add(NULL,2)` IS NULL; per-row null bitmap round-trips), and a 5000-row batch summing exactly
  (exercises 2048-row vectors × the C# param-limit chunking). Strict typing: a BIGINT arg to an `INTEGER`
  UDF errors (no implicit narrowing) — cast required. Committed test: `test/verify_scalar_functions.test`.

### Callable table functions (4c)
- **Scope**: inline + multi-statement **TVFs** (`kind=='table'`), output schema **static** from
  `INFORMATION_SCHEMA.ROUTINE_COLUMNS`. **Deferred**: stored procs (need `sp_describe_first_result_set`/
  `EXEC`/named params/`_OUTPUT_`).
- **Discovery + registration**: `LoadCatalog`/`RefreshCache` `AddTableFunction(name)` for every
  `kind=='table'` in a matched schema (`table_functions_`). `FabricatorSchemaEntry::LookupEntry`/`Scan` handle
  `CatalogType::TABLE_FUNCTION_ENTRY` → `GetOrCreateTableFunction` builds a `TableFunctionCatalogEntry` and
  caches it (stale-on-fetch self-heals, like the table/scalar paths).
- **Bind**: `table_function_bind_t` is a **raw fn pointer** (can't capture, unlike `scalar_function_t`), so
  the identity rides an `FabricatorTableFunctionInfo : TableFunctionInfo` on the `TableFunction`, read in the
  static bind via `input.info`. The bind (1) resolves the output schema via `get_function_output_schema`
  (zero-row → `PopulateReturnSchema`, so the TVF isn't executed just to bind), then (2) installs a capturing
  `StreamFactory` (which **is** `std::function`) that marshals the constant call args (`input.inputs`) into a
  1-row Arrow batch (`ArrowAppender`+`ArrowProducer`) and calls `execute_table`. Reuses `ArrowStreamScan`/
  `ArrowStreamInitGlobal`/`Local`.
- **Projection + filter pushdown (real, SQL-level)**: the TVF reuses the **catalog table scan's** pushdown
  machinery. `push_projection=true` on the bind_data + `projection_pushdown=true` + `pushdown_complex_filter
  = FabricatorComplexFilterPushdown` (extracted from `fabricator_table_entry.cpp` out of its anon namespace,
  declared in `fabricator_table_entry.hpp`, shared by both scans). The scan factory forwards the request's
  `spec_json`+`filter_values` to `execute_table`. So C# emits `SELECT <cols> FROM [s].[f](@a0,…) WHERE
  <filter>` — inline TVFs get inlined by SQL Server → genuine pushdown. Best-effort + never-erase (DuckDB
  re-applies every predicate), like the table scan.
- **C# execution** (`SqlServerCatalog`): `GetFunctionOutputSchema` = typed-NULL `SELECT` reconstructed from
  `INFORMATION_SCHEMA.ROUTINE_COLUMNS` (shared `BuildSqlType`); `ExecuteTable` binds the constant args as
  `@a0…` (disjoint from the filter's `@p0…`) and delegates to the shared **`ScanFromSource`** helper (also
  used by `ScanTable`), which builds the projected/filtered `SELECT … FROM <source> WHERE …` — streamed lazily.

### Function-abstraction refactor (Phase 5 — done)
A DuckDB-faithful `Bind`/`Binding` model + expressing SQL-Server functions as the authoring interfaces.
Complete: scalar wrappers, arg-dependent table output schema, in-out, the TVF/proc wrapper extraction, the
v29 table-function session, and the v30 removal of the dead `execute_table`/`execute_proc`.
- **Scalar — DONE** (`refactor(scalar)`, commit `60ea6f0`): discovered SQL UDFs are a `SqlServerScalarFunction
  : ICatalogScalarFunction` (in `SqlServerScalarFunction.cs`) — `ResolveScalar` returns a custom registry entry
  or the wrapper as the base `IScalarFunction`, so `ExecuteScalar`/param/return-schema dispatch through ONE path. The
  chunked `SELECT [s].[f](@..) UNION ALL` (≤2100-param cap) moved into the wrapper's single-batch `Invoke`; the
  per-cap sub-queries merge into one column via a typed builder (no `ArrowArrayConcatenator`). C#-only, no ABI.
- **Table `Bind` — DONE** (`feat(table)`, commit `85de4df`, **ABI v27**): `ITableFunction.Bind(RecordBatch
  args) → ITableFunctionBinding { OutputSchema; SupportsPushdown; Execute(TableFunctionScan) }` — a custom
  TVF's **output schema may depend on its constant args** (the gap before: a static `OutputSchema` property).
  `get_function_output_schema` gained a nullable `args` 1-row stream (the C++ table bind marshals the args once
  for both the output-schema resolution and the scan; the in-out `_each` base lookup passes null). A
  `StaticTableFunction` base keeps fixed-schema functions trivial (`cf_range`). Demo `dbo.cf_columns(n)` returns
  `n` columns `c1..cn` — schema resolved from the arg at bind (`verify_custom_functions.test`).
- **Discovered TVF/proc wrapper extraction — DONE** (`SqlServerProcedure.cs` `6da8033`,
  `SqlServerTableValuedFunction.cs` `eb6c34e`): the inline TVF/proc SQL (the `ExecuteTable`/`ExecuteProc`/
  `GetFunctionOutputSchema` branches + `FunctionOutputColumns`/`ProcResultColumns`/`ProcOutputParams`/
  `ScanFromSource`, the last four widened to `internal`) moved into top-level `internal` wrappers, leaving
  `ExecuteTable` + `GetFunctionOutputSchema` as **thin custom/TVF/proc dispatchers**. `SqlServerProcedure :
  ITableFunction` (proc EXEC has no pushdown → full batches match the bind-time `OutputSchema`, so the
  `IAsyncEnumerable`/`AsyncEnumerableArrowStream` shape is correct); but `SqlServerTableValuedFunction` is a
  **bespoke** wrapper (`OutputSchema` property + `ExecuteScan` → the `ScanFromSource` stream **returned
  directly**) — a pushdown source is stream-native, its stream schema IS the PROJECTED schema (matching the
  projected batches; re-wrapping it with the full schema crashed `arrow_ingest` — SIGSEGV on a column-subset
  projection `SELECT sq FROM tf_ms(4)`). C#-only (`execute_table`/`execute_proc` unchanged). Verified: full
  function suite green.
- **Table-function session — DONE** (ABI v29: `c2e452f` surface + `1f9fe96` C++ rewire): `tablefn_bind`
  (resolve a per-plan binding → output schema/return types + `supports_pushdown` + an opaque handle) /
  `tablefn_execute` (run the scan, per execution) / `tablefn_close` (free the binding at plan teardown), the
  session-handle successor to `get_function_output_schema`+`execute_table`/`execute_proc` in the table scan.
  C++ `FabricatorTableFunctionBind` uses them; the `is_proc` **execute** branch is gone (`tablefn_execute`
  unifies TVF/proc/custom — C# `SqlServerCatalog.TableFnBind` classifies + returns an `ITableFunctionSession`:
  `TvfTableFunctionSession` (SQL pushdown) or `TableFunctionBindingAdapter` (proc positional / custom by-name)). `push_projection`
  = the binding's `supports_pushdown` (= `!is_proc`, behavior-preserving; `is_proc` survives only for the
  named-vs-positional arg marshaling). The binding is **reused across (prepared) re-executions** — proven by
  a `PREPARE`/`EXECUTE`-twice test (R2); `SqlServerTableValuedFunction.ExecuteScan` no longer consumes its
  args, and the per-execution connection lives in `tablefn_execute`'s stream (released by the arrow scan), so
  the refcounted `TableFnBindState` only frees binding metadata at teardown (no `arrow_ingest` hook needed).
- **`execute_table`/`execute_proc` removed — DONE** (ABI v30, `8e2a194`): unused in C++ since v29, so the 2
  vtable entries + their `clr_host` wrappers + the Bootstrap handlers/assignments + the `Abi.cs` delegates +
  the `IBackendCatalog`/`StubBackend`/`SqlServerCatalog` methods are gone. A **mid-struct** removal (shifts
  every later entry's offset), so `abi.h` + `Abi.cs` field order stay in exact sync; the function suite is
  the alignment gate. **Optional remaining**: fold the bespoke `SqlServerTableValuedFunction` into
  `ITableFunction` now that `tablefn_execute` returns a stream (organizational — the dispatch is already
  unified under `ITableFunctionSession`). Full design: the plan file's "Phase 5".
- **Verified**: inline TVF (`tf_nums(3)`→1,2,3), multi-column (`tf_pair(7,'hi')`), multi-statement
  (`tf_ms`→squares), and aggregation over a TVF. **Pushdown proven via the plan cache**: the statement that
  reached SQL Server was `SELECT [id],[name],[salary] FROM [dbo].[tf_emp](@a0) WHERE [id] <> @p0` (column
  list, not `*`; parameterized `WHERE`). Committed test: `test/verify_table_functions.test` (incl. a
  `dm_exec_query_stats` proof that `FROM [dbo].[tf_ms] … WHERE …` reached the server).
### Callable stored procedures (4d)
- **Scope**: procs resolved as table functions (`SELECT * FROM db.schema.proc(name := val)`) with **named
  parameters** (4d-2); a proc returns either its **first result set** OR, if it has **OUTPUT params**, those
  outputs + the integer RETURN value as flat columns (4d-3). **Deferred**: multiple result sets, INPUT/OUTPUT
  (`INOUT` with a supplied value), the `_OUTPUT_`-struct broadcast-over-a-result-set shape (a proc with both
  outputs AND a result set is treated as outputs-only — per the observation that the combo doesn't occur).
- **OUTPUT params (4d-3, C#-only)**: input params are `PARAMETER_MODE='IN'` (functions are always IN, so
  this is a no-op there; for a proc it excludes OUTPUT params, mode `'INOUT'`, from the named inputs).
  `GetFunctionOutputSchema`: if a proc has OUTPUT params (`ProcOutputParams`) → output schema = those columns
  + a `return_value INT` (typed-NULL SELECT, **flat — no struct**); else its first result set (`sp_describe`).
  `ExecuteProc`: with OUTPUT params, runs a `DECLARE @o …, @_rv int; EXEC @_rv = [s].[p] @in=@p0,
  @o=@o OUTPUT; SELECT @o AS [o], @_rv AS [return_value];` batch — the final SELECT returns the captured
  outputs as a normal 1-row result set (no `Direction=Output` timing caveat, no buffering); the proc's own
  result set is ignored.
- **Named/optional params (4d-2)**: procs register their params as DuckDB **named parameters**
  (`tf.named_parameters[name]=type`, empty positional `arguments`) — mirrors `EXEC @name=val`. The bind
  gathers only the **supplied** `input.named_parameters` (each cast to its declared type) into the 1-row
  args batch whose **field names = the parameter names**; C# `ExecuteProc` builds `EXEC [s].[p] @name=@p0,…`
  from those field names. Omitting a param → it's absent from the EXEC → SQL Server uses the proc's own
  `DEFAULT` (so **optional params work for free**, no `has_default_value` discovery needed); a required
  param omitted → SQL Server errors. (TVFs stay **positional** — `input.inputs`.)
- **Unified with TVFs**: `table_functions_` is a `name -> is_proc` map; discovery routes `kind=='proc'`
  → `AddTableFunction(name, true)`. Procs reuse the **same** `TableFunctionCatalogEntry` registration +
  static bind via an `is_proc` flag on `FabricatorTableFunctionInfo`. Proc branch: factory calls
  `execute_proc` (not `execute_table`), `push_projection=false`, and **no** `pushdown_complex_filter` — a
  proc's `EXEC` isn't inline-wrappable, so DuckDB projects + filters locally.
- **Output schema** (`SqlServerCatalog.GetFunctionOutputSchema`): TVFs use `INFORMATION_SCHEMA.ROUTINE_COLUMNS`;
  a proc ⇒ OUTPUT params (`ProcOutputParams`) + `return_value` if any, else its first result set via
  **`sys.sp_describe_first_result_set`** (over `EXEC [s].[p] @a=@a,…` with the input params declared NULL;
  `system_type_name` used directly). **Uses the sp, NOT the `dm_exec_describe_first_result_set_for_object`
  DMV** — Fabric Warehouse doesn't support that DMV (error 15871) but supports the sp; the sp works on box
  too, so one path serves both (Fabric-validated 2026-06-24). Auto-routes by object kind. Empty ⇒ "no
  describable result set".
- **Execution** (`ExecuteProc`): no-output proc ⇒ `EXEC [s].[p] @name=@p0,…` (streams the first result set);
  output proc ⇒ the `DECLARE/EXEC OUTPUT/SELECT` batch above. Input param types come from
  `INFORMATION_SCHEMA.PARAMETERS` (reused `get_function_param_schema`, whose field names are the de-@'d names).
- **Verified**: `usp_sc(minSalary := 200)` → rows; local projection+filter; aggregation; **optional param**
  omitted → proc `DEFAULT` (`usp_opt(base:=10)`→60) and supplied → override (`…, bonus:=5`→15); **OUTPUT
  params** flat (`usp_outp(a:=10,b:=3)` → `sum=13, diff=7, return_value=42`). Committed test:
  `test/verify_stored_procs.test`.

### Custom (provider-authored) functions (4e scalar, 4f table)
- Beyond functions *discovered* from SQL Server, a provider can **author custom functions in C#**:
  - **4e scalar** — `ICatalogScalarFunction` (Bridge, derives the base `IScalarFunction`) = `SchemaName`/`Name`/`Parameters`(arg fields)/
    `Result`(field)/`Invoke(RecordBatch)→IArrowArray`. Demo `CustomFunctions.Scalar`: `dbo.cf_add(a,b)=a+b`.
  - **4f table** — `ITableFunction` (Bridge) = `SchemaName`/`Name`/`Parameters` + `Bind(args) →
    ITableFunctionBinding` (`OutputSchema` + `IAsyncEnumerable<RecordBatch> Execute(scan, ct)` — args = the
    1-row positional call args; yields result batches **async/lazily**, streamed to the host via
    `AsyncEnumerableArrowStream`). Fixed-schema functions derive from `StaticTableFunction` (override
    `OutputSchema` + `IAsyncEnumerable<RecordBatch> Invoke(args, ct)`). Demos `CustomFunctions.Table`:
    `dbo.cf_range(n)` → `(value, squared)` (StaticTableFunction) + `dbo.cf_columns(n)` (output schema from the
    arg, via Bind).
- **Reuses the entire catalog scalar/TVF path — C#-only, no ABI/C++ change** (the lean alternative to
  load-time global functions): `GetMetadata(Functions)` appends the custom functions to `FunctionsSql` via
  `UNION ALL` (`kind='scalar'`/`'table'`), so the existing C++ discovery registers them as a
  `ScalarFunctionCatalogEntry` / `TableFunctionCatalogEntry` exactly like a discovered function. The catalog
  consults `CustomScalar`/`CustomTable` registries (keyed `schema.name`, case-insensitive) **first**:
  `GetFunctionParamSchema` (both), `GetFunctionReturnSchema`/`ExecuteScalar` (scalar), `GetFunctionOutputSchema`/
  `ExecuteTable` (table) → declared schema / run the C# `Invoke`; otherwise the SQL path. A custom function
  shadows a same-named SQL object (custom wins).
- **Custom table functions get no SQL-level pushdown** (there's no SQL to push into): `ExecuteTable` ignores
  `spec_json`/`filter_values` and returns the full result; `push_projection=true` is still set (the TVF path),
  so `arrow_ingest`'s `BuildProjectionMapping` selects the projected columns **by name** from the full result
  (extra columns ignored), and DuckDB re-applies filters above the scan. Args are **positional** (TVF-style).
- **Attach-time + catalog-bound** (`db.schema.fn`), not connection-free globals — chosen over load-time
  global functions because it avoids booting the CLR at `Extension::Load()` and needs no new ABI. (Load-time
  global via `loader.RegisterFunction` remains an option if connection-free functions are ever needed; the
  same `IScalarFunction` authoring + the existing `execute_scalar` with a handle-less marker would reuse
  this path.)
- **Verified**: scalar `db.dbo.cf_add(2,3)=5`, vectorized, NULL→NULL; table `cf_range(3)`→`(1,1),(2,4),(3,9)`
  with projection (`squared` only) + filter (`value>1`) + aggregation; both discovered (`scalar`/`table`)
  with **no SQL object** (`sys.objects` count 0). Committed test: `test/verify_custom_functions.test`.

### Callable table-in-out (4g)
> **Fully superseded by the streaming exchange (Phase 6) — this push/materialize model is RETIRED.** The
> per-chunk materializing model described here (a C# `_ready` buffer + a C# lock, over
> `inout_open`/`push`/`finish`/`abort`) served custom in-out + discovered TVFs (moved to the exchange in
> Phase 6) and finally stored procs (`SqlServerProcEach`, moved in `9056eae`). The C# push sessions are gone;
> the C++ push operator + ABI are dead-in-place. These 4g notes remain the reference for the per-row CROSS
> APPLY / proc-EXEC **semantics** + the echo output schema (unchanged on the exchange); only the transport
> changed (push/materialize → gate + two pull streams). See "Streaming table-in-out exchange (Phase 6)".
- **Surface**: a discovered TVF `db.s.tf` ALSO gets a sibling `db.s.tf_each(<input table>)` (DuckDB
  forbids a TABLE-param overload sharing the bare name — see the §91 sequencing note). `tf_each(<table>)`
  applies `tf` **once per input row** via SQL-Server **`CROSS APPLY`** (T-SQL generated + run in C#),
  output = the echoed input columns (typed as `tf`'s **parameters** — C# CASTs the VALUES to them) ++
  `tf`'s output columns. Read-only (a TVF can't modify data). The input table's columns map **positionally**
  to `tf`'s params.
- **Registration** (`fabricator_schema_entry.cpp`): `AddTableFunction(name,false)` also registers
  `inout_functions_["<name>_each"] = name`; `GetOrCreateTableFunction` resolves the alias via
  `GetOrCreateInOutFunction` → a single `{LogicalType::TABLE}` `TableFunction` (`in_out_function` only,
  `function_info.func` = the **base** TVF). A real same-named `_each` function wins (matched first). `Scan`
  lists the aliases so they're discoverable.
- **Operator** (all in the anon namespace of `fabricator_schema_entry.cpp`): `FabricatorInOutBind` (output
  schema, no execution) / `…InitGlobal` (`ToArrowSchema` + `inout_open` into the holder) / `…InitLocal`
  (trivial) / `…Function` (`inout_push` → the chunk's **full** output, drained across `HAVE_MORE_OUTPUT`,
  then `NEED_MORE_INPUT`). **Synchronous per chunk → no tail → no `in_out_function_final`, no counter.**
  Session lives in `InOutSessionHolder` (refcounted, on the bind data); its **RAII destructor → `inout_abort`**
  is the release/rollback backstop on every teardown path (also frees the GCHandle). `OperatorFinalize`
  reserved for the per-row-proc COMMIT (it can't emit rows). See design §11.1.
- **C#** (`SqlServerCatalog.InOutSessionImpl`): synchronous — `Push` runs `RunCrossApply` inline
  (lock-serialized for parallel branches) + stashes the chunk's output, `DrainReady` returns it, `Finish`
  drains leftovers (no tail), `Abort` releases. `RunCrossApply` builds
  `SELECT p.*, f.* FROM (VALUES (CAST(@.. AS <paramtype>),…)) p(cols) CROSS APPLY [s].[tf](p.cols) f`,
  sub-chunked under the ~2100-param cap. A CROSS APPLY error throws out of `Push`/`inout_push` → fails the query.
- **Isolation / consistent view**: the in-out session opens ONE SQL transaction (ADO.NET `SqlTransaction`,
  MARS-compatible) wrapping all its per-chunk CROSS APPLY queries, at a configurable isolation level — so a
  call sees one consistent snapshot even if another process modifies the data between chunks. Level from the
  ATTACH `isolation_level` option (per-catalog default, `FabricatorCatalog::isolation_level_`) overridable by
  `SET mssql_isolation_level` (resolved in C++ `ResolveInOutIsolation`, passed via `inout_open`'s v24
  `isolation` arg). `SqlServerCatalog.BeginInOutScope` maps it (`read uncommitted`/`read committed`/
  `repeatable read`/`serializable`/`snapshot`; snapshot needs `ALLOW_SNAPSHOT_ISOLATION ON`); `Finish`
  commits, `Abort`/destructor rolls back. (Custom C# in-out functions run in C#, so isolation doesn't apply.)
  Verified: `test/verify_inout_isolation.test` (a TVF reporting `transaction_isolation_level`, run via
  `_each`, shows the configured level; ATTACH default + SET override + unknown-name error).
- **Verified**: `test/verify_table_inout.test` (63 assertions) — scalar-arg regression, single-chunk,
  parallel `UNION ALL` (coherent), `WHERE`, `ORDER BY`+`LIMIT`, aggregate, empty, multi-column, large
  50-row `BIGINT`→`INT` (sub-chunking + type round-trip), error mid-stream + recovery.
- **Custom C#-authored in-out (4g-custom)** — *unified in Phase 6: the per-chunk `Process` shape below is now
  the `StaticInOutFunction` base under the single `IInOutFunction`, and runs on the streaming exchange
  (`InOutBind`), not the push `CustomInOutSessionImpl`/`InOutOpen` described here. See "Streaming table-in-out
  exchange (Phase 6)". The per-chunk semantics are unchanged; the original push wiring below is historical.*
  Original (push) design: `IArrowTableInOutFunction` (Bridge) =
  `SchemaName`/`Name`/`InputSchema`/`OutputSchema` + `IEnumerable<RecordBatch> Process(chunk)` (the in-out
  analog of 4e `ICatalogScalarFunction` / 4f `ITableFunction`). A pure-C# **per-chunk streaming**
  table-in-out (no SQL object) that may keep mutable state across chunks (running aggregate — `Process` is
  invoked serially per session) and declares its **full** output (no input echo). There is no emit-at-end
  hook (a whole-table aggregate is a pipeline breaker, not a streaming in-out). Surfaced via
  `FunctionsMetadataSql` as `kind='inout'`; C++ `AddInOutFunction` registers a bare-name `{TABLE}` entry
  (`GetOrCreateCustomInOutFunction` + `FabricatorCustomInOutBind`, reusing the 4g operator callbacks — no new
  ABI). C# `CustomInOut` is a **factory** registry (fresh
  instance per session so state can't leak across queries); `InOutOpen` dispatches to `CustomInOutSessionImpl`
  (runs `Process` per push) ahead of the CROSS APPLY path. Demos `CustomFunctions.InOut`: `dbo.cf_tag`
  (per-row `(n, n*n)`) + `dbo.cf_running_sum` (stateful cumulative sum, emitted per row). Verified in
  `test/verify_custom_functions.test`.
- **Per-row stored procs (4g-proc → now on the exchange, `9056eae`)**: a discovered proc also gets `_each`
  (C++ `AddTableFunction` registers the alias for procs too; a proc can't be inline-CROSS-APPLY'd, so it's
  EXEC'd per input row). **Now on the streaming exchange** (`SqlServerProcEach : IInOutFunctionBinding`, resolved
  by `InOutBind` — proc vs TVF classified by ROUTINE_COLUMNS): `DoExchange` runs, per input row, `DECLARE @t
  TABLE(<proc result>); INSERT @t EXEC [s].[p] @param=@p,…; SELECT <echoed input>, t.* FROM @t;` on **DuckDB's
  pinned connection/`_txn`** (`BeginWrite`) — echo is server-side (output = input cols ++ proc result cols),
  result-set procs only. It does **not** commit/dispose the pinned scope, so the proc's writes commit/roll
  back with **DuckDB's** transaction (autocommit + explicit `BEGIN`); the gate (`MaxThreads=1`) serializes the
  EXECs on the pinned connection. (Was the 4g push `ProcInOutSessionImpl`, retired in `9056eae`.) Verified:
  `test/verify_proc_inout.test`.
- **OperatorFinalize cleanup signal (4g-finalize)**: an `OptimizerExtension` (`RegisterFabricatorInOutFinalizer`,
  registered at load) wraps each in-out `LogicalGet` (identified by `function.in_out_function ==
  FabricatorInOutFunction`, RTTI-free) in a pass-through `LogicalExtensionOperator`; its `PhysicalOperator`
  (`PhysicalOperatorType::EXTENSION`) forwards rows 1:1 (`Execute = chunk.Reference(input)`) and, in
  `OperatorFinalize`, calls `holder->Finish()` → C# `inout_finish`. Fires **once**, sink-level, even above a
  parallel UNION (unlike per-branch `in_out_function_final`) — a reliable C# resource-cleanup hook + the clean
  commit of a read-only TVF's snapshot transaction (NOT the proc commit — DuckDB drives that). Transparent to
  data flow (`GetColumnBindings`/`ResolveTypes` delegate to the child); the holder destructor's `inout_abort`
  stays the LIMIT/error backstop (idempotent via `_scopeClosed`).

### Streaming table-in-out exchange (Phase 6, ABI v28)
The streaming successor to the 4g push/materialize in-out model, for **ALL `_each` forms — custom C# in-out,
discovered TVFs, AND stored procs** (procs unified onto the exchange in `9056eae`; the 4g push path is now
fully retired). No per-chunk materialization: output streams via two pull-based Arrow streams coordinated by
a C++ "gate" mutex; the lock moved C#→C++. Commits `ca111e7` (ABI), `49f9a1d` (operator + custom), `330d2c7`
(discovered TVF), `9056eae` (proc). Design + the 6.0 spike: the plan file's "Phase 6".
- **Three bindings, one `DoExchange` shape** (resolved by `InOutBind`, which classifies custom / TVF / proc):
  a custom C# in-out (`IInOutFunction`); a discovered TVF `_each` (`SqlServerTvfEach` — per-row CROSS
  APPLY on its **own read-only** connection at the configured isolation); a stored-proc `_each`
  (`SqlServerProcEach` — per-row `EXEC` on **DuckDB's pinned write** connection (`BeginWrite`), no commit/
  dispose, so the proc's writes commit/roll back with DuckDB's COMMIT/ROLLBACK). The gate (`MaxThreads=1`)
  serializes the proc EXECs on the pinned connection; the transactional contract (autocommit / explicit-BEGIN
  read-your-writes + ROLLBACK) holds — verified by `verify_proc_inout`. The 4g push operator (`FabricatorInOut*`)
  + the `inout_open`/`push`/`finish`/`abort` ABI + `IInOutSession`/`InOutOpen` were **removed at ABI v31**
  (`49e6d94`); the exchange is the only in-out path.
- **Author API** (`IInOutFunctionBinding`, Bridge): `Schema OutputSchema` + `IAsyncEnumerable<RecordBatch>
  DoExchange(IAsyncEnumerable<RecordBatch> input, ct)`. `input` yields one batch per DuckDB input chunk; the
  returned enumerable maps to the operator contract — non-empty = HAVE_MORE_OUTPUT, **length-0 = the
  per-input sentinel (NEED_MORE_INPUT)**, end-of-enumerable = FINISHED. **The author yields the sentinel** (the
  decision after the 6.0 spike — a free-form `DoExchange` is incompatible with the single-slot gate unless the
  author delimits per chunk; the framework can't inject it without deadlocking). `IInOutIsolation` is the
  optional SQL-isolation hook (the framework sets it before `DoExchange`; pure-C# bindings ignore it).
- **ABI v28** (`abi.h`): `inout_bind(handle, schema, func, args, input_schema, out_schema, out_binding)` resolves
  the FULL output schema (input echo ++ the function's own columns) in C# + returns a binding handle (reused
  across prepared re-executions, freed via `inout_bind_close`); `inout_exchange_open(binding, input, isolation,
  output)` runs one execution — the host exports the INPUT stream (its get_next hands the gate-holder's one
  chunk to C#), C# exports the OUTPUT stream the host pulls. (The 4g `inout_open`/`push`/`finish`/`abort`
  were removed at ABI v31 once procs joined the exchange.)
- **C# pump** (`InOutExchange.cs`, Bridge): `InOutExchangeStream` exposes `DoExchange` as a pull-based
  `IArrowArrayStream` — the Arrow C-stream exporter blocks on `ReadNextRecordBatchAsync` (sync-over-async; the
  hostfxr CLR has no `SynchronizationContext`, so `GetResult` can't deadlock — proven by the 6.0 spike).
  Custom authors implement **one** interface, `IInOutFunction.Bind(args,inputSchema) →
  IInOutFunctionBinding` (registry `CustomInOut`, resolved by `InOutBind`): the author writes `DoExchange` —
  reads the input stream, yields output batches, and yields a length-0 sentinel after each input chunk, with
  cross-chunk state in `DoExchange` locals (a fresh enumerator runs per exchange, so state never leaks across
  re-executions). For a FIXED output schema, derive from the convenience base **`StaticInOutFunction`**
  (override `OutputSchema` + `DoExchange`; the base supplies the `Bind`→binding wiring) — it is to
  `IInOutFunction` what `StaticTableFunction` is to `ITableFunction`. Demos `cf_tag` (stateless),
  `cf_running_sum` (cumulative-sum local), `cf_exchange` (row-index local) all use it. `SqlServerTvfEach`
  (`SqlServerTvfEach.cs`) runs the per-row CROSS APPLY inside `DoExchange` on one pinned connection +
  transaction (the streaming successor to the deleted `InOutSessionImpl`). `InOutExchange.EmptyBatch` builds
  the length-0 sentinel matching the output schema.
- **C++ operator** (`fabricator_schema_entry.cpp`, anon ns): `FabricatorExchange{Bind,InitGlobal,InitLocal,Function}`
  + `FabricatorExchangeGlobalState` (the gate `std::mutex`, the single input `slot`, `input_eof`, the output
  reader, `MaxThreads()=1`) + a host-side input stream whose get_next hands the gate-holder's slot to C#.
  `Execute` holds the gate across the chunk's HAVE_MORE_OUTPUT cycle (ownership in the per-thread local state),
  releases it on the sentinel/EOF **or on a thrown managed error** (RAII-style — never leaks). `ArrowStreamReader`
  gained **sentinel-aware** `Pull()`/`HasPending()`/`Drain()` (its `Read()` skips empty batches, so the sentinel
  needs explicit length inspection + <=STANDARD_VECTOR_SIZE slicing). **EOF is the injected `OperatorFinalize`**
  (`FabricatorExchangeFinalizePhysical`, parallel to the 4g one): once, sink-level, after all branches it sets
  `input_eof` + drains the output to terminal-null so the managed `DoExchange` finishes + disposes — NOT a
  producer counter (the rejected premature-finish design). `ExchangeHolder` (refcounted on the bind data) frees
  the binding once. Registration: custom in-out (`GetOrCreateCustomInOutFunction`) + **every** `_each`
  (`GetOrCreateInOutFunction`) use the exchange callbacks (proc `_each` moved here in `9056eae` — no
  base-is-proc branch); the managed `InOutBind` classifies custom / TVF / proc and returns the matching
  binding (`SqlServerTvfEach` read-only conn / `SqlServerProcEach` DuckDB's pinned write conn).
- **Verified**: `verify_custom_functions` (73 — incl. parallel UNION ALL + a `threads=1` sequential-union case,
  the schedule a premature-finish bug would drop rows on); `verify_table_inout` (63, TVF now on the exchange);
  `verify_inout_isolation` (17, via `IInOutIsolation`); `verify_proc_inout` (31, push, unregressed). Full
  suite 20/20.

### Callable aggregate functions (4h — custom C# UDAF)
- **Scope**: provider-authored aggregates in C# (there are no SQL Server aggregates to discover), attach-time +
  catalog-bound (`db.dbo.cf_agg(x)`), usable in `GROUP BY`, parallel aggregation, and window (`OVER`) contexts.
  Authored via Bridge `IAggregateFunction` (`SchemaName`/`Name`/`Parameters`/`Result`/`CreateState()`) +
  `IAggregateState` (`Update(RecordBatch)`/`Combine(other)`/`object? Finalize()`). Demos
  `CustomFunctions.Aggregate`: `dbo.cf_product` (numeric product) + `dbo.cf_bit_or` (bitwise OR fold).
- **State model (the crux)**: DuckDB's aggregate model is *state-vectorized* — it owns a contiguous array of
  fixed-size state blobs and drives reduction through `state_size`/`initialize`/`update`/`simple_update`/
  `combine`/`finalize`/`destructor` callbacks. We keep each **blob as a mere `int64` id** (`state_size=8`);
  the real per-group accumulator lives in **C#** behind that id (a `ConcurrentDictionary<id, accumulator>` per
  bound aggregate). `initialize` writes `id = counter.fetch_add(1)` from a `std::atomic<int64_t>` on the
  function's `function_info` → **monotonic ids never collide** across threads or prepared-statement
  re-executions, so correctness needs no destructor. (State-in-C# was the user's call: matches the existing
  handle-based architecture, no per-call (de)serialization; the trade-off is that the managed map isn't visible
  to DuckDB's memory manager → no disk-spill for billions of distinct keys.)
- **Session**: opened in the aggregate `bind` (a `FunctionData` = `FabricatorAggregateBindData` holding a
  refcounted `AggSessionHolder`; its destructor calls `agg_close`). `bind` runs once per bound plan; update/
  combine/finalize/destructor reach the session via `AggregateInputData.bind_data`. Identity (handle/schema/
  func) + the counter ride on `FabricatorAggregateFunctionInfo : AggregateFunctionInfo` (reachable from
  `initialize` via `function.function_info`, which has no `bind_data`).
- **Two correctness rules from the DuckDB source** (both verified): (1) **read state pointers via
  `UnifiedVectorFormat`, never `FlatVector::GetData<data_ptr_t>`** — the ungrouped path passes a **CONSTANT**
  state vector to `finalize`/`simple_update`; (2) implement **both** `update` (grouped, FLAT state vector) and
  `simple_update` (ungrouped fast path, single `data_ptr_t` → one id, no C# grouping). Threading: each id is
  touched by one thread at a time (DuckDB's per-thread local hash tables; combine is partition-disjoint), so a
  `ConcurrentDictionary` suffices with **no** per-accumulator lock. Empty group: an init'd-but-never-updated id
  is absent from the map → finalize makes a fresh accumulator → its `Finalize()` is the empty value (NULL).
- **Window (OVER) needs no custom `window` callback**: DuckDB drives windowing through our update/combine/
  finalize via `WindowSegmentTree` (chosen at `window_aggregate_function.cpp`), which batches updates + does
  O(log n) combines per output row. A custom `window` callback would cost **one C++↔C# crossing per output
  row** — strictly worse for a marshaled bridge — so we deliberately don't implement it. The window paths churn
  many transient states, so the **destructor IS wired** (`agg_destroy`) to bound the C# map; `agg_close` is the
  backstop.
- **C++** (`fabricator_schema_entry.cpp`, anon ns): `FabricatorAggregate{StateSize,Init,Bind,Update,SimpleUpdate,
  Combine,Finalize,Destroy}` static callbacks marshal `[id ++ inputs]` / `[target_id, source_id]` / `[id]`
  Arrow batches (`ArrowAppender` + `fabricator::Agg*`); finalize ingests the single result column via
  `ArrowStreamReader` + `VectorOperations::Copy`. The aggregate callbacks get **no `ClientContext`** (unlike
  scalar/table execution), so the connection context + client properties are captured at `bind`.
  `GetOrCreateAggregateFunction` mirrors `GetOrCreateScalarFunction` → an `AggregateFunctionCatalogEntry`
  (`AggregateFunctionSet` of one). **Resolution**: DuckDB stores scalar/aggregate/macro in one namespace and
  the binder looks up `SCALAR_FUNCTION_ENTRY` then dispatches on the returned entry's actual type, so
  `LookupEntry(SCALAR_FUNCTION_ENTRY)` **falls back to the aggregate** (plus an explicit `AGGREGATE_FUNCTION_ENTRY`
  branch). Discovery routes `kind=='aggregate'` → `AddAggregateFunction`.
- **Opt-in disk-spill (`SupportsSpill`, ABI v26)**: by default the state lives in C# (fast, bounded by managed
  memory, no spill). A provider can instead set `IAggregateFunction.SupportsSpill=true` (+ implement
  `IAggregateState.Serialize()`/`Load()`) → **bytes-in-blob mode**: the per-group state is serialized into
  DuckDB's fixed, pointer-free state blob (`[uint32 len][byte data[FABRICATOR_AGG_SPILL_CAP=1 KB]]`) so DuckDB's
  external GROUP BY spills it to disk under memory pressure. Surfaced as `kind='aggregate_spill'`; `state_size`/
  `initialize` (sentinel len, no C# call) and update/combine/finalize/destroy all branch on the `spillable`
  flag (on `function_info` for the first two, `bind_data` for the rest). The spill callbacks marshal state as
  Arrow BLOB columns over the v26 `agg_*_spill` ABI; C# is **stateless per call** (rehydrate→apply→reserialize)
  so the destructor is a no-op (the blob owns the state). **Two correctness points found in the build:** (1)
  update + combine assign **dense group/target slots** so a group whose rows interleave (update) OR a target
  combined with several sources in one batch (the **window** segment-tree merges many nodes into one frame
  state) accumulates into one transient accumulator — a naive read-once/write-once loses merges (caught by the
  windowed-spill test); (2) serialized state must fit the 1 KB cap (enforced on write-back) → spill suits
  fixed/small state (sum/product/bitwise/avg/moments), not unbounded state (string concat). Demo
  `dbo.cf_sum_spill`.
- **Non-additive (holistic) aggregates** work in the fast mode with **no special support**: the accumulator's
  state IS the collected values (`Update` collects, **`Combine` merges the two collections** — concatenation,
  not arithmetic, the same way DuckDB's own `median`/`list`/`mode` combine — and `Finalize` computes over the
  union). Correct under parallel partial-state merging for order-independent aggregates. `SupportsSpill` stays
  false (an unbounded collection can't fit the blob cap). Demo `dbo.cf_median` (collect → sort → middle). The
  author must **copy values out in `Update`** (the batch's Arrow buffers are freed after it returns; don't
  retain the `RecordBatch`).
- **Verified**: `test/verify_custom_aggregates.test` (58 assertions) — discovery (`kind='aggregate'` +
  `'aggregate_spill'`) + no SQL object, ungrouped (`simple_update`), implicit INTEGER→BIGINT, NULL-skip,
  empty/all-NULL → NULL, `GROUP BY` (incl. a NULL-only group), parallel `bit_or` cross-checked vs DuckDB's
  built-in (threads=4), grouped+parallel `product` cross-checked vs DuckDB's `product()`, running-frame `OVER`,
  windowed `OVER`/`PARTITION BY` cross-checked vs the built-in; the spillable `cf_sum_spill` across ungrouped /
  `GROUP BY` / high-cardinality (50k rows × 1000 groups) / low-`memory_limit` (80k × 4000) / window — each
  cross-checked vs `sum()`; AND the holistic `cf_median` (odd/even/NULL/empty + a 5000-row × 50-group parallel
  cross-check vs DuckDB's `median()`, proving combine-as-merge under parallel aggregation).


- **`function_filter` ATTACH option — DONE (2026-07-15).** `ATTACH … (TYPE fabricator, function_filter
  '^ff_keep$')` — an icase regex on the routine NAME gates which discovered scalar UDFs / TVFs / procs register
  (symmetric with `table_filter`, which is table-only; `schema_filter` still gates functions by schema C++-side).
  Applied in C# `SqlServerCatalog.FilteredFunctions` (a type-preserving row filter over the Functions discovery
  stream — the diagnostic `fabricator_functions` keeps `param_count` as INT; `DiscoverFunctions` C++-side only
  reads the 3 string cols so it's tolerant). Parsed like schema_filter/table_filter (v37 ATTACH-options JSON, no
  ABI change). `test/verify_function_filter.test` (15 — exact-anchor keeps one/drops the other, substring matches
  both, cleanup); function suites unregressed (functions 13 / scalar 26 / procs 24 / table 33 / custom 89 /
  catalog_filter 7).
- **Open design items (refresh)** — deliberated, not yet built:
  - **Scoped refresh — DONE (2026-07-15).** `fabricator_invalidate_cache(catalog, name_regex)` — a non-empty
    2nd arg is an icase name pattern: SCOPED invalidation of only the MATCHING objects (drop their materialized
    entries; an ALTER'd one re-fetches its fresh schema on next access, a DROPped one self-heals when the
    column re-fetch fails), leaving the rest of the cache warm — "refresh only what I touched via
    fabricator_exec". No regex (arity 1, or an empty/NULL 2nd arg) = full `RefreshCache` (whole catalog within
    its filtered enumeration baseline). C++ `FabricatorCatalog::InvalidateMatching(pattern)` compiles the
    std::regex (bad pattern → clean error) + per-schema `FabricatorSchemaEntry::InvalidateMatching(pred)` evicts
    the matching *_entries_ caches (keeps the name lists). No ABI/C# change. `test/verify_invalidate_scoped.test`
    (18). The legacy `(catalog, schema, table)` arity still works (3rd arg ignored). The `fabricator_exec`
    auto-refresh (gated by `mssql_exec_invalidate_cache`) stays a **full** `RefreshCache` — the C# DDL detector
    returns only a bool `schema_may_change` (no object name crosses the ABI), so it can't auto-scope; do a
    manual `fabricator_invalidate_cache(cat, '<regex>')` for scoped, or full otherwise.
  - **ATTACH object filter is ENUMERATION-ONLY, not a cage — DONE (2026-07-15).** `schema_filter`/`table_filter`/
    `function_filter` make ATTACH fast on a huge catalog by discovering a SUBSET — but they bound ENUMERATION
    (SHOW TABLES / `duckdb_tables` / full refresh), NOT targeted-by-name access. `FabricatorSchemaEntry::
    GetOrCreateEntry` now, on a miss WHEN an object filter is active (`FabricatorCatalog::HasObjectFilter`, set
    at ATTACH from the presence of a *_filter option), FETCHES the table by name (the miss may be a real table
    the filter merely excluded from enumeration); the entry is cached in `entries_` but NOT added to
    `table_types_`, so enumeration stays filtered while `db.schema.OutOfFilterTable` resolves. Without a filter,
    the discovery list is authoritative so a miss is genuinely absent (no wasted round-trip — CREATE-new is
    unaffected). This means a restrictive perf-filter no longer CAGES the catalog: you can always reach an
    object by name (and a scoped `invalidate_cache` reconciles it), the filter is just a discovery speed-up. The
    filter was never a security boundary anyway (`fabricator_query` bypasses the catalog). `verify_invalidate_scoped`
    covers it; `verify_catalog_filter` (enumeration counts) unchanged.


- **`COPY … TO '<path>/<table>' (FORMAT delta, …)` — path-targeted Delta write, NO ATTACH (2026-07-10,
  C++-only, no ABI).** A third registered copy function (`"delta"` beside `fabricator`/`bcp`; the official
  duckdb-delta extension registers NO copy function — its only write surface is INSERT into an attached
  table — so the name is free and the capability is ours alone): the target is a raw path (local / `s3://` /
  `onelake://` / abfss), split into `<root>/<table>`; the bind opens NOTHING — `CopyToInitGlobal` opens a
  **transient per-execution engineered-wood catalog** (`fabricator::OpenCatalog(root, "delta", options_json)`,
  flat layout, owned by the copy global state) and streams through the EXACT catalog-COPY bulk machinery, so
  the write disposition is the **`MODE` option — the Spark/delta-rs save-mode vocabulary**: `'overwrite'`
  (the default when no options given — create or fully replace, COPY-to-file intuition) | `'append'`
  (create-if-missing + append-if-exists — Spark `mode=append`; EW's append path OpenOrCreates commit-0) |
  `'error'`/`'errorifexists'` (Spark's default save mode — fail on an existing target) | `'ignore'` (silent
  no-op on existing) | `'error_if_not_exists'` (strict append — fail on a MISSING target instead of
  implicitly provisioning; the inverse of 'error', no Spark equivalent) | `'overwrite_partitions'` (dynamic
  partition overwrite — Spark has no mode name for it, it's `overwrite` + the `partitionOverwriteMode=dynamic`
  conf; with PARTITION_COLUMNS a MISSING target is created PARTITIONED and the first run is a plain append —
  idempotent first-run; without them a missing target is rejected UP FRONT provider-side, since the
  append-shaped implicit create would otherwise leave an empty unpartitioned commit-0 behind before the
  partitioned-target guard fired). **`PARTITION_COLUMNS` is allowed with EVERY mode and applies whenever the
  write actually CREATES the table** (explicit or implicit — the `createTable||replace` gate in `BulkInsert`
  was dropped; EW's `OpenOrCreateAsync(partitionColumns:)` applies them at creation only; a mismatched
  PARTITION_COLUMNS on APPEND is tolerated + ignored; NOTE under the default name-mapping the Hive dirs carry
  the PHYSICAL `col-<guid>` names per the Spark convention — `COLUMN_MAPPING 'none'` for logical-named dirs).
  **REPARTITION-on-overwrite (2026-07-10): `MODE 'overwrite'` + PARTITION_COLUMNS differing from an EXISTING
  table's partitioning CHANGES the partitioning** — the Delta protocol allows a new
  `metaData.partitionColumns` ONLY when every active file is removed in the same commit (readers interpret
  each `add.partitionValues` against the current partition schema), i.e. exactly a full overwrite (Spark:
  `overwriteSchema=true` + new `partitionBy`; there is NO `ALTER TABLE … PARTITIONED BY`). EW
  `WriteAsync(repartitionTo:)` → `WriteCoreAsync` splits by the NEW columns + folds the metaData swap into
  the overwrite commit (guarded: full overwrite only — a partition-scoped/dynamic overwrite would keep
  nonconforming files; coordinated with the identity-HWM metadata so one commit never carries two metaData
  actions); the streaming path falls back to collect for this shape (`TryWriteStreaming` returns null — no
  metaData-swap support there; checked BEFORE SetSchemaAsync so the fallback leaves no half-done commit).
  Absence of PARTITION_COLUMNS on overwrite = keep the current partitioning (no implicit departitioning);
  kernel-validated (delta_scan reads the repartitioned table). Previously this case SILENTLY kept the old
  partitioning — a real gap found by probing the protocol question. The dispositions (`error`/`ignore`/
  `error_if_not_exists`) are checked provider-side: the per-statement disposition rides the TRANSIENT
  catalog's options JSON (`copy_disposition` — sound because that catalog serves exactly one COPY;
  `DeltaCatalog.BulkInsert` probes `TableExists`; the ignore no-op returns without consuming the stream —
  BulkSession's finally drains). The legacy **`CREATE_TABLE`/`REPLACE`/`PARTITION_OVERWRITE` flags** (shared
  with `FORMAT mssql`) still work but CANNOT be mixed with `MODE`; `SCHEMA_MODE 'merge'|'overwrite'`
  composes with either spelling, plus **`PARTITION_COLUMNS 'a,b'`** (create-time Hive
  partitioning — deliberately NOT the generic `PARTITION_BY`, which DuckDB's planner intercepts for
  file-based copies) and **provider-option passthrough** (`NATIVE_WRITE` — defaults TRUE here, bounded-memory
  streaming; `DELETION_VECTORS`/`COLUMN_MAPPING` — e.g. `DELETION_VECTORS false, COLUMN_MAPPING 'none'` for a
  SQL-Server-/protocol-1.0-readable table; `ROW_TRACKING`/`MATERIALIZE_ROW_TRACKING`/`CHANGE_DATA_FEED`/
  `IN_COMMIT_TIMESTAMPS`/`COMPRESSION`/`ROW_GROUP_SIZE`/`BLOOM_FILTER_COLUMNS`). **The transaction crux:** the
  provider PARKS plain appends per (txn, table) and flushes at `commit_transaction` — a transient handle has
  no TransactionManager, so the COPY drives it itself: finalize does `SetActiveTxn(handle, txn_id)` +
  `fabricator::CommitTransaction(handle)` (no-op for create/replace, flushes the parked append as ONE commit) +
  `CloseCatalog`; the gstate destructor is the failure backstop (`RollbackTransaction` — discard-only, no
  opener needed — then close). ⇒ the COPY is its own atomic Delta commit and deliberately does NOT roll back
  with a surrounding DuckDB BEGIN (file-COPY semantics; the transient catalog's buffer is invisible to the
  user txn). Verified: `test/verify_delta_copy_format.test` (41 — create/overwrite/append-flush/replace/
  partitioned + dynamic partition overwrite/schema merge+overwrite/protocol-shape pins/ATTACH-reads-it-back/
  path validation), S3 section in `verify_delta_catalog_s3.test` (131 — COPY to `s3://fabricator/copyfmt` incl.
  partition overwrite + re-ATTACH), catalog-COPY suites unregressed (partition_overwrite 90 / overwrite_merge
  47 / native_write_streaming 29), **live OneLake** (`COPY … TO 'onelake://Test/LH.Lakehouse/Tables/dbo/…'
  (FORMAT delta)` → native streamed v1 + exact readback; an `onelake://` root doesn't match the
  `FabricLakehouse.IsOneLake` abfss check, so it rides the plain host-FS path with opener-resolved secrets —
  which is exactly right). Kernel reads the outputs (known quirk unchanged: kernel shows a MAPPED partitioned
  table's partition column as NULL; plain shape exact).

---

## `MERGE INTO` — BUILT + GATED 2026-08-05 (C++-only, no ABI bump)

> **Moved verbatim out of `CLAUDE.md` (2026-08-23).** It shipped a SILENT-DATA-DESTRUCTION bug for half a
> day; the shape of that miss is the most reusable thing here, so it is kept in full. `CLAUDE.md` keeps the
> refusal rule and the "two row-addressing actions" smell.

- **`MERGE INTO` — BUILT + GATED 2026-08-05 (C++-only, no ABI bump). ⚠ It SHIPPED A SILENT-DATA-DESTRUCTION
  BUG FOR HALF A DAY BEFORE THE FIX, and the shape of that miss is the most reusable thing here.**
  - **⛔ THE BUG, MEASURED (found by the user asking "can we actually do a delete update insert in these
    order? i think we are in trouble" — the answer was yes, we were).** Delta × `deletion_vectors=false` ×
    AUTOCOMMIT × ≥2 mutating actions: a later action addressed the **WRONG ROW**. Every action consumes rowids
    captured from the merge's ONE join scan, but each action committed separately — and a **copy-on-write
    DELETE removes a row, shifting every LATER row's position down one**, so a subsequent action's captured
    `(fileOrdinal, position)` named a different row. On a one-file table `(1,10)(2,20)(3,30)(4,40)` with
    conditional deletes of id1 and id3 the survivors were **`2, 3`** — id3 NOT deleted, **id4 DESTROYED**,
    exit 0. The update variant silently lost the update instead.
    - **⚠ WHY EVERY TEST MISSED IT, which is the transferable part: the hazard needs the rows in ONE FILE.**
      With a row per file a copy-on-write rewrite renumbers nothing, so all four of my earlier
      multi-action/multi-file probes were correct — and both tiers were GREEN through the bug. It is strictly
      positional (corrupt iff the deleted row precedes the other action's target), so even a single-file test
      passes if the delete happens to sit last. **A merge test that does not put several affected rows in one
      file, with the delete FIRST, tests nothing about this.**
    - **⚠ AND THE GREEN TIERS WERE THE TRAP.** 65/65 and 45/45 were reported as evidence the feature was
      finished. They were evidence only that the shapes I had imagined worked. The user's question was worth
      more than the whole suite run.
  - **THE FIX: a merge carrying ≥2 `UPDATE`/`DELETE` actions is FORCED TO BUFFER, even in autocommit.**
    `PlanMergeInto` counts them and sets `force_buffered` on each target; each operator's `GetGlobalSinkState`
    then calls `BeginTransaction(handle, is_explicit=true)`. Both actions stage against ONE pinned snapshot ⇒
    neither can renumber the other's targets ⇒ one commit.
    - **⚠ THE MARK MUST HAPPEN AT EXECUTION TIME, NOT PLAN TIME.** A prepared statement's physical plan is
      reused across transactions, so a plan-time mark would apply to the first one only. `GetGlobalSinkState`
      is the right hook because `PhysicalMergeInto` builds every action's global sink state UP FRONT, before
      any action does provider work — so whichever action runs first sets it and the rest observe it,
      including the INSERT's own `begin_bulk`, which therefore buffers instead of committing on its own.
    - **⚠ THE COUNT EXCLUDES `INSERT`, AND MY FIRST VERSION GOT THIS WRONG — user-caught, via DuckLake's own
      docs.** Counting every MUTATING action was measured to REFUSE the single most common merge shape,
      `WHEN MATCHED THEN UPDATE` + `WHEN NOT MATCHED THEN INSERT`, on a non-DV table where it had always been
      correct and was never unsafe. An `INSERT` addresses no existing rows, so it can neither renumber another
      action's targets nor hold targets of its own — and it commits LAST regardless (it is the one action that
      always routes through the transaction buffer, as the instrumented log shows). So the broad count bought
      no safety and cost the common case. **The hazard needs TWO ROW-ADDRESSING actions, nothing less.**
      - **This is the boundary DuckLake documents** ("MERGE INTO with DuckLake only supports a single
        UPDATE/DELETE action currently", https://ducklake.select/docs/stable/duckdb/usage/upserting) — arrived
        at independently, which is some evidence it is the real fault line. **We are STRICTLY more capable:
        DuckLake REFUSES two such actions outright; we SERVE them by fusing, and refuse only when the table
        cannot be buffered at all.** ⚠ So the earlier note here claiming we are "more permissive than DuckLake"
        because we accept 4-action merges is still true, but for a narrower reason than it implied: what we add
        is fusion, not permissiveness about the hazard.
    - **ONE row-addressing action keeps the direct path** — nothing to collide with — so a non-DV table loses
      no capability. Asserted as a POSITIVE CONTROL, since otherwise the §11 refusal would pass equally if
      non-DV tables had simply become unwritable.
    - **⚠ NO ABI BUMP, by REUSING `BeginTransaction(isExplicit)` — whose real meaning is "the USER opened a
      transaction", not "buffer this statement". That overload HAD a measured cost, and fixing it is the second
      narrowing this feature needed.** On SQL Server that entry also gates three EXTERNAL-TABLE guards, so a
      2-action merge into an identity-equipped SQL Server external table was **refused** by the pre-existing
      *"storage-side DML … cannot roll back with an explicit transaction"* check — MEASURED, after first
      probing a table with no row identity and getting a different error, which nearly recorded the wrong
      conclusion in both directions.
      - **THE FIX, and it is the right scoping rather than a workaround: force only where row identity is
        POSITIONAL** — `rowid_actions >= 2 && entry.HasVirtualRowId()`. The hazard is one action RENUMBERING
        rows another addressed, which requires a TRANSIENT (file, position) rowid, i.e. a provider VIRTUAL
        rowid as Delta's `_metadata.row_id` is. Where the rowid is real KEY COLUMNS (SQL Server's PK / unique
        index / IDENTITY) it is a VALUE, stable under any rewrite — measured immune to both corrupting shapes —
        so forcing bought nothing there and only cost the external-table capability. **Provider-agnostic: it
        names an identity KIND, not a provider**, which is why it belongs in the shared layer.
    - **TRADE-OFF ACCEPTED:** a merge with one `UPDATE`/`DELETE` plus an `INSERT` is therefore NOT fused, so in
      autocommit it is two commits — correct but not atomic, i.e. the pre-existing Delta per-statement
      divergence. `BEGIN … COMMIT` still fuses it. That is the right way round: refusing the common shape on a
      non-DV table to buy atomicity would trade a capability for a guarantee nobody asked for.
    - **⚠ "TWO COMMITS" IS NOT THE SMELL — "TWO OPERATIONS ADDRESSING PRE-EXISTING ROWS" IS.** Worth stating
      because I had been using the commit count as the diagnostic, and a user question about CTAS showed it does
      not discriminate. MEASURED, same session: `CREATE TABLE … AS SELECT` is **2 commits** (`CREATE TABLE` then
      `WRITE`) in autocommit **AND inside an explicit transaction** — so its two-ness is NOT caused by the
      autocommit/buffered decision and forcing the buffer cannot fix it (it is limitation 1.5: EW's
      `OpenOrCreateAsync` commits v0 before any transaction on that table can exist). Yet a CTAS has NO hazard —
      a new table has no pre-existing rows to renumber. Conversely `CREATE OR REPLACE … AS SELECT` over an
      existing table is **1 commit** (a single `WRITE`), i.e. two operations fused with no forcing at all. So
      commit count tracks neither risk nor atomicity reliably.
      - **The audit that follows from the right smell:** MERGE is the ONLY statement DuckDB plans as multiple
        DML operators sharing ONE scan's row addresses, and `INSERT … ON CONFLICT` is the same mechanism (the
        binder rewrites it into a MERGE) which we already refuse. So the exposure was unique to MERGE rather
        than one member of a class we have patched only partially — checkable, and checked.
  - **Scope of the original hazard, all measured:** `deletion_vectors` defaults ON and a **DV delete PRESERVES
    positions**, so the default was always safe (all four position combinations verified). An EXPLICIT
    transaction already refused the non-DV path. **SQL Server is IMMUNE** — its rowid is a PK VALUE, stable
    under any rewrite (verified with both corrupting shapes). So the blast radius was exactly Delta × non-DV ×
    autocommit × ≥2 actions.
  - **⚠ The hazard was NOT two actions touching one row.** `PhysicalMergeInto` removes each row from the
    candidate set as an action claims it, so actions are row-DISJOINT by construction, and the existing
    same-transaction guards ("cannot delete rows inserted in this transaction") key on the ordinal's
    pending-vs-committed RANGE — a different axis. The hazard was one action RENUMBERING rows another had
    already addressed, which no guard covered and none of that family would have caught.
  - Gates: `verify_merge_into.test` **209 × 2 engine legs** (hermetic, ENGINE-DOUBLED) + `verify_merge_into_mssql.test`
    **106** (service). §11 is the destruction regression gate (refusal + table bit-for-bit intact + the
    single-action positive control), §11b the same shape on a DV table asserting BOTH the right answer and the
    fusion — a correct result reached by three commits would mean the unsound mechanism is still running and
    merely got lucky. **Mutation-tested**: disabling the forcing reproduces `2, 3` exactly and kills the suite.
  - **⚠ `ON CONFLICT` came along for free ARCHITECTURALLY and still does NOT work — for a reason upstream of
    the merge (see below).** One override,
  `FabricatorCatalog::PlanMergeInto` (`src/catalog/fabricator_merge_into.cpp`), lifted the shared refusal
  `Database type "fabricator" does not support MERGE INTO or ON CONFLICT` for **every** provider at once.
  Measured working on Delta AND SQL Server: matched UPDATE, matched conditional DELETE, not-matched INSERT
  (with and without a column list), `WHEN NOT MATCHED BY SOURCE`, `DO NOTHING`, the `ERROR` action, and
  ROLLBACK. Gates: `verify_merge_into.test` **130 × 2 engine legs** (hermetic, ENGINE-DOUBLED — a merge is
  composed of exactly the update/delete/insert paths that list already doubles) + `verify_merge_into_mssql.test`
  **90** (service).
  - **THE LOWERING IS DuckDB'S, NOT OURS — that is the whole reason this was small.** Each action becomes the
    same `Logical{Update,Delete,Insert}` the standalone statement produces, routed through our OWN
    `PlanUpdate`/`PlanDelete`/`PlanInsert`. So MERGE INHERITS every property of our rowid DML rather than
    re-deriving it: provider dispatch, the buffered-transaction fusion, the change feed, identity handling.
  - **⚠ DuckLake IS the reference, NOT `DuckCatalog` — and the earlier note here saying otherwise cost time.**
    `ducklake/src/storage/ducklake_merge_into.cpp` is a CUSTOM catalog doing exactly this (synthesize the
    logical op, call its own `Plan*`), which is our situation; `DuckCatalog` plans against its own storage. **We
    are MORE permissive than DuckLake on two axes**, both measured: it refuses more than ONE update-or-delete
    action total (*"MERGE INTO with DuckLake only supports a single UPDATE/DELETE action currently"*) while we
    serve DELETE + UPDATE + INSERT + NOT-MATCHED-BY-SOURCE in one statement, because each action gets its own
    operator and the buffer fuses their actions at COMMIT. (Both of us refuse RETURNING.)
  - **⚠ `PhysicalMergeInto` drives the sub-operators as MANUAL SINKS** — it calls
    `GetGlobalSinkState`/`GetLocalSinkState`/`Sink`/`Combine`/`Finalize` directly on sliced chunks, never as a
    pipeline. Ours are already self-contained sinks (our `PlanInsert` already accepted a null child), so they
    slotted in unchanged. **`parallel` MUST be false and that is load-bearing, not caution**: every action
    shares ONE global sink state, and `FabricatorPhysicalInsert` streams into a single bulk session whose
    `PushBatch` takes no lock — documented as safe only because `ParallelSink()` is false. DuckLake passes
    `true` because its operators are parallel-safe; ours are not.
  - **⚠ THE ONE REAL CODE CHANGE WAS WHERE AN UPDATE READS ITS SET VALUES, AND IT FIXED A LIVE CORRUPTION BUG.**
    `AppendModifyBatch` read them POSITIONALLY from chunk `0..n-1`, which is right only because a plain
    UPDATE's binder projection happens to put them there. Two things break it: a MERGE's UPDATE action shares
    ONE projection with every other action (arbitrary positions), and **`SET x = DEFAULT` contributes NO
    projection column, shifting every later SET value by one**. The second was already shipping. **Measured on
    `(a BIGINT DEFAULT 99, b BIGINT, c INTEGER)`: `SET a = DEFAULT, b = 5` SUCCEEDED and committed `a=5, b=0`**
    (b got the rowid) where correct is `a=99, b=5`; where the shifted types differ instead it raised an
    INTERNAL error and **fatally invalidated the database**. Now `FabricatorModifyTarget.set_child_indices`
    carries the BOUND_REF position per SET column (upstream `PhysicalUpdate` reads them the same way), shared by
    both paths via `FabricatorFillUpdateSetColumns` so they cannot drift; `SET = DEFAULT` is REFUSED rather
    than guessed (evaluating it needs the bound defaults in the operator — a feature, deliberately not smuggled
    into a MERGE change). Gate in `verify_delta_catalog_update.test` (63 → 73), mutation-tested: reverting to
    the positional read kills BOTH merge suites at their FIRST merge statement.
  - **⚠ THE `!HasRowId()` GUARD IS REQUIRED FOR *EVERY* MERGE, INCLUDING AN INSERT-ONLY ONE.** DuckDB decides
    matched-vs-not by testing the rowid column for NULL, so with no rowid `BindRowIdColumns` appends nothing and
    `row_id_start` points ONE PAST the chunk's width. `ComputeMatches` reads `chunk.data[row_id_index]`
    unconditionally. An insert-only merge never reaches `FabricatorBuildModifyTarget`'s own check, so without
    this guard it is an out-of-bounds read — **mutation-tested: `INTERNAL Error: Attempted to access index 2
    within vector of size 2`, then the database is FATALLY INVALIDATED.** Refuse at plan time, where it can
    still be a message.
  - **⚠ ATOMICITY IS THE TRANSACTION'S, NOT THE STATEMENT'S — measured both ways, and autocommit is NOT atomic.**
    A merge is several DML operators, so on Delta an **autocommit `MERGE` produces ONE COMMIT PER ACTION**
    (measured: baseline 2 → 4; three actions ⇒ three commits) while `BEGIN; MERGE; COMMIT;` fuses them into
    **ONE** (2 → 3). The DATA is correct either way; only atomicity differs. **The change feed of the fused
    form is exact** — an `update_preimage`/`update_postimage` pair plus the `insert`, all at one version (this
    was the stated priority) — while the autocommit one is SPLIT across versions. Same per-statement-commit
    divergence the rest of the Delta provider has; every number is pinned (`verify_merge_into.test`
    §3 / §3b / §5 / §12) so a change reads as deliberate.
    - **⚠ THE MECHANISM IS NOT "ONE `DeltaTransaction` PER ACTION" — INSTRUMENTED, because the obvious guess is
      wrong in both directions.** There are exactly TWO `StartTransaction` sites in the Bridge.
      **EXPLICIT: ONE shared transaction** — `pending.HeldTxn ??= table.StartTransaction(...)`
      (`DeltaCatalog.cs:3701`), keyed per DuckDB-transaction × table, so every action stages into it and one
      `CommitAsync` writes one version. **AUTOCOMMIT: three commits by THREE DIFFERENT mechanisms** — the DV
      DELETE commits directly with **no `DeltaTransaction` at all**, the merge-on-read UPDATE creates its OWN
      short-lived one (`DeltaReader.cs:2620`, `await using`), and the INSERT **still routes through the txn
      buffer** (autocommit has an implicit DuckDB transaction — the log shows `buffered … for txn 12`) so it is
      flushed LAST, after the delete and update have already committed. So the intermediate states an observer
      can see are delete → delete+update → all three, and the INSERT commits last despite its bulk session
      being opened FIRST at merge init.
    - **⚠ INTEROP: `commitInfo.operation` is `TRANSACTION` for a fused merge, and NOTHING we write ever says
      `MERGE`.** Autocommit labels each action instead (`DELETE`/`UPDATE`/`WRITE`). Measured via
      `fabricator_delta_snapshots` (identical on BOTH engines) and pinned per VERSION in §13 — never as an
      aggregate, since `max(operation)` over a string column returns the ALPHABETICAL maximum. A foreign
      consumer keying on `operation = 'MERGE'` will not match us.
  - **⚠ AND THE MODES DIFFER IN CAPABILITY, OPPOSITE TO THE ATOMICITY TRADE-OFF: a merge with an UPDATE/DELETE
    action WORKS in autocommit on a `deletion_vectors=false` table and is REFUSED inside a transaction.** The
    buffered path requires DVs; the autocommit path rewrites copy-on-write and does not. So wrapping a working
    merge in `BEGIN` to gain atomicity can COST the statement (*"… requires deletion vectors on the table … run
    it in autocommit (copy-on-write), or COMMIT first"*, table left unchanged). Inherited from the plain
    statements rather than MERGE-specific — which is the lowering working as designed — and it bites only where
    DVs were switched off. Pinned in BOTH directions with a positive control (§11).
  - **The same-transaction hazards do NOT bite, and the reason is structural.** `UPDATE of rows inserted in the
    same transaction` is refused on any table, `DELETE of rows inserted in the same transaction` on a CDF table
    — but both guards are **PER-ROW, keyed on the rowid's FILE ORDINAL** (`>= PendingOrdinalBase`), not on the
    mere presence of pending appends. A merge's matched rows come from the pre-merge snapshot, so they carry
    committed ordinals. ⇒ **MERGE does not need hoist slice 3.**
  - **STILL OPEN — the SQL Server half is CORRECT BUT NOT OPTIMISED.** Actions run as per-row DML on the pinned
    connection, NOT as a server-side T-SQL `MERGE`. Generating one server-side statement needs the SOURCE to be
    server-side too, and a DuckDB MERGE's source is a DuckDB relation (the README example merges a DuckDB temp
    table INTO SQL Server, which is exactly the shape that cannot be pushed down). A pushdown would have to
    detect "source and target are both in this catalog" and fall back otherwise.
  - **⚠ `ON CONFLICT` IS NOT AN INDEPENDENT FEATURE — THIS FILE SAID IT WAS, AND THAT WAS WRONG.** Since 1.5.x
    the binder **REWRITES `INSERT … ON CONFLICT` into a MERGE** (`Binder::Bind(InsertStatement&)` →
    `GenerateMergeInto`, `bind_insert.cpp:541`), which is why ONE message covered both features and ONE
    override lifted both. It still does not WORK, for a reason upstream of the merge: `GenerateMergeInto` keys
    the join on a UNIQUE/PK constraint and `FabricatorTableEntry::GetStorageInfo` returns an EMPTY
    `TableStorageInfo`, so DuckDB finds no uniqueness. Measured: with a target ⇒ *"The specified columns as
    conflict target are not referenced by a UNIQUE/PRIMARY KEY CONSTRAINT or INDEX"*; without ⇒ *"There are no
    UNIQUE/PRIMARY KEY constraints that refer to this table"*. **On Delta that refusal is semantically CORRECT**
    — Delta enforces no unique constraint on user columns, so there is nothing to conflict against and
    "fixing" it would claim a guarantee the format lacks. On SQL Server a real PK/unique index exists, so the
    remaining work is `GetStorageInfo`, NOT the merge hook. Pinned by `verify_merge_into.test` §10.
    - The old deferral rationale is **right about T-SQL and irrelevant to the path DuckDB takes**: SQL Server's
      `IGNORE_DUP_KEY = ON` is an option on a UNIQUE INDEX, so it expresses only `DO NOTHING` and only where the
      index was built that way. That matters for a *native* pushdown; through the merge rewrite ON CONFLICT
      needs no server feature at all.
  - `update_is_del_and_insert` is ignored: the merge binder hardcodes it FALSE (`bind_merge_into.cpp:87`) and we
    do not override `BindUpdateConstraints`, so nothing sets it — and our UPDATE operator owns that choice
    anyway (Delta copy-on-write already rewrites).
  - **⚠ A C++ TRAP worth remembering: do NOT declare `namespace fabricator` INSIDE `namespace duckdb`.** The
    extension's generic core is the GLOBAL `::fabricator`; a nested `duckdb::fabricator` shadows it for every
    TU that includes the header, so every existing `fabricator::PartitionColumnsArg` /
    `BoundaryClientProperties` call fails to compile with *"is not a member of duckdb::fabricator"*. Hence the
    two shared helpers are `FabricatorBuildModifyTarget` / `FabricatorFillUpdateSetColumns`, in `duckdb`
    directly.

---

## The UPDATE post-image grouped flush (2026-08-06) — as built

> Moved verbatim out of `CLAUDE.md` (2026-08-23). Kept in full because its headline is a
> NEGATIVE result — it does NOT fix "UPDATE memory" — and the measurement that says so, plus the
> boxed-SET-values attribution it led to, is the reusable part.

- **THE UPDATE POST-IMAGE GROUPED FLUSH — DONE 2026-08-06 (C#-only, no ABI). ⚠ IT DOES NOT FIX "UPDATE
  MEMORY", AND THE MEASUREMENT SAYING SO IS THE MOST USEFUL THING HERE.** Both UPDATE paths
  (`DeltaReader.MergeOnReadUpdateAsync` autocommit, `DeltaCatalog.BufferUpdateRows` buffered) used to
  accumulate EVERY post-image batch — and every pre-image on a CDF table — before writing anything. They now
  write a group's worth as the read-back streams and keep only the `WrittenDataFile`/`CdcFile` actions. Still
  exactly ONE commit. Threshold `DeltaReader.UpdateGroupBytes`, 64 MiB of Arrow data, env-overridable via
  `FABRICATOR_DELTA_UPDATE_GROUP_BYTES`.
  - **⚠ FILE LAYOUT IS UNCHANGED BY CONSTRUCTION, which is what makes the grouping free rather than a
    trade-off** — and it is worth knowing independently: `WriteDataFilesAsync` writes **one parquet file per
    (input batch × partition)** (`DeltaTable.cs:5053`, a `foreach` over the batches), so N read-back batches
    become N data files whether they arrive in one call or a hundred. The file count of an UPDATE's post-images
    is therefore its BATCH count and no size target touches it. Measured: a 5000-row UPDATE adds 3 files, 50k
    adds 25, 200k adds 98 — i.e. ~2048 rows per batch.
  - **⚠ IT IS INERT ON THE BUFFERED PATH, and this entry claimed otherwise until it was measured
    (2026-08-06).** A group boundary can only fall BETWEEN read-back batches, and the two paths batch
    differently. Same table, same 60k-row UPDATE, threshold forced to 1 byte: **autocommit 30 group flushes,
    buffered 1** — the buffered read-back hands over all 60,000 rows as ONE batch (confirmed independently by
    the post-image file count, 30 files vs 1, since `WriteDataFilesAsync` writes one file per input batch).
    So on the buffered path the group IS the statement and the grouping changes nothing. The autocommit
    numbers below are real; do not generalise them.
    - **⚠ MECHANISM — MEASURED, and it is NOT autocommit-vs-buffered at all: it is WHICH READER is in play.**
      Same autocommit UPDATE, same shape, threshold 1: `native_read true` ⇒ **30** flushes,
      `native_read false` ⇒ **1**. DuckDB's `read_parquet` yields standard 2048-row vectors; engineered-wood's
      codec reader yields **one batch per ROW GROUP** — pinned by a 300k-row control giving exactly **3**
      batches at the 122880 default. And the buffered read-back opens with a bare `DeltaWriter.Options()`,
      passing **no `dataFileReader`** (`DeltaReader.cs:974`), so it takes the codec reader ALWAYS — see the
      `native_read` entry in the streaming audit, which is the real defect here.
    - The candidates an earlier pass listed are all RETIRED: `BlockingEnumerable` was correctly cleared (it is
      a lazy pass-through), and `atVersion` / `skipUnresolvable` / `ReconcileBatch` were all wrong. **The
      answer was in the OPTIONS passed at open, not in the enumeration** — which is the reusable lesson: when
      two callers of one method behave differently, diff what they CONSTRUCT it with before diffing the call.
  - **⚠ MEASURED, and the headline is not the one this was built for.** On the shape that favours it most
    (600k rows × 16 VARCHAR, UPDATE every row, SET one column): **managed heap peak 327 → 171 MB** and now
    bounded by the GROUP rather than by the statement — but **process peak working set only 614 → 548 MB**.
    Time is flat (9.3 → 9.6 s; **71 flushes is as fast as 5**, so flush count costs nothing measurable). On a
    NARROW table the grouping does not fire at all: 1M rows × 3 columns accumulates ~50 MB of read-back, under
    the threshold, and peak is **identical either way (449 MB)** — so the earlier "~474 MB per 1M matched rows"
    figure was never mostly this.
  - **⚠ THE ACTUAL DOMINANT TERM, found by instrumenting the working set through the path: ~180 MB is already
    spent BEFORE the read-back begins (253 MB at 1M × 3 cols).** That is DuckDB's own side of the statement
    plus, on ours, `DeltaCatalog.ExecuteUpdate`'s `Dictionary<long, object?[]>` of **BOXED** SET values, the
    Arrow batch rebuilt from it, and `updRowByRid` — all three complete before any provider work starts, all
    three scaling with MATCHED rows.
    - **⚠ NOW MEASURED, not inferred, and the SLOPE is what makes it conclusive (2026-08-06).** One table,
      1M rows, every row touched, three statements differing only in how many SET values cross the seam:
      **DELETE (rowids only, no boxes) 204 MB / 1.7 s** — the floor; **UPDATE 1 SET column 454 MB / 5.5 s**
      (+250 MB); **UPDATE 3 SET columns 651 MB / 5.6 s** (+447 MB). So **~98 MB per ADDITIONAL SET column per
      1M rows ≈ 98 BYTES PER 8-BYTE BIGINT VALUE**, a ~12× representation overhead, and the first column costs
      more (~250 MB) because it carries the per-ROW costs too (the `object?[]` header + the dictionary entry).
      The DELETE floor is the control that makes this OURS rather than DuckDB's: same rows, same table, same
      scan, no SET values. Note the TIME gap as well — 3.2× for the same rows.
    - **NEXT FIX: keep the SET values in ARROW form instead of boxing them** — `ParseUpdateStream` builds
      Arrow columns directly from the incoming chunks and `updRowByRid` becomes rowid → ordinal. Expected
      ~250 MB → ~50 MB for the one-column case. It is a DML-SEAM change (`ParseUpdateStream` /
      `ExecuteUpdate` / `BufferUpdateRows`), not a Delta one; `ExternalTableRouting` also calls
      `ExecuteUpdate`, so check that path too.
      - **⚠ THE CONSTRAINTS FOUND WHILE SCOPING IT, all of which make the naive version wrong.**
        (1) **⚠ THE INCOMING BATCHES CANNOT BE RETAINED — this is the one that decides the design, and it is
        already established in this codebase.** `DeltaWriter.Materialize` does a full Arrow **IPC round-trip**
        (write every batch to a `MemoryStream`, read them back) precisely because *"the source batches may be
        freed after consumption"*; and `ParseUpdateStream`'s own `ReadScalarDeep` is documented as deep-copying
        because *"the batch is disposed after this loop"*. So "keep the chunks and address rows inside them" is
        a use-after-free, not an optimisation. The cheap independent copy is
        `ArrowCompute.Take(batch, schema, identityIndices)`, which allocates new buffers.
        (2) **⚠ A CLAIM RECORDED HERE WAS FALSE AND IS CORRECTED: `Apache.Arrow.ArrowArrayConcatenator.Concatenate`
        EXISTS and is public** (engineered-wood uses it in six places, e.g. `DeltaTable.cs:6509`,
        `LanceFileReader`, `VortexFileReader`). The earlier note said there was no Concat — that came from
        reading `EngineeredWood.Arrow.ArrowCompute`'s surface, which has `Take`/`Widen`/`MakeNullArray` and no
        concat, and generalising from ONE class to the whole Arrow surface. It is the same backwards-search
        error the tier-1 notes warn about: **a grep that finds nothing has only established where you looked.**
        With Concat available, the per-chunk copies can be joined into one array per column and the design does
        NOT need a bespoke gather helper.
        (3) **`updates[rid] = vals` DEDUPLICATES by rowid, last-write-wins** — reachable via
        `UPDATE … FROM other` whose join matches a target row twice — and it also sets the statement's
        REPORTED row count. Appends cannot overwrite, so the replacement must append everything, keep
        rowid → LAST ordinal, and compact with one `Take` at the end.
        (4) **⚠ The boxing is currently also doing a TYPE CONVERSION**: `BuildArray(field.DataType, values)`
        rebuilds each SET column at the TARGET column's type, so an incoming array of a different width or
        unit is silently converted through the boxed value. Reusing the incoming Arrow array directly changes
        that behaviour. Cheapest faithful answer: reuse Arrow only where the incoming type EQUALS the target
        type, and keep the boxed rebuild for that column otherwise — behaviour-preserving where it matters and
        free in the common case.
      - **Shape that follows from (1)–(4):** per chunk, `Take` an independent compact copy and record
        rid → packed (chunk, row); at the end `Concatenate` per column and apply ONE `Take` with the surviving
        ordinals — which yields `updatesBatch` DIRECTLY, so `ExecuteUpdate` stops rebuilding it and
        `BufferUpdateRows` reads its values from that batch instead of from boxes.
  - **⚠ THE ALL-OR-NOTHING ROW-ID RULE HAD TO MOVE EARLIER, and that is the one semantic consequence.** A
    group is written before the later groups' ids are known, so "every selected row resolved a stable id" can
    no longer be decided after the read-back. It is now decided BEFORE it, from the files: the read-back yields
    a null id only where the row's file has no `baseRowId` AND no materialized value, and a writer that
    materializes ids also stamps `baseRowId` (the spec requires one on every `add` of a row-tracking table), so
    "every selected file has a baseRowId" is the same condition — a dictionary lookup per selected path
    (`snapshot.ActiveFiles`), no extra IO. Autocommit checks the SELECTION's paths; the buffered path uses the
    new `TxnDmlProfile.AllFilesRowTracked` (computed in the probe it already does) and trusts it ONLY when the
    pinned version IS the version it describes. **Where it cannot be established the threshold is DISABLED and
    the statement buffers whole, byte-identically to before** — a legacy table keeps its old behaviour instead
    of acquiring new semantics from a memory fix. A null appearing after a group was written WITH ids throws
    loudly rather than silently splitting identity.
  - Also trimmed: `ridsPerBatch` / `srcTracking` are now drained per batch (their producer only appends, never
    reads them back), so they no longer accumulate across the statement either.
  - **64 MiB rather than 16 MiB** (which measured marginally better, 152 MB heap) because the buffered path's
    per-group write used to **open the table** — one `_delta_log` LIST per group, cheap locally and not on
    OneLake/S3. **FIXED 2026-08-06: `TryEagerWriteBatches` now reuses the pair's HELD table**
    (`EnsureHeldTableAsync`) instead of opening and disposing its own, so an eager write costs no log read at
    all. It no longer disposes the table either — that belongs to the buffer entry and pulling it out from
    under the held transaction would break every later statement of the DuckDB transaction.
    - **⚠ THE SWAP WAS NOT THE PURE PERF CHANGE IT LOOKED LIKE, AND DOING IT FIRST WOULD HAVE BROKEN A
      USER-FACING FEATURE.** `TryEagerWriteBatches` was the ONLY open in the whole Bridge passing a WRITE SPEC
      (`ResolveWriteSpec`); the held table passed none. Reusing it would have made the eager path lose the
      user's `delta_write_options` rather than making the held one honour them. So the spec was added to
      `EnsureHeldTableAsync` FIRST — which fixed a real defect in its own right (below) and only then made
      the swap equivalent.
  - **⚠ THE DEFECT THAT FOUND: WRITE TUNING REACHED THE BULK PATH AND ALMOST NOTHING ELSE (fixed for the
    buffered surface 2026-08-06, still open elsewhere).** `delta_write_options`
    (`compression` / `row_group_size` / `bloom_filter_columns`) is resolved by `ResolveWriteSpec`, which
    returns **null** when nothing is configured — so the divergence is invisible until a user sets something,
    which is why nothing caught it. MEASURED per file on the codec engine with `compression 'zstd'`: the CTAS
    files came out **ZSTD** and, in the SAME table, the CDF change files **SNAPPY** and the merge-on-read
    UPDATE's post-image file **SNAPPY**. A table therefore accumulates MIXED compression, and on an
    incrementally-updated dbt model most bytes would silently be snappy.
    - **⚠ The codec engine is required to see any of this**: under `native_write` (the `PROVIDER 'delta'`
      default) DuckDB's COPY writes the data files and EW's `ParquetWriteOptions` never apply — a first
      attempt at this measurement on `PROVIDER 'delta'` returned SNAPPY for everything and was VOID, not an
      answer. The gate pins the codec engine for the same reason and carries a positive control.
    - Fixed here: `EnsureHeldTableAsync` now passes the spec, so the CDF change files and any batches the
      flush parks honour it. **STILL OPEN and measured, for the audit:** every other EW open in
      `DeltaReader` passes no spec — the merge-on-read UPDATE post-images, the copy-on-write DELETE/UPDATE
      rewrites, and OPTIMIZE's compaction output. Those need the spec plumbed from the catalog into a static
      reader, which is more than a one-line change.
    - Gate `verify_with_options` 68 → **82**, mutation-tested (reverting the spec on `EnsureHeldTableAsync`
      fails at exactly the CDF assertion with `SNAPPY`).
  - **⚠ GATE: `verify_delta_update_grouped.test` (72), and it needs the runner to FORCE the threshold.** No
    hermetic suite comes within two orders of magnitude of 64 MiB, so without this the grouped path ships with
    ZERO coverage; `run-suites.sh` gives this ONE suite `FABRICATOR_DELTA_UPDATE_GROUP_BYTES=1` and `unset`s it
    for every other (load-bearing in both directions — a value left in the developer's shell would otherwise
    group every suite and the shipped default would go untested). It updates **6000 rows on purpose** (~2048 per
    batch ⇒ three groups) and asserts the ONE commit per statement, read-your-writes + ROLLBACK on the buffered
    path, the CDF pair joining row-for-row across group boundaries, and stable ids surviving. It passes
    IDENTICALLY with the default threshold — that equivalence is the point. **Mutation-tested with two mutants,
    each killed at its own section**: not clearing the per-group id list dies at the FIRST grouped UPDATE
    (*"materializedRowIds must carry one entry per row"*), and not clearing the per-group pre-images **survives
    51 assertions** before the CDF section catches **12144 pre-images for 6000 rows** — which is precisely why
    that section exists.
  - Gates: hermetic **66/66 — 6367**; the three engine-doubled delta suites also re-run with
    `GROUP_BYTES=1` at identical assertion counts.
