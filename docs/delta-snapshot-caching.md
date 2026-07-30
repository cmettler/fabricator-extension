# Delta snapshot caching — holding a `DeltaTable` open across ABI calls

**Status: NOT BUILT. Gated on a measurement nobody has taken (§7).** Two prerequisites are done and shipped
(§2). Read §4 and §7 before starting work: examining the design twice changed what it should be. Caching a
live `DeltaTable` — the original idea, §6 — is NOT recommended and is not needed; the expensive artifact is
the immutable `Snapshot`, and caching THAT (§5) serves both engines while dissolving the disposal, dangling-
opener and thread-safety problems entirely.

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

This needs **one small additive engineered-wood patch**, because `OpenAsync`/`CreateAsync`/`OpenOrCreateAsync`
are the only public factories and the snapshot-taking constructor is private:

```csharp
// engineered-wood, on fabricator-patches — additive, no behaviour change, upstreamable:
public static DeltaTable FromSnapshot(ITableFileSystem fs, DeltaTableOptions options, Snapshot snapshot)
    => new DeltaTable(fs, options, snapshot);
```

A legitimate entry point in its own right ("I already have the snapshot"), so it is a reasonable thing to
offer upstream rather than carry indefinitely.

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

1. **Measure before building anything.** A self-join over OneLake or S3, with and without the redundant
   opens, using the §1 method. If the redundant log reads are hundreds of milliseconds, §5 is worth it. If
   they are tens, close the item. This is roughly an hour of work and it settles a question that a multi-hour
   build would otherwise be defended by an operation count.
2. **If it matters, build §5** — cache the immutable `Snapshot` per (txn, path, version), plus the small
   additive `FromSnapshot` factory in engineered-wood. Understandable in one sitting, serves both engines, and
   the only failure mode left is staleness, which the version key and the §3.5 invalidation list address.
3. **Do not build §6** (caching a live `DeltaTable`). It buys nothing over §5 and costs a lease abstraction
   threaded through 6 async iterators plus a dependency on an EW invariant nobody enforces (§3.2).
4. **Do not cache the `NativeScanList`** (§4.1) — it is post-prune, so sharing it across scans with different
   pushed predicates silently drops rows.
