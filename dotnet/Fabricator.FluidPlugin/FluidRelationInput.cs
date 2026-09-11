// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Fabricator.Bridge;
using Fluid;

namespace Fabricator.FluidPlugin;

/// <summary>
/// The rules shared by the two surfaces that render a template WITH A RELATION IN HAND —
/// <see cref="FluidQueryBatchFunction"/> (the collector) and <see cref="FluidQueryInOutFunction"/> (the
/// streaming in-out). They differ ONLY in how the input is divided into renders; everything about what a
/// render is, how its statement is described, narrowed and checked, is the same.
/// </summary>
/// <remarks>
/// ⚠⚠ EXTRACTED WHEN THE SECOND SURFACE ARRIVED, deliberately, rather than copied: these are the pieces a
/// second copy would drift on, and each of them is load-bearing in a way a reader would not guess —
/// <see cref="DescribeGenerated"/>'s <c>LIMIT 0</c> wrapper is also what REQUIRES the generated statement
/// to be a subquery-usable SELECT, <see cref="Wrap"/> IS the projection pushdown, and
/// <see cref="Verify"/> is what stops a drifting render being read as DATA. A divergence in any one of them
/// between the two surfaces would be a silent behaviour difference, not a compile error.
/// </remarks>
internal static class FluidRelationInput
{
    /// <summary>The name both surfaces expose the input rows under.</summary>
    internal const string InputTable = "input_table";

    /// <summary>The template variable naming the output columns the caller actually reads.</summary>
    internal const string ProjectedVariable = "projected";

    /// <summary>The <c>publish()</c> refusal, which is the same measured hazard on both surfaces.</summary>
    /// <remarks>
    /// ⚠ Both run the rendered statement on the template's OWN pinned connection, so scanning a publication
    /// re-enters that connection mid-query — MEASURED to DEADLOCK rather than to raise the one-live-result
    /// error, which is why it is refused by name rather than left to fail. Nothing is lost: a publication
    /// exists to carry a staged relation ACROSS a connection boundary, and here there is none.
    /// </remarks>
    internal static string PublishRefusal(string functionName) =>
        "publish() cannot be used here — " + functionName + " runs the rendered statement on the template's "
        + "OWN connection, so scanning a publication would re-enter that connection and hang. A publication "
        + "carries a staged relation to a DIFFERENT connection, which this surface does not need: select the "
        + "staged table directly (SELECT * FROM my_table).";

    /// <summary>Builds the render context every render of one execution shares.</summary>
    internal static TemplateContext NewContext(string functionName,
                                               FluidRenderSession session,
                                               object? parameters,
                                               bool isBind,
                                               Schema? projectedSchema = null)
    {
        var ctx = FluidEngine.NewRenderContext(functionName, PublishRefusal(functionName), session, c =>
        {
            FluidValueModel.SetVariable(c, FluidValueModel.BagVariable, parameters);
        });
        ctx.SetValue(FluidEngine.IsBindVariable, isBind);
        if (projectedSchema is not null)
        {
            // ⚠ The template may use it or ignore it: the wrapper narrows the result either way, so a
            // template that never reads `projected` is correct and merely does more work. NOT bound during
            // the schema probe — there is no projection yet, the bind is what the planner narrows — so a
            // template reading it branches on is_bind.
            FluidValueModel.SetVariable(ctx, ProjectedVariable,
                                        projectedSchema.FieldsList.Select(f => f.Name).ToArray());
        }
        return ctx;
    }

    /// <summary>
    /// The generated statement as these functions run it: exactly the columns this plan reads, named.
    /// </summary>
    /// <remarks>
    /// ⚠⚠ NAMING THEM IS THE PROJECTION PUSHDOWN — MEASURED, DuckDB prunes an unreferenced expression inside
    /// a subquery, so a narrowed outer SELECT makes the TEMPLATE'S OWN statement stop computing what nobody
    /// reads. It is applied even with no projection, so the drift behaviour does not depend on the caller's
    /// SELECT list: an EXTRA column is dropped either way, and a missing or renamed one fails at the inner
    /// bind naming the column.
    /// </remarks>
    internal static string Wrap(string generated, Schema keep) =>
        $"SELECT {string.Join(", ", keep.FieldsList.Select(f => DuckSql.QuoteIdent(f.Name)))} FROM ({generated})";

    /// <summary>The schema of <paramref name="generated"/> without producing a row of it.</summary>
    /// <remarks>
    /// ⚠ Wrapped in <c>SELECT * FROM (…) LIMIT 0</c>, the same shape <c>Publish</c> uses, which does two
    /// jobs: it binds without scanning, and it REQUIRES the generated statement to be a SELECT usable as a
    /// subquery. Running the statement bare would silently accept a DDL or DML and declare whatever shape
    /// the engine reports for it.
    /// </remarks>
    internal static Schema DescribeGenerated(string functionName, FluidRenderSession session, string generated)
    {
        if (string.IsNullOrWhiteSpace(generated))
        {
            throw new ArgumentException(
                $"{functionName}: the template rendered nothing. It must render a SELECT — under "
                + $"{FluidEngine.IsBindVariable} too, where a `SELECT … LIMIT 0` of the right columns is the "
                + "usual answer.");
        }
        using var stream = session.Query($"SELECT * FROM ({generated}) LIMIT 0");
        return stream.Schema;
    }

    /// <summary>Creates an EMPTY <c>input_table</c> for the bind-time schema probe.</summary>
    /// <remarks>
    /// ⚠ Via a zero-row named source rather than rendered DDL: writing `CREATE TABLE t(a VARCHAR, …)` needs
    /// an Arrow→DuckDB type-name table by hand, which is the second type mapping this codebase keeps
    /// refusing to maintain. DuckDB derives the columns from the Arrow schema instead.
    /// </remarks>
    internal static void CreateEmptyInput(FluidRenderSession session, Schema inputSchema)
    {
        var token = session.RegisterRows(inputSchema);
        try
        {
            session.ExecuteNonQuery(
                $"CREATE OR REPLACE TEMP TABLE {DuckSql.QuoteIdent(InputTable)} AS "
                + $"SELECT * FROM fabricator_scan({DuckSql.Literal(token)})");
        }
        finally
        {
            session.ReleaseRows(token);
        }
    }

    internal static string ReadTemplate(string functionName, RecordBatch? args)
    {
        if (FluidValueModel.ArgColumn(args, "template") is not StringArray templates || templates.Length == 0
            || templates.IsNull(0))
        {
            throw new ArgumentException($"{functionName}: 'template' must be a non-NULL VARCHAR");
        }
        return templates.GetString(0);
    }

    /// <summary>The columns a projection narrows <paramref name="full"/> to.</summary>
    internal static Schema Narrow(Schema full, IReadOnlyList<int>? projected) =>
        projected is { Count: > 0 } && projected.Count != full.FieldsList.Count
            ? new Schema(projected.Select(i => full.FieldsList[i]).ToList(), metadata: null)
            : full;

    /// <summary>
    /// Refuses a render whose statement does not produce the columns the bind-time probe declared.
    /// </summary>
    /// <remarks>
    /// ⚠ Without it a drifting render would be read as DATA: the host builds its converters from the
    /// DECLARED schema, so a renamed or retyped column arrives as whatever those converters make of it.
    /// <paramref name="unit"/> is the word for one render on this surface — a "group" for the collector, a
    /// "chunk" for the streaming in-out — so the message names what the author has to keep stable.
    /// </remarks>
    internal static void Verify(string functionName, string unit, Schema arrived, Schema declared)
    {
        string? problem = null;
        if (arrived.FieldsList.Count != declared.FieldsList.Count)
        {
            problem = $"{arrived.FieldsList.Count} columns where {declared.FieldsList.Count} were declared";
        }
        else
        {
            for (int i = 0; i < declared.FieldsList.Count && problem is null; i++)
            {
                var d = declared.FieldsList[i];
                var a = arrived.FieldsList[i];
                if (!string.Equals(d.Name, a.Name, StringComparison.Ordinal))
                {
                    problem = $"column {i + 1} is named '{a.Name}' where '{d.Name}' was declared";
                }
                else if (d.DataType.TypeId != a.DataType.TypeId)
                {
                    problem = $"column '{d.Name}' is {a.DataType.Name} where {d.DataType.Name} was declared";
                }
            }
        }
        if (problem is not null)
        {
            throw new InvalidOperationException(
                $"{functionName}: a rendered statement produced {problem}. Every {unit} must produce the "
                + $"columns the schema-probe render declared — branch on {FluidEngine.IsBindVariable} to "
                + "declare them, and keep every other branch to that same shape.");
        }
    }
}
