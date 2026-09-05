// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Fabricator.Bridge;
using Fluid;

namespace Fabricator.FluidPlugin;

/// <summary>
/// <c>fluid_query_batch(template, &lt;input&gt; [, params := …] [, batchsize := …])</c> — a Liquid template
/// rendered WITH A RELATION IN HAND, once for the whole input or once per <c>batchsize</c> rows, with each
/// rendered statement run and its rows returned.
///
/// <para>Where <see cref="FluidQueryFunction"/> renders from CONSTANTS at bind time and lets DuckDB run the
/// result (<c>bind_replace</c>), this renders from DATA at execution time and runs the result itself. That
/// is the whole difference and it is also the whole cost: the call does not disappear into the caller's
/// plan, so every output row crosses Arrow. <b>Reach for it only when the SQL TEXT must depend on the
/// input</b> — a table name, a pivot list, a per-tenant fan-out. If the generated statement is the same
/// whatever the rows are, <c>fluid_query</c> plus an ordinary join is strictly better.</para>
/// </summary>
/// <remarks>
/// <para>
/// ⚠⚠ <b>IT IS A COLLECTOR, AND THAT IS FORCED.</b> A whole-table render cannot be a streaming in-out: that
/// operator's only all-input-done hook is handed no <c>DataChunk</c>, so output held back until input EOF is
/// DRAINED AND DISCARDED (docs/inout-collector-mode.md). A collector buffers its input and emits afterwards,
/// which is exactly this shape. The price is inherent: <b>the whole input is buffered</b> before the first
/// render, even with a small <c>batchsize</c>. <c>batchsize</c> is therefore about how many rows each RENDER
/// sees, never about memory.
/// </para>
/// <para>
/// ⚠⚠ <b><c>publish()</c> IS REFUSED HERE, and the refusal replaces a HANG.</b> This surface runs the
/// generated statement on the render's OWN pinned connection, so scanning a publication re-enters that
/// connection while it is mid-query — MEASURED to deadlock rather than to raise the one-live-result error.
/// Nothing is lost: a publication exists to carry a staged relation ACROSS a connection boundary, and here
/// there is none. Select the staged table directly.
/// </para>
/// <para>
/// ⚠ <b>The input arrives as the temp table <c>input_table</c></b>, on the render's own connection — so the
/// template reads it with <c>{% query %}</c>, joins it in the generated statement, or ignores it. Its
/// columns are the input relation's own, which is the reason this takes a TABLE argument rather than
/// correlated columns: a lateral function's inputs are named by their rendered EXPRESSION TEXT.
/// </para>
/// <para>
/// ⚠ <b>ONE session for the whole execution, so SQL state carries between groups</b> — a temp table one
/// group's <c>{% exec %}</c> creates is there for the next (MEASURED: three groups accumulating into one
/// temp table read back 1, 2, 3). Groups are rendered in order and never concurrently, which is what makes
/// that safe.
/// </para>
/// <para>
/// ⚠⚠ <b>LIQUID state does NOT carry, and I wrote the opposite down before measuring it.</b> A
/// <c>{% assign %}</c> in one group is NOT visible to the next: Fluid renders into a CHILD SCOPE and pops
/// it, so every group starts from the variables the context was built with. MEASURED — a counter assigned
/// per group read 1, 1, 1 where the same run's temp-table counter read 1, 2, 3. The shared context is worth
/// having for what it does do (the params bag and the host functions are bound once), not for state.
/// <b>Accumulate in a temp table, which is also what a SQL generator wants.</b>
/// </para>
/// </remarks>
internal sealed class FluidQueryBatchFunction : ICollectorFunction
{
    internal const string FunctionName = "fluid_query_batch";

    /// <summary>The name the template reads its rows under.</summary>
    private const string InputTable = "input_table";

    /// <summary>Where every input row is staged; <see cref="InputTable"/> is a view over a slice of it.</summary>
    private const string StagingTable = "__fab_input";

    /// <summary>The staging row number, which is what makes a group an EXACT <c>batchsize</c> rows.</summary>
    private const string SeqColumn = "__fab_seq";

    /// <summary>Any input column under this prefix would collide with the staging machinery.</summary>
    private const string ReservedPrefix = "__fab";

    private const string PublishRefusal =
        "publish() cannot be used here — " + FunctionName + " runs the rendered statement on the template's "
        + "OWN connection, so scanning a publication would re-enter that connection and hang. A publication "
        + "carries a staged relation to a DIFFERENT connection, which this surface does not need: select the "
        + "staged table directly (SELECT * FROM my_table).";

    public string Name => FunctionName;

    public Schema Parameters => new(new[]
    {
        // NON-nullable: a NULL template has no statement to generate, so the host refuses it by parameter
        // name rather than failing somewhere inside the parser.
        Params.Positional("template", StringType.Default, nullable: false),
        // ⚠ The table input may sit BETWEEN positionals — DuckDB pushes a placeholder for the subquery slot
        // — which is what lets the template stay first and read like fluid_query's.
        Params.TableInput("input"),
        // The same bag fluid_query and fluid_render take: STRUCT, MAP or a JSON string.
        Params.Named("params", NullType.Default),
        Params.Named("batchsize", Int64Type.Default),
    }, metadata: null);

    public ICollectorFunctionBinding Bind(RecordBatch? args, Schema inputSchema)
    {
        var template = ReadTemplate(args);
        long? batchSize = ReadBatchSize(args);
        // ⚠⚠ CAPTURED, not retained: the args batch belongs to the framework and its lifetime ends with this
        // call, while the groups render much later. FluidValueModel.Capture is eager all the way down, so
        // what comes back holds no Arrow memory.
        var parameters = FluidValueModel.CaptureBag(FluidValueModel.ArgColumn(args, "params"), 0);

        foreach (var f in inputSchema.FieldsList)
        {
            if (f.Name.StartsWith(ReservedPrefix, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"{FunctionName}: the input column '{f.Name}' uses the reserved '{ReservedPrefix}' prefix, "
                    + "which this function needs for its own staging columns. Alias it in the input query.");
            }
        }

        // ⚠⚠ THE OUTPUT SCHEMA IS WHAT THE TEMPLATE ACTUALLY PRODUCES, not what it claims. The probe renders
        // with is_bind = true against an EMPTY input_table and asks DuckDB to bind the result; a template may
        // use the flag to skip expensive setup, but the columns still come from binding what it rendered. A
        // declaration written twice — once for the probe, once for real — is a declaration that drifts, and
        // the drift would be read as DATA.
        using var probe = FluidRenderSession.TryCreate()
            ?? throw new InvalidOperationException(
                $"{FunctionName} needs the hosting DuckDB to determine its output columns, and it is not "
                + "available here.");
        var ctx = NewContext(probe, parameters, isBind: true);
        CreateEmptyInput(probe, inputSchema);
        var generated = FluidEngine.RenderOn(FunctionName, template, ctx);
        var outputSchema = DescribeGenerated(probe, generated);
        return new Binding(template, parameters, batchSize, inputSchema, outputSchema);
    }

    /// <summary>Builds the render context every render of one execution shares.</summary>
    private static TemplateContext NewContext(FluidRenderSession session,
                                              object? parameters,
                                              bool isBind)
    {
        var ctx = FluidEngine.NewRenderContext(FunctionName, PublishRefusal, session, c =>
        {
            FluidValueModel.SetVariable(c, FluidValueModel.BagVariable, parameters);
        });
        ctx.SetValue(FluidEngine.IsBindVariable, isBind);
        return ctx;
    }

    /// <summary>The schema of <paramref name="generated"/> without producing a row of it.</summary>
    /// <remarks>
    /// ⚠ Wrapped in <c>SELECT * FROM (…) LIMIT 0</c>, the same shape <c>Publish</c> uses, which does two
    /// jobs: it binds without scanning, and it REQUIRES the generated statement to be a SELECT usable as a
    /// subquery. Running the statement bare would silently accept a DDL or DML and declare whatever shape
    /// the engine reports for it.
    /// </remarks>
    private static Schema DescribeGenerated(FluidRenderSession session, string generated)
    {
        if (string.IsNullOrWhiteSpace(generated))
        {
            throw new ArgumentException(
                $"{FunctionName}: the template rendered nothing. It must render a SELECT — under "
                + $"{FluidEngine.IsBindVariable} too, where a `SELECT … LIMIT 0` of the right columns is the "
                + "usual answer.");
        }
        using var stream = session.Query($"SELECT * FROM ({generated}) LIMIT 0");
        return stream.Schema;
    }

    private static void CreateEmptyInput(FluidRenderSession session, Schema inputSchema)
    {
        // ⚠ Via a zero-row named source rather than rendered DDL: writing `CREATE TABLE t(a VARCHAR, …)`
        // needs an Arrow→DuckDB type-name table by hand, which is the second type mapping this codebase
        // keeps refusing to maintain. DuckDB derives the columns from the Arrow schema instead.
        var token = session.RegisterRows(inputSchema);
        try
        {
            session.ExecuteNonQuery(
                $"CREATE OR REPLACE TEMP TABLE {DuckSql.QuoteIdent(InputTable)} AS "
                + $"SELECT * FROM fabricator_scan({DuckSql.Literal(token)})");
        }
        finally
        {
            session.ReleaseRows(token);
        }
    }

    private static string ReadTemplate(RecordBatch? args)
    {
        if (FluidValueModel.ArgColumn(args, "template") is not StringArray templates || templates.Length == 0
            || templates.IsNull(0))
        {
            throw new ArgumentException($"{FunctionName}: 'template' must be a non-NULL VARCHAR");
        }
        return templates.GetString(0);
    }

    private static long? ReadBatchSize(RecordBatch? args)
    {
        if (FluidValueModel.ArgColumn(args, "batchsize") is not Int64Array sizes || sizes.Length == 0
            || sizes.IsNull(0))
        {
            return null;
        }
        long size = sizes.GetValue(0)!.Value;
        if (size <= 0)
        {
            throw new ArgumentException(
                $"{FunctionName}: batchsize must be positive; omit it to render once over the whole input.");
        }
        return size;
    }

    private sealed class Binding : ICollectorFunctionBinding
    {
        private readonly string _template;
        private readonly object? _parameters;
        private readonly long? _batchSize;
        private readonly Schema _inputSchema;

        internal Binding(string template, object? parameters,
                         long? batchSize, Schema inputSchema, Schema outputSchema)
        {
            _template = template;
            _parameters = parameters;
            _batchSize = batchSize;
            _inputSchema = inputSchema;
            OutputSchema = outputSchema;
        }

        public Schema OutputSchema { get; }

        public async IAsyncEnumerable<RecordBatch> Collect(
            IAsyncEnumerable<RecordBatch> allInput,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            // ⚠ PER EXECUTION, not per binding: a binding is reused across prepared re-executions, so a
            // session or a context built at bind would carry one execution's temp tables and Liquid state
            // into the next.
            using var session = FluidRenderSession.TryCreate()
                ?? throw new InvalidOperationException(
                    $"{FunctionName} needs the hosting DuckDB, which is not available here.");
            var ctx = NewContext(session, _parameters, isBind: false);

            long staged = await StageInputAsync(session, allInput, ct).ConfigureAwait(false);
            // ⚠ `staged > size` rather than `staged > 0`, so a batchsize at or above the row count is ONE
            // group by the same arithmetic that gives none — and, less obviously, so a huge batchsize
            // cannot overflow `staged + size - 1` into a negative count, which would silently produce no
            // groups and therefore no rows.
            long groups = _batchSize is long size && staged > size ? (staged + size - 1) / size : 1;

            for (long g = 0; g < groups; g++)
            {
                ct.ThrowIfCancellationRequested();
                DefineGroupView(session, g);
                var generated = FluidEngine.RenderOn(FunctionName, _template, ctx);
                if (string.IsNullOrWhiteSpace(generated))
                {
                    throw new ArgumentException(
                        $"{FunctionName}: the template rendered nothing for group {g + 1} of {groups}; "
                        + "it must render a SELECT.");
                }
                using var stream = session.Query(generated);
                Verify(stream.Schema, OutputSchema);
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
        }

        /// <summary>Copies every input row into the staging table, numbered, and returns how many there were.</summary>
        /// <remarks>
        /// ⚠⚠ Each batch is consumed INSIDE the loop, because the framework frees a chunk's Arrow buffers
        /// once it has been consumed — so the rows must move into DuckDB now, not be accumulated here. That
        /// is also the better place for them: DuckDB owns (and can spill) the staged relation.
        /// </remarks>
        private async Task<long> StageInputAsync(FluidRenderSession session,
                                                 IAsyncEnumerable<RecordBatch> input, CancellationToken ct)
        {
            long staged = 0;
            bool created = false;
            var seq = DuckSql.QuoteIdent(SeqColumn);
            var staging = DuckSql.QuoteIdent(StagingTable);
            await foreach (var batch in input.WithCancellation(ct).ConfigureAwait(false))
            {
                if (batch.Length == 0)
                {
                    continue;
                }
                var token = session.RegisterRows(batch);
                try
                {
                    // ⚠ row_number() OVER () numbers the batch in the order the scan delivers it. WHICH row
                    // gets which number does not matter — the numbers only have to be unique and contiguous,
                    // so that every row lands in exactly one group.
                    var rows = $"SELECT (CAST({staged} AS BIGINT) + row_number() OVER ()) - 1 AS {seq}, * "
                               + $"FROM fabricator_scan({DuckSql.Literal(token)})";
                    session.ExecuteNonQuery(created
                        ? $"INSERT INTO {staging} {rows}"
                        : $"CREATE OR REPLACE TEMP TABLE {staging} AS {rows}");
                }
                finally
                {
                    session.ReleaseRows(token);
                }
                created = true;
                staged += batch.Length;
            }
            if (!created)
            {
                // An input with no rows still renders ONCE — the template is a statement generator and its
                // output need not depend on the rows. It needs a staging table to define the view over.
                var token = session.RegisterRows(WithSeq(_inputSchema));
                try
                {
                    session.ExecuteNonQuery(
                        $"CREATE OR REPLACE TEMP TABLE {staging} AS "
                        + $"SELECT * FROM fabricator_scan({DuckSql.Literal(token)})");
                }
                finally
                {
                    session.ReleaseRows(token);
                }
            }
            return staged;
        }

        /// <summary>Points <c>input_table</c> at group <paramref name="group"/>'s rows.</summary>
        /// <remarks>
        /// ⚠ A VIEW, not a copy: the whole-input case would otherwise duplicate the staged relation, and the
        /// batched case would copy every group a second time. The range predicate is what makes a group
        /// EXACTLY <c>batchsize</c> rows rather than "at least, rounded up to an input chunk".
        /// </remarks>
        private void DefineGroupView(FluidRenderSession session, long group)
        {
            var seq = DuckSql.QuoteIdent(SeqColumn);
            var body = $"SELECT * EXCLUDE ({seq}) FROM {DuckSql.QuoteIdent(StagingTable)}";
            if (_batchSize is long size)
            {
                long lo = group * size;
                body += $" WHERE {seq} >= {lo} AND {seq} < {lo + size}";
            }
            session.ExecuteNonQuery(
                $"CREATE OR REPLACE TEMP VIEW {DuckSql.QuoteIdent(InputTable)} AS {body}");
        }

        /// <summary>The staging schema: the seq column then the input's own.</summary>
        private static Schema WithSeq(Schema inputSchema)
        {
            var fields = new List<Field> { new(SeqColumn, Int64Type.Default, nullable: false) };
            fields.AddRange(inputSchema.FieldsList);
            return new Schema(fields, metadata: null);
        }

        /// <summary>
        /// Refuses a group whose statement produced a different shape from the one declared at bind.
        /// </summary>
        /// <remarks>
        /// ⚠⚠ NOT defensive: the template renders anew for every group and may legitimately render DIFFERENT
        /// SQL, so a group producing different columns is reachable from an ordinary template. The host
        /// builds its Arrow→DuckDB converters from the DECLARED schema and reads batches through them, so an
        /// unchecked mismatch is read as DATA.
        /// <para>⚠ Count, names and type IDs — the same three the host's own declared-source check compares,
        /// and with the same limit: a change of type PARAMETERS alone (decimal(9,2) to decimal(18,4)) passes.
        /// Apache.Arrow's IArrowType.Equals is REFERENCE equality, so a fuller comparison needs a structural
        /// comparer this codebase has already noted is worth consolidating.</para>
        /// </remarks>
        private static void Verify(Schema arrived, Schema declared)
        {
            string? problem = null;
            if (arrived.FieldsList.Count != declared.FieldsList.Count)
            {
                problem = $"{arrived.FieldsList.Count} columns where {declared.FieldsList.Count} were declared";
            }
            else
            {
                for (int i = 0; i < declared.FieldsList.Count && problem is null; i++)
                {
                    var d = declared.FieldsList[i];
                    var a = arrived.FieldsList[i];
                    if (!string.Equals(d.Name, a.Name, StringComparison.Ordinal))
                    {
                        problem = $"column {i + 1} is named '{a.Name}' where '{d.Name}' was declared";
                    }
                    else if (d.DataType.TypeId != a.DataType.TypeId)
                    {
                        problem = $"column '{d.Name}' is {a.DataType.Name} where {d.DataType.Name} was declared";
                    }
                }
            }
            if (problem is not null)
            {
                throw new InvalidOperationException(
                    $"{FunctionName}: a rendered statement produced {problem}. Every group must produce the "
                    + $"columns the schema-probe render declared — branch on {FluidEngine.IsBindVariable} to "
                    + "declare them, and keep every other branch to that same shape.");
            }
        }

        public void Dispose()
        {
        }
    }
}
