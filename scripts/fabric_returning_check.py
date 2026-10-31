"""DML … RETURNING on Fabric Warehouse, which has no OUTPUT clause (docs/warehouse-support.md §6.8b).

Every row a statement returns must equal what the warehouse stores afterwards (for DELETE: what it stored before) —
the server is the oracle, so the store conversions (datetime2/time truncated, char padded to n bytes) are checked
rather than assumed. Covers INSERT (incl. omitted columns), UPDATE, DELETE, MERGE, the "moved" CTE, BEGIN … ROLLBACK
and BEGIN … COMMIT, and the two refusals (an UPDATE that changes a key, an INSERT into an IDENTITY table). Runs once
per insert path: SqlBulkCopy, and the staged COPY INTO when --staging names a location.

Not a CI gate: no tier reaches a warehouse. Run it after changing anything on that path:

  python -I scripts/fabric_returning_check.py [--staging abfss://<workspaceId>@onelake.dfs.fabric.microsoft.com/<lakehouseId>/Files/<dir>]

Needs the duckdb 2.0.0 wheel and the release-v2 build. The warehouse connection is the dbt `fabric` target of
dbt_mssql_test/profiles.yml; the OneLake credential for --staging is the secret in dax_secret.sql. ⚠ Both files are
gitignored and hold live credentials: this script reads them and never prints them. It consumes Fabric capacity and
creates/drops tables dbo.wret_r / dbo.wret_a / dbo.wret_i. Exit code 1 when any check fails.
"""
import argparse
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
os.environ.setdefault("FABRICATOR_MANAGED_DIR",
                      os.path.join(REPO, "build", "release-v2", "extension", "fabricator", "fabricator"))
import duckdb  # noqa: E402

LOADABLE = os.path.join(REPO, "build", "release-v2", "extension", "fabricator", "fabricator.duckdb_extension")
FAILS = []


def warehouse_dsn():
    """The Fabric Warehouse connection string of the dbt `fabric` target (holds a secret: never print it)."""
    text = open(os.path.join(REPO, "dbt_mssql_test", "profiles.yml"), encoding="utf-8").read()
    return re.search(r"path:\s*'([^']*Database=[^']*)'", text[text.index("    fabric:"):]).group(1)


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--staging", help="a OneLake location (GUID form) for the COPY INTO leg; omitted = SqlBulkCopy only")
    args = ap.parse_args()

    con = duckdb.connect(config={"allow_unsigned_extensions": "true"})
    con.execute(f"LOAD '{LOADABLE}'")
    con.execute(f"ATTACH '{warehouse_dsn()}' AS wb (TYPE fabricator)")
    cats = [("wb", "SqlBulkCopy")]
    if args.staging:
        con.execute(open(os.path.join(REPO, "dax_secret.sql"), encoding="utf-8").read())
        con.execute(f"ATTACH '{warehouse_dsn()}' AS wc (TYPE fabricator, copy_into_staging '{args.staging}')")
        cats.append(("wc", "COPY INTO"))

    def q(sql):
        return con.execute(sql).fetchall()

    def ex(sql):
        return q("SELECT fabricator_exec('wb', '" + sql.replace("'", "''") + "')")[0][0]

    def check(label, got, want):
        ok = sorted(got) == sorted(want)
        print(f"{'ok  ' if ok else 'FAIL'} {label}  ({len(got)} rows)", flush=True)
        if not ok:
            FAILS.append(label)
            print("   returned:", sorted(got)[:6])
            print("   stored  :", sorted(want)[:6])

    def refused(label, sql, needle):
        try:
            q(sql)
            print(f"FAIL {label}: not refused")
            FAILS.append(label)
        except Exception as e:  # noqa: BLE001
            ok = needle in str(e)
            print(f"{'ok  ' if ok else 'FAIL'} {label}: {str(e)[:160]}")
            if not ok:
                FAILS.append(label)

    cols = "id, v, c, d3, t3, dec, u, s"
    as_text = "id, v, c, d3::VARCHAR, t3::VARCHAR, dec::VARCHAR, u::VARCHAR, s"
    # sub-millisecond timestamps (truncated to datetime2(3) / time(3)), a non-ASCII char(5) value (padded to 5
    # BYTES), a UUID, NULLs and a pre-1970 value
    rows = """VALUES
     (1, 10, 'ä', TIMESTAMP '2026-01-01 12:00:00.123456', TIME '12:00:00.9999', 1.235, '0e7c0e5a-7f8b-4c4e-9a52-6a0d2f1b7c11', 'x'),
     (2, 20, 'abcde', TIMESTAMP '1969-12-31 23:59:59.9995', TIME '00:00:00.0005', -2.5, NULL, NULL),
     (3, NULL, NULL, NULL, NULL, NULL, NULL, 'only s')"""

    try:
        for cat, path in cats:
            print(f"=== {cat} ({path})", flush=True)

            def stored(where):
                return q(f"SELECT {as_text} FROM {cat}.dbo.wret_r WHERE {where}")

            ex("DROP TABLE IF EXISTS dbo.wret_r; CREATE TABLE dbo.wret_r (id BIGINT NOT NULL, v INT, c CHAR(5), "
               "d3 DATETIME2(3), t3 TIME(3), dec DECIMAL(10,2), u UNIQUEIDENTIFIER, s VARCHAR(20)); "
               "ALTER TABLE dbo.wret_r ADD CONSTRAINT pk_wret_r PRIMARY KEY NONCLUSTERED (id) NOT ENFORCED")
            q(f"SELECT fabricator_refresh_cache('{cat}')")

            got = q(f"WITH r AS (INSERT INTO {cat}.dbo.wret_r ({cols}) {rows} RETURNING *) SELECT {as_text} FROM r")
            check("INSERT RETURNING = stored", got, stored("id <= 3"))
            got = q(f"WITH r AS (INSERT INTO {cat}.dbo.wret_r (id, s) VALUES (4, 'omitted') RETURNING *) "
                    f"SELECT {as_text} FROM r")
            check("INSERT with omitted columns = stored", got, stored("id = 4"))

            got = q(f"WITH r AS (UPDATE {cat}.dbo.wret_r SET v = coalesce(v, 0) + 1, d3 = d3 + INTERVAL 1 SECOND, "
                    f"c = 'zz' WHERE id IN (1, 2, 4) RETURNING *) SELECT {as_text} FROM r")
            check("UPDATE RETURNING = stored after", got, stored("id IN (1, 2, 4)"))

            before = stored("id IN (2, 3)")
            got = q(f"WITH r AS (DELETE FROM {cat}.dbo.wret_r WHERE id IN (2, 3) RETURNING *) SELECT {as_text} FROM r")
            check("DELETE RETURNING = stored before", got, before)

            # MERGE is not a CTE body: run it directly
            got = [(str(r[0]), r[1], r[2], r[8]) for r in q(
                f"""MERGE INTO {cat}.dbo.wret_r AS t USING (VALUES (1, 100), (4, NULL), (9, 90)) AS src(id, v)
                    ON t.id = src.id
                    WHEN MATCHED AND src.v IS NULL THEN DELETE
                    WHEN MATCHED THEN UPDATE SET v = src.v
                    WHEN NOT MATCHED THEN INSERT (id, v, s) VALUES (src.id, src.v, 'm')
                    RETURNING merge_action, *""")]
            check("MERGE RETURNING", got, [("UPDATE", 1, 100, "x"), ("DELETE", 4, 1, "omitted"), ("INSERT", 9, 90, "m")])

            ex("DROP TABLE IF EXISTS dbo.wret_a; CREATE TABLE dbo.wret_a (id BIGINT, v INT, c CHAR(5), "
               "d3 DATETIME2(3), t3 TIME(3), dec DECIMAL(10,2), u UNIQUEIDENTIFIER, s VARCHAR(20))")
            q(f"SELECT fabricator_refresh_cache('{cat}')")
            before = stored("id = 9")
            q(f"WITH m AS (DELETE FROM {cat}.dbo.wret_r WHERE id = 9 RETURNING *) "
              f"INSERT INTO {cat}.dbo.wret_a SELECT * FROM m")
            check("moved CTE: archive = deleted", q(f"SELECT {as_text} FROM {cat}.dbo.wret_a"), before)

            q("BEGIN")
            q(f"UPDATE {cat}.dbo.wret_r SET v = -1 WHERE id = 1 RETURNING *")
            q(f"INSERT INTO {cat}.dbo.wret_r (id, s) VALUES (50, 'tx') RETURNING *")
            q("ROLLBACK")
            check("ROLLBACK undid both", q(f"SELECT id, v FROM {cat}.dbo.wret_r WHERE id IN (1, 50)"), [(1, 100)])
            q("BEGIN")
            got = q(f"INSERT INTO {cat}.dbo.wret_r (id, c, s) VALUES (51, 'ü', 'tx') RETURNING id, c, s")
            q("COMMIT")
            check("RETURNING in BEGIN ... COMMIT", got, q(f"SELECT id, c, s FROM {cat}.dbo.wret_r WHERE id = 51"))

            refused("UPDATE changing the key", f"UPDATE {cat}.dbo.wret_r SET id = 77 WHERE id = 1 RETURNING *",
                    "key column")
            check("key UPDATE changed nothing", q(f"SELECT id FROM {cat}.dbo.wret_r WHERE id IN (1, 77)"), [(1,)])

        ex("DROP TABLE IF EXISTS dbo.wret_i; CREATE TABLE dbo.wret_i (id BIGINT IDENTITY, s VARCHAR(10))")
        q("SELECT fabricator_refresh_cache('wb')")
        refused("INSERT into IDENTITY", "INSERT INTO wb.dbo.wret_i (s) VALUES ('a') RETURNING *", "IDENTITY")
        check("IDENTITY refusal inserted nothing", q("SELECT count(*) FROM wb.dbo.wret_i"), [(0,)])
    finally:
        ex("DROP TABLE IF EXISTS dbo.wret_r; DROP TABLE IF EXISTS dbo.wret_a; DROP TABLE IF EXISTS dbo.wret_i")
    print("FAILED: " + ", ".join(FAILS) if FAILS else "ALL OK")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
