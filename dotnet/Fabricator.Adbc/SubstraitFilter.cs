// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Arrays;
using Apache.Arrow.Types;
using Fabricator.Bridge;

namespace Fabricator.Adbc;

/// <summary>
/// The functions a plan calls, declared in its extension header. Anchors start above those the plan already uses,
/// so declarations can be appended to a plan the host produced.
/// </summary>
internal sealed class SubstraitFunctions
{
    private const string ComparisonUrn = "extension:io.substrait:functions_comparison";
    private const string BooleanUrn = "extension:io.substrait:functions_boolean";

    private readonly uint _extensionBase;
    private readonly uint _functionBase;
    private readonly List<string> _extensions = new();
    private readonly List<(int Extension, string Name)> _functions = new();

    public SubstraitFunctions(uint extensionBase = 1, uint functionBase = 1)
    {
        _extensionBase = extensionBase;
        _functionBase = functionBase;
    }

    public uint Comparison(string name) => Reference(ComparisonUrn, name);

    public uint Boolean(string name) => Reference(BooleanUrn, name);

    private uint Reference(string urn, string name)
    {
        int extension = _extensions.IndexOf(urn);
        if (extension < 0)
        {
            extension = _extensions.Count;
            _extensions.Add(urn);
        }
        int function = _functions.IndexOf((extension, name));
        if (function < 0)
        {
            function = _functions.Count;
            _functions.Add((extension, name));
        }
        return _functionBase + (uint)function;
    }

    /// <summary>
    /// Writes the declarations into a plan: each extension both as a URN and as a legacy URI under the same anchor
    /// (a driver reads one or the other), then the function declarations.
    /// </summary>
    public void WriteTo(ProtoWriter plan)
    {
        for (int i = 0; i < _extensions.Count; i++)
        {
            uint anchor = _extensionBase + (uint)i;
            string file = _extensions[i].Substring(_extensions[i].LastIndexOf(':') + 1);
            plan.Message(1, new ProtoWriter().Varint(1, anchor)
                .String(2, "https://github.com/substrait-io/substrait/blob/main/extensions/" + file + ".yaml"));
            plan.Message(8, new ProtoWriter().Varint(1, anchor).String(2, _extensions[i]));
        }
        for (int i = 0; i < _functions.Count; i++)
        {
            uint anchor = _extensionBase + (uint)_functions[i].Extension;
            plan.Message(2, new ProtoWriter().Message(3, new ProtoWriter()
                .Varint(1, anchor)
                .Varint(2, _functionBase + (uint)i)
                .String(3, _functions[i].Name)
                .Varint(4, anchor)));
        }
    }
}

/// <summary>
/// Turns the host's pushed filter tree (<see cref="FilterNode"/> and its value batch) into Substrait expressions.
/// The host re-applies every filter, so the result may be a SUPERSET: a node that has no Substrait form here is
/// dropped (TRUE in its place, which only widens — the tree has no NOT), and a long IN list may become its range.
/// </summary>
internal sealed class SubstraitFilter
{
    private readonly Func<string, int?> _column;
    private readonly int _maxListOptions;
    private readonly RecordBatch? _values;
    private readonly SubstraitFunctions _functions;

    private SubstraitFilter(Func<string, int?> column, RecordBatch? values, SubstraitFunctions functions, int maxListOptions)
    {
        _column = column;
        _maxListOptions = maxListOptions;
        _values = values;
        _functions = functions;
    }

    /// <summary>
    /// The filter's top-level conjuncts as Substrait expressions over the columns <paramref name="column"/> resolves
    /// by name (field indexes into the relation the filter applies to). Empty when nothing has a Substrait form.
    /// </summary>
    /// <param name="maxListOptions">The longest IN list sent as a list; a longer one goes as its range (integers and
    /// temporals) or not at all.</param>
    public static List<ProtoWriter> Conjuncts(FilterNode filter, Func<string, int?> column, RecordBatch? values,
                                              SubstraitFunctions functions, int maxListOptions = 1000)
    {
        var builder = new SubstraitFilter(column, values, functions, maxListOptions);
        var result = new List<ProtoWriter>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Collect(FilterNode node)
        {
            if (node.Op == "and")
            {
                foreach (var child in node.Children ?? new List<FilterNode>())
                    Collect(child);
            }
            // the host can hand the same filter twice (a join's key list, once as a list and once read back from its
            // range filter): send it once
            else if (builder.Node(node) is { } expression && seen.Add(Convert.ToBase64String(expression.ToArray())))
            {
                result.Add(expression);
            }
        }
        Collect(filter);
        return result;
    }

    /// <summary>The conjunction of <paramref name="conjuncts"/>; null for none.</summary>
    public static ProtoWriter? And(IReadOnlyList<ProtoWriter> conjuncts, SubstraitFunctions functions) =>
        conjuncts.Count switch
        {
            0 => null,
            1 => conjuncts[0],
            _ => Call(functions.Boolean("and:bool"), conjuncts),
        };

    private ProtoWriter? Node(FilterNode node)
    {
        switch (node.Op)
        {
            case "and":
            {
                var children = (node.Children ?? new List<FilterNode>()).Select(Node).OfType<ProtoWriter>().ToList();
                return And(children, _functions);
            }
            case "or":
            {
                var children = (node.Children ?? new List<FilterNode>()).Select(Node).ToList();
                // a dropped branch is TRUE, which makes the whole disjunction TRUE
                if (children.Count == 0 || children.Any(c => c is null))
                    return null;
                return children.Count == 1 ? children[0] : Call(_functions.Boolean("or:bool"), children!);
            }
            case "is_null":
                return Column(node) is { } isNull ? Call(_functions.Comparison("is_null:any"), isNull) : null;
            case "is_not_null":
                return Column(node) is { } notNull ? Call(_functions.Comparison("is_not_null:any"), notNull) : null;
            case "compare":
                return Compare(node);
            case "in":
                return In(node);
            default:
                return null;
        }
    }

    private ProtoWriter? Compare(FilterNode node)
    {
        if (Column(node) is not { } column || node.Val is not { } index)
            return null;
        var value = Literal(index);
        if (value is null)
        {
            // a NULL constant: IS [NOT] DISTINCT FROM is a null test; every other comparison is never TRUE, and TRUE
            // in its place is the superset
            return node.Cmp switch
            {
                "is_not_distinct" => Call(_functions.Comparison("is_null:any"), column),
                "is_distinct" => Call(_functions.Comparison("is_not_null:any"), column),
                _ => null,
            };
        }
        string? name = node.Cmp switch
        {
            "=" => "equal",
            "<>" => "not_equal",
            "<" => "lt",
            "<=" => "lte",
            ">" => "gt",
            ">=" => "gte",
            "is_distinct" => "is_distinct_from",
            "is_not_distinct" => "is_not_distinct_from",
            _ => null,
        };
        return name is null ? null : Call(_functions.Comparison(name + ":any_any"), column, value);
    }

    private ProtoWriter? In(FilterNode node)
    {
        if (Column(node) is not { } column || node.Vals is not { Count: > 0 } indexes)
            return null;
        if (indexes.Count > _maxListOptions)
            return Range(column, indexes);
        // a NULL option never makes IN TRUE, so leaving it out keeps every row IN keeps
        var options = indexes.Select(Literal).OfType<ProtoWriter>().ToList();
        if (options.Count == 0)
            return null;
        var list = new ProtoWriter().Message(1, column);
        foreach (var option in options)
            list.Message(2, option);
        return new ProtoWriter().Message(8, list); // Expression.singular_or_list
    }

    /// <summary>A long list of integers or temporals as <c>min &lt;= column &lt;= max</c>; null for other types.</summary>
    private ProtoWriter? Range(ProtoWriter column, IReadOnlyList<int> indexes)
    {
        int? min = null, max = null;
        long minValue = 0, maxValue = 0;
        foreach (int index in indexes)
        {
            if (Ordinal(index) is not { } value)
            {
                if (Value(index) is { } array && !array.IsNull(0))
                    return null; // not an integer or temporal value
                continue;
            }
            if (min is null || value < minValue) { min = index; minValue = value; }
            if (max is null || value > maxValue) { max = index; maxValue = value; }
        }
        if (min is null || max is null || Literal(min.Value) is not { } low || Literal(max.Value) is not { } high)
            return null;
        return Call(_functions.Boolean("and:bool"),
            Call(_functions.Comparison("gte:any_any"), column, low),
            Call(_functions.Comparison("lte:any_any"), column, high));
    }

    private long? Ordinal(int index) => Value(index) switch
    {
        { } a when a.IsNull(0) => null,
        Int8Array a => a.GetValue(0),
        Int16Array a => a.GetValue(0),
        Int32Array a => a.GetValue(0),
        Int64Array a => a.GetValue(0),
        UInt8Array a => a.GetValue(0),
        UInt16Array a => a.GetValue(0),
        UInt32Array a => a.GetValue(0),
        Date32Array a => a.GetValue(0),
        TimestampArray a => a.GetValue(0),
        _ => null,
    };

    private ProtoWriter? Column(FilterNode node)
    {
        if (node.Col is not { } name || _column(name) is not { } index)
            return null; // a struct member path, or a column the relation does not have
        var segment = new ProtoWriter().Message(2, new ProtoWriter().Varint(1, (ulong)index)); // struct_field
        var reference = new ProtoWriter().Message(1, segment).Message(4, new ProtoWriter()); // direct, root
        return new ProtoWriter().Message(2, reference); // Expression.selection
    }

    private IArrowArray? Value(int index)
    {
        if (_values is null || index < 0 || index >= _values.ColumnCount || _values.Length < 1)
            return null;
        var array = _values.Column(index);
        return array is ExtensionArray extension ? extension.Storage : array;
    }

    /// <summary>The constant as a Substrait literal expression; null for a NULL or a type with no literal here.</summary>
    private ProtoWriter? Literal(int index)
    {
        var array = Value(index);
        if (array is null || array.IsNull(0))
            return null;
        var literal = LiteralOf(array);
        return literal is null ? null : new ProtoWriter().Message(1, literal); // Expression.literal
    }

    private static ProtoWriter? LiteralOf(IArrowArray array)
    {
        var w = new ProtoWriter();
        switch (array)
        {
            case BooleanArray a: return w.Varint(1, a.GetValue(0) == true ? 1UL : 0UL);
            case Int8Array a: return w.Signed(2, a.GetValue(0)!.Value);
            case Int16Array a: return w.Signed(3, a.GetValue(0)!.Value);
            case Int32Array a: return w.Signed(5, a.GetValue(0)!.Value);
            case Int64Array a: return w.Signed(7, a.GetValue(0)!.Value);
            // the scan declares an unsigned column as the next wider signed type (SubstraitReadPlan)
            case UInt8Array a: return w.Signed(3, a.GetValue(0)!.Value);
            case UInt16Array a: return w.Signed(5, a.GetValue(0)!.Value);
            case UInt32Array a: return w.Signed(7, a.GetValue(0)!.Value);
            case UInt64Array a: return a.GetValue(0)!.Value <= long.MaxValue ? w.Signed(7, (long)a.GetValue(0)!.Value) : null;
            case FloatArray a: return w.Fixed32(10, BitConverter.SingleToUInt32Bits(a.GetValue(0)!.Value));
            case DoubleArray a: return w.Fixed64(11, BitConverter.DoubleToUInt64Bits(a.GetValue(0)!.Value));
            case StringArray a: return w.String(12, a.GetString(0));
            case LargeStringArray a: return w.String(12, a.GetString(0));
            case StringViewArray a: return w.String(12, a.GetString(0));
            case BinaryArray a: return w.Bytes(13, a.GetBytes(0));
            case LargeBinaryArray a: return w.Bytes(13, a.GetBytes(0));
            case BinaryViewArray a: return w.Bytes(13, a.GetBytes(0));
            case Date32Array a: return w.Signed(16, a.GetValue(0)!.Value);
            case Date64Array a: return w.Signed(16, Math.DivRem(a.GetValue(0)!.Value, 86_400_000L, out _));
            case Decimal32Array a: return Decimal(w, a.GetBytes(0), ((Decimal32Type)a.Data.DataType).Precision, ((Decimal32Type)a.Data.DataType).Scale);
            case Decimal64Array a: return Decimal(w, a.GetBytes(0), ((Decimal64Type)a.Data.DataType).Precision, ((Decimal64Type)a.Data.DataType).Scale);
            case Decimal128Array a: return Decimal(w, a.GetBytes(0), ((Decimal128Type)a.Data.DataType).Precision, ((Decimal128Type)a.Data.DataType).Scale);
            case Decimal256Array a: return Decimal(w, a.GetBytes(0), ((Decimal256Type)a.Data.DataType).Precision, ((Decimal256Type)a.Data.DataType).Scale);
            case TimestampArray a:
            {
                var type = (TimestampType)a.Data.DataType;
                var value = new ProtoWriter().Varint(1, Precision(type.Unit)).Signed(2, a.GetValue(0)!.Value);
                return w.Message(type.Timezone is null ? 34 : 35, value);
            }
            case Time32Array a:
                return w.Message(37, new ProtoWriter().Varint(1, Precision(((Time32Type)a.Data.DataType).Unit))
                    .Signed(2, a.GetValue(0)!.Value));
            case Time64Array a:
                return w.Message(37, new ProtoWriter().Varint(1, Precision(((Time64Type)a.Data.DataType).Unit))
                    .Signed(2, a.GetValue(0)!.Value));
            case FixedSizeBinaryArray a: return w.Bytes(23, a.GetBytes(0));
            default: return null;
        }
    }

    /// <summary>A decimal literal: 16 bytes of little-endian two's complement, its precision and scale.</summary>
    private static ProtoWriter? Decimal(ProtoWriter w, ReadOnlySpan<byte> value, int precision, int scale)
    {
        var bytes = new byte[16];
        byte sign = (value[value.Length - 1] & 0x80) != 0 ? (byte)0xff : (byte)0;
        for (int i = 0; i < 16; i++)
            bytes[i] = i < value.Length ? value[i] : sign;
        for (int i = 16; i < value.Length; i++)
        {
            if (value[i] != sign)
                return null; // a 256-bit value beyond 128 bits
        }
        if (precision > 38)
            return null;
        return w.Message(24, new ProtoWriter().Bytes(1, bytes).Varint(2, (ulong)precision).Varint(3, (ulong)scale));
    }

    private static ulong Precision(TimeUnit unit) => unit switch
    {
        TimeUnit.Second => 0,
        TimeUnit.Millisecond => 3,
        TimeUnit.Microsecond => 6,
        _ => 9,
    };

    private static ProtoWriter Call(uint function, params ProtoWriter[] args) => Call(function, (IReadOnlyList<ProtoWriter>)args);

    private static ProtoWriter Call(uint function, IReadOnlyList<ProtoWriter> args)
    {
        var call = new ProtoWriter().Varint(1, function);
        foreach (var arg in args)
            call.Message(4, new ProtoWriter().Message(3, arg)); // FunctionArgument.value
        call.Message(3, new ProtoWriter().Message(1, new ProtoWriter().Varint(2, 1))); // output_type: nullable boolean
        return new ProtoWriter().Message(3, call); // Expression.scalar_function
    }
}
