// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Types;
using Fabricator.Bridge;
using Fabricator.Bridge.Conversion;
using Microsoft.AnalysisServices.AdomdClient;

namespace Fabricator.AnalysisServices;

/// <summary>
/// <c>daxevaltable(&lt;input&gt;, expression := 'EVALUATE …')</c> — a table-in / table-out function that
/// injects the input table into the DAX query as a table named <c>_input</c> (a <c>DEFINE TABLE _input =
/// DATATABLE(…)</c> prepended to the expression), evaluates the query ONCE over the input, and returns its
/// result. The expression must be a single <c>EVALUATE …</c> (no <c>DEFINE</c> of its own) and reference
/// <c>_input</c>. The old Airport <c>DaxEvalTableFlight</c> equivalent.
///
/// <para>Whole-table semantics (the DAX sees the entire injected table at once) make this a <b>COLLECTOR</b>
/// (<see cref="ICollectorFunctionBinding"/>, registered <c>kind='collector'</c>): the C++ Sink+Source operator
/// buffers ALL input, then <see cref="Collect"/> reads every row into one <c>DATATABLE</c>, evaluates once,
/// and streams the result — so there is <b>no single-chunk cap</b> (unlike the earlier streaming-exchange
/// form). Bounded only by what ADOMD accepts as a query string, so still aimed at a parameter / lookup /
/// filter table; for row-by-row work over a large input use <c>daxeach</c> instead.</para>
/// </summary>
internal sealed class DaxEvalTableBinding : ICollectorFunctionBinding
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

    public async IAsyncEnumerable<RecordBatch> Collect(
        IAsyncEnumerable<RecordBatch> allInput, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Buffer the WHOLE input (across all chunks) into one DEFINE TABLE _input = DATATABLE(...) literal.
        var sb = new StringBuilder();
        sb.Append(DaxDataTable.DefineHeader(_inputSchema));
        bool any = false;
        await foreach (var chunk in allInput.WithCancellation(ct))
        {
            using (chunk)
            {
                for (int r = 0; r < chunk.Length; r++)
                {
                    if (any)
                    {
                        sb.Append(',');
                    }
                    any = true;
                    DaxDataTable.AppendRow(sb, _inputSchema, chunk, r);
                }
            }
        }
        if (!any)
        {
            // Empty input: a DATATABLE with no rows is invalid DAX, so emit no output (matches "empty in =>
            // empty out"). A caller wanting an all-data evaluation should use daxeval (no input table).
            yield break;
        }
        sb.Append(" }) ");
        sb.Append(_expression);

        // Evaluate once over the full injected table and stream the result.
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

    public void Dispose() { }
}

/// <summary>
/// <c>daxeach(&lt;input&gt;, expression := 'EVALUATE …')</c> — runs the DAX query ONCE PER INPUT ROW, binding
/// the row's columns as ADOMD parameters the expression references as <c>@&lt;column&gt;</c>, and streams each
/// row's result. The "each" analog of the SQL provider's <c>_each</c> (the old Airport <c>DaxApplyFlight</c>).
/// Output = the DAX result's columns (no input echo — reference <c>@col</c> in the expression to carry input
/// values through). Per-row + per-chunk emit fits the streaming exchange with no input-size limit.
/// </summary>
internal sealed class DaxEachBinding : IInOutFunctionBinding
{
    private readonly DaxCatalog _catalog;
    private readonly string _expression;
    private readonly Schema _inputSchema;

    public DaxEachBinding(DaxCatalog catalog, string expression, Schema inputSchema)
    {
        _catalog = catalog;
        _expression = expression;
        _inputSchema = inputSchema;
        // Output schema = the DAX result columns, resolved by executing once with BLANK (DBNull) parameters
        // (structure-determined; GetSchemaTable fetches no rows). Each input column is an @<name> parameter.
        using var conn = catalog.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = expression;
        foreach (var f in inputSchema.FieldsList)
        {
            ((AdomdCommand)cmd).Parameters.Add(new AdomdParameter(f.Name, DBNull.Value));
        }
        // The probe EXECUTES the expression — cancellable like the scan (Tier 3, ADOMD has no async).
        using var interrupt = new InterruptScope(AmbientOpener.Current);
        using var reg = interrupt.Token.Register(static state =>
        {
            try { ((System.Data.IDbCommand)state!).Cancel(); } catch { }
        }, cmd);
        using var reader = cmd.ExecuteReader();
        OutputSchema = DaxCatalog.ArrowSchemaFromReader(reader);
    }

    public Schema OutputSchema { get; }

    public async IAsyncEnumerable<RecordBatch> DoExchange(
        IAsyncEnumerable<RecordBatch> input, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // One connection + command reused across all rows; only the parameter values change per row.
        using var conn = _catalog.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = _expression;
        int ncols = _inputSchema.FieldsList.Count;
        var adomd = (AdomdCommand)cmd;
        for (int c = 0; c < ncols; c++)
        {
            adomd.Parameters.Add(new AdomdParameter(_inputSchema.FieldsList[c].Name, DBNull.Value));
        }
        // Tier 3 cancellation: ONE scope + registration covers every per-row ExecuteReader (the command
        // instance is reused); disposed when the exchange enumerator ends or is abandoned.
        using var interrupt = new InterruptScope(AmbientOpener.Current, ct);
        using var interruptReg = interrupt.Token.Register(static state =>
        {
            try { ((System.Data.IDbCommand)state!).Cancel(); } catch { }
        }, cmd);

        await foreach (var chunk in input.WithCancellation(ct))
        {
            for (int r = 0; r < chunk.Length; r++)
            {
                for (int c = 0; c < ncols; c++)
                {
                    adomd.Parameters[c].Value = ToParam(ArrowValueReader.ReadScalar(chunk.Column(c), r));
                }
                using var reader = adomd.ExecuteReader();
                foreach (var batch in ReadBatches(reader, OutputSchema))
                {
                    yield return batch;
                }
            }
            yield return InOutExchange.EmptyBatch(OutputSchema); // sentinel: this input chunk consumed
        }
    }

    private static object ToParam(object? value) => value switch
    {
        null => DBNull.Value,
        DateTimeOffset dto => dto.DateTime, // DAX datetime is naive (no tz)
        _ => value,
    };

    // Reads an ADOMD result fully into Arrow batches (<= batchSize), with the end-of-data guard
    // (AdomdDataReader.Read() past EOF throws — see DaxArrowStream). One column appender set per batch.
    private static IEnumerable<RecordBatch> ReadBatches(IDataReader reader, Schema schema, int batchSize = 2048)
    {
        int ncols = schema.FieldsList.Count;
        while (true)
        {
            var appenders = new ColumnAppender[ncols];
            for (int i = 0; i < ncols; i++)
            {
                appenders[i] = ColumnAppender.Create(schema.FieldsList[i].DataType);
            }
            int rows = 0;
            bool end = false;
            while (rows < batchSize)
            {
                if (!reader.Read())
                {
                    end = true;
                    break;
                }
                for (int i = 0; i < ncols; i++)
                {
                    var v = reader.GetValue(i);
                    if (v is null or DBNull) { appenders[i].AppendNull(); } else { appenders[i].Append(v); }
                }
                rows++;
            }
            if (rows > 0)
            {
                var arrays = new IArrowArray[ncols];
                for (int i = 0; i < ncols; i++)
                {
                    arrays[i] = appenders[i].Build();
                }
                yield return new RecordBatch(schema, arrays, rows);
            }
            if (end)
            {
                yield break;
            }
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
