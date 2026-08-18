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
    };
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
