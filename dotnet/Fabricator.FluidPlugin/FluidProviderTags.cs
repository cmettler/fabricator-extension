// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Text;
using Apache.Arrow;
using Fabricator.Bridge;

namespace Fabricator.FluidPlugin;

/// <summary>
/// The <c>{% provider_query %}</c> / <c>{% provider_exec %}</c> blocks: a body written in ANOTHER ENGINE's
/// dialect, run against an attached fabricator catalog.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>WRAP AND DELEGATE — there is no new mechanism here.</b> The body is rendered, then embedded in a
/// DuckDB statement calling <c>fabricator_query</c> / <c>fabricator_exec</c>, which the existing
/// <c>{% query %}</c> path runs. So the value model, the row cap, the per-render pinned connection and
/// <c>materialize:</c>' siblings all compose for free, and the two tags cannot drift from the function forms
/// on what a result or a count means.
/// </para>
/// <para>
/// ⚠⚠ <b>WHAT THE TAG BUYS over what already worked is that the body stops being a quoted string
/// argument</b>: multi-line, with <c>{% for %}</c>/<c>{% if %}</c> inside it, and no escaping.
/// <c>{% query r %}SELECT * FROM fabricator_query('mssql', '…'){% endquery %}</c> has always been legal.
/// That is the same argument that justified <c>{% exec %}</c> over <c>exec("…")</c>.
/// </para>
/// <para>
/// ⚠⚠ <b>A DISTINCT NAME IS A FEATURE, not just readability.</b> An option on the existing block
/// (<c>{% query r catalog: 'mssql' %}</c>) was considered and rejected: <c>materialize:</c> changes where the
/// rows GO, while a catalog option would change which engine PARSES AND RUNS the body. An option that
/// silently switches dialect is the runs-and-means-something-different shape.
/// </para>
/// <para>
/// ⚠⚠ <b>THE SELECT-ONLY GUARD DOES NOT TRANSFER, AND THAT IS ACCEPTED RATHER THAN OVERLOOKED (user
/// decision, 2026-09-06).</b> <c>{% query %}</c> refuses a non-SELECT using DuckDB's OWN parser, because a
/// bind REPEATS and happens WITHOUT execution. For provider SQL there is no parser we can ask, and
/// <c>fabricator_query</c> runs writes happily — so <c>{% provider_query %}</c> inside <c>fluid_query</c> can
/// write to another engine's database at BIND time, repeatedly. The cost is PINNED as asserted behaviour in
/// <c>verify_plugin_fluid</c> rather than described, which is the same treatment <c>{% exec %}</c>'s
/// bind-repetition already gets.
/// </para>
/// <para>
/// The precedent is exact: an <c>exec()</c> refusal in <c>fluid_query</c> was BUILT and then DELETED, because
/// §11.1a MEASURED that it was already walk-aroundable by nesting a writing scalar inside a SELECT. A refusal
/// anyone can nest around is a speed bump for the accident, not a boundary — and it reads as a defence.
/// ⛔ Do NOT "fix" this by matching a leading keyword on the body: that is the measured-broken prefix check,
/// defeated by <c>WITH x AS (…) INSERT …</c>, a view, or a name we do not ship.
/// </para>
/// </remarks>
internal static class FluidProviderTags
{
    /// <summary>The Liquid tag name of the read form.</summary>
    internal const string QueryBlockName = "provider_query";

    /// <summary>The Liquid tag name of the write form.</summary>
    internal const string ExecBlockName = "provider_exec";

    /// <summary>The column the exec wrapper reports its affected-row count under.</summary>
    internal const string AffectedColumn = "affected";

    /// <summary>
    /// Builds the DuckDB statement that runs <paramref name="body"/> on <paramref name="catalog"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ <b>THE TAG'S NAMED ARGUMENTS BECOME THE PROVIDER'S PARAMETERS, and they cross as DuckDB BOUND
    /// PARAMETERS the whole way</b> — <c>params := struct_pack("a" := $a)</c>, never a rendered literal.
    /// MEASURED that this binds and keeps its types: a prepared
    /// <c>fabricator_query($cat, $sql, params := struct_pack(a := $a, b := $b))</c> returns
    /// <c>int32</c>/<c>varchar</c> for 41 and 'hi'. That matters because the alternative — rendering the bag
    /// with <c>DuckSql.Literal</c> — is DuckDB DIALECT, which coincides with T-SQL for strings and integers
    /// and diverges for booleans, blobs and temporals.
    /// </para>
    /// <para>
    /// ⚠ The CATALOG and the BODY are embedded as DuckDB string LITERALS, and that is correct rather than a
    /// shortcut: they are arguments to a DuckDB function, so DuckDB's dialect is the right one, and
    /// <see cref="DuckSql.Literal"/> is its renderer. Nothing of the body is parsed here — it is opaque text
    /// on its way to another engine.
    /// </para>
    /// <para>
    /// ⚠ <c>fabricator_query</c> takes the bag NAMED (<c>params :=</c>) and <c>fabricator_exec</c> takes it
    /// POSITIONALLY, because the latter is a SCALAR and a DuckDB scalar carries no named parameters at all.
    /// The asymmetry is DuckDB's; both spellings reach one bag.
    /// </para>
    /// <para>
    /// ⚠ With NO named arguments the bag is OMITTED entirely rather than sent empty: <c>struct_pack()</c>
    /// with no fields is a zero-field struct, which Apache.Arrow refuses in both directions.
    /// </para>
    /// </remarks>
    internal static string BuildStatement(string catalog, string body, IReadOnlyList<string> parameterNames,
                                          bool isQuery)
    {
        var sql = new StringBuilder();
        sql.Append(isQuery ? "SELECT * FROM fabricator_query(" : "SELECT fabricator_exec(")
           .Append(DuckSql.Literal(catalog))
           .Append(", ")
           .Append(DuckSql.Literal(body));
        if (parameterNames.Count > 0)
        {
            sql.Append(", ");
            if (isQuery)
            {
                sql.Append("params := ");
            }
            sql.Append("struct_pack(");
            for (int i = 0; i < parameterNames.Count; i++)
            {
                if (i > 0)
                {
                    sql.Append(", ");
                }
                sql.Append(DuckSql.QuoteIdent(parameterNames[i]))
                   .Append(" := $")
                   .Append(parameterNames[i]);
            }
            sql.Append(')');
        }
        sql.Append(')');
        if (!isQuery)
        {
            // ⚠ Aliased, because without it the column is named by its own EXPRESSION TEXT -- the whole
            // fabricator_exec(...) call, quotes and all -- which is what the value would then have to be
            // addressed by.
            sql.Append(" AS ").Append(AffectedColumn);
        }
        return sql.ToString();
    }

    /// <summary>
    /// Resolves the tag's first token — the catalog — refusing the shapes that would otherwise reach the
    /// provider as an empty name.
    /// </summary>
    /// <remarks>
    /// ⚠ It is an EXPRESSION, not an identifier, so <c>{% provider_query 'mssql' r %}</c> and
    /// <c>{% provider_query params.cat r %}</c> both work. The cost is that a BARE word is a variable
    /// reference and evaluates to nil — which is why nil and empty are refused HERE, naming the quoted form.
    /// Without that, the natural-looking <c>{% provider_query mssql r %}</c> would reach the host as an empty
    /// catalog and fail somewhere that names neither the tag nor the word.
    /// </remarks>
    internal static string RequireCatalog(string caller, string tag, string? catalog)
    {
        if (string.IsNullOrWhiteSpace(catalog))
        {
            throw new ArgumentException(
                $"{caller}: {{% {tag} %}} was given no catalog. The first token is an EXPRESSION, so a "
                + $"literal name must be QUOTED — {{% {tag} 'mssql' result %}} — while a bare word is read "
                + "as a Liquid variable and is nil unless one is set.");
        }
        return catalog!;
    }

    /// <summary>Refuses an empty body, naming the tag that captured it.</summary>
    /// <remarks>
    /// ⚠ Checked on the BODY rather than on the wrapper, which can never be empty: an empty body would
    /// otherwise be sent to the provider as an empty statement and refused there, one layer away from the
    /// tag the author wrote.
    /// </remarks>
    internal static string RequireBody(string caller, string tag, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException($"{caller}: {{% {tag} %}} block is empty — it rendered no SQL.");
        }
        return body;
    }

    /// <summary>The parameter names of a block's argument batch, in order — the fields of the bag.</summary>
    internal static IReadOnlyList<string> NamesOf(RecordBatch? parameters)
    {
        if (parameters is null)
        {
            // ⚠ System.Array: `Apache.Arrow.Array` is also in scope here, so the bare name is CS0104.
            return System.Array.Empty<string>();
        }
        var fields = parameters.Schema.FieldsList;
        var names = new List<string>(fields.Count);
        for (int i = 0; i < fields.Count; i++)
        {
            names.Add(fields[i].Name);
        }
        return names;
    }
}
