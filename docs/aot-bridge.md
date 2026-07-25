# NativeAOT bridge SKU: compile-time providers, source-generated registry

Status: **DESIGN — nothing built.** Target: an optional **AOT SKU** of the fabricator managed
layer — `Fabricator.Bridge` + providers compiled with NativeAOT into a single native library —
loadable by the existing C++ extension with **no .NET installation and no runtime assembly
loading**, with the reflection-based backend/plugin discovery replaced by **source
generators**. The CoreCLR SKU (full provider set incl. DAX + runtime plugin dir) remains the
default; both SKUs build from one codebase.

Companion: [distribution-installer.md](distribution-installer.md) — the AOT SKU collapses
that design's payload to (C++ core + one native bridge file), no .NET prerequisite, no
FDD/self-contained split.

---

## 1. Why we are not AOT today — and what has changed

The original architecture chose hostfxr-hosted CoreCLR + reflection deliberately, for two
reasons:

1. **ADOMD** (`Microsoft.AnalysisServices.AdomdClient`) — the DAX provider's client — is
   closed-source, reflection/XMLA-serialization heavy, and not AOT-compilable. Still true;
   the AOT SKU **excludes the DAX provider** (see §7).
2. **The plugin system** (`FABRICATOR_PLUGIN_DIR` — runtime DLL scan + load) requires runtime
   IL loading, which NativeAOT does not have. Replaced for the AOT SKU by compile-time
   composition (§6).

What has changed since: the rest of the dependency tree has moved toward AOT
(SqlClient 6.x dropped native SNI and ships trim/AOT annotations; AWS SDK v4 and the Azure
SDKs are AOT-annotated), and the distribution work (installer doc) makes a
no-runtime-required SKU very attractive: the managed payload would shrink from ~250 MB
(self-contained) / ~35 MB + .NET prerequisite (FDD) to **one native library with zero
prerequisites**.

## 2. The ABI is already AOT-shaped — only the bootstrap changes

This is the pleasant surprise of the audit. The entire C++⇄C# contract is a C vtable of
function pointers filled by `Bootstrap.Initialize`, where every handler is already a
`[UnmanagedCallersOnly]` static — exactly the shape NativeAOT exports natively. The reverse
direction (`FabricatorHostServices` — the `fs_*`/`host_query`/`host_log`/`is_interrupted`
callbacks) is plain function pointers too. Arrow crosses as the C Data Interface
(`ArrowArrayStream`/`ArrowSchema`/`ArrowArray`) — manual marshaling, no serializers.

The **only** hostfxr-specific piece is how the C++ side *acquires* `Bootstrap.Initialize`
(`hdt_load_assembly_and_get_function_pointer`). Under AOT that becomes a plain native
export:

```csharp
[UnmanagedCallersOnly(EntryPoint = "FabricatorBridgeInit")]
public static int BridgeInit(nint vtable, int size, nint hostServices)
    => Bootstrap.Initialize(vtable, size, hostServices);   // the existing entry, unchanged
```

`clr_host` gains a **third hosting mode** beside self-contained and FDD (both detected
today by hostfxr's presence in the managed dir):

| managed dir contains | mode |
|---|---|
| `hostfxr.*` | self-contained CoreCLR (existing) |
| assemblies, no hostfxr | provided-runtime FDD (existing) |
| **`Fabricator.Bridge.Native.<dll/so/dylib>`** (no assemblies) | **native: dlopen + dlsym(`FabricatorBridgeInit`)** — no hostfxr, no runtimeconfig, no `DOTNET_ROOT`, ~40 lines of C++ |

Everything downstream — ABI version check, handle table, ambients (`AsyncLocal` works under
AOT), sync-over-async posture (NativeAOT also has no `SynchronizationContext` on these
threads), logging, cancellation — carries over unchanged. The NativeAOT runtime
self-initializes on the first export call; each AOT shared library carries its own isolated
runtime/GC, so coexistence with a CoreCLR-hosted extension or another AOT extension (e.g.
the installer from the distribution doc) in one DuckDB process is well-defined.

## 3. The dynamic-code inventory (audited)

Everything in Bridge/SqlServer/Abstractions that is not AOT-safe today, exhaustively:

| # | site | mechanism | AOT replacement |
|---|---|---|---|
| 1 | `BackendRegistry.Discover` ([BackendRegistry.cs:112-139](../dotnet/Fabricator.Bridge/BackendRegistry.cs)) | `Assembly.Load(name)` + `GetTypes()` scan for `IBackend` + `Activator.CreateInstance` | **source-generated registry** (§5) |
| 2 | `ScanPluginDirectories` (BackendRegistry.cs:147-220) | `AssemblyLoadContext.LoadFromAssemblyPath` + `Resolving` hook | **compile-time plugins** (§6); no runtime equivalent exists under AOT |
| 3 | `Bootstrap.FormatError` ([Bootstrap.cs:1839](../dotnet/Fabricator.Bridge/Bootstrap.cs)) | `e.GetType().GetProperty("Number")` duck-typing (provider-agnostic error numbers without a SqlClient ref) | `IBackend.GetErrorNumber(Exception) → int?` default-interface-method, `null` default; SqlServer overrides with `e is SqlException se ? se.Number : null`; `FormatError` polls registered backends. Removes reflection for **both** SKUs |
| 4 | `JsonSerializer.(De)Serialize<T>` — 10 Bridge/Abstractions files (DeltaCatalog, DeltaReader, DeltaGlobalTableFunction, ExternalTableRouting, FabricLakehouse, OneLakeForwardFs, S3CommitFileSystem, ScanSpec, Bootstrap) + EW's `ActionSerializer.cs` | reflection-based STJ | one **source-generated `JsonSerializerContext`** (`FabricatorJsonContext`) enumerating the ~15 payload shapes; EW gets the same treatment as a small upstreamable patch (its Delta path has exactly one `JsonSerializer` file — the rest of its JSON is already hand-rolled `Utf8JsonReader/Writer`) |
| 5 | `std::regex`-style filters (`schema_filter` etc. C#-side) | interpreted `Regex` | **no change needed** — interpreted Regex works under AOT; `[GeneratedRegex]` is an optional micro-optimization |

Notably absent: the vast `GetProperty` grep surface is `JsonElement.TryGetProperty` (STJ
DOM — AOT-safe), and the custom-function registries (`CustomFunctions.*`) are already
explicit construction, no reflection.

## 4. Dependency AOT status (to be confirmed by the Phase-0 ILC warning inventory)

| package | expectation | note |
|---|---|---|
| `Apache.Arrow` 23 + `.C` | good | manual C-interface marshaling, function pointers; **verify** the IPC paths we touch (`DeltaWriter.Materialize` round-trip) |
| `Microsoft.Data.SqlClient` | **validated (7.1 preview)** | user-tested 2026-07-25: **SqlClient 7.1 preview compiles and works under NativeAOT** — the AOT SKU targets the 7.1+ line (6.0.2, our current pin, was managed-SNI + annotations but only experimental AOT). Remaining check is scope, not feasibility: exercise OUR paths (TLS, Entra `AccessTokenCallback`, `SqlBulkCopy`, MARS) via the suite in Phase 3 |
| `Azure.Identity` / `Azure.Storage.Files.DataLake` | good | Azure SDK AOT-annotated; **verify** MSAL edges in `ClientSecretCredential`/`DefaultAzureCredential` |
| `AWSSDK.S3` 4.x | good | v4 line ships Native-AOT support |
| `Fluid.Core` 2.31 (`fabricator_render`) | conditional | Parlot's *compiled* mode uses `System.Linq.Expressions` (not AOT); **force interpreted mode**, or exclude `fabricator_render` from the AOT SKU if it fights back |
| `Microsoft.Extensions.Logging` | good | |
| `Microsoft.Fabric.Api` | verify | OpenAPI-generated REST client |
| engineered-wood | good after #4 | pure C#; one `JsonSerializer` file (`ActionSerializer.cs`) |
| delta-dotnet (`Fabricator.DeltaRs`) | plausible, optional | P/Invoke native bridge; already an opt-in publish — defer to a later slice |
| **`Microsoft.AnalysisServices.AdomdClient`** | **blocked** | the reason the AOT SKU exists as a *variant*, not a replacement |

## 5. Source-generated backend registry (replacing reflection discovery)

### Authoring surface

`Fabricator.Abstractions` gains one marker:

```csharp
[AttributeUsage(AttributeTargets.Class)]
public sealed class FabricatorBackendAttribute : Attribute { }
```

Every provider annotates its backend (`[FabricatorBackend] public sealed class
SqlServerBackend : IBackend`). Name/aliases stay instance properties (unchanged — the
attribute only marks *what to construct*, not metadata).

### The generator

New analyzer project `Fabricator.Generators` (Roslyn incremental generator,
netstandard2.0), applied to the **head project** (§6):

- Enumerates the compilation **and all referenced assemblies** for non-abstract
  `[FabricatorBackend]` classes implementing `IBackend` with a parameterless ctor
  (assembly symbol metadata is visible to generators — no runtime scan needed).
- Emits:

```csharp
// CompiledBackends.g.cs (in the head project)
internal static class CompiledBackends
{
    [ModuleInitializer]
    internal static void Register() =>
        BackendRegistry.SetCompiledProviders(new IBackend[] {
            new Fabricator.SqlServer.SqlServerBackend(),
            new Fabricator.Bridge.DeltaBackend(),
            // … exactly the referenced provider set
        });
}
```

- Diagnostics as compile errors: zero backends found; `[FabricatorBackend]` on a type that
  doesn't implement `IBackend` or lacks a parameterless ctor; duplicate provider names.

The explicit `new` expressions double as **trim roots** — the linker keeps exactly the
referenced providers; nothing else survives, which is the point.

### `BackendRegistry` rework (benefits both SKUs)

```csharp
public static void SetCompiledProviders(IReadOnlyList<IBackend> backends); // pre-empts discovery

// the reflection path goes behind a feature switch:
// AppContext switch "Fabricator.Bridge.EnableReflectionDiscovery" (default true)
```

The AOT head sets the switch to `false` via `<RuntimeHostConfigurationOption>`; ILC treats
feature switches as constants and **trims the whole reflection/ALC branch away** — no
`IL2026`/`IL3050` suppressions needed, the code is simply gone. The CoreCLR SKU keeps the
switch on: compiled providers register first (deterministic, no assembly scan for
first-party providers), reflection remains only for `FABRICATOR_BACKEND_ASSEMBLY` overrides
and `FABRICATOR_PLUGIN_DIR`. Same source, two shapes.

## 6. The plugin story under AOT: the head project *is* the plugin configuration

Runtime drop-in plugins cannot exist under NativeAOT (no IL loading). The replacement is
**compile-time composition**:

- New **head project** `Fabricator.Bridge.Native.csproj`: references
  `Fabricator.Bridge` + the chosen providers (+ any plugin packages), sets
  `PublishAot=true`, `NativeLib=Shared`, the feature switch off, and hosts the generator.
  Its entire source is the generated registration + the `FabricatorBridgeInit` export.
- A **plugin** for the AOT SKU is a NuGet package / project reference implementing
  `IBackend` (and/or the `I*Function` interfaces) with `[FabricatorBackend]` — add the
  reference, republish the head, done. The generator turns references into registrations;
  there is no manifest, no config file, no scan.
- We ship the head as a **template** so third parties can compose their own provider set
  (their plugin + ours) with one `dotnet publish`.

Explicitly deferred alternatives:

- **Native plugin ABI** — plugins as separately-AOT'd shared libraries exporting a C vtable
  (the same trick the C++⇄bridge boundary uses, applied per-plugin). Restores drop-in
  plugins under AOT, but means marshaling the whole `IBackend`/Arrow authoring surface over
  a C ABI per plugin — a large project; only worth it if AOT-SKU users actually demand
  runtime plugins rather than recompilation.
- **DAX under AOT via a sidecar** — a small CoreCLR helper process hosting ADOMD, talked to
  over Arrow IPC. Would give the AOT SKU DAX parity at the cost of a process boundary;
  noted, not planned.

## 7. SKU matrix

| | CoreCLR SKU (default, today) | AOT SKU (this design) |
|---|---|---|
| hosting | hostfxr (self-contained / FDD) | dlopen'd native library |
| .NET prerequisite | none (SC) / .NET 8+ (FDD) | **none** |
| payload size | ~250 MB SC / ~35 MB FDD | est. 40–80 MB single file (ILC output incl. SqlClient+EW+Azure+AWS; measured in Phase 0) |
| providers | SqlServer, Delta/EW, **DAX**, DeltaRs, plugins | SqlServer, Delta/EW (DeltaRs later); **no DAX** |
| plugins | runtime dir (`FABRICATOR_PLUGIN_DIR`) | compile-time (head project) |
| cold start | CLR boot + JIT | native (fastest) |
| distribution (installer doc) | standard/standalone SKUs | one payload, no .NET probing |

Not in scope in either SKU: replacing the C++ core. A pure-C# AOT *extension* (DuckDB's C
API, e.g. via an extension kit) cannot host fabricator — the C API has no catalog/storage/
optimizer-extension surface, and fabricator is built on all three. The AOT SKU changes how
the *managed half* is compiled and loaded; the C++ half and the ABI stay identical.

## 8. Build & packaging

- `publish-managed.ps1 -Mode Aot [-Rid <rid>]` → `dotnet publish Fabricator.Bridge.Native
  -r <rid> -p:PublishAot=true -p:NativeLib=Shared` → copy
  `Fabricator.Bridge.Native.<ext>` into the managed dir (alone — its presence IS the mode
  signal for clr_host).
- `IsAotCompatible=true` (which implies the trim/AOT analyzers) goes on Abstractions,
  Bridge, SqlServer, SamplePlugin, and EW's csproj — so AOT regressions surface as **build
  warnings on every dev build**, not at publish time. AnalysisServices is deliberately not
  annotated.
- NativeAOT cannot cross-compile OSes: same per-OS build-machine story as everything else
  (Windows native, linux via WSL — AOT links glibc dynamically, so the existing
  glibc-baseline discipline applies; osx when the C++ core gets there).
- **Endgame option (experimental, later): `NativeLib=Static`** — ilc emits a standard
  object archive linkable **into the C++ loadable itself** → literally one
  `fabricator.duckdb_extension` containing C++ core + managed layer, no trampoline, no
  managed dir at all. Officially a community-supported scenario with real caveats (MSVC/ilc
  link mixing, single AOT runtime per final binary, exception-boundary discipline). Do not
  build the design around it; revisit once the shared-lib SKU is proven.

## 9. Verification & phasing

The decisive property: **the entire verify suite is SQL-level** — it exercises the managed
layer through the same ABI regardless of how the bridge was compiled. The AOT gate is
therefore "the existing suites, minus DAX, on the native bridge", not a new test corpus.

1. **Phase 0 — ILC warning inventory (~1 day).** Create the head project (SqlServer + EW
   Delta only, SqlClient bumped to 7.1+ — its NativeAOT viability is already user-validated),
   publish with AOT. The ILC/trim warning list is the ground-truth work list for the
   remaining §4 verify-items (Azure/AWS/Fluid/Arrow) and measures real output size.
2. **Phase 1 — mechanical de-reflection** (benefits both SKUs, no ABI/C++ change):
   `FormatError` → `IBackend.GetErrorNumber`; `FabricatorJsonContext` (+ the EW
   `ActionSerializer` patch, upstreamable); `IsAotCompatible` annotations; Fluid interpreted
   mode. Gate: full existing suite on the **CoreCLR** SKU (proving the refactors are
   behavior-neutral).
3. **Phase 2 — generators.** `Fabricator.Generators` + `[FabricatorBackend]` +
   `BackendRegistry.SetCompiledProviders` + the feature switch. CoreCLR SKU switches its
   first-party registration to the compiled list (reflection stays for plugin dir). Gate:
   full suite + `verify_plugin.test` unchanged.
4. **Phase 3 — the native bridge.** `FabricatorBridgeInit` export, head project, clr_host
   mode 3, publish script. Gate: **full verify sweep (minus `verify_dax`) against the AOT
   bridge** on Windows, then linux (WSL). Delta + SQL Server + S3/MinIO suites are the
   interesting ones (SqlClient TLS, Azure/AWS SDK paths under ILC).
5. **Phase 4 — distribution integration.** Installer payload variant = core + native bridge
   (no .NET probing, one SKU); live Fabric-notebook validation (the AOT bridge removes the
   notebook's dependency on the preinstalled dotnet — one less moving part).

## 10. Risks / open questions

- **R1 — SqlClient under ILC: RETIRED as a feasibility risk** (user-validated 2026-07-25 on
  the 7.1 preview — compiles and works under NativeAOT). What remains is a *version-bump*
  task (6.0.2 → 7.1+, both SKUs or AOT-head-only via a conditional package version) plus
  coverage of our specific paths (TLS, Entra token callback, `SqlBulkCopy`, MARS) — the
  Phase-3 suite gate. The Delta-only-first fallback is no longer needed.
- **R2 — Fluid/Parlot** expression compilation; force interpreted or drop
  `fabricator_render` from the SKU.
- **R3 — behavioral drift** between SKUs (trimmed code paths, culture/ICU differences —
  NativeAOT defaults differ e.g. in globalization mode; pin `InvariantGlobalization=false`
  deliberately and let the suite decide).
- **R4 — debugging/diagnostics** story is thinner under AOT (no SOS-lite attach); the
  `FABRICATOR_LOG_*` file sink and `host_log` forwarding become the primary tools — already
  built.
- **R5 — two bridges in one process** (a CoreCLR-SKU catalog and an AOT-SKU catalog loaded
  by two different extensions/builds): isolated runtimes, but both would register the same
  global function names in one DuckDB — same as double-loading today; not a new failure
  mode, document.
- **Open:** exact head/artifact naming; whether the CoreCLR SKU should *default* to the
  compiled registry and demote `FABRICATOR_BACKEND_ASSEMBLY` to plugin-dir-only; DeltaRs
  inclusion timing; whether EW's Iceberg subprojects (heavier `JsonSerializer` use) ever
  enter the AOT closure (today they don't — only the Delta path is referenced).
