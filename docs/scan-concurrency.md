# Scan concurrency — how a fabricator scan uses cores, and every measured trap in doing so

> **Status: CURRENT (2026-08-21). Describes shipped behaviour as of `0.0.11`.**
>
> **WHY THIS DOC EXISTS.** The parallel scan (`38189db`) and the batch-size work (`e9f4b13`) were argued in
> commit messages and code comments, and everything measured since — the streaming × `PhysicalUnion` stall,
> the batch-is-also-a-file coupling, the thread-invariance of the transient rowid — accumulated in places that
> answer a different question. This is the one page for *"what happens when a fabricator scan meets
> `SET threads`"*, because that question now has several interacting answers and two of them are traps.
>
> ⚠ **It is deliberately NOT about connection or transaction concurrency.** Which provider connection a
> statement uses, MARS, the one-writer rule and `dbt --threads N` are [transactions.md](transactions.md) and
> [transaction-concurrency.md](transaction-concurrency.md); multi-writer COMMIT safety is
> [delta-transactions.md](delta-transactions.md) §8 and [known-limitations.md](known-limitations.md).

---

## 1. What parallelizes, and what does not

Every Arrow-streaming read surface goes through ONE scan implementation — `ArrowStreamScan` /
`ArrowStreamInitGlobal` in [src/fabricator/arrow_ingest.cpp](../src/fabricator/arrow_ingest.cpp) — registered
by the catalog table scan (`fabricator_scan`,
[src/catalog/fabricator_table_entry.cpp](../src/catalog/fabricator_table_entry.cpp)), by catalog-bound and
global table functions ([src/catalog/fabricator_schema_entry.cpp](../src/catalog/fabricator_schema_entry.cpp)),
and by `fabricator_query` / `fabricator_functions` / `fabricator_server_info` / `fabricator_host_query`. So
anything said here about the scan is true of **every provider** — SQL Server, Delta, DAX, delta-rs, a plugin's
— rather than of one.

The shape is: **the PULL is serialized, the CONVERSION is not.**

```
GetNextBatch:    lock(gstate.main_mutex) -> stream.get_next(...) -> hand the batch to the LOCAL state -> unlock
ArrowStreamScan: (no lock)               -> ArrowToDuckDB(lstate, ...) -> project into the output chunk
```

That is byte-for-byte DuckDB's own arrow scan (`ArrowScanParallelStateNext` in
[src/function/table/arrow.cpp](../duckdb/src/function/table/arrow.cpp) takes the global mutex, calls
`GetNextChunk()`, releases, and each thread converts its own batch). **The `RecordBatch` is therefore the
morsel**, and everything in §2–§4 follows from that one fact.

**MEASURED, not read out of the source** (§6's instrument, four batches x 500 ms): cost placed in the SOURCE,
i.e. inside `get_next`, is **2451 ms at threads=1 and 2454 ms at threads=4 — flat**, because the pull holds one
mutex. The same 2 s of cost placed in the PROJECTION above the scan goes **2425 -> 883 ms**. That is the split,
and it is an invariant rather than a shortcoming: the serialized pull is exactly what lets a provider's reader
(a `SqlDataReader`, an ADOMD reader) be touched from one thread only.

What is deliberately still single-threaded, and must stay so:

| path | `MaxThreads()` | why |
|---|---|---|
| the Arrow stream scan | `SET threads` | §2 |
| the table-in-out exchange | 1 | an intra-pipeline cap; parallel `UNION ALL` branches are separate pipelines, serialized by its own gate |
| the collector | 1 | one shared holder buffer per execution |
| `fabricator_fs_spike` diagnostics | 1 | a probe, not a data path |
| the per-file Delta loop | (not a DuckDB axis) | `FABRICATOR_DELTA_PREFETCH`, **default 1 = sequential**; concurrency there is C# `Task.Run` behind a semaphore, and it is a REMOTE-IO win, not a CPU one |

---

## 2. `MaxThreads()` was hardcoded to 1, which made the whole pipeline above it serial

**MEASURED**, 6M rows, median of 3, `SET threads` the only variable:

| shape | threads=1 | threads=20 | |
|---|---|---|---|
| CPU-bound (md5 per row) | 1.42 s | **0.73 s** | ~1.95x |
| merge-bound (50k-group `GROUP BY`) | 0.589 s | 0.655 s | **threads HURT, ~11%** |

The old return value was `1`, justified as *"a single Arrow C stream is consumed serially"*. **That sentence is
TRUE of `get_next`, and the conclusion drawn from it was too strong** — it is true of the PULL, and the pull was
already the only thing under the mutex. What the `1` cost was not conversion throughput but **the pipeline: a
source declaring one thread makes every operator above it single-threaded.** That is the same effect the
`UNION ALL` core-saturation trick was working around by hand.

- **`SET threads` IS the knob and needed no plumbing.** `ThreadsSetting` has `SetGlobal`/`ResetGlobal` and no
  `SetLocal` — verified from the header and then from behaviour (`SET SESSION threads=4` errors with *"option
  threads cannot be set locally"*) — so the host query's fresh connection is on the same `DatabaseInstance`,
  shares the `TaskScheduler`, and there is nothing to propagate to it. Captured at `InitGlobal`, because
  `MaxThreads()` is handed no context.
- **⚠ IT IS NOT A WIN ON EVERY SHAPE, AND THE FIRST SHAPE MEASURED SAID IT WAS A LOSS.** Stopping at the
  `GROUP BY` would have concluded threading was a wash. Both numbers are real: the aggregate is merge-bound,
  not scan-bound, and a parallel hash-aggregate merge can cost more than the split saves. **Quote both.**
- **⚠ THE SQL SERVER PATH IS SAFE ONLY BECAUSE THE PULL STAYS SERIALIZED.** A `SqlDataReader` cannot be read
  from two threads, and it is touched exclusively inside `get_next`, which holds `gstate.main_mutex`; what
  parallelizes there is the Arrow→DuckDB conversion. That is a STRUCTURAL argument, not a measurement, so it
  was gated: the service tier is the first run of parallel scans against a real SQL Server.
- The gate for the change itself was an IDENTITY, not an improvement: both tiers at exactly their prior counts,
  including the plan-sensitive suites (batched-read routing, `merge_into`, subplan dedup) — so no answer and no
  routing followed the extra threads.
- **⚠⚠ AND FOR TEN WEEKS IT ONLY PAID OFF WHEN A BLOCKING OPERATOR SAT ABOVE THE SCAN.** `Pipeline::Schedule
  Parallel` tests `!sink->ParallelSink()` FIRST and returns false, falling back to `ScheduleSequentialTask`
  (ONE task) — so `MaxThreads()` is not merely capped there, it is **never read**. The md5 and `GROUP BY`
  numbers above were both taken with an AGGREGATE above the scan, whose sink is parallel. For a plan that
  streams straight to the client the sink IS the result collector, and that one was non-parallel by default:
  MEASURED at 2694 / 2633 ms for threads 1 / 4 — perfectly flat. §5 is why, and it is fixed.

---

## 3. The morsel is the exported batch — and enlarging it is DEFAULTED OFF, because a batch is also a FILE

With `MaxThreads() > 1` the exported `RecordBatch` is the unit of parallel work, so its SIZE matters.
**MEASURED it was `STANDARD_VECTOR_SIZE`**: a 6M-row scan produced **2929 batches of exactly 2048 rows plus a
1408 tail**, against **49 row groups of 122880** for the same data read natively — 60x more morsels, each
paying a mutex acquisition, an Arrow import and converter setup. (The 49 × 122880 was confirmed to be that
file's own layout, so the 60x is the artifact's ratio and not a quoted constant that happened not to apply.)

`HostQueryGetNext` in [src/fabricator_host_query.cpp](../src/fabricator_host_query.cpp) can accumulate
`DataChunk`s into one batch, and **the default is one chunk** (`FABRICATOR_HOST_QUERY_BATCH_ROWS` unset).

> **⚠⚠ THE REASON IT IS OFF IS NOT PERFORMANCE — A BATCH IS ALSO A FILE.** This stream feeds WRITERS as well
> as scans, and the clustered rewrite cuts output files with `BudgetedStream`
> ([dotnet/Fabricator.Delta/DeltaReader.cs](../dotnet/Fabricator.Delta/DeltaReader.cs)). Raising the batch
> target can therefore only COARSEN physical layout, never refine it. MEASURED: at 122880,
> `verify_delta_clustered_optimize` FAILED after 70 assertions — an OPTIMIZE collapsed 80000 rows into ONE
> file, i.e. `delta.targetFileSize` stopped being enforceable; at one chunk, 147 passed.

One of the two couplings behind that has since been removed and one remains, which is why the default has not
moved:

- **REMOVED:** `BudgetedStream` used to emit its first batch unconditionally and then stop at
  `_rows >= _budget`, so it could only cut BETWEEN batches and the batch size was a hard FLOOR on file size.
  It now SPLITS the boundary batch (`ArrowCompute.Take` — one copy, of that batch only, type-agnostic including
  nested and extension types, which is the property that matters, because a VARIANT column must survive it).
  ⚠ **One case of that split is QUADRATIC**, and it is a second reason not to simply raise the batch size: a
  batch several times the budget is re-split once per output file, and each split copies the whole remaining
  tail (`Take` gathers; Arrow has no type-agnostic zero-copy slice). Unreachable at the shipped default — one
  `DataChunk` per batch means at most one split each — and bounded even when reached, but it bites exactly the
  combination of a large batch and a small `delta.targetFileSize`.
- **REMAINS:** engineered-wood's `WriteDataFilesAsync` writes **one parquet file per input batch**, so on the
  codec engine the batch size still sets file size. Decide whether that coupling is harmful — it may be what
  `targetFileSize` wants — before flipping the default.

⇒ the target must be scoped to the CONSUMER (a scan wants big morsels, a writer wants its own file
granularity), which `HostQueryGetNext` cannot see. That needs a parameter on the `host_query` ABI, so the win
is recorded and deferred. **The env var is an EXPERIMENT HOOK, not a knob**, and it is the hook that produced
every number in §4.

**⚠ The accumulated path is gated by exactly ONE suite leg**, the `ACCUMULATED` field in
[scripts/run-suites.sh](../scripts/run-suites.sh): `verify_delta_clustered_optimize` runs a SECOND time at an
accumulated batch size. Both legs must pass, and **the default leg is the control** — "the accumulated leg
passes" would be equally true of a build where accumulation had stopped working. The `unset` for every other
suite is load-bearing in the other direction: a value left in a developer's shell would accumulate everywhere,
and the run would then not be testing the shipped default anywhere.

---

## 4. The two levers OVERLAP rather than add

6M rows, CPU-bound `GROUP BY`, median of 3:

| batch | threads | |
|---|---|---|
| one chunk | 1 | 0.864 s |
| 32768 | 1 | 0.689 s — batching alone |
| one chunk | 8 | 0.640 s — threads alone |
| 32768 | 8 | **0.592 s** — both |

They attack the SAME per-batch cost (a mutex acquisition, an Arrow import, converter setup), so the gains do
not compose. Bigger batches also run far more **PREDICTABLY**: a 1.7% spread across repetitions against 10% at
one chunk, there being much less lock contention to jitter.

**⚠ The first sweep of this reported a 4x speedup that was every query FAILING** (~0.2 s per cell — the best
number of the day — each dying with *"Attempting to execute an unsuccessful or closed pending query result"*).
A `Fetch()` returning null also CLOSES the streaming result, so consuming that EOF mid-batch made the NEXT
`get_next` throw — unreachable in one-chunk mode, where the EOF is the value `get_next` returns. Correctness is
now asserted per cell rather than inferred from the timing, which is the general rule: **a performance number
from a run whose answers were not checked is not a performance number.**

---

## 5. ROOT CAUSE: our scans declared no batch index, so the default result collector was single-threaded

**This section used to describe an unattributed "DuckDB scheduling pathology". It is attributed now, the cause
was OURS, and it is fixed.** The symptom was spectacular: on a remote root, a STREAMING result x
`PhysicalUnion` x `threads>1` x a slow filesystem stalled. Bisected through `fabricator_host_query` (our
identical inner-query machinery, nothing of ours above it), a 198-file plain branch alone ran in **7.0 s**, and
adding ONE CONSTANT branch — `UNION ALL SELECT CAST(0 AS BIGINT)`, no CTE, no second file — took it to
**111.2 s**; the same at `SET threads=1` collapsed to **6.0 s**; and `EXPLAIN ANALYZE` of the slow shape
reported **3.38 s of operator work inside a 114 s statement**.

### The chain, every link read from DuckDB's source

1. **`PhysicalTableScan::SupportsPartitioning` is literally `function.get_partition_data != nullptr`**
   ([src/execution/operator/scan/physical_table_scan.cpp](../duckdb/src/execution/operator/scan/physical_table_scan.cpp)).
   Setting that callback IS how a source declares batch-index support; there is no other switch.
2. **No fabricator `TableFunction` set it.** DuckDB's own arrow scan does
   (`arrow.get_partition_data = ArrowGetPartitionData`), and the body it points at is three lines reading
   `state.batch_index` off an `ArrowScanLocalState` — **our local state's base class**, whose `batch_index` our
   `GetNextBatch` has always assigned under the pull mutex. The value was there; the declaration was not.
3. `AllSourcesSupportBatchIndex()` false ⇒ **`PhysicalPlanGenerator::UseBatchIndex` false**.
4. So `PhysicalResultCollector::GetResultCollector` fell to its middle branch: order-preserving but no batch
   index ⇒ **`PhysicalBufferedCollector(parallel = false)`** — a SINGLE-THREADED sink — instead of
   `PhysicalBufferedBatchCollector`, which is order-preserving AND `ParallelSink() == true`.
5. **`Pipeline::ScheduleParallel` tests `!sink->ParallelSink()` FIRST and returns false**, falling back to
   `ScheduleSequentialTask`: one `PipelineTask`. `MaxThreads()` is never even reached (§2).
6. **`PhysicalUnion::BuildPipelines` also sets `order_matters = true` when `!sink->ParallelSink()`**, so the
   branch pipelines are created dependency-chained rather than free-running. That is why a union MULTIPLIES it:
   one serialized pipeline becomes N serialized, chained pipelines.
7. Each hand-off is then paid at **20 ms granularity**. `Executor::WaitForTask`
   ([src/parallel/executor.cpp](../duckdb/src/parallel/executor.cpp)) waits on `task_reschedule`, a condition
   variable signalled by task RESCHEDULES and **never by a chunk arriving in the buffer**; its escape hatch,
   `ResultCollectorIsBlocked()`, short-circuits on `completed_pipelines + 1 != total_pipelines`, which a union
   keeps false for most of the statement. So the union takes the timed wait where a single-source plan takes the
   immediate return — which is also why the same stall was observed as spin (102 s user CPU) in one run and as
   idle (4.5 s user) in another: two faces of one protocol. `SET streaming_buffer_size='64MB'` changed nothing,
   which is what ruled out back-pressure as the driver.

### THE FIX: declare `get_partition_data` (2026-08-21)

`ArrowStreamGetPartitionData` in [src/fabricator/arrow_ingest.cpp](../src/fabricator/arrow_ingest.cpp) returns
`OperatorPartitionData(state.batch_index)`, wired into all nine `ArrowStreamScan` registrations (the catalog
scan, catalog-bound + global table functions, `fabricator_query`, `_functions`, `_server_info`, `_test_scan`,
`fabricator_host_query`, `fabricator_scan`). It mirrors `ArrowTableFunction::ArrowGetPartitionData` and refuses
partition COLUMNS the same way. Sound because the index is assigned in PULL order under the mutex, so ordering
by it reproduces the order the provider produced — which is exactly what the batch collector uses it for.

**⚠ Two safety questions it would be easy to skip, both checked in DuckDB's source rather than assumed.**
(a) *Can two branches of a union, each counting from 0, collide?* No — `PipelineExecutor::NextBatch` emits
`pipeline.base_batch_index + batch_index + 1`, and `MetaPipeline` hands every pipeline a distinct base
(`next_batch_index++ * BATCH_INCREMENT`), so a source's own counter is a pipeline-relative offset by
construction. That is also why it does not matter that DuckDB's arrow scan pre-increments from 1 where we
post-increment from 0. (b) *Can a long scan overflow into the next pipeline's range?* `BATCH_INCREMENT` is
10^13, so at 2048 rows per batch the overflow point is ~2 x 10^16 rows — and it THROWS
(`"invalid batch index ... returned by source operator"`) rather than silently reordering.

**MEASURED** (§6's instrument; 8192-row Delta table, one 500 ms sleep per 2048-row morsel, one process per
cell, `SET threads` the only variable):

| plan | threads=1 | threads=4 BEFORE | threads=4 AFTER |
|---|---|---|---|
| `sum(...)` — aggregate sink (parallel already) | 2702 ms | 1177 ms | 1140 ms |
| `SELECT plug_sleep(...)` — **streaming to client** | 2729 ms | **2633 ms (flat)** | **1197 ms** |
| `UNION ALL` of two scans, streaming | 5133 ms | — | 1859 ms (1858 at threads=8) — ⚠ see §5a, this is NOT inter-branch |

### ⚠⚠ §5a. WHAT THE FIX DOES NOT DO: A UNION'S BRANCHES ARE STILL SERIAL, AND THE ROW ABOVE MISLED ME

**Found by the fabricator-quantax plugin session, which repinned to this fix, re-measured, and reported
"partly fixed, not fully". They were right.** It reproduces on our OWN sample plugin, so it needs no plugin of
theirs — and the fix's own numbers could not have caught it, because every union cell I measured had the cost
in a per-row SCALAR, which parallelizes WITHIN a branch:

| shape (`plug_slow_range(4096, 500)` = 2 batches x 500 ms) | threads=4 | threads=8 |
|---|---|---|
| ONE branch | 1420 ms | 1416 ms |
| `UNION ALL` with itself | **2419 ms** | **2421 ms** |
| the same + `preserve_insertion_order=false` | 2486 ms | 2460 ms |

Perfectly additive: branch B does not start until branch A is exhausted, at any thread count, with or without
the setting. Their instrumentation shows the pull IS handed around inside a branch (five batches on five
different threads) and branch B still waits.

**So the 1859 ms in the table above is INTRA-branch parallelism, not inter-branch.** Each of my two branches
had four sleeping rows spread over four threads (2.0 s -> 0.5 s each) and the two branches then ran
back-to-back — 0.5 + 0.5 + baseline. I read a real 2.8x as "the union case is fixed" and it was not; a
source-side cost, which cannot parallelize within a branch, exposes it immediately.

**THE MECHANISM, and it is a direct consequence of the fix rather than something it missed.**
`PhysicalUnion::BuildPipelines` sets `order_matters` — which makes each union pipeline DEPEND on the previous
one (`MetaPipeline::CreateUnionPipeline` pushes `current` onto `pipeline_dependencies`) — if ANY of five things
hold, and **every result collector DuckDB has triggers one of them**:

| collector | how it sets `order_matters` |
|---|---|
| `PhysicalBufferedCollector` (either `parallel` value) | `SinkOrderDependent()` returns **true unconditionally** |
| `PhysicalMaterializedCollector` | same — **true unconditionally** |
| `PhysicalBufferedBatchCollector` (what this fix selects) | does not override `SinkOrderDependent`, but its `RequiredPartitionInfo()` **is** `BatchIndex()`, which is its own clause |

⇒ the fix swapped the `!sink->ParallelSink()` trigger for the `partition_info.batch_index` trigger. Net effect
on inter-branch concurrency: **zero, by construction.** And it is not the binder's doing —
`plan_setop.cpp` constructs `LogicalSetOperation` without the argument, so `allow_out_of_order` DEFAULTS TO
TRUE for an ordinary `UNION ALL`. The plan says order need not be preserved and the sink overrides it.

**UPSTREAM CANDIDATE, and the sharpest one this area has produced.** `PhysicalBufferedCollector::ParallelSink()`
returns its `parallel` flag while `SinkOrderDependent()` returns `true` REGARDLESS — so under
`preserve_insertion_order=false`, where the collector was chosen precisely because order does not matter, it
still declares itself order-dependent and still serializes the union. `SinkOrderDependent()` following
`parallel` (and the same for the materialized collector) would make `order_matters` false there and let the
branches run concurrently. ⚠ NOT verified: it needs a patched `duckdb_static` and a rebuild, and the
prediction ("the 2486 ms row collapses toward 1420") is exactly the kind this file requires to be measured
before being believed.

**⚠ Practical consequence for us, until that is settled: our remote union form gets NO inter-branch
concurrency**, so `TryUnionForm`'s cost model must keep assuming its D deletion-vector branches run
back-to-back. That is one more reason its remote gate stays where it is (§10).

**⚠ AND IT MAKES OUR PER-STATEMENT `preserve_insertion_order=false` REDUNDANT ON THIS SHAPE** — the union at
threads=8 is **1858 ms** by default against **1828 ms** with the SET, i.e. within noise. That setting was the
symptom treatment: it reached a parallel collector by DISCARDING the order guarantee (route 1,
`parallel = true`), where declaring the batch index reaches a parallel collector that KEEPS it (route 3). It is
still applied by `BatchPlan.Statement`
([dotnet/Fabricator.Delta/DeltaNativeReader.cs](../dotnet/Fabricator.Delta/DeltaNativeReader.cs)) and has NOT
been removed — see §10 item 1 for what removing it would need.

**⚠ THE ROUTING ITSELF IS NOT GATED, and the reason is worth stating rather than implying coverage.** The
change moves no row and alters no answer, so no row assertion can see it; and nothing prints the collector —
`EXPLAIN` does not, and `EXPLAIN ANALYZE` reports only `Total Time`, checked. The only observable is WALL
CLOCK, and proving parallelism needs an UPPER bound on time, which is precisely the flaky direction (§6's
assertions are lower bounds for that reason). So what stands behind it is: both tiers at IDENTICAL counts —
hermetic **72/72 — 7719**, including every plan-sensitive suite at its exact prior number (batched-read routing
399, subplan dedup 36, statistics 27, `merge_into` 239, clustered optimize 147) — plus the measurements above
and the source chain. A regression here would be silent and slow, not wrong.

**⚠ WHAT IS NOT YET MEASURED: the remote numbers.** Everything above is local, where the stall never opened on
its own (0.17 s) and the sleep is a stand-in for slow IO. The live OneLake figures in this section
(120.4 / 44.7 / 13.7 s, and the union form's remote gate in `TryUnionForm`) all predate the fix and must be
re-taken before that gate is narrowed or lifted. The mechanism is established; the remote payoff is inferred.

**⚠ The upstream observation survives the fix and is worth reporting**: nothing signals the consumer when a
chunk lands in the buffer, so `SimpleBufferedData::Append` (or the collector's sink) should notify
`task_reschedule`. Any source that cannot supply a batch index still pays what we were paying. Not filed.

## 6. Measuring it: `plug_sleep`, and the traps in measuring at all

The sample plugin ships **`plug_sleep(millis)`**
([dotnet/Fabricator.SamplePlugin/SamplePlugin.cs](../dotnet/Fabricator.SamplePlugin/SamplePlugin.cs)), a global
scalar that blocks its worker thread once PER ROW and returns what it slept. It is an INSTRUMENT, not a feature:
it makes a query's cost a number the caller chose, so **effective parallelism becomes arithmetic rather than an
inference**.

The shape that works is **one sleep per MORSEL**, not one per row — a fabricator scan hands out one
`DataChunk` per batch (§3), so making every 2048th row sleep puts exactly one sleep in each morsel and the
wall clock reads off how many ran at once:

```sql
ATTACH '<dir>' AS lk (TYPE fabricator, PROVIDER 'delta');
CREATE TABLE lk.main.t AS SELECT i::BIGINT AS id FROM range(8192) r(i);   -- 4 morsels
SELECT sum(plug_sleep(CASE WHEN id % 2048 = 0 THEN 500 ELSE 0 END)) FROM lk.main.t;
```

**MEASURED** (2026-08-21, two repetitions, one `duckdb.exe` process per row of the table so `SET threads` is
the only variable; the baseline is the identical query with every sleep at 0, which isolates process start +
ATTACH + the Delta open):

| threads | wall | − baseline | sleep term |
|---|---|---|---|
| 1 | 2650 / 2678 ms | 589 ms | **~2060 ms** — the 4 × 500 ms, serial |
| 2 | 1602 ms | | ~1010 ms |
| 4 | 1137 / 1102 ms | 599 ms | **~505 ms** — 2000/4, i.e. all four morsels slept at once |
| 8 | 1256 ms | | ~660 ms — no better, and there are only 4 sleeping rows to spread |

**⚠ THE CONTROL IS THE LOAD-BEARING HALF, and it also kills the first version of this example.** The BYTE-
IDENTICAL expression over `range(8192)` instead of the table shows **NO scaling whatsoever** (2408 / 2366 /
2381 ms at threads 1 / 2 / 4): `range` is a single-threaded source, so the parallelism above is attributable
to the fabricator scan declaring threads and to nothing else. This doc first carried
`SELECT sum(plug_sleep(50)) FROM range(80)` with an arithmetic prediction of "~0.5 s at threads=8" — MEASURED
it is 5306 ms at threads=1 and 5366 ms at threads=8, because 80 rows are ONE chunk and `range` would not have
split them anyway. **A parallelism example needs a source that parallelizes and more than one morsel to hand
out; assert both by measuring the serial leg, never by counting rows.**

Why a sleep beats the md5-per-row probe the §2 numbers were first taken with: its cost does not vary with
input, machine or build. Gate: [test/verify_plugin.test](../test/verify_plugin.test) (38, service tier).

### The SOURCE-side twin, and why the pair is the point

`plug_slow_range(rows, millis)` is a global TABLE function that yields `rows` rows in 2048-row batches, sleeping
`millis` before each batch — so the cost sits INSIDE `get_next`, on the other side of the scan boundary. The two
together turn §1's claim into a measurement:

| where the 2 s of cost sits | threads=1 | threads=4 | |
|---|---|---|---|
| in the SOURCE (`plug_slow_range(8192, 500)`) | 2451 ms | 2454 ms | **flat — the pull is serialized** |
| in the PROJECTION over that same source | 2425 ms | 883 ms | scales |

The first row is the invariant, not a defect: one mutex around `get_next` is what lets a provider's reader be
touched from one thread only (§2). The second row is what proves the §5 fix reaches a global TABLE-FUNCTION
scan, not merely the catalog table scan — both go through `ArrowStreamScan`, and now both declare the batch
index.

- **⚠ `sum()`, never `count(*)`.** DuckDB PRUNES a projected column no aggregate consumes, so `count(*)` over
  `plug_sleep(...)` evaluates it **zero** times — the measurement reads as instant parallelism, and the
  instrument was never called at all. This exact trap has been hit three separate times in this codebase on
  other paths (a `count(*)` answered from parquet footers is not a read; a projected-but-unused rowid is pruned
  and silently re-routes the scan form).
- **⚠ It must stay VOLATILE** (the `IScalarFunction.IsVolatile` default). A CONSISTENT scalar over a constant
  argument is FOLDED to a literal at plan time, so it would sleep once during binding and never again — and
  every number built on it would be confidently wrong while looking healthy.
- **⚠ It is not interruptible and takes no cap, deliberately.** A plugin references only the contract assembly,
  which exposes no interrupt surface, which makes it the reproducible test case for
  [cancellation.md](cancellation.md) — a query parked inside one long-blocking managed call is that doc's whole
  subject. A negative argument is REFUSED rather than clamped, because `Thread.Sleep(-1)` is `Timeout.Infinite`
  and a hanging suite is the one failure mode worse than a failing one.
- **⚠ ON WINDOWS THE FLOOR IS THE SYSTEM TIMER TICK, ~15 ms — MEASURED, and it makes small arguments a lie.**
  100 rows at `plug_sleep(1)` cost 1913 ms against a 343 ms baseline, i.e. **~15 ms per row**: `Thread.Sleep`
  rounds up to the scheduler's resolution (~15.6 ms by default), so a request for 1 ms costs 15x what it says.
  Use values comfortably above the tick — 500 ms above is what the table uses — and never build a per-row
  budget out of single-digit milliseconds. It is also why per-ROW sleeping is impractical as a scan probe at
  all: one morsel is 2048 rows, so even 1 ms per row is ~31 s per morsel.
- **⚠ Timing claims need the same discipline as everywhere else here.** Measure INSIDE the command
  (`S=$(date +%s); …; echo $(( $(date +%s) - S ))`): progress read behind a BACKGROUNDED `sleep` samples the
  same instant, which is how a suite that takes 196 s came to be believed to take 70 minutes and stall
  ([filesystem-bridge.md](filesystem-bridge.md)). And a local A/B cannot demonstrate a remote win — 56 saved
  round trips are free at localhost latency and ~10 s at the ~180 ms per request measured against OneLake.

---

## 7. Determinism under parallelism

**Row ORDER is not preserved and never was** — DuckDB guarantees none, re-applies its own `ORDER BY`/TopN above
the scan, and §5's setting only makes that explicit.

What must NOT move with the thread count is the **transient rowid**, because the whole rowid DML path rests on
it: `rowid = (file_ord << 40) | file_row_number`. Neither half depends on emission order — `file_ord` is per
FILE and arrives by a JOIN on `filename` (never zipped positionally), and `file_row_number` is the row's
physical position inside its own parquet file, which DuckDB derives from the footer's row-group offsets.

MEASURED and pinned by [test/verify_delta_batched_read.test](../test/verify_delta_batched_read.test) §8: two
files × 10 row groups × 40000 rows, the `id → rowid` PAIR checksum identical at threads 1, 8 and 4, plus a
threaded UPDATE hitting exactly its predicate's rows. The pair matters — counting distinct rowids alone would
pass with every one attached to the wrong row.

**⚠ Do NOT pin a literal checksum there.** The rowid is TRANSIENT by design: `file_ord` follows listing order
over UUID-named files, so two creations of the same logical table legitimately differ (measured — two distinct
checksums across three runs). The invariant is thread-count invariance WITHIN one database.

---

## 8. The ambient invariant: a PULL carries no ambients

`ArrowStreamInitGlobal` establishes the managed ambients — `set_active_txn` and `set_active_opener` — and their
comments say exactly why that is sufficient: *"the factory's scan_table runs synchronously on this thread, so
the managed per-thread ambient set here governs which connection it borrows."* The ambients cover **stream
CREATION**.

**`GetNextBatch` establishes nothing.** So a pull runs with whatever thread-local state the DuckDB worker
thread happens to have, which is none — and since `MaxThreads() > 1`, those pulls arrive from arbitrary workers.
The rule that follows:

> **A managed stream must never read an ambient from `get_next`.** Capture it when the stream is created (in
> the crossing that set it) and re-establish it inside the pump, which is what `DeltaGlobalTableFunction` and
> `BulkSession` already do with a comment saying why. The generalisation, from two bugs: **capture-and-
> re-establish for VALUES; resolve-per-use for POINTERS** — a raw `ClientContext *` whose owner may die must
> never be held by a long-lived object, which is the `table_stats` SIGSEGV class.

The related failure with the sharpest teeth is a **bound Arrow input** whose columns the generated SQL does not
all read: `duckdb_arrow_scan` plus a projection that is not a PREFIX of the bound stream's columns SEGFAULTS,
or nondeterministically corrupts a string length and INVALIDATES the database — reproduced on a stock DuckDB
with no extensions, [duckdb-upstream-issues.md](duckdb-upstream-issues.md) §2. **Add a bound column only
together with the SQL that reads it**; that invariant is why the partition-only and union forms emit their
per-file ordinal *only* when the rowid expression consumes it.

---

## 9. What this doc does not cover

- Connection/transaction concurrency, MARS, read-your-writes, `dbt --threads N`:
  [transactions.md](transactions.md), [transaction-concurrency.md](transaction-concurrency.md).
- Multi-writer COMMIT safety (put-if-absent per backend, lost commits on an unguarded root):
  [delta-transactions.md](delta-transactions.md) §8, [known-limitations.md](known-limitations.md).
- Which of the four batched read forms a scan takes, and their remote gates:
  [delta-catalog.md](delta-catalog.md) and the `BatchPlan` remarks in
  [dotnet/Fabricator.Delta/DeltaNativeReader.cs](../dotnet/Fabricator.Delta/DeltaNativeReader.cs).
- Per-IO instrumentation of the storage backends: [filesystem-bridge.md](filesystem-bridge.md).
- Cancelling work already in flight: [cancellation.md](cancellation.md).

## 10. Open, and unmeasured

1. **Can `preserve_insertion_order=false` be dropped now?** §5 measured it redundant on the LOCAL union repro
   (1858 vs 1828 ms), and removing it would give batched Delta statements their insertion order back. It must
   not be removed on that alone: the setting was justified by REMOTE numbers taken before the fix, and the
   union form's remote gate in `TryUnionForm` rests on the same pre-fix measurements. Re-measure live, then
   decide both together.
2. **The remote figures in §5 all predate the fix** — 120.4 / 44.7 / 13.7 s, and the "union loses cold by
   ~3.5x" ranking. Nothing about them is known to still hold.
3. **The batch-size default** (§3). Removing engineered-wood's one-file-per-batch coupling, or adding a
   consumer parameter to the `host_query` ABI, would let a scan take the measured ~20% while a writer keeps its
   own granularity. Nothing is built.
4. **`FABRICATOR_DELTA_PREFETCH` stays at 1.** The per-file loop is sequential by default, so a remote scan
   that falls to it pays N round trips in series. Raising it has never been measured against the current read
   path, and it is what made a fixed-name bound-input view collide (now per-query names).
5. **The `GROUP BY` regression of §2 is unexplained beyond "merge-bound".** If it ever matters, the lever is
   DuckDB's aggregate, not our scan.
