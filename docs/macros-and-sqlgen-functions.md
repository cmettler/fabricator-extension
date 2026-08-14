# C#-declared DuckDB macros + SQL-generating (bind-replace) table functions — design

Status: **ALL THREE PHASES BUILT + VERIFIED 2026-07-24** — Phase A macros (§1.3 "AS BUILT", no ABI bump),
Phases B+C SQL-generating table functions global + catalog-bound (§2.5 "AS BUILT", ABI v68). Written against
DuckDB v1.5.5 source (the in-tree `duckdb/` clone) and the fabricator global-function machinery.

Two related features, one theme — letting a provider ship **SQL** instead of marshaled execution:

1. **Registering DuckDB MACROs (scalar + table) from C#** — static SQL templates, expanded by
   DuckDB's binder. No new ABI entry.
2. **SQL-generating table functions** — the `query_table(...)` mechanism generalized: a C# function
   receives its bind-time constant args and returns a **SQL statement string**; a `bind_replace`
   hook substitutes the parsed statement for the function call in the plan. Dynamic SQL, where a
   macro's static template can't reach. One appended ABI entry (v68).

The dividing rule: **if the SQL text is fixed and only *values* vary → macro** (binder substitutes
parameters as expressions — structure and identifiers are frozen, injection-free by construction);
**if the SQL *text* depends on the args → SQL-generating function** (object names from args,
IN-list/pivot expansion, per-arg UNION fan-out, provider-side metadata lookups at bind time).

---

## 1. Registering DuckDB macros from C#

### 1.1 DuckDB internals (source-verified, v1.5.5)

- A macro is a catalog entry (`MACRO_ENTRY` scalar / `TABLE_MACRO_ENTRY` table) whose payload is a
  vector of `MacroFunction`s (`CreateMacroInfo::macros` — several = overload set;
  `duckdb/parser/parsed_data/create_macro_info.hpp:16-19`). A `ScalarMacroFunction` holds a parsed
  `ParsedExpression`; a `TableMacroFunction` holds a parsed `QueryNode`. Parameters are
  `ColumnRefExpression`s; named parameters carry default-value expressions
  (`function->default_parameters`).
- **Extensions register macros at load via `ExtensionLoader::RegisterFunction(CreateMacroInfo &)`**
  (`extension_loader.hpp:71`) — it writes the entry into the **SYSTEM catalog** under a system
  transaction (`extension_loader.cpp:133-137`), so the macro is visible in every database of the
  instance, exactly like a built-in function. This is the same loader we already use for global
  scalars, so macros slot into `RegisterFabricatorGlobalFunctions` naturally.
- Two in-tree construction helpers exist (`default_functions.hpp:19-34`,
  `default_table_functions.hpp:12-40`): `DefaultMacro`/`DefaultTableMacro` structs (schema, name,
  `parameters[8]`, `named_parameters[8]` with default-value text, body text) +
  `DefaultFunctionGenerator::CreateInternalMacroInfo` / `DefaultTableFunctionGenerator::
  CreateTableMacroInfo`, which parse the body (`Parser::ParseExpressionList` for scalar) and build
  the info. Flag convention set there (`default_functions.cpp:212-215`): `temporary = true`,
  `internal = true`.
- The full `CREATE MACRO` grammar — including **defaults** (`n := 8`) and **overload sets**
  (`CREATE MACRO m(a) AS ..., (a, b) AS ...`) — is parsed by DuckDB's own transformer into a
  `CreateStatement` whose `info` IS a `CreateMacroInfo`
  (`parser/transform/statement/transform_create_function.cpp:68`).

### 1.2 Design: C# ships the full `CREATE MACRO` DDL text; C++ parses + registers

The provider declares each macro as **one complete `CREATE MACRO` statement string**. The C++ load
path parses it with DuckDB's own `Parser`, takes the resulting `CreateMacroInfo`, stamps the
internal flags, and hands it to `loader.RegisterFunction(info)`.

Why DDL text instead of a structured (name/params/body) declaration:
- **Zero bespoke encoding.** One string crosses; DuckDB's parser is the single source of truth for
  the grammar — defaults, overload sets, `AS TABLE`, comments all work day one.
- **No 8-parameter cap** (the `DefaultMacro` fixed arrays) and no scalar/table kind flag — the
  parsed statement already knows (`CatalogType::MACRO_ENTRY` vs `TABLE_MACRO_ENTRY`).
- **Exact errors.** A malformed body produces DuckDB's own parser error, with the provider and
  macro name prepended by our wrapper.

The structured `DefaultMacro`-array route (Option B) remains the documented fallback if we ever
need to construct macros programmatically (e.g. generated bodies) — but the DDL route subsumes it.

#### C# authoring surface (Fabricator.Abstractions — plugins get it for free)

```csharp
/// <summary>A DuckDB macro this provider ships, as a complete CREATE MACRO statement.
/// Name is used for cross-provider duplicate detection and logging only — the authoritative
/// name/params/body come from parsing CreateSql on the C++ side.</summary>
public sealed record MacroDefinition(string Name, string CreateSql);

public partial interface IBackend
{
    /// <summary>Macros to register at extension load (system catalog, all databases).</summary>
    IEnumerable<MacroDefinition> GlobalMacros => Array.Empty<MacroDefinition>();
}
```

`GlobalFunctions` gains a `MacroMap` (union across providers, duplicate `Name` = fatal config
error, same as every other kind) + `AllMacros()`.

#### Crossing: a new `body` column on `list_global_functions` — NO ABI bump

`list_global_functions` currently emits `(name, kind, string_order, param_count, return_type)`;
the C++ registrar reads the **three leading string columns** positionally
(`fabricator_schema_entry.cpp:1795-1801`, `ReadStringTable(stream, 3)`). We add kind
**`"macro"`** and a **fourth leading string column `body`** (empty for every other kind), and bump
the read to `ReadStringTable(stream, 4)`. Precedent: the `string_order` column rode this stream
with no ABI bump (the vtable is unchanged; both sides move in lockstep — the decl-stream schema is
bridge-internal, and a stale-loadable mismatch already fails loudly at the ABI version check).

#### C++ registration (`RegisterFabricatorGlobalFunctions`, new `kind == "macro"` branch)

```cpp
} else if (kind == "macro") {
    try {
        Parser parser;                       // default options — the DDL is provider-authored
        parser.ParseQuery(bodies[i]);        // the full CREATE MACRO statement
        if (parser.statements.size() != 1 ||
            parser.statements[0]->type != StatementType::CREATE_STATEMENT) {
            throw ParserException("expected a single CREATE MACRO statement");
        }
        auto &create = parser.statements[0]->Cast<CreateStatement>();
        if (create.info->type != CatalogType::MACRO_ENTRY &&
            create.info->type != CatalogType::TABLE_MACRO_ENTRY) {
            throw ParserException("expected CREATE MACRO (scalar or TABLE)");
        }
        auto info = unique_ptr_cast<CreateInfo, CreateMacroInfo>(std::move(create.info));
        info->temporary = true;              // the DefaultFunctionGenerator convention
        info->internal = true;               // (default_functions.cpp:212-215)
        info->on_conflict = OnCreateConflict::ERROR_ON_CONFLICT;
        loader.RegisterFunction(*info);
    } catch (std::exception &ex) {
        // best-effort like the rest of load-time registration: WARN + skip, never fail the load
        FABRICATOR_LOG_WARN("global macro '%s' skipped: %s", fn_name, ex.what());
        continue;
    }
}
```

Implementation checkpoints (verify while building):
- The `temporary=true, internal=true` pair is what `CreateInternalMacroInfo` returns and what the
  system-catalog `CreateFunction` path expects for extension entries — confirm against an in-tree
  extension registering macros if anything rejects.
- `info->schema`: the parsed statement carries the user-written qualification; force
  `DEFAULT_SCHEMA` ("main") unless we deliberately allow provider-namespaced macros.
- Duplicate name vs a DuckDB built-in / another extension → `ERROR_ON_CONFLICT` surfaces it at
  load; our WARN+skip keeps the extension usable. Providers must prefix (`fabricator_*`, `dax_*`).

#### What macros give us (examples)

```sql
-- scalar: decode the transient Delta rowid (docs/rowid-concepts.md) into its two halves
CREATE MACRO fabricator_rowid_parts(rid) AS
    {file_ordinal: rid >> 40, row_position: rid & ((1::BIGINT << 40) - 1)};

-- scalar with a named default: sugar over the bucket() global scalar
CREATE MACRO fabricator_bucket_of(v, n := 8) AS bucket(n, v);

-- table macro: head of any Delta table, no ATTACH
CREATE MACRO fabricator_delta_head(path, n := 100) AS TABLE
    SELECT * FROM fabricator_delta_scan(path) LIMIT n;
```

Registered at load → available bare in every database, composable with all our functions, and the
binder substitutes `rid` / `path` / `n` as **expressions** (no string interpolation anywhere).

### 1.3 AS BUILT (2026-07-24)

Shipped exactly as designed above — the DDL-text route, no ABI bump. Files:
`dotnet/Fabricator.Abstractions/MacroDefinition.cs` (new) + `IBackend.GlobalMacros`;
`GlobalFunctions.MacroMap`/`AllMacros()`; `Bootstrap.ListGlobalFunctions` (kind `macro` + the new `body`
column); `RegisterFabricatorGlobalFunctions`'s `kind == "macro"` branch + `ReadStringTable(stream, 4)`
(`src/catalog/fabricator_schema_entry.cpp`); demos in `Fabricator.SqlServer/CustomFunctions.GlobalMacros`
and `Fabricator.SamplePlugin`. Tests: **`test/verify_macros.test` (41)** + `verify_plugin.test` (10, +4 for
the plugin-macro sections). Regression: global_functions 63 / custom_functions 89 / hilbert 27 / bucket 34.

Findings + deviations from the plan:

- **Flags: `internal = true` only** — NOT the `DefaultFunctionGenerator`'s `temporary = true, internal = true`
  pair. `BuiltinFunctions::AddFunction` (`built_in_functions.cpp:31-58`) sets only `internal`, and that is the
  right parallel for a load-time system-catalog entry (`temporary` is a lazily-generated-default concern).
  Verified: the entries behave like built-ins.
- **A defaulted macro parameter also accepts a POSITIONAL argument**: `fabricator_bucket_of('alice', 16)`
  works, not just `n := 16`. So a named default covers the "optional trailing arg" case completely — an
  overload set is only needed for genuinely different signatures (different arity *shapes*).
- **An overload set is ONE catalog entry but N rows in `duckdb_functions()`** (one per overload) — a
  `DISTINCT`/count-aware test query is required (pinned in `verify_macros.test` §1).
- **The load-time WARN is only observable on the `LOAD` path.** `DUCKDB_LOG_WARNING` at
  `Extension::Load` cannot reach `duckdb_logs` in a statically-linked binary (the load precedes any query, so
  logging can never have been enabled yet). With the loadable it works: `CALL enable_logging(...); LOAD
  fabricator;`. The *behavioral* contract (skip, don't block) is what the tests pin — the sample plugin ships
  a deliberately malformed `plug_bad_macro` beside a good `plug_double` for exactly that.
- **Provider qualification is rejected, not ignored**: a `CREATE MACRO somecat.someschema.foo(...)` is skipped
  with a warning (macros land in the system catalog's `main`; provider namespacing goes in the NAME prefix).
  This settles open question #2 in favor of "force main".

Demos now shipping (SQL Server provider = the always-present default, like `fabricator_render`):
`fabricator_rowid_parts(rid)` (scalar → struct), `fabricator_rowid_of(ordinal, position)` /
`(parts)` (**overload set**, the round-trip inverse), `fabricator_bucket_of(v, n := 8)` (**named default**),
`fabricator_delta_head(path, n := 100)` (**table macro** over `fabricator_delta_scan`).

#### Deliberately out of scope (deferred)

- Macro **removal/replacement** at runtime (they live in the system catalog for the process
  lifetime; `CREATE OR REPLACE` semantics across re-loads are a non-goal).

### 1.4 Catalog-bound (attach-time) macros — BUILT 2026-07-30, no ABI bump

Shipped after re-examining a deferral whose *rationale* turned out to be half wrong. The old entry deferred
these on the grounds that "the catalog-bound SQL-generating function (§2) covers that need better — the
generator is TOLD the alias". That is true for **one** of the two needs and false for the other, which is
the half that got built. Both halves of the mechanism were source-verified before writing any code.

**The binder half works, and by a pattern we already ship.** DuckDB resolves both macro kinds by looking
up a *function* type and then dispatching on the entry's ACTUAL type, so a macro entry handed out by
`FabricatorSchemaEntry::LookupEntry` is expanded normally:

| kind | binder looks up | dispatches on |
|---|---|---|
| scalar | `SCALAR_FUNCTION_ENTRY` | `MACRO_ENTRY` → `BindMacro` (`bind_function_expression.cpp:85,164`) |
| table | `TABLE_FUNCTION_ENTRY` | `TABLE_MACRO_ENTRY` → `BindTableMacro` (`bind_table_function.cpp:369,371`) |

That is the SAME trick our custom aggregates already ride — `LookupEntry`'s `SCALAR_FUNCTION_ENTRY` branch
must surface them because DuckDB shares one namespace across scalar/aggregate/macro — so this needs no new
pattern. Ctors match what we already construct: `ScalarMacroCatalogEntry(Catalog &, SchemaCatalogEntry &,
CreateMacroInfo &)`. And the table path goes through `GetCatalogEntry(catalog, schema, lookup)`, so a
qualified `db.schema.m(…)` routes into our schema entry.

**The resolution caveat is SHARPER than "bake the alias into parsed expressions".** Macro expansion
captures no search path at all (verified, with a positive control — `SetSearchPath` does exist elsewhere
in the tree, so the empty result is a real absence and not a mistyped pattern); `BindTableMacro` merely
returns a `QueryNode` that the CALLING binder binds. So a schema buys **namespacing, not resolution
scope**: a catalog-bound `CREATE MACRO recent() AS TABLE SELECT * FROM orders` resolves `orders` against
the *caller's* search path. That is a silent-wrong-table shape, not an error.

**Hence the split the old rationale missed — §2 is TABLE-valued only** (`bind_replace` → SubqueryRef):

- a table macro that references its own catalog → **use sqlgen**, exactly as the old entry said. Unchanged.
- a per-catalog **scalar** helper → sqlgen has no scalar analog at all, and the catalog-bound custom scalar
  (4e, `ICatalogScalarFunction`) is **marshaled** — data crosses the ABI per call. A catalog-bound scalar
  macro is expanded by the binder with **zero runtime crossing**. Nothing else in the stack offers that
  combination, so "§2 covers it" was never true for this case.

#### AS BUILT — and the one design decision that changed during the build

The plan sketched here first said the macro body should ride a **trailing `body` column on the FUNCTIONS
metadata stream**, mirroring what `list_global_functions` does. That was **wrong, and reading the producer
is what settled it**: `SqlServerCatalog.FunctionsMetadataSql()` builds that stream as **T-SQL text executed
on the server** (custom functions are appended as `UNION ALL SELECT '<schema>','<name>','kind',…`). Carrying
macro bodies there would have embedded a multi-line `CREATE MACRO` statement in a T-SQL literal, shipped it
to SQL Server, and read it back — for a declaration that is purely LOCAL. Worse, it would have made
declaring a macro depend on **server reachability**, and it offers nothing at all to a provider with no SQL
engine (the Delta catalog).

So the body travels on **its own metadata kind**, `FABRICATOR_META_CATALOG_MACROS = 15` — three string
columns (schema, name, create_sql). Adding a metadata kind is additive, so this is still **no ABI bump**, and
the FUNCTIONS column layout is untouched (nothing else had to change, and `fabricator_functions()` is
unaffected).

What shipped:

- **C#** — `CatalogMacroDefinition(SchemaName, Name, CreateSql)` in `Fabricator.Abstractions` +
  `IBackend.CatalogMacros` (default empty, so every provider and plugin gets the surface for free);
  `CatalogMacroMetadata.Stream(...)` in the Bridge builds the kind-15 table; the Delta and SQL Server
  catalogs each answer the kind from their backend's declaration. The Delta declarations use an `__all__`
  schema sentinel that `DeltaCatalog.ExpandCatalogMacroSchemas()` expands over the discovered schemas — a
  Delta root's schemas are *folder names*, unknown until ATTACH, so a static declaration cannot name them.
- **C++** — `DiscoverCatalogMacros` (`fabricator_metadata.cpp`, never throws: declaring macros is optional);
  registration in **both** `LoadCatalog` and `RefreshCache`, gated on the schema having been registered, so
  an ATTACH `schema_filter` gates macros exactly as it gates functions; `AddMacro` + `GetOrCreateMacro` on
  `FabricatorSchemaEntry` with the **retire-never-destroy graveyard** on eviction; the `LookupEntry`
  fallthroughs (scalar → aggregate → **macro**, and table function → **table macro**) plus direct
  `MACRO_ENTRY`/`TABLE_MACRO_ENTRY` lookups; and both `Scan` overloads.
- **The qualification rule INVERTS**, as predicted: the global path *rejects* a qualified body (§1.3, "force
  main"), the catalog-bound path *overwrites* `info->catalog`/`info->schema` with the ATTACH alias and the
  owning schema. Do not copy the global branch's validation into this path — it asserts the inverse
  invariant. Nor `internal = true`: that marks a built-in, which is right for a load-time system-catalog
  entry and wrong for a catalog one (no other catalog entry of ours sets it).

Findings and deviations:

- **`want_table` filtering is a correctness requirement, not tidiness.** The binder `Cast<>`s on the entry
  type *without checking* (`bind_function_expression.cpp:164`), so returning a table macro from a scalar
  lookup is an unchecked bad cast. `GetOrCreateMacro` therefore takes the wanted kind and returns nullptr
  for a mismatch — i.e. "no such function", which is the truthful answer for that lookup. Both directions
  are pinned (§5 of the suite). The mismatched entry is still cached, so the *matching* lookup does not
  re-parse.
- **Macros must be emitted by the SCALAR / TABLE_FUNCTION scans, not the MACRO ones.** `duckdb_functions()`
  only ever scans `SCALAR_FUNCTION_ENTRY` / `TABLE_FUNCTION_ENTRY` / `PRAGMA_FUNCTION_ENTRY`
  (`duckdb_functions.cpp:101-105`) and then switches on each entry's actual type (lines 806-826, which do
  handle both macro types). Emitting them only under `MACRO_ENTRY` would resolve by name but never appear in
  `duckdb_functions()` / `SHOW FUNCTIONS`.
- **`GetOrCreateMacro` is the only `GetOrCreate*` here that makes NO bridge call** — a body is a declaration
  already in hand, so materializing it is a local parse. `context` is used only for parser options and the
  skip warning.
- **A broken declaration is SKIPPED with a warning, never fatal** — the same contract §1.3 ships for the
  global form, and it must be a skip rather than a throw because `Scan` materializes too: throwing there
  would break enumeration of the whole schema, a far worse failure than one absent macro. The declaration is
  also dropped so a broken body is not re-parsed on every lookup. Verified against three break modes
  (unparseable body, a non-`CREATE MACRO` statement, and a declared-name/body-name mismatch): the ATTACH
  succeeds, the good macros still work, and none of the broken ones enumerate.
- **A latent out-of-bounds read in `ReadStringTable` surfaced and is fixed.** It reads the first N columns
  without checking the batch width, and a provider answering a metadata kind it does not implement returns a
  different shape — the Delta/DAX `_ =>` arm is a ONE-column empty table, which is exactly what a Delta
  catalog gives for FUNCTIONS while `DiscoverFunctions` asks for three. That has always been an OOB read on
  paper; it survives only because those fallbacks have zero rows, so the row loop never dereferences the
  missing children. The check is now per BATCH and conditional on `length > 0`, which keeps that legitimate
  reliance working while closing the real hazard. **An earlier attempt to validate the SCHEMA's width
  instead broke every Delta ATTACH immediately** ("metadata stream has 1 columns, expected at least 3") —
  a useful reminder that the lenient behaviour was load-bearing, not merely tolerated.

Gate: **`test/verify_macros_catalog.test` (50)**, hermetic — a Delta catalog over an empty
`FABRICATOR_DELTA_WRITE_DIR`, deliberately not the SQL Server provider even though it declares one too,
because a macro needing no server is part of the design and a hermetic suite proves it. Hermetic tier
60 runs/5471 → **61/5521**.

---

## 2. SQL-generating table functions (the `query_table` mechanism, provider-authored)

### 2.1 DuckDB internals (source-verified, v1.5.5)

- Hook: `table_function_bind_replace_t = unique_ptr<TableRef>(*)(ClientContext &,
  TableFunctionBindInput &)` (`function/table_function.hpp:290`, field `:412`).
- Binder flow (`planner/binder/tableref/bind_table_function.cpp:197-236`): args are folded to
  constant `Value`s and packed into `TableFunctionBindInput` (`inputs` + `named_parameters`);
  `bind_replace` runs **before** the regular `bind`; a non-null `TableRef` gets the call site's
  alias + column aliases copied onto it and is then **fully re-bound** (`return Bind(*new_plan)`)
  — the replacement is an ordinary plan subtree afterwards. Returning `nullptr` falls through to
  the regular `bind` (hybrid possible); `nullptr` with no `bind` → clean `BinderException` (`:236`).
- `query()` / `query_table()` (`function/table/query_function.cpp`) are exactly this: a
  `TableFunction` constructed with **null bind and null function**, only `bind_replace` set; the
  hook builds SQL text, `Parser::ParseQuery`s it, requires a **single SELECT statement**, and
  returns `make_uniq<SubqueryRef>(select_stmt)` (`ParseSubquery`, `:11-32`). Known upstream
  limitation baked into that helper: a `PIVOT` without an explicit `IN` list parses to a
  MultiStatement and is rejected — we inherit the same error.

Consequences that make this attractive for fabricator:

- **Zero data crosses the ABI at execution.** The function call disappears at bind; what runs is a
  native DuckDB plan over whatever the SQL references — including our own catalog scans, which then
  receive the full existing pushdown machinery (projection/filter/TopN into SQL Server, Delta file
  + row-group pruning, parallelism, join reordering). This is the structural advantage over the
  v29 table session (`tablefn_bind`/`tablefn_execute`), where results stream through C# as an opaque
  Arrow source.
- **Arg-dependent output schemas are free.** The schema falls out of binding the generated SQL — C#
  declares no output schema at all (contrast `ITableFunction.Bind`'s `OutputSchema`).

### 2.2 Design

New function kind **`table_sql`**, both **global** (handle-0, load-time) and **catalog-bound**
(attach-time, three-part name). The C# side is a pure `args → SQL text` transform.

#### C# authoring surface (Fabricator.Abstractions)

```csharp
/// <summary>A table function implemented as a SQL REWRITE: at bind time DuckDB hands the
/// constant args to GenerateSql and substitutes the returned (single SELECT) statement for the
/// call. No data crosses the bridge at execution — the SQL binds as a native plan.</summary>
public interface ISqlTableFunction
{
    string Name { get; }
    /// <summary>Positional bind-time constants. A SQLNULL-typed field is the ANY sentinel
    /// (accept any runtime type), same as the existing table-bind convention.</summary>
    Schema Parameters { get; }
    /// <summary>Optional named constants (e.g. by_name := true), with the field's type.</summary>
    // (named parameters are fields of Parameters tagged via Params.Named — one schema, see ParamStyle.cs)
    /// <summary>args = ONE 1-row batch: positional fields first (declared order), then the
    /// SUPPLIED named params by field name. Return exactly one SELECT statement.</summary>
    string GenerateSql(RecordBatch args);
}

/// <summary>Catalog-bound flavor: resolved as db.schema.fn(...); the generator is handed the
/// catalog's DuckDB ATTACH alias so it can emit fully-qualified references into its own catalog
/// (C# does not otherwise know the alias — only the C++ side does).</summary>
public interface ICatalogSqlTableFunction
{
    string SchemaName { get; }
    string Name { get; }
    Schema Parameters { get; }
    // (named parameters are fields of Parameters tagged via Params.Named — one schema, see ParamStyle.cs)
    string GenerateSql(string catalogName, RecordBatch args);
}
```

Registries: `IBackend.GlobalSqlTableFunctions` → `GlobalFunctions.SqlTableMap` (handle-0 path);
catalog-bound ones join the `CustomFunctions`-style per-catalog registry and are surfaced through
`FunctionsMetadataSql` with `kind='table_sql'` (consulted before SQL discovery, like
`CustomScalar`/`CustomTable`).

#### ABI v68 — ONE appended entry

```c
// Generate the replacement SQL for a table_sql function. handle == 0 => the global registry
// (schema empty); non-zero => the catalog registry (schema.func). catalog_name = the DuckDB
// ATTACH alias for catalog-bound calls ("" for globals) — C# splices it into qualified names.
// args = the 1-row constant batch (consumed). *out_sql = owned UTF-8, freed via free_error.
int32_t (*generate_table_sql)(FabricatorHandle handle, const char *schema, const char *func,
                              const char *catalog_name, struct ArrowArrayStream *args,
                              char **out_sql, char **err);
```

Appended → offsets stable; bump `FABRICATOR_ABI_VERSION` 67→68 + `vtable->AbiVersion` in lockstep
(the exact-match rule: rebuild loadable + republish bridge from one commit).

#### C++ — registration + the shared bind_replace hook

Global (`RegisterFabricatorGlobalFunctions`, new `kind == "table_sql"` branch):

```cpp
TableFunction tf(fn_name, arg_types, /*function*/ nullptr, /*bind*/ nullptr);
for (...) tf.named_parameters[name] = type;      // SQLNULL => LogicalType::ANY, as today
tf.bind_replace = FabricatorSqlGenBindReplace;
tf.function_info = fn_info;                      // FabricatorTableFunctionInfo{handle=0, "", name}
loader.RegisterFunction(tf);
```

Catalog-bound: `FabricatorSchemaEntry::GetOrCreateTableFunction` gets a `table_sql` branch that
builds the same shape (function/bind null + `bind_replace`) instead of the v29 session callbacks;
the entry is cached/self-healing like every discovered TVF. A name is EITHER `table_sql` or
session-based — never both.

The hook (mirrors `QueryBindReplace` + our arg marshaling):

```cpp
static unique_ptr<TableRef> FabricatorSqlGenBindReplace(ClientContext &context,
                                                        TableFunctionBindInput &input) {
    auto &info = input.table_function.function_info->Cast<FabricatorTableFunctionInfo>();
    // catalog-bound generators may run discovery SQL on their provider connection:
    FabricatorSetActiveTxn(context, info.handle);        // sets txn id + active opener (no-op for handle 0)
    // 1-row constant batch: positional inputs in declared order ++ supplied named parameters
    //   (lift the existing v29 table-bind marshaling in FabricatorTableFunctionBind verbatim)
    auto args = MarshalConstantArgs(context, info, input.inputs, input.named_parameters);
    string sql = fabricator::GenerateTableSql(info.handle, info.schema, info.func,
                                              CatalogAliasOf(info), std::move(args)); // throws w/ fn name
    // exact ParseSubquery semantics from query_function.cpp: single SELECT statement -> SubqueryRef
    return ParseSingleSelect(sql, context.GetParserOptions(), info.func);
}
```

The binder then re-binds the `SubqueryRef` in the calling context — alias handling, column aliases,
`DESCRIBE`, views, CTEs all behave as if the user had typed the SQL.

`CatalogAliasOf`: for a catalog-bound entry the attach alias comes from the owning
`FabricatorCatalog`'s `GetName()` (resolved once at entry creation, not per bind); globals pass "".

### 2.3 Semantics, edge cases, rules

- **Args must be bind-time constants.** The binder folds constant expressions into `Value`s; a
  `PREPARE` parameter or column ref as an argument is not foldable → clean binder error (identical
  to `query_table`). Document in the authoring guide.
- **NULL args**: rejected up front in the hook (the `query_table` precedent, `query_function.cpp:37`)
  unless the parameter is declared nullable-meaningful by the function — keep the C# generator in
  charge (it sees validity) but ship the guard as the default in `MarshalConstantArgs`.
- **The generator must be pure/deterministic per args and side-effect-free.** Binds happen without
  execution (`EXPLAIN`, `DESCRIBE`, view creation) and REPEAT (each re-bind of a view or prepared
  statement regenerates). This is a documented authoring contract, same class as
  `IScalarFunction.IsVolatile`.
- **Quoting/injection**: the generated text embeds user args — the provider is responsible for
  quoting. Ship `DuckSql.QuoteIdent(string)` + `DuckSql.Literal(object?)` helpers in Abstractions
  (precedent: the SQL provider's `Quote`/`ObjectLiteral`, the DAX literal builder). Not a privilege
  boundary (the SQL runs as the calling user, in their session, with their secrets) — purely a
  correctness/robustness concern.
- **Global generators must not guess ATTACH aliases.** A global function referencing
  `lake.main.t` breaks under a different alias — either take the catalog name as an argument or use
  the catalog-bound form (alias injected). Put this in the doc's authoring rules.
- **Single SELECT statement only**; the PIVOT-without-IN MultiStatement limitation is inherited
  from upstream `ParseSubquery` and reported with the same guidance.
- **Recursion**: generated SQL may reference macros (§1) or other table_sql functions; runaway
  self-reference dies on the binder's recursion/depth guards — acceptable, document.
- **Volatile generators + plan caching**: DuckDB re-binds per query; there is no cross-query plan
  cache to poison. `PREPARE` binds once — the SQL is frozen at prepare time (fine; args are
  constants anyway).
- **Errors**: C# exceptions surface through `err` → `BinderException("fabricator function %s: %s")`
  with the provider message (which already prepends provider error numbers via `FormatError`).

### 2.4 Motivating examples

```sql
-- catalog-bound, SQL Server provider: UNION ALL over every table matching a pattern.
-- C# lists matching tables on ITS OWN connection at bind time (sys.tables), quotes each,
-- and generates:  SELECT * FROM "db".dbo."sales_2024" UNION ALL BY NAME SELECT * FROM ...
SELECT * FROM db.dbo.union_by_pattern('sales_%', by_name := true);

-- catalog-bound, DAX provider: dynamic SUMMARIZECOLUMNS authoring — the generator builds the
-- EVALUATE text from the args and emits a plain call to the existing daxeval TVF:
SELECT * FROM dax."Model".measure_by('Total Sales', by := ['Product[Category]']);
--   -> SELECT * FROM "dax"."Model".daxeval(expression := 'EVALUATE SUMMARIZECOLUMNS(...)')

-- global demo (test workhorse, no provider state): args -> range SQL
SELECT * FROM fabricator_sql_seq(5);   -- -> SELECT range AS i, range*range AS sq FROM range(1, 6)
```

The `union_by_pattern` case is the one neither a macro (identifier list is dynamic) nor an
`ITableFunction` (would stream everything through C#, losing per-table pushdown) can do well.

---

### 2.5 AS BUILT (2026-07-24, ABI v68 — Phases B + C)

Shipped as designed, with two deliberate refinements (below). Files:
`Fabricator.Abstractions/ISqlTableFunction.cs` (`ISqlTableFunction` / `ICatalogSqlTableFunction` /
`SqlGenContext`) + `DuckSql.cs` (quoting helpers) + `IBackend.GlobalSqlTableFunctions` +
`IBackendCatalog.GenerateTableSql` (a throwing DIM, so no other provider changed);
`Fabricator.Bridge/SqlGen.cs` (arg validation, the named-parameter tag, generation) +
`GlobalFunctions.SqlTableMap`; `abi.h` `generate_table_sql` (appended, v68) + `clr_host` wrapper +
`Abi.cs`/`Bootstrap.cs` handler; `fabricator_schema_entry.cpp`
(`FabricatorSqlGenBindReplace` + `FabricatorParseGeneratedSelect` + `FabricatorBuildSqlGenSignature` +
`GetOrCreateSqlTableFunction` + `AddSqlTableFunction`), `fabricator_catalog.cpp` discovery kind
`table_sql`, `fabricator_metadata.cpp` (`FetchFunctionParamSchema(out_named)`). Demos:
`fabricator_sql_seq` + `fabricator_delta_union` (global), `dbo.cf_union_by_pattern` (catalog-bound).
Tests: **`test/verify_sqlgen.test` (59)** + **`test/verify_sqlgen_catalog.test` (30)**; regression: all 17
function suites green (global_functions 63 / custom_functions 89 / scalar 26 / table 33 / procs 24 /
aggregates 58 / table_inout 63 / proc_inout 31 / collector 40 / functions 13 / macros 41 / plugin 10 /
catalog_filter 7 / function_filter 15 / invalidate_scoped 18).

Refinements vs the plan:

- **Named parameters cross on ONE schema, tagged in FIELD METADATA** — not a second ABI fetch. A
  `table_sql` function's `get_function_param_schema` returns positional fields followed by named ones
  carrying `fabricator.named = "1"` (`SqlGen.ParamSchema`); `FetchFunctionParamSchema` gained an optional
  `out_named` flag vector read from that metadata BEFORE `ReadArrowSchema` consumes the struct — the exact
  channel + shape the volatility signal already uses (`fabricator.volatile`). So no ABI entry beyond
  `generate_table_sql`, and the split into DuckDB's `arguments` vs `named_parameters` happens in one shared
  helper for both the global and catalog registration paths.
- **The catalog-bound generator gets a `SqlGenContext`, not just the alias string.** It carries the ATTACH
  alias AND the live `IBackendCatalog`, so a generator can LOOK THINGS UP at bind time — which is what makes
  the flagship demo real: `cf_union_by_pattern` queries `sys.tables` on the catalog's own connection, then
  emits the UNION over quoted three-part names. The global form keeps the plain
  `GenerateSql(RecordBatch)` — connection-free is its defining property.

Findings:

- **The payoff is measured, not assumed.** `SELECT … FROM db.dbo.cf_union_by_pattern('sg_sales_%') WHERE
  yr = 2024` reaches SQL Server as **two** statements — `(@p0 int)SELECT [id], [nm], [yr] FROM
  [dbo].[sg_sales_2024] WHERE [yr] = @p0` and the same for `…_2023` — i.e. the predicate AND the projection
  pushed into every member's server-side query (pinned from `dm_exec_query_stats`). A marshaled table
  function would have streamed every row through C#. On the global side, `json_serialize_plan` of a
  `fabricator_sql_seq` query contains **no** fabricator function at all: the call is gone after bind.
- **`EXPLAIN` is not usable as a subquery** in DuckDB (parser error), so the "the call vanished" pin uses
  `json_serialize_plan(sql, optimize := true)` — which IS subquery-usable and strictly better (it inspects
  the optimized logical plan, not rendered text).
- **`pushdown_complex_filter` log lines are optimizer-round-dependent** (the documented re-invocation), so
  the Delta-side pushdown pin asserts `>= 2` (one per member scan), not an exact count.
- **`Scan()` must enumerate the new registry too** — omitting `sql_table_functions_` from
  `FabricatorSchemaEntry::Scan(TABLE_FUNCTION_ENTRY)` made the function resolvable BY NAME but invisible to
  `duckdb_functions()` / `SHOW FUNCTIONS` (caught by the test's discovery section).
- **T-SQL `LIKE` treats `[dbo]` as a character class**, so plan-cache assertions on emitted SQL must use
  `CHARINDEX` (the pattern `verify_table_functions.test` already established).
- Error surfaces are all clean and land at BIND: NULL for a non-nullable parameter (per-parameter message,
  better than `query_table()`'s blanket rejection), the generator's own validation verbatim, an unknown named
  parameter → DuckDB's candidate list, a non-constant argument → *"does not support lateral join column
  parameters"*, and a non-single-SELECT generation → our own message. `PREPARE`/`EXECUTE` twice and a `VIEW`
  over a call both work (the generator re-runs per bind — hence the determinism contract).

Open question resolutions: #3 → shipped `DuckSql.QuoteIdent`/`QuoteName`/`Literal`/`QuoteString` only (no
builder); #4 → LIST and BOOLEAN constants cross fine (`fabricator_delta_union(['a','b'], by_name := true)`),
no JSON fallback needed; #5 (overload sets) → still deferred, unneeded so far.

## 3. Phasing, tests, effort

Each phase independently shippable; suites green per phase (the standing convention).

- **Phase A — macros — DONE (2026-07-24, see §1.3).** No ABI bump; `verify_macros.test` 41 +
  `verify_plugin.test` 10.
- **Phase B — global `table_sql`** (**ABI v68**, lockstep): the entry + `Abi.cs`/`Bootstrap.cs`
  handler + `ISqlTableFunction` + `GlobalFunctions.SqlTableMap` + the registrar branch + the shared
  `FabricatorSqlGenBindReplace`. Demo `fabricator_sql_seq`. **`test/verify_sqlgen.test`**: result +
  schema-from-plan, arg-dependent schema (two calls, different column sets), named param, NULL-arg
  error, non-constant-arg error, single-SELECT violation error, `PREPARE`/`EXECUTE` twice, a VIEW
  over it, `EXPLAIN` (bind-only path runs the generator exactly once — pin via a C# call counter or
  `duckdb_logs`).
- **Phase C — catalog-bound `table_sql`** (no further ABI): discovery `kind='table_sql'` +
  `FabricatorSchemaEntry` branch + `ICatalogSqlTableFunction` + the catalog-alias plumbing. Demo
  `dbo.union_by_pattern` on the SQL provider. Test section: result parity vs a hand-written UNION,
  **pushdown proof** — a `WHERE` on the union prunes into each member scan (`dm_exec_query_stats`
  shows per-table parameterized WHERE, the `verify_table_functions` proof pattern), works inside a
  join, entry self-heals after `fabricator_invalidate_cache`.

Rough effort: A ≈ small (a day incl. tests); B ≈ small-medium (ABI bump discipline dominates);
C ≈ medium (discovery + entry wiring, mostly existing patterns).

## 4. Open questions (decide before Phase A/B)

1. **Load failure policy for bad macro DDL**: WARN + skip (recommended — matches the best-effort
   load-time registration; a broken provider macro must not brick the extension) vs fail-loud.
2. **Macro schema namespace**: force `main` (recommended) vs honor a provider-written
   qualification.
3. **`DuckSql` helper scope**: just `QuoteIdent`/`Literal`, or also a small `SelectBuilder`? Start
   minimal; the DAX/SQL providers' existing escaping stays where it is.
4. **LIST/STRUCT-typed constant args for `table_sql`** (the `by := [...]` example): the existing
   1-row Arrow marshaling should carry them — verify in Phase B and pin with a test; if a shape
   doesn't cross, the fallback is the JSON-string convention (`daxeval` precedent).
5. **Overload sets for `table_sql`** (same name, several signatures — `query_table` does this via
   `TableFunctionSet`): defer until a concrete need; the named-params + ANY-sentinel machinery
   covers most variance without overloads.
