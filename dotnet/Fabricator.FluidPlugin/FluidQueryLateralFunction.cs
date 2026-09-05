// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Types;
using Fabricator.Bridge;
using Fluid;

namespace Fabricator.FluidPlugin;

/// <summary>
/// <c>fluid_query_lateral(template, params, &lt;per-row columns…&gt;)</c> — a Liquid template rendered once
/// per INPUT CHUNK, with those rows in hand, and the rendered statement run. The correlated sibling of
/// <see cref="FluidQueryBatchFunction"/>:
/// <code>
/// SELECT t.id, f.*
/// FROM   t, fluid_query_lateral('SELECT __fab_row, upper(name) AS u FROM input_table', 'null', t.name) f;
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// <b>Where it differs from <c>fluid_query_batch</c>, which is the whole reason it exists.</b> The collector
/// takes a TABLE argument, buffers all of it and renders groups sequentially; this takes ordinary per-row
/// COLUMNS, so it composes with the enclosing query — DuckDB correlates it, the operator runs in PARALLEL,
/// and the caller's own columns are stamped onto every output row without the template projecting them.
/// The price is everything below.
/// </para>
/// <para>
/// ⚠⚠ <b>THE TEMPLATE MUST PROJECT <c>__fab_row</c>, and this is not a convention — it is the contract that
/// makes a batched lateral expressible at all.</b> One call sees up to 2048 input rows and may return any
/// number of output rows, so the host has to be told which INPUT row each OUTPUT row came from before it can
/// stamp the correlated columns. <c>input_table</c> carries <c>__fab_row</c> as its first column for exactly
/// that purpose; carry it through and it is stripped from the result. Omitting it is refused AT BIND, with a
/// message naming the column — never silently, because a wrong answer here is a row attributed to the wrong
/// outer row.
/// </para>
/// <para>
/// ⚠ It follows that <c>SELECT * FROM input_table</c> is the identity template: the row id rides along and
/// disappears again. 1→N (repeat a row id) and 1→0 (omit it) are both expressible, which is what a lateral
/// is for.
/// </para>
/// <para>
/// ⚠⚠ <b>NO STATE CARRIES BETWEEN CHUNKS, unlike the collector — the operator is PARALLEL.</b> Each pipeline
/// thread opens its own session, its own DuckDB connection and its own temporary catalog, and which rows
/// reach which thread is the scheduler's business. A temp table one chunk's <c>{% exec %}</c> creates may or
/// may not be there for the next. Treat every render as independent; if you need accumulation, use
/// <c>fluid_query_batch</c>, which is sequential by construction.
/// </para>
/// <para>
/// ⚠⚠ <b>The input columns are named by their RENDERED EXPRESSION TEXT, and it is faithful rather than
/// friendly.</b> DuckDB synthesises the input relation from the argument expressions, so MEASURED:
/// <c>t.n</c> arrives as <c>n</c>, <c>t.n + 1</c> as <c>(t.n + 1)</c>, <c>upper('x')</c> as
/// <c>upper('x')</c>, and <c>{'a': t.id, 'b': t.n}</c> as
/// <c>main.struct_pack(a := t.id, b := t.n)</c> — quoted, that is addressable, but nobody wants to write
/// it. A LITERAL call has no expression text at all and falls back to <c>col&lt;slot&gt;</c>, where SLOT is
/// the ARGUMENT POSITION: with the two constants ahead of it, the first input column of
/// <c>f('tpl', 'null', 7, 'x')</c> is <c>col2</c>, not <c>col0</c>.
/// </para>
/// <para>
/// ⚠ <b>So the naming-free spelling is a positional column alias, and it is ordinary SQL:</b>
/// <code>
/// SELECT r AS __fab_row, a * b AS s FROM (SELECT * FROM input_table) AS q(r, a, b)
/// </code>
/// The columns are positional (row id first, then the arguments in call order), so this needs no knowledge
/// of what anything rendered as and works identically in both call shapes. Note the row id is re-aliased
/// back to <c>__fab_row</c>: the requirement is on the OUTPUT column's name, not the input's.
/// </para>
/// <para>
/// ⚠⚠ <b><c>publish()</c> IS REFUSED, for the same measured reason as in <c>fluid_query_batch</c>:</b> the
/// generated statement runs on the render's OWN pinned connection, so scanning a publication would re-enter
/// that connection mid-query and HANG. Nothing is lost — a publication carries a staged relation across a
/// connection boundary, and here there is none.
/// </para>
/// <para>
/// ⚠ <b>Both cost arguments are CONSTANT, not NAMED, and that is forced.</b> A named argument is unusable in
/// the correlated shape: MEASURED, DuckDB drops the name and matches positionally, so
/// <c>f(t.n, params := …)</c> is a Binder Error while the same call with literals works. A constant occupies
/// a positional slot and is recovered in both shapes (see <see cref="ParamStyle.Constant"/>), so
/// <c>params</c> is always written, positionally, second.
/// </para>
/// <para>
/// ⚠⚠ <b>AND IT CANNOT BE <c>NULL</c> — the no-bag spelling is the JSON <c>'null'</c>.</b> A bind-time
/// constant that arrives NULL is REFUSED by the host, and rightly: in the correlated shape an explicit NULL
/// is indistinguishable from a fold that failed (a column, a volatile), which is the one thing that refusal
/// exists to catch. <c>'null'</c> is a VARCHAR bag whose JSON root is null, so it binds and
/// <see cref="FluidValueModel.CaptureBag"/> maps it to nil — MEASURED, <c>{% if params %}</c> is then FALSE,
/// exactly as for an absent bag elsewhere. (<c>'{}'</c> also binds and is TRUTHY, which is the other thing
/// a caller might mean.)
/// </para>
/// </remarks>
internal sealed class FluidQueryLateralFunction : ILateralFunction
{
    internal const string FunctionName = "fluid_query_lateral";

    /// <summary>The name the template reads its rows under.</summary>
    private const string InputTable = "input_table";

    /// <summary>
    /// The provenance column: 0-based index INTO THIS CALL'S INPUT CHUNK, present on
    /// <see cref="InputTable"/> and required back in the generated statement's projection.
    /// </summary>
    internal const string OriginColumn = "__fab_row";

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
        // ⚠ CONSTANT, never Named: a lateral's bind-time arguments have to occupy positional slots or the
        // correlated call shape cannot spell them (measured — DuckDB drops the name there).
        Params.Constant("template"),
        Params.Constant("params"),
        // The per-row input columns, any number of them, any type. A lateral's positional slots ARE its
        // input columns, so a variadic tail is a variable-width WIRE rather than a wider args batch.
        Params.VarArgs("input"),
    }, metadata: null);

    public ILateralFunctionBinding Bind(RecordBatch? args, Schema inputSchema)
    {
        var template = ReadTemplate(args);
        // ⚠⚠ CAPTURED, not retained: the args batch belongs to the framework and its lifetime ends with this
        // call, while the renders happen much later on other threads. FluidValueModel.CaptureBag is eager
        // all the way down, so what comes back holds no Arrow memory.
        var parameters = FluidValueModel.CaptureBag(FluidValueModel.ArgColumn(args, "params"), 0);

        foreach (var f in inputSchema.FieldsList)
        {
            if (f.Name.StartsWith(ReservedPrefix, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"{FunctionName}: the input column '{f.Name}' uses the reserved '{ReservedPrefix}' prefix, "
                    + $"which this function needs for its own provenance column. Alias it at the call site.");
            }
        }

        // The staging shape both the probe and every call use: the row id, then the caller's own columns.
        var stagedSchema = WithOrigin(inputSchema);

        // ⚠⚠ THE OUTPUT SCHEMA IS WHAT THE TEMPLATE ACTUALLY PRODUCES, not what it claims — the same rule
        // fluid_query_batch's probe follows, and for the same reason: a declaration written twice drifts,
        // and the drift would be read as DATA. The probe renders with is_bind = true against an EMPTY
        // input_table and asks DuckDB to bind the result.
        using var probe = FluidRenderSession.TryCreate()
            ?? throw new InvalidOperationException(
                $"{FunctionName} needs the hosting DuckDB to determine its output columns, and it is not "
                + "available here.");
        var ctx = NewContext(probe, parameters, isBind: true);
        CreateEmptyInput(probe, stagedSchema);
        var generated = FluidEngine.RenderOn(FunctionName, template, ctx);
        var outputSchema = DescribeGenerated(probe, generated);
        return new Binding(template, parameters, stagedSchema, outputSchema);
    }

    /// <summary>Builds the render context one session's renders share.</summary>
    private static TemplateContext NewContext(FluidRenderSession session, object? parameters, bool isBind)
    {
        var ctx = FluidEngine.NewRenderContext(FunctionName, PublishRefusal, session, c =>
        {
            FluidValueModel.SetVariable(c, FluidValueModel.BagVariable, parameters);
        });
        ctx.SetValue(FluidEngine.IsBindVariable, isBind);
        return ctx;
    }

    /// <summary>
    /// The generated statement as this function runs it: the callee's own columns, then the provenance
    /// column, cast and LAST.
    /// </summary>
    /// <remarks>
    /// ⚠ ONE wrapper, used at bind and at execute, so the position and type of <see cref="OriginColumn"/>
    /// are the same in both and no search is needed at execute time. It also normalises what the template
    /// may legitimately have produced — <c>SELECT 0 AS __fab_row</c> is an INT32 — into the one type the
    /// origin reader accepts.
    /// </remarks>
    private static string Wrap(string generated)
    {
        var origin = DuckSql.QuoteIdent(OriginColumn);
        return $"SELECT * EXCLUDE ({origin}), CAST({origin} AS BIGINT) AS {origin} FROM ({generated})";
    }

    /// <summary>The columns <paramref name="generated"/> produces, without the provenance column.</summary>
    /// <remarks>
    /// ⚠ Probed UNWRAPPED so the missing-column case gets OUR message rather than DuckDB's complaint about
    /// an EXCLUDE naming an unknown column — the requirement is this function's, so the diagnosis should be
    /// too. Removing the field here preserves the remaining order, which is exactly what
    /// <c>* EXCLUDE (…)</c> does at execute, so the two agree by construction.
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
        Schema described;
        using (var stream = session.Query($"SELECT * FROM ({generated}) LIMIT 0"))
        {
            described = stream.Schema;
        }
        var kept = new List<Field>(described.FieldsList.Count);
        bool sawOrigin = false;
        foreach (var f in described.FieldsList)
        {
            if (string.Equals(f.Name, OriginColumn, StringComparison.Ordinal))
            {
                sawOrigin = true;
                continue;
            }
            kept.Add(f);
        }
        if (!sawOrigin)
        {
            throw new ArgumentException(
                $"{FunctionName}: the generated statement must project '{OriginColumn}' — the 0-based row "
                + $"index of the input row each output row belongs to. It is the first column of "
                + $"{InputTable}, so carrying it through (SELECT {OriginColumn}, … FROM {InputTable}) is "
                + "usually all that is needed; it is removed from the result. Generated: " + generated);
        }
        if (kept.Count == 0)
        {
            throw new ArgumentException(
                $"{FunctionName}: the generated statement projects only '{OriginColumn}' and so has no "
                + "columns of its own to return. Add at least one.");
        }
        return new Schema(kept, metadata: null);
    }

    /// <summary>Creates <see cref="InputTable"/> with no rows, for the schema probe.</summary>
    private static void CreateEmptyInput(FluidRenderSession session, Schema stagedSchema)
    {
        // ⚠ Via a zero-row named source rather than rendered DDL: writing `CREATE TABLE t(a VARCHAR, …)`
        // needs an Arrow→DuckDB type-name table by hand, which is the second type mapping this codebase
        // keeps refusing to maintain. DuckDB derives the columns from the Arrow schema instead.
        var token = session.RegisterRows(stagedSchema);
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

    /// <summary>The staging schema: the provenance column, then the caller's own input columns.</summary>
    private static Schema WithOrigin(Schema inputSchema)
    {
        var fields = new List<Field> { new(OriginColumn, Int64Type.Default, nullable: false) };
        fields.AddRange(inputSchema.FieldsList);
        return new Schema(fields, metadata: null);
    }

    private static string ReadTemplate(RecordBatch? args)
    {
        if (FluidValueModel.ArgColumn(args, "template") is not StringArray templates || templates.Length == 0
            || templates.IsNull(0))
        {
            throw new ArgumentException(
                $"{FunctionName}: 'template' must be a non-NULL VARCHAR constant (the first argument).");
        }
        return templates.GetString(0);
    }

    private sealed class Binding : ILateralFunctionBinding
    {
        private readonly string _template;
        private readonly object? _parameters;
        private readonly Schema _stagedSchema;

        internal Binding(string template, object? parameters, Schema stagedSchema, Schema outputSchema)
        {
            _template = template;
            _parameters = parameters;
            _stagedSchema = stagedSchema;
            OutputSchema = outputSchema;
        }

        public Schema OutputSchema { get; }

        // ⚠ PER THREAD — the batched lateral operator declares itself parallel, so several of these are open
        // at once, each with its own DuckDB connection and temporary catalog. That is what makes cross-chunk
        // state unavailable here and available in the collector.
        public ILateralSession Open() =>
            new Session(_template, _parameters, _stagedSchema, OutputSchema);

        public void Dispose()
        {
        }
    }

    private sealed class Session : ILateralSession
    {
        private readonly string _template;
        private readonly Schema _stagedSchema;
        private readonly Schema _outputSchema;
        private readonly FluidRenderSession _session;
        private readonly TemplateContext _ctx;

        internal Session(string template, object? parameters, Schema stagedSchema, Schema outputSchema)
        {
            _template = template;
            _stagedSchema = stagedSchema;
            _outputSchema = outputSchema;
            _session = FluidRenderSession.TryCreate()
                ?? throw new InvalidOperationException(
                    $"{FunctionName} needs the hosting DuckDB, which is not available here.");
            _ctx = NewContext(_session, parameters, isBind: false);
        }

        public LateralResult Call(RecordBatch input)
        {
            StageInput(input);
            var generated = FluidEngine.RenderOn(FunctionName, _template, _ctx);
            if (string.IsNullOrWhiteSpace(generated))
            {
                throw new ArgumentException(
                    $"{FunctionName}: the template rendered nothing for a chunk of {input.Length} rows; it "
                    + "must render a SELECT.");
            }

            var parts = new List<RecordBatch>();
            try
            {
                long rows = 0;
                // ⚠ The stream is held until Assemble has decided what is handed on. A batch's lifetime is
                // independent of its stream's under the Arrow C data interface, so closing first would very
                // likely be fine — but "very likely" is not a property to rest a use-after-free on, and
                // outliving it costs nothing.
                using var stream = _session.Query(Wrap(generated));
                Verify(stream.Schema);
                while (true)
                {
                    var batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
                    if (batch is null)
                    {
                        break;
                    }
                    if (batch.Length == 0)
                    {
                        batch.Dispose();
                        continue;
                    }
                    parts.Add(batch);
                    rows += batch.Length;
                }
                if (rows == 0)
                {
                    // A legitimate answer: every input row of this chunk produced nothing (1→0).
                    return LateralResult.Empty;
                }
                return Assemble(parts, checked((int)rows), input.Length);
            }
            catch
            {
                // ⚠ Only the FAILURE path frees here. On success Assemble decides what is handed on and
                // frees the rest — a blanket `finally` would free the very arrays the result references,
                // which is a use-after-free that faults in Arrow's release callback rather than here.
                foreach (var p in parts)
                {
                    p.Dispose();
                }
                throw;
            }
        }

        /// <summary>Copies this call's rows into <see cref="InputTable"/>, numbered from 0.</summary>
        /// <remarks>
        /// ⚠⚠ The row id is built HERE, in Arrow, rather than by a <c>row_number()</c> in the staging SQL —
        /// because unlike <c>fluid_query_batch</c>'s grouping key it has to IDENTIFY an input row, not merely
        /// be unique. Numbering the Arrow columns directly is exact by construction and depends on no
        /// ordering promise from the scan.
        /// <para>⚠ The staged batch is BORROWED and must not be disposed: its columns are the framework's
        /// <paramref name="input"/>, which it disposes itself when <see cref="Call"/> returns.</para>
        /// </remarks>
        private void StageInput(RecordBatch input)
        {
            var ids = new Int64Array.Builder().Reserve(input.Length);
            for (int i = 0; i < input.Length; i++)
            {
                ids.Append(i);
            }
            var columns = new IArrowArray[input.ColumnCount + 1];
            columns[0] = ids.Build();
            for (int c = 0; c < input.ColumnCount; c++)
            {
                columns[c + 1] = input.Column(c);
            }
            var staged = new RecordBatch(_stagedSchema, columns, input.Length);
            var token = _session.RegisterRows(staged);
            try
            {
                _session.ExecuteNonQuery(
                    $"CREATE OR REPLACE TEMP TABLE {DuckSql.QuoteIdent(InputTable)} AS "
                    + $"SELECT * FROM fabricator_scan({DuckSql.Literal(token)})");
            }
            finally
            {
                _session.ReleaseRows(token);
            }
        }

        /// <summary>Joins the drained parts into one batch and splits off the provenance column.</summary>
        /// <remarks>
        /// ⚠⚠ IT OWNS THE PARTS, and the two cases free different things — which is the whole reason this is
        /// not a <c>finally</c>. With SEVERAL parts every output column is freshly allocated by the
        /// concatenator, so all the parts are released. With ONE part the output columns ARE that part's, so
        /// the batch is handed on undisposed and only the provenance column — the one array not handed on —
        /// is released; releasing the part as well would free the arrays the caller is about to read.
        /// <para>⚠ Disposing a single column is not a trick: <c>RecordBatch.Dispose</c> is defined as
        /// disposing its columns, so this is that operation performed selectively, and each array is still
        /// disposed exactly once in total.</para>
        /// </remarks>
        private LateralResult Assemble(List<RecordBatch> parts, int rows, int inputRows)
        {
            int width = _outputSchema.FieldsList.Count;
            // ⚠ The provenance FIRST, because it is the step that can refuse: nothing has been freed or
            // handed on yet, so the caller's catch is still the whole story.
            var origin = new int[rows];
            int at = 0;
            foreach (var p in parts)
            {
                var ids = (Int64Array)p.Column(width);
                for (int r = 0; r < p.Length; r++, at++)
                {
                    if (ids.IsNull(r))
                    {
                        throw new InvalidOperationException(
                            $"{FunctionName}: the generated statement produced a NULL '{OriginColumn}'. Every "
                            + $"output row must name the input row it belongs to.");
                    }
                    long v = ids.GetValue(r)!.Value;
                    if (v < 0 || v >= inputRows)
                    {
                        // ⚠ The host checks this too, and precisely. Checking here as well is about the
                        // MESSAGE: only this side knows the value came from a projected '__fab_row'.
                        throw new InvalidOperationException(
                            $"{FunctionName}: the generated statement produced '{OriginColumn}' = {v} for an "
                            + $"input chunk of {inputRows} rows. It must be a 0-based index into "
                            + $"{InputTable} — carry the column through rather than computing it.");
                    }
                    origin[at] = (int)v;
                }
            }

            var columns = new IArrowArray[width];
            for (int c = 0; c < width; c++)
            {
                columns[c] = Join(parts, c);
            }
            if (parts.Count == 1)
            {
                parts[0].Column(width).Dispose(); // the provenance column, the one array not handed on
            }
            else
            {
                foreach (var p in parts)
                {
                    p.Dispose(); // every output column was copied by the concatenator
                }
            }
            parts.Clear();
            return new LateralResult(new RecordBatch(_outputSchema, columns, rows), origin);
        }

        private static IArrowArray Join(List<RecordBatch> parts, int column)
        {
            if (parts.Count == 1)
            {
                return parts[0].Column(column);
            }
            var slices = new IArrowArray[parts.Count];
            for (int i = 0; i < parts.Count; i++)
            {
                slices[i] = parts[i].Column(column);
            }
            return ArrowArrayConcatenator.Concatenate(slices);
        }

        /// <summary>
        /// Refuses a chunk whose statement produced a different shape from the one declared at bind.
        /// </summary>
        /// <remarks>
        /// ⚠⚠ NOT defensive: the template renders anew for every chunk and may legitimately render DIFFERENT
        /// SQL, so a chunk producing different columns is reachable from an ordinary template. The host
        /// builds its Arrow→DuckDB converters from the DECLARED schema and reads batches through them, so an
        /// unchecked mismatch is read as DATA.
        /// <para>⚠ Count, names and type IDs — the same three the host's own declared-source check compares,
        /// and with the same limit: a change of type PARAMETERS alone (decimal(9,2) to decimal(18,4)) passes.
        /// Apache.Arrow's IArrowType.Equals is REFERENCE equality, so a fuller comparison needs a structural
        /// comparer this codebase has already noted is worth consolidating.</para>
        /// </remarks>
        private void Verify(Schema arrived)
        {
            int width = _outputSchema.FieldsList.Count;
            string? problem = null;
            if (arrived.FieldsList.Count != width + 1)
            {
                problem = $"{arrived.FieldsList.Count - 1} columns where {width} were declared";
            }
            else
            {
                for (int i = 0; i < width && problem is null; i++)
                {
                    var d = _outputSchema.FieldsList[i];
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
                    $"{FunctionName}: a rendered statement produced {problem}. Every chunk must produce the "
                    + $"columns the schema-probe render declared — branch on {FluidEngine.IsBindVariable} to "
                    + "declare them, and keep every other branch to that same shape.");
            }
        }

        public void Dispose() => _session.Dispose();
    }
}
