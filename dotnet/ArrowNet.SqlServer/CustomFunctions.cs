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
    public static readonly IReadOnlyList<ArrowScalarFunction> Scalar = new ArrowScalarFunction[]
    {
        new CfAddFunction(),
    };
}

// Demo: dbo.cf_add(a, b) -> a + b, computed in C# (no such object exists in SQL Server).
internal sealed class CfAddFunction : ArrowScalarFunction
{
    public override string SchemaName => "dbo";
    public override string Name => "cf_add";

    public override Schema Parameters => new(new[]
    {
        new Field("a", Int32Type.Default, nullable: true),
        new Field("b", Int32Type.Default, nullable: true),
    }, metadata: null);

    public override Field Result => new("result", Int32Type.Default, nullable: true);

    public override IArrowArray Invoke(RecordBatch args)
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
