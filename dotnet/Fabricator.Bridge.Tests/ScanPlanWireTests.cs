// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Fabricator.Bridge.Tests;

/// <summary>
/// The scan plan on the wire (ABI v101 <c>table_file_plan</c>): what <see cref="ScanPlanWire"/> writes is what
/// src/fabricator/fabricator_file_scan.cpp reads, and nothing short of a full build exercises the pair — these
/// tests pin the producer half against the protocol documented in abi.h. Keep the two in step.
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
        var root = Wire(new ScanPlan
        {
            Parts = new ScanPart[] { new FilePart("s3://b/t/f1.parquet") { Size = 1234, DeletedRows = new long[] { 3, 9 } } },
            RowCount = 98,
        });
        Assert.Equal(98, root.GetProperty("row_count").GetInt64());
        var file = root.GetProperty("files")[0];
        Assert.Equal("s3://b/t/f1.parquet", file.GetProperty("path").GetString());
        Assert.Equal(1234, file.GetProperty("size").GetInt64());
        Assert.Equal(new long[] { 3, 9 }, file.GetProperty("dv").EnumerateArray().Select(e => e.GetInt64()));
        Assert.False(file.TryGetProperty("stream", out _));
    }

    [Fact]
    public void What_is_unknown_or_empty_is_left_out()
    {
        var root = Wire(new ScanPlan { Parts = new ScanPart[] { new FilePart("f") { DeletedRows = new long[0] } } });
        Assert.False(root.TryGetProperty("row_count", out _));   // unknown: the host sets no cardinality
        Assert.False(root.TryGetProperty("columns", out _));
        Assert.False(root.TryGetProperty("virtual", out _));
        var file = root.GetProperty("files")[0];
        Assert.Equal(new[] { "path" }, file.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void A_stream_part_is_named_and_marked_and_has_no_deleted_rows()
    {
        var root = Wire(new ScanPlan { Parts = new ScanPart[] { new StreamPart("fragment-7@v3") } });
        var part = root.GetProperty("files")[0];
        Assert.Equal("fragment-7@v3", part.GetProperty("stream").GetString());
        Assert.Equal("fragment-7@v3", part.GetProperty("path").GetString()); // the host names it in errors
        Assert.False(part.TryGetProperty("dv", out _));
    }

    [Fact]
    public void Constants_are_text_and_a_null_constant_stays_null()
    {
        var root = Wire(new ScanPlan
        {
            Parts = new ScanPart[]
            {
                new FilePart("f")
                {
                    Constants = new Dictionary<string, string?> { ["region"] = "eu", ["day"] = "2024-01-02", ["k"] = null },
                },
            },
        });
        var constants = root.GetProperty("files")[0].GetProperty("constants");
        Assert.Equal("eu", constants.GetProperty("region").GetString());
        Assert.Equal("2024-01-02", constants.GetProperty("day").GetString());
        Assert.Equal(JsonValueKind.Null, constants.GetProperty("k").ValueKind);
    }

    [Fact]
    public void Virtual_columns_say_how_and_each_part_says_its_value()
    {
        var root = Wire(new ScanPlan
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
        });
        var rowid = root.GetProperty("virtual").GetProperty("rowid");
        Assert.True(rowid.GetProperty("add_row_number").GetBoolean());
        Assert.False(rowid.TryGetProperty("stored", out _));
        var version = root.GetProperty("virtual").GetProperty("__delta_row_commit_version");
        Assert.Equal("_row-commit-version-col-1", version.GetProperty("stored").GetString());
        Assert.False(version.TryGetProperty("add_row_number", out _)); // false is the default, left out
        var values = root.GetProperty("files")[0].GetProperty("virtual");
        Assert.Equal(1L << 40, values.GetProperty("rowid").GetInt64());
        Assert.Equal(500, values.GetProperty("__delta_row_id").GetInt64());
        Assert.Equal(JsonValueKind.Null, values.GetProperty("__delta_row_commit_version").ValueKind);
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
            Parts = new ScanPart[]
            {
                new FilePart("s3://b/t/a \"q\" \\ \u0001.parquet")
                {
                    Constants = new Dictionary<string, string?> { ["k\"ey"] = "v\nalue" },
                },
            },
        });
        var file = root.GetProperty("files")[0];
        Assert.Equal("s3://b/t/a \"q\" \\ \u0001.parquet", file.GetProperty("path").GetString());
        Assert.Equal("v\nalue", file.GetProperty("constants").GetProperty("k\"ey").GetString());
    }

    [Fact]
    public void Parts_keep_their_order()
    {
        var root = Wire(new ScanPlan
        {
            Parts = new ScanPart[] { new FilePart("b"), new StreamPart("a"), new FilePart("c") },
        });
        Assert.Equal(new[] { "b", "a", "c" },
                     root.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()));
    }
}
