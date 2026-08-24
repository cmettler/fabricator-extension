# ABI history — prior versions v16–v81

> Moved VERBATIM out of `CLAUDE.md` on 2026-07-29 (conservative split — see the commit message).
> Append-only as-built history; the live summary + pointers stay in `CLAUDE.md`.
> Paths/links inside are REPO-ROOT-relative (the text was written for `CLAUDE.md`).
> The CURRENT version + the bump rule stay in `CLAUDE.md` §C ABI contract.
>
> ⚠ **v72 and v73 are NOT in this file** — they are the catalog/table abstraction's own slices, recorded
> where their design is, in [catalog-table-abstraction.md](catalog-table-abstraction.md) §5 item 4d
> (v72: `get_metadata` + `scan_table` + the kind enum deleted, replaced by five typed `catalog_*` discovery
> entries and the `table_*` session; v73: `table_info`/`table_stats` re-carried as ONE typed JSON doc each,
> parsed with our own vcpkg yyjson, retiring the `ReadCapabilityFlag` string-find). v74 below is the
> follow-on that finished the same job for ALTER.

## v81 (2026-08-24) — `tablefn_execute` reports whether the execution changed the CATALOG

**Signature change on ONE existing entry** (so it needs the bump even though nothing was added or removed —
the vtable is positional, but a caller compiled against v80 would pass `err` where the flag now sits):

```c
int32_t (*tablefn_execute)(FabricatorHandle binding, const char *spec_json,
                         struct ArrowArrayStream *filter_values, struct ArrowArrayStream *out,
                         int32_t *schema_may_change, char **err);   //  <- new out-param
```

**WHY.** A provider-authored table function that performs DDL had no way to say so, and the consequence was
not cosmetic. MEASURED: with no ATTACH object filter, a name missing from the catalog's discovered list is
treated as GENUINELY ABSENT (`FabricatorSchemaEntry`'s lookup gate short-circuits before any by-name fetch),
so a table or function such a call created is **unreachable for the rest of the session** — not merely absent
from `duckdb_tables()`. `db.cdc.enable('dbo.o')` then `SELECT * FROM db.cdc.dbo_o_CT` gave
`Catalog Error: Table with name dbo_o_CT does not exist!` in the same session, with no by-name fallback to
save it.

The mechanism that already existed serves `fabricator_exec` ALONE: `execute_dml` has carried a
`schema_may_change` out-param (set in C# by `SqlDdl.MayChangeSchema`, acted on in
`fabricator_extension.cpp`) since long before this. So this is the same flag on the other execution path,
which makes the two paths consistent rather than adding a special case.

**⚠⚠ THE ORDERING IS THE CONTRACT, AND IT IS THE ONLY SUBTLE THING HERE.** The host reads the flag when
`tablefn_execute` RETURNS — before a single row is pulled — because the managed side reads
`IBoundTableFunction.SchemaMayChange` immediately after `bound.Execute(...)` and before exporting the stream.
A binding whose side effect lives in an async-iterator body has **not run it yet** at that moment: an
iterator does not begin until the first batch PULL, a different ABI crossing, on whatever thread DuckDB pulls
from. So a function reporting through this flag MUST do its work in the EAGER part of `Execute()`.

⚠ That failure is SILENT and it is worth knowing what it looks like: the DDL still happens and succeeds, the
function's own report row is correct, and only the cache rebuild is lost. MUTATION-TESTED — moving the work
into the iterator kills `verify_mssql_cdc` at exactly the same assertion as never setting the flag at all
(§11, `Catalog Error: Table with name dbo_cdc_setup_CT does not exist!`, after 78 assertions pass).

**⚠⚠ THE HOST MUST NOT ACT ON IT WHERE IT IS SET, and this is the design decision that took the longest to
reach.** The flag is raised during a SCAN. `FabricatorCatalog::RefreshCache` takes `schema_lock_` and calls
`ClearTables()` on every schema — which would RETIRE the very entry the running statement is scanning. The
graveyard (`retired_entries_`) makes that non-fatal rather than a use-after-free, but it is needless risk plus
a burst of discovery IO in the middle of someone's query. So:

- the scan factory records it: `FabricatorCatalog::MarkSchemaMayChange()` sets an `std::atomic<bool>`;
- `FabricatorTransactionManager::StartTransaction` consumes it (`ConsumeSchemaMayChange()`, an atomic
  exchange) and refreshes there.

**Why that point is provably safe, established from DuckDB's source rather than assumed:**
`FabricatorCatalog::LookupSchema` receives an ALREADY-RESOLVED `CatalogTransaction`, so DuckDB resolves the
transaction — triggering `StartTransaction` — **before** `schema_lock_` is taken. The path that fires the
refresh therefore holds no catalog lock of ours, and it is outside every bind and scan.

⚠ The refresh is deliberately best-effort (`try`/`catch(...)`): a failed re-discovery, because the server went
away between the DDL and now, must not abort an unrelated statement. The pre-existing stale cache is the same
state the caller would have had without the flag, and `fabricator_refresh_cache()` stays the explicit retry.

⚠ It also runs BEFORE the transaction is registered, because `RefreshCache` calls `FabricatorSetActiveTxn`
with the CALLER's context — the ambient it needs is not one this manager has not finished creating.

**A KNOWN GAP, stated rather than discovered later:** the refresh lands at the next transaction START, so
inside ONE explicit transaction a second statement does not see objects an earlier `cdc.enable` in that same
transaction created. Autocommit — every statement its own transaction — is unaffected, which is every
scheduler and dbt shape. The gap is narrow and the motivating case is unmotivated: `sp_cdc_enable_table` is
not transactional in any useful sense, and the change table it creates is empty at that instant anyway.
`fabricator_refresh_cache()` is the escape.

**Plumbing:**

| side | what |
|---|---|
| C++ | `TableFnExecute(..., bool *schema_may_change = nullptr)` — **SET-ONLY**, so several executions sharing one accumulator (a prepared statement re-executed, or one plan with several scans) cannot have an earlier `true` erased by a later `false` |
| C++ | `FabricatorTableFunctionInfo::catalog` carries the `FabricatorCatalog *`. Null for a GLOBAL function, which belongs to no catalog and has nothing to invalidate. ⚠ Set at exactly ONE registration site (`GetOrCreateTableFunction`) because that is the only path whose scans go through `tablefn_execute` — sqlgen uses `bind_replace`, in-out uses the exchange |
| C# | `ITableFunctionBinding.SchemaMayChange` (author-facing) and `IBoundTableFunction.SchemaMayChange` (ABI-facing), both DIM `=> false`, forwarded by `BindingBoundTableFunction`. An ordinary reader implements nothing |

⚠ Capturing the catalog by POINTER in the scan factory is safe for the same reason capturing `handle` there
already was: both are DATABASE-scoped and outlive every plan that can reference them. It is deliberately NOT
a `ClientContext`, which is this tree's recorded dangling-pointer class (a catalog outlives the connection
that attached it — the `table_stats` SIGSEGV).

**First consumer:** the four `db.cdc.*` setup functions (`enable_database` / `enable` / `disable`), which is
also the only coverage of this entry in either tier. ⚠ `cdc.capture_now()` (named `cdc.scan()` when this entry
was written — renamed 2026-08-24, docs/mssql-cdc.md §14.5) deliberately reports **false**: a log scan moves
data into existing change tables and creates nothing, so rebuilding after it would be pure waste on the one
function here most likely to be called in a loop.

**Gates:** `verify_mssql_cdc` 73 → **105**, two mutants (never set the flag; move the work into the iterator)
both killed at §11. Behaviour-neutral for everything else: the whole hermetic tier and every other service
suite unchanged.

## v80 (2026-08-23) — the `scalarfn_*` session: a scalar's return type resolved at BIND

**BREAKING** (`execute_scalar` REMOVED, three entries in its place — a mid-struct change, so every later slot
shifts; the version bump is what makes a mismatched loadable/bridge pair loud at boot instead of calling the
next entry through the wrong signature):

```c
int32_t (*scalarfn_bind)(handle, schema, func, args, arg_constant, out_schema, out_binding, err);
int32_t (*scalarfn_execute)(binding, args, out, err);
int32_t (*scalarfn_close)(binding, err);
```

**WHY.** A scalar function's return type was fixed at REGISTRATION and nowhere else: the host read
`get_function_return_schema(handle, schema, func)` — keyed on the NAME alone, no call site, no argument types —
memoized it on the catalog entry for that entry's LIFE, and passed no bind callback at all, so DuckDB's
per-call-site bind hook went unused. A provider could therefore not express "my result type depends on this
call's arguments", and had nowhere to park work done once per call site instead of once per chunk. Table
functions had had both since v27 (`tablefn_bind` → binding → `tablefn_execute`); this is the same shape for
scalars, and the user's framing was exactly that: *"scalars should support the bind callback like the table
functions"*.

**DuckDB fully supports it and always did** — `FunctionBinder::BindScalarFunction` takes the function BY VALUE
(`function_binder.cpp:645`), hands that per-call-site copy to the bind callback as a non-const reference, and
re-reads `bound_function.GetReturnType()` AFTER the callback returns (`:678`). `getvariable` is the upstream
precedent for the exact shape we now use: registered `LogicalType::ANY`, bind folds a constant argument and
calls `SetReturnType(value.type())`.

**THE OPTIONAL DECLARED TYPE, and why it is not just ergonomics.** `IScalarFunction.Result` became `Field?`.
Declared ⇒ the catalog entry registers that type (so `duckdb_functions()` and overload displays stay
accurate); absent ⇒ an Arrow `null`-typed field crosses as the UNRESOLVED SENTINEL, the entry registers as
`ANY`, and the bind must supply a type or the call is refused BY NAME at bind (an unresolved `ANY` flowing
onward gets no further validation — `CheckTemplateTypesResolved` guards only `TEMPLATE` — and would fail far
from its cause).

**⚠ THE SENTINEL IS READ IN BOTH DIRECTIONS, and that is what keeps the common case free.** A binding may
report `Result = null`, which means *"my result IS the declared type"* — the host already holds it, so it
simply leaves the registered return type alone. Without that, `StaticScalarBinding` would have had to read
the definition's `Result` at every call site, and for a discovered SQL Server UDF **that property is a round
trip to `INFORMATION_SCHEMA`** — turning a fixed-return function into one metadata query per call site to
re-learn something nobody had forgotten. Measured from the source, not guessed: `FunctionParameters` opens a
connection and queries every call, and it is not cached.

**⚠ UNLIKE `tablefn_bind`, THE ARGUMENT VALUES ARE PARTIAL — the one place the two sessions genuinely
differ.** A table function's arguments MUST be constant, and DuckDB pre-evaluates them into
`TableFunctionBindInput::inputs`; a scalar's need not be (`f(t.col)` is legal) and the bind callback is handed
argument EXPRESSIONS. So the host folds what it can (`IsFoldable` → `ExpressionExecutor::EvaluateScalar`) and
passes `arg_constant`, a MASK of one char per argument: `'1'` = a folded constant whose value is real, `'0'` =
a runtime expression whose slot holds a NULL PLACEHOLDER. **A `'0'` placeholder is indistinguishable from a
`'1'` slot holding an explicit NULL literal by looking at the value**, so a provider that reads a value
without consulting the mask reads a placeholder as data.

- **The mask is a separate PARAMETER rather than field metadata on `args`, deliberately.** Metadata would have
  to out-live the exported schema — the Arrow-lifetime class this codebase has already paid for once (the
  `ArrowProducer::Release` use-after-free) — and a field may already carry an extension-type marker there.
- **A folding failure is not a bind failure.** `1/0` is foldable and throws; DuckDB's own constant-folding
  rule swallows that and leaves the expression to be evaluated at execution, so the slot is reported as
  runtime and the error surfaces exactly where it does today.
- **`ParameterNotResolvedException` is required, not defensive.** `PREPARE p AS SELECT f(?)` cannot know what
  `f` returns until the parameter is bound; both upstream precedents throw it, and without it the parameter
  arrives as an UNKNOWN-typed placeholder and is silently reported as a runtime slot.

**⚠ BIND SEES PRE-CAST ARGUMENTS — `CastToFunctionArguments` runs AFTER the bind callback**
(`function_binder.cpp:676` vs `:654`). We narrow that as far as it can be narrowed: an argument is marshaled
as its DECLARED parameter type wherever that is concrete, since DuckDB is about to insert exactly that cast,
which makes the bind's view AGREE with the execute batch. Only an `ANY`-declared parameter is left pre-cast,
because there no cast happens and the expression's own type is what execute sees too. The residual rule
stands: bind values are for DECIDING, the execute batch is authoritative for COMPUTING.

**⚠⚠ A SCALAR BIND MUST NOT ESTABLISH THE AMBIENTS (txn + host-FS opener), AND THAT IS THE ONE PLACE IT
DIFFERS FROM EVERY OTHER BIND IN THE TREE. Doing what every other bind does SEGFAULTED THE PROCESS.**
`FabricatorSetActiveTxn` also calls `SetActiveOpener`, pushing the binding context as the ambient. For sqlgen,
`tablefn`, and the ALTER paths that is exactly right — each binds its statement's OWN source. A SCALAR binds
*wherever it is called*, including inside a **nested host query that an OUTER operation is running while that
outer operation holds the ambient**. OPTIMIZE's recluster is precisely that shape: it issues a host query
whose `ORDER BY` calls `hilbert_index`, and the outer Delta write keeps doing host-FS IO afterwards — so
pointing the ambient at the inner connection's `ClientContext` leaves that outer IO resolving a context that
is gone. The dangling-opener use-after-free this codebase has already paid for twice (`table_stats`,
`RollbackTransaction`).

Measured with the call as the ONLY variable: `verify_delta_clustered_optimize` died at `OPTIMIZE main.c1` with
**exit 127, and 139 (SIGSEGV) on its accumulated leg — not one line of output**, no assertion, no stderr; it
passes **147 assertions** without the call, and a shell repro flipped the same way. Nothing needs the ambients
today *by construction rather than by luck*: a discovered SQL UDF takes the DEFAULT binding, which resolves
nothing at bind, and every custom scalar shipped is pure compute. If a provider ever does need its connection
at bind, the fix is a MANAGED-side push/restore scope (the `InterruptScope` shape) — the host cannot restore
it, because the ambient is an AsyncLocal the host can only overwrite.

**⚠⚠ AND THE FIRST "CONFIRMATION" OF THIS WAS CONFOUNDED — worth more than the fix.** The result schema also
crosses as a bare `ArrowSchema` rather than `tablefn_bind`'s zero-row STREAM, because reading a stream's
schema host-side goes through `PopulateReturnSchema`, which sets the same ambient. That change is right on its
own merits (simpler, matches what `get_function_return_schema` has always used, same `ReadSchemaColumns` so
VARIANT imports identically, and it removes a SECOND instance of the hazard) — **but it did not fix the
crash, and it was written up as though it had.** The evidence was the exit code moving 127 → 1, which looked
decisive and was caused by an unrelated second bug found later: the null-`Result` deref had silently dropped
every managed global, so `hilbert_index` no longer existed and the crash was simply *unreachable*. A clean
error where a crash used to be read as "fixed" when it actually meant "no longer executed".

- **The rule: when a fix changes a failure's SHAPE rather than removing it, ask what else changed.** Exit
  1-instead-of-127 is not a pass; here it was a different bug masking the first one.
- **A helper's SIDE EFFECTS are part of its contract.** Nothing in `PopulateReturnSchema`'s name or signature
  says it mutates a process-wide ambient, and "the proven import path" was proven for callers that own their
  statement.
- ⚠ Two wrong repro attempts are worth recording, both from guessing at the mechanism instead of bisecting: a
  two-key `CREATE TABLE … SORTED BY` (PASSED — the CREATE's sort does not go through the recluster's host
  query) and `hilbert_index` over window functions (PASSED). The bisect found the statement in minutes; the
  missing ingredient for the shell repro was the **DELETE** (deletion vectors), without which the recluster
  takes a different path.

**⚠ A LATENT UPSTREAM INCOMPATIBILITY THIS MADE REACHABLE — on BOTH crossings, and the execute half was
BROKEN BEFORE ANY OF THIS.** `ArrowNullData::Append` only bumps `row_count` and its `Finalize` only clears
`n_buffers`, so DuckDB exports an Arrow NULL-typed array with `null_count = 0` — and Apache.Arrow refuses it
(*"Length must equal null count"*), which is what the Arrow spec requires.

- At BIND it surfaced on `fabricator_render(NULL, '{"a":1}')`, whose first parameter is declared VARCHAR: at
  bind the untyped NULL literal is still `SQLNULL`, at execute it has been cast. The declared-type marshaling
  above removes that case entirely.
- At EXECUTE it is reachable whenever an **`ANY`-declared** parameter is handed an untyped NULL, because such
  a parameter is never cast — so `fabricator_render('tpl', NULL)` **failed on `HEAD` too**, and had done for
  as long as the function existed. `CfRenderFunction.Invoke`'s own comment says its column may be *"a
  NullArray"*, i.e. the author expected it to work. **Nothing covered it**: the one NULL in the suites sat in
  the VARCHAR-declared position, which DuckDB casts, which is precisely why the ANY position stayed untested.
- Fixed on both sides by patching `null_count = length` on null-typed children, rather than by dropping or
  substituting the argument — *"you passed an untyped NULL"* is exactly what a bind resolving a result type
  needs to know. Verified pre-existing by reading `HEAD`'s marshal (no such patch) before claiming it.

**⚠ THE COMPILER WARNED AND THE BUILD CHECK MISSED IT — `Field Result` → `Field? Result` produced CS8602 at
two sites, and one of them silently dropped EVERY managed global function.** `Bootstrap`'s
`list_global_functions` enumeration did `fn.Result.DataType.Name`; one function with a null `Result` throws,
the whole crossing fails, and the host's registrar registers nothing — so `hilbert_index`, `bucket`,
`fabricator_render` and the `fabricator_delta_*` family all vanished from `duckdb_functions()` at once, with
no error anywhere. (The visible symptom was `OPTIMIZE` failing with *"Scalar Function with name
hilbert_index does not exist!"*, one layer away from the cause — the same shape as the Apache.Arrow
zero-field `StructType` failure recorded for the unified parameter protocol.) **Nullable warnings ARE the
finding when a member becomes nullable**: the build was checked with `grep ": error"` and read as clean, and
`dotnet build` reports nothing on an up-to-date project, so `--no-incremental` is required to see them at all.

**THE CONSTANT ARGUMENTS ARE REPEATED AT EXECUTE**, in full, materialised for every row — not omitted on the
grounds that the binding already saw them. Which arguments are constant is a property of the CALL SITE, so
omitting them would make column `i` stop meaning parameter `i`, differently per call site, and
`args.Column(i)` would silently read the wrong column. Uniformity keeps the parameter schema the single
positional contract. The cost is real and stated rather than hidden: Arrow has no constant encoding, so a
constant column is N materialised values per chunk (for VARCHAR, N string copies). The escape, if it is ever
measured to matter, is a binding-declared opt-out mirroring `SupportsProjectionPushdown` — deliberately NOT
built, since until a provider is demonstrably hurt it is a flag with no correct use and no honest gate.

Managed: `IScalarFunction.Result` is now `Field?` and `IScalarFunction.Bind(ScalarBindArgs)` is a
default-interface method returning `StaticScalarBinding` — so a fixed-return function implements NOTHING new.
New in `Fabricator.Abstractions`: `IScalarFunctionBinding`, `ScalarBindArgs` (carrying `IsConstant`),
`ScalarBindingHandle` (binding + definition — the definition only for the zero-input fallback that has to
type an empty result stream). New in `Fabricator.Bridge`: `ScalarBindingRunner`, the single per-chunk loop,
which replaced THREE copy-pasted copies of it (the global registry, `CatalogFunctionSet`, and
`SqlServerBackend`), and `ScalarFunctionMetadata.DeclaredReturnField`. `IBackendCatalog.ExecuteScalar` →
`ScalarFnBind`. C++: `FabricatorScalarBind` + `FabricatorScalarFunctionInfo` (identity for a bind callback
that is a raw function pointer and cannot capture) + `ScalarBindingHolder` (refcounted, destructor →
`scalarfn_close`; refcounted because `FunctionData::Copy` means several copies address ONE managed binding) in
`src/catalog/fabricator_schema_entry.cpp`.

## v79 (2026-08-22) — the `lateral_*` session: row-mapped (correlated LATERAL) functions

**Additive**, five entries appended after `table_close`:

```c
int32_t (*lateral_bind)(handle, schema, func, args, input_schema, out_schema, out_binding, err);
int32_t (*lateral_open)(binding, out_session, err);
int32_t (*lateral_call)(session, input, out, err);
int32_t (*lateral_close)(session, err);
int32_t (*lateral_bind_close)(binding, err);
```

**WHY A NEW SESSION RATHER THAN THE IN-OUT EXCHANGE, which it superficially resembles.** Three differences,
each of which the exchange cannot express:

1. **PROVENANCE.** When N input rows produce M output rows the host must know, per output row, WHICH input row
   produced it — otherwise it cannot stamp the correlated columns (`t.x` in
   `SELECT t.x, f.* FROM t, f(t.a)`) and 1→N / 1→0 are inexpressible. An in-out never has to answer that,
   because either exactly one row is in flight or there are no correlated columns to stamp. It rides the wire
   as a TRAILING int32 column on every result batch, so ONE format serves both execution paths.
2. **SEVERAL SESSIONS AT ONCE.** `inout_exchange_open` permits one exchange per binding and serialises
   parallel branches behind a C++ gate; the batched lateral operator declares `ParallelOperator()`, so every
   pipeline thread opens its OWN session and there is no shared mutable state to guard.
3. **REQUEST/RESPONSE, not a stream.** `lateral_call` answers exactly once per request — an EMPTY result means
   "these inputs produced nothing", never "end of stream". The exchange's length-0 SENTINEL has no meaning
   here (the reader skips one rather than reading it as end-of-call).

**⚠ It reuses the in-out ABI for NOTHING, deliberately**, and the reason is the user's own instruction on the
day it was built: the streaming exchange was hard to get right and every `_each` form in the product runs on
it. An additive, isolated path cannot regress it — the only shared code is `ArrowStreamReader`, which is
read-only.

`lateral_bind`'s `out_schema` is the function's OWN output columns — NOT the input echo an in-out returns,
because the correlated passthrough columns are the HOST's business (DuckDB's `projected_input` on the
row-by-row path, our own stamping on the batched one).

Managed: `ILateralTableFunction` / `ILateralBinding` / `ILateralSession` / `LateralResult` in
`Fabricator.Abstractions`, marshaled by `Fabricator.Bridge/LateralExchange.cs`; `IBackend
.GlobalLateralFunctions` and a throwing-DIM `IBackendCatalog.LateralBind`. Declaration kind `lateral`.
C++: `src/catalog/fabricator_lateral.{cpp,hpp}` — the bind, the row-by-row `in_out_function`, the batched
`PhysicalOperator` and the `OptimizerExtension` that installs it. Full as-built + what only building it
revealed: [lateral_unnest_analysis.md](lateral_unnest_analysis.md) §8.

## v78 (2026-08-20) — `catalog_init`: the provider's ONE chance to initialise with a live client context

**Additive**, one entry beside `get_capabilities`:

```c
int32_t (*catalog_init)(FabricatorHandle handle, char **err);
```

Called from `FabricatorCatalog::LoadCatalog` immediately after `FabricatorSetActiveTxn` establishes the
ambients (txn + host-FS opener + settings session) and **before every discovery crossing**. Managed side:
`IBackendCatalog.Initialize()`, a **DIM no-op** — a provider that needs nothing implements nothing, and no
plugin breaks.

**⚠ WHY IT WAS MISSING, and the accident it institutionalised.** `open_catalog` runs with NO ambients,
because it only CONSTRUCTS the catalog — `fabricator_storage.cpp:211` records that as MEASURED (a mutant
adding `SetActiveOpener` there changes nothing) and the invariant is what makes the absent ambient safe. So a
provider whose setup needs a context — connect and detect the engine, resolve a secret, probe a root — had
nowhere to put it and had to hang it off whichever discovery call ran FIRST. **That order is not part of the
contract**, so it was luck. In practice `get_capabilities` became the de-facto init hook by being first,
which is how **SQL Server's first CONNECT ended up inside a call documented as reading a doc of booleans**
(`CapabilitiesJson` reads `Profile.IsBinaryCollation`, whose getter is `EnsureProfile()`).

**⚠ ITS EXCEPTIONS PROPAGATE, and that is the point of the placement rather than a property of the entry.**
The call sits ABOVE `DiscoverSchemas` and is wrapped by NOTHING inside `LoadCatalog` — the only `catch`
there is scoped to the capability read BELOW it, which is what makes the placement safe rather than lucky.
`FabricatorAttach` then wraps it, so a failing init fails the ATTACH and **creates no catalog**. MEASURED:
`IO Error: MSSQL connection validation failed: … catalog_init failed: 258: …`, and `duckdb_databases()` has
no such row.

⚠ **AND THE INJECTED FAULT WAS NOT WHAT I FIRST WROTE DOWN.** This was recorded as "measured with a bad
password"; error **258** is *"A network-related or instance-specific error … the wait operation timed out"*,
i.e. an UNREACHABLE SERVER — a rejected credential is **18456 "Login failed for user"**. The docker stack had
stopped without my noticing, so the probe injected unreachability. **The mechanism claim survives unchanged**
— `Initialize()` → `EnsureProfile()` → connect → throw → propagate → wrapped → no catalog is the same path
whatever made the connection fail, and the error text names `catalog_init` either way — but the fault was
unreachability, and saying "bad password" would be reporting a test that passed for a different reason than
stated.

⚠ **RE-MEASURED PROPERLY once the stack was back, WITH the reachability control the first probe lacked**:
leg A attaches with the CORRECT password (so the server is provably reachable, which is what makes leg B
discriminating), then leg B with a genuinely wrong one against that same server ⇒
**`catalog_init failed: 18456: Login failed for user 'sa'.`** — 18456, not 258 — and `duckdb_databases()`
has no such row. Both legs of the failure surface are now measured; the control is the half that was
missing, not the assertion.

**⚠ AND IT FIXED THE HOLE ONE CALL OVER, which is the part worth carrying forward.** The worry that motivates
a propagating init — "a bad credential yields a successfully attached, empty catalog" — was NOT reachable
before, because `DiscoverSchemas` happens not to be one of the swallowing calls (measured on the mutant
build with the SAME unreachable-server fault: the failure surfaces at `catalog_schemas`, no catalog
created). The real hole was the capability read:

```cpp
try { auto caps = FetchCapabilities(handle_); … } catch (...) { /* every capability off */ }
```

That catch guards TWO unrelated things — a provider that cannot answer (fine: defaults are the safe
direction, superset-and-re-apply) and a TRANSIENT failure of whatever it needs to answer. The second one
disabled string `ORDER BY … LIMIT` pushdown and exact filter pushdown **for the catalog's whole life, with
no signal anywhere**. Now that init owns "the provider cannot serve this catalog", the catch **WARNS**
naming the catalog and what is off. Deliberately still not fatal: the defaults are CORRECT, merely slower,
so turning a degradation into a failed ATTACH would be the worse trade. Made visible, not made fatal.

**Gate `test/verify_catalog_init.test` (14, hermetic), mutation-tested** — removing the call dies at the
first assertion. ⚠ The suite pins the CROSSING ORDER off `duckdb_logs`, because the change moves WHERE work
happens and not WHAT any answer is: no row assertion can distinguish the two. Two things it records about
its own instrument: the assertion is on the MANAGED line (`abi catalog_init`) not the host's, since the
host's proves only that it CALLED; and the comparison is `<=` not `<`, because `duckdb_logs` has no sequence
column and on Delta (a no-op `Initialize`) init and the first discovery call can share a microsecond — while
the regression being guarded (the call moving after discovery) lands STRICTLY later and so is still caught.

⚠ **Lazy init must NOT be removed in favour of this.** A catalog reached through `fabricator_query` /
`fabricator_exec` with a raw connection string, or a transient one built by `COPY … (FORMAT delta)`, never
goes through `LoadCatalog` and so never receives the call. `SqlServerCatalog.Initialize()` is
`=> EnsureProfile()` and that method KEEPS its double-checked guard: this is an eager, well-placed trigger,
not a replacement.

## v77 (2026-08-19) — `catalog_views`: provider-declared catalog-bound VIEWS

**Additive**, one vtable entry beside `catalog_macros`:

```c
int32_t (*catalog_views)(FabricatorHandle handle, struct ArrowArrayStream *out, char **err);
```

Three UTF-8 columns — `schema`, `name`, `create_sql` — each `create_sql` one complete `CREATE VIEW`
statement the HOST parses with DuckDB's own parser and binds into the ATTACHed catalog's schema, where it
resolves as an ordinary relation `db.schema.v`. Byte-for-byte the same shape as `catalog_macros`, read with
`ReadStringTable`, fetched best-effort (`DiscoverCatalogViews` never throws — declaring views is optional
and a broken declaration must never block an ATTACH).

**⚠ The entry is placed mid-struct, beside the other `catalog_*` list entries rather than appended.** That
shifts every later slot, which is safe only because the version moved with it — a mismatched
loadable/bridge pair is rejected at boot rather than calling through a wrong signature. Same practice as
v75's mid-struct removal.

**Why it exists beside `catalog_macros` and is not symmetry.** DuckDB anchors a VIEW body's search path to
the view's own catalog and schema (`bind_basetableref.cpp:309-311`), so an unqualified table reference
inside the body resolves against THAT catalog. A macro body has no such anchor — it is expanded in the
CALLER's context — which is the documented, unfixable hazard of catalog macros. So a view is the only
declaration form whose body can name the provider's own tables without knowing the ATTACH alias.

Managed side: `ViewDefinition` + `IBackend.CatalogViews` (a DIM, empty by default) in
`Fabricator.Abstractions`; `IBackendCatalog.GetViews()` (required, mirroring `GetMacros()`);
`CatalogViewMetadata` in `Fabricator.Bridge`.

Full record, including the two facts the pre-build analysis got wrong and the enumeration defect the build
produced: [docs/macros-and-sqlgen-functions.md](macros-and-sqlgen-functions.md) §5. Gates
`test/verify_views_catalog.test` (59, hermetic) + `verify_functions` 27 → 34 (service, the second
provider).

## v76 (2026-08-18) — `http_request`: a managed HTTP call routed through DuckDB's own stack

**ADDITIVE**, appended to the END of `FabricatorHostServices` (the reverse-direction block), so nothing
moved. The bump is required all the same: the managed side rejects a host-services block whose
`abi_version` does not match, so a mismatched pair is loud at boot rather than reading a garbage slot.

```c
int32_t (*http_request)(FabricatorHandle opener, const char *method, const char *url, const char *headers_json,
                        const void *body, int64_t body_length, char **out_response_json, void **out_body,
                        int64_t *out_body_length, char **err);
```

The point is not the socket — it is that the request inherits DuckDB's configuration: the `TYPE http`
SECRET whose SCOPE matches this URL, `ca_cert_file`, `http_proxy*`, `http_timeout`, the retry knobs. So a
managed component, above all a PLUGIN calling a REST API, stops carrying its own TLS trust and retry
policy. `DuckDbHttpHandler` wraps it as an ordinary .NET `HttpMessageHandler`.

- **Passing the URL to `InitializeParameters` IS the secret mechanism** — httpfs builds a
  `KeyValueSecretReader` over the `FileOpenerInfo`'s `file_path`, so scope matching costs us nothing.
  Mutation-tested: resolving the params with an EMPTY url kills the gate at exactly the secret section
  after 7 assertions pass, i.e. with the TLS A/B still green — precisely discriminating.
- **The envelope is ONE typed JSON doc** (`{"status","reason","url","success","error","headers"}`), the
  v73/v74 pattern, written host-side with yyjson. The BODY crosses as a raw buffer beside it rather than
  base64 inside it. Both are freed with `free_str`, which is plain `free()`.
- ⚠ **`FABRICATOR_ABI_VERSION` and `vtable->AbiVersion` both move**, as always.

Full record, incl. the five things building it found and the two measured A/Bs:
[docs/http-transport.md](http-transport.md).

## v75 (2026-08-18) — `delta_list_files` DELETED with the MultiFileReader spike it served

**BREAKING, no aliases. One vtable slot REMOVED from the MIDDLE of the struct**, so every later field
shifts — the v30/v31/v47/v72 precedent. That is exactly why the version bump matters here: a stale
loadable paired with a new bridge would read `onelake_remove` where `delta_list_files` used to sit and
call it with the wrong signature. The bump makes the mismatch loud at boot instead.

**What went, in one commit** (user-directed): `src/fabricator/fabricator_delta_mfr.{cpp,hpp}` + <!-- check-docs:ignore (REMOVED at ABI v75; naming it IS the point) -->
its `RegisterDeltaMultiFileScan(loader)` call and CMake source entry; the `delta_list_files` slot in
`abi.h`; `DeltaListFiles` in `clr_host.{hpp,cpp}`, `Abi.cs` and `Bootstrap.cs`;
`DeltaReader.ListScanFilesJson` + its async core; and both suites (`verify_delta_mfr_scan` 36, <!-- check-docs:ignore (REMOVED at ABI v75; naming it IS the point) -->
`verify_delta_mfr_dv` 23). Hermetic floors 71 → **69** and 7558 → **7499**. <!-- check-docs:ignore (REMOVED at ABI v75; naming it IS the point) -->

- **⚠ IT WAS NOT DEAD CODE, and the framing that reached me first ("dead code from a multifile reader we
  only tested but never used") is half right in a way worth recording.** It WAS registered
  (`loader.RegisterFunction`), WAS deletion-vector correct, and carried **59 assertions green in both CI
  tiers**. What made it removable is that it is **absent from the README** — a spike that shipped by
  accident. Same pattern as `fabricator_delta_native_scan`, except that one was WRONG (it served deleted
  rows for months) and this one was correct and covered. ⚠ Its own header comment said *"Slice 1a: file
  list only (no DV / partition / pushdown yet)"*, which had been STALE since slice 1b landed DV — so the
  code understated itself, which is part of how it stayed invisible.
- **The removal is answer-neutral BY CONSTRUCTION, and the floor arithmetic is the claim.** The
  production Delta read path is the managed `DeltaNativeReader`, which builds its own `read_parquet` SQL
  and never crossed this entry; `delta_list_files` had exactly ONE caller
  (`fabricator_delta_mfr.cpp:154`) and `ListScanFilesJson` exactly one (`Bootstrap.cs`). 7558 − exactly <!-- check-docs:ignore (REMOVED at ABI v75; naming it IS the point) -->
  59 = 7499 says no surviving suite moved.
- **It deletes the LAST core→Delta coupling in the C++ layer** — which is the reason to do it before the
  Bridge assembly split rather than after: it removes the coupling outright instead of forcing it to be
  abstracted across a new assembly boundary.
- **⚠ A SIDE EFFECT NOT IN THE PLAN: fabricator now calls `ExtensionHelper::AutoLoadExtension` NOWHERE.**
  The only call lived inside this registration (a best-effort `try`/`catch` for `parquet`, whose own
  comment records that it resolves from DISK and never consults the statically linked set — so on our
  builds it usually threw and was swallowed). Nothing regresses: query-time `read_parquet` autoloads
  normally at bind, and the suites that need it already `require parquet` explicitly. But
  [distribution-installer.md](distribution-installer.md) cited it as the in-tree proof that chain-loading
  during load is lock-safe, so that citation was re-anchored to DuckDB's own source
  (`extension_manager.cpp:73-110`), which is where the claim always belonged.
- **⚠ WHAT IT COSTS, stated once so it is not rediscovered as a surprise.** This was the only working
  prototype of "form (b)" — a `MultiFileList` carrying `OpenFileInfo`s straight from the snapshot,
  duckdb-iceberg's shape and the durable form for a native read path. MEASURED 2026-08-18: native parquet
  does the same 6M-row aggregate in **0.203 s** vs **0.592 s** best-tuned through our Arrow boundary, so
  form (b) is worth ~3x where the batch/thread tuning of the preceding two commits got 31%. The DESIGN
  survives in [multifile-delta.md](multifile-delta.md), whose header was rewritten from "Phase-A slices
  BUILDING" to a removal-aware design record. **"Move it to C# as a custom function" is not the
  fallback** — the whole point is DuckDB's C++ `MultiFileReader` doing the read, and the C# expression of
  that idea already exists and IS the production path.

## v74 (2026-08-17) — `table_alter`: ALTER TABLE stops being five positional carriers

`alter_table` was the worst-shaped entry left in the vtable, and the last of the ORIGINAL DDL surface to
carry a name pair. It took an `alter_kind` int plus `arg1`, `arg2`, a `flags` bitfield and an optional
Arrow `column` stream, and **every one of those meant something different per kind**: `arg1` was a new
table name, or a column name, or a JSON array of path segments, or a JSON array of column names; `arg2`
was a new column name, or `"-"`/`"b"+base64(text)` for a DEFAULT; `flags` bit 0 was `IF NOT EXISTS` on the
ADD kinds and `IF EXISTS` on the DROP kinds. Nothing in the signature said which, so fourteen call sites
each spelled their own crossing and fourteen provider branches each re-derived the meaning.

It is now ONE entry on the v72 TABLE SESSION taking ONE typed JSON doc that NAMES ITS VARIANT and carries
only that variant's fields — the v73 `table_info`/`table_stats` pattern with the direction REVERSED (this
is the only ABI doc the HOST writes). The fourteen kind ints survive as the doc's `"kind"` strings, listed
in full on `table_alter` in `abi.h`.

**The two axes are independent, and both were taken.**
- **Name pair → the table handle.** Sound HERE and not for `create_table` / `begin_bulk`, because an ALTER
  always targets an EXISTING table — there is no "the object does not exist yet" asymmetry to work around.
  It costs no extra crossing: `Catalog::Alter` (`duckdb/src/catalog/catalog.cpp`) looks the entry up
  itself before dispatching and returns early when the lookup misses, so `FabricatorSchemaEntry::Alter`
  resolves a cache hit on an entry DuckDB materialized moments earlier. New accessor
  `FabricatorTableEntry::TableHandle()`.
- **Kind + args + flags → one typed doc.** Rendered host-side by `FabricatorRenderAlterJson` (yyjson's
  MUTABLE api — the first place the host writes JSON rather than parsing it), parsed bridge-side into
  `AlterTableSpec` (`Fabricator.Abstractions`, `System.Text.Json`).

**⚠ The `column` Arrow stream stayed, and must.** It is the TYPE CHANNEL for `add_column` / `column_type` /
`add_field`, and a VARIANT column is identified by Arrow field METADATA (`VariantMarker` /
`ew.variant_transport`) that a DuckDB type NAME cannot carry — folding types into the doc as text would
silently regress exactly the extension-type shapes.

**What the doc dissolved, beyond the ambiguity.** `arg2`'s `"-"` / `"b"`+base64 encoding for SET DEFAULT
existed ONLY because a C string cannot tell an EMPTY literal from an ABSENT one; the doc's `default` key
is a JSON string or JSON null, which spells both natively, so `DecodeDefaultArg` is gone. `JsonPathArray`
(C++) and `ParseJsonPath` (C#) are gone — the renderer owns escaping and the spec arrives parsed.
SqlServer's `RequireArg` is gone, replaced by `AlterTableSpec.Require*`, which name the missing FIELD of
the doc rather than a positional argument. And `DeltaCatalog.AlterTable`'s buffered-DML branch had its
kind list written TWICE (once in an `if`, once in the `switch` inside it); it is now one switch, which
cannot disagree with itself.

**⚠ IT FIXED A REAL DEFECT, AND CREATED A REGRESSION RISK IN THE SAME MOVE — say both.** The hand-rolled
`JsonPathArray` escaped `"` and `\` and nothing else, so a legal DuckDB identifier containing a CONTROL
character (`"a<TAB>b"`, via a quoted name) produced invalid JSON: measured against the pre-change build,
`ALTER TABLE t SET SORTED BY ("a<TAB>b")` failed with *"'0x09' is invalid within a JSON string … Path:
$[0] | BytePositionInLine: 3"* — a message about the transport, naming nothing the user typed. Loud, never
silent, so nothing was corrupted. **But the same change puts identifiers that never touched JSON before —
a column name, a new name — INTO the doc**, so correct escaping became load-bearing for carriers that
previously could not care. Both halves are gated, and the gates were mutation-tested by re-inserting the
old two-character escape: `verify_delta_catalog_alter` (116 → 132) dies at its tab-named ADD COLUMN after
118 assertions pass, `verify_delta_sorted_by` (30 → 40) at its `SET SORTED BY` after 32, and
`verify_delta_catalog_nested_alter` (100 → 127) at its nested RENAME after 113. ⚠ The nested suite dies at
the RENAME and not the earlier ADD, correctly: an ADD FIELD's path names the CONTAINING struct while the
new field's own name rides the type channel, so the first control character to cross IN THE DOC there is
the rename's. Tier-0 `Fabricator.Bridge.Tests` +50 (146 → 196) over the parse itself — admissible because
`AlterTableSpec`'s closure is the BCL, which is a consequence of the type NOT being in the doc.

## v71 (2026-08-14) — `get_capabilities`: the catalog capability doc

ONE appended vtable entry returning ONE flat JSON object of the catalog's host-consumed capability
booleans (`exact_filter_pushdown`, `is_binary_collation`; ABSENT key = false), read once at ATTACH from
`LoadCatalog`. Slice 3 of the catalog/table abstraction: it killed the ServerInfo grep — the old
`FetchExactFilterPushdown`/`FetchBinaryCollation` read the diagnostic kind-7 (property, value) stream
TWICE and string-matched it on both sides. Kind 7 stayed, diagnostic-only (`fabricator_server_info()`).
Managed: `IBackendCatalog.CapabilitiesJson`, a DIM returning `"{}"` so a provider with nothing to assert
declares nothing; SqlServer and Delta override it FROM THE SAME FIELDS their diagnostic rows read, so the
two surfaces cannot drift. ⚠ Deliberately NOT part of `open_catalog`'s result although the design doc
first sketched it there: `open_catalog` must stay connection-free (the measured mutant note in
`fabricator_storage.cpp`), while SQL Server needs a CONNECTION to answer (collation detection) — at
`LoadCatalog` the ambients are established and this path always paid the first connection anyway.
(At v72 the kind-7 stream itself became the dedicated `catalog_server_info` entry, still diagnostic-only.)

## ⚠ RENAME, 2026-08-14 — `table_bind`/`table_execute`/`table_close` are now `tablefn_*`. NO ABI BUMP.

**Grepping this file for the OLD names will find nothing — they were rewritten throughout, deliberately, so
that a search for a name lands on live code rather than on history alone. This section is the only record
that they ever had different names.**

| old | new |
|---|---|
| `table_bind` / `table_execute` / `table_close` | `tablefn_bind` / `tablefn_execute` / `tablefn_close` |
| C++ `fabricator::TableBind` / `TableExecute` / `TableClose`, `TableBindState` | `TableFnBind` / `TableFnExecute` / `TableFnClose`, `TableFnBindState` |
| C# `IBoundTable`, `BindingBoundTable`, `TvfBoundTable`, `DaxEvalBoundTable` | `IBoundTableFunction`, `BindingBoundTableFunction`, `TvfBoundTableFunction`, `DaxEvalBoundTableFunction` |
| C# `IArrowTableFunctionBinding` | `ITableFunctionBinding` |

**WHY — the word order was already right and the NOUN was wrong.** The vtable has a consistent convention:
one-shot entries are `verb_noun` (`execute_query`, `execute_scalar`, `scan_table`, `create_table`,
`alter_table`) and session entries are `<function kind>_verb` (`agg_open/update/combine/finalize/close`,
`inout_bind/exchange_open/bind_close`). This trio followed the session convention correctly — but `table_`
was the ONE session prefix whose noun is ALSO a first-class ABI noun with four entries of its own, so
`table_execute` read as "execute a table" while its neighbour `scan_table` is the entry that actually does
that. **The confusion is not hypothetical: a catalog table scan (`lake.dbo.his`) goes through `scan_table`
and has NO C# bind object at all — its bind state is the C++ `ArrowStreamBindData` — while `table_bind`
served only table FUNCTIONS.** Mistaking the two invites hanging per-statement state on a path that never
sees a catalog table; the per-transaction caches (`DeltaTableCache`, `SnapshotPinning`) exist precisely
because `scan_table` has nowhere to put it.

**NO BUMP, and that is the rule applied rather than an exemption.** `CLAUDE.md`'s rule is bump on *adding an
entry* or *changing a signature*; a rename is neither. The vtable is positional in memory, so renaming a
struct member changes no layout, ordinal or signature — a stale loadable against a fresh bridge and the
reverse both keep working, unlike the usual C++-touching change. Bumping would have forced a rebuild on
everyone to no purpose.

⚠ **It was safe to do wholesale because it is COMPILER-ENFORCED end to end** — every name is a struct member
or a type, never string-dispatched, so nothing can silently half-land (contrast the `fabric_` → `fabric.`
schema rename, which was grep-driven and needed three token classes protected by hand). The two hazards
were both mechanical: DuckDB's own `TableBinding` must NOT be caught by a `TableBind` pattern (use a word
boundary — it survives at `fabricator_table_entry.cpp:903` and `arrow_ingest.hpp:50`), and a `grep -rl`
across `dotnet/` sweeps `bin/`/`obj/`, so a `sed -i` over its output rewrites BUILD-OUTPUT DLLs. They are
gitignored and regenerable, but delete them afterwards rather than letting a corrupted one be picked up.

## The `IArrow*` un-prefixing, finished in the same pass (2026-08-14, C#-only, no ABI involvement)

**It did not invent a convention — it finished one that had been half-applied for months.** The FUNCTION
interfaces in `Fabricator.Abstractions` were ALREADY unprefixed (`IScalarFunction`, `IAggregateFunction`,
`IInOutFunction`, `ICollectorTableFunction`, `ISqlTableFunction`) — an earlier pass renamed the TYPES and
left the FILES, and `docs/global-functions.md` still records one of those moves in flight
(*"`ICatalogScalarFunction : IScalarFunction` // RENAME of `IArrowScalarFunction`"*). What remained was the
BINDING/state family and three stale filenames:

| old | new |
|---|---|
| `IArrowCollectorBinding` / `IArrowInOutBinding` / `IArrowInOutIsolation` / `IArrowAggregateState` | `ICollectorBinding` / `IInOutBinding` / `IInOutIsolation` / `IAggregateState` |
| files `IArrowAggregateFunction.cs` / `IArrowInOutFunction.cs` / `IArrowCollectorTableFunction.cs` / `IArrowTableFunction.cs` | `IAggregateFunction.cs` / `IInOutFunction.cs` / `ICollectorTableFunction.cs` / `ITableFunction.cs` |

⇒ **the only `IArrow*` names left in the tree are Apache Arrow's own** (`IArrowArray`,
`IArrowArrayBuilder`, `IArrowArrayStream`, `IArrowType`), which is now a usable invariant: an `IArrow`
prefix means the type is theirs, not ours. Historical design docs still name `IArrowFunction`,
`IArrowScalarFunction` and `IArrowTableInOutFunction` — deliberately NOT swept, because those passages
document the earlier renames themselves.

⚠ **IT IS A BREAKING CHANGE FOR PLUGIN AUTHORS** and no aliases were kept, consistent with the Fabricator
rename's precedent. `Fabricator.Abstractions` is the contract assembly a plugin compiles against, so an
out-of-tree plugin implementing `IArrowInOutBinding` no longer builds; the fix is mechanical (drop `IArrow`,
keep the rest).
- ⚠ **AND NOTHING IN THE REPO TESTS THAT — do not read the green `Fabricator.SamplePlugin` build as
  evidence.** The sample plugin implements exactly `IBackend` and `IScalarFunction`, NEITHER of which was
  renamed, so it compiles unchanged whatever happens to the binding/state types. Its gate
  `verify_plugin` (10 assertions) is a single scalar function, so the plugin SPI's in-out, collector and
  aggregate surface has NO out-of-tree coverage at all. What the rename IS covered by is the 78 in-tree
  uses across Bridge / SqlServer / AnalysisServices / DeltaRs — same-solution consumers, which the compiler
  fixes for you and a plugin author's does not.

⚠ **The README named THREE interfaces that never existed** — `IArrowTableFunction`, `IArrowInOutFunction`,
`IArrowAggregateFunction` — instructing users to implement FILE names. Corrected to the catalog interfaces
`CustomFunctions.cs` itself documents (`ICatalogTableFunction` / `ICatalogInOutFunction` /
`ICatalogAggregateFunction`). It had been wrong since those files were written, and it is exactly the drift
`CLAUDE.md`'s keep-the-README-in-sync rule exists to catch: the audience least able to spot it.

⚠ **RESIDUAL:** `FabricTableBinding` (the `FabricApi` base class) still reads like DuckDB's `TableBinding`
and would be `FabricTableFunctionBinding` under this convention.

- **Prior: ABI v70** (v70, 2026-08-14 = **METADATA KINDS 8-11/13-14 DELETED** — the Delta features that wore
  C++-registered `fabricator_delta_*` fronts became catalog-bound `delta.*` functions (slice 2 of the
  catalog/table abstraction, [catalog-table-abstraction.md](catalog-table-abstraction.md) §5 item 2). ⚠ No
  vtable entry or signature changed; the bump exists because kind REMOVAL is the inverse of the additive
  no-bump rule — a stale loadable would send kind 8 and get the provider's empty-table fallback, silently
  wrong, where a version mismatch fails loudly at boot. Kind 12 VirtualColumns and 15 CatalogMacros keep
  their numbers; the gaps stay unassigned so a stale peer's kind cannot silently alias a new one. Full text
  stayed in `CLAUDE.md` as the then-current version and moved here when v71 landed.)
- **Prior: ABI v69** (v69 = **SCOPED SETTINGS** — provider settings honour DuckDB's `SetScope`. `set_setting`
  gained a LEADING `int64_t session` (0 = the GLOBAL layer — a `SET GLOBAL`, and every registration
  default — non-zero = the SESSION layer, keyed by the setting connection's `ClientContext` address via
  `fabricator::SessionKeyFor`); `set_active_opener` gained a `int64_t session` beside the opener; and ONE
  appended entry `clear_session_settings(session)` reclaims a closed connection's values. Managed:
  `ProviderSettingsStore` grew a session layer resolving **session ?? global**, plus a `CurrentSession`
  AsyncLocal mirroring `AmbientOpener`. See the scoped-SET entry in `CLAUDE.md` "Next up" and
  [settings-architecture.md](settings-architecture.md) §5.3.)
- **Prior: ABI v68** (v68 = **`generate_table_sql`** — the SQL-GENERATING table function surface; see the
  sqlgen bullet in `CLAUDE.md` "Next up" and [macros-and-sqlgen-functions.md](macros-and-sqlgen-functions.md)
  §2. Full text stayed in `CLAUDE.md` as the then-current version and moved here when v69 landed.)
- **Prior: ABI v67** (v67 = **`options_json`** — `create_table` + `begin_bulk` each gained a
  nullable `const char *options_json` carrying the `CREATE TABLE [AS] ... WITH (key='value', ...)` clause
  as a flat JSON object of STRING values (`fabricator::TableOptionsArg` — constants only, one CAST level
  unwrapped so `true`/`false` arrive as postgres `'t'/'f'`; `SupportsCreateTable` now PERMITS `options`).
  The PROVIDER parses the keys it knows and REJECTS unknown ones — never silently ignored. Delta consumes
  three key kinds (see the WITH-options bullet in "Next up"); SQL Server/DAX/deltars reject any options
  (SQL Server's keys land with slice B). **Parser gotcha (load-bearing):** DuckDB's transformer LOWERCASES
  every WITH key (`transformer.cpp TransformTableOptions` — quoting does NOT help), so case-sensitive
  `delta.*` property keys are re-cased C#-side from a canonical well-known list (`DeltaWithOptions.
  CanonicalKeys`); arbitrary mixed-case custom keys must use `fabricator_delta_set_tblproperties`.)
- **Prior: ABI v66** (v66 = **host_query CANCELLATION** — `host_query` gained a nullable
  `void **out_interrupt` (a heap `shared_ptr<ClientContext>` to the query's FRESH context) + two appended
  host-service entries `host_query_interrupt` (thread-safe `Interrupt()`; no-op after the query ends — the
  shared_ptr keeps the context alive) / `host_query_interrupt_free`. Why: every `Host.Query` (native_write
  parquet writes + rewrites, `DeltaNativeReader` per-file `read_parquet`, `HostBatchFilter`, codec
  `IDataFileReader`) runs on a fresh connection INVISIBLE to the user query's Ctrl+C — a heavy first Fetch
  (the rewrite's anti-join build, a big COPY) was uncancellable. The C# `HostFs.Query` CENTRALIZES the fix:
  it wraps every result in an `InterruptibleQueryStream` (an `InterruptScope` on the AMBIENT opener whose
  token trips the interrupt; dispose order = registration [waits in-flight] → scope → inner stream →
  free-handle) — zero per-call-site changes. This closed the "native_write host_query rewrite" deferred
  item; Tier 4 (async/BLOCKED source) remains the only deferred rearchitecture. See docs/cancellation.md.)
- **Prior: ABI v65** (v65 = **`is_interrupted`** — a host→managed reverse callback on
  `FabricatorHostServices` reading the calling operator's `ClientContext::interrupted` (the atomic set by
  Ctrl+C via `Connection::Interrupt()` or a query timeout). The opener handle already IS a `ClientContext*`
  (the `fs_*` secret-resolution handle), so `HostIsInterrupted` is a one-line cast+read. **Cancellation
  Tier 1 — design + tiers: [docs/cancellation.md](docs/cancellation.md).** THE PROBLEM: C# CancellationTokens
  were dead-wired (~124 `default` sites; the only real CTS was BulkSession's internal consumer-exit); sync
  SqlClient/ADOMD ignore tokens; and the C++ extension never checked interruption — so a query parked inside a
  single long-blocking C# I/O call (a big OneLake/S3 read, a slow SQL scan, a hung socket) could only be
  cancelled AFTER that call returned (DuckDB cancels BETWEEN `get_next` calls — `pipeline_executor.cpp` throws
  `InterruptException` on `context.interrupted` — but a blocked `get_next` holds the pipeline). THE FIX (this
  slice): `is_interrupted` + a C# **`InterruptScope`** (a per-operation `CancellationTokenSource` + a
  `System.Threading.Timer` polling `is_interrupted(opener)` every 50 ms on a pool thread — NOT the blocked
  task thread — that trips the token on interrupt; `Dispose` waits for any in-flight callback so no poll
  outlives the `ClientContext`). Wired into the **engineered-wood streaming read paths**
  (`DeltaReader.Stream`/`StreamWithRowIds`/`StreamAt`/`StreamWithRowIdsAt` → their `*Impl` cores now take the
  opener, build an `InterruptScope`, and pass its token to `DeltaTable.OpenAsync`/`ReadAllAsync*` — EW already
  honors it, so a long OneLake/S3 batch read cancels between chunks). A never-tripped token is BYTE-NEUTRAL
  (full delta sweep + SQL fn suites green at v65). **Tier 2 SQL Server DONE (2a `4d33f68` + 2b `6e952f6`,
  C#-only):** the two long-running SQL windows now cancel. **2a — data scan:** `ExecuteQuery` uses
  `OpenAsync`/`ExecuteReaderAsync(token)` + `DbDataReaderArrowStream` fetches with `ReadAsync(token)` (async
  SqlClient honors the token natively — Microsoft's model, no `SqlCommand.Cancel()` trick; the stream owns the
  `InterruptScope`; gated to data scans, short metadata reads stay uncancelled). **2b — bulk (INSERT/CTAS/COPY):**
  `BulkSession` builds an `InterruptScope(opener)` + `token.Register`s its existing `Complete(abort)` teardown
  (fault the channel → `WriteToServer` stops + rolls back AND a backpressure-parked `push_batch` unblocks — no
  `WriteToServerAsync` needed); works for SQL bulk AND Delta streaming writes. **2c — SQL DML/exec DONE
  (`<pending-commit>`, C#-only):** `ExecuteNonQuery` (raw `fabricator_exec`), `ExecuteDelete`, `ExecuteUpdate` run
  their writes with `ExecuteNonQueryAsync(token)` under an `InterruptScope(AmbientOpener.Current)` (chunked
  DELETE/UPDATE loops share one scope) — a long rowid DELETE/UPDATE or slow `fabricator_exec` cancels.
  **Load-bearing constraint (verified):** `is_interrupted` derefs the opener as a `ClientContext*` and
  `AmbientOpener` is NEVER cleared, so polling is only safe where the opener is set FRESH right before the op —
  and EVERY write path does: scan (`arrow_ingest`), bulk (`fabricator_insert`), **the DELETE/UPDATE modify
  operator** (`Finalize` → `FabricatorSetActiveTxn` → `SetActiveOpener`, `fabricator_txn_util.hpp` — the earlier
  "modify doesn't set the opener" note was WRONG; it's inside that helper), and `fabricator_exec`
  (`fabricator_extension.cpp:501`). So 2a/2b/2c capture the current statement's live `&context`. Paths WITHOUT a
  preceding `SetActiveOpener` (metadata reads, DDL via CreateTable/AlterTable) are left uncancelled (short anyway).
  **Delta EW DML/maintenance DONE (`<pending-commit>`, C#-only):** the EW write cores `DeleteByRowIds`/
  `DeleteByRowIdsViaVectors`/`UpdateByRowIds`/`Optimize`/`Vacuum` each build an `InterruptScope(opener, ct)`
  internally (like the Tier 1 read paths) and pass the token to `OpenAsync` + the EW op — a slow OneLake/S3
  copy-on-write/DV rewrite, OPTIMIZE, or VACUUM cancels (codec path fully; native_write's parquet rewrite runs on
  a separate host_query connection, uncancelled — Tier 4). AUTOCOMMIT only. **Buffered explicit-txn path DONE
  (`<pending-commit>`, C#-only):** the COMMIT-phase flushes (`FlushDmlTransaction` — incl. breaking OUT of the
  OCC/rebase retry loop via `ThrowIfCancellationRequested`, token to `WriteDataFilesAsync`/`Compute…`/`Rebase…`/
  `CheckLogicalRebaseAsync`/`CommitDataFilesAsync`/`OpenAsync`; `FlushCreateTransaction`; `FlushDeferredFiles`) +
  the per-statement buffering (`WriteCdcFiles`, `TryEagerWriteBatches`, buffered-UPDATE `ReadRowsByRowIds`
  read-back) each build an `InterruptScope(opener)` and pass the token to their EW calls. Safe: a cancel before
  the atomic `_delta_log` commit lands = invisible orphan files (VACUUM reclaims) = rollback; a cancel isn't a
  `DeltaConflictException` so it exits the retry loop, not swallowed as a conflict. transactions 941 / txn_version
  51 / row_tracking_virtual 299 green. **2d — SQL command_timeout DONE (`<pending-commit>`, C#-only):** `mssql_command_timeout` setting
  (seconds; **default 0 = infinite**) + a per-catalog `command_timeout` ATTACH option, resolved `SET ?? ATTACH
  ?? 0` (`ResolveCommandTimeout`) → `SqlCommand.CommandTimeout` on scans/DML + `BulkCopyTimeout`. NATIVE,
  server-enforced, PER-ROUND-TRIP (aborts a hung round-trip, doesn't kill a long-but-progressing scan — which a
  client `CancelAfter` on the whole scope would); complements the interrupt token (token = Ctrl+C cancel; timeout
  = non-interactive/hung safety net). Default 0 removes the prior implicit SqlClient 30 s (a warehouse-query
  footgun). `test/verify_command_timeout.test` (6). The `io_timeout` for OneLake was DROPPED (httpfs/duckdb-azure
  have their own HTTP timeouts; a coarse whole-scope CancelAfter would mis-fire on long streaming reads).
  **Tier 3 DAX — DONE (2026-07-16, C#-only):** ADOMD has no async, so `InterruptScope` +
  `AdomdCommand.Cancel()` (thread-safe server-side abort) from the poller thread, wired at the chokepoints:
  `DaxCatalog.StreamCommand` (ALL data-returning DAX — scans/DMV/daxeval/daxevaltable; scope armed BEFORE
  `ExecuteReader` so the long initial evaluation is covered, ownership transfers into `DaxArrowStream`,
  disposed registration-first), `ProbeSchema` (the bind-time probe EXECUTES the query), and `DaxEachBinding`
  (ctor probe + ONE scope covering every per-row ExecuteReader in `DoExchange`, linked to the exchange ct).
  Metadata/DMV discovery reads stay uncancelled (SQL-provider policy); `AdomdConnection.Open` has no async —
  accepted. `verify_dax` 29 green vs live PBI Desktop. The host_query rewrite window closed at v66.
  **Remaining (deferred): Tier 4 only** — the arrow scan as a DuckDB async/BLOCKED source
  (`InterruptState`) to free the task thread during I/O (native interrupt + better parallelism, bigger). Live Ctrl+C
  behavior is a MANUAL check (a slow OneLake/SQL query + interrupt); the suites verify only behavior-neutrality.
  **BINARY STATUS (2026-07-23): ALL targets CURRENT on DuckDB v1.5.5 + the EW clast-master engine at
  ABI v67.** Windows: unittest + `duckdb.exe` shell + the loadable (2026-07-22). Linux (2026-07-23):
  `build/linux-payload/` fully refreshed — `fabricator.duckdb_extension` (35 MB, linux_amd64, built in
  WSL Ubuntu 24.04; **glibc symbol ceiling 2.38 = Azure Linux 3's glibc, Fabric-compatible**), the FDD
  zip (net8.0 loose-root, now incl. the AWSSDK assemblies), and the notebook wheel swapped to
  `duckdb-1.5.5-cp310` (the 1.5.4 wheel removed — `fabricnb` globs `duckdb-*.whl`; its Program.cs 1.5.4
  refs updated). Load-smoke green in WSL on the official 1.5.5 wheel + FDD + downloaded .NET 8 runtime
  (load, delta CTAS, explicit txn, DELETE, variant cast). **VALIDATED LIVE ON FABRIC (2026-07-23,
  fabricnb upload + RunNotebook): 16/18 probe steps green on the new payload — duckdb 1.5.5 wheel,
  loadable on AZL3 (the glibc-2.38 ceiling holds live), fuse Tables incl. txn append, ambient
  SQL/delta/DAX, token secrets; the 2 fails are the documented-expected pair (pbi-audience → 18456,
  static-azure-secret abfss → the pinned single-audience 401).** NOTE the harness prints the result,
  but the durable copy is `Files/fabricator_ext/result.json` on LH. To fetch it from Windows: `.read
  dax_secret.sql` then **`copy (select content from read_text('onelake://Test/LH.Lakehouse/Files/
  fabricator_ext/result.json')) to '<ABSOLUTE windows path>' (format csv, quote '', header false);`** —
  `read_text` is a TABLE function (a scalar call is a binder error), and the COPY target must be an
  absolute Windows path since duckdb.exe does not resolve a bash-style `/tmp`. Then parse the JSON
  locally (the probe's step list is the readable part). `fabricnb/Program.cs` repo path fixed to `d:\repos\fabricator-extension` (was the
  pre-rename path). `dbt_mssql_test/.venv` (serves dbt_dax_test too) upgraded to `duckdb==1.5.5` — the venv is uv-managed (NO pip module; use
  `uv pip install --python .venv/Scripts/python.exe`). Linux rebuild recipe unchanged: rsync `src/` +
  `test/` + `extension_config.cmake` → WSL `~/sqlext`, fresh duckdb clone at v1.5.5,
  `-DOVERRIDE_GIT_DESCRIBE=v1.5.5` + the `~/vcpkg` x64-linux toolchain, target
  `fabricator_loadable_extension`.
  **THE FABRIC NOTEBOOK NOW USES THE SINGLE FILE — the three-piece payload is RETIRED (2026-07-25,
  validated live).** `fabricnb` uploads ONE `fabricator.duckdb_extension` (the 40 MB linux_amd64
  STANDARD/framework-dependent SKU from `scripts/pack-distribution.ps1`) instead of loadable + FDD zip +
  `FABRICATOR_MANAGED_DIR`; the notebook stages that one file and just `load_extension`s it with **ZERO
  env vars** — the installer unpacks the core + bridge into DuckDB's own extension directory itself.
  **LIVE RESULT: 16/18 probe steps green (the 2 fails are the same documented-expected pair), i.e. NO
  capability lost.** Load timings measured IN the notebook: **3.22 s cold** (extract 39 MB + chain-load +
  CLR boot) and **0.18 s warm in a FRESH process** — the sha marker short-circuits extraction, proven on
  Fabric. **KEY FINDING: the DEFAULT extension directory is WRITABLE in a Fabric notebook** —
  `HOME=/home/trusted-service-user`, the bridge landed at
  `/home/trusted-service-user/.duckdb/extensions/v1.5.5/linux_amd64/fabricator`, so NO
  `SET extension_directory` is needed (the harness keeps a `/tmp` fallback attempt for environments where
  HOME is not usable; it did not fire). Everything else still passes on the new payload: fuse Tables
  (attach/read/create/txn append), delta local + lakehouse-files + abfss-ambient, warehouse SQL via
  database-audience token / `authentication default` / bare connstr, lakehouse SQL endpoint, warehouse
  ATTACH via token secret, azure access_token secret, and DAX ambient (14 tables, daxeval). The lakehouse
  `Files/fabricator_ext/` folder is down to TWO uploads (artifact + the duckdb wheel, which is STILL
  required because the core is CPP-ABI-locked to its DuckDB version while notebooks preinstall an older
  one) — the FDD zip and a stale 1.5.4 wheel were deleted, and the driver now retires anything in that
  folder that is not one of the current files. **⇒ `build/linux-payload/` no longer feeds the notebook
  flow; the recurring "refresh the stale payload" chore is GONE** (that dir remains only as the raw linux
  core + wheel source; `build/linux-dist/` holds the current v68 linux core).)
- **Prior: ABI v64** (v64 = **`onelake_move`** — atomic single-file rename via the ADLS Gen2
  DFS **native rename** (`DataLakeFileClient.RenameAsync`, a metadata op that overwrites an existing
  destination = MoveFile semantics; destination path filesystem-relative with the `<item>.Lakehouse`
  leading segment, same quirk as `FabricLakehouse.RenameDirectory`; same-workspace only). The onelake FS
  now implements `MoveFile`, which makes **DuckDB's default COPY tmp-file staging work on `onelake://`**:
  COPY to an EXISTING file writes `<file>.tmp` then `MoveFile` — a branch taken ONLY because `onelake://`
  classifies as LOCAL in DuckDB's hardcoded remote-prefix list (`bind_copy.cpp` forces
  `use_tmp_file=false` for remote schemes like `abfss://`, so duckdb-azure never hits its missing
  MoveFile). Previously the overwrite threw "MoveFile is not supported" unless `USE_TMP_FILE false`;
  live-validated (default COPY over an existing onelake file → new data, no `.tmp` leftover). NOTE:
  engineered-wood's Delta COMMIT rename deliberately stays on the exclusive-create-copy + delete
  emulation (it needs put-if-absent on the destination; a plain rename overwrites).)
  **⚠ DO NOT set `USE_TMP_FILE` on our COPYs — it is ALREADY false on every write path, and setting it
  explicitly BREAKS the partitioned one (analysed 2026-07-29).** The rule is NOT "true for local"
  (`bind_copy.cpp:226-236`): remote prefix ⇒ false, else
  `FileExists(target) && !per_thread_output && partition_cols.empty() && !is_stdout` — so the decisive
  conjunct is that the target **already exists as a REGULAR file** (`FileExists` is `S_ISREG`-gated, so a
  DIRECTORY target is false too). Every fabricator COPY is therefore already false: `RunCopy` and
  `RunCopySql` write a fresh file (and this does not rest on reading the name generator — **Delta data
  files are IMMUTABLE by protocol**, a rewrite writes a new file and tombstones the old, so an existing
  target would itself be a bug), `RunCopyPartitioned` is forced false by `PARTITION_BY`,
  `ExternalTableRouting`'s parquet append uses a fresh Guid, and our own `FORMAT delta` copy targets a
  directory. And `user_set_use_tmp_file` combined with `PARTITION_BY` / `FILE_SIZE_BYTES` /
  `PER_THREAD_OUTPUT` **THROWS** `NotImplementedException` (`bind_copy.cpp:205-213`), so a defensive
  blanket `USE_TMP_FILE false` in `CopyTuning` would kill `RunCopyPartitioned` at bind.
  **What actually protects us from half-written files is the COMMIT ORDER, not COPY:** data file first,
  then the atomic `_delta_log` commit that references it — a partial data file is referenced by no `add`,
  so it is an invisible orphan for VACUUM (the same mechanism that makes ROLLBACK safe for eager writes).
  The one file whose atomicity matters is the commit JSON, and DuckDB's COPY never writes it (EW writes it
  through `ITableFileSystem` with temp + rename/exclusive-create). **Fabric-notebook FUSE mount:**
  `/lakehouse/default/…` is a genuine LOCAL path, so the same rule applies — tmp-file staging only on
  overwrite of an existing file, which our Delta writes never do; fuse CAN leave a partial or
  never-flushed data file (it buffers and flushes on close) but harmlessly, as an orphan. The real fuse
  risk is the COMMIT put-if-absent (already recorded: single-writer only over fuse — use abfss/onelake for
  concurrent writers), which `USE_TMP_FILE` does not touch either way. The tmp+rename branch IS live for a
  USER's own `COPY … TO 'onelake://…/existing.parquet'` (why v64 exists); whether that rename behaves over
  the FUSE mount is UNTESTED by us — nothing on a fabricator write path depends on it.
- **Prior: v63** (v63 = **etag/mtime-backed cache validation on `onelake://`** —
  `onelake_open` gained `char **out_etag` (owned UTF-8, freed via free_error) + `int64 *out_modified_ms`
  outs: when the managed open DOES fetch properties (bare open, `known_size<0`) it returns the file's
  ETag + LastModified alongside the size (the SAME response — zero extra IO); a known-size open leaves
  them untouched and the host takes them from the listing's `extended_info` (v62 already carried
  `etag`/`last_modified` there). Both land on `FabricatorOneLakeFileHandle`, and the FS now overrides
  **`GetVersionTag`** (returns the etag) + `GetLastModifiedTime` (the real mtime; 0 when unknown).
  Why this matters: `onelake://` is NOT in DuckDB's hardcoded `EXTENSION_FILE_PREFIXES` remote list →
  `IsRemoteFile()=false`, but the cache-validation default is **`validate_external_file_cache =
  'VALIDATE_ALL'`** — validation RUNS for onelake, and `ExternalFileCache::IsValid` prefers the version
  tag whenever either side has one (exact etag compare; empty-vs-empty falls to the mtime + 10s-threshold
  path, which with our previous constant-0 mtime degenerated to always-valid). So cached ranges of an
  IN-PLACE OVERWRITTEN onelake file are now invalidated by default — for free, since the etag always
  rides an existing response. Unknown identity (no listing info on a bare open that skipped properties —
  can't happen; both sources covered) degrades to the old immutable-file assumption.)
- **Prior: v62** (v62 = **listing-metadata riding + skip-HEAD opens on `onelake://`**:
  `onelake_open` gained an `int64 known_size` arg (−1 = fetch) — the managed open SKIPS its per-file
  GetProperties round trip when the size is already known. Sources of "known": the OneLake DataLake
  listing now emits `size` + `last_modified` + `etag` in the glob JSON (all FREE fields of GetPaths), the
  C++ `FabricatorOneLakeFileSystem::Glob` surfaces them as `OpenFileInfo.extended_info` under the SAME keys
  httpfs fills (`file_size`/`last_modified`/`etag`), and the FS now opts into DuckDB's
  **`SupportsOpenFileExtended`/`OpenFileExtended`** so glob-fed opens (read_parquet globs, file lists)
  carry the info through — the exact mechanism httpfs uses to skip its HEAD request. Same pass, NO-bump
  fixes: **`HostFsGlob` no longer does OpenFile+GetFileSize PER MATCHED FILE** (on a fuse mount an open
  can DOWNLOAD the blob — this was the 258 s-ATTACH root cause; on S3 a HEAD per match) — size now comes
  from the glob entry's `extended_info` (object stores fill it in the LIST response, verified in httpfs
  s3fs.cpp) else −1, and the managed `DuckDbTableFileSystem.ListAsync` fills LOCAL files via a cheap
  `FileInfo.Length` (only consumer of size = VACUUM's bytes metric, best-effort by design).)
- **Prior: v61** (v61 = **`onelake://` is now WRITE-COMPLETE for Delta commits** —
  `onelake_open_write` gained an `int32 exclusive` arg (put-if-absent via ADLS conditional create,
  `If-None-Match:*` — the C++ onelake FS now honors `EXCLUSIVE_CREATE` instead of silently ignoring it) and
  one appended entry `onelake_remove(path, cred_json)` (`DataLakeFileClient.DeleteIfExists`, idempotent) backs
  `RemoveFile` — engineered-wood's commit rename is emulated as exclusive-create-copy + DELETE-SOURCE, so both
  were needed before **`fabricator_delta_write(<input>, path := 'onelake://…')` works** (previously abfss:// only;
  live-validated, v3 Overwrite commit + exact readback). Same pass, C++-only: `CreateDirRecursive`
  (fabricator_fs_spike.cpp) got a **scheme-authority recursion FLOOR** — the old guard only matched a literal
  trailing `scheme://`, and since the onelake FS reports `DirectoryExists=false` always (implicit ADLS dirs),
  the recursion walked past `onelake://Test` to `onelake:` which fell through to the LOCAL filesystem
  ("Failed to create directory \"onelake:\""); abfss:// never hit it because duckdb-azure answers the
  ancestor-exists probe. `MoveFile` on the onelake FS still throws (RENAME TABLE keeps the DFS-SDK route).)
- **Prior: v60** (v60 = `begin_transaction` gained an `int32 is_explicit` arg — 1 for a user
  `BEGIN..COMMIT`, 0 for the implicit per-statement autocommit wrapper (C++ reads
  `context.transaction.IsAutoCommit()` in `FabricatorTransactionManager::StartTransaction`). Drives the Delta
  provider's **buffered transactional DML** (slice 2 — see the explicit-transactions bullet): DELETE/UPDATE
  buffer ONLY in explicit transactions; autocommit keeps the direct per-statement paths (CDF capture,
  copy-on-write) byte-identical. Other providers ignore the flag.)
- **Prior: v59** (v59 = `begin_bulk` gained an `int32 partition_overwrite` arg — the
  **`PARTITION_OVERWRITE` COPY option**: DYNAMIC partition overwrite (Spark `partitionOverwriteMode=dynamic`) —
  `COPY src TO 'cat.sch.t' (FORMAT mssql, CREATE_TABLE false, PARTITION_OVERWRITE true)` atomically replaces
  exactly the partitions PRESENT IN THE INPUT in ONE Delta commit (their active files removed + the new files
  added — a log-level swap, no physical delete, so time travel keeps working; unlike DuckDB COPY's local-only
  physical OVERWRITE flag); untouched partitions are unaffected. The SQL-friendly successor to the
  `delta_write_options` `replace_where` setting (which stays for INSERT — STATIC explicit filter vs this DYNAMIC
  data-derived one; combining both errors). Guards: C++ bind rejects it with CREATE_TABLE/REPLACE; the provider
  rejects SCHEMA_MODE 'overwrite' combos + an unpartitioned target; providers WITHOUT partition semantics
  (SQL Server/DAX/deltars) REJECT it when set — an overwrite flag must never be silently ignored (deliberate
  break from the advisory schema/sort-option precedent). Delta: STREAMS under `native_write` (the partitioned
  COPY runs as usual; EW `CommitDataFilesAsync(dynamicPartitionOverwrite:)` derives the touched set from the
  written files' partitionValues and removes the matching active files) and collects otherwise (EW
  `DynamicOverwriteAsync` → `WriteCoreAsync(dynamicPartitionOverwrite:)` collects touched canonical partition
  keys during the write — `CanonicalPartitionKey`, physical-keyed under mapping with logical-key tolerance for
  old commits). `appendOnly` enforcement treats it as a non-append (it removes files).
  **CDF respects it**: the overwrite commit carries no `_change_data` files, so the feed INFERS the swap —
  removed files' rows → `delete`, added files → `insert` (Spark's representation of an overwrite). Making that
  correct fixed TWO PRE-EXISTING CDF-inference gaps (EW `CdfReader` rewritten to take the current snapshot):
  (1) on a PARTITIONED table the inferred rows lacked the partition column (data files exclude it — now re-added
  from the action's `partitionValues` via `PartitionUtils.AddPartitionColumns`, dual logical|physical keys);
  (2) a removed/added file's **deletion vector** was ignored — already-DV-deleted rows were re-reported as
  deletes (now excluded via `DeletionVectorFilter`; they were reported when the DV committed).
  `test/verify_delta_catalog_partition_overwrite.test` (85 — collect + streaming, one-commit swap, time travel,
  CDF feed incl. the DV edge, guardrails).)
- **Prior: v58** (v58 = additive `host_log` on `FabricatorHostServices` — forward managed ILogger events into DuckDB internal logging; wired + lockstep-verified + **`duckdb_logs` surfacing CONFIRMED LIVE**: `CALL enable_logging(storage='memory')` then `SELECT * FROM duckdb_logs WHERE type LIKE 'Fabricator%'` shows the events with the ILogger category as `type` [`Fabricator.Delta`/`.Native`] + mapped `log_level`. The earlier 0-rows was two red herrings, not a code bug: the enable form is `CALL enable_logging(...)` not `PRAGMA`, and the **shell** defaults log storage to a console-printing sink [`storage='memory'` is the `unittest`/API default]. The `FABRICATOR_LOG_LEVEL`+`FABRICATOR_LOG_FILE` file sink is the always-on independent trace. **2026-07-16 FIX: `HostLogService` now gates on `Logger::ShouldLog` before `WriteLog`** — `Logger::WriteLog` writes UNCONDITIONALLY (bypasses enabled/level/type config), and the shell's console sink printed EVERY forwarded Debug/Info event (`DEBUG: abi get_metadata …` on each metadata call — first visible when the v66 rebuild gave the shell a working bridge again). Semantics are now DuckDB-native: the shell surfaces Fabricator WARNINGs only (empirically its default config passes WARNING and blocks Info/Debug — e.g. the benign per-ATTACH DAX `TMSCHEMA_PARTITION_SOURCES` probe failure via the central failed-crossing WARN), unittest/API stay silent, and `duckdb_logs` shows what the user enables. **Gotcha: the `.test` duckdb_logs pins on Debug messages (per-file `read_parquet` SQL) must enable with `CALL enable_logging(level := 'debug', storage := 'memory')`** — the default enabled level is INFO (late_materialization / row_tracking_virtual / dynamic_filter updated).)
- **Prior: v57** (v57 = **`delta_list_files`** — one appended vtable entry for the native-read
  MultiFileReader path (docs/multifile-delta.md Phase A slice 1a): `fabricator_delta_mfr_scan(path)` clones <!-- check-docs:ignore (REMOVED at ABI v75; naming it IS the point) -->
  `parquet_scan` + swaps in `FabricatorDeltaMultiFileReader` (`src/fabricator/fabricator_delta_mfr.cpp`), whose <!-- check-docs:ignore (REMOVED at ABI v75; naming it IS the point) -->
  `CreateFileList` calls `delta_list_files(path, push_json)` → C# `DeltaReader.ListScanFilesJson` (engineered-wood's
  EXACT active `add` files as JSON `[{"path":<uri>}]`, onelake:// for OneLake) → a `SimpleMultiFileList`; DuckDB's
  **native parquet MultiFileReader** reads them (cached). The C++ MultiFileReader foundation for DV / partition /
  dynamic-filter pushdown (later slices 1b–1e); `parquet` statically linked (extension_config.cmake). Live/local:
  `test/verify_delta_mfr_scan.test` (36, matches the C# reader). **Slice 1b DONE (deletion vectors, no ABI bump):** <!-- check-docs:ignore (REMOVED at ABI v75; naming it IS the point) -->
  `delta_list_files` emits per file the deleted row positions (`"dv":[…]`, via engineered-wood's
  `DeletionVectorReader`); C++ gained a custom `FabricatorDeltaMultiFileList` (per-file DV) + `InitializeGlobalState`
  override + `FinalizeBind` attaching an `FabricatorDeltaDeleteFilter` → DuckDB's native read EXCLUDES deleted rows
  (`test/verify_delta_mfr_dv.test`, 23). Gotchas: the DeleteFilter must `result_sel.Initialize(STANDARD_VECTOR_SIZE)` <!-- check-docs:ignore (REMOVED at ABI v75; naming it IS the point) -->
  before writing (reader passes a null sel_vector → else segfault); **bare `count(*)` over-counts on a DV table**
  (empty-projection parquet-metadata count path skips the DeleteFilter — use a column scan; follow-up can disable
  it). **1c (partition) + 1d (filter pushdown) LARGELY ALREADY WORK via the inherited parquet_scan (verified):**
  `fabricator_delta_mfr_scan` clones parquet_scan → inherits **filter pushdown** (EXPLAIN shows `Filters:` INSIDE the <!-- check-docs:ignore (REMOVED at ABI v75; naming it IS the point) -->
  scan → static + dynamic filters prune row-groups natively, no custom Complex/DynamicFilterPushdown) + **hive
  partitioning** (engineered-wood's `<col>=<value>/` layout → `region` resolves from the path; verified on a
  PARTITIONED BY table). So 1a+1b + inherited features = a nearly complete native read (reader + projection +
  filter/row-group pushdown + hive partitions + parallelism + ExternalFileCache + DV). **Remaining:** 1d-file =
  Delta-log FILE-level pruning (optimization over row-group pruning; needs an engineered-wood prune-by-predicate
  API), 1c-edge = log-authoritative partition injection for typed/NULL edge cases, the bare-count(*)-on-DV fix.
  **1e slice 1 DONE (2026-07-03, C#-only, NO ABI/C++) — native read folded into the ATTACH catalog:** the Delta
  folder-catalog ATTACH option **`native_read true`** routes a plain `SELECT … FROM lake.main.t` through DuckDB's
  own parquet reader — `DeltaCatalog.ScanTable`'s plain-read branch runs `Host.Query("SELECT * FROM
  read_parquet([<exact active files>])")` (files via `DeltaReader.GetActiveFileUrisWithDv`; tuned decode +
  cross-file parallelism + ExternalFileCache, over `onelake://` for OneLake) instead of engineered-wood's C#
  reader. **NOT the C++ MultiFileReader-from-the-catalog fold** (that needs a pre-bound parquet scan out of
  `GetScanFunction` — `TableFunctionBindInput` wants a live `Binder`+`TableFunctionRef` the catalog entry lacks,
  fragile/DuckDB-coupled); routing through the C# `ScanTable` seam keeps all catalog plumbing (three-part names,
  stats, DML, time travel) intact and is a pure byte-source switch. **Opt-in (default off) + read-only:** a rowid
  scan (UPDATE/DELETE), a time-travel scan (`AT`), or a DV table transparently falls back to the C# reader (no
  DeleteFilter/rowid/snapshot on the native path) — `test/verify_delta_catalog_native_read.test` (53: read/
  projection/filter/aggregate/multi-file append + DELETE/UPDATE via the rowid C# reader + DV fallback). **Caveats:**
  the pushed FILTER is not yet translated into the host SQL (no Delta-log/row-group pruning on this path — the C#
  reader still prunes), projection is left to DuckDB above the scan, and the bind-time COLUMNS schema must match
  `read_parquet` BY NAME (holds for engineered-wood/Spark plain tables; decimals align at `Decimal128`). **Remaining
  (heavier follow-up) = the full MultiFileReader-in-catalog fold** (native DeleteFilter for DV, dynamic join/TopN
  filter pushdown into row-groups, native rowid via `file_row_number`+path-sorted ordinal for DML, native time
  travel). **Direct-fold design decisions captured (docs/multifile-delta.md §"Native-read fold"):** (a) **rowid is
  a VIRTUAL COLUMN** (duckdb-delta sets `function.get_virtual_columns` declaring `file_row_number`/`rowid`/
  `delta_file_number`) requested at scan-init, NOT a bind-time decision → native DML is achievable directly; our
  transient rowid `(fileOrdinal<<40)|file_row_number` = `file_list_idx<<40 | file_row_number` in `FinalizeChunk`,
  needing a **relative-path-sorted** file list to match engineered-wood's `OrderedActiveFiles` ordinal; (b)
  **snapshot consistency across a multi-table join** — capture one UTC instant per DuckDB transaction
  (`AmbientTransaction`/`TxnState`, v35) and pass it as an implicit `AT (TIMESTAMP)` into `catalog_list_scan_files`
  (explicit `AT` overrides; resolved via always-on `commitInfo.timestamp`; cache the resolved version per
  (txn,table)); (c) **dynamic filters + parallelism** inherited from `parquet_scan` for free — `global_state.filters`
  is a live pointer applied per-file-at-open, so dynamic filters only prune NOT-YET-opened files and a very high
  thread count opens files before the join filter materializes (parallelism-vs-late-filter trade-off, self-bounded by
  the #threads look-ahead); (d) **S3/MinIO write** works with an S3 secret + NO opener conflict (`_fabricCredential`
  null → host-FS + opener secret), caveats: httpfs must be linked for tests, EXCLUSIVE_CREATE (commit put-if-absent)
  may not be enforced on S3 httpfs (concurrent-writer safety), DROP/RENAME dir-ops may be unimplemented on the S3 FS.
  **DECISION (2026-07-03): the pure-C# native reader is the target path, NOT the C++ MFR fold.** Analysis showed the
  MFR's advantages all replicate in a C#-orchestrates-`read_parquet`-via-`Host.Query` reader for the cloud target:
  native decode + cache (read_parquet), rowid/DML (`(ordinal<<40)|file_row_number` in SQL, no `get_virtual_columns`),
  DV (drop positions per file), projection + static filter (into read_parquet WHERE), Delta-log file pruning +
  early-stop (per-file loop), and even **dynamic (join) filters are NOT MFR-exclusive** (they flow to any
  `filter_pushdown=true` table function via `input.filters`, `physical_table_scan.cpp:35-36`; mid-scan-materializing
  filters only prune the not-yet-opened tail even in the MFR, closable in C# via a per-file live-filter callback).
  The ONLY non-replicable MFR edge = **downstream multi-lane parallelism** (one Arrow stream = one `get_next` lane),
  relevant for CPU-bound-local star-schema joins, **secondary for cloud I/O**, and **additive later** (partition the
  file list into N per-thread streams — doesn't touch rowid/DV/filter, ordinal stays global-path-sorted). Plan
  (docs/multifile-delta.md §"Concrete plan"): grow the `native_read` branch into a per-file loop
  (prefetch/bounded-channel ≈ threads) with `file_row_number` rowid/DML + DV + projection + static filter + log
  file-pruning (slice 1, C#-only), optional live-filter host-callback for dynamic pruning (slice 2), multi-lane
  parallelism (slice 3). Build the C++ `fabricator_delta_mfr_scan` MFR (slices 1a/1b done, standalone) only if <!-- check-docs:ignore (REMOVED at ABI v75; naming it IS the point) -->
  CPU-bound-local multi-lane becomes a goal. **Slice 1 DONE (2026-07-03, C#-only, no ABI):** `DeltaNativeReader`
  is a **per-file loop** (`FABRICATOR_DELTA_PREFETCH`, default 1 = sequential, >1 = concurrent file fetch) emitting
  per file `SELECT <proj>[, ((ord::BIGINT<<40)|file_row_number) AS "_metadata.row_id"] FROM read_parquet(<file>,
  file_row_number => true) [WHERE <static> [AND file_row_number NOT IN (dv)]]` via `Host.Query` — so plain SELECT,
  **DELETE/UPDATE (native rowid via file_row_number, no C#-reader fallback)**, DV exclusion, projection, static
  filter + **Delta-log FILE pruning** (`DeltaFilePruner`, now `public`), and time travel (`AT VERSION/TIMESTAMP`)
  all run natively. File list **relative-path-sorted** for `OrderedActiveFiles` rowid-decode parity; output schema
  **probed** (`read_parquet … LIMIT 0`) to match batches by type. **Snapshot consistency DONE:** `SnapshotPinning`
  captures one UTC instant per DuckDB transaction (`AmbientTransaction`, keyed on the txn id — fires per statement
  in autocommit, once per explicit `BEGIN`) → resolves+pins the version per (txn,table) via
  `DeltaReader.ResolveVersionAsOf` (commitInfo.timestamp; falls back to latest), so a multi-table join reads a
  consistent cut. **Logging DONE (ILogger, C#-only):** `FabricatorLog` (off by default; `FABRICATOR_LOG_LEVEL` +
  `FABRICATOR_LOG_FILE` → file sink; factory pluggable for a future DuckDB-forwarding provider) traces the resolved
  snapshot version, file list (active/scanned/pruned), and each per-file `read_parquet` SQL. Verified:
  `test/verify_delta_catalog_native_read.test` (66); Delta write/delete/update/decimal/time_travel/changes
  unregressed. **Slice 2 DONE (dynamic/join filter pushdown, C++/C#, NO ABI — the predicted per-file host
  callback was unnecessary):** a hash join builds before the probe scan inits, so the runtime filter set is
  rendered ONCE at `ArrowStreamInitGlobal`. A C#-declared catalog capability (`DeltaCatalog` →
  `exact_filter_pushdown=true` on `ServerInfo`, ONLY under `native_read`) → C++ `FetchExactFilterPushdown` →
  `BuildScanFunction` sets `function.filter_pushdown=true` for the native catalog ONLY (SQL Server / DAX /
  non-native Delta stay false → unchanged, verified). `arrow_ingest::RenderLiveFilters`/`RenderTableFilter` walk
  the live `TableFilterSet` (keys→names as `PhysicalTableScan::GetFilterInfo`), emitting exact DuckDB SQL: unwrap
  `OptionalFilter`, resolve `DynamicFilter` under its lock (skip if not `initialized`), skip bare `BLOOM` (all
  three are pruning-only — the join re-applies — so skip-safe), recurse `CONJUNCTION` per child (not `ToString`,
  which leaks `optional:`), else `TableFilter::ToString`; merged into `spec_json.native_filter` (slice-1 channel).
  Mandatory erased static filters render 1:1 (read_parquet target IS DuckDB) → correct; bonus: string `<>`/ordering
  now pushes exactly on the native path. `test/verify_delta_catalog_dynamic_filter.test` (21) + native_read (66) /
  delete (28) / update (63) / partition (54) / dv (48) / time_travel (48) + SQL Server pushdown suites green.
  Nuance: a mid-scan-refined dynamic filter (TopN) is captured only as of scan-init (best-effort). **Remaining:
  slice 3** (multi-lane, deferred by user).
  **STRUCT-member filter pushdown/PRUNING DONE (2026-07-06).** `WHERE (s).a = 5` (arrives at
  `pushdown_complex_filter` as a `struct_extract` `BoundFunctionExpression` chain — verified in DuckDB source;
  the ColumnIndex-with-children rewrite is projections-only and runs AFTER filter pushdown) is now serialized
  as a **dotted-path FilterNode** (`"path":["s","a"]`, `col` null — a renderer without path support throws →
  its caller falls back to no pushdown, superset-safe; SQL Server/DAX can't have struct columns) and drives
  engineered-wood **file-level pruning via nested Delta stats + parquet row-group/bloom pruning**:
  `FilterSerializer::ColumnPath` unwraps `struct_extract`/`struct_extract_at` (member names resolved from the
  child struct type via the bound `StructExtractBindData` index — exact case), all shapes (compare / IS NULL /
  IN / BETWEEN / conjunctions); `DeltaFilterBuilder` joins the path to EW's dotted convention ("s.a") which the
  parquet `StatisticsAccessor`/bloom probe ALREADY resolve natively (`ColumnDescriptor.DottedPath`); EW
  `ColumnStats.Parse` now FLATTENS nested minValues/maxValues/nullCount into dotted keys (nested nullCount was
  previously dropped at parse) + `DeltaFilePruner` registers struct leaves dotted (dual logical|physical under
  column mapping; a literal dotted column name colliding with a struct path is POISONED — no pruning, never a
  guess; EW `NestedStatsPruningTests` 6). **Nested nodes emit NO native-SQL twin** (JSON-only): the rendered
  logical member access would mis-bind inside `read_parquet` on a column-mapped table (physical child names) —
  so the Conjunction/top-level SQL joins tolerate empty per-part SQL (AND skips = superset; OR drops its whole
  SQL twin). **Exact mode**: the erased StructFilter renders as proper `struct_extract("s",'name')` SQL (new
  `RenderTableFilter` STRUCT_EXTRACT case — the bare `ToString` dot form mis-quotes exotic names); works on the
  codec path (HostBatchFilter runs over logically-renamed batches → mapped tables fine), unmapped native, AND
  **column-mapped native_read via the LOGICAL STRUCT REBUILD** (second pass, same day): `DeltaNativeReader`'s
  per-file inner subquery rebuilds each mapped struct column with logical member names — `CASE WHEN "col-s" IS
  NULL THEN NULL ELSE struct_pack("a" := ("col-s")."col-a", …) END AS "s"` (recursive; the CASE preserves
  NULL-struct semantics — bare struct_pack would materialize a non-NULL struct of NULLs; quoted named args
  verified). Child names resolve per level via `StoredChildName`: **id mode through THIS file's parquet
  field_ids** (`FileMapping.FieldIdToName` — the pre-existing per-file footer probe, reads every vintage incl.
  old EW id-files that stored logical names) else the declared `physicalName` (name mode), else logical.
  `NeedsRebuild` skips no-op trees (unmapped/equal names keep the plain alias + parquet filter pushdown);
  list/map members pass through unrebuilt (per-batch `ArrowColumnMappingRename` still fixes their inner names —
  its tolerant either-name matching makes it a no-op on rebuilt columns). `DeltaSqlFilter` also renders path
  nodes as `struct_extract` chains (a nested-only WHERE now applies in-query on the static native path too).
  **DuckDB's `supports_pushdown_type` veto was tried and REJECTED** (would pull
  struct filters back out of the scan): it requires `filter_prune`-maintained projection_ids and DuckDB's veto
  path (plan_get.cpp) corrupts rowid DML plans (RemoveUnusedColumns early-outs on everything_referenced →
  empty projection_ids → the veto's append turns identity into a 1-column projection) and crashes on rowid
  entries (`virtual_columns.at` reads the FUNCTION-level hook, not the entry's) — upstream only pairs it with
  the DML-less arrow scan; upstream-report candidate. `ArrowStreamInitGlobal` now honors `input.projection_ids`
  (the filter_prune contract, currently always empty=identity — defensive). **CRUX BUG FIXED en route (latent,
  affected ALL exact-mode scans): `pushdown_complex_filter` is re-invoked by a later optimizer round with an
  EMPTY list once the first round's predicates were erased into the TableFilterSet — the callback's
  clear-on-entry WIPED `filter_json`, silently forfeiting Delta file pruning on every exact-mode
  (native_read / `pushdown_filters 'all'`) scan.** Now an empty re-invocation KEEPS the previous serialization
  (still the query's own predicates → pruning stays superset-correct); proven: `delta native list … pruned=1`
  on a nested predicate. `test/verify_delta_catalog_struct_filter.test` (67 — all predicate shapes, two-level
  nesting, cross-mode agreement none/static/exact, exact-mode DML with struct WHERE, **name- AND id-mapped
  native_read struct filters via the rebuild** incl. NULL-struct semantics, unmapped native_read, plain +
  mapped codec); delta suite 42/42 + SQL Server suites green.
  **DYNAMIC-filter I/O PRUNING DONE (2026-07-06, third pass — C++-only, no ABI).** The live TableFilterSet
  render at scan init now has a SECOND, structured channel: `SerializeLiveFilters` (arrow_ingest.cpp, beside
  `RenderLiveFilters`) converts the erased statics + **materialized dynamic/join filters** into a FilterNode
  JSON tree — AND-merged with the bind-time `filter_json` in `BuildScanSpec` (both are true predicates;
  duplicated statics are idempotent for pruning) — so a hash-join's runtime bounds now drive engineered-wood
  **Delta file pruning + parquet row-group/bloom skipping** on BOTH Delta paths (previously dynamics reached
  only the SQL channel = read_parquet row-groups on native; the codec path got nothing). Handles
  Constant/IsNull/In/Conjunction/Optional/Dynamic(under its lock)/StructFilter(→ path nodes); OR is
  all-or-nothing with constants staged in a scratch vector so a dropped branch can't leak indices; constants
  extend a **PER-EXECUTION copy** of the bind constants (bind data is shared across executions — never
  mutated); string-ordering gate honored; TOP/ORDER-BY spec emission now also gated on the live JSON (a
  pushed TOP over a superset-filtered scan could drop rows). Proven: JOIN probe scan logs `active=2 scanned=1
  pruned=1` (the out-of-range file skipped BEFORE any I/O) with the IN + min/max bounds also in the WHERE.
  SQL Server/DAX unaffected (input.filters exists only under exact-mode filter_pushdown). Same pass: the **6
  EW Parquet.Tests decimal assertions rewritten for the committed Decimal128 widening** (read tests assert
  Decimal128Type/Array with preserved precision/scale; roundtrips write narrow → read wide, values lossless)
  — EW Parquet.Tests now 573/585, the remaining 12 = the pre-existing ALP bit-exact + parquet-testing sweep
  failures (separate triage).
  **ROWID FAST PATH + LATE MATERIALIZATION — DONE (2026-07-13, C++/C#, no ABI).** The `native_read` Delta
  scan opts into DuckDB's late-materialization rewrite (`function.late_materialization = true` + the
  FUNCTION-level `get_row_id_columns` hook — distinct from the entry-level `GetRowIdColumns` that serves
  DML planning; the flag defaults FALSE and only DuckDB's own seq_scan/read_duckdb set it, so the rewrite
  never fired on extension scans before): `ORDER BY x LIMIT n` becomes TopN-on-a-narrow-scan + **SEMI join
  back on rowid** (JoinFilterPushdown supports SEMI → the tiny build side's dynamic min/max — and for small
  builds an IN-list — lands on the probe scan's rowid column via the exact-mode `input.filters`). Rowid
  filters (dynamic + user `WHERE rowid =/IN/range`) are now SERIALIZED — `SerializeLiveFilters`/
  `RenderLiveFilters` previously SKIPPED `COLUMN_IDENTIFIER_ROW_ID`, which in exact mode was a **latent
  correctness bug** (an ERASED static `WHERE rowid = X` was silently unapplied); they now resolve it to the
  single virtual rowid name (`_metadata.row_id`, gated `virtual_rowid_columns.size()==1`) — and **DECODED
  by the native reader** (`DeltaRowIdFilter`): the ordinal half (`rowid >> 40`) selects EXACTLY the matching
  files (no stats — the transient rowid is a LOCATOR, which is why `__delta_row_id`-style stats are not
  needed for fast access), the position half becomes a per-file `file_row_number` predicate which the
  parquet reader ROW-GROUP-prunes (verified: `ParquetColumnSchema::Stats` synthesizes exact per-row-group
  min/max for FILE_ROW_NUMBER). Rowid conjuncts are STRIPPED from the EW prune tree (no rowid stats;
  dropping an AND-conjunct widens = superset-safe) while the rendered SQL keeps them exact — the per-file
  SELECT aliases the rowid expression and DuckDB permits SELECT-alias references in WHERE. Enablement
  required **`ArrowStreamBindData::Copy`** (the rewrite clones the fetch-side get's bind data via
  `LateMaterializationHelper::CreateLHSGet`; `schema_root`/`arrow_table` are bind-time-only — nothing reads
  them after PopulateReturnSchema, scans build per-scan converters from the live stream — so the copy
  shares everything else and leaves them empty). Gated to `ExactFilterPushdown()` + virtual BIGINT rowid ⇒
  **Delta native_read only** (SQL Server/DAX deliberately off: their TopN pushdown is superior and a
  join-back would re-scan the server; entry-level rowid/DML unaffected). Fetch cost = O(matched files):
  LIMIT 1 → `files 3 -> 1` + `file_row_number IN (p)`. NOT used by DML — UPDATE/DELETE/MERGE plans scan
  ONCE with the WHERE pushed and rowids flow UP to the modify operator (no rowid-filtered re-scan exists
  there). Fixed en route: `WithPendingDeletes` dropped `PartitionColumns` from its listing rebuild (a
  partitioned table with buffered DELETEs read its partition column as NULL mid-txn under native_read).
  `test/verify_delta_late_materialization.test` (57 — TopN + prepared re-exec, point/IN/range/edge-bound/
  contradictory rowid lookups [layout-independent counts: file ordinals are path-sorted RANDOM uuids], DV
  composition, pending-file ordinals inside a txn, non-native catalog unaffected, duckdb_logs pins of
  `rowid prune … 3 -> 1` + `file_row_number IN`); regression: native_read 88 / dynamic_filter 21 /
  struct_filter 67 / update 63 / delete 28 / dv_default 58 / time_travel 48 / transactions 934 + SQL
  scalar/table-fn/pushdown suites green. `docker/provision.ps1` now also creates the shared read-only
  `dbo.TestSimplePK` fixture (7 suites read it; nothing re-created it after the compose migration).
  **STABLE ROW-TRACKING VIRTUAL COLUMNS — DONE (2026-07-13, additive metadata kind 12, NO ABI bump).**
  `SELECT __delta_row_id, __delta_row_commit_version FROM lake.s.t` works on **row-tracking tables under
  `native_read`** (the Delta materialized-column names; Spark's `_metadata.row_id`/`_metadata.
  row_commit_version` equivalents): queryable by name, EXCLUDED from `SELECT *`, per row =
  `COALESCE(materialized __delta_row_id column, baseRowId + file_row_number)` /
  `COALESCE(materialized version, defaultRowCommitVersion)` — distinct from the transient `rowid` (a
  locator), these are the durable identity. Mechanism = **generic provider virtual columns**:
  `FABRICATOR_META_VIRTUAL_COLUMNS = 12` (arg1/2 = schema/table → (name, type-text) rows; best-effort
  try/catch fetch in `GetOrCreateEntry`; every other provider returns empty) → the entry registers them in
  `GetVirtualColumns()` at `fabricator::ProviderVirtualBase()` (= `VIRTUAL_COLUMN_START + 0x100`; DuckDB's
  `TableBinding` maps virtual names for bare-name binding, and a REAL same-named column shadows the
  virtual) → `ArrowStreamBindData.provider_virtual_columns` → `BuildScanSpec` fetches by name,
  `BuildProjectionMapping` resolves 1:1 by name, and both live-filter serializers resolve virtual-id
  filters (exact-mode erased `WHERE __delta_row_id = k` applied exactly, like the rowid fix). C#:
  `DeltaCatalog` advertises the pair gated on `_nativeRead` + `delta.enableRowTracking` (flag cached per
  path by the Columns fetch via `GetSchemaAndRowTracking` — no extra `_delta_log` read on entry
  materialization, the OneLake cost concern); `NativeScanFile` carries `BaseRowId`/`CommitVersion` from
  the add actions; `DeltaNativeReader.RowTrackingExpr` renders the per-file expression (materialized
  presence via the existing footer probe). Semantics validated: append ids follow COMMIT order
  (deterministic); **buffered (explicit-txn) UPDATE preserves the id + bumps the version** (post-images
  bake materialized ids); DV DELETE removes the id; **OPTIMIZE preserves both** (compaction materializes);
  a txn's PENDING rows read NULL (baseRowId assigned at commit); non-tracked tables / codec catalogs
  don't bind the names (clean binder error). **Pinned known gap:** an AUTOCOMMIT UPDATE on a (default)
  column-mapped table takes the copy-on-write rewrite whose new add carries NO row tracking (the
  documented deferred P5-rewrite gap) — those rows read NULL until the EW rewrite materializes ids.
  NOTE: with `deletion_vectors` defaulting true (which enables rowTracking), most default-catalog tables
  are row-tracking → the columns bind broadly under native_read.
  `test/verify_delta_row_tracking_virtual.test` (87); regression: transactions 934 / row_tracking 33 /
  materialize 17 / compaction_rowtracking 24 / native_read 88 / dv_default 58 / update 63 /
  column_mapping 251 / late_materialization 57 + SQL scalar/table-fn/procs/orderby green (SQL/DAX/deltars/
  stub each gained an explicit empty VirtualColumns metadata case).
  **STABLE-ID FAST PATH — DONE (2026-07-14, `DeltaRowTrackingFilter`): filters on
  `__delta_row_id`/`__delta_row_commit_version` skip FILES + ROW GROUPS** (point lookups, dedup DELETEs
  without a unique key, `version > X` incremental extracts) — NO log-stats writes needed (the materialized
  column is off-schema; Spark writes no stats for it either, and neither do we). Per file, decided AFTER
  the footer probe every native scan already runs: **derived file** (no materialized column — every plain
  append) → the log alone bounds ids to `[baseRowId, baseRowId + stats.numRecords)` (`NativeScanFile.
  NumRecords` added) — file SKIPPED on no intersection, else the constraint rewrites to a
  `file_row_number` predicate (the same synthesized-zone-map machinery as the rowid path); the version is
  a per-file CONSTANT (`defaultRowCommitVersion`) — whole-file match/skip; **materialized file**
  (rewrites — ORIGINAL ids, decoupled from the fresh baseRowId, so the derived-range subtraction does NOT
  apply) → the constraint pushes onto the PHYSICAL column in the per-file INNER WHERE as
  `(pred(col) OR col IS NULL)` (single-column ⇒ parquet zone maps prune; the IS NULL arm keeps
  derived-fallback rows visible); files with NULL BaseRowId under a value constraint (pre-enablement adds,
  a txn's pending files) skip outright (the column reads NULL). Extraction = AND-reachable compare/IN
  conjuncts + SINGLE-COLUMN OR-of-equals (how the live serializer renders erased/dynamic IN filters);
  conjuncts stripped from the EW prune tree (no log stats — dropping widens). The condition sits on the
  INNER subquery over read_parquet (inner WHERE binds source columns before SELECT aliases → hits the raw
  physical column, not the COALESCE alias); superset-safe — the outer WHERE still applies the exact
  predicate. NOTE OPTIMIZE consumes fresh baseRowId space for the compacted add (HWM jumps), so
  post-compaction inserts get ids ABOVE the old range (pinned). Spark/OSS-delta have NO row-id skipping
  (docs+source checked — `_metadata.row_id` is computed post-scan, file-constant-only metadata predicates)
  — our stable-id lookups are now faster than Spark's on the same tables. Guidance stands: IDENTITY
  columns for lookup keys (standard stats everywhere); row-ids for correlation + keyless dedup.
  **CRITICAL PRE-EXISTING BUG FOUND + FIXED by the dedup test (C++, ALL providers):
  `DELETE … WHERE x [NOT] IN (subquery)` read the WRONG child column as the rowid** —
  `AppendModifyBatch` assumed "rowid = last column", but a mark-join DELETE plan has NO projection
  between FILTER and DELETE, so the child chunk ends with the BOOLEAN mark (crash
  `Vector::Reference … BIGINT referenced BOOLEAN`; a same-width plan could have deleted wrong rows).
  Fix mirrors upstream `DuckCatalog::PlanDelete`: the rowid position comes from
  `LogicalDelete::expressions[0]` (the bound row-identifier ref) → `FabricatorModifyTarget.
  rowid_child_index`; UPDATE keeps the last-column contract (its binder-built projection guarantees it,
  as upstream PhysicalUpdate assumes). `verify_delta_row_tracking_virtual.test` now 299 (fast-path
  sections: point/IN/range + version filters with duckdb_logs skip pins + the `file_row_number` rewrite
  pin, post-OPTIMIZE materialized-column pushdown pin, DELETE by id list, mark-join dedup DELETE);
  regression: transactions 934 / late_materialization 57 / native_read 88 / update 63 / delete 28 /
  dynamic_filter 21 / SQL scalar 26 green.**
  The MoR eligibility check in EW `UpdateByRowIdsAsync` still required `mappingMode == None` — a **stale
  gate**: the 2026-07-06 `ColumnMappingRecursive.ToPhysical` pass had already made `UpdateViaVectorsAsync`'s
  post-image append mapping-capable, but the caller's condition was never relaxed, so since `column_mapping
  'name'` became the default (same day) EVERY autocommit UPDATE on a default-created table silently took
  copy-on-write (full-file rewrite + the P5 row-tracking loss; unnoticed because the DV/MoR suites pin
  `column_mapping 'none'`). Now lifted: autocommit UPDATE on mapped (name AND id) DV tables is merge-on-read
  on both writer modes — kernel (delta-kernel) reads the commits exactly; the commit shape is
  remove+add(same path, DV, **baseRowId preserved**) + post-image add. **Two more EW fixes in the pass:**
  (1) the post-image add's stats were collected over the LOGICAL-named batches — now over the
  physical-renamed, pre-`__delta_row_id` batches (spec: stats keys are physical under mapping);
  (2) **materialized-source ids honored**: updating a row in a file that ITSELF carries materialized
  `__delta_row_id` (a compacted file, an earlier update's post-image) resolved the id as `baseRowId +
  position` = the file-LOCAL id — silently changing row identity post-OPTIMIZE (caught by the new virtual-
  columns pin: id 9 read 18 after OPTIMIZE→UPDATE). `ProcessFileBatchesAsync`/`ReadFileAsync` gained an
  optional `strippedRowIdsOut` collector (the strip already had the ids in hand) and `UpdateViaVectorsAsync`
  prefers the source's materialized value per row. **The BUFFERED path still has this post-OPTIMIZE caveat**
  (its read-back resolves via `OrderedActiveBaseRowIds` + ordinal arithmetic, no materialized-source read —
  follow-up). Remaining MoR gates (the rest of the full-matrix plan): partitions (slice 2 — route the append
  through `WriteDataFilesAsync` for the partition split), CDF (slice 3/4 — pre/post-image cdc emission;
  rows already in hand), type widening (validation-only). **PolyBase interop tables are unaffected** (DV
  off ⇒ MoR never engages; their protocol-1.0 CoW path is untouched). No memory-shape change: both UPDATE
  paths materialize one affected file at a time (pre-existing; MoR appends only the matched rows).
  Noted for separate triage (pre-existing, surfaced while probing): `(v).a` member access reads NULL on a
  `CREATE TABLE + INSERT CAST(… AS VARIANT)` shape while the full `v` value + kernel read are exact (both
  reader paths; before any UPDATE — not MoR-related; the variant suite's own dot-access pins pass).
  `verify_delta_row_tracking_virtual.test` now 93 (autocommit UPDATE on the mapped default table preserves
  `__delta_row_id` + bumps the version — incl. a post-OPTIMIZE source; the remaining-CoW pin moved to a
  PARTITIONED table); full delta sweep (update 63 / dv_default 58 / dv 48 / variant 133 / column_mapping
  251 / changes 73 / delete 28 / materialize 17 / compaction 24 / row_tracking 33 / native_write 147 /
  native_read 88 / nested_alter 100 / constraints 50 / partition 54 / time_travel 48 / transactions 934 /
  late_mat 57) + EW DeltaLake 168 & Table 147 (all TFMs) green.
  **MERGE-ON-READ UPDATE: PARTITION GATE LIFTED (slice 2, 2026-07-13, EW + one Bridge guard removal).**
  Partitioned DV tables now take merge-on-read too: `UpdateViaVectorsAsync`'s post-image append routes
  through **`WriteDataFilesAsync`** when the table is partitioned (partition split → Hive dirs + per-file
  physical-keyed partitionValues + per-file stats + the `IDataFileWriter` seam), and builds the `add`s from
  the returned `WrittenDataFile`s exactly as `CommitDataFilesAsync` does (`DeltaPath.Encode`, baseRowId
  sequencing per file, numRecords-fallback stats, HWM domainMetadata). **A SET of the PARTITION column just
  works** — the post-image row lands in its new partition's file (DV-delete in the old partition, add in the
  new; verified: `region=EU → APAC/` dir in the commit, identity preserved). **`WriteDataFilesAsync`'s
  `materializedRowIds` unpartitioned-only restriction is lifted** — the id column is attached BEFORE the
  partition split (rides the regrouping with its row), then strip→ToPhysical→re-append per partition file
  (out of stats) — which also lifted the Bridge's buffered `materialize_row_tracking × partitioned UPDATE`
  rejection (`EnsureBufferedDmlEligible`). MoR passes `identityValuesPreGenerated: true` (post-images carry
  their existing identity values; regeneration would reassign) and the MoR gate gained `!IsIcebergCompat`
  (WriteDataFilesAsync rejects iceberg; falls to CoW). Remaining MoR gates: **CDF** (slice 3/4 — the last
  CoW-with-row-tracking-loss shape, pinned) and type widening (validation-only). Unpartitioned MoR keeps the
  single-file bespoke append unchanged (slice-1-validated); the partitioned path writes one file per
  (post-image batch × partition) — small-file inefficiency only, OPTIMIZE compacts. Kernel-validated:
  unmapped partitioned MoR reads exactly (partition column included); mapped partitioned shows the known
  kernel partition-column-NULL quirk (all commits of such tables, not MoR-specific; Spark is the reference).
  `verify_delta_row_tracking_virtual.test` now 110 (partitioned MoR preservation + partition-key SET moves
  the row with identity intact + buffered partitioned materialize UPDATE + the CDF CoW-NULL pin); sweep:
  update/dv_default/dv/variant/column_mapping/changes/delete/materialize/compaction/row_tracking/
  native_write/native_read/partition 54/partition_overwrite 90/constraints/identity 38/transactions 934 +
  EW 168 & 147 green.
  **MERGE-ON-READ UPDATE: CDF GATE LIFTED (slice 3, 2026-07-13, EW-only).** Unpartitioned Change-Data-Feed
  tables now take merge-on-read too: `UpdateViaVectorsAsync` emits **`update_preimage`/`update_postimage`
  cdc files per affected file** — the exact copy-on-write shapes (pre = the matched OLD rows from the
  source batch, post = the substituted rows; `CdfWriter.WriteAsync` handles the physical rename + the
  unmapped `_change_type` column) — and since a commit carrying ANY cdc action is read cdc-ONLY, the DV
  re-add and the post-image add never double-count in the feed (verified: the update commit's feed is
  EXACTLY one pre + one post; the full feed = 5 inserts + 1 pre + 1 post). Row-tracking identity is
  preserved on CDF tables now too. The MoR gate is down to: DV-enabled AND NOT (CDF × PARTITIONED — the
  cdc partitionValues/column semantics corner, same as the buffered path, slice 4) AND no type widening
  AND not IcebergCompat. **The full matrix from the plan is now: mapping (name+id) ✓ / partitions ✓ /
  CDF-unpartitioned ✓ / identity ✓ / row tracking + materialize ✓ — autocommit AND explicit txn; remaining:
  CDF × partitioned (slice 4) + the type-widening validation pass.**
  `verify_delta_row_tracking_virtual.test` now 127 (CDF MoR preservation + the exact cdc-only feed pins +
  the CDF×partitioned CoW-NULL pin as the last remaining shape); changes 73 / update / dv_default / dv /
  variant / column_mapping / delete / materialize / row_tracking / native_write / partition /
  transactions 934 + EW 168 & 147 green.
  **FABRIC LIVE VALIDATION — FULL PASS (2026-07-13, workspace Test / LH; + ONE REAL EW FIX).** The whole
  2026-07-13 row-tracking/merge-on-read block validated on all three surfaces. Our provider created on
  OneLake: `lake.dbo.fabricator_rtdef` (pure defaults → mapped + DV + materialized row tracking, MoR UPDATE),
  `fabricator_rtpart` (partitioned MoR + partition-key SET US→APAC), `fabricator_rtcdf` (CDF MoR). **Spark
  (Livy, sparkprobe `rtmatrix`)**: reads every table with EXACT `_metadata.row_id`/`_metadata.
  row_commit_version` (id 2 preserved/ver 2; the APAC-moved row keeps id 4; mapped PARTITIONED partition
  column reads fine — Spark, unlike kernel), `table_changes` shows exactly pre+post for the MoR commit,
  and **Spark WROTE BACK** (UPDATE id=3 → Spark itself preserved row_id 3 honoring OUR materialized
  declaration; INSERT → fresh id from the continued HWM). **SQL endpoint** (after the usual metadata-sync
  lag): all three tables registered + queryable incl. post-Spark state. **Our read-back of Spark's write
  initially 404'd — a REAL pre-existing EW bug: on-disk (`storageType "u"`) deletion vectors were
  unreadable** (never exercised — EW only writes inline DVs; Spark's DV UPDATE writes `.bin` files). Three
  spec bugs in `DeletionVectorReader`: resolved to `_delta_log/` (spec: TABLE ROOT + the optional
  random-prefix DIRECTORY from pathOrInlineDv's leading chars), UUID built with .NET's little-endian
  `Guid(byte[])` (spec: canonical big-endian/Java rendering — now formatted by hand), and the offset slice
  ignored the on-disk framing (spec: `<dataSize: 4-byte BE int><bitmap><CRC-32>` with offset at the size
  field — now detected by the size-field match, bitmap extracted, CRC unverified; raw-slice fallback kept).
  With the fix our reader matches Spark's own view byte-for-byte through Spark's u-DV. dv 48 / dv_default
  58 / update 63 / delete 28 / changes 73 / native_read 88 unregressed; validation tables left on LH for
  inspection.
  **MERGE-ON-READ UPDATE: CDF × PARTITIONED LIFTED (slice 4 — the FULL MATRIX is complete) + ON-DISK
  DELETION VECTORS FIXED BOTH WAYS (2026-07-13, EW + Bridge guards).** The last MoR gate: cdc files now
  follow the DATA-FILE convention on partitioned tables — new `CdfWriter.WriteSplitAsync` splits the
  change rows by partition (rows arrive WITH partition columns from the read paths; `SplitByPartition`
  excludes them from the file bytes; per-file `partitionValues` physical-keyed under mapping), which also
  makes a partition-key SET's update_postimage land in its NEW partition's cdc file (feed shows pre=EU,
  post=APAC). ALL cdc emission sites route through it (MoR + CoW UPDATE pre/post, rowid + DV DELETE,
  predicate UpdateAsync, the buffered `WriteChangeDataFileAsync` wrapper — now returns the split list);
  `CdfReader.ReadCdcFileAsync` re-adds partition columns from the cdc action's partitionValues
  (presence-checked for legacy baked-in files; the file's `_change_type` column detached around
  AddPartitionColumns since Spark cdc files may mix change types per row). Bridge: the buffered
  partitioned-CDF guards lifted (`CdfEnabled` probe + `EnsureBufferedDmlEligible` keep only
  identity/IcebergCompat) — buffered INSERT+UPDATE+DELETE on a partitioned CDF table fuses into ONE
  commit with an exact feed. The MoR gate is now just: DV-enabled, not IcebergCompat, no type widening.
  **The full matrix (DV + mapping name/id + partitions + CDF + row tracking + identity, autocommit AND
  explicit txn) is COMPLETE**; the only shapes still falling to CoW (row tracking lost) are type-widened +
  IcebergCompat — neither creatable by this provider, so the CoW-NULL pin is retired.
  **On-disk ("u") deletion vectors — BOTH sides were spec-broken, found by the slice-4 gate + the Fabric
  write-back:** the READER fixes (table root + prefix dir, canonical big-endian UUID, the
  `<version><size BE><bitmap><CRC>` framing) landed in the Fabric-validation pass; this pass fixed the
  WRITER (`DeletionVectorWriter.CreateFileAsync` wrote a raw blob to `_delta_log/` under a
  little-endian-Guid name — internally consistent with the old reader, unreadable by Spark/kernel; any
  DELETE whose bitmap exceeded the 1 KB inline threshold has been writing these all along). Now writes the
  spec shape (table root, canonical name, framing + CRC-32 via System.IO.Hashing — new DeltaLake package
  ref); the reader keeps a LEGACY fallback (`_delta_log/` + little-endian name) so old tables read.
  **delta-kernel now reads our large-DV deletes exactly** (20k rows, 6667 scattered deletions → on-disk
  DV; count + predicate exact) — previously every external reader broke on them. VACUUM safety checked:
  it only sweeps `.parquet`, so live `.bin` DVs are never deleted (orphaned `.bin`s stay uncollected — the
  documented gap, now with real orphans possible).
  `verify_delta_row_tracking_virtual.test` now 165 (partitioned-CDF MoR preservation + the partition-move
  feed + the buffered fused feed); transactions 934 / changes 73 / dv 48 / dv_default 58 / update /
  delete / optimize / partition / partition_overwrite / column_mapping 251 / variant 133 / native_write
  147 / native_read 88 / time_travel / snapshots + EW 168 & 147 (all TFMs) green.
  **P5 REWRITE GAP CLOSED — COPY-ON-WRITE DELETE/UPDATE MATERIALIZE ROW-TRACKING IDS (2026-07-14, EW +
  Bridge rewriter; motivated by PolyBase-recipe tables synced to Fabric via SHORTCUT needing update-stable
  ids).** Both CoW loops (`DeleteByRowIdsAsync` survivors + `UpdateByRowIdsAsync` rewrites) now bake each
  row's ORIGINAL `__delta_row_id` + `__delta_row_commit_version` into the rewritten file (the compaction
  rule; an UPDATED row's version = the new commit) AND assign fresh `baseRowId`/`defaultRowCommitVersion`
  on the new add + the HWM domainMetadata (the old CoW add carried NONE — a spec gap). Per-row source =
  the source file's materialized value where present (chained rewrites carry through) else
  `baseRowId + originalPosition`; NULL when underivable (pre-row-tracking sources — readers then derive a
  fresh id for exactly that row; the materialized columns are written NULLABLE for this,
  `AddRowIdAndCommitVersionColumns(nullable:)`). **Two byte paths:** codec — `ReadFileAsync`/
  `ProcessFileBatchesAsync` gained a `strippedVersionsOut` collector (the version column was dropped by
  the schema reconcile with its VALUES lost) and the loops build row-aligned arrays from in-hand
  positions; native — **`IDataFileRewriter.ReadRewriteAsync` gained an optional `RowTrackingRewrite`
  record** (SourceBaseRowId, SourceDefaultCommitVersion, NewCommitVersion) and the Bridge
  `NativeParquetDataFileRewriter` projects the two columns IN SQL (`COALESCE(source materialized,
  base + file_row_number)`; the UPDATE join's CASE sets the new version on matched rows), returned
  TRAILING per the contract and detached around clean/ToPhysical EW-side (`DetachRowTrackingColumns`);
  stats collect over the detached user batches. Gated on the table's DECLARED materialized column
  (config-driven — old undeclared tables just get the fresh base/default spec fix). **VALIDATED:** smoke
  incl. the chained rewrite (2nd UPDATE of an already-rewritten file keeps ids + old versions); kernel
  reads the outputs; **Spark reads the OneLake CoW table's `_metadata.row_id` PRESERVED while showing the
  mechanics** (`base_row_id=12` fresh + shuffled `row_index`, yet row_id = original — the override
  working); **PolyBase OPENROWSET reads the rewritten file with BOTH extra columns exactly**
  (`verify_mssql_s3_polybase` §5c extended, 137 — the update-stable-ids × protocol-1.0 combination now
  COMPOSES; the id pin is RELATIVE (+1 between consecutive rows) since the persistent bucket's HWM grows
  across re-runs). `verify_delta_row_tracking_virtual` now 247 (CoW preservation on the PolyBase recipe,
  both writer modes, chained); full sweep green (delete 28 / update 63 / dv 48 / dv_default 58 / changes
  73 / native_write 147 / native_read 88 / column_mapping 251 / variant 133 / compaction 24 / materialize
  17 / row_tracking 33 / optimize 40 / partition 54 / constraints 50 / transactions 934) + EW 168 & 147.
  The ONLY remaining CoW-without-id-preservation shapes: type-widened + IcebergCompat (not creatable by
  this provider). **The BUFFERED-path post-OPTIMIZE caveat is CLOSED too (same pass):**
  `ReadRowsByRowIdsAsync` gained an optional `sourceRowTrackingOut` collector (one row-aligned entry per
  yielded batch: original id/version = the source's MATERIALIZED value else baseRowId + position — plain
  `long?[]` value arrays — trivially lifetime-free), threaded through
  the Bridge wrapper; `BufferUpdateRows` now takes its stableIds from the read-back (the
  `GetOrderedActiveBaseRowIds` ordinal-arithmetic call is gone from that path) — so a buffered UPDATE of
  a COMPACTED file bakes the ORIGINAL id (pinned: post-OPTIMIZE buffered UPDATE keeps `__delta_row_id=4`).
  ANY unresolvable row (pre-row-tracking source) disables materialization for the whole statement (fresh
  ids — never a wrong/colliding id; replaces the old `?? 0` flaw).
  **ROW TRACKING × PolyBase — WORKS (probed live + pinned 2026-07-13):** a `row_tracking true` table
  (writer v7, minReader stays 1) on the PolyBase recipe (`deletion_vectors false, column_mapping 'none'`)
  reads exactly via SQL Server OPENROWSET — INCLUDING after OPTIMIZE materializes the physical
  `__delta_row_id` column into the compacted file (the reader ignores extra parquet columns not in the
  Delta schema). So the full SQL-Server-facing lifecycle is: CoW DML + identity + CDF + row tracking all
  OK; only DV (reader v3) and column mapping are hard rejections. `verify_mssql_s3_polybase` §5c (129).
  **Slice-4 LIVE VALIDATION — FULL PASS (2026-07-13, workspace Test / LH):** our provider created on
  OneLake `lakecdf.dbo.fabricator_rtcdfp` (partitioned CDF + row tracking: MoR UPDATE, partition-key SET
  US→APAC, DV DELETE) and `lake.dbo.fabricator_bigdv` (20k rows, scattered DELETE → the new SPEC-SHAPED
  ON-DISK DV over OneLake). **Spark** (sparkprobe `dvcdfp`): reads the on-disk DV exactly (13333/1/19999,
  zero deleted ids) and `table_changes` returns the partitioned MoR feed byte-identical to ours (per-
  partition cdc incl. the EU→APAC pre/post pair + the delete; `_metadata.row_id` preserved). **SQL
  endpoint**: reads BOTH tables exactly — including decoding the on-disk `.bin` deletion vector (the
  endpoint's DV support covers our inline AND on-disk forms) and the partitioned-CDF post-DML state.
  **ROW TRACKING NOW IMPLIES MATERIALIZATION — `materialize_row_tracking` DROPPED (2026-07-13, C#-only).**
  Spark parity: `delta.enableRowTracking` promises ids stable across rewrites, implemented via the
  materialized columns (Spark auto-declares them at enablement) — our opt-in split was Fabric-conversion
  caution that's since been validated away. Now `DeltaWriter.CreateConfig` declares
  `delta.rowTracking.materializedRowIdColumnName`/`...RowCommitVersionColumnName` WHENEVER row tracking is
  on (standalone `row_tracking true` OR the `deletion_vectors` default) — so **default-created tables
  preserve `__delta_row_id` across UPDATE/OPTIMIZE out of the box** (verified: pure-default catalog, id
  preserved + version bumped, kernel-exact). The ATTACH option is REMOVED (an old `materialize_row_tracking
  true` in an ATTACH is silently ignored — providers parse known keys only); the buffered-UPDATE bake gate
  switched from the catalog flag to the TABLE's declared materialized column (`TxnDmlProfile` gained
  `MaterializeRowIds`) — so tables created by OLDER versions without the declaration keep their old behavior
  (config-driven, never an undeclared physical column). Appends still never materialize (readers derive
  baseRowId + position). Tests updated (the option stripped from materialize_rowtracking 17 /
  compaction_rowtracking 24 / transactions §34-§36 / row_tracking_virtual 127 — all pass on the implied
  default); sweep green (update / dv_default / dv / variant / column_mapping 251 / changes / delete /
  native_write 147 / native_write_streaming 29 / native_read / partition / partition_overwrite 90 /
  optimize 40 / identity / constraints / copy_format 96 / transactions 934 / row_tracking 33).
  **COLUMN MAPPING on the native read — DONE (2026-07-05, C#-only, no ABI; live Fabric-Spark-validated).** A
  column-mapping Delta table (`delta.columnMapping.mode` = `name` or `id`) stores columns under PHYSICAL names
  (`col-<guid>`), so the plain `read_parquet` SELECT-by-logical-name failed (*"Referenced column 'id' not found;
  candidates: col-…"*). Fixed by mimicking the MultiFileReader's identifier mapping in the C# reader: `DeltaReader`
  captures logical→physical from THIS snapshot's field metadata (`delta.columnMapping.physicalName`, read DIRECTLY
  — `ColumnMapping.GetPhysicalName` returns the logical name in id mode since EW matches id mode by field-id, but
  Delta writes a physicalName in BOTH modes and Spark stores the parquet columns under it) onto `NativeScanList.
  LogicalToPhysical`; `DeltaNativeReader.FileSql` then reads through an **inner alias subquery** `(SELECT "phys" AS
  "logical", …, file_row_number FROM read_parquet(…))` so the OUTER projection, user filter (incl. filters on a
  mapped/renamed column), rowid, and DV condition all reference logical names unchanged — no filter rewrite, and
  DuckDB pushes the outer filter down into `read_parquet` (mapped to the physical column) for row-group pruning.
  Validated LIVE on Fabric Spark-created tables (`fabricator_cm_name` name mode + `fabricator_cm_id` id mode, each with
  a post-create column RENAME so logical≠physical): both read correctly via `native_read`, incl. filter on the
  mapped column + aggregation. The default (EW) reader already handled both modes (`RenameColumns` for name /
  `RenameByFieldId` for id) — confirmed on the same tables. **Top-level columns only** (a nested mapped column
  would need field-id matching via `parquet_schema.field_id` — deferred, clean follow-up mirroring
  duckdb-delta's `MultiFileColumnMapper`). Spark-created via the `scratchpad/sparkprobe createcolmap` harness.
  **CREATING a column-mapping table — the `column_mapping 'name'` ATTACH option — DONE (2026-07-05; live
  Fabric-Spark round-trip).** `ATTACH … (PROVIDER 'delta', column_mapping 'name')` → tables CREATED in the catalog
  enable Delta column mapping (`DeltaCatalog._columnMappingMode` → `DeltaWriter.Create`/`Write` → EW
  `OpenOrCreateAsync(columnMappingMode:)` → `CreateAsync` assigns physical `col-<guid>` names + bumps the protocol).
  Two EW fixes made the created table Spark-readable (our lenient reader tolerated the gaps; Spark didn't):
  (1) `CreateAsync` now DECLARES `columnMapping` in BOTH the reader + writer feature lists when in table-features
  mode (reader v3 / writer v7 — forced by the DV default), else Spark throws `DELTA_FEATURES_PROTOCOL_METADATA_MISMATCH`;
  (2) `DeltaSchemaSerializer` emits `delta.columnMapping.id` as a JSON NUMBER (Delta field-id is numeric — Spark's
  `Metadata.getLong` threw "String cannot be cast to Long" on the old string form). The write rides the existing
  EW-codec `RenameToPhysical` path.
  **COLUMN MAPPING IS THE DEFAULT — `column_mapping 'name'` (DEFAULT since 2026-07-06) | `'id'` | `'none'`.**
  Tables CREATED in a Delta catalog default to **name-mode column mapping**, so `ALTER TABLE … RENAME
  COLUMN` / `DROP COLUMN` work out of the box as **metadata-only commits** (no rewrite) AND the table stays
  readable by the **Fabric T-SQL endpoint — which REJECTS id-mode tables outright**
  (`UnsupportedColumnMappingMode`, user-validated live 2026-07-06; Spark/kernel/DuckDB read both modes).
  The default was `id` from 2026-07-05 until this finding; id stays opt-in for its field-id resolution.
  Opt out with `column_mapping 'none'` for a plain minimal-protocol table.
  (`verify_delta_catalog_column_mapping.test` asserts the name default; the explicit-id sections keep
  id-mode coverage.) The original id-default rationale + mechanics (all mode-agnostic — physical names +
  field_ids are written in BOTH modes):
  - **EW spec fix — id-mode files now use PHYSICAL names** (the earlier "id writes logical names" behavior was an
    EW spec violation: the Delta protocol requires physical names + parquet `field_id` in BOTH modes — Spark's
    id-mode files are `col-<guid>`+ids). `ColumnMapping.GetPhysicalName`/`BuildLogical↔PhysicalMap`/
    `FindFieldIdForArrowField` are now mode-agnostic (mapping-on ⇒ physical), so every EW write path (codec write,
    copy-on-write rewrite, DV update) emits physical names + field_ids for id mode too. **Proven with the
    reference reader**: DuckDB's official `delta_scan` (delta-kernel-rs — what Spark/Fabric use) reads our
    default-id tables correctly incl. after RENAME+ADD+DROP (it read ALL-NULLs on the old logical-named layout —
    the bug that triggered this pass). The old `'id'-REJECTED` note above is SUPERSEDED.
  - **Field-id native read**: `NativeScanList.LogicalToFieldId` (id mode; `LogicalToPhysical` stays for name
    mode) + `DeltaNativeReader.LogToPhys` probes each file's `parquet_schema` (`field_id → physical name`,
    footer-only) and composes logical→field_id→physical for the alias subquery — per-file, so mixed-vintage files
    across a RENAME read correctly, and both EW-created and external-Spark id layouts work.
  - **RENAME/ADD/DROP COLUMN under mapping** (all metadata-only): EW `RenameColumnAsync` (keeps id+physicalName),
    `AddColumnAsync` now assigns a fresh id (`max(columnMapping.maxColumnId, schema-max)+1`) + physicalName +
    bumps maxColumnId, new `DropColumnAsync` (retires the id; partition columns + last column rejected);
    `BackfillMissingColumns` reconciles EXTRA columns away too (DROP) — not just missing (ADD). Plain tables:
    RENAME/DROP still cleanly rejected (would need a full rewrite); ADD works as before. Bridge:
    `DeltaCatalog.AlterTable` gained RenameColumn/DropColumn cases → `DeltaReader.RenameColumn`/`DropColumn`.
  - **`native_write` STREAMS mapping tables (both modes)**: the COPY projection aliases `"logical" AS "physical"`
    (`NativeParquetDataFileWriter.SelectList`) + stamps `FIELD_IDS {'<physical>': id}`; stats are typed/keyed by a
    physical-renamed schema (spec: stats keys are physical). EW `SupportsExternalDataFileCommit` now allows
    mapping (caller contract: physical names + field_ids + physical-keyed stats documented on the property);
    `TryWriteStreaming` passes `columnMappingMode` to OpenOrCreate (a NEW table is created WITH the mode), skips
    `SetSchemaAsync` for a same-shape mapping replace, and falls back to collect for partitioned-mapping or a
    mapping replace that CHANGES the schema. EW `SetSchemaAsync` now supports mapping (re-assigns fresh field ids
    continuing from maxColumnId — sound for REPLACE since the paired Overwrite removes the old files) with a
    LOGICAL-shape no-op compare (`LogicalSchemaString` — the SchemaString always differs by ids). **`CREATE OR
    REPLACE` with a changed schema works on mapping tables** (collect path).
  - **Compaction (OPTIMIZE) is mapping-aware**: `CompactionExecutor` widens against a physical-renamed target
    schema, reconciles each source batch to the current column set (`BackfillMissingColumns` — mixed ADD/DROP
    vintages compact correctly; also fixes plain-table compaction-after-ADD, a pre-existing hole), and rebuilds
    CLEAN + `SetParquetFieldIds` so the compacted file KEEPS the mapping identity (without this it read back
    all-NULL). **CDF is mapping-aware**: `CdfReader` renames physical→logical on inferred (data-file) + `_change_data`
    reads (`ReadChangesAsync` passes the current `physicalToLogical`).
  Validated: `test/verify_delta_catalog_column_mapping.test` (122 — name/id create+read, physical names + field_ids
  in the parquet for BOTH modes, RENAME across mixed-vintage files via default+native readers, default=id declares
  the mode + RENAME/ADD/DROP + OPTIMIZE/VACUUM round-trip, id+native_write streams with FIELD_IDS, 'none' opt-out,
  unknown-mode error); `verify_delta_catalog_alter.test` (116 — ADD/RENAME/DROP positive, TYPE rejected); FULL
  delta suite 34/34 + SQL Server function suites green. Raw-file-shape tests pinned `column_mapping 'none'`
  (native_write/streaming/dv_default/materialize_rowtracking/optimize/compaction_rowtracking/overwrite_merge/
  mfr_dv — they assert physical parquet layouts or use the non-mapping-aware C++ MFR spike). Live: OneLake
  default-id CTAS + RENAME + ADD + post-rename INSERT round-trip (`lake.dbo.fabricator_cmidlive`, native_write
  streaming).
  **PARTITIONED + mapping (second pass, same day) — STREAMS + full Spark round-trip.** Convention settled
  EMPIRICALLY against a Spark-created partitioned id-mode fixture (`fabricator_cm_part`, via `sparkprobe
  createpartcm` + `_delta_log` inspection): `metaData.partitionColumns` = LOGICAL names (updated by a
  partition-column RENAME), `add.partitionValues` keys = **PHYSICAL** names (stable across the rename — the
  reason they're physical), `add.path` = opaque (Spark writes no Hive dirs under mapping; paths are opaque to
  readers, so our physical-named Hive dirs are spec-fine). Implemented: EW `WriteCoreAsync` tracks
  partitionValues (+ dirs) by physical name under mapping; `RenameColumnAsync` also updates
  `metaData.partitionColumns` (without this a renamed partition column broke reads/writes/kernel);
  `PartitionValuesMatch` (replace_where file selection), `PartitionUtils.AddPartitionColumns` (read-side re-add)
  and `DeltaFilePruner` (partition pruning + stats) all do DUAL lookup (logical | physical key) so old
  logical-keyed EW commits, new physical-keyed ones, AND external Spark tables all resolve. Bridge:
  `TryWriteStreaming` no longer falls back for partitioned mapping — `RunCopyPartitioned` gets the aliased
  projection + `PARTITION_BY (<physical>)` + physical-keyed FIELD_IDS (partition cols excluded — not in the
  files) + physical stats schema; `RETURN_STATS.partition_keys` then come back physical = the committed
  partitionValues. Validated: local smoke (rename of the partition column mid-life, pruning), full delta suite
  34/34 (`verify_delta_catalog_column_mapping` 157 — incl. partitioned CTAS streams/physical dirs/prune/rename/
  durability; `verify_delta_catalog_partition` 54 unchanged), and LIVE both directions: **Spark reads our
  partitioned mapped table** (`fabricator_cmpartlive`, created via native_write streaming with BOTH a data and the
  partition column renamed) AND **our provider reads Spark's** `fabricator_cm_part` (values + agg + pruning on the
  renamed partition column). NOTE: DuckDB's official `delta_scan` (duckdb-delta/kernel) reads mapped PARTITIONED
  tables' partition column as NULL — a kernel-side integration gap (our layout matches Spark's own convention;
  Spark is the reference).
  **NESTED-type column mapping — DONE (2026-07-05, third pass; kernel-validated).** Nested (STRUCT) columns on a
  mapping table now follow the spec at EVERY level: data files store nested struct children under PHYSICAL
  `col-<guid>` names + field_ids (both were LOGICAL/id-less before → delta-kernel read nested children as
  all-NULL — the top-level bug one level down; the Delta metadata was already recursive via
  `AssignColumnMapping`). Per the user's direction the nested WRITE is **native (COPY) only**:
  - **`ArrowColumnMappingRename` (Bridge)** — the C#-side analog of duckdb-delta's `MultiFileColumnMapper`:
    recursive logical↔physical rename of an Arrow schema/batch/stream driven by the EW Delta schema
    (physicalName metadata), matching EITHER name at every level (tolerant of aliased/legacy sources). Arrays
    are rebuilt by re-wrapping `ArrayData` with the renamed type tree — zero copy. Structs recurse to any depth;
    lists/maps recurse into struct elements.
  - **WRITE (streaming COPY)**: `TryWriteStreaming` renames the input STREAM to physical (all levels — replacing
    the old top-level SQL alias) and stamps a RECURSIVE `FIELD_IDS` spec (`DeltaWriter.BuildFieldIdsSpec` —
    struct fields via DuckDB's `__duckdb_field_id` sentinel; list/map emit their own id as a leaf, struct-fields
    INSIDE list/map not yet stamped — follow-up). The **EW-codec (collect) path was GATED for nested+mapping (gate LIFTED 2026-07-06 via
    `ColumnMappingRecursive.ToPhysical` — see Known limitations)**
    (clear error → use `native_write` or `column_mapping 'none'`): EW's writer renames/stamps top-level only, so
    its nested files would be silently Spark-unreadable. Nested WITHOUT mapping stays allowed on both paths.
  - **READ**: the native reader renames nested children physical→logical per batch
    (`NativeScanList.MappedSchema`); the DEFAULT (EW) reader path gets the same recursive rename in
    `DeltaReader.Stream/StreamWithRowIds/StreamAt/StreamWithRowIdsAt` (EW's own `RenameColumns`/`RenameByFieldId`
    are top-level-only — upstream-fix candidate). AT-version streams rename per the AS-OF snapshot's schema.
  - **EW fix**: `LogicalSchemaString` strips mapping metadata RECURSIVELY — without it a fresh nested CTAS
    falsely "differed" in SetSchema's no-op compare and re-assigned every column id (the file/metadata id
    mismatch seen in the baseline probe).
  Verified: `verify_delta_catalog_column_mapping.test` (185 — nested CTAS/INSERT via native_write, member
  projection/predicate, physical names + 4 distinct field_ids in the parquet, struct-column RENAME reading old
  data, native_read nested, the EW-codec gate error); official `delta_scan` (delta-kernel) reads the nested
  mapped table incl. after the RENAME; full delta suite 35/35; live OneLake nested CTAS+RENAME+INSERT
  round-trip (`lake.dbo.fabricator_cmnested`) + Fabric Spark read.
  **BUG FIXED while stabilizing (latent, pre-existing): bulk-session double-complete.** `complete_bulk` CONSUMES
  the session handle managed-side EVEN WHEN IT RETURNS AN ERROR, but the COPY/CTAS/INSERT `Finalize` set
  `bulk_completed = true` only AFTER the call — a thrown provider error left the flag false, so the gstate
  destructor called `CompleteBulk(abort)` AGAIN on the freed value. GCHandle slots are RECYCLED → the second
  free killed an arbitrary unrelated live handle → intermittent (~1 in 4) `commit_transaction failed: stale
  catalog handle` on LATER statements (surfaced by the new PARTITION_OVERWRITE guardrail tests, which made an
  erroring complete_bulk a routine path). Fix: all three operators mark the session consumed BEFORE the call.
  Also `Bootstrap.RunTransactionOp` no longer falls back to `Active.OpenCatalog("")` on an unresolvable handle
  (nonsense "empty connection string" errors) — it now throws a real stale-handle diagnostic incl. what the
  handle resolves to.
  **Nested follow-ups CLOSED (same day, fourth pass):** (1) **CDF on nested mapped tables** — the feed leaked
  physical struct-child names → `DeltaReader.GetChanges` applies the recursive rename (CDF metadata columns pass
  through); (2) **DELETE on nested tables** — EW `DeletionVectorFilter.TakeRows` + `PartitionUtils.TakeRows`
  gained a recursive `StructArray` case (children indexed at parentOffset+r — `StructArray.Fields` does NOT
  incorporate the parent offset; validity rebuilt), so the DV-delete's CDC capture + copy-on-write survivor
  filtering work on struct columns (previously a clean throw); verified insert/insert/delete feed with logical
  nested names; (3) **struct-in-LIST field_ids** — `BuildFieldIdsSpec` renders
  `{'<phys>': {__duckdb_field_id: id, 'element': {<children>}}}` (DuckDB accepts an element dict without its own
  sentinel — the element node has no Delta id, unrepresentable in the Delta schema); list column + element-struct
  fields all carry physical names+ids, kernel-read verified; (4) **EW-codec stats keyed PHYSICAL under mapping**
  (`WriteCoreAsync` collects over the top-level-renamed batch — stats cover top-level primitives only, so the
  flat rename suffices; matches the streaming writer + spec readers' skipping).
  **STRUCT UPDATE + EW parquet-writer bug hunt (fifth pass, same day).** `UPDATE t SET s = {'a':…}` (struct SET
  values) now works on UNMAPPED tables via `ArrowValueReader.ReadScalarDeep` (struct → `Dictionary<string,object?>`
  recursive, deep-copied; kept SEPARATE from `ReadScalar` — filter callers rely on unsupported-type throws
  meaning "don't push") + a `BuildArray` StructType case (children built recursively, validity rebuilt). Works on
  both writers incl. `SET s = NULL`; MAPPED tables initially gated struct-SET (LIFTED 2026-07-06 — see Known
  limitations) (`DeltaReader.IsColumnMapped`
  — the EW-codec rewrite can't produce the spec nested layout; scalar SET on mapped nested tables works).
  Chasing kernel "Out of buffer" on the merge-on-read post-images uncovered **three EW parquet-WRITER bugs**
  (NOT documented limitations — known-issues.md's write-reject list never included struct/list/map; minimal
  repro harness `scratchpad/ewstruct`):
  1. **Null-struct child misalignment** (`NestedLevelWriter`): a null STRUCT row was treated like a null LIST
     (no child slot), but Arrow struct children are 1:1 with parent rows — every child value after a null
     struct row shifted one slot (def levels + values both wrong → file unreadable by DuckDB/kernel; only our
     lenient reader tolerated it). Fixed by threading an explicit per-level VALUE MAP from struct parents
     through struct/leaf/list/map decomposition (+ the sliced-struct case: children are NOT sliced with the
     parent, so the map bakes in `Data.Offset` — same subtlety as the TakeRows fix).
  2. **`ExpandArray` default 8-byte stride**: unknown fixed-width leaf types expanded as `long` — corrupting
     Int8/Int16/Date32/Time32/… whenever expansion triggered. Now width-dispatched; genuinely unsupported
     types throw instead of corrupting.
  3. **All-null pages declared a delta encoding with a 0-byte payload** — DELTA_BINARY_PACKED /
     DELTA_LENGTH_BYTE_ARRAY require a header even for zero values → readers underrun ("Out of buffer"). An
     all-null page now declares PLAIN (the only encoding whose empty representation is valid). This hit ANY
     all-null column page (e.g. a merge-on-read post-image whose struct is NULL, an all-null flat column page).
  **Plus the cheap known-issues interop fixes** (user: "if we can fix EW issues then fix"): ns-timestamp/time
  `converted_type` OMITTED (no ns variant exists — was mislabeled micros, 1000x for converted_type-trusting
  readers); `SchemaConverter.FromArrowField` PRESERVES per-field metadata (comments/mapping ids/invariants;
  `PARQUET:*` transport keys filtered); deprecated `Statistics.min`/`max` restricted to signed-order-safe types
  (parquet-mr parity — UTF-8/binary/unsigned/decimal-FLBA get `min_value`/`max_value` only); the stale
  known-issues `column_orders`-never-written entry removed (ColumnOrderBuilder already populates it);
  known-issues.md updated accordingly.
  Verified: repro trio (all-null / mixed / no-null structs) reads in DuckDB; struct UPDATE round-trips +
  kernel-reads on both writers; `verify_delta_catalog_column_mapping` 232 assertions; full delta suite 35/35 +
  SQL suites; EW's own parquet test suite.
  **Known limitations (2026-07-06 wrap-up):** a mapping REPLACE that CHANGES the schema now STREAMS
  (`TryWriteStreaming` adopts the new schema via `SetSchemaAsync` BEFORE building the maps/FIELD_IDS/COPY —
  metadata commit then streamed Overwrite, kernel-validated with a full schema swap incl. nested struct) and
  `ToArrowField` (Delta→Arrow) preserves per-field metadata (the reverse of `FromArrowField`). Deliberately
  REMAINING: binary partition values (clean error — the spec byte-escape encoding is not implemented; exotic),
  orphan DV `.bin` vacuum (we only write inline DVs — no `.bin` files to orphan). **Nested-field stats are
  BUILT (EW `850ffad`):** `StatsCollector` recurses into struct leaves — spec nested JSON objects for
  minValues/maxValues/nullCount; nullCount EXACT per leaf (parent-null OR child-null — IS NULL pruning needs
  exactness); min/max via the flat collectors (parent-null slots can only WIDEN bounds → superset, prune-safe);
  32-char string truncation at every level; physical-keyed at every level under mapping (the codec stats batch
  now goes through the recursive `ToPhysical`). Applies to the EW-codec/collect + rewrite/compaction paths;
  the streamed native COPY keeps DuckDB's `RETURN_STATS` (top-level — DuckDB reports no nested column stats;
  a follow-up if it ever does). Kernel reads the nested-stats tables fine.
  **Closed 2026-07-06:** (a) **map-of-struct field_ids on the COPY FIELD_IDS spec** — `BuildFieldIdsSpec`
  renders map fields as `{__duckdb_field_id: id, 'key': …, 'value': …}` (+ `AppendInnerNode` recursion for
  arbitrarily deep list/map-of-struct; structural element/key/value nodes carry no id, per DuckDB); validated:
  a `MAP(VARCHAR, STRUCT)` column under id mapping + native_write writes inner struct children physical-named
  with their ids (parquet_schema-verified) and kernel-reads. (b) **EW's own reader now renames nested levels**
  — `ColumnMappingRecursive.ToLogical` (the read direction of the same transform, no id stamping) is wired
  into EW `ReadFileAsync` (after the flat `RenameByFieldId`/`RenameColumns`), the `UpdateAsync` predicate
  read, and `CdfReader`'s three feed yield sites — EW standalone now returns logical nested names; the
  Bridge's `ArrowColumnMappingRename` read-side wraps stay as tolerant no-ops. **BOTH former gates are LIFTED (2026-07-06,
  kernel-validated, `column_mapping` test 251):** (a) **struct SET on MAPPED tables works** — new EW
  `ColumnMappingRecursive.ToPhysical` (recursive physical rename + PARQUET:field_id at EVERY level, tolerant
  either-name matching, zero-copy ArrayData rewrap — the EW-side sibling of the Bridge's
  `ArrowColumnMappingRename`) replaced the top-level-only `RenameToPhysical`+`SetParquetFieldIds` pair at all
  mapped write sites (CoW DELETE/UPDATE rewrites, UpdateViaVectors append, UpdateAsync, WriteCoreAsync codec,
  CdfWriter); PLUS the crux found in validation: EW hands the rewrite callback source batches with PHYSICAL
  nested child names (EW read rename is top-level only), so `BuildArray`'s logical-name carry-over of
  NON-updated rows read NULL — `DeltaReader.UpdateByRowIds` now wraps the callback to rename source batches
  to LOGICAL first (recursive, Bridge `ArrowColumnMappingRename`), EW's recursive ToPhysical converts back.
  Works across a column RENAME; pass-through rows keep values; kernel reads exact. (b) **the EW-codec
  (collect) nested+mapping write gate is lifted** — WriteCoreAsync now writes nested physical names + ids, so
  a nested CTAS/INSERT on a mapped table works WITHOUT `native_write` (kernel-validated). (CDC `_change_data`
  files are written PHYSICAL-named + field_id'd under mapping via the same recursive transform in `CdfWriter`;
  `_change_type` stays unmapped; Spark reads cdc parquet through the table mapping.)
  **WRITING to a Spark-created (external) table — DONE (2026-07-05, EW-only; live Fabric-Spark round-trip).** An
  INSERT initially failed at engineered-wood's `ProtocolVersions.ValidateWriteSupport`: *"unsupported writer
  features: [appendOnly, invariants]"*. Root cause (grounded in the table's `_delta_log` protocol): enabling
  `columnMapping`+`deletionVectors` upgrades the table to the writer-v7 "table features" protocol, which
  ENUMERATES the legacy writer-v2 features `appendOnly` + `invariants` EXPLICITLY — even though neither is ACTIVE
  (`delta.appendOnly` isn't set; no constraints declared). EW's allowlist just lacked the two names. Fix:
  (1) added `appendOnly`/`invariants`/`checkConstraints` to `SupportedWriterFeatures`; (2) new
  `DeltaTable.HonorWriterFeatures(isAppend)` (called from `WriteCoreAsync`/`CommitDataFilesAsync` + the rowid
  DELETE/UPDATE entrypoints) enforces them **only when active**: `appendOnly` → a non-append write throws when
  `delta.appendOnly=true`; `invariants`/`checkConstraints` (arbitrary SQL expressions in a column's
  `delta.invariants` metadata / `delta.constraints.*` config — which we can't evaluate in the C# writer) → the
  write is REJECTED with a clear error rather than silently writing possibly-violating data (Delta constraints
  are write-time-only; NOT NULL is schema nullability, unaffected). A table that merely LISTS the features (the
  common v7-upgrade case) writes normally. **Column-mapping WRITE rides the existing EW-codec collect path**
  (`WriteCoreAsync` → `RenameToPhysical` for name mode / `SetParquetFieldIds` for id mode) — no new alias/FIELD_IDS
  code needed. Validated LIVE: INSERT (4,'d'),(5,'e') into the Spark name-mode table `fabricator_cm_name` via our
  provider → committed v3 → **Spark reads all 5 rows back through the column mapping** (round-trip). Local Delta
  write/delete/update/dv/native_write suites unregressed (HonorWriterFeatures is a no-op on our own mode=none
  tables). **STREAMING native write to a mapping table stays on the collect path** (`SupportsExternalDataFileCommit`
  is false for mapping → `Materialize`+EW codec, correct but RAM-bounded); bounded-memory streaming to an EXTERNAL
  mapping table (COPY `FIELD_IDS` + physical-name alias) is deferred — niche (you bulk-write tables our provider
  creates, which stream; external mapping tables are typically read).
  v56 = **`onelake://` WRITE forward callbacks** — appended 3 vtable entries
  `onelake_open_write`/`onelake_write`/`onelake_close_write`; the C++ `FabricatorOneLakeFileSystem` `OpenFile(write)`/
  `Write` (sequential append → managed `OneLakeForwardFs` create/append/flush) make **`COPY … TO 'onelake://…'` +
  any DuckDB writer** write to OneLake (Phase-3 step-3 slice 2; live-validated: COPY a parquet, read back 5/5).
  `read_csv`/`read_json` reads already worked via the slice-1 OpenFile/Read path, so any reader/writer now works on
  OneLake. Read caching via DuckDB's `ExternalFileCache` was confirmed already transparent on the native
  `read_parquet('onelake://…')` path (`duckdb_external_file_cache()` shows the file cached). Non-sequential writes +
  directory ops throw; caching engineered-wood's reverse `fs_*` reads deferred (low value). v55 = **`onelake://` FileSystem forward callbacks** — appended 5 vtable entries
  `onelake_open`/`onelake_read`/`onelake_close`/`onelake_glob`/`onelake_exists` (host C++ → managed). A C++
  `FabricatorOneLakeFileSystem : FileSystem` (`src/fabricator/fabricator_onelake_fs.{hpp,cpp}`) is registered in DuckDB's
  VFS at load (`RegisterOneLakeFileSystem`, `CanHandleFile` = the `onelake://` scheme) and forwards its **read** ops
  to the managed Azure DataLake SDK (`OneLakeForwardFs`, reusing the step-2 `OneLakeDataLakeFileSystem` logic) — so
  DuckDB's **native parquet reader + ExternalFileCache** use OneLake uniformly, bypassing duckdb-azure. Credential =
  C++ resolves the azure secret from the calling opener (`SecretManager::LookupSecret(path,"azure")`, fallback to any
  `azure` secret since `onelake://` doesn't match azure's default scopes) → fields JSON → C# `FabricCredentialResolver`
  (empty ⇒ `DefaultAzureCredential`). This is Phase-3 step 3 of the OneLake-filesystem design
  ([docs/filesystem-bridge.md](docs/filesystem-bridge.md) §3): the C#→C++→C# path (OneLake IO logic in C#, registered
  as a C++ FileSystem, reached back in C#). **Slice 1 (read-only) DONE + live-validated on Fabric**:
  `SELECT count(*) FROM read_parquet('onelake://Test/LH_no_schema.Lakehouse/Tables/t/*.parquet')` reads OneLake Delta
  data files through DuckDB's native reader over the subsystem (no duckdb-azure); local regression green. Write ops on
  the C++ FS throw NotImplemented; slice 2 (caching the reverse `fs_*` reads, routing the Delta catalog through
  `onelake://`, `read_json`/`csv`/`excel`/`COPY TO`) deferred. v54 = **`SCHEMA_MODE` COPY option** — appended a `schema_mode` param to
  `begin_bulk` (nullable: "merge" | "overwrite"); the Delta provider does append+union / replace+adopt-schema,
  and `CREATE OR REPLACE` is now a true schema replace via engineered-wood `SetSchemaAsync`. Provider-agnostic —
  SQL Server / DAX ignore it. See the Delta partitioning/write bullet ("SCHEMA EVOLUTION on write"). v53 = **IDENTITY columns** — appended an `identity_columns` param (nullable,
  comma-separated) to `create_table` (begin_bulk NOT changed — CTAS has no generated columns; the auto-identity
  is a C#-side setting). DuckDB has no IDENTITY concept, so TWO mechanisms: **(1) a DuckDB GENERATED column**
  (`col BIGINT AS (0)`) is (mis)used as an IDENTITY MARKER — the C++ DDL `CreateTable` detects `col.Generated()`
  (binder allows generated columns on an attached catalog; no capability gate) and passes the name(s); the
  generated-ness exists only at create time (the table is re-fetched from SQL Server as a normal identity BIGINT
  afterward). **(2) an `add_identity` ATTACH option + `mssql_add_identity` SET** (`ResolveAddIdentity`: SET wins)
  auto-appends a `<table>_id BIGINT IDENTITY` surrogate key on CREATE + CTAS, skipped when a column is explicitly
  marked OR the target name already exists. C# `BuildCreateTable` emits **box `IDENTITY(1,1)` / Fabric bare
  `IDENTITY`** (Fabric supports only BIGINT IDENTITY, no seed/increment) via `IdentityClause(profile)`; identity
  columns are always BIGINT, no NULL/DEFAULT. The engine assigns values on INSERT — the identity column is absent
  from the source Arrow stream, so SqlBulkCopy's name-based `ColumnMappings` naturally skips it (works for
  explicit CREATE + INSERT and CTAS). The **read + insert paths already handle IDENTITY** (discovery +
  `KeepIdentity`), so only CREATE-side emission was new. Delta / DAX ignore `identity_columns`. Validated:
  `test/verify_identity.test` (45 — marker→IDENTITY [IsIdentity=1], add_identity SET, CTAS, skip-if-present,
  OFF=no column) + SqlServer/Delta suites unregressed; **live on Fabric Warehouse** (generated marker → real
  IDENTITY, 3 distinct auto values; add_identity CTAS → `fabricator_idfab2_id`, 4 distinct). v52 = **native `SORTED BY` → Fabric Warehouse `CLUSTER BY`** — appended a
  `sort_columns` param (nullable, comma-separated) to **both** `create_table` and `begin_bulk`, mirroring the v51
  `partition_columns`. DuckDB v1.5.4 parses `CREATE TABLE [t] SORTED BY (cols) [AS …]` into
  `CreateTableInfo::sort_keys`; `FabricatorCatalog::SupportsCreateTable` now permits BOTH partition_keys AND
  sort_keys (only the WITH-options clause stays rejected). C++ extracts the sort columns (reusing
  `fabricator::PartitionColumnsArg`) in the DDL create + CTAS and passes them; the **SQL Server provider maps them
  to a Fabric Warehouse / Synapse `WITH (CLUSTER BY (cols))`** layout in `BuildCreateTable` — **only on a
  warehouse profile** (`profile.IsWarehouse`; box SQL Server has no such syntax → the clause is a no-op there),
  for both explicit CREATE and CTAS. A **`mssql_cluster_by` session setting** (comma-separated columns) is the
  fallback when there's no native clause (`ResolveClusterColumns`: native SORTED BY wins). Delta / DAX ignore
  `sort_columns`. Validated: `test/verify_cluster_by.test` (18 — box plumbing + graceful no-op), columnstore +
  Delta suites unregressed, and **live on Fabric Warehouse**: `CREATE TABLE … SORTED BY (CustomerID, SaleDate)` +
  CTAS both accepted (data correct), and a bad cluster column is **rejected by Fabric** (error 1911 — proving the
  `WITH (CLUSTER BY …)` clause is emitted, not dropped). v51 = **native `PARTITIONED BY`** — appended a `partition_columns` param (nullable,
  comma-separated column names) to **both** `create_table` and `begin_bulk` (a signature change, not a slot add).
  DuckDB v1.5.4 parses `CREATE TABLE [t] PARTITIONED BY (cols) [AS …]` into `CreateTableInfo::partition_keys`
  (clause precedes `AS` for CTAS); the base `Catalog::SupportsCreateTable` REJECTS any partition_keys, so
  `FabricatorCatalog::SupportsCreateTable` is overridden to **permit** them (SORTED BY + WITH-options stay
  unsupported). C++ extracts the column names (`fabricator::PartitionColumnsArg`, `catalog/fabricator_partition_util.hpp`
  — column-refs only) in the DDL create (`FabricatorSchemaEntry::CreateTable`) and CTAS (`FabricatorCtasInfo`), passes
  them to `create_table`/`begin_bulk`; C# `SplitColumnList` → `IReadOnlyList<string>` → `DeltaCatalog` →
  engineered-wood `CreateAsync(partitionColumns:)` (Hive `<table>/<col>=<value>/*.parquet`, reads
  `Metadata.PartitionColumns` so INSERT/Append preserve the layout). **Provider-agnostic**: SQL Server / DAX
  ignore the arg (only Delta partitions). Also (C#-only, NO ABI): a **`delta_write_options` DuckDB session
  setting** (JSON — `compression`/`row_group_size`/`bloom_filter_columns`/`partition_by`) overlaying per-catalog
  ATTACH write-tuning defaults (`compression`/`row_group_size`/`bloom_filter_columns`), resolved by
  `DeltaCatalog.ResolveWriteSpec` → `DeltaWriteSpec` → `DeltaWriter.Options`; a native `PARTITIONED BY` clause
  overrides the setting's `partition_by`. engineered-wood's `ParquetWriteOptions` is delta-rs-class (auto
  dictionary + always-on min/max stats; bloom off by default). Validated: `test/verify_delta_catalog_partition.test`
  (54 — native CTAS/empty-CREATE+INSERT/multi-column/setting/override/re-attach), full Delta suite + SqlServer
  columnstore (CREATE+CTAS) unregressed, native partitioning **live on Fabric OneLake** (`LH.dbo.fabricator_parttest`,
  `region=US/EU/APAC`). **`delta_write_options` also carries `replace_where`** (C#-only, no ABI): **`replace_where`
  = `{partcol:val,…}`** turns an INSERT into an ATOMIC partition-overwrite — engineered-wood
  `DeltaTable.OverwritePartitionsAsync` (new; `WriteAsync`→ private `WriteCoreAsync` core with an
  `overwritePartitions` filter) removes exactly the matching-partition files + adds the new data in ONE commit
  (delta-rs static partition overwrite); keys MUST be partition columns (else `DeltaFormatException` — file-level
  removal is only exact for partition predicates) and the input must fall within them (else it errors, no
  silent append). `DeltaCatalog` gates it to plain INSERT (dropped for CREATE/CTAS/REPLACE).

  **SCHEMA EVOLUTION on write — `SCHEMA_MODE` COPY option + true CREATE OR REPLACE (ABI v54).** Because DuckDB's
  INSERT binder rejects wider-than-table data BEFORE the provider, schema evolution lives on **COPY** (COPY-TO
  isn't schema-checked, so arbitrary source schemas reach the provider) — surfaced as a `SCHEMA_MODE` COPY option
  threaded through **`begin_bulk`** (the ABI v54 arg `schema_mode`, next to partition/sort columns; provider-
  agnostic — SQL Server / DAX ignore it). **`SCHEMA_MODE 'merge'`** = append + UNION (engineered-wood
  `AddColumnAsync` per incoming-new column, then Append; old rows read NULL). **`SCHEMA_MODE 'overwrite'`** =
  replace data + adopt the incoming source schema (drop/add/retype) via new engineered-wood
  `DeltaTable.SetSchemaAsync` (a metadata-only `metaData` commit adopting the Arrow schema; no-op if identical;
  rejects column-mapping tables) then an Overwrite. **`CREATE OR REPLACE` / CTAS-replace is now a TRUE replace**:
  the Overwrite path always calls `SetSchemaAsync(incoming)` so the table adopts EXACTLY the new schema — a
  dropped column is GONE (not a lingering NULL), a new column appears — matching DuckDB's drop+create semantics
  and the SQL Server provider (which drops+recreates on replace). This **replaced the earlier confusing
  `merge_schema`-on-CREATE-OR-REPLACE band-aid**. `DeltaSchemaMode` (None/Merge/Overwrite) on `DeltaWriteSpec`,
  resolved by `ResolveWriteSpec` (COPY `SCHEMA_MODE` arg > `delta_write_options` `schema_mode`/`merge_schema` >
  the `merge_schema` ATTACH option, → Merge for append). Also fixed: history-preserving (the metaData commit keeps
  time-travel; old versions still show the old schema). Verified:
  `test/verify_delta_catalog_overwrite_merge.test` (47 — atomic partition overwrite, true CREATE OR REPLACE
  narrower-drops/wider-adds, COPY SCHEMA_MODE merge + overwrite); full Delta + SqlServer (columnstore/identity)
  suites unregressed. **Delta DECIMAL read + rowid-DML corruption — FIXED at the source (engineered-wood, no
  Bridge widening).** TWO root causes, both in engineered-wood, both fixed there: (1) **read corruption** — the
  parquet reader mapped a decimal to its parquet PHYSICAL width (INT32 → narrow Arrow `Decimal32`, INT64 →
  `Decimal64`), and the newer narrow decimal types are mishandled crossing the Arrow C-data-interface to DuckDB
  (read as 128-bit over the 4/8-byte buffer → garbage; e.g. `CAST(1.5 AS DECIMAL(2,1))` → `10.4`,
  `DECIMAL(10,2) 123.45` → `0.00`). DuckDB's native `read_parquet` reads the SAME files correctly → the WRITE was
  fine, only the read handoff was wrong. **Fix:** `ArrowSchemaConverter.MakeDecimalType` now always emits the
  classic `Decimal128` (≤38) / `Decimal256` (>38) regardless of physical width (`BuildDecimalFromInt32/Int64`
  already sign-extend to any byteWidth, so it's lossless). (2) **rowid `DELETE`/`UPDATE` corruption + crash** —
  the copy-on-write survivor filter `DeletionVectorFilter.TakeRows` had no decimal case → its `default: return
  source` passed the decimal column through UNFILTERED (all original rows), so the rewritten file had a
  row-count mismatch (e.g. `id` filtered to 2 values, `b` still 3) → mispaired reads + a `ReserveValues` buffer
  overrun. **Fix:** `TakeRows` now handles `Decimal128Array`/`Decimal256Array` via a byte-slice copy of the
  fixed-width value buffer (avoids `System.Decimal`'s 28-digit cap so precision 29–38 survives). With (1) the
  Bridge-side `DecimalWidening` (schema+batch widen on the Delta read boundary) is redundant and **removed** —
  the source now emits `Decimal128` directly. Verified: `test/verify_delta_catalog_decimal.test` (47 —
  Decimal32/64/128 physical widths, negatives, filter/aggregate, INSERT, time-travel AT VERSION, **DELETE**,
  **UPDATE**, re-attach durability); full Delta catalog suite (write/delete/update/changes/snapshots/
  time_travel/dv/alter/schemas) unregressed. **The decimal bug was one instance of a broader pattern — swept +
  fixed the rest (engineered-wood).** Both per-column "take rows" filters ended in `default: return source`,
  silently passing ANY unenumerated type through UNFILTERED (wrong-length column → the same corruption + overrun):
  `DeletionVectorFilter.TakeRows` (copy-on-write DELETE/UPDATE survivor filter) had **no Date/Timestamp/decimal
  case** — Date and Timestamp are ordinary columns, so this corrupted plain DELETE/UPDATEs, not just decimals;
  `PartitionUtils.TakeRows` (partition-split on partitioned writes) had no decimal case. Both now route every
  fixed-width Arrow type through a generic offset-aware value-buffer slicer and **THROW** on a genuinely
  unsupported (nested) type instead of returning it unfiltered. Also fixed: `ArrowSchemaConverter` mapped a
  parquet `TIME(micros/nanos)` to a malformed `Time32Type` (4-byte type, 8-byte semantics + a `<int>` value
  decode over an INT64 physical) → now `Time64` for micro/nano (general parquet-correctness fix; Delta itself has
  no TIME/unsigned type so it's not Delta-reachable). Verified: `test/verify_delta_catalog_temporal.test` (63 —
  DATE/TIMESTAMP/DECIMAL through DELETE, UPDATE, native partitioned write, re-attach durability); full Delta
  catalog suite unregressed.
  v50 = **directory move/rename** — appended `fs_move_dir(opener,src,dest,…)` to
  `FabricatorHostServices` (the reverse host→managed struct): maps to DuckDB's `FileSystem::MoveFile` — an atomic
  directory rename on a local filesystem; object stores (S3/Azure DFS) throw "not implemented". Powers **local/S3
  Delta catalog RENAME TABLE** (`DeltaCatalog.AlterTable` RenameTable → `HostFs.MoveDir`; OneLake still renames via
  the DFS SDK since Azure `MoveFile` is unimplemented). `test/verify_delta_catalog_schemas.test`. v49 =
  **recursive directory delete** — appended `fs_remove_dir(opener,path,…)` to
  `FabricatorHostServices` (the reverse host→managed struct, not the vtable): deletes a directory RECURSIVELY via
  DuckDB's `FileSystem::RemoveDirectory` (idempotent — no error if absent). Powers **Delta catalog DROP TABLE**
  (`DeltaCatalog.DropTable` → `HostFs.RemoveDir` removes the table's whole `<root>/<table>/` folder; opener
  threaded by `DropEntry`'s `FabricatorSetActiveTxn`). `test/verify_delta_catalog_write.test` (31). **OneLake DROP
  goes a different route** (`fs_remove_dir` → `FileSystem::RemoveDirectory` throws `AzureDfsStorageFileSystem:
  RemoveDirectory is not implemented!` — duckdb-azure has no recursive-delete on the DFS endpoint): `DropTable`
  branches on `FabricLakehouse.IsOneLake(root)` → a **direct ADLS Gen2 / OneLake DFS recursive delete**
  (`FabricLakehouse.DeleteDirectory` → `DataLakeDirectoryClient.DeleteIfExistsAsync`, idempotent) using the SP
  `ClientSecretCredential` the catalog mints — bypassing duckdb-azure entirely; local/S3 keep `fs_remove_dir`.
  Validated live 2026-06-30 on both `LH` (schema-enabled) and `LH_no_schema` (flat). See the OneLake-discovery
  paragraph below — discovery + DROP now share the DFS endpoint. v48 =
  **host-FS WRITE surface** — the Delta write-back foundation: appended five
  WRITE callbacks to `FabricatorHostServices` (the reverse host→managed struct, not the vtable) —
  `fs_open_write(opener,path,exclusive,…)` / `fs_write` / `fs_close_write` / `fs_remove` / `fs_create_dir` — plus
  the `FABRICATOR_ALREADY_EXISTS=4` status. `exclusive=1` opens with `EXCLUSIVE_CREATE` (the put-if-absent commit
  primitive — honored on OneLake/ADLS + POSIX; returns `ALREADY_EXISTS` if the target exists). `fs_create_dir`
  is recursive (mkdir -p; DuckDB's is single-level). The C# `DuckDbTableFileSystem` write methods
  (`CreateAsync`/`WriteAllBytesAsync`/`RenameAsync`/`DeleteAsync` + `DuckDbSequentialFile`) sit on these;
  **`RenameAsync` is emulated as exclusive-create-copy + delete-source** because DuckDB's `MoveFile` overwrites
  on local and is *unimplemented* on Azure DFS — so the commit's put-if-absent guard rides `EXCLUSIVE_CREATE`,
  and engineered-wood's temp+rename commit works unchanged (a conflicting target → `RenameAsync` returns false
  → `DeltaConflictException`). `HostFsGlob` now normalizes a not-found glob (object-store 404) to empty so a
  brand-new table's missing `_delta_log/` reads as "create". Demo `fabricator_delta_write_demo(path)` — a global
  host-FS table fn writing a fixed 5-row Delta table via engineered-wood (`DeltaWriteMode.Overwrite`,
  idempotent), validated end-to-end (write+read round-trip) on **local AND a live OneLake lakehouse** (SP
  azure secret). `test/verify_delta_write.test`. Single-writer; concurrent commits work where `EXCLUSIVE_CREATE`
  is honored (OneLake/POSIX — not Windows local). **Portability (validated with delta-kernel-rs, the reference
  reader, via DuckDB's official `delta` extension): engineered-wood's defaults are NOT standard-readable (incl.
  Fabric) — three fixes:** (1) `metaData.format.options` + (2) `metaData.configuration` always emitted
  (engineered-wood `ActionSerializer` — were omitted when empty/null, non-nullable for strict readers); (3)
  parquet `path_in_schema` (engineered-wood `OmitPathInSchema` defaults TRUE → drops this REQUIRED field →
  `TProtocolException: Invalid data`) — fixed our side via `ParquetWriteOptions { OmitPathInSchema = false }` in
  `DeltaWriteDemoFunction`. With all three, delta-kernel-rs reads it locally; a fresh `Tables/dbo/fabricator` table
  written to OneLake for Fabric. (#1/#2 are engineered-wood-repo patches; #3 is a write option. DuckDB's official
  `delta_scan` can't LIST a OneLake `_delta_log` — a delta-kernel azure/secret quirk, "No files in log segment" —
  so OneLake validation is via our reader + the local delta-kernel read. A table written BEFORE the fixes stays
  broken on its version-0 metaData → write a fresh one.) **`fabricator_delta_write(<input>, path := '…')`** — a
  global host-FS **collector** that writes ANY input table (a DuckDB query result) to a Delta table (Overwrite),
  returning `(version, rows_written)`; buffers input (Arrow-IPC round-trip copy), commits one version via the
  shared `DeltaWriter`. Cost args ride as NAMED params (`Parameters` added to `IInOutFunction`/
  `ICollectorTableFunction` + handle-0 `GlobalFunctions.ParamSchema`); the opener is threaded into the collector
  Source `GetDataInternal` (where C# `Collect` runs — Finalize-only was racy) AND into the shared
  `FabricatorSetActiveTxn` helper (so any connection-using callsite sets it). Validated local + a live OneLake
  managed table (`Tables/dbo/fabricator_query`). `test/verify_delta_write.test` (18). **Delta folder-as-catalog
  (READ + WRITE) DONE**: `DeltaBackend` (3rd `IBackend`, `"delta"`/`"deltalake"`, registered explicitly in
  `BackendRegistry.Discover` — Bridge-resident) + `DeltaCatalog`. `ATTACH '/lake'
  (TYPE fabricator, PROVIDER 'delta')` discovers subdirs-with-`_delta_log/` as tables under a flat `main` schema
  (glob `<root>/*/_delta_log/*.json`), columns via `DeltaReader.GetSchema`, scan via `DeltaReader.Stream` with
  filter pushdown. The opener is threaded into the catalog metadata path (`LoadCatalog`/`RefreshCache` call
  `FabricatorSetActiveTxn` before discovery; `FetchTableColumns` already did). `test/verify_delta_catalog.test`
  (17 — discovery + filter + join, LOCAL). **CATALOG STREAMING WRITE DONE** (the chosen slice): `CREATE TABLE`/
  `INSERT`/CTAS/COPY stream straight to engineered-wood via the **standard bulk path** (`begin_bulk`/`push_batch`/
  `complete_bulk` → `BulkSession` → `DeltaCatalog.BulkInsert`), exactly like the SQL/DAX backends — the global
  `fabricator_delta_write` collector is no longer needed for the catalog case (it stays as the no-ATTACH function
  form). **Opener threading into the bulk path:** `SetActiveOpener(&context)` is set immediately before
  `BeginBulk` in the insert/CTAS/COPY operators; `BulkSession` captures it at `begin_bulk` and **re-establishes
  `AmbientOpener.Current` (+ the txn id) on its background consumer thread** (the opener's `ClientContext` stays
  valid until `complete_bulk`, which blocks on the consumer). `createTable`/`replace` ⇒ `DeltaWriteMode.Overwrite`,
  plain INSERT ⇒ `Append`; one commit per statement; `CreateTable` writes empty commit-0 (PK/UNIQUE/DEFAULT
  ignored — Delta has none); `DeltaWriter.Materialize` IPC-round-trips the streamed batches for the commit. NO
  ABI change (reuses bulk + the v47 `set_active_opener`). `test/verify_delta_catalog_write.test` (31 — CREATE/
  INSERT/append/CTAS/aggregate + DROP TABLE + detach/re-attach durability, LOCAL). **DROP TABLE DONE** (ABI v49
  — appended `fs_remove_dir` to `FabricatorHostServices`: recursive directory delete via DuckDB's
  `FileSystem::RemoveDirectory`, idempotent; `DeltaCatalog.DropTable` deletes the table's whole `<root>/<table>/`
  folder via `HostFs.RemoveDir(AmbientOpener.Current, …)`, opener threaded by `DropEntry`'s `FabricatorSetActiveTxn`).
  **DELETE DONE — copy-on-write via a TRANSIENT (file,position) rowid; tables are PLAIN Delta (no features)**
  (mirrors the SQL Server backend's rowid DML — reuses the existing rowid operators wholesale, NO OptimizerExtension/
  custom operator, NO ABI change). **Why this shape (3 live-Fabric iterations):** DuckDB doesn't expose the WHERE
  at `PlanDelete` (`LogicalDelete` keeps only the table + a rowid-producing child) → predicate-capture is unsafe
  (pushdown is a superset → over-delete) → the rowid path is correct. The FIRST attempt used **Delta row tracking**
  (stable `_metadata.row_id`) + **deletion vectors**, but Fabric's OneLake converter / Spark could NOT read those
  commits: engineered-wood's protocol omitted feature dependencies (rowTracking needs `domainMetadata`; DVs need
  reader-v3 + `deletionVectors`) AND — even with a fully spec-compliant protocol — engineered-wood's inline DV
  byte format isn't what Spark/Fabric decode. **Final design = plain Delta, no table features:** the rowid is a
  TRANSIENT `(fileOrdinal << 40) | rowPositionInFile` (file ordinal in the path-sorted active set), computed at
  scan time, NOT persisted; DELETE is **copy-on-write** — rewrite each affected file without the deleted rows,
  committing plain `remove`+`add` (no DV). minReaderVersion 1 / minWriterVersion 2, zero features → every reader
  (Fabric OneLake conversion, Spark, delta-kernel) reads it. **engineered-wood** (local working changes):
  `ReadAllWithRowIdsAsync` appends the transient rowid (`OrderedActiveFiles` path-sort + per-file position);
  `DeleteByRowIdsAsync` decodes rowids → positions-per-file → copy-on-write rewrite; `CreateAsync` writes plain
  Delta (the feature-declaration logic stays but is unused — `DeltaWriter` passes no config). **Two parquet
  footer gotchas the rewrite hit** (both made the rewritten file unreadable by delta-kernel/Spark/Fabric with
  `TProtocolException: Invalid data`, while OUR reader tolerated it): (1) the rewrite must open with
  `DeltaWriter.Options()` (`OmitPathInSchema=false`) — `DeltaTableOptions.Default` drops the REQUIRED parquet
  `path_in_schema`; (2) the rewrite must REBUILD each kept batch with a CLEAN schema (drop the parquet reader's
  field metadata, e.g. an existing `PARQUET:field_id`) before re-writing — else `SetParquetFieldIds` collides
  and the footer is malformed. With both, delta-kernel reads our copy-on-write output (verified locally via
  DuckDB's official `delta_scan` — the reference reader Spark/Fabric use). **Virtual rowid
  threading** (the crux — `_metadata.row_id` is NOT a user column; surfacing it as one would break INSERT):
  `FetchRowIdColumns` returning a name absent from the schema is treated as a VIRTUAL rowid — `FabricatorTableEntry`/
  `ArrowStreamBindData` carry the NAMES (not indices) in `virtual_rowid_columns`, `HasRowId`/`GetVirtualColumns`/
  `GetRowIdColumns` honor them, `BuildScanSpec` adds them to the fetch list when rowid is requested, `arrow_ingest`
  resolves their result positions BY NAME for `BuildRowId`, and `BuildModifyTarget` uses the virtual names +
  BIGINT. SQL Server is unaffected (its rowid names always resolve to real columns; the virtual branch never
  fires — verified `verify_proc_inout`/`verify_time_travel`/`verify_columnstore` green). `DeltaCatalog`:
  `GetMetadata(RowId)` ALWAYS returns `_metadata.row_id` (the transient rowid works on ANY Delta table);
  `ScanTable` streams WITH the row-id column when requested; `ExecuteDelete` collects the ids → `DeleteByRowIdsAsync`.
  `test/verify_delta_catalog_delete.test` (28 — equality/range/name predicates + durable across re-attach +
  DELETE-all). **Live Fabric: plain-Delta CTAS+DELETE validated end-to-end** on the schema-enabled `LH` lakehouse
  (`fabricator_deltest4`: v0 protocol = minReader 1/minWriter 2/no features, DELETE = plain remove+add, our read
  correct); the OneLake table-format conversion is expected to succeed on plain Delta (pending user confirm — the
  earlier row-tracking/DV tables failed conversion). **A transient rowid is valid only within one snapshot** (a
  scan's rowids must be consumed by the DELETE before another write changes the file set — true for a single
  DML statement). **UPDATE DONE — rowid-based PER-FILE copy-on-write** (matches DELETE). `ExecuteUpdate` receives
  the new SET-column values (named) + the transient `_metadata.row_id` per row; it builds a `rowid → new values`
  map and calls engineered-wood `UpdateByRowIdsAsync(rowIds, rewriteFile)`, which rewrites ONLY the files
  containing a matched row (decoded from `rowid >> 40`) — each affected file's batches are handed back via the
  `rewriteFile` callback, where Fabricator rebuilds the SET columns on the matched positions as CLEAN Apache.Arrow
  batches (`BuildArray`, a typed inverse of `ArrowValueReader.ReadScalar` — bool/ints/uints/float/double/
  decimal128/string/date32/timestamp; rowid recomputed as `(ordinal << 40) | positionInFile` to match the scan),
  and engineered-wood re-writes them as plain `remove`+`add` with a CLEAN schema (the parquet-footer fix). The
  typed substitution stays in Fabricator (reuses `BuildArray`/`ReadScalar`); engineered-wood stays generic (file
  selection + read + clean write). Unaffected files are untouched. NO C++ change (reuses the DELETE virtual-rowid
  planning + the `ExecuteUpdate` ABI). Verified single-row / multi-row / expression (`amt=amt+1`) updates,
  UPDATE∘DELETE composition, re-attach durability, a delta-kernel `delta_scan` read-back, AND live on Fabric
  (`fabricator_updtest` on the schema-enabled `LH` lakehouse). `test/verify_delta_catalog_update.test` (63).
  **SCHEMA EVOLUTION — `ALTER TABLE … ADD COLUMN` DONE** (the only supported ALTER kind on Delta): a
  **metadata-only commit** appending a nullable column (NO file rewrite) — engineered-wood `DeltaTable.AddColumnAsync`
  writes a new `MetadataAction` (current Arrow schema ++ the new field → `SchemaConverter.FromArrowSchema` →
  `DeltaSchemaSerializer` → `metaData` at version+1; rejects column-mapping tables [no field-id assignment] +
  non-nullable + duplicate names). The crux is the **read-side NULL backfill** (`DeltaTable.BackfillMissingColumns` +
  `MakeNullArray`, in `ReadFileAsync` before the rowid append): a column added after a data file was written is
  absent from that file's parquet, so each batch is reconciled to the current schema — present columns by name, the
  missing one as an all-NULL typed array (no-op fast path when the file already has every column). `DeltaReader.AddColumn`
  → `DeltaCatalog.AlterTable`; `a1`=name, the `Field` carries type+nullability. C++ `FabricatorSchemaEntry::Alter` now
  **`SetActiveOpener` before the alter** (host-FS opener for the Delta metadata write; no-op for SQL) + the existing
  eager column re-fetch surfaces the new column in-session. NO ABI change (reuses the v2 `alter_table` entry).
  Standard-compliant by construction (a textbook metaData commit on already-standard data files →
  delta-kernel/Spark/Fabric backfill old-file NULLs natively). Verified: `test/verify_delta_catalog_alter.test` (81 —
  old rows NULL, new rows valued, 2nd-column add, predicate on the new column, re-attach durability,
  RENAME/DROP/TYPE error). **`RENAME TABLE` DONE (OneLake only)** — the table IS its folder and Delta's `_delta_log`
  uses table-relative paths, so renaming = moving the whole folder. OneLake uses the DFS endpoint's **atomic native
  rename** (`FabricLakehouse.RenameDirectory` → `DataLakeDirectoryClient.RenameAsync`; the destination path is
  filesystem-relative WITHOUT the workspace prefix — OneLake requires the leading segment to be the
  `<item>.Lakehouse`, else 400 "item type extension is missing"). C++ `Alter`'s RENAME_TABLE branch already updates
  the entry cache. Validated live on `LH` (create → rename → new name reads → drop). **local/S3 RENAME — DONE (ABI
  v50)** via the new host `fs_move_dir` → `FileSystem::MoveFile` (`DeltaCatalog.AlterTable` → `HostFs.MoveDir`):
  an atomic directory rename on local; an object store whose FileSystem doesn't implement `MoveFile` throws a clean
  error. Verified local (`verify_delta_catalog_schemas.test` — rename + reattach durability). So RENAME works on
  local + OneLake (S3 iff DuckDB's S3 FileSystem implements `MoveFile`). **Still unsupported** (clean error): raw
  exec, RENAME/DROP COLUMN + ALTER COLUMN TYPE (need column mapping / rewrite). **DROP SCHEMA** is supported in
  `schemas` mode (see below), else unsupported. (Recursive DROP TABLE already works on local/S3 via `fs_remove_dir`,
  ABI v49.)
  **Multi-schema for local/S3 — the `schemas true` ATTACH option (DONE)**: by default a non-OneLake Delta catalog is
  FLAT (single `main` schema; the schema component is ignored, so `db.staging.t` and `db.main.t` would both map to
  `<root>/t` — a silent collision footgun). `ATTACH '…' (TYPE fabricator, PROVIDER 'delta', schemas true)` switches it
  to the two-level `<root>/<schema>/<table>` layout (the same layout a schema-enabled OneLake lakehouse uses):
  `DeltaCatalog._schemas` (parsed from the forwarded ATTACH options JSON, like `deletion_vectors`) drives
  `SchemaLayout` → `TablePath` nests by schema; `DiscoverTablePairs` globs the two-level
  `<root>/*/*/_delta_log/*.json` (the segment before `_delta_log` = table, the one before that = schema);
  `SchemaNames` enumerates the discovered schemas + always `main`; `CreateSchema` materializes the
  `<root>/<schema>/` subfolder (`HostFs.CreateDir`, recursive) so a fresh schema works; `DropSchema` removes it
  recursively (`HostFs.RemoveDir`; `main` is protected). OneLake ignores the option (its layout is driven by the
  lakehouse schema-enabled flag). NO ABI change (the option rides the v37 ATTACH-options→JSON forwarding). Verified:
  `test/verify_delta_catalog_schemas.test` (23 — subfolder layout, same-name tables in two schemas don't collide,
  re-attach rediscovers schemas, CREATE/DROP SCHEMA, RENAME-unsupported-locally). **Empty created schemas don't
  survive re-attach** (no `_delta_log` to glob) — a documented limitation. **Gotcha found:** unqualified
  `staging.t` resolves to DuckDB's DEFAULT (memory) catalog, not the attached one — always use `db.schema.table`.
  **TIME TRAVEL — `FROM t AT (VERSION => n)` DONE** (C#-only; the plumbing already existed —
  `FabricatorCatalog::SupportsTimeTravel()` is `true` for all providers and the `AT` clause already flows to the
  scan's `spec_json` as `"at":{unit,value}` → `ScanSpec.At`, which the SQL backend uses for `FOR SYSTEM_TIME AS
  OF`). `DeltaCatalog.ScanTable` now honors `spec.At`: `DeltaReader.StreamAt` / `GetSchemaAt` resolve the
  snapshot (engineered-wood `ReadAtVersionAsync` + `GetSnapshotAtVersionAsync`) and stream as of that version,
  advertising the schema AS OF that version. **Filter pushdown applies under time travel** (`ReadAtVersionAsync`
  takes the predicate → file/row-group pruning). Unlike the SQL provider (timestamp-only, `FOR SYSTEM_TIME`),
  Delta accepts **VERSION** (the natural Delta form) — and works under JOIN/UNION version comparison. **Rowid
  under time travel:** DuckDB's `count(*)`-via-rowid optimization can request the virtual `_metadata.row_id` on a
  time-travel scan; the branch routes that to a version-aware rowid stream
  (`DeltaReader.StreamWithRowIdsAt` → engineered-wood `ReadAtVersionWithRowIdsAsync`, the version analog of
  `ReadAllWithRowIdsAsync`) — without it, the rowid (BIGINT) collided with the first user column (`INTERNAL
  Error: Vector::Reference … BIGINT referenced INTEGER`) when the same table was scanned at multiple versions in
  one statement. **`AT (TIMESTAMP => ts)`: opt-in via `in_commit_timestamps true`.** engineered-wood's
  `GetSnapshotAtTimestampAsync` resolves a timestamp via the Delta **in-commit-timestamps** feature (NOT the
  commit-file mtime — mtime is mutable/non-monotonic on object stores, the very problem inCommitTimestamps
  solves). On a plain table (default) it has no timestamp to read → a clean "Ensure the table has in-commit
  timestamps enabled" error; **VERSION travel always works**. The **`in_commit_timestamps true` ATTACH option**
  (mirrors `deletion_vectors`; `DeltaCatalog._inCommitTimestampsOnCreate` → `delta.enableInCommitTimestamps=true`
  in the create config; engineered-wood `CreateAsync` declares the `inCommitTimestamp` **writer** feature
  [writer v7, NOT a reader feature — readers read normally] + `EnsureCommitInfo` writes the per-commit timestamp)
  makes TIMESTAMP travel resolve. Enabled at creation (v0), so no
  `inCommitTimestampEnablementVersion/Timestamp` props needed. **delta-rs/delta-kernel reads it** (DuckDB's
  official `delta_scan`), and our provider write+read+timestamp-travel works on OneLake. **Fabric OneLake
  conversion — GATED ON A FABRIC TIME-TRAVEL SETTING (validated live 2026-06-30):** on a lakehouse WITHOUT it
  (`LH_no_schema`) the converter rejects the writer-v7 table — it shows "Unable to identify these objects as
  tables or views"; on a lakehouse WHERE the Fabric time-travel setting is ENABLED (`LH2`) the converter
  **accepts** it — the table registers AND is **queryable via the Fabric SQL endpoint** (confirmed live, not just
  visible in the Tables list). So it's NOT a blanket rejection — Fabric's converter accepts the
  inCommitTimestamp (writer-only) feature once the workspace/lakehouse time-travel setting is on. (The setting is
  documented for the Warehouse but also affects the Lakehouse converter — confirmed.) Earlier
  `deletion_vectors`/row-tracking failures were a different cause (reader-v3 / domainMetadata / DV byte format),
  not just "writer v7". **⇒ `in_commit_timestamps` works on Fabric lakehouses with time-travel enabled, AND on
  local/S3 + delta-rs/Spark; without the Fabric setting use plain tables (VERSION travel) on Fabric.** A
  **commit-file mtime** path (timestamp travel on PLAIN tables, no writer-v7, no Fabric setting needed — a clean
  engineered-wood spec-fix since `ITableFileSystem.ListAsync` already returns `LastModified`, + a host-FS mtime
  callback our side) remains an option but is now lower-priority since inCommitTimestamps works on Fabric.
  **VERSION travel works everywhere (plain Delta) and is the universal form.** NO ABI/C++ change. Verified:
  `test/verify_delta_catalog_time_travel.test` (47 — version 0/1/2/3, filter pushdown, multi-version
  count/JOIN/UNION, re-attach durability, plain-table timestamp error, and `in_commit_timestamps` timestamp
  travel [local]).
  **Snapshots/history function — DONE: `fabricator_delta_snapshots('<catalog>', '<schema.>table')`** (DuckLake-style
  snapshots view). First arg = the ATTACH'd catalog NAME (resolved to its handle via `ResolveConnection` — no
  abfss path needed, the catalog's own `TablePath` builds the location); second = the table, schema-qualified
  (schema **mandatory on a schema-enabled lakehouse**, defaults to `main` on a flat catalog; C++ splits on the
  first `.`, C# resolves the default/required schema). Returns `(version BIGINT, timestamp TIMESTAMP, operation
  VARCHAR, operation_parameters VARCHAR)` from the `_delta_log`. C++ `SnapshotsBind` mirrors `ServerInfoBind`
  (catalog→handle, factory = `GetMetadata(handle, FABRICATOR_META_SNAPSHOTS=8, schema, table)`, reuses
  `ArrowStreamScan`); C# `DeltaCatalog.GetMetadata(Snapshots)` → `DeltaReader.GetSnapshots` → engineered-wood
  `DeltaTable.GetHistoryAsync` (new — `ListVersionsAsync` + `ReadCommitAsync` → `CommitInfo`). **Additive enum →
  NO ABI bump.** **`commitInfo` is now written on EVERY commit by default** (engineered-wood
  `InCommitTimestamp.EnsureCommitInfo` always prepends a `commitInfo` with `operation` + a `timestamp` — standard
  feature-free fields, no protocol bump, writer v2; `CreateAsync`'s v0 also gets one, operation `CREATE TABLE`).
  So **plain tables now show a full operation + timestamp history** (`CREATE TABLE`/`WRITE` per version), not just
  the version list. The opt-in `inCommitTimestamp` field is added on top ONLY for `in_commit_timestamps` tables.
  **`AT (TIMESTAMP)` time travel now works on PLAIN tables too** (`GetTimestamp(CommitInfo)` reads
  `inCommitTimestamp ?? commitInfo.timestamp`, and `GetSnapshotAtTimestampAsync` uses it) — the always-on
  `commitInfo.timestamp` is enough to resolve a snapshot, so the `in_commit_timestamps` feature is now only needed
  for the **in-protocol monotonic** guarantee (Spark/Fabric interop), NOT for local timestamp travel. A far-future
  instant → latest version; an instant before commit-0 → clean "No commit found" error
  (`test/verify_delta_catalog_time_travel.test`, 48). Validated local + **live Fabric**: `LH2.dbo.fabricator_ict2` (ICT) AND a PLAIN table on
  `LH_no_schema` both show v0 `CREATE TABLE` + `WRITE`s with timestamps — and the plain `commitInfo` table on
  `LH_no_schema` (no time-travel setting) **registers + is SQL-endpoint-queryable** in Fabric (confirmed), i.e.
  always-on `commitInfo` is Fabric-safe on plain writer-v2 tables. `test/verify_delta_catalog_snapshots.test`
  (28). Pairs with VERSION time travel: read the
  snapshots to pick a version. **Per-row commit version as a virtual column (DuckLake
  `snapshot_id` analog) — NOT built**: needs the Delta **row-tracking** feature (`_metadata.row_commit_version`),
  which our plain tables don't enable (only the opt-in `deletion_vectors true` path enables row tracking) + a
  second-virtual-column plumbing beyond `_metadata.row_id` + uncertain Fabric read-compat of row-tracking
  commits. **Change Data Feed (CDF) — DONE + VALIDATED LIVE on Fabric (2026-06-30).** Enabled per-catalog via the
  ATTACH option **`change_data_feed true`** (`DeltaCatalog._changeDataFeedOnCreate`): tables CREATEd in that
  catalog declare the Delta **`changeDataFeed` writer feature** (writer-v7; `DeltaWriter.CreateConfig` →
  engineered-wood `CreateAsync` adds `delta.enableChangeDataFeed=true` + the feature). Then INSERT/DELETE/UPDATE
  **capture CDC change files**: blind appends infer naturally, and the rowid copy-on-write DELETE/UPDATE +
  DV-delete paths emit `_change_data/*.parquet` (`CdfConfig.Delete`/`UpdatePreimage`/`UpdatePostimage`) — they
  already read the changed rows for the rewrite, so capture is "for free" there. **Read** via
  **`fabricator_delta_changes('<catalog>', '<schema.>table', from [, to])`** (2 overloads — `to` omitted/-1 ⇒
  latest): the row-level feed between two versions with `_change_type` (insert/delete/update_preimage/
  update_postimage) ++ `_commit_version BIGINT` ++ `_commit_timestamp BIGINT` (epoch ms, from the always-on
  commitInfo). C++ `ChangesBind` mirrors `SnapshotsBind` (catalog→handle, arg2 = `"from:to"`, factory =
  `GetMetadata(handle, FABRICATOR_META_CHANGES=9, schema.table, range)`); C# `DeltaCatalog.GetMetadata(Changes)` →
  `DeltaReader.GetChanges` → engineered-wood `DeltaTable.ReadChangesAsync`. **Additive enum → NO ABI bump.** **Two
  fixes that made the read work:** (1) the rowid-DML CDC capture must drop the **virtual** `_metadata.row_id`
  trailing column (`DropVirtualRowId` — NOT `RowTrackingWriter.StripRowIdColumn`, which strips the *physical*
  `__delta_row_id`) before writing the change file, else the update_preimage batch has 6 cols vs 5 elsewhere → a
  schema mismatch across change batches → **arrow_ingest SIGSEGV**; (2) `DeltaReader.GetChanges` **streams lazily**
  (peek-the-first-batch for the schema, table stays open for the whole enumeration — kept for BOUNDED MEMORY; the
  original "materializing then disposing the table frees the batches' Arrow buffers = use-after-free, 'Out of Range
  string size'" diagnosis was DISPROVEN 2026-07-16 — see the lifetime-correction note in the buffered-DML slice-2
  bullet; the corruption was almost certainly fix (1)'s schema mismatch). **CDF-enabled guard:**
  engineered-wood's `CdfReader` silently INFERS changes from add/remove on a non-CDF table (misleading for
  copy-on-write — survivors look like inserts), so `GetChanges` requires `CdfConfig.IsEnabled(config)` and throws
  "Change Data Feed is not enabled" otherwise (Spark `DELTA_CHANGE_DATA_FEED_NOT_ENABLED` parity). Validated local
  (`test/verify_delta_catalog_changes.test`, 45 — full feed + change-type breakdown + pre/post images + bounded
  ranges + 3-arg-latest + CDF-off error) AND **live Fabric** (`Test`/`LH` schema-enabled: CTAS → DELETE → UPDATE
  on `lake.dbo.fabricator_cdftest` → correct feed [3 ins/v1, del/v2, upd pre+post/v3] + snapshots v0 CREATE/v1
  WRITE/v2 DELETE/v3 UPDATE). **OneLake table discovery + DROP
  — via the ADLS Gen2 / OneLake DFS endpoint directly** (`FabricLakehouse`, Bridge; `Azure.Storage.Files.DataLake`
  12.21.0): DuckDB's azure glob can't recurse a OneLake `_delta_log` tree (mid-path wildcard → `type must be
  string, but is null`, duckdb-azure PR #174), so a OneLake root (`abfss://<ws>@onelake…/<lh>.Lakehouse/Tables`)
  lists its tables via the **Azure SDK `DataLakeFileSystemClient.GetPaths`** (NOT DuckDB's azure ext, NOT the
  Fabric `ListTables` REST API, NOT the SQL endpoint) — flat lakehouse → table dirs under `Tables/` (schema
  `main`), schema-enabled → schema dirs under `Tables/` then table dirs under each (`Tables/<schema>/<table>`);
  **local/S3/plain-ADLS roots keep the host-FS glob** (`DeltaCatalog.DiscoverTables` branches on
  `FabricLakehouse.IsOneLake`). This is immune to the glob bug AND the `ListTables` 400 on schema-enabled
  lakehouses, AND free of the SQL-endpoint sync lag (DFS reflects committed files immediately). **DESIGN (deferred,
  nothing built): generalize `FabricLakehouse` into a full C# OneLake filesystem to escape duckdb-azure entirely** —
  [docs/filesystem-bridge.md](docs/filesystem-bridge.md) §"OneLake filesystem + unified Fabric credential": (1) a
  shared `FabricCredentialResolver` (SP secret ⇒ `ClientSecretCredential`, else `DefaultAzureCredential` picking up
  Fabric managed/workspace identity — one credential feeding DAX/SQL/OneLake with per-service scopes; the "seamless
  local + Fabric-notebook" requirement); (2) `OneLakeDataLakeFileSystem : ITableFileSystem` on the Azure DataLake SDK,
  swapped in for OneLake roots instead of the host `fs_*` callbacks (removes duckdb-azure from ALL C#-side IO); (3)
  **with the native Multifile-Delta model, register it as a DuckDB `onelake://` FileSystem (forward callbacks)** — NOT
  Delta-specific, it lifts EVERY DuckDB reader/writer (read_json/csv/parquet, excel, `COPY … TO 'onelake://…'`) onto
  OneLake, so Excel/JSON round-trips ride the same transport for free; (4) optional `TYPE fabric` secret (v38 machinery)
  for explicit auth UX (redundant over azure-secret + DefaultAzureCredential; pure clarity). `duckdb_onelake` is the
  secret-layering blueprint only — it still leans on duckdb-azure/httpfs, doesn't fix the FS. **The Fabric REST
  API is kept ONLY for the schema-enabled flag** (`GetLakehouse.DefaultSchema` — authoritative even for an empty
  lakehouse, where the DFS structure alone is ambiguous) **+ workspace/lakehouse name→GUID resolution**
  (`WorkspacesClient`/`ItemsClient`; GUIDs in the path skip it). **CRITICAL — use the ASYNC DataLake APIs**
  (`GetPathsAsync`/`DeleteIfExistsAsync` + `GetAwaiter().GetResult()` at the boundary): the SYNC `GetPaths` uses
  `HttpClient.Send`, whose sync transport HANGS under the hostfxr-hosted CLR (a single discovery never returns —
  isolated to the sync path; the same call is ~1s in a normal console host) — same reason every other Bridge IO
  path is async. Auth = the **ATTACH'd azure SP secret** (`ATTACH '…OneLake…' (TYPE fabricator, PROVIDER 'delta',
  SECRET <azure_sp>)` → v39 foreign-secret path → `DeltaBackend.BuildConnectionString` appends a cred marker on
  the root → `DeltaCatalog` mints a `ClientSecretCredential`, mirroring DAX); the data files are still
  read/written through DuckDB's FileSystem (the opener + a DuckDB azure secret) — the DFS endpoint is used for
  table LISTING + DROP only. **Live Fabric tests use the gitignored `dax_secret.sql`** at the repo root (`CREATE
  OR REPLACE SECRET fabric_sp (TYPE azure, PROVIDER service_principal, TENANT_ID/CLIENT_ID/CLIENT_SECRET …)` — the
  Fabric-Warehouse SP; ATTACH `… (PROVIDER 'delta', SECRET fabric_sp, READ_ONLY false)`; one secret serves DuckDB
  OneLake IO + the Fabric REST API + the DFS endpoint). **Schema-enabled AND flat lakehouse support — DONE +
  VALIDATED LIVE (2026-06-30) on workspace `Test`, lakehouses `LH` (schema-enabled, tables at
  `Tables/<schema>/<table>`) and `LH_no_schema` (flat, `Tables/<table>`).** `DeltaCatalog` is **multi-schema**:
  `GetMetadata(Schemas)` returns the lakehouse schemas (+ always the `DefaultSchema`), and `TablePath` is
  schema-aware (`<root>/<schema>/<table>` when schema-enabled, else flat `<root>/<table>`). **Validated live
  end-to-end:** on both lakehouses, DFS discovery + CTAS + scan + DROP (DFS recursive delete, confirmed the table
  dir is gone); plus on `LH` CTAS into `lake.dbo.fabricator_deltest` → `DELETE WHERE id=2` (deletion-vector commit)
  → `SELECT` returns 1,a/3,c. **`READ_ONLY false` is REQUIRED** for OneLake writes: DuckDB force-bumps any remote
  (`abfss://`) ATTACH to read-only when the access mode is AUTOMATIC (`database_manager.cpp:105`); Delta supports
  remote writes, so set it explicitly. **Caveat:** `SHOW TABLES` / `duckdb_tables()` over a OneLake catalog is
  slow (it materializes every table's COLUMNS = N OneLake `_delta_log` reads — DFS table LISTING itself is fast,
  ~1–7s; it's the per-table column fetch that's slow) — use targeted `lake.<schema>.<t>` access.
  **Fabric-compat history (2026-06-29, SUPERSEDED — kept as the trail):** the first DELETE used row tracking +
  deletion vectors. Two protocol bugs surfaced first (rowTracking missing its `domainMetadata` dependency →
  `DELTA_FEATURES_PROTOCOL_METADATA_MISMATCH`; DVs written without the `deletionVectors` reader-v3 feature →
  OneLake conversion `INTERNAL_ERROR`); declaring all features fixed the protocol, but Fabric/Spark STILL could
  not read it — engineered-wood's inline DV byte format isn't Spark-decodable (Fabric DOES support DVs, so it's
  our format). At that point we shipped plain Delta + copy-on-write + transient rowid (see the DELETE paragraph
  above) as the default — validated live. **DV FORMAT BUG NOW FIXED (upstream engineered-wood, verified against
  delta-kernel via DuckDB's official `delta_scan`).** Two bugs in engineered-wood's `RoaringBitmapWriter`/`Reader`:
  (1) it omitted the 64-bit **`RoaringBitmapArray` wrapper** — wrote `[magic][32-bit bitmap]` instead of
  `[magic][int64 sub-bitmap-count][int32 high-key + 32-bit bitmap]…`; (2) the inner 32-bit bitmap used a
  non-standard no-run cookie `((count-1)<<16)|12346` instead of the CRoaring portable form
  `[12346 full u32][int32 size][descriptive][offset-header-ALWAYS]`. Fixed both + made
  `RoaringBitmap.DeserializePortable` return bytes-consumed (to walk multi-sub-bitmap arrays); the reader now
  parses the array wrapper with the legacy bare-bitmap fallback. `delta_scan` reads an engineered-wood DV table
  with the deleted row correctly removed (`scratchpad/dvtest` harness). This also fixes engineered-wood's own
  `DeleteAsync` — report upstream. **Finding:** the Delta `rowTracking` FEATURE is NOT actually needed for DV
  deletes — an ABSOLUTE-position transient `(file, position)` rowid composes correctly across repeated DV deletes
  (the parquet file is never rewritten, so absolute positions are stable); rowTracking only adds stable-ids-across-
  compaction, which our DML doesn't use. **DELETION VECTORS ARE THE DEFAULT DML MODE (2026-07-04, commit
  `66e97f5`).** New Delta catalog tables use deletion vectors by default (the modern Delta standard): a DELETE
  marks rows in a DV bitmap instead of rewriting the file — cheap, and it preserves row-tracking ids/versions
  for free (no rewrite). `DeltaCatalog._deletionVectorsOnCreate` now defaults **true** (`ParseBoolOption` gained a
  defaulted overload distinguishing absent → default from explicit false). **Opt OUT with `deletion_vectors
  false`** for a plain copy-on-write table (`minReader 1`, no reader-v3 bump) — e.g. a consumer that can't read
  reader-v3 DV tables. Copy-on-write stays the fallback (opt-out) + the compaction path. **UPDATE on a DV table
  is MERGE-ON-READ** (engineered-wood `UpdateViaVectorsAsync`, picked internally by `UpdateByRowIdsAsync` when
  `delta.enableDeletionVectors` + clean shape [no mapping/partitions/type-widening/CDF]): DV-delete the matched
  OLD rows (union their positions into the file's DV — NO file rewrite) + APPEND their post-image rows as one
  small new file, committed atomically as `remove`(old,oldDV)+`add`(same,newDV)+`add`(post-image file). Big
  write-amplification win for a small update on a large file, and it re-ids FEWER rows than copy-on-write (which
  re-ids every row in the rewritten file — only the appended rows get fresh ids; non-updated rows keep their
  original id/version). **Stable-id preservation across UPDATE is now DONE (opt-in `materialize_row_tracking
  true`) + VALIDATED ON FABRIC SPARK** — see the "Materialized row tracking" bullet below (the appended row
  carries its ORIGINAL `__delta_row_id`, so Spark reads `_metadata.row_id` preserved; default off keeps the
  validated DV path untouched). Validated: official `delta_scan` reads the DV-original +
  appended result + LIVE on Fabric OneLake; `test/verify_delta_catalog_dv_default.test` asserts the append is
  small (a 2-row post-image file beside the 10-row original, not a 10-row rewrite). Non-DV (opt-out) UPDATE stays
  copy-on-write (native rewriter under `native_write`). `test/verify_delta_catalog_dv_default.test`
  proves default ⇒ DV (on-disk parquet count stays 1 after DELETE = no rewrite) vs `deletion_vectors false` ⇒
  copy-on-write (2 files = rewrite); the CoW-intent tests (`verify_delta_catalog_delete` + the native_write CoW
  catalogs) are pinned `deletion_vectors false` to keep that coverage. **OPTIMIZE + VACUUM maintenance WIRED
  (2026-07-04).** Under DV-default, DVs + merge-on-read append small files accumulate, so bin-pack compaction
  matters: `fabricator_exec('<catalog>', 'OPTIMIZE <schema.table>')` (+ `VACUUM <schema.table> [RETAIN <hours>
  HOURS] [DRY RUN]`) → `DeltaCatalog.ExecuteNonQuery` → `DeltaReader.Optimize`/`Vacuum` → engineered-wood
  `CompactAsync`/`VacuumAsync` (mirrors the delta-rs provider's maintenance dialect). **Under `native_write` the
  OPTIMIZE compacted files are written by DuckDB's native writer too** (`CompactionExecutor` gained an
  `IDataFileWriter` branch; `DeltaReader.Optimize` opens with the native writer when `_nativeWrite`) — so an
  OPTIMIZE KEEPS the native-write quality (bloom/stats/decimal) instead of reverting to the EW codec. Proven: the
  compacted file (after OPTIMIZE+VACUUM leaves only it) carries the native bloom signature (bloom on the
  dict-encoded `grp`, none on all-distinct `id`); validated LIVE on Fabric OneLake. **`CompactionExecutor` fixed
  to be DV-AWARE** — it now EXCLUDES each candidate file's deletion-vector-deleted rows (else compaction would
  RESURRECT them — a data bug, since DV is the default) + strips the internal `__delta_row_id` + carries each
  removed file's DV on the `remove`. **The exec path now threads the host-FS opener** (`fabricator_extension.cpp`
  `FabricatorExecFunction` calls `SetActiveOpener` before `ExecuteDml`; C++-only, no ABI) — without it a
  fresh-connection OPTIMIZE segfaulted (the Delta provider's `_delta_log` listing needs the opener; SQL Server /
  delta-rs ignore it). Validated: `test/verify_delta_catalog_optimize.test` (30 — 4 small files + DV-delete →
  OPTIMIZE consolidates, deleted rows NOT resurrected, VACUUM cleans, durability) + delta_scan + LIVE Fabric
  OneLake (`compacted → v6`, data {1,3} correct). **Row-tracking-on-rewrite (compaction + merge-on-read UPDATE)
  — NOW PRESERVED under `materialize_row_tracking true` (2026-07-04, Fabric-Spark-validated).** By default the
  compacted/appended files get a FRESH `baseRowId` and EW does NOT declare the materialized row-tracking columns,
  so a spec reader (delta-kernel/Spark) computes ids from `baseRowId + position` → a rewrite CHANGES the stable id
  of the rewritten rows (the DATA is always correct — delta_scan + Fabric validated — only external
  stable-row-tracking drifts). With **`materialize_row_tracking true`** both the UPDATE-append AND **compaction**
  now materialize the ORIGINAL ids: the UPDATE case needs only `__delta_row_id` (appended rows share the new
  commit version = the file's `defaultRowCommitVersion`), but **compaction mixes rows from several source files**,
  so `CompactionExecutor` materializes BOTH `__delta_row_id` (source `baseRowId + position`, or the source's own
  materialized id when present) AND `__delta_row_commit_version` (the source file's `defaultRowCommitVersion`) —
  a single `baseRowId`/`defaultRowCommitVersion` on the compacted `add` can't represent them (new
  `RowTrackingWriter.AddRowIdAndCommitVersionColumns` + the compaction read loop tracking survivor id/version
  arrays aligned with the DV filter's keep order). **Validated live on Fabric Spark (2026-07-04):** after
  CTAS+2×INSERT (3 files) → OPTIMIZE+VACUUM, Spark reads `_metadata.row_id` = 0,1,2 (PRESERVED write-order ids,
  NOT the fresh `base_row_id`=3 range) and `_metadata.row_commit_version` = 1,2,3 (per-row original versions,
  overriding the single `default_row_commit_version`=1). Local write-shape test
  `test/verify_delta_catalog_compaction_rowtracking.test` (24 — compacted parquet carries original ids + 3
  distinct versions + durability); default (non-materialize) compaction path unchanged
  (`verify_delta_catalog_optimize` green). **Fabric Spark row-id validator:** the preview **Microsoft ADO.NET
  Driver for Fabric Data Engineering**
  below. **Fabric Spark row-id validator:** the preview **Microsoft ADO.NET Driver for Fabric Data Engineering**
  (`Microsoft.Spark.Livy.AdoNet`, download-center zip) — its own session-create POST 404s, but the underlying
  **Fabric Livy REST API** works with the `fabric_sp` SP (`ClientSecretCredential`, `livyApi` version
  `2023-12-01`) and Spark exposes `_metadata.row_id`/`_metadata.row_commit_version`. Harness =
  `scratchpad/sparkprobe` (drives Livy raw: create session → `POST /statements` Spark SQL → read output; reads
  the SP from `dax_secret.sql` at runtime). This is THE way to validate Delta row tracking end-to-end.
  **Activate DV explicitly** with the ATTACH option
  `deletion_vectors true` (now also the default) → tables CREATED in that catalog enable the `deletionVectors` +
  `rowTracking` features (`DeltaWriter.DeletionVectorConfig`; `CreateAsync` declares reader-v3 + the features).
  DELETE follows the TABLE's `delta.enableDeletionVectors` config (`DeltaReader.IsDeletionVectorsEnabled`):
  DV-enabled → `DeleteByRowIdsViaVectorsAsync` (union the new in-file positions into the file's DV, write a fresh
  DV, commit `remove`(old path+DV)+`add`(same path, new DV) — NO rewrite); else copy-on-write. Repeat DV deletes
  COMPOSE because the scan now emits **ABSOLUTE** file positions (`ReadFileAsync` tracks the pre-DV-filter index;
  SAFE for copy-on-write — no-DV tables have absolute==sequential). UPDATE on a DV table is copy-on-write: it reads
  the file WITH the absolute-rowid column (so it matches post-DV survivors) and rewrites a clean file, and its
  `RemoveFile` carries the file's DV so it matches the active `(path, DV)` entry (without that the old file stayed
  active → duplicated rows — the bug found in testing). Verified local (`test/verify_delta_catalog_dv.test`, 48 —
  DV delete + composition + UPDATE-on-DV + post-UPDATE DV delete + re-attach) + delta-kernel `delta_scan` read-back;
  copy-on-write delete (28)/update (63)/write (31) unregressed. **DV write now also live-validated on OneLake
  (2026-07):** CTAS + `DELETE … WHERE id IN (2,4)` on `LH.dbo` with `deletion_vectors true` produced a v2 commit
  that `remove`+`add`s the SAME file (numRecords 5 unchanged, no rewrite) with an **inline `deletionVector`**
  (`storageType:"i"`, `cardinality:2` = the two deleted rows) + row-tracking `baseRowId`/`defaultRowCommitVersion`;
  our read returned the correct survivors (1,3,5). Confirms the RoaringBitmap format fix writes correctly to
  OneLake. **FABRIC READS IT — confirmed (user-verified live, 2026-07):** the reader-v3 + inline-DV table
  (`LH.dbo.fabricator_ewdv_live`) registers in Fabric AND is **SQL-endpoint-queryable** with the deleted rows
  removed. So engineered-wood's DV mode is END-TO-END validated on OneLake: DV write → our read → Fabric SQL
  endpoint. (This supersedes the earlier row-tracking/DV Fabric-conversion failures, which were the pre-fix
  byte-format/protocol bugs; the current inline-DV format is Fabric-readable. Contrast the delta-rs provider,
  which copy-on-writes DELETE and produces no DV.)
  **STANDALONE ROW TRACKING DONE — ATTACH option `row_tracking true`.** Enables the Delta
  `delta.enableRowTracking` WRITER feature (writer-v7 + `domainMetadata`) **independent of `deletion_vectors`**,
  and — unlike DV mode — WITHOUT a reader-v3 bump (`minReaderVersion` stays 1). Verified: commit-0
  `writerFeatures:["rowTracking","domainMetadata"]` + `delta.enableRowTracking=true`; the WRITE commit's `add`
  carries `baseRowId`/`defaultRowCommitVersion` (stable ids for Spark/Fabric). `DeltaCatalog._rowTrackingOnCreate`
  (parsed like `deletion_vectors`) → `DeltaWriter.Create`/`Write(rowTracking:)` → `CreateConfig`'s standalone
  `delta.enableRowTracking` branch (the DV→RT coupling is unchanged). **Our DELETE/UPDATE still use the transient
  `(file,position)` rowid, NOT the stable id** — Delta DML is physical (the copy-on-write rewrite / DV path need
  `(file,position)`, which the transient rowid already IS, computed at scan time); a stable id would have to be
  resolved back to `(file,position)` via per-file `baseRowId` ranges for no benefit under DuckDB's single-statement
  atomic scan→mutate (the transient rowid is valid for exactly that snapshot window). Stable-id DML only pays off
  for cross-snapshot retry, which DuckDB never needs → `row_tracking` is a **write-side interop feature for external
  readers**, not a DML mechanism. `verify_delta_catalog_row_tracking.test` (33 — feature declared, baseRowId
  materialized, DELETE/UPDATE/INSERT unaffected). See [docs/delta-catalog.md](docs/delta-catalog.md).
  **Materialized row tracking (SUPERSEDED 2026-07-13: the `materialize_row_tracking` ATTACH option is
  GONE — materialization is now IMPLIED whenever row tracking is enabled, incl. via the DV default; see
  the consolidation bullet. Historical record of the original opt-in below.) — STABLE ROW ID PRESERVED ACROSS
  MERGE-ON-READ UPDATE, VALIDATED ON FABRIC SPARK (2026-07-04).** The gap: our merge-on-read UPDATE appends the
  post-image row in a NEW file, so its `base_row_id + row_index` changed (stable id 1→3) — proven via Spark
  (`_metadata.base_row_id`). Root cause: EW enabled row tracking but never declared
  `delta.rowTracking.materializedRowIdColumnName`, so Spark couldn't even synthesize `_metadata.row_id` for our
  tables, and the UPDATE-append wrote a fresh `__delta_row_id`. **Fix (opt-in):** `materialize_row_tracking true`
  → `DeltaCatalog._materializeRowTracking` → `CreateConfig` declares
  `delta.rowTracking.materializedRowIdColumnName=__delta_row_id` (+ `...RowCommitVersionColumnName`) at create;
  engineered-wood `UpdateViaVectorsAsync` materializes each appended row's **ORIGINAL** stable id
  (`sourceAddFile.BaseRowId + position`) into `__delta_row_id` (new `RowTrackingWriter.AddRowIdColumn(batch,
  Int64Array)` overload) instead of a fresh id. **Validated live on Fabric Spark:** after `UPDATE … WHERE id=2`,
  Spark reads `_metadata.row_id=1` (PRESERVED, was 3 without this) + `_metadata.row_commit_version=2` (correctly
  bumped) — matching Spark's own writer's reference behavior; untouched rows keep ids 0,2. Local write-shape test
  `verify_delta_catalog_materialize_rowtracking.test` (the appended row carries `__delta_row_id=1`); the row-id
  READBACK is Spark-only (see the validator below). **Default OFF** (no new feature declaration on the DV-default
  path → no Fabric-conversion risk to the validated path). **Compaction materialization — DONE + Fabric-Spark-
  validated (2026-07-04).** Because compaction mixes rows from several source files into one file, `CompactionExecutor`
  materializes BOTH `__delta_row_id` (each source's `baseRowId + position`, or the source's own materialized id when
  present) AND `__delta_row_commit_version` (each source's `defaultRowCommitVersion`) — a single
  `baseRowId`/`defaultRowCommitVersion` on the compacted `add` can't represent them (new
  `RowTrackingWriter.AddRowIdAndCommitVersionColumns`; the compaction read loop builds survivor id/version arrays
  in the DV filter's keep order so they stay aligned). Validated live: CTAS+2×INSERT (3 files) → OPTIMIZE+VACUUM,
  Spark reads `_metadata.row_id` = 0,1,2 (preserved, not the fresh `base_row_id`=3 range) and
  `_metadata.row_commit_version` = 1,2,3 (per-row originals, overriding the single `default_row_commit_version`=1).
  `test/verify_delta_catalog_compaction_rowtracking.test` (24); default compaction path (`verify_delta_catalog_optimize`)
  unchanged.
  **known-issues Delta SWEEP — FIXED (2026-07-05, EW `5e0bba2` + Bridge `50e37ca`; kernel-validated, delta+deltars
  suites green, EW DeltaLake 168 + Table 141 green):** (1) **CHECKPOINT corruption (critical)** — `CheckpointWriter`
  dropped `add.deletionVector` (+ `baseRowId`/`defaultRowCommitVersion`) and the protocol
  `readerFeatures`/`writerFeatures`, so with DV-default DML and `CheckpointInterval=10` **deleted rows RESURRECTED
  after 10 commits** (even same-session; repro'd then fixed writer+reader). Also made checkpoints
  delta-kernel-readable: `metaData.format.options` emitted + top-level action structs NULLABLE per the spec schema
  (kernel rejects always-present structs with null required children — the DV struct is nullable too). (2)
  **Partition-value interop** — null partitions = JSON null in `add.partitionValues` (`__HIVE_DEFAULT_PARTITION__`
  is only the DIRECTORY name; serializer + checkpoint map values null-safe); reads decode null/sentinel/missing as
  typed NULL columns (`BuildConstantArray` also gained Int16/Int8/Float/Double/Decimal128 + invariant culture +
  THROWS on unsupported instead of a wrong-typed string fallback; `GetStringValue` same policy on write); decimal
  partitions dot-notation; timestamp partition values emit the no-fraction spec form when fraction==0. (3) **PATH
  ENCODING (new EW `DeltaPath`)** — partition dirs use Spark's `escapePathName` (escapes `:` `%` `/` …, NOT space —
  Windows-safe), and **`add.path` is the URL-ENCODED form of the on-disk relative path (spec), URL-DECODED at every
  EW read site + the Bridge native-reader URI builders** (rowid ordinal sort stays on the RAW encoded add.path on
  both sides); vacuum protects encoded+decoded names; pre-fix EW tables whose add.path held literal `%XX` need a
  rewrite (documented). This also makes Spark tables with escaped paths (spaces etc.) readable by us. (4)
  **`timestampNtz` feature** — a schema containing `timestamp_ntz` (any naive DuckDB TIMESTAMP!) now declares the
  required reader+writer feature at create; `AddColumnAsync`/`SetSchemaAsync` emit a protocol UPGRADE in the same
  commit when introducing it (legacy versions upgraded to table-features mode with implied features enumerated —
  `LegacyWriter/ReaderFeatures`); previously Spark/kernel REJECTED every table with a naive-timestamp column.
  `generatedColumns` allowlisted + enforced like invariants. (5) **Stats/row-tracking** — `tightBounds:false` on
  DV-carrying adds; string min/max truncated at 32 chars Spark-style (max side last-char-incremented, omitted when
  impossible); **`delta.rowTracking` domainMetadata high-water mark emitted on every id-assigning commit**
  (write/external-commit/merge-on-read-update/compaction) and preferred via max() on snapshot rebuild — without it
  removes REGRESSED the derived mark and a later writer could reassign used row ids; `rowTracking` accepted as a
  READER feature; `commitInfo` gains `engineInfo`+`operationParameters`. **Known kernel quirk (not ours):**
  duckdb-delta/delta-kernel reads a column-MAPPED partitioned table's partition column as all-NULL (physical-keyed
  partitionValues per the Spark convention we validated live; Spark reads it fine). **Second pass (same day):**
  (6) **checkpoint REMOVE TOMBSTONES** — snapshots track tombstones (`Snapshot.Tombstones`, keyed by
  ReconciliationKey — a DV remove+re-add of the same path keeps the old-(path,DV) tombstone, correct);
  checkpoints include unexpired ones (`delta.deletedFileRetentionDuration` honored when parseable, default 7d)
  + `add.tags` preserved through checkpoints; (7) **CDF `_change_data` files under mapping** written
  PHYSICAL-named + field_id'd like data files (`CdfWriter` gets the snapshot; `_change_type` stays unmapped) —
  Spark reads cdc parquet through the table mapping. Remaining documented gaps: binary partition values (clean
  error), orphan DV `.bin` vacuum, nested-field stats.
  **engineered-wood WRITE interop caveats (reviewed 2026-07-02; from its `doc/known-issues.md`):** engineered-wood
  is a from-scratch C# Parquet/Delta stack, so the write path diverges from Spark/parquet-mr in a few subtle ways —
  none a show-stopper (Fabric/DuckDB/arrow-rs read our output, validated live), but the two "Spark-ecosystem
  friendliness" candidates are: (1) **writes DataPage V2 by DEFAULT, not V1** (`ParquetWriteOptions.DataPageVersion`
  defaults to V2; `DeltaWriter.Options()` doesn't override → all Delta writes are DataPage V2, *newer* than Spark's
  V1 default and still "experimental" in parts of the ecosystem; a V1 pin is a one-liner — `DataPageVersion.V1` in
  `DeltaWriter.Options()`); (2) **deprecated parquet `min`/`max` always emitted with SIGNED byte ordering + no
  `column_orders`** → wrong ordering for UTF-8/unsigned columns *if* a legacy reader falls back to them (modern
  readers use `min_value`/`max_value`, correct → low risk). Lesser: ns-timestamp write sets `ConvertedType=micros`;
  Delta stats have no string-min/max truncation + only top-level primitives; DVs are inline/UUID-relative only,
  one file per delete; features only settable at create (matches our design). Both cheap defensive fixes are
  DEFERRED until a concrete reader complains. Full list: [docs/delta-catalog.md](docs/delta-catalog.md)
  §"engineered-wood interop caveats". **The structural fix for all of these = the native-write inversion**
  (DuckDB's parquet writer for data files + engineered-wood only for the `_delta_log` metadata) — **design +
  phasing (incl. rowid/row-commit-version materialization for DML + CDF + a future DuckLake→Delta bridge):
  [docs/native-delta-write.md](docs/native-delta-write.md)**. Positioning: one backend, a `native_write`
  toggle symmetric to `native_read`, with the `delta` alias defaulting both on (hybrid = prod) vs
  `engineeredwooddelta` (pure EW = sample/driver-test). **P0 spike + P1 (INSERT/CTAS/append) DONE (2026-07-04,
  C#-only + one EW seam, NO ABI/C++).** The **`native_write true`** ATTACH option routes INSERT/CTAS/append data
  files through DuckDB's OWN native parquet writer; the `_delta_log` commit stays engineered-wood. **The EW
  seam** = `DeltaTableOptions.DataFileWriter` (new `IDataFileWriter`): `WriteCoreAsync` delegates each partition
  file's parquet bytes to it instead of the built-in `ParquetFileWriter` — partition split / row tracking /
  column mapping / stats / the `add` / commit / OCC unchanged (override null by default → default path
  byte-identical). Fabricator's `NativeParquetDataFileWriter` binds the streamed batch via the existing
  `Host.Query(sql, inputs)` (host-query v42–v45 — so **gate A input-binding worked directly; the temp-IPC gate B
  was never needed**) and runs `COPY (SELECT * FROM <batch>) TO '<root>/<file>' (FORMAT parquet,
  WRITE_BLOOM_FILTER true, RETURN_STATS)`, reading back `file_size_bytes` for `add.Size` (stats stay EW's
  `StatsCollector.Collect(batches)` — batches in hand). URIs reuse `DeltaReader.ToReadableRoot` (onelake://
  rewrite). **§9 acid test PASSED (local):** the official `delta_scan` (delta-kernel-rs) reads it (6 rows, exact
  `DECIMAL(10,2)`+`DATE` — the types EW's codec mangled), and **bloom filters flow through** on dict-encoded
  columns (`grp` 5-distinct → `has_bloom=true`; `id` all-distinct → none) = the maturity win AND a deterministic
  native-write signature (EW default writes no bloom). `test/verify_delta_catalog_native_write.test` (36); default
  path unregressed (write/decimal/temporal/partition/overwrite_merge/update/delete green). **Opt-in only (default
  off);** the `delta`-alias default-on is deferred (needs the resolved provider name threaded to the
  `DeltaCatalog` ctor — a separate policy step). **P3 (DELETE) DONE (2026-07-04):** under `native_write` the
  copy-on-write DELETE rewrites the survivor file with DuckDB's native writer too (the `IDataFileWriter` seam
  generalized to a batch **list** — a rewrite writes a file's survivor batches as one parquet), EW still
  selects/reads the affected files + commits `remove`+`add`; the READ half stays EW's reader (fully-native
  `read_parquet … WHERE file_row_number NOT IN` rewrite deferred), DV-mode delete unchanged (no data rewrite).
  Acid-tested (delta_scan reads the 5 survivors, exact decimals); default delete/dv/update unregressed.
  **P4 (UPDATE) DONE (2026-07-04):** the copy-on-write UPDATE rewrite likewise writes the modified file with
  DuckDB's native writer (same list-form seam at the UPDATE site); EW reads the affected files, applies the SET
  substitution (still C# `BuildArray` — the SQL-join substitution is deferred), and commits `remove`+`add`.
  Acid-tested (constant/string/expression updates → delta_scan matches, exact decimals).
  **P5-append (row tracking) works FREE on the native path (2026-07-04):** `row_tracking true` + `native_write
  true` → EW materializes the row-id column into `physicalBatch` + assigns `baseRowId`/`defaultRowCommitVersion`
  on the `add` before the writer, so DuckDB just emits the bytes (writer-v7 + `rowTracking`, `baseRowId` 0/5,
  delta_scan reads it). `verify_delta_catalog_native_write.test` (87 — INSERT/CTAS/append + bloom signature +
  DELETE + UPDATE + row_tracking + durability); default update/delete/dv/changes unregressed. **VALIDATED LIVE
  on Fabric OneLake (2026-07-04):** workspace `Test` / lakehouse `LH` (schema-enabled) — `native_write true`
  CTAS + INSERT + DELETE + UPDATE on `lake.dbo.fabricator_nwtest` round-tripped correctly over `onelake://` (v56
  OneLake write callbacks + DuckDB's native writer), and a 5000-row low-card table showed the DuckDB bloom
  signature on the dict column via `parquet_metadata('onelake://…')` (EW writes none) — confirming DuckDB (not
  EW) wrote the OneLake parquet. **Partitioned `native_write` DONE (2026-07-04):** EW splits by partition +
  calls the writer per Hive-partition file; `NativeParquetDataFileWriter` creates the `<col>=<value>/` parent
  dir first (`HostFs.CreateDir`, best-effort) since DuckDB's single-file COPY doesn't mkdir — also the
  prerequisite for native CDF `_change_data/`. `verify_delta_catalog_native_write.test` (107).
  **Fully-native copy-on-write REWRITE + SQL-join UPDATE substitution DONE (2026-07-04, commit `143c09d`) —
  read half native, `BuildArray` retired for the native path.** DELETE/UPDATE now READ the source via
  `read_parquet` AND apply the row-level transform in DuckDB SQL, not just write natively. New engineered-wood
  seam **`IDataFileRewriter`** (`DeltaTableOptions.DataFileRewriter`, next to `DataFileWriter`): the copy-on-write
  DELETE/UPDATE loops delegate the source read + transform to it for the clean shape (no column mapping, no
  partitions, no type widening, no CDF — gated in EW), falling back to EW's own reader + the in-process
  `rewriteFile`/`TakeRows` transform otherwise. Fabricator's `NativeParquetDataFileRewriter` (a *reader*: returns
  the transformed batches; EW keeps stats/writer/commit) runs per affected file `SELECT <cols> FROM
  read_parquet(src, file_row_number => true) WHERE file_row_number NOT IN (<deleted positions, bound anti-join>)`
  (DELETE) or a `LEFT JOIN` against the (position → new SET values) rows bound as an Arrow view, substituting via
  `CASE WHEN u.__fabricator_pos IS NOT NULL THEN u.<col> ELSE p.<col> END` (UPDATE) — **retiring the in-process
  typed `BuildArray`** for the native path. **DV-aware** (the file's existing deletion-vector positions are
  passed as `excludePositions`, so a copy-on-write UPDATE on a DV table rewrites only live rows) + **schema-
  evolution backfill** (the rewriter probes the source file's columns via `read_parquet … LIMIT 0` and emits a
  typed `CAST(NULL AS <t>)` for a column ADDed after the file was written — no gate needed). **Stats stay EXACT
  via EW's `StatsCollector` on the returned batches** — `RETURN_STATS`/`parquet_metadata` surface DECODED VARCHAR
  min/max whose float/double rounding could skip live rows (violates the exact-or-nothing rule), so the stats
  bridge is StatsCollector-on-the-batches NOT the footer strings. Verified: §9 acid test (official `delta_scan`/
  delta-kernel reads the native-rewritten table — exact decimal/date/double, DELETE + UPDATE applied) +
  **LIVE on Fabric OneLake** (`read_parquet('onelake://…')` source read + native write over `onelake://`, DELETE
  + UPDATE round-trip, decimals exact); `verify_delta_catalog_native_write.test` (147 — + DV-table UPDATE +
  schema-evolution UPDATE); default-path + delete/update/dv/changes/partition unregressed. **Still pending:
  the P5 REWRITE half (materialize `row_id`+`row_commit_version` on copy-on-write DELETE/UPDATE — the deferred
  gap in BOTH paths, a deep EW row-tracking change) + CDF native change files (CDF tables keep the EW-read path
  today — needs the rewriter to also produce the deleted/pre/post-image rows + `CdfWriter` on the native writer)
  + delta-alias default-on.**
  **STREAMING native bulk write — bounded memory (2026-07-04, C#-only + one EW seam, NO ABI/C++; live-OneLake
  validated).** The prior native_write INSERT/CTAS/append path fully COLLECTED the dataset in C# first
  (`DeltaWriter.Materialize` — an Arrow-IPC round-trip into a `List<RecordBatch>`) before writing — bounded by
  RAM, a problem in a Fabric notebook. **`native_write true` now STREAMS** the bulk write: `DeltaWriter.TryWriteStreaming`
  binds the LIVE channel stream (the same `ChannelArrowStream` the SQL Server path feeds `SqlBulkCopy`, cap 8,
  backpressured) straight into ONE DuckDB `COPY (SELECT * FROM <stream>) TO '<root>/<uuid>.parquet' (FORMAT
  parquet, WRITE_BLOOM_FILTER true, RETURN_STATS)` — DuckDB pulls batches incrementally + streams row-groups to
  disk, so the whole dataset NEVER materializes in C#. `RETURN_STATS` yields the file's `count`(→numRecords) +
  `file_size_bytes`(→add.Size); the single `add` is committed via the **new EW commit-only seam**
  `DeltaTable.CommitDataFilesAsync(IReadOnlyList<WrittenDataFile>, mode)` — the commit half of `WriteCoreAsync`,
  factored out: it builds the `add`(+ `remove`s for Overwrite), assigns row-tracking `baseRowId`/
  `defaultRowCommitVersion` per file (high-water mark derived on snapshot rebuild — no domainMetadata action),
  prepends commitInfo, and writes with OCC retry. **Row tracking rides on `baseRowId`** (no physical
  `__delta_row_id` column — the collect+native path injects one; its ABSENCE in the streamed parquet is the test
  signal that streaming ran). **FULL data-skipping stats** (min/max/nullCount + numRecords) are emitted for the
  streamed file — parsed from DuckDB's `RETURN_STATS.column_statistics` (a **MAP(colname → MAP(statname → text))** —
  a map-of-maps, everything stringified; `NativeParquetDataFileWriter.BuildDeltaStats` walks both `MapArray`
  levels via `KeyValues`) and typed from the write schema into the Delta stats JSON via an **exact-or-omit**
  policy: min/max emitted for integer / decimal / string / boolean / date / naive-timestamp (decoded text is
  exact; integers/decimals via `WriteRawValue` so no double round-trip; timestamp space→'T'), and **OMITTED for
  float/double** (decoded text may round → a too-narrow min could wrongly skip a file — correctness-safe by
  omission) and tz-timestamp / time / nested (format risk); `nullCount` + `numRecords` always (exact integers).
  On any parse hiccup it falls back to numRecords-only (never fails the write for stats). So the streamed file
  gets the SAME skipping quality as the collect path (better on float/double, which the collect path emits
  possibly-imprecise). **PARTITIONED writes STREAM too (local/S3)** — `DeltaWriter.TryWriteStreaming` branches on
  the table's `Metadata.PartitionColumns`: non-partitioned → one COPY to a single file; partitioned →
  `NativeParquetDataFileWriter.RunCopyPartitioned` runs ONE `COPY … TO '<root>' (PARTITION_BY (cols), APPEND true,
  FILENAME_PATTERN '{uuid}', RETURN_STATS)` that streams the whole Hive `col=val/<uuid>.parquet` layout in a
  single pass (bounded memory), excluding the partition columns from the files (Delta convention); `RETURN_STATS`
  returns one row PER FILE → each becomes a `WrittenDataFile` with its `partitionValues` (from `partition_keys`, a
  `MAP(colname→value)`) + per-file stats, all committed in one `CommitDataFilesAsync`. `RunCopy` returns per-file
  `CopiedFile` records (relpath from `filename`, rows, size, partitionValues, stats); the shared `ReadFileStats`
  parses both. **Partitioned streaming works on OneLake too** — DuckDB's partitioned COPY needs a writable
  DIRECTORY target, which the `onelake://` C++ FileSystem now supports: `FabricatorOneLakeFileSystem` overrides
  `DirectoryExists`→false + `CreateDirectory`→no-op (ADLS Gen2 dirs are implicit — a blob write materializes the
  hierarchy), and the managed `OneLakeForwardFs.Exists` returns FALSE for a directory (via the `hdi_isfolder`
  metadata marker) so DuckDB's setup doesn't mistake the table root for a file (the old *"exists and is a file,
  not a directory"* error). With `APPEND true` + `FILENAME_PATTERN '{uuid}'`, `CheckDirectory` early-returns and
  the per-partition files are written by `OpenFile`-for-writing (the same mechanism the single-file path uses).
  **Falls back to the collect path** (`Materialize` + `Write`, `data` untouched — the fallback is decided BEFORE
  any COPY via EW's `SupportsExternalDataFileCommit` + `Metadata.PartitionColumns`, so no orphan file) only for:
  `replace_where`, `schema_mode=merge`, or a table needing EW's own writer (column mapping / identity /
  IcebergCompat). The **EW-codec path (`native_write` off) is unchanged** — still collects (the user's call: only
  the native path streams). DELETE/UPDATE (rewriter paths) unaffected.
  Verified: `test/verify_delta_catalog_native_write_streaming.test` (29 — no `__delta_row_id` in the streamed
  file, bloom signature present, **min/max/nullCount in the commit: int→JSON number, string→JSON string,
  double→omitted**, 8000-row append, **partitioned CTAS+INSERT stream: Hive layout, partition column excluded, no
  `__delta_row_id`, per-file partitionValues+stats in the commit**) + native_write (147) + optimize/partition/
  overwrite_merge/update/dv/write/native_read/decimal/changes/time_travel/alter/snapshots unregressed; **live
  OneLake** (non-partitioned CTAS 5000 + append→8000 + DELETE→7920; **partitioned CTAS+INSERT stream —
  `partitioned copy onelake://… → committed v1/v2 files=3 (native COPY, bounded memory)`, 200 rows/region**;
  commit carries `minValues`/`maxValues`/`nullCount`).
  **OCC RETRY DONE (concurrent writers):**
  engineered-wood `WriteCommitAsync` throws `DeltaConflictException` when a concurrent writer takes the target
  version; `DeltaWriter.Write`/`Create` (append/CTAS/create) catch it and retry by reopening at the new latest
  version (bounded `MaxCommitAttempts=16`) — the data is snapshot-independent so re-commit is safe. Rowid
  DELETE/UPDATE do NOT retry (their absolute positions are tied to the scanned snapshot; a concurrent change
  invalidates them) — `DeltaReader` surfaces a clear "concurrent modification — retry the statement" error.
  Verified: 4 parallel processes appending 200 rows each to ONE local Delta table → 800/800 distinct, no lost
  commits, no surfaced conflicts.
  **EXPLICIT TRANSACTIONS — SNAPSHOT-ISOLATED, BUFFERED (slices 1–4, 2026-07-07). Consolidated
  semantics reference (modes × paths × isolation, rebase checks, guards, multi-writer by storage
  + §10 Databricks-comparison SQL scenarios — repeatable reads + atomic multi-statement + true
  ROLLBACK are OUR advantages over classic Databricks/Spark [no multi-statement txns there,
  snapshot per QUERY]; Databricks' advantages = row-level concurrency [DBR 14.3+, DV-based
  same-file disjoint-row DML — we conflict at FILE level like OSS Spark] and the
  `delta.isolationLevel` TABLE property — **NOW HONORED (2026-07-15): the effective isolation for the
  buffered-DML flush's OCC check + row-level relaxation is the TABLE's `delta.isolationLevel` (Serializable
  vs the WriteSerializable default), read once per (txn,table) and cached on the buffer
  (`PendingSerializable`); the ATTACH `isolation_level` option is the FALLBACK when the property is absent
  (backward-compatible — property-less tables follow the catalog default). So our writer conforms to the
  guarantee the table ADVERTISES, uniform with Spark — the whole reason Delta centralizes isolation as a
  table property (mixed per-writer levels don't corrupt — each writer's own check preserves its own
  guarantee — but they make the table's advertised guarantee non-uniform). Autocommit single-statement DML
  still uses the catalog default for its row-level-retry resilience knob (documented minor divergence).
  Change a table's level (or any `delta.*` config) with the new **`fabricator_delta_set_tblproperties(catalog,
  'schema.table', '{"delta.isolationLevel":"Serializable"}')`** table function (SET/UNSET via ONE metaData
  commit — merged `configuration`, rides `extraActions` like a buffered ALTER; feature-enabling keys
  [`delta.enable*`, `columnMapping.mode`] rejected — those need a protocol upgrade at CREATE via the ATTACH
  option; kernel-valid; re-attach-durable) + **`fabricator_delta_tblproperties(catalog, 'schema.table')`**
  (READ, (property,value) rows). Both are table functions (side-effecting op must NOT be a scalar — optimizer
  purity), additive metadata kinds 13/14 (NO ABI bump), Bridge-only (no EW/C++-operator change — just 2 binds).
  `test/verify_delta_tblproperties.test` (34 — round-trip/UNSET/guard/re-attach + the property overriding a
  write_serializable catalog in a two-connection racer). Regression-free (transactions 941 / row_level 70 /
  update 63 / delete 28 / row_tracking_virtual 299 / dv_default 58 / txn_version 51 / changes 73 / native_write
  147). **CREATE-TIME STAMP — DONE (2026-07-15):** ATTACH `isolation_level 'serializable'` now stamps
  `delta.isolationLevel=Serializable` into CREATEd tables (all 3 create paths — `DeltaWriter.Write`/
  `TryWriteStreaming`/`Create` gained a `serializable` param threaded from `_serializable`); write_serializable
  is the Spark default so it's left ABSENT (no stamp), matching Spark's minimal metadata. `verify_delta_tblproperties`
  now 42 (serializable-ATTACH create stamps it; default create leaves it absent). **STALE-BINARY:** the loadable +
  linux payload predate the 2 new C++ function binds — rebuild before the next dbt/notebook run.):
  [docs/delta-transactions.md](docs/delta-transactions.md).** The engineered-wood
  Delta provider buffers a DuckDB transaction's writes per (txn, table) (`DeltaTxnBuffer`, keyed by the v35
  `AmbientTransaction` id) and flushes at COMMIT as **ONE atomic Delta commit per table** (Delta has no
  cross-table txn); **ROLLBACK discards** (streamed-but-uncommitted parquet = invisible orphans → vacuum,
  Spark's rollback shape). **Slice 1 — appends (all txns incl. autocommit, semantics-neutral there):**
  `native_write` streams files as before but PARKS the `WrittenDataFile` list
  (`TryWriteStreaming(deferCommitTo:)`; flush = `CommitDataFilesAsync` with OCC retry — appends are
  snapshot-independent); the codec path parks materialized batches (flush = `DeltaWriter.Write`).
  **Slice 2 — buffered DELETE/UPDATE = snapshot isolation (EXPLICIT txns only, gated by the v60
  `begin_transaction(is_explicit)` flag; autocommit keeps the direct per-statement paths — CDF capture,
  copy-on-write — byte-identical):** DELETE buffers (pinned-snapshot file ordinal → absolute positions)
  per table (`BufferDeleteRows` — rowids decoded Bridge-side); UPDATE buffers its old rows the same way +
  builds post-image rows at statement time (`BufferUpdateRows`: EW `ReadRowsByRowIdsAsync` reads exactly
  the matched rows, then the SET values substitute via the existing `BuildArray`; post-images join the
  pending append batches). **LIFETIME CORRECTION (2026-07-16, user-driven): the long-recorded "EW batch
  buffers don't outlive the open table" belief is FALSE and the read-back's Arrow-IPC deep copy was
  REMOVED** — EW batches are SELF-OWNED (every `ArrowArrayBuilder` output is a fresh
  `Apache.Arrow.Memory.NativeBuffer` native allocation behind a refcounted `SharedMemoryHandle` WITH a
  finalizer; decode-path `ArrayPool` rentals are all rent→copy-out→return in-scope; `DeltaTable.Dispose`
  has been a no-op `_disposed` flag since EW's original April commit — it never freed anything). Proven
  empirically (scratchpad/ewlifetime harness: materialize `ReadRowsByRowIdsAsync` + `ReadChangesAsync`
  with NO copy → dispose table → 20 re-reads churning the ArrayPool + disposing churn batches → forced
  GC+finalizers ×3 → all 20k rows byte-exact). The 2026-06-30 GetChanges "Out of Range string size" that
  spawned the belief was almost certainly the CDF update_preimage 6-vs-5-column schema mismatch fixed in
  the SAME commit (`7f0563a`), misattributed to buffer lifetime. `GetChanges` stays lazy purely for
  bounded memory. Batches imported over the C ABI (bulk channel) are a DIFFERENT lifetime domain — their
  copies (`DeltaWriter.Materialize`, `ProjectPending` clone) are NOT covered by this finding; leave them.
  **Follow-up (same day, user's design): the read-back is now a LAZY STREAM** — `DeltaReader.
  ReadRowsByRowIds` returns `IEnumerable<RecordBatch>` (async iterator holding the table open for the
  enumeration + a net8-safe `BlockingEnumerable` adapter — .NET 9's `ToBlockingEnumerable` is unavailable
  on the net8.0 target; EW's `ReadRowsByRowIdsAsync` param order changed to token-LAST, sourceTracking
  entries appended BEFORE each yield so in-loop indexing works). The **CDF-DELETE capture is fully
  streaming** (read-back → `DropRowIdStreaming` [per-batch dispose after consumption — the derived batch
  aliases the source columns] → `WriteCdcFiles(IEnumerable<RecordBatch>)` with LAZY table open, so an
  empty statement still never touches storage — one batch in flight, a huge CDF DELETE never materializes
  its matched rows). The **buffered-UPDATE deliberately keeps its pre/post-image accumulation**: the
  all-or-nothing stable-ids decision (original vs fresh `__delta_row_id`) is only known after the WHOLE
  statement, so its post-images can't stream into the eager write without changing row-identity semantics.
  **SNAPSHOT READS ARE THE DEFAULT (2026-07-11)**: inside an explicit transaction, the FIRST scan captures
  one UTC instant (`SnapshotPinning`, per txn — the MVCC snapshot-at-first-read shape, like Postgres
  REPEATABLE READ; capturing at literal BEGIN is impossible since catalogs are touched lazily) and each
  table resolves it to a version on first touch — EVERY scan in the transaction then reads that consistent
  cut on BOTH paths (native always did; the CODEC plain/rowid reads now route through
  `GetSchemaAt`/`StreamAt`/`StreamWithRowIdsAt` at the pin — previously codec reads floated to latest until
  the first DML pinned). A concurrent commit is invisible mid-transaction; autocommit is unchanged (a
  single codec statement is one snapshot anyway; native pins per statement for the cross-table cut).
  Recording happens in `ScanTable` alongside the read-set capture (skipped for pending-CREATED tables —
  nothing on storage to pin, the gotcha found in the build). **PinnedVersion** (= the pin above; the DML
  paths reuse it via `TryGetPinned ?? profile.Version`, so buffered positions are always consistent with
  what the pinned scans read); duckdb-delta's `VERSION`/`PIN_SNAPSHOT` ATTACH options were considered and
  REJECTED for the folder-catalog (they pin ONE table — duckdb-delta attaches a single table; a
  catalog-wide version is meaningless across tables — the per-txn instant→per-table-version mapping is the
  correct multi-table analog and is now the default). **Flush fusion**
  (`FlushDmlTransaction`) with **Spark-style LOGICAL REBASE on concurrent writers (2026-07-11), FULL
  ConflictChecker parity incl. READ-PREDICATE tracking**: when the table moved past PinnedVersion, EW
  **`CheckLogicalRebaseAsync(pinnedSnapshot, plannedActions, readPredicates, readWholeTable,
  serializable)`** walks the concurrent commit range (pinned+1..latest via `TransactionLog.ReadCommitAsync`)
  and decides whether the concurrent commits COMMUTE — commuting appends pass (the COMMIT rebases on top:
  DV ordinals/old-DVs resolve against the PINNED snapshot via
  `ComputeDeletionVectorActionsAsync(resolveAgainst:)` since the newer snapshot's path-sorted ordinals
  differ, the remove+add pairs stay valid because the touched files are unchanged, and row-id/identity
  high-water marks re-derive from the snapshot committed onto); REAL conflicts abort with the clear
  "transaction conflict … the concurrent changes do not commute" error. **The four checks (Spark
  ConflictChecker parity)**: metadataChangedCheck (schema/partitioning/config — buffered ALTERs chained
  against pinned metadata), protocolChangedCheck, concurrentDeleteDeleteCheck (any planned `RemoveFile`
  whose (path, DV) is no longer active unchanged — covers concurrent DELETE/UPDATE/OPTIMIZE of a file we
  modify), and the **READ-SET checks**: concurrentDeleteReadCheck (a concurrent data-changing remove of a
  file our reads consumed) + concurrentAppendCheck (a concurrent data-changing add matching our reads —
  from non-blind-append commits always; from blind appends only under `isolation_level 'serializable'`).
  **The read set**: `ScanTable` records every explicit-txn scan's PUSHED predicate (the built EW
  `Predicate` — a superset of the rows consumed, since unpushed residue filters above the scan) or a
  whole-table flag when nothing pushed, on `DeltaTxnBuffer.PendingAppends.ReadPredicates`/`ReadWholeTable`
  (deliberately NOT in `HasAny` — read-only entries trip no guards; `spec == null` scans are the BIND-TIME
  schema probe, excluded — the gotcha found in the build: every statement's first ScanTable call is the
  probe with no spec, which recorded a false whole-table read). Predicate-vs-file matching =
  `DeltaFilePruner.ShouldInclude` over the pinned schema (partition values exact, stats conservative);
  `dataChange=false` actions (OPTIMIZE) are exempt from the read checks (rows unchanged; a compaction of a
  file we MODIFY still hits delete/delete). **The `isolation_level` ATTACH option**
  (`'write_serializable'` default = Spark's default | `'serializable'`): serializable makes commit order
  the logical order — blind appends matching the reads abort too — and the FIRST read of a table also PINS
  the txn's base version (`SnapshotPinning`), so an APPEND-ONLY transaction that read the table routes
  through the checked flush (under write_serializable it stays on the blind OCC path — a documented
  divergence: Spark would deleteRead-check append-only txns too). The commit runs `expectedVersion:
  CurrentSnapshot.Version` in a bounded reopen+revalidate retry loop (a writer landing mid-flush).
  Two-connection racer tests pin all outcomes: append→rebase-success (both changes land), same-file
  delete/delete→abort, concurrent ALTER→abort, serializable+predicate-matching append→abort,
  serializable+non-matching append→rebase-success (stats exclude), whole-table-read (unpushable WHERE) +
  concurrent delete of an unmodified file→deleteRead abort. **ROW-LEVEL CONCURRENCY v1 (2026-07-14,
  Databricks-style — OSS Spark/delta-rs/kernel are all FILE-level): concurrent DML touching the SAME
  FILE no longer conflicts when the touched ROWS are disjoint** (under `write_serializable` only;
  `serializable` keeps strict file-level checks; DV tables only — CoW rewrites can't reconcile, so
  the `deletion_vectors false` PolyBase recipe keeps file conflicts). Mechanism = EW
  **`RebaseDvDmlActionsAsync`**: the loser's DV remove+add pairs re-target onto the LATEST snapshot —
  path must still be active (a concurrent OPTIMIZE/CoW rewrite stays a conflict; v2 idea = row-id
  remap across rewrites via `__delta_row_id`), OUR newly-deleted positions must be DISJOINT from the
  concurrent deletions (absolute in-file positions are stable across DV swaps — same-row overlap ⇒
  "row-level conflict on file … N row(s) … concurrently deleted or updated"), then remove(path,
  currentDV) + add(path, currentDV ∪ ours); post-image adds re-derive baseRowId/version + the HWM
  action from the target snapshot. Wired THREE places: the buffered-flush retry loop (rebases per
  attempt; `CheckLogicalRebaseAsync(rowLevelDml:)` relaxes deleteRead for still-active paths +
  concurrentAppend for swap re-adds — so a txn that READ a file concurrently DV-swapped no longer
  aborts) + BOTH autocommit DML paths (`DeleteByRowIdsViaVectorsAsync`/`UpdateByRowIdsAsync` gained
  `rowLevelRetry` — a bounded reload+rebase+retry loop replaces the old "retry the statement" error).
  BEYOND Databricks: works on PARTITIONED tables and inside multi-statement transactions (theirs is
  unpartitioned + per-statement). Semantics note: position-targeted DML can't produce write skew
  (SET values derive only from the matched row), so the relaxation is serial-equivalent. Transactions
  §6e REWRITTEN (the old whole-table-read deleteRead abort now COMMITS — both deletes land — with a
  `serializable` strict counterpart pinned; suite 941). **v2 (same day): the REWRITE boundary is gone —
  a concurrent OPTIMIZE / CoW rewrite of a touched file REMAPS instead of conflicting**
  (EW `RemapRowsAcrossRewriteAsync`, invoked from the rebase when a touched path vanished): the
  TOMBSTONED source file (parquet on storage until VACUUM) is read at the base snapshot to resolve the
  target rows' STABLE IDS + ORIGINAL commit versions (materialized columns else baseRowId+position /
  defaultRowCommitVersion); the post-rewrite files (active-in-to \ active-in-from; compaction-shaped
  dataChange=false candidates first, early exit; fresh appends can't hold the ids — derived ids sit
  above the base HWM, so only materialized-id files match) are scanned for the ids; **the row's commit
  version is the concurrent-modification discriminator** (relocated-untouched keeps its original
  version — compaction + CoW pass-through both materialize it; concurrently UPDATED carries the
  rewrite's version ⇒ row-level conflict; found NOWHERE ⇒ concurrently deleted ⇒ conflict — a
  DV-hidden row resolves here too); found positions become DV pairs on the NEW files. Requires row
  tracking (the default; no-tracking tables keep the path-level conflict). **Databricks itself still
  conflicts on compaction — v2 is capability beyond ANY Delta engine.** The rowLevelDml read-check
  relaxation is now a FULL skip (the row-level write validation replaces deleteRead+append checks —
  WriteSerializable's reads-don't-serialize definition; matches Databricks' WS matrix). Racer test now
  70 (DELETE + buffered UPDATE THROUGH a concurrent OPTIMIZE compose — value lands on the compacted
  file, post-image kept; same-row-through-rewrite conflicts via the version discriminator / not-found);
  kernel reads the remapped commits exactly; dv 48 / dv_default 58 / update 63 / delete 28 /
  changes 73 / row_tracking_virtual 299 / txn_version 51 / optimize 40 / transactions 941 + EW 168 &
  147 (all TFMs) green. Semantics doc: docs/delta-transactions.md §6 + §10.4 (now PARITY+). **EW now carries SELF-CONTAINED
  xunit suites for the whole transaction-era API surface (2026-07-14, for upstream review — Curt can run
  them without our DuckDB harness): `RowLevelConcurrencyTests` (7 — v1/v2 racers via two table handles),
  `LogicalRebaseTests` (7 — WriteSerializable vs Serializable blind-append semantics, stats-based read
  matching, deleteRead/delete-delete/metadata, compaction exemption), `BufferedTransactionTests` (6 —
  fused ALTER+INSERT+DELETE one-version commit, chained Compute*, ReconcileBatchToFields overlay,
  ReadRowsByRowIdsAsync atVersion, expectedVersion abort, txn-action round-trip),
  `SchemaWriteModesTests` (6 — SetSchemaAsync adopt/no-op, repartition-on-overwrite, static+dynamic
  partition overwrite, appendOnly), `IdentityTransactionSeamsTests` (4 — mark chaining, fused HWM
  commit, ForSchema pending-create, un-valued guard), `WriterFeatureEnforcementTests` (4),
  `TimestampResolutionTests` (1) — Table.Tests now 182 (was 147 pre-row-level; older "EW 168 & 147"
  gate counts in this file are historical). doc/upstream-candidates.md refreshed: on-disk-DV second
  pass in slice 2, row-tracking preservation + MoR matrix + S3 conditional-put in slice 8, NEW slice 9
  (row-level concurrency), test pointers per slice. Fork pushed through `d09b966` (PR #4 auto-tracks).
  STALE-BINARY NOTE: ~~the linux notebook payload + the loadable were built BEFORE the mark-join DELETE
  fix~~ — **REBUILT 2026-07-15 on the Fabricator-rename branch** (post-rename + post-DELETE-fix):
  `build/linux-payload/fabricator.duckdb_extension` (34 MB, linux_amd64) + `fabricator-fdd-linux-x64.zip`
  (loose-root FDD, net8.0) + the Windows loadable `build/release/extension/fabricator/fabricator.duckdb_extension`
  were all current AS OF 2026-07-15 (the linux payload has since gone stale again — see the BINARY
  STATUS note at the ABI-version bullet). The `scratchpad/fabricnb` driver was updated to the fabricator payload names (its Fabric
  NOTEBOOK item name stays `arrownet_ext_probe` — the SP can't recreate notebooks). Linux build: rsync the
  renamed `src/` into WSL `~/sqlext`, `cmake --build … --target fabricator_loadable_extension`.
  **LINUX LOAD-SMOKE VALIDATED (no dotnet needed pre-installed):** the FDD payload is framework-dependent, so
  a **downloaded-and-extracted** .NET 8 runtime suffices — `dotnet-install.sh --runtime dotnet --install-dir
  ~/dotnet8` (no root/install), `export DOTNET_ROOT=~/dotnet8` + `FABRICATOR_MANAGED_DIR=<extracted FDD zip>`,
  then the duckdb wheel MATCHING the loadable's declared version (1.5.5 since the bump; `pip install --target`, no venv) `load_extension`s the loadable. On WSL Ubuntu
  22.04 (glibc 2.35 = Fabric Azure-Linux-3 baseline): `fabricator_version()`, delta CTAS/scan/filter-pushdown,
  and rowid DELETE all pass — so the payload is proven to LOAD + RUN on linux, not just compile.** **IDEMPOTENT APPENDS (2026-07-11) — Delta
  APPLICATION TRANSACTIONS (the `txn` action; duckdb-delta/Spark txnAppId parity, additive metadata kinds
  10/11 — NO ABI bump):** `CALL fabricator_delta_set_transaction_version(catalog, 'schema.table', app_id,
  version [, expected_previous])` PARKS the version on the current EXPLICIT transaction
  (`DeltaTxnBuffer.AppTxnVersions`; requires BEGIN — error otherwise; also pins the base version, so an
  append-only producer transaction routes through the checked flush); at COMMIT the flush
  compares-and-swaps against the LATEST snapshot's `AppTransactions` on EVERY retry-loop attempt (expected
  omitted/NULL = must-not-exist) and emits one spec `txn` action per app ATOMICALLY with the fused commit —
  a retried batch whose first attempt actually landed (crash-after-commit, the failure class plain OCC
  can't protect) fails the CAS with "transaction version conflict" instead of duplicating data.
  `fabricator_delta_get_transaction_version(catalog, 'schema.table', app_id)` reads the committed
  high-water mark (NULL row when never set; EW `Snapshot.AppTransactions` — the read side always existed).
  The C++ binds set the FIXED (app_id, version) schema directly — deliberately NO PopulateReturnSchema
  probe, so the side-effecting factory runs only at EXECUTION where ArrowStreamInitGlobal establishes the
  ambient txn (the bind-time probe would see no transaction and throw). kernel-reads the txn-action
  commits; `test/verify_delta_txn_version.test` (51 — lifecycle, blind-retry CAS failure + no duplicates,
  chained expected, two-producer race → exactly one wins, per-app independence, ROLLBACK discards,
  re-attach durability). Flush mechanics: write pending batches as files via the new
  EW **`WriteDataFilesAsync`** (write-no-commit half of
  the batch path: partition split, recursive mapping rename+field-ids, variant transport, `IDataFileWriter`
  seam, stats; NO row-id materialization — baseRowId assigned at commit like the streaming writer), compute
  DV actions via the new EW **`ComputeDeletionVectorActionsAsync`** (positionsByOrdinal → remove/add pairs
  with unioned inline DVs — pure metadata, no CDF), then ONE
  **`CommitDataFilesAsync(files, Append, extraActions: dvActions, expectedVersion: pinned, operation:)`**
  (extended: extraActions join the commit; expectedVersion ⇒ conflict-abort instead of the append retry;
  `HonorWriterFeatures(isAppend:false)` when removes present). commitInfo operation = DELETE / UPDATE /
  WRITE when single-kind, TRANSACTION when mixed. **Read-your-writes** on every in-txn scan: codec appends
  concat `ProjectPending` (`ArrayData.Clone` per yield; list snapshotted; synthetic rowid
  `(0x700000<<40)|pos`); with pending DML the codec scan is FORCED onto `StreamWithRowIdsAt(pinned)` and
  `DeltaTxnBuffer.ExcludeDeleted` drops the pending-deleted rows (rowid col removed again unless requested);
  native_read merges pending deletes into each file's DV exclusion (`WithPendingDeletes`) + appends pending
  FILES to the per-file loop (ordinals from 0x780000) + honors the buffer pin over SnapshotPinning; a
  `native_write`-without-`native_read` catalog mid-txn routes scans through `ScanNative`. Explicit `AT`
  time travel EXCLUDES pending. **Guards (never silently non-atomic — clean errors with the autocommit
  escape):** buffered DML requires DV-enabled + non-CDF tables (UPDATE additionally
  `SupportsExternalDataFileCommit` + not materialize_row_tracking); DML on rows inserted/updated in the
  SAME transaction ("COMMIT the inserts first" — pending rowids ≥ 0x700000); DROP/CREATE-OR-REPLACE/
  OPTIMIZE/VACUUM/non-append writes with ANY pending changes ("uncommitted buffered changes — COMMIT
  first"). A statement error ABORTS the DuckDB txn. **Slice 3 — buffered `ALTER TABLE … ADD COLUMN`
  (top-level; explicit txns):** the metaData (+ merged protocol-upgrade) action is computed at statement
  time via the new EW **`ComputeAddColumn(field, baseMetadata, baseProtocol)`** (compute-only extraction of
  AddColumnAsync — chained adds compose against the previous pending metadata/protocol; Bridge
  `MergeProtocol` unions feature lists) and parked on the buffer (`PendingMetadata`/`PendingProtocol`/
  `PendingDeltaSchema`/`PendingArrowSchema`); it joins the SAME fused commit → **ALTER + INSERT + DELETE +
  UPDATE in one BEGIN..COMMIT = ONE atomic Delta version** (kernel-validated: delta-kernel reads the fused
  metaData+protocol+DV+adds commit exactly), and ROLLBACK undoes the column. Overlays: `GetMetadata(Columns)`
  serves the pending schema (covers DuckDB's bind + the C++ eager post-ALTER re-fetch); codec scans strip
  pending-only names from the EW projection and RECONCILE each batch to the pending shape
  (`ReconcileBatch` — added columns backfilled via the typed-NULL `BuildArray`; `ExcludeDeleted` now derives
  its output shape from each batch); native scans pass the pending Delta schema into
  `ListNativeScanFiles(schemaOverride:)` so the per-file presence machinery emits `CAST(NULL AS type)`
  exactly like committed schema evolution (mapping maps recomputed from the pending schema — the added
  column's id/physicalName was assigned by the compute step); the buffered UPDATE's read-back reconciles
  before substitution (so `UPDATE t SET <new column> = …` works in the same txn); flush writes batches via
  `WriteDataFilesAsync(schemaOverride:)` and `BulkInsert` skips the streamed COPY under a pending ALTER
  (collect path, still native-written via the writer seam); pure-ALTER commit operation = the tracked kind
  ("ADD COLUMNS"/"RENAME COLUMN"/"DROP COLUMNS", several kinds → "ALTER TABLE"), mixed with data =
  "TRANSACTION". Order rule: ALTERs must come BEFORE the transaction's data changes ("after buffered data
  changes" error — writes then run schema-overridden; changing the schema under buffered rows is
  unsupported). **Slices 3b+3c — buffered RENAME/DROP COLUMN + nested ADD/DROP FIELD** (same mechanism; EW
  gained the chainable compute-only counterparts `ComputeRenameColumn`/`ComputeDropColumn`/`ComputeAddField`/
  `ComputeDropField` + the public **`ReconcileBatchToFields`** export of the read path's RECURSIVE
  schema-evolution reconcile — which made the nested codec overlay free). RENAME's read overlay is a
  **rename map** (pending name → committed name, composed across chained renames; a renamed pending-ADDed
  column deliberately has no entry): the codec projection translates pending→committed names for the EW
  read, `ReconcileBatch` re-labels batch columns committed→pending BEFORE the recursive reconcile (a naive
  name-match would silently NULL a renamed column — the trap that kept rename out of 3a), and the native
  path gets rename free (mapping physical names/field-ids are rename-stable, maps recomputed from the
  pending schema). DROP falls out of the reconcile's project-target-fields semantics. `AlterOps` on the
  buffer tracks kinds. Boundaries: **nested RENAME FIELD** stays IMMEDIATE (a per-level nested name map in
  the overlay — deferred; pinned in the test) + **RENAME of a partition column** in a txn is rejected (the
  flush's partition split runs against committed partition columns; clear error) + RENAME TABLE stays
  immediate (physical folder move). **Slice 4 — buffered CREATE TABLE / CTAS (fresh tables, explicit
  txns):** the table exists ONLY in the buffer until COMMIT (`PendingCreate` +
  `PendingArrowSchema`/`CreatePartitionColumns`; NOTHING touches the `_delta_log` before the flush — the
  key constraint is that DuckDB's rollback callback has no ClientContext/opener, so rollback can only
  DISCARD, never clean storage). Scans (`ScanPendingCreated` — both codec + native entry points) and
  binds (`GetMetadata(Columns)` via `PendingArrowSchema`) serve entirely from the buffer; CTAS collects
  (the streamed COPY would OpenOrCreate commit-0 mid-txn — the append branch also skips streaming under
  `PendingCreate`); the flush = today's autocommit CTAS commit shape (v0 CREATE TABLE + ONE WRITE for ALL
  buffered rows — CTAS + later INSERTs merge; single-commit CTAS = EW follow-up), with a concurrent
  same-name create conflict-aborting (commit-0 put-if-absent arbitrates; pre-check for the clear error).
  **CREATE + DROP in one txn cancels out** (`RemoveTable` — nothing ever on storage). Boundaries:
  ALTER/DELETE/UPDATE on a pending-created table error cleanly ("created in the same transaction — COMMIT
  the CREATE first"); identity-marked creates + CREATE OR REPLACE + CTAS over an existing table stay
  immediate (replace removes are snapshot-coupled; identity needs EW's committing writer). dbt is
  unaffected either way (a buffered model build is simply atomic now). Spark-style logical rebase on
  conflict: DONE 2026-07-11 — see the flush-fusion paragraph above (`CheckLogicalRebase`,
  WriteSerializable). C++: `CommitTransaction` sets `SetActiveOpener(&context)`
  before the ABI call (the flush writes `_delta_log` through the host FS); rollback needs no opener (the C++
  `InvalidateAllEntries` on rollback drops the pending-schema catalog entry).
  `test/verify_delta_catalog_transactions.test` (542 — atomic multi-INSERT, buffered DELETE/UPDATE with
  mid-txn visibility + rollback-undoes + one-commit-per-txn + operation names, mixed
  INSERT+DELETE+UPDATE→TRANSACTION, conflict-abort via a second connection, DV-off/CDF/same-txn-row guards,
  multi-table, AT-excludes-pending, re-attach durability, native_write fused flush (1000-row stream +
  DELETE + UPDATE), native_read read-your-writes, autocommit version counts + direct-DELETE path, buffered
  ALTER+DML fused commit + rollback-undoes-column + chained ADDs + SET-on-added-column + ALTER-after-data
  guard + both native paths, buffered RENAME (data under the new name mid-txn + filter-on-renamed +
  rollback restores) + DROP (rollback restores column+data; pure-DROP op name) + chained
  add→rename-pending→add mixed + nested ADD/DROP FIELD fused with INSERT + nested-RENAME-stays-immediate +
  partition-column-rename guard, buffered CREATE + CTAS (mid-txn queryable, rollback-leaves-nothing incl.
  re-attach proof, CTAS+INSERT one WRITE, CREATE+DROP cancels, pending-create ALTER/DML guards, empty
  create, partitioned CTAS-in-txn, native_write CTAS + rollback + retry));
  `verify_delta_catalog_constraints.test` §4 re-pinned; full delta sweep 45/45; EW DeltaLake 168 + Table
  147 (both TFMs); SQL fn suites 7/7 (v60 lockstep); kernel readback of the fused commits
  (metaData+protocol+DV+adds, rename+nested-add/drop, and buffered-CREATE shapes) via the official delta
  extension. **PROVIDER RENAMED to `engineeredwooddelta`** (the engineered-wood-backed Delta
  provider), with **`delta` + `deltalake` kept as aliases** (non-breaking — `BackendRegistry` resolves Name +
  Aliases case-insensitively; all `verify_delta_*` tests still ATTACH with `PROVIDER 'delta'`). The distinct
  primary name reserves space for a future delta-rs/delta-kernel production provider. `test/verify_delta_rename.test`
  (12 — new name + both aliases). Other remaining (OPTIONAL): a `delta-rs` production provider (`deltars`) via the
  **delta-dotnet** binding — **design + build-feasibility (compiles on Windows, no WSL) in
  [docs/delta-rs-provider.md](docs/delta-rs-provider.md)**: it's the better reader/writer/maintenance engine
  (reference impl, standard-compliant writes, OPTIMIZE/Z-ORDER/VACUUM/CHECKPOINT/MERGE, DataFusion pushdown,
  object_store cloud IO); DELETE/UPDATE now work by mapping DuckDB's rowid DML to a **record-batch MERGE**
  (all columns = the rowid; see below), so `deltars` coexists as an opt-in read/write/**DML**/maintenance
  provider (`engineeredwooddelta` stays the default, incl. ALTER). **v1 BUILT +
  verified (local FS)**: `dotnet/Fabricator.DeltaRs` (`DeltaRsBackend`/`DeltaRsCatalog`/`StorageOptionsCodec`),
  registered in `BackendRegistry` (skipped if not published), opt-in publish via `publish-managed.ps1
  -IncludeDeltaRs` (adds `DeltaLake.dll` + the ~240 MB native `delta_rs_bridge.dll`/`delta_kernel_ffi.dll`);
  NO ABI/C++ change. Working: `ATTACH … (PROVIDER 'deltars')` (+ alias `delta-rs`), scan (via
  `ReadAsArrowTableAsync` — NOT DataFusion `QueryAsync`, whose schema diverged and SIGSEGV'd arrow_ingest),
  CREATE/CTAS/INSERT/**COPY** (append+overwrite; `COPY … (FORMAT mssql)`: CREATE_TABLE false→append,
  default→overwrite, new-table→create; `SCHEMA_MODE 'overwrite'` adopts schema [SchemaMode::Overwrite],
  `'merge'` appends + unions new source columns/old rows NULL [the bridge's `table_insert` maps a plain Append
  with `overwrite_schema=false` to delta-rs `SchemaMode::Merge`, so merge = force Append]), metadata, snapshots
  (`HistoryAsync`), **DELETE + UPDATE**, re-attach durability; `test/verify_delta_rs*.test`
  (56/12/27/31/39/29), engineered-wood suite unregressed. **DELETE/UPDATE = rowid →
  record-batch MERGE** (the DML crux, solved): the provider advertises ALL columns as the rowid, so DuckDB's
  rowid `PlanDelete`/`PlanUpdate` give `ExecuteDelete`/`ExecuteUpdate` the scanned rows; the catalog builds a
  delta-rs MERGE matching them on every column NULL-safe (`WHEN MATCHED THEN DELETE` / `… UPDATE SET`, source
  cols renamed `s__`/`k__` to split SET vs key) — sound because a WHERE can't distinguish identical rows, so
  DuckDB's rowid set is a complete equivalence class. Caveat: a duplicated pre-image row may make delta-rs
  reject an UPDATE as an ambiguous multi-match. **DELETE/UPDATE are always copy-on-write — NO deletion vectors**
  (verified 2026-07: with `delta.enableDeletionVectors=true`, BOTH our MERGE-delete AND delta-rs's predicate
  `DeleteAsync` emit `add`+`remove`/rewritten file, never a `deletionVector`, in deltalake 0.32.1). So there is
  **no `deletion_vectors` ATTACH option** for delta-rs (declaring the feature would only bump the table to
  reader-v3, risking Fabric-converter breakage, for zero DV benefit) — use the `engineeredwooddelta` provider's
  `deletion_vectors true` for real DVs. **Maintenance** (OPTIMIZE / Z-ORDER / VACUUM / CHECKPOINT —
  ops engineered-wood lacks) via a command dialect on `fabricator_exec('<catalog>', 'OPTIMIZE main.t ZORDER
  (id)' | 'VACUUM … [DRY RUN]' | 'CHECKPOINT main.t')` (C#-only `ExecuteNonQuery`, no ABI change;
  `test/verify_delta_rs_maintenance.test`, 12). **Filter pushdown** (FilterNode→DataFusion WHERE via QueryAsync,
  superset-safe/unpushable→TRUE; `_pushdown` 27), **time travel** (`AT (VERSION => n)` via QueryAsync — DataFusion
  reads the loaded snapshot, sidestepping the kernel-only `ReadAsArrowTableAsync`; composes with pushdown;
  `_time_travel` 36; VERSION 0 reads latest — delta-dotnet `Version=0` sentinel), and **Change Data Feed**
  (`change_data_feed` ATTACH option + `fabricator_delta_changes`; `_cdf` 31) all work. **ALTER ADD COLUMN**
  works via a 0-row merge-append (Append+OverwriteSchema=false → `SchemaMode::Merge` unions the widened schema,
  old rows NULL — pure delta-dotnet, all backends, no engineered-wood IO seam) + **RENAME TABLE** (local folder
  move); `_alter` 47. **Deferred (clean error): RENAME/DROP COLUMN + ALTER TYPE** (need column mapping), cloud
  RENAME. **Not yet wired**: a first-class MERGE surface + S3/plain-ADLS discovery
  (OneLake IS wired — see next). **OneLake via the
  Unity Catalog REST API**: OneLake exposes a Unity-Catalog-compatible REST API
  (`onelake.table.fabric.microsoft.com/delta/<wsGuid>/<lhGuid>/api/2.1/unity-catalog/schemas` + `/tables`,
  **paginated** via `next_page_token`, storage.azure.com-scope token) that lists schemas + tables + each
  table's `storage_location` — cleaner than the DFS `GetPaths` (no recursion, immune to the duckdb-azure
  mid-path-glob bug). **The engineered-wood provider now discovers OneLake tables via this API**
  (`FabricLakehouse.ListTablesViaUnityCatalog`, replacing GetPaths in `Resolve`; live-validated on
  `LH_no_schema`). **delta-rs OneLake read is PROVEN**: `object_store` reads OneLake with a **GUID-based** abfss
  path (`abfss://<wsGuid>@onelake.dfs.fabric.microsoft.com/<lhGuid>/Tables/[schema/]table` + `bearer_token` +
  `account_name=onelake` + `use_fabric_endpoint=true`) — both the kernel AND DataFusion reads succeed
  (`load_a`→2000 rows); the NAME-based path fails with delta-kernel's "No files in log segment" (that error,
  also from DuckDB's official `delta_scan` on OneLake, was purely name→GUID resolution, NOT a kernel limit).
  The UC `storage_location` returns GUIDs — exactly the read form. **The delta-rs OneLake path is now WIRED +
  live-validated**: `ATTACH 'abfss://<ws>@onelake…/<lh>.Lakehouse/Tables' (PROVIDER 'deltars', SECRET
  <azure_sp>)` → `DeltaRsCatalog` detects the OneLake root, resolves GUIDs + schema-enabled + the UC table list
  via `FabricLakehouse.ResolveOneLakeTables` (made `public`; `Resolve` → `internal`), builds the GUID-abfss
  `TableUri`, and augments `storage_options` with `azure_storage_account_name=onelake` +
  `_use_fabric_endpoint=true` over the SP client-creds (auto-refresh, no static bearer). Validated on
  `LH_no_schema`: 4 tables discovered under `main`, `load_a`→2000 rows, filter pushdown works. S3/plain-ADLS
  discovery still needs a lister. See docs/delta-rs-provider.md. v47 =
  **host-FS global table functions**: appended one vtable entry `set_active_opener(opener)` — a per-thread ambient (`AmbientOpener`, mirroring `set_active_txn`) recording the
  calling operator's `ClientContext` so a connection-free GLOBAL host-FS table reader (a lakehouse format)
  resolves DuckDB secrets while reading through the host `fs_*` callbacks; set in the shared `PopulateReturnSchema`
  + `ArrowStreamInitGlobal` arrow-scan hooks, read by the host-FS binding in `Bind`/`Execute`. **REMOVED**
  `delta_schema`/`delta_scan` (a mid-struct removal → offsets of later entries shift; `abi.h` ↔ `Abi.cs` kept in
  lockstep, function/delta suites the gate): `fabricator_delta_scan` is now a pure-C# global `ITableFunction`
  (`DeltaGlobalTableFunction`) on the v29 table session — a new lakehouse format costs zero C++. See the
  load-time-global-functions bullet + [docs/global-functions.md](docs/global-functions.md) §"Host-FS global table
  functions". v46 = **load-time global functions**: appended one vtable entry
  `list_global_functions` (enumerate the provider-union of connection-free global functions at extension load);
  the scalar entries `get_function_param_schema`/`get_function_return_schema`/`execute_scalar` gained a
  **`handle==0`** branch that resolves a function by name against the C# global registry instead of a catalog
  (all five global kinds — scalar/in-out/collector/table/aggregate — reuse it).
  v42–v45 = the **host-query** feature, prior session. v41 (its `delta_schema`/`delta_scan`, removed at v47) was
  the **Delta lakehouse reader on the filesystem bridge** SPIKE: appended
  `delta_schema`/`delta_scan` to the vtable + `fs_glob` to `FabricatorHostServices`. `fabricator_delta_scan(path)`
  reads a Delta Lake table via **engineered-wood** (Curt Hagenlocher's pure-C# Delta), with ALL IO through
  DuckDB's `FileSystem` over the host callbacks — so local/`az://`/`s3://`/`https://` + DuckDB secrets all work.
  C# `DuckDbTableFileSystem : ITableFileSystem` (root-relative paths, read-only) + `DuckDbRandomAccessFile` over
  the callbacks; `DeltaReader` = `delta_schema` (bind, `OpenAsync().ArrowSchema`, no data) + `delta_scan`
  (execute, `ReadAllAsync()` **materialized** into an `InMemoryArrayStream` while the opener is valid); C++
  `fabricator_delta.cpp` binds via `DeltaSchema`+`ReadArrowSchema`, scans via `DeltaScan`+`ArrowStreamReader`.
  DuckDB applies projection/filter/aggregation above the scan. engineered-wood is referenced from
  `Fabricator.Bridge` (**in-tree git submodule `engineered-wood/` at the repo root**, pinned to the
  `cmettler/engineered-wood` fork, **Apache.Arrow 23.0.0 aligned**, net10.0) + published transitively. **One local engineered-wood patch:** `ActionSerializer` read optional `add`/`remove`
  numeric fields (`baseRowId`/`defaultRowCommitVersion`/remove `size`/`deletionTimestamp` — the **Delta
  row-tracking** fields) with a bare `GetInt64()` that throws on delta-rs's explicit `"field":null`; guarded with
  `TokenType==Null?null:GetInt64()`. Validated: `test/verify_delta.test` (39 — fixture `test/fixtures/delta_simple`,
  a delta-rs 10-row table; full scan + filter/aggregate + DESCRIBE + join). SPIKE, not a user feature. See
  [docs/filesystem-bridge.md](docs/filesystem-bridge.md). A faster future path — engineered-wood as a Delta
  **snapshot/file-list provider** feeding DuckDB's C++ **`MultiFileReader`** + native parquet reader (the
  architecture of DuckDB's own `delta` ext, swapping delta-kernel-rs for the C# log layer), with a cheaper
  `host_query`+`read_parquet` middle-ground first — is captured as a design note (deferred, nothing built):
  [docs/multifile-delta.md](docs/multifile-delta.md). **Phase-A pre-spike BUILT (2026-07-03):**
  `fabricator_delta_native_scan(path)` — engineered-wood lists the EXACT active files + schema
  (`DeltaReader.GetActiveFileUris`, the `add` set not a glob), DuckDB's NATIVE parquet reader reads them via
  `read_parquet([...])` on the host engine (`Host.Query`/host_query); OneLake file URIs rewritten to `onelake://`
  → native + ExternalFileCache-cached. Matches the C# reader row-for-row (`test/verify_delta_native_scan.test`,
  36; `parquet` now statically linked in the test binaries). C#-only (no ABI — reuses onelake:// v56 + host_query).
  Plain tables only (no DV/partition/pushdown; OneLake log read needs the ambient credential = the ATTACH-catalog
  path, a later slice). Validates the "engineered-wood lists → DuckDB reads natively" architecture before the full
  MultiFileList C++ work. **Design fleshed out + source-grounded 2026-07-02**
  (against `D:\repos\duckdb-delta` + DuckDB 1.5.4's `MultiFileReader` API): the **inversion** = C# becomes a
  pure **metadata provider** (`ILakehouseSnapshot`: file list + DV + partition values + schema + baseRowId),
  DuckDB's native parquet reader/writer does ALL parquet I/O → engineered-wood's weakest part (its parquet
  reader/writer, source of the decimal/DV-format/DataPage-V2/signed-min-max issues) FALLS AWAY; its strongest
  part (the `_delta_log` layer) stays. **The wiring trick** (from duckdb-delta): don't build `MultiFileFunction<>`
  — **clone `parquet_scan` + inject `function.get_multi_file_reader`**, inheriting all encodings + multi-file
  parallelism + dynamic (join/TopN) filter pushdown for free; custom = `FabricatorMultiFileList : SimpleMultiFileList`
  (file list) + `FabricatorMultiFileReader : MultiFileReader` (~8 overrides), ~400–1700 LOC provider-agnostic C++
  in `fabricator-core`, reuses `ParquetMultiFileInfo` verbatim. **DVs are ~free** (native `BaseFileReader::deletion_filter`
  hook — attach a `DeleteFilter` from the C#-supplied DV). **Row tracking / commit version / CDF are all LOG-side
  (C#), orthogonal to who writes parquet, and EASIER here**: `baseRowId` assigned on commit from the exact per-file
  `WRITTEN_FILE_STATISTICS` row_count + the `delta.rowTracking` domainMetadata high-water mark (the one gap to
  add in engineered-wood); the readable stable id = `baseRowId + file_row_number` (native parquet virtual column)
  → **this is exactly the read-side stable id delta-rs couldn't expose**; `defaultRowCommitVersion` = the commit
  version (per-file constant); CDF INSERT is inferred (free), DELETE/UPDATE need one extra tagged `_change_data/`
  write (engineered-wood already has the CDF log machinery). **DELETE with `deletion_vectors true` is
  rewrite-free** (pure log+DV write = engineered-wood's existing Fabric-validated capability, NO native parquet
  writer — so DV-DELETE rides on Phase A/read alone); copy-on-write DELETE + UPDATE use the native writer;
  merge-on-read DV-UPDATE (DV old + small postimage file) is an optional later refinement. Biggest cost =
  coupling to DuckDB's churning
  MultiFileReader internals (mitigated: provider-agnostic, in `fabricator-core`); no benefit for SQL Server / DAX
  (Delta-only). Phased: A read-only (host_query+read_parquet pre-spike optional) → B native write → C DML+CDF.
  A separate deferred note,
  [docs/delta-catalog.md](docs/delta-catalog.md), covers a **Delta WRITE-BACK** path: expose a Delta **folder
  as an ATTACH catalog root** (`ATTACH '/lake/root' (TYPE fabricator, PROVIDER 'delta')`; each `_delta_log` subdir
  = a table, discovered via `fs_glob`) as the 3rd `IBackend` reusing the provider-agnostic C++ catalog/scan/DML
  wholesale. engineered-wood is full read-write (INSERT/DELETE/UPDATE via `WriteAsync`/`DeleteAsync`/`UpdateAsync`
  + deletion vectors + commit writing; NO merge). Clean first slice = read + INSERT + CREATE (no rowid); the one
  real decision is DELETE/UPDATE = rowid→deletion-vector (fits our rowid DML, needs an engineered-wood
  position-delete) vs predicate (`FilterNode`→engineered-wood `Predicate` via its Arrow row evaluator — clean,
  gap-free in that direction); UPDATE-SET evaluation + Delta's per-commit (non-cross-table-ACID) semantics are
  the caveats. The note also records **scan data-skipping**: engineered-wood file-pruning (Delta `add` stats +
  partition values) works once we pass the predicate; row-group/bloom skipping exists in its parquet reader but
  isn't plumbed through the Delta filter path (small engineered-wood fix); and **dynamic (join/TopN) filters**
  can be applied by reading the live `TableFilterSet` (`TableFunctionInitInput.filters`) at execute time +
  merging into the predicate — the Delta scan's per-file C# loop even lets it re-check before each file. v40 =
  filesystem reverse-callback SPIKE/foundation: a new `FabricatorHostServices`
  struct (host→managed function pointers: `fs_open_read`/`fs_size`/`fs_read`/`fs_close`/`free_str`) is passed
  to `Bootstrap.Initialize(vtable, size, host)` so the managed side can call back into DuckDB's `FileSystem`
  (secret-backed remote IO via DuckDB), plus an `fs_spike` vtable entry + `fabricator_fs_spike(path)` table fn
  that proves it. Key finding: `FileSystem::GetFileSystem(context)` is an `OpenerFileSystem` that AUTO-pushes
  the context's `FileOpener` (secrets) — open with NO explicit opener. Validated: local parquet (PAR1 footer)
  + remote https via httpfs (range GET). Foundation for a future C# lakehouse-format provider (engineered-wood
  Delta/Iceberg/Lance/… reusing DuckDB IO + secrets). See [docs/filesystem-bridge.md](docs/filesystem-bridge.md).
  v39 = foreign-secret consumption: `build_connection_string` gained
  `secret_type` + `base_connstr` args so a provider can reuse a secret of ANOTHER extension's type. C++
  `BuildConnectionStringFromSecret` resolves ANY secret (`IsFabricatorSecret`→`IsKnownSecret`) and passes the
  type + fields + the ATTACH target to C#; `SqlServerBackend` maps an `azure` service_principal/managed_identity
  secret to Entra auth merged onto the ATTACH target (`ATTACH 'Server=…;Database=…' (TYPE fabricator, SECRET
  <azure_sp>)`), and rejects `credential_chain` (storage-scoped/lazy token) pointing to
  authentication='Active Directory Default'. Validated end-to-end against a live Fabric Warehouse (manual);
  error paths in `verify_azure_secret.test` (`require azure`). See [docs/provider-extensibility.md](docs/provider-extensibility.md) §2.1.
  v38 = the secret-field declaration refactor: a `list_secret_fields` entry was
  appended; the provider declares its secret type + fields in C# (`IBackend.SecretType`/`SecretFields`), and
  `RegisterProviderSecrets` registers one DuckDB secret type per declared type generically (fields = the
  `CREATE SECRET` named params; one shared `CreateProviderSecret` keyed by `input.type`). The C++ core names
  no secret type/field — the `kHost…` constants, `ValidateFields`, and `CreateFabricatorSecret` are gone;
  validation moved to C# `BuildConnectionString` (surfaces at connect time). `IsFabricatorSecret`→
  `IsProviderSecret`. See [docs/provider-extensibility.md](docs/provider-extensibility.md) §2.
  v37 = the ATTACH-options→C# refactor: `open_catalog` gained an `options_json`
  arg carrying the provider-owned ATTACH options as a flat JSON object, and `inout_exchange_open` dropped its
  `isolation` arg. `FabricatorAttach` now extracts only PROVIDER/SECRET (meta) and forwards every other option
  as JSON; C# `SqlServerCatalog` parses `schema_filter`/`table_filter` (applied in `get_metadata`, with the
  regex validated C#-side) + `isolation_level` (resolved with `mssql_isolation_level` in `InOutBind`). The
  C++ `CatalogFilters`/`ValidateCatalogFilters`/`ResolveInOutIsolation` + the catalog's filter/isolation
  members are gone; function discovery is schema-filtered by only registering functions whose schema is
  already registered. See [docs/provider-extensibility.md](docs/provider-extensibility.md) §3.
  v36 = the `fabricator_exec` join-only refinement: `set_active_txn` gained an
  `int32 join_only` arg — a raw exec joins the active transaction's pinned connection iff one already exists
  (atomic with an in-flight DuckDB-managed write, e.g. a dbt post-hook adding an index to the model), else
  autocommits without pinning; fixes the in-transaction-hook self-block, see [docs/dbt-hooks.md](docs/dbt-hooks.md) §3.
  v35 = the write-concurrency fix: `begin_bulk`'s `autocommit` int32 arg became
  `int64 txn_id`, and one new entry `set_active_txn(handle, txn_id, …)` was appended — the host sets the active
  DuckDB `global_transaction_id` so the managed side keys a per-transaction provider connection; see the
  "Per-DuckDB-transaction connections" bullet under Transactions + [docs/transaction-concurrency.md](docs/transaction-concurrency.md).
  v33/v34 = the settings refactor: v33 added `list_settings` + `set_setting`; v34 dropped `create_table`'s
  `text_type` param. v32 changed `get_function_param_schema`/`get_function_return_schema`/
  `get_function_output_schema` to fill a bare `ArrowSchema *out` instead of an `ArrowArrayStream *out` — they
  are schema-only, so the zero-row-stream-carrying-a-schema is gone; C# returns `Schema`, C++ reads it via
  the new `ReadArrowSchema` (sharing a `ReadSchemaColumns` core with `PopulateReturnSchema`). A signature
  change, not a slot change → no offset shift. v31 **removed** the dead 4g push entries `inout_open`/`inout_push`/
  `inout_finish`/`inout_abort` — every `_each` form runs on the streaming exchange since `9056eae`; the
  C++ push operator + `IInOutSession`/`InOutOpen` went with them. **Mid-struct** removal (they sat before the
  agg/exchange/table entries), so `abi.h` + `Abi.cs` field order stays in exact sync — the function/agg/in-out
  suites are the alignment gate. v30 **removed** `execute_table`/`execute_proc` — superseded by the v29
  table-function session; they had been unused in C++ since v29, so this is the cleanup. NOTE: that too was a
  **mid-struct** removal (between `get_function_output_schema` and the inout entries), so it shifted every later
  entry's offset — `abi.h` + `Abi.cs` field order must stay in exact sync. v29 appended the three
  **table-function session** entries `tablefn_bind`/`tablefn_execute`/`tablefn_close` — the session-handle
  successor to `execute_table`/`execute_proc`; see the "Table-function session — DONE" bullet under
  "Function-abstraction refactor (Phase 5)".
  v28 appended the three streaming table-in-out **exchange** entries `inout_bind`/
  `inout_exchange_open`/`inout_bind_close` — Phase 6, see "Streaming table-in-out exchange (Phase 6)" below.
  v27 added a nullable `args` 1-row stream to `get_function_output_schema` so a
  custom table function's output schema can depend on its constant arguments — the **table `Bind`** capability;
  see "Callable table functions (4c)". v26 appended the three spillable-aggregate entries `agg_update_spill`/
  `agg_combine_spill`/`agg_finalize_spill` + the `FABRICATOR_AGG_SPILL_CAP` constant — 4h opt-in spill; v25
  appended the six custom-aggregate entries `agg_open`/`agg_update`/`agg_combine`/`agg_finalize`/`agg_destroy`/
  `agg_close` — 4h; v24 added `inout_open`'s `isolation` arg so the in-out session runs its per-chunk CROSS
  APPLY in one transaction at that SQL isolation level for a consistent view). **Bump rule:** when you add a
  vtable entry OR change a signature, bump
  **BOTH** `FABRICATOR_ABI_VERSION` in `abi.h` AND `vtable->AbiVersion = N` in `Bootstrap.Initialize`,
  else the host throws "ABI version mismatch". Adding an *enum value* (e.g. a new metadata/alter kind)
  is additive and needs NO bump.
