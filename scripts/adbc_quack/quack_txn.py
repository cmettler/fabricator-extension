"""An explicit transaction on a quack server reads one snapshot from its first read; a scan buffers only when
the connection is needed while it is open. Every check prints PASS/FAIL.

    python -I quack_txn.py <managed dir>
"""
import importlib.util
import os
import sys
import tempfile
import threading
import time
from pathlib import Path

LOG = Path(tempfile.gettempdir()) / f"fab_snapshot_{os.getpid()}.log"
os.environ["FABRICATOR_LOG_LEVEL"] = "Debug"
os.environ["FABRICATOR_LOG_FILE"] = str(LOG)

spec = importlib.util.spec_from_file_location("qe", Path(__file__).with_name("quack_edges.py"))
qe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(qe)

BIG = int(os.environ.get("PROBE_BIG", "2000000"))
fails = 0


def check(name, ok, detail=""):
    global fails
    fails += not ok
    print(f"{'PASS' if ok else 'FAIL'} {name}" + (f"  ({detail})" if detail else ""), flush=True)


def log_lines():
    try:
        return LOG.read_text(encoding="utf-8", errors="replace").splitlines()
    except FileNotFoundError:
        return []


def buffered_since(mark):
    return [l for l in log_lines()[mark:] if "pinned scan buffered" in l]


def run(port):
    uri = f"quack://127.0.0.1:{port}/?token={qe.TOKEN}&tls=false"

    # ONE database (the host query knows the last one loaded), clients are its cursors
    con = qe.duckdb.connect(config={"allow_unsigned_extensions": "true"})
    con.execute(f"LOAD '{qe.LOADABLE}'")
    con.execute(f"ATTACH 'driver={qe.DRIVER};uri=\"{uri}\"' AS rp (TYPE fabricator, PROVIDER 'adbc')")
    con.execute(f"ATTACH 'driver={qe.DRIVER};uri=\"{uri}\"' AS rq (TYPE fabricator, PROVIDER 'adbc', transaction_snapshot false)")

    def connect():
        return con.cursor()

    for st in [f"CREATE OR REPLACE TABLE sn_t AS SELECT range::INTEGER AS id, range % 10 AS g FROM range(100)",
               f"CREATE OR REPLACE TABLE sn_big AS SELECT range AS id, range % 100 AS g FROM range({BIG})",
               "CREATE OR REPLACE TABLE sn_u AS SELECT range::INTEGER AS id FROM range(50)"]:
        con.execute("SELECT fabricator_exec('rp', ?)", [st])
    con.execute("SELECT fabricator_refresh_cache('rp')")
    r = con.execute("SELECT DISTINCT table_catalog || '.' || table_schema FROM information_schema.tables "
                    "WHERE table_catalog = 'rp'").fetchall()[0][0] + "."
    q = r.replace("rp.", "rq.", 1)
    other = connect()  # a second client, writing on its own connection (autocommit on the server)

    def count(c):
        return c.execute(f"SELECT count(*) FROM {r}sn_t").fetchone()[0]

    def other_insert(v):
        other.execute(f"INSERT INTO {r}sn_t VALUES ({v}, 0)")

    # 1. explicit read transaction: one snapshot
    base = count(con)
    con.execute("BEGIN")
    first = count(con)
    other_insert(1000)
    second = count(con)
    con.execute("COMMIT")
    after = count(con)
    check("read txn sees one snapshot", first == base and second == first and after == base + 1,
          f"base {base}, first {first}, after other commit {second}, after COMMIT {after}")

    # 2. the control: transaction_snapshot false reads fresh before the first write
    con2 = connect()
    con2.execute("BEGIN")
    first = con2.execute(f"SELECT count(*) FROM {q}sn_t").fetchone()[0]
    other_insert(1001)
    second = con2.execute(f"SELECT count(*) FROM {q}sn_t").fetchone()[0]
    con2.execute("COMMIT")
    check("control: transaction_snapshot false sees the other commit", second == first + 1,
          f"first {first}, second {second}")
    con2.close()

    # 3. autocommit is unchanged: each statement sees what is committed
    a = count(con)
    other_insert(1002)
    check("autocommit sees the other commit", count(con) == a + 1)

    # 4. read-your-writes after a read in the same transaction, and ROLLBACK
    con.execute("BEGIN")
    a = count(con)
    con.execute(f"INSERT INTO {r}sn_t VALUES (2000, 1)")
    b = count(con)
    con.execute("ROLLBACK")
    check("read, write, read your write, rollback", b == a + 1 and count(con) == a, f"{a} -> {b} -> {count(con)}")

    con.execute("SET fabricator_subtree_pushdown = false")  # scans stay scans: the host joins them
    # 4b. a lookup of a table that is not there yet must not abort the server transaction (CREATE TABLE asks first)
    err = None
    try:
        con.execute("BEGIN")
        con.execute(f"INSERT INTO {r}sn_t VALUES (3000, 1)")
        con.execute(f"CREATE TABLE {r}sn_new (a INTEGER)")
        con.execute(f"INSERT INTO {r}sn_new VALUES (1)")
        got = con.execute(f"SELECT count(*) FROM {r}sn_new").fetchone()[0]
        con.execute("COMMIT")
    except Exception as e:  # noqa: BLE001
        err = str(e).splitlines()[0][:160]
        got = None
        con.execute("ROLLBACK")
    check("write, then CREATE TABLE in the same transaction", err is None and got == 1, err or "")
    con.execute(f"DROP TABLE IF EXISTS {r}sn_new")
    # 5. a big scan in a read transaction streams: nothing buffered; two statements one after the other: nothing
    mark = len(log_lines())
    con.execute("BEGIN")
    s1 = con.execute(f"SELECT sum(id + 0), count(*) FROM {r}sn_big WHERE id + 1 > 0").fetchone()
    s2 = con.execute(f"SELECT count(*) FROM {r}sn_t").fetchone()
    con.execute("COMMIT")
    check("big scan in a read txn, then another: nothing buffered", not buffered_since(mark) and s1[1] == BIG,
          f"{buffered_since(mark)[:2]}")

    # 6. a join of two remote tables in a read txn that is not pushed (a local table in it): does it buffer?
    con.execute(f"CREATE OR REPLACE TABLE loc AS SELECT range::INTEGER AS id FROM range(0, 100, 2)")
    mark = len(log_lines())
    con.execute("BEGIN")
    j = con.execute(f"SELECT count(*) FROM {r}sn_t t JOIN {r}sn_u u ON t.id + 0 = u.id + 0 JOIN loc ON loc.id = t.id").fetchone()[0]
    con.execute("COMMIT")
    ref = con.execute(f"SELECT count(*) FROM {r}sn_t t JOIN {r}sn_u u ON t.id = u.id JOIN loc ON loc.id = t.id").fetchone()[0]
    print(f"     join of two remote scans: buffered {buffered_since(mark)}", flush=True)
    check("join of two remote scans in a read txn is right", j == ref, f"{j} vs {ref}")

    # 7. a join of a big remote table with another remote one: what buffers (rows)
    mark = len(log_lines())
    con.execute("BEGIN")
    j = con.execute(f"SELECT count(*) FROM {r}sn_big b JOIN {r}sn_u u ON b.g + 0 = u.id + 0 JOIN loc ON loc.id = u.id").fetchone()[0]
    con.execute("COMMIT")
    print(f"     big x small: buffered {buffered_since(mark)}", flush=True)
    check("big x small join in a read txn is right", j == BIG // 100 * 25, f"{j}")

    # 7b. POSITIVE CONTROL: a load in the transaction while its scan of the same connection is open must buffer it
    con.execute(f"SELECT fabricator_exec('rp', 'CREATE OR REPLACE TABLE sn_o (id BIGINT, s VARCHAR)')")
    con.execute("SELECT fabricator_refresh_cache('rp')")
    mark = len(log_lines())
    con.execute("BEGIN")
    con.execute(f"INSERT INTO {r}sn_o VALUES (-1, 'x')")
    con.execute(f"INSERT INTO {r}sn_o SELECT b.id, repeat('x', 40) || b.id FROM {r}sn_big b JOIN loc ON loc.id = b.g")
    n = con.execute(f"SELECT count(*) FROM {r}sn_o").fetchone()[0]
    con.execute("COMMIT")
    print(f"     load while scanning: buffered {buffered_since(mark)}", flush=True)
    check("positive control: a load during a scan buffers the scan, rows right",
          bool(buffered_since(mark)) and n == 1 + BIG // 100 * 50, f"{n}")
    con.execute("RESET fabricator_subtree_pushdown")

    # 8. write-write conflict: the snapshot is older than the other commit, both update the same row
    con.execute("BEGIN")
    count(con)
    other.execute(f"UPDATE {r}sn_t SET g = 7 WHERE id = 5")
    err = None
    try:
        con.execute(f"UPDATE {r}sn_t SET g = 8 WHERE id = 5")
        con.execute("COMMIT")
    except Exception as e:  # noqa: BLE001
        err = str(e).splitlines()[0][:160]
        try:
            con.execute("ROLLBACK")
        except Exception:  # noqa: BLE001
            pass
    g = con.execute(f"SELECT g FROM {r}sn_t WHERE id = 5").fetchone()[0]
    print(f"     conflict: error={err!r}, g={g}", flush=True)
    check("a conflicting update after the snapshot fails, the other's value stays", err is not None and g == 7)

    # 9. concurrency: 4 clients, each a read transaction with a big scan, at once
    clients = [connect() for _ in range(4)]
    out = [None] * 4

    def work(i):
        c = clients[i]
        c.execute("BEGIN")
        out[i] = c.execute(f"SELECT count(*), sum(g) FROM {r}sn_big").fetchone()
        c.execute("COMMIT")
    ts = [threading.Thread(target=work, args=(i,)) for i in range(4)]
    t0 = time.perf_counter()
    [t.start() for t in ts]
    [t.join(120) for t in ts]
    check("4 concurrent read transactions", all(o == out[0] and o is not None for o in out),
          f"{time.perf_counter() - t0:.2f}s {out[0]}")
    print(f"fails: {fails}")


qe.run = run
qe.main()
