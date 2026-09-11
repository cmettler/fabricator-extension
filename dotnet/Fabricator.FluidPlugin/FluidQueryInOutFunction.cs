// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using Apache.Arrow;
using Apache.Arrow.Types;
using Fabricator.Bridge;

namespace Fabricator.FluidPlugin;

/// <summary>
/// <c>fluid_query_inout(template, &lt;input&gt; [, params := …])</c> — the STREAMING sibling of
/// <see cref="FluidQueryBatchFunction"/>: the same body, registered on the table-in-out exchange instead of
/// the collector. The template is rendered ONCE PER INPUT CHUNK, with that chunk's rows in hand as
/// <c>input_table</c>, and each rendered statement's rows are emitted before the next chunk is read.
/// </summary>
/// <remarks>
/// <para>
/// ⚠⚠ <b>WHAT IT BUYS IS BOUNDED MEMORY, AND THAT IS THE ONLY REASON IT EXISTS.</b> The collector must
/// buffer the WHOLE input before its first render — inherent to a collector, and true even at a small
/// <c>batchsize</c>, which is why that parameter is about how many rows each render SEES and never about
/// memory. This surface holds one chunk at a time, so a template that fans out over a large input no longer
/// stages the input first. Everything else about the two is the same, by construction: they share
/// <see cref="FluidRelationInput"/>.
/// </para>
/// <para>
/// ⚠⚠ <b>AND WHAT IT COSTS IS THE WHOLE-INPUT RENDER, which is not a parameter you can set.</b> A streaming
/// in-out CANNOT hold output until input EOF: its all-input-done hook is handed no <c>DataChunk</c>, so
/// anything held back is DRAINED AND DISCARDED (docs/inout-collector-mode.md). So there is no
/// <c>batchsize</c> here and no whole-table mode — <b>the chunk IS the batch</b>, and how the input divides
/// into chunks is DuckDB's business, not the caller's. If the template's statement must see the whole
/// relation at once, or must see an exact row count per render, use <c>fluid_query_batch</c>.
/// </para>
/// <para>
/// ⚠⚠ <b>AN EMPTY INPUT RENDERS NOTHING HERE and ONCE on the collector</b> — a real divergence, measured
/// and gated, not an oversight. With no rows there are no chunks, and a render is per chunk; the collector
/// renders once because a template is a statement GENERATOR whose output need not depend on the rows. A
/// caller who needs the generator to fire regardless wants the collector.
/// </para>
/// <para>
/// ⚠ <b>SQL state carries between chunks, Liquid state does not</b> — one session and one
/// <c>TemplateContext</c> per EXECUTION, exactly as on the collector: a temp table one chunk's
/// <c>{% exec %}</c> creates is there for the next, while a <c>{% assign %}</c> is not, because Fluid
/// renders into a child scope and pops it. Chunks are rendered in order and never concurrently, which is
/// what makes the SQL half safe. (The PARALLEL sibling is <c>fluid_query_lateral</c>, which is correlated
/// and has no cross-chunk state at all.)
/// </para>
/// <para>
/// ⚠ <c>publish()</c> is refused for the same measured reason as on the collector — see
/// <see cref="FluidRelationInput.PublishRefusal"/>.
/// </para>
/// </remarks>
internal sealed class FluidQueryInOutFunction : IInOutFunction
{
    internal const string FunctionName = "fluid_query_inout";

    public string Name => FunctionName;

    public Schema Parameters => new(new[]
    {
        // NON-nullable: a NULL template has no statement to generate, so the host refuses it by parameter
        // name rather than failing somewhere inside the parser.
        Params.Positional("template", StringType.Default, nullable: false),
        // ⚠ The table input may sit BETWEEN positionals — DuckDB pushes a placeholder for the subquery slot
        // — which is what lets the template stay first and read like fluid_query_batch's.
        Params.TableInput("input"),
        // The same bag fluid_query and fluid_render take: STRUCT, MAP or a JSON string.
        Params.Named("params", NullType.Default),
        // ⚠⚠ NO `batchsize`, and its ABSENCE is the contract rather than an omission: a render is one input
        // chunk, and holding rows back to fill a group would need a tail this operator cannot emit.
    }, metadata: null);

    public IInOutFunctionBinding Bind(RecordBatch? args, Schema inputSchema)
    {
        var template = FluidRelationInput.ReadTemplate(FunctionName, args);
        // ⚠⚠ CAPTURED, not retained: the args batch belongs to the framework and its lifetime ends with this
        // call, while the chunks render much later. FluidValueModel.Capture is eager all the way down, so
        // what comes back holds no Arrow memory.
        var parameters = FluidValueModel.CaptureBag(FluidValueModel.ArgColumn(args, "params"), 0);

        // ⚠⚠ THE OUTPUT SCHEMA IS WHAT THE TEMPLATE ACTUALLY PRODUCES, not what it claims. The probe renders
        // with is_bind = true against an EMPTY input_table and asks DuckDB to bind the result; a template may
        // use the flag to skip expensive setup, but the columns still come from binding what it rendered. A
        // declaration written twice — once for the probe, once for real — is a declaration that drifts, and
        // the drift would be read as DATA.
        using var probe = FluidRenderSession.TryCreate()
            ?? throw new InvalidOperationException(
                $"{FunctionName} needs the hosting DuckDB to determine its output columns, and it is not "
                + "available here.");
        var ctx = FluidRelationInput.NewContext(FunctionName, probe, parameters, isBind: true);
        FluidRelationInput.CreateEmptyInput(probe, inputSchema);
        // ⚠ Bound at the probe too, EMPTY, so `{{ input_table.size }}` answers 0 here rather than failing.
        FluidHostQuery.BindLazyRelation(ctx, FluidRelationInput.InputTable);
        var generated = FluidEngine.RenderOn(FunctionName, template, ctx);
        var outputSchema = FluidRelationInput.DescribeGenerated(FunctionName, probe, generated);
        return new Binding(template, parameters, outputSchema);
    }

    private sealed class Binding : IInOutFunctionBinding
    {
        private readonly string _template;
        private readonly object? _parameters;

        internal Binding(string template, object? parameters, Schema outputSchema)
        {
            _template = template;
            _parameters = parameters;
            OutputSchema = outputSchema;
        }

        public Schema OutputSchema { get; }

        // ⚠ DECLARING that this binding HONOURS the hint: the exchange's stream schema is read before its
        // first batch, so narrowing the output without saying so here would have the host read narrow
        // batches through wide converters.
        public Schema ProjectedOutputSchema(IReadOnlyList<int>? projected) =>
            FluidRelationInput.Narrow(OutputSchema, projected);

        public void Dispose()
        {
            // Nothing execution-scoped is held on the binding: the session and the context are created per
            // DoExchange, because a binding is reused across prepared re-executions and one built here would
            // carry an execution's temp tables and Liquid state into the next.
        }

        public IAsyncEnumerable<RecordBatch> DoExchange(IAsyncEnumerable<RecordBatch> input,
                                                        CancellationToken ct = default) =>
            DoExchange(input, null, ct);

        public async IAsyncEnumerable<RecordBatch> DoExchange(
            IAsyncEnumerable<RecordBatch> input,
            IReadOnlyList<int>? projected,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var outputSchema = FluidRelationInput.Narrow(OutputSchema, projected);
            // ⚠ PER EXECUTION, not per binding — see Dispose.
            using var session = FluidRenderSession.TryCreate()
                ?? throw new InvalidOperationException(
                    $"{FunctionName} needs the hosting DuckDB, which is not available here.");
            var ctx = FluidRelationInput.NewContext(FunctionName, session, _parameters, isBind: false,
                                                    outputSchema);

            await foreach (var chunk in input.WithCancellation(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                // ⚠ A zero-length chunk is not a group to render — but it still owes the sentinel, or the
                // operator would never be told this input was consumed.
                if (chunk.Length > 0)
                {
                    // ⚠⚠ CONSUMED INSIDE THE LOOP, and this is what makes the surface bounded: the rows move
                    // into DuckDB now and the batch is released, rather than being accumulated the way the
                    // collector must. The framework frees a chunk's Arrow buffers once consumed, so holding
                    // one past this point would be a use-after-free as well as a memory cost.
                    DefineInput(session, chunk);
                    // ⚠⚠ A FRESH lazy value per chunk, because it CACHES: one carried across chunks would
                    // serve the first chunk's rows to every later one, silently. The SQL table and the Liquid
                    // value are repointed together, so the two access paths cannot disagree.
                    FluidHostQuery.BindLazyRelation(ctx, FluidRelationInput.InputTable);
                    var generated = FluidEngine.RenderOn(FunctionName, _template, ctx);
                    if (string.IsNullOrWhiteSpace(generated))
                    {
                        throw new ArgumentException(
                            $"{FunctionName}: the template rendered nothing for an input chunk; it must "
                            + "render a SELECT.");
                    }
                    using var stream = session.Query(FluidRelationInput.Wrap(generated, outputSchema));
                    FluidRelationInput.Verify(FunctionName, "chunk", stream.Schema, outputSchema);
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
                chunk.Dispose();
                // The per-input-chunk sentinel: NEED_MORE_INPUT.
                yield return InOutExchange.EmptyBatch(outputSchema);
            }
        }

        /// <summary>Points <c>input_table</c> at this chunk's rows.</summary>
        /// <remarks>
        /// ⚠ A TABLE rather than a view over staged rows, which is the whole structural difference from the
        /// collector: there is nothing to stage, because the chunk is the group. It is replaced per chunk,
        /// so at most one chunk's rows are in DuckDB at a time.
        /// </remarks>
        private static void DefineInput(FluidRenderSession session, RecordBatch chunk)
        {
            var token = session.RegisterRows(chunk);
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
        }
    }
}
