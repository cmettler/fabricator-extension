// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// Normalises the ABI v88 <c>params</c> bag — ONE row, ONE column named <c>params</c> holding the
/// <c>params :=</c> argument exactly as the caller wrote it — into the shape the provider contract takes:
/// a one-row <see cref="RecordBatch"/> with one column PER PARAMETER, named.
/// </summary>
/// <remarks>
/// <para>⚠ <b>The STRUCT/JSON branch lives HERE and nowhere else.</b> Both spellings are accepted because
/// the argument is declared <c>ANY</c> (a fixed VARCHAR would force DuckDB to stringify a struct before we
/// ever saw it — the reason recorded at <c>daxeval</c>'s own declaration). Doing the branch once, host-side,
/// is what keeps <c>fabricator_query</c> and <c>fabricator_exec</c> from drifting on what a bag means, and
/// what keeps every provider free of it.</para>
/// <para>⚠ <b>The two spellings are not equally faithful, and the asymmetry is inherent.</b> A STRUCT's
/// children ARE the output columns — no conversion at all, so precision, scale, time zone and unit survive
/// exactly. JSON has four scalar kinds, so a JSON bag can only produce BIGINT / DOUBLE / VARCHAR / BOOLEAN,
/// and a caller who needs a DECIMAL or a typed temporal must pass a STRUCT. That is a property of JSON, not
/// a shortcut here, but it is the reason to prefer the STRUCT form.</para>
/// <para>This half is deliberately free of pointers and file I/O so it is testable offline: every refusal
/// below is reachable from a unit test, where reaching it through SQL would cost a provider round trip.</para>
/// </remarks>
public static class ProviderParameters
{
    /// <summary>The single column the ABI wire carries.</summary>
    public const string BagColumn = "params";

    /// <summary>
    /// Normalises the wire batch. Returns <c>null</c> for "no parameters" — which an absent argument, an
    /// explicit NULL and an empty bag all mean, so a provider never has to tell them apart.
    /// </summary>
    /// <exception cref="ArgumentException">The bag is malformed: not an object, nested, or duplicated.</exception>
    public static RecordBatch? Normalize(RecordBatch? wire)
    {
        if (wire is null)
        {
            return null;
        }
        var fields = wire.Schema.FieldsList;
        for (int i = 0; i < fields.Count; i++)
        {
            if (!string.Equals(fields[i].Name, BagColumn, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var column = wire.Column(i);
            if (column.Length == 0 || column.IsNull(0))
            {
                return null;
            }
            if (column is StructArray sa)
            {
                return FromStruct(sa);
            }
            // ⚠ Refuse ANYTHING that is not a struct or a string, HERE, rather than letting it fall into the
            // JSON path. MEASURED before this guard existed: a MAP or a LIST died inside
            // ArrowValueReader.ReadScalar as "unsupported filter value type Map" — a message naming a
            // subsystem the caller never touched — and a TIMESTAMP was stringified and then reported as
            // invalid JSON. Both are the error-names-the-wrong-thing class. A MAP is additionally a poor fit
            // for a bag whatever we did here: its values are ONE type, so a heterogeneous bag cannot even be
            // written as one.
            if (column is not StringArray and not LargeStringArray and not StringViewArray)
            {
                throw new ArgumentException(
                    $"fabricator: 'params' is a {column.Data.DataType.Name}; it must be a DuckDB STRUCT "
                    + "(params := {'a': 1}) or a JSON object string (params := '{\"a\": 1}')");
            }
            return FromJson(ArrowValueReader.ReadScalar(column, 0)?.ToString());
        }
        return null;
    }

    // A STRUCT's children ARE the parameters: the field list and the child arrays transfer verbatim, so
    // nothing is converted and no type can be lost. The struct is known non-null at row 0 by the caller.
    private static RecordBatch? FromStruct(StructArray sa)
    {
        var structType = (StructType)sa.Data.DataType;
        if (structType.Fields.Count == 0)
        {
            return null;
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fields = new Field[structType.Fields.Count];
        var arrays = new IArrowArray[structType.Fields.Count];
        for (int f = 0; f < structType.Fields.Count; f++)
        {
            var name = structType.Fields[f].Name;
            RefuseDuplicate(names, name);
            // ⚠ Every child is declared NULLABLE regardless of what the struct said. A parameter's value is
            // the caller's business and a NULL is a legitimate one; claiming otherwise would be a claim we
            // cannot support about a value we did not produce.
            fields[f] = new Field(name, structType.Fields[f].DataType, nullable: true);
            arrays[f] = sa.Fields[f];
        }
        return new RecordBatch(new Schema(fields, metadata: null), arrays, length: 1);
    }

    private static RecordBatch? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json!);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"fabricator: 'params' is not valid JSON: {ex.Message}", ex);
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "fabricator: 'params' must be a DuckDB STRUCT or a JSON object, e.g. params := {'a': 1} "
                    + "or params := '{\"a\": 1}'");
            }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fields = new List<Field>();
            var arrays = new List<IArrowArray>();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                RefuseDuplicate(names, property.Name);
                var (type, array) = FromJsonScalar(property.Name, property.Value);
                fields.Add(new Field(property.Name, type, nullable: true));
                arrays.Add(array);
            }
            if (fields.Count == 0)
            {
                return null;
            }
            return new RecordBatch(new Schema(fields, metadata: null), arrays, length: 1);
        }
    }

    private static (IArrowType, IArrowArray) FromJsonScalar(string name, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                // ⚠ int64 FIRST, then double. C# unifies the branches of a conditional to the wider type, so
                // writing this as `TryGetInt64(out l) ? l : GetDouble()` silently routes EVERY integer
                // through a double and loses exactness above 2^53 — a defect this repo shipped once, in
                // JsonToClr, and one that is invisible for every integer a test happens to use.
                if (value.TryGetInt64(out long l))
                {
                    return (Int64Type.Default, new Int64Array.Builder().Append(l).Build());
                }
                return (DoubleType.Default, new DoubleArray.Builder().Append(value.GetDouble()).Build());
            case JsonValueKind.String:
                return (StringType.Default, new StringArray.Builder().Append(value.GetString()).Build());
            case JsonValueKind.True:
            case JsonValueKind.False:
                return (BooleanType.Default, new BooleanArray.Builder().Append(value.GetBoolean()).Build());
            case JsonValueKind.Null:
                // A JSON null carries no type. VARCHAR is the choice every other NULL-typing decision here
                // makes; a caller who needs a typed NULL passes a STRUCT, where the type is explicit.
                return (StringType.Default, new StringArray.Builder().AppendNull().Build());
            default:
                // Refuse rather than stringify: a nested object rendered as its JSON text is a value the
                // caller never wrote, and it would reach the provider as a plausible-looking string.
                throw new ArgumentException(
                    $"fabricator: 'params' value for '{name}' is a JSON "
                    + $"{value.ValueKind.ToString().ToLowerInvariant()}; a parameter must be a scalar "
                    + "(number, string, boolean or null)");
        }
    }

    private static void RefuseDuplicate(HashSet<string> seen, string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("fabricator: 'params' contains a parameter with an empty name");
        }
        if (!seen.Add(name))
        {
            // Collapsing to one would run the statement with a value the caller did not choose; SQL Server
            // would refuse a duplicate parameter anyway, one layer later and with a worse message.
            throw new ArgumentException($"fabricator: 'params' declares '{name}' more than once");
        }
    }
}
