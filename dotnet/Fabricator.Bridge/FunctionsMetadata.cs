// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Generic;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// Builds the <c>catalog_functions</c> discovery stream from a provider's <see cref="FunctionDeclaration"/>s. Every
/// provider's declarations cross through here since ABI v97, so the wire shape lives in ONE place: a
/// <c>LIST&lt;VARCHAR&gt;</c> schema path, then name, kind, param_count, return_type and varargs. The host reads the
/// path, the name and the kind (see <c>DiscoverFunctions</c> in <c>src/catalog/fabricator_metadata.cpp</c>);
/// <c>fabricator_functions()</c> shows all six.
/// </summary>
public static class FunctionsMetadata
{
    /// <summary>The Arrow schema of the kind-6 stream, in the host's column order.</summary>
    public static Schema StreamSchema { get; } = new Schema.Builder()
        .Field(SchemaPathColumn.Field("schema_path"))
        .Field(new Field("name", StringType.Default, nullable: true))
        .Field(new Field("kind", StringType.Default, nullable: true))
        .Field(new Field("param_count", Int32Type.Default, nullable: true))
        .Field(new Field("return_type", StringType.Default, nullable: true))
        .Field(new Field("varargs", StringType.Default, nullable: true))
        .Build();

    /// <summary>
    /// Streams <paramref name="declarations"/> as the kind-6 table. An empty sequence yields a zero-row stream of
    /// the FULL three-column schema — deliberately, and it matters: the host's width check is per BATCH and only
    /// when <c>length &gt; 0</c>, so a 1-column empty fallback also passes today, but emitting the real schema is
    /// what makes the stream self-describing if that leniency is ever tightened.
    /// </summary>
    public static IArrowArrayStream Stream(IEnumerable<FunctionDeclaration>? declarations)
    {
        var schemas = new SchemaPathColumn();
        var names = new StringArray.Builder();
        var kinds = new StringArray.Builder();
        var counts = new Int32Array.Builder();
        var returns = new StringArray.Builder();
        var varargs = new StringArray.Builder();
        int n = 0;
        foreach (var d in declarations ?? System.Array.Empty<FunctionDeclaration>())
        {
            schemas.Append(d.SchemaPath);
            names.Append(d.Name);
            kinds.Append(d.Kind);
            counts.Append(d.ParamCount);
            returns.Append(d.ReturnType);
            varargs.Append(d.VarArgs);
            n++;
        }
        if (n == 0)
        {
            return new InMemoryArrayStream(StreamSchema, System.Array.Empty<RecordBatch>());
        }
        var batch = new RecordBatch(
            StreamSchema,
            new IArrowArray[]
            {
                schemas.Build(), names.Build(), kinds.Build(), counts.Build(), returns.Build(), varargs.Build(),
            },
            n);
        return new InMemoryArrayStream(StreamSchema, new[] { batch });
    }
}
