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
  pre-cast: **one `ArrowTypeExtension` registered at load** (`src/fabricator/fabricator_variant.cpp` — <!-- check-docs:ignore (DELETED 2026-10-22 with the variant transport; naming it IS the point) -->
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

## Appendix — records moved verbatim from CLAUDE.md (2026-09-18)

CLAUDE.md carried these as-built records inline until it grew to 10,776 lines — a file loaded into every
session's context. They are moved here VERBATIM; CLAUDE.md keeps each entry's summary head plus a pointer to
this section. The one edit made on the way: a link that pointed into the docs directory is rewritten relative
to this directory, so it still resolves from here.

- **⚠⚠ AN `UPDATE` THAT SETS A *LIST* COLUMN HANDED THE PROVIDER THE WHOLE ROW AS SET COLUMNS — 17 instead
  of 1. ANALYSED 2026-09-15 from `fabricator-grist`, ✅ FIXED THE SAME DAY by the virtual override this repo
  did not have. No ABI change; C++ only. ⚠ THE AS-BUILT RECORD IS THE LAST SUB-ENTRY — read it first; the
  analysis below it is intact and still accurate, and one thing it could not know is that on THIS tree every
  triggering type is also one the Delta provider cannot write (limitation 1.26).** A provider that diffs the incoming row to decide what actually
  changed cannot tell "the user set 17 columns" from "DuckDB expanded one", and there is nothing on the
  wire that says which happened.
  - **THE MEASUREMENT.** Instrumented `ExecuteUpdate` in the Grist plugin and ran two statements against
    the same column of the same table (`_columns`, whose `choices` attribute is `VARCHAR[]`):
    ```
    UPDATE … SET label   = 'Stat'                  ->  setColumnCount=1  of 2  ->  [label, col_ref]
    UPDATE … SET choices = ['open','doing']        ->  setColumnCount=17 of 18 ->
        [choices, table_id, col_id, label, type, duckdb_type, type_known, kind, is_writable,
         is_formula, formula, ref_table, timezone, description, is_hidden, col_ref, parent_pos, col_ref]
    ```
    ⚠ Note `col_ref` appears **twice** — once as SET column 15 and once as the trailing key. That is
    consistent with `BindExtraColumns(all_columns)` adding every physical column including the one the key
    is derived from; worth a look while you are in there, because a provider that maps SET columns by NAME
    will see a duplicate.
  - **⚠ IT IS NOT THIS REPO'S BUG — the forwarding is faithful.** `FabricatorCatalog::PlanUpdate`
    (`src/catalog/fabricator_catalog.cpp:626`) calls `FabricatorFillUpdateSetColumns(op.table, op.columns,
    op.expressions, target)` (`:590`), and `set_count = target.set_columns.size()`
    (`src/dml/fabricator_modify.cpp:35`). So we send exactly what `LogicalUpdate::columns` holds. **DuckDB
    put 17 in there.**
  - **THE CAUSE, in DuckDB core** — `src/catalog/catalog_entry/table_catalog_entry.cpp`,
    `TableCatalogEntry::BindUpdateConstraints`:
    ```cpp
    // we also convert any updates on LIST columns into delete + insert
    for (auto &col_index : update.columns) {
        auto &column = GetColumns().GetColumn(col_index);
        if (!column.Type().SupportsRegularUpdate()) { update.update_is_del_and_insert = true; break; }
    }
    if (update.update_is_del_and_insert) {
        physical_index_set_t all_columns;                      // every physical column
        LogicalUpdate::BindExtraColumns(*this, get, proj, update, all_columns);
    }
    ```
    ⚠⚠ **`LogicalType::SupportsRegularUpdate()` (`src/common/types.cpp:706`) IS NOT VIRTUAL AND TAKES NO
    CATALOG** — it is a switch on the type id: `LIST` / `ARRAY` / `MAP` / `UNION` / `VARIANT` / `GEOMETRY`
    are false unconditionally, `STRUCT` is false if any child is. So **there is no flag a catalog entry can
    set** to declare "my nested types update fine", which is the first thing one reaches for. The rule is
    about DuckDB's own row-group storage — you cannot patch a LIST in place — and it has nothing to say
    about a provider that updates a list column BY NAME over a wire protocol.
  - **✅ THE HOOK THAT *IS* AVAILABLE: `TableCatalogEntry::BindUpdateConstraints` IS `virtual`**
    (`src/include/duckdb/catalog/catalog_entry/table_catalog_entry.hpp:120`), and **`grep -rn
    BindUpdateConstraints src/` over THIS repo finds nothing** — so the fabricator table entry inherits the
    base and gets all of the above. An override is the fix. For a fabricator table all four base reasons
    are vacuous or handled elsewhere:
    | base step | for a fabricator table |
    |---|---|
    | CHECK constraints -> `BindExtraColumns(check.bound_columns)` | fabricator tables declare no constraints |
    | `update.return_chunk` -> all columns | `PlanUpdate` already throws *"fabricator: UPDATE ... RETURNING is not supported yet"* |
    | index columns -> del+insert | no LOCAL indexes on a remote-backed table |
    | **LIST columns -> del+insert** | ⚠ **the only one that fires**, and the reason does not apply |
  - **TWO MEASUREMENTS THAT MAKE THE OVERRIDE SAFE, both worth re-running rather than trusting:**
    - **`update_is_del_and_insert` is read NOWHERE in this repo** (`grep -rn update_is_del_and_insert src/`
      is empty). Nothing downstream of the planner branches on it, so leaving it false changes no
      fabricator behaviour — it only changes how many columns the binder projects.
    - **The OTHER `SupportsRegularUpdate` call site is not the bind path.**
      `LogicalUpdate::RewriteInPlaceUpdates` (`src/planner/operator/logical_update.cpp:51-70`, sets the flag
      at `:198`) is plan-DESERIALIZATION compatibility — its own comment reads *"Okay, we are reading an old
      plan version that has in-place updates for a type that no longer supports it."* It cannot fire for a
      freshly bound statement, but it WILL fire for a deserialized plan, so an override must not assume the
      flag is always false at execution time.
  - **⚠ WHAT TO VERIFY BEFORE SHIPPING THE OVERRIDE** — each of these is the base behaviour you would be
    dropping:
    - `GetStorageInfo(context).index_info` really is empty for a fabricator table (the index branch);
    - the `constraints` vector really is empty (the CHECK branch) — if a fabricator table can ever carry a
      CHECK, that branch must be KEPT, not skipped;
    - keep the `return_chunk` branch as-is rather than deleting it: `PlanUpdate` refuses RETURNING today,
      and an override that silently stopped projecting the row would turn a clean refusal into a wrong
      answer the day RETURNING lands;
    - **MERGE.** `src/catalog/fabricator_merge_into.cpp:23` says *"the only thing that had to move was WHERE an
      UPDATE reads its SET values from (see `FabricatorFillUpdateSetColumns`)"* — so a `WHEN MATCHED THEN
      UPDATE` setting a LIST column goes through the same binding. Test it explicitly;
    - `FabricatorModifyTarget::set_child_indices` is documented **NOT positional** (`SET x = DEFAULT`
      contributes no projection column; a MERGE shares one projection across actions). Changing how many
      columns get bound is exactly the kind of change that invalidates a "not positional" assumption — the
      comment at `src/include/dml/fabricator_modify.hpp:48-53` is the one to re-read.
  - **THE DOWNSTREAM SYMPTOM, for a reproducer.** In `fabricator-grist`, `_columns` is a writable metadata
    table whose `choices` attribute is `VARCHAR[]`. Because the whole row arrives, the plugin's
    "is this cell actually changed?" diff has to cover EVERY column, and a single field missing from that
    switch turns a supported statement into a refusal:
    ```
    UPDATE g."doc"._columns SET choices = ['open','doing','done'] WHERE table_id = 'T' AND col_id = 'Status';
    -- grist: '_columns.parent_pos' is not writable
    ```
    ⚠ The plugin can and will work around it (that diff is exactly what it is for) — the point is that
    **every provider with a writable wide table has to**, and none of them can see why the row grew.
  - **⚠ THE LESSER FIX, if the expansion has to stay:** forward `update_is_del_and_insert` over the ABI on
    `execute_update`, so a provider can DISTINGUISH a user-written 17-column SET from an expanded 1-column
    one. Strictly worse than not expanding — the provider still receives and must ignore 16 columns — but
    it is additive, it needs no behaviour change, and it is the only thing that helps the cases where the
    expansion is legitimate and unavoidable (RETURNING, CHECK constraints, real indexes).
  - **⚠ Measured against `build/release` on `v1.5-variegata` (`dfa6872`); `fabricator-grist` compiles
    against pin `afda880`.** The plugin-side half (the missing diff field) is being fixed in that repo
    independently and does NOT depend on this — the two are worth keeping separate, because the plugin fix
    is needed anyway for the paths that expand for legitimate reasons.


  - **✅ BUILT 2026-09-15 — `FabricatorTableEntry::BindUpdateConstraints` overrides the base, keeping every
    rule except the LIST one. C++-only, NO ABI change. Gate: a NEW hermetic suite
    `verify_update_set_columns` (**32**), floors 9410 → **9442** and MIN_SUITES 77 → **78**, one mutant.**
    - **MEASURED, on the 5-column Delta table the analysis above describes** — and the `EXPLAIN (FORMAT
      json)` split is sharper than the ASCII tree, which is what the gate uses: **before, the scan's
      `"Projections"` renders as a JSON ARRAY and the PROJECTION carries FOUR `#N` bound references between
      the assigned value and the rowid; after, it is the scalar string `"Projections": "id"` and the
      projection is `['q'], rowid`** — identical in shape to the scalar-SET control.
    - **⚠ THE `col_ref` DUPLICATE THE ANALYSIS FLAGGED GOES WITH IT, at no extra cost.** The scan's
      `Projections: id, id, …` collapses to `id`, because the duplicate was the key column appearing once
      as an expanded SET column and once as the key. It was never a second defect — it was this one seen
      from the scan side.
    - **⚠⚠ AND IT MAKES NO IN-TREE STATEMENT START WORKING, WHICH IS THE HONEST FRAMING.** Every type that
      trips the base rule (LIST/ARRAY/MAP/UNION/VARIANT/GEOMETRY, and a STRUCT containing one) is ALSO a
      type the Delta provider cannot read as a SET value — see **limitation 1.26**, found by building this
      and deliberately NOT folded in. So on Delta the statement failed before and fails after, 16 unwanted
      columns earlier. The value is for a provider that CAN write such a column (grist), plus not shipping
      a row's worth of columns nobody asked for.
      - ⇒ **that is why the gate asserts the PLAN and not an answer, and the suite says so.** It is also why
        the two are separate commits: bundling the provider fix would have made this change's
        behaviour-neutrality claim untestable — the same reasoning the EW pin and the SqlServer discovery
        fix were split under.
    - **⚠ EVERY OTHER BASE BRANCH IS REPRODUCED VERBATIM, and each for a stated reason rather than because
      it is unreachable** — the CHECK loop (a fabricator table carries no constraints today, so it is a
      no-op over an empty vector), the `return_chunk` branch (⚠ `PlanUpdate` refuses RETURNING, but at
      PHYSICAL planning, i.e. AFTER this runs — deleting it would turn a clean refusal into a wrong answer
      the day RETURNING lands), and the index loop (`GetStorageInfo` returns a default `TableStorageInfo`,
      so `index_info` is empty — there are no LOCAL indexes on a remote-backed table). All three verified
      in this tree rather than assumed, and the .cpp remark says to re-verify rather than trust it.
    - **⚠ THE MUTANT IS THE DELETED LOOP, RESTORED: it dies at §2's first row after exactly 5 assertions —
      the §1 scalar baseline — with the pre-fix array rendering.** That baseline is load-bearing: without
      it, "the projection is narrow" would pass equally on a build where the plan had stopped projecting
      anything at all.
    - ⚠ §3 asserts MAP and a STRUCT-CONTAINING-A-LIST beside the LIST case, because `SupportsRegularUpdate`
      covers six type ids and recurses into struct children — a build special-casing LIST alone passes §2
      and fails there. §4 pins a STRUCT of SCALARS as a CHARACTERIZATION (it returns TRUE, so it never
      expanded and reads the same either way), so the sections above cannot be misread as "nested types are
      special to us": the split is DuckDB's.
    - ⚠ MERGE is asserted separately (§5), as the checklist demanded — `WHEN MATCHED THEN UPDATE` lowers to
      the same `LogicalUpdate` and the same `FabricatorFillUpdateSetColumns`, but shares ONE projection
      across its actions. ⚠ Its plan has TWO scans (target and source); the target's is the one asserted.
    - ⚠ `set_child_indices` is UNAFFECTED, which was the sharpest item on the pre-ship list: it is filled
      from each bound expression's own `BoundReferenceExpression::index`, never from an ordinal, so fewer
      bound columns changes the indices consistently rather than shifting them. §6's two answer rows are
      the check — a multi-column SET and a struct SET, both landing correctly.
    - ⚠ **The lesser fix (forward `update_is_del_and_insert` over the ABI) was NOT taken and should stay
      unbuilt unless the expansion has to come back**: it would leave the provider receiving and ignoring
      the extra columns, and the cases where the expansion is legitimate (RETURNING, CHECK constraints,
      real indexes) are exactly the branches this override KEEPS.
    - ⚠ Two traps re-earned while measuring it: **`EXPLAIN` cannot be a subquery source NOR a `COPY`
      source** (so the JSON plan cannot be written to a file and parsed — the regex on the `physical_plan`
      row is the only route), and a restored mutant must be written by something that UPDATES THE MTIME or
      ninja skips the rebuild and the next run silently re-measures the mutant.

  - **✅ AND THE PROVIDER-SIDE GAP IT EXPOSED IS FIXED TOO — SAME DAY, ITS OWN COMMIT (user-directed: *"take
    the Arrow-passthrough fix"*). `UPDATE … SET <a LIST / MAP / nested column>` now WORKS on Delta, on both
    update paths. C#-only, no ABI, no C++. Gate `verify_update_set_columns` 32 → **53**, floor 9442 →
    **9463**. Limitation 1.26 lived for about three hours.**
    - **⚠⚠ THE REFUSAL WAS A CLR ROUND TRIP, NOT A PROPERTY OF THE FORMAT OR THE WRITER — which is what the
      user's question forced out ("with native_write there should be no limits at all"), and they were
      right.** `native_write` only decides who writes the parquet BYTES; the refusal happened earlier, while
      assembling the post-image IN MANAGED CODE. Both paths boxed every SET value and rebuilt an Arrow array
      from the boxes, and neither ladder (`ReadScalar` out, `BuildArray` back) has a list case.
    - **⚠ THE ROUND TRIP EXISTS FOR ONE REAL JOB AND THE CODE SAYS SO** — a TARGET-TYPE conversion, so an
      incoming INT32 or a differently-united timestamp still lands as the column's declared type. ⇒ the fix
      is to do it only when there IS something to convert: **when the incoming Arrow type already matches
      the target STRUCTURALLY, hand the array through.** `BuildArray` still has NO list case, deliberately —
      a list needing a genuine conversion is REFUSED rather than silently rebuilt through boxes.
    - **⚠⚠ THE TWO PATHS NEEDED DIFFERENT FIXES, AND ONLY ASKING "where is ReadScalarDeep actually used?"
      FOUND THE SECOND ONE.** It has exactly TWO call sites, both in the Delta UPDATE: `ExecuteUpdate`
      rebuilds a SET column (a straight PASSTHROUGH), while `BufferUpdateRows` — the EXPLICIT-TRANSACTION
      path — MERGES the new values with the untouched rows ROW BY ROW, which is a CONCATENATE + TAKE, not a
      passthrough. ⇒ fixing only the first would have made one statement work in autocommit and fail inside
      `BEGIN … COMMIT`: the "honoured on some writes and not others" asymmetry this file records as harder
      to notice than a feature that never works (the `delta.checkpointInterval` lesson).
    - **⚠ A THIRD SITE HID IN THE NOT NULL CHECK, and it fired FIRST.** `DeltaNullability.ValidateSetValue`
      inspects a value's CONTENTS for exactly one shape — a struct's children — and for everything else acts
      on nothing but null-ness. It boxed regardless, so a list died in a NULLABILITY CHECK before the write
      path had any say. It now boxes only for a STRUCT and asks the Arrow array otherwise. ⚠ That puts every
      scalar column's NOT NULL enforcement on a new code path, which is why §8 exists as its control.
    - **⚠ NESTED FIELD NAMES ARE COMPARED LOOSELY, AND THAT IS THE ONE JUDGEMENT CALL.** Arrow labels a
      list's child `item` while the parquet spec calls it `element`, so the two converters that meet here
      (DuckDB's export, engineered-wood's Delta→Arrow) can describe ONE `VARCHAR[]` and disagree on a LABEL
      while the layout is identical. The passthrough asks "may I hand this array over?", for which a label
      is irrelevant. ⚠ MEASURED rather than assumed: the passthrough FIRES, so on this pin they agree — the
      loose comparison is insurance, not the thing that made it work.
    - **✅ IT CLOSED THE `Host.cs:351` FOLLOW-UP INSTEAD OF ADDING A THIRD COPY.** `IArrowType.Equals` is
      REFERENCE equality, so every caller needing real equality hand-writes one, and that note already said
      consolidating `SqlServerCdcReader.SameType` into the bridge was the right move. It is now
      `Fabricator.Common/ArrowTypeCompare.SameType(a, b, namesMatter)` — ⚠⚠ **the flag is not a convenience:
      CDC drift detection wants names to MATTER (a renamed field IS the drift) and the passthrough wants
      them IGNORED, so there is deliberately no default.**
    - ⚠ NOT mutation-tested, and the suite says what stands in its place: the same statements were MEASURED
      failing before the change and passing after, and each leg is structurally forced onto its own path (a
      `BEGIN … COMMIT` cannot reach the autocommit rebuild). ⚠ The untouched rows in §7 are the real control
      — a wrong index in the concat+take would corrupt THOSE, not the updated cells.

- **⚠⚠ SESSION RECORD 2026-09-12 — FOUR COMMITS, PUSHED (`59277bf..0a22d8b` on `v1.5-variegata`), NOTHING
  LEFT OPEN. ⚠ `git log` is the authority, not this prose.**
  - `43a8991` fluid_query_inout (message amended: the projection hint was NOT shared with the collector then)
  - `61c6575` **projection pushdown through the STREAMING in-out exchange** — C++-only, NO ABI bump.
    Hermetic **76/76 — 9153**, service **59/59 — 3483**. Gates `verify_plugin_fluid` §36 (810) +
    `verify_global_functions` (178). Full record: [docs/fluid-templating.md](fluid-templating.md) §37.
  - `19a207c` doc-only: limitation 1.25 corrected (see below).
  - `0a22d8b` **the in-out gate deadlock FIXED** — released from the local state destructor. Hermetic
    **76/76 — 9166**, service **59/59 — 3483** (unchanged, which is the `_each` claim).
  - **⚠ `duckdb` shows ` m` and must NEVER be staged** — it is the user's own `CREATE_NEW`/`ExclusiveCreate`
    work.
  - **✅ THE LIMIT DEADLOCK IS FIXED (limitation 1.25 / fluid-templating.md §37.5-§37.7)** — released from
    the in-out's LOCAL STATE DESTRUCTOR; no new primitive, no thread-identity assumption, `FinishEof`
    untouched. Gate `verify_global_functions` 178 → **191**, one mutant dying at the `LIMIT 1` row.
    **⚠⚠ THE FIRST ATTEMPT (owner tracking) WAS STRICTLY WORSE THAN THE BUG** — built, measured, reverted.
    The measurements below are what made the second attempt small, and what a THIRD must not re-derive:
    - the trigger is a LIMIT satisfied BEFORE the operator is pulled again (`LIMIT 10` over 4 rows is FINE);
    - `FinishEof` runs on a FOREIGN thread almost always (`verify_plugin_fluid`: 15 calls, `owns=1` ZERO);
    - `std::mutex` can only be unlocked by its owner, and the gate is a TOKEN whose holder may never run
      again ⇒ the PRIMITIVE is wrong, not the tracking;
    - **a gate MUST cover the foreign-thread case (a LIMIT over a parallel `UNION ALL`)** — every
      single-branch probe was green while the fluid suite hung, which is what made the bad fix look right.
    - **⚠⚠ THE TOKEN DESIGN (mutex + condvar + a `taken` flag) WAS NOT NEEDED, and the measurement that
      retired it is the transferable part: `~FabricatorExchangeLocalState` runs BEFORE `OperatorFinalize`
      AND ON THE THREAD THAT TOOK ITS OWN LOCK** (3/3 single-branch, 3/3 parallel). The second half is what
      makes `unlock()` legal there — had the destructor been foreign, `std::mutex` would have been unusable
      and the condvar swap would have been forced. **Measure the release POINT before designing the release
      MECHANISM.**
    - **⚠⚠ THE TWO MULTI-BRANCH SHAPES ARE NOT THE SAME THING, and the first version of the gate conflated
      them (user-caught: *"did you test a multi branch input (union alls) as well"*).** A `UNION` of
      SEPARATE in-out CALLS gives each call its OWN gate — MEASURED with a (gate address, thread id)
      instrument: **3 gates, 1 thread each**, i.e. NO contention, though it stays load-bearing for the
      FOREIGN-THREAD property. The CONTENDED shape is a multi-branch `UNION` as the **INPUT** to ONE call:
      **1 gate, 3 threads**, which is also the shape the original 4g table-in-out work found problematic.
      Both are in the gate now, each labelled with what it does and does not prove.
    - **⚠⚠ CONTENTION MUST BE VERIFIED, NEVER ASSUMED** — `PhysicalUnion::BuildPipelines` may run branches
      SEQUENTIALLY, so a green test over a union that happened to run on one thread proves nothing. The
      (gate, tid) instrument is the cheap check; without it this gate was vacuous twice over. ⚠ Twice this
      session a "parallel" test proved nothing and BOTH times the USER caught it, not me.
  - **ENVIRONMENT NOTES PAID FOR THIS SESSION** (each cost a wrong result): build C++ ONLY via the
    PowerShell tool with an absolute-path `.bat` calling vcvars64 (`cmd /c` from Git Bash exits 0 having
    built NOTHING); kill any leftover `unittest.exe` before a build or it fails `LNK1104`; `verify_plugin_fluid`
    needs `FABRICATOR_PLUGIN_DIR` at an EMPTY dir or it fails on the provider list; and **never `^`-anchor a
    grep over unittest output** — it writes progress without a trailing newline, which silently hid the
    `op LOCK` line and cost a wrong mechanism.

- **⚠⚠ THE INVALID-VIEW ENUMERATION FIX IS COMMITTED as `59277bf` (2026-09-11) and UNPUSHED. Its full
  record is the "AN INVALID VIEW CRASHED CATALOG ENUMERATION" entry further down; this is the handoff so a
  fresh session does not re-derive it.**
  - **SIX FILES**: `src/catalog/fabricator_schema_entry.cpp` (snapshot the names + per-table catch +
    rethrow INTERRUPT/FATAL/INTERNAL), `dotnet/Fabricator.SqlServer/SqlServerTable.cs` (classify 208 by
    asking `OBJECT_ID`), `test/verify_invalid_objects.test` (NEW, 36), `scripts/run-suites.sh` (floors),
    `docs/known-limitations.md` (1.24), `README.md` (the Catalog Integration note). ⚠ `duckdb` stays
    MODIFIED and must NEVER be staged — that is the user's own `CREATE_NEW`/`ExclusiveCreate` work.
  - **MEASURED ON THE SHIPPING BINARY** (both tiers re-run AFTER the rethrow guard was added, because the
    first pair had measured the pre-guard build): hermetic **76/76 — 9088** (unchanged) and service
    **59/59 — 3483** (3447 + 36 exactly). Floors already bumped to 59 / 3483.
  - ⚠ **A SEQUENCING TRAP PAID FOR HERE: do not rebuild C++ while a tier is running.** The link failed with
    `LNK1104` because the running `unittest.exe` was locked — which was LUCKY: had it succeeded, both
    in-flight tiers would have silently switched binaries mid-run and their results would have been void
    without saying so. Same family as the recorded leftover-`duckdb.exe` lock, but the failure mode is a
    VOID MEASUREMENT rather than a failed build.
  - ⚠ Non-finding, recorded so nobody re-investigates: the user's `readonly true` ATTACH option IS honoured
    (DuckDB accepts that spelling for `READ_ONLY` — measured: a CREATE is refused with *"attached in
    read-only mode"*). I asserted the opposite from the options list WITHOUT measuring and had to retract it.

- **⚠⚠ THE 2026-09-08 USER-DIRECTED PAIR IS DONE — BOTH (A) and (B). (A) the `column_default` read-back
  (its record is the entry below, `581b598`), and (B) the ALTER gates (this entry). It also produced two
  findings recorded separately: the upstream-issues §7 RETRACTION — there was no DuckDB bug — and the
  `KeepNulls` NULL-replacement defect, limitation 1.23, which is NOT fixed and wants its own change.**

  **⚠ AND THE `KeepNulls` DEFECT IT FOUND IS ALSO FIXED (2026-09-10, `mssql_keep_nulls`, entry below) —
  as a SETTING, because it is not solveable as a policy.**

  **⚠ WHAT IS STILL OPEN:** the ONE residual of that setting (`VALUES (…, DEFAULT)` on an EXPRESSION
  default stores NULL under the new default — inherent, since the binder collapses it with an explicit
  NULL); `ALTER` under CDC (a captured column cannot be renamed at all, `Msg 4928` — unasserted); and the
  warehouse leg of both ALTER gates plus `supports_default_constraints`, all UNMEASURABLE here for want of
  a live warehouse.

  **(B) IS BUILT — a NEW SERVICE SUITE `test/verify_alter_mssql.test` (**63**), covering rename table,
  rename column, drop column, change type and SET/DROP NOT NULL. Floors 3316 → **3379** and MIN_SUITES
  56 → **57**. TWO mutants, each killed at its own row. SQL Server's ALTER now has TWO gates
  (`verify_alter_default` owns DEFAULT, `verify_comment_on_mssql` owns COMMENT ON, this owns the rest);
  before 2026-09-08 it had NONE, which is how a silently-dropped DEFAULT survived to be user-reported.**
  - **⚠⚠ THE TWO LOAD-BEARING SECTIONS ASSERT WHAT THE DDL MUST *NOT* HAVE CHANGED, which is the whole
    reason the kinds were worth gating rather than smoke-testing.** SQL Server's `ALTER COLUMN` defaults to
    NULLable when the statement is silent AND must restate the type, so the emitter reconstructs both from
    the catalog. Each loss is a SILENT data-model change that "did the statement succeed" cannot see:
    - **§3 — CHANGE TYPE PRESERVES NULLABILITY.** Mutant 1 (drop the restatement) dies at the nullability
      row after 25 pass, with `k bigint 1`: the type changed CORRECTLY and the NOT NULL was gone, every
      existing row still satisfying it. Backed by a behaviour row (a NULL insert must still be refused).
    - **§4 — SET/DROP NOT NULL PRESERVES THE TYPE.** Mutant 2 (`.dataType` instead of `.fullType`, which is
      the natural slip — both are fields of the same tuple) dies at the precision row after 30 pass, with
      `dec1 decimal 18 0` where `9 2` is expected.
  - **⚠⚠ THE ORDER INSIDE §4 IS LOAD-BEARING AND MEASURING IS WHAT SET IT — the two column kinds fail
    DIFFERENTLY, and my first write-up had the hazard backwards.** I had recorded "reconstructing `varchar`
    without its length yields varchar(1) and TRUNCATES". MEASURED: it does NOT truncate — SQL Server
    REFUSES the statement (*"String or binary data would be truncated"*) because the existing 40-char value
    does not fit. The DECIMAL is the silent one: `decimal(9,2)` → `decimal(18,0)` SUCCEEDS and turns 123.45
    into **123**. ⇒ with the varchar first its loud failure MASKS the decimal case and mutant 2 dies on a
    `statement ok` rather than on the corruption it causes. Decimal first, and it dies on the precision row.
    **This is "order the assertions so mutants separate" again, and the ordering could only come from
    running both.**
  - **⚠ BOTH SIZED COLUMNS ARE CREATED SERVER-SIDE, and the section says why: a DuckDB `VARCHAR` carries no
    length**, so a catalog-created column arrives as `nvarchar(MAX)` and the sized-varchar half is
    UNREACHABLE through the catalog — the section would pass vacuously. `fabricator_exec` + a
    `fabricator_refresh_cache` is what makes it expressible.
  - ⚠ Everything is asserted ON THE SERVER (`sys.columns`/`sys.objects`/`sys.types` through a
    `fabricator_query` view) as well as through DuckDB's view, because a rename that only re-keyed our cache
    would pass a DuckDB-only assertion. The RENAME sections assert BOTH names in one query (new present, old
    gone), so a build that added an entry without evicting the old one fails rather than half-passes.
  - ⚠ RENAME goes through **`sp_rename`, not `ALTER TABLE … RENAME`**, so those sections pin a different
    code path; `@objname` may be schema-qualified while `@newname` must not be. §6 does the TABLE rename
    LAST, because it invalidates the `cols` view every earlier section reads.
  - ⚠ §5 pins the rolled-back ALTER, recorded-but-unasserted until now: the ALTER paths re-fetch columns
    EAGERLY on the transaction's own connection (the dbt-incremental deadlock fix), so a rolled-back ALTER
    has already put the UNCOMMITTED schema in the cache — asserted on both the server and the host's view.
  - **⚠ THE `statement error` TEXT IS PINNED, not left empty.** The NULL-insert refusal surfaces as
    SqlBulkCopy's own client-side *"does not allow DBNull.Value"* rather than a server error — still
    evidence, because SqlBulkCopy reads the destination column's nullability from the server to make it, but
    an empty expectation would have accepted ANY failure.
  - ⚠ NOT covered, and the suite does not imply otherwise: `ALTER` under CDC (a captured column cannot be
    renamed at all — `Msg 4928`), and the warehouse engines.

- **⚠⚠ THE `column_default` READ-BACK — BUILT 2026-09-09 (user-directed; the LITERALS-ONLY shape put to the
  user and chosen). C++ + C#, NO ABI bump (one ADDITIVE key on the `table_info` doc, the comments'
  precedent). `information_schema.columns.column_default` and `duckdb_columns()` now report a SQL Server
  column's default. Gate `verify_alter_default` 59 → **82**, service floor 3293 → **3316**, TWO mutants each
  killed at its OWN row — and the FIRST one SURVIVED, which is where the value of the pass is.**
  - **⚠⚠ LITERALS ONLY, AND IT IS A SEMANTIC DECISION WITH NO SAFE MIDDLE.** `bind_insert.cpp`'s
    `ExpandDefaultExpression` SUBSTITUTES `column.DefaultValue().Copy()` for a column the statement OMITS, so
    reporting a default stops the SERVER applying its own and starts DuckDB sending a copy of the expression.
    Equivalent for a literal; for `getdate()` it is the CLIENT's clock — a silently different row — and for a
    function DuckDB lacks it is a bind error on a statement that works today.
    - **⚠ "Report it but stop the binder using it" IS NOT AVAILABLE, checked rather than assumed**: one grep
      over `HasDefaultValue()`/`DefaultValue()` shows display (`duckdb_columns`, `pragma_table_info`, the
      CREATE-SQL renderer) and semantics (`bind_insert` ×2, `bind_create_table`, `data_table`, `appender`,
      the C API) reading the SAME field.
    - **⚠⚠ THE RULE IS DuckDB'S OWN LINE, not ours**: `transform_alter_table.cpp` splits `ADD COLUMN …
      DEFAULT` on `ExpressionClass::CONSTANT`. Corroborated on a NATIVE table, where `DEFAULT true` and
      `DEFAULT DATE '…'` render as `CAST('t' AS BOOLEAN)` / `CAST('2024-01-01' AS "DATE")` — casts, not
      constants. So `ApplyColumnDefault` parses the provider's text and keeps a single CONSTANT, dropping
      everything else; a parse failure is CAUGHT and dropped, because nothing may throw during entry
      materialization (an unreported default is a missing row, a failed materialization is an unreadable
      TABLE).
    - **⚠ WITHHOLDING COSTS NOTHING AT INSERT TIME, measured and gated**: with nothing reported the server
      applies its own default — a withheld `getdate()` column still gets the server's clock on an INSERT
      that omits it. That row is the one the whole rule exists to protect.
  - **DIALECT NORMALISATION IS THE PROVIDER'S, THE POLICY IS THE HOST'S — one policy point.** SQL Server
    hands over `((10))` / `(N'2024-01-01')` / `(getdate())`; `NormaliseDefault` peels BALANCED outer parens
    (a naive starts-with-`(`-ends-with-`)` test would mangle `(1)+(2)` into `1)+(2`, which could then parse
    as something ELSE rather than fail) and the `N` prefix, then sends everything. The host decides.
  - **⚠⚠ THE FIRST MUTANT SURVIVED, AND CHASING IT FOUND A REAL PRODUCT GAP — the most useful result of the
    pass.** Mutant B (honour ANY expression class) passed the whole suite, because the `getdate()` column
    was added OUT OF BAND via `fabricator_exec` and **the provider's database-wide cache had never heard of
    it**: the `(withheld)` row was passing on a stale cache rather than on the rule. ⇒ `fabricator_refresh_
    cache` did NOT drop the comment/default cache, so a comment or default changed out of band — or a whole
    TABLE created out of band — stayed invisible for the life of the ATTACH.
    - **FIXED at the one honest hook: `GetTables()` invalidates it.** `FabricatorCatalog::RefreshCache` calls
      `DiscoverTables`, and that crossing is reached from exactly TWO places — the ATTACH (cache unset
      anyway) and refresh_cache. Entry materialization goes through `table_open`/`table_info`, so it costs
      nothing per enumeration.
    - **⚠ IT FALSIFIED A COMMENT I WROTE THE DAY BEFORE.** The cache's own doc claimed staleness was "the
      same contract `_externalInfo` documents: … shows up on re-ATTACH or after fabricator_refresh_cache" —
      and `_externalInfo` neither documents that nor lives in that file, and the refresh half was simply
      false. Replaced with what is true, plus how it became true.
    - ⇒ the gate needed a PAIR: one out-of-band default that is a LITERAL (must be reported) beside the
      non-literal one (must be withheld). Without it, "withheld" proves nothing — a cache that never heard
      of either column answers withheld for both. Mutant B now dies on the non-literal row (`getdate()`),
      mutant C (drop the invalidation) on the literal row beside it (`(withheld)` where 42 is expected).
  - **⚠ A NEW CAPABILITY, deliberately SEPARATE from the comments' one**: `SupportsDefaultConstraints =>
    !IsWarehouse` (`supports_default_constraints`), because extended properties and DEFAULT constraints are
    different catalog surfaces and a future engine may have one without the other. UNMEASURED on a warehouse,
    conservative by choice, and a capability gate rather than a probe for the recorded §6.5 reason.
    ⚠ It broke `verify_server_profile`'s property COUNT (17 → **18**) — which is the point of that
    assertion, and the SECOND time in two days it has caught exactly this. Bump it in the same commit.
  - ⚠ Delta reports NONE (`ColumnDefaults()` left at its null-returning DIM) because engineered-wood
    implements no part of the column-default writer feature — the same reason the WRITE side refuses there.
    Asserted, so the two halves cannot drift.
  - ⚠ `verify_alter_default` §5 was a CHARACTERIZATION pinning the absence; it is REPLACED, not deleted,
    because falsifying it is the change announcing itself. Same treatment as `verify_comment_on_mssql` §5.

- **⚠⚠ `mssql_keep_nulls` — A NULL IS WRITTEN AS NULL NOW, DEFAULT TRUE. BUILT 2026-09-10 (user-directed:
  *"i think this is not solveable we should make KeepNulls configureable"* — and the premise is right, it is
  not solveable as a policy). C#-only, NO ABI change. **BEHAVIOUR-CHANGING**: the previous behaviour was the
  replacement. Gate: a NEW service suite `verify_keep_nulls` (**68**), floors 3379 → **3447** and MIN_SUITES
  57 → **58**, THREE mutants each killed at its own section. Full record: limitation 1.23 (rewritten from
  a defect row into a fixed-by-setting row) + the README's settings and ATTACH tables.**
  - **THE DEFECT IT CLOSES, measured with the control that isolates the write path:** through the catalog
    `INSERT … VALUES (4, NULL, NULL)` stored `10` and a `getdate()` timestamp, while the byte-identical
    statement as plain T-SQL through `fabricator_exec` stored `NULL, NULL`; and a COPY of a CSV with a
    **BLANK field** stored `99`. `SqlBulkCopy` without `KeepNulls` replaces a source NULL with the
    destination column's DEFAULT, so this provider disagreed with plain SQL on the commonest spelling there
    is — silently, on both INSERT and COPY.
  - **⚠⚠ IT IS A SETTING BECAUSE NO POLICY CAN BE RIGHT: DuckDB's binder COLLAPSES an explicit NULL and an
    explicit `DEFAULT` keyword into the SAME typed NULL** (`ExpandDefaultExpression`) before our sink sees
    the chunk. TRUE serves the explicit NULL; FALSE serves `VALUES (…, DEFAULT)` on a default we do not
    report. **There is no third option** — by the time `PlanInsert` runs the substitution has happened, so
    we cannot even REFUSE the ambiguous case, which would otherwise have been the better answer.
  - **⚠⚠ TWO OF THE THREE SURFACES THE USER ASKED FOR DO NOT EXIST, and both were established by
    measurement rather than argued:**
    - a **COPY option** is NOT EXPRESSIBLE — `COPY … FROM … (…)` options are consumed by the SOURCE format,
      and the CSV reader refuses `keep_nulls` by name before anything reaches our insert side;
    - a **CTAS `WITH` option** would parse (that bag is ours) but is VACUOUS: a CTAS-created table carries
      **ZERO** default constraints (measured), and no statement both creates a table with defaults and
      bulk-loads into it. DuckDB's `INSERT` has no options clause at all.
    - ⇒ the buildable shape is the established pair: `SET mssql_keep_nulls` + a `keep_nulls` ATTACH option,
      precedence SET ?? ATTACH ?? true, exactly like `materialize` / `read_isolation` / `command_timeout`.
  - **⚠ THE DEFAULT IS THE CONFORMANT DIRECTION AND DIVERGES FROM SqlBulkCopy's OWN DEFAULT deliberately**
    (user decision, offered against keeping the old behaviour): plain T-SQL stores NULL when you write NULL.
    The cost is exactly ONE cell — `VALUES (…, DEFAULT)` on an EXPRESSION default now stores NULL — and it
    is pinned in the gate as §1 row 3 beside §4 row 9, i.e. the same spelling under both values, which is
    the only honest way to show a TRADE rather than a fix.
  - **⚠⚠ AN OMITTED COLUMN IS UNAFFECTED BY EITHER VALUE, and getting this wrong cost a day.** DuckDB's
    `column_index_map` leaves it out of the batch entirely, so it never becomes a NULL to replace and the
    server applies its own default — measured on BOTH values, including for a withheld default, and pinned
    as a CHARACTERIZATION (§6) that no mutant of ours can kill.
    - **⚠ MY EARLIER CLAIM THAT `KeepNulls` "WOULD BREAK THE OMITTED-COLUMN CASE" WAS WRONG, and it is the
      reason this looked unfixable.** I read `ExpandDefaultExpression`, saw "a column with no reported
      default gets a typed NULL", and never checked WHICH CALLER reaches it — it serves the explicit
      `DEFAULT` KEYWORD (via `TryReplaceDefaultExpression` on `ExpressionType::VALUE_DEFAULT`), not
      omission. **The lesson is this file's most-repeated one: a mechanism assembled from a function whose
      callers you have not read.** One probe build settled it in minutes, and the wrong version had already
      reached a commit message, a limitations row and this file.
  - ⚠ Applied to INSERT, CTAS and COPY alike — the semantic does not vary by statement kind — and it is a
    NO-OP on a CTAS by construction (no defaults on a fresh table). The two paths it changes are INSERT and
    COPY into an existing table.
  - ⚠ Two gate mechanics worth reusing: **`require-env FABRICATOR_DELTA_WRITE_DIR` is what makes
    `${…}` INTERPOLATE** (without it sqllogictest leaves the literal in the path and the COPY fails naming
    the variable), and the ATTACH-option section must come BEFORE any `SET`, because a SET outranks the
    option and would make that test vacuous.
  - ⚠ `duckdb_settings()` shows it as `(unset)` until someone sets it, exactly like `mssql_materialize` —
    the effective TRUE comes from the resolver, not from DuckDB's registry. Pinned as a pair so nobody reads
    "(unset)" as "off".

- **⚠⚠ `COMMENT ON TABLE` / `COMMENT ON COLUMN` — BUILT 2026-09-08 (user-asked "i think we don't support
  this yet?", then "yes build it"). C++ + C#, NO ABI bump (two ADDITIVE kinds on `table_alter`).
  WRITE-ONLY by decision; the read-back is slice 2. Gates: a NEW hermetic suite `verify_comment_on` (**39**)
  + a NEW service suite `verify_comment_on_mssql` (**39**), tier-0 252 → **258**, TWO mutants each killed at
  its own row. Full records: the README's DDL note, [docs/known-limitations.md](known-limitations.md)
  1.21, [docs/warehouse-support.md](warehouse-support.md) §6.7.**
  It was refused in BOTH directions before: the write failed with *"fabricator: only ALTER TABLE is
  supported"* and `duckdb_tables().comment` was NULL even for a comment already set on the server.
  - **⚠⚠ IT ARRIVES AS ITS OWN `AlterType`s, NOT AS AN ALTER TABLE VARIANT — which is the whole reason it was
    refused and the one structural fact to know.** `COMMENT ON TABLE` is `AlterType::SET_COMMENT`
    (`SetCommentInfo`: `entry_catalog_type` + `comment_value`) and `COMMENT ON COLUMN` is
    **`AlterType::SET_COLUMN_COMMENT`** (`SetColumnCommentInfo`: `column_name` + `comment_value`) — two
    DIFFERENT types, and NEITHER is an `AlterTableInfo`. So they must be dispatched BEFORE
    `FabricatorSchemaEntry::Alter`'s `info.Cast<AlterTableInfo>()`, which is why they live in their own
    `AlterComment` method rather than as cases in that switch. The old guard was `info.type !=
    AlterType::ALTER_TABLE`, so it caught both and said the one thing that reads as "the statement is wrong".
  - **⚠⚠ THE DELTA COLUMN COMMENT IS A SURGICAL `JsonNode` EDIT OF `metaData.schemaString`, AND A TYPED ROUND
    TRIP WOULD BE THE SILENT-CORRUPTION CLASS.** A column comment lives in the field's `metadata.comment` —
    the SAME dictionary where **column mapping keeps `delta.columnMapping.physicalName` and `.id`**, and
    column mapping is the DEFAULT here. Deserialising the schema into engineered-wood's typed model and
    re-serialising it could drop any key that model does not represent, and losing a physical name makes the
    existing data files unreadable **with nothing failing at the time of the ALTER**. `JsonNode` preserves
    every property it does not touch, so the blast radius is the one key being set. MEASURED preserved, and
    **mutant B (replace the field's metadata object wholesale) dies at exactly the `phys_kept` row after 14
    assertions pass** — which is what makes that row, not the comment row, the load-bearing one.
    - ⚠ The suite proves it from the DATA side too, because a metadata assertion alone would not: values are
      read back, and an INSERT after the rewrite proves the WRITE path still matches. **⚠ `count(*)` proves
      NOTHING on Delta** — it is answered from the log and opens no data file (this file's own recorded trap).
    - ⚠ Matched on the LOGICAL `name`, which is correct BECAUSE column mapping keeps the physical name in
      metadata; and top-level only, because `SetColumnCommentInfo` carries a single column name so a nested
      field is not expressible in the statement.
  - **⚠⚠ IT IS THE OPPOSITE OF THE `ADD COLUMN … DEFAULT` CASE, and that is worth stating because the reflex
    is to expect another Delta refusal.** There, Delta records a column default through the
    `allowColumnDefaults` writer feature and EW implements NONE of it, so the refusal had to stand. Here BOTH
    halves are representable with **NO EW change**: `MetadataAction.Description` is a first-class field, and
    `field.Metadata` is a generic `Dictionary<string,string>` — which is exactly where the protocol puts a
    column comment, so delta-spark reads both back. **Check the far side's surface before assuming the
    previous answer generalises.**
  - **⚠⚠ SQL SERVER NEEDS THREE PROCS, NOT ONE, and it is the engine's shape rather than ours.** A comment is
    the **`MS_Description`** extended property (what SSMS's *Description* box writes, so it interoperates):
    `sp_addextendedproperty` FAILS when the property exists (15233), `sp_updateextendedproperty` FAILS when it
    does not, and removal is a third proc. So `COMMENT ON` is an UPSERT-OR-REMOVE, composed as ONE guarded
    batch (`IF EXISTS(…) EXEC update … ELSE EXEC add …`) — one round trip, and no check-then-act race
    between a separate probe and the write. **Mutant C (only ever add) dies at the upsert row after 14 pass.**
    - ⚠ Guarded on BOTH sides: removing a comment that is not there is a NO-OP, which is what
      `COMMENT ON … IS NULL` does on DuckDB's own tables. Asserted twice so it is idempotence being pinned.
    - ⚠ `minor_id` addresses the COLUMN (0 = the object); the gate resolves it back to a NAME through
      `sys.columns`, because an assertion on the raw ordinal would pass silently if the wrong column were
      commented after a schema change.
  - **⚠ THE WIRE'S `comment` KEY IS REQUIRED WITH A NULLABLE VALUE — the empty-vs-absent trap, THIRD
    occurrence in this class.** `IS NULL` REMOVES a comment, so absent and null mean different things and only
    one of them is expressible by a value alone. Same rule `set_default` follows; the first two were `arg2`'s
    `"-"` vs `"b"`+base64 and `add_column`'s OPTIONAL `default`. Tier-0 pins all of it offline.
  - **⚠ THE WAREHOUSE GATE IS §6.5's RULE APPLIED IN ADVANCE, AND IT IS UNMEASURED BY CHOICE.**
    `ServerProfile.SupportsExtendedProperties => !IsWarehouse` (surfaced as `supports_extended_properties` in
    `fabricator_server_info`). Extended properties are not part of the warehouse surface — user-flagged:
    *"MS_Description for fabric warehouse might not be available yet. Same for fabric lakehouse"*.
    ⚠ **Edition 11 cannot tell a Fabric Warehouse from a Lakehouse SQL endpoint**, so the gate necessarily
    covers both and a measurement on one would not settle the other. ⚠ It REFUSES rather than attempting: on
    those engines a failing statement inside an explicit transaction ABORTS it, so letting the server answer
    costs the caller their whole transaction. ⚠ No live warehouse here, so it is CONSERVATIVE rather than
    measured — §6.7 carries the one-line check to run if one becomes available.
  - **⚠ A DEFECT I WROTE AND CAUGHT BEFORE COMPILING: user identifiers spliced into a `string.Format` FORMAT
    STRING.** The proc arguments were assembled with a `{0}` placeholder while `level2`/schema/table were
    concatenated in BEFORE formatting — so a quoted DuckDB identifier containing `{` would make the format
    string itself malformed. Plain concatenation now. (Escaping proper is fine: doubling the single quote is
    the COMPLETE escape for a T-SQL literal — there is no backslash escaping to also handle — which is what
    makes composing these calls safe for arbitrary comment text. The gate uses `O'Brien's` to prove it.)
  - **⚠ `SET_COMMENT` IS SHARED BY EVERY COMMENTABLE CATALOG TYPE** (TABLE / VIEW / INDEX / SEQUENCE / TYPE /
    MACRO), so `entry_catalog_type` has to be CHECKED rather than assumed — otherwise a provider-declared VIEW
    would be forwarded as though it were a table. Refused by name (measured: *"… not View"*).
  - **⚠ ADDING A ROW TO `ServerProfile.Properties()` BREAKS `verify_server_profile`, WHICH IS THE POINT — and
    it caught me, for the SECOND time in this file's history.** That suite pins the property COUNT
    (16 → **17** here; an earlier addition took it 14 → 15), and every OTHER reader of
    `fabricator_server_info` filters by NAME so none of them notices. ⇒ the count assertion is the ONLY thing
    that sees a capability appear or disappear without its gate being updated, so **bump it in the same
    commit** — and the cost of not doing so is a full service tier: the failing run reports an assertion
    total that is an UNDERCOUNT (a failing suite stops), which this file already forbids using as a floor,
    so the tier has to be re-run rather than reasoned about.

  - **⚠ THE GATE HAD TO READ THE FAR SIDE, because there is no read-back — and the mechanism is reusable.**
    Delta: `read_text` over `_delta_log/*.json`, split on newlines, keep the NEWEST line carrying
    `$.metaData.id` (a metaData action REPLACES the previous one, so an older one says nothing about now),
    then `json_extract_string`. ⚠ **`require json` is LOAD-BEARING** — `unittest` does not auto-load, and
    without it the whole suite fails as a missing FUNCTION rather than a missing REQUIRE. SQL Server:
    `fabricator_query` over `sys.extended_properties`.
  - **⚠ SLICE 1's "STILL OPEN: the READ-BACK" IS DONE — same day. Its guess at the MECHANISM was wrong and is
    worth keeping for that reason: it said comments would ride "the table-schema crossing as Arrow field
    metadata". The Delta COLUMN comments do exactly that (for free), but the channel is `table_info`, and the
    reason is in the slice-2 entry below.**
  - **⚠⚠ SLICE 2 — THE READ-BACK — BUILT 2026-09-08 the same day (user: "continue with read part"). C++ + C#,
    NO ABI bump (two ADDITIVE keys on the `table_info` doc). `duckdb_tables().comment` and
    `duckdb_columns().comment` now report the provider's own comment, including one somebody ELSE set (SSMS's
    Description box, Spark's `COMMENT ON`). Gates: `verify_comment_on` 40 → **53**, `verify_comment_on_mssql`
    39 → **52**; TWO mutants, and the second has a CONTROL that makes it attributable.**
    - **⚠⚠ A COMMENT CANNOT BE LAZY THE WAY STATISTICS ARE, AND THAT SINGLE FACT DETERMINED THE WHOLE
      DESIGN.** `table_stats` exists as a SEPARATE entry the host calls at first SCAN precisely so entry
      materialization — i.e. catalog ENUMERATION — never pays a stats query; `TableSession.StatsJson`'s own
      doc says so. A comment has no such option: DuckDB copies `CreateTableInfo::comment` in the
      `TableCatalogEntry` CONSTRUCTOR (`table_catalog_entry.cpp:31`) and `duckdb_tables()` reads the ENTRY, so
      the value must exist BEFORE the entry does. There is no later hook. ⇒ it rides `table_info`, which IS
      the enumeration crossing, ⇒ **a provider needing IO for it owes a per-CATALOG cache rather than a
      per-table query**, or every full enumeration costs one round trip per table.
    - **⚠⚠ THE DELTA COLUMN COMMENTS COST NOTHING, AND THE REASON IS ONE GREP: the Arrow schema ALREADY
      CARRIES THEM.** engineered-wood's `SchemaConverter.ToArrowField` copies each `StructField`'s metadata
      onto the Arrow field VERBATIM — its `FilterArrowMetadata` is write-direction only and strips just
      `PARQUET:*` — and `comment` is where the Delta protocol puts a column comment. So `ColumnComments()` is
      a read of the schema the binding already fetched, it works through COLUMN MAPPING (the default) because
      the schema carries the LOGICAL name, and it is AS-OF correct for a time-travel entry for free.
    - **⚠⚠ I BUILT A CACHE FOR IT ON A COST CLAIM THAT WAS FALSE, AND THE MUTANT DELETED THE CACHE.** The
      `Schema` getter does NOT memoize (each access re-opens), so I restructured it into a `ResolveSchema()`
      wrapper that cached the comments, justified as "otherwise this DOUBLES the `_delta_log` reads of every
      table during enumeration — the OneLake cost". **MEASURED with the per-IO instrument
      (`FABRICATOR_LOG_LEVEL=Debug`, counting `Fabricator.Host.Fs` lines around a `duckdb_tables()`): a
      2-table enumeration is 10 ops (4 list + 6 read-all) on BOTH builds — identical** — because the table
      open is already SHARED for the statement. The restructure was REVERTED and the justification rewritten
      to say what was measured, per this file's own rule (the `IHostLog.Log` mutant that survived and
      simplified the code). ⚠ The instrument and the numbers are in the code comment so nobody re-adds it
      without re-measuring.
    - **⚠ THE SQL SERVER SIDE IS A DIFFERENT QUERY SHAPE, NOT A CACHE OVER A SHARED RESOURCE, which is why it
      stands where the Delta one did not.** Its column fetch is `SELECT * FROM t WHERE 1 = 0` — a describe
      that CANNOT carry an extended property — so comments need their own query, and a per-table one would be
      one round trip per table on the enumeration path this provider has already been burned by twice (the
      OneLake slowness; the 15871 discovery defect). It asks ONCE, database-wide
      (`sys.extended_properties` joined to `sys.objects`/`sys.schemas`/`sys.columns`), and caches per catalog.
    - **⚠⚠ READ-YOUR-OWN-WRITES NEEDS TWO INDEPENDENT INVALIDATIONS, and the mutants prove they are
      independent.** The PROVIDER's database-wide comment cache must be dropped at the write site, and the
      HOST's catalog ENTRY must be evicted — miss either and `COMMENT ON t IS 'x'` followed by a read reports
      the PREVIOUS value in the same session. Mutant G (no entry eviction) kills BOTH suites at their FIRST
      read-back row (Delta 210 after 39 pass, SQL Server 162 after 34); mutant F (no provider cache drop)
      kills the SQL Server suite at the same row **with Delta GREEN at 53 as the control** — which is what
      makes it a provider-specific attribution rather than two failures that happen to look alike.
    - **⚠⚠ IT FIRED A NOTE I HAD LEFT MYSELF, WHICH IS THE PAYOFF OF WRITING THE PREDICTION DOWN.**
      `AlterComment` shipped in slice 1 with "⚠ NO refresh(), deliberately … The day comments are surfaced,
      this needs the same refresh(info.name) the column kinds do, or the cached entry keeps the old comment."
      That day was the same day. The comment is now REPLACED (not softened) with what is true, and the
      eviction rule was extracted into a shared `RefreshEntry` so ALTER and COMMENT ON cannot drift on it.
    - **⚠ THE STRUCT RENAME COLLIDED WITH A NAME 140 LINES UP IN THE SAME HEADER, and my collision check was
      SELF-DEFEATING.** `FabricatorTableRowIdentity` stopped describing its contents once comments joined it,
      so I renamed it `FabricatorTableInfo` — which `catalog_tables` has used for a DISCOVERED table all
      along. The compiler said *"use of undefined type"* at a `for` loop, which points nowhere near the
      cause. ⚠ **The grep I "verified" with was `grep -rn FabricatorTableInfo src/ | grep -v fabricator_metadata`
      — I excluded the very file I was editing.** It is `FabricatorTableDetails` now, and the header says why
      the obvious name was unavailable. **A collision check must include the file you are changing.**
    - **⚠ RESTORING A C++ SOURCE IS NOT RESTORING THE BINARY, and a C#-only rebuild in the same command line
      HIDES it.** Mutant F's first run was VOID: I restored `fabricator_schema_entry.cpp`, ran
      `dotnet build` (which succeeded, looking like "the build ran") and measured — with `unittest.exe` still
      carrying mutant G. **The tell was the CONTROL**: Delta failed with mutant G's exact signature (40
      assertions, 39 passed) on a mutant that cannot reach Delta. Same class as the recorded `Move-Item` mtime
      trap; rebuild C++ explicitly after restoring a C++ mutant.
    - ⚠ Two smaller things, both deliberate: an unmatched column name in `column_comments` is IGNORED rather
      than an error (a provider may report a comment for a column a pending ALTER or an object filter has
      removed from this entry's schema, and refusing there would make a stale comment break MATERIALIZATION);
      and the table comment carries a `has_comment` flag beside it because an EMPTY comment is a value a
      caller can set (`COMMENT ON TABLE t IS ''`) — the empty-vs-absent rule, fourth occurrence.
    - **⚠ STILL OPEN, and narrower than before: a TABLE comment is not reported for a Delta TIME-TRAVEL (`AT`)
      entry.** An AT binding takes its schema from `GetSchemaAt`, which does not carry `description`, so
      reporting the CURRENT comment on a historical entry would be a WRONG answer rather than a missing one —
      null is the honest choice. COLUMN comments are unaffected (they ride the AS-OF schema). Closing it means
      threading the description through `GetSchemaAt` too.

- **⚠⚠ `ALTER TABLE … ADD COLUMN … DEFAULT <literal>` WAS ACCEPTED AND SILENTLY DROPPED — user-reported
  2026-09-08 ("i think we don't support a default when adding a column"), FIXED. C++ + C#, NO ABI change.
  Gate: a NEW suite `verify_alter_default` (**59**, SERVICE tier), three mutants each killed at its OWN row.
  Full records: [docs/duckdb-upstream-issues.md](duckdb-upstream-issues.md) §7 (the DuckDB half) +
  the README's DDL note.**
  MEASURED before anything was built: the column appeared, existing rows were NULL, **and every later insert
  omitting the column was NULL too** — on BOTH providers — while DuckDB's own table backfills AND applies it.
  The C++ `ADD_COLUMN` render read only `Name()` and `Type()`, never `DefaultValue()`.
  - **⚠⚠ THE USER WAS RIGHT ABOUT NOT NULL AND MY FIRST MEASUREMENT WAS THE WRONG CASE.** I measured a
    NULLABLE column (existing rows NULL) and reported that SQL Server does not backfill; they said it does.
    Both are true and the split is the rule: **NOT NULL + DEFAULT backfills automatically** (measured, and
    metadata-only since 2012), **NULLABLE + DEFAULT does not** unless `WITH VALUES` (measured). ⇒ **a claim
    about "SQL Server's behaviour" that does not say WHICH NULLABILITY is half a claim.**
  - **⚠ THE EMIT IS ONE FORM UNCONDITIONALLY, and a measurement is what made it one: DuckDB REFUSES
    `ADD COLUMN … NOT NULL DEFAULT` outright** (*"Adding columns with constraints not yet supported"*), so
    the NOT NULL shape can never arrive — the only possible shape is NULLABLE, which is exactly the one
    needing `WITH VALUES`. Without checking that, this would have grown a nullability branch for an
    unreachable case. ⚠ `NOT NULL DEFAULT 10 WITH VALUES` is valid T-SQL anyway (measured), so the
    unconditional form is safe rather than lucky.
  - **⚠ BACKFILL IS A USER DECISION AND THEY MADE IT** (match DuckDB, "even though it is very expensive").
    `WITH VALUES` is O(table) where the plain form is metadata-only; that is the price of a catalog table
    answering the same statement the same way a native one does.
  - **⚠⚠ THE SHARP FINDING — DuckDB LOSES A CAST-WRAPPED DEFAULT BEFORE THE CATALOG SEES IT, and it made a
    partial fix WORSE THAN NONE.** Probing the expression we are handed: a BARE literal survives (`10`,
    `1.5`, `1.50`, `'x'`, and `'2024-01-01'` written as a STRING) while ANYTHING the parser wraps in a CAST
    arrives as a `VALUE_CONSTANT` holding **NULL** — `true`, `CAST(1 AS BOOLEAN)`, `DATE '2024-01-01'`,
    `TIMESTAMP '…'`, `'\x41'::BLOB`. So the first working build emitted `DEFAULT (NULL)` for `DEFAULT true`:
    the very defect being fixed, reintroduced one layer in.
    - **THE CONTROL that makes it DuckDB's rather than ours: `ALTER COLUMN … SET DEFAULT` carries the SAME
      boolean and date correctly** (measured: `((1))` and `(N'2024-01-01')` on the server) **through the SAME
      helper**, and DuckDB's own table is unaffected. ⇒ localised to `AddColumnInfo`, not to our literal
      handling. Recorded as upstream-issues §7, **NOT FILED** — it needs a repro with no third-party
      extension, which is that file's own standing rule.
    - **⚠⚠ REFUSED, AS ONE RULE RATHER THAN A TYPE LIST.** A lost value is INDISTINGUISHABLE from an honest
      `DEFAULT NULL` (both a NULL constant), so any NULL arrival on `add_column` is refused by name. A list
      of "types whose literals get cast" would be enumerated from measurements and could MISS one (UUID,
      ENUM, a nested type) — **and a missed type is a silent wrong answer again**, which is the whole thing
      being avoided. The cost is only the NO-OP spelling: `DEFAULT NULL` on a nullable column asks for what
      it already does.
  - **⚠ `DefaultLiteral` COULD NOT CARRY THE STATE — the empty-vs-absent trap, SECOND occurrence in the same
    class.** Null means "DEFAULT NULL" on `set_default` (where the key is REQUIRED) and "no default" on
    `add_column` (where it is OPTIONAL), so `AlterTableSpec` grew `HasDefault` read from key PRESENCE. The
    class's own comment already warns about this for `arg2`'s `"-"` vs `"b"`+base64; it arrived again on a new
    kind. ⚠ Asserted on the SERVER, the only place it is visible: an absent default has NO
    `sys.default_constraints` row, `DEFAULT ''` has one holding `(N'')`.
  - **⚠⚠ SQL SERVER'S `ALTER TABLE` HAD ESSENTIALLY NO GATE, WHICH IS WHY THIS SURVIVED.** The only
    catalog-level `ADD COLUMN` in the whole tree was one SETUP line in a locking test, asserting nothing
    about the ALTER; Delta's ALTER has three suites. `verify_alter_default` is SQL Server's first. **⚠ The
    rest of SQL Server ALTER (rename table/column, drop column, change type, SET/DROP NOT NULL) is STILL
    ungated** — that is a standing gap, not something this fixed.
  - **⚠ A REUSABLE GATE TECHNIQUE: ORDER THE ASSERTIONS SO THE MUTANTS SEPARATE.** As first written, "the
    default is dropped" and "`WITH VALUES` is missing" both died at the same row, because backfill was
    asserted first and fails under either. Asserting the INSERT first splits them — mutant A dies at the
    insert row after 8 assertions, B at the backfill row after 13, C (drop the NULL refusal) at the boolean
    row after 35. It is also the honest ordering: an insert honouring a declared default is wrong on ANY
    reading, where backfill is the contested half.
  - **⚠ DELTA REFUSES rather than dropping, and the reason is checked rather than assumed:** Delta records a
    column default through the `allowColumnDefaults` writer feature and the `CURRENT_DEFAULT`/
    `EXISTS_DEFAULT` field-metadata keys, and **engineered-wood implements NONE of them** (grepped: zero
    hits; every `defaultValue`/`ColumnDefault` hit in that tree is ORC or Avro). ⇒ **the user's plan to
    backfill on Delta behind a setting cannot be made correct**: with no way to RECORD the default, a
    backfill leaves the first N rows carrying it and every later insert NULL, with nothing saying one was
    declared and nothing marking which rows were written — unrepairable afterwards. Retiring the refusal
    needs EW support FIRST.
  - **⚠ STILL OPEN, PRE-EXISTING, and now pinned as a CHARACTERIZATION (§5 of the suite): a column default is
    NOT reported in `information_schema.columns.column_default`** — the catalog never reads defaults back
    from the provider, so a column with a demonstrable `((10))` on the server shows NULL in DuckDB's
    metadata. Equally true of a CREATE-time default, so it predates this. A different mechanism (the
    table-schema crossing would carry them as Arrow field metadata) and a different decision.

- **⚠⚠ THE ARROW STREAM'S ERROR STRING WAS READ AFTER THE STREAM WAS RELEASED — a read-after-free on
  every bind-time schema failure, latent since the file was written and made REACHABLE by the
  2026-08-24 lazy-describe fix. ✅ FIXED 2026-08-26 (C++-only, no ABI). FOUND BY THE SCHEDULED LINUX
  INTEGRATION TIER (run `32928853715`) AND STRUCTURALLY INVISIBLE ON WINDOWS, which is why the local
  service tier was green through it for two days.**
  - **THE SHAPE.** `PopulateReturnSchema` and the three bind sites beside it did
    `msg = stream.get_last_error(&stream); stream.release(&stream); throw IOException(... + msg ...)`.
    The two CI failures were `statement error` assertions whose query failed correctly but whose
    MESSAGE arrived as garbage: `verify_raw_query:118` expected *"Invalid column name 'nosuchcolumn'"*
    and `verify_mssql_s3_polybase:200` expected *"Column mapping"*.
  - **⚠⚠ THE MECHANISM IS THE EXPORTER'S OWN SOURCE, NOT AN INFERENCE — and that is what makes this a
    settled fact rather than a plausible story.** `apache/arrow-dotnet`'s
    `CArrowArrayStreamExporter.Release` → `ExportedArrayStream.Free` → `Dispose()` →
    `ReleaseLastError()` → **`Marshal.FreeHGlobal((IntPtr)LastError)`**, where `LastError` is exactly
    the `byte*` that `get_last_error` returns. Release frees the message BY CONSTRUCTION. ⚠ The C#
    Arrow sources live in **`apache/arrow-dotnet`**, not `apache/arrow` — the latter 404s.
  - **⚠⚠ WHY ONLY LINUX SAW IT, and the observation and the source agree exactly.** Windows `HeapFree`
    typically leaves the bytes intact, so the correct message survives the free and every Windows run
    passes; glibc writes tcache metadata into the freed chunk immediately, so its first bytes become
    POINTERS — which is precisely what CI printed: short, high-byte, and **DIFFERENT PER CALL**
    (`%�sU` in one suite, `c�7^U.V` in the other). ⇒ **third instance of this project's
    standing rule** (macOS `ArrowProducer`, macOS aggregate-state destructor): *a use-after-free is
    invisible on the platform you develop on, so a green local run is not evidence.*
  - **⚠⚠ WHY IT SURFACED NOW rather than in the year the bug existed.** Before the lazy describe
    (`0acd679`), `fabricator_query`'s bind called `bind_data.factory(...)`, which EXECUTED the query
    inside the ABI crossing — a broken query threw there through `ThrowManagedError` with a proper
    message, and `get_schema` could never fail. Making the stream lazy moved the first failure onto
    `get_schema` + the stream's error buffer, i.e. onto the dormant path. **The two failing assertions
    are the very ones the describe fix ADDED to pin its new bind-time error behaviour** — the gate
    worked, one tier later than the change.
  - **FIXED AT ALL FOUR SITES** by copying into a `std::string` before release: `arrow_ingest.cpp`
    (`PopulateReturnSchema` — raw query + catalog scan binds), `fabricator_lateral.cpp` (lateral bind),
    and `fabricator_schema_entry.cpp` × 2 (in-out exchange + collector binds).
  - **⚠ THE OTHER FIVE `get_last_error` CALLERS ARE CORRECT AND WERE SWEPT rather than assumed** —
    each builds its message while the stream is still ALIVE, its owner releasing later
    (`arrow_ingest.cpp` 168/1002/1118/1148/1174). **`ThrowManagedError` is correct too**: it appends
    `err` into the message BEFORE calling `free_error`. So the four bind sites were the only instance.
  - ⚠ Releasing the STREAM does not invalidate the SCHEMA `get_schema` filled — that is a separately
    owned `ArrowSchema` with its own release callback, held by `ArrowSchemaWrapper`. The surrounding
    code was right about that; only the error string was wrong.
  - **⚠ THE GATE IS THE LINUX TIER AND NOTHING ELSE, and the suites say so rather than implying
    coverage.** `verify_raw_query:118` and `verify_mssql_s3_polybase:200` already assert the message
    text so no new test is owed — but **both pass on Windows with the bug fully present**, so the local
    tiers (service **54/54 — 3011**, hermetic **74/74 — 8003**, both at their floors) are a
    NO-REGRESSION result and NOT evidence of the fix.
  - **✅ PROVEN ON LINUX (integration run `32966403735`, dispatched on `e0523a1`): both suites pass at
    their FULL counts — `verify_raw_query` 34 and `verify_mssql_s3_polybase` 263** — where the
    scheduled run one commit earlier failed both. ⚠ **That run is nonetheless RED**, on
    `verify_session_tag:146`, which is the recurring pre-existing flake recorded under the CI section <!-- check-docs:ignore (verify_session_tag was deleted 2026-10-29 with fabricator_session_tag) -->
    and cannot be reached by an Arrow error-string change. **Read the per-suite lines, not the run's
    conclusion** — the tier's verdict says nothing about which suite moved.

- **⚠⚠ THE TIERS REPORTED AND THE SERVICE ONE FOUND A REGRESSION IN THE `fabricator_query` FIX ITSELF —
  ✅ FIXED 2026-08-24, commit `00bd30e` (C#-only, no ABI). Hermetic was 74/74 — 8003 GREEN as predicted; service came back
  52/54 with `verify_session_tag` failing at line 77, the assertion whose own comment says "this is what <!-- check-docs:ignore (verify_session_tag was deleted 2026-10-29 with fabricator_session_tag) -->
  fails without the pin, and it fails by returning NULL rather than by erroring".**
  - **THE DEFECT: deferring the execution ACROSS A CROSSING dropped the ambients.** `AmbientTransaction` /
    `ProviderSettingsStore.CurrentSession` / `AmbientOpener` are `AsyncLocal` PER CROSSING. The old eager
    stream executed inside `execute_query`, where `ArrowStreamInitGlobal` had just established them; the lazy
    one executes at the first `get_next`, where nothing has. So `catalog.ExecuteQuery` saw txn 0, found no
    pinned connection, and ran the caller's SQL on a POOLED one — **outside the user's transaction**.
  - **MEASURED A/B, the capture being the only variable**: inside `BEGIN` after a DuckDB-managed write,
    `fabricator_query('q','SELECT @@TRANCOUNT')` returned **0 before the fix and 1 after**. The session tag
    was the cheap symptom; the real one is that **read-your-writes was silently gone for every raw query in a
    transaction**.
  - **⚠ THIS IS THE SECOND INSTANCE OF ONE RULE, so treat it as general rather than as a table-function
    quirk.** The first was `fabricator_install_plugin` reading a session-scoped opt-in inside an async
    iterator. **A LAZY BODY MUST BE HANDED THE AMBIENTS ITS OWN CROSSING CAPTURED — never read them where it
    runs.** Both failed by returning a WRONG ANSWER rather than erroring, and the plugin one was
    non-deterministic on top.
  - **⚠ MAKING A PATH LAZY IS A CROSSING CHANGE, AND THE REVIEW QUESTION IS "WHOSE AMBIENTS?"** Nothing about
    the describe fix looked transactional — it is a schema optimisation — which is exactly why the ambient
    question was never asked. Its commit message reasoned carefully about schema agreement and about
    null-vs-throw, and said nothing about the ambients.
  - Gate: `verify_raw_query` 27 → **34**, new §8. ⚠ It asserts **`@@TRANCOUNT`, deliberately, because it is
    LOCK-FREE**: the obvious probe — read a row this transaction inserted — would BLOCK on the uncommitted
    row's lock when run pooled under READ COMMITTED, so a regression would HANG the suite rather than fail it
    (limitation 1.15). A test that hangs on regression is worse than no test. Its autocommit control (0) is
    load-bearing: without it, "1" would be equally satisfied by a build reporting a constant.
  - **⚠ THE SECTION WAS FLAKY FIRST, and the cause is a reusable trap: `rq_probe` is created by
    `fabricator_exec`, so THE CATALOG NEVER LEARNS ABOUT IT** (`mssql_exec_invalidate_cache` off by default —
    MEASURED absent from `duckdb_tables()` 3/3 after its own CREATE). A catalog-level `INSERT INTO
    q.dbo.rq_probe` therefore only binds if a PREVIOUS run left the table behind, so the section **ALTERNATED
    pass/fail** — the identical signature `verify_read_isolation` once had, for the identical reason (a
    failing run never reaches its teardown). Fixed by giving §8 its own table created THROUGH the catalog;
    4/4 green from a cleaned server afterwards.
  - ⚠ `verify_session_tag` then failed ONCE at line **146** — a DIFFERENT assertion, and it is the one-off <!-- check-docs:ignore (verify_session_tag was deleted 2026-10-29 with fabricator_session_tag) -->
    flake already recorded further down this file. 25 assertions, green 3/3 afterwards. Do not conflate the
    two: line 77 was mine, line 146 is the pre-existing one.

- **⚠⚠ `fabricator_query` USED TO EXECUTE ITS SQL TWICE — a shipped defect, user-found and ✅ FIXED 2026-08-24, commit `0acd679` (C#-only, NO ABI change). Gate `test/verify_raw_query.test` 27, mutation-tested (the eager stream dies at §1 with "Expected 1, Actual 2").**
  MEASURED: `SELECT * FROM fabricator_query('db','INSERT INTO dbo.twice VALUES (1); SELECT 1 AS x')` left
  **2 rows**; the same INSERT through `fabricator_exec` left 1 and reported `affected = 1`.
  - **CAUSE.** `fabricator_query` is a TABLE function, so DuckDB needs its column types at BIND. The C++ bind
    sets only `bind_data->factory`, and `PopulateReturnSchema` (`arrow_ingest.cpp:263`) therefore calls that
    factory to get a stream just to read `get_schema` — i.e. a full execution — then the scan executes again.
    `DbDataReaderArrowStream`'s constructor takes an ALREADY-EXECUTED `DbDataReader` and reads
    `reader.GetColumnSchema()`, so the stream cannot exist without executing.
  - **⚠⚠ THE UNCOMFORTABLE PART: BOTH HALVES OF THE FIX WERE ALREADY IN THE TREE, WIRED TO OTHER CALLERS.**
    `sp_describe_first_result_set` is used — for STORED PROCS only (`SqlServerBackend.cs:3841`); and
    `bind_data->schema_factory`, whose own comment is *"The bound object can describe itself — ask it instead
    of executing it"*, is used — for CATALOG TABLE SCANS only (`fabricator_table_entry.cpp:884`).
    `fabricator_query` fell between them, and nothing in the tree uses `CommandBehavior.SchemaOnly`.
  - **THE FIX IS MEASURED SOUND before being built:** `EXEC sys.sp_describe_first_result_set @tsql =
    N'INSERT INTO dbo.twice VALUES (1); SELECT 1 AS x, CAST(2 AS BIGINT) AS y'` left **0 rows** — the INSERT
    did not run — and reported both columns correctly with types and nullability. So a describe gives the
    schema for exactly the batch shape that currently double-executes.
  - **AS BUILT, and it needed NO ABI change.** The C ArrowArrayStream has SEPARATE `get_schema` and
    `get_next` callbacks, and `PopulateReturnSchema` calls only `get_schema` then releases ⇒ a LAZY stream
    works: `get_schema` describes (`CommandBehavior.SchemaOnly`), `get_next` executes once. Scope it to
    **`Bootstrap.ExecuteQuery`** (the raw `fabricator_query` ABI handler) so the many internal C# callers of
    `catalog.ExecuteQuery` — `ReadMetadataRows` and friends — keep executing directly and do NOT gain a
    describe round trip they do not need.
  - **BOTH DOUBTS RESOLVED BY MEASUREMENT.** (1) The exporter IS lazy — it fills a `get_schema` FUNCTION
    POINTER, so no ABI entry was needed. (2) The fallback works and the split is now MEASURED: a plain
    SELECT, **dynamic SQL** (`sp_executesql`), a `DECLARE` prologue, a UNION and a **CDC change-table read**
    all DESCRIBE (single execution); a `SELECT … INTO #t; SELECT … FROM #t` batch FALLS BACK (still two, i.e.
    unchanged). ⚠ What makes reporting a described schema to DuckDB acceptable is CONSTRUCTION, not care:
    both answers are built by `SqlArrowMapping.ToArrowField` from a `DbColumn`, so describe and execute run
    the SAME mapping and cannot diverge through a hand-written type table.
  - **⚠ A BEHAVIOUR CHANGE WORTH KNOWING: a broken query now fails at BIND**, with SQL Server's own message
    (`Invalid column name 'x'`) instead of mid-scan. Better, and pinned (§5) — because only the message text
    distinguishes "the describe refused this shape" (fall back, null) from "the describe surfaced a real
    error" (throw). Swallowing the second would make every broken query cost a wasted execution first.
  - **⚠⚠ THE FIX ITSELF SHIPPED A REGRESSION FOR ONE TIER RUN (fixed in `00bd30e`) — the deferral dropped the AsyncLocal
    ambients, so a raw query inside a transaction ran POOLED.** See the entry above; the standing rule it
    confirms is that a lazy body must be handed the ambients its own crossing captured.
  - **⚠ It was a PRE-EXISTING defect independent of CDC** and got its OWN commit. Every `fabricator_query`
    user was paying it: any side effect doubled, any expensive SELECT costing twice.
  - **⚠ IT ALSO STRENGTHENS THE CDC READER'S OPTION A**, which is a connection worth keeping: A2 was "emit
    `FROM fabricator_query('db','<our T-SQL>')`", so under this defect A2 would read the change table TWICE
    per statement — on top of losing the measured pushdown. Two independent arguments for A now.
  - ⚠ **THE SUITE IS THE OTHER HALF OF THE FIX, and its absence is why the defect shipped.** Nothing owned
    these two functions: 29 suites USE `fabricator_query`, none was ABOUT it. Only a COUNTING assertion can
    see a double execution — every "the rows are right" test in the tree passes either way, which is exactly
    how it went unnoticed. `verify_raw_query` also now documents the remaining fallback, the bind-time error
    change, and `fabricator_exec`'s JOIN-ONLY transaction semantics (both README-documented as of this fix).

- **USER-DIRECTED 2026-08-18: (1) REMOVE THE MFR SPIKE — ✅ DONE (ABI v75); (2) SPLIT `Fabricator.Bridge`
  BY ASSEMBLY — ✅ PHASE A DONE (`Fabricator.Delta`), PHASE B (`Fabricator.Fabric`) SCOPED.** The facts are
  recorded here because they were established by measurement in-session and are not derivable from the code.
  - **PHASE A AS BUILT (2026-08-18, C#-only, no ABI): `Fabricator.Delta` — 24 files / ~19.4k lines out of
    Bridge, which drops from ~36k to 54 root files + `FabricApi/` 17.** Bridge no longer references the
    **engineered-wood submodule** or **AWSSDK.S3**. Same `Fabricator.Bridge` NAMESPACE (the user's "split by
    assembly, group by directory, keep the namespace"), following the `Fabricator.Abstractions` precedent —
    which is why the move needed ZERO `using` churn across six projects.
    - **THE SET IS DEFINED BY A PROPERTY, NOT A NAME PREFIX: a file is Delta iff it needs engineered-wood**,
      plus the closure of files whose every consumer is already in the set. That is what the assembly exists
      to move, so it is the only defensible membership rule — and it puts `AdlsGen2TableFileSystem`,
      `DuckDbTableFileSystem`, `S3CommitFileSystem`, `ExternalTableRouting`, `ArrowColumnMappingRename` and
      `VariantTransport` in Delta despite none of them being named `Delta*`, while leaving `AdlsCredential`,
      `VariantMarker`, `DeltaFunctions`, `DeltaParquetProperties`, `DeltaCatalogInfoFunction` and
      `OneLakeStagingLocation` in Bridge because they need no EW and have Bridge-side or sibling consumers.
    - **⚠⚠ THE CLOSURE MUST COUNT THE SIBLING ASSEMBLIES AS CONSUMERS, AND MY FIRST ONE DID NOT.**
      Run over Bridge alone it proposed moving `ArrowValueReader` and `CatalogMacroMetadata` — which
      `Fabricator.SqlServer`, `.AnalysisServices` and `.DeltaRs` use in 9 files between them. Moving either
      would have broken three assemblies. **A closure is only as good as its consumer set; enumerate every
      project that references the one you are carving up.**
    - **`Fabricator.SqlServer` → `Fabricator.Delta` IS A REAL DEPENDENCY, not a split artifact**:
      `SqlServerBackend` calls `ExternalTableRouting` at 7 sites (a SQL Server EXTERNAL TABLE's INSERT and
      identity-keyed UPDATE/DELETE are routed to storage, i.e. they ARE Delta writes) and its
      `CustomFunctions` registers the four Delta global functions. `Fabricator.DeltaRs` → Delta likewise
      (`DeltaFunctions`, `FabricLakehouse`). Both gained a ProjectReference.
    - **`InternalsVisibleTo("Fabricator.Delta")` rather than making ~20 host types public.** The split is a
      dependency boundary, not an API boundary; widening `HostFs`/`BoundInput`/`HostBatchFilter`/
      `ParquetTuning`/the ambients to `public` would have grown the plugin-visible surface for a project-file
      reason. It departs from the `DeltaFunctions`-made-public precedent deliberately — that was ONE type
      crossing to an opt-in provider. All 77 errors from the move were `CS0122`, i.e. exactly this.
    - **⚠ USER-VISIBLE BEHAVIOUR CHANGE, and no suite can catch it: setting `FABRICATOR_BACKEND_ASSEMBLY`
      NOW BOUNDS THE DELTA PROVIDER TOO.** It used to be hard-registered UNCONDITIONALLY after the scan, so
      it was present whatever the variable said; a narrow override like
      `FABRICATOR_BACKEND_ASSEMBLY=Fabricator.SqlServer` now silently loses `PROVIDER 'delta'`. The failure
      is a clean unknown-provider error at ATTACH rather than a wrong answer, and the tiers never set the
      variable narrowly, so this is documented (docs/dax-provider.md §Discovery) rather than gated.
    - **⚠ `BackendRegistry`'s hard `Add(map, new DeltaBackend())` is GONE — Delta is discovered by name
      like every other provider, and `Fabricator.Delta` is APPENDED LAST to the default
      `FABRICATOR_BACKEND_ASSEMBLY` list.** The old comment said it was registered after the scan "so a
      scanned provider stays the default"; that was not decoration, because `Default()` falls through to
      `map.Values.Distinct().First()` — Dictionary INSERTION order. Position in that string is now what keeps
      SqlServer the default; prepend Delta and it silently stops being.
    - **`OneLakeForwardFs` STAYS IN BRIDGE and that is the one deliberate compromise.** Ten `onelake_*` ABI
      entries in `Bootstrap.cs` call it, so the VFS is ABI SURFACE; it needs no EW, so it can stay. Bridge
      therefore keeps `Azure.Storage.Files.DataLake` + `Azure.Identity` + the three `Fabric*Credential` files.
      Its one `FabricLakehouse` mention is a DOC COMMENT, which is what makes this work — otherwise Bridge
      would need `Microsoft.Fabric.Api` too.
    - Gates: all six assemblies build; hermetic + service tiers; and a smoke proving the DISCOVERED provider
      still works (CTAS 500 / UPDATE 10 / DELETE 9 ⇒ 491 rows, 10 updated). ⚠ Publish needs **`-Clean`** —
      this is exactly the moved-PackageReference hazard measured hours earlier.
      **CI tier 1 is GREEN ON ALL THREE PLATFORMS** (osx_arm64 20m44s / linux_amd64 19m37s / windows_amd64
      35m37s, run `32152100109`), which closes the caveat this entry shipped with: a brand-new csproj is
      exactly the kind of thing that trips on Linux or macOS, and only CI could settle it.
  - **PHASE B, NOT BUILT: extract `Fabricator.Fabric` (`FabricApi/`, 17 files, `Microsoft.Fabric.Api`).**
    Measured prerequisite: with the Delta files gone, `FabricApi/` needs only `Host` and
    `FabricCredentialResolver` from the Bridge core, and NOTHING in the core needs `FabricApi/` — so
    Fabric → Bridge one-way, no cycle. It could not be done first: while the Delta files sat in Bridge,
    `DeltaCatalog` used `FabricApiClient`, which would have made Bridge → Fabric → Bridge.
  - **(2) IS SCOPED FROM THE SOURCE — a three-way coupling scan, comments and string literals stripped,
    every TYPE the candidate set declares grepped against the other two sets (script kept in the session
    scratchpad; re-derive it, do not trust this summary).** Sets: core 46 files / Delta 31 / Fabric
    (`FabricApi/`) 17. Results:

    | direction | real references |
    |---|---|
    | CORE → FABRIC | **NONE** ⇒ `Fabricator.Fabric` extracts with ZERO change to Bridge |
    | FABRIC → DELTA | **NONE** ⇒ the layering holds, no cycle |
    | CORE → DELTA | **exactly TWO** — `BackendRegistry` → `DeltaBackend`, and `Bootstrap` → `OneLakeForwardFs` |

    ⇒ **`Fabricator.Fabric` ← `Fabricator.Delta` ← `Fabricator.Bridge`**, each keeping the
    `Fabricator.Bridge` NAMESPACE per the user's "split by assembly, group by directory, keep the namespace".
    **Phase it: extracting `Fabricator.Fabric` alone is mechanical and needs no code change at all.**
    - **⚠⚠ MY FIRST SCAN SAID "the only Bridge→Delta reference is `new DeltaBackend()`" AND THAT WAS
      WRONG — the pattern was `\bDelta[A-Z]`, which cannot see `DuckDbTableFileSystem`, `S3CommitFileSystem`
      or `OneLakeForwardFs`.** It is this file's own recorded error in a new costume: a backwards grep
      encodes the searcher's assumed naming and returns a plausible, incomplete answer. **The right method
      is the one above — enumerate the types the PROVIDER set DECLARES, then grep the consumers for those.**
      Two further traps it exposed: doc comments dominate the raw hits (strip `///` and `//` or the signal
      drowns), and private nested helper names (`Binding`, `Handle`, `State`, `Entry`, `Options`, `Row`)
      collide across dozens of files and must be excluded or everything looks coupled to everything.
    - **⚠ `Bootstrap` → `OneLakeForwardFs` IS THE REAL DESIGN QUESTION, and it was not anticipated.** Ten
      ABI entries (`onelake_open`/`_read`/`_close`/`_glob`/`_exists`/`_open_write`/`_write`/`_close_write`/
      `_remove`/`_rename`) are `[UnmanagedCallersOnly]` handlers in `Bootstrap.cs` that call
      `OneLakeForwardFs` + its nested `Handle`/`WriteHandle`. So the `onelake://` VFS is part of the ABI
      SURFACE while its implementation needs Azure DataLake + Fabric credentials. Three ways out, none free:
      (a) let Bridge reference `Fabricator.Fabric` (no cycle, but Bridge keeps the Azure/Fabric packages —
      most of the point lost); (b) move those handlers into the provider assembly and have `Bootstrap` fill
      the slots through a discovered hook (clean, but NOT the mechanical rename the user asked for);
      (c) keep `OneLakeForwardFs` in Bridge and accept that the VFS is host surface. **Decide this before
      moving any file** — it determines which of the three assemblies carries
      `Azure.Storage.Files.DataLake`.
    - `HostFileSystem.cs` is NOT a Delta file despite sitting next to them: `HostFs` is used by
      `Bootstrap`, `Host`, `HostBatchFilter`, `HostParquetStaging`, `InterruptScope` and
      `SingleScanArrowStream`. It stays in Bridge.
    - **⚠ ORDER IS LOAD-BEARING IN `BackendRegistry`, and the comment says why in a way easy to undo.**
      `Add(map, new DeltaBackend())` is deliberately AFTER the assembly scan so a scanned provider
      (SqlServer) stays the default — `Default()` falls through to `map.Values.Distinct().First()`, i.e.
      Dictionary INSERTION order. So `Fabricator.Delta` must be APPENDED to the end of the default
      `FABRICATOR_BACKEND_ASSEMBLY` list (`Fabricator.SqlServer,Fabricator.AnalysisServices,
      Fabricator.DeltaRs`), never prepended, or SqlServer silently stops being the default.
    - **THE "FREE WIN" IS TAKEN (Bridge no longer declares `Microsoft.Data.SqlClient` +
      `.Extensions.Azure`) — AND IT WAS NOT FREE. It broke the published payload, silently, and finding
      that is worth more than the win.** The greps were right about CODE: nothing in Bridge uses either
      package (the only two matches are a comment saying *"rather than reference Microsoft.Data.SqlClient"*
      and `FabricSqlEndpointHost.cs` saying it hand-parses *"to stay BCL-only"*), the csproj justification
      naming *"the SQL Server backend + the DAX SQL-endpoint path"* is stale in both halves, all four
      assemblies compile without it, and `Fabricator.SqlServer` declares both itself.
      - **⚠⚠ AND THE PAYLOAD LOST ALL FIVE SqlClient DLLs ANYWAY. MEASURED, with a control both
        directions: reference present ⇒ 5 files in `build/release/extension/fabricator/fabricator`;
        removed ⇒ ZERO — while `Fabricator.SqlServer.dll` itself stayed.** So the SQL Server provider would
        have shipped unable to open a connection.
      - **THE MECHANISM, isolated rather than guessed:** several projects publish into ONE directory, and
        `dotnet publish` REMOVES what its own previous publish wrote and its current closure no longer
        contains. `Fabricator.AnalysisServices` publishes LAST and used to pull SqlClient transitively via
        Bridge; once Bridge dropped it, its publish DELETED the five files `Fabricator.SqlServer` had just
        written. Proof: into a FRESH dir the identical two publishes keep all five, and a `-Clean` publish
        of the real script restores them.
      - **⚠ IT IS INVISIBLE WHERE IT MATTERS — nothing fails to compile, and the HERMETIC TIER NEVER
        TOUCHES SQL SERVER, so only the service tier catches it.** Same shape as the SqlClient 7.0 Entra
        finding: a dependency defect both CI tiers can be green through.
      - **`publish-managed.ps1` GAINED `-Clean`**, documented beside the existing mode-change clean (the
        same class of problem, which is why that one already existed). Not unconditional: a self-contained
        payload is ~250 MB and the common loop is a C#-only edit. **Standing rule for the split: after
        MOVING a PackageReference between assemblies, publish with `-Clean` and then run the SERVICE
        tier** — the split moves several packages, so this will fire again.
      - **⚠⚠ AND IT FIRED AGAIN 2026-09-08, WITH NO PackageReference HAVING MOVED — SO THE RULE IS TOO
        NARROW: a MUTATION-TESTING ROUND TRIGGERS IT TOO.** After publishing mutant payloads to a scratch
        `-ExtensionDir` and then republishing the restored source to the REAL directory, the payload held
        `Fabricator.SqlServer.dll` and its `deps.json` still declaring `Microsoft.Data.SqlClient/7.0.2`
        while **all five SqlClient DLLs were gone** — AnalysisServices' 14-file Azure closure survived,
        i.e. the later publish deleted the earlier one's files. `-Clean` restored all five.
        - **⚠⚠ THE SYMPTOM NAMES THE CONNECTION STRING, NOT THE PAYLOAD**, which is what makes it expensive:
          an ATTACH fails with *"open_catalog failed: A data source must be specified in the connection
          string"*, on a connstr the service tier had accepted minutes earlier. **The discriminator is
          `PROVIDER 'sqlserver'` on the same ATTACH**, which answers *"unknown provider 'sqlserver'.
          Registered providers: dax, engineeredwooddelta, dlrest"* — naming the real fault in one line.
          Reach for that before doubting the connstr, docker or the secret.
        - ⚠ **The assembly being PRESENT is what disguises it**: `BackendRegistry` skips an unloadable name
          on purpose, so a provider whose own DLL is there and whose closure is not is reported as ABSENT
          rather than as broken. `ls <payload> | grep -ic sqlclient` ⇒ must be **5**.
        - ⇒ **restated: publish with `-Clean` after ANY round of publishes that did not all target the same
          directory**, mutation testing included — not only after moving a package.
  - **(1) ✅ DONE 2026-08-18 — ABI v75, breaking, no aliases. Hermetic 69/69 — 7499, i.e. 7558 minus exactly
    the 59 assertions of the two deleted suites, so NO surviving suite moved. Full as-built:
    [docs/abi-history.md](abi-history.md) §v75. ⚠ ONE THING THE SCOPING DID NOT ANTICIPATE: fabricator
    now calls `ExtensionHelper::AutoLoadExtension` NOWHERE** — the only call lived inside this registration,
    and `docs/distribution-installer.md` cited it as the in-tree proof that chain-loading during load is
    lock-safe, so that citation was re-anchored to DuckDB's own source. Original scoping, kept because every
    fact in it held:
  - **`fabricator_delta_mfr_scan` REMOVAL — ⚠ IT IS NOT DEAD CODE, and the user's first framing ("dead <!-- check-docs:ignore (REMOVED at ABI v75; naming it IS the point) -->
    code from a multifile reader we only tested but never used") is half right.** It IS registered
    (`loader.RegisterFunction`), IS deletion-vector correct, and carries **59 assertions in two hermetic
    suites** (`verify_delta_mfr_scan` 36 + `verify_delta_mfr_dv` 23) green in both tiers. It is ABSENT from <!-- check-docs:ignore (REMOVED at ABI v75; naming it IS the point) -->
    the README — a spike that shipped by accident, the `fabricator_delta_native_scan` pattern again, except
    that one was WRONG and this one is correct and covered. ⚠ Its header comment *"Slice 1a: file list only
    (no DV / partition / pushdown yet)"* is STALE — DV landed in slice 1b.
    - To remove: `src/fabricator/fabricator_delta_mfr.{cpp,hpp}` + its registration; the `delta_list_files` <!-- check-docs:ignore (REMOVED at ABI v75; naming it IS the point) -->
      ABI entry (`abi.h`, `clr_host.{hpp,cpp}`, `Abi.cs`, `Bootstrap.cs` → `DeltaReader.ListScanFilesJson`);
      both suites; floors **71 → 69** and **7558 → 7499**. **ABI bump (v75), breaking.**
    - **Upside for the split**: it deletes the LAST core→Delta coupling outright instead of abstracting it
      (the only others were `BackendRegistry`'s `new DeltaBackend()`; everything else was comments).
    - **⚠ COST, stated once so it is not re-discovered as a surprise**: it discards the working prototype of
      the "form (b)" architecture (`MultiFileList` carrying OpenFileInfos from the snapshot — duckdb-iceberg's
      shape), which this file records as the durable read-path form. MEASURED 2026-08-18: native parquet does
      the same 6M-row aggregate in **0.203 s** vs **0.592 s** best-tuned through our Arrow boundary — so form
      (b) is worth ~3x where batch/thread tuning got 31%. "Move it to C# as a custom function" is NOT an
      option: the whole point is DuckDB's C++ MultiFileReader doing the read, and the C# version of that idea
      already exists and IS the production path (`DeltaNativeReader`).
  - **(2) THE ASSEMBLY SPLIT — "split by assembly, group by directory, KEEP the namespace" (user, 2026-08-18).**
    That follows the precedent already in the tree: `Fabricator.Abstractions` is a SEPARATE ASSEMBLY in the
    SAME `Fabricator.Bridge` namespace ("Same Fabricator.Bridge namespace — assembly split only"), and
    `FabricApi/` already groups 21 files by directory without a namespace of its own. Regrouping into
    namespaces would fight that decision and churn `using`s across four assemblies for no dependency benefit.
    - **MEASURED composition** (96 files / 36 221 lines): **Delta provider 20 files / 17 892 / 49.4%**;
      **Fabric REST API 21 / 7 625 / 21.1%**; ABI+hosting 13 / 3 596; storage FS 12 / 3 058; function
      machinery 16 / 1 813; Arrow interop 12 / 1 513; DML/bulk 2 / 724. **~70% of "Bridge" is two tenants
      that are not the bridge**; the genuinely shared remainder is ~10.7k lines.
    - **The shared part is REAL, not hypothetical**: 18 Bridge types are used by 2+ of the other provider
      assemblies (`ArrowValueReader` in 10 files, `InterruptScope`, `InMemoryArrayStream`, `BackendRegistry`,
      `InOutExchange`, `DbDataReaderArrowStream`, `CatalogFunctionSet`, `AmbientTransaction`/`AmbientOpener`,
      `StaticTableFunction`, `ColumnAppender`, `ArrowDataReader`, …).
    - **Delta is the anomaly and the code admits it**: every other backend is a separate assembly discovered
      by reflection (`FABRICATOR_BACKEND_ASSEMBLY`, default `Fabricator.SqlServer,Fabricator.AnalysisServices,
      Fabricator.DeltaRs`); Delta alone is hard-registered at `BackendRegistry.cs:124` with the comment *"The
      built-in Delta provider lives in the Bridge (alongside DeltaReader / engineered-wood), so it isn't
      [discovered]"*.
    - **The cost is not aesthetic**: Bridge project-references the **engineered-wood submodule** and packages
      **Microsoft.Fabric.Api / Azure.Identity / Azure.Storage.Files.DataLake / AWSSDK.S3** for those two
      tenants — which is exactly what `Fabricator.Bridge.Tests.csproj` cites as the reason tier 0 CANNOT
      reference Bridge and must link individual source files.
    - **FREE WIN, do it first and independently**: `Microsoft.Data.SqlClient` + `.Extensions.Azure` appear
      UNUSED by Bridge (the only two matches are a comment saying *"rather than reference
      Microsoft.Data.SqlClient"* and a file that deliberately hand-rolls a connstr parse to stay BCL-only),
      and `Fabricator.SqlServer` declares both itself.
    - ⚠ **`DeltaCatalog.cs` depends on the Fabric REST API** (OneLake discovery), so Delta and Fabric cannot
      be split as independent peers — Delta would reference Fabric, or the OneLake bits stay with Delta.
    - ⚠ **`clr_host.cpp` hardcodes `Fabricator.Bridge.dll` and `Fabricator.Bridge.Bootstrap`**, so BRIDGE
      keeps its name (a split moves Delta OUT). A new assembly needs its own `Publish-Project` line in
      `publish-managed.ps1`, as DeltaRs already has, and the `FABRICATOR_BACKEND_ASSEMBLY` default list
      updated. Unchecked: whether the extraction compiles without a circular reference, and the AOT plan's
      source-generator design ([docs/aot-bridge.md](aot-bridge.md)), which assumes the current registry.
    - Incidental: 47 empty `dotnet/Fabricator.Bridge/duckdb_unittest_tempdir/<pid>/` dirs, untracked and NOT
      gitignored (git cannot show them — it does not track empty dirs).

- **SqlClient PINNED TO 7.0.2 (2026-08-10, user-asked; was 6.0.2) — AND IT NEEDED A SECOND PACKAGE THAT NO
  CI TIER WOULD HAVE DEMANDED.** `Microsoft.Data.SqlClient` 7.0 **MOVED the Entra (Azure AD) authentication
  providers out of the core package** into `Microsoft.Data.SqlClient.Extensions.Azure`, so every
  `Authentication=Active Directory …` connection string — Fabric Warehouse via a service principal, the
  Fabric SQL endpoint, Azure SQL with Entra, the ambient notebook token — fails at CONNECT with *"Cannot find
  an authentication provider for 'ActiveDirectoryServicePrincipal'"*. Added at the same version (they ship as
  a matched pair), which forced **`Azure.Identity` 1.13.2 → 1.21.0** (the extension requires ≥ 1.18.0; the
  downgrade surfaces as `NU1605`, an error here because warnings-as-errors is on).
  - **⚠ NEITHER CI TIER CAN CATCH ITS ABSENCE, and that is the durable lesson: the docker rig authenticates
    with `sa`/password, so tier 1 and tier 2 stay GREEN — 48/48 — 1867 — while every Entra attach in the
    product is broken.** It surfaced only by re-running a LIVE Fabric shape after the bump. Any future
    SqlClient bump needs a live Entra attach before it is believed, and the same hole exists for anything
    else auth-shaped.
  - Gates after the bump: service **48/48 — 1867** and hermetic **67/67 — 6895**, both IDENTICAL to the
    pre-bump counts ⇒ behaviour-preserving on everything the tiers reach.
  - ⚠ `7.1.0-preview2` exists and is deliberately NOT taken: a preview does not belong in the shipped
    dependency. Note [docs/aot-bridge.md](aot-bridge.md) records the AOT SKU as targeting 7.1+, so that
    plan's pin and this one will need reconciling when 7.1 ships stable.

- **`MERGE INTO` — BUILT + GATED 2026-08-05 (C++-only, no ABI bump). Full record moved verbatim to
  [docs/feature-history.md](feature-history.md) §MERGE INTO (2026-08-23).** One override,
  `FabricatorCatalog::PlanMergeInto` (`src/catalog/fabricator_merge_into.cpp`), lifted the shared refusal
  for EVERY provider at once; DuckDB lowers each action to the same `Logical{Update,Delete,Insert}` the
  standalone statement produces, so MERGE INHERITS our rowid DML rather than re-deriving it. Gates
  `verify_merge_into` **209 × 2 engine legs** (hermetic) + `verify_merge_into_mssql` **106** (service).
  - **⚠⚠ IT SHIPPED A SILENT-DATA-DESTRUCTION BUG FOR HALF A DAY, and the shape of the miss is the point:
    the hazard needs several affected rows in ONE FILE, with the DELETE FIRST.** On Delta ×
    `deletion_vectors=false` × autocommit × ≥2 mutating actions, every action consumed rowids from ONE join
    scan while committing separately — so a copy-on-write DELETE renumbered the rows a later action had
    already addressed. Measured: two conditional deletes left `2, 3` — id3 NOT deleted, **id4 DESTROYED**,
    exit 0. Every test missed it because with a row per file a rewrite renumbers nothing, and both tiers
    were GREEN throughout. **The user's one question was worth more than the whole suite run.**
  - **THE FIX: a merge carrying ≥2 row-addressing actions is FORCED TO BUFFER, even in autocommit** — set
    at EXECUTION time (`GetGlobalSinkState`), never at plan time, because a prepared statement's plan is
    reused across transactions. Scoped to `rowid_actions >= 2 && entry.HasVirtualRowId()`: the hazard needs
    a TRANSIENT (file, position) rowid, so where identity is real key columns (SQL Server) it is immune and
    forcing there only cost the external-table capability.
  - **⚠ THE COUNT EXCLUDES `INSERT` — counting every MUTATING action refused the commonest merge shape**
    (`WHEN MATCHED UPDATE` + `WHEN NOT MATCHED INSERT`) on a non-DV table where it had always been correct.
    An INSERT addresses no existing rows, so it can neither renumber nor hold targets. **The smell is TWO
    OPERATIONS ADDRESSING PRE-EXISTING ROWS — not "two commits"**, which tracks neither risk nor atomicity
    (a CTAS is two commits with no hazard; a `CREATE OR REPLACE … AS SELECT` is one).
  - **⚠ The `!HasRowId()` guard is required for EVERY merge, including an INSERT-ONLY one** — DuckDB tests
    the rowid column for NULL to decide matched-vs-not, so with no rowid `row_id_start` points ONE PAST the
    chunk and `ComputeMatches` reads out of bounds: `INTERNAL Error`, then the database is FATALLY
    INVALIDATED. Refuse at plan time, where it can still be a message.
  - **⚠ `parallel` MUST stay false**: every action shares ONE global sink state, and `PhysicalMergeInto`
    drives our operators MANUALLY rather than as a pipeline.
  - Atomicity is the TRANSACTION's, not the statement's: autocommit yields ONE COMMIT PER ACTION on Delta
    (so the change feed is SPLIT across versions), `BEGIN; MERGE; COMMIT;` fuses them into one with an exact
    pre/post-image pair. `commitInfo.operation` is `TRANSACTION` for a fused merge and NOTHING we write
    ever says `MERGE`, so a foreign consumer keying on that will not match us.
  - **STILL OPEN**: the SQL Server half is correct but runs as per-row DML, not a server-side T-SQL `MERGE`
    (a pushdown needs source AND target in the same catalog); `ON CONFLICT` is lowered to a MERGE by the
    binder since 1.5.x and still fails because `GetStorageInfo` advertises no uniqueness — semantically
    CORRECT on Delta, and on SQL Server the remaining work is `GetStorageInfo`, not the merge hook.

- **THE UPDATE POST-IMAGE GROUPED FLUSH — DONE 2026-08-06 (C#-only, no ABI). ⚠ IT DOES NOT FIX "UPDATE
  MEMORY", AND THE MEASUREMENT SAYING SO IS THE MOST USEFUL THING ABOUT IT. Full record moved verbatim to
  [docs/feature-history.md](feature-history.md) §The UPDATE post-image grouped flush (2026-08-23).**
  Both UPDATE paths used to accumulate EVERY post-image batch (and every pre-image on a CDF table) before
  writing anything; they now write a group's worth as the read-back streams and keep only the actions. Still
  exactly ONE commit. Threshold `DeltaReader.UpdateGroupBytes` = 64 MiB, env-overridable.
  - **MEASURED, on the shape that favours it most (600k × 16 VARCHAR): managed heap peak 327 → 171 MB** and
    now bounded by the GROUP rather than the statement — but **process peak working set only 614 → 548 MB**,
    and on a NARROW table it does not fire at all (**449 MB either way**). So the earlier "~474 MB per 1M
    matched rows" figure was never mostly this. Flush count costs nothing measurable (71 flushes is as fast
    as 5).
  - **⚠ THE ACTUAL DOMINANT TERM, found by instrumenting the working set along the path: ~180 MB is spent
    BEFORE the read-back begins**, in `ExecuteUpdate`'s `Dictionary<long, object?[]>` of **BOXED** SET
    values. MEASURED with a control that isolates it — same table, every row touched, only the SET count
    differing: **DELETE (rowids only, no boxes) 204 MB / UPDATE 1 column 454 MB / UPDATE 3 columns 651 MB**
    ⇒ **~98 BYTES PER 8-BYTE BIGINT VALUE**, a ~12x representation overhead, and a 3.2x TIME gap. The DELETE
    floor is what makes it OURS rather than DuckDB's.
  - **NEXT FIX (scoped, not built): keep the SET values in ARROW form instead of boxing.** Four constraints
    found while scoping, each of which makes the naive version wrong: **the incoming batches CANNOT be
    RETAINED** (`Materialize` does a full IPC round-trip precisely because "the source batches may be freed
    after consumption"), `ArrowArrayConcatenator.Concatenate` DOES exist and is public (an earlier note here
    said otherwise — that came from reading ONE class's surface and generalising, the same backwards-search
    error), `updates[rid] = vals` DEDUPLICATES last-write-wins and sets the reported row count, and the
    boxing is currently also doing a TYPE CONVERSION.
  - **⚠ IT IS INERT ON THE BUFFERED PATH, and this entry claimed otherwise until it was measured.** Same
    table, same 60k UPDATE, threshold forced to 1 byte: **autocommit 30 group flushes, buffered 1.** The
    mechanism is NOT autocommit-vs-buffered but WHICH READER: DuckDB's `read_parquet` yields 2048-row
    vectors, engineered-wood's codec reader yields **one batch per ROW GROUP** — and the buffered read-back
    opens with a bare `DeltaWriter.Options()`, passing no `dataFileReader`, so it takes the codec reader
    ALWAYS. **When two callers of one method behave differently, diff what they CONSTRUCT it with before
    diffing the call.**
  - **⚠ FILE LAYOUT IS UNCHANGED BY CONSTRUCTION** — `WriteDataFilesAsync` writes one parquet file per
    (input batch × partition), so N read-back batches become N data files whether they arrive in one call or
    a hundred. That is what makes the grouping free rather than a trade-off.
  - **⚠ THE ALL-OR-NOTHING ROW-ID RULE HAD TO MOVE EARLIER** (a group is written before later groups' ids
    are known), so it is decided from the FILES — every selected file has a `baseRowId` — which is the same
    condition and costs a dictionary lookup. Where it cannot be established the threshold is DISABLED and
    the statement buffers whole, byte-identically: **a legacy table keeps its old behaviour instead of
    acquiring new semantics from a memory fix.**
  - **⚠ GATE: `verify_delta_update_grouped` (72), and the runner must FORCE the threshold** — no hermetic
    suite comes within two orders of magnitude of 64 MiB, so without
    `FABRICATOR_DELTA_UPDATE_GROUP_BYTES=1` on that ONE suite the grouped path ships with ZERO coverage;
    `unset` for every other suite is load-bearing in the other direction too. It passes IDENTICALLY at the
    default threshold, and that equivalence is the point. Mutation-tested: not clearing the per-group
    pre-images **survives 51 assertions** before the CDF section catches 12144 pre-images for 6000 rows.

## Appendix — records moved verbatim from CLAUDE.md (2026-11-16)

These were open-thread or reference records in CLAUDE.md. They are closed, so they moved here word for word in the 2026-11-16 cleanup; links inside them are written relative to the repo root, as they were in CLAUDE.md.

### From CLAUDE.md §Target architecture — the "suggested order" step record (1)–(4g)

- Suggested order: (1) **C# multi-backend registry — DONE** (`BackendRegistry` is provider-keyed:
  `IBackend.Name`/`Aliases`, `Resolve(provider)`, `Active`=default; multi-assembly discovery via
  `FABRICATOR_BACKEND_ASSEMBLY` comma-list; SqlServer = `"sqlserver"`/alias `"mssql"`. Behavior-preserving —
  `Active` still routes to SqlServer); (2) **provider selection — DONE** (`open_catalog(provider,…)` ABI
  v17 → `BackendRegistry.Resolve`; ATTACH `PROVIDER` option + `scheme://` inference; clean unknown-provider
  error). The **generic names are now live as ADDITIVE ALIASES** (no breakage): `fabricator_query`/`fabricator_exec`/
  `fabricator_functions`/`fabricator_server_info` (+ the existing `fabricator_version`) and `ATTACH … (TYPE fabricator)`
  (the storage extension is registered under both `fabricator` and `fabricator`). Its gate, `test/verify_generic_names.test`, was DELETED by the rename (`2a26b7a`) together with the aliases it pinned — there is no such suite now. <!-- check-docs:ignore (naming it IS the point) -->
  The **breaking removal** of the `fabricator_*` names (+ catalog-type string `"fabricator"`, settings/secret/URI
  scheme rename, compat-corpus regen) remains the separate full-rename pass;
  (3) **connstr/auth → C# — DONE** (`build_connection_string` ABI v18: `fabricator_secret.cpp` reads the
  secret + emits its fields as JSON, `SqlServerBackend.BuildConnectionString` assembles the SqlClient
  connstr; `MapAuthentication`/`QuoteConnValue`/the access-token marker are now C#-only — C++ has no connstr
  knowledge); (4) dynamic functions — **(4a) function discovery DONE** (`fabricator_functions(catalog)` table
  fn + `FABRICATOR_META_FUNCTIONS`); **(4b) attach-time catalog-bound scalar UDFs DONE** (discovered scalar
  UDFs become `ScalarFunctionCatalogEntry` in `FabricatorSchemaEntry`, resolved as `db.schema.fn(args)` and
  executed over Arrow — ABI v19); **(4c) attach-time catalog-bound table-valued functions DONE** (discovered
  TVFs become `TableFunctionCatalogEntry`, resolved as `SELECT * FROM db.schema.tvf(args)`, with real
  SQL-level projection + best-effort filter pushdown reusing the table scan's machinery — ABI v21);
  **(4d) attach-time catalog-bound stored procedures DONE** (procs with a determinable result set resolved
  as table functions — `sp_describe` schema, `EXEC` execution, no pushdown — ABI v22; **named/optional
  params DONE (4d-2)**; **OUTPUT params + RETURN value as flat columns DONE (4d-3)**; multi-result-set +
  INPUT/OUTPUT deferred); **(4e) attach-time custom C#-authored scalar functions DONE** (`ICatalogScalarFunction`)
  **+ (4f) custom table functions DONE** (`ICatalogTableFunction`) — both reuse the catalog scalar/TVF path,
  C#-only, no ABI; chosen over load-time global (deferred). **(4g) table-in-out DONE** (ABI v23 —
  `inout_open`/`push`/`finish`/`abort`; full plan + corrections:
  [docs/custom-functions-design.md](docs/custom-functions-design.md) §11.1): a discovered TVF `db.s.tf`
  gains a **sibling `db.s.tf_each(<input table>)`** (⚠ since 2026-08-02 the PROVIDER declares that sibling —
  see "THE `_each` MOVE" below; the host no longer invents it) that applies it **once per input row** via SQL-Server
  `CROSS APPLY` (T-SQL generated by C#, run on SQL Server — NOT a DuckDB lateral join), over the §11
  coordinated bounded channel (one session per call; parallel `UNION ALL` input fed thread-safely). Output =
  the echoed input columns (typed as the TVF **parameters**, since C# CASTs the VALUES to them) ++ the TVF
  output columns. **Two corrections found during the build:** (1) DuckDB forbids a `{LogicalType::TABLE}`
  overload coexisting with the scalar-arg scan form under one name (`bind_table_function.cpp`: "TABLE
  parameter, and multiple function overloads — not supported") → the in-out form is a **separate `_each`
  catalog entry** (single TABLE overload), the scan form (4c) keeps the bare name; alias tracked in
  `FabricatorSchemaEntry::inout_functions_`. (2) **Output is emitted SYNCHRONOUSLY per input chunk** (each
  `inout_push` runs that chunk's CROSS APPLY to completion + returns its full output) → there is **no tail**,
  so emitting rows never depends on detecting which parallel branch finishes last. This replaced an unsound
  first attempt (an atomic last-branch counter in `in_out_function_final`): `PhysicalUnion::BuildPipelines`
  can run UNION branch pipelines **sequentially**, so branch 1 could finish (counter→0, premature finish)
  before branch 2 starts → lost rows; it passed tests only by scheduling luck (caught in review). Also
  `OperatorFinalize` is a **global single-shot hook handed no `DataChunk`** so it **can't emit rows** — it's
  reserved for the per-row-proc COMMIT (a no-rows action). Session lifecycle = a refcounted
  `InOutSessionHolder` on the bind data; its **RAII destructor → `inout_abort`** is the release/rollback
  backstop on every teardown path (also frees the GCHandle). Verified: `test/verify_table_inout.test` (63
  assertions — incl. parallel UNION ALL, ORDER BY+LIMIT, WHERE, aggregate, empty, multi-column, large
  BIGINT→INT, error+recover). **(4g-custom) custom C#-authored table-in-out DONE** (now `ICatalogInOutFunction` /
  the `StaticInOutFunction` base — in-out analog of 4e/4f): a pure-C# **per-chunk streaming** table-in-out (no SQL object), may keep mutable
  state across chunks (running aggregate); surfaced as `kind='inout'` → C++ `AddInOutFunction` registers a
  bare-name `{TABLE}` entry (`GetOrCreateCustomInOutFunction` + `FabricatorCustomInOutBind`, output = the fn's
  full declared schema, no input echo), dispatched in C# (`CustomInOut` factory registry → fresh instance per
  session; `CustomInOutSessionImpl` runs `Process(chunk)`). Reuses the 4g operator path — no new ABI/C++
  operator. Verified: `test/verify_custom_functions.test` (cf_tag per-row, cf_running_sum stateful 4999-row
  multi-chunk, per-session state, no SQL object). **(4g-proc) per-row stored-proc in-out DONE**: a discovered
  proc also gets `_each` (`db.s.usp_x_each(<table>)` EXECs the proc once per input row — a proc can't be
  inline-CROSS-APPLY'd). The per-row EXECs run on **DuckDB's pinned transaction** (`BeginWrite`), so the
  proc's writes commit/roll back with **DuckDB's** COMMIT/ROLLBACK — atomic in autocommit AND inside an
  explicit DuckDB `BEGIN`, no per-row commits. **`OperatorFinalize` is NOT used for the commit** (committing
  the in-out's own txn at operator-finish would commit before a user's explicit `ROLLBACK` could undo it; the
  transaction manager is the correct signal). Now on the Phase 6 streaming exchange (`SqlServerProcEach :
  IInOutFunctionBinding`, resolved by `InOutBind`; was the 4g push `ProcInOutSessionImpl`, retired in `9056eae`):
  `DoExchange` per row runs `DECLARE @t TABLE(<proc result>); INSERT @t EXEC [s].[p] @param=@p,…;
  SELECT <echoed input>, t.* FROM @t;` on the pinned conn/`_txn` (echo server-side → output = input columns ++
  proc result columns; result-set procs only). Verified: `test/verify_proc_inout.test` (echo output, autocommit
  commit, row-failure rolls back the whole statement, explicit-`BEGIN` read-your-writes + `ROLLBACK` undoes —
  31 assertions). **(4g-finalize) the injected `OperatorFinalize` DONE**: an `OptimizerExtension`
  (`RegisterFabricatorInOutFinalizer`) wraps each in-out `LogicalGet` (identified by `function.in_out_function
  == FabricatorInOutFunction`) in a pass-through `LogicalExtensionOperator` whose `PhysicalOperator`
  (`PhysicalOperatorType::EXTENSION`) forwards rows 1:1 and, in `OperatorFinalize`, calls `holder->Finish()`
  → C# `inout_finish`. This is the reliable single "in-out finished" signal (fires **once**, sink-level, even
  above a parallel UNION — verified empirically + via `MetaPipeline`/executor finish-event scheduling),
  intended as a C# resource-cleanup hook + a clean commit of the read-only TVF's snapshot transaction
  (NOT the proc commit). **4g (table-in-out) is fully complete.**

### From CLAUDE.md §Next up — session records and handoffs 2026-11-14 … 2026-11-04

- **2026-11-14 later: four commits on top of v2.0-cyanoptera 7e7dfc3, FAST-FORWARDED INTO v2.0-cyanoptera AND PUSHED (7e7dfc3..854ff17): `71be528` NuGet SqlClient 7.1.2 (+Extensions.Azure), DataLake 12.28.0 (GetPathsAsync args now explicit), Fabric.Api 2.27.0 (GetStatus includeFilesDetails ambiguity), ADOMD 19.118.3 (verify_dax 66/66 vs PBI Desktop, table esg vwDimEntity / "Business Description"); `df2c6ca` Fabric.Api 2.27 features: refresh_sql_endpoint(tables := ...) (max 25 per request, BCL-only FabricRefreshTables + 11 tier-0 tests, floor 474), git_status(files := true), items.logical_id, connections.my_role, BOOLEAN named params of FabricRowsFunction now reach Fill (docs/fabric-api-functions.md §9o); `5b7bced` Fluid 3.0.0-beta.7 -> beta.9 PORT (Parlot 2.2.1; TemplateOptionsBuilder, *.Create, ctx conversions, EnterScope(Local), FabricatorFluidParser.OptionalValue; behaviour: dict renders {"k"=>"v"}, top-level break ends the render, tag delimiters inside comments are parsed -> 3 macro comments reworded; docs/fluid-templating.md §45). Tiers: hermetic 101/101 13385 on every step, service 92/92 137768 for 71be528 and df2c6ca, Fluid service suites green for 5b7bced. Prereleases NOT taken: DataLake 12.29 beta, SqlClient 8.0 preview. Logging 11 / test tooling majors open. Then `854ff17` Fluid #1057 IFluidArray: ArrowRowList (rows made on first read) for input_table and query results; query results keep their Arrow batches for the render (RowLease, FluidRenderSession.Hold, released per render in FluidEngine.RenderToStringAsync); 1M rows r[999999] 1.82 -> 0.09 s; verify_plugin_fluid §41 994 -> 1003, hermetic 101/101 13394 (floor set); docs/fluid-templating.md §46.
- **2026-11-14: CODEC READ PATH — FAST-FORWARDED INTO v2.0-cyanoptera AND PUSHED (fc4a434..7e7dfc3, incl. e210ff7 native scan units): `debe42c` remote reads through DuckDB's file cache (C++ ALWAYS_CACHE in HostFsOpenRead), onelake:// data reads, range coalescing, read hint 4, scan from the stored snapshot + OneLake size seeds (S3 35.4 -> 27.8 s, OneLake 119 -> 46 s; gate verify_delta_codec_file_cache, service); `f2a3aa6` EW bump 3dadb699 -> c1574081 (176 commits, zero patches; alone S3 27.8 -> 24.6 s masks off); `7e7dfc3` join filters as EW row masks, ABI v103 join_filter_lookup (renumbered from exp v102). Masks cost 10% at the default budget (27.1 vs 24.6) and save 7% at 16 reads + 2 GiB (23.1), +4.5 GB peak (DuckDB itself ~9 GB at SF10); user: commit anyway, native is the main provider, re-measure with new EW. Knobs FABRICATOR_EW_READ_CONCURRENCY / FABRICATOR_EW_BUFFERED_READ_BYTES / FABRICATOR_JOIN_FILTER_MASKS=0. bench.py now records peak working set (_peak_ws_mb). Tiers hermetic 101/101 13385, service 92/92 137768 (floors set). Docs: delta-read-path.md §codec remote reads, ew-late-decoding-join-filters.md §7-§8, abi-history v103. UNITS VS ORDERED MEMORY (exp/codec-units-861-masks, S3 cache, best warm / process peak): 256 MiB ordered (4 reads) 27.7 s 9.4 GB vs units 32.7 s 11.6 GB; 512 MiB ord16 26.2 s 15.7 GB vs units 27.1 s 12.8 GB; 1 GiB 23.2 s 15.2 GB vs 24.4 s 12.9 GB; 2 GiB 22.8 s 13.9 GB vs 23.4 s 14.2 GB => no robust memory argument for units (the process peak is dominated by DuckDB hash tables ~9 GB and noisy; EW LastScanBudget.Peak would be the precise measure). build/release-v2 is back on feat/codec-read-path (7e7dfc3).
- **2026-11-14: EW #861 REVIEW MEASURED — THE GAIN WAS THE BUDGET, NOT THE ORDER (Curt was right).** EW branch `perf/scan-units-v2` (worktree D:/repos/ew-units, commit c046a3f1 on upstream main c1574081, LOCAL, not pushed) addresses every review point (PrepareScan shared with ReadCoreAsync incl. ResolveClockWords; planning error rethrown to every caller; per-unit reservation on one shared ScanMemoryBudget, front = earliest unit out, LastScanBudget; caller token cancels the planner; OpenPartitionsAsync gone; readers before the token; tests in ScanUnitsTests.cs + ClockWordsOnceTests, three mutants killed; benchmark drains ordered with 16 consumers). Measured TPC-H SF10 codec pushdown_filters 'all' on the PR stack (branch exp/codec-units-861-masks = WIP 4f9719a + new EW; bare stack exp/codec-units-861): RustFS cache ordered 27.4 / ordered 16 reads + 2 GiB 23.5 / units shared 256 MiB 33.5 / units 2 GiB 23.7 / units no budget 23.6; RustFS no cache 41.8 / 34.9 / 44.7 / 35.1 (4 reads + 2 GiB 39.0); OneLake no cache warm 319 / 268 / 303 / 263, cache 49.1 / 38.3 / - / 38.9. Window AND budget together give it; units add nothing measurable. Env knobs (measurement only): FABRICATOR_EW_BUFFERED_READ_BYTES, FABRICATOR_EW_READ_CONCURRENCY. Logs logs/r861*, results scratchpad/rustfs-bench/results/duckdb_codec_861*. Reply draft for the user to post: session scratchpad ew861_reply.md (proposes closing the PR for an OrderedScan budget issue). Fabricator could set MaxConcurrentReads/MaxBufferedReadBytes for codec reads today (memory decision). payload build/release-v2 is currently built from exp/codec-units-861-masks. LATER: 4 reads + 2 GiB (S3 cache 24.5, OneLake cache 40.4, OneLake no cache 327) = with cache the budget is the lever, without cache only reads + budget together; 16 reads 1 GiB / 512 MiB: S3 cache 24.5 / 26.2, no cache 35.2 / 37.9, OneLake cache 43.6 / 50.0, OneLake no cache 1 GiB 302. EW ScanUnitsBenchmarks (16 consumers, datastall back): ordered 16 reads + 2 GiB = units incl. datastall (215 vs 205-212 ms) => stall isolation does not hold either. DECIDED (user): no separate issue, no push; the reply (ew861_reply.md) carries numbers + the OrderedScan proposal and lets Curt close the PR.
- **2026-11-13: NATIVE READ AS SCAN UNITS — COMMITTED `e210ff7` on local branch `feat/native-scan-units` (off v2.0-cyanoptera fc4a434, NOT pushed, NOT merged): a CLEAN PORT, unit mode only, ABI v102 `scan_next_unit` (Bridge `ScanUnits`/`IScanUnitSource`; docs/abi-history.md §v102). Tiers on the port: hermetic 101/101 — 13375 (floors set), service 91/91 — 137757. The full previous state (codec units prototype with partition mode v103/v104 + EW pin on local EW commits 389a6b56, codec file cache + its suite, probes) is saved as WIP `4f9719a` on exp/ew-row-masks; ⚠ there v102 = join_filter_lookup, renumber when it moves over. Description of the feature as measured on the exp state: SET fabricator_scan_units (default true; C++ arrow_ingest offers its thread count only when on, replaced the FABRICATOR_SCAN_PARTITIONS env gate for the native path — the codec path still needs the env var), Delta ATTACH scan_units (default true) + scan_unit_min_bytes (default 32 MiB); fab_delta_info reports scan_units (verify_delta_nested_schemas 5 -> 6 rows). Gate NEW hermetic verify_delta_scan_units (74; storage_buffer_size := 1 because duckdb_logs() does not flush; a parallel write cuts 5 OR 6 row groups, so unit counts are not pinned), mutants off-by-one/slice-ignored-on-DV-path/units-never each die at their own section. Tiers hermetic 101/101 — 13385 (= 13311 + 74, floors set), service 92/92 — 137768 (= floor). README rows + docs/delta-read-path.md 'Scan units'. Upstream EW #861 review (Curt: budget question, ResolveClockWords bug, error propagation, rebase) deliberately AFTER this. HISTORY of the prototype:** `DeltaNativeReader.Read` split into `Prepare` (listing, filters) + `Open(prepared, listing)`; new `DeltaNativeReader.Units.cs` cuts the planned files into consecutive groups (>= max(total/(2*threads), 32 MiB, FABRICATOR_NATIVE_UNIT_MIN_BYTES)) and serves them through the existing ABI v104 `scan_next_unit` path, one inner host query per unit; gate in `DeltaCatalog.ScanNative`: no pending writes, no rowid, no pushed LIMIT. TPC-H SF10 RustFS, file scan OFF, 22q best of 3, all correct (logs/native_units.jsonl): autocommit 33.2/39.6 -> 19.1/19.3 s, in BEGIN (the dbt path) 34.1/34.0 -> 19.2/19.0 s, wall ~125 -> 62 s, cores 3.8 -> 11; Q3 1.9 -> 0.54, Q4 1.15 -> 0.28, Q20 1.9 -> 0.81; Q22 0.52 -> 0.58 (tiny tables). With DuckDB PR #28781 on top: 18.9/18.7 s (adds ~nothing once the pull is parallel). File scan (autocommit default) stays 12.9 s. lineitem has 9 files => 9 units max; next lever: row-group slices of large files. To ship: a setting, tests, rowid/pending paths. LATER SAME DAY (logs/native_slices*.jsonl): (1) units BATCH from one file on through the PLAIN form (declared schema map, no parquet_schema footer probe per unit; was the per-file path because MinFiles=2) - RustFS -1..4%, should matter on OneLake (not measured yet); (2) ROW-GROUP SLICES: a file >= 2x target is cut at its row-group boundaries from ONE parquet_metadata query (42 ms for 9 footers on RustFS), a slice = plain form + file_row_number => true + range (aligned range scans exactly 1 row group, measured; a misaligned one decodes the shared row group twice); a footer whose row sum != numRecords is not split. Measured: slicing EVERY big file is mixed - Q13 1.94 -> 1.11, Q22 0.59 -> 0.36, but lineitem scans slower (Q1 1.14 -> 1.26): a whole-file query already reads its row groups in parallel, and the file_row_number filter costs ~25% CPU on a bare scan. Rule kept: slice only when files < threads/4 (FABRICATOR_NATIVE_UNIT_SLICES=0 off). Sum of best times: files 18.12, slice-all 17.33, rule 17.04 s (runs 17.2-18.0), all correct. ONELAKE (LH_bench, logs/ol_native_units.jsonl, file scan off, 22q x 3 rounds, all correct): one stream best 42.6 s (warm rounds 44.6/45.5), units with footer probe 27.4, plain-form units 27.0, plain + slices rule 24.8 (warm 25.4/33.3 - noisy) vs the file scan's 22.2 (ol854 run); cold round ~76 s in every unit variant. Probe vs plain form is a wash WARM: DuckDB's external file cache serves the footer, the 1253 azure calls are identical in all four. Cores only 4.6-4.9 on OneLake (13 on RustFS).
- **2026-11-13: DuckDB PR #28781 (`e2f9f75`, main/2.1 only, NOT on v2.0-cyanoptera): the consumer of a streaming result runs 1-chunk task slices instead of 50.** MEASURED on v2.0.0 + the patch (worktree D:/repos/duckdb-pr28781, build tree build/release-v2-pr28781, shell only; log logs/pr28781_ab.jsonl), TPC-H SF10 RustFS native, interleaved A/B: host-query path (file scan OFF) best-sum 35.3/34.8 -> 30.6/32.7 s (~7-10%), wall 126 -> 120 s; Q8 3.5/3.1 -> 1.6/1.3, Q9 2.6 -> 2.2/1.8, Q19 1.4/1.7 -> 1.0/0.9; Q1 UNCHANGED 2.1 s (so the 0.36 vs 2.1 s host-query gap is NOT this); file scan ON (default) 12.87 vs 12.85 = control, no effect. Helps only the fallback/host-query paths; worth a 2.0 backport request, not a pin change.
- **2026-11-11: DuckDB join-order fix duckdb/duckdb#27493 (`<>` / IS DISTINCT FROM joins estimated like range joins -> TPC-DS Q19/Q64 nested loops, 200 s / out of spill at SF=100) is on v2.0-cyanoptera (a725d696d8, 22.10.) but NOT in our pin v2.0.0 (375 commits behind). USER DECISION: wait for v2.0.1, do not pin the unreleased branch tip. On the bump: re-run TPC-DS at a larger SF and check Q19/Q64 plans.**
- **2026-11-11: EW #502 ROW MASKS TESTED on local branch `exp/ew-row-masks` (commits `ab67f63`, `579734e`, not pushed; EW submodule on `feature/row-level-filtering` 53b8821, NOT on main yet).** ABI v102 `join_filter_lookup` host service + capability; codec scan passes hash-join filters as `DeltaRowMask { ValueWise }` (DeltaJoinFilter.cs); off with FABRICATOR_JOIN_FILTER_MASKS=0. Gate verify_delta_catalog_dynamic_filter 100 -> 110 (rows the READ returned, from the log; all-true mutant dies at :354). Found: EW reads two-pass only for files reporting IReadConcurrencyHint, ours did not (every S3/OneLake codec read was 'local') -> RemoteReadConcurrency, hint 4 (8/16 slower on local RustFS; FABRICATOR_EW_READ_CONCURRENCY). TPC-H SF10 RustFS codec 'all' best warm: 41.6 s before -> 36.7 s; V2 layout (DELTA_BINARY_PACKED keys, s3://fabricator/tpch10v2) 34.8 s; 'static' 35.0. Tiers hermetic 100/100 — 13311 (floor set), service 91/91 — 137757. Baseline worktree D:/repos/fx-ewrm-base (fc4a434) left in place. Record: docs/ew-late-decoding-join-filters.md §7. NEXT when EW merges #502 to main: bump the pin, sweep, move the branch work over.
- **2026-11-10 night: MERGE ACTIONS SIDE BY SIDE ON DELTA (local, unpushed).** fct_scada gap (dbt merge 70 vs 48 s DuckDB/Iceberg) was NOT the filesystem: serialize_actions (our 2.0 rule, every 2+ writers) buffers all but the FIRST action (BUFFER_ALL, 9 GB + spill) and the first is the UPDATE, so 8.1M inserts waited. Measured OneLake in BEGIN..ROLLBACK: UPDATE+INSERT 44 s, INSERT-only merge 25 s, plain INSERT 21.5 s; parallel action threads 118 s (no). Iceberg plans append_count=1 so it never serializes. Without serializing, Delta failed 12/12 at COMMIT: NullReferenceException in DeltaPath.Encode = two unguarded List appends to pending.Files from the two action pipelines (the old CDF "lost preimage" race). Fix: DeltaTableBinding.Gate (buffered UPDATE/DELETE under it, INSERT takes it only around state changes, never while consuming its stream) + capability concurrent_merge_actions (Delta only); serialize stays for !capability, 2+ rowid actions, RETURNING. SQL Server MUST stay serial: concurrent actions on its one txn connection failed "The connection is closed" with MARS off. After: UPDATE+INSERT 23 s, 8077152 rows. Gate verify_merge_into §5 (mutant no-op gate dies at assertion 102 both runs); tiers hermetic 13301, service 137757 (= floors).
- **2026-11-10 evening: dbt AEMO workload (D:/repos/lakehouse_benchmark/workload, branch fabricator-delta, LOCAL ONLY) on OneLake LH_bench (workspace Test), duckdb Iceberg vs fabricator Delta, incremental MERGE: results IDENTICAL every run (model_rows/model_mw/model_files). Run 3 (50 files/model): 186 s vs 249 s (fct_scada 59 vs 79 s; others at parity). Found+fixed: (1) profile: two unscoped azure secrets, DuckDB picked credential_chain for CSV/parquet IO (2.2 s vs 0.08 s per CSV) -> onelake_storage scoped to the workspace in workload/profiles.yml (uncommitted, user's repo); (2) 08101b7 CHAIN on credential_chain honored (AzureCredentialChain.cs, 8 tier-0 tests, floor 463) — DefaultAzureCredential gave 403 InsufficientScopes locally; (3) d78d3c0 Resolve/AmbientChain return ONE cached credential per secret (CachingTokenCredential): onelake:// forwarder fetched a token per file open (az process) — 6 files 15.6 s -> 4.5 s cold, 13.9 -> 0.29 s warm. Also local: 1ab5a0a, c6edd32, c0d7d2a (TPC rerun doc) — NOT pushed; tiers not rerun after 08101b7/d78d3c0 (Azure-only code, hermetic untouched). Run: scratchpad workload_run3.sh (env FABRICATOR_AZURE_CHAIN=cli, .venv-local). Open: onelake:// still slower cold than duckdb-azure abfss (4.5 vs 2.2 s); fct_scada gap.
- **2026-11-10 later: THE "ALLE" LIST IS DONE (local, unpushed; 17 commits since a45eea7).** 3e4a9c8 a file's naive timestamps under a UTC-adjusted column are declared as the table's kind (read as UTC, filters too; other units refused; gate §10, mutant 5:45 off at :360). eee3fc2 part pruning: at scan start the MFR's DynamicFilterPushdown asks table_parts_matching (ABI v101, ITable.PartsMatching(FilterNode, values)) and skips the rest unopened; Delta answers with EW's DeltaFilePruner over the pinned snapshot (same rules as the native reader); C++ SerializeColumnFilters in arrow_ingest; gate §11 overwrites the ruled-out file with garbage (mutant 'no pruning' dies at :415). SF10 RustFS best warm 13.9 s (was 13.3, noise; all 22 correct). Last commit: FABRICATOR_FILE_SCAN=1 is now the setting's DEFAULT (a SET wins); nine suites set it off (partition_stats SET GLOBAL — con1/con2); docs delta-read-path 'The file scan', README row, known-limitations 1.33; hermetic 100/100 — 13207 with the switch OFF and ON (floor set). ⚠ Running batched_read by hand needs DELTA_PROVIDER=delta AND FABRICATOR_DELTA_BATCH_MIN_FILES=1 (run-suites sets both) — without them it fails at :1731 and looks like a regression. THEN ef5d4d2: late materialization in the file scan, row id (filename, file_row_number) — NOT parquet_scan's file_index, which pruning renumbers per scan; gate §12 (+54), mutant (row number alone) dies at the differential. Service tier with the switch ON = OFF per suite, 91/91 — 137757 (verify_plugin's parallel-sink timing now pins the provider scan: the file scan splits by ROW GROUP, its source is one). Floors hermetic 13261, service 137757. ⚠ The stream-part SPIKE hands id-mapped tables stream parts (its LogicalToPhysical-is-null check misses id mode) and then fails reading logical names; spike only. THEN (user: all three): fabricator_file_scan DEFAULT ON (FABRICATOR_FILE_SCAN=0 = process default off); struct pushdown extract in the file scan for structs WITHOUT a struct member (§17 needs a path THROUGH a missing struct member; missing leaf/list/map fine — measured; gate verify_delta_file_scan §13, 206, mutants: none at :568, all-structs at :576); the stream-part spike now only for tables without mapping and without a timestamp column. Tiers default on: hermetic 100/100 — 13301 (floor set), service 91/91 — 137757. Inversion pass (docs/delta-read-path.md 'Inversion pass'): no file-scan defect; found pre-existing: local Windows INSERT ~5 s on a 280-file table (ListAsync stats every entry twice, 0.7 s per listing vs DuckDB glob 0.04 s), '__HIVE_DEFAULT_PARTITION__' literal reads NULL. THEN 1ab5a0a: local log listing reads size+time from ONE directory enumeration (was a FileInfo per entry, ~1.2 ms each on Windows; INSERT on a 280-file table 2-7 s -> 0.3-0.4 s; not the Restart Manager — the INSERT path opens no missing file). c6edd32: the streaming write and the deferred commit open from the stored snapshot (OpenAsync(fs, startFrom)): steady-state INSERT 6 log listings -> 3 (OneLake warm listing 50-100 ms). Hermetic 100/100 — 13301 and service 91/91 — 137757 after both (file scan default on). S3 (local RustFS) one-row INSERT 0.24-0.49 s -> 0.11-0.16 s. Open, small: HostFsOpenWrite exclusive could set FILE_FLAGS_NULL_IF_EXISTS (a lost race hits the Restart Manager); EW plain OpenAsync lists twice (existence + build) — upstream candidate. Push on the user's ask.
- **2026-11-10: FILE SCAN COMMITS (local, unpushed): 096dc36 WIP, 2c61590 partitions, cae679e top-level timestamps, 18a0c6f mapped struct members + setting `fabricator_file_scan` + gate verify_delta_file_scan (61, hermetic, mutant at :87), bb8e173 floors (100 runs / 13146), 92dabc4 FORMAT-NEUTRAL PLAN (columns/stored names, virtual {stored, add_row_number}, per-file constants + virtual values; no Delta name in C++; protocol at table_file_plan in abi.h), 0455b8b plan read with type checks, b1703ca SPIKE stream parts (a plan entry with "stream" is read by the provider via table_part_scan through a StreamPartReader inside the same multi-file scan; mapping shared; ⚠ FinishFile is called by whichever thread finds no more work — release a part on the draining thread). Spike measured SF10 Q1 warm: native 0.41 s, 4/9 parts streamed 1.49 s, old single stream 3.53 s; mixed == provider scan 3/3. User design goal (Dirac): ONE concept = a table is a set of parts, each says how it is read (FilePart: DuckDB reads; StreamPart: provider reads, e.g. Lance/Vortex via EW) and how its rows map. NEXT agreed: typed ScanPlan/FilePart/StreamPart in Fabricator.Abstractions (testable offline), Delta on it, DVs as a bitmap instead of JSON ints, then the open gaps (UTC cast for naive timestamp files, id mapping, struct in list/map, file pruning by log stats, struct extract, old-suite markers, docs, default on).
- **2026-11-09 night: STEP 2 = THE FILE SCAN (format-neutral MultiFileReader), SLICE 1 BUILT, UNCOMMITTED, opt-in FABRICATOR_FILE_SCAN=1.** ABI v101 `table_file_plan` on the table session: JSON {version, row_count, physical{logical:phys}, files[{path, rowid_base, size, dv[]}]} or {declined}. C#: Bridge `IFileScanPlan` (FileScanPlan.cs), DeltaTableBinding implements it, DeltaCatalog.FilePlanJson (pin/read-set shared with PartitionStatistics via TryResolveMetadataRead/SeedPin; rowid_base = EW TransientRowAddress.Pack(ordinal,0), user pointed to the EW helper). C++: src/fabricator/fabricator_file_scan.cpp — copy of the system catalog's parquet_scan at BIND (no load-order issue), FabricatorFileScanReader (CreateFileList from the plan, Bind = entry columns BY_NAME with NULL defaults, name-mode via VARCHAR identifier = physical name, FinalizeBind = DeletedRowsFilter, rowid = rowid_base + file_row_number via extended_info), serialize/statistics/partition_stats off, cardinality = plan row_count; hooked first in FabricatorTableEntry::BuildScanFunction. Declines: native_read off, pending writes, column mapping 'id' / nested mapped, partition columns, timestamp/timestamp_ntz/variant; provider virtual columns (row tracking) THROW. MEASURED TPC-H SF10 RustFS best warm 13.3 s (was 37.5; DuckDB iceberg_scan 13.8), all 22 correct; Q1 0.36 s. Local differential smoke (DV DELETE, rowid UPDATE, ADD COLUMN) identical on/off. LATER SAME NIGHT: row tracking virtual columns (COALESCE(materialized, base_row_id + file_row_number) / commit_version, as RowTrackingExpr), recursive NULL defaults for struct members, plan copy via a `fabricator_file_scan(VARCHAR)` stub (FabricatorFileScanSerialize/Deserialize rebuild through the entry), partition stats shared with the scan (PartitionStatsFor), capability `file_scan` (Delta native_read) + autocommit-only: the plan crossing BINDS the table, and an explicit txn's later reads depend on the first bind (verify_delta_partition_stats:288 on the codec engine) and the plan would read the WHOLE table where the scan records its predicate (verify_delta_catalog_transactions:517 under serializable). Pushdown EXTRACT is OFF in the file scan: with it verify_delta_catalog_nested_alter:239 hit INTERNAL 'result vector INTEGER vs STRUCT(q INTEGER)' 3/3 (path into a struct member old files lack) — upstream candidate, not yet reproduced on stock DuckDB; costs struct-field join filters. Tiers: hermetic switch OFF 99/99 — 13085 (unchanged); switch ON the remaining reds are routing/log/EXPLAIN-marker assertions of the old reader (autocommit_pin, batched_read, dynamic_filter, late_materialization, partition_stats:132 marker, row_tracking_virtual log, subplan_dedup marker — its copy with the marker widened passes 77/77, update_set_columns) plus the real loss struct_extract. NEXT: then partitions (constants), row tracking (base row id + materialized col), id mode, timestamps, file pruning by log stats, own gate suite, docs, then default on.
- **2026-11-09 evening: STEP 1 (chunk passthrough) DISCARDED (user decision) after measuring — WIP f262c09 reset, branch back at a45eea7; the full diff incl. the 60-chunk pull is kept as scratchpad/chunk-passthrough/chunk_passthrough.patch (repo scratchpad, gitignored). Reason: ~6% wall, and it only covered the simple-table case that STEP 2 (MFR) takes over entirely. CONTROL measured after: Q1 as UNION ALL of 9 direct read_parquet = 0.36-0.46 s, i.e. UNION ALL is free and the host-query layer itself costs ~1.1-1.5 s and ~2x CPU even across 9 streams. User idea recorded (not built): a provider scan returning several streams (IAsyncEnumerable<stream> / IReadOnlyList<stream>), each drained by one scan thread. Record of the step-1 measurement: Hermetic 99/99 — 13085 (unchanged). SF10 RustFS best warm: passthrough 35.1 s vs off 37.5 s (all correct) — ~6%, NOT the lever. One chunk per pull made every scan thread contend on main_mutex (sys 1.6 -> 8 s on Q1); now 60 chunks per pull (env FABRICATOR_HOST_QUERY_PASSTHROUGH_CHUNKS). DECOMPOSITION of Q1 (the finding that matters): direct read_parquet 0.36 s warm, the SAME SQL through fabricator_host_query 2.0-3.4 s, nine per-file host queries UNION ALL 1.5-1.9 s; passthrough cuts user CPU (~18 -> ~11 s with nine streams) but not wall clock. max_streaming_buffer_size 10MB -> 1GB: warm 3.4 -> 2.6 s, noisy, not kept. => the cost is the serial handoff through a streaming result, and only STEP 2 (MFR, the plan reads read_parquet itself) closes it. Oddity: the FIRST Q1 in a session is consistently faster (1.8 s) than the identical second (2.6-3.8 s), unexplained.
- **(superseded) 2026-11-09 HANDOFF (context full): STEP 1 = HOST-QUERY CHUNK PASSTHROUGH, BUILT + COMPILED, NOT TESTED, UNCOMMITTED.** User agreed order: (1) passthrough, (2) option (c) as format-neutral MultiFileReader like duckdb-iceberg (iceberg_scan = parquet_scan copy + get_multi_file_reader, D:/repos/duckdb-iceberg; our removed spike = same shape, docs/multifile-delta.md), native reader stays as fallback, NO optimizer-splice. Passthrough as built: C++ fabricator_host_query.cpp HostQueryChunkStreamTypes/HostQueryFetchChunk (chunk-mode stream, release==HostQueryRelease, no promoted nulls); arrow_ingest.cpp gstate.chunk_passthrough (types must equal scan_types; env FABRICATOR_HOST_QUERY_PASSTHROUGH=0 off; FABRICATOR_DUMP_FILTERS prints SCAN passthrough=on/off; interrupt checked per pull), lstate.passthrough_chunk referenced into all_columns. C#: HostFs.Query(raw:true) -> HostFs.RawHostQueryStream (schema via C layout fn ptr, TryDetachTo, lazy import fallback), Host.QueryChunks, Bootstrap.TableScan detaches it, DeltaNativeReader.Read passthrough when batchRows>0 && batch && loopFiles==0 && MappedSchema null (env FABRICATOR_DELTA_CHUNK_PASSTHROUGH=0). NEXT: smoke + verify_delta_* suites, measure SF10 (scratchpad/rustfs-bench: bench.py duckdb_delta 3; baseline 44.4 s warm), hermetic+service tiers, then docs + commit on user's ask. Also uncommitted: docs/tpc-benchmarks.md RustFS section (user approved recording; commit not yet asked).
- **2026-11-09: LAKEHOUSE ENGINES ON RUSTFS, TPC-H SF10 — docs/tpc-benchmarks.md (last section).** Same parquet files as Delta and Iceberg; warm sums: DuckDB iceberg_scan 13.8 s, Polars Iceberg 20.6 / Delta 26.1, Sail 32-33, fabricator codec 35.8, **fabricator native 44.4 (3x DuckDB Iceberg; Q1 3.81 vs 0.55 s)**, all results correct. The gap is the inner-query Arrow transport; the lever is planning the Delta read as read_parquet directly (optimizer pass, like the SQL Server subtree pushdown) — option (c) of 2026-11-07, NOT started. Harness local: scratchpad/rustfs-bench/. Release v0.2.6-duckdb2.0.0 drafted the same day (publishing is the user's call); Linux raw artifacts for elt-airflow in build/linux-v2-0.2.6-dist.
- **2026-11-09: MinIO IS GONE FROM THE RIG — RustFS 1.0.4 is the S3 store** (docker/README.md). Same keys and bucket, port 9000, SQL Server reaches it as `rustfs:9000` (FABRICATOR_S3_SQL_ENDPOINT), bucket from the one-shot `rustfs-init` (RustFS's `rc`, --insecure because rustls refuses our CA:TRUE cert); CI waits on it (`docker compose wait rustfs-init`). Cert CN/SAN `rustfs`, CA mounted as `s3.crt`. verify_http_transport now probes `/health` and a `content-type` header (RustFS sends no Server header). Service tier green on RustFS (91/91 — 137756). ⚠ Two traps paid for: a RESTARTED Bitnami MinIO kept its old CA copy and crash-looped every ~75 s while reporting healthy (suites passed by timing); renaming the CA bind mount left an EMPTY `minio.crt` placeholder in the SQL Server volume, which crashed SQL Server at startup ("Failed to install certs" / "Failed to load LSA") until deleted. The three SQL Server S3 suites now re-point `s3_ds` (they used to keep a stale location and read the other store). Credentials `miniouser`/`miniosecret123` and the secret name `minio_s3` kept (renaming them needs the same re-point for `s3_dc`).
- **2026-11-08: DELTA SCANS GET HASH-JOIN FILTERS AGAIN (C++ only, uncommitted at time of writing).** Above
  `dynamic_or_filter_threshold` (50 keys, i.e. every TPC-H join) DuckDB 2.0 sends only a prefix-range filter (PRF); the
  port dropped it as "unreadable" and §5.5's key reading (`196dbbf`) was gated to SQL Server (`reapply_filters`), so a
  Delta probe scan got NO join filter (SF10 q8: 60M lineitem rows into the plan vs Iceberg's 24k). Now the SQL channel
  renders `fabricator_join_filter(<id>, "col")`: a registered scalar that looks the PRF up by id (process registry,
  per-thread cache — a lock per chunk made q8 slower) and calls `LookupKeys`; the scan's gstate removes the ids after
  releasing the inner stream. Key-list SQL was measured and rejected (13k keys ~0.3 s; >100k keys / span >2^24 only a
  range). Expansion now on for every provider behind `fabricator_join_key_lists`. SF10 local native 42/43 -> 34/36 s,
  MinIO 49/51 -> 42/41 (Iceberg 14/15); q3/q8 scan rows = Iceberg's. Remaining gap: pure scans (q1/q6 ~6x, transport).
  Gate `verify_delta_catalog_dynamic_filter` 21 -> 47 (profile row count of the probe scan), two mutants killed. Then the integer key RANGE beside the call (`("id" >= lo AND "id" <= hi) AND fabricator_join_filter(..)`) so parquet skips row groups: key-clustered 20M rows 0.07 vs 0.27 s; gate 48; tiers hermetic 98/98 — 12958, service 91/91 — 137756. And BLOOM filters (keys the prefix-range filter does not cover: DOUBLE, random strings) through the same function (hash as DuckDB does, VectorOperations::Hash + LookupHashes; only a filter on the column itself, never one behind a cast — FLOAT vs DOUBLE would drop rows): md5-key join 20M rows 0.51 vs 1.0 s; gate 68, two mutants. STRUCT FIELD JOINS: the scan declares 2.0 pushdown extract (supports_pushdown_extract for STRUCT columns + statistics moved to statistics_extended, which DuckDB requires), so `s.a` is a scan column and gets join filters: 20M rows 0.6 vs 1.8 s; NEW hermetic verify_delta_struct_extract (50), switch FABRICATOR_NO_PUSHDOWN_EXTRACT; the native reader still reads the whole struct. CODEC ENGINE (2026-11-09): with pushdown_filters 'all' it declares superset_filter_pushdown (the host re-applies every filter per chunk, natively on every scan thread, as SQL Server/ADBC) instead of exact + the per-batch host query (HostBatchFilter, now only a native read's buffered rows): TPC-H SF10 MinIO 62.2 -> 43.0 s (static 39.6), Q1 10.0 -> 2.4 s. Pushdown extract now also under re-apply (filter typed by the emitted field). A C# lookup (ABI v101 join_filter_lookup + `lookup` node + EW ArrowRowEvaluator) was built, measured SLOWER (serial on the one managed producer stream: 46.7 vs 43.0 s) and REMOVED, never committed; ABI stays v100. Gates dynamic_filter 99, struct_extract 75, filter_modes 56; mutants: codec declaring exact (profile rows), re-apply typed by the struct (INTERNAL error). Tiers hermetic 99/99 — 13085 (floor set), service 91/91 — 137756 (unchanged). Late materialization stays native-only: the codec path does not turn a rowid filter into a file selection (only DeltaNativeReader uses DeltaRowIdFilter). Everything about building it again on engineered-wood late decoding (#502): docs/ew-late-decoding-join-filters.md.
  Doc: remote-execution §5.8. Also on the tree, uncommitted: SF10 data (OneLake CH0010, D:/tpch10_*, MinIO tpch10).
- **2026-11-08: DELTA TYPE WIDENING BUILT on `v2.0-cyanoptera` (UNCOMMITTED at time of writing; snapshot cache pushed
  `5f7e5e4` before it).** `ALTER TABLE t ALTER COLUMN c TYPE <wider>` (also `s.f`) = an empty one-column write with
  `mergeSchema` (EW records `delta.typeChanges`, adds `typeWidening` + e.g. `timestampNtz`, refuses dependents/IcebergCompat).
  We pre-check (narrowing would otherwise COMMIT AND CHANGE NOTHING: an empty narrower batch is a valid append), refuse
  partition columns and int->double/decimal (Spark allows on explicit ALTER; EW mergeSchema does not), post-verify.
  `WITH (type_widening = true)` at CREATE; `ALTER TABLE … SET (type_widening = true)` later = property commit WITH a
  protocol action (`DeltaReader.TypeWideningProtocol`, table-features (3,7), legacy implied features listed) — EW consumes
  `delta.feature.*` only at create and refuses them in host metaData, so the host supplies the protocol; applies to
  set_tblproperties too. Native reader bug FIXED: the PER-FILE SQL (one file, or any per-file fallback) had no cast, so
  a widened column read only from old files failed "INTEGER on the provider but BIGINT in the catalog" (the batched
  forms cast via their schema map); now `CAST` for fields with typeChanges + struct members via the rebuild; list/map
  holding a struct NOT cast (physical member names) -> still the loud type-check error. HOST (C++): `ALTER COLUMN … USING`
  was silently DROPPED for every provider (only the type crosses) -> refused unless it is the binder's own
  `CAST(col AS type)` or the bare column; a nested `s.a` path was sent as column `s` -> now `path` without `column`
  (SQL Server refuses a path). Gate NEW hermetic `verify_delta_type_widening` (115 x2 engines, doubled), four mutants
  killed at their own rows (per-file cast 64, member cast 58, protocol 188, USING 250); `verify_delta_catalog_alter`
  :121 pinned the old refusal, now "type widening is not enabled". README §Type widening. Tiers: hermetic
  97/97 — 12876 (= 12646 + 230, floors set), service 91/91 — 137756 (unchanged). FABRIC MEASURED LIVE 2026-11-08 (LH/dbo, probe
  tables dropped after): SQL endpoint reads our widened tables exactly incl. old-files-only + SET-enabled (drops
  struct/timestamp_ntz as always); Fabric Spark (Livy, scratchpad/fabricnb/tw_spark.py) reads all incl. s.a +
  timestamp_ntz, APPENDS to our widened table, and a Spark-widened table (old files only) reads back here on both
  engines. Open: int->double/decimal would need a rewrite path.
- **2026-11-05: VARIANT SHREDDING ON BY DEFAULT (`e51f354`) + identity UPDATE/MERGE inside BEGIN (`0bfac0e`) + EW bump
  `3d3be76 -> 5165c13` (`74fe688`; variant bounds for EW-written files only, journal §THE 2026-11-05 BUMP), not pushed.**
  (1) A table CREATED with a VARIANT column gets delta.enableVariantShredding = true (EW declares variantShredding);
  opt out WITH (variant_shredding = false) / ATTACH variant_shredding false; CREATE flag, ALTER refuses it. EW checks
  shredding only for its own writer, so every native write path asks the opened table
  (NativeParquetDataFileWriter.AllowsShredding) before dropping SHREDDING; CreateConfig takes the schema (required).
  Fabric Spark reads shredded tables (user-confirmed); delta-spark < 4.1 refuses them. Gate verify_delta_catalog_variant
  (257), three mutants. (2) Buffered UPDATE on identity tables: post-images keep their values
  (TryEagerWriteBatches keepIdentityValues, flush flag IdentityValuesKept); MERGE in a txn works with it. Still
  refused with a message: SET of the identity column, UPDATE of same-txn inserted rows (every table), identity
  UPDATE after a buffered ALTER, IcebergCompat / constraint / generated-column tables. Gate
  verify_delta_catalog_identity (171), three mutants; external-table UPDATE inside BEGIN works with it (verify_mssql_external_txn 127). Tiers hermetic 94/94 — 12599, service 91/91 — 137706 (floors set; external_txn re-run standalone after the tier). User rule: DML must work, failures must be clearly communicated.
  Variant BOUNDS for our DuckDB-written files: DONE, in our pin since `5d57ba1` (EW 089959f, 2026-11-06; hermetic and service unchanged; macOS listing workaround `4249672`). IDataFileWriter files via #692/#695 (ours),
  staged/committed files via #711 (issue #693; maintainers declined a public encoder, EW reads each file's footer when the
  table allows shredding and our stats lack minValues.v). Tested 2026-11-06 against #711 before merge: local, MinIO,
  OneLake, every write path, decoded bounds correct, one footer read per file (~150 ms sequential on OneLake), hermetic
  94/94 — 12599 unchanged. Limit: a variant column added by a buffered ALTER in the same commit as data gets no bound.
- **2026-11-04 night: EW bump `b644f50 -> 3d3be76` (`f7e0d36`, 14 commits, zero patches; brings #672 = our #671,
  variantShredding features) + `ada44cf` UPDATE on Delta identity tables (was broken on both paths, PRE-EXISTING;
  SET of the identity column now refused like Spark). EW #653: a supplied GENERATED ALWAYS identity value (NULL included)
  is refused now. Record: docs/ew-master-migration.md §THE 2026-11-04 EVENING BUMP, known-limitations 1.32. Tiers
  hermetic 94/94 — 12433, service 91/91 — 137690. Not pushed at time of writing. NEXT: per-table variant shredding opt-in.
- **2026-11-04 night: DuckLake PROTOTYPE for duckdb/ducklake#1900 (stable `col-<field id>` file column names), local
  only: `D:/repos/ducklake` branch `feat/field-id-column-names`, commits `32a7d0d2` + `831f8263` (lake-wide test; no AI trailer, upstream-bound). Option
  `parquet_column_names 'current'|'field_id'`; hive dirs `col-N=...`; test `parquet_column_names_field_id` (126); full
  DuckLake suite green (993 cases). Build: scratchpad `dlfid/build.bat` (own ext config, `D:/repos/ducklake/build/release`).
  Pushed to the fork https://github.com/cmettler/ducklake (remote `fork`). PR text draft: repo `scratchpad/upstream/ducklake_field_id_column_names_pr.md`,
  posted by the user after a maintainer signal in #1900. Our side (sync physicalName = col-N, guard allows renames) only
  once upstream.
- **2026-11-04 night: RELEASES TAGGED — `v0.2.4-duckdb2.0.0` (2.0, `14b4164`) and `v0.0.18-duckdb1.5.6` (1.5, `9cf225d`,
  the naive-timestamp backport); `distribution` builds DRAFT releases, publishing is the user's call.** Also committed on 2.0:
  `589b4d8` the DuckLake mirror guard refuses a SET/RESET PARTITIONED BY that drops a declared partition column (marker
  column `partition_column_ids`; gate verify_ducklake_mirror_guard_mssql §4b, 60), `b38991a` naive timestamps inside
  lists/maps on the per-file read (was a misleading "changed outside this session" error; gate verify_ducklake_delta_sync
  §15, 625), `1c13ace` docs/duckdb-upstream-issues.md §16 (SHREDDING misses mixed-case names; draft for the user in the
  repo `scratchpad/upstream/duckdb_shredding_mixed_case_issue.md`). Tiers: hermetic 94/94 — 12405, service 91/91 — 137690 (floors set).
- **2026-11-04 evening: `HostSession` is MANDATORY on the managed `Host` API (committed `eb409bf`).**
  `Host.Query` / `ExecuteNonQuery` / `OpenConnection` take `HostSession.Neutral | Inherit`, no default; every
  in-tree caller says Neutral (behaviour-identical, tiers at the old floors). `IHostQuery.ExecuteNonQuery` gained
  `inheritSession = true` (it always ran neutral while `Query` inherited); gate `verify_plugin` via the sample
  plugin's `plug_host_exec` (136 -> 145), mutant killed at line 769. MEASURED: the exact (erased) filter applied
  in a neutral session does not change answers — DuckDB pushes only offset-carrying constants and optional ranges,
  never a zone- or collation-dependent expression. Also pushed: `.gitattributes` `*.sql`/`*.test eol=lf` (both
  lines), 1.5 backport of the naive-timestamp fix `d507ec5` (1.5 integration run dispatched: 41872123633).
- **2026-11-04 later: host-query sessions reworked (`3381a0b`) + VARIANT written unshredded (`75e8765`), not pushed.**
  INHERIT copies every setting the caller SET (LocalUserSettings scope, not duckdb_settings().scope), the search path and
  variables; NEUTRAL = defaults + TimeZone UTC (skipped where already UTC). Fluid RESETs `params` without a bag. VARIANT:
  DuckDB COPY shreds by default but Delta forbids it without `variantShredding`, which EW refuses even to READ —
  issue clast-project/engineered-wood#671 (user: upstream fixes it). Our COPYs pass SHREDDING {col: 'VARIANT'}; DuckDB
  defect: mixed-case names are silently not matched. NEXT after #671: opt-in shredding per table, then Spark-style
  footer-derived variant path stats (delta-spark stats/VariantStats.scala). Hermetic 94/94 — 12380, service 91/91 — 137659.
- **2026-11-04: `ducklake_delta_sync(…, rebuild := true)` BUILT, commit `d4f0076` (not pushed, nor `91829f4`).** Deletes
  `_delta_log/`, `_change_data/`, our `deletion_vector_*.bin` and the marker row, then a first sync with the given
  options (switch partitioning/mapping; follow a partitioning change). Needs table_name; refuses a foreign log. MEASURED:
  DuckLake compaction does NOT move files into a new partitioning, so after a change the rebuild declares no key (warns).
  Gate verify_ducklake_delta_sync §16 (600), four mutants; hermetic 94/94 — 12344, service 91/91 — 137659. Open: the
  SQL Server catalog guard does not check partitioning changes (the sync refuses them).
- **2026-11-04: NAIVE TIMESTAMPS IN A DELTA `timestamp` COLUMN WERE MISREAD OFF UTC — FIXED (docs/ducklake-catalog.md
  §11.12, uncommitted at time of writing).** DuckLake 'utc' mirrors, Spark INT96, legacy converted types: the batched
  `schema` map cast them in the reader connection's MACHINE zone (silent shift), the per-file path refused them.
  Batched statement now `SET SESSION TimeZone='UTC'`; per-file converts the probe's naive leaves (and offset-free
  timestamp partition values) with `timezone('UTC', CAST(x AS TIMESTAMP))` — zone-free, because the clustered
  OPTIMIZE runs the same SQL in a COPY. Gate verify_ducklake_delta_sync §15 (556) under SET GLOBAL TimeZone, four
  mutants. ⚠⚠ AND A 2.0 PORT REGRESSION: `MakeHostQueryStream` sent several statements to `conn->Query`, which
  MATERIALISES the last — so every batched Delta read (`SET …; SELECT …`) buffered its result. Now leading
  statements via `Query`, the last via `Submit` (1.5's SendQuery). docs/host-query.md.
- **2026-11-04: EW bump `957d24d → b644f50` (25 commits, zero patches) — record docs/ew-master-migration.md §THE
  2026-11-04 BUMP.** Constrained tables: DELETE works, copy-on-write UPDATE enforced, merge-on-read UPDATE still refused
  (gate verify_delta_tblproperties §8, 158). EW now writes RUN-container deletion vectors — MEASURED LIVE: Fabric's SQL
  endpoint reads them exactly (inline + file DV, control without runs). EW reads a file's row groups in PARALLEL on
  one handle — our IRandomAccessFile implementations are positional and tolerate it. delta.min*Version / delta.feature.*
  are a protocol floor now.
- **2026-11-03: PARALLEL DDL TRANSACTIONS HUNG FOREVER on SQL Server (dbt table swap at --threads > 1, user repro
  elt-airflow/docs/fabricator-ddl-deadlock/repro.py) — FIXED, see docs/transactions.md §8.** Three cycles, each
  half a SQL Server wait and half a CLIENT wait (so SQL Server saw only a sleeping session): (1) the database-wide
  comment/DEFAULT read-back (2026-09-09) reloaded after every DDL inside a txn, under a C# lock — now per table
  WITH (NOLOCK) (dc.definition blocks even under READ UNCOMMITTED, measured); (2) GetOrCreateEntry held
  entry_lock_ across provider IO — released now; (3) table_types_ was shared by all txns, so enumeration described
  (or got 208 and ERASED) another txn's uncommitted table — now FabricatorSchemaEntry::pending_ (owner + state
  before; commit keeps, rollback restores, other txns' enumeration skips). Gate NEW verify_ddl_txn_visibility_mssql
  (172), three mutants killed (M3 = old read hangs). Tiers: hermetic 93/93 — 12221, service 91/91 — 137659.
  Open: 1.5 line has (2)+(3) too, and (1) since 2026-09 — backport is the user's call.

### From CLAUDE.md §Next up — records 2026-11-02 … 2026-10-21 (incl. the handoffs of 2026-10-24 … 2026-10-31)

- **2026-11-02 evening: DuckLake mirror guard + two silent bugs fixed** (docs/ducklake-catalog.md §11.11): the SQL Server
  catalog checks DuckLake's column/data-file inserts against marked mirrors (fabricator_delta_mirror) right before COMMIT
  (DuckLake writes the data file before its snapshot row). Silent bug 1: sync inferred physical names from metadata, but
  ducklake_rewrite_data_files keeps the old name with a later begin snapshot -> mirror NULL; now read from parquet_schema.
  Silent bug 2: INSERT into SQL Server fired no triggers (SqlBulkCopy without FireTriggers) — fixed; the pushed INSERT no
  longer declines trigger tables (OUTPUT INTO still does). DuckLake/Iceberg resolve by field id, so 'name' mapping cannot
  follow a rename with data (guard refuses it). Tiers: hermetic 93/93 — 11997, service 90/90 — 137487.
- **2026-11-02 later: `ducklake_delta_maintain` BUILT** (flush → merge → rewrite → sync → expire → cleanup → orphans;
  docs/ducklake-catalog.md §11.10, gate verify_ducklake_delta_maintain 57, hermetic 93/93 — 11980). Found on the way: a
  sync behind an expire scanned AT an expired snapshot (fixed: only snapshots still in ducklake_snapshot). FUSE: link()
  is skipped on fuse* mounts (mountinfo) — the attempt put 16 Fabric writers in lockstep; CommitBackoff + 64 attempts.
  EW pin 957d24d. All pushed except this maintain commit at time of writing; 1.5 has the FUSE fixes (a4d9437).
- **2026-11-02: POSIX commit window LOST DELETES (CI red linux/osx 5 of 9 runs since 5d5d0d4, never Windows) — FIXED,
  uncommitted at time of writing.** A loser re-read the winner's commit while it was still EMPTY (O_EXCL create, then
  write) and saw no conflict. Fix: local POSIX publish via temp + link() (FUSE without link -> O_EXCL, remembered),
  and ReadAllBytesAsync never returns an empty commit JSON (re-reads ~2 s, then fails). Repro only under CPU pressure:
  WSL `taskset -c 0,1` (scratchpad/wsl_cdml2.sh, dvlog.py). Record: delta-transactions.md §8.5b. Also today: math
  pushdown (§4.30, baedf5a), duckdb-odbc 2.0.0.1 carries our fixes (da3ad80). ⚠ CHECK CI red runs, not only the latest.
- **⚠⚠⚠ HANDOFF 2026-10-31 evening (context full). READ FIRST — supersedes every handoff below.**
  Branch `v2.0-cyanoptera`, PUSHED to `f722ea9`; LOCAL ONLY: `b473dce` (Delta DELETE/UPDATE info log carries
  `scanned=vN`). Today's commits: `7d0699c` bare count, `e1cd8c4` ||/trim/left/right/date cast, `76386ae` bare names via
  USE (+ refresh retires dropped schemas), `71dfc4f` correlated scalar subqueries (single join → OUTER APPLY), `f722ea9`
  concurrent Delta DML row ids at the scanned version + atomic local-Windows commit publish. Records: remote-execution
  §4.20–§4.23, delta-transactions §8.5b, upstream-issues §14. Tiers at f722ea9: hermetic 88/88 — 11135, service
  84/84 — 6714 (floors set). CI `extension` run 41205759349 for f722ea9: linux + osx GREEN, windows was STILL RUNNING —
  check it first (`gh run view 41205759349 -R cmettler/fabricator-extension`); then push b473dce.
  **PROGRESS 2026-10-31 later — items 1 and 2 DONE, all pushed (2.0 tip `8aee794`, 1.5 tip `e0f80cc`):**
  (1) 1.5 BACKPORT `e5dd942` + docs `e0f80cc`, CI extension + integration green, NO tag (user's call). 1.5.6 with
  controls: no fix 350/900 INSERT rows; publish only 900/900 but UPDATE race v 1500/2000 all exit 0; both 900/900 +
  2000/2000. Known-limitation 1.1 fixed on 1.5 too. Windows CI red on f722ea9 (verify_ducklake_delta_sync:419) = TWO
  DuckLake-sync races, fixed `8feedb6` (retry budget counted every lost race; stale state diffed against an older
  snapshot), gate §8b; CI then green on all 3 platforms.
  (2) INVERSION PASS, measured: (a) single join on an EXPRESSION key = N x M on the server (45 s vs 0.27 s) -> declined
  unless the key is a plain column and every join below seeks (`aeaaf53`; rowstore / CCI / CCI+NC measured, the rule
  is storage-independent — the user asked); (b) EnsureUnchangedSince refused concurrent DV deletes of one file ->
  DV DELETE is a transaction on the pinned snapshot under write_serializable without CDF (`5d5d0d4`; MoR UPDATE left
  strict: no overlap could be produced, and its txn is based on the OPENED snapshot — move it to the pin if the check
  is ever dropped); (c) code-page varchar: no defect; (d) refresh retirement: no defect, but FOUND pre-existing: a table
  in a schema `schema_filter` EXCLUDES is not reachable by name (the 2026-07-15 lift is table-level only) — OPEN;
  crash-orphaned `.tmp` commit files: harmless litter. Tiers at 5d5d0d4: hermetic 88/88 — 11475, service 84/84 —
  6729 (floors set). Lesson: duckdb.exe agent mode prints `FABRICATOR_QUERY` — grep EXPLAIN with -no-agent.
  (3) TPC harness scratchpad `tpc/harness.py` (bench, sf, placements local/remote/facts-remote/dims-remote/random,
  ordered vs multiset compare, interrupt timeout; positive control `tpc/control.py` deletes a nation row): TPC-H SF0.01
  110/110 ok, TPC-DS SF0.01 495/495 ok. TPC-H SF1 (loaded in TestDB schema `tpch`, local file tpc/tpch_sf1.duckdb)
  STOPPED at q20 to fix the bugs below — remote q16 44 s and q20 >300 s TIMEOUT (local 0.03-0.06 s): TO INVESTIGATE.
  (4) substrait-java mined (subagent report: categories A-Q) -> `tpc/edges.py`, 139 edge cases rp vs db. FOUND +
  FIXED, UNCOMMITTED: (i) SILENT WRONG ANSWER since b8abd9d — EqualizeJoinKeys rewrote NDF->= on MARK joins: NOT
  EXISTS dropped rows with NULL keys on ANY fabricator table (Delta too); MARK skipped now (C++), gate NEW hermetic
  verify_null_key_subqueries (33). (ii) SQL Server returns NO row for ANY GROUP BY on empty input (even GROUP BY ())
  -> ROLLUP/CUBE/sets with () beside others lost the grand total; now declined (TSqlEmitter), remote_select §21 (742),
  guide S17 (244), remote-execution §4.24. Tier run started; then floors (hermetic 89/11508?, service 6774?), commit,
  push, then resume TPC-H SF1 + q16/q20. Open: schema_filter by-name gap; ROLLUP NOT EXISTS-union pushdown (perf).
  LATER SAME DAY: pushed 5af297b (NOT EXISTS / MARK), 6326b54 (ROLLUP local), 226ce05 (IN / NOT IN filter as plain
  semi/anti join: q20 >300 s -> 7.6 s, q16 43 s -> 3.8 s; native T-SQL q20 0.28 s, rest = string prefix range).
  UNCOMMITTED, tiers running: scripts/gen_tpc_suites.py -> NEW service suites verify_tpch_mssql (22 q x 4 placements)
  + verify_tpcds_mssql (99 x 4), expected = local answers, rowsort; scripts/pushdown_edge_cases.py (--emit) -> NEW
  verify_pushdown_edges_mssql (139 cases; a BOOLEAN is written 0/1, the runner sorts rowsort by that);
  scripts/tpc_bench.py (targets box | warehouse | lakehouse-delta | lakehouse-sql; local data build/tpc/, remote
  schema <bench>_sf<sf>); extension_config.cmake links tpch + tpcds statically (CI needs no download).
  **PLAN (user-agreed): (a) finish TPC: floors, commit; Fabric runs at SF 0.01 — warehouse, lakehouse-delta,
  lakehouse-sql (wait for the endpoint sync; name mapping, the endpoint skips id-mapped tables); (b) THEN Delta
  RETURNING via the read-back (C# only: ExecuteDeleteReturning / ExecuteUpdateReturning / InsertReturning in
  DeltaCatalog from the pinned read-back and the MoR/CoW post-image batches, capability dml_returning; MERGE comes
  with it), own suite. NOT via CDF (no version inside BEGIN…COMMIT, CDF a precondition). EW #569/#562/#563 do not
  help RETURNING (it forces reading the rows); #562 helps the predicate DELETE on tables without DVs.**
  **DONE 2026-10-31 evening:** TPC block COMMITTED + PUSHED `18348d9` (service 87 runs / 137113 — TPC counts every
  value; floors set; list-*-suites.sh LINKED gained tpch tpcds, without it the suites were silently excluded). Fabric
  SF0.01 all correct: warehouse TPC-H 110/110 + TPC-DS 495/495, lakehouse SQL endpoint 110/110 (sync 153 s),
  lakehouse Delta engine 110/110 (3.7x slower than the endpoint). Open: TPC-DS q64 / q72 on the warehouse (47 / 30 s),
  TPC-H SF1 all placements. Results: docs/tpc-benchmarks.md. DELTA RETURNING BUILT, UNCOMMITTED, tiers running:
  DeltaCatalog.Returning.cs (partial class; MergeSetValues factored out of the buffered UPDATE), capability
  dml_returning, gate NEW hermetic verify_delta_returning (164 x2 engines, incl. RETURNING in a CTE and the moved CTE; three mutants killed), verify_merge_into §9 now asserts MERGE … RETURNING, README + delta-catalog.md.
  **COMMITTED + PUSHED `1a70144`; hermetic 91/91 — 11840 (floors set).**
  **WAREHOUSE RETURNING EMULATION MEASURED 2026-10-31 (nothing built, doc warehouse-support.md §6.8a, uncommitted):** rows must come from the WRITE connection (DML scans run pooled), SELECT-in-batch costs x1.2–2.5, Fabric TRUNCATES datetime2/time precision; side finding: rowid UPDATE is one statement PER ROW on the warehouse (10 rows 5.3 s).
  **PUSHED `164c9a1` (fabricator.sql.type/length/precision/scale on SQL Server Arrow fields, SqlTypeMetadata, user-chosen key namespace) + `bb162df` (warehouse rowid UPDATE as UPDATE … FROM t JOIN (VALUES …), last row per key wins: 1000 rows 5.8 s, was ~0.5 s/row; matches DuckDB incl. double match).** NEXT (user-agreed): warehouse RETURNING emulation — DELETE/UPDATE: SELECT in the same batch on the WRITE connection; INSERT: computed client-side from the metadata (Fabric TRUNCATES datetime2/time precision, box ROUNDS; char pads); no staging table (user). Live-only gate.
  **DONE + PUSHED: `aa88d5c` (every bulk INSERT into uniqueidentifier failed, box + warehouse — UUID crosses as text; ArrowDataReader(stream, guidColumns); verify_granular_types 26->34; service floor 137121) + `c9625cd` (RETURNING on Fabric Warehouse: DELETE SELECT-then-DELETE, UPDATE UPDATE-then-SELECT in one batch on the write connection, INSERT computed via SqlServerComputedInsert.cs; key-change and IDENTITY refused; doc warehouse-support.md §6.8b; live gate scratchpad wret/m6.py, 3 mutants killed; tiers hermetic 91/91 — 11840, service 87/87 — 137121).** Open: a repo-side manual Fabric suite, Synapse dedicated unmeasured, COPY INTO slow below ~1M rows (100k 18 s vs 2-3 s bulk). NEXT open: TPC-DS q64/q72 on the warehouse, TPC-H SF1 all placements, schema_filter by-name gap.
  **2026-11-01: COMMITTED + PUSHED `a40c959` (tiers hermetic 91/91 — 11840, service 87/87 — 137162): a pushed SUBTREE sends outputs nobody above reads as NULL (fabricator_remote_select.cpp ReplaceSubtree + CollectReadBindings), doc remote-execution §4.26, gate verify_remote_select_mssql §23 (771 -> 812). Found by TPC-H SF1 q10. MEASURED: SQL Server pays for the DECLARED width — nvarchar(max) via LOB however short, and in a PARALLEL plan even nvarchar(4000) (exchange buffers sized by declared width). scripts/tpc_bench.py loads with mssql_default_varchar_length = 200 (longest TPC value 198); SF1 box sums max/4000/200: facts-remote 612/375/127 s, q10 >300/183/4.6 s (docs/tpc-benchmarks.md). USER DECISION: narrowing wide/max columns in scans of foreign tables is NOT built — "damit leben", create tables with fitting varchar sizes (WITH (varchar_length=…) per table exists; a WITH option listing max columns was rejected as murks). NEXT: width-independent outliers — q9 ~29 s, q21 ~32 s, q3/q4 ~12 s, q17 ~9 s, q8 7–8 s (random2 q8 0.24 s once, 6.9 s since — unexplained).** **THEN, UNCOMMITTED, tiers GREEN (hermetic 91/91 — 11840, service 87/87 — 137202, floor set): (a) tpc_bench loads as CLUSTERED COLUMNSTORE (user: a heap without keys is no baseline) — SF1 sums remote 21.8 / facts-remote 65.2 / random1 62.4 / random2 54.3 s; heap measured: a parallel plan returning 6M rows 28 s at DOP 20 vs 7.8 s at MAXDOP 1-4 (selective scans the other way); (b) §4.27 ReplaceOrDescend: a part the translation declines tries its children (q8 all remote 7.5 -> 0.24 s), gate subtree P20/P21; (c) §4.28 to_substrait sends on_division_by_zero (IEEE/ERROR/NULL from ieee_floating_point_ops + error_on_division_by_zero), emitter declines only IEEE, NULL -> NULLIF; gates remote_select 828, guide S18. (a)-(c) COMMITTED + PUSHED `6408792`. THEN COMMITTED + PUSHED `6f0cb26`, tiers GREEN (hermetic 91/91 — 11840, service 88/88 — 137280, floors set): §4.29 SET fabricator_pushdown_boundary = 'none'|'cte' (HostSettings, Varchar, empty = catalog decides) + ATTACH option pushdown_boundary (SqlServerBackend -> capability pushdown_boundary): a USER CTE (not __duckdb_*) reading one catalog is replaced alone in AnalyzeSubtree, the main query stays local and streams (CTEInlining runs after the pre pass); a CTE read twice is forced MATERIALIZE ALWAYS; BindToSubstrait declines a SELECT with a user CTE under the boundary. Gate NEW verify_pushdown_boundary_mssql (68), guide S19; three mutants killed (script scratchpad q9/mut_bnd.ps1). THEN `8392a7f` (pushed): §5.3 more than 50 integer IN keys go as ONE OPENJSON parameter (FilterWhereBuilder, gated ServerProfile.CompatibilityLevel >= 130 / warehouse), non-JSON lists > 2000 refused client-side; DuckDB's dynamic_or_filter_threshold (default 50, counts build ROWS) left at its default BY DECISION (local TPC-H +8% at 20000, x2.3 at 100000) — README tells the user to raise it; with it q17 9.5 -> 0.12 s, q8 8.4 -> 1.35, q9 10.2 -> 2.6 (100000). Tiers hermetic 91/91 — 11840, service 88/88 — 137311. THEN `5eb23de` (pushed; service 89/89 — 137347): §5.4 rowid DELETE/UPDATE as OPENJSON blocks of 10 000 (OpenJsonRows.cs; types + COLLATE per statement from sys.columns, NOT cached — a stale type silently matches nothing; RETURNING / binary-like types / no OPENJSON keep the old paths), CCI UPDATE 344 -> 2.2 s, two-part-key DELETE 49.7 -> 1.3 s; OPENJSON MEASURED live on Fabric Warehouse + Lakehouse endpoint (compat 160); gate NEW verify_rowid_json_mssql (36). THEN `2da8737` (pushed; service 89/89 — 137378): RETURNING via the same JSON join (OUTPUT / OUTPUT INTO for trigger tables / warehouse read-back; a double match returns once, as DuckDB) + FIXED a silent bug of 5eb23de: the in-block duplicate test keyed a DateTime to the SECOND (Convert.ToString), now the shared full-precision RowIdKey; suite 36 -> 67. THEN `196dbbf` (pushed; service 89/89 — 137393): §5.5 a hash join's prefix-range filter read back as keys (arrow_ingest ReadPrefixRangeKeys, SQL Server only via reapply_filters, cap 100k keys / span 2^24, gapless -> range), box: every long list in a TABLE VARIABLE + OPTION (RECOMPILE) (two OPENJSON semi joins measured > 60 s on box), warehouse: shortest list as OPENJSON + others as range (Fabric SF1 measured: 2 OPENJSON 0.76 s, rule 0.30 s, #temp 4.6-5.9 s; Fabric has no table variables / RECOMPILE), same-column dedupe, switch SET fabricator_join_key_lists; q8 7.3 -> 1.55, q17 8.9 -> 0.12, q21 11.1 -> 6.4, q9 unchanged (coarse bitmap). Fabric warehouse now holds tpch_sf1.part/orders/lineitem (loaded for that measurement). OPEN: reading DuckDB's exact prefix-range bitmap as a key list (internals). Remaining outliers where lineitem is remote and other tables local (DuckDB joins it with a local table first).**
  **2026-11-01 later, COMMITTED + PUSHED `2a72a75` (hermetic 91/91 — 11840, service 89/89 — 137394): (a) §5.6 Fabric row counts as COUNT_BIG(*) (metadata, ~0.09 s; TPC-DS q64/q72 on the warehouse were DuckDB join orders built without cardinality — fixed); Fabric told by DATABASEPROPERTYEX Edition name (DataWarehouse / LakeWarehouse) as well as engine edition 11, never edition 6 (Synapse dedicated left out, user); `database_edition` row in fabricator_server_info (verify_server_profile 21 rows). (b) §5.7 `SET mssql_result_set_caching = false` / ATTACH `result_set_caching` appends OPTION (USE HINT ('DISABLE_RESULT_SET_CACHE')) to Fabric scans (cache measured 3.0 -> 0.09 s on repeats; session SET refused; hint accepted on warehouse AND lakehouse endpoint); tpc_bench sets it for its Fabric targets. THEN q9/q21 investigated, `1569ee8` (docs only): both are DuckDB limits, docs/duckdb-upstream-issues.md §15 — q21: NOT EXISTS stays IS NOT DISTINCT FROM on the ANTI join (SimplifyNullSafeSemiJoinConditions is SEMI-only) AND RIGHT_ANTI is excluded from join filters; a local 2-line patch (scratchpad/upstream/anti_join_filter.patch, untracked) gave q21 5.9 -> 3.4 s, all results identical, one DuckDB plan-shape test (deliminator.test:414) changes; a PR is the USER's. q9: bloom/PRF only for joins with exactly one equality condition (lineitem joins on partkey AND suppkey) — described, not built. User decided: no NOT NULL / segment statistics for now.**
  **2026-11-01 night, COMMITTED + PUSHED `7638800` + `1dcc040` (tiers hermetic 92/92 — 11923, service 89/89 — 137418, floors set):**
  (1) ABI v100 `catalog_schema_exists`: a schema `schema_filter` left out resolves by name (capability `schema_by_name`, Bridge
  `ISchemaByName`, system schemas still refused; not enumerated; negative memo cleared by RefreshCache); verify_catalog_filter 16->40.
  (2) engineered-wood b4cf12c -> 805c91b (30 commits, zero patches). #501: a sequential file publishes only at CompleteAsync —
  DuckDbSequentialFile closes there and REMOVES on an abandoned dispose; Windows local uses EW's LocalSequentialFile (temp+move);
  ADLS OneLakeSequentialFile flushes with Close only at completion, deletes on abandon. #530: EW now fills in change rows at commit
  for staged ops not declared covered — we stage our own, so `changeDataStagedByCaller: true` at the buffered flush and the MoR
  UPDATE commit (mutant: duplicates, killed at verify_delta_catalog_transactions:2479). DeleteTrustsStatistics -> TrustStatisticsProofs.
  NEW `cat.delta.restore('s.t', version := | timestamp := [, ignore_missing_files :=])` (EW #567), SchemaMayChange, verify_delta_restore 73.
  Not adopted yet (options): EW key MERGE #563, replaceWhere predicates #566, ADD/DROP CONSTRAINT #565, commit metadata #564/#577,
  #562 predicate DELETE without DVs for the delete shortcut (needs a perf measurement first).
  (3) the native scan's schema probe read the path-smallest ACTIVE file (another partition's footer, failed when unreadable) — now the
  first PLANNED file; verify_delta_delete_where §4b. (4) nested schemas on Azure were BROKEN: duckdb-azure refuses `**` mid-pattern and
  answers abfss://host/fs/path — list `<root>/**` + AzureHostFirst; verify_delta_catalog_adls §9 (live, 81; also: DuckDB 2.0 no longer
  makes an abfss ATTACH without READ_ONLY read-only, so it warns; the suite INSTALLs azure, 2.0 unittest has a per-test home).
  (5) a non-SQL-Server ATTACH failure reads "Fabricator: ATTACH of a '<provider>' catalog failed". (6) verify_returning_fabric (manual,
  122, FABRICATOR_FABRIC_WAREHOUSE_DSN). (7) Top-1 per key as OUTER APPLY: built, measured, NOT kept (2.4 s vs 0.14 s without a covering
  index), remote-execution §4.23. (8) POSIX commits measured (WSL ext4, scratchpad/posix_race.sh, 16 writers, races LOST 2-8 and retried,
  exact) and the Fabric fuse mount on DuckDB 2.0 (320/320, 82 OCC retries) — needs the PySpark kernel: DuckDB 2.0 has no Python 3.10
  wheel, Fabric's Python notebook is 3.10 (known-limitations §3). Linux build: WSL ~/sqlext2 (needed vcpkg roaring), payload build/linux-v2.
  Fabric LH still holds dbo.fuse_race; Fabric warehouse still holds tpch_sf1.*.**
  **ORDER (user-agreed):**
  1. **Backport f722ea9 (+ b473dce) to `v1.5-variegata`** — the row-id bug is a SILENT wrong-row bug on every
     multi-writer root (OneLake/S3 too) in the SHIPPED line. 1.5 has the same four paths (verified via
     `git show origin/v1.5-variegata:dotnet/Fabricator.Delta/DeltaReader.cs`: SelectionFromRowIds(table,
     table.CurrentSnapshot…), ReKeyUpdatesOntoMetadata(table, updates), MoR snapshot = CurrentSnapshot); EW pin there
     e515a2b. Procedure: checkout v1.5-variegata + `git submodule update --init duckdb engineered-wood
     extension-ci-tools`; cherry-pick; build only the managed side into build/release (1.5 tree, shared with the plugin
     repos — C++ unchanged); run the delta hermetic suites + scratchpad lwin/race.sh against build/release/duckdb.exe
     (sed the paths: it uses build/release-v2). ⚠ MEASURE whether the atomic Windows publish ALSO fixes known-limitation
     1.1 on 1.5 (DuckDB 1.5.6 still ignores EXCLUSIVE_CREATE on Windows; File.Move no-replace does not depend on it) —
     claim it only if measured. Then 1.5 tiers, commit, push, CI; a release tag is the USER's call. Switch back to
     v2.0-cyanoptera + submodule update afterwards.
  2. **Inversion pass over today's features, MEASURED not just reasoned** (user asked; none was done today):
     (a) single join WITHOUT an index on the inner key (N×M per outer row vs DuckDB's one hash) — only measured with an
     index; (b) EnsureUnchangedSince is stricter than rowLevelRetry — concurrent DV DELETEs on the SAME file may now
     fail "Retry" where they re-unioned (availability); (c) trim/||/left on varchar with a CODE-PAGE collation
     (SQL_Latin1_General_CP1) — only nvarchar + UTF-8 varchar were in the differential; (d) RefreshCache schema
     retirement vs functions-only schemas / schema_filter; also: a crashed Windows publish leaves `.<v>.json.<guid>.tmp`
     in _delta_log that VACUUM never removes. Measured already: the `main` alias is harmless for schema DDL (CREATE
     SCHEMA IF NOT EXISTS main creates it, DROP SCHEMA main leaves dbo) — scratchpad inv/mainalias.sql.
  3. **NEW test idea (user): TPC-H and TPC-DS** (https://duckdb.org/docs/2.0/core_extensions/tpch, …/tpcds) with the
     tables distributed between LOCAL DuckDB and SQL Server in different combinations — every query's result must equal
     the all-local answer; performance is the interesting second output. Needs: dbgen/dsdgen in DuckDB, a loader into
     SQL Server (CTAS through the catalog), a driver per combination (all-remote, facts remote/dims local, the reverse,
     random splits), a diff per query (EXCEPT ALL both ways, ordered queries compared ordered), timings vs all-local.
  4. **NEW (user): substrait-java is cloned at D:/repos/substrait-java** — mine its tests (Spark/isthmus TPC-H/TPC-DS
     and expression tests) for cases our producer/consumer pair has not covered.
  Open from earlier, unchanged: GREATEST/LEAST need a SQL Server 2022 gate; top-1 per key as OUTER APPLY (SELECT TOP 1
  … ORDER BY) instead of the declined window form; DELETE/UPDATE … RETURNING as one statement; POSIX create-then-write
  commit window unmeasured (verify_delta_concurrent_dml green on linux CI once); upstream drafts (§14, IsPassthroughProjection).
  Scratch harnesses: scratchpad inv/ (expression inventory, scalar/single-join probes, sqperf.sql), lwin/ (race.sh,
  race_dbg.sh with per-process FABRICATOR_LOG_FILE, race_nockpt.sh), subtree/ (tiers.sh, cases.py+gensuite.py).
  ⚠ Lessons today: duckdb.exe in agent mode prints errors as JSON without "Error" — grep exit codes, use -no-agent; in
  a bash double-quoted string a backslash pair before `$t` escapes the `$` (use `/` in paths); a guessed expected value in a gate is wrong more often than not —
  take pinned values from the local path; Snapshot.ActiveFiles is keyed path+DV (look files up by AddFile.Path).

- **⚠⚠⚠ HANDOFF 2026-10-29 late (context full). READ FIRST.** Commits today, not pushed: `07a4bfc` INSERT…SELECT +
  CTAS pushdown, `bafd129` RETURNING (ABI v98). **STEP 2 (DELETE/UPDATE via Substrait) BUILT, UNCOMMITTED, built +
  published in release-v2:** `SubstraitDml` in src/catalog/fabricator_remote_select.cpp rewrites DELETE as `SELECT 1
  FROM target [CROSS JOIN using…] WHERE cond`, UPDATE as `SELECT CAST(value AS column type)… FROM target WHERE cond`,
  binds via `BindToSubstrait`, wraps with `WrapInWrite` (WRITE_OP_DELETE/UPDATE), `TranslateWrite`, runs via
  `fabricator_remote_dml`. SupportsPushdown(DELETE/UPDATE) = no RETURNING, no UPDATE…FROM. C# `TSqlEmitter.EmitDeleteOrUpdate`
  (target = the one read at the TOP of FROM, `TopLevelOccurrences`; UPDATE refuses joins) → `DELETE [t0] FROM …` /
  `UPDATE [t0] SET … FROM …`; `WriteDeclineReason(op,…)` declines only external tables for DELETE/UPDATE. CTAS refactored
  onto WrapInWrite/TranslateWrite. NEW gate `test/verify_remote_dml_mssql.test` (87; three-way on/off/DuckDB `loc`;
  expected counts come from running the same sequence in plain duckdb — scratchpad rdloc.sql), mutants killed: update
  without WHERE (200), any read as target (60), no cast (300), DELETE never pushed (49). Changed:
  `verify_dml_pushdown_mssql` attaches `remote_pushdown 'false'` (it gates the shortcut); `verify_pushdown_guide_mssql`
  D1–D4/U1–U3 rewritten (74); docs/sql-server-pushdown.md DELETE/UPDATE section + glance table; README DELETE section.
  ⚠ `||` is not in the Substrait producer (a SET with || stays local). ⚠ mut_any.sh restores the source but does NOT
  rebuild — rebuild before trusting the next run.
  **TIERS DONE (2026-10-29):** service 80 runs, all green after one fix — `verify_remote_select_mssql` §6 pinned the
  old route (now asserts `FABRICATOR_REMOTE_DML` on `rp`, the shortcut on `db`; 510 -> 512); per-suite diff against the
  5684 run shows nothing else moved; floors MIN_SUITES 80 / MIN_ASSERTIONS 5783. Hermetic 87/87 — 11030 (identical).
  Doc §4.6 written. COMMITTED `fbb0468`.
  **STEP 3 BUILT 2026-10-29, COMMITTED `7e1e77f`: the moved CTE as ONE statement** (doc §4.7, gate NEW
  `verify_moved_rows_mssql` 126, five mutants). User-raised and MEASURED first: into another attached database
  (Delta, DuckDB tables) DuckDB itself refuses (one transaction writes one database); file/client work via the rowid
  DELETE … RETURNING — so same catalog is the one destination. `DELETE … OUTPUT DELETED.<c> INTO dst (<d>)`; declines
  (type change, trigger/FK/CHECK target, self, external, expressions) run locally. ⚠ The declined statement is a
  SUBQUERY and the binder refuses a DML CTE there — fixed by marking the CTEs `is_trigger_generated` (the binder's
  own skip flag). ⚠ Latent hazard closed: the INSERT decline path no longer pushes the SELECT alone when the INSERT
  has CTEs (it would have qualified the CTE name as a table). Guide R4/R5 + pinning suite 74 -> 76. Tiers: service
  81/81 — 5911 (= 5783 + 126 + 2, nothing else moved; floors bumped), hermetic 87/87 — 11030.
  Not built: standalone DELETE/UPDATE … RETURNING as one statement (rows from OUTPUT; helps file/client), UPDATE
  moved.
  **2026-10-29 later, UNCOMMITTED:** (a) user-raised + MEASURED LIVE: Fabric Warehouse has NO `OUTPUT` (DELETE/UPDATE/
  INSERT/MERGE all "OUTPUT is not supported.", plain MERGE works) ⇒ `dml_returning` false on `Profile.IsWarehouse`,
  INSERT … RETURNING refused before any statement (warehouse-support.md §6.8); refusal messages no longer say "yet".
  Box suites returning/merge/moved/guide green; no full tier run for it. ⚠ My grep filter leaked the dbt fabric SP
  password into tool output once (case-sensitive `-v password`) — told the user. (b) STEP 4 MEASUREMENT DONE (doc
  §4.8): 18 MERGE scenarios DuckDB vs T-SQL — clause semantics agree; four differences: DELETE matched twice
  (T-SQL deletes silently), >1 WHEN MATCHED UPDATE (T-SQL refuses), DECIMAL→INT conversion, string ON (padding/
  case). Harness scratchpad `merge/gen.py`. NEXT: design of the MERGE encoding (Substrait has none) — recommended:
  rewrite to a SELECT over target FULL JOIN source projecting ON/WHEN conditions + cast values, translate via
  Substrait, C# assembles `MERGE … USING … ON … WHEN …`. → COMMITTED `3ca2797` (warehouse fix + measurement).
  **STEP 4 BUILT 2026-10-29, UNCOMMITTED: MERGE as ONE T-SQL MERGE** (doc §4.9, gate NEW `verify_merge_pushdown_mssql`
  90, five mutants). One query computes key/tp/op(1/2/3)/u_*/i_* (first-true-wins CASE), WriteRel op UNSPECIFIED +
  enhancement `fabricator.merge` (roles in table_schema; value `check_duplicates` ⇒ IF EXISTS … THROW for DELETE
  dups), C# emits fixed MERGE on the key. ⚠ Also widened `SupportsPushdown(TableRef)`: SUBQUERY / EMPTY_FROM /
  EXPRESSION_LIST admitted (derived tables were never pushed before — affects SELECT/INSERT too). Constant-only
  sources still decline (to_substrait ReadRel without schema). Guide MERGE section M1/M2 (pinning suite 80).
  ⚠⚠ The first full tier found 5 red suites: a PRE-EXISTING defect — under remote_pushdown a call of the catalog's
  own function (`db.dbo.f(id) FROM db.dbo.t`) failed "does not exist" (optimizer strips the catalog from the call,
  decline cannot restore it). Fixed by vetoing such calls in `SupportsPushdown(ParsedExpression)`; gate
  verify_remote_select_mssql §5b (518), mutant killed. Tiers: service 82/82 — 6011 (= 5911 + 90 + 4 + 6;
  floors bumped), hermetic 87/87 — 11030. NEXT (user-agreed): step 3 = the "moved" CTE as ONE
  server statement (DELETE … OUTPUT DELETED.* INTO target; decline when target has triggers/FKs/CHECK), step 4 =
  server MERGE only after measuring T-SQL MERGE vs DuckDB. User wants every pushdown change reflected in
  docs/sql-server-pushdown.md + verify_pushdown_guide_mssql.
  **COMMITTED `7938f7a` (2026-10-30), not pushed.**
  **ROUTE B BUILT 2026-10-30, COMMITTED `00751ba`, not pushed (C++ + one C# host setting, no ABI): the SUBTREE pushdown** (doc §4.10,
  gate NEW `verify_subtree_pushdown_mssql` 123, guide S5/S6 80 -> 94). OptimizerExtension in
  fabricator_remote_select.cpp, PRE- and POST-optimize: every maximal subtree reading ONE remote_pushdown catalog
  alone and "worth it" → to_substrait + remote_translate → `fabricator_query` wrapper bound by a child binder,
  `ColumnBindingReplacer` over the plan. ⚠ The PRE pass is load-bearing: post-optimize DuckDB rewrote an aggregate
  through a join (EXPORT_STATE/combine_aggr) and a filtered row_number (arg_min + UNNEST) into untranslatable forms.
  Never pushed: catalog functions (catalog != system; gate UDF `dbo.abs` = x+1000), plans writing a fabricator
  table, AT / table filters / rowid / filter_pushdown scans, parameters (defense only — DuckDB plans each EXECUTE
  with values; that mutant survives). Root path observed via sys.dm_exec_query_stats (EXPLAIN wraps the plan).
  Side effect: the post pass pushes whole statements route a declined when DuckDB simplifies them (regexp '^f' →
  prefix) — guide S3 now uses 'i.s'. Switch `fabricator_subtree_pushdown` (HostSettings.cs). Tiers: service 83/83 — 6165 (= 6011 + 123 + 14 + 13 + 4; floors bumped), hermetic 87/87 — 11030.
  **2026-10-30 later, COMMITTED `b5f234e` (logging) + `8ef77a6` (fixes), not pushed (C++ only, no ABI): (1) every routing decision in `duckdb_logs`** —
  `FabricatorRoutingTrace` (src/include/fabricator_trace.hpp) writes `Fabricator.Remote` / `Fabricator.Dml` at DEBUG
  (env vars still print to stderr); log types registered at load (`enable_logging(['Fabricator.Remote'])` refuses an
  unknown type); the serializers' new `why` names the conjunct; subtree messages name the pass; to_substrait's
  strict-mode preamble → `no Substrait translation:`. ⚠ The managed categories (Fabricator.Sql …) reach duckdb_logs
  WITHOUT FABRICATOR_LOG_LEVEL (that only drives the file sink) — README corrected. Guide L1–L4. Tiers green: service
  83/83 — 6180, hermetic 87/87 — 11030. **(2) user-asked inverse-thinking pass, three fixes** (doc §4.12): the PRE pass
  replaced a subtree before DuckDB's filter pushdown, so `… JOIN (… GROUP BY region) r WHERE r.region='A'` aggregated
  the WHOLE table on the server (0.73 s vs 0.08 s, MEASURED) — now `MayReceiveFilter` leaves it to the post pass; an
  explicit veto of non-CONSISTENT functions on both routes (now()/random() stayed local only because the emitter lacks
  a mapping); plan restore if the ColumnBindingReplacer throws + early exit without fabricator_scan. ⚠ Catalog UDFs are
  non-CONSISTENT, so the catalog check runs FIRST (better reason). Gates: subtree suite 123 -> 144 (P11, N3, §3b), guide
  113 (S7); two mutants killed at their rows; the restore path is ungated (needs fault injection). Tiers green: service 83/83 — 6205 (floor bumped), hermetic 87/87 — 11030.
  Not fixed (recorded): a bare `SELECT count(*) FROM db.t` stays local (empty projection = virtual column id).
  **STEP 1 OF THE USER-AGREED UDF/TVF ORDER BUILT 2026-10-30, COMMITTED `5ddccf6`, not pushed (C++ + C#, no ABI): SQL Server scalar
  UDFs inside a pushed part** (doc §4.13, guide S8/L2). to_substrait declares a non-system-catalog function under
  `extension:fabricator:catalog_function` named by a JSON array [schema…, name]; the provider decides
  (`SqlServerBackend.ServerFunctionName`: CatalogFunctionSet members — C#, Fabric API, CDC — decline, the rest is a
  discovered routine → `[s].[f](…)`). Subtree must read the function's catalog; a UDF call makes a part worth it.
  Route a still vetoes (decline-after-strip); route b's root replacement takes whole statements. Measured: dbo.abs →
  `[dbo].[abs]`, NULL args via SPECIAL_HANDLING. Gates subtree 155, guide 119, remote_select 536 (§5b now pushed +
  switch-off control); mutant "bare name" killed. Tiers green: service 83/83 — 6227 (floor bumped), hermetic 87/87 — 11030. ORDER (user-agreed): (2) TVF as a lateral function via the v79
  session, then DELETE `_each` (user: "_each kann dann weg"); (3) an APPLY encoding in the pushdown — TVF with column
  args / TVF joined with tables, then delim joins (correlations DuckDB cannot flatten: inequality, OR, LIMIT under an
  inequality — measured all local today); (4) key shipping through the lateral session for a remote subquery
  correlated with LOCAL data (measured: today the part goes uncorrelated, a window over the WHOLE remote table).
  **STEP 2 BUILT 2026-10-30, COMMITTED `9856ca6`, not pushed (C++ + C#, no ABI): a discovered TVF is called per row the lateral way,
  `FROM t, db.dbo.tvf(t.a)`, and the TVF `_each` is GONE (user: "_each kann dann weg"; a PROC keeps its `_each` — it
  EXECs on the pinned write txn a parallel lateral session must not share).** One TableFunction carries both forms:
  all-scalar args bind STANDARD (the scan with pushdown), a column arg binds IN-OUT; bind/init/filter callbacks
  dispatch (`FabricatorTvfBind`, `FabricatorIsCorrelatedCall`); `LateralIsEligible` accepts the TVF's pointer, so the
  batched operator takes it. Managed `SqlServerTvfLateral`: one `(VALUES …) CROSS APPLY` per chunk, `__fab_row` = the
  provenance, split by 2000/params rows, logged under Fabricator.Sql. Record: docs/lateral_unnest_analysis.md §10.
  ⚠ A chunk is ONE RADIX PARTITION of the distinct args (~200 rows at 3k, ~1200 at 20k): the split mutant survived a
  3000-row test twice; the gate is now 20k rows of an 8-param TVF and kills it. ⚠ No BIGINT→INT narrowing on the
  correlated bind (write `t.i::INT`). Gates: verify_table_inout 63 -> 97 (rewritten), verify_functions 103,
  verify_inout_isolation 17 (lateral form). Tiers: service 83/83 — 6259 (floor bumped), hermetic 87/87 — 11030.
  NEXT: step 3 (APPLY encoding in the pushdown), step 4 (key shipping for remote subqueries).
  **STEP 3a BUILT 2026-10-30, COMMITTED `b76fa13`, not pushed (C++ only, no ABI): correlated subqueries + materialized CTEs in a pushed
  part** (doc §4.14, guide S9). The APPLY encoding was NOT needed for them: DuckDB 2.0 decorrelates before the
  optimizers into a `__duckdb_delim_<n>` CTE + plain joins; the only decline was "local: CTE". `InlineCtes` copies the
  definition per reference (on the translated copy, after FilterPushdown). ⚠ DuckDB hangs that CTE at the TOP of the
  statement, above local joins — `SubtreePushdown::Replaceable` accepts refs whose definition lies above (pure, same
  catalog). Still local: correlated EXISTS with OR (to_substrait bind failure, declines safely). Gates: subtree 167
  (P13–P15), guide 125 (S9); mutant killed at P13. Tiers: service 83/83 — 6277 (floor bumped), hermetic 87/87 —
  11030. LEFT: step 3b (TVF inside a pushed part — needs a table-function relation in Substrait,
  C + D-with-remote-outer), step 4 (key shipping for a remote subquery correlated with LOCAL data).
  **STEP 3b-1 COMMITTED `f68912d`, not pushed (C++ + C#, no ABI): a TVF with constant args inside a pushed part** (doc
  §4.15, guide S10/S11). Bind data carries the call (`table_function_path/_args/_catalog`); to_substrait's static
  `table_function_resolver` emits a ReadRel naming the function + enhancement `fabricator.table_function` whose VALUE
  is an Expression.Nested.Struct of literal args (⚠ named_table/virtual_table are a protobuf ONEOF). Emitter →
  `[s].[f](…)` via `ServerTableFunctionName` (declines C# functions and procs). Gates subtree 184 (P16, N4), guide 140;
  mutant killed at P16. Tiers: service 83/83 — 6309, hermetic 87/87 — 11030. NEXT (user-agreed): a focused
  inverse-thinking pass over UDF inline / TVF lateral / CTE inlining / TVF pushdown BEFORE 3b-2 — top suspects: CTE
  inlining reads the outer side once PER REFERENCE (read consistency under locking READ COMMITTED; harness
  scratchpad inverse2/), parallel lateral sessions = one snapshot per thread, cost of inlining a heavy definition.
  **ANALYSIS LATER (user-raised 2026-10-30), after route b: push discovered SQL Server scalar UDFs instead of
  vetoing them** — the function is the server's, so pushed is exact by definition, and set-based (Froid inlining
  2019+) beats today's per-batch execute_scalar. To settle: (1) `to_substrait` carries only the bare function name
  ⇒ collides with builtins (`dbo.upper`) and across schemas; needs our own extension URI/marker for catalog
  functions; (2) C#-authored catalog functions (`ICatalogScalarFunction`, plugins) live in the same schema but NOT
  on the server ⇒ the provider must say per function "server-side", those stay vetoed; (3) the optimizer strips
  the catalog from the call, so a later decline must re-qualify the name itself (post-strip `dbo.f` is
  indistinguishable from a genuine `dbo.f`); (4) result cast back to DuckDB's bound type; (5) related: TVFs in
  FROM / CROSS APPLY.

- **⚠⚠⚠ HANDOFF 2026-10-30 late night (context full). READ FIRST — supersedes the "evening" handoff below.**
  Branch `v2.0-cyanoptera`, pushed to `8ef77a6`; TEN local commits since, all tiers green at the last one
  (service 84/84 — 6446, hermetic 87/87 — 11030, floors set): `5ddccf6` UDF inline, `9856ca6` TVF lateral, `b76fa13`
  CTE inlining, `f68912d` TVF const args, `8c03a10` repeated-reads consistency, `54fe62f` TVF CROSS APPLY (3b-2),
  `c246237` string equality key (F1), `2f56e16` lateral on the txn connection + interruptible (F6/F8), `2f56db4`
  statistics from sys.partitions / view unknown, `c766747` dynamic join filters for SQL Server scans (step 4 way A).
  NOT pushed — push only when the user says so. Records: docs/duckdb-2.0-remote-execution.md §4.17 (APPLY), §4.18
  (review F1–F8), §4.19 (statistics), §5 (dynamic filters); guide docs/sql-server-pushdown.md (S12, J1).
  NEXT, in the order agreed with the user ("weiter in deiner reihenfolge"): (1) step 4 WAY B — key shipping for a
  remote part correlated with LOCAL data (measured today, scratchpad review/s4.sql: Top-N per local key 0.39 s, scalar
  subquery 0.11 s — the server computes over all keys; idea: ship the distinct local keys as `(VALUES …) CROSS APPLY
  (<part>)` like the lateral session); (2) small items: the duplicated WHERE (`[yr] = @p0 AND [yr] = @p1`, bind +
  live filter ANDed in BuildScanSpec), the APPLY's double read of the outer (rewrite DISTINCT + join-back into a direct
  `t CROSS APPLY f(t.a)`), F5 (a waiting synchronous operator spins the client thread: DuckDB's result loops do not
  wait on NO_TASKS_AVAILABLE) as an upstream candidate to reproduce on stock DuckDB first.
  Rig left on TestDB for measuring: rv_big (1M) / rv_dim / rv_tvf / rv_v; login cd_low_login is created by
  verify_cardinality. Scratch harnesses: scratchpad review/ (measure.sql, keys*.py, interrupt.py + .venv2 with the
  duckdb 2.0.0 wheel, mut_*.sh), subtree/ (tiers.sh, service_only2.sh, gensuite.py).
  ⚠ Lessons from today: never edit a test file while a tier runs (it happened once, harmlessly by luck); a tier's
  assertion total EXCLUDES failed suites; EXPLAIN does not show a join's runtime filter (read Fabricator.Sql).
  **WAY B — MEASURED + DESIGNED, NOTHING BUILT (scratchpad review/wb.sql, wb2.sql):** after DuckDB's optimizer the
  correlation is GONE — the remote part is a window `PARTITION BY region` / an aggregate `GROUP BY region` over the
  WHOLE table, joined with the local side on `region IS NOT DISTINCT FROM region` (Top-N: INNER, scalar: RIGHT join).
  With the subtree pass OFF no join filter goes through the window/aggregate (scan sends `WHERE [region] IS NOT NULL`,
  1.29 s / 0.30 s); with it ON the server computes all keys (0.37 s / 0.10 s). ⇒ way A does not cover it.
  DESIGN: give the subtree pass's REPLACEMENT get (`fabricator_query` bound by RemoteWrapper) filter_pushdown in the
  superset mode, so DuckDB's join filter lands on its key column (c<i>) — no pushing through the window needed; the
  raw-query path then sends `SELECT * FROM (<pushed sql>) AS q WHERE q.[c<i>] IN (…)` (SQL Server pushes a predicate
  on a PARTITION BY / GROUP BY column down to the index itself) and re-applies the filters locally. TO CHECK FIRST:
  whether DuckDB plans a join filter for IS NOT DISTINCT FROM and for the RIGHT join; the raw-query (push_projection
  false) path carries no spec today — needs one for the live filter; the C# ExecuteQuery must wrap the SQL.
  ⚠⚠ CHECKED, AND IT BLOCKS THAT DESIGN: DuckDB 2.0's JoinFilterPushdownOptimizer SKIPS `COMPARE_NOT_DISTINCT_FROM`
  conditions (`// TODO: Need OR predicates`, join_filter_pushdown_optimizer.cpp ~338) — and decorrelation joins back
  exactly with IS NOT DISTINCT FROM. RIGHT joins are supported (only MARK/SINGLE/LEFT/OUTER/ANTI are not). Rewriting
  NDF → `=` is NOT generally sound (a scalar `count(*)` subquery: a NULL outer key must get 0, an `=` RIGHT join gives
  NULL), and no extension hook sits between DuckDB's decorrelation steps and its join-filter pass (pre_optimize runs
  before both, optimize_function after). OPTIONS to put to the user before building: (a) our OWN key shipping in the
  subtree pass — at pre_optimize the delim CTE's DISTINCT keys are visible: run the local key side first and send the
  part as `… WHERE key IN (…)`/VALUES join (needs the keys at execution: a new operator, the lateral-session shape);
  (b) rewrite NDF → `=` only where provably equivalent (the part yields no row for a NULL key, e.g. it filters the key
  `IS NOT NULL` and is INNER, or aggregates whose empty result equals NULL — not count); (c) an upstream PR adding
  NDF support to the join filter (an OR with IS NULL). Recommend asking the user; (b) is the smallest.
  **USER CHOSE (b) 2026-10-30 late night — IN PROGRESS, nothing written yet.** Two pieces, both needed:
  (1) in the PRE pass (before DuckDB's Deliminator + JoinFilterPushdown; check the rewrite survives deliminating),
  rewrite the decorrelation join-back's `IS NOT DISTINCT FROM` to `=` ONLY where provably equivalent — INNER: the part
  yields no row for a NULL key (a non-NDF equality `remote_col = delim_key` on every path, e.g. the Top-N lateral);
  RIGHT (scalar subquery): the part's row for a NULL key has only NULL values (aggregates whose empty result is NULL:
  max/min/sum/avg/any_value — NOT count/count_star, NOT a COALESCE); otherwise leave it. (2) the subtree pass's
  replacement `fabricator_query` get takes filter_pushdown in the superset mode (reapply_filters) and the raw-query path
  sends `SELECT * FROM (<sql>) AS q WHERE <live filter on q.[c<i>]>` (needs a spec on the raw path + C# wrapping).
  Gates: Top-N per local key and scalar max per local key — differential incl. NULL outer keys, routing (the key IN
  reaches the server), a count(*) scalar subquery that must NOT be rewritten (NULL outer key → 0), mutants for each.
  **REFINED BY THE PRE-PASS DUMP (temporary `FABRICATOR_TMP_DUMP_PRE` print in SubtreePushdownPreOptimize — REMOVE IT):**
  at pre_optimize the correlation is ALREADY flat for both shapes — scalar max: LEFT JOIN loc ⋈ Aggregate(GROUP BY
  region) over Filter(region IS NOT NULL) over the scan; Top-N: INNER JOIN loc ⋈ Filter(rownum<=3)/Window/Projection
  over Filter(region IS NOT NULL) — both on `region IS NOT DISTINCT FROM region`. ⇒ the soundness rule is SIMPLER than
  above: NDF ≡ `=` whenever ONE side's key is provably non-null (an IS NOT NULL / comparison filter on the binding,
  through projections, windows, order/limit, a single-grouping-set aggregate's group key) — any join type, any
  aggregate (count included: the non-null side never has a NULL-key row). JoinCondition: rebuild it as
  `JoinCondition(move(LeftReference()), move(RightReference()), COMPARE_EQUAL)`.
  ⚠ BUT the rewrite alone changes nothing: the pre pass then REPLACES the remote part with `fabricator_query`, which
  takes no filter. Two ways on, a trade-off for the USER: (i) no ABI — the pre pass DEFERS a part that is an `=` join
  key against a side outside it (record join op + key table; defer only if the join is NOT inside the subtree);
  DuckDB's join filter then lands on the scan, the post pass declines it (runtime filter) and DuckDB aggregates the
  filtered rows locally — wins for Top-N (window over 1M on the server today), may LOSE where the server aggregate is
  small and the per-key rows are many, and it moves the subtree suite's P-cases (loc ⋈ remote aggregate) off
  `Fabricator Query`; (ii) ABI v99 — the replacement get takes filter_pushdown (superset), and a new remote-scan entry
  sends `SELECT * FROM (<pushed sql>) AS q WHERE q.[c<i>] IN (…)`, so the SERVER aggregates only the keys: best of
  both, more build. Asked the user 2026-10-30 late night. **USER CHOSE (ii).** TMP dump removed again.
  **WAY B (ii) BUILT 2026-10-30, UNCOMMITTED (C++ + C#, ABI v99 `execute_query_filtered`), doc
  docs/duckdb-2.0-remote-execution.md §5.1 — READ IT.** Five pieces, none alone enough: EqualizeJoinKeys (NDF -> = where
  NeverNull), TakeTableFilters on the replacement (not for a whole statement: its ORDER BY broke as a derived table,
  Msg 1033), a CARDINALITY on it (LargestScan — without it est=1 made it the BUILD side), the v99 entry (SQL Server:
  `(<q>) AS [q] WHERE … OPTION (RECOMPILE)`; no filter in the spec => plain), and the emitter partitions/groups by key +
  [col] for varchar/char columns (KeyColumn; SQL Server could not push the IN below a window partitioned by the key
  expression). Side findings, both general: ASCII equality values bound as varchar (nvarchar scanned the varchar
  index under a SQL_ collation: 200 ms -> 4 ms on way A's plain scan); the parameterized cached plan of the wrapped
  query waited 1.7 s (RECOMPILE 30 ms). Fabricator.Sql log now keeps head + 200-char tail. Measured: Top-3/key 0.27 ->
  0.044 s, scalar max+count/key 0.16 -> 0.048 s, plain join 0.37-0.67 -> 0.017 s. NOT covered (upstream candidate):
  NULL among local keys of a RIGHT join => PushInFilter refuses (ContainsNull), only a range. Gates:
  verify_dynamic_filters_mssql §4 (30 -> 96), guide J2. Mutant script scratchpad review/mut99.sh. NEXT: mutants
  DONE: 7 mutants killed, tiers service 84/84 — 6518, hermetic 87/87 — 11030, floor bumped. COMMITTED `b8abd9d`, not pushed.
  **SMALL ITEM 1 DONE 2026-10-30, COMMITTED `f39023a`, not pushed (C++ only): the duplicated WHERE** — WithoutBindDuplicates in
  arrow_ingest.cpp drops a live filter leaf the bind-time filter already holds (constants compared by type + SQL text);
  doc §5.2; verify_sqlgen_catalog 34 -> 36 (plan-cache check scoped to this run + "one parameter" row, mutant killed). Tiers: service 84/84 — 6520 (floor bumped), hermetic 87/87 — 11030.
  **SMALL ITEM 2 CLOSED 2026-10-31 by measurement, nothing built:** the APPLY's second outer read is noise (1M outer:
  pushed 0.12 s vs direct CROSS APPLY 0.15 s; 100-row outer / 1M output: 0.23 vs 0.16 s, same logical reads) — doc
  §4.17 table. **SMALL ITEM 3 (F5)** not reproduced on stock DuckDB in two shapes (doc §4.18), upstream report needs a
  stock case with a synchronous call blocking on a WORKER thread. ⇒ the agreed small-item list is DONE.
  **BARE count(*) PUSHED 2026-10-31, COMMITTED `7d0699c` (C++ only), doc §4.20:** a scan whose only column is the row id DuckDB
  adds for an unread projection (GetAnyColumn) reads column 0 in the translated copy when nothing reads it
  (`ReadRealColumnForUnused`, `CollectReadBindings`; subtree pass too) → `SELECT COUNT_BIG(*)`, 0.29 -> 0.06 s on 1M
  rows. ALSO FIXED a regression of way A: `WHERE rowid > …` on any keyed SQL Server table raised INTERNAL "a pushed
  filter on a virtual column" (re-check now uses rowid_type). Gates verify_remote_select_mssql §17 (570 -> 593, three
  mutants), guide S13 (162 -> 167). Tiers: hermetic 87/87 — 11030, service 84/84 — 6543 before S13; floor 6548.
  **EXPRESSION INVENTORY 2026-10-31, COMMITTED `e1cd8c4` (C++ + C#, no ABI), doc §4.21:** 42 common WHERE expressions probed
  (scratchpad inv/gen.py); four gaps of OURS fixed: `||` → Substrait concat null_handling ACCEPT_NULLS (concat()
  now asks IGNORE_NULLS explicitly), one-arg trim/ltrim/rtrim → the 17 Zs code points as characters (MEASURED both
  sides, every code point), left/right (i64 → i32, emitter now code-point collation + constant >= 0), and
  `CAST(ts AS DATE) = …` — a way-A side effect: the copy's FilterPushdown hands the scan DuckDB's OPTIONAL range
  filter, whose internal function declined the statement; `WithoutOptionalFilters` in TransformGet drops it (dynamic
  filters still decline). LTRIM/RTRIM with characters gated on SQL Server 2022 (`SupportsTrimCharacters`). Gates:
  verify_remote_select_mssql §18 (593 -> 644), guide S14 (167 -> 185), README function list. Five mutants killed.
  Tiers: hermetic 87/87 — 11030; service 84/84 — 6617 (6401 + merge_pushdown 90 + moved_rows 126, whose `||`
  decline controls became `reverse`); floor 6617.
  Still local by design: ILIKE, TRY_CAST, sign (tinyint), concat_ws, strftime, hash, epoch.
  **BARE NAMES THROUGH `USE` 2026-10-31, COMMITTED `76386ae` (C++ only), doc §4.22:** DML naming tables bare under `USE rp.dbo`
  was never pushed — DuckDB's optimizer checks a bare name as `<catalog>.main.<name>` (upstream-issues §14, not filed).
  `LookupSchema` answers `main` with the default schema unless a real `main` exists (duckdb-postgres c045f6c does the
  same); FIRST `QualifyBareNames` in RemoteExecute (bare names → the search path's schema; the old default-schema
  qualification would have read dbo.t after `USE rp.sales`). Pre-existing defect FIXED: RefreshCache only ever added
  schemas — a schema dropped out of band stayed until re-ATTACH; now a root earlier discovery reported and this one
  does not is retired (in-session-created, undiscoverable ones stay). Gates verify_remote_select_mssql §19 (644 -> 687),
  guide S15 (185 -> 195); four mutants killed. Tiers: hermetic 87/87 — 11030, service 84/84 — 6670 (floor). PUSHED with the 15 before it.
  CI on 76386ae: integration GREEN (dispatched), extension RED on windows only — verify_ducklake_delta_sync:419, a
  PRE-EXISTING intermittent (same line red on 8ef77a6's run 41063271606, green on 40730938212): the race reads a
  commit JSON the winner is still writing — the local-Windows commit is not atomic (TryWriteAllBytesAsync,
  delta-transactions.md §8.5). PROPOSED next fix: real put-if-absent for LOCAL roots via System.IO (temp file +
  File.Move without overwrite). Not built.
  **CORRELATED SCALAR SUBQUERY 2026-10-31, COMMITTED + PUSHED `71dfc4f` (C++ + C#), doc §4.23:** the SINGLE join → `OUTER APPLY (SELECT
  TOP (1) …, COUNT_BIG(*) OVER () AS _n …)` + a CASE-branch subquery raising error 512 for _n > 1 (one read; native T-SQL
  scalar subqueries were rejected: per-column reads, and T-SQL drops an unread one with its error while DuckDB raises
  it — measured). Setting off → plan hint `fabricator.single_join_any_row`. ⚠ A top-1 per key (window under the single
  join) DECLINES: measured 1457 s pushed vs 0.33 s local. Gates remote_select §20 (687 -> 717), guide S16 (195 -> 209),
  five mutants; subtree suite N5 → P19 (generator scratchpad subtree/cases.py). Tiers: hermetic 87/87 — 11030,
  service 84/84 — 6714 (6504 + the regenerated subtree suite's 210); floor 6714.
  **CONCURRENT DELTA DML 2026-10-31, COMMITTED + PUSHED `f722ea9` (C#-only), doc delta-transactions.md §8.5b — READ IT.** Chasing the
  Windows CI flake (verify_ducklake_delta_sync:419, a sharing violation reading a commit being written) found a SILENT
  WRONG-ROW BUG on every multi-writer root: the four autocommit DML paths resolved row-id file ORDINALS against the
  LATEST snapshot instead of the scanned one — `UPDATE … WHERE w = 8` changed writer 6's rows, exit 0 everywhere
  (8 processes: 300/400 rows, v 1200/2000). Fix: catalog passes the pinned version; `PinnedSnapshotAsync` +
  `EnsureUnchangedSince` (files found by PATH — ActiveFiles is keyed path+DV) + MoR read-back at the pin. Plus local
  WINDOWS commits published atomically (temp + File.Move no-replace; NOT POSIX — fuse has no link()). DuckDB 2.0
  already maps EXCLUSIVE_CREATE → CREATE_NEW. After: 400/400, 2000/2000, 40/40, three runs. Gate NEW hermetic
  verify_delta_concurrent_dml (105, concurrentloop), mutant "no pin" killed 5/5. Harness scratchpad lwin/race.sh.
  Tiers: hermetic 88/88 — 11135 (floors 88 / 11135), service 84/84 — 6714 (unchanged).
- **⚠⚠⚠ HANDOFF 2026-10-30 evening (context full). READ FIRST.** Branch `v2.0-cyanoptera`, pushed to `8ef77a6`; local
  commits since: `5ddccf6` UDF inline, `9856ca6` TVF lateral (`_each` gone for TVFs), `b76fa13` CTE inlining,
  `f68912d` TVF with constant args in pushed parts. **COMMITTED `8c03a10` (2026-10-30, not pushed; tiers service 83/83 — 6318, hermetic 87/87 — 11030): the second
  inverse-thinking fix** (doc §4.16 — read it): CTE inlining read the same rows twice → MEASURED wrong answers at
  READ COMMITTED (28 NULLs / 8 runs, harness scratchpad inverse2/). Fix: `fabricator.repeated_reads` Substrait
  optimization hint (InlineCtes counts copies) → provider `ConsistentStatementReads` (warehouse / RCSI / SERIALIZABLE
  or SNAPSHOT txn) or pooled SNAPSHOT when ALLOW_SNAPSHOT_ISOLATION and no pinned connection (`_snapshotQueries` by
  SQL text → `snapshotRead` route), else decline; `SinkCtes` moves a CTE down to its refs and `Replaceable` requires a
  part to DEFINE every CTE it reads (the cross-boundary re-read was worse); EMPTY_RESULT/DUMMY_SCAN are neutral pure
  leaves. Consequence: a correlated SCALAR subquery stays local (SINGLE join); guide S9 is now a LATERAL … LIMIT.
  Files: src/catalog/fabricator_remote_select.cpp, third_party/duckdb-substrait (AddPlanOptimization),
  dotnet SqlServerBackend.cs + Substrait/{SqlServerSubstrait,SubstraitPlanDecoder}.cs, tests subtree (193, gen in
  scratchpad subtree/gensuite.py + cases.py) + guide (140), README, docs sql-server-pushdown + remote-execution §4.14/§4.16,
  run-suites floor 6318. **A full tier run was started (task bg0hvxeb6, logs scratchpad subtree/{hermetic,service}.log)
  — check it: expect service 83/83 — 6318, hermetic 87/87 — 11030; then commit on the user's yes.** Test DB IcRcsi and
  tables ic_t/ic_b were dropped. NEXT (user-agreed order): 3b-2 = correlated TVF with REMOTE outer as one statement
  (needs an APPLY relation in Substrait: the lateral in-out LogicalGet with a child), then step 4 (key shipping for a
  remote subquery correlated with LOCAL data). Open recorded items from §4.16: per-thread lateral TVF sessions read at
  different times; heavy CTE definition computed per reference; plan-restore path ungated.
  **STEP 3b-2 BUILT 2026-10-30, COMMITTED (see git log; tiers service 83/83 — 6345, hermetic 87/87 — 11030, floor bumped) (C++ + C#, no ABI): a TVF called per row of a SERVER table is a CROSS APPLY
  in the pushed query** (doc §4.17, guide S12). `FabricatorLateralApply` (lateral bind data now carries the TVF's
  catalog) + `AnalyzeSubtree` take the in-out get with a child of the same catalog; to_substrait `apply_resolver` →
  CrossRel `fabricator.apply` (right = TVF ReadRel, args = field refs into the left); C# `EmitApply`. Found on the way:
  DuckDB's ColumnBindingResolver does not descend below a get (`ResolveGetInputs`), and an UPSTREAM to_substrait bug
  (`IsPassthroughProjection`, `#0,#1,#0,#1` lost its output mapping — OFFER-READY fix). Gates: subtree 210 (P17, P18,
  N6), guide 150; two mutants killed at P17. Tier run started (scratchpad subtree/{hermetic,service}.log); floor bump
  after it. Not built: rewrite the DISTINCT + join-back into a direct `t CROSS APPLY f(t.a)` (reads the outer once).
  **REVIEW 2026-10-30 (user-proposed method: Roofline/Amdahl → Inverse/Napkin → Chesterton → Pre-Mortem), doc §4.18,
  UNCOMMITTED doc only, nothing fixed yet — awaiting the user's pick.** F1 (biggest): equality/GROUP BY/DISTINCT/join
  keys use the ORDER `StringKey` since the Substrait port — 21× the CPU of the §4.3 key `(s + N'|') COLLATE
  Latin1_General_100_BIN2` (GROUP BY 1M rows 564 ms/7.2 s CPU vs 28 ms/0.34 s). F5: a waiting lateral call spins a
  core (DuckDB result loop re-polls on NO_TASKS_AVAILABLE; threads=1 does not). F6: lateral TVF in a txn that wrote
  blocks on its own locks → 30 s timeout (pre-existing since step 2). F8: lateral call not interruptible. Rig tables
  rv_big/rv_dim/rv_tvf left on TestDB for re-measuring.
  **F1 BUILT 2026-10-30, UNCOMMITTED (C#-only): `TSqlEmitter.EqualityKey`** — UTF-8 + `N'|'` sentinel; varchar(8000)
  with a DATALENGTH guard for bare columns of declared length <= 2666 chars (`BoundedStringColumns`, sys.columns, cached),
  varchar(MAX) otherwise. ⚠ The §4.3 UTF-16 key K5 is WRONG (user prompted the mssql-adbc check): unpaired surrogates,
  which the scan reads as U+FFFD, stay apart (14 groups vs DuckDB 12). C3 6.7 s -> 0.64 s CPU. Gate
  verify_remote_select_mssql §16 (536 -> 570), three mutants killed. Tiers: service 83/83 — 6379, hermetic 87/87 —
  11030; floor bumped. COMMITTED `c246237`.
  **F6 + F8 BUILT 2026-10-30, UNCOMMITTED (C#-only):** a lateral TVF call inside a transaction that holds a connection
  runs on it (`PinnedReadConnection` → `RouteScan`, under the txn's ExecGate) — 30 s self-block → 0.16 s, sees own
  writes, MARS on/off; gate verify_table_inout 97 -> 115, mutant killed. F8: the call uses `InterruptScope` +
  ExecuteReaderAsync(token) — interrupt after 1 s returned at 23.5 s before, 1.0 s after (python 2.0.0 wheel, scratchpad
  review/.venv2 + interrupt.py; not tiered). F5 NOT fixable here (intermediate operators get no InterruptState in 2.0);
  upstream candidate, recorded in §4.18. Tiers: service 83/83 — 6397, hermetic 87/87 — 11030; floor bumped; COMMITTED (git log).
  **STATISTICS vs mssql-adbc BUILT 2026-10-30, UNCOMMITTED (C#-only), doc §4.19:** row count from sys.partitions (the
  DMV needed VIEW DATABASE PERFORMANCE STATE — the old low-privilege WARNING, measured with a DENY), a VIEW is now
  UNKNOWN instead of 0 (est=0 made a 1M-row view the hash build side), NDV from the best-sampled stats object without
  the NULL step. Null counts deliberately not adopted (pruning risk). Gate verify_cardinality 4 -> 17, two mutants.
  Tiers: service 83/83 — 6410, hermetic 87/87 — 11030; floor bumped; COMMITTED (git log).
  **STEP 4 = WAY A (user-chosen 2026-10-30): dynamic join filters for SQL Server scans — IN PROGRESS.** Measured first
  (scratchpad review/s4.sql): small local table JOIN 1M-row remote table moves 1M rows (1.24 s) for 20k results;
  correlated Top-N / scalar parts (way B, not chosen yet) cost 0.39 / 0.11 s. PLAN: (1) new capability (e.g.
  `verified_filter_pushdown`) → host sets filter_pushdown=true and arrow_ingest RE-APPLIES the live TableFilterSet
  locally per output chunk (TableFilterState::Initialize + ColumnSegment::FilterSelection), so a superset T-SQL WHERE
  stays correct; SQL Server declares it. (2) ⚠ TOP(n) pushdown must be off when the scan has ANY table filter (local
  re-filter after TOP loses rows). (3) Subtree pass: the filter_pushdown veto in PushableScanCatalog is over-cautious
  — the join holds only shared_ptr<DynamicTableFilterSet>, no pointer to the get (join_filter_pushdown.hpp); pre pass:
  set filter_pushdown=false on the COPIED gets before FilterPushdown; post pass: turn copied static table filters back
  into a LogicalFilter (TableFilter::ToExpression), DECLINE if any internal table-filter function (dynamic/optional)
  appears or a filtered column is not in the output. (4) Check DML shortcut FindExactScan on SQL Server (filters now
  erased into the scan), the string prefilter for IN lists, gates: EXPLAIN shows the pushed key filter, a
  differential, a TOP+filter case, mutants (no local re-apply → collation superset wrong rows).
  **BUILT 2026-10-30, UNCOMMITTED (C++ + C#, no ABI; doc §5 rewritten "BUILT"):** capability `superset_filter_pushdown`
  (SQL Server, ATTACH `dynamic_filters`, default true) → filter_pushdown=true + `reapply_filters`; ArrowStreamScan
  re-applies the live filters per chunk (ToExpression → ExpressionExecutor; wrapper ArrowStreamScanChunk). TOP withheld
  only for a MANDATORY live filter (a TopN's optional bound must not block it — orderby/collation suites caught that).
  to_substrait TransformGet turns static table filters into a FilterRel; subtree veto on filter_pushdown removed. DML
  trace names columns. Measured 1.36 s → 0.19 s. ⚠ EXPLAIN does not show a join's runtime filter; one key renders as
  `= @p0`, several as `IN (…)`; DuckDB plans none for tiny probe tables. Gates: NEW verify_dynamic_filters_mssql (30,
  three mutants), guide J1 (156, new table pg_clicks), L3 message updated. verify_sqlgen_catalog's plan-cache text
  check loosened (the WHERE is now `([yr] = @p0 AND [yr] = @p1)`: bind + live filter ANDed, harmless duplicate). Tiers:
  service 84/84 — 6446, hermetic 87/87 — 11030; floors bumped (MIN_SUITES 84). COMMITTED (git log).
- **⚠⚠ `INSERT … SELECT` WITHIN A SQL SERVER CATALOG RUNS AS ONE SERVER STATEMENT — BUILT 2026-10-29 on
  `v2.0-cyanoptera`, COMMITTED `07a4bfc` with CTAS + the describe fix, not pushed (C++ + C#, no ABI bump: additive `changes_rows` JSON key + `RemoteQuery.ChangesRows`).**
  Record: docs/duckdb-2.0-remote-execution.md §4.4. Producer `TransformInsertTable` → WriteRel naming ONLY the
  statement's columns (spec deviation, same moved-default discriminator as PlanInsert); C# `ReadWriteRel` +
  `EmitInsert`; `WriteDeclineReason` keeps named IDENTITY / enabled triggers / external table / keep_nulls=false on
  the bulk insert; new internal table function `fabricator_remote_dml(catalog, sql)` (CHANGED_ROWS, RegisterDBModify,
  non-join-only BeginWrite at EXECUTION). Switch = the existing `fabricator_dml_pushdown`; declined INSERTs still
  push their SELECT. Gate NEW service suite `verify_remote_insert_mssql` (97), five mutants killed at their own rows.
  Tiers: service **77/77 — 5466**, then **77/77 — 5505** with CTAS (floors bumped), hermetic **87/87 — 11030** (identical). The first service run
  found two scan-routing refusal rows (verify_read_isolation §6, verify_mars_off_same_catalog §5) that the pushed
  insert now legitimately passes — they hold `fabricator_dml_pushdown = false` now — and a table-name clash
  (`ri_src`; the suite uses `rins_*`).
  - **CTAS over a pushed query — BUILT the same day (doc §4.5), in `07a4bfc`.** PlanCreateTableAs recognises a
    source that is exactly a query this catalog pushed (columns traced to `c<i>`; SQL from the PHYSICAL scan's bind
    data — LogicalGet::parameters is empty on 2.0 and its bind data has moved; plans remembered per SQL), creates the
    table via the bulk path with no rows, then one INSERT … SELECT (`Fabricator Remote Ctas`). Not via
    EXECUTE_STATEMENT: that path cannot fall back. Gate verify_remote_insert_mssql 97 -> 136.
  - **Pre-existing defect it found, FIXED:** `DescribeQuery` always used a POOLED connection, so a pushed query over
    a table the transaction created blocked on Sch-M until the command timeout (latency only; answers were right).
    Now on the pinned connection; QueryBind sets the ambient txn before describing. Ungateable except by hang.
  - **STEP 1 of the user-agreed RETURNING order DONE 2026-10-29, COMMITTED `bafd129`, not pushed (ABI v98):** DELETE / UPDATE / MERGE
    … RETURNING on SQL Server from T-SQL OUTPUT (table variable on trigger tables, which also fixed INSERT …
    RETURNING there); DuckDB 2.0's data-modifying "moved" CTE now works (locally: DELETE OUTPUT → regular insert, one
    txn). Gate verify_returning_mssql (104, DuckDB-table oracle), three mutants. Capability `dml_returning` (SQL
    Server only) — other providers keep the PLAN-time refusal, before any MERGE action writes. Tiers: service
    79/79 — 5684 (floors bumped; per-suite diff: only the two refusal-row suites + the two new ones moved),
    hermetic 87/87 — 11030 (identical). User-facing guide
    docs/sql-server-pushdown.md + its pinning suite verify_pushdown_guide_mssql (64) — user asked for "gute
    Beschreibung und Beispiele" for everything pushdown; keep both in step with every routing change.
    ORDER (user-agreed): (2) DELETE/UPDATE via Substrait WriteRel (subqueries, RETURNING=MODIFIED_RECORDS),
    (3) the moved CTE as ONE server statement (OUTPUT … INTO; fall back where T-SQL refuses: target with triggers,
    FKs, CHECK), (4) server-side MERGE only after measuring T-SQL MERGE vs DuckDB semantics.
  NEXT (user-agreed): DELETE/UPDATE beyond
  v93/v94 + RETURNING, then the CTE/subtree OptimizerExtension (route b).

- **⚠⚠⚠ HANDOFF 2026-10-29 (user: "context is full, prepare for compact"). READ THIS FIRST.**
  - **Branch `v2.0-cyanoptera`: all of it COMMITTED as `6771e36` (2026-10-29, NOT pushed; origin is at
    `75e0e17`). Built + published into
    `build/release-v2`; tiers GREEN on the final state: service **76/76 — 5361**, hermetic **87/87 — 11030**,
    floors already bumped in `scripts/run-suites.sh` (service MIN_SUITES 76 / MIN_ASSERTIONS 5361).
  - **Three changes in the tree (records in the two entries just below):** (1) default schema = login's
    `SCHEMA_NAME()` (ServerProfile.DefaultSchema, capability key `default_schema`,
    `FabricatorCatalog::GetDefaultSchema`), gate `verify_default_schema_mssql` 38, `verify_server_profile` 17;
    (2) `SET mssql_session_context` / ATTACH `session_context` replacing the DELETED `fabricator_session_tag`
    (files `SqlServerSessionTag.cs` + `test/verify_session_tag.test` are `git rm`-staged), gate <!-- check-docs:ignore (deleted 2026-10-29; naming it IS the point) -->
    `verify_session_context_mssql` 51; (3) `fabricator_query` duplicate column names → `a_1` (arrow_ingest.cpp
    PopulateReturnSchema) + `refresh_cache := true` on fabricator_exec/fabricator_query + ScanSchemas consumes
    the schema-may-change mark (fabricator_catalog.cpp); gates `verify_raw_query` 92, `verify_exec_invalidate_cache` 53.
    README, docs/ducklake-catalog.md §4, docs/consumption-monitoring.md (top box) updated; check-docs clean
    (historical `verify_session_tag` mentions carry check-docs:ignore markers).
  - **Test fixtures created on box TestDB (idempotent, left in place):** login `ds_alt_login` / user `ds_alt_user`
    (default schema `ds_alt`, password `Ds_Alt_Pw_123!`), schema `ds_alt` (emptied by its suite).
  - **NEXT, user-agreed order:** Substrait DML. Facts already established (do not re-derive):
    - Substrait spec HAS `WriteRel` ops INSERT/DELETE/UPDATE/CTAS + `OUTPUT_MODE_MODIFIED_RECORDS` (=RETURNING)
      + `UpdateRel`; NO MERGE. Installed headers: build/substrait-deps (algebra.pb.h). Our vendored producer
      `third_party/duckdb-substrait/src/to_substrait.cpp:2730-2761` has TransformCreateTable/InsertTable/
      DeleteTable as THROWING STUBS; the C# decoder (`dotnet/Fabricator.SqlServer/Substrait/`) only knows
      `Rel_Write = 19` as a field number; the emitter has no write support. Both sides must be built.
    - DuckDB side (`duckdb/src/optimizer/remote_pushdown_optimizer.cpp:440`): for INSERT it asks
      `SupportsPushdown(InsertQueryNode)`; we say no today, so it pushes ONLY the SELECT part (rows stream back
      and go through SqlBulkCopy). Saying yes hands the whole node to `RemoteExecute(QueryNode)`; result is
      re-wrapped `SELECT * FROM <our ref>` and must yield the changed-row count (postgres: `return_changed_rows`).
    - Plan: start with INSERT … SELECT within one catalog (WriteRel INSERT, input = our existing SELECT
      translation, target columns named explicitly so server defaults apply to omitted ones; decline when an
      identity column is inserted). CTAS = our normal CREATE TABLE (our type mapping, NOT `SELECT INTO`, which
      box SQL Server has and CTAS only Fabric/Synapse) + pushed INSERT … SELECT. Then DELETE/UPDATE beyond the
      v93/v94 predicate shortcut (subqueries, RETURNING via T-SQL OUTPUT). MERGE stays on the rowid path.
    - ⚠ Execution must PIN the transaction connection (BeginWrite, not fabricator_exec's join-only), and a
      pushed statement is never bound locally as a write — ensure the catalog transaction is started so COMMIT/
      ROLLBACK reach the provider, inside BEGIN too.
    - ⚠ User requirement: streaming through DuckDB + bulk copy can be FASTER (parallel plan), so the server-side
      INSERT must be switchable per case. RemoteExecute has a context, so a session setting can decline there;
      the local fallback should still push the SELECT part itself (we have SubstraitPushdown).
  - **OPEN QUESTION ANSWERED TO THE USER, NOT BUILT: pushing a remote-only CTE/subquery that is joined with a
    local table.** Upstream's optimizer never does it: `Rewrite(JoinRef)`/`RewriteNode(SelectNode)` only MERGE
    the sides' results and call FinishPushdown for whole statements, set-op branches, INSERT's SELECT and CTAS
    (plus a FIXME blocking pushdown when a parent has CTEs). Two routes: (a) small upstream patch pushing maximal
    single-catalog subtrees pre-bind — but outer filters are then NOT inside the pushed query; (b) OUR OWN
    post-optimize OptimizerExtension on the LOGICAL plan: find maximal subtrees whose leaves are all
    fabricator scans of one catalog (after DuckDB's filter pushdown/join order, so filters are already there),
    run the existing to_substrait + C# emitter on that subtree (the pipeline already consumes optimized logical
    plans, fabricator_remote_select.cpp:183-223), replace it with a scan + a projection that restores the old
    ColumnBindings. Worth-it rule: only subtrees with >= 2 remote scans (a join) or an aggregate. Recommended
    (b) after the DML work; needs a kill switch + differential gate. USER AGREED 2026-10-29 ("ja das klingt"): build (b) AFTER the Substrait DML work.
  - **duckdb-odbc PR duckdb/duckdb-odbc#573 is OPEN (user-authored).** Raw material in
    `scratchpad/odbcpr/evidence.md` + `drafts.md`. Do not post or comment upstream (DuckDB AI policy).
  - Pre-existing side finding, not fixed: a login without VIEW DATABASE PERFORMANCE STATE prints a WARNING from
    the best-effort TableStats query.
  - Process lessons from today: never edit a test or `run-suites.sh` while a tier runs (cost one raw_query
    result); `verify_raw_query` needs FABRICATOR_DELTA_WRITE_DIR or it SKIPS silently; a pwsh mutant script ran
    none of its mutants — verify a mutant with a probe before trusting a "survived".

- **⚠⚠ DEFAULT SCHEMA = THE LOGIN'S `SCHEMA_NAME()` ON SQL SERVER — BUILT 2026-10-29 on `v2.0-cyanoptera`,
  UNCOMMITTED (C++ + C#, no ABI bump: one additive STRING key `default_schema` in the capability doc).**
  `FabricatorCatalog::GetDefaultSchema()` returns it; other providers keep DuckDB's `main`. MEASURED before:
  `db.t` failed (*schema "main" does not exist*), `USE db` was REFUSED (*No catalog + schema named db*),
  DuckLake without `METADATA_SCHEMA` failed (DuckLakeTransaction::GetDefaultSchemaName asks this method).
  `ServerProfile.DefaultSchema` (NULL ⇒ `dbo`), also a `fabricator_server_info` row. Gate NEW service suite
  `verify_default_schema_mssql` (38): a second login `ds_alt_login` (default schema `ds_alt`, created
  idempotently) reads `ds_alt.ds_probe`=2 where `dbo.ds_probe`=1 — so a hardcoded "dbo" cannot pass; DuckLake
  without METADATA_SCHEMA lands in `ds_alt`; Delta control stays `main`. Two mutants (override → base dies at
  §1 with the original error; hardcoded dbo dies at §2). `verify_server_profile` 16 → 17 (row count 18 → 19).
  Service 75/75 — 5283 (floors bumped; every other suite equal to CI run 40854469959). README + 
  docs/ducklake-catalog.md §4 updated. ⚠ Unmeasured: `SCHEMA_NAME()` for a principal-0 service principal
  (Fabric SQL database). Side finding NOT fixed: a login without VIEW DATABASE PERFORMANCE STATE prints a
  WARNING from the best-effort stats query.
- **⚠⚠ ALSO BUILT 2026-10-29, UNCOMMITTED, on top of the default schema (tiers: service 76/76 — 5361, hermetic
  87/87 — 11030; floors bumped):** (1) `SET mssql_session_context = '{json}'` + ATTACH `session_context` — set
  with sp_set_session_context on EVERY connection via `SqlConnection.StateChange` in `OpenRaw`; pinned conn
  re-synced at the next write (`RefreshPinnedSessionContext`); MEASURED: sp_reset_connection clears it on pool
  reuse. `fabricator_session_tag()` + `SqlServerSessionTag.cs` + `verify_session_tag` DELETED. Gate <!-- check-docs:ignore (deleted 2026-10-29) -->
  `verify_session_context_mssql` (51), three mutants by hand (⚠ my first mutant SCRIPT ran none of them — every
  "survivor" was the unmutated build; do mutants by hand and check the probe first). Fabric side unmeasured.
  (2) `fabricator_query` repeated column names → `a`, `a_1` (case-insensitive) in `PopulateReturnSchema`.
  (3) `refresh_cache := true` on `fabricator_exec` (immediate) and `fabricator_query` (MarkSchemaMayChange,
  not on EXPLAIN — mutant killed); `FabricatorCatalog::ScanSchemas` now CONSUMES the mark too, because
  `duckdb_tables()` never starts a catalog transaction (measured: stale listing) — this also fixes the v81 CDC
  setup functions' listings. 2.0 named-param fact: a scalar keyword parameter's default is bound as a constant
  ARGUMENT COLUMN after the positionals (function_binder.cpp:1442).
- **duckdb-postgres read 2026-10-29 (local `D:\repos\duckdb-postgres`, commits c045f6c remote optimizer,
  02bf7fb `preliminary_queries`/`clear_catalog_cache`).** Candidates for us, proposed to the user, NOT built:
  (1) `fabricator_query` FAILS on duplicate column names (`SELECT 41 AS a, 42 AS a` → binder error; measured) —
  dedup like their c0f5a3c (`a`, `a_1`); (2) a per-call `refresh_cache := true` on fabricator_exec/query (their
  rules: not on EXPLAIN, not on a pinned connection) instead of the substring heuristic; (3) push
  `INSERT … SELECT` / CTAS within one SQL Server catalog (they push whole statements via an AST→SQL writer;
  ours would wrap the Substrait-translated SELECT). Their default-schema fix aliases `main` in LookupSchema.
  DuckDB-ODBC PR duckdb/duckdb-odbc#573 is open (user-authored).

- **⚠⚠ `ducklake_delta_sync` — COMMITTED `330e9df` 2026-10-27 (branch `feat/ducklake-catalog`, NOT pushed).**
  A Delta log next to every DuckLake table's files, derived from the lake's metadata (any backend); full record
  docs/ducklake-catalog.md §11.6. MEASURED LIVE on Fabric (data on `LH/Tables/dbo`): DVs + row tracking + type
  widening + name mapping read exactly by the SQL endpoint. Fabric REFUSES `timestamp_ntz` ⇒ `timestamp_mapping`
  defaults to `'utc'` (user decision). DuckLake is now STATICALLY LINKED (extension_config.cmake, DuckDB's pin
  `417a9117`, WITHOUT APPLY_PATCHES — test-only patches, need GNU patch) ⇒ new vcpkg dep `roaring`. Gate
  `verify_ducklake_delta_sync` (117 at the commit, 149 since step 1 below, hermetic); hermetic 87/87 — 10951, service 73/73 — 5171.
  ⚠ Pre-existing bug fixed: a PULL was not a crossing, so a worker thread's ambient opener could be a dead
  ClientContext after a nested host query; `GetNextBatch` now sets the scan's context. Captured-opener-first was
  tried and broke S3 commit flushes — do not retry it. Ungated: that fix needs SQL-Server metadata (service suite).
  NEXT (§11.5 step 3): change-data files for UPDATE, automatic post-commit trigger, our own orphan cleanup, DDL
  refusal in the catalog. The OneLake probe tables and the DLFab metadata DB were deleted; local test DBs DLSync,
  DLMap, DuckLakeHR remain on box.
  - **STEP 1 OF THE USER-AGREED ORDER DONE 2026-10-27, COMMITTED `e1dbc95`** (order: (1) conflict retry + atomic commit FS,
    (2) opt-in marker IN the metadata DB so every process sees it — without opt-in SQL Server is a plain DuckLake
    metadata DB, no refusals, (3) automatic post-commit trigger that never fails the user's commit + DDL refusal in
    the catalog for opted-in tables). Whoever syncs catches up ALL snapshots; a lost race (`DeltaConflictException`)
    re-reads and continues, 32 attempts. Remote paths need a REAL conditional write: `DeltaCatalog.CredentialsFor`
    (registry of open Delta catalogs with a secret, longest root prefix) or the Fabric ambient credential, else
    REFUSED before IO. Docs §11.7, README. Gate verify_ducklake_delta_sync 117 -> 149 (§8 concurrentloop race,
    §9 refusal); mutant "no retry" dies at 419; mutant "no refusal" shows as SKIP (the runner skips HTTP errors;
    run-suites fails on skip). Hermetic 87/87 — 10983, floor bumped; service 73/73 — 5171, IDENTICAL to its floor. MEASURED LIVE on OneLake: 4 parallel
    duckdb.exe syncs, losers logged "lost a commit race", versions contiguous, file rows exact.
  - **⚠⚠ FOUND BY THAT LIVE RUN, NOT FIXED: INLINED ROWS ARE SILENTLY MISSING FROM THE MIRROR** (DuckLake 98 rows,
    Delta 75). DuckLake inlines small inserts/update post-images by default; the sync mirrors files only. The suite
    hid it with `DATA_INLINING_ROW_LIMIT 0`. Documented as a requirement for now; options put to the user (my
    recommendation: the step-2 opt-in also sets `data_inlining_row_limit 0` per table, sync refuses leftover inlined
    rows by name). Awaiting the user's choice.
  - **FLUSH RACES MEASURED 2026-10-27 (docs/ducklake-catalog.md §11.8): flush vs user = NO conflict (every
    variant, auto/explicit, both backends); flush vs flush = one non-retried DuckLake conflict per run, data
    exact.** ⚠⚠ THE REAL FINDING, NOT FIXED: a SQL Server metadata session attached BEFORE another process
    creates a DuckLake helper table (`ducklake_inlined_delete_<id>` on the first delete of a flushed row) READS
    WRONG DATA silently — deleted rows back, updated rows twice (11 vs 9 rows; SQLite 9/9). Cause: our schema
    entry treats a name missing from the ATTACH-time list as absent when no object filter is set
    (`fabricator_schema_entry.cpp:428`). Writes from the stale session were correct. Harness scratchpad
    `flushrace/` (DB `DLFlush` on box).
    - **✅ FIXED same day, COMMITTED `42198f1` (C++ only):** a table-name miss is fetched BY NAME always; the
      `HasObjectFilter`/`SetObjectFilter` flag is removed. Enumeration unchanged (refresh still needed for
      `duckdb_tables()`). Gates: NEW service suite `verify_ducklake_mssql` (32; two ATTACHes of one metadata
      schema = two processes) + `verify_exec_invalidate_cache` 21 -> 33 (out-of-band CREATE). Mutant (old early
      return) killed in both. ⚠ This changes a documented behaviour: an out-of-band created table is now
      reachable by name without `fabricator_refresh_cache`. Service floor needs MIN_SUITES 74 and +44 assertions
      once the tier confirms.
  - **DECIDED (user, 2026-10-27): inlining stays ON; NO auto-sync on commit** (it would flush per commit and
    undo inlining's batching; only meaningful with inlining 0, not built). docs/ducklake-catalog.md §11.5.
  - **✅ BUILT 2026-10-27, COMMITTED `e1dbc95` together with step 1 (C#-only): complete snapshots + a SQL-Server-readable mirror** (doc §11.9).
    The sync skips snapshots with live inlined rows (first sync = newest complete one); MEASURED: a flush stamps
    moved rows with their ORIGINAL snapshot, so a skipped snapshot becomes complete retroactively and still gets
    its own version. New options `column_mapping := 'none'` and `deletion_vectors := false` (persisted) → reader
    version 1; a live delete waits for `ducklake_rewrite_data_files(…, delete_threshold => 0)` (merge skips files
    with deletes; default threshold 0.95). SQL Server reads it via OPENROWSET on MinIO (verify_mssql_s3_polybase
    §6d, 268 -> 297; ⚠ needed `schemas 'onelevel'` on the cleanup ATTACH or the second run failed).
    verify_ducklake_delta_sync 149 -> 196. Three mutants (DV rule, completeness, mapping under 'none'); the
    completeness one survives the PolyBase suite by nature, dies hermetic. Tiers green: hermetic 87/87 — 11030,
    service 74/74 — 5244 (floors bumped). NEXT: maintenance procedure flush → rewrite → merge → sync →
    cleanup, then the opt-in marker for DDL refusal; optional: schema lookup by name (out-of-band schemas).
  - **EXPERIMENT (user: "interessant herauszufinden, ob das technisch geht"): SQL Server → PolyBase ODBC → DuckDB
    ODBC driver → DuckLake. docs/ducklake-catalog.md §12.** WORKS in WSL over pyodbc (ODBC side only, no PolyBase
    yet): our v2.0.0 linux loadable (built in WSL `~/sqlext2/build/odbc`, OVERRIDE_GIT_DESCRIBE=v2.0.0, ducklake
    block stripped from the config; script `scratchpad/odbc_wsl_build.sh`) + framework payload
    (`scratchpad/linuxpub`) in `~/odbcexp`, attach via `session_init_sql_file` in `~/.odbc.ini` (DSN ONLY — the
    driver refuses it in a connection string). ⚠⚠ The released 2.0.0.0 LINUX driver cannot run that file: its
    CMakeLists passes `-Bsymbolic` without `-Wl,` (gcc eats it as -B<dir>), so its internal SQLAllocHandle binds
    to unixODBC's (LD_DEBUG proved it). Patched build in `~/duckdb-odbc-src` (2 lines). Upstream issue not filed.
    Test DB `DLOdbc` on box. NEXT: PolyBase itself — Linux container with mssql-server-polybase, or the user's
    Windows server (official Windows driver should be fine; system DSN needs admin).
- **⚠⚠ POLYBASE → DuckDB ODBC → DuckLake WORKS (2026-10-27, docker rig, UNCOMMITTED; docs/ducklake-catalog.md §12).**
  `docker compose --profile polybase up -d sqlpoly` (port 1434, `docker/polybase/{Dockerfile,start.sh,stage.sh}`);
  EES started by start.sh (no systemd in a container). Needed 3 MORE duckdb-odbc fixes besides -Wl,-Bsymbolic
  (null indicator + row offset, chunk-vs-rowset indexing — both SIGSEGV in EES; SQL_C_NUMERIC 30.0 → 3.0, a SILENT
  wrong answer), all UPSTREAM since duckdb-odbc v2.0.0.1 (patch deleted 2026-11-02, stage.sh downloads the release); LOCATION 'lake.main.t' because PolyBase passes the
  first part as Database=. Ours: absence probes no longer log WARNING (Bootstrap.TableSchema, Debug now).
  ⚠ Stray file `D:	mp_patch_fetch.py` (my accidental copy) — the rm was blocked, delete by hand. Open: the
  user's Windows PolyBase server.
  - **2026-10-28, uncommitted (doc §12):** ~90% of a 1.7 s query is PolyBase reconnecting (~11×/query, ~4–5 new
    DuckDB instances each re-running the init); unixODBC pooling cannot help (EES uses a fresh env per connect).
    OURS FIXED: `g_host_db` raw pointer → `weak_ptr` (EES SIGSEGV in HostLogService after an instance teardown;
    hermetic 87/87 — 11030). No SQL Server connection leak from us (4 sessions over 60 queries). The "EES leaks
    DuckDB instances" finding is RETRACTED (doc §12): it was my own `Pooling=Yes` + `CPTimeout=300` in the
    container's odbcinst.ini (survives `docker restart`, gone on recreate). EES enables pooling itself; a parked
    connection keeps the whole DuckDB instance, reaped only inside a later connect. CPTimeout=30 reproduced the
    old curve to the thread and released it after the timeout. Never set CPTimeout for the DuckDB driver. Rig compose now has `cap_add SYS_PTRACE`
    and gdb installed by hand; DLPoly has extra test objects x_plain/duckplain, x_self/sqlself/sacred,
    dbo.self_src, and stage/odbc.ini has DSNs DuckPlain (threads=1) + SqlSelf (stage.sh regenerates odbc.ini).
- **⚠⚠⚠ HANDOFF 2026-10-27 (user: "prepare for compact to continue"). READ THIS FIRST.**
  - **Branch `feat/ducklake-catalog`, NOT pushed.** Committed today: `4864142` remote_pushdown default ON + the
    MARS-off decline + uncollatable types + scan-pushdown suites pinned to `remote_pushdown 'false'` + the
    `FABRICATOR_REMOTE_PUSHDOWN` env var and the 6-suite pushdown-off leg in run-suites; `e4a189e` service floor
    5018 (72 runs); `ccc68b3` known-limitations 1.30 (sql_variant read as varchar).
  - **UNCOMMITTED, DONE + GREEN: `sql_variant` ⇄ VARIANT** (entry below). Files: `Bridge/Conversion/
    SqlVariantAppender.cs` (new), `ColumnAppender.cs`, `SqlArrowMapping.cs`, `Common/VariantClrValue.cs` (new),
    `Common/ArrowDataReader.cs`, `SqlServer/SqlServerBackend.cs` (`MapArrowFieldToSqlType`), `test/
    verify_sql_variant_mssql.test` (new, 129, service), README type mapping, known-limitations 1.30 → FIXED.
    Gate standalone green from both a clean and a dirty DB; FIVE mutants each killed at its own row (43, 48, 148,
    166, 224). ⚠ The first mutant round was VOID — the suite lacked `fabricator_refresh_cache` after creating its
    table out of band and passed only because a leftover table existed; fixed.
  - **✅ DONE 2026-10-27: service tier 73/73 — 5147 (= 5018 + 129, nothing else moved), floors bumped in
    run-suites.sh (MIN_SUITES 73, MIN_ASSERTIONS 5147). Uncommitted. Only (3) commit and (4) hermetic remain.**
  - **✅ FABRIC SQL DATABASE AS DUCKLAKE METADATA HOST — MEASURED LIVE 2026-10-27** (docs/ducklake-catalog.md §10):
    EngineEdition 12 ⇒ not a warehouse, keys enforced; §6 sequence exact; 8×25 race 200/200 with 87 PK retries.
    FIXED (uncommitted): CREATE SCHEMA failed 33134 for the SP (no db user, principal 0) ⇒ `AUTHORIZATION [dbo]`
    when `DATABASE_PRINCIPAL_ID() = 0`. Needs `LOAD azure` for the `fabric_sp` secret. DB: ducklakemeta-08aadfd4…
  - **✅ HIDDEN ROWID INDEX — BUILT 2026-10-27, UNCOMMITTED:** `UNIQUE CLUSTERED` on `[__fabricator_rowid]` (NONCLUSTERED
    beside a CCI, none on a warehouse). 200k rows: rowid UPDATE of 2000 rows 115 s → 1.8 s, DELETE of 5000 6.7 → 0.46 s.
    verify_hidden_rowid 87 → 104, one mutant; service 73/73 — 5164, floor bumped. Existing tables deliberately ignored
    (user: not productive yet). Commits so far today also: `ab83c5b` sql_variant, `b0a1f86` Fabric SQL DB create-schema.
  - **NEXT, in order:** (1) run the FULL service tier on release-v2 (env block = Build & test copy-paste +
    `UNITTEST=build/release-v2/test/unittest.exe MANAGED=build/release-v2/extension/fabricator/fabricator`,
    run_in_background); expect 73 runs. ⚠ The read change touches EVERY sql_variant column (verify_remote_select_mssql
    §7 reads one) and `ArrowDataReader`'s last resort now THROWS instead of writing a type name — watch for a
    suite that relied on that. (2) Bump `MIN_SUITES` 72→73 and `MIN_ASSERTIONS` to the measured total (5018 +
    129 if nothing else moved) — only AFTER the tier finished. (3) Commit when the user asks (short message, no
    signature). (4) Optionally the hermetic tier (C# changes in Common/Bridge are shared).
  - Scratch: `scratchpad/mutants_sv.sh` (sql_variant mutants), `scratchpad/svprobe/` (SqlClient probe of
    GetValue/GetSqlValue and SqlBulkCopy base types). Probe tables `dbo.sv_probe`, `dbo.sv_bulk` exist in TestDB.
  - Still open, not requested: dynamic join filters for SQL Server (docs/duckdb-2.0-remote-execution.md §5 — gate
    must pin the path via EXPLAIN); DuckLake default schema main/dbo; live Fabric SQL Database test.

- **⚠⚠⚠ HANDOFF 2026-10-26 (end of session, user: "prepare for compact to continue"). READ THIS FIRST.**
  - **STATE:** the GitHub default branch is now **`v2.0-cyanoptera`** (switched 2026-10-26 with `gh repo edit`).
    2.0 is PUSHED at `5767252`. Its last two commits:
    - `12df4e9` moves the EW pin to upstream main `b4cf12c`. Our #523/#524/#525 were implemented upstream as
      #527/#528/#531.
    - `5767252` makes buffered Delta DML on a table without deletion vectors copy-on-write at COMMIT.

    Earlier on 2.0 the same day:
    - `9789d11`: default-branch docs plus `duckdb-main-canary.yml`.
    - `712eaae`: external Delta INSERT inside BEGIN (ExternalDeltaSession).
    - `67e60cd`: external DELETE without an identity column.

    Local tiers: hermetic **86/86 — 10765**, service **64/64 — 4357**.
  - **CI at the push:** `docs` 40367583299 GREEN; `extension` 40367583339 was still running at handoff, so CHECK IT
    FIRST: `export PATH="$PATH:/c/Program Files/GitHub CLI"; gh run view 40367583339 -R cmettler/fabricator-extension`.
    The nightly `integration` now runs on 2.0 (the default branch). 1.5 runs only by hand:
    `gh workflow run integration.yml --ref v1.5-variegata`.
  - **1.5:** `v0.0.17-duckdb1.5.6` is tagged. Its release is a DRAFT (4 ZIPs + 2 nupkgs); CI is green on every tier.
    Local tiers: hermetic 82/82 — 10142, service 61/61 — 3594.
    - Backported: BOOLEAN stats, rowid timestamp binding, Spark-named row ids, the CTE `fabricator_scan()` stub.
    - The EW repin is deliberately NOT on 1.5 (user: "opt 1"). EW #464 refuses differently shaped row groups, and
      1.5's codec variant transport shreds per batch, so OPTIMIZE over differently-shredded files would fail
      (`verify_delta_catalog_variant:233`, codec engine only).
    - Publishing the draft is the user's call.
  - **✅ BUILT LATER THE SAME DAY (uncommitted): external-table DELETE inside `BEGIN … COMMIT`** — same
    `ExternalDeltaSession` (now a generic `Run(root, leaf, txnId, write)` + `HasWrites`), `GuardExternalDml` gone,
    `SqlServerBackend.ExternalSession(txnId)`. Predicate DELETE always takes the scan route in a txn; identity-keyed
    DELETE refused once the session wrote the table; UPDATE stays refused (identity tables never buffer an UPDATE —
    `!SupportsExternalCommit`). Gates: external_txn 62 -> 111, external_delete 43 -> 45, s3_polybase 267 -> 268;
    hermetic 86/86 — 10765, service 64/64 — 4409; two mutants. Record: docs/create-table-with-options.md
    §DELETE inside an explicit transaction.
  - **VERSION 0.2.0 -> 0.2.1 (uncommitted, user-chosen)** because the contract grew since the 0.2.0 package
    (DmlSpec, IProvider/ITable DIMs). Packed locally and vendored into all four plugin repos (`nuget/`, property
    `FabricatorAbstractionsVersion` 0.2.1, uncommitted there too); offline tests 237/206/201/535 green; install on
    the republished 2.0 host shows no skew note. ⚠ `fabricator_version()` still says 0.2.0 until release-v2's C++
    is rebuilt.
  - **NEXT (proposed, NOT yet requested): external-table (PolyBase) DELETE/UPDATE inside `BEGIN … COMMIT`,** built
    on ExternalDeltaSession and EW's copy-on-write `StageRowDeletesAsync`. Design notes:
    - A DeleteWhere inside a transaction must take the rowid route through the session catalog. EW's predicate
      delete sees only the base snapshot.
    - Identity-keyed UPDATE/DELETE get their keys from SQL Server, which cannot see pending writes. Refuse them when
      the session already has writes on that table.
    - Deleting rows inserted in the same transaction stays refused on a table without deletion vectors.
  - **Open, not requested:**
    - An upstream issue for variant compaction over differently-shredded files (EW #464). Needs a pure-EW repro
      first.
    - Two failures from the DuckDB-main canary, for the 2.1 port: `verify_delta_nested_schemas` line 188 and a
      `verify_delta_row_tracking_virtual` crash inside libduckdb.
    - The rowid DELETE's native scan does not prune partitions.
    - EW change-feed follow-ups #529 and #530 are open upstream.
  - **Working tree:** clean apart from the usual untracked scaffold.
    - `build/release` holds the 1.5 payload and is shared with the plugin repos. `build/release-v2` is 2.0.
    - EW's local branch `wip/cow-staging` is an obsolete backup.

- **⚠⚠ DUCKLAKE ON A SQL SERVER METADATA CATALOG — `hidden_rowid` BUILT 2026-10-27, branch
  `feat/ducklake-catalog`, UNCOMMITTED (C#-only, no ABI).** `ATTACH 'ducklake:fabricator:<connstr>' AS lake
  (DATA_PATH …, METADATA_SCHEMA 'dbo', META_HIDDEN_ROWID true)` works with DuckLake's DEFAULT inlining incl.
  flush/merge/rewrite/expire/cleanup/CHECKPOINT, counts exact. A keyless created table gets
  `[__fabricator_rowid] BIGINT IDENTITY`, hidden from every schema (`ColumnsSchemaCore`), from `RETURNING`, and
  from every `SELECT *` catalog scan (`HiddenRowIdStream`) — the host then treats it as a VIRTUAL rowid.
  ⚠ HIDDEN, not visible: DuckLake commits with positional `INSERT INTO … VALUES` (no column list). ⚠ The
  `SELECT *` hiding was found by the gate: after ALTER ADD COLUMN the hidden column sits mid-row and the host
  maps describe columns BY POSITION → INTERNAL "Vector::Reference … different type". Gate `verify_hidden_rowid`
  (87, service, two mutants); service 65/65 — 4508 (= 4421 + 87). Record: docs/ducklake-catalog.md §6. CONCURRENCY MEASURED (doc §7): 8 parallel writer processes, box RC vs Fabric-like UTF8+RCSI vs read_isolation
  snapshot — IDENTICAL; conflicts are PK 2627 on ducklake_snapshot (retried), no deadlocks/timeouts; appends need
  `SET ducklake_max_retry_count=100` for 100%; DELETE/UPDATE losses are DuckLake's own non-retried conflict, matched
  by its SQLite backend. `isolation_level` scopes in-out sessions only BY DESIGN; the txn level comes from `read_isolation`, else
  the profile default (that run covered snapshot WRITES too). Open: live Fabric SQL
  database (EngineEdition must stay outside IsWarehouse), default schema main/dbo, Delta metadata generation.
  ⚠⚠ `remote_pushdown` DEFAULTS TO TRUE since 2026-10-27 (user decision; strict parse, 'false' opts out) — because (doc §9): without it DuckLake's latest-snapshot read
  (`WHERE snapshot_id = (SELECT MAX(…))`) FULL-scans `ducklake_snapshot` twice per commit — one row per commit, so
  78 → 335 ms/commit at 200k snapshots; flat 81 ms with pushdown, both races still correct.
  OPEN, recorded not built (user-agreed): dynamic join filters for SQL Server scans — needs filter_pushdown=true +
  supports_pushdown_type limited to EXACT-rendering column types; docs/duckdb-2.0-remote-execution.md §5.
- **⚠⚠ `sql_variant` ⇄ DuckDB `VARIANT` — BUILT 2026-10-27, branch `feat/ducklake-catalog` (C#-only, no ABI).**
  READ: `SqlArrowMapping` maps `sql_variant` → `VariantType` (arrow.parquet.variant, DuckDB 2.0 imports it),
  `SqlVariantAppender` encodes each `GetValue` result as its base type's variant primitive (after mssql-adbc's
  `VariantDecoders.cs`); a sub-µs timestamp uses the NANOS encoding. ⚠ `date` and `datetime2` both come back as
  `DateTime` (measured; `GetSqlValue` does not separate them either), so a date reads as a midnight timestamp.
  WRITE: `ArrowDataReader` decodes a variant column via `VariantClrValue` (Fabricator.Common); DDL maps VARIANT →
  `SQL_VARIANT` (`MapArrowFieldToSqlType`; refused on a warehouse). Exact or refused, naming the column:
  object/array (→ `v::JSON`), string > 4000 chars, binary > 8000, and a timestamp/date that legacy `datetime`
  cannot hold exactly. ⚠⚠ MEASURED SqlBulkCopy behaviour that forced those refusals: `DateTime` → `datetime`
  (silently rounds `.1234567` to `.123`, fails < 1753), string > 4000 → `varchar` (code page), `DateOnly` BREAKS
  the TDS stream, `sbyte` refused; `DateTimeOffset` → datetimeoffset(7) and `TimeSpan` → time(7) are exact.
  ⚠⚠ FOUND ON THE WAY, SILENT: `ArrowDataReader.ValueAt`'s last resort RETURNED THE ARRAY'S TYPE NAME as the value,
  so an INSERT of a VARIANT into sql_variant stored the text `StructArray`. It throws now. Gate
  `verify_sql_variant_mssql` (128, service). known-limitations 1.30 marked fixed; README §Type mapping.
- **⚠⚠ BIND-TIME DESCRIBE CACHE + OUT-OF-BAND TYPE GUARD — BUILT 2026-10-27, branch `feat/ducklake-catalog`,
  UNCOMMITTED (C++ + C#, no ABI bump: one additive capability key).** A table entry reuses its FIRST
  `SELECT * … WHERE 1 = 0` describe for later binds when the provider declares `bind_schema_cacheable` (SQL Server
  only — the Delta describe seeds the statement's snapshot pin, `verify_delta_autocommit_pin`); never for an AT
  reference; kill switch `fabricator_bind_schema_cache`. DuckLake: describes 1264 → 49, statements 26 → 16 per op,
  7.3 → 6.35 s locally. ⚠ The cache lives on the ENTRY, which every DDL/ROLLBACK/refresh REPLACES, so it can be
  no staler than the column list. ⚠ Out-of-band DDL (another process) was ALREADY unsafe: a retyped (and, without
  the cache, a dropped) column hit `Vector::Reference` → INTERNAL error → DATABASE INVALIDATED. `ArrowStreamScan`
  now throws a clean IOException on a source/output type mismatch; the rollback drops entries, so the next
  statement heals. Gate `verify_bind_schema_cache` (85, service, three mutants). Record: docs/ducklake-catalog.md §8.
- **⚠⚠ NESTED SCHEMAS FOR EVERY OBJECT — ABI v97, BUILT 2026-10-26, COMMITTED `a39cad8` with VERSION 0.2.2, NOT PUSHED (user: "wir benötigen den support für
  alles, also auch macros … rückwärtskompatibilität egal").** Functions/macros/views register at their PATH; scoped
  invalidation matches `a.b.name`. Gate `verify_delta_nested_schemas` §12 (94 -> 143, three mutants);
  hermetic 86/86 — 10834, service 64/64 — 4421 (= its floor: SQL Server unmoved). Two UPSTREAM
  2.0 bugs found + worked around: a view body binds against the LEAF schema (wrong rows), and a nested CTAS whose
  leaf names an existing table creates it EMPTY (docs/duckdb-upstream-issues.md §12/§13, not filed). NEXT: the four
  plugin repos (retype script `scratchpad/retype_function_members.py`, typed GetFunctions/GetMacros/GetViews,
  new Abstractions version + re-vendor) — DONE, UNCOMMITTED in all four: 0.2.2 vendored, function classes got
  `SchemaPath => SchemaName`, catalogs return the typed lists and resolve paths through a `Flat()` that refuses depth
  != 1; offline 237/206/201/535 and host suites 65/21/26/21-of-22 (grist :181 pre-existing), all unchanged. NEXT:
  phase 2 (leaf-form TABLE members -> path-only, managed-only).
- **⚠⚠⚠ HANDOFF 2026-10-24 (end of session, user-directed: "push and prepare for compact to continue in your
  given order"). READ THIS FIRST.**
  - **STATE:** `v2.0-cyanoptera` PUSHED at `c977c21` (12 commits: id-mode batched reads, bloom on mapped
    tables, the remote-execution survey, ABI v93 DELETE + v94 UPDATE predicate shortcut on SQL Server, the
    rowid timestamp binding fix). Local tiers: hermetic **83/83 — 10333**, service **61/61 — 3792**.
  - **CI IN FLIGHT at the push — CHECK FIRST:** `extension` run `40127519206` (hermetic on 3 platforms),
    `installer-core` `40127519263`, and the DISPATCHED `integration` run `40127563274` (service tier, linux).
    `docs` passed. ⚠ The C++ added in v93/v94 has only ever compiled on MSVC — the C++11 brace-init and
    macOS-symbol traps (see Build & test) are exactly what linux/osx CI catches.
    `export PATH="$PATH:/c/Program Files/GitHub CLI"; gh run view <id> -R cmettler/fabricator-extension`.
  - **CI on `c977c21`: `extension` GREEN on all 3 platforms (the v93/v94 C++ compiles on gcc/clang),
    `installer-core` GREEN, `integration` RED BEFORE ANY SUITE — `quay.io/minio/minio` unauthorized. ⚠ The
    bitnamilegacy MinIO switch (`cc1b2c1`, `aa0efeb`) was ONLY EVER ON `v1.5-variegata`; it is now applied to
    the 2.0 working tree (`git cherry-pick -n`, STAGED, NOT committed). Commit + push, then re-dispatch
    integration.
  - **⚠⚠ ROOT-CAUSED (2026-10-25): the 2.0 `integration` tier never passed in CI** — every SQL Server connect
    failed `SSL Provider, error: 31` (run `40132894343`), same compose green on 1.5. CAUSE, measured in a WSL 2.0
    build (`~/sqlext2`, configured like CI): DuckDB 2.0 links unittest against a SHARED `libduckdb.so`, and the
    vcpkg OpenSSL httpfs links statically is EXPORTED from it (536 SSL_*; the 1.5 static unittest exported 0).
    .NET's system `libssl.so.3` then binds its libcrypto references through the global scope — LD_DEBUG: 495 to
    libduckdb.so, 0 to libcrypto.so.3 — i.e. system libssl 3.0 over vcpkg libcrypto 3.6.x. Locally (vcpkg 3.6.3)
    the mix happens to work, so the failure is CI's 3.6.5 (that version link is inferred, not reproduced).
    FIX in CMakeLists.txt: `-Wl,--exclude-libs,libssl.a:libcrypto.a` on the `duckdb` target, Linux only,
    applied with `cmake_language(DEFER …)` because DuckDB adds extension dirs BEFORE src/ (an immediate
    `if(TARGET duckdb)` was silently false). After: 0 exports, 495 bindings to libcrypto.so.3; WSL suites
    cardinality / s3 222 / polybase 263 / remote_select 88 green. Committed `9f6416d`; integration run
    `40180400721` GREEN, 62/62 — 3880 (identical to the local Windows tier) — the FIRST green 2.0 service tier
    in CI; extension green on all 3 platforms. ⚠ It does not affect shipped loadables (they never link
    libduckdb.so). Control: v1.5 integration `40141419848` green the same night (60/60 — 3572).
  - **THE ORDER (user-agreed):**
    1. ~~push + CI~~ (done; see the line above).
    2. ~~**Delta `get_partition_stats`**~~ — BUILT 2026-10-24, UNCOMMITTED, ABI **v95** `table_partition_stats`.
       COUNT(*)/MIN/MAX answered from the log (EXPLAIN: `Column Data Scan`, no `Fabricator Scan`); kill switch
       `fabricator_partition_stats`. Gate `verify_delta_partition_stats` (130, engine-doubled); hermetic
       **85/85 — 10593** (= 10333 + 260 exactly). Full record: docs/duckdb-2.0-remote-execution.md §3.1.
       ⚠ THE TIER FOUND WHAT THE GATE DID NOT: a metadata answer is still a READ — without recording a
       whole-table read in an explicit txn, `INSERT INTO t SELECT max(id)+1 FROM t` committed as a BLIND
       APPEND (`verify_delta_catalog_transactions` §42a). Fixed by calling ScanCore's exact read-set + PinVersion.
       ⚠ PRE-EXISTING, NOT FIXED, worth a look: in an explicit txn the scan of a table first touched later
       reads as of the txn's first-read INSTANT — unless that statement is the entry's FIRST bind, whose
       SchemaProbe seeds the pin at LATEST. So mid-transaction visibility of another commit depends on
       catalog-entry state (measured, switch off).
       Service tier **61/61 — 3792**, IDENTICAL to its floor (the callback is on every provider's scan;
       SQL Server answers "no statistics", so nothing moved — the behaviour-neutrality claim).
    3. ~~**Delta predicate DELETE**~~ — ✅ BUILT 2026-10-25 (no ABI change; doc §2.6; gate `verify_delta_delete_where`
       106, three mutants). EW bumped first as its own commit `5076cca` (987a0ce → e598d1d, six commits, zero patches;
       hermetic 85/85 — 10634 and service 62/62 — 4248 IDENTICAL; EW DeltaLake.Tests 609/609, Table.Tests 1496 passed /
       126 skipped on net10.0). Shortcut only where EW wins: partition-column predicates (files removed UNREAD), or any
       column under ATTACH `delete_trusts_statistics true` on a DV table; never with CDF, never in BEGIN…COMMIT. Three
       additive `table_dml_info` keys (`delete_where_autocommit_only`, `delete_where_columns`, `exact_strings`) via an
       INTERNAL `IDeleteWhereShape`. ⚠⚠ Delta's WHERE is ERASED into the scan (`table_filters`), which the SQL-Server-era
       `FindExactScan` refused — the first build never fired; table filters are now turned back into expressions
       (`TableFilter::ToExpression`). ⚠ Observed, NOT fixed: the rowid DELETE's native scan does not prune partitions
       by its own filter (it read partition a's file for `p = 'c'`).
       Earlier text of this item:
       **Delta predicate DELETE via EW `DeltaTransaction.DeleteAsync(Expressions.Predicate)`** — ✅ UNBLOCKED:
       **#506 was IMPLEMENTED and closed 2026-10-25** (PRs #509/#510/#512, commits 146783c/abf2ba8/c66712b/
       65144c0/8fc3345; follow-up issue #511). Needs an EW bump first — run the recorded sweep (public-surface diff
       of the option records, `known-issues.md`), not just a subject read. Was: wait for the
       maintainers on **clast-project/engineered-wood#506** (opened today; offered a stage-1 PR). Our C++ side
       is the existing plan-less PlanDelete; `DeltaFilterBuilder` already builds the EW predicate. Explicit
       transactions with pending writes on the table must keep the rowid path (EW sees the BASE snapshot).
    4. ~~**SELECT pushdown spike for SQL Server**~~ — BUILT 2026-10-24, committed + pushed as `1d14bd2`,
       `remote_pushdown 'true'` ATTACH option, off by default, no ABI change (capability doc `remote_select`).
       `src/catalog/fabricator_remote_select.cpp`. Single-table count/min/max/integer+decimal sum + GROUP BY +
       exact-type WHERE runs as ONE T-SQL statement via fabricator_query, cast back to DuckDB types/names. Gate
       `verify_remote_select_mssql` (88, service), four mutants killed. Full record: doc §4.1.
       ⚠ The smoke caught a WRONG ANSWER first: DuckDB's "strict" DECIMAL->INTEGER cast ROUNDS, so `id > 1.5`
       went out as `[id] > 2`; constants now need a lossless ROUND TRIP. ⚠ A query in `FROM (DESCRIBE …)` never
       reaches the remote optimizer — a DESCRIBE-based type check was vacuous; compare CTAS tables instead.
       ⚠ DuckDB pushes set-op branches and CTAS queries on their own, but NOT a subquery joined to a local table.
       Earlier commits this session: `b89c6ba` minio bitnamilegacy on 2.0, `6d6c575` v95 partition stats (pushed).
       **EXTENDED 2026-10-25 (doc §4.2): ORDER BY/LIMIT, HAVING, INNER/LEFT joins within the catalog.** One typer
       (scopes [t0..], RenderValue, RenderPredicate). NULL order rendered as a CASE rank (T-SQL puts NULLs first
       for ASC) — under LIMIT the server would otherwise return a different SET of rows. Order applied on the
       server AND re-sorted locally via hidden key columns (that re-sort's mutant SURVIVES: fabricator_query keeps
       server order). Gate 88 -> 262; service floor 4054. Committed `44f29e3`; CI integration 62/62 — 4054 and
       extension (3 platforms) GREEN.
       **EXTENDED AGAIN 2026-10-25 (doc §4.3): exact string =/<>/GROUP BY/DISTINCT/count(DISTINCT)/joins via
       `(s + N'|') COLLATE Latin1_General_100_BIN2` (binary collation + sentinel against ANSI padding); string
       ORDER BY/min/max stay local (not exact on 2008+). DISTINCT = GROUP BY. Integer + - * typed as DuckDB (widest
       column operand), computed BIGINT — intermediate overflow is error-vs-answer, pinned. Gate 262 -> 373, floor 4165.
  - **SUBSTRAIT PUSHDOWN INTEGRATED (2026-10-25, ABI v96, docs/duckdb-2.0-remote-execution.md §3.2–§3.12):** vendored
    to_substrait (third_party/duckdb-substrait, ported to 2.0) + the mssql-adbc T-SQL emitter
    (dotnet/Fabricator.SqlServer/Substrait). Windows, exact string keys + an indexable prefilter, extract. The
    differentials found TEN wrong-answer classes (lower/upper, UTF-16 positions, reverse, regexp, binary trailing
    zeros, float→int cast, decimal scale loss, float /0, any_value, upstream rowid/TIME bugs) — all declined or
    fixed; §3.12 has the table. Gate verify_remote_select_mssql 439. **User decision 2026-10-25: Substrait is THE
    path** — the hand-written renderer is deleted, the build option is gone, and protobuf + substrait-protobuf
    come from deps/substrait/vcpkg.json (CI: .github/actions/build-extension, cached; locally into
    build/substrait-deps, see the quickstart).
  - **⚠⚠ EVERY FRESH RESTORE IS BROKEN RIGHT NOW (found 2026-10-25, NOT ours): NU1605 in engineered-wood.** EW
    references `System.Text.Json` as the FLOATING `9.*` and pins `System.Threading.Tasks.Extensions 4.6.0`; nuget.org
    now has STJ 9.0.22 but NOT `System.IO.Pipelines 9.0.22` (404), so NuGet takes Pipelines 10.0.0, which needs
    Tasks.Extensions >= 4.6.3 — a downgrade error on EW's netstandard2.0 target (which we never consume). Fails
    standalone too (`dotnet restore engineered-wood/src/EngineeredWood.DeltaLake`), so CI breaks as well.
    Likely transient (a half-published .NET servicing release). LOCAL workaround only, nothing committed: a
    temporary `dotnet/Directory.Build.rsp` holding `"-p:WarningsAsErrors="` and `-p:TreatWarningsAsErrors=false`
    around publish-managed.ps1, deleted right after. (An env var and `-p:WarningsNotAsErrors=NU1605` do NOT work:
    EW sets TreatWarningsAsErrors and the SDK lists NU1605 in WarningsAsErrors.) Worth an upstream note: floating
    `9.*` + a pinned transitive is fragile by construction.
  - **✅ FIXED 2026-10-25 (C++-only, no ABI): a zero-argument `fabricator_scan()` STUB now sits in the global
    `fabricator_scan` set and carries the catalog scan's serialize/deserialize pair; the deserializer swaps in the
    real scan via `FabricatorTableEntry::RebuildScanFunction`, WITH the AT clause (mutant without it: VERSION => 1
    silently returned LATEST rows). A direct `fabricator_scan()` call is refused at bind. Gate
    `verify_delta_subplan_dedup` §7 (36 -> 77), two mutants killed. ⚠ Only the ORDER BY … LIMIT shape is inlined —
    a `count(*)` self-join stays a materialized CTE and never copies, so §7 asserts the inlined plan via EXPLAIN.
    The original finding:
  - **⚠ PRE-EXISTING: a CTE over a fabricator table fails when DuckDB INLINES it at two places**
    (found 2026-10-25, with or without remote_pushdown; measured on ArrowTest.dbo.cte_demo):
    `WITH x AS (SELECT * FROM db.dbo.t) SELECT … FROM x a JOIN x b ON … ORDER BY 1 LIMIT 2` → *"Serialization
    Error: Failed to find function system.main.fabricator_scan()"*. The same with `AS NOT MATERIALIZED` fails;
    without ORDER BY/LIMIT (DuckDB materializes) and with `AS MATERIALIZED` it works; referenced once it works;
    over a local table it works. ⇒ inlining COPIES the CTE plan by serialize/deserialize, and deserializing our
    scan looks the function up by name — the global `fabricator_scan(VARCHAR)` matches no zero-argument catalog
    scan. Fix direction: serialize/deserialize callbacks on the catalog scan's TableFunction. Ungated.
  - **⚠⚠ A SCALAR-SUBQUERY COMPARISON IS NOT A PUSHDOWN DIFFERENTIAL (found 2026-10-25):** `SELECT (SELECT … FROM rp…)
    = (SELECT … FROM db…)` runs BOTH sides locally (the remote optimizer pushes whole nodes only), and its EXPLAIN
    row still shows `Fabricator Query` because EXPLAIN does not execute. Ten rows of verify_remote_select_mssql
    were vacuous until a producer mutant survived them. Write differentials as `(rp) EXCEPT ALL (db)` both ways —
    set-operation branches are pushed one by one.
  - **⚠⚠ SILENT WRONG RESULT FOUND + FIXED 2026-10-26 (known-limitations ~~1.29~~): the native writer recorded every
    BOOLEAN's maxValues as `false`** — DuckDB's RETURN_STATS says `0`/`1`, `WriteStat` compared with "true". So
    `WHERE flag = true` pruned matching files away (0 instead of 15, both engines). Pre-existing (measured on the old
    EW pin too). EXISTING tables keep the wrong bound until rewritten; ⚠ OPTIMIZE leaves a lone file untouched
    (measured) — `UPDATE t SET flag = flag` heals it. Gate `verify_delta_statistics` §9 (34 -> 45). Found while
    probing narrow integer types for the Delta DELETE shortcut (SMALLINT/TINYINT/BIGINT literals were fine).
  - **✅ BUILT 2026-10-26 (uncommitted at the time of writing): (1) DELETE on a PolyBase external Delta table WITHOUT
    identity** — gate `verify_mssql_external_delete` (43, service), hermetic 86/86 — 10751 and service 63/63 — 4291
    (= 4248 + 43). ⚠ The scan's pushed filter is a SUPERSET: without the exact re-evaluation `val > 5 AND name <> 'n1'`
    deleted 20 rows instead of 5 (the mutant). ⚠ The external table reads a Delta partition column as NULL (MS
    limitation) — pinned. Record: docs/create-table-with-options.md §DELETE without an identity column. Was: code written
    (SqlServerBackend.DeleteWhereCore → ExternalTableRouting.DeleteWhere: partition predicate → EW unread; else scan +
    EW row evaluator + the delta rowid DELETE, CoW keeps protocol 1.0), suite `verify_mssql_external_delete` parked in
    the scratchpad until the boolean commit is in; (2) then explicit-transaction support for external DML, option 3
    (defer the Delta commit to DuckDB COMMIT; no read-your-writes on the external table; refuse DELETE/UPDATE after a
    pending INSERT on it; not atomic with SQL Server's own changes).
  - **✅ BUILT 2026-10-26: option 3, NARROWED to INSERT.** Inside BEGIN..COMMIT an INSERT into an external DELTA
    table writes its files at statement time and commits at COMMIT (`ExternalDeltaSession`, one transient delta
    catalog per root in an explicit Delta txn, owned by `SqlServerTransaction.External`; EndTransaction commits Delta
    FIRST, then SQL Server). ⚠ DELETE/UPDATE could NOT follow: MEASURED, a buffered DELETE on a `deletion_vectors
    false` table refuses (`EnsureBufferedDmlEligible`) — buffering stages deletes only as DVs, and an external table
    must never carry one (19726). Lifting it needs a copy-on-write STAGING verb in EW; issue text drafted in the
    scratchpad (`ew_issue_cow_staging.md`), NOT posted. Parquet INSERT and CETAS stay autocommit-only. Gate
    `verify_mssql_external_txn` (62, service, two mutants); `verify_mssql_s3_polybase` 263 -> 267 (its old refusal
    row now asserts the ROLLBACK; parquet refusal added). Service floor 4291 -> 4357, MIN_SUITES 63 -> 64.
  - **EW COPY-ON-WRITE STAGING — DONE (2026-10-26), committed `12df4e9` + `5767252`, not pushed.** The maintainers
    implemented all three issues themselves (#527 same-file second stage refused, #528 DV staging / bornDeleted
    refused without DVs, #531 `StageRowDeletesAsync(selection, RowDeleteMode, ct)` — the signature we proposed). EW
    pin -> upstream main `b4cf12c` (9 commits; sweep: no option-record change; behaviour: NOT NULL enforced against
    the table schema #514, partition-moving UPDATE #519, missing numRecords = unknown #516, view types #515). Our
    flush stages CopyOnWrite when the table has no DVs; deleting a same-txn row there stays refused. Hermetic
    86/86 — 10765, service 64/64 — 4357, EW DeltaLake.Tests 616/616, Table.Tests 1595 + 126 skipped. Limitations
    1.12/1.13 marked fixed. Local EW branch `wip/cow-staging` (our own version) is now only a backup. NEW upstream
    follow-ups: #529 (feed misread for a DV commit without change rows), #530 (a fused commit needs change rows for
    every data change once any are staged) — we stage change rows for every buffered DELETE/INSERT already.
    NEXT: external-table DELETE/UPDATE inside BEGIN on top of this (ExternalDeltaSession).
  - **1.5 BACKPORT (2026-10-26), pushed as `1142a65` on v1.5-variegata, version 0.0.17; tag after CI.** Four fixes,
    each MEASURED on unchanged 1.5.6 first: BOOLEAN stats (`54e00b4`, 0 of 5 rows), rowid timestamps (`93e8f6f`, new
    suite `verify_rowid_timestamps_mssql` — the 2.0 test lives in a 2.0-only suite), <!-- check-docs:ignore (a 1.5-only suite) --> Spark-named row ids (`9a7b9fe`,
    adapted to 1.5's union_by_name full form, mutant killed in §13e), inlined CTE (`83916ff`, on 1.5 it was an
    INTERNAL Error; ported to 1.5 APIs). Hermetic 82/82 — 10142, service 61/61 — 3594.
    - ⚠⚠ `261f47a` (truncated string max) was NOT backported and its 2.0 commit message is WRONG: it is a DuckDB 2.0
      REGRESSION, not pre-existing. 1.5.6 rounds a truncated max up (TryTruncateMax increments the last kept byte;
      measured 256 chars ending 'b', a valid upper bound). 1.5 now pins that in verify_delta_statistics §3.
    - ⚠⚠ The EW repin (e515a2b -> e598d1d) was dropped for 1.5: EW #464 refuses a row group whose shape differs from
      the file's, and 1.5's codec variant transport shreds PER BATCH, so OPTIMIZE over differently-shredded files fails
      (`verify_delta_catalog_variant` line 233, codec engine only). EW's compaction never reassembles/re-shreds; the
      old pin wrote such batches UNCHECKED (correctness of those files unverified). Upstream issue still to file after
      a pure-EW repro.
    - Since the default-branch switch, 1.5 gets NO nightly service tier — dispatch `integration` by hand.
  - **DuckDB main canary (`.github/workflows/duckdb-main-canary.yml`, weekly):** first run compiled against main (2.1)
    and failed 2 of 86 hermetic suites — `verify_delta_nested_schemas` (assertion at line 188) and
    `verify_delta_row_tracking_virtual` (crash in libduckdb). Early warning for the 2.1 port; not chased yet.
  - Loose ends: measure EW #493 on our same-txn insert-then-delete path; admit TIMESTAMP SET values in the UPDATE
    shortcut now that rowid binding is column-typed.
  - Build/test commands for the 2.0 tree: see the 2.0 HANDOFF entry below (`build_v2_all.bat`,
    `publish-managed.ps1 -ExtensionDir …release-v2…`, `UNITTEST=… MANAGED=… bash scripts/run-suites.sh`).
    Service tier env: the copy-paste block under Build & test, plus `UNITTEST`/`MANAGED` for release-v2.

- **⚠⚠ THE MinIO IMAGE IS GONE FROM EVERY FREE CHANNEL — the service tier was DEAD IN CI from 2026-10-17,
  and the rig now runs on `bitnamilegacy/minio` (user decision 2026-10-20, "erstmal"). This is the SECOND
  time the publisher withdrew it, so treat the REGISTRY as the fragile part of this rig, not the software.**
  - **MEASURED, all of it, not inferred:** `quay.io/minio/minio` answers *"unauthorized"* — and the quay
    **tag listing itself** now demands authentication, so the repository is closed rather than a tag being
    gone; `minio/minio` on Docker Hub denies access; **`dl.min.io` returns `410 Gone` for the raw `minio`
    AND `mc` binaries.** MinIO no longer distributes a community build in any form.
  - **⚠⚠ THE ANSWER TO "IS THERE A NEWER ONE" IS NO, AND IT IS THE OTHER WAY ROUND: the image we had
    CACHED (`RELEASE.2025-09-07`) IS NEWER THAN ANYTHING STILL OBTAINABLE.** The best free source is
    Bitnami's frozen mirror at **2025.7.23**, roughly six weeks older. ⇒ the local cache was, for a while,
    the only copy in existence here — worth a `docker save` if that state ever matters again.
  - **WHAT WAS CHANGED:** `bitnamilegacy/minio:2025.7.23-debian-12-r5`, **pinned to an exact tag** (a moving
    `latest` on a FROZEN mirror is the worst of both worlds). Three Bitnami conventions differ and all three
    matter: NON-ROOT (uid 1001), data at `/bitnami/minio/data`, certs at `/certs`. **The `createbucket`
    service is GONE**, replaced by `MINIO_DEFAULT_BUCKETS` — which also halves the exposure, since the rig
    now depends on ONE unobtainable image instead of two.
  - **⚠⚠ TWO SILENT-FAILURE TRAPS, both found by reading the image's own scripts rather than by running it:**
    - `MINIO_DEFAULT_BUCKETS` creates the bucket with the bundled **`mc`, over HTTPS, against our own
      SELF-SIGNED cert**. With no trust anchor the container comes up **healthy** and the bucket is simply
      ABSENT. The image's `setup.sh` copies `${MINIO_CERTS_DIR}/CAs/` into mc's trust store, so
      `generate-certs.ps1` now writes `certs/CAs/public.crt` as well. ⚠ **That copy is load-bearing and the
      certs are GITIGNORED**, so it only exists if the generator makes it — i.e. exactly the clean-machine
      path CI takes.
    - `MINIO_SCHEME=https` is required, or the internal client speaks http to an https server.
  - **⚠ THE VOLUME NEEDED `chown -R 1001:1001`** (the old image ran as root). Kept the DATA deliberately
    rather than starting a fresh volume: changing the image and the data at once would have made a green
    result unattributable. ⚠ A fresh volume is still the tidier long-term move, but it triggers the recorded
    rule — emptying the bucket ORPHANS SQL Server external tables and the next full-catalog ENUMERATION
    suite dies on them, so the two halves must be done together.
  - **GATES:** `verify_delta_catalog_s3` **196 × 2 engine legs**, identical to the pre-switch count, and TLS
    verified from the host against our CA **without `-k`** — the property SQL Server depends on.
  - ⚠ The same dead images are cached for OTHER projects on this box (`fabricator-grist` and older rigs use
    `minio/minio` + `minio/mc`), so they carry the identical latent breakage.
  - **⚠⚠ `quay.io/minio/aistor/minio` IS PULLABLE AND IS A TRAP — MEASURED 2026-10-20 (user-asked, and it is
    the obvious next idea, which is why it is written down).** It pulls without credentials and is CURRENT
    (`RELEASE.2026-09-19`, a month old — far newer than our cache and than Bitnami's mirror), so it looks
    like the answer. **Run it and it prints: *"No valid license found, running in offline mode. All S3
    operations are denied."*** The container STARTS and reports itself up; every S3 operation then fails.
    ⇒ a switch would have produced a healthy-looking rig in which bucket creation and then every suite
    failed with access errors and nothing anywhere said "licence". AIStor is MinIO's COMMERCIAL product;
    whether any free or evaluation licence covers a CI rig is a commercial question, not a technical one.
    ⇒ do not re-test this image — test the LICENCE question first if it ever comes up again.

- **⚠⚠⚠ NESTED SCHEMAS ARE DONE — SLICES 0/1/2a/2b/2c (2026-10-23). A Delta root attached
  `schemas 'nested'` (was `nested_schemas true` — see the rename below) resolves `cat.a.b.t` to any
  depth, for READS AND WRITES. ABI v92. Full record:
  [docs/nested-schemas.md](docs/nested-schemas.md) §Slice 2b and §Slice 2c.**
  - **⚠⚠ RENAMED 2026-10-24 (user-proposed): `schemas` + `nested_schemas` (two booleans) -> ONE option
    `schemas 'none' | 'onelevel' | 'nested'`.** Four combinations meant three states, and both-true
    silently meant nested. `true`/`false` stay PERMANENT spellings of 'onelevel'/'none' (every existing
    two-level ATTACH). `nested_schemas` is REFUSED BY NAME, no alias — ATTACH options are free-form, so a
    leftover one would attach flat and lose every nested table silently; it shipped only in the
    unpublished v0.2.0 draft. ⚠ An unknown value now THROWS: the old boolean read took anything but
    "true" as false, so `schemas 'nested'` against it attached FLAT with no message. C#-only, no ABI.
    Gate `verify_delta_nested_schemas` 79 -> 94 (§11), one mutant (unknown value read as 'none') killed
    at the unknown-value row. 'onelevel' is NOT redundant with 'nested' at depth 1: 'nested' declares
    `supports_nested_schemas` (changes dotted-name resolution) and walks the whole tree on an object
    store where 'onelevel' globs exactly two levels.
  - **STATE: branch `v2.0-cyanoptera`, tip `6a77a04`. Hermetic 83/83 — 10196, service 60/60 — 3593.**
    Gates: `verify_delta_nested_schemas` (79) and `verify_with_options` (234). ⚠ The SERVICE count is
    IDENTICAL to the pre-slice one, which is the behaviour-neutrality claim that matters most for v92:
    it widened EVERY provider's write path, and SQL Server — which nests nothing and takes the
    leaf-returning DIM defaults throughout — moved by not one assertion.
  - **⚠⚠ FIVE DEFECTS ACROSS THE TWO SLICES, AND FOUR WERE SILENT OR MISLEADING. The two general
    lessons:**
    - **READ THE CALLEE, not where the crossing rides.** ALTER was exempted from 2b's refusal because it
      travels on the path-carrying table session — the ENTRY does; the managed handler then passed the
      definition's LEAF and the provider re-derived the wrong directory.
    - **A DIM DEFAULT IS A SILENT FALLBACK.** `DeltaTableDefinition` carried a schema path and did not
      OVERRIDE `ITable.SchemaPath`, so ALTER alone kept using `[SchemaName]`. Every other statement on a
      depth-2 table worked and ALTER reported *"No Delta table found"* — which reads as a missing table,
      not a wrong path. Overriding a path-taking METHOD without the path PROPERTY leaves exactly one
      crossing wrong.
    - The other three: `FabricatorCatalog::LookupSchema` never WALKED the path (a straight
      `schemas_.find()` answers only the first level — ⚠ **a two-level layout structurally cannot catch
      this**, which is why slices 1 and 2a were green with it missing); 2a's `SCHEMA_ENTRY` branch sat
      BELOW the `!transaction.context` guard; and the new path overload of `TablePath` broke the FLAT
      layout, because since v91 the host calls `GetTable(path, …)` for EVERY table.
  - **⚠⚠ A FIVE-LINE SMOKE TEST KILLED THE FIRST DESIGN.** Root-level tables were going to get a
    synthetic schema `main`, but `CREATE TABLE cat.main.t` under `schemas true` makes a REAL
    `<root>/main/` folder — so the commonest existing layout would have collided on the first ATTACH.
    Every segment is a real directory now, and a root-level table is SKIPPED with a warning (`schemas
    true` does not discover one either).
  - **⚠⚠ THE OBJECT-STORE LEG IS MEASURED NOW (2026-10-24), and this entry used to say it was not.**
    `verify_delta_catalog_s3` grew a nested section (199 -> **222**, engine-doubled ⇒ service floor
    3593 -> **3639**): discovery, a depth-3 read, and INSERT/DELETE at depth 2, on BOTH engine legs,
    with the write read back through a sub-root attached FLAT as the control that it landed where the
    path says. The depth-3 row can only pass if ONE `**` glob spans two levels against a real object
    store, which is the thing httpfs might not have done. ⚠ **AZURE is still unmeasured** — a third glob
    implementation, and no CI tier reaches a live ADLS account. Depth is capped at 16 and REFUSES rather
    than truncating.
  - ⚠ `COMMENT ON` cannot name a nested table at all — DuckDB's PARSER caps it at three qualifications.
    The gate pins the parser's error, not ours, so it fails the day upstream lifts the cap.
  - **⚠⚠ SLICE 3 (DAX per-model `system`) IS RETRACTED, NOT DEFERRED — ITS PREMISE WAS FALSE (2026-10-24).**
    docs/nested-schemas.md §3 claimed *"two attached semantic models share ONE `cat.system`"*. They do
    not: a `DaxCatalog` is scoped to ONE Analysis Services database (`_catalog` + `ChangeDatabase`),
    which holds ONE Tabular model (`DiscoverModelNames()[0]` over `$SYSTEM.TMSCHEMA_MODEL`), so two
    attached models are two DuckDB CATALOGS and cannot collide. ⚠ **The claim was read off the code's
    own comment ("the model name(s)") while the line beside it takes a single element** — the same
    comment-vs-code trap this file already records. ⚠ READ, NOT MEASURED: `verify_dax` is the manual
    tier. **The DAX case that IS real: a WORKSPACE XMLA endpoint exposes many databases and
    `DiscoverDefaultCatalog` takes the FIRST, so one ATTACH per dataset is required today**; nesting
    would give `cat.<dataset>.<table>` — but the catalog would then span databases, so the connection,
    every scan and the function registrations all gain a dimension. Size it before promising it.

- **⚠⚠ TABLE OPTIONS IN DDL — `ALTER TABLE … SET (…) / RESET (…)` (BUILT 2026-10-23, user-asked).
  DuckDB 2.0's `SET_TABLE_OPTIONS` / `RESET_TABLE_OPTIONS`, as two ADDITIVE `table_alter` kinds — NO ABI
  bump (the enum-value rule). Gate `verify_with_options` 206 -> 234.**
  - The vocabulary is the `CREATE TABLE … WITH (…)` one, both spellings, landing in the same persisted
    `fabricator.parquet.*` properties — so a table's write settings can now be CHANGED, not only
    declared. RESET is SET with a null value, which is the UNSET spelling `SetTableProperties` already
    took.
  - **⚠⚠ IT MADE A SHIPPED REFUSAL STALE, AND LIFTING IT IS THE REAL FIX: a column-list
    `CREATE TABLE t (…) WITH (parquet_compression='zstd')` used to be REJECTED** with *"write tuning
    applies to the statement's write — use CREATE TABLE AS"*. That reasoning was correct when written and
    stopped being true on 2026-08-07, when the tuning became a PERSISTED table property: the declaration
    is about the table's storage format, not about this statement. It is also the exact form the feature
    request was written in. The gate row that pinned the refusal now asserts the stronger thing — the
    property persists AND a later INSERT obeys it.
  - ⚠ A LIST literal (`['a','b']`) is accepted in BOTH `WITH` and `SET`: DuckDB parses it as a
    `list_value()` CALL rather than a constant, so it needs its own branch in the renderer. Joined with
    commas, which is the spelling the comma-separated options already take — one renderer
    (`TableOptionValueText`) for both statements, so they cannot disagree.
  - ⚠ An unknown key is REFUSED, never ignored (the WITH rule), and so are the CREATE-only feature flags,
    BY NAME with the reason — they are real WITH options, so the natural assumption is that SET can
    change them.
  - ⚠ OPEN AND THE USER'S, unchanged: the `System.Security.Cryptography.Pkcs` CVE (GHSA-846f-f764-76mx —
    the assembly is NOT in the shipped payload, but **NU1903 does not fail our build**, so the next
    transitive CVE is silent too); and whether to supersede the v0.1.0 draft with a v0.1.1 carrying the
    truncated-max Delta stats fix, which IS reachable on the default path.

- **THE DELETE/UPDATE SHORTCUT — DELETE (ABI v93) AND UPDATE (ABI v94) ON SQL SERVER ARE BUILT (2026-10-24;
  records: docs/duckdb-2.0-remote-execution.md §2.4 + §2.5). UPDATE: each SET value must render exactly too —
  constants (VARCHAR included), exact-type columns, integer `+ - *`; casts/functions/division/DECIMAL arithmetic
  refused. ⚠ The FILTER under an UPDATE carries a PROJECTION MAP (plan_filter.cpp turns it into a projection),
  so SET values index its NARROWED output and are remapped before naming a scan column — invisible in an
  `optimized_only` logical dump. `table_delete_where` was renamed `table_dml_where` and v94 is load-bearing (a
  v93 bridge would run an UPDATE spec as a DELETE). Gate grew 60 -> 131. A DELETE whose WHOLE predicate serializes in STRICT mode (no
  dropped conjunct; types BOOLEAN/ints/DECIMAL/DATE only — VARCHAR refused for collation) runs as one
  `DELETE … WHERE` via the plan-less `PlanDelete`; anything else takes the rowid path. Kill switch
  `fabricator_dml_pushdown`; trace `FABRICATOR_DUMP_DML=1`. ⚠ At physical planning filter refs are BOUND_REF
  (map via projection_ids → column_ids). Side effect: a no-PK table becomes deletable for exact predicates.
  Gate `verify_dml_pushdown_mssql` (service; differential vs the kill switch; 153 since the timestamp fix).
  - **⚠⚠ THE ROWID DML PATH BOUND TIMESTAMPS AS LEGACY `datetime` — FIXED `c977c21`, known-limitations 1.28.**
    `AddWithValue(DateTime)` rounded datetime2 SET values and made datetime2 KEYS match nothing (0 rows, no
    error). A blanket datetime2 pin breaks legacy `datetime` keys instead (.127 is stored .1266667), so the
    binding follows the column's declared type (`DateTimeBindings`, one sys.columns read per statement on the
    statement's own connection, only when a timestamp field is bound).
  - **duckdb-mysql (read 2026-10-24, doc §1.3a)** uses the remote optimizer for SELECT/UPDATE/DELETE/some DDL;
    its CREATE writer falls through `IF NOT EXISTS` into OR REPLACE's DROP (reachable, untested upstream).
    DECIDED: pushed DML keeps DuckDB semantics (doc §1.4); complex DML comes with SELECT pushdown, not before.
  - **Delta DELETE via an EW predicate (user-raised):** `DeltaFilterBuilder` already builds EW predicates (used
    for `DeclareRead`). EW `DeleteAsync(predicate)` prunes AlwaysFalse files but READS every other candidate
    whole (its codec reader, all columns) and only removes a file after reading. Its evaluator already answers
    `AlwaysTrue` and the pruner feeds partition values as exact min=max, so "AlwaysTrue ⇒ remove without
    reading" is a small UPSTREAM offer — partition columns first, because `DeltaFileStatsAccessor.IsMinExact/
    IsMaxExact` are hardcoded true while Delta string stats can be truncated (a false AlwaysTrue deletes rows).
    Our side: EW's delete sees only the BASE snapshot, so explicit transactions with pending writes on the table
    must keep the rowid path.
    - **⚠ CORRECTION: the "truncated string stats" risk was OVERSTATED.** The comparison evaluators do not read
      `IsMinExact/IsMaxExact`; `AlwaysTrue` uses the SAME bound contract pruning already trusts (max is an
      upper bound — EW's `TruncateMaxString` rounds UP; `WidenTimestampMax` covers Spark's ms timestamps). What
      changes is the CONSEQUENCE of a wrong bound (a deleted row instead of a missing read), hence partition-only
      first.
    - **OPENED upstream as clast-project/engineered-wood#506 (2026-10-24, user-directed: issue before PR).**
      Related open upstream issues: #439 (DELETE prunes row groups/pages — orthogonal, composes), #502 (late
      materialization), #493 (CommitDataFilesAsync ORPHANS large born-deleted DVs on a failed/retried commit —
      our same-txn insert-then-delete path), #500/#501 (checkpoint fallback, discard-unless-completed writes),
      #504 (every Decimal128 written as FLBA(16) — our codec writer), #497 (adaptiveMetadata, tracking only),
      #444 (layout for Fabric Direct Lake), #384 (Spark function registry gap).
    - **⚠ MISSED BY THE 2026-10-23 BUMP SWEEP: EW #489 (in our pin 987a0ce) REFUSES commits to a UniForm table**
      unless `DeltaTableOptions.AllowWritesWithoutUniversalFormatConversion` (we never set it), so a write from
      us to a UniForm table now fails loudly instead of leaving its Iceberg metadata stale. Correct behaviour;
      #490 (OPEN) plans the converter. The bump's surface diff should have shown the new option. — Original survey entry (2026-10-24, user-directed: overview of 2.0 planner
  features first). Full record: [docs/duckdb-2.0-remote-execution.md](docs/duckdb-2.0-remote-execution.md).**
  - Two routes. (a) the NEW remote pushdown optimizer: a PRE-BIND AST rewrite that ships whole
    `DeleteQueryNode`/`UpdateQueryNode`/SELECT nodes to a catalog declaring `EXECUTE_QUERY_NODE`; none of
    duckdb-postgres / ducklake / iceberg use it (postgres declares only `IS_REMOTE`+`CONNECT`), it has no
    in-tree test, the statement is never bound locally (no `RegisterDBModify`, no CHANGED_ROWS), and it
    hands us DuckDB-dialect AST that needs a T-SQL renderer. (b) the PLAN-LESS `PlanDelete`/`PlanUpdate`
    overloads (1.5 AND 2.0): overriding them means the child scan is never built.
  - **MEASURED on our SQL Server catalog: the whole predicate survives as a FILTER directly above
    FABRICATOR_SCAN** (our pushdown never erases), incl. OR + `lower()`; UPDATE adds two projections.
  - **⚠ THE SAFETY RULE: render EXACTLY or fall back.** Scan pushdown may be a superset because DuckDB
    re-applies predicates; a remote DELETE has no net — a weaker predicate deletes rows the user did not
    name. Fall back on: any unrenderable conjunct, a join/USING/second scan, RETURNING, MERGE.
  - Recommended order: SQL Server DELETE, then UPDATE, then a Delta METADATA-ONLY delete (whole-file
    `remove` from partition values / exact stats — neither DuckLake nor Iceberg has one), then
    `get_partition_stats` for Delta, and the remote optimizer as a later spike.
  - ⚠ duckdb-postgres forwarded whole DELETE/UPDATE once (`dd51b9f`, 2023) and reverted it the same day with
    no stated reason — look for the edge case before trusting the shape.
  - **User notes (2026-10-24), for the later remote-optimizer spike:** its real consumer is DuckDB's own
    **quack** extension + `CONNECT`; and for the "DuckDB AST → T-SQL" problem, the local repo
    **`D:/repos/mssql-adbc`** (experiment by the engineered-wood author) translates **Substrait → T-SQL** in
    C# and rejects what it cannot translate exactly. DuckDB has a substrait extension (`get_substrait`); not
    linked in our build, 2.0 support and DML/AST coverage unchecked. Recorded in the doc §1.3/§1.4.

- **⚠⚠ THE FULL BATCHED DELTA FORM IS A DECLARED `schema` MAP ON 2.0 (2026-10-24, user-directed). C#-only.
  Gate `verify_delta_batched_read` 399 -> 406; hermetic 83/83 — 10218 (= 10211 + 7, nothing else moved).**
  - **WHY IT WAS POSSIBLE, measured with the same probes on our 1.5.6 and 2.0 builds:** 2.0 lets a NAME-keyed
    `schema` map compose with `file_row_number` (1.5: *"column 2147483645 … not found"*), fixed the field-id
    `INTERNAL Error` (1.5 invalidated the database), and maps nested members through the entry's 4th field
    `children` — which 1.5 ACCEPTED AND SILENTLY IGNORED. ⇒ docs/duckdb-upstream-issues.md §1 is NOT to be
    filed any more.
  - **WHAT CHANGED:** `TryFullForm` keeps its routing, inputs and outputs, and swaps `union_by_name` +
    `PresentNames` (a parquet_schema footer sweep) for the plain form's map entries + `filename` +
    `file_row_number`. The materialized row-tracking column is DECLARED with a NULL default, so a file
    lacking it falls through to the derived id with no presence question — that removes a wrong-answer
    hazard, not just a cost. Structs are served (the old form declined them). `PresentNames` and
    `ContainsStructAnywhere` are deleted. The routing marker in the suite is now
    `filename => true, file_row_number => true` (unique to this form).
  - Mutation-tested: dropping the materialized COALESCE dies at the suite's line 387.
  - **STATE 2026-10-24, end of the batched-read run: tip `fdfe390` (local, unpushed); hermetic 83/83 — 10333,
    service 60/60 — 3639 (IDENTICAL to its floor, so none of the day's Delta changes moved SQL Server/S3/
    PolyBase).** Commits: `d6662c9` full form = schema map, `509499c` Spark-named materialized row ids,
    `d8aa45c` id mode by field id, `7424281` seam stamps nested field ids, `5e1ad8b` full form in id mode,
    `fdfe390` bloom columns on mapped tables.
  - ⚠ NOT re-measured: every REMOTE number ranking the full form against the union/plain forms was taken on
    the union_by_name form (the union form's remote LIMIT gate leans on it).
  - **✅ ID-MODE TABLES BATCH BY FIELD ID (2026-10-24) — the plain form (and the union form built on it) keys
    the `schema` map by INTEGER field id; STRUCTS TOO since the seam fix below (id-keyed `children`, §16c/§16e,
    458 -> 475). Only a list/map holding a struct stays per file (its element node has no Delta id).** Gate `verify_delta_batched_read` §16
    (424 -> 458): a FOREIGN-named file (different stored names, same field ids) committed by hand as an `add`
    action — the shape the old name-mode gate warned about and no suite could build. Two mutants: a name-keyed
    map dies at 16a reading `NULL NULL`; the old blanket decline dies at the routing row.
    - **✅ FIXED THE SAME DAY (user: "files already written don't care, we are still in dev"): the seam renders the
      WHOLE id tree from EW's Arrow metadata (`FieldIdsSpecFromArrow`, same FIELD_IDS shape as the bulk path).
      Mutant (top-level only) dies at §16e: 4 member nodes without an id. History of the gap:** the rewrite seam
      (`NativeParquetDataFileWriter.FieldIdsFromMetadata` — DML post-images, compaction) stamps TOP-LEVEL field
      ids only, while the bulk path (`BuildFieldIdsSpec`) recurses. So an id-mode struct's members in a
      rewritten file carry no id; an id-keyed `children` map would read them as NULL, while the per-file loop
      survives by falling back to the physical name. ⚠ Files written BEFORE the fix carry no member ids and an
      id-mode batch now reads their struct members as NULL — accepted deliberately (dev only, no real tables).
    - **✅ AND THE FULL FORM (the rowid shapes: DML reads, `rowid` projections) takes the same id-keyed map
      beside `file_row_number` — §16f (475 -> 483); mutant (full form declines id mode) dies at its routing
      row. ⚠ A test query must keep a DATA column in the projection: `count(*) … (SELECT rowid, (s).a …)` lets
      the optimizer prune `s`, and a rowid-only scan never reaches the full form (it needs ≥ 1 data column).**
    - Still on the loop in id mode: a list/map holding a struct, and a projected row-tracking column (no field id
      in ANY writer — measured on Fabric Spark + delta-spark source + EW + ours, all appending them LAST).
  - **⚠⚠ known-limitations §2.x WAS REAL AND IS FIXED (2026-10-24): a SPARK-named materialized row id was
    IGNORED.** MEASURED on a Fabric Spark table (row tracking, UPDATE of ids 2/3/12): we returned
    **22/20/21** (the new file's baseRowId + position), both read paths, no error. Every site keyed on the
    LITERAL `__delta_row_id` — the name OUR tables declare in `delta.rowTracking.materializedRowIdColumnName`;
    Spark declares `_row-id-col-<uuid>`. Now read from the property (`DeltaReader.MaterializedTrackingNames`,
    `NativeScanList.MaterializedRowIdName`) in `RowTrackingExpr`, the row-id filter verdict, `TryFullForm` AND
    the **clustered rewrite**, which carried ids only when the declared names EQUALLED ours (so a SORTED BY
    OPTIMIZE of a Spark table re-numbered every moved row). Re-measured live after: 2/3/12. Gates
    `verify_delta_batched_read` §13e (424) and `verify_delta_clustered_optimize` §9 (160), each declaring a
    Spark-style name BEFORE the rewrite so the post-image stores it; both mutation-tested.
  - **New upstream defect, ready to file (§11):** 2.0's `FieldIdMapper` `break`s at the first column without
    a field id, so every id'd column AFTER it silently reads NULL. Unreachable from known writers (they put
    id-less columns last), measured.
  - Related measurements the same day (docs/known-limitations.md §3): the Fabric SQL endpoint SILENTLY SKIPS
    column-mapping `id` tables (was an explicit error in July) and DROPS struct columns in every mode; Fabric
    runtime 2.0 rejects `delta.enableIcebergCompatV3`. IcebergCompat was evaluated and NOT recommended
    (engineered-wood has no UniForm converter; it would cost our fast commit path and DV default).


### From CLAUDE.md §Next up — the distribution smoke test and the projection fix

- **⚠⚠ THE DISTRIBUTION SMOKE TEST IS A SECOND DECLARATION OF THE USER-FACING SURFACE, AND IT ONLY RUNS
  ON A TAG PUSH — so a BREAKING RENAME sits unnoticed until the next release. Found 2026-09-21 when
  `v0.0.15`'s `distribution` run failed ALL FOUR pack jobs at the smoke step (build and pack were fine;
  `release (draft)` was skipped, so no release object and no assets were ever created).**
  - Two stale calls, both broken by changes in the v0.0.14..HEAD interval: `fluid_query(…, params := …)`
    (renamed to `fluid_replacement_query`, and the NAME REUSED for a different function), and
    `{{ who }}` (the params bag stopped being SPREAD as top-level variables — `{{ params.who }}` now).
  - **⚠⚠ THE ARTIFACT WAS FINE — read which CHECK failed before blaming the build.** `LOAD`, the core
    registration and the managed-bridge boot all PASSED on every platform; only the two stale SQL calls
    failed. "All four platforms failed" reads like a broken release and was a broken test.
  - **⚠⚠ THE COMMENT ABOVE THE CHECK IS WHAT MADE THE FIX CORRECT.** It states the check exists to cover
    the `bind_replace` registration path (`GlobalSqlTableFunctions`), which is now
    `fluid_replacement_query` — so "fixing" it to the NEW `fluid_query` would have left it green while
    silently covering a different path. The corrected check says so in its own comment.
  - **⚠ CLAUDE.md WAS THE STALE SOURCE HERE, and the code comment was right.** This file still said the
    params bag is *"bound whole TOO, under the name `params`"* — additive. `FluidValueModel` says
    *"The spread is GONE (user decision) and there is no alias"*. MEASURED: `{{ who }}` renders empty,
    `{{ params.who }}` works. **When memory and a code comment disagree about a contract, measure.**
  - ⇒ the same class as the recorded `integration.yml` `ls build/plugins/fluid/` breakage: **a CI step
    that names a user-facing surface must be grepped whenever that surface is renamed.** `grep -rn
    '<old name>' .github/ test/distribution/` belongs in any rename pass.

- **⚠⚠ PROJECTING A COLUMN THAT IS NOT THE FIRST WAS BROKEN ON THREE HOST TABLE FUNCTIONS —
  USER-REPORTED 2026-09-21 as a `fluid_query` crash, ROOT-CAUSED AND ✅ FIXED THE SAME DAY. C++-only,
  THREE LINES, no ABI change. Gate `verify_host_query` 107 → **124**; two mutants each killed at its own
  row. Full record: [docs/known-limitations.md](docs/known-limitations.md) §1.27 (kept as a FIXED entry
  because the diagnosis is worth more than the fix).**
  - **THE CAUSE, one sentence:** `fabricator_host_query` / `fabricator_host_exec` / `fabricator_scan`
    never set `TableFunction::projection_pushdown`, while the `ArrowStreamScan` they share fills the
    output chunk by walking `output_column_ids` and writing slot k from `output_source_pos[k]` — correct
    ONLY when DuckDB has NARROWED the chunk. Without the flag DuckDB returns a FULL-WIDTH chunk while
    still reporting the needed subset in `column_ids`, so the k-th needed column lands in slot k.
  - **THE REPRO IS ONE LINE AND HAS NOTHING TO DO WITH FILTERS, FLUID OR DECISION TABLES:**
    `SELECT b FROM fabricator_host_query('SELECT 5 AS a, ''x'' AS b');`
  - **⚠⚠ THE SEVERITY WAS WORSE THAN THE FIRST WRITE-UP SAID, AND IN THE DIRECTION THAT MATTERS.** I had
    recorded *"every same-type case passes VACUOUSLY"*. It does not — a same-type subset **SEGFAULTS**
    (exit 139): `Vector::Reference` accepts the wrong column when the types agree, and the never-written
    slot is read afterwards. So the two modes are an INVALIDATED DATABASE (types differ) and a DEAD
    PROCESS (types agree); the type mismatch was merely the louder one. **"Passes vacuously" was inferred
    from the absence of an error message in a pipeline that had already died.**
  - **⚠⚠ IT WAS NOT A FILTER BUG AND THE FILTER MODEL WAS INNOCENT — the flip test pointed confidently
    at the wrong subsystem.** The report came through `fluid_query`, and the message naming whichever
    pair of types the two columns had made *"a filter value is read at the wrong index of the staged
    constants batch"* look certain. What the OR actually contributes is only this: `Flatten` DROPS a
    disjunction while its constants STAY STAGED, so the emitted statement references a **SUBSET** of the
    staged columns — which is what dragged the projection bug in. Dumping the statement showed the
    indexes were CORRECT (`region = idx:1` against a `[Int32, String]` batch → `CAST(v1 AS VARCHAR)`).
  - **⚠⚠ ONE OF MY INTERMEDIATE READINGS WAS AN ARTEFACT OF MY OWN PROBE.** A first dump piped through
    `tr ',' '
'` split the statement mid-item and appeared to show `region` referencing `v0`, the
    integer — "confirming" the stale-index theory. Printing the conjuncts properly showed the opposite.
    **A probe whose output you reformat can manufacture the finding you are looking for.**
  - **⚠⚠ AND MY UNTRUNCATED-GREP DISCIPLINE FAILED THE SAME WAY:** `grep -rn projection_pushdown src/ |
    head -20` cut off the `fabricator_extension.cpp` hits, so I nearly "fixed" three functions that
    ALREADY had the flag — and would have mis-stated the blast radius. **`head` on an evidence grep is the
    same class of mistake as `tail` on a tier log**, which this file already records.
  - **WHY IT SURVIVED:** `SELECT *` and `SELECT <first column>` work whatever the bug does (position 0
    maps to slot 0), and that is what every existing test and nearly all real usage do. Every other
    `ArrowStreamScan` registration already set the flag — `fabricator_query`'s carries the comment
    *"arrow_ingest maps requested columns; required"* — so this exact defect had been found and fixed
    once and these three were missed.
  - **⚠⚠ READING DID NOT FIND IT; ONE PRINT DID.** Four passes over the filter model, the mapping and
    the ingest each concluded the code was correct. One env-gated `Printer::Print` at the mapping site
    settled it in a single run: `out_cols=2[INTEGER,VARCHAR] … output_column_ids=[1] output_source_pos=[1]`
    — a two-wide chunk filled at slot 0. The rule this file already states, paid for again.
  - ⚠ The third flag (`fabricator_host_exec`) is **UNGATED and the test says so**: one column, so only
    the empty projection differs and a mutant removing it SURVIVES. Set for the contract, not for a test.
  - ⚠ `decision_check` stays on `fluid_replacement_query` (adopted as the workaround) because the
    remaining merit stands: `bind_replace` lets DuckDB push the caller's predicate into the generated
    SQL. `decision_coverage` is back to costing nothing on `fluid_query`.


### From CLAUDE.md §Next up — record heads 2026-09-15 … 2026-08-18 (each points at its full record)

- **⚠⚠ AN `UPDATE` THAT SETS A *LIST* COLUMN HANDED THE PROVIDER THE WHOLE ROW AS SET COLUMNS — 17 instead
  of 1. ANALYSED 2026-09-15 from `fabricator-grist`, ✅ FIXED THE SAME DAY by the virtual override this repo
  did not have. No ABI change; C++ only. ⚠ THE AS-BUILT RECORD IS THE LAST SUB-ENTRY — read it first; the
  analysis below it is intact and still accurate, and one thing it could not know is that on THIS tree every
  triggering type is also one the Delta provider cannot write (limitation 1.26).** A provider that diffs the incoming row to decide what actually
  changed cannot tell "the user set 17 columns" from "DuckDB expanded one", and there is nothing on the
  wire that says which happened.
  - **The rest of this record moved verbatim to [docs/feature-history.md](docs/feature-history.md) §Appendix (2026-09-18).**


- **⚠⚠ `Fabricator.Functions` — A BUILT-IN PROVIDER WHOSE ONLY CONTRIBUTION IS GLOBAL MACROS, SHIPPED AS
  REAL `Macros/*.sql` FILES. BUILT 2026-09-15 (user-directed: *"let us add a seperate plugin project for
  such useful global functions `Fabricator.Functions`. look at fabricator-grist repo. there we put all
  macros as sql into a macros subfolder which are added as a resource"*). C#-only, NO ABI change, NO C++
  change. First occupants: `table_zip` / `query_zip`, developed interactively against the real extension
  over the preceding session. Gate: a NEW hermetic suite `verify_functions_zip` (**67**), floors
  9343 → **9410** and MIN_SUITES 76 → **77**, three mutants. Hermetic **77/77 — 9410**, i.e. 9343 + 67
  exactly, which is the behaviour-neutrality claim for the `ProviderRegistry` change below — it touches
  EVERY provider's discovery.**
  - **The rest of this record moved verbatim to [docs/macros-and-sqlgen-functions.md](docs/macros-and-sqlgen-functions.md) §Appendix (2026-09-18).**

- **⚠⚠ THE CALL-SCOPED FLUID SURFACES STOPPED COPYING THEIR INPUT TWICE — BUILT 2026-09-14 (user-directed:
  *"we have the rows in memory as arrow/recordbatch anyway"* … *"the idea of arrow is to avoid copying
  data.. analyse all fluid functions where you unnecessarily duplicate data"*). C#-only in the plugin, NO
  ABI change. `fluid_scalar`, `fluid_query_lateral` and `fluid_query_inout` now stage a VIEW over the
  registered batch instead of a temp table, and bind the Liquid `input_table` over THAT SAME batch instead
  of re-querying DuckDB. ONE shared helper (`FluidRelationInput.StageLive`). Full record:
  [docs/fluid-templating.md](docs/fluid-templating.md) §44.**
  - **The rest of this record moved verbatim to [docs/fluid-templating.md](docs/fluid-templating.md) §Appendix (2026-09-18).**

- **⚠⚠ `fluid_aggregate` — A TEMPLATE RENDERED ONCE PER GROUP, WHOSE RESULT TYPE IT DECLARES. BUILT
  2026-09-14 (user-asked, after an analysis pass: *"a fluid_aggregate must at bind be able to supply the
  return_type! If it is possible then lets build it"*). It IS possible, for one structural reason:
  **`agg_open` is already called from DuckDB's aggregate BIND**, so a session exists per CALL SITE. ABI
  **v89** teaches that entry to carry the call. Gate `verify_plugin_fluid` 905 → **946** (§40), hermetic
  floor 9261 → **9302**, four mutants each killed at its own row. Full records:
  [docs/abi-history.md](docs/abi-history.md) §v89 + [docs/fluid-templating.md](docs/fluid-templating.md)
  §43.**
  - **The rest of this record moved verbatim to [docs/fluid-templating.md](docs/fluid-templating.md) §Appendix (2026-09-18).**

- **⚠⚠ `fluid_query` IS NOW AN ORDINARY TABLE FUNCTION AND THE OLD ONE IS `fluid_replacement_query` —
  BUILT 2026-09-13 (user-designed), TWO commits: the BREAKING rename, then the new function. The new one
  runs its own statement and HANDS THE TEMPLATE the scan's pushed FILTER and PROJECTION, as Fluid values and
  as DuckDB variables. C#-only in the plugin apart from THREE latent host defects it exposed (below); NO ABI
  change. Gate `verify_plugin_fluid` 873 → **905**, hermetic floor 9229 → **9261**, four mutants — and TWO
  of them are the useful ones because one SURVIVED and one killed the wrong thing. Full record:
  [docs/fluid-templating.md](docs/fluid-templating.md) §41 (the rename) + §42 (as built).**
  - **The rest of this record moved verbatim to [docs/fluid-templating.md](docs/fluid-templating.md) §Appendix (2026-09-18).**

- **⚠⚠ A VARCHAR PARAMS BAG IS JSON ONLY WHEN IT *IS* JSON — BUILT 2026-09-13 (user-raised from a real
  failure: `fluid_render('…','result')` died with "params is not valid JSON"). C#-only in the plugin, and it
  changes EVERY Fluid surface at once because they share one `CaptureBag`. Gate `verify_plugin_fluid`
  867 → **873**, floor 9223 → **9229**. Full record:
  [docs/fluid-templating.md](docs/fluid-templating.md) §40.**
  - **The rest of this record moved verbatim to [docs/fluid-templating.md](docs/fluid-templating.md) §Appendix (2026-09-18).**

- **⚠⚠ `fluid_scalar` — A TEMPLATE RENDERED TO A SQL SELECT, WHOSE RETURN TYPE THE TEMPLATE DECLARES.
  BUILT 2026-09-13 (user-designed). C#-only IN THE PLUGIN: NO ABI change, NO C++ change, NO bridge change.
  Gate `verify_plugin_fluid` 828 → **865** (§38), hermetic floor 9184 → **9221**, three mutants run against
  the earlier expression form (two killed at their own rows; the ordering one SURVIVED, which is what showed
  the ORDER BY was never load-bearing and made the statement form a cheaper trade than it looked). Full record:
  [docs/fluid-templating.md](docs/fluid-templating.md) §39.**
  `fluid_scalar(template, params, arg_0 … arg_N)`; under `is_bind` the template renders a SELECT of the
  result type (`select NULL::STRUCT(a INTEGER)`), otherwise the SELECT that computes it over `input_table`.
  - **The rest of this record moved verbatim to [docs/fluid-templating.md](docs/fluid-templating.md) §Appendix (2026-09-18).**

- **⚠⚠ THE PARAMS BAG IS A DuckDB VARIABLE TOO — `getvariable('params')` in the render's own SQL. BUILT
  2026-09-13 (user-asked). C#-only IN THE PLUGIN: NO ABI change, NO C++ change, NO bridge change. Gate
  `verify_plugin_fluid` 810 → **828** (§37), hermetic floor 9166 → **9184**, THREE mutants each killed at its
  own row. Full record: [docs/fluid-templating.md](docs/fluid-templating.md) §38.**
  - **The rest of this record moved verbatim to [docs/fluid-templating.md](docs/fluid-templating.md) §Appendix (2026-09-18).**


- **⚠⚠ SESSION RECORD 2026-09-12 — FOUR COMMITS, PUSHED (`59277bf..0a22d8b` on `v1.5-variegata`), NOTHING
  LEFT OPEN. ⚠ `git log` is the authority, not this prose.**
  - **The rest of this record moved verbatim to [docs/feature-history.md](docs/feature-history.md) §Appendix (2026-09-18).**

- **⚠⚠ `fluid_query_inout` — THE STREAMING SIBLING OF THE COLLECTOR. BUILT 2026-09-11 (user-asked). C#-only,
  NO ABI change and NO C++ change. Gate `verify_plugin_fluid` 759 → **794** (§35), hermetic floor
  9088 → **9123**, THREE mutants each killed at its own row. Full record:
  [docs/fluid-templating.md](docs/fluid-templating.md) §30.** It is the item §19 recorded as deliberately
  still open — *"a bounded-memory batched variant is the SAME body registered on the streaming in-out"*.
  - **The rest of this record moved verbatim to [docs/fluid-templating.md](docs/fluid-templating.md) §Appendix (2026-09-18).**

- **⚠⚠ THE INVALID-VIEW ENUMERATION FIX IS COMMITTED as `59277bf` (2026-09-11) and UNPUSHED. Its full
  record is the "AN INVALID VIEW CRASHED CATALOG ENUMERATION" entry further down; this is the handoff so a
  fresh session does not re-derive it.**
  - **The rest of this record moved verbatim to [docs/feature-history.md](docs/feature-history.md) §Appendix (2026-09-18).**

- **⚠⚠ THE 2026-09-08 USER-DIRECTED PAIR IS DONE — BOTH (A) and (B). (A) the `column_default` read-back
  (its record is the entry below, `581b598`), and (B) the ALTER gates (this entry). It also produced two
  findings recorded separately: the upstream-issues §7 RETRACTION — there was no DuckDB bug — and the
  `KeepNulls` NULL-replacement defect, limitation 1.23, which is NOT fixed and wants its own change.**

  **⚠ AND THE `KeepNulls` DEFECT IT FOUND IS ALSO FIXED (2026-09-10, `mssql_keep_nulls`, entry below) —
  as a SETTING, because it is not solveable as a policy.**

  **⚠ WHAT IS STILL OPEN:** the ONE residual of that setting (`VALUES (…, DEFAULT)` on an EXPRESSION
  default stores NULL under the new default — inherent, since the binder collapses it with an explicit
  NULL); `ALTER` under CDC (a captured column cannot be renamed at all, `Msg 4928` — unasserted); and the
  warehouse leg of both ALTER gates plus `supports_default_constraints`, all UNMEASURABLE here for want of
  a live warehouse.

  **(B) IS BUILT — a NEW SERVICE SUITE `test/verify_alter_mssql.test` (**63**), covering rename table,
  rename column, drop column, change type and SET/DROP NOT NULL. Floors 3316 → **3379** and MIN_SUITES
  56 → **57**. TWO mutants, each killed at its own row. SQL Server's ALTER now has TWO gates
  (`verify_alter_default` owns DEFAULT, `verify_comment_on_mssql` owns COMMENT ON, this owns the rest);
  before 2026-09-08 it had NONE, which is how a silently-dropped DEFAULT survived to be user-reported.**
  - **The rest of this record moved verbatim to [docs/feature-history.md](docs/feature-history.md) §Appendix (2026-09-18).**

- **⚠⚠ THE `column_default` READ-BACK — BUILT 2026-09-09 (user-directed; the LITERALS-ONLY shape put to the
  user and chosen). C++ + C#, NO ABI bump (one ADDITIVE key on the `table_info` doc, the comments'
  precedent). `information_schema.columns.column_default` and `duckdb_columns()` now report a SQL Server
  column's default. Gate `verify_alter_default` 59 → **82**, service floor 3293 → **3316**, TWO mutants each
  killed at its OWN row — and the FIRST one SURVIVED, which is where the value of the pass is.**
  - **The rest of this record moved verbatim to [docs/feature-history.md](docs/feature-history.md) §Appendix (2026-09-18).**

- **⚠⚠ upstream-issues §7 IS RETRACTED — there was no DuckDB bug, and the retraction is worth more than the
  finding was (2026-09-09).** It recorded *"a cast-wrapped column DEFAULT is flattened to NULL in the
  AlterInfo a FOREIGN catalog receives"*. Every MEASUREMENT in it reproduces; the INFERENCE was wrong.
  - **The rest of this record moved verbatim to [docs/duckdb-upstream-issues.md](docs/duckdb-upstream-issues.md) §Appendix (2026-09-18).**

- **⚠⚠ `mssql_keep_nulls` — A NULL IS WRITTEN AS NULL NOW, DEFAULT TRUE. BUILT 2026-09-10 (user-directed:
  *"i think this is not solveable we should make KeepNulls configureable"* — and the premise is right, it is
  not solveable as a policy). C#-only, NO ABI change. **BEHAVIOUR-CHANGING**: the previous behaviour was the
  replacement. Gate: a NEW service suite `verify_keep_nulls` (**68**), floors 3379 → **3447** and MIN_SUITES
  57 → **58**, THREE mutants each killed at its own section. Full record: limitation 1.23 (rewritten from
  a defect row into a fixed-by-setting row) + the README's settings and ATTACH tables.**
  - **The rest of this record moved verbatim to [docs/feature-history.md](docs/feature-history.md) §Appendix (2026-09-18).**

- **⚠⚠ `COMMENT ON TABLE` / `COMMENT ON COLUMN` — BUILT 2026-09-08 (user-asked "i think we don't support
  this yet?", then "yes build it"). C++ + C#, NO ABI bump (two ADDITIVE kinds on `table_alter`).
  WRITE-ONLY by decision; the read-back is slice 2. Gates: a NEW hermetic suite `verify_comment_on` (**39**)
  + a NEW service suite `verify_comment_on_mssql` (**39**), tier-0 252 → **258**, TWO mutants each killed at
  its own row. Full records: the README's DDL note, [docs/known-limitations.md](docs/known-limitations.md)
  1.21, [docs/warehouse-support.md](docs/warehouse-support.md) §6.7.**
  It was refused in BOTH directions before: the write failed with *"fabricator: only ALTER TABLE is
  supported"* and `duckdb_tables().comment` was NULL even for a comment already set on the server.
  - **The rest of this record moved verbatim to [docs/feature-history.md](docs/feature-history.md) §Appendix (2026-09-18).**


- **⚠⚠ `ALTER TABLE … ADD COLUMN … DEFAULT <literal>` WAS ACCEPTED AND SILENTLY DROPPED — user-reported
  2026-09-08 ("i think we don't support a default when adding a column"), FIXED. C++ + C#, NO ABI change.
  Gate: a NEW suite `verify_alter_default` (**59**, SERVICE tier), three mutants each killed at its OWN row.
  Full records: [docs/duckdb-upstream-issues.md](docs/duckdb-upstream-issues.md) §7 (the DuckDB half) +
  the README's DDL note.**
  MEASURED before anything was built: the column appeared, existing rows were NULL, **and every later insert
  omitting the column was NULL too** — on BOTH providers — while DuckDB's own table backfills AND applies it.
  The C++ `ADD_COLUMN` render read only `Name()` and `Type()`, never `DefaultValue()`.
  - **The rest of this record moved verbatim to [docs/feature-history.md](docs/feature-history.md) §Appendix (2026-09-18).**

- **⚠⚠ `fabricator_query` / `fabricator_exec` TAKE A PARAMETER BAG — ABI **v88**, BUILT 2026-09-06 (user-asked:
  "i think fabricator_query/fabricator_execute ABI version with parameter binding would be beneficial?").
  C++ + C#. Gate `verify_raw_query` 34 → **85** (service tier), two mutants each killed at its own row. Full
  records: [docs/provider-query-parameters.md](docs/provider-query-parameters.md) §5 (as built) +
  [docs/abi-history.md](docs/abi-history.md) §v88.**
  `fabricator_query(cat, sql, params := {'r': 'eu'})` and `fabricator_exec(cat, sql, {'new': 'uk'})` — values
  reach the provider as bound parameters, never as text. It closes the gap §1.1 of the design note names: DAX's
  `daxeval` grew its OWN bag *because* `fabricator_query` had none, so the provider with the weakest schema
  story had parameter binding and the one with real `SqlParameter` support did not.
  - **The rest of this record moved verbatim to [docs/provider-query-parameters.md](docs/provider-query-parameters.md) §Appendix (2026-09-18).**

- **⚠⚠ `fluid_query_lateral(template, params, <per-row columns…>)` — A TEMPLATE RENDERED ONCE PER INPUT
  CHUNK, CORRELATED, PARALLEL. BUILT 2026-09-05 (user-designed; their hint "laterals currently don't
  support named args, so params must be positional" settled the signature). C#-only IN THE PLUGIN: NO ABI
  change, NO C++ change, NO bridge change. Gate `verify_plugin_fluid` 459 → **563**, hermetic floor
  8735 → **8839**, two mutants each killed at its own assertion. Full record:
  [docs/fluid-templating.md](docs/fluid-templating.md) §22.**
  - **The rest of this record moved verbatim to [docs/fluid-templating.md](docs/fluid-templating.md) §Appendix (2026-09-18).**

- **⚠⚠ `{% query name materialize: 'view'|'table' %}` — LEAVE THE RESULT ON THE CONNECTION **AND** BIND THE
  NAME LAZILY. BUILT 2026-09-06 in two rounds (user-asked, then user-directed after they proposed the
  opposite mechanism and agreed the inversion was better). C#-only IN THE PLUGIN: no ABI, no C++. Gate
  `verify_plugin_fluid` 716 → 729 → **737**, hermetic floor 8992 → 9005 → **9013**, two mutants each killed
  at its own assertion. Full record: [docs/fluid-templating.md](docs/fluid-templating.md) §27.**
  - **The rest of this record moved verbatim to [docs/fluid-templating.md](docs/fluid-templating.md) §Appendix (2026-09-18).**
- **⚠⚠ `input_table` IS A LAZY FLUID VALUE TOO — the same inversion, second surface. BUILT 2026-09-06
  (user-asked: "yes lazy fluid makes also sense for input_table"). C#-only IN THE PLUGIN: no ABI, no C++,
  and NO new mechanism — it is `LazyRowsValue` pointed at the temp object each surface already creates.
  Gate `verify_plugin_fluid` 737 → **758**, hermetic floor 9013 → **9034**, one mutant. Full record:
  [docs/fluid-templating.md](docs/fluid-templating.md) §28.**
  - **The rest of this record moved verbatim to [docs/fluid-templating.md](docs/fluid-templating.md) §Appendix (2026-09-18).**
- **⚠⚠ `{% ret %}` — END THE RENDER HERE, KEEP WHAT WAS WRITTEN. BUILT 2026-09-05 (user-asked). C#-only IN
  THE PLUGIN: NO ABI change, NO C++ change, NO bridge change. Gate `verify_plugin_fluid` 563 → **599**,
  hermetic floor 8839 → **8875**, three mutants. Full record:
  [docs/fluid-templating.md](docs/fluid-templating.md) §23.**
  - **The rest of this record moved verbatim to [docs/fluid-templating.md](docs/fluid-templating.md) §Appendix (2026-09-18).**

- **⚠ PARENTHESES ARE ENABLED ON THE FLUID PARSER (2026-09-05, user-asked): `AllowParentheses = true`
  beside `AllowFunctions` in `FluidEngine.CreateParser`. One line. Gate `verify_plugin_fluid` 455 →
  **459**, hermetic floor 8731 → **8735**, one mutant. Full record:
  [docs/fluid-templating.md](docs/fluid-templating.md) §21.**
  - **The rest of this record moved verbatim to [docs/fluid-templating.md](docs/fluid-templating.md) §Appendix (2026-09-18).**

- **⚠⚠ THE FLUID PARAMS BAG IS NOW BOUND WHOLE TOO, under the name `params` — `params.x`, `params[0]`,
  `params.size`. BUILT 2026-09-05 (user-asked: "let us lift this … assign params to variable name
  `params`"). C#-only in the plugin, ONE function (`FluidValueModel.Capture`); NO ABI, NO C++. Gate
  `verify_plugin_fluid` 443 → **455**, hermetic floor 8719 → **8731**, two mutants. Full record:
  [docs/fluid-templating.md](docs/fluid-templating.md) §20.**
  - **The rest of this record moved verbatim to [docs/fluid-templating.md](docs/fluid-templating.md) §Appendix (2026-09-18).**

- **⚠⚠ `fluid_query_batch(template, <input> [, params :=] [, batchsize :=])` — A TEMPLATE RENDERED WITH A
  RELATION IN HAND, and its statement run. BUILT 2026-09-05 (user-designed over two rounds; the user's own
  correction — "whole table does not work, we have a special collector function for this" — settled the
  mechanism). C# in the plugin + two host members + ONE C++ marshal fix; **NO ABI change**. Gate
  `verify_plugin_fluid` 397 → **443**, hermetic **75/75 — 8719** (8673 + exactly 46, so no other suite
  moved), THREE mutants each killed at its own assertion. Full record:
  [docs/fluid-templating.md](docs/fluid-templating.md) §19.**
  - **The rest of this record moved verbatim to [docs/fluid-templating.md](docs/fluid-templating.md) §Appendix (2026-09-18).**

- **⚠⚠ `publish(name)` — A TABLE THE TEMPLATE STAGED BECOMES THE RELATION THE GENERATED SQL SCANS. BUILT
  2026-09-04 (user-asked, and the user chose this spelling over their own `fluid_table(this,'_result')`
  sketch). C#-only: NO ABI change, NO C++ change, and NO new SQL function — it renders a call to the
  `fabricator_scan` that already existed. Gate `verify_plugin_fluid` 344 → **386** (→ **397** with ABI v85's
  §24), hermetic floor 8620 → **8662** → **8673**, THREE mutants each killed at its own assertion. Full record:
  [docs/fluid-templating.md](docs/fluid-templating.md) §18 (§18.8 as built).**
  `SELECT * FROM {{ publish('_result') }}` after an `{% exec %}CREATE TEMP TABLE _result AS …{% endexec %}`.
  - **The rest of this record moved verbatim to [docs/fluid-templating.md](docs/fluid-templating.md) §Appendix (2026-09-18).**

- **⚠⚠ GLOBAL FUNCTIONS NOW GET THEIR CALLER'S CONTEXT — ABI **v82** (the scalar crossings) and **v83**
  (`host_query` may run AS the caller's session). BUILT 2026-09-02, user-directed and user-diagnosed
  ("problem is the set_active_open/trx stuff not implemented for global functions. read_blob can access the
  filesystem so we should also. and the context for the session sessions"). Full records:
  [docs/abi-history.md](docs/abi-history.md) §v82 + §v83. Gates: `verify_delta_clustered_optimize` 147 × 2
  engines, `verify_host_query` 98 → **107**, `verify_plugin_fluid` 177 → **188**; hermetic **74/74 — 8259**,
  service **54/54 — 3257**; THREE mutants each killed at its own assertion.**
  - **The rest of this record moved verbatim to [docs/abi-history.md](docs/abi-history.md) §Appendix (2026-09-18).**

- **⚠⚠ THE FLUID `exec()` — BUILT 2026-09-02 (user-asked: "i want a exec() in fluid as well"). C#-only, in
  the PLUGIN: NO ABI change, NO C++ change, NO bridge change. Full record:
  [docs/fluid-templating.md](docs/fluid-templating.md) §11.** Gate `verify_plugin_fluid` **188 → 234** (service **54/54 — 3318**),
  THREE mutants each killed at its own assertion; hermetic **74/74 — 8259** (unchanged — no hermetic suite
  loads this plugin) and service **54/54 — 3302** = 3272 + exactly this suite's 30, which is what shows no
  other suite moved. It also gives `IHostQuery.ExecuteNonQuery` its first caller, closing the gap recorded
  hours earlier.
  - **The rest of this record moved verbatim to [docs/fluid-templating.md](docs/fluid-templating.md) §Appendix (2026-09-18).**
- **⚠⚠ A FLUID ARRAY BINDS AS A SQL LIST — BUILT 2026-09-04 (user-asked, after hitting the refusal with
  `{% query result3 arg: input %}SELECT a: unnest($arg){% endquery %}`). C#-only IN THE PLUGIN: no ABI, no
  C++, no bridge. Gate `verify_plugin_fluid` 296 → **307**, hermetic floor 8572 → **8583**, two mutants.
  Full record: [docs/fluid-templating.md](docs/fluid-templating.md) §Values.**
  - **The rest of this record moved verbatim to [docs/fluid-templating.md](docs/fluid-templating.md) §Appendix (2026-09-18).**

- **⚠⚠ NAMED ARROW INPUTS ARE **TEMPORARY** VIEWS NOW — a SHIPPED DEFECT, measured and FIXED 2026-09-03
  (C++-only, NO ABI change). It came out of the `{% query %}`-by-name analysis and is much bigger than
  that feature. Gate `verify_delta_catalog_filter_modes` 39 → **55**, hermetic floor 8555 → **8571**, one
  mutant. Full record: [docs/host-query.md](docs/host-query.md) §Named Arrow inputs are TEMPORARY views;
  the analysis it came from is [docs/fluid-templating.md](docs/fluid-templating.md) §17.**
  - **The rest of this record moved verbatim to [docs/host-query.md](docs/host-query.md) §Appendix (2026-09-18).**
- **⚠⚠ THE `{% query name %}` BLOCK — the body is SQL, and the RESULT IS A RESULT SET. BUILT 2026-09-03
  (user-asked, and the requirement stated sharply: "where result is the result set and not some rendered
  as a single varchar, i.e. like a function call result"). C#-only IN THE PLUGIN: NO ABI, NO C++, NO
  bridge. Gate `verify_plugin_fluid` 275 → **285**, hermetic floor 8534 → **8544**, ONE mutant aimed at
  exactly that requirement. Full record: [docs/fluid-templating.md](docs/fluid-templating.md) §15.**
  - **The rest of this record moved verbatim to [docs/fluid-templating.md](docs/fluid-templating.md) §Appendix (2026-09-18).**
- **⚠⚠ THE `{% exec %}` BLOCK + THE `fabricator_render` → `fluid_render` RENAME — BOTH 2026-09-03,
  user-asked. C#-only IN THE PLUGIN: NO ABI change, NO C++ change, NO bridge change. The rename is
  BREAKING with NO ALIAS. Gate `verify_plugin_fluid` 256 → **275**, hermetic floor 8515 → **8534**, two
  mutants. Full record: [docs/fluid-templating.md](docs/fluid-templating.md) §13 (block) + §14 (rename).**
  - **The rest of this record moved verbatim to [docs/fluid-templating.md](docs/fluid-templating.md) §Appendix (2026-09-18).**
- **⚠⚠ THE PINNED HOST CONNECTION — ABI **v84**, BUILT 2026-09-03 (user-asked, with the shape given:
  "con=host.connection() / con.query() / con.dispose()", so "a exec() could create a temporary table on
  this connection which could be queried in the same render session"). C++ + C#. Full records:
  [docs/abi-history.md](docs/abi-history.md) §v84, [docs/host-query.md](docs/host-query.md) §PINNED
  CONNECTIONS, [docs/fluid-templating.md](docs/fluid-templating.md) §12.** Gate `verify_plugin_fluid`
  238 → **256**; hermetic floor 8497 → **8515**; `verify_host_query` **107** unchanged, which is the
  behaviour-neutrality claim for the fresh-connection path.
  - **The rest of this record moved verbatim to [docs/abi-history.md](docs/abi-history.md) §Appendix (2026-09-18).**
- **⚠⚠ CORE PROVIDERS ARE DISCOVERED BY GLOB (`Fabricator*.dll` in the managed dir), AND THE PROVIDER
  MANIFEST IS RETIRED UNBUILT — 2026-09-02, user-proposed ("instead of a manifest what about just listing
  `fabricator*.dll`"). C#-only, NO ABI change, NO C++ change. Gates: hermetic **75/75 — 8497** and service
  **53/53 — 3108**, both unchanged. Full record: [docs/plugin-services.md](docs/plugin-services.md) §13.**
  - **The rest of this record moved verbatim to [docs/plugin-services.md](docs/plugin-services.md) §Appendix (2026-09-18).**
- **⚠⚠ FLUID IS A BUILT-IN PROVIDER, NOT A PLUGIN — and `IBackend` IS NOW `IProvider`. BUILT 2026-09-02
  (user-directed: "i find the fluid usefull so we could add it as builtin functions instead of seperate
  plugin but leave as a seperate project?", then "interface + backendregistry"). C#-only apart from build
  files; NO ABI change, NO C++ CODE change. Gates: hermetic 74/74 — 8259 → **75/75 — 8497**, service
  54/54 — 3339 → **53/53 — 3108**. Full record: [docs/plugin-services.md](docs/plugin-services.md) §12.**
  - **The rest of this record moved verbatim to [docs/plugin-services.md](docs/plugin-services.md) §Appendix (2026-09-18).**
- **⚠⚠ THE PLUGIN SERVICE LOCATOR — STEP 1 BUILT 2026-09-02 (user-directed: "i am fine for an easy solution
  with `GetService<T>()` which works for the duckdb filesystem,http and host query/exec"). C#-only, NO ABI
  change, NO C++ change, BREAKING for out-of-tree plugins. **BOTH STEPS ARE NOW BUILT** — step 2
  (`Fabricator.Common`), §7.4a's `IHostLog` and the NuGet packaging on the same day. Full record:
  [docs/plugin-services.md](docs/plugin-services.md) §7 (the plan) + §8 (the locator) + §9 (Common) + §10
  (`IHostLog`) + §11 (versioning + packages), the last four all as built.**
  Gate `verify_plugin` 97 → **112**; `verify_plugin_fluid` **188** and `verify_http_transport` **21** both
  UNCHANGED, which is the behaviour-neutrality claim for the two capabilities that already had callers;
  hermetic **74/74 — 8259** (IDENTICAL to the previous floor) and service **54/54 — 3272** = 3257 + exactly
  this suite's 15, which is what shows no other suite moved. ONE mutant, killed at its own assertion.
  - **The rest of this record moved verbatim to [docs/plugin-services.md](docs/plugin-services.md) §Appendix (2026-09-18).**

- **FLUID TEMPLATING — SLICES 1, 2, 3 AND 4 DONE (`fluid_replacement_query` + the shared value model; the bind-time
  probe; `HostQueryTransport` + the Fluid `query`; `{% include %}` from any storage — 2026-09-01/02);
  SLICE 5 PLANNED, and §10.8 says RE-DERIVE it rather than inherit it.
  Full plan, the user's own code sketches, and the as-built record with every measurement:
  [docs/fluid-templating.md](docs/fluid-templating.md) §7.** C#-only, NO ABI change, NO C++ change — because
  `IBackend.GlobalSqlTableFunctions` already existed, which is §2's finding paying out immediately. Gate
  `verify_plugin_fluid` **23 → 89**, seven mutants each killed at its own assertion.
  `SELECT * FROM fluid_replacement_query('SELECT {{ n }} AS n', params := {'n': 7})` — `template` positional, **`params`
  NAMED and optional** (the `fabricator_sql_seq(2, cols := 3)` precedent), taking the same STRUCT / MAP / JSON
  bag `fluid_render` does.
  - **The rest of this record moved verbatim to [docs/fluid-templating.md](docs/fluid-templating.md) §Appendix (2026-09-18).**
- **⚠⚠ OPEN — `Host.Query` DOES NOT SEE THE CALLER'S SESSION, AND `fabricator_host_query`'s REPLAY OF IT
  RAISES A DuckDB INTERNAL ERROR ON AN UNQUALIFIED SEARCH PATH. Found 2026-09-01 (user-raised: "add a
  reminder about fixing the missing timezone in host.query… we need to think about this"); NOTHING BUILT.
  Full record + the four options: [docs/host-query.md](docs/host-query.md) §OPEN.** Neither is a slice-3
  regression — the first is how `Host.Query` has always behaved, the second predates today's commits.
  - **MEASURED**, outer session set: `SET TimeZone='UTC'` ⇒ `fabricator_host_query` reports **UTC**, a
    template's `query()` reports **`Europe/Berlin`** (the machine's zone); `SET search_path='myschema'` ⇒
    the outer session resolves `FROM t`, `query()` fails **"Table with name t does not exist!"**. ⚠ **The
    search-path half is the sharper one** — a wrong timezone is a silently wrong VALUE, an unresolvable
    name is an outright failure — and it breaks the README's own `SET TimeZone='UTC'` convention, which
    does not reach a template's queries.
  - **⚠⚠ WHY THE SQL SURFACE SEES IT IS NOT INHERITANCE, and that is the thing to understand before
    designing a fix**: `fabricator_host_query` is C++ invoked WITH a `ClientContext` and explicitly COPIES
    exactly TWO settings — `CaptureSession` reads `catalog_search_path->GetSetPaths()` +
    `TryGetCurrentSetting("TimeZone")`, `ApplyHostQuerySession` replays them onto the fresh connection. The
    C# path cannot: the ABI `host_query` entry has NO per-call context (its own comment says so) and opens
    on `g_host_db`, the DatabaseInstance captured at load.
  - **⚠ THE `SET SESSION …;` PREFIX TRICK (the in-tree precedent, `DeltaNativeReader.cs:519`) IS NOW MUCH
    LESS ATTRACTIVE BECAUSE OF NAMED PARAMETERS**: the no-params path is `SendQuery` (several statements, a
    prefix works) while the parameterised path is `Prepare` (ONE statement), so a prefix would silently
    work for unparameterised calls and break every parameterised one.
  - ⚠ The user's "optional settings string arg on Host.Query" is the right shape for *"run this WITH these
    settings"* and does NOT by itself achieve inheritance — the caller would have to name them, and a
    plugin does not know the session either. Passing the ambient `ClientContext` (which
    `HostHttpTransport` already reads per call) is the only option that inherits. **Settle whether it
    SHOULD inherit before choosing**; "copy the session" has no principled boundary, and the SQL surface
    copies two settings because those are the two that were needed.
  - **⚠⚠ AND THE SEPARATE, SHARPER BUG: `SET search_path='myschema'` + `fabricator_host_query(…)` raises
    `INTERNAL Error: SET_WITHOUT_VERIFICATION requires a fully qualified set path`** — `ApplyHostQuerySession`
    replays with `CatalogSetPathType::SET_DIRECTLY`, which demands fully-qualified entries, while
    `GetSetPaths()` returns what the user set. PRE-EXISTING (the same call and flag are in the parent of
    `20960af`; today only factored it into a helper). ⚠ It is an INTERNAL error, i.e. the assertion-failure
    class this file records as able to invalidate the database. **UNGATED, which is why it survived** — no
    suite sets `search_path` and then calls `fabricator_host_query`; any fix must land with one that does.
  - **⚠ RELATED, AND ALREADY A LONG-STANDING OPEN ITEM: the batched Delta reader FORCES
    `SET SESSION preserve_insertion_order=false;` onto its own statement** (`DeltaNativeReader.Statement`).
    User-raised 2026-09-01 as *"we added this for performance… after that we added get_partition_data +
    batch-index which enabled parallelism, so it might not be needed, or taken from the outer session"*.
    **The first half is RIGHT and already recorded**: `b96454a`'s own message says the batch-index fix
    *"MAKES OUR PER-STATEMENT preserve_insertion_order=false REDUNDANT ON THIS SHAPE (1858 ms by default vs
    1828 ms with the SET) AND IT IS DELIBERATELY NOT REMOVED"*, because the SET's justification is REMOTE
    numbers (94.5 s → 7.5 s) taken BEFORE that fix. Full record + the option list:
    [docs/scan-concurrency.md](docs/scan-concurrency.md) §10 item 1.
    - **⚠⚠ THERE IS A NAMED REASON TO EXPECT THE SET IS STILL NEEDED, so do NOT drop it as "obviously
      redundant now"**: the batch-index fix is about OUR scan, and the inner host query does not CONTAIN our
      scan — it contains DuckDB's `read_parquet`, `duckdb_arrow_scan` views, and a `WITH … AS MATERIALIZED`
      CTE, whose `PhysicalColumnDataScan` overrides `SupportsPartitioning` NOWHERE, while
      `AllSourcesSupportBatchIndex()` requires EVERY source.
    - **⚠⚠ IT IS NOT ONLY A PERFORMANCE QUESTION — REMOVING THE SET CREATES A PROMISE WE WOULD OWE
      (user, and MEASURED 2026-09-01).** UNION ALL of three 400k-row `read_parquet` scans at `threads=8`,
      order observed as the CLIENT receives it: `true` ⇒ 1,200,000 rows, **0 out-of-sequence steps**,
      first 0 / last 1199999; `false` ⇒ **478 steps**, first **368640**, last **1045759**. So the setting
      is OBSERVABLE, and DuckDB DOES keep insertion order across that union. ⚠ **Two questions, only the
      first answered**: (1) does DuckDB preserve what our plan emits — yes for the SYNTHETIC union, and it
      does NOT transfer, because our real plan carries `duckdb_arrow_scan` views and a MATERIALIZED CTE,
      the very sources with no batch index, so it would preserve order OR fall back to the SINGLE-THREADED
      collector, **i.e. the 94.5 s stall the SET exists to avoid**; (2) is the order our plan emits the
      TABLE's insertion order — **UNANSWERED and OURS**, since DuckDB preserves whatever the sources emit
      and nothing pins our union's branch order. That is what "adjust this with batchid or whatever" means.
      ⚠ **The obvious instrument is VOID**: `row_number() OVER ()` makes the plan order-preserving by
      itself (`PhysicalWindow` is one of four `SupportsPartitioning` overriders) and reports perfect order
      under BOTH settings; and at 3 × 2000 rows both settings give 0 steps because nothing splits below the
      row-group/morsel thresholds. Observe the CLIENT's rows, at scale.
    - **⚠⚠ THE "TAKE IT FROM THE OUTER SESSION" HALF IS IN DIRECT TENSION WITH THE SET's OWN
      JUSTIFICATION**, which is the part worth carrying: the SET ships as CORRECTNESS-NEUTRAL *here* (the
      batched reader's contract is already that row order across files is not preserved). **If that holds,
      inheriting is strictly WORSE than forcing** — a user on the DuckDB default (`true`) would pay the
      remote penalty for an order guarantee this reader never made. ⇒ inherit is worth building only if the
      neutrality claim is FALSE. It is also not POSSIBLE today, which couples it to the `Host.Query`
      session item above — forcing a literal is precisely what the missing session forces us into.
    - **⚠⚠ AND NAMED PARAMETERS HAVE CONSTRAINED THE MECHANISM (2026-09-01)**: the `SET SESSION …;` prefix
      works only because the no-params path is `SendQuery` (several statements); the parameterised path is
      `Prepare` (one). The Delta plan binds INPUTS, not parameters, so it is unaffected today — but the
      prefix is no longer a generally-available trick, and giving that plan a bound parameter would break it
      silently.
- **⚠⚠ `fabricator_host_query` USED TO EXECUTE ITS SQL TWICE — FOUND AND ✅ FIXED 2026-09-01 (C++-only, no
  ABI). It was the SAME shipped defect fixed for `fabricator_query` in `0acd679`, and the comment justifying
  it pointed at that very function. Gate `verify_host_query` 31 → **98** across the whole pass (53 at the
  describe fix, 92 with `fabricator_host_exec`, 98 with the named-source declared schema), FIVE mutants
  each killed at its own assertion; hermetic **74/74 — 8250** (8183 + exactly this suite's 67, so no other
  suite moved). Full record:
  [docs/host-query.md](docs/host-query.md).** MEASURED before: one call of
  `SELECT * FROM fabricator_host_query('INSERT INTO audit VALUES (1)')` left **2 rows**, and DDL failed its
  second run *"already exists"* from a statement issued once. After: 1 row.
  - **The rest of this record moved verbatim to [docs/host-query.md](docs/host-query.md) §Appendix (2026-09-18).**
- **⚠⚠ `fluid_render` LEFT THE CORE — it is `Fabricator.FluidPlugin` now (2026-09-01, user-directed:
  "move fluid fluid_render into its own Fluid Plugin"). C#-only, NO ABI change, NO bridge change, and
  BREAKING for anyone using it: it is OPT-IN. Full record:
  [docs/plugin-system.md](docs/plugin-system.md) §The FLUID plugin.**
  - **The rest of this record moved verbatim to [docs/plugin-system.md](docs/plugin-system.md) §Appendix (2026-09-18).**



- **⚠⚠ VARIADIC PARAMETERS — `Params.VarArgs` — BUILT 2026-08-31 for SCALAR + TABLE + SQLGEN (user-scoped:
  "let's try it with scalars+table function+sqlgen and defer in-out+lateral"). C# + C++, NO ABI bump (the
  style is an additive `fabricator.param_style` value, the `Params.Constant` precedent). Full record:
  [docs/custom-functions-design.md](docs/custom-functions-design.md) §13. Gates: `verify_global_functions`
  118 → **145**, `verify_sqlgen` 59 → **76**, `verify_custom_functions` 89 → **101**; hermetic
  **74/74 — 8126** (8082 + 27 + 17 exactly, so no other suite moved); FOUR mutants, each killed at its own
  assertion. Uncommitted; `git status` is the authority.**
  - **The rest of this record moved verbatim to [docs/custom-functions-design.md](docs/custom-functions-design.md) §Appendix (2026-09-18).**

- **⚠⚠ `const_arg` IS GONE AGAIN (2026-08-29, same day, user-driven: "i am seriously believing we don't
  need the const_arg fallback" — measured, agreed, REMOVED; committed as `6897c8c`, unpushed).** Every probed case works BARE
  through the correlated text channel: strings/casts/expressions/LISTs/STRUCTs, `getvariable(…)`, and —
  the case expected to need the wrapper — PREPARED PARAMETERS (`f(t.n, ?)`: DuckDB re-binds each EXECUTE
  and the re-parsed `$1` binds against the executing binder's parameter values; successive EXECUTEs may
  resolve DIFFERENT schemas — gated). With it went EVERYTHING stateful: the `CapturedConstants` registry,
  the consumed+unreferenced lifecycle, the purge, the "expired" error, the residual race — what remains is
  a stateless `LateralConstants.Validate` + `TryFoldRenderedConstant`. ⚠ Do not re-add the wrapper
  speculatively: a future rendering drift fails LOUDLY at bind and the gate catches it at the next pin
  bump. ⚠ Removal found one more v80-class Arrow hostility: an UNTYPED NULL constant slot crashed the args
  marshal ("Length must equal null count") — carried as a typed NULL now. Gate `verify_lateral` 235 → 247,
  hermetic floor → 8082. Full record: lateral_unnest_analysis.md §9.1. The superseded entry below records
  the capture design's build day; its lifecycle findings remain valid DuckDB-binder facts.
- **⚠⚠ LATERAL BIND-TIME CONSTANTS — `Params.Constant("name")` + the host scalar `const_arg(…)` — BUILT
  2026-08-29 (C++ + C#, NO ABI bump; the `constant` param style is an additive metadata value). Full
  record: [docs/lateral_unnest_analysis.md](docs/lateral_unnest_analysis.md) §9. Gate: `verify_lateral`
  168 → 211 (hermetic floor 8003 → 8046), one mutant killed at its own assertion. Committed as `2e41b2a`
  (+ the §5 doc pointer `8c17f3b`), unpushed — `git log @{u}..HEAD` is the authority.** A lateral function's output schema can now depend on a per-call constant: the author declares
  `Params.Constant("fields")` and reads the typed value in `Bind`'s `args`; the slot is stripped from the
  input schema and from every `Session.Call` chunk. Callers: a BARE constant in the literal shape
  (`f(7, 'x,y')` — the folded value reaches the bind via `input.inputs`), `const_arg('x,y')` in the
  correlated shape (its v80 bind parks the value in `CapturedConstants` and smuggles the key through its
  result TYPE — `STRUCT("__fab_const_<md5>": VARCHAR)` — past the binder's args→input-relation rewrite).
  - **The rest of this record moved verbatim to [docs/lateral_unnest_analysis.md](docs/lateral_unnest_analysis.md) §Appendix (2026-09-18).**

- **⚠ THE FUNCTION-CONTRACT CLEANUP — THREE COMMITS 2026-08-28 (`8d3dbed`/`167030c`/`5c65fa8`), C#-only,
  NO ABI change, BREAKING for plugin authors (no aliases — the `IArrow*` precedent). Full record:
  [docs/abi-history.md](docs/abi-history.md) §The function-kind naming normalization + the v80 SUPERSEDED
  note.** What a session needs to know:
  - **The rest of this record moved verbatim to [docs/abi-history.md](docs/abi-history.md) §Appendix (2026-09-18).**

- **⚠⚠ THE ARROW STREAM'S ERROR STRING WAS READ AFTER THE STREAM WAS RELEASED — a read-after-free on
  every bind-time schema failure, latent since the file was written and made REACHABLE by the
  2026-08-24 lazy-describe fix. ✅ FIXED 2026-08-26 (C++-only, no ABI). FOUND BY THE SCHEDULED LINUX
  INTEGRATION TIER (run `32928853715`) AND STRUCTURALLY INVISIBLE ON WINDOWS, which is why the local
  service tier was green through it for two days.**
  - **The rest of this record moved verbatim to [docs/feature-history.md](docs/feature-history.md) §Appendix (2026-09-18).**

- **⚠⚠ DUCKDB PR #24157 (VARIANT over Arrow, `arrow.parquet.variant`) — THE FULL REVIEW ROUND IS ADDRESSED
  AND FORCE-PUSHED 2026-08-26; head `ae74889a` = current `main` (`a0f16b1d`) + 6 commits, their CI running.**
  Both reviewers' seven inline comments implemented; the branch lives in **`D:\repos\duckdb-fix`**
  (remote `fork` = cmettler/duckdb; safety pointer `pre-rebase-backup` = the old head `a7d238f5`).
  - **The rest of this record moved verbatim to [docs/variant-support.md](docs/variant-support.md) §Appendix (2026-09-18).**
- **⚠⚠ CATALOG DISCOVERY ENUMERATED SCHEMAS IT HAD ALREADY EXCLUDED — ✅ FIXED 2026-08-25 (C#-only, no ABI).
  It made FULL ENUMERATION IMPOSSIBLE on Fabric Warehouse, and therefore every dbt run there, for sixteen
  days. Gate `verify_catalog_filter` 7 → 16 (service), mutation-tested.** User-prompted: *"can we somehow
  catch this IO Error … 'managed_delta_table_forks' is not supported"*.
  - **The rest of this record moved verbatim to [docs/warehouse-support.md](docs/warehouse-support.md) §Appendix (2026-09-18).**

- **⚠⚠ THE TIERS REPORTED AND THE SERVICE ONE FOUND A REGRESSION IN THE `fabricator_query` FIX ITSELF —
  ✅ FIXED 2026-08-24, commit `00bd30e` (C#-only, no ABI). Hermetic was 74/74 — 8003 GREEN as predicted; service came back
  52/54 with `verify_session_tag` failing at line 77, the assertion whose own comment says "this is what <!-- check-docs:ignore (verify_session_tag was deleted 2026-10-29 with fabricator_session_tag) -->
  fails without the pin, and it fails by returning NULL rather than by erroring".**
  - **The rest of this record moved verbatim to [docs/feature-history.md](docs/feature-history.md) §Appendix (2026-09-18).**

- **⚠⚠ `fabricator_query` USED TO EXECUTE ITS SQL TWICE — a shipped defect, user-found and ✅ FIXED 2026-08-24, commit `0acd679` (C#-only, NO ABI change). Gate `test/verify_raw_query.test` 27, mutation-tested (the eager stream dies at §1 with "Expected 1, Actual 2").**
  MEASURED: `SELECT * FROM fabricator_query('db','INSERT INTO dbo.twice VALUES (1); SELECT 1 AS x')` left
  **2 rows**; the same INSERT through `fabricator_exec` left 1 and reported `affected = 1`.
  - **The rest of this record moved verbatim to [docs/feature-history.md](docs/feature-history.md) §Appendix (2026-09-18).**

- **SQL SERVER CDC — THE DESIGN LIST IS EMPTY: every slice is built, including `'fill'`/`'null'`, which
  §15.9 had scheduled "last, if ever" and the user asked for on 2026-08-25. Read §16, §17, §18, §19, §20,
  §21, §22 AND §24-§27 (as built) BEFORE §15,
  because between them they REFUTE eight of §15's claims and RE-SCOPE its slice list — §20.2 in particular
  kills the exactly-once step §5 is built around, and §22.6 RETRACTS a follow-on §21.2 proposed.**
  As-built:
  §13 (slice 1), §14 (slice 2), **§16 (slice 3, the reader)**,
  **§17 (slice 4, the hidden name + ownership marker + `enable := true`)**,
  **§18 (slice 5, `_capture_instance` + the in-window DDL check, which absorbed slice 6)**,
  **§19 (slice 7, the two-instance boundary)**, **§20 (slice 8, the initial-snapshot leg)**,
  **§21 (slice 9, the timestamp bounds + `images := 'both'`)**, **§22 (slice 8b, `resync`)**,
  **§24 (`_changed_columns`)**, **§25 (`inc_position`/`dec_position`)**, **§26 (`ddl_history()` /
  `lsn_time_mapping()`)**, **§27 (`'null'`/`'fill'`)**;
  [docs/abi-history.md](docs/abi-history.md) §v81 for the ABI entry slice 2 needed. Slices 3, 4, 5, 7, 8,
  9 and 8b needed **NO ABI change** — all seven are C#-only.
  - **The rest of this record moved verbatim to [docs/mssql-cdc.md](docs/mssql-cdc.md) §Appendix (2026-09-18).**

- **THE CATALOG/TABLE ABSTRACTION: `get_metadata` IS RETIRED — ✅ COMPLETE 2026-08-15 (user-directed).
  Every slice (1a/1b/2/3, 4a–4d, 5) is BUILT; the design, the eight load-bearing subtleties (§3), the
  migration order and every per-slice as-built note live in
  [docs/catalog-table-abstraction.md](docs/catalog-table-abstraction.md), whose Appendix now also holds
  this entry verbatim (2026-08-23). READ §3 BEFORE TOUCHING ANY OF IT.** The user's framing: no
  provider-specific function defined in C++; an `ITable` like `ITableFunction` but binding a TRANSACTION
  (+ the AT clause) instead of args; clean breaks, no compat layers; state owned where it belongs. As
  shipped: catalog discovery is five dedicated typed ABI entries, per-table work is a
  `table_open/schema/info/stats/scan/alter/close` SESSION (ABI v72/v73/v74), and the 16-kind multiplexer,
  `scan_table` and the kind enum are GONE.
  - **The rest of this record moved verbatim to [docs/catalog-table-abstraction.md](docs/catalog-table-abstraction.md) §Appendix (2026-09-18).**
- **`ViewDefinition` — a PROVIDER-DECLARED VIEW. ✅ BUILT 2026-08-19 (ABI v77, ADDITIVE; C++ + C#),
  user-raised. Gates: `verify_views_catalog` **59** (hermetic, three mutants each killed at its own section)
  + `verify_functions` 27 → **34** (service) for the SECOND provider — mutation-tested.
  Full record: [docs/macros-and-sqlgen-functions.md](docs/macros-and-sqlgen-functions.md) §5.** A provider
  declares one complete `CREATE VIEW` statement (`IBackend.CatalogViews`); the host parses it and binds it
  into the ATTACHed catalog's schema, where it resolves as an ordinary relation `db.schema.v`.
  - **The rest of this record moved verbatim to [docs/macros-and-sqlgen-functions.md](docs/macros-and-sqlgen-functions.md) §Appendix (2026-09-18).**
- **SCAN + WRITE CONCURRENCY — all of it, with every measurement and every wrong turn, lives in
  [docs/scan-concurrency.md](docs/scan-concurrency.md) (§1–§10). Compressed out of here 2026-08-23.**
  Status: **DONE and measured.** A scan uses every thread (`MaxThreads()` was hardcoded to 1 — §2, ~1.95x
  CPU-bound and ~11% WORSE on a merge-bound GROUP BY, say both); order-preserving plans get a parallel
  collector (`get_partition_data` — §5, 2729 → 1197 ms streaming, 5133 → 1859 ms unioned); a scan whose
  PULL blocks hands its worker back instead of parking on the mutex (§5f, two managed scans unioned
  2.06 → 1.03 s); and **every write sink declares itself parallel** (§7c — CTAS 3.49 → 2.13 s, INSERT
  3.29 → 1.98, DELETE 3.22 → 1.36, UPDATE 7.15 → 5.00, COPY 3.41 → 2.07 with `preserve_insertion_order`
  off). `order_preservation_type` was analysed and **NOT adopted** (§7d), and the managed→host wake for a
  parked sink is **CLOSED by measurement, not deferred** (§7c — a co-tenant loses ~11–23%, which is
  ordinary contention, not starvation).
  - **The rest of this record moved verbatim to [docs/scan-concurrency.md](docs/scan-concurrency.md) §Appendix (2026-09-18).**
- **THE HOST-FS PATH NOW HAS A PER-IO INSTRUMENT, AND IT IMMEDIATELY FOUND THAT THE ONELAKE MICRO-GET FIX HAD
  NEVER REACHED IT — BUILT 2026-08-20 (C#-only, no ABI), user-directed. Full record:
  [docs/filesystem-bridge.md](docs/filesystem-bridge.md) §Per-IO instrumentation.**
  `Fabricator.Host.Fs`, Debug-gated, on `DuckDbTableFileSystem` (`list` WITH the entry count, `open`,
  `exists`, `read-all`, `delete`), `DuckDbRandomAccessFile` (`read @off+len`, `read-ranges xN`) and — only
  for what it does ITSELF via the SDK — `S3CommitFileSystem` (the conditional PUT's **won/LOST**, and
  `rename-dir`). ⚠ Its delegating members are deliberately NOT instrumented: they forward to the inner host
  FS, which logs to the same category, so doing both would double every line of a timeline.
  - **The rest of this record moved verbatim to [docs/filesystem-bridge.md](docs/filesystem-bridge.md) §Appendix (2026-09-18).**
- **`catalog_init` — THE PROVIDER INIT HOOK. ✅ BUILT 2026-08-20 (ABI v78, ADDITIVE; C++ + C#), user-directed
  ("we need a function on backend at attach time where context/session is set to do some init before the
  other gettables and so on are called"). Gate `verify_catalog_init` 14 (hermetic), mutation-tested. Full
  record: [docs/abi-history.md](docs/abi-history.md) §v78.** `IBackendCatalog.Initialize()` (a DIM no-op),
  called from `LoadCatalog` right after `FabricatorSetActiveTxn` and BEFORE every discovery crossing.
  - **The rest of this record moved verbatim to [docs/abi-history.md](docs/abi-history.md) §Appendix (2026-09-18).**

### From CLAUDE.md §Next up — record heads 2026-08-18 … 2026-08-04

- **A MANAGED HTTP CALL GOES THROUGH DuckDB'S OWN STACK — BUILT 2026-08-18 (ABI v76, ADDITIVE; C++ + C#),
  user-directed. Full record: [docs/http-transport.md](docs/http-transport.md), whose Appendix also holds
  this entry verbatim (2026-08-23); gate `verify_http_transport` 21 (service tier).** `DuckDbHttpHandler` is
  an ordinary .NET `HttpMessageHandler` whose transport is `HTTPUtil`, so a PLUGIN calling a REST API
  inherits the `TYPE http` secret whose SCOPE covers the URL, plus `ca_cert_file`, `http_proxy*`,
  `http_timeout` and the retry knobs, instead of carrying its own. SQL surface
  `fabricator_http_request(url [, method :=] [, headers :=] [, body :=])`. It lives in
  `Fabricator.Abstractions`, so a plugin reaches it with the reference it ALREADY HAS.
  - **The rest of this record moved verbatim to [docs/http-transport.md](docs/http-transport.md) §Appendix (2026-09-18).**
- **THE PLUGIN SYSTEM — scan report, default root, recursive search, INSTALL and UNINSTALL: ALL BUILT
  2026-08-18 (C#-only, no ABI). Full record moved verbatim to
  [docs/plugin-system.md](docs/plugin-system.md) Appendix (2026-08-23).** A default root
  `~/.duckdb/fabricator/plugins` (`FABRICATOR_PLUGIN_DIR` still wins and REPLACES it), a RECURSIVE search
  (it was top-level only, so an installed layout was never seen), `SELECT * FROM fabricator_plugins()`,
  `fabricator_install_plugin(archive [, root :=] [, replace :=])` +
  `fabricator_uninstall_plugin(name [, version :=] [, root :=])` behind the
  `fabricator_allow_plugin_install` setting, and a REFUSAL when a plugin claims a registered provider's
  name. Gates: `verify_plugin` 97 + `verify_plugin_install` 45 (service, mutation-tested) + tier-0
  `PluginPathsTests`/`PluginPackageTests`.
  - **The rest of this record moved verbatim to [docs/plugin-system.md](docs/plugin-system.md) §Appendix (2026-09-18).**
- **USER-DIRECTED 2026-08-18: (1) REMOVE THE MFR SPIKE — ✅ DONE (ABI v75); (2) SPLIT `Fabricator.Bridge`
  BY ASSEMBLY — ✅ PHASE A DONE (`Fabricator.Delta`), PHASE B (`Fabricator.Fabric`) SCOPED.** The facts are
  recorded here because they were established by measurement in-session and are not derivable from the code.
  - **The rest of this record moved verbatim to [docs/feature-history.md](docs/feature-history.md) §Appendix (2026-09-18).**

- **EVERY DELTA TABLE WAS PLANNED WITH NO CARDINALITY — FIXED 2026-08-17 (C#-only, no ABI; user-flagged).**
  `DeltaTableBinding.ApproximateRowCount()` returned `null`, so `FabricatorScanCardinality` returned
  `nullptr` and `EXPLAIN` showed **no `~N rows` line at all** on any Delta scan (verified by mutation — the
  estimates vanish from the plan entirely). It now sums the LOG: `add.GetNumRecords()` per active file minus
  `add.DeletionVector?.Cardinality`, so it opens **no data file and no DV file**, and it is **EXACT** despite
  the interface's name because the log is the authority on which rows are live. Gate
  `verify_delta_statistics` **27** (hermetic), two mutants each killed at its own section.
  - **The rest of this record moved verbatim to [docs/delta-catalog.md](docs/delta-catalog.md) §Appendix (2026-09-18).**

- **THE DELTA NATIVE READ PATH — the whole performance story, and it is LONG: moved verbatim to
  [docs/delta-read-path.md](docs/delta-read-path.md) (2026-08-23).** Read it before touching
  `DeltaNativeReader`/`BatchPlan`, before quoting any remote figure, and before adding a fifth form.
  What happened, in one paragraph: the profiled Fabric `LIMIT 1` went **291 s → 8.8 s** across a dozen
  C#-only fixes — the per-transaction table cache, the AT-version double build, the bind-time schema
  probe, preferring the cheap `schema`-map form over `union_by_name`, pushing the bare `LIMIT` and then
  `ORDER BY … LIMIT`, echoing literal globs with zero IO, buffering a ≤16 MB checkpoint in ONE request
  instead of ~63 micro-GETs, seeding known file sizes + declaring immutability, and adding the union and
  partition-only batched forms. There are now **FOUR batched forms**, routed by the PROJECTION rather
  than by the table.
  - **The rest of this record moved verbatim to [docs/delta-read-path.md](docs/delta-read-path.md) §Appendix (2026-09-18).**
- **PROVIDER SETTINGS ARE NOW SESSION-SCOPED — ABI v69, 2026-08-11 (C++ + C#). Before this a `SET` in ONE
  DuckDB connection changed the DATA another connection saw.** Full record:
  [docs/settings-architecture.md](docs/settings-architecture.md) §5.3.
  - **The rest of this record moved verbatim to [docs/settings-architecture.md](docs/settings-architecture.md) §Appendix (2026-09-18).**

- **`mssql_mars` IS NOW RESOLVED PER CONNECTION (change B, 2026-08-11, C#-only — no ABI, no C++).** It was
  the LAST setting still baked at first connect; every other one is already read at use time, which is what
  made A enough for them and made this a one-setting job.
  - **The rest of this record moved verbatim to [docs/settings-architecture.md](docs/settings-architecture.md) §Appendix (2026-09-18).**

- **THE AMBIENT CREDENTIAL NOW REACHES THE DELTA CATALOG ON FABRIC COMPUTE (2026-08-11, C#-only) — before
  this, EVERY secret-less notebook attach to `abfss://…onelake…` had a NON-ATOMIC COMMIT.** `_adlsCredential`
  came only from the base64 marker `DeltaBackend.BuildConnectionString` appends from the secret the ATTACH
  *names*, so the documented credential-free Fabric story arrived at `TableFileSystems.Create` with null and
  fell through to the host FS — duckdb-azure, whose `ExclusiveCreate` is a client-side existence check, not a
  conditional PUT (the measured 41-of-48 shape). `DeltaCatalog`'s constructor now adopts
  `FabricCredentialResolver.AmbientChain()` when the root is OneLake, no secret was named, and
  `FabricNotebookCredential.IsAvailable`.
  - **The rest of this record moved verbatim to [docs/warehouse-support.md](docs/warehouse-support.md) §Appendix (2026-09-18).**

- **SqlClient PINNED TO 7.0.2 (2026-08-10, user-asked; was 6.0.2) — AND IT NEEDED A SECOND PACKAGE THAT NO
  CI TIER WOULD HAVE DEMANDED.** `Microsoft.Data.SqlClient` 7.0 **MOVED the Entra (Azure AD) authentication
  providers out of the core package** into `Microsoft.Data.SqlClient.Extensions.Azure`, so every
  `Authentication=Active Directory …` connection string — Fabric Warehouse via a service principal, the
  Fabric SQL endpoint, Azure SQL with Entra, the ambient notebook token — fails at CONNECT with *"Cannot find
  an authentication provider for 'ActiveDirectoryServicePrincipal'"*. Added at the same version (they ship as
  a matched pair), which forced **`Azure.Identity` 1.13.2 → 1.21.0** (the extension requires ≥ 1.18.0; the
  downgrade surfaces as `NU1605`, an error here because warnings-as-errors is on).
  - **The rest of this record moved verbatim to [docs/feature-history.md](docs/feature-history.md) §Appendix (2026-09-18).**
- **⚠ A PINNED SCAN FEEDING A SAME-CATALOG BULK WRITE IS BROKEN WHENEVER MARS IS OFF — mapped 2026-08-10,
  then largely FIXED by the bulk deferral the same day. The settings × engine × statement matrix (11 rows,
  every one measured live) is [docs/transactions.md](docs/transactions.md) §5.6a, which also holds this
  entry verbatim in its Appendix (2026-08-23).** Symptom on Fabric: `BEGIN; INSERT INTO wh.t …; CREATE TABLE
  wh.u AS SELECT … FROM wh.t; COMMIT;` dies after ~30 s with `Execution Timeout Expired` or an SSL/TLS
  handshake failure, and the aborted transaction then reports the far less helpful `208: Invalid object
  name`.
  - **The rest of this record moved verbatim to [docs/transactions.md](docs/transactions.md) §Appendix (2026-09-18).**
- **THE SAME-CATALOG READ+WRITE MARK NAMES THE SINK — the PROVIDER decides (2026-08-10, C++ + C#, no ABI
  bump). Full record: [docs/transactions.md](docs/transactions.md) §5.9.** The host emitted
  `"materialize":true`; it now emits `"sink":{"schema","table","kind"}` and `SinkRequiresDrainedScan` answers
  on the provider side. Presence alone carries what the boolean said (`ScanSpec.HasSink`).
  - **The rest of this record moved verbatim to [docs/transactions.md](docs/transactions.md) §Appendix (2026-09-18).**
- **THE FABRIC `COPY INTO` LOAD PATH — BUILT 2026-08-10 as an OPT-IN (`mssql_copy_into_staging` / the
  `copy_into_staging` ATTACH option; C#-only, no ABI). Full record:
  [docs/transactions.md](docs/transactions.md) §5.9.** Name a storage location this extension may stage
  temporary parquet in and a bulk write on a WAREHOUSE engine writes it there with DuckDB's own parallel
  writer (`COPY … TO <dir> (FORMAT parquet, PER_THREAD_OUTPUT true)`), then loads the folder with ONE
  `COPY INTO` — the rows never cross TDS. New: `Fabricator.Bridge/OneLakeStagingLocation.cs`,
  `Fabricator.Bridge/HostParquetStaging.cs` (provider-agnostic), `Fabricator.SqlServer/WarehouseCopyInto.cs`.
  - **The rest of this record moved verbatim to [docs/transactions.md](docs/transactions.md) §Appendix (2026-09-18).**
- **CROSS-STATEMENT READ STABILITY — BUILT 2026-08-09 as an OPT-IN (`mssql_read_isolation` / the
  `read_isolation` ATTACH option; C#-only, no ABI). Full record: [docs/transactions.md](docs/transactions.md)
  §5.8.** `BEGIN; SELECT count(*) FROM t; SELECT count(*) FROM t; COMMIT;` used to return DIFFERENT answers
  under a concurrent committer with **no setting that changed it**. MEASURED: unset **3 → 4**, `'snapshot'`
  **4 → 4** while the writer committed a fifth row (its own count is the control).
  - **The rest of this record moved verbatim to [docs/transactions.md](docs/transactions.md) §Appendix (2026-09-18).**
- **A SINGLE STATEMENT IS NOT A READ-CONSISTENCY BOUNDARY EITHER, on box without RCSI — MEASURED 2026-08-09,
  12 trials. Full record: [docs/transactions.md](docs/transactions.md) §5.7; limitation 1.17.** One statement
  referencing a table twice reported **150000 and 155000**, and a single scan returned a FRACTION of one
  committed transaction (**2036 / 2376 / 3770** of 5000 rows).
  - **The rest of this record moved verbatim to [docs/transactions.md](docs/transactions.md) §Appendix (2026-09-18).**
- **READ+WRITE OF ONE CATALOG IN ONE STATEMENT FAILED ON BOX AT SCALE — FIXED 2026-08-08 (C++ + C#, NO ABI
  bump). `INSERT INTO t SELECT … FROM t` raised `595: Bulk Insert with another outstanding result set
  should be run with XACT_ABORT on`.** Full record: [docs/transactions.md](docs/transactions.md) §5.1.
  Cause: the bulk session pins the connection at operator INIT, before the scan streams, so with MARS on the
  scan reuses that connection and the bulk copy starts with the reader still open.
  - **The rest of this record moved verbatim to [docs/transactions.md](docs/transactions.md) §Appendix (2026-09-18).**
- **`delta.checkpointInterval` WAS ACCEPTED, STORED AND IGNORED — FIXED 2026-08-08 (one EW patch).**
  MEASURED: `CREATE TABLE … WITH ("delta.checkpointInterval" = '25')` writes the value into the v0 `metaData`
  and the table was still checkpointed at **10 and 20**. It is in `DeltaWithOptions.CanonicalKeys`, so we
  advertise it on the `WITH` surface AND persist it where another engine reads and believes it — which makes
  ignoring it worse than not accepting it: a table declaring 100 got ten times the checkpoint objects its
  owner asked for. `DeltaTable` now resolves the interval from `snapshot.Metadata.Configuration`, falling
  back to the code-level `DeltaTableOptions.CheckpointInterval`.
  - **The rest of this record moved verbatim to [docs/ew-master-migration.md](docs/ew-master-migration.md) §Appendix (2026-09-18).**
- **VACUUM WAS BLIND BELOW THE TABLE ROOT — FIXED 2026-08-08 (C# + one EW patch). On a PARTITIONED table it
  reclaimed NOTHING.** `ITableFileSystem.ListAsync` globbed `<root>/<prefix>*` — ONE LEVEL — in all three of
  our filesystems, each with a comment justifying it as *"the Delta log is flat"*. True of the log; false of
  the OTHER caller. `VacuumExecutor` lists the whole table ROOT to find files no version references, so it
  only ever collected orphans sitting at the root, and everything under `col=value/` grew forever.
  - **The rest of this record moved verbatim to [docs/ew-master-migration.md](docs/ew-master-migration.md) §Appendix (2026-09-18).**

### From CLAUDE.md §Next up — the streaming/buffering audit and MERGE INTO

- **THE STREAMING/BUFFERING AUDIT — STARTED 2026-08-07; `Materialize` DONE, the rest still open. A
  whole-codebase pass over EVERY path that holds batches.** The UPDATE work (grouped flush → unboxed input)
  kept turning up buffering that nobody had decided on, so the remaining ones get looked at deliberately
  rather than one at a time when they hurt.
  - **⚠ THE SHAPE THE USER WANTS, and it is a standing rule for new code, not just this audit:
    `IAsyncEnumerable` consumed with `await foreach`, with the STATE HELD BY THE CODE THAT YIELDS THE BATCHES
    and released once everything has been yielded.** So the producer owns its resources for exactly the
    enumeration's life (an iterator's `finally` / `await using` is the release point), and no consumer
    accumulates a list to keep something alive. Where a consumer genuinely needs the whole set at once (EW's
    `WriteAsync` commits over all batches), that requirement must be stated at the seam rather than met by a
    silent collect upstream.
  - **`Materialize`'S IPC COPY IS GONE — DONE 2026-08-07 (C++ + C#, no ABI). MEASURED: peak working set
    427 MB → 232 MB (−46%) on a 1.5M-row partitioned collect-path INSERT, time flat (8.8 s → 8.6 s),
    byte-identical data.** `DeltaWriter.Materialize` used to write every batch into a `MemoryStream` and read
    them back, documented as needed because *"the source batches may be freed after consumption"*. It now
    RETAINS them; `FABRICATOR_MATERIALIZE_COPY=1` restores the old path.
    - **The justification was false, and the Arrow C data interface is why**: a consumed `ArrowArray` is the
      CONSUMER's property. Our own producer implements exactly that (`ArrowProducer::GetNext` moves the batch
      out of its queue — *"ownership transfers to the consumer"* — and `Release` frees only what is STILL
      QUEUED), and `PushBatch`'s array is imported by `CArrowArrayImporter.ImportRecordBatch`, which takes
      ownership. `ChannelArrowStream` disposes nothing it has yielded; `BulkSession`'s drain disposes only what
      is still IN the channel.
    - **⚠ NONE OF THAT SETTLED IT, DELIBERATELY — the verification is the reusable part.** A use-after-free
      here is SILENT on Windows and Linux (exactly how the `ArrowProducer::Release` mutex bug hid until macOS
      CI ran it), so green suites and correct data prove nothing. New **`ArrowLiveness`**
      (`src/fabricator/arrow_produce.cpp`, `FABRICATOR_ARROW_LIVENESS=1|2`, off by default) INTERPOSES the
      release callback of every batch handed to the managed side — the standard C-data-interface wrap: stash
      the original callback + `private_data`, restore them before delegating — and ATTRIBUTES each free.
      - **COUNTING (level 1), swept over the hermetic suites: 1292 batches handed out, `released_by_producer=0`,
        `double_released=0`, zero suites with a bad verdict.** A `ProducerFreeScope` thread-local marks
        `ArrowProducer::Release`/the destructor, so a free fired from OUR side is distinguishable from the
        consumer disposing what it owns.
      - **⚠ COUNTING ALONE CANNOT ANSWER THE QUESTION, which is why there is a level 2.** "Released once" is
        equally true of a batch freed BEFORE the write read it. At level 2 the consumer prints its own markers
        to the same stderr, so the interleaving decides. MEASURED: all three handouts, then `materialize
        retained 3 batches`, then `write BEGIN` … `write END`, and only THEN the three releases. Nothing is
        freed while the write is reading.
      - Reproduce: `FABRICATOR_ARROW_LIVENESS=2` on a partitioned codec INSERT (the collect path); or the
        level-1 sweep, `for s in $(./scripts/list-hermetic-suites.sh); do unittest --test-dir . "$s"; done |
        grep handed_out`.
    - **⚠ THE INSTRUMENTATION WAS FIRST PUT IN THE WRONG PLACE, and a zero nearly read as a clean result.**
      `ArrowProducer::GetNext` is NOT the path that feeds `Materialize` — a bulk write goes through
      `PushBatch` into the C# channel, and the producer's queue serves only `RETURNING`/modify/function args.
      The first armed run reported `handed_out=0`, which is indistinguishable from "nothing was freed". The
      positive control (does the registry print at all?) is what caught it. **Instrument the seam the data
      actually crosses, and never accept a zero without one.**
    - **⚠ THE CONTROL RUN FOUND A SECOND THING: THE OLD PATH LEFT THE ORIGINALS UNRELEASED.** With
      `FABRICATOR_MATERIALIZE_COPY=1` the counters read `handed_out=3 released=0` — the source batches are
      never deterministically freed, so peak memory held the ORIGINALS, the serialized `MemoryStream` AND the
      decoded copies at once. That is most of the 195 MB the change gives back. (`released=0` is at `atexit`;
      a finalizer may reclaim them eventually — the claim is that release was not DETERMINISTIC, not that they
      leaked forever.) In retain mode the releases fire after `write END`, i.e. EW's `WriteAsync` disposes the
      batches it was given — so retaining also makes the disposal deterministic.
    - ⚠ Across the sweep `released` (776) is well below `handed_out` (1292), so ~40% of handed-out batches are
      not deterministically released on OTHER paths either. Not chased here; it is a lead for the rest of the
      audit, not a defect anyone has demonstrated.
    - **⚠ Empty batches are still SKIPPED and that is load-bearing** — engineered-wood writes one parquet file
      per input batch, so passing a zero-row batch through would commit an empty data file.
    - Gate: the whole hermetic tier at **67/67 — 6661, IDENTICAL to the pre-change counts**, which is the
      behaviour-preservation claim; plus the liveness sweep above. The registry SHIPS (env-gated, like the
      `Fabricator.Memory` marks and for the same reason): the removal rests on a measurement taken on ONE
      platform, so a macOS or foreign-producer surprise should be one environment variable away from being
      isolated rather than a rebuild.
  - Ladder to price each remaining site against: **retain = 0 copies** (what `Materialize` now does),
    **`ArrowCompute.Take` = 1 copy** (new buffers, type-agnostic incl. nested/extension — what
    `ParseUpdateStream` now uses), **IPC round-trip = 2 copies + serialization**.
  - **⚠ THE BUFFERED READ-BACK SILENTLY IGNORED THE CATALOG'S `native_read` — FIXED 2026-08-07 by giving
    `ReadRowsByRowIds` the catalog's flag (both call sites are in `DeltaCatalog`, so it was bounded). Original
    finding, kept because the reasoning is what generalises:**
    `ReadRowsByRowIdsAsync` opens with a bare `DeltaWriter.Options()` (`DeltaReader.cs:974`), passing **no
    `dataFileReader`**, so a buffered UPDATE's read-back takes the EW CODEC reader even on a
    `PROVIDER 'delta'` catalog where the user's `native_read` is on. Two consequences: it is the wrong engine
    for what the attach asked for, and **the codec reader yields ONE BATCH PER ROW GROUP** where DuckDB's
    `read_parquet` yields 2048-row vectors — measured 30 flushes vs 1 on a 60k-row UPDATE, and a 300k-row
    control giving exactly 3 batches at the 122880 default. That is what makes the UPDATE grouped flush inert
    on that path, and it means the buffered read-back materialises a whole row group at a time on the path a
    dbt `BEGIN…COMMIT` model takes.
    - Fix = pass the catalog's reader (and writer, and write spec) into that open — **the SAME structural
      change the write-options revisit needs for the spec, so do them together.** Then re-measure the grouped
      flush on the buffered path; it should start behaving like autocommit.
    - ⚠ Sweep the other bare `DeltaWriter.Options()` opens in `DeltaReader` for the same question — the
      grep is ~30 sites and this one was found by accident, not by looking.
  - **THE SQL READER NOW RELEASES AT END OF RESULT SET — DONE 2026-08-09 (C#-only, no ABI). This is the
    "eager-close entry" the read+write item points at.** `DbDataReaderArrowStream` closed its reader only in
    `Dispose()`, while the consumer releases the exported stream from the scan's global-state DESTRUCTOR —
    query teardown, not pipeline end — so a fully-drained scan held a SQL Server result set open for the rest
    of the statement. It now releases reader/command/(owned) connection the moment the result set ends. Full
    record: [docs/transactions.md](docs/transactions.md) §5.3.
    - The read loop now distinguishes "this batch is full" from "the result set ended", so EOF is caught on
      the LAST DATA BATCH rather than one pull later; `Release()` sits behind a `_released` flag.
    - **⚠ IDEMPOTENCY IS LOAD-BEARING, NOT DEFENSIVE** — every drained scan now releases TWICE (eagerly, then
      when the consumer releases the exported stream), so the flag makes the ORDINARY path correct. The whole
      service tier is the test.
    - **⚠ SOUND ONLY BECAUSE THE BATCH OWNS ITS MEMORY**, which is the invariant to protect: the
      `DbDataReader` indexer builds a fresh object per row (`GetValue`, never a reusable caller buffer — we
      use neither `GetBytes`/`GetChars` nor `SequentialAccess`) and every `ColumnAppender` copies into Arrow
      builder buffers. An appender gaining a zero-copy path would make this a use-after-free.
    - **⚠ IT DOES NOT COVER AN EARLY-ABANDONED SCAN** (`LIMIT`, a short-circuiting join, the bind-time schema
      scan — which is visible in the log releasing at `dispose`), and **does NOT supersede the materialise fix
      for 595**, where the scan feeds the bulk row by row so the reader is open by construction.
    - **It BRINGS THIS CLASS IN LINE WITH `DaxArrowStream`**, and that is a bug avoided rather than a
      tidiness win: the old loop returned a partial batch without setting `_done`, so the next pull read PAST
      EOF. Idempotent on `SqlDataReader`; `docs/dax-provider.md` records months lost to that exact call
      THROWING on ADOMD, which is why DAX has its own stream class.
    - Gate: service **46/46 — 1746, identical to pre-change** ⇒ behaviour-preserving, plus a Debug
      `reader released (eof|dispose)` line as the positive control (the change is otherwise invisible from
      SQL — on a self-join the release now falls BETWEEN the two scans).
  - Other paths the audit must cover, not only `Materialize`: `pending.Batches` (the buffered park),
    `BulkSession`'s bounded channel (already streaming — confirm, do not assume), the CDF capture,
    `DeltaWriter.Write`'s batch list, `ArrowDataReader`, and the collector/in-out sessions. The
    `Fabricator.Memory` marks exist to make each one answerable rather than arguable.
- **`MERGE INTO` — BUILT + GATED 2026-08-05 (C++-only, no ABI bump). Full record moved verbatim to
  [docs/feature-history.md](docs/feature-history.md) §MERGE INTO (2026-08-23).** One override,
  `FabricatorCatalog::PlanMergeInto` (`src/catalog/fabricator_merge_into.cpp`), lifted the shared refusal
  for EVERY provider at once; DuckDB lowers each action to the same `Logical{Update,Delete,Insert}` the
  standalone statement produces, so MERGE INHERITS our rowid DML rather than re-deriving it. Gates
  `verify_merge_into` **209 × 2 engine legs** (hermetic) + `verify_merge_into_mssql` **106** (service).
  - **The rest of this record moved verbatim to [docs/feature-history.md](docs/feature-history.md) §Appendix (2026-09-18).**

### From CLAUDE.md §Next up — record heads 2026-08-02 … 2026-07 (DONE features)

- **FABRIC REST API FUNCTIONS (`fabric.*`) — the whole curated set is BUILT; P0/P1/P2 + semantic models +
  XMLA live-validated, P3 WIRED BUT NOT LIVE (no git-connected workspace / pipeline / mirrored DB on this
  tenant). Full record: [docs/fabric-api-functions.md](docs/fabric-api-functions.md) — §9c as-built, §9h the
  SQL Server binding, §9j variable libraries, §9k Spark sessions, §9l the `fabric` schema move, §9m the
  fan-out verdict per function, §9n the inferred ATTACH options, §10 the API sweep with a verdict per area;
  the `CLAUDE.md` entry was moved verbatim into its Appendix 2026-08-23.** 51 functions over
  `Microsoft.Fabric.Api` (+ the Power BI REST half for semantic-model refresh), in their own **`fabric`
  SCHEMA** on a OneLake Delta attach or a Fabric SQL endpoint attach.
  - **The rest of this record moved verbatim to [docs/fabric-api-functions.md](docs/fabric-api-functions.md) §Appendix (2026-09-18).**
- **THE `_each` MOVE: the PROVIDER declares its per-row form, the host stopped inventing one — DONE
  2026-08-02 (user-directed).** `FabricatorSchemaEntry::AddTableFunction` used to synthesise a
  `<name>_each` table-in-out alias for **every `table`-kind function of every provider**. That made a
  SQL-Server semantic (CROSS APPLY / per-row EXEC) the HOST's business and produced entries that can only
  fail wherever there is nothing to apply per row — **measured: 30 of the 70 names on a Fabric attach were
  dead `_each` siblings**, all advertised in `duckdb_functions()`.
  - **The rest of this record moved verbatim to [docs/custom-functions-design.md](docs/custom-functions-design.md) §Appendix (2026-09-18).**

- **THE UNIFIED PARAMETER PROTOCOL — DONE 2026-08-02 (behaviour-preserving, no ABI bump). Full record
  moved verbatim to [docs/custom-functions-design.md](docs/custom-functions-design.md) §12 (2026-08-23).**
  A function declares **ONE parameter schema** whose every field carries its STYLE in Arrow field metadata
  (`fabricator.param_style` = `named` | `table`; ABSENT ⇒ positional), replacing a split `Parameters` +
  `NamedParameters` pair plus a third `InputSchema` on the in-out/collector kinds.
  `dotnet/Fabricator.Abstractions/ParamStyle.cs` (`ParamStyle` / `Params`) is the whole protocol; C++ reads
  it as `FabricatorParamStyle`. Why: the split forced every consumer to reconstruct one ordering rule, and
  a host that got the NULL substitution off by one would corrupt a POSITIONAL value rather than error —
  with one schema, position IS declaration order and that bug cannot be written.
  - **The rest of this record moved verbatim to [docs/custom-functions-design.md](docs/custom-functions-design.md) §Appendix (2026-09-18).**
- **⚠ CROSS-ENGINE `commitInfo.isBlindAppend` — BOTH HALVES NOW DONE (reading 2026-08-01, writing
  2026-08-08). Full record: [docs/delta-transactions.md](docs/delta-transactions.md) §10.6 +
  [docs/ew-master-migration.md](docs/ew-master-migration.md) §isBlindAppend §4a, and this entry verbatim in
  delta-transactions.md's Appendix (2026-08-23).**
  - **The rest of this record moved verbatim to [docs/ew-master-migration.md](docs/ew-master-migration.md) §Appendix (2026-09-18).**
- **⚠ REVERSED 2026-08-11 (user decision): the Delta catalog default is `write_serializable` again.** The
  entry below is the record of the 2026-08-01 flip and its measurement, ALL OF WHICH STILL HOLDS — Fabric
  Spark really does commit at `Serializable` and really does refuse to SET `WriteSerializable`, so on a
  table declaring no level we are once more the more permissive writer and the effective guarantee depends
  on which engine wrote last. What changed is which side of that trade-off we take: **row-level concurrency
  is a `write_serializable`-ONLY relaxation**, so under `serializable` concurrent disjoint-row DML on one
  file conflicts instead of composing. `isolation_level 'serializable'` on the ATTACH restores the aligned
  behaviour, and a table's own `delta.isolationLevel` still outranks the catalog either way.
  - **The rest of this record moved verbatim to [docs/delta-transactions.md](docs/delta-transactions.md) §Appendix (2026-09-18).**
- **THE DELTA ISOLATION DEFAULT FLIP — DONE (2026-08-01, behaviour-breaking for CONCURRENT writers).** The
  catalog default is now **`serializable`** (was `write_serializable`), because the measurement below showed
  the old default made us the WEAKER writer than Fabric Spark on any table that declares no level — so the
  effective guarantee depended on which engine wrote. Single-writer behaviour is unchanged; concurrent
  read-write transactions now conflict-abort against a matching blind append where they used to commute.
  Explicit `isolation_level 'write_serializable'` restores the old behaviour, and a table's own
  `delta.isolationLevel` still overrides the catalog.
  - **The rest of this record moved verbatim to [docs/delta-transactions.md](docs/delta-transactions.md) §Appendix (2026-09-18).**
- **PLAIN (non-OneLake) ADLS Gen2 SUPPORT — BUILT + LIVE-VALIDATED 2026-08-02. Full record:
  [docs/delta-transactions.md](docs/delta-transactions.md) §8.4; gate `test/verify_delta_catalog_adls.test`
  (**55**, manual/live-account tier — ⚠ this line read **140** until 2026-08-07, a number transcribed from
  the `verify_mssql_adls_polybase` gate beside it. The suite has said 55 since the commit that added it
  (`33eb3e1`) and has never changed. Caught by RUNNING it and disbelieving the shortfall; a wrong gate
  number is worse than none, because the next person reads a green 55 as a suite that aborted).** A Delta catalog on `abfss://<fs>@<account>.dfs.core.windows.net/…` —
  a plain storage account, not a Fabric lakehouse. It LOOKED like it already worked (attach, discovery,
  CTAS, INSERT, DELETE, DROP and both parquet directions through duckdb-azure all passed first try); two
  things did not.
  - **The rest of this record moved verbatim to [docs/delta-transactions.md](docs/delta-transactions.md) §Appendix (2026-09-18).**
- **ISOLATION + ONELAKE MULTI-WRITER — MEASURED LIVE 2026-07-31; one bug FIXED, one gap OPEN. Full record:
  [docs/delta-transactions.md](docs/delta-transactions.md) §8.1 (multi-writer) + §10.6 (Spark isolation).**
  Two long-standing claims in this file were wrong, and both were beliefs never measured.
  - **The rest of this record moved verbatim to [docs/delta-transactions.md](docs/delta-transactions.md) §Appendix (2026-09-18).**
- **THE DELTA NATIVE DEFAULTS FLIP — DONE (2026-07-29, behaviour-breaking for `PROVIDER 'delta'`).**
  `native_read`/`native_write` used to default **off** everywhere, so the production path was opt-in and
  the *tested* path was the pure-EW codec. Now **the provider NAME selects a default profile**
  (`DeltaBackend.NativeDefaultsFor`): **`PROVIDER 'delta'` ⇒ both ON** (DuckDB reads/writes the parquet
  bytes, EW owns the log — the hybrid we actually ship), **`PROVIDER 'engineeredwooddelta'` ⇒ both OFF**
  (pure codec). Explicit options still win on either spelling. The name reaches the backend through a
  3-arg `IBackend.OpenCatalog` **default-implementation interface overload**, so SqlServer/DAX/deltars are
  untouched — no ABI bump. The redundant alias `deltalake` was REMOVED in the same pass.
  - **The rest of this record moved verbatim to [docs/native-delta-write.md](docs/native-delta-write.md) §Appendix (2026-09-18).**
- **DELTA SNAPSHOT CACHING (perf) — PREREQUISITES DONE, the cache itself NOT BUILT and the full version
  NOT RECOMMENDED. Full design + every finding: [docs/delta-snapshot-caching.md](docs/delta-snapshot-caching.md).**
  Every table REFERENCE costs **4 snapshot constructions per statement**, dead linear in references
  (self-join 8, three references 12), each a `_delta_log` LIST that `ExternalFileCache` does not serve — so
  OneLake/S3 pay most. Shipped so far: `SnapshotPinning.Release` wired to commit/rollback (it was DEAD CODE,
  and the 4096-entry panic `Clear()` it left as the only reclamation was silently breaking snapshot isolation
  for in-flight transactions), and the host-FS opener now resolved PER CALL (`142b350`) — a cached table
  holding a stale `ClientContext*` is a use-after-free, not staleness, and would not fault on this box.
  - **The headline number is a COUNT, not a profile** — nobody has measured what the redundant opens cost in
    wall-clock. The Fabric notebook's 305 s → 15 s came from two OTHER fixes, dominated by `HostFsGlob`'s
    open-per-matched-file (258 s → 2 s). Do not call this "the biggest remaining perf item" again without
    profiling it; that inference is what the doc's decision gate exists to stop.
  - **If anything is built, cache the immutable `Snapshot` — NOT a `DeltaTable`, NOT a `NativeScanList`.**
    `DeltaTable.OpenAsync` is *entirely* "LIST the log, replay it into a `Snapshot`, wrap it in a cheap
    holder", and `Snapshot` is init-only over `IReadOnlyDictionary`. So caching it per (txn, path, version)
    captures ALL the redundant cost while every call still builds its own table — which dissolves disposal,
    the dangling opener AND the thread-safety dependency on an unenforced EW invariant. Serves BOTH engines.
    Needs one small additive EW patch (`FromSnapshot`, since the snapshot-taking ctor is private). Caching a
    live `DeltaTable` buys nothing over this and costs a lease threaded through 6 async iterators. Caching a
    `NativeScanList` is WRONG: it is post-prune, so sharing it between scans with different pushed predicates
    silently DROPS ROWS.
  - **⚠ A CORRECTNESS CONSTRAINT, MEASURED 2026-08-07 while settling a different question — THE COMMIT PATHS
    MUST NEVER BE SERVED FROM THE CACHE** ([docs/delta-snapshot-caching.md](docs/delta-snapshot-caching.md)
    §3.5a). `FlushDeferredFilesAsync` opens the table FRESH at COMMIT, and that is load-bearing rather than
    incidental: Delta conflicts on a concurrent **metaData** UNCONDITIONALLY (reads are not consulted, so
    even a blind append is examined), and the rule can only fire over the commits between the commit's BASE
    version and the version attempted. A fresh base makes that range EMPTY. Serve the flush's open from a
    cache and every append racing a property or schema edit — ours, a Spark job, an OPTIMIZE — starts
    FAILING where it commits today. MEASURED both ways on both engines: a buffered `INSERT` whose window
    contains a `fabricator_delta_set_tblproperties` commits, while a buffered `DELETE` — whose held
    `DeltaTransaction` IS pinned at statement time — conflicts with *"Concurrent commit N changed the table
    metadata."* Same table, same isolation, same edit; only the base differs. **This is a different failure
    from the staleness the invalidation list guards** — a correct answer replaced by a refusal — and it is
    the kind a change framed as "pure performance" ships without anyone looking. Gate
    `verify_delta_catalog_transactions` §41 (965 → 1021 per leg, mutation-tested with two mutants).
  - **⚠ Two traps recorded in the doc:** there is NO intra-call shortcut (the `Stream*` methods are async
    ITERATORS, so the schema open completes and disposes BEFORE the stream open begins, in a different ABI
    call), and `TableFunction::function_info` is the WRONG shelf for cached state (its lifetime is the PLAN,
    and plans are re-executed across transactions) — use `ClientContextState`/`registered_state`, or the
    existing per-txn `SnapshotPinning` structure whose `Release` is already the disposal point.
- **SINGLE-FILE DISTRIBUTION — BUILT + validated live (phases 1–4 of 5; REMAINING: user-facing install
  docs + CI matrix — CI tier 3 exists).** ONE `fabricator.duckdb_extension` self-installs (extract +
  chain-load + CLR boot; ~2–3 s cold, 0.01–0.2 s warm; win 61 MB standalone / linux 40 MB standard —
  the Fabric-notebook SKU). Build: `scripts/pack-distribution.ps1`; smoke:
  `test/distribution/smoke_distribution.py` (15 checks). Design + findings:
  [docs/distribution-installer.md](docs/distribution-installer.md) §12/§14/§15/§16. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **NativeAOT BRIDGE SKU (design only, 2026-07-25 — nothing built):
  [docs/aot-bridge.md](docs/aot-bridge.md).** An optional AOT-compiled variant of the
  managed layer (Bridge + providers → ONE native lib, `NativeLib=Shared`) beside the CoreCLR
  SKU — zero .NET prerequisite, est. 40–80 MB total. **Key audit finding: the ABI is already
  AOT-shaped** (vtable of `[UnmanagedCallersOnly]` statics both directions) — only the
  bootstrap changes: a `FabricatorBridgeInit` native export + clr_host mode 3 (managed dir
  contains `Fabricator.Bridge.Native.<ext>` ⇒ plain dlopen/dlsym, no hostfxr). The complete
  dynamic-code inventory is FIVE sites: BackendRegistry reflection discovery → a
  **`Fabricator.Generators` Roslyn source generator** (`[FabricatorBackend]` attr → emitted
  `CompiledBackends` factory in a HEAD project `Fabricator.Bridge.Native.csproj`, trim-rooted
  by construction; reflection branch behind an AppContext feature switch ILC trims away);
  the plugin ALC → **compile-time plugins** (reference + republish the head — the head IS the
  plugin config; native-plugin C-ABI + a DAX CoreCLR sidecar noted as deferred);
  `FormatError`'s `GetProperty("Number")` duck-type → `IBackend.GetErrorNumber(Exception)`
  DIM; ~10 `JsonSerializer<T>` files (+ EW `ActionSerializer`) → one source-gen
  `JsonSerializerContext`; Regex is AOT-fine as-is. **DAX/ADOMD stays CoreCLR-only** (the
  original non-AOT reason — closed-source, not AOT-able); AOT SKU = SqlServer + Delta/EW
  (**SqlClient AOT feasibility USER-VALIDATED 2026-07-25 on the 7.1 preview — the AOT SKU
  targets 7.1+**, remaining work is the version bump + our-paths coverage via the suite;
  Fluid must run interpreted). Gate = the existing SQL-level verify sweep (minus verify_dax)
  against the native bridge. Endgame option noted: `NativeLib=Static` linked INTO the C++
  loadable = literally one file, no trampoline (experimental, later). Composes with the
  distribution installer (payload → core + one native lib, no .NET probing).
- **`CREATE TABLE … WITH (…)` options + SQL Server EXTERNAL TABLES — ALL FOUR SLICES DONE (ABI v67).**
  WITH write-tuning/CREATE-flag-overrides/TBLPROPERTIES on Delta; external-table INSERT/identity-keyed
  UPDATE+DELETE routing to storage — **`s3://` AND (since 2026-08-02) `adls://`**, see the ADLS Gen2
  data-virtualization entry for why that took two gates and not one; the CETAS-analog
  `WITH (location=…, table_type=…)` DDL.
  [docs/create-table-with-options.md](docs/create-table-with-options.md); gates verify_with_options 68 +
  verify_mssql_s3_polybase 252 + verify_mssql_adls_polybase 140. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **SQL-GENERATING TABLE FUNCTIONS — DONE (ABI v68 `generate_table_sql`, global + catalog-bound).** The
  call DISAPPEARS at bind (`bind_replace` → SubqueryRef); arg-dependent schema + full pushdown for free.
  Rule: fixed SQL text + varying VALUES ⇒ macro; SQL TEXT depends on args ⇒ sqlgen.
  [docs/macros-and-sqlgen-functions.md](docs/macros-and-sqlgen-functions.md) §2; verify_sqlgen 59 +
  verify_sqlgen_catalog 30. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **PROVIDER-DECLARED DuckDB MACROS — DONE (no ABI bump; decl kind `macro` + body column).** DuckDB
  parses the full CREATE MACRO grammar; registered into the SYSTEM catalog at load; injection-free by
  construction. [docs/macros-and-sqlgen-functions.md](docs/macros-and-sqlgen-functions.md) §1;
  verify_macros 41 + verify_plugin 10. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
  - **The rest of this record moved verbatim to [docs/macros-and-sqlgen-functions.md](docs/macros-and-sqlgen-functions.md) §Appendix (2026-09-18).**
- **`hilbert_index` + `bucket` global scalars, declared scalar VOLATILITY, and the FULL LIQUID-CLUSTERING
  stack — ALL DONE, Spark-interop validated live BOTH directions** (writes to clustered tables; SORTED BY
  declares clustering; clustered OPTIMIZE incl. multi-file + partitioned partial recluster + ZCube
  incremental — Spark recognized OUR cubes as its own; `ALTER … SET/RESET SORTED BY` (alter kinds 12/13);
  `SORTED_COLUMNS` COPY option). Gate verify_delta_clustered_optimize 138 + hilbert 27 + bucket 34 +
  sorted_by 30. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **`SORTED BY` → Delta ORDERED writes — DONE.** Persists as the `fabricator.sortedBy` table property;
  INSERTs re-apply the ORDER BY via a host-side spilling sort. verify_delta_sorted_by 30. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **dbt DAX→Delta pipeline — DONE + validated live** (`dbt_dax_test/`, gitignored; plain-DAX model bodies
  via the custom `dax_table` materialization). Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **THE UPDATE POST-IMAGE GROUPED FLUSH — DONE 2026-08-06 (C#-only, no ABI). ⚠ IT DOES NOT FIX "UPDATE
  MEMORY", AND THE MEASUREMENT SAYING SO IS THE MOST USEFUL THING ABOUT IT. Full record moved verbatim to
  [docs/feature-history.md](docs/feature-history.md) §The UPDATE post-image grouped flush (2026-08-23).**
  Both UPDATE paths used to accumulate EVERY post-image batch (and every pre-image on a CDF table) before
  writing anything; they now write a group's worth as the read-back streams and keep only the actions. Still
  exactly ONE commit. Threshold `DeltaReader.UpdateGroupBytes` = 64 MiB, env-overridable.
  - **The rest of this record moved verbatim to [docs/feature-history.md](docs/feature-history.md) §Appendix (2026-09-18).**
- **Eager-write DeltaTxnBuffer — ALL SLICES DONE (A, B, C1–C3, D + edge lifts).** Data files always land
  on storage at statement time; the buffer holds ACTIONS. **"Rollback = invisible orphans for VACUUM" is
  now HISTORICAL — as of 2026-08-02 a ROLLBACK RECLAIMS the bytes, via two mechanisms with different
  owners** (full record: [docs/delta-transactions.md](docs/delta-transactions.md) §7):
  (a) the flush's transaction is `await using`, so a flush that does not commit takes back what EW's OWN
  writers staged — e.g. the deletion vector of a buffered DELETE (`StageRowDeletesAsync` writes it before
  the commit is judged). ⚠ Safe only from EW #49; at #46 the same line would have deleted COMMITTED data.
  Measured — a small delete's vector is INLINE, so the orphan only reproduces above the 1 KB roaring
  threshold. Gate verify_delta_txn_version §9 (65).
  (b) `RollbackTransaction` calls EW #52's **`DiscardDataFilesAsync`** on the eagerly-written DATA files,
  the class (a) structurally could not touch — EW's provenance rule never collects a host-written file, so
  the host has to name them. ⚠ This needed a C++ fix first: `FabricatorTransactionManager::
  RollbackTransaction` **never set an opener**, so it held a STALE `ClientContext*` — harmless while
  rollback did no IO, a use-after-free the moment it does any. It now takes its own short-lived connection
  like the commit path, and clears the opener to 0 (there is no caller context to restore). Never throws:
  a failed discard logs and leaves the orphan, i.e. the old behaviour. Gate
  verify_delta_catalog_transactions 943 → 944, mutation-tested. Both gates mutation-tested. Incl.
  S3 multi-writer conditional-PUT commits (SECRET-routed), the dbt table-swap RENAME fix, buffered
  IDENTITY/CDF/same-txn-DML, and the partitioned×native_read partition-column bug fix. Gate
  verify_delta_catalog_transactions (now 941); semantics [docs/delta-transactions.md](docs/delta-transactions.md).
  Still immediate by design: identity creates, DROP/OPTIMIZE/VACUUM, CREATE-OR-REPLACE/partition-overwrite.
  - **The rest of this record moved verbatim to [docs/delta-transactions.md](docs/delta-transactions.md) §Appendix (2026-09-18).**
- **Fabric-notebook AMBIENT AUTH — DONE + validated live.** All three providers work with ZERO
  credentials on Fabric compute via `FabricNotebookCredential` (the trident token service; per-scope
  refreshing tokens); azure `access_token` secrets consumed for SQL. Pinned gap: a STATIC storage-token
  secret cannot serve the fabric+storage audiences for abfss ATTACH — use ambient. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
  - **The rest of this record moved verbatim to [docs/warehouse-support.md](docs/warehouse-support.md) §Appendix (2026-09-18).**
- **Sync-over-async (Bridge) — DONE** (superseded note; see the entry near the top + the full record in
  docs/ew-master-migration.md). AsyncLocal ambients (`0533eb7`) keep the opener/txn across pool hops. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Discovered TVF/proc wrapper extraction — DONE** (`SqlServerProcedure` / bespoke
  `SqlServerTableValuedFunction`; dispatch unified under `IBoundTable`, v29). Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Load-time GLOBAL functions — ALL FIVE KINDS DONE (scalar/in-out/collector/table/aggregate, ABI
  v46/v47)** incl. the host-FS global table sub-case (`set_active_opener`; `fabricator_delta_scan` is
  pure C# — a new lakehouse format costs zero C++). [docs/global-functions.md](docs/global-functions.md);
  verify_global_functions 63. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **VARIANT for the Delta provider — DONE through SIX passes** (leaf-blob transport
  `ew.variant_transport` — DuckDB #24157 filed for the canonical struct; codec + FULL shredding tiers;
  DML/OPTIMIZE via the IDataFileReader seam; mapped/nested gates; Spark + kernel validated live; the
  Fabric T-SQL endpoint REJECTS VARIANT and id-mode mapping — re-measured 2026-10-24: id-mode tables are now
  SILENTLY SKIPPED rather than refused, and the endpoint also DROPS STRUCT columns in every mapping mode;
  docs/known-limitations.md §3).
  [docs/variant-support.md](docs/variant-support.md); verify variant 157. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **NESTED STRUCT-field schema evolution — DONE** (alter kinds 9–11; recursive read reconcile; the
  native_read presence probe lifted the top-level + nested limitations). verify nested_alter 100. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Delta write-side NOT NULL enforcement — DONE** (`DeltaNullability`, nested included; per-statement
  Delta commits pinned as a documented divergence). verify constraints 50. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Delta IDENTITY columns — DONE** (v53 marker `id BIGINT AS (0)`; OCC retry regenerates from the fresh
  HWM — safer than Spark). verify identity(delta) 38. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **DAX / ADOMD 2nd provider — DONE slices 1–6** (PBI Desktop + workspace XMLA + Fabric SP/ambient auth;
  scan pushdown + streaming to 10.5M rows; `system` DMV schema; `daxeval`/`daxevaltable`(collector)/
  `daxeach`; the read-past-EOF ADOMD gotcha). Read-only **for DATA** — since 2026-07-31 it also hosts a
  `CatalogFunctionSet` and the TMSL refresh trio (`dax_refresh`/`_table`/`_partition`), which move data
  INTO a model; model AUTHORING stays out (no `dax_tmsl`). [docs/dax-provider.md](docs/dax-provider.md);
  verify_dax 29 (manual — needs PBI Desktop). Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Multi-edition support (Fabric WH / Synapse / box) — DONE slices 1–6** (`ServerProfile`, MARS gating +
  connection mode, profile-driven type mapping, collation-gated ORDER BY pushdown, JSON/UUID/tz
  read-side; write-side rich types deliberately deferred).
  [docs/warehouse-support.md](docs/warehouse-support.md). Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Settings refactor — DONE, all three flavors** (settings v33/v34 + ATTACH options v37 + secret fields
  v38 — the provider is fully self-describing; `RESET` does not fire set-callbacks, restore with SET).
  [docs/provider-extensibility.md](docs/provider-extensibility.md). Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Plugin system — default-context SPI DONE** (`FABRICATOR_PLUGIN_DIR`; plugins load into the BRIDGE's
  ALC and must align their dependency closure — Apache.Arrow above all; `Fabricator.Abstractions` is the
  contract assembly; per-plugin ALC isolation deferred). [docs/plugin-system.md](docs/plugin-system.md);
  verify_plugin 10. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).

### From CLAUDE.md §C ABI contract — COPY … (FORMAT delta)

- **`COPY … TO '<path>/<table>' (FORMAT delta, …)` — DONE: path-targeted Delta write, NO ATTACH**
  (transient per-execution catalog; `MODE` = the Spark/delta-rs save-mode vocabulary incl.
  `overwrite_partitions`; repartition-on-overwrite; PARTITION_COLUMNS with every mode; its own atomic
  commit — deliberately NOT rolled back by a surrounding BEGIN). verify_delta_copy_format 109. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).

### From CLAUDE.md §CI — proven-in-CI status (2026-07-26)

**Proven-in-CI status (2026-07-26).** Tier 0 green. **Tier 1 green on ALL THREE platforms in ONE run**
(`30192450794`, sha `124ad4f`) — each independently 53/53 suites / 4152 assertions, verified from the job
logs rather than the status tick. **Tier 2 green** (`30192508662`) — 42/42 / 1221, nothing skipped,
`verify_mssql_s3_polybase` at its full 252. Both defects that the first CI runs surfaced (the macOS
`ArrowProducer` use-after-free and the undeclared `require parquet`) are fixed and confirmed IN CI, not
merely locally — a distinction this repo's history says to insist on. **`distribution.yml` is now GREEN
on all THREE platforms too** (`30195834247`): each packs the single-file artifact and passes all 12 smoke
checks against a STOCK DuckDB wheel — cold LOAD, `['fabricator','fabricator_core']` both reporting
loaded, a Delta round trip through the extracted core, the warm fast path, and both
must-not-touch-disk rejections. Artifacts upload as `fabricator-v1.5.5-<platform>-<sku>`
(windows_amd64 Standalone 62 MB / osx_arm64 Standalone 60 MB / linux_amd64 Standard 40 MB).

### From CLAUDE.md §CI — tier 1 stayed green

**Tier 1 has since stayed green across every pin bump and the `PlanFiles` work** — `01994fb` (second EW
bump) and `5c28297` (`PlanFiles`) both green on all three platforms. A green tier-1 job is a stronger
claim than it looks: `run-suites.sh` floors on the run/assertion counts and fails on any SKIP, so the
tick alone proves the counts without reading logs. **One CI gap was closed the same day**: the path
filter listed `.gitmodules` but not the submodule POINTERS, so a pin bump ran NO CI at all (see the traps
list). Note that fix is not self-proving — every commit since has also touched `dotnet/`, so it is only
exercised the next time a pin moves on its own.


### From CLAUDE.md §CI — the verify_session_tag flake (the suite was deleted 2026-10-29) <!-- check-docs:ignore (deleted suite) -->

**⚠⚠ UPDATE 2026-08-31 — THE ENTRY BELOW IS NOW WRONG ON ITS SHARPEST POINT, AND THE SUITE HAS BEEN HELD
OUT OF THE SERVICE TIER (`9cff364`).** It says *"local service runs have never reproduced it"*. They do:
it fired in a full local service tier and then again on the FIRST standalone re-run, after which four
consecutive runs were green — 1 in 4, then clean. So it is reproducible locally, which is the first handle
anyone has had on it; the CI-only framing below is what made it look un-investigable for two occurrences.
- **⚠ THE LEAD WORTH PULLING, and it is an OBSERVATION not a diagnosis: both local failures were the FIRST
  run after a preceding full tier, and everything after them was green.** That points at STATE CARRY-OVER
  (something a previous run leaves on the server or in the pool) rather than randomness. Not chased.
- **⚠ It is now UNGATED** — `require-env FABRICATOR_RUN_SESSION_TAG`, service floors 54 → 53 runs and
  3056 → 3069. The suite is UNCHANGED and runs on demand; deleting that one require-env line puts it back.
  ⚠ The cost is the whole feature's coverage, §4 (a tag set by one call, read by a DIFFERENT one) included.
- ⚠ It was NOT caused by the `fabricator_functions()` varargs work that was in flight when it fired, though
  that commit did touch the session-tag row of the declarations UNION. Four grounds: it is INTERMITTENT (a
  registration-metadata change is deterministic), the refusal is a RUNTIME transaction-state check, it had
  already fired twice on commits that cannot reach it, and the tier arithmetic reconciled exactly
  (3069 + 25 = 3094 = 3056 + 38, nothing else moved).

**⚠⚠ RECURRING CI FLAKE — IT HAS NOW FIRED TWICE, SO IT IS NOT A ONE-OFF (2026-08-23 on `4eec562`,
2026-08-26 on `e0523a1`), AND NOBODY HAS LOOKED AT IT. ITS SHAPE IS NOT A TIMING FLAKE.** The second
occurrence is BYTE-FOR-BYTE the first — same line, same statement, same message — on a run whose only
other content was the Arrow error-string fix, which cannot reach transaction state. Both were the
`integration` tier; local service runs have never reproduced it (green 3/3 and again at 54/54 — 3011
the same day). ⚠ Its cost is a RED TIER on an otherwise green run, so it will keep being mistaken for
whatever change is in flight — as it was here, where the run it reddened had in fact PROVEN its fix
(`verify_raw_query` 34 and `verify_mssql_s3_polybase` 263, both full). The scheduled `integration` run
on `4eec562` failed at `verify_session_tag.test:146` — <!-- check-docs:ignore (verify_session_tag was deleted 2026-10-29 with fabricator_session_tag) -->
`SELECT * FROM db.dbo.fabricator_session_tag('dbt_run_id','run-77')` refused with *"must be called inside an
explicit transaction (BEGIN … COMMIT)"* **on the statement immediately after a `statement ok BEGIN;`**. The
previous SEVEN integration runs were green on essentially the same tree, and a local service run minutes later
was green (52/52 — 2221), so it is intermittent rather than a regression.
- **Why it deserves its own pass rather than a re-run:** the failure is a TRANSACTION-STATE disagreement, not
  a clock. The provider learns "this is explicit" when the host calls `BeginTransaction(is_explicit)`, and a
  bare `BEGIN` touches no catalog — so the tag call is the FIRST catalog touch in that transaction. Something
  made the provider see a non-explicit transaction there. That is the same family as the recorded
  `_explicitTxns`/`IsExplicitTxn` ordering bugs (read-state-after-remove), which is why a flake here is worth
  more attention than its frequency suggests.
- ⚠ Do NOT read a green re-run as a fix: one green run is what the seven before it already were.

### From CLAUDE.md §CI — four defects CI found in its first hours

**Four defects CI found in its first hours — every one of them invisible on a developer box** (two
destruction-order bugs that only a different allocator faults on, and two "works because of prior
state" bugs that only a CLEAN machine reveals). The through-line: an environment that already has what
you need — an installed extension, a previous build's output — silently satisfies a dependency the code
never actually declares, so a passing local run proves nothing about a fresh one:
1. **Aggregate state destructor = use-after-free (FIXED).** `PhysicalOperator::sink_state` is a
   BASE-class member while the bound aggregate expressions owning the `FunctionData` are derived
   members, so at plan teardown the bind data is already freed when the state destructor dereferences
   it. Deterministic ordering, allocator-dependent fault: Linux SIGSEGV, macOS SIGABRT, Windows silent.
   No destructor is registered now; `AggSessionHolder`'s `agg_close` reclaims.
2. **A late `ArrowProducer` stream release aborted on macOS (FIXED).** `verify_global_functions` died at
   assertion 41 with `libc++abi: terminating due to uncaught exception of type std::system_error: mutex
   lock failed: Invalid argument` (exit 134). Two-line repro, which aborts on its own (NOT
   state-dependent — the statement bisect and an isolated run both land here):
   ```sql
   SELECT squared FROM fabricator_seq(5) WHERE value > 3 ORDER BY squared;
   ```
   lldb (`-k`, not `-o` — see the traps list) put the whole diagnosis in five frames: `std::mutex::lock()`
   ← `ArrowProducer::Release` ← six unsymbolized JIT frames ← `ArrowStreamScan` ←
   `PhysicalTableScan::GetDataInternal`, on the MAIN thread's pipeline (so not a finalizer).

   **Root cause needs BOTH halves, which is why it hid so well:**
   - **C++:** `BuildFilterValues`' producer was a `unique_ptr` LOCAL to `ArrowStreamInitGlobal`, promising
     only to "outlive the scan_table call". It dies when InitGlobal returns.
   - **C#:** the binding's `Execute` was an `async IAsyncEnumerable`, so its `scan.FilterValues?.Dispose()`
     did NOT run at call time — an async-iterator body starts at the first `MoveNextAsync`, i.e. inside
     `get_next`, long after InitGlobal returned. `ArrowProducer::Stream()` hands out a pointer INTO the
     object, so that release locks a destroyed `std::mutex`.

   **Why only macOS reported it:** Apple's `pthread_mutex_lock` validates the signature and returns
   EINVAL, which `std::mutex::lock` turns into a throw; glibc and Windows lock a destroyed mutex
   silently. Same lesson as bug 1 — a passing platform proves nothing about a use-after-free.

   **The `WHERE` is load-bearing** (no predicate ⇒ no filter constants ⇒ no producer ⇒ no crash), and the
   reason filter values exist at all for a function whose binding says `SupportsPushdown => false` is that
   `BindingBoundTable` reports `true` for a global/custom function — that flag is the host's BY-NAME
   projection mapping, not SQL pushdown (its doc says so). Reading the binding's flag instead of the
   wrapper's is what made an earlier pass wrongly "rule out" the filter-values path; the other earlier
   ruling-out was checking `StaticTableFunction.Execute` (a plain method — correct for THAT class) while
   `fabricator_seq` is `GfSeqFunction`, which implements `ITableFunction` directly with an async iterator.

   **Fix, in two layers:** the producer is now owned by `ArrowStreamGlobalState::filter_value_producer`,
   so it lives for the whole scan; the destructor body releases `stream` BEFORE member destructors run
   (a destructor body always does), so a dispose triggered by that release still sees a live producer.
   And the four bindings that ignore pushed filters now dispose in a PLAIN method and delegate to a
   private iterator (`GfSeqFunction`, `GfColumnsFunction`, `cf_columns`, `SqlServerProcedure`) — needed
   independently, because an iterator that is never enumerated never disposes at all, leaving the release
   to the GC finalizer. The contract note lives on `StaticTableFunction.Execute`, which already did it right.

   **How it was found without a CI cycle, and the transferable technique:** a destruction-ORDER bug is
   deterministic, so the non-faulting platform executes the same sequence and can be made to *detect* it.
   A temporary out-of-band liveness registry in `ArrowProducer` (origin string + alive flag in a static
   map, so `Release` never dereferences freed memory) printed
   `LATE RELEASE (use-after-free) of producer … created at [BuildFilterValues]` **on Windows**, first try.
   Then a **class sweep** with the diagnostic still armed over all 53 hermetic suites (4152 assertions)
   came back with ZERO other late releases, so this was the only instance. Reach for this before paying
   for a 20-minute remote debug cycle: you do not have to debug on the platform that faults.

3. **Tier 2's first CI run found an undeclared parquet dependency — the same class as the six hermetic
   suites, in the tier that had never run (FIXED).** All infrastructure came up green (build, TLS certs,
   compose, provisioning); `verify_mssql_s3_polybase` then failed at line 267 — its ONLY
   `native_write true` section, whose data files are written by a host `COPY … (FORMAT parquet)` — with
   `Copy Function with name "parquet" is not in the catalog`. The suite never declared `require parquet`
   (Tier 1's native-write suite does, line 12), so nothing loaded it and the copy-function lookup fell
   back to autoload-from-DISK. **Reproduced locally in one shot with the empty-USERPROFILE trick** —
   identical line and identical 117/116 assertion counts to CI — which is the proof that trick is worth
   keeping: it turns a service-tier CI failure into a local edit loop. A developer box passes either way
   because it has parquet under `~/.duckdb`. Adding the directive does not change the derived
   classification (still 53 hermetic / 42 service; the classifier keys on `require-env`).

4. **The packaging tier could never have worked on a clean machine — `pack-distribution.ps1` probed for
   its own build output BEFORE producing it (FIXED).** `$shellLibrary` was resolved at the top of the
   script, then the NativeAOT publish ran ~40 lines later; on a machine with no previous publish the
   probe returned `$null` and the script threw *"Installer shell (Fabricator.Installer.so for linux-x64)
   not found — publish it on a linux-x64 machine first"* immediately after that very publish printed
   `Generating native code` and succeeded. Every prior run — mine, and the WSL linux build — passed only
   because an earlier publish had left the file on disk. The probe is now a `Resolve-ShellLibrary`
   function called AFTER the publish step. Both jobs fail identically, so it is one fix for both
   platforms. This is the single best argument for having built the packaging tier at all: the artifact
   had been produced correctly by hand many times, and the script was still broken for anyone starting
   from nothing.

5. **The dual entry point — the keystone of the single-file distribution — was silently NOT EXPORTED on
   macOS (FIXED).** Enabling osx_arm64 in the packaging tier produced a clean build, a clean AOT link and
   a clean pack, then failed the smoke test: *"Extension … fabricator_core.duckdb_extension did not
   contain the expected entrypoint function 'fabricator_core_duckdb_cpp_init'"*. Cause is UPSTREAM, in
   `duckdb/extension/extension_build_tools.cmake`: on Apple a loadable extension is linked with hidden
   visibility, `-dead_strip`, and an explicit ONE-symbol whitelist
   `-Wl,-exported_symbol,_${NAME}_duckdb_cpp_init`. Our second entry is therefore stripped. Linux
   (`--gc-sections`/`--exclude-libs,ALL`) and Windows (`dllexport`) both keep it, so macOS is the ONLY
   platform where the one-binary-two-filenames trick fails — and it fails at LOAD time, not build time.
   Fixed in our `CMakeLists.txt` with an APPLE-guarded extra
   `-Wl,-exported_symbol,_fabricator_core_duckdb_cpp_init` (ld64 accumulates repeated `-exported_symbol`
   flags, so it adds to DuckDB's whitelist; the leading underscore is the Mach-O C prefix). Worth
   remembering as a general rule: **anything relying on an exported symbol other than the single blessed
   entry point needs an explicit macOS whitelist entry.**

### From CLAUDE.md §Release lines — release status v0.0.1/v0.0.2

**Release status (CORRECTED 2026-07-28 — the note below used to say "DRAFT … unpublished" and was WRONG,
which nearly caused a published release to be retagged):**
- **`v0.0.1-duckdb1.5.5` is PUBLISHED** (`draft=false`), created 2026-07-27, pinning **`a8de094`** — NOT the
  `5c28297` this note recorded; it was retagged again after that. Three ZIPs attached, 0 downloads:
  `linux_amd64-Standard` 40.1 MB / `osx_arm64-Standalone` 60.0 MB / `windows_amd64-Standalone` 62.2 MB.
- **`v0.0.2-duckdb1.5.5`** cut on 2026-07-28 at `21e7be5`, +30 commits over v0.0.1: both EW clast-master
  bumps, the variant shredding split, the `_metadata` locator conformance, and the `TransientRowAddress`
  helper migration. `distribution.yml` run **green on all three platforms** and the **DRAFT** release exists
  with its three ZIPs (linux_amd64-Standard 40.2 MB / osx_arm64-Standalone 60.1 MB /
  windows_amd64-Standalone 62.2 MB). Publishing is still a human decision.
- **`v0.0.1-duckdb1.5.5`'s RELEASE object was DELETED BY THE USER, deliberately (confirmed 2026-07-29)** — so
  the API lists only v0.0.2. **Its TAG deliberately survives on the remote at `a8de094`**, which is the part
  that matters: the tag is what keeps that release's source reproducible (`git submodule update` cannot
  reliably fetch an unreachable sha, so an orphaned commit would make the tagged build unbuildable). Nothing
  to investigate — do NOT "restore" it.

### From CLAUDE.md §Release lines — version declared in two CMake files (superseded by ./VERSION)

**The version number is NOT free to choose: it must match what the binary reports**, or the release is
mislabelled against its own contents. That means bumping **BOTH** declarations, which are easy to miss:
`CMakeLists.txt`'s `FABRICATOR_EXTENSION_VERSION` (→ the `FABRICATOR_VERSION` compile definition, i.e. what
`fabricator_version()` returns) **and** `extension_config.cmake`'s `EXTENSION_VERSION` (→ the extension
footer). `v0.0.2` was preceded by exactly that bump.

### From CLAUDE.md §Release lines — release cut records v0.0.7 … v0.0.12

  - **`v0.0.12-duckdb1.5.5` cut + pushed 2026-08-25**, TWO commits over `v0.0.11`, and they are independent:
    the **engineered-wood pin `3a31d9e → bab9a89`** (39 commits, fast-forward, still zero patches — the
    `.crc` version checksum beside every commit, `ARROW:schema` in every EW-written file including
    checkpoints, the five newly-rebasing delta commit paths, and the `CheckpointWriter` coercion fix), and
    the **SQL Server discovery fix** that had made Fabric Warehouse enumeration impossible for sixteen days.
    Deliberately two commits: folding a SqlServer-provider change into the pin would have made the pin's
    behaviour-neutrality claim untestable.
    - Gates behind it: hermetic **74/74 — 8003**, service **54/54 — 3001** (2992 for the pin alone, then
      +9 for the fix's gate and nothing else moved), EW `DeltaLake.Tests` **575/575** and
      `DeltaLake.Table.Tests` **841 passed / 0 failed / 113 skipped** × {net10.0, net8.0, net472}, plus the
      LIVE Fabric legs neither CI tier can be (lakehouse dbt PASS=4/4; Fabric's own SQL endpoint reading
      our tables and reading THROUGH a checkpoint the new EW wrote; and the warehouse dbt target PASS=4/4,
      which it had never been).
  - **`v0.0.11-duckdb1.5.5` cut + pushed 2026-08-20**, ONE commit over `v0.0.10` — the smallest interval
    on this line, and deliberately its own version rather than a move of v0.0.10's draft (that tag sits at
    its own bump commit, its draft already carries artifacts built from it, and one version name must not
    mean two artifacts). Content: the **host-FS per-IO instrument and the micro-GET fix it found** — the
    2026-08-16 OneLake `BufferedReadMax = 16 MB` fix had never reached `DuckDbTableFileSystem`, so every
    `s3://` root and every credential-less `abfss://` root still read a checkpoint parquet as dozens of
    8–55-byte ranged GETs (**57 ranged reads → 0, 73 IO ops → 17** on a 218-commit log). Gates: hermetic
    **72/72 — 7719** and service **52/52 — 2108**, both identical to the previous pin.
    **✅ THE DRAFT LANDED** — `distribution` green in 21m43s, all FOUR assets attached (linux Standalone
    74 MB + Standard 40 MB, osx Standalone 58 MB, windows Standalone 60 MB), `isDraft: true`. Each pack job
    includes the 12-check smoke against a STOCK DuckDB wheel, so a green job means the single-file artifact
    cold-loads, extracts its core and round-trips Delta — which is the only tier that exercises the shipped
    artifact rather than the statically-linked dev binaries. ⚠ FOUR drafts now exist (v0.0.6, v0.0.7, v0.0.9,
    v0.0.11) plus v0.0.10's; publishing stays a human decision and **`gh release list` is the authority.**
  - **`v0.0.10-duckdb1.5.5` cut + pushed 2026-08-20** (draft green in 27m21s), three commits over `v0.0.9`:
    **ABI v77** (`catalog_views` — a provider can declare a VIEW, whose body binds against its OWN catalog,
    which is the hazard a catalog macro cannot fix), **ABI v78** (`catalog_init` — the first crossing at
    ATTACH time with a live `ClientContext`, so a provider may do IO/HTTP before any discovery call; its
    exceptions PROPAGATE by design, since a swallowed one would yield a successfully attached empty catalog
    on a bad credential), and the **engineered-wood repin `2c51cd7 → 3a31d9e`** — twelve parquet commits, two
    of them SILENT WRITE CORRUPTIONS, one of which (validity taken unshifted across an auto-split row group)
    reached our CHECKPOINTS though not our data files.
  - **`v0.0.9-duckdb1.5.5` cut + pushed 2026-08-18**, NINE commits over `v0.0.8` and the largest interval
    since the line began. TWO breaking changes: **ABI v75** (`fabricator_delta_mfr_scan` +
    `delta_list_files` DELETED — a MID-STRUCT slot removal, so the bump is what makes a mismatched
    loadable/bridge pair loud at boot) and, breaking for PLUGIN AUTHORS, the **`Fabricator.Delta` assembly
    split** — the Delta provider is discovered by name now, so a narrow `FABRICATOR_BACKEND_ASSEMBLY`
    override that omits it loses `PROVIDER 'delta'`. Plus the `MaxThreads` parallel-scan fix (1.95x on a
    CPU-bound aggregate, ~11% REGRESSION on a merge-bound GROUP BY — say both) and the `BudgetedStream`
    boundary split that makes `delta.targetFileSize` enforceable again. Gates: hermetic **70/70 — 7646**,
    service **50/50 — 2028**. Cut as a NEW version rather than re-running v0.0.8's stuck job, for the
    standing reason: that tag sits at its own bump commit and predates all nine.
    **✅ THE DRAFT LANDED** — `distribution` green in 28m33s, all FOUR assets attached (linux Standalone
    78.5 MB + Standard 42.1 MB, osx Standalone 61.2 MB, windows Standalone 63.3 MB), `isDraft: true`.
    ⚠ Three drafts now exist (v0.0.6, v0.0.7, v0.0.9) — this file previously said only v0.0.6 survived, which
    was true when written and stopped being so at the next tag. **`gh release list` is the authority.**
  - **`v0.0.8-duckdb1.5.5` cut + pushed 2026-08-17**, three commits over `v0.0.7`: ABI **v74**
    (`alter_table` → `table_alter`, BREAKING) plus the `table_stats` dangling-opener SIGSEGV fix. Cut as a
    NEW version rather than moving v0.0.7's draft, for the SAME reason v0.0.7 was cut rather than moving
    v0.0.6: **v0.0.7's tag sits at its own version-bump commit and therefore predates all three**, and the
    interval carries a breaking ABI change — one version name must not mean two artifacts. Draft created by
    the tag push (distribution.yml's `release (draft)` job). ⚠ **ITS RELEASE JOB FAILED ON A GITHUB 503 AND IS NOW SUPERSEDED BY v0.0.9 — deliberately NOT re-run.**
    All FOUR pack jobs (windows/linux Standalone, linux Standard, osx Standalone) SUCCEEDED incl. the
    12-check smoke, in 14-22 min each; only `gh release create` 503'd. Re-running it (`gh run rerun
    32048246261 --failed`) would now produce a v0.0.8 draft from artifacts NINE commits stale, spanning a
    breaking ABI change — so the tag stays (it keeps that source reproducible) and no release object is
    made for it. ⚠ **`gh run list` showed that run as `failure ... 19s`, which reads as a build that died
    instantly and is NOT what happened**: the duration column there is the RELEASE JOB's, not the run's.
    `gh run view <id>` shows the per-job truth. ⚠ `gh` is installed but NOT on PATH:
    `export PATH="$PATH:/c/Program Files/GitHub CLI"`.
  - **Tags `v0.0.1`…`v0.0.6-duckdb1.5.5` all exist locally AND on the remote.** Only ONE release object
    survives: the **`v0.0.6-duckdb1.5.5` DRAFT** (created 2026-08-13, 4 assets — linux Standalone+Standard,
    osx Standalone, windows Standalone). Its tag sits at the `0.0.5 -> 0.0.6` bump commit, so by 2026-08-16
    it was **28 commits stale**, which is why `v0.0.7` was cut as a NEW version rather than moving it: the
    interval carries BREAKING ABI changes (v70/v72/v73) and the breaking `ITable`/`ITableBinding` rename, and
    shipping those under an already-drafted version number would make one version name two artifacts.
  - Content of `v0.0.7-duckdb1.5.5` (28 commits since `v0.0.6`): the catalog/table abstraction end to end
    (slices 1a–5, ABI v70–v73 — `get_metadata`/`scan_table`/the kind enum retired, the `delta.*` function
    namespace, the capability JSON, `ITransaction`/`ITable`), the Fabric read-path performance work
    (291 s → 8.8 s on the profiled lakehouse query: the plain-form preference, the pushed bare LIMIT, the
    literal-glob echo, the checkpoint buffered read, the size seed + immutability declaration, the mixed-table
    union form and its `preserve_insertion_order` fix), `cat.delta.checkpoint()`, and the AT-clause
    schema-change fix.

Earlier moves, while it genuinely was a draft: `0eadd00` → `c2af48a` (to pick up the first EW bump's two
silent-corruption fixes — the UTF-16-vs-UTF-8 comparator that could make pruning SKIP a file containing
matching rows, and stats truncation splitting a surrogate pair) → `5c28297` (the second EW bump, the
ns/second timestamp guard, and `PlanFiles`) → `a8de094`. Each tag message records what that move gained, so
the reasoning survives without this file. **`distribution.yml`'s release job creates the release with
`--draft`**, so a new tag yields a draft and publishing stays a human decision.


