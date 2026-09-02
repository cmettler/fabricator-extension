# Plugin services — replacing the ad-hoc seams with a resolvable service surface

> **Status: BOTH STEPS BUILT — §8 (the `GetService<T>()` locator), §9 (`Fabricator.Common`), §10
> (`IHostLog`, §7.4a's third question).**
> ⚠ §9.4, §9.5 and §10.4 CORRECT the plan and this build: the acceptance test's target no longer existed,
> §7.4a's "13 → 16" is wrong on two of three, and a defensive mechanism in `IHostLog` turned out to be
> unnecessary — its mutant survived. Opened user-directed:
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

⚠ ~~`BackendRegistry`'s own comment is STALE about this~~ — it claimed *"A plugin references
Fabricator.Bridge + Apache.Arrow (host-provided, not copied)"* while `Fabricator.FluidPlugin` and
`fabricator-sustainalytics` both reference **Abstractions only**. **FIXED 2026-09-02** with §8.

### 1.2 The seams that exist(ed), and the fact that there were nearly three

**⚠ SUPERSEDED BY §8 — both seams are DELETED.** Read this section anyway: the three rules below ARE the
real contract, and §8 preserves them verbatim on the services that replaced the delegates. What changed is
the mechanism, not the rules.

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

**⚠⚠ CORRECTION TO AN EARLIER DRAFT OF THIS SECTION, caught while planning the build: THE REGISTRY CANNOT
LIVE IN COMMON.** This section first said Common was "the natural home for the locator plumbing". It is not:
Common references Abstractions and not the reverse, so a registry there would make **Common mandatory for
any plugin that wants to resolve a host service** — which contradicts the whole point of Common being the
OPTIONAL half. The registry is tiny (a dictionary and an `IServiceProvider`), so it belongs in
**Abstractions**, and Common stays optional. See §7.2.

**⚠ What Common does still give the cross-plugin case is real, and it is not a place to put types.** Because
Common ships with the host, it is in `host.Assemblies` at plugin-scan time, so it lands in the skip set and
no plugin's copy is ever loaded — the version-collision measured in §3.3 cannot happen to it. That makes it
the natural home for the **locator plumbing** through which plugins find each other, while the TYPES they
exchange stay theirs. Shared mechanism, private contracts.

⚠ It is also the answer if the assumption in §3.3 ever breaks: if cross-plugin contracts stop being the
exception, a host-shipped assembly is the shape that fixes the collision, and Common already is one.

### 6.5 What is NOT settled

1. ~~Is a third assembly worth it?~~ **ANSWERED 2026-09-02 (user): "one more assembly in the payload is
   no problem." `Fabricator.Common` is approved — see §7.**
2. Does `Fabricator.Common` need its own `net8.0`-only target, like the plugins? A plugin runs on whatever
   the bridge was published for, and Abstractions multi-targets — check before assuming.
3. `FabricatorLog` stays in Bridge on the MEL argument (§3.1). Revisit only if MEL is wanted in the plugin
   closure for other reasons.

## 7. IMPLEMENTATION PLAN (approved 2026-09-02; start here)

### 7.1 What is decided

| | decision |
|---|---|
| the locator | **APPROVED**, scoped to the DuckDB **filesystem**, **HTTP**, and **host query/exec** |
| pattern | service LOCATOR, not constructor injection (§3.4) |
| contract type | **`System.IServiceProvider`** (BCL) + our own `Get<T>()` extension — no MEDI package (§3.4a) |
| `Fabricator.Common` | **APPROVED** — user: *"one more assembly in the payload is no problem"* |
| cross-plugin contracts | the plugins' own shared assembly, not ours (§3.3) |

### 7.2 ⚠ Where each piece lives — and the registry is NOT in Common

    Fabricator.Abstractions   interfaces + data contracts + THE REGISTRY      (Apache.Arrow only)
        ^
    Fabricator.Common         dependency-light reusable implementations       (optional for a plugin)
        ^
    Fabricator.Bridge         ABI, host, Azure/Fabric/unsafe                  (never referenced by a plugin)

The registry goes in **Abstractions** because Common references Abstractions and not the reverse: putting it
in Common would make Common mandatory for anyone resolving a host service, and Common's whole point is being
optional. It is a dictionary and an `IServiceProvider`; it does not need a home of its own.

### 7.3 STEP 1 — the locator

**New in `Fabricator.Abstractions`:**

- `FabricatorServices.cs` — a `ConcurrentDictionary<Type, object>` behind
  `Register<T>(T)` / `Get<T>()` / `GetRequired<T>()`, exposed additionally as `System.IServiceProvider` so a
  plugin author can hold the BCL type. Mutable by design: `BackendRegistry.Invalidate()` re-scans, and an
  immutable built container would fight that (§3.4).
- `IHostFileSystem` — read-all + glob for v1. ⚠ Scope it to what is demonstrably needed and say so; the
  deleted `HostFileTransport` was `ReadAllBytes(path, maxBytes)` and that ceiling parameter is worth keeping
  (it is what stops a wrong path buffering a multi-gigabyte object).
- `IHostHttp` — the shape `HostHttpTransport.Send` already has (JSON envelope + body bytes). It is not
  pretty, but `DuckDbHttpHandler` already consumes exactly that and it is proven; do not redesign it in the
  same change.
- `IHostQuery` — `Query(sql, parameters?, inheritSession?)` + `ExecuteNonQuery(sql)`. ⚠ Expose the ABI v83
  session choice: the transport currently passes the ambient unconditionally, and the interface should let a
  caller ask for a clean session.

**In `Fabricator.Bridge`:** implementations over `HostFs` / `Host`, registered in `Bootstrap.Initialize`
beside the existing seam fills. **⚠ Every implementation reads the ambient PER CALL and captures nothing** —
§3.1's rule, and the reason the three existing seams each carry the same warning.

**Migration:** delete `HostHttpTransport` and `HostQueryTransport`. Breaking for out-of-tree plugins, which
this repo does without aliases (the `IArrow*` renames, `ScalarFnBind`). Update `DuckDbHttpHandler` and the
FluidPlugin's `FluidHostQuery`.

**Gate:** `verify_plugin_fluid` already exercises `query()` end to end through the transport, so it becomes
the locator's regression test for free. ⚠ **`IHostFileSystem` would have NO consumer and therefore no gate**
— decide before building: either add a small `IBackend` in `Fabricator.SamplePlugin` that reads a file
through it (the natural home for "prove the plugin surface works"), or do not ship that interface yet.
**Do not** switch the Fluid template provider back onto the filesystem to manufacture a consumer: §10 of
fluid-templating.md gives four measured reasons `read_blob` is better there (zero rows establishes absence,
plus `size`, `last_modified`, and a bound parameter).

### 7.4 STEP 2 — `Fabricator.Common`

> **BUILT 2026-09-02 — read §9 FIRST.** Two things below did not survive contact: the acceptance test's
> target no longer exists and what replaced it is not substitutable (§9.4), and four of the thirteen movers
> were deliberately left behind (§9.3). The plan is kept as written so the corrections have something to
> correct.

**New project** `dotnet/Fabricator.Common/Fabricator.Common.csproj`, referencing Abstractions. ⚠ It needs no
`TargetFramework` line — `dotnet/Directory.Build.props` sets `net10.0;net8.0` for everything. ⚠ It needs no
`publish-managed.ps1` line either: Abstractions reaches the payload transitively as a ProjectReference, and
Common will the same way (VERIFIED: `Fabricator.Abstractions.dll` is in the payload with no script entry).

**Keep the `Fabricator.Bridge` NAMESPACE.** That is the established convention for an assembly split here —
`Fabricator.Abstractions` and `Fabricator.Delta` both do it — and it is what makes the move cost ZERO `using`
churn across six projects.

**MOVES CLEANLY (13, closure-checked 2026-09-02):** `ArrowValueReader`, `InMemoryArrayStream`,
`AsyncEnumerableArrowStream`, `DescribedArrowStream`, `ChannelArrowStream`, `ArrowDataReader`,
`AggregateSession`, `SqlDdl`, `SqlGen`, `ObjectNotFoundException`, `StaticTableFunction`,
`StaticInOutFunction`, `StaticCollectorFunction`.

**⚠ THE CLOSURE CHECK PRODUCED THREE FALSE POSITIVES AND ONE REAL BLOCKER — read both.**

- **FALSE POSITIVE:** the three `Static*Function` files "depend on" a type `Binding` declared in
  `DeltaCatalogInfoFunction.cs`. They do not — each declares its OWN `private sealed class Binding`. This is
  the exact trap CLAUDE.md records from the `Fabricator.Delta` split ("private nested helper names collide
  across dozens of files"), hit again by a script that excluded `Handle`/`State`/`Entry` but not `Binding`.
  **A name-based closure check must be confirmed by reading each hit.**
- **REAL BLOCKER, and all three share it:** `DbDataReaderArrowStream`, `SingleScanArrowStream` and
  `MemoryProbe` each do `FabricatorLog.CreateLogger(...)`, and `DbDataReaderArrowStream` also takes an
  `InterruptScope`. ⇒ **DEFER them to a phase 2**, whose only real question is whether
  `Microsoft.Extensions.Logging.Abstractions` (where `ILogger` lives — light) is acceptable in a plugin's
  closure. ⚠ Bridge references the FULL `Microsoft.Extensions.Logging` 9.0.0, so check which half
  `FabricatorLog` actually needs before moving it.

**Also consider moving OUT of Abstractions** (§6.3): `DuckSql` and `DuckDbHttpHandler` are implementations,
not abstractions. ⚠ Breaking — the FluidPlugin uses `DuckSql` in two places and out-of-tree plugins may too;
they gain a Common reference. Worth it only if Abstractions is meant to stay purely contracts, which is the
argument for having Common at all.

**Acceptance test, and it is the whole point:** delete the FluidPlugin's local `ArrowScalar.Read` duplicate
and have it use `ArrowValueReader` — **without widening its reference to `Fabricator.Bridge`.** If that is
not possible, the split did not achieve what it was for.

⚠⚠ **SUPERSEDED — `ArrowScalar` no longer exists, and its successor (`FluidValueModel.ReadCell`) is a
documented SUPERSET whose substitution would revert three gated behaviours. The property is intact and was
checked against `ReadTimestamp` instead: §9.4.**

### 7.4a DECISIONS on the three open sub-questions (user, 2026-09-02)

**1. `DuckSql` / `DuckDbHttpHandler` STAY IN ABSTRACTIONS.** User: *"i think it doesn't matter today where
DuckSql/DuckDbHttpHandler live."* Agreed and settled: moving them is a BREAKING change for out-of-tree
plugins (the FluidPlugin alone uses `DuckSql` in two places, and the three out-of-tree ones may) in exchange
for a naming purity nobody has asked for. Reversible later at the same cost; §6.3's tidying argument is
noted and declined for now.

**2. THE MOVERS BECOME `public`, AND NO `InternalsVisibleTo` IS NEEDED — but the second half of the question
has the direction inverted, and the inversion matters.** User: *"if we move stuff to common then internal
does not make much sense as the purpose is to reuse? so InternalsVisibleTo not needed? make public but
bridges internals must be visible in common?"*

The first two clauses are right: a type moved to Common **in order to be reusable** must be `public`, or the
move achieves nothing, and once it is public Bridge needs no `InternalsVisibleTo` to keep using it.

**⚠⚠ The third clause is impossible, not merely unnecessary: COMMON CAN NEVER SEE BRIDGE.** Bridge
references Common, so a reference back would be a project CYCLE — and `InternalsVisibleTo` cannot help,
because it grants access to code that already has a reference. So:

| direction | answer |
|---|---|
| Bridge → Common internals | unnecessary; make the movers `public` |
| Common → Bridge internals | **IMPOSSIBLE (cycle)** — a file needing one CANNOT MOVE |

⇒ **that impossibility IS the closure check's criterion**, and it is exactly why the three
`FabricatorLog` users were deferred rather than moved with a visibility patch. There is no
`InternalsVisibleTo` that would have rescued them. Anything a mover needs from Bridge must either move with
it, become a SERVICE, or stop the move.

⚠ **One cost to take deliberately rather than by reflex: making 13 types `public` widens a contract that has
no version number** — out-of-tree plugins pin this repo BY SHA (§1.5), so each newly public type is one more
thing a pin bump can break. Move what is genuinely wanted, not all 13 because the list exists.

⚠ **`ObjectNotFoundException` should go to ABSTRACTIONS, not Common** (§6.2 flagged this and it is now a
decision): every provider is REQUIRED to throw it to signal absence, so it is part of the provider CONTRACT
rather than a reusable convenience.

**3. ✅ BUILT — §10. `FabricatorLog` IS A SERVICE (`IHostLog`), NOT A MOVER — and answering this DISSOLVES the phase-2
deferral rather than deciding a trade-off.** User: *"FabricatorLog into common or as a GetService<>()?"*

Two reasons, the first decisive and the second merely expensive:

- **It needs the RUNNING HOST.** What a plugin wants from it is reaching `duckdb_logs`, and that happens
  through the `host_log` ABI callback (`FabricatorLog.EnableHostForwarding`). That is §3.1's dividing line —
  needs host state ⇒ SERVICE. (It also has a host-independent file sink via `FABRICATOR_LOG_LEVEL`/`_FILE`,
  but that is not the half a plugin is asking for.)
- **Moving it drags `Microsoft.Extensions.Logging` into every plugin's compile closure**, and the two are not
  separable: its whole public surface is MEL types (`ILoggerFactory`, `ILogger`, `ILoggerProvider`). An
  interface with primitive parameters keeps MEL out.

**⚠⚠ THE CONSTRAINT THAT MUST SURVIVE INTO THE INTERFACE: `IHostLog` MUST EXPOSE `IsEnabled(level)`.**
`MemoryProbe` gates every mark on `Log.IsEnabled(LogLevel.Debug)` and CLAUDE.md is emphatic that it must stay
that way, because `Environment.WorkingSet` queries OS process counters and some marks sit in per-group loops.
An `IHostLog` with only `Log(level, category, message)` would force either losing that gate or computing the
values eagerly — the exact regression the gate exists to prevent.

**⇒ WITH `IHostLog`, THE DEFERRED THREE BECOME MOVABLE AND THE MOVER LIST GOES 13 → 16.**

⚠⚠ **MEASURED WRONG ON TWO OF THE THREE — the real number is 13 → 14, and `IHostLog` therefore has to be
justified as a CAPABILITY rather than as an unblocker. `InterruptScope` reaches `HostFs`, so
`DbDataReaderArrowStream` is blocked transitively, and `SingleScanArrowStream` shares a file with a
`Host.Query` caller. §9.5.**

`DbDataReaderArrowStream`, `SingleScanArrowStream` and `MemoryProbe` need MEL for nothing else — read from
source, each holds one `static readonly ILogger` and uses `IsEnabled` plus one log call. So "phase 2" is not
a second migration; it is *add `IHostLog` first*. ⚠ `DbDataReaderArrowStream` additionally takes an
`InterruptScope`, so it needs that resolved too (either as a mover or as a second service) — check before
counting it in.

⚠ **Formatting moves caller-side and nothing is lost by it, which is worth stating because it looks like a
regression.** Those three use structured logging (`LogDebug("mem {Where}: ws={Ws}MB…", …)`), and an
`IHostLog` with primitive parameters means interpolating first. Both existing sinks are message-string based
anyway — the host callback is `(int level, string category, string message)` and `FileLoggerProvider.Write`
takes a line — so no structured field survives today either.

### 7.5 Order, and how each step is proven

**Locator first, Common second.** They are independent, but the locator is the approved capability and is
self-contained, while Common is a large mechanical move whose acceptance test (the duplicate disappearing)
is cleaner once nothing else is in flight.

| step | proof |
|---|---|
| locator ✅ **DONE, §8** | `verify_plugin_fluid` green (it drives `query()` through the new service); a gate for `IHostFileSystem` per §7.3, or the interface does not ship |
| Common ✅ **DONE, §9** | **both tiers at IDENTICAL counts** — a pure move changes no answer — plus the **masking check**: strip the moved files from `git diff -U0` and every removed line must be byte-identical to its added counterpart |

⚠ Publish with **`-Clean`** if any PackageReference moves between assemblies. That rule exists because a
publish once silently deleted all five SqlClient DLLs from the payload, and it is invisible to the hermetic
tier.

### 7.6 Hazards specific to this work

1. **⚠⚠ A plugin's reference to Common must be `Private="false"`, exactly like Abstractions.** A copied
   `Fabricator.Common.dll` beside a plugin is a second copy of every type in it — the "aligned dependency
   closure" hazard, and the FluidPlugin already carries `ExcludeAssets="runtime"` on Apache.Arrow for the
   same reason.
2. **⚠ Deleting the two transports is a contract break for three out-of-tree plugins** (`-sustainalytics`,
   `-quantax`, `-dlrest`), which pin by sha and migrate at their next bump. Fine, but say so in the commit.
3. **⚠ `BackendRegistry`'s comment is stale** — it claims a plugin references `Fabricator.Bridge`. Fix it
   while in there (§1.1).
4. **⚠ Registration ORDER for cross-plugin services:** the plugin scan is sorted by path, so a plugin
   resolving another's service at LOAD time may run first. Resolution must be LAZY — at use, not at load —
   and the docs should say so before anyone builds a cross-plugin dependency on it.
5. **⚠ A missing service must not be silent** (§5 Q6, still open): the existing seams expose `IsAvailable`
   and callers refuse BY NAME. `Get<T>()` returning null loses that unless `GetRequired<T>()` throws with
   the interface name in the message.

## 8. STEP 1 AS BUILT (2026-09-02) — the locator

C#-only. **NO ABI change, NO C++ change.** Gate `verify_plugin` **97 → 112**; `verify_plugin_fluid` **188**
and `verify_http_transport` **21**, both UNCHANGED, which is the behaviour-neutrality claim for the two
capabilities that already had callers. Tiers: hermetic **74/74 — 8259**, IDENTICAL to the previous floor, and
service **54/54 — 3272** = 3257 + exactly this suite's 15, which is what shows no other suite moved.

### 8.1 What shipped

| | |
|---|---|
| `Fabricator.Abstractions/FabricatorServices.cs` | the registry: `Register<T>` / `Get<T>` / `GetRequired<T>` / `IsAvailable<T>`, plus `Provider` as a BCL `System.IServiceProvider` |
| `Fabricator.Abstractions/IHostFileSystem.cs` | `ReadAllBytes(path, maxBytes)` + `Glob(pattern)`, with `HostFileEntry(Path, Size)` |
| `Fabricator.Abstractions/IHostHttp.cs` | `Send(method, url, headersJson, body)` — the shape `DuckDbHttpHandler` already consumed |
| `Fabricator.Abstractions/IHostQuery.cs` | `Query(sql, parameters?, inheritSession?)` + `ExecuteNonQuery(sql)` |
| `Fabricator.Bridge/HostServices.cs` | the three implementations + `Publish()` |
| *(later the same day)* `IHostLog.cs` | a FOURTH service — §10 |
| DELETED | `HostHttpTransport.cs`, `HostQueryTransport.cs` |

Consumers updated: `DuckDbHttpHandler` (in Abstractions), the Fluid plugin's `FluidHostQuery` and
`FluidTemplateFiles`. ⚠ **Breaking for the three out-of-tree plugins** (`-sustainalytics`, `-quantax`,
`-dlrest`), which pin by sha and migrate at their next bump — this repo does not keep aliases (the `IArrow*`
renames, `ScalarFnBind`).

✅ **THE `GetService<T>()` EXTENSION OVER `IServiceProvider` IS NOW SHIPPED** (2026-09-02, user-asked;
`FabricatorServiceProviderExtensions` — `GetService<T>()` and `GetRequiredService<T>()`). The primary API is
still `FabricatorServices.Get<T>()`; these exist for the case the locator was shaped around — handing
`Provider` to code that wants an `IServiceProvider` and expects the familiar generic call on it. They work on
ANY provider, not only ours, which is why `GetRequiredService<T>`'s message says nothing about the bridge.
See §8.5.

### 8.2 ⚠⚠ `IHostFileSystem` SHIPS, and what decided it was ABI v82 rather than taste

§7.3 left this open: the interface would have had no consumer and therefore no gate. It ships **with** a
gate, and the reason it is now shippable at all is that **the ambient gap that killed the last attempt is
closed**. Slice 4 of the Fluid work built a `HostFileTransport` to exactly the HTTP seam's shape and the
first include died with `0xC0000005` inside `HostFs.OpenRead`, because every `fs_*` host callback
dereferences the calling operator's `ClientContext` and a GLOBAL function had none
(docs/fluid-templating.md §10.2). ABI v82 gives the scalar crossings `(opener, session, txn)`, so a
plugin's global scalar now HAS one.

⇒ the gate is `plug_read_file(path)` and `plug_glob_count(pattern)` in `Fabricator.SamplePlugin`, and it is
**the first in-tree proof that the v82 ambient reaches a PLUGIN** — a plugin reading a file through DuckDB's
own filesystem is a claim about two mechanisms at once, and neither had a test before.

⚠ **What the gate does NOT cover, said plainly rather than implied:** the interesting half of routing through
the host is that the same call reaches `s3://` or `abfss://` with the CALLING SESSION's secrets, and no
hermetic fixture has a remote root. The suite asserts local reads and says so.

⚠ **The surface is deliberately two members.** A filesystem interface is easy to widen and impossible to
narrow, and an unused member on a plugin-facing contract is a compatibility obligation bought for nothing.
Streaming, writing and directory manipulation are all reachable inside the bridge and are not exposed.

⚠ **`read_blob` through `IHostQuery` remains the better tool where ABSENCE is an ordinary outcome** — it
returns zero rows rather than throwing, and reports `size` and `last_modified` besides. That is why the Fluid
template provider was NOT switched onto the filesystem now that one exists (docs/fluid-templating.md §10.3
gives all four measured reasons). `IHostFileSystem` is for a path you expect to exist.

### 8.2a ⚠ `IHostQuery.ExecuteNonQuery` IS UNGATED, and the suite does not pretend otherwise

The member is in scope (the user's words were "host query/**exec**") and `Host.ExecuteNonQuery` behind it is
long-standing and used internally — but **no in-tree plugin calls it**, so nothing exercises it through the
locator. The Fluid `query()` filter cannot be the gate: it REFUSES anything that is not a SELECT, by design
(§8.3 of fluid-templating.md — a bind-time write fires on `EXPLAIN`).

⇒ It is one sample-plugin scalar away from covered — the same shape `plug_read_file` took — and that is the
natural follow-up. It is recorded here rather than quietly shipped as if the `IHostQuery` gate covered both
members: `verify_plugin_fluid`'s 188 assertions prove `Query`, and say nothing about `ExecuteNonQuery`.

⚠ Adding that scalar needs a managed publish, which is exactly what §8.4 is blocked on.

### 8.3 Four things building it established

1. **`HostFs` had no read-all**, so one was added THERE — the unsafe wrapper class is its home, and putting
   it in the service would have made `HostServices.cs` unsafe for one method. ⚠ The ceiling is checked
   against the file's SIZE **before** a byte is read, so an oversized file costs an open and a stat rather
   than the memory, and it FAILS rather than truncating: a truncated document is a wrong answer that looks
   like a right one.
2. **⚠ `Array.Empty<byte>()` DOES NOT COMPILE inside `Fabricator.Bridge`** — `Apache.Arrow.Array` and
   `System.Array` are both in scope (CS0104). Loud, immediate, and worth knowing before writing the next
   file there.
3. **`Publish()` registers only what the host supports.** A capability the host did not register is absent
   from the registry, so `GetRequired<T>()` names the interface. Registering an implementation that always
   throws would make *"the host cannot do this"* indistinguishable from *"the call was wrong"*.
4. **The glob JSON is parsed in the BRIDGE, not handed to the plugin.** A plugin gets `HostFileEntry`, never
   the host's wire format. ⚠ `Size` is **-1** when the listing carries none, not 0: a local filesystem
   reports no size (DuckDB's `FileSystem` has no path-stat, so a size there costs an OPEN — the finding that
   once made a lakehouse ATTACH take minutes), and a 0 would read as "empty file".

### 8.3a ✅ MUTATION-TESTED — the gate IS an ABI-v82-ambient test, and it dies with the predicted sentence

One mutant, killed at its own assertion. In `HostFileSystemService.ReadAllBytes`, pass `0` instead of
`AmbientOpener.Current`:

| leg | result |
|---|---|
| control (clean) | `verify_plugin` **112** |
| mutant (ambient dropped) | **dies at line 519**, the first `plug_read_file` assertion, after 100 pass |

**The message is the point, and it was PREDICTED before the run rather than read off afterwards:**
`IO Error: … scalarfn_execute failed: host fs_open_read failed: … "fabricator: fs_open_read requires a
client context (no ambient opener)"`. So the gate does not merely read a file — it reads it *through the
caller's `ClientContext`*, which is what makes it a proof about ABI v82 and not a coincidence.

⚠ **Both legs were published to a SCRATCH `-ExtensionDir`**, leaving the tier-measured payload untouched —
the shared `build/release` payload is contended (another session's `unittest.exe` has raced it before), and
a mutant published over it would invalidate the very counts it is being compared against.

⚠ **It also gives the `fs_*` null-opener guard its first demonstrated reachable path.** That guard was added
the same day and recorded as UNGATED, because nothing in tree calls an `fs_*` callback without an ambient.
It still is, in normal operation — but the mutant shows it converting what used to be an access violation
into a sentence naming the missing context, which is the behaviour it was written for.

⚠ **The control also settles that the SDK move is behaviour-neutral HERE**: the clean leg was published by
SDK **10.0.400** (the tiers were measured on a **10.0.203** payload) and answers the same 112. That is one
suite, not a tier — see §8.4.

### 8.5 `GetService<T>()` over `IServiceProvider` (2026-09-02)

`FabricatorServiceProviderExtensions` in Abstractions: `GetService<T>()` → `T?`, `GetRequiredService<T>()` →
throws naming the interface. Two extension methods, no new dependency.

**⚠⚠ THE NAMES ARE DELIBERATELY MEDI's, AND THAT MEANS A PLUGIN REFERENCING MEDI TOO SEES AN AMBIGUITY
(CS0121) if it imports both namespaces.** That is the right trade and the right failure mode, for three
reasons: familiarity is the ONLY thing MEDI was wanted for here (§3.4), a plugin that references MEDI
*already has* these methods so ours are redundant for it, and the compiler saying so — with a one-line fix,
dropping one `using` — beats inventing a second vocabulary nobody knows. It is a COMPILE error, never a
silent wrong resolution.

⚠ **MEASURED that it reaches no in-tree project**: all seven assemblies build with zero errors, and the
reason is checkable rather than inferred — **no file in `dotnet/` imports
`Microsoft.Extensions.DependencyInjection` at all** (grepped), so the two namespaces are never in scope
together. ⚠ MEDI *is* in the published payload transitively, so the collision is reachable the day someone
imports it.

**⚠ GATED WITHOUT A NEW ASSERTION, by construction rather than by a test written to cover it.** The sample
plugin's two service-backed scalars now resolve by DIFFERENT routes — `plug_read_file` through
`FabricatorServices.GetRequired<T>()`, `plug_glob_count` through
`FabricatorServices.Provider.GetRequiredService<T>()` — so each existing assertion gates one route.
Mutation-tested: making the extension resolve nothing kills `plug_glob_count` **after both
`plug_read_file` assertions pass**, which is what demonstrates the two routes are independent rather than
one wrapping the other.

⚠ `verify_plugin` stays at **112** — the same assertions, one of them now flowing through the new path. A
count that did not move is the honest outcome for a change that adds no answer; what stands behind it is the
mutant and the tier at its exact prior total.

### 8.4 ⚠ THE MUTATION TEST WAS BLOCKED FOR AN HOUR BY A VISUAL STUDIO UPDATE IN FLIGHT — and my first
diagnosis named the wrong cause

**Kept because the CORRECTION is the useful part.** Mid-session, between a successful publish and the next
one, publishing began failing with `NETSDK1045: The current .NET SDK does not support targeting .NET 10.0`.
The observations were right — `dotnet --list-sdks` reported only 9.0.313/9.0.317, `host/fxr` had no 10.x,
and `sdk/10.0.203` was an empty leftover holding one `Roslyn` folder. **The CAUSE I wrote down was wrong:**
I recorded it as "the .NET 10 SDK was uninstalled from this machine", which implies somebody must reinstall
it. **A VISUAL STUDIO UPDATE WAS RUNNING IN THE BACKGROUND** (user-supplied), and I had caught the window
between it removing 10.0.203 and installing its replacement. Forty minutes later:
`10.0.400` present, `host/fxr` carrying `10.0.11`.

⇒ **the transferable rule: a build environment that changes UNDER a session is more likely mid-update than
mid-uninstall, and the two call for opposite responses** — wait, versus escalate. The tell was available and
I did not read it: an "uninstall" that leaves a version-numbered `Roslyn` folder behind is a partially
completed *replacement*, not a removal.

⚠ **THE SDK VERSION MOVED (10.0.203 → 10.0.400) AND THAT IS NOT NEUTRAL FOR MEASUREMENT.** A payload
published after the update is built by a different compiler and ships a different runtime pack than one
published before it. So a mutation test spanning the change would differ in TWO variables. Both legs —
mutant and control — must be published on the SAME SDK, and a green result carried over from before the
update is a result about a payload that no longer exists.

⚠ **The other half of a VS update: the MSVC toolset can move too.** `build/release/CMakeCache.txt` pins
`CMAKE_CXX_COMPILER` to an exact toolset directory, and CLAUDE.md records that linking against a *different*
toolset than the configure used fails with unresolved `__std_*` STL intrinsics. Nothing here needed a C++
rebuild, so it was not hit — but check that path exists before the next one.

✅ **AND THE MUTANT HAS SINCE RUN — §8.3a.** The SDK came back as 10.0.400 within the hour, both legs were
published on it, and the mutant died at its own assertion with the predicted message. What §8.4 preserves is
the diagnosis error, not an outstanding debt.

⚠ **What the block did NOT cause: the two void service tiers.** Those were a concurrency mistake of mine
(two runs against one SQL Server and one MinIO bucket), and the SIGSEGV and row-count mismatch they produced
belong to that, not to the SDK. Do not let one environmental problem absorb the blame for an unrelated one.

## 9. STEP 2 AS BUILT (2026-09-02) — `Fabricator.Common`

C#-only. **NO ABI change, NO C++ change.** Nine files left `Fabricator.Bridge`: eight to a new
`Fabricator.Common`, one (`ObjectNotFoundException`) to `Fabricator.Abstractions` per §7.4a. **Compile cost
ZERO** — 0 errors across every buildable project, and the same 8 pre-existing warnings.

### 9.1 What shipped

| | |
|---|---|
| `dotnet/Fabricator.Common/Fabricator.Common.csproj` | references **Abstractions and nothing else**; no `TargetFramework` line (Directory.Build.props); no `publish-managed.ps1` line |
| → `Fabricator.Common` | `ArrowValueReader`, `InMemoryArrayStream`, `AsyncEnumerableArrowStream`, `ArrowDataReader`, `AggregateSession`, `StaticTableFunction`, `StaticInOutFunction`, `StaticCollectorFunction` |
| → `Fabricator.Abstractions` | `ObjectNotFoundException` — it is provider CONTRACT, not a convenience (§7.4a) |
| `Fabricator.Bridge.csproj` | one new `ProjectReference` |
| `Fabricator.FluidPlugin.csproj` | one new `ProjectReference`, `Private="false"` — and **still none to Bridge** |
| `BackendRegistry`'s plugin-reference comment | now names Common as the optional middle (§7.6 hazard 3) |

**VERIFIED in the payload: `Fabricator.Common.dll` sits in `build/release/extension/fabricator/fabricator/`
with no script entry**, exactly as §7.4 predicted — a ProjectReference is transitive, which is also why
`Fabricator.SqlServer`, `.Delta`, `.DeltaRs` and `.AnalysisServices` needed no edit at all.

### 9.2 ⚠⚠ THE PLAN PREDICTED "a batch of visibility errors". THERE WERE NONE — and the reason is the cheap check to run BEFORE any future carve-out

Two facts, both knowable in advance and neither one obvious:

1. **All eight movers were ALREADY `public`.** §7.4a spends a paragraph deciding they should become public
   and warning about the widened contract; measured, that widening had happened long ago for the ordinary
   reason — `Fabricator.SqlServer` / `.Delta` / `.DeltaRs` / `.AnalysisServices` are separate assemblies and
   were already using them. **The only genuinely NEW public surface this commit creates is one method**
   (`ArrowValueReader.ReadTimestamp`, §9.4). So the "13 newly public types widen a contract with no version
   number" cost is about one thirteenth of what the plan priced.
2. **The namespace is unchanged**, so not one `using` moved. Third time that convention has paid out
   (`Fabricator.Abstractions`, `Fabricator.Delta`, now Common).

⇒ **the pre-flight check for a carve-out is: grep the candidates' declared visibility, and who already
references them.** Public and used across assemblies ⇒ the move is mechanical. The plan's fear was of a case
that did not exist here.

### 9.3 ⚠⚠ FOUR OF THE THIRTEEN DID NOT MOVE, AND THE DECLINATIONS ARE THE PART A LIST CANNOT CAPTURE

§7.4a's instruction was *"move what is genuinely wanted, not all 13 because the list exists"*. Applied, with
one reason each:

| stayed in Bridge | why |
|---|---|
| `ChannelArrowStream` | `internal`, and the bulk-write channel is host machinery — no caller outside the ABI's own `begin_bulk`/`push_batch` |
| `DescribedArrowStream` | exists ONLY for the host's bind-vs-scan split (`PopulateReturnSchema` calling `get_schema` without pulling). No consumer in any other assembly |
| `SqlGen` | the host calls it **on a provider's behalf** during `bind_replace`; a provider never calls it. Being *about* `ISqlTableFunction` is not the same as being *for* its author |
| `SqlDdl` | a heuristic producing a signal the HOST acts on (invalidate the catalog cache after `fabricator_exec`) |

⚠ The distinction that did the work in three of the four: **"is this called BY a provider, or called ON a
provider's declaration?"** `AggregateSession` moved because a provider's `AggOpen` returns one
(`SqlServerBackend` literally does `new AggregateSession(fn)`), so an out-of-tree plugin authoring a UDAF
needs it. `SqlGen` did not, because the provider only supplies the declaration it validates.

### 9.4 ⚠⚠ THE ACCEPTANCE TEST HAD TO BE RE-DERIVED — ITS TARGET NO LONGER EXISTS, AND WHAT REPLACED IT IS NOT SUBSTITUTABLE

§7.4's acceptance test was: *"delete the FluidPlugin's local `ArrowScalar.Read` duplicate and have it use
`ArrowValueReader` — without widening its reference to `Fabricator.Bridge`."*

**There is no `ArrowScalar` in the plugin any more.** The ~20-line duplicate that existed when §7.4 was
written became `FluidValueModel.ReadCell` during the Fluid value-model work, and **it is now a documented
SUPERSET rather than a duplicate** — its own remarks say so. Measured against `ArrowValueReader.ReadScalar`,
it differs on four case groups:

| case | `ReadScalar` | `ReadCell` |
|---|---|---|
| `FloatArray` / `DoubleArray` | the raw float/double | the **decimal ladder** (`Number(...)`), which REFUSES a magnitude Fluid cannot represent |
| `BinaryArray` | `byte[]` | **lowercase hex**, because a byte array renders as concatenated decimals |
| `Date32` / `Date64` | the raw `DateTime` (**`Kind = Unspecified`**) | `AsUtc(...)`, i.e. `DateTimeKind.Utc` |
| STRUCT / MAP / LIST | not handled at all | the Fluid indexables |

⇒ **substituting it wholesale would revert three GATED behaviours**, the date one being the
renders-the-previous-day bug fixed in fluid-templating.md §7.4a. **A test written as "delete the duplicate"
can age into "delete the fix".**

**WHAT SHIPPED INSTEAD, and it is narrower but genuinely the right one: `ReadTimestamp`.** The plugin held a
character-for-character copy of the bridge's, including its two explicit `(object)` casts — and those casts
are the fix for a defect that **shipped for four months** (C#'s conditional operator unifying both branches
back to `DateTimeOffset`; docs/mssql-cdc.md §21.5 and §23). It is `ArrowValueReader.ReadTimestamp` now, made
`public` for exactly this caller, reached through a `Fabricator.Common` reference **with no
`Fabricator.Bridge` reference added** — which is the property the acceptance test existed to check.

⚠ **Of the two duplicates in that file, the one worth removing was the one that looked least urgent.** The
big obvious one is a deliberate superset; the small invisible one carried the defect history. **The size of a
duplicate says nothing about the cost of its divergence.**

⚠ **`Private="false"` verified by measurement, not by reading the csproj**: `build/plugins/fluid/` still
holds exactly its previous seven DLLs and the plugin archive still ships seven — `Fabricator.Common.dll`
resolves from the host's own context, per §7.6 hazard 1.

⚠ **NOT done, deliberately: no consumer was manufactured in `Fabricator.SamplePlugin`.** It reads its scalar
arguments by casting to the concrete Arrow array type, which is correct for a typed signature; routing that
through `ArrowValueReader` would box every value for nothing — a worse example, not a better gate.

### 9.5 ⚠⚠ §7.4a's "13 → 16" IS WRONG ON TWO OF THE THREE, AND THE CHECK IT ASKED FOR IS WHAT SHOWS IT

§7.4a concluded that adding `IHostLog` makes `DbDataReaderArrowStream`, `SingleScanArrowStream` and
`MemoryProbe` movable, *"so 'phase 2' is not a second migration; it is add `IHostLog` first"*, with one
caveat: *"`DbDataReaderArrowStream` additionally takes an `InterruptScope`, so it needs that resolved too —
check before counting it in."* **Checked. It does not resolve, and it takes a second file with it.**

| deferred file | needs, besides `FabricatorLog` | verdict |
|---|---|---|
| `MemoryProbe` | nothing — `Environment.WorkingSet` + `GC`, both BCL | **movable with `IHostLog`** |
| `DbDataReaderArrowStream` | `InterruptScope`, which calls **`HostFs.CanInterrupt` / `HostFs.IsInterrupted`** | **BLOCKED** — `HostFs` is the unsafe ABI wrapper and can never leave Bridge |
| `SingleScanArrowStream` | shares its FILE with a class calling **`Host.Query`** | **BLOCKED**, and it is `internal` Bridge machinery for single-use host-query inputs — not plugin-facing even if it were free |

⇒ **the real number is 13 → 14**, and the one type `IHostLog` unlocks is a diagnostic for OUR heavy paths
rather than anything a plugin wants. **So `IHostLog`'s justification is NOT "it unblocks the deferred
three" — that argument is measured false. Its justification is the capability itself**: a plugin has no way
to log anywhere a user can see, because reaching `duckdb_logs` goes through the `host_log` ABI callback. That
is a §3.1 service on its own merits, and it should be decided on those rather than inherited from a
migration argument that did not survive contact.

⚠ The chain `DbDataReaderArrowStream → InterruptScope → HostFs` is §7.4a's impossibility rule working one
step further out than the rule was stated: not only *"a file needing a Bridge internal cannot move"* but
*"a file needing a type that needs a Bridge internal cannot move either"*. **A closure check must be
TRANSITIVE** — a one-level scan reports `DbDataReaderArrowStream` as clean.

### 9.6 Gates

| | |
|---|---|
| every project builds | **0 errors**, 8 pre-existing warnings, no new one |
| the masking check (§7.5) | **git itself asserts it**: `git diff -M --numstat` reports **8 of the 9 moved files as 0-changed-line renames**. The ninth is `ArrowValueReader.cs`, whose entire diff is `private`→`public` plus the five doc lines explaining it |
| both tiers | IDENTICAL counts — a pure move changes no answer |
| the payload | `Fabricator.Common.dll` present, with no `publish-managed.ps1` entry |
| the plugin output | still exactly seven DLLs, so nothing was copied that should not be |

⚠ **The tiers can only show the move broke nothing; no assertion can see WHERE a type lives.** What proves
the move achieved something is §9.4's deletion, and the FluidPlugin's csproj having two ProjectReferences
rather than three.

## 10. `IHostLog` AS BUILT (2026-09-02) — the answer to §7.4a's third question

C#-only. **NO ABI change, NO C++ change.** Gate `verify_plugin` **112 → 133**; three mutants, each killed at
its own assertion, and a fourth that SURVIVED and corrected the code (§10.4). Service **54/54 — 3339** =
3318 + exactly this suite's 21, which is what shows no other suite moved; hermetic **74/74 — 8259**,
unchanged, because that tier points `FABRICATOR_PLUGIN_DIR` at an EMPTY directory on purpose and so cannot
load the sample plugin at all.

⚠ **Nothing user-visible ships.** `plug_log` lives in `Fabricator.SamplePlugin`, which is a test fixture —
`pack-distribution.ps1` bundles only the Fluid plugin — so no SQL function was added to the product and the
README owes nothing. `IHostLog` is a plugin-AUTHOR surface.

**⚠ Built on the CAPABILITY argument, not the migration one.** §7.4a decided `FabricatorLog` should be a
service rather than a mover, and gave two reasons: it needs the running host, and moving it would drag
Microsoft.Extensions.Logging into every plugin's closure. Both hold. What does NOT hold is the third thing it
said — that `IHostLog` unblocks three deferred movers — which §9.5 measured wrong on two of the three. So the
case for building it is the plain one: **a plugin has no way to log anywhere a user can see.**

### 10.1 What shipped

| | |
|---|---|
| `Fabricator.Abstractions/IHostLog.cs` | `HostLogLevel` (an enum whose VALUES are the `host_log` ABI codes), `IHostLog.GetLogger(category)`, `IHostLogger.IsEnabled(level)` / `.Log(level, message)` |
| `Fabricator.Bridge/HostServices.cs` | `HostLogService` / `HostLoggerAdapter` over `FabricatorLog`, registered by `Publish()` |
| `Fabricator.SamplePlugin` | `plug_log(level, message) -> BOOLEAN` — logs when enabled, returns `IsEnabled` |

**⚠ It is a FACTORY, not one `Log(category, level, message)` method**, and that is §7.4a's `IsEnabled`
constraint deciding the shape rather than taste: the category is fixed per call site while the level check
happens per event, often inside a per-batch loop. A one-method interface would re-resolve the category on
every event and — worse — make the cheap gate look expensive enough to skip, which is exactly the regression
`MemoryProbe`'s `IsEnabled` gate exists to prevent.

**⚠ Registered UNCONDITIONALLY, unlike the other three.** `IHostFileSystem` / `IHostHttp` / `IHostQuery` are
each published only if the host filled the matching callback, so `GetRequired<T>()` can say "the host cannot
do this". Logging has no such capability to test: `FabricatorLog` always has somewhere to put an event (the
`FABRICATOR_LOG_FILE` sink, or nothing), and reaching `duckdb_logs` is an ADDITIONAL sink rather than a
precondition. A plugin can therefore always log; it may log into the void, which is the same deal the host's
own categories get.

### 10.2 ⚠⚠ MEASURED: `IsEnabled` IS A REAL ANSWER, and the discriminating pair is Trace vs Debug

`plug_log('debug', …)` → **true**, `plug_log('trace', …)` → **false**, with no environment change. The
mechanism, read from `FabricatorLog` and then confirmed: when the host forwarding is wired,
`EnableHostForwarding` PROMOTES a `NullLoggerFactory` to `LoggerFactory.Create(b =>
b.SetMinimumLevel(LogLevel.Debug))` — so Debug is the floor and Trace falls below it.

⚠ **That pair is the only assertion in the gate a developer's own environment can move**, and the suite says
so: with `FABRICATOR_LOG_LEVEL=trace` exported, `BuildDefault()` builds a real factory at Trace, the
promotion does not happen, and Trace answers true. Neither CI tier sets that variable.

⚠ Without the pair the boolean would be worthless — a build returning a constant `true` passes every other
assertion here. Mutant B is exactly that build.

### 10.3 ⚠⚠ TWO COLUMNS OF `duckdb_logs` CARRY THINGS THE MESSAGE CANNOT

MEASURED, one row per plugin event:

| column | value | what it proves |
|---|---|---|
| `message` | `plug_log warning probe` | the event arrived |
| `log_level` | **`WARNING`** (not `WARN`) | the level survived TWO hops — `HostLogLevel` → MEL `LogLevel` → the host's int code |
| `type` | **`Fabricator.SamplePlugin`** | the CATEGORY crossed, so a plugin's output is attributable to the plugin rather than lost among the host's (`QueryLog`, `Fabricator.Bridge`, …) |

⇒ a mapping that slipped by one still delivers every message, so **only `log_level` can see it**. That is
mutant A.

**⚠ A MEASUREMENT WORTH KEEPING FOR ANY FUTURE `duckdb_logs` PROBE: in the SHELL, `duckdb_logs` is EMPTY
unless you `SET logging_storage = 'memory'`.** The default storage is stdout, so `duckdb.exe` PRINTS every
event (which looks like success) while `SELECT count(*) FROM duckdb_logs` answers **0**. `unittest` does not
need the setting — `verify_catalog_init` has queried `duckdb_logs` with only `enable_logging` +
`logging_level` since it was written. Two environments, two answers; a probe in the wrong one reads as "the
service does not work".

### 10.4 ⚠⚠ THE FOURTH MUTANT SURVIVED, AND IT CORRECTED THE CODE RATHER THAN THE GATE

`HostLoggerAdapter.Log` shipped for an hour as:

```csharp
LoggerExtensions.Log(_log, Map(level), "{Message}", message);   // the message as an ARGUMENT
```

justified in a comment by: *"MEL parses the format string for {placeholders}, so a message containing a brace
— a rendered template, a JSON fragment — would either be mangled or throw inside the logger."*

**The mutant that removes the indirection (`Log(_log, Map(level), message)`) passes all 133 assertions**,
including one asserting that `plug_log braces {Not} {0} {{x}} probe` arrives verbatim. Reading MEL's source
afterwards says why: `FormattedLogValues` constructs a `LogValuesFormatter` only when the argument array is
NON-EMPTY, so a zero-argument call returns the original string untouched and never parses it.

⇒ **the theory was wrong, the indirection unnecessary, and the code was simplified to match the
measurement.** The braces assertion stays, relabelled as what it is: a CHARACTERIZATION test of MEL, which no
mutant of ours can kill. Its value is that a change in that behaviour — ours or upstream's — arrives as a
failed assertion naming the string.

⚠ The general form, and it is this file's recurring one: **a defensive mechanism justified by a hazard nobody
measured is indistinguishable from a necessary one until you delete it.** The mutation run is what asked the
question; nothing about reading the code would have.

### 10.5 What was deliberately NOT done

- **`MemoryProbe` did NOT move**, though §9.5 establishes it is the one type `IHostLog` unlocks. It is a
  diagnostic for OUR heavy paths and a plugin gains nothing from it, so moving it would be motion to
  demonstrate the interface rather than to serve anybody. It would also need its `static readonly` logger
  resolved lazily, since a static initializer can run before `Publish()`.
- **`FabricatorLog` is unchanged and stays in Bridge.** `HostLogService` is a thin adapter on purpose: every
  routing decision (which providers, the minimum level, the `duckdb_logs` forwarding) already lives in one
  place, and a plugin's events must be filtered and sunk exactly like the host's own.
- **No structured logging.** `IHostLog` takes a rendered string, so a caller interpolates its own message.
  Nothing is lost: both existing sinks are message-string based already — the host callback is
  `(int level, string category, string message)` and `FileLoggerProvider.Write` takes a line — so no
  structured field survives today either.

## 5. Open questions

1. ~~Is **cross-plugin** sharing in scope?~~ **ANSWERED 2026-09-02 (user): it is the EXCEPTION, and a
   shared assembly the plugins own is the answer — so the host ships nothing new. See §3.3, including the
   measured version rule.**
2. ~~`System.IServiceProvider` + our own extensions, or take MEDI?~~ **ANSWERED and BUILT (§8): BCL
   `IServiceProvider` as the contract, our own `ConcurrentDictionary` as the implementation.** The deciding
   facts are that there is no dependency graph to resolve, and that a built MEDI provider is IMMUTABLE while
   `BackendRegistry.Invalidate()` re-scans.
3. ~~Does the locator REPLACE `HostHttpTransport` / `HostQueryTransport`, or wrap them?~~ **REPLACED —
   both are DELETED (§8). Breaking for the three out-of-tree plugins, which pin by sha and migrate at their
   next bump; no aliases, as with the `IArrow*` renames and `ScalarFnBind`.**
4. Should `Fabricator.Abstractions` finally be PACKED and versioned? (§1.5.) A bigger contract surface makes
   the sha-pin more load-bearing, and this is the natural moment to ask.
5. Scoping: are all services singletons, or is there a per-call/per-transaction scope? **STILL OPEN, and
   the three built services sidestep it rather than answer it**: the ambient rule (§3.1) makes a singleton
   safe for all three, because each reads the ambient per call and holds nothing. A service that genuinely
   needed per-call state (an `IInterruptScope`) would be a FACTORY, not a singleton, and the registry has no
   opinion about that yet.
6. ~~What does a plugin do when a service is absent?~~ **ANSWERED by the build (§8): `Get<T>()` returns
   null, `GetRequired<T>()` throws NAMING the interface, and `IsAvailable<T>()` preserves what the old seams'
   `IsAvailable` gave.** ⚠ The load-bearing half is what `Publish()` does NOT do: a capability the host did
   not register is simply absent, rather than present as an implementation that always throws — otherwise
   "the host cannot do this" and "the call was wrong" become the same failure.
