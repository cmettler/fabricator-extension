// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using Apache.Arrow;
using Apache.Arrow.Types;
using Fabricator.Bridge;
using Fluid;
using Fluid.Values;

namespace Fabricator.FluidPlugin;

/// <summary>
/// The ONE value model shared by every function this plugin ships: how a params bag — a DuckDB STRUCT/MAP, or
/// a JSON string — becomes template variables, and how a value renders, compares and arithmetics inside a
/// template.
///
/// <para>It is deliberately one type rather than one per function. <c>fluid_render</c> produces TEXT and
/// <c>fluid_query</c> produces SQL, but a params bag means the same thing in both, and two mappings that
/// drifted would make the same JSON render differently depending on which function you called.</para>
///
/// <para><b>⚠⚠ THE MEASUREMENT THIS FILE EXISTS FOR — Fluid 3.0.0-beta.7 understands
/// <c>System.Text.Json</c>'s <see cref="JsonNode"/> natively, and that support RENDERS CORRECTLY WHILE
/// COMPUTING WRONG.</b> Bound with no converter, every one of <c>{{ d.i }}</c>, <c>{{ d.big }}</c>,
/// <c>{{ d.o.a.b }}</c>, <c>{% for x in d.arr %}</c>, <c>d.arr[1]</c>, <c>d.arr.size</c> is exactly right —
/// and <c>{% if d.i &gt; 1 %}</c> with <c>i = 3</c> takes the ELSE branch, <c>{{ d.money | plus: 1 }}</c>
/// with 19.99 renders <c>1</c>, <c>{% if d.s == 'x' %}</c> with <c>s = "x"</c> is FALSE, and summing an array
/// in a loop yields 0. The leaves arrive as opaque nodes, so they format faithfully and compare as nothing.
/// <b>A render-only test suite passes 100% against that.</b> For <c>fluid_query</c>, where the rendered text
/// IS the SQL, a wrong <c>{% if %}</c> branch is a wrong statement — so the converter below is load-bearing
/// rather than an optimisation, and <c>verify_plugin_fluid</c> asserts COMPARISON and ARITHMETIC precisely
/// because rendering cannot tell the two builds apart.</para>
/// </summary>
internal static class FluidValueModel
{
    /// <summary>
    /// Shared options: the JSON leaf converter plus the SQL-quoting filters. Fluid documents
    /// <c>TemplateOptions</c> as reusable across contexts, and this instance is never mutated after
    /// construction.
    /// </summary>
    internal static readonly TemplateOptions Options = Build();

    /// <summary>A fresh context over the shared <see cref="Options"/>.</summary>
    internal static TemplateContext NewContext() => new(Options);

    private static TemplateOptions Build()
    {
        var o = new TemplateOptions();

        // Leaves ONLY: returning null means "not handled", so JsonObject/JsonArray keep Fluid's own native
        // handling — member access, iteration, .size, indexing, all MEASURED correct without us.
        o.ValueConverters.Add(v => v is JsonValue jv ? JsonLeaf(jv) : null);

        // Rendered text becomes SQL in fluid_query, so give the author an explicit way to say "quote this".
        // NOT automatic: a template must be able to emit raw SQL fragments — that is the entire point of a
        // SQL-generating function — so quoting is opt-in per interpolation and loudly documented.
        o.Filters.AddFilter("sql", (input, _, ctx) =>
            new ValueTask<FluidValue>(new StringValue(SqlLiteral(input, ctx), encode: false)));
        // ⚠ `query` is registered ONCE, HERE, rather than per context: this TemplateOptions is a static
        // singleton, so a per-render AddFilter would mutate shared state on every call. The function form
        // of the same name is per-context (FluidEngine); the caller travels in AmbientValues.
        o.Filters.AddFilter(FluidHostQuery.FunctionName, FluidHostQuery.Filter);
        // ⚠ The write-side twin, registered unconditionally on the SHARED options like query's. It is
        // available on BOTH surfaces; in fluid_query that means it writes during BINDING, which repeats —
        // see FluidHostExec.
        o.Filters.AddFilter(FluidHostExec.FunctionName, FluidHostExec.Filter);
        o.Filters.AddFilter("sql_ident", (input, _, ctx) =>
            new ValueTask<FluidValue>(new StringValue(DuckSql.QuoteIdent(input.ToStringValue(ctx)), encode: false)));

        // {% include %} / {% render %} resolve through DuckDB's FileSystem against fluid_template_root.
        // ⚠ ONE instance on the shared static, and safe here where a per-render FILTER would not be: Fluid
        // passes the TemplateContext to the provider, so the root, the read cache and the failure record all
        // travel per call rather than on this object. See HostTemplateFileProvider.
        o.FileProvider = new HostTemplateFileProvider();

        return o;
    }

    /// <summary>Binds every member of a params bag as a top-level template variable.</summary>
    /// <param name="ctx">The context to populate.</param>
    /// <param name="paramsCol">The ANY-declared argument column: a STRUCT, a MAP, a JSON string, or all-null.</param>
    /// <param name="row">Which row of that column supplies this call's variables.</param>
    internal static void Bind(TemplateContext ctx, IArrowArray? paramsCol, int row)
    {
        foreach (var member in Capture(paramsCol, row))
        {
            SetVariable(ctx, member.Key, member.Value);
        }
    }

    /// <summary>
    /// The params bag's members as PLAIN VALUES, so a caller can bind them to a context LATER — after the
    /// Arrow batch they came from is gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ <b>Safe to outlive the batch because <see cref="ReadCell"/> is EAGER ALL THE WAY DOWN</b>, which
    /// was checked rather than assumed: a STRUCT materialises through <c>EagerStruct</c>, a MAP through
    /// <see cref="ArrowMap"/> (whose constructor copies every entry), a LIST through <c>ReadList</c>, and
    /// every scalar is a .NET value. Nothing returned here holds an <c>IArrowArray</c>. Were any leaf lazy
    /// it would read a disposed batch's nulled buffers at render time — the exact NullReferenceException
    /// the eagerness note on the STRUCT case records.
    /// </para>
    /// <para>
    /// It exists for <c>fluid_query_batch</c>, whose bind args are gone by the time its groups render, and
    /// whose context must be per-EXECUTION (a binding is reused across prepared re-executions, so a context
    /// built at bind would carry one execution's state into the next).
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<KeyValuePair<string, object?>> Capture(IArrowArray? paramsCol, int row)
    {
        var members = new List<KeyValuePair<string, object?>>();
        switch (paramsCol)
        {
            case null:
            case NullArray:
                break;
            case StructArray sa when sa.Data.DataType is StructType st:
                if (sa.IsNull(row))
                {
                    break;
                }
                for (int k = 0; k < st.Fields.Count; k++)
                {
                    members.Add(new KeyValuePair<string, object?>(st.Fields[k].Name, ReadCell(sa.Fields[k], row)));
                }
                break;
            case StringArray json:
                if (!json.IsNull(row))
                {
                    CaptureJson(members, json.GetString(row));
                }
                break;
            default:
                // A MAP arrives as a MapArray; spread its entries the same way a STRUCT's fields are, so
                // `{'a': 1}` and `MAP {['a'], [1]}` bind identically.
                // ⚠ The indexable is built here rather than unwrapped from ReadCell's DictionaryValue:
                // DictionaryValue exposes neither Keys nor TryGetValue publicly.
                if (paramsCol is MapArray map && !map.IsNull(row))
                {
                    var spread = new ArrowMap(map, row);
                    foreach (var key in spread.Keys)
                    {
                        if (spread.TryGetValue(key, out var v))
                        {
                            members.Add(new KeyValuePair<string, object?>(key, v));
                        }
                    }
                }
                break;
        }
        return members;
    }

    private static void CaptureJson(List<KeyValuePair<string, object?>> members, string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"fabricator: params is not valid JSON: {ex.Message}", ex);
        }
        if (root is not JsonObject obj)
        {
            throw new ArgumentException(
                "fabricator: params JSON must be an OBJECT — its members become the template's variables");
        }
        foreach (var kv in obj)
        {
            // ⚠ A JSON `null` member arrives as a NULL JsonNode rather than a JsonValue holding null, so the
            // converter never sees it — SetVariable is what maps it to Liquid's nil.
            members.Add(new KeyValuePair<string, object?>(kv.Key, kv.Value));
        }
    }

    /// <summary>Binds one template variable, mapping a missing value to Liquid's <c>nil</c>.</summary>
    /// <remarks>
    /// ⚠ A NULL is ordinary here, not an edge case: a STRUCT field can be NULL and JSON has <c>null</c>.
    /// <para>Fluid v3 annotates <c>SetValue(string, object)</c> as taking a NON-NULLABLE object, while its
    /// body maps null to <see cref="NilValue.Instance"/> anyway — so passing null would work and would warn.
    /// Routing null to the <c>FluidValue</c> overload OURSELVES keeps us off that implementation detail,
    /// which matters more than usual because the Fluid pin is a PRERELEASE: an internal null-handling branch
    /// is exactly the kind of thing that moves between betas, and it would move SILENTLY.</para>
    /// </remarks>
    internal static void SetVariable(TemplateContext ctx, string name, object? value)
    {
        if (value is null)
        {
            ctx.SetValue(name, NilValue.Instance);
        }
        else if (value is FluidValue already)
        {
            // ⚠ The FluidValue overload EXPLICITLY. The MAP path yields values that are already
            // FluidValues, and routing one through the object overload would hand it to FluidValue.Create
            // to be wrapped a second time. It reached ctx.SetValue directly before this method took over
            // that path, and this keeps it doing so.
            ctx.SetValue(name, already);
        }
        else
        {
            ctx.SetValue(name, value);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // JSON leaves
    // ------------------------------------------------------------------------------------------------

    /// <summary>One JSON leaf as a CLR value Fluid can compute with.</summary>
    /// <remarks>
    /// <b>⚠ BOTH ARMS ARE REAL and each has its own caller — MEASURED, not assumed.</b>
    /// <c>GetValue&lt;object&gt;()</c> hands back a <see cref="JsonElement"/> for a node PARSED FROM TEXT
    /// (every call through this plugin today) and an already-boxed CLR value for a node BUILT IN MEMORY
    /// (<c>String</c>/<c>Int32</c>/<c>Double</c>/<c>Boolean</c>), which is the shape a future in-process
    /// caller — a Fluid <c>query</c> function assembling a model, say — would produce.
    /// </remarks>
    internal static object? JsonLeaf(JsonValue jv)
    {
        var raw = jv.GetValue<object>();
        if (raw is not JsonElement e)
        {
            return raw is double d ? Number(d, jv.GetPath()) : raw;
        }
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                return e.GetString();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.Number:
                // ⚠⚠ THE LADDER, and every rung was measured. int64 FIRST because it is exact for every
                // integer a JSON document can hold in one: 9007199254740993 renders exactly here and comes
                // back 9007199254740990 through a double, since Fluid's decimal conversion keeps only 15
                // significant digits. decimal SECOND because it is exact for ordinary fractional values, and
                // it is what makes `19.99` stay `19.99`. double LAST, and only to reach Number()'s refusal —
                // a magnitude decimal cannot hold is one Fluid cannot represent at all.
                if (e.TryGetInt64(out var l))
                {
                    return l;
                }
                if (e.TryGetDecimal(out var m))
                {
                    // ⚠ decimal's RANGE is not its RESOLUTION: 1e-30 converts happily, and converts to ZERO.
                    return m == 0m && !IsJsonZero(e) ? Unrepresentable(e.GetRawText(), jv.GetPath()) : m;
                }
                return Number(e.GetDouble(), jv.GetPath());
            default:
                return null;
        }
    }

    private static bool IsJsonZero(JsonElement e) => e.TryGetDouble(out var d) && d == 0d;

    /// <summary>
    /// A CLR double on its way into a template, refused when Fluid's DECIMAL number model cannot hold it.
    /// </summary>
    /// <remarks>
    /// ⚠ Without this the failure is Fluid's own <c>OverflowException("Value was either too large or too
    /// small for a Decimal")</c> — raised mid-render, naming neither the parameter nor the value — or, worse,
    /// a SILENT zero. In <c>fluid_query</c> a silent zero is a wrong number spliced into a SQL statement, so
    /// refusing is the only safe direction.
    /// </remarks>
    private static object Number(double d, string where)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
        {
            return Unrepresentable(Render(d), where);
        }
        try
        {
            var m = Convert.ToDecimal(d); // exactly what Fluid does when it builds a NumberValue
            if (m == 0m && d != 0d)
            {
                return Unrepresentable(Render(d), where);
            }
            return m;
        }
        catch (OverflowException)
        {
            return Unrepresentable(Render(d), where);
        }
    }

    private static string Render(double d) =>
        d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private static object Unrepresentable(string rendered, string where) =>
        throw new ArgumentException(
            $"fabricator: the number {rendered} at '{where}' cannot be represented in a template — Fluid's "
            + "number model is DECIMAL (about ±7.9e28, with no resolution below ~1e-28), so this value would "
            + "otherwise render as 0 or fail mid-render. Pass it as a string if you only need to print it.");

    // ------------------------------------------------------------------------------------------------
    // Arrow cells
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// One Arrow value as something a template can render, compare and iterate — the SAME unboxing a query
    /// result row will need, which is why it lives here rather than inside the render function.
    /// </summary>
    /// <remarks>
    /// <b>⚠ A deliberate superset of <c>ArrowValueReader.ReadScalar</c>, not an accidental duplicate.</b>
    /// That type lives in <c>Fabricator.Bridge</c>, and a plugin references only
    /// <c>Fabricator.Abstractions</c> — the minimal, stable surface out-of-tree plugins are written against
    /// (<c>IScalarFunction</c>'s own doc says <c>ArrowValueReader</c> is available only "if a provider
    /// references the bridge"). Widening this plugin's reference to save the flat cases would make the
    /// in-tree example stop demonstrating the surface third-party authors actually have — and the NESTED
    /// cases have no counterpart there at all, because the bridge's reader exists for FILTER values, which
    /// are scalars by construction.
    /// </remarks>
    internal static object? ReadCell(IArrowArray array, int index)
    {
        if (array.IsNull(index))
        {
            return null;
        }
        switch (array)
        {
            case BooleanArray a: return a.GetValue(index);
            case Int8Array a: return a.GetValue(index);
            case Int16Array a: return a.GetValue(index);
            case Int32Array a: return a.GetValue(index);
            case Int64Array a: return a.GetValue(index);
            case UInt8Array a: return a.GetValue(index);
            case UInt16Array a: return a.GetValue(index);
            case UInt32Array a: return a.GetValue(index);
            case UInt64Array a: return a.GetValue(index);
            case FloatArray a: return Number(a.GetValue(index)!.Value, "a REAL value");
            case DoubleArray a: return Number(a.GetValue(index)!.Value, "a DOUBLE value");
            case Decimal128Array a: return a.GetValue(index);
            case Decimal256Array a: return a.GetValue(index);
            case StringArray a: return a.GetString(index);
            case LargeStringArray a: return a.GetString(index);
            case BinaryArray a: return Convert.ToHexString(a.GetBytes(index)).ToLowerInvariant();
            case Date32Array a: return AsUtc(a.GetDateTime(index));
            case Date64Array a: return AsUtc(a.GetDateTime(index));
            // /!\ NOT a local copy: the shared decision from Fabricator.Common, which the Bridge's own
            // filter marshaling uses too. See the note where the local duplicate used to be.
            case TimestampArray a: return ArrowValueReader.ReadTimestamp(a, index);
            // ⚠⚠ EAGER, not `new ArrowStruct(s, index)`. A lazy wrapper here reads its members at RENDER
            // time, and on a QUERY RESULT the RecordBatch is long disposed by then — Apache.Arrow nulls a
            // disposed batch's buffers, so `{{ r[0].s.a }}` died with a NullReferenceException out of
            // SharedMemoryHandle.get_Memory(). The eagerness `EagerStruct` gives a ROW has to reach all the
            // way down, or it only holds for the first level.
            case StructArray s:
                return new DictionaryValue(
                    new EagerStruct(((StructType)s.Data.DataType).Fields, s.Fields, index));
            // ⚠ MapArray BEFORE ListArray, and the compiler is what caught it: Apache.Arrow declares
            // `MapArray : ListArray`, so the list case matches a MAP first. Ordered the other way this is
            // not an error but a silently WRONG SHAPE - a MAP would arrive as a list of key/value structs,
            // which renders and iterates happily while every lookup by key fails.
            case MapArray m: return new DictionaryValue(new ArrowMap(m, index));
            case ListArray l: return ReadList(l.GetSlicedValues(index));
            case LargeListArray l: return ReadList(l.GetSlicedValues(index));
            case FixedSizeListArray l: return ReadList(l.GetSlicedValues(index));
            default:
                throw new NotSupportedException(
                    $"fabricator: unsupported params field type {array.Data.DataType.TypeId} — pass the "
                    + "params as a JSON string, or cast the field to a supported type.");
        }
    }

    private static object ReadList(IArrowArray? values)
    {
        var items = new List<object?>();
        if (values is not null)
        {
            for (int i = 0; i < values.Length; i++)
            {
                items.Add(ReadCell(values, i));
            }
        }
        return items;
    }

    /// <summary>A DATE's midnight, stamped UTC so nothing shifts it.</summary>
    /// <remarks>
    /// ⚠⚠ THE KIND IS THE WHOLE FIX, and it is not the knob it looks like. `Date32Array.GetDateTime` returns
    /// a DateTime with `Kind = Unspecified`, which Fluid resolves against the machine's LOCAL zone — so on a
    /// UTC+2 box `DATE '2026-09-01'` rendered as **`2026-08-31 22:00:00Z`**, the PREVIOUS DAY, and
    /// `fluid_query` would have spliced that wrong date into a statement.
    /// <para>⚠ `TemplateOptions.TimeZone` does NOT fix it — MEASURED, an Unspecified midnight renders
    /// identically under `TimeZoneInfo.Utc` and under the local zone, because the conversion happens where
    /// the DateTime is turned into a DateTimeOffset. Setting that option was the obvious one-line fix and it
    /// would have changed nothing.</para>
    /// <para>⚠ `DateOnly` is NOT the answer either: Fluid has no support for it, so it degrades to a
    /// <c>StringValue</c> rendering `09/01/2026` — culture-dependent, and it would reach the `sql` filter as
    /// a quoted STRING rather than a date. Both alternatives were measured before this one was chosen.</para>
    /// </remarks>
    private static object? AsUtc(DateTime? value) =>
        value is { } dt ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : null;

    // ⚠⚠ THE LOCAL ReadTimestamp IS GONE — it is `ArrowValueReader.ReadTimestamp` (Fabricator.Common) now,
    // and this is the one duplicate here that was worth removing rather than the one it looks like.
    //
    // It was a CHARACTER-FOR-CHARACTER copy of the bridge's, including the two explicit `(object)` casts,
    // and those casts are the fix for a defect that shipped for four months: without them C#'s conditional
    // operator unifies both branches to DateTimeOffset and the DateTime branch is converted straight back,
    // so a tz-less timestamp silently marshals as the wrong CLR type. A decision with that history must not
    // exist twice with two chances to drift.
    //
    // ⚠ Contrast ReadCell above, which is NOT substitutable and stays: it differs from
    // ArrowValueReader.ReadScalar on floats (the decimal ladder), blobs (hex, not byte[]), dates (the
    // DateTimeKind.Utc stamp) and every nested type — Fluid semantics, not generic unboxing. Replacing it
    // wholesale would revert three gated behaviours.

    // ------------------------------------------------------------------------------------------------
    // SQL quoting filters
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>{{ v | sql }}</c> — the value as a DuckDB SQL LITERAL, via the same <see cref="DuckSql.Literal"/>
    /// every in-tree SQL generator uses.
    /// </summary>
    /// <remarks>
    /// ⚠ An ALLOW-LIST, not an escaper: a value whose rendering is not provably safe is refused BY NAME
    /// rather than interpolated. That is the <c>fabricator_va_values</c> precedent, and it is why a list or
    /// an object reaching this filter is an error rather than a <c>ToString()</c>.
    /// <para>⚠⚠ EVERY temporal value renders as a <c>TIMESTAMPTZ</c> literal, whatever it started as:
    /// Fluid's date model is a single <c>DateTimeOffset</c>, so by the time the filter sees it a DATE, a
    /// TIMESTAMP and a TIMESTAMPTZ are indistinguishable. The INSTANT is preserved; the TYPE is not.</para>
    /// <para><b>⚠⚠ AND GETTING A DATE BACK OUT IS A SILENT TIMEZONE TRAP.</b> Anything that reads a
    /// TIMESTAMPTZ without NAMING a timezone reads it in the SESSION's timezone — <c>::DATE</c>,
    /// <c>::TIMESTAMP::DATE</c>, <c>date_trunc</c>, <c>strftime</c>, <c>extract</c> — so in a session west
    /// of UTC every one of them yields the PREVIOUS DAY, with no error. MEASURED under
    /// <c>America/New_York</c>. Two routes are safe and both are gated: name the zone
    /// (<c>({{ d | sql }} AT TIME ZONE 'UTC')::DATE</c>), or never build a TIMESTAMPTZ at all
    /// (<c>{{ d | date: '%Y-%m-%d' | sql }}::DATE</c>, which also needs no ICU). See
    /// docs/fluid-templating.md §7.4a.</para>
    /// </remarks>
    /// <param name="surface">What to NAME in the refusal — the <c>sql</c> filter by default, and
    /// <c>{% print sql_literal: true %}</c> when the print block borrows this. ⚠ Not cosmetic: a message
    /// naming a filter the author did not write sends them looking in the wrong place, which is the same
    /// defect the SELECT-only refusal had until the print block exposed it.</param>
    internal static string SqlLiteral(FluidValue input, TemplateContext ctx, string? surface = null)
    {
        surface ??= "the 'sql' filter";
        var clr = input is null or NilValue ? null : input.ToObjectValue(ctx);
        try
        {
            return DuckSql.Literal(clr);
        }
        catch (NotSupportedException)
        {
            throw new ArgumentException(
                $"fabricator: {surface} cannot render a {clr?.GetType().Name ?? "null"} as a SQL "
                + "literal — apply it to a number, a string, a boolean, a date/time or nil. For a list, loop "
                + "and quote each element.");
        }
    }
}

/// <summary>
/// One row of a STRUCT — and, in a later slice, one row of a query result — exposed to a template: members by
/// NAME, and, for the case where no name is available, by ORDINAL.
/// </summary>
/// <remarks>
/// ⚠ The ordinal path costs nothing and is not a second mechanism: MEASURED, Fluid resolves <c>r[0]</c> by
/// asking <see cref="TryGetValue"/> for the key <c>"0"</c>, so an int-parse fallback IS index access. A field
/// genuinely NAMED <c>0</c> therefore wins over the ordinal, which is the right precedence — the data's own
/// names come first.
/// </remarks>
/// <remarks>
/// ⚠⚠ THIS IS THE LAZY FORM AND IT MUST NOT OUTLIVE ITS <see cref="RecordBatch"/>. It reads a member only
/// when asked, so anything that survives the batch has to be materialised through
/// <see cref="EagerStruct"/> instead — which is why <see cref="FluidValueModel.ReadCell"/> never returns one
/// of these. It exists as the SOURCE EagerStruct materialises from, so the two cannot drift on the lookup
/// rule (name, then an int-parse ordinal, then FALSE so Fluid can answer `.size` itself).
/// </remarks>
internal sealed class ArrowStruct : IFluidIndexable
{
    private readonly IReadOnlyList<IArrowArray> _columns;
    private readonly IReadOnlyList<Field> _fields;
    private readonly int _row;

    internal ArrowStruct(StructArray array, int row)
        : this(((StructType)array.Data.DataType).Fields, array.Fields, row)
    {
    }

    /// <summary>
    /// The shared form, and what makes a QUERY RESULT row (slice 3) the SAME type with the SAME lookup rule
    /// as a STRUCT cell — which is what docs/fluid-templating.md §6 settled: one type, not two.
    /// </summary>
    /// <remarks>⚠ <paramref name="columns"/> is hoisted by the caller on the row-per-batch path, so a large
    /// result does not allocate one list per row.</remarks>
    internal ArrowStruct(IReadOnlyList<Field> fields, IReadOnlyList<IArrowArray> columns, int row)
    {
        _fields = fields;
        _columns = columns;
        _row = row;
    }

    public int Count => _fields.Count;

    public IEnumerable<string> Keys => _fields.Select(f => f.Name);

    public bool TryGetValue(string name, out FluidValue value)
    {
        int i = -1;
        for (int k = 0; k < _fields.Count; k++)
        {
            if (string.Equals(_fields[k].Name, name, StringComparison.Ordinal))
            {
                i = k;
                break;
            }
        }
        if (i < 0 && int.TryParse(name, out var ordinal) && ordinal >= 0 && ordinal < _fields.Count)
        {
            i = ordinal;
        }
        if (i < 0)
        {
            // ⚠ FALSE, not a nil value: returning false is what lets Fluid answer `.size` and friends itself.
            // A member the struct really has SHADOWS those, which is the right way round.
            value = NilValue.Instance;
            return false;
        }
        var cell = FluidValueModel.ReadCell(_columns[i], _row);
        value = cell is null ? NilValue.Instance : FluidValue.Create(cell, FluidValueModel.Options);
        return true;
    }
}

/// <summary>One row of a MAP exposed to a template, by key. A non-string key renders through its own form.</summary>
internal sealed class ArrowMap : IFluidIndexable
{
    private readonly List<KeyValuePair<string, object?>> _entries = new();

    internal ArrowMap(MapArray map, int row)
    {
        if (map.GetSlicedValues(row) is StructArray kv && kv.Fields.Count >= 2)
        {
            for (int i = 0; i < kv.Length; i++)
            {
                var key = FluidValueModel.ReadCell(kv.Fields[0], i);
                if (key is not null)
                {
                    _entries.Add(new KeyValuePair<string, object?>(
                        Convert.ToString(key, System.Globalization.CultureInfo.InvariantCulture)!,
                        FluidValueModel.ReadCell(kv.Fields[1], i)));
                }
            }
        }
    }

    public int Count => _entries.Count;

    public IEnumerable<string> Keys => _entries.Select(e => e.Key);

    public bool TryGetValue(string name, out FluidValue value)
    {
        foreach (var e in _entries)
        {
            if (string.Equals(e.Key, name, StringComparison.Ordinal))
            {
                value = e.Value is null ? NilValue.Instance : FluidValue.Create(e.Value, FluidValueModel.Options);
                return true;
            }
        }
        value = NilValue.Instance;
        return false;
    }
}
