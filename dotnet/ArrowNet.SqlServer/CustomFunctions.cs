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
