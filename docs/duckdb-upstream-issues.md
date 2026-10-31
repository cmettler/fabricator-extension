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

**Status: reproduced on stock 1.5.5 AND ROOT-CAUSED in the source (§ below). ⚠⚠ FIXED IN DuckDB 2.0 —
do NOT file it (measured 2026-10-24, the same probes on our 1.5.6 and 2.0 builds side by side).** On 2.0
the query below returns `(1, 0)`, and the same holds over several files where only some carry field ids.
The missing-default path still exists, but it now throws an ordinary `InvalidInputException` that names the
field (*"Field \"x\" (field id N) is missing from the file schema and has no default expression"*)
instead of an internal error that invalidates the database. It is NOT backported: 1.5.6 still reproduces
it exactly as recorded below.

**The other `schema` limits we measured on 1.5 are lifted by 2.0 as well**, and they matter more to us than
this one did:

| shape | 1.5.6 | 2.0 |
|---|---|---|
| NAME-keyed map + `file_row_number` | fails — *"column 2147483645 … could not be found"* | ✅ |
| a nested member mapped through the entry's 4th field `children`, by id or by name | **accepted and SILENTLY IGNORED** — the member reads NULL | ✅ |
| a nested member no file carries, with a default | binder error | ✅ default applied |
| `schema` + `union_by_name` / `hive_partitioning => true` | refused | still refused |
| a column with NO field id, reached through an id-keyed map | impossible | still impossible |

⚠ The `children` row is the dangerous half of 1.5: a map that LOOKS like it renames a struct member is
silently a no-op there. On 2.0, `schema` and `file_row_number` moved from the parquet reader into the
generic multi-file reader, which is where the fix lives (`multi_file_reader.cpp`'s `ParseSchemaMap`).
What 2.0 enables for us is in `DeltaNativeReader.TryFullForm`'s remarks; the new upstream defect it exposes
is §11.

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

**⚠⚠ RESOLVED UPSTREAM — IT IS IN `v2.0-cyanoptera`, AND IT IS NOT IN THE 1.5 LINE. Verified by CONTENT
(2026-10-20), never by PR state, which is the rule this file already carries.** `src/common/local_file_system.cpp`
on the 2.0 branch has both halves: `flags.ExclusiveCreate()` → `CREATE_NEW`, tested FIRST in the `open_write`
block, and a `ReturnNullIfExists()` arm accepting `ERROR_FILE_EXISTS` **and** `ERROR_ALREADY_EXISTS`. The same
file at tag **`v1.5.6` contains ZERO occurrences of `CREATE_NEW`**, so everything under "What it costs us"
below is still live for anything built on the 1.5 line — including the bump this note was written during.
- **The local working-tree patch in the `duckdb` submodule is DELETED (2026-10-20, user decision: 2.0 is the
  destination, and the 1.5 gap is accepted).** ⚠ It had **never reached a shipped artifact** — CI checks the
  submodule out clean at the pinned sha — so it only ever made THIS machine behave unlike every build we
  release. Deleting it ends a hidden dev-vs-shipped divergence rather than taking something from users, which
  is the part worth carrying: *a patch that lives only in a submodule working tree is not a fix, it is a
  divergence*. `git show v2.0-cyanoptera:src/common/local_file_system.cpp` is the durable copy.
- ⚠ The "make the failure classifiable" ask in the tail below is only **half** answered upstream: a caller
  setting `NULL_IF_EXISTS` now gets a null handle instead of locale-dependent prose, but a plain exclusive
  create still throws a stringified Win32 error with no structured errno.

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

---

## 7. ⚠⚠ RETRACTED — a cast-wrapped column DEFAULT is NOT lost; it is a deliberate three-statement rewrite

**Recorded 2026-09-08 as a DuckDB defect. RETRACTED 2026-09-09 after reading the transform and measuring.
There is no upstream bug here, and it was never filed — which is the one thing this file's own standing
rule got right.** The section is kept rather than deleted because the measurements below are all CORRECT
and it is the INFERENCE drawn from them that was wrong, which is the more useful thing to be able to
re-read.

### What was measured (unchanged — every row still reproduces)

`ALTER TABLE <foreign>.t ADD COLUMN e <type> DEFAULT <literal>` reaches the catalog's `Alter` as an
`AddColumnInfo` whose `new_column.DefaultValue()` is a `VALUE_CONSTANT`. Probing that expression directly:

| written | arrives as |
|---|---|
| `INTEGER DEFAULT 10` | `10` |
| `DOUBLE DEFAULT 1.5` | `1.5` |
| `DECIMAL(9,2) DEFAULT 1.50` | `1.50` |
| `VARCHAR DEFAULT 'x'` | `'x'` |
| `DATE DEFAULT '2024-01-01'` | `'2024-01-01'` |
| `BOOLEAN DEFAULT true` | **`NULL`** |
| `DATE DEFAULT DATE '2024-01-01'` | **`NULL`** |
| `TIMESTAMP DEFAULT TIMESTAMP '…'` | **`NULL`** |
| `BLOB DEFAULT 'A'::BLOB` | **`NULL`** |

### ⚠⚠ THE INFERENCE THAT WAS WRONG

*"anything the parser wraps in a CAST arrives as a constant holding NULL ⇒ DuckDB has LOST the value before
a foreign catalog sees it."* The first clause is true. The second does not follow, and it is false.

`transform_alter_table.cpp` splits `ADD COLUMN … DEFAULT` on `ExpressionClass::CONSTANT`. A **non-constant**
default is deliberately rewritten into THREE statements, with the reason in an upstream comment — a WAL
replay must not re-evaluate `random()`/`current_timestamp` into a different answer:

1. `ALTER TABLE t ADD COLUMN col <type> DEFAULT NULL;`  ← **the only one a catalog's `Alter` receives**
2. `UPDATE t SET col = <expression>;`                   ← materialises the value for existing rows
3. `ALTER TABLE t ALTER col SET DEFAULT <expression>;`  ← reinstates the real default

So the NULL is statement 1's own honest placeholder, not a dropped value. `true` and `DATE '…'` take that
path because DuckDB parses each as a CAST rather than a constant — visible on its OWN tables, where
`information_schema.columns.column_default` renders them as `CAST('t' AS BOOLEAN)` and
`CAST('2024-01-01' AS "DATE")`.

### ⚠⚠ THE "CONTROL" WAS PART OF THE THING IT WAS CONTROLLING FOR

The finding rested on: *"`ALTER COLUMN … SET DEFAULT` carries the same boolean and date correctly, which
localises the problem to `AddColumnInfo`."* That is **statement 3 of the very rewrite** — so the control was
observing the mechanism succeed and being read as evidence that a different path had failed. A control drawn
from inside the process under test cannot discriminate.

**The discriminator that settles it is one keyword**, and it needs no C++ at all:
`ADD COLUMN IF NOT EXISTS c BOOLEAN DEFAULT true` takes the `missing_ok` branch, which skips the rewrite —
and the value lands on SQL Server as `((1))`, backfills the existing row, and is applied to later inserts.
Same statement, same type, one keyword apart from the spelling that "loses" it.

### What we do about it, and why the behaviour did not change

`fabricator` still REFUSES a NULL-arriving default on `ADD COLUMN`, for a corrected reason. At statement 1
an honest `DEFAULT NULL` and the rewrite's placeholder are the same NULL constant, and accepting it hands
the caller a sequence that completes only sometimes: statement 2 is an UPDATE, and on a table with no
PK/unique/identity our rowid requirement refuses it (measured), leaving the column added, unbackfilled and
defaultless behind a statement that failed with an error about UPDATE. Refusing at statement 1 is a clean
failure with no partial state. Gate: `verify_alter_default` §3; the message now names `IF NOT EXISTS` as the
workaround, which is both simpler and measured.

### The transferable lesson

Three things were measured correctly and assembled into a wrong mechanism: the value is absent, the absence
is type-dependent, and a neighbouring path works. **The missing step was reading the code that produces the
`AlterInfo`** — 20 lines of `transform_alter_table.cpp` — rather than inferring a cause from the shape of
the symptom. This file already carries the same lesson under §2 for a different finding; it is now carrying
it twice.

---

## 8. `ROW_GROUP_SIZE` is SILENTLY IGNORED below ~8192 when the COPY's source supplies batch indexes — a 2.0 regression

**DuckDB 2.0 (`v2.0-cyanoptera`); 1.5.6 does the right thing.** A `COPY … TO … (FORMAT parquet,
ROW_GROUP_SIZE n)` whose source hands the copy BATCH INDEXES — i.e. the plan is `PhysicalBatchCopyToFile`,
which `read_parquet`, a table scan and `duckdb_arrow_scan` all produce — writes **one row group per source
batch** instead of merging them to `n`, for any `n` below roughly four vectors. No warning, no error; the
option is simply inert and the file's row groups come out at the source's granularity.

### Repro — stock wheel, no extension

```sql
-- a source whose batches are one vector each
COPY (SELECT i, 'v'||i AS s FROM range(20000) t(i)) TO 'small.parquet' (FORMAT parquet, ROW_GROUP_SIZE 2048);
SELECT count(DISTINCT row_group_id) FROM parquet_metadata('small.parquet');   -- 10

-- now ask for 5000-row row groups while copying it
COPY (SELECT * FROM read_parquet('small.parquet')) TO 'merged.parquet' (FORMAT parquet, ROW_GROUP_SIZE 5000);
SELECT count(DISTINCT row_group_id) FROM parquet_metadata('merged.parquet');
-- 2.0   : 10   (unchanged — ROW_GROUP_SIZE had no effect)
-- 1.5.6 :  4   (merged, 6144/6144/6144/1568)
```

### The controls that turn this into a report

**(a) It is a THRESHOLD, not a total failure.** Same 2048-row source, sweeping `ROW_GROUP_SIZE` on 2.0:

| ROW_GROUP_SIZE | row groups | max rows/group |
|---|---|---|
| 1000 | 10 | 2048 |
| 4096 | 10 | 2048 |
| 5000 | 10 | 2048 |
| 8192 | 3 | 8192 |
| 10000 | 2 | 10240 |
| 20480 | 1 | 20000 |
| 122880 (the default) | 1 | 20000 |

The cut sits at `4 * STANDARD_VECTOR_SIZE`. That matches the batch analyzer's tolerance: a source batch
within **two vectors** of the requested size is accepted as-is and written as its own row group, and with
`ROW_GROUP_SIZE 5000` the requested size is itself only two vectors, so a one-vector batch always qualifies.
⚠ Note the option does not act as a CAP either — `ROW_GROUP_SIZE 1000` does not split a 2048-row batch.
The source's boundaries win in both directions.

**(b) It is the BATCH path specifically, which is what makes it easy to miss.** The same request against a
source with NO batch index (`range()` directly) honours the option on 2.0: 4 row groups of
6144/6144/6144/1568. So the option works, is not misspelled, and is not being dropped at bind — it reaches
the writer and is then overruled by the source's batching.

**(c) The default is unaffected**, which is why this does not show up as a general layout regression:
`ROW_GROUP_SIZE` defaults to 122880, which is far above the threshold.

### Why it matters here

Our Delta native write is `COPY (SELECT … FROM <registered arrow view>) TO <file> (FORMAT parquet, …)`, and
the arrow view always supplies batch indexes (that is what preserves insertion order under a parallel scan).
So the user-facing `parquet_row_group_size` write option became inert for any value below ~8192 — accepted,
persisted into the Delta table's configuration, and then ignored by the writer. Gated in
`test/verify_with_options.test` §1, whose comment names this entry; the suite now asks for 10000 so the row
still gates that the option reaches the writer at all.

---

## 9. A VARIANT-less `NULL` column exports as an Arrow null array with `null_count = 0`

**DuckDB 2.0.** The Arrow spec says a null array's `null_count` **is** its length. `ArrowNullData` leaves it
at 0, so a strict Arrow consumer rejects the batch. Apache.Arrow (C#) does, with
*"Length must equal null count (Parameter 'data')"* — i.e. an exported result containing an untyped NULL
column kills the consumer rather than yielding nulls.

### Repro

```sql
SELECT NULL AS v;         -- DESCRIBE: 2.0 says `v null`, 1.5.6 said `v integer`
```

Export that result over the Arrow C data interface and read it with a consumer that validates the layout.

### The controls

**(a) The TYPE change is deliberate and is not the bug.** 1.5.6 bound `SELECT NULL AS v` as INTEGER at bind
time; 2.0 keeps `"null"`. That alone is defensible — it is the export's `null_count` that violates the spec.

**(b) It is the null TYPE, not nulls in general.** `SELECT NULL::INTEGER AS v` exports an INT32 array with
`null_count = 1` and is accepted.

**(c) It is not exotic.** Our decision-table macros render a blank output cell as a bare `NULL`, so an
ordinary user query hits it.

### What we do about it

`PromoteNullColumns` restores 1.5's observable type at our own boundary (the values are constant NULL by
construction, so nothing is lost). That is a workaround in our code, not a fix: any other consumer of
DuckDB's Arrow export has the same exposure.

---

## 10. `arrow_scan` reads the WRONG COLUMN when the factory does not narrow the stream — silent, 2.0

⚠ **STATUS: measured through our own `ArrowScanFactory`, NOT yet through the stock C API.** The mechanism
below is read from DuckDB's source and the behaviour is measured end to end here; the C-API half is an
INFERENCE from the code and must be reproduced with a small C program before this is filed. It is written
up now because the mechanism is exact and the workaround is in our tree.

### The mechanism, read from 2.0's source

`ArrowTableFunction::ArrowToDuckDB` indexes the incoming arrow array **by position**:

```cpp
auto arrow_array_idx = arrow_scan_is_projected ? idx : col_idx;          // src/function/table/arrow.cpp
static void ArrowToDuckDB(..., bool arrow_scan_is_projected = true, ...); // arrow.hpp — DEFAULTS to true
```

So `arrow_scan` assumes the `ArrowScanFactory` honoured `parameters.projected_columns` and produced a
stream narrowed to the projected columns in projection order. A factory that hands back the caller's
stream whole therefore has column *k* of the result read from arrow child *k* of the **full** array: the
values are of the right type and the query succeeds, they are simply the wrong column's.

⚠ **`CArrowScanFactory`, behind the public `duckdb_arrow_scan`, is such a factory** —
`src/main/capi/v1/arrow-c.cpp`'s `ProduceStream` takes `parameters` and never reads it. That is the
inference above: every C-API-registered stream should be exposed to this, and the Python path should not
be, because DuckDB's own Python factory passes `projected_columns.columns` into the reader it builds.

### What was MEASURED, with the controls

Our named Arrow inputs (the per-file metadata and deletion-vector views the Delta native reader binds)
were registered with `arrow_scan` and a factory shaped exactly like `CArrowScanFactory`. On a Delta
row-tracking read of a metadata view `[fn, base_row_id, commit_version]`:

| what was projected | `__delta_row_commit_version` | correct? |
|---|---|---|
| `__delta_row_id` + the version | **24** (a `baseRowId`) | ✗ — the log says 8 |
| the version alone | 8 | ✓ |
| `s` + the version | 8 | ✓ |
| all rows, no filter | 8 | ✓ |

**(a) Forcing an UNREAD column into the view breaks a shape that was correct.** With `base_row_id` emitted
but not referenced by the generated SQL, the `s` + version shape flips from 8 to **24** — i.e. the symptom
follows the arrow view's column COUNT, not the query's text.

**(b) SWAPPING the two columns in the view fixes the failing shape** (24 → 8), which is what identifies the
read as positional rather than by name.

**(c) 1.5.6 answers 8 for every one of those shapes, on the same files and the same C# stream**, so this is
a 2.0 change and not a long-standing defect in our view construction.

**(d) The C# side was exonerated before the C++ side was blamed**: a probe printing the built metadata, and
a second one running `SELECT * FROM <the view>` back through the host, both showed `base_row_id = 24,11`
and `commit_version = 8,1` — correct, and correctly ordered, on the way in.

### The projection DuckDB actually asks for — measured at the factory, both registrations

A probe in `FabricatorArrowScanFactory::ProduceStream` printing `parameters.projected_columns.columns`,
on the same Delta row-tracking query, switching only the registered function:

```
arrow_scan       PROBE projected=[fn,commit_version]                filters=none   -> answers 24  (WRONG)
arrow_scan_dumb  PROBE projected=[fn,base_row_id,commit_version]    filters=none   -> answers 8   (right)
```

So DuckDB **did** prune `base_row_id` out of a three-column view — a NON-PREFIX subset `{0,2}` — even
though the generated SQL references it, and the factory then handed back all three children. Position 1 of
the request landed on child 1 (`base_row_id`) instead of child 2. ⚠ That is the SAME SHAPE as §2, which on
1.5.6 SEGFAULTED; 2.0 reads it silently instead. **§2 and this entry are one bug with two symptoms.**

### The FILTER half of the same contract, and why `arrow_scan_dumb` closes it too

`arrow_scan` declares `filter_pushdown = true`, and DuckDB's optimizer **removes** a filter from the plan
once it is fully pushed — `pushdown_get.cpp` ends the generic-expression loop with `filters.erase_at(i)` on
`FilterPushdownResult::PUSHED_DOWN_FULLY`, and `GenerateTableScanFilters` moves the rest into
`get.table_filters`. The scan is then trusted to apply them.

**DuckDB's arrow scan never applies them.** `parameters.filters` is handed to the factory and
`ArrowScanLocalState::filters` is *stored and never read* — grep-verified across `arrow.cpp` and
`arrow_conversion.cpp`. pyarrow's factory satisfies the contract; `CArrowScanFactory` does not, and neither
did ours. ⇒ a TableFilter pushed into such a view is **silently dropped**, i.e. too many rows.

⚠ **Not reached in practice here, and that IS measured**: with the probe armed across all 76 hermetic
suites, **1162 named-input scans in 58 suites reported `filters=none` without exception** — so our
generated SQL never puts a constant predicate directly on one of these views, and the hazard was latent.
`arrow_scan_dumb` (`filter_pushdown = false`, `filter_prune = false`) removes it by construction.
⚠⚠ THE FIRST ATTEMPT AT THIS SWEEP MEASURED NOTHING AND LOOKED LIKE A CLEAN RESULT: `run-suites.sh`
captures each suite's output into a variable and prints it only on FAILURE (line 1580), so the probe's
lines were discarded and the count came back ZERO — indistinguishable from "no filters anywhere". The
rerun drives the suites directly and keeps stderr; the 1162 lines ARE the positive control.
⚠ The sweep covers the HERMETIC tier only. A plugin calling `Host.Query` with its own wide input and a
WHERE is the shape that would both reach the filter path and make the lost projection pushdown matter.

### Why `arrow_scan_dumb` is the right shape and not a retreat

`ArrowScanBindDumb` sets `projection_pushdown_enabled = false`, and `ArrowScanInitLocalInternal` then does
`result->column_ids.clear()` — which makes `ArrowToDuckDB` fall back to the identity. **That is exactly
what our own `fabricator_scan` has always done**: `ArrowStreamInitLocal` clears `column_ids` too, ingests
every column of the actual result, and projects by NAME-resolved positions computed at bind
(`output_source_pos`). The two paths now share one invariant instead of disagreeing about it.

⚠ The two paths and what each is for, since the split is easy to lose:

| path | function | projection | used by |
|---|---|---|---|
| ours | `fabricator_scan` | narrowed AT THE PRODUCER, mapped BY NAME | the catalog scan, fluid's `input_table`, `publish()` |
| copied | `RegisterArrowInputView` | identity, no pushdown (since `arrow_scan_dumb`) | `Host.Query(sql, inputs)` named inputs — the Delta metadata and deletion-vector views, bulk-write inputs |

### Our workaround

`arrow_scan_dumb` — upstream's own variant, same bind and same scan with projection and filter pushdown
off, so `column_ids` is the identity and positional indexing is right again. The cost is that a named
input crosses whole. The real fix, ours or upstream's, is a factory that narrows the produced stream.

---

## 11. An id-keyed `schema` map SILENTLY NULLs every column after the first one without a field id — 2.0

**Status: reproduced on our 2.0 build 2026-10-24 with the control beside it, mechanism read from the source.
NOT filed. Ready to file — it is a wrong answer with no error.**

```sql
-- 'noid' carries no field id and comes FIRST; 'a' carries field id 1 and comes after it
COPY (SELECT 2 AS noid, 1 AS a) TO 'noidfirst.parquet' (FORMAT parquet, FIELD_IDS {a: 1});

SELECT * FROM read_parquet('noidfirst.parquet',
    schema = map {1: {'name': 'a', 'type': 'INTEGER', 'default_value': NULL}});
-- a = NULL   (wrong — the file holds 1 under field id 1)
-- with 'default_value': -1 the same query answers -1, i.e. the column is treated as ABSENT
```

**Control:** the same columns in the other order — the id'd column first, the id-less one after — read
correctly (measured with id-less row-tracking columns trailing, nested `children` and `file_row_number` all
present).

**Cause** (`multi_file_column_mapper.cpp`, `FieldIdMapper`): building the field-id lookup stops at the FIRST
local column without an identifier — `if (column.identifier.IsNull()) { break; }`, commented *"Extra columns
at the end will not have a field_id"*. That is an assumption about the file's LAYOUT, and every column after
the break is missing from the map, so it resolves to its default. `continue` is the obvious fix: an id-less
column cannot be matched by id anyway, and skipping it costs nothing.

**Why it does not reach us today, verified rather than assumed:** the only id-less columns in a Delta data
file are the materialized row-tracking ones, and every writer we know appends them LAST — measured in files
Fabric Spark wrote (`_row-id-col-…` then `_row-commit-version-col-…`, after all data columns, both mapping
modes), read in delta-spark's source (`UpdateCommand`: `targetOutput ++ rowId ++ rowCommitVersion`; MERGE and
OPTIMIZE the same, all via `withColumn`), in engineered-wood (`RowTrackingWriter.AddRowIdAndCommitVersion
Columns` appends both) and in our native writer (keeps the batch's order). A writer that puts one first would
make an id-keyed read return NULLs with no message.

## 12. A VIEW in a nested schema binds its body against the LEAF schema — a wrong answer, 2.0

Found 2026-10-26 while nesting our declared views (ABI v97). Ready to file; native control below.

```sql
CREATE SCHEMA eu; CREATE SCHEMA sales; CREATE SCHEMA sales.eu;
CREATE TABLE eu.src(w VARCHAR);       INSERT INTO eu.src VALUES ('TOP');
CREATE TABLE sales.eu.src(w VARCHAR); INSERT INTO sales.eu.src VALUES ('NESTED');
CREATE VIEW sales.eu.v  AS SELECT * FROM src;            -- unqualified
CREATE VIEW sales.eu.v2 AS SELECT * FROM sales.eu.src;   -- qualified (control)
SELECT * FROM sales.eu.v;    -- TOP     ← another schema's rows, no error
SELECT * FROM sales.eu.v2;   -- NESTED
```

- **Cause:** `bind_basetableref.cpp`, the `VIEW_ENTRY` case, builds the view binder's search path with
  `GetSearchPath(view.ParentCatalog(), view.ParentSchema().name, true)` — the parent schema's NAME, i.e. the
  leaf of `GetSchemaPath()`. `CatalogSearchEntry` holds one schema identifier, so a path cannot be expressed
  there at all; the fix needs the search entry to carry a path (or the view binder to resolve against the
  parent entry directly).
- ⚠ Silent whenever a top-level schema shares the leaf name; when none does, an unqualified reference merely
  fails to resolve, which hides the cause.
- **Our workaround:** `FabricatorSchemaEntry::QualifyNestedViewBody` qualifies a declared view's body at
  materialization (depth ≥ 2 only). Gate `verify_delta_nested_schemas` §12, mutant killed at :446.

## 13. A CTAS into a nested schema creates an EMPTY table when the leaf names an existing one — silent data loss, 2.0

Found the same day, by the probe for §12. Ready to file; native control below.

```sql
CREATE SCHEMA eu; CREATE SCHEMA a; CREATE SCHEMA a.eu;
CREATE TABLE eu.src AS SELECT 'TOP' AS w;
CREATE TABLE a.eu.src AS SELECT 'A' AS w;    -- succeeds
SELECT count(*) FROM a.eu.src;                -- 0   ← the query's row is gone
CREATE TABLE a.eu.other AS SELECT 'X' AS w;   -- control: no leaf collision
SELECT count(*) FROM a.eu.other;              -- 1
```

- **Cause:** `PhysicalPlanGenerator::CreatePlan(LogicalCreateTable)` (`plan_create_table.cpp`) decides between a
  CTAS and a bare CREATE with `catalog.GetEntry(TABLE_ENTRY, create_info.GetQualifiedName().Schema(), name)` —
  `Schema()` is the element before the name, i.e. the LEAF. It finds `eu.src`, takes the "already exists"
  branch, plans `PhysicalCreateTable` without the query, and the create itself then succeeds in `a.eu`
  because nothing is there. The bound schema (`op.schema`) is the right one; the check should ask it.
- ⚠ The top-level table is untouched, so nothing anywhere looks wrong except the missing rows.
- **Our workaround:** an optimizer extension replaces a CTAS into a nested fabricator schema with
  `FabricatorNestedCtasOperator`, whose `CreatePlan` repeats the planner's decision with the existence check
  asked of the schema entry. ⚠ `PRAGMA disable_optimizer` disables it. Gate `verify_delta_nested_schemas` §12,
  mutant killed at :465.

## 14. The remote pushdown optimizer checks a bare table name in `main`, not in the search path's schema — 2.0

Found 2026-10-31 (docs/duckdb-2.0-remote-execution.md §4.22). NOT filed. A missed pushdown, never a wrong answer.

- `RemotePushdownOptimizer::Rewrite(BaseTableRef)`, case 2 (no catalog in the name): after deciding the single
  remote catalog of the search path, it confirms the table with `LookupEntry(remote_catalog, table, schema_path)`,
  which qualifies an empty schema path with `DEFAULT_SCHEMA` (`main`). The search path's schema for that catalog
  (`USE db.dbo` ⇒ `dbo`) is not consulted — although `EntryExistsInLocalCatalog`, two lines above, does use the
  search-path schema for local catalogs.
- So a remote catalog without a `main` schema never gets a bare-name statement. duckdb-postgres works around it by
  answering `main` with its default schema (`PostgresCatalog::LookupSchema`, `c045f6c`); we do the same now.
- Even with that alias the check looks in the DEFAULT schema: after `USE db.sales`, a table only in `sales` is never
  pushed. The fix is to look it up in `GetSchemasForCatalog(remote_catalog)` from the search path.

## Not a bug, but pinned here because it wasted time three times

`read_parquet` answers `count(*)` — and `count(<col>)` — from parquet footer metadata without decoding the
column. Any measurement or control built on one is **void**: it will report success for a query shape that
fails on a real decode, and near-zero time for a scan that never happened. Both happened here in one
session: the field-id + `file_row_number` combination was pronounced working on a multi-file `count(*)`, and
a `schema`-vs-plain timing comparison showed a 10x difference that disappeared entirely under
`sum(length(s))`.

Force a real decode (`sum(…)`, `sum(length(…))`) in every control on this surface.

## Appendix — records moved verbatim from CLAUDE.md (2026-09-18)

CLAUDE.md carried these as-built records inline until it grew to 10,776 lines — a file loaded into every
session's context. They are moved here VERBATIM; CLAUDE.md keeps each entry's summary head plus a pointer to
this section. The one edit made on the way: a link that pointed into the docs directory is rewritten relative
to this directory, so it still resolves from here.

- **⚠⚠ upstream-issues §7 IS RETRACTED — there was no DuckDB bug, and the retraction is worth more than the
  finding was (2026-09-09).** It recorded *"a cast-wrapped column DEFAULT is flattened to NULL in the
  AlterInfo a FOREIGN catalog receives"*. Every MEASUREMENT in it reproduces; the INFERENCE was wrong.
  - **THE REAL MECHANISM: `transform_alter_table.cpp` deliberately REWRITES `ADD COLUMN … DEFAULT <non-
    constant>` into THREE statements** — add with `DEFAULT NULL`, `UPDATE` to materialise, `SET DEFAULT` to
    reinstate — so a WAL replay cannot re-evaluate `random()`/`current_timestamp` differently. We see
    statement 1 of 3, legitimately carrying NULL.
  - **⚠⚠ THE "CONTROL" WAS PART OF THE MECHANISM IT WAS CONTROLLING FOR.** The finding rested on *"`ALTER
    COLUMN … SET DEFAULT` carries the same boolean correctly, which localises it to `AddColumnInfo`"* — that
    IS statement 3 of the same rewrite. **A control drawn from inside the process under test cannot
    discriminate.**
  - **THE DISCRIMINATOR IS ONE KEYWORD**: `ADD COLUMN IF NOT EXISTS c BOOLEAN DEFAULT true` takes the
    `missing_ok` branch, skips the rewrite, and lands `((1))` on the server with the existing row backfilled.
    Same statement, same type, one keyword from the spelling that "loses" it.
  - **THE REFUSAL STAYS, on a corrected reason (user decision: fix the reasoning, keep the behaviour).** At
    statement 1 an honest `DEFAULT NULL` and the placeholder are the same NULL constant, and accepting it
    hands the caller a sequence that completes only sometimes: statement 2 is an UPDATE, and on a table with
    no PK/unique/identity our rowid requirement REFUSES it (measured), leaving the column added,
    unbackfilled and defaultless behind a statement that failed with an error about UPDATE. The code comment,
    the error MESSAGE (which now names `IF NOT EXISTS`) and the gate's §3 comment are all rewritten.
  - **THE LESSON, and this file now carries it twice**: three things were measured correctly and assembled
    into a wrong mechanism. The missing step was reading the 20 lines that PRODUCE the `AlterInfo`, rather
    than inferring a cause from the shape of the symptom.
