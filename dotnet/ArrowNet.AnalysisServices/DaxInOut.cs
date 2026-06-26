using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Types;
using ArrowNet.Bridge;

namespace ArrowNet.AnalysisServices;

/// <summary>
/// <c>daxevaltable(&lt;input&gt;, expression := 'EVALUATE …')</c> — a table-in / table-out function that
/// injects the input table into the DAX query as a table named <c>_input</c> (a <c>DEFINE TABLE _input =
/// DATATABLE(…)</c> prepended to the expression), evaluates the query ONCE over the input, and returns its
/// result. The expression must be a single <c>EVALUATE …</c> (no <c>DEFINE</c> of its own) and reference
/// <c>_input</c>. The old Airport <c>DaxEvalTableFlight</c> equivalent.
///
/// <para>Whole-table semantics (the DAX sees the entire injected table at once), which a streaming exchange
/// can't express — the operator has no emit-at-end hook (a whole-table operation is a pipeline breaker). So
/// the result is emitted DURING the input chunk's tenure, which requires the whole input to arrive in a
/// SINGLE chunk (≤ DuckDB's vector size, 2048 rows). That matches the intended use (inject a small
/// parameter / lookup / filter table into DAX); a larger input raises a clear error. For row-by-row work
/// over a large input, use <c>daxeach</c> instead.</para>
/// </summary>
internal sealed class DaxEvalTableBinding : IArrowInOutBinding
{
    private readonly DaxCatalog _catalog;
    private readonly string _expression;
    private readonly Schema _inputSchema;

    public DaxEvalTableBinding(DaxCatalog catalog, string expression, Schema inputSchema)
    {
        _catalog = catalog;
        _expression = expression;
        _inputSchema = inputSchema;
        // Output schema is structure-determined (independent of the data), so resolve it from a 1-row dummy
        // DATATABLE + the expression (GetSchemaTable fetches no rows).
        OutputSchema = catalog.ProbeSchema(DaxDataTable.DefineDummy(inputSchema) + " " + expression);
    }

    public Schema OutputSchema { get; }

    public async IAsyncEnumerable<RecordBatch> DoExchange(
        IAsyncEnumerable<RecordBatch> input, [EnumeratorCancellation] CancellationToken ct = default)
    {
        bool consumed = false;
        await foreach (var chunk in input.WithCancellation(ct))
        {
            if (consumed)
            {
                // The result depends on the WHOLE injected table, but it was already emitted for the first
                // chunk and the exchange can't emit at end. So a multi-chunk input can't be supported here.
                throw new NotSupportedException(
                    "daxevaltable: the injected input table must fit in a single batch (<= 2048 rows). " +
                    "Reduce/pre-aggregate the input, or use daxeach for row-by-row evaluation.");
            }
            consumed = true;

            if (chunk.Length > 0)
            {
                // Build DEFINE TABLE _input = DATATABLE(...) from THIS (sole) chunk + the expression, run once,
                // and stream the result NOW — during this chunk's tenure (before the sentinel), since output
                // emitted after input EOF is discarded by the operator's finalize drain.
                var sb = new StringBuilder();
                sb.Append(DaxDataTable.DefineHeader(_inputSchema));
                for (int r = 0; r < chunk.Length; r++)
                {
                    if (r > 0)
                    {
                        sb.Append(',');
                    }
                    DaxDataTable.AppendRow(sb, _inputSchema, chunk, r);
                }
                sb.Append(" }) ");
                sb.Append(_expression);

                using var stream = _catalog.StreamCommand(sb.ToString(), OutputSchema);
                while (true)
                {
                    var batch = await stream.ReadNextRecordBatchAsync(ct).ConfigureAwait(false);
                    if (batch is null)
                    {
                        break;
                    }
                    yield return batch;
                }
            }
            yield return InOutExchange.EmptyBatch(OutputSchema); // sentinel: this (sole) input chunk consumed
        }
    }

    public void Dispose() { }
}

/// <summary>
/// Renders an Arrow input table into a DAX <c>DATATABLE</c> literal (column type map + per-value formatting),
/// used by <see cref="DaxEvalTableBinding"/> to inject the input as the DAX table <c>_input</c>.
/// </summary>
internal static class DaxDataTable
{
    public const string TableName = "_input";

    /// <summary><c>DEFINE TABLE _input = DATATABLE("c1", T1, "c2", T2, {</c> (open; caller appends rows + closes).</summary>
    public static string DefineHeader(Schema input) =>
        $"DEFINE TABLE {TableName} = DATATABLE({ColumnDefs(input)}, {{";

    /// <summary>A complete 1-row dummy DATATABLE define (for the output-schema probe — values are irrelevant,
    /// only the column structure of the expression's result matters).</summary>
    public static string DefineDummy(Schema input)
    {
        var dummy = "{" + string.Join(", ", input.FieldsList.Select(f => Dummy(f.DataType))) + "}";
        return $"DEFINE TABLE {TableName} = DATATABLE({ColumnDefs(input)}, {{ {dummy} }})";
    }

    public static void AppendRow(StringBuilder sb, Schema input, RecordBatch chunk, int row)
    {
        sb.Append('{');
        for (int c = 0; c < input.FieldsList.Count; c++)
        {
            if (c > 0)
            {
                sb.Append(", ");
            }
            sb.Append(Literal(ArrowValueReader.ReadScalar(chunk.Column(c), row)));
        }
        sb.Append('}');
    }

    private static string ColumnDefs(Schema input) =>
        string.Join(", ", input.FieldsList.Select(f => $"\"{f.Name.Replace("\"", "\"\"")}\", {DaxType(f.DataType)}"));

    // Arrow type -> DATATABLE column type (one of BOOLEAN / INTEGER / DOUBLE / CURRENCY / DATETIME / STRING).
    private static string DaxType(IArrowType t) => t switch
    {
        BooleanType => "BOOLEAN",
        Int8Type or Int16Type or Int32Type or Int64Type
            or UInt8Type or UInt16Type or UInt32Type or UInt64Type => "INTEGER",
        Decimal128Type { Scale: <= 4 } => "CURRENCY",
        FloatType or DoubleType or Decimal128Type or Decimal256Type => "DOUBLE",
        Date32Type or Date64Type or TimestampType => "DATETIME",
        _ => "STRING",
    };

    // CLR value -> DAX literal (the value's type aligns with its column's Arrow type).
    private static string Literal(object? v) => v switch
    {
        null => "BLANK()",
        bool b => b ? "TRUE()" : "FALSE()",
        DateTime dt => $"\"{dt:yyyy-MM-ddTHH:mm:ss}\"",
        DateTimeOffset dto => $"\"{dto.DateTime:yyyy-MM-ddTHH:mm:ss}\"",
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        sbyte or byte or short or ushort or int or uint or long or ulong
            => Convert.ToString(v, CultureInfo.InvariantCulture)!,
        string s => "\"" + s.Replace("\"", "\"\"") + "\"",
        _ => "\"" + v.ToString()!.Replace("\"", "\"\"") + "\"",
    };

    private static string Dummy(IArrowType t) => DaxType(t) switch
    {
        "BOOLEAN" => "FALSE()",
        "INTEGER" or "CURRENCY" or "DOUBLE" => "0",
        "DATETIME" => "\"1900-01-01T00:00:00\"",
        _ => "\"\"",
    };
}
