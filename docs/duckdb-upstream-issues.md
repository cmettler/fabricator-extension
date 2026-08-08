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

## 4. `FILE_FLAGS_EXCLUSIVE_CREATE` is SILENTLY IGNORED on Windows — no put-if-absent primitive

**Status: FIXED, VALIDATED, AND SUBMITTED — [duckdb/duckdb#24612](https://github.com/duckdb/duckdb/pull/24612)
(draft, 2026-08-08), from `cmettler/duckdb@fix/windows-exclusive-create` (`f894841e`) off `duckdb/main`
(`e500d778`).** No existing DuckDB issue or PR mentioned `EXCLUSIVE_CREATE`. This is the cause of
[delta-transactions.md](delta-transactions.md) §8.5, which had recorded it as "not investigated".

**Upstream CI green on all three platforms** (fork run of `Main` + `OSX` on `f894841e`): **Windows (64 Bit)
5246 passed / 0 failed**, twice (MSVC and the VS2019-stdlib rebuild); Linux `make allunit`; macOS 6187 passed;
plus format, generated-files, clang-tidy, clang warnings-as-errors, and **Linux Relassert Tests** — the last
being the only leg that exercises `FileOpenFlags::Verify()`, which is `#ifdef DEBUG` and so invisible to every
release-build green. The sole red anywhere is an unrelated `test_string_agg_overflow.test_slow` OOM (asked for
4 GiB with 3.8 of 5.5 GiB used) on an undersized fork macOS runner; upstream runs that on larger hardware.
- ⚠ **THREE of those greens do NOT test what their name suggests, and each nearly got over-claimed here.**
  `Linux Release` runs `make smoke T=--changed-tests=…` (6 seconds — a subset, not the suite); `OSX Debug` is
  a warnings-as-errors COMPILE with no test step; and confirming Windows took four hops — job steps → the
  `run.py` runner it invokes → the Makefile's `-DENABLE_UNITTEST_CPP_TESTS=0` path → establishing that nothing in
  `.github/` or `scripts/` ever sets `DISABLE_CPP_UNITTESTS`, so the `TRUE` default holds. **On this workflow
  "build" and "tests" are deliberately separate jobs; a passing build job says nothing about coverage.**
- ⚠ Test names are NOT printed in these job logs — grepping for one is a VOID check. Verified with a positive
  control: five pre-existing `[file_system]` test names are equally absent from a log of a run that certainly
  executed them. Coverage here is established BY CONSTRUCTION (default-on CMake option + `'*'` selector), not
  by log inspection.

**The fix is two changes, both mirroring the POSIX branch** — `ExclusiveCreate()` → `CREATE_NEW` (tested
FIRST), and the `ERROR_FILE_EXISTS` mirror of POSIX's `EEXIST` so `FILE_FLAGS_NULL_IF_EXISTS` works. Validated
four ways: the probe flips to `true`; the 6-writer race goes **150/900 → 900/900 rows and 4/19 → 19/19 commit
files**; a new platform-agnostic `TEST_CASE` passes (7 assertions, run via a temporary
`ENABLE_UNITTEST_CPP_TESTS=TRUE`); and the fabricator hermetic tier is **67/67 — 6895, identical to baseline**.
Harness: `scratchpad/local_win_race.sh` (runs both legs). The src half is saved as
`scratchpad/duckdb-win-exclusive-create.patch` so the local capability is one `git apply` away.

⚠ **Do NOT re-pin our submodule to a patched DuckDB.** We ship a *loadable* extension, so
`LocalFileSystem::OpenFile` executes inside the **host's** DuckDB — a patched submodule fixes only our own
statically-linked `unittest.exe`/`duckdb.exe`, which is useful for running multi-writer experiments here and
worthless to anyone using the extension. Only an upstream release reaches users.

`FileOpenFlags::ExclusiveCreate()` is read in **exactly one place in all of DuckDB** —
`local_file_system.cpp:370-371`, inside the **POSIX** branch:

```cpp
if (flags.ExclusiveCreate()) {
    open_flags |= O_EXCL;
}
```

The **Windows** `LocalFileSystem::OpenFile` (`local_file_system.cpp:1069-1075`) never consults it:

```cpp
if (open_write) {
    if (flags.CreateFileIfNotExists()) {        // FILE_FLAGS_FILE_CREATE
        creation_disposition = OPEN_ALWAYS;
    } else if (flags.OverwriteExistingFile()) { // FILE_FLAGS_FILE_CREATE_NEW
        creation_disposition = CREATE_ALWAYS;
    }
}
```

`CREATE_NEW` — the Win32 disposition that fails with `ERROR_FILE_EXISTS` when the target exists, i.e.
*precisely* the missing primitive — **appears nowhere in the file.** So the flag is dropped, and since
`Verify()` requires it to be combined with `FILE_CREATE` (below), the open falls to `OPEN_ALWAYS` = "open,
creating if absent" — which succeeds happily on an existing file. **The OS is not the limitation; the
disposition is simply never selected.**

### It is public, documented surface, not a half-built flag

- `FileOpenFlags::Verify()` (`file_system.cpp:90-93`) asserts its combination rules and comments them:
  *"FILE_FLAGS_EXCLUSIVE_CREATE only can be combined with CREATE/CREATE_NEW"*.
- It is exposed on the **C API**: `DUCKDB_FILE_FLAG_CREATE_NEW` → `FILE_FLAGS_EXCLUSIVE_CREATE`
  (`main/capi/file_system-c.cpp:81-82`), reachable via `duckdb_file_system_open`.

⇒ **a stock repro needs no extension**: open an existing path with
`DUCKDB_FILE_FLAG_WRITE | DUCKDB_FILE_FLAG_CREATE | DUCKDB_FILE_FLAG_CREATE_NEW`. It fails on Linux/macOS
and **succeeds on Windows**. Same shape as §2's C repro; worth writing before filing.

### The naming collision that probably hid it

DuckDB has **two** things called `CREATE_NEW`, meaning opposite things:

| name | meaning | Win32 equivalent |
|---|---|---|
| C API `DUCKDB_FILE_FLAG_CREATE_NEW` | exclusive create — fail if exists | `CREATE_NEW` |
| internal `FILE_FLAGS_FILE_CREATE_NEW` → `OverwriteExistingFile()` | truncate — overwrite if exists | `CREATE_ALWAYS` |

So the Windows branch *looks* complete — it handles a flag named `CREATE_NEW` — while the flag it handles is
the truncating one, and the one that matches the Win32 disposition by name is the one it ignores.

### Why upstream has not hit it — ESTABLISHED FROM HISTORY, not inferred

The obvious question is whether the Windows branch was left alone on purpose for some undocumented reason.
It was not. `git blame` on `duckdb/main` settles it:

| line | commit | date | what |
|---|---|---|---|
| POSIX `if (flags.ExclusiveCreate())` | `b2f9767a` | **2024-07-24** | *"Create file with O_EXCL flag set."* — [#13123](https://github.com/duckdb/duckdb/pull/13123) |
| Windows `creation_disposition` block | `74561a79` / `8e80101f` / `eee76b85` | 2021-09 … 2024-03 | predates the flag entirely, never revisited |

**One PR introduced both `FILE_FLAGS_EXCLUSIVE_CREATE` and `FILE_FLAGS_NULL_IF_EXISTS`, added their
combination rules to `FileOpenFlags::Verify`, and implemented them in the POSIX branch of
`local_file_system.cpp` — while the Windows branch of the same function, in the same file, went untouched.**
Its description is written purely in POSIX vocabulary (*"When O_EXCL is used WITH O_CREAT open will fail if
file exists"*), so Windows never entered the frame. Nobody writes `Verify()` assertion rules for a flag they
intend to no-op on a platform, and nothing anywhere — comment, doc or issue — records the flag as POSIX-only.

Two aggravating facts. `FILE_FLAGS_EXCLUSIVE_CREATE` has **zero internal callers** (a grep finds only the flag
machinery and the C-API translation), so no DuckDB test could ever reach the Windows path — that is the
*mechanism* by which it stayed invisible for two years, and it also means the fix carries essentially no
regression risk for DuckDB proper. And 14 months later `d4f7b546` promoted it to **public C API surface** as
`DUCKDB_FILE_FLAG_CREATE_NEW`, by which time the gap was already there and unnoticed.

⚠ The one Windows wrinkle worth knowing, and it is not a reason to have omitted the disposition: `CREATE_NEW`
against an existing **directory** reports `ERROR_ACCESS_DENIED` rather than `ERROR_FILE_EXISTS`, and some
existing entries report `ERROR_ALREADY_EXISTS`. The fix accepts both of the latter two for the
`NULL_IF_EXISTS` path.

### The fix is three lines, and the ORDER is load-bearing

```cpp
if (open_write) {
    if (flags.ExclusiveCreate()) {
        creation_disposition = CREATE_NEW;        // fails with ERROR_FILE_EXISTS if present
    } else if (flags.CreateFileIfNotExists()) {
        creation_disposition = OPEN_ALWAYS;
    } else if (flags.OverwriteExistingFile()) {
        creation_disposition = CREATE_ALWAYS;
    }
}
```

Exclusive must be tested **first**: `Verify()` *requires* `EXCLUSIVE_CREATE` to be accompanied by
`FILE_CREATE`, so every legal caller sets both — testing `CreateFileIfNotExists()` first preserves the bug
verbatim.

⚠ A complete fix should also make the failure **classifiable**. `CreateFileW` sets `ERROR_FILE_EXISTS` (80),
which DuckDB stringifies through `GetLastErrorAsString()` — **locale-dependent prose**. The POSIX side
already surfaces a structured `errno`, which is what our own conflict classifier reads (matching on the
message is a trap we have already been bitten by on a fuse mount). Without an equivalent on Windows, a caller
still cannot cheaply tell "already exists" from a real IO error.

### What it costs us

It is the whole of §8.5: on a local Windows Delta root **neither** commit primitive is conditional
(`MoveFile` overwrites too), so concurrent writers lose commits silently — measured at 6 writers × 3 INSERTs
× 50 rows ⇒ **400 of 900 rows landed, 500 lost, every writer exit 0**. Single-writer is unaffected, which is
the entire hermetic tier, so this constrains *harness design* rather than the shipped product: a Windows
local root cannot host any multi-writer experiment. Fixing it upstream would make local Windows as safe as
local POSIX for free — the code above `HostFsOpenWrite` needs no change at all.

## Not a bug, but pinned here because it wasted time three times

`read_parquet` answers `count(*)` — and `count(<col>)` — from parquet footer metadata without decoding the
column. Any measurement or control built on one is **void**: it will report success for a query shape that
fails on a real decode, and near-zero time for a scan that never happened. Both happened here in one
session: the field-id + `file_row_number` combination was pronounced working on a multi-file `count(*)`, and
a `schema`-vs-plain timing comparison showed a 10x difference that disappeared entirely under
`sum(length(s))`.

Force a real decode (`sum(…)`, `sum(length(…))`) in every control on this surface.
