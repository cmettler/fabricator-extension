"""Scale and concurrency checks of the adbc provider against a quack server (needs psutil).

  overhead   latency of point queries (a filtered scan) and of a pushed aggregate
  memory     peak working set of a large scan in autocommit vs. in a transaction after a write (drained there)
  bulk       CTAS from local rows (ingest into a new table) vs. INSERT into an existing one (through a temporary table)
  conflict   two transactions updating the same row: the second COMMIT must fail, no update may be lost

    python -I quack_scale.py <managed dir> [section ...]
"""
import importlib.util
import sys
import time
from pathlib import Path

import psutil

spec = importlib.util.spec_from_file_location("qe", Path(__file__).with_name("quack_edges.py"))
qe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(qe)
SECTIONS = set(sys.argv[2:]) or {"overhead", "memory", "bulk", "conflict"}
ROWS = 2_000_000


def peak_mb():
    return psutil.Process().memory_info().peak_wset / 2**20


def run(port):
    con = qe.duckdb.connect(config={"allow_unsigned_extensions": "true"})
    con.execute(f"LOAD '{qe.LOADABLE}'")
    uri = f"quack://127.0.0.1:{port}/?token={qe.TOKEN}&tls=false"
    con.execute(f"ATTACH 'driver={qe.DRIVER};uri=\"{uri}\"' AS rp (TYPE fabricator, PROVIDER 'adbc')")
    s = con.execute("SELECT DISTINCT table_catalog || '.' || table_schema FROM information_schema.tables "
                    "WHERE table_catalog = 'rp'").fetchall()[0][0]
    con.execute("SELECT fabricator_exec('rp', ?)", [
        f"CREATE OR REPLACE TABLE big AS SELECT range AS id, range % 100 AS g, 'value ' || range AS s FROM range({ROWS})"])
    con.execute("SELECT fabricator_exec('rp', ?)", ["CREATE OR REPLACE TABLE w (id INTEGER PRIMARY KEY, v INTEGER)"])
    con.execute("SELECT fabricator_exec('rp', ?)", ["INSERT INTO w VALUES (1, 0), (2, 0)"])
    con.execute("SELECT fabricator_refresh_cache('rp')")
    con.execute("CREATE TABLE memory.main.small AS SELECT range AS g FROM range(3)")

    if "overhead" in SECTIONS:
        t = time.perf_counter()
        for k in range(20):
            con.execute(f"SELECT * FROM {s}.big WHERE id = {k * 99991}").fetchall()
        point = (time.perf_counter() - t) / 20 * 1000
        t = time.perf_counter()
        for _ in range(5):
            con.execute(f"SELECT g, count(*), max(s) FROM {s}.big GROUP BY g").fetchall()
        agg = (time.perf_counter() - t) / 5 * 1000
        print(f"overhead: point query {point:.1f} ms, pushed aggregate over {ROWS} rows {agg:.1f} ms")

    if "memory" in SECTIONS:
        q = f"SELECT count(*), sum(length(b.s)) FROM {s}.big b JOIN memory.main.small l ON b.g % 50 = l.g"
        base = peak_mb()
        t = time.perf_counter()
        a = con.execute(q).fetchall()
        auto_s = time.perf_counter() - t
        after_auto = peak_mb()
        con.execute("BEGIN")
        con.execute(f"UPDATE {s}.w SET v = v + 1 WHERE id = 2")  # the write that pins the transaction
        t = time.perf_counter()
        b = con.execute(q).fetchall()
        txn_s = time.perf_counter() - t
        after_txn = peak_mb()
        con.execute("ROLLBACK")
        print(f"memory: autocommit {auto_s:.1f} s, peak {base:.0f} -> {after_auto:.0f} MB; "
              f"in a transaction after a write {txn_s:.1f} s, peak -> {after_txn:.0f} MB; same answer {a == b} {a}")

    if "bulk" in SECTIONS:
        con.execute(f"CREATE OR REPLACE TABLE memory.main.src AS SELECT range AS id, range % 7 AS g, 'row ' || range AS s FROM range({ROWS // 2})")
        t = time.perf_counter()
        con.execute(f"CREATE TABLE {s}.bulk_a AS SELECT * FROM memory.main.src")
        ctas = time.perf_counter() - t
        con.execute(f"CREATE TABLE {s}.bulk_b (id BIGINT, g BIGINT, s VARCHAR)")
        t = time.perf_counter()
        con.execute(f"INSERT INTO {s}.bulk_b SELECT * FROM memory.main.src")
        insert = time.perf_counter() - t
        counts = con.execute("SELECT * FROM fabricator_query('rp', ?)",
                             ["SELECT (SELECT count(*) FROM bulk_a), (SELECT count(*) FROM bulk_b)"]).fetchall()
        print(f"bulk: {ROWS // 2} rows, CTAS {ctas:.1f} s, INSERT into an existing table {insert:.1f} s, counts {counts}")

    if "conflict" in SECTIONS:
        c1, c2 = con.cursor(), con.cursor()
        c1.execute("BEGIN")
        c2.execute("BEGIN")
        c1.execute(f"UPDATE {s}.w SET v = v + 1 WHERE id = 1")
        outcome = []
        try:
            c2.execute(f"UPDATE {s}.w SET v = v + 10 WHERE id = 1")
            outcome.append("second UPDATE ran")
        except Exception as e:  # noqa: BLE001
            outcome.append("second UPDATE failed: " + str(e).splitlines()[0][:120])
        for name, c in (("first", c1), ("second", c2)):
            try:
                c.execute("COMMIT")
                outcome.append(f"{name} COMMIT ok")
            except Exception as e:  # noqa: BLE001
                outcome.append(f"{name} COMMIT failed: " + str(e).splitlines()[0][:120])
                try:
                    c.execute("ROLLBACK")
                except Exception:  # noqa: BLE001
                    pass
        v = con.execute("SELECT * FROM fabricator_query('rp', 'SELECT v FROM w WHERE id = 1')").fetchall()
        print("conflict:", " | ".join(outcome), "| final v =", v, "(1 or 10 if one won, 11 = both: a lost check)")


qe.run = run
qe.main()
