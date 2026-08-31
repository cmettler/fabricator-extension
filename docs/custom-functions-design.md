# Custom Functions — Design (Phase 3+)

Status: **design / not yet implemented.** Locks the C++⇄C# contract and the C# authoring API for
custom **scalar**, **table**, and (later) **table-in-out** functions, across the one-binary /
multi-provider architecture (see `CLAUDE.md` → target architecture).

> ⚠ **A KIND THIS DOCUMENT DOES NOT COVER: `lateral`** (`ILateralFunction`, built 2026-08-22, ABI v79) —
> a ROW-MAPPED function called `FROM t, fn(t.a, t.b)`. It is deliberately not a variant of §11's table-in-out:
> an in-out declares its input as a `{TABLE}` parameter and so can only be called on a relation the caller can
> NAME, while a lateral function declares its positional parameters as real value types and lets DuckDB
> synthesise the input relation from the argument EXPRESSIONS. Batching it also needs a PROVENANCE channel
> (which output row came from which input row) that §11's contract has no reason to carry. Its own record:
> [lateral_unnest_analysis.md](lateral_unnest_analysis.md) §8.

## Goals

- Custom **scalar** + **table** functions now; **table-in-out** sketched (Phase 4).
- **Vectorized over Arrow** end to end — a function receives a `RecordBatch` of arguments and returns
  Arrow. No row-at-a-time callbacks (the thing that makes the DuckDB.NET C-API binding feel un-vectorized:
  it wraps a per-row `Func<>`; we hand whole batches instead).
- **Two sources, one contract:**
  1. **Authored** provider/library functions, declared in C# (load-time, global).
  2. **Discovered** SQL Server stored procs / table-valued + scalar UDFs, mapped at ATTACH (catalog-bound).
- A **nice C# authoring API** (lambda / attribute / derived-class — see §5), all lowering to one contract.

Non-goals (now): non-Arrow execution; the in/out pump protocol (Phase 4, sketched in §7); window
functions. **Aggregate functions are future (after table-in-out) and get a separate, stateful contract —
sketched in §9.**

## 1. Why not DuckDB's C API from C# (DuckDB.NET style)

DuckDB.NET registers functions through DuckDB's **C API** from C# and adapts execution to a per-row
`Func<>`. We deliberately do **not** do that: we are already a C++ extension with a C# bridge, so we
register functions in **C++** (DuckDB C++ API) and dispatch to C# over our existing **Arrow ABI**. The
C# function sees an Arrow `RecordBatch` and returns an Arrow array/stream — vectorized by construction,
reusing `arrow_produce` (args → Arrow) and `arrow_ingest` (result → DataChunk). One marshaling path, no
second registration mechanism.

## 2. Output-schema timing (the core constraint)

| Kind | When the output schema is known | How |
|------|----------------------------------|-----|
| **Scalar** | **Bind time**, from the argument types AND the **constant** argument values (never runtime values) | Declared return type, or `Bind(ScalarBindArgs) → binding` — BUILT at ABI v80, see the note below |
| **Table** | **Bind time**, from the **constant arguments** (TVF/proc args are literals at bind) | `bind(constArgs) → Schema` — for SQL Server this calls `sp_describe_first_result_set` (§6) |
| **Table-in-out** | Bind time (from the input-table schema + args) | `bind(inputSchema, args) → Schema` (Phase 4) |

So a scalar's output is essentially static (matches the user's intuition); a table function's output is
**late-bound** and can depend on its parameters — which is exactly what makes mapping a stored proc work.

> **⚠ UPDATED AT ABI v80 (2026-08-23) — the scalar row above was written before the bind session existed and
> understated the ceiling in one way while being right about the floor.** A scalar's result type IS resolved at
> BIND, per CALL SITE, and it may depend on a **folded constant VALUE**, not merely on the argument types —
> `strptime` picking `TIMESTAMP` vs `TIMESTAMP_TZ` from its format string is upstream's own example, and
> `fabricator_parse(text, type_name)` is ours. "Never runtime values" stays exactly right (a plan-time type
> cannot depend on row data, and the bind REFUSES a non-constant slot rather than guessing) — a folded literal
> is simply a third category the original table did not name.
>
> Two consequences the table above cannot express, both of which cost something to learn — full record in
> [abi-history.md](abi-history.md) §v80:
> - **The declared return type stayed, and is not merely a convenience.** `IScalarFunction.Result` is
>   `Field?`: declared ⇒ registered on the catalog entry; absent ⇒ the bind must supply one. A binding may
>   answer "the declared type stands", which is what stops a discovered SQL Server UDF from paying an
>   `INFORMATION_SCHEMA` round trip at every call site to re-learn a type the host already holds.
> - **A scalar bind must NOT establish the host ambients**, unlike every other bind in the tree — a scalar
>   binds wherever it is CALLED, including inside a nested host query an outer operation is running while IT
>   holds the ambient. Doing what the other binds do SIGSEGVs.



## 3. The model: one declaration + bind + execute

Every custom function — authored or discovered — reduces to:

```csharp
// Fabricator.Bridge (provider-agnostic)
public enum FunctionKind { Scalar, Table, TableInOut }

// Mirror DuckDB's enums (duckdb/function/function.hpp) 1:1 — C++ sets ScalarFunction.stability /
// .null_handling straight from these.
public enum FunctionStability    { Consistent, Volatile, ConsistentWithinQuery }   // = CONSISTENT / VOLATILE / CONSISTENT_WITHIN_QUERY
public enum FunctionNullHandling { Default, Special }                              // = DEFAULT_NULL_HANDLING / SPECIAL_HANDLING

public sealed record FunctionDeclaration(
    string  DeclId,            // stable id the ABI uses to invoke (e.g. "sqlserver:dbo.usp_orders")
    string  Name,              // bare function name (no schema prefix)
    string? TargetSchema,      // DuckDB schema to place it in (see §3.1); null => main (global) | SQL schema (discovered)
    FunctionKind Kind,
    Schema  ParamSchema,       // ordered params — EMPTY schema (0 fields) = no args, never null; field
                               // name = param name, field metadata = named/optional (§3.4)
    Schema? OutputSchema,      // scalar: 1-field schema; table: fixed schema, or null = late-bound (call Bind)
    bool    LateBound,         // table: resolve OutputSchema via Bind(args) at DuckDB bind time
    bool    SupportsProjection,// table only (see §3.3): advertise projection pushdown — TVFs yes, EXEC procs no
    bool    SupportsFilter,    // table only (see §3.3): accept best-effort pushed filters (never-erase)
    FunctionStability    Stability,     // scalar only (see §3.2); table fns are opaque/volatile
    FunctionNullHandling NullHandling,  // scalar only (see §3.2)
    string? Metadata);         // opaque provider JSON (e.g. how to invoke: EXEC vs SELECT, proc id)

public interface IArrowFunction {
    FunctionDeclaration Declaration { get; }

    // Args are always a RecordBatch — never marshalled to object[] (keeps it all-Arrow, typed, named).
    //   • Table (constant args): a 0-or-1-row batch — one column per SUPPLIED parameter, field NAME = param
    //     name, field METADATA = named/optional (§3.4). 1 row carries the constant values; 0 rows conveys
    //     just the schema/types (for binds that only need types). Named vs positional is read off the schema.
    //   • Scalar: an N-row batch (the input vectors), columns = ParamSchema order.

    // Table (late-bound) only: compute the output schema from the constant-args batch. Scalars/fixed tables
    // return Declaration.OutputSchema. May run a provider round trip (e.g. sp_describe_first_result_set).
    Schema Bind(RecordBatch args);

    // Scalar: N-row args batch -> one IArrowArray (N rows).
    IArrowArray  ExecuteScalar(RecordBatch args);

    // Table: the 0-or-1-row constant-args batch -> a stream of result batches (matching the bound schema).
    IArrowArrayStream ExecuteTable(RecordBatch args);
}
```

**Args are a `RecordBatch`, and named args ride the schema** (your question). The args batch's **field
names = parameter names** and **field metadata = the named/optional attributes** (§3.4), so the function
reads which column is which param off `args.Schema` rather than relying on position — and only *supplied*
params are present (omitted optionals are simply absent → the proc's `DEFAULT` applies). C++ builds this
batch from `input.inputs` (positional) + `input.named_parameters` (named), naming each column after its
parameter. (A small typed accessor — `args.GetString("region")`, `args.GetInt32(0)` — can wrap the batch
for authoring ergonomics, but the contract is the `RecordBatch`.) This is the same Arrow object the ABI
already carries as the `args` stream, so there's no `object[]` conversion layer on either side.

Discovered SQL Server functions implement this **data-drivenly** (the provider synthesizes the
declaration from `sys.*` and implements `Bind`/`Execute` via `sp_describe_first_result_set` + `EXEC` /
`SELECT`). Authored functions get the sugar in §5, which compiles to the same interface.

### 3.1 Placement: catalog & schema

The **catalog is implicit by registration phase — never a declaration field, and never auto-created**
(a DuckDB catalog is a database, not a function namespace). Only the **schema** (`TargetSchema`) is
explicit:

- **Catalog-bound (attach-time, the main path):** the catalog *is* the attached database (implicit from
  the handle `list_catalog_functions` was called on). `TargetSchema` = the **SQL Server schema** the
  proc/UDF lives in (`dbo`, `sales`), so `dbo.usp_orders` → `mssql.dbo.usp_orders`. C++ adds the function
  to the matching `FabricatorSchemaEntry`. No auto-create — schema discovery already produced those entries
  (ensure discovery includes schemas that contain *only* procs/functions).
- **Global (load-time):** the catalog is the **system catalog** (implicit; the only sensible target — a
  function in an attached catalog would disappear on `DETACH`). `TargetSchema` defaults to `main` →
  callable unqualified (`fabricator_query()`). A non-default schema in the system catalog *may* be
  auto-created (a schema is a cheap namespace, unlike a catalog), but for **collision-avoidance across
  providers prefer prefixing the name** (`mssql_query`, `dax_query`) over schema namespacing — a system
  schema named `mssql` collides conceptually with an attached catalog named `mssql`. Generic functions
  stay `fabricator_*`.

### 3.2 Scalar semantics: stability & null handling

C++ sets `ScalarFunction.stability` / `.null_handling` directly from the declaration. Both are
**scalar-only** (table functions ignore them; a proc-as-TVF is opaque/volatile by nature).

- **`Stability`** — `Consistent` (default): pure/deterministic, so DuckDB may constant-fold, cache, and
  reorder it. `Volatile`: side effects or non-deterministic → always re-evaluated, never folded/reordered
  (use for anything that writes, or reads changing state). `ConsistentWithinQuery`: stable for one query.
  *Discovered SQL Server functions:* read `OBJECTPROPERTY(object_id,'IsDeterministic')` → deterministic
  scalar UDF ⇒ `Consistent`, else `Volatile`. Anything proc-shaped / side-effecting ⇒ `Volatile`. *Authored:*
  the author declares it (default `Consistent`).
- **`NullHandling`** — `Default`: if **any** argument is NULL, DuckDB yields NULL **without invoking** the
  function for that row (efficient; correct only for NULL-propagating functions). `Special`: the function
  is invoked with NULL args and decides (needed for `coalesce`/`isnull`-style logic).
  *Discovered SQL Server scalar UDFs:* SQL Server **does** call a UDF with NULL inputs and it may return
  non-NULL, so to mirror semantics faithfully default discovered UDFs to **`Special`**; pick `Default`
  only when the UDF is known NULL-propagating (it then skips NULL-arg rows *and* the per-batch round trip).
  Since our scalar execution is **batched** (a `RecordBatch` of inputs → one `execute_scalar` → one column
  back), `Special` just keeps NULL rows in the batch — no per-row cost.

### 3.3 Table function pushdown (projection + optional filter)

A custom table function is just a scan whose FROM source is a function invocation, so it **reuses the
catalog-scan pushdown machinery** (`ScanSpec` / `FilterWhereBuilder` / projection-by-name in
`arrow_ingest`) — no new pushdown code.

- **DuckDB side:** the registered `TableFunction` sets `projection_pushdown = true`, and — when the decl
  advertises `SupportsFilter` — `pushdown_complex_filter` using the **same never-erase, best-effort** model
  as catalog scans (DuckDB always re-applies every predicate, so pushing a superset is always correct).
  The output schema is still the **full** result set at bind (§6); projection trims columns and filters
  trim rows at execute.
- **Execution:** `execute_table` carries the same `spec_json` (`{columns, filter, top}`) + `filter_values`
  as `scan_table`. C# applies them by **wrapping the invocation**:
  `SELECT <cols> FROM schema.tvf(@args) WHERE <filter>` — SQL Server does the projection + filtering
  (inline TVFs get inlined/optimized, so it's real pushdown). The same best-effort guard applies: any
  filter node C# can't render is omitted and DuckDB re-applies it.
- **TVF vs stored proc — the key distinction:** a **table-valued function** is wrappable in
  `SELECT … FROM tvf(@args) WHERE …`, so it advertises `SupportsProjection = SupportsFilter = true` and
  gets full pushdown. A **stored procedure** (`EXEC`) is *not* wrappable inline (nowhere to add a column
  list / WHERE), so a proc-backed function sets both `false` and DuckDB applies projection + filters
  locally. (`INSERT … EXEC #tmp` then filtering `#tmp` materializes server-side first — no pushdown win;
  skip unless justified.)
- `LateBound` composes: `bind_function` reports the full output schema; pushdown only changes which
  rows/columns cross the wire at `execute_table`.
- **Scope: scan-shaped table functions only** (scalar-arg TVFs/procs that plan as a `LogicalGet`).
  **Table-in-out** functions are *operators*, not scans — they get **no** filter/projection pushdown; a
  `WHERE`/projection on their output is applied by DuckDB above the operator (see §11).

### 3.4 Parameters: positional, named, optional (stored procs)

DuckDB distinguishes **positional arguments** (`fn(a, b)` — all required, matched by type) from **named
parameters** (`fn(x := 1)` — supplied by name, optional by nature). Stored procs need the latter: they're
called `EXEC proc @p = val`, their parameters are frequently **optional (have defaults)**, and DuckDB has
no "optional positional" — so any optional parameter *must* be a named parameter.

**Per-parameter attributes ride `ParamSchema` as Arrow field metadata** (the Airport convention — no extra
ABI surface):
- `fabricator:named` = `1` → register as a DuckDB **named parameter** (else a positional argument).
- `fabricator:optional` = `1` → has a SQL Server default; not required (implies named).

**Mapping by source:**
- **Scalar UDF / table-valued function** → **positional** (`SELECT dbo.f(x, y)`,
  `SELECT * FROM dbo.tvf(1, 'x')` — SQL Server TVFs are called positionally in a FROM clause; their args
  can't be named there).
- **Stored procedure** → **named parameters** (mirrors `EXEC @name = val` and handles optionals: the caller
  supplies a subset; omitted optional params fall back to the proc's own `DEFAULT`). Discovered from
  `sys.parameters` (name, type, `has_default_value`, `is_output`).

**Flow:** C++ reads the field metadata → builds the `TableFunction`'s positional `arguments` vector and/or
`named_parameters` map. At bind it gathers supplied values — positional from `input.inputs`, named from
`input.named_parameters` — into the 1-row args batch whose **field names = the parameter names** (only
supplied params present). C# reads those names → builds the parameterized call: `EXEC dbo.usp_orders
@region=@p0, @top=@p1` (proc, only supplied params) or `SELECT … FROM dbo.tvf(@p0, @p1)` (TVF, positional).
`bind_function` receives the same args batch, so `sp_describe_first_result_set` runs with the right
`@params` declaration + values.

**`OUTPUT` parameters** are surfaced as an extra **`_OUTPUT_` STRUCT column** appended to the result
schema — one struct field per OUTPUT param (`sys.parameters.is_output = 1`; field name = param name, type
from the param). Its value is the proc's post-execution output, identical for every row (broadcast). This
is known at **bind** (from `sys.parameters`), so it composes with §6: bound schema = the `sp_describe`
result columns **+** `_OUTPUT_`.

> **Timing caveat:** SqlClient populates OUTPUT `SqlParameter` values only **after the reader is fully
> consumed/closed** — so a proc *with* OUTPUT params can't stream while broadcasting `_OUTPUT_`. It must
> **buffer its result set**, read the outputs, then emit rows with `_OUTPUT_` filled. Procs *without*
> OUTPUT params still stream. (Acceptable: OUTPUT-param procs are typically small compute/status results,
> not large scans.)

Notes: the proc's integer **RETURN value** can ride the same struct as a conventional field (e.g.
`_return`); an **INPUT/OUTPUT** param appears both in the args and in `_OUTPUT_`; a result column literally
named `_OUTPUT_` would collide → detect and suffix. Variadic procs are out of scope.

## 4. The ABI (additions to `abi.h`)

Declarations are listed; schemas and execution go through the existing zero-row-stream / Arrow-stream
mechanisms so **no new Arrow-IPC parsing is needed in C++** (it reuses `PopulateReturnSchema`).

```c
// List a provider's authored (global) functions, or an attached catalog's discovered functions.
// Returns rows: decl_id (utf8), name (utf8), kind (int), late_bound (int), metadata (utf8).
// (Param/output schemas are fetched per-decl below — keeps each row flat/string-readable.)
int32_t (*list_global_functions)(const char *provider, ArrowArrayStream *out, char **err);
int32_t (*list_catalog_functions)(FabricatorHandle handle, ArrowArrayStream *out, char **err);

// Per-decl Arrow schemas as ZERO-ROW streams (C++ reads them like COLUMNS metadata → LogicalTypes).
int32_t (*get_function_param_schema)(FabricatorHandle handle, const char *decl_id,
                                     ArrowArrayStream *out /*zero-row*/, char **err);
// Scalar / fixed table: the output schema. (Late-bound tables: use bind_function instead.)
int32_t (*get_function_output_schema)(FabricatorHandle handle, const char *decl_id,
                                      ArrowArrayStream *out /*zero-row*/, char **err);

// Table late-binding: given the constant args (1-row Arrow batch, like filter_values), return the
// output schema (zero-row stream). Called from the DuckDB TableFunction bind.
int32_t (*bind_function)(FabricatorHandle handle, const char *decl_id,
                         ArrowArrayStream *args /*1-row, nullable*/, ArrowArrayStream *out, char **err);

// Execute. Scalar: args = one batch (N rows) -> out = one batch (N rows, the single output column).
//          Table:  args = 1-row batch of the constants    -> out = stream of result batches.
int32_t (*execute_scalar)(FabricatorHandle handle, const char *decl_id,
                          ArrowArrayStream *args, ArrowArrayStream *out, char **err);
// Table: spec_json {columns, filter, top} + filter_values mirror scan_table (§3.3) — projection +
// best-effort filter pushdown into the TVF. Both nullable => no pushdown (full result).
int32_t (*execute_table)(FabricatorHandle handle, const char *decl_id, ArrowArrayStream *args,
                         const char *spec_json, ArrowArrayStream *filter_values,
                         ArrowArrayStream *out, char **err);
// execute_inout: Phase 4 (see §7).
```

`decl_id` namespaces by provider/object (e.g. `"sqlserver:dbo.usp_orders"`), so one handle can host many.
Bump the ABI version once when these land (per the §ABI rule in `CLAUDE.md`).

> **Decl-encoding note.** Listing decls as flat string/int rows (read via the existing `ReadStringTable`)
> + per-decl schema streams avoids needing full `nanoarrow`/`ArrowSerializer` in C++ now. When
> `ArrowSerializer` is adopted (it's staged for Phase 3 anyway), `list_*_functions` can return richer
> nested decl rows in one shot and drop the per-decl schema calls. Either way the C# contract (§3) is
> unchanged.

## 5. C# authoring API — recommendation

Support **three styles, layered**, all producing an `IArrowFunction` (so the runtime, the discovered
functions, and authored functions share one path). Lead with vectorized Arrow signatures; offer a
row-shaped convenience only as explicit opt-in.

**(a) Lambda / fluent — quick & inline**
```csharp
provider.Functions.AddScalar(
    name: "levenshtein",
    parms: ("a", StringType.Default), ("b", StringType.Default),
    returns: Int32Type.Default,
    exec: batch => /* vectorized: build an Int32Array from batch.Column(0)/(1) */ );

provider.Functions.AddTable(
    name: "top_orders",
    parms: ("region", StringType.Default), ("n", Int32Type.Default),
    bind:  args => DescribeSchema(...),                 // late-bound output schema
    exec:  args => /* IArrowArrayStream of result batches */ );
```

**(b) Attribute + reflection — the "nice", SQLCLR-style authoring path** (recommended default for
libraries of provider built-ins). Vectorized: parameters and return are **Arrow arrays** (whole columns),
not scalars — so it stays columnar.
```csharp
public static class StringFns {
    [ArrowScalar("levenshtein")]
    public static Int32Array Levenshtein(StringArray a, StringArray b) {
        var b32 = new Int32Array.Builder();
        for (int i = 0; i < a.Length; i++) b32.Append(Lev(a.GetString(i), b.GetString(i)));
        return b32.Build();
    }
}
// Provider assembly is scanned for [ArrowScalar]/[ArrowTable]; reflection infers ParamSchema from the
// Arrow-array parameter types and OutputSchema from the return type, and wraps the method as Execute.
```
This is the SQLCLR analogy you asked about (`[SqlFunction]`) but **columnar** — the win over SQLCLR's
row model. *Optional convenience:* an attribute variant with scalar parameters
(`[ArrowScalar] static int Lev(string a, string b)`) that the framework auto-vectorizes by looping;
clearly documented as the non-vectorized path for ergonomics, not the default.

**(c) Derived class — full control + the only practical option for table / table-in-out**
```csharp
public sealed class TopOrders : ArrowTableFunction {
    public override Schema Bind(BindContext ctx) =>
        ctx.DescribeFirstResultSet($"EXEC dbo.usp_top_orders @region=@p0, @n=@p1", ctx.Args);
    public override IAsyncEnumerable<RecordBatch> Execute(ExecContext ctx) =>
        ctx.StreamQuery($"EXEC dbo.usp_top_orders @region=@p0, @n=@p1", ctx.Args);
}
```

**Discovered functions don't use (a)–(c)** — the SqlServer backend generates `FunctionDeclaration`s from
`sys.procedures` / `sys.objects` (+ parameter metadata) and implements `Bind`/`Execute` directly via §6.

## 6. SQL Server late-binding via `sp_describe_first_result_set`

For a table-valued proc/function, the output schema is resolved at DuckDB **bind** time, without
executing:

```sql
EXEC sys.sp_describe_first_result_set
  @tsql   = N'EXEC dbo.usp_top_orders @region=@p0, @n=@p1',
  @params = N'@p0 nvarchar(50), @p1 int',
  @browse_information_mode = 0;   -- returns name / system_type_name / is_nullable / ... per column
```

The provider maps the returned column metadata → Arrow schema → DuckDB `return_types`/`names` (reusing
the existing SqlClient→Arrow type mapping). **Scalar** UDFs need no describe — their return type is in
`sys` (static). **Fallbacks** when describe can't determine a shape (dynamic SQL, temp tables,
multiple/conditional result sets): (i) a declared/static schema carried in the decl metadata; (ii) a
clear "result schema not describable; declare it explicitly" error; (iii — future) execute-once-and-infer
with a cached schema. (This mirrors what the Airport/Flight extension already does — proven.)

For a future DAX/ADOMD provider, `Bind` uses the analogous describe (or executes a 0-row `EVALUATE
TOPN(0, …)` to infer columns).

## 7. C++ side: registration + dispatch

- **Load-time / global** (authored provider built-ins): in `Extension::Load()`, boot the bridge, call
  `list_global_functions(provider)` for each registered provider, fetch param/output schemas, and
  `loader.RegisterFunction` a `ScalarFunction`/`TableFunction` whose `function_info` carries `(provider,
  decl_id)`. **Consequence: the bridge must initialize at extension load**, not lazily — DuckDB only
  permits global registration during `Load()`. *Decision needed:* accept CLR-at-load, or make authored
  functions attach-bound too (then we keep lazy loading).
- **Attach-time / catalog-bound** (discovered procs/UDFs): on `LoadCatalog`/`RefreshCache`, call
  `list_catalog_functions(handle)` → add `ScalarFunctionCatalogEntry` / `TableFunctionCatalogEntry` to
  the `FabricatorSchemaEntry`, resolved as `db.schema.fn(args)`. Rides the existing cache invalidation
  (`fabricator_refresh_cache`). This is the Airport pattern and the bulk of real usage.
- **Bind callback** (table): extract the constant args → 1-row Arrow batch (via `arrow_produce`) →
  `bind_function(decl_id, args)` → zero-row stream → `PopulateReturnSchema` → `return_types`/`names`.
- **Execute callback**: marshal the arg chunk → Arrow (`arrow_produce`) → `execute_scalar`/`execute_table`
  → ingest the result (`arrow_ingest`; scalar = one column into the result vector, table = the scan loop).
  Table functions also build the projection/filter `spec_json` + `filter_values` from `column_ids` + the
  pushed filters and pass them to `execute_table` — the same code path as catalog scans (§3.3).
- New core files `src/fabricator/arrow_functions.{hpp,cpp}` (or `src/include/fabricator/`) hold this — generic,
  reused by every provider.

**Table-in-out (Phase 4):** DuckDB's `in_out_function` + `OperatorResultType` (`NEED_INPUT` /
`HAVE_OUTPUT` / `FINISHED`). The ABI gets an `execute_inout` with an explicit framed protocol (push an
input chunk as Arrow, pull output batches, status enum) — replacing Airport's fragile magic-string
buffer signals. The hard part is **reliably detecting end-of-input** for cleanup/commit — see §11.

## 8. Open decisions

1. **CLR-at-load vs everything-attach-bound** (§7) — the one lifecycle question. Recommendation: make
   discovered + provider functions **all catalog-bound** initially (keeps lazy load, simplest, covers the
   stored-proc use case); add load-time global functions only if a provider needs functions without an
   ATTACH.
2. **Function naming / namespacing** — global authored functions: `fabricator_*` or provider-prefixed
   (`mssql_*`)? Catalog functions are naturally `db.schema.fn`. 
3. **Attribute API surface** — vectorized-only, or also the row-convenience overload (§5b)?
4. **Decl encoding** — flat rows + per-decl schema streams now, vs `ArrowSerializer` nested rows once
   adopted (§4 note).
5. **Param passing** — named vs positional; how DuckDB named params (`fn(region := 'US')`) map to the
   proc's `@params`.

## 9. Aggregate functions (4h — IMPLEMENTED)

> **STATUS: built (4h).** Option **(b)** below — the DuckDB-side custom C# aggregate (SQLCLR-style, handle-based
> state) — is implemented and verified (`test/verify_custom_aggregates.test`, 35 assertions). The as-built
> design + the deviations from this original sketch are in **§9.1**. Option **(a)** aggregate *pushdown* (rewrite
> `GROUP BY` to server-side SQL) remains a future optimizer feature, unbuilt.

Aggregates are the heaviest function type and get a **separate, stateful contract** (not `IArrowFunction`).
Two distinct features get conflated here — decide which is actually needed:

**(a) Aggregate pushdown — recommended first; low plumbing, high value for SQL-resident data.** For
`SELECT g, agg(x) FROM mssql.dbo.t GROUP BY g`, rewrite to server-side `SELECT g, agg(x) FROM t GROUP BY g`
and stream the grouped result. **No state crosses the ABI** — SQL Server's engine does the work, including
built-in and SQL Server CLR aggregates that already exist server-side. This is an optimizer / scan-rewrite
feature (sibling to filter/TopN pushdown), not function registration, and it's the right path whenever the
data lives in SQL Server.

**(b) DuckDB-side custom aggregate — SQLCLR-style; heaviest.** Runs in DuckDB's engine over *any* data
(local + remote mixed), backed by C#. DuckDB's lifecycle (`aggregate_function.hpp`) maps 1:1 onto SQLCLR's
user-defined aggregate, so a **derived class is the right fit**:

| DuckDB callback | SQLCLR | C# `ArrowAggregateFunction` |
|---|---|---|
| `initialize` / `size` | `Init` | `CreateState()` |
| `update` (grouped, vectorized) | `Accumulate` | `Update(state, RecordBatch)` |
| `combine` | `Merge` | `Combine(target, source)` |
| `finalize` | `Terminate` | `Finalize(states) -> IArrowArray` |
| `serialize` / `deserialize` | `Write` / `Read` | `Serialize` / `Deserialize(state)` (spill / distributed) |

The hard parts (why it's last):
- **State ownership across the ABI.** DuckDB allocates a raw state blob per group in its hash table; a C#
  state is a managed object. So the blob holds only a **handle** (`aggregate_size` = 8 bytes) and C# owns a
  state table keyed by it. Every `update`/`combine`/`finalize` crosses the boundary.
- **Grouped `update` scatters vectorization.** DuckDB hands N input rows + N state pointers, and rows for
  one group are *not* contiguous. ABI shape: `agg_update(ctx, input_batch, state_handles[])`; C# routes
  rows to states — vectorized *across* the batch but scattered *within* a group.
- **Spill/serialize.** Without `serialize`/`deserialize`, a large GROUP BY that spills to disk fails —
  implement them (C# state → bytes) for robustness, or accept in-memory-only for v1. Window support
  (`aggregate_window_t`) left null initially.

**Recommendation:** do **(a) pushdown first** — most aggregation over SQL Server data wants server-side
GROUP BY anyway, and it sidesteps the entire state-plumbing problem. Tackle **(b)** only when a genuine
client-side / cross-source aggregate need appears; it's a separate `ArrowAggregateFunction` base + the
state-handle ABI above, sequenced after table-in-out. (`FunctionKind` gains an `Aggregate` value, but
aggregates do not use the `IArrowFunction` scalar/table contract.)

### 9.1 As built (4h) — the DuckDB-side custom C# aggregate

Implemented option (b). The sketch above was largely right (handle-in-blob, scattered grouped `update`); the
concrete build settled these points:

- **C# authoring** = `IAggregateFunction` (`SchemaName`/`Name`/`Parameters`/`Result`/`CreateState()`) +
  `IAggregateState` (`Update(RecordBatch)` / `Combine(IAggregateState source)` /
  **`object? Finalize()`** — a boxed scalar, null = SQL NULL; the session builds the typed result column). Demos:
  `dbo.cf_product`, `dbo.cf_bit_or`. Always provider-authored (no SQL Server aggregate to discover); surfaced via
  the custom-function metadata `UNION ALL` as `kind='aggregate'` → C++ `AddAggregateFunction` → an
  `AggregateFunctionCatalogEntry`. Catalog-bound + attach-time (`db.dbo.cf_agg(x)`), like 4e/4f/4g.

- **State = id in the blob, accumulator in C#.** `state_size=8`; the blob holds an `int64` id assigned in
  `initialize` from a `std::atomic<int64_t>` on `function_info` (reachable from `initialize`, which has no
  bind_data). **Monotonic ids never collide** → correctness needs no destructor, even across prepared-statement
  re-executions (which share bind_data). The C# session is a `ConcurrentDictionary<id, accumulator>` opened in
  the aggregate `bind` (stored on `FabricatorAggregateBindData`; the holder destructor calls `agg_close`).

- **ABI v25, six entries** (not the single `agg_update(ctx, batch, handles[])` of the sketch): `agg_open` /
  `agg_update` (`[int64 id ++ params]`) / `agg_combine` (`[target_id, source_id]`) / `agg_finalize`
  (`[id]` → one result column in id order) / `agg_destroy` (`[id]`) / `agg_close`. Arg/return schemas reuse
  `get_function_param_schema`/`get_function_return_schema`.

- **Two corrections found against the DuckDB source** (both verified): (1) read state pointers via
  `UnifiedVectorFormat`, **never** `FlatVector::GetData<data_ptr_t>` — the ungrouped path passes a **CONSTANT**
  state vector to `finalize`/`simple_update`; (2) implement **both** `update` (grouped) and `simple_update`
  (ungrouped) — DuckDB calls different ones. Threading: each id is touched by one thread at a time (per-thread
  local hash tables; partition-disjoint combine) → `ConcurrentDictionary`, no per-accumulator lock. C# grouping
  in `update`: a fast path when the chunk is one group (always for `simple_update`), else group row-indices by
  id and gather per-group sub-batches (Apache.Arrow C# has no `take`).

- **Window (OVER): no custom `window` callback.** With `window==nullptr`, DuckDB drives windowing through our
  `update`/`combine`/`finalize` via `WindowSegmentTree` — batched updates + O(log n) combines per output row. A
  custom per-output-row `window` callback would be **one C++↔C# crossing per output row**, strictly worse for a
  marshaled bridge, so it's deliberately omitted. Because the window paths churn many transient states, the
  **destructor IS wired** (`agg_destroy`) to bound the C# map (the GROUP-BY-only design could have skipped it).

- **Disk-spill is opt-in per aggregate** (`IAggregateFunction.SupportsSpill`, ABI v26). The default
  (fast) mode keeps the live accumulator in C# behind an id — bounded by managed memory, no spill. Setting
  `SupportsSpill=true` (+ `IAggregateState.Serialize()`/`Load()`) switches to **bytes-in-blob mode**: the
  per-group state is serialized into DuckDB's fixed, pointer-free state blob
  (`[uint32 len][byte data[FABRICATOR_AGG_SPILL_CAP = 1 KB]]`), so DuckDB's external GROUP BY spills it to disk
  under memory pressure. The cost is (de)serialization on every update/combine/finalize, and a 1 KB cap on the
  serialized state — so it suits fixed/small state (sum/product/bitwise/avg/moments), not unbounded state
  (string concat). Surfaced as `kind='aggregate_spill'`; the C++ callbacks branch on the `spillable` flag and
  marshal state as Arrow BLOB columns (the C# side is stateless per call). **Build note:** the spill update +
  combine assign *dense group/target slots* so interleaved-group updates and the window segment-tree's
  merge-many-nodes-into-one-frame-state combine accumulate correctly (a naive read-once/write-once per row
  loses merges — caught by the windowed-spill test). `serialize`/`deserialize` for *variable/unbounded* state
  and for distributed-plan serialization remain deferred.

- **Resolution detail**: DuckDB stores scalar/aggregate/macro in one `functions` namespace and the binder looks
  up `SCALAR_FUNCTION_ENTRY` then dispatches on the returned entry's *actual* type — so
  `FabricatorSchemaEntry::LookupEntry(SCALAR_FUNCTION_ENTRY)` **falls back to the aggregate** (plus an explicit
  `AGGREGATE_FUNCTION_ENTRY` branch).

## 10. Build-on points (already in the repo)

`arrow_produce` (DataChunk→Arrow, for args), `arrow_ingest` / `PopulateReturnSchema` (Arrow→DuckDB, for
results + schema-from-zero-row-stream), `FabricatorSchemaEntry` (hang catalog function entries here),
`ArrowDataReader` / `DbDataReaderArrowStream` (C# query→Arrow streaming for `Execute`), the
SqlClient→Arrow type mapping (reused by `Bind`/describe). The handle/`BackendRegistry` dispatch (once
multi-provider) routes each `execute_*` to the right backend automatically.

## 11. Table-in-out functions (Phase 4) — reliable end-of-input

(Reasoned from DuckDB internals + the described problem in `duckdb/duckdb#18222` — the issue wasn't
fetchable from this build env.) Table-in-out streams a TABLE in and a TABLE out (e.g. push rows to a
proc/TVP and read results back). Execution is the easy part; the hard part is reliably knowing **when the
input stream has ended**, for cleanup / commit on the C# side.

**What v1.5.4 exposes (verified):**
- `TableFunction.in_out_function` (per input chunk) + `in_out_function_final` (flush final output). The
  final callback is **not reliably invoked** on LIMIT short-circuit or error — the crux of #18222.
- `PhysicalOperator::OperatorFinalize(Pipeline&, Event&, …)` (gated by `RequiresOperatorFinalize()`) is the
  reliable pipeline-level finalize Mytherin points to — but it's a *physical-operator* hook;
  `PhysicalTableInOutFunction` doesn't surface it through the `TableFunction` API.
- `LogicalExtensionOperator::CreatePlan(ClientContext&, PhysicalPlanGenerator&)` lets an extension inject a
  custom physical operator.

**Two end signals — don't conflate:** **clean end** (input consumed, or finished early via a still-*successful*
LIMIT) → `finish` (flush + commit); **abort** (error / cancellation) → `abort` (release + roll back).

**Design (public hooks + RAII — no vgi code):**
1. **Inject a pass-through operator above the in-out via `LogicalExtensionOperator`** (the supported form of
   your idea B). An `OptimizerExtension` wraps the logical in-out node; the extension op's `CreatePlan`
   builds a thin `PhysicalOperator` that forwards `Execute` to the in-out's output and implements
   **`OperatorFinalize`** → C# `inout_finish`. Sitting above the in-out, by the time *its* finalize runs the
   in-out's input is exhausted → a reliable clean-end signal, without subclassing/replacing
   `PhysicalTableInOutFunction` (your idea A needs a physical-plan hook we don't have; this is the supported
   equivalent).
2. **Operator-state destructor = abort backstop (RAII).** The in-out's operator state is *ours*
   (`GetOperatorState` returns our `unique_ptr`), and its destructor runs on **every** teardown path —
   normal, LIMIT, error/cancel. On destruction, if `inout_finish` wasn't already signalled → C#
   `inout_abort`. This catches the error path even `OperatorFinalize` misses.
3. **Idempotent C# protocol** so correctness never hinges on a perfect event: `inout_finish` / `inout_abort`
   are idempotent; the exchange cleans up on either; an optional server-side timeout reclaims an abandoned
   exchange. (This robustness-by-design is the "creative" part — vgi solves it with bespoke operator
   machinery; we lean on the documented `LogicalExtensionOperator` + RAII + an idempotent protocol.)

**ABI sketch:** `inout_open(handle, decl_id, input_schema) -> session`; `inout_push(session, in_chunk, out)
-> ResultType` (NEED_INPUT / HAVE_OUTPUT); `inout_finish(session, out)` (flush + commit, idempotent);
`inout_abort(session)` (idempotent). C++ wiring: `in_out_function` → `inout_push`; the injected operator's
`OperatorFinalize` → `inout_finish`; the operator-state destructor → `inout_abort` if not finished.

**Parallel input — the UNION-ALL trap (the query that breaks Airport).** A table-in-out's table argument
can be parallelizable, e.g.:

```sql
SELECT * FROM test1.utils.test_table_in_out(
    'Sloane',
    (SELECT txt:'hello', num:12 UNION ALL SELECT txt:'world', num:15));
```

DuckDB runs the two `UNION ALL` branches as **separate input pipelines, possibly on different threads**,
both feeding the one in-out operator → the function sees **multiple per-thread local states feeding one
logical call**. Airport already holds the exchange in a **global** state (`AirportExchangeGlobalState`,
confirmed in `storage/airport_exchange.hpp`) — so the failure is *not* "two exchanges." It's that **one**
exchange/writer is fed and closed by **parallel, uncoordinated** local states: concurrent writes to the
single Flight writer aren't thread-safe, and the writer tends to be finished/closed when the *first*
branch ends rather than after *all* do. In other words a global session is **necessary but not
sufficient** — you also need (1) **thread-safe / serialized feeding** into it and (2) a reliable
**"all local states exhausted"** finalize before closing.

**Streaming by default — the framework never buffers the input.** The parallel local states' input chunks
are fed into **one global, bounded multi-producer channel** (the same `Channel<RecordBatch>` + backpressure
pattern as the streaming bulk-write), which the single C# in-out session consumes as an
`IAsyncEnumerable<RecordBatch>` and yields output back the same way (the Flight-style contract you liked).
This **serializes the concurrent feed without buffering the whole input** — memory is bounded by the
channel + backpressure — and the channel is **completed at the single all-inputs-done finalize** (§11's
`OperatorFinalize` / global-state destructor, firing once after *every* branch/thread is exhausted). A
per-operator active-producer count over the parallel local states, plus that global finalize, decide when
the channel is complete. The `'Sloane' + UNION ALL` query then streams **both** branches' rows through the
one session (interleaved — order across `UNION ALL` is undefined anyway) and returns one coherent result.

**Internal buffering is the function's opt-in, never the framework's.** A particular C# in-out *may* choose
to drain its input enumerable fully before emitting output — e.g. to send the whole table as a
**Table-Valued Parameter** in one `EXEC`, or because its logic needs all rows first. It just
`await foreach`-es to the end; the framework neither knows nor cares. (A buffered-TVP in-out is then simply
one implementation; for a *bounded* input it can equivalently be written as a plain §3 table function with
a table-valued argument.)

The invariant: **one session in the global state, fed thread-safely through the bounded channel, closed by
a single all-inputs-done finalize.** Global state alone (what Airport has) isn't enough without that
coordination — the channel supplies the thread-safe serialized feed while staying fully streaming.

> *Future throughput option:* a stateless/partitionable in-out could instead get an **independent session
> per local state** (parallel branches processed concurrently, outputs concatenated) — more throughput, no
> coordination. Not needed for the proc/exchange case, which requires one coherent input stream; revisit if
> a partitionable workload appears.

**No pushdown into an in-out.** Unlike scan-shaped table functions (§3.3), a table-in-out is an *operator*,
not a `LogicalGet`, so it does **not** participate in filter/projection pushdown. A `WHERE` or column list
on its **output** is applied by DuckDB *above* the operator (a `LogicalFilter`/projection on the in-out's
result), never pushed into the function. (§3.3's `SELECT <cols> … WHERE <filter>` wrapping is only for
scalar-arg TVFs/procs that plan as a scan.) So `SupportsProjection`/`SupportsFilter` are scan-table-only;
they don't apply to in-out declarations.

**Test matrix — operators *around* the in-out (these have historically broken it; regression-test each):**
- **`UNION ALL` input** (parallel feed) — the query above; expect one coherent result with **both** rows.
- **`ORDER BY` on the output** — a sort above the in-out is a pipeline-breaker that changes the
  finalize / end-of-input timing; this **broke before**, so test it explicitly (e.g. add `ORDER BY num` to
  the `'Sloane' + UNION ALL` query).
- **`LIMIT` on the output** — short-circuit: `in_out_function_final` / `OperatorFinalize` may not fire, so
  the destructor abort backstop must still clean up exactly once.
- **`WHERE` on the output** — applied locally (no pushdown); verify correctness *and* that the in-out still
  finalizes.
- **aggregation / join above** the in-out.
- **error / cancellation mid-stream** — abort path fires exactly once; the session/connection is released.
- **empty input** and **large / unbounded input** (backpressure holds, memory stays bounded).

### 11.1 Concrete plan for the first build (4g) — parameter-table CROSS APPLY (decided)

(Refined with the user; this is what we build first. Supersedes any conflicting sketch above.)

**Feature.** A discovered SQL Server **TVF** gains a sibling **`<name>_each`** entry that takes an
**input parameter table** and applies the function **once per input row**, combined via SQL Server
**`CROSS APPLY`**. So `SELECT * FROM db.dbo.tf_nums_each( (SELECT n FROM params) )` runs `tf_nums(n)`
for each row of `params`. Per input chunk the C# session generates and runs, **on SQL Server**:
```sql
SELECT p.*, f.* FROM (VALUES (@r0c0,…),(@r1c0,…),…) AS p(col0,…) CROSS APPLY [dbo].[tf_nums](p.col0,…) AS f;
```
The CROSS APPLY is **T-SQL generated by C# and executed in SQL Server** — NOT a DuckDB LATERAL join.
Output schema (at bind) = the input parameter columns **+** the TVF's output columns. Default `CROSS APPLY`
(rows with no function output are dropped). **TVFs first** — a SQL Server TVF cannot modify data, so this is
**read-only** (no commit/rollback). Per-input-row **stored procs** are a later layer (a proc *can* modify →
needs rollback-on-failure; and a proc can't be inline-CROSS APPLY'd → per-row `EXEC`).

**We still need the §11 coordinated session + channel** (do NOT treat each chunk as an independent query):
parallel `UNION ALL` input arrives as multiple local states/threads that must feed **one** session over
**one** connection, thread-safely, with a single reliable finalize.

**The crux — verified empirically.** `in_out_function_final` is called **once per parallel input branch**
(observed: **twice** for a 2-way `UNION ALL`), so it is **NOT** a usable single "all input done" signal —
completing the channel / closing the session on the first one loses the second branch's rows (the real pain).

**RESOLUTION (built — supersedes both the "OperatorFinalize emits the tail" and the "last-branch counter"
sketches):** make the output **synchronous per input chunk** so there is **no tail** — then emitting rows
never depends on detecting which branch finishes last, and the problem dissolves.

Two facts forced this. (1) `PhysicalOperator::OperatorFinalize` is a **global single-shot** hook handed
**no `DataChunk`** (`OperatorFinalizeInput {GlobalOperatorState&, InterruptState&}`), running in the
`PipelineFinishEvent` *after* the pipeline stopped pulling rows — so it **cannot emit rows**. (2) The only
row-emitting post-input hook, `FinalExecute` (`in_out_function_final`), fires **per branch**, and an atomic
**last-branch counter is unsound**: `PhysicalUnion::BuildPipelines` adds dependencies so the UNION branch
pipelines may run **sequentially**, and a branch's operator state is created when its pipeline runs — so
branch 1 can finish (counter → 0, premature `inout_finish`) **before** branch 2 even starts, losing branch
2's rows. (The first build used the counter and passed tests only because that query let the branches
overlap — scheduling luck, not a contract. Caught in review.)

So:
- **`inout_push` is synchronous**: it runs THAT chunk's CROSS APPLY (or custom `Process`) to completion and
  returns the chunk's **full** output. `in_out_function` drains it across `HAVE_MORE_OUTPUT`, then
  `NEED_MORE_INPUT`. No tail → **no `in_out_function_final`, no counter**. Parallel branches push into the
  one session concurrently; the managed `Push` is lock-serialized. Correct under sequential *or* parallel
  union, by construction.
- **Session lifecycle = a refcounted `InOutSessionHolder` on the bind data.** `init_global` opens the
  session into it; `in_out_function` pushes via it; the holder's **RAII destructor calls `inout_abort`** on
  every teardown path (normal/`LIMIT`/error/cancel) — the reliable release/rollback backstop (frees the
  managed GCHandle too; `inout_finish` does not). The holder is `shared_ptr` so the **injected
  OperatorFinalize** (next bullet) can reach the same session.
- **`OperatorFinalize` is BUILT — as a reliable "in-out finished" signal for C# resource cleanup**, NOT for
  the proc commit. (Originally planned for the proc COMMIT; that turned out wrong — committing the in-out's
  own txn at operator-finish would commit before a user's explicit `ROLLBACK` could undo it, so the per-row
  proc instead runs on DuckDB's pinned transaction and the **transaction manager** drives commit/rollback.)
  It fires **once** even above a UNION (a UNION branch pipeline shares the base pipeline's single
  `PipelineFinishEvent` — `MetaPipeline::CreateUnionPipeline` doesn't `AddFinishEvent`, executor `else`
  branch — unlike per-branch `FinalExecute`; verified empirically). An `OptimizerExtension`
  (`RegisterFabricatorInOutFinalizer`) wraps the in-out's `LogicalGet` (identified by `function.in_out_function
  == FabricatorInOutFunction`, RTTI-free) in a pass-through `LogicalExtensionOperator` whose `PhysicalOperator`
  (`PhysicalOperatorType::EXTENSION`) forwards rows 1:1 and calls `holder->Finish()` → C# `inout_finish` in
  `OperatorFinalize`. For a read-only TVF that's the clean commit of its snapshot transaction; for procs/custom
  it's resource cleanup; the holder destructor's `inout_abort` stays the LIMIT/error backstop.

**APIs used in v1.5.4** (verified in the build): `in_out_function` +
`OperatorResultType {NEED_MORE_INPUT, HAVE_MORE_OUTPUT, FINISHED, BLOCKED}`; the
`{LogicalType::TABLE}`-arg registration pattern (ref: built-in `summary` =
`TableFunction("summary",{LogicalType::TABLE},nullptr,Bind)` + `in_out_function` + `init_global`/`init_local`).
`OperatorFinalize` (fires once, sink-level, even above a UNION) / `LogicalExtensionOperator` /
`OptimizerExtension` / `PhysicalOperatorType::EXTENSION` — used to inject the "in-out finished" cleanup
signal (see above).

**ABI (session-based, implemented at v23):** `inout_open(handle, schema, func, input_schema) → session` (the
input table's columns ARE the TVF's positional params — no separate scalar args); `inout_push(session,
in_chunk, out)` (out = output ready so far); `inout_finish(session, out)` (out = the entire remaining tail);
`inout_abort(session)` (frees the handle) — all idempotent.

**Registration (CORRECTED — shape-dispatch is infeasible).** The original plan was a single
`TableFunctionSet` with the scalar-arg form (4c, `tf_nums(5)`) **+** a `{LogicalType::TABLE}` overload, with
DuckDB picking by call shape. **DuckDB v1.5.4 forbids this:** `bind_table_function.cpp` throws *"Function …
has a TABLE parameter, and multiple function overloads — this is not supported"* (`functions.Size() != 1`).
So the in-out form is a **separate catalog entry under its own name** — the convention is the discovered TVF
name **+ `_each`** suffix (e.g. `tf_nums_each`), a `TableFunctionCatalogEntry` with a **single**
`{LogicalType::TABLE}` function; the scan form (4c) keeps the bare name `tf_nums`. The synthetic alias is
tracked in `FabricatorSchemaEntry::inout_functions_` (`<name>_each` → base TVF), registered for every
discovered `kind='table'` (procs excluded — per-row procs are a later layer), and listed by `Scan` so it's
discoverable. A *real* SQL Server function literally named `…_each` shadows the alias (the real name is
matched first). The in-out bind's `function_info.func` is the **base** TVF name (the CROSS APPLY target).

**Custom C#-authored table-in-out — DONE.** *(Reworked in Phase 6 → now `IInOutFunction`, or the
`StaticInOutFunction` convenience base, on the gate-based streaming exchange; the author writes `DoExchange`
(yielding a per-input sentinel), not `Process`/`Finish`, and there is one `IInOutFunction` registry. See
CLAUDE.md "Streaming table-in-out exchange (Phase 6)". The original 4g push design is recorded below.)*
Original (push) design (`IArrowTableInOutFunction`, the in-out analog of 4e
`ICatalogScalarFunction` / 4f `ITableFunction`): a pure-C# in-out (streaming transform / running aggregate
/ whole-table summary) authored in the provider, dispatched through the *same* session machinery as the TVF
CROSS APPLY. `Process(chunk)`/`Finish()` are invoked serially per session (no locking needed), and the
function declares its **full** output schema (no input echo, unlike `_each`). Surfaced as `kind='inout'`;
C# `CustomInOut` is a **factory** registry (fresh instance per session so state can't leak across queries);
C++ `AddInOutFunction` registers a bare-name `{TABLE}` entry reusing the 4g operator callbacks (no new ABI).
Demos `dbo.cf_tag` (per-row) + `dbo.cf_summarize` (stateful, emits at Finish); see
`test/verify_custom_functions.test`.

**Build order (all DONE — 4g complete):** session ABI + C# `InOutSession` → C++ `in_out_function` operator
(synchronous per-chunk output, no counter; RAII `InOutSessionHolder`) → §11 test matrix
(`test/verify_table_inout.test`, 63) → configurable isolation (ATTACH `isolation_level` + `SET
mssql_isolation_level`, one `SqlTransaction` per call, ABI v24; `test/verify_inout_isolation.test`, 17) →
custom C#-authored in-out (`test/verify_custom_functions.test`) → per-row stored procs
(`usp_x_each`, on DuckDB's pinned transaction; `test/verify_proc_inout.test`, 31) → the injected
`OperatorFinalize` "in-out finished" cleanup signal (`OptimizerExtension` + `LogicalExtensionOperator` +
pass-through `PhysicalOperator`, fires once even above a UNION). Possible future: OUTPUT-param-only proc
`_each`, multi-result-set procs, an `OperatorExtension` so in-out plans can be serialized/deserialized.

---

## 12. The UNIFIED PARAMETER PROTOCOL (2026-08-02) — as built

> **Moved verbatim out of `CLAUDE.md` (2026-08-23).** This is the record of the refactor that replaced
> the split `Parameters` + `NamedParameters` (+ a third `InputSchema` on the in-out/collector kinds) with
> ONE parameter schema whose every field carries its STYLE in Arrow field metadata. `CLAUDE.md` keeps a
> short pointer plus the rules that generalise; the reasoning, the traps and the two shipped defects it
> exposed are here.

- **THE UNIFIED PARAMETER PROTOCOL — DONE 2026-08-02 (behaviour-preserving; no ABI bump).** A function now
  declares **ONE parameter schema** whose every field carries its STYLE in Arrow field metadata
  (`fabricator.param_style` = `named` | `table`; ABSENT ⇒ positional). This replaced a split
  `Parameters` + `NamedParameters` pair plus a third `InputSchema` on the in-out/collector kinds.
  `dotnet/Fabricator.Abstractions/ParamStyle.cs` (`ParamStyle` / `Params`) is the whole protocol; C++ reads it
  as `FabricatorParamStyle` (`FetchFunctionParamSchema`'s `out_styles`, replacing `vector<bool> arg_is_named`).
  - **Why**: the split forced every consumer to reconstruct one ordering rule ("positions are `Parameters` ++
    `NamedParameters`"), and a host that got the NULL substitution off by one would corrupt a POSITIONAL value
    rather than error. With one schema, position IS declaration order and that bug cannot be written.
  - **⚠ BOTH ordering rules are DuckDB's, not ours** — verified in `bind_table_function.cpp`: *"Unnamed
    parameters cannot come after named parameters"* and *"Table function can have at most one subquery
    parameter"*. `Params.Validate` moves those from CALL time to DECLARATION time. Named on a SCALAR is a
    declaration ERROR (DuckDB `ScalarFunction` has no named-parameter concept), never silently ignored.
  - **⚠ A table input is POSITIONAL-ONLY, and that is forced**: the binder's named-parameter path sets the
    argument name and the subquery branch then ignores it, so `f(t := (SELECT …))` silently binds as THE
    positional table arg. It MAY sit between positionals — DuckDB pushes a placeholder for the subquery slot
    (`parameters.emplace_back()`), so later positions keep their index. Its declared `StructType` is carried
    for US only: DuckDB registers `{LogicalType::TABLE}` and never sees it, so any schema validation is a
    BIND-TIME check of our own (not built).
  - `param_count` is **derived** (`Params.DeclaredCount`), excluding the table input so the number keeps
    meaning "arguments you pass a value for". It is not host-read at all (registration reads 3 columns) but IS
    user-visible via `fabricator_functions()`. Retired: `SqlGen.ParamSchema` + the `fabricator.named` tag.
  - **⚠ THE COMPILER FINDS ALMOST NONE OF THIS.** Removing an interface member leaves `override`s of a
    BASE-CLASS member compiling happily as DEAD CODE — ~25 declarations would have silently stopped being
    read. The gate is a GREP (zero live `fabricator.named`), not a green build. 18 classes that hold the two
    halves apart keep their shorthand via an EXPLICIT interface implementation
    (`Schema ITableFunction.Parameters => Params.Combine(...)`); consequence to know: reading `Parameters` off
    a CONCRETE subclass yields only the positional half.
  - **⚠ Do NOT script structural edits to C#.** A brace-matching insertion loop ran away (no damage — it never
    reached its write). A single-pass anchored insertion with an explicit class→interface map is the safe form.
  - **POSITIONAL and/or NAMED constant args now work on an IN-OUT / COLLECTOR too, not just named** (user
    requirement, 2026-08-02). The old bind marshalled `input.named_parameters` ALONE, which was fine only
    while cost args were named BY CONVENTION; the moment one could be declared positional, the signature
    accepted `f((SELECT …), 3)` and the 3 was **silently dropped before reaching C#** — a half-offered
    capability, worse than refusing it. `FabricatorMarshalInOutArgs` now walks the DECLARED order:
    TABLE_INPUT consumes its reserved slot and emits nothing (DuckDB pushes a placeholder Value for the
    subquery, so skipping the slot would shift every later positional), POSITIONAL takes the next
    `input.inputs` value, NAMED takes the supplied value or a typed NULL. Demo + gate: the global
    `fabricator_mix(<input>, factor, bias := k)`.
    - ⚠ **A named parameter must not be a DuckDB RESERVED WORD** — the demo first used `offset :=` and the
      call was a *parser* error, which reads as a broken function rather than a bad name.
  - **⚠ TWO DEFECTS THAT BOTH TIERS COULD NOT SEE, found by reading `duckdb_functions()` directly.** (1) With
    the input table a declared parameter but the host still tagging every declared parameter as named, `input`
    LEAKED into in-out/collector signatures as `input := STRUCT(…)`; an extra OPTIONAL named parameter breaks
    no call, so nothing failed. (2) For in-out/collector the OLD `Parameters` meant *named cost args*, so
    unflagged fields silently became POSITIONAL and `fabricator_delta_write(…, path := '…')` stopped binding.
    Both are now gated by asserting the SIGNATURE itself (verify_global_functions), which is the only thing
    that can catch "accepts an argument the implementation never receives".
  - **⚠ Apache.Arrow 23 cannot even CONSTRUCT `new StructType(empty)`** — `ArgumentNullException('fields')` on
    a non-null EMPTY list, so the message names the wrong problem. It fires in a STATIC FIELD INITIALIZER,
    taking down `CustomFunctions` and, through `ListGlobalFunctions`, silently dropping every global function
    registered after it — the visible symptom was an unrelated table function "not existing". Hence
    `Params.TableInput` uses a scalar placeholder when no columns are declared. This extends the known
    zero-field hostility one step earlier than export/import.
  - Gates: hermetic **63/63 — 5685** and service **44/44 — 1458**. The protocol refactor alone was
    5664/1446 — IDENTICAL to pre-refactor, which is the behaviour-preservation claim; the rest is the new
    signature/mixed-arg and `_each` coverage.
  - **THE SQL SERVER BINDING — BUILT + LIVE-VALIDATED 2026-08-02 (§9h). This closes §8's "largest remaining
    gap in reach", and building it found TWO SHIPPED BUGS.** The whole set was bound to a OneLake **Delta**
    attach, so a dbt project on a Fabric **Warehouse** over T-SQL could not call even
    `refresh_sql_endpoint`. Now: `ATTACH 'Server=<ep>.datawarehouse.fabric.microsoft.com;Database=LH'
    AS w (TYPE fabricator, SECRET fabric_sp)` → `w.fabric.refresh_sql_endpoint()` (the two ATTACH options this
    originally required are now INFERRED and renamed — §9n below).
    **No ABI and no C++ change** — `fabricator_storage.cpp` already forwards unknown ATTACH options as JSON.
    - **The recorded diagnosis ("credential plumbing") was the SMALLER half.** The real blocker: the function
      context held the OneLake **ROOT** and parsed workspace+item out of it, so the set was structurally
      unreachable from any other provider. ⚠ **The reason recorded here — "a Fabric SQL connstr supplies
      neither, its host is an opaque per-workspace routing GUID" — is FALSE, corrected 2026-08-03 (§9n):** the
      host's second base32 label IS the workspace GUID and `Database` IS the item. The refactor was still
      needed; only that justification was wrong. Context is now `(Workspace, Item, Credential)`, each provider supplying the pair its
      own way. `Root` had exactly two uses, both in `FabricApiClient`.
    - Credential rides a connstr marker (`;FabricatorFabricCred=`), the mechanism already proven by
      `AccessTokenKeyword` + `FabricatorDeltaCred`. **⚠ ORDER IS LOAD-BEARING** — the access-token marker means
      "everything after me is the token", so this one is appended AFTER and stripped BEFORE it.
    - **⚠ A pre-minted `access_token` is deliberately NOT carried** (SQL audience ≠ `api.fabric.microsoft.com`
      ⇒ guaranteed 401); carrying nothing falls through to the ambient chain, which works on and off Fabric.
      **`azure_tenant_id` — declared in `SecretFields` since the beginning, consumed by nothing — is now
      load-bearing**: SqlClient infers the tenant from the server, `ClientSecretCredential` cannot.
    - **⚠ The gate is the HOST (`*.fabric.microsoft.com`), NOT `ServerProfile.IsWarehouse`** — `EngineEdition
      == 11` also means **Synapse serverless**, and `IsWarehouse` would force profile detection (a connection)
      at ATTACH just to decide registration.
    - **⚠ BUG 1 (pre-existing): `lakehouses()` and `warehouses()` THREW ON EVERY CALL.** The
      `workspace :=` pass added the `args[0]` read to every catalog-bound table function but the declaration to
      all except these two; the base sizes the args array from the declared count ⇒ `IndexOutOfRangeException`,
      total not partial. It landed one day AFTER both were live-validated, and their only gate is live.
    - **⚠ BUG 2 (pre-existing): EVERY timestamp on the hand-rolled functions read as JANUARY 1970** — 15 sites
      / 5 files incl. the flagship refresh, all four job functions, the notebook runner, both semantic-model
      functions. `new TimestampArray.Builder()` **defaults to MILLISECOND** while the columns declare
      MICROSECOND; nothing reports the mismatch, so the host faithfully reads a number 1000× too small. It
      survived live validation of every affected function because each was checked for status/ids and **nobody
      looked at the times**. Functions on `FabricRowBuilder` were immune (it builds FROM the declared field);
      the fix gives the rest that property via one shared `TsType`/`TsBuilder()`.
    - `__all__` is now IMPLEMENTED on SqlServer (`ExpandAllSchemas`, lazy + `schema_filter`-aware), superseding
      §9g's "rejected loudly" — that was correct only while nothing used the sentinel.
    - Gate `verify_functions` 13 → **15** (negative control + a `cf_*` POSITIVE control, mutation-tested); the
      positive live path is manual, like `verify_dax`.
  - **VARIABLE LIBRARIES — 10 functions BUILT + LIVE-VALIDATED end to end (2026-08-03), §9j.** Fabric's
    per-environment config item (default value set + alternative sets, exactly one ACTIVE, flipped per stage by
    a deployment pipeline). Reads: `variable_libraries` / `variables(lib, value_set := …)` /
    `variable_value_sets` / the scalar `variable(lib, name)`. Writes:
    `create_variable_library` / `set_variable` / `set_variables_json` /
    `set_variable_override` / `set_active_value_set` / `drop_variable_library`. No new
    dependency — the pinned 2.18.0 SDK already carries `FabricClient.VariableLibrary.Items`.
    - **Why it earns its place: an `ItemReference` variable stores exactly `{workspaceId, itemId}`, which is what
      our own `workspace :=` / `item :=` overrides consume.** Proven live:
      `refresh_sql_endpoint(item := variable('cfg','target') ->> 'itemId')` refreshed the real
      lakehouse's 21 tables. So a dbt project reads its target from the library instead of hardcoding it.
    - **⚠ There is NO effective-value API** — the typed model stops at `ActiveValueSetName` and every value lives
      in the item DEFINITION as base64 parts, so resolution is ours (decode `variables.json`, overlay
      `valueSets/<name>.json` by name). Same shape as `notebook_parameters`.
    - **⚠ The definition API is WHOLE-DOCUMENT and has no ETag.** A write that sends only the part it changed
      DELETES the value sets and settings, so every setter reads all parts and writes all parts back — and that
      read-modify-write is LAST-WRITER-WINS. `set_variables_json` is the single-call declarative
      alternative (it also REPLACES, so an omitted variable is removed).
    - **⚠ Reads and writes are LONG-RUNNING OPERATIONS: the 13-step live script took 7m39s** for ~15 definition
      operations. Two `variable()` calls in one SELECT list are two reads — the "no cache across calls"
      decision is right for configuration but not cheap.
    - **`variable` is declared CONSISTENT and that is load-bearing** — our scalar default is VOLATILE, and
      a volatile function is never folded, so the default would cost one LRO PER ROW.
      `BoundFunctionExpression::IsFoldable()` is exactly `stability != VOLATILE`. (As a table-function argument
      it is evaluated once regardless: `bind_table_function.cpp` checks only `IsScalar()` then calls
      `EvaluateScalar(…, allow_unfoldable: true)` at bind.) Consequence: a PREPARED statement bakes the value in.
      Conversely **every WRITE function must stay VOLATILE** or it may run at bind, once for N rows, or be elided.
    - **⚠ CREATION is refused for a service principal** (`FeatureNotAvailable`), contradicting the docs' *"the
      variable library REST APIs support service principals"* — same as `ResetShortcutCache`, same error code as
      notebook creation. **Scope settled by measurement, not inference:** a library created by another identity
      is then fully driveable by the SP (definition GET/PUT, properties update, list all permitted) ⇒ principal-
      scoped and specific to creation, exactly like notebooks. **The error names the wrong cause** — the feature
      IS available. So creating the library is a one-time human action; everything after automates.
    - **⚠ Microsoft's docs contradict themselves in FOUR places, each a silent wrong answer if guessed**: the
      value-set folder is spelled both `valueSets\…` and `valueSet/…` (we read either + normalize `\`, write
      plural); `type` casing is unstable (`"String"` beside `"boolean"`) so types pass through VERBATIM; the REST
      page's type table OMITS `Guid` and `ConnectionReference` (so no closed enum — an unknown type parses as
      JSON, falling back to string, with the service as the validating backstop); and **`VariableOverride.value`
      is typed `String` and that is wrong** — mutation-testing showed it breaks **Integer** too, not just the
      object types. Variable names are NOT case sensitive, so both the read overlay and the write upsert match
      that way (otherwise an upsert appends a second entry and invalidates the library).
    - **⚠ THE DEFECT LIVE VALIDATION FOUND, and why the offline test could not:**
      `JsonElement.GetRawText()` returns the raw SOURCE SPAN, so a pretty-printed object value arrived in a SQL
      column as `{\r\n        "workspaceId": …}`. It is a READ-side bug (the portal or a git sync may indent, so
      normalizing belongs on read) fixed by re-serializing through a `Utf8JsonWriter`. **The offline round trip
      was blind to it because `ToJsonString()` emits compact JSON — the harness was reading back its own
      formatting convention. A round trip only tests the shapes you generate.**
    - Coverage: the live lifecycle incl. three negative controls (unknown value set, undeclared override,
      mistyped value) each erroring with the library left unchanged — plus **`dotnet/Fabricator.Bridge.Tests`,
      THE FIRST TEST PROJECT FOR BRIDGE LOGIC** (2026-08-03): 47 cases × {net10.0, net8.0} in ~100 ms over the
      format, incl. the write→read round trip, the pretty-printed case, and a named test per documentation
      contradiction. **Mutation-tested with three mutants, each killed at exactly its own tests** (folder
      spelling → 6; the docs' `value: String` → 4, incl. Integer; `Render`→`GetRawText` → 2).
      - **⚠ It has NO `ProjectReference` to `Fabricator.Bridge`, and adding one would BREAK TIER 0** — the Bridge
        project-references **engineered-wood (a submodule)** plus Arrow/Fabric SDK/SqlClient/AWS/Azure, and tier
        0's defining property is needing no C++, no vcpkg and no submodules. It **compiles selected Bridge source
        files directly**, admission rule: *a file belongs there only if its closure is the BCL*. A forcing
        function, not a workaround — it rewards keeping parsing/resolution/rendering out of the Arrow/SDK
        boundary, which is why `FabricVariableLibraryFormat.cs` was split out in the first place.
      - **WIRED INTO TIER 0** as a SECOND JOB (`bridge`) in `installer-core.yml`, floor 47, both TFMs × both
        OSes. Three things about that wiring are deliberate and easy to undo by accident:
        - **A separate job, not another step** — the count tripwire reads the FIRST `Total:` in its output file
          (`head -1`), so a second `dotnet test` piped into the same file would leave this project's floor
          silently unchecked. A tripwire that looks armed and is not.
        - **The path filter lists `dotnet/Fabricator.Bridge/**`, not the individual linked file** — filtering the
          one file would silently stop covering the next one someone links, the same failure as the
          submodule-pointer omission (a filter that misses the change the gate exists to guard).
        - **Both OSes**, because the format code normalises PATH SEPARATORS and asserts on CRLF in stored JSON;
          pinning that on one platform is how an accidental `Path.DirectorySeparatorChar` /
          `Environment.NewLine` dependency hides.
        ⚠ The workflow is still NAMED `installer-core` for a historical reason only (renaming it renames every
        status check) — read it as "tier 0", not "the installer".
  - **SPARK SESSIONS — `sessions([workspace := …])` BUILT + LIVE-VALIDATED (2026-08-03), §9k.** 27
    columns over the WORKSPACE-scoped `Spark.LivySessions.ListLivySessions(workspaceId)` — so, unlike
    `job_instances(item)`, it answers "what is on the Spark compute" with **no item argument and one
    request**. No ABI/C++ change, no new dependency. It is the Spark half in DETAIL (queued vs running time,
    runtime version, attempt number, `spark_application_id`, high-concurrency — none of which a job instance
    carries), not a better job list: job instances still cover Pipeline/Dataflow/TableMaintenance, which never
    appear as sessions.
    - **⚠ TWO CLAIMS I WROTE FIRST AND THE DATA FALSIFIED.** (1) "Interactive sessions have no job instance" —
      wrong: all 115 sessions carried a `job_instance_id`, and a spot-checked one WAS in that notebook's
      `job_instances` history, so it is a real join key. (2) **`JupyterSession` does NOT mean
      "interactive"** — every one observed was created by the RunNotebook JOB api with nobody clicking; the
      value names the session KIND (a Jupyter kernel), not its trigger. Whether a portal-driven session lacks a
      job instance is **UNVERIFIED** (no such session existed in the data) — do not restate it either way.
    - **⚠ The SAME work is labelled differently in TWO columns.** One identical `job_instance_id` reads
      `job_type='JupyterSession'`/`state='Succeeded'` as a session and `job_type='RunNotebook'`/
      `status='Completed'` as a job instance ⇒ a predicate carried across matches NOTHING. Values pass through
      VERBATIM (normalising would hide which API answered; both are extensible enums that can grow).
    - **⚠ THE SQL VALUES ARE NOT THE SDK MEMBER NAMES**: the member is `NotStarted`, the column says
      **`Not Started` — with a space** (captured live), while `InProgress` has none ⇒ a predicate derived by
      reading the enum is wrong. Observed: `Not Started`/`InProgress`/`Succeeded`; `Failed`/`Cancelled`/
      `Unknown` are declared but their SPELLING IS UNCONFIRMED. Also **casing differs across columns of the SAME
      ROW** — `item_type` is lower-case (`notebook`) while `job_type`/`state` are PascalCase. And `submitter`
      (display name) was EMPTY on all 115 rows while `submitter_id` was populated on all 115 — group by the id.
    - **⚠ ALL 34 FIELDS SHIP — and a WRONG CONCLUSION OF MINE IS RECORDED HERE ON PURPOSE.** The seven
      ALLOCATION columns (driver/executor cores+memory, num_executors, dynamic allocation ×2) were NULL in all
      116 observed sessions and I first DROPPED them, claiming "the list endpoint never populates them". Wrong.
      NULL across finished sessions was correctly rejected as insufficient ("only reported while running"
      explained it equally well), so a session was manufactured (`run_notebook(…, wait_seconds := 0)`) and polled
      to `InProgress` — still NULL. That kills the LIFECYCLE explanation and says NOTHING about the structural
      one, **because the variable that actually differed is session KIND**: by `runtime_version` the history is
      `jupyter1.0`/`JupyterSession` (Python notebook runs) plus `2.0`/`SparkSession`+`SparkBatch` (SYSTEM-managed
      `Lakehouse Operations`/`Table Maintenance`) — **no user-authored PySpark session at all**, the only kind
      carrying a real executor allocation. A NULL there means "this workload has no Spark allocation", i.e.
      information. PySpark population is EXPECTED but UNVERIFIED here. **Standing lesson: eliminating one rival
      explanation is not eliminating all of them — name the variables you did NOT control before generalising
      from a negative result.** (`executor_cores` is VARCHAR because the SDK types it `object`.)
      **The manufactured session was still worth it — it is the POSITIVE CONTROL for the headline feature**:
      every prior row was `Succeeded`, so the function had never once been observed doing what it exists for;
      polling showed `InProgress` with `running_seconds` 26→53.
    - **⚠ Live cell OUTPUT is an API absence, not a gap to fill**: `spark_application_id`/`resource_uri` are
      POINTERS, and the whole SDK assembly has no log-fetching method. This gets you to the session, not inside it.
    - **`Duration` is a `{value, unit}` PAIR, not a TimeSpan** — a CLASS (absent-able) whose `TimeUnit` is an
      Azure EXTENSIBLE enum (Seconds/Minutes/Hours/Days). Normalised to seconds as DOUBLE, compared against the
      TYPED members (a rename is then a compile error), and an **unknown unit yields NULL, not the raw number**:
      a column mixing seconds with minutes makes every `ORDER BY` wrong, and wrong is worse than absent. ⚠ It
      also collides by name with `Apache.Arrow.Types.TimeUnit` — hence the alias.
    - **`FabricRowBuilder.EndRow()` now VERIFIES every column got exactly one value** and names those that did
      not. Identity there is a bare INDEX and this function writes 27, so the off-by-one the class exists to
      prevent was one edit away; a skipped column used to surface as a length mismatch deep in `RecordBatch`
      construction, or — two skips in different rows — as a batch that builds fine with values in the WRONG
      rows. All 7 pre-existing sites were audited against their declared counts FIRST (all correct), so the
      guard changed nothing. Also gained DOUBLE support. Hermetic **63/63 — 5685** (unchanged ⇒
      behaviour-preserving; `fab_delta_info` is what exercises the shared builder offline). The function itself
      is live-only, like `verify_dax`.
  - **ATTACH OPTIONS INFERRED + RENAMED `API_WORKSPACE`/`API_ITEM` — DONE 2026-08-03 (breaking), §9n.** A Fabric
    SQL attach now needs NO Fabric-specific option: `ATTACH 'Server=<ep>…;Database=MyLH' AS w (TYPE fabricator,
    SECRET fabric_sp)`.
    - **⚠ THE ENDPOINT HOST IS NOT OPAQUE — this file said it was, and that was FALSE.**
      `<base32(cluster GUID)>-<base32(WORKSPACE GUID)>.datawarehouse.fabric.microsoft.com`: 26 unpadded
      lower-case RFC-4648 base32 chars per label = 130 bits carrying a 16-byte GUID, and the **second** label
      decodes **little-endian** (.NET `Guid.ToByteArray()` order) to exactly the workspace id. Established by:
      all 3 lakehouses AND a warehouse in one workspace returning a BYTE-IDENTICAL host while their own
      `sql_endpoint_id`s matched neither label ⇒ label 2 = workspace, label 1 = a workspace-level SQL cluster.
      The **item** needs no decoding — on a Fabric SQL endpoint `Database` IS the item.
    - **Live proof is DISCRIMINATING, not just "rows came back"**: two attaches differing ONLY in `Database`,
      same server, no options ⇒ `LH` **21** tables vs `LH2` **0**; and `API_ITEM 'LH2'` on the `LH` attach ⇒ 0,
      so an override still outranks the default.
    - **⚠ The inference must NEVER GUESS.** The encoding is UNDOCUMENTED, one tenant, one region ⇒
      `WorkspaceIdFromHost` returns **null** on any doubt (wrong suffix / label count / label length / a char
      outside the base32 alphabet) and the caller falls back to demanding the option. A WRONG workspace id would
      aim REST calls at a different workspace the identity may well have access to, so silence is the only
      acceptable failure. The enumerate-and-match fallback (list workspaces → compare each item's endpoint
      connstr to this host) is **deliberately NOT built**: O(workspaces × items) REST calls AT ATTACH to convert
      a clear error into a slow success, on the one path whose defining property is costing no round trip.
    - **Why RENAMED and not merely optional**: `WORKSPACE`/`ITEM` read as if they selected the ATTACH TARGET.
      They do not — they scope the `fabric.*` functions only, and two attaches differing solely in `ITEM` expose
      IDENTICAL tables (the option is invisible until a function runs). **⚠ The old names now ERROR rather than
      being ignored, and that guard is load-bearing**: unknown ATTACH keys are dropped for forward-compat, so
      leaving them unhandled would silently fall back to the inferred default — redirecting a refresh at the
      `Database` item instead of the named one, with no message. Verified live.
    - **The OneLake side ALREADY did this — no work needed.** `ParseOneLake` takes the container as the workspace
      and the first segment as the item, and BOTH resolvers short-circuit on `Guid.TryParse`, so a **pure-GUID
      root costs ZERO resolution calls** (verified live: 21 tables + `fabric.sessions()` 116).
    - **Which identifiers the APIs accept** (easy to assume wrongly): the Fabric REST API is **GUID-ONLY** —
      every SDK method takes `Guid workspaceId`/`Guid itemId`. Accepting a NAME is purely our convenience layer
      (`ResolveWorkspace`/`ResolveItem` list + display-name match, cached per catalog in `_idCache`), so a name
      costs one listing on first use and a GUID costs none.
    - **Gate: `Fabricator.Bridge.Tests` 47 → 85, tier-0 floor raised.** `FabricSqlEndpointHost` is BCL-only BY
      DESIGN — it hand-rolls the base32 decode (the BCL has none) and the connstr parse instead of using
      `SqlConnectionStringBuilder` — so the undocumented part is testable OFFLINE. **It paid for itself
      immediately: the tests failed on first run and caught a real bug** (the label extraction took the substring
      after the LAST dot, yielding `datawarehouse` instead of the encoded pair).
  - **JOB-INSTANCE FAN-OUT — DONE 2026-08-03 (breaking on ONE parameter), §9m.**
    `fabric.job_instances([item := …] [, item_type := …])`: omitting `item` fans out over every item of
    `item_type`, one `ListItemJobInstances` per item, with `item_name`/`item_id` APPENDED (appended, not
    prepended — D4 keeps `SELECT *` additive). Live: `'Notebook'` → 53 runs across 2 notebooks; `'Lakehouse'` →
    LivySession 47 / TableLoad 9 / TableMaintenance 7.
    - **⚠ `item` moved POSITIONAL → NAMED and HAD to**: DuckDB arity is fixed, so a positional parameter cannot
      be omitted, and omitting it is what selects fan-out. Shipped in the SAME breaking window as §9l so callers
      migrate once.
    - **Why here and not in `sessions()`**: sessions are already workspace-scoped in one request but Spark-ONLY;
      job instances cover every item kind and the API is strictly per-item, so enumerating items is the only way
      to ask "what ran in this workspace". The two CROSS-VALIDATED — the lakehouse fan-out's `LivySession` = 47
      is exactly the 47 `Session Livy Run` rows `sessions()` reports.
    - **Two deliberate refusals**: omitting BOTH `item` and `item_type` ERRORS rather than sweeping the workspace
      (unbounded × per-principal throttle), and there is **no `max_items` cap** — a cap would under-report while
      looking complete. One item's failure fails the whole statement, on purpose (a partial result that looks
      complete is worse). Cost is stated in the error, not hidden.
    - **⚠ A job instance's `StartTimeUtc`/`EndTimeUtc` are ISO STRINGS** (a Livy session's are `DateTimeOffset`)
      ⇒ `FabricRowBuilder.Iso`, not `.Ts`. The compiler catches it. Binding moved to `FabricRowBuilder` (10 cols).
    - **THE SECOND AXIS — `sessions(all_workspaces := true)` — ALSO DONE (2026-08-03).** The job fan-out
      enumerates ITEMS inside one workspace; this enumerates WORKSPACES (one `ListWorkspaces` + one
      `ListLivySessions` each) and appends `workspace_name`/`workspace_id`. Mutually exclusive with
      `workspace :=` (errors — naming one workspace and asking for all is contradictory). `all_workspaces` is a
      REAL `BooleanType` read via `FabricArgs.Bool`, safe because this binding reads args individually — the
      "BOOLEAN named parameter silently reads NULL" hazard is specific to `FabricRowsFunction`.
      - **⚠ THE MULTI-WORKSPACE AGGREGATION IS UNVERIFIED — the tenant exposes exactly ONE workspace to this SP**,
        so a fan-out result is INDISTINGUISHABLE from the single-workspace one. Do not read a green run as
        coverage; a second workspace is the only thing that settles combining/attribution/paging.
      - **What IS proven is that the fan-out PATH executes, via a constructed discriminator** rather than a row
        count: attach by a **GUID** root so the single-workspace default carries no name ⇒ `sessions()` gives
        `workspace_name = NULL` while `sessions(all_workspaces := true)` gives `Test`, which could ONLY come from
        `ListWorkspaces`. (Hence `workspace_name` is NULL in single mode on a GUID default — same rule as the job
        fan-out's `item_name`: echo what the caller knows, never pay for a listing to restate it.)
      - A per-workspace failure fails the WHOLE statement (consistent with the item fan-out). **Unvalidated for
        the interesting case** — "can see a workspace but cannot list its sessions" has never been observed here.
        If that proves common the answer is an `error` COLUMN, not a silent skip.
    - **§9m carries the FAN-OUT VERDICT for every other candidate.** The deciding pattern: fan out when the
      per-item call is a cheap LIST; REFUSE when it is a long-running definition read. So
      `semantic_model_refreshes` (over `semantic_models()`) — **dropped from the recommendation by the user
      2026-08-03: not needed** — then `mirroring_status`/`mirrored_tables`, `data_access_roles`, the
      deployment-pipeline trio, `list_shortcuts`; **`git_status` over WORKSPACES is DEFERRED (user, 2026-08-03)
      until a git-connected workspace exists to test against** — writing it blind would ship an untested
      promotion surface, the same reason P3 is "wired but NOT live"; and `notebook_parameters` + `variables` are
      **NO** — each is a ~20 s LRO, so fanning out multiplies a multi-minute operation behind a call site that
      looks cheap.
  - **THE `fabric` SCHEMA — DONE 2026-08-03 (BREAKING, no aliases), §9l.** `dbo.fabric_sessions()` →
    **`fabric.sessions()`**: one dedicated schema, the `fabric_` prefix dropped from all **51** functions.
    C#-only — no ABI, no C++ change. Why: the `__all__` sentinel declares each function once PER DISCOVERED
    SCHEMA, so on `dbo`+`dbt` the set rendered as **102** rows in `duckdb_functions()` (measured **51** after);
    and it separates a DATA schema discovered from storage from a FUNCTION namespace the provider declares.
    - **⚠ IT IS NOT A RENAME — it is a catalog-structure change.** `fabricator_catalog.cpp:99` SILENTLY SKIPS a
      declared function whose schema the provider did not DISCOVER (`if (sit == schemas_.end()) continue;`) —
      deliberately, since that is how ATTACH `schema_filter` reaches functions. So the schema must be ADVERTISED
      by each hosting provider, gated on the SAME condition as the registration:
      `DeltaCatalog.CatalogSchemaNames()` (gate `IsOneLake`) and `SqlServerBackend.SchemasMetadata()` (gate
      `IsFabricEndpoint`). **MUTATION-TESTED because "silently" is the claim**: with the Delta gate reverted the
      ATTACH still SUCCEEDED with no error or warning, `duckdb_functions()` showed 0 in `fabric`, and the call
      failed as *"Table Function with name sessions does not exist! Did you mean main.seq_scan?"* — pointing
      nowhere near the cause.
    - **⚠ The name must NOT join the `__all__` EXPANSION list** — that list means "every DATA schema", and
      feeding `fabric` in would re-declare the provider macros + `fab_delta_info` inside it, restoring the very
      duplication this removes. Hence `CatalogSchemaNames()` is SEPARATE from `SchemaNames()`, and
      `ExpandAllSchemas()` still reads `SchemasSql` directly. The two lists look redundant and are not.
    - `fabric` is deliberately EXEMPT from `schema_filter` on both providers (that option scopes DATA discovery;
      deleting the whole Fabric API because someone narrowed their tables would be a surprising coupling —
      `function_filter` is the option for functions). DDL into it is REFUSED
      (`DeltaCatalog.RejectFunctionSchemaDdl`), because `CREATE TABLE cat.fabric.t` would otherwise create a
      real `fabric/` folder that the NEXT attach discovers as a data schema — the namespace quietly stops being
      separate. Only where the synthetic schema exists; elsewhere `fabric` is a name a user may legitimately use.
    - **⚠ TWO NEGATIVE CONTROLS WERE ABOUT TO GO VACUOUSLY GREEN**: `verify_delta_catalog_functions` §4 and
      `verify_functions` both asserted `function_name LIKE 'fabric\_%'` = 0, which after the rename matches
      NOTHING whether or not the set is registered. Both now key on `schema_name='fabric'`; the Delta suite also
      asserts the SCHEMA is absent, and THAT is the load-bearing one — mutating the `IsOneLake` gate leaves the
      function-count assertion passing (a local root registers nothing to leak) and is caught only by the schema
      assertion (verified: the mutant failed at exactly that line). 27 → **28**.
    - **Rename mechanics worth reusing: the substitution is NOT `s/fabric_//g`.** Three token classes had to be
      protected first — **`fabric_sp`** (the SECRET in every example; stripping gives `sp` and breaks every
      snippet), **`fabric_*`** (the GLOB in prose; a blind strip leaves a meaningless bare `` `*` `` — it became
      `fabric.*`), and **`<cat>.dbo.fabric_<fn>`** (33 qualified call sites in the README, 12 in docs; stripping
      only the prefix leaves `<cat>.dbo.<fn>`, wrong in a way that still LOOKS right). The glob was found because
      the occurrence COUNT disagreed by one with the number of function references — **an arithmetic disagreement
      between two ways of counting is what caught it**, not review.
    - Gates: hermetic **63/63 — 5686**; live end-to-end (`fabric` beside `dbo`/`dbt`, 51 functions ×1 each, a
      table fn, a named-parameter call, a scalar, and the DDL refusal).
  - Output shape rule (D4): typed flat columns + one raw-JSON column for polymorphic parts; **no STRUCT
    wrapping** (adding a column is additive for `SELECT *`; adding a struct FIELD changes a column's type
    and breaks bound views), no JSON-only. Every `table`-kind function also gets a dead `_each` sibling —
    pre-existing host behaviour, shared with SqlServer's custom table functions.
  A table function that sets neither `serialize` nor `deserialize` still takes part in DuckDB's
  **common-subplan optimizer** (1.5.4+), which dedups subplans by SERIALIZING each operator and hashing
  the bytes. `FunctionSerializer::Serialize` writes only name+arguments in that case and **does not
  throw**, so `fabricator_scan`'s signature carried NO table identity — ours lives in
  `ArrowStreamBindData`. `LogicalGet::Serialize` does contribute returned_types/names/filters, and
  `common_subplan_optimizer.cpp:120` canonicalizes `table_index` to 0 before hashing ⇒ **two scans of
  DIFFERENT tables that share a schema hashed IDENTICALLY**, one was materialized as a CTE, and both
  consumers read the FIRST table's rows. `ArrowStreamBindData::Equals` was correct all along and is
  never consulted — the optimizer compares BYTES, not `FunctionData::Equals`. That is the whole trap.
  - Found by reading **hugr-lab/mssql-extension#211** (same defect, same fix) and checking whether we
    had it. We did, on **every provider** — one `FabricatorTableEntry` / one `fabricator_scan` serves
    SQL Server, Delta and DAX; reproduced on the first two, and DAX reaches the identical path via
    `DaxCatalog.ScanTable`. DAX was arguably the most exposed in practice: it is read-only and the
    failing shapes are pure read shapes (a measure over table A vs table B).
  - **Affected shapes** (all silent): identical aggregate subplans over two same-schema tables — a
    `UNION ALL` of aggregates, or two scalar subqueries. **Unaffected:** joins / EXCEPT / INTERSECT /
    plain unions of rows (bare gets are not materialized), differing column names or types (they ARE in
    the signature), same-table-different-filter (the differing `Filter` child changes the subplan
    signature — safe by plan shape, not by design), and every global/discovered function (their args
    ride in `parameters`, which IS serialized).
  - **Fix**: `FabricatorScanSerialize` writes catalog/schema/table **plus the pushed spec**
    (`filter_json`+constants, `native_filter_sql`, `top_n`, `order_by_json`, `at_unit`/`at_value`) so
    two differently-pushed scans of ONE table cannot collapse either; identical scans still dedup, which
    for a remote provider is a real win. Gate `verify_delta_subplan_dedup` (36), mutation-tested.
  - **`PRAGMA verify_serializer` does NOT work on a fabricator catalog scan, and never did.**
    `LogicalGet::Deserialize` calls `FunctionSerializer::DeserializeBase` UNCONDITIONALLY (before it
    checks has_serialize), resolving the function BY NAME against `TABLE_FUNCTION_ENTRY`; the catalog
    scan is handed out by `GetScanFunction` and is not a registered catalog function ⇒ "Failed to find
    function fabricator_scan()". So `FabricatorScanDeserialize` is UNREACHABLE — and must still exist,
    because `Serialize` only emits bind data when BOTH callbacks are set. Do not "clean it up".

## 13. VARIADIC parameters (`Params.VarArgs`) — as built, 2026-08-31

Scalar, table and SQL-generating functions may declare a **variadic tail**: any number of trailing
arguments. Lateral and table-in-out are **deferred** (see §13.5). C# + C++, **no ABI bump** — the style rides
the existing `fabricator.param_style` field metadata as a new value, exactly as `constant` did.

### 13.1 How DuckDB does it, and why that shape is the whole design

`varargs` is a single `LogicalType` on `SimpleFunction`, the shared base of `ScalarFunction`, `TableFunction`
and `AggregateFunction` (`function.hpp`); `LogicalTypeId::INVALID` means "not variadic". The entire contract
is `FunctionBinder::BindVarArgsFunctionCost` (`function_binder.cpp`):

```cpp
if (arguments.size() < func.arguments.size()) return optional_idx();   // minimum arity
for (idx_t i = 0; i < arguments.size(); i++) {
    LogicalType arg_type = i < func.arguments.size() ? func.arguments[i] : func.varargs;
    ... ImplicitCastCost(arguments[i], arg_type) ...
}
```

So **`arguments` is a fixed prefix = the MINIMUM arity, `varargs` is the type every further argument must
implicitly cast to, and there is no maximum.** Overload resolution costs a variadic candidate like any other,
so a fixed-arity overload still wins on cost when both match. `Function::CallToString` renders it `[TYPE...]`,
which is what a too-short call's error message shows.

⇒ our declaration's tail field is **not** one of `tf.arguments`; it names the tail's TYPE.

**⚠ In DuckDB's own catalog `varargs` is an OVERLOAD-RESOLUTION device, not an arity contract**, and reading
it as one misleads. MEASURED: `cardinality(MAP{'a':1}, 42, 'junk')` binds through the variadic path and is
then refused by cardinality's OWN bind (*"Cardinality must have exactly one arguments"*); `to_json()`,
`struct_insert()` and `array_value()` likewise register with minimum arity **0** and enforce the real rule in
their bind. `hash(1,2,3)` and `list_concat()` are genuinely variadic. So `duckdb_functions().varargs` reports
the REGISTRATION faithfully; several functions register permissively on purpose and pay only a worse error
message.

### 13.2 The author contract

`Params.VarArgs(name)` (ANY tail — the `NullType` sentinel this protocol has always used for
`LogicalType::ANY`) or `Params.VarArgs(name, type)` (homogeneous tail). Two structural rules, refused rather
than reinterpreted:

- **at most one** — DuckDB carries exactly one varargs type per function;
- **it must be the last POSITIONAL field** — named parameters may follow, since they are a separate
  namespace. A positional field after the tail could never be filled: every argument past the prefix belongs
  to the tail, so the caller would pass a value the function never receives at the position it declared.

Enforced in **two** places on purpose. `Params.ValidateVarArgs` (C#) is the author-facing check and also
gates the KIND (`Params.Validate(..., allowVarArgs:)` — false for lateral/in-out, refused by name rather than
silently registered as an ordinary positional parameter). `FabricatorVarArgsIndex` (C++) re-checks where the
signature is BUILT, because that is the last point before a malformed declaration becomes a registered
function.

**⚠ It is deliberately NOT checked in `GetFunctionParamSchema`**, the one crossing that sees every
declaration: the host treats ANY failure there as "the function is stale" and silently drops it, so a
declaration bug would present as a function that does not exist. Loud in the right place beats early in the
wrong one.

### 13.3 What arrives, and the one thing that had to change everywhere

**The args batch of a variadic call is WIDER than `Parameters`.** The fixed prefix keeps its declared
positions and names; each tail argument follows in call order, named `<tail>_0`, `<tail>_1`, …
(`FabricatorArgName`, which subsumes the historical `arg<i>` fallback). An implementation reads the count
from the batch.

**⚠ THE TRAP THIS FEATURE IS BUILT AROUND: every args marshal in `fabricator_schema_entry.cpp` initializes a
`DataChunk` from the DECLARED types and then loops the SUPPLIED values.** For a variadic call there are more
values than declared types, so each marshal writes past its chunk. `FabricatorExpandVarArgs` widens the
declaration to the actual call before the chunk is built; a non-variadic function comes back byte-identical,
so no call site needs a second branch. Three marshals needed it: the TVF bind's pure-positional branch, its
mixed positional+named branch, and the sqlgen `bind_replace`.

**An ANY tail keeps each value's own runtime type** (DuckDB inserts no cast for ANY), which is what makes a
heterogeneous call arrive verbatim — gated by asserting `int32`/`utf8`/`bool` side by side from one call. A
concrete tail type is applied by DuckDB before the batch is built, so the marshal takes the declared type
there; the scalar BIND additionally reads `bound_function.varargs` for a tail slot, because at bind time the
cast has not been inserted yet and `arguments[i]` does not exist past the prefix.

**⚠ A CONCRETE TAIL IS NOT "ANYTHING, COERCED", and the obvious reading is wrong.** DuckDB applies its
ORDINARY implicit-cast rules per tail argument, so a cast it declines is a BIND error exactly as for a
declared parameter. MEASURED on the `BIGINT` tail of `cf_va_sum`: `(1, 2::SMALLINT, 3::TINYINT)` → 6, while
`(1, 3.0)` is refused — *"No function matches … `cf_va_sum(INTEGER_LITERAL, DECIMAL(2,1))`"*, because
DECIMAL→BIGINT is lossy. ⚠ The first version of that demo's own doc comment claimed the DECIMAL case worked;
running it is what corrected it. **An ANY tail is the declaration that accepts anything.**

### 13.4 The pre-existing defect it made reachable

A SQL-generating function with **minimum arity 0** — which only a variadic one can have — called with no
arguments crossed as a **zero-FIELD Arrow schema**, which Apache.Arrow cannot represent in either direction
(`ArgumentNullException` on `'fields'`). `FabricatorSqlGenBindReplace` constructed its `ArrowProducer`
unconditionally, so the bind failed with an error naming nothing recognizable. Fixed by passing **no stream**
when there are no arguments — the same rule, for the same reason, as the zero-argument branch that
`FabricatorTableFunctionBind` has always had; it was simply unreachable on the sqlgen path until a generator
could take zero arguments. The managed side already read a null args stream as an empty batch
(`SqlGen.Generate`'s `args ?? EmptyArgs()`), so the generator's own arity rule is what refuses the call.

### 13.5 The deferred kinds, and why they must REFUSE rather than ignore

Lateral and table-in-out positional slots ARE the per-row input columns, so a tail is a variable-width wire
rather than a wider args batch — and it would have to compose with `Params.Constant`, whose slots are also
trailing and also stripped from the wire (`LateralBindData.wire_slots` / `arg_width`). Neither question is
answered. Aggregates could carry one (DuckDB's `AggregateFunction` shares the same `varargs` field) but the
state/update marshal was not examined, so they are out of scope too.

**⚠ On every one of those kinds, a `switch` with no VARARGS case does not "ignore" the declaration — it
falls through to the positional branch and registers the tail as an ordinary `ANY` argument.** The function
then binds, runs, and does not do what its declaration says, which is the failure mode this protocol exists
to prevent. So each refuses explicitly, at REGISTRATION:

| kind | refusal |
|---|---|
| lateral | `FabricatorMakeLateralFunction` throws (plus the C# `Params.Validate(allowVarArgs: false)` at bind) |
| in-out, collector | `FabricatorBuildInOutSignature` → `FabricatorRefuseVarArgs` |
| aggregate | both registration sites → `FabricatorRefuseVarArgs` |

⚠ For a GLOBAL declaration the throw is caught by the registration loop's own `continue` — the standing
policy that one bad declaration must not fail extension load — so the function is simply ABSENT rather than
misregistered. Loud at call time ("does not exist"), and the same behaviour a table-input on a lateral has
always had.

### 13.6 What it is worth

**A `LIST` parameter already covers the homogeneous case** (`f(['a','b'])`) and needs none of this machinery.
What a tail buys is HETEROGENEOUS, individually-typed arguments — which is exactly the shape of DuckDB's own
variadics (`printf`, `format`, `concat_ws`, `struct_pack`, `row`, `create_sort_key`). Every shipped demo
mixes types at the call site for that reason.

### 13.7 Demos, gates and the mutation record

| demo | kind | path | gate |
|---|---|---|---|
| `fabricator_va_concat(sep, …)` | scalar | load-time global | `verify_global_functions` 118 → **145** |
| `fabricator_va_args(label, …)` | table | load-time global | (same suite) |
| `fabricator_va_values(…)` | sqlgen | load-time global | `verify_sqlgen` 59 → **76** |
| `cf_va_sum(…)` | scalar, CONCRETE tail | **attach-time catalog** | `verify_custom_functions` 89 → **101** |

**The load-bearing assertion in each is the SIGNATURE from `duckdb_functions()`**, not the rows: an
implementation reading its args batch positionally would produce identical rows for a fixed-arity
declaration, so only `varargs` and `parameter_types` distinguish "registered variadic" from "registered with
N arguments".

⚠ `cf_va_sum` exists because **a declaration form that only ever ships GLOBAL looks covered while the
ATTACH-TIME registration path is untested** — the same gap the catalog-views work had to close. It is a
second registration site (`GetOrCreateScalarFunction`), not a second spelling.

**Mutation-tested, four mutants, each killed at its own assertion:**

| mutant | dies at | after |
|---|---|---|
| 1 — `fn.varargs` never set on a scalar | the `duckdb_functions()` signature assertion | 119 pass |
| 2 — the TVF marshal not widened to the actual call | the per-tail-argument rows | 129 pass |
| 3 — tail columns named `arg<i>` instead of `<tail>_<k>` | the same rows, on the NAME column | 130 pass |
| 4 — `tf.varargs` never set on a generator | the variadic generator's first call | 60 pass |

**⚠ THE FIRST MUTATION RUN WAS VOID AND REPORTED ALL FOUR AS SURVIVORS.** It drove the build from a bash
script via `cmd /c`, which MSYS rewrites into a path — `cmd` then starts INTERACTIVELY, reads EOF and exits
**0** having built nothing, so each "mutant" was the clean binary tested again. The tell is a bare
`D:eposabricator-extension>` prompt where ninja output belongs. This trap is already recorded in
`CLAUDE.md`; it is repeated here because a surviving mutant is normally read as a weak gate, and here it
meant the opposite. **Drive every C++ build from the PowerShell tool.**
