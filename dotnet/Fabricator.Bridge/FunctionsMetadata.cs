using System.Collections.Generic;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// Builds the <c>MetadataKind.Functions</c> (kind 6) discovery stream IN MEMORY, for a provider that has no SQL
/// engine to assemble it with.
///
/// <para>The SQL-Server catalog produces this same stream as T-SQL executed on the server (its discovered
/// routines <c>UNION ALL</c> literal rows for the custom registries). A Delta/DAX-style catalog has no server to
/// run that on, so it needs this: the identical column contract, built locally. Column ORDER is the contract —
/// the host reads the first three string columns (<c>schema_name</c>, <c>name</c>, <c>kind</c>) and ignores any
/// trailing ones (see <c>DiscoverFunctions</c> in <c>src/catalog/fabricator_metadata.cpp</c>).</para>
///
/// <para><c>kind</c> must be one of the strings the host's registration switch knows —
/// <c>scalar</c> / <c>table</c> / <c>proc</c> / <c>table_sql</c> / <c>inout</c> / <c>collector</c> /
/// <c>aggregate</c> / <c>aggregate_spill</c> — anything else is silently ignored there, which is a very quiet
/// way to lose a function.</para>
/// </summary>
public static class FunctionsMetadata
{
    /// <summary>One declaration row: which schema it binds into, its name, and its host registration kind.</summary>
    /// <remarks>
    /// <see cref="ParamCount"/> and <see cref="ReturnType"/> are NOT part of the three columns this class
    /// streams — the host ignores them. They exist because the SQL-Server catalog assembles the SAME
    /// declarations as a T-SQL <c>UNION ALL</c> against its discovered routines, whose shape is five columns
    /// wide, so every branch must supply all five. Carrying them here is what lets ONE producer
    /// (<see cref="CatalogFunctionSet.Declarations"/>) feed both the in-memory stream and that SQL.
    /// </remarks>
    public readonly struct Declaration
    {
        public Declaration(string schemaName, string name, string kind, int paramCount = 0,
                           string returnType = "")
        {
            SchemaName = schemaName;
            Name = name;
            Kind = kind;
            ParamCount = paramCount;
            ReturnType = returnType;
        }

        public string SchemaName { get; }
        public string Name { get; }
        public string Kind { get; }

        /// <summary>Declared argument count (positional ++ named), or the input-table width for in-out kinds.</summary>
        public int ParamCount { get; }

        /// <summary>Arrow type name of a scalar/aggregate result; empty for every other kind.</summary>
        public string ReturnType { get; }
    }

    /// <summary>The Arrow schema of the kind-6 stream, in the host's column order.</summary>
    public static Schema StreamSchema { get; } = new Schema.Builder()
        .Field(new Field("schema_name", StringType.Default, nullable: true))
        .Field(new Field("name", StringType.Default, nullable: true))
        .Field(new Field("kind", StringType.Default, nullable: true))
        .Build();

    /// <summary>
    /// Streams <paramref name="declarations"/> as the kind-6 table. An empty sequence yields a zero-row stream of
    /// the FULL three-column schema — deliberately, and it matters: the host's width check is per BATCH and only
    /// when <c>length &gt; 0</c>, so a 1-column empty fallback also passes today, but emitting the real schema is
    /// what makes the stream self-describing if that leniency is ever tightened.
    /// </summary>
    public static IArrowArrayStream Stream(IEnumerable<Declaration>? declarations)
    {
        var schemas = new StringArray.Builder();
        var names = new StringArray.Builder();
        var kinds = new StringArray.Builder();
        int n = 0;
        foreach (var d in declarations ?? System.Array.Empty<Declaration>())
        {
            schemas.Append(d.SchemaName);
            names.Append(d.Name);
            kinds.Append(d.Kind);
            n++;
        }
        if (n == 0)
        {
            return new InMemoryArrayStream(StreamSchema, System.Array.Empty<RecordBatch>());
        }
        var batch = new RecordBatch(StreamSchema,
                                    new IArrowArray[] { schemas.Build(), names.Build(), kinds.Build() }, n);
        return new InMemoryArrayStream(StreamSchema, new[] { batch });
    }
}
