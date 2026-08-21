using Apache.Arrow;
using Apache.Arrow.Types;
using Fabricator.Bridge;

namespace Fabricator.SamplePlugin;

/// <summary>
/// A sample third-party plugin backend. It exposes no catalog (ATTACH throws) — it exists purely to contribute
/// connection-free GLOBAL functions, demonstrating that a plugin dropped into an <c>FABRICATOR_PLUGIN_DIR</c>
/// folder is discovered, its <see cref="IBackend"/> registered, and its global functions surfaced — with no
/// change to the bridge or any ABI. See docs/plugin-system.md.
/// <para>Two scalars, for two different jobs: <see cref="PlugGreetFunction"/> proves the plumbing carries
/// VALUES, and <see cref="PlugSleepFunction"/> is a test INSTRUMENT — it makes a query's cost a number the
/// caller chose, which is what lets a test measure effective parallelism or park a query inside a long
/// managed call on purpose.</para>
/// </summary>
public sealed class SamplePluginBackend : IBackend
{
    public string Name => "sampleplugin";

    public IEnumerable<IScalarFunction> GlobalScalarFunctions =>
        new IScalarFunction[] { new PlugGreetFunction(), new PlugSleepFunction() };

    /// <summary>
    /// Macros a plugin ships — proving the SQL-template path needs no more from a plugin than the function path
    /// (declare; the host parses + registers at load). The second entry is DELIBERATELY MALFORMED: it pins the
    /// contract that a broken provider macro is SKIPPED with a warning and does NOT block its sibling (or the
    /// extension). It is why loading this sample plugin prints one macro warning at load.
    /// </summary>
    public IEnumerable<MacroDefinition> GlobalMacros => new MacroDefinition[]
    {
        new MacroDefinition("plug_double", "CREATE MACRO plug_double(x) AS x * 2"),
        new MacroDefinition("plug_bad_macro", "CREATE MACRO plug_bad_macro(x) AS x +"),
    };

    public string BuildConnectionString(string secretType, IReadOnlyDictionary<string, string> fields,
                                        string baseConnString) =>
        throw new NotSupportedException("sampleplugin: global functions only (no catalog).");

    public IBackendCatalog OpenCatalog(string connectionString, string optionsJson) =>
        throw new NotSupportedException("sampleplugin: global functions only (no catalog).");
}

/// <summary>A connection-free global scalar: <c>plug_greet(name) -&gt; 'Hello, &lt;name&gt; (from plugin)'</c>.
/// Authored against the shared <see cref="IScalarFunction"/> contract; runs over Apache.Arrow like any other.</summary>
internal sealed class PlugGreetFunction : IScalarFunction
{
    public string Name => "plug_greet";
    public Schema Parameters => new(new[] { new Field("name", StringType.Default, nullable: true) }, metadata: null);
    public Field Result => new("greeting", StringType.Default, nullable: true);

    public IArrowArray Invoke(RecordBatch args)
    {
        var names = (StringArray)args.Column(0);
        var b = new StringArray.Builder().Reserve(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            b.Append(names.IsNull(i) ? "Hello, stranger (from plugin)" : $"Hello, {names.GetString(i)} (from plugin)");
        }
        return b.Build();
    }
}

/// <summary>
/// A deliberately SLOW global scalar: <c>plug_sleep(millis) -&gt; millis</c>, blocking the calling thread once
/// PER ROW (a NULL row sleeps not at all and returns NULL).
/// <para><b>WHAT IT IS FOR: it makes time a CONTROLLED variable, so effective parallelism becomes a
/// MEASUREMENT rather than an inference.</b> A scan's wall clock under it is
/// <c>rows x millis / threads actually used</c>, so
/// <c>SELECT count(plug_sleep(50)) FROM range(80)</c> at <c>SET threads=8</c> answers "how many cores did that
/// pipeline really get" with arithmetic instead of a profiler — which is exactly the question
/// docs/scan-concurrency.md exists to answer. The md5-per-row probe those numbers were first taken with
/// approximates this with a cost that itself varies by input and by machine; a sleep does not.</para>
/// <para><b>⚠ ON WINDOWS THE FLOOR IS THE SYSTEM TIMER TICK, ~15 ms — MEASURED, and it makes a small
/// argument a lie.</b> 100 rows at <c>plug_sleep(1)</c> cost 1913 ms against a 343 ms baseline, i.e. ~15 ms
/// per row: <see cref="Thread.Sleep(int)"/> rounds up to the scheduler's resolution (~15.6 ms by default).
/// Ask for values comfortably above the tick. It also means per-ROW sleeping is impractical as a scan probe —
/// a morsel is 2048 rows, so even 1 ms per row is ~31 s per morsel; sleep once per MORSEL instead
/// (<c>CASE WHEN id % 2048 = 0 THEN 500 ELSE 0 END</c>, the recipe measured in docs/scan-concurrency.md §6).
/// Not worked around here: raising the resolution is a process-wide <c>timeBeginPeriod</c> call, which a test
/// helper has no business making on its host's behalf.</para>
/// <para><b>⚠ IT MUST STAY VOLATILE</b> (the default — see <see cref="IScalarFunction.IsVolatile"/>). A
/// CONSISTENT function over a constant argument is folded to a literal at PLAN time, so it would sleep ONCE
/// during binding and never again — and a parallelism measurement built on it would report a confident wrong
/// answer while looking perfectly healthy.</para>
/// <para><b>⚠ IT BLOCKS THE DuckDB WORKER THREAD it runs on, and that is the point</b> — occupying a worker is
/// what makes the wall clock proportional to work/threads. It follows that N rows in flight occupy N workers,
/// so a sleep long enough to fill the pool starves every other operator in the plan.</para>
/// <para><b>⚠ IT IS NOT INTERRUPTIBLE, DELIBERATELY, AND THERE IS NO CAP ON THE ARGUMENT.</b> A plugin
/// references only the contract assembly, which exposes no interrupt surface, so the sleep runs to completion;
/// that makes this the reproducible test case for the cancellation tiers (docs/cancellation.md), whose whole
/// subject is a query parked inside one long-blocking managed call. Capping the argument would silently make
/// the function stop being the thing under test.</para>
/// <para><b>⚠ A NEGATIVE argument is REFUSED rather than clamped, because <c>Thread.Sleep(-1)</c> is
/// <c>Timeout.Infinite</c></b> — in a test suite a hang is the one failure mode worse than a wrong answer, and
/// clamping to zero would instead hide the caller's arithmetic bug.</para>
/// </summary>
internal sealed class PlugSleepFunction : IScalarFunction
{
    public string Name => "plug_sleep";

    public Schema Parameters =>
        new(new[] { new Field("millis", Int64Type.Default, nullable: true) }, metadata: null);

    public Field Result => new("slept_millis", Int64Type.Default, nullable: true);

    public IArrowArray Invoke(RecordBatch args)
    {
        var millis = (Int64Array)args.Column(0);
        var b = new Int64Array.Builder().Reserve(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            if (millis.IsNull(i))
            {
                b.AppendNull(); // NULL is the no-op row: nothing to wait for, nothing to report
                continue;
            }
            long ms = millis.GetValue(i)!.Value;
            if (ms < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(args), ms, "plug_sleep: millis must be >= 0 (a negative sleep would never return).");
            }
            if (ms > 0)
            {
                // int.MaxValue ms is ~24 days; the clamp exists so the cast cannot wrap into a negative
                // (i.e. into Timeout.Infinite), never as a policy about how long a caller may wait.
                Thread.Sleep((int)Math.Min(ms, int.MaxValue));
            }
            b.Append(ms);
        }
        return b.Build();
    }
}
