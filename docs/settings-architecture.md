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

**Definition** is hardcoded in C++ — [`RegisterCompatSettings`](../src/fabricator_extension.cpp#L86) holds a
literal list of `mssql_*` names. That already breaks "C++ is provider-agnostic"; a DAX provider would need
`dax_*` baked into the same core C++.

**Reading** happens at exactly three C++ sites via `context.TryGetCurrentSetting(...)`, and each value then
rides an ABI **method param** into C#:

| Setting | Read site | Crosses as |
|---|---|---|
| `mssql_ctas_text_type` | [fabricator_schema_entry.cpp:1658](../src/catalog/fabricator_schema_entry.cpp#L1658) | `create_table`'s `text_type` param |
| `mssql_isolation_level` | [fabricator_schema_entry.cpp:891](../src/catalog/fabricator_schema_entry.cpp#L891) | the in-out exchange's `isolation` param |
| `mssql_exec_invalidate_cache` | [fabricator_extension.cpp:253](../src/fabricator_extension.cpp#L253) | read *and used* C++-side (never crosses) |

So every new C#-consumed setting = C++ registration + a C++ read site + a new ABI param — O(settings ×
providers) churn. This is what blocked `mssql_default_varchar_length` (it would need new params on
`create_table` **and** `begin_bulk`, since CTAS/COPY create via [`BeginBulk(create=true)`](../src/dml/fabricator_ctas.cpp#L65)).

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

> **⚠ SUPERSEDED 2026-08-11 — BUILT as ABI v69. The recommendation below ("accept it") was WRONG, and the
> deferred alternative it names in its last sentence is exactly what shipped. Kept verbatim because the
> shape of the error is the reusable part: the trade-off was assessed as a *configuration ergonomics*
> question, and it was a *correctness* one. See §5.3.**

Today the value is read from the *operation's* `ClientContext`, so `SET SESSION` is honored per-connection.
The push model makes provider settings effectively global (per-provider, with per-catalog ATTACH overrides).
For connection *configuration* (isolation, varchar length, MARS mode) that is fine and arguably more
intuitive — but it is a real semantic change. **Recommended: accept it** for config settings. If true
session-local is ever needed, key the `SettingsStore` by a session token pushed with each operation
(deferred — adds the per-operation threading this design avoids).

### 5.3 Scoped settings — BUILT (ABI v69, 2026-08-11)

**The measurement that settled it.** `SET mssql_mars='false'` in DuckDB connection A made a same-catalog
CTAS in connection B — which set nothing — return **10** rows instead of **15**; the control (same script,
no `SET`) returned 15. A setting applied in one connection changed the **data another connection saw**. So
this was never "config ergonomics": §5.2's own example list (*"isolation, varchar length, MARS mode"*) names
MARS, and MARS is precisely the one whose leak changes an answer, because it selects the scan's connection
routing and thus whether a write is visible to a later read.

**The practical consequence that motivated the fix**: configuring ONE dbt model via a pre-hook could not
work. The value leaks to models building concurrently on other threads, and with no scoping at all it also
persists to every later model even at `--threads 1`.

**What DuckDB was already doing.** Extension options registered with `AddExtensionOption` default to
`SetScope::SESSION`, and DuckDB stores the value per-connection in `client_config.user_settings`. So an
unqualified `SET` was *already* session-scoped on DuckDB's side; only our push was process-wide, because the
trampoline's signature is `(ClientContext &, SetScope, Value &)` and we discarded the first two arguments.

**The design.**

| layer | key | written by |
|---|---|---|
| session | the setting connection's `ClientContext` address (`fabricator::SessionKeyFor`) | an unqualified `SET` (AUTOMATIC → SESSION) |
| global | `0` | `SET GLOBAL`, and every registration default |

`ProviderSettingsStore.GetString` resolves **session ?? global**, and the typed getters go through it so
they cannot diverge. The read path learns the session from `ProviderSettingsStore.CurrentSession`, an
`AsyncLocal<long>` mirroring `AmbientOpener`.

**⚠ The session is NOT the host-FS opener, and that is why it is a separate ABI parameter rather than
something the managed side derives.** They are set at the same moments (so `set_active_opener` carries both,
which is what stops them drifting), but the commit flush and the rollback deliberately open their *own*
short-lived connection and pass ITS context as the opener — the user's transaction is already ending and the
secret manager needs an active one. Keying settings off that connection would resolve a flush-time write
against a connection that has set nothing.

- ⚠ **That separation is REASONED, NOT MEASURED.** Deriving the session from the flush connection is a
  mutant that **survives** every shape that could be constructed for it: the eager-write buffer and the
  transaction hoist moved essentially every tuning-sensitive write to STATEMENT time, where the session is
  trivially correct — a buffered `INSERT`, a `CREATE OR REPLACE … AS SELECT` inside `BEGIN`/`COMMIT`, and
  even a CDF table's `_change_data` files were all measured correct under the mutant. Treat it as
  correct-by-construction insurance against a write moving back onto the flush path, not as a fix for an
  observed defect.
- ⚠ **The rollback's session must be read BEFORE `transactions.erase()`** — that map OWNS the
  `FabricatorTransaction`, so erasing destroys it and any later use of the reference is a use-after-free.
  It comes from `Transaction::context`, DuckDB's `weak_ptr` to the originating connection; a connection
  already torn down yields 0 (the global layer), which is the safe fallback.

**⚠ Lifetime is correctness, not housekeeping.** The session key is a `ClientContext` **address**, so an
entry left behind can be inherited by a later connection the allocator happens to place at the same address
— a silent wrong answer surfacing only under connection churn (a dbt run), where it is hardest to attribute.
A `FabricatorSessionSettingsState : ClientContextState` is registered on the context at the first
session-scoped `SET` (lazily, so a connection that never sets anything costs nothing); a
`ClientContextState` is held for the context's whole life, so its **destructor** is the connection-close
signal — there is no explicit close callback to hook. It calls `clear_session_settings`.

**⚠ `RESET` at session scope LATCHES "unset" — it does not fall back to the global value.** MEASURED with
DuckDB's own vocabulary: after `RESET delta_write_options`, `current_setting('delta_write_options')` reports
NULL, and it *still* reports NULL after a subsequent `SET GLOBAL … = gzip` in the same connection. That is
DuckDB's behaviour (`PhysicalReset::ResetExtensionVariable` stores the option's DEFAULT as the connection's
own value), so we match it deliberately — which keeps our resolution and `current_setting`'s answer in
agreement, the property that matters when a user diagnoses a write by reading the setting back.

**⚠ `SET` and `RESET` hand the callback the scope DIFFERENTLY.** `PhysicalSet::SetExtensionVariable` calls
it with the RAW scope and resolves `AUTOMATIC` *afterwards*; `PhysicalReset::ResetExtensionVariable`
resolves it *before* calling. So the trampoline must resolve `AUTOMATIC` itself, and to the same value
DuckDB will — hence `FABRICATOR_SETTING_DEFAULT_SCOPE`, passed explicitly to `AddExtensionOption` as well
so the two readings come from one constant.

**Gates.** `test/verify_setting_scope.test` (**30**, hermetic) pins it with `delta_write_options`
compression as the observable — the setting's effect is written into the parquet files, so it is read back
with `parquet_metadata` rather than inferred. Mutation-tested: restoring the pre-v69 behaviour (discard the
scope, always write the global layer) kills it at exactly the §1 assertion, with the symptom the leak
produces (ZSTD where SNAPPY is expected). Tier-0 `ProviderSettingsScopeTests` adds 9 offline cases over the
store's layering. ⚠ The §1 **positive control** is load-bearing: without it the "con_b writes SNAPPY"
assertion would pass equally if the setting had stopped working, or stopped reaching the writer, entirely.

**⚠ Found while gating it, unrelated to scoping — fixed the same day, and it was the FOURTH site of one
defect.** On `native_write` a flush-path parked-batch write came out SNAPPY regardless of
`delta_write_options`, while statement-time native writes honoured it. Measured on the one shape that still
retains batches until COMMIT (an IDENTITY table's buffered INSERT): codec ZSTD, native SNAPPY.

`EnsureHeldTableAsync` *did* pass the spec — to `DeltaWriter.Options(...)`, which configures
engineered-wood's `ParquetWriteOptions`, which is why the codec engine worked. Under `native_write` the bytes
come from DuckDB's COPY through `NativeParquetDataFileWriter`, which never reads those options and takes the
spec as a **constructor** argument; it was constructed with the path alone.

The 2026-08-07 pass had already diagnosed exactly this ("threading the spec into the EW open was necessary
and NOT sufficient") and fixed the three `DeltaReader` constructions plus `DeltaGlobalTableFunction` — this
one was not in that sweep. **When the fix is "pass the argument the constructor already accepts", grep every
construction, not the sites the bug was reported against.**

⚠ The gate beside it could not have caught this, and its own comment says why: it *requires* the codec
engine, because under `native_write` engineered-wood's options never apply. So the native half was asserted
nowhere. **Fixing half the writers is invisible to a gate pinned on the half that works.**
`verify_with_options` 199 → **207** adds the native leg (mutation-tested; needs an IDENTITY column, since
that is the only branch still writing through the held table).

**⚠ A GAP I "FOUND" IN THE ATTACH PATH DID NOT EXIST, and the mutant is what settled it.** `mssql_mars` is
resolved once per catalog and `fabricator_storage.cpp` establishes no session before `open_catalog`, so a
fresh connection's `SET mssql_mars='false'; ATTACH …` looked like it would read the GLOBAL layer and
silently produce a MARS-ON catalog. It does not: `OpenCatalog` merely CONSTRUCTS the catalog (no connect, no
`EnsureProfile`), and the metadata calls that follow establish the session themselves via
`FabricatorSetActiveTxn`. Adding the call changed nothing — `mars_enabled` is `false` either way. **The
error was inferring a gap from one FILE not containing a call, without checking whether a CALLEE made it**
— the same backwards-reasoning this project has recorded before. The fix was reverted; what survives is the
observable it needed:

- **`fabricator_server_info()` gained `mars_enabled`** — the value THIS catalog resolved, beside the
  server's `supports_mars` capability. Until now nothing in SQL could distinguish them, which is precisely
  why `verify_mars_off_same_catalog` could pass vacuously (its own header warns that a wrong SET/ATTACH
  order "silently produced a MARS-ON catalog and a vacuously passing suite" — with no assertion able to
  tell). That suite's new §0 asserts the pair for both catalogs; `verify_server_profile`'s property count
  goes 14 → 15.

### 5.4 Per-connection MARS — BUILT (change B, 2026-08-11, C#-only)

`mssql_mars` was the **last setting still baked at first connect**; every other one is already read at use
time, which is what made §5.3 sufficient for them and made this a one-setting job.

Two things were wrong, and the second is the one that mattered:

1. A `SET mssql_mars` after the ATTACH was a **silent no-op** — the README had to say "set it before
   ATTACH", and nothing could show you that you had failed to.
2. **An ATTACH is DATABASE-level**, so one `SqlServerCatalog` is shared by every DuckDB connection — even a
   correctly-ordered SET applied to all of them.

`EnsureProfile` still detects the SERVER profile once (it describes the server) but now builds **both**
connection strings; `OpenConnection` picks per open from `EffectiveMars()`, which reads the current session.
Two stable strings rather than one rebuilt per open, because **SqlClient pools by connection string** — a
pair gives two pools, not a pool per open.

**⚠ The routing must ask about the connection in play, not about the session.** `TxnState.MarsEnabled`
records what the PINNED connection was opened with, and the routing/self-block sites read it (`TxnMars()`);
"may this scan reuse the pinned connection?" is a question about that connection. A fresh resolve could send
a scan onto a no-MARS pinned connection — limitation 1.15's unbounded hang, not an error.

- ⚠ **Defensive, not gated.** The mutant SURVIVES, necessarily: a DuckDB transaction belongs to ONE
  connection, so the answers differ only if that session changes `mssql_mars` between pinning and the scan
  — meaningless as a request, and its failure mode is a hang, so a gate would be a test that hangs rather
  than fails.

`fabricator_server_info`'s `mars_enabled` is now **session-dependent** — two connections on one catalog can
report different values, which is the feature. An invalid value is refused at the first statement that opens
a connection rather than at ATTACH, because validation lives where the value is resolved and that moved.

**⚠ It removed a capability, and the gate caught it — a new `mars` ATTACH option restores it.** Freezing the
mode per catalog was what made `SET; ATTACH; SET; ATTACH` produce two catalogs on different modes. Under a
session-scoped resolve those two attaches are identical, and whichever value the session holds last governs
both: `verify_mars_off_same_catalog` §0 failed because its `m_on` "MARS ON control" was silently running
with MARS off. So the per-catalog form is now explicit —
`ATTACH … (TYPE fabricator, mars 'auto'|'true'|'false')`, precedence `SET ?? ATTACH option ?? auto`, the
same shape as every other behaviour option here.

> **A capability that exists only as a side effect of caching disappears when you fix the caching, and
> nothing about the change announces it.** This was visible only because §0 had been added hours earlier for
> an unrelated reason (the vacuous-pass hole). A suite that merely "still passes" would have hidden it.

**Gate** `test/verify_mars_dynamic.test` (**44**, service tier), mutation-tested: re-introducing the
per-catalog cache kills it at §1's post-ATTACH SET. §3 is the load-bearing section — a true A/B where two
sessions on ONE attached catalog run byte-identical statements and differ only in `mssql_mars`, giving
**400** (MARS on ⇒ drained onto the pinned connection ⇒ read-your-writes) vs **200** (MARS off ⇒ pooled at
SNAPSHOT ⇒ committed state only). Without it the suite would pin a reporting string while connections kept
using a cached mode.

**⚠ Scoping is necessary, not sufficient, for per-model configuration.**
[consumption-monitoring.md](consumption-monitoring.md) §2.4c measured **3 distinct connections serving 4
models** — dbt-duckdb reuses connections, so a pre-hook's `SET` persists to the next model on that
connection. A+B remove the CONCURRENT leak and the permanent one; a genuinely per-model setting also needs a
post-hook `RESET`. Making MARS a
per-connection decision is a separate change (B), deliberately sequenced *after* this one: doing it first
would have upgraded today's harmless no-op into a live cross-model leak.

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
