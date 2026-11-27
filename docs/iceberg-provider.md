# A potential Iceberg provider: where engineered-wood stands

Status: **notes, nothing built, nothing decided** (2026-11-27, user-asked: "festhalten für einen potentiellen
Iceberg-Provider"). Written from reading engineered-wood `upstream/main` at `32dabeff` against our pin
`feda5cdd`; no code of ours was run against it.

## 1. What engineered-wood has

There are two unrelated things called Iceberg in engineered-wood (EW). Keep them apart:

| | where | what it does |
|---|---|---|
| **`EngineeredWood.Iceberg`** | its own project, already in our pin (40 files there, 59 on `upstream/main`) | A **metadata-only** Iceberg library. Depends on Core, Avro and Expressions; **not** on Parquet and **not** on Delta. Its own plan says "Nothing in it reads or writes data files." |
| **`IcebergCompat` (V1-V3)** | `EngineeredWood.DeltaLake.Table` | The Delta table feature. It only ENFORCES the constraints (column mapping, row tracking, partition values materialized in the data files). It writes **no** Iceberg metadata (the code says "UniForm, whose metadata this library does not maintain"). Unchanged between our pin and `32dabeff`. |

What the Iceberg library does on `upstream/main` (Phase 0 of EW's plan, merged 2026-11-25/26):

- Table metadata JSON for v1-v3 round-trips without loss, including fields it does not model. Format version 4
  and encrypted tables are refused (`UnsupportedTableFeatureException`).
- The manifest codec reads manifests of ANY writer (Spark, Iceberg Java, pyiceberg, iceberg-go), v1-v3, by
  field id, and writes the spec's manifest schema per format version (#586). Before it refused every manifest it
  had not written itself.
- Column bounds are decoded for scan pruning (UUID bounds deliberately not, because engines disagree on their
  order); commits write manifest-list partition summaries.
- `FileSystemCatalog` commits are optimistic and atomic (create-if-absent), where they used to overwrite
  `vN.metadata.json` unconditionally. Catalogs: in-memory and Hadoop-layout file system; no REST client yet.
- Snapshot expiry, refs, time travel.
- Checked in both directions against iceberg-go, plus a committed Spark 4.1 / Iceberg 1.12 corpus.

Still missing, per EW's own README and plan: applying partition transforms to values, delete-file matching in
planning, any data-file read or write, REST catalog. The README still says: **"Do not use it to write tables
other engines will read."**

## 2. EW's plan (`doc/iceberg-v3-plan.md`, status "proposal, 2026-11-25")

| phase | issue | content |
|---|---|---|
| 0 | #953 (#586) | safe and lossless metadata, oracles (**done** as of `32dabeff`) |
| 1 | #954 | a correct v1-v3 READER: transforms, delete matching by sequence number, DVs, defaults, schema per snapshot |
| 2 | #955 | v2 + v3 WRITER and append, REST catalog |
| 3 | #956 | row-level DML: deletion vectors (v3), position deletes (v2), copy-on-write |
| 4 | #957 | maintenance and breadth |
| 5 | #958 | niche v3 features |
| UniForm | #490 | after phase 2: an Iceberg snapshot generated from each Delta commit |

Decided there: data reading and writing go into a NEW package `EngineeredWood.Iceberg.Table`, beside the
metadata-only one; the writer produces v2 and v3 (per table); REST catalog is in scope.

Two facts in the plan matter for us:

- **A file-backed Delta deletion vector is byte-identical to an Iceberg v3 `deletion-vector-v1` blob** (same
  magic, same Roaring64 portable layout, same length + CRC framing); only the container differs. So UniForm
  needs no DV rewrite: an Iceberg entry can point at the Delta `.bin` (offset = the descriptor's offset,
  size = `sizeInBytes + 8`). Inline (Z85) DVs have no file and would need a Puffin file or an inline threshold
  of 0 on IcebergCompatV3 tables.
- Delta row tracking maps onto Iceberg row lineage; `IcebergCompat` already uses the reserved field ids.

## 3. What this would mean for fabricator

Three different products, not one. Decide which one is wanted before building anything:

1. **An Iceberg catalog provider** (`ATTACH … (TYPE fabricator, PROVIDER 'iceberg')`), the analogue of the Delta
   provider on EW's table layer. Needs EW phase 1 for reads and phase 2 for writes; DML needs phase 3. On our
   side it would reuse the `ITableDefinition`/`ITable`/`ITransaction` model
   ([catalog-table-abstraction.md](catalog-table-abstraction.md)) and the host file system
   ([filesystem-bridge.md](filesystem-bridge.md)) as the Delta provider does.
2. **Delta tables readable as Iceberg (UniForm)**, as an option of the existing Delta provider. Needs EW #490,
   which comes after phase 2. No new provider, but every Delta commit of ours would also have to produce the
   Iceberg metadata, so it is a write-path change with a cost per commit.
3. **Nothing of ours: DuckDB's own `iceberg` extension.** It reads Iceberg and attaches REST catalogs; what it
   writes on DuckDB 2.0.1 is **not checked here**. A fabricator provider is only worth building for what that
   extension cannot do, and that list does not exist yet: measure the extension on 2.0.1 first (read, write,
   DML, catalogs, OneLake/`abfss://`, S3) before proposing option 1.

Points to establish before any of it (none measured):

- Which Iceberg tables users actually have: Fabric/OneLake (shortcuts, table format virtualization), Snowflake,
  S3 + REST catalogs (Polaris, Glue, Unity). This decides catalog support more than anything in EW.
- Whether the read path should go through EW (Arrow boundary, as the codec Delta read) or hand DuckDB a file
  list (duckdb-iceberg's shape, "form (b)" in [multifile-delta.md](multifile-delta.md)). The 2026-08-18
  measurement there found the native Parquet read about 3x faster than our Arrow boundary for the same scan.
- EW's projection by NESTED field id is a listed gap in Parquet (only top-level today); a reader needs it for
  schema evolution of nested columns.
- Deletes on the read path. Our file scan (`src/fabricator/fabricator_file_scan.cpp`, the duckdb-iceberg shape) takes
  deleted positions per file from the provider; it "knows nothing about Delta", so an EW Iceberg plan can feed it.
  v3 deletion vectors are byte-identical to Delta's and fit that path. v2 POSITION-DELETE files are parquet
  `(file_path, pos)`: instead of EW decoding them into a position list, duckdb/duckdb#29441 (`filter_query` in a
  file's open options, OPEN on `main` as of 2026-11-27) would let DuckDB read them itself, in parallel — its own
  example is exactly that query. Watch it; see [delta-read-path.md](delta-read-path.md) §The file scan / Limits.

## 4. When to look again

- EW #954 merged (phase 1 reader): a read-only spike becomes possible, ideally compared with DuckDB's
  `iceberg` extension on the same tables.
- EW #955 merged (writer, append, REST): writes and catalogs.
- EW #490 (UniForm): option 2 above.
- duckdb/duckdb#29441 merged (and the line on 2.1): the position-delete route above.
- Each EW bump: read `doc/known-issues.md` (its Iceberg section is where EW lists what is still wrong) and
  re-check the README's "Do not use it to write tables other engines will read" line.

## 5. The same EW range, for the next bump (not Iceberg, recorded here because it was read together)

- No change in `EngineeredWood.DeltaLake*`.
- `DecimalNarrowing.CopyNarrowed` gained a fast path (Decimal128 to INT32/INT64, little-endian) that is ALSO on
  the normal writer path (`ColumnChunkWriter` → `ToInteger`), i.e. our codec writes of decimals with precision
  up to 18. Read and looks right (sign check, zero under NULL); it is a parquet-writer change, so it is a
  data-integrity question for the tiers at the bump.
- `BufferedParquetWriter` was reworked heavily; neither we nor EW's Delta code use it.
- `LocalTableFileSystem.ReadAllBytesAsync` retries Windows sharing/lock violations (errors 32/33) for about
  0.5 s (EW #960 measured about 1 read in 300 failing while eight writers race).
- Expressions: a `TimestampNanos` literal kind (additive).
