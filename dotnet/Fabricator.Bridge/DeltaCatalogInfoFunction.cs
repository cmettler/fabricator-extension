using System.Collections.Generic;
using System.Threading;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// <c>db.&lt;schema&gt;.fab_delta_info()</c> — one (property, value) row per attach-level fact: the normalized
/// root, the provider spelling's engine defaults, and the discovered schema count.
/// </summary>
/// <remarks>
/// <para>Useful in its own right (the effective root after credential-marker stripping and normalization, and
/// which engine a given provider spelling selected, are otherwise invisible from SQL — the analog of
/// <c>fabricator_server_info</c> for a Delta attach).</para>
/// <para>It is also the smallest thing that proves what the catalog-bound form is FOR: the function captures
/// catalog context, so the caller passes no arguments at all. A macro cannot do that (its body would have to
/// name the root literally) and a global function cannot either (it has no catalog).</para>
/// <para><b>Zero-argument on purpose</b>, and that is load-bearing: it is the shape the Fabric REST functions
/// need, and it only works because <see cref="ArrowSchemaExport"/> handles the empty parameter schema that
/// Apache.Arrow itself cannot export. A regression there makes this function silently vanish.</para>
/// </remarks>
internal sealed class DeltaCatalogInfoFunction : ICatalogTableFunction
{
    private readonly IReadOnlyList<KeyValuePair<string, string>> _facts;

    internal DeltaCatalogInfoFunction(IReadOnlyList<KeyValuePair<string, string>> facts) => _facts = facts;

    public string SchemaName => CatalogFunctionSet.AllSchemas;
    public string Name => "fab_delta_info";

    public Schema Parameters { get; } = new Schema(System.Array.Empty<Field>(), null);

    /// <summary>
    /// <c>fab_delta_info(property := 'root')</c> — return just that property. Optional, hence a DuckDB NAMED
    /// parameter: a positional one would force every caller to write <c>fab_delta_info(NULL)</c>.
    /// </summary>
    public Schema NamedParameters { get; } =
        new Schema(new[] { new Field("property", StringType.Default, nullable: true) }, null);

    // Position 0 is the first NamedParameters field (Parameters is empty here); an omitted named argument
    // arrives as NULL.
    public IArrowTableFunctionBinding Bind(RecordBatch args) =>
        new Binding(_facts, FabricArgs.Str(args, 0));

    private sealed class Binding : FabricTableBinding
    {
        private static readonly Schema Columns = new(new[]
        {
            new Field("property", StringType.Default, nullable: true),
            new Field("value", StringType.Default, nullable: true),
        }, null);

        private readonly IReadOnlyList<KeyValuePair<string, string>> _facts;
        private readonly string? _property;

        internal Binding(IReadOnlyList<KeyValuePair<string, string>> facts, string? property)
        {
            _facts = facts;
            _property = property;
        }

        public override Schema OutputSchema => Columns;

        protected override IAsyncEnumerable<RecordBatch> Rows(CancellationToken ct)
        {
            var props = new StringArray.Builder();
            var values = new StringArray.Builder();
            int n = 0;
            foreach (var kv in _facts)
            {
                if (_property is not null
                    && !string.Equals(kv.Key, _property, System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                props.Append(kv.Key);
                values.Append(kv.Value);
                n++;
            }
            return One(Columns, new IArrowArray[] { props.Build(), values.Build() }, n);
        }
    }
}
