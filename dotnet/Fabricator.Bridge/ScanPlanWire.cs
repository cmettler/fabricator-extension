// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Fabricator.Bridge;

/// <summary>
/// A <see cref="ScanPlan"/> on the wire (ABI v101 <c>table_file_plan</c>, documented in abi.h, read by
/// src/fabricator/fabricator_file_scan.cpp): the table's part — row count, stored names, virtual column rules — as a
/// JSON doc, and the parts as columns (<see cref="PartColumns"/>) that ScanPartsArrow turns into one Arrow batch.
/// BCL-only, so both decisions are tested offline.
/// </summary>
internal static class ScanPlanWire
{
    /// <summary>The table's part of the plan; a declined plan is <c>{"declined":"…"}</c>.</summary>
    public static string ToJson(ScanPlan plan)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            if (plan.DeclinedReason is { } reason)
            {
                json.WriteString("declined", reason);
                json.WriteEndObject();
                json.Flush();
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
            if (plan.RowCount is { } rows)
            {
                json.WriteNumber("row_count", rows);
            }
            if (plan.StoredColumns is { Count: > 0 } stored)
            {
                json.WritePropertyName("columns");
                WriteStoredColumns(json, stored);
            }
            if (plan.VirtualColumns is { Count: > 0 } virtualColumns)
            {
                json.WriteStartObject("virtual");
                foreach (var (name, column) in virtualColumns)
                {
                    json.WriteStartObject(name);
                    if (column.StoredColumn is { } storedColumn)
                    {
                        json.WriteString("stored", storedColumn);
                    }
                    if (column.AddRowNumber)
                    {
                        json.WriteBoolean("add_row_number", true);
                    }
                    json.WriteEndObject();
                }
                json.WriteEndObject();
            }
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// The parts as columns, one row per part: <c>path</c>, <c>stream</c> (read by the provider), <c>size</c>,
    /// <c>deleted</c>, then one <c>constant:&lt;column&gt;</c> per constant column and one <c>virtual:&lt;name&gt;</c>
    /// per virtual value. The constant and virtual columns are the TABLE's, so every part must state the same ones —
    /// a part without one would read as "NULL for this part", which is a different statement than "read it from the
    /// file".
    /// </summary>
    public static ScanPartColumns PartColumns(ScanPlan plan)
    {
        var parts = plan.Parts;
        var constantNames = parts.Count > 0 ? parts[0].Constants?.Keys.ToList() ?? new List<string>() : new List<string>();
        var virtualNames = parts.Count > 0 ? parts[0].VirtualValues?.Keys.ToList() ?? new List<string>() : new List<string>();
        var result = new ScanPartColumns(constantNames, virtualNames);
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            Same("constants", constantNames, part.Constants?.Keys, i);
            Same("virtual values", virtualNames, part.VirtualValues?.Keys, i);
            switch (part)
            {
                case FilePart file:
                    result.Paths.Add(file.Path);
                    result.Streams.Add(false);
                    result.Sizes.Add(file.Size);
                    result.Deleted.Add(file.DeletedRows is { Count: > 0 } deleted ? deleted : null);
                    break;
                case StreamPart stream:
                    result.Paths.Add(stream.Name);
                    result.Streams.Add(true);
                    result.Sizes.Add(null);
                    result.Deleted.Add(null);
                    break;
                default:
                    throw new NotSupportedException($"scan part {part.GetType().Name}");
            }
            for (int c = 0; c < constantNames.Count; c++)
            {
                result.ConstantValues[c].Add(part.Constants![constantNames[c]]);
            }
            for (int v = 0; v < virtualNames.Count; v++)
            {
                result.VirtualValues[v].Add(part.VirtualValues![virtualNames[v]]);
            }
        }
        return result;
    }

    private static void Same(string what, List<string> first, IEnumerable<string>? keys, int part)
    {
        var set = keys?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        if (set.Count != first.Count || !first.All(set.Contains))
        {
            throw new InvalidOperationException(
                $"scan plan: part {part} states other {what} than part 0 ({string.Join(", ", first)})");
        }
    }

    private static void WriteStoredColumns(Utf8JsonWriter json, IReadOnlyDictionary<string, StoredColumn> columns)
    {
        json.WriteStartObject();
        foreach (var (name, column) in columns)
        {
            json.WriteStartObject(name);
            if (column.StoredName is { } storedName)
            {
                json.WriteString("stored", storedName);
            }
            if (column.FieldId is { } fieldId)
            {
                json.WriteNumber("field_id", fieldId);
            }
            if (column.Members is { Count: > 0 } members)
            {
                json.WritePropertyName("members");
                WriteStoredColumns(json, members);
            }
            json.WriteEndObject();
        }
        json.WriteEndObject();
    }
}

/// <summary>The parts of a <see cref="ScanPlan"/> as columns (<see cref="ScanPlanWire.PartColumns"/>).</summary>
internal sealed class ScanPartColumns
{
    public ScanPartColumns(List<string> constantNames, List<string> virtualNames)
    {
        ConstantNames = constantNames;
        VirtualNames = virtualNames;
        ConstantValues = constantNames.Select(_ => new List<string?>()).ToList();
        VirtualValues = virtualNames.Select(_ => new List<long?>()).ToList();
    }

    public List<string> Paths { get; } = new();
    public List<bool> Streams { get; } = new();
    public List<long?> Sizes { get; } = new();
    public List<IReadOnlyList<long>?> Deleted { get; } = new();
    public List<string> ConstantNames { get; }
    public List<List<string?>> ConstantValues { get; }
    public List<string> VirtualNames { get; }
    public List<List<long?>> VirtualValues { get; }
}
