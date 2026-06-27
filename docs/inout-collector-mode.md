# Collector table-in-out (pipeline-breaker mode) — design idea, DEFERRED

> Status: **design note only — nothing built.** A *second* table-in-out execution shape alongside the Phase 6
> streaming exchange: a binding that **collects all input, emits nothing until input EOF, then emits its full
> output**. The streaming exchange ([the Phase 6 work](../CLAUDE.md)) is **untouched** — the two modes coexist,
> picked by a discovery `kind`. Builds on the v28 exchange ABI (reused as-is) + the injected-finalize machinery.

## Motivation

The streaming exchange (`IArrowInOutFunction.DoExchange`) emits output **interleaved** with input, per chunk,
under a single-slot gate. That is correct for high-fan-out (`_each`) functions but **cannot express a
whole-table transform** — one whose output depends on having seen *all* input rows (inject the full input as a
DAX `DATATABLE`, a lookup/dimension table, sort/dedup the whole input, collect TMDL fragments and apply once).

The wall is concrete and already hit: `daxevaltable` (DAX slice 5) is capped at **a single input chunk ≤2048
rows** because the streaming operator emits only inside `Execute` (driven by input-chunk pulls), and its single
"all input done" hook — the injected `OperatorFinalize` — is **handed no `DataChunk`**, so any output held back
until input EOF is **drained and discarded**. A binding that wants to see everything before emitting anything
loses its entire result.

The earlier intuition *"a pipeline breaker with unlimited input could never work"* was right **about the
streaming model** — buffering all input inside the single-slot gate deadlocks. The resolution is to stop
pretending it streams: declare it a **pipeline breaker**, accept the buffering, and host it on the DuckDB
scaffold built for exactly that (Sink + Source), not on the streaming operator.

## The two modes (coexist; picked by discovery `kind`)

| | Streaming exchange (Phase 6, **kept**) | Collector (this note) |
|---|---|---|
| Output timing | interleaved with input, per chunk | nothing until input EOF, then all of it |
| DuckDB shape | `in_out_function` **Operator** + gate + per-chunk length-0 sentinel | **Sink + Source** pipeline breaker |
| Memory | bounded (≤1 batch in flight) | **unbounded** (buffers input/output — inherent) |
| Author API | `DoExchange(input)→output`, author yields sentinels | `Collect(allInput)→output`, **no sentinel** |
| Fits | high-fan-out `_each` (CROSS APPLY / per-row EXEC) | whole-table: DATATABLE inject, lookup, sort/dedup, fragment-collect |

`kind='inout'` → streaming exchange; new `kind='collector'` → the pipeline breaker. Adding the `kind` string is
**additive** (a new metadata enum value) → **no ABI version bump**.

## Why this is the canonical DuckDB pattern

A Sink+Source operator (`IsSink()==true && IsSource()==true`) is exactly how `ORDER BY` (`PhysicalOrder`),
hash aggregates (`PhysicalHashAggregate`), and our own **4h C# aggregate** already work: the **Sink** consumes
*all* input across all threads/branches, a single sink-level **`Finalize`** fires once after everyone, then the
operator becomes a **Source** that emits the buffered result. The planner splits the pipeline at the sink. No
per-chunk coordination, no gate, no sentinel — conceptually simpler than the streaming exchange.

This is the **table-valued twin of the documented `apply_tmdl_agg` 4h aggregate** (collect fragments → one
atomic commit at finalize): same "effect happens once, single-threaded, after all input" safety property, which
makes the collector the natural home for *effectful* whole-input operations, not just data-in transforms.

## Why the native `in_out_function_final` is NOT the general answer

DuckDB's native `in_out_function_final(ExecutionContext, TableFunctionInput, DataChunk &output)` **does** receive
an output chunk and **can** emit (`HAVE_MORE_OUTPUT` until done). For a **single input pipeline** it gives
collector mode almost for free: return `NEED_MORE_INPUT` (no output) from every `Execute`, then stream the
collected result out of `function_final`.

**But it fires per UNION branch**, and `PhysicalUnion::BuildPipelines` can run branches **sequentially** — so
branch 1's `function_final` would emit (and commit/teardown) before branch 2 runs → **lost rows**. This is the
identical premature-finish trap already rejected for the in-out finish counter (see the 4g notes in CLAUDE.md).
So native-final collector is sound **only** when input is a single, non-UNION pipeline — too fragile as the
general signal. The injected single-shot `OperatorFinalize` (fires **once**, sink-level, after *all* branches)
is the correct "all input done" signal — which is exactly what the Sink+Source `Finalize` gives us.

## Scope of the build

**New:**
- **C# `IArrowCollectorTableFunction`** + its binding:
  ```csharp
  public interface IArrowCollectorTableFunction {
      string SchemaName { get; } string Name { get; }
      Schema Parameters { get; }                                   // CONSTANT (non-table) "cost" args
      IArrowCollectorBinding Bind(RecordBatch args, Schema inputSchema);
  }
  public interface IArrowCollectorBinding : IDisposable {
      Schema OutputSchema { get; }                                 // MAY depend on args AND inputSchema
      // allInput yields EVERY input batch (Sink drains the child fully); ends at real input EOF.
      // The returned enumerable is the FULL output (Source phase). No sentinel — input EOF and
      // output EOF are the genuine signals.
      IAsyncEnumerable<RecordBatch> Collect(IAsyncEnumerable<RecordBatch> allInput, CancellationToken ct = default);
  }
  ```
  A `StaticCollectorFunction` base (fixed `OutputSchema`) keeps simple cases one-liners. `IArrowInOutIsolation`
  still applies for SQL-backed collectors (one read txn at the configured level).
- **A new C++ Sink+Source operator** (the pipeline breaker): Sink buffers all input chunks (pushed into the
  exchange input slot), the single sink-level `Finalize` flips to source mode, Source `GetData` emits the
  collected output (null array = done). The wrapping EXTENSION operator's no-emit `OperatorFinalize` is the
  *model* for the single-shot signal; here that signal must instead drive emission, hence Sink+Source rather
  than a pass-through Operator.
- **`kind='collector'`** discovery tag → routes registration to the new operator (additive, no bump).

**Untouched:**
- The entire streaming `DoExchange` path — `IArrowInOutFunction`/`IArrowInOutBinding`, the gate, the per-chunk
  sentinel, the `ArrowNetExchange*` operator. The two modes coexist, selected by `kind`.

**Reused AS-IS — no new ABI:**
- The v28 entries `inout_bind` / `inout_exchange_open` / `inout_bind_close`. The **data contract is identical**:
  `inout_bind` resolves the binding + full output schema from cost args (1-row, nullable) + the input table
  schema; `inout_exchange_open` hands C# the input stream and gets back the output stream (isolation still
  applies); `inout_bind_close` tears the binding down. The collector binding is classified inside `InOutBind`
  alongside custom/TVF/proc.

The **only** difference lives in the C++ operator — **when** it pushes input vs pulls output — not in the ABI:
- *Streaming operator:* interleaves (push chunk → pull output → sentinel → next chunk), gate-coordinated.
- *Collector operator:* Sink pushes **all** input + marks input-EOF; **then** Source pulls output.

From C#'s side both are identical: `await foreach` the input to EOF, yield output. `Collect` simply doesn't
yield its first output batch until the input enumerable is exhausted, and the sync-over-async block on the
output stream's `ReadNextRecordBatchAsync` naturally waits for that. So `inout_exchange_open` slots in
unchanged — the timing lines up because C++ only pulls output in its Source phase, after the Sink has drained
and EOF'd the input. (A distinct `collector_open` entry could be added purely for naming clarity, but it would
be a redundant alias of `inout_exchange_open` — not a technical need.)

## "Empty record batch signals EOF"

In the collector model you do **not** need the streaming model's per-chunk length-0 sentinel (that was the
`NEED_MORE_INPUT` handshake). The natural Arrow C-stream release (null array) already carries both signals:
input stream end = "all input collected"; output stream end = "all output emitted." An explicit length-0 batch
as an EOF marker is harmless but **redundant** with the null-array release — so the protocol is simpler than the
exchange's, not more complex.

## What this fixes / enables

- **Removes the slice-5 cap** — `daxevaltable` (and any "inject the whole input as a DATATABLE / lookup table"
  function) takes **arbitrarily many** input chunks instead of one ≤2048-row chunk.
- **Simpler, safer author API** — `Collect(allInput)→output` has **no sentinel contract to forget**, the single
  biggest footgun of the streaming `DoExchange`.
- **A home for whole-input effects** — the table-valued twin of `apply_tmdl_agg`: collect → act/emit once,
  single-threaded, after all input. Effect-safe by construction.

## Cost / trade-off (inherent, not incidental)

It is a **pipeline breaker** — it buffers all input (or all output) with **no streaming memory bound**. That is
the definition of "see all input before any output," not a flaw to engineer away. Right for parameter/lookup
tables, model fragments, whole-table transforms; **wrong** for high-fan-out streaming (use the exchange there).
DuckDB's external-sort/aggregate spill applies to its *own* operators, not to a C#-buffered collector — so a
collector that must bound memory would have to spill in C# (out of scope; the streaming exchange is the
bounded-memory path by design).

## Build sketch (when motivated)

1. C# `IArrowCollectorTableFunction`/`IArrowCollectorBinding` + `StaticCollectorFunction`; classify in
   `InOutBind`; register a demo (`cf_collect` — e.g. emit input row count + a fold) with no SQL object.
2. C++ Sink+Source operator; route `kind='collector'` registration to it (additive `kind`, no ABI bump).
3. Reuse `inout_bind`/`inout_exchange_open`/`inout_bind_close` verbatim.
4. **Migrate `daxevaltable` to the collector** (drops its single-chunk cap) — the proof-of-value.
5. Tests: a multi-chunk collector (proves >2048-row input), a **sequential-UNION** input case (the schedule
   that catches premature-finish — must see *all* branches before emit), a cost-arg-dependent output schema,
   and a re-run of `verify_dax` for `daxevaltable`.

## Recommendation

Build **on demand** — when a real whole-table need lands (lifting the `daxevaltable` cap, a sort/dedup-the-input
function, or the `apply_tmdl_agg`-style table-valued collector). It's a contained addition (1 C# interface + 1
C++ operator + 1 additive `kind`, no ABI bump, streaming exchange untouched), and it slots cleanly beside the
streaming exchange as the second of the two table-in-out execution shapes.
