# VARIANT support for the Delta provider

Status: **V1 BUILT (2026-07-06)** — `CREATE TABLE lake.s.t (v VARIANT)` / CTAS / INSERT / SELECT /
DV-DELETE work on the Delta folder-catalog under `native_read true, native_write true`;
`test/verify_delta_catalog_variant.test` (55 assertions); **delta-kernel reads the result** (validated —
the "kernel variantType support unverified" risk below is resolved). Fabric Runtime 2.0 Spark validation
is the remaining step. See "AS BUILT" below; the design sections after it are the original research.

## AS BUILT (differs from the planned design in two important ways)

1. **No per-operator pre-casts, no SQL wrapping — ONE Arrow type extension.** DuckDB's Arrow export hits its
   `default:` branch for VARIANT and consults the **`ArrowTypeExtension` registry UNCONDITIONALLY** (not
   gated on `arrow_lossless_conversion` — verified in `arrow_converter.cpp` `SetArrowFormat`). So
   `RegisterFabricatorVariantExtension` (`src/fabricator/fabricator_variant.cpp`, registered at extension load,
   idempotent) makes EVERY boundary crossing transparent: bulk INSERT/CTAS/COPY appenders, host-query result
   streams, create-table schema export, scan ingest, the catalog bind schema (`FetchTableColumns` →
   `PopulateArrowTableSchema` is registry-aware), and the host-query INPUT stream import (so the streaming
   COPY sees real VARIANT with no cast in the SQL). The conversions delegate to the parquet extension's
   scalars via `FunctionBinder`+`ExpressionExecutor` (parquet is statically linked; no parquet internals
   linked): `variant_to_parquet_variant(v)` out, `variant_bytes_to_variant(blob)` in.
2. **The transport is ONE self-delimiting BLOB per row (`ew.variant_transport`), NOT the canonical
   `arrow.parquet.variant` struct.** The value = parquet-variant metadata bytes immediately followed by the
   value bytes (the metadata header is self-delimiting — exactly the byte form `variant_bytes_to_variant`
   consumes). Reason: **upstream appender bug** — `ArrowAppender::Finalize`/`FinalizeChild` passes the
   LOGICAL type (VARIANT, whose struct info has 4 children: keys/children/values/data) to the child
   appender's finalize, which walks those children against the appender initialized for the INTERNAL type →
   a NESTED internal type (the canonical `struct<metadata,value>`) crashes with "Attempted to access index 2
   within vector of size 2". No built-in extension has a nested internal type (bool8/geoarrow/bignum are all
   leaves), so a LEAF internal type sidesteps the bug entirely. Upstream-PR candidate: `FinalizeChild` should
   use `append_data.extension_data->GetInternalType()` when set. The EW/C# marker is
   `SchemaConverter.VariantExtensionName = "ew.variant_transport"` (field metadata `ARROW:extension:name`).

Other findings from the build:
- **NULL variant rows**: the parquet binary decoder rejects an empty metadata buffer outright (does not
  consult validity), and a NULL row can arrive at the ingest conversion as a VALID zero-length blob
  (validity dropped somewhere in the C# crossing). The ingest substitutes the minimal valid "variant null"
  encoding (`01 00 00 00`) for null/empty rows and re-invalidates them after conversion — NULL semantics
  round-trip exactly (`v IS NULL` works).
- **`variant_extract(v, '$.a')` returns NULL** in 1.5.4; struct-style dot access `(v).a` works. The `->`
  operator casts via JSON and fails on the VARIANT repr.
- The DuckDB parquet writer **shreds** small variants by default (`typed_value` columns appear); reads are
  shredding-transparent via `read_parquet`.
- `PARQUET_VARIANT` (the transform's return alias) blocks `struct_extract`/dot access on its result — the
  alias prevents the STRUCT function match. Irrelevant to the blob transport, but a trap for SQL-side use.

EW/Delta layer (as planned): schema type `"variant"` ⇄ tagged blob in `SchemaConverter` (marker stripped
from Delta metadata like `PARQUET:*`); `variantType` reader+writer feature at create + on
`AddColumnAsync`/`SetSchemaAsync` via the generalized `UpgradeProtocolForFeatures`/`RequiredSchemaFeatures`
(replacing `UpgradeProtocolForTimestampNtz`); `ProtocolVersions` allowlists; `StatsCollector` treats a
variant field as a LEAF (nullCount only — automatic for the blob transport, guarded anyway).

Gates (clean errors): Bridge `DeltaCatalog` — CREATE/CTAS/INSERT with variant require
`native_write true` AND `native_read true`; `change_data_feed true` rejected at CREATE (CDC files are
codec-written); codec-path reads of a variant table rejected ("requires native_read").

**UPDATE / copy-on-write DELETE / OPTIMIZE — LIFTED via the `IDataFileReader` codec seam (second pass).**
EW gained the read-side counterpart of `IDataFileWriter`: `DeltaTableOptions.DataFileReader`
(`IDataFileReader.ReadAsync(relativePath, physicalColumns, ct)` → RAW batches, physical names, FILE ORDER,
DV rows included — order is a correctness requirement, every consumer is position-keyed). `ReadFileAsync`
and `CompactionExecutor` route their raw decode through it when set (the shared per-batch pipeline —
rename/DV/backfill/partitions/rowid — is unchanged; extracted as `ProcessFileBatchesAsync`). The Bridge's
`NativeParquetDataFileReader` implements it with `read_parquet(..., file_row_number => true) ORDER BY
file_row_number` (explicit order — a user can disable preserve_insertion_order), wired on `native_read`
into DeleteByRowIds/UpdateByRowIds/Optimize. With BOTH seams set the EW variant-rewrite gates return early
(the host codec preserves the annotation end to end), so UPDATE (non-variant columns AND the variant value
itself — SET values cross as the transport blob, `BuildArray` gained the Binary case, and the rewriter's
update-view field keeps the marker metadata so the bound view types as VARIANT), CoW DELETE, and OPTIMIZE
all work — kernel-validated post-DML. Three supporting fixes: the clean-rebuild before every rewrite now
preserves `ARROW:extension:*` field metadata (`DeltaTable.CleanField` — it previously stripped ALL
metadata, which would silently drop the variant tag on any rewrite); id-mode projection under the seam
resolves by physical NAME (field-id resolution needs the parquet footer the seam hides; spec files carry
physicalName in both modes); the seam reader is also the compaction read half (`CompactAsync` passes it).
EW backstop remains for codec-only tables: the gates still throw when either seam is missing.

**Fabric Runtime 2.0 validation — DONE (2026-07-06, live, both directions).** Workspace `Test` / lakehouse
`LH` runs **Spark 4.1.1**; `scratchpad/sparkprobe variant`:
- **We write → Spark reads**: `lake.dbo.fabricator_varlive` (native_write streaming COPY over `onelake://`,
  object / NULL / array / string rows) — Spark `to_json(v)` returns `{"a":1,"b":"x"}` / `[1,2,3]` /
  `"plain"` exactly, and `variant_get(v, '$.a', 'int')` = 1.
- **Spark writes → we read**: `fabricator_var_spark` (`CREATE TABLE … (v VARIANT) USING delta` +
  `parse_json` inserts) — our provider reads it typed VARIANT with dot access (`(v).x` = 10) and correct
  SQL-NULL semantics (`v IS NULL` matches Spark's NULL row).
- **Known NULL nuance (one direction)**: a SQL-NULL variant WE write reads in Spark as a **variant
  JSON-null value** (`to_json` = `"null"`, `v IS NULL` = false), while DuckDB reads the same file back as
  SQL NULL — DuckDB's parquet writer's representation choice for null variants, not a transport bug
  (Spark's own SQL-NULL row round-trips as SQL NULL through us). Revisit only if it bites a consumer.

Also: the **SQL Server provider rejects variant columns** with a clean error (`BuildCreateTable` — before
the arrow extension existed a VARIANT CTAS failed at export; without the guard it would now silently map
the tagged blob to VARBINARY). Cast to JSON/VARCHAR to move variant data into SQL Server.

Remaining: V2 per below (lift UPDATE/OPTIMIZE via a variant-aware native read half; list/map-nested
variant; EW-codec write annotation).

---

The sections below are the ORIGINAL design/research (kept for context; superseded where the AS BUILT
section says otherwise). Goal: `CREATE TABLE lake.s.t (v VARIANT)` /
CTAS / INSERT / SELECT on the Delta folder-catalog, Spark-4.1-interoperable.

## Landscape (verified)

- **Fabric**: Runtime 2.0 = Spark 4.1 + Delta Lake 4.1 supports VARIANT, but marks Delta 4.x
  features (variant, collations, coordinated commits) **experimental + Spark-experiences-only**
  (notebooks / Spark Job Definitions). The **SQL endpoint does NOT read variant tables**; MS docs
  say "don't enable if you need the table across Fabric workloads". ⇒ our usual Spark+SQL-endpoint
  reference pair only half-applies: **Spark is the only external validator** for now.
  https://learn.microsoft.com/en-us/fabric/data-engineering/runtime-2-0 ·
  https://learn.microsoft.com/en-us/fabric/fundamentals/delta-lake-interoperability
- **Delta protocol**: `variantType` READER+WRITER table feature (reader v3 / writer v7). Schema
  type name `"variant"`. Parquet physical: annotated group `struct<metadata: binary, value: binary>`
  (unshredded), plus the shredded layout (`typed_value` columns). Databricks/Spark 4.0+ is the
  reference writer.
- **DuckDB 1.5.4** (our pinned submodule): VARIANT is first-class (`LogicalTypeId::VARIANT = 109`,
  `src/common/types/variant/`). The **parquet extension reads/writes parquet Variant natively**,
  incl. shredding: settings `variant_minimum_shredding_size`, `force_variant_shredding`,
  `delta_only_variant_encoding_enabled`; scalar `variant_to_parquet_variant` (parquet ext) converts
  a VARIANT value to the `STRUCT(metadata BLOB, value BLOB)` transport form.
- **arrow-dotnet** (`D:\repos\arrow-dotnet`, official apache repo, VersionPrefix 23.0.0 — the
  version we pin): `arrow.parquet.variant` extension type (`VariantArray.cs` — accepts unshredded
  `struct<metadata,value>`, shredded `struct<metadata,value,typed_value>` and fully-shredded
  layouts), variant binary scalars (`Apache.Arrow.Scalars.Variant`), and `Apache.Arrow.Operations`
  (Shredding + VariantJson). **TODO check**: whether Variant is in the RELEASED 23.0.0 nuget or
  only master — if master-only we'd have to build Apache.Arrow locally and align Bridge + EW +
  plugins on it (the shared-Arrow ALC constraint), so prefer designs that DON'T need it (see below).
- **EW / our stack**: nothing yet — no `variant` in `SchemaConverter`/`DeltaSchemaSerializer`, no
  `variantType` feature declaration, EW parquet writer emits no VARIANT logical-type annotation.

## Key architectural insight — C# is a PASS-THROUGH

Variant needs **none** of the C#-side machinery that usually costs us:
- no min/max stats (meaningless for variant → stats-by-omission is spec-correct),
- cannot be a partition column,
- filters into the variant value are unpushable (DuckDB re-applies above the scan),
- an Arrow extension type transports opaquely (storage struct + metadata name) — Apache.Arrow C#
  passes unknown extension types through without understanding them.

So the data path can be **DuckDB-native end to end** (the `native_read`/`native_write` inversion we
already ship): DuckDB writes/reads the variant parquet; EW only does the `_delta_log`; C# only
moves opaque Arrow structs.

## SPIKE RESULTS (2026-07-06, executed against our build — definitive)

1. **In-engine VARIANT is fully functional** in DuckDB 1.5.4: `{'a':1}::VARIANT` literals,
   `typeof(...)='VARIANT'`, and a native parquet `COPY`/`read_parquet` round-trip preserves the type.
2. **VARIANT does NOT cross the Arrow C boundary**: pushing a VARIANT column through
   `fabricator_delta_write` (DuckDB→C# Arrow export) throws `Not implemented Error: Unsupported Arrow
   type VARIANT` — outcome 3 of the options below is the ONLY path; there is no extension-type export
   to allow through.
3. **The transport form CROSSES and CONVERTS BOTH WAYS**:
   `variant_to_parquet_variant(v)` (parquet ext scalar) → type `PARQUET_VARIANT` (struct-of-blobs
   alias) — crossed the boundary and committed via `fabricator_delta_write` successfully; and the
   REVERSE CAST EXISTS: `<parquet_variant>::VARIANT` → `VARIANT`. So the boundary recipe is:
   OUT of DuckDB = wrap the column `variant_to_parquet_variant(v) AS v`; INTO DuckDB = `v::VARIANT`.

**Implementation implications:**
- **native_read** (`Host.Query` SQL we control): the per-file `read_parquet` SELECT casts a variant
  column `v::VARIANT`? — NO: `read_parquet` already returns VARIANT in-engine; the problem is the
  RESULT crossing back C++←C# is the C# side pulling DuckDB output — wrap the projection with
  `variant_to_parquet_variant(v) AS v` in the reader SQL, and have `arrow_ingest`'s bind schema
  declare the column VARIANT with a C++-side cast `PARQUET_VARIANT→VARIANT` on ingest (or surface
  the transport struct + cast in the catalog scan SQL layer).
- **native_write streaming COPY**: the COPY input stream arrives FROM C# as the transport struct;
  the COPY SQL casts back (`v::VARIANT`) so DuckDB's parquet writer emits the annotated VARIANT
  (+ shredding settings apply).
- **The catalog bulk INSERT/CTAS path is the hard part**: chunks export via the C++ `ArrowAppender`
  (not SQL) → a VARIANT column throws at export. The C++ bulk/insert/CTAS operators must pre-cast
  VARIANT columns to the transport struct before appending (a bound cast exists — the reverse cast
  was verified; verify/use `BoundCastExpression` VARIANT→PARQUET_VARIANT, else bind the
  `variant_to_parquet_variant` scalar from the catalog). Same in reverse for scan ingest.
- EW/Delta schema work (type `"variant"` + `variantType` feature) is unchanged from the plan below.

## The one unknown to SPIKE first (RESOLVED above — kept for context)

**How does DuckDB 1.5.4 export VARIANT across the Arrow C interface?** `arrow_converter.cpp` has no
explicit VARIANT case — it routes through the Arrow-extension registry (`config.GetArrowExtension`),
and our boundary pins the STANDARD encoding (`fabricator::BoundaryClientProperties`,
`arrow_lossless_conversion=false`). Outcomes:
1. Exports as the `arrow.parquet.variant`-style extension struct → pure pass-through, nothing to do.
2. Exports only under lossless/extension mode → allow THAT extension through the boundary (a
   targeted exception in `BoundaryClientProperties`, keeping everything else standard).
3. Errors / unsupported → SQL-side conversion at the boundary: the native writer projects
   `variant_to_parquet_variant(v)` (→ `STRUCT(metadata BLOB, value BLOB)`) into the COPY, and the
   native reader CASTs back (`read_parquet` already returns VARIANT for annotated columns — verify;
   else `CAST(struct AS VARIANT)` / the parquet ext's reverse). Our `Host.Query`-based reader/writer
   can inject both transparently — **this fallback is guaranteed to work** because it never crosses
   the boundary as VARIANT at all, only as a plain struct of blobs.

Spike script: `SELECT {'metadata': ..}::VARIANT`-ish value via `fabricator_query`-style round-trip;
plus `COPY (SELECT <variant>) TO parquet` + `read_parquet` under `Host.Query`.

## Implementation plan (phased, mirrors the timestampNtz pattern)

**Phase V1 — native-path variant (the whole user feature):**
1. **EW schema layer**: `"variant"` in `SchemaConverter` (Delta→Arrow: the extension struct — or the
   plain `struct<metadata: binary, value: binary>` storage if we avoid the extension type;
   Arrow→Delta: recognize either) + `DeltaSchemaSerializer` round-trip.
2. **EW feature declaration** (copy `SchemaUsesTimestampNtz` wholesale): `SchemaUsesVariant` →
   CreateAsync adds `variantType` to reader+writer features (reader 3 / writer 7);
   `AddColumnAsync`/`SetSchemaAsync` emit the protocol-upgrade action when a variant column is
   introduced (reuse `UpgradeProtocolForTimestampNtz`'s legacy-feature enumeration — generalize it
   to `UpgradeProtocolForFeature(current, name)`).
3. **C++ type mapping**: DuckDB `VARIANT` ⇄ the boundary form chosen by the spike. The catalog bind
   schema (`FetchTableColumns`) must surface the column as DuckDB `VARIANT` (C# declares it; the
   Arrow schema field carries the extension name or a marker like the `arrow.json` pattern —
   follow the JSON logical-type precedent: tag the Arrow field, import as VARIANT, fallback to the
   storage struct if unregistered).
4. **Gates**: variant columns REQUIRE `native_write` + `native_read` (the EW-codec writer emits no
   VARIANT annotation → a codec-written file would read as a plain struct in Spark; clean error
   with guidance, our established pattern). DML: DELETE is layout-agnostic (DV / rewrite by rowid);
   UPDATE SET of a variant value initially gated (needs boundary value transport), scalar SET of
   OTHER columns on a variant table must work (pass-through column).
5. **Stats**: exact-or-omit already omits unknown types — verify `BuildDeltaStats`/StatsCollector
   skip variant cleanly (numRecords/nullCount only).
6. **Tests**: local round-trip (CTAS/INSERT/SELECT + `delta_scan` kernel read if delta-kernel
   supports variantType — check; it may reject → then our reader + Spark are the validators);
   **live Fabric Runtime 2.0** via `scratchpad/sparkprobe` (check workspace `Test` offers
   Runtime 2.0): Spark writes a variant table → we read; we write → Spark reads
   (`parse_json`/`variant_get` round-trip). Feature declaration verified in `_delta_log`.

**Phase V2 (optional/later):**
- Shredded variant read (arrow-dotnet's shredded layouts / DuckDB handles natively on the native
  path — likely free; verify against a Spark-shredded table).
- EW-codec write annotation (lift the codec gate) — needs the parquet VARIANT logical type in EW's
  writer (`ArrowToSchemaConverter` + logical-type annotation on the group).
- C#-side variant ops via `Apache.Arrow.Operations` (variant↔JSON global scalar, e.g.
  `fabricator_variant_to_json`) — only then does the arrow-dotnet Variant API matter.
- SQL Server provider mapping (VARIANT → `json`/`nvarchar(max)` via the JSON path) + DuckLake
  interop (DuckLake supports variant natively — relevant for a future DuckLake bridge).

## Risks / caveats

- **Fabric marks it experimental**; SQL-endpoint blindness is a Fabric limitation, not ours — but
  it means a variant table breaks the "one table, every Fabric workload" story until MS ships it.
- **delta-kernel (duckdb-delta) variantType support unverified** — if it rejects the feature, our
  local acid test loses the kernel check (Spark becomes the only reference; same situation as
  mapped-partition columns).
- The `variantType` feature bumps reader to v3 — same Fabric-conversion class as DV tables
  (validated fine), but combined feature sets should be re-validated once on a live lakehouse.
- Apache.Arrow 23.0.0 nuget vs master for the extension type — avoided entirely if the spike lands
  on outcome 1/3 (pass-through / SQL-side conversion), which don't need C# to know the type.

## Appendix — records moved verbatim from CLAUDE.md (2026-09-18)

CLAUDE.md carried these as-built records inline until it grew to 10,776 lines — a file loaded into every
session's context. They are moved here VERBATIM; CLAUDE.md keeps each entry's summary head plus a pointer to
this section. The one edit made on the way: a link that pointed into the docs directory is rewritten relative
to this directory, so it still resolves from here.

- **⚠⚠ DUCKDB PR #24157 (VARIANT over Arrow, `arrow.parquet.variant`) — THE FULL REVIEW ROUND IS ADDRESSED
  AND FORCE-PUSHED 2026-08-26; head `ae74889a` = current `main` (`a0f16b1d`) + 6 commits, their CI running.**
  Both reviewers' seven inline comments implemented; the branch lives in **`D:\repos\duckdb-fix`**
  (remote `fork` = cmettler/duckdb; safety pointer `pre-rebase-backup` = the old head `a7d238f5`).
  - **⚠⚠ `D:\repos\duckdb-fix` IS THE PARENT REPO OF TWO WORKTREES: this repo's `duckdb/` SUBMODULE DIR
    (detached at the v1.5.5 pin, holding the user's uncommitted `CREATE_NEW`/`ExclusiveCreate` fix) and
    `D:\tmp\duckdb-main-wt` (`fix/windows-exclusive-create`).** They share one object store — no
    destructive git ops (gc/prune/filter) in duckdb-fix without checking the worktrees.
  - **What shipped in the round, commit-walkable (DuckDB SQUASH-MERGES, so never squash review commits
    locally — the `git mv` commit is what keeps the 900-line move rendering as a rename):** fields resolved
    by NAME with the schema's order recorded on a per-column `ArrowTypeExtensionData` (needed a 1-line core
    change: `GetTypeFromSchema` no longer clobbers extension data `GetType` attached); dictionary/REE
    children via `GetArrowLogicalType` (= `GetTypeFromSchema` + dictionary wrap); shredded schemas
    (`typed_value`, both 2- and 3-child forms) REFUSED with a clear NotImplemented; export declares the real
    layout (`z`/`Z`/`vz` from session settings) and `metadata` NON-NULLABLE with NULL rows' child slots
    backfilled by the minimal encoding; hand-built Arrow C Data tests for every reviewer-requested shape.
  - **⚠⚠ THE FIND OF THE ROUND: fixing the nullability comment exposed that the extension-type IMPORT never
    received a column's top-level validity** (`ColumnArrowToDuckDB`'s extension branch built `input_data`
    with no validity, and the conversion's rebuild destroyed the caller's pre-set mask) — the original PR
    only round-tripped NULLs because the old export LEAKED parent nulls into the child buffers, i.e. the
    exact spec violation under review. Fixed with one `FlatVector::CopyValidity` line; plausibly fixes
    `geoarrow.wkb` NULL import too. Found by instrumentation (export `parent_nulls=5` arriving as
    `src_nulls=0`), after a stash-and-rebuild CONTROL proved the pristine rebase passed and MY edits broke it.
  - **⚠⚠ Comment #7 (binder crashes without a valid context) resolved the way the user chose: the variant
    binary encode/decode MOVED TO CORE** (`src/common/types/variant/` — `parquet_variant_encoding.cpp`,
    `parquet_variant_iterator.{hpp,cpp}`, `variant_binary_decoder.{hpp,cpp}`), exposed as
    `ParquetVariantConversion::ToParquetVariant`/`ConvertBinary` with `DUCKDB_API`; parquet keeps
    registering the SQL functions and delegates. The encode block was ALREADY core-clean (zero parquet
    symbols — measured before cutting); `convert_variant.cpp` went 1041 → 142 lines. ⇒ **VARIANT over Arrow
    no longer needs parquet loaded at all**, and the alias-`Reinterpret` dance dissolved with the binder.
  - **⚠ TRAPS PAID FOR, worth keeping:** (1) the `[arrow]` tag's ASSERTION TOTAL is nondeterministic —
    same binary gave 31,764 then 39,358, all-pass 61/61 both times; the CASE count is the metric, do not
    chase totals. (2) `CompareResults` rendered diff rows via `GetValue<string>`, which THROWS on NULL —
    and `Value::ToString()` throws on a VARIANT-NULL too (its VARCHAR cast is NULL on a non-null value) —
    so a data mismatch presented as `StringValue::Get on a NULL value` escaping the harness; fixed to
    `GetBaseValue().ToString()`. (3) main REWROTE the Vector model (buffer-based, SHREDDED vector type,
    `SetAlias` → immutable `WithAlias`); patching buffers `Reference`-shared with another vector corrupted
    the export — copy into the result's OWN children instead. (4) duckdb-fix's `build/relassert` is
    Release+FORCE_ASSERT+**ASAN**: `unittest.exe` needs `clang_rt.asan_dynamic-x86_64.dll` on PATH from the
    MSVC `bin/Hostx64/x64` dir; `build_relassert.bat`/`build_shell.bat` in the repo root. (5) format gate:
    venv + `pip install "black>=24" clang_format==11.0.1 cmake-format`, then
    `echo y | python scripts/format.py --fix` (PEP 668 blocks bare pip; `--fix` prompts).
  - **PENDING:** post the PR summary comment + the reply to evertlammerts (texts drafted in-session, user
    has them). The Tidy failure was pure branch staleness (it lints
    `git diff origin/main`, and 2221 behind meant linting upstream's own files) — the rebase alone fixes it.
  - **✅✅ 2026-08-28 — SUPERSEDES THE 2026-08-27 DRAFT NOTE BELOW: THE PR IS OUT OF DRAFT, its branch
    `arrow-variant-extension` was FAST-FORWARDED to the rebased head `83cbc26770` (9 commits on main
    `d7dcda1203`, 18 files, +1966/−954, MERGEABLE, state OPEN), and the duplicate fork CI runs were
    cancelled.** Upstream's `Main` workflow sits at `action_required` (fork-PR runs need maintainer
    approval) — nothing for us to do there. **STILL PENDING, and the texts are the part to not lose:**
    (1) post the PR summary comment — a fresh draft (the original was lost to a context compact) is at
    **`scratchpad/pr24157_summary.md`** (repo scratchpad, gitignored), covering the review-round
    resolutions, the import-validity core fix, the move-to-core, and the three post-review CI fixes;
    (2) the reply to evertlammerts — NO draft exists (also lost); re-read their comment before writing.
    The `Bench Ingestion Perf` variance stays unexplained (3 of 4 regressions in `native` ingestion our
    diff does not touch; the decisive test is a `Regression.yml` re-run — optional).
  - **⚠⚠ 2026-08-27 — THE PR IS NOW A **DRAFT** (user-asked) AND THE FIXES ARE ON A THROWAWAY BRANCH, NOT
    IN THE PR. `arrow-variant-extension` is still `ae74889a9`; the work is `arrow-variant-enum-blacklist`
    at `fed1832c23` on the FORK ONLY. Nothing has ever been pushed to `duckdb/duckdb`.** The user's
    instruction was to validate on the fork's CI before touching the PR. When green, `fed1832c23` is a
    strict DESCENDANT of `ae74889a9`, so the PR branch fast-forwards — no cherry-pick.
    - **THREE REAL DEFECTS THE PR'S OWN CI COULD NEVER HAVE SHOWN**, because it died at the FIRST gate
      (`prepare`) and upstream ran NOTHING on that head (`head_sha=ae74889a9f` → zero workflow runs; the
      only check is the "Apply PR actions" bot). Each is one commit on the branch:
      1. **`scripts/generate_enum_util.py` blacklist** (`4fb6c740`). Moving the variant encode/decode into
         `src/include/duckdb/common/types/variant/` made FOUR enums visible to a generator that walks all
         of `src/**.hpp` — `Kind`, `ParquetGroupKind`, `VariantBasicType`, `VariantPrimitiveType` — so the
         committed `enum_util.cpp`/`.hpp` went stale and `git diff --exit-code` failed. ⚠ **Regenerating
         and committing would NOT COMPILE**: `ParquetVariantNode::Kind` is NESTED and the generator's regex
         has no notion of nesting, so it emits `EnumUtil::ToChars<Kind>` at namespace scope. That is why
         the blacklist already holds generic nested names (`Type`, `Flags`, `Slot`, …). ⚠ **The step is
         named "Check format" but the format check PASSES** — it also regenerates and diffs, so the name
         misleads.
      2. **`Flatten(count)` → `Flatten()`** (`c87a4e501`) in `arrow_type_extension.cpp:675-676`. The
         count-taking overload is `[[deprecated]]` and literally `{ Flatten(); }`, so it is
         behaviour-preserving. ⚠ Caught ONLY by the clang `-Werror` builds (Relassert, OSX **Debug**, Tidy);
         Linux Release and OSX **Release** passed, and a local MSVC build reports C4996 without erroring.
      3. **A vector-size-fragile test of ours** (`fed1832c23`). `Test Arrow VARIANT export layouts and
         schema flags` did ONE `result->Fetch()` over `range(4)` and hardcoded `null_count == 2`. The
         **Vector Sizes** job builds with `STANDARD_VECTOR_SIZE=2`, so the first chunk holds two rows and
         one NULL ⇒ `1 == 2`. Fixed by accumulating over every chunk, with the per-chunk assertion being
         the invariant that matters (the non-nullable metadata child never carries a NULL). ⚠ The other
         `Fetch()` in that file is a proper streaming `get_next` and the hand-built probe uses
         `GetValue(col,row)` on a materialized result — both vector-size agnostic, swept and confirmed.
    - **✅✅ BOTH REMAINING FAILURES ARE PRE-EXISTING AT THE BASE AND ARE FIXED ON CURRENT MAIN — SETTLED
      2026-08-27 BY A CONTROL RUN PLUS UPSTREAM'S OWN CI, AND THE BRANCH IS NOW REBASED ONTO MAIN.**
      Control **`33070411099`** on `ci-control-pristine-base` (`a0f16b1dba`, byte-identical to
      `origin/main` at the time) **FAILED `Linux Config (Execution)` in 1h33m01s, exit 2** — versus the
      work run's **1h31m41s** failure, on a tree containing NONE of our work. ⇒ not ours.
      - **⚠⚠ THE DECISIVE COMPARISON IS THE DURATION *RATIO TO ITS OWN SIBLINGS*, NOT THE ABSOLUTE TIME**
        — fork runners and upstream runners are different fleets, so absolute seconds prove nothing.
        At our base Execution is a **2x OUTLIER** (1h33m vs siblings 44-65m); on upstream main
        `d99587345c` it is **IN-FAMILY** (11m54s vs siblings 7/8/20m). Same job, 8x swing.
      - Upstream's own CI on main is GREEN for BOTH of our failures: `Linux Config (Execution)` success on
        `d99587345c` AND `1eb850db7b`, and **all four `windows_amd64_mingw` extension legs success**. So
        upstream fixed both somewhere between 2026-08-25 and 2026-08-26.
      - **✅✅ THE CONTROL RAN TO COMPLETION AND ITS FAILURE SET IS EXACTLY OURS — FIVE JOBS, and the
        mingw half is now MEASURED rather than argued.** On the pristine base: `Linux Config (Execution)`
        FAILED **and all FOUR `windows_amd64_mingw` extension legs FAILED** (scanner/core/search/cloud) —
        the identical set the work run failed. Neither failure is ours; both are pre-existing at
        `a0f16b1dba` and both are green on current upstream main.
      - **⚠ I HAD CALLED THE mingw ONE "toolchain, not ours" AND SAID I WOULD BE SURPRISED TO BE WRONG.**
        The CONCLUSION held and the REASONING did not: upstream passes all four legs on a newer main, so it
        is a BASE-ERA BREAKAGE SINCE FIXED, not a standing toolchain fault. **Right answer, wrong argument —
        and only the control could tell those apart.**
      - **⚠⚠ `origin/main` IN `D:\repos\duckdb-fix` IS STALE AND CANNOT BE TRUSTED — `remote.origin.fetch`
        is a TAG-ONLY refspec (`+refs/tags/v1.5.3:...`), so `git fetch origin` NEVER updates
        `refs/remotes/origin/*`.** It read `a0f16b1dba` while the server was at `d7dcda1203`. **`git
        ls-remote origin refs/heads/main` is the authority**; to get a usable ref,
        `git fetch origin refs/heads/main:refs/remotes/origin/main-real`. This is the same class as the
        engineered-wood `upstream/master` trap already in this file — a stale remote-tracking ref that
        still resolves locally, so every comparison against it silently answers the wrong question.
      - **REBASED ONTO MAIN 2026-08-27 — `arrow-variant-on-main` @ `83cbc26770`, base PINNED to
        `d7dcda1203`, run `33086108403`.** ⚠ On a NEW branch: `arrow-variant-extension` stays at
        `ae74889a9f` (draft PR, untouched) and `arrow-variant-enum-blacklist` at `fed1832c23`.
      - **THE REBASE IS A PROVEN PURE REPLAY, not a hoped-for one**: 9/9 commits, zero conflicts, and the
        two diffs (`a0f16b1dba..fed1832c23` vs `d7dcda1203..arrow-variant-on-main`) are **137414 bytes
        BOTH**, differing only in blob index hashes and ONE hunk-header line number. ⚠ Main is 131 commits
        ahead in TWO DAYS, but it touched only **2 of our 18 files** (`parquet_extension.cpp`,
        `generate_enum_util.py`) — every arrow/variant file we edit was untouched, which is the opposite
        of the previous rebase where main had rewritten the Vector model under us. Upstream also extended
        the enum blacklist, with **no overlap** with our four names.
    - **⚠ THE SUPERSEDED PRE-REBASE STATE, kept because the classification rests on it.**
    - **⚠ TWO FAILURES REMAIN ON THE WORK BRANCH AND A CONTROL RUN IS IN FLIGHT TO CLASSIFY THEM.**
      Control = branch **`ci-control-pristine-base`** at **`a0f16b1dba`** (byte-identical to `origin/main`;
      differs from the work branch by exactly our 18 files), dispatched as run **`33070411099`**; the work
      run is **`33067194845`**. Compare `Linux Config (Execution)` between them.
      - `Linux Config (Execution)`: 2 of ~4541 tests TIME OUT (600 s, 4 retries) under
        `test/configs/verify_fetch_row.json` (`debug_force_fetch_row=true`) —
        `duckdb_eviction_queues.test` and `substring_zonemap_pruning.test`, which touch neither Arrow nor
        VARIANT. The other three configs in that same job pass at ~250 s each. ⚠ **NOT yet established as
        pre-existing**: upstream main passes that job, but only on `d99587345`, **80 commits AHEAD** of our
        base, and upstream's run on our EXACT base skips Config entirely — hence the control.
      - `extensions / Main (*) / windows_amd64_mingw` ×4: `cc1plus.exe: error: bad value ('x86-64-v2') for
        '-march=' switch`, dying on `third_party/fmt|fastpforlib|hyperloglog`. Toolchain, not ours (the
        same job also reports a missing cmake and "not a git repository"). ⚠ `windows / MinGW (64 Bit)` in
        the MAIN matrix passes — it is only the extension-build leg.
      - Everything else on the work branch is GREEN: Vector Sizes (the fix confirmed), Thread Sanitizer,
        Relassert (+Tests), Tidy, all four Windows variants, swift, the whole regression suite, and the
        entire OSX run.
    - ⚠ **Pushing a branch that points at an EXISTING commit triggers NOTHING** — the `push` trigger has
      `paths` filters and a new branch with no new commits matches none of them. Use
      `gh workflow run Main.yml --ref <branch> -f run_all=true`. ⚠ The concurrency group is keyed on
      `github.ref`, so a push to a DIFFERENT branch cannot cancel an in-flight run on another.
