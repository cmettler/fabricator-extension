// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Fabricator.Bridge.Tests;

/// <summary>
/// The scan plan on the wire (ABI v101 <c>table_file_plan</c>): the table's half as JSON and the parts as columns
/// (turned into one Arrow batch by ScanPartsArrow, which needs Arrow and is covered end to end by
/// verify_delta_file_scan). What src/fabricator/fabricator_file_scan.cpp reads; these tests pin the producer half
/// against the protocol documented in abi.h. Keep the two in step.
/// </summary>
public class ScanPlanWireTests
{
    private static JsonElement Wire(ScanPlan plan) => JsonDocument.Parse(ScanPlanWire.ToJson(plan)).RootElement;

    [Fact]
    public void A_declined_plan_is_only_its_reason()
    {
        var root = Wire(ScanPlan.Decline("column mapping 'id'"));
        Assert.Equal("column mapping 'id'", root.GetProperty("declined").GetString());
        Assert.Single(root.EnumerateObject());
    }

    [Fact]
    public void A_file_part_carries_path_size_and_deleted_rows()
    {
        var plan = new ScanPlan
        {
            Parts = new ScanPart[] { new FilePart("s3://b/t/f1.parquet") { Size = 1234, DeletedRows = new long[] { 3, 9 } } },
            RowCount = 98,
        };
        Assert.Equal(98, Wire(plan).GetProperty("row_count").GetInt64());
        var parts = ScanPlanWire.PartColumns(plan);
        Assert.Equal(new[] { "s3://b/t/f1.parquet" }, parts.Paths);
        Assert.Equal(new[] { false }, parts.Streams);
        Assert.Equal(new long?[] { 1234 }, parts.Sizes);
        Assert.Equal(new long[] { 3, 9 }, parts.Deleted[0]);
    }

    [Fact]
    public void What_is_unknown_or_empty_is_left_out()
    {
        var plan = new ScanPlan { Parts = new ScanPart[] { new FilePart("f") { DeletedRows = new long[0] } } };
        var root = Wire(plan);
        Assert.False(root.TryGetProperty("row_count", out _));   // unknown: the host sets no cardinality
        Assert.False(root.TryGetProperty("columns", out _));
        Assert.False(root.TryGetProperty("virtual", out _));
        var parts = ScanPlanWire.PartColumns(plan);
        Assert.Null(parts.Sizes[0]);
        Assert.Null(parts.Deleted[0]);   // empty = none
        Assert.Empty(parts.ConstantNames);
        Assert.Empty(parts.VirtualNames);
    }

    [Fact]
    public void A_stream_part_is_named_and_marked_and_has_no_deleted_rows()
    {
        var parts = ScanPlanWire.PartColumns(new ScanPlan { Parts = new ScanPart[] { new StreamPart("fragment-7@v3") } });
        Assert.Equal(new[] { "fragment-7@v3" }, parts.Paths);
        Assert.Equal(new[] { true }, parts.Streams);
        Assert.Null(parts.Deleted[0]);
    }

    [Fact]
    public void Constants_are_text_and_a_null_constant_stays_null()
    {
        var parts = ScanPlanWire.PartColumns(new ScanPlan
        {
            Parts = new ScanPart[]
            {
                new FilePart("f1")
                {
                    Constants = new Dictionary<string, string?> { ["region"] = "eu", ["day"] = "2024-01-02" },
                },
                new FilePart("f2") { Constants = new Dictionary<string, string?> { ["day"] = null, ["region"] = "us" } },
            },
        });
        Assert.Equal(new[] { "region", "day" }, parts.ConstantNames);
        Assert.Equal(new string?[] { "eu", "us" }, parts.ConstantValues[0]);
        Assert.Equal(new string?[] { "2024-01-02", null }, parts.ConstantValues[1]);
    }

    [Fact]
    public void Every_part_must_state_the_same_constants_and_virtual_values()
    {
        // a part without a constant would read as NULL for that part, which is not "read it from the file"
        var ex = Assert.Throws<System.InvalidOperationException>(() => ScanPlanWire.PartColumns(new ScanPlan
        {
            Parts = new ScanPart[]
            {
                new FilePart("f1") { Constants = new Dictionary<string, string?> { ["region"] = "eu" } },
                new FilePart("f2"),
            },
        }));
        Assert.Contains("part 1", ex.Message);
        Assert.Throws<System.InvalidOperationException>(() => ScanPlanWire.PartColumns(new ScanPlan
        {
            Parts = new ScanPart[]
            {
                new FilePart("f1") { VirtualValues = new Dictionary<string, long?> { ["rowid"] = 0 } },
                new FilePart("f2") { VirtualValues = new Dictionary<string, long?> { ["row_id"] = 0 } },
            },
        }));
    }

    [Fact]
    public void Virtual_columns_say_how_and_each_part_says_its_value()
    {
        var plan = new ScanPlan
        {
            VirtualColumns = new Dictionary<string, ScanVirtualColumn>
            {
                ["rowid"] = new(AddRowNumber: true),
                ["__delta_row_id"] = new("_row-id-col-1", AddRowNumber: true),
                ["__delta_row_commit_version"] = new("_row-commit-version-col-1"),
            },
            Parts = new ScanPart[]
            {
                new FilePart("f")
                {
                    VirtualValues = new Dictionary<string, long?>
                    {
                        ["rowid"] = 1L << 40, ["__delta_row_id"] = 500, ["__delta_row_commit_version"] = null,
                    },
                },
            },
        };
        var root = Wire(plan);
        var rowid = root.GetProperty("virtual").GetProperty("rowid");
        Assert.True(rowid.GetProperty("add_row_number").GetBoolean());
        Assert.False(rowid.TryGetProperty("stored", out _));
        var version = root.GetProperty("virtual").GetProperty("__delta_row_commit_version");
        Assert.Equal("_row-commit-version-col-1", version.GetProperty("stored").GetString());
        Assert.False(version.TryGetProperty("add_row_number", out _)); // false is the default, left out
        var parts = ScanPlanWire.PartColumns(plan);
        Assert.Equal(new[] { "rowid", "__delta_row_id", "__delta_row_commit_version" }, parts.VirtualNames);
        Assert.Equal(new long?[] { 1L << 40 }, parts.VirtualValues[0]);
        Assert.Equal(new long?[] { 500 }, parts.VirtualValues[1]);
        Assert.Equal(new long?[] { null }, parts.VirtualValues[2]);
    }

    [Fact]
    public void Stored_names_nest_through_struct_members()
    {
        var root = Wire(new ScanPlan
        {
            StoredColumns = new Dictionary<string, StoredColumn>
            {
                ["id"] = new("col-1"),
                ["st"] = new("col-2", new Dictionary<string, StoredColumn>
                {
                    ["a"] = new("col-3"),
                    ["b"] = new(null, new Dictionary<string, StoredColumn> { ["c"] = new("col-5") }),
                }),
            },
            Parts = new ScanPart[] { new FilePart("f") },
        });
        var columns = root.GetProperty("columns");
        Assert.Equal("col-1", columns.GetProperty("id").GetProperty("stored").GetString());
        var st = columns.GetProperty("st");
        Assert.Equal("col-2", st.GetProperty("stored").GetString());
        Assert.Equal("col-3", st.GetProperty("members").GetProperty("a").GetProperty("stored").GetString());
        var b = st.GetProperty("members").GetProperty("b");
        Assert.False(b.TryGetProperty("stored", out _)); // stored under its own name
        Assert.Equal("col-5", b.GetProperty("members").GetProperty("c").GetProperty("stored").GetString());
    }

    [Fact]
    public void Names_with_quotes_and_control_characters_survive()
    {
        var root = Wire(new ScanPlan
        {
            StoredColumns = new Dictionary<string, StoredColumn> { ["k\"ey"] = new("col \"q\" \\ \u0001") },
            Parts = new ScanPart[] { new FilePart("f") },
        });
        Assert.Equal("col \"q\" \\ \u0001", root.GetProperty("columns").GetProperty("k\"ey").GetProperty("stored").GetString());
    }

    [Fact]
    public void Parts_keep_their_order()
    {
        var parts = ScanPlanWire.PartColumns(new ScanPlan
        {
            Parts = new ScanPart[] { new FilePart("b"), new StreamPart("a"), new FilePart("c") },
        });
        Assert.Equal(new[] { "b", "a", "c" }, parts.Paths);
        Assert.Equal(new[] { false, true, false }, parts.Streams);
    }
}
