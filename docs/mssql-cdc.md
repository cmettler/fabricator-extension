# Log-based change capture for SQL Server — slices 1 + 2 + 3 + 4 + 5 BUILT

> **Status: slices 1 (§13), 2 (§14), 3 — THE READER (§16) — 4 (§17) and 5 (§18) are BUILT and gated.**
>
> **⚠ READ §16 FIRST for what shipped**, and §15 for the design it was built from. §15 supersedes §4's
> recommendation and most of §7 and corrects three things this document previously asserted; §16 corrects
> two more — §3.4's cursor idiom does not run as printed, and §15.7's "the first call always returns zero
> rows" is false in an already-capturing database.
>
> **What remains** is the two-instance boundary (with the name-alignment and widening §18.1 deferred into
> it), the snapshot leg, `images := 'both'` and the timestamp bounds. ⚠ §18.1 records why slice 5 was
> re-scoped and slice 6 partly absorbed — read it before trusting §15.12's ordering.
>
> ⚠ The original header read *"DESIGN ONLY, 2026-08-23. No code, no ABI, no gate."* — true when written,
> and superseded by slice 2's ABI v81.
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
> contract but **not** run here are tagged ⚠ **UNVERIFIED**. ⚠ §11's items are now all ANSWERED; what
> remains open is §15.13. The 2026-08-24 measurements in §15 were run the same way, in a throwaway
> `cdcprobe` database dropped afterwards, with the rig verified back to zero CDC-enabled databases and zero
> capture jobs.

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

⚠⚠ **AND THE GATE MUST STAY ON THE PROFILE, NOT ON THE HOSTNAME — the three Fabric products differ.**
`IsFabricEndpoint` is a HOST test (it is how the `fabric.*` REST functions are enabled), and a future
"helpful" simplification to `!IsFabricEndpoint` would be WRONG: **Fabric SQL Database is a real SQL Server
engine and does support CDC**, while a Fabric Warehouse and a Lakehouse SQL endpoint do not. Reading the
edition gets all three right for free — a Fabric SQL Database attach receives the `fabric.*` functions AND
`db.cdc.*`; a warehouse or lakehouse endpoint receives the `fabric.*` functions and no CDC.

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
| `sys.fn_cdc_get_max_lsn()` | the highest scanned LSN; **NULL before the capture job has ever run** — but see the ⚠⚠ below: that is true only once CDC is ENABLED |
| `sys.fn_cdc_get_min_lsn('<capture_instance>')` | the retention floor — takes the **capture instance** name, not the table's. ⚠ Also transiently NULL; see below |
| `sys.fn_cdc_increment_lsn(@lsn)` | `0x…05900005` → `0x…05900006`: the next representable LSN. Needed because the TVF's lower bound is **inclusive** |
| `sys.fn_cdc_map_lsn_to_time(@lsn)` | `2026-08-23 17:12:07.927` — a `datetime`, so **≈3.33 ms resolution, not microseconds** |
| `sys.fn_cdc_map_time_to_lsn('largest less than or equal', SYSDATETIME())` | returned exactly the max LSN ⇒ **timestamp bounds are available server-side**, so the reader can offer `starting_timestamp` for free |
| `cdc.lsn_time_mapping` | `(start_lsn binary, tran_begin_time datetime, tran_end_time datetime, tran_id varbinary, tran_begin_lsn binary)` — one row per captured transaction, and where a commit timestamp comes from |

⚠ **The commit timestamp is a `datetime`.** Two transactions inside the same 3.33 ms tick carry the same
`_commit_timestamp`. It is metadata, never an ordering key — the LSN is the ordering key.

#### 1.6a ⚠⚠ Two NULL findings from building slice 1, and the first one is not NULL at all

**MEASURED 2026-08-23, while building slice 1. Both correct the row above, and both are the §2.1
misleading-error class in miniature.**

**(a) With CDC NOT enabled on the database, `sys.fn_cdc_get_max_lsn()` does not return NULL — it RAISES:**

```
Msg 208: Invalid object name 'cdc.lsn_time_mapping'.
```

…naming an object the caller never mentioned, for a question that has a perfectly good answer ("no position
exists yet"). Same for `sp_cdc_help_change_data_capture` and `sp_cdc_help_jobs`, which raise the *good*
message `22901 The database '…' is not enabled for Change Data Capture` — a clear error where "no captured
tables" is the honest answer. ⇒ **every one of these calls must be guarded on
`sys.databases.is_cdc_enabled`**, which is what slice 1 does; `cdc.max_position()` then answers NULL and
`cdc.tables()` zero rows.

⚠ And `OBJECT_ID('sys.sp_cdc_enable_db')` is **NON-NULL even with CDC disabled** (the procs ship in every
database), so it is *not* a usable "is CDC available" test — which also means §0.1's suggested confirmation
probe for a warehouse engine is only meaningful there, where the proc genuinely does not exist.

**(b) `sys.fn_cdc_get_min_lsn(<instance>)` is transiently NULL for a NEWLY ENABLED capture instance — and
the floor is briefly UNKNOWABLE rather than absent.** Reproduced with a discriminator: in a database whose
capture job is already running, enabling a new instance leaves `fn_cdc_get_min_lsn` answering NULL while
`cdc.change_tables.start_lsn` for that very instance is **already set** (`0x0000002E000009100034`); ~8 s
later both agree. So the function is not simply projecting `start_lsn`.

⚠ **My first story for this was refuted by its own control**: in a FRESH database the first
`sp_cdc_enable_table` returned a non-NULL floor immediately, while `max_lsn` was still NULL. So it is not
"NULL until the job runs" — it is specific to enabling an instance alongside a live capture session.

⚠⚠ **Consequence for §2.1's pre-check, which is the highest-value line in the feature: a NULL floor must NOT
be read as "no lower bound".** Reading it that way passes the window straight to the TVF and gets the
misleading 313. The honest answer is *"the retention floor is not established yet — retry"*. And do not
substitute `start_lsn`: that would ASSERT a floor the engine declined to state.

⚠ **It is sharper than "transient", and it broke a test: TWO CALLS IN ONE STATEMENT CAN STRADDLE THE
TRANSITION.** `min_position(x) IS NOT DISTINCT FROM min_position(x)` returned **false** — measured, 1 run in
14. So no assertion here may depend on the floor's VALUE (a `WAITFOR` only lengthens the window), and
`verify_mssql_cdc` §6 proves the resolution through the ERROR behaviour instead.

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

⚠⚠ **AND THE " ... " ABOVE IS NOT AN ELISION — IT IS THE OBJECT'S LITERAL NAME (MEASURED 2026-08-24,
§15.3).** Every CDC-enabled database carries four placeholder functions, two of them named
`fn_cdc_get_all_changes_...` and `fn_cdc_get_all_changes_ ... `, and `sys.fn_cdc_check_parameters` calls
them deliberately to *"Force error 313"*. So the message is IDENTICAL for every capture instance and
every cause — and it can never be parsed or attributed. That raises the pre-check above what this
paragraph claims for it: it is the ONLY channel by which a user can learn what went wrong.

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
per LSN range and switch at the boundary. ✅ BUILT as slice 7 — §19, where the double count is what mutant E
produces ("Expected 4 rows, but got 6").

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
  `cdc.max_position()` and the two-step idiom of §3.4;
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
`db.cdc.enable(…)`, `db.cdc.max_position()`.

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
      [, ending_position    := <BLOB>]                -- inclusive upper bound; default = cdc.max_position()
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

> **⚠⚠ THE BLOCK BELOW DOES NOT RUN AS PRINTED — SUPERSEDED BY §16.2 (2026-08-24).** A subquery is refused
> as an `EXECUTE` argument as well as as a table-function argument, so the middle step never bound; and
> there IS a pure-SQL idiom, which §16.2 gives. It is left here because the reasoning around it — take the
> end first, advance to the window end — is unchanged and is the part that matters.

The two-step, which is what the docs should show:

```sql
-- 1. take the window end FIRST, and store it whatever the read returns
CREATE OR REPLACE TEMP TABLE w AS SELECT db.cdc.max_position() AS pos;

-- 2. read a closed window. ⚠ The bounds must be LITERALS or PREPARED PARAMETERS — an inline scalar
--    subquery does NOT bind here (MEASURED, §11 item 6: `Binder Error: Table function cannot contain
--    subqueries`), even though DuckDB's own `range()` accepts one.
PREPARE win AS
  INSERT INTO staging
  SELECT * FROM db.cdc.changes('dbo.orders', starting_position := ?, ending_position := ?);
EXECUTE win(
  (SELECT cur FROM my_cursors WHERE tbl = 'dbo.orders'),   -- ⚠ REFUSED — see §16.2
  (SELECT pos FROM w));

-- 3. advance to the WINDOW END, not to what you saw
UPDATE my_cursors SET cur = (SELECT pos FROM w) WHERE tbl='dbo.orders';
```

⚠ **`max(_position)` is correct only over an unfiltered read, and only when the window was non-empty.** Two
distinct ways it goes wrong: a `WHERE` clause makes the maximum *seen* lower than the maximum *read*, so the
next window replays rows already consumed; and an empty window yields NULL, so the cursor never advances —
harmless for a moment, and a slow walk toward the §1.9 retention cliff. Advancing to the window end is
correct in both cases, which is why `cdc.max_position()` exists as its own function rather than being implied.

⚠ `ending_position` is resolved **at bind** when defaulted. For a one-shot query that is exactly right; for
a **view** or a prepared statement it re-resolves on every bind, so the window moves. Documented rather than
prevented — a moving window is usually what a view over a change feed *means* — but a durable pipeline
should pass the bound explicitly, as above.

⚠ **SETTLED 2026-08-23 (§11 item 6): an inline scalar subquery does NOT bind as an argument to one of our
table functions.** ⚠⚠ **AND THE OTHER HALF IS NOW MEASURED TOO (2026-08-24, §16.2): a subquery is NOT legal
as an `EXECUTE` argument either.** This paragraph shipped that as unmeasured with a guess attached; the
guess was the wrong way round. What DOES work is a scalar FUNCTION CALL as an `EXECUTE` argument, and —
better, because it reads the cursor out of a TABLE — `SET VARIABLE` plus `getvariable()`. So a resumable
pipeline IS expressible in pure SQL after all, which is what this section had concluded was unavailable.

### 3.5 Setup and inspection

| function | does | shape |
|---|---|---|
| `db.cdc.enable_database()` | `sys.sp_cdc_enable_db` | table fn, one report row |
| `db.cdc.enable('dbo.orders' [, capture_instance :=] [, columns :=] [, net :=] [, role :=] [, filegroup :=] [, index :=])` | `sys.sp_cdc_enable_table`. ⚠ **`net` defaults to FALSE**, matching SQL Server — an opt-in for callers who want the net TVF directly, and a **one-way door** (§1.7c) | table fn, one report row |
| `db.cdc.disable('dbo.orders' [, capture_instance :=])` | `sys.sp_cdc_disable_table` | table fn |
| `db.cdc.tables()` | `sp_cdc_help_change_data_capture` — MEASURED to return schema, table, capture_instance, start_lsn, end_lsn, supports_net_changes, role, index, create_date, **`captured_column_list`** and `index_column_list` | table fn |
| `db.cdc.max_position()` | `sys.fn_cdc_get_max_lsn()` — **NULL when the job has never run**, and NULL rather than 208 when CDC is not enabled (§1.6a) | scalar → `BLOB` |
| `db.cdc.min_position('dbo.orders')` | `fn_cdc_get_min_lsn(<instance>)` — the retention floor | scalar → `BLOB` |
| `db.cdc.health()` | agent state, capture/cleanup job config, and `max_lsn_age_seconds` (`map_lsn_to_time(max_lsn)` vs now). ⚠ NOT called `capture_lag_seconds`, which this table said first and would be a misleading name: it is the AGE of the newest CAPTURED transaction, so on an idle database it grows without bound while capture is perfectly current. It is an upper bound on lag, and a signal only beside known write traffic | table fn |
| `db.cdc.capture_now()` | `sys.sp_cdc_scan` | table fn — see the ⚠ below. ⚠ It was `cdc.scan()` for one day; §14.5 records why the name had to move |

**Which fabricator kind, and why:**
- the setup functions are **`ICatalogTableFunction`** returning one report row, following
  `delta.set_tblproperties` — which does its work at **execution**, not bind. A table function is not
  constant-folded, so there is no volatility question; a scalar would need `IsVolatile => true` (the default)
  and would still be the wrong shape for something that wants to *report*.
- `max_position()` / `min_position()` are **`ICatalogScalarFunction`**, and **must stay VOLATILE** — a
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

⚠ **`db.cdc.capture_now()` is a deliberate judgement call, not an oversight.** Forcing a log scan is a
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

> **⚠⚠ SUPERSEDED 2026-08-24 by §15.1 — the reader is OPTION B (marshaled C#), not A.** The paragraph
> below is kept because its reasoning is sound and because a THIRD option it never enumerated — the
> generated TVFs are DISCOVERED catalog functions — nearly won on exactly these grounds. What killed
> A is not pushdown but §5: the two-connection snapshot protocol is inexpressible in generated SQL.

**Build A (with A2 for the net modes). Keep B in reserve for §6 only.** A is a fraction of the code, every
phase we need is expressible in generated SQL, and it inherits pushdown and parallelism that B would have to
give up. The three things A cannot do — cross-table ordering, following, and read-time error translation —
are respectively out of scope (§6), out of scope (§9), and better done at bind anyway (§2.1).

⚠ **A's viability rested on two UNVERIFIED facts. One is now settled and it changes the emitted SQL above:
the `LEFT JOIN` must be CONDITIONAL, because DuckDB does not eliminate it when nothing selects
`_commit_timestamp` — MEASURED, and not even a `PRIMARY KEY` on the right side changes that (§11 item 2).**
So the join belongs behind something `GenerateSql` can see (a named parameter), and the default window read
is a single change-table scan. `fn_cdc_is_bit_set` (§11 item 1, needed only for `images := 'both'`) is still
unverified.

---

## 5. Snapshot, then changes — MEASURED, exactly-once, with a SHORT-LIVED lock

**This section was rewritten twice on user direction, and the final protocol is measured end to end.** It is
also *better* than the established practice it was checked against — see §5.4.

### 5.1 The protocol

```
A (ordinary connection)                        B (snapshot connection)
--------------------------------------------   ------------------------------------
1  BEGIN TRAN
   SELECT ... FROM t WITH (TABLOCK, HOLDLOCK)  <- writers frozen, READERS UNAFFECTED (see 5.2a)
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

### 5.2a ⚠ THE HINT IS `TABLOCK, HOLDLOCK` — NOT `TABLOCKX` (user-raised, MEASURED 2026-08-24)

The protocol above was written with `TABLOCKX` and it is stronger than the job requires. The user's point:
**`TABLOCK` is a SHARED lock and is STATEMENT-scoped, so it must be combined with `HOLDLOCK` to survive to
end of transaction; `TABLOCKX` is an EXCLUSIVE lock and is transaction-scoped by nature.** Both halves
MEASURED, by asking `sys.dm_tran_locks` from a LATER statement in the same transaction:

| hint | lock on the table, seen by the NEXT statement |
|---|---|
| `TABLOCK, HOLDLOCK` | **`OBJECT / S / GRANT`** — held |
| `TABLOCK` alone | **none** — released at end of statement |
| `TABLOCKX` | `OBJECT / X / GRANT` — held |

⇒ `TABLOCK` alone is unusable here: steps 2 and 3 are SEPARATE STATEMENTS, so the window would be
unprotected before `sp_cdc_scan` even ran.

**And the shared lock is SUFFICIENT, because the only thing the protocol must prevent is a WRITE committing
inside the window.** MEASURED in ONE batch, with the lock state asserted before and after so nothing rests
on wall clock between calls:

```
foreign lock on dbo.o: S      <- control, before
READER: 2 rows                <- a READ COMMITTED reader is NOT blocked   (TABLOCKX blocks it: 5.2)
sp_cdc_scan under S: OK       <- step 2 still works under the shared lock
WRITER: 1222                  <- a writer IS blocked
foreign lock still held: S    <- control, after: all four ran inside the window
```

A SNAPSHOT pin also succeeds under it, which follows a fortiori from §5.2 (it already succeeds under the
stronger X lock) and was re-measured anyway.

⇒ **use `WITH (TABLOCK, HOLDLOCK)`.** It freezes writers exactly as `TABLOCKX` does while leaving ordinary
readers alone, so the protocol's blast radius shrinks from "everyone touching this table" to "writers only",
for the seconds the pin takes. §5.2's measurements were taken with `TABLOCKX` and remain valid — what
changes is the hint we ship, not any claim about the handoff.

⚠ `HOLDLOCK` is the `SERIALIZABLE` hint under another name. If connection A were already SERIALIZABLE,
`TABLOCK` alone would hold to end of transaction — but say both, because the isolation level of a connection
we did not open is not a thing to depend on.

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
`cdc.lsn_time_mapping` has the commit LSNs to do it with, and `cdc.max_position()` already returns one.

---

## 7. Schema evolution — the hard part

> **⚠⚠ LARGELY SUPERSEDED 2026-08-24 by §15.6 / §15.9 / §15.11, and one premise of this section is
> WRONG.** The change table's schema is NOT fixed for the life of the instance — an `ALTER COLUMN
> <type>` IS propagated, asynchronously, by the capture job. A captured COLUMN also cannot be renamed
> at all (SQL Server refuses it), and the refuse/project/stop trilemma below is resolved differently:
> project to the union WITH a `_capture_instance` column, and prefer a resync. Read §15.6 first.

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
| **refuse** | error naming both instances and the boundary position, telling the caller to read up to `B`, then from `B` | ~~RECOMMENDED default~~ — **SHIPPED as slices 3-6 and REPLACED by slice 7 (§19)**: with `_capture_instance` on every row the middle option stops fabricating data, because a NULL becomes decidable |
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
   run, which is the *default* state of a freshly enabled table. `cdc.max_position()` returns NULL, the reader
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
  job with `cdc.max_position()`.
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

### 10.2a ⚠⚠ TWO CORRECTIONS TO §10.2, both from building slice 1

**(a) `sp_cdc_disable_db` CONTENDS FOR THE SAME LOG-SCAN SESSION, and §10.2 only knew about `sp_cdc_scan`.**
OBSERVED ONCE in `verify_mssql_cdc`:

```
22896: sp_cdc_disable_db caught an exception in try block when executing command
       'sys.sp_cdc_disable_db_internal'. The error returned was 22831: 'Could not update the metadata that
       indicates database TestDB is not enabled for Change Data Capture. The failure occurred when executing
       the command 'sys.sp_repldone NULL, NULL, 0, 0, 1, 0, 0'. The error returned was 22912:
       'sp_repldone failed'.'
```

`sp_repldone` is the same log-reader machinery as `sp_replcmds`. ⇒ **the rule generalises: the single
per-database log-scan session is contended by more than `sp_cdc_scan`, and a TEARDOWN can fail on it** —
which is worse than a failed scan, because it leaves CDC enabled and the capture job scanning between runs.

⚠ **Could NOT be forced deterministically**: 0 failures in 5 iterations that enabled, inserted, waited 7 s so
the job was actively scanning, then disabled. So the suite carries a 10-attempt retry with a 1 s backoff as
INSURANCE against a rare race, not as a fix for a reproducible one — and the retry needed 0 attempts in
every measured run.

**(b) §10.2's own remedy — "stop the capture job first" — CANNOT BE MADE TOLERANT, so it is the wrong tool
for a teardown.** §10.2 says the stop "is REFUSED if the job has not started yet — wrap it, do not assert
it." MEASURED: **T-SQL `BEGIN TRY … END CATCH` does NOT catch that refusal** (the SQL Server Agent proxy
raises it outside the batch's error handling), and it escapes through `fabricator_exec` as
`22022 SqlException`. So it cannot be wrapped, and the window where it fires — immediately after
`sp_cdc_enable_table`, before the job starts — is exactly where a suite wants it. The retry in (a) needs no
guard and is therefore the better shape for teardown.

⚠ This does **not** overturn §10.2 for its own case: stopping the job before `sp_cdc_scan` is still right,
because there the job is running by construction, so the refusal cannot fire. What changes is that "stop the
job" is not a general-purpose prelude.

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
| 0 | `cdc.max_position()` is NULL on a freshly enabled table; the reader returns 0 rows and does not error | the **positive control** for §8.3 — without it every later "N rows" could pass on a broken NULL path |
| 1 | **the agent captures with NO manual scan** (§10.3): start the capture job, insert, server-side wait, assert, **stop the job again** | the only guard on `MSSQL_AGENT_ENABLED`. ⚠ Must leave the job STOPPED or every later section reopens the §10.2 race |
| 2 | enable_database / enable / `cdc.tables()` shows the instance and its captured columns | |
| 3 | insert/update/delete + `scan` ⇒ exact rows and `_change_type`s for `images := 'after'` and `'both'` | |
| 4 | `_position` round-trip: read window 1, store `cdc.max_position()`, more DML, read window 2 ⇒ **no duplicates, no gaps** | the whole feature. Needs the §8.1 boundary to be right |
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
next run's `cdc.max_position()` is not NULL where §0 expects it. Disable in teardown, and do not rely on
`CREATE OR REPLACE` — enabling capture is a separate act from creating the table.

---

## 11. Settle these before building — in this order

> **⚠ Items 1–6 are ANSWERED. What is still open is in §15.13**, and the most useful number left is
> one this list never contained: how long the capture job takes to apply a `required_column_update`.

1. ~~**`sys.fn_cdc_is_bit_set` / `sys.fn_cdc_get_column_ordinal`**~~ **ANSWERED 2026-08-23 — both exist and
   behave as documented, so §4's Option A keeps its MAX-column placeholder.** MEASURED:
   `fn_cdc_get_column_ordinal('dbo_o','amount')` = 3, `'notes'` = 4, and **a column that does not exist
   returns NULL rather than raising** — so the emitted SQL must tolerate a NULL ordinal.
   `fn_cdc_is_bit_set(ordinal, __$update_mask)` discriminates correctly: for the UPDATE pair (mask `0x04`)
   `amount` reports 1 and `notes` reports 0.
   - **⚠ AND THE SAME PROBE CONFIRMS §1.5's TRAP IS DISTINGUISHABLE, which is the point of the placeholder:**
     op 3 (before) has `notes = NULL` while op 4 (after) has `notes = 'first'`, and the mask says `notes` was
     NOT changed in both. So "the writer did not record it" is readable from the mask even though the VALUE
     alone cannot tell it from a genuine NULL.
2. ~~**What a `LEFT JOIN cdc.lsn_time_mapping` costs** as two catalog scans versus letting SQL Server do it
   inside `fabricator_query`.~~ **LARGELY DISSOLVED, 2026-08-24 — the join is needed for ONE OUTPUT COLUMN and
   should not be emitted unless that column is asked for.** What remains to measure is only the cost of the
   opt-in path, which by definition nobody pays by default.

   **Where the join is needed at all, stated plainly: only `_commit_timestamp`.** The change table's eight
   columns (§1.2) carry no time — `__$start_lsn`, `__$seqval`, `__$operation`, `__$update_mask`,
   `__$command_id` and the captured source columns — so `cdc.lsn_time_mapping` is the only place a commit
   time exists (`tran_end_time`, keyed by `start_lsn`, one row per captured transaction). Every other output
   column of §6's contract comes from the change table itself, and the WINDOW never needs the join either:
   bounds are LSNs, and a timestamp bound is resolved server-side by `sys.fn_cdc_map_time_to_lsn` at bind
   (§1.6), not by joining.

   ⚠ **And it is metadata, not an ordering key** (§1.6: `datetime`, ~3.33 ms, so two transactions in one tick
   share a value). So a reader that omits it loses nothing structural — which is exactly what makes leaving it
   out of the default emission defensible rather than a gap.

   ⚠⚠ **MEASURED 2026-08-24: DuckDB will NOT eliminate the join for you, and a PRIMARY KEY does not help.**
   `EXPLAIN SELECT ct.v FROM ct LEFT JOIN ltm ON ltm.k = ct.k` keeps a `HASH_JOIN` with a full scan of the
   right side even though nothing selects from it — and it keeps it whether or not the right side declares
   `PRIMARY KEY (k)`. So "emit the join always and let projection pushdown prune it" **does not work**: the
   cost is paid by every caller, on both scans, whether or not they want a timestamp. (Our catalog advertises
   no uniqueness anyway — `GetStorageInfo` reports none, which is why `ON CONFLICT` is refused — so there was
   never a route to the elimination even in principle.)

   ⇒ **Design consequence for slice 3: `_commit_timestamp` must be requested by something `GenerateSql` can
   see** — a named parameter, not a projection — because the emitter runs at bind and the projection is
   applied to the subquery afterwards. Then the default window read is ONE catalog scan of one change table,
   and the two-scan question only arises for the caller who asked for commit times.

   ⚠ It does **not** re-open A versus A2: that fork was settled by two other measurements (a catalog scan of
   a change table PUSHES the LSN range down into the T-SQL, and `fabricator_query` used to execute its SQL
   twice — since fixed). The join was the last "unverified fact" behind A's viability, and this narrows it to
   an opt-in path rather than the default one.
3. ~~**Does `sys.fn_cdc_get_max_lsn()` inside a `SNAPSHOT` transaction return the snapshot-consistent
   position?**~~ **DISSOLVED, not answered (user-directed, 2026-08-23).** §5's two-connection protocol reads
   P0 on an ordinary connection under TABLOCKX, so nothing depends on how that function behaves inside a
   snapshot transaction. The exactly-once form is MEASURED and needs no answer here. *The best kind of
   resolution: the question stopped being load-bearing.*
4. ~~**Does `@supports_net_changes = 1` refuse a table with no PK or unique index**~~ **ANSWERED
   (`Msg 22939`, naming `@index_name` as the escape) and NO LONGER GATING** — §1.7d took net out of our
   surface, so this only affects the opt-in `net := true` at enable time. §1.7c keeps the facts.
5. ~~**Is `__$command_id` absent from the TVF output?**~~ **ANSWERED 2026-08-23 — YES, absent.** MEASURED via
   `sys.dm_exec_describe_first_result_set` over `fn_cdc_get_all_changes_dbo_o`: exactly **8** columns —
   `__$start_lsn`, `__$seqval`, `__$operation`, `__$update_mask`, then the source columns. No `__$end_lsn`
   and no `__$command_id`. The change TABLE has both (§1.2).
   - **⚠ The useful corollary: `__$command_id` is NOT needed for ordering, so §2.4's 21-byte position is
     COMPLETE.** Within one `__$start_lsn` the measured `__$seqval`s already order the statements the same
     way `command_id` does (`0x…1B`/cmd 1 before `0x…1C`/cmd 2), and the TVF — which SQL Server itself orders
     — has no `command_id` to order by. So a direct-table read MAY add it as a tie-break but does not need
     it, and the resume tuple stays `(start_lsn, seqval, operation)`.
6. ~~**Can a table-function argument be a scalar subquery?**~~ **ANSWERED 2026-08-23 — NO for our functions,
   but a PREPARED PARAMETER works, so §3.4's idiom is fine.** MEASURED, three argument spellings against both
   a built-in and one of ours:

   | argument | `range(...)` (built-in) | `db.dbo.cf_range(...)` (ours) |
   |---|---|---|
   | `3` | 3 rows | 3 rows |
   | `(SELECT 3)` | 3 rows | **`Binder Error: Table function cannot contain subqueries`** |
   | `(SELECT n FROM cur)` | 3 rows | same refusal |
   | `?` + `EXECUTE q(3)` | 3 rows | **3 rows** |

   ⇒ **document the cursor idiom with a prepared statement or a literal, not an inline scalar subquery.** That
   is what a scheduler, a dbt macro and every client driver use anyway, so the ergonomic cost is close to
   zero — and §3.4's illustrative `(SELECT cur FROM my_cursors …)` spelling must be corrected before it is
   published, because it does not bind.
   - **⚠ THE ASYMMETRY IS REAL BUT ITS CAUSE IS NOT ESTABLISHED — do not write one down.** Both paths appear
     to go through `TableFunctionBinder` (whose default `clause` is literally `"Table function"`, and whose
     `ExpressionBinder` base is what refuses subqueries), and `BindTableFunctionParameters` turns a
     `SubqueryExpression` child into a `LogicalType::TABLE` argument before reaching it — yet `range` folds
     the subquery to a constant and ours refuses. Reproducer above if it is ever worth filing; it changes
     nothing about slice 3.
   - ⚠ Method note: the first run of this A/B printed only the last output line, which was a box border — so
     "the built-in did not error" was read as "the built-in returned 3". It was re-run printing VALUES. A
     non-error is not a result.
7. **The Azure SQL Database middle case** (§0.1). The warehouse question is SETTLED — not supported, gate on
   `ServerProfile.IsWarehouse`, never probe. What remains is edition 5: CDC works, there is no agent, so
   which of `cdc.health()`'s answers are meaningful, and whether `sp_cdc_scan` is permitted. *Needs a live
   Azure SQL Database; do NOT probe a warehouse to find out (§0.1).*

---

## 12. Slices, if this is built

> **⚠ SUPERSEDED 2026-08-24 by §15.12**, which reorders 5–8 (the snapshot leg has to precede the
> schema-drift resync that now depends on it) and adds the hidden-instance-name and alignment slices.

| slice | contents | why this order |
|---|---|---|
| **1** | ✅ **BUILT 2026-08-23** — see §13 | read-only, no reader yet, and it makes everything else observable. ⚠ The gate leads: without it every later slice can poison a transaction on a Fabric attach |
| **2** | ✅ **BUILT 2026-08-24 (ABI v81)** — see §14 | after this a table can be captured entirely from SQL — already a shippable increment |
| **3** | `cdc.changes` — single instance, `images := 'after'`, explicit bounds, **the §2.1 pre-check** | the reader, at its smallest correct size |
| **4** | `starting_timestamp` / `ending_timestamp`; `images := 'both'` + the mask placeholder | both are additive to the same generator |
| **5** | `include := 'snapshot'` / `'snapshot+changes'` — the §5.1 two-connection protocol | no longer blocked: §11 item 3 dissolved. Needs a second connection at `IsolationLevel.Snapshot` and `ALLOW_SNAPSHOT_ISOLATION ON`, which the ATTACH can check once |
| **6** | the two-instance boundary: derivation, `UNION ALL` split, refusal | the last correctness gap; needs 3 to exist first |

⚠ There is no net-changes slice: §1.7d removed it. What was slice 2's *"including the `net := true`
default"* is now just the `net` opt-in, and the `MERGE INTO` use case is served by the documented dedupe
recipe over slice 3.

Slices 1–3 are the whole story for a consumer who can run one statement per window, which is every dbt and
scheduler user. Everything after 3 is a shape, not a capability.

---

## 13. Slice 1 — AS BUILT (2026-08-23)

**C#-only, no ABI change, no C++.** Gate `test/verify_mssql_cdc.test` (**73**, service tier) +
`verify_server_profile` 15 → **16**; three mutants, each killed at its own section; the suite ran **25/25**
consecutively before being accepted (its first version failed 1 in 14 — see §6's note).

**Service tier 52 → 53 runs and 2221 → 2295 assertions, i.e. EXACTLY +1 run and +74** (73 new + the one
`supports_cdc` row) ⇒ **no other suite moved**, which is the behaviour-preservation claim and it is exact
rather than approximate. That run also closes the open item the rig change shipped with: the
`MSSQL_AGENT_ENABLED=true` compose change had only ever been spot-checked on 5 suites, and the full tier is
now green with it.

| what | where |
|---|---|
| the capability gate | `ServerProfile.SupportsCdc => !IsWarehouse`, surfaced as the `supports_cdc` row of `fabricator_server_info()` |
| `cdc` appended when absent | `SqlServerCatalog.SchemasMetadata` |
| registration | `SqlServerCdcFunctions.Register`, called from `BuildFunctionSet` only when `SupportsCdc` |
| the four functions | `dotnet/Fabricator.SqlServer/SqlServerCdc.cs` |
| the T-SQL + Arrow conversion | `dotnet/Fabricator.SqlServer/SqlServerCdcCatalog.cs` (a `partial` of `SqlServerCatalog`) |

### 13.1 The one design decision that departs from this document

**§0.1 asks `cdc.health()` to answer *"not supported on this engine"* from `IsWarehouse` alone. It does not
exist there at all instead** — the functions are registered only when `SupportsCdc`, and the `cdc` schema is
appended only then.

Why the absence is the stronger form of the same requirement: it makes "never issue a CDC statement on a
warehouse engine" true **by construction** rather than by a guard someone could later delete, and it keeps a
phantom `cdc` schema out of `duckdb_schemas()` on every Fabric catalog — a durable, enumerable artifact that
BI tools would list for a feature that can never work there. The cost is that the error becomes DuckDB's own
*"does not exist"* rather than a sentence naming the engine. That cost is paid back through the one surface
that DOES exist on every engine: the `supports_cdc` row, which `verify_server_profile` now asserts.

### 13.1a ⚠⚠ `position()` SHIPPED AS `max_position()` — the design's name collides with a DuckDB built-in, and it is the ABSENT case that suffers

**MEASURED while verifying §13.1's own claim, which is how it was found.** §13.1 says the cost of making the
surface ABSENT on a warehouse is "DuckDB's own *does not exist*". For the table functions that is exactly what
happens (`Catalog Error: Table Function with name health does not exist!`). For the scalar it was **not**:

```
SELECT w.cdc.position();          -> Binder Error: Referenced table "w" not found!
SELECT w.cdc.min_position('x');   -> Catalog Error: Scalar Function with name min_position does not exist!
SELECT w.cdc.nosuchthing();       -> Catalog Error: Scalar Function with name nosuchthing does not exist!
SELECT w.dbo.position();          -> Binder Error: Referenced table "w" not found!     <- schema EXISTS
```

⇒ **the NAME is the cause, not the missing schema** — `position` is a DuckDB BUILT-IN scalar
(`duckdb_functions()` has it in `system`), and a qualified call to a nonexistent `<cat>.<schema>.position()`
reports a missing TABLE, pointing at the ATTACH alias instead of at the function. The last line is the
discriminator: the same bad error on a schema that exists.

**That case is not hypothetical — it is precisely what a Fabric Warehouse or Synapse user hits**, because the
whole surface is absent there by design. So the one population guaranteed to meet this error would have been
sent to check their catalog name, their credentials and their ATTACH before ever suspecting the engine.

⇒ shipped as **`max_position()`**, which also repairs an asymmetry the design note had: `position()` beside
`min_position()` was an odd pair, and `min_position` / `max_position` maps one-to-one onto
`fn_cdc_get_min_lsn` / `fn_cdc_get_max_lsn`. No alias is kept — nothing had shipped.

**VERIFIED AFTER THE RENAME on a simulated warehouse (`SupportsCdc` forced false), all four entry points:**

```
SELECT w.cdc.max_position();        -> Catalog Error: Scalar Function with name max_position does not exist!
SELECT w.cdc.min_position('dbo.t'); -> Catalog Error: Scalar Function with name min_position does not exist!
SELECT * FROM w.cdc.tables();       -> Catalog Error: Table Function with name tables does not exist!
SELECT * FROM w.cdc.health();       -> Catalog Error: Table Function with name health does not exist!
fabricator_server_info -> supports_cdc = false
```

So §13.1's claim — "the cost is DuckDB's own *does not exist*" — is now TRUE for the whole surface. Before
the rename it was true for three of four, and false for the one a user would reach for first.

⚠ **The general lesson, and it is the reason this is written up rather than quietly fixed: a name that
collides with a host built-in is a liability in the ABSENT case, which is the case naming reviews never look
at.** Qualified resolution works perfectly when the function EXISTS (73 assertions say so); the defect is
only visible where it does not — and the population for whom it does not exist is the whole warehouse family.

### 13.2 What the build established that reading could not

1. **§1.6a(a)** — `fn_cdc_get_max_lsn()` RAISES 208 with CDC disabled; it does not return NULL. Every CDC
   call is now guarded on `is_cdc_enabled`, and the suite's §3 carries that raw error as its POSITIVE
   CONTROL, so the three "NULL is a state" assertions are about our guard rather than about SQL Server being
   lenient.
2. **§1.6a(b)** — `fn_cdc_get_min_lsn` is transiently NULL for a newly enabled instance *while its
   `start_lsn` is already set*, and two calls in one statement can straddle the transition. This is the fact
   the reader's retention pre-check must not get wrong.
3. **§10.2a** — `sp_cdc_disable_db` contends for the log-scan session, and `sp_cdc_stop_job`'s refusal cannot
   be caught in T-SQL.
4. **A zero-argument catalog SCALAR works, and the mechanism was already there** —
   `fabricator_schema_entry.cpp`'s `BuildFabricatorScalarFunction` marshals a throwaway
   `__fabricator_rows` column because Apache.Arrow cannot import a zero-FIELD schema, so the row count still
   crosses. `cdc.max_position()` is the first zero-argument catalog scalar in the tree; it needed no new
   plumbing.
5. **SQL Server's own per-instance TVFs get `_each` siblings.** Once CDC is enabled, each capture instance
   contributes FOUR entries to the `cdc` schema
   (`fn_cdc_get_{all,net}_changes_<inst>` and `…_each`), because this provider declares a `<routine>_each`
   for every discovered TVF that takes parameters. Not a defect — a per-row `CROSS APPLY` over
   `fn_cdc_get_all_changes` is meaningful — but it means any assertion counting functions in that schema must
   be scoped to our four names, or it becomes a function of how many tables happen to be captured.

### 13.3 Choices worth knowing before extending it

- **`INSERT INTO @tablevar EXEC sys.sp_cdc_help_change_data_capture`**, with the MEASURED 15-column shape
  declared in `SqlServerCdcFunctions.HelpTableVar`. The proc rather than `cdc.change_tables` because it
  applies the capture instance's `@role_name` permission filtering, which is security logic not worth
  reimplementing; a table VARIABLE rather than `#temp` because it is batch-scoped and so cannot be left
  behind on a pooled connection. ⚠ A future engine adding a column to that proc breaks this loudly.
  - **⚠ IT SHIPPED A WRONG ANSWER, found 2026-08-24 — see §15.14.** The ALL-TABLES form of that proc
    LEAKS the previous row's `index_column_list` onto a row whose `index_name` is NULL; called for that
    one table it returns NULL correctly. So `cdc.tables()` reports an index column list for a capture
    instance that has no index. One-line fix, plus the assertion §3 does not currently carry.
- **Results are read as all-`varchar` and re-typed in C#** (`ReadMetadataRows`, this catalog's own metadata
  idiom). It costs a hex parse for the LSNs and buys an output schema that cannot drift from whatever the
  type mapper does with `binary(10)` / `bit` / `datetime` — and since the schema is resolved at BIND, one
  crossing before the rows, a drift would corrupt rather than fail.
- **`min_position` resolves a capture-instance name OR a `schema.table` name, both EXACTLY**, and REFUSES
  when the name matches both kinds or the table has two instances (§2.2) — naming what it matched. Two
  instances of one table can have different floors (MEASURED: `0x…2C00000C980040` vs `0x…2D00000E900043`), so
  picking one would be a wrong answer rather than a shortcut.
- **`health()` is (property, value)** like `fabricator_server_info()`, because the answers are of mixed grain
  (server, database, job) and mixed type. Its agent probe is a SEPARATE round trip that the main batch must
  not so much as mention: a batch referencing a nonexistent object fails at COMPILE, so on Azure SQL
  Database — no `sys.dm_server_services`, but CDC does work — one batch would take the whole surface down.
  It is skipped there by EDITION, and the permission is *asked about* with `HAS_PERMS_BY_NAME` rather than
  tried, so a reader without `VIEW SERVER STATE` gets `unknown` and never `Stopped`.
- **The suite's teardown ordering is load-bearing twice over**: it must run BEFORE the `ATTACH` (discovery
  happens at attach, so a catalog opened first reports a previous run's leftovers whatever the teardown then
  does — this is how §2 first passed while asserting nothing), and §9 must leave no CDC-enabled table behind
  or the capture job keeps scanning between runs and the next run's §3 fails for an unrelated reason.

### 13.4 ⚠⚠ SLICE 2's PREREQUISITE — ✅ RESOLVED 2026-08-24 by ABI v81, via the option recommended below.
The analysis is kept because it is why the entry has the shape it does; §14.1 records what it got wrong.

**§3.5 says every setup function MUST invalidate the cache, and MEASURED that it matters** (enabling capture
creates a change table and two TVFs that the session cannot see until the cache is rebuilt — the 0 → 2
measurement). What was not established is *how a managed function does that*, and the answer today is: it
cannot.

**The mechanism that exists serves `fabricator_exec` ALONE.** The ABI's `execute_dml` carries an out-param
`schema_may_change` (set in C# by `SqlDdl.MayChangeSchema`), and `FabricatorExecFunction`
(`src/fabricator_extension.cpp:443`) acts on it — gated on the `mssql_exec_invalidate_cache` setting AND on
the first argument having named an attached catalog. **The table-function path (`tablefn_bind` /
`tablefn_execute`) has no such out-channel**, so an `ICatalogTableFunction` that performs DDL has no way to
say so.

Three candidate answers, with what each costs:

| option | shape | cost |
|---|---|---|
| **(a) an ABI out-flag on the table-function execute path**, mirroring `execute_dml`'s | a provider-authored function reports "my execution changed the catalog"; the host refreshes | **an ABI bump (v81)**. Additive, small, and it GENERALISES — any provider function doing DDL gets it, not just CDC. The recommended one |
| (b) put the ATTACH ALIAS in the options JSON, then `Host.Query("SELECT fabricator_refresh_cache('<alias>')")` from inside the function | no ABI bump — the options JSON is free-form, the `"materialize":true` precedent | ⚠ **RE-ENTRANCY RISK, unmeasured.** `RefreshCache` takes `entry_lock_`, and this would take it while EXECUTING a table function bound in that same catalog. The tree already records a hard rule about `entry_lock_` re-entry (a view body that binds under it throws `resource deadlock would occur` on MSVC and HANGS on glibc). Would need measuring before it could be trusted |
| (c) report-and-tell-the-user | the report row names `fabricator_refresh_cache` | free, and §3.5 argues against it: *"a user should not have to know that enabling capture is a DDL"* |

⚠ The ATTACH alias is genuinely absent from the managed side today: `fabricator_storage.cpp` has it
(`const string &name`) but puts only user-supplied ATTACH options into `options_json`. So (b) needs that
one-line addition before it is even expressible — which is worth knowing because it is ALSO what any future
"a provider function needs to name its own catalog" feature would need.

**Recommendation: (a)** — with one refinement that the naive version gets WRONG:

⚠⚠ **`tablefn_execute` fills an out STREAM, and our binding's rows come from an ASYNC ITERATOR — whose body
does not begin until the host's first BATCH PULL, a different ABI crossing.** So an out-flag set inside the
iterator would be read by the host BEFORE the DDL had run. That is the same trap `CLAUDE.md` records as a
standing rule from the `fabricator_install_plugin` bug: *a global table function must read every ambient in
`Execute()`, never in the iterator* — here it applies to a WRITE rather than a read, in the same place.

⇒ **the setup functions must do their work in `Execute()` (the plain method) and yield an already-built
batch.** Then `tablefn_execute`'s out-flag is set before it returns and the host can act on it. That is also
better for a DDL function on its own merits: the side effect happens exactly once, at a defined point, on the
thread the host established the ambients on — instead of at whatever moment DuckDB happens to pull.

⚠ Do NOT put the flag on `tablefn_close` instead: that runs at scan teardown from a destructor-ish path
documented as best-effort-must-not-throw, which is the wrong place to trigger a catalog rebuild.

With that refinement (a) is the only option whose correctness is obvious, and the flag it adds is the same
flag the DML path already has — so it makes the two paths consistent rather than adding a special case.

---

## 14. Slice 2 — AS BUILT (2026-08-24), and the ABI entry it needed

**C++ + C#, ABI v81.** `db.cdc.enable_database()` / `enable(...)` / `disable(...)` / `scan()`, and the
mechanism that makes their DDL visible. Gate `verify_mssql_cdc` 73 → **105**, two mutants both killed at §11.

### 14.1 §13.4 is RESOLVED, and by the option it recommended

The prerequisite is built: **`tablefn_execute` gained a `schema_may_change` out-param** (full record:
[abi-history.md](abi-history.md) §v81). What that section could only recommend, this one can state:

- **The refinement it predicted was necessary and is now MUTATION-PROVEN.** Moving the DDL out of the eager
  part of `Execute()` into the iterator body kills the suite at exactly the same assertion as never setting
  the flag at all — because the host reads the flag when `tablefn_execute` returns, and an iterator has not
  begun then. ⚠ The failure is silent in the worst way: the enable SUCCEEDS, its report row is correct, and
  only the rebuild is lost.
- **The host does NOT act on the flag where it is set**, which §13.4 did not anticipate. Doing so would
  retire the entry the running statement is scanning (`RefreshCache` calls `ClearTables()` on every schema).
  The catalog records it and `FabricatorTransactionManager::StartTransaction` refreshes — provably outside
  any bind or scan, because DuckDB resolves the `CatalogTransaction` before `LookupSchema` takes
  `schema_lock_`.
- **⚠ Option (b) — the no-bump alternative — would have been WORSE than §13.4 judged it.** It was assessed
  as risky because `RefreshCache` re-enters `entry_lock_`; the deeper problem is that a refresh *at that
  moment* is wrong wherever it is invoked from, so the alternative was not merely riskier plumbing for the
  same outcome. The bump bought the ability to DEFER, which is the part that makes it correct.

### 14.2 ⚠⚠ The staleness was WORSE than §3.5 measured, and that is what justified the ABI change

§3.5 recorded a 0 → 2 function count across `fabricator_refresh_cache`. Re-measured while scoping this, in
one session on one catalog:

| surface, after `cdc.enable` with no refresh | before v81 |
|---|---|
| our own `cdc.tables()` | **works** — it queries the server live, so it never went stale |
| `duckdb_tables()` in the `cdc` schema | stale (the new change table missing) |
| the new change table **by name** | **`Catalog Error: Table with name dbo_two_CT does not exist!`** |
| the new per-instance TVF **by name** | **`Catalog Error: Table Function with name fn_cdc_get_all_changes_dbo_three does not exist!`** |

⇒ the objects were **UNREACHABLE**, not merely un-enumerated. The mechanism, read from the source rather than
guessed: without an ATTACH object filter, `FabricatorSchemaEntry`'s lookup gate treats a name missing from
the discovered list as genuinely absent and returns before any by-name fetch.

⚠ **That refines a note this project already carries.** `CLAUDE.md` says a table can exist without being in
the discovered list "because an ATTACH `table_filter` bounds ENUMERATION only and that path fetches BY NAME".
True — and CONDITIONAL on a filter being set. With no filter there is no by-name path at all, which is
exactly the configuration almost everyone runs.

After v81, in the same session with no refresh: the change table reads, enumeration goes 0 → 6, and the
per-instance TVFs go 0 → 2.

### 14.3 Decisions in the surface

- **`changed` is separate from success.** An idempotent call that found the work already done SUCCEEDS and
  reports `changed = false`; collapsing them would make the ordinary "already enabled" outcome look like a
  failure. (The distinction the plugin uninstaller draws between `removed` and `purged`.)
- **`enable`'s idempotence keys on the capture INSTANCE, not the table** — keying on the table would wrongly
  refuse a table's legitimate second instance, which is how a schema change is absorbed (§2.2). §12 of the
  suite pins both halves, the second being the positive control: without it, "the same call twice reports
  false" would pass equally on a build that refused every re-enable.
- **Every caller-supplied value crosses as a PARAMETER**, never spliced text. These are identifiers and a
  column list a user types.
- **There is NO `disable_database()`.** `sp_cdc_disable_db` drops every capture instance in the database at
  once — a bigger hammer than anything else here, destroying history nothing else on this surface can. An
  operator who means it has `fabricator_exec`; putting it one word away from `cdc.disable('t')` would invite
  the wrong one. `cdc.disable` IS offered because it is per-TABLE and named explicitly by the caller, which
  is the consent line `DROP TABLE` already sits on.
- **`cdc.capture_now()` TRANSLATES the log-scan race** rather than passing it through. §10.2's `22903 … sp_replcmds`
  names nothing a reader would connect to CDC, and §10.2a established that retrying does not help — so the
  message says to stop the capture job or wait a polling interval, and no retry is attempted. It also reports
  `schema_may_change = false`: a log scan creates nothing.

### 14.4 What the suite does NOT cover, said rather than implied

- **`cdc.capture_now()`'s SUCCESS path is not asserted.** It contends with the capture job for the database's single
  log-scan session (~1 failure in 57, measured), and the obvious mitigation is unavailable: `sp_cdc_stop_job`'s
  refusal when the job is not yet running is raised by the Agent proxy, is not catchable in T-SQL, and escapes
  through `fabricator_exec` as `22022` (§10.2a). What IS asserted is its refusal on a non-CDC database, which
  is our own guard. The translated 22903 message is therefore also ungated — producing it needs winning a race
  against a live job on demand.
- **The v81 deferral's explicit-transaction gap is not asserted** — sqllogictest drives connections
  sequentially, and the gap is about a second statement inside one transaction. Documented in §v81 instead.

### 14.5 ⚠ `cdc.scan()` → `cdc.capture_now()` — the rename, and why the obvious name was wrong

**It shipped as `cdc.scan()` and the very first person to read the function list took it for the READER**
(user, on being shown the eight functions: *"so scan will be the one to get a snapshot leg + the changes?"*).
Renamed to **`cdc.capture_now()`** on their instruction, 2026-08-24 — a pure rename, no behaviour change.

The mistake worth learning from is that the old name was **faithful to the wrong thing**. It named the
underlying procedure (`sys.sp_cdc_scan`) rather than what a caller of *this* surface gets, and in a namespace
whose entire purpose is reading changes, "scan" is a reading verb — DuckDB's own vocabulary has `read_parquet`
and `parquet_scan` as synonyms, so a `scan` that returns no data contradicts the surrounding idiom rather than
merely being terse.

⚠ **It gets EXPENSIVE at slice 3, which is why it was worth doing before `changes()` lands.** The two would
sit adjacent in the same schema, a caller wanting rows would find the shorter, more familiar-looking name
first, and calling it in a loop is precisely the per-query CPU decision §3.5 says it must not invite. A wrong
name next to the right one is worse than a wrong name alone.

⚠ **Do not "simplify" it back to match the procedure.** The proc keeps its own name everywhere it is
mentioned — in the T-SQL, in the error text, in every comment — so the invariant now is: **"scan" in the CDC
code means SQL Server's procedure, never a fabricator function.** The reasoning lives on
`CdcCaptureNowFunction`, and the suite pins the name (`verify_mssql_cdc` §14's `function_name IN (...)`),
because a rename with no gate is a rename that can quietly come undone.

---

## 15. THE 2026-08-24 REDESIGN — user-directed, and it SUPERSEDES §4's recommendation and most of §7

> **⚠ SLICE 3 IS NOW BUILT — see §16 for what shipped, and for the five things building it established that
> reading this section could not (one of which, §15.7's "the first call always returns zero rows", it
> REFUTES).**

**This is the design slice 3 was built from**, and where it disagrees with §4, §7,
§11 or §12 it wins. Those sections are left intact because the reasoning that produced them is still worth
reading, and because two of the things they got wrong were findable only by measuring.

The user's framing, which is the requirement the rest of this section serves: *"at the start of the cdc
design i mentioned i want an easy to use cdc"*. Every decision below trades a knob for a measurement.

**⚠⚠ THREE CORRECTIONS UP FRONT, because each reverses something this document asserted:**

1. **§4's Option A is DEAD.** The reader is marshaled C# (§4's Option B), not a SQL rewrite — §15.1.
2. **The change table is not the interface; the TVF is** — and SQL Server says so in metadata — §15.2.
3. **⚠⚠ "The change table's schema is frozen at capture-instance creation" is FALSE for a TYPE change**
   (§15.6). It was asserted as a reassurance during this very redesign, and the measurement inverts it: an
   `ALTER COLUMN <type>` IS propagated, asynchronously, by the capture job.

### 15.1 The reader is marshaled C#, and the DuckDB catalog is not in its path

**User-directed: *"don't rely on the duckdb catalog to access the sql server cdc functions"*.** Right, for a
reason §4 only half-recorded: routing the reader through DISCOVERED objects makes it fail when an ATTACH
`table_filter`/`schema_filter` hides them, and makes every `cdc.enable` require a catalog rebuild before the
reader can see what it just created — which is the whole reason ABI v81 exists.

**⚠ A THIRD OPTION EXISTED THAT §4 NEVER ENUMERATED, AND IT ALMOST WON.** The generated TVFs are DISCOVERED
catalog table functions — §3.5's own `0 → 2` measurement is exactly that fact, sitting here unread. So
`bind_replace` could emit `FROM db.cdc.fn_cdc_get_all_changes_x(...)` and keep every advantage §4 attributes
to Option A. MEASURED against the rig, all four:

- binds and returns the right rows with **BLOB bounds**;
- **projection pushes down** — `SELECT [__$operation], [id], [customer], [amount] FROM [cdc].[fn_...](@a0, @a1, @a2)`;
- **filters push down on top of it** — `... (@a0,@a1,@a2) WHERE ([__$operation] = @p0 AND [id] > @p1)`, five params;
- **op 3 is absent** from `'all'`, so `images := 'after'` costs nothing.

**It dies on none of those grounds: the §5 snapshot protocol cannot be expressed in generated SQL.** TABLOCKX
on connection A while connection B pins a SNAPSHOT view, then A releases, is two connections at different
isolation levels with a lock spanning a specific window. One connection cannot do it — locks are held to end
of transaction, so a single connection holds the TABLOCKX for the WHOLE snapshot read, the opposite of
§5.2's entire point. ⇒ **`ICatalogTableFunction` with C#-owned connections.**

**What that costs, stated rather than buried:** projection and filter pushdown into the change read. It is
acceptable *here* because the window IS the filter and the TVF is already bounded by its arguments; a user's
extra `WHERE customer = 'acme'` is a secondary filter over an already-bounded window. What it buys: the
two-connection protocol, read-time error translation, and §6's k-way merge if that is ever wanted.

⚠ `fabricator_query` / `fabricator_exec` keep the setup functions and any single-statement metadata lookup —
they are the right tool there and are already used that way. They are NOT the reader.

### 15.2 The TVF, never the change table — and the metadata says so

§4's Option A sketch read `cdc.<instance>_CT` directly, and §2.1 recorded the consequence as an accepted
trade-off. Two measurements say otherwise:

- **`is_ms_shipped` splits the schema.** MEASURED: the seven metadata tables, the four placeholder functions
  AND **the change table itself** are `is_ms_shipped = 1`; the generated per-instance TVFs are the ONLY
  objects in the `cdc` schema with `is_ms_shipped = 0`. That is the product stating, in metadata, which of
  the two it considers yours.
- **The TVF validates and the table does not.** A below-floor `from_lsn` through the TVF raises 313; the same
  bounds read straight from the change table return whatever survived, silently.

**Corollary: `is_ms_shipped = 0` is the cheapest enumeration of the generated functions**, and it cannot
catch the placeholders:

```sql
SELECT name FROM sys.objects
WHERE schema_id = SCHEMA_ID('cdc') AND type = 'IF' AND is_ms_shipped = 0;
```

**⚠ The TVF body also PROVES three things this document had inferred.** MEASURED via `OBJECT_DEFINITION`:

```sql
from [cdc].[dbo_o_CT] t with (nolock)
where (lower(rtrim(ltrim(@row_filter_option))) = 'all')
  and ([sys].[fn_cdc_check_parameters](N'dbo_o', @from_lsn, @to_lsn, ..., 0) = 1)
  and (t.__$operation = 1 or t.__$operation = 2 or t.__$operation = 4)
  and (t.__$start_lsn <= @to_lsn) and (t.__$start_lsn >= @from_lsn)
```

1. It is an **inline** TVF, which is why predicates pushed on top of it fold in.
2. Its window predicate is `__$start_lsn` BETWEEN the two arguments — **no seqval or operation
   granularity** — which is what forces the exclusive cursor to be applied locally (§2.4's 21-byte position).
3. `__$command_id` and `__$end_lsn` are not in the select list: §11 item 5 proven from the definition rather
   than from a `describe`.

**⚠ NOLOCK is in the body, and it is benign for one specific reason.** The table hint overrides the
transaction's isolation level, so a SNAPSHOT pin does nothing for the changes leg. It does not need to: the
upper bound is `fn_cdc_get_max_lsn()`, which the capture job advances only after committing its batch, so
everything at or below the watermark is committed. **The window is the guarantee, not the isolation.**

**⚠ And the change table's own clustered index is `(__$start_lsn, __$command_id, __$seqval, __$operation)`** —
MEASURED, UNIQUE, on every change table. So §6's direct-read ordering tuple is the PHYSICAL order, and the
TVF's range predicate is a clustered index SEEK. Nothing else in this document had established that.

### 15.3 ⚠⚠ The 313 message names a PLACEHOLDER OBJECT, and the " ... " is LITERAL

§2.1 renders the error as `cdc.fn_cdc_get_all_changes_ ... .` and every reader of this document — including
its author — took the ellipsis for an abbreviation of the instance name. **It is the object's real name.**
MEASURED: every CDC-enabled database contains four placeholder functions; `LEN` plus UTF-16 hex confirm two
of them are `fn_cdc_get_all_changes_...` (26 chars) and `fn_cdc_get_all_changes_ ... ` (27, with the spaces).
`sys.fn_cdc_check_parameters` says why in its own comments:

```sql
-- Force error 229 execute permission denied on all changes dummy
if exists(select * from cdc.[fn_cdc_get_all_changes_...](0X00, 0X01, 'all')) return 0
...
-- Force error 313 -- Insufficient number of arguments
select @val = sys.fn_cdc_all_changes_range_error()
```

⇒ **the message is IDENTICAL for every capture instance and every cause** — below floor, above max, NULL
bound, *and a misspelled `@row_filter_option`*. It cannot be parsed or attributed. That raises §2.1's
pre-check above the value §2.1 itself claimed: it is not a nicety, it is the only channel by which a user
can ever learn what went wrong.

**⚠ SQL Server's own boolean validator cannot be borrowed.** MEASURED: `sys.fn_cdc_is_range_valid` and
`sys.fn_cdc_has_select_access` are internal — `Msg 4121`, and they do not appear in `sys.all_objects`.
`sys.fn_cdc_check_parameters` IS callable and returns 1 for a valid window, but validates an invalid one by
THROWING that same 313. So the pre-check stays hand-rolled against `fn_cdc_get_min_lsn` / `fn_cdc_get_max_lsn`.

### 15.4 The capture instance name is GENERATED and HIDDEN — and it removes a defect

**User-directed**, and the measurements turn it from an ergonomic preference into a fix.

- **The limit is exactly 100 characters**, enforced by `sp_cdc_verify_capture_instance`: `Msg 22927 …
  exceeds the length limit of 100 characters`. 100 is accepted and yields a TVF name of **123**
  (`fn_cdc_get_all_changes_` is 23, `sysname` is 128) — so SQL Server already sized its own limit against
  the prefix, with five to spare. The arithmetic worry is real and already handled upstream of us.
- **⚠⚠ THE DEFECT: THE DEFAULT NAME IS REFUSED FOR A LONG TABLE.** With no `@capture_instance`,
  `sp_cdc_enable_table` builds `<schema>_<table>`; MEASURED, a 100-character table in `dbo` produces
  `dbo_tttt…` = 104 and the SAME `Msg 22927`. So today's `cdc.enable('dbo.<long name>')` fails with an error
  about a name the user never chose, and the only escape is the very knob we want to hide.

⇒ **generate `fab_<16 hex of a hash of schema.table>`** (20 chars) plus a one-character discriminator for the
second instance (§2.2 caps it at two). Deterministic base ⇒ re-enabling the same table computes the same
name, so "is this already ours?" is a lookup rather than a scan.

**⚠ A derived name is guaranteed to go stale; an opaque one cannot.** MEASURED (§15.6): a table rename is
ALLOWED and CDC follows it, so `dbo_o` — SQL Server's own default — permanently misnames a table now called
`orders2`. `fab_<hash>` never claimed to mean anything.

⚠ The instance name stays user-VISIBLE in `cdc.tables()`. That is fine and already anticipated: slice 1's
`min_position(...)` accepts EITHER a capture-instance name or `schema.table` and refuses ambiguity, so a
user with opaque names passes the table name, which already works.

### 15.5 An extended property marks the instance as OURS — but it is not the resolution

**User-directed: set an extended property on the TVF naming the source table, and have `cdc.changes`
enumerate TVFs and match on it.** MEASURED as working, on both carriers:

```sql
EXEC sys.sp_addextendedproperty @name=N'fabricator_source', @value=N'dbo.ep',
     @level0type=N'SCHEMA',@level0name=N'cdc',@level1type=N'FUNCTION',@level1name=N'fn_cdc_get_all_changes_fab_ep';
-- and the enumeration returns exactly:  cdc.fn_cdc_get_all_changes_fab_ep | dbo.ep
```

An EP can also be set on the change table (`@level1type = 'TABLE'`), and both are dropped with their object.

**⚠ THE CORRECTION: the EP must be the OWNERSHIP MARKER, not the mapping.** MEASURED (§15.6): after
`sp_rename 'dbo.o', 'orders2'`, `cdc.change_tables.source_object_id` resolves to **`orders2`** while the EP
text still says `dbo.o`. So:

| carrier | job |
|---|---|
| **EP presence** | "this instance is ours" — which no built-in metadata can express, and which is exactly what is needed once the names are opaque (§15.4) |
| **EP value** | provenance: the name the user typed, for diagnostics and `cdc.tables()` |
| **`source_object_id`** | **the resolution** — it follows a rename, so it is what answers "which instance serves `dbo.orders2`?" |

⚠ `sp_cdc_enable_table` and `sp_addextendedproperty` are two statements. If the EP write fails we own an
UNMARKED instance. Run both in one transaction; the fallback (treat unmarked as ours) silently adopts a
DBA's instance and must not be the design.

### 15.6 ⚠⚠ THE SCHEMA CORRECTION — the change table is NOT frozen, and rename cannot happen

The DDL matrix, every row MEASURED:

| source DDL | change table | detector |
|---|---|---|
| `ADD COLUMN` | **not propagated** — never captured by this instance | `cdc.ddl_history` |
| `DROP COLUMN` | **not propagated** — the column stays, new rows read NULL | `cdc.ddl_history` |
| **`ALTER COLUMN <type>`** | **PROPAGATED, ASYNCHRONOUSLY, by the capture job** | `ddl_history.required_column_update = 1` |
| `sp_rename` a captured COLUMN | **REFUSED by SQL Server** | n/a — it cannot happen |
| `sp_rename` the TABLE | allowed; CDC follows it via `source_object_id` | — |

**The type case, in full, because it reverses the reassurance:**

```
ALTER TABLE dbo.o ALTER COLUMN customer NVARCHAR(50) -> NVARCHAR(200)
ALTER TABLE dbo.o ALTER COLUMN amt DECIMAL(9,2)      -> DECIMAL(18,4)

cdc.fab_o_CT immediately after :  customer nvarchar(100 bytes)   amt decimal(9,2)
cdc.fab_o_CT after the job ran :  customer nvarchar(400 bytes)   amt decimal(18,4)
```

`cdc.ddl_history` flags both `required_column_update = 1`, an insert of a 200-character value and
`123456789.1234` landed intact, and `sys.dm_cdc_errors` stayed empty. ⇒ **the CT schema CAN change under a
running read**, and the earlier "frozen at creation" claim held only because the first `sys.columns` read
happened before the capture job processed the DDL.

**⚠ WHERE THAT BITES IS NARROW AND WORTH KNOWING PRECISELY.** Our read mapping sends `nvarchar(n)` to a
length-agnostic Arrow `string`, so a widened string is harmless. `Decimal128Type` carries precision and
scale. **So the hazard is numeric precision/scale (and temporal scale), not strings**: declaring
`decimal(9,2)` at bind and receiving `123456789.1234` at execute is a conversion failure or a silent
corruption. Rule: declare the WIDER of (source, captured) per column, and if the CT type has moved by
execute, **fail loudly rather than convert**.

**⚠ THE RENAME FINDINGS, because they close the nastiest case and validate §15.5:**

```
EXEC sp_rename 'dbo.o.customer', 'client', 'COLUMN';
Msg 4928: Cannot alter column 'customer' because it is 'REPLICATED'.
```

CDC marks captured columns with the replication flag, so data captured under a name we no longer declare is
**impossible**. ⚠ Note the asymmetry: `DROP COLUMN` on a captured column IS allowed (the CT keeps it, new
rows read NULL) while `sp_rename` is refused.

A TABLE rename is allowed, capture continues across it (a post-rename insert was captured), the TVF keeps
working, and `source_object_id` resolves to the new name — which is the measurement behind §15.5's split.

### 15.7 The output schema is SOURCE-DERIVED, so the enable can DEFER to execute

**User-directed, and it removes the one objection that had looked unavoidable.** Earlier analysis concluded
that `enable := true` must run at BIND because the output schema comes from the change table, which does not
exist until enable — and therefore that `EXPLAIN`, `DESCRIBE` and `CREATE VIEW` would perform DDL.

Deriving the declared schema from the SOURCE table plus the four known TVF metadata columns
(`__$start_lsn`, `__$seqval`, `__$operation`, `__$update_mask`) dissolves that: bind needs no change table,
so the enable moves to `Execute()` and bind is side-effect-free again.

**It is correct by construction for a fresh enable.** MEASURED: a default `sp_cdc_enable_table` captures
every source column (`id,customer,amt`), so at the instant we enable, captured == source.

⚠ **Two things move to Execute with it, and both are real:**
- **Window resolution.** No instance ⇒ no floor, and `max_lsn` may be NULL. Incidentally this FIXES §3.4's
  determinism complaint about defaulting `ending_position` at bind.
- **The §2.1 retention pre-check**, whose error now arrives mid-stream. Under a C#-owned reader we translate
  it either way, so what is lost is earliness — a good trade against enabling CDC from an `EXPLAIN`.

⚠ **When the instance DOES exist at bind, prefer the CAPTURED set** (authoritative for what the TVF returns)
unioned with source-only columns. Source-derived is the right answer specifically for the not-yet-enabled
case.

⚠ The first call after an auto-enable **always returns zero rows** — the instance's `start_lsn` is now and
`max_lsn` is NULL until the capture job scans. Document it as priming the pump; `include := 'snapshot+changes'`
is the answer for "I want data now", and it composes with auto-enable exactly.

### 15.8 Alignment by NAME, and who does the widening

The declared schema and the TVF's actual columns are aligned BY NAME, missing columns NULL-filled. MEASURED
in DuckDB, since the user's point was that `UNION ALL BY NAME` would give the widening for free:

```
DECIMAL(9,2) u DECIMAL(18,4)  ->  DECIMAL(18,4)      -- widened; 1.2300 and 123456789.1234 both intact
column in one branch only     ->  present, NULL-filled, keeps its own type
INTEGER u VARCHAR             ->  VARCHAR            -- coerced SILENTLY, not refused
```

**True — but the free widening is only available if the alignment happens in DuckDB SQL**, and §15.1 puts the
reader in C#. Two shapes:

| | |
|---|---|
| **(a) two mechanisms** | SQL form for changes-only, C# when a snapshot leg is requested |
| **(b) one mechanism** | C# everywhere, widen ourselves — **RECOMMENDED** |

(b) because the set of types where "wider" is meaningful in our mapping is small: DECIMAL (max precision,
max scale) and integer promotion; strings and binaries are already length-agnostic in Arrow. A
`WidenArrowType(a, b)` helper buys one reader and one schema-resolution path, where (a) buys two that must
be kept in agreement — the divergence shape this codebase has been bitten by before.

> **⚠⚠ SUPERSEDED BY §19.2, AND THE RULE IN THE PARAGRAPH ABOVE IS WRONG.** There is a THIRD shape this
> analysis missed: the reader GENERATES T-SQL, so the alignment can happen on the SERVER — one statement,
> one describe, one stream, and SQL Server's own type precedence does the widening. MEASURED,
> `decimal(9,0) ∪ decimal(5,4)` is `decimal(13,4)`, not the `decimal(9,4)` that "max precision, max scale"
> gives — at which a nine-integral-digit value overflows. And the helper has **no reachable case** anyway:
> an `ALTER COLUMN <type>` is propagated to BOTH change tables so the instances converge, a column captured
> by one instance is NULL-filled, and a drop-and-re-add is a CONFLICT rather than a widening. Read §19.2
> before reviving any of this.

⚠ **The third line is a caution, not a feature**: on a genuine type conflict DuckDB coerces to VARCHAR rather
than erroring, so "the union failed" can never be used as a drift detector.

**⚠ THE ALIGNMENT MACHINERY IS ALSO §7's TWO-INSTANCE MERGE.** Old instance for `[from, B)`, new for
`[B, to]`, aligned by name into one declared schema. Building it for schema drift makes slice 6 nearly free.

**⚠ BUT IT NEEDS `_capture_instance` OR IT REINTRODUCES §7's OBJECTION.** A NULL for a column that did not
exist yet is indistinguishable from a genuine NULL. Emitting the instance per row makes it decidable — the
consumer can tell "this row predates the column" from "this row had no value" — and that is what makes
projecting to the union acceptable where §7 said refuse.

### 15.9 `on_schema_change` — resync by default, fill as an option, NULL as the floor

**User decision, and the ordering is right: NULL is also a claim we cannot support.** It asserts the column
had no value, when the truth is that we never captured it.

| mode | behaviour | verdict |
|---|---|---|
| `resync` | new capture instance + a fresh snapshot leg (§5), then changes from its `start_lsn` | **DEFAULT when `enable := true`.** Coherent: the snapshot is a consistent point in time and the handoff is MEASURED exactly-once |
| `fill` | added columns read from the SOURCE by key, for rows after the adding DDL | opt-in only, and name the semantics: values are as of NOW, not as of the change |
| `null` | NULL + `_capture_instance` | the floor — honest and decidable, never silent |
| `error` | refuse, naming the boundary | **DEFAULT when the instance is NOT ours** |

**⚠ `resync` may only be the default when WE own the capture instance.** Creating an instance and taking a
full-table snapshot on a DBA's configuration is a heavy, privileged act that must not happen implicitly —
which gives §15.5's ownership marker a second job.

**⚠ What `fill` costs, so the option is chosen with open eyes** (this is why it is not the default): it
produces a TORN ROW — captured columns as of LSN L, the looked-up column as of now — and since the added
column is not captured, **no later change event ever corrects it**. It also needs a key, so it is impossible
for exactly the tables that most need help (no PK and no unique index is a legitimate configuration,
MEASURED), and it adds a read of the LIVE source table to what was a capture-layer-only read.

### 15.10 The lock is `TABLOCK, HOLDLOCK`, and only for the snapshot leg

⚠ **The hint changed too — see §5.2a.** `TABLOCKX` is stronger than the job needs: MEASURED, a shared
table lock held with `HOLDLOCK` blocks a writer (`Msg 1222`) while a READ COMMITTED reader passes and
`sp_cdc_scan` still runs, so the protocol stops blocking readers it never needed to block. ⚠ And `TABLOCK`
without `HOLDLOCK` is STATEMENT-scoped — measured gone by the next statement — so it cannot span steps 2–3.

For `include := 'changes'` a lock buys **nothing**: the change table is append-only and the LSN window fully
determines the result. Taking a table lock on every `cdc.changes()` would block writers on the source table
for the duration of bind — including on an `EXPLAIN`. That is a regression, not a safety measure.

It would not buy the schema guarantee either: TABLOCKX on `dbo.orders` does not protect the CDC metadata.

⇒ default path: resolve window and schema, no lock, no pinned connection. `snapshot+changes`: the §5
two-connection protocol, and only there.

### 15.11 Third-party DDL inside a running window

The user's question. What the reader can do about it, now that §15.6 has the matrix:

- **Detect it at bind (or at window resolution).** `cdc.ddl_history` carries `ddl_lsn`, so "did a DDL land
  inside my window?" is one predicate, and `ddl_command` gives the text to put in the message.
  `required_column_update = 1` narrows it further to "a captured column's TYPE changed inside this window".
- **Only ADD COLUMN needs a new instance.** Type changes pass through (§15.6); a DROP leaves the column
  returning NULL and a source-derived declaration simply stops projecting it.
  ⚠ One case that looks like an ADD and is not: **drop-then-re-add with a different type** — the CT still
  holds the old column under the same name, so name-alignment collides two types and §15.8's measurement
  says DuckDB would coerce both to VARCHAR rather than complain.
- **⚠ At most TWO capture instances** (`Msg 22962`, MEASURED §2.2), so auto-re-enable is NOT repeatable: the
  second drift must first `disable` the oldest, which **destroys its unread history**. Safe only once the
  caller's cursor is past the boundary, which we cannot know ⇒ dropping the old instance must be opt-in.
- **Two residual hazards, REASONED not measured**: an in-flight read holds Sch-S on the change table, so a
  concurrent `sp_cdc_disable_table` should block rather than pull the object out from under us (§8.7 assumes
  this); and the **cleanup job can purge below the floor mid-read**, which with NOLOCK would silently shorten
  a window whose `from_lsn` sits near the retention edge. The pre-check narrows that to a race, not to zero.

### 15.12 The revised slice table

| slice | contents | why this order |
|---|---|---|
| **1** | ✅ BUILT — §13 | inspection first |
| **2** | ✅ BUILT — §14 | setup from SQL |
| **3** | ✅ **BUILT 2026-08-24 — §16.** The reader: `ICatalogTableFunction`, C#-owned connection, ONE capture instance, `images := 'after'`, explicit bounds, the §2.1 pre-check, the 21-byte cursor | the smallest correct reader. §15.1/§15.2/§15.7 |
| **4** | ✅ **BUILT 2026-08-24 — §17.** Generated hidden instance name + the EP ownership marker + `enable := true` deferred to execute | makes the surface "easy to use"; independent of 5–7. §15.4/§15.5/§15.7 |
| **5** | ✅ **PARTLY BUILT 2026-08-25 — §18.** `_capture_instance` shipped; name-alignment and widening DEFERRED into slice 7 (§18.1 says why), and slice 6's DDL detection pulled forward | buys slice 7 nearly free. §15.8 |
| **6** | ✅ **BUILT 2026-08-25 — §18.3**, pulled forward into slice 5. `on_schema_change := 'error'` (default) / `'ignore'` | loud before it is clever. §15.11 |
| **7** | ✅ **BUILT 2026-08-25 — §19.** The two-instance boundary: derive the split, partition the window, `UNION ALL` by name. It also retires BOTH items deferred from slice 5 — the name-alignment is BUILT, and `WidenArrowType` is **DISSOLVED** (§19.2: the union is in T-SQL, and the helper's stated rule was measurably wrong AND has no reachable case) | needed 3 and 5 |
| **8** | `include := 'snapshot'` / `'snapshot+changes'` (§5), then `on_schema_change := 'resync'` | the resync story needs the snapshot leg first |
| **9** | `starting_timestamp` / `ending_timestamp`; `images := 'both'` + the mask placeholder | additive to the same reader |
| — | ~~`on_schema_change := 'fill'`~~ | last, if ever — §15.9 records why |

⚠ The old §12 ordering put the snapshot leg at 5 and the two-instance boundary at 6. The reordering follows
from §15.9: `resync` is the preferred answer to schema drift, and it CANNOT be built before the snapshot leg.

### 15.13 Still unmeasured

- **How long the capture job takes to apply a `required_column_update`** — that is the width of the window
  where declared and actual types disagree, and it decides whether "fail loudly at execute" is a rare event
  or a routine annoyance. The single most useful number left. ⚠ Slice 3 SHIPPED the loud failure (§16.4
  item 1) and it is UNGATED for exactly this reason: no suite can arrange the race.
- Whether an in-flight TVF read really blocks `sp_cdc_disable_table` (§15.11).
- The Azure SQL Database middle case (§11 item 7) — unchanged.

### 15.14 ⚠ A SHIPPED DEFECT FOUND ON THE WAY: `cdc.tables()` reports a bogus `index_column_list` — ✅ FIXED 2026-08-24 (§16.7)

MEASURED through the exact table-variable path `SqlServerCdcCatalog` uses: the **all-tables** form of
`sp_cdc_help_change_data_capture` leaks the PREVIOUS row's `index_column_list` onto a row that has no index.

```
dbo_o      | [id] | PK__o__3213E83FEE57FF32
dbo_o_v2   | [id] | PK__o__3213E83FEE57FF32
dbo_plain  | [id] | <NULL>          <-- no index, yet [id] is reported
```

Called for that one table the proc returns NULL correctly. Fix is one line — null `index_column_list` when
`index_name IS NULL` — plus an assertion in `verify_mssql_cdc` §3, since nothing currently covers a capture
instance with no index. Not a reader concern; it is in slice 1's shipped surface.

---

## 16. Slice 3 — THE READER, AS BUILT (2026-08-24)

Built from §15. `db.cdc.changes(...)` is a marshaled `ICatalogTableFunction` over SQL Server's generated
per-instance TVF, with C#-owned connections, one capture instance, `images := 'after'`, explicit bounds, the
§2.1 retention pre-check and the 21-byte resume position of §2.4. Gate: `verify_mssql_cdc` **105 → 182**
(service tier), three mutants, each killed at its own assertion.

```sql
FROM db.cdc.changes('dbo.orders'
      [, starting_position := <BLOB>]     -- EXCLUSIVE lower bound: a 10-byte LSN or a 21-byte _position
      [, ending_position   := <BLOB>]     -- INCLUSIVE upper bound; default cdc.max_position()
      [, capture_instance  := '<name>']   -- required when a table has two (§2.2)
      [, images            := 'after']    -- the only value this release accepts
      [, commit_timestamp  := false])     -- opt-in; see §11 item 2
```

Output: `_change_type`, `_position`, `_commit_lsn`, `_seq_val`, `_operation`, optionally
`_commit_timestamp`, then the captured source columns.

### 16.1 How it resolves, in two round trips at bind and one at execute

**Bind.** One metadata batch through `sp_cdc_help_change_data_capture` resolves `source` (a `schema.table`
name OR a capture-instance name) to exactly one instance and brings back the SOURCE table's per-column
nullability in the same result; two matches are REFUSED naming both. Then one **DESCRIBE** —
`CommandBehavior.SchemaOnly` over the very statement the reader is about to run, with `c.*` so the captured
column NAMES are what is being learned — supplies the types through `SqlArrowMapping.ToArrowField`, the same
call the read itself makes. `DescribeQuery` gained a parameterized overload for this; the parameter VALUES
are never evaluated but the parameters must be DECLARED or SQL Server cannot compile what it is describing.

⚠ The four TVF metadata columns are asserted BY NAME (`__$start_lsn`, `__$seqval`, `__$operation`,
`__$update_mask`) rather than assumed to be first. The alternative to that check is reading four metadata
columns as DATA and shifting every source column by one, silently.

**Execute.** One short metadata read resolves `fn_cdc_get_min_lsn(<instance>)` and `fn_cdc_get_max_lsn()`,
the pre-check runs, and the change read streams.

The composed statement, with the cursor folded in as a WHERE clause:

```sql
SELECT CASE c.[__$operation] WHEN 1 THEN 'delete' … END AS [_change_type],
       c.[__$start_lsn] + c.[__$seqval] + CONVERT(binary(1), c.[__$operation]) AS [_position],
       c.[__$start_lsn] AS [_commit_lsn], c.[__$seqval] AS [_seq_val], c.[__$operation] AS [_operation],
       c.[id], c.[customer], …
FROM cdc.[fn_cdc_get_all_changes_dbo_orders](@from_lsn, @to_lsn, @row_filter) AS c
WHERE (c.[__$start_lsn] > @cur_lsn OR (c.[__$start_lsn] = @cur_lsn AND (c.[__$seqval] > @cur_seq
       OR (c.[__$seqval] = @cur_seq AND c.[__$operation] > @cur_op))))
```

**⚠ MEASURED that the position concatenation is exactly 21 bytes and that the operation byte is the LOW byte
of the `int`** — `0x…02` for an insert, `0x…04` for an update after-image, `0x…01` for a delete. The gate
pins the ENCODING rather than describing it: `_position = _commit_lsn || _seq_val || '\x04'::BLOB`.

**⚠ The window is resolved at EXECUTE, not at bind** (§15.7), which is also what fixes §3.4's determinism
complaint about a defaulted `ending_position`.

**⚠ The change read runs POOLED, and unlike every other read on this surface that is the CORRECT answer
rather than a compromise.** Read-your-writes buys a change reader nothing: the capture job populates the
change table ASYNCHRONOUSLY from COMMITTED log records, so a transaction's own uncommitted writes are not
there to be seen on any connection. Routing onto the pinned connection would only hold a long streaming
reader open on the write connection — the outstanding-result-set hazard (595 on a no-MARS engine). The
window resolution deliberately goes the other way, because a capture instance enabled in this transaction IS
visible to it.

**⚠ No `ORDER BY` is emitted, deliberately.** The change table's clustered index is
`(__$start_lsn, __$command_id, __$seqval, __$operation)` (MEASURED, §15.2) so ordering by our 3-tuple would
insert a real SORT rather than ride the index, and DuckDB does not promise to preserve a table function's row
order through its pipeline anyway. Every row carries its own `_position`; `ORDER BY _position` is the
documented and correct way to ask for order.

### 16.2 ⚠⚠ THE CURSOR IDIOM, CORRECTED — and there IS a pure-SQL one

**This supersedes §3.4's code block, which does not run as printed.** Three spellings, all MEASURED
2026-08-24 against the built reader:

| spelling | result |
|---|---|
| `changes(…, starting_position := (SELECT … ))` | **`Binder Error: Table function cannot contain subqueries`** — already known (§11 item 6) |
| `EXECUTE q((SELECT … ))` | **`Invalid Input Error: Only scalar parameters, named parameters or NULL supported for EXECUTE`** — NEW, and it CLOSES the "NOT measured" note §3.4 shipped with. §3.4's own example uses exactly this and is wrong |
| `EXECUTE q(db.cdc.max_position())` | **works** — a scalar FUNCTION CALL is a legal EXECUTE argument where a subquery is not |
| `SET VARIABLE cur = (SELECT … );` then `changes(…, starting_position := getvariable('cur'))` | **works** — and this is the one that reads the cursor OUT OF A TABLE |

⇒ the idiom to publish:

```sql
-- 1. take the window end FIRST, and store it whatever the read returns
SET VARIABLE cdc_end = (SELECT db.cdc.max_position());

-- 2. read a closed window, resuming from the cursor your own table holds
SET VARIABLE cdc_cur = (SELECT cur FROM my_cursors WHERE tbl = 'dbo.orders');
INSERT INTO staging
SELECT * FROM db.cdc.changes('dbo.orders',
                             starting_position := getvariable('cdc_cur'),
                             ending_position   := getvariable('cdc_end'));

-- 3. advance to the WINDOW END, not to what you saw
UPDATE my_cursors SET cur = getvariable('cdc_end') WHERE tbl = 'dbo.orders';
```

`SET VARIABLE` accepts a subquery; `getvariable()` is a function call at the call site, so neither refusal
applies. **That makes a resumable pipeline expressible in pure SQL — no client, no prepared statement, no
spliced literal** — which is what §2.3 said the whole surface exists for and what §3.4 had concluded was
unavailable.

⚠ **BOTH bound LENGTHS are legal on both sides, and that is a requirement rather than a convenience.** The
idiom above stores a 10-byte LSN while a row's own `_position` is 21 bytes; accepting only one would break
the idiom the docs teach. A 10-byte lower bound is exclusive AT LSN GRANULARITY, which is exactly right —
the previous window ended at that LSN INCLUSIVE. The 21-byte form is what resumes mid-transaction: the gate
inserts three rows in one statement, so they share one `start_lsn`, and resuming after the FIRST of them
must leave the other two — which no 10-byte cursor can express.

### 16.3 The pre-check, as built

Every refusal names the cause, the value and the way out, because the alternative is §15.3's placeholder
error that names none of them:

| condition | answer |
|---|---|
| `starting_position` below `fn_cdc_get_min_lsn` | ERROR: *"is BELOW the retention floor … THIS READ WOULD HAVE SILENTLY SKIPPED THEM"*, with both LSNs and the remedy |
| `ending_position` above `fn_cdc_get_max_lsn` | ERROR naming the watermark and the two-step idiom |
| floor is NULL | ERROR: *"not established yet"* — never read as "no lower bound" (§2.1) |
| watermark is NULL, end DEFAULTED | **zero rows**, no error — the ordinary state of a freshly enabled instance |
| watermark is NULL, end SUPPLIED | ERROR — a bound that cannot exist is worth answering |
| `from > to` | **zero rows**, no error |
| bound length ∉ {10, 21} | ERROR at BIND — the earliest point the value exists |

**⚠ Mutant 1 dies at exactly the below-floor assertion, AND ITS ACTUAL RESULT IS THE RAW 313** — *"An
insufficient number of arguments were supplied for the procedure or function
cdc.fn_cdc_get_all_changes_ ... ."* on a call with three arguments. That is the best evidence in this
document that the pre-check is the feature rather than a nicety.

### 16.4 ⚠⚠ FIVE THINGS THE BUILD ESTABLISHED THAT READING COULD NOT

1. **`IArrowType.Equals` IS REFERENCE EQUALITY, and using it made the schema check fire on every
   well-formed read.** Apache.Arrow does not override `Equals` on its type classes, so two separately
   constructed `Decimal128Type(18,4)` instances — one from the describe crossing, one from the execute
   crossing — are unequal. The first smoke test refused its own correct read with *"declared 'amount'
   decimal128 … arrived as 'amount' decimal128"*, a message comparing two identical renderings. Fixed with
   a structural comparer, and the message now renders precision/scale — `IArrowType.Name` prints
   `decimal(9,2)` and `decimal(18,4)` identically as `decimal128`, which is precisely the difference the
   check exists to report. ⚠ A singleton such as `StringType.Default` would have masked it; a DECIMAL column
   in the probe is what exposed it.

2. **DuckDB DROPS the declared nullability, so the source-vs-change-table split of §1.2 and §3.3 is
   UNASSERTABLE.** MEASURED in all three directions reachable from SQL — `DESCRIBE` over the function, a
   `CREATE TABLE AS` from it, and `duckdb_columns()` over that table — every column reports nullable,
   including `_change_type`, which the reader declares NOT NULL. The reader still declares source
   nullability (it is the honest answer and it rides the round trip that resolves the capture instance
   anyway) and the gate SAYS SO rather than implying coverage. It becomes load-bearing when
   `images := 'both'` arrives, where an op-3 before-image can carry NULL for an unrecorded MAX column and
   the claim has to be relaxed.

3. **⚠ §15.7's *"the first call after an auto-enable always returns zero rows"* is WRONG in an
   already-capturing database.** MEASURED: a table enabled in a database whose capture job is already
   running has a NULL FLOOR — not merely a NULL watermark — because `fn_cdc_get_min_lsn` is NULL for up to
   one polling interval (the effect `CdcMinLsn` recorded on 2026-08-23 with a discriminator). So the reader
   answers *"the retention floor … is not established yet — retry"* rather than zero rows, and reads
   cleanly once the job scans. §15.7's sentence holds only for the FIRST instance in a freshly CDC-enabled
   database, where the watermark is the thing that is NULL. Slice 4's `enable := true` has to expect the
   retry, not silence.

4. **⚠ The inverted-window branch is NARROWER than its own comment claimed, and a SURVIVING MUTANT is what
   showed it.** Disabling the `from > to` short-circuit left the suite GREEN. A caught-up polling consumer
   passes its cursor with the end DEFAULTED, so `from == to` (the watermark) — which the TVF accepts, and
   whose rows the exclusive predicate removes in SQL. Reaching `from > to` needs an EXPLICIT
   `ending_position` below the cursor. Gated with exactly that shape
   (`starting_position := max_position(), ending_position := min_position(...)`), after which the mutant
   dies with the raw 313.

5. **A DESCRIBE of the reader's own statement works, parameters and all.** MEASURED through
   `sp_describe_first_result_set` with `@params` and then through SqlClient's `SchemaOnly`: the CASE
   expression describes as `varchar(16)`, the concatenation as `binary(21)`, the LEFT JOIN's
   `tran_end_time` as `datetime`, and the TVF's own four metadata columns come first in the documented
   order followed by the captured source columns.

### 16.5 Choices worth knowing before extending it

- **`images` is DECLARED although only its default is implemented.** A caller who writes the mode they read
  about gets a sentence rather than DuckDB's "invalid named parameter", and the two refusals are
  deliberately different: `'both'` is *not built yet*, `'net'` is *not a value this reader will ever have*
  (§1.7d). It also pins the vocabulary now rather than inventing it later.
- **No `max_rows`, though §3.2 lists it.** A truncated read breaks the cursor idiom — the caller would have
  to advance to `max(_position)` rather than to the window end, which is exactly the trap §3.4 exists to
  warn about. It belongs with a story about resuming a PARTIAL window, not with the smallest correct reader.
- **`_update_mask` is not emitted.** In `'after'` mode the after-image is the truth, so the mask is not
  needed for correctness; it arrives with `images := 'both'`, where it is the only way to read a NULL
  correctly.
- **The DESCRIBE opens its OWN short-lived connection** (`DescribeQuery`'s documented behaviour), so a
  capture instance enabled inside an UNCOMMITTED transaction cannot be described. The error says so — and
  the shape is harmless, because a change captured by that same transaction would not be readable either:
  the capture job reads COMMITTED log records.

### 16.6 What the gate does NOT cover, said rather than implied

- **The declared nullability** — invisible from SQL in every direction (§16.4 item 2).
- **A captured column's TYPE changing between bind and execute** (§15.6). The check exists and is reasoned
  from a measurement, but producing the race needs the capture job to apply a `required_column_update`
  inside one statement's lifetime, which no suite can arrange. §15.13's open number is exactly this window's
  width.
- **A source table dropped out from under a capture instance**, where the nullability join returns nothing
  and every source column degrades to nullable — the safe direction, unasserted.
- **Two capture instances read as ONE stream across the boundary** — that is slice 7, and until it exists
  the refusal in §19 of the suite is the whole behaviour.
- **Permissions.** The rig is `sa`, so the `@role_name` filtering that makes
  `sp_cdc_help_change_data_capture` the right resolution path is exercised nowhere.

### 16.7 §15.14's defect is FIXED

`cdc.tables()` no longer reports a bogus `index_column_list` for a capture instance with no index — the
column is nulled when `index_name IS NULL`. Gated with an index-less instance beside an indexed one; the
indexed row is the positive control AND what makes the mutant die, since the leak copies the PREVIOUS row's
value. ⚠ The probe table is named to sort LAST for that reason.

---

## 17. Slice 4 — THE HIDDEN INSTANCE NAME, THE OWNERSHIP MARKER AND `enable := true`, AS BUILT (2026-08-24)

Built from §15.4, §15.5 and §15.7. C#-only, no ABI change. Gate: `verify_mssql_cdc` **182 → 239**, four
mutants, each killed at its own assertion.

```sql
-- capture a table without ever naming a capture instance
SELECT * FROM db.cdc.enable('dbo.orders');       -- -> dbo.orders (fab_61a00e766c20381c)

-- …or let the first read do it
FROM db.cdc.changes('dbo.orders', enable := true);
```

### 17.1 The name is generated, and it removes a defect

`fab_` plus 16 hex characters of MD5 over `schema.table` — **20 characters, whatever the table is
called**.

**⚠⚠ THE DEFECT, MEASURED both ways.** `sp_cdc_enable_table`'s own default is `<schema>_<table>` and the
limit is exactly 100 characters, so a 100-character table in `dbo` produces a 104-character name and
`Msg 22927 … exceeds the length limit of 100 characters` — `cdc.enable` failing with an error about a name
the user never chose, whose only escape was the very knob we wanted to hide. The same table enables fine
with a generated name. Both legs are gated (§22), with the table's own length as the positive control:
without it, the passing row would prove nothing about LENGTH.

**⚠⚠ ANY STABLE DIGEST WILL DO — but NEVER `string.GetHashCode()`.** .NET randomizes string hash codes PER
PROCESS, so a GetHashCode-derived name would differ on every run — the opposite of the determinism this exists for, and it
would present as a caching bug. ⚠ The gate cannot catch that specific mistake: within ONE process
`GetHashCode` is stable, and sqllogictest gives one process. What it does catch is a generator that is not a
function of the name at all (a GUID, a timestamp, a counter), by disabling and re-enabling and demanding the
same name back.

**MD5 rather than SHA-256 (user, 2026-08-25), and why it is a free choice is worth stating**: this is a
NAME, not a security boundary. Nothing authenticates or authorises on it, only 64 of its bits are kept, and
a collision costs a REFUSAL (`this capture instance already exists`) rather than a wrong answer, because SQL
Server enforces instance-name uniqueness.

**⚠ CHANGING THE DIGEST IS SAFE, and that falls out of §17.3 rather than luck.** Idempotence keys on the
TABLE, so the generated name is computed only when CREATING — a table already captured under a name from a
different digest is still found, reported and read. No migration, no orphan. ⚠ The corollary is a standing
rule: **never PARSE the name to recover the table.** `cdc.tables()` and the `fabricator_source` marker are
the mapping.

**⚠ The input is NOT lower-cased, deliberately.** Normalising case would make two genuinely different tables
on a case-SENSITIVE collation (`dbo.Orders`, `dbo.orders`) collide on one instance name, and the second
enable would then fail naming a name the caller never chose — the defect above, reintroduced. Nothing is
lost, because idempotence keys on the TABLE (§17.3), so two spellings of one table cannot produce two
instances — which the gate asserts directly.

**⚠ An opaque name cannot go stale and a derived one does.** MEASURED (§15.6): a table rename is allowed and
CDC follows it, so SQL Server's own `dbo_o` permanently misnames a table now called `orders2`.
`fab_<hash>` never claimed to mean anything.

**⚠ There is NO second-instance discriminator yet**, though §15.4 anticipated one. Nothing in this release
creates a second instance by itself — a default enable refuses to (§17.3) and an explicit
`capture_instance :=` names it — so the shape would be a guess. Slice 8's `resync` is what will need one and
should choose it then.

### 17.2 The ownership marker, and the transaction it needs

An extended property `fabricator_source` on the instance's `fn_cdc_get_all_changes_*` function, valued with
the resolved `schema.table`. Surfaced as a new `cdc.tables()` column of the same name: **non-NULL means the
instance was created by this extension**.

| carrier | job |
|---|---|
| **presence** | "this instance is ours" — no built-in metadata expresses it, and it is what makes the opaque names safe to manage |
| **value** | provenance, for diagnostics and `cdc.tables()` |
| **`source_object_id`** | **the resolution** — it follows a rename where the marker text goes stale (MEASURED, §15.6) |

**⚠⚠ THE PAIR IS ONE TRANSACTION, and all three states are MEASURED** — §15.5 warned that a failed marker
write leaves an instance we own but cannot recognise, and the warning is real:

| context | on a failed marker write |
|---|---|
| autocommit, plain batch | the enable **SURVIVES** unmarked — the outcome to avoid |
| autocommit + our own `BEGIN/COMMIT` | the enable is **ROLLED BACK** — atomic, and what ships |
| inside an AMBIENT transaction, via a savepoint | **unusable** — the marker's error kills the whole transaction before a `CATCH` can act (`XACT_STATE() = 0`, `@@TRANCOUNT = 0`), so there is nothing to roll back TO |

⇒ `IF @@TRANCOUNT = 0 BEGIN TRANSACTION`. We open one only when we are outermost; nested, we inherit a
transaction whose destruction is at least LOUD rather than leaving an unmarked instance behind.

**⚠ There is deliberately no fallback that treats an UNMARKED instance as ours.** That would silently adopt a
DBA's capture instance, which slice 8's `resync` would then be entitled to drop and re-create. Unmarked means
"not ours", full stop — and the gate's load-bearing row is the NULL one, since "the marker is set" would
otherwise pass on a build returning a constant.

### 17.3 ⚠⚠ A DEFAULT enable keys on the TABLE, and that is a CORRECTNESS fix

The guard used to key on the capture INSTANCE, reasoning that a table legitimately has two and refusing the
second would be wrong. True of an explicitly named second instance — still how you ask for one — and fatal
as a DEFAULT: **a bare `cdc.enable` that silently added a second instance would make
`cdc.changes('<that table>')` AMBIGUOUS**, and the reader refuses an ambiguous source rather than picking one
(§2.2 — both instances capture every change in the overlap window, so either answer is wrong).

So the default enable's question is *"is this table captured?"* and the explicit one's is still *"does this
instance exist?"*. It reports what EXISTS rather than what it would have created: with opaque names, a bare
"already captured" would leave the caller unable to name the instance they now have.

⚠ The gate's discriminating row is the **different spelling** (`DBO.CDC_GEN` after `dbo.cdc_gen`): a
name-keyed build computes a different hash and creates a second instance, so that is where the mutant dies.
The plain repeat passes on both builds, because the same spelling hashes the same.

### 17.4 `enable := true` — the DDL is at EXECUTE, so bind stays side-effect-free

**⚠⚠ THE OBJECTION IT DISSOLVES (§15.7).** Earlier analysis concluded the enable had to run at BIND, because
the output schema comes from the change table and the change table does not exist until the enable — which
would make `EXPLAIN`, `DESCRIBE` and `CREATE VIEW` perform DDL. Deriving the declaration from the SOURCE
dissolves it, and it is correct by construction for a fresh enable: a default `sp_cdc_enable_table` captures
every source column, so at the instant we enable, captured == source.

**How the deferred declaration is built without the change table:** describe

```sql
SELECT <the metadata list over LITERALS of the same types>, s.* FROM [dbo].[orders] AS s
```

⚠ **The metadata expressions are written ONCE** (`CdcMetadataSelectList`, parameterised on the operation and
the two LSN expressions) and rendered twice — over `c.[__$operation]` for the real statement and over
`CONVERT(int, 0)` for this one. That is what makes the two declarations the same expression rather than two
that agree today. MEASURED identical: `varchar(16)`, `binary(21)`, `binary(10)`, `binary(10)`, `int`,
`datetime`.

⚠ Every source column is declared NULLABLE on this path. At bind we do not know which columns the enable
will capture — a caller can reach it on a table someone captures PARTIALLY between our bind and our execute —
and a NOT NULL claim we cannot keep is the one direction that becomes a wrong answer. The execute-time
arrival check pins the rest.

**⚠ THE FIRST READ IS ZERO ROWS, NOT AN ERROR — and §15.7's own prediction of this was wrong for a different
reason (§16.4 item 3).** For an instance we did NOT create, a NULL retention floor is genuinely unknowable
and the reader says so. For one created microseconds ago, `start_lsn` is now and "nothing is readable yet" is
a FACT. So the resolution carries a `justCreated` flag and answers empty.

**⚠ IT DOES NOT BACKFILL, and the gate asserts it.** Capture starts at the enable's `start_lsn`, so rows
written before it are invisible. A user who expected a full table gets silence, which is why it is an
assertion rather than a sentence in prose — the initial-snapshot leg is §15.12 item 8.

⚠ It reports `SchemaMayChange` only when the enable actually ran, set in the EAGER part of `Execute` — the
host reads that flag the moment `tablefn_execute` returns, so a DDL placed in the iterator body would happen
with the flag already read as false.

### 17.5 What the gate does NOT cover

- **Cross-process hash determinism** (§17.1) — one process, so the `GetHashCode` mistake specifically is
  invisible.
- **The marker's atomicity.** All three transaction states are MEASURED by hand, but forcing
  `sp_addextendedproperty` to fail from SQL requires a deliberately broken call that the shipped code cannot
  make.
- **A 64-bit hash collision.** Its consequence would be a refusal (`this capture instance already exists`)
  rather than a wrong answer, since SQL Server enforces uniqueness — it is not a correctness boundary.
- **Permissions.** The rig is `sa`; `sp_addextendedproperty` needs `ALTER` on the object, which is
  unexercised.
- **⚠ `enable := true` INSIDE AN EXPLICIT TRANSACTION.** The enable runs on the transaction's PINNED
  connection (read-your-writes) while the change READ runs POOLED (§16.1 — a change reader gains nothing
  from read-your-writes and a streaming reader on the write connection is the 595 hazard). So an uncommitted
  enable creates a TVF the read's connection cannot see. **REASONED unreachable rather than guarded**: the
  capture job cannot scan an uncommitted transaction, so `fn_cdc_get_min_lsn` is NULL for that instance, and
  the window resolution answers "just created ⇒ empty" on the first call and "floor not established ⇒ retry"
  on any later one — neither of which executes the read. The chain is three links long, which is exactly why
  it is written down rather than trusted silently. Committing first makes all of it moot.

---

## 18. Slice 5 — `_capture_instance` AND THE IN-WINDOW DDL CHECK, AS BUILT (2026-08-25)

C#-only, no ABI change. Gate: `verify_mssql_cdc` **239 → 268**, two mutants, each killed at its own
assertion.

```sql
FROM db.cdc.changes('dbo.orders'
      …
      [, on_schema_change := 'error'])   -- 'error' (default) | 'ignore'
```

### 18.1 ⚠⚠ THE SLICE WAS RE-SCOPED, and the reasons are the useful part

§15.12 defined slice 5 as *"name-alignment with widening + `_capture_instance`"*. Only one of those three
was built, and one of slice 6's was pulled forward:

| §15.12 | what happened | why |
|---|---|---|
| `_capture_instance` | **BUILT** | an output column added LATER breaks every `INSERT INTO staging SELECT * FROM cdc.changes(…)` in existence. The reader was two days old; this was the cheapest moment it will ever be |
| name-alignment + NULL-fill | **DEFERRED to slice 7** | with ONE instance it would replace a LOUD failure with a silently all-NULL column — and §15.9's own reasoning says NULL asserts *"this row had no value"* when the truth is *"we never captured it"*. It is decidable only when `_capture_instance` VARIES, which needs two instances |
| `WidenArrowType` | **DEFERRED to slice 7** | no caller. Widening unifies TWO declared schemas at bind; with one describe there is nothing to unify, and building it now would be an untestable helper |
| — | **slice 6's DDL detection PULLED FORWARD** | cheap, independent, and it delivers the signal slice 5 was meant to make decidable: *the shape I declared may not describe this whole window* |

**The transferable point: an "infrastructure" slice whose only visible behaviour is a REGRESSION is not
ready to be built.** NULL-filling here would have been strictly worse than the failure it replaced, and the
slice list could not see that because it was written before the reader existed to make the failure loud.

### 18.2 `_capture_instance`

Emitted on EVERY row, always, between `_operation` and the optional `_commit_timestamp`. It is the instance
name as a constant, passed as a PARAMETER rather than spliced (it is a `sysname` from server metadata, so
splicing would be safe in practice and wrong in principle, and the parameter costs nothing).

⚠ With one instance it is constant and decides nothing — its job starts at slice 7. It ships now purely
because adding an output column is a breaking change and this was the cheapest moment.

### 18.3 The in-window DDL check

At execute, after the window is resolved and BEFORE any row is read, `cdc.ddl_history` is asked whether a
DDL landed in `(from, to]`. If one did, the read is REFUSED, naming the count, the first command, its LSN
and whether any of them changed a captured column's TYPE.

**⚠⚠ THE CASE IT EXISTS FOR IS THE SILENT ONE.** MEASURED, all three kinds land in `cdc.ddl_history` with an
`ddl_lsn` directly comparable to the window bounds, and `required_column_update = 1` for exactly one:

```
0x0000002D000006280001  rcu=0  ALTER TABLE dbo.t ADD c INT NULL
0x0000002D00000630000E  rcu=1  ALTER TABLE dbo.t ALTER COLUMN a NVARCHAR(200)
0x0000002D000006380011  rcu=0  ALTER TABLE dbo.t DROP COLUMN b
```

- **ADD COLUMN** — NOT captured by this instance, so the read simply OMITS it. **A pipeline loses a field
  and nothing fails.** This is the one that justifies a default of `error`.
- **ALTER COLUMN &lt;type&gt;** — propagated to the change table ASYNCHRONOUSLY (§15.6), so the type declared
  at bind may already be stale. §16's arrival check catches it once the capture job has acted; this catches
  it before.
- **DROP COLUMN** — the column stays in the change table and reads NULL from that point. The mildest, and
  still a shape change worth naming.

**⚠ THE DEFAULT IS `error`, AND IT COSTS A ROUND TRIP.** Loud before clever (§15.11): a window containing a
DDL is uncommon for a polling consumer and normal for a first read over a long retention window, and silence
is the worse failure. `'ignore'` reads anyway and buys the round trip back.

**⚠ IT CANNOT SHARE THE WINDOW-RESOLUTION BATCH, and that is forced rather than lazy.** That batch has to
survive CDC being DISABLED between bind and execute — it carries the guard for exactly that — and a batch
REFERENCING `cdc.ddl_history` fails at COMPILE when the schema is gone, turning a precise message into
`Invalid object name`. Same fact the suite's §0 teardown is built around.

**⚠ THE RANGE IS THE POINT, AND A MUTANT PROVES IT.** Dropping the `ddl_lsn > @from AND ddl_lsn <= @to`
predicate — checking the table's whole DDL history instead — leaves the suite failing at *"a read that
STARTS after the DDL is clean again"*. Without the window scope the check would read as "this table is
poisoned forever", which no consumer could act on.

### 18.4 What the gate does NOT cover

- **The round-trip cost.** Nothing measures it; it is one small metadata query on a read that already makes
  two.
- **A DDL landing between the check and the read.** The check is a snapshot, not a lock — a DDL committed in
  that microsecond window is missed, and §16's arrival check is what stands behind it for the type case.
- **`resync` / `fill` / `null`.** Refused BY NAME so a reader of §15.9 is told which slice they are waiting
  for rather than that their spelling is wrong.

---

## 19. Slice 7 — THE TWO-INSTANCE BOUNDARY, AS BUILT (2026-08-25)

C#-only, no ABI change. Gate: `verify_mssql_cdc` **268 → 351**, five mutants, each killed at its own
assertion; 3/3 green re-runs. This is the last slice §15.12 scheduled before the snapshot leg, and it also **retires both items
§18.1 deferred out of slice 5** — the name-alignment (BUILT, below) and `WidenArrowType` (**DISSOLVED**, §19.2).

```sql
-- a table with two capture instances now reads as ONE stream, with no new syntax
FROM db.cdc.changes('dbo.orders')
-- …and naming one still reads that one alone, which is the escape for what the union cannot represent
FROM db.cdc.changes('dbo.orders', capture_instance := 'dbo_orders')
```

### 19.1 The boundary is DERIVED, and from the floor rather than from `start_lsn`

§2.2 measured that `cdc.change_tables.end_lsn` is **NULL for both instances**, so the older one's stop
position exists nowhere and must be computed. Re-confirmed 2026-08-25 on a fresh two-instance probe, along
with the double capture that makes it matter: one INSERT produced a row in **both** change tables.

The split is **`sys.fn_cdc_get_min_lsn(<newer instance>)`**, and choosing that over
`cdc.change_tables.start_lsn` is what makes it correct under cleanup rather than merely correct today. The
two are EQUAL for a fresh instance (measured: both `0x0000002D00000B30003E`), and they diverge once the
cleanup job runs — it RAISES the floor as it purges, and the purged range is exactly the range the newer
instance can no longer answer for, while the older instance still covers it. Splitting on `start_lsn` would
hand that range to a leg that no longer holds it: a **short read**, silent.

- **Older leg** reads strictly below the split, **newer leg** at or above it. Every LSN is covered exactly
  once, so double-counting is unrepresentable rather than merely avoided.
- The instances are ordered by `start_lsn`, tie-broken by `create_date` then name. `start_lsn` is the
  semantic discriminator (a second instance's `start_lsn` IS the boundary); the tie-break is real, because
  cleanup raises BOTH floors and they converge.
- ⚠ **Getting the order backwards is not a cosmetic bug**: the newer instance does not hold the
  pre-boundary rows, so a swapped pair returns a SHORT result rather than a wrong-looking one. Mutant B.

### 19.2 ⚠⚠ The union is in T-SQL — and that DISSOLVED `WidenArrowType`, whose stated rule was WRONG

§15.8 weighed two shapes — "align in DuckDB SQL and get the widening free" versus "align in C# and widen
ourselves" — and recommended the second, because §15.1 puts the reader in C#. **There is a third it did not
consider: we already GENERATE T-SQL**, so the alignment can happen on the server, where SQL Server's own
data-type precedence does the widening. One statement, one describe, one stream, no C#-side batch surgery.

**And the helper §15.8 specified would have been wrong.** MEASURED 2026-08-25:

```
decimal(9,2) ∪ decimal(18,4)  ->  decimal(18,4)     -- as §15.8 expected
decimal(9,0) ∪ decimal(5,4)   ->  decimal(13,4)     -- NOT the decimal(9,4) its rule gives
```

Thirteen is `max(integral digits) + max(scale)`. §15.8's "max precision, max scale" yields `decimal(9,4)`,
which holds five integral digits — so a nine-integral-digit value from the first branch **OVERFLOWS**. The
helper would have silently lost data on a shape SQL Server gets right for free.

**⚠⚠ AND IT HAS NO REACHABLE CASE AT ALL, which is a stronger statement than "no caller yet".** Two
instances of one table can differ on a column's type in exactly one way, and it is not a widening:

| how the two could differ | what actually happens |
|---|---|
| an `ALTER COLUMN <type>` between the enables | **they CONVERGE** — MEASURED, the capture job propagates it to BOTH change tables (~2 s on the rig), so `decimal(9,2)`/`decimal(9,2)` became `decimal(18,4)`/`decimal(18,4)` |
| a column captured by only ONE instance | nothing to unify — it is NULL-filled from the other leg |
| a column DROPPED and RE-ADDED with a different type (§15.11) | a genuine **CONFLICT**, not a widening — refused, §19.5 |

⇒ the transient window while the capture job propagates is the only moment the two legs' types differ, both
are the same TYPE NAME, and SQL Server's union widens to the wider one. Correct, for free, and there is
nothing left for a helper to do.

**⚠ One silent case is accepted and said out loud.** Where SQL Server's rules cannot represent the union of
two decimals within 38 digits it TRUNCATES the scale. That is the same answer any T-SQL user gets from a
`UNION ALL`, which is what makes it defensible; a hand-rolled rule would have been ours to get wrong, and
the measurement above says which way that goes.

### 19.3 Alignment by NAME, and the bare `NULL` that removes all the type rendering

**⚠⚠ A BARE `NULL` IN ONE UNION BRANCH TAKES THE OTHER BRANCH'S TYPE — MEASURED**, and it is the finding
that keeps this path free of SQL type-name rendering entirely:

```
SELECT CAST('hello' AS varchar(50)) AS b  UNION ALL  SELECT NULL AS b     ->  b is varchar(50)
```

…not `int`, which is what a bare `SELECT NULL` gives on its own and which would have made the *other* branch
fail to convert. So a column only one instance captures is filled with the literal `NULL` and still arrives
correctly typed. Without this, the leg builder would have needed the full `sys.types` → `varchar(n)` /
`decimal(p,s)` / `datetime2(n)` rendering, with a quoting problem at the end of it.

- **COLUMN ORDER: the newer instance's captured columns first, then columns only the older one has.** The
  newer set is the table's current shape and the one a consumer keeps seeing once the older instance is
  gone, so `SELECT *` is stable in the direction that matters; a dropped column is history and is appended.
- **The captured sets come from `cdc.captured_columns`, not from parsing `captured_column_list`.** The help
  proc already returns `[id], [v], [extra]` — a string that escapes a `]` in a column name by doubling it,
  so parsing it is a quoting problem with a silent wrong answer at the end. ⚠ And `column_ordinal` is the
  CHANGE TABLE's order, which is what the TVF returns after its four metadata columns — not the source
  table's `column_id`.
- **A column captured by only ONE instance is declared NULLABLE whatever `sys.columns` says.** It is
  NULL-filled for the other leg by construction, so a NOT NULL claim would be one the result violates on
  every pre-boundary row.
- **⚠⚠ AND THE NULL IS DECIDABLE ONLY BECAUSE `_capture_instance` SHIPPED IN SLICE 5.** `region` NULL on a
  span_v1 row means *"that instance never captured the column"*; `region` NULL on a span_v2 row means the
  value really is NULL. Same NULL, two meanings — and without the instance column §7's *refuse* would still
  be the only honest answer. This is the slice §18.2 was buying.

### 19.4 One statement, built at BIND, for every position of the window

The window is resolved at EXECUTE (§15.7) but the statement is composed at BIND, so **both legs are always
in it** — including when the caller's whole window sits on one side of the boundary. The TVF answers an
inverted window with the unattributable 313 (§2.1), so a leg with nothing to read cannot simply be handed a
backwards range. Two mechanisms, deliberately redundant:

| | |
|---|---|
| **clamped TVF arguments** | older leg `(from, max(from, min(to, split)))`, newer leg `(max(from, split), max(that, to))`. Always a legal window; the clamp can only ever WIDEN a degenerate leg |
| **explicit predicates** | older `< @split`; newer `>= @split AND <= @win_to`. These are the partition, and they hold whatever the arguments are |

MEASURED both degenerate directions through the built reader: a window entirely below the boundary returns
only older-instance rows (the newer leg is handed `(split, split)` and its rows are removed by `<= @win_to`),
and a window at or above it returns only newer-instance rows.

**⚠ The redundancy is why the gate can only kill the removal of BOTH.** In the fixture each half covers the
other, so mutating either alone survives; removing both produces **"Expected 4 rows, but got 6"** — the
double count itself. The redundancy is deliberate: the failure it guards is a silent wrong answer, and belt
and braces is the right posture for one.

### 19.5 A DDL at or below the boundary is ABSORBED

§18.3's check refuses a read whose window contains a DDL. On a two-instance read its lower bound becomes
`max(from, split)`: **the second capture instance exists BECAUSE of the DDL that motivated it**, so the union
already carries both shapes and `_capture_instance` tells the eras apart. Refusing there would refuse exactly
the window this slice was built to serve.

MEASURED, on one table: the `ADD region` and `DROP note` that motivated the second instance land BELOW its
`start_lsn`; an `ADD extra` issued afterwards lands ABOVE it and still refuses. Both halves are gated, and
the refusal's message now says which side of the boundary it is talking about — on a two-instance read the
obvious reading of *"a schema change landed inside this window"* is the one that produced the second
instance, i.e. precisely the one NOT being reported.

**⚠ `cdc.ddl_history` holds ONE ROW PER (DDL × capture instance)** — MEASURED, with two instances every DDL
appears twice, INCLUDING DDLs that predate the newer instance, which SQL Server back-fills onto it. Counting
rows would report *"2 schema changes"* for one ALTER, so the query is `DISTINCT`. ⚠ And the command is taken
with `TOP 1 ORDER BY ddl_lsn` rather than `MIN(ddl_command)`: MIN picks the alphabetically first statement,
which need not be the one at `MIN(ddl_lsn)` — harmless while a window held one DDL, and wrong by construction
for the windows this slice creates.

**⚠ `cdc.ddl_history` IS POPULATED ASYNCHRONOUSLY TOO**, and it lags the change rows: an `ADD` was still
absent from it seconds after the DML that followed it had been captured. A gate for the unabsorbed case must
wait for the ddl_history ROW, not for a row count, or it passes for the wrong reason. That wait doubles as
the watermark guarantee — the job cannot have recorded the DDL without scanning past it.

### 19.6 A genuine type conflict is REFUSED at bind, and it costs nothing

Per §19.2 the only way two instances disagree on a type is a drop-and-re-add. MEASURED end to end: a column
dropped as `varchar(20)` and re-added as `int` really does leave the older instance holding `varchar(20)`,
the union describes it as `int` by precedence, and the read dies mid-scan with

```
Conversion failed when converting the varchar value 'text-value' to data type int.
```

**⚠ THAT IS NOT GOOD ENOUGH, AND THE REASON IS THE SILENT HALF: the conversion error fires on an
unconvertible VALUE, not on the conflict.** A column whose historical text happens to be numeric converts
quietly, and the two eras silently stop meaning the same thing. So the conflict is caught at BIND by
comparing the captured type NAMES, which came back with the column names at no extra cost. A difference
WITHIN one type name (`varchar(20)` vs `varchar(50)`, `decimal(9,2)` vs `decimal(18,4)`) is a widening SQL
Server performs correctly and is deliberately allowed through.

### 19.7 ⚠⚠ `fn_cdc_get_min_lsn` answers ZERO, not NULL, for an instance it does not know

MEASURED 2026-08-25, and it is a trap with two different consequences:

```
sys.fn_cdc_get_min_lsn(NULL)                ->  0x0000000000000000000
sys.fn_cdc_get_min_lsn('no_such_instance')  ->  0x0000000000000000000
sys.fn_cdc_get_min_lsn('t_v1')              ->  0x0000002C00000C980040
```

Zero is a well-formed LSN that compares BELOW every real one:

- **as a FLOOR**, it passes the §2.1 retention pre-check trivially and hands the window to the TVF — the
  misleading 313 again, for a capture instance that was DISABLED between bind and execute. Guarded now on
  BOTH paths, one-instance included; it is a small pre-existing hole this slice's measurement exposed.
- **as a SPLIT it is far worse**: every row would fall in the newer leg, which does not hold the
  pre-boundary changes, so the read comes back SHORT with nothing failing.

It is distinguishable from the genuinely transient NULL floor of §1.6a, so the two get different answers —
"retry" for NULL, "SQL Server no longer knows this instance" for zero. ⚠ It is also why the window batch
APPENDS its split column rather than always selecting one: passing NULL for a second instance that does not
exist would read back zero rather than NULL, i.e. a value that must be ignored on pain of silent data loss.

**⚠ The transient NULL is a real, user-visible window**: for up to one polling interval after a second
instance is enabled, a two-instance read answers *"the boundary … is not established yet — retry"* rather
than rows. Substituting `cdc.change_tables.start_lsn` there would work only until the cleanup job has run
(§19.1), so the refusal stands. The gate carries floor waits for exactly this, and one assertion is made
through `DESCRIBE` — which BINDS without executing — for the same reason.

### 19.8 `cdc.min_position('<table>')` answers again

It refused a source matching two instances, and rightly so while `cdc.changes` refused the same source:
*"the floor of what?"* had no answer. Now it does — the union's readable range starts at the OLDER
instance's floor — so it reports the MINIMUM of the two. ⚠ **Unknown wins over min**: if either floor is
NULL the answer is NULL, because reporting the other would ASSERT a lower bound above the true one, which is
the substitution `CdcMinLsn`'s own remarks refuse to make for one instance. The mixed case (a string
matching both as an instance name and as a table name) still refuses — that is an ambiguous QUESTION, not a
boundary.

### 19.9 What the gate does NOT cover, said rather than implied

- **The cleanup-correctness argument for splitting on the floor rather than `start_lsn`** (§19.1). Producing
  it needs the cleanup job to purge, which needs a retention horizon to pass; the two values are equal in
  any suite that can run in a minute, so the mutant survives. REASONED from `fn_cdc_get_min_lsn`'s
  documented behaviour and from the measurement that the two agree before cleanup.
- **The zero-LSN guards** (§19.7). Reaching them needs a capture instance to vanish between one statement's
  bind and its execute, which no suite can arrange.
- **The boundary-above-the-watermark guard.** Found by walking the clamp rather than by a failure: the newer
  leg's TVF accepts only a window inside `[split, max_lsn]`, so when the boundary sits ABOVE the watermark
  there is no legal call to make and every clamp that keeps the legs partitioned produces an inverted or
  below-floor one — the 313 again. §1.6a says the floor is NULL in exactly that window, so the retry branch
  fires first and this is REASONED unreachable. It is guarded anyway: "reasoned unreachable" is the argument
  that has already been wrong twice in this feature, and the cost of being wrong here is precisely the
  unattributable message the pre-check exists to prevent.
- **Either half of the partition alone** (§19.4) — each covers the other in a fixture with no row at exactly
  the split LSN, and no statement can place one there.
- **A third capture instance.** SQL Server caps a table at two (Msg 22962), so the >2 branch is reachable
  only through a source string matching both as an instance name and as a table name.
- **The decimal widening itself.** §19.2 establishes it has no reachable case between two instances, so
  there is nothing to assert; what IS asserted is that the aligned column keeps its own type.
