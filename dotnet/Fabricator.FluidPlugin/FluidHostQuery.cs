// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Types;
using Apache.Arrow.Ipc;
using Fabricator.Bridge;
using Fluid;
using Fluid.Ast;
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
    internal static string CallerOf(TemplateContext ctx) =>
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
        return Run(caller, args.At(0).ToStringValue(ctx), null, FluidRenderSession.For(ctx));
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
        return new(Run(caller, input.ToStringValue(ctx), BuildParameters(caller, FunctionName, args, ctx),
                        FluidRenderSession.For(ctx)));
    }

    // ⚠ `session` is THIS RENDER's pinned connection (ABI v84) — see FluidRenderSession. Both the
    // classifier and the statement itself go through it, so a template's exec() and query() share one
    // DuckDB connection and therefore one TEMPORARY catalog. Null only when the host publishes no
    // IHostQuery at all, which the guard below reports by name.
    /// <summary>The Liquid tag name of the BLOCK form: <c>{% query name %}…{% endquery %}</c>.</summary>
    internal const string BlockName = "query";

    /// <summary>
    /// The BLOCK form's body, already rendered to text: run it and return the ROW SET, so the caller can
    /// bind it to a template variable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ It returns exactly what the <c>query()</c> FUNCTION returns — an array of indexable rows — because
    /// it goes through the same <see cref="Run"/>. That is the whole point of the block: the body is SQL
    /// written as ordinary template text, and the RESULT is still a result set rather than a rendered
    /// string. One mechanism, two spellings, so the classifier, the row cap, the value model and the
    /// per-render connection cannot drift between them.
    /// </para>
    /// <para>
    /// ⚠ <b>Named arguments on the tag become BOUND parameters</b> — <c>{% query t a: 1, b: 2 %}</c> binds
    /// <c>$a</c> and <c>$b</c>, so a value crosses as a VALUE rather than as SQL text. Interpolating with
    /// <c>{{ v | sql }}</c> still works and is what you need for an object NAME or a fragment, which is
    /// not something a parameter can carry.
    /// </para>
    /// </remarks>
    /// <param name="tag">The BLOCK that captured the body — <c>query</c> or <c>print</c>. It names itself in
    /// the empty-body message, because "{% query %} block is empty" pointing at a <c>{% print %}</c> sends
    /// the author to the wrong tag.</param>
    internal static FluidValue RunCaptured(TemplateContext ctx, string tag, string sql,
                                           RecordBatch? parameters)
    {
        var caller = CallerOf(ctx);
        // ⚠ The block-specific empty message, before Run's own: an empty body is a different mistake from
        // an empty argument, and json_serialize_sql('') reports NO error, so something must refuse it here.
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException(
                $"{caller}: {{% {tag} %}} block is empty — it rendered no SQL.");
        }
        return Run(caller, sql, parameters, FluidRenderSession.For(ctx), "{% " + tag + " %}");
    }

    /// <param name="surface">How to NAME this call in an error — <c>query()</c> by default, so the function
    /// and filter forms are unchanged, and <c>{% print %}</c> for the print block.
    /// ⚠ Not cosmetic: the refusal below tells the author which construct to reach for instead, and a
    /// message naming <c>query()</c> inside a <c>{% print %}</c> sends them to the wrong tag. Every existing
    /// caller passes nothing and keeps the wording its gates already assert.</param>
    private static FluidValue Run(string caller, string? sql, RecordBatch? parameters,
                                  FluidRenderSession? session, string? surface = null)
    {
        surface ??= FunctionName + "()";
        // ⚠ MEASURED: json_serialize_sql('') reports NO error — an empty string parses to zero statements,
        // so the classifier below would wave it through. Refused here, where the cause can still be named.
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException($"{caller}: {surface} was given an empty statement.");
        }

        var run = session
            ?? throw new InvalidOperationException(
                $"{caller}: {surface} needs the IHostQuery service, which is not published here. "
                + "It is available only from inside a fabricator function call.");

        RefuseUnlessSelect(caller, surface, sql, run);

        using var stream = run.Query(sql, parameters);
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
                        $"{caller}: {surface} returned more than {MaxRows} rows. Narrow the query — "
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
                    rows.Add(new DictionaryValue(new EagerStruct(fields, columns, r)));
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
    /// <summary>
    /// The BLOCK forms' named arguments — <c>{% query t a: 1, b: 2 %}</c> — as a 1-row Arrow batch, or
    /// <see langword="null"/> when there are none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ The AST twin of <see cref="BuildParameters"/>: a tag's arguments arrive as unevaluated
    /// <see cref="FilterArgument"/>s (name + expression) where a filter's arrive already evaluated, so this
    /// evaluates each and then hands it to the SAME <c>ToParameter</c>. One conversion table for all three
    /// spellings — the value→Arrow rules (the int64/decimal ladder, the UTC stamp on dates, the refusal of
    /// LIST/STRUCT/MAP) cannot drift between a filter and a block.
    /// </para>
    /// <para>
    /// ⚠ POSITIONAL arguments are REFUSED rather than ignored, exactly as in the filter form: the statement
    /// references parameters as <c>$name</c>, so an unnamed one could not be bound and dropping it silently
    /// would run the statement with a parameter the author believed they had supplied.
    /// </para>
    /// <para>
    /// ⚠ A DUPLICATE name is refused HERE as well as by the host. The host's refusal is correct but names
    /// the crossing rather than the tag; catching it early is what makes the message point at the template.
    /// </para>
    /// </remarks>
    internal static async ValueTask<RecordBatch?> BuildBlockParametersAsync(
        string caller, string tag, IReadOnlyList<FilterArgument>? args, TemplateContext ctx)
    {
        if (args is null || args.Count == 0)
        {
            return null;
        }

        var fields = new List<Field>(args.Count);
        var columns = new List<IArrowArray>(args.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arg in args)
        {
            if (string.IsNullOrEmpty(arg.Name))
            {
                throw new ArgumentException(
                    $"{caller}: {{% {tag} %}} takes NAMED parameters only — write `{tag} … name: value` so "
                    + "the statement can reference $name.");
            }
            if (!seen.Add(arg.Name))
            {
                throw new ArgumentException(
                    $"{caller}: {{% {tag} %}} was given the parameter '{arg.Name}' twice.");
            }
            var value = await arg.Expression.EvaluateAsync(ctx);
            var (field, array) = ToParameter(caller, arg.Name, value, ctx);
            fields.Add(field);
            columns.Add(array);
        }
        return new RecordBatch(new Schema(fields, null), columns, 1);
    }

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
    /// <summary>
    /// A Fluid value as ONE bound SQL parameter, RECURSIVELY: scalars, lists, structs and any nesting of
    /// them. Two passes — infer the Arrow type over the whole value, then build it — because Arrow (and
    /// DuckDB) need a concrete type at every level where Fluid has none.
    /// <para>⚠ Nesting is not speculative: DuckDB binds all of these, MEASURED — a STRUCT
    /// (<c>SELECT ($1).a</c> gives 42), a nested LIST (<c>len(unnest($1))</c> gives 2, 1), a LIST of STRUCT
    /// (<c>unnest($1).label</c> gives a, b) and a MAP (<c>($1)['k']</c> gives 7).</para>
    /// <para>⚠⚠ A DuckDB MAP round-trips as a STRUCT, and that is a real asymmetry rather than an
    /// oversight: read in, a MAP becomes an <c>IFluidIndexable</c> exactly like a struct, so nothing at the
    /// Fluid level distinguishes them and a struct is the only honest thing to write back. Refusing instead
    /// would block a shape that otherwise works end to end.</para>
    /// </summary>
    private static (Field, IArrowArray) ToParameter(string caller, string name, FluidValue value,
                                                    TemplateContext ctx)
    {
        // ⚠ An all-NULL or empty value carries no type at all. VARCHAR is what the scalar NULL case has
        // always used, and for the same reason: it is what DuckDB casts most freely FROM.
        var type = InferType(caller, name, value, ctx, 0) ?? StringType.Default;
        return (new Field(name, type, nullable: true),
                BuildArray(caller, name, type, new[] { value }, ctx));
    }

    /// <summary>How deep a bound value may nest. A guard, not a limit anyone should meet: it exists so a
    /// cyclic or pathological value fails with a sentence instead of a stack overflow.</summary>
    private const int MaxParameterDepth = 16;

    private static bool IsNullish(FluidValue v)
        => v.Type is FluidValues.Nil or FluidValues.Empty or FluidValues.Blank;

    private static IReadOnlyList<FluidValue> Items(FluidValue v, TemplateContext ctx)
        => (v as ArrayValue)?.Values ?? v.EnumerateAsync(ctx).ToBlockingEnumerable().ToList();

    /// <summary>The members of a dictionary-like value, in the order it enumerates them.
    /// <para>⚠ Read through <c>EnumerateAsync</c> rather than <c>IFluidIndexable.Keys</c> because that is
    /// the ONE spelling that works for both sources: a DuckDB STRUCT arrives as a <c>DictionaryValue</c>
    /// over our <c>ArrowStruct</c>, a JSON object through Fluid's own JsonNode support, and both yield
    /// <c>[key, value]</c> pairs (measured — it is what <c>{% for kv in v %}</c> uses). A value that yields
    /// anything else is refused rather than guessed at.</para></summary>
    private static IReadOnlyList<(string Key, FluidValue Value)> Members(string caller, string name,
                                                                        FluidValue v, TemplateContext ctx)
    {
        var members = new List<(string, FluidValue)>();
        foreach (var pair in v.EnumerateAsync(ctx).ToBlockingEnumerable())
        {
            if (pair is ArrayValue kv && kv.Values.Count == 2)
            {
                members.Add((kv.Values[0].ToStringValue(), kv.Values[1]));
            }
            else
            {
                throw Refusal(caller, name, "a " + v.Type + " whose members cannot be enumerated as "
                                            + "key/value pairs has no SQL parameter form");
            }
        }
        return members;
    }

    // null == "no type yet" (a NULL, or an empty container). Resolved to VARCHAR by the caller.
    private static IArrowType? InferType(string caller, string name, FluidValue v, TemplateContext ctx,
                                         int depth)
    {
        if (depth > MaxParameterDepth)
        {
            throw Refusal(caller, name, "it nests deeper than " + MaxParameterDepth + " levels");
        }
        switch (v.Type)
        {
            case FluidValues.Nil:
            case FluidValues.Empty:
            case FluidValues.Blank:
                return null;
            case FluidValues.Boolean:
                return BooleanType.Default;
            case FluidValues.String:
                return StringType.Default;
            case FluidValues.DateTime:
                return new TimestampType(TimeUnit.Microsecond, "UTC");
            case FluidValues.Number:
            {
                // Fluid's number model IS decimal, so even `10` arrives as one. The integral case is
                // narrowed back to BIGINT so the ordinary spelling behaves like the literal it replaced; a
                // fractional value keeps its own scale, so 19.99 stays 19.99.
                var n = v.ToNumberValue();
                return decimal.Truncate(n) == n && n >= long.MinValue && n <= long.MaxValue
                    ? Int64Type.Default
                    : new Decimal128Type(38, (int)((decimal.GetBits(n)[3] >> 16) & 0x7F));
            }
            case FluidValues.Array:
            {
                IArrowType? elem = null;
                foreach (var item in Items(v, ctx))
                {
                    elem = Unify(caller, name, elem, InferType(caller, name, item, ctx, depth + 1));
                }
                return new ListType(new Field("item", elem ?? StringType.Default, nullable: true));
            }
            case FluidValues.Dictionary:
            case FluidValues.Object:
            {
                var fields = new List<Field>();
                foreach (var (key, member) in Members(caller, name, v, ctx))
                {
                    var ft = InferType(caller, name, member, ctx, depth + 1) ?? StringType.Default;
                    fields.Add(new Field(key, ft, nullable: true));
                }
                if (fields.Count == 0)
                {
                    throw Refusal(caller, name, "an empty struct has no SQL parameter form");
                }
                return new StructType(fields);
            }
            default:
                throw Refusal(caller, name, "a " + v.Type + " has no SQL parameter form");
        }
    }

    /// <summary>
    /// One type for values that must share one. ⚠⚠ MIXED KINDS ARE REFUSED rather than coerced: the only
    /// representation they all share is text, and turning 5 into '5' silently changes what the statement
    /// compares. The only widening allowed is the scalar path's own int64 to decimal ladder; for structs the
    /// field NAME SETS must match exactly, since unioning them with NULLs would invent members the author
    /// never wrote.
    /// </summary>
    private static IArrowType? Unify(string caller, string name, IArrowType? a, IArrowType? b)
    {
        if (a is null) { return b; }
        if (b is null) { return a; }
        switch (a, b)
        {
            case (Int64Type, Int64Type):
                return a;
            case (Decimal128Type da, Decimal128Type db):
                return new Decimal128Type(38, Math.Max(da.Scale, db.Scale));
            case (Int64Type, Decimal128Type d1):
                return d1;
            case (Decimal128Type d2, Int64Type):
                return d2;
            case (ListType la, ListType lb):
                return new ListType(new Field(
                    "item", Unify(caller, name, la.ValueDataType, lb.ValueDataType) ?? StringType.Default,
                    nullable: true));
            case (StructType sa, StructType sb):
            {
                if (sa.Fields.Count != sb.Fields.Count
                    || !sa.Fields.Select(f => f.Name).SequenceEqual(sb.Fields.Select(f => f.Name)))
                {
                    throw Refusal(caller, name,
                                  "its structs do not all carry the same fields in the same order, and an "
                                  + "Arrow struct has ONE shape");
                }
                var merged = new List<Field>(sa.Fields.Count);
                for (int i = 0; i < sa.Fields.Count; i++)
                {
                    merged.Add(new Field(
                        sa.Fields[i].Name,
                        Unify(caller, name, sa.Fields[i].DataType, sb.Fields[i].DataType)
                            ?? StringType.Default,
                        nullable: true));
                }
                return new StructType(merged);
            }
            default:
                if (a.TypeId == b.TypeId)
                {
                    return a; // Boolean / String / Timestamp — nothing to widen
                }
                throw Refusal(caller, name,
                              "its elements mix " + Friendly(a) + " and " + Friendly(b)
                              + ", and one parameter carries ONE type");
        }
    }

    /// <summary>
    /// Builds the Arrow array for <paramref name="values"/> at the already-inferred <paramref name="type"/>.
    /// <para>⚠⚠ EVERY BUILDER IS CONSTRUCTED FROM THE DECLARED TYPE, and the nested arrays are assembled by
    /// hand rather than through <c>ListArray.Builder</c>. That is not fussiness: its <c>ValueBuilder</c>
    /// does NOT carry a <c>TimestampType</c>'s UNIT into the builder it creates, so appending through it
    /// stored MILLISECONDS under a field declaring MICROSECONDS and a date read back as
    /// <c>1970-01-20 09:36:57.6</c> — the January-1970 signature this repo already records for hand-rolled
    /// Arrow timestamp sites. Only a DATE shows it; numbers, strings and booleans have nothing to get
    /// wrong, so a battery without one would ship it.</para>
    /// </summary>
    private static IArrowArray BuildArray(string caller, string name, IArrowType type,
                                          IReadOnlyList<FluidValue> values, TemplateContext ctx)
    {
        switch (type)
        {
            case BooleanType:
            {
                var b = new BooleanArray.Builder();
                foreach (var v in values)
                {
                    if (IsNullish(v)) { b.AppendNull(); } else { b.Append(v.ToBooleanValue()); }
                }
                return b.Build();
            }
            case Int64Type:
            {
                var b = new Int64Array.Builder();
                foreach (var v in values)
                {
                    if (IsNullish(v)) { b.AppendNull(); } else { b.Append((long)v.ToNumberValue()); }
                }
                return b.Build();
            }
            case Decimal128Type dec:
            {
                var b = new Decimal128Array.Builder(dec);
                foreach (var v in values)
                {
                    if (IsNullish(v)) { b.AppendNull(); } else { b.Append(v.ToNumberValue()); }
                }
                return b.Build();
            }
            case TimestampType ts:
            {
                var b = new TimestampArray.Builder(ts); // the DECLARED unit, never the builder's default
                foreach (var v in values)
                {
                    if (IsNullish(v))
                    {
                        b.AppendNull();
                        continue;
                    }
                    // Stamped UTC: an Unspecified DateTime is otherwise resolved against the MACHINE's
                    // zone, which silently shifts the value.
                    var o = v.ToObjectValue();
                    var d = o is DateTimeOffset dto
                        ? dto.UtcDateTime
                        : DateTime.SpecifyKind((DateTime)(o ?? default(DateTime)), DateTimeKind.Utc);
                    b.Append(new DateTimeOffset(d, TimeSpan.Zero));
                }
                return b.Build();
            }
            case ListType lt:
            {
                var flat = new List<FluidValue>();
                var offsets = new ArrowBuffer.Builder<int>();
                var validity = new ArrowBuffer.BitmapBuilder();
                int nulls = 0;
                offsets.Append(0);
                foreach (var v in values)
                {
                    if (IsNullish(v))
                    {
                        validity.Append(false);
                        nulls++;
                    }
                    else
                    {
                        validity.Append(true);
                        flat.AddRange(Items(v, ctx));
                    }
                    offsets.Append(flat.Count);
                }
                var child = BuildArray(caller, name, lt.ValueDataType, flat, ctx);
                return new ListArray(type, values.Count, offsets.Build(), child,
                                     nulls == 0 ? ArrowBuffer.Empty : validity.Build(), nulls);
            }
            case StructType st:
            {
                // One COLUMN per field, gathered across all the struct values — a struct array is stored
                // column-wise, so an absent member becomes a NULL in that column rather than a hole.
                var perValue = new List<Dictionary<string, FluidValue>?>(values.Count);
                foreach (var v in values)
                {
                    perValue.Add(IsNullish(v)
                                     ? null
                                     : Members(caller, name, v, ctx)
                                           .ToDictionary(m => m.Key, m => m.Value));
                }
                var children = new List<IArrowArray>(st.Fields.Count);
                foreach (var f in st.Fields)
                {
                    var column = new List<FluidValue>(values.Count);
                    foreach (var members in perValue)
                    {
                        column.Add(members is not null && members.TryGetValue(f.Name, out var mv)
                                       ? mv
                                       : NilValue.Instance);
                    }
                    children.Add(BuildArray(caller, name, f.DataType, column, ctx));
                }
                var validity = new ArrowBuffer.BitmapBuilder();
                int nulls = 0;
                foreach (var v in values)
                {
                    bool present = !IsNullish(v);
                    validity.Append(present);
                    if (!present) { nulls++; }
                }
                return new StructArray(type, values.Count, children,
                                       nulls == 0 ? ArrowBuffer.Empty : validity.Build(), nulls);
            }
            default:
            {
                var b = new StringArray.Builder();
                foreach (var v in values)
                {
                    if (IsNullish(v)) { b.AppendNull(); } else { b.Append(v.ToStringValue(ctx)); }
                }
                return b.Build();
            }
        }
    }

    /// <summary>The TEMPLATE AUTHOR's word for a type, not Arrow's. They wrote `1` and `"a"`, so the
    /// refusal must say "Number and String" — `int64` and `utf8` name an implementation they never see.
    /// ⚠ int64 and decimal are UNIFIED rather than refused, so they can never appear opposite each other
    /// here and both may safely read as "Number".</summary>
    private static string Friendly(IArrowType t) => t switch
    {
        Int64Type or Decimal128Type => "Number",
        StringType => "String",
        BooleanType => "Boolean",
        TimestampType => "DateTime",
        ListType => "Array",
        StructType => "Dictionary",
        _ => t.Name,
    };

    private static ArgumentException Refusal(string caller, string name, string why)
        => new($"{caller}: {FunctionName} cannot bind parameter '{name}' — {why}. Pass numbers, strings, "
               + "booleans, dates, nulls, or lists and structs nesting those.");

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
    private static void RefuseUnlessSelect(string caller, string surface, string sql,
                                           FluidRenderSession run)
    {
        var (isSelect, msg) = Classify(caller, FunctionName, sql, run);
        if (isSelect)
        {
            return;
        }

        // ⚠ The engine's own message is surfaced verbatim, because it distinguishes the two causes this
        // check would otherwise conflate: "Only SELECT statements can be serialized to json!" (a non-SELECT)
        // and a real syntax error such as `syntax error at or near "SELEC"`. Reporting "not a SELECT" for a
        // typo would send the author looking in the wrong place.
        throw new InvalidOperationException(
            $"{caller}: {surface} runs SELECT statements only, and this one was refused by DuckDB's "
            + $"parser: {msg ?? "(no message)"}. A template is rendered while a statement is being BOUND, "
            + "and a bind repeats and happens without execution — so a write here would fire on EXPLAIN. "
            + $"To write, use {FluidHostExec.FunctionName}() from fluid_render.");
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
    // ⚠ Classified on the SAME pinned connection as the statement it is about (ABI v84), which is right
    // on both counts: it is one connection per render instead of one per classification, and a template
    // whose exec() created a TEMP table classifies its later query() against the connection that owns it.
    // ⚠ It PARSES ONLY, so it cannot see a temp table either way — what matters is that it does not open
    // a second connection to find that out.
    internal static (bool IsSelect, string? Message) Classify(string caller, string fn, string sql,
                                                             FluidRenderSession run)
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
            using var stream = run.Query(classify, parameters);
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
/// A struct whose cells were read EAGERLY, so it outlives the <see cref="RecordBatch"/> they came from —
/// used both for a query-result ROW and for a nested STRUCT cell.
/// </summary>
/// <remarks>
/// ⚠ It exists because the batches are disposed as the result is consumed, while the rows live for the whole
/// render. It keeps <see cref="ArrowStruct"/>'s lookup rule — name first, then an int-parse ORDINAL, and
/// FALSE for an unknown member so Fluid can still answer <c>.size</c> — by building itself FROM one.
/// <para>⚠⚠ It was called <c>EagerStruct</c> and served ROWS only, which left the eagerness ONE LEVEL DEEP: a
/// STRUCT cell inside a row came back as a lazy <see cref="ArrowStruct"/> and reading it after the batch
/// was disposed threw a NullReferenceException. `{% query r %}SELECT {'a':1} AS s{% endquery %}{{ r[0].s.a }}`
/// reproduced it with no parameters at all.</para>
/// </remarks>
internal sealed class EagerStruct : IFluidIndexable
{
    private readonly List<KeyValuePair<string, FluidValue>> _cells = new();

    internal EagerStruct(IReadOnlyList<Field> fields, IReadOnlyList<IArrowArray> columns, int row)
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
