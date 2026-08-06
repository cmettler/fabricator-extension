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

## 2. `Information loss on integer cast: value 4294967296` — NOT reproducible upstream, so NOT ours to file

**Status: our bug or an interaction, not a DuckDB one. Do not file.**

Reading a partitioned Delta table through the batched path raised:

```
INTERNAL Error: Information loss on integer cast: value 4294967296 outside of target range [0, 4294967295]
```

`4294967296` is `2^32`, i.e. something ≥ `UINT32_MAX` being cast to `uint32`. It was **recorded here and in
the code as a DuckDB assertion caused by `union_by_name` + a virtual column over a hive layout. That
attribution is WRONG** — the minimal form works fine:

```sql
COPY (SELECT 1 AS id, 'x' AS p UNION ALL SELECT 2, 'y') TO 'hive' (FORMAT parquet, PARTITION_BY (p));
-- OK: (1, 0, 'x'), (2, 0, 'y')
SELECT * FROM read_parquet(['hive/p=x/data_0.parquet', 'hive/p=y/data_0.parquet'],
                           union_by_name => true, file_row_number => true);
```

with `union_by_name` alone, `file_row_number` alone, and `hive_partitioning => false` all fine too. Four
controls, no failure. **The error therefore comes from something our generated SQL adds**, and writing it up
as upstream would have been a false report.

### Narrowed further, and the first hypothesis was WRONG

The lead recorded here first was a name collision: `hive_partitioning` is auto-detected from the paths, so
`read_parquet` emits a partition column of its own while our projection emits the same one from the bound
metadata input. **Tested: `hive_partitioning => false` does NOT fix it.** (It was kept anyway — it is a real
guard, since any `x=y` directory anywhere in a table's path would otherwise inject a phantom column — but it
is not the cause.)

What IS ruled out, each checked against the stock 1.5.5 wheel over the exact failing files, with a real
decode rather than a `count(*)`:

| candidate | verdict |
|---|---|
| `read_parquet` itself — `union_by_name` × `hive_partitioning=false` × `filename` × `file_row_number`, every combination | all fine |
| hive auto-detection colliding with our projected column | **refuted** — `hive_partitioning => false` still fails |
| the SQL SHAPE — the byte-identical statement with `__fab_f` as an inline `VALUES` CTE | **fine, returns the right 20 rows** |

⇒ the failure is in the **bound metadata Arrow input** (`MetaStream`), not in DuckDB. The one thing that
distinguishes a partitioned scan there is the extra `p<i>` VARCHAR column beside `fn`/`ord` — a 3-column
input where the working non-partitioned one has 2, and where the deletion-vector input (2 columns, also with
a string column) is fine.

**Next step: dump what `MetaStream` actually exports before theorising again.** Two hypotheses have now died
here, both of which looked obvious.

⚠ Also observed once: the unittest process **SEGFAULTED** rather than raising, so this is not a containable
failure. Treat the gate as load-bearing.

**The gate stays and its comment says "cause narrowed, not found" — it must not blame upstream.** A wrong
attribution in a code comment is worse than none: it stops the next person looking.

---

## Not a bug, but pinned here because it wasted time three times

`read_parquet` answers `count(*)` — and `count(<col>)` — from parquet footer metadata without decoding the
column. Any measurement or control built on one is **void**: it will report success for a query shape that
fails on a real decode, and near-zero time for a scan that never happened. Both happened here in one
session: the field-id + `file_row_number` combination was pronounced working on a multi-file `count(*)`, and
a `schema`-vs-plain timing comparison showed a 10x difference that disappeared entirely under
`sum(length(s))`.

Force a real decode (`sum(…)`, `sum(length(…))`) in every control on this surface.
