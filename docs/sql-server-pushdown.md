# SQL Server pushdown — what runs on the server, what runs in DuckDB

DuckDB can hand work to SQL Server instead of pulling rows and doing it itself. This page shows, statement by
statement, **what is pushed, what is not, and how to tell**. Every example is checked by
`test/verify_pushdown_guide_mssql.test` under the same id (S1, D2, …), so a change in routing shows up there first.

> **The one rule behind every decision: SQL Server runs a statement only if the T-SQL reproduces DuckDB's result
> exactly.** If any part cannot be translated exactly, that part (or the whole statement) runs in DuckDB as before.
> Nothing is approximated. The result never changes with pushdown — only where the work happens.

## At a glance

| Statement | Pushed when … | `EXPLAIN` shows | Otherwise |
|---|---|---|---|
| `SELECT` | it joins, aggregates, sorts with a limit, uses windows, … over tables of the catalog (S1) | `Fabricator Query` | `Fabricator Scan` (+ DuckDB operators); the scan still sends its filter and columns to the server (S2) |
| part of a statement — a subquery or CTE | it reads tables of the catalog alone and aggregates, joins, … while the rest of the statement reads local data (S5–S9) | `Fabricator Query` under DuckDB's own join | `Fabricator Scan` (S4) |
| a table joined with LOCAL data | the server table is large enough for DuckDB to plan a join filter: the keys reach the scan's `WHERE` once DuckDB has read the local side (J1) | `Fabricator Scan` (the `WHERE … IN` shows in `Fabricator.Sql`) | `dynamic_filters 'false'`: the whole table is read |
| `INSERT … SELECT` | the `SELECT` reads tables of the same catalog and translates exactly (I1) | `FABRICATOR_REMOTE_DML` | `Mssql Net Insert`: DuckDB streams the rows into a bulk copy — and still pushes the `SELECT` when worth it |
| `INSERT … VALUES` | never — a bulk copy is already the fast path (I2) | — | `Mssql Net Insert` |
| `CREATE TABLE … AS` | its query is pushed (C1) | `Fabricator Remote Ctas` | `Mssql Net Create Table As`: rows stream into a bulk copy (C2) |
| `DELETE` | the `WHERE` translates exactly — strings, subqueries over the catalog, `USING` included (D1–D3) | `FABRICATOR_REMOTE_DML` | `Mssql Net Delete`: scan the keys, delete by key (D4) |
| `UPDATE` | as `DELETE`, every `SET` value translates (U1); not `UPDATE … FROM` | `FABRICATOR_REMOTE_DML` | `Mssql Net Update`: scan, update by key (U2) |
| `… RETURNING` | not as one pushed statement: `DELETE` / `UPDATE` take the key path, the rows come from T-SQL `OUTPUT` | `Mssql Net Delete` / `Update` / `Insert` | see [RETURNING](#returning) |
| `WITH moved AS (DELETE … RETURNING …) INSERT INTO … SELECT … FROM moved` | source and target are tables of the same catalog, the columns keep their types (R4) | `FABRICATOR_REMOTE_DML` | the `DELETE … RETURNING` (R2), then the regular insert (R5) |
| `MERGE` | its conditions and values translate exactly; any number of `WHEN` clauses (M1) | `FABRICATOR_REMOTE_DML` | merge by key (M2), see [MERGE](#merge) |

## Seeing what happens

- **`EXPLAIN <statement>`** — the operator names in the table above: *where* it runs.
- **`duckdb_logs`** — *why* it runs there; see [Why it ran where it ran](#why-it-ran-where-it-ran).
- **`FABRICATOR_DUMP_REMOTE=1`** / **`FABRICATOR_DUMP_DML=1`** (environment variables) print the same decisions to
  the console, prefixed `[fabricator remote]` / `[fabricator dml]`.

## Why it ran where it ran

Every decision — pushed or not, and for what reason — is written to DuckDB's log under two types:
`Fabricator.Remote` (statements and parts of statements) and `Fabricator.Dml` (the `DELETE` / `UPDATE … WHERE`
shortcut). The T-SQL SQL Server actually received is under `Fabricator.Sql`.

```sql
CALL enable_logging(['Fabricator.Remote', 'Fabricator.Dml', 'Fabricator.Sql'], storage := 'memory');
-- run the statements
SELECT type, message FROM duckdb_logs WHERE type LIKE 'Fabricator.%';
CALL truncate_duckdb_logs();   -- start over
CALL disable_logging();
```

`storage := 'memory'` is what makes `duckdb_logs` queryable in the DuckDB CLI (its default prints to the console).

| message | meaning |
|---|---|
| `remote: <T-SQL>` | the whole statement ran on the server as this T-SQL |
| `remote subtree (before the optimizers): <T-SQL>` | a part of the statement ran on the server; *before* / *after* names which of the two passes found it |
| `local: <reason>` | the whole statement stays in DuckDB; its tables are still scanned with their filter and columns pushed |
| `local subtree (…): <reason>` | a part that reads only the server stays in DuckDB |
| `rowid path: <reason>` | a `DELETE` / `UPDATE` scans the keys and changes by key |
| `delete-where: …` / `update-where: …` | the `DELETE` / `UPDATE` ran as one statement through the shortcut |

The reasons you will meet most:

| reason | what to do |
|---|---|
| `no Substrait translation: Could not find function "<f>" …` | the function has no exact T-SQL; rewrite with one that has, or accept the local run |
| `a plain scan keeps the table scan` | nothing to gain: the scan already sends its filter and columns |
| `… dbo.<f>() is not a function of the server` | a function the provider implements itself (C#, a plugin): it does not exist on SQL Server |
| `… stays local because it calls <f>() of catalog <c>` | a function of ANOTHER attached catalog, or a catalog aggregate / window function |
| `… stays local because it calls <f>(), whose value is not fixed by its arguments` / `local: it calls …` | `now()`, `current_date`, `random()`, … — the server would answer with its own clock, time zone or generator |
| `a filter above it reads its columns; left for the pass after the optimizers` | not a refusal: the part is pushed once DuckDB has moved the filter into it, so the server query carries the `WHERE` (S7) |
| `… stays local because it uses a prepared-statement parameter` | only for a part of a statement; a whole prepared statement is planned per `EXECUTE` with its values |
| `the predicate does not render exactly: <condition>` | the named condition has no exact T-SQL |
| `fabricator_dml_pushdown = false` | switched off in this session |

**L1** — a statement that stays local says why:

```sql
SELECT id FROM mssql.dbo.pg_sales WHERE regexp_matches(note, 'i.s') GROUP BY id;
-- Fabricator.Remote | local: no Substrait translation: Could not find function "regexp_matches" with argument types: ('string', 'string')
```

**L2** — a part that reads only the server, and still stays local (`cf_add` is implemented by the provider in C#, so
it does not exist on SQL Server):

```sql
SELECT t.region, s.n
FROM (VALUES ('EU')) t(region)
JOIN (SELECT region, sum(mssql.dbo.cf_add(id, 1)) AS n FROM mssql.dbo.pg_sales GROUP BY region) s USING (region);
-- Fabricator.Remote | local subtree (before the optimizers): Unsupported Substrait construct: dbo.cf_add() is not a function of the server
```

**L3** — a `DELETE` that takes the key path names the condition (DuckDB rewrote `'^l'` to `prefix`):

```sql
DELETE FROM mssql.dbo.pg_sales WHERE regexp_matches(note, '^l');
-- Fabricator.Dml | rowid path: the predicate does not render exactly: ((note >= 'l') AND (note < 'm')) (BOUND_CONJUNCTION)
-- (DuckDB turns the prefix test into a string range, and a string comparison has no exact T-SQL here)
```

**L4** — what was pushed, and what SQL Server received:

```sql
SELECT r.name, count(*) AS n FROM mssql.dbo.pg_sales s JOIN mssql.dbo.pg_regions r USING (region) GROUP BY r.name;
-- Fabricator.Remote | remote: SELECT CONVERT(nvarchar(max), … FROM [dbo].[pg_sales] AS [t0] INNER JOIN [dbo].[pg_regions] … GROUP BY …
-- Fabricator.Sql    | query [pooled txn=… route=pooled]: SELECT CONVERT(nvarchar(max), …
```

The `Fabricator.Sql` line collapses whitespace and stops after 400 characters; the `Fabricator.Remote` line has
the whole statement. `Fabricator.Sql` also shows the catalog's own metadata queries, which are not pushdowns.

## Switching it off

| Scope | How | Effect |
|---|---|---|
| one catalog | `ATTACH … (TYPE fabricator, remote_pushdown 'false')` | no `SELECT` / `INSERT … SELECT` / `CREATE TABLE … AS` pushdown |
| every new ATTACH | environment `FABRICATOR_REMOTE_PUSHDOWN=false` | the default for ATTACHes that do not name the option |
| the session, per statement | `SET fabricator_dml_pushdown = false;` … `RESET fabricator_dml_pushdown;` | `DELETE`, `UPDATE`, `INSERT … SELECT` and `CREATE TABLE … AS` use the regular paths; `SELECT` pushdown is unaffected |
| the session, parts of statements | `SET fabricator_subtree_pushdown = false;` … `SET fabricator_subtree_pushdown = true;` | a subquery or CTE beside local data runs in DuckDB again (S5, S6); whole statements are unaffected |
| one catalog, join keys | `ATTACH … (TYPE fabricator, dynamic_filters 'false')` | a scan no longer takes a join's keys (J1) or DuckDB's other table filters; filters stay above it |

Use the per-statement switch when streaming through DuckDB is faster — DuckDB reads and writes in parallel, while a
single server `INSERT … SELECT` uses whatever plan SQL Server picks.

**Requirements:** SQL Server 2019+ or Azure SQL. Not on Fabric Warehouse / Synapse (there the regular paths run).

## Example tables

```sql
ATTACH 'Server=…;Database=…' AS mssql (TYPE fabricator);
SELECT fabricator_exec('mssql', '
  CREATE TABLE dbo.pg_regions (region VARCHAR(2) PRIMARY KEY, name NVARCHAR(30));
  CREATE TABLE dbo.pg_sales (id INT PRIMARY KEY, region VARCHAR(2), amount DECIMAL(10,2), sold DATE, note NVARCHAR(50));
  CREATE TABLE dbo.pg_sales_eu (id INT, amount DECIMAL(10,2), loaded DATETIME2 DEFAULT SYSDATETIME());
  INSERT INTO dbo.pg_regions VALUES (''EU'', N''Europe''), (''US'', N''Americas'');
  INSERT INTO dbo.pg_sales VALUES (1,''EU'',10,''2026-01-05'',N''first''), (2,''US'',20,''2026-02-01'',NULL),
                                  (3,''EU'',30,''2026-02-07'',N''Größe''), (4,''US'',5,''2025-12-30'',N''late'');',
  refresh_cache := true);
```

S8, S10 and S11 also use two SQL Server functions:

```sql
SELECT fabricator_exec('mssql', 'CREATE OR ALTER FUNCTION dbo.pg_net(@a DECIMAL(10,2)) RETURNS DECIMAL(10,2) AS BEGIN RETURN @a * 0.8 END');
SELECT fabricator_exec('mssql', 'CREATE OR ALTER FUNCTION dbo.pg_region_sales(@r VARCHAR(2)) RETURNS TABLE AS RETURN (SELECT id, amount FROM dbo.pg_sales WHERE region = @r)',
  refresh_cache := true);
```

## SELECT

**S1 — pushed.** A join with an aggregate and an `ORDER BY`:

```sql
SELECT r.name, count(*) AS n, sum(s.amount) AS total
FROM mssql.dbo.pg_sales s JOIN mssql.dbo.pg_regions r USING (region)
GROUP BY r.name ORDER BY total DESC;
-- EXPLAIN: Order By -> Fabricator Query.   Europe 2 40.00 / Americas 2 25.00
```

Only the two result rows leave the server. The T-SQL compares and groups the strings in a binary collation (wrapped
in `REPLACE`/`COLLATE` expressions), because SQL Server's default collation would treat `'a'`, `'A'` and `'a '` as
equal where DuckDB does not. The `ORDER BY` runs on the server and again in DuckDB, which keeps the order through the
subquery the result arrives in.

**S2 — not pushed, still filtered on the server.** A plain scan gains nothing from a pushed query; the regular scan
already sends its `WHERE` and its column list:

```sql
SELECT id, amount FROM mssql.dbo.pg_sales WHERE amount > 15;
-- EXPLAIN: Projection -> Filter -> Fabricator Scan.   trace: "local: a plain scan keeps the table scan"
```

**S3 — not pushed: no exact T-SQL.** Regular expressions (and `reverse`, float → string casts, …) have no T-SQL
equivalent that gives the same answer, so the query runs in DuckDB:

```sql
SELECT id FROM mssql.dbo.pg_sales WHERE regexp_matches(note, 'i.s') GROUP BY id;
-- EXPLAIN: Hash Group By -> Filter -> Fabricator Scan.   trace: Could not find function "regexp_matches"
```

A pattern DuckDB's own optimizer turns into something simpler is pushed after all: `regexp_matches(note, '^f')`
becomes a prefix test, which T-SQL has exactly (`LIKE N'f%'` in a binary collation), and the second look for
pushable parts (see S5) sees that form.

**S4 — not pushed: a local table in the join, and the remote part is only a scan.** The aggregate here sits above
the join with local data, so all that reads the server alone is the scan — which already sends its filter and
columns:

```sql
CREATE TEMP TABLE targets AS SELECT 'EU' AS region, 25 AS target;
SELECT t.region, sum(s.amount) - t.target AS gap
FROM mssql.dbo.pg_sales s JOIN targets t USING (region) GROUP BY t.region, t.target;
-- EXPLAIN: Hash Join over Fabricator Scan.   EU 15.00
```

**S5 — pushed: the part that reads the server alone.** Written with the aggregate in a subquery (or a CTE), the
same question has a part that reads only the catalog and reduces it — that part runs on the server as one query,
and DuckDB joins its few rows with the local table:

```sql
SELECT t.region, s.total - t.target AS gap
FROM targets t JOIN (SELECT region, sum(amount) AS total FROM mssql.dbo.pg_sales GROUP BY region) s USING (region);
-- EXPLAIN: Hash Join -> Fabricator Query (the aggregate) + the local scan.   EU 15.00
```

Every maximal part of the plan that reads tables of ONE catalog and nothing else is a candidate: an aggregate, a
join of the catalog's tables, a window, `DISTINCT`, an `ORDER BY … LIMIT`, a set operation. A part that only scans,
filters and projects stays a `Fabricator Scan` (S4). It is looked for twice, before and after DuckDB's own
optimizer, because that optimizer rewrites some shapes into forms T-SQL has none for (an aggregate pushed through a
join, a filtered `row_number()`).

**S6 — pushed: a scalar subquery.** `(SELECT max(…) FROM …)` is such a part too:

```sql
SELECT region, target - (SELECT max(amount) FROM mssql.dbo.pg_sales) AS below_max FROM targets;
-- EXPLAIN: Fabricator Query (the max) crossed with the local scan.   EU -5.00
```

**S7 — pushed, with the condition inside.** A `WHERE` on a column of the part belongs in the server query:

```sql
SELECT t.region, s.total
FROM targets t JOIN (SELECT region, sum(amount) AS total FROM mssql.dbo.pg_sales GROUP BY region) s USING (region)
WHERE s.region = 'EU';
-- sent: SELECT … FROM [dbo].[pg_sales] … WHERE … = N'EU' … GROUP BY …   EU 40.00
```

Such a part is pushed only after DuckDB has moved the condition into it. Pushed earlier, the server would aggregate
the whole table and the condition would be applied afterwards in DuckDB — on 200 000 rows with an index on the column
that was 0.73 s instead of 0.08 s.

**S8 — pushed: a SQL Server UDF.** A scalar function of the catalog is called on the server, by its schema and name:

```sql
SELECT region, sum(mssql.dbo.pg_net(amount)) AS net FROM mssql.dbo.pg_sales GROUP BY region ORDER BY 1;
-- sent: SELECT … SUM(… [dbo].[pg_net]([t0].[amount]) …) … GROUP BY …   EU 32.00 / US 20.00
```

Without this the table came over and the function was called once per batch, `SELECT [dbo].[pg_net](@p0) UNION ALL
…`. The answer is the same either way: it is the server's function, it sees NULL arguments as before, and its result is
the server's value. It crosses as its schema and name, never by its bare name — a `dbo.abs` is called as `[dbo].[abs]`,
not as `ABS`. A function the provider implements itself (C#-authored, a plugin's, the Fabric API and CDC functions)
does not exist on the server and keeps its part local (L2). Table-valued functions are not covered yet.

**S9 — pushed: a LATERAL subquery with a `LIMIT` per row.** DuckDB decorrelates it before planning — the outer rows
become a CTE, the correlation a join with their distinct keys — and the server gets that CTE inlined, as one statement:

```sql
SELECT r.name, x.amount
FROM mssql.dbo.pg_regions r,
     LATERAL (SELECT s.amount FROM mssql.dbo.pg_sales s WHERE s.region = r.region AND s.amount >= 10
              ORDER BY s.amount DESC LIMIT 1) x;
-- one statement; the outer rows are read once per reference.   Americas 20.00 / Europe 30.00
```

⚠ **Reading the outer rows more than once is only allowed where one statement reads consistently.** DuckDB read that
CTE once; the server reads each copy separately, and at READ COMMITTED a concurrent writer can make them disagree —
measured: 28 of 300 000 rows lost their subquery result over 8 runs. So such a query runs where the database has
`READ_COMMITTED_SNAPSHOT`, at `SNAPSHOT` on a pooled connection where it allows snapshot isolation (and no transaction
holds a connection), inside a SERIALIZABLE / SNAPSHOT transaction — and stays in DuckDB otherwise
(`… reads the same rows more than once …` in the log).

A correlated **scalar** subquery is pushed too since 2026-10-31 (S16). A correlated subquery whose OUTER side is local
data stays as before: its remote part is sent uncorrelated.

**S10 — pushed: a table-valued function with constant arguments.** It is called by its schema and name inside the
server query, like a table:

```sql
SELECT r.name, s.id, s.amount
FROM mssql.dbo.pg_region_sales('EU') s CROSS JOIN mssql.dbo.pg_regions r WHERE r.region = 'EU';
-- sent: … FROM [dbo].[pg_region_sales](… 'EU' …) AS [t0] CROSS JOIN [dbo].[pg_regions] AS [t1] …
```

**S11 — per row: a table-valued function with a column argument.** `FROM t, f(t.a)` calls it once per distinct
argument; each chunk of them goes to the server as ONE `(VALUES …) CROSS APPLY [dbo].[f](…)`. `EXPLAIN` shows
`Fabricator Lateral Batched`:

```sql
SELECT t.region, s.id FROM targets t, mssql.dbo.pg_region_sales(t.region) s;   -- EU 1 / EU 3
```

**S12 — pushed: the same function per row of a table OF THE CATALOG.** Then the whole query is one statement, the
call a `CROSS APPLY` (a `LEFT JOIN LATERAL … ON true` keeps the rows without a match, as in DuckDB):

```sql
SELECT r.name, s.id FROM mssql.dbo.pg_regions r, mssql.dbo.pg_region_sales(r.region) s;
-- Europe 1 / Americas 2 / Europe 3 / Americas 4
-- sent: … FROM [dbo].[pg_regions] … CROSS APPLY [dbo].[pg_region_sales]([t2].[region] …) AS [t3] …
```

DuckDB plans such a call over the DISTINCT arguments and joins the result back, so the server reads the outer table
twice — like S9, only where it reads one statement consistently; elsewhere S11's per-chunk calls run.

**S13 — pushed: a count that reads no column.** `count(*)` needs no value, only the rows; the server counts them and
returns one number (until 2026-10-31 every row's key was read for it):

```sql
SELECT count(*) FROM mssql.dbo.pg_sales;
-- EXPLAIN: Fabricator Query.   4   sent: SELECT COUNT_BIG(*) AS [c0] FROM [dbo].[pg_sales] AS [t0]
```

`count(rowid)` or anything else that reads the row id keeps it, and runs as a scan.

**S14 — pushed: `||`, `trim`, `left` / `right`, a date cast of a timestamp.** These stayed in DuckDB until
2026-10-31. Each is translated to give DuckDB's answer: `||` returns NULL for a NULL operand (T-SQL `+`, where
`concat()` skips it), a `trim` without characters removes every Unicode space separator — NBSP and the ideographic
space too, never a tab — and `left` / `right` count code points:

```sql
SELECT id, region || '-' || note AS label, trim(note) AS t, left(note, 3) AS l
FROM mssql.dbo.pg_sales ORDER BY id LIMIT 3;
-- EXPLAIN: Fabricator Query.   1 EU-first first fir / 2 NULL NULL NULL / 3 EU-Größe Größe Grö
SELECT count(*) FROM mssql.dbo.pg_sales_eu WHERE CAST(loaded AS DATE) = DATE '2026-01-05';
-- EXPLAIN: Fabricator Query.   sent: … WHERE (CAST([t0].[loaded] AS date) = CAST('2026-01-05' AS date))
```

A negative count (`left(note, -1)`, all but the last character in DuckDB) stays local: T-SQL refuses it.
`ltrim` / `rtrim` need SQL Server 2022 or later to be pushed.

**S15 — names through `USE`.** After `USE mssql.dbo` (or `USE mssql`) the bare names are pushed like qualified ones —
DML too, which stayed in DuckDB until 2026-10-31. A bare name takes the schema `USE` / `search_path` gives it, as in
DuckDB; `mssql.main` is the database's default schema (`dbo`) unless a schema called `main` exists:

```sql
USE mssql.dbo;
SELECT region, sum(amount) FROM pg_sales GROUP BY region ORDER BY region;
-- EXPLAIN: Fabricator Query.   EU 40.00 / US 25.00
DELETE FROM pg_sales_eu WHERE id IN (SELECT id FROM pg_sales WHERE region = 'US');
-- EXPLAIN: Fabricator Remote Dml
USE memory;
```

One limit, DuckDB's: the check whether a bare name is a table of the catalog looks in `main`, i.e. the default
schema — a table only in another schema stays in DuckDB when named bare (it is pushed when named `schema.table`).

**S16 — pushed: a correlated scalar subquery.** A lookup or a count per row: DuckDB plans it as a join that allows at
most one matching row per outer row; the server gets it as an `OUTER APPLY` of the first matching row and how many
matched — one read per outer row — and more than one match is an error there as in DuckDB (SQL Server's error 512):

```sql
SELECT s.id, (SELECT r.name FROM mssql.dbo.pg_regions r WHERE r.region = s.region) AS name,
       (SELECT count(*) FROM mssql.dbo.pg_sales x WHERE x.region = s.region) AS same_region
FROM mssql.dbo.pg_sales s ORDER BY s.id;
-- EXPLAIN: Fabricator Query.   1 Europe 2 / 2 Americas 2 / 3 Europe 2 / 4 Americas 2
```

A top-1 per row (`(SELECT … ORDER BY … LIMIT 1)`) keeps that join in DuckDB: it becomes a ranking over the whole
table, which SQL Server would compute again for every outer row.

So does a subquery that matches on an EXPRESSION rather than a column (`WHERE x.id + x.id = s.id * 2`): DuckDB
can only join it with the outer keys inside the subquery, and SQL Server evaluates that again for every outer row
(measured 45 s against 0.27 s at 2 000 × 100 000 rows). A plain column needs no index: SQL Server builds a
temporary one (an index spool) once — on a rowstore and a clustered columnstore alike — and an index on the column
makes it a seek (20 000 × 1 000 000 rows: 1.0 s pushed with the index, 2.0 s without, 1.1 s in DuckDB).

**S17 — not pushed: ROLLUP and CUBE.** On an empty input SQL Server returns no row for any `GROUP BY` — ROLLUP,
CUBE, `GROUPING SETS ((), …)`, even `GROUP BY ()` — where the grand total `()` still yields one row in DuckDB (count
0). So a grouping-set list that holds `()` beside other sets stays in DuckDB; grouping sets without `()` are pushed:

```sql
SELECT region, count(*) FROM mssql.dbo.pg_sales WHERE amount > 1000 GROUP BY ROLLUP (region);
-- NULL 0   (pushed, SQL Server answered no row)
-- Fabricator.Remote | local: … grouping sets with the empty set (ROLLUP, CUBE) stay local …
SELECT region, sold, count(*) FROM mssql.dbo.pg_sales GROUP BY GROUPING SETS ((region), (sold));  -- pushed
```

**J1 — a table joined with local data reads only the matching rows.** DuckDB reads the local side of a hash join
first; its keys (an `IN` list, or their minimum and maximum) go into the WHERE of the scan of the server table, where
an index can seek on them. The scan checks every row it gets against the same filters again, so a case-insensitive or
padding collation on the server cannot add rows:

```sql
-- pg_clicks: 3000 rows, region cycling EU / US / AP (created with the example tables in the pinning suite)
SELECT t.region, count(*) FROM targets t JOIN mssql.dbo.pg_clicks c ON c.region = t.region GROUP BY t.region;  -- EU 1000
-- Fabricator.Sql: SELECT [region] FROM [dbo].[pg_clicks] WHERE [region] = @p0
-- (one key here; several give WHERE [region] IN (@p0, @p1, …), many a range)
-- (EXPLAIN shows the Fabricator Scan only: the filter is made while the query runs)
```

DuckDB plans such a filter only where the server table is worth filtering — not for the four rows of `pg_sales`.

Measured over 1M server rows and a 2-row local table: 1.36 s → 0.19 s (20 000 rows cross instead of 1 000 000).

**J2 — a part computed per local key runs for those keys only.** A Top-N or a scalar subquery per row of a local table
is pushed as one query (S5–S9); the join's keys then go around it, and SQL Server applies them below the window or
aggregate, at the index:

```sql
SELECT t.region, c.id FROM targets t,
       LATERAL (SELECT id FROM mssql.dbo.pg_clicks c WHERE c.region = t.region ORDER BY id DESC LIMIT 2) c;
-- Fabricator.Sql: … ) AS [q] WHERE [c1] = @p0 OPTION (RECOMPILE)
```

Measured over 1M server rows and 3 local keys: a Top-3 per key 0.27 s → 0.044 s, a scalar max and count per key
0.16 s → 0.048 s. For the column to be usable there it must be `varchar`/`char` (an `nvarchar` key is grouped by its
exact form only). With a NULL among the local keys of a scalar subquery DuckDB builds no key list (only a range), so
that query is computed for every key — the answer is the same.

Not as a part: a statement that WRITES a table of the catalog (its reads stay with the write on the catalog's own
connection), a part calling a function the provider implements itself (scalar or table) or a function of another
catalog, a part
calling a function whose value is not fixed by its arguments
(`now()`, `current_date`, `random()` — the server would use its own clock, time zone or generator; this holds for whole
statements too), time travel (`AT (…)`), and anything that does not translate exactly.

The full list of what translates — functions, window frames, date parts, string functions — and of what does not is
in the README, section *remote_pushdown*.

## INSERT … SELECT and CREATE TABLE … AS

**I1 — pushed.** The `SELECT` reads the same catalog, so the whole statement is one server statement and no row
travels:

```sql
INSERT INTO mssql.dbo.pg_sales_eu (id, amount)
SELECT id, amount FROM mssql.dbo.pg_sales WHERE region = 'EU';
-- EXPLAIN: FABRICATOR_REMOTE_DML.   returns 2 (changed rows)
-- T-SQL: INSERT INTO [dbo].[pg_sales_eu] ([id], [amount])
--        SELECT [t0].[id] AS [c0], [t0].[amount] AS [c1] FROM [dbo].[pg_sales] AS [t0] WHERE …
```

Only the named columns are written, so SQL Server fills `loaded` with its own `SYSDATETIME()` — exactly what the
regular insert does. It runs on the transaction's connection: inside `BEGIN … ROLLBACK` it is undone.

It stays on the regular insert when a server `INSERT` would behave differently from the bulk copy:

| Case | Why |
|---|---|
| the statement names an `IDENTITY` column | the bulk copy keeps the given values; a server `INSERT` would need `IDENTITY_INSERT` |
| the target has enabled triggers | a bulk copy fires none |
| the target is an external (PolyBase) table | its rows go to storage, not to SQL Server |
| `SET mssql_keep_nulls = false` | the bulk copy then writes a column's default for a NULL; an `INSERT` writes the NULL |
| `RETURNING`, `ON CONFLICT`, `DEFAULT VALUES`, a source outside the catalog | not translated |

**I2 — not pushed.** `INSERT … VALUES` keeps the bulk copy (`Mssql Net Insert`); there is nothing to save.

**C1 — pushed.** `CREATE TABLE … AS` over a pushed query: the table is created exactly as before (same column
types, same `WITH` options), then filled by one `INSERT … SELECT` on the server:

```sql
CREATE TABLE mssql.dbo.pg_totals AS
SELECT region, sum(amount) AS total FROM mssql.dbo.pg_sales GROUP BY region;
-- EXPLAIN: Fabricator Remote Ctas.   returns 2
```

**C2 — not pushed.** Over a plain scan the query is not pushed (S2), so neither is the CTAS: the rows stream through
DuckDB into a bulk copy (`Mssql Net Create Table As` over `Fabricator Scan`). The same happens with
`WITH (location = …)` (an external table) and a source outside the catalog.

## DELETE and UPDATE

A `DELETE` / `UPDATE` whose condition and new values translate exactly is ONE statement on the server — no scan, no
keys travel. DuckDB's statement is evaluated as the query over its target (`SELECT … FROM target WHERE …`), which is
translated like any pushed `SELECT`, and written as `DELETE [t0] FROM … WHERE …` / `UPDATE [t0] SET … FROM … WHERE …`.
A new value is first cast to the column's type exactly as DuckDB casts it when it assigns (a DECIMAL rounds into an
INTEGER column; T-SQL alone would truncate).

**D1 — pushed.** `DELETE FROM mssql.dbo.pg_sales WHERE sold < DATE '2026-01-01';` — `FABRICATOR_REMOTE_DML`.

**D2 — pushed: a string comparison.** `region = 'US'` matches only `'US'`, not `'us'` or `'US '` — DuckDB's
comparison, whatever the column's collation. The T-SQL carries the plain comparison too, so an index on the column
can still be used:

```sql
DELETE FROM mssql.dbo.pg_sales WHERE region = 'US';
-- T-SQL: DELETE [t0] FROM [dbo].[pg_sales] AS [t0]
--        WHERE (([t0].[region] = 'US') AND (<the same comparison in a binary collation>))
```

**D3 — pushed: a subquery over the catalog** becomes `WHERE EXISTS (…)` (`NOT IN` keeps its NULL semantics):

```sql
DELETE FROM mssql.dbo.pg_sales WHERE region IN (SELECT region FROM mssql.dbo.pg_regions WHERE name = 'Americas');
```

`DELETE … USING other_table WHERE …` is pushed the same way; a row the join matches twice is deleted, and counted, once.

**D4 — not pushed: a function without an exact T-SQL equivalent** (here `regexp_matches`): DuckDB scans the matching
keys and deletes by key (`Mssql Net Delete`). This needs a primary key, unique index or IDENTITY column; on a table
without one, use `fabricator_exec`.

**U1 — pushed.** `UPDATE mssql.dbo.pg_sales SET amount = amount * 2 WHERE id = 1;` — DECIMAL arithmetic, cast back
to `DECIMAL(10,2)`.

**U2 — not pushed: `UPDATE … FROM`.** A target row the join matches twice would get whichever match SQL Server takes,
where DuckDB's answer is fixed; it updates by key instead (`Mssql Net Update`).

**U3 — with `remote_pushdown 'false'`**, a `DELETE` / `UPDATE` whose predicate uses only integer, DECIMAL, BIT and DATE
comparisons (and whose `SET` values are constants, such columns or integer `+ - *`) is still one statement — the
older shortcut, `Fabricator Delete Where` / `Fabricator Update Where`. It also catches what the translation declines.

`RETURNING` keeps a `DELETE` / `UPDATE` on the key path — see the next section.

## RETURNING

Every DML statement returns its rows from the statement itself — T-SQL's `OUTPUT` clause — so there is no second
read and the rows are exactly the ones the statement changed:

| Statement | Where | Returns |
|---|---|---|
| `INSERT … RETURNING` (R1) | regular insert (`Mssql Net Insert`) | the inserted rows, defaults and `IDENTITY` values included |
| `DELETE … RETURNING` (R2) | scan, then delete by key (`Mssql Net Delete`) | the deleted rows; `rowid` is the key |
| `UPDATE … RETURNING` (R3) | scan, then update by key (`Mssql Net Update`) | the rows' NEW values |
| `MERGE … RETURNING` | by key, per action | each action's rows, with `merge_action` (`INSERT` / `UPDATE` / `DELETE`) |

```sql
DELETE FROM mssql.dbo.pg_sales WHERE id = 4 RETURNING id, note;          -- R2: 4 | late
UPDATE mssql.dbo.pg_sales SET amount = 1 WHERE id = 2 RETURNING id, amount; -- R3: 2 | 1.00
```

`RETURNING` keeps a `DELETE` / `UPDATE` on the key path: the one-statement forms return only a count.
On a table with an enabled trigger, where SQL Server refuses `OUTPUT` on its own, the rows go `OUTPUT … INTO` a table
variable that the same batch reads back — the trigger fires as for any other statement. Not on an external
(PolyBase) table: its writes go to storage, which returns no rows. **Fabric Warehouse** has no `OUTPUT` clause: there
a `DELETE` reads its rows just before deleting, an `UPDATE` just after updating (same batch, same transaction), and an
`INSERT` computes them from its input with the warehouse's own store conversions; an `UPDATE` that changes a key
column and an `INSERT` into an `IDENTITY` table are refused. Synapse dedicated refuses every `… RETURNING`
([warehouse-support.md §6.8b](warehouse-support.md#68-no-output-on-fabric-warehouse-so-no-returning-2026-10-29-user-raised-measured-live)).

**R4 — moving rows with a data-modifying CTE (DuckDB 2.0).** A CTE body can be a `DELETE … RETURNING`, and the
statement that follows consumes what it returned. When both tables belong to the same SQL Server catalog this is ONE
server statement — `DELETE … OUTPUT DELETED.<columns> INTO pg_archive (<columns>) FROM … WHERE …` — so the rows never
leave the server and the move is atomic in itself:

```sql
WITH moved AS MATERIALIZED (
    DELETE FROM mssql.dbo.pg_sales WHERE sold < DATE '2026-02-01' RETURNING *
)
INSERT INTO mssql.dbo.pg_archive SELECT * FROM moved;     -- 1 (row 1 moved); EXPLAIN: FABRICATOR_REMOTE_DML
```

The `WHERE` follows the rules of a pushed `DELETE` (strings, subqueries and `USING` included). `RETURNING` may name
columns (`RETURNING *`, `RETURNING id, note`); the `SELECT` picks them by position, and an `INSERT INTO t (c, …)`
column list is honoured.

**R5 — when the move runs in DuckDB instead.** Then it is the `DELETE … RETURNING` (R2) whose rows DuckDB inserts with
the regular insert — both in one transaction, so a `ROLLBACK` undoes both halves. That happens when:

| Case | Why |
|---|---|
| a moved column lands in a column of another type (e.g. `DECIMAL` into `INT`) | `OUTPUT INTO` converts the T-SQL way (truncates), DuckDB rounds |
| the target has an enabled trigger, a `FOREIGN KEY` (either side) or a `CHECK` constraint | T-SQL refuses `OUTPUT INTO` such a table |
| the target is the source itself, or either is an external (PolyBase) table | not translated |
| `RETURNING` computes a value (`RETURNING n + 1`), or the `SELECT` filters, groups or computes | only plain columns are moved |
| the `WHERE` does not translate exactly, or `SET fabricator_dml_pushdown = false` | as for any `DELETE` |

Other destinations: `COPY (WITH moved AS (DELETE … RETURNING *) SELECT * FROM moved) TO 'file.csv'` works (R2 plus
DuckDB's writer). Moving into a table of ANOTHER attached database — a Delta catalog, DuckDB's own tables — is refused
by DuckDB itself: one transaction writes to one attached database (*"a single transaction can only write to a single
attached database"*), and nothing is deleted. ⚠ DuckDB does not take a `MERGE` as a CTE body.

## MERGE

`MERGE INTO` works on every SQL Server table with a row id (a primary key, a unique index or an IDENTITY column).
When its parts translate exactly it runs as ONE T-SQL `MERGE` (`EXPLAIN`: `FABRICATOR_REMOTE_DML`); otherwise DuckDB
joins source and target and applies each action by key (`Mssql Net Update` / `Delete` / `Insert`).

**M1 — one statement.** Any number of `WHEN` clauses, `WHEN NOT MATCHED BY SOURCE` included, with DuckDB's rule that
the first clause whose condition holds wins:

```sql
MERGE INTO mssql.dbo.pg_sales AS t
USING (SELECT id, amount FROM mssql.dbo.pg_sales_eu) AS src ON t.id = src.id
WHEN MATCHED AND src.amount > 100 THEN DELETE
WHEN MATCHED THEN UPDATE SET amount = src.amount
WHEN NOT MATCHED THEN INSERT (id, region, amount) VALUES (src.id, 'EU', src.amount);
```

T-SQL's own `MERGE` takes at most one UPDATE and one DELETE per match class, so the statement is not translated
clause by clause: one query over target and source decides, per row, the action and the new values, and a fixed
`MERGE … ON <key> WHEN MATCHED AND op = 1 THEN UPDATE … WHEN MATCHED AND op = 2 THEN DELETE WHEN NOT MATCHED AND op = 3
THEN INSERT …` applies it. The `ON` and `WHEN` conditions and the values follow the rules of a pushed `SELECT`:
strings compare exactly as in DuckDB (no trailing-space padding, case-sensitive), a value is converted the DuckDB way
(`2.5` into an `INT` column is `3`, not T-SQL's `2`). A target row that two source rows would change is refused, as
DuckDB refuses it — T-SQL would quietly delete such a row.

**M2 — when the MERGE runs in DuckDB instead:**

| Case | Why |
|---|---|
| `RETURNING` | the key path returns each action's rows (see [RETURNING](#returning)) |
| a condition or value does not translate exactly (e.g. `\|\|`), or `SET fabricator_dml_pushdown = false` | as for any pushed statement |
| `THEN ERROR`, `UPDATE` without `SET`, `INSERT` without values, `DEFAULT`, two `INSERT` clauses naming different columns | not translated |
| the target has an enabled trigger | a T-SQL `MERGE` fires it per action; the key path fires UPDATE / DELETE triggers per statement and none for its bulk insert |
| an `INSERT` names the `IDENTITY` column, or `SET mssql_keep_nulls = false` | the INSERT rules (see [INSERT … SELECT](#insert--select-and-create-table--as)) |
| the source is made of constants only (`USING (SELECT 1 AS id)`) | DuckDB's Substrait producer cannot write a SELECT without FROM |
| the target is an external (PolyBase) table, or the catalog is a warehouse (no pushdown there) | not translated |
