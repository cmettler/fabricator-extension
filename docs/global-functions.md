# Load-time global functions (connection-free) — plan

> Status: **design / plan — nothing built.** The deferred **Phase 3-A**: connection-free functions registered
> at `Extension::Load` so a bare `fn(...)` works with **no ATTACH** (e.g. a template engine). The 4th member of
> the "provider declares; core stays name-agnostic" family (after settings v33 / ATTACH options v37 / secret
> fields v38). Today provider functions are all **attach-time catalog-bound** (`db.schema.fn`, dispatched via a
> catalog handle — 4e/4f/4g/4h). Global functions are the **orthogonal scope that coexists** with that.
> Motivating case: a **template engine scalar**. See the existing summary in [CLAUDE.md] and the related
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

| | Global SCALAR (build now) | Global TABLE (deferred) |
|---|---|---|
| Return shape | **fixed** return type (from the decl) | **arg-dependent** output schema (delta's columns come from `path`) |
| IO / opener | none (pure compute) | may need the **host-FS opener (ClientContext)** for IO |
| ABI reuse | reuses `get_function_*_schema` + `execute_scalar` (handle-less) | needs `table_bind`(args→schema) + an **opener arg** the SQL path lacks |
| Verdict | clean — the template engine motivates it | keep delta **bespoke**; build the generic table-global path when a 2nd lakehouse format/provider lands |

So this plan builds **global scalar functions**; global table functions stay the documented deferral (see
[docs/delta-catalog.md](delta-catalog.md) + the CLAUDE Phase-3-A note — the two wrinkles live there).

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

- **Interface** (`ArrowNet.Bridge`): a minimal `IArrowGlobalScalarFunction`:
  ```csharp
  public interface IArrowGlobalScalarFunction {
      string Name { get; }            // BARE function name (registered as-is; arrownet_-prefixed by convention)
      Schema Parameters { get; }      // arg fields (NullType sentinel = "any", reused from daxeval for STRUCT|JSON bags)
      Field Result { get; }           // fixed return type
      IArrowArray Invoke(RecordBatch args);   // vectorized over the arg batch (same shape as IArrowScalarFunction)
  }
  ```
  It is `IArrowScalarFunction` minus `SchemaName` (no catalog schema for a global). The execution body is
  identical, so a tiny adapter lets the same code back both scopes if desired.
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
- **params**: accept EITHER a DuckDB `STRUCT` (`{'name':'world','n':3}`, preferred — type-safe) OR a JSON string,
  via the **`NullType` sentinel → `LogicalType::ANY`** marker already used by `daxeval` (so `params` crosses
  uncast and `Invoke` reads its runtime type). Materialize each row's bag into a `Dictionary<string,object>` /
  `JsonElement` and drop it into the `TemplateContext` (dictionaries/JSON need no member allow-listing), render,
  emit the text.
- Vectorized: parse the template ONCE and **cache the `IFluidTemplate` keyed by the template string** (usually
  constant across a batch — a literal), render per row off the cached, thread-safe template.

**Composition with TMDL** (the original driver, [docs/dax-provider.md](dax-provider.md) "TMDL"): the global
scalar is the **"dynamically create a TMDL"** step — `arrownet_render(tmdl_template, params)` → a TMDL string —
which then feeds the **effectful apply** step (a table function / the collector `apply_tmdl`, never a
side-effecting scalar). Render = pure global scalar; apply = catalog/collector. Clean separation of the pure and
effectful halves, exactly as deliberated.

## Verification

- `test/verify_global_functions.test`: **no ATTACH** — `SELECT arrownet_render('Hi {{name}}', {'name':'x'})` →
  `Hi x`; vectorized over a `range()`; NULL handling; the JSON-string param form; and that it resolves on a bare
  loaded extension (no catalog). Plus a collision test if two providers declare the same global name.
- Build: VS18 vcvars `--target unittest shell`; `publish-managed.ps1`. ABI bumped → rebuild **both** from one
  commit (exact-match ABI).

## Recommendation (sequenced)

1. **Global scalar functions** (this plan) — `list_global_functions` ABI + handle-0 reuse + `IArrowGlobalScalarFunction`
   / `IBackend.GlobalScalarFunctions` + `RegisterArrowNetGlobalFunctions` at load + the `arrownet_render` demo.
   Small, motivated, and unblocks the TMDL render step.
2. **Global table functions** — deferred; the arg-dependent-schema + host-FS-opener wrinkles (keep delta bespoke
   until a 2nd lakehouse format lands; then give the global table-fn bind/execute path an opener arg). See
   [docs/delta-catalog.md](delta-catalog.md) + the CLAUDE Phase-3-A note.

**Net:** global scalar functions are a contained, ~1-ABI-entry addition that reuses the existing scalar
authoring/execution wholesale, gives connection-free functions (template engine first), and composes cleanly
with the deferred TMDL apply path — while leaving the genuinely harder global *table* case documented for later.
