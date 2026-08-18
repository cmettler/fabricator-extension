# Plugin system (third-party backends + global functions)

> Status: **default-context SPI BUILT + verified; ALC isolation DEFERRED.** A plugin (a folder of managed
> assemblies) dropped into an **`FABRICATOR_PLUGIN_DIR`** folder is discovered at load, its `IBackend`(s)
> registered, and its **global functions** surfaced as a bare `fn(...)` with NO ATTACH — verified end-to-end
> (`Fabricator.SamplePlugin`'s `plug_greet`, `test/verify_plugin.test`). Loaded into the **default (non-isolated)**
> context for now; per-plugin `AssemblyLoadContext` **isolation** (for conflicting transitive deps) is the
> deferred upgrade — a loader-internal swap, no contract change. The contract assembly **`Fabricator.Abstractions`
> is extracted** (a plugin references it + Apache.Arrow only — see recommendation #2). Builds on `BackendRegistry` +
> [docs/global-functions.md](global-functions.md) + [docs/provider-extensibility.md](provider-extensibility.md).
> The load-bearing constraint regardless of ALC: **Apache.Arrow must be shared, never isolated**.
>
> **As-built (no-ALC SPI)** — the one non-obvious real-world finding: hostfxr loads the bridge into a
> **non-default ALC**, so the loader must load plugins into the **bridge's own context**
> (`AssemblyLoadContext.GetLoadContext(typeof(BackendRegistry).Assembly)`), NOT `AssemblyLoadContext.Default`.
> Loading into Default made the plugin bind to a *separate* copy of `Fabricator.Bridge` (from the plugin dir) → its
> `IBackend` was a different, non-assignable type → 0 backends registered. The loader (`BackendRegistry`):
> splits `FABRICATOR_PLUGIN_DIR` (comma list of dirs), installs a `Resolving` hook on the host context (probes the
> plugin dirs for a plugin's private transitive deps), skips assemblies already loaded in the host context (the
> shared set — `Fabricator.Bridge`, `Apache.Arrow`, built-in providers — so a plugin-dir copy of the bridge isn't
> reflected + its `StubBackend` re-registered), and `LoadFromAssemblyPath`s the rest into the host context,
> reflecting for `IBackend`. The scan runs inside `Discover()` (first `BackendRegistry.All()`, at load — before
> the `list_global_functions` union), so a plugin's global functions register with **no ABI/C++ change**. No-op
> Sample: `dotnet/Fabricator.SamplePlugin` (a catalog-less `IBackend` whose
> only job is to contribute the global scalar `plug_greet`), built to a folder and pointed at via the env var.

## The 2026-08-18 pass: a DEFAULT root, a RECURSIVE search, and a scan that says what it did

Three changes, all C#-only, no ABI. Together they are the prerequisite for a plugin INSTALLER (see
[the installer sketch](#installing-a-plugin--sketch-not-built) below) rather than features in their own right.

- **A default root: `~/.duckdb/fabricator/plugins`.** Before this the scan RETURNED IMMEDIATELY when
  `FABRICATOR_PLUGIN_DIR` was unset, so there was nowhere to install to. `FABRICATOR_PLUGIN_DIR` still wins,
  and it **REPLACES rather than extends** the default — a rig that narrows the search must actually get a
  narrow search, or it is not testing what it claims.
  - **⚠ NOT under the managed directory, and that is a MEASURED hazard rather than taste.** Several
    projects publish into the managed dir and `dotnet publish` DELETES files its own previous publish wrote
    whose closure no longer contains them — that is what silently removed five `Microsoft.Data.SqlClient`
    DLLs from a populated payload on 2026-08-18. A plugin installed there would be wiped by an ordinary
    `publish-managed.ps1` run, with no error. `~/.duckdb` is also DuckDB's own per-user directory (where
    `INSTALL` puts extensions), is writable without admin, and is STABLE while the managed dir is not: that
    one moves between a build tree, `~/.duckdb/extensions/<version>/<platform>/`, and the single-file
    distribution's cache.
- **The search is RECURSIVE.** It was `Directory.GetFiles(dir, "*.dll")` — top level only — so a plugin laid
  out the way an installer writes one (`<root>/<name>/<version>/<platform>/`) was never seen. Candidates are
  ordered by path, which is not cosmetic: the FIRST provider registered under a name wins, and `Directory`
  enumeration order is filesystem-dependent, so an unordered scan makes *which plugin wins* a property of the
  disk rather than of the configuration. The dependency-probing `Resolving` hook now gets every directory
  holding a candidate, not just the roots — a plugin's private deps sit next to it, several levels down.
- **`SELECT * FROM fabricator_plugins()`** — one row per root plus one per candidate, with a status and a
  reason: `root` / `root_missing` / `loaded` / `no_backend` / `shared` / `rejected`.

**⚠ WHY THE DIAGNOSTIC IS THE LOAD-BEARING PART.** The scan ends every candidate in a `catch`, so a plugin
built against a different `Apache.Arrow` major, or missing a private dependency, was skipped with **no signal
at all** — and a failing `verify_plugin` is indistinguishable from "the plugin loaded and chose to register
nothing". Four states used to be one silence, and each now names itself:

| status | means |
|---|---|
| `root_missing` | a configured root does not exist — the most common real cause, previously invisible |
| `rejected` | load or reflection threw; `detail` carries the exception (e.g. `BadImageFormatException`) |
| `no_backend` | loaded fine, declares no `IBackend` — the ordinary state of a plugin's private dependency, and NOT a failure |
| `shared` | skipped because the host already has an assembly of that name — deliberate, so it must be visible |

Gates: `verify_plugin` 10 -> **17** (service tier), **mutation-tested** — a scan that records nothing dies at
assertion 11, i.e. after all ten pre-existing plugin assertions pass, which is the right kill because the
plugin still WORKS and only the report is silent. Plus **10 tier-0 cases** (`PluginPathsTests`, floor 196 ->
206) for the two properties SQL structurally cannot reach: the default root is under the real user's home, so
no hermetic suite may create it, and the override precedence is only observable with the variable UNSET —
which `verify_plugin` must set.

- ⚠ **A PLUGIN CAN STILL SHADOW A BUILT-IN PROVIDER SILENTLY — found while building the report, NOT fixed,
  and deliberately out of this pass's scope.** `BackendRegistry.Add` is `map[backend.Name] = backend`, an
  OVERWRITE, so a plugin whose `IBackend.Name` is `sqlserver` replaces the first-party provider and the scan
  reports it as an ordinary `loaded` row. Pre-existing behaviour (nothing about this pass changed it) and
  nobody has hit it, but it is exactly the class of silence `fabricator_plugins()` exists to remove, and the
  report is the natural place to surface it: the `detail` of a `loaded` row could name any provider name it
  DISPLACED. Doing it needs a decision this pass did not want to take — whether shadowing should be reported,
  refused, or allowed as an override mechanism.
- ⚠ **No cap on the candidate count, deliberately.** A self-contained plugin can carry hundreds of DLLs and
  most will be `rejected`; a silent truncation would read as "covered everything". Every one gets a row.
- ⚠ **`Environment.GetFolderPath(SpecialFolder.UserProfile)` does NOT read `%USERPROFILE%` on Windows** — it
  calls the Win32 shell API. So the empty-profile trick this repo uses to simulate a bare runner does not
  redirect the default plugin root.

## Installing a plugin — sketch, NOT built

The shape agreed 2026-08-18: a zip carrying `any/` (platform-independent) and `<platform>/` folders named with
DuckDB's own platform strings (`windows_amd64`, `linux_amd64`, `osx_arm64` — the extension already knows its
own), plus a manifest declaring name, version, entry assembly and the `Fabricator.Abstractions` version it was
built against; `fabricator_install_plugin(<zip>)` extracts it under the default root.

**Most of the machinery exists**: `Fabricator.Installer.Core` is the same problem solved for the extension
itself — `PayloadExtractor` already does zip extraction with a working zip-slip guard, `PayloadManifest`
already carries the platform string, and there is a `CrossProcessLock` and `Hashing`. It is BCL-only and
tier-0 tested.

**⚠ THE RELOAD QUESTION SPLITS, and only one half is a problem** — this is what should drive the design:

| a plugin contributes | resolved when | addable mid-session |
|---|---|---|
| `IBackend` (`ATTACH ... PROVIDER 'x'`) | at ATTACH, via `BackendRegistry.Resolve` | **yes** — needs only an invalidation of the memoized map |
| catalog-bound functions | at ATTACH, via that catalog | **yes** — rides on the above |
| **global functions** | `loader.RegisterFunction` during `Extension::Load()` | **no, by no trick** |

DuckDB permits global registration only during extension load, **has no unload API at all**, and re-`LOAD` of
a loaded extension is a no-op. `GlobalFunctions`' maps are `Lazy<>` besides — evaluated once per PROCESS — so
even a second database instance would miss a newly installed plugin's globals.

**⚠ Upgrade and uninstall are the hard part.** `LoadFromAssemblyPath` maps the file, which LOCKS it on
Windows, and the bridge's ALC (created by hostfxr) is not collectible — so a loaded assembly can never be
replaced in-process. That forces the UX: install into a version-stamped folder and activate at next start;
uninstall must mark for deletion rather than delete.

**Security, stated once:** this is arbitrary in-process .NET execution from a SQL-reachable path, unsandboxed.
DuckDB gates its own unsigned extensions behind `allow_unsigned_extensions`; an installer should do at least
the same, refuse remote URLs initially, and never auto-install.

## Why / when

Today the bridge loads providers (`Fabricator.SqlServer`, `Fabricator.AnalysisServices`) by reflection into the
**default** ALC (`BackendRegistry`, env `FABRICATOR_BACKEND_ASSEMBLY`). They're all version-aligned with the
bridge, so they share one of everything — fine. A plugin system adds value only when plugins have **genuinely
conflicting managed deps** (e.g. two providers needing different Azure SDK / Newtonsoft versions) or are
**third-party** (you don't control their dependency graph). ALC isolation gives each plugin its own private
dependency closure while still speaking the shared contract.

It **works on our host**: the bridge runs on CoreCLR (hostfxr, self-contained .NET 10), where `AssemblyLoadContext`
+ `AssemblyDependencyResolver` are the standard mechanism. The two classic limitations don't bite us — we are
not Native AOT (ALC exists) and not loading .NET Framework 4.x assemblies (CoreCLR only).

## The crux — Apache.Arrow MUST be shared (the whole boundary hinges on this)

Every cross-boundary call traffics **Apache.Arrow** types: `IScalarFunction.Parameters → Schema`,
`Invoke(RecordBatch) → IArrowArray`, `ITableFunctionBinding.Execute → IArrowArrayStream`, and the bridge's
C-ABI marshaling (`CArrowArrayStreamExporter`/`Importer`, `CArrowSchemaExporter`) all operate on `Apache.Arrow`
types. **Types from different ALCs are not assignable.** So if a plugin loaded its own `Apache.Arrow`, its
`RecordBatch` would be a *different type* than the bridge's and every `Invoke`/`Bind`/export would throw
`InvalidCastException` (or hand the exporter a foreign object).

Therefore:
- **`Apache.Arrow` + `Apache.Arrow.C` are contract surface** — loaded **once, in the default context**, shared
  by the bridge and every plugin.
- **Hard constraint:** every plugin pins the **same Apache.Arrow version as the bridge** (today 23.0.0).
  Isolation buys plugins freedom for their *other* managed deps (SqlClient, ADOMD, Fluid, Azure.Identity,
  engineered-wood, JSON, …) but **never for Arrow**. (engineered-wood works in a plugin ALC precisely because
  it is already Arrow-23-aligned; a plugin needing a *different* Arrow for some private lib simply cannot.)

## The shared boundary for fabricator

Extract a thin **`Fabricator.Abstractions`** assembly = the interfaces + the Arrow-typed contract POCOs
(`IBackend`, `IBackendCatalog`, `IScalarFunction`/`ICatalog*`, `ITableFunction`/`ITableFunctionBinding`,
`IInOutFunction`, `ICollectorTableFunction`, `IAggregateFunction`/`IAggregateState`/`IAggregateSession`,
`ProviderSetting`, `SecretField`, `TableFunctionScan`, `ScanSpec`, `FilterNode`, `IBoundTableFunction`, …). Shared
(default context). `Fabricator.Bridge` references it and keeps the ABI/marshaling/`Bootstrap`/`GlobalFunctions`/
`BackendRegistry` (also default context — it's the hostfxr entry assembly). A plugin references **only**
`Fabricator.Abstractions` + `Apache.Arrow` (both host-provided, NOT copied into the plugin dir) + its own private
deps.

Why a separate Abstractions rather than "bridge = contracts": plugins should bind to a **minimal, stable
contract surface**, not the ABI internals (`Bootstrap`'s `[UnmanagedCallersOnly]` exports, the marshaling). It
also guarantees every type in a contract signature is shared (Abstractions / Apache.Arrow / BCL) — no contract
method exposes a plugin-private type, which would otherwise force *that* dependency to be shared too.

**The complete shared set** (returned as `null` from a plugin's `Load`, i.e. resolved from the default context):
`Fabricator.Abstractions`, `Apache.Arrow`, `Apache.Arrow.C`, and the BCL/`System.*` (the runtime shares framework
assemblies automatically).

## PluginLoadContext — the one correction over the textbook sketch

The standard sketch returns `null` from `Load` only when the resolver *misses*. **That is insufficient here:**
`AssemblyDependencyResolver` will *succeed* for `Apache.Arrow` (it's in the plugin's `deps.json`), so it would
load an **isolated Arrow copy** and break everything. You must short-circuit the shared set to `null` **before**
consulting the resolver:

```csharp
private static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase)
{
    "Fabricator.Abstractions", "Apache.Arrow", "Apache.Arrow.C",
    // BCL/System.* are shared by the runtime automatically.
};

public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    public PluginLoadContext(string pluginPath) : base(isCollectible: false) // we never unload (see Lifetime)
        => _resolver = new AssemblyDependencyResolver(pluginPath);

    protected override Assembly? Load(AssemblyName name)
    {
        if (Shared.Contains(name.Name!)) return null;   // force fall-through to AssemblyLoadContext.Default
        var path = _resolver.ResolveAssemblyToPath(name);
        return path != null ? LoadFromAssemblyPath(path) : null; // else plugin-private
    }

    protected override IntPtr LoadUnmanagedDll(string name)
    {
        var p = _resolver.ResolveUnmanagedDllToPath(name);
        return p != null ? LoadUnmanagedDllFromPath(p) : IntPtr.Zero;
    }
}
```

`null` → the runtime falls back to `AssemblyLoadContext.Default`, where the bridge already loaded the shared set
— so `typeof(IBackend).IsAssignableFrom(pluginType)` resolves to the *same* `IBackend` and the cast works (the
reason the contracts must be shared). Plugins build the shared refs with `<Private>false</Private>` /
`ExcludeAssets` so they aren't copied into the plugin folder (the host provides them).

## Lifetime — non-collectible (no unload machinery)

Global functions register at `Extension::Load` and live for the process; `BackendRegistry` / `GlobalFunctions`
(static, default context) hold the plugin objects, which pins the plugin ALCs alive regardless. So use
**`isCollectible: false`** — simpler and faster, and it skips the `WeakReference<AssemblyLoadContext>` + unload
discipline (and the restrictions collectible ALCs impose). We never unload a plugin.

## Integration (additive to BackendRegistry)

- **First-party providers (SqlServer, DAX) stay in the default context** — version-aligned with the bridge, so
  isolation buys them nothing.
- Add a **plugin-dir scan**: for each plugin folder → a `PluginLoadContext` → load its entry assembly → find
  `IBackend` types (their `IBackend` resolves to the default-context one, so `IsAssignableFrom` works) →
  instantiate → `BackendRegistry.Register`. Their `GlobalScalarFunctions` / `GlobalInOutFunctions` / … get
  unioned by `GlobalFunctions` exactly like first-party ones — **no change to the global-function machinery**,
  since it only ever touches shared (Arrow / Abstractions) types.
- **Timing:** the scan must run at bridge init, **before** the first `list_global_functions` (which lazily
  unions `BackendRegistry.All()`). Slot it into `Bootstrap.Initialize` / first registry access.
- **Static state stays in the default context** (`BackendRegistry`, `GlobalFunctions`, `ProviderSettingsStore`,
  `Handles`, `AmbientTransaction`) — one process-wide instance the plugin objects register into. Correct (one
  registry, one handle table); it also means these are contract surface (already in the bridge/Abstractions).

## Gotchas

- **Native deps aren't ALC-isolated.** `ResolveUnmanagedDllToPath` resolves a plugin's native libs per ALC, but
  the OS loads a native DLL **once per process** — two plugins needing *different native* versions of the same
  library still collide (e.g. SqlClient's native SNI on Windows). ALC cleanly isolates **managed** conflicts
  only; flag native conflicts as out of scope.
- **One Arrow version, forever-pinned** (restated because it's the whole ballgame): bumping the bridge's
  Apache.Arrow is a coordinated change across all plugins.
- **Reflection across ALCs** works only through the shared contracts — a plugin type is matched via the
  default-context `IBackend`; never reflect over a plugin's private types from the host.

## Recommendation (sequenced)

1. **Default-context plugin-dir loader — DONE** (this build): `FABRICATOR_PLUGIN_DIR` scan in `BackendRegistry`,
   plugins loaded into the **bridge's** ALC (not Default — see As-built), additive beside the env-assembly
   discovery. Plugins reference `Fabricator.Bridge` directly (no `Abstractions` needed without ALC — everything is
   one context). Sample plugin + `verify_plugin.test`. **Plugins must align their full dependency closure with
   the host** (Apache.Arrow always; every other shared dep too — there is no version isolation without ALC).
2. **Extract `Fabricator.Abstractions` — DONE** — the contract surface (the `I*Function`/`IBackend`/`IBoundTableFunction`/
   `IAggregateSession` interfaces + `ProviderSetting`/`SecretField`/`TableFunctionScan`/`ScanSpec`/`FilterNode`)
   is now a separate assembly, **kept in the `Fabricator.Bridge` namespace** (assembly split only — zero source
   churn). `Fabricator.Bridge` references it (the ABI/marshaling/`Bootstrap`/`BackendRegistry`/Static-bases/
   adapters stay in Bridge); the `BackendRegistry`, `InOutExchangeStream`/`InOutExchange`, and
   `CollectorInOutBinding` impls split back out of their old interface files into Bridge. `Fabricator.SamplePlugin`
   now references **`Fabricator.Abstractions` ONLY** (+ Apache.Arrow, host-provided) — a lean, Bridge-independent
   plugin surface (its plugin folder is just `Fabricator.Abstractions.dll` + the plugin dll). Behavior-preserving;
   full `verify_*` suite + `verify_plugin` green.
3. **ALC isolation** (deferred) — a loader-internal swap (`host.LoadFromAssemblyPath` → a per-plugin
   `PluginLoadContext`) with the shared-name allowlist `Load` above (non-collectible). **Adopt only when a real
   dependency conflict / a third-party plugin with conflicting managed deps lands** — version-aligned plugins
   gain nothing and pay the cost (per-plugin `deps.json`, the allowlist, the "don't copy the shared set" build
   config, the native-dep caveat). The contract + the plugin packaging do NOT change when isolation is turned on.

**Net:** the SPI is built and works on our CoreCLR host without ALC — third-party plugins contribute backends +
global functions today, provided they align their dependency closure with the host (Apache.Arrow always). ALC
isolation is a non-breaking later upgrade to the loader, worth turning on only when a genuine dep conflict
appears; the must-fix for that day is the explicit shared-name allowlist in `Load` (the resolver would otherwise
isolate Apache.Arrow and break every Arrow-typed call).
