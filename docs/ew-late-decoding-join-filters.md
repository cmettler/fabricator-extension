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
