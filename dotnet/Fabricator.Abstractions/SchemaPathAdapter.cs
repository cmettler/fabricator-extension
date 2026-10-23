// Copyright (c) fabricator. All rights reserved.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

// The assembly is Fabricator.Abstractions; the NAMESPACE is Fabricator.Bridge, as for every type here
// (the 2026-08-18 split moved assemblies, not namespaces).
namespace Fabricator.Bridge;

/// <summary>
/// Widens a catalog-discovery stream's leading UTF-8 schema column into a <c>LIST&lt;VARCHAR&gt;</c>
/// schema PATH, one element per row.
/// </summary>
/// <remarks>
/// <para>This exists so DuckDB 2.0 nested schemas could be put on the wire WITHOUT breaking a provider
/// that has never heard of them. <see cref="IProviderCatalog.GetSchemaPaths"/> and
/// <see cref="IProviderCatalog.GetTablePaths"/> default to calling it over the flat members, so an
/// un-migrated provider — including the four OUT-OF-TREE PLUGINS, which pin this assembly by SHA and
/// cannot be edited from here — keeps emitting exactly what it always did, just shaped as depth-1 paths.
/// </para>
/// <para>⚠ It is a STREAMING wrapper, not a materialising one: catalog enumeration is already the
/// expensive path on a remote root (a Delta ATTACH globs the whole tree), so collecting every batch to
/// rewrite one column would add a second full pass over the discovery result. Each batch is converted as
/// it arrives and the source's own batch boundaries are preserved.</para>
/// <para>⚠ The source stream is DISPOSED with this one. The ABI's rule is that whoever receives a stream
/// consumes and releases it, and the caller never sees the inner stream to release it itself.</para>
/// </remarks>
public static class SchemaPathAdapter
{
    /// <summary>
    /// Returns <paramref name="source"/> with column 0 — which must be UTF-8 — replaced by a
    /// <c>LIST&lt;VARCHAR&gt;</c> column named <paramref name="name"/> holding a single-element path per
    /// row. A NULL name yields a NULL path rather than a one-element list of NULL, so "no schema" stays
    /// distinguishable from "a schema whose name is unknown".
    /// </summary>
    public static IArrowArrayStream WidenFirstColumn(IArrowArrayStream source, string name)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }
        return new Widened(source, name);
    }

    /// <summary>
    /// Builds a discovery stream whose FIRST column is a <c>LIST&lt;VARCHAR&gt;</c> schema path and whose
    /// remaining columns are UTF-8 — the shape <c>catalog_schemas</c> / <c>catalog_tables</c> carry since
    /// ABI v90, built from REAL paths rather than widened from flat names.
    /// </summary>
    /// <remarks>⚠ For a provider that genuinely nests. One that does not should leave
    /// <see cref="IProviderCatalog.GetSchemaPaths"/> / <see cref="IProviderCatalog.GetTablePaths"/> alone
    /// and let the default widen its flat stream; there is nothing to gain from building the same depth-1
    /// paths by hand. Every column must have the same length as <paramref name="paths"/>.</remarks>
    public static IArrowArrayStream PathTable(string pathName, IReadOnlyList<IReadOnlyList<string>> paths,
                                              params (string Name, IReadOnlyList<string> Values)[] columns)
    {
        var fields = new List<Field>(columns.Length + 1)
        {
            new(pathName, new ListType(new Field("item", StringType.Default, nullable: true)), nullable: true),
        };
        var pathBuilder = new ListArray.Builder(StringType.Default);
        var elements = (StringArray.Builder)pathBuilder.ValueBuilder;
        foreach (var path in paths)
        {
            pathBuilder.Append();
            foreach (var segment in path)
            {
                elements.Append(segment);
            }
        }
        var arrays = new List<IArrowArray>(columns.Length + 1) { pathBuilder.Build() };
        foreach (var (name, values) in columns)
        {
            if (values.Count != paths.Count)
            {
                throw new ArgumentException(
                    $"SchemaPathAdapter.PathTable: column '{name}' has {values.Count} values for " +
                    $"{paths.Count} paths", nameof(columns));
            }
            fields.Add(new Field(name, StringType.Default, nullable: true));
            var b = new StringArray.Builder();
            foreach (var v in values)
            {
                b.Append(v);
            }
            arrays.Add(b.Build());
        }
        var schema = new Schema(fields, null);
        return new OneBatch(schema, new RecordBatch(schema, arrays, paths.Count));
    }

    private sealed class OneBatch : IArrowArrayStream
    {
        private RecordBatch? _batch;

        public OneBatch(Schema schema, RecordBatch batch)
        {
            Schema = schema;
            _batch = batch;
        }

        public Schema Schema { get; }

        public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken ct = default)
        {
            var b = _batch;
            _batch = null;
            return new ValueTask<RecordBatch?>(b);
        }

        // The batch is handed to the CONSUMER on the first read, so disposing here would free arrays it
        // still owns — the same ownership rule the Widened wrapper's big warning records. Only an
        // UNCONSUMED batch is ours to drop.
        public void Dispose()
        {
            _batch?.Dispose();
            _batch = null;
        }
    }

    private sealed class Widened : IArrowArrayStream
    {
        private readonly IArrowArrayStream _source;
        private readonly string _name;

        public Widened(IArrowArrayStream source, string name)
        {
            _source = source;
            _name = name;
            var src = source.Schema;
            if (src.FieldsList.Count == 0 || src.FieldsList[0].DataType.TypeId != ArrowTypeId.String)
            {
                throw new InvalidOperationException(
                    "SchemaPathAdapter: column 0 must be a UTF-8 schema name; the stream declares " +
                    (src.FieldsList.Count == 0 ? "no columns" : src.FieldsList[0].DataType.ToString()));
            }
            var fields = new List<Field>(src.FieldsList.Count)
            {
                new(_name, new ListType(new Field("item", StringType.Default, nullable: true)), nullable: true),
            };
            for (int i = 1; i < src.FieldsList.Count; i++)
            {
                fields.Add(src.FieldsList[i]);
            }
            Schema = new Schema(fields, src.Metadata);
        }

        public Schema Schema { get; }

        public async ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken ct = default)
        {
            var batch = await _source.ReadNextRecordBatchAsync(ct).ConfigureAwait(false);
            if (batch is null)
            {
                return null;
            }
            // ⚠⚠ THE SOURCE BATCH IS NOT DISPOSED HERE, AND THAT IS NOT AN OVERSIGHT. Columns 1.. are handed
            // to the new RecordBatch BY REFERENCE, and RecordBatch.Dispose() disposes its columns — so
            // disposing the source too would free arrays the returned batch still points at. MEASURED as
            // exactly that: with a `using` here, catalog_schemas (ONE column, whose only column is freshly
            // built) worked while catalog_tables (THREE) failed with "failed to read metadata batch" on 69
            // of 82 hermetic suites. Ownership of the shared columns passes to the batch we return.
            {
                var names = (StringArray)batch.Column(0);
                var builder = new ListArray.Builder(StringType.Default);
                var values = (StringArray.Builder)builder.ValueBuilder;
                for (int i = 0; i < batch.Length; i++)
                {
                    if (names.IsNull(i))
                    {
                        builder.AppendNull(); // a NULL name is NO path, not a path of one NULL
                        continue;
                    }
                    builder.Append();
                    values.Append(names.GetString(i));
                }
                var columns = new List<IArrowArray>(batch.ColumnCount) { builder.Build() };
                for (int i = 1; i < batch.ColumnCount; i++)
                {
                    columns.Add(batch.Column(i));
                }
                return new RecordBatch(Schema, columns, batch.Length);
            }
        }

        public void Dispose() => _source.Dispose();
    }
}
