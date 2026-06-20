using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace ArrowNet.Bridge;

/// <summary>
/// Phase 0 stub backend. Ignores the connection string and returns a fixed
/// 3-row table for any query, proving the C++ -> CoreCLR -> Arrow -> DuckDB
/// round-trip end to end without a real database.
/// </summary>
public sealed class StubBackend : IBackend
{
    public string Name => "stub";

    // The stub ignores the connection string; return any non-empty marker.
    public string BuildConnectionString(IReadOnlyDictionary<string, string> fields) => "stub";

    public IBackendCatalog OpenCatalog(string connectionString) => new StubCatalog();

    private sealed class StubCatalog : IBackendCatalog
    {
        private static Schema ScanSchema() =>
            new Schema.Builder()
                .Field(new Field("id", Int32Type.Default, nullable: false))
                .Field(new Field("name", StringType.Default, nullable: false))
                .Field(new Field("query", StringType.Default, nullable: false))
                .Build();

        public IArrowArrayStream ExecuteQuery(string sql)
        {
            var schema = ScanSchema();
            var id = new Int32Array.Builder().Append(1).Append(2).Append(3).Build();
            var name = new StringArray.Builder().Append("alpha").Append("beta").Append("gamma").Build();
            var echoed = new StringArray.Builder().Append(sql).Append(sql).Append(sql).Build();

            var batch = new RecordBatch(schema, new IArrowArray[] { id, name, echoed }, 3);
            return new InMemoryArrayStream(schema, new[] { batch });
        }

        public long ExecuteNonQuery(string sql) => 0;

        public IArrowArrayStream GetMetadata(int kind, string? schema, string? table) => kind switch
        {
            MetadataKind.Tables => EmptyStringTable("schema_name", "table_name", "table_type"),
            // Zero-row stream whose schema describes the (stub) table columns.
            MetadataKind.Columns => new InMemoryArrayStream(ScanSchema(), System.Array.Empty<RecordBatch>()),
            _ => EmptyStringTable("name"),
        };

        public IArrowArrayStream ScanTable(string schemaName, string tableName, string? specJson,
                                           IArrowArrayStream? filterValues) =>
            ExecuteQuery($"SELECT * FROM {schemaName}.{tableName}");

        public IArrowArrayStream GetFunctionParamSchema(string schemaName, string functionName) =>
            new InMemoryArrayStream(new Schema(System.Array.Empty<Field>(), null), System.Array.Empty<RecordBatch>());

        public IArrowArrayStream GetFunctionReturnSchema(string schemaName, string functionName) =>
            EmptyStringTable("result");

        public IArrowArrayStream ExecuteScalar(string schemaName, string functionName, IArrowArrayStream args) =>
            EmptyStringTable("result");

        public IArrowArrayStream GetFunctionOutputSchema(string schemaName, string functionName) =>
            EmptyStringTable("result");

        public IArrowArrayStream ExecuteTable(string schemaName, string functionName, IArrowArrayStream args) =>
            EmptyStringTable("result");

        public void CreateTable(string schemaName, string tableName, Schema columns, bool ifNotExists,
                                string? primaryKey, string? uniques, string? defaults, string? textType)
        {
        }

        public void DropTable(string schemaName, string tableName, bool ifExists)
        {
        }

        public void CreateSchema(string schemaName, bool ifNotExists)
        {
        }

        public void DropSchema(string schemaName, bool ifExists)
        {
        }

        public void AlterTable(int alterKind, string schemaName, string tableName, string? arg1, string? arg2,
                               Field? column, int flags)
        {
        }

        public void BeginTransaction()
        {
        }

        public void CommitTransaction()
        {
        }

        public void RollbackTransaction()
        {
        }

        public IArrowArrayStream InsertReturning(string schemaName, string tableName, IArrowArrayStream rows)
            => rows; // echo back the input rows for the stub

        private static IArrowArrayStream EmptyStringTable(params string[] columns)
        {
            var builder = new Schema.Builder();
            foreach (var column in columns)
            {
                builder.Field(new Field(column, StringType.Default, nullable: true));
            }
            return new InMemoryArrayStream(builder.Build(), System.Array.Empty<RecordBatch>());
        }

        public long BulkInsert(string schemaName, string tableName, IArrowArrayStream data, bool createTable,
                               bool replace, bool checkConstraints) => CountRows(data);

        public long ExecuteDelete(string schemaName, string tableName, IArrowArrayStream keys) => CountRows(keys);

        public long ExecuteUpdate(string schemaName, string tableName, int setColumnCount, IArrowArrayStream data)
            => CountRows(data);

        private static long CountRows(IArrowArrayStream stream)
        {
            long rows = 0;
            using var reader = new ArrowDataReader(stream);
            while (reader.Read())
            {
                rows++;
            }
            return rows;
        }

        public void Dispose()
        {
        }
    }
}
