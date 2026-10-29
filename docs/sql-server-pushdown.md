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
| `INSERT … SELECT` | the `SELECT` reads tables of the same catalog and translates exactly (I1) | `FABRICATOR_REMOTE_DML` | `Mssql Net Insert`: DuckDB streams the rows into a bulk copy — and still pushes the `SELECT` when worth it |
| `INSERT … VALUES` | never — a bulk copy is already the fast path (I2) | — | `Mssql Net Insert` |
| `CREATE TABLE … AS` | its query is pushed (C1) | `Fabricator Remote Ctas` | `Mssql Net Create Table As`: rows stream into a bulk copy (C2) |
| `DELETE` | the whole `WHERE` is exact: integer, DECIMAL, BIT, DATE comparisons, `IN`, `BETWEEN`, `IS NULL`, `AND`/`OR` (D1) | `Fabricator Delete Where` | `Mssql Net Delete`: scan the keys, delete by key (D2) |
| `UPDATE` | as `DELETE`, and every `SET` value is a constant, an exact column, or integer `+ - *` (U2) | `Fabricator Update Where` | `Mssql Net Update`: scan, update by key (U1) |
| `… RETURNING` | not as one pushed statement: `DELETE` / `UPDATE` take the key path, the rows come from T-SQL `OUTPUT` | `Mssql Net Delete` / `Update` / `Insert` | see [RETURNING](#returning) |
| `MERGE` | — | — | merge by key, see [MERGE](#merge) |

## Seeing what happens

- **`EXPLAIN <statement>`** — the operator names in the table above.
- **`FABRICATOR_DUMP_REMOTE=1`** (environment variable) prints every pushdown decision and the T-SQL sent, e.g.
  `[fabricator remote] remote: INSERT INTO [dbo].[pg_sales_eu] …` or `[fabricator remote] local: a plain scan keeps
  the table scan`.
- **`FABRICATOR_DUMP_DML=1`** does the same for `DELETE` / `UPDATE` / `CREATE TABLE … AS`, e.g.
  `[fabricator dml] rowid path: the predicate does not render exactly`.

## Switching it off

| Scope | How | Effect |
|---|---|---|
| one catalog | `ATTACH … (TYPE fabricator, remote_pushdown 'false')` | no `SELECT` / `INSERT … SELECT` / `CREATE TABLE … AS` pushdown |
| every new ATTACH | environment `FABRICATOR_REMOTE_PUSHDOWN=false` | the default for ATTACHes that do not name the option |
| the session, per statement | `SET fabricator_dml_pushdown = false;` … `RESET fabricator_dml_pushdown;` | `DELETE`, `UPDATE`, `INSERT … SELECT` and `CREATE TABLE … AS` use the regular paths; `SELECT` pushdown is unaffected |

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
SELECT id FROM mssql.dbo.pg_sales WHERE regexp_matches(note, '^f') GROUP BY id;
-- EXPLAIN: Hash Group By -> Filter -> Fabricator Scan.   trace: Could not find function "regexp_matches"
```

**S4 — not pushed: a local table in the join.** DuckDB pushes whole queries only; a remote table joined with local
data is scanned, and the join runs in DuckDB:

```sql
CREATE TEMP TABLE targets AS SELECT 'EU' AS region, 25 AS target;
SELECT t.region, sum(s.amount) - t.target AS gap
FROM mssql.dbo.pg_sales s JOIN targets t USING (region) GROUP BY t.region, t.target;
-- EXPLAIN: Hash Join over Fabricator Scan.   EU 15.00
```

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

**D1 — pushed.** The whole `WHERE` translates exactly, so it is one `DELETE … WHERE` on the server — no scan, no keys:

```sql
DELETE FROM mssql.dbo.pg_sales WHERE sold < DATE '2026-01-01';
-- EXPLAIN: Fabricator Delete Where
```

**D2 — not pushed: a string comparison.** `region = 'US'` depends on the column's collation on SQL Server (`'us'`,
`'US '` may match there), so DuckDB scans the matching keys and deletes by key (`Mssql Net Delete`). This needs a
primary key or unique index; on a table without one, use `fabricator_exec`.

**U1 — not pushed: DECIMAL arithmetic in SET.** `SET amount = amount * 2` on a DECIMAL column is not rendered (scale
and rounding would have to match DuckDB's exactly), so it updates by key (`Mssql Net Update`).

**U2 — pushed.** `UPDATE … SET amount = 12.50 WHERE id = 1`: a constant and an exact predicate, one `UPDATE … WHERE`
(`Fabricator Update Where`).

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

`RETURNING` keeps a `DELETE` / `UPDATE` off the one-statement shortcut (D1, U2): that shortcut returns only a count.
On a table with an enabled trigger, where SQL Server refuses `OUTPUT` on its own, the rows go `OUTPUT … INTO` a table
variable that the same batch reads back — the trigger fires as for any other statement. Not on an external
(PolyBase) table: its writes go to storage, which returns no rows.

**R4 — moving rows with a data-modifying CTE (DuckDB 2.0).** A CTE body can be a `DELETE … RETURNING`, and the
statement that follows consumes what it returned:

```sql
WITH moved AS MATERIALIZED (
    DELETE FROM mssql.dbo.pg_sales WHERE sold < DATE '2026-02-01' RETURNING *
)
INSERT INTO mssql.dbo.pg_archive SELECT * FROM moved;     -- 1 (row 1 moved)
```

Today it runs as the `DELETE … OUTPUT DELETED.*` (R2), whose rows DuckDB then inserts with the regular insert — both
in one transaction, so a `ROLLBACK` undoes both halves. Planned: the whole statement as ONE server statement
(`DELETE … OUTPUT DELETED.* INTO pg_archive`), so the rows never leave the server. ⚠ DuckDB does not take a `MERGE`
as a CTE body.

## MERGE

`MERGE INTO` works on every SQL Server table with a row id (a primary key, a unique index or an IDENTITY column): DuckDB joins source and target and
applies each action by key. It is not pushed as a T-SQL `MERGE` (Substrait, the plan format the translation uses,
has no MERGE, and T-SQL's `MERGE` differs in places that have to be measured first). `MERGE … RETURNING` works
(see [RETURNING](#returning)).
