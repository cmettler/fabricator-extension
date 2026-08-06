# DuckDB issues found from here — reproducible, with controls

Bugs in DuckDB itself that our work has surfaced, written so they can be pasted into an upstream issue
without any of our code. **Verified against the stock `duckdb` PyPI wheel `1.5.5`, no extension loaded** —
that is the point of this file: an assertion reached through the fabricator extension is worth nothing
upstream until it is reproduced without it.

Standing rule for anything added here: **a minimal repro plus the controls that isolate it.** A bare failing
query invites "you were holding it wrong"; the controls are what turn it into a report. And if a repro cannot
be constructed, the finding belongs in the "not reproducible" section below and NOT in an upstream issue —
see §2, which is exactly that case and was nearly filed as a DuckDB bug.

---

## 1. `INTERNAL Error: No default expression in FieldId Map` — field-id `schema` + `file_row_number`

**Status: reproduced on stock 1.5.5. Ready to file.**

`read_parquet`'s `schema` parameter with **INTEGER** keys puts the reader in `BY_FIELD_ID` mode. Combined
with `file_row_number => true`, it raises an internal assertion whenever the *file* contains at least one
column that carries **no** parquet `field_id`.

```sql
-- a file where 'a' carries a field id and 'b' does not
COPY (SELECT 1 AS a, 2 AS b) TO 'mixed.parquet' (FORMAT parquet, FIELD_IDS {a: 1});

-- FAILS: INTERNAL Error: No default expression in FieldId Map
SELECT * FROM read_parquet(['mixed.parquet'],
                           schema = map {1: {'name': 'a', 'type': 'INTEGER', 'default_value': NULL}},
                           file_row_number => true);
```

**Controls, all of which succeed — each removes exactly one ingredient:**

| variant | result |
|---|---|
| the query above | `INTERNAL Error: No default expression in FieldId Map` |
| same, **without** `file_row_number` | OK → `(1)` |
| same, but `filename => true` instead | OK → `(1, '…/mixed.parquet')` |
| same, over a file where **every** column has a field id (`FIELD_IDS {a: 1, b: 2}`) | OK → `(1, 0)` |

So the trigger is precisely **field-id-keyed `schema` + `file_row_number` + a file containing a
field-id-less column**. Note `filename` is *not* affected, though it is the same virtual-column mechanism —
worth mentioning in the issue, since it suggests the missing default is specific to how `file_row_number`
is materialised.

### ⚠ Severity is higher than "a query fails": it INVALIDATES THE DATABASE

The assertion is not contained. After it fires, the next unrelated query on the same connection returns:

```
FATAL Error: Failed: database has been invalidated because of a previous fatal error.
The database must be restarted prior to being used again.
```

Measured by running two statements on one connection. So a user who hits it loses the session, not just the
query. That is the sentence to lead the issue with.

### Why it matters to us, and why we are not waiting for it

Delta row-tracking columns (`__delta_row_id`, `__delta_row_commit_version`) are **materialized columns and
are not column-mapped**, so they carry no field id — while every other column of a column-mapped table does.
Row tracking is on by default for tables we create, and every merge-on-read post-image file contains that
column. So the field-id route is unusable on the *default* table shape, which is why
`DeltaNativeReader.BatchPlan`'s full form uses `union_by_name` + an explicit alias projection instead of the
`schema` map (see the class remarks there).

### Related, same area, lower value: a name-keyed `schema` + a virtual column reports a sentinel id

Not an assertion, but a confusing error rather than a clean one. With **VARCHAR** keys, `filename` /
`file_row_number` fail to resolve and the message names the virtual column's internal id:

```
Invalid Input Error: … schema mismatch in glob: column "2147483645" was read from the original file "…",
but could not be found in file "…"
Candidate names: id, t, file_row_number
```

Both file names in that message are the same file, which is its own small bug. Worth folding into the issue
as a second observation: a name-keyed `schema` map appears simply not to support the virtual columns, and
says so badly.

---

## 2. `Information loss on integer cast: value 4294967296` — trigger found, OWNER STILL OPEN, do not file

**Status: reproducible in four lines with no Delta, no parquet and no CTE. The trigger is a bound host-query
Arrow input carrying a column that DuckDB's projection PRUNES. Whether the defect is in DuckDB's C-API arrow
scan or in Apache.Arrow C#'s export is NOT established — so this must not be filed, and must not be written
down as "ours" either.** The partition gate in `DeltaNativeReader.BatchPlan` stands on this.

Reading a partitioned Delta table through the batched path raised, non-deterministically, either

```
INTERNAL Error: Information loss on integer cast: value 4294967296 outside of target range [0, 4294967295]
```

or an outright **SEGFAULT** (`0xC0000005`).

### The minimal repro

Bind an Arrow batch of `(a0 utf8, a1 int64, a2 utf8)` as a host-query input and run `SELECT a0, a2 FROM
<view>` — i.e. read columns 0 and 2 and let DuckDB prune column 1. The process dies inside `host_query`.
Instrumented on the managed side: the call is entered and never returns. Reading **all three** columns is
fine, as is every all-columns shape tried (`ss`, `sss`, `is`, `iss`, `sis`).

A partitioned Delta scan hits it because the per-file metadata input carries `ord` (the global file ordinal,
for the transient rowid) and a scan that wants no rowid never reads it.

### The error text named the wrong subsystem, and that is what made it slow

`4294967296` is `2^32`. Grepping DuckDB for the message lands on `NumericCast`, and the only *checked*
`uint32_t` casts on this path are in **`ColumnDataAllocator`** — so the value is an **allocation size**, not
an offset. `SetVectorString` narrows `offsets[i+1] - offsets[i]` with `UnsafeNumericCast`, which in a
**Release** build is an unchecked `static_cast`, so a garbage length passes silently and only blows up two
layers later when a `ColumnDataCollection` tries to size a block for it. Read the number as "something made a
string length absurd", never as an allocator or cast bug.

### Ruled out, each measured rather than argued

| candidate | verdict |
|---|---|
| the input's column SHAPE — "a second VARCHAR column breaks" | **refuted.** `(VARCHAR, BIGINT, VARCHAR)` round-trips perfectly when all columns are read. The earlier evidence for this ("with `p0` as `Int64` the query runs") was timing luck, and the "0 rows instead of 10" beside it was just integers failing a `p = 'p1'` filter, not corruption. |
| the SQL | the entire failing statement — MATERIALIZED CTE, `union_by_name`, `filename`, `file_row_number`, join, pushed filter — runs and returns the right rows when nothing is pruned. |
| our cleanup of the input allocations | **leaking them instead of freeing changes nothing.** |
| `SingleScanArrowStream`'s second-scan guard | bypassing it changes nothing. |
| hive auto-detection colliding with our projected partition column | `hive_partitioning => false` does not fix it. (Kept anyway as a real guard: any `x=y` directory in a table's path would otherwise inject a phantom column.) |
| a DuckDB assertion in `union_by_name` + a virtual column over a hive layout | does not reproduce on the stock 1.5.5 wheel — four controls with real decodes. Filing that would have been a false report. |

### ⚠ Why the stock-wheel control does NOT settle ownership

A pyarrow `RecordBatchReader` registered on a stock 1.5.5 wheel survives the same pruning. That looks like it
convicts our export — and it does not, because **Python's `register` does not go through the C API's
`duckdb_arrow_scan`**; it binds a Python object through its own factory. So the control exercises a different
path from the one that dies. This is the same mistake as the SQL-shape control below, in a new costume: a
control that changes the mechanism under test is not a control.

**To settle it, the next experiment must drive `duckdb_arrow_scan` itself** — a tiny C (or C#) harness linking
`duckdb.dll`, exporting one Apache.Arrow C# batch of `(utf8, int64, utf8)` and one hand-built C struct of the
same shape, then running `SELECT a0, a2` against each. Same call, two producers: whichever crashes owns it.

### A separate, real bug WAS found and fixed on the way (ours, C++ only, no ABI)

`MakeHostQueryStream` handed `duckdb_arrow_scan` the **caller's** `ArrowArrayStream *` — which DuckDB stores
as a raw pointer inside the view it creates — and then ran the SQL with `conn->SendQuery`, a **streaming**
result. The comment above that loop claimed the stream was "consumed + released by DuckDB during the
(materializing) query"; the next line of code said `// streaming (lazy Fetch)`. Both were in the file and only
one was true, so the managed caller's `finally` released and freed storage the view still pointed at. Fixed by
`OwnedArrowInputs`: each input is **moved** (struct copy + zero the source — the C-data-interface move) into
storage owned by the `HostQueryStream`, declared as its FIRST member so it outlives the result and connection
that scan it; zeroing the source also makes the managed cleanup inert (`release` is null ⇒ no double-release).

⚠ **This did not fix §2** — the crash is unchanged with it in — so it is a genuine latent hazard that the
investigation happened to surface, not the answer. It masked itself the same way: a `WITH … AS MATERIALIZED`
CTE is fully materialised during `SendQuery`'s first chunk push, so nearly every shape drained the input
before the ABI call returned, by plan accident.

Related: because `duckdb_arrow_scan` creates a **catalog-level (non-temporary)** view, it outlives both the
connection and the stream owning the input's storage — so `BoundInput.Drop` is correctness, not tidiness, and
the one lazy-stream site (`DeltaCatalog.SortStream`) now defers its drop to Dispose via `BoundInput.WrapDrop`.

### What actually made progress: stop bisecting the query, get a stack trace

Three hypotheses came from bisecting SQL and all three died. Running the failing statement outside
sqllogictest printed `0xC0000005 at Interop+Kernel32.LocalAlloc ← Fabricator.Bridge.HostFs.Query`, and
checkpoint logging then placed the death inside `host_query`. Two throwaway tools made that reachable and are
the reusable part: an env-var un-gate so the failure could be provoked on demand, and a temporary global table
function `fabricator_probe_input(sql, shape, vals)` binding an arbitrary hand-built Arrow batch under a name
substituted into arbitrary SQL — which exonerated marshalling, column shape, the query, our cleanup and the
scan guard in minutes each. **Build the probe that isolates one variable instead of re-running the composite.**

⚠ And note the control that looked decisive and was worthless: "the byte-identical statement with the metadata
as an inline `VALUES` CTE returns the right rows" — an inline CTE has no bound input, so it changed the very
variable under test while appearing to hold everything constant.

---

## 3. `duckdb_arrow_scan` leaks the schema it probes — observed, not filed

While reading the C API for §2: `duckdb_arrow_scan` calls `stream->get_schema(stream, &schema)` into a local
`ArrowSchema`, backs up and restores the children's release functions around `Ingest`, and **never releases
`schema`**. One leaked exported schema per registered input view. Real but small, and we register few views
per query; recorded so it is not re-discovered as a mystery rather than because it is worth an issue.

## Not a bug, but pinned here because it wasted time three times

`read_parquet` answers `count(*)` — and `count(<col>)` — from parquet footer metadata without decoding the
column. Any measurement or control built on one is **void**: it will report success for a query shape that
fails on a real decode, and near-zero time for a scan that never happened. Both happened here in one
session: the field-id + `file_row_number` combination was pronounced working on a multi-file `count(*)`, and
a `schema`-vs-plain timing comparison showed a 10x difference that disappeared entirely under
`sum(length(s))`.

Force a real decode (`sum(…)`, `sum(length(…))`) in every control on this surface.
