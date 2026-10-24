# Nested schemas (DuckDB 2.0) — the plan, and what is built

**Status (2026-10-23): SLICES 0, 1, 2a, 2b AND 2c BUILT — a Delta root attached with `nested_schemas
true` resolves `cat.a.b.t` to any depth, for READS AND WRITES. ABI v92. The only statement that still
cannot name a nested table is `COMMENT ON`, and the layer that stops it is DuckDB's own parser. Slice 3
(DAX per-model `system`) NOT started.**

> ⚠ **THE OPTION WAS RENAMED 2026-10-24: `nested_schemas true` IS NOW `schemas 'nested'`.** `schemas` and
> `nested_schemas` were two booleans spelling three states, and setting both silently meant `nested`. One
> option takes `'none' | 'onelevel' | 'nested'`, with `true`/`false` kept as PERMANENT spellings of
> `'onelevel'`/`'none'` (every existing two-level ATTACH uses `schemas true`). `nested_schemas` is REFUSED
> BY NAME rather than ignored — ATTACH options are free-form, so a leftover one would otherwise attach flat
> and lose every nested table without a message. No alias: it shipped only in the unpublished v0.2.0
> draft. The slice records below keep the old spelling because that is what they built and measured.
> Gate: `verify_delta_nested_schemas` §11.
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
- **⚠⚠ RETRACTED 2026-10-24 — THE DAX ARGUMENT ABOVE IS WRONG, and it was wrong when it was written.**
  It claimed *"two attached semantic models share ONE `cat.system`"*. They do not. A `DaxCatalog` is
  scoped to exactly ONE Analysis Services DATABASE (`_catalog`, resolved at construction and applied with
  `ChangeDatabase`), and an AS database holds exactly one Tabular model — `DiscoverModelNames()` reads
  `$SYSTEM.TMSCHEMA_MODEL` and the constructor takes `[0]`. So one ATTACH is one model plus its own
  `system`, and two attached models are two separate DuckDB CATALOGS (`a.system`, `b.system`) that cannot
  collide. **The plural in the code's own comment ("the model name(s)") is what the claim was read off,
  and the code beside it takes a single element.**
  - ⚠ READ FROM THE CODE, NOT MEASURED: `verify_dax` is the manual tier (it needs a live Power BI
    Desktop / SSAS / Fabric instance), so this is a source reading rather than a run. It is unambiguous
    enough to retract on — `GetSchemas()` is literally `new[] { _modelName, SystemSchema }` — but anyone
    with a live instance should confirm before relying on the retraction in the other direction.
  - **⇒ NOTHING WAS BUILT FOR IT.** Nesting `system` under the model inside one catalog
    (`cat.<model>.system.<dmv>`) would be a BREAKING rename of a working surface for no gain: the
    grouping it buys is cosmetic, and `cat.system.<dmv>` already means exactly one thing.
  - **⚠ THE REAL DAX CASE IS LARGER AND IS STILL OPEN: a WORKSPACE, not a model.** A Power BI workspace
    XMLA endpoint exposes MANY databases (one per dataset), and `DiscoverDefaultCatalog` picks the FIRST
    row of `$SYSTEM.DBSCHEMA_CATALOGS` and ignores the rest — so today you ATTACH once per dataset.
    Nesting would let one ATTACH name the workspace and give `cat.<dataset>.<table>`, which is the grist
    shape (`team -> workspace -> document -> tables`) and a genuine structural three-level need.
    ⚠ It is NOT the small provider-side change §3 implied: the catalog would span several databases, so
    the connection, every scan and the per-model function registrations each gain a database dimension.
    Size it before promising it.
  - ⇒ **grist remains the strongest argument for the feature**, and it is unaffected by this retraction:
    its hierarchy is the product's own, so under a two-part model one of workspace/document MUST be
    flattened into a compound name. The ABI work (v90/v91/v92) is done and is what that plugin needs.

- **⚠⚠ AND THE CLEANEST FIT IS A PLUGIN, NOT A FIRST-PARTY PROVIDER (user, 2026-10-23):
  `fabricator-grist` is `team -> workspace -> document -> tables`.** ATTACH names the team, and
  `cat.<workspace>.<document>.<table>` falls straight out. Unlike Delta's ad-hoc trees this is not an
  unusual layout anyone could avoid — it is the product's OWN hierarchy, so under a two-part model one of
  workspace/document MUST be flattened into a compound schema name, which is exactly the kind of
  name-mangling this feature exists to remove.
  ⇒ **TWO providers want it and grist is structurally three-level by design** (the DAX half of this
  count is RETRACTED above; its real case — a workspace spanning datasets — is open but much larger).
  That one of them is an out-of-tree PLUGIN is what still justifies changing the ABI rather than
  encoding paths in strings. ⚠ It also means the feature must work for an OUT-OF-TREE plugin, so whatever carries
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

### ✅ Slice 2b — something actually nests (BUILT 2026-10-23, ABI v90 -> v91, READ-ONLY)

`ATTACH … (PROVIDER 'delta', nested_schemas true)` and `cat.a.b.t` resolves, scans, and reports its
nesting in `duckdb_schemas()`. Gate: a NEW hermetic suite `verify_delta_nested_schemas` (**65**), floors
10089 -> **10154** and MIN_SUITES 82 -> **83**. Hermetic **83/83 — 10154**, i.e. 10089 + exactly 65, which
is the behaviour-neutrality claim for everything the slice touched outside the new option.

**What is on the wire.** `table_open` takes `const char *const *schema_path, int32_t schema_path_len`
instead of one `const char *schema` — the v91 bump, both halves (`FABRICATOR_ABI_VERSION`,
`vtable->AbiVersion`). The managed contract gains `IProviderCatalog.GetTable(IReadOnlyList<string>,
string)` as a DEFAULT INTERFACE METHOD that takes the LEAF, so an un-migrated provider — the four
out-of-tree plugins included, which pin `Fabricator.Abstractions` by SHA — is byte-for-byte unaffected.

**What is in the host.** `DiscoverSchemaPaths`/`DiscoverTables` stop flattening;
`FabricatorCatalog::EnsureSchemaPathLocked` walks a path creating one entry per level (ONE function, not
the two identical lambdas it replaced — LoadCatalog and RefreshCache must register the same tree or a
refresh reshapes the catalog); `ScanSchemas` and both `Scan` overloads report the tree;
`ClearTables`/`InvalidateEntryCache`/`InvalidateMatching` recurse into children.
`FabricatorTableInfo` gained `schema_path` ALONGSIDE `schema_name` — measured 20 consumers of the leaf,
and the ones that matter (functions, macros, views) resolve against the TOP-LEVEL map and are correct
*because* they do not nest.

**What is in the provider.** `nested_schemas` is OPT-IN and REFUSED on a OneLake root (Fabric's `Tables/`
is defined as exactly two levels). It supersedes `schemas` rather than extending it: nested mode is the
`schemas` layout generalised, so at depth 1 the two discover the identical thing. Local roots walk
directories (O(dirs) syscalls, stopping at any directory holding a `_delta_log` — a table's own
subdirectories are its data/partition folders); object stores take ONE recursive `**` glob.

#### ⚠⚠ NESTING IS READ-ONLY, BY A LOUD REFUSAL RATHER THAN BY OMISSION

Only `table_open` takes a path. `create_table` / `drop_table` / `begin_bulk` / `execute_update` /
`execute_delete` / `insert_returning` / `create_schema` / `drop_schema` still carry one `const char
*schema`, and handing them a nested schema's LEAF does not fail — it resolves to a DIFFERENT, same-named
top-level schema, or creates the table one level too shallow. `FabricatorRefuseNestedWrite` refuses each
BY NAME instead. Reads nest; writes say so.

- **⚠ ALTER and COMMENT ON are refused too, and my first version exempted them on a WRONG argument** —
  that they ride `table_alter` on the table SESSION, which was opened with the full path. The entry does;
  the managed side then calls `AlterTable(spec, definition.SchemaName, TableName)` and re-derives the
  storage path from the leaf, exactly like the flat crossings. **Found by reading the callee**, which is
  the rule this repo already records, after the SCAN was caught doing the same thing.
- ⚠ `COMMENT ON` never reaches the refusal anyway: DuckDB's PARSER caps it at three qualifications. The
  gate pins the parser's error, not ours — asserting ours would misname the layer that stops it.

#### ⚠⚠ FOUR DEFECTS, AND THREE OF THEM WERE SILENT OR MISLEADING

1. **`FabricatorCatalog::LookupSchema` never WALKED the path, and that is the one that made depth 3
   impossible.** It did a straight `schemas_.find()`, which answers only the FIRST level. `DuckCatalog`
   delegates to `Catalog::LookupSchemaPath` with a root-lookup lambda and nothing else; omitting that is
   omitting the walk. MEASURED: `cat.a.b.t` reported *schema "a.b" does not exist* while `cat.a` and
   `cat.s.t` both resolved. ⚠ **A two-level layout cannot see this** — its lookups never go past the root,
   which is why slices 1 and 2a stayed green with it missing.
2. **Slice 2a's `SCHEMA_ENTRY` branch sat BELOW the `!transaction.context` guard**, so it was unreachable
   from the walk: `Catalog::GetSchema` reaches `LookupEntry` through a CatalogTransaction carrying no
   ClientContext. Everything below that guard materializes an entry from the provider and genuinely needs
   a context; a child schema is already in memory and needs none.
3. **THE SCAN RE-DERIVED THE PATH FROM THE LEAF.** `DeltaTableBinding.Scan` called
   `Catalog.ScanCore(SchemaName, TableName, …)`, which recomputes `TablePath`. MEASURED in the bridge log,
   and the shape is the instructive part: `table_open` and the describe both used `<root>/a/b/t1`, then
   the scan opened `<root>/b/t1` and reported *"No Delta table found"*. The binding's own `Path` is the
   identity everything else on it already keys on; the scan was the one member that did not use it.
4. **⚠⚠ THE PATH OVERLOAD OF `TablePath` BROKE THE *FLAT* LAYOUT — a regression in code that only exists
   for nesting.** Since v91 the host calls `GetTable(path, …)` for EVERY table, so a depth-1 path reaches
   the path overload too; joining it unconditionally put a plain `PROVIDER 'delta'` catalog's tables at
   `<root>/main/<table>`. MEASURED: the attach wrote `<root>/loose` and then looked for
   `<root>/main/loose`, so the table vanished on re-attach. A depth-1 path now DELEGATES to the
   name overload, which is where the flat/schemas/OneLake question has always been answered.

#### ⚠⚠ "main" IS AN ORDINARY DIRECTORY HERE, AND A SMOKE TEST KILLED THE OTHER DESIGN

The first version gave a ROOT-LEVEL table the synthetic schema `main` (the flat layout's rule) and
resolved `["main"]` back to the root. A five-line smoke test ended it: `CREATE TABLE cat.main.t` under
`schemas true` makes a REAL `<root>/main/` folder, so the commonest existing layout would have collided
with the synthetic name on the very first ATTACH, and one of the two sets of tables would have read from
the wrong directory with nothing failing. ⇒ every segment is a real directory, and a Delta table sitting
directly under the root is SKIPPED with a warning naming it. That is not a regression: `schemas true`
does not discover one either (its glob is two levels deep). Both halves are gated, with the same root
attached FLAT as the control proving the table exists.

#### Measured, and not measured

- `**` matches zero or more directory levels in DuckDB's **local** glob — measured directly, including
  that a depth-1 table still matches.
- **⚠⚠ AND ON S3 TOO, MEASURED 2026-10-24 — this was the one open gap and it is closed.** The entry
  below used to read *"the httpfs/azure glob is a different code path and is NOT measured, so the
  object-store leg is reasoned, not tested"*. It is tested now: `verify_delta_catalog_s3` grew a nested
  section (199 -> **222**, engine-doubled, so the service floor moved 3593 -> **3639**) whose depth-3
  row can only pass if ONE `**` glob spans two levels against a real object store. Discovery, the
  depth-3 read, an INSERT and a DELETE at depth 2 all pass on BOTH engine legs, and the write is read
  back through a sub-root attached FLAT — the control that it landed where the path says.
  ⚠ **AZURE is still unmeasured**: it is a third glob implementation, and no CI tier can reach a real
  ADLS account (the `verify_delta_catalog_adls` suite is the manual/live-account tier).
- The plain-ADLS SDK fast path (`AdlsTableDiscovery`) is deliberately not used for nested roots: its walk
  is fixed at one or two levels. Nested ADLS pays the glob — correct, and slower.
- Depth is capped at 16 levels and REFUSES rather than truncating; silently not discovering a table is
  the failure mode the whole feature exists to avoid.

#### What slice 2c did — SUPERSEDED, see the section below

It predicted the work exactly ("widen the eight write crossings, each needs a managed DIM, and give
`AlterTable` the binding's resolved path") and got ONE thing wrong that cost a debugging round: it said
`AlterTable` needed "the binding's resolved path", when what it needed was the DEFINITION's — and the
definition had the path all along without exposing it.

### ✅ Slice 2c — writes nest too (BUILT 2026-10-23, ABI v91 -> v92)

The eight flat write crossings carry a schema PATH now, so the refusals slice 2b put in are GONE: INSERT,
UPDATE, DELETE, CREATE/DROP TABLE, CREATE/DROP SCHEMA and ALTER all work on a nested schema.
`verify_delta_nested_schemas` 65 -> **79** (its §5 went from asserting nine refusals to asserting the
writes), floor 10154 -> **10168**.

**The wire.** `create_table` / `drop_table` / `begin_bulk` / `execute_update` / `execute_delete` /
`insert_returning` / `create_schema` / `drop_schema` each take
`const char *const *schema_path, int32_t schema_path_len` in place of one `const char *schema`. For the
two SCHEMA entries the path is the schema's OWN, so `CREATE SCHEMA a.b` arrives as `["a","b"]`.
`bulk_insert` was left alone — it has had no C++ caller since the streaming bulk path replaced it.

**`table_alter` needed NO wire change, and that is the part worth remembering.** It rides the table
session, which was opened with the full path — but the managed handler then passed the definition's LEAF
name and the provider re-derived the wrong directory. The fix is `ITable.SchemaPath` (a DIM defaulting to
`[SchemaName]`) plus an `AlterTable` path overload. ⚠ Slice 2b had EXEMPTED ALTER from its refusal on
exactly the wrong half of this: *where the crossing rides* rather than *what the callee does with it*.

**Every managed widening is a DIM taking the leaf**, so the four out-of-tree plugins — which pin
`Fabricator.Abstractions` by SHA — keep working untouched. ⚠ `DeltaCatalog` inverts the direction: it
implements the PATH forms and derives the NAME forms from them, because for a provider that nests the
interface's leaf-taking default is exactly the wrong way round.

#### ⚠⚠ Two defects, and the second is the one worth carrying

1. **The ABI version was bumped in `abi.h` and NOT in `Bootstrap.cs`.** Caught immediately and loudly by
   the version check itself (*"ABI version mismatch (host=92, bridge=91)"*) — which is what that check is
   for, and the reason the bump rule says BOTH halves.
2. **⚠⚠ `DeltaTableDefinition` carried the path and did not EXPOSE it**, so ALTER kept taking the
   interface default. MEASURED: every other statement on a depth-2 table worked and `ALTER TABLE … ADD
   COLUMN` alone reported *"No Delta table found"* — a message that reads like a missing table rather
   than a wrong path. A DIM default is a silent fallback by construction; overriding the property is not
   optional for a provider that overrides the method.

#### What is left

`COMMENT ON` still cannot name a nested table: DuckDB's PARSER caps that statement at three
qualifications, so it never reaches us. The gate pins the PARSER's error rather than ours, and fails the
day upstream lifts the cap.

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

### ⛔ Slice 3 — DAX per-model `system`: NOT BUILT, and the premise is RETRACTED (2026-10-24)

§3's DAX argument was wrong: a `DaxCatalog` is one Analysis Services DATABASE, which holds one model and
one `system`, so two attached models are two DuckDB catalogs and never shared one. Nesting `system` under
the model would be a breaking rename of a working surface for a cosmetic gain. See the retraction in §3
for what was read, and for the DAX case that IS real and open — a workspace spanning many datasets,
which is a far larger change than this slice described.

## 5. Traps already identified

- ⚠ **Do not widen `ReadStringTable`.** Its contract is *"all UTF-8 columns"* and 8 call sites rely on it;
  CLAUDE.md already records an OOB hazard in that area. Add a sibling.
- ⚠ **A schema name may legitimately contain a dot.** That is precisely why the path must be structural
  (a list) rather than encoded in a string — and why the capability gates the behaviour per provider.
- ⚠ **`SupportsNestedSchemas` also changes name RESOLUTION**, not just DDL: `Catalog::ResolveEntryName`
  *"prefer[s] an existing nested entry over a literal dotted schema"*. So turning it on changes how an
  existing dotted string argument resolves — a behaviour change for any provider that has such names.
