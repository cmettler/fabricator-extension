# Join filters on the codec engine, and late decoding in engineered-wood

> Notes to pick up later (2026-11-09). **Nothing here is open work in our code.** The next step is upstream:
> [engineered-wood #502](https://github.com/clast-project/engineered-wood/issues/502). This file keeps what we
> measured, what we built and removed, and what we would need, so nothing has to be worked out again.
> The as-built join-filter work is in [duckdb-2.0-remote-execution.md](duckdb-2.0-remote-execution.md) §5.8.

## 1. Where it stands

- **Native engine** (`PROVIDER 'delta'`, `native_read`): a hash join's runtime filter reaches the inner `read_parquet`
  query as `fabricator_join_filter(<id>, col)` plus the integer key range beside it, and bloom filters go through the
  same function. DuckDB's own Parquet reader then does everything below for free: row-group pruning on the range,
  late decoding per 2048-row vector (§4). SF10 local 42/43 → 34/36 s.
- **Codec engine** (`PROVIDER 'engineeredwooddelta'`) with `pushdown_filters 'all'`: since 2026-11-09 the catalog
  declares `superset_filter_pushdown`. The filter tree carries a join filter as its keys or their range, and
  engineered-wood prunes files, row groups and pages with that. The host then re-applies every filter to each chunk,
  natively and on every scan thread. The per-batch host query (`HostBatchFilter`) is gone from this path.
- **TPC-H SF10 on MinIO, codec engine, sum of round 2:**

  | mode | sum |
  |---|---|
  | `'static'` (default, no join filters in the scan) | 39.6 s |
  | `'all'` with the per-batch host query (before 2026-11-09) | 62.2 s |
  | `'all'` re-applied in C++ (today) | 43.0 s |
  | `'all'` re-applied + the C# lookup below | 46.7 s |
  | Iceberg, for comparison | 14.0 s |
  | Delta native | 41.0 s |

- **Why join filters barely help the codec engine:** engineered-wood decodes every projected column of every row
  range that survives pruning. Filtering afterwards only saves what comes after the decode. TPC-H keys are not
  clustered, so the key range prunes almost no row groups.

## 2. What was built and removed: the C# lookup (ABI v101, never committed)

The idea: let the codec engine ask DuckDB's join filter itself, per batch, from C#.

- **Host service** `join_filter_lookup(client_context, id, ArrowArrayStream *keys, uint8_t *out_match, row_count, err)`,
  appended to `FabricatorHostServices`. The key column goes over as Arrow C data, nothing is serialized. The host
  moves the stream out, reads it with `ArrowStreamReader` in chunks of up to 2048, and answers through
  `JoinFilterMatch`, the same core `fabricator_join_filter` uses (`LookupKeys`, or the key hash and `LookupHashes`).
  An unknown id, a key of another type or no client context answers 1 for every row: a join filter may only widen.
- **Filter tree:** for a provider declaring capability `join_filter_lookup`, the scan registered the filter object and
  added a `lookup` node `{"op":"lookup","id":N,"col"|"path":...}` beside the keys or range (`FilterNode.Id`).
- **C#** (`DeltaJoinFilter`): collected the AND-reachable lookup nodes and evaluated each as the engineered-wood call
  `fabricator_join_filter_<id>(col)` with `ArrowRowEvaluator` and its own `IFunctionRegistry`. It ANDed the packed
  bitmaps and filtered the batch with `ArrowCompute.Filter`. This ran before the per-batch host query.
- **Traps found building it, worth keeping:**
  - ⚠ The id rides the function NAME. As a literal argument the evaluator expanded it into a column as long as the
    batch, which cost more than the lookup itself (20M rows: 542 ms against 118 ms in the host, 148 ms with the id
    in the name).
  - ⚠ The service must be patched onto the services block BEFORE `RegisterProviderSecrets`, the first call that boots
    the bridge. The bridge copies the block at boot, so a later patch is never seen, and the C# filter then silently
    found no service.
  - The lookup stays out of engineered-wood's read predicate (`DeltaReadOptions.Filter`), which is also the
    transaction's read set for conflict detection and cannot evaluate a host function.
- **Measured:** the lookup itself costs 130–150 ms per 20M integer keys and 280–390 ms for strings. It filtered
  correctly, but end to end it was slower. It runs serially on the scan's one managed producer stream, while the C++
  re-apply runs natively on every thread: 46.7 s with it, 43.0 s without (Q2 2.25 vs 0.76 s, Q5 3.76 vs 1.89 s).
- **Archive (LOCAL ONLY, gitignored, no safety net):** `scratchpad/ew-join-filter-v101/`, containing:
  - `v101_bridge.patch` (Abi, HostFileSystem incl. `BorrowedKeysStream`, `FilterNode.Id`, version)
  - `v101_cpp.patch` (abi.h, clr_host)
  - `DeltaJoinFilter.cs`
  - `arrow_ingest_removed.cpp.txt` (the host function, the `lookup` nodes, the capability plumbing)

  The registry, `JoinFilterMatch` and `fabricator_join_filter` it builds on are still in `arrow_ingest.cpp`.

## 3. What would make it pay: engineered-wood late decoding (#502)

#502 is open. Curt Hagenlocher turned it into the umbrella for exact row-level filtering, in three parts:

1. **Two-pass read:** per row group, decode the filter columns, evaluate the mask, turn it into `RowRange`s, then decode
   the other columns only over those ranges. The building blocks exist: `GetCandidateRowRangesAsync` (page index) and
   `ReadRowRangesAsync`, which reads only the pages of the ranges.
2. **Skipping unselected values inside the decoders,** instead of a full decode followed by a gather. Per the issue,
   prototyped and measured 2026-10-22.
3. **Answering the first pass from the dictionary** for column-vs-constant predicates.

The issue names the hard parts itself:
- adaptivity (a non-selective mask makes the second pass a loss; parquet-rs and DataFusion had regressions)
- positions (deletion vectors, row ids, `_ew_row_address` must be masked by the same selection)
- nested columns, batch sizing, and errors that must surface as they would in a post-filter

**What our use needs from it:**

- **A mask hook with its columns**, next to the predicate:
  `(IReadOnlyList<string> Columns, Func<RecordBatch, BooleanArray> Mask)`. The reader decodes `Columns` first, calls
  the mask, and decodes the rest only for passing rows. Several join filters mean several columns and an AND inside
  the delegate. The issue first proposed exactly this delegate shape; the later comment wants the reader to see the
  `Predicate` too, for part 3. Both can coexist.
- **Not in `Filter`:** `Filter` is the read set (above). The key range stays a plain predicate there, and the mask is a
  separate option that only removes rows.
- **Superset semantics are fine:** a bloom filter has false positives; the join and the C++ re-apply check again.
- **Threads and context:** engineered-wood reads row groups in parallel. Take the scan's client context into the
  delegate's closure rather than relying on the ambient (AsyncLocal) opener.
- **Part 2 is what matters for joins, not part 1.** Join filters are selective but scattered. In TPC-H q8 about 24k of
  lineitem's 60M rows survive. At ~20k rows per page (engineered-wood's `DataPageRowCountLimit` default; DuckDB-written
  files differ, not measured) that is ~8 matches per page, so page skipping saves almost nothing. As a rule of thumb,
  with λ matches per page the share of pages that can be skipped is e^−λ: about half at λ = 0.7, nothing at λ = 8.

A comment for #502 describing this is drafted in `scratchpad/upstream/ew_502_join_filter_mask.md` (gitignored, not
posted; posting is the user's call).

## 4. Reference: DuckDB's late decoding, and when it does not happen

Read in our pin's `duckdb/extension/parquet/parquet_reader.cpp` and `column_reader.cpp`:

- **Per 2048-row vector:** filter columns first, in adaptive order. A vector with no match skips the other columns
  (`Skip`, no decode). Otherwise the rest is decoded with `Select`.
- **`DirectSelect` (skip unselected values while decoding)** happens only for PLAIN encoding and for strings, and only
  when the vector lies within one page. Dictionary-, RLE- and delta-encoded columns are decoded whole, then selected.
- **No late decoding** when the predicate is not pushed into the scan. DuckDB pushes per-column filters only;
  expressions over several columns (`a + b > 5`, an OR across columns) run in a FILTER above the scan, and every
  column is decoded.
- **IO is separate (remote files):** `lazy_fetch` fetches the filter columns first and the rest only for row groups
  with matches. It applies only with a NON-optional filter. Join filters arrive in optional wrappers, so a scan that
  only a join filter restricts fetches every column's bytes; the decode is still late. The first row group uses a
  heuristic (the filter columns' share of the bytes), later ones the observed match rate.

Other engines, from memory and not verified: open-source Spark's vectorized Parquet reader prunes row groups and
pages but decodes all projected columns. Photon, Velox (Fabric's native engine via Gluten), arrow-rs `RowFilter`
(DataFusion, delta-rs) and DuckDB decode late.

## 5. Related, not built: DuckDB's late materialization on the codec engine

This is a different mechanism. DuckDB rewrites `ORDER BY … LIMIT n` / `LIMIT` with a filter into two scans: a narrow
one (filter and sort columns plus rowid), then a SEMI JOIN on rowid back to a full scan, whose rowid filter must be
cheap. We enable it only with `exact_filter_pushdown` (native engine): `DeltaRowIdFilter` turns the rowids into a file
selection plus `file_row_number`. The codec path does not read a rowid filter, so the second scan would decode the
whole table. To enable it there, the codec read would first have to turn a rowid filter into file and row-group
selection (or row ranges, which `ReadRowRangesAsync` would take).

## 6. When #502 lands: checklist

1. Bump engineered-wood. Run the recorded sweep: public-surface diff of the option records, `doc/known-issues.md`.
2. Rebuild the host service and the `lookup` node from the archive (ABI bump, service patched before the bridge boots).
3. Pass the mask through the new read option, not through `Filter`; capture the client context in the closure.
4. Measure SF10 on MinIO against the 43.0 s baseline, and a clustered-key case where page skipping should show.
5. Gates: the codec section of `verify_delta_catalog_dynamic_filter` (answers + the probe scan's row count from the
   profile), plus a mutant that lets every row through the mask.

## 7. Built and measured on `exp/ew-row-masks` (2026-11-11)

engineered-wood implemented #502 on `feature/row-level-filtering` (pinned at `53b8821`, not merged to `main` at the
time). Its shape is what §3 asked for: `DeltaReadOptions.RowMasks`, a list of
`DeltaRowMask(IReadOnlyList<string> Columns, Func<RecordBatch, BooleanArray> Mask)` with `ValueWise` for asking a
dictionary instead of rows. The masks run inside the row-group reads and never see a row a deletion vector deletes.

**What we built (ABI v102 on that branch; on the main line it is ABI v103, since v102 is `scan_next_unit` there):** the archive of §2 came back, with the mask in place of the C# post-filter.
- Host service `join_filter_lookup` (as §2), registered at load before the bridge boots.
- Capability `join_filter_lookup` (codec engine only; `FABRICATOR_JOIN_FILTER_MASKS=0` turns it off). The scan's
  filter tree then carries a `lookup` node per prefix-range filter (beside its keys or range) and per bloom filter.
- `DeltaJoinFilter.Masks` turns the top-level lookups into `DeltaRowMask { ValueWise = true }`, with the scan's client
  context in the closure. A lookup on a struct member stays with the host's re-apply.
- Gate: `verify_delta_catalog_dynamic_filter` reads, from the log, how many rows the READ returned (the host's re-apply
  keeps the scan's output the same either way). The mutant that lets every row through dies there.

**⚠ The first measurement showed nothing, and the reason was ours.** engineered-wood reads in two passes (the mask's
columns first, the other columns only over 8192-row blocks that hold a selected row) only for a file that reports
`IReadConcurrencyHint.PreferredConcurrentReads`. Without it a file counts as local: one pass, and at most
`min(4, cores)` files and row groups at once. Our `DuckDbRandomAccessFile`, `OneLakeRandomAccessFile` and their
filesystems reported nothing, so every S3/OneLake codec read was a local read. They report 4 now
(`RemoteReadConcurrency`, `FABRICATOR_EW_READ_CONCURRENCY`; 0 reports nothing). 4 keeps the parallelism as it was and
changes only the two-pass decision: 8 and 16 made full and scattered scans 10-40 % SLOWER against local RustFS.

**Measured, TPC-H SF10 on RustFS, best warm sum of 22 queries, all results checked against the reference:**

| codec engine, `pushdown_filters 'all'` | sum |
|---|---|
| before (`fc4a434`, EW `3dadb69`) | 41.6 s |
| EW `53b8821`, masks off | 40.5 s |
| masks on, no concurrency hint | 44.7 s |
| masks on, hint 4 | **36.7 s** |
| `'static'` (no join filters), hint 4 | 35.0 s |
| masks on, hint 4, V2 layout (below) | **34.8 s** |
| Delta native (control): before / after / V2 layout | 13.4 / 13.4 / 11.2 s |

Largest per-query gains (masks off -> on): Q19 2.46 -> 1.45 s, Q10 2.42 -> 1.53, Q9 2.93 -> 2.17, Q7 2.63 -> 2.19.

Micro test on lineitem (six columns, warm, hint 4): a join whose matches lie in blocks of consecutive `l_orderkey`
(0.8 % of rows, spread over the whole key range) 3.55 -> 2.10 s and CPU 9.4 -> 3.1 s; scattered matches (0.5 %) wall
unchanged 4.4 s, CPU 12.5 -> 6.8 s. Scattered matches touch every page, so every page is still fetched and
decompressed; §3's e^-λ rule holds.

**The benchmark's layout (parquet-rs 60.0.0, ZSTD):** lineitem 9 files, 36 row groups of ~1.67M rows, sorted by
`l_orderkey`; key columns in RLE_DICTIONARY overflowing into PLAIN (the 1 MB dictionary page limit). A V2 copy
(`s3://fabricator/tpch10v2`, our native writer, `parquet_version 'V2'`, same order and row-group sizes) writes the
integer keys as DELTA_BINARY_PACKED: `l_orderkey` 47 -> 11 MB, `l_partkey` 224 -> 165 MB, `o_custkey` 56 -> 41 MB,
lineitem 1612 -> 1393 MB; `l_suppkey` (100k distinct) stays a dictionary. Both engines got 8-16 % faster on it.

Open: raise the hint on a real remote store (OneLake) after measuring; ask EW whether two passes could be decided by
something other than the concurrency hint.

## 8. On the main line, with EW `main` (2026-11-14)

The masks are committed on `feat/codec-read-path` (ABI v103), on engineered-wood `main` `c1574081`, which carries #502.
The concurrency hint and the codec's file cache came first, in their own commit
([delta-read-path.md](delta-read-path.md#the-codec-engines-remote-reads-2026-11-14)).

**They pay only together with a larger read budget.** TPC-H SF10 on RustFS with the host file cache, best warm sum of
22 queries, three independent runs each, all results correct:

| codec engine, `pushdown_filters 'all'` | masks off | masks on | peak working set |
|---|---|---|---|
| defaults (4 reads, `MaxBufferedReadBytes` 256 MiB) | 24.6 s | 27.1 s | ~9 GB |
| 16 reads, 2 GiB | 24.9 s | **23.1 s** | ~13-14 GB (one run 18.5 GB) |

- At the default budget the masks cost 10 %. A masked row group is read in two passes, and EW reserves the larger pass
  (#846), so 256 MiB admits even fewer row groups at once.
- With 2 GiB and 16 reads they save 7 %, and the larger budget alone (masks off) saves nothing.
- On OneLake the runs scattered more than the differences (defaults 62.8 / 45.9 s, 16 reads and 2 GiB 40.8 / 44.5 s).
- DuckDB itself needs about 9 GB at SF10 (its hash tables), and the larger budget adds about 4.5 GB.

So the masks are on by default (the codec engine is not the main provider), and the budget stays at EW's default
until engineered-wood's ordered scan admits work differently (the #861 review). Measurement knobs:
`FABRICATOR_EW_READ_CONCURRENCY` (the hint), `FABRICATOR_EW_BUFFERED_READ_BYTES` (the budget),
`FABRICATOR_JOIN_FILTER_MASKS=0` (masks off). Re-measure with each EW bump.

