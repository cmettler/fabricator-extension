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
| **Secret fields** (`CREATE SECRET (TYPE …, field …)`) | secret type + params | designed (§2) |
| **ATTACH options** (`ATTACH … (TYPE …, opt …)`) | storage-extension attach map | designed (§3) |

## 1. Settings — DONE (ABI v33)

Full design: [settings-architecture.md](settings-architecture.md). Recap: `IBackend.Settings` →
`list_settings` (registered as DuckDB extension options at load) → per-slot trampoline set-callbacks push
values via `set_setting` into `ProviderSettingsStore` → the provider reads them in C#
(`MapArrowToSqlType` already reads `mssql_default_varchar_length` this way). The core names no setting.

## 2. Secret fields — the cleanest remaining (values already flow to C#)

**Today (C++-hardcoded):** [mssql_net_secret.cpp](../src/mssql_net_secret.cpp) registers the `mssql_net`
secret type and a `CreateSecretFunction` whose `named_parameters[kHost] = …` etc. are all `mssql`-specific,
plus a C++ `ValidateFields` (port-range check).

**Already C#:** the secret *values* cross via `build_connection_string(provider, fields_json)` (ABI v18) —
C# reads the secret's fields as JSON and assembles the provider connstr. So only the **declaration** and
**validation** are stuck in C++.

**Refactor:**
- `IBackend.SecretType` (the secret type name, e.g. `sqlserver`/`mssql`, `dax`) + `IBackend.SecretFields`
  (name/type, like `ProviderSetting`).
- A new `list_secret_fields` ABI (the `list_settings` twin) called at load; C++ registers **one secret type
  per provider** generically from the result. The core stops naming secret fields.
- `ValidateFields` moves to C# — it's provider-specific, and C# already owns connstr assembly
  (`BuildConnectionString`), so validation belongs right next to it.

**Why cleanest:** only declaration + validation move; the value path (`build_connection_string`) already
exists. Mostly *deleting* the C++ field list + `ValidateFields` and declaring them in C#.

## 3. ATTACH options — designed, with one structural nuance

**Today (C++-hardcoded):** [mssql_net_storage.cpp:90-110](../src/mssql_net_storage.cpp#L90) parses options
with a hardcoded `if (lower == "schema_filter") …` chain (`schema_filter`/`table_filter`/`isolation_level`/
`provider`/`secret`), all `mssql`-specific.

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

- **Settings** — done.
- **Secret fields + ATTACH options** — build **with the DAX provider**: that's when a *second* secret type
  and a *second* ATTACH-option set first exist to validate the genericity against (building them now would be
  speculative with only one provider). The **secret-field declaration** is close enough to done (values
  already flow) to be a tidy standalone follow-up if the `mssql` provider should be fully self-describing
  before DAX.

The architecture converges on: **a provider declares its settings, secret fields, and ATTACH options; the
C++ core stays name-agnostic** — three instances of one pattern.

## Open decisions

- **Secret type naming per provider** (`sqlserver`/`mssql` vs a generic `arrownet` type with a provider field?).
- **ATTACH filter-location change** — confirm the C#-side filtering matches the current C++ semantics
  (`verify_catalog_filter`).
- **Declare ATTACH options (for validation) vs just read the map** — declaration adds a clean unknown-option
  error + docs at the cost of a second declaration surface.
