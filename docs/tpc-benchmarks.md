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
- **TPC-DS on the warehouse: q64 (47 s all remote, 94 s with the dimensions remote) and q72 (30 s)** — FIXED
  (2026-11-01, docs/duckdb-2.0-remote-execution.md §5.6): the scans reported no row count on a warehouse, so DuckDB
  planned a 1.2 billion row join. With `COUNT_BIG(*)` (Fabric answers it from metadata in ~0.1 s): q64 1.65 s / 1.38 s,
  q72 1.67 s / 0.8 s, every placement equal to the local answer.
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

## Lakehouse engines on S3 (RustFS), TPC-H SF10 (2026-11-09)

The same parquet files read as Delta and as Iceberg by several engines, from RustFS (`rustfs/rustfs:1.0.4` in the
docker rig, HTTPS, on the same machine: 20 cores, 32 GB). The question it answers: where our Delta reading stands next
to DuckDB's own Iceberg reader and two other engines, on identical data.

**Data.** The TPC-H SF10 copy of OneLake's `CH0010` (42 parquet files, 2.3 GiB, the copy the 2026-11-08 SF10 runs used)
as Delta in `s3://fabricator/tpch10/CH0010`, plus Iceberg tables over the SAME files in `s3://fabricator/tpch10ice`
(pyiceberg `SqlCatalog`, `add_files`, unpartitioned). So Delta and Iceberg read byte-identical data files, and the
comparison measures engine and metadata path only.

**Engines.** Each runs in its own process, three rounds of the 22 queries, the first cold, all with their default
thread settings (all cores). The queries are `sql/tpch.sql` of `lakehouse_benchmark` with only the table names
rewritten per engine.
- DuckDB 2.0 with fabricator: `ATTACH 's3://fabricator/tpch10' (TYPE fabricator, PROVIDER 'delta' | 'engineeredwooddelta', schemas 'onelevel')`.
  Native reader and codec engine, both with their default `pushdown_filters`.
- DuckDB 2.0 `iceberg_scan` over each table's metadata file.
- Polars 2.1.0, `scan_delta` (deltalake 1.6.6) and `scan_iceberg` (the table loaded through the pyiceberg 0.12.0
  catalog, the data files read by Polars itself).
- LakeSail 0.7.3 (`pysail`, Spark Connect), `format("delta")` / `format("iceberg")` loads registered as temp views.

**Correctness.** Every query of every engine and round matches a reference: DuckDB 2.0 over the same files locally,
compared as multisets with numbers within 0.02 absolute or 1e-6 relative.

**Seconds, sum over the 22 queries:**

| engine | round 0 (cold) | round 1 | round 2 |
|---|---|---|---|
| DuckDB `iceberg_scan` | 15.0 | 14.6 | 13.8 |
| Polars Iceberg | 22.3 | 20.6 | 21.1 |
| Polars Delta | 25.9 | 26.1 | 26.6 |
| Sail Iceberg | 32.0 | 32.1 | 33.6 |
| Sail Delta | 33.8 | 33.5 | 32.7 |
| fabricator codec engine | 38.2 | 35.8 | 36.7 |
| **fabricator native reader** | 44.9 | 45.0 | 44.4 |
| reference: DuckDB on the local files, one round | 17.3 | | |

**Per query, the better of the two warm rounds:**

| query | DuckDB Iceberg | Polars Iceberg | Polars Delta | Sail Iceberg | Sail Delta | fab. codec | fab. native |
|---|---|---|---|---|---|---|---|
| Q1 | 0.55 | 1.20 | 2.23 | 2.00 | 1.93 | 2.12 | 3.81 |
| Q2 | 0.21 | 0.47 | 0.53 | 0.33 | 0.40 | 0.53 | 0.66 |
| Q3 | 0.40 | 0.77 | 0.92 | 1.14 | 1.17 | 1.44 | 1.57 |
| Q4 | 0.24 | 0.50 | 0.51 | 0.56 | 0.55 | 0.74 | 1.06 |
| Q5 | 0.50 | 0.95 | 1.10 | 2.16 | 2.08 | 1.81 | 1.50 |
| Q6 | 0.20 | 0.56 | 0.73 | 0.75 | 0.72 | 1.43 | 1.01 |
| Q7 | 0.51 | 1.11 | 1.20 | 1.25 | 1.21 | 1.89 | 1.60 |
| Q8 | 0.58 | 1.11 | 1.28 | 2.01 | 2.02 | 2.21 | 1.90 |
| Q9 | 1.19 | 1.68 | 2.36 | 3.08 | 3.06 | 3.23 | 3.60 |
| Q10 | 0.81 | 1.07 | 1.21 | 1.56 | 1.56 | 2.30 | 2.08 |
| Q11 | 0.13 | 0.12 | 0.18 | 0.32 | 0.24 | 0.26 | 0.42 |
| Q12 | 0.26 | 0.54 | 0.81 | 0.76 | 0.80 | 1.19 | 1.38 |
| Q13 | 0.87 | 0.86 | 1.03 | 1.04 | 0.97 | 1.89 | 2.10 |
| Q14 | 0.40 | 0.74 | 0.84 | 0.93 | 0.92 | 1.33 | 0.81 |
| Q15 | 0.26 | 0.60 | 0.89 | 1.74 | 1.73 | 1.32 | 0.73 |
| Q16 | 0.28 | 0.18 | 0.18 | 0.37 | 0.47 | 0.37 | 0.41 |
| Q17 | 1.67 | 1.76 | 1.99 | 2.84 | 2.88 | 2.96 | 3.84 |
| Q18 | 0.80 | 1.17 | 1.25 | 2.94 | 2.97 | 1.26 | 1.88 |
| Q19 | 0.46 | 0.73 | 1.26 | 1.44 | 1.47 | 2.35 | 2.17 |
| Q20 | 0.71 | 1.12 | 1.64 | 1.58 | 1.53 | 1.56 | 2.16 |
| Q21 | 2.09 | 2.40 | 2.50 | 2.59 | 3.20 | 2.76 | 5.24 |
| Q22 | 0.35 | 0.36 | 0.36 | 0.36 | 0.35 | 0.51 | 0.54 |
| **sum** | **13.5** | **20.0** | **25.0** | **31.7** | **32.2** | **35.5** | **40.5** |

**What it says:**
- **Our native Delta reader is three times slower than DuckDB's Iceberg reader** on the same files, in the same DuckDB,
  with the same join filters. The gap is widest on plain scans: Q1 3.81 against 0.55 s, Q6 1.01 against 0.20, Q12
  1.38 against 0.26. It is the transport measured on 2026-11-07 (commit `abcbd5d`, which already moved the inner
  query's Arrow export onto DuckDB's own producer threads): our scan takes its rows out of an inner DuckDB query as Arrow and imports them into the outer plan again, where
  Iceberg plans `read_parquet` into the query. Planning the Delta read as `read_parquet` directly (an optimizer pass,
  the same technique as the SQL Server subtree pushdown) is the lever.
- **The codec engine beats the native reader here** (35.5 against 40.5 s, warm). It has no second transport and wins
  where `lineitem` is read more than once (Q17, Q18, Q21) and on Q1; it loses on Q5–Q8, Q10, Q14, Q15, Q19.
- DuckDB Iceberg's warm rounds beat the local reference because DuckDB's external file cache serves the S3 bytes from
  memory after the first round.
- Polars reads Iceberg faster than Delta (20.0 against 25.0 s); Sail is about equal on both and slowest on Q5, Q15 and
  Q18.
- The 2026-11-08 SF10 runs on MinIO (round 2: Iceberg 14.0 s, native 41.0 s, codec 39.6 s) agree with these.

**Caveats.** One machine, the store local in Docker, so network latency plays almost no part. Three rounds. Engines at
their defaults: no thread or memory tuning, Sail with join reordering on (as in `lakehouse_benchmark`). The Rust
clients (Polars, Sail) read with `allow_invalid_certificates`, because rustls refuses the rig's self-signed CA:TRUE
certificate (docker/README.md).

**Rerun.** The harness is local and gitignored: `scratchpad/rustfs-bench/` with `upload.py` (data to RustFS + Iceberg
metadata), `bench.py <engine> [rounds]` (one engine per process, results to `results/<engine>.json`) and `compare.py`
(sums and the correctness check). The Python side is a uv venv there (polars, deltalake, pysail, pyspark-client,
pyiceberg[sql-sqlite], s3fs, duckdb 2.0.0); the DuckDB engines use `build/release-v2/duckdb.exe`.
