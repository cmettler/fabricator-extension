using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// Phase 0 stub backend. Ignores the connection string and returns a fixed
/// 3-row table for any query, proving the C++ -> CoreCLR -> Arrow -> DuckDB
/// round-trip end to end without a real database.
/// </summary>
public sealed class StubBackend : IBackend
{
    public string Name => "stub";

    // The stub ignores the connection string; return any non-empty marker.
    public string BuildConnectionString(string secretType, IReadOnlyDictionary<string, string> fields,
                                        string baseConnString) => "stub";

    public IBackendCatalog OpenCatalog(string connectionString, string optionsJson) => new StubCatalog();

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

        public IArrowArrayStream GetSchemas() => EmptyStringTable("schema_name");

        public IArrowArrayStream GetTables() => EmptyStringTable("schema_name", "table_name", "table_type");

        public IArrowArrayStream GetFunctions() =>
            EmptyStringTable("schema_name", "name", "kind", "param_count", "return_type");

        public IArrowArrayStream GetMacros() => EmptyStringTable("schema", "name", "create_sql");
        public IArrowArrayStream GetViews() => EmptyStringTable("schema", "name", "create_sql");

        public IArrowArrayStream GetServerInfo() => EmptyStringTable("property", "value");

        public ITable GetTable(string schemaName, string tableName) =>
            new StubTableDefinition(this, schemaName, tableName);

        private sealed class StubTableDefinition : ITable
        {
            private readonly StubCatalog _catalog;

            internal StubTableDefinition(StubCatalog catalog, string schemaName, string tableName)
            {
                _catalog = catalog;
                SchemaName = schemaName;
                TableName = tableName;
            }

            public string SchemaName { get; }
            public string TableName { get; }

            public ITableBinding Bind(ITransaction? transaction, TableAt? at = null) => new StubTableBinding(_catalog, this);
        }

        private sealed class StubTableBinding : ITableBinding
        {
            private readonly StubCatalog _catalog;
            private readonly StubTableDefinition _definition;

            internal StubTableBinding(StubCatalog catalog, StubTableDefinition definition)
            {
                _catalog = catalog;
                _definition = definition;
            }

            public Schema Schema => ScanSchema();

            public IReadOnlyList<string> RowIdColumns() => System.Array.Empty<string>();

            public IReadOnlyList<VirtualColumn> VirtualColumns() => System.Array.Empty<VirtualColumn>();

            public long? ApproximateRowCount() => null;

            public IReadOnlyList<NdvEntry> ColumnNdv() => System.Array.Empty<NdvEntry>();

            public IArrowArrayStream Scan(string? specJson, IArrowArrayStream? filterValues) =>
                _catalog.ScanTable(_definition.SchemaName, _definition.TableName, specJson, filterValues);

            public void Dispose()
            {
            }
        }

        public IArrowArrayStream ScanTable(string schemaName, string tableName, string? specJson,
                                           IArrowArrayStream? filterValues) =>
            ExecuteQuery($"SELECT * FROM {schemaName}.{tableName}");

        public Schema GetFunctionParamSchema(string schemaName, string functionName) =>
            new Schema(System.Array.Empty<Field>(), null);

        public Schema GetFunctionReturnSchema(string schemaName, string functionName) =>
            new Schema(new[] { new Field("result", StringType.Default, nullable: true) }, null);

        public IArrowArrayStream ExecuteScalar(string schemaName, string functionName, IArrowArrayStream args) =>
            EmptyStringTable("result");

        public Schema GetFunctionOutputSchema(string schemaName, string functionName, RecordBatch? args = null) =>
            new Schema(new[] { new Field("result", StringType.Default, nullable: true) }, null);

        public IBoundTableFunction TableFnBind(string schemaName, string functionName, RecordBatch? args) =>
            throw new NotSupportedException("stub backend has no table functions");

        public IInOutBinding InOutBind(string schemaName, string functionName, RecordBatch? args, Schema inputSchema) =>
            throw new NotSupportedException("stub backend has no table-in-out functions");

        public IAggregateSession AggOpen(string schemaName, string functionName) =>
            throw new NotSupportedException("stub backend has no aggregate functions");

        public void CreateTable(string schemaName, string tableName, Schema columns, bool ifNotExists,
                                string? primaryKey, string? uniques, string? defaults,
                                IReadOnlyList<string>? partitionColumns, IReadOnlyList<string>? sortColumns,
                                IReadOnlyList<string>? identityColumns, string? optionsJson)
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

        public void AlterTable(AlterTableSpec spec, string schemaName, string tableName, Field? column)
        {
        }

        public void BeginTransaction(bool isExplicit)
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
                               bool replace, bool checkConstraints, long txnId,
                               IReadOnlyList<string>? partitionColumns,
                               IReadOnlyList<string>? sortColumns, string? schemaMode,
                               bool partitionOverwrite, string? optionsJson) => CountRows(data);

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
