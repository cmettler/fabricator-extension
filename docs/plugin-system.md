# Plugin system (AssemblyLoadContext isolation) — design idea, DEFERRED

> Status: **design note only — nothing built.** A plugin SPI where each plugin (a folder of managed assemblies
> + its `deps.json`) contributes one or more **backends** (`IBackend`) and **global functions**, optionally
> isolated in its own `AssemblyLoadContext` (ALC) so plugins with conflicting transitive dependencies can
> coexist (the diamond-dependency problem). Builds on `BackendRegistry` (the current provider discovery) +
> [docs/global-functions.md](global-functions.md) + [docs/provider-extensibility.md](provider-extensibility.md).
> Works on our CoreCLR host; the load-bearing constraint is that **Apache.Arrow must be shared, never isolated**.

## Why / when

Today the bridge loads providers (`ArrowNet.SqlServer`, `ArrowNet.AnalysisServices`) by reflection into the
**default** ALC (`BackendRegistry`, env `ARROWNET_BACKEND_ASSEMBLY`). They're all version-aligned with the
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

## The shared boundary for arrownet

Extract a thin **`ArrowNet.Abstractions`** assembly = the interfaces + the Arrow-typed contract POCOs
(`IBackend`, `IBackendCatalog`, `IScalarFunction`/`ICatalog*`, `ITableFunction`/`IArrowTableFunctionBinding`,
`IInOutFunction`, `ICollectorTableFunction`, `IAggregateFunction`/`IArrowAggregateState`/`IAggregateSession`,
`ProviderSetting`, `SecretField`, `TableFunctionScan`, `ScanSpec`, `FilterNode`, `IBoundTable`, …). Shared
(default context). `ArrowNet.Bridge` references it and keeps the ABI/marshaling/`Bootstrap`/`GlobalFunctions`/
`BackendRegistry` (also default context — it's the hostfxr entry assembly). A plugin references **only**
`ArrowNet.Abstractions` + `Apache.Arrow` (both host-provided, NOT copied into the plugin dir) + its own private
deps.

Why a separate Abstractions rather than "bridge = contracts": plugins should bind to a **minimal, stable
contract surface**, not the ABI internals (`Bootstrap`'s `[UnmanagedCallersOnly]` exports, the marshaling). It
also guarantees every type in a contract signature is shared (Abstractions / Apache.Arrow / BCL) — no contract
method exposes a plugin-private type, which would otherwise force *that* dependency to be shared too.

**The complete shared set** (returned as `null` from a plugin's `Load`, i.e. resolved from the default context):
`ArrowNet.Abstractions`, `Apache.Arrow`, `Apache.Arrow.C`, and the BCL/`System.*` (the runtime shares framework
assemblies automatically).

## PluginLoadContext — the one correction over the textbook sketch

The standard sketch returns `null` from `Load` only when the resolver *misses*. **That is insufficient here:**
`AssemblyDependencyResolver` will *succeed* for `Apache.Arrow` (it's in the plugin's `deps.json`), so it would
load an **isolated Arrow copy** and break everything. You must short-circuit the shared set to `null` **before**
consulting the resolver:

```csharp
private static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase)
{
    "ArrowNet.Abstractions", "Apache.Arrow", "Apache.Arrow.C",
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

## Recommendation (sequenced; build on demand)

1. **Extract `ArrowNet.Abstractions`** (the contract surface) and have `ArrowNet.Bridge` reference it. Pure
   refactor, no behavior change — the natural prerequisite, and useful on its own (a stable, minimal SPI surface
   that decouples providers from the ABI internals).
2. **A plugin-dir loader** with the shared-name allowlist `Load` above (non-collectible), additive beside the
   default-context `BackendRegistry` reflection. First-party providers stay in the default context; external
   plugins go in per-plugin ALCs.
3. **Adopt ALC isolation only when a real conflict or a third-party plugin lands** — two version-aligned
   first-party providers gain nothing from it and pay the cost (per-plugin `deps.json`, the allowlist, the
   "don't copy the shared set" build config, the native-dep caveat).

**Net:** the sketch is sound for our CoreCLR host; the single must-fix is the explicit shared-name allowlist in
`Load` (the resolver would otherwise isolate Apache.Arrow and break every Arrow-typed call); the clean shape is
a thin shared `ArrowNet.Abstractions` + `Apache.Arrow` + non-collectible per-plugin ALCs, kept as an opt-in path
beside the default-context first-party providers. Worth designing the SPI now; worth turning on isolation only
when a dependency conflict actually appears.
