# Plugin services — replacing the ad-hoc seams with a resolvable service surface

> **Status: the LOCATOR is APPROVED and scoped (2026-09-02); `Fabricator.Common` is under
> analysis (§6). Nothing built yet.** Opened user-directed:
> *"we should improve the use of fabricator.bridge/abstraction functionalities in plugins. reflection hack is
> not so nice. something like a `GetService<IHttpClientxx>()` would be nice. in a similar way what dependency
> injection does. Maybe this way a plugin expose a singleton which could be used by another plugin."*
>
> This file is the working record so the analysis can continue across sessions. Everything in §1 is READ FROM
> THE TREE or MEASURED and dated; §2 onward is design space, not decisions.

## 1. What is actually there today (read 2026-09-02)

### 1.1 The two assemblies, and why a plugin references only one

| | references | what it holds |
|---|---|---|
| `Fabricator.Abstractions` | `Apache.Arrow` only | the 25 contract files: `IBackend`, the function interfaces, `ITable`, `ITransaction`, `ProviderSettings`, `DuckSql`, `ScanSpec`, `DuckDbHttpHandler`, and the two seams below |
| `Fabricator.Bridge` | Azure.Storage.Files.DataLake, Azure.Identity, Microsoft.Fabric.Api, Apache.Arrow | the ABI, the handle table, `Host`, `HostFs`, `ArrowValueReader`, `InterruptScope`, `FabricatorLog`, `MemoryProbe`, the registry |

**⚠⚠ THE CONSTRAINT IS NOT VISIBILITY — IT IS WEIGHT, and getting this wrong sends the design somewhere
useless.** `ArrowValueReader`, `Host`, `AmbientOpener`, `AmbientTransaction`, `InterruptScope`,
`FabricatorLog`, `MemoryProbe`, `InMemoryArrayStream`, `DbDataReaderArrowStream` are **all already `public`
in `Fabricator.Bridge`** (grepped). A plugin cannot use them because it does not *reference* Bridge, and it
does not reference Bridge because that would drag the Azure + Fabric package closure into every plugin
build. `Fabricator.Abstractions` exists to be the light contract.

⇒ **the shape the user asks for is the right one**: interfaces in Abstractions (light), implementations in
Bridge, resolution by interface.

⚠ `BackendRegistry`'s own comment is STALE about this — *"A plugin references Fabricator.Bridge +
Apache.Arrow (host-provided, not copied)"*. `Fabricator.FluidPlugin` references **Abstractions only**, and
`fabricator-sustainalytics` does the same. Fix that comment whenever this area is touched.

### 1.2 The seams that exist, and the fact that there were nearly three

Both are a `static` mutable delegate property on a static class in Abstractions, filled in by
`Bootstrap.Initialize`:

```csharp
HostHttpTransport.Send  : Func<string, string, string?, byte[]?, (string ResponseJson, byte[]? Body)>?
HostQueryTransport.Query: Func<string, RecordBatch?, IArrowArrayStream>?
```

Each carries `IsAvailable => X is not null`, and each documents the same three rules (read them before
designing anything — they are the real contract):

1. **The delegate carries no opener, deliberately.** The bridge's lambda reads the AMBIENT `ClientContext`
   per call. Anything holding an ATTACH-time `ClientContext *` is a dangling pointer the day that connection
   closes — the `table_stats` SIGSEGV class, paid for twice already.
2. It is therefore usable only from INSIDE an ABI crossing, or where the ambient still flows from one.
   `AsyncLocal`, so it survives `await` and `Task.Run`; it does NOT survive a thread parked before the
   crossing began.
3. `null` until the bridge boots.

**⚠ A THIRD SEAM WAS WRITTEN AND DELETED ON 2026-09-02** — `HostFileTransport.ReadAllBytes`, for the Fluid
template file provider. It was deleted because a GLOBAL SCALAR had no ambient opener, so every `fs_*` host
callback received a null `ClientContext` and the process died; the provider was rebuilt on `read_blob` over
`HostQueryTransport` instead (docs/fluid-templating.md §10). ABI v82 has since given the scalar crossings
their context, so the filesystem seam is now *buildable* — and that is the point: **the pattern has been
reached for three times in about two weeks, which is the argument for generalising it rather than adding a
fourth static class.**

### 1.3 The reflection, and which of it the user means

Three different things get called "reflection" around here; they are separate problems and only the first
two are in scope.

| where | what | in scope? |
|---|---|---|
| `BackendRegistry.ScanPluginDirectories` | `assembly.GetTypes()` + `typeof(IBackend).IsAssignableFrom` + `Activator.CreateInstance` — plugin DISCOVERY | partly: a service surface does not remove it, but see §4 |
| a plugin reaching Bridge internals | there is no mechanism, so today it either cannot, or it duplicates code (§1.4) | **yes — this is the ask** |
| `Bootstrap.FormatError` | `e.GetType().GetProperty("Number")` — duck-typing `SqlException.Number` so Bridge need not reference `Microsoft.Data.SqlClient` | **NO — different problem.** Its answer is already designed: an `IBackend.GetErrorNumber(Exception)` DIM (docs/aot-bridge.md). Do not conflate. |

⚠ Nearly every other `GetProperty(` hit in the tree is `JsonElement.TryGetProperty` — not reflection at all.
Do not count those.

### 1.4 What the absence costs today, concretely

- `Fabricator.FluidPlugin` carries a local `ArrowScalar.Read` — *"a deliberate superset of
  `ArrowValueReader.ReadScalar`"* — ~20 lines duplicating a Bridge type that is public but unreachable.
  `IScalarFunction`'s own doc admits the split: `ArrowValueReader` is available *"if a provider references
  the bridge"*.
- A plugin cannot log through `FabricatorLog` (so nothing it does appears in `duckdb_logs`), cannot use
  `InterruptScope` (so a long plugin operation ignores Ctrl+C), cannot read the host filesystem, cannot
  register a named Arrow source (`Host.RegisterSource`).
- `ProviderSettingsStore` IS reachable (it lives in Abstractions) — a useful precedent: the one piece of
  host state a plugin can already touch, because someone put it on the right side of the line.

### 1.5 Two facts that constrain every option

- **ALL PLUGINS LOAD INTO ONE ALC — the BRIDGE's, not Default** (`BackendRegistry`, with the reason: hostfxr
  loads the bridge into a non-default context, so a plugin must join it or its `IBackend` is a different,
  non-assignable type). Per-plugin ALC isolation is DEFERRED (docs/plugin-system.md). ⇒ cross-plugin type
  sharing *works today by accident of there being no isolation*, and would break the day isolation lands
  unless the shared interface lives in an assembly both sides reference.
- **`Fabricator.Abstractions` IS NOT VERSIONED OR PACKED.** Every assembly is `1.0.0.0`, there is no
  `PackageId`, it is not on NuGet, and out-of-tree plugins pin this repo BY SHA
  (`fabricator-sustainalytics`, `-quantax`, `-dlrest`). A mismatch surfaces as a `rejected` plugin row with
  a `TypeLoadException`. ⇒ **anything added to Abstractions widens a contract that has no version number**,
  and a service locator widens it a lot. See §5.

## 2. The shape the user described

```csharp
var http = FabricatorServices.Get<IHostHttp>();      // host capability
var log  = FabricatorServices.Get<IHostLog>();
var mine = FabricatorServices.Get<ISomeOtherPlugin>(); // another plugin's singleton
```

**⚠ `System.IServiceProvider` IS IN THE BCL** (`System`, since .NET 1.0: `object? GetService(Type)`), so the
locator shape needs NO new package — a `GetService<T>()` extension method over it is three lines. That
matters because a plugin's dependency closure is a live concern here (docs/plugin-system.md: the FluidPlugin
already has to `ExcludeAssets="runtime"` on Apache.Arrow to avoid handing the host a second copy).
`Microsoft.Extensions.DependencyInjection.Abstractions` would give `GetRequiredService<T>()` and a
container, at the cost of a package every plugin then aligns on — weigh, do not assume.

## 3. Design space (nothing chosen)

### 3.1 Host capabilities — interfaces in Abstractions, implementations in Bridge

The mechanical part, and the part with a clear answer. Candidates, each with why it is wanted:

| interface | wraps | why |
|---|---|---|
| `IHostQuery` | `Host.Query` / `ExecuteNonQuery` | already a seam; add the v83 `clientSession` |
| `IHostHttp` | `HostFs.HttpRequest` | already a seam; `DuckDbHttpHandler` is its consumer and already lives in Abstractions |
| `IHostFileSystem` | `HostFs` open/read/glob/write | the seam that was written and deleted; now buildable post-v82 |
| `IHostLog` | `FabricatorLog` | a plugin's diagnostics currently cannot reach `duckdb_logs` |
| ~~`IArrowValues`~~ | ~~`ArrowValueReader`~~ | **REMOVED from the list by §6's analysis — it needs no host state, so it wants to be REFERENCEABLE, not resolvable. It belongs in `Fabricator.Common`.** |
| `IHostSources` | `Host.RegisterSource` | lets a plugin publish a named Arrow source |
| `IInterruptScope` | `InterruptScope` | Ctrl+C during a long plugin operation |

**✅ APPROVED AND SCOPED (user, 2026-09-02): "i am fine for an easy solution with `GetService<T>()` which
works for the duckdb filesystem, http and host query/exec."**

**⚠⚠ AND THAT SCOPING DRAWS THE LINE THIS DESIGN NEEDED, which §3.1's first draft did not have.** The four
approved capabilities have one thing in common and it is not that they are useful: **they all need the
RUNNING HOST** — specifically the ambient `ClientContext`, for secrets, for the HTTP stack, for a
connection. A capability that needs no host state has no business being resolved at all; it should simply be
code a plugin can REFERENCE. That is §6.

    needs the live host  -> a SERVICE, resolved through the locator   (fs, http, query/exec)
    needs nothing        -> a LIBRARY, referenced directly            (Arrow helpers, SQL helpers)

⇒ `IArrowValues` came off the list above, and `IHostLog` survives for a reason worth stating: logging looks
like pure computation but `FabricatorLog` forwards into `duckdb_logs` through a host callback, and
`Fabricator.Bridge` references `Microsoft.Extensions.Logging` — so exposing it as an interface with primitive
parameters keeps MEL out of every plugin's closure, where MOVING the class would drag it in. A service, not
a library, and the dependency is why.

**⚠ RULE THAT MUST SURVIVE THE REFACTOR: a service instance MUST NOT capture the ambient.** The three
warnings in §1.2 exist because holding an opener is a use-after-free. A singleton `IHostQuery` is fine
*provided* every method reads the ambient at call time — which is what the current lambdas do. Say it on the
interface, not just in the implementation.

### 3.2 Where registration happens

- Host services: `Bootstrap.Initialize`, beside the existing seam fills. Trivial.
- Plugin services: `IBackend` gains a DIM, e.g. `void RegisterServices(IFabricatorServiceRegistry r)`, called
  during the plugin scan. Costs nothing for a plugin that publishes none.

### 3.3 ✅ DECIDED — plugin → plugin is the EXCEPTION, and a shared assembly is the answer

**User decision, 2026-09-02: *"plugin->plugin is the exception and creating a shared assembly is the better
solution"*.** ⇒ the locator's job is HOST → plugin, which needs **no new shipped artifact**. Two plugins that
genuinely need a contract between them ship a small assembly of their own and both reference it; the host
curates nothing.

That is the right split for a reason worth stating: the alternative — a first-party "plugin contracts"
assembly — would put the host in the business of knowing about every cross-plugin contract, which is exactly
the coupling a plugin system exists to avoid.

#### ⚠⚠ THE OPERATING RULE THE DECISION NEEDS, MEASURED 2026-09-02

Two plugin directories, each holding a copy of `SharedContracts.dll`, loaded into ONE `AssemblyLoadContext`
by `LoadFromAssemblyPath` — which is exactly what `ScanPluginDirectories` does, because a shared assembly is
subtracted OUT of the skip set (it is in `PluginLoaded`) and so is a candidate again in the second plugin's
directory:

| the two copies | what .NET does |
|---|---|
| **same identity** (same version) | **returns the ALREADY-LOADED assembly.** One copy in the ALC, `ReferenceEquals` true, SAME type identity, `IsAssignableFrom` both ways. It just works. |
| **different versions** | **`FileLoadException: Assembly with same name is already loaded`** |

⇒ **a shared contracts assembly must be VERSIONED and both plugins must ship the SAME version.** On a
mismatch the second copy is reported `rejected` in `fabricator_plugins()` (visible — good), but the
CONSEQUENCE is not: the second plugin still loads and silently binds to the FIRST one's version, so it fails
later at the first use of anything the older version lacks. Which version wins is decided by scan order,
i.e. sorted by path — a property of the install layout, not of either plugin.

⚠ This is the "aligned dependency closure" hazard docs/plugin-system.md already records for `Apache.Arrow`,
now measured for the cross-plugin case. The FluidPlugin's `ExcludeAssets="runtime"` on `Apache.Arrow` is the
same problem solved the same way.

**⚠ THE STRICTLY BETTER SHAPE, if a shared contract ever becomes common: ship it with the HOST.** An
assembly in the bridge payload is in `host.Assemblies` at scan time, lands in the skip set, and neither
plugin's copy is loaded at all — one identity, no collision, no ordering dependence. Nothing supports that
today (there is no "host-provided but not first-party" slot), and it is not needed while cross-plugin
sharing is the exception. Record it as the escape if that assumption stops holding.

⚠ **And it all rests on there being ONE ALC** (§1.5). Per-plugin ALC isolation, deferred, would give each
plugin its own copy of the shared assembly and non-assignable types — so if isolation is ever built, the
shared assembly must become host-provided or explicitly shared at that moment. Note it in whatever issue
tracks the isolation work; it is a consequence that will not be obvious from the isolation change itself.

### 3.4 "Locator vs Microsoft.Extensions.DependencyInjection" — they are not the same kind of thing

Asked directly (2026-09-02), and the question is worth untangling before answering, because the two names
sit on different axes:

- **Service locator** and **constructor injection** are PATTERNS. The first asks a registry at the point of
  use (`Services.Get<IHostHttp>()`); the second takes dependencies as constructor parameters and lets
  something else decide what to pass.
- **`Microsoft.Extensions.DependencyInjection`** is a LIBRARY — a container plus the
  `IServiceCollection` / `ServiceDescriptor` abstractions. It *supports* constructor injection, and
  `provider.GetRequiredService<T>()` is *itself* a locator call. So MEDI is not the opposite of a locator;
  it is one possible implementation of one, with a graph resolver attached.

⇒ the real choice is **(a) which pattern**, and **(b) hand-written registry or take the package**.

#### What MEDI would add, and whether we would use it

| MEDI feature | do we need it here? |
|---|---|
| recursive constructor injection | **no** — the candidate services (§3.1) have no dependency graph at all |
| lifetimes (singleton / scoped / transient) | **no** — every candidate is a stateless singleton, and the only "scope" that exists is the AMBIENT, an `AsyncLocal` the service reads per call, which no container models |
| disposal management | **no** — nothing to dispose; `InterruptScope` is a factory the caller already `using`s |
| `IEnumerable<T>`, keyed services, validate-on-build | not for this set |
| familiarity | **yes, genuinely** — every .NET developer knows the shape, and a plugin author seeing `IServiceProvider` needs no documentation |

Only the last row is a real benefit, and §3.4a shows it can be had without the package.

#### ⚠⚠ Two costs that are specific to THIS codebase

1. **`Fabricator.Abstractions` references `Apache.Arrow` and nothing else.** Adding
   `Microsoft.Extensions.DependencyInjection.Abstractions` makes it a second closure every plugin has to
   align on — and the FluidPlugin already carries `ExcludeAssets="runtime"` on Apache.Arrow for exactly that
   reason (docs/plugin-system.md). ⚠ It is *softer* than it looks: `GetRequiredService<T>` is a static
   extension over the BCL `IServiceProvider`, so a duplicated copy of that package resolves against itself
   and no type identity crosses. The cost lands only if the host ever exposes an `IServiceCollection` for
   plugins to register into — which §3.2's design does not.
2. **⚠⚠ A BUILT MEDI PROVIDER IS IMMUTABLE, AND OUR REGISTRY IS NOT — the decisive one.**
   `BuildServiceProvider()` is one-shot: there is no adding after the build. But
   `BackendRegistry.Invalidate()` nulls the memoized map so the NEXT access re-scans, which is what makes
   `fabricator_install_plugin` usable in the session that installs (docs/plugin-system.md, "the reload
   split"). A container would have to be rebuilt on every invalidate, and anything holding the previous
   provider would be silently stale. A mutable registry fits the lifecycle that already exists; an immutable
   one fights it.

#### 3.4a RECOMMENDED (not yet decided): the CONTRACT is BCL, the IMPLEMENTATION is ours

**`System.IServiceProvider` is in the BCL** (`System.Runtime`, since .NET 1.0: `object? GetService(Type)`).
So expose that as the contract, implement it with a dictionary keyed on `typeof(T)`, and ship a three-line
`GetService<T>()` extension in Abstractions.

That gets every real benefit and none of the costs: a plugin author sees a familiar BCL type; no package
enters anyone's closure; the registry stays mutable so `Invalidate()` keeps working; and it is trivially
AOT-safe where a reflection-based container is the thing docs/aot-bridge.md exists to remove. It is also not
a one-way door — if constructor injection is ever wanted for `IBackend`, MEDI can be swapped in *behind*
`IServiceProvider` without touching the contract or any plugin.

⚠ **Pattern-wise this is a locator, and that is a deliberate choice rather than an oversight.** Locators are
often criticised for hiding dependencies — fair in an application, much weaker here: a plugin is discovered
by reflection and instantiated with `Activator.CreateInstance(type)` (parameterless), so constructor
injection would change the discovery contract and every out-of-tree plugin's constructor. It can be added
later ON TOP; it cannot be un-added.

## 4. Interactions to check before building

- **AOT** (docs/aot-bridge.md): a `typeof(T)`-keyed locator is AOT-safe and strictly better than reflection,
  so this direction HELPS that plan. ⚠ But the AOT plan's five dynamic-code sites include the
  `BackendRegistry` reflection and `FormatError`'s duck-type — re-read it, because a service surface changes
  the shape of the source-generator answer it proposes.
- **`fabricator_plugins()`** already reports the scan per root with a status. A service registry should be
  visible the same way, or a mis-registration is silent — the exact fault that section of
  docs/plugin-system.md was written to end.
- **The three out-of-tree plugins** pin by sha and hand-write boilerplate; each migrates at its next pin
  bump. An additive locator breaks none of them.
- ⚠ **Do not let this become a reason to widen the FluidPlugin's reference to Bridge.** Its Abstractions-only
  reference is deliberate — it is what makes the in-tree example demonstrate the surface out-of-tree plugins
  actually have (CLAUDE.md). If the locator is right, the ~20-line duplicate disappears *without* widening
  the reference, which is the test of whether the design worked.

## 6. `Fabricator.Common` — the reusable middle (analysis opened 2026-09-02)

User-raised: *"we can also analyse the fabricator.bridge files to move some useful/reusable ones into a
Fabricator.Common. Fabricator.Common could then be the place for shared assemblies for plugins?"*

### 6.1 The inventory (measured 2026-09-02)

66 root files in `Fabricator.Bridge`. Classified by whether anything HEAVY appears in them — Azure, Fabric,
SqlClient, engineered-wood, `Apache.Arrow.C`, `unsafe`, or the ABI surface:

| | files | lines |
|---|---|---|
| BCL + `Apache.Arrow` only | **47** | **6208** |
| pinned to something heavy | 19 | ~6600 |

⚠ **The script mis-tagged two files and only reading them caught it**: `Bootstrap.cs` and `PluginPaths.cs`
matched `Microsoft.Data.SqlClient` in COMMENTS — Bridge has not referenced that package since the
2026-08-18 split. A dependency inventory by grep needs its hits read, not counted.

⚠ **Bridge's real package closure is `Apache.Arrow`, `Microsoft.Extensions.Logging`,
`Microsoft.Fabric.Api`, `Azure.Identity`, `Azure.Storage.Files.DataLake` + a ProjectReference to
`Fabricator.Installer.Core`.** `Fabricator.Abstractions` references `Apache.Arrow` and nothing else. That
asymmetry is the whole reason this analysis exists.

### 6.2 Dependency-light is NOT the same as plugin-useful

Of the 47 clean files, most are HOST INTERNALS that merely happen to need no heavy package — the binding
adapters, the metadata carriers, `CatalogFunctionSet`, the exchanges, `StubBackend`, `BackendRegistry`
itself. Moving those would grow a plugin-facing assembly with things no plugin can use.

The genuinely reusable set, each with the reason:

| candidate | lines | why a plugin wants it |
|---|---|---|
| `ArrowValueReader` | 112 | THE motivating case — the FluidPlugin duplicates it today |
| `InMemoryArrayStream` | 39 | an `IArrowArrayStream` over batches; anything returning data needs one |
| `AsyncEnumerableArrowStream` | 57 | the async form |
| `DescribedArrowStream` | 131 | declared-schema wrapper (the `RegisterSource` laziness fix) |
| `SingleScanArrowStream` | 141 | one-shot scan |
| `ChannelArrowStream` | 51 | producer/consumer streaming |
| `DbDataReaderArrowStream` | 183 | ADO.NET → Arrow, for any SQL-shaped plugin |
| `ArrowDataReader` | 211 | the reverse direction |
| `StaticTableFunction` / `StaticInOutFunction` / `StaticCollectorFunction` | ~207 | the AUTHOR-FACING base classes — a plugin writing a table function wants these, and they are documented as the authoring surface |
| `AggregateSession` | 371 | the UDAF helper |
| `SqlDdl`, `SqlGen` | 128 | SQL text helpers |
| `MemoryProbe` | 76 | already `public` *"so any backend assembly can use it"* |
| `ObjectNotFoundException` | 49 | ⚠ arguably belongs in **Abstractions**, not Common: every provider is REQUIRED to throw it to signal absence, so it is part of the contract |

≈ 15 files, ≈ 1800 lines. ⚠ Every entry needs its `internal` dependencies checked before moving — the
2026-08-18 `Fabricator.Delta` split found the first closure attempt proposed moving two types that three
sibling assemblies used, because it was computed over one project instead of all consumers. **Same method
here: enumerate the types the candidate set DECLARES, then grep every project for those.**

### 6.3 It also lets `Fabricator.Abstractions` become what its name says

Abstractions already holds two things that are not abstractions: `DuckSql` (a quoting helper) and
`DuckDbHttpHandler` (a full `HttpMessageHandler`). A `Common` gives them a home and leaves Abstractions as
interfaces and data contracts only — which matters because Abstractions is the thing out-of-tree plugins pin
BY SHA (§1.5): the smaller and more stable it is, the less a pin bump can mean.

Proposed layering, each arrow a reference:

    Fabricator.Abstractions  (interfaces + data contracts; Apache.Arrow only)
        ^
    Fabricator.Common        (dependency-light reusable implementations)
        ^
    Fabricator.Bridge        (ABI, host, Azure/Fabric/unsafe)

A plugin references Abstractions (required) and Common (optional).

### 6.4 ⚠⚠ "the place for shared assemblies for plugins?" — YES for code, NO for cross-plugin contracts

The question deserves splitting, because the two readings have opposite answers:

- **Reusable CODE that many plugins want** — yes, that is exactly what Common is for.
- **A CONTRACT between two specific plugins** — no. §3.3 settled that (user: cross-plugin is the exception,
  the plugins ship their own shared assembly), and putting it in Common would recreate what that decision
  rejected: the host curating contracts it has no stake in.

**⚠ But Common does give the cross-plugin case something real, and it is not a place to put types.** Because
Common ships with the host, it is in `host.Assemblies` at plugin-scan time, so it lands in the skip set and
no plugin's copy is ever loaded — the version-collision measured in §3.3 cannot happen to it. That makes it
the natural home for the **locator plumbing** through which plugins find each other, while the TYPES they
exchange stay theirs. Shared mechanism, private contracts.

⚠ It is also the answer if the assumption in §3.3 ever breaks: if cross-plugin contracts stop being the
exception, a host-shipped assembly is the shape that fixes the collision, and Common already is one.

### 6.5 What is NOT settled

1. Is a third assembly worth it, or should the reusable set simply go INTO Abstractions? The case for
   splitting is that Abstractions is sha-pinned and should stay small; the case against is one more
   assembly in the payload, the publish script, and every plugin's csproj. **§6.3's tidying of
   `DuckSql`/`DuckDbHttpHandler` only pays off if the answer is "separate".**
2. Does `Fabricator.Common` need its own `net8.0`-only target, like the plugins? A plugin runs on whatever
   the bridge was published for, and Abstractions multi-targets — check before assuming.
3. `FabricatorLog` stays in Bridge on the MEL argument (§3.1). Revisit only if MEL is wanted in the plugin
   closure for other reasons.

## 5. Open questions

1. ~~Is **cross-plugin** sharing in scope?~~ **ANSWERED 2026-09-02 (user): it is the EXCEPTION, and a
   shared assembly the plugins own is the answer — so the host ships nothing new. See §3.3, including the
   measured version rule.**
2. ~~`System.IServiceProvider` + our own extensions, or take MEDI?~~ **ANALYSED in §3.4; RECOMMENDED:
   BCL `IServiceProvider` as the contract, our own dictionary as the implementation. Awaiting the
   user's decision.** The deciding facts are that there is no dependency graph to resolve, and that a
   built MEDI provider is IMMUTABLE while `BackendRegistry.Invalidate()` re-scans.
3. Does the locator REPLACE `HostHttpTransport` / `HostQueryTransport`, or wrap them? Replacing is cleaner
   and is a BREAKING change for out-of-tree plugins — which this repo has done before without aliases (the
   `IArrow*` renames, `ScalarFnBind`), so it is a decision, not an obstacle.
4. Should `Fabricator.Abstractions` finally be PACKED and versioned? (§1.5.) A bigger contract surface makes
   the sha-pin more load-bearing, and this is the natural moment to ask.
5. Scoping: are all services singletons, or is there a per-call/per-transaction scope? The ambient rule
   (§3.1) means a singleton is safe for the current set — but a service that *did* need per-call state
   (an `IInterruptScope`) is a factory, not a singleton.
6. What does a plugin do when a service is absent — `null`, throw, or a no-op implementation? The existing
   seams expose `IsAvailable` and the callers refuse by name; a locator should not quietly lose that.
