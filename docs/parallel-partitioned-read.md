# Parallel partitioned reads (ConnectorX-style) — design idea, DEFERRED

> Status: **design note only — nothing built.** Captures supporting ConnectorX-style `partition_on` /
> `partition_num` parallel reads in `fabricator_query` + custom `ITableFunction`s. Builds on the existing
> arrow scan + the `daxeval` named-parameter pattern. No ABI change for the simple form (A); the full
> core-utilization form (B) needs a parallel multi-stream scan.

## Motivation

ConnectorX splits one query into N range partitions on a column and fetches them in parallel:

```python
cx.read_sql("postgresql://…", "SELECT * FROM lineitem", partition_on="l_orderkey", partition_num=10)
```

Two distinct wins, and **they are not the same** — this drives the whole design:

1. **Parallel fetch from the source.** SQL Server round-trips dominate; Arrow→DataChunk ingest is cheap. N
   concurrent range queries hide latency / saturate the source.
2. **Parallel DuckDB pipeline / core utilization.** (The key field observation.) A *single* sequential Arrow
   stream feeds a *single* scan thread, so the whole pipeline above it (filter / aggregate / join) can run
   under-parallelized — cores sit idle. Splitting the source into several `UNION ALL` branches has been seen
   to **greatly improve core usage**, because DuckDB then instantiates multiple parallel scan pipelines. The
   native equivalent is N source streams mapped to N scan threads.

## The authoring shape

A binding produces **partitions**, each its own batch stream — the proposed:

```csharp
// outer = partitions, inner = that partition's batches (one range query each)
IAsyncEnumerable<IAsyncEnumerable<RecordBatch>> Execute(TableFunctionScan scan, CancellationToken ct = default);
```

A single-partition function is just the trivial case (one inner). To avoid breaking existing
`ITableFunction`s, expose this as an **opt-in** (a sibling method / `IArrowPartitionedTableFunction`, or a
`Partitions` property) — `StaticTableFunction` / `cf_*` stay on the single-stream `Execute`.

## Two architectures (A is easy; B is the one that fixes core usage)

### A — C#-side merge → one stream (parallel FETCH only)

The framework runs the N inner enumerables concurrently (each = one partition's range query) and **merges**
them into the single `ArrowArrayStream` the existing `ArrowStreamScan` already drains:

```csharp
static IArrowArrayStream ParallelMerge(IAsyncEnumerable<IAsyncEnumerable<RecordBatch>> partitions,
                                       int maxConcurrency); // bounded channel; yields as batches arrive (order-free)
```

(Same bounded-channel pattern as `ChannelArrowStream`.) **No ABI / C++ change.** But: one merged stream → one
scan thread (`MaxThreads()=1`) → it parallelizes the *fetch*, **not** the DuckDB pipeline. So it helps
source-latency-bound queries but does **not** fix the core-under-utilization above.

### B — N streams → N DuckDB scan threads (parallel FETCH **and** pipeline)

The native equivalent of the `UNION ALL` trick: a parallel multi-stream scan whose global state holds the N
partitions as a work queue, `MaxThreads()=N`, and each per-thread local state drains one partition's stream
(Arrow→DataChunk). DuckDB then runs N-way parallel scan and parallelizes the whole pipeline (morsel-driven)
across cores — the actual fix for the idle-cores case. Cost: a new parallel scan variant (today's
`ArrowStreamScan` is single-stream/sequential) + per-partition stream marshaling across the ABI (the host asks
C# for partition i's stream per thread, or C# hands over N streams up front). This is the meaningful build.

## Partition strategies (the planner is pluggable; the executor is shared)

Range-on-a-numeric-column (ConnectorX) is only one way to slice. The real abstraction: a **planner emits a
list of disjoint, covering `WHERE` predicates**; the parallel executor (form A merge / form B N-thread scan)
is identical regardless of how they were produced. Three planners, pick by the column(s):

- **range** (ConnectorX) — an ordered/numeric `partition_on`, `partition_num` equal-width ranges
  (`col >= lo AND col < hi`). Needs a `min/max` probe; **NULLs excluded**; skew-prone on non-uniform keys.
- **hash bucket** — *any* column(s), `WHERE ABS(CHECKSUM(col1, col2)) % partition_num = i` pushed to SQL
  Server. **No probe**, exactly `partition_num` partitions, balanced (the hash distributes), and it **covers
  every row including NULLs** (the buckets are a total partition of the hash space). Often the best general
  default — works for strings/dates/composites and gives controllable, balanced N. (CHECKSUM is light but can
  skew; HASHBYTES is heavier but more uniform.)
- **distinct values** — *(this idea)* one or more columns, one partition per distinct combination:
  `SELECT DISTINCT col1, col2 FROM (<sql>)` → per combo `WHERE col1 IS NOT DISTINCT FROM v1 AND …` (NULL-safe).
  Best when the data has **logical partitions** (region / year / tenant) or the key is non-numeric/composite.
  Trade-off: **N = the distinct cardinality, not chosen** — low cardinality → under-parallel; high → thousands
  of tiny single-combo queries (overhead). Bridge to a controllable N by **bucketing the distinct combos**
  into ≤ `partition_num` groups (`WHERE (col1,col2) IN (…combos for bucket i…)`), which is really the hash
  strategy applied to the distinct set.

All three feed the same machinery: planner → predicate list → A (`ParallelMerge`) or B (N-thread scan). So a
`partition_strategy` selector (`range` | `hash` | `values`, default `hash` for arbitrary columns / `range`
for the ConnectorX-compatible numeric case) generalizes cleanly.

## Fitting it into `fabricator_query`

`fabricator_query` is the raw-query path (C++ `QueryBind` → C# scan), not an `ITableFunction`. Add two
**optional NAMED parameters** (the `daxeval` pattern — named ⇒ optional, doesn't break the 2-arg call):

```sql
-- range (ConnectorX-compatible)
SELECT * FROM fabricator_query('db', 'SELECT * FROM lineitem',
                             partition_on := 'l_orderkey', partition_num := 10);
-- hash bucket (any column, balanced, no probe)
SELECT * FROM fabricator_query('db', 'SELECT * FROM orders',
                             partition_on := 'o_custkey', partition_num := 10, partition_strategy := 'hash');
-- distinct values (multi-column, logical partitions)
SELECT * FROM fabricator_query('db', 'SELECT * FROM sales',
                             partition_on := 'region, year', partition_strategy := 'values');
```

Optional NAMED params: `partition_on` (column, or comma-list for `values`/`hash`), `partition_num`,
`partition_strategy` (`range` | `hash` | `values`; default `hash` for non-numeric, `range` for numeric). No
new ABI for the planning — `QueryBind` forwards the params; the **SqlServer backend** runs the selected
planner:

1. **Plan** the predicate list (per strategy): `range` → `min/max` probe + equal-width ranges; `hash` →
   `ABS(CHECKSUM(<cols>)) % N = i` (no probe); `values` → `SELECT DISTINCT <cols>` → per-combo NULL-safe
   equality (optionally bucketed to ≤ `partition_num`).
2. **Per partition**: `SELECT * FROM (<sql>) x WHERE <predicate_i>`, run concurrently.
3. **Consume**: form A → `ParallelMerge` to the existing single-stream scan (no ABI); form B → the N queries
   become the N partition streams a parallel scan drains on N threads.
4. Output schema probed once from `(<sql>)`, as the scan already does.

Absent the params → today's single query.

## Caveats (ConnectorX has these too)

- `partition_on` must be a **numeric/sortable column present in the query's output**, and `<sql>` must be
  subquery-wrappable.
- **NULLs in `partition_on` are excluded** by the range predicates (ConnectorX's documented behavior) —
  accept it, or add an `… OR partition_on IS NULL` tail partition.
- **Skew**: equal-width ranges can be lopsided on skewed keys (ConnectorX accepts this; NTILE/quantile
  splitting is a fancier option).
- The `min/max` probe is an extra round-trip — worth it only for `partition_num > 1`.
- A and B both leave **ORDER BY** to DuckDB (the merge / parallel scan is order-free).

## Recommendation (sequenced)

1. **Form A** (`partition_on`/`partition_num` named params + `ParallelMerge` + the SqlServer range-planner) —
   small, no ABI, wins on source-latency-bound queries.
2. **Form B** (parallel multi-stream scan, N streams → N scan threads) — the one that delivers the
   core-utilization win the `UNION ALL` experience points to. Build it when ingest/pipeline parallelism (not
   just fetch latency) is the bottleneck. It's the larger piece (parallel scan state + per-partition stream
   marshaling), and it generalizes: a custom `ITableFunction` returning `IAsyncEnumerable<IAsyncEnumerable
   <RecordBatch>>` would feed the same N-thread scan.

**Net:** A is a cheap latency win; B is the structural one that makes DuckDB actually use the cores (the
native form of the proven `UNION ALL` workaround). Same `partition_on`/`partition_num` surface for both.
