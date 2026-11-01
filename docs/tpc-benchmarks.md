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
same queries against the same rows, loaded three times with a different declared width for the text columns:

| placement | `nvarchar(max)` | `nvarchar(4000)` | `nvarchar(200)` |
|---|---|---|---|
| remote | 38.5 | 37.0 | 28.0 |
| facts-remote | 612.1 (q10 timed out at 300) | 374.7 | 126.7 |
| dims-remote | 20.2 | 18.8 | 4.5 |
| random1 | 608.0 (q10 timed out) | 372.9 | 124.5 |
| random2 | 510.8 (q10 timed out) | 332.6 | 119.1 |

- **The declared text width was most of it**, and on the server, not on our side (docs/duckdb-2.0-remote-execution.md
  §4.26): a max value goes through the LOB path however short, and a parallel plan pays for the DECLARED width — 1.48M
  one-character `l_returnflag` rows through `sqlcmd` alone: 49 s as `nvarchar(4000)`, 1.2 s as `nvarchar(100)`. q10
  > 300 → 183 → 4.6 s, q21 168 → 76 → 32 s, q19 49 → 24 → 0.85 s, q12 (random2) 95 → 31 → 0.83 s. Our CTAS creates
  `nvarchar(max)` unless `mssql_default_varchar_length` is set; `scripts/tpc_bench.py` loads with 200 (the longest TPC
  value is 198 characters).
- A subtree fix found on the way: a pushed part returned a filter column nothing above reads; it goes out as NULL now
  (§4.26, isolated q10 part 19 → 0.53 s).
- **Open, width-independent**: q9 ~29 s and q3 / q4 ~12 s wherever lineitem is remote and other tables are local, q21
  ~32 s in facts-remote / random1 / random2, q8 7–8 s (random2 q8 was 0.24 s in the first run and 6.9 s in both later
  ones — not yet explained), q17 ~9 s.
