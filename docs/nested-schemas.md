# Nested schemas (DuckDB 2.0) — the plan, and what is built

**Status (2026-10-23): SLICE 0 BUILT (the capability, inert). Slices 1 and 2 NOT started.**
User-directed, from <https://duckdb.org/docs/2.0/sql/statements/create_schema#nested-schemas>. Scope
decided by the user: a **full ABI change** carrying a schema PATH, Delta on **local FS + plain ADLS/S3**,
with Fabric's `Tables/` area and SQL Server deliberately excluded.

## 1. What DuckDB actually requires — read from 2.0's source, not the doc page

- `Catalog::SupportsNestedSchemas()` — **opt-in, defaults false**, so we are not broken today.
- The walk is `Catalog::LookupSchemaPath`, and it is three lines that decide the whole design:

  ```cpp
  auto entry = schema ? schema->LookupEntry(transaction, lookup) : lookup_root(lookup);
  if (!entry) { /* throw or null */ }
  schema = entry->Cast<SchemaCatalogEntry>();
  ```

  The ROOT comes from the catalog's existing `LookupSchema`; every DEEPER level comes from asking the
  **parent schema entry** for a child with `CatalogType::SCHEMA_ENTRY`.
- `SchemaCatalogEntry` carries `parent_schema`, and `GetSchemaPath()` / `GetSchemaName()` /
  `GetQualifiedName(entry)` are derived from that chain.
- `CreateSchemaInfo` carries a nested flag for `CREATE SCHEMA a.b`.

⇒ **we already implement the hard half.** `FabricatorSchemaEntry::LookupEntry` exists; what it does not do
is answer `SCHEMA_ENTRY`, and our entries have no parent link.

## 2. Why it is not a small change — the two structural blockers

1. **The `catalog_*` streams carry a flat UTF-8 schema NAME.** `catalog_schemas` (1 column),
   `catalog_tables` (3), `catalog_functions` (5), `catalog_macros` (3), `catalog_views` (3). ⚠ All five are
   read host-side by the ONE `ReadStringTable(stream, expected_cols) -> vector<vector<string>>`, whose
   contract is *"all UTF-8 columns"* — 8 call sites. A `LIST<VARCHAR>` path column needs a sibling reader;
   it cannot just be slotted in.
2. **`table_open(handle, const char *schema, const char *table, …)`** takes a name. This is the one true
   C-signature change, and it is what forces the ABI bump.

Plus: the host's schema store is FLAT (`case_insensitive_map_t<unique_ptr<FabricatorSchemaEntry>>
schemas_`) alongside the `retired_schemas_` graveyard that exists to prevent a recorded use-after-free —
both become trees, and the graveyard's ordering rule (a schema entry must outlive the table entries whose
`ParentSchema()` references it) now has a second dimension.

## 3. Who it is for — and the honest value check

- **⚠ BOTH STANDARD LAKEHOUSE LAYOUTS ARE EXACTLY TWO LEVELS** — Unity Catalog's `catalog.schema.table`
  and Fabric's `Tables/<schema>/<table>` — and `schemas true` already serves them. Nesting does NOT unlock
  those; it unlocks **ad-hoc deeper folder trees**, which today are not discovered at all (the object-store
  glob is literally `"/*/*/_delta_log/*.json"` or `"/*/_delta_log/*.json"`, a FIXED depth, and `_schemas`
  is a BOOLEAN rather than a depth).
- **⚠⚠ OneLake `Files/` IS PLAIN ADLS (user, 2026-10-23), which is what makes the layout list short.**
  Covering ADLS covers a Files-based lakehouse for free; `Tables/` stays two-level because Fabric defines
  it that way, so there is NO OneLake-specific work in this feature.
- **⚠ THE SECOND PROVIDER IS THE BETTER ARGUMENT, and it is not Delta (user-raised).** `DaxCatalog`
  declares *"the model name(s) + the `system` schema"* as FLAT SIBLINGS, so two attached semantic models
  share ONE `cat.system` — even though `$SYSTEM` DMVs are per-database in Analysis Services. Nesting gives
  each model its own `cat.<model>.system.<dmv>`: a genuine three-level need, from a provider with no
  directory tree at all. That it is not Delta-only is what justifies changing the ABI rather than encoding
  paths in strings.
- **⚠⚠ AND THE CLEANEST FIT IS A PLUGIN, NOT A FIRST-PARTY PROVIDER (user, 2026-10-23):
  `fabricator-grist` is `team -> workspace -> document -> tables`.** ATTACH names the team, and
  `cat.<workspace>.<document>.<table>` falls straight out. Unlike Delta's ad-hoc trees this is not an
  unusual layout anyone could avoid — it is the product's OWN hierarchy, so under a two-part model one of
  workspace/document MUST be flattened into a compound schema name, which is exactly the kind of
  name-mangling this feature exists to remove.
  ⇒ **THREE providers now want it and two of them are structurally three-level** (grist by design, DAX
  per-model `system`), which is what turns "nice to have for odd Delta folders" into a real argument for
  changing the ABI. ⚠ It also means the feature must work for an OUT-OF-TREE plugin, so whatever carries
  the path has to be part of the published contract (`Fabricator.Abstractions`), not an internal detail —
  see the plugin/host build boundary in CLAUDE.md.
- **⛔ SQL SERVER AND FABRIC `Tables/` MUST LEAVE THE CAPABILITY OFF.** Their namespace is genuinely
  two-part; declaring nesting there would make DuckDB offer a syntax that resolves to nothing.

## 4. The slices

### ✅ Slice 0 — the capability (BUILT 2026-10-23, inert, no ABI bump)

`FabricatorCapabilities.supports_nested_schemas`, parsed from the capability doc beside
`is_binary_collation` / `exact_filter_pushdown` / `null_order_expressible`, surfaced as
`FabricatorCatalog::SupportsNestedSchemas()`. The capability JSON is additive, so **no version bump**.
**Nothing declares it true** — reporting the capability without the wire shape would let DuckDB resolve
`a.b` against a catalog that cannot express it. Gate: hermetic **82/82 — 10089**, identical, i.e. inert.

### ✅ Slice 1 — the wire shape (ABI v89 -> v90), BUILT 2026-10-23, BEHAVIOUR-NEUTRAL

As planned below, with two deviations worth knowing:

- **`table_open` was NOT changed.** At depth 1 a path IS a single name, so its signature holds until
  slice 2 actually nests — which kept this slice to TWO surfaces (`catalog_schemas`, `catalog_tables`)
  instead of four. `catalog_functions` / `catalog_macros` / `catalog_views` stay flat for the same reason:
  functions, macros and views live in TOP-LEVEL schemas, and nothing yet puts them deeper.
- **The managed contract migrated by DIM, so NO out-of-tree plugin breaks.** `GetSchemaPaths()` /
  `GetTablePaths()` default to `SchemaPathAdapter.WidenFirstColumn(GetSchemas()/GetTables())`, wrapping a
  flat name as a depth-1 path. A provider that has never heard of paths is unaffected and needs no repin —
  which matters because the four plugins pin `Fabricator.Abstractions` by SHA. Only a nesting provider
  overrides. Same idiom as `Initialize()` and the 3-arg `OpenCatalog`.

**⚠⚠ THE SLICE SHIPPED A USE-AFTER-FREE FIRST, AND THE TIER'S FAILURE SHAPE IS WHAT NAMED IT — 69 of 82
hermetic suites failed with `failed to read metadata batch`.** The adapter wrapped the source batch in
`using` while handing its columns 1.. to the new `RecordBatch` BY REFERENCE; `RecordBatch.Dispose()`
disposes its columns, so the returned batch pointed at freed arrays.
- **The asymmetry is the diagnosis**: `catalog_schemas` has ONE column and worked (its only column is
  freshly built and shares nothing); `catalog_tables` has THREE and failed. A one-provider smoke test
  would have passed.
- The LOG was the discriminator rather than the error text: both crossings were recorded with NO
  `SetError`, so the ABI call itself had succeeded and the fault was in pulling a batch — i.e. in the
  adapter, not the wiring.
- ⇒ ownership of the shared columns passes to the returned batch, and the missing `using` carries the
  measurement beside it so it is not "tidied up" later.
- ⇒ **this is the argument for the slice being behaviour-neutral by design**: it was a lifetime error in
  a pure refactor, and had it been bundled with the tree and Delta's discovery there would have been no
  assertion-identical baseline to test it against.

Gates: hermetic **82/82 — 10089** and service **60/60 — 3593**, BOTH identical to the pre-slice counts,
which is the neutrality claim.

### Slice 1 — as originally planned

Carry a path everywhere, with **every provider still emitting depth-1 paths**, so nothing nests yet.

- the five `catalog_*` streams: schema column `VARCHAR` -> `LIST<VARCHAR>`
- a path-aware reader beside `ReadStringTable` (do NOT widen that one — 8 call sites depend on its
  all-strings contract)
- `table_open(handle, const char *const *schema_path, int32_t schema_depth, const char *table, …)`
- the five managed producers (`DeltaCatalog`, `SqlServerBackend`, `DaxCatalog`, `DeltaRsCatalog`,
  `StubBackend`) emit single-element paths
- **the claim to test is arithmetic: both tiers come back ASSERTION-IDENTICAL.** That is the whole reason
  this is its own slice — a combined slice could not prove the refactor disturbed nothing.

### ✅ Slice 2a — the host can REPRESENT a tree (BUILT 2026-10-23, still inert)

`FabricatorSchemaEntry` owns `child_schemas_` and forwards a parent to `SchemaCatalogEntry`;
`LookupEntry` answers `CatalogType::SCHEMA_ENTRY`, which is the call `Catalog::LookupSchemaPath` makes on
each parent. **`child_schemas_` is empty for every provider**, so the branch answers null and nothing
existing changes. Hermetic **82/82 — 10089**, identical.

- **Ownership gives the lifetime ordering for free.** A parent cannot be destroyed while holding a live
  child, and every entry's `ParentSchema()` is a REFERENCE into its schema entry — the use-after-free the
  catalog's retire-don't-destroy graveyard already exists to prevent. No second graveyard was needed.
- **⚠ The parent is a CONSTRUCTOR PARAMETER on `SchemaCatalogEntry`, not a field set afterwards.** It is
  stored at construction and `GetSchemaPath()` walks that chain, so an entry given its parent later would
  already have reported itself top-level. (First attempt wrote `info.parent_schema = this`, inventing a
  field; `CreateSchemaInfo` instead encodes the parent in its QUALIFIED NAME, and exposes
  `ParentSchemas()` / `IsNested()`.)

### ⏳ Slice 2b — NEXT, and the point at which something actually nests

**Nothing below is started.** In order:

1. **`FabricatorTableInfo` gains `schema_path` ALONGSIDE `schema_name`** (the leaf). Measured: 20
   consumers of `.schema_name`, and the split is favourable — functions/macros/views use
   `schemas_.find(schema_name)` against the TOP-LEVEL map, which stays correct because they do not nest.
   Only `ensure_schema(table.schema_name)` needs the path. Replacing the field outright would touch all
   20 for no gain.
2. **`DiscoverSchemas` / `DiscoverTables` stop flattening.** Both currently do `path.back()` with a
   comment pointing here; they return the whole path instead.
3. **`ensure_schema` walks the path**, calling `GetOrCreateChildSchema` per level (the two identical
   lambdas in `LoadCatalog` ~L114 and `RefreshCache` ~L228 — keep them in step or factor them).
4. **`table_open` takes a path — THIS IS THE ABI v90 -> v91 BUMP.** Deferred out of slice 1 because at
   depth 1 a path IS a name; a nested table makes that false. Bump BOTH `FABRICATOR_ABI_VERSION` in
   `abi.h` and `vtable->AbiVersion` in `Bootstrap.cs`.
5. **Delta discovery goes depth-aware**, local FS first (the System.IO walk already recurses — cheapest),
   then S3/ADLS. ⚠ The object-store glob is a FIXED-depth pattern (`"/*/*/_delta_log/*.json"` vs
   `"/*/_delta_log/*.json"`) and `_schemas` is a BOOLEAN, so this is a strategy change, not a parameter.
   ⚠ Measure before choosing recursive-glob vs repeated globs: this repo records ~85 ms per active file
   on S3 and a per-match cost in `HostFsGlob`.
6. **Delta declares `supports_nested_schemas`** in its capability JSON (`DeltaCatalog` ~L1073) — and ONLY
   then, since the flag is what makes DuckDB offer `a.b`.
7. **Gates**: a table at depth 3 resolving as `cat.a.b.t`; `duckdb_schemas()` showing the nesting; a
   two-level control proving the existing layouts are unchanged; and a mutant.

⚠ Do NOT turn the capability on before steps 1-5, and keep SQL Server + Fabric `Tables/` off permanently.

### Slice 2 — as originally planned

- host: schema TREE + parent links; `FabricatorSchemaEntry::LookupEntry` answers `SCHEMA_ENTRY`; the
  graveyard keeps parents alive at least as long as children
- Delta: depth-aware discovery, local FS (the System.IO walk is already recursive-capable — cheapest) and
  S3/ADLS (⚠ the fixed-depth glob must become a recursive one or repeated globs, and the recorded
  per-match cost makes depth expensive — measure before choosing)
- `CREATE SCHEMA a.b` -> nested directory; `DROP SCHEMA` likewise
- everything else keyed on `schema.table`: DML routing, the synthetic `delta` / `fabric` function schemas,
  `schema_filter`, cache invalidation
- gates, including the one that matters: a table at depth 3 resolving as `cat.a.b.t`

### Slice 3 (optional, separate) — DAX per-model `system`

Only worth doing once slice 2 exists; it is the semantic-model case in §3 and needs no Delta work.

## 5. Traps already identified

- ⚠ **Do not widen `ReadStringTable`.** Its contract is *"all UTF-8 columns"* and 8 call sites rely on it;
  CLAUDE.md already records an OOB hazard in that area. Add a sibling.
- ⚠ **A schema name may legitimately contain a dot.** That is precisely why the path must be structural
  (a list) rather than encoded in a string — and why the capability gates the behaviour per provider.
- ⚠ **`SupportsNestedSchemas` also changes name RESOLUTION**, not just DDL: `Catalog::ResolveEntryName`
  *"prefer[s] an existing nested entry over a literal dotted schema"*. So turning it on changes how an
  existing dotted string argument resolves — a behaviour change for any provider that has such names.
