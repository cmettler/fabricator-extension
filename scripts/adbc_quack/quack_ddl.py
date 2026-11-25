"""DDL and bulk loads through the adbc provider against a quack server: the server's column types and values, read
with raw SQL on the server (fabricator_query), compared with the same table in the local DuckDB.

    python -I quack_ddl.py <managed dir>      (QUACK_EDGES_LOADABLE = the loadable)
"""
import importlib.util
from pathlib import Path

spec = importlib.util.spec_from_file_location("qe", Path(__file__).with_name("quack_edges.py"))
qe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(qe)

COLUMNS = ("a HUGEINT, b UUID, c TIMETZ, d BOOLEAN, e UTINYINT, f DECIMAL(38,0), g VARCHAR NOT NULL, h INTEGER[], "
           "i STRUCT(x INTEGER, y VARCHAR), j TIMESTAMP_NS, k INTERVAL, l BLOB, m DATE, n DOUBLE, o UHUGEINT, p BIT")
ROWS = ("(-170141183460469231731687303715884105728, '00000000-0000-0000-0000-00000000002a', '12:34:56.789+02:30', true, "
        "255, 12345678901234567890123456789012345678, 'x', [1, NULL, 3], {'x': 1, 'y': 'z'}, "
        "'2024-01-02 03:04:05.678901234', INTERVAL '1 year 2 days 3 seconds', '\\xDE\\xAD'::BLOB, '2024-02-29', "
        "-0.0, 340282366920938463463374607431768211455, '101'), "
        "(NULL, NULL, NULL, NULL, NULL, NULL, 'y', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)")


def server(con, sql):
    return con.execute("SELECT * FROM fabricator_query('rp', ?)", [sql]).fetchall()


def run(port):
    con = qe.duckdb.connect(config={"allow_unsigned_extensions": "true"})
    con.execute(f"LOAD '{qe.LOADABLE}'")
    uri = f"quack://127.0.0.1:{port}/?token={qe.TOKEN}&tls=false"
    con.execute(f"ATTACH 'driver={qe.DRIVER};uri=\"{uri}\"' AS rp (TYPE fabricator, PROVIDER 'adbc')")
    s = con.execute("SELECT DISTINCT table_catalog || '.' || table_schema FROM information_schema.tables "
                    "WHERE table_catalog = 'rp'").fetchall()[0][0]
    bad = 0

    def check(name, ok, detail=""):
        nonlocal bad
        bad += not ok
        print(f"{'ok ' if ok else 'BAD'} {name}" + (f"  {detail}" if detail and not ok else ""))

    def types(table):
        return server(con, f"SELECT column_name, data_type FROM information_schema.columns WHERE table_name = '{table}' "
                           "ORDER BY ordinal_position")

    def values(table, where=""):
        cols = [c for c, _ in types(table)]
        text = ", ".join(f"coalesce(CAST({c} AS VARCHAR), 'NULL')" for c in cols)
        return sorted(server(con, f"SELECT {text} FROM {table} {where}"))

    # CREATE TABLE: the server's column types are the declared ones
    con.execute(f"CREATE TABLE {s}.ddl_t ({COLUMNS}, PRIMARY KEY (g))")
    con.execute(f"CREATE TABLE memory.main.ddl_t ({COLUMNS}, PRIMARY KEY (g))")
    want = con.execute("SELECT column_name, data_type FROM information_schema.columns WHERE table_catalog = 'memory' "
                       "AND table_name = 'ddl_t' ORDER BY ordinal_position").fetchall()
    got = types("ddl_t")
    check("create table: column types", got == want, f"\n   server {got}\n   local  {want}")
    pk = server(con, "SELECT constraint_type FROM information_schema.table_constraints WHERE table_name = 'ddl_t'")
    check("create table: primary key", ("PRIMARY KEY",) in pk, str(pk))

    # INSERT … VALUES (a bulk load): the same values
    con.execute(f"INSERT INTO {s}.ddl_t VALUES {ROWS}")
    con.execute(f"INSERT INTO memory.main.ddl_t VALUES {ROWS}")
    local = sorted(con.execute("SELECT " + ", ".join(f"coalesce(CAST({c} AS VARCHAR), 'NULL')" for c, _ in want)
                               + " FROM memory.main.ddl_t").fetchall())
    check("insert values: values", values("ddl_t") == local, f"\n   server {values('ddl_t')}\n   local  {local}")
    # read back through the catalog: the same rows as the local table
    back = sorted(map(repr, con.execute(f"SELECT g, CAST(a AS VARCHAR), CAST(b AS VARCHAR), CAST(o AS VARCHAR) FROM {s}.ddl_t").fetchall()))
    lback = sorted(map(repr, con.execute("SELECT g, CAST(a AS VARCHAR), CAST(b AS VARCHAR), CAST(o AS VARCHAR) FROM memory.main.ddl_t").fetchall()))
    check("read back through the catalog", back == lback, f"\n   {back}\n   {lback}")

    # CTAS from a local table
    con.execute(f"CREATE TABLE {s}.ctas_t AS SELECT * FROM memory.main.ddl_t")
    check("ctas: column types", [(c, t) for c, t in types("ctas_t")] == [(c, t) for c, t in want], str(types("ctas_t")))
    check("ctas: values", values("ctas_t") == local)

    # a transaction: CREATE, INSERT, read its own rows, ROLLBACK
    con.execute("BEGIN")
    con.execute(f"CREATE TABLE {s}.tx_t (a INTEGER, b VARCHAR)")
    con.execute(f"INSERT INTO {s}.tx_t VALUES (1, 'one'), (2, 'two')")
    inside = con.execute(f"SELECT count(*), max(b) FROM {s}.tx_t").fetchall()
    con.execute("ROLLBACK")
    gone = server(con, "SELECT count(*) FROM information_schema.tables WHERE table_name = 'tx_t'")
    check("txn: create + insert + read, then rollback", inside == [(2, "two")] and gone == [(0,)], f"{inside} {gone}")

    # the same, committed
    con.execute("BEGIN")
    con.execute(f"CREATE TABLE {s}.tx_t (a INTEGER, b VARCHAR)")
    con.execute(f"INSERT INTO {s}.tx_t VALUES (1, 'one'), (2, 'two')")
    con.execute("COMMIT")
    check("txn: committed", server(con, "SELECT count(*) FROM tx_t") == [(2,)])

    # schema and drop
    con.execute("CREATE SCHEMA rp.extra")
    con.execute("CREATE TABLE rp.extra.t AS SELECT 42 AS x")
    check("schema: table in a new schema", server(con, "SELECT x FROM extra.t") == [(42,)])
    con.execute("DROP TABLE rp.extra.t")
    con.execute("DROP SCHEMA rp.extra")
    check("drop table + schema", server(con, "SELECT count(*) FROM information_schema.schemata WHERE schema_name = 'extra'") == [(0,)])
    con.execute(f"DROP TABLE {s}.tx_t")
    check("drop table", server(con, "SELECT count(*) FROM information_schema.tables WHERE table_name = 'tx_t'") == [(0,)])
    print(f"\n{bad} not ok")


qe.run = run
qe.main()
