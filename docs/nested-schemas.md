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
- **⛔ SQL SERVER AND FABRIC `Tables/` MUST LEAVE THE CAPABILITY OFF.** Their namespace is genuinely
  two-part; declaring nesting there would make DuckDB offer a syntax that resolves to nothing.

## 4. The slices

### ✅ Slice 0 — the capability (BUILT 2026-10-23, inert, no ABI bump)

`FabricatorCapabilities.supports_nested_schemas`, parsed from the capability doc beside
`is_binary_collation` / `exact_filter_pushdown` / `null_order_expressible`, surfaced as
`FabricatorCatalog::SupportsNestedSchemas()`. The capability JSON is additive, so **no version bump**.
**Nothing declares it true** — reporting the capability without the wire shape would let DuckDB resolve
`a.b` against a catalog that cannot express it. Gate: hermetic **82/82 — 10089**, identical, i.e. inert.

### Slice 1 — the wire shape (ABI v89 -> v90), BEHAVIOUR-NEUTRAL

Carry a path everywhere, with **every provider still emitting depth-1 paths**, so nothing nests yet.

- the five `catalog_*` streams: schema column `VARCHAR` -> `LIST<VARCHAR>`
- a path-aware reader beside `ReadStringTable` (do NOT widen that one — 8 call sites depend on its
  all-strings contract)
- `table_open(handle, const char *const *schema_path, int32_t schema_depth, const char *table, …)`
- the five managed producers (`DeltaCatalog`, `SqlServerBackend`, `DaxCatalog`, `DeltaRsCatalog`,
  `StubBackend`) emit single-element paths
- **the claim to test is arithmetic: both tiers come back ASSERTION-IDENTICAL.** That is the whole reason
  this is its own slice — a combined slice could not prove the refactor disturbed nothing.

### Slice 2 — actual nesting

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
