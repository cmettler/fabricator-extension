// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Fluid;
using Fluid.Ast;
using Fluid.Values;

namespace Fabricator.FluidPlugin;

/// <summary>
/// The <c>{% print %}</c> block: run the captured body as a query and RENDER its rows, rather than binding
/// them to a name.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ It is <c>{% query %}</c> with the destination changed, and it routes through the same
/// <see cref="FluidHostQuery.RunCaptured"/> — so the classifier (SELECT only), the per-render pinned
/// connection, the row cap and the value model are ONE mechanism, not a second copy that could drift.
/// </para>
/// <para>
/// ⚠⚠ Each cell is written with <c>WriteToAsync</c>, the same call <c>{{ r.a }}</c> makes, rather than
/// through <c>ToStringValue</c>. That is what makes printed text identical to interpolated text — a second
/// formatting path would be free to disagree about numbers, dates and nulls, and would do so silently.
/// </para>
/// </remarks>
internal static class FluidHostPrint
{
    internal const string BlockName = "print";

    /// <summary>Separator between the VALUES of one row. Default a single space.</summary>
    internal const string DelimArg = "delim";

    /// <summary>Separator between ROWS. Default a newline.</summary>
    internal const string RowDelimArg = "rowdelim";

    /// <summary>Render each value as a DuckDB SQL LITERAL rather than as text. Default false.</summary>
    internal const string SqlLiteralArg = "sql_literal";

    internal const string DefaultDelim = " ";
    internal const string DefaultRowDelim = "\n";

    /// <summary>
    /// Splits the tag's named arguments into this block's OPTIONS and the rest, which become bound
    /// parameters.
    /// </summary>
    /// <remarks>
    /// ⚠⚠ <c>delim</c> and <c>rowdelim</c> are RESERVED here, so a statement wanting a parameter of either
    /// name cannot get one. That is a real collision and it is accepted because it FAILS LOUDLY rather than
    /// silently: DuckDB reports the parameter it was not given, by name
    /// (<c>Values were not provided for the following prepared statement parameters: delim</c>). A silent
    /// version of this — quietly binding nothing — would be the trap; a named error is a signpost.
    /// ⚠ Fluid's grammar is <c>name: value</c>, so the <c>delim := " "</c> spelling of the request is not
    /// expressible; inventing one would be a grammar only this plugin speaks (the reason
    /// <c>ArgumentsList</c> was reused in the first place).
    /// </remarks>
    internal static async ValueTask<(string Delim, string RowDelim, bool SqlLiteral,
                                     List<FilterArgument> Parameters)>
        SplitOptionsAsync(IReadOnlyList<FilterArgument>? args, TemplateContext ctx)
    {
        var delim = DefaultDelim;
        var rowDelim = DefaultRowDelim;
        var sqlLiteral = false;
        var rest = new List<FilterArgument>();
        if (args is null)
        {
            return (delim, rowDelim, sqlLiteral, rest);
        }
        foreach (var arg in args)
        {
            if (string.Equals(arg.Name, DelimArg, StringComparison.OrdinalIgnoreCase))
            {
                delim = (await arg.Expression.EvaluateAsync(ctx)).ToStringValue();
            }
            else if (string.Equals(arg.Name, RowDelimArg, StringComparison.OrdinalIgnoreCase))
            {
                rowDelim = (await arg.Expression.EvaluateAsync(ctx)).ToStringValue();
            }
            else if (string.Equals(arg.Name, SqlLiteralArg, StringComparison.OrdinalIgnoreCase))
            {
                sqlLiteral = (await arg.Expression.EvaluateAsync(ctx)).ToBooleanValue();
            }
            else
            {
                rest.Add(arg);
            }
        }
        return (delim, rowDelim, sqlLiteral, rest);
    }

    /// <summary>Writes the rows, values joined by <paramref name="delim"/> and rows by
    /// <paramref name="rowDelim"/>.</summary>
    /// <remarks>
    /// ⚠ The delimiters are JOINERS, not terminators: nothing is written before the first row or after the
    /// last. A trailing newline would be invisible in most templates and wrong in the ones that compose
    /// this into a larger string, and a caller who wants one can write it.
    /// </remarks>
    /// <param name="sqlLiteral">Render each value as a DuckDB SQL literal instead of as text — the SAME
    /// <see cref="FluidValueModel.SqlLiteral"/> the <c>{{ v | sql }}</c> filter uses, so the two spellings
    /// cannot disagree about quoting, about the invariant number format, or about which values are refused.
    /// ⚠ It is an ALLOW-LIST, not an escaper: a cell whose rendering is not provably safe (a LIST, a STRUCT)
    /// is refused BY NAME rather than stringified, and the refusal names this block.</param>
    internal static async ValueTask WriteRowsAsync(FluidValue rows, IFluidOutput output,
                                                   System.Text.Encodings.Web.TextEncoder encoder,
                                                   TemplateContext ctx, string delim, string rowDelim,
                                                   bool sqlLiteral = false)
    {
        bool firstRow = true;
        await foreach (var row in rows.EnumerateAsync(ctx))
        {
            if (!firstRow)
            {
                output.Write(rowDelim);
            }
            firstRow = false;

            bool firstCell = true;
            await foreach (var member in row.EnumerateAsync(ctx))
            {
                // A dictionary-like row enumerates as [key, value] pairs — the same shape Members() reads,
                // and what `{% for kv in row %}` gives. Anything else is not a row and is written whole.
                var cell = member is ArrayValue kv && kv.Values.Count == 2 ? kv.Values[1] : member;
                if (!firstCell)
                {
                    output.Write(delim);
                }
                firstCell = false;
                if (sqlLiteral)
                {
                    // ⚠ Written RAW, not through the encoder: the output is SQL text, and an encoder would
                    // turn the quotes this very option exists to produce into &#39; — corrupting every
                    // literal. The same reason the {% exec %} block renders its body with NullEncoder.
                    output.Write(FluidValueModel.SqlLiteral(cell, ctx,
                                                            "{% " + BlockName + " " + SqlLiteralArg + " %}"));
                }
                else
                {
                    await cell.WriteToAsync(output, encoder, ctx.CultureInfo);
                }
            }
        }
    }
}
