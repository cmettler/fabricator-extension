// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fabricator.Bridge;

/// <summary>
/// The spec of one predicate DML statement (ABI v94 <c>table_dml_where</c>):
/// <c>{ "filter": &lt;FilterNode&gt;, "set": [ { "col": "n", "value": &lt;ValueNode&gt; } ] }</c>.
/// A spec WITH <c>set</c> is an UPDATE, one without is a DELETE; an absent <c>filter</c> means every row.
///
/// <para>⚠ Unlike a scan's <see cref="ScanSpec.Filter"/>, every tree here is EXACT: nothing re-applies a write,
/// so the host sends only what rendered in full, and a provider must render every node it receives or throw —
/// never drop one.</para>
/// </summary>
public sealed class DmlSpec
{
    [JsonPropertyName("filter")]
    public FilterNode? Filter { get; set; }

    [JsonPropertyName("set")]
    public List<SetAssignment>? Set { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static DmlSpec Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new DmlSpec() : JsonSerializer.Deserialize<DmlSpec>(json, Options) ?? new DmlSpec();
}

/// <summary>One <c>SET col = value</c> of a predicate UPDATE.</summary>
public sealed class SetAssignment
{
    [JsonPropertyName("col")]
    public string Col { get; set; } = "";

    [JsonPropertyName("value")]
    public ValueNode Value { get; set; } = new();
}

/// <summary>
/// A SET value, discriminated by <c>op</c>:
/// <list type="bullet">
/// <item><c>const</c> — <c>val</c>, an index into the constant batch (shared with the filter)</item>
/// <item><c>col</c> — <c>col</c>, the row's own value of that column</item>
/// <item><c>+</c> / <c>-</c> / <c>*</c> — two <c>children</c>, INTEGER operands only (the host refuses
/// decimal, temporal and division, whose semantics differ between engines)</item>
/// </list>
/// </summary>
public sealed class ValueNode
{
    [JsonPropertyName("op")]
    public string Op { get; set; } = "";

    [JsonPropertyName("val")]
    public int? Val { get; set; }

    [JsonPropertyName("col")]
    public string? Col { get; set; }

    [JsonPropertyName("children")]
    public List<ValueNode>? Children { get; set; }
}
