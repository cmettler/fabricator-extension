# Settings architecture — provider-declared, C#-accessible settings

> Design for making `SET`-able settings a **provider-owned, C#-accessible** concept instead of the current
> "hardcode in C++, read in C++, pass each value through an ABI method param" model. Motivated by the
> second provider (DAX/ADOMD): the per-method-param approach is O(settings × providers) ABI churn and forces
> the provider-agnostic C++ core to know `mssql_*` names. Status: **design / not yet implemented.** File
> references are repo-root-relative.

## TL;DR

- A provider **declares** its settings in C# (`IBackend.Settings`); the core knows no setting names.
- C++ **registers** them with DuckDB at load (generic `list_settings(provider)` ABI) so `SET` /
  `duckdb_settings()` work.
- Values reach C# by **push** (a generic `set_setting(provider, name, value)` on every `SET` + an initial
  snapshot), cached in a C# `SettingsStore`; C# reads `catalog.Settings.Get<T>(name)` directly.
- **Net ABI reduction**: two generic entries replace the growing list of per-setting params
  (`text_type`, `isolation`, the proposed `text_length`, …).
- Two deliberate trade-offs: **boot the CLR at extension load** (needed for `SET` before first ATTACH;
  aligns with Phase-3 load-time functions) and **catalog/provider-scoped** values (not session-local).

## 1. Current architecture & why it doesn't scale

**Definition** is hardcoded in C++ — [`RegisterCompatSettings`](../src/mssql_net_extension.cpp#L86) holds a
literal list of `mssql_*` names. That already breaks "C++ is provider-agnostic"; a DAX provider would need
`dax_*` baked into the same core C++.

**Reading** happens at exactly three C++ sites via `context.TryGetCurrentSetting(...)`, and each value then
rides an ABI **method param** into C#:

| Setting | Read site | Crosses as |
|---|---|---|
| `mssql_ctas_text_type` | [arrownet_schema_entry.cpp:1658](../src/catalog/arrownet_schema_entry.cpp#L1658) | `create_table`'s `text_type` param |
| `mssql_isolation_level` | [arrownet_schema_entry.cpp:891](../src/catalog/arrownet_schema_entry.cpp#L891) | the in-out exchange's `isolation` param |
| `mssql_exec_invalidate_cache` | [mssql_net_extension.cpp:253](../src/mssql_net_extension.cpp#L253) | read *and used* C++-side (never crosses) |

So every new C#-consumed setting = C++ registration + a C++ read site + a new ABI param — O(settings ×
providers) churn. This is what blocked `mssql_default_varchar_length` (it would need new params on
`create_table` **and** `begin_bulk`, since CTAS/COPY create via [`BeginBulk(create=true)`](../src/dml/arrownet_ctas.cpp#L65)).

The rest of the registered settings (`mssql_connection_cache`, `mssql_order_pushdown`, …) are accepted
no-ops for native-extension compatibility.

## 2. Goal

A provider **declares** its settings in C#; they are **registered** with DuckDB (so `SET` /
`duckdb_settings()` work); and C# **reads** them directly — no per-method params, no `mssql_*` knowledge in
the core, and a new provider gets settings by implementing one interface member.

## 3. The core challenge: where setting *values* live

DuckDB setting values live in the C++ `ClientContext` and are **scoped to it** (`SET` is session-local by
default; `SET GLOBAL` is instance-wide). C# cannot read a `ClientContext`. So a value must be **pushed** to
C# or **pulled** by C#, and there is a real scoping mismatch: a DuckDB setting is *session*-scoped, but a C#
catalog handle (from `BackendRegistry` / `OpenCatalog`) is **shared across all sessions** of the database
instance. The options and their costs:

| Approach | How C# gets values | Problem |
|---|---|---|
| **Pull via host callback** (`host_get_setting(name)`) | C# calls back into C++ on demand | needs the *operation's* context; an ambient/thread-local context breaks on the async bulk-consumer thread (`BulkSession`'s `Task.Run`) |
| **Push at change** (a `set_callback` → `set_setting` ABI) | C++ pushes on every `SET`; C# caches | the callback gets **no setting name** and fires **before** the value is stored ([physical_set.cpp:23-30](../duckdb/src/execution/operator/helper/physical_set.cpp#L23)), so a *single* generic callback can neither identify the setting nor re-read it — needs a per-slot trampoline (§4.3). Session-local `SET` still resolves onto the shared catalog (§5.2). |
| **Per-operation snapshot** | C++ reads context, passes a settings blob | still a per-method param — the thing we are removing |

No option is simultaneously zero-param, fully session-local-correct, and async-safe. The
session-vs-shared-catalog scoping is a genuine constraint, not an implementation gap — so the design picks a
scoping model deliberately (§5.2).

## 4. Proposed design — provider-declared, catalog/provider-scoped

Lean into **catalog/provider scoping**: treat these as connection *configuration* (like ATTACH options
already are), accepting that `SET` is provider-global rather than session-local. This is the clean model and
matches how `isolation_level` already works as an ATTACH option.

### 4.1 Declaration (C#-owned)

```csharp
public sealed record ProviderSetting(string Name, ProviderSettingType Type, object? Default, string Description);

public interface IBackend
{
    // ... existing members ...
    IEnumerable<ProviderSetting> Settings => System.Array.Empty<ProviderSetting>();
}
```
`SqlServerBackend.Settings` returns `mssql_ctas_text_type`, `mssql_isolation_level`,
`mssql_default_varchar_length`, the future `mssql_mars`, etc. A DAX backend returns its `dax_*` set. The
core never names a setting.

### 4.2 Registration (C++ at load)

A generic `RegisterProviderSettings(loader)` enumerates providers and calls a new ABI
`list_settings(provider) → Arrow[(name, type, default, description)]`, then `AddExtensionOption(...)` for
each, assigning it a **slot index** and a per-slot trampoline `set_callback` (see §4.3). Replaces the
hardcoded `mssql_*` list. (Requires the bridge booted at load — see §5.1.)

### 4.3 Value flow (push) — per-slot trampolines

DuckDB's `set_option_callback_t` is `(ClientContext&, SetScope, Value&)` — **no setting name** — and the SET
operator invokes it *before* storing the value ([physical_set.cpp:23-30](../duckdb/src/execution/operator/helper/physical_set.cpp#L23)),
so a single shared callback can neither tell which setting fired nor re-read it from the context. Resolve
this with a **compile-time array of trampolines** `SetTrampoline<0..N>` (N = a fixed cap, e.g. 64). At
registration, setting *i* is bound to `SetTrampoline<i>`; the trampoline knows its `(provider, name)` from a
global table at index *i* and pushes the `Value` it was handed via a generic ABI
`set_setting(provider, name, value)` into C#'s provider-keyed `SettingsStore`. An initial snapshot of
defaults/current values is pushed at registration. ATTACH options layer on top per-catalog. (`SetScope` is
available to the trampoline but, per §5.2, all scopes resolve onto the provider-global store.)

### 4.4 C# access

`SqlServerCatalog` reads `Settings.Get<int>("mssql_default_varchar_length")` etc., resolving **ATTACH
override → provider-global → declared default**. `MapArrowToSqlType` reads the length / text type straight
from the catalog. The `text_type` / `isolation` / proposed `text_length` **params disappear** from the ABI.

## 5. The two real trade-offs

### 5.1 Load-time CLR boot

DuckDB requires extension options registered during `Extension::Load()` so a preamble `SET mssql_x = …`
*before* any ATTACH resolves. That means **booting the bridge at load** to call `list_settings` (today the
bridge boots lazily on first ATTACH/query). Cost: ~100–300 ms + the .NET runtime loaded even if the
extension is never used. **Recommended: accept it** — it aligns with what Phase 3 (load-time global
functions) needs anyway, and a connector extension that's been `LOAD`ed is intended to be used. (Alternative:
lazy registration → `SET` before first use fails with "unknown setting" — worse UX.)

### 5.2 Catalog/provider scope vs session-local

Today the value is read from the *operation's* `ClientContext`, so `SET SESSION` is honored per-connection.
The push model makes provider settings effectively global (per-provider, with per-catalog ATTACH overrides).
For connection *configuration* (isolation, varchar length, MARS mode) that is fine and arguably more
intuitive — but it is a real semantic change. **Recommended: accept it** for config settings. If true
session-local is ever needed, key the `SettingsStore` by a session token pushed with each operation
(deferred — adds the per-operation threading this design avoids).

## 6. ABI impact (a net simplification)

- **Add** two generic entries: `list_settings(provider, out_stream)` and `set_setting(provider, name, value)`.
- **Remove** (once C# reads from `catalog.Settings`): the `text_type` param on `create_table`, the
  `isolation` param on the in-out exchange open — and the never-added `text_length`. Each removal is a
  signature change (version bump) but no new growth.
- The core's `RegisterCompatSettings` + the three `TryGetCurrentSetting` sites + the per-setting param
  marshaling all go away. `mssql_exec_invalidate_cache` can stay a C++-read/C++-used generic
  "invalidate-after-exec" flag (it never needed to reach C#).

## 7. Migration path

1. **Mechanism first** (no behavior change): `ProviderSetting` + `IBackend.Settings`; the `list_settings` /
   `set_setting` ABI; C++ generic registration (boot at load); C# `SettingsStore`. Declare the existing
   `mssql_*` settings in `SqlServerBackend.Settings`. Keep the old param paths working in parallel.
2. **Cut over the readers**: `SqlServerCatalog` reads `mssql_ctas_text_type` / `mssql_isolation_level` from
   `Settings`; remove the `text_type` / `isolation` ABI params. Gate: full `verify_*` suite green +
   `verify_inout_isolation` / `verify_ctas_text_type` unchanged.
3. **Unblock `mssql_default_varchar_length`** (the original motivator) — **DONE**: declared in C#, read in
   `MapArrowToSqlType` from `ProviderSettingsStore` (applies to **all** created text columns incl. CTAS/COPY,
   no `begin_bulk`/`create_table` signature changes; `mssql_ctas_text_type` whole-type override still wins).
   `test/verify_default_varchar_length.test`. See [warehouse-support.md](warehouse-support.md) §3.2.

**Status:** steps 1–3 (mechanism) + step 5 (`mssql_default_varchar_length`) DONE at ABI v33. Step 4
**partly done** at ABI v34: `ctas_text_type` cut over (C# reads it from the store in `MapArrowToSqlType`; the
`text_type` param dropped from `create_table` end-to-end — proving a per-setting param can be removed — and
it now applies to CTAS/COPY too). The C++11 trampoline array was hardened (hand-rolled `IndexSeq`). The
**`isolation` cutover is deferred**: it's entangled with the per-catalog `isolation_level` ATTACH option (a
provider-global store can't hold a per-catalog value), so it lands with the ATTACH-options refactor
([provider-extensibility.md](provider-extensibility.md) §3).
4. Each slice rebuilds C++ + republishes managed (the ABI changes are lockstep — exact-match version check).

## 8. Open decisions

- **Load-time boot** OK (§5.1)? — recommended yes.
- **Catalog/provider scope** acceptable vs session-local (§5.2)? — recommended yes for config settings.
- **Setting value transport** for `set_setting`: a typed `Value`-as-string + a type tag is simplest
  (settings are few and small); revisit only if a richer type is needed.
