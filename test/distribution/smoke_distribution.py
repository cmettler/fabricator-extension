#!/usr/bin/env python
"""Smoke test for the single-file distribution artifact (docs/distribution-installer.md).

Deliberately NOT a sqllogictest: the repo's `unittest` binary statically embeds fabricator, so a
chain-loaded core would collide with already-registered functions there. This must run against a
STOCK DuckDB whose python wheel version matches the core the artifact was built for.

    pip install duckdb==1.5.5
    python test/distribution/smoke_distribution.py build/distribution/windows_amd64/fabricator.duckdb_extension

Covers: fresh install into an empty extension directory with no environment variables; the CLR
booting from the extracted managed dir; a real Delta round trip through the extracted core; the
second load taking the fast path; and the two failure modes that must not touch disk (a version
mismatch, and an artifact carrying no payload).
"""
from __future__ import annotations

import glob
import os
import shutil
import subprocess
import sys
import tempfile
import time

FAILURES: list[str] = []


def check(condition: bool, description: str, detail: str = "") -> None:
    print(f"  {'PASS' if condition else 'FAIL'}  {description}{'  ' + detail if detail else ''}")
    if not condition:
        FAILURES.append(description)


def connect(duckdb, extension_directory: str):
    con = duckdb.connect(config={"allow_unsigned_extensions": True})
    con.execute(f"SET extension_directory='{extension_directory.replace(os.sep, '/')}'")
    return con


def test_fresh_install(duckdb, artifact: str) -> None:
    print("\n[1] fresh install, no environment variables")
    extension_directory = tempfile.mkdtemp(prefix="fabdist_")
    try:
        con = connect(duckdb, extension_directory)
        started = time.time()
        con.execute(f"LOAD '{artifact}'")
        cold = time.time() - started

        check(True, "LOAD succeeded", f"({cold:.2f}s cold)")
        check(
            con.execute("select fabricator_version()").fetchone()[0] is not None,
            "the core registered its functions",
        )
        # hilbert_index and bucket are registered only if the managed bridge booted, and evaluating
        # one crosses the C++/C# ABI - so this proves the CLR came up, not merely that C++ loaded.
        check(
            con.execute("select hilbert_index([1,2], 4), bucket(8, 'alice')").fetchone() == (7, 5),
            "the managed bridge booted and answers (zero configuration)",
        )
        # THE FLUID TEMPLATE ENGINE, which ships as a BUILT-IN provider assembly since 2026-09-02 --
        # publish-managed.ps1 puts Fabricator.FluidPlugin in the managed directory and BackendRegistry
        # discovers it by name, exactly like Fabricator.Delta.
        #
        # /!\ IT WAS A BUNDLED PLUGIN UNTIL THEN, and this check attributed it BY ROOT for a reason that
        # still applies in its new form: this session sets no environment variables, so the per-user plugin
        # root (~/.duckdb/fabricator/plugins) is searched too, and a developer who had INSTALLED Fluid there
        # would make a bare "does fluid_render work" check pass on an artifact shipping nothing. The
        # root check is replaced by its exact counterpart for a built-in: Fluid must contribute NO plugin
        # row at all. A copy loaded from a plugin root would show up here, and the render below would then
        # be proving somebody's local install.
        fluid_rows = con.execute(
            "select status, root from fabricator_plugins() where provider = 'fluid'"
        ).fetchall()
        check(
            len(fluid_rows) == 0,
            "Fluid ships as a built-in provider, not as a plugin (no plugin row claims it)",
            str(fluid_rows),
        )
        check(
            con.execute("select fluid_render('builtin {{ who }}', {'who': 'provider'})").fetchone()[0]
            == "builtin provider",
            "the built-in Fluid renders (its package closure shipped in the managed directory)",
        )
        # /!\ A SECOND REGISTRATION PATH, not a second spelling of the one above. fluid_render arrives
        # through IBackend.GlobalScalarFunctions and is registered as a DuckDB scalar; fluid_query arrives
        # through GlobalSqlTableFunctions and is registered as a bind_replace TABLE function. Fluid is the
        # only shipped provider using the latter at all, so a packaging or registration fault that reached
        # only the sqlgen path would leave the check above passing.
        check(
            con.execute(
                "select * from fluid_query('select {{ n }} as n', params := {'n': 41})"
            ).fetchone()[0] == 41,
            "the built-in Fluid's SQL-generating table function binds and its generated SQL runs",
        )

        loaded = [r[0] for r in con.execute(
            "select extension_name from duckdb_extensions() "
            "where extension_name like 'fabricator%' and loaded order by 1").fetchall()]
        check(loaded == ["fabricator", "fabricator_core"],
              "both the installer and the core report as loaded", str(loaded))

        extracted = sorted(os.path.basename(p) for p in
                           glob.glob(os.path.join(extension_directory, "*", "*", "*")))
        check(
            extracted == ["fabricator", "fabricator_core.duckdb_extension", "fabricator_core.payload.sha"],
            "the extension directory holds exactly the core, the managed dir and the marker",
            str(extracted),
        )

        lake = tempfile.mkdtemp(prefix="fabdist_lake_").replace(os.sep, "/")
        try:
            con.execute(f"ATTACH '{lake}' AS lake (TYPE fabricator, PROVIDER 'delta')")
            con.execute("CREATE TABLE lake.main.t AS SELECT i AS id, i * 2 AS v FROM range(5) t(i)")
            check(con.execute("select count(*), sum(v) from lake.main.t").fetchone() == (5, 20),
                  "a Delta write/read round trip works through the extracted core")
        finally:
            con.close()
            shutil.rmtree(lake, ignore_errors=True)

        # Second session over the same directory: the marker must short-circuit extraction.
        con = connect(duckdb, extension_directory)
        started = time.time()
        con.execute(f"LOAD '{artifact}'")
        warm = time.time() - started
        check(con.execute("select hilbert_index([3,4], 4)").fetchone()[0] == 53,
              "a second session loads and works", f"({warm:.2f}s warm)")
        check(warm < cold, "the second load took the fast path", f"({warm:.2f}s < {cold:.2f}s)")
        con.close()
    finally:
        shutil.rmtree(extension_directory, ignore_errors=True)


def test_rejects(duckdb, artifact: str, number: int, title: str, expected: str) -> None:
    print(f"\n[{number}] {title}")
    extension_directory = tempfile.mkdtemp(prefix="fabdist_")
    try:
        con = connect(duckdb, extension_directory)
        try:
            con.execute(f"LOAD '{artifact}'")
            check(False, "the load was rejected", "it succeeded instead")
        except Exception as error:  # noqa: BLE001 - any load failure is what we are asserting
            message = str(error).replace("\n", " ")
            check(expected in message, f"the error explains the problem ({expected!r})", message[:160])
        finally:
            con.close()
        check(glob.glob(os.path.join(extension_directory, "**", "*"), recursive=True) == [],
              "nothing was written to the extension directory")
    finally:
        shutil.rmtree(extension_directory, ignore_errors=True)


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2

    artifact = os.path.abspath(sys.argv[1]).replace(os.sep, "/")
    if not os.path.exists(artifact):
        print(f"artifact not found: {artifact}")
        return 2

    try:
        import duckdb  # noqa: PLC0415 - imported late so --help works without the wheel
    except ImportError:
        print("this harness needs the duckdb python wheel: pip install duckdb==<the core's version>")
        return 2

    for variable in ("FABRICATOR_MANAGED_DIR", "FABRICATOR_DOTNET_ROOT"):
        os.environ.pop(variable, None)

    print(f"artifact : {artifact} ({os.path.getsize(artifact):,} bytes)")
    print(f"duckdb   : {duckdb.__version__}")

    test_fresh_install(duckdb, artifact)

    # Optional negatives, built by `pack-distribution.ps1 -WithNegatives` as SIBLINGS of the real
    # artifact: they must be per-platform, because DuckDB checks the footer's platform field before
    # any extension code runs, so a Windows negative tells you nothing on Linux.
    directory = os.path.dirname(artifact)
    wrong_version = os.path.join(directory, "_negative", "fabricator.duckdb_extension").replace(os.sep, "/")
    no_payload = os.path.join(directory, "_nopayload", "fabricator.duckdb_extension").replace(os.sep, "/")

    if os.path.exists(wrong_version):
        test_rejects(duckdb, wrong_version, 2, "version mismatch",
                     "This fabricator distribution targets DuckDB")
    if os.path.exists(no_payload):
        test_rejects(duckdb, no_payload, 3, "artifact without a payload",
                     "does not contain a fabricator payload")

    print()
    if FAILURES:
        print(f"FAILED: {len(FAILURES)} check(s): " + "; ".join(FAILURES))
        return 1

    print("all checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
