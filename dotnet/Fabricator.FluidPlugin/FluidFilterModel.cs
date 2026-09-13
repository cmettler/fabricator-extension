// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Ipc;
using Fabricator.Bridge;

namespace Fabricator.FluidPlugin;

/// <summary>
/// Turns the host's pushed predicate into the two things a template can act on: the top-level CONJUNCTS as
/// typed values, and the same list as a DuckDB variable.
/// </summary>
/// <remarks>
/// <para>
/// ⚠⚠ <b>ONLY THE TOP-LEVEL <c>AND</c> CONJUNCTS ARE FLATTENED, AND THAT IS A CORRECTNESS RULE RATHER THAN A
/// SIMPLIFICATION.</b> A conjunct of the whole predicate is true of every row the query wants, so a template
/// may narrow by it. A branch of an <c>OR</c> is NOT: flattening <c>a = 1 OR b = 2</c> into two entries and
/// letting a template "restrict to the partition a = 1" DROPS every row where <c>b = 2</c> — a silent wrong
/// answer, and one the host cannot catch, because re-applying the predicate cannot bring back rows the
/// template never read. So a disjunction contributes NOTHING to <c>filters</c>; it is still carried whole in
/// <c>filter_sql</c>, where splicing it is safe.
/// </para>
/// <para>
/// ⚠ A node whose constant could not be read is DROPPED for the same reason it is dropped everywhere else in
/// this codebase: a missing constant is not a predicate against NULL (which matches no row), and dropping a
/// conjunct only widens what the template reads, which DuckDB then re-applies.
/// </para>
/// <para>
/// ⚠⚠ <b>THE VALUES GO THROUGH <see cref="FluidValueModel.ReadCell"/>, NOT
/// <c>ArrowValueReader.ReadScalar</c></b> — deliberately, although the Bridge's own filter readers use the
/// latter. <c>ReadCell</c> is this plugin's documented SUPERSET and it is what every OTHER Fluid value in a
/// template comes from: it stamps a date <c>DateTimeKind.Utc</c> (without which a DATE renders as the
/// PREVIOUS DAY east of UTC), renders a BLOB as hex rather than as concatenated decimal bytes, and takes the
/// decimal ladder for floats. A filter value read the other way would render differently from an
/// identically-typed <c>params</c> value in the same template, for no reason the author could see.
/// </para>
/// </remarks>
internal static class FluidFilterModel
{
    /// <summary>One top-level conjunct of the pushed predicate.</summary>
    /// <param name="Column">The column, dotted for a struct member (<c>s.a</c>).</param>
    /// <param name="Op">One of <c>= != &lt; &lt;= &gt; &gt;=</c>, <c>in</c>, <c>is_null</c>, <c>is_not_null</c>.</param>
    /// <param name="Value">The typed value; a LIST for <c>in</c>; null for the two null tests.</param>
    /// <param name="ValueIndexes">Which staged constants it used — how the SQL variable renders the value.</param>
    internal sealed record Conjunct(string Column, string Op, object? Value, IReadOnlyList<int> ValueIndexes);

    /// <summary>The conjuncts plus the staged constants, which the DuckDB variable renders through.</summary>
    internal sealed record Pushed(IReadOnlyList<Conjunct> Conjuncts, RecordBatch? Values);

    private static readonly Pushed None = new(System.Array.Empty<Conjunct>(), null);

    /// <summary>
    /// Reads the pushed predicate. ⚠ CONSUMES AND DISPOSES <paramref name="filterValues"/>, so it must be
    /// called from the EAGER part of a binding's <c>Execute</c> — an async iterator that is never pulled
    /// would otherwise leak the host's stream.
    /// </summary>
    internal static Pushed Read(FilterNode? tree, IArrowArrayStream? filterValues)
    {
        if (filterValues is null)
        {
            return None;
        }
        RecordBatch? values;
        using (filterValues)
        {
            var batch = filterValues.ReadNextRecordBatchAsync().GetAwaiter().GetResult();
            // ⚠⚠ COPIED INSIDE THE `using`. The batch's buffers belong to the imported stream, so keeping it
            // past disposal is the use-after-free this codebase records as invisible on Windows and Linux.
            // The copy is ours outright and outlives the call, which is what lets the SQL variable below be
            // rendered by DuckDB from the TYPED constants rather than by a second renderer of ours.
            values = batch is null ? null : Copy(batch);
        }
        if (tree is null || values is null)
        {
            return new Pushed(System.Array.Empty<Conjunct>(), values);
        }
        var typed = new object?[values.ColumnCount];
        for (int i = 0; i < values.ColumnCount; i++)
        {
            try
            {
                typed[i] = FluidValueModel.ReadCell(values.Column(i), 0);
            }
            catch (System.NotSupportedException)
            {
                typed[i] = null; // unreadable => every node referencing it is dropped below
            }
        }
        var conjuncts = new List<Conjunct>();
        Flatten(tree, typed, conjuncts);
        return new Pushed(conjuncts, values);
    }

    /// <summary>Walks the top-level AND spine, collecting the conjuncts it can express.</summary>
    private static void Flatten(FilterNode node, object?[] values, List<Conjunct> into)
    {
        switch (node.Op)
        {
            case "and":
                foreach (var child in node.Children ?? new List<FilterNode>())
                {
                    Flatten(child, values, into);
                }
                return;

            case "compare" when node.Cmp is { } cmp && node.Val is { } idx:
                if (Named(node) is { } col && TryValue(values, idx, out var v))
                {
                    into.Add(new Conjunct(col, cmp, v, new[] { idx }));
                }
                return;

            case "in" when node.Vals is { Count: > 0 } vals:
                if (Named(node) is { } inCol)
                {
                    var items = new List<object?>(vals.Count);
                    foreach (var i in vals)
                    {
                        if (!TryValue(values, i, out var iv))
                        {
                            return; // one unreadable element makes the whole IN a subset — drop it
                        }
                        items.Add(iv);
                    }
                    into.Add(new Conjunct(inCol, "in", items, vals));
                }
                return;

            case "is_null":
            case "is_not_null":
                if (Named(node) is { } nullCol)
                {
                    into.Add(new Conjunct(nullCol, node.Op, null, System.Array.Empty<int>()));
                }
                return;

            // ⚠ "or" lands here and contributes NOTHING — see the class remarks. So does any node shape this
            // version does not know: silence is the superset-safe direction.
            default:
                return;
        }
    }

    /// <summary>The node's column, dotted for a struct member; null when it names none.</summary>
    /// <remarks>
    /// ⚠ A struct-member conjunct IS offered here although <c>filter_sql</c> deliberately omits it (the host
    /// emits no SQL twin for one, because a column-mapped table's nested children carry PHYSICAL names in
    /// storage). Offering it is still safe — it is a genuine conjunct of the query — but it means
    /// <c>filters</c> can carry a predicate <c>filter_sql</c> does not.
    /// </remarks>
    private static string? Named(FilterNode node)
    {
        if (!string.IsNullOrEmpty(node.Col))
        {
            return node.Col;
        }
        return node.Path is { Count: > 0 } path ? string.Join(".", path) : null;
    }

    /// <summary>The constant at <paramref name="index"/>; false when it is absent or was unreadable.</summary>
    /// <remarks>
    /// ⚠ A legitimate filter constant is NEVER null — the host's serializer refuses <c>col &lt;op&gt; NULL</c>
    /// outright, since it is unknown for every row — so a null here means only "we could not read this Arrow
    /// type", and the node referencing it is dropped.
    /// </remarks>
    private static bool TryValue(object?[] values, int index, out object? item)
    {
        item = index >= 0 && index < values.Length ? values[index] : null;
        return item is not null;
    }

    /// <summary>The conjuncts as Fluid values: a list of <c>{ column, op, value }</c>.</summary>
    internal static object AsFluid(Pushed pushed)
    {
        var rows = new List<object>(pushed.Conjuncts.Count);
        foreach (var c in pushed.Conjuncts)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["column"] = c.Column,
                ["op"] = c.Op,
                ["value"] = c.Value,
            });
        }
        return rows;
    }

    /// <summary>
    /// Declares <c>filters</c> and <c>projected</c> as DuckDB variables on the render's pinned connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ <b>RENDERED AS SQL, where the params bag is staged as a named Arrow source — and the difference is
    /// principled rather than an inconsistency.</b> The params bag is staged because rendering it would lose
    /// types (a DECIMAL's scale, a temporal's unit and zone), which is the whole reason
    /// <c>FluidRenderSession.ApplyVariable</c> exists. Here every member is VARCHAR BY CONSTRUCTION: a
    /// heterogeneous filter list has no homogeneous typed form, so there is no type left to lose. What is NOT
    /// rendered by us is the VALUE — see below.
    /// </para>
    /// <para>
    /// ⚠⚠ <b>THE VALUE IS RENDERED BY DuckDB, FROM THE TYPED CONSTANT.</b> The staged batch is the host's own
    /// filter-value batch (one column <c>v0</c>, <c>v1</c>, … per constant, each its real type), so the
    /// variable is built with <c>CAST(v&lt;i&gt; AS VARCHAR)</c> and DuckDB does the formatting. Rendering it
    /// here instead would mean a second value→text ladder — and the one we have, <c>DuckSql.Literal</c>,
    /// collapses every temporal to TIMESTAMPTZ and REFUSES a LIST or STRUCT by name, so it would be both a
    /// duplicate and a worse one. ⚠ It is the value's TEXT, not a SQL literal: a string arrives UNQUOTED.
    /// Quote it yourself, or use the typed Fluid value.
    /// </para>
    /// <para>
    /// ⚠ Issued directly rather than through <c>FluidRenderSession.BindVariable</c>, whose laziness exists for
    /// <c>fluid_render</c> — a per-ROW scalar where most renders run no SQL at all and an eager copy would be
    /// pure waste. This surface ALWAYS pins its connection (it runs the generated statement), so there is
    /// nothing to defer.
    /// </para>
    /// </remarks>
    internal static void DeclareVariables(FluidRenderSession session, Pushed pushed, Schema projected)
    {
        var names = new List<string>(projected.FieldsList.Count);
        foreach (var f in projected.FieldsList)
        {
            names.Add(DuckSql.Literal(f.Name));
        }
        session.ExecuteNonQuery(
            $"SET VARIABLE {DuckSql.QuoteIdent(FluidRelationInput.ProjectedVariable)} = "
            + $"[{string.Join(", ", names)}]::VARCHAR[]");

        var type = "STRUCT(\"column\" VARCHAR, \"op\" VARCHAR, \"value\" VARCHAR)";
        var variable = DuckSql.QuoteIdent(FluidQueryTableBinding.FiltersVariable);
        if (pushed.Conjuncts.Count == 0 || pushed.Values is null)
        {
            // ⚠ An EMPTY LIST, not an unset variable, and the cast is what gives it a type: `len(...)` then
            // answers 0 for every scan instead of NULL for some. (The params bag goes the other way — absent
            // means absent there — because a bag has no natural empty value.)
            session.ExecuteNonQuery($"SET VARIABLE {variable} = []::{type}[]");
            return;
        }
        var items = new List<string>(pushed.Conjuncts.Count);
        foreach (var c in pushed.Conjuncts)
        {
            string value = c.ValueIndexes.Count switch
            {
                0 => "NULL",
                1 => $"CAST(v{c.ValueIndexes[0]} AS VARCHAR)",
                _ => "CAST(list_value("
                     + string.Join(", ", c.ValueIndexes.Select(i => $"v{i}")) + ") AS VARCHAR)",
            };
            items.Add($"{{'column': {DuckSql.Literal(c.Column)}, 'op': {DuckSql.Literal(c.Op)}, "
                      + $"'value': {value}}}");
        }
        var token = session.RegisterRows(pushed.Values);
        try
        {
            session.ExecuteNonQuery(
                $"SET VARIABLE {variable} = (SELECT CAST([{string.Join(", ", items)}] AS {type}[]) "
                + $"FROM fabricator_scan({DuckSql.Literal(token)}))");
        }
        finally
        {
            session.ReleaseRows(token);
        }
    }

    private static RecordBatch Copy(RecordBatch batch)
    {
        var ms = new MemoryStream();
        using (var w = new ArrowStreamWriter(ms, batch.Schema, leaveOpen: true))
        {
            w.WriteRecordBatch(batch);
            w.WriteEnd();
        }
        ms.Position = 0;
        using var r = new ArrowStreamReader(ms);
        return r.ReadNextRecordBatch()!;
    }
}
