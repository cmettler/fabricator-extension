// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Linq;
using Fabricator.Bridge;
using Xunit;

namespace Fabricator.Bridge.Tests;

/// <summary>
/// <see cref="FabricRefreshTables.Parse"/>: the <c>tables</c> argument of <c>fabric.refresh_sql_endpoint</c>
/// (Fabric.Api 2.27). The service takes at most 25 tables per request, so the list is cut into batches, each grouped
/// by schema.
/// </summary>
public class FabricRefreshTablesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(" , ,")]
    public void Nothing_refreshes_everything(string? tables) => Assert.Null(FabricRefreshTables.Parse(tables));

    [Fact]
    public void A_bare_table_is_in_dbo()
    {
        var batch = Assert.Single(FabricRefreshTables.Parse("orders")!);
        var group = Assert.Single(batch);
        Assert.Equal("dbo", group.Schema);
        Assert.Equal(new[] { "orders" }, group.Tables);
    }

    [Fact]
    public void Tables_are_grouped_by_schema_in_their_order()
    {
        var batch = Assert.Single(FabricRefreshTables.Parse(" dbo.orders, sales.customers ,dbo.lines")!);
        Assert.Equal(new[] { "dbo", "sales" }, batch.Select(g => g.Schema));
        Assert.Equal(new[] { "orders", "lines" }, batch[0].Tables);
        Assert.Equal(new[] { "customers" }, batch[1].Tables);
    }

    [Fact]
    public void Quoted_names_keep_their_dots()
    {
        var batch = Assert.Single(FabricRefreshTables.Parse("\"my.schema\".\"a.b\", [x].[y.z], \"t.1\"")!);
        Assert.Equal(new[] { "my.schema", "x", "dbo" }, batch.Select(g => g.Schema));
        Assert.Equal(new[] { "a.b" }, batch[0].Tables);
        Assert.Equal(new[] { "y.z" }, batch[1].Tables);
        Assert.Equal(new[] { "t.1" }, batch[2].Tables);
    }

    [Fact]
    public void More_than_25_tables_go_in_several_requests()
    {
        var names = string.Join(",", Enumerable.Range(0, 60).Select(i => $"t{i}"));
        var batches = FabricRefreshTables.Parse(names)!;
        Assert.Equal(new[] { 25, 25, 10 }, batches.Select(b => b.Sum(g => g.Tables.Count)));
        Assert.Equal("t25", batches[1][0].Tables[0]);
    }

    [Theory]
    [InlineData(".orders")]
    [InlineData("dbo.")]
    [InlineData("\"\".x")]
    public void A_missing_name_is_refused(string tables) =>
        Assert.Throws<ArgumentException>(() => FabricRefreshTables.Parse(tables));
}
