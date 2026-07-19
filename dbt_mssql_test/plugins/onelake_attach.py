"""dbt-duckdb plugin: attach a writable OneLake Delta catalog on every connection.

Why a plugin (not the profile `attach:`): dbt-duckdb's attach renderer can only emit `READ_ONLY` when true,
but OneLake needs `READ_ONLY false` explicitly — DuckDB bumps a remote `abfss://` ATTACH to read-only when the
access mode is AUTOMATIC (database_manager.cpp). `configure_connection` runs per connection AFTER the profile
`secrets:` are created (so the SP secret exists) and BEFORE dbt's per-connection schema creation, so the `mssql`
catalog is attached writable for every cursor dbt uses (list/create-schema/model threads).
"""

from typing import Any, Dict

from dbt.adapters.duckdb.plugins import BasePlugin


class Plugin(BasePlugin):
    def initialize(self, config: Dict[str, Any]):
        self._path = config["path"]
        self._alias = config.get("alias", "mssql")
        self._provider = config.get("provider", "delta")
        self._secret = config.get("secret")
        # MinIO rig: self-signed TLS -> disable curl cert verification (GLOBAL; per-connection is fine
        # since every dbt cursor runs configure_connection).
        self._curl_insecure = bool(config.get("curl_insecure", False))

    def configure_connection(self, conn):
        if self._curl_insecure:
            conn.execute("SET GLOBAL enable_curl_server_cert_verification = false")
        # TYPE fabricator = the registered storage extension keyword. The Delta backend is selected by PROVIDER.
        opts = ["TYPE fabricator", f"PROVIDER '{self._provider}'"]
        if self._secret:
            opts.append(f"SECRET {self._secret}")
        opts.append("READ_ONLY false")  # explicit => access mode READ_WRITE, no remote read-only bump
        conn.execute(f"ATTACH IF NOT EXISTS '{self._path}' AS {self._alias} ({', '.join(opts)})")
