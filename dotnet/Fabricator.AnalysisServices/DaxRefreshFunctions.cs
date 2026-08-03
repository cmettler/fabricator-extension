using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Apache.Arrow;
using Apache.Arrow.Types;
using Fabricator.Bridge;
using Microsoft.AnalysisServices.AdomdClient;

namespace Fabricator.AnalysisServices;

/// <summary>
/// XMLA/TMSL refresh for the semantic model this catalog is attached to: <c>dax_refresh</c>,
/// <c>dax_refresh_table</c> and <c>dax_refresh_partition</c>.
/// </summary>
/// <remarks>
/// <para><b>Why these exist next to <c>fabric.refresh_semantic_model</c>, which already refreshes a model.</b>
/// The Power BI REST enhanced-refresh API answers "refresh this model and tell me when it is done"; it cannot
/// express per-partition work, and its <c>objects</c> list is submitted as one asynchronous request you then
/// poll. TMSL over the XMLA endpoint is the other half: it addresses individual TABLES and PARTITIONS, and
/// <c>sequence</c> lets you bound the parallelism of a batch. The split is deliberate and recorded in
/// docs/fabric-api-functions.md §9f — REST is <c>fabric_*</c>, XMLA is <c>dax_*</c>, and neither grows into the
/// other.</para>
/// <para><b>These are SYNCHRONOUS</b>, which is the biggest practical difference from the REST path: the XMLA
/// command does not return until the refresh has finished, so there is no request id, no polling and no
/// "in-progress" status to misread. A long refresh is therefore a long-running statement — cancellable through
/// the same <see cref="InterruptScope"/> tier-3 mechanism as a DAX scan.</para>
/// <para><b>Refresh is the ONLY TMSL verb exposed, on purpose.</b> The same
/// <c>AdomdCommand.ExecuteNonQuery</c> path would happily run <c>createOrReplace</c> or <c>delete</c>, which
/// would turn a documented read-only provider into an arbitrary model-mutation surface reachable from any SQL
/// string. Refresh moves DATA (which is what a dbt-style flow needs after writing Delta); model authoring stays
/// with the tools that own it. There is deliberately no generic <c>dax_tmsl(command)</c> escape hatch.</para>
/// <para><b>Validation status:</b> the DAX provider's gate (<c>test/verify_dax.test</c>) is MANUAL — it needs a
/// live Analysis Services endpoint (Power BI Desktop or a Fabric/AAS XMLA endpoint), so these functions are
/// wired and reviewed but NOT covered by the automated tiers. See docs/dax-provider.md.</para>
/// </remarks>
internal static class DaxRefreshFunctions
{
    internal static void Register(List<ICatalogTableFunction> tables, DaxCatalog catalog, string schemaName)
    {
        tables.Add(new DaxRefreshFunction(catalog, schemaName));
        tables.Add(new DaxRefreshTableFunction(catalog, schemaName));
        tables.Add(new DaxRefreshPartitionFunction(catalog, schemaName));
    }

    /// <summary>
    /// Maps a user-supplied refresh type onto the TMSL spelling, which is <b>camelCase</b> and NOT the same
    /// vocabulary as the Power BI REST API's (<c>Full</c>, <c>ClearValues</c>, …). Accepting either spelling
    /// case-insensitively means a user who copied a type from <c>fabric.refresh_semantic_model</c> is not
    /// punished for it — and an unknown value is REJECTED rather than passed through, because the engine's own
    /// error for a bad type is a generic XMLA parse failure.
    /// </summary>
    internal static string TmslType(string? requested)
    {
        var t = (requested ?? "full").Trim();
        return t.ToLowerInvariant() switch
        {
            "full" => "full",
            "clearvalues" => "clearValues",
            "calculate" => "calculate",
            "dataonly" => "dataOnly",
            "automatic" => "automatic",
            "add" => "add",
            "defragment" => "defragment",
            _ => throw new ArgumentException(
                $"dax refresh: unknown type '{requested}' — expected one of full, clearValues, calculate, "
                + "dataOnly, automatic, add, defragment."),
        };
    }

    /// <summary>
    /// Builds the TMSL command. <paramref name="objects"/> are (table, partition) pairs; an EMPTY list means
    /// the whole database. With <paramref name="maxParallelism"/> the refresh is wrapped in a <c>sequence</c>,
    /// which is the only way TMSL expresses a parallelism bound.
    /// </summary>
    /// <remarks>
    /// Written with <see cref="Utf8JsonWriter"/> rather than string concatenation so a table or partition name
    /// containing a quote or backslash cannot alter the command's structure — the same reason the SQL side
    /// parameterizes instead of interpolating. Names reach us straight from a SQL literal.
    /// </remarks>
    internal static string BuildRefreshCommand(
        string database, string tmslType, IReadOnlyList<(string Table, string? Partition)> objects,
        long? maxParallelism)
    {
        var buffer = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            if (maxParallelism is > 0)
            {
                w.WriteStartObject("sequence");
                w.WriteNumber("maxParallelism", maxParallelism.Value);
                w.WriteStartArray("operations");
                w.WriteStartObject();
            }
            w.WriteStartObject("refresh");
            w.WriteString("type", tmslType);
            w.WriteStartArray("objects");
            if (objects.Count == 0)
            {
                // No objects named => the whole model. TMSL has no "everything" keyword; the database object IS
                // the whole-model form.
                w.WriteStartObject();
                w.WriteString("database", database);
                w.WriteEndObject();
            }
            foreach (var (table, partition) in objects)
            {
                w.WriteStartObject();
                w.WriteString("database", database);
                w.WriteString("table", table);
                if (!string.IsNullOrEmpty(partition))
                {
                    w.WriteString("partition", partition!);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            if (maxParallelism is > 0)
            {
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Parses the <c>objects_json</c> argument: an array of <c>{"table": "...", "partition": "..."}</c> objects.
    /// A bare string element is accepted as a table name, since that is the shape a caller reaches for first.
    /// </summary>
    internal static IReadOnlyList<(string Table, string? Partition)> ParseObjects(string? json)
    {
        var result = new List<(string, string?)>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }
        using var doc = JsonDocument.Parse(json!);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException(
                "dax refresh: objects_json must be a JSON ARRAY, e.g. "
                + "'[{\"table\":\"Sales\"},{\"table\":\"Sales\",\"partition\":\"2024\"}]'");
        }
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.ValueKind == JsonValueKind.String)
            {
                result.Add((e.GetString()!, null));
                continue;
            }
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("table", out var t)
                || t.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException(
                    "dax refresh: each objects_json element must be a table name, or an object with a "
                    + "\"table\" member (optionally \"partition\").");
            }
            string? partition = e.TryGetProperty("partition", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() : null;
            result.Add((t.GetString()!, partition));
        }
        return result;
    }
}

/// <summary>
/// Base for the three refresh functions: they differ only in their arguments and in which objects they name, so
/// the command execution, the timing and the result row live here.
/// </summary>
internal abstract class DaxRefreshBase : ICatalogTableFunction
{
    // The canonical signature: ONE schema, each field flagged with its style. Explicit so this class
    // may keep declaring the two halves separately (a local shorthand); consumers see the combination.
    Apache.Arrow.Schema Fabricator.Bridge.ITableFunction.Parameters =>
        Fabricator.Bridge.Params.Combine(Parameters, NamedParameters);

    protected DaxRefreshBase(DaxCatalog catalog, string schemaName)
    {
        Catalog = catalog;
        SchemaName = schemaName;
    }

    protected DaxCatalog Catalog { get; }

    public string SchemaName { get; }

    public abstract string Name { get; }

    public virtual Schema Parameters { get; } = new Schema(System.Array.Empty<Field>(), null);

    public abstract Schema NamedParameters { get; }

    /// <summary>The objects to refresh, from this call's arguments. Empty ⇒ the whole model.</summary>
    protected abstract IReadOnlyList<(string Table, string? Partition)> Objects(string?[] args);

    /// <summary>Index of the <c>type</c> argument in the flattened (positional ++ named) argument list.</summary>
    protected abstract int TypeArgIndex { get; }

    /// <summary>Index of a <c>max_parallelism</c> argument, or -1 when this function does not take one.</summary>
    protected virtual int MaxParallelismArgIndex => -1;

    internal static readonly Schema ResultColumns = new(new[]
    {
        new Field("status", StringType.Default, nullable: true),
        new Field("refresh_type", StringType.Default, nullable: true),
        new Field("database", StringType.Default, nullable: true),
        new Field("objects", StringType.Default, nullable: true),
        new Field("duration_ms", Int64Type.Default, nullable: true),
    }, null);

    public IArrowTableFunctionBinding Bind(RecordBatch args)
    {
        // Extract every declared argument WHILE THE BATCH IS VALID: the host disposes the stream it was
        // imported from when table_bind returns, so a binding holding the batch would read freed buffers.
        int n = Parameters.FieldsList.Count + NamedParameters.FieldsList.Count;
        var values = new string?[n];
        for (int i = 0; i < n; i++)
        {
            values[i] = ArgString(args, i);
        }
        return new Binding(this, values);
    }

    // The named `max_parallelism` arrives as a BIGINT, so it must not go through a string-only reader.
    private static string? ArgString(RecordBatch? args, int col)
    {
        if (args is null || col >= args.ColumnCount || args.Length == 0)
        {
            return null;
        }
        return args.Column(col) switch
        {
            StringArray s => s.IsNull(0) ? null : s.GetString(0),
            LargeStringArray ls => ls.IsNull(0) ? null : ls.GetString(0),
            StringViewArray sv => sv.IsNull(0) ? null : sv.GetString(0),
            Int64Array i64 => i64.IsNull(0) ? null : i64.GetValue(0).ToString(),
            Int32Array i32 => i32.IsNull(0) ? null : i32.GetValue(0).ToString(),
            _ => null,
        };
    }

    private sealed class Binding : IArrowTableFunctionBinding
    {
        private readonly DaxRefreshBase _fn;
        private readonly string?[] _args;

        internal Binding(DaxRefreshBase fn, string?[] args)
        {
            _fn = fn;
            _args = args;
        }

        public Schema OutputSchema => ResultColumns;

        public bool SupportsPushdown => false;

        public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
        {
            // Dispose the pushed filter values in this PLAIN method, before the iterator body can run: the
            // producer is owned by the scan's global state and an async-iterator body starts inside get_next,
            // long after InitGlobal returned (the documented late-release use-after-free).
            scan.FilterValues?.Dispose();
            return Run(ct);
        }

        private async IAsyncEnumerable<RecordBatch> Run(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await System.Threading.Tasks.Task.CompletedTask;
            var objects = _fn.Objects(_args);
            string tmslType = DaxRefreshFunctions.TmslType(_args.Length > _fn.TypeArgIndex && _fn.TypeArgIndex >= 0
                ? _args[_fn.TypeArgIndex] : null);
            long? parallelism = null;
            if (_fn.MaxParallelismArgIndex >= 0 && _fn.MaxParallelismArgIndex < _args.Length
                && long.TryParse(_args[_fn.MaxParallelismArgIndex], out var p))
            {
                parallelism = p;
            }
            string database = _fn.Catalog.DatabaseName;
            string command = DaxRefreshFunctions.BuildRefreshCommand(database, tmslType, objects, parallelism);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            _fn.Catalog.ExecuteTmsl(command, ct);
            watch.Stop();

            var summary = objects.Count == 0
                ? "database:" + database
                : string.Join(", ", System.Linq.Enumerable.Select(objects,
                    o => o.Partition is null ? o.Table : $"{o.Table}[{o.Partition}]"));
            var columns = new IArrowArray[]
            {
                Str("Completed"), Str(tmslType), Str(database), Str(summary),
                new Int64Array.Builder().Append(watch.ElapsedMilliseconds).Build(),
            };
            yield return new RecordBatch(ResultColumns, columns, 1);
        }

        private static IArrowArray Str(string? v)
        {
            var b = new StringArray.Builder();
            b.Append(v);
            return b.Build();
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// <c>dax_refresh([type := 'full'] [, objects_json := …] [, max_parallelism := …])</c> — refreshes the whole
/// model, or exactly the tables/partitions named in <c>objects_json</c>.
/// </summary>
internal sealed class DaxRefreshFunction : DaxRefreshBase
{
    internal DaxRefreshFunction(DaxCatalog catalog, string schemaName) : base(catalog, schemaName)
    {
    }

    public override string Name => "dax_refresh";

    public override Schema NamedParameters { get; } = new(new[]
    {
        new Field("type", StringType.Default, nullable: true),
        new Field("objects_json", StringType.Default, nullable: true),
        new Field("max_parallelism", Int64Type.Default, nullable: true),
    }, null);

    protected override int TypeArgIndex => 0;

    protected override int MaxParallelismArgIndex => 2;

    protected override IReadOnlyList<(string Table, string? Partition)> Objects(string?[] args) =>
        DaxRefreshFunctions.ParseObjects(args.Length > 1 ? args[1] : null);
}

/// <summary><c>dax_refresh_table(table [, type := 'full'])</c> — the single-table convenience.</summary>
internal sealed class DaxRefreshTableFunction : DaxRefreshBase
{
    internal DaxRefreshTableFunction(DaxCatalog catalog, string schemaName) : base(catalog, schemaName)
    {
    }

    public override string Name => "dax_refresh_table";

    public override Schema Parameters { get; } = new(new[]
    {
        new Field("table", StringType.Default, nullable: false),
    }, null);

    public override Schema NamedParameters { get; } = new(new[]
    {
        new Field("type", StringType.Default, nullable: true),
    }, null);

    protected override int TypeArgIndex => 1;

    protected override IReadOnlyList<(string Table, string? Partition)> Objects(string?[] args)
    {
        var table = args.Length > 0 ? args[0] : null;
        if (string.IsNullOrWhiteSpace(table))
        {
            throw new ArgumentException("dax_refresh_table: a table name is required.");
        }
        return new[] { (table!, (string?)null) };
    }
}

/// <summary>
/// <c>dax_refresh_partition(table, partition [, type := 'full'])</c> — the operation the REST API cannot
/// express at all, and the reason this file exists.
/// </summary>
internal sealed class DaxRefreshPartitionFunction : DaxRefreshBase
{
    internal DaxRefreshPartitionFunction(DaxCatalog catalog, string schemaName) : base(catalog, schemaName)
    {
    }

    public override string Name => "dax_refresh_partition";

    public override Schema Parameters { get; } = new(new[]
    {
        new Field("table", StringType.Default, nullable: false),
        new Field("partition", StringType.Default, nullable: false),
    }, null);

    public override Schema NamedParameters { get; } = new(new[]
    {
        new Field("type", StringType.Default, nullable: true),
    }, null);

    protected override int TypeArgIndex => 2;

    protected override IReadOnlyList<(string Table, string? Partition)> Objects(string?[] args)
    {
        var table = args.Length > 0 ? args[0] : null;
        var partition = args.Length > 1 ? args[1] : null;
        if (string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(partition))
        {
            throw new ArgumentException("dax_refresh_partition: both a table and a partition name are required.");
        }
        return new (string, string?)[] { (table!, partition) };
    }
}
