# DuckDB 2.0 port (`v2.0-cyanoptera`)

> Records moved verbatim from CLAUDE.md on 2026-11-16. The port is DONE and shipped (v0.2.x on DuckDB
> 2.0.0). What still matters day to day — the 2.0 build commands, the version-string rule, the submodule
> switch — stays in CLAUDE.md §Build & test. This file is the record of how the port went and of the
> upstream behaviour changes it found; read it before the next DuckDB bump (2.0.1, 2.1). Links inside are
> written relative to the repo root, as they were in CLAUDE.md.

## The handoff and the destination entry, verbatim

- **⚠⚠⚠ HANDOFF — THE 2.0 PORT IS ALL BUT DONE. READ THIS FIRST, THEN THE ENTRY BELOW (2026-10-21).**
  - **WHERE IT STANDS: THE PORT IS DONE. Branch `v2.0-cyanoptera`, pinned on the RELEASED `v2.0.0` tag.
    Hermetic **82 of 82 / 10082** (the 1.5.6 baseline is 82/82 — 10040) and service **60 of 60 / 3586,
    zero skipped** — the service tier having never run on 2.0 at all before 2026-10-22, where it started
    at 49/60 and went 51 → 54 → 60. ⚠ THE BRANCH IS STILL LOCAL: not pushed, and the CI branch lists now
    name it but no run has ever fired. Earlier in the port it read
    **81 of 82 / 9910 assertions** (from 435 compile errors, via
    11 -> 30 -> 62 -> 65 -> 66 -> 72 -> 73 -> 74 -> 80 -> 81). The 1.5.6 baseline is 82/82 — 10040.
    THE ONE RED SUITE IS `verify_delta_catalog_variant`, which is the VARIANT TRANSPORT REMOVAL — the
    user's own gated step, NOT a port item (its own top-level entry below: 38 call sites in 8 files).**
    The branch is LOCAL ONLY — not pushed, and no CI runs on it (every workflow's `on.push` names
    `v1.5-variegata` alone).
  - **HOW TO BUILD AND MEASURE — the 2.0 tree is SEPARATE and the commands are not the documented ones:**
    ```
    # build (PowerShell tool, never Git Bash — the cmd /c trap):
    cmd /c "D:\repos\fabricator-extension\scripts\build_v2_all.bat"   # vcvars + cmake --build build/release-v2 -- -k 0
    pwsh scripts/publish-managed.ps1 -ExtensionDir "D:\repos\fabricator-extension\build\release-v2\extension\fabricator"
    # one suite:
    FABRICATOR_MANAGED_DIR=build/release-v2/extension/fabricator/fabricator \
      FABRICATOR_DELTA_WRITE_DIR=$(mktemp -d) DELTA_PROVIDER=delta \
      ./build/release-v2/test/unittest.exe --test-dir . "test/<suite>.test"
    # the tier (Git Bash, backgrounded, NEVER nohup):
    UNITTEST=build/release-v2/test/unittest.exe \
      MANAGED=build/release-v2/extension/fabricator/fabricator bash scripts/run-suites.sh hermetic > /tmp/h.log 2>&1
    ```
    ⚠ `run-suites.sh` MUST run under **Git Bash**: a PowerShell invocation reaches WSL bash, which ignores
    the env vars and silently runs the **1.5.6** build (the tell is `/mnt/d/...` in its header).
    ⚠ `FABRICATOR_DUMP_FILTERS=1` prints every pushed filter, the rendered SQL and the UNPRUNED expression.
  - **THE EXPRESSION_FILTER MIGRATION IS DONE** (`a503b8d`) — `LiveExprNode`/`LiveExprSubject` in
    `arrow_ingest.cpp` convert an `ExpressionFilter`'s tree into the pruning channel's FilterNode JSON, so
    Delta file pruning and parquet row-group pruning happen again. Gate:
    `verify_delta_late_materialization` at 57, whose assertion 129 is the `rowid prune files 3 -> 1` LOG
    LINE — the thing that was missing, not the answer, which was correct throughout.
  - **⚠⚠⚠ THE MOST DANGEROUS THING FOUND IN THE WHOLE PORT, AND IT WAS A WRONG ANSWER WITH NO ERROR —
    FIXED in `c557649` by ONE WORD: `arrow_scan` -> `arrow_scan_dumb` in `RegisterArrowInputView`.**
    2.0's `ArrowToDuckDB` indexes the incoming arrow array BY POSITION (`arrow_scan_is_projected` defaults
    to TRUE), i.e. `arrow_scan` assumes the factory honoured `parameters.projected_columns` and returned a
    NARROWED stream. Ours returns the caller's stream whole — as does DuckDB's own `CArrowScanFactory`
    behind the public `duckdb_arrow_scan` — so ANY non-identity projection reads the WRONG CHILD.
    MEASURED: `__delta_row_commit_version` answered **24** (a `baseRowId`) where the log says 8.
    Full record + the controls: docs/duckdb-upstream-issues.md §10.
    - ⚠ **The cost is that a named input now crosses WHOLE** (no column pruning, no filter pushdown into
      the view). Ours are small by construction; the real fix, if the pushdown is ever wanted back, is a
      projecting wrapper around the caller's stream.
    - ⚠ **The C# side was exonerated BEFORE C++ was blamed** — two probes showed the metadata built and
      read back correctly. On a bug of this shape that ordering is the whole method.
    - **⚠⚠ WHY WE INHERITED IT, which is the part that keeps getting re-asked: `RegisterArrowInputView`
      IS `duckdb_arrow_scan` WITH ONE ARGUMENT CHANGED** (its own comment says so — the factory callbacks
      are copied verbatim from `capi/v1/arrow-c.cpp`, where they sit in an anonymous namespace and cannot
      be linked from an extension; only `temporary = true` differs, for the recorded dangling-view crash).
      Copying it meant copying its contract violation.
    - **⚠⚠ THERE ARE TWO ARROW SCAN PATHS AND ONLY ONE WAS EVER BROKEN:**

      | path | function | projection | used by |
      |---|---|---|---|
      | OURS | `fabricator_scan` | narrowed AT THE PRODUCER, mapped BY NAME (`ArrowStreamInitLocal` clears `column_ids`, ingests all, then projects via `output_source_pos`) | the catalog scan, fluid's `input_table`, `publish()` |
      | COPIED | `RegisterArrowInputView` | identity, no pushdown (since `arrow_scan_dumb`) | `Host.Query(sql, inputs)` named inputs — Delta metadata + deletion-vector views, bulk-write inputs |

      `arrow_scan_dumb` is not a retreat: `ArrowScanBindDumb` does `column_ids.clear()`, which is EXACTLY
      what our own scan has always done. The two paths now share one invariant.
    - **⚠⚠ THE FILTER HALF WAS LATENT AND IS NOW CLOSED TOO.** `arrow_scan` declares
      `filter_pushdown = true`, the optimizer ERASES a fully pushed filter (`pushdown_get.cpp`:
      `filters.erase_at(i)`), and DuckDB's arrow scan NEVER APPLIES ONE — `ArrowScanLocalState::filters`
      is stored and never read. A filter pushed into such a view would simply vanish: too many rows.
      MEASURED across all 76 hermetic suites with `FABRICATOR_DUMP_ARROW_SCAN=1`: **1162 named-input scans
      in 58 suites, `filters=none` without exception**, so it was never reached; `arrow_scan_dumb` removes
      it by construction. ⚠ **THE FIRST SWEEP OF THIS MEASURED NOTHING AND LOOKED CLEAN** —
      `run-suites.sh` captures a suite's output into a variable and prints it only on FAILURE (line 1580),
      so a probe's lines are DISCARDED and the count came back zero. Drive the suites directly when
      sweeping for instrumentation output; the 1162 lines are the positive control.
    - ⚠ The probe SHIPS (`FABRICATOR_DUMP_ARROW_SCAN=1`, one getenv per scan) for the reason the other
      env-gated probes in this tree do: what DuckDB asks a named input for is invisible from SQL, and the
      failure mode was a wrong column with no error. Under `arrow_scan_dumb` the printed projection MUST
      be the full column list in schema order.
  - **FIVE MORE UPSTREAM BEHAVIOUR CHANGES, each measured with a stock-DuckDB control** (details in
    docs/duckdb-upstream-issues.md §8/§9 and in the suites' own ⚠ comments):
    - a BINDER ERROR inside an explicit transaction now ABORTS it (`BEGIN; SELECT nope FROM t; INSERT …;
      COMMIT;` leaves 1 row on 2.0, 2 on 1.5.6). ⚠ Scanned for a class: 18 candidate sites, **ONE** real —
      the other 17 roll back immediately. Do that refinement before touching six suites.
    - `ROW_GROUP_SIZE` below ~4 x STANDARD_VECTOR_SIZE is SWALLOWED when the source supplies batch indexes;
      the DEFAULT (122880) is unaffected.
    - `ROW_GROUP_SIZE_BYTES` no longer needs `preserve_insertion_order=false` — the recorded trap is gone.
    - the PARAMETER MODEL is Python's now: a positional argument may not follow a named one, and a WRONG
      name no longer binds positionally (`upper(zzz := 'a')` is 'A' on 1.5.6 and a binder error on 2.0).
      That RETIRED a documented trap in `verify_plugin_fluid` rather than breaking it.
    - `UPDATE … SET col = DEFAULT`: 2.0's `Binder::BindUpdateSet` PUSHES the bound default into the
      projection, so our refusal lost its reason and is gone (`118ea74`). The gate now asserts the three
      VALUES, which is strictly stronger — a shift lands a wrong number in a neighbouring column.
  - **⚠ THE METHOD THAT WORKED, worth keeping rather than re-deriving: chase the CLASS, not the suite.**
    Almost every tier round was cut by ONE upstream decision; the tier — not the build — is this port's
    oracle. And twice a suite's expectation was PINNING AN ARTEFACT rather than a promise (a partitioned
    write's file order, a tail position), which is worth checking before "fixing" a number.
  - ⚠ Before touching anything: `git submodule update --init` (switching branches does NOT move the pins)
    and check the compiler path in `build/release-v2/CMakeCache.txt`.
    ⚠⚠ **DO NOT run the recorded `grep -E 'AUTOLOAD|AUTOINSTALL'` check on this tree — it would always
    fail and send you chasing a phantom.** DuckDB 2.0 REMOVED both options: its CMakeLists lists them under
    `foreach(REMOVED_OPTION …)` and warns *"has no effect: autoload_known_extensions and
    autoinstall_known_extensions default to true, change them at runtime instead"*. Passing them to a
    configure produces the "manually-specified variables were not used by the project" warning, which
    reads like success in a tail-filtered log — measured 2026-10-22. The 1.5 tree still needs the check.
  - **WHAT IS LEFT: THE PUSH, and that is the user's call.** (1) the VARIANT transport removal is DONE
    (`06a1011`) — 2.0 ships canonical `arrow.parquet.variant`, so `ew.variant_transport` and the C++
    `fabricator_variant.cpp` registration are GONE; (2) the SERVICE tier is DONE — 60/60 — 3586, and the
    prediction recorded here was RIGHT for the wrong reason: the `statement error`-inside-a-transaction
    suites were indeed fine, while the three that were NOT are ones this list never named (stored-proc
    named arguments, the s3 read-only bump, the ns cast); (3) the CI branch lists are DONE — `docs.yml`,
    `extension.yml` and `installer-core.yml` all name `v2.0-cyanoptera`, and `distribution.yml` is pinned
    to `DUCKDB_VERSION: v2.0.0` / `DUCKDB_WHEEL: 2.0.0` now that the wheel exists; (4) the push.
    - ⚠ ONE DELIBERATE GAP, and it is not a port item: a CODEC-path variant WRITE loses shredding (the
      canonical struct is written unshredded). Recorded with a native control row beside it rather than
      papered over; the native path, which is the `PROVIDER 'delta'` default, is unaffected.

- **⚠⚠ DuckDB 2.0 (`v2.0-cyanoptera`) IS THE DESTINATION — USER-DIRECTED 2026-10-20. The 1.5.6 bump is DONE
  (see Build & test); what follows is RECONNAISSANCE, established from the branch's own source, plus the
  branching decision that is still the user's.**
  - **⚠⚠ THE BIGGEST FINDING CORRECTS THE PREMISE IT WAS LOOKING FOR. The user asked about "optimizer
    extensions for remote execution, so a DELETE can be pushed through without going via rowid". That is NOT
    an optimizer extension, and it is NOT new in 2.0 — `optimizer_extension.hpp` is BYTE-IDENTICAL between
    1.5.6 and 2.0, and its `pre_optimize_function` already exists in 1.5.5.** The mechanism is a CATALOG
    overload that has been there all along:
    ```cpp
    PlanDelete(ctx, planner, LogicalDelete &op, PhysicalOperator &plan);  // we override THIS
    PlanDelete(ctx, planner, LogicalDelete &op);                          // plan-LESS, unused by us
    ```
    `PhysicalPlanGenerator::CreatePlan(LogicalDelete&)` calls the **plan-less** one; DuckDB's default body
    is `auto &plan = planner.CreatePlan(*op.children[0]); return PlanDelete(…, plan);`. Because we override
    only the 4-arg form, DuckDB builds the child scan for us — **which is where the rowid requirement comes
    from**. Overriding the 3-arg form means never materializing that scan: read `op.children[0]` as a
    `LogicalGet` with its pushed filters and emit `DELETE FROM t WHERE …` directly. `PlanUpdate` is the same
    shape (both verified present in 1.5.5, 1.5.6 AND 2.0).
    - ⚠ **Two limits shape any design**: `op.return_chunk` (`DELETE … RETURNING`) still needs real rows, and
      a filter we cannot translate still needs the scan. ⇒ it must FALL BACK to today's path, never be
      all-or-nothing. ⚠ Nothing gates the choice — overriding the 3-arg form IS the opt-in.
    - ⇒ **buildable TODAY on 1.5.x; it is not a reason to wait for 2.0.**
  - **WHAT 2.0 COSTS US, read from the branch rather than from release prose:** `DUCKDB_CPP_EXTENSION_ENTRY`
    and `ExtensionLoader` SURVIVE, so the direct C++ bridge stays both necessary and available (the user's
    own assessment, confirmed). The loader's apparent removals do NOT reach us — `GetDatabaseInstance()`
    (our 10 call sites) merely becomes `const`, and the `GetFunction`/`TryGetFunction` overloads that move to
    the new `Identifier` type are ones we never call.
    - **⚠ THE REAL PORT COST IS THE `Identifier` TYPE** (`src/include/duckdb/common/identifier.hpp`):
      construction from a RUNTIME `string` is **`explicit`** (only string LITERALS convert implicitly, via the
      `const char *` ctor) and the way back is explicit too (`GetIdentifierName()`). So every catalog call
      handing in a computed name needs a wrap. Wide, but purely mechanical and COMPILER-GUIDED — it fails
      loudly, never silently, which is the good kind.
    - ⚠ `catalog.hpp` is the broad surface (+198/−97 vs 1.5.6). New and directly useful to us:
      `ExtensionLoader::UseDedicatedSchemaForExtension()` — the blessed version of our hand-built `fabric`
      schema.
    - **⚠⚠ MEASURED 2026-10-20 BY ACTUALLY BUILDING IT, and it REFINES the estimate above: the port is NOT
      `Identifier`-dominated — it is an ENCAPSULATION SWEEP.** Branch `v2.0-cyanoptera` created, pins set,
      configured and built in a SEPARATE tree `build/release-v2`. **DuckDB 2.0 itself configured and compiled
      clean; only OUR code fails** — so there is no build-system or httpfs problem, just API adaptation.
      `-k 0` (keep-going) gives the full surface: **19 files, 435 errors, 16 failed translation units** (of
      25 .cpp in `src/`). ⚠ The first run reported "6 files" — that is ninja stopping after the first
      failure, i.e. a LOWER BOUND, not a result. Always `-k 0` when measuring a port.
      - **The dominant change: public FIELDS became private/protected with ACCESSORS.** Verified as accessors
        rather than removals, which is what makes it mechanical: `Expression::return_type` →
        `GetReturnType()`/`SetReturnType()`, `SimpleFunction::arguments` → `GetArguments()` (const and
        non-const). Top absent members, by count: `TableFunction::named_parameters` (8),
        `CreateSchemaInfo::schema` (6), `TableFunction::arguments` (6), `ScalarFunction::arguments` (5),
        `varargs`/`stability` on both (4 each), `BoundComparisonExpression::left`/`right` (3 each),
        `ConstantExpression::value` (2). 56 further errors are access violations on the same pattern
        (`BoundColumnRefExpression::binding`, `BoundOperatorExpression::children`, `BaseExpression::type`, …).
      - ⚠ **`BoundComparisonExpression::left`/`right` and `ConstantExpression::value` are the FILTER-PUSHDOWN
        model**, so that surface is touched throughout even though each individual edit is a rename.
      - ⚠ Second theme, unrelated to `Identifier`: scoped enums (28 × C2838 *"illegal qualified name"* on
        `ExpressionType::BOUND_COMPARISON`, `CONJUNCTION_AND`, …) and a CONST-CORRECTNESS wave on vector data
        pointers and `ValidityMask` (C3892/C2662).
      - ⚠ **ONE GENUINELY OPEN QUESTION, do not assume it is a rename: `named_parameters` has NO accessor on
        `TableFunction`** (it survives on `TableFunctionBindInput`). Where a function DECLARES its named
        parameters has moved somewhere else and must be found before costing the rest. It matters beyond the
        count — this file records the `unordered_map` ORDERING lesson about exactly that member.
      - ⚠ `ClientContext::interrupted` is gone — that is our cancellation path, see docs/cancellation.md.
      - **⚠⚠ `Connection::SendQuery` IS SPLIT IN TWO AND YOU CANNOT HAVE BOTH HALVES — the second change in
        this port that compiles clean and would regress silently.** 1.5's `SendQuery` ran SEVERAL statements
        AND could stream. 2.0 offers **`Query`** (several statements — verified, `ClientContext::Query` loops
        them and returns the last one's result — but MATERIALISES, its own doc says "runs the query to
        completion… the result is retained") and **`Submit`** (non-blocking, STREAMS, "may only contain a
        SINGLE statement"). ⇒ a mechanical `SendQuery` -> `Query` keeps every call compiling and quietly
        drops streaming.
        - Our two call sites needed OPPOSITE halves, and each site's own comment is what decided it:
          `RunHostExec` exists for multi-statement ⇒ **`Query`** (cost: a big SELECT passed to host_exec now
          materialises; only the last statement's result is read, for the row count). `fabricator_host_query`
          streams, and the file header says streaming is LOAD-BEARING there — the arrow scan holds a raw
          pointer into the caller's struct and bounded memory for large results is the point ⇒ **`Submit`**
          (cost: several statements now fail LOUDLY where 1.5 ran them; loud beats materialising silently).
        - ⚠ This invalidates the recorded `SET SESSION …;` PREFIX TRICK wherever it rode on SendQuery's
          multi-statement behaviour on a STREAMING path — the two properties no longer coexist.
      - **⚠ HEADERS ARE SPLIT AND THE OLD ONE KEEPS ONLY A FORWARD DECLARATION**, which reports as
        *"use of undefined type"*, not *"unknown type"*. Seen three times: `StructVector` ->
        `common/vector/struct_vector.hpp`, `TableFilterSet` -> `planner/table_filter_set.hpp`,
        `DynamicFilterData` -> `planner/filter/table_filter_functions.hpp`, plus the `DUCKDB_LOG_*` macros in
        `logging/logger.hpp`. ⚠ Grep for the DEFINITION (`struct X {`), not the name — searching the name
        lands on a forward declaration in an unrelated header, as it did here twice.
      - **BETWEEN WENT THE SAME WAY AS COMPARISONS**: `BoundBetweenExpression` is now a struct of static
        helpers over `BoundFunctionExpression` (`Input`/`LowerBound`/`UpperBound`/`LowerInclusive`/
        `UpperInclusive`/`HasValidBindData`), and `ExpressionClass::BOUND_BETWEEN` is gone. Both now arrive
        as `BOUND_FUNCTION`; the discriminators are disjoint — `IsComparison` covers exactly the eight binary
        comparison types (read from `comparison.cpp`, which excludes BETWEEN) and a range is
        `ExpressionType::COMPARE_BETWEEN`. ⚠ We deliberately do NOT handle `COMPARE_NOT_BETWEEN`: refusing is
        always superset-safe because DuckDB re-applies every predicate.
    - **⚠⚠ THE PORT IS UNDERWAY (2026-10-20, user-directed "beginne mit dem port"). 435 -> 362 errors so
      far. THE METHOD: find the DESIGN change behind an error class, apply it mechanically, re-measure with
      `-k 0`. Do not fix errors one at a time — three of the four biggest classes turned out to be ONE
      upstream decision each.**
      - **⚠⚠ THE 42 `TableFunction` CONSTRUCTOR ERRORS WERE NOT ABOUT THE CONSTRUCTOR.** The bind callback
        changed `vector<string> &names` -> **`vector<Identifier> &names`**, so no overload matched and MSVC
        blamed argument 1. Fixed on 14 signatures / 6 files. ⇒ **when every overload "fails to convert", the
        argument it names is rarely the one that moved.**
      - **⚠⚠ EVERY TABLE FILTER IS NOW `Legacy*`** — `LegacyConstantFilter`, `LegacyConjunctionAndFilter`,
        `LegacyStructFilter`, … and `TableFilterType::LEGACY_CONSTANT_COMPARISON` etc. A NEW
        `EXPRESSION_FILTER = 9` ("an arbitrary expression") sits beside them. The rename alone cleared
        **64 errors in `arrow_ingest.cpp` (104 -> 40)**.
        - **⚠⚠ AND THE RISK IT CREATES IS NOT A COMPILER ERROR: if 2.0's optimizer starts handing us
          `EXPRESSION_FILTER` instead of the legacy shapes, our legacy handling simply sees NOTHING.**
          Results stay correct — DuckDB re-applies every predicate — so the symptom is pushdown silently
          not happening, i.e. a PERFORMANCE regression with no message. **MEASURE this once it builds**
          (a pushed WHERE must still reach the generated T-SQL / the Delta pruner); do not assume.
        - **⇒ MIGRATING TO `EXPRESSION_FILTER` IS A DECIDED FOLLOW-UP (user, 2026-10-20: *"umstellung auf
          EXPRESSION_FILTER sollte folgen, da duckdb diesen weg weiter gehen wird"*) — a SEPARATE step
          after the port compiles, not part of it.** Rationale beyond "upstream is going there": we
          maintain TWO filter paths today — `pushdown_complex_filter` over expressions in
          `fabricator_table_entry.cpp`, and the structured `TableFilter` walk in `arrow_ingest.cpp` —
          and `EXPRESSION_FILTER` collapses them into one. ⚠ Do it only once the legacy path is green,
          so the two can be compared against each other instead of debugged together.
      - **⚠⚠ A COMPARISON IS A FUNCTION EXPRESSION NOW.** The old class is `LegacyBoundComparisonExpression`;
        the NAME `BoundComparisonExpression` was REUSED for a struct of static helpers over
        `BoundFunctionExpression` — `IsComparison(expr)`, `Left()/Right()`, `LeftMutable()/RightMutable()`,
        `Create()`, `FlipType()`. ⚠ Its own doc warns that a comparison's type lives in BOTH the expression
        type and the bind data, so `FlipType`/`SetType` must be used instead of `SetExpressionTypeUnsafe`.
        **That trap does NOT reach us** — checked rather than assumed: our `Flip` is PURE, returning a local
        `ExpressionType` used to render SQL, and we never mutate the tree. Our port there is access-only.
      - **NAMED PARAMETERS ARE A NEW MODEL, not a moved field:** `fn.GetSignature()` with `AddPositionalOnly`
        / `AddKeywordOnly` / `AddArgs` (varargs) / `AddKwargs` / `WithTypedKwargs(name, lambda)` — a
        Python-shaped parameter model that maps closely onto our own `ParamStyle`. ⚠ Parameters now live in a
        **`vector`**, so the recorded platform-dependent `unordered_map` ORDERING trap in
        `duckdb_functions().parameters` is probably GONE — verify, and simplify the sorted assertion in
        `verify_plugin_fluid` if so.
      - **THE ACCESSOR TABLE (fields -> accessors). ⚠ The naming is NOT uniformly `Get*`:**
        `Expression::return_type` -> `GetReturnType()`/`SetReturnType()`; `BaseExpression::type` ->
        `GetExpressionType()`; `::alias` -> `GetAlias()`/`SetAlias()`; `BoundOperatorExpression::children` ->
        `GetChildren()`/`GetChildrenMutable()`; `BoundColumnRefExpression::binding` -> **`Binding()`** /
        `BindingMutable()`; `BoundReferenceExpression::index` -> **`Index()`**/`IndexMutable()`;
        `ConstantExpression::value` -> `GetLiteral()`; `SimpleFunction::arguments` -> `GetArguments()`;
        `stability` -> `GetStability()`/`SetStability()`; `ClientContext::interrupted` -> `IsInterrupted()`;
        `CreateViewInfo::view_name` -> `GetViewName()`.
      - **OTHER MOVES:** `StructVector` and friends moved out of `common/types/vector.hpp` into
        **`common/vector/*.hpp`**; `CatalogEntry::name` is an `Identifier` (so `.GetIdentifierName()` where a
        `string` is wanted); `SchemaCatalogEntry` and `BinderException` now need their own includes in
        places that used to get them transitively.
      - ⚠ The core callbacks are UNCHANGED (`table_function_t`, `_bind_t`, `_init_global_t`, `_init_local_t`),
        so no function BODY has to be rewritten — this really is an adaptation, not a redesign of ours.
      - **⚠⚠ THE ERROR COUNT IS NOT A PROGRESS METRIC WHILE A FILE IS CAPPED — MSVC STOPS AT 100 PER
        TRANSLATION UNIT (`fatal error C1003: error count exceeds 100`), and I nearly reported "no progress"
        because of it.** One round fixed 16 real sites in `fabricator_schema_entry.cpp` and the total moved
        329 -> 329, per-file counts IDENTICAL: the cap simply surfaced 16 more. ⇒ **read the C1003 line
        first**: `grep -oE '[A-Za-z0-9_]+\.cpp\([0-9]+\): fatal error C1003' <log>`. A capped file's number
        is a FLOOR, never a measurement, and the baseline "435" was itself an undercount because BOTH
        `arrow_ingest` and `schema_entry` were capped — so the filter rename did not take arrow_ingest from
        104 to 40, it took it from >100 to 40. Track **uncapped total + which files are still capped**.
      - **⚠⚠ READING A FUNCTION'S DECLARED ARGUMENT TYPES IS A REWRITE, NOT A RENAME — and this is what
        `schema_entry` is now blocked on.** `GetArguments()` belongs to **`BoundSimpleFunction`** (the BOUND
        form); a DEFINITION (`ScalarFunction`, now CRTP: `class ScalarFunction : public
        BaseScalarFunction<ScalarFunction>`) exposes its parameters through **`GetSignature()`** —
        `GetParameterCount()`, `GetParameter(i).GetType()/.GetName()/.GetKind()`, plus
        `BoundSimpleFunction::GetStandardArgumentCount(sig)` / `GetVarArgsCount(sig)` /
        `GetArgumentParameterKind(sig, i)`. ⇒ **substituting `.arguments` -> `.GetArguments()` is WRONG on a
        definition** (it compiled nowhere and produced 14 fresh errors). Our per-slot marshalling reads
        exactly these types, so that loop has to be rewritten against the signature.
        - ⚠ `varargs` is the same story: no field, no getter on the definition — declared via the ctor or
          `AddArgs(name, type)`, read via `GetSignature().GetArgs()` (an `optional_ptr<const
          FunctionParameter>`) or `IsVariadic()`.
        - **⚠⚠⚠ THE MOST DANGEROUS THING FOUND IN THE PORT SO FAR, AND IT COMPILES CLEAN: PARAMETER ORDER
          IS NOW LOAD-BEARING.** 1.5 kept `arguments`, `varargs` and `named_parameters` in THREE SEPARATE
          containers, so the order in which a declaration loop filled them was irrelevant. 2.0 keeps ALL
          parameters in ONE ORDERED `vector` on the signature. Anything that reads a declared slot takes
          the **leading NON-VARIADIC** parameters as the fixed prefix, so the emit order must be
          **positionals → variadic tail → keyword-only**. Our two declaration loops
          (`FabricatorBuildInOutSignature` and the sqlgen/global builder) set the tail WHERE IT APPEARED in
          the declaration, i.e. possibly mid-list, and a NAMED parameter emitted before the tail would be
          counted INTO the prefix. Both now COLLECT and then emit in that order, with the reason written
          beside them because nothing in the code enforces it. ⇒ **the failure mode is arguments silently
          shifting by one position — no compiler error, no crash, a wrong answer.**
        - ⚠ **`GetPositionalParameterCount()` IS THE WRONG ORACLE for the fixed prefix** — read its inline
          body rather than its name: it counts leading parameters that `AcceptsPosition()`, which INCLUDES
          the variadic one. `FabricatorDeclaredArgType` walks to the first `VAR_POSITIONAL` instead.
        - **STILL OPEN in `fabricator_schema_entry.cpp` (CAPPED, so counts are floors):** `push_back` into
          `vector<Identifier>` (9), `CatalogEntry::schema`/`catalog` (9/8), `named_parameters[...]` ->
          `AddKeywordOnly` (10 sites in source), the `TableFunction` ctor (5), our own
          `FetchFunctionParamSchema` signature (4), `ClientContext::GetParserOptions` now private (4),
          `BaseAggregateFunction::function_info` (4).
      - **⚠ MORE 2.0 SHAPES, each costing a lookup the first time:** `StructVector::GetEntries` returns
        **`vector<Vector>&`** (was `vector<unique_ptr<Vector>>&`) — dot, not arrow, and no deref;
        `LogicalGet::function` is a **`BoundTableFunction`** whose name comes from the inherited
        `GetName()`; `CopyInfo::options` is an **`identifier_map_t`**, so our own option helpers had to
        change their parameter type AND key their lookups `Identifier(key)`; `Catalog::GetEntry`/`GetSchema`/
        `GetCatalog` and `DatabaseManager::GetDatabase` all take Identifiers now.
      - **⚠ THE ACCESSOR NAMING RULE, and where it stops:** a field that got encapsulated is read by its
        own NOUN (`Binding()`, `Index()`, `Child()`, `Function()`, `BindInfo()`) and written through
        `…Mutable()`. ⚠ But WHERE the method lives still has to be looked up — the validity mask is
        `FlatVector::ValidityMutable(v)`, NOT `v.GetValidityMask()`; I guessed that one wrong.
      - **⚠⚠ A SELF-INFLICTED BUG WORTH THE WARNING: a substring replace of `info.schema` also hit OUR OWN
        `info.schema_name` and `info.schema_entry`**, producing
        `info.GetQualifiedName().Schema().GetIdentifierName()_name`. It surfaced as *"'GetQualifiedName' is
        not a member of…"*, i.e. it READ LIKE ANOTHER UPSTREAM CHANGE rather than like my own damage. ⇒ on a
        port this size, every field-name substitution gets `` boundaries, and an error naming a method you
        just introduced is your own until proven otherwise.
      - **⚠⚠ THE BIND CALLBACKS TAKE AN INPUT STRUCT NOW, and my earlier "the core callbacks are unchanged"
        was too broad — it holds for the EXECUTION callbacks only.** `bind_scalar_function_t` is
        `(BindScalarFunctionInput &)` (was context + function + arguments); `table_function_get_bind_info_t`
        is `(TableFunctionGetBindInfoInput &)`; serialize/deserialize take a **`BoundTableFunction`**. Pull
        the old parameters out as locals at the top and the body needs no change. ⚠ The struct already
        carries more than we use (`GetArgumentNames()`, `GetConstant()`), which is the point of the shape.
      - **⚠ TWO MORE SELF-INFLICTED ONES FROM THE SAME EARLY MASS-SUBSTITUTION, found rounds apart:**
        `.stability` -> `.GetStability()` was applied on the LEFT of assignments too, so four WRITES became
        reads (`SetStability` is the writer); and the `info.schema` substring also hit our own
        `info.schema_name`/`info.schema_entry`. ⇒ **a mapping table says what a member is CALLED now — not
        whether the site reads or writes it**, and field substitutions need `` boundaries. Both classes
        fail to compile, so the cost was build cycles, not correctness.
      - **MORE 2.0 SHAPES worth knowing:** an `Identifier` is NOT character-iterable (a `for (char c : id)`
        reports *"no callable 'begin'"*); `TableCatalogEntry::columns`/`constraints` ->
        `GetColumns()`/`GetConstraints()`; `TableFilterSet` is iterable itself and its entry exposes
        `GetIndex()`/`Filter()` instead of `first`/`second`; `CreateTableInfo` takes a `QualifiedName`;
        `Parser::GetBuiltinParser()` is the context-free parser for code with no ClientContext in scope;
        `BaseQueryResult::types`/`names` -> `GetTypes()`/`GetNames()`;
        `PreparedStatement::named_param_map` -> `GetNamedParameterMap()`.
      - **⚠⚠ IT COMPILES AND LINKS — 2026-10-21, 435 -> 0 over ~62 measured rounds.** `unittest`, `shell`
        and `fabricator_loadable_extension` all build in `build/release-v2`. The last handful of errors were
        each their own design change rather than a class, which is the shape the tail of a port takes:
        - **`BindInfo` LOST ITS `TableCatalogEntry` MEMBER** — only `BindInfo(ScanType)` remains, so a
          catalog scan reports `ScanType::TABLE` and the entry pointer is gone for everyone (nothing in the
          engine reads it back out; the only in-tree use of the enum is serialization).
        - **`TableCatalogEntry::GetColumns()` IS PURE VIRTUAL AND THE BASE'S `columns` MEMBER IS GONE** —
          every table entry owns its own `ColumnList` now (`DuckTableEntry` does exactly this). Ours moves
          it out of the `CreateTableInfo` in the ctor; it is immutable for the entry's life, which already
          held because an ALTER REBUILDS the entry rather than editing it.
        - **`cast_duck_arrow_t` TAKES A `const Vector &` SOURCE** (the arrow→duck direction does NOT), which
          is the one VARIANT-transport change — and it is a signature change only, so the byte form of
          `ew.variant_transport` is untouched. ⚠ That surface is the one this file flags as able to record a
          wrong type DURABLY in a Delta `metaData` commit; verify the round trip before trusting it.
        - **`ColumnBinding`'s second argument is a `ProjectionIndex` with an `explicit` ctor** (first is
          `TableIndex`), so a raw `idx_t` no longer converts.
        - **`PreparedStatement::Execute` LOST `allow_stream_result`** — same split as `SendQuery`:
          `Execute` materialises, **`Submit`** streams. Our prepared path in `fabricator_host_query` takes
          `Submit`, matching the unprepared one and the file header's load-bearing streaming claim.
          ⚠ The error for this is a `value.hpp` static assert ("No specialization exists for this type")
          inside `Value::CreateValue`, because the extra `bool` sends the call to the VARIADIC
          `Execute(ARGS...)` overload, which treats the parameter MAP as a positional value. Read the
          `note: see reference to function template instantiation` chain to get back to the call site —
          the error itself names a foreign header and nothing of ours.
        - ⚠ `LogicalType::BLOB` and friends are now `LogicalTypeId` constants, not `LogicalType` — harmless,
          since `LogicalType(LogicalTypeId)` is still implicit, but it changes what an error message says
          the argument type is, which sent me looking at the wrong parameter first.
        - ⚠ `Vector::ToUnifiedFormat(count, data)` is DEPRECATED (the vector carries its own size now); the
          count-taking overload still compiles and was deliberately left alone — a sweep of it is its own
          pass, not part of the port.
      - **⚠⚠ THEN THE SUITES FOUND THE THINGS A COMPILER CANNOT — all three were predicted as
        compile-silent in this very section, and all three were REAL. Running the suites is what turned the
        prediction into a measurement; a green build had already "finished" the port twice by then.**
        - **⚠⚠⚠ 2.0's OPTIMIZER ALREADY HANDS US `EXPRESSION_FILTER` (type 9), NOT the legacy shapes — the
          risk recorded above is not a future one.** And the expression it builds EMBEDS DuckDB's INTERNAL
          table-filter functions, whose bind THROWS *"Table filter functions are for internal use only!"*.
          MEASURED on a join, the rendered predicate was
          `(("id" >= 2) AND (__internal_tablefilter_optional("id") AND __internal_tablefilter_optional("id")))`,
          which binds NOWHERE — so every Delta/native scan under a join FAILED.
          - **The five are optional / bloom / dynamic / prefix-range / selectivity, all "not required for
            query correctness"** (DuckDB re-applies every predicate above the scan), so PRUNING them is
            superset-safe — the same rule the legacy branches already applied by TYPE.
            `TableFilterFunctions::IsTableFilterFunction(fn)` is the oracle; do not match on the name.
          - ⚠ Pruning happens ONLY under a CONJUNCTION_AND. Dropping a branch of an OR NARROWS the result
            (loses rows), and an argument of a comparison has no meaning without it — both return "cannot
            render", which is always safe.
          - **⚠⚠ FOUR PASSES OF READING DID NOT FIND IT; ONE ENV-GATED PRINT DID, in one run.** The probe
            SHIPS (`FABRICATOR_DUMP_FILTERS`, beside the `Fabricator.Memory` marks and the Arrow liveness
            registry) for the reason it earned: what a pushed filter renders to is INVISIBLE from SQL — a
            predicate that silently fails to render is a performance regression with no message. It is also
            the instrument the EXPRESSION_FILTER migration will need.
        - **⚠⚠ A KEYWORD-ONLY PARAMETER WITHOUT A DEFAULT IS *REQUIRED* IN 2.0.** 1.5's `named_parameters`
          were always optional, so every optional named parameter in the extension silently became
          mandatory. MEASURED: `fluid_replacement_query('SELECT 1 AS a')` reported *"No function matches …
          (STRING_LITERAL)"* while the identical call WITH `params := {...}` bound — and an explicit
          `::VARCHAR` cast did NOT help, which is what separates this from a literal-type question.
          ⇒ every `AddKeywordOnly` gets a `Value()` (NULL) default. **That is exactly equivalent to absence
          here**, because our marshalling already substitutes NULL for an omitted named argument;
          `FillNamedDefaults` just does the substitution one layer earlier. ⚠ It is NOT equivalent for a
          parameter whose bind distinguishes "absent" from "NULL" — check the read site before adding a
          default elsewhere.
        - **⚠ A CTAS NOW REPORTS THE ROWS IT CREATED through `fabricator_host_exec` (was 0), because 2.0
          CLASSIFIES a CTAS as a row-count statement.** We still ASK the statement
          (`StatementReturnType::CHANGED_ROWS`) rather than inferring from column types; what moved is
          upstream. Measured: CTAS → the row count, plain `CREATE TABLE b(x INT)` → 0, `DROP` → 0.
          ⇒ **this CLOSES a documented divergence** — `IHostQuery.ExecuteNonQuery` INFERS its count and
          already reported the rows a CTAS created, so the SQL and C# surfaces disagreed on exactly this
          case on 1.5 and now agree. `verify_host_query` and the `ExecuteNonQuery` doc were corrected
          together.
        - **⚠ `Submit` ACCEPTS ONE STATEMENT, so the multi-statement fallback had to be routed to `Query`.**
          The port first let it fail loudly; `verify_host_query` pins that shape as a DOCUMENTED FALLBACK
          (double execution included), and turning a working call into an error is the one outcome the
          fallback exists to avoid. ⇒ `ExtractStatements(sql).size() > 1` picks `Query` (materialises),
          everything else `Submit` (streams). The cost is bounded: a multi-statement call could never be
          described, so nothing streams it by design.
      - **⚠⚠ AND THE SUITES KEPT FINDING MORE — the tier is the real oracle for this port, and each round
        of it has produced a DIFFERENT upstream decision rather than a long tail of one.** 71 failures ->
        52 -> (next round), each cut by ONE shared cause:
        - **⚠⚠ `arrow_scan` TAKES AN `ArrowScanFactory` AS BIND INFO — the three POINTER arguments are
          gone and the function declares NO arguments at all** (`Connection::TableFunction(name, {}, {},
          factory)`, the shape `duckdb_arrow_scan`'s own C API now uses). It was the single cause of ~70 of
          the 71 failures, because EVERY Delta write registers a named Arrow input. ⚠ The non-owning
          contract is unchanged: the factory's `GetSchema`/`ProduceStream` are verbatim the two free
          callbacks they replaced, neutralising the stream's and the schema's release callbacks.
          ⚠⚠ **The message was SWALLOWED by a bare `catch (...)`** — the caller said only *"failed to
          register input view '<name>'"* and named no cause, which cost a debugging round; the reason is
          carried out now.
        - **⚠⚠ A SCALAR'S ARGUMENTS WERE POSITIONAL-ONLY, SO `f(x, params := …)` STOPPED BINDING.** The
          types-only `ScalarFunction` ctor names them `col0`, `col1`, … and marks them POSITIONAL_ONLY,
          which 2.0 ENFORCES — the candidate line even prints the `/` marker
          (`fluid_render(col0 VARCHAR, col1 ANY, /)`). **`STANDARD` is 2.0's positional-OR-keyword kind**
          (Python's), so declaring the parameters BY NAME restores 1.5's behaviour and gives a real name in
          `duckdb_functions().parameters`. ⚠ An argument the provider did not name stays positional-only:
          a name that was never declared must not become callable.
        - **⚠⚠ `LogicalGet::GetTable()` ASKS THE FUNCTION NOW** — `function.get_table_entry(bind_data)`,
          a new `TableFunction` callback, where 1.5 derived the entry from the bind data. Without it every
          catalog scan looks like a non-table to the binder and **UPDATE/DELETE are refused with
          *"Can only delete from base table"*** on a perfectly ordinary scan. The entry was already on our
          bind data; the callback is three lines.
        - ⚠ `SetColumnCommentInfo` does NOT override `AlterInfo::GetColumnName()`, whose base returns an
          EMPTY Identifier — so the inherited accessor silently yields nothing and `COMMENT ON COLUMN`
          fails inside the PROVIDER (*"missing its 'column'"*), which reads like our bug and is an upstream
          move. Read the `column_name` member.
      - **⚠⚠ THE TIER IS THE ORACLE FOR THIS PORT, AND IT TOOK SEVEN ROUNDS: 11 -> 30 -> 62 -> 65 -> 66 ->
        72 -> **73 of 82 suites / 6061 assertions**. Almost every round was cut by ONE shared cause, so
        chase the class, not the suite. Five more upstream decisions, each compile-silent:**
        - **⚠⚠⚠ `PhysicalMergeInto` GAINED A TRAILING `serialize_actions`, AND DuckDB'S OWN RULE FOR IT IS
          WRONG FOR US — this is the one that LOST DATA.** Upstream derives it from `append_count > 1`,
          counting only INSERT and del-and-insert UPDATE, i.e. "two operators APPEND to the same table".
          For us EVERY writing action — INSERT, UPDATE, DELETE alike — goes to ONE remote table through ONE
          provider session and fuses into ONE commit, so any two running as concurrent action pipelines can
          lose each other's work. ⇒ ALL writers count, `writing_actions >= 2`.
          - **The two shapes failed DIFFERENTLY and that is what forced the wider rule:** two MATCHED
            DELETEs lost a row OUTRIGHT (3 rows where 2 were expected, deterministic); UPDATE + INSERT on a
            change-data-feed table lost the `update_preimage` from the feed in **half of runs** — a RACE,
            which a rule keyed on rowid actions alone (1 here) left flaky rather than fixed. 2 of 4 runs
            red before, 6 of 6 green at the full 239 after.
          - ⚠ Both were CONTROLLED against a native DuckDB table, where the same statements are correct —
            which is what shows the loss is ours and not upstream's.
        - **⚠⚠ A BARE `NULL` COLUMN NO LONGER SURVIVES THE ARROW BOUNDARY.** 1.5 typed `SELECT NULL AS v`
          as INTEGER at BIND (`DESCRIBE` said `v integer`); 2.0 keeps `"null"`, so it exports as a true
          Arrow null array — and DuckDB's `ArrowNullData` leaves `null_count` at **0** where the Arrow spec
          says a null array's null_count IS its length. Apache.Arrow refuses it: *"Length must equal null
          count (Parameter 'data')"*, i.e. a host query with an untyped NULL column KILLS the managed
          caller. ⚠ Not exotic — our decision-table macros render a blank output cell as `NULL`.
          ⇒ `PromoteNullColumns` restores 1.5's observable type at our boundary (values are constant NULL
          by construction, so nothing is lost). **The export is an upstream defect and belongs in
          docs/duckdb-upstream-issues.md.**
        - **⚠⚠ `TIMESTAMP_NS` → `TIMESTAMP` ROUNDS ON 2.0 WHERE IT TRUNCATED ON 1.5, AND THE CARRY
          REACHES THE SECONDS FIELD.** Found by `verify_granular_types`, whose SQL Server `datetime2(7)`
          workaround rests on that cast. MEASURED with the SAME LITERAL on both builds, so it is upstream
          and nothing of ours moved — `CAST(TIMESTAMP_NS '…' AS TIMESTAMP)`: `.1234564` → `.123456` on
          both (rounds down, indistinguishable from truncation, which is why a one-value probe proves
          nothing); `.1234565` → `.123456` on 1.5.6 and **`.123457`** on 2.0; `.1234575` → `.123457` vs
          `.123458`, i.e. half-AWAY-FROM-ZERO, not banker's; and **`12:34:56.9999999` → `.999999` on
          1.5.6 but `12:34:57` on 2.0.**
          - ⇒ **RECORDED, NOT WORKED AROUND** — rounding is the defensible behaviour for a lossy
            narrowing cast, so this is NOT an upstream-issues entry. What changed for a CALLER is that the
            surviving six digits are no longer a PREFIX of the original, and a value a microsecond short
            of the next second now comes back AS the next second.
          - ⚠ The Delta WRITE REFUSAL above it is untouched (nanoseconds still have no faithful Delta
            encoding); only what the documented workaround produces has moved.
          - ⚠ **MY FIRST CONTROL WAS THE WRONG ONE AND NEARLY SENT ME INTO OUR OWN CODE**: a cast from a
            STRING literal truncates on BOTH builds, so it "proved" DuckDB had not changed and that the
            rounding had to be ours. The discriminating control casts from `TIMESTAMP_NS`, which is the
            type the value actually has. **Control the TYPE the path really carries, not a literal that
            merely looks like the value.**
        - **⚠⚠ THE FILTER WRAPPERS HIDE A REAL PREDICATE — RECOVER IT BEFORE PRUNING.** An
          optional/selectivity-optional wrapper carries the child in its bind data and a dynamic one the
          live join bound; on 1.5 those arrived as their own TableFilter TYPES and were rendered, on 2.0
          they arrive INSIDE an EXPRESSION_FILTER, so pruning alone silently gives up the pushdown.
          ⚠ The recovered child is bound to **`#0`**, not to the scan's column, so it must be REBOUND or
          the generated SQL does not parse (measured as a Parser error from the host query).
          ⚠ A PREFIX-RANGE child is probabilistic (`PrefixRangeFilter` exposes only `LookupRange`) and
          still drops — correctly.
        - **⚠⚠ AND I NEARLY SHIPPED A WEAKENED GATE OVER THAT ONE.** From a minimal repro that rendered
          nothing I concluded the join bound was "no longer expressible on 2.0" and rewrote the assertion
          from `true` to `false`, complete with a confident explanation. **The repro hit a different join
          shape than the suite.** After the rebinding, `verify_delta_catalog_dynamic_filter` is green at 21
          with its ORIGINAL assertions. ⇒ **a repro that disagrees with a suite is a repro to doubt first**,
          and an explanation written before the fix is not evidence.
        - **⚠ TWO MORE RENDERINGS MOVED, and the NEGATIVE assertions are the reason it matters:** EXPLAIN
          title-cases operators (`SEQ_SCAN` -> `Seq Scan`, `FABRICATOR_LATERAL_BATCHED` -> `Fabricator
          Lateral Batched`) and spaces filters (`g=3` -> `g = 3`); a plan's JSON names a function as
          `"qname":{"path":[...]}` rather than `"name"`; an untyped literal reports as `STRING_LITERAL`;
          the pushed predicate text is `("id" > 5)` rather than `"id">5`. Left unported, `verify_lateral`'s
          two `<!REGEX>` rows would have passed **VACUOUSLY** — the kill switch and the uncorrelated shape
          silently unchecked. ⚠ And a comment placed AFTER a `----` separator counts as an expected ROW;
          three suites failed on my own explanation before I moved them above the `query` line.
      - **⚠⚠⚠ ✅ DONE (`a503b8d`) — THE `EXPRESSION_FILTER` MIGRATION WAS NOT A FOLLOW-UP, 2.0 FORCED IT,
        AND THE PLAN
        RECORDED ABOVE ("do it only once the legacy path is green, so the two can be compared") IS
        UNRUNNABLE: the legacy path CANNOT be green on 2.0 and there is nothing left to compare it to.**
        `SerializeLiveFilters`/`LiveFilterNode` end in `default: return false` with the comment
        *"EXPRESSION_FILTER / BLOOM_FILTER: not convertible"* — true on 1.5, where that type never arrived.
        On 2.0 it ALWAYS arrives, so **the STRUCTURED PRUNING channel is silently DEAD**: Delta file
        pruning and parquet row-group/bloom pruning no longer happen at all.
        - **MEASURED, and the two channels disagree, which is what makes it diagnosable:** the SQL channel
          renders `("_metadata.row_id" = 1099511627781)` correctly while the log line
          `rowid prune files 3 -> 1` is ABSENT (`verify_delta_late_materialization`). Results stay correct
          — DuckDB re-applies every predicate — so the only symptom is work not being skipped.
        - ⇒ BUILT as `LiveExprSubject` + `LiveExprNode` in `arrow_ingest.cpp`, beside the SQL renderer and
          walking the same shapes one level lower: comparisons via `BoundComparisonExpression::IsComparison`
          (a comparison is a FUNCTION expression on 2.0), AND/OR via `BoundConjunctionExpression`,
          IS [NOT] NULL and IN via `BoundOperatorExpression`, struct members via `struct_extract(_at)`, and
          the wrappers via the same `RecoverFilterFunction`. The prune line is back
          (`verify_delta_late_materialization` 57, assertion 129).
        - ⚠ TWO guards carry the safety and neither is cosmetic: an unconvertible node is DROPPED (which
          only widens) but a dropped OR branch refuses the whole disjunction, and an `ExpressionFilter`
          with non-empty `column_indexes` is REFUSED rather than pruned on the wrong column — a
          single-column filter never has them, so a non-empty one means the subject is not this entry's.
      - **REMAINING AT 81/82 (2026-10-21, end of day):** `verify_delta_catalog_variant` ALONE — the variant
        transport, its own entry, not a port item. Every other suite above is green; what each of them
        turned out to be is in the HANDOFF block at the top of §Next up.
      - **⚠⚠ AND THEN A GREEN BUILD STILL RAN NOTHING: DuckDB 2.0 REMOVED `DONT_LINK` AND INVERTED THE
        DEFAULT — static linking into `duckdb.exe`/`unittest.exe` is now OPT-IN per extension, via
        `duckdb_extension_statically_link(<name>)` in the extension config.** `duckdb_extension_load` only
        BUILDS now. Symptom: the loadable is produced, the tools link, and **every suite SKIPS with
        `require fabricator`** — which reads as a missing managed bridge, not as a link-config change. ⚠ The
        decisive probe is `SELECT extension_name, loaded FROM duckdb_extensions()` in the new shell:
        `json`/`icu`/`parquet`/`core_functions` were `loaded=true` (DuckDB's OWN config lists them) while
        `fabricator` was ABSENT from the table entirely and `httpfs` sat at `false`. Both of ours needed the
        opt-in. ⚠ `duckdb_extension_load(x DONT_LINK)` is now a FATAL error naming the replacement, so the
        OTHER direction is loud — only the default flip is silent. Configure then prints
        `-- Linked into DuckDB: extensions [fabricator, httpfs, core_functions, parquet, json, icu]`, which
        is the line to check after any config edit.
      - **⚠ THE CAP LIFTED at round 30** (`fabricator_schema_entry.cpp` fell under 100), which is when the
        totals became honest for the first time. Until then: verify a capped file's progress BY CATEGORY
        (`FetchFunctionParamSchema` 7 -> 1 while the total stood still), never by the total.
    - **⚠ OPERATIONAL, AND IT IS A SILENT-FAILURE RISK ON A TWO-LINE SETUP: switching our branch does NOT
      move the submodules.** Going between `v1.5-variegata` and `v2.0-cyanoptera` needs
      `git submodule update --init`, or you compile one branch's source against the OTHER branch's DuckDB
      with nothing warning you. ⚠ And keep the 2.0 build in its OWN tree — `build/release` is shared with the
      four out-of-tree plugin repos, so a half-ported 2.0 state there breaks their sessions.
    - **⚠ THE 2.0 BRANCH HAS NO CI AT ALL, and that is quiet rather than loud.** Every workflow's `on.push`
      names `branches: [v1.5-variegata]` ONLY, so a push to `v2.0-cyanoptera` fires nothing — no red runs
      while the port is in flight (useful), but also no gate the day it compiles (not useful). Extending the
      branch lists is part of the port, not an afterthought. ⚠ `distribution.yml` additionally pins
      `DUCKDB_VERSION`/`DUCKDB_WHEEL` to 1.5.6 and its smoke test `pip install`s that wheel — there IS no
      released 2.0 wheel, so that tier cannot work on this line until 2.0 ships, whatever the branch list says.
    - Starting point committed as `accfe12` ("pin duckdb 2.0-cyanoptera"): the two submodule pins plus the
      httpfs sha 2.0's own CI uses (`77392a72`). Nothing of the port itself is in it.
  - **THE BRANCHING QUESTION (user: "folgen wir dem duckdb branch muster?") — RECOMMENDED, NOT YET DECIDED.**
    The decision recorded under §BRANCH NAMING said `main` is RESERVED for tracking duckdb `main` and should
    be created "when there's a 1.6 preview worth tracking". **That trigger has now fired.** Recommended shape,
    which is DuckDB's pattern minus a line we would not use:
    - **⚠⚠ CORRECTED 2026-10-20, USER-CAUGHT: the branch is `v2.0-cyanoptera`, NOT `main`.** I first
      recommended pinning `main` at 2.0, arguing that a separate line branch would "duplicate `main` today".
      **That rested on a factual error — DuckDB's `main` is ALREADY 2.1.** MEASURED, from
      `scripts/ci/release_version.txt` on each branch: `v2.0-cyanoptera` → **2.0**, `main` → **2.1** (the file
      does not exist on 1.5.x, which used the older scheme). So the 2.0 line is already branched off and main
      has moved on; there is no duplication to avoid, and naming our 2.0 line `main` would have made the name
      lie on the day it was created — the exact "sharp edge" §BRANCH NAMING already warns about.
    - `v1.5-variegata` stays the SHIPPING line, bugfix-only once 2.0 builds green.
    - **Create `v2.0-cyanoptera`** — same name as the DuckDB branch its submodule pin references, which makes
      the correspondence self-documenting in a way `main` structurally cannot.
    - `main` stays UNCREATED. It would track duckdb `main` (2.1), and the original reasons against it are
      untouched by any of this: continuous upstream API churn for zero consumers.
    - **Direction: features on `v2.0-cyanoptera`, backport only what must ship before 2.0.** The usual advice
      (develop on the OLDEST line, merge forward) is WRONG here: 2.0 is the destination, and anything using
      `Identifier` or `UseDedicatedSchemaForExtension` is not backportable at all.
    - ⚠ **Do NOT try to hold both DuckDB versions in one branch behind `#ifdef`s.** No DuckDB extension does
      this, and our ABI history shows how quietly such a thing diverges.
    - What keeps it cheap: the duckdb pin is a SUBMODULE, so a cross-line merge conflicts only at the gitlink,
      `extension_config.cmake` and `DUCKDB_VERSION` in `distribution.yml` — one small, always-identical
      conflict set that FORCES the version decision instead of hiding it. The tag scheme already carries two
      lines (`v0.0.16-duckdb1.5.6` beside `v0.1.0-duckdb2.0.0`).
    - ⚠ OPEN AND THE USER'S TO ANSWER: **how long 1.5 keeps shipping.** It changes only how much is
      backported, not the structure above.

