using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace ArrowNet.Bridge;

/// <summary>
/// The Delta Lake provider (the 3rd <see cref="IBackend"/>, after SQL Server and DAX): a Delta <b>folder</b>
/// is an ATTACH-able catalog root — <c>ATTACH '/lake' AS lake (TYPE arrownet, PROVIDER 'delta')</c> (or an
/// <c>abfss://…</c> OneLake/ADLS prefix). Each immediate subdirectory containing a <c>_delta_log/</c> is a
/// table under a single flat <c>main</c> schema. Connection-free: all IO goes through DuckDB's FileSystem via
/// the host callbacks (so local / az:// / s3:// + DuckDB secrets all work), reusing <see cref="DeltaReader"/>.
/// Read + INSERT/CTAS (write) reuse the provider-agnostic C++ catalog machinery; this slice is READ-only
/// (writes throw). See docs/delta-catalog.md.
/// </summary>
public sealed class DeltaBackend : IBackend
{
    public string Name => "delta";

    public IEnumerable<string> Aliases => new[] { "deltalake" };

    // Delta has no provider secret (cloud auth is via DuckDB FS secrets); the connstr IS the folder root.
    public string BuildConnectionString(
        string secretType, IReadOnlyDictionary<string, string> fields, string baseConnString) => baseConnString;

    public IBackendCatalog OpenCatalog(string connectionString, string optionsJson) =>
        new DeltaCatalog(connectionString);
}

/// <summary>An ATTACH'd Delta folder catalog. Lazy: holds the root path; all FS access happens during metadata
/// discovery / scan, using the active host-FS opener (<see cref="AmbientOpener"/>, set by the host before each
/// catalog metadata + scan call). Read-only in this slice.</summary>
public sealed class DeltaCatalog : IBackendCatalog
{
    private const string MainSchema = "main";
    private readonly string _root; // normalized (forward slashes), no trailing slash

    public DeltaCatalog(string root) => _root = Normalize(root).TrimEnd('/');

    private static string Normalize(string p) => p.Replace('\\', '/');

    private string TablePath(string table) => _root + "/" + table;

    public IArrowArrayStream GetMetadata(int kind, string? schema, string? table) => kind switch
    {
        MetadataKind.Schemas => SingleColumn("schema_name", new[] { MainSchema }),
        MetadataKind.Tables => DiscoverTables(),
        // Columns = a zero-row stream whose SCHEMA describes the table's columns (engineered-wood's Delta schema).
        MetadataKind.Columns => new InMemoryArrayStream(
            DeltaReader.GetSchema(AmbientOpener.Current, TablePath(table!)), System.Array.Empty<RecordBatch>()),
        // No rowid (no UPDATE/DELETE yet), no row-count/NDV stats surfaced, no functions.
        _ => EmptyStringTable("name"),
    };

    /// <summary>Discovers tables = immediate subdirs of the root containing a <c>_delta_log/</c>. Globs the
    /// commit files (<c>&lt;root&gt;/*/_delta_log/*.json</c>) and takes the distinct parent-of-_delta_log
    /// directory name as the table.</summary>
    private IArrowArrayStream DiscoverTables()
    {
        var names = new SortedSet<string>(System.StringComparer.Ordinal);
        var json = HostFs.Glob(AmbientOpener.Current, _root + "/*/_delta_log/*.json");
        using var doc = JsonDocument.Parse(json);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var path = Normalize(el.GetProperty("path").GetString() ?? string.Empty);
            // …/<table>/_delta_log/<file>.json  →  the segment before "/_delta_log/".
            int marker = path.IndexOf("/_delta_log/", System.StringComparison.Ordinal);
            if (marker < 0)
            {
                continue;
            }
            int slash = path.LastIndexOf('/', marker - 1);
            var name = slash < 0 ? path.Substring(0, marker) : path.Substring(slash + 1, marker - slash - 1);
            if (name.Length > 0)
            {
                names.Add(name);
            }
        }
        var schemaCol = new List<string>();
        var nameCol = new List<string>();
        var typeCol = new List<string>();
        foreach (var n in names)
        {
            schemaCol.Add(MainSchema);
            nameCol.Add(n);
            typeCol.Add("BASE TABLE");
        }
        return ThreeColumn("schema_name", schemaCol, "table_name", nameCol, "table_type", typeCol);
    }

    public IArrowArrayStream ScanTable(string schemaName, string tableName, string? specJson,
                                       IArrowArrayStream? filterValues)
    {
        var opener = AmbientOpener.Current;
        var path = TablePath(tableName);
        // Push the FILTER into engineered-wood file/row-group skipping (superset-safe; DuckDB re-applies).
        // Projection is left to DuckDB above the scan (the full schema is returned, mapped by name) — same as
        // the global arrownet_delta_scan; column-pruning into parquet would need a projected-schema stream.
        var spec = ScanSpec.Parse(specJson);
        EngineeredWood.Expressions.Predicate? filter = spec?.Filter is { } node
            ? new DeltaFilterBuilder(ReadFilterValues(filterValues)).Build(node)
            : null;
        var schema = DeltaReader.GetSchema(opener, path);
        return new AsyncEnumerableArrowStream(schema, DeltaReader.Stream(opener, path, columns: null, filter, default));
    }

    private static IReadOnlyList<object?> ReadFilterValues(IArrowArrayStream? filterValues)
    {
        if (filterValues is null)
        {
            return System.Array.Empty<object?>();
        }
        using (filterValues)
        {
            var batch = filterValues.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
            if (batch is null)
            {
                return System.Array.Empty<object?>();
            }
            var values = new object?[batch.ColumnCount];
            for (int i = 0; i < batch.ColumnCount; i++)
            {
                try { values[i] = ArrowValueReader.ReadScalar(batch.Column(i), 0); }
                catch (System.NotSupportedException) { values[i] = null; }
            }
            return values;
        }
    }

    // ---- read-only / unsupported surface (this slice) ----
    private static NotSupportedException ReadOnly() =>
        new("delta provider is read-only in this slice (INSERT/CTAS/DELETE not yet wired).");

    public IArrowArrayStream ExecuteQuery(string sql) => throw new NotSupportedException("delta provider: no raw query.");
    public long ExecuteNonQuery(string sql) => throw ReadOnly();
    public long BulkInsert(string s, string t, IArrowArrayStream d, bool c, bool r, bool cc, long txn) => throw ReadOnly();
    public long ExecuteDelete(string s, string t, IArrowArrayStream k) => throw ReadOnly();
    public long ExecuteUpdate(string s, string t, int n, IArrowArrayStream d) => throw ReadOnly();
    public IArrowArrayStream InsertReturning(string s, string t, IArrowArrayStream r) => throw ReadOnly();
    public void CreateTable(string s, string t, Schema c, bool ie, string? pk, string? u, string? d) => throw ReadOnly();
    public void DropTable(string s, string t, bool ie) => throw ReadOnly();
    public void CreateSchema(string s, bool ie) => throw ReadOnly();
    public void DropSchema(string s, bool ie) => throw ReadOnly();
    public void AlterTable(int k, string s, string t, string? a1, string? a2, Field? c, int f) => throw ReadOnly();
    public void BeginTransaction() { }
    public void CommitTransaction() { }
    public void RollbackTransaction() { }

    public Schema GetFunctionParamSchema(string s, string f) => throw NoFunctions();
    public Schema GetFunctionReturnSchema(string s, string f) => throw NoFunctions();
    public IArrowArrayStream ExecuteScalar(string s, string f, IArrowArrayStream a) => throw NoFunctions();
    public Schema GetFunctionOutputSchema(string s, string f, RecordBatch? a = null) => throw NoFunctions();
    public IBoundTable TableBind(string s, string f, RecordBatch? a) => throw NoFunctions();
    public IArrowInOutBinding InOutBind(string s, string f, RecordBatch? a, Schema input) => throw NoFunctions();
    public IAggregateSession AggOpen(string s, string f) => throw NoFunctions();
    private static NotSupportedException NoFunctions() => new("delta provider: no catalog functions.");

    public void Dispose() { }

    // ---- Arrow metadata-stream helpers (mirror DaxCatalog) ----
    private static IArrowArrayStream SingleColumn(string name, IReadOnlyList<string> values)
    {
        var schema = new Schema(new[] { new Field(name, StringType.Default, nullable: true) }, null);
        var b = new StringArray.Builder();
        foreach (var v in values) { b.Append(v); }
        return new InMemoryArrayStream(schema, new[] { new RecordBatch(schema, new IArrowArray[] { b.Build() }, values.Count) });
    }

    private static IArrowArrayStream ThreeColumn(string n0, IReadOnlyList<string> c0, string n1,
                                                 IReadOnlyList<string> c1, string n2, IReadOnlyList<string> c2)
    {
        var schema = new Schema(new[]
        {
            new Field(n0, StringType.Default, nullable: true),
            new Field(n1, StringType.Default, nullable: true),
            new Field(n2, StringType.Default, nullable: true),
        }, null);
        static IArrowArray Build(IReadOnlyList<string> vals)
        {
            var b = new StringArray.Builder();
            foreach (var v in vals) { b.Append(v); }
            return b.Build();
        }
        return new InMemoryArrayStream(schema,
            new[] { new RecordBatch(schema, new[] { Build(c0), Build(c1), Build(c2) }, c0.Count) });
    }

    private static IArrowArrayStream EmptyStringTable(params string[] columns)
    {
        var builder = new Schema.Builder();
        foreach (var c in columns) { builder.Field(new Field(c, StringType.Default, nullable: true)); }
        return new InMemoryArrayStream(builder.Build(), System.Array.Empty<RecordBatch>());
    }
}
