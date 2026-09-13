// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Fabricator.Bridge;

namespace Fabricator.FluidPlugin;

/// <summary>
/// <c>fluid_scalar(template, params, arg0, arg1, …)</c> — a template rendered to a SQL SELECT, evaluated by
/// DuckDB over the call's per-row arguments, whose RESULT TYPE the template declares itself.
/// </summary>
/// <remarks>
/// <para>
/// <c>template</c> and <c>params</c> are BIND-TIME CONSTANTS; everything after them is a per-row argument,
/// staged as <c>input_table</c> and addressable in the expression as <c>arg_0</c>, <c>arg_1</c>, … The
/// template renders TWICE in a different sense from the other surfaces: once at bind with
/// <c>is_bind = true</c>, where it must render a SELECT of the RETURN TYPE, and once per CHUNK for real.
/// <code>
/// SELECT fluid_scalar(
///   '{% if is_bind %}SELECT NULL::STRUCT(a INTEGER)'
///   '{% else %}SELECT {''a'': arg_0 + 1} FROM input_table{% endif %}', NULL, n) FROM t;
/// </code>
/// ⚠ The template writes its own <c>SELECT</c> — user decision, and nothing here prepends one. The bind
/// render is therefore a statement you can paste into a shell and run.
/// </para>
/// <para>
/// ⚠⚠ <b>THE TYPE COMES FROM DuckDB BINDING THE TEMPLATE'S OWN SQL — there is no type mapping here, and that
/// is the whole point of the design.</b> The bind render is wrapped and described (the
/// <see cref="FluidRelationInput.DescribeGenerated"/> shape), so whatever DuckDB says that expression's type
/// is becomes this call site's result type: <c>STRUCT(a INTEGER, b VARCHAR)</c>, <c>DECIMAL(9,2)</c> with its
/// scale intact, a LIST, a MAP — anything DuckDB can express. The alternative considered and rejected was
/// rendering a TYPE NAME and parsing it, which is a second SQL type ladder; this codebase has refused to
/// maintain one three times.
/// </para>
/// <para>
/// ⚠⚠ <b>CARDINALITY AND ORDER ARE THE TEMPLATE'S CONTRACT, and that is the cost of the statement form.</b>
/// A scalar owes exactly ONE value per input row IN INPUT ORDER. A rendered SELECT can change both, so a
/// join or an aggregate inside it MISALIGNS every row — a wrong answer with nothing failing. What is
/// enforced here: exactly ONE output column, and a row count equal to the chunk's (a short or long read is
/// refused by name). What is NOT enforced is ORDER. A plain projection over <c>input_table</c> preserves it
/// — MEASURED, including for a correlated-subquery expression over 3000 rows and under
/// <c>SET GLOBAL preserve_insertion_order = false</c> — so the ordinary shape is safe. A statement that can
/// reorder must carry <c>__fab_row</c> through and <c>ORDER BY</c> it; the staged relation provides it.
/// </para>
/// <para>
/// ⚠ <b>ONE RENDER PER CHUNK, not per row</b> (user decision). Liquid decides the SHAPE of the computation —
/// from <c>params</c>, which is constant — and DuckDB computes every row natively. That is what makes this
/// cheaper than <c>fluid_render</c> for per-row work, and it is also why the template cannot branch on a row
/// VALUE in Liquid: at render time there is no row. Branch in the SQL it emits instead.
/// </para>
/// <para>
/// ⚠⚠ <b>A FRESH CONNECTION PER CHUNK, and a pinned one is NOT available.</b> The binding is shared across
/// pipeline threads — this plugin already records that a volatile scalar may be evaluated on several threads
/// at once, which is why <see cref="FluidRenderSession"/> is per-render — and a DuckDB connection is
/// single-threaded by contract. So the session is built inside <c>Invoke</c> and disposed with it. The cost
/// is one connection per 2048 rows, which is far cheaper than the per-CALL connections every
/// <c>query()</c> paid before ABI v84.
/// </para>
/// </remarks>
internal sealed class FluidScalarFunction : IScalarFunction
{
    internal const string FunctionName = "fluid_scalar";

    public string Name => FunctionName;

    public Schema Parameters => new(new[]
    {
        Params.Positional("template", StringType.Default),
        // The SQLNULL sentinel => registered as LogicalType::ANY, so the bag may be a STRUCT (preferred), a
        // MAP or a JSON string — the same contract every other Fluid surface's params has.
        Params.Positional("params", NullType.Default),
        // ⚠ The tail arrives as `arg_0`, `arg_1`, … (FabricatorArgName: `<tail>_<k>`), which is what the
        // template addresses. Deterministic, unlike fluid_query_lateral's wire names, which fall back to the
        // rendered EXPRESSION TEXT of the call-site argument.
        Params.VarArgs("arg"),
    }, metadata: null);

    // No fixed return type: it is whatever the template's is_bind expression binds to, so the host registers
    // this as ANY and requires Bind to resolve a type per call site.
    public Field? Result => null;

    // Never called: Bind always returns a FluidScalarBinding carrying the resolved type. Failing loudly beats
    // silently computing something untyped.
    public IArrowArray Invoke(RecordBatch args) =>
        throw new InvalidOperationException(
            FunctionName + " must be executed through its binding (its result type is resolved at bind)");

    public IScalarFunctionBinding Bind(ScalarBindArgs args)
    {
        // ⚠ IsConstant, not merely "is there a value": a non-constant slot carries a NULL PLACEHOLDER that no
        // inspection of the value can distinguish from an explicit NULL. Refusing by name is the honest
        // answer — the result type is part of the PLAN, so it cannot depend on row data.
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
                + "bind). Per-row values belong in the trailing arguments, which the expression reads as "
                + "arg_0, arg_1, ….");
        }
        var template = (args.ConstantArray(0) as StringArray)?.GetString(0);
        if (string.IsNullOrWhiteSpace(template))
        {
            throw new InvalidOperationException(FunctionName + ": template must be a non-NULL VARCHAR");
        }

        var bag = args.Count > 1 ? args.ConstantArray(1) : null;
        var parameters = FluidValueModel.CaptureBag(bag, 0);
        // ⚠ COPIED: the SQL variable is staged when a chunk's session opens its connection, long after these
        // bind arguments are freed. Same rule as the three deferred surfaces.
        var paramsRows = FluidValueModel.CopyBagRow(bag, 0);

        // The staged shape: the row number the wrap orders by, then the per-row arguments under the names the
        // template addresses. Built from the DECLARED arity so the probe and every chunk agree.
        var stagedSchema = StagedSchema(args.Count);

        using var probe = FluidRenderSession.TryCreate()
            ?? throw new InvalidOperationException(
                FunctionName + " needs the hosting DuckDB to determine its result type, and it is not "
                + "available here.");
        var (result, typeName) = DescribeResult(probe, template!, parameters, paramsRows, stagedSchema);
        return new FluidScalarBinding(template!, parameters, paramsRows, stagedSchema, result, typeName);
    }

    /// <summary>The staged input's columns: the row number, then <c>arg_0 … arg_{n-1}</c>.</summary>
    /// <remarks>
    /// ⚠ Every argument column is declared as the SQLNULL sentinel's Arrow type at bind because the bind sees
    /// only PRE-CAST values and a non-constant slot carries a placeholder; the real types arrive with each
    /// chunk and the staged relation is rebuilt from THAT batch. The bind-time shape exists so the probe can
    /// bind the expression's column NAMES, which is all it needs.
    /// </remarks>
    private static Schema StagedSchema(int declaredCount)
    {
        var fields = new List<Field> { new(FluidScalarBinding.RowColumn, Int64Type.Default, nullable: false) };
        for (int i = 2; i < declaredCount; i++)
        {
            fields.Add(new Field("arg_" + (i - 2), StringType.Default, nullable: true));
        }
        return new Schema(fields, metadata: null);
    }

    /// <summary>
    /// Renders with <c>is_bind</c> and asks DuckDB what that expression's type is — as an Arrow type for the
    /// result field, and as DuckDB's own type NAME for the cast every chunk is coerced through.
    /// </summary>
    /// <remarks>
    /// ⚠ The NAME comes from <c>DESCRIBE</c>, i.e. from DuckDB rendering its own type, so it re-parses in a
    /// CAST by construction — MEASURED for <c>STRUCT(a INTEGER, b VARCHAR)</c>, <c>DECIMAL(9,2)</c> with its
    /// scale, <c>INTEGER[]</c> and <c>MAP(VARCHAR, INTEGER)</c>. It is never the template's own text.
    /// </remarks>
    private static (Field Result, string TypeName) DescribeResult(
        FluidRenderSession probe, string template, object? parameters,
        RecordBatch? paramsRows, Schema stagedSchema)
    {
        var ctx = FluidRelationInput.NewContext(FunctionName, probe, parameters, isBind: true,
                                                paramsRows: paramsRows);
        FluidRelationInput.CreateEmptyInput(probe, stagedSchema);
        FluidHostQuery.BindLazyRelation(ctx, FluidRelationInput.InputTable);
        var expression = FluidEngine.RenderOn(FunctionName, template, ctx);
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new InvalidOperationException(
                FunctionName + ": the template rendered nothing under " + FluidEngine.IsBindVariable
                + " = true. It must render a SELECT of the result type there — `SELECT NULL::<type>` is the "
                + "usual answer, e.g. SELECT NULL::STRUCT(a INTEGER).");
        }
        // ⚠ LIMIT 0 binds without scanning, which is also what requires the render to be a usable EXPRESSION:
        // a statement, a DDL or anything else fails here rather than being accepted and described as whatever
        // shape the engine reports for it.
        //
        // ⚠⚠ SCOPED, and it has to be: this session's connection is PINNED, and a pinned connection allows
        // ONE live result at a time — the type-name query below is a SECOND statement on it. Leaving this
        // stream open is refused by name (ABI v84), which is the guard that exists because DuckDB would
        // otherwise close the first result silently and truncate it.
        Schema schema;
        using (var stream = probe.Query(FluidScalarBinding.WrapBind(expression)))
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
                DescribeTypeName(probe, expression));
    }

    /// <summary>DuckDB's own name for the bind expression's type.</summary>
    private static string DescribeTypeName(FluidRenderSession probe, string expression)
    {
        using var stream = probe.Query(
            "SELECT column_type FROM (DESCRIBE " + FluidScalarBinding.WrapBind(expression) + ")");
        var batch = stream.ReadNextRecordBatchAsync().GetAwaiter().GetResult();
        using (batch)
        {
            var name = batch is { Length: > 0 } ? (batch.Column(0) as StringArray)?.GetString(0) : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException(
                    FunctionName + ": could not determine the result type of the bind render.");
            }
            return name!;
        }
    }
}

/// <summary>One bound <c>fluid_scalar</c> call site: the resolved result type plus the constants every chunk
/// re-renders with.</summary>
internal sealed class FluidScalarBinding : IScalarFunctionBinding
{
    /// <summary>The staged row number the wrap orders by — the only guarantee that row i in equals row i out.</summary>
    internal const string RowColumn = "__fab_row";

    private const string ValueColumn = "__fab_value";

    private readonly string _template;
    private readonly object? _parameters;
    private readonly RecordBatch? _paramsRows;
    private readonly Schema _stagedSchema;
    private readonly string _typeName;

    internal FluidScalarBinding(string template, object? parameters, RecordBatch? paramsRows,
                                Schema stagedSchema, Field result, string typeName)
    {
        _template = template;
        _parameters = parameters;
        _paramsRows = paramsRows;
        _stagedSchema = stagedSchema;
        _typeName = typeName;
        Result = result;
    }

    /// <summary>A CONCRETE field: this call's type came from binding the template, not from a declaration.</summary>
    public Field? Result { get; }

    /// <summary>Wraps the bind render: binds the template's own SELECT without scanning a row.</summary>
    /// <remarks>
    /// ⚠ The template writes its OWN <c>select</c> (user decision) — this wrap adds none. <c>LIMIT 0</c>
    /// binds without scanning, the same shape <see cref="FluidRelationInput.DescribeGenerated"/> uses, and it
    /// is also what REQUIRES the render to be a subquery-usable SELECT: a DDL or a DML fails here rather than
    /// being accepted and described as whatever shape the engine reports for it.
    /// </remarks>
    internal static string WrapBind(string statement) => $"SELECT * FROM ({statement}) LIMIT 0";

    /// <summary>Wraps the per-chunk render: casts the template's single column to the declared type.</summary>
    /// <remarks>
    /// ⚠⚠ <b>THE CAST IS WHAT MAKES THE <c>is_bind</c> RENDER A DECLARATION RATHER THAN A THING TO MATCH.</b>
    /// Without it a template declaring <c>NULL::BIGINT</c> and rendering the literal <c>42</c> is REFUSED,
    /// because <c>42</c> is INTEGER — so the author would have to write every literal's type twice and keep
    /// the two in step. Coercing instead makes the declared type authoritative, using DuckDB's own cast, and
    /// an expression that genuinely cannot become that type fails in DuckDB's cast with DuckDB's message.
    /// ⚠ The cost is that a LOSSY but legal conversion is silent — a DOUBLE expression under a declared
    /// BIGINT truncates — which is the same thing a declared column type does everywhere in SQL.
    /// <para>
    /// ⚠⚠ <b>THE WRAP NO LONGER ORDERS, because the template now writes its own SELECT and the wrap adds
    /// nothing to it.</b> Order therefore rests on DuckDB preserving a projection's row order, which it does
    /// — MEASURED, including for a correlated-subquery expression over 3000 rows and under
    /// <c>SET GLOBAL preserve_insertion_order = false</c>, both ZERO misaligned. A template whose statement
    /// can genuinely reorder must carry <c>__fab_row</c> through and <c>ORDER BY</c> it. The gate's
    /// alignment row covers the ordinary shape; it does not prove the unusual one.
    /// </para>
    /// <para>
    /// ⚠ The single column is aliased POSITIONALLY (<c>t(x)</c>) rather than by name, so the wrap needs no
    /// knowledge of what the template called it.
    /// </para>
    /// </remarks>
    internal string WrapExecute(string statement) =>
        $"SELECT CAST(x AS {_typeName}) AS {DuckSql.QuoteIdent(ValueColumn)} FROM ({statement}) t(x)";

    public IArrowArray Invoke(RecordBatch args)
    {
        // ⚠ A NEW session per chunk, and it cannot be hoisted onto the binding: a volatile scalar may be
        // evaluated on several pipeline threads at once while ONE binding serves them all, and a DuckDB
        // connection is single-threaded by contract.
        using var session = FluidRenderSession.TryCreate()
            ?? throw new InvalidOperationException(
                FluidScalarFunction.FunctionName + " needs the hosting DuckDB, which is not available here.");

        // ⚠ The context BEFORE the staging, so the params variable is declared before this session runs any
        // statement. FluidRenderSession.BindVariable copes with either order now, but declaring first is the
        // order that reads correctly.
        var ctx = FluidRelationInput.NewContext(FluidScalarFunction.FunctionName, session, _parameters,
                                                isBind: false, paramsRows: _paramsRows);
        var staged = StageArgs(session, args);
        try
        {
            FluidHostQuery.BindLazyRelation(ctx, FluidRelationInput.InputTable);
            var expression = FluidEngine.RenderOn(FluidScalarFunction.FunctionName, _template, ctx);
            if (string.IsNullOrWhiteSpace(expression))
            {
                throw new InvalidOperationException(
                    FluidScalarFunction.FunctionName + ": the template rendered nothing. It must render a "
                    + "SELECT for every chunk, not only under " + FluidEngine.IsBindVariable + ".");
            }
            using var stream = session.Query(WrapExecute(expression));
            return ReadOne(stream, args.Length);
        }
        finally
        {
            session.ReleaseRows(staged);
        }
    }

    /// <summary>Stages this chunk's arguments as <c>input_table</c>, row number first.</summary>
    /// <remarks>
    /// ⚠ Built from THIS batch's columns, so the argument types are the post-cast ones DuckDB actually
    /// delivers rather than the placeholder shapes the bind saw. The first two columns (template, params) are
    /// dropped: they are constants the template already has, and carrying them would make <c>arg_0</c> mean
    /// the template.
    /// </remarks>
    private string StageArgs(FluidRenderSession session, RecordBatch args)
    {
        var fields = new List<Field> { new(RowColumn, Int64Type.Default, nullable: false) };
        var columns = new List<IArrowArray>();
        var rows = new Int64Array.Builder().Reserve(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            rows.Append(i);
        }
        columns.Add(rows.Build());
        for (int c = 2; c < args.ColumnCount; c++)
        {
            fields.Add(new Field("arg_" + (c - 2), args.Schema.FieldsList[c].DataType, nullable: true));
            columns.Add(args.Column(c));
        }
        // ⚠ BORROWED, like every other RegisterRows caller: the argument columns belong to the framework and
        // are neither copied nor disposed here, which is why the token is released in the caller's finally.
        var batch = new RecordBatch(new Schema(fields, metadata: null), columns, args.Length);
        var token = session.RegisterRows(batch);
        session.ExecuteNonQuery(
            $"CREATE OR REPLACE TEMP TABLE {DuckSql.QuoteIdent(FluidRelationInput.InputTable)} AS "
            + $"SELECT * FROM fabricator_scan({DuckSql.Literal(token)})");
        return token;
    }

    /// <summary>Drains the one result column, checking it is the shape this call site promised.</summary>
    /// <remarks>
    /// ⚠⚠ The ROW COUNT is checked, not assumed. The wrap makes a short read impossible for a well-formed
    /// expression, but a template rendering something that binds as a projection yet does not behave as one
    /// would otherwise hand the host a column of the wrong length — which DuckDB reads as DATA for the
    /// following rows. A drifting TYPE is caught for the same reason: the host builds its converters from the
    /// type this binding declared at bind.
    /// </remarks>
    private IArrowArray ReadOne(IArrowArrayStream stream, int expected)
    {
        var parts = new List<RecordBatch>();
        int total = 0;
        while (true)
        {
            var batch = stream.ReadNextRecordBatchAsync().GetAwaiter().GetResult();
            if (batch is null)
            {
                break;
            }
            if (batch.Length == 0)
            {
                batch.Dispose();
                continue;
            }
            if (batch.ColumnCount != 1)
            {
                throw new InvalidOperationException(
                    FluidScalarFunction.FunctionName + ": expected one result column, got "
                    + batch.ColumnCount + ".");
            }
            var arrived = batch.Schema.FieldsList[0].DataType;
            if (arrived.TypeId != Result!.DataType.TypeId)
            {
                throw new InvalidOperationException(
                    FluidScalarFunction.FunctionName + ": the template rendered an expression of type "
                    + arrived.Name + " for this chunk, but this call site was bound as "
                    + Result.DataType.Name + ". The " + FluidEngine.IsBindVariable + " render must declare "
                    + "the type the other branch produces.");
            }
            parts.Add(batch);
            total += batch.Length;
        }
        if (total != expected)
        {
            foreach (var p in parts)
            {
                p.Dispose();
            }
            throw new InvalidOperationException(
                FluidScalarFunction.FunctionName + ": the rendered SELECT produced " + total
                + " values for " + expected + " input rows. A scalar owes exactly ONE value per row, so the "
                + "statement must be a projection over " + FluidRelationInput.InputTable
                + " — anything that changes cardinality (a join that fans out, an aggregate, a filter) "
                + "cannot be used here.");
        }
        // ⚠⚠ OWNERSHIP, and it is the seam that faulted once in fluid_query_lateral: with ONE part the
        // returned column IS that batch's, so the batch must be handed on UNDISPOSED; with several the
        // concatenator allocated a fresh column, so every part is released. Disposing in the single-part
        // case frees the buffers the host is about to export — an access violation inside Apache.Arrow's
        // own release callback, two frames deep and naming nothing of ours.
        if (parts.Count == 1)
        {
            return parts[0].Column(0);
        }
        var slices = new IArrowArray[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            slices[i] = parts[i].Column(0);
        }
        var joined = ArrowArrayConcatenator.Concatenate(slices);
        foreach (var p in parts)
        {
            p.Dispose();
        }
        return joined;
    }

    public void Dispose() { }
}
