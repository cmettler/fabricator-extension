#!/usr/bin/env bash
#
# Prints the sqllogictest suites that need NOTHING from the environment except a writable scratch
# directory and the in-repo fixtures — no SQL Server, no MinIO, no Power BI Desktop, no live Fabric
# credentials, no opt-in native libraries. These are what CI can run on a bare runner, and they
# carry most of this project's coverage (the Delta catalog suites: transactions, row tracking,
# column mapping, native write/read, clustered optimize, ...).
#
# Derived rather than hardcoded, so a newly added hermetic suite is picked up automatically and
# cannot silently sit outside CI. A suite qualifies when ALL THREE hold:
#
#   1. every `require-env` it declares is one CI can satisfy itself:
#        FABRICATOR_DELTA_WRITE_DIR  — a scratch directory
#        FABRICATOR_DELTA_DIR        — test/fixtures/delta_simple, committed to this repo
#   2. every `require <extension>` it declares is statically linked into the test binary
#      (extension_config.cmake: fabricator, json, icu, parquet, httpfs), and
#   3. it does not ATTACH the `deltars` provider.
#
# Rule 2 matters because a `require` for an unlinked extension makes the runner SKIP the file, and
# CI asserts that nothing in this set skips — an unlinked requirement would otherwise become a
# permanently green no-op.
#
# Rule 3 covers an UNDECLARED dependency: the delta-rs suites gate only on
# FABRICATOR_DELTA_WRITE_DIR, but they also need `publish-managed.ps1 -IncludeDeltaRs` (DeltaLake.dll
# plus ~240 MB of native delta-rs/delta-kernel libraries), which the default publish omits. Without
# it they FAIL rather than skip (verified: `PROVIDER 'deltars'` errors at ATTACH), so they would turn
# CI red for an environmental reason. Detecting the provider in the file body rather than matching
# `verify_delta_rs*` keeps this self-maintaining. The cleaner long-term fix is for those suites to
# declare the requirement themselves (a `require-env` of their own, as every other optional surface
# in this repo does), after which this rule becomes dead weight and can go.
#
# Usage:  scripts/list-hermetic-suites.sh > suites.txt
#         build/release/test/unittest.exe --test-dir . -f suites.txt
set -euo pipefail
cd "$(dirname "$0")/.."

LINKED='fabricator json icu parquet httpfs'
SATISFIABLE_ENV='FABRICATOR_DELTA_WRITE_DIR FABRICATOR_DELTA_DIR'

for f in test/*.test; do
    qualifies=1

    # (1) `^require-env NAME` — anchored so a prose mention in a comment cannot match.
    while read -r name; do
        [ -z "$name" ] && continue
        case " $SATISFIABLE_ENV " in
            *" $name "*) ;;
            *) qualifies=0 ;;
        esac
    done < <(grep -E '^require-env[[:space:]]+' "$f" 2>/dev/null | tr -d '\r' | awk '{print $2}')

    # (2) `^require NAME` — the whitespace in the pattern is what keeps this from also matching
    # `require-env`, whose next character is a hyphen.
    while read -r ext; do
        [ -z "$ext" ] && continue
        case " $LINKED " in
            *" $ext "*) ;;
            *) qualifies=0 ;;
        esac
    done < <(grep -E '^require[[:space:]]+' "$f" 2>/dev/null | tr -d '\r' | awk '{print $2}')

    # (3) the opt-in delta-rs native libraries.
    if grep -qEi "PROVIDER[[:space:]]+'(deltars|delta-rs)'" "$f" 2>/dev/null; then
        qualifies=0
    fi

    # `if` rather than `[ ... ] && echo`: the latter leaves a non-zero status when the suite does
    # not qualify, and the LAST loop iteration's status becomes the script's exit code.
    if [ "$qualifies" -eq 1 ]; then
        echo "$f"
    fi
done

exit 0
