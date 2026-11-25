"""ALTER TABLE, MERGE INTO and INSERT … RETURNING through the adbc provider against a quack server, each compared with
the same statements on a local copy.

    python -I quack_alter_merge.py <managed dir>      (QUACK_EDGES_LOADABLE = the loadable)
"""
import importlib.util
from pathlib import Path

spec = importlib.util.spec_from_file_location("qe", Path(__file__).with_name("quack_edges.py"))
qe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(qe)

SETUP = [
    "CREATE OR REPLACE TABLE {s}am_t (id INTEGER, k VARCHAR, v INTEGER, h HUGEINT)",
    "INSERT INTO {s}am_t VALUES (1, 'a', 10, 1), (2, 'b', 20, 2), (3, NULL, 30, 3), (4, 'd', NULL, 4)",
    "CREATE OR REPLACE TABLE {s}am_m (id INTEGER PRIMARY KEY, k VARCHAR, v INTEGER)",
    "INSERT INTO {s}am_m VALUES (1, 'a', 10), (2, 'b', 20), (3, NULL, 30), (4, 'd', NULL)",
    "CREATE OR REPLACE TABLE {s}am_src (id INTEGER, k VARCHAR, v INTEGER, del BOOLEAN)",
    "INSERT INTO {s}am_src VALUES (1, 'a', 11, false), (2, 'b', 0, true), (5, 'e', 50, false), (3, NULL, 33, false)",
]

ALTERS = [
    ("add column", ["ALTER TABLE {s}am_t ADD COLUMN u UUID", "ALTER TABLE {s}am_t ADD COLUMN w HUGEINT DEFAULT 7"]),
    ("rename column", ["ALTER TABLE {s}am_t RENAME COLUMN v TO value"]),
    ("drop column", ["ALTER TABLE {s}am_t DROP COLUMN h"]),
    ("type", ["ALTER TABLE {s}am_t ALTER COLUMN v TYPE BIGINT"]),
    ("not null", ["ALTER TABLE {s}am_t ALTER COLUMN id SET NOT NULL", "ALTER TABLE {s}am_t ALTER COLUMN id DROP NOT NULL"]),
    ("default", ["ALTER TABLE {s}am_t ALTER COLUMN v SET DEFAULT 99", "INSERT INTO {s}am_t (id) VALUES (9)"]),
    ("rename table", ["ALTER TABLE {s}am_t RENAME TO am_t2", "ALTER TABLE {s}am_t2 RENAME TO am_t"]),
]

MERGES = [
    ("merge upsert", ["MERGE INTO {s}am_m t USING {s}am_src s ON t.id = s.id "
                      "WHEN MATCHED AND s.del THEN DELETE WHEN MATCHED THEN UPDATE SET v = s.v "
                      "WHEN NOT MATCHED THEN INSERT (id, k, v) VALUES (s.id, s.k, s.v)"]),
    ("merge by source", ["MERGE INTO {s}am_m t USING {s}am_src s ON t.id = s.id "
                         "WHEN MATCHED THEN UPDATE SET k = s.k WHEN NOT MATCHED BY SOURCE THEN DELETE"]),
    ("merge in txn", ["BEGIN", "MERGE INTO {s}am_m t USING {s}am_src s ON t.id = s.id WHEN MATCHED THEN DELETE", "COMMIT"]),
    ("merge update joined", ["UPDATE {s}am_m SET v = s.v FROM {s}am_src s WHERE am_m.id = s.id AND NOT s.del"]),
    ("merge update key", ["UPDATE {s}am_m SET id = id + 10 WHERE id = 4"]),
]
# on am_t, which has no key: the provider's row identity is then DuckDB's own rowid
KEYLESS = [
    ("rowid update joined", ["UPDATE {s}am_t SET v = s.v FROM {s}am_src s WHERE am_t.id = s.id AND NOT s.del"]),
    ("rowid merge", ["MERGE INTO {s}am_t t USING {s}am_src s ON t.id = s.id "
                     "WHEN MATCHED AND s.del THEN DELETE WHEN MATCHED THEN UPDATE SET v = s.v "
                     "WHEN NOT MATCHED THEN INSERT (id, k, v) VALUES (s.id, s.k, s.v)"]),
    ("rowid in txn", ["BEGIN", "DELETE FROM {s}am_t WHERE id = 1", "UPDATE {s}am_t SET k = s.k FROM {s}am_src s WHERE am_t.id = s.id", "COMMIT"]),
]
KEYLESS_RETURNING = [
    ("rowid delete returning", "DELETE FROM {s}am_t WHERE v > 15 RETURNING *"),
    ("rowid update returning", "UPDATE {s}am_t SET v = coalesce(v, 0) + 1 WHERE id < 4 RETURNING *"),
]
RETURNING = [
    ("delete returning", "DELETE FROM {s}am_m WHERE v > 15 RETURNING *"),
    ("update returning", "UPDATE {s}am_m SET v = coalesce(v, 0) + 1 WHERE id < 4 RETURNING *"),
    ("update joined returning", "UPDATE {s}am_m SET k = s.k FROM {s}am_src s WHERE am_m.id = s.id RETURNING *"),
]


def run(port):
    con = qe.duckdb.connect(config={"allow_unsigned_extensions": "true"})
    con.execute(f"LOAD '{qe.LOADABLE}'")
    uri = f"quack://127.0.0.1:{port}/?token={qe.TOKEN}&tls=false"
    con.execute(f"ATTACH 'driver={qe.DRIVER};uri=\"{uri}\"' AS rp (TYPE fabricator, PROVIDER 'adbc')")
    remote = con.execute("SELECT DISTINCT table_catalog || '.' || table_schema FROM information_schema.tables "
                         "WHERE table_catalog = 'rp'").fetchall()[0][0] + "."
    local = "memory.main."
    con.execute("CALL enable_logging(['Fabricator.Remote'], storage := 'memory', level := 'debug')")
    bad = 0

    def setup():
        for st in SETUP:
            con.execute(st.format(s=local))
            con.execute("SELECT fabricator_exec('rp', ?)", [st.format(s="")])
        con.execute("SELECT fabricator_refresh_cache('rp')")

    def server_table(name):
        cols = con.execute("SELECT * FROM fabricator_query('rp', ?)",
                           [f"SELECT column_name, data_type, is_nullable, column_default FROM information_schema.columns "
                            f"WHERE table_name = '{name}' ORDER BY ordinal_position"]).fetchall()
        rows = sorted(map(repr, con.execute("SELECT * FROM fabricator_query('rp', ?)",
                                            [f"SELECT * FROM {name}"]).fetchall()))
        return cols, rows

    def local_table(name):
        cols = con.execute("SELECT column_name, data_type, is_nullable, column_default FROM information_schema.columns "
                           f"WHERE table_catalog = 'memory' AND table_name = '{name}' ORDER BY ordinal_position").fetchall()
        rows = sorted(map(repr, con.execute(f"SELECT * FROM {local}{name}").fetchall()))
        return cols, rows

    for name, statements in ALTERS + MERGES + KEYLESS:
        setup()
        con.execute("CALL truncate_duckdb_logs()")
        err = None
        try:
            for st in statements:
                con.execute(st.format(s=remote))
        except Exception as e:  # noqa: BLE001
            err = str(e).splitlines()[0][:220]
            try:
                con.execute("ROLLBACK")
            except Exception:  # noqa: BLE001
                pass
        for st in statements:
            con.execute(st.format(s=local))
        logs = [m for (m,) in con.execute("SELECT message FROM duckdb_logs WHERE type = 'Fabricator.Remote'").fetchall()]
        table = "am_m" if name.startswith("merge") else "am_t"
        got, want = server_table(table), local_table(table)
        ok = err is None and got == want
        bad += not ok
        pushed = "PUSHED" if any(m.startswith("remote") for m in logs) else "-"
        print(f"{'ok ' if ok else 'BAD'} {pushed:6} {name}" + (f"  error: {err}" if err else ""))
        if name.startswith("merge"):
            print("   logs: " + " | ".join(m[:260] for m in logs[:4]))
        if err is None and got != want:
            print(f"   server {got}\n   local  {want}")

    for name, st in RETURNING + KEYLESS_RETURNING:
        setup()
        try:
            got = sorted(map(repr, con.execute(st.format(s=remote)).fetchall()))
            want = sorted(map(repr, con.execute(st.format(s=local)).fetchall()))
            table = "am_t" if name.startswith("rowid") else "am_m"
            ok = got == want and server_table(table) == local_table(table)
            print(f"{'ok ' if ok else 'BAD'} {name}" + ("" if ok else f" {got} / {want}"))
        except Exception as e:  # noqa: BLE001
            ok = False
            print(f"BAD {name}  error:", str(e).splitlines()[0][:220])
        bad += not ok

    # CREATE TABLE with literal defaults, then an INSERT naming one column
    for st in ["CREATE TABLE {s}df_t (a INTEGER DEFAULT 5, b VARCHAR DEFAULT 'x', c INTEGER, d BOOLEAN DEFAULT true)",
               "INSERT INTO {s}df_t (c) VALUES (1), (2)"]:
        con.execute(st.format(s=remote))
        con.execute(st.format(s=local))
    ok = server_table("df_t") == local_table("df_t")
    bad += not ok
    print(f"{'ok ' if ok else 'BAD'} create table defaults" + ("" if ok else f" {server_table('df_t')} / {local_table('df_t')}"))

    # INSERT … RETURNING
    setup()
    st = "INSERT INTO {s}am_t (id, k) VALUES (7, 'g'), (8, NULL) RETURNING *"
    try:
        got = sorted(map(repr, con.execute(st.format(s=remote)).fetchall()))
        want = sorted(map(repr, con.execute(st.format(s=local)).fetchall()))
        ok = got == want and server_table("am_t") == local_table("am_t")
        print(f"{'ok ' if ok else 'BAD'} insert returning" + ("" if ok else f"\n   {got}\n   {want}"))
    except Exception as e:  # noqa: BLE001
        ok = False
        print("BAD insert returning  error:", str(e).splitlines()[0][:220])
    bad += not ok
    print(f"\n{len(ALTERS) + len(MERGES) + 1} cases, {bad} not ok")


qe.run = run
qe.main()
