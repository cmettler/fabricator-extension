#!/usr/bin/env bash
#
# Prints the suites that need the docker/docker-compose.yml stack — SQL Server 2025 and MinIO — and
# nothing else. This is the COMPLEMENT of scripts/list-hermetic-suites.sh within the automatable set,
# so the two tiers together cover every suite CI can run, with no overlap and no silent gaps.
#
# A suite qualifies when ALL of:
#
#   1. it is NOT already in the hermetic set (otherwise it would run twice), and
#   2. every `require-env` it declares is one this tier provides:
#        MSSQL_TESTDB_DSN / MSSQL_TEST_SERVER / MSSQL_TEST_CONNECTION_STRING / MSSQL_TESTDB_URI /
#        MSSQL_TEST_PASS   — the compose SQL Server
#        MSSQL_BINCOLL_DSN — the BinCollTest database (binary UTF-8 collation), provisioned by
#                            docker/provision.ps1; the suite does NOT self-provision it
#        FABRICATOR_S3_ENDPOINT / FABRICATOR_S3_SQL_ENDPOINT — MinIO, and the endpoint AS SQL SERVER
#                            SEES IT (the two differ: localhost:9000 vs minio:9000)
#        FABRICATOR_DELTA_WRITE_DIR / FABRICATOR_DELTA_DIR — a scratch dir and the in-repo fixture
#        FABRICATOR_PLUGIN_DIR — a published Fabricator.SamplePlugin
#        FABRICATOR_PLUGIN_ZIP — that same plugin as an INSTALLABLE ARCHIVE, emitted by its own build
#                            (the PackPluginArchive target). Same tier as FABRICATOR_PLUGIN_DIR because it
#                            comes from the same build step, not because it needs a service.
#   3. every `require <extension>` it declares is statically linked into the test binary.
#
# Rule 3 is what excludes verify_azure_secret (`require azure`, not linked, so it would only ever
# skip). Deliberately NOT satisfiable, and therefore absent by rule 2: FABRICATOR_DAX_* (needs Power
# BI Desktop) and FABRICATOR_DELTARS (needs publish-managed.ps1 -IncludeDeltaRs, ~240 MB of native
# libraries). Those stay manual, and the docs say so rather than leaving it to be inferred.
set -uo pipefail
cd "$(dirname "$0")/.."

LINKED='fabricator json icu parquet httpfs'
PROVIDED='MSSQL_TESTDB_DSN MSSQL_TEST_SERVER MSSQL_TEST_CONNECTION_STRING MSSQL_TESTDB_URI
          MSSQL_TEST_PASS MSSQL_BINCOLL_DSN FABRICATOR_S3_ENDPOINT FABRICATOR_S3_SQL_ENDPOINT
          FABRICATOR_DELTA_WRITE_DIR FABRICATOR_DELTA_DIR FABRICATOR_PLUGIN_DIR
          FABRICATOR_PLUGIN_ZIP DELTA_PROVIDER'
# DELTA_PROVIDER is not a dependency — it is which Delta engine to run as, a constant string that
# run-suites.sh sets, running the suites that declare it once per engine. It has to be listed here for
# the same reason as in the hermetic classifier: an unrecognized require-env disqualifies a suite
# ENTIRELY, so omitting it would silently drop verify_delta_catalog_s3 out of this tier.

hermetic=$(scripts/list-hermetic-suites.sh)

for f in test/*.test; do
    # (1) skip anything the hermetic tier already covers
    if printf '%s\n' "$hermetic" | grep -qxF "$f"; then
        continue
    fi

    qualifies=1
    declared=0

    while read -r name; do
        [ -z "$name" ] && continue
        declared=1
        case " $(echo $PROVIDED) " in
            *" $name "*) ;;
            *) qualifies=0 ;;
        esac
    done < <(grep -E '^require-env[[:space:]]+' "$f" 2>/dev/null | tr -d '\r' | awk '{print $2}')

    while read -r ext; do
        [ -z "$ext" ] && continue
        case " $LINKED " in
            *" $ext "*) ;;
            *) qualifies=0 ;;
        esac
    done < <(grep -E '^require[[:space:]]+' "$f" 2>/dev/null | tr -d '\r' | awk '{print $2}')

    # A suite with NO require-env at all cannot be in this tier: either the hermetic tier already
    # took it, or something non-env (an unlinked `require`) is keeping it out.
    if [ "$declared" -eq 0 ]; then
        qualifies=0
    fi

    if [ "$qualifies" -eq 1 ]; then
        echo "$f"
    fi
done

exit 0
