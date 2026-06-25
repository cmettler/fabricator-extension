# Provider extensibility — provider-declared config (settings, secrets, ATTACH options)

> One principle across three flavors of provider-specific named config: **a provider declares its config in
> C#; the provider-agnostic C++ core registers/forwards it generically and never names a provider-specific
> key.** Motivated by the second provider (DAX/ADOMD) — otherwise every key is O(keys × providers) C++ churn
> and forces the core to know `mssql_*` names. Settings is **built** (ABI v33); secret fields and ATTACH
> options are **designed here**. File references are repo-root-relative.

## The principle

| Layer | Owns |
|---|---|
| **C++ core** | the registration + dispatch *machinery* — no `mssql_*` / `dax_*` names anywhere |
| **C# provider** | *declares* its settings / secret-fields / ATTACH-options and *consumes* the values |

The three flavors:

| Flavor | DuckDB surface | Status |
|---|---|---|
| **Settings** (`SET x = …`) | extension options | **DONE** (ABI v33) |
| **Secret fields** (`CREATE SECRET (TYPE …, field …)`) | secret type + params | **DONE** (ABI v38, §2) |
| **ATTACH options** (`ATTACH … (TYPE …, opt …)`) | storage-extension attach map | **DONE** (ABI v37, §3) |

All three flavors now follow the one principle: **the provider declares; the core stays name-agnostic.**

## 1. Settings — DONE (ABI v33)

Full design: [settings-architecture.md](settings-architecture.md). Recap: `IBackend.Settings` →
`list_settings` (registered as DuckDB extension options at load) → per-slot trampoline set-callbacks push
values via `set_setting` into `ProviderSettingsStore` → the provider reads them in C#
(`MapArrowToSqlType` already reads `mssql_default_varchar_length` this way). The core names no setting.

## 2. Secret fields — DONE (ABI v38)

**Implemented.** The provider declares its secret type + fields in C#: `IBackend.SecretType` (e.g.
`"mssql_net"`) + `IBackend.SecretFields` (a `SecretField` list — name / type (`varchar`/`integer`/`boolean`)
/ `redact`). A new `list_secret_fields` ABI (the `list_settings` twin) is queried at extension load;
`RegisterProviderSecrets` registers **one DuckDB secret type per declared `secret_type`** generically — the
listed fields become the `CREATE SECRET` named parameters (redacting the marked ones) via one shared
`CreateProviderSecret` (keyed by `input.type`). The C++ core names **no** secret type or field: the field
constants (`kHost` …), `ValidateFields`, and `CreateMssqlNetSecret` are gone; `IsMssqlNetSecret` →
`IsProviderSecret` and `BuildConnectionStringFromSecret` now check the registered-types map and pass the
owning provider to `build_connection_string`.

**Validation moved to C#** (`SqlServerBackend.BuildConnectionString`): host/database required + port range —
it surfaces at **connect/ATTACH time** rather than at `CREATE SECRET` (the design intent; `use_encrypt`/`port`
defaults were already applied there). `verify_secret.test` updated to assert the connect-time error.

**The value path** (`build_connection_string`, ABI v18) was already C#, so only declaration + validation
moved — net a *deletion* of the C++ field list + `ValidateFields`, replaced by one generic registration
driven by `list_secret_fields`. Validated: `verify_secret` (incl. redaction + connect-time validation) +
full suite 30/30.

### 2.1 Consuming a FOREIGN secret type (ABI v39)

A provider can also **reuse a secret of another extension's type** (e.g. DuckDB's `azure` secret) — useful
both for SQL Entra auth and, later, for a storage-capable provider reading `azure`/`s3`/`http` creds. The
mechanism: `BuildConnectionStringFromSecret` resolves a secret of **any** type (`IsMssqlNetSecret` →
`IsKnownSecret` = "any secret exists"; the type-restricted check is gone) and passes `(secret_type,
fields_json, base_connstr)` to `build_connection_string` (ABI v39 added `secret_type` + `base_connstr`). C#
interprets the fields **per type** — so each provider handles the foreign types it understands; the core just
forwards. Resolution stays at the points where C++ holds a `ClientContext` (ATTACH, raw-query functions) —
secrets are context-scoped, so there is no "fetch any secret from arbitrary C#" path.

**SQL Server's azure mapping** (`SqlServerBackend.BuildAzureEntraConnectionString`): an `azure`
`service_principal` secret → `Active Directory Service Principal` (`User Id`=`CLIENT_ID`,
`Password`=`CLIENT_SECRET`); `managed_identity` → `Active Directory Managed Identity` (+ `CLIENT_ID` for
user-assigned). The azure secret carries **auth only** (its `ACCOUNT_NAME` is a storage account), so the
server/database come from the **ATTACH target** (`base_connstr`), merged via `SqlConnectionStringBuilder`:
`ATTACH 'Server=…;Database=…' AS d (TYPE mssql_net, SECRET <azure_sp>)`. `credential_chain` is rejected with a
clear error (its token is storage-scoped + fetched lazily — no SQL-usable credential) pointing to
`authentication='Active Directory Default'`, which makes SqlClient run the same chain scoped for SQL — also
the answer for "DuckDB running inside Fabric/Azure with an ambient managed identity." Validated end-to-end:
an azure SP secret → Entra → a live **Fabric Warehouse** query (manual smoke; the azure extension +
credentials aren't in CI). Error paths: `verify_azure_secret.test` (`require azure`).

## 3. ATTACH options — DONE (ABI v37)

**Implemented.** `open_catalog` gained an `options_json` arg: `MssqlNetAttach` now extracts only the two
META options it must handle before the provider is resolved (**PROVIDER** — selects the backend, incl.
`scheme://` inference; **SECRET** — resolved to a connstr), serializes **every other** ATTACH option into a
flat JSON object, and forwards it. The provider-agnostic core names no provider-specific option. C#
(`SqlServerCatalog`) parses the keys it knows — `schema_filter`/`table_filter` (applied in `get_metadata`)
and `isolation_level` (per-catalog default for table-in-out, resolved with `mssql_isolation_level` in
`InOutBind`). Consequences carried out: the C++ `CatalogFilters` + `ValidateCatalogFilters` (regex validation
moved to C#, clean ATTACH error preserved) + the catalog's `schema_filter_`/`table_filter_`/`isolation_level_`
+ `ResolveInOutIsolation` are gone, and `inout_exchange_open` dropped its `isolation` arg (C# resolves it).
Function discovery is schema-filtered for free: the C++ catalog only registers a discovered function if its
schema is already registered (and the managed `schema_filter` kept non-matching schemas out of
`DiscoverSchemas`). Validated: `verify_catalog_filter` + `verify_inout_isolation` + full suite 30/30; dbt
`--threads 4` green. Below is the original design (retained for context).

**Was (C++-hardcoded):** [mssql_net_storage.cpp](../src/mssql_net_storage.cpp) parsed options with a hardcoded
`if (lower == "schema_filter") …` chain (`schema_filter`/`table_filter`/`isolation_level`/`provider`/`secret`),
all `mssql`-specific.

**Key difference from settings/secrets:** ATTACH options are **not pre-registered** in DuckDB — the storage
extension just reads them from the attach options map at attach time. So the refactor is "**pass the map to
C#**," not "register declarations."

**Refactor:**
- Extend `open_catalog` to carry the **full ATTACH options map** (today it gets only the connstr); C# parses
  the ones it knows. (Signature change → ABI bump.)
- The provider *may* declare its accepted options (for validation / a clean "unknown option" error and docs),
  but strictly C# can read what it knows from the map.

**The nuance — filter application moves C++ → C#.** `schema_filter`/`table_filter` are applied **C++-side**
today (the catalog filters which discovered schemas/tables to register). To make ATTACH options
provider-owned, that filtering moves into C# — apply the regex inside `get_metadata` so it returns only
matches. Cleaner (a provider may filter on its own semantics), but it's a behavior-*location* change to
verify against `verify_catalog_filter.test`.

**Two options stay C++-side by necessity:**
- **`PROVIDER`** — it *selects* which provider handles the ATTACH; it must be parsed before the provider is
  resolved (you can't ask the provider to parse the option that picks the provider). Includes the
  `scheme://` inference.
- **`SECRET`** — C++ resolves the named secret → fields → `build_connection_string` → connstr before the
  provider opens the catalog.

Everything else flows to C#.

## ABI deltas

- **Secrets:** `+ list_secret_fields(out, err)` (load-time, the `list_settings` twin). `build_connection_string`
  already exists. Net: remove the C++ field list + `ValidateFields`; add one generic registration entry.
- **ATTACH:** extend `open_catalog` to pass the options map (signature change → version bump); move filter
  application into C# (`get_metadata`). No new vtable slot strictly required if the map rides the existing
  call.

Both are **net reductions** in provider-specific C++, mirroring the settings outcome.

## Sequencing

- **Settings** — done (ABI v33).
- **ATTACH options** — done (ABI v37).
- **Secret fields** — done (ABI v38).

All three flavors are built and the `mssql` provider is fully self-describing. The DAX provider can now
declare its settings / ATTACH options / secret type the same way, with no provider-specific names in the
core — the genericity is in place to validate against a second provider when it lands.

The architecture converges on: **a provider declares its settings, secret fields, and ATTACH options; the
C++ core stays name-agnostic** — three instances of one pattern.

## Open decisions

- **Secret type naming per provider** (`sqlserver`/`mssql` vs a generic `arrownet` type with a provider field?)
  — still open; decide with the DAX provider (§2).
- **ATTACH filter-location change** — RESOLVED: C#-side filtering matches the C++ semantics
  (`verify_catalog_filter` green; icase unanchored substring regex).
- **Declare ATTACH options (for validation) vs just read the map** — RESOLVED for now: **just read the map**
  (the provider reads keys it knows; unknown keys are ignored — no per-provider declaration surface). A
  declared-options validation pass (clean "unknown option" error + docs) can be added later if wanted.
