#!/usr/bin/env bash
#
# Runs a TIER of suites, and fails loudly unless every selected suite actually ran.
#
#   run-suites.sh hermetic   (default) needs nothing but a scratch dir and the in-repo fixtures
#   run-suites.sh service              needs the docker/docker-compose.yml stack: SQL Server + MinIO
#
# One runner for both so the assertions below — the part with the actual value — exist once. Used by
# CI and usable as-is on a dev machine.
#
# ONE PROCESS PER SUITE, with a FRESH scratch directory each. Both halves are load-bearing, and
# were established by watching the alternative fail:
#
#   * Batching the whole set into one `unittest -f <list>` invocation SIGSEGVs partway through
#     (observed at suite 41/53, in Apache.Arrow's ImportedArrowArrayStream finalizer during
#     GC.RunFinalizers) and reports spurious failures. One CLR is booted for the process, so state
#     and finalizers from earlier suites outlive them and run during later ones. Every suite passes
#     in isolation.
#   * Sharing one scratch directory across suites makes them collide on table paths; CLAUDE.md's
#     documented recipe gives each file its own.
#
# The per-suite cost is a CLR boot (~1-2s), which is the price of isolation.
#
# ASSERTIONS THIS MAKES, beyond a zero exit status — none of which `unittest` gives you for free:
#   * every selected suite reports "All tests passed"
#   * NOTHING skips. A skip here means an environment variable CI thought it set is missing, or a
#     `require`d extension is not linked; either way the suite silently tested nothing.
#   * the runner never says "No tests ran". A filter that matches no test case exits ZERO, so a
#     mistyped suite path would otherwise be a permanently green no-op.
#
# Environment:
#   UNITTEST   path to the unittest binary   (default: build/release/test/unittest[.exe])
#   MANAGED    path to the published bridge  (default: build/release/extension/fabricator/fabricator)
set -uo pipefail   # deliberately NOT -e: collect every failure, then report them together
cd "$(dirname "$0")/.."

TIER=${1:-hermetic}
case "$TIER" in
    hermetic)
        SELECT_CMD=scripts/list-hermetic-suites.sh
        # Floors measured 2026-07-25: 53 suites / 4152 assertions, all green.
        # 54 / 4199 since 2026-07-29: verify_delta_autocommit_pin (34) pins that an autocommit CODEC statement
        # establishes ONE snapshot pin per (statement, table) however many times it names the table. Raised in
        # the same commit, per the error text below. Note 4165 - 4152 = 13 of the gap predates this: the floor
        # had not been raised for verify_delta_catalog_variant's +13 (the CAST(NULL AS VARIANT) backfill fix).
        # 4197 since 2026-07-29: verify_delta_rename lost 2 (the redundant PROVIDER alias 'deltalake' was
        # REMOVED; its two positive assertions became one negative pin plus a no-catalog-left-behind pin).
        # A floor LOWERED for a deliberate removal, which is the one legitimate reason to lower it.
        # 4206 since 2026-07-29: verify_delta_clustered_optimize gained 9 (§8 pins that a clustering-declared
        # table on a catalog WITHOUT the native writer WARNS instead of silently bin-packing).
        # 4208 since 2026-07-29: verify_delta_catalog_transactions gained 2 (the ROLLBACK atomicity pin for
        # the buffered identity append the native-write default exposed).
        # 58 RUNS / 5290 since 2026-07-29, all measured: the four engine-parameterized suites each run a
        # SECOND time on the codec engine (+1065 — write 31, transactions 943, update 63, delete 28, the
        # counts identical to their native leg, which is the point), and verify_delta_rename gained 17
        # (10 -> 27) becoming the pin that each PROVIDER spelling selects its documented engine.
        # NOTE the floor is on RUNS, not distinct suites: 54 suites, 58 runs. A suite dropping out of the
        # tier still trips it, which is what the floor is for.
        # 59 runs / 5326 since 2026-07-29: verify_delta_subplan_dedup (36) pins that the catalog scan
        # SERIALIZES its table identity, so DuckDB's common-subplan optimizer cannot conflate two scans of
        # different same-shaped tables and silently return one table's rows for both.
        # 5357 since 2026-07-29: verify_delta_autocommit_pin 34 -> 65. Sections 9-10 pin that an explicit AT
        # stays INDEPENDENT of the shared pin (it must neither consume nor seed it) on both engines, and
        # section 11 pins the NATIVE path's pin as shared-and-free after the redundant ResolveVersionAsOf
        # open was removed.
        # 60 runs / 5459 since 2026-07-30: verify_delta_mixed_engines (102) pins CROSS-ENGINE parquet
        # compatibility — native_read/native_write are INDEPENDENT options, so besides the two symmetric
        # profiles the provider names select there are two MIXED ones a user can ask for. Written by one
        # engine, read back by the other, over the same table path.
        : "${MIN_SUITES:=60}"
        : "${MIN_ASSERTIONS:=5459}"
        ;;
    service)
        SELECT_CMD=scripts/list-service-suites.sh
        # Floors measured 2026-07-25 against the compose stack: 42 suites / 1221 assertions, all green.
        # 1227 since 2026-07-26: verify_granular_types gained 6 (the SQL datetime2(7) -> Delta refusal and
        # the microsecond-cast workaround). Raised deliberately in the same commit, per the error text below.
        # 43 RUNS / 1388 since 2026-07-29: verify_delta_catalog_s3 runs a SECOND time on the codec engine
        # (+161, the same count as its native leg). 42 suites, 43 runs — the floor is on RUNS.
        : "${MIN_SUITES:=43}"
        : "${MIN_ASSERTIONS:=1388}"
        ;;
    *)
        echo "usage: $0 [hermetic|service]" >&2
        exit 2
        ;;
esac

if [ -z "${UNITTEST:-}" ]; then
    if [ -x build/release/test/unittest.exe ]; then
        UNITTEST=build/release/test/unittest.exe
    else
        UNITTEST=build/release/test/unittest
    fi
fi
MANAGED=${MANAGED:-build/release/extension/fabricator/fabricator}

if [ ! -e "$UNITTEST" ]; then
    echo "ERROR: unittest binary not found at '$UNITTEST' (build it, or set UNITTEST=)" >&2
    exit 1
fi
if [ ! -d "$MANAGED" ]; then
    echo "ERROR: managed bridge not found at '$MANAGED' (run scripts/publish-managed.ps1, or set MANAGED=)" >&2
    exit 1
fi

export FABRICATOR_MANAGED_DIR="$MANAGED"

# The fixture path is interpolated straight into SQL, so it has to be a path DuckDB understands: a
# Windows drive path rather than the MSYS /d/... form that $PWD carries under Git Bash.
if [ -n "${RUNNER_OS:-}" ] && [ "${RUNNER_OS}" = 'Windows' ] || [ "${OS:-}" = 'Windows_NT' ]; then
    export FABRICATOR_DELTA_DIR="$(pwd -W)/test/fixtures/delta_simple"
else
    export FABRICATOR_DELTA_DIR="$PWD/test/fixtures/delta_simple"
fi

if [ "$TIER" = 'hermetic' ]; then
    # Anything that would let a service-dependent suite run half-configured is cleared, so this set
    # is PROVABLY the hermetic one rather than hermetic by assumption.
    unset MSSQL_TESTDB_DSN MSSQL_TEST_SERVER MSSQL_TEST_CONNECTION_STRING MSSQL_BINCOLL_DSN \
          MSSQL_TESTDB_URI MSSQL_TEST_PASS FABRICATOR_S3_ENDPOINT FABRICATOR_S3_SQL_ENDPOINT \
          FABRICATOR_DAX_DSN FABRICATOR_PLUGIN_DIR 2>/dev/null || true
else
    # Fail fast on a missing variable. Without this the affected suite would merely SKIP, the run
    # would still be green, and the coverage would be silently gone — the exact failure mode this
    # script exists to prevent.
    missing=''
    for v in MSSQL_TESTDB_DSN MSSQL_TEST_SERVER MSSQL_TEST_CONNECTION_STRING MSSQL_TESTDB_URI \
             MSSQL_TEST_PASS MSSQL_BINCOLL_DSN FABRICATOR_S3_ENDPOINT FABRICATOR_S3_SQL_ENDPOINT \
             FABRICATOR_PLUGIN_DIR; do
        eval "val=\${$v:-}"
        if [ -z "$val" ]; then
            missing="$missing $v"
        fi
    done
    if [ -n "$missing" ]; then
        echo "ERROR: the service tier needs these environment variables:$missing" >&2
        echo "       bring up docker/docker-compose.yml and run docker/provision.ps1 first." >&2
        exit 1
    fi
fi

suites=$("$SELECT_CMD")
if [ -z "$suites" ]; then
    echo "ERROR: no $TIER suites selected — $SELECT_CMD is broken." >&2
    exit 1
fi

# THE DOUBLED LEG. Since 2026-07-29 the PROVIDER NAME picks a default engine — 'delta' means DuckDB
# reads and writes the parquet bytes while engineered-wood owns the log, 'engineeredwooddelta' means
# EW's own codec does both. Most suites are pinned to whichever engine they are ABOUT. The ones listed
# below are not about an engine at all, so they interpolate ${DELTA_PROVIDER} and run once per engine.
# A divergence shows up as a failure in exactly one leg, which names the engine for you.
#
#   hermetic — write / transaction / update / delete semantics must come out IDENTICAL either way.
#   service  — the S3 suite, because object storage is where the two engines' file handling has
#              diverged most historically, and the flip moved the whole s3:// path onto the native
#              engine. Without this leg the codec path over object storage has NO coverage at all.
#
# Each entry below is "<suite><TAB><provider>". Suites that do not interpolate the variable simply
# ignore it, so the first leg can set it unconditionally.
case "$TIER" in
    hermetic)
        DOUBLED='test/verify_delta_catalog_write.test
test/verify_delta_catalog_transactions.test
test/verify_delta_catalog_update.test
test/verify_delta_catalog_delete.test'
        ;;
    service)
        DOUBLED='test/verify_delta_catalog_s3.test'
        ;;
esac

entries=$(printf '%s\n' "$suites" | sed 's/$/\tdelta/')
while read -r d; do
    [ -z "$d" ] && continue
    # Guard against the doubled list drifting away from the selected set: a rename or a
    # reclassification would otherwise silently drop the second leg while the run stayed green.
    if ! printf '%s\n' "$suites" | grep -qxF "$d"; then
        echo "ERROR: doubled-leg suite '$d' is not in the $TIER set (renamed or reclassified?)." >&2
        exit 1
    fi
    entries="$entries
$(printf '%s\tengineeredwooddelta' "$d")"
done <<EOF
$DOUBLED
EOF

expected=$(printf '%s\n' "$entries" | wc -l | tr -d ' ')
echo "Running $expected $TIER suite runs, one process each."
echo "  unittest: $UNITTEST"
echo "  managed : $MANAGED"
echo "  fixture : $FABRICATOR_DELTA_DIR"
echo

ran=0
failed=0
assertions=0
failures=''

# Snapshot the fixtures' git state so the check at the end can attribute a change to THIS RUN rather
# than to whatever the developer already had in progress. See the check for what it is guarding.
fixtures_before=''
have_git=0
if command -v git >/dev/null 2>&1 && git rev-parse --git-dir >/dev/null 2>&1; then
    have_git=1
    fixtures_before=$(git status --porcelain --untracked-files=all -- test/fixtures 2>/dev/null)
fi

while IFS="$(printf '\t')" read -r suite provider; do
    [ -z "$suite" ] && continue
    scratch=$(mktemp -d)
    export FABRICATOR_DELTA_WRITE_DIR="$scratch"
    export DELTA_PROVIDER="${provider:-delta}"

    # Only label the non-default leg, so the common output stays as it was.
    label="$suite"
    [ "$DELTA_PROVIDER" != 'delta' ] && label="$suite [$DELTA_PROVIDER]"

    output=$("$UNITTEST" --test-dir . "$suite" 2>&1)
    status=$?

    count=$(printf '%s' "$output" | grep -oE '[0-9]+ assertion' | grep -oE '[0-9]+' | head -1)
    [ -z "$count" ] && count=0

    if [ "$status" -ne 0 ]; then
        reason="exit status $status"
    elif printf '%s' "$output" | grep -q 'No tests ran'; then
        reason='the filter matched no test case (exits zero — would be a silent no-op)'
    elif printf '%s' "$output" | grep -qE 'skipped test'; then
        reason="skipped: $(printf '%s' "$output" | grep -A3 'Skipped tests' | tail -n +2 | tr '\n' ' ')"
    elif ! printf '%s' "$output" | grep -q 'All tests passed'; then
        reason='did not report "All tests passed"'
    else
        reason=''
    fi

    if [ -n "$reason" ]; then
        failed=$((failed + 1))
        failures="${failures}
  $label — $reason"
        printf 'FAIL  %-58s %s\n' "$label" "$reason"
        printf '%s\n' "$output" | tail -25
        echo "      (scratch kept for inspection: $scratch)"
    else
        ran=$((ran + 1))
        assertions=$((assertions + count))
        printf 'ok    %-58s %6s assertions\n' "$label" "$count"
        rm -rf "$scratch"
    fi
done <<EOF
$entries
EOF

echo
echo "================================================================"
echo "suite runs      : $expected"
echo "suites passed   : $ran"
echo "suites failed   : $failed"
echo "assertions      : $assertions"

if [ "$failed" -ne 0 ]; then
    echo "FAILURES:$failures"
    exit 1
fi
if [ "$ran" -ne "$expected" ]; then
    echo "ERROR: $ran of $expected suites ran." >&2
    exit 1
fi

# The in-repo fixtures are read-only reference data; a suite that writes into them dirties the working
# tree and — as happened on 2026-07-29 — gets its droppings COMMITTED by the next `git add`. That leak
# was invisible for a whole session: `verify_delta_catalog_constraints` attaches test/fixtures and
# attempts INSERTs that must fail, which wrote nothing while the codec was the default and started
# leaving one orphan .parquet per failed INSERT the moment `native_write` became it. Cheap to assert,
# so assert it. Compared against the pre-run snapshot, so a developer's own in-progress fixture edit
# is not reported as this run's doing. Skipped when git is unavailable or this is not a checkout.
if [ "$have_git" -eq 1 ]; then
    fixtures_after=$(git status --porcelain --untracked-files=all -- test/fixtures 2>/dev/null)
    if [ "$fixtures_after" != "$fixtures_before" ]; then
        echo "ERROR: this run modified test/fixtures — those are committed, read-only inputs." >&2
        echo "  before:${fixtures_before:+$(printf '\n%s' "$fixtures_before")}" >&2
        echo "  after :${fixtures_after:+$(printf '\n%s' "$fixtures_after")}" >&2
        echo "       find the suite that writes there and point it at FABRICATOR_DELTA_WRITE_DIR" >&2
        echo "       (or pin it to the codec engine, which writes nothing on a refused statement)." >&2
        exit 1
    fi
fi

# Floors on the SELECTED set (values set per tier at the top). Without these, a suite that quietly
# gains a `require-env` drops out of its tier and CI stays green with less coverage: `ran == expected`
# would still hold, because `expected` shrank too. Floors rather than equalities, so adding suites and
# assertions never breaks the build.
if [ "$expected" -lt "$MIN_SUITES" ]; then
    echo "ERROR: only $expected suite RUNS were selected, floor is $MIN_SUITES. A suite likely gained a" >&2
    echo "       require-env and silently left the $TIER set — or a doubled leg stopped being added." >&2
    echo "       Check $SELECT_CMD and the DOUBLED list above," >&2
    echo "       and if the drop is intended, lower MIN_SUITES here in the same commit." >&2
    exit 1
fi
if [ "$assertions" -lt "$MIN_ASSERTIONS" ]; then
    echo "ERROR: $assertions assertions ran, floor is $MIN_ASSERTIONS — coverage went backwards." >&2
    exit 1
fi

echo "ALL GREEN"
