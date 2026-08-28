// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Generic;
using Apache.Arrow;

namespace Fabricator.Bridge;

/// <summary>
/// Resolves a scan's pushed projection against a binding's declared output schema, for the bindings that
/// answer <c>SupportsProjectionPushdown</c> = true.
/// </summary>
/// <remarks>
/// <para><b>ONE resolver, used twice on purpose.</b> <see cref="TableFunctionBindingAdapter"/> declares the stream's
/// schema with it and the binding builds its read with it, so the batches and the declared schema cannot
/// disagree — and a disagreement is not an error here, it is <c>arrow_ingest</c> reading past the end
/// (SIGSEGV). Two independent derivations of "which columns, in what order" would be one edit away from
/// that at all times.</para>
/// <para>⚠ THE ORDER IS THE DECLARED SCHEMA'S, NOT THE REQUEST'S, AND THAT IS REQUIRED RATHER THAN TIDY —
/// MEASURED. DuckDB maps these results by name (<see cref="ITableFunctionSession.MapResultByName"/>), so the request's
/// order does not bind the result; but <b>engineered-wood emits in SCHEMA order whatever order it is asked
/// in</b>. Ordering by the request therefore makes the declaration disagree with the batches for any query
/// that asks out of schema order — and the failure is not a wrong answer, it is
/// <b>SIGSEGV</b> (arrow_ingest reads a VARCHAR where the batch holds an INT). Mutation-tested: switching
/// this loop to the request's order crashes at exactly the reverse-order assertion in
/// <c>verify_delta_native_scan</c>, after 67 others pass. <c>DeltaNativeReader</c> emits in the order it is
/// handed and would have agreed either way, so only the C# reader forces the rule — one reader's convention
/// deciding the contract for both.</para>
/// <para>⚠ RETURNS NULL RATHER THAN GUESSING in three cases, each of which must degrade to "no projection,
/// declare everything": nothing was pushed; the pushed list is EMPTY (the <c>COUNT(*)</c> shape — Apache.Arrow
/// 23 cannot represent a zero-field schema across the C interface in either direction, so declaring one is
/// not an option); or a requested name is not in the schema, which means the two sides disagree about what
/// this function returns and the safe reading is to send everything and let DuckDB sort it out.</para>
/// </remarks>
internal static class ProjectionPlan
{
    /// <summary>The projected fields in DECLARED order, or null when the full schema must be used.</summary>
    public static IReadOnlyList<Field>? Resolve(Schema full, IReadOnlyList<string>? requested)
    {
        if (requested is not { Count: > 0 })
        {
            return null;
        }
        var wanted = new HashSet<string>(requested, System.StringComparer.Ordinal);
        var fields = new List<Field>(requested.Count);
        foreach (var f in full.FieldsList)
        {
            if (wanted.Remove(f.Name))
            {
                fields.Add(f);
            }
        }
        // Anything left unmatched means the scan asked for a column this binding does not declare.
        return wanted.Count == 0 && fields.Count > 0 ? fields : null;
    }

    /// <summary>The schema to DECLARE for this scan — the projected subset, else <paramref name="full"/>.</summary>
    public static Schema Schema(Schema full, IReadOnlyList<string>? requested)
        => Resolve(full, requested) is { } fields ? new Schema(fields, full.Metadata) : full;

    /// <summary>The column NAMES to read, in the same order — null when everything must be read.</summary>
    public static IReadOnlyList<string>? Columns(Schema full, IReadOnlyList<string>? requested)
    {
        if (Resolve(full, requested) is not { } fields)
        {
            return null;
        }
        var names = new List<string>(fields.Count);
        foreach (var f in fields)
        {
            names.Add(f.Name);
        }
        return names;
    }
}
