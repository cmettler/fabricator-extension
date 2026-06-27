# MultiFileReader + engineered-wood — a faster Delta path (design idea, DEFERRED)

> Status: **design note only — nothing built.** Captures the idea of integrating DuckDB's `MultiFileReader`
> framework with the pure-C# Delta reader (engineered-wood) so DuckDB's native parquet reader does the
> reading while engineered-wood supplies the Delta snapshot. Builds on the validated filesystem bridge
> ([docs/filesystem-bridge.md](filesystem-bridge.md)) + host-query ([docs/host-query.md](host-query.md)).

## Today: `arrownet_delta_scan` (the spike, working)

engineered-wood (C#) does **everything**: parse `_delta_log` → snapshot → file list, read the parquet files,
apply its own partition pruning / file skipping / deletion vectors, materialize Arrow. All IO goes through
DuckDB's `FileSystem` via the host callbacks (so local / `az://` / `s3://` / `https://` + DuckDB secrets all
work). Simple + proven, but the read is **C#-side parquet → materialized Arrow** — slower, no real pushdown,
buffers the result.

## The idea: let DuckDB read, engineered-wood describe

This is exactly the architecture of DuckDB's **own `delta` extension**: a custom `MultiFileList`
(`DeltaMultiFileList`) whose files + per-file metadata come from **delta-kernel-rs**, plugged into
`MultiFileReader` + DuckDB's native parquet reader. The idea here is to **swap delta-kernel-rs (Rust) for
engineered-wood (C#)** — keeping a pure-managed Delta-log implementation (no native delta binary,
C#-extensible) while gaining DuckDB's fast, parallel, pushdown-capable parquet reader.

**Framing correction:** `MultiFileReader` is a **C++** framework (the machinery behind `read_parquet` /
`read_csv` and the `delta` / `iceberg` extensions). C# can't *wrap* it. The integration runs the other way —
a **C++ table function uses `MultiFileReader`, and engineered-wood (C#) feeds it the Delta snapshot**.
engineered-wood becomes a *metadata / file-list provider*, not the reader.

## Responsibility split

| Concern | Today (`arrownet_delta_scan`) | With `MultiFileReader` |
|---|---|---|
| `_delta_log` → snapshot → file list + partition values + stats + deletion vectors + schema/column-mapping | engineered-wood (C#) | engineered-wood (C#) — **kept** |
| Read parquet → Arrow | engineered-wood (C#) | **DuckDB native parquet reader (C++)** |
| Projection / filter pushdown, file skipping, partition columns, schema union | engineered-wood's own pruning | **`MultiFileReader` (C++)** |
| Deletion-vector application | engineered-wood | `MultiFileReader`'s per-file deletion-filter hook (as the delta ext does) |

The win: drop C#-side parquet reading + materialization for DuckDB's vectorized, parallel, pushdown-capable
reader; get projection/filter/partition pushdown for free.

## What gets re-engineered in engineered-wood

1. **Metadata-only mode.** Today it reads; here it must expose a snapshot **without reading data** — the file
   list + each file's partition values, byte stats (for stats-based skipping), deletion-vector reference, plus
   the table schema + column mapping. Its parquet→Arrow path is bypassed.
2. **Hand pruning to `MultiFileReader`.** engineered-wood does its own partition pruning today; in this model
   `MultiFileReader` owns pushdown (it holds the pushed filters + parquet stats + partition values), so
   engineered-wood supplies the *full* list + stats and lets `MultiFileReader` skip. **Avoid double-pruning.**
3. **A snapshot ABI.** Per scan, C# marshals the file list + metadata to the C++ `MultiFileList` — an Arrow
   batch of `{path, partition_values, deletion_vector_ref, stats}` is the natural carrier (reuses the bridge's
   Arrow boundary). The parquet IO continues through the host `FileSystem` callbacks (already validated).

## The genuinely hard parts (most of the risk)

- **Deletion vectors** — row-level deletes without rewriting files. The delta ext applies them as a per-file
  row filter via a `MultiFileReader` hook; engineered-wood would supply the DV (or the resolved deleted-row
  bitmap) per file and the application happens C++-side. The fiddliest piece.
- **Column mapping** (name/id mapping: physical parquet names ≠ logical names) — `MultiFileReader`'s
  column-mapping must be driven from engineered-wood's metadata.
- **`MultiFileReader` API churn** — it has evolved across DuckDB versions; the `delta` extension is the moving
  reference to track (the integration is coupled to internal-ish C++ APIs).

## A lighter middle-ground (reuses host-query)

Before the full `MultiFileReader` path, a cheaper integration uses **`host_query` + named inputs** (see
[docs/host-query.md](host-query.md)): engineered-wood parses the log → file list, then a host query runs
`SELECT … FROM read_parquet([<files>])` (DuckDB reads with full pushdown). Partition columns added via the
query; **deletion vectors** passed as a C#-provided Arrow input (deleted row-ids) and anti-joined. Not as
clean as a native `MultiFileList` (DV-as-anti-join is coarser than per-file filtering; less precise file
skipping), but a **days-scale spike** that already reuses existing plumbing — a way to measure the
"DuckDB parquet reader vs engineered-wood C# read" win before committing to the `MultiFileReader` work.

## Recommendation (sequenced; build only on demand)

1. **Spike the `host_query` + `read_parquet` middle-ground** — cheap, reuses this codebase's host-query +
   filesystem bridge, yields a real perf number.
2. If the win justifies it, build the **`MultiFileList`-backed** integration (engineered-wood → snapshot
   provider; DuckDB → reader + pushdown), porting the delta ext's deletion-vector + column-mapping handling.

**Net:** sound idea, proven shape (it's the delta-ext architecture), real perf/capability upside — but the
hard 20% (deletion vectors, column mapping, tracking the `MultiFileReader` API) is most of the cost. The
existing `arrownet_delta_scan` (materialized C# read) stays the simple, working baseline meanwhile.

## Why engineered-wood at all (vs DuckDB's `delta` extension)

DuckDB's `delta` extension already does the full thing (delta-kernel-rs + `MultiFileReader`). engineered-wood's
value is **pure-managed** Delta-log handling: no native delta-kernel-rs binary (cross-platform without the
Rust dependency), a C#-extensible log/snapshot layer that fits this repo's C#-centric bridge, and a path to
future write support. This integration keeps that managed log layer while borrowing DuckDB's reader.
