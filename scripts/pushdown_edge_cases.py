"""SQL edge cases from substrait-java's semantics tests (integer/decimal/float arithmetic, rounding, strings and
collation, NULL logic, dates, grouping sets, windows, set operations, joins, subqueries), run through `rp`
(remote_pushdown) and `db` (DuckDB evaluates over the same SQL Server rows).

    python -I scripts/pushdown_edge_cases.py [case ...]     differential against the box SQL Server, route per case
    python -I scripts/pushdown_edge_cases.py --emit         regenerate test/verify_pushdown_edges_mssql.test

Found two wrong answers on 2026-10-31 (NOT EXISTS with NULL keys; ROLLUP / CUBE on an empty input)."""
import math
import os
import sys

REPO = r"D:\repos\fabricator-extension"
os.environ.setdefault("FABRICATOR_MANAGED_DIR", REPO + r"\build\release-v2\extension\fabricator\fabricator")
import duckdb  # noqa: E402

REPO_TEST = os.path.join(REPO, "test", "verify_pushdown_edges_mssql.test")

LOADABLE = REPO + r"\build\release-v2\extension\fabricator\fabricator.duckdb_extension"
DSN = "Server=localhost,1433;Database=TestDB;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true"

SETUP = """
DROP TABLE IF EXISTS dbo.ed_t; DROP TABLE IF EXISTS dbo.ed_u;
CREATE TABLE dbo.ed_t (id INT PRIMARY KEY, i INT NULL, j INT NULL, b BIGINT NULL, ti TINYINT NULL, si SMALLINT NULL,
  d DECIMAL(10,2) NULL, d1 DECIMAL(1,1) NULL, f FLOAT NULL, s NVARCHAR(40) NULL, v VARCHAR(40) NULL,
  c NCHAR(10) NULL, flag BIT NULL, dt DATE NULL, ts DATETIME2(6) NULL, g INT NULL);
INSERT INTO dbo.ed_t VALUES
 (1, 7, 2, 9000000000, 5, 300, 2.50, 0.5, 2.5, N'Abc', 'Abc', N'ab', 1, '2024-01-31', '2024-01-31 10:00:00', 1),
 (2, -7, 2, -1, 0, -300, -2.50, -0.5, -2.5, N'abc', 'abc', N'ab  ', 0, '2024-02-29', '2024-02-29 23:59:59.999999', 1),
 (3, 5, 0, 2147483647, 255, 32767, 0.00, 0.0, 0.0, N'abc  ', 'abc  ', N'AB', NULL, '2023-12-31', '1970-01-01 00:00:00', 2),
 (4, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 2),
 (5, 2147483647, 1, 2147483647, 1, 1, 1.49, 0.1, -0.0, N'École', 'Ecole', N'x', 1, '2024-03-15', '2024-03-15 12:30:45.5', 3),
 (6, 3, -2, 7, 2, 2, 3.555, 0.9, 1e308, N'', '', N'', 0, '0001-01-01', '0001-01-01 00:00:00', 3),
 (7, 3, 3, 7, 2, 2, 1.005, 0.3, 0.1, N'a%b', 'a_b', N'a', 1, '9999-12-31', '9999-12-31 23:59:59', NULL);
CREATE TABLE dbo.ed_u (id INT PRIMARY KEY, i INT NULL, s NVARCHAR(40) NULL);
INSERT INTO dbo.ed_u VALUES (1, 7, N'Abc'), (2, 7, N'abc'), (3, NULL, N'x'), (4, 5, NULL), (5, 3, N'abc  ');
"""

# (name, sql with {t} / {u})
CASES = [
    # A/B integer and decimal arithmetic
    ("int div", "SELECT id, i / j FROM {t} WHERE j <> 0"),
    ("int intdiv", "SELECT id, i // j FROM {t} WHERE j <> 0"),
    ("int mod neg", "SELECT id, i % j FROM {t} WHERE j <> 0"),
    ("int div zero", "SELECT id, i / j FROM {t}"),
    ("int mod zero", "SELECT id, i % j FROM {t}"),
    ("int overflow add", "SELECT id, i + 1 FROM {t}"),
    ("int overflow mul", "SELECT id, i * 2 FROM {t}"),
    ("bigint mul", "SELECT id, b * 2 FROM {t}"),
    ("neg intmin", "SELECT id, -(i - 1) FROM {t} WHERE i < 0 OR i IS NULL"),
    ("abs", "SELECT id, abs(i), abs(si) FROM {t}"),
    ("tinyint", "SELECT id, ti + ti, ti - 10 FROM {t}"),
    ("sum int big", "SELECT sum(i), sum(b), avg(i), avg(j) FROM {t}"),
    ("avg int group", "SELECT g, avg(i), sum(i) FROM {t} GROUP BY g"),
    ("dec mod", "SELECT id, d % 1, d % 0.3 FROM {t}"),
    ("dec div", "SELECT id, d / 3, d / 0.7 FROM {t} WHERE d <> 0"),
    ("dec mul", "SELECT id, d * d, d * i FROM {t}"),
    ("avg dec", "SELECT avg(d), sum(d), avg(d1) FROM {t}"),
    ("cast dec int", "SELECT id, CAST(d AS INTEGER), CAST(d1 AS INTEGER) FROM {t}"),
    ("cast dbl int", "SELECT id, CAST(f AS INTEGER) FROM {t} WHERE abs(f) < 1e9"),
    ("cast dbl bigint", "SELECT id, CAST(f * 1e17 AS BIGINT) FROM {t} WHERE abs(f) < 50"),
    # C floating point
    ("dbl div zero", "SELECT id, f / 0 FROM {t}"),
    ("dbl zero zero", "SELECT id, f / f FROM {t}"),
    ("sqrt neg", "SELECT id, sqrt(f) FROM {t}"),
    ("ln", "SELECT id, ln(f) FROM {t}"),
    ("log10", "SELECT id, log10(f) FROM {t}"),
    ("asin", "SELECT id, asin(f) FROM {t}"),
    ("power", "SELECT id, power(f, 2), power(i, 2) FROM {t}"),
    ("dbl overflow", "SELECT id, f * 10 FROM {t}"),
    ("exp", "SELECT id, exp(f) FROM {t} WHERE abs(f) < 100"),
    # D rounding
    ("round dec", "SELECT id, round(d), round(d, 1), round(d1), round(d1, 0) FROM {t}"),
    ("round dbl", "SELECT id, round(f), round(f, 1) FROM {t} WHERE abs(f) < 1e9"),
    ("round int", "SELECT id, round(i, -1), round(i, 2) FROM {t}"),
    ("floor ceil", "SELECT id, floor(d), ceil(d), floor(f), ceil(f) FROM {t} WHERE abs(f) < 1e9"),
    ("trunc", "SELECT id, trunc(d), trunc(f) FROM {t} WHERE abs(f) < 1e9"),
    ("sign", "SELECT id, sign(i), sign(d), sign(f) FROM {t}"),
    # E statistics
    ("stddev", "SELECT stddev_samp(i), stddev_pop(i), var_samp(d), var_pop(f) FROM {t} WHERE abs(f) < 1e9"),
    ("stddev one row", "SELECT g, stddev_samp(i), var_samp(i) FROM {t} GROUP BY g"),
    # F strings
    ("like case", "SELECT id FROM {t} WHERE s LIKE 'a%'"),
    ("like case v", "SELECT id FROM {t} WHERE v LIKE 'a%'"),
    ("ilike", "SELECT id FROM {t} WHERE s ILIKE 'a%'"),
    ("like escape chars", "SELECT id FROM {t} WHERE s LIKE 'a\\%b' ESCAPE '\\'"),
    ("like underscore", "SELECT id FROM {t} WHERE v LIKE 'a_b'"),
    ("replace case", "SELECT id, replace(s, 'a', 'x') FROM {t}"),
    ("position case", "SELECT id, position('a' IN s), strpos(s, 'b') FROM {t}"),
    ("starts ends", "SELECT id, starts_with(s, 'a'), ends_with(s, 'c'), suffix(s, 'c'), prefix(s, 'A') FROM {t}"),
    ("contains", "SELECT id, contains(s, 'b') FROM {t}"),
    ("substr neg", "SELECT id, substring(s, -1, 4), substring(s, -2) FROM {t}"),
    ("substr past", "SELECT id, substring(s, 42), substring(s, 2, 0), substring(s, 0, 2) FROM {t}"),
    ("lower upper", "SELECT id, lower(s), upper(s) FROM {t}"),
    ("lower neq", "SELECT id FROM {t} WHERE lower(s) <> s"),
    ("length", "SELECT id, length(s), length(v), length(c), octet_length(v) FROM {t}"),
    ("trailing eq", "SELECT id FROM {t} WHERE s = 'abc'"),
    ("trailing eq char", "SELECT id FROM {t} WHERE c = 'ab'"),
    ("distinct trailing", "SELECT DISTINCT s FROM {t}"),
    ("group trailing", "SELECT s, count(*) FROM {t} GROUP BY s"),
    ("distinct case", "SELECT DISTINCT lower(s) FROM {t}"),
    ("reverse char", "SELECT id, reverse(c) FROM {t}"),
    ("concat null", "SELECT id, concat(s, v), s || v, concat_ws('-', s, v) FROM {t}"),
    ("lpad rpad", "SELECT id, lpad(s, 5, '-'), rpad(s, 2, '*') FROM {t}"),
    ("trim chars", "SELECT id, trim(s, 'A'), ltrim(s, 'a'), rtrim(s, ' c') FROM {t}"),
    ("left right neg", "SELECT id, left(s, -1), right(s, -1) FROM {t}"),
    ("repeat", "SELECT id, repeat(s, 2) FROM {t}"),
    ("string compare", "SELECT id FROM {t} WHERE s < 'abc'"),
    ("string minmax", "SELECT min(s), max(s), min(v), max(v) FROM {t}"),
    ("order by string", "SELECT s FROM {t} ORDER BY s NULLS LAST, id LIMIT 10"),
    # G comparisons and NULL logic
    ("is true", "SELECT id, (i > j) IS TRUE, (i > j) IS NOT FALSE FROM {t}"),
    ("distinct from", "SELECT id, i IS DISTINCT FROM j, i IS NOT DISTINCT FROM NULL FROM {t}"),
    ("least greatest", "SELECT id, least(i, j), greatest(i, j, 0) FROM {t}"),
    ("coalesce nullif", "SELECT id, coalesce(i, j, -1), nullif(i, 7) FROM {t}"),
    ("case null", "SELECT id, CASE WHEN flag THEN 'y' WHEN NOT flag THEN 'n' ELSE '?' END FROM {t}"),
    ("bool filter", "SELECT id FROM {t} WHERE flag"),
    ("not bool", "SELECT id FROM {t} WHERE NOT flag"),
    ("in null", "SELECT id FROM {t} WHERE i IN (7, NULL)"),
    ("not in null", "SELECT id FROM {t} WHERE i NOT IN (7, NULL)"),
    ("not in subq null", "SELECT id FROM {t} WHERE i NOT IN (SELECT i FROM {u})"),
    ("not in subq", "SELECT id FROM {t} WHERE i NOT IN (SELECT i FROM {u} WHERE i IS NOT NULL)"),
    ("between", "SELECT id FROM {t} WHERE d BETWEEN 0.99 AND 2.5"),
    # H dates
    ("date + hours", "SELECT id, dt + INTERVAL 36 HOUR FROM {t}"),
    ("date + month", "SELECT id, dt + INTERVAL 1 MONTH, dt - INTERVAL 1 MONTH FROM {t} WHERE dt < DATE '9999-01-01' AND dt > DATE '0001-12-31'"),
    ("date + year", "SELECT id, dt + INTERVAL 1 YEAR FROM {t} WHERE dt < DATE '9999-01-01'"),
    ("date + int", "SELECT id, dt + 1 FROM {t} WHERE dt < DATE '9999-01-01'"),
    ("date diff", "SELECT id, dt - DATE '2024-01-01', date_diff('month', DATE '2024-01-01', dt) FROM {t}"),
    ("ts + interval", "SELECT id, ts + INTERVAL 90 MINUTE, ts - INTERVAL '1 day 12:30:45' FROM {t} WHERE year(ts) BETWEEN 1000 AND 9000"),
    ("extract", "SELECT id, year(dt), month(dt), day(dt), dayofweek(dt), dayofyear(dt), quarter(dt), week(dt) FROM {t}"),
    ("extract ts", "SELECT id, hour(ts), minute(ts), second(ts), microsecond(ts), epoch(ts) FROM {t}"),
    ("date trunc", "SELECT id, date_trunc('month', dt), date_trunc('week', dt), date_trunc('hour', ts) FROM {t}"),
    ("last day", "SELECT id, last_day(dt) FROM {t}"),
    ("cast ts date", "SELECT id, CAST(ts AS DATE), CAST(dt AS TIMESTAMP) FROM {t}"),
    ("strftime", "SELECT id, strftime(dt, '%Y-%m-%d') FROM {t}"),
    # I aggregates
    ("count distinct", "SELECT g, count(DISTINCT i), sum(DISTINCT i), avg(DISTINCT i) FROM {t} GROUP BY g"),
    ("filter agg", "SELECT sum(i) FILTER (WHERE j > 0), count(*) FILTER (WHERE flag) FROM {t}"),
    ("empty agg", "SELECT sum(i), count(i), count(*), min(s), avg(d) FROM {t} WHERE id < 0"),
    ("empty group", "SELECT g, sum(i) FROM {t} WHERE id < 0 GROUP BY g"),
    ("bool agg", "SELECT bool_and(flag), bool_or(flag) FROM {t}"),
    ("string_agg", "SELECT g, string_agg(s, ',' ORDER BY id) FROM {t} GROUP BY g"),
    ("group expr", "SELECT i + 1 AS k, count(*) FROM {t} GROUP BY i + 1"),
    ("having", "SELECT g, sum(i) FROM {t} GROUP BY g HAVING sum(i) > 0"),
    # J grouping sets
    ("grouping sets dup", "SELECT g, flag, count(*) FROM {t} GROUP BY GROUPING SETS ((g), (flag), (g))"),
    ("rollup", "SELECT g, flag, count(*), grouping(g) FROM {t} GROUP BY ROLLUP (g, flag)"),
    ("cube", "SELECT g, flag, sum(i) FROM {t} GROUP BY CUBE (g, flag)"),
    ("rollup empty", "SELECT g, count(*) FROM {t} WHERE id < 0 GROUP BY ROLLUP (g)"),
    ("grouping sets null key", "SELECT g, count(*), grouping(g) FROM {t} GROUP BY GROUPING SETS ((g), ())"),
    # K windows
    ("rank ties", "SELECT id, rank() OVER (ORDER BY g), dense_rank() OVER (ORDER BY g), percent_rank() OVER (ORDER BY g), cume_dist() OVER (ORDER BY g) FROM {t}"),
    ("row_number part", "SELECT id, row_number() OVER (PARTITION BY g ORDER BY id) FROM {t}"),
    ("lag lead default", "SELECT id, lag(i) OVER (ORDER BY id), lead(i, 1, 100) OVER (ORDER BY id), lag(i, 2) OVER (ORDER BY id) FROM {t}"),
    ("rows frame", "SELECT id, sum(i) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING), sum(i) OVER (ORDER BY id ROWS UNBOUNDED PRECEDING) FROM {t}"),
    ("range frame default", "SELECT id, sum(i) OVER (ORDER BY g), min(i) OVER (ORDER BY g) FROM {t}"),
    ("range offset", "SELECT id, sum(i) OVER (ORDER BY id RANGE BETWEEN 2 PRECEDING AND CURRENT ROW) FROM {t}"),
    ("first last value", "SELECT id, first_value(i) OVER (PARTITION BY g ORDER BY id), last_value(i) OVER (PARTITION BY g ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) FROM {t}"),
    ("ntile", "SELECT id, ntile(3) OVER (ORDER BY id) FROM {t}"),
    ("window agg whole", "SELECT id, avg(i) OVER (), count(*) OVER (PARTITION BY g) FROM {t}"),
    ("window nulls order", "SELECT id, row_number() OVER (ORDER BY i DESC) FROM {t}"),
    # L ordering / M limit
    ("order nulls default asc", "SELECT id, i FROM {t} ORDER BY i, id LIMIT 4"),
    ("order nulls default desc", "SELECT id, i FROM {t} ORDER BY i DESC, id LIMIT 4"),
    ("order nulls first", "SELECT id, i FROM {t} ORDER BY i NULLS FIRST, id LIMIT 4"),
    ("order desc nulls last", "SELECT id, i FROM {t} ORDER BY i DESC NULLS LAST, id LIMIT 4"),
    ("offset only", "SELECT id FROM {t} ORDER BY id OFFSET 3"),
    ("limit offset", "SELECT id FROM {t} ORDER BY id LIMIT 2 OFFSET 3"),
    ("order cast", "SELECT id FROM {t} ORDER BY CAST(s AS VARCHAR), id LIMIT 7"),
    # N set operations
    ("except all", "SELECT i FROM {t} EXCEPT ALL SELECT i FROM {u}"),
    ("intersect all", "SELECT i FROM {t} INTERSECT ALL SELECT i FROM {u}"),
    ("except", "SELECT s FROM {t} EXCEPT SELECT s FROM {u}"),
    ("intersect", "SELECT s FROM {t} INTERSECT SELECT s FROM {u}"),
    ("union", "SELECT s FROM {t} UNION SELECT s FROM {u}"),
    ("union all", "SELECT i FROM {t} UNION ALL SELECT i FROM {u}"),
    # O joins
    ("left join where null", "SELECT t.id, u.id FROM {t} t LEFT JOIN {u} u ON t.i = u.i WHERE u.id IS NULL"),
    ("full join", "SELECT t.id, u.id FROM {t} t FULL JOIN {u} u ON t.id = u.id WHERE t.id IS NULL OR u.id IS NULL"),
    ("join string key", "SELECT t.id, u.id FROM {t} t JOIN {u} u ON t.s = u.s"),
    ("join ndf", "SELECT t.id, u.id FROM {t} t JOIN {u} u ON t.i IS NOT DISTINCT FROM u.i"),
    ("semi", "SELECT id FROM {t} t WHERE EXISTS (SELECT 1 FROM {u} u WHERE u.i = t.i)"),
    ("anti", "SELECT id FROM {t} t WHERE NOT EXISTS (SELECT 1 FROM {u} u WHERE u.i = t.i)"),
    ("in or", "SELECT id FROM {t} t WHERE t.i > 6 OR t.s IN (SELECT s FROM {u})"),
    ("row in", "SELECT id FROM {t} t WHERE (t.i, t.s) IN (SELECT i, s FROM {u})"),
    ("lateral", "SELECT t.id, x.s FROM {t} t, LATERAL (SELECT u.s FROM {u} u WHERE u.i = t.i ORDER BY u.id LIMIT 1) x"),
    # P subqueries
    ("scalar sum corr", "SELECT id, (SELECT sum(u.i) FROM {u} u WHERE u.id <= t.id) FROM {t} t"),
    ("scalar count corr", "SELECT id, (SELECT count(*) FROM {u} u WHERE u.i = t.i) FROM {t} t"),
    ("some", "SELECT id FROM {t} t WHERE t.i <> SOME (SELECT i FROM {u} u WHERE u.i IS NOT NULL)"),
    ("all", "SELECT id FROM {t} t WHERE t.i > ALL (SELECT i FROM {u} u WHERE u.i IS NOT NULL)"),
    ("nested corr", "SELECT id FROM {t} t WHERE t.i < (SELECT sum(u.i) FROM {u} u WHERE u.id <= t.id AND u.i > (SELECT count(*) FROM {u} w WHERE w.id < u.id))"),
]


def norm(x):
    if isinstance(x, float):
        if math.isnan(x):
            return "nan"
        return float(f"{x:.9g}")
    if hasattr(x, "is_finite"):
        return float(f"{float(x):.12g}")
    if isinstance(x, (list, tuple)):
        return tuple(norm(y) for y in x)
    return x


def run(con, sql):
    try:
        return sorted((tuple(norm(x) for x in r) for r in con.execute(sql).fetchall()), key=repr), None
    except Exception as e:  # noqa: BLE001
        return None, type(e).__name__ + ": " + str(e).splitlines()[0][:160]


def main():
    con = duckdb.connect(config={"allow_unsigned_extensions": "true"})
    con.execute(f"LOAD '{LOADABLE}'")
    con.execute(f"ATTACH '{DSN}' AS rp (TYPE fabricator, remote_pushdown 'true')")
    con.execute(f"ATTACH '{DSN}' AS db (TYPE fabricator, remote_pushdown 'false')")
    con.execute("SELECT fabricator_exec('db', ?, refresh_cache := true)", [SETUP])
    con.execute("SELECT fabricator_refresh_cache('rp')")
    con.execute("CALL enable_logging(['Fabricator.Remote'], storage := 'memory', level := 'debug')")
    only = set(sys.argv[1:])
    bad = 0
    for name, tpl in CASES:
        if only and name not in only:
            continue
        con.execute("CALL truncate_duckdb_logs()")
        got, gerr = run(con, tpl.format(t="rp.dbo.ed_t", u="rp.dbo.ed_u"))
        logs = [m for (m,) in con.execute(
            "SELECT message FROM duckdb_logs WHERE type = 'Fabricator.Remote'").fetchall()]
        pushed = any(m.startswith("remote") for m in logs)
        ref, rerr = run(con, tpl.format(t="db.dbo.ed_t", u="db.dbo.ed_u"))
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
        if pushed is False and only:
            print("        " + " | ".join(m[:150] for m in logs[:3]))
    print(f"\n{len(CASES) if not only else len(only)} cases, {bad} not ok")
    con.execute("SELECT fabricator_exec('db', 'DROP TABLE dbo.ed_t; DROP TABLE dbo.ed_u;')")


def cell(v):
    if v is None:
        return "NULL"
    if isinstance(v, bool):
        return "true" if v else "false"
    s = str(v)
    return "(empty)" if s == "" else s


def emit():
    """Writes the service suite: every case against `rp`, expected = what `db` (DuckDB evaluating) answers."""
    con = duckdb.connect(config={"allow_unsigned_extensions": "true"})
    con.execute(f"LOAD '{LOADABLE}'")
    con.execute(f"ATTACH '{DSN}' AS db (TYPE fabricator, remote_pushdown 'false')")
    con.execute("SELECT fabricator_exec('db', ?, refresh_cache := true)", [SETUP])
    w = []
    w.append("# name: test/verify_pushdown_edges_mssql.test")
    w.append("# description: SQL edge cases mined from substrait-java's semantics tests, each run with remote_pushdown")
    w.append("#              and compared with what DuckDB itself answers over the same SQL Server rows. GENERATED by")
    w.append("#              scripts/pushdown_edge_cases.py --emit — edit that, not this file. Found NOT EXISTS dropping")
    w.append("#              NULL-key rows and ROLLUP / CUBE losing the grand total on an empty input (2026-10-31).")
    w.append("# group: [fabricator]")
    w.append("")
    w.append("require fabricator")
    w.append("")
    w.append("require-env MSSQL_TESTDB_DSN")
    w.append("")
    w.append("statement ok")
    w.append("ATTACH '${MSSQL_TESTDB_DSN}' AS rp (TYPE fabricator, remote_pushdown 'true');")
    w.append("")
    w.append("statement ok")
    w.append("SELECT fabricator_exec('rp', '" + SETUP.strip().replace("\n", " ").replace("'", "''") + "', refresh_cache := true);")
    w.append("")
    for name, tpl in CASES:
        local = tpl.format(t="db.dbo.ed_t", u="db.dbo.ed_u")
        pushed = tpl.format(t="rp.dbo.ed_t", u="rp.dbo.ed_u")
        w.append(f"# {name}")
        try:
            con.execute(local)
            booleans = [str(d[1]).upper() == "BOOLEAN" for d in con.description]
            # formatted by DuckDB, the way the runner prints a value (a timestamp's fraction, a double's .0); a
            # BOOLEAN as 0 / 1, which is what the runner sorts a `rowsort` result by
            rows = [tuple({"true": "1", "false": "0"}.get(v, v) if b else v for v, b in zip(row, booleans))
                    for row in con.execute(f"SELECT COLUMNS(*)::VARCHAR FROM ({local})").fetchall()]
            n = len(con.description)
        except Exception:  # noqa: BLE001
            # DuckDB refuses it (an overflow, a division by zero, …): the pushed run must fail too; the wording may
            # differ between the two paths, so any error passes
            w.append("statement error")
            w.append(pushed)
            w.append("----")
            w.append("<REGEX>:.*")
            w.append("")
            continue
        w.append(f"query {'T' * n} rowsort")
        w.append(pushed)
        w.append("----")
        for r in sorted(tuple(cell(v) for v in row) for row in rows):
            w.append("\t".join(r))
        w.append("")
    w.append("statement ok")
    w.append("SELECT fabricator_exec('rp', 'DROP TABLE dbo.ed_t; DROP TABLE dbo.ed_u;', refresh_cache := true);")
    con.execute("SELECT fabricator_exec('db', 'DROP TABLE dbo.ed_t; DROP TABLE dbo.ed_u;')")
    with open(REPO_TEST, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(w) + "\n")
    print(f"{REPO_TEST}: {len(CASES)} cases")


if __name__ == "__main__":
    if sys.argv[1:] == ["--emit"]:
        emit()
    else:
        main()
