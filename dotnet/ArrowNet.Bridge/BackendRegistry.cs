namespace ArrowNet.Bridge;

/// <summary>
/// Process-wide registry of backends, **keyed by provider name** so one binary can host several
/// providers (SQL Server, later Power BI/DAX, …). On first use it loads the provider assemblies named
/// by <c>ARROWNET_BACKEND_ASSEMBLY</c> (comma-separated; default <c>ArrowNet.SqlServer</c>), finds every
/// <see cref="IBackend"/> implementation, and registers each under its <see cref="IBackend.Name"/> +
/// <see cref="IBackend.Aliases"/> (case-insensitive). If none are found it falls back to
/// <see cref="StubBackend"/> so the bridge still works standalone.
/// <para>
/// <see cref="Resolve"/> picks a backend by provider name; <see cref="Active"/> returns the default
/// (the sole backend, or the one named by <c>ARROWNET_DEFAULT_PROVIDER</c>) for call sites that don't
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
    /// registered backend becomes the default unless <c>ARROWNET_DEFAULT_PROVIDER</c> overrides it.
    /// </summary>
    public static void Register(IBackend backend)
    {
        lock (Gate)
        {
            _byName ??= NewMap();
            Add(_byName, backend);
            _defaultProvider ??= Environment.GetEnvironmentVariable("ARROWNET_DEFAULT_PROVIDER") ?? backend.Name;
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
        throw new ArgumentException($"mssql_net: unknown provider '{provider}'. Registered providers: {known}.");
    }

    /// <summary>
    /// The default backend, for call sites that don't yet carry a provider (the sole registered backend,
    /// or the one named by <c>ARROWNET_DEFAULT_PROVIDER</c> / the first registered).
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
        var names = Environment.GetEnvironmentVariable("ARROWNET_BACKEND_ASSEMBLY");
        if (string.IsNullOrWhiteSpace(names))
        {
            // Default to every shipped provider; a missing/unloadable assembly is skipped below, so listing
            // ArrowNet.AnalysisServices here is harmless when only the SqlServer provider is published.
            names = "ArrowNet.SqlServer,ArrowNet.AnalysisServices";
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
                _defaultProvider ??= Environment.GetEnvironmentVariable("ARROWNET_DEFAULT_PROVIDER") ?? backend.Name;
            }
        }
    }

    // Third-party plugin discovery (docs/plugin-system.md). ARROWNET_PLUGIN_DIR is a comma-separated list of
    // folders; every assembly in each is loaded into the DEFAULT context (no AssemblyLoadContext isolation —
    // deferred until a real dep conflict lands) and reflected for IBackend, whose backends + global functions
    // register like the built-in providers. A plugin references ArrowNet.Bridge + Apache.Arrow (host-provided,
    // not copied), so its IBackend resolves to the default-context one and IsAssignableFrom works. Plugins must
    // align their full dependency closure with the host (Apache.Arrow especially) — there is no version
    // isolation without ALC.
    private static void ScanPluginDirectories(Dictionary<string, IBackend> map)
    {
        var dirsEnv = Environment.GetEnvironmentVariable("ARROWNET_PLUGIN_DIR");
        if (string.IsNullOrWhiteSpace(dirsEnv))
        {
            return;
        }
        var dirs = dirsEnv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .Where(System.IO.Directory.Exists)
                          .Select(System.IO.Path.GetFullPath)
                          .ToArray();
        // Load plugins into the BRIDGE's own ALC, not Default: hostfxr loads the bridge into a non-default
        // context, so a plugin must be loaded into that same context for its ArrowNet.Bridge / Apache.Arrow
        // references to resolve to the RUNNING bridge (else it binds to a separate copy and its IBackend is a
        // different, non-assignable type). The same ALC's loaded assemblies are the shared set to skip.
        var host = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(BackendRegistry).Assembly)
                   ?? System.Runtime.Loader.AssemblyLoadContext.Default;
        InstallPluginResolver(host, dirs); // so a plugin's private transitive deps resolve from its own folder
        // Skip assemblies already loaded in the host context (the shared set — ArrowNet.Bridge, Apache.Arrow,
        // the built-in providers): reflecting a plugin-dir copy of ArrowNet.Bridge would otherwise re-register
        // its StubBackend. So only genuinely-new assemblies (the plugin entry + its private deps) are loaded.
        var loaded = new HashSet<string>(
            host.Assemblies.Select(a => a.GetName().Name).Where(n => n != null)!,
            StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            foreach (var dll in System.IO.Directory.GetFiles(dir, "*.dll"))
            {
                if (loaded.Contains(System.IO.Path.GetFileNameWithoutExtension(dll)))
                {
                    continue; // shared/already-loaded — host provides it
                }
                try
                {
                    var assembly = host.LoadFromAssemblyPath(System.IO.Path.GetFullPath(dll));
                    RegisterBackendsFrom(assembly, map);
                }
                catch
                {
                    // Not a plugin entry, or a native/unloadable dll — skip.
                }
            }
        }
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
