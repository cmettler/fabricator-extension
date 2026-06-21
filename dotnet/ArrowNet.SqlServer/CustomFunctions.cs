using Apache.Arrow;
using Apache.Arrow.Types;
using ArrowNet.Bridge;

namespace ArrowNet.SqlServer;

/// <summary>
/// Provider-authored custom scalar functions, surfaced into every attached catalog alongside the
/// discovered SQL Server functions (resolved as <c>db.schema.name(args)</c>). To add one, implement
/// <see cref="ArrowScalarFunction"/> and list it here. These run entirely in C# — there need be no
/// corresponding SQL Server object.
/// </summary>
internal static class CustomFunctions
{
    public static readonly IReadOnlyList<IArrowScalarFunction> Scalar = new IArrowScalarFunction[]
    {
        new CfAddFunction(),
    };

    public static readonly IReadOnlyList<IArrowTableFunction> Table = new IArrowTableFunction[]
    {
        new CfRangeFunction(),
    };

    // Factories, not instances: a table-in-out may keep mutable state across its input stream (a running
    // aggregate), so each session gets a fresh instance (see SqlServerCatalog.InOutOpen).
    public static readonly IReadOnlyList<Func<IArrowTableInOutFunction>> InOut = new Func<IArrowTableInOutFunction>[]
    {
        () => new CfTagFunction(),
        () => new CfRunningSumFunction(),
    };
}

// Demo (table-in-out, per-row/streaming): dbo.cf_tag(<table of n>) -> (n, sq=n*n) per input row, emitted
// in Process. Order-independent (one output row per input row). Pure C#, no SQL object.
internal sealed class CfTagFunction : IArrowTableInOutFunction
{
    public string SchemaName => "dbo";
    public string Name => "cf_tag";

    public Schema InputSchema => new(new[] { new Field("n", Int32Type.Default, nullable: true) }, metadata: null);

    public Schema OutputSchema => new(new[]
    {
        new Field("n", Int32Type.Default, nullable: true),
        new Field("sq", Int32Type.Default, nullable: true),
    }, metadata: null);

    public IEnumerable<RecordBatch> Process(RecordBatch inputChunk)
    {
        var n = (Int32Array)inputChunk.Column(0);
        int rows = inputChunk.Length;
        var nb = new Int32Array.Builder().Reserve(rows);
        var sq = new Int32Array.Builder().Reserve(rows);
        for (int i = 0; i < rows; i++)
        {
            if (n.IsNull(i))
            {
                nb.AppendNull();
                sq.AppendNull();
            }
            else
            {
                nb.Append(n.Values[i]);
                sq.Append(n.Values[i] * n.Values[i]);
            }
        }
        yield return new RecordBatch(OutputSchema, new IArrowArray[] { nb.Build(), sq.Build() }, rows);
    }
}

// Demo (table-in-out, STATEFUL streaming): dbo.cf_running_sum(<table of n>) -> (n, running) where running is
// the cumulative sum across the input stream (state kept across Process calls). Emitted per row in Process,
// so it fits the per-chunk streaming model (no emit-at-end). Pure C#, no SQL object. The cumulative VALUE is
// order-dependent, but max(running) == total is order-independent (the last row processed always holds the
// full sum), which is what the test asserts.
internal sealed class CfRunningSumFunction : IArrowTableInOutFunction
{
    private long _running;

    public string SchemaName => "dbo";
    public string Name => "cf_running_sum";

    public Schema InputSchema => new(new[] { new Field("n", Int32Type.Default, nullable: true) }, metadata: null);

    public Schema OutputSchema => new(new[]
    {
        new Field("n", Int32Type.Default, nullable: true),
        new Field("running", Int64Type.Default, nullable: false),
    }, metadata: null);

    public IEnumerable<RecordBatch> Process(RecordBatch inputChunk)
    {
        var n = (Int32Array)inputChunk.Column(0);
        int rows = inputChunk.Length;
        var nb = new Int32Array.Builder().Reserve(rows);
        var rb = new Int64Array.Builder().Reserve(rows);
        for (int i = 0; i < rows; i++)
        {
            if (n.IsNull(i))
            {
                nb.AppendNull();
            }
            else
            {
                _running += n.Values[i];
                nb.Append(n.Values[i]);
            }
            rb.Append(_running);
        }
        yield return new RecordBatch(OutputSchema, new IArrowArray[] { nb.Build(), rb.Build() }, rows);
    }
}

// Demo: dbo.cf_range(n) -> rows (value, squared) for value = 1..n, generated in C#
// (no such object exists in SQL Server). Multi-column to exercise projection.
internal sealed class CfRangeFunction : IArrowTableFunction
{
    public string SchemaName => "dbo";
    public string Name => "cf_range";

    public Schema Parameters => new(new[]
    {
        new Field("n", Int32Type.Default, nullable: true),
    }, metadata: null);

    public Schema OutputSchema => new(new[]
    {
        new Field("value", Int32Type.Default, nullable: false),
        new Field("squared", Int32Type.Default, nullable: false),
    }, metadata: null);

    public IEnumerable<RecordBatch> Invoke(RecordBatch args)
    {
        var arg = (Int32Array)args.Column(0);
        int n = args.Length > 0 && !arg.IsNull(0) ? arg.Values[0] : 0;
        var value = new Int32Array.Builder().Reserve(n);
        var squared = new Int32Array.Builder().Reserve(n);
        for (int i = 1; i <= n; i++)
        {
            value.Append(i);
            squared.Append(i * i);
        }
        yield return new RecordBatch(OutputSchema, new IArrowArray[] { value.Build(), squared.Build() }, n);
    }
}

// Demo: dbo.cf_add(a, b) -> a + b, computed in C# (no such object exists in SQL Server).
internal sealed class CfAddFunction : IArrowScalarFunction
{
    public string SchemaName => "dbo";
    public string Name => "cf_add";

    public Schema Parameters => new(new[]
    {
        new Field("a", Int32Type.Default, nullable: true),
        new Field("b", Int32Type.Default, nullable: true),
    }, metadata: null);

    public Field Result => new("result", Int32Type.Default, nullable: true);

    public IArrowArray Invoke(RecordBatch args)
    {
        var a = (Int32Array)args.Column(0);
        var b = (Int32Array)args.Column(1);
        var builder = new Int32Array.Builder().Reserve(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            if (a.IsNull(i) || b.IsNull(i))
            {
                builder.AppendNull();
            }
            else
            {
                builder.Append(a.Values[i] + b.Values[i]);
            }
        }
        return builder.Build();
    }
}
