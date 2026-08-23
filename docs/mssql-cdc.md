# Log-based change capture for SQL Server — DESIGN (nothing built)

> **Status: DESIGN ONLY, 2026-08-23. No code, no ABI, no gate.**
>
> **The ask (user, 2026-08-23):** change data capture for SQL Server, with **no message queue** anywhere in
> it. A small set of *very usable* table/scalar functions that both **set capture up** and **return the
> stream** — ideally one common reader taking a table name plus options. An optional **initial snapshot**
> that then continues with changes.
>
> **EVIDENCE DISCIPLINE.** Every fact tagged **MEASURED** was run on 2026-08-23 against the docker rig
> (`mcr.microsoft.com/mssql/server:2025-latest`, SQL Server 2025) in a throwaway `CdcProbe` database, and
> through this extension's own `duckdb.exe` where the claim is about us. The probe database was **dropped**
> and the rig verified back to its original seven databases. Facts read from SQL Server's documented
> contract but **not** run here are tagged ⚠ **UNVERIFIED** — and §11 lists the ones worth settling before
> any of this is built, because two of them decide which implementation to pick.

---

## 0. The headline: the plumbing already works. What is missing is not plumbing.

**MEASURED, with zero new code.** Attach a CDC-enabled database and the whole raw surface is already
there:

```sql
ATTACH 'Server=…;Database=CdcProbe;…' AS cp (TYPE fabricator);

SELECT schema_name, table_name FROM duckdb_tables()    WHERE database_name='cp' AND schema_name='cdc';
-- cdc.captured_columns  cdc.change_tables  cdc.dbo_orders_CT  cdc.dbo_orders_v2_CT
-- cdc.ddl_history       cdc.index_columns  cdc.lsn_time_mapping                        (7 rows)

SELECT function_name FROM duckdb_functions() WHERE database_name='cp' AND schema_name='cdc';
-- fn_cdc_get_all_changes_dbo_orders      (+ _each)   fn_cdc_get_net_changes_dbo_orders      (+ _each)
-- fn_cdc_get_all_changes_dbo_orders_v2   (+ _each)   fn_cdc_get_net_changes_dbo_orders_v2   (+ _each)
```

and both of them *work*:

```sql
DESCRIBE SELECT * FROM cp.cdc.dbo_orders_v2_CT;
--  __$start_lsn blob | __$end_lsn blob | __$seqval blob | __$operation integer
--  __$update_mask blob | id integer | customer varchar | amount decimal | notes varchar
--  updated timestamp | region varchar | __$command_id integer

SELECT __$operation AS op, id, amount, region
FROM   cp.cdc.fn_cdc_get_all_changes_dbo_orders_v2(
         '\x00\x00\x00\x2D\x00\x00\x0B\x30\x00\x66'::BLOB,
         '\x00\x00\x00\x2E\x00\x00\x03\xA0\x00\x22'::BLOB, 'all update old');
--  op=2  id=3  amount=5.0000  region=eu
```

**Why each of those works, established rather than assumed:**

| mechanism | why it already reaches us | evidence |
|---|---|---|
| the `cdc` schema is visible | `SchemasSql` excludes only `sys`/`INFORMATION_SCHEMA`/the fixed db roles — not `cdc` | MEASURED |
| the change tables are visible | `TablesSql` reads `sys.tables` with **no `is_ms_shipped` filter**, and `cdc.dbo_orders_CT` is `is_ms_shipped = 1` | MEASURED |
| the per-instance TVFs are visible | `FunctionsSql` filters `is_ms_shipped = 0`, and a **per-capture-instance** `fn_cdc_get_*_changes_<inst>` is `is_ms_shipped = 0` while the generic templates are `1` | MEASURED — the filter draws exactly the right line, by luck rather than by design |
| the LSN survives the boundary | `binary(10)` → Arrow binary → DuckDB **BLOB** | MEASURED |
| **BLOB ordering matches LSN ordering** | DuckDB compares BLOBs **unsigned bytewise**: `'\x7F' < '\x80'` is true, and the two real probe LSNs compare correctly; `min()`/`max()` work on BLOB | MEASURED — see §2.4, this is load-bearing |
| setup already works | `SELECT fabricator_exec('cp','EXEC sys.sp_cdc_enable_table …')` created a capture instance | MEASURED |

So this design is **not** about reaching SQL Server's CDC. It is about the distance between *reachable* and
*usable*:

| missing | why it is not a detail |
|---|---|
| **position algebra** | the resume tuple is `(start_lsn, seqval, operation)` with a strictly-after predicate the caller must write by hand, three times, correctly (§1.3) |
| **a cursor a consumer can store** | there is no offset store in a pull surface, so the cursor has to come back as DATA (§2.3) |
| **stale-cursor detection** | a purged cursor and a malformed call are the **same error number**, whose text names neither (§2.1) |
| **capture-instance choice** | two instances capture the **same change concurrently**, so reading both double-counts (§2.2) |
| **the initial snapshot** | and its handoff to the change stream, which is the whole "start from nothing" story (§5) |
| **the MAX-column trap** | an unchanged `nvarchar(max)` reads back as NULL in one of the two update images (§1.5) |
| **error translation, retention floors, agent health** | each currently surfaces as a raw provider error or not at all (§8) |

---

## 0.1 ⚠⚠ SCOPE: SQL SERVER ONLY. NOT Fabric Warehouse, NOT the Lakehouse SQL endpoint.

**User-stated, 2026-08-23, and it is a hard scope boundary rather than a limitation to work around.** CDC is
a SQL Server engine feature. The warehouse engines do not have it — no `cdc` schema, no
`sys.sp_cdc_enable_db`, no capture job, and on Fabric no `msdb` for the job metadata to live in.

| engine | `ServerProfile` | CDC |
|---|---|---|
| SQL Server (box), Azure SQL MI | `EngineEdition` 3 / 8, `IsWarehouse == false` | **yes**, with SQL Server Agent |
| Azure SQL Database | `EditionAzureSqlDatabase == 5` | **yes**, but there is **no SQL Server Agent** — the platform schedules capture, and `sp_cdc_help_jobs` reports `pollinginterval = 0`, which our existing code already treats as that environment |
| **Fabric Warehouse** | `EditionFabricOrSynapseServerless == 11` | **NO** |
| **Fabric Lakehouse SQL endpoint** | `EditionFabricOrSynapseServerless == 11` | **NO** — and it is read-only anyway |
| Synapse dedicated pool | `EditionSynapseDedicated == 6` | **NO** |

⇒ **the gate already exists: `ServerProfile.IsWarehouse`**, which is exactly
`EngineEdition is EditionSynapseDedicated or EditionFabricOrSynapseServerless`. Edition 11 covers **both**
Fabric Warehouse and the Lakehouse SQL endpoint (`ServerProfile.cs`'s own comment says so), so one flag
covers all three exclusions. Add a `SupportsCdc` derived flag beside `SupportsMars` / `HasNativeJson` rather
than testing editions at each call site.

⚠⚠ **AND IT MUST BE A CAPABILITY GATE, NEVER A TRY/CATCH PROBE — this is the one rule in this file that is
already paid for in blood.** `warehouse-support.md` §6.5: *"on a warehouse engine, never issue a statement
whose failure you intend to swallow."* A statement that ERRORS inside an explicit transaction on Fabric
**ABORTS the whole transaction**, and the next unrelated statement then fails confusingly. That is precisely
how every dbt table model on Fabric came to die at the swap with `15225` — two best-effort probes
(`sys.external_file_formats`, the stats DMVs) were being issued and their failures swallowed. A
"let's see if CDC is here" probe would reproduce that defect exactly.

⇒ **refuse at ATTACH-adjacent time from the profile, and never issue a CDC statement on a warehouse
profile.** §3.5's `cdc.health()` must answer "not supported on this engine" from `IsWarehouse` alone,
without touching `cdc.*`, `msdb`, or `sys.dm_server_services`.

⚠ **NOT MEASURED here** — it is user-stated and consistent with the engine's nature; this session had no live
warehouse to probe, and probing one is precisely the transaction-poisoning act described above. The cheap
confirmation for whoever has one: `SELECT SERVERPROPERTY('EngineEdition')` (expect 11) and
`SELECT OBJECT_ID('sys.sp_cdc_enable_db')` (expect NULL) — two reads, no DDL, outside a transaction.

⚠ The Azure SQL Database row is the interesting middle case and is **also unmeasured**: CDC works, the agent
does not exist, so `cdc.health()`'s agent answer must degrade to "not applicable" there rather than "not
running" (§8.4's unknown-is-not-absence rule), and §10's `sp_cdc_scan` affordance may or may not be
permitted. Worth settling before claiming support for it.

---

## 1. What SQL Server actually gives us

### 1.1 The objects `sp_cdc_enable_db` + `sp_cdc_enable_table` create

MEASURED, in creation order, in the user database:

- **`cdc` schema** with six metadata tables — `change_tables`, `captured_columns`, `index_columns`,
  `lsn_time_mapping`, `ddl_history`, plus one **`<capture_instance>_CT`** change table per capture instance.
- **two inline TVFs per capture instance**: `cdc.fn_cdc_get_all_changes_<inst>(@from_lsn binary,
  @to_lsn binary, @row_filter_option nvarchar)` and `cdc.fn_cdc_get_net_changes_<inst>(…)` — MEASURED
  signature.
- internal `sp_batchinsert_*` / `sp_ins_*` / `sp_upd_*` / `sp_insdel_*` procedures the capture job uses.
- ⚠⚠ **`sp_cdc_enable_db` and `sp_cdc_enable_table` both SUCCEED with SQL Server Agent stopped**, printing
  `SQLServerAgent is not currently running so it cannot be notified of this action.` four times. MEASURED.
  **So "capture is enabled" and "capture is happening" are independent states, and the enable path reports
  success for both** — which is a real production failure mode (a table that looks captured and never
  produces a row), and the reason `cdc.health()` exists (§3.5). With the agent RUNNING the same call also
  prints `Job 'cdc.<db>_capture' started successfully` — MEASURED, so no operator step is needed there.

### 1.2 The change table's shape — and its nullability lies

MEASURED for a source table `dbo.orders(id INT NOT NULL PK, customer NVARCHAR(50), amount DECIMAL(18,4),
notes NVARCHAR(MAX), updated DATETIME2(3))`:

| # | column | type | nullable |
|---|---|---|---|
| 1 | `__$start_lsn` | `binary(10)` | no |
| 2 | `__$end_lsn` | `binary(10)` | yes |
| 3 | `__$seqval` | `binary(10)` | no |
| 4 | `__$operation` | `int` | no |
| 5 | `__$update_mask` | `varbinary(128)` | yes |
| 6–10 | `id`, `customer`, `amount`, `notes`, `updated` | as the source | **all yes** |
| 11 | `__$command_id` | `int` | yes |

⚠⚠ **`id` is NULLABLE in the change table although it is `NOT NULL` in the source.** MEASURED. So a reader
that derives its output schema from the change table reports every column as optional. **Nullability must
come from the SOURCE table** (`sys.columns` on `source_object_id`), intersected with `cdc.captured_columns`
— which is also where the column ORDER for the update mask lives.

⚠ `__$command_id` is present in the change TABLE and, per SQL Server's documented TVF output, **not** in
the TVF. Not probed directly here — inferred from the fact that a direct-table read must order by
`(__$start_lsn, __$command_id, __$seqval, __$operation)` while a TVF read cannot. **UNVERIFIED**; it only
matters if we choose the direct-table read (§4).

### 1.3 Operation codes and the ordering key

MEASURED on a two-row insert, one update and one delete, read straight from the change table:

```
start_lsn                seqval                  op  mask  cmd  id  customer  amount    notes
0x0000002D00000580001D   0x0000002D00000580001B   2  0x1F   1    1  acme      10.5000   first
0x0000002D00000580001D   0x0000002D00000580001C   2  0x1F   2    2  globex    20.0000   NULL
0x0000002D000005880003   0x0000002D000005880002   3  0x04   1    1  acme      10.5000   NULL
0x0000002D000005880003   0x0000002D000005880002   4  0x04   1    1  acme      99.9999   first
0x0000002D000005900005   0x0000002D000005900002   1  0x1F   1    2  globex    20.0000   NULL
```

`1` = delete, `2` = insert, `3` = update **before** image, `4` = update **after** image. (`5` = merge —
§1.7.)

⚠⚠ **The two update rows share the SAME `seqval` and differ only in `__$operation`.** MEASURED, and it is
the reason the sort key and the resume tuple are **three** components, not two: sorting or resuming on
`(start_lsn, seqval)` alone can split an update pair or replay half of one.

Both inserts share one `start_lsn` (one transaction) with distinct `seqval` and distinct `command_id` (one
per statement).

**The resume predicate is therefore strictly-after on a 3-tuple**, and it is not expressible as a simple
comparison:

```sql
(   (start_lsn = @L AND seqval = @S AND operation > @O)
 OR (start_lsn = @L AND seqval > @S)
 OR (start_lsn > @L) )
AND start_lsn <= @UpperBound
```

Nobody should have to write that. It is the core of what the reader function exists to hide.

### 1.4 The update mask

MEASURED: `__$update_mask = 0x04` for an `UPDATE … SET amount = …`, and `0x1F` (all five captured columns)
for the insert and the delete. `cdc.captured_columns.column_ordinal` was `1..5` for `id, customer, amount,
notes, updated`, so **bit index = `column_ordinal − 1`**, little-endian within each byte:
`0x04 = 0b100` = bit 2 = ordinal 3 = `amount`. ✓

SQL Server also documents `sys.fn_cdc_is_bit_set(position, mask)` and
`sys.fn_cdc_get_column_ordinal(capture_instance, column_name)` for doing this server-side. ⚠ **UNVERIFIED**
here — and §4 leans on them, so §11 lists them first.

### 1.5 ⚠⚠ The MAX-column trap, and the measurement points the OPPOSITE way to the obvious guess

A `varchar(max)` / `nvarchar(max)` / `varbinary(max)` column that an `UPDATE` did not touch is **not stored**
in the change table for that update. Look at the probe rows again — the `UPDATE` touched only `amount`, and
`notes` was `'first'` both before and after:

| op | image | `notes` |
|---|---|---|
| 3 | before | **NULL** ← the value is gone |
| 4 | after | `first` ← the value is there |

**MEASURED: it is the BEFORE image that loses the value, and the AFTER image that keeps it.** A reader that
emits the before image must distinguish "this MAX column was genuinely set to NULL" from "SQL Server did not
record it" — impossible from the value alone, possible from the mask bit (§1.4), and the industry answer is
to substitute a distinguishable placeholder.

**Design consequence, and it is a good one:** emitting **after images only** (§1.7 `'all'`) makes this trap
disappear for the common case. It is the default this design picks.

### 1.6 Position algebra — the functions, all MEASURED

| call | measured behaviour |
|---|---|
| `sys.fn_cdc_get_max_lsn()` | the highest scanned LSN; **NULL before the capture job has ever run** |
| `sys.fn_cdc_get_min_lsn('<capture_instance>')` | the retention floor — takes the **capture instance** name, not the table's |
| `sys.fn_cdc_increment_lsn(@lsn)` | `0x…05900005` → `0x…05900006`: the next representable LSN. Needed because the TVF's lower bound is **inclusive** |
| `sys.fn_cdc_map_lsn_to_time(@lsn)` | `2026-08-23 17:12:07.927` — a `datetime`, so **≈3.33 ms resolution, not microseconds** |
| `sys.fn_cdc_map_time_to_lsn('largest less than or equal', SYSDATETIME())` | returned exactly the max LSN ⇒ **timestamp bounds are available server-side**, so the reader can offer `starting_timestamp` for free |
| `cdc.lsn_time_mapping` | `(start_lsn binary, tran_begin_time datetime, tran_end_time datetime, tran_id varbinary, tran_begin_lsn binary)` — one row per captured transaction, and where a commit timestamp comes from |

⚠ **The commit timestamp is a `datetime`.** Two transactions inside the same 3.33 ms tick carry the same
`_commit_timestamp`. It is metadata, never an ordering key — the LSN is the ordering key.

### 1.7 Two read shapes: all changes, or net changes

MEASURED over the same five change rows (insert 1, insert 2, update 1, delete 2):

| call | rows | result |
|---|---|---|
| `fn_cdc_get_all_changes_…(f, t, 'all update old')` | 5 | every image, including op 3 and op 4 |
| `fn_cdc_get_all_changes_…(f, t, 'all')` | 4 | the update collapses to **one** row, op **4** (after image) |
| `fn_cdc_get_net_changes_…(f, t, 'all')` | **1** | `op=2, id=1, amount=99.9999` — the final state; **id 2 vanished entirely** (inserted then deleted) |
| `fn_cdc_get_net_changes_…(f, t, 'all with merge')` | **1** | `op=5, id=1, amount=99.9999` — `5` = "insert or update" |

⚠⚠ **DECISION (user, 2026-08-23): we ship the REPLAY only. `net` is NOT a mode of our reader.** *"Net
changes is a business layer thing — better to get all changes and dedupe later manually."* That is right, and
§1.7d records why together with the measurement that settles it. The rest of §1.7 stays because the net TVF
remains **directly callable** (it is a discovered function — §0), so this documents what a caller who reaches
for it will get, and §1.7b is precisely why we do not put our name on it.

#### 1.7a ⚠⚠ The collapse is over the WHOLE WINDOW, not per transaction

This was asked directly and the loose phrasing above invites the wrong answer. **It is not a
same-transaction optimisation.** MEASURED: nine scenarios, every statement its own autocommit transaction
except the last — **15 distinct `start_lsn` values in the window, 19 raw change rows, 7 net rows.**

| # | scenario (each step a SEPARATE transaction) | raw `'all'` | net `'all'` | net `'all with merge'` |
|---|---|---|---|---|
| A | insert | `2 a` | **`2 a`** | `5 a` |
| B | insert, then update | `2 a`, `4 b` | **`2 b`** | `5 b` |
| C | **insert, then delete** | `2 a`, `1 a` | **absent** | absent |
| D | update a row that existed before the window | `4 new` | **`4 new`** | `5 new` |
| E | delete a row that existed before | `1 old` | **`1 old`** | `1 old` |
| F | delete a pre-existing row, then re-insert it | `1 old`, `2 reborn` | **`4 reborn`** | `5 reborn` |
| G | update a pre-existing row, then delete it | `4 doomed`, `1 doomed` | **`1 doomed`** | `1 doomed` |
| H | insert, delete, insert | `2 a`, `1 a`, `2 again` | **`2 again`** | `5 again` |
| I | insert + delete **in ONE explicit transaction** | `2`, `1` | **absent** | absent |

Rows **C** and **I** are the answer: an insert-then-delete cancels **whether or not the two were in the same
transaction**. C was two separate autocommit commits with a different `start_lsn` each; I was one
`BEGIN TRANSACTION … COMMIT`. Both vanish.

⇒ **the unit of collapse is the LSN WINDOW you asked for**, so *how often you poll* decides how much
collapsing happens. A row touched a hundred times between two reads costs **one** row. That is the real
economics of this mode, and it is the opposite of a replay, where cost scales with churn.

#### 1.7c ⚠⚠ BOTH MODES ARE ONE DECISION, AND IT IS MADE AT *ENABLE* TIME

**Decision (user, 2026-08-23): support both `all` and `net`.** That is a read-time option in §3.2 — but
whether it is *available* is fixed when capture is enabled, and getting it wrong is expensive to undo. All
MEASURED:

| | `@supports_net_changes = 0` (SQL Server's default) | `= 1` |
|---|---|---|
| TVFs created | `fn_cdc_get_all_changes_<inst>` **only** | **both** `all` and `net` |
| indexes on the change table | 1 clustered unique | 1 clustered unique **+ 1 nonclustered unique** |
| prerequisite | none | a PK, or an explicit `@index_name` unique index |

⇒ **`= 1` is purely ADDITIVE at read time.** A net-capable instance serves *both* modes — MEASURED, the same
instance answered `fn_cdc_get_all_changes_…` and `fn_cdc_get_net_changes_…` for the same row. There is no
mode you give up by asking for net support, only one extra index you pay for.

⚠ **Without it, `images := 'net'` fails on a MISSING OBJECT, not on a capability check.** The net TVF simply
does not exist, so the user gets `Invalid object name 'cdc.fn_cdc_get_net_changes_…'` — an error about an
object they never typed. `cdc.change_tables.supports_net_changes` is the flag to check at bind, and refuse
with something that names the fix.

⚠ **The prerequisite has a MEASURED error worth translating**, and it names its own escape:

```
Msg 22939: The parameter @supports_net_changes is set to 1, but the source table does not have a
           primary key defined and no alternate unique index has been specified.
```

⚠⚠ **THIS SECTION'S CONCLUSION REVERSED WHEN §1.7d LANDED, and the facts above did not change — worth
saying plainly, because it is the same facts reaching the opposite answer.** While `net` was going to be one
of our reader's modes, the argument was *"default `net := true`, since it is additive and the door is
one-way"*. Now that we ship the replay only, defaulting it true would provision an index and a TVF on the
**OLTP box** for a mode we deliberately do not offer — write amplification on the machine we are trying not
to load.

⇒ **`cdc.enable` defaults `net := false`, matching SQL Server's own default**, and exposes `net := true` as
an explicit opt-in for a caller who intends to use the net TVF **directly**. Its help text must name the
one-way door above, because opting in later is the expensive moment and enabling is the cheap one.

⚠ Diverging from a platform default to pre-provision a capability we chose not to support would need a
better reason than insurance. If someone later argues for flipping it, the facts they need are all in the
table above; the *decision* rests on §1.7d, not on them.

⚠⚠ **And it IS foreclosure, not a preference — MEASURED.** `supports_net_changes` cannot be altered on an
existing capture instance. The two ways out both cost something:

| route | cost |
|---|---|
| `sp_cdc_disable_table` + re-enable with `net = 1` | **the change table is dropped — captured history is GONE**, and every consumer's cursor is below the new floor (§2.1) |
| a **SECOND** capture instance with `net = 1` | MEASURED to work and to preserve the first instance's history — but it consumes the **two-instance budget** (§2.2) and creates a boundary the reader must handle (§7) |

So the cheap moment is the first one. This is the same shape as the `set_tblproperties` one-way door: a
default that looks harmless until the only way back is destructive.

#### 1.7b ⚠⚠ The net op code depends on pre-window existence — so prefer `'all with merge'`

Look at rows **A** and **F**. Both end with the row present, and they get **different** op codes: `2`
(insert) for a row that did not exist at the window's start, `4` (update) for one that did. SQL Server is
tracking existence as of the lower bound. Then compare **H** — insert/delete/insert inside the window — which
is `2`, because it also did not exist beforehand.

That is correct *and* it is a trap. A consumer that maps op 2 → `INSERT` and op 4 → `UPDATE` literally is
right **only while its target is exactly in sync with the window's start**. Miss one window, replay an old
cursor, or seed the target from a snapshot taken at a different instant, and an op-2 "insert" arrives for a
key the target already holds — a primary-key violation, at the sink, for data that was never wrong.

`'all with merge'` removes the distinction by design: MEASURED, **op 2 and op 4 both become op 5**
("insert or update") while op 1 (delete) is untouched. Two cases for the consumer — upsert, or delete — and
both are idempotent.

⇒ **this trap is a large part of why §1.7d declines to wrap net at all.** While it was going to be one of
our modes the conclusion here was *"recommend `net_merge`, keep plain `net` for a caller with a reason"* —
i.e. our surface would have shipped two options where one is safe under cursor loss and the other silently
is not. A caller reaching for the net TVF **directly** should still prefer `'all with merge'`, for exactly
the reason above; the difference is that the choice, and its consequence, is now theirs and visible at their
call site rather than hidden behind a mode name of ours.

⚠ A net **delete** carries the last known values, not NULLs — MEASURED: row E's delete reports `v = 'old'`,
and row G's reports `v = 'doomed'`, i.e. the value written by the update that preceded the delete. Useful for
logging what went, and it means a delete row's non-key columns are not a reliable "before image" of anything
in particular.

Its price, all of it real:
- **`@supports_net_changes = 1` at enable time**, which needs a primary key or unique index. MEASURED
  working on a PK table; ⚠ the refusal without one is UNVERIFIED.
- ⚠ **MEASURED WARNING: `Update mask evaluation will be disabled in net_changes_function because the CLR
  configuration option is disabled.`** So `'all with mask'` is silently degraded unless CLR is enabled on
  the instance. `'all'` and `'all with merge'` are unaffected. ⇒ **do not expose `'all with mask'`** without
  probing `sys.configurations` for `clr enabled` and saying so.
- It is **not resumable within a window**: net changes has no per-row position, so a partial read cannot be
  resumed — a net window is all-or-nothing.

#### 1.7d Why the replay wins, and the measurement that makes it free

**The collapse is a transformation, not a read.** The tell is in §1.7a: its answer depends on *when you
asked*. The same source history yields a different row set at a different polling cadence, which is a
property no one wants in a pipeline's extract step.

Five costs, each already measured elsewhere in this document, all of which disappear:

| keeping `net` as a mode | cost |
|---|---|
| it is **lossy and irreversible** | intermediate states are gone. No SCD2, no audit, no "how often did this change" — ever, for that window |
| the answer depends on the **schedule** | §1.7a. Poll differently, get different rows |
| **not resumable** | no per-row position, so a net window is all-or-nothing (§1.7) |
| the op code depends on **pre-window existence** | §1.7b — a missed window turns an "insert" into a PK violation at the sink |
| it needs `@supports_net_changes = 1` | which needs a PK, adds an index, and is a **one-way door** (§1.7c) |

And the benefit is recoverable in one line. **MEASURED in DuckDB against the exact 16-row `'all'` result of
§1.7a:**

```sql
SELECT id, CASE WHEN op = 1 THEN 'delete' ELSE 'upsert' END AS action, v
FROM changes
QUALIFY row_number() OVER (PARTITION BY id ORDER BY _position DESC) = 1;
```

reproduces SQL Server's `'all with merge'` result **exactly** — same seven keys, same actions, same values —
**plus exactly two rows: deletes for keys that were inserted and deleted inside the window** (ids 3 and 20).
Those two are **no-ops at an idempotent sink**: `DELETE … WHERE id = 3` matches nothing.

⇒ **the local dedupe is outcome-identical for any idempotent sink**, and it differs only by doing slightly
more harmless work. Meanwhile it keeps the full history, keeps the per-row cursor, keeps the semantics
visible and editable at the call site, and needs no PK on the source.

⚠ **What we give up, stated honestly: transfer volume.** A key updated 1000 times between two reads crosses
the wire 1000 times instead of once. That is the one real argument for the server-side collapse, it is a
*volume* optimisation rather than a semantic one, and the remedy is available to the caller for free — poll
more often, or `WHERE` the read. If someone measures a table where this hurts, the net TVF is already
callable directly (§0) and needs nothing from us.

### 1.8 The capture job, and the latency that is not ours

Capture is **asynchronous**: an agent job scans the transaction log and writes the change tables. MEASURED
config in the rig (`sys.sp_cdc_help_jobs`):

| job | `pollinginterval` | `maxtrans` | `maxscans` | `continuous` | `retention` | `threshold` |
|---|---|---|---|---|---|---|
| capture | 5 | 10000 | 10 | 1 | — | — |
| cleanup | — | — | — | 0 | **4320** min = 3 days | 4999 |

⚠ **`maxtrans = 10000`, where the commonly-quoted default is 500.** MEASURED on SQL Server 2025 — so quote
the server, not the folklore.

⚠ **Read job config through `sys.sp_cdc_help_jobs`, never `msdb.dbo.cdc_jobs` directly.** MEASURED: that
table **does not exist** on a server where no database has CDC enabled — it is created with the first one
and gone again after the last is dropped, so a `cdc.health()` that queries it directly fails with
`Msg 208: Invalid object name 'msdb.dbo.cdc_jobs'` in exactly the situation a health check is for.

⚠⚠ **Committed ≠ capturable.** There is always a lag between a commit and its appearance in the change
table, and it is the *job's* lag, not ours. A reader must never treat "no rows" as "no changes"; the honest
upper bound is `fn_cdc_get_max_lsn()`, which is precisely "as far as the job has got".

**MEASURED, and it is the finding that makes this testable at all: `EXEC sys.sp_cdc_scan` forces a scan
synchronously — with the agent STOPPED *and* with the capture job LIVE.** Agent stopped: before it,
`fn_cdc_get_max_lsn()` was `NULL` and both the change table and `lsn_time_mapping` were empty; after it, 5
rows each and a real max LSN. Agent running: **45 of 45 attempts succeeded** across two cadences (30 back to
back, then 15 spread over ~15 s so they straddled several 5-second poll cycles, with 8 log-scan sessions
recorded in that window ⇒ the job really was scanning). ⇒ **manual and automatic capture COMPOSE.** See §10.

**And the automatic path's latency is one poll interval, MEASURED over five trials** (insert, then poll every
50 ms until the row appears, no manual scan): **3125 / 5034 / 4985 / 5037 / 5035 ms**. The 3.1 s was a lucky
partial cycle; the honest number is *`pollinginterval`, every time*.

### 1.9 Retention is a cliff, not a slope

MEASURED. Starting from six change rows, with `@low_water_mark` set to the current max LSN:

```
EXEC sys.sp_cdc_cleanup_change_table @capture_instance=N'dbo_orders', @low_water_mark=@max;
   min_lsn  0x0000002C00000CB00054  ->  0x0000002E000003A00022
   ct_rows  6                       ->  1
```

and then reading from a position below the new floor **fails** (§2.1). A stored cursor older than the
retention window is not slow, or approximate — it is **unusable**, and the data it wanted is gone.

---

## 2. The four facts that shape the whole design

### 2.1 ⚠⚠ A stale cursor and a malformed call are the SAME error, and its text names neither

MEASURED, three different ways, all identical:

```
-- from_lsn below the retention floor
-- to_lsn   above the max LSN
-- from_lsn NULL
Msg 313: An insufficient number of arguments were supplied for the procedure or function
         cdc.fn_cdc_get_all_changes_ ... .
```

…and it reaches a DuckDB user through us in exactly that shape (MEASURED through `duckdb.exe`):

```
IO Error: Fabricator: failed to read next batch from stream: An insufficient number of arguments
          were supplied for the procedure or function cdc.fn_cdc_get_all_changes_ ... .
```

**Three arguments were supplied.** The message is not merely unhelpful, it is *misleading* — it sends the
reader to look at their call site while the real cause is that their pipeline has been down longer than the
retention window and **has lost data**.

⇒ **The reader MUST pre-check its window** against `fn_cdc_get_min_lsn(<instance>)` and
`fn_cdc_get_max_lsn()` and raise its own error naming the actual cause, the floor, the requested position
and the remedy (re-snapshot). This is the single highest-value line of code in the whole feature, and
nothing else in this design is worth building without it.

⚠ **The direct change-table read does NOT validate at all.** MEASURED: the same absurd bounds
(`0x00…00` to `0xFF…FF`) returned all 5 rows from `cdc.dbo_orders_CT`. Permissive where the TVF is strict —
which is a §4 trade-off, not a bug: silently returning what survives is *worse* than 313 for a pipeline that
must not lose rows.

### 2.2 ⚠⚠ Two capture instances capture the SAME change, concurrently

A table may have at most **two** capture instances — MEASURED, the third is refused:

```
Msg 22962: Two capture instances already exist for source table 'dbo.orders'. A table can have at most
           two capture instances. …
```

The second is how a schema change is absorbed: `ALTER TABLE … ADD region`, then a second
`sp_cdc_enable_table` with a new `@capture_instance`. MEASURED, `cdc.captured_columns` after that:

```
dbo_orders      id customer amount notes updated
dbo_orders_v2   id customer amount notes updated region
```

and then **one** INSERT produced rows in **both**:

```
dbo_orders     5 -> 6 rows
dbo_orders_v2  0 -> 1 row
```

⇒ a reader that reads both instances **double-counts every change in the overlap window**. It must pick one
per LSN range and switch at the boundary.

⚠⚠ **And the boundary is not recorded anywhere.** MEASURED: `cdc.change_tables.end_lsn` is **NULL for both
instances**. The old instance's stop position must be *derived* as the new instance's `start_lsn`
(`0x0000002D00000B300066` in the probe). This is a derivation the design must own, not a column it can read.

### 2.3 A pull surface has no offset store, so the cursor must be DATA

A streaming daemon owns a durable offset and a loop. We have neither, and want neither. What we have is SQL,
so the position has to be a **value** the consumer can select, store in their own table, and pass back.

That single constraint decides most of the surface:
- the reader takes a `starting_position` and returns rows carrying their own `_position`;
- **the window's END must be obtainable independently of the rows**, because a window can legitimately
  return zero rows and the cursor must still advance — otherwise a quiet period leaves the consumer pinned
  at an old position, drifting toward the retention cliff of §1.9 for no reason. Hence a separate
  `cdc.position()` and the two-step idiom of §3.4;
- **exactly-once is the consumer's problem, and the design's job is to make it solvable**: a stable
  ordering key, an idempotent shape (net changes), and a cursor that round-trips.

### 2.4 The LSN survives into DuckDB as a first-class orderable value

MEASURED (§0): `binary(10)` → BLOB, DuckDB compares BLOBs **unsigned bytewise** including across the
`0x7F`/`0x80` boundary where a signed comparison would invert, and `min()`/`max()` work.

⇒ the 3-tuple can be encoded as **one 21-byte BLOB** — `start_lsn(10) ‖ seqval(10) ‖ operation(1)` — whose
**lexicographic order is exactly the change order**. So `max(_position)` in DuckDB is a correct resume
point, `ORDER BY _position` is a correct replay order, and the consumer's cursor column is one BLOB rather
than three columns plus the §1.3 predicate. This is the nicest consequence of a measurement in the whole
document, and it is what makes the surface small.

⚠ Read §3.4 before using `max(_position)`: it is correct only over an **unfiltered** read.

---

## 3. The surface

### 3.1 Namespace: `db.cdc.*`

The precedent is exact: the Delta provider already puts its function set in a per-catalog schema —
`cat.delta.changes(…)`, `cat.delta.snapshots(…)`, `cat.delta.tblproperties(…)` — and the SQL Server backend
already appends a synthetic `fabric` schema on a Fabric endpoint. So: `db.cdc.changes(…)`,
`db.cdc.enable(…)`, `db.cdc.position()`.

⚠⚠ **Unlike `delta` and `fabric`, `cdc` is a REAL schema that really holds tables.** MEASURED: it exists
after `sp_cdc_enable_db`, and our catalog discovers it and all seven of its tables. Three consequences, and
they resolve in our favour:

1. **No lookup collision.** `FROM db.cdc.changes(…)` is a `TableFunctionRef` → `TABLE_FUNCTION_ENTRY`
   lookup; `FROM db.cdc.dbo_orders_CT` is a `BaseTableRef` → `TABLE_ENTRY` lookup. Different `CatalogSet`s
   (unlike views and tables, which share one and therefore have to refuse collisions). A function and a
   table may share the name `cdc.x` without either shadowing the other.
2. **Function declarations land on the real schema for free.** `FabricatorCatalog::LoadCatalog` registers a
   declared function only onto a schema **already discovered** — so when CDC is enabled, our functions
   attach to SQL Server's own `cdc` schema. Nothing to arrange.
3. ⚠ **But when CDC is NOT enabled the schema does not exist, and the host silently drops functions whose
   schema it did not register.** That would make `db.cdc.enable_database()` — the one function you need
   precisely then — unreachable, with no error. ⇒ `SchemasMetadata()` must **append `cdc` when absent**,
   exactly as it appends `fabric` today, including the "a real schema already named that ⇒ do not duplicate"
   guard (a duplicate is an `ensure_schema` collision, not a merge).

⚠ And it must be appended **outside `schema_filter`**, like `fabric`: that filter scopes DATA discovery,
and silently deleting the whole CDC surface because someone narrowed which tables they wanted is exactly the
surprising coupling the `fabric` note warns about.

### 3.2 The reader — one function

```sql
FROM db.cdc.changes('dbo.orders'                      -- positional: the SOURCE table
      [, starting_position  := <BLOB>]                -- exclusive lower bound (a previous _position)
      [, ending_position    := <BLOB>]                -- inclusive upper bound; default = cdc.position()
      [, starting_timestamp := <TIMESTAMP>]           -- alternative to starting_position
      [, ending_timestamp   := <TIMESTAMP>]
      [, images  := 'after' | 'both']                 -- default 'after'; NO net mode, see §1.7d
      [, include := 'changes' | 'snapshot' | 'snapshot+changes']  -- default 'changes'
      [, capture_instance := '<name>']                -- default: resolved per window (§7)
      [, max_rows := <BIGINT>])
```

**Naming is not free here and the tree already paid for it.** `starting_*`/`ending_*` rather than `from`/`to`
because **`from` and `to` are reserved words and a named parameter that is one is a PARSER error** that
reads as a broken function — recorded twice already (the `offset :=` lesson, then `delta.changes`). Matching
`delta.changes`'s vocabulary is a bonus; avoiding the parser error is the requirement.

**Options map to measured mechanics, one for one:**

| option | maps to | why not something else |
|---|---|---|
| `images := 'after'` (default) | `'all'` | one row per change, and it **dodges the §1.5 MAX-column trap** |
| `images := 'both'` | `'all update old'` | the only way to get a before image; carries the trap, so the placeholder rule applies |
| — | ~~`'net'` / `'net_merge'`~~ | **NOT a mode of this reader** (§1.7d): the collapse is lossy, schedule-dependent, unresumable, and reproducible locally in one line with a measured-identical outcome |
| — | ~~`'all with mask'`~~ | moot — it is an option of the net function, which we do not wrap. (MEASURED to be silently degraded without CLR anyway) |
| `include := 'snapshot+changes'` | §5 | the "start from nothing" story |

### 3.3 Output columns

Metadata first, then the captured source columns in `captured_columns.column_ordinal` order:

| column | type | notes |
|---|---|---|
| `_change_type` | `VARCHAR` | `insert` / `update_preimage` / `update_postimage` / `delete` / `upsert` |
| `_position` | `BLOB(21)` | `start_lsn ‖ seqval ‖ operation` — the resume token (§2.4). NULL in `net` modes, which have no per-row position |
| `_commit_lsn` | `BLOB(10)` | the transaction's commit LSN — all rows of one transaction share it |
| `_seq_val` | `BLOB(10)` | ⚠ shared by an update pair (§1.3) |
| `_operation` | `INTEGER` | the raw code, for anyone who wants it |
| `_commit_timestamp` | `TIMESTAMP` | from `lsn_time_mapping`; ⚠ `datetime` precision (§1.6) |
| `_update_mask` | `BLOB` | only when `images := 'both'`, where it is the only way to read a NULL correctly |
| …source columns… | as the **source** table | ⚠ nullability from the source, never the change table (§1.2) |

⚠⚠ **`_change_type` deliberately reuses the Delta change-feed spellings** (`insert`, `update_preimage`,
`update_postimage`, `delete`). A consumer that already handles a Delta CDF handles this one **unchanged** —
which is the whole point in a tool that has both providers, and the reason for **emitting op 3 and op 4 as
two rows rather than pairing them into one wide before/after row**. It also happens to delete the pairing
logic and its "the before event was not followed by an after event" failure mode entirely.

`upsert` for net-changes op 5, because `insert` would be a lie about a row that already existed.

### 3.4 ⚠ The cursor idiom, and why `max(_position)` alone is a trap

The two-step, which is what the docs should show:

```sql
-- 1. take the window end FIRST, and store it whatever the read returns
CREATE OR REPLACE TEMP TABLE w AS SELECT db.cdc.position() AS pos;

-- 2. read a closed window
INSERT INTO staging
SELECT * FROM db.cdc.changes('dbo.orders',
         starting_position := (SELECT cur FROM my_cursors WHERE tbl='dbo.orders'),
         ending_position   := (SELECT pos FROM w));

-- 3. advance to the WINDOW END, not to what you saw
UPDATE my_cursors SET cur = (SELECT pos FROM w) WHERE tbl='dbo.orders';
```

⚠ **`max(_position)` is correct only over an unfiltered read, and only when the window was non-empty.** Two
distinct ways it goes wrong: a `WHERE` clause makes the maximum *seen* lower than the maximum *read*, so the
next window replays rows already consumed; and an empty window yields NULL, so the cursor never advances —
harmless for a moment, and a slow walk toward the §1.9 retention cliff. Advancing to the window end is
correct in both cases, which is why `cdc.position()` exists as its own function rather than being implied.

⚠ `ending_position` is resolved **at bind** when defaulted. For a one-shot query that is exactly right; for
a **view** or a prepared statement it re-resolves on every bind, so the window moves. Documented rather than
prevented — a moving window is usually what a view over a change feed *means* — but a durable pipeline
should pass the bound explicitly, as above.

⚠ Table-function arguments must be constant at bind, so the `(SELECT cur FROM …)` spellings above are
illustrative of the *pattern*, not necessarily of the syntax: a scalar subquery may not bind there. Settle
this when slice 3 is written — the fallback is a prepared statement or a macro that splices the literal, and
it changes the ergonomics of the whole idiom, so it is worth checking early.

### 3.5 Setup and inspection

| function | does | shape |
|---|---|---|
| `db.cdc.enable_database()` | `sys.sp_cdc_enable_db` | table fn, one report row |
| `db.cdc.enable('dbo.orders' [, capture_instance :=] [, columns :=] [, net :=] [, role :=] [, filegroup :=] [, index :=])` | `sys.sp_cdc_enable_table`. ⚠ **`net` defaults to FALSE**, matching SQL Server — an opt-in for callers who want the net TVF directly, and a **one-way door** (§1.7c) | table fn, one report row |
| `db.cdc.disable('dbo.orders' [, capture_instance :=])` | `sys.sp_cdc_disable_table` | table fn |
| `db.cdc.tables()` | `sp_cdc_help_change_data_capture` — MEASURED to return schema, table, capture_instance, start_lsn, end_lsn, supports_net_changes, role, index, create_date, **`captured_column_list`** and `index_column_list` | table fn |
| `db.cdc.position()` | `sys.fn_cdc_get_max_lsn()` — **NULL when the job has never run** | scalar → `BLOB` |
| `db.cdc.min_position('dbo.orders')` | `fn_cdc_get_min_lsn(<instance>)` — the retention floor | scalar → `BLOB` |
| `db.cdc.health()` | agent state, capture/cleanup job config, capture lag (`map_lsn_to_time(max_lsn)` vs now), per-table retention floor | table fn |
| `db.cdc.scan()` | `sys.sp_cdc_scan` | table fn — see the ⚠ below |

**Which fabricator kind, and why:**
- the setup functions are **`ICatalogTableFunction`** returning one report row, following
  `delta.set_tblproperties` — which does its work at **execution**, not bind. A table function is not
  constant-folded, so there is no volatility question; a scalar would need `IsVolatile => true` (the default)
  and would still be the wrong shape for something that wants to *report*.
- `position()` / `min_position()` are **`ICatalogScalarFunction`**, and **must stay VOLATILE** — a
  `CONSISTENT` zero-argument scalar folds to a literal at plan time, which for "the current log position"
  is a wrong answer that looks like a cached one.

⚠⚠ **Every setup function MUST invalidate the catalog cache, or its own effect is invisible.** MEASURED:

```sql
SELECT fabricator_exec('cp','EXEC sys.sp_cdc_enable_table … @source_name=N''later'' …');
SELECT count(*) FROM duckdb_functions() WHERE … function_name LIKE '%dbo_later%';   -- 0
SELECT fabricator_refresh_cache('cp');
SELECT count(*) FROM duckdb_functions() WHERE … function_name LIKE '%dbo_later%';   -- 2
```

Enabling capture **creates objects** (a change table and two TVFs), and the session cannot see them until
the cache is rebuilt. `cdc.enable`/`disable` must call the invalidation themselves — a user should not have
to know that enabling capture is a DDL.

⚠ **`db.cdc.scan()` is a deliberate judgement call, not an oversight.** Forcing a log scan is a
maintenance action a caller should not normally take, and shipping it invites someone to call it per query
— which is a CPU load decision that belongs to the DBA (§1.8). But it is also exactly what makes the gate
deterministic (§10) and what unblocks a container with no agent. Ship it, name the cost in its own error
text and in the README, and do not use it anywhere in the reader.

---

## 4. Two ways to implement the reader — and the fork is real

### Option A — SQL rewrite (`ICatalogSqlTableFunction`)

`GenerateSql` emits one `SELECT` over objects DuckDB can already see, and the call **disappears at bind**.

```sql
-- what db.cdc.changes('dbo.orders', starting_position := X) could become
SELECT CASE __$operation WHEN 1 THEN 'delete' WHEN 2 THEN 'insert'
                         WHEN 3 THEN 'update_preimage' ELSE 'update_postimage' END AS _change_type,
       __$start_lsn AS _commit_lsn, __$seqval AS _seq_val, __$operation AS _operation,
       ltm.tran_end_time AS _commit_timestamp,
       id, customer, amount, notes, updated
FROM   "db"."cdc"."dbo_orders_CT" ct
LEFT JOIN "db"."cdc"."lsn_time_mapping" ltm ON ltm.start_lsn = ct.__$start_lsn
WHERE  … the §1.3 predicate with literal bounds …
ORDER BY __$start_lsn, __$command_id, __$seqval, __$operation
```

**What it buys, and it is a lot:**
- **No data crosses the bridge.** Both scans are ordinary catalog scans, so they keep projection pushdown,
  filter pushdown, TopN and parallelism — MEASURED to work on `cdc.dbo_orders_CT` today.
- **Zero streaming code**, and the output schema falls out of binding.
- The **snapshot leg is a `UNION ALL`** over the base table (§5) — and that leg keeps *its* pushdown too.
- The **capture-instance switch is a `UNION ALL`** of two windows split at the boundary LSN (§7).
- The **bind-time retention pre-check** of §2.1 fits naturally: a catalog-bound generator receives a
  `SqlGenContext` with the live catalog, so it can look up the floor and refuse **before** any SQL runs.

**What it costs:**
- ⚠ **It depends on the change table being DISCOVERED.** MEASURED true by default, but an ATTACH
  `table_filter` or `schema_filter` can hide it, and then the function fails with a confusing
  catalog error about an object the user never named. A marshaled reader issues its own SQL and does not
  care. This needs a bind-time existence check with an error that names the filter.
- ⚠ **Bounds must be bind-time literals.** DuckDB cannot call `sys.fn_cdc_get_max_lsn()`, so the default
  upper bound is resolved during `GenerateSql` — which is a **side-effect-free but non-deterministic**
  generator, against the letter of the authoring contract (`GenerateSql` must be deterministic; binds
  repeat). §3.4 documents the consequence; it is not hidden.
- ⚠ It reads the change table **directly**, which MEASURED performs **no range validation** — so §2.1's
  pre-check is not belt-and-braces, it is the *only* validation on this path.
- The MAX-column placeholder needs `sys.fn_cdc_is_bit_set` in the emitted SQL — **UNVERIFIED** (§11), and
  only for `images := 'both'`.
- A **cross-table merge in commit order** is not expressible (§6).

**Variant A2** — emit `FROM fabricator_query('db', '<our T-SQL>')` instead. That regains full T-SQL control,
including a server-side `sys.fn_cdc_get_max_lsn()` upper bound (deterministic generator, window resolved at
execution) and the strict TVF instead of the unvalidated table. It loses DuckDB-level pushdown into the
scan — though our own T-SQL already carries the window predicate, so what is actually lost is projection
pruning. **A2 is the better default for the `net` modes**, where the TVF *is* the computation and there is
nothing to push down.

### Option B — marshaled (`ICatalogTableFunction`)

A C# binding issues its own T-SQL and streams Arrow batches, like every other provider reader.

Buys: complete control — error translation at read time, no dependency on discovery, the k-way merge of §6,
and any future follow/tail. Costs: every row crosses the bridge, no pushdown, and we write and own the
streaming, the ordering and the resumption.

### Recommendation

**Build A (with A2 for the net modes). Keep B in reserve for §6 only.** A is a fraction of the code, every
phase we need is expressible in generated SQL, and it inherits pushdown and parallelism that B would have to
give up. The three things A cannot do — cross-table ordering, following, and read-time error translation —
are respectively out of scope (§6), out of scope (§9), and better done at bind anyway (§2.1).

⚠ **A's viability rests on two UNVERIFIED facts** (`fn_cdc_is_bit_set`, and how a `LEFT JOIN` to
`lsn_time_mapping` costs across two catalog scans). Settle §11 items 1–2 before committing.

---

## 5. Snapshot, then changes — MEASURED, exactly-once, with a SHORT-LIVED lock

**This section was rewritten twice on user direction, and the final protocol is measured end to end.** It is
also *better* than the established practice it was checked against — see §5.4.

### 5.1 The protocol

```
A (ordinary connection)                        B (snapshot connection)
--------------------------------------------   ------------------------------------
1  BEGIN TRAN
   SELECT ... FROM t WITH (TABLOCKX)           <- writers frozen, unversioned readers blocked
2  EXEC sys.sp_cdc_scan                        <- capture catches up (see 5.3)
3  P0 = sys.fn_cdc_get_max_lsn()               <- the handoff position
4                                              SET TRANSACTION ISOLATION LEVEL SNAPSHOT
                                               BEGIN TRAN
                                               <pin statement against t>  <- view fixed HERE
5  COMMIT   <- lock RELEASED, writers resume
6                                              ...read the whole table at leisure...
                                               COMMIT
7  stream from P0, EXCLUSIVE
```

### 5.2 The lock is SHORT-LIVED, and that is the point

**The lock's only job is to freeze the instant, so it can be released as soon as B's snapshot is pinned** —
it does *not* have to span the data read. That matters operationally rather than theoretically: holding
TABLOCKX across a multi-GB scan blocks every writer for the duration, which is not shippable.

**MEASURED, with all three controls firing:**

| | result |
|---|---|
| positive control — is the X lock genuinely held at B's moment? | `sys.dm_tran_locks` -> `X / OBJECT / GRANT` to A's session |
| **B reads under SNAPSHOT isolation while TABLOCKX is held** | **SUCCEEDS** — snapshot readers use the version store and take no shared locks |
| negative control — B reads under READ COMMITTED | **`Msg 1222` lock request timeout** => blocked |
| negative control — a writer INSERTs | **`Msg 1222`** => blocked |

⚠ **An earlier run of this same probe reported all three as SUCCEEDED and was VOID, not positive** — 25 s of
wall clock passed between two tool calls, A's transaction had already committed, and B was reading an
unlocked table. The negative controls are what exposed it. **Run both sessions inside ONE invocation** and
assert the lock's existence rather than inferring it from blocking.

**And the pinned view SURVIVES the release — the claim the whole scheme rests on. MEASURED:**

```
B pins during A's lock window          -> 2 rows
A COMMITs (TABLOCKX released)
a writer INSERTs id=3 and UPDATEs id=1, commits
B re-reads 12+ s later, same txn       -> 2 rows          <- the snapshot held
stream from P0 exclusive               -> exactly 2 rows: insert id=3, update id=1
committed table state                  -> 3 rows
```

=> **snapshot leg = the state at P0; stream = everything after P0. No gap, no duplicate**, and the lock was
held only for the pin.

⚠ **The pin statement is REQUIRED, because SNAPSHOT isolation fixes its view at the first statement that
touches DATA, not at `BEGIN TRANSACTION`.** So B must issue a real read against the table *inside* A's lock
window; otherwise the snapshot is taken after the release and can miss writes. (Independent corroboration:
the established implementation's multi-connection snapshot pool executes a throwaway first query on every
pooled connection for exactly this reason.)

### 5.3 ⚠⚠ `fn_cdc_get_max_lsn()` is the CAPTURE WATERMARK, not the log head — hence step 2

This is the step that is easy to omit, and it is the difference between exactly-once and at-least-once.
Taking the lock freezes writers, but the capture job is **asynchronous** (§1.8): a transaction that committed
moments before the lock may not be captured yet, so `max_lsn` sits *below* it. Then:

- the snapshot (reading committed data) **includes** that row;
- its change row carries a `__$start_lsn` **above** P0, so the stream **re-delivers** it;
- => a duplicate, for every transaction inside the capture lag — up to one `pollinginterval` of commits.

`EXEC sys.sp_cdc_scan` under the lock closes the window, and **MEASURED: it succeeds while TABLOCKX is
held.** Note the failure direction without it is duplication, never loss — so an idempotent sink is
unaffected either way, and this step is what buys exactly-once for everyone else.

⚠ It inherits §10.2's race: the scan is refused with `Msg 22903` if the capture job holds the log-scan
session. Stop the job, or retry — but a retry can lose 20 times in a row (measured), so on a busy server the
honest fallback is to skip step 2 and document at-least-once.

### 5.4 What this improves on, and the one thing it costs

The established practice for this handoff takes table locks for the **schema** phase only and **releases them
before reading data** (its step 6 precedes its step 7), with a default isolation of `REPEATABLE_READ` whose
own documentation admits it *"does not fully guarantee consistency"* because phantom reads can occur. So a
row inserted mid-snapshot may appear in both the snapshot and the stream: **at-least-once**.

Pinning a snapshot *before* releasing the lock is what upgrades that to exactly-once while keeping the lock
short — and it needs no long-held lock, no RCSI, and no dependency on how `fn_cdc_get_max_lsn()` behaves
inside a snapshot transaction (which is why §11's item 3 is **dissolved** rather than answered: P0 is read on
A, in an ordinary transaction).

**What it costs, stated plainly:** `ALLOW_SNAPSHOT_ISOLATION ON` for the database (MEASURED as the only
prerequisite — RCSI is *not* required; the probe ran with `is_read_committed_snapshot_on = 0`), a tempdb
version store for the snapshot's lifetime, and a brief write freeze on the captured table.

### 5.5 ⚠ Isolation-level naming does NOT transfer from the reference, and ours is the easier side

| | how snapshot isolation is named |
|---|---|
| JDBC | **not in the standard.** The reference hardcodes `private static final int TRANSACTION_SNAPSHOT = 4096;` — the SQL Server driver's proprietary constant, redeclared locally. Standard JDBC has only READ_UNCOMMITTED / READ_COMMITTED / REPEATABLE_READ / SERIALIZABLE |
| **SqlClient (ours)** | **`IsolationLevel.Snapshot` is a first-class enum value**, and `SqlServerBackend.ParseIsolationLevel` already maps `"snapshot"` to it. `ServerProfile.DefaultWriteIsolation` already returns `"snapshot"` on Fabric/Synapse-serverless |

=> **the vocabulary we need already exists and is already wired.** Do not port the magic number, and do not
invent a second isolation vocabulary — reuse `mssql_read_isolation` / the ATTACH `isolation_level` option.

⚠ **One engine rule DOES transfer: you cannot switch INTO snapshot isolation mid-transaction.** SQL Server
permits changing isolation level during a transaction with exactly one exception, and that exception is
SNAPSHOT — so B must begin at snapshot isolation, i.e. `BeginTransaction(IsolationLevel.Snapshot)` in
SqlClient, never `SET TRANSACTION ISOLATION LEVEL SNAPSHOT` after a `BEGIN`.

⚠ Locking mode is a separate axis from isolation, and both vocabularies exist upstream
(`snapshot.isolation.mode` x `snapshot.locking.mode`, defaulting to `repeatable_read` x `exclusive`). We need
only the one combination above, so **do not surface the matrix** — surface the protocol.

## 6. Ordering, and the cross-table merge we are NOT building

Within one table, order is `(start_lsn, command_id, seqval, operation)` reading the change table directly,
or `(start_lsn, seqval, operation)` through the TVF. MEASURED necessary: the update pair shares `seqval`
(§1.3).

**Across tables**, a globally-ordered stream means opening a cursor per change table and repeatedly
emitting the smallest position — a k-way merge whose whole purpose is preserving cross-table transaction
order. It is genuinely useful for replication and it is **Option B only**.

**Not building it, and the reason is not effort.** A per-table reader composes in SQL — `UNION ALL` two
`cdc.changes(…)` calls and `ORDER BY _position` — because §2.4 gives the positions a total order that is
correct *across* tables, LSNs being database-wide. So the merge is expressible by the caller for the price
of a sort, and the sort is DuckDB's, which spills. A bespoke streaming merge would buy only the memory
profile, for a consumer nobody has yet.

⚠ One thing that composition does **not** give: transaction ATOMICITY across tables. A window boundary can
fall inside a transaction that touched two tables, so one table's half arrives in this window and the other's
in the next. Anyone needing atomic multi-table windows must align bounds on transaction boundaries —
`cdc.lsn_time_mapping` has the commit LSNs to do it with, and `cdc.position()` already returns one.

---

## 7. Schema evolution — the hard part

MEASURED facts to design against (§2.2): at most two capture instances; both capture concurrently; the
older one's stop position is **not recorded** and must be derived from the newer one's `start_lsn`; the two
have different column sets.

**The reader's rule, per window:**

1. list the instances for the source table from `cdc.change_tables`;
2. one instance ⇒ use it;
3. two ⇒ boundary `B` = the newer instance's `start_lsn`. Read the older for `(from, min(to, B))` and the
   newer for `(max(from, B), to)` — a `UNION ALL` in Option A. **Never both over one range.**

⚠⚠ **The output schema is pinned at BIND, and the two instances disagree.** A window spanning the boundary
has two column sets, and a table function has one output schema. Three options, and the choice must be
explicit:

| | behaviour | verdict |
|---|---|---|
| **refuse** | error naming both instances and the boundary position, telling the caller to read up to `B`, then from `B` | **RECOMMENDED default** — loud, and the remedy is one position the message can hand over |
| **project to the newer schema** | new columns read NULL for pre-boundary rows | tempting and **wrong by default**: NULL is indistinguishable from a genuine NULL, so it silently fabricates data |
| **stop at the boundary** | return rows up to `B` and no more | attractive — the caller's loop advances and re-binds against the new schema — but the caller cannot tell a short window from an exhausted one without comparing to `ending_position` |

⚠ **Refusing is only defensible because the error can name the exact fix.** A message that says "two
capture instances, boundary `0x…66`, read up to it and then from it" costs the caller one extra statement.
One that just says "schema changed" would make refusal the wrong answer.

⚠ Also measured and not to be forgotten: **we cannot create the new capture instance ourselves.** It needs
`db_owner`, it is an operator action, and the window between the `ALTER TABLE` and the new instance is a
period where changes are captured under the *old* column set — invisible to us and not ours to fix. Say so
in the README rather than papering over it.

---

## 8. Correctness rules the implementation must carry

1. **The lower bound is EXCLUSIVE for a resume, and the TVF's is INCLUSIVE.** So a resume must either use
   `fn_cdc_increment_lsn(pos)` or the §1.3 three-clause predicate. Getting this wrong duplicates exactly one
   change per window — the kind of bug a small test cannot see.
2. **Pre-check the window against the retention floor** and raise our own error (§2.1). Non-negotiable.
3. **`fn_cdc_get_max_lsn()` NULL is a STATE, not an error.** MEASURED: it is NULL before the job has ever
   run, which is the *default* state of a freshly enabled table. `cdc.position()` returns NULL, the reader
   returns zero rows, and `cdc.health()` explains why. It must never look like a failure.
4. **Distinguish "the agent is not running" from "nothing has changed".** Both give NULL. The check is
   `sys.dm_server_services` — MEASURED, and it needs `VIEW SERVER STATE`, which a least-privilege reader may
   not have. So the probe must degrade to "unknown" rather than fail, and `cdc.health()` must say *unknown*
   rather than *not running*.
5. **Never emit an unpaired before image.** With `images := 'both'`, op 3 without a following op 4 means the
   window boundary split the pair. Prevented by making the window an LSN range (a transaction's rows share
   `start_lsn`), but `max_rows` can still split one ⇒ `max_rows` must round down to a transaction boundary,
   or be documented as "may split an update pair".
6. **Read-only replica**: capture cannot run on a replica, and a read there must use `SNAPSHOT` isolation
   and commit its read transaction each pass or it never sees new capture metadata. Out of scope for the
   first slice, but do not design it out.
7. **The reader must not hold a long transaction.** A long-running read transaction blocks
   `sp_cdc_disable_table` — so a reader that keeps one open makes the *operator's* schema-change procedure
   fail. Option A gets this for free (ordinary scans); Option B would have to rollback per pass.

---

## 9. What this deliberately does not build

- **No daemon, no queue, no offset store.** The loop is the caller's — dbt, a scheduler, a `/loop`. That is
  the user's requirement and it is also what keeps the surface this small.
- **No follow/tail.** A DuckDB query terminates. A `poll_timeout` that blocks inside a scan would hold a
  connection and a transaction for an unbounded time, against §8.7 — and the caller's loop already does the
  job with `cdc.position()`.
- **No transaction-boundary or DDL event streams.** `cdc.lsn_time_mapping` and `cdc.ddl_history` are
  discovered tables already; anyone who needs them can query them.
- **No net-changes mode** (§1.7d, user decision) — the collapse is a business-layer transformation: lossy,
  irreversible, dependent on polling cadence, unresumable, and MEASURED to be reproducible locally in one
  line with an outcome identical for any idempotent sink. The net TVF stays directly callable for anyone who
  wants it; we simply do not wrap it. **And declining it deletes four other problems**: the PK prerequisite,
  the extra index, the §1.7c one-way door, and §1.7b's pre-window-existence footgun.
- **No `'all with mask'`** — moot once net is out, and MEASURED to be silently degraded without CLR anyway.
  The `images := 'both'` replay carries `__$update_mask` regardless.
- **No automatic capture-instance creation** on schema change (§7): elevated privileges, an operator
  decision.
- **No cross-table streaming merge** (§6): composable in SQL for the price of a sort.

---

## 10. Gates — and the rig prerequisite, now DONE

**⚠⚠ THE FIRST VERSION OF THIS SECTION WAS WRONG, and the user caught it.** It presented the agent and
`sp_cdc_scan` as *"two ways out"* and recommended the scan on the grounds that a running agent would make
the suite flaky. They are not alternatives — **they compose**, and the rig should have both. What follows
replaces it.

### 10.1 The agent is now ENABLED in the rig (a real change, MEASURED)

`docker/docker-compose.yml` previously set `ACCEPT_EULA` and `MSSQL_SA_PASSWORD` and nothing else, so the
agent was **stopped** and the capture job could never run. It now sets **`MSSQL_AGENT_ENABLED=true`**.

MEASURED after recreating the container: `sys.dm_server_services` reports
`SQL Server Agent (MSSQLSERVER) | Running`, all seven databases survived (the `mssql-data` volume is
persistent), and `sp_cdc_enable_table` now auto-starts both jobs
(`Job 'cdc.<db>_capture' started successfully`).

**It is behaviour-neutral for the existing tier.** The capture and cleanup jobs exist only for CDC-enabled
databases, and no test database has CDC enabled. Spot-checked after the change — `verify_server_profile`
**15**, `verify_exec_invalidate_cache` **21**, `verify_session_tag` **25**, `verify_columnstore` **20**,
`verify_time_travel` **14**, each at its expected count. ⚠ That is a REPRESENTATIVE SAMPLE, not the tier: the
full service tier has not been re-run since the change and should be, once, before this is relied on.

**Why enable it at all, when the suite will not wait for it:**
- Without it the rig cannot represent the shipped product. A user's server has an agent; ours did not, so
  every "capture works" claim would have been about a configuration nobody runs.
- It is what makes `cdc.health()` meaningful (§3.5). With the agent permanently stopped, the *only* answer
  that surface could ever be asserted against is "not running".
- The enable path's own behaviour differs (jobs auto-start, §1.1) — and that is the path a user takes first.

### 10.2 ⚠⚠ `sp_cdc_scan` RACES the capture job — so the suite must STOP the job

**MEASURED, and it corrects the paragraph that stood here.** The first version of §10.2 said the two
"compose" on the strength of 45/45 successful manual scans against a live capture job. Then a fresh database
refused the very first one:

```
Msg 22903: Another connection with session ID 72 is already running 'sp_replcmds' for
           Change Data Capture in the current database.
```

There is **one log-scan session per database**, and `sp_cdc_scan` and the capture job contend for it.
Tally across the probes, all against a job proven live (rows appeared without a manual scan):
**1 failure in ~57 attempts.** Not a conflict, not a clean compose — a **race**, and a hard error that
aborts a suite rather than retrying.

⚠ **A 1-in-57 hard failure is the worst possible test property**: green often enough to look correct, red
often enough to erode trust in every other suite, and the failure text names `sp_replcmds` rather than
anything a reader would connect to CDC. My 45/45 was luck, in a database where the manual scan had won the
session first.

⚠⚠ **AND RETRYING DOES NOT FIX IT — MEASURED, which is what makes stopping the job mandatory rather than
tidy.** A later probe wrapped the scan in a 20-attempt loop with 500 ms backoff and **lost all 20**, with
`msdb.dbo.sysjobactivity` confirming the capture job was `RUNNING` throughout. So the session is not held
*briefly* and contended *occasionally*: an actively-scanning job can hold it right through a ten-second
retry budget. The rate is not the property to design against — the mechanism is.

**The fix is to remove the contention, not to retry it.** MEASURED:

```
EXEC sys.sp_cdc_stop_job @job_type = N'capture';   -- 'Job cdc.<db>_capture stopped successfully'
-- then 10 of 10 DML + sp_cdc_scan cycles succeeded
```

⚠ **And the stop itself must tolerate a refusal**: MEASURED, immediately after `sp_cdc_enable_table` the job
has not started yet and the stop fails with
`SQLServerAgent Error: Request to stop job … refused because the job is not currently running.` Wrap it, do
not assert it.

⚠⚠ **This is a third, stronger argument for §10.1.** `sp_cdc_stop_job` / `sp_cdc_start_job` are *agent* jobs
— with the agent disabled there is nothing to stop **and nothing to start**, so the suite could neither
guarantee determinism nor exercise the realistic path. **Enabling the agent is what buys the control.**

So the shape is: **agent enabled in the rig; the suite stops the capture job for its own database, drives
`sp_cdc_scan`, and starts the job again only for the one assertion in §10.3.**

The cost side is unchanged and still favours the manual scan: the automatic path takes **one full
`pollinginterval` — MEASURED 3.1–5.0 s, essentially always ~5 s** — so waiting at ten assertion points
would add ~50 s per run of *fixed* sleeping, sqllogictest having no retry-until primitive.

### 10.3 ⚠⚠ The vacuous-pass hole, and the one assertion that closes it

A suite that stops the capture job and always scans manually **passes identically on a rig whose agent is
stopped**. That leaves §10.1's compose change *untested*, and a revert of it invisible.

So exactly one section must exercise the real path: `sp_cdc_start_job @job_type='capture'`, insert, wait
~10 s server-side (a `WAITFOR DELAY` through `fabricator_exec` — the one place a sleep is the right
instrument), assert the row is present **with no manual scan**, then stop the job again.

⚠ It must run FIRST, before any `sp_cdc_scan`, or a scan from an earlier section makes it pass for the wrong
reason — and it must leave the job **stopped**, or every later section reopens the §10.2 race.

### 10.4 Sketch of `test/verify_mssql_cdc.test` <!-- check-docs:ignore (PROPOSED suite; naming it IS the point) -->

**Service tier** — it needs a real SQL Server.

| § | asserts | why it is not vacuous |
|---|---|---|
| 0 | `cdc.position()` is NULL on a freshly enabled table; the reader returns 0 rows and does not error | the **positive control** for §8.3 — without it every later "N rows" could pass on a broken NULL path |
| 1 | **the agent captures with NO manual scan** (§10.3): start the capture job, insert, server-side wait, assert, **stop the job again** | the only guard on `MSSQL_AGENT_ENABLED`. ⚠ Must leave the job STOPPED or every later section reopens the §10.2 race |
| 2 | enable_database / enable / `cdc.tables()` shows the instance and its captured columns | |
| 3 | insert/update/delete + `scan` ⇒ exact rows and `_change_type`s for `images := 'after'` and `'both'` | |
| 4 | `_position` round-trip: read window 1, store `cdc.position()`, more DML, read window 2 ⇒ **no duplicates, no gaps** | the whole feature. Needs the §8.1 boundary to be right |
| 5 | the **documented dedupe recipe** (§1.7d) over a replay of the §1.7a scenarios reduces to the expected one-row-per-key result | it is SQL we SHIP to users, so the README rule applies: an untested example is a defect shipped to the least-equipped audience. ⚠ Must include an insert-then-delete key, whose no-op delete is the one place the recipe differs from a server-side collapse |
| 5b | `images := 'net'` is **refused**, naming §1.7d | pins the decision. Without it, someone re-adds the mode and nothing objects |
| 6 | an unchanged `nvarchar(max)` reads NULL in the **before** image and its value in the **after** image; the mask distinguishes | pins §1.5 in the measured direction, so a later "fix" cannot silently invert it |
| 7 | a position below the retention floor (forced via `sp_cdc_cleanup_change_table`) gives **our** error, naming the floor — not 313 | pins §2.1. The mutant is removing the pre-check: it dies with "insufficient number of arguments" |
| 8 | two capture instances ⇒ the boundary is derived from the newer `start_lsn`, and a spanning window is **refused** naming it | pins §2.2/§7. ⚠ Needs the **overlap** measurement — assert that both change tables received the same insert, or the section proves nothing about double-counting |
| 9 | `include := 'snapshot+changes'`: snapshot rows carry `P0`; a writer commits AFTER the lock release; resuming from `P0` yields exactly that writer's changes and no duplicate of the snapshot | pins the §5.2 protocol. ⚠ Needs the writer to commit after the release, or it proves nothing about the pinned view surviving |
| 10 | `cdc.enable` makes its own new objects visible **without** a manual refresh | pins the MEASURED 0 to 2 staleness |

⚠ **§4 and §7 are the load-bearing sections.** The rest can pass on a reader that is merely plausible; those
two fail on the two mistakes that lose or duplicate data.

⚠ **Permissions are NOT covered by a suite run as `sa`**, and that is the same structural blindness as the
SqlClient Entra finding: the rig authenticates as `sa`, so a least-privilege reader (SELECT on the change
table via `@role_name`, no `VIEW SERVER STATE`) is exercised nowhere. Document the required grants; do not
imply they are tested.

⚠ **Each run must leave no CDC-enabled table behind**, or the capture job keeps scanning between runs and the
next run's `cdc.position()` is not NULL where §0 expects it. Disable in teardown, and do not rely on
`CREATE OR REPLACE` — enabling capture is a separate act from creating the table.

---

## 11. Settle these before building — in this order

1. **`sys.fn_cdc_is_bit_set` / `sys.fn_cdc_get_column_ordinal`** — do they exist and behave as documented?
   Option A's MAX-column placeholder is emitted SQL that calls them. *Cheap: one query.*
2. **What a `LEFT JOIN cdc.lsn_time_mapping` costs** as two catalog scans versus letting SQL Server do it
   inside `fabricator_query`. Decides A versus A2 for the default mode. *Cheap: `EXPLAIN ANALYZE` both.*
3. ~~**Does `sys.fn_cdc_get_max_lsn()` inside a `SNAPSHOT` transaction return the snapshot-consistent
   position?**~~ **DISSOLVED, not answered (user-directed, 2026-08-23).** §5's two-connection protocol reads
   P0 on an ordinary connection under TABLOCKX, so nothing depends on how that function behaves inside a
   snapshot transaction. The exactly-once form is MEASURED and needs no answer here. *The best kind of
   resolution: the question stopped being load-bearing.*
4. ~~**Does `@supports_net_changes = 1` refuse a table with no PK or unique index**~~ **ANSWERED
   (`Msg 22939`, naming `@index_name` as the escape) and NO LONGER GATING** — §1.7d took net out of our
   surface, so this only affects the opt-in `net := true` at enable time. §1.7c keeps the facts.
5. **Is `__$command_id` absent from the TVF output?** Only matters if the direct-table read is chosen —
   it is part of that path's ordering key.
6. **Can a table-function argument be a scalar subquery?** §3.4's whole idiom depends on how a stored cursor
   reaches the call. If not, the idiom needs a prepared statement or a macro, and that is an ergonomics
   decision worth making before the surface is documented.
7. **The Azure SQL Database middle case** (§0.1). The warehouse question is SETTLED — not supported, gate on
   `ServerProfile.IsWarehouse`, never probe. What remains is edition 5: CDC works, there is no agent, so
   which of `cdc.health()`'s answers are meaningful, and whether `sp_cdc_scan` is permitted. *Needs a live
   Azure SQL Database; do NOT probe a warehouse to find out (§0.1).*

---

## 12. Slices, if this is built

| slice | contents | why this order |
|---|---|---|
| **1** | the `ServerProfile.SupportsCdc` gate (§0.1) FIRST; then `cdc` schema appended when absent, `cdc.tables()` / `position()` / `min_position()` / `health()` | read-only, no reader yet, and it makes everything else observable. ⚠ The gate leads: without it every later slice can poison a transaction on a Fabric attach |
| **2** | `cdc.enable_database` / `enable` / `disable` / `scan` + cache invalidation | after this a table can be captured entirely from SQL — already a shippable increment |
| **3** | `cdc.changes` — single instance, `images := 'after'`, explicit bounds, **the §2.1 pre-check** | the reader, at its smallest correct size |
| **4** | `starting_timestamp` / `ending_timestamp`; `images := 'both'` + the mask placeholder | both are additive to the same generator |
| **5** | `include := 'snapshot'` / `'snapshot+changes'` — the §5.1 two-connection protocol | no longer blocked: §11 item 3 dissolved. Needs a second connection at `IsolationLevel.Snapshot` and `ALLOW_SNAPSHOT_ISOLATION ON`, which the ATTACH can check once |
| **6** | the two-instance boundary: derivation, `UNION ALL` split, refusal | the last correctness gap; needs 3 to exist first |

⚠ There is no net-changes slice: §1.7d removed it. What was slice 2's *"including the `net := true`
default"* is now just the `net` opt-in, and the `MERGE INTO` use case is served by the documented dedupe
recipe over slice 3.

Slices 1–3 are the whole story for a consumer who can run one statement per window, which is every dbt and
scheduler user. Everything after 3 is a shape, not a capability.
