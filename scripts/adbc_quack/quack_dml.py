"""DML through the adbc provider against a quack server: each case runs on rp (the server's tables) and on the same
tables in the local DuckDB, then both tables are compared. Also: read-your-writes in a transaction, ROLLBACK, COMMIT.

    python -I quack_dml.py <managed dir>      (QUACK_EDGES_LOADABLE = the loadable)
"""
import importlib.util
import os
import sys
from pathlib import Path

spec = importlib.util.spec_from_file_location("qe", Path(__file__).with_name("quack_edges.py"))
qe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(qe)

RESET = """
CREATE OR REPLACE TABLE {s}dm_t AS SELECT id, i, g, s FROM {s}ed_t;
CREATE OR REPLACE TABLE {s}dm_u AS SELECT id, i, s FROM {s}ed_u;
CREATE OR REPLACE TABLE {s}dm_log (id INTEGER, s VARCHAR);
"""

CASES = [
    ("delete where", ["DELETE FROM {s}dm_t WHERE i > 5"]),
    ("delete null", ["DELETE FROM {s}dm_t WHERE i IS NULL OR s = 'abc'"]),
    ("update", ["UPDATE {s}dm_t SET s = upper(s), i = i * 2 WHERE g = 1"]),
    ("update all", ["UPDATE {s}dm_t SET g = coalesce(g, 0) + 1"]),
    ("insert select", ["INSERT INTO {s}dm_log SELECT id, s FROM {s}dm_t WHERE i < 5"]),
    ("delete using", ["DELETE FROM {s}dm_t USING {s}dm_u WHERE dm_t.i = dm_u.i AND dm_u.s = 'abc'"]),
    ("delete in subquery", ["DELETE FROM {s}dm_t WHERE i IN (SELECT i FROM {s}dm_u WHERE s IS NOT NULL)"]),
    ("txn commit", ["BEGIN", "DELETE FROM {s}dm_t WHERE g = 2", "UPDATE {s}dm_t SET s = 'x' WHERE g = 3", "COMMIT"]),
]


def tables(con, s):
    return {t: sorted(map(repr, con.execute(f"SELECT * FROM {s}{t}").fetchall()))
            for t in ("dm_t", "dm_u", "dm_log")}


def run(port):
    con = qe.duckdb.connect(config={"allow_unsigned_extensions": "true"})
    con.execute(f"LOAD '{qe.LOADABLE}'")
    con.execute(qe.TABLES)
    uri = f"quack://127.0.0.1:{port}/?token={qe.TOKEN}&tls=false"
    con.execute(f"ATTACH 'driver={qe.DRIVER};uri=\"{uri}\"' AS rp (TYPE fabricator, PROVIDER 'adbc')")
    remote = con.execute("SELECT DISTINCT table_catalog || '.' || table_schema FROM information_schema.tables "
                         "WHERE table_catalog = 'rp'").fetchall()[0][0] + "."
    local = "memory.main."
    con.execute("CALL enable_logging(['Fabricator.Remote'], storage := 'memory', level := 'debug')")
    bad = 0

    def reset():
        con.execute(RESET.format(s=local))
        # the server's copies, made on the server (through the provider's raw SQL)
        for st in filter(None, (x.strip() for x in RESET.format(s="").split(";"))):
            con.execute("SELECT fabricator_exec('rp', ?)", [st])
        con.execute("SELECT fabricator_refresh_cache('rp')")

    for name, statements in CASES:
        reset()
        con.execute("CALL truncate_duckdb_logs()")
        err = None
        try:
            for st in statements:
                con.execute(st.format(s=remote))
        except Exception as e:  # noqa: BLE001
            err = str(e).splitlines()[0][:200]
            try:
                con.execute("ROLLBACK")
            except Exception:  # noqa: BLE001
                pass
        logs = [m for (m,) in con.execute("SELECT message FROM duckdb_logs WHERE type = 'Fabricator.Remote'").fetchall()]
        for st in statements:
            con.execute(st.format(s=local))
        same = tables(con, remote) == tables(con, local)
        ok = same and err is None
        bad += not ok
        pushed = any(m.startswith("remote") for m in logs)
        print(f"{'PUSHED' if pushed else 'local ':6} {'ok' if ok else 'BAD':4} {name}" + (f"  error: {err}" if err else ""))
        if not same:
            print("   remote:", tables(con, remote)["dm_t"][:5])
            print("   local: ", tables(con, local)["dm_t"][:5])
        if not pushed:
            print("   " + " | ".join(m[:220] for m in logs[:3]))

    # read-your-writes and ROLLBACK
    reset()
    before = con.execute(f"SELECT count(*) FROM {remote}dm_t").fetchone()[0]
    con.execute("BEGIN")
    con.execute(f"DELETE FROM {remote}dm_t WHERE g = 1")
    inside = con.execute(f"SELECT count(*) FROM {remote}dm_t").fetchone()[0]
    inside_sum = con.execute(f"SELECT g, count(*) FROM {remote}dm_t GROUP BY g ORDER BY g").fetchall()
    con.execute("ROLLBACK")
    after = con.execute(f"SELECT count(*) FROM {remote}dm_t").fetchone()[0]
    deleted = con.execute(f"SELECT count(*) FROM {local}dm_t WHERE g = 1").fetchone()[0]
    ok = inside == before - deleted and after == before and all(g != 1 for g, _ in inside_sum)
    bad += not ok
    print(f"{'ok' if ok else 'BAD'} read-your-writes + rollback: before {before}, inside {inside} {inside_sum}, after {after}")
    print(f"\n{len(CASES) + 1} cases, {bad} not ok")


qe.run = run
qe.main()
