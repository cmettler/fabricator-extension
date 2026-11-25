"""Captures fabricator's Substrait plans for a few queries over the edge tables, for quack-net's tests.

    python -I capture_plans.py <loadable> <managed dir> <out dir>
"""
import base64
import importlib.util
import os
import sys
from pathlib import Path

LOADABLE, MANAGED, OUT = sys.argv[1], sys.argv[2], Path(sys.argv[3])
os.environ["QUACK_EDGES_LOADABLE"] = LOADABLE
spec = importlib.util.spec_from_file_location("qe", Path(__file__).with_name("quack_edges.py"))
qe = importlib.util.module_from_spec(spec)
sys.argv = [sys.argv[0], MANAGED]
spec.loader.exec_module(qe)

QUERIES = {
    "aggregate": "SELECT g, sum(i) AS total, count(*) AS n FROM {s}.ed_t WHERE i > 0 GROUP BY g ORDER BY g",
    "join": "SELECT t.id, u.s FROM {s}.ed_t t JOIN {s}.ed_u u ON t.i = u.i WHERE t.id < 5 ORDER BY t.id, u.s",
    "any_value": "SELECT g, any_value(s) FROM {s}.ed_t GROUP BY g",
    "delete": "DELETE FROM {s}.ed_t WHERE i > 5",
    "leftjoin": "SELECT t.id, x.one, x.y FROM {s}.ed_t t LEFT JOIN (SELECT id, 1 AS one, coalesce(i, 0) AS y FROM {s}.ed_u) x ON t.id = x.id ORDER BY t.id LIMIT 100",
    "merge": "MERGE INTO {s}.ed_u t USING (SELECT id, s FROM {s}.ed_t) src ON t.id = src.id WHEN MATCHED THEN UPDATE SET s = src.s WHEN NOT MATCHED THEN INSERT (id, s) VALUES (src.id, src.s)",
    "update": "UPDATE {s}.ed_t SET s = upper(s), i = i * 2 WHERE g = 1",
}


def run(port):
    con = qe.duckdb.connect(config={"allow_unsigned_extensions": "true"})
    con.execute(f"LOAD '{qe.LOADABLE}'")
    uri = f"quack://127.0.0.1:{port}/?token={qe.TOKEN}&tls=false"
    con.execute(f"ATTACH 'driver={qe.DRIVER};uri=\"{uri}\"' AS rp (TYPE fabricator, PROVIDER 'adbc')")
    schema = con.execute("SELECT DISTINCT table_catalog || '.' || table_schema FROM information_schema.tables "
                         "WHERE table_catalog = 'rp'").fetchall()[0][0]
    OUT.mkdir(parents=True, exist_ok=True)
    # the extension reads FABRICATOR_DUMP_SUBSTRAIT_PLAN from its own C runtime, which sees the process start's
    # environment only: one query per process, named by CAPTURE
    name = os.environ["CAPTURE"]
    target = Path(os.environ["FABRICATOR_DUMP_SUBSTRAIT_PLAN"])
    for name, q in [(name, QUERIES[name])]:
        rows = con.execute(q.format(s=schema)).fetchall()
        data = target.read_bytes()
        print(name, len(data), "bytes", rows[:3])
        (OUT / f"{name}.b64").write_text(base64.b64encode(data).decode(), encoding="ascii")


qe.run = run
qe.main()
