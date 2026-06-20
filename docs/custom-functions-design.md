# Custom Functions — Design (Phase 3+)

Status: **design / not yet implemented.** Locks the C++⇄C# contract and the C# authoring API for
custom **scalar**, **table**, and (later) **table-in-out** functions, across the one-binary /
multi-provider architecture (see `CLAUDE.md` → target architecture).

## Goals

- Custom **scalar** + **table** functions now; **table-in-out** sketched (Phase 4).
- **Vectorized over Arrow** end to end — a function receives a `RecordBatch` of arguments and returns
  Arrow. No row-at-a-time callbacks (the thing that makes the DuckDB.NET C-API binding feel un-vectorized:
  it wraps a per-row `Func<>`; we hand whole batches instead).
- **Two sources, one contract:**
  1. **Authored** provider/library functions, declared in C# (load-time, global).
  2. **Discovered** SQL Server stored procs / table-valued + scalar UDFs, mapped at ATTACH (catalog-bound).
- A **nice C# authoring API** (lambda / attribute / derived-class — see §5), all lowering to one contract.

Non-goals (now): aggregate / window functions; non-Arrow execution; the in/out pump protocol (Phase 4,
sketched in §7).

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
// ArrowNet.Bridge (provider-agnostic)
public enum FunctionKind { Scalar, Table, TableInOut }

public sealed record FunctionDeclaration(
    string  DeclId,            // stable id the ABI uses to invoke (e.g. "sqlserver:dbo.usp_orders")
    string  Name,              // DuckDB-facing name (global) or catalog entry name
    FunctionKind Kind,
    Schema  ParamSchema,       // ordered params as an Arrow schema (field name = param name)
    Schema? OutputSchema,      // scalar: 1-field schema; table: fixed schema, or null = late-bound (call Bind)
    bool    LateBound,         // table: resolve OutputSchema via Bind(constArgs) at DuckDB bind time
    string? Metadata);         // opaque provider JSON (e.g. how to invoke: EXEC vs SELECT, proc id)

public interface IArrowFunction {
    FunctionDeclaration Declaration { get; }

    // Table (late-bound) only: compute the output schema from the constant args. Scalars/fixed tables
    // return Declaration.OutputSchema. May run a provider round trip (e.g. sp_describe_first_result_set).
    Schema Bind(IReadOnlyList<object?> constantArgs);

    // Scalar: args = one RecordBatch (N rows, ParamSchema columns) -> one IArrowArray (N rows).
    IArrowArray  ExecuteScalar(RecordBatch args);

    // Table: constant args -> a stream of result batches (matching the bound output schema).
    IArrowArrayStream ExecuteTable(IReadOnlyList<object?> constantArgs);
}
```

Discovered SQL Server functions implement this **data-drivenly** (the provider synthesizes the
declaration from `sys.*` and implements `Bind`/`Execute` via `sp_describe_first_result_set` + `EXEC` /
`SELECT`). Authored functions get the sugar in §5, which compiles to the same interface.

## 4. The ABI (additions to `abi.h`)

Declarations are listed; schemas and execution go through the existing zero-row-stream / Arrow-stream
mechanisms so **no new Arrow-IPC parsing is needed in C++** (it reuses `PopulateReturnSchema`).

```c
// List a provider's authored (global) functions, or an attached catalog's discovered functions.
// Returns rows: decl_id (utf8), name (utf8), kind (int), late_bound (int), metadata (utf8).
// (Param/output schemas are fetched per-decl below — keeps each row flat/string-readable.)
int32_t (*list_global_functions)(const char *provider, ArrowArrayStream *out, char **err);
int32_t (*list_catalog_functions)(ArrowNetHandle handle, ArrowArrayStream *out, char **err);

// Per-decl Arrow schemas as ZERO-ROW streams (C++ reads them like COLUMNS metadata → LogicalTypes).
int32_t (*get_function_param_schema)(ArrowNetHandle handle, const char *decl_id,
                                     ArrowArrayStream *out /*zero-row*/, char **err);
// Scalar / fixed table: the output schema. (Late-bound tables: use bind_function instead.)
int32_t (*get_function_output_schema)(ArrowNetHandle handle, const char *decl_id,
                                      ArrowArrayStream *out /*zero-row*/, char **err);

// Table late-binding: given the constant args (1-row Arrow batch, like filter_values), return the
// output schema (zero-row stream). Called from the DuckDB TableFunction bind.
int32_t (*bind_function)(ArrowNetHandle handle, const char *decl_id,
                         ArrowArrayStream *args /*1-row, nullable*/, ArrowArrayStream *out, char **err);

// Execute. Scalar: args = one batch (N rows) -> out = one batch (N rows, the single output column).
//          Table:  args = 1-row batch of the constants    -> out = stream of result batches.
int32_t (*execute_scalar)(ArrowNetHandle handle, const char *decl_id,
                          ArrowArrayStream *args, ArrowArrayStream *out, char **err);
int32_t (*execute_table)(ArrowNetHandle handle, const char *decl_id,
                         ArrowArrayStream *args, ArrowArrayStream *out, char **err);
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
  the `ArrowNetSchemaEntry`, resolved as `db.schema.fn(args)`. Rides the existing cache invalidation
  (`mssql_refresh_cache`). This is the Airport pattern and the bulk of real usage.
- **Bind callback** (table): extract the constant args → 1-row Arrow batch (via `arrow_produce`) →
  `bind_function(decl_id, args)` → zero-row stream → `PopulateReturnSchema` → `return_types`/`names`.
- **Execute callback**: marshal the arg chunk → Arrow (`arrow_produce`) → `execute_scalar`/`execute_table`
  → ingest the result (`arrow_ingest`; scalar = one column into the result vector, table = the scan loop).
- New core files `src/arrownet/arrow_functions.{hpp,cpp}` (or `src/include/arrownet/`) hold this — generic,
  reused by every provider.

**Table-in-out (Phase 4):** DuckDB's `in_out_function` + `OperatorResultType` (`NEED_INPUT` /
`HAVE_OUTPUT` / `FINISHED`). The ABI gets an `execute_inout` with an explicit framed protocol (push an
input chunk as Arrow, pull output batches, status enum) — replacing Airport's fragile magic-string
buffer signals. Designed when we get there.

## 8. Open decisions

1. **CLR-at-load vs everything-attach-bound** (§7) — the one lifecycle question. Recommendation: make
   discovered + provider functions **all catalog-bound** initially (keeps lazy load, simplest, covers the
   stored-proc use case); add load-time global functions only if a provider needs functions without an
   ATTACH.
2. **Function naming / namespacing** — global authored functions: `arrownet_*` or provider-prefixed
   (`mssql_*`)? Catalog functions are naturally `db.schema.fn`. 
3. **Attribute API surface** — vectorized-only, or also the row-convenience overload (§5b)?
4. **Decl encoding** — flat rows + per-decl schema streams now, vs `ArrowSerializer` nested rows once
   adopted (§4 note).
5. **Param passing** — named vs positional; how DuckDB named params (`fn(region := 'US')`) map to the
   proc's `@params`.

## 9. Build-on points (already in the repo)

`arrow_produce` (DataChunk→Arrow, for args), `arrow_ingest` / `PopulateReturnSchema` (Arrow→DuckDB, for
results + schema-from-zero-row-stream), `ArrowNetSchemaEntry` (hang catalog function entries here),
`ArrowDataReader` / `DbDataReaderArrowStream` (C# query→Arrow streaming for `Execute`), the
SqlClient→Arrow type mapping (reused by `Bind`/describe). The handle/`BackendRegistry` dispatch (once
multi-provider) routes each `execute_*` to the right backend automatically.
```
