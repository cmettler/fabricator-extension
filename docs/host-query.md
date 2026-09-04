# Host query — reuse the host's DuckDB engine from C#, over Arrow

> Status: **design + implementation in progress.** Lets a managed component (a provider backend, a custom
> function) run a DuckDB query/statement **on the host's own engine** and exchange data as Apache Arrow —
> reusing DuckDB features (functions, readers, extensions, the catalog) without re-implementing them, and
> without going out-of-process. This is the C#→host reverse-callback companion to the v40 filesystem bridge
> (`FabricatorHostServices`); see [docs/filesystem-bridge.md](filesystem-bridge.md).

## Why not ADBC / a second DuckDB

We are **already inside DuckDB** — the extension holds a first-class C++ `Connection`/`DatabaseInstance`.
Loading DuckDB's native ADBC driver in C# would open a *separate* database/connection (its own transaction,
its own locks) and binding our connection into it requires poking the driver's internal connection wrapper +
disabling its release — fragile and version-coupled (analysed and rejected). A reverse callback that runs on
the host's engine is simpler, has zero coupling to ADBC internals, and lets **C++ own the connection/transaction
binding**.

## The non-negotiable: a FRESH connection per call

`host_query` runs on a **new `Connection` over the host `DatabaseInstance`**, never the in-flight
`ClientContext`. A `ClientContext` is **not reentrant** — it owns one active transaction, one in-flight
query/result (executor pipeline, arena allocators, bound expression context) and a context lock held during
execution. A `host_query` is almost always invoked *from inside* an executing query (a C# scalar/table/in-out
callback), so reusing that context would **deadlock on the context lock or corrupt the outer query's state**.
A fresh `Connection` gets its own `ClientContext` (own transaction, own executor state), sharing only the
`DatabaseInstance` at the storage/catalog layer — which is built for concurrent connections (MVCC).

Consequences (accepted): the query runs in a **separate transaction** → it sees *committed* data, not the
extension's own uncommitted writes. (Same-transaction read-your-writes would mean reusing the live context =
the corruption path; explicitly out of scope.) Each call is naturally **thread-safe** (own connection, no
shared mutable state); a small connection pool is a later optimization (create-per-call first).

### A host query DOES see attached catalogs — including our own (measured 2026-07-30)

`ATTACH` registers with the `DatabaseManager`, which lives on the `DatabaseInstance` and is therefore shared
by the fresh connection. So every attached catalog is reachable from inside a host query, **fabricator ones
included** — a host query can scan a Delta or SQL Server table through our own provider. What the fresh
connection does *not* inherit is the search path (see the next section); the catalog itself was always
visible, which is why the pre-fix error read *"Table with name t does not exist! Did you mean `lake.t`?"* —
the hint could only come from a catalog the fresh connection could see.

Measured, with `lake` = a Delta catalog holding `t` with `sum(a) = 3`:

| probe | result |
|---|---|
| `fabricator_host_query('SELECT sum(a) FROM lake.main.t')` | `3` — scans through the provider |
| `… duckdb_databases() WHERE database_name='lake'` | the attachment is listed |
| inside an outer `BEGIN` + uncommitted `INSERT … VALUES (100)` | outer session `103`, **host query `3`** |
| after `COMMIT` | `103` |

**Row 3 is the one that surprises people, and it is worth stating separately from the abstract rule above.**
"No read-your-writes" sounds academic for plain DuckDB tables; seen through a fabricator catalog it looks like
a bug: you INSERT, your own session reports 103, and a host query on the next line reports 3. It is not a bug
— our Delta transaction buffer is keyed by transaction id, so a *different* transaction structurally cannot
see the buffered actions. It is the fresh-connection rule and the buffered-DML design meeting, and it follows
from the decision that makes host queries safe at all.

**Re-entrancy into our own provider works, by machinery that already exists.** The fresh connection has its
own `MetaTransaction`, so `global_transaction_id` differs and `set_active_txn` routes the nested scan to a
**separate provider connection** — the same per-transaction-connection mechanism that makes `dbt --threads 4`
work (docs/transaction-concurrency.md). Verified working in the table above.

**⚠ A SQL-Server self-deadlock is possible in principle — PREDICTED, NOT OBSERVED.** Stated at that
confidence deliberately. CLAUDE.md records that an uncommitted `ALTER`'s Sch-M lock blocks a *pooled*
connection's read with `LCK_M_IS` until a 30 s timeout (found via `sys.dm_os_waiting_tasks` during the dbt
incremental work). Compose that with re-entrancy: a host query issued *inside* a transaction that has already
done DDL on a SQL Server table takes a **separate** provider connection, which can block on the outer
transaction's own lock — and that lock cannot release, because the outer transaction is blocked waiting for
the host query to return. That is a lock-wait cycle. Reproducing it needs SQL Server plus in-flight DDL and
has not been attempted, so treat it as a hazard derived from a documented mechanism rather than a finding.
The Delta path has no equivalent (it takes no server-side locks) and degrades to the invisible-writes
behaviour above instead of blocking.

Deliberately **not guarded in code**: a guard would have to answer "am I inside a transaction that has
touched this catalog", which is substantial machinery for a hazard nobody has hit, and it would also forbid
the legitimate read-only re-entrancy that the table above shows working.

## ✅ PINNED CONNECTIONS — several calls on ONE connection (ABI v84, 2026-09-03)

User-asked, in the shape the request gave: `con = host.connection()` / `con.query()` / `con.dispose()`,
*"so a exec() could create a temporary table on this connection which could be queried in the same render
session"*. Full ABI record: [abi-history.md](abi-history.md) §v84.

> **⚠ SINCE 2026-09-04 `IHostConnection` HAS A THIRD MEMBER: `Publish(sql)`** — it registers the statement
> as a named Arrow source the hosting DuckDB can scan, returning an opaque token, and runs it LAZILY when
> that scan pulls. It exists because a relation staged in this connection's temporary catalog has **no other
> way** to reach the caller's statement: a temp table is invisible to every other connection, and a real
> table created during `bind_replace` is invisible to the statement being bound. DEFAULT-implemented, so the
> contract gained a member without breaking a plugin.
>
> ⚠⚠ **AND SINCE 2026-09-04 `Query` TAKES `batchRows` (ABI v85)** — how many rows to accumulate into each
> exported Arrow batch, 0 keeping the historical default of one DuckDB `DataChunk` (2048 rows). Ask for a
> big batch when the rows become SCAN MORSELS (measured ~2.4x on 100M rows; a publication asks for 122880)
> and leave it at 0 when they become FILES, because engineered-wood writes one parquet file per input batch
> — which is why this cannot be a better default and has to be the caller's call. Full record:
> [abi-history.md](abi-history.md) §v85.
>
> ⚠⚠ **IT IS WHY THIS CLASS IS REFERENCE-COUNTED.** A publication must be able to ISSUE its query after the
> render that opened the connection has finished, so an unscanned publication holds the handle open. It only
> has to survive until `Query` RETURNS — from there the stream holds its own reference, per the Dispose
> remark below — so the reference is given back the moment the stream exists. ⚠ Consequence of the
> single-live-stream rule below: **only ONE publication of a connection can be scanned at a time**, and two
> scanned together fail loudly rather than silently. Full record:
> [fluid-templating.md](fluid-templating.md) §18.9.

**It does not weaken the section above.** A pinned connection is still NOT the in-flight `ClientContext` —
it is a connection of its own, opened on the host `DatabaseInstance`, and every reason "a FRESH connection
per call" gives still applies to it. What changes is only its LIFETIME: instead of dying with the call it
lives until the caller disposes it, so the caller's own statements share one transaction context, one set
of session settings and — the point — one **TEMPORARY catalog**.

### What it buys, measured before it was built

| | |
|---|---|
| a TEMP table read back on the SAME connection | **works** |
| the same table from another connection | `Catalog Error: Table with name t does not exist!` |
| a `SET` on one connection | persists there; another connection reports its own value |
| a TEMP table inside `BEGIN … ROLLBACK` | **gone** — temp tables are transactional |

⇒ this is not "a faster fresh connection". A multi-step managed job — stage, then read — is
INEXPRESSIBLE on fresh connections when the intermediate is a temp table, whatever the timing. And a temp
table is the right intermediate: it needs no name in the shared catalog, nothing else can see it, and
closing the connection destroys it.

### ⚠⚠ ONE result stream at a time, and the host refuses rather than truncating

`ClientContext::InitialCleanup` — called by every query path, comment *"Cleanup any open results"* —
closes the connection's active streaming result. MEASURED, it does so **SILENTLY**:

```
r1 = conn.execute("SELECT i FROM big ORDER BY i");  r1.fetchmany(3)  -> [(0,), (1,), (2,)]
conn.execute("SELECT 1").fetchall()                  # second query, SAME connection
r1.fetchmany(3)                                      -> []           # the rest is GONE, no error
   the same pair across two DIFFERENT connections    -> [(3,), (4,), (5,)]
```

So a second statement on a pinned connection with a live result stream is **REFUSED**, naming the cause.
Release (or fully read) each stream first. `HostConnection::open_streams` counts them; the slot is taken
only once the statement has SUCCEEDED, and released after the result is dropped.

⚠ Unreachable from the Fluid plugin, which reads every result eagerly and disposes it before returning —
so the refusal carries no test. It exists for a plugin author holding a stream, which is a sequencing
mistake whose alternative is a silent short read.

### ⚠ Refused: named Arrow inputs — and the stated reason was MEASURED FALSE the next day

As shipped, the refusal reads: *"`duckdb_arrow_scan` registers a CONNECTION-scoped view … on a pinned one it
would outlive it and the next call using that name would collide."* **Both halves are wrong** (measured
2026-09-03, see §Named Arrow inputs are TEMPORARY views below): that view was neither connection-scoped nor
collision-prone — it was a CATALOG view created with `replace: true`, so a re-registration REPLACES rather
than colliding, and it outlived not just the call but the connection.

⇒ **The refusal is now LIFTABLE and has not yet been lifted.** Since the inputs are TEMPORARY views, a
pinned connection is exactly the right scope for one: the view lives as long as the pin, dies with it,
is invisible to every other connection, and re-registering the same name replaces it. That is the
prerequisite [fluid-templating.md](fluid-templating.md) §17 needs for `{% query t %}` …
`{% query u %}SELECT … FROM t{% endquery %}`. Lifting it is its own change: the refusal is currently the
only thing keeping a caller from binding an input whose STREAM it might release before the pin closes,
so the ownership rule (`OwnedArrowInputs` is owned by the RESULT STREAM, not by the connection) has to be
re-examined for a view that now outlives the result stream.

### ⚠ The session is applied at OPEN, and that has a consequence worth knowing

v83's optional `client_context` is read ONCE, when the connection is opened — because re-applying it per
query would undo a `SET` the caller performed THROUGH the pin. It follows that **a pinned connection
outliving its logical unit of work hands every later user the FIRST one's session**. MEASURED via a mutant
that shared one connection process-wide: a render under `Asia/Kolkata` reported the zone the first render
had seen. That is a wrong VALUE, not merely stale scratch state, and it is the sharpest reason the Fluid
engine scopes its connection to ONE RENDER.

### It does NOT join the caller's transaction

Unchanged, in both directions: a pinned connection reads COMMITTED state, so the surrounding statement's
uncommitted rows are invisible to it, and what it writes is invisible to the surrounding statement (whose
snapshot predates the commit — fluid-templating.md §11.1b). What changed is only that the CALLER's own
earlier statements are visible to its later ones.

### The surface

- C++: `host_query`'s new `connection` parameter (0 = fresh), `host_connection_open` /
  `host_connection_close`; `MakeHostQueryStream` takes an optional `shared_ptr<HostConnection>`.
- Bridge: `Host.OpenConnection(clientSession)` → `Host.HostConnection : IDisposable`
  (`Query` / `ExecuteNonQuery` / `Dispose`), plus `Host.CanPinConnection`.
- Plugin contract: `IHostQuery.OpenConnection(inheritSession)` → `IHostConnection : IDisposable`.
  DEFAULT-implemented, so a published contract gained a member without breaking a plugin — the default is
  for an IMPLEMENTER (a test double), never for an old host, which cannot reach managed code at all.
  **⚠ There is deliberately NO capability probe** (user, 2026-09-03: *"we don't need any fallbacks with
  CanPinConnection"*): a probe exists only so a caller can DEGRADE, and a degraded pinned connection means
  statements quietly stop sharing — a different answer, not a slower one. Contrast `Host.CanQuery`, which
  stays and has a dozen callers, because falling back to a provider's own reader is still CORRECT.
- First consumer: the Fluid plugin's per-render session — see
  [fluid-templating.md](fluid-templating.md) §12.

## ✅ Named Arrow inputs are TEMPORARY views now — a SHIPPED DEFECT, measured and fixed (2026-09-03)

A named Arrow input was registered with DuckDB's C-API `duckdb_arrow_scan`, which ends in
`CreateView(table_name, replace: true, temporary: false)` (`duckdb/src/main/capi/arrow-c.cpp:425`). So every
bound input became an ordinary **CATALOG view in the user's own `memory.main`** — visible to every other
connection, and outliving the connection that made it *and the stream whose raw pointer it stores*.

`MakeHostQueryStream` now registers them itself (`RegisterArrowInputView`), which is that same upstream code
with `temporary` flipped to `true`. Nothing else changed.

### The measurement, with its positive controls

Two probes, each with a control that proves the code path ran — because §17.9 had recorded three earlier
probes that all *looked* like passes and had never reached a view-creating path at all.

| probe | control that the path ran | before | after |
|---|---|---|---|
| `dbo.cf_host_sum(1)` — binds `in0`, sums it on the host | returns **10**, which can only come from `in0` having been registered AND scanned | `in0` present in `memory.main`, `temporary = false` | **absent** |
| codec Delta scan, `pushdown_filters 'all'`, three filtered `SELECT`s | Debug `mode=Exact native_filter="g"=1/2/3` — the `HostBatchFilter` path, three times | **three** views `__fabricator_scan_batch_1..3` | **none** |

Row counts were identical throughout (715 / 714 / 714), which is the behaviour-neutrality claim.

### ⚠⚠ It was not untidiness — it was a SIGSEGV reachable from ordinary SQL

The view holds a raw `ArrowArrayStream *` that `OwnedArrowInputs` releases when the result stream is
released. Once the query is done, the view points at freed memory — and it is still in the catalog, under a
name anyone can type:

```sql
SELECT dbo.cf_host_sum(1);        -- 10
SELECT * FROM in0;                 -- Segmentation fault, exit 139
```

Measured on both the demo name (`in0`) and a production one (`__fabricator_scan_batch_1`). And it
**accumulated**: one view per statement, unbounded, for the process's life.

⚠ **A second hazard goes with it, and it is v83's doing.** The fresh connection inherits the CALLER's
search path, so a catalog view named by a managed caller landed in **whatever schema the user was working
in**, under a name the user never chose. A temporary view lands in `temp.main` whatever the search path —
which also makes the input name resolve identically regardless of session state, since `temp` is always
searched first.

### ⚠ Reachability was narrower than the mechanism — and that is why it survived

Establish it per path rather than assuming. The one prediction made before measuring was WRONG, and it was
the one that would have made the defect far more serious — I expected the buffered-overlay branch to leak
under the DEFAULT `PROVIDER 'delta'`, i.e. on the configuration almost everyone runs. It does not.

- **`HostBatchFilter`** (codec Delta + exact pushdown) leaks — measured. It is the one production path that
  calls `BoundInput.NextName` and never calls `Drop`.
- **`cf_host_sum`** leaks — a demo function using the FIXED name `in0` with no drop.
- **The default `PROVIDER 'delta'`** does NOT — measured with the same Debug control (`mode=Exact
  native_filter="g"=3` present, zero views left). Under `native_read` the filter goes into `read_parquet`'s
  own WHERE and no input is bound.
- Every other input site — `HostParquetStaging`, `ExternalTableRouting`, `NativeParquetDataFileWriter`,
  `DeltaCatalog`'s sort input, `DeltaNativeReader` — already worked around it with a unique name plus an
  explicit `DROP VIEW`.

### ✅ THE APPARATUS IT DISSOLVED — retired in the follow-on commit (−171 lines)

Two independent copies of the same workaround existed — `BoundInput.NextName`/`Drop`/`WrapDrop`
(`Fabricator.Bridge`) and `DeltaNativeReader`'s own `NextViewName`/`DropViews` — and both existed ONLY
because the view was catalog-level: unique names avoided a cross-connection collision a temp view cannot
have, and the drops reclaimed a catalog entry a temp view never makes. `BoundInput`'s doc recorded what it
cost to learn — *"six concurrent Delta writers in one process — the `dbt run --threads N` shape — and
**five of the six failed**"*.

Both are gone, across seven call sites in three assemblies: `HostBatchFilter`, `HostParquetStaging`,
`ExternalTableRouting`, `DeltaCatalog`'s sort input, `NativeParquetDataFileWriter` (×2) and
`DeltaNativeReader`'s per-file and batched forms — plus the `BatchPlan.ViewNames` / `BatchQueryOwner` view
plumbing that carried names to the drop. Each site takes a FIXED name again.

**⚠ THE INVARIANT THAT MAKES A FIXED NAME SAFE IS ENFORCED, NOT ASSUMED, and every site says so:** a bound
input is a TEMPORARY view on that call's OWN fresh connection, and named inputs are REFUSED on a pinned
connection — so no two host queries can ever share a temp catalog. **Lifting that refusal is exactly what
would bring the race back**, which is why the note lives at the call sites and not only here.

⚠ `SingleScanArrowStream` STAYS and is untouched. The single-use property is the STREAM's, not the view's,
and it is unchanged — measured the same day at 399 (§the double-scan measurement in
[fluid-templating.md](fluid-templating.md) §17.11).

**⚠ What proves a removal, when there is no new behaviour to assert:** the gate written for the fix got
STRONGER without changing a character. `starts_with(view_name, '__fab') = 0` used to be satisfiable two
ways — temp-ness OR the drops. With the drops gone, only temp-ness can satisfy it.

### ⚠⚠ IT PERSISTED INTO A FILE-BACKED DATABASE, AND THE CRASH SURVIVES A RESTART — measured

The worst case was real, and it is the reason this is worth telling users about rather than only fixing.
Measured on the **pre-fix build**, against a `.duckdb` FILE rather than the in-memory default:

```
$ duckdb probe.db
  SELECT db.dbo.cf_host_sum(1);                     -- 10   (control: the input really was bound)
  SELECT database_name, view_name, temporary
    FROM duckdb_views() WHERE view_name = 'in0';    -- probe | in0 | false     <- the FILE, not memory
$ duckdb probe.db                                   -- fresh process
  SELECT count(*) FROM duckdb_views()
    WHERE view_name = 'in0';                        -- 1     <- it was SERIALIZED
  SELECT * FROM in0;                                -- Segmentation fault, exit 139
```

⇒ a database file written by a pre-fix build can hold a **permanently poisoned view**: it dereferences a
pointer from a process that no longer exists, so it crashes any process that scans it, for ever. The
remediation is a one-liner — `DROP VIEW IF EXISTS <name>` — but a user has to know to run it.

⚠ The exposure is bounded by the reachability above: the fixed name `in0` (from the `cf_host_sum` demo) and
`__fabricator_scan_batch_N` (codec Delta with exact pushdown). Both only reach a file-backed catalog when
the user's DEFAULT database is a file. `SELECT view_name FROM duckdb_views() WHERE view_name = 'in0' OR
starts_with(view_name, '__fab')` finds them.

### ⚠ What is NOT settled

- The root `ArrowSchema` that `RegisterArrowInputView` fills is never released — replicated verbatim from
  upstream, where it is also never released. A per-registration leak of one schema struct. Left alone
  deliberately: the children's release callbacks are the CALLER's and are restored afterwards, so releasing
  the root here risks a double free, and an ownership fix does not belong in a scope fix.

### The gate

`verify_delta_catalog_filter_modes` **39 → 55** (hermetic). No row assertion can see this change, so the
section is built as a pair:

- **the positive control** — an `EXPLAIN` (`<REGEX>:` form; `EXPLAIN` cannot be a subquery source) showing
  that under exact mode the plan carries `Filters: id>495` on the scan and has **no `FILTER` operator**.
  DuckDB has erased the predicate and will not re-apply it, so on the codec path the correct row count is
  evidence that `HostBatchFilter` ran.
- **the assertion** — `SELECT count(*) FROM duckdb_views() WHERE starts_with(view_name, '__fab')` = 0.

⚠ **The first version of that assertion was VACUOUS and passed on both builds.** It was written as
`LIKE '\_\_fab%' ESCAPE ''` and the escape character did not survive into the file (`ESCAPE ''`), so the
pattern matched nothing. Only the mutation test caught it. `starts_with` has no metacharacters and cannot
fail that way. Mutation-tested afterwards: restoring `temporary: false` kills the suite at exactly that
line after 53 assertions pass.


## Session state — the table function inherits, the C# service deliberately does NOT (2026-07-30)

A fresh `ClientContext` starts at DuckDB's **defaults**, which is a second consequence of the above and was
missed until measured. What is and is not carried over splits exactly along DuckDB's global/session line:
**global** settings live on the `DatabaseInstance`, so the fresh connection already sees them (`threads` was
observed identical); **session-local** state does not (search path and `TimeZone` were observed as
`memory.main` and the machine default while the caller had `lake.main` and `America/New_York`).

Untreated, that produced a genuinely surprising failure: `USE lake.main;` then
`SELECT * FROM fabricator_host_query('SELECT count(*) FROM t')` failed with *"Table with name t does not
exist! Did you mean lake.t?"* while the identical SQL worked one line earlier.

**The table function `fabricator_host_query(sql)` now adopts the caller's search path + `TimeZone`.** That
SQL is text the *user* wrote in their session, so unqualified names and timestamp rendering should mean what
they mean there. Two implementation points worth keeping:

- **Captured BY VALUE at bind, never as a `ClientContext *`.** The factory that opens the connection runs
  later and can re-run per execution, so a stored context pointer is the same dangling-pointer bug that
  commit `142b350` removed from the host-FS opener. `HostQueryBind` reads
  `ClientData::catalog_search_path->GetSetPaths()` and `TryGetCurrentSetting("TimeZone")` into a
  `HostQuerySession` the lambda owns.
- **The search path is applied programmatically** (`CatalogSearchPath::Set(entries, SET_DIRECTLY)`), not by
  emitting `USE <ident>` text, which would need identifier quoting to be safe. `TimeZone` cannot be: it is an
  **ICU-registered extension option** (`icu_extension.cpp` `AddExtensionOption`), so there is no core
  `set_local` to call and it goes through a real `SET` — with `Value::ToSQLString()` quoting the literal, and
  a failure treated as non-fatal (a build without ICU has no such option, and refusing to run the caller's
  query over that would be worse).

**The C#-callable `host_query` service passes no session at all, on purpose.** Two independent reasons:
practically, it runs on a managed thread off the global `DatabaseInstance` and there is **no calling
`ClientContext`** to inherit from without new ambient machinery (the way `set_active_opener` supplies one for
host-FS calls). On principle, provider-generated SQL is *code*: making it depend on whatever the user last
`USE`d would be fragile, and the codebase already has the right answer elsewhere — sqlgen functions
(`generate_table_sql`) are handed the ATTACH **alias** explicitly so they can qualify references without
touching session state. Same distinction as a macro body, which binds in the caller's context: correct for
user-written text, wrong for provider-declared text.

That negative is **not asserted in the suite**, and the reason is worth recording rather than leaving as an
apparent gap: it is not observable from SQL, because no provider generates unqualified names in the first
place (`fabricator_delta_scan` builds `read_parquet()` over absolute paths). A test would have to contrive a
caller that does, pinning the contrivance rather than the contract.

## ABI — additions to `FabricatorHostServices` (the reverse-direction struct)

`FabricatorHostServices` already carries host→managed function pointers the managed side calls (the v40
`fs_*` callbacks). We append one primitive:

```c
// Run `sql` on a FRESH host connection (own transaction). `params` (nullable) is a 1-row Arrow batch whose
// columns bind to the statement's parameters (by name when the batch field is named, else positionally).
// `inputs` (nullable) registers named Arrow sources as connection-scoped views BEFORE the query runs (via
// duckdb_arrow_scan), so the SQL can reference them by name (`SELECT * FROM <input_name> …`). `out` receives
// the result as an ArrowArrayStream. The connection + result outlive `out` (released when `out` is released).
int32_t (*host_query)(const char *sql,
                      struct ArrowArray *params /*nullable, 1-row*/,
                      FabricatorHostInputs *inputs /*nullable*/,
                      struct ArrowArrayStream *out, char **err);
```

`FabricatorHostInputs` = `{ int32_t count; const char **names; struct ArrowArrayStream **streams; }` — the
managed caller hands over N named Arrow streams (it owns producing them; the host consumes/releases them when
the query's connection is torn down). Bump `FABRICATOR_ABI_VERSION` **and** the host-services `abi_version`.

**`exec_nonquery` is NOT a separate entry.** A DDL/DML statement returns a result in DuckDB too (DML → a
1-row `BIGINT` count; DDL → empty), so `host_query` subsumes it. "Exec, return affected rows" is a thin **C#
helper over `host_query`** (run, read the single count column if present, discard the stream) — keeping the
ABI minimal. Both get parameter binding for free.

## C++ side (`fabricator_host_query.cpp`, host service + the test/utility table function)

- **`HostQuery(...)`** (the `host_query` callback): `Connection conn(*g_database)` (the `DatabaseInstance`
  captured at extension load, like the fs services); for each input `duckdb_arrow_scan(conn, name, stream)`
  to register it as a connection view; `conn.Prepare(sql)` + bind the `params` row (read via a 1-row
  `ArrowAppender`→`DataChunk`, each value `→ Value` bound positionally/by name); execute; **export the result
  as an `ArrowArrayStream`** by fetching each `DataChunk` and `ArrowAppender`→`ArrowArray`→`ArrowProducer`,
  whose `Stream()` is returned. The `Connection` + materialized batches live in the producer's lifetime
  (released with `out`). Errors → `DupErr` (freed via `free_str`), like the fs callbacks.
- **Capture the `DatabaseInstance` at load** (`InstallHostServices(loader.GetDatabaseInstance())`) into a
  global the callback reads — `host_query` has no per-call opener (unlike the fs callbacks), it just needs the
  database to open a connection on.

## C# side (`Fabricator.Bridge`)

- **`Abi.cs`**: add the `HostQuery` function-pointer field to `FabricatorHostServices` (+ the `FabricatorHostInputs`
  struct).
- **`Host` API** (mirrors `HostFs`): `Host.Query(string sql, RecordBatch? params = null, IReadOnlyDictionary<
  string, IArrowArrayStream>? inputs = null) → IArrowArrayStream` — marshals params to a 1-row Arrow array,
  exports each input stream + the names into an `FabricatorHostInputs`, calls the pointer, imports the result
  stream. `Host.ExecuteNonQuery(sql, params) → long` = the helper (run, read the count, discard).
- This is the surface a provider backend / custom function uses to reuse the host engine.

## Data-in — two layers

1. **Scoped inputs (built in `host_query`):** the caller passes named Arrow streams with the query; the host
   registers them as **TEMPORARY** views for that query only, and they die with the connection. No global
   state, no name collisions, no lifetime ambiguity. **This is the primary data-in path** — the query
   references the input names directly.
   - ⚠⚠ **That sentence described the INTENT and not the behaviour until 2026-09-03**, and it said
     "connection-scoped" while `BoundInput` (`SingleScanArrowStream.cs`) documented the opposite — CATALOG
     views that outlive the connection — *with a measurement attached*. Two docs in one tree, contradicting
     each other, and the wrong one was the one describing the mechanism. See §Named Arrow inputs are
     TEMPORARY views.
2. **Replacement-scan layer (optional, ambient registry):** for "register a C# source by name once, then any
   query referencing that bare name resolves to it" (pandas-df style). A C# registry maps `name → Func<
   IArrowArrayStream>`; a C++ replacement scan registered on the `DBConfig` rewrites an unknown table name to a
   `fabricator_scan('name')` `TableFunctionRef` when the name is registered (a `named_input_exists(name)` +
   `open_named_input(name, out)` managed lookup). `fabricator_scan(name)` is a global table function that opens
   the registered stream and scans it via the existing `arrow_ingest` path. Single-use streams → the registry
   holds a **factory** so each scan gets a fresh stream. This layer is additive over (1) and is only needed for
   the ambient/by-bare-name ergonomics.

## Verification

- A test table function `fabricator_host_query(sql)` (C++) that calls `HostQuery` directly proves the
  fresh-connection run + param-free Arrow export (`SELECT * FROM fabricator_host_query('SELECT 42 AS x')`).
- A C# round-trip test — a custom C# table function whose `Execute` calls `Host.Query(...)` — proves
  SQL → our C# function → `host_query` → fresh host connection → Arrow → back, including the reentrancy safety
  (the nested run is on a fresh connection, so the outer query's context is untouched).
- Data-in: a `host_query` with an input stream that the SQL joins/filters; and (layer 2) a replacement-scan
  test resolving a bare registered name.

## Implementation status

- **Slice 1 — `fabricator_host_query(sql)`** (the C++ engine: fresh connection + self-owning Arrow result via
  the ingest path). DONE, `verify_host_query.test`.
- **Slice 2 — C#-callable `host_query` host service (ABI v42→v43)** + public `Host.Query`/`Host.ExecuteNonQuery`.
  DONE; round-trip verified (`cf_host_answer` in `verify_custom_functions.test`).
- **Slice 3 — named Arrow inputs (data-in)**: `host_query` gained `FabricatorHostInputs` (ABI v43); the host
  registers each C#-provided stream as a connection-scoped view via `duckdb_arrow_scan` before the query.
  `Host.Query(sql, inputs)`. DONE; verified (`cf_host_sum` pushes a C# Arrow table into a host query and sums
  it on the host engine — `verify_custom_functions.test`).
- **Slice 4 — parameter binding (ABI v44)**: `host_query` gained a nullable `params` 1-row Arrow stream; the
  host reads it via `ArrowStreamReader` and binds the columns POSITIONALLY (`?`, `$1`, …) to a prepared
  statement (materialized result so it doesn't outlive the prepared stmt). `Host.Query(sql, parameters)`.
  DONE; verified (`cf_host_param` binds `[40, 2]` into `SELECT (?::BIGINT)+(?::BIGINT)` → 42 —
  `verify_custom_functions.test`). **Ownership note:** the host's `ArrowStreamReader` releases its *copy* of
  the params stream, so the managed caller frees only its allocation (`Marshal.FreeHGlobal`), never
  re-releasing (which would double-free the exporter → NRE).
- **Slice 5 — ambient named-source registry + replacement scan (ABI v45)**: `Host.RegisterSource(name,
  Func<IArrowArrayStream>)` registers a stream factory; two handle-less vtable entries (`open_named_input`,
  `named_input_exists`) let the host resolve a name to a fresh stream. `fabricator_scan('name')` scans it; a
  `DBConfig` **replacement scan** rewrites a bare unresolved name to `fabricator_scan('name')` when it's
  registered (so `FROM <name>` works), declining unknown names so a genuine "table does not exist" is left to
  DuckDB (`NamedInputExists` is non-throwing + bridge-tolerant). DONE; verified (`verify_host_query.test`, 15
  — `fabricator_scan` + bare-name + unknown-name passthrough; built-in demo source `fabricator_demo_numbers`).
- **Slice 6 — streaming results**: `host_query` now uses `SendQuery` (and a streaming prepared `Execute`) so
  the result is fetched lazily (`StreamQueryResult.Fetch()` per `get_next`) — bounded memory for large
  results (validated to 1M rows). The holder keeps the connection (+ the prepared statement for params)
  alive; runtime errors that surface during `Fetch` (vs bind errors at `SendQuery`) are caught in `get_next`
  and reported via `get_last_error`. DONE.
- **Deferred:** parameter binding for the ambient `fabricator_scan` (it resolves a registered source by NAME —
  parameters belong on the scoped `host_query` path, which already binds them; a parameterized named source
  would be a separate, larger design); and the **full breaking rename** (removing the `fabricator_*` names;
  the generic `fabricator_*`/`TYPE fabricator` names already existed as additive aliases at the time; that era's gate `verify_generic_names.test` was deleted by the rename, `2a26b7a`). <!-- check-docs:ignore -->

## Open / deferred

- **Connection pool** for hot `host_query` callers (create-per-call first).
- **Same-transaction** reads — intentionally not supported (would require the live context = corruption).

## ⚠⚠ `fabricator_host_query` USED TO EXECUTE ITS SQL TWICE — found and FIXED 2026-09-01

**MEASURED before the fix**: `SELECT * FROM fabricator_host_query('INSERT INTO audit VALUES (1)')`, called
ONCE, left **2 rows**. DDL doubled too — the second `CREATE TABLE` failed *"already exists"* from a statement
the user issued once. After: **1 row**, and the DDL runs once.

It was the SAME defect fixed for `fabricator_query` in `0acd679` (2026-08-24), and `HostQueryBind`'s own
comment described it:

> *"PopulateReturnSchema runs the factory once for the schema; the scan runs it again for the data — like the
> other fabricator table functions."*

⚠ **That justification was STALE in its load-bearing clause.** "Like the other fabricator table functions"
was true when written; `fabricator_query` was fixed precisely so it no longer does this, so the sibling the
comment appealed to had become the counter-example. **A justification by analogy ages when the analogue is
fixed, and nothing re-checks it.**

⚠ **IT WAS THE SQL FUNCTION ONLY, NOT THE C# `Host.Query`.** Measured separately: a C# caller runs the
statement ONCE (the slice-2 bind probe wrote exactly one row per bind). The doubling came from
`PopulateReturnSchema` running the bind factory to obtain the output schema — i.e. from the TABLE FUNCTION's
bind, which only the SQL surface has.

### The fix — describe instead of execute

`HostQueryBind` now sets `bind_data->schema_factory`, the existing "the bound object can describe itself"
seam, filled from a **PREPARED statement**: DuckDB carries the bound plan's result `types`/`names` without
running it. Cheaper than `fabricator_query`'s fix, which needed the provider to describe remote SQL
(`sp_describe_first_result_set`); here the engine describes its own statements natively.

⚠ **The describe and the execute must BIND IDENTICALLY**, or the declared schema and the delivered batches
could disagree — and the scan reads batches through converters built from the DECLARED schema. So the session
application (search path, TimeZone) was factored into one `ApplyHostQuerySession` that both call, and both
derive the Arrow schema the same way (`ArrowConverter::ToArrowSchema` over the plan's types/names with
`BoundaryClientProperties`).

⚠ **A DOCUMENTED FALLBACK, not a silent one.** `Prepare` handles ONE statement; `SendQuery` accepts several
in a string. Rather than turn a working call into a bind error, the bind falls back to the old path there, so
a multi-statement call keeps its PRE-EXISTING behaviour — double execution included. Pinned in
`verify_host_query` as the value it really produces (two rows from one call).

⚠⚠ **"Still works" is too kind, and the gate says so.** Double execution means a NON-IDEMPOTENT prefix
FAILS: `fabricator_host_query('CREATE TABLE mk …; SELECT * FROM mk')` creates `mk` on the describe run and
then collides with itself on the data run — measured, *"Table with name mk already exists!"*. That is
UNCHANGED from before the fix (everything double-executed then, so this shape failed identically), and it is
also why describing cannot be MADE to work here rather than merely being unimplemented: **the last
statement's schema can depend on the earlier statements' effects**, so there is nothing to describe until
they have run.

⚠ **A behaviour change worth knowing**: a bind/parse error now surfaces at BIND rather than mid-scan. Better,
and the same change `fabricator_query`'s fix made.

⚠ **`fabricator_scan` (`NamedScanBind`) IS NOT THE SAME DEFECT — corrected after actually reading the C#
side, having first been written up here as "the identical shape".** `Host.RegisterSource` registers a
**factory** (`Func<IArrowArrayStream>`), and `PopulateReturnSchema` opens a stream, reads `get_schema` and
releases it **without ever pulling a batch**. So the bind does not execute anything the caller wrote; it
INVOKES THE FACTORY. Whether that costs is entirely the factory's business — lazy ones are nearly free, eager
ones repeat their work, side-effecting ones repeat their effect.

It is fixed below, differently and more cheaply than `host_query`'s was.

Gate: `verify_host_query` 31 → 53 for THIS section (the suite ends the pass at **98** — see the totals at
the foot of this page), mutation-tested: restoring describe-by-execute dies at exactly the `count(*)`
assertion. ⚠ Only a COUNTING assertion can see this — every "the rows are right" test in that file passed
with the defect fully present, which is how it survived.

## `fabricator_host_exec(sql)` — the DDL/DML sibling (added 2026-09-01)

```sql
SELECT * FROM fabricator_host_exec('INSERT INTO t VALUES (1),(2),(3)');   -- affected = 3
```

One `BIGINT` column, `affected`. It exists because of the section above: `fabricator_host_query` must declare
its output columns at BIND, which for arbitrary SQL means asking DuckDB to prepare the statement — impossible
for several statements in one string, and impossible in principle when a later statement's schema depends on
an earlier one's effects. Those fall back to describing by EXECUTING.

**exec has a FIXED output schema, so there is nothing to describe, nothing to prepare, and the statement runs
EXACTLY ONCE whatever it is.** That is the point of the function, not a side benefit — and it is what makes
this work where the sibling cannot:

```sql
-- through host_query: "Table with name he_both already exists!" (the describe run created it)
-- through host_exec:  both statements run, once each
SELECT * FROM fabricator_host_exec('CREATE TABLE t AS SELECT 1 AS c; INSERT INTO t VALUES (2)');
```

- **The count is ASKED OF THE STATEMENT**, via DuckDB's own `StatementReturnType::CHANGED_ROWS`, not inferred
  from column types — so a `SELECT` returning one `BIGINT` does not get its first value mistaken for a count.
- **For several statements it is the LAST one's**, because `SendQuery` returns the last result.
- ⚠ **A CTAS reports 0 though it created rows.** DuckDB does not classify `CREATE` as a row-count statement,
  and 0 also matches `Host.ExecuteNonQuery`'s existing contract ("DML → a 1-row BIGINT Count; DDL → 0"), so
  the SQL surface and the C# one agree.
- ⚠ **The result is NORMALISED, not passed through**: whatever the statement returns is discarded. Without
  that the fixed schema would be a lie — a `SELECT` would declare one BIGINT and deliver something else, and
  the scan reads batches through converters built from the DECLARED schema.
- Same fresh connection and committed-reads semantics as `fabricator_host_query`, asserted side by side so
  the pair cannot drift.

### Both spellings exist

```sql
SELECT * FROM fabricator_host_exec('…');   -- TABLE form: one row, runs once per scan
SELECT fabricator_host_exec('…');          -- SCALAR form, for symmetry with fabricator_exec
```

⚠ **A scalar and a table function SHARE the name**, which is a non-obvious DuckDB fact and is pinned rather
than assumed: they live in different catalog sets, so each resolves in its own syntactic position and neither
shadows the other. Both go through ONE execution path (`RunHostExec`), so the count rule, the session
handling and the error prefix cannot drift between them.

⚠⚠ **PREFER THE TABLE FORM FOR DDL**, and the reason is measured rather than stylistic:

- The scalar is **`VOLATILE`, and that is load-bearing**. Without it DuckDB constant-folds a call over
  constant arguments at PLAN time, so `EXPLAIN SELECT fabricator_host_exec('INSERT …')` would perform the
  insert for a statement that executes nothing. Mutation-tested: dropping the volatility makes exactly that
  assertion fail, 1 instead of 0.
- **What volatile does NOT prevent is PER-ROW evaluation.** In a row context the statement runs once per
  row — measured, `SELECT fabricator_host_exec('INSERT …') FROM range(3)` performs three inserts. The table
  form runs once per scan whatever the cardinality.
- ⚠ Measuring that needs an aggregate that CONSUMES the column: with `count(*)` DuckDB prunes a projected
  column no aggregate reads, so the scalar is evaluated zero times and the measurement asserts the opposite
  of the truth while passing. That trap caught the first version of this very check.

A `NULL` statement yields `NULL`, not 0 — "no statement" and "zero rows affected" are different claims.

## Named sources: the factory is invoked TWICE per bound scan (fixed 2026-09-01)

`Host.RegisterSource`'s doc said the factory *"is invoked per scan to produce a fresh stream."* **That was
wrong, and it is the sentence an author would write an expensive or side-effecting factory against**: it is
invoked once per BIND and once per SCAN, and binds REPEAT — every use of a view over the source, every
`EXECUTE` of a prepared statement.

**The fix is an optional declared schema**, C#-only, no ABI change and no C++ change:

```csharp
Host.RegisterSource("my_source", factory, schema);   // the bind never invokes `factory`
```

`OpenSource` then returns a wrapper that answers the schema from the declaration and opens the real stream on
the first batch pull — which the bind never performs. So the factory runs **exactly once, by the scan**. Use
it whenever producing a stream costs anything: opening a connection, running a query, buffering. Sources that
declare no schema keep the old behaviour.

⚠ **The declaration is VERIFIED on the first pull**, because the host builds its Arrow→DuckDB converters from
the DECLARED schema and reads the delivered batches through them — a mismatch would be read as data rather
than reported. The check covers the column COUNT, the column NAMES and each column's `TypeId`; it does NOT
compare a type's PARAMETERS, so a declared `decimal(18,4)` against a produced `decimal(9,2)` passes.
`IArrowType.Equals` is REFERENCE equality in Apache.Arrow, so real structural comparison needs a hand-written
comparer — `SqlServerCdcReader.SameType` is one, private to another assembly. **Consolidating the two into
the bridge is the right follow-up.**

⚠ **The cost is invisible in ordinary data**, so two instruments ship and the gate reads them rather than
asserting nothing: `fabricator_demo_eager` (no declared schema) and `fabricator_demo_lazy` (declared) each
yield one row — how many times its own factory ran before this invocation. Read twice, the DELTA is the
invocations one bound scan costs. MEASURED: eager `1, 3` (delta 2); lazy `0, 1` (delta 1), and that leading
**0** is the whole claim — the bind never reached the factory. Same mechanism on both sides, so baseline and
fix come from one comparison. Mutation-tested: ignoring the declared schema turns that 0 into a 1.

### ⚠ A LATER DIRECTION, not built: registration by LOOKUP rather than by name

Recorded 2026-09-01 (user): the replacement-scan mechanism will want revisiting, and the shape it likely
needs is **a lookup function plus a schema function** rather than eager per-name registration — i.e. the host
asks "do you have a source called X, and what is its schema" instead of the provider having enumerated every
name up front. That suits a provider whose source set is large or dynamic, which the current dictionary
cannot express at all.

The declared-schema overload above is a step toward it rather than a detour: it already separates *what the
columns are* from *producing the data*, which is precisely the split a lookup+schema pair formalises. Revisit
both together.

## ✅ CLOSED — `Host.Query` can now run AS THE CALLER'S SESSION (ABI v83, 2026-09-02); the record below is what it took

**⚠⚠ BOTH ITEMS ARE FIXED. ABI v83 gives `host_query` an OPTIONAL `client_context`** — 0 for a clean
session, non-zero for the caller's, whose TimeZone and catalog search path are copied onto the fresh
connection. `Host.Query(sql, parameters, inputs, clientSession)` exposes it, and the `IHostQuery` service passes
the ambient for you (it was `HostQueryTransport` until 2026-09-02 — docs/plugin-services.md §8), and item B's INTERNAL error is gone because the replay goes through the `SET`
statement rather than `SET_DIRECTLY`. **MEASURED after the fix: with `SET search_path='myschema'` and
`SET TimeZone='UTC'`, the outer session, `fabricator_host_query` and a template's `query()` all agree.**
Full record, including the two API routes that are BOTH wrong and the mutant that exposed a vacuous
assertion: [abi-history.md](abi-history.md) §v83. Everything below is the record of the gap.

⚠ It does NOT make the query part of the caller's TRANSACTION — the connection is still fresh and still
reads COMMITTED state, so §8.2's visibility rule is unchanged. What is inherited is name and time
RESOLUTION, and only the two settings named; "copy the session" has no principled boundary.

Two separate items, found by probing what slice 3's Fluid `query()` inherits. **Neither is a slice-3
regression** — the first is how `Host.Query` has always behaved, the second predates today's commits.

### A. The C# `Host.Query` inherits nothing, so a template's `query()` runs in a different session

MEASURED with the outer session set:

| path | `SET TimeZone='UTC'` ⇒ it reports | `SET search_path='myschema'` ⇒ `FROM t` |
|---|---|---|
| the outer session | `UTC` | resolves, 1 row |
| `fabricator_host_query(sql)` — the SQL surface | `UTC` ✓ | ⚠ **INTERNAL Error**, see B |
| **`query()` in a template** (C# `Host.Query`) | **`Europe/Berlin`** ✗ | ✗ *"Table with name t does not exist!"* |

**⚠⚠ ABI v82 DID NOT CLOSE THIS, and the distinction is worth stating because the two look alike.** v82
gives a global SCALAR its caller's context — the host-FS opener, the provider-settings session and the
transaction id — with a restore (abi-history.md §v82). Those are OUR ambients. `Host.Query` opens its own
connection on the captured `DatabaseInstance`, so **DuckDB's own session settings are untouched by it**:
MEASURED after v82, `SET TimeZone='UTC'` still leaves a template's `query()` reporting the machine zone
while `fabricator_host_query` reports `UTC`. Item A is unchanged and still needs the ambient
`ClientContext` on the `host_query` entry (option 3 below).

**⚠⚠ `SET GLOBAL` IS A WORKING WORKAROUND FOR THE TIMEZONE HALF, AND THERE IS NONE FOR THE OTHER HALF —
MEASURED 2026-09-02 (user-asked: "does a set global timezone get seen in the host query or not").** The
answer is YES for TimeZone and NOT AVAILABLE for search_path:

| statement | outer session | `fabricator_host_query` | `query()` in a template |
|---|---|---|---|
| *(nothing set)* | `Europe/Berlin` | `Europe/Berlin` | `Europe/Berlin` |
| `SET TimeZone='UTC'` | `UTC` | **`UTC`** ✓ | **`Europe/Berlin`** ✗ |
| `SET GLOBAL TimeZone='UTC'` | `UTC` | **`UTC`** ✓ | **`UTC`** ✓ |

It works for the reason the gap exists: `Host.Query` opens its connection on the captured
`DatabaseInstance`, and a fresh connection there inherits the GLOBAL setting layer while it has no way to
see another connection's SESSION layer. So a client that follows the README's UTC convention with
`SET GLOBAL` rather than `SET` gets the convention honoured everywhere, template queries included.

**⚠⚠ THE SAME TRICK DOES NOT EXIST FOR `search_path`: DuckDB REFUSES IT** — *"Catalog Error: option
\"search_path\" cannot be set globally"*, with or without a fully-qualified value. So that half has NO
workaround at all, which is a second reason it is the sharper one, and it stays a real blocker for a
template that wants to name tables unqualified.

⚠ **The search-path half is the sharper one**: a wrong timezone is a silently wrong VALUE, but an
unresolvable name is an outright failure for a table the caller can see unqualified. It also breaks the
standing convention (README Quick Start) that a client should `SET TimeZone = 'UTC'` — that setting does
not reach a template's queries.

**⚠⚠ WHY THE SQL SURFACE SEES IT, AND IT IS NOT INHERITANCE — this is the part to understand before
designing a fix.** `fabricator_host_query` is C++ invoked WITH a `ClientContext`, and it explicitly COPIES
exactly TWO settings: `CaptureSession` reads `catalog_search_path->GetSetPaths()` and
`TryGetCurrentSetting("TimeZone")`, and `ApplyHostQuerySession` replays them onto the fresh connection.
**Nothing is inherited; two named settings are copied.** So any fix here has the same shape, and the same
question: WHICH settings.

**Why the C# path cannot do it today**: the ABI `host_query` entry has no per-call context — its own
comment says *"the host service has no per-call context, unlike the fs callbacks"* — and it opens its
connection on `g_host_db`, the `DatabaseInstance` captured at extension load. There is no caller session
on that path to copy from.

**Options, none chosen:**

1. **Prepend `SET SESSION …;` to the SQL.** The existing in-tree precedent — `DeltaNativeReader.cs:519`
   emits `"SET SESSION preserve_insertion_order=false; " + Sql`. ⚠⚠ **But it is now much less attractive
   than it was, because of named parameters**: the no-params path is `SendQuery` (several statements, so a
   prefix works) while the parameterised path is `Prepare` (ONE statement — *"Cannot prepare multiple
   statements at once"*). A prefix would therefore silently work for unparameterised calls and break every
   parameterised one.
2. **An optional settings argument on `Host.Query`** (user's suggestion), applied to the connection before
   the statement. ⚠ It needs an ABI change (a new `host_query` argument) — and, more importantly, it does
   not by itself achieve INHERITANCE: the caller would have to name the settings, and a plugin does not
   know the session either. It is the right shape for *"run this with these settings"*, not for *"run this
   as my caller would".*
3. **Pass the ambient `ClientContext` to `host_query`** so C++ can `CaptureSession` itself, exactly as the
   SQL surface does. The ambient already exists and `HostHttpTransport` already reads it per call
   (`AmbientOpener.Current`). This is the only option that actually inherits. ABI change.
4. **Do nothing; document it.** Defensible: `Host.Query` is contracted as a FRESH connection with its own
   transaction and committed-reads semantics, and "a different session" is arguably consistent with that.

⚠ **The design question to settle first is whether it SHOULD inherit at all**, and 2 vs 3 turns on it: an
explicit settings argument and an implicit session copy answer different questions. Note also that "copy
the session" has no principled boundary — the SQL surface copies two settings because those are the two
that were needed, not because they are the right set.

### B. ⚠⚠ `fabricator_host_query`'s search-path replay raises a DuckDB INTERNAL Error — PRE-EXISTING

```
SET search_path='myschema';
SELECT * FROM fabricator_host_query('SELECT count(*) FROM t');
-- INTERNAL Error: SET_WITHOUT_VERIFICATION requires a fully qualified set path
```

`ApplyHostQuerySession` replays with `CatalogSetPathType::SET_DIRECTLY`, which requires fully-qualified
entries, while `GetSetPaths()` returns what the user set — here the bare schema `myschema`. **Pre-existing**:
the same call with the same flag is in the parent of `20960af`; today's work only factored it into a helper.

⚠ It is an **INTERNAL Error**, the class this project records as an assertion failure that can invalidate
the database — so it is worth more than its rarity suggests. Likely fixes: qualify the captured entries
against the caller's catalog before replaying, or use a set-path type that verifies. **Not attempted.**

⚠ **It is UNGATED, and that is why it survived**: no suite sets `search_path` and then calls
`fabricator_host_query`. Any fix should land with a gate that does exactly that.

## Gate totals for the whole pass

`verify_host_query` **31 → 98** (hermetic), across the three changes on this page: the double-execution fix
(→ 53), `fabricator_host_exec` in both spellings (→ 92), and the named-source declared schema (→ 98).

**FIVE mutants, each killed at its own assertion** — describe-by-execute, count-by-type-inference, dropping
the scalar's volatility, removing the `schema_factory`, and ignoring a source's declared schema.

Tiers on the final payload: hermetic **74/74 — 8250** (8183 + exactly this suite's 67, which is what shows no
other suite moved) and service **54/54 — 3162** unchanged, since `verify_host_query` is hermetic — that run
is the regression check for the C# Bridge change, not a floor bump.