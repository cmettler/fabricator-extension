# Load-time global functions (connection-free) — plan

> Status: **BUILT + verified for ALL FIVE kinds** — global **scalar** (ABI v46 `list_global_functions` +
> handle-0 dispatch; `arrownet_render`, Fluid/Liquid), **in-out + collector** (`arrownet_tag` +
> `arrownet_collect_sum`, handle-0 `inout_bind`), **table** (`arrownet_seq` fixed + `arrownet_columns`
> ARG-DEPENDENT schema, handle-0 `table_bind` / v29 session), AND **aggregate** (`arrownet_product`, handle-0
> `agg_open`; GROUP BY / parallel / OVER all work). All resolve as a bare `fn(...)` with NO ATTACH —
> `test/verify_global_functions.test` (63). **The host-FS table sub-case is now DONE too (ABI v47)** — a global
> table reader that does secret-backed IO through DuckDB's FileSystem (lakehouse readers like Delta). It needed
> the calling operator's opener (ClientContext) threaded to the C# binding, solved by **one appended ABI entry
> `set_active_opener`** (a per-thread ambient mirroring `set_active_txn`) set in the shared table bind/init hooks
> and read by the binding — reusing the v29 table session verbatim, no new operator. **`arrownet_delta_scan` is
> now a pure-C# global host-FS `ITableFunction`** (the bespoke `arrownet_delta.cpp` + the `delta_schema`/
> `delta_scan` ABI entries were removed) — proof a new lakehouse format (Iceberg/Lance/…) is added with **zero
> C++**. `test/verify_delta.test` (39). See §"Host-FS global table functions (DONE)" below.
> The **Phase 3-A**: connection-free functions registered
> at `Extension::Load` so a bare `fn(...)` works with **no ATTACH** (e.g. a template engine). The 4th member of
> the "provider declares; core stays name-agnostic" family (after settings v33 / ATTACH options v37 / secret
> fields v38). Today provider functions are all **attach-time catalog-bound** (`db.schema.fn`, dispatched via a
> catalog handle — 4e/4f/4g/4h). Global functions are the **orthogonal scope that coexists** with that.
> Motivating case: a **template engine scalar**. This plan covers **all four kinds** — scalar, table, in-out,
> collector — through **one mechanism** (`list_global_functions` + a handle-0 `*_bind` marker reusing the
> existing scalar / v29 table-session / v28 exchange-collector ABIs). Only **one sub-case is deferred**: a global
> *table* fn that needs the **host-FS opener** (secret-backed lakehouse readers like delta) — it wants an opener
> arg on `table_bind`; delta stays bespoke until a 2nd such reader lands. See the summary in CLAUDE.md +
> [docs/provider-extensibility.md](provider-extensibility.md), [docs/delta-catalog.md](delta-catalog.md).

## Why / the defining property

A global function is **connection-free and ATTACH-free**: `SELECT arrownet_render('Hello {{name}}', {'name':'x'})`
works on a bare DuckDB with the extension loaded — no `ATTACH … (TYPE arrownet)`, no SQL Server / DAX
connection. That is exactly right for:
- a **template engine** (`arrownet_render(template, params)` → text) — pure compute, no backend;
- future connection-free readers (`arrownet_iceberg_scan(path)`, lakehouse readers) — they belong to no
  catalog (`arrownet_delta_scan` is **already** a global function — bespoke `RegisterDeltaScan`, proof the scope
  exists).

The earlier objection (don't boot the CLR at load) **has dissolved**: the settings refactor (v33) + the fs/delta
spike already boot the bridge **best-effort at extension load**. So load-time registration is now free to use.

## Scope split — scalar NOW, table LATER

All four kinds are *registrable* at load by the **same mechanism** (`list_global_functions` enumerates them; the
per-call `*_bind` ABIs resolve schema/binding with a **handle-0 marker**). What differs is only whether a kind
needs the **host-FS opener** for IO — which is the single axis that splits "build now" from "needs an opener arg":

| Kind | Output schema | Host IO / opener | ABI reuse (all handle-0) | When |
|---|---|---|---|---|
| **scalar** | fixed (decl) | none | `get_function_*_schema` + `execute_scalar` | **slice 1** — template engine |
| **in-out / collector** (pure-C#) | from cost args + input schema, via `inout_bind` | none (transforms its input) | `inout_bind` + `inout_exchange_open` + `inout_bind_close` (v28) | **slice 2** — clean, no opener |
| **table** (compute / connstr) | arg-dependent, via `table_bind` | none | `table_bind` + `table_execute` + `table_close` (v29) | **slice 3** — clean |
| **table** (host-FS reader, e.g. delta) | arg-dependent, via `table_bind` | **needs the host-FS opener (ClientContext + secrets)** | as above **+ an opener arg on `table_bind`** | **deferred** — delta stays bespoke until then |

**Key point: global table + in-out add ZERO new ABI entries beyond the scalar plan.** The arg-dependent output
schema (wrinkle 1) is *already solved* by the v27/v29 `table_bind`(args→schema) and v28 `inout_bind`(args+input
schema→schema) sessions — we just call them with the handle-0 marker. Only the *host-FS-opener* split (wrinkle
2) remains, and it bites **one** sub-case (secret-backed FS readers like delta), not the others.

Sections below cover scalar in full, then the table + in-out specifics.

## ABI — one new entry + a handle-less branch (minimal)

Global functions reuse the **entire** scalar machinery (authoring, schema fetch, execution) — the only new
thing is *enumerating* them at load. The catalog scalar path is `FetchFunctionParamSchema` /
`FetchFunctionReturnType` (→ `get_function_param_schema` / `get_function_return_schema`) + `execute_scalar`,
all keyed by a catalog **handle**. Global functions have **no handle** → route on a **null/0 handle marker**.

- **ADD** one vtable entry (bump `ARROWNET_ABI_VERSION` + `vtable->AbiVersion`):
  ```c
  // List the provider-union of connection-free global functions (no handle — load-time). Metadata rows:
  // {name VARCHAR, kind VARCHAR, param_count INT, return_type VARCHAR}, same shape as the catalog functions
  // metadata. C++ then fetches each function's precise param/return Arrow schema via the existing
  // get_function_*_schema entries with handle = 0.
  int32_t (*list_global_functions)(struct ArrowArrayStream *out, char **err);
  ```
- **REUSE** `get_function_param_schema(handle, schema, func, out)` / `get_function_return_schema(...)` /
  `execute_scalar(handle, schema, func, args, out)` **with `handle == 0`** → C# routes to the global registry
  by `func` name instead of `Handles.Resolve<IBackendCatalog>(handle)`. No new execution/schema entries.

Net ABI cost: **+1 entry**, plus a `handle == 0` branch in three existing C# handlers. (A dedicated
`execute_global_scalar(name, args, out)` is the alternative; the handle-0 reuse is leaner and the CLAUDE note
already anticipated it — "the existing `execute_scalar` with a handle-less marker would reuse this path".)

## C# authoring (provider declares; Bridge unions)

- **Interface (extract a shared base, don't duplicate)** (`ArrowNet.Bridge`): `SchemaName` is the *only*
  catalog-specific member, so factor it out — a global scalar is just a scalar without a catalog binding:
  ```csharp
  public interface IScalarFunction {                   // scope-independent: pure compute
      string Name { get; }            // BARE name (registered as-is; arrownet_-prefixed by convention)
      Schema Parameters { get; }      // arg fields (NullType sentinel = "any", reused from daxeval for STRUCT|JSON)
      Field Result { get; }           // fixed return type
      IArrowArray Invoke(RecordBatch args);            // vectorized over the arg batch
  }
  public interface ICatalogScalarFunction : IScalarFunction {  // RENAME of IArrowScalarFunction
      string SchemaName { get; }      // the only catalog-specific bit (db.schema.fn resolution)
  }
  ```
  Globals declare `IBackend.GlobalScalarFunctions : IReadOnlyList<IScalarFunction>` directly — **no separate
  global marker interface**, the base *is* the global contract. The `Invoke` body + the schema/execute dispatch
  (`GetFunctionParamSchema`/`GetFunctionReturnSchema`/`ExecuteScalar`) all operate on `IScalarFunction`, so they
  don't care whether resolution was catalog (by `schema.name` via the handle) or global (by `name` via the
  registry). This **rename is behavior-preserving** (Liskov-clean: every catalog scalar *is* a scalar) and is
  done as the first slice-1 step — it touches `IArrowScalarFunction.cs` + its implementors (the `Cf*` scalar
  demos, `SqlServerScalarFunction`, `SqlServerBackend`'s `CustomScalar`/`ResolveScalar`/the three scalar
  handlers); DAX has no scalar functions so it's untouched. Gate: `verify_scalar_functions` /
  `verify_custom_functions` stay green. (The same base/derived split would extend to `IArrowTableFunction`/
  in-out when global *table* functions eventually land — YAGNI until then.)
- **Declaration**: `IBackend.GlobalScalarFunctions` (new property, default empty) — like `IBackend.Settings` /
  `SecretFields`. Each provider contributes its globals; the **Bridge `BackendRegistry` unions them** (plus an
  optional provider-agnostic *core* set) into a `GlobalScalarRegistry` keyed by name (case-insensitive).
  **Name collisions across providers → a clear error at load** (or first-registered-wins; pick one).
- **Handlers**: `list_global_functions` emits the union as the metadata rows;
  `get_function_param_schema`/`get_function_return_schema`/`execute_scalar` gain a `handle == 0` →
  `GlobalScalarRegistry[func]` branch (return `Parameters` / `Result` / run `Invoke`).

## C++ load-time registration (mirrors the catalog scalar build)

A new `RegisterArrowNetGlobalFunctions(ExtensionLoader &loader)` called from `Extension::Load` (after the bridge
is booted best-effort, as settings already are):
1. `list_global_functions()` → the decl rows.
2. For each `kind=='scalar'`: `FetchFunctionParamSchema(0, "", name)` + `FetchFunctionReturnType(0, "", name)`
   (handle 0) → arg/return `LogicalType`s.
3. Build a `ScalarFunction(name, arg_types, return_type, callback)` whose **capturing-lambda** callback marshals
   the arg `DataChunk` → Arrow, calls `execute_scalar(0, "", name, args, out)`, ingests the single result column
   — **the same callback `GetOrCreateScalarFunction` already uses**, just with `handle = 0`. Register VOLATILE +
   SPECIAL_HANDLING (sees NULL args), like the catalog scalar UDFs.
4. `loader.RegisterFunction(fn)` — global registration is only legal during `Extension::Load`, which is why
   this forces the bridge to boot at load (already does).

If the bridge can't boot (no managed dir), global functions simply aren't registered — graceful, same as
settings. **Zero per-function C++** — adding a global scalar is a pure-C# change (declare it; rebuild only the
managed bridge unless the ABI bumped).

## Global table + in-out functions (the other kinds)

Same registration spine, same base/derived interface split, same handle-0 reuse — only the per-kind ABI and the
opener question differ.

### Interface hierarchy (mirror the scalar split)

Extract a schema-free base per kind; the existing `IArrow*` interface becomes the catalog-bound derived type:

```csharp
public interface ITableFunction {                               // global table fn
    string Name { get; } Schema Parameters { get; }
    IArrowTableFunctionBinding Bind(RecordBatch args);          // arg-dependent OutputSchema + Execute
}
public interface ICatalogTableFunction : ITableFunction { string SchemaName { get; } }   // was IArrowTableFunction

public interface IInOutFunction {                               // global in-out (streaming exchange)
    string Name { get; } Schema InputSchema { get; }
    IArrowInOutBinding Bind(RecordBatch? args, Schema inputSchema);
}
public interface ICatalogInOutFunction : IInOutFunction { string SchemaName { get; } }   // was IArrowInOutFunction

public interface ICollectorTableFunction {                      // global collector (pipeline breaker)
    string Name { get; } Schema InputSchema { get; }
    IArrowCollectorBinding Bind(RecordBatch? args, Schema inputSchema);
}
public interface ICatalogCollectorTableFunction : ICollectorTableFunction { string SchemaName { get; } }  // was IArrowCollectorTableFunction
```

Globals declare `IBackend.GlobalTableFunctions` / `GlobalInOutFunctions` / `GlobalCollectorFunctions`
(`IReadOnlyList<ITableFunction>` etc., default empty), unioned by `BackendRegistry` into per-kind global
registries keyed by name. The `Binding` types (`IArrowTableFunctionBinding`, `IArrowInOutBinding`,
`IArrowCollectorBinding`) are **unchanged** — they already carry no schema-name. Behavior-preserving renames,
same gate (the function `verify_*` suites green).

### Registration at load + handle-0 reuse (no new ABI)

`list_global_functions` already emits a `kind` per row; C++ at load branches on it (exactly as the catalog
discovery does), all dispatching with the **handle-0 marker** at *bind* time:

- **table** → build a `TableFunction(name, arg_types, ArrowStreamScan, GlobalTableBind, …)` where `arg_types` =
  `get_function_param_schema(0,"",name)` (positional) and `GlobalTableBind` marshals `input.inputs` →
  `table_bind(0,"",name,args,&binding)` → output schema + a **concrete binding handle**; the scan factory calls
  `table_execute(binding,spec,filter,out)`; teardown `table_close(binding)`. Reuses the v29 session + the
  existing `arrow_ingest` scan verbatim.
- **in-out** → register the `{LogicalType::TABLE}` **exchange** operator (the same `ArrowNetExchange*` used by
  `_each`/custom in-out) under the bare name; its bind marshals the input schema + cost named-params →
  `inout_bind(0,"",name,args,input_schema,&out_schema,&binding)`; `inout_exchange_open(binding,…)` runs it.
- **collector** → register the `{TABLE}` **collector** (Sink+Source) operator under the bare name; bind →
  `inout_bind(0,…)` (collectors reuse the inout_bind/exchange ABI); the operator streams the output as built.

In every case only the **bind** entry takes the handle-0 marker (→ C# routes to the global registry by name);
the binding handle it returns is a real, resolvable handle, so `table_execute`/`table_close` /
`inout_exchange_open`/`inout_bind_close` are unchanged. **So global table + in-out cost zero new vtable entries**
— just extend the handle-0 branch (added for scalar) to `table_bind` and `inout_bind`, and have C++
`RegisterArrowNetGlobalFunctions` branch on `kind` to register the right operator.

### Host-FS global table functions (DONE, ABI v47)

A global table reader doing secret-backed IO through DuckDB's FileSystem (lakehouse readers — Delta today,
Iceberg/Lance next). The mechanism, built on the existing `kind='table'` global path:

- **The opener** (the calling operator's `ClientContext`, which resolves DuckDB secrets for `az://`/`s3://`/…)
  is not an argument of the generic `table_bind`/`table_execute`. So — exactly like `set_active_txn` for the
  transaction id — the host records it in a **per-thread ambient** via one appended ABI entry
  `set_active_opener(opener)`, set in the two shared arrow-scan hooks (`PopulateReturnSchema` at bind,
  `ArrowStreamInitGlobal` at execute) right next to the existing `set_active_txn` call. The managed
  `AmbientOpener.Current` (ThreadStatic) holds it; a SQL/compute binding never reads it.
- **Authoring = a plain global `ITableFunction`.** No new interface: the host-FS reader's `Bind(args)` reads
  `AmbientOpener.Current` to resolve its schema (e.g. open the Delta log) and `Execute(scan)` reads it to read
  the data through `DuckDbTableFileSystem` (the host `fs_*` callbacks). Because the opener is valid only for the
  synchronous call, `Execute` **materializes** the result while it's valid (then streams the in-memory batches)
  — exactly what the bespoke delta reader did at init_global. Declared in `IBackend.GlobalTableFunctions`.
- **Delta is the reference impl**: `DeltaGlobalTableFunction` (Bridge, over engineered-wood + `DuckDbTableFileSystem`)
  registered via `CustomFunctions.GlobalTable`. The bespoke `arrownet_delta.cpp` + the `delta_schema`/`delta_scan`
  ABI entries were removed; `arrownet_delta_scan(path)` now resolves as a global, enumerated by
  `list_global_functions` (`kind='table'`) and dispatched through the v29 table session. `test/verify_delta.test`.
- **A future streaming refinement** (not done): the opener (ClientContext) actually lives for the whole
  table-function execution, so a host-FS reader *could* stream (capture the opener, pull lazily) instead of
  materializing — and forward the scan's filter/projection spec into engineered-wood's file/row-group skipping.
  See docs/filesystem-bridge.md "Next".

### The opener wrinkle — where it bit, and how it was resolved (historical)

`table_bind`/`table_execute` (and `inout_bind`) pass a **handle** to C#; a catalog fn uses that handle's
`SqlConnection`. A global fn has handle 0 — fine for:
- **pure-compute** table fns (generators, transforms) and **all pure-C# in-out/collector** globals (they
  transform their *input table*, no external IO) — **no opener needed**;
- **connstr-style** table fns that take the connection target as an *argument* (handle-0 + args is enough).

It bites exactly one sub-case: a **host-FS reader** (delta/iceberg/parquet over `az://`/`s3://` with **DuckDB
secrets**), which needs the host `FileSystem` opened against a `ClientContext` for secret resolution — and the
v29 `table_bind`/`table_execute` path doesn't thread a `ClientContext`/opener to C#. The filesystem bridge
(`ArrowNetHostServices` fs callbacks, v40/v41) gives C# host IO, but secret-backed opens need the right context.
**Resolved (ABI v47):** rather than an opener *param* on `table_bind`/`table_execute` (which would touch every
catalog/proc/custom callsite + churn `IArrowTableFunctionBinding.Execute`), the opener is threaded as a
**per-thread ambient** — one appended entry `set_active_opener(opener)` set in the shared `PopulateReturnSchema`
+ `ArrowStreamInitGlobal` hooks (beside `set_active_txn`), read by the host-FS binding from
`AmbientOpener.Current`. SQL fns ignore it; the v29 session is otherwise untouched. Delta migrated onto it
(bespoke `arrownet_delta.cpp` + `delta_schema`/`delta_scan` removed). See the "Host-FS global table functions
(DONE)" section above for the built shape.

### Effectful global table/in-out (the apply half)

Side effects belong in **table / in-out / collector / aggregate-finalize**, never scalars (optimizer purity).
So the effectful "apply" steps can be **global** too — e.g. a global `arrownet_apply_tmdl(<fragments>)`
**collector** (collect fragments → one atomic apply at Finalize, run once single-threaded) whose target is
addressed by its args (a connstr/endpoint), composing with the global `arrownet_render` scalar: render (pure
global scalar) → apply (effectful global collector), **both connection-free / no ATTACH**. A target that's
inherently a live model/connection is more naturally catalog-bound; a target addressable by an arg works global.

## The template-engine demo (the motivator)

A provider-agnostic core global, e.g. `arrownet_render(template VARCHAR, params <any>) → VARCHAR`:
- **Engine**: **Fluid** (`github.com/sebastienros/fluid`) — a pure-managed (.NET, MIT, on Parlot) **Liquid**
  template engine; published transitively like `Azure.Identity` / engineered-wood. Chosen over Scriban for this
  use because (a) **secure-by-default** — Liquid + opt-in `MemberAccessStrategy`, no arbitrary .NET eval, so a
  *user-supplied* template (a SQL literal/column) can't reach arbitrary object graphs; (b) **Liquid** is a
  widely-known syntax; (c) **fast + low-alloc** with a clean **parse-once / render-many** split
  (`FluidParser.TryParse` → a thread-safe `IFluidTemplate`), ideal for vectorized rendering; (d) native
  dictionary / `System.Text.Json` model binding, which is exactly how we hand it `params`. (Scriban stays a fine
  alternative if a richer scripting language is later wanted for heavy TMDL generation.)
- **params (DONE — STRUCT or JSON)**: accepts EITHER a DuckDB `STRUCT` (`{'name':'world','n':3}`, preferred —
  type-safe, no quoting) OR a JSON string, via the **`NullType` sentinel → `LogicalType::ANY`** marker (now
  wired for SCALARS, not just the daxeval table/proc path). `CfRenderFunction.Parameters` declares `params` as
  `NullType`; the C++ scalar builder maps `SQLNULL → ANY` for the signature AND marshals the exec chunk using
  its **runtime** column types (`DataChunk::GetTypes()`, not the declared signature) so a STRUCT/VARCHAR passed
  for an ANY param appends correctly; `Invoke` reads column 1's runtime type — a `StructArray` (each field → a
  template var via `ArrowValueReader.ReadScalar`) or a `StringArray` (JSON). `test/verify_global_functions.test`
  covers both forms incl. per-row struct extraction.
- Vectorized: parse the template ONCE and **cache the `IFluidTemplate` keyed by the template string** (usually
  constant across a batch — a literal), render per row off the cached, thread-safe template.

**Composition with TMDL** (the original driver, [docs/dax-provider.md](dax-provider.md) "TMDL"): the global
scalar is the **"dynamically create a TMDL"** step — `arrownet_render(tmdl_template, params)` → a TMDL string —
which then feeds the **effectful apply** step (a table function / the collector `apply_tmdl`, never a
side-effecting scalar). Render = pure global scalar; apply = catalog/collector. Clean separation of the pure and
effectful halves, exactly as deliberated.

## Verification

- `test/verify_global_functions.test`: **no ATTACH** throughout. Scalar: `SELECT arrownet_render('Hi {{name}}',
  {'name':'x'})` → `Hi x`; vectorized over `range()`; NULL handling; the JSON-string param form; resolves on a
  bare loaded extension (no catalog); a collision test if two providers declare the same global name. Later
  slices add: a global **table** fn (`SELECT * FROM arrownet_gen(3)`) proving arg-dependent output schema via
  `table_bind` with no catalog; a global **in-out** (`SELECT * FROM arrownet_xform((SELECT …))`) and **collector**
  proving the exchange/Sink+Source operators run handle-0 with no ATTACH.
- Build: VS18 vcvars `--target unittest shell`; `publish-managed.ps1`. ABI bumped → rebuild **both** from one
  commit (exact-match ABI).

## Recommendation (sequenced)

The **single new ABI entry (`list_global_functions`) + the `RegisterArrowNetGlobalFunctions` load hook** are
built once in slice 1; each later slice just extends the handle-0 branch to one more `*_bind` and adds the
`kind` case in the load registrar — no further ABI.

1. **Global scalar — DONE** (ABI v46): the `IScalarFunction`/`ICatalogScalarFunction` rename, `list_global_functions`
   + handle-0 reuse of `get_function_*_schema`/`execute_scalar`, `IBackend.GlobalScalarFunctions` (unioned by
   `GlobalFunctions`), `RegisterArrowNetGlobalFunctions` at load (shared `BuildArrowNetScalarFunction`), the
   `arrownet_render` (Fluid/Liquid) demo. `test/verify_global_functions.test`. Unblocks the TMDL render step.
2. **Global in-out + collector (pure-C#) — DONE**: the `IInOutFunction`/`ICollectorTableFunction` base renames;
   the handle-0 branch on `inout_bind` resolves against the C# global registry (`GlobalFunctions.ResolveInOut`,
   a collector wrapped as `CollectorInOutBinding`); `RegisterArrowNetGlobalFunctions` registers the
   exchange/collector operators by `kind` at load (handle 0). **No opener** (they transform their input). Demos
   `arrownet_tag` (streaming) + `arrownet_collect_sum` (collector); `test/verify_global_functions.test`. Enables
   the effectful global *apply* half (e.g. an `arrownet_apply_tmdl` collector).
3. **Global table (compute / connstr) — DONE**: the `ITableFunction` base rename; `table_bind` handle-0 →
   `GlobalFunctions.ResolveTable` (wraps the arg-dependent binding in the now-Bridge `BindingBoundTable`);
   `RegisterArrowNetGlobalFunctions` registers `kind='table'` on the v29 session at load (handle 0, projection +
   best-effort filter pushdown). The handle-0 `get_function_param_schema` became kind-agnostic
   (`GlobalFunctions.ParamSchema`). Demos `arrownet_seq` (fixed schema) + `arrownet_columns` (ARG-DEPENDENT
   schema). No opener. `test/verify_global_functions.test`.
4. **Global aggregate (UDAF) — DONE**: the `IAggregateFunction` base rename (`IArrowAggregateFunction` →
   `ICatalogAggregateFunction`); `AggSessionImpl` moved to the Bridge as the public `AggregateSession` (shared by
   catalog + global); `agg_open` handle-0 → `GlobalFunctions.ResolveAggregate`; the handle-0
   `get_function_param_schema`/`get_function_return_schema` cover the aggregate kind (`ParamSchema`/`ReturnField`);
   `RegisterArrowNetGlobalFunctions` registers `kind='aggregate'`/`'aggregate_spill'` via a shared
   `BuildArrowNetAggregateFunction` at load. Reuses the v25/v26 `agg_*` ABI (no bump). Demo `arrownet_product`;
   GROUP BY / parallel / OVER verified. `test/verify_global_functions.test`.
5. **Global table (host-FS reader) — DONE (ABI v47)**: a global table reader that does secret-backed IO through
   DuckDB's FileSystem. The opener (the operator's `ClientContext`, carrying secret resolution) is threaded to
   the C# binding via **one appended ABI entry `set_active_opener`** — a per-thread ambient (`AmbientOpener`,
   mirroring `set_active_txn`) the host sets in the shared table bind/init hooks (`PopulateReturnSchema` +
   `ArrowStreamInitGlobal`), read by the host-FS binding in `Bind` (schema) + `Execute` (data; materialized
   while the opener is valid). NO opener *param* on `table_bind`/`table_execute` and NO new operator — the v29
   table session is reused verbatim. `arrownet_delta_scan` migrated to a pure-C# global host-FS `ITableFunction`
   (`DeltaGlobalTableFunction`, declared in `CustomFunctions.GlobalTable`); the bespoke `arrownet_delta.cpp` +
   the `delta_schema`/`delta_scan` ABI were removed. So a new lakehouse format (Iceberg/Lance/…) = a pure-C#
   `ITableFunction` whose `Bind`/`Execute` read `AmbientOpener.Current` + read files via `DuckDbTableFileSystem`,
   declared as a global — **zero C++**. `test/verify_delta.test` (39). See the §below + docs/filesystem-bridge.md.

**Net:** all FIVE global kinds (scalar / in-out / collector / table / aggregate) register through one mechanism
(`list_global_functions` + the handle-0 `*_bind`/`*_open` marker), reusing the scalar / v29 table-session /
v28 exchange-collector / v25-v26 aggregate machinery wholesale — **zero ABI beyond the v46 scalar entry**. The
only genuinely deferred piece is the *host-FS-opener* sub-case of global table (secret-backed lakehouse readers
like delta), which gets an opener arg when a 2nd such reader lands.
