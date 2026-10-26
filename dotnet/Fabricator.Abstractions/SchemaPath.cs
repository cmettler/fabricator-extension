// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Fabricator.Bridge;

/// <summary>
/// Where an object lives in a catalog: its schema as a PATH, outermost segment first. <c>["a","b"]</c> is schema
/// <c>b</c> nested inside <c>a</c> (DuckDB 2.0 nested schemas); a one-segment path is an ordinary top-level schema,
/// which is what most providers have.
/// </summary>
/// <remarks>
/// <para>Every object a provider declares or is asked about — a table, a function of any kind, a macro, a view —
/// is addressed by one of these. A leaf NAME is not an address once schemas nest: the same name can sit in two
/// branches, and resolving by it reads or writes whichever one a lookup happens to find first.</para>
/// <para>⚠ Structural rather than a dotted string ON PURPOSE: a schema name may contain a dot, so any encoding
/// would need escaping at every boundary. <see cref="ToString"/> renders a quoted form for MESSAGES only.</para>
/// <para>A string converts implicitly to a one-segment path, so the common case stays
/// <c>new ViewDefinition("main", …)</c>. Equality is ordinal and segment by segment.</para>
/// </remarks>
public sealed class SchemaPath : IReadOnlyList<string>, IEquatable<SchemaPath>
{
    private readonly string[] _segments;

    /// <summary>A path from its segments, outermost first. Refuses an empty path or an empty segment.</summary>
    public SchemaPath(IEnumerable<string> segments)
    {
        _segments = segments?.ToArray() ?? throw new ArgumentNullException(nameof(segments));
        if (_segments.Length == 0)
        {
            throw new ArgumentException("a schema path needs at least one segment", nameof(segments));
        }
        foreach (var s in _segments)
        {
            if (string.IsNullOrEmpty(s))
            {
                throw new ArgumentException("a schema path segment must not be empty", nameof(segments));
            }
        }
    }

    /// <summary>A path from its segments, outermost first.</summary>
    public SchemaPath(params string[] segments) : this((IEnumerable<string>)segments)
    {
    }

    /// <summary>A one-segment (top-level) schema.</summary>
    public static implicit operator SchemaPath(string name) => new(new[] { name });

    /// <summary>The innermost segment: the schema's own name.</summary>
    public string Leaf => _segments[_segments.Length - 1];

    /// <summary>The number of segments; 1 for a top-level schema.</summary>
    public int Depth => _segments.Length;

    /// <summary>This path with <paramref name="segment"/> appended — a child schema.</summary>
    public SchemaPath Child(string segment) => new(_segments.Append(segment));

    public string this[int index] => _segments[index];

    public int Count => _segments.Length;

    public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)_segments).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _segments.GetEnumerator();

    public bool Equals(SchemaPath? other) =>
        other is not null && _segments.AsSpan().SequenceEqual(other._segments, StringComparer.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as SchemaPath);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var s in _segments)
        {
            hash.Add(s, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }

    public static bool operator ==(SchemaPath? a, SchemaPath? b) => a is null ? b is null : a.Equals(b);

    public static bool operator !=(SchemaPath? a, SchemaPath? b) => !(a == b);

    /// <summary>The path as <c>"a"."b"</c>, for messages. Not a key and not parseable back.</summary>
    public override string ToString() => string.Join(".", _segments.Select(s => "\"" + s.Replace("\"", "\"\"") + "\""));
}
