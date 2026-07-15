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
> when `FABRICATOR_PLUGIN_DIR` is unset. Sample: `dotnet/Fabricator.SamplePlugin` (a catalog-less `IBackend` whose
> only job is to contribute the global scalar `plug_greet`), built to a folder and pointed at via the env var.

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
`Invoke(RecordBatch) → IArrowArray`, `IArrowTableFunctionBinding.Execute → IArrowArrayStream`, and the bridge's
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
(`IBackend`, `IBackendCatalog`, `IScalarFunction`/`ICatalog*`, `ITableFunction`/`IArrowTableFunctionBinding`,
`IInOutFunction`, `ICollectorTableFunction`, `IAggregateFunction`/`IArrowAggregateState`/`IAggregateSession`,
`ProviderSetting`, `SecretField`, `TableFunctionScan`, `ScanSpec`, `FilterNode`, `IBoundTable`, …). Shared
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
2. **Extract `Fabricator.Abstractions` — DONE** — the contract surface (the `I*Function`/`IBackend`/`IBoundTable`/
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
