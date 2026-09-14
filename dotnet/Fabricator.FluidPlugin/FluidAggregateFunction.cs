// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Types;
using Fabricator.Bridge;
using Fluid;

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
///   '{{ s | md5 }}{% endif %}', NULL, struct_pack(a := a, b := b)) FROM t GROUP BY g;
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
/// ⚠ <b>The rendered text is CAST to the declared type by DuckDB</b>, in ONE statement per finalize call
/// (a whole vector of groups), not one per group — and skipped entirely when the declared type is VARCHAR,
/// which is the common case. Rendering the value and parsing it HERE would be a second type ladder; this
/// codebase has refused to maintain one four times.
/// </para>
/// <para>
/// ⚠ <b>An EMPTY render is NULL</b>, which is also what a group that was never updated finalizes to. A
/// template that means the empty STRING must say so (<c>{{ s | default: '' }}</c> renders empty too — use a
/// sentinel and strip it, or declare VARCHAR and accept the equivalence).
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
            var (result, typeName) = DescribeResult(session, template!, parameters, paramsRows);
            return new FluidAggregateBinding(template!, parameters, paramsRows, result, typeName, session);
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
                                                                  object? parameters, RecordBatch? paramsRows)
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
    private readonly bool _needsCast;
    private FluidRenderSession? _session;
    private TemplateContext? _ctx;

    internal FluidAggregateBinding(string template, object? parameters, RecordBatch? paramsRows,
                                   Field result, string typeName, FluidRenderSession session)
    {
        _template = template;
        _parameters = parameters;
        _paramsRows = paramsRows;
        _typeName = typeName;
        _session = session;
        Result = result;
        // ⚠ VARCHAR needs no cast, and that is the common case — so an ordinary hash/summary template runs
        // with NO SQL at all. Everything else is cast by DuckDB, once per finalize call.
        _needsCast = result.DataType.TypeId != ArrowTypeId.String;
    }

    public Field Result { get; }

    public IAggregateState CreateState() => new FluidAggregateState(this);

    /// <summary>Renders one group's value; null when the render is empty (an absent value).</summary>
    internal string? Render(IReadOnlyList<object?> rows)
    {
        lock (_gate)
        {
            var ctx = Context();
            // ⚠ REBOUND PER GROUP, because a Fluid value is bound INTO the context: one bound at creation
            // would serve the FIRST group's rows to every later group — right shape, wrong rows, no error.
            // The same rule the collector's input_table records.
            FluidValueModel.SetVariable(ctx, FluidAggregateFunction.RowsVariable, rows);
            var text = FluidEngine.RenderOn(FluidAggregateFunction.FunctionName, _template, ctx);
            return string.IsNullOrEmpty(text) ? null : text;
        }
    }

    /// <summary>
    /// Converts one finalize call's rendered texts to the declared type — ONE statement for the whole vector
    /// of groups, or none at all when the declared type is VARCHAR.
    /// </summary>
    /// <remarks>
    /// ⚠⚠ THE CAST IS DuckDB'S, deliberately: parsing the text here would be a second SQL type ladder, and
    /// the one this plugin has (<c>DuckSql.Literal</c>) collapses every temporal to TIMESTAMPTZ and refuses a
    /// LIST or a STRUCT by name. Staging the texts and casting them means a template may declare
    /// <c>DECIMAL(9,2)</c> with its scale, a <c>STRUCT(a INTEGER)</c> or a LIST and get exactly that back.
    /// </remarks>
    public IArrowArray? FinalizeColumn(object?[] values)
    {
        if (!_needsCast || values.Length == 0)
        {
            return null; // VARCHAR: the rendered text IS the value, so the host's default builds it
        }
        lock (_gate)
        {
            var builder = new StringArray.Builder();
            foreach (var v in values)
            {
                if (v is string t) { builder.Append(t); } else { builder.AppendNull(); }
            }
            var batch = new RecordBatch(
                new Schema(new[] { new Field("v", StringType.Default, nullable: true) }, null),
                new IArrowArray[] { builder.Build() }, values.Length);
            var session = Session();
            var token = session.RegisterRows(batch);
            try
            {
                using var stream = session.Query(
                    $"SELECT CAST(v AS {_typeName}) AS v FROM fabricator_scan({DuckSql.Literal(token)})");
                var parts = new List<IArrowArray>();
                int got = 0;
                while (true)
                {
                    var next = stream.ReadNextRecordBatchAsync().GetAwaiter().GetResult();
                    if (next is null) { break; }
                    parts.Add(next.Column(0));
                    got += next.Length;
                }
                if (got != values.Length)
                {
                    // ⚠ A short read would MIS-ALIGN every later group's value — a wrong ANSWER, not an
                    // error. The cast is a projection over a staged relation, so it cannot legitimately
                    // change cardinality; checking costs nothing and the failure is otherwise invisible.
                    throw new InvalidOperationException(
                        $"{FluidAggregateFunction.FunctionName}: the result cast returned {got} values for "
                        + $"{values.Length} groups.");
                }
                // ⚠⚠ THE COLUMN DuckDB BUILT, handed over as-is. Reading it back into boxed objects and
                // rebuilding would reintroduce exactly the per-type ladder this design exists to avoid, and
                // would refuse a STRUCT, a LIST or a MAP — types a template may legitimately declare.
                return parts.Count == 1 ? parts[0] : ArrowArrayConcatenator.Concatenate(parts);
            }
            finally
            {
                session.ReleaseRows(token);
            }
        }
    }

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
    private readonly List<object?> _rows = new();

    internal FluidAggregateState(FluidAggregateBinding binding) => _binding = binding;

    public void Update(RecordBatch args)
    {
        var col = args.Column(FluidAggregateBinding.ValueColumn);
        for (int i = 0; i < args.Length; i++)
        {
            // ⚠ ReadCell, this plugin's value model — so a DATE in `rows` renders like a DATE in `params`
            // (it stamps DateTimeKind.Utc, without which a date renders as the PREVIOUS DAY east of UTC).
            _rows.Add(FluidValueModel.ReadCell(col, i));
        }
    }

    /// <summary>⚠ APPEND, preserving each partial's own order — see the ordering note on the function.</summary>
    public void Combine(IAggregateState source) => _rows.AddRange(((FluidAggregateState)source)._rows);

    /// <summary>
    /// This group's value as TEXT; the binding casts a whole vector of them at once (FinalizeBatch).
    /// </summary>
    /// <remarks>
    /// ⚠ A group that was never updated renders with an EMPTY <c>rows</c> rather than short-circuiting to
    /// NULL: DuckDB finalizes states it never updated, and a template may legitimately have something to say
    /// about an empty group (a hash of nothing, a literal). Rendering nothing is still NULL.
    /// </remarks>
    public object? Finalize() => _binding.Render(_rows);
}
