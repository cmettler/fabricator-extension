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

## 5. Above the scan: a streaming result over `UNION ALL` can lose ~100 s in the scheduler

The largest concurrency effect measured on this codebase is not in the scan at all. On a remote root, a
STREAMING result × `PhysicalUnion` × `threads>1` × a slow filesystem stalls. Bisected through
`fabricator_host_query` (our identical inner-query machinery, nothing of ours above it): a 198-file plain
branch alone ran in **7.0 s**, and adding ONE CONSTANT branch — `UNION ALL SELECT CAST(0 AS BIGINT)`, no CTE,
no second file — took it to **111.2 s**; the same at `SET threads=1` collapsed to **6.0 s**; and
`EXPLAIN ANALYZE` of the slow shape reported **3.38 s of operator work inside a 114 s statement**. Locally
every op is instant, so the gap never opens (0.17 s).

- **It is the PARALLELISM SPLIT, not oversubscription.** `external_threads=2` changed nothing;
  `threads=16, external_threads=16` (zero internal workers) ran it in 97.0 s at 0.72 s user CPU — pure idle —
  while `threads=1` (also zero workers, but split 1) ran the same shape in 6.0 s. The only difference between
  those two is `NumberOfThreads()`, i.e. the pipeline split.
- **The protocol, read from DuckDB's source rather than guessed:** producers sink into
  `PhysicalBufferedCollector` → `SimpleBufferedData`
  ([src/main/buffered_data/simple_buffered_data.cpp](../duckdb/src/main/buffered_data/simple_buffered_data.cpp));
  a full-buffer producer parks in `blocked_sinks` and is woken only by the consumer. The consumer, when no task
  is available, enters `Executor::WaitForTask`
  ([src/parallel/executor.cpp](../duckdb/src/parallel/executor.cpp)) — a **20 ms timed poll** on
  `task_reschedule`, a condition variable signalled by task RESCHEDULES and **never by a chunk arriving in the
  buffer** — with an immediate-return (spin) branch when the collector is blocked. Hence the two observed faces
  of one protocol: spin (102 s user CPU) or idle (4.5 s user), both losing wall clock because nothing wakes the
  consumer on data arrival. `SET streaming_buffer_size='64MB'` changed nothing, which is what rules out
  back-pressure as the driver.
- **Largely defused by `SET SESSION preserve_insertion_order=false`**, which every batched Delta statement now
  carries (`BatchPlan.Statement`,
  [dotnet/Fabricator.Delta/DeltaNativeReader.cs](../dotnet/Fabricator.Delta/DeltaNativeReader.cs)): the minimal
  union went **94.5 s → 7.5 s** and a real remote union scan **44.7 s → 13.7 s** cold. Mechanism:
  `PhysicalResultCollector::GetResultCollector` branches on `PreserveInsertionOrder`, and for a plan with no
  `ORDER BY` that is decided by the setting
  ([src/execution/physical_plan/plan_insert.cpp](../duckdb/src/execution/physical_plan/plan_insert.cpp)).
  ⚠ It is correctness-NEUTRAL there rather than a tuning knob — that reader's contract is already that row
  order across files is not preserved — and **SESSION scope is load-bearing and was verified**: the setting's
  declared target is `GLOBAL_DEFAULT`, so a bare `SET` would change the whole database, including the user's
  own connections.
- The upstream patch shape this suggests: `SimpleBufferedData::Append` (or the collector's sink) should signal
  the consumer's wait. Not filed.

---

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
input, machine or build. Gate: [test/verify_plugin.test](../test/verify_plugin.test) (25, service tier).

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

1. **The batch-size default.** Removing engineered-wood's one-file-per-batch coupling (§3), or adding a
   consumer parameter to the `host_query` ABI, would let a scan take the measured ~20% while a writer keeps its
   own granularity. Nothing is built.
2. **The union form's remote gate** is now a RANKING, not a pathology (union 13.7 s cold / ~1.05 s warm against
   the full form's 3.9 s / ~1.53 s), and the cold residue is UNATTRIBUTED — it is not props fetches, which are
   zero on both forms.
3. **`FABRICATOR_DELTA_PREFETCH` stays at 1.** The per-file loop is sequential by default, so a remote scan
   that falls to it pays N round trips in series. Raising it has never been measured against the current read
   path, and it is what made a fixed-name bound-input view collide (now per-query names).
4. **The `GROUP BY` regression of §2 is unexplained beyond "merge-bound".** If it ever matters, the lever is
   DuckDB's aggregate, not our scan.
