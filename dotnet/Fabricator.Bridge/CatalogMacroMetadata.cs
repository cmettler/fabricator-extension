using System.Collections.Generic;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// Builds the <c>MetadataKind.CatalogMacros</c> (kind 15) stream: three UTF-8 columns
/// (<c>schema</c>, <c>name</c>, <c>create_sql</c>) the host reads to bind provider-declared macros into an
/// ATTACHed catalog's schemas.
///
/// <para>Shared by every provider that declares <see cref="IBackend.CatalogMacros"/> — the point of a separate
/// metadata kind is that a macro body is a purely LOCAL declaration, so it must not be assembled into provider
/// SQL and executed on a server (which is how the <c>Functions</c> discovery stream works). That also means a
/// provider with no SQL engine, and a provider whose server is unreachable, can still declare macros.</para>
/// </summary>
public static class CatalogMacroMetadata
{
    /// <summary>The Arrow schema of the kind-15 stream. Column ORDER is the contract with the host.</summary>
    public static Schema StreamSchema { get; } = new Schema.Builder()
        .Field(new Field("schema", StringType.Default, nullable: true))
        .Field(new Field("name", StringType.Default, nullable: true))
        .Field(new Field("create_sql", StringType.Default, nullable: true))
        .Build();

    /// <summary>
    /// Streams <paramref name="macros"/> as the kind-15 table. An empty (or null) sequence yields a zero-row
    /// stream rather than a fault — "this provider declares no catalog macros" is the ordinary case, not an
    /// error.
    /// </summary>
    public static IArrowArrayStream Stream(IEnumerable<CatalogMacroDefinition>? macros)
    {
        var schemas = new StringArray.Builder();
        var names = new StringArray.Builder();
        var bodies = new StringArray.Builder();
        int n = 0;
        foreach (var m in macros ?? System.Array.Empty<CatalogMacroDefinition>())
        {
            schemas.Append(m.SchemaName);
            names.Append(m.Name);
            bodies.Append(m.CreateSql);
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
