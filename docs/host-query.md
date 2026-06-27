# Host query — reuse the host's DuckDB engine from C#, over Arrow

> Status: **design + implementation in progress.** Lets a managed component (a provider backend, a custom
> function) run a DuckDB query/statement **on the host's own engine** and exchange data as Apache Arrow —
> reusing DuckDB features (functions, readers, extensions, the catalog) without re-implementing them, and
> without going out-of-process. This is the C#→host reverse-callback companion to the v40 filesystem bridge
> (`ArrowNetHostServices`); see [docs/filesystem-bridge.md](filesystem-bridge.md).

## Why not ADBC / a second DuckDB

We are **already inside DuckDB** — the extension holds a first-class C++ `Connection`/`DatabaseInstance`.
Loading DuckDB's native ADBC driver in C# would open a *separate* database/connection (its own transaction,
its own locks) and binding our connection into it requires poking the driver's internal connection wrapper +
disabling its release — fragile and version-coupled (analysed and rejected). A reverse callback that runs on
the host's engine is simpler, has zero coupling to ADBC internals, and lets **C++ own the connection/transaction
binding**.

## The non-negotiable: a FRESH connection per call

`host_query` runs on a **new `Connection` over the host `DatabaseInstance`**, never the in-flight
`ClientContext`. A `ClientContext` is **not reentrant** — it owns one active transaction, one in-flight
query/result (executor pipeline, arena allocators, bound expression context) and a context lock held during
execution. A `host_query` is almost always invoked *from inside* an executing query (a C# scalar/table/in-out
callback), so reusing that context would **deadlock on the context lock or corrupt the outer query's state**.
A fresh `Connection` gets its own `ClientContext` (own transaction, own executor state), sharing only the
`DatabaseInstance` at the storage/catalog layer — which is built for concurrent connections (MVCC).

Consequences (accepted): the query runs in a **separate transaction** → it sees *committed* data, not the
extension's own uncommitted writes. (Same-transaction read-your-writes would mean reusing the live context =
the corruption path; explicitly out of scope.) Each call is naturally **thread-safe** (own connection, no
shared mutable state); a small connection pool is a later optimization (create-per-call first).

## ABI — additions to `ArrowNetHostServices` (the reverse-direction struct)

`ArrowNetHostServices` already carries host→managed function pointers the managed side calls (the v40
`fs_*` callbacks). We append one primitive:

```c
// Run `sql` on a FRESH host connection (own transaction). `params` (nullable) is a 1-row Arrow batch whose
// columns bind to the statement's parameters (by name when the batch field is named, else positionally).
// `inputs` (nullable) registers named Arrow sources as connection-scoped views BEFORE the query runs (via
// duckdb_arrow_scan), so the SQL can reference them by name (`SELECT * FROM <input_name> …`). `out` receives
// the result as an ArrowArrayStream. The connection + result outlive `out` (released when `out` is released).
int32_t (*host_query)(const char *sql,
                      struct ArrowArray *params /*nullable, 1-row*/,
                      ArrowNetHostInputs *inputs /*nullable*/,
                      struct ArrowArrayStream *out, char **err);
```

`ArrowNetHostInputs` = `{ int32_t count; const char **names; struct ArrowArrayStream **streams; }` — the
managed caller hands over N named Arrow streams (it owns producing them; the host consumes/releases them when
the query's connection is torn down). Bump `ARROWNET_ABI_VERSION` **and** the host-services `abi_version`.

**`exec_nonquery` is NOT a separate entry.** A DDL/DML statement returns a result in DuckDB too (DML → a
1-row `BIGINT` count; DDL → empty), so `host_query` subsumes it. "Exec, return affected rows" is a thin **C#
helper over `host_query`** (run, read the single count column if present, discard the stream) — keeping the
ABI minimal. Both get parameter binding for free.

## C++ side (`arrownet_host_query.cpp`, host service + the test/utility table function)

- **`HostQuery(...)`** (the `host_query` callback): `Connection conn(*g_database)` (the `DatabaseInstance`
  captured at extension load, like the fs services); for each input `duckdb_arrow_scan(conn, name, stream)`
  to register it as a connection view; `conn.Prepare(sql)` + bind the `params` row (read via a 1-row
  `ArrowAppender`→`DataChunk`, each value `→ Value` bound positionally/by name); execute; **export the result
  as an `ArrowArrayStream`** by fetching each `DataChunk` and `ArrowAppender`→`ArrowArray`→`ArrowProducer`,
  whose `Stream()` is returned. The `Connection` + materialized batches live in the producer's lifetime
  (released with `out`). Errors → `DupErr` (freed via `free_str`), like the fs callbacks.
- **Capture the `DatabaseInstance` at load** (`InstallHostServices(loader.GetDatabaseInstance())`) into a
  global the callback reads — `host_query` has no per-call opener (unlike the fs callbacks), it just needs the
  database to open a connection on.

## C# side (`ArrowNet.Bridge`)

- **`Abi.cs`**: add the `HostQuery` function-pointer field to `ArrowNetHostServices` (+ the `ArrowNetHostInputs`
  struct).
- **`Host` API** (mirrors `HostFs`): `Host.Query(string sql, RecordBatch? params = null, IReadOnlyDictionary<
  string, IArrowArrayStream>? inputs = null) → IArrowArrayStream` — marshals params to a 1-row Arrow array,
  exports each input stream + the names into an `ArrowNetHostInputs`, calls the pointer, imports the result
  stream. `Host.ExecuteNonQuery(sql, params) → long` = the helper (run, read the count, discard).
- This is the surface a provider backend / custom function uses to reuse the host engine.

## Data-in — two layers

1. **Scoped inputs (built in `host_query`):** the caller passes named Arrow streams with the query; the host
   registers them as connection-scoped views (`duckdb_arrow_scan`) for that query only and tears them down with
   the connection. No global state, no name collisions, no lifetime ambiguity. **This is the primary data-in
   path** — the query references the input names directly.
2. **Replacement-scan layer (optional, ambient registry):** for "register a C# source by name once, then any
   query referencing that bare name resolves to it" (pandas-df style). A C# registry maps `name → Func<
   IArrowArrayStream>`; a C++ replacement scan registered on the `DBConfig` rewrites an unknown table name to a
   `arrownet_scan('name')` `TableFunctionRef` when the name is registered (a `named_input_exists(name)` +
   `open_named_input(name, out)` managed lookup). `arrownet_scan(name)` is a global table function that opens
   the registered stream and scans it via the existing `arrow_ingest` path. Single-use streams → the registry
   holds a **factory** so each scan gets a fresh stream. This layer is additive over (1) and is only needed for
   the ambient/by-bare-name ergonomics.

## Verification

- A test table function `arrownet_host_query(sql)` (C++) that calls `HostQuery` directly proves the
  fresh-connection run + param-free Arrow export (`SELECT * FROM arrownet_host_query('SELECT 42 AS x')`).
- A C# round-trip test — a custom C# table function whose `Execute` calls `Host.Query(...)` — proves
  SQL → our C# function → `host_query` → fresh host connection → Arrow → back, including the reentrancy safety
  (the nested run is on a fresh connection, so the outer query's context is untouched).
- Data-in: a `host_query` with an input stream that the SQL joins/filters; and (layer 2) a replacement-scan
  test resolving a bare registered name.

## Implementation status

- **Slice 1 — `arrownet_host_query(sql)`** (the C++ engine: fresh connection + self-owning Arrow result via
  the ingest path). DONE, `verify_host_query.test`.
- **Slice 2 — C#-callable `host_query` host service (ABI v42→v43)** + public `Host.Query`/`Host.ExecuteNonQuery`.
  DONE; round-trip verified (`cf_host_answer` in `verify_custom_functions.test`).
- **Slice 3 — named Arrow inputs (data-in)**: `host_query` gained `ArrowNetHostInputs` (ABI v43); the host
  registers each C#-provided stream as a connection-scoped view via `duckdb_arrow_scan` before the query.
  `Host.Query(sql, inputs)`. DONE; verified (`cf_host_sum` pushes a C# Arrow table into a host query and sums
  it on the host engine — `verify_custom_functions.test`).
- **Slice 4 — parameter binding (ABI v44)**: `host_query` gained a nullable `params` 1-row Arrow stream; the
  host reads it via `ArrowStreamReader` and binds the columns POSITIONALLY (`?`, `$1`, …) to a prepared
  statement (materialized result so it doesn't outlive the prepared stmt). `Host.Query(sql, parameters)`.
  DONE; verified (`cf_host_param` binds `[40, 2]` into `SELECT (?::BIGINT)+(?::BIGINT)` → 42 —
  `verify_custom_functions.test`). **Ownership note:** the host's `ArrowStreamReader` releases its *copy* of
  the params stream, so the managed caller frees only its allocation (`Marshal.FreeHGlobal`), never
  re-releasing (which would double-free the exporter → NRE).
- **Slice 5 — ambient named-source registry + replacement scan (ABI v45)**: `Host.RegisterSource(name,
  Func<IArrowArrayStream>)` registers a stream factory; two handle-less vtable entries (`open_named_input`,
  `named_input_exists`) let the host resolve a name to a fresh stream. `arrownet_scan('name')` scans it; a
  `DBConfig` **replacement scan** rewrites a bare unresolved name to `arrownet_scan('name')` when it's
  registered (so `FROM <name>` works), declining unknown names so a genuine "table does not exist" is left to
  DuckDB (`NamedInputExists` is non-throwing + bridge-tolerant). DONE; verified (`verify_host_query.test`, 15
  — `arrownet_scan` + bare-name + unknown-name passthrough; built-in demo source `arrownet_demo_numbers`).
- **Slice 6 — streaming results**: `host_query` now uses `SendQuery` (and a streaming prepared `Execute`) so
  the result is fetched lazily (`StreamQueryResult.Fetch()` per `get_next`) — bounded memory for large
  results (validated to 1M rows). The holder keeps the connection (+ the prepared statement for params)
  alive; runtime errors that surface during `Fetch` (vs bind errors at `SendQuery`) are caught in `get_next`
  and reported via `get_last_error`. DONE.
- **Deferred:** parameter binding for the ambient `arrownet_scan` (it resolves a registered source by NAME —
  parameters belong on the scoped `host_query` path, which already binds them; a parameterized named source
  would be a separate, larger design); and the **full breaking rename** (removing the `mssql_net_*` names;
  the generic `arrownet_*`/`TYPE arrownet` names already exist as additive aliases — `verify_generic_names.test`).

## Open / deferred

- **Connection pool** for hot `host_query` callers (create-per-call first).
- **Same-transaction** reads — intentionally not supported (would require the live context = corruption).
