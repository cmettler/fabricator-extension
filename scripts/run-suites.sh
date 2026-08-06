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
        # 5471 since 2026-07-30: verify_delta_row_level_concurrency gained 12 (§10 pins that a row-level
        # DELETE under `serializable` still lands across a concurrent COMPACTION — it ABORTED before the
        # engineered-wood bump, and nothing covered it on either side).
        # 61 runs / 5521 since 2026-07-30: verify_macros_catalog (50) pins CATALOG-BOUND provider macros —
        # resolution of both kinds through db.schema.m(...), that the BARE name does NOT resolve (the one
        # assertion separating catalog-bound from the load-time global form), and KIND FILTERING in both
        # directions (the binder Cast<>s on the entry type without checking, so the wrong kind is an unchecked
        # bad cast rather than a clean error).
        # 5537 since 2026-07-30: verify_host_query gained 16 (15 -> 31). The table function now adopts the
        # CALLER's search path + TimeZone, so `USE s; SELECT … FROM fabricator_host_query('… FROM t')` resolves
        # instead of failing against the fresh connection's default memory.main. TimeZone is asserted as a
        # rendered VALUE, not just current_setting(), since the label alone would pass without reaching the
        # computation. Suite count unchanged — the suite already existed.
        # 62 runs / 5558 since 2026-07-30: verify_delta_catalog_functions (21) pins CATALOG-BOUND CUSTOM
        # FUNCTIONS on the Delta catalog — until then all seven of its function ABI members threw and the
        # FUNCTIONS metadata kind fell to a 1-column empty fallback. It also pins the ZERO-ARGUMENT shape, which
        # needs BOTH halves of a real fix: an empty parameter schema is unrepresentable in Apache.Arrow's C
        # interface (export AND import throw on 'fields'), so the host passes no args stream for an
        # argument-less function and the bridge exports the empty schema itself. A regression in either half
        # makes such a function silently VANISH rather than error, which is why it is asserted here.
        # 5564 since 2026-07-31: verify_delta_catalog_functions gained 6 (21 -> 27) for NAMED parameters on a
        # custom table function — both `:=` and `=>` spellings, that the argument really crosses the ABI (an
        # unknown value yields NO rows rather than all of them), that a misspelled name is a clean BINDER error
        # rather than silently ignored, and that a named parameter is NOT positionally callable.
        # 5573 since 2026-07-31: verify_global_functions gained 9 (63 -> 72) — the demo global fabricator_seq
        # now has a MIXED signature (positional n + named start), which is the combination that can fail
        # SILENTLY: the host marshals every declared parameter, substituting a typed NULL for an omitted named
        # one, so an off-by-one there corrupts the POSITIONAL value rather than erroring.
        # 63 suites / 5607 since 2026-07-31: NEW verify_delta_last_checkpoint (34) — an empty/corrupt/
        # field-less _last_checkpoint must fall back to listing _delta_log instead of failing the read.
        # Regression for a LIVE OneLake multi-writer failure (the hint file is updated by non-atomic
        # OVERWRITE, so a concurrent reader could see it at ZERO bytes and die in JsonDocument.Parse).
        # Pinned hermetically by writing the corrupt states directly — a live race only sometimes collides,
        # so it cannot serve as the gate.
        # 5623 since 2026-08-01: verify_delta_tblproperties 42 -> 58, for the isolation-default FLIP
        # (catalog default write_serializable -> serializable, matching Fabric Spark) and the removal of the
        # automatic create-time delta.isolationLevel stamp. The added sections pin the DEFAULT itself — every
        # other isolation assertion in the tree now states its level explicitly, so without this a regression
        # in the default would fail nothing — plus that neither level auto-stamps and that an explicit
        # WITH ("delta.isolationLevel"=...) still does.
        # 5639 since 2026-08-01: verify_delta_row_level_concurrency 82 -> 93 (§11). The EW bump made
        # DeltaTransaction's whole-table-read exemption an explicit opt-in
        # (ExemptRowLevelFromWholeTableRead) instead of unconditional behaviour, and NOTHING failed when it
        # was left unset — §11 is the section that fails. It drives a non-pushable DELETE (so the scan
        # declares the whole table) against a concurrent DELETE of a DIFFERENT row of a DIFFERENT file: with
        # the opt-in they compose, without it the declaration meets the concurrent commit and aborts.
        # 5640 since 2026-08-01: verify_delta_catalog_time_travel 48 -> 49. Upstream EW #36 made an
        # INCOMPLETE LOG REPLAY an error instead of a silence, which changes what AT (VERSION => n) does past
        # the end of the log: it used to return the NEWEST snapshot under the requested label, so a stale pin
        # or an off-by-one silently got real rows for a version that does not exist. Nothing pinned either
        # answer before, so the behaviour could have flipped back unnoticed in whichever direction.
        # 5654 since 2026-08-02: verify_delta_txn_version 51 -> 65, §9 — a REFUSED flush now takes back the
        # deletion vector it staged (EW #46's ledger + #49's fix). MEASURED before the change: the same shape
        # left a stray deletion_vector_*.bin forever. The section needs a delete LARGER than the 1 KB roaring
        # inline threshold, or the vector rides inside the commit json and there is no file to leak.
        : "${MIN_SUITES:=65}"
        # 5656 since 2026-08-02: verify_delta_catalog_transactions 943 -> 944 — ROLLBACK now RECLAIMS the
        # data files the transaction eagerly wrote (EW #52's DiscardDataFilesAsync) instead of leaving them
        # for VACUUM. +2, not +1: that suite is one of the DOUBLED ones below, so an assertion added to it
        # counts once per engine. The section had asserted the parquet count only BEFORE the rollback for a
        # year, which is exactly why the behaviour could change under it in silence.
        # 5710 since 2026-08-04: verify_delta_catalog_write 31 -> 43 for the CREATE-over-an-existing-table
        # refusal — the shared C++ CreateTable never checked ERROR_ON_CONFLICT, so a plain CTAS wrote no rows
        # and kept the OLD data (exit 0, no error) and a plain CREATE silently ignored its DECLARED SCHEMA.
        # ⚠ +24, not +12: verify_delta_catalog_write is one of the four DOUBLED suites, so each assertion
        # counts once per engine — a floor of 5668 from the standalone delta would have tolerated a
        # 12-assertion regression, the same trap the s3 note records on the service tier.
        # ⚠ 5686 - 5656 = 30 of this gap PREDATES the change: the floor was not raised for the unified
        # parameter protocol's coverage (verify_global_functions + the signature/mixed-arg pins). Closed here
        # rather than left, since a floor 30 below the actual silently tolerates a regression that large.
        # 65 runs / 6032 since 2026-08-05, taken from a green tier run (never computed): MERGE INTO landed, so
        # verify_merge_into (130) joins the DOUBLED list below — it is composed of exactly the update/delete/
        # insert paths those suites double, so a divergence must fail in one leg and name the engine. That is
        # +2 RUNS and +260 assertions; the remaining +20 is verify_delta_catalog_update 63 -> 73 (also doubled),
        # which gained the `SET col = DEFAULT` refusal. That last one is a CORRUPTION regression gate, not a
        # feature note: the operator used to read SET values by ORDINAL, and a DEFAULT contributes no
        # projection column, so it committed a shifted row (measured a=5,b=<rowid> for a correct a=99,b=5) or
        # fatally invalidated the database when the shifted types differed.
        # 6172 since 2026-08-05, from a green tier run: verify_merge_into 130 -> 200 (+70, doubled = +140).
        # The additions are the two places autocommit and an explicit transaction genuinely DIVERGE, both
        # measured: a merge carrying an UPDATE/DELETE action WORKS in autocommit on a table with deletion
        # vectors DISABLED and is REFUSED inside a transaction (the buffered path requires them — so reaching
        # for BEGIN to get atomicity can cost the statement, which is the opposite direction from the
        # atomicity trade-off and is pinned in BOTH directions with a positive control); and the change feed
        # of an autocommit merge is SPLIT across versions where the fused one reports a single version.
        # Plus the commitInfo.operation labels, which are an INTEROP contract: a fused merge commits as
        # TRANSACTION, never MERGE, so a consumer keying on the operation string will not match us.
        # 6190 since 2026-08-05, from a green tier run: verify_merge_into 200 -> 209 (+9, doubled = +18) when a
        # MULTI-ACTION merge became FORCED-BUFFERED. That was not a feature — it fixed SILENT DATA DESTRUCTION.
        # A merge's actions all address rows located by ONE join scan, and while they committed separately a
        # copy-on-write DELETE renumbered the rows a later action had already addressed: measured on a ONE-FILE
        # non-DV table, two conditional deletes left the wrong survivors and DESTROYED a row. §11 is that
        # regression gate (refusal + table intact + a single-action positive control), §11b the same shape on a
        # DV table asserting the answer AND the fusion. ⚠ Both tiers were GREEN THROUGH the bug, because every
        # earlier test put the affected rows in SEPARATE FILES where a rewrite renumbers nothing — so a floor
        # rise here is worth little unless the new assertions are single-file with the delete FIRST.
        # 6250 since 2026-08-05, from a green tier run: verify_merge_into 209 -> 239 after the forcing rule was
        # NARROWED TWICE. It now fires only when a merge carries >= 2 UPDATE/DELETE actions AND the table's row
        # identity is POSITIONAL (HasVirtualRowId()). Both narrowings removed a REFUSED CAPABILITY that bought no
        # safety: counting INSERT too refused the commonest shape (UPDATE+INSERT) on a non-DV table, and forcing
        # on SQL Server refused a 2-action merge into an identity EXTERNAL table. The added assertions pin BOTH
        # sides of each boundary, which is the point — a guard wider than its hazard fails as a lost capability,
        # which nothing complains about.
        : "${MIN_ASSERTIONS:=6284}"
        ;;
    service)
        SELECT_CMD=scripts/list-service-suites.sh
        # Floors measured 2026-07-25 against the compose stack: 42 suites / 1221 assertions, all green.
        # 1227 since 2026-07-26: verify_granular_types gained 6 (the SQL datetime2(7) -> Delta refusal and
        # the microsecond-cast workaround). Raised deliberately in the same commit, per the error text below.
        # 43 RUNS / 1388 since 2026-07-29: verify_delta_catalog_s3 runs a SECOND time on the codec engine
        # (+161, the same count as its native leg). 42 suites, 43 runs — the floor is on RUNS.
        # 44 RUNS / 1413 since 2026-07-31: verify_session_tag (+25) — fabricator_session_tag, which needs a
        # real server (it pins a provider connection and reads the session's own monitoring ids).
        : "${MIN_SUITES:=45}"
        # 1424 since 2026-08-01: verify_exec_invalidate_cache 10 -> 21, for the OUT-OF-BAND DROP path — the
        # catalog's self-heal, documented in CLAUDE.md and until now covered by NOTHING. The service tier ran
        # 44/44 green while that path was broken, which is why the section exists. It must run with
        # mssql_exec_invalidate_cache OFF: with the auto-invalidate ON (as the rest of that suite needs) the
        # DROP refreshes the whole cache and the name leaves the discovered list, so the lookup answers
        # "does not exist" WITHOUT ever fetching columns — the section then passes with the provider's
        # absence detection disabled, which is exactly what mutation-testing caught it doing.
        # 1444 since 2026-08-02: verify_delta_catalog_s3 161 -> 171, §11 — the attach-time warning for an
        # s3:// root opened READ_WRITE with no NAMED secret. MEASURED first: that shape loses 40 of 48
        # concurrent commits SILENTLY (§8.3). Two mutants, killed in opposite directions by ONE assertion —
        # suppressing the warning gives 0, ignoring access_mode gives 2 (the AUTOMATIC attach warns too),
        # which is what proves the new C++ access_mode plumbing is read rather than merely forwarded.
        # ⚠ +20, not +10: verify_delta_catalog_s3 is THE DOUBLED SUITE of this tier (see DOUBLED below), so
        # every assertion added to it counts ONCE PER ENGINE. A floor of 1434 was set first from the
        # standalone 161 -> 171 delta and would have silently tolerated a 10-assertion regression.
        # 1465 since 2026-08-04: verify_ctas_text_type 8 -> 15 — the SQL Server half of the CREATE-conflict
        # fix. SQL Server was never in the DANGEROUS half of that defect (its own CREATE TABLE rejects a
        # duplicate, so no write was lost); what changed is that the user gets the ordinary catalog error
        # instead of the raw provider 2714. Asserted on this tier because SQL Server SHARES the fixed C++
        # path with Delta, where the same gap discarded the write silently.
        # ⚠ 1458 - 1444 = 14 of this gap PREDATES the change (the unified parameter protocol pass raised the
        # actual and not the floor). Closed here for the same reason as the hermetic one.
        # ⚠ The floor is the MEASURED tier total (1465), not 1458 + the standalone delta. Both numbers in the
        # first draft of this note were ARITHMETIC rather than measured and both were wrong: the suite was 8
        # assertions, not 6 (I counted the statements I had added instead of running the suite before changing
        # it), so a floor of 1467 tripped the tripwire on a perfectly green 44/44 run and cost a re-run to
        # explain. Measure the BEFORE count while you still can, or take the floor from a green tier run.
        # 45 runs / 1583 since 2026-08-05, from a green tier run: verify_merge_into_mssql (106) is the
        # SQL Server half of MERGE INTO. It is the companion to the hermetic verify_merge_into and covers the
        # two things Delta structurally cannot: a COMPOUND rowid (a composite PK arrives as ONE struct-typed
        # column that ReferenceKeyColumns destructures — a Delta rowid is always a single virtual BIGINT), and
        # a table with NO row identity at all, which is the shape the !HasRowId() guard exists for (without it
        # an insert-only merge reads one past the chunk's width and fatally invalidates the database).
        : "${MIN_ASSERTIONS:=1583}"
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
test/verify_delta_catalog_delete.test
test/verify_merge_into.test'
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
