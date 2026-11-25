"""TPC-H / TPC-DS with tables split between local DuckDB and a remote target, for correctness AND timings.

Every query of every placement must return what the all-local run returns; the timings are the second output. Not a CI
gate (timings on shared runners are noise) — the fixed SF 0.01 correctness lives in test/verify_tpch_mssql.test and
test/verify_tpcds_mssql.test (scripts/gen_tpc_suites.py). Run this after changing the pushdown, at a larger SF.

  python -I scripts/tpc_bench.py <tpch|tpcds> <sf> [--target box|warehouse|lakehouse-delta|lakehouse-sql] [--load]
         [--queries 1,5,9] [--combos remote,facts-remote,dims-remote,random1,random2] [--timeout 300] [--out f.json]

Targets:
  box              the docker SQL Server (scripts' usual DSN), remote_pushdown on
  warehouse        Fabric Warehouse (connection from dbt_mssql_test/profiles.yml, target `fabric`), remote_pushdown on
  lakehouse-delta  Delta tables on OneLake (LH, schema-enabled) read through our Delta engine (secret from
                   dax_secret.sql)
  lakehouse-sql    the same Delta tables read through the lakehouse's SQL analytics endpoint, remote_pushdown on; the
                   load waits until the endpoint's asynchronous metadata sync shows every table with its full count
  quack            a DuckDB 2.0.1 server serving quack (started here, its database kept in <data dir>/quack_tpc.duckdb),
                   attached with PROVIDER 'adbc' through quack-net's driver (QUACK_ADBC_DRIVER); the CLI is
                   QUACK_DUCKDB_CLI, the loadable FABRICATOR_TPC_LOADABLE — this branch's build, not release-v2
⚠ The Fabric targets read credentials from gitignored files and never print them. They consume Fabric capacity.
"""
import argparse
import atexit
import json
import math
import os
import random
import re
import socket
import subprocess
import threading
import time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
os.environ.setdefault("FABRICATOR_MANAGED_DIR",
                      os.path.join(REPO, "build", "release-v2", "extension", "fabricator", "fabricator"))
import duckdb  # noqa: E402

LOADABLE = os.environ.get("FABRICATOR_TPC_LOADABLE",
                          os.path.join(REPO, "build", "release-v2", "extension", "fabricator", "fabricator.duckdb_extension"))
BOX = "Server=localhost,1433;Database=TestDB;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true"
ONELAKE_TABLES = "abfss://Test@onelake.dfs.fabric.microsoft.com/LH.Lakehouse/Tables"
DATA_DIR = os.environ.get("FABRICATOR_TPC_DIR", os.path.join(REPO, "build", "tpc"))

FACTS = {
    "tpch": {"lineitem", "orders", "partsupp"},
    "tpcds": {"store_sales", "store_returns", "catalog_sales", "catalog_returns", "web_sales", "web_returns",
              "inventory"},
}


def warehouse_dsn():
    """The Fabric Warehouse connection string of the dbt `fabric` target (holds a secret: never print it)."""
    text = open(os.path.join(REPO, "dbt_mssql_test", "profiles.yml"), encoding="utf-8").read()
    block = text[text.index("    fabric:"):]
    return re.search(r"path:\s*'([^']*Database=[^']*)'", block).group(1)


def rschema(bench, sf):
    """The remote schema of one benchmark at one scale factor (a shared one once mixed SF1 rows into SF0.01)."""
    return f"{bench}_sf{str(sf).replace('.', '_')}"


QUACK_TOKEN = "tpc-token"
REMOTE = {}  # remote schema -> how the attached catalog names it (the adbc provider may qualify it with the database)


def start_quack():
    """A DuckDB CLI serving quack on a free port, its database a file that outlives the run (load once)."""
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        port = s.getsockname()[1]
    os.makedirs(DATA_DIR, exist_ok=True)
    init = os.path.join(DATA_DIR, "quack_init.sql")
    with open(init, "w", encoding="utf-8") as f:
        f.write("INSTALL quack; LOAD quack;\n"
                f"SELECT * FROM quack_serve('quack:127.0.0.1:{port}', token => '{QUACK_TOKEN}', disable_ssl => true);\n")
    log = open(os.path.join(DATA_DIR, "quack_server.log"), "w", encoding="utf-8")
    cli = os.environ.get("QUACK_DUCKDB_CLI", os.path.join(REPO, "build", "release-v2", "duckdb.exe"))
    server = subprocess.Popen([cli, "-no-agent", "quack_tpc.duckdb", "-init", init], cwd=DATA_DIR,
                              stdin=subprocess.PIPE, stdout=log, stderr=subprocess.STDOUT, text=True)

    def stop():
        server.stdin.close()
        try:
            server.wait(timeout=60)
        except subprocess.TimeoutExpired:
            server.kill()
        log.close()
    atexit.register(stop)
    deadline = time.monotonic() + 60
    while True:
        try:
            socket.create_connection(("127.0.0.1", port), timeout=1).close()
            return port
        except OSError:
            if time.monotonic() > deadline or server.poll() is not None:
                raise RuntimeError("quack server did not start, see quack_server.log")
            time.sleep(0.3)


def remote_name(con, target, rs):
    """`<catalog>.<schema>` of the remote schema as the attached catalog names it."""
    if rs not in REMOTE:
        cat = remote_catalog(target)
        if target != "quack":
            REMOTE[rs] = f"{cat}.{rs}"
        else:
            con.execute("SELECT fabricator_refresh_cache('rp')")
            hits = [r[0] for r in con.execute(
                "SELECT schema_name FROM information_schema.schemata WHERE catalog_name = 'rp'").fetchall()
                    if r[0] == rs or r[0].endswith("." + rs)]
            if len(hits) != 1:
                raise RuntimeError(f"remote schema {rs}: {hits}")
            REMOTE[rs] = f'{cat}."{hits[0]}"' if "." in hits[0] else f"{cat}.{hits[0]}"
    return REMOTE[rs]


def connect(bench, sf, target):
    con = duckdb.connect(config={"allow_unsigned_extensions": "true"})
    con.execute(f"LOAD '{LOADABLE}'")
    con.execute(f"INSTALL {bench}; LOAD {bench}")
    os.makedirs(DATA_DIR, exist_ok=True)
    con.execute(f"ATTACH '{os.path.join(DATA_DIR, f'{bench}_sf{sf}.duckdb')}' AS l")
    if target == "box":
        con.execute(f"ATTACH '{BOX}' AS rp (TYPE fabricator, remote_pushdown 'true')")
    elif target == "warehouse":
        con.execute(f"ATTACH '{warehouse_dsn()}' AS rp (TYPE fabricator, remote_pushdown 'true')")
    elif target == "quack":
        port = start_quack()
        uri = f"quack://127.0.0.1:{port}/?token={QUACK_TOKEN}&tls=false"
        con.execute(f"ATTACH 'driver={os.environ['QUACK_ADBC_DRIVER']};uri=\"{uri}\"' AS rp (TYPE fabricator, PROVIDER 'adbc')")
    else:
        con.execute(open(os.path.join(REPO, "dax_secret.sql"), encoding="utf-8").read())
        # name mapping: the SQL analytics endpoint skips id-mapped tables without a word
        con.execute(f"ATTACH '{ONELAKE_TABLES}' AS lw (TYPE fabricator, PROVIDER 'delta', SECRET fabric_sp, "
                    "READ_ONLY false, schemas 'onelevel', column_mapping 'name')")
        if target == "lakehouse-sql":
            dsn = re.sub(r"Database=[^;]*", "Database=LH", warehouse_dsn())
            con.execute(f"ATTACH '{dsn}' AS rp (TYPE fabricator, remote_pushdown 'true')")
    if target in ("warehouse", "lakehouse-sql"):
        # Fabric answers a repeated statement from its result set cache (on by default; the same scans recur across
        # placements), so a later placement would look faster than it is — docs/duckdb-2.0-remote-execution.md §5.7
        con.execute("SET mssql_result_set_caching = false")
    return con


def remote_catalog(target):
    return "lw" if target == "lakehouse-delta" else "rp"


def tables(con, bench):
    return [r[0] for r in con.execute(
        f"SELECT table_name FROM duckdb_tables() WHERE database_name = 'l' AND schema_name = '{bench}' ORDER BY 1"
    ).fetchall()]


def load(con, bench, sf, target):
    if not tables(con, bench):
        con.execute(f"CREATE SCHEMA IF NOT EXISTS l.{bench}")
        gen = "dbgen" if bench == "tpch" else "dsdgen"
        con.execute(f"CALL {gen}(sf = {sf}, catalog = 'l', schema = '{bench}')")
    rs = rschema(bench, sf)
    names = tables(con, bench)
    if target in ("box", "warehouse"):
        con.execute(f"SELECT fabricator_exec('rp', 'IF SCHEMA_ID(''{rs}'') IS NULL EXEC(''CREATE SCHEMA {rs}'')', "
                    "refresh_cache := true)")
        # text columns as wide as the data (the longest TPC-H / TPC-DS value is 198 chars): SQL Server returns a max
        # value through its LOB path however short (200k one-char flags: 37 s as max, 0.28 s cast), and a parallel plan
        # pays for the DECLARED width (1.48M one-char rows: 49 s as nvarchar(4000), 1.2 s as nvarchar(100)) —
        # docs/duckdb-2.0-remote-execution.md §4.26
        con.execute("SET mssql_default_varchar_length = 200")
        # clustered columnstore, as an analytic table on box would be (a heap without keys or indexes is no fair
        # baseline); a no-op on the warehouse, whose tables are columnstore anyway
        con.execute("SET mssql_default_table_type = 'clustered columnstore'")
        writer = "rp"
    elif target == "quack":
        con.execute(f"SELECT fabricator_exec('rp', 'CREATE SCHEMA IF NOT EXISTS {rs}', refresh_cache := true)")
        writer = "rp"
    else:
        con.execute(f"CREATE SCHEMA IF NOT EXISTS lw.{rs}")
        writer = "lw"
    for t in names:
        t0 = time.perf_counter()
        dest = remote_name(con, target, rs) if target == "quack" else f"{writer}.{rs}"
        con.execute(f"CREATE OR REPLACE TABLE {dest}.{t} AS SELECT * FROM l.{bench}.{t}")
        print(f"  loaded {t} in {time.perf_counter() - t0:.1f} s", flush=True)
    if target == "lakehouse-sql":
        wait_for_endpoint(con, bench, rs, names)


def wait_for_endpoint(con, bench, rs, names, limit=900):
    """Polls the SQL analytics endpoint until it shows every table with its full row count: its metadata sync is
    asynchronous, and a stale-but-consistent snapshot reads exactly like wrong data."""
    want = {t: con.execute(f"SELECT count(*) FROM l.{bench}.{t}").fetchone()[0] for t in names}
    t0 = time.perf_counter()
    while True:
        con.execute("SELECT fabricator_refresh_cache('rp')")
        missing = []
        for t in names:
            try:
                n = con.execute(f"SELECT count(*) FROM rp.{rs}.{t}").fetchone()[0]
            except Exception:  # noqa: BLE001
                n = None
            if n != want[t]:
                missing.append(f"{t} {n}/{want[t]}")
        if not missing:
            print(f"  endpoint in sync after {time.perf_counter() - t0:.0f} s", flush=True)
            return
        if time.perf_counter() - t0 > limit:
            raise RuntimeError("SQL endpoint not in sync: " + ", ".join(missing))
        print(f"  endpoint not in sync yet: {', '.join(missing[:4])}", flush=True)
        time.sleep(20)


def placements(bench, names, which, seed):
    facts = FACTS[bench]
    fixed = {
        "local": set(),
        "remote": set(names),
        "facts-remote": {t for t in names if t in facts},
        "dims-remote": {t for t in names if t not in facts},
    }
    out = {}
    for k in which:
        if k.startswith("random"):
            rnd = random.Random(f"{seed}:{k}")
            out[k] = {t for t in names if rnd.random() < 0.5}
        else:
            out[k] = fixed[k]
    return out


def use_placement(con, bench, names, remote, sf, target):
    con.execute("USE memory.main")
    con.execute("DROP SCHEMA IF EXISTS memory.mix CASCADE")
    con.execute("CREATE SCHEMA memory.mix")
    prefix = remote_name(con, target, rschema(bench, sf)) if remote else None
    for t in names:
        src = f"{prefix}.{t}" if t in remote else f"l.{bench}.{t}"
        con.execute(f"CREATE VIEW memory.mix.{t} AS SELECT * FROM {src}")
    con.execute("USE memory.mix")


def run(con, sql, timeout):
    timer = threading.Timer(timeout, con.interrupt)
    timer.start()
    t0 = time.perf_counter()
    try:
        rows = con.execute(sql).fetchall()
        return rows, time.perf_counter() - t0, None
    except duckdb.InterruptException:
        return None, time.perf_counter() - t0, "TIMEOUT"
    except Exception as e:  # noqa: BLE001
        return None, time.perf_counter() - t0, f"{type(e).__name__}: {str(e).splitlines()[0][:200]}"
    finally:
        timer.cancel()


def norm(v):
    if isinstance(v, float):
        return None if math.isnan(v) else float(f"{v:.9g}")
    if hasattr(v, "is_finite"):  # Decimal
        return float(f"{float(v):.12g}")
    return v


def key(row):
    return tuple((x is None, str(type(x)), x if x is not None else 0) for x in row)


def compare(ref, got, ordered):
    a = [tuple(norm(x) for x in r) for r in ref]
    b = [tuple(norm(x) for x in r) for r in got]
    if len(a) != len(b):
        return f"rows {len(b)} != {len(a)}"
    if ordered and a == b:
        return None
    if sorted(a, key=key) == sorted(b, key=key):
        return None if not ordered else "order differs (same rows)"
    diff = sum(1 for x, y in zip(sorted(a, key=key), sorted(b, key=key)) if x != y)
    return f"{diff} rows differ"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("bench", choices=["tpch", "tpcds"])
    ap.add_argument("sf")
    ap.add_argument("--target", default="box", choices=["box", "warehouse", "lakehouse-delta", "lakehouse-sql", "quack"])
    ap.add_argument("--load", action="store_true")
    ap.add_argument("--queries")
    ap.add_argument("--combos", default="remote,facts-remote,dims-remote,random1,random2")
    ap.add_argument("--seed", default="2026-10-31")
    ap.add_argument("--timeout", type=float, default=300)
    ap.add_argument("--out")
    args = ap.parse_args()

    con = connect(args.bench, args.sf, args.target)
    if args.load:
        load(con, args.bench, args.sf, args.target)
    names = tables(con, args.bench)
    fn = "tpch_queries" if args.bench == "tpch" else "tpcds_queries"
    queries = con.execute(f"SELECT query_nr, query FROM {fn}() ORDER BY 1").fetchall()
    if args.queries:
        wanted = {int(x) for x in args.queries.split(",")}
        queries = [q for q in queries if q[0] in wanted]
    combos = placements(args.bench, names, args.combos.split(","), args.seed)
    for k, v in combos.items():
        print(f"placement {k}: remote = {sorted(v)}", flush=True)

    results = []
    use_placement(con, args.bench, names, set(), args.sf, args.target)
    refs = {}
    for nr, sql in queries:
        rows, secs, err = run(con, sql, args.timeout)
        refs[nr] = (rows, secs, err)
        results.append({"q": nr, "combo": "local", "secs": round(secs, 3), "status": err or "ok"})
    for combo, remote in combos.items():
        use_placement(con, args.bench, names, remote, args.sf, args.target)
        for nr, sql in queries:
            ref_rows, ref_secs, ref_err = refs[nr]
            rows, secs, err = run(con, sql, args.timeout)
            if err is None and ref_err is None:
                status = compare(ref_rows, rows, "order by" in sql.lower()[-400:]) or "ok"
            else:
                status = err or f"reference failed: {ref_err}"
            results.append({"q": nr, "combo": combo, "secs": round(secs, 3), "local_secs": round(ref_secs, 3),
                            "status": status})
            mark = "" if status == "ok" else "   <-- " + status
            print(f"q{nr:>3} {combo:<13} {secs:8.3f} s  (local {ref_secs:7.3f} s){mark}", flush=True)
    bad = [r for r in results if r["status"] != "ok" and r["combo"] != "local"]
    totals = {}
    for r in results:
        totals[r["combo"]] = totals.get(r["combo"], 0.0) + r["secs"]
    print(f"\n{len(results) - len(queries)} runs, {len(bad)} not ok; seconds per placement: "
          + ", ".join(f"{k} {v:.1f}" for k, v in totals.items()))
    for r in bad:
        print(f"  q{r['q']} {r['combo']}: {r['status']}")
    if args.out:
        with open(args.out, "w") as f:
            json.dump(results, f, indent=1)


if __name__ == "__main__":
    main()
