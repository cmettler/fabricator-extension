# Scan concurrency — how a fabricator scan uses cores, and every measured trap in doing so

> **Status: CURRENT (2026-08-21). Describes shipped behaviour as of `0.0.11`.**
>
> **WHY THIS DOC EXISTS.** The parallel scan (`38189db`) and the batch-size work (`e9f4b13`) were argued in
> commit messages and code comments, and everything measured since — the streaming × `PhysicalUnion` stall
> and the blocking-pull starvation it turned out to be (§5c, fixed in §5f),
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
                 try_lock FAILED         -> return BLOCKED + one bounded AsyncTask wait   (since §5f)
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

**⚠ And until 2026-08-22 none of §2–§5f reached a WRITE at all**, because a serial sink is tested before the
source (§7a): every INSERT / CTAS / COPY into a fabricator table ran the whole pipeline on one task, so
`MaxThreads()` was never read there. §7c is that fix; §7a is the measurement that found it.

**⚠ What the serialized pull must NOT do is make the OTHER threads park on it, and until 2026-08-21 it did**
— which is how one scan came to occupy every DuckDB worker for the length of a blocking remote read and starve
every sibling pipeline in the plan. The pull is still serialized; the losers now hand their workers back
(§5c for the measurement, §5f for the fix).

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

**⚠ THE HEADING IS HISTORY AS OF §5f (2026-08-21): a union's branches DO overlap now.** What §5a–§5c
established — that the `get_partition_data` fix did not touch inter-branch concurrency, and why — is unchanged
and is what led to the cause; the state of the world it describes is not. `plug_slow_range ∪ plug_slow_range2`
is 1.03 s where this section measures 2419 ms.

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

**⚠ NO SINK IS EXEMPT, INCLUDING A CTAS — and checking that caught me making the SAME mistake twice within
the hour.** Asked what `CREATE TABLE … AS SELECT … UNION ALL …` does, I first measured it with the per-row
SCALAR and reported DuckDB's own storage sink scaling 2.5x (4834 -> 1928 ms). Re-measured with the SOURCE-side
cost, which is the instrument that answers this question:

| cost in the SOURCE, `UNION ALL` of two `plug_slow_range(4096,500)` | threads=1 | threads=4 |
|---|---|---|
| CTAS -> DuckDB's own storage | 2467 ms | **2503 ms — flat** |
| the same + `preserve_insertion_order=false` | — | 2419 ms |
| CTAS -> a fabricator table | 2740 ms | 2751 ms |

DuckDB's `PhysicalBatchInsert` declares `ParallelSink() == true`, so it is not the `!ParallelSink()` clause that
chains it — it is the same `RequiredPartitionInfo() == BatchIndex()` clause the batch COLLECTOR trips. ⇒ every
sink in DuckDB that either preserves order or wants batch indices serializes union branches, and that is the
whole set.

**⚠⚠ THE ERROR PATTERN, named because it recurred immediately after being corrected: a per-row SCALAR measures
INTRA-branch parallelism, and I twice read its result as a statement about INTER-branch concurrency.** The two
instruments answer different questions and neither substitutes for the other — see §6, which now says which is
which.

### ⚠⚠ §5b. THE `order_matters` EXPLANATION ABOVE IS REFUTED FOR OUR CASE — a pure-C++ control overlaps, and what serializes is TWO CALLS OF THE SAME FUNCTION

`fabricator_wait(rows, millis)` ([src/fabricator_wait.cpp](../src/fabricator_wait.cpp)) was added for exactly
this: a plain DuckDB table function with **no Arrow, no bridge, no plugin and no pull mutex of ours**, so a
scheduling answer cannot be blamed on our own machinery. Its sleep is deliberately OUTSIDE its claim lock —
holding it across the sleep would reproduce the serialization the control exists to rule out.

**Validity first** (a control has to be shown able to produce the positive result): 4 chunks x 500 ms scales
2434 -> 1351 -> 869 ms at threads 1/2/4. Then, one chunk per branch so ONLY inter-branch concurrency can help,
2000 ms of work, threads=4, and **distinct arguments in every pair so the common-subplan optimizer cannot
dedup** (confirmed by `EXPLAIN`: two separate scans, no CTE):

| union of two branches | result |
|---|---|
| C++ `fabricator_wait` ∪ C++ `fabricator_wait` | **1352–1388 ms — OVERLAPPED** |
| C++ `fabricator_wait` ∪ managed `plug_slow_range` | **1412 ms — OVERLAPPED** |
| managed scan ∪ managed scan (same function) | 2379 ms — serial |
| managed SCALAR ∪ managed SCALAR (same function, over C++ sources) | 2382 ms — serial |
| managed SCALAR ∪ managed SCAN (two DIFFERENT functions) | **1380 ms — OVERLAPPED** |

**So `order_matters` is NOT the explanation for the shape we actually hit.** Every cell above uses a `count(*)`
sink, where `SinkOrderDependent()` is false and `RequiredPartitionInfo()` is empty — `order_matters` is FALSE
and the branches are free to overlap. The C++ ones do. What does not overlap is **two calls of the SAME
function**, and two calls of two DIFFERENT functions overlap fine. §5a's mechanism is real DuckDB behaviour for
a streaming/collector sink and it is NOT what produced the measurements either of us took.

**⚠ Also ruled out, each by measurement rather than by reading:** the declared thread count (`threads := 1 / 2 /
4 / 20` on the control, all 1350–1381 ms — hence that named parameter exists); `CanSaturateThreads` (a
2048-row branch has `EstimatedThreadCount() == 1` and a bare scan `ContainsSink() == false`); common-subplan
dedup (distinct args + `EXPLAIN`); and thread starvation — a stderr trace of the managed sleep shows branch B
beginning **4 ms after A ends, on a DIFFERENT managed thread**, which is a wait, not a shortage.

### §5c. MECHANISM ESTABLISHED: our pull mutex is held ACROSS the blocking pull while we declare a thread per core

**Two intermediate readings were wrong and are recorded because each was refuted by the next measurement, not
by argument.** First "`order_matters` chains the branches" (refuted: every cell uses a `count(*)` sink where
`order_matters` is false, and the C++ control overlaps). Then "two calls of the SAME function serialize"
(refuted the moment a SECOND managed table function existed: `plug_slow_range ∪ plug_slow_range2` is 2382 ms,
just as serial as the same-function pair). ⚠ That second wrong turn came from substituting a managed SCALAR
for the second table function, which is the §6 instrument error again — a scalar is expression evaluation, not
a scan, so a scalar/scan pair overlapping said nothing about two scans.

**What the trace showed.** Both branches BIND up front; branch B's `Execute` body is not entered until 2 ms
after branch A's sleep ENDS. So nothing in managed code is waiting — the host never asks B for a batch while A
is blocked.

**The mechanism, then reproduced in pure C++ with both halves shown NECESSARY.** `GetNextBatch` holds
`gstate.main_mutex` across the managed pull (correct in itself: one Arrow stream cannot be pulled from two
threads) while `ArrowStreamInitGlobal` declares `MaxThreads() = NumberOfThreads()`. Branch A therefore launches
one task per thread: one blocks INSIDE the pull holding the mutex, and the others block ON that mutex — each
burning a DuckDB worker. Every worker is consumed by branch A, so branch B's task never gets one. Inverting the
control (`hold_lock`, plus the `threads` override) reproduces it exactly, threads=4, 2000 ms of work:

| control configuration | result |
|---|---|
| lock released before the sleep (the control) | 1386 ms — overlapped |
| `hold_lock := true, threads := 1` | **1338 ms — overlapped** |
| `hold_lock := true, threads := 4` | **2417 ms — SERIAL** |
| `hold_lock := true, threads := 20` | 2356 ms — SERIAL |
| `hold_lock := false, threads := 20` | 1327 ms — overlapped |

**Both conditions are required. Either alone overlaps.**

**⚠⚠ AND THAT MAKES IT A SIDE EFFECT OF §2's OWN FIX.** Before 2026-08-18 `MaxThreads()` returned 1, which is
the `threads := 1` row — one task per branch, nothing to pile up, branches overlap. So the change that made a
single scan's per-thread work parallel simultaneously made sibling tasks starve every other pipeline in the
plan. §2's gate could not have caught it: both tiers were byte-identical, because a sqllogictest suite never
runs two expensive scans concurrently.

**⚠ `MaxThreads() = 1` DOES FIX THE UNION, AND IT IS THE WRONG FIX — MEASURED BOTH SIDES (2026-08-21,
user-raised).** Patched temporarily and reverted; threads=4 throughout:

| shape | MaxThreads = NumberOfThreads() (shipped) | MaxThreads = 1 |
|---|---|---|
| two managed scans unioned, ONE blocking morsel each | 2382 ms | **1403 ms** — fixed |
| one Delta scan, 4 morsels of per-row work | **1178 ms** | 2693 ms — flat, §2's win gone |
| two Delta scans unioned, 4 morsels each | **1859 ms** | 2809 ms — WORSE |

So it is not "a fix with a cost", it is a **shape-dependent trade with no winner**: a constant of 1 wins only
when each branch holds a SINGLE blocking morsel, i.e. when there is nothing to spread within a branch anyway,
and loses whenever there is per-row work above the scan — including on the union shape it was supposed to help
(row 3). **Which of the two our remote reads resemble is not settled**: their cost is the blocking pull, which
the mutex serializes at either setting, so intra-branch parallelism has little to give and inter-branch overlap
would put D deletion-vector branches' IO in flight together — but that is REASONING, and the remote
measurement has not been taken.

⇒ neither constant is right, which is the argument for the real fix below rather than a knob.

### §5d. THE FIX DIRECTION, made concrete: `TableFunctionInput::async_result` + a single-pump channel

**⚠⚠ SUPERSEDED BY §5f — BUILT, AND NOT THIS WAY. Read §5f first.** The mechanism named here is
right and is what shipped; the ARCHITECTURE is not. Its central claim — that a blocked branch would afterwards
hold ONE worker — is FALSE (an `AsyncTask` that waits until data arrives holds a worker for exactly as long as
parking on the mutex did), and with that corrected the managed pump and the two new ABI entries below turn out
to buy nothing the fix needed. Kept verbatim because the reasoning is where the error is visible.

A source that cannot make progress must hand its worker BACK rather than block on a mutex. Ours has no such
path — it returns rows or end-of-stream — and the two shortcuts are both wrong: `try_lock` + zero rows is a
WRONG ANSWER (an empty chunk means end-of-scan) and `MaxThreads() = 1` is priced above. ⚠ DuckDB's own arrow
scan has the same mutex-around-the-pull shape and does not suffer this, because its `GetNextChunk` is an
in-memory read that never blocks for a second — **the BLOCKING pull is ours, so the fix is ours.**

**IT IS NOT POLLING, and a TABLE FUNCTION CAN EXPRESS IT.** `InterruptState::Callback()` is a completion
callback ("perform the callback to indicate the Interrupt is over"), so a blocked task is descheduled — worker
released — and rescheduled explicitly. The route for a table function is `TableFunctionInput::async_result`
(1.5.5): set it to `AsyncResult(vector<unique_ptr<AsyncTask>>)`, leave the chunk EMPTY, and
`PhysicalTableScan::GetDataInternal` calls `ScheduleTasks(input.interrupt_state, context.pipeline->executor)`
and returns `SourceResultType::BLOCKED`. When `AsyncTask::Execute()` finishes, the interrupt fires and the scan
is called again.

**THE SHAPE (user-proposed, and it is better than a lock change because it deletes the lock).** ONE pump task
per scan owns the Arrow stream and writes batches into a bounded channel; the scan callback `TryRead`s. Since
only the pump ever touches the stream, **`gstate.main_mutex` disappears** — and that mutex being held across
the pull is the whole defect. N scan tasks are then free to emit concurrently, and a scan that finds the channel
empty returns BLOCKED with an `AsyncTask` that waits for readability. Channel capacity ≈ thread count is a
sensible default and doubles as the backpressure knob (capacity x batch size is what it retains).
⚠ The producer still needs a `Task.Run` pump because our pull is a blocking sync-over-async call; what moves
into the `AsyncTask` is the WAITING, executed by DuckDB's executor rather than by a worker we are holding.

**⚠ `Execute()` MUST BLOCK, and the thread accounting is the whole reason this fixes anything.** The interrupt
fires only AFTER it returns — `AsyncExecutionTask::ExecuteTask` is `async_task->Execute(); if
(counter->IterateAndCheckCounter()) interrupt_state.Callback();` — so "start the IO and return" would reschedule
the scan into an empty channel and it would return BLOCKED again, i.e. a spin. And the task goes through
`TaskScheduler::ScheduleTask`, so it runs on the SAME worker pool, not a separate one. ⇒ **today branch A holds
N workers (one blocked in the pull, N-1 piled on the mutex); afterwards it holds ONE** — its scan task is
descheduled and only its AsyncTask occupies a worker. The union unblocks not because nothing blocks, but because
blocking stops being MULTIPLIED by the thread count.

**⚠ WHERE `TaskCompletionSource` BELONGS — cancellation, not the wait.** `ChannelReader.WaitToReadAsync()` is
already TCS-backed internally and the crossing is this tree's existing sync-over-async convention
(`AsyncEnumerableArrowStream` does `MoveNextAsync().AsTask().GetAwaiter().GetResult()`), so a TCS layered on top
of the channel is redundant. What it IS right for is the gap a blocking `Execute()` creates: an uncancellable
park is exactly [cancellation.md](cancellation.md)'s subject. The wait must take a token driven from DuckDB's
interrupt (or a TCS the interrupt path can `TrySetCanceled`), and a pump fault must surface as an exception into
`Execute()` — `ch.Writer.Complete(ex)` carries that. **Without it we would trade a starvation bug for a query
that cannot be interrupted.**

**⚠ FOUR CAVEATS, all read from source rather than assumed:**
- `D_ASSERT(data.async_result.HasTasks())` — a BLOCKED carrying no tasks is an assertion failure.
- If `CanBlock` is false the host converts our BLOCKED into **FINISHED**. Only `FinishProcessing` sets that
  (the pipeline is already tearing down, e.g. a satisfied LIMIT), so it is benign — but BLOCKED must never be
  the only route by which rows we still owe would have arrived.
- `PhysicalTableScanExecutionStrategy::SYNCHRONOUS` takes `ExecuteTasksSynchronously()`, running the task
  INLINE, so the task must be safe on the calling thread too. `DEFAULT` maps to `TASK_EXECUTOR`.
- **⚠ WE WOULD BE AN EARLY ADOPTER.** `ValidateAsyncStrategyResult` enforces the contract, but no in-tree table
  function returns BLOCKED with real tasks: `table_scan.cpp` uses only the non-blocking enum values, and the
  JSON extension's only use is `AsyncResult::GenerateTestTasks()` behind `DUCKDB_DEBUG_ASYNC_SINK_SOURCE`.
  There is also a debug SETTING for the strategy, which reads like a mechanism still being shaken out.

Nothing built. ⚠ When it is, the gate is `fabricator_wait`'s `hold_lock` inversion (§5c) run against a MANAGED
scan: it is the one shape that distinguishes "workers handed back" from "workers held", deterministically
enough to pin.

### §5e. IMPLEMENTATION CHECKLIST for whoever builds it (written while the facts were live; nothing done)

**⚠⚠ SUPERSEDED BY §5f.** Both invariants named here survive and are worth keeping; the ABI shape, the pump
and therefore "THE HAZARD THAT WILL BITE" all dissolved once the design stopped introducing a second owner of
the Arrow stream. The gate shape described at the end is what was built, with one improvement it did not
anticipate: `debug_physical_table_scan_execution_strategy` makes the PRE-FIX path reachable from SQL, so the
A/B needs no remembered number.

**Two invariants that MUST survive, and the design happens to preserve both — say so explicitly, because both
are correctness rather than performance.**
1. **The pull stays single-threaded.** Today `gstate.main_mutex` guarantees it; with one pump per scan it is
   guaranteed BY CONSTRUCTION. That is what keeps a provider's reader (a `SqlDataReader`, an ADOMD reader)
   touched from one thread only — §2's safety argument, which must not be traded away for throughput.
2. **`batch_index` stays monotonic in pull order.** The pump assigns it, so it stays a single sequence — which
   is what the `get_partition_data` declaration (§5) promises and what the batch collector uses to restore
   order. A per-scan-task counter would break it.

**The ABI shape.** Two entries rather than one, because the fast path must not be able to block: a non-blocking
`stream_try_next(handle, out_array, out_has)` and a cancellable `stream_wait(handle, timeout_or_token)`. ⚠ Both
sides of the version must move (`FABRICATOR_ABI_VERSION` in `abi.h` AND `vtable->AbiVersion` in
`Bootstrap.Initialize`) or the host throws a mismatch at boot — and this is a mid-struct addition if placed
beside the existing stream entries, so a stale pair must be caught by the version, not by calling through the
wrong signature.

**⚠⚠ THE HAZARD THAT WILL BITE, and it is this codebase's worst bug class.** A pump that OWNS the stream while
the consumer abandons EARLY — which is exactly what a pushed `LIMIT` produces — is the
`BatchQueryOwner.Claim()` shape: releasing the query from the consumer side while the pump is inside a read gave
`STATUS_HEAP_CORRUPTION` with no assertion output and no stack, and it did NOT reproduce standalone. The
existing pre-existing leak (an orphaned pump on early abandonment) becomes LOAD-BEARING here, because the pump
holds the stream rather than merely draining it. Budget the teardown design first, not last: cancel, drain,
join, and only then release — and expect the failure to present as a crash in an unrelated suite.

**Also required:**
- `ExecuteTasksSynchronously()` runs `Execute()` INLINE on the scan's own worker (`SYNCHRONOUS` strategy), so
  the task must be correct there too — including not deadlocking against its own pump.
- The wait must be CANCELLABLE from DuckDB's interrupt (§5d), or a starvation bug is traded for an
  uninterruptible query.
- A pump fault must surface as an exception from the wait (`ch.Writer.Complete(ex)`), not as end-of-stream —
  silently ending a scan on a provider error is a wrong ANSWER.

**The gate**, and it has to be the shape no existing suite has: two MANAGED scans in one `UNION ALL`, each with
one blocking morsel, asserting the union costs about ONE branch rather than two. `fabricator_wait`'s
`hold_lock` provides the deterministic negative control (workers held ⇒ serial). ⚠ It is a TIMING assertion in
the direction this file otherwise forbids (an upper bound), so it needs a wide margin — assert "well under the
serial sum", never "close to one branch".

**Both tiers, and the service tier is not optional**: it is the first and only run of this path against a real
SQL Server, whose single-threaded-reader safety is the invariant above.

**⚠ THE PRACTICAL REFINEMENT, and it is actionable today: it is not "unions do not overlap".** A union of two
DIFFERENT provider functions overlaps. So a plan that fans out over one function N times is the shape that
serializes, and the remote union form — whose D branches are all the same per-file read — is exactly that
shape.

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

**THE SCALING CURVE, which is the strongest evidence for the fix and better than any single before/after
ratio.** Same table, per-row scalar cost, one process per cell, baseline (every sleep at 0) **652 ms**
subtracted:

| work in the pipeline | t=1 | t=2 | t=4 | t=8 |
|---|---|---|---|---|
| 4 sleeping rows = 2000 ms | 2014 ms | 1058 ms | **526 ms** | 590 ms |
| 8 sleeping rows = 4000 ms | 4128 ms | 2035 ms | 1036 ms | **531 ms** |

Textbook 1/N — and **the ceiling MOVES WITH THE ROW COUNT**, which is what makes it a mechanism rather than a
coincidence: four sleeps cannot use eight threads (590 ms, no better than t=4) and eight sleeps can (531 ms).
`sum(...)` returns 4000, so every sleep really executed and nothing was pruned.

**⚠ COROLLARY WORTH KNOWING: parallelism is bounded by MORSELS, and a morsel is one `DataChunk` — ≤2048 rows
at the shipped default (§3).** A 4096-row table therefore has two morsels and cannot use more than two threads
whatever `SET threads` says. That is the same knob §3 describes from the other side: bigger batches mean fewer,
coarser morsels, so the two levers trade against each other at small row counts as well as overlapping at
large ones.

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

### ✅ §5f. BUILT 2026-08-21 — the loser of the pull hands its worker BACK. **AND THE DESIGN ABOVE WAS WRONG IN ITS CENTRAL CLAIM, which is the most useful thing in this section.**

**As built** (`src/include/fabricator/scan_wait.hpp` + `src/fabricator/scan_wait.cpp`, C++-only, **NO ABI
change and NO managed change**): a thread that finds the pull lock taken no longer parks on it. It reads a
progress counter, `try_lock`s, and on failure returns `SourceResultType::BLOCKED` carrying ONE `AsyncTask`
that waits — **for a bounded time** — for that counter to move. The winner pulls exactly as before; only the
losers behave differently. Shared with `fabricator_wait`'s `async_wait := true`, which prototyped it.

| shape (threads=4, in-session A/B) | old path | new path |
|---|---|---|
| **two managed table-function scans unioned, 1000 ms of blocking pull each** | **2.06 s** | **1.03 s** |
| `fabricator_wait` union, `hold_lock` (pure C++, no managed code at all) | **4.08 s** | **2.05 s** |
| one local Delta scan, 4 morsels of per-row work | 0.73 s | 0.62 s |
| two local Delta scans unioned, per-row work | 1.14 s | 1.18 s |
| 6M-row local Delta aggregate (the overhead check) | 0.96 / 0.86 s | 0.84 / 0.81 s |

Every row is an in-session A/B off ONE binary (see the `SYNCHRONOUS` note below), so "old path" is the code
this change replaced rather than a number remembered from another day. Two more, measured the other way — the
`async_wait` parameter toggled inside the new build — say the same thing about overhead from the other side:
`fabricator_wait(2048000, 0)`, i.e. 1000 chunks of pure contention with no sleep at all, is 0.002 s with the
old shape and 0.002 s with the new one, and the answers (`count`, `sum(id)`) are identical in every mode.

**⚠⚠ THE CLAIM §5d MADE AND I DID NOT EARN: *"today branch A holds N workers … afterwards it holds ONE"*.
That is FALSE for any design where the surplus tasks wait until data arrives.** `AsyncExecutionTask::
ExecuteTask` runs `async_task->Execute()` and fires the interrupt only after it RETURNS, and
`TaskScheduler` is a FIXED set of OS threads with nothing that compensates for a blocked one
(`RelaunchThreadsInternal` only ever tracks `requested_thread_count`) — so an AsyncTask parked on a
condition variable occupies a worker for exactly as long as parking on the mutex did. N blocked scan tasks
still mean N held workers. **The whole benefit therefore comes from the wait RETURNING EARLY**, i.e.:

- **THE TIMEOUT IS THE MECHANISM, NOT A SAFETY NET.** It is what hands the worker back. Getting this
  backwards would produce a change that measures identical to no change at all.
- **THE `notify` IS THE LATENCY FIX, NOT THE MECHANISM.** Without it, a pull that completes in microseconds
  — every local scan — would cost the losers a full timeout each, which on a fast source is catastrophic.
  With it, the fast case never waits at all (the last two rows of the table above are the check).
- Hence the **backoff**: 1 ms doubling to 16 ms, reset the moment a thread gets a batch. A 200 ms remote
  pull then costs a waiter ~90 wake-ups instead of 200, and a microsecond pull costs zero.

**⇒ THE MANAGED PUMP AND THE TWO NEW ABI ENTRIES OF §5d/§5e WERE NOT NEEDED, and dropping them removed the
one hazard that section said to budget for first.** The pull still happens on the scan task that won the
lock, so **nothing new owns the Arrow stream** — the `BatchQueryOwner.Claim()` shape (a pump inside a read
while an early-abandoning consumer releases the query: `STATUS_HEAP_CORRUPTION`, no assertion output, does
not reproduce standalone) cannot arise, because there is no pump. A channel would buy PREFETCH, which is a
separate and still-unbuilt idea; it was never what fixed the starvation.

**Both §5e invariants survive, and by construction rather than by care:**
1. **The pull stays single-threaded** — it is the same `gstate.main_mutex`, still held across the pull. Only
   who WAITS for it changed.
2. **`batch_index` stays monotonic in pull order** — still assigned under that lock, which is what
   `ArrowStreamGetPartitionData` promises DuckDB and what the batch collector uses to restore order.

**⚠ THE LOST-WAKEUP ORDERING IS LOAD-BEARING: read the generation BEFORE the `try_lock`.** A puller that
finishes in between then leaves the waiter's predicate already true, so it returns immediately instead of
sleeping through a change it missed. Releasing the lock and announcing progress is done by one
`AnnounceProgressOnExit` guard on EVERY exit including a throw, so a failed pull cannot leave waiters
sleeping on their timeouts.

**⚠ The wait state is REFCOUNTED (`shared_ptr<ScanWaitState>`), because a parked AsyncTask may OUTLIVE the
scan's global state** — a satisfied LIMIT tears the query down while a wait is in flight. The global state's
destructor calls `Shutdown()`, which wakes every waiter it leaves behind; the memory is the shared object's,
so a late wake-up cannot touch a freed mutex. That is the same class of bug as the `ArrowProducer::Release`
use-after-free (§5's own history) and the same reason it faults on macOS and not here, so it was designed
out rather than tested for.

**⚠ THE `SYNCHRONOUS` STRATEGY IS THE FALLBACK *AND* THE GATE'S A/B LEVER, which is the nicest thing about
it.** Under `debug_physical_table_scan_execution_strategy='SYNCHRONOUS'` DuckDB's
`ValidateAsyncStrategyResult` THROWS on a BLOCKED result, so `CanReturnBlocked` is false and the scan parks
on the lock exactly as it did before this change. That makes the pre-fix path reachable from SQL, so both
legs of the measurement run in ONE process off ONE binary — no remembered numbers, no rebuild. Both gates
are built on it or on `async_wait`, and both were mutation-tested: forcing the park kills
`verify_plugin` at its ratio assertion after 47 pass (every correctness assertion and the positive control
first, which is the right kill for a change that moves no rows), and ignoring `async_wait` kills
`verify_wait` at its own after 29.

**⚠ WHAT IT DOES NOT FIX, measured rather than reasoned: a union whose cost sits DOWNSTREAM of the scan.**
Two local Delta scans with per-row sleeps are 1.14 s before and 1.18 s after — unchanged, and already
OPTIMAL (4000 ms of sleep over 4 threads = 1000 ms), because those pulls never block: the cost is in the
projection. That shape is the collector/`order_matters` axis of §5a, not the pull axis, and this change
neither helps nor harms it. **The target is a scan whose PULL blocks**, i.e. every remote read.

**⚠ `fabricator_host_query` IS A BAD INSTRUMENT HERE and cost a wrong reading before it was dropped.** Its
BIND runs the inner query (measured: `EXPLAIN` alone costs a full inner execution), so most of a union's
wall clock is serial PLAN time by construction, and the inner query's own threads compete with the outer
query's on the same scheduler. Its A/B came out flat while the CPU told a different story (4.05 s user
before vs 1.00 s after — the spin stopped), and reading the flat wall clock as "the fix does not work"
would have been the §6 instrument error yet again. Use `plug_slow_range` (cost inside the pull, one
scheduler) or `fabricator_wait` (no managed code at all).

**Tiers: hermetic 73/73 — 7750 and service 52/52 — 2140, each exactly its previous floor plus the 11
assertions of its own new section** — so no other suite moved, which is the whole behaviour-preservation claim
for a change that touches the scan every provider goes through. The service leg matters beyond regression
coverage: `verify_delta_catalog_s3` is the only place this path runs against `s3://` URIs, and the SQL Server
suites are the only place it runs against a real `SqlDataReader`, whose single-threaded-reader safety is
invariant 1 above.

**⚠ CANCELLATION: the park is BOUNDED, so an interrupt is delayed by at most the backoff cap (16 ms).** That
is what makes this safe without any of §5d's `TaskCompletionSource` plumbing — the concern that section raised
("a starvation bug traded for a query that cannot be interrupted") applies to an UNBOUNDED wait, which this is
not. The scan re-enters and DuckDB's own interrupt check fires on the next call.
[cancellation.md](cancellation.md) is unchanged by this.

**⚠ EARLY ABANDONMENT (a pushed `LIMIT`) leaves waiters parked, and it is the one path that had to be tried
rather than reasoned about.** Six runs each of a `LIMIT` over a 20-batch blocking source — plain, unioned, and
through `fabricator_wait` — return the right rows and exit 0. What carries it is structural (the wait state is
refcounted and the destructor wakes it); the runs are the backstop, because a race is never proved by passing.

**⚠ STILL UNMEASURED: the remote payoff.** Everything above is local, with a sleep standing in for slow IO.
Every live OneLake figure in this section predates it, including the one `TryUnionForm`'s remote gate rests
on (§10) — so that gate stays where it is until the numbers are re-taken.

### §5g. WHAT §5f REACHES, AND WHAT IT DOES NOT: the in-out and collector operators are a DIFFERENT path — and they never had this bug

**Covered, all of it, by one change**: every scan whose rows come from `function.function`, i.e. all nine
`ArrowStreamScan` registrations — the catalog table scan (the `ITable` path), catalog-bound and global table
functions, and `fabricator_query` / `fabricator_functions` / `fabricator_server_info` /
`fabricator_host_query`. One implementation serves all of them, so this is true of **every provider** rather
than of one.

**NOT covered: the table-in-out exchange and the collector**, which produce rows from `in_out_function` —
a separate branch of `PhysicalTableScan::GetDataInternal` with a separate blocking mechanism
(`g_state.BlockSource(guard, input.interrupt_state)`, the interrupt-state parking that sinks use). `scan_wait`
is not reachable from there at all.

**⚠ AND THEY DO NOT NEED IT, because they cannot have the bug: both declare `MaxThreads() == 1`**
([src/catalog/fabricator_schema_entry.cpp:1643](../src/catalog/fabricator_schema_entry.cpp) for the exchange,
`:2014` for the collector — an intra-pipeline cap they hold deliberately, because each has ONE shared holder
buffer per execution). The starvation §5c measured needs N tasks piling onto one lock; with one task per scan
there is nothing to pile up. A blocking call there occupies ONE worker, which is the irreducible cost of a
blocking call rather than a multiplication of it, so a sibling pipeline still gets a worker. **⚠ The corollary
is the thing to remember: if anyone ever raises either thread count, the §5f fix does NOT come along for free
— they would need the mechanism ported to the in-out branch first.**

**⚠ A SECOND, NARROWER GAP, ESTABLISHED FROM SOURCE AND NOT DEMONSTRATED: neither declares
`get_partition_data`** (only the two `ArrowStreamScan` sites do, `:2442` and `:2594`), so such a scan reports
no batch-index support — and `UseBatchIndex` demands that **every** source support it
(`plan_insert.cpp:58-68` → `PhysicalOperator::AllSourcesSupportBatchIndex`), which would take a whole
statement back to §5's single-threaded result collector.

- **⚠⚠ MY TEST OF THAT WAS VOID, AND READING IT AS A REFUTATION WOULD HAVE BEEN THE §6 INSTRUMENT ERROR
  AGAIN.** A streaming fabricator scan with four sleeping morsels measured 0.62 s with a 1-row `cf_tag`
  in-out CROSS JOINED into the plan and 0.62–0.70 s without it — no effect. That is not evidence: **a JOIN
  is a sink, and `GetSources()` walks only the single-child spine** (`physical_operator.cpp:246`; `PhysicalUnion`
  overrides it precisely because of that), so the walk STOPS at the join and never sees the in-out. The
  collector choice was irrelevant in both legs for a third reason as well — the scan's sink was the join, and
  a parallel sink is what §2 shows makes `MaxThreads()` matter at all.
- ⇒ the reachable shape is an in-out or collector scan **on the spine or in a UNION**, and a union's own
  branch serialization confounds the clock. So the gap is real in the source and has **no measured victim**;
  do not fix it on the strength of the reading alone, and do not declare `get_partition_data` for those
  operators without deciding what a batch index MEANS for them (the exchange emits per input chunk, and
  `order_matters` reads the sink's requirement, so declaring one can change a union's scheduling too).

**⚠ Both paths still pay §2's ORIGINAL cost, which this work does not touch**: a source declaring one thread
makes every operator ABOVE it single-threaded. For the exchange and the collector that is the price of their
shared holder, and it is a deliberate trade rather than an oversight — but it means an expensive projection
over an in-out's output gets one thread.

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

**⚠⚠ WHICH INSTRUMENT ANSWERS WHICH QUESTION — get this wrong and the measurement is confidently misleading,
as §5a records happening twice:**

| question | instrument | why the other one lies |
|---|---|---|
| does the plan give a scan's per-thread work more than one thread? | the **scalar** in the projection | a source-side cost is serialized by our own pull mutex, so it is flat even on a perfectly parallel plan |
| do a union's BRANCHES overlap? | the **table function** (cost in the source) | a scalar parallelizes WITHIN a branch, so it shows a real speedup while the branches stay strictly serial |
| is a WRITE sink parallel? | the **scalar**, single source, no union | with a source-side cost nothing can scale, so the comparison cannot discriminate |
| does a blocking pull still starve siblings? | `fabricator_wait(…, hold_lock := true)` — pure C++ — or `debug_physical_table_scan_execution_strategy` as the A/B lever over a **table function** | the setting reaches OUR scan only, so it isolates the pull path from every other reason a plan might serialize (§5f) |
| anything about union overlap through `fabricator_host_query` | **NOTHING — do not use it** | its BIND runs the inner query and the inner query's threads share the scheduler, so most of the wall clock is serial plan time by construction (§5f) |

That third row is why §7's write-path finding rests on scalar measurements and §5a's union finding rests on
table-function ones. Neither result transfers to the other question.

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

## 7a. EVERY WRITE INTO A FABRICATOR TABLE IS SINGLE-TASKED

Measured while answering "what does a CTAS from a union do" (2026-08-21), and it is the more consequential half
of that answer, because a dbt model IS a CTAS. Single source, no union, four morsels x 500 ms of per-row cost —
so intra-branch parallelism is available to every row of the table and only the SINK differs:

| | threads=1 | threads=4 |
|---|---|---|
| streaming to client | 2730 ms | **1227 ms** |
| `CREATE TABLE … AS` -> DuckDB's own storage | 2678 ms | **1224 ms** |
| `CREATE TABLE lk.main.x AS` -> a fabricator table | 2919 ms | **2952 ms** |
| `INSERT INTO lk.main.x SELECT …` | 2954 ms | **3021 ms** |

**So neither §2's `MaxThreads` nor §5's `get_partition_data` reaches any write path** — nor, as §7c measured,
any rowid DELETE or UPDATE, whose scan and filter sit in that same single-tasked pipeline. The cause is ours and our
own code states it, at [src/dml/fabricator_insert.cpp](../src/dml/fabricator_insert.cpp): *"The sink is serial
(ParallelSink defaults to false), so no lock is needed and blocking here cannot starve another sink thread."*
`Pipeline::ScheduleParallel` tests `!sink->ParallelSink()` FIRST, so the whole source pipeline gets one task and
`MaxThreads()` is never read.

**What lifting it would cost — small edit, real decision. ⚠ And mind which side the CAUSE is on, because the
obvious reading is backwards.** The decisive line is the C++ one above: `ParallelSink()` is never overridden, so
it defaults to false. `BulkSession`'s channel being created **`SingleWriter = true`**
([dotnet/Fabricator.Bridge/BulkSession.cs](../dotnet/Fabricator.Bridge/BulkSession.cs), a
`BoundedChannelOptions` property) and `PushBatch` taking no lock are CONSEQUENCES of that, not the blocker —
which matters because flipping `SingleWriter` alone changes nothing, while declaring the sink parallel WITHOUT
it is a correctness bug: concurrent `Push` calls would violate a contract the channel was told it could rely
on. Both must move together, and even then:

- **batch ORDER becomes nondeterministic across threads** — but ⚠ **NOT in the way this doc first claimed, and
  the correction removes the biggest apparent blocker.** `SORTED BY` / persisted `fabricator.sortedBy` /
  clustered writes are SAFE: `DeltaCatalog.SortStream` runs a GLOBAL `ORDER BY` in the host engine over the
  channel-fed stream ([DeltaCatalog.cs:1360](../dotnet/Fabricator.Delta/DeltaCatalog.cs#L1360), applied at
  `:2388`), i.e. DOWNSTREAM of the channel — so producers interleaving upstream of it cannot disturb an
  ordering that is imposed afterwards. What IS exposed is the narrower case: an explicit
  `INSERT … SELECT … ORDER BY x` with NO sort declared on the table, where the user's ordering is carried by
  ARRIVAL ORDER alone and a parallel sink loses it. That costs file clustering (a documented technique), never
  a wrong answer — Delta reads are unordered and DuckDB re-applies any `ORDER BY` above the scan;
- **file LAYOUT changes**, because `BudgetedStream` cuts output files at batch boundaries (§3), so which rows
  land in which parquet file stops being deterministic and `delta.targetFileSize` reasoning goes with it.

The plausible shape is parallel by default with the ordered-write paths opting out, which is a decision to take
rather than a flag to flip. **TAKEN 2026-08-22, in exactly that shape — see §7c.**
**⚠ AND FLIPPING IT NEEDS THE §5f TREATMENT ON THE SINK SIDE (raised 2026-08-21; nothing built).** With
`ParallelSink() == true`, N sink tasks reach `Push` → `WriteAsync(...).GetAwaiter().GetResult()` on a BOUNDED
channel, and the consumer is ONE pool thread doing the actual load — so whenever the load is slower than the
producers (a remote bulk write, i.e. the normal case) the channel fills and every DuckDB worker sits inside
that managed call. That is §5c mirrored, and here blocking is not an edge case but the STEADY STATE: the
bounded channel exists precisely to apply backpressure.

**⚠ THE SINK CAN DO IT BETTER THAN §5f COULD, and the asymmetry is worth knowing.** `OperatorSinkInput`
carries `InterruptState &interrupt_state` (`physical_operator_states.hpp:158`) and `GlobalSinkState` already
derives from `StateWithBlockableTasks` (`:72`) — so `BlockSink(guard, interrupt_state)` PARKS the task and holds
**no worker at all**, where §5f's `AsyncTask` always occupies one. The source's `function.function` branch is
handed no interrupt state, which is the whole reason it had to use a task.

The managed half is nearly free, because the channel already offers both primitives: `Writer.TryWrite` is
synchronous and returns false on a `FullMode.Wait` channel that is full, and `Writer.WaitToWriteAsync()` is the
exact mirror of the consumer's existing `WaitToReadAsync()`. Two shapes:

- **(a) try-push + park + wake** — a host-service callback the consumer invokes when it drains one. No worker
  held, no timeout to tune. Needs the managed→host direction (which host services already are).
- **(b) the §5f shape** — try-push + BLOCKED with a bounded wait (`wait_for_space(timeout)` implemented by
  `WaitToWriteAsync`). No new callback direction, but holds a worker per blocked task for up to the timeout.
  A legitimate stepping stone; (a) is reachable on a sink, so stopping at (b) leaves the better mechanism
  unused.

**⚠ THREE TRAPS, and the first turns an error into a HANG.** (1) `TryWrite`'s `false` is AMBIGUOUS — full, or
the channel COMPLETED because the consumer faulted; treating both as "would block" parks the sink forever on a
failed load. Today's `Push` separates them via `_consumerExited` + the `ChannelClosedException` catch, so the
ABI needs a THREE-way answer (accepted / full / closed), with closed keeping today's behaviour: drop, dispose,
let the real error surface from `Complete`. (2) A BLOCKED sink gets **the same chunk re-delivered**
(`remaining_sink_chunk = true`, `pipeline_executor.cpp:104`), so the try-push may take ownership of the Arrow
array ONLY on acceptance — `Push` currently takes it unconditionally, and its own doc says so. (3)
`SingleWriter = true` must become false, which is the contract half and worthless alone.



## 7b. IMPLEMENTATION HANDOFF for the parallel write sink

⚠ **HISTORY as of 2026-08-22 — this was written before starting, and the sink IS NOW PARALLEL. Read §7c for
what shipped, which numbers were measured, and the TWO claims below that turned out to be false** (the
stepping-stone mechanism its step 3 offers does not exist for a sink, and the same-binary A/B it says is
unreachable from SQL is reachable). Kept because every OTHER prediction in it held, and because the parts still
unbuilt are specified here.

The goal: `CREATE TABLE lk.t AS SELECT …` and `INSERT INTO lk.t SELECT …` should scale with `SET threads` the
way a streaming scan now does. §7a is the measurement (both flat: 2919/2952 ms and 2954/3021 ms at threads 1
vs 4, against 2730 → 1227 ms for the same work streaming to the client) and §7a's bullets are the trade-offs.
This section is the surface.

**THE SURFACE, verified rather than recalled:**

| what | where |
|---|---|
| the sink that must declare itself parallel | `FabricatorPhysicalInsert::Sink` — [src/dml/fabricator_insert.cpp:101](../src/dml/fabricator_insert.cpp#L101); neither this class nor `FabricatorPhysicalCreateTableAs` ([src/dml/fabricator_ctas.cpp:79](../src/dml/fabricator_ctas.cpp#L79)) overrides `ParallelSink()`, so both default to FALSE |
| the COPY path | `CopyToSink` — [src/copy/fabricator_copy.cpp:357](../src/copy/fabricator_copy.cpp#L357). ⚠ Its parallelism is decided by the COPY function's own flags, NOT by `ParallelSink()`; establish how before assuming it comes along |
| the blocking call | `BulkSession.Push` — [dotnet/Fabricator.Bridge/BulkSession.cs:144](../dotnet/Fabricator.Bridge/BulkSession.cs#L144), `WriteAsync(...).GetAwaiter().GetResult()` |
| the contract to flip | `SingleWriter = true` — [BulkSession.cs:45](../dotnet/Fabricator.Bridge/BulkSession.cs#L45), a `BoundedChannelOptions` property. Capacity is `ChannelCapacity = 8` ([:21](../dotnet/Fabricator.Bridge/BulkSession.cs#L21)) — with N producers that is less headroom PER PRODUCER, so re-price it |
| the ABI entry to join | `push_batch` — [src/include/fabricator/abi.h:291](../src/include/fabricator/abi.h#L291), `clr_host.hpp:322`, `Abi.cs:78`, `Bootstrap.cs` |
| the coupling to re-derive | `PhysicalMergeInto(parallel=false)` — [src/catalog/fabricator_merge_into.cpp:200-207](../src/catalog/fabricator_merge_into.cpp#L200-L207) |

**ORDER OF WORK.** Do the managed half first: it is independently testable and cannot change behaviour while
the sink is still serial.

1. **`push_batch_try` (new ABI entry, three-way).** Managed body is `Writer.TryWrite(batch)`. ⚠ **Its `false`
   is AMBIGUOUS — full, or the channel COMPLETED because the consumer faulted — and collapsing the two turns a
   failed load into a HANG** (the sink would park forever on an error). Return ACCEPTED / FULL / CLOSED,
   deriving CLOSED from `_consumerExited` as `Push` does today (`TryWrite` takes no cancellation token, so the
   check has to be explicit). CLOSED keeps today's behaviour: drop, dispose, let the real error surface from
   `Complete`.
2. **⚠ OWNERSHIP ONLY ON ACCEPTANCE.** A BLOCKED sink gets the SAME CHUNK RE-DELIVERED
   (`remaining_sink_chunk = true`, `pipeline_executor.cpp:104`), and `Push` currently takes ownership
   unconditionally — its own doc says so. A `FULL` answer that consumed the Arrow array is a double-free on
   the retry.
3. **The wait.** Prefer shape (a) of §7a — `BlockSink(guard, input.interrupt_state)` parks the task and holds
   NO worker, which the source side could not do — with a host-service callback the consumer invokes when it
   drains one. Shape (b) (`wait_for_space(timeout)` over `WaitToWriteAsync`, a `scan_wait` mirror) is the
   cheaper stepping stone and holds one worker per blocked task.
4. **Then flip `ParallelSink()`** on the two operators, and only then look at COPY.
5. **Re-derive the MERGE comment.** Its `parallel=false` is justified by TWO things, and this work invalidates
   one: *"PushBatch blocks for backpressure and takes no lock, documented as safe only because ParallelSink()
   is false"*. The OTHER reason — every action's operator shares ONE global sink state — survives on its own,
   so MERGE can stay serial; but the comment must say so for the right reason, and whether MERGE could then go
   parallel is a SEPARATE decision.


**THE GATE, and it is in better shape than §5f's was.** The claim is a wall-clock RATIO, but this time the
correctness half is assertable: rows and values must be identical, and the ordered-write suites must stay at
their exact counts — which is precisely what tests the sort-is-downstream reasoning above.

- `verify_delta_sorted_by` (30) and `verify_delta_clustered_optimize` (147) are the load-bearing ones: if
  parallel producers DID disturb a declared ordering, they are what fails.
- `verify_delta_catalog_write` / `_transactions` (engine-doubled) cover the plain paths; the whole hermetic +
  service tiers must come back at IDENTICAL counts, since no answer may move.
- For the speed claim, reuse §7a's shape (single source, no union, per-morsel `plug_sleep`, CTAS into a
  fabricator table) and the same-process A/B trick §5f used — ⚠ but note there is no equivalent of
  `debug_physical_table_scan_execution_strategy` for a SINK, so the pre-fix leg is not reachable from SQL.
  `SET threads=1` vs `threads=4` is the honest A/B here, with the DuckDB-storage CTAS row as the control that
  the machine really can scale.
- ⚠ **Mutation-test the ownership rule specifically** (step 2). Its failure mode is a double-free under
  backpressure — silent on Windows, and the kind of thing a green suite hides. Forcing every `TryWrite` to
  report FULL once before accepting would exercise the retry path deterministically.

**⚠⚠ AND THE CAUSAL FACT THAT MAKES THIS WORTH DOING AT ALL, which an earlier version of this section got
BACKWARDS (user-raised: "I would assume ParallelSink() == false should keep the pipeline multi threaded until
pumped into the sink" — it does NOT).** `Pipeline::ScheduleParallel` tests `!sink->ParallelSink()` at
[pipeline.cpp:103](../duckdb/src/parallel/pipeline.cpp#L103) — BEFORE it looks at the source, the intermediate
operators or `MaxThreads()` — and falls through to `ScheduleSequentialTask` (`:175`). So a serial sink
serializes the **whole pipeline**: the scan, every projection, the sort, all of it, on one task. §7a's own
table is the proof (same source, same per-row work: 1227 ms streaming vs 2952 ms into a fabricator sink).

⇒ **THE "KILL CONDITION" RECORDED HERE EARLIER WAS WRONG.** It said that if the bulk CONSUMER is the
bottleneck — which for a remote target it always is, it is real IO — parallel producers buy nothing. False:
the producers are not merely filling a channel, they are doing all of the query's CPU work. Parallelising them
takes the statement from roughly *CPU + IO* toward *max(CPU/N, IO)*, so the win survives an IO-bound sink and
is bounded only by whichever side saturates first. The measurement still worth taking is the SPLIT (how much
of a real write is CPU above the sink vs IO inside it), because that predicts the SIZE of the win — not
whether there is one.

**⚠ THE ORDER-PRESERVING ROUTE EXISTS, AND §5f IS ITS PREREQUISITE — which we only satisfied on 2026-08-21.**
DuckDB does NOT protect an ordered write from a sink that declares itself parallel: `ParallelSink()` is a
virtual on the operator and nothing second-guesses it. What it offers instead is the mechanism its OWN insert
uses — a sink declaring `RequiredPartitionInfo() == BatchIndex()` receives chunks TAGGED in order even when N
threads read them, and a sort is fully cooperative about that (`PhysicalOrder` declares
`ParallelSource() == true`, `SupportsPartitioning(BatchIndex()) == true`, `SourceOrder() == FIXED_ORDER`). So
the ordered case does not have to be given up; it needs a reorder buffer ahead of the channel.
- ⚠ **It must be gated PER PLAN or it is an InternalException**: `pipeline.cpp:120-124` throws when a
  batch-index-requiring sink meets a source that cannot supply one. DuckDB gates its own with the public
  `PhysicalPlanGenerator::UseBatchIndex(context, plan)` (`plan_insert.cpp:58` — threads > 1 AND every source
  supports it), and since we build the operator in our own `PlanInsert` we can make the same call there.
- ⚠ **Before §5f our own scans could not have fed such a sink at all**, since `get_partition_data` was
  declared nowhere — so this option is newly available rather than previously overlooked.


**⚠ THE STAGED `COPY INTO` ROUTE IS WHERE THIS SHOULD PAY MOST — AND IT HANDS DuckDB'S OWN ARROW SCAN A
BLOCKING PULL (user-raised 2026-08-21; REASONED, NOT MEASURED).** On a Fabric warehouse with
`mssql_copy_into_staging`, the consumer is not `SqlBulkCopy` over TDS but
`HostParquetStaging.WriteDirectory` ([dotnet/Fabricator.Bridge/HostParquetStaging.cs:44](../dotnet/Fabricator.Bridge/HostParquetStaging.cs#L44)),
which binds the channel-backed stream into `COPY (SELECT * FROM "…") TO '<dir>' (FORMAT parquet,
PER_THREAD_OUTPUT true)`.

- **⚠ SEPARATE THE TWO SIDES — the fixes in this doc act on only one of them, and a first version of this
  bullet ("the mechanism isn't a faster pull") was too narrow (user-corrected).**
  - **The CONSUMER side is already parallel and none of our work touches it**: that inner `COPY … TO` query has
    DuckDB's OWN `duckdb_arrow_scan` as its source and a `PER_THREAD_OUTPUT` COPY sink — upstream at both ends.
    Its speedup over `SqlBulkCopy` is parallel parquet ENCODING to local disk replacing row-by-row TDS, not a
    faster pull (a pull is an in-memory channel read either way).
  - **The PRODUCER side is where §2, §5 and §5f are all currently DEAD, and a parallel sink is what switches
    them on.** For `CREATE TABLE wh.t AS SELECT … FROM lake.t` the outer plan is ONE pipeline — our Delta scan
    → projection → the bulk sink — and a serial sink makes it ONE TASK, so `MaxThreads()` is never read,
    `get_partition_data` has nothing to reorder, and **§5f is literally inert: with one task there is no lock
    contention to hand a worker back from.**
  - ⇒ **§7b is therefore compositional rather than incremental**: it does not merely parallelise the sink, it
    ACTIVATES the three read-path fixes on every write. A lake→warehouse staged load is where they compound —
    the source is a remote Delta scan whose pull genuinely blocks (§5f's exact target) feeding a consumer that
    is no longer the limit.
- ⇒ **it shifts the bottleneck ONTO our single-tasked producer**, so this route is the best place to MEASURE
  §7b's payoff rather than an alternative to it. ⚠ The existing staged-vs-drained numbers (14.5–16.1 s vs
  16.8–28.9 s, 2026-08-10) predate the `MaxThreads` fix, §5f and everything here — re-take them.
- **⚠ AND IT QUALIFIES A CLAIM §5d MAKES.** That section says the blocking pull is OURS because *"DuckDB's own
  arrow scan … does not suffer it because its pull is an in-memory read"*. That is true of a MATERIALIZED Arrow
  table and FALSE of a channel-backed one: `ChannelArrowStream` blocks whenever the producer is behind, while
  DuckDB's arrow scan declares `NumberOfThreads()` and serializes its pull under a global mutex — the §5c shape
  exactly, inside upstream code we cannot apply §5f to. It is live TODAY on this route (the producer is one
  task, so the channel is often empty) and it SHRINKS once §7b lands (parallel producers keep the channel fed).
  ⚠ No CI can see it: `verify_copy_into_staging`'s positive leg is manual/live-Fabric.
- **Upstream-shaped, not filed**: a bound arrow input CAN block, so DuckDB's arrow scan should hand its worker
  back (or cap threads for a producer-backed stream). A stock repro is a python `RecordBatchReader` that sleeps
  between batches, unioned with anything — the same shape as `plug_slow_range`.

## ✅ 7c. BUILT 2026-08-22 — the write sinks declare themselves parallel. AND THE HANDOFF ABOVE WAS WRONG ABOUT THE MECHANISM ITS OWN STEP 3 PREFERRED, which is the most useful thing in this section.

`FabricatorPhysicalInsert` and `FabricatorPhysicalCreateTableAs` now override `ParallelSink()`, decided at PLAN
time in [src/catalog/fabricator_catalog.cpp](../src/catalog/fabricator_catalog.cpp)'s
`FabricatorParallelWrite`, and the managed channel behind them is declared multi-writer. C++ plus one managed
line; **no ABI change**.

**MEASURED, same binary, `SET threads` the only variable** — 2 M rows out of a local Delta table, eight md5
rounds per row as the CPU term, median of two, with a CTAS into DuckDB's OWN storage as the control that the
machine really can scale:

| statement | threads=1 | threads=4 | ratio |
|---|---|---|---|
| `CREATE TABLE lk.t AS SELECT id, md5^8(s) FROM lk.src` | 3.49 s | **2.13 s** | 1.64x |
| `INSERT INTO lk.t SELECT id, md5^8(s) FROM lk.src` | 3.29 s | **1.98 s** | 1.66x |
| the same CTAS into DuckDB storage (control) | 2.70 s | 1.04 s | 2.60x |

⚠ The gap between 1.64x and 2.60x is the sink's own consumer — one .NET pool thread writing local parquet,
~1.1 s of the 2.13 — not a residue of serialization. That is the SPLIT §7b said to measure in order to predict
the size of the win, and it says the win is bounded by whichever side saturates first, exactly as predicted.
The compositional claim of §7b (that this activates §2 / §5 / §5f on every write) is visible here only as the
scan half; the remote case where it compounds is still unmeasured.

**The gate:**

```
FabricatorParallelWrite(context, plan) =
    plan is present                                    // INSERT ... VALUES has no source pipeline
    AND TaskScheduler::NumberOfThreads() > 1
    AND OrderPreservationRecursive(*plan) != FIXED_ORDER
```

plus `&& !op.return_chunk` at the INSERT site.

**⚠ IT DELIBERATELY DOES NOT CONSULT `preserve_insertion_order`, where DuckDB's own `PlanInsert` does — and
that is a property of the TARGET rather than a liberty taken.** That setting is about the order of a RESULT
handed to a client, and DuckDB's inserts must honour it because its storage IS ordered. A fabricator table has
no insertion order of its own: a scan of it returns rows in whatever order the provider yields. So the only
ordering that has to survive a write here is one the PLAN states, which is exactly `FIXED_ORDER` — an explicit
`ORDER BY`, which stays serial.
- What that costs, stated rather than implied: a table getting its file clustering INCIDENTALLY from source
  order stops getting it. Pruning quality, never a wrong answer.
- A DECLARED ordering is untouched, and this is §7a's correction paying off: `SORTED BY` /
  `fabricator.sortedBy` / a clustered Delta table are imposed by `DeltaCatalog.SortStream` DOWNSTREAM of the
  channel these tasks feed, so producers interleaving upstream cannot disturb them. `verify_delta_sorted_by`
  (30) and `verify_delta_clustered_optimize` (147) came back at their exact counts.

**⚠⚠ AN EXPLICIT `ORDER BY` WRITE LOSES ALMOST NOTHING BY STAYING SERIAL — MEASURED, and it re-prices the
order-preserving route this section lists as open.** The intuition ("FIXED_ORDER ⇒ one task ⇒ back to §7a's
flat row") is wrong, because a sort SPLITS the plan: `PhysicalOrder` is a blocking sink, so the scan and the
projection live in their OWN pipeline whose sink is the sort — which declares `ParallelSink()` true — and only
the sort→our-sink pipeline is serialized by us. The expensive half therefore still uses every thread.
MEASURED, same shape as the table above plus `ORDER BY id DESC`: **3.62 s → 1.90 s at threads 1 vs 4**, i.e.
within noise of the unordered 2.13 s. ⇒ the reorder buffer would buy the residue of a cheap pipeline, not the
1.64x. Do not build it on the assumption that ordered writes are slow; measure a shape where the work is
genuinely BELOW the sort first.

**⚠ `SET threads=1` IS THE PRE-CHANGE LEG, so a same-binary A/B exists after all — §7b said it did not**
("there is no equivalent of `debug_physical_table_scan_execution_strategy` for a SINK"). The gate returns false
at one thread, so leg A takes exactly the old code path. That is what makes the ratio assertion in
`verify_plugin` a comparison rather than a remembered number.

**⚠⚠ WHAT IS NOT BUILT, AND WHY THE HANDOFF'S OWN PLAN FOR IT CANNOT WORK AS WRITTEN: a sink that finds the
channel full still PARKS its worker.** §7a preferred shape (a) (`BlockSink` + a wake) and offered shape (b) —
a `scan_wait` mirror: try-push, return BLOCKED, wait with a timeout — as "a legitimate stepping stone".
**Shape (b) does not exist for a sink.** `OperatorSinkInput` carries `global_state`, `local_state` and
`interrupt_state` and **no `async_result`** (`physical_operator_states.hpp:151`), so the AsyncTask mechanism
§5f is built on is unreachable from a sink; a sink's only route to BLOCKED is
`StateWithBlockableTasks::BlockSink`, which parks the task and is woken by nothing until someone holding the
same lock calls `UnblockTasks`. Only the managed consumer knows when space appears.
- ⇒ the refinement needs a **managed→host wake**: a new `FabricatorHostServices` entry the consumer calls
  after each drained batch, plus a shared wake object whose gstate pointer is cleared under a mutex at
  teardown (the `ScanWaitState::Shutdown` shape), plus a way not to pay a host call per batch on the hot path.
  That is a design, not a flag.
- ⇒ **steps 1 and 2 of §7b's order of work (`push_batch_try` + ownership-on-acceptance) were deliberately NOT
  built**: without the wake they have no consumer, and a three-way ABI entry nothing calls is dead code
  carrying its own ownership hazard.
- **⚠ AND `BlockSink`'S RETURN IS A TRAP FOR WHOEVER DOES BUILD IT: it returns `SinkResultType::FINISHED` when
  `can_block` is false** (`interrupt.hpp:104`), and FINISHED tells the pipeline to STOP FEEDING THE SINK. A
  sink that treats "BlockSink returned" as "I am blocked" therefore drops the rest of its input SILENTLY.
  Check `CanBlock(guard)` first and fall back to the blocking push.

**⚠ THE PARKED WORKERS CANNOT DEADLOCK, and that is what makes shipping without the wake defensible rather
than optimistic.** Established from DuckDB's source rather than assumed: the bulk consumer runs on a .NET pool
thread, never a DuckDB worker, so it drains and unparks regardless; and the one case that looked like a cycle —
the staged `COPY INTO` route, whose consumer runs `COPY (SELECT * FROM <arrow view>) TO …` on a NEW DuckDB
connection — is safe because `Executor::ExecuteTask` fetches from the CALLING thread's own producer queue and
executes the task itself (`executor.cpp:569-590`). So an inner query progresses on the pool thread even with
every worker parked. The cost is latency for co-tenant statements sharing the scheduler, which went from one
parked worker to N; the benefit is that a write is no longer single-tasked at all.

**COPY got the same prize through a different door, and a STRICTER gate — for a reason worth recording.**
`PhysicalCopyToFile::ParallelSink()` is `per_thread_output || partition_output || parallel`, and `parallel`
comes from the copy FUNCTION's `execution_mode` callback, so `FabricatorCopyExecutionMode` now returns
`PARALLEL_COPY_TO_FILE`. ⚠ But that callback is handed BOOLEANS, and `preserve_insertion_order` is already true
for an explicit `ORDER BY` **and** for the default setting — indistinguishable from inside it. Returning
PARALLEL unconditionally would silently ignore `COPY (SELECT … ORDER BY x) TO …`. So COPY is parallel only when
`preserve_insertion_order` is off, exactly like DuckDB's own parquet writer, and is therefore SERIAL BY DEFAULT
where INSERT/CTAS are parallel by default. An inconsistency with a cause; lifting it needs the plan, i.e. an
upstream signature change. ⚠⚠ **THAT LAST CLAUSE IS WRONG, corrected the same day in §7d**: the callback cannot discriminate, but `plan_copy_to_file` derives the boolean FROM THE PLAN, so a scan declaring `order_preservation_type = NO_ORDER` would make COPY parallel by default with an explicit `ORDER BY` still winning — the discrimination does not have to happen inside the callback. Not adopted, for a reason that has nothing to do with COPY (§7d). ⚠ `BATCH_COPY_TO_FILE` is NOT claimed even when `supports_batch_index` is true —
that mode requires `prepare_batch`/`flush_batch`, i.e. the reorder buffer below, and claiming it without them
throws at planning.
- **MEASURED, both halves, same shape as the table above** (`COPY (SELECT id, md5^8(s) FROM lk.src) TO '<dir>'
  (FORMAT delta, MODE 'overwrite')`): order-preserving **3.21 s / 3.38 s** at threads 1 vs 4 — FLAT, the
  documented serial default — and with `SET preserve_insertion_order=false` **3.41 -> 2.07 s (1.65x)**, i.e.
  the same ratio the two operator sinks get. The flat row is the CONTROL: it is what says the gate really is
  the setting rather than something else quietly serializing COPY.

**The MERGE comment was re-derived (step 5) and one of its two reasons is now dead.** "PushBatch takes no lock,
safe only because ParallelSink() is false" no longer holds. What survives on its own: `PhysicalMergeInto` drives
our sub-operators MANUALLY over one shared global sink state, so their `ParallelSink()` is never consulted there
at all — and the INSERT action builds `FabricatorInsertTarget` directly, leaving `parallel` at its false
default. Whether a merge could then go parallel is a separate decision, not a consequence of this one.

**Gates.** Hermetic `verify_delta_catalog_write` 43 → 54 per engine leg (+11 twice), plus
`verify_delta_catalog_delete` 28 → 39 and `verify_delta_catalog_update` 84 → 96 for the rowid DML sinks
(+23 twice) — tier floor 7750 → **7818**, and no other suite moved: a 40 000-row (≈20 morsel) CTAS and
INSERT read THROUGH the catalog, asserted by count + `sum(id)` + a `hash` checksum, so a batch enqueued twice,
dropped, or mis-paired moves an assertion. ⚠ Its source must be a fabricator table and not `range()`, which is
a single-threaded source (§6) — off `range` the sink gets ONE task however many threads are set, and the
section would pass while exercising nothing. Service `verify_plugin` 49 → 79 (floor 2140 → **2170**): the
`threads=1` vs `threads=4` ratio over four `plug_sleep` morsels, with the serial leg's own duration as the
positive control and both legs' row counts asserted (a speed claim is worthless if the fast leg dropped a
batch).
- ⚠ **It also made that the first plugin suite to WRITE, and it failed on the first tier run for the reason
  this repo has already been caught by twice: no `require parquet`.** The sqllogictest runner does not
  auto-load a statically linked extension the way the shell does, so the Delta write died inside DuckDB's COPY
  with *"Copy Function with name \"parquet\" is not in the catalog"* — a message about the writer, in a suite
  about plugins. Any suite that starts writing needs that line.

**✅ AND THE ROWID DML SINKS FOLLOWED THE SAME DAY — DELETE is the LARGEST ratio of the whole pass.** An
UPDATE's and a DELETE's scan, filter and rowid append sit in the SAME pipeline as their sink, so they were
single-tasked for exactly the reason INSERT was. MEASURED on the same 2 M rows with the CPU term in the
PREDICATE (`WHERE md5^8(s) LIKE '0%'`), before and after, threads 1 vs 4:

| statement | before | after |
|---|---|---|
| `DELETE FROM lk.t WHERE md5^8(s) LIKE '0%'` | 3.13 / 3.24 s → 3.28 / 3.38 s (FLAT) | 3.22 s → **1.36 s (2.37x)** |
| `UPDATE lk.t SET s='x' WHERE md5^8(s) LIKE '0%'` | 7.15 / 7.43 s → 6.99 / 7.16 s (FLAT) | 7.15 s → **5.00 s (1.43x)** |

- The DELETE ratio is the biggest because its whole cost IS that pipeline — the provider writes one deletion
  vector at Finalize. The UPDATE residue is the merge-on-read read-back plus the post-image write, which
  happen inside `ExecuteUpdate` at Finalize and are serial by construction.
- **⚠ THE "BEFORE" UPDATE ROW ALREADY BURNED THE THREADS WITHOUT GETTING ANYTHING: 7.7 s of user CPU at
  threads=1 against 10.5–12.5 s at threads=4, for the SAME wall clock.** That is the provider-side host
  queries taking the threads while the statement's own pipeline could not — a shape worth recognising,
  because "CPU went up and the clock did not move" reads like contention and was really one half of the
  statement being unable to use what the other half was already paying for.
- **It was a one-line change because `AppendModifyBatch` ALREADY took `gstate.lock` around its only shared
  mutation**, with the `ArrowAppender` per-call. That lock is now load-bearing rather than defensive, and its
  comment says so.
- **⚠ DuckDB's OWN `PhysicalUpdate` and `PhysicalDelete` declare `ParallelSink()` TRUE UNCONDITIONALLY**
  (`physical_update.hpp:61`, `physical_delete.hpp:52`) — no order gate, no duplicate-match gate. That is the
  precedent, and it corrects a caution recorded here hours earlier: the objection below is real but is NOT a
  reason to serialize, since upstream accepts the same nondeterminism. (Its `ON CONFLICT DO UPDATE` path is
  the one that serializes, for a different reason — it must DETECT a double update in order to error.)
- **⚠ THE ONE SEMANTIC CONSEQUENCE, stated rather than buried: a duplicate-match UPDATE's winner becomes
  nondeterministic.** `ExecuteUpdate` keys its post-image dictionary by rowid and is LAST-WRITE-WINS, so
  `UPDATE t SET … FROM other` whose join matches one target row twice used to resolve in the order the serial
  sink saw the batches. That was never a promise — it is a hash join's probe order — but it was stable, and
  now it is not. The row COUNT such a statement reports (the dictionary's size, not the matched rows) is
  unchanged.
- Gates: `verify_delta_catalog_delete` 28 → **39** and `verify_delta_catalog_update` 84 → **96**, both
  engine-doubled, at ~20 morsels rather than the single chunk the rest of those suites touch. ⚠ The UPDATE one
  asserts a DERIVED value per row (`s = 'u' || id`), because what a pairing bug produces is a value on the
  WRONG row, which no count can see. Plus `verify_plugin` 66 → **79** for the DELETE ratio.
  **Mutation-tested**: forcing the modify flag false dies at exactly that ratio after 74 assertions pass,
  while BOTH correctness suites stay green — the right kill, since a parallel DELETE and a serial one return
  the same rows.

**Still open here:**
- the wake above, which is the only thing between this and holding zero workers under backpressure;
- **the ORDER-PRESERVING route** — `RequiredPartitionInfo() == BatchIndex()` plus a reorder buffer ahead of the
  channel, gated per plan via the public `PhysicalPlanGenerator::UseBatchIndex` or it is an InternalException
  (`pipeline.cpp:120-124`). ⚠ **Re-priced DOWNWARD by the measurement above**: an explicit `ORDER BY` write
  already scales (the sort splits the plan), so its remaining prize is the sort→sink pipeline, not the 1.64x.
  Where it would still pay is a shape whose cost sits BELOW the sort, and **COPY**, which is serial by default
  for a reason no reorder buffer removes;
- `ChannelCapacity = 8` — CLOSED by measurement, see the box below;
- ~~the remote payoff~~ — **MEASURED 2026-08-22 and it is NIL: every remote shape is FLAT in `SET threads`.**
  The parallel write sink is a LOCAL / CPU-bound win; §7e has the numbers, the reason, and the two void
  measurements that said 3.7x first.

**⚠ `ChannelCapacity = 8` IS NOT THE CONSTRAINT — MEASURED 2026-08-22, so this stops being an open question.**
The worry was that a bound of 8 became a TOTAL across N producers rather than one producer's allowance. Two
2 M-row CTAS statements at `threads=4`, capacity the only variable, republished per cell: **8 → 1.87 / 1.26 s,
32 → 1.79 / 1.30 s, 128 → 1.83 / 1.28 s.** Indistinguishable. The consumer keeps up well enough on this shape
that a deeper buffer buys nothing, and the number's job is to bound memory — so it stays at 8. ⚠ It is a LOCAL
shape; a remote consumer is the case that could still want more, and that is part of the unmeasured remote
payoff rather than a separate question.

**⚠⚠ AND THE CO-TENANT MEASUREMENT — the one that would price the wake — IS BLOCKED BY A PRE-EXISTING,
UNRELATED DEFECT: the raw loadable SEGFAULTS at `LOAD` inside a stock DuckDB 1.5.5 python wheel.** Worth
recording loudly because it is not about concurrency at all and it breaks two documented flows (dbt-duckdb and
the Fabric notebook both load the raw loadable into a stock wheel).
- **Established, not assumed, that it is not today's work**: `git checkout <pre-change> -- src/`, rebuild, and
  the crash reproduces identically (exit 139, no output). Restored afterwards.
- **It is not the CLR boot either**: pointing `FABRICATOR_MANAGED_DIR` at a nonexistent directory STILL
  segfaults, where that path is documented to fail cleanly with *"failed to load hostfxr"*. So it happens in
  the extension's own load/registration, before any managed code runs (no `FABRICATOR_LOG_FILE` line appears).
- **The version-rejection path is intact**: the same artifact against duckdb 1.5.2 gives the clean
  *"built specifically for DuckDB version 'v1.5.5'"* error, exit 0. So the crash needs a version MATCH, i.e.
  a load that actually initializes.
- Unknown: when it broke, and whether the SHIPPED single-file artifact is affected (its smoke test is the
  dispatch-only distribution tier, which has not run since). ⇒ **check the shipped artifact before assuming
  users are unaffected**, and bisect from the last known-good flow rather than from this section.

⇒ **THE WAKE THEREFORE STAYS UNBUILT, and the reason is now stronger than "it is a design rather than a
flag".** Its hazard is latency-only and provably not a deadlock; the measurement that would tell us how much
latency needs two connections in one process, which is exactly what the crash above prevents. Building an
ABI entry plus a new lifetime protocol into the write path for an effect nobody can size is the shape this
repo keeps recording as the source of confident wrong stories.

### §7d. `TableFunction::order_preservation_type` — ANALYSED + PROBED 2026-08-22 (user-raised), NOT ADOPTED

The knob a scan can use to tell DuckDB "my rows have no order". `OrderPreservationType { NO_ORDER,
INSERTION_ORDER, FIXED_ORDER }`, defaulting to `INSERTION_ORDER` — and **not one function in DuckDB's own tree
overrides it**, on 1.5.5 OR on `main` (a fetch of `origin/main` finds exactly ONE assignment in the whole
repository: the default). Setting it would make us the first.

**THREE readers, feeding TWO mechanisms** (established by grep, not from the names):
- `PhysicalTableScan::SourceOrder()` and `PhysicalTableInOutFunction::OperatorOrder()` both return it — so the
  same field governs our catalog/table-function SCANS *and* our table-in-out operators (`_each`, custom in-out,
  the collector).
- `PhysicalPlanGenerator::OrderPreservationRecursive` → `PreserveInsertionOrder`, consulted at **five** sites:
  the result collector, the Arrow collector, `plan_copy_to_file`, `plan_insert`/`plan_create_table`, and
  `plan_limit`.
- `Pipeline::IsOrderDependent()`, consulted at **two**: `PhysicalUnion`'s `order_matters` and
  `PhysicalOperator::OperatorCachingMode`.

**WHAT `NO_ORDER` WOULD BUY, and one item corrects this doc:**
- **⚠ COPY WOULD BE PARALLEL BY DEFAULT — which means §7c's claim that the COPY gate is "liftable only with the
  plan, i.e. an upstream signature change" is WRONG.** True of the `execution_mode` callback, which is handed
  booleans; false as a conclusion, because `plan_copy_to_file` derives `preserve_insertion_order` FROM THE PLAN,
  so a source declaring its own order semantics decides it. An explicit `ORDER BY` still wins —
  `OrderPreservationRecursive` short-circuits at the first source and `PhysicalOrder` is a source returning
  `FIXED_ORDER`. **The discrimination does not have to happen inside the callback.**
- a parallel `PhysicalStreamingLimit` instead of the batch limit; DuckDB's OWN insert/CTAS reading our scan
  going parallel-streaming; and our own `preserve_insertion_order=false` SET on batched host queries becoming
  redundant (§10 item 1).
- **NOT a union fix.** `NO_ORDER` short-circuits ONE of `order_matters`'s five clauses and the SINK's clause
  fires regardless (`PhysicalBufferedCollector::SinkOrderDependent()` is unconditionally true; the batch
  collector trips the `batch_index` clause instead). That confirms §5a's mechanism from the other direction.
- **On `main` it would buy slightly more, and this is the half worth watching**: `IsOrderDependent` is
  byte-identical there, but a NEW consumer exists — `PipelineBroadcastExchange` carries an
  `OrderPreservationType source_order` and derives an `order_mode` (UNORDERED / SEQUENTIAL / BATCH_INDEX) that
  `PhysicalCTE` consults. So the type is being wired into new scheduling on the future line, and a source
  declaring `NO_ORDER` will unlock more of it than it does today.

**THE COST, MEASURED — and the first measurement of it was VOID, which is the reusable part.** A single-file
400k-row table, `threads=8`, one chunk in eight made ~200x heavier so threads finish out of order:

| probe | shipped (`INSERTION_ORDER`) | `NO_ORDER` |
|---|---|---|
| `SELECT id FROM (<uneven projection>) WHERE id % 997 = 0 LIMIT 12` | the SAME ascending 12 rows, 4/4 runs | a DIFFERENT, non-ascending 12 rows on EVERY run |

- ⚠ **My first instrument was `md5(string_agg(id, ','))` and it was worthless**: `string_agg` over a parallel
  aggregate combines partial states in arbitrary order, so BOTH legs varied — for a reason that has nothing to
  do with the scan. A positional fingerprint has to be an arrival-order observable, not an aggregate.
- ⚠ **And the hermetic tier is GREEN under `NO_ORDER` — 73/73, 7818, zero failures — which is a VACUOUS PASS,
  not evidence.** The suites are small and uniform, so the collector happens to emit in order; the adversarial
  shape above is what shows the order really moves. A tier run must not be used to clear this change.

**IF IT IS EVER EXPOSED TO C#, EXPOSE A BOOLEAN, NOT THE ENUM.** It is expressible per table — `GetScanFunction`
builds a FRESH `TableFunction` per table REFERENCE, so this is per-bind decidable exactly like the
`exact_filter_pushdown` follow-on, and it would ride the v73 `table_info` doc. But `FIXED_ORDER` is a TRAP:
nothing in DuckDB reads it to eliminate a redundant `ORDER BY`, it makes `IsOrderDependent` true (union
serialization), it forces the order-preserving collector — and, sharpest, **since §7c it would make
`FabricatorParallelWrite` return FALSE**, so a provider declaring `FIXED_ORDER` would silently serialize every
write that reads that table. ⚠ Also remember the field is shared with `PhysicalTableInOutFunction`, so a
per-function declaration would change `_each` output ordering too.

**NOT ADOPTED, and the reason is the shape of the trade rather than the size of it.** The concrete win is
parallel COPY by default, which already has a one-line user workaround (`SET preserve_insertion_order=false`);
the price is that an unordered `SELECT` over a fabricator table stops returning rows in a stable order and a
bare `LIMIT` starts returning arbitrary rows. If it is ever wanted it should be an OPT-IN — an ATTACH option or
a per-table provider declaration — never a blanket default, because the semantic claim ("this table has no row
order") is true of the FORMAT while the stable order is something callers observably rely on.

### ⛔ §7e. THE REMOTE PAYOFF — MEASURED LIVE 2026-08-22 (user-enabled), AND §7b'S COMPOSITIONAL CLAIM IS REFUTED

§7b argued that the parallel write sink is compositional: on a lake→warehouse staged load it would ACTIVATE §2
(`MaxThreads`), §5 (`get_partition_data`) and §5f (the BLOCKED pull), all three of which are dead on a
single-tasked write. That was REASONED. Measured against live Fabric — `lake.dbo.his`, 89 files, 659,278 rows —
**every remote shape is FLAT in `SET threads`:**

| shape | threads=1 | threads=4 | verdict |
|---|---|---|---|
| lake → lake, 3 columns (trivial producer work) | 5.93 / 6.23 s | 6.68 / 5.53 s | FLAT |
| lake → lake, 8 md5 rounds per row (real producer CPU) | 5.56 s | 5.63 / 5.57 s | FLAT |
| lake → warehouse, STAGED `COPY INTO` | 6.36 / 6.22 s | 7.51 / 6.73 s | FLAT (marginally worse) |
| lake → warehouse, TDS `SqlBulkCopy` | 39.77 s | 37.93 s | FLAT |

Row counts verified on both warehouse routes (**659,278 landed = source**), so no cell is a fast-because-empty
artifact.

**WHY, and it is a bound on §7c rather than a defect in it.** Read the CPU column: user CPU is **0.47–3.67 s
inside a ~6 s wall** on the staged and lake→lake shapes (and 1.2–18.6 s inside a ~38 s TDS wall). These
statements are latency-bound end to end, so the term a parallel sink reduces — producer CPU — is a small
fraction of the clock and dividing it by four is invisible. ⚠ Note the threads=4 legs consistently burn MORE
user CPU for the SAME wall time (staged 0.66 vs 0.83; md5 3.67 vs 0.84; TDS 18.6 vs 1.2): the parallel work IS
happening, it just does not reach the critical path. §7b's reasoning ("a serial sink serializes the whole
pipeline, so the win survives an IO-bound sink") is right about the MECHANISM and wrong about the SIZE on these
shapes — the win is real where producer CPU is a real share of the clock, which is the LOCAL case §7c measured
(1.6x–2.4x), not the remote one.

**⚠⚠ AND THE FIRST TWO MEASUREMENTS SAID THE OPPOSITE — 3.7x — WHICH IS THE MOST REUSABLE PART.** Order
"threads=1 then threads=4" gave **22.74 → 6.15 s**, a beautiful 3.7x. Reversing the order INVERTED it
(threads=4 first **17.60 s**, threads=1 second **5.33 s**), so the variable was RUN POSITION, not threads:
first-touch against OneLake costs ~12–17 s that the second statement does not pay.
- **⚠ My "warm-up" was VOID: a `SELECT count(*)` on a Delta table is answered FROM THE LOG** (the
  partition-only form, §7c's own subject) and never opens a data file. Only
  `count(c_fund)+count(d_nav)+count(isin)` warms the columns the statement reads.
- ⇒ **the shape that settles it is INTERLEAVED — 1, 4, 1, 4 after a real warm-up** — because it makes position
  and thread count separable instead of confounded. A two-cell A/B on a remote store cannot do that, and both
  orders of one are not enough either: they disagree, which tells you position matters but not by how much.

**THE ONE STRONG POSITIVE, and it supersedes a stale pair of numbers:** on the SAME shape, in the SAME session,
with row counts verified, **staged `COPY INTO` is ~6x faster than TDS — 6.2–7.5 s versus 37.9–39.8 s.** The
figures this doc carried (14.5–16.1 vs 16.8–28.9 s, 2026-08-10) were a different shape and only ~1.2x apart;
they are retired. `mssql_copy_into_staging` is the single biggest lever on a lake→warehouse load, and nothing
about the parallel sink changes that.
- ⚠ Also the first live validation of that route in a while (`verify_copy_into_staging`'s positive leg is
  manual). Setup worth keeping: the staging location must be the GUID form, discovered with
  `lake.fabric.workspaces()` / `.items()` / `.warehouses()` — the last returns the SQL endpoint connection
  string, so the whole rig is discoverable from one lakehouse attach.

⇒ **§7c stands, with its scope corrected: the parallel write sink is a LOCAL / CPU-bound win.** Remove
"unmeasured remote payoff" from the open list — it is measured, and it is nil.

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

1. **Can `preserve_insertion_order=false` be dropped now? — there is now a NAMED REASON TO EXPECT NOT, found
   2026-08-22 (user-raised).** The SET's value was routing away from the SINGLE-THREADED
   `PhysicalBufferedCollector`, which `GetResultCollector` picks when `UseBatchIndex` is false. The obvious
   reading — "§5's `get_partition_data` fixed that, so the SET is redundant" — does NOT transfer, because the
   inner host query does not contain OUR scan: it contains DuckDB's `read_parquet` (which does declare a batch
   index, via `MultiFileFunction`), `duckdb_arrow_scan` views, and **a `WITH … AS MATERIALIZED` CTE**. And
   `SupportsPartitioning(BatchIndex())` **DEFAULTS TO FALSE** (`AnyRequired()` is true for `batch_index`), with
   only FOUR overriders in the whole tree — `PhysicalTableScan`, `PhysicalOrder`, `PhysicalTopN`,
   `PhysicalWindow`. A materialized-CTE scan (`PhysicalColumnDataScan`) overrides nothing, and
   `AllSourcesSupportBatchIndex()` requires EVERY source. Both the union form and the partition-join plain form
   emit exactly those CTEs. ⚠ Two things keep this a mechanism rather than a proof: whether that CTE scan is
   REACHED depends on plan shape (`GetSources()` walks only the single-child spine, and in the union form the
   CTEs are join inputs), and LOCALLY the stall does not manifest at all (§5: 1858 vs 1828 ms — unmeasurable
   either way). ⚠ Scope, checked in source since it decides the blast radius: the setting is `GLOBAL_DEFAULT`
   ("settable in both scopes but defaults to global"), so a BARE `SET` would change the whole database
   including the user's own connections — we emit `SET SESSION`, on a fresh connection per call, so it is one
   statement. Original note follows.
   **Can `preserve_insertion_order=false` be dropped now?** §5 measured it redundant on the LOCAL union repro
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
6. **PREFETCH is the idea §5f dropped and did not refute.** One pump running AHEAD of the converters is what
   would let a slow remote pull overlap with the CPU work above it — the fix only stopped the pull from
   starving OTHER pipelines. It costs a second owner of the Arrow stream and therefore the whole teardown
   design §5e budgets for, so it should be taken only once there is a remote measurement asking for it.
7. **A write sink that finds the channel full still PARKS its worker** (§7c). Handing it back needs a
   managed→host wake, because a sink has no `async_result` and so cannot use §5f's mechanism. It cannot
   deadlock — established, not assumed — so this is co-tenant latency, not correctness.
8. **An explicit `ORDER BY` write is still serial, and so is every COPY by default** (§7c). Both want the same
   unbuilt thing: `RequiredPartitionInfo() == BatchIndex()` plus a reorder buffer ahead of the channel.
9. **The waiter's backoff bounds (1 ms → 16 ms) are a judgement, not a measurement.** They were chosen so a
   fast pull never waits (the notify wins) and a 200 ms pull costs ~90 wake-ups; nothing has measured whether
   a longer cap would be cheaper or a shorter one fairer.
