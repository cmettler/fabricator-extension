// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Fabricator.Bridge;
using Xunit;

namespace Fabricator.Bridge.Tests;

/// <summary>
/// <see cref="DuckSql.QuoteIdent"/> / <see cref="DuckSql.UnquoteIdent"/>.
/// </summary>
/// <remarks>
/// ⚠ The interesting half is what UnquoteIdent REFUSES to do. Stripping the outer quotes of
/// <c>"a"."b"</c> yields <c>a"."b</c> — a relation name nobody wrote — and a template that mislabels a
/// column that way fails nowhere. These cases are the reason the helper exists in C# rather than as eight
/// lines of Liquid in each template.
/// </remarks>
public class DuckSqlIdentTests
{
    [Theory]
    [InlineData("px_one", "\"px_one\"")]
    [InlineData("odd name", "\"odd name\"")]
    [InlineData("od\"d", "\"od\"\"d\"")]
    public void QuoteIdent_doubles_embedded_quotes(string raw, string quoted) =>
        Assert.Equal(quoted, DuckSql.QuoteIdent(raw));

    [Theory]
    [InlineData("\"px_one\"", "px_one")]
    [InlineData("\"odd name\"", "odd name")]
    [InlineData("\"od\"\"d\"", "od\"d")]
    [InlineData("\"\"\"\"", "\"")]          // QuoteIdent of a lone quote
    public void UnquoteIdent_unwraps_a_single_quoted_identifier(string quoted, string raw) =>
        Assert.Equal(raw, DuckSql.UnquoteIdent(quoted));

    [Theory]
    [InlineData("px_one")]                   // bare
    [InlineData("memory.main.px_two")]       // qualified: NOT reduced to its last part
    [InlineData("\"a\".\"b\"")]              // TWO identifiers, not one
    [InlineData("\"a\".b")]                  // mixed
    [InlineData("\"")]                       // too short to be a pair
    [InlineData("\"\"\"")]                   // a lone interior quote
    [InlineData("no\"pair")]
    public void UnquoteIdent_leaves_anything_it_cannot_decide(string s) =>
        Assert.Equal(s, DuckSql.UnquoteIdent(s));

    [Theory]
    [InlineData("px_one")]
    [InlineData("odd name")]
    [InlineData("od\"d")]
    [InlineData("a\".\"b")]                  // the string that LOOKS like two parts once quoted
    [InlineData("\"")]
    [InlineData("")]
    public void Unquote_round_trips_quote(string raw) =>
        Assert.Equal(raw, DuckSql.UnquoteIdent(DuckSql.QuoteIdent(raw)));
}
