// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Apache.Arrow;
using Apache.Arrow.Types;
using Fabricator.Bridge;
using Fluid;
using Fluid.Values;

namespace Fabricator.FluidPlugin;

/// <summary>
/// <c>fluid_aggregate(template, params, value)</c> — a Liquid template rendered ONCE PER GROUP with that
/// group's rows in hand, reducing them to ONE value whose TYPE the template declares itself.
/// </summary>
/// <remarks>
/// <para>
/// <code>
/// SELECT g, fluid_aggregate(
///   '{% if is_bind %}select NULL::VARCHAR'
///   '{% else %}{% capture s %}{% for r in rows %}{{ r.a }}|{{ r.b }};{% endfor %}{% endcapture %}'
///   'select md5({{ s | sql }}){% endif %}', NULL, struct_pack(a := a, b := b)) FROM t GROUP BY g;
/// </code>
/// </para>
/// <para>
/// ⚠⚠ <b>IT RENDERS TEXT, WHERE <see cref="FluidScalarFunction"/> RENDERS SQL — and that difference is
/// forced, not a preference.</b> A scalar's rows live in DuckDB, so its template emits an expression DuckDB
/// evaluates. An aggregate's rows live HERE, in the accumulator, so there is nothing for DuckDB to evaluate
/// them with: the reduction happens in Liquid and the render IS the value. A template that wants SQL can
/// still have it — <c>{% query %}</c> / <c>{% exec %}</c> work in any render — but that is one statement per
/// GROUP, so it is the author's explicit choice rather than this function's shape.
/// </para>
/// <para>
/// ⚠⚠ <b>THE RESULT TYPE COMES FROM THE <c>is_bind</c> RENDER, which is the whole reason this needed
/// ABI v89.</b> Under <c>is_bind</c> the template renders a SELECT of the result type
/// (<c>select NULL::VARCHAR</c>), exactly <see cref="FluidScalarFunction"/>'s contract and through the same
/// helper, and DuckDB binding that statement is what decides the type — no type ladder here, no type name
/// parsed by us. An aggregate's session is opened from its BIND, once per call site, which is what makes a
/// per-call-site type expressible at all.
/// </para>
/// <para>
/// ⚠⚠ <b>THE NON-BIND RENDER IS A SELECT THAT IS EXECUTED, exactly as on every other Fluid surface</b>
/// (user-directed). The VALUE is whatever DuckDB computes, so nothing is parsed back out of text: a STRUCT,
/// LIST or MAP result is just a value, and the reduction may use any DuckDB function rather than only what
/// Liquid can express. ⚠ The CAST that remains is <c>WrapExecute</c>'s — the one that makes the
/// <c>is_bind</c> render a DECLARATION rather than a thing to match — not the text→type parse that went.
/// </para>
/// <para>
/// ⚠⚠ <b>ONE STATEMENT PER GROUP</b>: the statements differ per group, which is the point, so they cannot be
/// folded into one. MEASURED locally at ~14 µs for a trivial statement and <b>~0.5 ms</b> for a real hashing
/// one. That bounds this to modest group counts — which its memory already did, since the accumulator holds
/// every group's rows and cannot spill.
/// </para>
/// <para>
/// ⚠ <b>An EMPTY render is an ERROR, not NULL.</b> Every group must render a SELECT, including one that was
/// never updated; <c>{% if rows.size == 0 %}select NULL{% else %}…{% endif %}</c> is how an empty group gets
/// a different answer. (It used to mean NULL, which silently conflated "nothing to say" with a template bug.)
/// </para>
/// <para>
/// ⚠⚠ <b>ROW ORDER IS THE CALLER'S, AND IT MATTERS HERE MORE THAN ANYWHERE ELSE IN THIS PLUGIN.</b> An
/// aggregate is unordered by definition, so a hash over <c>rows</c> is only deterministic if the caller
/// orders it: <c>fluid_aggregate(tpl, NULL, v ORDER BY k)</c>. MEASURED that DuckDB's aggregate ORDER BY
/// reaches <c>Update</c> in order (<c>ORDER BY a DESC</c> arrives 7,4,1 where ASC arrives 1,4,7). Without
/// it, parallel aggregation and combine order decide the result — a value that changes between runs with
/// nothing failing.
/// </para>
/// <para>
/// ⚠ <b>Not spillable</b> (<see cref="IAggregateFunction.SupportsSpill"/> stays false): the accumulator
/// holds the group's rows, which is unbounded, where spilling requires a state that fits a 1 KB blob. A
/// high-cardinality GROUP BY therefore holds every group's rows in managed memory at once.
/// </para>
/// </remarks>
internal sealed class FluidAggregateFunction : IAggregateFunction
{
    internal const string FunctionName = "fluid_aggregate";

    /// <summary>The group's rows, as a Fluid list of the per-row <c>value</c> arguments.</summary>
    internal const string RowsVariable = "rows";

    /// <summary>The single relation column's name when the per-row argument is NOT a struct.</summary>
    /// <remarks>⚠ The declared parameter's own name, so SQL and the signature agree.</remarks>
    internal const string RowsValueColumn = "value";

    public string Name => FunctionName;

    public Schema Parameters => new(new[]
    {
        // NON-nullable: a NULL template has no value to render, so the host refuses it by parameter name.
        Params.Positional("template", StringType.Default, nullable: false),
        // The SQLNULL sentinel => ANY, so the bag may be a STRUCT (preferred), a MAP, a JSON string, a LIST
        // or a plain scalar — the same contract every other Fluid surface's params has.
        Params.Positional("params", NullType.Default),
        // ⚠ ONE per-row argument, and that is DuckDB's limit rather than a choice: a variadic tail is
        // REFUSED on aggregates (FabricatorRefuseVarArgs), because the update crossing sends a bare
        // ArrowArray whose schema the managed side rebuilds from the DECLARATION — there is no per-call-site
        // width. Pass several columns as ONE struct_pack(a := a, b := b); the template reads r.a, r.b.
        Params.Positional("value", NullType.Default),
    }, metadata: null);

    /// <summary>⚠ NULL: the type is whatever the template's <c>is_bind</c> SELECT binds to (ABI v89).</summary>
    public Field? Result => null;

    /// <summary>Never called — <see cref="Bind"/> always returns a binding that creates its own states.</summary>
    public IAggregateState CreateState() =>
        throw new InvalidOperationException(
            FunctionName + " must be used through its binding (its result type is resolved at bind)");

    public IAggregateBinding Bind(ScalarBindArgs args)
    {
        // ⚠ IsConstant, not "is there a value": a non-constant slot carries a NULL placeholder that no
        // inspection can tell from an explicit NULL. The result type is part of the PLAN, so it cannot
        // depend on row data — refusing by name is the honest answer.
        if (!args.IsConstant(0))
        {
            throw new InvalidOperationException(
                FunctionName + ": the template must be a constant (it is rendered at bind to resolve the "
                + "result type, so it cannot depend on row values).");
        }
        if (args.Count > 1 && !args.IsConstant(1))
        {
            throw new InvalidOperationException(
                FunctionName + ": the params bag must be a constant (the template is rendered with it at "
                + "bind). Per-row values belong in the third argument, which the template reads as `rows`.");
        }
        var template = (args.ConstantArray(0) as StringArray)?.GetString(0);
        if (string.IsNullOrWhiteSpace(template))
        {
            throw new InvalidOperationException(FunctionName + ": template must be a non-NULL VARCHAR");
        }
        var bag = args.Count > 1 ? args.ConstantArray(1) : null;
        var parameters = FluidValueModel.CaptureBag(bag, 0);
        // ⚠ COPIED: the SQL variable is staged when a render opens its connection, long after these bind
        // arguments are freed. Same rule as every other deferred surface.
        var paramsRows = FluidValueModel.CopyBagRow(bag, 0);

        // ⚠⚠ THE SESSION IS CREATED HERE AND KEPT FOR THE CALL SITE'S LIFE — it is NOT a probe. The ambients
        // a host connection needs (the opener above all) are established around THIS crossing and nowhere
        // else on the aggregate path: update, combine and finalize carry none, so a session created at
        // finalize dies with `host_connection_open failed: Attempted to dereference unique_ptr that is NULL`.
        // MEASURED exactly that, twice — once for the bind before agg_open carried the call context, and
        // once for the finalize cast afterwards.
        //
        // ⚠ A plugin cannot solve it the way the Bridge's own readers do (capture AmbientOpener.Current and
        // hand it on): AmbientOpener lives in Fabricator.Bridge, which a plugin deliberately does not
        // reference. Same conclusion fluid_query reached — do the host work where the ambients are.
        //
        // ⚠ It costs ONE connection per call site, held for the statement. Cheap beside fluid_scalar's one
        // per CHUNK, and it is what lets a template use {% query %} from a finalize render at all.
        var session = FluidRenderSession.TryCreate()
            ?? throw new InvalidOperationException(
                FunctionName + " needs the hosting DuckDB to determine its result type, and it is not "
                + "available here.");
        try
        {
            // ⚠⚠ THE RESOLVED per-row TYPE, taken from the CALL's own arguments rather than from the
            // declaration — the parameter is declared ANY (the SQLNULL sentinel), so the declaration says
            // nothing about what this call site passes. It is what shapes `rows`: a STRUCT becomes a
            // multi-column relation, anything else a single column.
            var valueField = args.Count > FluidAggregateBinding.ValueColumn && args.Values is { } v
                ? v.Schema.FieldsList[FluidAggregateBinding.ValueColumn]
                : new Field(FluidAggregateFunction.RowsValueColumn, NullType.Default, nullable: true);
            var (result, typeName) = DescribeResult(session, template!, parameters, paramsRows, valueField);
            return new FluidAggregateBinding(template!, parameters, paramsRows, result, typeName, valueField,
                                             session);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>Renders with <c>is_bind</c> and asks DuckDB what that SELECT's type is.</summary>
    /// <remarks>
    /// ⚠ The NAME comes from <c>DESCRIBE</c>, i.e. from DuckDB rendering its own type, so it re-parses in a
    /// CAST by construction. It is never the template's own text.
    /// </remarks>
    private static (Field Result, string TypeName) DescribeResult(FluidRenderSession probe, string template,
                                                                  object? parameters, RecordBatch? paramsRows,
                                                                  Field valueField)
    {

        var ctx = FluidRelationInput.NewContext(FunctionName, probe, parameters, isBind: true,
                                                paramsRows: paramsRows);
        // ⚠ `rows` is bound EMPTY at bind, so `{{ rows.size }}` answers 0 rather than failing — a name that
        // resolves at finalize and not at bind is the split this plugin already records as a trap.
        FluidValueModel.SetVariable(ctx, RowsVariable, System.Array.Empty<object>());
        var statement = FluidEngine.RenderOn(FunctionName, template, ctx);
        if (string.IsNullOrWhiteSpace(statement))
        {
            throw new InvalidOperationException(
                FunctionName + ": the template rendered nothing under " + FluidEngine.IsBindVariable
                + " = true. It must render a SELECT of the result type there — `select NULL::<type>` is the "
                + "usual answer, e.g. select NULL::VARCHAR.");
        }
        // ⚠⚠ THE EMPTY RELATION IS WHAT LETS A TEMPLATE SKIP `is_bind` ENTIRELY. `select max(t) from rows t`
        // has a type only because `rows` exists HERE with its real COLUMN TYPES and no rows — MEASURED that
        // DESCRIBE over exactly that answers STRUCT(a INTEGER, b VARCHAR). Without it the probe would fail on
        // a missing table and every such template would owe a hand-written `{% if is_bind %}select NULL::…`.
        // Same property fluid_scalar's empty input_table has, for the same reason.
        using var staged = FluidAggregateBinding.StageRows(probe, valueField, rows: null);
        // ⚠⚠ SCOPED: this session's connection is PINNED and a pinned connection allows ONE live result, so
        // the type-name query below is a SECOND statement on it. Leaving this stream open is refused by name.
        Schema schema;
        using (var stream = probe.Query(FluidScalarBinding.WrapBind(statement)))
        {
            schema = stream.Schema;
        }
        if (schema.FieldsList.Count != 1)
        {
            throw new InvalidOperationException(
                FunctionName + ": the bind render must select exactly ONE column, got "
                + schema.FieldsList.Count + ".");
        }
        return (new Field("result", schema.FieldsList[0].DataType, nullable: true),
                FluidScalarBinding.DescribeTypeName(probe, statement, FunctionName));
    }
}

/// <summary>One bound <c>fluid_aggregate</c> call site: the resolved type plus the constants every group
/// re-renders with.</summary>
/// <remarks>
/// <para>
/// ⚠ ONE render context for the whole call site, created LAZILY — and for the ordinary template that costs
/// no DuckDB connection at all, because <see cref="FluidRenderSession"/> only opens one when a render runs
/// SQL. A template using <c>{% query %}</c> pays one connection per call site, not one per group.
/// </para>
/// <para>
/// ⚠⚠ <b>THE LOCK IS REQUIRED, NOT DEFENSIVE.</b> A <c>TemplateContext</c> is not thread-safe and a DuckDB
/// connection is single-threaded by contract, while DuckDB may finalize different vectors of groups on
/// different threads — the accumulators are per-group and thread-confined, this binding is not. ⚠ UPDATE
/// deliberately touches none of this (it only appends to its own accumulator), so the parallel half of
/// aggregation stays parallel; only the render serialises.
/// </para>
/// </remarks>
internal sealed class FluidAggregateBinding : IAggregateBinding, IDisposable
{
    /// <summary>The per-row argument's column in the update batch — <c>template</c> and <c>params</c> are
    /// constants this binding already holds, but they still occupy their declared slots.</summary>
    internal const int ValueColumn = 2;

    private readonly object _gate = new();
    private readonly string _template;
    private readonly object? _parameters;
    private readonly RecordBatch? _paramsRows;
    private readonly string _typeName;
    private readonly Field _valueField;
    private FluidRenderSession? _session;
    private TemplateContext? _ctx;

    internal FluidAggregateBinding(string template, object? parameters, RecordBatch? paramsRows,
                                   Field result, string typeName, Field valueField,
                                   FluidRenderSession session)
    {
        _template = template;
        _parameters = parameters;
        _paramsRows = paramsRows;
        _typeName = typeName;
        _valueField = valueField;
        _session = session;
        Result = result;
    }

    public Field Result { get; }

    public IAggregateState CreateState() => new FluidAggregateState(this);

    /// <summary>Renders one group's SELECT. ⚠ The template must render one — an empty render is refused.</summary>
    internal string Render(IReadOnlyList<RecordBatch> batches)
    {
        lock (_gate)
        {
            var ctx = Context();
            // ⚠⚠ LAZY, like `input_table` on every other relation surface: a template that reads the group
            // only in SQL — which is now the natural way to write one — pays NO per-row boxing at all. It
            // matters more here than elsewhere, because this runs once per GROUP.
            //
            // ⚠ It is lazy over the retained ARROW BATCHES rather than over the staged relation, and that is
            // forced: every group RENDERS before any group is staged (see FinalizeColumn), so there is no
            // relation to read here yet. The other surfaces stage first and can point their lazy value at the
            // temp table; the content is the same either way.
            //
            // ⚠ REBOUND PER GROUP, because a Fluid value is bound INTO the context: one bound at creation
            // would serve the FIRST group's rows to every later group — right shape, wrong rows, no error.
            // The same rule the collector's input_table records.
            FluidValueModel.SetVariable(
                ctx, FluidAggregateFunction.RowsVariable,
                new LazyRowsValue(() => FluidValue.Create(ReadRows(batches), ctx.Options)));
            var text = FluidEngine.RenderOn(FluidAggregateFunction.FunctionName, _template, ctx);
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException(
                    FluidAggregateFunction.FunctionName + ": the template rendered nothing for a group. It "
                    + "must render a SELECT for every group, including an EMPTY one — branch on `rows.size` "
                    + "if an empty group needs a different answer (`select NULL` is the usual one).");
            }
            return text;
        }
    }

    /// <summary>
    /// Runs each group's rendered SELECT and returns the finished column — one row per group, in state order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ <b>THE STATEMENT IS EXECUTED, so the VALUE is whatever DuckDB computes — there is no text
    /// intermediate and nothing is parsed back</b> (user-directed: *"the return value should be an explicit
    /// select again which is executed like in fluid scalar, batch, inout, query"*). That is what makes every
    /// Fluid surface's non-bind render mean the same thing, and it removes a whole class of question: a
    /// STRUCT, LIST or MAP result is just a value DuckDB produced, an empty render is a template error rather
    /// than a silent NULL, and the template can reach any DuckDB function for the reduction itself.
    /// </para>
    /// <para>
    /// ⚠ <b>The CAST stays, and it is not the cast that was removed.</b> It is
    /// <c>FluidScalarBinding.WrapExecute</c>'s — the one that makes the <c>is_bind</c> render a DECLARATION
    /// rather than a thing to match, so a template declaring <c>NULL::BIGINT</c> may render <c>select 42</c>
    /// without also writing INTEGER. What went away is parsing a rendered STRING into the declared type.
    /// </para>
    /// <para>
    /// ⚠⚠ <b>ONE STATEMENT PER GROUP, and that is the cost to know before reaching for this.</b> The
    /// statements differ per group — that is the point — so they cannot be folded into one. MEASURED on a
    /// local in-memory table: a TRIVIAL statement is ~14 µs per group (10 000 groups in 0.14 s), and a real
    /// hashing template that embeds its group's rows is <b>~0.5 ms per group</b> (2 000 groups in 1.0 s,
    /// with 2 000 distinct hashes — so every statement really did run). ⇒ a few thousand groups is
    /// unremarkable; a hundred thousand is not this function's shape, which its MEMORY already said, since
    /// the accumulator holds every group's rows and cannot spill. Where the reduction is really "render per
    /// row, then join", <c>md5(string_agg(fluid_render(...), ';' ORDER BY k))</c> stays far cheaper.
    /// </para>
    /// <para>
    /// ⚠ Each group's result must be exactly ONE ROW. A statement that produces none or several would
    /// mis-align every later group's value — a wrong ANSWER, not an error — so it is refused by name.
    /// </para>
    /// </remarks>
    public IArrowArray? FinalizeColumn(object?[] values)
    {
        if (values.Length == 0)
        {
            return null; // nothing to finalize; the host's default builds an empty column
        }
        lock (_gate)
        {
            var session = Session();
            var parts = new List<IArrowArray>(values.Length);
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] is not GroupPlan plan)
                {
                    throw new InvalidOperationException(
                        $"{FluidAggregateFunction.FunctionName}: group {i} produced no statement.");
                }
                // ⚠⚠ STAGED HERE, NOT AT RENDER, and that is forced: every group renders BEFORE any group
                // runs (DuckDB finalizes a whole vector of states, then the host asks for the column), so a
                // relation staged at render time would be overwritten by the next group and every statement
                // would read the LAST group's rows — a wrong answer with nothing failing. The rows therefore
                // travel with the statement in GroupPlan and are staged immediately before it runs.
                using var staged = StageRows(session, _valueField, plan.Rows);
                using var stream = session.Query(WrapExecute(plan.Statement));
                var batch = stream.ReadNextRecordBatchAsync().GetAwaiter().GetResult();
                if (batch is null || batch.Length != 1)
                {
                    throw new InvalidOperationException(
                        $"{FluidAggregateFunction.FunctionName}: a group's statement produced "
                        + $"{batch?.Length ?? 0} rows where exactly 1 is required. An aggregate owes ONE "
                        + "value per group.");
                }
                // ⚠⚠ COPIED, because this column must outlive its stream: the `using` closes the result
                // before the NEXT group's statement runs (a pinned connection allows ONE live result), and
                // the concatenation below reads every part long after that. Whether an imported stream's
                // already-returned batch is self-owning is a question about Apache.Arrow's internals —
                // guessing it wrong is a use-after-free this codebase records as INVISIBLE on Windows and
                // Linux, so it is not a question worth answering by hoping.
                parts.Add(FluidFilterModel.Copy(batch).Column(0));
                if (stream.ReadNextRecordBatchAsync().GetAwaiter().GetResult() is { } extra)
                {
                    extra.Dispose();
                    throw new InvalidOperationException(
                        $"{FluidAggregateFunction.FunctionName}: a group's statement produced more than one "
                        + "batch. An aggregate owes ONE value per group.");
                }
                // ⚠ The group's rows are done with: released here rather than at Dispose so peak memory is
                // one group's rows past the point they are consumed, not every group's at once.
                plan.Release();
            }
            return parts.Count == 1 ? parts[0] : ArrowArrayConcatenator.Concatenate(parts);
        }
    }

    /// <summary>One group's finished work: the SELECT it rendered, and the rows that SELECT reads.</summary>
    /// <remarks>
    /// ⚠ The two must travel TOGETHER — see the staging note in <see cref="FinalizeColumn"/>. Splitting them
    /// (the statement through the host, the rows on the binding) is exactly the shape that makes every group
    /// read the last group's rows.
    /// </remarks>
    internal sealed class GroupPlan
    {
        internal GroupPlan(string statement, IReadOnlyList<RecordBatch> rows)
        {
            Statement = statement;
            Rows = rows;
        }

        internal string Statement { get; }

        internal IReadOnlyList<RecordBatch> Rows { get; }

        internal void Release()
        {
            foreach (var b in Rows)
            {
                b.Dispose();
            }
        }
    }

    /// <summary>
    /// (Re)creates the temp relation the rendered SELECT reads as <c>rows</c> — this group's rows, or an
    /// EMPTY one when the group has none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ <b>A STRUCT argument is EXPANDED into columns</b> (<c>UNNEST</c>), so the SQL relation and the
    /// Liquid value agree: <c>{{ r.a }}</c> in Liquid is <c>a</c> in SQL. That is what makes
    /// <c>struct_pack(a := a, b := b)</c> — the documented way to pass several columns to an aggregate —
    /// arrive as a two-column relation rather than as one column of structs. A non-struct argument stays a
    /// single column under the parameter's own name.
    /// </para>
    /// <para>
    /// ⚠ The expansion is DuckDB's, not ours: <c>UNNEST</c> over an Arrow struct column. MEASURED to keep the
    /// column TYPES on an EMPTY relation (which is what lets the bind probe derive a result type from it) and
    /// to turn a NULL struct row into a row of NULL fields rather than dropping it.
    /// </para>
    /// <para>
    /// ⚠⚠ <b>A VIEW over the registered batch, NOT a materialized copy — the rows are already in memory as
    /// Arrow, so exposing them as a relation costs no data movement at all.</b> It is re-scannable because
    /// <c>RegisterRows</c> registers a FACTORY (<c>() =&gt; new BorrowedBatchStream(schema, rows)</c>), so
    /// each scan gets a fresh cursor over the same retained batch. ⚠ The single-use rule this codebase
    /// records elsewhere is about <c>host_query</c>'s BOUND INPUTS, which wrap one raw stream — a different
    /// mechanism. A statement reading <c>rows</c> twice is gated below.
    /// </para>
    /// <para>
    /// ⚠ An EMPTY group still gets the relation. A group DuckDB never updated is still finalized, and a
    /// template whose SELECT reads <c>rows</c> must not fail on it — it should return whatever that statement
    /// says about no rows (<c>max</c> of nothing is NULL).
    /// </para>
    /// </remarks>
    internal static StagedRows StageRows(FluidRenderSession session, Field valueField,
                                         IReadOnlyList<RecordBatch>? rows)
    {
        var (batch, owned) = rows is null ? (null, false) : Combine(rows);
        string? token = null;
        try
        {
            token = batch is null
                ? session.RegisterRows(new Schema(new[] { valueField }, null))
                : session.RegisterRows(batch);
            session.ExecuteNonQuery(
                $"CREATE OR REPLACE TEMP VIEW {DuckSql.QuoteIdent(FluidAggregateFunction.RowsVariable)} "
                + $"AS SELECT {RowsProjection(valueField)} FROM "
                + $"fabricator_scan({DuckSql.Literal(token)})");
            return new StagedRows(session, token, owned ? batch : null);
        }
        catch
        {
            if (token is not null)
            {
                session.ReleaseRows(token);
            }
            if (owned)
            {
                batch?.Dispose();
            }
            throw;
        }
    }

    /// <summary>The registration behind a staged <c>rows</c> view, released when the statement is done.</summary>
    /// <remarks>
    /// ⚠⚠ <b>The token must outlive every statement that reads the view</b> — the view holds the token, not
    /// the data, so releasing it first turns the next scan into "no named source registered". That is why
    /// staging hands one of these back instead of releasing in its own <c>finally</c>.
    /// </remarks>
    internal sealed class StagedRows : System.IDisposable
    {
        private readonly FluidRenderSession _session;
        private readonly string _token;
        private readonly RecordBatch? _owned;

        internal StagedRows(FluidRenderSession session, string token, RecordBatch? owned)
        {
            _session = session;
            _token = token;
            _owned = owned;
        }

        public void Dispose()
        {
            _session.ReleaseRows(_token);
            _owned?.Dispose();
        }
    }

    /// <summary>The single-column relation's projection: struct fields expanded, anything else as itself.</summary>
    private static string RowsProjection(Field valueField) =>
        valueField.DataType is StructType
            ? $"UNNEST({DuckSql.QuoteIdent(valueField.Name)})"
            : DuckSql.QuoteIdent(valueField.Name);

    /// <summary>One batch for the whole group (null when it has no rows), and whether WE allocated it.</summary>
    /// <remarks>
    /// ⚠ The commonest group is ONE batch, and it is handed STRAIGHT THROUGH — the state already owns it and
    /// copying it here would make staging cost a full round trip per group for nothing. <c>owned</c> is what
    /// keeps that safe: only a batch this method built gets disposed by the caller.
    /// </remarks>
    private static (RecordBatch? Batch, bool Owned) Combine(IReadOnlyList<RecordBatch> rows)
    {
        if (rows.Count == 0)
        {
            return (null, false);
        }
        if (rows.Count == 1)
        {
            return (rows[0], false);
        }
        var col = ArrowArrayConcatenator.Concatenate(rows.Select(b => b.Column(0)).ToList());
        return (new RecordBatch(rows[0].Schema, new[] { col }, col.Length), true);
    }

    /// <summary>The Liquid <c>rows</c> value: each row's argument as this plugin's value model reads it.</summary>
    /// <remarks>
    /// ⚠ <c>ReadCell</c>, so a DATE in <c>rows</c> renders like a DATE in <c>params</c> (it stamps
    /// <c>DateTimeKind.Utc</c>, without which a date renders as the PREVIOUS DAY east of UTC).
    /// ⚠ Built HERE rather than at update: a template that only reads <c>rows</c> in SQL pays no boxing.
    /// </remarks>
    private static List<object?> ReadRows(IReadOnlyList<RecordBatch> batches)
    {
        var rows = new List<object?>();
        foreach (var b in batches)
        {
            var col = b.Column(0);
            for (int i = 0; i < b.Length; i++)
            {
                rows.Add(FluidValueModel.ReadCell(col, i));
            }
        }
        return rows;
    }

    /// <summary>Wraps one group's render: casts its single column to the type the bind render declared.</summary>
    /// <remarks>
    /// ⚠ Positional alias (<c>t(x)</c>), so the wrap needs no knowledge of what the template called its
    /// column — the same shape, and the same reasoning, as <c>FluidScalarBinding.WrapExecute</c>.
    /// </remarks>
    private string WrapExecute(string statement) =>
        $"SELECT CAST(x AS {_typeName}) AS v FROM ({statement}) t(x)";

    // ⚠ Created at BIND and handed in — see the note there. Never created lazily: by update/finalize the
    // ambients a connection needs are gone.
    private FluidRenderSession Session() =>
        _session ?? throw new ObjectDisposedException(nameof(FluidAggregateBinding));

    private TemplateContext Context() =>
        _ctx ??= FluidRelationInput.NewContext(FluidAggregateFunction.FunctionName, Session(), _parameters,
                                               isBind: false, paramsRows: _paramsRows);

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        _ctx = null;
    }
}

/// <summary>One group's accumulator: the per-row <c>value</c> arguments, as Fluid values.</summary>
/// <remarks>
/// ⚠⚠ IT HOLDS THE GROUP'S ROWS, which is what makes this function unspillable and bounds its memory by the
/// group's size rather than by a fixed state. That is inherent to "render the template with the group in
/// hand": a reduction expressed in Liquid cannot be folded incrementally, because the template runs once and
/// only at the end.
/// </remarks>
internal sealed class FluidAggregateState : IAggregateState
{
    private readonly FluidAggregateBinding _binding;
    private readonly List<RecordBatch> _batches = new();

    internal FluidAggregateState(FluidAggregateBinding binding) => _binding = binding;

    /// <summary>
    /// Retains the group's rows in ARROW form — one single-column batch per update, COPIED.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ <b>COPIED, and not optionally.</b> The caller owns the batch it hands us and disposes it when
    /// <c>Update</c> returns, and its columns may be SLICES of a larger chunk. Keeping either past the call
    /// is the use-after-free class this codebase records as invisible on Windows and Linux, so it goes
    /// through the plugin's established IPC copier.
    /// </para>
    /// <para>
    /// ⚠ Only the VALUE column is copied. <c>template</c> and <c>params</c> occupy their declared slots in
    /// every update batch, so copying the whole thing would duplicate a constant string per row.
    /// </para>
    /// <para>
    /// ⚠⚠ <b>Arrow rather than CLR values, which is what makes <c>rows</c> a SQL relation possible at all.</b>
    /// It used to read each cell into a boxed object here; a staged relation needs the Arrow data, and
    /// rebuilding it from boxed values would be an Arrow type ladder — the thing this function's own history
    /// records retiring twice. The Liquid <c>rows</c> value is built from these batches at RENDER instead, so
    /// there is ONE representation and the boxing is paid only by a template that actually reads rows in
    /// Liquid — the value is LAZY, so a SQL-only template never pays it.
    /// </para>
    /// </remarks>
    public void Update(RecordBatch args)
    {
        if (args.Length == 0)
        {
            return;
        }
        var field = args.Schema.FieldsList[FluidAggregateBinding.ValueColumn];
        // ⚠ NOT disposed: this wrapper's column belongs to the caller's batch. Copy() reads it and returns
        // a batch that owns its own memory.
        var one = new RecordBatch(new Schema(new[] { field }, null),
                                  new[] { args.Column(FluidAggregateBinding.ValueColumn) }, args.Length);
        _batches.Add(FluidFilterModel.Copy(one));
    }

    /// <summary>⚠ APPEND, preserving each partial's own order — see the ordering note on the function.</summary>
    public void Combine(IAggregateState source) => _batches.AddRange(((FluidAggregateState)source)._batches);

    /// <summary>
    /// This group's rendered SELECT; the binding RUNS it (and every other group's) to build the column.
    /// </summary>
    /// <remarks>
    /// ⚠ A group that was never updated still renders, with an EMPTY <c>rows</c> — DuckDB finalizes states it
    /// never updated, and a template may legitimately have something to say about an empty group. It must
    /// still render a SELECT; <c>{% if rows.size == 0 %}select NULL{% else %}…{% endif %}</c> is how an empty
    /// group gets a different answer.
    /// <para>
    /// ⚠ Rendering here and EXECUTING in the binding is not an arbitrary split: the statement must run on the
    /// call site's own session, which only the binding holds, and running it here would put one query inside
    /// each state's finalize with no chance to check the column's shape across groups.
    /// </para>
    /// </remarks>
    public object? Finalize() => new FluidAggregateBinding.GroupPlan(_binding.Render(_batches), _batches);
}
