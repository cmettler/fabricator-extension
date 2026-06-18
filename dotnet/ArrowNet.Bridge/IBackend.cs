using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace ArrowNet.Bridge;

/// <summary>
/// A data backend behind the bridge (SQL Server, later Analysis Services/DAX).
/// The bridge resolves the active backend, opens catalogs, and turns the
/// backend's results into Arrow streams for the C++ host. Phase 0 ships only
/// <see cref="StubBackend"/>; Phase 1 adds the SqlClient-based implementation in
/// the ArrowNet.SqlServer assembly.
/// </summary>
public interface IBackend
{
    /// <summary>Open a catalog/connection for the given connection string.</summary>
    IBackendCatalog OpenCatalog(string connectionString);
}

/// <summary>An opened connection/catalog. Disposed when the native side closes the handle.</summary>
public interface IBackendCatalog : IDisposable
{
    /// <summary>Execute a query and return its result as an Arrow stream.</summary>
    IArrowArrayStream ExecuteQuery(string sql);

    /// <summary>Execute a non-query statement (DML/DDL); returns rows affected.</summary>
    long ExecuteNonQuery(string sql);

    /// <summary>
    /// Bulk-loads an Arrow stream into a table. If <paramref name="createTable"/>,
    /// the target is created from the stream's Arrow schema (mapping Arrow types
    /// to provider types); <paramref name="replace"/> drops it first. Returns rows
    /// written. This is the provider-specific bulk path (e.g. SqlBulkCopy).
    /// </summary>
    long BulkInsert(string schemaName, string tableName, IArrowArrayStream data, bool createTable, bool replace);

    /// <summary>
    /// rowid-based DELETE. <paramref name="keys"/> columns (named by Arrow field)
    /// are the key values to delete. Returns rows affected.
    /// </summary>
    long ExecuteDelete(string schemaName, string tableName, IArrowArrayStream keys);

    /// <summary>
    /// rowid-based UPDATE. The first <paramref name="setColumnCount"/> columns of
    /// <paramref name="data"/> are the SET values; the rest are the key values.
    /// Returns rows affected.
    /// </summary>
    long ExecuteUpdate(string schemaName, string tableName, int setColumnCount, IArrowArrayStream data);

    /// <summary>
    /// Discovers catalog metadata as an Arrow stream. <paramref name="kind"/> is a
    /// <see cref="MetadataKind"/>; <paramref name="schema"/>/<paramref name="table"/>
    /// are supplied when the kind needs them. The backend owns all provider SQL
    /// (e.g. <c>sys.*</c>, primary-key / unique-index discovery). For
    /// <see cref="MetadataKind.Columns"/> the stream carries zero rows and its
    /// schema describes the table's columns.
    /// </summary>
    IArrowArrayStream GetMetadata(int kind, string? schema, string? table);

    /// <summary>
    /// Scans a table, returning its rows as an Arrow stream. The backend builds
    /// the provider SELECT (keeping read-path SQL out of the C++ host).
    /// <paramref name="specJson"/> (null => SELECT *) carries projection + filter
    /// pushdown: <c>{ "columns": [...], "filter": &lt;tree&gt; }</c>. Filter-tree
    /// constants are referenced by index into <paramref name="filterValues"/>, a
    /// one-batch Arrow stream of the typed constant values (null => no filter).
    /// </summary>
    IArrowArrayStream ScanTable(string schemaName, string tableName, string? specJson, IArrowArrayStream? filterValues);

    /// <summary>
    /// Creates a table whose columns are described by <paramref name="columns"/>
    /// (a non-nullable field maps to NOT NULL). The backend maps Arrow types to
    /// provider types and runs the provider CREATE TABLE. When
    /// <paramref name="ifNotExists"/> is set, creation is skipped if it exists.
    /// <paramref name="primaryKey"/> is one comma-separated group of 0-based
    /// column indices for the PRIMARY KEY (null/empty if none);
    /// <paramref name="uniques"/> is ';'-separated groups of comma-separated
    /// indices, one UNIQUE constraint per group. <paramref name="defaults"/>
    /// carries literal column DEFAULTs as space-separated "<index> <payload>"
    /// pairs, payload = base64(value-text) or "-" for DEFAULT NULL.
    /// </summary>
    void CreateTable(string schemaName, string tableName, Schema columns, bool ifNotExists, string? primaryKey,
                     string? uniques, string? defaults);

    /// <summary>Drops a table; <paramref name="ifExists"/> suppresses the missing-table error.</summary>
    void DropTable(string schemaName, string tableName, bool ifExists);

    /// <summary>Creates a schema; <paramref name="ifNotExists"/> guards creation.</summary>
    void CreateSchema(string schemaName, bool ifNotExists);

    /// <summary>Drops a schema; <paramref name="ifExists"/> suppresses the missing-schema error.</summary>
    void DropSchema(string schemaName, bool ifExists);

    /// <summary>
    /// Enters transaction mode for this catalog. The backend pins a connection +
    /// provider transaction (lazily, on the first write); subsequent DML runs on
    /// it until <see cref="CommitTransaction"/> / <see cref="RollbackTransaction"/>.
    /// </summary>
    void BeginTransaction();

    /// <summary>Commits the pinned transaction (no-op if none was opened).</summary>
    void CommitTransaction();

    /// <summary>Rolls back the pinned transaction (no-op if none was opened).</summary>
    void RollbackTransaction();

    /// <summary>
    /// Inserts the rows in <paramref name="rows"/> (its Arrow field names are the
    /// target column list) and returns the inserted rows — all table columns in
    /// table order, including generated identity/default values — as an Arrow
    /// stream (SQL Server <c>OUTPUT INSERTED.*</c>). Backs INSERT … RETURNING.
    /// </summary>
    IArrowArrayStream InsertReturning(string schemaName, string tableName, IArrowArrayStream rows);

    /// <summary>
    /// Alters a table. <paramref name="alterKind"/> is an <see cref="AlterKind"/>;
    /// <paramref name="arg1"/>/<paramref name="arg2"/> are names (per kind). For
    /// <see cref="AlterKind.AddColumn"/> / <see cref="AlterKind.ColumnType"/> the
    /// new column's type is carried by <paramref name="column"/>. <paramref name="flags"/>
    /// bit 0 (<see cref="AlterKind.FlagIfExists"/>) is the if-(not-)exists guard.
    /// </summary>
    void AlterTable(int alterKind, string schemaName, string tableName, string? arg1, string? arg2, Field? column,
                    int flags);
}

/// <summary>
/// Process-wide registry of the active backend. The bridge stays decoupled from
/// any concrete backend: on first use it loads the backend assembly named by
/// <c>ARROWNET_BACKEND_ASSEMBLY</c> (default <c>ArrowNet.SqlServer</c>), finds an
/// <see cref="IBackend"/> implementation, and instantiates it. If that fails it
/// falls back to <see cref="StubBackend"/> so the bridge still works standalone.
/// </summary>
public static class BackendRegistry
{
    private static IBackend? _active;

    public static void Register(IBackend backend) => _active = backend;

    public static IBackend Active => _active ??= LoadDefault();

    private static IBackend LoadDefault()
    {
        var assemblyName = Environment.GetEnvironmentVariable("ARROWNET_BACKEND_ASSEMBLY");
        if (string.IsNullOrEmpty(assemblyName))
        {
            assemblyName = "ArrowNet.SqlServer";
        }
        try
        {
            var assembly = System.Reflection.Assembly.Load(assemblyName);
            foreach (var type in assembly.GetTypes())
            {
                if (!type.IsAbstract && typeof(IBackend).IsAssignableFrom(type) &&
                    type.GetConstructor(Type.EmptyTypes) != null)
                {
                    return (IBackend)Activator.CreateInstance(type)!;
                }
            }
        }
        catch
        {
            // No backend assembly available — fall back to the stub.
        }
        return new StubBackend();
    }
}
