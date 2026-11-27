# The `adbc` provider (experimental)

A provider that loads a **native ADBC driver** through the ADBC C interface and talks to it in Arrow and
Substrait, so the remote pushdown built for SQL Server (docs/duckdb-2.0-remote-execution.md §3–§5) can serve any
driver that accepts Substrait plans. First driver: [mssql-adbc](https://github.com/clast-project/mssql-adbc)'s
NativeAOT build. Kusto ADBC is the obvious second.

```sql
ATTACH 'driver=D:/repos/mssql-adbc/artifacts/native-win-x64/adbc_driver_sqlserver.dll;adbc.mssql.server=localhost,1433;adbc.mssql.database=TestDB;username=sa;password=…;adbc.mssql.trust_server_certificate=true'
    AS a (TYPE fabricator, PROVIDER 'adbc');
```

`driver` names the library (`entrypoint`, default `AdbcDriverInit`, its init function); every other key is a
database option handed to the driver unchanged.

**Status: experimental, published by default for now** (`scripts/publish-managed.ps1`; `-ExcludeAdbc` leaves it
out), so it can be tried without a separate step. No automated suite runs it, because every test needs a native
driver and a server. There is no known use case yet; it is kept in main so a change to the contract it implements
breaks the build rather than an unmaintained branch.

| ATTACH option | default | meaning |
|---|---|---|
| `max_in_list` | 1000 | longest IN list a filter sends as a list; longer goes as a range (integers, temporals) or not at all |
| `transaction_snapshot` | true | DuckDB server: an explicit transaction reads one server snapshot from its first read (its connection is pinned then); false: each read before the first write takes its own connection ("Transactions on a quack server") |
| `consistent_reads` | false | the user's promise that one statement reads the same rows alike twice (RCSI / snapshot, or no concurrent writers); without it a plan carrying `fabricator.repeated_reads` is declined |

Drivers tried (both forks, branches pushed; mssql-adbc PRs since 2026-11-06, see below):

| driver | fork / branch | notes |
|---|---|---|
| mssql-adbc | [cmettler/mssql-adbc](https://github.com/cmettler/mssql-adbc) `fabricator-tsql` | our T-SQL emitter ported in; `artifacts/native-port-win-x64` |
| Kusto (Fabric eventhouse, ADX) | [cmettler/kusto_adbc](https://github.com/cmettler/kusto_adbc) `fabricator` | Arrow 23 / ADBC 1.0, `Prepare` + `ExecuteSchema`, a NativeAOT project `KustoAdbc.Native`, `GetStatistics` (row counts from `.show tables details`); options `adbc.kusto.endpoint`, `adbc.kusto.database`, `adbc.kusto.access_token` (a bearer token, not refreshed) |

## How it works

| piece | how |
|---|---|
| discovery | `GetObjects(depth = tables)` for the connection's current catalog (`adbc.connection.catalog`); the default schema from `adbc.connection.db_schema` |
| table schema | `GetTableSchema` |
| scan | a Substrait plan of one `ReadRel` (named table, full base schema, projection mask) built in C# (`SubstraitReadPlan`), with the host's filters as its `best_effort_filter` (see below). Limits are not sent. Substrait has no unsigned integers, so `uint8/16/32` are declared as the next wider signed type |
| filters | the provider declares `superset_filter_pushdown`, so the host hands every scan its filters (a hash join's runtime keys among them) and re-applies them. `SubstraitFilter` turns the filter tree (`FilterNode` + its value batch) into Substrait expressions: comparisons, IS [NOT] NULL, AND/OR, IN as a `SingularOrList` (longer than ATTACH option `max_in_list`, default 1000: its range for integers and temporals, else left out — a driver that writes the list as literals would build a statement the server refuses only when it runs; the `fabricator-tsql` port sends a long list as one OPENJSON array, so it takes `max_in_list 100000`). A scan sends them as `ReadRel.best_effort_filter`; a pushed query that takes a join's keys (way B) gets a `FilterRel` around its root (`SubstraitPlanFilter`) and keeps the columns the spec projects. Whatever the driver refuses at `Prepare` is left out, conjunct by conjunct; `Fabricator.Adbc` (Debug) logs how many went and the plan's size |
| statistics | the table's row count from ADBC `GetStatistics` (approximate). Without it DuckDB takes a remote table for one row, builds the join's hash table over it, and no join key reaches the driver |
| remote pushdown | `remote_translate` hands over the host's Substrait plan: the provider sets it on a statement, calls `Prepare()` (a driver refuses there, before anything runs) and `ExecuteSchema()`, keeps the plan under an opaque handle and returns the handle as the query. `ExecuteQuery(handle)` runs the plan. Result columns are renamed to `c0…` by position; no ORDER BY keys are declared, so the driver's row order is kept |
| connections | one ADBC connection per statement (a driver connection is not thread-safe) |
| writes | a pushed write (the host's WriteRel: `INSERT … SELECT`, `DELETE`, `UPDATE` within the catalog) runs on a driver connection pinned per DuckDB transaction (`AdbcTransaction`: opened with autocommit off by the first write — on a DuckDB server by the first read of an explicit transaction, see "Transactions" below — committed / rolled back with the transaction); reads after it use that connection, so the transaction sees its own writes. For a DuckDB server also DDL (CREATE / DROP TABLE and SCHEMA, CREATE TABLE with NOT NULL / PRIMARY KEY / UNIQUE; `DuckDbDdl`) and bulk loads (`INSERT … VALUES`, COPY, CTAS from local rows: ADBC ingest), in the transaction; host capability `lossless_arrow_boundary` makes those cross in DuckDB's lossless Arrow forms, and the connections read in them (`quack.arrow_lossless_conversion`), so no column changes its type. ALTER TABLE (rename, add / drop / retype a column, NOT NULL, defaults, struct fields, comments), INSERT … RETURNING and an INSERT naming some of the columns go through a temporary table of the session; the table's PRIMARY KEY / UNIQUE is its row identity, so the host pushes MERGE (the driver writes `MERGE INTO`). A DELETE / UPDATE the host cannot push (an UPDATE joined with another table, any RETURNING) takes the key path: the keys (and new values) go to a temporary table, then ONE `DELETE … WHERE EXISTS` / `UPDATE … FROM` (`RETURNING T.*`), capability `dml_returning`. CREATE TABLE takes literal DEFAULTs. The row identity is the table's PRIMARY KEY / UNIQUE, else DuckDB's own `rowid` as a VIRTUAL row id (BIGINT): a scan that reads it runs on the transaction's pinned connection, so the rowids it hands the key path name the rows the DELETE / UPDATE / MERGE then changes. Still refused: an identity or WITH options on CREATE TABLE |

Nothing on the C++ side changed: the ABI v96 contract already lets a provider return any query text for a plan,
and ABI v99 hands a pushed query its table filters.

### Filters, measured (2026-11-05)

200 000-row table on the docker SQL Server, compared with DuckDB over a local copy; the server's rows from
`sys.dm_exec_query_stats`:

| query | rows the server returned |
|---|---|
| `SELECT id, g … WHERE id < 10 OR id > 199995` (plain scan) | 14 (was 200 000) |
| `… WHERE g = 'g7' AND v > 100` | 1 184 |
| `… WHERE g > 'g8' AND id < 500` | 499 — string ordering is not sent by the host (no `string_order_pushable`) |
| `loc JOIN t ON t.id = loc.k`, `loc` 4 rows (runtime keys on a scan) | 3, `WHERE id IN (5, 77, 199999)` |
| `loc JOIN (SELECT id % 1000 AS b, max(v) … GROUP BY 1) s ON s.b = loc.k` (keys on a pushed aggregate) | 2, `… ) AS [t1] WHERE [t1].[_g0] IN (…)` |

A long list (28 572 join keys, `SET dynamic_or_filter_threshold = 100000`, `max_in_list 100000`): one 226 KB plan,
all keys sent and rendered as OPENJSON by the port (docs/duckdb-2.0-remote-execution.md §4.31). The statement time
did not move with it (2.8 s with the list, 2.8 s with the range): the cost sits outside the server — local DuckDB
takes 0.5 s for the same query, the SQL Server provider 1.5 s. Not chased yet; suspects are the 28 572-column value
batch the host builds for the constants and the driver translating the plan twice (Prepare, then execute).

All answers equal DuckDB's. The 139 edge cases are unchanged: 139/139 with the ported driver (48 pushed, see below), and the
same 8 differences as before with `main`. Not measured: the fallback when a driver refuses a conjunct.

## Measured so far (2026-11-05)

The 139 pushdown edge cases (`scripts/pushdown_edge_cases.py`, `EDGE_ADBC_DRIVER=<driver library>`) against
DuckDB evaluating the same rows. With mssql-adbc unchanged (`main`, `590a2d0`): 42 pushed, **8 wrong answers**:

| class | cases | upstream's view |
|---|---|---|
| trailing spaces ignored (`'abc' = 'abc  '`) | distinct trailing, group trailing, distinct case, intersect, union, join string key | **documented as accepted** (`SqlServerSubstrait` remarks, dialect `metadata.semantics`) |
| `avg` of a decimal cut to scale 6 | avg dec | a bug |
| ROLLUP on an empty input loses the grand total | rollup empty | a bug |

The provider changes no answer by itself: every difference is the driver's translation.

With our emitter ported into the driver (branch below): **139/139 agree, 48 pushed** (re-measured 2026-11-06 on
`e2175b7`). Our SqlServer provider pushes 51; the three it pushes and this provider keeps local are declined on
purpose: `grouping sets dup` reads the same rows twice (gap 1, unless `consistent_reads`), and `scalar sum corr` /
`scalar count corr` carry the any-row hint the driver does not read (gap 3). Before those declines it was 51.

## Limitations (2026-11-05)

What works: everything the host pushes as a plain query plan — aggregates, joins, windows, set operations, and the
correlated subqueries and laterals DuckDB turns into joins before translation (the scalar subquery as `OUTER APPLY`,
CTEs inlined). The driver translates those with the same emitter as our SqlServer provider.

What does not, and why. The driver turns a plan into T-SQL with `SqlServerSubstrait.Translate`
(`SqlServerStatement.cs`), while our SqlServer provider calls `TranslatePushdown` and hands it what it knows about the
server. Everything in that hand-over is missing on the ADBC path:

| # | gap | effect |
|---|---|---|
| 1 | **the plan hint `fabricator.repeated_reads` is ignored by the driver — the PROVIDER now declines such a plan** (unless `consistent_reads`) | without that, **wrong answers were possible.** When DuckDB inlines a CTE at two places, the server reads the same rows twice; under READ COMMITTED with concurrent writers the two reads can differ (measured: 28 wrong NULLs in 8 runs, docs/duckdb-2.0-remote-execution.md §4.16). The SqlServer provider declines such a plan unless the reads are consistent (RCSI, snapshot); the ADBC path runs it. No suite catches it — the edge cases have no concurrent writer. Fix belongs in the provider: it sees the hint and can decline in `TranslateRemotePlan` |
| 2 | **catalog functions** (scalar UDFs, TVFs, a TVF per row in a `CROSS APPLY`) | stay local, or do not bind at all: the provider declares no functions (ADBC `GetObjects` lists none), so `a.dbo.f(x)` is unknown to DuckDB; and the driver's emitter has no callback saying a function runs on the server, so it refuses `extension:fabricator:catalog_function`. Possible: the provider reads the routines through the driver with SQL, and since every catalog function of an ADBC catalog is a server function, the driver could write `[schema].[name](…)` directly |
| 3 | the plan hint `fabricator.single_join_any_row` is ignored by the driver — the provider now declines such a plan | DuckDB takes any row itself; before, the driver failed where DuckDB answers |
| 4 | no declared lengths of string columns | equality keys are `varchar(max)`, which SQL Server cannot hash: string joins and GROUP BY are slower, answers unchanged |
| 5 | server features assumed | IGNORE NULLS, DATETRUNC, LTRIM/RTRIM with characters (SQL Server 2022) and OPENJSON (compatibility level 130) are taken as present; an older server fails when the query runs, not at Prepare |
| 6 | columns the emitter must not compare (`sql_variant` and similar) are unknown | the driver may translate a plan the SqlServer provider declines |
| 7 | a TVF per row (`FROM t, a.dbo.tvf(t.a)`) | not a Substrait path at all — it is the lateral session (ABI v79), which this provider does not implement |
| 8 | writes | only what the host pushes as one WriteRel and the driver translates (quack: `INSERT … SELECT`, `DELETE`, `UPDATE`, MERGE), and for a DuckDB server DDL, ALTER, bulk loads, RETURNING and the key path; refused: an identity or WITH options on CREATE TABLE |
| 9 | long IN lists | sent as a list up to `max_in_list` (default 1000), else as a range or not at all; a driver that writes a list as literals fails on a large one only when it runs |
| 10 | per-statement cost outside the server | a scan with 28 572 keys took 2.8 s where the SqlServer provider took 1.5 s (see above); not investigated |

### The way to close them

Our SqlServer provider has its own copy of the same code (`dotnet/Fabricator.SqlServer/Substrait/`, namespace
`Clast.Drivers.SqlServer.Substrait`) and calls `SqlServerSubstrait.TranslatePushdown` in-process
(`SqlServerBackend.cs`), answering its callbacks from what it knows about the server. The mssql-adbc port carries
`TranslatePushdown` too, but `SqlServerStatement` calls `Translate`, and the ADBC C interface cannot carry callbacks.

So the driver calls `TranslatePushdown` itself and answers the callbacks over its own connection — it can look up
nearly all of it:

| callback / fact | the driver's source | closes |
|---|---|---|
| consistent reads (`fabricator.repeated_reads`) | `sys.databases` (`is_read_committed_snapshot_on`, `snapshot_isolation_state`) and the session's isolation level; without them the plan is refused at Prepare | 1 |
| `fabricator.single_join_any_row` | read by `TranslatePushdown` from the plan itself | 3 |
| declared string lengths, column SQL types, uncollatable columns | `sys.columns` / `sys.types` for the tables the plan reads | 4, 6 |
| SQL Server 2022 functions, OPENJSON | `SERVERPROPERTY('ProductMajorVersion')`, `compatibility_level` | 5 |
| catalog functions | every function of an ADBC catalog is a server function: answer `[schema].[name]` once the routine exists (`sys.objects`) | 2 (driver half) |

The provider only switches it on: a statement option meaning "translate this plan as a DuckDB pushdown" — needed
because `TranslatePushdown` reads our `fabricator.*` hints and follows the pushdown's conventions (result columns
`c0…`, ORDER BY through hidden key columns), which a plain ADBC client does not expect.

What stays for the provider: declaring the server's routines to DuckDB (gap 2, host half), read through the driver
with an SQL query. Gaps 7–10 are outside this.

## Kusto (Fabric eventhouse), measured 2026-11-05

Eventhouse with two tables (`Stocks` 749 597 rows, `Logs` 214 769), the Fabric service principal's token. Schema =
the database (a GUID in Fabric): `k."93181802-…".Stocks`. Eleven queries, each compared with a local copy both ways:
all equal. Pushed as KQL: GROUP BY with count/sum/min/max, `LIKE 'A%'` (a range plus `matches regex`), ORDER BY …
LIMIT (`sort … | take`), `count(DISTINCT)`, a scalar subquery, a self-join, a TIMESTAMPTZ bound (converted to UTC). A
filtered scan sends its filter (`| where ((['Ticker'] == 'MSFT') and (['Close'] > real(50.0)))`), and with
`GetStatistics` a join with three local keys sends them (`| where (['Ticker'] in ('KO', 'IBM', 'PEP'))`). Local:
`date_trunc` on TIMESTAMPTZ (our producer declines it), and inline `VALUES` (the Kusto translator has no virtual table).

What the Kusto driver needed, all on the fork branch: ADBC 1.0 (the 0.22 exporter failed to load under our 1.0
importer, "Undefined error"), `Prepare` / `ExecuteSchema`, source-generated JSON for the request (reflection is off
under NativeAOT), a stream that knows its schema before the first batch, quoted column names from `cslschema`
(`['1mbin']`), and `notruncation` (Kusto cuts a result at 500 000 rows and puts the error inside the rows).

## quack (a DuckDB server), measured 2026-11-25 (branch `exp/adbc-duckdb-dialect`)

quack-net's ADBC driver (`quack_adbc`) takes a Substrait plan since its branch `substrait` (`35a1313`, local): it
writes the plan as one DuckDB query and refuses at `Prepare` what it cannot write exactly — the same ADBC contract as
mssql-adbc. Nothing to set on our side:

```sql
ATTACH 'driver=…/quack_adbc.dll;uri="quack://127.0.0.1:9494/?token=…&tls=false"' AS q (TYPE fabricator, PROVIDER 'adbc');
```

- **The emitter lives in the driver** (quack-net, `DuckDbSubstrait.cs` in its Quack.Adbc Substrait folder, with a copy of the
  decoder and model of our T-SQL emitter — they derive from the Kusto ADBC driver, the same author's). Since the
  target is DuckDB, nearly all of the T-SQL emitter's compensations fall away (strings, collation, rounding, null
  order); what is left is to undo the producer's renames (`modulus` → `%`, `std_dev` → `stddev`, `round_calendar` →
  `date_trunc`, `extract` → `year()` …) and write each relation. Refusals: `any_value` (it stands for DuckDB's
  `first` AND `any_value`, which differ on NULL), a repeated grouping set, table functions, catalog functions,
  volatile functions, writes. The driver checks the plan's division / domain options against the server's
  `ieee_floating_point_ops` / `error_on_division_by_zero` (DuckDB 2.0.1's default: `error_on_division_by_zero =
  true`).
- **The provider tells a DuckDB server by GetInfo `vendor_name`** (`DuckDB`) and then:
  - declares the host capability **`substrait_native_functions`**: the producer runs non-strict for that catalog, so a
    DuckDB function with no Substrait declaration (`stddev_samp`, `median`, `arg_max`, `grouping`, `dayname`,
    `regexp_matches` …) is declared by its DuckDB name with no extension URN, and the driver writes it as is. For every
    other provider the producer stays strict;
  - **compares the calling session's settings with the server's** before a plan with expressions goes (a pushed query,
    a scan's filters): `TimeZone`, `Calendar` (absent without ICU — a difference too), `default_collation`,
    `scalar_subquery_error_on_multiple_rows`, `old_implicit_casting`, the two division settings, and the DuckDB
    version. Only this side knows the calling session (read through `IHostQuery` as that session). A plain read goes
    whatever the settings;
  - passes both plan hints on: one DuckDB statement reads one snapshot (`repeated_reads`), and the driver writes the
    any-row single join with `LIMIT 1` (one column) or refuses it.

**Measured** (the scripts in `scripts/adbc_quack/`, run by hand: a DuckDB 2.0.1 CLI serving quack with the edge-case tables in DuckDB types,
compared with the same rows in the local DuckDB): the 142 SQL Server edge cases plus 17 DuckDB-only ones (unsigned /
HUGEINT columns, `list`, QUALIFY, DISTINCT ON, median, arg_max, intervals …): **159 cases, 0 wrong, 69 pushed as whole
queries** (the rest run as scans with their filters; local: aggregates with ORDER BY and RANGE offset frames, both
refused by the producer for every dialect). Control: with `SET TimeZone` differing from the server's, a pushed query
is declined with that reason and the scan answers correctly. quack-net's own tests: real plans of this producer as
fixtures (`SubstraitTests`, a mutant in the emitter's `=` is caught).

**Writes, measured the same way.** DML (`DELETE` incl. `USING` and an `IN` subquery, `UPDATE`, `INSERT … SELECT`, a
committed transaction): 8 cases, each equal to the same statements on a local copy, all pushed; read-your-writes
inside a transaction, then ROLLBACK. DDL and bulk: a table of 16 types (HUGEINT, UHUGEINT, UUID, TIMETZ, BIT, a list,
a struct, TIMESTAMP_NS, INTERVAL …) created with exactly those types and its PRIMARY KEY, filled by `INSERT … VALUES`
and by CTAS from a local table with exactly the same values; CREATE + INSERT + read + ROLLBACK / COMMIT in a
transaction; schemas and DROP. Before the lossless boundary the same table read back HUGEINT as DECIMAL(38,0) — the
INSERT then failed at bind (`Could not cast … to DECIMAL(38,0)`). ALTER / MERGE / RETURNING: 7 ALTER cases, 3 MERGE cases (pushed as one statement) and INSERT … RETURNING, each
equal to the same statements locally. ⚠ The first MERGE run gave two WRONG answers that were the DRIVER emitter's, not
MERGE's: a computed column (here the host's `1 AS __fab_t`) on the side an outer join pads with NULL was written above
the join and kept its value; reads had the same defect (`a LEFT JOIN (SELECT id, 1 AS one …)`), unseen by the edge
cases because none had a computed column on the padded side. Fixed in the driver, gated there and in the harness; our
T-SQL emitter does not have it (measured on SQL Server). Key path and defaults: DELETE / UPDATE … RETURNING, an UPDATE … FROM
with a join and one changing a key, CREATE TABLE with defaults then an INSERT naming one column — each equal locally. On a table
with no key (the rowid): an UPDATE … FROM with a join, MERGE, a transaction, DELETE / UPDATE … RETURNING — each equal
locally; without the virtual rowid all five fail at bind ("requires a table with a primary key"), which is the control
that they take that path.

**Three producer defects it found, on the SqlServer path too** (control: v0.2.11, remote pushdown on by default, all
wrong; fixed on `v2.0-cyanoptera` in `third_party/duckdb-substrait/src/to_substrait.cpp` as refusals, gated in
`verify_pushdown_edges_mssql` cases `cast unsigned` / `distinct on` / `interval mixed`; released in 0.2.12):
- `CAST(x AS UTINYINT)` (any unsigned target, and HUGEINT): Substrait declares the target as the wider signed type, so
  the range check went missing — `CAST(-7 AS UTINYINT)` fails in DuckDB and returned -7 when pushed.
- `DISTINCT ON (g)`: written as a grouping by EVERY column, so every distinct row came back instead of one per `g`.
  Refused now unless its targets are every column (`SELECT DISTINCT ON (k) k`, a plain DISTINCT, still goes —
  `verify_remote_select_mssql` pins that; the first version of the fix refused it too and that suite caught it).
- an interval constant with months AND days or a time: upstream kept only the months ("set the largest interval
  value"), so `ts + INTERVAL '1 month 2 days'` was pushed as `ts + 1 month`.

### Inversion pass (2026-11-25, `scripts/adbc_quack/quack_inversion.py`, `quack_scale.py`)

What the differential runs could not see, measured:

| candidate | result |
|---|---|
| ORDER BY lost through projection, join, set operation, window, nested limit, an unselected sort key (the other runs compare sorted rows) | kept in all 12 shapes |
| a volatile function written twice by the emitter's inlining (`random()`, `gen_random_uuid()`) | not pushed at all (the host keeps them local) |
| nested types read losslessly (`BOOLEAN[]`, STRUCT with UUID / HUGEINT, MAP, `TIMETZ[]`, `BIT[]`) | exact |
| **ENUM columns** | **two defects, both fixed.** (1) HOST: six sites read an Arrow column's type with `GetDuckType()`, whose default leaves out the dictionary, so a dictionary-encoded column was typed as its INDEX type (UTINYINT) and read as indexes — any provider or plugin returning dictionary arrays had this; DuckDB's own scan asks `GetDuckType(true)`. (2) DRIVER: the catalog sees an ENUM as VARCHAR (Arrow has no enum), but a pushed ORDER BY / min / max / top-n followed the ENUM's declared order — the driver now reads a column the plan declares a string as `CAST(… AS VARCHAR)` (DuckDB drops the cast on a VARCHAR) |
| two transactions updating one row | the second fails ("Conflict on update!"), no update lost |
| a large scan in a transaction after a write (drained) | +45 MB beyond the autocommit run for a 120k-row result: linear in the result. **Superseded** — a pinned scan now streams and buffers only on overlap ("Transactions" below) |
| bulk through a temporary table | 1M rows: CTAS 0.5 s, INSERT into an existing table 0.3 s — no cost |
| per-query cost | **a point query took 55 ms where the driver alone takes 2.8 ms**: both settings reads used `duckdb_settings()` (~7.6 ms, every setting) and the driver translated each plan twice. Now `current_setting` and one translation per plan: **22.6 ms**. What remains is the bind's metadata round trips (schema, key, row count) and the host-session read through `IHostQuery` |
| a non-fabricator Substrait producer | not measurable here: the driver takes a function with no URN as a DuckDB function and the standard names by their DuckDB meaning — fine for DuckDB's own `to_substrait`, an assumption for any other producer |

### Transactions on a quack server (2026-11-25)

A quack session runs one statement at a time, so everything a DuckDB transaction does on its pinned connection
takes turns (`AdbcTransaction.Use`). What a transaction sees, by default:

| | before the first write | after it |
|---|---|---|
| autocommit statement | each scan its own connection: what is committed when it starts | — |
| explicit transaction, `transaction_snapshot` true (default) | **one server snapshot from the first read** (the connection is pinned then) | the same snapshot plus its own writes |
| explicit transaction, `transaction_snapshot false` | each scan its own connection (per-scan, like RCSI but per scan, not per statement) | one snapshot from the first write |

The default is what DuckDB does locally (a transaction reads one snapshot, taken at its first access to the
database), i.e. SQL Server `SNAPSHOT`, not RCSI. A write conflicting with a commit after the snapshot fails
(`Conflict on update!`), as in DuckDB. `fabricator_query` / `fabricator_exec` still take their own connection.

**Buffering only on overlap.** A scan on the pinned connection STREAMS (`PinnedStream`); only when something else
needs the connection while it is open does it read the rest of its result into memory (debug log `pinned scan
buffered: N batches, M rows`). Statements one after the other buffer nothing; a non-pushed join of two remote
tables did not buffer either (DuckDB finishes the build side before the probe side starts). What does buffer: a
load into the server while a scan of the same connection feeds it (an `INSERT … SELECT` that is not pushed whole) —
the rest of the scan, once the load's first chunk is sent.

**Later, with a DuckDB 2.1 server: `JOIN TRANSACTION` (duckdb/duckdb#29429 on `main`, noted 2026-11-27, nothing
built).** A connection in an explicit transaction can `SELECT share_transaction()` and other connections of the
same server can `JOIN TRANSACTION '<uuid>'`; all of them then run in ONE transaction, also at the same time. The
pinned connection is already in `BEGIN`, so the driver could open further server connections that join it and
let overlapping scans stream in parallel in the same snapshot instead of buffering (`PinnedStream`), with no
protocol change (it is SQL). Rules from the diff: every extra connection votes (`COMMIT`, or `ROLLBACK` on a
rollback) before it is released, because closing it counts as a rollback vote, and the real `COMMIT` on the
pinned connection comes last; a scan of a table that the same statement writes stays on the pinned connection
(a participant can see the partial effect of another's running write); a failure still aborts the whole
transaction, as today. Detect it by `share_transaction` in `duckdb_functions()`; a 2.0.x server keeps
`PinnedStream`.

**Loads never hold the connection while waiting for their input.** Before this, a load held the connection for its
whole ingest; if its input came from a scan of the same connection that DuckDB opened only later, both waited for
ever — MEASURED: `BEGIN; INSERT …; INSERT INTO rp.t SELECT … FROM local UNION ALL SELECT … FROM rp.s` hung (the
other orders passed by luck of the scan opening first). Loads (bulk, the temporary tables of INSERT naming some
columns, RETURNING and the key path) now read their input in chunks of 16 MB outside the connection and hold it
only to send each chunk (`IngestChunks`).

**A failed statement aborts a DuckDB transaction**, so nothing may fail on the pinned connection that we would
swallow. Pinning at the first read exposed one that was already there after a first write: CREATE TABLE asks the
catalog whether the table exists, and `GetTableSchema` of a missing table failed on the server — "Current
transaction is aborted" at the CREATE. On a pinned connection the lookup now asks `duckdb_tables()` /
`duckdb_views()` first, a query that cannot fail (`quack_ddl.py` caught it; `quack_txn.py` pins it after a write).

Measured (`scripts/adbc_quack/quack_overlap.py`, `quack_txn.py`): the UNION ALL case and 6 other
read-and-write shapes 3x each, no hang, right rows; one snapshot across a concurrent commit and the control with
the option off; read-your-writes + ROLLBACK; a 2M-row scan in a read transaction and a non-pushed join of two remote
tables buffer nothing; the positive control (a load fed by a scan of the same connection) buffers 702,464 rows,
right result; a conflicting update after the snapshot fails; 4 concurrent read transactions.

## The mssql-adbc side (branch `fabricator-tsql`, draft PR clast-project/mssql-adbc#32)

Our T-SQL emitter started as a copy of mssql-adbc's at `677676c`; upstream has not changed those files since.
The branch copies ours back, with what mssql-adbc's build demands that ours never had to: the dialect YAML entry
point, an AOT-safe JSON parse, and netstandard2.0 compatibility.

mssql-adbc's own tests against the docker server: 7 failures on `main` (environment), 30 with the port. The 23 new
ones are expected SQL text that changed (string keys), new refusals (ROLLUP/CUBE with the empty set — a real fix;
`sqrt`/`ln` domain — a DuckDB assumption, see 4.), wider translations (correlated scalar subquery via OUTER APPLY,
code-point `CHARINDEX`), and the dialect YAML not listing the float `divide`/`power` variants.

Open questions before an upstream PR:
1. **Trailing spaces and UTF-16 are upstream's documented choice.** Our exact string keys change that contract,
   so they would go in as an option (or a proposal to change the default), not as a bug fix.
2. **Fabricator-specific encodings** (catalog functions, table functions / APPLY, MERGE, the moved CTE, the
   `repeated_reads` hint, the `extension:fabricator:*` function URNs) need a neutral name or stay ours.
3. **Server facts** our emitter takes from the provider (string column byte lengths, uncollatable columns, the
   SQL Server 2022 gate) have no source inside the driver yet.
4. **DuckDB assumptions.** Some refusals follow what DuckDB does rather than what the plan says (e.g. a domain
   error is NaN in DuckDB). A driver for any client should read the plan's function options
   (`on_domain_error`, `on_division_by_zero`, …) instead; our producer would then have to state them.

## Plan for upstream PRs (inventory 2026-11-05)

**Sent 2026-11-06:** #30 (row 1, the empty grouping set kept with a LEFT JOIN from one constant row, not refused) and
#31 (row 2, code points under `Latin1_General_100_CS_AS_SC_UTF8`), each off upstream `main` with hand-built-plan
translation tests and execution tests; #32 is the whole port as a DRAFT, a report to take from, not for merge. Small
PRs for the other rows were dropped for now (user decision): the maintainers can take what they want from #32.

mssql-adbc tests its emitter in three ways: `SubstraitTranslationTests` build a plan by hand (`SubstraitPlanBuilder`)
and expect the exact T-SQL text, which `AssertParses` also runs through the T-SQL parser (no server);
`SubstraitExecutionTests` run such plans against a server and check rows; `DataFusionFixtureTests` translate plans
another producer wrote. The port adds about 1160 lines to the emitter and 600 to the function catalog **and no test of
its own**: everything it does is covered only by our suites, which go through DuckDB's producer and our host. A
reviewer can neither run those nor read from them which plan gives which SQL. So every PR below needs, per rule, one
translation test with a minimal hand-built plan, plus an execution test wherever correctness depends on what the
server does (those are exactly the places our wrong answers came from).

The port breaks 23 of upstream's tests (16 in fixtures). Each is accounted for below.

| # | change (our commit) | kind | upstream tests it breaks | tests to write | PR |
|---|---|---|---|---|---|
| 1 | ROLLUP / CUBE / grouping sets with the empty set refused: SQL Server returns no grand-total row on an empty input (`6326b54`) | **bug fix** | fixtures `grouping_sets`, `rollup`, `cube`; `GroupingSetsAppendTheSetIndex` | execution: empty input, DuckDB's answer has the total row. Better than refusing: emit the total via a separate branch | A |
| 2 | string positions and lengths in code points, `CHARINDEX` per code point (`24af628`) | **bug fix** (supplementary characters) | `ExpressionsSupportedOnlyByWorkarounds` (expected text) | execution with a surrogate pair | A |
| 3 | float → integer cast rounds half to even, decimal → integer half away from zero, where T-SQL `CAST` truncates (`24af628`) | **DuckDB semantics** — Substrait leaves a cast's rounding open | — | ask the maintainers whether a plan option should carry it; until then ours | ? |
| 4 | correlated scalar subquery (single join) as `OUTER APPLY (SELECT TOP (1) …, COUNT_BIG(*) OVER ())` raising error 512 for more than one row (`71dfc4f`, `aeaaf53`) | **wider** (upstream refuses it) | fixture `correlated_scalar_subquery` (expects a refusal) | translation + execution: 0 / 1 / 2 rows | B |
| 5 | `IN` / `NOT IN` filters as semi / anti joins, three-valued NOT IN (`226ce05`) | wider | — | execution with NULL in the list and in the column | B |
| 6 | long `IN` lists as one OPENJSON array (`bd367e3`) | wider, needs a server fact (compatibility level ≥ 130) | — | translation (threshold), execution: 1000 keys, NULL | B |
| 7 | window functions (`f931434`; upstream has none) | wider | — | translation per frame kind, execution for ranking + running sum | C |
| 8 | `||`, trim (17 space code points), left / right, lpad / rpad, `date_trunc`, `date_diff`, `last_day`, `make_date`, `round_calendar` (`e1cd8c4`, `5d21c06`, `24af628`) | wider | `Dialect_FunctionsMatchTranslatorCatalog` (dialect YAML lacks them) | translation each; execution where a rule is subtle (trim set, date_diff boundaries) | C |
| 9 | exact string equality / GROUP BY / DISTINCT / join keys (UTF-8 key with sentinel, sized to the column), indexable prefilter, string min / max by code point (`d86252a`, `c246237`) | **contract change**: upstream documents collation-based comparison and offers "the database's collation" | fixtures `aggregates`, `union_distinct`, `string_ordering`, `aggregate_sort_limit`, `case_sensitive_group`, `in_subquery`, `order_limit_offset`, `left_join_extra_condition`, `distinct`, `in_list`; `StringsGetTheCollationUnlessTurnedOff`; `DatabaseCollationIgnoresCase` (**a regression**: the port ignores the database-collation mode) | option, off by default; execution: trailing spaces, case, unpaired surrogates | D |
| 10 | float math refused when DuckDB's result differs (`sqrt`, `ln`, `log*`, `asin`, `acos`, `power`, division by zero) (`6408792`, `baedf5a`) | **DuckDB assumption** — upstream already reads `on_domain_error` / `on_division_by_zero` from the plan | fixture `math`; dialect test | none upstream: drop from the port, have **our producer** state the options instead (to_substrait) | ours |
| 11 | `substring` with a non-constant or negative start refused (`24af628`) | DuckDB assumption — upstream reads `negative_start` | `StringFunctions` | as 10 | ours |
| 11b | further refusals where T-SQL differs from DuckDB: `any_value` (T-SQL MAX), binary `=` / GROUP BY ignoring trailing zero bytes, `reverse` per code unit, `regexp_*`, decimal arithmetic past precision 38 losing scale | documented upstream differences or DuckDB semantics | — | a strict mode could refuse them; otherwise ours | D or ours |
| 12 | INSERT … SELECT / CTAS, DELETE / UPDATE as a `WriteRel` (`07a4bfc`, `fbb0468`) | wider, standard Substrait | — | translation + execution per op; needs an ADBC entry that executes a write plan | E (ask first) |
| 13 | MERGE (`fabricator.merge`), the moved CTE (`DELETE … OUTPUT INTO`), catalog UDFs, TVFs and `CROSS APPLY`, the `repeated_reads` / `single_join_any_row` hints, `extension:fabricator:*` URNs (`7e1e77f`, `7938f7a`, `5ddccf6`, `f68912d`, `54fe62f`, `8c03a10`) | **ours** | — | — | stays ours until a neutral encoding exists |
| 14 | a malformed collation name throws `SubstraitTranslationException` instead of the `AdbcException` the statement API promises | **regression in the port** | `StatementTranslatesAndDescribesPlans` | fix in the port | — |
| 15 | regexp refusal message no longer names RE2 | wording | `RejectsWhatCannotBeTranslatedFaithfully` | keep upstream's wording | — |

Server facts our emitter gets from the provider and the driver does not have yet: string column byte lengths (9),
uncollatable columns (9), compatibility level (6), the SQL Server 2022 gate (8, trim with characters). Each PR that
needs one must also teach the driver to read it (`sys.columns`, `DATABASEPROPERTYEX`, `SERVERPROPERTY`).

`avg` over integers (an integer division inside `AVG`, recorded in duckdb-2.0-remote-execution.md §3.2) is fixed upstream meanwhile (`Widened`); it is not on the list.

Order: **A** first (bug fixes, smallest, no contract question), then B and C (wider translations, each self-contained),
D as an option once A–C are in, E only after asking the maintainers. 10 and 11 are not upstream work: the port should
go back to upstream's plan-option reading, and DuckDB's semantics belong in what our producer writes into the plan.
