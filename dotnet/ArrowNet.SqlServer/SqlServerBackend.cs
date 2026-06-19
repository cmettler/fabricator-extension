using System.Data;
using System.Text;
using ArrowNet.Bridge;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Microsoft.Data.SqlClient;

namespace ArrowNet.SqlServer;

/// <summary>
/// <see cref="IBackend"/> backed by Microsoft.Data.SqlClient. Discovered and
/// instantiated reflectively by <see cref="BackendRegistry"/>.
/// </summary>
public sealed class SqlServerBackend : IBackend
{
    public IBackendCatalog OpenCatalog(string connectionString) => new SqlServerCatalog(connectionString);
}

/// <summary>
/// A SQL Server connection target. Each query opens a fresh pooled
/// <see cref="SqlConnection"/> (ADO.NET pools by connection string); the
/// connection's lifetime is owned by the returned Arrow stream.
/// </summary>
public sealed class SqlServerCatalog : IBackendCatalog
{
    // Non-standard trailing segment used by the secret builder to carry an Azure
    // Entra access token (not a valid SqlClient connection-string keyword); it is
    // stripped here and applied via SqlConnection.AccessToken.
    private const string AccessTokenKeyword = ";ArrowNetAccessToken=";

    private readonly string _connectionString;
    private readonly string? _accessToken;

    public SqlServerCatalog(string connectionString)
    {
        // Empty connection string is rejected early with a clear message.
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("mssql_net: empty connection string", nameof(connectionString));
        }
        // A `mssql://[user[:password]@]host[:port]/database[?encrypt=..&trustservercertificate=..]`
        // URI is translated to a SqlClient connection string (SqlClient can't parse it).
        if (connectionString.StartsWith("mssql://", StringComparison.OrdinalIgnoreCase))
        {
            connectionString = ParseMssqlUri(connectionString);
        }

        int idx = connectionString.IndexOf(AccessTokenKeyword, StringComparison.OrdinalIgnoreCase);
        string connStr;
        if (idx >= 0)
        {
            // Everything after the keyword is the token (the builder appends it last).
            _accessToken = connectionString.Substring(idx + AccessTokenKeyword.Length);
            connStr = connectionString.Substring(0, idx);
        }
        else
        {
            connStr = connectionString;
        }
        // Enable MARS so a scan reader and the transaction's DML commands can be
        // active on the one pinned connection (read-your-writes within a transaction).
        _connectionString = new SqlConnectionStringBuilder(connStr) { MultipleActiveResultSets = true }.ConnectionString;
    }

    // Translates a mssql://[user[:password]@]host[:port]/database[?params] URI into
    // a Microsoft.Data.SqlClient connection string. Mirrors the C++ mssql extension:
    // user/password before the LAST '@' (passwords may contain unencoded '@'), all
    // components percent-decoded; encrypt/trustservercertificate query params honored
    // (TLS on by default for compatibility).
    private static string ParseMssqlUri(string uri)
    {
        string rest = uri.Substring("mssql://".Length);
        string query = "";
        int q = rest.IndexOf('?');
        if (q >= 0)
        {
            query = rest.Substring(q + 1);
            rest = rest.Substring(0, q);
        }

        var builder = new SqlConnectionStringBuilder();
        int at = rest.LastIndexOf('@');
        if (at >= 0)
        {
            string userInfo = rest.Substring(0, at);
            rest = rest.Substring(at + 1);
            int colon = userInfo.IndexOf(':');
            if (colon >= 0)
            {
                builder["User ID"] = Uri.UnescapeDataString(userInfo.Substring(0, colon));
                builder["Password"] = Uri.UnescapeDataString(userInfo.Substring(colon + 1));
            }
            else
            {
                builder["User ID"] = Uri.UnescapeDataString(userInfo);
            }
        }

        int slash = rest.IndexOf('/');
        string hostPort = rest;
        if (slash >= 0)
        {
            hostPort = rest.Substring(0, slash);
            builder["Database"] = Uri.UnescapeDataString(rest.Substring(slash + 1));
        }
        // host:port -> "host,port" (split at the last ':')
        int hostColon = hostPort.LastIndexOf(':');
        builder.DataSource = hostColon >= 0
            ? hostPort.Substring(0, hostColon) + "," + hostPort.Substring(hostColon + 1)
            : hostPort;

        bool encryptSet = false, trustSet = false;
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }
            string key = Uri.UnescapeDataString(part.Substring(0, eq)).ToLowerInvariant();
            string val = Uri.UnescapeDataString(part.Substring(eq + 1));
            switch (key)
            {
                case "encrypt":
                case "ssl":
                case "use_ssl":
                    builder["Encrypt"] = val; encryptSet = true; break;
                case "trustservercertificate":
                    builder["TrustServerCertificate"] = val; trustSet = true; break;
                // schema_filter/table_filter/authenticator/... are accepted but not wired.
            }
        }
        if (!encryptSet)
        {
            builder["Encrypt"] = "True";
        }
        if (!trustSet)
        {
            builder["TrustServerCertificate"] = "True";
        }
        return builder.ConnectionString;
    }

    // Creates a connection, applying an Azure access token when one was supplied
    // via the secret (Entra "bring-your-own-token" auth).
    private SqlConnection OpenConnection()
    {
        var connection = new SqlConnection(_connectionString);
        if (_accessToken is not null)
        {
            connection.AccessToken = _accessToken;
        }
        return connection;
    }

    // ---- Transaction state ----------------------------------------------------
    // While in transaction mode, all DML runs on a single pinned connection +
    // SqlTransaction (opened lazily on the first write) so COMMIT/ROLLBACK are
    // atomic. Reads keep using fresh connections. Single-session by design.
    private readonly object _txnLock = new();
    private bool _inTransaction;
    private SqlConnection? _txnConnection;
    private SqlTransaction? _txn;

    public void BeginTransaction()
    {
        lock (_txnLock)
        {
            _inTransaction = true; // pin lazily on the first write
        }
    }

    public void CommitTransaction() => EndTransaction(commit: true);

    public void RollbackTransaction() => EndTransaction(commit: false);

    private void EndTransaction(bool commit)
    {
        lock (_txnLock)
        {
            try
            {
                if (_txn is not null)
                {
                    if (commit)
                    {
                        _txn.Commit();
                    }
                    else
                    {
                        _txn.Rollback();
                    }
                }
            }
            finally
            {
                _txn?.Dispose();
                _txnConnection?.Dispose();
                _txn = null;
                _txnConnection = null;
                _inTransaction = false;
            }
        }
    }

    // Returns a connection for a write. In transaction mode it is the pinned
    // connection (opened + provider-transaction started on first use), owns=false
    // so the caller must NOT dispose it; otherwise a fresh autocommit connection
    // (owns=true). The connection is already open.
    private (SqlConnection connection, SqlTransaction? transaction, bool owns) BeginWrite()
    {
        lock (_txnLock)
        {
            if (_inTransaction)
            {
                if (_txnConnection is null)
                {
                    _txnConnection = OpenConnection();
                    _txnConnection.Open();
                    _txn = _txnConnection.BeginTransaction();
                }
                return (_txnConnection, _txn, false);
            }
        }
        var connection = OpenConnection();
        connection.Open();
        return (connection, null, true);
    }

    public IArrowArrayStream ExecuteQuery(string sql) => ExecuteQuery(sql, null);

    public IArrowArrayStream ExecuteQuery(string sql, IReadOnlyList<SqlParameter>? parameters)
    {
        // Inside a transaction that has a pinned connection (a write has happened),
        // read on that connection so the query sees uncommitted changes
        // (read-your-writes). Borrowed: the stream must not dispose the connection.
        SqlConnection? pinned;
        SqlTransaction? pinnedTransaction;
        lock (_txnLock)
        {
            pinned = _inTransaction ? _txnConnection : null;
            pinnedTransaction = _txn;
        }
        if (pinned is not null)
        {
            var pinnedCommand = pinned.CreateCommand();
            pinnedCommand.CommandText = sql;
            pinnedCommand.CommandType = CommandType.Text;
            pinnedCommand.Transaction = pinnedTransaction;
            AddParameters(pinnedCommand, parameters);
            var pinnedReader = pinnedCommand.ExecuteReader();
            return new DbDataReaderArrowStream(pinned, pinnedCommand, pinnedReader, ownsConnection: false);
        }

        SqlConnection? connection = null;
        SqlCommand? command = null;
        try
        {
            connection = OpenConnection();
            connection.Open();
            command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandType = CommandType.Text;
            AddParameters(command, parameters);
            var reader = command.ExecuteReader();
            return new DbDataReaderArrowStream(connection, command, reader);
        }
        catch
        {
            command?.Dispose();
            connection?.Dispose();
            throw;
        }
    }

    private static void AddParameters(SqlCommand command, IReadOnlyList<SqlParameter>? parameters)
    {
        if (parameters is null)
        {
            return;
        }
        foreach (var p in parameters)
        {
            command.Parameters.Add(p);
        }
    }

    public long ExecuteNonQuery(string sql)
    {
        var (connection, transaction, owns) = BeginWrite();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandType = CommandType.Text;
            command.Transaction = transaction;
            // ExecuteNonQuery returns -1 for statements that don't affect rows
            // (DDL, SET, ...); report 0 for those (matches the C++ mssql extension).
            return Math.Max(0, command.ExecuteNonQuery());
        }
        finally
        {
            if (owns)
            {
                connection.Dispose();
            }
        }
    }

    public long BulkInsert(string schemaName, string tableName, IArrowArrayStream data, bool createTable, bool replace)
    {
        var (connection, transaction, owns) = BeginWrite();
        try
        {
            string qualified = Quote(schemaName) + "." + Quote(tableName);
            string objectLiteral = "N'" + (schemaName + "." + tableName).Replace("'", "''") + "'";

            if (replace)
            {
                using var drop = connection.CreateCommand();
                drop.Transaction = transaction;
                drop.CommandText = $"IF OBJECT_ID({objectLiteral}, 'U') IS NOT NULL DROP TABLE {qualified}";
                drop.ExecuteNonQuery();
            }
            if (createTable)
            {
                using var create = connection.CreateCommand();
                create.Transaction = transaction;
                create.CommandText = $"IF OBJECT_ID({objectLiteral}, 'U') IS NULL " +
                                     BuildCreateTable(qualified, data.Schema);
                create.ExecuteNonQuery();
            }

            // If the source maps an IDENTITY column of an existing target, preserve the
            // explicit values (KeepIdentity); otherwise let SQL Server auto-generate them.
            var options = SqlBulkCopyOptions.Default;
            if (!createTable)
            {
                var identityColumns = GetIdentityColumns(connection, transaction, schemaName, tableName);
                if (identityColumns.Count > 0 &&
                    data.Schema.FieldsList.Any(f => identityColumns.Contains(f.Name)))
                {
                    options |= SqlBulkCopyOptions.KeepIdentity;
                }
            }

            using var reader = new ArrowDataReader(data);
            using var bulk = new SqlBulkCopy(connection, options, transaction)
                { DestinationTableName = qualified, BulkCopyTimeout = 0 };
            // Map by name (case-insensitive) so source/target column order need not match.
            foreach (var field in data.Schema.FieldsList)
            {
                bulk.ColumnMappings.Add(field.Name, field.Name);
            }
            bulk.WriteToServer(reader);
            return bulk.RowsCopied;
        }
        finally
        {
            if (owns)
            {
                connection.Dispose();
            }
        }
    }

    public long ExecuteDelete(string schemaName, string tableName, IArrowArrayStream keys)
    {
        var (connection, transaction, owns) = BeginWrite();
        try
        {
        string qualified = Quote(schemaName) + "." + Quote(tableName);
        var keyColumns = keys.Schema.FieldsList.Select(f => f.Name).ToList();

        using var reader = new ArrowDataReader(keys);
        long total = 0;
        var batch = new List<object?[]>();

        void Flush()
        {
            if (batch.Count == 0)
            {
                return;
            }
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            var sb = new StringBuilder("DELETE FROM ").Append(qualified).Append(" WHERE ");
            int p = 0;
            for (int r = 0; r < batch.Count; r++)
            {
                if (r > 0)
                {
                    sb.Append(" OR ");
                }
                sb.Append('(');
                for (int c = 0; c < keyColumns.Count; c++)
                {
                    if (c > 0)
                    {
                        sb.Append(" AND ");
                    }
                    string pn = "@p" + p++;
                    sb.Append(Quote(keyColumns[c])).Append(" = ").Append(pn);
                    cmd.Parameters.AddWithValue(pn, batch[r][c] ?? DBNull.Value);
                }
                sb.Append(')');
            }
            cmd.CommandText = sb.ToString();
            total += cmd.ExecuteNonQuery();
            batch.Clear();
        }

        while (reader.Read())
        {
            var row = new object?[keyColumns.Count];
            for (int c = 0; c < keyColumns.Count; c++)
            {
                row[c] = reader.IsDBNull(c) ? null : reader.GetValue(c);
            }
            batch.Add(row);
            if (batch.Count >= 500)
            {
                Flush();
            }
        }
        Flush();
        return total;
        }
        finally
        {
            if (owns)
            {
                connection.Dispose();
            }
        }
    }

    public long ExecuteUpdate(string schemaName, string tableName, int setColumnCount, IArrowArrayStream data)
    {
        var (connection, transaction, owns) = BeginWrite();
        try
        {
        string qualified = Quote(schemaName) + "." + Quote(tableName);
        var columns = data.Schema.FieldsList.Select(f => f.Name).ToList();
        int keyStart = setColumnCount;

        using var reader = new ArrowDataReader(data);
        long total = 0;
        while (reader.Read())
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            var sb = new StringBuilder("UPDATE ").Append(qualified).Append(" SET ");
            for (int c = 0; c < setColumnCount; c++)
            {
                if (c > 0)
                {
                    sb.Append(", ");
                }
                string pn = "@s" + c;
                sb.Append(Quote(columns[c])).Append(" = ").Append(pn);
                cmd.Parameters.AddWithValue(pn, reader.IsDBNull(c) ? DBNull.Value : reader.GetValue(c));
            }
            sb.Append(" WHERE ");
            for (int c = keyStart; c < columns.Count; c++)
            {
                if (c > keyStart)
                {
                    sb.Append(" AND ");
                }
                string pn = "@k" + c;
                sb.Append(Quote(columns[c])).Append(" = ").Append(pn);
                cmd.Parameters.AddWithValue(pn, reader.IsDBNull(c) ? DBNull.Value : reader.GetValue(c));
            }
            cmd.CommandText = sb.ToString();
            total += cmd.ExecuteNonQuery();
        }
        return total;
        }
        finally
        {
            if (owns)
            {
                connection.Dispose();
            }
        }
    }

    public IArrowArrayStream InsertReturning(string schemaName, string tableName, IArrowArrayStream rows)
    {
        var (connection, transaction, owns) = BeginWrite();
        try
        {
            string qualified = Quote(schemaName) + "." + Quote(tableName);
            var columns = rows.Schema.FieldsList.Select(f => f.Name).ToList();
            string columnList = string.Join(", ", columns.Select(Quote));

            using var reader = new ArrowDataReader(rows);
            var batch = new List<object?[]>();
            Schema? outputSchema = null;
            var outputBatches = new List<RecordBatch>();

            void Flush()
            {
                if (batch.Count == 0)
                {
                    return;
                }
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                var sb = new StringBuilder("INSERT INTO ").Append(qualified).Append(" (").Append(columnList)
                    .Append(") OUTPUT INSERTED.* VALUES ");
                int p = 0;
                for (int r = 0; r < batch.Count; r++)
                {
                    if (r > 0)
                    {
                        sb.Append(", ");
                    }
                    sb.Append('(');
                    for (int c = 0; c < columns.Count; c++)
                    {
                        if (c > 0)
                        {
                            sb.Append(", ");
                        }
                        string pn = "@p" + p++;
                        sb.Append(pn);
                        cmd.Parameters.AddWithValue(pn, batch[r][c] ?? DBNull.Value);
                    }
                    sb.Append(')');
                }
                cmd.CommandText = sb.ToString();
                var rdr = cmd.ExecuteReader();
                // The OUTPUT result set is the inserted rows; materialize them as Arrow
                // batches (borrowed connection — only the reader/command are disposed).
                using var stream = new DbDataReaderArrowStream(connection, cmd, rdr, ownsConnection: false);
                outputSchema ??= stream.Schema;
                RecordBatch? rb;
                while ((rb = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()) is not null)
                {
                    outputBatches.Add(rb);
                }
                batch.Clear();
            }

            while (reader.Read())
            {
                var row = new object?[columns.Count];
                for (int c = 0; c < columns.Count; c++)
                {
                    row[c] = reader.IsDBNull(c) ? null : reader.GetValue(c);
                }
                batch.Add(row);
                if (batch.Count >= 1000) // SQL Server's row-constructor VALUES limit
                {
                    Flush();
                }
            }
            Flush();

            // No rows inserted: return an empty stream carrying the table's schema.
            outputSchema ??= ReadTableSchema(connection, transaction, qualified);
            return new InMemoryArrayStream(outputSchema, outputBatches);
        }
        finally
        {
            if (owns)
            {
                connection.Dispose();
            }
        }
    }

    // Reads a table's column layout (zero rows) as an Arrow schema.
    private static Schema ReadTableSchema(SqlConnection connection, SqlTransaction? transaction, string qualified)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"SELECT * FROM {qualified} WHERE 1 = 0";
        using var rdr = cmd.ExecuteReader();
        using var stream = new DbDataReaderArrowStream(connection, cmd, rdr, ownsConnection: false);
        return stream.Schema;
    }

    public IArrowArrayStream GetMetadata(int kind, string? schema, string? table) => kind switch
    {
        MetadataKind.Schemas => ExecuteQuery(SchemasSql),
        MetadataKind.Tables => ExecuteQuery(TablesSql),
        // Zero-row result whose Arrow schema describes the table's columns; the
        // C++ host reads that schema to learn the DuckDB column types.
        MetadataKind.Columns => ExecuteQuery($"SELECT * FROM {Quote(Require(schema, table).schema)}." +
                                             $"{Quote(Require(schema, table).table)} WHERE 1 = 0"),
        MetadataKind.RowId => ExecuteQuery(RowIdSql(Require(schema, table).schema, Require(schema, table).table)),
        MetadataKind.RowCount => ExecuteQuery(RowCountSql(Require(schema, table).schema, Require(schema, table).table)),
        MetadataKind.ColumnNdv => ExecuteQuery(ColumnNdvSql(Require(schema, table).schema, Require(schema, table).table)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "mssql_net: unknown metadata kind"),
    };

    // Per-column distinct-value estimate (NDV) from existing statistics — (column,
    // ndv) rows. Derived from the leading-column histogram of each stats object:
    // sum of distinct values strictly inside the steps + one per step boundary. Cheap
    // (metadata, no scan); only columns that are a leading stat key appear (others =>
    // no row => unknown). Used ONLY for selectivity estimation, so stale/approximate
    // is safe (never drives pruning); min/max is deliberately not exposed.
    private static string ColumnNdvSql(string schema, string table)
    {
        string objectLiteral = "N'" + (schema + "." + table).Replace("'", "''") + "'";
        return
            "SELECT c.name AS column_name, CAST(MAX(h.ndv) AS VARCHAR(32)) AS ndv " +
            "FROM sys.stats s " +
            "JOIN sys.stats_columns sc ON sc.object_id = s.object_id AND sc.stats_id = s.stats_id AND sc.stats_column_id = 1 " +
            "JOIN sys.columns c ON c.object_id = sc.object_id AND c.column_id = sc.column_id " +
            "CROSS APPLY (SELECT SUM(hist.distinct_range_rows) + COUNT_BIG(*) AS ndv " +
            "             FROM sys.dm_db_stats_histogram(s.object_id, s.stats_id) hist) h " +
            "WHERE s.object_id = OBJECT_ID(" + objectLiteral + ") AND h.ndv > 0 " +
            "GROUP BY c.name";
    }

    // Approximate row count (one VARCHAR cell) from partition stats — a cheap
    // metadata read (not COUNT(*)) used for the optimizer's cardinality estimate.
    // Views / tables with no partition rows yield 0.
    private static string RowCountSql(string schema, string table)
    {
        string objectLiteral = "N'" + (schema + "." + table).Replace("'", "''") + "'";
        return "SELECT CAST(COALESCE(SUM(p.row_count), 0) AS VARCHAR(32)) AS n " +
               "FROM sys.dm_db_partition_stats p " +
               "WHERE p.object_id = OBJECT_ID(" + objectLiteral + ") AND p.index_id IN (0, 1)";
    }

    public IArrowArrayStream ScanTable(string schemaName, string tableName, string? specJson,
                                       IArrowArrayStream? filterValues)
    {
        var qualified = $"{Quote(schemaName)}.{Quote(tableName)}";
        var spec = ScanSpec.Parse(specJson);

        // Projection: SELECT only the requested columns (absent/empty => SELECT *).
        var columns = spec?.Columns is { Count: > 0 } cols
            ? string.Join(", ", cols.Select(Quote))
            : "*";

        // LIMIT pushdown: SELECT TOP (n). The host only sets this when there is no
        // pushed filter and no offset, so it is safe (DuckDB still re-applies LIMIT).
        var top = spec?.Top is long n and >= 0 ? $"TOP ({n}) " : "";

        // ORDER BY pushdown (paired with TOP for TopN). The host only sets this for
        // safe keys (non-string, NULL-order compatible, no filter); DuckDB re-sorts.
        var orderBy = spec?.OrderBy is { Count: > 0 } keys
            ? " ORDER BY " + string.Join(", ", keys.Select(k => $"{Quote(k.Col)}{(k.Desc ? " DESC" : " ASC")}"))
            : "";

        // Filter pushdown: render spec.Filter into a parameterized WHERE. This is
        // best-effort — DuckDB re-applies every predicate above the scan — so if we
        // can't read a value or render a node, omit the WHERE and let DuckDB filter.
        if (spec?.Filter is not null)
        {
            try
            {
                var values = ReadFilterValues(filterValues);
                filterValues = null; // consumed + disposed by ReadFilterValues
                var builder = new FilterWhereBuilder(values);
                var where = builder.Build(spec.Filter);
                return ExecuteQuery($"SELECT {top}{columns} FROM {qualified} WHERE {where}{orderBy}", builder.Parameters);
            }
            catch
            {
                // Fall through to an unfiltered scan; correctness preserved by DuckDB.
            }
        }

        filterValues?.Dispose();
        return ExecuteQuery($"SELECT {top}{columns} FROM {qualified}{orderBy}");
    }

    // Reads the one-row filter value batch (column i == value i) into CLR values.
    private static List<object?> ReadFilterValues(IArrowArrayStream? stream)
    {
        if (stream is null)
        {
            return new List<object?>();
        }
        using (stream)
        {
            var batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
            if (batch is null)
            {
                return new List<object?>();
            }
            using (batch)
            {
                var values = new List<object?>(batch.ColumnCount);
                for (int i = 0; i < batch.ColumnCount; i++)
                {
                    values.Add(ArrowValueReader.ReadScalar(batch.Column(i), 0));
                }
                return values;
            }
        }
    }

    public void CreateTable(string schemaName, string tableName, Schema columns, bool ifNotExists, string? primaryKey,
                            string? uniques, string? defaults, string? textType)
    {
        // Route through BeginWrite so this participates in the pinned transaction
        // when one is active — without it, CREATE OR REPLACE (DROP pinned + CREATE
        // fresh) would self-deadlock on the dropped table's schema lock.
        var (connection, transaction, owns) = BeginWrite();
        try
        {
            string qualified = Quote(schemaName) + "." + Quote(tableName);
            string create = BuildCreateTable(qualified, columns, primaryKey, uniques, defaults, textType);
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = ifNotExists
                ? $"IF OBJECT_ID({ObjectLiteral(schemaName, tableName)}, 'U') IS NULL {create}"
                : create;
            cmd.ExecuteNonQuery();
        }
        finally
        {
            if (owns)
            {
                connection.Dispose();
            }
        }
    }

    public void DropTable(string schemaName, string tableName, bool ifExists)
    {
        string qualified = Quote(schemaName) + "." + Quote(tableName);
        ExecuteNonQuery((ifExists ? "DROP TABLE IF EXISTS " : "DROP TABLE ") + qualified);
    }

    public void CreateSchema(string schemaName, bool ifNotExists)
    {
        // CREATE SCHEMA must be the first statement in its batch, so run it via
        // EXEC; that also lets us guard it with IF SCHEMA_ID(...) IS NULL.
        string createExec = "EXEC('CREATE SCHEMA " + Quote(schemaName).Replace("'", "''") + "')";
        ExecuteNonQuery(ifNotExists
            ? $"IF SCHEMA_ID(N'{schemaName.Replace("'", "''")}') IS NULL {createExec}"
            : createExec);
    }

    public void DropSchema(string schemaName, bool ifExists)
    {
        ExecuteNonQuery((ifExists ? "DROP SCHEMA IF EXISTS " : "DROP SCHEMA ") + Quote(schemaName));
    }

    public void AlterTable(int alterKind, string schemaName, string tableName, string? arg1, string? arg2,
                           Field? column, int flags)
    {
        string qualified = Quote(schemaName) + "." + Quote(tableName);
        bool ifFlag = (flags & AlterKind.FlagIfExists) != 0;
        switch (alterKind)
        {
            case AlterKind.RenameTable:
                // sp_rename: @objname may be schema-qualified; @newname must NOT be.
                ExecuteNonQuery($"EXEC sp_rename N'{(Quote(schemaName) + "." + Quote(tableName)).Replace("'", "''")}', " +
                                $"N'{RequireArg(arg1, "new table name").Replace("'", "''")}'");
                break;
            case AlterKind.RenameColumn:
                ExecuteNonQuery(
                    $"EXEC sp_rename N'{(Quote(schemaName) + "." + Quote(tableName) + "." + Quote(RequireArg(arg1, "old column"))).Replace("'", "''")}', " +
                    $"N'{RequireArg(arg2, "new column").Replace("'", "''")}', 'COLUMN'");
                break;
            case AlterKind.AddColumn:
            {
                var field = RequireField(column, "added column");
                string colDef = Quote(RequireArg(arg1, "column name")) + " " + MapArrowToSqlType(field.DataType) +
                                (field.IsNullable ? " NULL" : " NOT NULL");
                string add = $"ALTER TABLE {qualified} ADD {colDef}";
                ExecuteNonQuery(ifFlag
                    ? $"IF COL_LENGTH({ObjectLiteral(schemaName, tableName)}, N'{RequireArg(arg1, "column name").Replace("'", "''")}') IS NULL " +
                      $"EXEC('{add.Replace("'", "''")}')"
                    : add);
                break;
            }
            case AlterKind.DropColumn:
                ExecuteNonQuery($"ALTER TABLE {qualified} DROP COLUMN {(ifFlag ? "IF EXISTS " : string.Empty)}" +
                                Quote(RequireArg(arg1, "column name")));
                break;
            case AlterKind.ColumnType:
            {
                // SQL Server defaults an ALTER COLUMN with no NULL/NOT NULL to NULLable,
                // so restate the column's current nullability explicitly.
                string col = RequireArg(arg1, "column name");
                bool nullable = ColumnIsNullable(schemaName, tableName, col);
                ExecuteNonQuery($"ALTER TABLE {qualified} ALTER COLUMN {Quote(col)} " +
                                MapArrowToSqlType(RequireField(column, "column type").DataType) +
                                (nullable ? " NULL" : " NOT NULL"));
                break;
            }
            case AlterKind.SetNotNull:
            case AlterKind.DropNotNull:
            {
                // SQL Server ALTER COLUMN must restate the type; reconstruct the
                // column's current type from the catalog and toggle nullability.
                string col = RequireArg(arg1, "column name");
                string type = ColumnTypeInfo(schemaName, tableName, col).fullType;
                ExecuteNonQuery($"ALTER TABLE {qualified} ALTER COLUMN {Quote(col)} {type} " +
                                (alterKind == AlterKind.SetNotNull ? "NOT NULL" : "NULL"));
                break;
            }
            case AlterKind.SetDefault:
            {
                // SQL Server defaults are named constraints: replace any existing one.
                string col = RequireArg(arg1, "column name");
                using var connection = OpenConnection();
                connection.Open();
                DropColumnDefault(connection, schemaName, tableName, col);
                string dataType = ColumnTypeInfo(schemaName, tableName, col).dataType;
                string literal = RenderDefaultBySqlType(dataType, DecodeDefaultArg(arg2));
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"ALTER TABLE {qualified} ADD DEFAULT ({literal}) FOR {Quote(col)}";
                cmd.ExecuteNonQuery();
                break;
            }
            case AlterKind.DropDefault:
            {
                string col = RequireArg(arg1, "column name");
                using var connection = OpenConnection();
                connection.Open();
                DropColumnDefault(connection, schemaName, tableName, col);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(alterKind), alterKind, "mssql_net: unknown alter kind");
        }
    }

    // Reconstructs a column's current SQL type (with length/precision) from the
    // catalog, e.g. "nvarchar(max)", "decimal(10,2)", "datetime2(7)", "int".
    private (string fullType, string dataType) ColumnTypeInfo(string schemaName, string tableName, string columnName)
    {
        using var connection = OpenConnection();
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, DATETIME_PRECISION " +
            "FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = @s AND TABLE_NAME = @t AND COLUMN_NAME = @c";
        cmd.Parameters.AddWithValue("@s", schemaName);
        cmd.Parameters.AddWithValue("@t", tableName);
        cmd.Parameters.AddWithValue("@c", columnName);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            throw new ArgumentException($"mssql_net: column '{columnName}' not found on {schemaName}.{tableName}");
        }
        string dataType = reader.GetString(0);
        int? charLen = reader.IsDBNull(1) ? null : Convert.ToInt32(reader.GetValue(1));
        int? numPrec = reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2));
        int numScale = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3));
        int? dtPrec = reader.IsDBNull(4) ? null : Convert.ToInt32(reader.GetValue(4));

        string full = dataType.ToLowerInvariant() switch
        {
            "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary" =>
                $"{dataType}({(charLen == -1 ? "max" : charLen?.ToString())})",
            "decimal" or "numeric" => $"{dataType}({numPrec},{numScale})",
            "datetime2" or "time" or "datetimeoffset" => dtPrec is null ? dataType : $"{dataType}({dtPrec})",
            _ => dataType,
        };
        return (full, dataType);
    }

    // Drops any DEFAULT constraint bound to a column (no-op if none).
    private static void DropColumnDefault(SqlConnection connection, string schemaName, string tableName, string column)
    {
        using var find = connection.CreateCommand();
        find.CommandText =
            "SELECT dc.name FROM sys.default_constraints dc " +
            "JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id " +
            "WHERE dc.parent_object_id = OBJECT_ID(@obj) AND c.name = @col";
        find.Parameters.AddWithValue("@obj", schemaName + "." + tableName);
        find.Parameters.AddWithValue("@col", column);
        if (find.ExecuteScalar() is string name)
        {
            using var drop = connection.CreateCommand();
            drop.CommandText = $"ALTER TABLE {Quote(schemaName)}.{Quote(tableName)} DROP CONSTRAINT {Quote(name)}";
            drop.ExecuteNonQuery();
        }
    }

    // Decodes the SET DEFAULT arg2: "-" => DEFAULT NULL; "b"+base64 => literal text.
    private static string? DecodeDefaultArg(string? arg2)
    {
        if (arg2 == "-")
        {
            return null;
        }
        if (arg2 is not null && arg2.StartsWith('b'))
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(arg2.Substring(1)));
        }
        throw new ArgumentException("mssql_net: SET DEFAULT requires a literal value");
    }

    // Renders a literal DEFAULT, quoting by the column's SQL data type.
    private static string RenderDefaultBySqlType(string dataType, string? value)
    {
        if (value is null)
        {
            return "NULL";
        }
        string dt = dataType.ToLowerInvariant();
        if (dt == "bit")
        {
            return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("t", StringComparison.OrdinalIgnoreCase) || value == "1"
                ? "1"
                : "0";
        }
        switch (dt)
        {
            case "tinyint":
            case "smallint":
            case "int":
            case "bigint":
            case "decimal":
            case "numeric":
            case "real":
            case "float":
            case "money":
            case "smallmoney":
                return value; // numeric literal, no quoting
            default:
                return "N'" + value.Replace("'", "''") + "'";
        }
    }

    // Reads a column's current nullability from the catalog (defaults to nullable
    // if the column can't be found, matching SQL Server's ALTER COLUMN default).
    private bool ColumnIsNullable(string schemaName, string tableName, string columnName)
    {
        using var connection = OpenConnection();
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS " +
                          "WHERE TABLE_SCHEMA = @s AND TABLE_NAME = @t AND COLUMN_NAME = @c";
        cmd.Parameters.AddWithValue("@s", schemaName);
        cmd.Parameters.AddWithValue("@t", tableName);
        cmd.Parameters.AddWithValue("@c", columnName);
        var result = cmd.ExecuteScalar() as string;
        return !string.Equals(result, "NO", StringComparison.OrdinalIgnoreCase);
    }

    private static string RequireArg(string? value, string what) =>
        string.IsNullOrEmpty(value) ? throw new ArgumentException($"mssql_net: ALTER TABLE missing {what}") : value;

    private static Field RequireField(Field? field, string what) =>
        field ?? throw new ArgumentException($"mssql_net: ALTER TABLE missing {what}");

    private static string ObjectLiteral(string schemaName, string tableName) =>
        "N'" + (schemaName + "." + tableName).Replace("'", "''") + "'";

    private static (string schema, string table) Require(string? schema, string? table)
    {
        if (string.IsNullOrEmpty(schema) || string.IsNullOrEmpty(table))
        {
            throw new ArgumentException("mssql_net: metadata kind requires schema and table names");
        }
        return (schema, table);
    }

    // User schemas only (exclude system / fixed database roles).
    private const string SchemasSql =
        "SELECT s.name FROM sys.schemas s " +
        "WHERE s.name NOT IN ('sys','INFORMATION_SCHEMA','guest','db_owner','db_accessadmin'," +
        "'db_securityadmin','db_ddladmin','db_backupoperator','db_datareader','db_datawriter'," +
        "'db_denydatareader','db_denydatawriter') ORDER BY s.name";

    // Base tables and views across all schemas, with a uniform (schema, table, type) shape.
    private const string TablesSql =
        "SELECT s.name AS schema_name, t.name AS table_name, 'BASE TABLE' AS table_type " +
        "FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id " +
        "UNION ALL " +
        "SELECT s.name, v.name, 'VIEW' " +
        "FROM sys.views v JOIN sys.schemas s ON v.schema_id = s.schema_id " +
        "ORDER BY 1, 2";

    // Row-identity columns in key order: the primary key if present, else the
    // unique index with the fewest columns (tie-break by index_id).
    private static string RowIdSql(string schema, string table)
    {
        string objectLiteral = "N'" + (schema + "." + table).Replace("'", "''") + "'";
        return "SELECT c.name FROM sys.indexes i " +
               "JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
               "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
               "WHERE i.object_id = OBJECT_ID(" + objectLiteral + ") AND i.index_id = (" +
               "  SELECT TOP 1 i2.index_id FROM sys.indexes i2 " +
               "  JOIN sys.index_columns ic2 ON ic2.object_id = i2.object_id AND ic2.index_id = i2.index_id " +
               "  WHERE i2.object_id = OBJECT_ID(" + objectLiteral + ") AND (i2.is_primary_key = 1 OR i2.is_unique = 1) " +
               "  GROUP BY i2.index_id, i2.is_primary_key " +
               "  ORDER BY i2.is_primary_key DESC, COUNT(*) ASC, i2.index_id ASC) " +
               "ORDER BY ic.key_ordinal";
    }

    // IDENTITY column names of an existing table (case-insensitive set).
    private static HashSet<string> GetIdentityColumns(SqlConnection connection, SqlTransaction? transaction,
                                                      string schemaName, string tableName)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT c.name FROM sys.columns c WHERE c.object_id = OBJECT_ID(@obj) AND c.is_identity = 1";
        cmd.Parameters.AddWithValue("@obj", schemaName + "." + tableName);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    private static string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    private static string BuildCreateTable(string qualified, Schema schema) =>
        BuildCreateTable(qualified, schema, null, null, null, null);

    private static string BuildCreateTable(string qualified, Schema schema, string? primaryKey, string? uniques,
                                           string? defaults, string? textType)
    {
        var defaultMap = ParseDefaults(defaults);
        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").Append(qualified).Append(" (");
        for (int i = 0; i < schema.FieldsList.Count; i++)
        {
            var field = schema.FieldsList[i];
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append(Quote(field.Name)).Append(' ').Append(MapArrowToSqlType(field.DataType, textType))
              .Append(field.IsNullable ? " NULL" : " NOT NULL");
            if (defaultMap.TryGetValue(i, out var defaultValue))
            {
                sb.Append(" DEFAULT ").Append(RenderDefault(field.DataType, defaultValue));
            }
        }
        var pk = ParseIndexGroup(primaryKey);
        if (pk.Count > 0)
        {
            sb.Append(", PRIMARY KEY (").Append(ColumnList(schema, pk)).Append(')');
        }
        foreach (var group in ParseIndexGroups(uniques))
        {
            sb.Append(", UNIQUE (").Append(ColumnList(schema, group)).Append(')');
        }
        sb.Append(')');
        return sb.ToString();
    }

    private static string ColumnList(Schema schema, List<int> indices) =>
        string.Join(", ", indices.Select(i => Quote(schema.FieldsList[i].Name)));

    // "0,1" -> [0,1]; null/empty -> [].
    private static List<int> ParseIndexGroup(string? group) =>
        string.IsNullOrEmpty(group)
            ? new List<int>()
            : group.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();

    // "2;3,4" -> [[2],[3,4]]; null/empty -> [].
    private static List<List<int>> ParseIndexGroups(string? groups) =>
        string.IsNullOrEmpty(groups)
            ? new List<List<int>>()
            : groups.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(ParseIndexGroup).ToList();

    // "0 aGVsbG8= 2 -" -> {0:"hello", 2:null}. Payload is base64(value-text), or
    // "-" for DEFAULT NULL. Key present => the column has a default.
    private static Dictionary<int, string?> ParseDefaults(string? spec)
    {
        var map = new Dictionary<int, string?>();
        if (string.IsNullOrEmpty(spec))
        {
            return map;
        }
        var tokens = spec.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < tokens.Length; i += 2)
        {
            int index = int.Parse(tokens[i]);
            string payload = tokens[i + 1];
            map[index] = payload == "-"
                ? null
                : System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        }
        return map;
    }

    // Renders a literal DEFAULT for a column, quoting by Arrow type.
    private static string RenderDefault(IArrowType type, string? value)
    {
        if (value is null)
        {
            return "NULL";
        }
        switch (type.TypeId)
        {
            case ArrowTypeId.Boolean:
                return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                       value.Equals("t", StringComparison.OrdinalIgnoreCase) || value == "1"
                    ? "1"
                    : "0";
            case ArrowTypeId.Int8:
            case ArrowTypeId.UInt8:
            case ArrowTypeId.Int16:
            case ArrowTypeId.UInt16:
            case ArrowTypeId.Int32:
            case ArrowTypeId.UInt32:
            case ArrowTypeId.Int64:
            case ArrowTypeId.UInt64:
            case ArrowTypeId.Float:
            case ArrowTypeId.Double:
            case ArrowTypeId.Decimal128:
                return value; // numeric literal, no quoting
            default:
                return "N'" + value.Replace("'", "''") + "'";
        }
    }

    // Arrow type -> SQL Server column type (provider-specific).
    private static string MapArrowToSqlType(IArrowType type, string? textType = null)
    {
        switch (type.TypeId)
        {
            case ArrowTypeId.Boolean: return "BIT";
            case ArrowTypeId.Int8: return "SMALLINT"; // signed; SQL TINYINT is unsigned
            case ArrowTypeId.UInt8: return "TINYINT";
            case ArrowTypeId.Int16: return "SMALLINT";
            case ArrowTypeId.UInt16: return "INT";
            case ArrowTypeId.Int32: return "INT";
            case ArrowTypeId.UInt32: return "BIGINT";
            case ArrowTypeId.Int64: return "BIGINT";
            case ArrowTypeId.UInt64: return "DECIMAL(20,0)";
            case ArrowTypeId.Float: return "REAL";
            case ArrowTypeId.Double: return "FLOAT";
            case ArrowTypeId.Decimal128:
            {
                var d = (Decimal128Type)type;
                return $"DECIMAL({d.Precision},{d.Scale})";
            }
            case ArrowTypeId.Date32:
            case ArrowTypeId.Date64:
                return "DATE";
            case ArrowTypeId.Time32:
            case ArrowTypeId.Time64:
                return "TIME(7)";
            case ArrowTypeId.Timestamp:
                return ((TimestampType)type).Timezone != null ? "DATETIMEOFFSET(7)" : "DATETIME2(7)";
            case ArrowTypeId.Binary:
                return "VARBINARY(MAX)";
            case ArrowTypeId.String:
            default:
                return string.IsNullOrWhiteSpace(textType) ? "NVARCHAR(MAX)" : textType!;
        }
    }

    public void Dispose()
    {
        // Roll back and release a still-open transaction (e.g. on DETACH mid-txn).
        if (_inTransaction || _txn is not null)
        {
            try
            {
                RollbackTransaction();
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
