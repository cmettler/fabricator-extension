#!/usr/bin/env bash
# Regenerates the mssql-extension compatibility test corpus under test/mssqlcompat/
# by adapting the C++ `mssql` extension's sqllogictests to our `mssql_net` surface.
#
# Renames: require mssql -> require mssql_net, TYPE mssql -> TYPE mssql_net,
# mssql_scan( -> mssql_net_query(, mssql_exec( -> mssql_net_exec(, and the
# `type='mssql'` / `extension_name='mssql'` assertion literals. Azure tests are
# excluded; a few hang-prone TDS/TLS/timeout tests are dropped.
#
# Run the result with the unittest binary, pointing its discovery root at our
# repo (so the duckdb submodule stays clean):
#   build/release/test/unittest --test-dir <repo-root> "test/mssqlcompat/*"
# with MSSQL_TESTDB_DSN / MSSQL_TEST_DSN / ... and ARROWNET_MANAGED_DIR set.
set -euo pipefail

SRC="${1:-/d/repos/mssql-extension/test/sql}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DST="$ROOT/test/mssqlcompat"

if [ ! -d "$SRC" ]; then
  echo "source test dir not found: $SRC" >&2
  exit 1
fi

rm -rf "$DST"
mkdir -p "$DST"
while IFS= read -r f; do
  rel="${f#"$SRC"/}"
  mkdir -p "$DST/$(dirname "$rel")"
  sed -E "s/\brequire mssql\b/require mssql_net/g; \
          s/\bTYPE mssql\b/TYPE mssql_net/g; \
          s/\bmssql_scan\(/mssql_net_query(/g; \
          s/\bmssql_exec\(/mssql_net_exec(/g; \
          s/(type|extension_name) = 'mssql'/\1 = 'mssql_net'/g" "$f" > "$DST/$rel"
done < <(find "$SRC" -name '*.test' ! -path "$SRC/azure/*")

# Drop genuinely hang-prone tests (real TLS handshakes / cancellation / pool loops).
rm -f "$DST"/integration/connection_pool.test "$DST"/integration/pool_limits.test \
      "$DST"/integration/parallel_queries.test "$DST"/integration/query_cancellation.test \
      "$DST"/integration/tls_connection.test "$DST"/integration/tls_multipacket.test \
      "$DST"/integration/tls_parallel.test "$DST"/integration/tls_queries.test \
      "$DST"/query/cancellation.test "$DST"/query/exec_query_timeout.test

echo "Generated $(find "$DST" -name '*.test' | wc -l) tests under test/mssqlcompat/"
