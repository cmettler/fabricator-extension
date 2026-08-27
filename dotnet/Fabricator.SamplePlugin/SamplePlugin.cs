// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

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

    public IEnumerable<ITableFunction> GlobalTableFunctions =>
        new ITableFunction[] { new PlugSlowRangeFunction(), new PlugSlowRange2Function() };

    public IEnumerable<ILateralTableFunction> GlobalLateralFunctions =>
        new ILateralTableFunction[] { new PlugLatSlowFunction() };

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

/// <summary>
/// The SOURCE-side twin of <see cref="PlugSleepFunction"/>: <c>plug_slow_range(rows, millis)</c> yields
/// <paramref name="rows"/> rows of a single BIGINT <c>id</c> in 2048-row batches, sleeping <c>millis</c>
/// before each batch. It exists to put the cost on the other side of the scan boundary, and the pair is what
/// makes docs/scan-concurrency.md §1 measurable instead of merely read out of the source:
/// <list type="bullet">
/// <item>a scalar in the PROJECTION is per-thread work, so it scales with the thread count once the plan's
/// sink is parallel;</item>
/// <item>a sleep in this SOURCE is inside <c>get_next</c>, which the host pulls under one mutex — so it must
/// NOT scale, at any thread count. That is the invariant, not a defect: the pull is serialized precisely so
/// a provider's reader (a <c>SqlDataReader</c>, say) is never touched from two threads.</item>
/// </list>
/// <para>⚠ The same ~15 ms Windows timer floor applies (see <see cref="PlugSleepFunction"/>), so ask for
/// values well above it. A batch is the unit of sleeping here, not a row, which is what keeps a measurement
/// affordable — 4 batches x 500 ms is 2 s, where per-row sleeping over the same 8192 rows could not be run
/// at all.</para>
/// </summary>
internal class PlugSlowRangeFunction : ITableFunction
{
    internal const int BatchRows = 2048;

    public virtual string Name => "plug_slow_range";

    public Schema Parameters => new(
        new[]
        {
            new Field("rows", Int64Type.Default, nullable: true),
            new Field("millis", Int64Type.Default, nullable: true),
        },
        metadata: null);

    internal static void Trace(string what)
    {
        if (Environment.GetEnvironmentVariable("PLUG_TRACE") == "1")
        {
            Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] tid={Environment.CurrentManagedThreadId} {what}");
        }
    }

    public ITableFunctionBinding Bind(RecordBatch args)
    {
        Trace($"{Name} BIND");
        long rows = ReadArg(args, 0);
        long millis = ReadArg(args, 1);
        if (rows < 0 || millis < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args), "plug_slow_range: rows and millis must both be >= 0.");
        }
        return new Binding(rows, millis, $"{Name}({rows})");
    }

    private static long ReadArg(RecordBatch args, int i)
    {
        var col = (Int64Array)args.Column(i);
        return col.IsNull(0) ? 0 : col.GetValue(0)!.Value;
    }

    private sealed class Binding : ITableFunctionBinding
    {
        private readonly long _rows;
        private readonly long _millis;
        private readonly string _tag;

        internal Binding(long rows, long millis, string tag = "?")
        {
            _rows = rows;
            _millis = millis;
            _tag = tag;
        }

        public Schema OutputSchema =>
            new(new[] { new Field("id", Int64Type.Default, nullable: false) }, metadata: null);

        // Neither is claimed: this function computes its rows and ignores the pushed spec entirely, so DuckDB
        // re-applies the filter and maps the full declared schema. Claiming either would be a wrong ANSWER,
        // not a missed optimisation.
        public bool SupportsFilterPushdown => false;
        public bool SupportsProjectionPushdown => false;

        public async IAsyncEnumerable<RecordBatch> Execute(
            TableFunctionScan scan,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var schema = OutputSchema;
            Trace($"{_tag} EXECUTE-ENTER");
            for (long start = 0; start < _rows; start += BatchRows)
            {
                ct.ThrowIfCancellationRequested();
                if (_millis > 0)
                {
                    Trace($"{_tag} SLEEP-BEGIN start={start}");
                    // The cost sits HERE, inside the batch the host is pulling — see the class remarks.
                    Thread.Sleep((int)Math.Min(_millis, int.MaxValue));
                    Trace($"{_tag} SLEEP-END   start={start}");
                }
                int n = (int)Math.Min(BatchRows, _rows - start);
                var b = new Int64Array.Builder().Reserve(n);
                for (int i = 0; i < n; i++)
                {
                    b.Append(start + i);
                }
                yield return new RecordBatch(schema, new IArrowArray[] { b.Build() }, n);
            }
            await Task.CompletedTask; // the body is synchronous; this keeps it an async iterator
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// A SECOND, independent table function with identical behaviour and a different NAME —
/// <c>plug_slow_range2(rows, millis)</c>. It exists for one measurement and would otherwise be pure
/// duplication: whether two managed table-function scans serialize because they are the SAME function or
/// because they are both managed scans. That question cannot be answered with one table function, and it must
/// NOT be answered with a scalar stand-in — a scalar is expression evaluation, not a scan, so it changes the
/// very thing under test. See docs/scan-concurrency.md §5b.
/// </summary>
internal sealed class PlugSlowRange2Function : PlugSlowRangeFunction
{
    public override string Name => "plug_slow_range2";
}

/// <summary>
/// A plugin-authored ROW-MAPPED (correlated LATERAL) function, and the INSTRUMENT that makes the batching
/// claim measurable: <c>plug_lat_slow(n, millis)</c> sleeps <c>millis</c> ONCE PER CALL — not per row — and
/// returns <c>(squared, batch_rows)</c>, one row per input row.
/// </summary>
/// <remarks>
/// <para>
/// It exists because a per-CALL cost is the only thing batching can amortise, and it is the shape this
/// function kind is FOR: a REST call, a model invocation, a per-row query. With it the win stops being an
/// argument and becomes a ratio between two legs of one suite — N distinct outer rows cost N sleeps on
/// DuckDB's row-by-row driver and about one on the batched operator.
/// </para>
/// <para>
/// ⚠ It also proves the PLUGIN path: a plugin references <c>Fabricator.Abstractions</c> and nothing else, so
/// declaring one of these must need no more than declaring a scalar. <c>batch_rows</c> is what the suite reads
/// to tell the two paths apart without any logging (docs/lateral_unnest_analysis.md §8.4 — duckdb_logs flushes
/// too lazily to COUNT).
/// </para>
/// <para>
/// ⚠ On Windows the sleep floor is the timer tick, ~15 ms, so a small <c>millis</c> is a lie by an order of
/// magnitude. Use >= 50. A NEGATIVE argument is refused rather than passed to Thread.Sleep, where -1 means
/// Timeout.Infinite — in a suite a hang is the one failure worse than a failure.
/// </para>
/// </remarks>
internal sealed class PlugLatSlowFunction : ILateralTableFunction
{
    public string Name => "plug_lat_slow";

    // BOTH POSITIONAL — these two ARE the per-row input columns. `millis` is deliberately not a NAMED
    // parameter: a named argument cannot be written in the CORRELATED call shape at all (a DuckDB limitation,
    // docs/duckdb-upstream-issues.md §5), so a named cost arg would make this instrument unusable for exactly
    // the measurement it exists for.
    public Schema Parameters => new(new[]
    {
        Params.Positional("n", Int32Type.Default),
        Params.Positional("millis", Int32Type.Default),
    }, metadata: null);

    public ILateralBinding Bind(RecordBatch? args, Schema inputSchema) => new Binding();

    private sealed class Binding : ILateralBinding
    {
        public Schema OutputSchema => new(new[]
        {
            new Field("squared", Int64Type.Default, nullable: true),
            new Field("batch_rows", Int32Type.Default, nullable: false),
        }, metadata: null);

        public ILateralSession Open() => new Session(OutputSchema);

        public void Dispose() { }
    }

    private sealed class Session : ILateralSession
    {
        private readonly Schema _output;

        public Session(Schema output) => _output = output;

        public LateralResult Call(RecordBatch input)
        {
            var n = (Int32Array)input.Column(0);
            var millis = (Int32Array)input.Column(1);
            int wait = input.Length > 0 && !millis.IsNull(0) ? millis.Values[0] : 0;
            if (wait < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(input), "plug_lat_slow: millis must be >= 0 (a negative value is Timeout.Infinite).");
            }
            Thread.Sleep(wait); // ONCE per call — the whole point
            var sq = new Int64Array.Builder().Reserve(input.Length);
            var rows = new Int32Array.Builder().Reserve(input.Length);
            for (int r = 0; r < input.Length; r++)
            {
                if (n.IsNull(r)) { sq.AppendNull(); } else { sq.Append((long)n.Values[r] * n.Values[r]); }
                rows.Append(input.Length);
            }
            // No Origin: exactly one output row per input row, in order (the identity map).
            return new LateralResult(
                new RecordBatch(_output, new IArrowArray[] { sq.Build(), rows.Build() }, input.Length));
        }

        public void Dispose() { }
    }
}
