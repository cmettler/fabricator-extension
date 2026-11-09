// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Fabricator.Bridge;

/// <summary>
/// A <see cref="ScanPlan"/> on the wire: the JSON doc of ABI v101 <c>table_file_plan</c> (documented in abi.h, read by
/// src/fabricator/fabricator_file_scan.cpp). BCL-only, so the format is tested offline.
/// </summary>
internal static class ScanPlanWire
{
    /// <summary>The plan as the host reads it; a declined plan is <c>{"declined":"…"}</c>.</summary>
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
            json.WriteStartArray("files");
            foreach (var part in plan.Parts)
            {
                json.WriteStartObject();
                switch (part)
                {
                    case FilePart file:
                        json.WriteString("path", file.Path);
                        if (file.Size is { } size)
                        {
                            json.WriteNumber("size", size);
                        }
                        if (file.DeletedRows is { Count: > 0 } deleted)
                        {
                            json.WriteStartArray("dv");
                            foreach (var position in deleted)
                            {
                                json.WriteNumberValue(position);
                            }
                            json.WriteEndArray();
                        }
                        break;
                    case StreamPart stream:
                        // the name doubles as the part's display path (errors name it)
                        json.WriteString("path", stream.Name);
                        json.WriteString("stream", stream.Name);
                        break;
                    default:
                        throw new System.NotSupportedException($"scan part {part.GetType().Name}");
                }
                if (part.Constants is { Count: > 0 } constants)
                {
                    json.WriteStartObject("constants");
                    foreach (var (column, value) in constants)
                    {
                        if (value is null)
                        {
                            json.WriteNull(column);
                        }
                        else
                        {
                            json.WriteString(column, value);
                        }
                    }
                    json.WriteEndObject();
                }
                if (part.VirtualValues is { Count: > 0 } values)
                {
                    json.WriteStartObject("virtual");
                    foreach (var (column, value) in values)
                    {
                        if (value is { } v)
                        {
                            json.WriteNumber(column, v);
                        }
                        else
                        {
                            json.WriteNull(column);
                        }
                    }
                    json.WriteEndObject();
                }
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
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
