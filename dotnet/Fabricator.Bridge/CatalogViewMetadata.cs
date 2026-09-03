// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Generic;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// Builds the <c>catalog_views</c> discovery stream (ABI v77): three UTF-8 columns (<c>schema</c>,
/// <c>name</c>, <c>create_sql</c>) the host reads to bind provider-declared views into an ATTACHed catalog's
/// schemas.
///
/// <para>Shared by every provider that declares <see cref="IProvider.CatalogViews"/>. Its own entry rather
/// than a column on the functions stream for the same reason <see cref="CatalogMacroMetadata"/> has one: a
/// view body is a purely LOCAL declaration, so it must not be assembled into provider SQL and executed on a
/// server. A provider with no SQL engine, or one whose server is unreachable, can still declare views.</para>
/// </summary>
public static class CatalogViewMetadata
{
    /// <summary>The Arrow schema of the stream. Column ORDER is the contract with the host.</summary>
    public static Schema StreamSchema { get; } = new Schema.Builder()
        .Field(new Field("schema", StringType.Default, nullable: true))
        .Field(new Field("name", StringType.Default, nullable: true))
        .Field(new Field("create_sql", StringType.Default, nullable: true))
        .Build();

    /// <summary>
    /// Streams <paramref name="views"/>. An empty (or null) sequence yields a zero-row stream rather than a
    /// fault — "this provider declares no catalog views" is the ordinary case, not an error.
    /// </summary>
    public static IArrowArrayStream Stream(IEnumerable<ViewDefinition>? views)
    {
        var schemas = new StringArray.Builder();
        var names = new StringArray.Builder();
        var bodies = new StringArray.Builder();
        int n = 0;
        foreach (var v in views ?? System.Array.Empty<ViewDefinition>())
        {
            schemas.Append(v.SchemaName);
            names.Append(v.Name);
            bodies.Append(v.CreateSql);
            n++;
        }
        if (n == 0)
        {
            return new InMemoryArrayStream(StreamSchema, System.Array.Empty<RecordBatch>());
        }
        var batch = new RecordBatch(StreamSchema,
                                    new IArrowArray[] { schemas.Build(), names.Build(), bodies.Build() }, n);
        return new InMemoryArrayStream(StreamSchema, new[] { batch });
    }
}
