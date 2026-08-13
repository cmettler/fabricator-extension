# Delta snapshot caching — holding a `DeltaTable` open across ABI calls

**Status: the cache IS BUILT (§0.4) — as a per-transaction `DeltaTable` cache, which REVERSES §5/§6's recommendation for one reason §5 never weighed: it needs nothing from engineered-wood. §0 has the profile. Two CHEAPER fixes found by
taking that measurement are built too, and the three together cut the profiled query **291.06 s → 77.19 s**
(−73%), with the log replayed ONCE instead of five times and no engineered-wood change at all.** Two prerequisites were already done and shipped (§2). Read §0, §4 and §7 before
starting work: examining the design four times changed what it should be.

⚠ **§5 and §6 below are now HISTORY and their recommendation is INVERTED — read §0.4 first.** They argue
for caching the immutable `Snapshot` and against caching a live `DeltaTable`, on the grounds that the
snapshot dissolves the disposal, dangling-opener and thread-safety problems. Every one of those points is
still TRUE; what they omit is that a `Snapshot` cannot be turned back into a `DeltaTable` without an
additive engineered-wood `FromSnapshot` factory, and therefore a fork branch we deliberately do not have.
The table cache needs nothing upstream, and §0.4 records how each of the three objections was handled.
§5 remains the cleaner endgame if `FromSnapshot` ever lands.

---

## 0. THE PROFILE — taken 2026-08-13, and it settles §7.1

`SELECT c_fund, d_nav, isin FROM lake.dbo.his LIMIT 1` against a Fabric lakehouse over `abfss://`
(`native_read`, the shipped default). The table is at **v1850**: 89 active files, 1851 commit JSONs, 18
checkpoints at interval 100, newest at v1799; the table has **49 columns**.

One `delta fs <path>` Debug line is emitted per `TableFileSystems.Create`, i.e. per `DeltaTable.OpenAsync`,
so the log counts opens exactly (the §1 method). Both columns below are MEASURED, same session, same table.

| span | what | before | after |
|---|---|---|---|
| **open 1** | `get_metadata kind=2` — the bind's column fetch | 26.9 s | 20.1 s |
| **open 2** | bind probe: `GetSchemaAndVersion` at latest → seeds the pin at v1850 | 25.7 s | 24.0 s |
| **open 3** | bind probe: `ListNativeScanFiles` AT v1850 → active=89 | 46.9 s | **gone** |
| — | bind probe: `BatchPlan.Build` + `ProbeSchema` `LIMIT 0` over all 49 columns | 38.4 s | **gone** |
| **open 4** | real scan: `GetSchemaAt` v1850 | 47.9 s | **26.0 s** |
| **open 5** | real scan: `ListNativeScanFiles` AT v1850 | 48.1 s | **25.3 s** |
| — | real scan: `BatchPlan.Build` + `ProbeSchema` `LIMIT 0` over 3 columns | 37.7 s | 34.4 s |
| — | run the batched `read_parquet` and take the row | 19.4 s | ~16 s |
| | **TOTAL** | **291.06 s** | **146.58 s** |

Before: **5 snapshot builds ≈ 195 s (67%)**, 2 schema probes ≈ 76 s (26%), the read ≈ 19 s. So §1.1's warning
("this is a COUNT, not a profile") is discharged — the redundant opens are **tens of seconds each**, not the
"tens of milliseconds" under which §7 said to close the item. After the two fixes below: **−49.6%**, same row,
with 2 of the 5 builds gone and the surviving AT-version ones halved.

### 0.0 ⚠ THE `LIMIT` NEVER REACHES THE SCAN — a THIRD finding, not fixed here

The generated SQL is `SELECT … FROM read_parquet([… 89 files …])` with **no `LIMIT`**, so `LIMIT 1` reads the
whole batched list and discards it above the scan; that is the last ~16 s of the profile and it is what the
user's original "a LIMIT 1 should break the pipeline early" was about. `ScanSpec.Top` exists and carries
DuckDB's pushed bare LIMIT, but **its only consumer is `SqlServerBackend`** (as T-SQL `TOP n`) — no Delta
reader reads it. Appending `LIMIT n` to the batched query (and per file in the loop form, where the union is
then a superset that DuckDB re-limits) is the obvious fix. Deliberately NOT bundled into this pass.

### 0.1 ⚠ AN AT-VERSION OPEN IS **TWO** SNAPSHOT BUILDS — this was not in the doc, and it is why the pin costs

Compare the rows: an open **at latest** costs ~26 s, an open **AT v1850** ~47–48 s. The reason is structural,
not incidental — `DeltaReader.ResolveSnapshotAsync` was

```csharp
var table = await DeltaTable.OpenAsync(fs, …);          // builds the LATEST snapshot
var snap  = await table.GetSnapshotAtVersionAsync(v, …); // builds it AGAIN at v
```

and on the ordinary read path **v IS the latest**, because the pin is seeded from exactly such an open. So
the mechanism that exists to give a statement ONE consistent cut was roughly DOUBLING the cost of every open
it touched.

**FIXED (2026-08-13, C#-only, no EW change): return `table.CurrentSnapshot` when its `Version` equals the
requested one.** Provably equivalent rather than usually right — `SnapshotBuilder.BuildAsync` computes
`targetVersion = atVersion ?? listing.LatestVersion`, so with `atVersion == LatestVersion` both calls select
the same checkpoint and replay the same commit range, and a Delta version is immutable. **⚠ And it is
UPSTREAM'S OWN RULE**: `DeltaTable.ResolveReadSnapshot` is literally
`options.AtVersion is { } v && v != CurrentSnapshot.Version ? null : CurrentSnapshot`. Our `Stream*At` paths
already got it for free by passing `AtVersion` into `ReadAsync`; `ResolveSnapshotAsync` was the one place
that duplicated the resolution without it.

### 0.2 ⚠ THE BIND-TIME SCHEMA PROBE WAS RUNNING A FULL SCAN SETUP — 85 s for a stream nobody reads

Rows 3 and 4 above are the PROBE listing every active file and building a `read_parquet` over all 49 columns,
purely to answer "what are the column types". `PopulateReturnSchema` calls `get_schema` and releases without
ever pulling a batch.

**FIXED (2026-08-13, C#-only): `DeltaCatalog.ScanTable` short-circuits a `schema_only` spec into
`SchemaProbe`**, which resolves the schema by the same precedence the two read paths use and returns an empty
stream. ⚠ **It still SEEDS the pin** — that is load-bearing, not leftover: the probe runs in the statement's
own transaction, and if it described the table at latest while seeding nothing, a concurrent ALTER between
bind and execute would give the plan one schema and the scan another.

**⚠ SOUNDNESS HAD TO BE MEASURED, because the native reader does NOT trust the Delta schema.**
`DeltaNativeReader.ProbeSchema` runs a `LIMIT 0` against a real data file and advertises DuckDB's own types,
falling back to the userSchema-derived form only when there is no file to probe. If those two ever disagreed
in TYPE, the plan and the batches would disagree — the read-past-the-end (SIGSEGV) class. An env-gated
self-check (`FABRICATOR_DELTA_PROBE_CHECK=1`, shipped like the `Fabricator.Memory` marks and for the same
reason — the conclusion rests on the shapes one tier happens to reach) compared them on **every real scan of
the whole hermetic tier: 1303 scans, 1015 identical, 288 differing, and 0 TYPE differences.** The 288 are
`ProjectFor`'s documented "requested set resolves to no user column" fallback — 479 columns present only in
the derived form (a `count(*)`-via-rowid plan projects the rowid alone, so `ProjectFor` falls back to the
full schema) and 57 only in the advertised one (the row-tracking virtual columns, which are not in the user
schema at all). **Projection, never type — and the probe carries no projection.**

**Then re-run on the SERVICE tier, because the hermetic one is local filesystem only and a reader that
derives types from a probed file is exactly where remote storage could differ: 50/50 — 2028 green, 62 more
scans, 61 of them against `s3://`, and again 0 TYPE differences.**

⚠ Scope, stated rather than implied: local + `s3://` are covered; **`abfss://` is not**, because no CI tier
reaches it (`verify_delta_catalog_adls` is manual/live-account). That residue is why the check ships behind
an env var instead of being deleted.

⚠ **METHOD — the comparison itself gave a WRONG answer first, and the shape is worth remembering.** Diffing
the two rendered schemas over a CRLF log reported **254 type differences** whose two sides printed
IDENTICALLY (`Int64Type != Int64Type`), because the last value on each line carried a trailing `\r`. Printing
the differing entries with `repr()` — rather than believing a count — is what showed the diff was in
invisible characters. **A textual diff that reports a difference it cannot show you is measuring your
tooling.**

### 0.4 THE CACHE IS BUILT — and it caches the TABLE, not the `Snapshot` (2026-08-13)

**291.06 s → 77.19 s, and the statement now replays the log ONCE instead of five times.**

| stage | time | snapshot builds |
|---|---|---|
| baseline | 291.06 s | 5 |
| + §0.1 AT-version fix, + §0.2 `schema_only` probe | 146.58 s | 4 |
| + `DeltaTableCache`, four read sites | 101.26 s | 2 |
| + the fifth read site (see below) | **77.19 s** | **1** |

**⚠ IT CACHES THE `DeltaTable`, REVERSING §5/§6's RECOMMENDATION — and the reason is a factor §5 never
weighed: caching the table needs NOTHING from engineered-wood.** A `DeltaTable` can only be constructed by
`OpenAsync`/`CreateAsync`/`OpenOrCreateAsync`, so serving a cached `Snapshot` needs the additive
`FromSnapshot` factory and therefore a fork branch, which we deliberately do not have. Keeping the table we
already opened costs nothing upstream. §6's four objections were re-checked against the CURRENT pin and only
one survived:

- **Thread safety** — `Snapshot` is immutable (11 `{ get; init; }`, zero setters); `DeltaTable` is not. But
  all 10 assignments to `_currentSnapshot` are in the ctor, `RefreshAsync` or a commit/write path — **none on
  a read path**. Keep the cache READ-ONLY and nothing mutates. ⚠ Still an invariant upstream has never
  promised: re-check it at every bump.
- **Disposal** — NOT the "lease threaded through 6 async iterators" §6 claims. The codebase already draws the
  line (35 `OpenAsync` vs 34 `DisposeAsync`; helpers that take a `DeltaTable` parameter never dispose), so it
  is a boolean at the open sites, and getting it wrong throws `ThrowIfDisposed()` LOUDLY.
- **A leaked cached table** — a GC-able object, not a resource leak: `Dispose()` only sets `_disposed`.
- **The opener — THE ONE THAT SURVIVED.** `DuckDbTableFileSystem.Opener` prefers the `AmbientOpener` and
  keeps the constructed value as a fallback, which its own comment documents as safe *"because no object
  outlives its call today … and becomes load-bearing the moment something is cached"*. A cached table is
  exactly that. Handled by building its filesystem with **opener 0**
  (`TableFileSystems.Create(…, outlivesThisCall: true)`) so the ambient is the only source and its absence
  fails loudly instead of dereferencing a dangling `ClientContext*`.

**⚠ THE FIFTH READ SITE WAS FOUND BY INSTRUMENTING, NOT BY READING.** Four sites took the query to 2 opens
and the last one would not collapse. A Debug line recording the txn id on every cache MISS showed only ONE
miss against TWO opens — so the other open never reached the cache at all. It was
`GetSchemaAndRowTracking`, which serves `MetadataKind.Columns`; the four had been listed from memory instead
of grepped from the callee, which is the error CLAUDE.md already records. **11 further read-only opens share
the same shape and are deliberately NOT wired** — unmeasured gain, and each one widens the verification
surface. `SetTablePropertiesAsync` and `ComputeSchemaChangeAsync` must never be wired: sharing is sound only
for readers.

**⚠ CATALOG ENUMERATION IS THE ONE SHAPE WHERE THE CACHE IS PURE COST, and the first version got it wrong.**
`information_schema.tables` / `duckdb_tables()` materialise EVERY table, one `MetadataKind.Columns` crossing
each — so wiring that crossing made one statement retain a `Snapshot` per table, and a snapshot holds
`ActiveFiles`: an `AddFile` per data file with its `Stats` JSON, partition values and tags. Enumeration
touches each table exactly ONCE, so all of that retention buys no reuse, on the path CLAUDE.md already
records as the expensive one on OneLake. Bounded by `MaxTablesPerTxn = 32`, which DECLINES rather than
evicts (an entry at the cap is likelier to be re-read than a newcomer) — a query re-reads a few tables ~4
times each, a listing touches hundreds once each, and the cap sits between. `Publish` therefore returns
whether it cached, and the caller derives "shared" from THAT rather than from `txn != 0`; deriving it from
the transaction would leave a declined table undisposed.

**Invalidation is deliberately COARSE** — `InvalidateReadCache()` at the head of the nine mutating entry
points drops the whole transaction's cache. Over-invalidating costs one re-open; under-invalidating is a
silent stale read, and keying on "this call can change something" avoids having to keep the immediate-commit
list (CREATE OR REPLACE / DROP / OPTIMIZE / VACUUM / identity create / partition overwrite) in sync forever.
Note the ordinary staleness question is ALREADY answered by the snapshot pin — within a transaction every
pinned read resolves to one version anyway — so this guards the paths that do NOT consult the pin.

**Gates: hermetic 69/69 — 7078. MUTATION-TESTED with two mutants, both killed:**
- **Disable the invalidation** → killed by `verify_delta_catalog_transactions` §2917 and
  `verify_delta_catalog_column_mapping` §142, which are the SAME shape and the one it exists for: a DDL
  rename followed by a read in the same transaction. The first is the **dbt table-swap**
  (`CREATE …__dbt_tmp; RENAME m → m__dbt_backup; RENAME __dbt_tmp → m; SELECT count(*) FROM m`), where the
  new `m` resolves to the old `m`'s path and the cache serves the pre-swap table; the second renames a COLUMN
  and reads it by its new name. Seven other mutating suites SURVIVED, which is worth stating: the invalidation
  is load-bearing for the rename shapes specifically, not for mutation generally.
- **Dispose a SHARED table** → killed by all four suites tried, at the FIRST read of each
  (`Cannot access a disposed object`). So sharing is the normal path, not an edge case.

### 0.5 The better fix for enumeration, NOT built: a schema-only read in engineered-wood

The cap bounds the damage; it does not remove it. The shape that would is an EW entry point that reads a
table's SCHEMA without materialising its file list:

```csharp
public static ValueTask<(Apache.Arrow.Schema ArrowSchema, Schema.StructType Schema, long Version)>
    ReadSchemaAsync(ITableFileSystem fileSystem, long? atVersion = null, CancellationToken ct = default)
```

Two facts make it cheap rather than a rewrite. A checkpoint is **parquet with one top-level struct column per
action type** (`txn`, `add`, `remove`, `metaData`, `protocol`, `domainMetadata`, `commitInfo`), and
**`ParquetFileReader.ReadAllAsync` already takes `IReadOnlyList<string>? columnNames`** —
`ReadParquetCheckpointBodyAsync` simply omits it, so today it fetches everything including `add`, which is
one row per data file carrying its stats JSON. Projecting `["metaData", "protocol"]` skips those bytes for
real (column-projected parquet, not just less CPU), and `ActiveFiles` is never built at all.

⚠ **What it would NOT buy, and the measurement to take first:** the post-checkpoint commit tail is JSON and
is read whole either way — on the profiled table that is 51 commits after v1799, and **the split of the ~26 s
build between the checkpoint parquet and that tail is UNMEASURED**. If the tail dominates, the win is much
smaller than it looks. It also does not help a real SCAN, which needs `ActiveFiles` regardless — so it
COMPOSES with the table cache (enumeration takes the cheap path, a query takes the full open and reuses it)
rather than replacing it. A timestamp variant is not the cheap case: resolving one needs the commit
timestamps, i.e. walking versions.

Good offer shape by this file's own criteria — we need it, it is self-contained, and "read a table's schema
without materialising its file list" is what any engine doing catalog listing wants.

### 0.3 What is left, and what it is NOT

MEASURED after both fixes: **146.58 s** (the table above). What is left is one column fetch (20.1), one
probe schema open (24.0), the real scan's two opens (26.0 + 25.3), one `LIMIT 0` (34.4) and the read (~16).
A snapshot cache (§5) would collapse the four opens to one: **~85 s**.

⚠ **The residual `ProbeSchema` `LIMIT 0` is ~38 s and is NOT a snapshot problem** — it is O(active files)
remote parquet FOOTER reads (89 files here), so no amount of log caching touches it. Separate item, separate
key.

⚠ **None of this is a Delta-log-length problem you can fix by caching either**: a single build of this log
costs ~26 s, and `LogCleanup` (shipped) bounds the log only for tables whose `delta.logRetentionDuration`
has passed. The user-side levers remain `delta.checkpointInterval` (100 → 10 shortens the replay tail),
`OPTIMIZE` (fewer active files ⇒ a cheaper `LIMIT 0` probe) and retention.

---

## 1. The problem

`DuckDbTableFileSystem` used to capture the per-call host-FS opener, so a `DeltaTable` could not be held
open across ABI calls. Every table REFERENCE therefore costs **4 snapshot constructions per statement**,
and it is dead linear in references. Measured 2026-07-29 on a local codec catalog:

| statement | snapshot constructions |
|---|---|
| `SELECT sum(id) FROM t` (steady state) | **4** |
| `SELECT … FROM t a JOIN t b` (self-join) | **8** |
| three references to `t` in one statement | **12** |
| `INSERT INTO t SELECT … FROM t a JOIN t b` (autocommit) | **10** (8 scan + 2 write) |
| the same INSERT inside `BEGIN … COMMIT` | **13** |

Decomposed and confirmed as **2 `ScanTable` calls per reference** (the bind-time `spec == null` schema probe
plus the execution) **× 2 opens per call** (the schema fetch, then the stream or the file listing).

Each open costs a `_delta_log` LIST — which `ExternalFileCache` does **not** serve, because it caches file
CONTENT ranges and not listings — plus the commit/checkpoint reads and the replay CPU. On OneLake and S3 the
repeated listing is the part that hurts.

**Method** (repeatable): every `DeltaTable.OpenAsync` is preceded by `TableFileSystems.Create`, so ONE
temporary debug line there counts opens exactly. A second Delta table scanned between the statements under
test delimits them in the log, since its opens appear under its own path. Revert the probe after measuring.

### 1.1 ⚠ This is a COUNT, not a profile

The table above counts operations. **Nobody has measured what those redundant opens cost in wall-clock** on
OneLake or S3. That distinction matters, because the evidence we do have points elsewhere: the Fabric
notebook's in-session work went **305 s → 15 s** from two *different* fixes, and the dominant one was
`HostFsGlob` doing an open + `GetFileSize` per matched file (**258 s → 2 s**). That was the real bottleneck
and it is already fixed. Calling the remaining 4-per-reference "the biggest remaining perf item" was an
inference from the count, and it should not be repeated without a profile.

### 1.2 ⚠ Mind which engine you are measuring

The same `PROVIDER 'delta'` text selected the **codec** before the native-defaults flip (2026-07-29) and
selects **native** after it. A comparison spanning that change silently swaps the labels. That is how "the
codec did more snapshot reads" came to be believed; the recorded table and a re-measurement both say the
opposite — native cost one MORE until the `+1` was removed (§2).

---

## 2. Prerequisites — both DONE

**`SnapshotPinning.Release` is wired** (2026-07-29). It existed and was **never called**. The only
reclamation was `InstantFor`'s panic `Txns.Clear()` at 4096 entries, and since one autocommit statement is
one transaction id that threshold arrives routinely — wiping the pins of transactions still IN FLIGHT, after
which an explicit transaction re-captures a NEW instant on its next scan and starts seeing a concurrent
writer's commits mid-transaction (a silent snapshot-isolation violation, not reproducible on demand).
`DeltaCatalog.CommitTransaction`/`RollbackTransaction` now Release UNCONDITIONALLY, before their
`tables is null` early return — a READ-ONLY transaction is exactly the one that pins and had no other exit.
No ABI or C++ change: both are `TransactionManager` overrides that already call into C# for every
transaction, reads included. **This is also the disposal hook any cache would hang off.**

**The opener is resolved per call** (2026-07-30, `142b350`). The FS reads `AmbientOpener.Current` and keeps
the constructor value as a FALLBACK — the ambient is an `AsyncLocal` and reads 0 wherever the execution
context did not flow, and there the captured pointer is still correct because nothing outlives its call yet.
Behaviour-preserving today: all 46 construction sites pass `Opener()`, which just returns the ambient. Also
verified that `DuckDbRandomAccessFile` uses its opener ONLY in the constructor and stores just the host file
handle, so the FS was the ONLY lingering capture.

**Why the opener came first:** a cached `DeltaTable` whose FS holds a stale `ClientContext*` is a
**use-after-free**, not a staleness bug, and neither Windows nor glibc would necessarily fault on it — the
same asymmetry that hid the late `ArrowProducer` stream release everywhere except macOS, where Apple's
`pthread_mutex_lock` validates the signature and threw. Lifetime and disposal are resource hygiene; the
opener is memory safety.

**The `+1` is gone** (2026-07-29). The measurement's "native_read costs one MORE than the codec" was the
autocommit native path resolving its pin through `ResolveVersionAsOf` — a whole extra `DeltaTable` open plus
a timestamp→version scan of the commit timestamps, to learn a version the listing open already knew.
`ScanNative` now consults an existing pin first and otherwise seeds from the schema fetch
(`DeltaReader.GetSchemaAndVersion`, one open, replacing the `GetSchema` it would have called anyway — the
same zero-IO trick `ScanCodec` uses). Proof is by ABSENCE: the retired helper logs unconditionally in both
its success and catch branches, so no `delta snapshot pin … as-of` line means it was not called.

Explicit transactions deliberately KEEP the instant-based resolve: every table there must pin to ONE instant,
including one first touched late, which a per-table-latest seed cannot provide.

---

## 3. Findings that constrain any design

### 3.1 Read-your-writes is NOT at risk

It does not come from the `DeltaTable` snapshot. On the codec path `ScanCodec` composes the pinned base
stream with the `DeltaTxnBuffer` overlay (pending files concatenated, pending deletes excluded, a pending
ALTER's schema advertised); on the native path pending files join the `read_parquet` loop. A table pinned at
the **base** version is exactly what that overlay composes against. So caching is compatible with
read-your-writes by construction.

### 3.2 A shared `DeltaTable` is read-safe — but the guarantee is FRAGILE

EW's `DeltaTable` has mutable state (`_currentSnapshot`, `_disposed`) and **zero** locks or `Interlocked`,
which reads as disqualifying. But all **11** assignments to `_currentSnapshot` are in WRITE/commit paths or
the explicit `RefreshAsync` — ctor, `RefreshAsync`, `CommitMetadataOnlyAsync`, `SetClusteringColumnsAsync`,
`Set`/`RemoveDomainMetadataAsync`, `CommitOccAsync`, `CommitWriteAsync`, `CommitDataFilesAsync` ×2,
`CompactAsync` — and **none in a read path**. The helpers reads go through (`TransactionLog`,
`DeletionVectorReader`, `CheckpointReader`) are STATELESS, holding only a readonly `_fs`.

⚠ A grep for "mutable private fields" on those three hits **method declarations** — false positives. Trusting
that scan would wrongly conclude the readers are stateful and kill a viable design.

**The fragility is the point:** this safety rests on an EW invariant that upstream has never promised and no
test enforces, in a submodule we bump regularly. A future bump adding a lazy snapshot refresh on a read path
would turn the cache into a silent, rare data race. Owning that is the strongest argument against §6.

### 3.3 There is NO intra-call shortcut

It looks as though the 2 opens per `ScanTable` call could collapse to 1 by handing the schema step's
already-open table to the stream step, with no cross-call lifetime and none of the disposal or thread-safety
questions. They cannot: `Stream`/`StreamAt`/`StreamWithRowIds*` are async **iterators**, so the table is
opened at the first `MoveNextAsync` and disposed in the iterator's `finally`. The schema open has therefore
already COMPLETED AND DISPOSED before the stream open begins — and the stream's open happens during
`get_next`, a LATER ABI call than the one that resolved the schema. The two are sequential and in different
calls. A statement-scoped cache is the only way to share them.

### 3.4 Where state should live — `ClientContextState`, not `function_info`

The storage mechanism is already solved and used throughout: `Handles.Alloc` (a **Normal** `GCHandle` →
opaque `nint`; NOT `Pinned` — a reference type needs no pinning and it would block heap compaction) plus
`Handles.Resolve<T>`, with a C++ RAII holder whose destructor calls an ABI release (`InOutSessionHolder` →
`inout_abort`, `AggSessionHolder` → `agg_close`, the catalog handle → `close_catalog`).

- **`TableFunction::function_info` is the WRONG shelf** (considered, rejected): a
  `shared_ptr<TableFunctionInfo>` copied with the function into `LogicalGet.function`, and our catalog scan
  builds its `TableFunction` FRESH per bind — so the lifetime is the PLAN, and plans are cached and
  re-executed (`SupportStatementCache()` true). A cached snapshot there would be reused by a later execution
  IN A DIFFERENT TRANSACTION. Wrong in both directions at once: it spans transactions, yet a new bind gets a
  new one so two statements never share. It IS correct for what we already use it for — the aggregates'
  identity and atomic counter, which are version-independent.
- **`context.registered_state->GetOrCreate<T>(key)` is the right one.** `ClientContextState` carries
  `TransactionBegin` / `TransactionCommit` / `TransactionRollback(…, error)` / `QueryEnd(context, error)` —
  per-CONNECTION storage with a destructor. Marginal gain over the `FabricatorTransactionManager` hook
  already used for `SnapshotPinning.Release`: cleanup even when a transaction never ends cleanly, plus
  `TransactionBegin`, which we have no other equivalent of.
- **Simplest of all:** the EXISTING `SnapshotPinning` per-txn structure keyed (txn, path), because
  `Release(txnId)` is already wired to commit and rollback and becomes the disposal point for free.

### 3.5 Invalidation list

Any cache must be dropped when an operation BYPASSES the buffer and commits immediately, since those advance
the version under a held snapshot and a later read in the same transaction must see its own committed write:

- identity creates
- DROP / OPTIMIZE / VACUUM
- CREATE-OR-REPLACE
- partition-overwrite

This list is a **maintenance liability**: it has to stay in sync as new immediate operations are added, and
missing one produces a silent stale read.

### 3.5a ⚠ THE COMMIT PATHS MUST NEVER BE SERVED FROM THE CACHE — a stale base turns a working append into a conflict

Measured 2026-08-07, and this is a CORRECTNESS constraint on a design otherwise framed as pure performance.

`DeltaCatalog.FlushDeferredFilesAsync` opens the table FRESH at COMMIT (and again on every retry), so the
base snapshot it hands `CommitDataFilesAsync` is always the latest version. That is not incidental. Delta's
conflict checker examines the commits between the base version and the version being attempted, and a
concurrent **metaData** action conflicts UNCONDITIONALLY — reads are not consulted, so even a blind append is
examined. With a latest base that range is EMPTY and the append commits; with a base cached from earlier in
the transaction the range contains the property edit and **the append is refused**.

MEASURED both ways on both engine legs (`verify_delta_catalog_transactions` §41): a buffered `INSERT` whose
window contains a `fabricator_delta_set_tblproperties` commits, while a buffered `DELETE` — whose held
`DeltaTransaction` IS pinned at statement time — conflicts with *"Concurrent commit N changed the table
metadata."* Same table, same isolation, same property edit; the only difference is where the base came from.

So the cache is for READS. The invalidation list above is about staleness; this is a different failure and a
worse one — a correct answer replaced by a refusal, on every append that races a property edit (ours, a Spark
job, an OPTIMIZE writing clustering metadata), i.e. exactly the `dbt run` shape. §41 exists to catch it.

### 3.6 The key must be pinned-version-scoped

A `DeltaTable` holds ONE `_currentSnapshot`, while `GetSchemaAt(v)` / `StreamAt(v)` need v's. Inside a
transaction the pin makes every read use one version, which is what makes (txn, path) sufficient — but the
key must carry the version, or be scoped by the pin, rather than meaning "the table at latest".

---

## 4. What is actually expensive, and therefore what to cache

`DeltaTable.OpenAsync` is, in full:

```csharp
long latestVersion = await log.GetLatestVersionAsync(ct);                     // the _delta_log LIST
var snapshot = await SnapshotBuilder.BuildAsync(log, checkpointReader, null, ct); // commit/checkpoint reads + replay
ProtocolVersions.ValidateReadSupport(snapshot.Protocol);
return new DeltaTable(fileSystem, options, snapshot);                        // a cheap holder
```

Every expensive thing — the listing, the commit and checkpoint reads, the replay CPU — exists to produce the
**`Snapshot`**. The `DeltaTable` is a thin wrapper around it, and the filesystem is `opener` + a normalized
root string.

`Snapshot` (`EngineeredWood.DeltaLake/Snapshot/Snapshot.cs`) is **immutable**: `required … { get; init; }`
properties over `IReadOnlyDictionary` collections (`ActiveFiles`, `AppTransactions`, `DomainMetadata`,
`Tombstones`), plus `Version`, `Metadata`, `Protocol`, `Schema`, `ArrowSchema`.

**So cache the `Snapshot`, keyed (txn, path, version) — not a `DeltaTable`, and not a `NativeScanList`.**

### 4.1 Why NOT the `NativeScanList` (a flaw in an earlier draft of this doc)

`ListNativeScanFiles(opener, path, unit, value, **prune**, log, schemaOverride)` returns a **post-prune**
list: its `Files` are the survivors of best-effort Delta-log file pruning against the pushed predicate.
Caching it per (txn, path, version) would hand one scan another scan's pruned file set — and a file set that
is too SMALL means **silently missing rows**. Two aggregate subqueries over one table with different `WHERE`
clauses is enough to hit it. Pruning and deletion-vector resolution are per-call work DOWNSTREAM of the
snapshot; only the snapshot is cacheable.

---

## 5. RECOMMENDED design: cache the immutable `Snapshot`, construct the table per call

Per read: build the (cheap) filesystem, take the cached `Snapshot` for (txn, path, pinned version), wrap it
in a fresh `DeltaTable`, then prune / resolve DVs / decode exactly as today.

### 5.0a ⚠ WHAT THE EW PATCH ACTUALLY BUYS — narrower than this section originally claimed, and it now costs more

Two things changed since this was written, and they pull in opposite directions.

**(a) A cached snapshot can ALREADY be handed to a read, with no patch.** `DeltaReadOptions.Snapshot` is
public, and its guard `RequireSnapshotOfThisTable` compares the Delta **table id** out of `metaData` — not
object identity — so a snapshot built by a DIFFERENT `DeltaTable` instance over the same path is ACCEPTED.
Upstream also refuses `Snapshot` and `AtVersion` together rather than resolving by precedence.

**(b) But that does not avoid the open, which is the whole cost.** Every consumer still needs a live
`DeltaTable`: `ReadAsync` is an instance method, and so is `PlanFiles` (which takes its `snapshot` explicitly
but must be called on *something*). So the honest split is:

| site | cacheable with NO EW change | needs `FromSnapshot` |
|---|---|---|
| `GetSchema` / `GetSchemaAndVersion` / `GetSchemaAt` | **yes** — only `snap.ArrowSchema` is wanted, so cache the `Schema` itself and skip the open entirely | — |
| `ListNativeScanFiles` | no — `table.PlanFiles(prune, snap, …)` needs a table | yes |
| `Stream*` / `ReadAsync` | no | yes |

⇒ **a plain SCHEMA cache is available today and removes two of the four remaining opens** (the column fetch
or the probe, and the real scan's schema open). The listing and stream opens are what the patch buys.

**⚠ And the patch is no longer free to propose.** When this section was written we carried a
`fabricator-patches` branch, so "one small additive patch" cost nothing extra. Since 2026-08-12 the submodule
points at ORIGINAL upstream with **zero** patches — reaching that was a stated goal — so adding `FromSnapshot`
means re-opening a fork branch, repointing `.gitmodules` and pushing it (see CLAUDE.md's ⚠ TO OFFER A PATCH
AGAIN). That is a reasonable price for a real win, but it is a decision rather than a detail, and it argues
for landing the schema cache first and measuring what remains.

This needs **one small additive engineered-wood patch**, because `OpenAsync`/`CreateAsync`/`OpenOrCreateAsync`
are the only public factories and the snapshot-taking constructor is private:

```csharp
// engineered-wood, on fabricator-patches — additive, no behaviour change, upstreamable:
public static DeltaTable FromSnapshot(ITableFileSystem fs, DeltaTableOptions options, Snapshot snapshot)
    => new DeltaTable(fs, options, snapshot);
```

A legitimate entry point in its own right ("I already have the snapshot"), so it is a reasonable thing to
offer upstream rather than carry indefinitely. Full signature, following EW's own factory conventions
(`fileSystem` first, the essential artifact second, `DeltaTableOptions? options = null` third as in
`CreateAsync`; `Snapshot.Snapshot` qualified the way `CreateAsync` writes `Schema.StructType`):

```csharp
public static DeltaTable FromSnapshot(
    ITableFileSystem fileSystem,
    Snapshot.Snapshot snapshot,
    DeltaTableOptions? options = null)
{
    ArgumentNullException.ThrowIfNull(fileSystem);
    ArgumentNullException.ThrowIfNull(snapshot);
    options ??= DeltaTableOptions.Default;
    ProtocolVersions.ValidateReadSupport(snapshot.Protocol);   // free, and keeps the invariant local
    return new DeltaTable(fileSystem, options, snapshot);
}
```

- **Synchronous on purpose** — it does no IO, so `ValueTask<DeltaTable>` would be dishonest and would force
  pointless awaits at every call site. Deliberately asymmetric with the other three factories.
- **`snapshot` non-nullable** although the private ctor takes `Snapshot?`: a null would surface much later as
  `CurrentSnapshot`'s "Table not initialized", far from the cause.
- **`options` must be threaded through**, not defaulted away — the ctor derives
  `_dataFileReadOptions = WithVariantExtension(options.ParquetReadOptions)`, so this is variant correctness,
  not cosmetics. The Bridge passes `DeltaWriter.Options()` as it does today.

### 5.0 A per-call `DeltaTable` is FREE, which is what makes this shape work

`DeltaTable.Dispose()` and `DisposeAsync()` do nothing but set `_disposed = true` — no filesystem, no
handles, nothing released. Combined with the cheap constructor, the ENTIRE cost of `OpenAsync` is the log
replay. So building a fresh table around a cached snapshot on every call adds no measurable overhead, and
there is no ownership question to model.

The same fact WEAKENS one argument against §6, and it should be stated rather than quietly left standing: a
leaked cached `DeltaTable` would be a GC-able object, not an OS-resource leak. The other three objections to
§6 (stale read, data race, use-after-free) are untouched, and §5 still dominates on every other row of the
table above.

**Why this dominates both alternatives:**

| | cache `Snapshot` (§5) | cache `NativeScanList` | cache `DeltaTable` (§6) |
|---|---|---|---|
| captures the redundant cost | **all of it** (LIST + replay) | all of it | all of it |
| correct under differing pushed predicates | **yes** — pruning stays per-call | **NO** — silently drops rows (§4.1) | yes |
| serves which engines | **both** | native only | both |
| disposal | none — immutable data, GC | none | sync-over-async at txn end |
| dangling opener | cannot happen — fs per call | cannot happen | must be solved |
| thread safety | **dissolves** — only immutable data is shared, each call gets its own table | trivial | rests on an unenforced EW invariant (§3.2) |
| code shape | a lookup at the existing open sites | ditto | a lease + ownership flag threaded through 6 async iterators |
| staleness | version key + §3.5 | version key + §3.5 | version key + §3.5 |

The §3.2 thread-safety worry — that our safety rests on "no read path mutates `_currentSnapshot`", an EW
invariant nobody enforces — **disappears** here: nothing mutable is shared, because each call still gets its
own `DeltaTable`. That is the single biggest reason to prefer this shape. It also needs none of the
async-iterator surgery §3.3 identified, since tables remain per-call and their `finally DisposeAsync` stays
exactly as it is.

### 5.1 Sketch

1. EW: the `FromSnapshot` factory above.
2. Bridge: a `SnapshotCache` beside `SnapshotPinning` (or inside its per-txn structure, so
   `Release(txnId)` — already wired to commit and rollback — clears it for free). Key (txn, path, version);
   value the `Snapshot`. No disposal needed.
3. Bridge: at each `DeltaTable.OpenAsync` site that is a READ, consult the cache; on a miss, open as today
   and populate from `table.CurrentSnapshot`. Writes and `RefreshAsync` never consult it.
4. Invalidate per §3.5.

### 5.2 What it does NOT cover

Deletion-vector position resolution stays per-call (it reads the DV files). That is content IO rather than a
listing, so unlike `_delta_log` LISTs it can be served by `ExternalFileCache` — measure before treating it as
a problem, and note it would be a separate cache with a separate key.

---

## 6. NOT RECOMMENDED: caching the `DeltaTable` itself

The original proposal, kept for the record. Needed only if something must share a LIVE table across calls,
which §5 shows is unnecessary: the expensive artifact is the snapshot, and wrapping it is free.

Shape, if ever built anyway:

1. A lease type reporting whether the table is OWNED (dispose in the iterator's `finally`) or BORROWED (do
   not) — §3.3 identifies that unconditional `DisposeAsync` as the specific thing to change.
2. Thread it through the 6 schema/`Stream*` entry points in `DeltaReader`.
3. Store per (txn, path) in `SnapshotPinning`; dispose in `Release(txnId)`, sync-over-async at one blocking
   point per the codebase convention.
4. Invalidate per §3.5.

**Why not:** all four failure modes are silent — stale read, data race, leaked table, use-after-free — and
they land where this development box would not fault. It buys nothing over §5 while owning an EW invariant
nobody enforces (§3.2).

---

## 7. Decision gate

1. ~~**Measure before building anything.**~~ **DONE 2026-08-13 — §0. The gate is PASSED: the redundant opens
   are ~26–48 s EACH on OneLake, 195 s of a 291 s query.** Recorded verbatim because the sequence is the
   point: taking the measurement found TWO cheaper fixes (§0.1 the doubled AT-version build, §0.2 the bind
   probe running a full scan setup) worth ~130 s between them, neither of which is a cache and neither of
   which anyone would have proposed from the operation count. **Re-measure against the post-fix baseline
   before building §5** — the cache's remaining prize is MEASURED at ~146.6 s → ~85 s, not 291 → 85.
2. **If it matters, build §5** — cache the immutable `Snapshot` per (txn, path, version), plus the small
   additive `FromSnapshot` factory in engineered-wood. Understandable in one sitting, serves both engines, and
   the only failure mode left is staleness, which the version key and the §3.5 invalidation list address.
3. **Do not build §6** (caching a live `DeltaTable`). It buys nothing over §5 and costs a lease abstraction
   threaded through 6 async iterators plus a dependency on an EW invariant nobody enforces (§3.2).
4. **Do not cache the `NativeScanList`** (§4.1) — it is post-prune, so sharing it across scans with different
   pushed predicates silently drops rows.
