# Custom Functions — Design (Phase 3+)

Status: **design / not yet implemented.** Locks the C++⇄C# contract and the C# authoring API for
custom **scalar**, **table**, and (later) **table-in-out** functions, across the one-binary /
multi-provider architecture (see `CLAUDE.md` → target architecture).

> ⚠ **A KIND THIS DOCUMENT DOES NOT COVER: `lateral`** (`ILateralTableFunction`, built 2026-08-22, ABI v79) —
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
| **Scalar** | Fixed; at most a function of the **argument types** (never runtime values) | Declared return type, or an optional `bind(argTypes) → returnType` for polymorphic ones |
| **Table** | **Bind time**, from the **constant arguments** (TVF/proc args are literals at bind) | `bind(constArgs) → Schema` — for SQL Server this calls `sp_describe_first_result_set` (§6) |
| **Table-in-out** | Bind time (from the input-table schema + args) | `bind(inputSchema, args) → Schema` (Phase 4) |

So a scalar's output is essentially static (matches the user's intuition); a table function's output is
**late-bound** and can depend on its parameters — which is exactly what makes mapping a stored proc work.

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
