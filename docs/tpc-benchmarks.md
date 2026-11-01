# TPC-H / TPC-DS across local DuckDB and a remote target

The benchmark queries with their tables split between local DuckDB and a remote target. Every query of every placement
must return what the all-local run returns; the timings are the second output.

## What exists

| piece | what | where |
|---|---|---|
| `test/verify_tpch_mssql.test` | 22 queries × 4 placements at SF 0.01, box SQL Server, service tier | generated |
| `test/verify_tpcds_mssql.test` | 99 queries × 4 placements at SF 0.01, box SQL Server, service tier | generated |
| `scripts/gen_tpc_suites.py` | writes both suites; the expected results are the local answers (`rowsort`, the runner compares numbers approximately) | |
| `scripts/tpc_bench.py` | the harness for timings and larger scale factors; targets `box`, `warehouse`, `lakehouse-delta`, `lakehouse-sql` | not CI |
| `test/verify_pushdown_edges_mssql.test` + `scripts/pushdown_edge_cases.py --emit` | 139 SQL edge cases mined from substrait-java's semantics tests, pushed vs DuckDB | generated |

Placements: all remote; the facts remote (`lineitem`, `orders`, `partsupp` / the `*_sales`, `*_returns`, `inventory`
tables); the dimensions remote; a fixed random split. Each is a schema of views over either the local or the remote
copy, so the benchmark text runs unchanged (`PRAGMA tpch(n)` resolves the names through the search path).

`tpch` and `tpcds` are statically linked (`extension_config.cmake`): a test build reports a dev version, for which the
extension repository has nothing, and a `require` that cannot load SKIPS the suite.

⚠ `tpc_bench.py` keeps one remote schema per benchmark and scale factor (`tpch_sf0_01`, …): one shared schema once
mixed SF1 rows into an SF0.01 run, which reads exactly like a flood of wrong answers.

⚠ The Fabric targets read credentials from gitignored files (`dbt_mssql_test/profiles.yml`, `dax_secret.sql`) and never
print them; they consume Fabric capacity.

## Results (2026-10-31)

### Correctness

| target | SF | runs | not ok |
|---|---|---|---|
| box SQL Server, TPC-H | 0.01 | 110 | 0 |
| box SQL Server, TPC-DS | 0.01 | 495 | 0 |
| Fabric Warehouse, TPC-H | 0.01 | 110 | 0 |
| Fabric Warehouse, TPC-DS | 0.01 | 495 | 0 |
| Fabric Lakehouse via the Delta engine, TPC-H | 0.01 | 110 | 0 |
| Fabric Lakehouse via its SQL analytics endpoint, TPC-H | 0.01 | 110 | 0 |

The benchmarks found no wrong answer at SF 0.01. The edge-case differential did, twice: `NOT EXISTS` dropped rows with
a NULL key on every fabricator table (a MARK join's condition rewritten to `=`), and `ROLLUP` / `CUBE` lost the grand
total on an empty input (SQL Server returns no row for any `GROUP BY` there). Both fixed — docs/duckdb-2.0-remote-
execution.md §5.1 and §4.24.

### Timings, seconds summed over all queries of a run

| target | local | all remote | facts remote | dims remote | random 1 | random 2 |
|---|---|---|---|---|---|---|
| box, TPC-H SF 0.01 | 0.2 | 3.9 | 1.5 | 0.4 | 1.2 | 6.2 |
| Warehouse, TPC-H SF 0.01 | 0.2 | 15.2 | 3.5 | 2.3 | 4.1 | 4.0 |
| Lakehouse SQL endpoint, TPC-H SF 0.01 | 0.2 | 13.6 | 3.2 | 2.3 | 3.4 | 3.8 |
| Lakehouse Delta engine, TPC-H SF 0.01 | 0.2 | 50.1 | 27.2 | 23.5 | 29.9 | 29.9 |
| Warehouse, TPC-DS SF 0.01 | 2.3 | 129.2 | 14.0 | 115.2 | 23.2 | 24.6 |

At SF 0.01 a remote run measures round trips, not computation. Notes:

- **TPC-H SF1 on box, all remote, found two catastrophes**, both fixed: q20 did not finish in 300 s and q16 took 43 s
  (DuckDB: 0.03 / 0.08 s; q20 hand-written in T-SQL on the same index-less tables: 0.28 s). A mark join's three-valued
  CASE kept SQL Server from planning a semi join; a WHERE on the marker now renders the bare EXISTS / NOT IN — 7.6 s and
  3.8 s (docs/duckdb-2.0-remote-execution.md §4.25). The rest of q20's gap is its `LIKE 'forest%'`, a range on the
  binary string key computed per row.
- **The Delta engine is ~3.7× slower than the lakehouse's SQL endpoint at SF 0.01**: every query opens each table's log
  on OneLake. Not investigated further.
- **The lakehouse's SQL endpoint needed 153 s** after the Delta write before it showed every table with its full count
  (the harness polls; a stale-but-consistent snapshot reads like wrong data).
- **TPC-DS on the warehouse: q64 (47 s all remote, 94 s with the dimensions remote) and q72 (30 s)** dominate. Not yet
  analysed — the same method as q16 / q20 applies: the pushed T-SQL from the `Fabricator.Remote` log against the query
  hand-written in T-SQL.
- The SF1 TPC-H run on box was stopped at q20 for the fixes; the placements are measured below.

### TPC-H SF1 on box, all placements (2026-11-01)

Correct in every run (110 of 110). Seconds summed over the 22 queries; all local: 1.0 s. The three columns are the
same queries against the same rows, loaded four times: three heaps with a different declared width for the text columns, then `nvarchar(200)` as
clustered columnstore (`mssql_default_table_type`, which `scripts/tpc_bench.py` sets now):

| placement | heap, `nvarchar(max)` | heap, `nvarchar(4000)` | heap, `nvarchar(200)` | columnstore, `nvarchar(200)` | columnstore, + key lists |
|---|---|---|---|---|---|
| remote | 38.5 | 37.0 | 28.0 | 21.8 | 11.8 |
| facts-remote | 612.1 (q10 timed out at 300) | 374.7 | 126.7 | 65.2 | 25.3 |
| dims-remote | 20.2 | 18.8 | 4.5 | 5.1 | 4.4 |
| random1 | 608.0 (q10 timed out) | 372.9 | 124.5 | 62.4 | 25.0 |
| random2 | 510.8 (q10 timed out) | 332.6 | 119.1 | 54.3 | 24.7 |

The last column is the same data after `6408792` (a declined part's children pushed, the DuckDB division settings),
`8392a7f` (long key lists as one JSON parameter) and `196dbbf` (a join's keys read back from DuckDB's prefix-range
filter, table variables on box) — all at DuckDB's default `dynamic_or_filter_threshold`. In facts-remote: q8 7.3 →
1.3 s, q17 8.9 → 0.09 s, q5 6.6 → 1.0 s, q3 3.7 → 0.3 s, q18 5.0 → 1.5 s, q21 11.4 → 5.3 s, q10 1.7 → 0.2 s.

- **The declared text width was most of it**, and on the server, not on our side (docs/duckdb-2.0-remote-execution.md
  §4.26): a max value goes through the LOB path however short, and a parallel plan pays for the DECLARED width — 1.48M
  one-character `l_returnflag` rows through `sqlcmd` alone: 49 s as `nvarchar(4000)`, 1.2 s as `nvarchar(100)`. q10
  > 300 → 183 → 4.6 s, q21 168 → 76 → 32 s, q19 49 → 24 → 0.85 s, q12 (random2) 95 → 31 → 0.83 s. Our CTAS creates
  `nvarchar(max)` unless `mssql_default_varchar_length` is set; `scripts/tpc_bench.py` loads with 200 (the longest TPC
  value is 198 characters).
- A subtree fix found on the way: a pushed part returned a filter column nothing above reads; it goes out as NULL now
  (§4.26, isolated q10 part 19 → 0.53 s).
- **A heap without keys or indexes is no fair baseline.** Columnstore cut the lineitem-heavy queries by two to three:
  q9 29 → 9.4 s, q21 32 → 11 s, q3 12 → 3.7 s, q7 7.9 → 2.7 s, q10 4.6 → 1.6 s (facts-remote). Part of the heap cost
  was SQL Server's parallel plan returning many rows: q9's lineitem scan (6M rows back, only a range filter from the
  local `part`) took 28 s at the server's default degree of parallelism (20 cores, `max degree of parallelism` 0),
  7.8 s at `MAXDOP` 1, 2 or 4, 14 s at 8; a selective scan (1138 rows) went the other way, 0.11 s parallel vs 0.49 s
  serial — so a blanket `MAXDOP 1` would be wrong.
- **q8 all remote 7.65 → 0.24 s** after the run: its final DOUBLE division has no exact translation, and the subtree
  pass gave up on the whole plan instead of pushing the aggregate below it (docs/duckdb-2.0-remote-execution.md §4.27).
- **Open with the key lists**: q9 ~9 s (lineitem's part keys come from a coarse bitmap with more candidates than the
  cap, so only ranges reach the server), q21 ~5 s (its EXISTS / NOT EXISTS scans of lineitem get no keys), q3 in
  random2 3.2 s, q18 ~1.5 s. Before the key lists (columnstore column): q8 ~7.3 s where lineitem is remote and `part`
  local (DuckDB joins them first, so no server-only part holds lineitem and it comes back unfiltered); q21 ~11 s and q9 ~9 s where lineitem is remote and other tables are local; q17 8.9 s and q5 6.5 s in
  facts-remote; q18 ~5 s; q13 ~4 s.
