// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Fabricator.Bridge;

namespace Fabricator.FluidPlugin;

/// <summary>
/// <c>fluid_query(template, params, arg0, arg1, …)</c> — a Liquid template rendered to a statement that this
/// function runs itself, with the scan's PUSHED FILTER and PROJECTION handed to the template so it can fold
/// them into what it generates.
/// </summary>
/// <remarks>
/// <para>
/// ⚠⚠ <b>"PUSHDOWN" IS NOT WHAT THIS BUYS OVER <see cref="FluidReplacementQueryFunction"/>, and reading it
/// that way makes the function look redundant.</b> <c>fluid_replacement_query</c> DISAPPEARS at bind, so
/// DuckDB binds the generated statement directly and its pushdown is already full and free — better than any
/// hint can be. What this one buys is that the TEMPLATE SEES the predicate. DuckDB can only push a filter
/// through what it can see through; an aggregate, a volatile call, a remote read or an opaque function in
/// the generated statement all stop it. A template that is HANDED the predicate can fold it into the thing
/// the optimiser cannot reach into — a remote <c>WHERE</c>, a partition choice, a narrower scan.
/// </para>
/// <para>
/// ⚠⚠ <b>IT IS A HINT THE TEMPLATE MAY IGNORE, and that is the whole safety property.</b> The host leaves
/// every pushed predicate in the plan (<c>FabricatorComplexFilterPushdown</c> does not erase), so DuckDB
/// re-applies all of them whatever the template did. A template that never mentions <c>filters</c> is
/// therefore CORRECT, just slower — which is what let the projection hint ship across every existing surface
/// without touching one callee.
/// </para>
/// <para>
/// ⚠⚠ <b>WHAT MAKES THAT TRUE IS THE HOST, NOT <see cref="SupportsFilterPushdown"/> — and a comment here
/// claimed otherwise until a MUTANT survived.</b> Flipping that flag to <c>true</c> passes this whole
/// section, because <b>nothing reads it</b> (its own contract doc says so). So the <c>false</c> below is a
/// STATEMENT OF A GUARANTEE WE DO NOT MAKE, not a switch that protects anything today — and the day it is
/// wired, a careless <c>true</c> becomes silently dropped rows. Do not read the mutant's survival as "the
/// value does not matter": it means the gate cannot see it, which is a reason to get it right by reading
/// rather than by testing.
/// </para>
/// <para>
/// <b>What the template gets, at scan time only</b> (none of it exists at bind — the planner has not run):
/// <list type="bullet">
/// <item><c>filters</c> — the top-level conjuncts as <c>{ column, op, value }</c>, and the SAME list as the
/// DuckDB variable <c>getvariable('filters')</c>.</item>
/// <item><c>filter_sql</c> — the whole pushed predicate as DuckDB SQL, rendered BY DUCKDB (see
/// <see cref="FluidQueryTableBinding.FilterSqlVariable"/>).</item>
/// <item><c>projected</c> — the output columns this plan reads, as a Fluid list and as
/// <c>getvariable('projected')</c>.</item>
/// </list>
/// </para>
/// <para>
/// ⚠ <b>The tail arguments are BIND-TIME CONSTANTS</b>, staged as <c>input_table</c> under <c>arg_0</c>,
/// <c>arg_1</c>, … — deterministic, unlike <c>fluid_query_lateral</c>'s wire names, which fall back to the
/// rendered EXPRESSION TEXT of the call-site argument. ⚠⚠ <b>A caller CANNOT rename them with
/// <c>name := expr</c> the way <see cref="FluidScalarFunction"/> allows</b>: MEASURED, DuckDB reads
/// <c>name := v</c> on a TABLE function as a NAMED PARAMETER and removes the value from the positional list
/// entirely (<c>fabricator_seq(zzz := 3)</c> fails as <c>fabricator_seq()</c>), so the alias never reaches a
/// table function's bind. That asymmetry is DuckDB's, not ours.
/// </para>
/// </remarks>
internal sealed class FluidQueryTableFunction : ITableFunction
{
    internal const string FunctionName = "fluid_query";

    public string Name => FunctionName;

    public Schema Parameters => new(new[]
    {
        // NON-nullable: a NULL template has no statement to generate, so the host refuses it by parameter
        // name rather than failing somewhere inside the parser.
        Params.Positional("template", StringType.Default, nullable: false),
        // POSITIONAL, matching fluid_query_lateral's signature (user's ask) rather than
        // fluid_replacement_query's `params :=`. A table function's positional slots are constants by
        // construction, so there is nothing to gain from the named form here — and matching the lateral is
        // what lets one template body be moved between the two surfaces. The SQLNULL sentinel => ANY, so the
        // bag may be a STRUCT (preferred), a MAP, a JSON string, a LIST or a plain scalar.
        Params.Positional("params", NullType.Default),
        Params.VarArgs("arg"),
    }, metadata: null);

    public ITableFunctionBinding Bind(RecordBatch args)
    {
        var template = FluidRelationInput.ReadTemplate(FunctionName, args);
        // ⚠⚠ COPIED, not retained, and all three of these for the same reason: `args` belongs to the host and
        // its lifetime ends with this call, while every render happens later — at scan, and again on each
        // re-execution of a prepared statement. FluidValueModel.CaptureBag is eager all the way down; the
        // other two are IPC round trips.
        var bag = FluidValueModel.ArgColumn(args, "params");
        var parameters = FluidValueModel.CaptureBag(bag, 0);
        var paramsRows = FluidValueModel.CopyBagRow(bag, 0);
        var input = CopyTail(args);

        using var probe = FluidRenderSession.TryCreate()
            ?? throw new InvalidOperationException(
                $"{FunctionName} needs the hosting DuckDB to determine its output columns, and it is not "
                + "available here.");
        // ⚠ NO `projectedSchema` and no filters: the planner has not run, so neither exists yet. A template
        // that reads them must branch on is_bind — the same split every other surface's `projected` has.
        var ctx = FluidRelationInput.NewContext(FunctionName, probe, parameters, isBind: true,
                                                paramsRows: paramsRows);
        FluidRelationInput.CreateEmptyInput(probe, input.Schema);
        FluidHostQuery.BindLazyRelation(ctx, FluidRelationInput.InputTable);
        var generated = FluidEngine.RenderOn(FunctionName, template, ctx);
        var outputSchema = FluidRelationInput.DescribeGenerated(FunctionName, probe, generated);
        return new FluidQueryTableBinding(template, parameters, paramsRows, input, outputSchema);
    }

    /// <summary>The tail arguments as a standalone one-row relation — this call's <c>input_table</c>.</summary>
    /// <remarks>
    /// ⚠ An IPC round trip rather than a slice: a slice would still borrow the host's buffers, and these have
    /// to outlive <see cref="Bind"/>. Same copier shape as <c>FluidValueModel.CopyBagRow</c>.
    /// <para>
    /// ⚠⚠ With NO tail arguments the relation has ZERO columns, which Apache.Arrow cannot represent across
    /// the C interface in either direction — so a placeholder column appears exactly then, as it does for
    /// <see cref="FluidScalarFunction.PlaceholderColumn"/>. Unlike the scalar's, it carries no row count
    /// obligation: this surface's output cardinality is the generated statement's own.
    /// </para>
    /// </remarks>
    private static RecordBatch CopyTail(RecordBatch args)
    {
        var fields = new List<Field>();
        var columns = new List<IArrowArray>();
        for (int c = 2; c < args.ColumnCount; c++)
        {
            fields.Add(new Field(args.Schema.FieldsList[c].Name, args.Schema.FieldsList[c].DataType,
                                 nullable: true));
            columns.Add(args.Column(c));
        }
        if (fields.Count == 0)
        {
            fields.Add(new Field(FluidScalarFunction.PlaceholderColumn, Int64Type.Default, nullable: false));
            columns.Add(new Int64Array.Builder().Append(0).Build());
        }
        var view = new RecordBatch(new Schema(fields, metadata: null), columns, 1);
        var ms = new MemoryStream();
        using (var w = new ArrowStreamWriter(ms, view.Schema, leaveOpen: true))
        {
            w.WriteRecordBatch(view);
            w.WriteEnd();
        }
        ms.Position = 0;
        using var r = new ArrowStreamReader(ms);
        return r.ReadNextRecordBatch()
               ?? throw new InvalidOperationException(FunctionName + ": could not copy the call arguments.");
    }
}

/// <summary>One bound <c>fluid_query</c> call: the constants, and the schema its bind render declared.</summary>
internal sealed class FluidQueryTableBinding : ITableFunctionBinding
{
    /// <summary>The whole pushed predicate as DuckDB SQL — a Fluid value, deliberately NOT a DuckDB variable.</summary>
    /// <remarks>
    /// ⚠ It is TEXT for the template to splice into the statement it generates, which is a Liquid job. As a
    /// DuckDB variable it could not be used as a predicate anyway (<c>WHERE getvariable('filter_sql')</c> is
    /// a boolean test OF THE STRING), so it would be a name with no use. <c>filters</c> and <c>projected</c>
    /// ARE variables because they are DATA a SQL expression can transform.
    /// </remarks>
    internal const string FilterSqlVariable = "filter_sql";

    /// <summary>The pushed conjuncts — a Fluid value AND a DuckDB variable of the same three members.</summary>
    internal const string FiltersVariable = "filters";

    private readonly string _template;
    private readonly object? _parameters;
    private readonly RecordBatch? _paramsRows;
    private readonly RecordBatch _input;

    internal FluidQueryTableBinding(string template, object? parameters, RecordBatch? paramsRows,
                                    RecordBatch input, Schema outputSchema)
    {
        _template = template;
        _parameters = parameters;
        _paramsRows = paramsRows;
        _input = input;
        OutputSchema = outputSchema;
    }

    public Schema OutputSchema { get; }

    /// <summary>
    /// ⚠ FALSE, and it must stay false: the template MAY ignore the filter hint, so this binding cannot
    /// promise a filtered result. ⚠⚠ NOTHING READS THIS FLAG TODAY (see its contract doc), so a mutant
    /// setting it <c>true</c> SURVIVES the gate — it is the claim that is wrong, not the behaviour, and it
    /// becomes dropped rows the day the flag is honoured.
    /// </summary>
    public bool SupportsFilterPushdown => false;

    /// <summary>⚠ TRUE because <c>FluidRelationInput.Wrap</c> narrows the result by NAME whatever the
    /// template did, so the batches always carry exactly the declared set.</summary>
    public bool SupportsProjectionPushdown => true;

    public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
    {
        // ⚠⚠ EVERY LINE OF THE WORK IS EAGER, AND FOR A PLUGIN THAT IS THE ONLY OPTION. The ambients this
        // needs — the host-FS opener above all — are AsyncLocal PER ABI CROSSING, and an async iterator's
        // body does not begin until the first batch PULL, which is a different crossing on whatever thread
        // DuckDB pulls from. Building the session there SIGSEGV'd inside HostFs.OpenConnection: the recorded
        // rule "a global table function must read every ambient in Execute(), never in the iterator", in its
        // third instance.
        //
        // ⚠ The Bridge's own readers solve it by CAPTURING (`var opener = AmbientOpener.Current`) and handing
        // the value to their lazy stream. A PLUGIN cannot: AmbientOpener lives in Fabricator.Bridge, which a
        // plugin deliberately does not reference. So doing the work here is not a workaround, it is the
        // available correct shape — and it is worth knowing before anyone "tidies" this into an iterator.
        var spec = scan.Spec;
        // ⚠⚠ THE SAME RESOLVER THE HOST DECLARED WITH. TableFunctionBindingAdapter narrows the stream's
        // declared schema with ProjectionPlan, and a second derivation of "which columns, in what order"
        // would put arrow_ingest one edit away from reading past the end (SIGSEGV, not a wrong answer). That
        // is why ProjectionPlan moved into Fabricator.Common — so a plugin can use the one copy.
        var outputSchema = ProjectionPlan.Schema(OutputSchema, spec?.Columns);
        // ⚠ Consumes and disposes scan.FilterValues, which must happen HERE for a second reason: a scan that
        // is bound and never pulled would otherwise leak the host's stream (StaticTableFunction.Execute's
        // lifetime note).
        var filters = FluidFilterModel.Read(spec?.Filter, scan.FilterValues);

        IArrowArrayStream stream;
        // ⚠⚠ THE SESSION IS DISPOSED BEFORE THE ROWS ARE READ, AND THAT IS SAFE RATHER THAN CLEVER: a host
        // connection is REFERENCE-COUNTED and every result stream holds its own reference, so the connection
        // — and the temporary catalog holding `input_table` — dies with the LAST of them, not with this
        // handle. It is the same property `publish()` is built on.
        using (var session = FluidRenderSession.TryCreate()
                   ?? throw new InvalidOperationException(
                       $"{FluidQueryTableFunction.FunctionName} needs the hosting DuckDB, which is not "
                       + "available here."))
        {
            // ⚠ PER EXECUTION, not per binding: a binding is reused across prepared re-executions, so a
            // session or context built at bind would carry one execution's temp tables into the next.
            var ctx = FluidRelationInput.NewContext(FluidQueryTableFunction.FunctionName, session, _parameters,
                                                    isBind: false, outputSchema, _paramsRows);
            FluidValueModel.SetVariable(ctx, FiltersVariable, FluidFilterModel.AsFluid(filters));
            FluidValueModel.SetVariable(ctx, FilterSqlVariable, spec?.NativeFilter ?? string.Empty);

            var token = session.RegisterRows(_input);
            try
            {
                session.ExecuteNonQuery(
                    $"CREATE OR REPLACE TEMP TABLE {DuckSql.QuoteIdent(FluidRelationInput.InputTable)} AS "
                    + $"SELECT * FROM fabricator_scan({DuckSql.Literal(token)})");
            }
            finally
            {
                session.ReleaseRows(token);
            }
            FluidHostQuery.BindLazyRelation(ctx, FluidRelationInput.InputTable);
            FluidFilterModel.DeclareVariables(session, filters, outputSchema);

            var generated = FluidEngine.RenderOn(FluidQueryTableFunction.FunctionName, _template, ctx);
            if (string.IsNullOrWhiteSpace(generated))
            {
                throw new ArgumentException(
                    $"{FluidQueryTableFunction.FunctionName}: the template rendered nothing; it must render "
                    + "a SELECT.");
            }
            stream = session.Query(FluidRelationInput.Wrap(generated, outputSchema));
            FluidRelationInput.Verify(FluidQueryTableFunction.FunctionName, "render", stream.Schema,
                                      outputSchema);
        }
        return new Rows(stream, ct);
    }

    /// <summary>The result stream as an async sequence whose enumerator ALWAYS releases it.</summary>
    /// <remarks>
    /// ⚠⚠ HAND-WRITTEN RATHER THAN AN `async IAsyncEnumerable` ITERATOR, and the difference is the whole
    /// point: a compiler-generated iterator that is never MOVED runs no <c>finally</c>, so a scan that binds
    /// and never pulls — a <c>LIMIT 0</c>, a short-circuited join — would leak the open result.
    /// <c>AsyncEnumerableArrowStream</c> takes its enumerator in its CONSTRUCTOR and disposes it in
    /// <c>Dispose</c>, so <c>DisposeAsync</c> here runs in that case too. It matters for this surface and not
    /// for the collector/in-out ones because theirs open nothing until they are pulled.
    /// </remarks>
    private sealed class Rows : IAsyncEnumerable<RecordBatch>
    {
        private readonly IArrowArrayStream _stream;
        private readonly CancellationToken _ct;

        internal Rows(IArrowArrayStream stream, CancellationToken ct)
        {
            _stream = stream;
            _ct = ct;
        }

        public IAsyncEnumerator<RecordBatch> GetAsyncEnumerator(CancellationToken ct = default)
            => new Enumerator(_stream, _ct == default ? ct : _ct);

        private sealed class Enumerator : IAsyncEnumerator<RecordBatch>
        {
            private readonly IArrowArrayStream _stream;
            private readonly CancellationToken _ct;
            private bool _done;

            internal Enumerator(IArrowArrayStream stream, CancellationToken ct)
            {
                _stream = stream;
                _ct = ct;
            }

            public RecordBatch Current { get; private set; } = null!;

            public async ValueTask<bool> MoveNextAsync()
            {
                if (_done)
                {
                    return false;
                }
                _ct.ThrowIfCancellationRequested();
                var batch = await _stream.ReadNextRecordBatchAsync(_ct).ConfigureAwait(false);
                if (batch is null)
                {
                    _done = true;
                    return false;
                }
                Current = batch;
                return true;
            }

            public ValueTask DisposeAsync()
            {
                _done = true;
                _stream.Dispose();
                return default;
            }
        }
    }

    /// <summary>⚠ Nothing to release: the two copied batches are small, and the framework may dispose this
    /// binding while the stream it handed back is still being pulled (see <see cref="ITableFunctionBinding"/>),
    /// so freeing them here would be a use-after-free on exactly the ordinary path.</summary>
    public void Dispose()
    {
    }
}
