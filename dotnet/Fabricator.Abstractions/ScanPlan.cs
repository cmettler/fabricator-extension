// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;

namespace Fabricator.Bridge;

/// <summary>
/// How a table is read as a set of PARTS (<see cref="ITableBinding.PlanScan"/>, ABI v101): each part says how its
/// rows are read — a <see cref="FilePart"/> by DuckDB's own reader, a <see cref="StreamPart"/> by the provider — and
/// how they map onto the table. The host runs every part in one parallel scan and applies the mapping the same way
/// for both: deleted rows, per-part constants, stored column names, NULL for a column a part lacks, and virtual
/// columns.
/// </summary>
/// <remarks>
/// <para>⚠ The plan REPLACES the provider's scan, so it must describe exactly what <see cref="ITableBinding.Scan"/>
/// would return in the ambient transaction — the same snapshot, nothing pending. When it cannot, the provider answers
/// <see cref="Decline"/> and the host scans as before.</para>
/// <para>Nothing here is format-specific: a Delta log, an Iceberg manifest, a DuckLake catalog or a Lance dataset
/// each only say which parts exist. A format DuckDB cannot read itself is a <see cref="StreamPart"/>.</para>
/// <para>BCL-only on purpose, so the plan and its wire form are tested offline (Fabricator.Bridge.Tests).</para>
/// </remarks>
public sealed record ScanPlan
{
    /// <summary>The parts, in order: the scan's row order across parts (and each part's row order within it).</summary>
    public IReadOnlyList<ScanPart> Parts { get; init; } = Array.Empty<ScanPart>();

    /// <summary>The live rows across all parts, for the optimizer; null = unknown.</summary>
    public long? RowCount { get; init; }

    /// <summary>Columns stored under another name than the table's (Delta column mapping), by table column name.
    /// A column not listed is stored under its own name.</summary>
    public IReadOnlyDictionary<string, StoredColumn>? StoredColumns { get; init; }

    /// <summary>How the virtual columns the table declares (e.g. <c>rowid</c>) are computed, by name.</summary>
    public IReadOnlyDictionary<string, ScanVirtualColumn>? VirtualColumns { get; init; }

    /// <summary>Why the table cannot be read this way; set only by <see cref="Decline"/>.</summary>
    public string? DeclinedReason { get; init; }

    /// <summary>The answer when the table cannot be read as parts: the host scans through the provider instead.</summary>
    public static ScanPlan Decline(string reason) => new() { DeclinedReason = reason };
}

/// <summary>Where a column (and, for a struct, each member) is stored in the parts.</summary>
/// <param name="StoredName">The name in the parts; null = the column's own name.</param>
/// <param name="Members">A struct's members stored under other names, by member name.</param>
public sealed record StoredColumn(string? StoredName, IReadOnlyDictionary<string, StoredColumn>? Members = null);

/// <summary>
/// How a virtual column is computed for a row: the part's own column <paramref name="StoredColumn"/> where the part
/// has one and the row's value there is not NULL, else the part's <see cref="ScanPart.VirtualValues"/> entry — plus
/// the row's position in the part when <paramref name="AddRowNumber"/> — else NULL.
/// </summary>
public sealed record ScanVirtualColumn(string? StoredColumn = null, bool AddRowNumber = false);

/// <summary>One part of a <see cref="ScanPlan"/>.</summary>
public abstract record ScanPart
{
    /// <summary>Columns the part does not store but has one value for (a partition value), as text the host casts
    /// to the column type; a null value = NULL for the whole part.</summary>
    public IReadOnlyDictionary<string, string?>? Constants { get; init; }

    /// <summary>This part's value for each virtual column (see <see cref="ScanVirtualColumn"/>); null = none.</summary>
    public IReadOnlyDictionary<string, long?>? VirtualValues { get; init; }
}

/// <summary>A part DuckDB reads itself: a parquet data file. Fastest by far — no row crosses into the provider.</summary>
/// <param name="Path">The file, as DuckDB's file system reaches it.</param>
public sealed record FilePart(string Path) : ScanPart
{
    /// <summary>The file's size in bytes when known (saves a round trip on an object store).</summary>
    public long? Size { get; init; }

    /// <summary>The positions of the file's deleted rows, ascending; null or empty = none.</summary>
    public IReadOnlyList<long>? DeletedRows { get; init; }
}

/// <summary>
/// A part the PROVIDER reads (<see cref="ITableBinding.ReadPart"/>): a format DuckDB has no reader for. The host asks
/// for it by <paramref name="Name"/> with the columns the scan needs, from one thread; deleted rows are the provider's
/// to remove.
/// </summary>
/// <param name="Name">Opaque to the host: everything the provider needs to read the part again, statelessly (it
/// may be asked again when a plan is executed again).</param>
public sealed record StreamPart(string Name) : ScanPart;
