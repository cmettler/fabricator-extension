using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// Global functions contributed by the HOST itself rather than by a provider.
/// </summary>
/// <remarks>
/// Every other global function comes from an <see cref="IBackend"/> — <see cref="GlobalFunctions"/> builds its
/// maps by walking <see cref="BackendRegistry.All"/>. That is right for anything provider-shaped, and wrong
/// for a diagnostic ABOUT the provider machinery: <c>fabricator_plugins()</c> has to answer "which providers
/// were found, and why was this one not" — a question no single provider can answer, and which must still be
/// answerable when the answer is "none of them loaded".
/// <para>Host functions are merged FIRST, so a provider declaring a colliding name produces
/// <see cref="GlobalFunctions"/>'s existing duplicate-name error rather than silently shadowing a
/// diagnostic.</para>
/// </remarks>
internal static class HostGlobalFunctions
{
    public static IEnumerable<ITableFunction> TableFunctions { get; } = new ITableFunction[]
    {
        new PluginsFunction(),
        new InstallPluginFunction(),
        new UninstallPluginFunction(),
    };
}

/// <summary>
/// <c>SELECT * FROM fabricator_uninstall_plugin('&lt;name&gt;' [, version := …] [, root := …])</c> — takes a
/// plugin out of the scan, and reclaims its bytes when it can.
/// </summary>
/// <remarks>
/// One row per installed version it acted on, so uninstalling a plugin with three versions says what happened
/// to each rather than collapsing to a single boolean. Omitting <c>version</c> removes them ALL — deliberate,
/// because the common intent is "get rid of this plugin", and the per-version form is there for the case where
/// it is not.
/// </remarks>
internal sealed class UninstallPluginFunction : ITableFunction
{
    public string Name => "fabricator_uninstall_plugin";

    public Schema Parameters { get; } = new(new[]
    {
        Params.Positional("name", StringType.Default),
        // Omitted => every installed version.
        Params.Named("version", StringType.Default),
        Params.Named("root", StringType.Default),
    }, metadata: null);

    public ITableFunctionBinding Bind(RecordBatch args) =>
        new Binding(ReadString(args, 0) ?? string.Empty, ReadString(args, 1), ReadString(args, 2));

    private static string? ReadString(RecordBatch args, int ordinal) =>
        args.Column(ordinal) is StringArray a && a.Length > 0 && !a.IsNull(0) ? a.GetString(0) : null;

    private sealed class Binding : ITableFunctionBinding
    {
        private readonly string _name;
        private readonly string? _version;
        private readonly string? _root;

        public Binding(string name, string? version, string? root)
        {
            _name = name;
            _version = version;
            _root = root;
        }

        public Schema OutputSchema { get; } = new(new[]
        {
            new Field("name", StringType.Default, nullable: false),
            new Field("version", StringType.Default, nullable: false),
            new Field("path", StringType.Default, nullable: false),
            // Out of the scan. FALSE is the only real failure — it means the plugin is still discoverable.
            new Field("removed", BooleanType.Default, nullable: false),
            // Bytes reclaimed. FALSE is ORDINARY: a loaded assembly cannot be deleted, so it is swept later.
            new Field("purged", BooleanType.Default, nullable: false),
            new Field("detail", StringType.Default, nullable: false),
        }, metadata: null);

        public bool SupportsFilterPushdown => false;
        public bool SupportsProjectionPushdown => false;

        public void Dispose()
        {
        }

        // Same contract as the installer: dispose in a PLAIN method, and capture the ambients HERE — the
        // iterator body runs at the first batch pull, a different crossing, where AmbientOpener and the
        // settings session are gone. Reading the opt-in there would make an enabled function report itself
        // disabled, non-deterministically. Measured on the install path; not repeated here.
        public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
        {
            scan.FilterValues?.Dispose();
            return Rows(AmbientOpener.Current, ProviderSettingsStore.CurrentSession, ct);
        }

        private async IAsyncEnumerable<RecordBatch> Rows(nint opener, long session,
                                                        [EnumeratorCancellation] CancellationToken ct)
        {
            AmbientOpener.Current = opener;
            ProviderSettingsStore.CurrentSession = session;
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            var rows = PluginInstall.Uninstall(_name, _version, _root);
            var name = new StringArray.Builder();
            var version = new StringArray.Builder();
            var path = new StringArray.Builder();
            var removed = new BooleanArray.Builder();
            var purged = new BooleanArray.Builder();
            var detail = new StringArray.Builder();
            foreach (var r in rows)
            {
                name.Append(r.Name);
                version.Append(r.Version);
                path.Append(r.Path);
                removed.Append(r.Removed);
                purged.Append(r.Purged);
                detail.Append(r.Detail);
            }
            yield return new RecordBatch(OutputSchema, new IArrowArray[]
            {
                name.Build(), version.Build(), path.Build(), removed.Build(), purged.Build(), detail.Build(),
            }, rows.Count);
        }
    }
}

/// <summary>
/// <c>SELECT * FROM fabricator_install_plugin('&lt;archive.zip&gt;' [, root := …] [, replace := …])</c> —
/// unpacks a plugin archive into a plugin root and makes its PROVIDER usable in this session.
/// </summary>
/// <remarks>
/// <para>A table function rather than a scalar so it can report what it did — where it landed, how many
/// files, which providers registered, and whether they registered at all. The whole point of the scan report
/// is that "installed" and "installed and usable" are different answers, and this row carries both.</para>
/// <para>⚠ THE INSTALL HAPPENS AT EXECUTION, NEVER AT BIND. <see cref="Bind"/> copies the three argument
/// values and does nothing else: a bind may run for a statement that is then not executed (or executed
/// several times), so a side effect there is a side effect nobody asked for. Same rule as the
/// <c>delta.set_*</c> functions, and the same fixed output schema that makes it possible — the schema does
/// not depend on the archive, so binding needs to open nothing.</para>
/// </remarks>
internal sealed class InstallPluginFunction : ITableFunction
{
    public string Name => "fabricator_install_plugin";

    public Schema Parameters { get; } = new(new[]
    {
        Params.Positional("archive", StringType.Default),
        // Which plugin root to install into. Omitted => the first configured root (FABRICATOR_PLUGIN_DIR's
        // first entry, or the per-user default), i.e. the one the scan searches first.
        Params.Named("root", StringType.Default),
        // Re-install the SAME version, moving the existing directory aside. A DIFFERENT version never needs
        // it: the layout is version-stamped precisely so an upgrade writes beside the running copy.
        Params.Named("replace", BooleanType.Default),
    }, metadata: null);

    public ITableFunctionBinding Bind(RecordBatch args)
    {
        string archive = ReadString(args, 0) ?? string.Empty;
        string? root = ReadString(args, 1);
        bool replace = args.Column(2) is BooleanArray b && b.Length > 0 && !b.IsNull(0) && b.GetValue(0) == true;
        return new Binding(archive, root, replace);
    }

    private static string? ReadString(RecordBatch args, int ordinal) =>
        args.Column(ordinal) is StringArray a && a.Length > 0 && !a.IsNull(0) ? a.GetString(0) : null;

    private sealed class Binding : ITableFunctionBinding
    {
        private readonly string _archive;
        private readonly string? _root;
        private readonly bool _replace;

        public Binding(string archive, string? root, bool replace)
        {
            _archive = archive;
            _root = root;
            _replace = replace;
        }

        public Schema OutputSchema { get; } = new(new[]
        {
            new Field("name", StringType.Default, nullable: false),
            new Field("version", StringType.Default, nullable: false),
            // DuckDB's own platform string, asked of the engine — the directory of the archive that was taken.
            new Field("platform", StringType.Default, nullable: false),
            new Field("destination", StringType.Default, nullable: false),
            new Field("files", Int64Type.Default, nullable: false),
            // Comma-separated providers the re-scan registered from it; '' when none did.
            new Field("providers", StringType.Default, nullable: false),
            // Whether the plugin is usable NOW. False is a legitimate, informative answer, not an error.
            new Field("activated", BooleanType.Default, nullable: false),
            new Field("detail", StringType.Default, nullable: false),
        }, metadata: null);

        public bool SupportsFilterPushdown => false;
        public bool SupportsProjectionPushdown => false;

        public void Dispose()
        {
        }

        // Dispose the pushed filter values eagerly in a PLAIN method — an async-iterator body does not run
        // until the first MoveNextAsync, which is the late-release class this repo already paid for once.
        //
        // ⚠⚠ AND CAPTURE THE AMBIENTS HERE, FOR THE SAME REASON, WHICH IS NOT A DETAIL: this method runs
        // INSIDE the crossing that set them (ArrowStreamInitGlobal calls SetActiveOpener and then the factory
        // synchronously), while the iterator body runs at the first BATCH PULL — a separate crossing, on
        // whatever thread DuckDB pulls from. AmbientOpener/CurrentSession are AsyncLocal per crossing, so the
        // iterator can legitimately see 0.
        // MEASURED, and it is NON-DETERMINISTIC, which is what makes it dangerous: reading the session inside
        // the iterator, the same suite refused an install as "disabled" at the THIRD call in one build and the
        // FOURTH in another — because session 0 falls back to the GLOBAL settings layer, where the registration
        // default (false) sits, so an enabled function silently reports itself disabled. It passed the first
        // time it was run. Same capture-in-Execute shape as DeltaGlobalTableFunction, and the same reason.
        public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
        {
            scan.FilterValues?.Dispose();
            return Rows(AmbientOpener.Current, ProviderSettingsStore.CurrentSession, ct);
        }

        private async IAsyncEnumerable<RecordBatch> Rows(nint opener, long session,
                                                         [EnumeratorCancellation] CancellationToken ct)
        {
            // Re-establish what Execute captured: the install reads a SESSION-scoped setting (the opt-in) and
            // asks the host for DuckDB's platform string, and both need this operator's context.
            AmbientOpener.Current = opener;
            ProviderSettingsStore.CurrentSession = session;
            ct.ThrowIfCancellationRequested();
            var r = await PluginInstall.InstallAsync(_archive, _root, _replace).ConfigureAwait(false);
            var name = new StringArray.Builder();
            var version = new StringArray.Builder();
            var platform = new StringArray.Builder();
            var destination = new StringArray.Builder();
            var files = new Int64Array.Builder();
            var providers = new StringArray.Builder();
            var activated = new BooleanArray.Builder();
            var detail = new StringArray.Builder();
            name.Append(r.Name);
            version.Append(r.Version);
            platform.Append(r.Platform);
            destination.Append(r.Destination);
            files.Append(r.Files);
            providers.Append(r.Providers);
            activated.Append(r.Activated);
            detail.Append(r.Detail);
            yield return new RecordBatch(OutputSchema, new IArrowArray[]
            {
                name.Build(), version.Build(), platform.Build(), destination.Build(),
                files.Build(), providers.Build(), activated.Build(), detail.Build(),
            }, 1);
        }
    }
}

/// <summary>
/// <c>SELECT * FROM fabricator_plugins()</c> — what the plugin scan looked at and what it decided, one row
/// per configured root plus one per candidate assembly.
/// </summary>
/// <remarks>
/// It reports the RECORDED scan, never a fresh one: the scan happens once per process behind
/// <see cref="BackendRegistry"/>'s memoized map, and re-running it would both lie about when it happened and
/// risk loading assemblies a second time. By the time this is callable the scan has necessarily run —
/// registering this very function enumerates the global functions, which walks the backend registry.
/// </remarks>
internal sealed class PluginsFunction : ITableFunction
{
    public string Name => "fabricator_plugins";

    /// <summary>No parameters. A zero-field parameter schema is supported since the empty-schema export fix;
    /// the host passes no args stream at all for an argument-less table function.</summary>
    public Schema Parameters { get; } = new(System.Array.Empty<Field>(), metadata: null);

    public ITableFunctionBinding Bind(RecordBatch args) => new Binding();

    private sealed class Binding : ITableFunctionBinding
    {
        public Schema OutputSchema { get; } = new(new[]
        {
            // The configured root this row belongs to (absolute).
            new Field("root", StringType.Default, nullable: false),
            // The candidate assembly, or '' for a root-level row.
            new Field("path", StringType.Default, nullable: false),
            // root | root_missing | loaded | no_backend | shared | rejected — see PluginScanStatus.
            new Field("status", StringType.Default, nullable: false),
            // Comma-separated provider names this assembly registered ('' unless status='loaded').
            new Field("provider", StringType.Default, nullable: false),
            // Why: the candidate count, the rejection's exception message, or the skip reason.
            new Field("detail", StringType.Default, nullable: false),
        }, metadata: null);

        public bool SupportsFilterPushdown => false;
        public bool SupportsProjectionPushdown => false;

        // Nothing to release: the binding holds no stream, handle or unmanaged buffer — it reads a recorded
        // in-memory list. Stated rather than left blank so it is clear this is "owns nothing", not "forgot".
        public void Dispose()
        {
        }

        // Dispose eagerly in a PLAIN method, then delegate to the iterator. An async-iterator body does not
        // start until the first MoveNextAsync, so disposing inside it would leave the pushed filter values
        // alive past the call — the late-release class this repo already paid for once (macOS SIGABRT).
        public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
        {
            scan.FilterValues?.Dispose();
            return Rows(ct);
        }

        private async IAsyncEnumerable<RecordBatch> Rows([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            var report = PluginPaths.Report();
            var root = new StringArray.Builder();
            var path = new StringArray.Builder();
            var status = new StringArray.Builder();
            var provider = new StringArray.Builder();
            var detail = new StringArray.Builder();
            foreach (var e in report)
            {
                ct.ThrowIfCancellationRequested();
                root.Append(e.Root);
                path.Append(e.Path);
                status.Append(e.Status);
                provider.Append(e.Provider);
                detail.Append(e.Detail);
            }
            // An EMPTY report is a legitimate answer (no root configured and no default home), so a zero-row
            // batch is still emitted rather than nothing — a consumer must be able to tell "scan found no
            // roots" from "the function did not run".
            yield return new RecordBatch(OutputSchema,
                new IArrowArray[] { root.Build(), path.Build(), status.Build(), provider.Build(), detail.Build() },
                report.Count);
        }
    }
}
