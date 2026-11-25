"""Statements that read and write the same quack session in one statement / transaction: do they finish, and right?

    python -I quack_overlap.py <managed dir> [case ...]
"""
import importlib.util
import os
import sys
import threading
import time
from pathlib import Path

spec = importlib.util.spec_from_file_location("qe", Path(__file__).with_name("quack_edges.py"))
qe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(qe)

N = int(os.environ.get("PROBE_ROWS", "200000"))

RESET = f"""
CREATE OR REPLACE TABLE ov_s AS SELECT range::INTEGER AS id, range % 7 AS g, 'v' || range AS v FROM range({N});
CREATE OR REPLACE TABLE ov_o (id INTEGER, v VARCHAR);
"""

CASES = [
    ("insert select join local (autocommit)",
     ["INSERT INTO {r}ov_o SELECT s.id, s.v FROM {r}ov_s s JOIN l USING (id)"],
     "SELECT count(*), sum(id) FROM {r}ov_o"),
    ("ctas join local (autocommit)",
     ["CREATE OR REPLACE TABLE {r}ov_c AS SELECT s.id, s.v FROM {r}ov_s s JOIN l USING (id)"],
     "SELECT count(*), sum(id) FROM {r}ov_c"),
    ("update from local (autocommit)",
     ["UPDATE {r}ov_s s SET v = l.w FROM l WHERE s.id = l.id"],
     "SELECT count(*) FILTER (WHERE v LIKE 'w%'), count(*) FROM {r}ov_s"),
    ("txn: write, then insert select join local",
     ["BEGIN", "INSERT INTO {r}ov_o VALUES (-1, 'x')",
      "INSERT INTO {r}ov_o SELECT s.id, s.v FROM {r}ov_s s JOIN l USING (id)", "COMMIT"],
     "SELECT count(*), sum(id) FROM {r}ov_o"),
    ("txn: write, then join of two remote scans",
     ["BEGIN", "INSERT INTO {r}ov_o VALUES (3, 'x')",
      "CREATE TEMP TABLE res AS SELECT count(*) AS n FROM {r}ov_s s JOIN {r}ov_o o ON s.id + 0 = o.id + 0 JOIN l ON l.id = s.id", "COMMIT"],
     "SELECT n FROM res"),
    ("txn: write, then insert local union all remote",
     ["BEGIN", "INSERT INTO {r}ov_o VALUES (-1, 'x')",
      "INSERT INTO {r}ov_o SELECT id, w FROM l UNION ALL SELECT id, v FROM {r}ov_s", "COMMIT"],
     "SELECT count(*), sum(id) FROM {r}ov_o"),
    ("autocommit: insert local union all remote",
     ["INSERT INTO {r}ov_o SELECT id, w FROM l UNION ALL SELECT id, v FROM {r}ov_s"],
     "SELECT count(*), sum(id) FROM {r}ov_o"),
]


def run(port):
    con = qe.duckdb.connect(config={"allow_unsigned_extensions": "true"})
    con.execute(f"LOAD '{qe.LOADABLE}'")
    uri = f"quack://127.0.0.1:{port}/?token={qe.TOKEN}&tls=false"
    con.execute(f"ATTACH 'driver={qe.DRIVER};uri=\"{uri}\"' AS rp (TYPE fabricator, PROVIDER 'adbc')")
    con.execute(f"CREATE TABLE l AS SELECT range::INTEGER * 3 AS id, 'w' || range AS w FROM range({N // 3})")
    for st in filter(None, (x.strip() for x in RESET.split(";"))):
        con.execute("SELECT fabricator_exec('rp', ?)", [st])
    con.execute("SELECT fabricator_refresh_cache('rp')")
    r = con.execute("SELECT DISTINCT table_catalog || '.' || table_schema FROM information_schema.tables "
                    "WHERE table_catalog = 'rp'").fetchall()[0][0] + "."
    only = sys.argv[2:]
    for name, statements, check in CASES * int(os.environ.get('PROBE_REPEAT', '1')):
        if only and not any(o in name for o in only):
            continue
        for st in filter(None, (x.strip() for x in RESET.split(";"))):
            con.execute("SELECT fabricator_exec('rp', ?)", [st])
        con.execute("SELECT fabricator_exec('rp', 'DROP TABLE IF EXISTS ov_c')")
        con.execute("SELECT fabricator_refresh_cache('rp')")
        con.execute("DROP TABLE IF EXISTS res")
        done = threading.Event()

        def watchdog():
            if not done.wait(60):
                print(f"HANG   {name}", flush=True)
                os._exit(3)
        threading.Thread(target=watchdog, daemon=True).start()
        t = time.perf_counter()
        err = None
        try:
            for st in statements:
                con.execute(st.format(r=r))
            got = con.execute(check.format(r=r)).fetchall()
        except Exception as e:  # noqa: BLE001
            err = str(e).splitlines()[0][:240]
            got = None
            try:
                con.execute("ROLLBACK")
            except Exception:  # noqa: BLE001
                pass
        done.set()
        print(f"{time.perf_counter() - t:6.2f}s {name}: {got if err is None else 'ERROR ' + err}", flush=True)


qe.run = run
qe.main()
