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

## Installing a plugin — BUILT 2026-08-18 (`fabricator_install_plugin`)

Steps 2 and 3 of the installer, on top of the scan work above. C#-only, no ABI change. What follows is the
as-built record; the sketch it replaced is kept below it because every prediction in it held.

### What shipped

- **`BackendRegistry.Invalidate()`** — drops the memoized provider map so the next resolve re-discovers.
  Ten lines. Everything hard about it is in the three re-scan hazards below.
- **`fabricator_install_plugin(archive [, root := …] [, replace := …])`** — a table function returning ONE
  row: `name`, `version`, `platform`, `destination`, `files`, `providers`, `activated`, `detail`.
- **`fabricator_allow_plugin_install`** — a BOOLEAN setting, default false, gating the above.
- The archive contract: a `fabricator-plugin.json` manifest at the root plus `any/` and/or
  `<duckdb platform>/`, merged with the platform overlaying `any/`.

### THE THREE RE-SCAN HAZARDS — this is the part a naive `Invalidate()` gets wrong

`Map()` was `_byName ??= Discover()` and nothing ever cleared it, so **the scan had never run twice in one
process**. Three things silently depended on that, and none of them fails loudly:

1. **The "shared" skip would have DROPPED every plugin on the second scan.** An assembly cannot be unloaded,
   so on re-scan the plugins loaded by the first are in `host.Assemblies` — they match the
   already-loaded-by-the-host skip set, get reported `shared`, and are never registered into the FRESH map.
   Fixed by subtracting what we ourselves loaded from a plugin directory (`BackendRegistry.PluginLoaded`);
   `LoadFromAssemblyPath` then returns the already-loaded instance and the provider goes back in. A plugin
   whose FILES were deleted simply stops being a candidate and drops out — the right answer for an uninstall.
2. **The dependency resolver CAPTURED its probe directories.** Correct while the scan ran once; wrong the
   moment a plugin can be installed mid-session, because the new plugin's directory is not in the captured
   array and its private dependencies would not resolve — surfacing as `rejected` with a
   `FileNotFoundException` naming a dependency sitting right next to it. The hook is now installed once and
   reads a field replaced on every scan.
3. **`_defaultProvider` is deliberately NOT cleared.** It is set from the first provider discovered, so
   clearing it would let an install re-derive which provider is the default and silently re-point every call
   site that carries no provider name. An install adds a provider; it must not move the existing ones.

Existing ATTACHed catalogs are unaffected either way — they hold an already-resolved `IBackend` and its
catalog object, neither reached through the map.

### ⚠⚠ THE BUG THIS FOUND IN MY OWN FUNCTION, WHICH IS THE MOST TRANSFERABLE THING HERE

`fabricator_install_plugin` read the session-scoped opt-in setting **inside its async iterator body**. That
body runs at the first BATCH PULL — a different ABI crossing from the one that set the ambient, on whatever
thread DuckDB pulls from. `AmbientOpener` / `ProviderSettingsStore.CurrentSession` are `AsyncLocal` per
crossing, so the iterator can legitimately see **session 0**, which falls back to the GLOBAL settings layer,
where the registration default (`false`) sits. An enabled function then reports itself **disabled**.

- **It is NON-DETERMINISTIC, and it passed the first time it was run.** The same suite refused an install at
  the THIRD call in one build and the FOURTH in another — the two differing only in an unrelated mutant and a
  stderr probe. A "works on my run" check would have shipped it.
- **It was found by mutation testing, not by review** — and not by the mutant it was aimed at. The mutant died
  at the right place for the WRONG REASON ("disabled" rather than a provider mismatch), and chasing that
  discrepancy instead of banking the kill is what exposed it. **A kill by an unexplained mechanism is not a
  kill you have understood.**
- **The fix is the pattern already in the tree**: capture the ambients in `Execute()` — the plain method,
  which runs inside the crossing that set them — and re-establish them at the top of the iterator.
  `DeltaGlobalTableFunction` does exactly this, with a comment saying why, and `BulkSession` does it for its
  background thread.
- **Standing rule it generalises to: a global table function must read every ambient in `Execute()`.** By
  execution time the opener, the transaction and the settings session are all gone or arbitrary.

### Decisions worth keeping

- **The layout is FIXED, never inferred.** A flat archive (assemblies at the root) is REFUSED. The
  alternative needs a rule that recognises a platform directory by NAME, under which an archive shipping only
  `linux_amd64/` looks flat on Windows and its Linux binaries get installed — a wrong answer, not a missing
  feature.
- **The write is STAGE-THEN-MOVE.** Extraction goes to `<root>/.staging/<guid>` and the finished directory is
  `Directory.Move`d onto `<root>/<name>/<version>`: atomic on one volume, so two processes installing one
  version race on a put-if-absent instead of interleaving their writes. ⚠ Stated precisely because
  atomicity claims are where this codebase has been burned before: the refusal is EXACT on Windows
  (`MoveFileEx` without `MOVEFILE_REPLACE_EXISTING`) and CONDITIONAL on Unix (POSIX `rename` fails with
  `ENOTEMPTY` only for a NON-EMPTY destination and silently replaces an empty one). It holds here only because
  a destination is never created any other way than by this same fully-populated rename. Both staging and `.trash` live INSIDE the root to keep the move on one volume, and
  `EnumerateCandidates` now skips any path segment beginning with `.` so a concurrent scan cannot load a
  half-extracted plugin. (The ROOT itself may be dotted — the default one is `~/.duckdb/...`.)
- **`replace := true` MOVES the old directory aside rather than deleting it.** A loaded assembly is locked on
  Windows: it can be renamed, not removed. Measured — `Directory.Move` of a directory containing a loaded
  assembly succeeds on Windows.
- **The entry assembly's presence is checked BEFORE the move.** An archive that installs cleanly and contains
  no plugin is the exact "install succeeded, nothing happened" failure the scan report exists to remove.
- **`abstractionsVersion` is recorded and NOT gated on.** Nothing versions `Fabricator.Abstractions` — every
  assembly is 1.0.0.0 — so a comparison would pass always or fail always, i.e. an untestable flag. The real
  incompatibility already has an honest report: the scan records it as `rejected` with the exception.
- **`activated` is read back out of the FRESH scan**, so the row distinguishes "installed" from "installed and
  usable" rather than assuming them equal — and an install into a root nothing scans says SO, in different
  words from a plugin that was scanned and declared nothing. The two look identical in the report and mean
  completely different things.
- **The platform string is asked of DuckDB** (`pragma_platform()`), never derived from `RuntimeInformation`.
  The spelling is DuckDB's, so deriving it would be a second implementation free to drift from the one the
  archive was built against.
- **The gate is OUR setting, not `allow_unsigned_extensions`.** The latter is nearly always true by the time
  this extension is loaded at all, so gating on it would gate nothing. Remote URLs are refused outright.
- **The zip-slip guard is REUSED, not re-written.** `Fabricator.Bridge` project-references
  `Fabricator.Installer.Core` for `ArchivePath` alone. A security guard is the last thing that should exist
  twice in one codebase with two chances to drift.

### Gates

- **Tier 0, `PluginPackageTests` +34 (floor 206 → 240)**: the manifest and the merge. These are the rules an
  end-to-end suite structurally cannot reach — it installs ONE archive, built on the machine running it, for
  the platform running it, so "another platform's directory is never taken", "an archive carrying nothing for
  this platform is refused" and "a manifest naming `../..` is refused" have no fixture there. Plus two
  `PluginPathsTests` for the hidden-segment rule, including that a DOTTED ROOT is still searched (without
  which the default root would disable discovery out of the box).
- **`verify_plugin_install.test` (31, service tier)**, run against its OWN empty plugin root — every assertion
  in it is of the form "this changed", so with the plugin already loaded the before-state assertions fail and
  the after-state ones would pass with the install doing nothing at all.
  - **The load-bearing pair**: after the install the ATTACH error CHANGES from *"unknown provider"* to the
    plugin's own *"global functions only"* (nothing but a re-discovery produces that), while `plug_greet` is
    STILL absent — the documented half of the split, pinned so a future "improvement" has to reckon with why
    it cannot be added.
  - **Mutation-tested, each mutant killed at its own section**: removing `Invalidate()` dies at the install
    row (`providers` empty, `activated` false) after 9 assertions pass — the files landed, the session did not
    see them; removing the plugin-loaded subtraction dies at the ATTACH assertion with *"unknown provider"*
    after 13 pass, INCLUDING the first install's success, which is the right discrimination since that
    subtraction only becomes load-bearing on the second scan.
  - ⚠ **Hazard 2 (the resolver's directories) is REASONED, NOT GATED** — the sample plugin has no
    private dependencies, so no mutant of it dies. Say so rather than implying the three are equally covered.
- ⚠ **The archive fixture is emitted by the plugin's OWN build** (`PackPluginArchive`, MSBuild's
  `ZipDirectory`), because `zip` is not present in Git Bash on Windows and a fixture that exists on one
  platform is a gate that runs on one platform. It stages under `obj/`, not `bin/`: staging in the output
  directory put a second copy of the plugin under the now-RECURSIVE scan and `verify_plugin` duly reported TWO
  loaded plugins. Measured, not theorised — that is how the line came to be written.

### What is still NOT built

- **Uninstall.** Needs mark-for-deletion semantics (a loaded assembly cannot be removed) and a decision about
  what a half-removed plugin looks like to the scan.
- **A plugin can still SHADOW a built-in provider silently.** `BackendRegistry.Add` is an overwrite, so a
  plugin naming its `IBackend` `sqlserver` replaces the first-party provider and the scan reports it as an
  ordinary `loaded` row. Pre-existing; the report is the natural place to name a displaced provider, but the
  choice between report / refuse / allow-as-override has not been taken.
- **Signature or checksum verification** of an archive. `Hashing` is there; nothing consumes it here.

## The original sketch — kept because every prediction in it held

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

4. **The scan report, a default root and a recursive search — DONE** (2026-08-18): `fabricator_plugins()`,
   `~/.duckdb/fabricator/plugins`, and a search that reaches the nested layout an installer writes. See the
   2026-08-18 section at the top.
5. **The installer — DONE** (2026-08-18): `BackendRegistry.Invalidate()` + `fabricator_install_plugin()` +
   the `fabricator_allow_plugin_install` gate. See the Installing-a-plugin section.
6. **Uninstall** (NOT built) — needs mark-for-deletion semantics, because a loaded assembly cannot be removed
   while the process lives, plus a decision on what a half-removed plugin looks like to the scan.

**Net:** the SPI is built and works on our CoreCLR host without ALC — third-party plugins contribute backends +
global functions today, provided they align their dependency closure with the host (Apache.Arrow always). ALC
isolation is a non-breaking later upgrade to the loader, worth turning on only when a genuine dep conflict
appears; the must-fix for that day is the explicit shared-name allowlist in `Load` (the resolver would otherwise
isolate Apache.Arrow and break every Arrow-typed call).
