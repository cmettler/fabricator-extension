// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Types;
using Apache.Arrow.Ipc;
using Fabricator.Bridge;
using Fluid;
using Fluid.Values;

namespace Fabricator.FluidPlugin;

/// <summary>
/// The Fluid <c>query(sql)</c> function: runs a SELECT on the hosting DuckDB through
/// <see cref="IHostQuery"/> and hands the template its rows.
/// </summary>
/// <remarks>
/// <para>
/// Rows come back as the SAME <see cref="ArrowStruct"/> lookup a STRUCT cell uses, so a row is addressed by
/// NAME (<c>r.total</c>, <c>r['total']</c>) and by ORDINAL (<c>r[0]</c>) through one rule, and nested values
/// unbox exactly as they do everywhere else in this plugin.
/// </para>
/// <para>
/// ⚠⚠ <b>It refuses anything that is not a SELECT, and that is a correctness requirement rather than a
/// policy.</b> A template may be rendered at BIND (<c>fluid_query</c> is a sqlgen function), and a bind
/// REPEATS and happens WITHOUT execution — MEASURED, a bind-time write fires on <c>EXPLAIN</c> of a
/// statement that never runs, and again on merely defining a view over it. So a writing <c>query</c> would
/// mutate where nobody asked, invisibly. See docs/fluid-templating.md §8.3 and §9.
/// </para>
/// </remarks>
internal static class FluidHostQuery
{
    /// <summary>The name a template calls it by, as BOTH a function and a filter.</summary>
    internal const string FunctionName = "query";

    /// <summary>
    /// The <see cref="TemplateContext.AmbientValues"/> key carrying the SQL function name being rendered.
    /// </summary>
    /// <remarks>
    /// ⚠ It exists because the FILTER is registered ONCE in the shared <see cref="TemplateOptions"/>, so it
    /// cannot capture `caller` the way the per-context function does — and registering it per render would
    /// mutate that shared object on every call and report another render's name in an error.
    /// </remarks>
    internal const string CallerKey = "fabricator.caller";

    /// <summary>The caller for this render, or a neutral name if it was not set.</summary>
    private static string CallerOf(TemplateContext ctx) =>
        ctx.AmbientValues.TryGetValue(CallerKey, out var v) && v is string s ? s : FunctionName;

    /// <summary>The most rows one <c>query()</c> call will materialise before refusing.</summary>
    /// <remarks>
    /// ⚠ It ERRORS rather than truncating. A silent truncation is a wrong ANSWER — the template would render
    /// a partial result as though it were the whole one — whereas the cap exists only to turn an
    /// out-of-memory into a sentence naming the cause. Rows are fully materialised because a template may
    /// iterate them any number of times, so there is no streaming form to fall back to.
    /// </remarks>
    internal const int MaxRows = 1_000_000;

    /// <summary>The FUNCTION form — <c>query(sql)</c>, no parameters.</summary>
    /// <param name="caller">The SQL function the template was rendered by, so an error names it.</param>
    internal static FluidValue Execute(string caller, FunctionArguments args, TemplateContext ctx)
    {
        if (args.Count < 1)
        {
            throw new ArgumentException($"{caller}: {FunctionName}() takes one argument, the SQL to run.");
        }
        // ⚠ NAMED ARGUMENTS CANNOT REACH HERE, and the reason is Fluid's GRAMMAR rather than our choice:
        // MEASURED, `query('s', a: 5)` is a PARSE error ("End of tag was expected"), while the same names on
        // a FILTER parse and arrive populated. That is why parameter binding is the filter form below and
        // not an overload of this one. If a future Fluid teaches the function grammar named arguments, this
        // is where they would be read.
        return Run(caller, args.At(0).ToStringValue(ctx), null);
    }

    /// <summary>
    /// The FILTER form — <c>sql | query: a: 1, b: 2</c> — whose NAMED arguments become the statement's
    /// <c>$a</c> / <c>$b</c> parameters, bound as VALUES rather than spliced into the SQL.
    /// </summary>
    /// <remarks>
    /// ⚠ It is a filter because Fluid's named-argument grammar only exists there (see <see cref="Execute"/>).
    /// The SQL is the filter's INPUT, which also reads correctly: the statement is the subject and the
    /// parameters modify it.
    /// </remarks>
    internal static ValueTask<FluidValue> Filter(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        var caller = CallerOf(ctx);
        return new(Run(caller, input.ToStringValue(ctx), BuildParameters(caller, FunctionName, args, ctx)));
    }

    private static FluidValue Run(string caller, string? sql, RecordBatch? parameters)
    {
        // ⚠ MEASURED: json_serialize_sql('') reports NO error — an empty string parses to zero statements,
        // so the classifier below would wave it through. Refused here, where the cause can still be named.
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException($"{caller}: {FunctionName}() was given an empty statement.");
        }

        var host = FabricatorServices.Get<IHostQuery>()
            ?? throw new InvalidOperationException(
                $"{caller}: {FunctionName}() needs the IHostQuery service, which is not published here. "
                + "It is available only from inside a fabricator function call.");

        RefuseUnlessSelect(caller, sql, host);

        using var stream = host.Query(sql, parameters);
        var rows = new List<FluidValue>();
        while (true)
        {
            var batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
            if (batch is null)
            {
                break;
            }
            using (batch)
            {
                if (rows.Count + batch.Length > MaxRows)
                {
                    throw new InvalidOperationException(
                        $"{caller}: {FunctionName}() returned more than {MaxRows} rows. Narrow the query — "
                        + "the rows are all held in memory, because a template may iterate them repeatedly.");
                }
                // ⚠ Hoisted out of the row loop: batch.Arrays is an enumerable, so materialising it per
                // row would allocate one list per row on a path whose whole point is many rows.
                var columns = batch.Arrays.ToList();
                var fields = batch.Schema.FieldsList;
                for (int r = 0; r < batch.Length; r++)
                {
                    // ⚠ The cells are read EAGERLY rather than holding the batch, because the batch is
                    // disposed at the end of this block while the rows live for the whole render.
                    // ⚠ MEASURED, and NOT the silent native use-after-free class this project usually warns
                    // about: Apache.Arrow nulls a disposed RecordBatch's arrays, so holding it fails LOUDLY
                    // with a NullReferenceException on the first cell read, deterministically and on every
                    // platform. Mutation-tested — it dies at the very first query() assertion.
                    rows.Add(new DictionaryValue(new EagerRow(fields, columns, r)));
                }
            }
        }
        return new ArrayValue(rows);
    }


    /// <summary>
    /// Turns the filter's NAMED arguments into the 1-row Arrow batch the host binds as <c>$name</c>
    /// parameters. Returns null when there are none, which keeps the no-parameter path byte-identical.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ <b>Every column is NAMED, and that is what selects binding by name.</b> The host binds by name
    /// when all columns carry a non-empty Arrow field name and positionally otherwise — the names were
    /// always crossing in the Arrow schema and simply were not read until this. So an unnamed batch keeps
    /// the original positional behaviour.
    /// </para>
    /// <para>
    /// ⚠ POSITIONAL arguments are REFUSED rather than ignored. Fluid lets a filter take both
    /// (<c>sql | query: 5, b: 'x'</c> parses), and a positional value here could only mean a <c>?</c> or
    /// <c>$1</c> placeholder — which cannot coexist with the named binding this batch selects. Silently
    /// dropping it would run the statement with a parameter the author believed they had supplied.
    /// </para>
    /// <para>
    /// ⚠ The value types are an ALLOW-LIST, the same discipline as the <c>| sql</c> filter, but they cross
    /// as VALUES rather than as rendered text — so this is strictly safer and there is no escaping to get
    /// wrong. A LIST/STRUCT/MAP argument is refused BY NAME: DuckDB has no parameter binding for them here,
    /// and rendering one into the SQL is exactly what parameters exist to avoid.
    /// </para>
    /// </remarks>
    internal static RecordBatch? BuildParameters(string caller, string fn, FilterArguments args, TemplateContext ctx)
    {
        var names = args.Names.ToList();
        if (names.Count == 0)
        {
            if (args.Count > 0)
            {
                throw new ArgumentException(
                    $"{caller}: {fn} takes NAMED parameters only — write "
                    + $"`sql | {fn}: name: value` so the statement can reference $name.");
            }
            return null;
        }
        if (args.Count != names.Count)
        {
            throw new ArgumentException(
                $"{caller}: {fn} was given {args.Count - names.Count} positional argument(s) "
                + "beside its named ones. Parameters must all be named, because the statement references "
                + "them as $name.");
        }

        var fields = new List<Field>(names.Count);
        var columns = new List<IArrowArray>(names.Count);
        foreach (var name in names)
        {
            var value = args[name];
            var (field, array) = ToParameter(caller, name, value, ctx);
            fields.Add(field);
            columns.Add(array);
        }
        return new RecordBatch(new Schema(fields, null), columns, 1);
    }

    /// <summary>One named parameter as a 1-row Arrow column.</summary>
    private static (Field, IArrowArray) ToParameter(string caller, string name, FluidValue value,
                                                    TemplateContext ctx)
    {
        switch (value.Type)
        {
            case FluidValues.Nil:
            case FluidValues.Empty:
                // ⚠ A NULL still needs a TYPE to cross, and VARCHAR is the one DuckDB casts most freely
                // from. `$x IS NULL` and `$x` on its own behave; a comparison against a numeric column may
                // still refuse the cast, which is DuckDB's answer rather than something to paper over.
                return (new Field(name, StringType.Default, nullable: true),
                        new StringArray.Builder().AppendNull().Build());
            case FluidValues.Boolean:
                return (new Field(name, BooleanType.Default, nullable: true),
                        new BooleanArray.Builder().Append(value.ToBooleanValue()).Build());
            case FluidValues.String:
                return (new Field(name, StringType.Default, nullable: true),
                        new StringArray.Builder().Append(value.ToStringValue(ctx)).Build());
            case FluidValues.Number:
                return NumberParameter(name, value.ToNumberValue());
            case FluidValues.DateTime:
                // ⚠ Stamped UTC for the same reason §7.4a records on the way in: an Unspecified DateTime is
                // resolved against the MACHINE's local zone, which silently shifts the value.
                var dt = value.ToObjectValue() is DateTimeOffset dto
                    ? dto.UtcDateTime
                    : DateTime.SpecifyKind((DateTime)(value.ToObjectValue() ?? default(DateTime)),
                                           DateTimeKind.Utc);
                return (new Field(name, new TimestampType(TimeUnit.Microsecond, "UTC"), nullable: true),
                        new TimestampArray.Builder(new TimestampType(TimeUnit.Microsecond, "UTC"))
                            .Append(new DateTimeOffset(dt, TimeSpan.Zero)).Build());
            default:
                throw new ArgumentException(
                    $"{caller}: {FunctionName} cannot bind parameter '{name}' — a {value.Type} has no SQL "
                    + "parameter form. Pass a number, string, boolean, date or null; build a list or struct "
                    + "in SQL instead.");
        }
    }

    /// <summary>
    /// A numeric parameter, as <c>BIGINT</c> when the value is integral and fits, else <c>DECIMAL</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Fluid's number model IS decimal (§7.3), so every number arrives here as one — including a literal
    /// `10`. Binding all of them as DECIMAL would work but would make an ordinary integer parameter compare
    /// against integer columns as a decimal; the integral case is narrowed back to BIGINT so the common
    /// spelling behaves like the literal it replaced. A value outside BIGINT keeps full precision as
    /// DECIMAL(38, scale).
    /// </remarks>
    private static (Field, IArrowArray) NumberParameter(string name, decimal number)
    {
        if (decimal.Truncate(number) == number && number >= long.MinValue && number <= long.MaxValue)
        {
            return (new Field(name, Int64Type.Default, nullable: true),
                    new Int64Array.Builder().Append((long)number).Build());
        }
        // The decimal's own scale, so 19.99 stays 19.99 rather than being widened or rounded.
        var scale = (int)((decimal.GetBits(number)[3] >> 16) & 0x7F);
        var type = new Decimal128Type(38, scale);
        return (new Field(name, type, nullable: true),
                new Decimal128Array.Builder(type).Append(number).Build());
    }

    /// <summary>
    /// Refuses <paramref name="sql"/> unless DuckDB's own parser can serialize it, which it does for the
    /// SELECT family and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ <b>The classifier is <c>json_serialize_sql</c>, i.e. THE ENGINE'S OWN PARSER, and the SQL crosses
    /// as a BOUND PARAMETER.</b> Two cheaper-looking mechanisms were measured and both are broken:
    /// </para>
    /// <list type="bullet">
    /// <item>A prefix check (<c>starts with SELECT/WITH</c>) waves through
    /// <c>WITH x AS (SELECT 1) INSERT INTO t SELECT * FROM x</c> — a write that begins with WITH.</item>
    /// <item>Wrapping as <c>SELECT * FROM (&lt;sql&gt;)</c> refuses the honest non-SELECT and is defeated by
    /// the adversarial one: MEASURED, <c>SELECT 1) ; INSERT INTO t VALUES (99); SELECT * FROM (SELECT 2</c>
    /// performed the insert, and a <c>DROP TABLE</c> variant dropped the table. It is string concatenation,
    /// so it has an escape by construction.</item>
    /// </list>
    /// <para>
    /// The serializer refuses multi-statement input too, so <c>SELECT 1; INSERT …</c> cannot slip past, and
    /// it PARSES ONLY — measured, a table's row count is unchanged by classifying a DELETE against it.
    /// </para>
    /// <para>
    /// ⚠ <b>The cost, stated rather than hidden: it also refuses some READ-ONLY statements.</b> <c>PIVOT</c>
    /// and <c>EXPLAIN</c> are not serializable, and for PIVOT that holds even wrapped in a subquery, where
    /// it would otherwise execute. <c>DESCRIBE</c>, <c>SUMMARIZE</c>, <c>VALUES</c>, <c>TABLE t</c>,
    /// <c>FROM t</c>, CTEs and set operations all pass. Being conservative in this direction is the correct
    /// trade: the alternative admits writes.
    /// </para>
    /// <para>
    /// ⚠ If the classification itself cannot run — <c>json</c> unavailable, host unreachable — the statement
    /// is REFUSED, not waved through. An unenforceable check must fail closed.
    /// </para>
    /// </remarks>
    private static void RefuseUnlessSelect(string caller, string sql, IHostQuery host)
    {
        var (isSelect, msg) = Classify(caller, FunctionName, sql, host);
        if (isSelect)
        {
            return;
        }

        // ⚠ The engine's own message is surfaced verbatim, because it distinguishes the two causes this
        // check would otherwise conflate: "Only SELECT statements can be serialized to json!" (a non-SELECT)
        // and a real syntax error such as `syntax error at or near "SELEC"`. Reporting "not a SELECT" for a
        // typo would send the author looking in the wrong place.
        throw new InvalidOperationException(
            $"{caller}: {FunctionName}() runs SELECT statements only, and this one was refused by DuckDB's "
            + $"parser: {msg ?? "(no message)"}. A template is rendered while a statement is being BOUND, "
            + "and a bind repeats and happens without execution — so a write here would fire on EXPLAIN. "
            + $"To write, use {FluidHostExec.FunctionName}() from fabricator_render.");
    }

    /// <summary>
    /// Asks DuckDB'S OWN PARSER whether <paramref name="sql"/> is a SELECT, without executing it. Returns
    /// whether it is, plus the engine's message when it is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>This is the one mechanism behind two opposite policies</b> — <c>query()</c> refuses everything
    /// that is NOT a SELECT, <c>exec()</c> refuses everything that IS one — so they cannot drift apart on
    /// what "a SELECT" means. Both fail CLOSED: a classification that cannot run refuses.
    /// </para>
    /// <para>
    /// ⚠⚠ <b>The two mechanisms anyone reaches for first are MEASURED broken</b> (docs/fluid-templating.md
    /// §9.2). A prefix check admits <c>WITH x AS (SELECT 1) INSERT INTO t SELECT * FROM x</c>, a write
    /// beginning with WITH. Wrapping as <c>SELECT * FROM (&lt;sql&gt;)</c> is worse than useless: it refuses
    /// every honest non-SELECT and is defeated by the adversarial one —
    /// <c>SELECT 1) ; INSERT INTO aud VALUES (99); SELECT * FROM (SELECT 2</c> performed the insert, and a
    /// DROP variant dropped the table. A mechanism that refuses the accident and admits the attack is worse
    /// than none, because it reads as a defence.
    /// </para>
    /// <para>
    /// ⚠ <b>MULTI-STATEMENT, measured 2026-09-02 and NOT what an earlier note claimed.</b> The classifier
    /// does not refuse multi-statement input as such: <c>SELECT 1; SELECT 2</c> classifies as a SELECT,
    /// while <c>SELECT 1; INSERT …</c>, <c>SELECT 1; DROP TABLE t</c> and <c>CREATE …; INSERT …</c> are all
    /// refused. So the SAFETY property is intact in both directions — an all-SELECT sequence is harmless to
    /// <c>query()</c>, and a sequence containing a write reaches <c>exec()</c>, which is exactly the
    /// several-statements case exec exists for.
    /// </para>
    /// <para>
    /// ⚠ It PARSES ONLY. Measured: classifying a <c>DELETE</c> leaves the target table's row count
    /// unchanged. And it tolerates a <c>$name</c> placeholder, which is what lets the parameterised filter
    /// forms be classified at all.
    /// </para>
    /// </remarks>
    internal static (bool IsSelect, string? Message) Classify(string caller, string fn, string sql, IHostQuery host)
    {
        const string classify =
            "SELECT j ->> 'error' AS err, j ->> 'error_message' AS msg "
            // ⚠ The cast is REQUIRED, not defensive: an untyped placeholder has no type at bind, so DuckDB
            // cannot resolve the overload and answers "json_serialize_sql first argument must be a VARCHAR"
            // - which arrives as a REFUSAL, since an unenforceable check fails closed.
            // ⚠⚠ $sql, NOT `?`: the batch below names its column, and a batch whose columns are all named
            // is bound BY NAME. This read `?` first and broke the instant named binding landed
            // ("Values were not provided for ... parameters: 1"), which is the mechanism announcing itself.
            + "FROM (SELECT json_serialize_sql($sql::VARCHAR) AS j)";

        string? err;
        string? msg;
        try
        {
            var field = new Field("sql", Apache.Arrow.Types.StringType.Default, nullable: false);
            var schema = new Schema(new[] { field }, null);
            var arg = new StringArray.Builder().Append(sql).Build();
            using var parameters = new RecordBatch(schema, new IArrowArray[] { arg }, 1);
            using var stream = host.Query(classify, parameters);
            var batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
            if (batch is null || batch.Length == 0)
            {
                throw new InvalidOperationException("the classifier returned no row");
            }
            using (batch)
            {
                err = ((StringArray)batch.Column(0)).GetString(0);
                msg = batch.Column(1) is StringArray m && !m.IsNull(0) ? m.GetString(0) : null;
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{caller}: {fn}() could not establish what kind of statement this is, so it was "
                + $"refused rather than run: {ex.Message}", ex);
        }

        return (string.Equals(err, "false", StringComparison.OrdinalIgnoreCase), msg);
    }
}

/// <summary>
/// A result row whose cells were read EAGERLY, so it outlives the <see cref="RecordBatch"/> they came from.
/// </summary>
/// <remarks>
/// ⚠ It exists because the batches are disposed as the result is consumed, while the rows live for the whole
/// render. It keeps <see cref="ArrowStruct"/>'s lookup rule — name first, then an int-parse ORDINAL, and
/// FALSE for an unknown member so Fluid can still answer <c>.size</c> — by building itself FROM one.
/// </remarks>
internal sealed class EagerRow : IFluidIndexable
{
    private readonly List<KeyValuePair<string, FluidValue>> _cells = new();

    internal EagerRow(IReadOnlyList<Field> fields, IReadOnlyList<IArrowArray> columns, int row)
    {
        var source = new ArrowStruct(fields, columns, row);
        foreach (var name in source.Keys)
        {
            _cells.Add(new KeyValuePair<string, FluidValue>(
                name, source.TryGetValue(name, out var v) ? v : NilValue.Instance));
        }
    }

    public int Count => _cells.Count;

    public IEnumerable<string> Keys => _cells.Select(c => c.Key);

    public bool TryGetValue(string name, out FluidValue value)
    {
        for (int i = 0; i < _cells.Count; i++)
        {
            if (string.Equals(_cells[i].Key, name, StringComparison.Ordinal))
            {
                value = _cells[i].Value;
                return true;
            }
        }
        // ⚠ Fluid resolves `r[0]` by asking for the KEY "0", so an int-parse fallback IS index access — the
        // same rule ArrowStruct documents. A column genuinely named "0" wins, which is the right precedence.
        if (int.TryParse(name, out var ordinal) && ordinal >= 0 && ordinal < _cells.Count)
        {
            value = _cells[ordinal].Value;
            return true;
        }
        value = NilValue.Instance;
        return false;
    }
}
