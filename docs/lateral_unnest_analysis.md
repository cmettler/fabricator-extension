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
