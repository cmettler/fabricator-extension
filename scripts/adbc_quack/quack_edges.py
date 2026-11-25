"""The pushdown edge cases (scripts/pushdown_edge_cases.py) against a quack server, through the adbc provider with
dialect 'duckdb': rp = the remote tables on the quack server, db = the same rows in the local DuckDB.

    python -I quack_edges.py <managed dir> [case ...]
"""
import importlib.util
import os
import socket
import subprocess
import sys
import tempfile
import time
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
MANAGED = sys.argv[1]
ONLY = set(sys.argv[2:])
os.environ["FABRICATOR_MANAGED_DIR"] = MANAGED
import duckdb  # noqa: E402

spec = importlib.util.spec_from_file_location("edges", REPO / "scripts" / "pushdown_edge_cases.py")
edges = importlib.util.module_from_spec(spec)
spec.loader.exec_module(edges)

# a DuckDB 2.0.1 CLI that can INSTALL quack (the server), the fabricator loadable, and quack-net's native driver
DUCKDB_CLI = Path(os.environ.get("QUACK_DUCKDB_CLI", str(REPO / "build" / "release-v2" / "duckdb.exe")))
LOADABLE = Path(os.environ.get("QUACK_EDGES_LOADABLE", str(REPO / "build" / "release-v2" / "extension" / "fabricator" / "fabricator.duckdb_extension")))
DRIVER = Path(os.environ["QUACK_ADBC_DRIVER"])
TOKEN = "edge-token"

TABLES = """
CREATE TABLE ed_t (id INTEGER PRIMARY KEY, i INTEGER, j INTEGER, b BIGINT, ti UTINYINT, si SMALLINT,
  d DECIMAL(10,2), d1 DECIMAL(1,1), f DOUBLE, s VARCHAR, v VARCHAR, c VARCHAR, flag BOOLEAN, dt DATE, ts TIMESTAMP, g INTEGER);
INSERT INTO ed_t VALUES
 (1, 7, 2, 9000000000, 5, 300, 2.50, 0.5, 2.5, 'Abc', 'Abc', 'ab', true, '2024-01-31', '2024-01-31 10:00:00', 1),
 (2, -7, 2, -1, 0, -300, -2.50, -0.5, -2.5, 'abc', 'abc', 'ab  ', false, '2024-02-29', '2024-02-29 23:59:59.999999', 1),
 (3, 5, 0, 2147483647, 255, 32767, 0.00, 0.0, 0.0, 'abc  ', 'abc  ', 'AB', NULL, '2023-12-31', '1970-01-01 00:00:00', 2),
 (4, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 2),
 (5, 2147483647, 1, 2147483647, 1, 1, 1.49, 0.1, -0.0, 'École', 'Ecole', 'x', true, '2024-03-15', '2024-03-15 12:30:45.5', 3),
 (6, 3, -2, 7, 2, 2, 3.56, 0.9, 1e308, '', '', '', false, '0001-01-01', '0001-01-01 00:00:00', 3),
 (7, 3, 3, 7, 2, 2, 1.01, 0.3, 0.1, 'a%b', 'a_b', 'a', true, '9999-12-31', '9999-12-31 23:59:59', NULL);
CREATE TABLE ed_u (id INTEGER PRIMARY KEY, i INTEGER, s VARCHAR);
INSERT INTO ed_u VALUES (1, 7, 'Abc'), (2, 7, 'abc'), (3, NULL, 'x'), (4, 5, NULL), (5, 3, 'abc  ');
"""


# DuckDB-only shapes: types Substrait does not have, DuckDB's own functions and syntax
EXTRA = [
    ("x cast utinyint", "SELECT sum(CAST(i AS UTINYINT)) FROM {t} WHERE i BETWEEN -10 AND 10"),
    ("x cast utinyint ok", "SELECT g, sum(CAST(j AS UTINYINT)) FROM {t} WHERE j >= 0 GROUP BY g"),
    ("x utinyint col", "SELECT g, sum(ti), max(ti), min(ti) FROM {t} GROUP BY g"),
    ("x hugeint", "SELECT g, sum(b * CAST(1000000000000 AS HUGEINT)) FROM {t} GROUP BY g"),
    ("x list agg", "SELECT g, list(i ORDER BY id) FROM {t} GROUP BY g"),
    ("x interval agg", "SELECT g, max(ts - TIMESTAMP '2000-01-01') FROM {t} GROUP BY g"),
    ("x regexp", "SELECT g, count(*) FILTER (WHERE regexp_matches(s, '^a')) FROM {t} GROUP BY g"),
    ("x strftime group", "SELECT strftime(dt, '%Y') AS y, count(*) FROM {t} GROUP BY y"),
    ("x qualify", "SELECT id FROM {t} QUALIFY row_number() OVER (PARTITION BY g ORDER BY id) = 1"),
    ("x distinct on", "SELECT DISTINCT ON (g) g, id FROM {t} ORDER BY g, id"),
    ("x median", "SELECT g, median(i), quantile_cont(f, 0.5) FROM {t} WHERE abs(f) < 1e9 GROUP BY g"),
    ("x arg_max", "SELECT g, arg_max(id, i), arg_min(s, id) FROM {t} GROUP BY g"),
    ("x stddev", "SELECT g, stddev_samp(i), var_pop(f) FROM {t} WHERE abs(f) < 1e9 GROUP BY g"),
    ("x string_agg", "SELECT g, string_agg(s, ',' ORDER BY id) FROM {t} GROUP BY g"),
    ("x rollup", "SELECT g, flag, count(*), grouping(g) FROM {t} GROUP BY ROLLUP (g, flag)"),
    ("x date_part agg", "SELECT year(dt) AS y, count(*), max(dayname(dt)) FROM {t} GROUP BY y"),
    ("x round agg", "SELECT g, round(avg(d), 1), sum(d * d) FROM {t} GROUP BY g"),
    ("x left join computed", "SELECT t.id, x.one, x.y, count(*) OVER () FROM {t} t LEFT JOIN (SELECT id, 1 AS one, coalesce(i, 0) AS y FROM {u}) x ON t.id = x.id"),
    ("x full join computed", "SELECT t.id, x.one FROM (SELECT id, 2 AS two FROM {t}) t FULL JOIN (SELECT id + 3 AS id, 1 AS one FROM {u}) x ON t.id = x.id GROUP BY ALL"),
]
edges.CASES = edges.CASES + EXTRA


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def main():
    port = free_port()
    with tempfile.TemporaryDirectory(prefix="quack-edges-") as work:
        Path(work, "init.sql").write_text(
            "INSTALL quack; LOAD quack;\n"
            f"SELECT * FROM quack_serve('quack:127.0.0.1:{port}', token => '{TOKEN}', disable_ssl => true);\n" + TABLES,
            encoding="utf-8")
        log = open(Path(work, "server.log"), "w", encoding="utf-8")
        server = subprocess.Popen([str(DUCKDB_CLI), "edges.duckdb", "-init", "init.sql"], cwd=work,
                                  stdin=subprocess.PIPE, stdout=log, stderr=subprocess.STDOUT, text=True)
        try:
            deadline = time.monotonic() + 60
            while True:
                try:
                    socket.create_connection(("127.0.0.1", port), timeout=1).close()
                    break
                except OSError:
                    if time.monotonic() > deadline or server.poll() is not None:
                        log.flush()
                        sys.exit("server did not start:\n" + Path(work, "server.log").read_text())
                    time.sleep(0.3)
            time.sleep(1)  # the tables are created after quack_serve
            run(port)
        finally:
            server.stdin.close()
            try:
                server.wait(timeout=20)
            except subprocess.TimeoutExpired:
                server.kill()
            log.close()


def run(port):
    con = duckdb.connect(config={"allow_unsigned_extensions": "true"})
    con.execute(f"LOAD '{LOADABLE}'")
    con.execute(TABLES)
    uri = f"quack://127.0.0.1:{port}/?token={TOKEN}&tls=false"
    con.execute(f"ATTACH 'driver={DRIVER};uri=\"{uri}\"' AS rp (TYPE fabricator, PROVIDER 'adbc')")
    cat = con.execute("SELECT DISTINCT table_catalog || '.' || table_schema FROM information_schema.tables "
                      "WHERE table_catalog = 'rp'").fetchall()
    schema = cat[0][0] if cat else "rp.main"
    con.execute("CALL enable_logging(['Fabricator.Remote'], storage := 'memory', level := 'debug')")
    bad = pushed_n = 0
    n = 0
    for name, tpl in edges.CASES:
        if ONLY and name not in ONLY:
            continue
        n += 1
        con.execute("CALL truncate_duckdb_logs()")
        got, gerr = edges.run(con, tpl.format(t=f"{schema}.ed_t", u=f"{schema}.ed_u"))
        logs = [m for (m,) in con.execute(
            "SELECT message FROM duckdb_logs WHERE type = 'Fabricator.Remote'").fetchall()]
        pushed = any(m.startswith("remote") for m in logs)
        pushed_n += pushed
        ref, rerr = edges.run(con, tpl.format(t="memory.main.ed_t", u="memory.main.ed_u"))
        if gerr or rerr:
            same = bool(gerr) == bool(rerr)
            status = "ok (both error)" if same else f"ERROR MISMATCH  pushed: {gerr}  local: {rerr}"
        else:
            status = "ok" if got == ref else "DIFF"
        if not status.startswith("ok"):
            bad += 1
        print(f"{'PUSHED' if pushed else 'local ':6} {status[:8]:8} {name}", flush=True)
        if status == "DIFF":
            print(f"        pushed: {got[:6]}\n        local:  {ref[:6]}")
        elif not status.startswith("ok"):
            print(f"        {status}")
        if not pushed and ONLY:
            print("        " + " | ".join(m[:300] for m in logs[:4]))
    print(f"\n{n} cases, {pushed_n} pushed, {bad} not ok")
    # control: a session setting the written SQL depends on differs from the server's -> nothing may be pushed
    con.execute("SET TimeZone = 'America/New_York'")
    q = f"SELECT g, sum(i) FROM {schema}.ed_t GROUP BY g"
    con.execute("CALL truncate_duckdb_logs()")
    got, gerr = edges.run(con, q)
    logs = [m for (m,) in con.execute("SELECT message FROM duckdb_logs WHERE type = 'Fabricator.Remote'").fetchall()]
    ref, _ = edges.run(con, q.replace(f"{schema}.", "memory.main."))
    print("TimeZone control:", "PUSHED" if any(m.startswith("remote") for m in logs) else "local",
          "ok" if got == ref and not gerr else f"WRONG {gerr}", "|", " | ".join(m[:200] for m in logs[:2]))
    con.execute("RESET TimeZone")


if __name__ == "__main__":
    main()
