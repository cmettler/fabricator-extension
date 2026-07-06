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
   `RegisterArrowNetVariantExtension` (`src/arrownet/arrownet_variant.cpp`, registered at extension load,
   idempotent) makes EVERY boundary crossing transparent: bulk INSERT/CTAS/COPY appenders, host-query result
   streams, create-table schema export, scan ingest, the catalog bind schema (`FetchTableColumns` →
   `PopulateArrowTableSchema` is registry-aware), and the host-query INPUT stream import (so the streaming
   COPY sees real VARIANT with no cast in the SQL). The conversions delegate to the parquet extension's
   scalars via `FunctionBinder`+`ExpressionExecutor` (parquet is statically linked; no parquet internals
   linked): `variant_to_parquet_variant(v)` out, `variant_bytes_to_variant(blob)` in.
2. **The transport is ONE self-delimiting BLOB per row (`arrownet.variant`), NOT the canonical
   `arrow.parquet.variant` struct.** The value = parquet-variant metadata bytes immediately followed by the
   value bytes (the metadata header is self-delimiting — exactly the byte form `variant_bytes_to_variant`
   consumes). Reason: **upstream appender bug** — `ArrowAppender::Finalize`/`FinalizeChild` passes the
   LOGICAL type (VARIANT, whose struct info has 4 children: keys/children/values/data) to the child
   appender's finalize, which walks those children against the appender initialized for the INTERNAL type →
   a NESTED internal type (the canonical `struct<metadata,value>`) crashes with "Attempted to access index 2
   within vector of size 2". No built-in extension has a nested internal type (bool8/geoarrow/bignum are all
   leaves), so a LEAF internal type sidesteps the bug entirely. Upstream-PR candidate: `FinalizeChild` should
   use `append_data.extension_data->GetInternalType()` when set. The EW/C# marker is
   `SchemaConverter.VariantExtensionName = "arrownet.variant"` (field metadata `ARROW:extension:name`).

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
codec-written); codec-path reads of a variant table rejected ("requires native_read"). EW backstops
(`DeltaTable`) — codec parquet write on a variant schema throws; `DeleteByRowIdsAsync` (copy-on-write),
`UpdateByRowIdsAsync`, `CompactAsync` throw ("would strip the parquet VARIANT annotation" — the rewrite
READ half is the codec reader even under native_write). **DV DELETE works** (bitmap-only, no data rewrite —
and DV is the catalog default). Lifting UPDATE/OPTIMIZE needs a variant-aware read half (the native
rewriter path, clean-shape-gated in EW) — follow-up.

**Fabric Runtime 2.0 validation — DONE (2026-07-06, live, both directions).** Workspace `Test` / lakehouse
`LH` runs **Spark 4.1.1**; `scratchpad/sparkprobe variant`:
- **We write → Spark reads**: `lake.dbo.arrownet_varlive` (native_write streaming COPY over `onelake://`,
  object / NULL / array / string rows) — Spark `to_json(v)` returns `{"a":1,"b":"x"}` / `[1,2,3]` /
  `"plain"` exactly, and `variant_get(v, '$.a', 'int')` = 1.
- **Spark writes → we read**: `arrownet_var_spark` (`CREATE TABLE … (v VARIANT) USING delta` +
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
   `arrownet_delta_write` (DuckDB→C# Arrow export) throws `Not implemented Error: Unsupported Arrow
   type VARIANT` — outcome 3 of the options below is the ONLY path; there is no extension-type export
   to allow through.
3. **The transport form CROSSES and CONVERTS BOTH WAYS**:
   `variant_to_parquet_variant(v)` (parquet ext scalar) → type `PARQUET_VARIANT` (struct-of-blobs
   alias) — crossed the boundary and committed via `arrownet_delta_write` successfully; and the
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
and our boundary pins the STANDARD encoding (`arrownet::BoundaryClientProperties`,
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

Spike script: `SELECT {'metadata': ..}::VARIANT`-ish value via `arrownet_query`-style round-trip;
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
  `arrownet_variant_to_json`) — only then does the arrow-dotnet Variant API matter.
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
