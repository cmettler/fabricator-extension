// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

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
    // The canonical signature: ONE schema, each field flagged with its style. Explicit so this class
    // may keep declaring the two halves separately (a local shorthand); consumers see the combination.
    Apache.Arrow.Schema Fabricator.Bridge.ITableFunction.Parameters =>
        Fabricator.Bridge.Params.Combine(Parameters, NamedParameters);

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
    public ITableFunctionBinding Bind(RecordBatch args) =>
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

        /// <remarks>
        /// Built through <see cref="FabricRowBuilder"/> deliberately, even though two string columns need none of
        /// its typing: this function is the only one on this path with a HERMETIC gate
        /// (test/verify_delta_catalog_functions.test), and the builder is what every Fabric REST read now
        /// produces its rows with. Routing this through it means a regression in the shared builder — including
        /// the empty-result case, which §6 of that suite exercises — fails the offline tier instead of only
        /// surfacing on a live tenant call.
        /// </remarks>
        protected override IAsyncEnumerable<RecordBatch> Rows(CancellationToken ct)
        {
            var row = new FabricRowBuilder(Columns);
            foreach (var kv in _facts)
            {
                if (_property is not null
                    && !string.Equals(kv.Key, _property, System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                row.Str(0, kv.Key).Str(1, kv.Value).EndRow();
            }
            return One(row.Build());
        }
    }
}
