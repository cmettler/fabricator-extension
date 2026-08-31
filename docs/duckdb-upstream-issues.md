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

**Status: reproduced on stock 1.5.5 AND ROOT-CAUSED in the source (§ below). Ready to file.**

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

### Root cause — located, and it explains every control above

All in `src/common/multi_file/`. Two facts combine.

**(1) Virtual columns carry a hardcoded, Iceberg-reserved field id** (`multi_file_reader.hpp`):

```cpp
// Reserved field id used for the "_file" field according to the iceberg spec (used for file_row_number)
static constexpr int32_t ORDINAL_FIELD_ID  = 2147483645;
// Reserved field id used for the "_pos" field according to the iceberg spec (used for file_row_number)
static constexpr int32_t FILENAME_FIELD_ID = 2147483646;
```

That is where the `"2147483645"` in the name-keyed error comes from — it is `ORDINAL_FIELD_ID`, not a stray
sentinel, and a name-keyed map stringifies it and hunts for a parquet column with that literal name.

**(2) `FieldIdMapper` assumes every mapped column HAS an identifier** (`multi_file_column_mapper.cpp`):

```cpp
FieldIdMapper(const vector<MultiFileColumnDefinition> &columns) {
    ...
    if (column.identifier.IsNull()) {
        // Extra columns at the end will not have a field_id
        break;                                    // ← the column is never added to field_id_map
    }
optional_idx Find(const MultiFileColumnDefinition &column) const override {
    D_ASSERT(!column.identifier.IsNull());        // ← DEBUG ONLY; release falls through
    ...                                           //    → not found
static unique_ptr<Expression> GetDefault(const MultiFileColumnDefinition &column) {
    auto &default_val = column.default_expression;
    if (!default_val) {
        throw InternalException("No default expression in FieldId Map");
    }
```

So an identifier-less column is **skipped while building the map**, then **not found** during resolution,
then has **no default** to fall back on — and throws. The `D_ASSERT` shows the code knows this case is not
supposed to reach `Find`; in a release build it does.

**Why `filename` escapes it** — the control that looked arbitrary is exactly predicted
(`MultiFileReader::GetConstantVirtualColumn`):

```cpp
if (column_id == COLUMN_IDENTIFIER_EMPTY || column_id == COLUMN_IDENTIFIER_FILENAME) {
    return make_uniq<BoundConstantExpression>(Value(type));
}
return nullptr;
```

`filename` is CONSTANT per file, so it is answered with a constant expression and never enters the
Find/GetDefault path at all. `file_row_number` varies per row, gets `nullptr`, and goes down the path that
throws. Every one of the four control outcomes follows from these three fragments.

**⚠ WHICH column fails — MEASURED, because the source alone reads ambiguously.** It is the VIRTUAL column,
not a file column lacking a field id. Four probes on the fixture above (`a` has field id 1, `b` has none):

| declared map | `file_row_number => true`? | result |
|---|---|---|
| `{1: a}` | yes | `INTERNAL Error` |
| `{1: a, 2147483645: rn}` | **no** | **OK** — `rn` is NULL |
| `{1: a, 2147483645: rn}` | yes | `INTERNAL Error` |
| `{1: a, 2147483646: fn}` | no | OK — `fn` is NULL |

The same map is fine WITHOUT the option and throws WITH it, so the map's contents are not the trigger. And a
declared-but-absent column resolves cleanly through its own `default_value`, so a column missing from the
file is not the trigger either. What throws is the global column the OPTION appends, which no `schema` entry
can give a default to — declaring it by its reserved id does not attach one (the id is used internally to
identify the virtual column, it is not an input contract; DuckDB just treats it as an ordinary field id,
finds nothing, and NULL-fills), and declaring it under the option's own name is refused as
`Binder Error: table "read_parquet" has duplicate column name "file_row_number"`.

**Fix shape:** give the virtual column a default expression, or have `GetDefaultExpression` handle a virtual
column instead of assuming a user-supplied default. ⚠ Falling through to NAME matching would NOT help — the
column does not exist in the file under any name. Separately, `FieldIdMapper`'s `break` is worth questioning
on its own: it stops at the FIRST identifier-less column, so any real column after it is dropped from the map
too.

### ⚠ Nor does Delta's ICEBERG COMPAT mode — checked because Iceberg is where the reserved id comes from

The obvious way the "Delta cannot provide a field id" claim could be too strong: Iceberg reserves
`2147483540` for `_row_id` (that is where `MultiFileReader::ROW_ID_FIELD_ID` comes from, and DuckLake stamps
it), and `delta.enableIcebergCompatV1/V2` exist precisely to make a Delta table's parquet consumable as
Iceberg. So does IcebergCompat assign it? MEASURED on Fabric Spark 4.1.1.5.5 — **no**:

| table | user columns | materialized row-id column |
|---|---|---|
| `icebergCompatV2` + rowTracking | field_id 1, 2 | **null** |
| `icebergCompatV1` + rowTracking | field_id 1, 2 | **null** |
| `icebergCompatV2` alone (control) | field_id 1, 2 | column absent — nothing to materialise |
| rowTracking alone (control) | field_id 1, 2 | **null** |

The user columns carry ids under IcebergCompat, so the field-id machinery is active; the row-tracking columns
carry none anyway. Consistent with the structural reason — the column is not a schema field, IcebergCompat
validates the SCHEMA, and the schema never mentions it.

⚠ **Two attempts at this measured nothing, and the controls are what showed it.** IcebergCompat REFUSES any
table carrying the `deletionVectors` table feature (`DELTA_ICEBERG_COMPAT_VIOLATION.
DELETION_VECTORS_SHOULD_BE_DISABLED`), DVs are ON BY DEFAULT on Fabric, and setting
`delta.enableDeletionVectors = 'false'` in `TBLPROPERTIES` is NOT enough — the validation checks the
protocol FEATURE, so it must be disabled at the session default
(`spark.databricks.delta.properties.defaults.enableDeletionVectors`) so the feature is never added. Without
the "icebergCompat ALONE" control, the refusal read as "row tracking and IcebergCompat are incompatible",
which is false.

⚠ **Note the consequence for scope**: IcebergCompat and deletion vectors are MUTUALLY EXCLUSIVE, DVs are on
by default, and DVs are the whole reason our batched reader needs `file_row_number`. So even had the answer
gone the other way, it would have exempted only tables we do not read this way.

### ⚠ DuckLake — DuckDB's OWN format — does NOT trip this, and that is worth stating accurately

Tempting to write "DuckDB's own lakehouse format breaks its own reader". It does not. MEASURED locally
(`ducklake` on 1.5.5, a 5000-row table with `DATA_INLINING_ROW_LIMIT 0` and an `UPDATE` to force a rewrite):

| file | columns | field_id |
|---|---|---|
| plain append | `id`, `v` | 1, 2 |
| UPDATE post-image | `id`, `v`, **`_ducklake_internal_row_id`** | 1, 2, **2147483540** |
| delete file | `file_path`, `pos` | 2147483646, 2147483645 |

`2147483540` is `MultiFileReader::ROW_ID_FIELD_ID`; the delete file's pair are `FILENAME_FIELD_ID` and
`ORDINAL_FIELD_ID`. So the reserved-id range is DuckDB's INTENDED mechanism for columns outside the user
schema, and DuckLake uses it consistently — it never emits an identifier-less column, so the field-id path's
assumption holds for it.

**That is the sharp version of the report**: the field-id mapping assumes a property only DuckLake
guarantees. Delta cannot provide it — the protocol names row-tracking columns through table PROPERTIES, so
they are not schema fields and there is nowhere for an id to live (measured for delta-spark, above).

⚠ **And DuckLake's row id is a DIFFERENT mechanism from the failing one, which supports the attribution
above rather than contradicting it.** `_ducklake_internal_row_id` is a REAL column IN THE FILE carrying a
reserved id, so `Find` succeeds and no default is ever needed. `file_row_number => true` appends a column
present in NO file, so `Find` must fail and the absent default is the defect.

⚠ **The two symptoms reported here are ONE gap seen from both ends**: DuckDB gives virtual columns a
reserved field id, and assumes every column in a field-id mapping has an identifier. A name-keyed map trips
the first assumption; a field-id-keyed map over a file with an identifier-less column trips the second.

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

⚠ **AND THAT IS TRUE OF EVERY DELTA WRITER, NOT JUST OURS — MEASURED 2026-08-13** on Fabric Spark
4.1.1.5.5 (delta-spark), row tracking + column mapping `id`, after an `UPDATE`. The contrast is inside ONE
file: the two user columns carry `field_id` 1 and 2, and `_row-id-col-<guid>` /
`_row-commit-version-col-<guid>` beside them carry **null**. It is structural rather than an oversight —
the Delta protocol names those columns through the table PROPERTIES
`delta.rowTracking.materializedRowIdColumnName` / `…RowCommitVersionColumnName` and resolves them BY
NAME, so they are not schema fields and there is nowhere for a `maxColumnId`-allocated id to live.

⇒ **the assertion is reachable from a SPARK-written table**, which is worth stating in the issue: it is
not one implementation's unusual output, it is what the Delta spec requires of everyone.
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

## 5. A NAMED argument is unusable in the CORRELATED shape of a table-in-out function

**Status: reproduced on our pinned 1.5.5, mechanism read from the source, NOT filed. ⚠ CHECKED AGAINST
UPSTREAM `main` @ `044a04a7` (2026-08-23): STILL PRESENT.** It bounds a shipped feature
(`ILateralFunction`, see docs/lateral_unnest_analysis.md §8), so the workaround matters more than the
report. **Decision (user, 2026-08-23): WAIT for an upstream fix rather than build a host-side one** — see
§5.1 for what was declined and why waiting is cheap.

**⚠ PARTLY SUPERSEDED FOR THE BIND-TIME CASE (2026-08-29):** the need this limitation actually bounded — a
per-call constant a lateral function reads AT BIND (e.g. to shape its output schema) — now has a shipped
spelling that works BARE in BOTH call shapes: declare `Params.Constant("name")` and the caller passes the
constant directly (`f(t.a, 'x,y')`); in the correlated shape the value is recovered from the synthesized
column's rendered expression text (a `const_arg(…)` wrapper existed for one day and was removed once the
text channel measured complete). Full record: docs/lateral_unnest_analysis.md §9/§9.1. ⚠ That is NOT a
named-parameter fix and does not touch the mechanism below — `f(t.a, opt := 5)` still does not bind, the
2026-08-23 decision to wait stands, and §5.1's declined host-side rebind is still declined. What changed is
only that the commonest reason to WANT a named argument in the correlated shape no longer needs one.

`f(t.a, opt := 5)` — a named argument alongside a column argument — does not bind:

```
Binder Error: No function matches the given name and argument types 'fabricator_lat_scale(INTEGER, INTEGER)'.
	Candidate functions:
	fabricator_lat_scale(INTEGER, factor : INTEGER)
```

The named argument became a POSITIONAL one, so the call's arity no longer matches the declaration. The
literal-argument form of the SAME function binds and honours it (`f(5, factor := 10)` ⇒ 50).

**Mechanism** (`src/planner/binder/tableref/bind_table_function.cpp:96-102`): `BindTableFunctionParameters`
tests the bind TYPE first, and for `TABLE_IN_OUT_FUNCTION` it calls `BindTableInTableOutFunction(expressions,
subquery)` and RETURNS. That helper moves **every** argument expression into a synthesized subquery's SELECT
list. The named-parameter extraction — the loop below it, whose own comment reads *"hack to make named
parameters work"* — is never reached, so `opt := 5` is bound as an ordinary comparison expression and becomes
a phantom input COLUMN. The literal form escapes because all-scalar arguments take the
`STANDARD_TABLE_FUNCTION` path, where that loop does run.

**A stock, extension-free demonstration of the same sweep** (not a full repro — `unnest` handles `recursive`
in the parser and declares no table-function named parameters at all, so it would refuse the named form
anyway):

```sql
CREATE TABLE t AS SELECT 1 AS id, [[1,2],[3]] AS l;
SELECT * FROM t, unnest(t.l, recursive := true);
-- Binder Error: ... 'unnest(INTEGER[][], BOOLEAN)'
```

The reported argument types are the tell: `recursive := true` arrived as a positional BOOLEAN. A real repro
needs an extension-registered in-out function that declares a named parameter.

**⚠ THE WORKAROUND IS THE PART THAT MATTERS, and it is not a compromise: declare a per-call constant
POSITIONALLY.** Under the row-mapped registration every positional slot is runtime data, so a literal written
at the call site arrives as a CONSTANT INPUT COLUMN — `f(t.a, 2)` works in both shapes, and the callee reads
the value per call instead of at bind. Measured working (`verify_lateral` §2). What genuinely remains
unavailable is a named parameter used correlated, i.e. bind-time configuration of a correlated call; the
default declared in `Bind` still applies there.

### 5.1 Re-checked against upstream `main`, and the decision to wait (2026-08-23)

**NOT FIXED on `main` @ `044a04a7`.** The early return is byte-identical to our pin:

```cpp
auto bind_type = GetTableFunctionBindType(table_function, expressions);
if (bind_type == TableFunctionBindType::TABLE_IN_OUT_FUNCTION) {
    BindTableInTableOutFunction(expressions, subquery);   // sweeps EVERY expression
    arguments = subquery.types;
    return true;                                          // <- the named-param loop is never reached
}
bool seen_subquery = false;
```

⚠ **The file MOVED a lot and none of it is the fix, which is why this needed a diff rather than a glance:**
97 insertions / 62 deletions between the pin and `main`, all API churn —
`GetFunctionReferenceByOffset` → `GetFunctionByOffset`, `.arguments` → `.GetArguments()`,
`can_contain_nulls = true` → `SetCanContainNulls(true)`, `string parameter_name` → `Identifier`, includes
shuffled. A "the file changed, so maybe it is fixed" reading would have been wrong in both directions.

⚠ **`GetTableFunctionBindType` was checked TOO, because that is the other place a fix could hide** — a
change there could route the named form down the `STANDARD_TABLE_FUNCTION` path where the extraction loop
does run. Its logic is unchanged (only the same accessor renames): a lateral has `in_out_function` set and
no TABLE parameter, so a call with one non-scalar argument still classifies as `TABLE_IN_OUT_FUNCTION`.

**Reproduce the check without touching the submodule's working tree** (ours carries unrelated local edits):

```bash
git -C duckdb fetch --depth 1 origin main
git -C duckdb diff HEAD FETCH_HEAD -- src/planner/binder/tableref/bind_table_function.cpp
git -C duckdb show FETCH_HEAD:src/planner/binder/tableref/bind_table_function.cpp
```

`fetch` + `show` only add objects and read a remote ref — no checkout, no pin move.

**WHAT WAS DECLINED, and it is worth recording because it is buildable:** a new declared `Params.Literal`
style meaning "this positional slot is a per-call constant". The host would lift the value from row 0 of the
first chunk, hand it to the session, and DROP that column from the batch so the callee's input matches its
declared per-row inputs. Sound — a swept literal is constant in every row, and the correlated plan
de-duplicates by distinct tuple so it cannot vary. **Its limit is what killed it: the value still would not
be available at BIND**, so it cannot drive an output schema, which is most of the reason to want a declared
constant at all.

⚠ **AND THE SCALAR TRICK IS NOT AVAILABLE HERE — the difference is upstream's, not ours.** ABI v80's scalar
bind folds constants because it receives argument EXPRESSIONS. `TableFunctionBindInput` carries only
`vector<Value> inputs`, `named_parameter_map_t named_parameters` and the input-table types/names — **no
expressions**. By the time `LateralBind` runs, DuckDB has already rewritten the positional arguments into a
relation and we see just its SCHEMA. There is nothing to fold.

⚠ **"Wait for upstream" is load-bearing on someone else filing it, since WE have not** (see the status line).
The code path's own comment reads *"hack to make named parameters work"*, so nobody is guarding it. Waiting
is cheap — the positional-constant idiom above is gated and works — but it is waiting, not scheduling. The
cheap move if that ever matters: file it. We have the reproduction, the file and line, and a one-line fix
direction (run the named-parameter extraction BEFORE the in-out sweep), which is the self-contained,
needs-nothing-of-ours shape CLAUDE.md records as making an offer land.

## 6. A TABLE function's `varargs` is omitted from the "Candidate functions" list — scalars render it

**Found 2026-08-31 while gating our own variadic table functions. Cosmetic — a message, never an answer —
but it MISDESCRIBES the function to the one person who needs the description: someone whose call just
failed.** A variadic table function's candidate is printed as though it had a fixed arity, so a caller reads
"this takes one argument" about a function that takes one or more.

**Stock repro, three lines, no extensions** (`duckdb==1.5.2` python wheel):

```python
import duckdb
duckdb.sql("SELECT concat_ws()")            # SCALAR    -> concat_ws(VARCHAR, ANY, [ANY...]) -> VARCHAR
duckdb.sql("SELECT * FROM test_vector_types()")  # TABLE -> test_vector_types(ANY, all_flat : BOOLEAN)
duckdb.sql("SELECT * FROM enable_profiling(1)")  # TABLE -> enable_profiling(format : VARCHAR, …, metrics : ANY)
```

`test_vector_types` and `enable_profiling` both declare a `varargs` (`ANY` and `VARCHAR[]` respectively —
`SELECT function_name, varargs FROM duckdb_functions() WHERE varargs IS NOT NULL` lists them), and neither
candidate shows it. The scalar path does: `Function::CallToString` appends `"[" + varargs.ToString() + "...]"`
when `varargs.IsValid()`, and the scalar binder's error goes through it.

**The control is the pair, not either half** — `concat_ws` and `test_vector_types` are both variadic, both
fail to bind, and only one renders the tail. Without the scalar beside it, the table rendering looks like a
deliberate choice rather than a gap.

⚠ **NOT FILED**, and the reason is worth stating rather than leaving to inference: it is a message-only
defect with an obvious workaround (read `duckdb_functions().varargs`), so it is worth an issue only if
someone is already in that code. Recorded here so the next person who sees a fixed-arity candidate for a
variadic function of ours does not go looking for a bug on our side — which is exactly what happened when
`verify_lateral`'s first draft asserted the tail would appear.

⚠ It is also why that gate asserts the candidate DuckDB actually prints
(`fabricator_lat_span(ANY, BIGINT)`) rather than the one it should: an assertion written against the
correct-but-absent rendering would fail forever, and "fix the test to match" is the wrong move only when the
behaviour under test is ours. Here it is not.

## Not a bug, but pinned here because it wasted time three times

`read_parquet` answers `count(*)` — and `count(<col>)` — from parquet footer metadata without decoding the
column. Any measurement or control built on one is **void**: it will report success for a query shape that
fails on a real decode, and near-zero time for a scan that never happened. Both happened here in one
session: the field-id + `file_row_number` combination was pronounced working on a multi-file `count(*)`, and
a `schema`-vs-plain timing comparison showed a 10x difference that disappeared entirely under
`sum(length(s))`.

Force a real decode (`sum(…)`, `sum(length(…))`) in every control on this surface.
