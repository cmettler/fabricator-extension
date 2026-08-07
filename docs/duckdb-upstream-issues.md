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

## 2. `duckdb_arrow_scan` + a NON-PREFIX projection = SEGFAULT

**Status: reproduced on plain DuckDB v1.5.5 — no extensions, no fabricator code, a hand-built C Arrow struct,
two passing positive controls. Ready to file.** This is what the `DeltaNativeReader.BatchPlan` partition gate
stands on.

Register a 3-column Arrow stream with `duckdb_arrow_scan` and select a subset of its columns that is **not a
prefix** — i.e. skip a column that has a projected column after it — and the process **segfaults**.

```
duckdb v1.5.5
positive controls:
  SELECT a0, a1, a2 FROM v           ... ok, 2 rows
  SELECT a0, a1 FROM v               ... ok, 2 rows     <- prefix, fine
subject (non-prefix projection):
  SELECT a0, a2 FROM v               ... Segmentation fault
```

`SELECT a1, a2` (skipping column 0) crashes the same way. The rule is exactly *the projected column set must be
a prefix of the stream's columns*.

**Repro: [test/repro/duckdb_arrow_scan_nonprefix.c](../test/repro/duckdb_arrow_scan_nonprefix.c)** — pure C,
public C API only, ~110 lines, self-contained. The Arrow array is hand-built, so **no producer library is
involved**: this is not an Arrow-implementation bug. Build against a static DuckDB with `-DDUCKDB_STATIC_BUILD`
(the file's header comment carries the exact `cl` line used here).

### It also surfaces as a corrupted string length rather than a crash

Non-deterministically the same shape instead raises

```
INTERNAL Error: Information loss on integer cast: value 4294967296 outside of target range [0, 4294967295]
```

and then **invalidates the database**, as §1 does. `4294967296` is `2^32`, and the only *checked* `uint32_t`
casts on this path are in **`ColumnDataAllocator`** — so the value is an **allocation size**, not an offset:
`SetVectorString` narrows `offsets[i+1] - offsets[i]` with `UnsafeNumericCast`, unchecked in Release, so a
garbage length passes silently and only throws when a `ColumnDataCollection` tries to size a block for it.
Worth stating in the issue, because the message points at the allocator while the fault is reading a column's
buffers at the wrong index.

### How ownership was established — and the control that nearly got it wrong

Diagnosed from a partitioned Delta scan whose bound per-file metadata input carries a global-ordinal column
that a scan wanting no rowid never reads. The sequence matters, because two controls were misleading:

1. **A pyarrow `RecordBatchReader` on the stock wheel survives the same pruning.** That looks like it convicts
   the *producer* — and it does not: **Python's `register` does not go through `duckdb_arrow_scan`**, so it
   never exercises this path. A control that changes the mechanism under test is not a control.
2. Swapping the producer while holding the call site fixed is what settled it: feeding `duckdb_arrow_scan` a
   **DuckDB-produced** stream (`ArrowAppender` output) crashes identically to an Apache.Arrow C#-produced one.
   Producer-independent ⇒ the consumer owns it.
3. Only then was the standalone C repro written, so the report carries no third-party code at all.

Three earlier hypotheses died on the way, all from bisecting the *query* rather than the mechanism: a hive
column collision (`hive_partitioning => false` does not fix it), a `union_by_name` + virtual-column assertion
(does not reproduce), and "a bound input with a second VARCHAR column breaks" (refuted — `(utf8, int64, utf8)`
round-trips perfectly when every column is read; the evidence for it was timing luck).

### A separate real bug of OURS was found on the way, and fixed

`MakeHostQueryStream` handed `duckdb_arrow_scan` the **caller's** `ArrowArrayStream *` — which DuckDB stores as
a raw pointer inside the view it creates — and then ran the SQL with `conn->SendQuery`, a **streaming** result.
The comment above that loop claimed the stream was "consumed + released by DuckDB during the (materializing)
query"; the next line of code said `// streaming (lazy Fetch)`. Both were in the file and only one was true, so
the managed caller's `finally` released and freed storage the view still pointed at. Fixed by
`OwnedArrowInputs`: each input is **moved** (struct copy + zero the source) into storage owned by the
`HostQueryStream`, declared as its FIRST member so it outlives the result and connection that scan it.

⚠ **That is not this bug** — the crash is unchanged with the fix in. It is a latent hazard the investigation
surfaced, and it hid the same way: a `WITH … AS MATERIALIZED` CTE is fully materialised during `SendQuery`'s
first chunk push, so nearly every shape drained the input before the ABI call returned, by plan accident.

Related, and the reason `BoundInput.Drop` is correctness rather than tidiness: `duckdb_arrow_scan` creates a
**catalog-level (non-temporary)** view, so it outlives both the connection and the stream owning the storage.

### ⚠ Wrapping the scan does NOT work — measured, because it is the obvious first guess

A subquery, a plain CTE and even a **MATERIALIZED** CTE all still crash: projection pushdown goes straight
through every one of them. That is exactly why the real query that first exposed this still died despite
already being written as `WITH … AS MATERIALIZED (SELECT * FROM <view>)`.

| variant | result |
|---|---|
| `SELECT a0, a2 FROM v` | crash |
| `SELECT a0, a2 FROM (SELECT * FROM v) t` | crash |
| `WITH t AS (SELECT * FROM v) SELECT a0, a2 FROM t` | crash |
| `WITH t AS MATERIALIZED (SELECT * FROM v) SELECT a0, a2 FROM t` | crash |
| `… FROM (SELECT * FROM v OFFSET 0) t` / `LIMIT 100` / `UNION ALL` / `a1+0 AS a1` | crash |
| `… FROM (SELECT * FROM v ORDER BY a1) t` | **ok** |
| `SELECT a0, a2 FROM (SELECT * FROM v) t WHERE t.a1 IS NOT NULL` | **ok** |

The two that work are the two where the skipped column is still **referenced**, so the scan is asked for the
full set. ⇒ the reliable mitigation is not a wrapper but **making the stream's columns equal the consumed
set**: bind only the columns the generated SQL actually reads. (Relying on a stray reference to survive the
optimizer is not a mitigation — constant-folding or a provably-true predicate can drop it again.)

### What it costs us

Not a gate any more, but a **standing invariant**: every column of a bound host-query input must be read by
the generated SQL. `DeltaNativeReader.MetaStream` therefore takes `withOrdinal` — the per-file global ordinal
is emitted only when the rowid expression reads it — so the consumed set always equals the produced set and
the bug is unreachable by construction rather than by luck. Partitioned tables were gated off the batched read
until that was understood; they are batched now.

⚠ The cheap-looking alternative (leave the column bound, wrap the query) does **not** work — see the table
above. And a future bound input that adds a column without the SQL to read it re-arms this immediately, which
is why the invariant is stated on `MetaStream` itself and gated by a *filtered* partition query in
`verify_delta_batched_read` §6: an unfiltered partitioned scan reads every bound column anyway and passed
happily while the bug was live.

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
