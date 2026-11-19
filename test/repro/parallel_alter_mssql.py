"""Repro: ALTER TABLE in parallel DuckDB transactions through the SQL Server provider fails.

What dbt-duckdb does for an incremental model with `on_schema_change='sync_all_columns'` and threads > 1,
without dbt: N threads, one DuckDB cursor each (= one DuckDB transaction each), every thread altering its OWN
table on the attached SQL Server and then writing to the new column in the SAME transaction. Found in
elt-airflow's grist_mirror (36 incremental MERGE models, dbt-duckdb 1.11, `threads: 4`; its CLAUDE.md trap 7).

    python parallel_alter_mssql.py <mode> [threads] [rounds]
    python parallel_alter_mssql.py --clean                      # drop every table of the schema

    mode  dbt    dbt-duckdb's statements for one model, as captured from its debug log (2026-11-19): BEGIN,
                 a fabricator_exec hook, a local TEMP table, DESCRIBE x2, three ALTER ... ADD COLUMN, MERGE INTO
                 ... USING the temp table, the hook again, COMMIT
           add   BEGIN; ALTER TABLE t ADD COLUMN c; UPDATE t SET c = ...; COMMIT        (the smallest failing shape)
           type  dbt's alter_column_type: BEGIN; ADD COLUMN n__dbt_alter VARCHAR; UPDATE SET n__dbt_alter = n;
                 DROP COLUMN n; RENAME COLUMN n__dbt_alter TO n; COMMIT
           block ONE thread, two cursors, no race: A: BEGIN; ALTER TABLE ta ADD COLUMN c  — then B (autocommit):
                 UPDATE tb (ANOTHER table). B never returns (threads/rounds ignored).

Needs FABRICATOR_EXTENSION (the .duckdb_extension) and MSSQL_HOST/_PORT/_DATABASE/_USER/_PASSWORD (a SQL login).
Writes only to schema `fab_par_alter` of MSSQL_DATABASE; table names carry the PID. Every round starts from a
freshly created table with a unique index on id (serial, before the threads start; REPRO_FRESH_NAMES=1 = a new
name every round instead of drop + re-create — fails the same). Exit 0 = every thread committed every round and
the tables look as expected, 1 = a thread raised or a table is wrong, 2 = a thread hung.

MEASURED 2026-11-19 — Windows build build/release-v2 (fabricator_version() 0.2.6), DuckDB 2.0.0 (Python), the
docker SQL Server 2025 `mssql-fabricator`; the same errors under dbt with the Linux 0.2.6 artifact:

    dbt   1 thread  x 2 rounds   OK
    dbt   4 threads x 3 rounds   3 of 3 rounds failed, 8 of 12 transactions raised:
          "column 'column 3' of 'p…_0' is BIGINT on the provider but VARCHAR in the catalog — the table was
          changed outside this session"
    add   1 thread  x 2          OK
    add   4 threads x 3          2 of 3 failed: "Binder Error: Referenced update column hd not found in table!"
                                 (4x) and "execute_update failed: 207: Invalid column name 'hd'" (2x)
    add   2 threads x 5          4 of 5 failed (207 'hd')
    type  1 thread  x 2          OK
    type  4 threads x 3          2 of 3 failed: "execute_dml failed: 207: Invalid column name 'n__dbt_alter'"
    Typically ONE transaction of a round commits and the others raise; round 0 often passes.

  With two cursors in a FIXED order on one thread (no race) every interleaving tried is clean — A's own ALTER +
  UPDATE + COMMIT with B, between them, doing ALTER on another table (COMMIT, autocommit or ROLLBACK) or CREATE
  TABLE. So the column errors need real concurrency. The one fixed order that does NOT return is `block`:
  B's UPDATE on tb waits on A's open DDL transaction. Read from SQL Server while it hung:

    session  blocked_by  wait     wait_s  sql
    62       60          LCK_M_S  309     SELECT CAST(SUM(p.rows) AS VARCHAR(32)) AS n FROM sys.partitions p
                                          WHERE p.object_id = OBJECT_ID(N'fab_par_alter.tb') AND p.index_id IN (0, 1)
    60  sleeping, open_transaction_count 1  ← A, its last statement a column-metadata read after the ALTER

  i.e. the row-count read for ANOTHER table (no NOLOCK, unlike the per-table read-back of the 0.2.3 fix
  550610b) blocks on the uncommitted ALTER. Under threads it is a wait until A commits — and a hang SQL Server
  cannot see whenever A waits for something B holds inside the process (the shape of the 2026-11-03 deadlock).
  Whether this wait is also behind the column errors is NOT shown.
"""

import os
import sys
import threading
import time

import duckdb

SCHEMA = "fab_par_alter"
ROWS = 250
FRESH = os.environ.get("REPRO_FRESH_NAMES") == "1"   # a new table name every round (no drop + re-create)
TIMEOUT = 120
CLEAN = sys.argv[1:2] == ["--clean"]
MODES = ("dbt", "add", "type", "block")
if not CLEAN and (len(sys.argv) < 2 or sys.argv[1] not in MODES):
    sys.exit(__doc__)
MODE = None if CLEAN else sys.argv[1]
THREADS = int(sys.argv[2]) if len(sys.argv) > 2 else 4
ROUNDS = int(sys.argv[3]) if len(sys.argv) > 3 else 3
PREFIX = f"p{os.getpid()}_"
e = os.environ


def lit(v: str) -> str:
    return "'" + v.replace("'", "''") + "'"


con = duckdb.connect(config={"allow_unsigned_extensions": "true"})
con.execute(f"LOAD {lit(e['FABRICATOR_EXTENSION'])}")
con.execute("CREATE SECRET t (TYPE mssql, host {}, port {}, database {}, user {}, password {})".format(
    *(lit(e[k]) for k in ("MSSQL_HOST", "MSSQL_PORT", "MSSQL_DATABASE", "MSSQL_USER", "MSSQL_PASSWORD"))))
con.execute("ATTACH '' AS mssql (TYPE fabricator, SECRET t)")
con.execute(f"CREATE SCHEMA IF NOT EXISTS mssql.{SCHEMA}")


def tables() -> list[str]:
    return [r[0] for r in con.execute("SELECT table_name FROM duckdb_tables() WHERE database_name = 'mssql' "
                                      f"AND schema_name = '{SCHEMA}' ORDER BY 1").fetchall()]


if CLEAN:
    for name in tables():
        con.execute(f'DROP TABLE mssql.{SCHEMA}."{name}"')
        print("dropped", name)
    sys.exit(0)

print(f"fabricator {con.execute('SELECT fabricator_version()').fetchone()[0]}, duckdb {duckdb.__version__}, "
      f"mode {MODE}, {THREADS} threads x {ROUNDS} rounds", flush=True)

if MODE == "block":
    ta, tb = f"mssql.{SCHEMA}.{PREFIX}a", f"mssql.{SCHEMA}.{PREFIX}b"
    for t in (ta, tb):
        con.execute(f"DROP TABLE IF EXISTS {t}")
        con.execute(f"CREATE TABLE {t} AS SELECT range AS id, 'v' || range AS a FROM range(10)")
        con.execute(f"SELECT fabricator_exec('mssql', 'CREATE UNIQUE CLUSTERED INDEX ux_id ON "
                    f"{SCHEMA}.{t.rsplit('.', 1)[1]} (id)')")
    a, b = con.cursor(), con.cursor()
    a.execute("BEGIN")
    a.execute(f"ALTER TABLE {ta} ADD COLUMN c VARCHAR")
    print("A: BEGIN; ALTER TABLE ta ADD COLUMN c   (open)", flush=True)

    def hung() -> None:
        print(f"HUNG: B's UPDATE on tb has not returned after {TIMEOUT // 4}s — see sys.dm_exec_requests "
              "(blocking_session_id) and sys.dm_exec_sessions (open_transaction_count) now; tables "
              f"{PREFIX}a/b are left, --clean drops them", flush=True)
        os._exit(2)

    guard = threading.Timer(TIMEOUT // 4, hung)
    guard.start()
    b.execute(f"UPDATE {tb} SET a = 'x'")
    guard.cancel()
    print("B: UPDATE tb returned — not blocked", flush=True)
    a.execute("ROLLBACK")
    for t in (ta, tb):
        con.execute(f"DROP TABLE IF EXISTS {t}")
    print("OK")
    sys.exit(0)


def index_hook(name: str) -> str:
    """The dbt pre/post hook: a unique index on id if there is none (fabricator's MERGE needs one)."""
    t = f"[{SCHEMA}].[{name}]"
    return (f"SELECT fabricator_exec('mssql', 'IF OBJECT_ID(N''{t}'') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "
            f"sys.indexes WHERE object_id = OBJECT_ID(N''{t}'') AND is_unique = 1) "
            f"CREATE UNIQUE CLUSTERED INDEX [ux_id] ON {t} ([id])')")


def setup(name: str) -> None:
    """Serial, autocommit: the table as the previous dbt run left it."""
    t = f"mssql.{SCHEMA}.{name}"
    con.execute(f"DROP TABLE IF EXISTS {t}")
    con.execute(f"CREATE TABLE {t} AS SELECT range AS id, 'v' || range AS a, range * 10 AS n FROM range({ROWS})")
    con.execute(index_hook(name))


def transaction(cur, name: str, r: int) -> None:
    t = f'"mssql"."{SCHEMA}"."{name}"'
    if MODE == "dbt":
        tmp = f'"{name}__dbt_tmp{r}"'
        cur.execute("BEGIN")
        cur.execute(index_hook(name))
        cur.execute(f"CREATE TEMPORARY TABLE {tmp} AS SELECT range AS id, 'v' || range AS a, range * 10 AS n, "
                    f"md5('v' || range) AS hd, 's' AS hd_schema, now() AS ldts FROM range({ROWS})")
        cur.execute(f"SELECT column_name, column_type FROM (DESCRIBE {tmp})").fetchall()
        cur.execute(f"SELECT column_name, column_type FROM (DESCRIBE {t})").fetchall()
        cur.execute(f'ALTER TABLE {t} ADD COLUMN "hd" character varying(256)')
        cur.execute(f'ALTER TABLE {t} ADD COLUMN "hd_schema" character varying(256)')
        cur.execute(f'ALTER TABLE {t} ADD COLUMN "ldts" TIMESTAMP WITH TIME ZONE')
        cur.execute(f"""MERGE INTO {t} AS DBT_INTERNAL_DEST USING {tmp} AS DBT_INTERNAL_SOURCE
            ON (DBT_INTERNAL_SOURCE.id = DBT_INTERNAL_DEST.id)
            WHEN MATCHED AND (DBT_INTERNAL_DEST."hd" IS DISTINCT FROM DBT_INTERNAL_SOURCE."hd"
                              OR DBT_INTERNAL_DEST."hd_schema" IS DISTINCT FROM DBT_INTERNAL_SOURCE."hd_schema")
                THEN UPDATE BY NAME
            WHEN NOT MATCHED THEN INSERT BY NAME
            WHEN NOT MATCHED BY SOURCE THEN DELETE""")
        cur.execute(index_hook(name))
        cur.execute("COMMIT")
        cur.execute(f"DROP TABLE IF EXISTS {tmp}")
    elif MODE == "add":
        cur.execute("BEGIN")
        cur.execute(f'ALTER TABLE {t} ADD COLUMN "hd" VARCHAR')
        cur.execute(f"UPDATE {t} SET \"hd\" = md5(a)")
        cur.execute("COMMIT")
    else:  # type: dbt's default__alter_column_type, BIGINT n -> VARCHAR
        cur.execute("BEGIN")
        cur.execute(f'ALTER TABLE {t} ADD COLUMN "n__dbt_alter" VARCHAR')
        cur.execute(f'UPDATE {t} SET "n__dbt_alter" = "n"')
        cur.execute(f'ALTER TABLE {t} DROP COLUMN "n"')
        cur.execute(f'ALTER TABLE {t} RENAME COLUMN "n__dbt_alter" TO "n"')
        cur.execute("COMMIT")


EXPECTED = {   # (columns, a check that every row carries the new value)
    "dbt": ("id,a,n,hd,hd_schema,ldts", "count(*) FILTER (WHERE hd = md5(a) AND ldts IS NOT NULL)"),
    "add": ("id,a,n,hd", "count(*) FILTER (WHERE hd = md5(a))"),
    "type": ("id,a,n", "count(*) FILTER (WHERE n = (id * 10)::VARCHAR)"),
}[MODE]

errors: dict[str, str] = {}
done: list[str] = []


def table_name(i: int, r: int) -> str:
    return f"{PREFIX}{i}_r{r}" if FRESH else f"{PREFIX}{i}"


def worker(i: int, r: int, barrier: threading.Barrier) -> None:
    name = table_name(i, r)
    cur = con.cursor()
    try:
        barrier.wait()
        transaction(cur, name, r)
        done.append(name)
    except Exception as ex:
        errors[f"{name} round {r}"] = f"{type(ex).__name__}: {str(ex).splitlines()[0]}"
        try:
            cur.execute("ROLLBACK")
        except Exception:
            pass


start = time.time()
failed_rounds = 0
for r in range(ROUNDS):
    for i in range(THREADS):
        setup(table_name(i, r))
    done.clear()
    barrier = threading.Barrier(THREADS)
    threads = [threading.Thread(target=worker, args=(i, r, barrier), daemon=True) for i in range(THREADS)]
    t0 = time.time()
    for th in threads:
        th.start()
    for th in threads:
        th.join(max(0.0, TIMEOUT - (time.time() - t0)))
    hung = [th for th in threads if th.is_alive()]
    if hung:
        print(f"HUNG in round {r} after {TIMEOUT}s: {len(hung)} of {THREADS} threads still running", flush=True)
        os._exit(2)

    # Read back serially, from a fresh view of the catalog.
    con.execute("SELECT fabricator_refresh_cache('mssql')")
    wrong = []
    for name in sorted(done):
        cols = ",".join(c for (c,) in con.execute(
            "SELECT column_name FROM duckdb_columns() WHERE database_name = 'mssql' "
            f"AND schema_name = '{SCHEMA}' AND table_name = '{name}' ORDER BY column_index").fetchall())
        good = con.execute(f'SELECT {EXPECTED[1]} FROM mssql.{SCHEMA}."{name}"').fetchone()[0]
        if cols != EXPECTED[0] or good != ROWS:
            wrong.append(f"{name}: columns {cols}, {good} of {ROWS} rows as expected")
    failed = THREADS - len(done)
    failed_rounds += bool(failed or wrong)
    print(f"round {r}: {len(done)} of {THREADS} committed, {failed} raised, {len(wrong)} wrong "
          f"({time.time() - t0:.1f}s)", flush=True)
    for w in wrong:
        print("  WRONG", w, flush=True)

for key, err in sorted(errors.items()):
    print(f"ERROR {key}: {err}", flush=True)
print(f"{'OK' if not failed_rounds else 'FAILED'}: {failed_rounds} of {ROUNDS} rounds failed "
      f"({time.time() - start:.1f}s)")
sys.exit(1 if failed_rounds else 0)
