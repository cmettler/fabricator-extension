"""Inversion checks of the adbc provider against a quack server: what the differential runs could not see.

  order     ORDER BY kept through projections, joins, set operations, windows, nested limits (the other scripts
            compare sorted rows, so a lost order is invisible there)
  volatile  a volatile function written once must give one value per row
  nested    lossless reads of nested types (LIST<BOOLEAN>, STRUCT with UUID, MAP, HUGEINT[])

    python -I quack_inversion.py <managed dir> [section ...]
"""
import importlib.util
import sys
from pathlib import Path

spec = importlib.util.spec_from_file_location("qe", Path(__file__).with_name("quack_edges.py"))
qe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(qe)
SECTIONS = set(sys.argv[2:]) or {"order", "volatile", "nested", "enum"}

ORDER = [
    "SELECT id, CAST(i AS BIGINT) * 2 AS x FROM {t} ORDER BY i DESC NULLS LAST, id",
    "SELECT g, sum(i) AS s FROM {t} GROUP BY g ORDER BY s DESC NULLS FIRST, g",
    "SELECT t.id, u.s FROM {t} t JOIN {u} u ON t.i = u.i ORDER BY u.s, t.id, u.id",
    "SELECT s FROM {t} UNION ALL SELECT s FROM {u} ORDER BY 1 NULLS FIRST",
    "SELECT id, row_number() OVER (ORDER BY id DESC) AS rn FROM {t} ORDER BY rn",
    "SELECT * FROM (SELECT id, i FROM {t} ORDER BY id LIMIT 5) ORDER BY i DESC NULLS LAST, id",
    "SELECT id FROM {t} ORDER BY i * -1 NULLS LAST, id",
    "SELECT DISTINCT g FROM {t} ORDER BY g DESC NULLS LAST",
    "SELECT id, lower(s) AS l FROM {t} ORDER BY l NULLS LAST, id LIMIT 4",
    "SELECT g, count(*) AS n FROM {t} GROUP BY g HAVING count(*) > 1 ORDER BY n, g",
    "SELECT t.id FROM {t} t LEFT JOIN {u} u ON t.id = u.id ORDER BY u.i NULLS FIRST, t.id DESC",
    "SELECT id, s FROM {t} ORDER BY s COLLATE NOCASE, id",
]

VOLATILE = [
    "SELECT count(*) FROM (SELECT r FROM (SELECT random() AS r FROM {t}) WHERE r <> r)",
    "SELECT count(*) FROM (SELECT x, x AS y FROM (SELECT random() AS x FROM {t}) WHERE x <> y)",
    "SELECT count(DISTINCT u) = count(*) FROM (SELECT gen_random_uuid() AS u FROM {t})",
]

NESTED_TABLE = ("CREATE OR REPLACE TABLE nest_t (id INTEGER, lb BOOLEAN[], st STRUCT(b BOOLEAN, u UUID, h HUGEINT), "
                "m MAP(VARCHAR, UUID), hl HUGEINT[], tz TIMETZ[], e ENUM('a', 'b'), bl BIT[])")
NESTED_ROWS = ("INSERT INTO nest_t VALUES (1, [true, NULL, false], {'b': true, 'u': '00000000-0000-0000-0000-00000000002a', "
               "'h': 170141183460469231731687303715884105727}, MAP {'k': '00000000-0000-0000-0000-000000000001'}, "
               "[1, -170141183460469231731687303715884105728], ['12:00:00+01'], 'b', ['101']), "
               "(2, NULL, NULL, NULL, NULL, NULL, NULL, NULL)")


def run(port):
    con = qe.duckdb.connect(config={"allow_unsigned_extensions": "true"})
    con.execute(f"LOAD '{qe.LOADABLE}'")
    con.execute(qe.TABLES)
    uri = f"quack://127.0.0.1:{port}/?token={qe.TOKEN}&tls=false"
    con.execute(f"ATTACH 'driver={qe.DRIVER};uri=\"{uri}\"' AS rp (TYPE fabricator, PROVIDER 'adbc')")
    s = con.execute("SELECT DISTINCT table_catalog || '.' || table_schema FROM information_schema.tables "
                    "WHERE table_catalog = 'rp'").fetchall()[0][0]
    con.execute("CALL enable_logging(['Fabricator.Remote'], storage := 'memory', level := 'debug')")
    bad = 0

    def pushed():
        return any(m.startswith("remote") for (m,) in con.execute(
            "SELECT message FROM duckdb_logs WHERE type = 'Fabricator.Remote'").fetchall())

    if "order" in SECTIONS:
        for q in ORDER:
            con.execute("CALL truncate_duckdb_logs()")
            try:
                got = con.execute(q.format(t=f"{s}.ed_t", u=f"{s}.ed_u")).fetchall()
            except Exception as e:  # noqa: BLE001
                got = "ERROR " + str(e).splitlines()[0][:150]
            p = pushed()
            want = con.execute(q.format(t="memory.main.ed_t", u="memory.main.ed_u")).fetchall()
            ok = got == want
            bad += not ok
            print(f"{'ok ' if ok else 'BAD'} {'PUSHED' if p else 'local '} order  {q[:90]}")
            if not ok:
                print(f"     got  {got}\n     want {want}")

    if "volatile" in SECTIONS:
        for q in VOLATILE:
            con.execute("CALL truncate_duckdb_logs()")
            got = con.execute(q.format(t=f"{s}.ed_t")).fetchall()
            p = pushed()
            want = con.execute(q.format(t="memory.main.ed_t")).fetchall()
            ok = got == want
            bad += not ok
            print(f"{'ok ' if ok else 'BAD'} {'PUSHED' if p else 'local '} volatile  {q[:90]}  {got}")

    if "nested" in SECTIONS:
        for st in (NESTED_TABLE, NESTED_ROWS):
            con.execute("SELECT fabricator_exec('rp', ?)", [st])
            con.execute(st.replace("nest_t", "memory.main.nest_t"))
        con.execute("SELECT fabricator_refresh_cache('rp')")
        rtypes = con.execute("SELECT column_name, data_type FROM information_schema.columns WHERE table_catalog = 'rp' "
                             "AND table_name = 'nest_t' ORDER BY ordinal_position").fetchall()
        ltypes = con.execute("SELECT column_name, data_type FROM information_schema.columns WHERE table_catalog = 'memory' "
                             "AND table_name = 'nest_t' ORDER BY ordinal_position").fetchall()
        # an ENUM reads as VARCHAR (Arrow has no enum; the driver reads it as a string, so it behaves as one)
        ltypes = [(c, "VARCHAR" if t.startswith("ENUM(") else t) for c, t in ltypes]
        ok = rtypes == ltypes
        bad += not ok
        print(f"{'ok ' if ok else 'BAD'} nested: catalog types" + ("" if ok else f"\n     got  {rtypes}\n     want {ltypes}"))
        for q in ["SELECT * FROM {n} ORDER BY id",
                  "SELECT id, lb[1], st.u, st.h, m['k'], hl[2], e FROM {n} ORDER BY id",
                  "SELECT count(*) FILTER (WHERE lb[3] = false), max(st.h), list_sort(any_value(hl)) FROM {n} WHERE id = 1"]:
            con.execute("CALL truncate_duckdb_logs()")
            try:
                got = con.execute(q.format(n=f"{s}.nest_t")).fetchall()
            except Exception as e:  # noqa: BLE001
                got = "ERROR " + str(e).splitlines()[0][:150]
            p = pushed()
            want = con.execute(q.format(n="memory.main.nest_t")).fetchall()
            ok = got == want
            bad += not ok
            print(f"{'ok ' if ok else 'BAD'} {'PUSHED' if p else 'local '} nested  {q[:80]}" +
                  ("" if ok else f"\n     got  {got}\n     want {want}"))
    if "enum" in SECTIONS:
        # the catalog reads an ENUM as VARCHAR (Arrow has no enum): a pushed query must then mean what a VARCHAR
        # column means, whatever the server's ENUM order
        con.execute("SELECT fabricator_exec('rp', ?)", ["CREATE OR REPLACE TABLE enum_t (id INTEGER, e ENUM('z', 'a', 'm'))"])
        con.execute("SELECT fabricator_exec('rp', ?)", ["INSERT INTO enum_t VALUES (1, 'z'), (2, 'a'), (3, 'm'), (4, NULL), (5, 'a')"])
        con.execute("CREATE OR REPLACE TABLE memory.main.enum_t (id INTEGER, e VARCHAR)")
        con.execute("INSERT INTO memory.main.enum_t VALUES (1, 'z'), (2, 'a'), (3, 'm'), (4, NULL), (5, 'a')")
        con.execute("SELECT fabricator_refresh_cache('rp')")
        for q in ["SELECT id, e FROM {n} ORDER BY e NULLS LAST, id",
                  "SELECT count(*) FROM {n} WHERE e < 'm'",
                  "SELECT min(e), max(e) FROM {n}",
                  "SELECT e, count(*) FROM {n} GROUP BY e ORDER BY e NULLS LAST",
                  "SELECT id FROM {n} WHERE e > 'b' ORDER BY id",
                  "SELECT id, e FROM {n} ORDER BY e DESC NULLS LAST, id LIMIT 2"]:
            con.execute("CALL truncate_duckdb_logs()")
            got = con.execute(q.format(n=f"{s}.enum_t")).fetchall()
            p = pushed()
            want = con.execute(q.format(n="memory.main.enum_t")).fetchall()
            ok = got == want
            bad += not ok
            print(f"{'ok ' if ok else 'BAD'} {'PUSHED' if p else 'local '} enum  {q[:70]}" + ("" if ok else f"  got {got} want {want}"))
    print(f"\n{bad} not ok")


qe.run = run
qe.main()
