"""dbt-duckdb plugin: attach BOTH fabricator catalogs (DAX semantic model + OneLake Delta lakehouse).

Credentials: this plugin executes the gitignored repo-root dax_secret.sql (CREATE OR REPLACE SECRET
fabric_sp ...) on every connection, so no secret value ever appears in the dbt project. Why a plugin
(not the profile `attach:`): dbt-duckdb's attach renderer can only emit `READ_ONLY` when true, but
OneLake needs `READ_ONLY false` explicitly (DuckDB bumps a remote abfss:// ATTACH to read-only under
AUTOMATIC access mode). `configure_connection` runs per dbt cursor, so both catalogs exist for every
thread (introspection, schema creation, model builds).
"""

from typing import Any, Dict

from dbt.adapters.duckdb.plugins import BasePlugin


class Plugin(BasePlugin):
    def initialize(self, config: Dict[str, Any]):
        self._secret_sql = config["secret_sql"]
        self._secret_name = config.get("secret_name", "fabric_sp")
        self._lakehouse_path = config["lakehouse_path"]
        self._lakehouse_alias = config.get("lakehouse_alias", "lake")
        # One or more semantic models: [{xmla: <connstr>, alias: <catalog name>}, ...]
        self._dax_attaches = config["dax_attaches"]

    def configure_connection(self, conn):
        # 1. The SP secret (CREATE OR REPLACE — idempotent). Strip comment lines, execute per statement.
        with open(self._secret_sql, encoding="utf-8") as f:
            text = "\n".join(l for l in f.read().splitlines() if not l.lstrip().startswith("--"))
        for stmt in text.split(";"):
            if stmt.strip():
                conn.execute(stmt)
        # 2. The writable OneLake Delta lakehouse (READ_ONLY false — see module docstring).
        conn.execute(
            f"ATTACH IF NOT EXISTS '{self._lakehouse_path}' AS {self._lakehouse_alias} "
            f"(TYPE fabricator, PROVIDER 'delta', SECRET {self._secret_name}, READ_ONLY false)"
        )
        # 3. The semantic model(s) over the workspace XMLA endpoint (read-only by nature).
        for dax in self._dax_attaches:
            conn.execute(
                f"ATTACH IF NOT EXISTS '{dax['xmla']}' AS {dax['alias']} "
                f"(TYPE fabricator, PROVIDER 'dax', SECRET {self._secret_name})"
            )
