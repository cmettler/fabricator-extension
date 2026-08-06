"""dbt-duckdb plugin: attach a writable Delta catalog on S3 for every connection.

Two things the profile `attach:` block cannot do, both load-bearing:

1. `READ_ONLY false`. dbt-duckdb's attach renderer only emits READ_ONLY when it is TRUE, but DuckDB bumps
   a remote `s3://` ATTACH to read-only under the AUTOMATIC access mode — so a profile attach yields a
   catalog dbt cannot write to.
2. `SECRET minio_s3`. On S3 the conditional-PUT commit path is selected by the secret the ATTACH *names*,
   not by a secret merely in scope. Without it httpfs still authenticates every read and write, so the
   catalog looks entirely healthy while its commits are unguarded put-if-absent — measured as silent lost
   commits under concurrency (docs/delta-transactions.md 8.3). Naming the secret is therefore a
   correctness requirement, not tidiness.

`configure_connection` runs per connection, AFTER the profile `secrets:` are created and BEFORE dbt's
per-connection schema creation, so every cursor dbt uses sees the catalog.
"""

from typing import Any, Dict

from dbt.adapters.duckdb.plugins import BasePlugin


class Plugin(BasePlugin):
    def initialize(self, config: Dict[str, Any]):
        self._path = config["path"]
        self._alias = config.get("alias", "lake")
        self._provider = config.get("provider", "delta")
        self._secret = config.get("secret")
        # MinIO rig: self-signed TLS => turn off curl cert verification. GLOBAL is required (the Delta
        # transaction flush commits on its own connection), and re-running it per connection is harmless.
        self._curl_insecure = bool(config.get("curl_insecure", False))

    def configure_connection(self, conn):
        if self._curl_insecure:
            conn.execute("SET GLOBAL enable_curl_server_cert_verification = false")
        # TYPE fabricator = the storage-extension keyword; the Delta backend is chosen by PROVIDER.
        opts = ["TYPE fabricator", f"PROVIDER '{self._provider}'"]
        if self._secret:
            opts.append(f"SECRET {self._secret}")
        opts.append("READ_ONLY false")
        conn.execute(f"ATTACH IF NOT EXISTS '{self._path}' AS {self._alias} ({', '.join(opts)})")
