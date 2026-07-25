# Single-file distribution: the NativeAOT C# installer extension

Status: **BUILT AND WORKING ON WINDOWS** (phases 1–3 of 5; §12 spike, §14 `Installer.Core`,
§15 the AOT shell + packaging). Remaining: the Linux artifact, user docs, CI. Goal: distribute
the fabricator extension (C++ loadable + the managed .NET payload) as **one `.duckdb_extension`
file** that a user can `INSTALL`/`LOAD` like any other extension, with the runtime/assemblies
extracted into DuckDB's extension directory at first load.

```sql
-- one 61 MB file, an empty extension directory, no environment variables:
LOAD '<path>/fabricator.duckdb_extension';   -- 1.2 s first time, 0.01 s after
SELECT fabricator_version();                 -- the core and its CLR are up
```

---

## 1. Problem

The fabricator extension is not one file. A working install is:

| piece | today | size |
|---|---|---|
| `fabricator.duckdb_extension` | the C++ loadable (CPP ABI, exact-DuckDB-version-locked) | ~35 MB |
| `fabricator/` managed dir | Bridge + SqlServer + AnalysisServices + DeltaLake assemblies (+ optionally a self-contained .NET runtime) | ~35 MB FDD / ~250 MB self-contained |
| glue | `FABRICATOR_MANAGED_DIR` env var, or the dir must sit *next to* the loadable | — |

Distribution today is "unzip this archive somewhere, set an env var, `LOAD` by path" — three
steps, three failure modes, and no `INSTALL fabricator`-shaped story. We want:

1. **One file** per (DuckDB version × platform × SKU).
2. **Zero env vars** in the happy path.
3. `LOAD` semantics identical to any other DuckDB extension afterwards.
4. Packaging/extraction logic that is **testable on any dev machine** — explicitly including
   macOS, where we have no C++ build yet and no native toolchain experience.

## 2. Ground truth from DuckDB's loader (source-verified, v1.5.5 tree)

Facts the design leans on, with the source locations:

- **One file = one extension identity.** The entry symbol is derived from the *filename*:
  `<filebase>_duckdb_cpp_init` (CPP ABI) / `<filebase>_init_c_api` (C ABI)
  — `extension_load.cpp:633,660`. A single binary cannot register as two extensions, but it
  **can export both entry-symbol spellings** so the same bytes load under two filenames.
- **An extension may LOAD another extension during its own load.** `ExtensionManager::BeginLoad`
  takes a per-extension lock (not a global one) and releases the manager's list lock before
  loading (`extension_manager.cpp:73-110`); fabricator itself already autoloads `parquet`
  inside its own registration (`fabricator_delta_mfr.cpp:204`). Chain-loading is a proven,
  lock-safe pattern.
- **C-ABI (`C_STRUCT`) extensions are DuckDB-version-portable.** Their footer records a
  **C API version**, checked as `major == 1 && minor <= host minor`
  (`extension.cpp:60-78,106-114`) — one binary per platform spans DuckDB releases.
  **CPP-ABI extensions are exact-version-locked** (`extension.cpp:51-58`); that constraint
  stays on the fabricator core and cannot be engineered away.
- **The metadata footer is a plain file append** — 512 bytes of fields + 256 bytes of
  signature space, written by `extension-ci-tools/scripts/append_extension_metadata.py`
  (pure Python: copy file, append bytes). No linker, no resource compiler, no platform
  divergence. OS loaders (PE/ELF/Mach-O) read headers, not file ends, so trailing bytes are
  inert — this is why DuckDB's own footer works, and it equally permits *our* payload bytes
  between the library image and the footer (§5).
- **The extension directory layout** is `<base>/<version-dir>/<platform>/`, where version-dir =
  the release tag (`v1.5.5`) for releases else the source id (`IsRelease` =
  `!contains("-dev")` — `extension_install.cpp:31-47`), platform = `PRAGMA platform`, and base is
  the first non-empty of: the **`extension_directory`** setting, the first entry of the
  **`extension_directories`** LIST setting, else the default `~/.duckdb/extensions`
  (`extension_install.cpp:93-136`; `~` expands via the `home_directory` setting).
  **There is no `extensions/` path component** — it appears in the default case only because the
  default *base string itself* ends in `extensions`. So `SET extension_directory='D:\ext'` gives
  `D:\ext\v1.5.5\windows_amd64`, **not** `D:\ext\extensions\…`. (Corrected in Phase 2 — see §14;
  an earlier draft of this section had it wrong, which would have extracted the payload into a
  directory DuckDB never searches.) All inputs are queryable via SQL, so the path is computable
  from inside an extension — verified empirically by observing where `INSTALL '<local file>'` lands
  under each of the three cases.
- **Name-based `LOAD fabricator` works without an `.info` file** — a missing
  `.duckdb_extension.info` falls back to `ExtensionInstallMode::NOT_INSTALLED` and loads
  anyway (`extension_load.cpp:556-563`).
- **Signature reality:** we cannot sign; both pieces load only under
  `allow_unsigned_extensions` (a startup-only option). That is exactly today's requirement —
  this design neither improves nor worsens it. (`allow_extensions_metadata_mismatch` exists
  as a separate escape hatch for version-field mismatches; we do not rely on it.)
- **The managed-dir lookup is filename-independent**: `FABRICATOR_MANAGED_DIR` env, else a
  folder literally named `fabricator/` next to the loaded module (`clr_host.cpp:341-345`).
  So if the core lands in the extension directory with `fabricator/` beside it, the CLR
  boots with **zero configuration**. FDD-vs-self-contained is auto-detected by hostfxr's
  presence in that dir (existing mechanism).

## 3. Why the installer is C#, not C++

A self-extracting trampoline needs its payload embedded in the binary. In C++ that is three
divergent, toolchain-specific mechanisms — Windows resources (`.rc`/`FindResource`), ELF
sections (`objcopy`/`.incbin`), Mach-O segments (`-sectcreate`/`getsectiondata`) — each only
testable on its own OS, which is precisely the machine class (macOS) we cannot iterate on.

In C# the same job is one platform-neutral code path (BCL file IO, `System.IO.Compression`,
SHA-256), and — decisively — **the entire extraction/packaging logic is plain .NET, unit-
testable as ordinary xunit on the Windows dev machine with no AOT and no DuckDB involved**.
Only the final `dotnet publish -p:PublishAot=true` is per-platform.

The substrate is **DuckDB.ExtensionKit** (MIT), a NativeAOT C# extension toolkit on DuckDB's
C extension API:

- a source generator emits the `<name>_init_c_api` entry (`[UnmanagedCallersOnly]`,
  `CallConvCdecl`), performs the `duckdb_extension_access` handshake (`GetApi` → full
  `DuckDBExtApiV1` struct copy → `GetDatabase` → `duckdb_connect`) and hands user code a live
  connection — the entire C-ABI sharp edge (mirroring the api-struct layout) is the kit's
  code, not ours;
- the `duckdb_query` slot is present in its `DuckDBExtApiV1` mirror — everything the
  installer needs (pragmas, settings, and the chain `LOAD`) is one `duckdb_query` away
  (if the kit's `DuckDBConnection` wrapper lacks a public `Execute(sql)`, we add a thin
  helper — MIT, upstreamable);
- its MSBuild packaging target already invokes `append_extension_metadata.py` with
  `--abi-type C_STRUCT` and the C API version → the produced installer is
  **DuckDB-version-portable** per §2;
- supported RIDs: win-x64/arm64, linux-x64/arm64, osx-x64/arm64.

## 4. Architecture: two pieces, one file

```
fabricator.duckdb_extension                  ← the ONE distributed file
├─ [NativeAOT installer image]                 C# (Fabricator.Installer), C_STRUCT ABI,
│                                              entry: fabricator_init_c_api
├─ [payload]                                   deflate zip (§5):
│    fabricator_core.duckdb_extension            the existing C++ loadable (CPP ABI)
│    fabricator/…                                the managed dir (FDD or self-contained)
├─ [manifest json]                             UNCOMPRESSED, outside the archive: target duckdb
│                                              version, platform, sku, payload sha + length
├─ [payload index]                             magic "FABPKG01" + format version + the two lengths
└─ [DuckDB metadata footer]                    written by append_extension_metadata.py (must be last)
```

The manifest sits **outside** the archive, between payload and index. That is what lets it carry the
payload's own SHA-256 (a hash inside the thing it hashes would be circular) and what keeps the
steady-state load path free of any decompression: one tail read yields the version gate, the names
and the sha to compare against the on-disk marker.

**Transparent naming (chosen):** the installer *is* `fabricator` — the name users type. The
inner core is extracted as **`fabricator_core.duckdb_extension`** (same binary as today's
loadable; it additionally exports a forwarding `fabricator_core_duckdb_cpp_init` entry —
~3 lines, `DUCKDB_CPP_EXTENSION_ENTRY(fabricator_core, loader)` calling the shared
`LoadInternal` — so one artifact loads under either filename and **dev flows are unchanged**:
building and direct-loading `fabricator.duckdb_extension` (CPP) by path keeps working).

User experience, all sessions identical:

```sql
-- direct file (or: INSTALL '<path>/fabricator.duckdb_extension'; LOAD fabricator;)
LOAD '<path>/fabricator.duckdb_extension';
-- installer: ensure payload extracted (sha-cached, no-op after first run) → LOAD core
-- result: every fabricator function/ATTACH surface is available
```

After the first run the extension directory contains `fabricator_core.duckdb_extension` +
`fabricator/` — so a fresh session can do a bare **`LOAD fabricator_core;` with the
trampoline completely out of the loop**: name resolution finds the file in the extension
directory (no `.info` needed — the `NOT_INSTALLED` fallback loads it), the forwarding
`fabricator_core_duckdb_cpp_init` export satisfies the filename-derived entry lookup, and
the managed dir sits beside it. That is the recommended steady-state for pinned
environments (dbt profiles, notebooks) once provisioned; the trampoline path stays the
universal instruction because it self-heals (re-extracts a swapped-in newer artifact,
friendly version error on mismatch).

Per-session cost of the trampoline path is smaller than the file size suggests: dlopen maps
only the segments the library's program headers declare — the appended payload, like
DuckDB's own footer, is unreferenced trailing data and is **read only during extraction**;
and the whole-file signature hash is skipped under `allow_unsigned_extensions` (required
anyway). Steady state = 512-byte footer read + AOT init + two pragmas + one sha-marker read
+ the nested core load.

All user-facing names (functions, `TYPE fabricator`, secrets, settings) are registered by
the core and are unaffected by which file loaded it. `duckdb_extensions()` shows both
`fabricator` (installer) and `fabricator_core` — cosmetic, documented.

**Why not an `INSTALL`-only trampoline under a different name** (`fabricator_install` that
you run once, then `LOAD fabricator` hits the extracted core directly): workable, but it
splits the story into two names and makes the fresh-machine `LOAD fabricator` fail until the
installer ran once. The transparent model gives one name, one file, one instruction that is
correct in every environment (dbt profile, notebook, CI), at the cost of a per-session
no-op sha check (one small file read).

## 5. Payload embedding: polyglot append (chosen) vs embedded resource

**Chosen: polyglot append.** Packaging = `cat installer-aot-lib + payload.zip + index` then
run `append_extension_metadata.py` (footer must be the trailing bytes). Extraction reads the
installer's *own file*: locate the index by fixed offset from EOF (footer size is a known
constant), verify magic, stream-decompress from the payload offset.

- The AOT binary **compiles once per platform**; payload swaps (FDD ↔ self-contained SKU,
  core rebuilt for a new DuckDB version) are pure file concatenation — no recompile.
- The ~100 MB self-contained payload never goes through the AOT compiler (ilc) or sits in
  memory; extraction streams from disk.
- Cost: the installer must discover its own file path. The C extension API does not expose
  it, so: `GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, &someFn)` +
  `GetModuleFileNameW` on Windows, `dladdr` on POSIX — two tiny P/Invokes, pure C#, no build
  system divergence, unit-testable (resolve the test assembly's own path).

**Alternative (fallback): `EmbeddedResource`** — supported under NativeAOT
(`Assembly.GetManifestResourceStream`), zero own-path discovery, but every payload change
recompiles, and pushing a 100 MB resource through ilc is an unmeasured risk. Keep as the
fallback if the polyglot spike hits a loader that objects to trailing bytes (none known —
DuckDB's own footer is exactly this).

Payload archive: single deflate zip (BCL `ZipArchive`), optionally Brotli-wrapped for the
self-contained SKU (BCL `BrotliStream`, better ratio on the runtime). Built
**deterministically** (fixed entry order + timestamps) so the payload SHA-256 is reproducible.

### SKUs

| SKU | payload | file size | requires |
|---|---|---|---|
| standard | core + FDD managed dir | ~20–25 MB (est.) | .NET 8+ on the machine (`FABRICATOR_DOTNET_ROOT`/`DOTNET_ROOT`/default probing — existing clr_host logic) |
| standalone | core + self-contained managed dir | **58 MB (measured, §14)** | nothing |

Same installer binary, same filename, different payload — the SKU is a download choice.

## 6. Installer flow (at every load)

1. Kit-generated entry runs; our `RegisterFunctions(connection)` body executes
   (registers nothing — the installer contributes no SQL surface).
2. **Compatibility gate:** `PRAGMA platform` + `PRAGMA version` vs `manifest.json`.
   Mismatch → `set_error` with an exact message
   (*"this fabricator build targets DuckDB v1.5.5 / windows_amd64; you are running v1.6.0 —
   download the matching artifact from …"*). This converts the CPP-ABI exact-match failure
   from DuckDB's generic footer error into an actionable one — and it fires **before**
   anything touches disk.
3. **Resolve the extension directory** (mirror of `ExtensionHelper` §2):
   `current_setting('extension_directory')` else `<home>/.duckdb`, + `extensions/` +
   version-dir (tag if no `-dev`, else source id from `PRAGMA version`) + platform.
   `CreateDirectory` as needed.
4. **Idempotence check:** compare `<extdir>/fabricator_core.payload.sha` against the
   manifest's payload SHA **and** confirm the files it vouches for are actually present
   (so a payload someone half-deleted is repaired rather than trusted). Equal → skip to
   step 6 (the steady-state fast path: one small file read plus two stats; no lock taken).
5. **Extract** under a cross-process lock (`<extdir>/.fabricator.lock`, `FileShare.None`
   + bounded retry), re-checking the marker once the lock is held (another process may
   have just installed exactly this payload — the won-race case): verify the payload SHA,
   stream-decompress into `<extdir>/.fabricator.staging.<random>/`, check the expected
   layout, then move into place — and write the sha marker **last**, so the marker can
   only ever mean "a complete payload with this SHA is present".
   Displaced files are **renamed aside** into `<extdir>/.fabricator.old.<random>/` rather
   than deleted: Windows refuses to delete a library another process still has mapped but
   does permit renaming it (the loader opens image files with `FILE_SHARE_DELETE`), so an
   upgrade while another session holds the old core loaded **succeeds**, and the displaced
   file is swept on the next slow path. Only when even the rename is refused does it fail
   with *"close other DuckDB sessions that have fabricator loaded and retry"*.
6. **Chain-load:** `duckdb_query("LOAD '<extdir>/fabricator_core.duckdb_extension'")`
   (full path — unambiguous regardless of search-path config). Errors (including the core's
   own clr_host errors, e.g. no .NET found for the standard SKU) propagate verbatim via
   `set_error`; optionally prefixed with SKU-specific guidance.
7. Return success. Total steady-state overhead: two pragma queries + one file read + one
   nested LOAD.

## 7. Version compatibility model

| artifact | ABI | locked to |
|---|---|---|
| installer (outer) | `C_STRUCT` | C API v1.x (minor ≤ host) — spans DuckDB releases |
| core (inner) | CPP | exact DuckDB version — unavoidable (we use catalog/storage/optimizer internals) |

The *distributed artifact* is therefore still per-(DuckDB version × platform) — the
version-portability of the outer shell buys not fewer artifacts but a **stable installer
binary** (recompiled only when installer logic changes; re-packaged per core build) and the
step-2 friendly version error. Artifact matrix = duckdb-versions we support × platforms we
build (today: windows_amd64, linux_amd64; osx blocked on a core C++ build, not on this
design) × 2 SKUs.

Hosting: direct download (GitHub releases) and/or a custom extension repository
(`INSTALL fabricator FROM '<url>'` — repo layout `<base>/<duckdb-version>/<platform>/
fabricator.duckdb_extension.gz`), both of which serve exactly this matrix. Community-repo
distribution is out of scope: its CI builds C/C++/Rust from source and cannot produce or
host the .NET payload, and its signing wouldn't cover the inner core anyway.

## 8. Build pipeline

New projects:

- **`dotnet/Fabricator.Installer.Core`** (plain net10.0/net8.0 class lib, no AOT):
  payload packer (deterministic zip + manifest + sha), polyglot reader/writer
  (offset math, magic, index), extension-dir resolution (pure function of
  `(extension_directory, home, version, source_id, platform)`), staging/locking/extraction
  state machine. **This is where all the logic lives — and all the unit tests.**
- **`dotnet/Fabricator.Installer`** (NativeAOT shared lib on DuckDB.ExtensionKit):
  the thin shell — `[DuckDBExtension]` class, own-path P/Invokes, `duckdb_query` calls,
  error prefixing. Target: near-zero logic.

Packaging script `scripts/pack-distribution.ps1 [-Sku Standard|Standalone] [-Rid <rid>]`:

1. Build the core loadable (existing cmake target) — with the added
   `fabricator_core_duckdb_cpp_init` forwarding export.
2. `publish-managed.ps1` in the SKU's mode (existing).
3. `Fabricator.Installer.Core` packer → deterministic `payload.zip` + manifest.
4. `dotnet publish Fabricator.Installer -r <rid> -p:PublishAot=true` (cached — recompiles
   only on installer changes).
5. Concatenate AOT lib + payload + index; run `append_extension_metadata.py`
   (`-n fabricator --abi-type C_STRUCT -dv <capi version> -ev <fabricator version>
   -p <duckdb platform>`).

NativeAOT cannot cross-compile between OSes, so per-OS build machines remain required
(Windows native; linux via the existing WSL flow — note AOT links glibc dynamically, so the
same glibc-baseline discipline as the C++ loadable applies; osx needs a mac/CI runner). The
point of this design is that those machines only *compile and smoke* — they never *debug
platform-specific packaging code*, because there is none.

## 9. Testing strategy (the reason this design exists)

| layer | what | where it runs |
|---|---|---|
| unit (xunit, no AOT, no DuckDB) | packer determinism + sha stability; polyglot round-trip (write → locate-the-index → extract, across footer sizes and against decoy magic bytes); extension-dir resolution table (release/dev version, custom/default dir, all platforms); staging/lock/upgrade state machine (won-race, in-use files, partial extraction recovery) | any dev machine |
| integration (`test/distribution/smoke_distribution.py`) | fresh install with no env vars → managed calls prove the CLR booted → Delta round trip → second load takes the fast path; plus the two must-not-touch-disk rejections (version mismatch, no payload) | per platform, CI |

**Not sqllogictest** (corrected in phase 3): the repo's `unittest` binary statically embeds
fabricator, so a chain-loaded core would collide with already-registered functions there — the test
would be measuring the wrong thing, or nothing. The distribution has to be exercised against a
**stock DuckDB**, so the harness is a small python script driving the official wheel (whose version
must match the core's).

The kit's own risk surface (api-struct layout, entry-point marshaling) is upstream-tested
and shared with its other consumers; our AOT shell adds ~3 P/Invokes and a handful of
`duckdb_query` calls on top.

## 10. Alternatives considered

- **C++ trampoline with per-platform resource embedding** — rejected: three
  toolchain-specific embedding mechanisms, each only debuggable on its own OS (the
  motivating problem, §3).
- **Tiny C trampoline + polyglot payload** — viable and dependency-free, but the
  extraction/locking/path logic would be C on three platforms with no unit-test story;
  the C# kit gives the same portable ABI shell with testable logic.
- **No trampoline: archive + install script / docs** — the status quo; no single file, no
  `INSTALL` story, env-var friction.
- **Installer downloads the payload instead of embedding** — smaller file, but adds
  network/proxy/trust failure modes at load time; embedding keeps the artifact offline and
  reproducible. Could be a later third SKU if artifact size ever matters.
- **Static linking into a custom DuckDB build** — a different product (we ship an extension,
  not a DuckDB distribution).

## 11. Risks and open questions

- **R1 — polyglot dlopen tolerance: RETIRED on Windows** (§12: a 138 MB artifact loads in
  0.56 s). Re-confirm once per platform when linux/osx artifacts are first built; the
  `EmbeddedResource` fallback (§5) is no longer expected to be needed.
- **R2 — kit maturity: acceptable** (§12 — the entry handshake + `duckdb_query` slice works
  as-is against DuckDB 1.5.5). Two caveats recorded: the `duckdb_result` out-param modeling
  (§12.2) and IL warnings in its LIST/MAP readers (§12.5). The slice is small enough to
  vendor/fork if the project stalls; MIT permits it.
- **R3 — unsigned-extensions flag.** Unchanged requirement, but now it gates a *nested*
  load too — both loads fail without it, with DuckDB's standard error. Document prominently.
- **R4 — Windows upgrade-in-use: mostly RETIRED** (§14). The rename-aside promote makes an
  upgrade succeed while another session has the old core loaded; the clear error remains
  only for the case where even a rename is refused. Both paths are unit-tested.
- **R5 — antivirus heuristics** on self-extracting binaries: low, monitor.
- **Open:** exact SKU naming for downloads; whether the installer should pre-probe .NET
  (standard SKU) for a friendlier error than clr_host's; whether `INSTALL`-time (rather than
  first-`LOAD`-time) extraction is worth pursuing later via an `.info`-writing step.

## 12. Spike results — ALL KEYSTONES VALIDATED (2026-07-25, Windows)

Spike lives in `scratchpad/installer-spike/` (gitignored): a DuckDB.ExtensionKit NativeAOT
extension `fabspike`, packed polyglot, loaded into the **official `duckdb==1.5.5` Python
wheel** (deliberately not our `duckdb.exe`, which statically embeds fabricator and would make
the chain-load proof vacuous — the test asserts `fabricator_version` is absent *before* the
load).

| # | keystone | result |
|---|---|---|
| S1 | a C_STRUCT NativeAOT kit extension loads into official DuckDB 1.5.5 | ✅ `LOAD ok`, `fabspike_hello() = spike-loaded` |
| S2 | the extension discovers its own file path | ✅ returns the **`.duckdb_extension` path** (not the `.dll`) — precisely what extraction needs |
| S3 | a polyglot payload is locatable + readable from its own file | ✅ `payload len=134171775 at=3678208 head=PK` |
| S4 | **a C-ABI extension chain-loads the CPP-ABI core** | ✅ `fabricator_version` 0 before → 1 after; `duckdb_extensions()` = `[(fabricator,True),(fabspike,True)]` |
| S4b | the chain-loaded core boots its CLR **unconfigured** | ✅ `hilbert_index([1,2],4)=7`, `bucket(8,'alice')=5` (managed calls over the C++/C# ABI), managed dir resolved by the default next-to-module lookup, **zero env vars** |
| R1 | large payload does not disturb dlopen | ✅ **138 MB artifact loads in 0.56 s** (whole process: python start + connect + AOT load + chain-load + CLR boot) — indistinguishable from the 12 MB build |
| §6.2 | error propagation | ✅ bad core path → `LOAD` fails cleanly with our message wrapping DuckDB's (`[fabspike] "LOAD '…'" failed: IO Error: Extension "…" not found`), no partial state |

Measured artifact composition (real core as payload): `3,678,208` lib + `8,304,160` payload
(the 22.8 MB core, deflated) + `16` index + `534` footer = **11,982,918** bytes.

### Findings that change the design

1. **The appended footer is 534 bytes, not 512.** `append_extension_metadata.py` writes a
   22-byte WASM custom-section header + 8×32 metadata fields + 256 signature bytes; DuckDB
   parses only the *trailing 512* (`ParsedExtensionMetaData::FOOTER_SIZE`, extension.hpp:42).
   ⇒ **Never compute the index position from a hardcoded footer size.** The spike (and the
   product) locate the index by a **bounded backward scan for the magic** over the last 8 KB —
   immune to footer-format changes and to whether padding is added.
2. **`duckdb_result` is a 6-field struct (48 bytes on x64)** but the kit's `DuckDBExtApiV1`
   models the out-param as `nint*`. Passing an 8-byte cell would let `duckdb_query` scribble
   past it → always hand it a zeroed over-allocated buffer (the spike uses 256 bytes).
3. **AOT publish needs both** a vcvars64 environment **and** `-p:IlcUseEnvironmentalTools=true`.
   Without the latter, ilc runs its own `findvcvarsall.bat` → `vswhere.exe` probe whose
   *failure text* gets concatenated into the linker command line (`error MSB3073 … 'vswhere.exe'
   is not recognized … link.exe`). With it, ilc uses the ambient `link`/`LIB`. Recorded in
   `scratchpad/installer-spike/build.bat`.
4. **C API version alignment is free:** DuckDB 1.5.5 exposes `DUCKDB_EXTENSION_API_VERSION`
   **1.2.0** (duckdb_extension.h:46-48), which is the kit's default `DuckDBVersion` for
   C_STRUCT — and the check is `major==1 && minor<=2`, so a `v1.2.0` artifact loads.
5. **The kit is not fully AOT-clean** — `ListVectorDataReader`/`MapVectorDataReader` raise
   IL3050 (`MakeGenericType`) + IL2067 (`Activator.CreateInstance`). Not on the installer's
   path (it reads no LIST/MAP vectors), but it is a real datum for
   [aot-bridge.md](aot-bridge.md) if the kit is ever used for data-returning functions.

## 13. Phasing

1. **Spike — DONE (2026-07-25)**, see §12: kit extension + polyglot append + nested
   `duckdb_query("LOAD …")` of the real core on Windows; R1/R2 and the chain-load keystone
   validated end-to-end, plus error propagation and a 138 MB scale test.
2. **`Fabricator.Installer.Core` + full unit suite — DONE (2026-07-25)**, see §14.
3. **AOT shell + `pack-distribution.ps1` + the core's dual entry symbol — DONE (2026-07-25)**, see
   §15: a real 61 MB Windows artifact installs and runs from one file.
4. **Linux** (WSL AOT publish + smoke); README/user docs.
5. **CI matrix + release automation**; osx deferred with the core's osx build.

## 14. Phase 2 as-built — `Fabricator.Installer.Core` (2026-07-25)

`dotnet/Fabricator.Installer.Core` (net10.0;net8.0, `IsAotCompatible=true`, **zero package
references** — BCL only, so the AOT shell's dependency closure is this one assembly) plus
`dotnet/Fabricator.Installer.Core.Tests`: **91 tests green on both TFMs**, no DuckDB and no AOT
involved. Types: `PayloadPacker`/`PayloadEntry`, `PolyglotIndex`/`PolyglotWriter`/`PolyglotPackage`
(+ internal `WindowStream`, `ArchivePath`), `ExtensionDirectoryResolver`/`DuckDbEnvironment`,
`CompatibilityGate`, `PayloadExtractor`, `PayloadInstaller` (+ internal `CrossProcessLock`),
`PayloadManifest`, `FabricatorPayloadNames`, `InstallerException`.

**Validated end-to-end with the real payload** (`RealPayloadEndToEndTests`, gated on
`FABRICATOR_E2E_CORE`/`_MANAGED`): the built core (22.9 MB) + the published self-contained managed
dir (115 MB, 367 files) → pack → artifact → read own trailer → gate → install → re-install fast
path, in **~4 s**. The produced install tree then **loads in the official `duckdb==1.5.5` wheel with
zero environment variables** and its CLR boots — `fabricator_version()` = 0.0.1,
`fabricator_managed_dir()` resolves to the extracted `fabricator/` purely by sitting next to the
core, and managed calls return real results (`hilbert_index([1,2],4)`=7, `bucket(8,'alice')`=5).
So the Core's output is a working install, not just well-formed bytes.

### Findings

1. **§2's extension-directory rule was wrong and is corrected above.** A custom
   `extension_directory` gets **no `extensions/` component**; the plural `extension_directories`
   LIST setting is a second source; `home_directory` drives `~`. Pinned by tests whose expectations
   were captured by watching where `INSTALL '<file>'` actually lands under all three cases. Had this
   shipped as designed, every user who sets `extension_directory` would have had the payload
   extracted somewhere DuckDB never looks.
2. **A zip's DOS timestamp has no timezone.** .NET encodes the `DateTimeOffset`'s wall-clock
   component verbatim, so the fixed UTC epoch yields identical bytes on build machines in different
   timezones — but reading it back reattaches the *reader's* local offset, so a round-trip only
   compares equal on the `DateTime` part. Reproducibility across build machines is load-bearing (the
   payload SHA is the idempotence marker), so it is pinned at the byte level: local-file-header bytes
   10..14 must be `00 00 21 00`.
3. **The payload archive must be packed at stream position 0.** A zip's central directory records
   local-header offsets relative to the start of the *stream*, so an archive written at an offset is
   only readable through a view with that same origin — which the polyglot's payload window is not.
   `PayloadPacker.Pack` therefore refuses a non-zero position instead of producing an artifact that
   fails to open later.
4. **The 58 MB standalone artifact beats the 90–110 MB estimate** (138 MB of input; deflate is very
   effective on the .NET runtime). The standard/FDD SKU will be proportionally smaller.
5. **Windows upgrade-in-use is largely solved, not merely reported** (§6.5, R4): rename-aside makes
   the loader-style-open case succeed. Both branches are tested — a handle opened
   `FileShare.ReadWrite|Delete` (what the loader holds) upgrades cleanly; `FileShare.None` produces
   the friendly error.
6. **The dual entry symbol is confirmed necessary, with the exact failure.** The same bytes that load
   as `fabricator.duckdb_extension` fail as `fabricator_core.duckdb_extension` —
   *"Extension '…' did [not contain the expected entry point]"* — because DuckDB derives the entry
   symbol from the file name. Until the forwarding `fabricator_core_duckdb_cpp_init` export lands
   (phase 4), `CoreFileName` is a manifest field so a tree using today's name can be produced.
7. Hardening that emerged while writing the tests, all covered: zip-slip rejection (entry paths
   validated syntactically *and* by resolved-path containment), manifest `CoreFileName`/
   `ManagedDirectoryName` rejected unless plain file names (the manifest is untrusted input joined
   onto a real directory), payload SHA/length verified before extraction, and the lock file
   deliberately never deleted (unlinking a locked file lets a second process create and lock a fresh
   one — two winners — under POSIX `flock` semantics).

## 15. Phase 3 as-built — the AOT shell, the packer and the dual entry symbol (2026-07-25)

**A single 61 MB `fabricator.duckdb_extension` now installs and runs itself on Windows**, verified
against the official `duckdb==1.5.5` wheel with an empty extension directory and no environment
variables (`test/distribution/smoke_distribution.py`, 12 checks):

```
[1] fresh install, no environment variables
  PASS  LOAD succeeded  (1.22s cold)
  PASS  the managed bridge booted and answers (zero configuration)
  PASS  both the installer and the core report as loaded  ['fabricator', 'fabricator_core']
  PASS  the extension directory holds exactly the core, the managed dir and the marker
  PASS  a Delta write/read round trip works through the extracted core
  PASS  the second load took the fast path  (0.01s < 1.22s)
[2] version mismatch          -> our message, and NOTHING written to the extension directory
[3] artifact without a payload -> "does not contain a fabricator payload", nothing written
```

**Cold 1.2 s / warm 0.01 s.** The warm path is the one that matters: it is the per-session cost in
every dbt run, notebook and CI job.

What landed:

- **`src/fabricator_extension.cpp`**: a second `DUCKDB_CPP_EXTENSION_ENTRY(fabricator_core, loader)`
  forwarding to the same `LoadInternal`, so one binary serves both file names.
- **`dotnet/Fabricator.Installer`** — the NativeAOT shell (2.9 MB native lib exporting
  `fabricator_init_c_api`): own-path discovery, trailer read, gate, extension-dir resolution,
  install, chain-`LOAD`. Around 60 lines of flow plus two helper files; all decisions delegated to
  `Fabricator.Installer.Core`.
- **`dotnet/Fabricator.Installer.Pack`** — a build-time CLI over the Core, so
  `scripts/pack-distribution.ps1` is pure orchestration and packing has exactly one (tested)
  implementation.
- **`scripts/pack-distribution.ps1`** — core → managed publish → AOT publish → pack → footer, each
  step skippable (`-SkipCore/-SkipManaged/-SkipShell`) because only the payload changes on a core
  rebuild.

### Findings

1. **The distinct core name is a REQUIREMENT, not cosmetics.** `ExtensionManager::BeginLoad` takes a
   lock per extension NAME, and a path-based `LOAD` derives that name from the file name
   (`ExtensionHelper::GetExtensionName` → the first dot-segment, extension_load.cpp:593-607). An
   installer named `fabricator` chain-loading a file that also resolves to `fabricator` would block
   on its own load lock. Hence the forwarding export — and hence the plan order changed: this had to
   land before the shell, not after it in phase 4.
2. **`duckdb_fetch_chunk` takes `duckdb_result` BY VALUE** (duckdb.h:5394 — a 48-byte struct), while
   DuckDB.ExtensionKit's API mirror types it as a single pointer. That is ABI-correct only where large
   structs are passed indirectly (Windows x64, AArch64); on **x64 SysV (linux/macOS-x64) the argument
   is copied onto the stack**, so a pointer-shaped call would hand the callee 40 bytes of garbage.
   The shell re-types the function pointer to take the real struct by value, letting the compiler emit
   the right convention per platform. Worth knowing before the Linux build rather than debugging it there.
3. **The reading path deliberately avoids `duckdb_value_varchar`/`duckdb_row_count`.** They are
   correctly modelled by the kit and would have been three lines instead of forty — but duckdb.h marks
   them "scheduled for removal", and their removal turns a struct slot into null, i.e. a crash. The
   installer is the version-PORTABLE half of the distribution, so it is exactly the component that
   should not be built on them. (Mixing is also forbidden at runtime: any deprecated accessor marks the
   result `CAPI_RESULT_TYPE_DEPRECATED`, after which `duckdb_fetch_chunk` returns null —
   result-c.cpp:340, stream-c.cpp:22.)
4. **sqllogictest is the wrong harness** for this (see §9): our `unittest` binary embeds fabricator
   statically, so the chain-loaded core would collide with already-registered functions. The
   distribution is tested against a stock wheel instead.
5. **Two build-mechanics traps**, both now encoded in the scripts: an AOT project must CLEAR the
   repo-wide `TargetFrameworks` (a single-value plural still counts as cross-targeting, and
   `dotnet publish` then demands `-f`); and `dotnet run` does not accept `--nologo` — it forwards it to
   the program, which sees it as argument one.
6. **Measured composition**: 2,882,560 B AOT shell + 61,111,666 B payload (368 entries: the 22.9 MB
   core plus the 115 MB self-contained managed dir) + 32 B index + manifest + 534 B footer =
   **63,995,127 B**.
