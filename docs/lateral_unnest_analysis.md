# Batched/Row By Row Correlated LATERAL for a DuckDB Table-In-Out Function

A design note for implementing a custom `PhysicalOperator` that collapses DuckDB's
row-by-row correlated-LATERAL driver into one call per input chunk.

## How to use this document

This is not a complete specification. 
Design/Implement it from the invariants of the fabricator extension.
The pseudocode is control flow and ordering.

Every DuckDB API named here (`LogicalGet::projected_input`,
`OperatorResultType`, `OperatorState::Finalize`, …) must be verified against the
DuckDB headers vendored in *your* tree before use — these move between versions.
If a signature in the pseudocode doesn't compile, the header is right and this doc
is stale.

The parts worth reading twice are **§5 Invariants** and **§6 Testing**. The class
scaffolding is mechanical; the invariants are what make the difference between an
operator that works and one that silently corrupts rows under fan-out.

---

## 0. Do you need this at all?

Two gates before writing any code here. Most readers stop at one of them.

**Gate 1 — is a table-in-out function even the right shape?** If the callee runs
in-process, the alternatives usually avoid this whole design:

| Callee shape | Idiomatic form | What it gives you |
|---|---|---|
| 1→1 | **scalar function** | The full vector per call, natively. No correlation, no operator, no optimizer extension. Return a STRUCT for multi-field output. |
| 1→N, N bounded | **scalar returning `LIST<STRUCT>` + `UNNEST`** | Same vectorization; `UNNEST` already does the fan-out and correlated-column replication |
| 1→N unbounded, output schema resolved at bind, or must appear in `FROM` | table function / table-in-out | Continue to gate 2 |

A scalar function is batched *by construction* — DuckDB hands you 2048 rows and
there is no correlation to decorrelate, so the correlated columns are just a
projection. None of §5's invariants exist. Check this first.

**Gate 2 — measure the stock path.** Register the function normally, run it over
~1M rows, and compare against the same computation expressed as a scalar
function. Per outer row the stock driver costs a `DataChunk` slice, a virtual
dispatch, **your own per-call setup**, and — the big one — your callee sees a
single row and cannot vectorize. The first two are tens of nanoseconds and
ignorable; the last two are where the win lives.

So batching pays substantially when the callee is internally vectorized (SIMD,
BLAS, a model runtime — `batch=1` wastes the point) or when converting into its
input format has fixed per-call cost, and pays close to nothing when the callee is
a cheap per-row computation with no setup. A 1.5× measured gap does not justify
the several hundred lines below; a 20× gap does.

You lose nothing by deferring. The rewrite happens entirely after binding, so the
stock and batched paths are always binding-identical — which is both why you can
add this later and why the reference-oracle test in §6 works.

## 0.5 Prerequisite: how the function must be registered

Everything below assumes a working table-in-out registration. If you don't have
one yet, this is the part that isn't obvious.

A plain `TableFunction` receives its arguments as **constant `Value`s at bind
time** (`TableFunctionBindInput::inputs`). A column reference is not a constant,
so a plain table function cannot accept `my_map(i.a)` at all. Setting
**`in_out_function`** is the switch: the binder then treats the call as
`TABLE_IN_OUT_FUNCTION` and gathers non-constant argument expressions into a
child relation that streams into your callback.

Two conventions for the input:

- **(a) Explicit TABLE parameter** — declare `LogicalType::TABLE` in the arg
  types (`my_map(TABLE t)`). The relation is named outright. Does not give you
  `my_map(i.a)`.
- **(b) Arguments ARE the input columns** — declare real value types
  (`{DOUBLE, DOUBLE}`), no TABLE marker. DuckDB synthesizes the input relation
  from whichever arguments are expressions. This is what native `UNNEST` does,
  and it is what `is_row_mapped` means throughout this document. One registration
  serves the literal, column, and explicit-LATERAL shapes.

**The asymmetry to get right.** Under (b), positional arguments do **not** arrive
in `input.inputs`. They arrive as the input relation's schema:

| | `input.inputs` | `input.input_table_types` / `_names` |
|---|---|---|
| positional args | *skip these* | ← the per-row input columns |
| named args (`opt := 5`) | `input.named_parameters` | — |

Read your arg types from `input_table_types` and build the callee input schema
from them. The literal call goes through the same route — DuckDB synthesizes a
cardinality-1 input chunk from the constants — and is detected by
`input_table_names` coming back **all empty**. That shape gets a childless plan
driven by `PhysicalTableScan`, which acts as a *source*: it re-invokes the
callback with the same 1-row chunk and decides flow purely on `chunk.size()`,
discarding your returned `OperatorResultType`. So it needs its own write-once →
close-input → drain-to-EOS loop, returning a 0-row chunk only at true EOS or the
query spins forever. It does not reach the operator in this document (no
`projected_input`), but it shares your bind.

Consequences of (b):

- **Bind-time configuration must use named parameters.** Every positional slot is
  runtime data, so there is no positional constant to read at bind.
- **Overloads work**, because the args are real value types — the binder's
  TABLE-parameter overload restrictions don't apply.
- **Resolve overloads by input-column count**, not by argument values, since the
  values aren't available at bind.

**Caveat to verify against your DuckDB version.** Combining inline named args
with column args in the correlated shape — `my_map(t.x, t.y, opt := 5)` — has
historically been broken upstream: the `TABLE_IN_OUT_FUNCTION` binder branch
swept *all* expressions into the input subquery before extracting named
parameters, turning `opt := 5` into a phantom input column. Named args in the
literal shape always worked. Test it; if unfixed in your version, keep named args
out of the correlated call shape.

## 1. The problem

Suppose your extension registers a table function with an `in_out_function`
callback whose arguments can reference an outer table:

```sql
SELECT * FROM inputs i, my_map(i.a, i.b);       -- implicitly correlated
SELECT * FROM inputs i, LATERAL my_map(i.a, i.b);
```

DuckDB's binder plans this as a `LogicalGet` over your table function with a
**child** (the outer relation) and a non-empty `projected_input`. The
decorrelator (`flatten_dependent_join`) is what populates `projected_input`: it
lists the child-chunk column indices that must be carried through to the output
alongside your function's own columns.

At execution, `PhysicalTableInOutFunction` drives that plan **one outer row at a
time**. It slices the child chunk to cardinality 1 and invokes your callback per
row. It has to: it stamps the correlated columns onto your output itself, and it
can only know which outer row a given output row belongs to if exactly one outer
row is in flight.

That is fine when your callback is cheap. It is pathological when the per-call
cost is dominated by **fixed overhead** rather than per-row work:

- a network round trip (HTTP, gRPC, database driver)
- a process or language boundary (subprocess pipe, FFI, embedded interpreter)
- a model invocation, GPU kernel launch, or any batched-by-nature API
- anything with a per-call handshake

10,000 outer rows becomes 10,000 round trips. The fix is to ship the whole input
chunk (up to `STANDARD_VECTOR_SIZE`) in one call — which means taking over the
operator, because the stock driver's row-at-a-time slicing is the very thing you
need to eliminate.

**Expected win.** Purely fixed-overhead amortization: `N` calls become
`ceil(N / 2048)`. If your per-call overhead is `c` and per-row work is `w`, you go
from `N(c + w)` to `N·w + ceil(N/2048)·c`. Measure `c` before building this — if
`c` is small relative to `w`, you get nothing and you've taken on §5's invariants
for free.

## 2. Preconditions on the callee

Batching is only sound if the callee can answer two questions the stock driver
answered structurally.

**(a) Row provenance — mandatory for anything but 1→1.**
When you send N rows and get M rows back, you need to know, for each of the M
output rows, *which of the N input rows produced it*. Without this you cannot
stamp the correlated columns, and you cannot support fan-out (1→N) or filtering
(1→0) at all.

So the callee must return, alongside its output, an array of length M of input-row
indices. Design this as an **additive, optional** part of your protocol: absent
provenance means "identity 1→1 map", which lets existing 1→1 callees work
unchanged. Make the absent case *strict* — if provenance is missing and
`M != N`, that is an error, not a guess.

**(b) No finalize phase.**
DuckDB refuses to combine a finalize callback with `projected_input` — a
`TableFunction` that sets both `in_out_function_final` and receives a non-empty
`projected_input` throws at execution (the error mentions `project_input`). So a
correlated call can never have a finalize stage, and your operator never needs to
model one. Exclude finalize-bearing functions at the eligibility check anyway, as
defense against a mis-declared function reaching you.

**(c) Order independence — check this, don't assume it.**
You will declare `OrderPreservationType::NO_ORDER` (§4.3). That is sound for
LATERAL because, post-decorrelation, the correlated columns are re-associated by
value in the join above your operator — not by row position. Verify that holds in
your plan shape (dump `EXPLAIN` for your target queries) before relying on it. If
your operator sits somewhere that *does* depend on position, you must preserve
order instead, and lose cross-morsel parallelism.


## 3. Architecture

Three pieces, in dependency order:

| Piece | Role |
|---|---|
| `BatchedLateralRewriter` (`OptimizerExtension`) | Swaps the eligible `LogicalGet` for your logical node |
| `LogicalBatchedLateral` (`LogicalExtensionOperator`) | Carries the plan-shape contract; builds the physical op |
| `PhysicalBatchedLateral` (`PhysicalOperator`) | The batched exchange itself |

Plus a boolean setting acting as a kill switch (§6).

Note what is *not* in the list: the bind function. You reuse your existing table
function's bind untouched — the rewrite happens entirely after binding, and it
steals the bind data off the `LogicalGet`. This matters: it means the stock path
and the batched path are always binding-identical, which is what makes the
result-equivalence test in §6 meaningful.

## 4. The three pieces

### 4.1 The optimizer extension

**Hook point.** `OptimizerExtension` exposes two entry points:
`pre_optimize_function` (before the built-in passes) and `optimize_function`
(after them). Registered extensions fire in registration order within each phase.

You need **`optimize_function`**, for two reasons:

1. **Decorrelation must have already run.** `projected_input` — your entire
   eligibility signal — is produced by the decorrelator. In the pre-optimize
   phase it isn't populated yet, and you'd match nothing.
2. **Projection narrowing must have already run.** If your function supports
   projection pushdown, the unused-columns pass has already narrowed the get's
   column list by the time `optimize_function` runs. Rewriting earlier means you
   capture the un-narrowed set and lose the pushdown.

The tradeoff you accept: your node is an opaque extension operator, so no
built-in pass that runs *before* you can see through it, and none that runs after
will push filters into it. For a map operator over an already-materialized child,
that costs nothing.

Verify the phase empirically. Put a log line in your rewriter, run your target
query, and confirm it fires and matches. Don't trust the phase ordering from this
document.

**Eligibility.** Be conservative and explicit. Every clause below is a guard
against a shape you have not designed for:

```
FUNCTION IsEligible(get: LogicalGet) -> bool:
    IF get.bind_data is null: RETURN false
    bd := dynamic_cast<MyMapBindData*>(get.bind_data)
    IF bd is null: RETURN false                 // not our function at all

    RETURN bd.is_row_mapped                     // args ARE the per-row input columns
       AND NOT bd.has_finalize                  // §2(b); defensive
       AND NOT bd.uses_other_custom_operator    // don't fight your own rewriters
       AND NOT get.projected_input.empty()      // ⇐ THE correlated-LATERAL signal
       AND get.children.size() == 1             // exactly one outer relation
```

`projected_input.empty()` is the load-bearing clause: empty means the plain
uncorrelated shape, which must stay on the stock path.

**The walk.** Recurse children first, then test the node. Replace in place through
the `unique_ptr&` so the parent's child slot is updated:

```
PROCEDURE RewriteTree(op: unique_ptr<LogicalOperator>&):
    FOR EACH child IN op->children:
        RewriteTree(child)                      // depth-first, before testing self

    IF op->type != LOGICAL_GET: RETURN
    get := op->Cast<LogicalGet>()
    IF NOT IsEligible(get): RETURN

    // Split the get's output: [ callee columns | correlated passthrough columns ]
    base_idx := get.types.size() - get.projected_input.size()
    callee_types    := get.types[0 .. base_idx)
    callee_bindings := get.GetColumnBindings()[0 .. base_idx)

    // Capture callee-original column indices NOW — after the node is replaced,
    // this information is gone. See Invariant 7.
    callee_col_ids := CaptureColumnIds(get, base_idx)

    node := new LogicalBatchedLateral(
                table_index      = get.table_index,          // MUST be preserved
                projected_input  = move(get.projected_input),
                callee_types     = move(callee_types),
                callee_bindings  = move(callee_bindings),
                bind_data        = move(get.bind_data),       // steal it
                callee_col_ids   = move(callee_col_ids),
                projection_pushdown = bd.projection_pushdown)
    node->children.push_back(move(get.children[0]))          // steal the child
    node->ResolveOperatorTypes()
    op = move(node)                                          // replace in place
```

**Registration and the kill switch:**

```
CLASS BatchedLateralRewriter EXTENDS OptimizerExtension:
    CONSTRUCTOR: optimize_function = Optimize      // NOT pre_optimize_function

    STATIC PROCEDURE Optimize(input, plan: unique_ptr<LogicalOperator>&):
        IF setting("my_batch_lateral") is false:
            RETURN                                 // fall back to the stock path
        RewriteTree(plan)
```

Register it in your extension's load function alongside the boolean option.

### 4.2 The logical operator

Its entire job is to be **indistinguishable from the `LogicalGet` it replaced**,
as far as the surrounding plan is concerned. A DELIM_JOIN sits above it and
resolves column bindings by `(table_index, column_index)`. Get this wrong and you
get binder errors at best, wrong columns at worst.

```
CLASS LogicalBatchedLateral EXTENDS LogicalExtensionOperator:
    table_index         : idx_t          // same index the LogicalGet used
    projected_input     : vector<column_t>
    callee_types        : vector<LogicalType>
    callee_bindings     : vector<ColumnBinding>
    bind_data           : unique_ptr<FunctionData>
    callee_col_ids      : vector<column_t>
    projection_pushdown : bool

    METHOD GetTableIndex() -> { table_index }        // bindings resolve through this

    METHOD GetColumnBindings():
        // Exactly the LogicalGet's shape: callee columns, then the child's
        // bindings for each projected_input entry — in that order.
        out := callee_bindings
        child_b := children[0]->GetColumnBindings()
        FOR EACH i IN projected_input: out.append(child_b[i])
        RETURN out

    METHOD ResolveTypes():
        types := callee_types
        FOR EACH i IN projected_input: types.append(children[0]->types[i])

    METHOD GetName()          -> "MY_BATCHED_LATERAL"     // shows in EXPLAIN
    METHOD GetExtensionName() -> "my_batched_lateral"

    METHOD Serialize(s):
        THROW NotImplementedException(...)   // unless you need cross-process plans

    METHOD CreatePlan(context, planner) -> PhysicalOperator&:
        child_plan  := planner.CreatePlan(*children[0])
        input_width := children[0]->types.size() - projected_input.size()
        base_idx    := callee_types.size()
        estimated_cardinality := EstimateCardinality(context)
        op := planner.Make<PhysicalBatchedLateral>(types, move(bind_data),
                  move(projected_input), input_width, base_idx,
                  move(callee_col_ids), projection_pushdown,
                  estimated_cardinality)
        op.children.push_back(child_plan)
        RETURN op
```

Two derived widths worth naming explicitly, because they're easy to confuse:

- `base_idx` = number of **callee output** columns = where correlated columns
  begin in the *output* chunk.
- `input_width` = number of **callee input** columns = the *leading* columns of
  the *child* chunk. The correlated columns live in the child chunk's trailing
  slots, indexed by `projected_input`.

### 4.3 The physical operator

A pipeline **operator** — `Execute` only, no Sink/Source. Type is `EXTENSION`.

```
CLASS PhysicalBatchedLateral EXTENDS PhysicalOperator:
    // ... fields from CreatePlan ...

    METHOD ParallelOperator() -> true
        // Each pipeline thread gets its own OperatorState, hence its own callee
        // session. No shared mutable state on the operator itself.

    METHOD OperatorOrder() -> OrderPreservationType::NO_ORDER
        // Justified by §2(c). Verify for your plan shape.

    METHOD GetGlobalOperatorState(context) -> GlobalOperatorState
        // Cross-thread aggregates only (atomics for counters / EXPLAIN ANALYZE).
        // Never per-row state.

    METHOD GetOperatorState(context) -> OperatorState
        // Per-thread: callee session, drain buffer, held provenance.

    METHOD ParamsToString() -> { "Function": name, "Projected": count }
        // Captured PRE-execution — see Invariant 8. Static facts only.
```

**Per-thread state:**

```
STRUCT OperatorState:
    session            : unique_ptr<CalleeSession>   // lazily created
    result_buffer       : <your batch cursor type>     // partially-drained output
    origin              : vector<int32>                // per output row: input row idx
    input_size_at_decode: idx_t                        // Invariant 1

    DESTRUCTOR:
        // End the stream cleanly if you can, then return the session to whatever
        // pool you have. There is no finalize handshake owed (§2b), so closing
        // input IS a complete end-of-stream. Dropping the session instead means
        // every query pays a fresh setup — a flat per-query cost independent of
        // row count, which is easy to miss in benchmarks that only vary N.
        // Never pool a session that threw; discard those.
```

**The `Execute` contract.** Three branches, in this order. The order is not
cosmetic — the drain branch must come first, because DuckDB will call you again
with the same input while you have output pending.

```
METHOD Execute(context, input: DataChunk&, output: DataChunk&, gstate, state)
        -> OperatorResultType:

    // ---- (A) Drain in progress: finish the batch already in the buffer -------
    IF HasPendingRows(state.result_buffer):
        IF input.size() != state.input_size_at_decode:
            THROW IOException("input chunk resized mid-drain")   // Invariant 1
        start    := CurrentOffset(state.result_buffer)
        produced := EmitCalleeColumns(state.result_buffer, output, base_idx)
        StampCorrelated(output, input, state.origin, projected_input,
                        base_idx, start, produced)
        output.SetCardinality(produced)
        RETURN HasPendingRows(state.result_buffer) ? HAVE_MORE_OUTPUT
                                                  : NEED_MORE_INPUT

    // ---- (B) Nothing to do --------------------------------------------------
    IF input.size() == 0:
        output.SetCardinality(0)
        RETURN NEED_MORE_INPUT                  // NOT FINISHED — Invariant 5

    // ---- (C) Fresh input chunk: ONE batched call ----------------------------
    IF state.session is null:
        state.session := OpenSession(context, bind_data,
                                     projection = projection_pushdown
                                                  ? callee_col_ids : none)

    callee_input := ViewLeadingColumns(input, input_width)  // zero-copy if possible
    result       := state.session->Call(callee_input)       // <-- the whole point

    IF result is END_OF_STREAM:
        // A map owes one response per request. Early EOS would silently drop the
        // rest of the input. Fail loudly and discard the session.
        state.session.reset()
        THROW IOException("callee ended stream mid-exchange")

    IF result.row_count == 0:
        output.SetCardinality(0)
        RETURN NEED_MORE_INPUT                  // whole chunk filtered out (1→0)

    state.origin = DecodeOrigin(result.provenance, result.row_count,
                                input.size())               // Invariant 3
    state.input_size_at_decode = input.size()
    LoadIntoBuffer(state.result_buffer, result)

    start    := 0
    produced := EmitCalleeColumns(state.result_buffer, output, base_idx)
    StampCorrelated(output, input, state.origin, projected_input,
                    base_idx, start, produced)
    output.SetCardinality(produced)
    RETURN HasPendingRows(state.result_buffer) ? HAVE_MORE_OUTPUT : NEED_MORE_INPUT
```

**Provenance decode — treat it as untrusted input:**

```
FUNCTION DecodeOrigin(raw, output_rows, input_rows) -> vector<int32>:
    IF raw is absent:
        IF output_rows != input_rows:
            THROW IOException("no provenance but N->M; a fan-out or filtering "
                              "map must emit per-output-row parent indices")
        RETURN [0, 1, ..., output_rows-1]           // identity

    IF output_rows > MAX_SIZE / sizeof(int32):
        THROW IOException("implausible output row count")   // overflow guard
    IF SizeOf(raw) != output_rows * sizeof(int32):
        THROW IOException("provenance length mismatch")

    origin := Decode(raw)
    FOR EACH i, v IN origin:
        IF v < 0 OR v >= input_rows:
            THROW IOException("provenance[i] out of range [0, input_rows)")
    RETURN origin
```

**Stamping — a gather, not a copy loop:**

```
PROCEDURE StampCorrelated(output, input, origin, projected_input,
                          base_idx, start, produced):
    IF produced == 0: RETURN
    sel := SelectionVector(produced)
    FOR r IN [0, produced):
        sel.set_index(r, origin[start + r])       // start = rows already drained

    FOR k IN [0, projected_input.size()):
        VectorOperations::Copy(input.data[projected_input[k]],
                               output.data[base_idx + k],
                               sel, produced, 0, 0)
```

A flat `VectorOperations::Copy` through a `SelectionVector` both replicates rows
for fan-out and severs the emitted chunk's dependency on the input chunk's
buffers — which matters because the input chunk is owned upstream and will be
recycled.

## 5. Invariants — this is the part that bites

**1. DuckDB re-passes the *same* input chunk while you return `HAVE_MORE_OUTPUT`.**
That is what makes the drain branch work at all: you hold `origin` across calls
and keep gathering from `input` at those indices. But it means a stale `origin`
paired with a *different* input chunk is an out-of-bounds read. Record
`input.size()` at decode time and assert it on every drain. The assert should be
dead code; keep it anyway — the failure mode it prevents is silent memory
corruption, not a crash.

**2. The output chunk is WIDER than your callee's output.**
DuckDB hands you `base_idx + projected_input.size()` columns. Your conversion
routine must write only `[0, base_idx)`. Two distinct traps:

- A helper that iterates `output.ColumnCount()` walks off the end of your
  callee's result. Bound it explicitly.
- If your conversion **rebinds** destination vector buffers (any zero-copy
  handoff does), then converting into a `Reference`-based *view* over the leading
  columns silently does nothing — the rebind lands on the view's `Vector` object
  and never reaches `output.data[c]`. Convert into a freshly-allocated scratch
  `DataChunk` of exactly `base_idx` columns, then `output.data[c].Reference(
  scratch.data[c])`. Buffer refcounting keeps the data alive after the scratch
  goes out of scope, and the trailing correlated slots stay untouched.

**3. Provenance is adversarial input.** It is used directly as an array index.
Validate length, range, and the row-count multiplication for overflow — before
the first dereference. A hostile or buggy callee claiming an enormous row count
must produce a clean error, never a wrap or an OOB read. Write a fixture that
emits each malformed variant and assert the error.

**4. A 1→N fan-out can exceed `STANDARD_VECTOR_SIZE`.** Slice it across
`HAVE_MORE_OUTPUT` calls, tracking the offset in your buffer and passing it as
`start` so the stamp gathers the right slice of `origin`. Test with a fan-out
factor that pushes a single input chunk over 2048 output rows — this is the case
that exercises branch (A) and Invariant 1 together.

**5. Never return `FINISHED`.** A map operator is done when its child is done;
`FINISHED` tells DuckDB to stop feeding you and truncates the result. An empty
output for a chunk is `SetCardinality(0)` + `NEED_MORE_INPUT`.

**6. Zero-row and all-filtered cases are distinct.** `input.size() == 0`, a
callee returning 0 rows for a non-empty input (every row filtered), and a callee
returning 0 rows because it's *done* are three different things. Only the third
is an error (Invariant: a map owes one response per request).

**7. Projection pushdown is where silent corruption lives.** If you support it:
the unused-columns pass narrows the get's column list, so the callee emits a
*narrow* batch whose positions no longer match your bind-time output schema. You
must capture the callee-original column indices at rewrite time (they're gone
once you replace the node), thread them to the callee as the wire projection, and
drive your conversion with the same remapping. Get the mapping off by one and you
read a callee column into a correlated column's slot — wrong data, no error. If
you're not going to test this thoroughly, don't support it: leave pushdown
unadvertised and let DuckDB project above your operator.

**8. `ParamsToString()` is captured before execution.** It cannot carry runtime
counters. To report per-operator statistics in `EXPLAIN ANALYZE`, override
`OperatorState::Finalize(op, context)`, read your `GlobalOperatorState` atomics,
and write into `context.thread.profiler.GetOperatorInfo(op).extra_info["..."]`.
Guard on `QueryProfiler::Get(context.client).IsEnabled()` — with profiling off,
the entry is discarded and the work is wasted. `Finalize` runs once per pipeline
thread; each writes the same aggregate string, and last-write-wins leaves one
correct copy.

**9. Filter pushdown is unavailable, both paths.** DuckDB's planner discards
`LogicalGet::table_filters` for the in-out path, so a filter stays as a separate
node above your operator regardless. Don't build for it.

## 6. Kill switch and testing

**The kill switch is a testing tool, not just an ops escape hatch.** A boolean
setting that makes the rewriter return early leaves the stock row-by-row path in
place — and since both paths share the same bind, that gives you a **reference
oracle**. This is the single highest-value test you can write:

```sql
SET my_batch_lateral = false;
CREATE TEMP TABLE expected AS SELECT * FROM inputs i, my_map(i.a, i.b) ORDER BY ALL;
SET my_batch_lateral = true;
CREATE TEMP TABLE actual   AS SELECT * FROM inputs i, my_map(i.a, i.b) ORDER BY ALL;
-- assert expected == actual
```

`ORDER BY ALL` on both sides is required, not optional — you declared `NO_ORDER`.

**Proof of batching.** Result equivalence doesn't prove you batched anything.
Count calls (a log event or an atomic counter) and assert
`calls == ceil(rows / 2048)`, not `calls == rows`. Without this assertion a
regression that silently reverts to per-row calls passes your whole suite.

**Shape matrix.** One test per fan-out shape, because they exercise different
branches: 1→1 (identity provenance path), 1→0 (filtering), 1→N (stamp
replication), and N large enough that one input chunk overflows 2048 output rows
(multi-slice drain + Invariant 1).

**Adversarial provenance.** A fixture with a mode switch emitting: an index
outside `[0, input_rows)`, a wrong-length array, and a malformed encoding. Assert
each produces a clear error naming the callee and function. Run it on every
transport/backend you support — the validation must be symmetric across them.

**Parallelism.** Run with `SET threads=8` and a source that actually splits into
multiple morsels (a temp table or a UNION ALL, not `range()` or `VALUES`), so you
exercise multiple `OperatorState`s and multiple concurrent sessions.

**Plan shape.** An `EXPLAIN` test asserting your operator name appears, and that
it disappears when the setting is off. Cheap, and it catches an eligibility
regression immediately.

## 7. Checklist

- [ ] Boolean setting registered; rewriter returns early when false
- [ ] `optimize_function` (not `pre_optimize_function`); verified by log
- [ ] Eligibility requires non-empty `projected_input` and rejects finalize
- [ ] Logical node reproduces the get's `table_index`, bindings, and types exactly
- [ ] `Serialize` throws unless plan serialization is genuinely needed
- [ ] `ParallelOperator() = true`, `OperatorOrder()` justified
- [ ] Drain branch first in `Execute`; input-size assert present
- [ ] Callee columns written only to `[0, base_idx)`; scratch-chunk indirection if buffers are rebound
- [ ] Provenance validated for length, range, and overflow before use
- [ ] Never returns `FINISHED`; mid-stream EOS throws
- [ ] Session released cleanly in the state destructor; failed sessions discarded
- [ ] Result-equivalence test vs the setting-off path, with `ORDER BY ALL`
- [ ] Call-count assertion proving batching
- [ ] Fan-out matrix: 1→1, 1→0, 1→N, N > 2048
- [ ] Adversarial provenance fixture
- [ ] Multi-threaded run over a morsel-splittable source

---

## 8. As built in fabricator (2026-08-22)

**BUILT: `ILateralFunction`, both execution paths, ABI v79.** C++
`src/catalog/fabricator_lateral.{cpp,hpp}`; managed
`dotnet/Fabricator.Abstractions/ILateralFunction.cs` + `dotnet/Fabricator.Bridge/LateralExchange.cs`.
Gates: `verify_lateral` **144** (hermetic, GLOBAL demos — no ATTACH) and `verify_functions` 34 → **67**
(service, the catalog-bound half). Four mutants, each killed at its own section.

### 8.1 The framing this document does not state, and it is the strongest argument for the work

**We already had the fast path, under an awkward spelling.**
`PhysicalTableInOutFunction::ExecuteInternal` branches on `if (projected_input.empty())` and THAT branch
passes the whole chunk to `in_out_function`. Our pre-existing `_each(<input table>)` form takes a `{TABLE}`
argument, so it is uncorrelated, so `projected_input` is empty — it has always been one call per chunk. What
was row-by-row was the *idiomatic* spelling. So this is not "make in-out faster"; it is **let users write
`FROM t, f(t.a, t.b)` and get the speed the awkward spelling already had** — plus a spelling an in-out cannot
offer at all, since its input must be a relation the caller can NAME.

### 8.2 What the document got right

Everything load-bearing. The API references were all verified against our pinned 1.5.5 and hold:
`LogicalGet::projected_input`; the row-by-row branch and its `ConstantVector::Reference` +
`SetCardinality(1)` mechanism; `base_idx = chunk.ColumnCount() - projected_input.size()`; the
`FinalExecute not supported for project_input` throw; `OptimizerExtension` carrying both hooks. §0.5's
ASYMMETRY is exactly right and is the single most important fact for the bind: positional arguments do NOT
arrive in `input.inputs`, they arrive as `input_table_types`, and the literal shape is detected by
`input_table_names` coming back all-empty.

**§5's Invariant 2 was RIGHT to insist on the scratch-chunk indirection, and for us it is MANDATORY rather
than optional** — `ArrowStreamReader::Drain` calls `output.Reset()` and writes through `ArrowToDuckDB`, which
rebinds destination vectors; draining straight into the wide output chunk would clear the correlated columns
DuckDB had just stamped. We allocate the scratch chunk FRESH per drain and `Reference` its columns out, per
the document's own advice: a reused chunk's `Reset()` restores the same cached buffers, so the next drain
would overwrite rows already handed downstream.

**§0.5's named-argument caveat was right and the bug is UNFIXED in 1.5.5.** See
docs/duckdb-upstream-issues.md §5 for the mechanism; the practical consequence is in §8.4 below.

### 8.3 Where the build diverges

- **The provenance rides the WIRE as a trailing int32 column**, not as a separate out-parameter. One wire
  format serves both paths (the row-by-row path ignores it — DuckDB stamps — but still validates it, so the
  two paths agree on what is an ERROR, which is what makes the reference oracle sound).
- **Validation is SPLIT rather than duplicated.** The managed marshaling layer checks the column count, the
  provenance LENGTH and the absent-strict rule; the HOST checks the RANGE, because the host is what indexes
  with it. Checking the range on both sides would have made one of the two dead code — and it would have
  been the host's, i.e. the one guarding the memory access. (It was written duplicated first; the mutant
  that removes the host check then survives, which is what exposed it.)
- **`OperatorOrder()` stays INSERTION_ORDER**, not the document's `NO_ORDER`. Our operator emits rows grouped
  by input row in input order, so INSERTION_ORDER is TRUE of it; `ParallelOperator()` is what buys
  cross-morsel parallelism, and `NO_ORDER` has measured costs elsewhere in this tree
  ([scan-concurrency.md](scan-concurrency.md) §7d — a bare `LIMIT` starts returning arbitrary rows, and since
  §7c it would also disable the parallel write sink).
- **Projection pushdown is NOT advertised**, which is §5 Invariant 7's own advice when it will not be
  thoroughly tested. With `projection_pushdown` false DuckDB never narrows the get, so the callee's batch
  positions always match the bind-time output schema and the whole class of silent corruption is
  unreachable. The eligibility check additionally refuses a get whose `projection_ids` are non-empty, so if
  something ever does narrow one we fall back to the stock path rather than mis-read a column.
- **No `LogicalExtensionOperator::Serialize` override.** The document says to throw; the inherited
  implementation does not throw and is what the two existing `Fabricator*FinalizeOperator` nodes use. It
  matters because DuckDB's common-subplan optimizer hashes operators BY SERIALIZING them — it runs before our
  `optimize_function`, so our node is never reached, but a throwing Serialize would be a landmine.

### 8.4 What only building it revealed

- **⚠⚠ THE CORRELATED PLAN DE-DUPLICATES BEFORE CALLING US, and every performance claim is bounded by it.**
  Decorrelation puts a DISTINCT aggregate (the delim scan) under the operator, so the function is called once
  per distinct correlated TUPLE and the join above re-expands the result. A 20 000-row table with 97 distinct
  correlated values hands the callee at most 97 rows on EITHER path. This is not a footnote: it means the win
  scales with distinct tuples, and it invalidated the first version of the batching gate, which asserted
  `max(batch_rows) > 100` over exactly such a table — unreachable by construction.
- **The batched call count is NOT `ceil(rows / 2048)`.** The delim scan emits one chunk per radix partition,
  so chunk sizes depend on the thread count as well: 200 000 all-distinct rows gave **111 calls (1802
  rows/call)** at default threads, and 16 calls for 5000 rows. The gate asserts a RANGE plus the row-by-row
  leg's exact `= 1` as its positive control.
- **The measurement, taken after the fact because it was free:** 200 000 rows, the cheapest possible callee
  (`n * 2`, so this is CROSSING OVERHEAD ALONE) — batched **0.030–0.075 s / 111 calls**, row-by-row
  **0.902–1.054 s / 200 000 calls**. ~30x wall clock and ~100x CPU (0.08 s vs 9.7–11.7 s user), i.e. ~5 µs
  per crossing. §0's gate 2 asks for a 20x gap to justify the work; the crossing overhead alone clears it,
  and a callee whose per-call cost is a round trip clears it by orders of magnitude.
- **A named argument cannot be used correlated (upstream), so the guidance is: declare a per-call constant
  POSITIONALLY.** It then arrives as a constant input column and works in both shapes. What remains
  unavailable is bind-time configuration OF A CORRELATED CALL.
- **`duckdb_logs` IS THE WRONG INSTRUMENT for a call count.** It flushes per-thread lazily: a read
  immediately after a query that made 98 crossings saw **1** entry, and `disable_logging()` does not flush
  either. §6's "count calls" is best served by making the callee report its own batch size as DATA —
  `fabricator_lat_scale` returns `batch_rows`, which is deterministic and needs no logging at all. The Debug
  line stays as a diagnostic.
- **A whole-chunk 1→0 needs a purpose-built fixture to be gated.** The "never return FINISHED" invariant
  (§5.5) survives an ordinary filtering test, because a partly-filtered chunk still returns rows. It is
  killed only by a chunk that is ENTIRELY empty with more chunks behind it — 4000 of 6000 distinct values
  emitting nothing, **at threads=1**: with several threads each has its own chunk boundaries and the same
  mutant returned the right answer at threads=4 and 0 at threads=1.
- **A lateral function must answer `CatalogFunctionSet.ParamSchema`.** Its positional parameters become the
  DuckDB argument types, so without that the declaration is listed by `fabricator_functions()` and the
  function still "does not exist" — found by running the catalog gate, which is the whole reason that gate
  exists (the hermetic demos are global and never touch this path).

### 8.5 Not built

`FinalExecute` (§2b makes it unreachable for a correlated call, and the eligibility check refuses a
finalize-bearing function defensively); `ParamsToString` runtime counters (Invariant 8 — the string is
captured before execution, and the per-operator `OperatorState::Finalize` route was not wired); projection
pushdown (§8.3).

### 8.6 ⛔ A PER-FUNCTION batching flag on `ILateralFunction` — considered 2026-08-23, DECLINED

**Mechanically trivial and needs no ABI change**: the bind's `out_schema` already crosses, so a
`fabricator.batched = "0"` in Arrow schema/field metadata (the `fabricator.volatile` / `fabricator.param_style`
precedent) could be read into `LateralBindData` and tested in `LateralIsEligible`. ~20 lines. Precedence would
have to be **AND** — session ∧ function — or `fabricator_batched_lateral` stops being a reference oracle.

**Declined because every reason a function might decline batching dissolves:**

| reason | what actually happens |
|---|---|
| the callee cannot compute provenance | then it is 1:1, which batches fine — the framework fills in the identity map |
| its API takes one item per request | the callee loops internally, which is STRICTLY better than the host looping (no crossing per row) and lets it pipeline its own requests |
| a 2048-row batch is too much memory | same — the callee slices |
| its cost is per-ROW, not per-call | then by §0 gate 1 it should not be a lateral function at all (a scalar, or `LIST<STRUCT>` + `UNNEST`) |

**And the one case that looked real is FALSE — measured, because the intuition is backwards.** The hypothesis
was a `LIMIT` above a fan-out: the row-by-row driver would stop being fed after a few outer rows while a
batched call has already done 2048 rows of callee work, so an expensive per-row callee would lose badly. On 8
distinct outer rows sleeping 100 ms per call:

```
row-by-row, LIMIT 1 : 0.916 s     <- eight sleeps
row-by-row, no LIMIT: 0.870 s     <- the control: identical, so there is no early exit at all
batched,    LIMIT 1 : 0.107 s     <- one sleep
```

There is no early exit to lose: the correlated input is the hash-join **BUILD** side (fed by the delim scan),
so it is consumed in full whatever sits above. Batching is 8x faster under a `LIMIT` too.

⇒ it would be a flag with **no correct use**, which is the shape this tree has already priced once (the
per-table `exact_filter_pushdown` follow-on is deferred for exactly this reason — no mutant can kill an
untestable flag, so no gate can cover it, and it becomes declaration surface a plugin author must reason about
for nothing).

**THE TRIGGER CONDITION TO WATCH FOR** — the one thing a per-function declaration would genuinely buy — is
TESTING ERGONOMICS: `fabricator_batched_lateral` is session-wide, so with two lateral functions in one query
you cannot A/B just one of them.

**And if a real case does appear, the better knob is not a boolean but a MAX BATCH SIZE** (an API with a hard
per-request limit, or a memory bound). It degrades gracefully — still our operator, still one
provenance-carrying call per N rows, correlated columns still gathered — where a boolean falls all the way back
to one call per outer row. It needs a loop over sub-slices of the input chunk, tracking an offset; the
provenance contract already supports it unchanged.

---

## 9. Bind-time constants: `Params.Constant` + `const_arg` — BUILT 2026-08-29 (C++ + C#, no ABI bump)

§5's workaround ("declare the per-call constant positionally, it arrives as a constant input column") gives
a lateral function the constant's *runtime* value, which cannot shape the OUTPUT SCHEMA — the bind sees no
argument values at all: DuckDB rewrites every argument of a table-in-out call into the synthesized input
relation (`bind_table_function.cpp`), named parameters are unspellable in the correlated shape (§5), and the
one channel that provably survives the rewrite in BOTH call shapes is the column TYPE. This section is the
as-built record of the mechanism that rides it.

**The author contract.** A lateral declares `Params.Constant("fields")` — a positional slot registered as
`ANY` — and reads the constant's typed value in `Bind`'s `args` under the parameter's name. The slot never
reaches the rows: the host strips it from the bind's input schema and from every `Session.Call` chunk
(`LateralBindData.wire_slots` — the child chunk still carries it at its positional index, the wire skips it,
and the correlated split point is the new `arg_width`, not `input_types.size()`).

**The caller contract.** In the LITERAL shape a bare constant works with no wrapper — the binder folds the
arguments into `Value`s and the C++ bind forwards `input.inputs[slot]` into the managed args batch. In the
CORRELATED shape only the type crosses, so the caller wraps the constant: `const_arg(…)`, a host CONSISTENT
scalar whose per-call-site bind (ABI v80) parks the value in a registry (`CapturedConstants`) and resolves
its result type to `STRUCT("__fab_const_<md5>": VARCHAR)` — the member NAME is the registry key, and the
member-name PREFIX is what makes a capture struct unambiguous against a user's own single-member struct
constant. The scalar's bind runs while the binder builds the synthesized input relation, i.e. BEFORE the
lateral's own bind consumes the key. A bare constant (or a real column) in a correlated constant slot is
refused with a message naming the wrapper.

**The lifecycle, and it is MEASURED, not designed on taste: an entry is removable only when it is CONSUMED
*and* UNREFERENCED — neither signal alone is correct.** The prototype (sample plugin, 2026-08-29) shipped
dispose-only release first and every literal-shape call MISSED: in that shape the binder folds the argument
and DISCARDS the bound scalar expression, so the binding's Dispose fires BEFORE the consumer's bind — while
in the correlated shape the bound scalar lives in the subquery until plan teardown and Dispose fires after.
So `Store` takes a reference, Dispose releases it, the consumer's lookup marks it consumed, and removal
happens at whichever event sees both. Mutation-tested: reverting Release to dispose-only dies at
`verify_lateral`'s wrapped-literal assertion with the explicit "expired" error (171 pass before it). Every
re-bind — view, EXPLAIN, prepared statement (DuckDB re-binds each EXECUTE — MEASURED; the PREPARE-time
bind's plan is torn down) — re-runs the scalar bind and repopulates before the consumer looks, so the state
drains to zero after every statement. The stored value is an OWNED copy via one Arrow IPC round-trip, whose
canonical bytes are also what the md5 key hashes — same value ⇒ same key, which keeps the CONSISTENT
contract and makes double-Store idempotent through the refcount.

**Two binder quirks the build flushed out, both fixed in `fabricator_lateral.cpp` and both PRE-EXISTING
v79 sharp edges** (`bind_table_function.cpp:425-457` reports `input_table_types` differently per shape, and
neither reading matches what the child delivers): the CORRELATED shape RELABELS the reported type to the
declared parameter type without casting the child — `Vector::Reference` on the mismatch was an INTERNAL
error that INVALIDATED the whole database; the LITERAL shape reports the PRE-cast expression type while
delivering the POST-cast value. Fixed by normalizing `input_types` to the DECLARATION (the one contract
stable across both shapes; ANY-declared slots keep the bound type — that is what carries the capture
struct) and casting each runtime chunk to it at the marshaling seam.

Consumers: `fabricator_lat_fields(n, fields)` (host, `CustomFunctions.cs` — fields may be a VARCHAR of
comma-separated names, an integer count, or a LIST of names, pinning that the channel is type-generic) and
`plug_lat_fields` (sample plugin, ZERO plugin-side machinery — the point of hosting it). Gate:
`verify_lateral` 168 → **211**, one mutant killed at its own assertion. No ABI bump: the `constant` param
style is an additive metadata value, and the capture scalar is ordinary v80 machinery.

## Appendix — the `CLAUDE.md` entry, moved verbatim (2026-08-23)

> §8 above is the as-built record. This is the entry `CLAUDE.md` carried alongside it — the scoping
> bullets, the declined per-function flag, and the corrections — moved here to bound that file.

- **`ILateralFunction` — BATCHED CORRELATED LATERAL. ✅ BUILT 2026-08-22 (ABI v79, ADDITIVE; C++ + C#),
  user-directed. Gates: `verify_lateral` **168** (hermetic) + `verify_functions` 34 → **67** and
  `verify_plugin` 79 → **97** (service); tiers **74/74 — 7986** and **52/52 — 2221** LOCALLY, both exactly the
  old floor plus the new assertions, so NO other suite moved. Four mutants, each killed at its own section.
  Committed + pushed as **`cea3921`**; **CI tier 1 GREEN ON ALL THREE PLATFORMS** (run `32632791345`,
  `verify_lateral` 168 + `ALL GREEN` on each of linux_amd64 / windows_amd64 / osx_arm64, read from the job
  logs rather than the status tick). ⚠ **osx_arm64 is the leg that mattered and it is worth knowing WHY it
  was in doubt**: this change's riskiest construct is the freshly-allocated scratch `DataChunk` whose columns
  are `Reference`d into the output and kept alive only by buffer refcount — the same Arrow-LIFETIME class as
  the `ArrowProducer::Release` use-after-free, which faulted ONLY on macOS because Apple's pthread validates a
  destroyed mutex where glibc and Windows do not. So a green Windows run would have proven nothing about it,
  and any future fault there should suspect that chunk's lifetime first. Full as-built:
  [docs/lateral_unnest_analysis.md](docs/lateral_unnest_analysis.md) §8; ABI:
  [docs/abi-history.md](docs/abi-history.md) §v79. THE SCOPING BULLETS BELOW ARE THE RECORD OF HOW IT WAS
  ARGUED — every API claim in them held; the two predictions that did NOT are corrected here.**
  - **MEASURED, 200 000 rows, the CHEAPEST POSSIBLE callee (`n * 2`), so this is CROSSING OVERHEAD ALONE:
    batched 0.030–0.075 s / 111 calls vs row-by-row 0.902–1.054 s / 200 000 calls — ~30x wall clock and
    ~100x CPU (0.08 s vs 9.7–11.7 s user), i.e. ~5 µs per crossing.** §0's gate 2 asks for a 20x gap; the
    crossing overhead alone clears it, so the "measure `c` first" step the user waived was not needed to
    justify the work — the number arrived for free afterwards.
  - **⚠⚠ THE FINDING THAT BOUNDS EVERY CLAIM, AND NOTHING IN THE DESIGN NOTE MENTIONS IT: THE CORRELATED PLAN
    DE-DUPLICATES BEFORE THE FUNCTION IS CALLED.** Decorrelation puts a DISTINCT aggregate (the delim scan)
    under the operator and the join above re-expands, so the callee is invoked once per distinct correlated
    TUPLE — a 20 000-row table with 97 distinct argument values hands it at most 97 rows on EITHER path. Cost
    and win both scale with DISTINCT TUPLES, not with outer rows. It also invalidated the first version of the
    batching gate, which asserted `max(batch_rows) > 100` over exactly such a table: unreachable by
    construction, and it FAILED rather than passing vacuously only because the observable is a real value.
  - **⚠ THE CALL COUNT IS NOT `ceil(rows/2048)` — so the design note's own assertion cannot be written.** The
    delim scan emits one chunk per radix partition, so chunk size depends on the THREAD COUNT too: 111 calls
    for 200 000 rows at default threads, 16 for 5000. The gate asserts a RANGE, with the row-by-row leg's
    exact `= 1` as its positive control.
  - **⚠ `duckdb_logs` IS THE WRONG INSTRUMENT FOR A CALL COUNT, and this is reusable well beyond here.** It
    flushes per-thread LAZILY: a read immediately after a query that made 98 crossings saw **1** entry, and
    `disable_logging()` does not flush either. Two versions of the gate were written on it and both measured
    whatever happened to be visible (one passed for the wrong reason at 98 == the PREVIOUS leg's count). The
    fix is to make the callee report its own batch size as DATA — `fabricator_lat_scale` returns
    `batch_rows` — which is deterministic and needs no logging. **Existing suites' `count(*) > 0` log
    assertions are fine; a COUNT over log lines is not.**
  - **⚠ A NAMED ARGUMENT CANNOT BE USED IN THE CORRELATED SHAPE — UPSTREAM, UNFIXED IN 1.5.5, and the design
    note predicted it.** `f(t.a, opt := 5)` does not bind: `BindTableFunctionParameters` tests the bind TYPE
    first and for `TABLE_IN_OUT_FUNCTION` sweeps EVERY argument expression into the input subquery before the
    named-parameter extraction below it runs, so the named arg becomes a phantom input column and the arity
    stops matching. The literal shape is unaffected (all-scalar args take the standard path). **THE GUIDANCE:
    declare a per-call constant POSITIONALLY** — it arrives as a constant input column and works in both
    shapes; what stays unavailable is bind-time configuration OF a correlated call. Report-ready:
    [docs/duckdb-upstream-issues.md](docs/duckdb-upstream-issues.md) §5.
  - **⚠ "NEVER RETURN FINISHED" SURVIVES AN ORDINARY FILTERING TEST, so its gate is purpose-built.** A partly
    filtered chunk still returns rows; the invariant is killed only by a chunk that is ENTIRELY empty with
    more chunks behind it (4000 of 6000 distinct values emitting nothing) **at threads=1** — measured, the
    same mutant returns the RIGHT answer at threads=4, because each thread's chunk boundaries differ.
  - **⚠ THE VALIDATION SPLIT, arrived at by a mutant SURVIVING.** Range validation was written on BOTH sides;
    removing the host's then changed nothing, which is the definition of dead code — and it would have been
    the copy guarding the memory access. Now: the managed shim checks the column count, the provenance LENGTH
    and the absent-strict rule (what it alone knows cheaply); the HOST checks the RANGE, because the host is
    what INDEXES with it.
  - **⚠ ONE PRE-EXISTING GAP FOUND AND DELIBERATELY NOT FIXED: `CatalogFunctionSet.ParamSchema` answers for
    scalar/table/aggregate/table_sql and NOT for in-out/collector.** A lateral function had to be added there
    (its positional parameters ARE the DuckDB argument types, so without it the declaration is listed by
    `fabricator_functions()` and the function still "does not exist" — measured). The in-out omission is
    survivable only because `GetOrCreateCustomInOutFunction` CATCHES the failure and falls back to the bare
    `{TABLE}` signature, which is right for every in-out shipped today and would silently DROP a declared cost
    arg on a CATALOG-bound one. Left alone: fixing it changes how every existing in-out's signature is built.
  - **THE WIN IS MEASURED IN CI, not merely argued, and the instrument is a PLUGIN function — which proves the
    audience path at the same time.** `plug_lat_slow(n, millis)` (Fabricator.SamplePlugin) sleeps ONCE PER
    CALL, which is the only cost batching can amortise and the shape this kind is FOR (a REST or model call).
    `verify_plugin`'s new section runs 8 distinct outer rows at millis=100 on both paths in one process:
    **0.870 s / `max(batch_rows)=1` row-by-row vs 0.154 s / `max(batch_rows)=8` batched**, with the serial
    leg's own duration as the positive control and both legs' answers asserted (a speed claim is worthless if
    the fast leg dropped a row). ⚠ threads=1 is REQUIRED — the delim scan emits one chunk per radix partition,
    so several threads split the 8 rows and move the ratio. ⚠ A plugin references `Fabricator.Abstractions`
    and nothing else, so this also establishes that declaring a lateral costs a plugin no more than declaring
    a scalar.
  - **⛔ A PER-FUNCTION batching flag on `ILateralFunction` — asked 2026-08-23, DECLINED, and the
    measurement is what settles it (docs §8.6).** It is mechanically trivial (schema metadata on the bind's
    out_schema, the `fabricator.volatile` precedent, no ABI change, precedence AND so the oracle survives) and
    every reason to want it dissolves: a callee that cannot compute provenance is 1:1 and batches fine; a
    one-item-per-request API loops internally, which BEATS the host looping; a memory bound slices the same
    way; and a per-ROW cost means it should have been a scalar per §0 gate 1. **⚠ The one case that looked real
    is FALSE and I had it backwards** — a `LIMIT` above a fan-out does NOT let the row-by-row driver exit
    early, because the correlated input is the hash-join BUILD side and is consumed in full either way:
    8 distinct rows × 100 ms ⇒ **row-by-row 0.916 s with `LIMIT 1` vs 0.870 s without it (identical) vs 0.107 s
    batched**. So it would be a flag with NO CORRECT USE — the untestable-flag shape this file already prices
    at the per-table `exact_filter_pushdown` follow-on. ⚠ The trigger condition to watch for is TESTING
    ERGONOMICS (the setting is session-wide, so two lateral functions in one query cannot be A/B'd
    separately), and the better knob if one ever appears is a MAX BATCH SIZE, not a boolean — it degrades to
    N-row calls through our own operator instead of falling back to one call per row.
  - **The one thing the build did NOT reuse, deliberately, per the user's instruction:** anything of the
    streaming in-out exchange. Five additive ABI entries and a separate `src/catalog/fabricator_lateral.cpp`;
    the only shared code is the read-only `ArrowStreamReader`. The exchange could not have served it anyway —
    it permits ONE exchange per binding (serialising parallel branches behind a gate) where the batched
    operator wants a session per thread, and it has no provenance channel.
  - **⚠ `OperatorOrder()` stays INSERTION_ORDER, NOT the note's `NO_ORDER`** — our operator emits rows grouped
    by input row in input order, so INSERTION_ORDER is TRUE of it, and `ParallelOperator()` is what buys
    cross-morsel parallelism. §7d already measured what `NO_ORDER` costs (a bare `LIMIT` returns arbitrary
    rows; since §7c it would also disable the parallel write sink). Consequence: the suite's `ORDER BY ALL` is
    still mandatory, but because the PLAN does not promise order, not because we declared it away.
  - **⚠ The design note says its DuckDB API references are UNVERIFIED. Every load-bearing one was verified
    against our pinned 1.5.5 while building this and they ALL HOLD — the list is below, so do not re-derive
    them.**
  - **⚠⚠ THE FRAMING THE DOC DOES NOT SPELL OUT FOR OUR CASE, and it is the whole point: WE ALREADY HAVE THE
    FAST PATH, UNDER AN AWKWARD SPELLING.** `PhysicalTableInOutFunction::ExecuteInternal` branches on
    `if (projected_input.empty())` (`physical_tableinout_function.cpp:87`) and that branch passes the WHOLE
    input chunk to `in_out_function`. The non-empty branch is explicitly commented *"when project_input is set
    we execute the input function row-by-row"* and does `ConstantVector::Reference(...)` + `SetCardinality(1)`
    per outer row. ⇒ our `_each(<input table>)` form (a TABLE argument, so NO correlation, so
    `projected_input` EMPTY) is **already one call per chunk** — that is why 4g works per-chunk — while the
    IDIOMATIC `SELECT * FROM inputs i, fn(i.a, i.b)` is row-by-row. **So this feature is not "make in-out
    faster", it is "let users write the spelling they expect and get the speed we already have".** State it
    that way; it changes how the work is justified and it is the strongest argument for doing it.
  - **VERIFIED AGAINST OUR PIN (all of these hold — banked so a future session does not repeat it):**
    `LogicalGet::projected_input` exists (`logical_get.hpp:54`); the row-by-row branch and its mechanism as
    above; **`base_idx = chunk.ColumnCount() - projected_input.size()`** (`:120`) — exactly Invariant 2's
    arithmetic; **`throw InternalException("FinalExecute not supported for project_input")`** (`:163`), which
    is §2(b)'s refusal, plus `plan_get.cpp:86` *"LogicalGet::project_input can only be set for table-in-out
    functions"*; `OperatorState::Finalize(const PhysicalOperator &, ExecutionContext &)`
    (`physical_operator_states.hpp:36`) for Invariant 8; and `OptimizerExtension` carries BOTH
    `optimize_function` and `pre_optimize_function` (`optimizer_extension.hpp:40/44`).
  - **⚠ THE SCAFFOLDING IS NOT NOVEL — WE HAVE THE THREE-PIECE PATTERN TWICE IN-TREE.** `FabricatorInOutOptimize`
    (`src/catalog/fabricator_schema_entry.cpp:2310`, registered `:2317-2319`) is an `OptimizerExtension` that
    already wraps in-out `LogicalGet`s in `LogicalExtensionOperator`s — `FabricatorExchangeFinalizeOperator`
    (`:1952`) and `FabricatorCollectorFinalizeOperator` (`:2248`) — each building a
    `PhysicalOperatorType::EXTENSION` operator. `src/fabricator_optimizer.cpp:236` is a second
    `OptimizerExtension`. Copy those shapes rather than inventing one; the 4g-finalize entry in this file
    records why that hook fires exactly once, which is the same reasoning the new operator needs.
  - **THE MANAGED SIDE IS WHERE THE NEW CONTRACT LIVES: provenance.** §2(a) — when we send N rows and get M
    back, the callee must return an M-length array of INPUT-ROW INDICES, or the correlated columns cannot be
    stamped and 1→N / 1→0 are impossible. Make it **additive and optional** in the in-out protocol: absent ⇒
    identity 1→1, and make the absent case **STRICT** (absent + `M != N` is an ERROR, never a guess). That is
    the one genuinely new piece of ABI surface; everything else reuses `inout_open`/`push`/`abort`.
  - **APPLY §0'S GATES BEFORE WRITING ANYTHING.** Gate 1: a 1→1 callee wants a SCALAR and a bounded 1→N wants
    `LIST<STRUCT>` + `UNNEST` — both batched by construction, neither needs any of this. Gate 2: measure the
    per-call overhead `c` against per-row work `w`; the doc's own bar is that a 1.5x gap does not justify it
    and a 20x gap does. ⚠ **For OUR providers `c` is a SQL Server round trip**, so the SQL-Server `_each` and
    any plugin whose callee is a network or model call pass gate 2 easily — but a pure-C# `cf_*` in-out
    (`cf_running_sum`) does NOT, and building for it would be the untestable-flag mistake this file records.
  - **THREE RISKS THAT ARE OURS SPECIFICALLY, not the doc's:** (a) Invariant 2's REBIND trap — our Arrow→
    DataChunk conversion is exactly the "zero-copy handoff that rebinds destination buffers" the doc warns
    about, so the scratch-`DataChunk`-then-`Reference` indirection is probably MANDATORY for us rather than
    optional; check `arrow_ingest`'s write target before assuming otherwise. (b) Invariant 7 — we DO advertise
    `projection_pushdown` on scan paths, so decide explicitly whether the new operator advertises it, and the
    doc's advice is DON'T unless it is thoroughly tested (an off-by-one there reads a callee column into a
    correlated slot: wrong data, no error). (c) **The ambient rule** ([docs/scan-concurrency.md](docs/scan-concurrency.md)
    §8): a per-chunk managed call must establish the opener/txn the way the existing in-out path does — read
    every ambient in the crossing that sets them, never later.
  - **THE GATE PREDICTION HELD IN SUBSTANCE AND MOVED IN EVERY DETAIL — the kill switch IS a reference
    oracle, and that is what `verify_lateral` §3 is** (both paths' full result sets, `EXCEPT` in BOTH
    directions, at threads=4 over DUPLICATE correlated values with a per-row fan-out of 0..3). What changed:
    the home is a NEW HERMETIC suite, not `verify_table_inout`/`verify_custom_functions` — the demos are
    GLOBAL functions, so no ATTACH and no server is needed, and the catalog-bound half rides `verify_functions`
    in the service tier. The call-count assertion could not be written as `== ceil(rows/2048)` (see the
    de-duplication finding above) and is a `batch_rows` value rather than a log count. `ORDER BY ALL` is still
    mandatory — because the PLAN promises no order, not because we declared `NO_ORDER`.

