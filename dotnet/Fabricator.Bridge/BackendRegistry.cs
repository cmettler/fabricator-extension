namespace Fabricator.Bridge;

/// <summary>
/// Process-wide registry of backends, **keyed by provider name** so one binary can host several
/// providers (SQL Server, later Power BI/DAX, …). On first use it loads the provider assemblies named
/// by <c>FABRICATOR_BACKEND_ASSEMBLY</c> (comma-separated; default <c>Fabricator.SqlServer</c>), finds every
/// <see cref="IBackend"/> implementation, and registers each under its <see cref="IBackend.Name"/> +
/// <see cref="IBackend.Aliases"/> (case-insensitive). If none are found it falls back to
/// <see cref="StubBackend"/> so the bridge still works standalone.
/// <para>
/// <see cref="Resolve"/> picks a backend by provider name; <see cref="Active"/> returns the default
/// (the sole backend, or the one named by <c>FABRICATOR_DEFAULT_PROVIDER</c>) for call sites that don't
/// yet carry a provider — preserving single-provider behaviour until provider selection is wired through
/// the ABI.
/// </para>
/// </summary>
public static class BackendRegistry
{
    private static readonly object Gate = new();
    private static Dictionary<string, IBackend>? _byName; // name/alias (case-insensitive) -> backend
    private static string? _defaultProvider;              // canonical name of the default backend

    /// <summary>
    /// Explicitly registers a backend (e.g. from a host or test) under its name + aliases. The first
    /// registered backend becomes the default unless <c>FABRICATOR_DEFAULT_PROVIDER</c> overrides it.
    /// </summary>
    public static void Register(IBackend backend)
    {
        lock (Gate)
        {
            _byName ??= NewMap();
            Add(_byName, backend);
            _defaultProvider ??= Environment.GetEnvironmentVariable("FABRICATOR_DEFAULT_PROVIDER") ?? backend.Name;
        }
    }

    /// <summary>
    /// Resolves a backend by provider name or alias (case-insensitive). A null/empty provider yields the
    /// default (see <see cref="Active"/>). Throws when the provider is unknown.
    /// </summary>
    public static IBackend Resolve(string? provider)
    {
        var map = Map();
        if (string.IsNullOrWhiteSpace(provider))
        {
            return Default(map);
        }
        if (map.TryGetValue(provider.Trim(), out var backend))
        {
            return backend;
        }
        var known = string.Join(", ", map.Values.Select(b => b.Name).Distinct(StringComparer.OrdinalIgnoreCase));
        throw new ArgumentException($"fabricator: unknown provider '{provider}'. Registered providers: {known}.");
    }

    /// <summary>
    /// The default backend, for call sites that don't yet carry a provider (the sole registered backend,
    /// or the one named by <c>FABRICATOR_DEFAULT_PROVIDER</c> / the first registered).
    /// </summary>
    public static IBackend Active => Default(Map());

    /// <summary>All distinct registered backends — for host-side enumeration (e.g. listing every provider's
    /// declared settings at load).</summary>
    public static IEnumerable<IBackend> All() => Map().Values.Distinct();

    private static IBackend Default(Dictionary<string, IBackend> map)
    {
        if (_defaultProvider != null && map.TryGetValue(_defaultProvider, out var named))
        {
            return named;
        }
        // Exactly one backend (the common single-provider case) => it. Otherwise the first by name.
        return map.Values.Distinct().First();
    }

    private static Dictionary<string, IBackend> Map()
    {
        lock (Gate)
        {
            return _byName ??= Discover();
        }
    }

    private static Dictionary<string, IBackend> NewMap() => new(StringComparer.OrdinalIgnoreCase);

    private static void Add(Dictionary<string, IBackend> map, IBackend backend)
    {
        map[backend.Name] = backend;
        foreach (var alias in backend.Aliases)
        {
            if (!string.IsNullOrWhiteSpace(alias))
            {
                map[alias] = backend;
            }
        }
    }

    private static Dictionary<string, IBackend> Discover()
    {
        var map = NewMap();
        var names = Environment.GetEnvironmentVariable("FABRICATOR_BACKEND_ASSEMBLY");
        if (string.IsNullOrWhiteSpace(names))
        {
            // Default to every shipped provider; a missing/unloadable assembly is skipped below, so listing
            // Fabricator.AnalysisServices here is harmless when only the SqlServer provider is published.
            //
            // ⚠ ORDER IS LOAD-BEARING, and Fabricator.Delta must stay LAST. Until 2026-08-18 the Delta
            // provider lived in this assembly and was registered by hand AFTER this loop, with a comment
            // saying it went there so a scanned provider stayed the default. That was not decoration:
            // Default() falls through to map.Values.Distinct().First(), i.e. Dictionary INSERTION order, so
            // whichever provider is registered first becomes the default for every call site that carries no
            // provider name. Delta is now discovered like the others, so its POSITION IN THIS STRING is what
            // preserves that — prepend it and SqlServer silently stops being the default.
            names = "Fabricator.SqlServer,Fabricator.AnalysisServices,Fabricator.DeltaRs,Fabricator.Delta";
        }
        foreach (var assemblyName in names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var assembly = System.Reflection.Assembly.Load(assemblyName);
                RegisterBackendsFrom(assembly, map);
            }
            catch
            {
                // Assembly missing/unloadable — skip it; fall back to the stub below if nothing registered.
            }
        }
        ScanPluginDirectories(map);
        if (map.Count == 0)
        {
            Add(map, new StubBackend());
        }
        return map;
    }

    private static void RegisterBackendsFrom(System.Reflection.Assembly assembly, Dictionary<string, IBackend> map)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (!type.IsAbstract && typeof(IBackend).IsAssignableFrom(type) &&
                type.GetConstructor(Type.EmptyTypes) != null)
            {
                var backend = (IBackend)Activator.CreateInstance(type)!;
                Add(map, backend);
                _defaultProvider ??= Environment.GetEnvironmentVariable("FABRICATOR_DEFAULT_PROVIDER") ?? backend.Name;
            }
        }
    }

    // Third-party plugin discovery (docs/plugin-system.md). FABRICATOR_PLUGIN_DIR is a comma-separated list of
    // folders; every assembly in each is loaded into the DEFAULT context (no AssemblyLoadContext isolation —
    // deferred until a real dep conflict lands) and reflected for IBackend, whose backends + global functions
    // register like the built-in providers. A plugin references Fabricator.Bridge + Apache.Arrow (host-provided,
    // not copied), so its IBackend resolves to the default-context one and IsAssignableFrom works. Plugins must
    // align their full dependency closure with the host (Apache.Arrow especially) — there is no version
    // isolation without ALC.
    private static void ScanPluginDirectories(Dictionary<string, IBackend> map)
    {
        // Every decision below is RECORDED, not just acted on: fabricator_plugins() reads this back. The scan
        // runs once per process behind the memoized map, so it cannot be replayed on demand — see the remarks
        // on PluginScanEntry for why silence here was the worst property of the plugin system.
        var report = new List<PluginScanEntry>();
        var roots = PluginPaths.ResolveRoots();
        var existing = new List<string>();
        foreach (var root in roots)
        {
            if (!System.IO.Directory.Exists(root))
            {
                // Recorded rather than filtered away. A configured-but-absent root is the most common real
                // cause of "my plugin is not found", and it used to be invisible.
                report.Add(new PluginScanEntry(root, string.Empty, PluginScanStatus.RootMissing, string.Empty,
                                               "directory does not exist"));
                continue;
            }
            existing.Add(root);
        }
        if (existing.Count == 0)
        {
            PluginPaths.SetReport(report);
            return;
        }
        // Load plugins into the BRIDGE's own ALC, not Default: hostfxr loads the bridge into a non-default
        // context, so a plugin must be loaded into that same context for its Fabricator.Bridge / Apache.Arrow
        // references to resolve to the RUNNING bridge (else it binds to a separate copy and its IBackend is a
        // different, non-assignable type). The same ALC's loaded assemblies are the shared set to skip.
        var host = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(BackendRegistry).Assembly)
                   ?? System.Runtime.Loader.AssemblyLoadContext.Default;
        // The resolver gets every DIRECTORY that holds a candidate, not just the roots: with the recursive
        // search below, a plugin's private dependencies sit next to it several levels down, and a resolver
        // pointed only at the root would not find them.
        var candidatesByRoot = existing.ToDictionary(r => r, PluginPaths.EnumerateCandidates, StringComparer.OrdinalIgnoreCase);
        var probeDirs = candidatesByRoot.Values
            .SelectMany(list => list)
            .Select(System.IO.Path.GetDirectoryName)
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
        InstallPluginResolver(host, probeDirs.Length > 0 ? probeDirs : existing.ToArray());
        // Skip assemblies already loaded in the host context (the shared set — Fabricator.Bridge, Apache.Arrow,
        // the built-in providers): reflecting a plugin-dir copy of Fabricator.Bridge would otherwise re-register
        // its StubBackend. So only genuinely-new assemblies (the plugin entry + its private deps) are loaded.
        var loaded = new HashSet<string>(
            host.Assemblies.Select(a => a.GetName().Name).Where(n => n != null)!,
            StringComparer.OrdinalIgnoreCase);
        foreach (var root in existing)
        {
            var candidates = candidatesByRoot[root];
            report.Add(new PluginScanEntry(root, string.Empty, PluginScanStatus.Root, string.Empty,
                                           $"{candidates.Count} candidate assembly file(s)"));
            foreach (var dll in candidates)
            {
                if (loaded.Contains(System.IO.Path.GetFileNameWithoutExtension(dll)))
                {
                    report.Add(new PluginScanEntry(root, dll, PluginScanStatus.Shared, string.Empty,
                                                   "an assembly of this name is already loaded by the host"));
                    continue;
                }
                try
                {
                    var assembly = host.LoadFromAssemblyPath(System.IO.Path.GetFullPath(dll));
                    // Count what THIS assembly added, so "loaded" and "loaded but contributed nothing" are
                    // distinguishable. The second is the ordinary state of a plugin's private dependency and
                    // must not read as a failure.
                    var before = new HashSet<IBackend>(map.Values);
                    RegisterBackendsFrom(assembly, map);
                    var added = map.Values.Distinct().Where(b => !before.Contains(b)).Select(b => b.Name).ToArray();
                    report.Add(added.Length > 0
                        ? new PluginScanEntry(root, dll, PluginScanStatus.Loaded, string.Join(",", added),
                                              $"{added.Length} provider(s)")
                        : new PluginScanEntry(root, dll, PluginScanStatus.NoBackend, string.Empty,
                                              "loaded, but declares no IBackend"));
                }
                catch (Exception ex)
                {
                    // Not a plugin entry, a native dll, or a managed one whose references do not resolve
                    // against the RUNNING bridge (a different Apache.Arrow major is the classic). Recorded
                    // with the reason: this row is the whole point of the report.
                    report.Add(new PluginScanEntry(root, dll, PluginScanStatus.Rejected, string.Empty,
                                                   $"{ex.GetType().Name}: {ex.Message}"));
                }
            }
        }
        PluginPaths.SetReport(report);
    }

    private static bool _pluginResolverInstalled;

    // Resolve a plugin's transitive deps from the plugin folders (probing the bridge's own context — no
    // isolation, so first-found wins across plugins; that's the documented trade vs. an ALC per plugin).
    private static void InstallPluginResolver(System.Runtime.Loader.AssemblyLoadContext host, string[] dirs)
    {
        lock (Gate)
        {
            if (_pluginResolverInstalled || dirs.Length == 0)
            {
                return;
            }
            _pluginResolverInstalled = true;
            host.Resolving += (ctx, name) =>
            {
                foreach (var dir in dirs)
                {
                    var candidate = System.IO.Path.Combine(dir, name.Name + ".dll");
                    if (System.IO.File.Exists(candidate))
                    {
                        try
                        {
                            return ctx.LoadFromAssemblyPath(candidate);
                        }
                        catch
                        {
                            // keep probing other dirs
                        }
                    }
                }
                return null;
            };
        }
    }
}
