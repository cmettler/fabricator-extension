// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Fabricator.Bridge;

namespace Fabricator.SqlServer;

// The SQL Server half of slice 4c (docs/catalog-table-abstraction.md §2.2/§2.3): the table DEFINITION and
// the deliberately THIN bound table — SQL Server holds no per-(table × transaction) state worth an object
// (the connection/routing belongs to SqlServerTransaction and is resolved ambiently by the scan routing),
// so the bound table mostly borrows and delegates. The TYPED members are primary since 4c: the
// GetMetadata kind arms in SqlServerBackend.cs re-encode them into the streams the current transport
// carries, and slice 4d's table_* session retires the re-encoding.
public sealed partial class SqlServerCatalog
{
    /// <summary>The SQL Server <see cref="ITable"/> — transient in the current transport (see
    /// the interface remarks; nothing is cached on it, so it cannot go stale).</summary>
    public ITable GetTable(string schemaName, string tableName) =>
        new SqlServerTableDefinition(this, schemaName, tableName);

    /// <summary>Binds (schema, table) against the AMBIENT transaction — the metadata/scan adapters' path
    /// onto the object model.</summary>
    private SqlServerTableBinding BindAmbient(string schemaName, string tableName) =>
        (SqlServerTableBinding)GetTable(schemaName, tableName)
            .Bind(AmbientTransaction.Current is var t && t != 0 ? _txns.TryGet(t) : null, at: null);

    // ── the typed cores the bound table delegates to (the catalog owns connections + profile) ───────────

    /// <summary>The table's Arrow schema from a zero-row describe (<c>SELECT * … WHERE 1 = 0</c>) on the
    /// metadata connection (read-your-writes — a just-created table must be visible in its own
    /// transaction). Absence = SQL Server error 208, classified as <see cref="ObjectNotFoundException"/>;
    /// see the kind-2 adapter's remarks in SqlServerBackend.cs for why 208 and only 208.</summary>
    internal Schema ColumnsSchemaCore(string schemaName, string tableName)
    {
        try
        {
            using var probe = ExecuteMetadataQuery(
                $"SELECT * FROM {Quote(schemaName)}.{Quote(tableName)} WHERE 1 = 0");
            return probe.Schema;
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == InvalidObjectNameError)
        {
            throw new ObjectNotFoundException("table", $"{schemaName}.{tableName}", ex);
        }
    }

    /// <summary>The rowid column names: a detected external DELTA table with a Delta IDENTITY column
    /// advertises THAT column (slice D — an external table has no PK/UNIQUE/IDENTITY SQL-side); everything
    /// else runs the standard PK / smallest-unique-index / IDENTITY discovery (RowIdSql, whose engine-flipped
    /// precedence is documented there). Empty = no rowid, UPDATE/DELETE unavailable.</summary>
    internal IReadOnlyList<string> RowIdColumnsCore(string schemaName, string tableName)
    {
        if (DetectExternalTable(schemaName, tableName) is { IdentityColumn: { } idCol })
        {
            return new[] { idCol };
        }
        var names = new List<string>();
        foreach (var row in ReadMetadataRows(RowIdSql(schemaName, tableName, Profile), 1))
        {
            if (row[0] is { } name)
            {
                names.Add(name);
            }
        }
        return names;
    }

    /// <summary>Approximate row count from partition stats (a cheap metadata read, not COUNT(*)). NULL on a
    /// warehouse engine — sys.dm_db_partition_stats is unsupported there, and on Fabric a failed best-effort
    /// statement ABORTS an open transaction, so the probe must never be issued (the standing rule).</summary>
    internal long? RowCountCore(string schemaName, string tableName)
    {
        if (Profile.IsWarehouse)
        {
            return null;
        }
        foreach (var row in ReadMetadataRows(RowCountSql(schemaName, tableName), 1))
        {
            if (row[0] is { } text && long.TryParse(text, out long n))
            {
                return n;
            }
        }
        return null;
    }

    /// <summary>Per-column NDV from the leading-column histogram (costing only). Empty on a warehouse
    /// engine, for the same never-issue-a-swallowable-statement rule as <see cref="RowCountCore"/>.</summary>
    internal IReadOnlyList<NdvEntry> ColumnNdvCore(string schemaName, string tableName)
    {
        if (Profile.IsWarehouse)
        {
            return System.Array.Empty<NdvEntry>();
        }
        var entries = new List<NdvEntry>();
        foreach (var row in ReadMetadataRows(ColumnNdvSql(schemaName, tableName), 2))
        {
            if (row[0] is { } column && row[1] is { } text && long.TryParse(text, out long ndv))
            {
                entries.Add(new NdvEntry(column, ndv));
            }
        }
        return entries;
    }

    // Nested (not top-level) so they can name the private nested SqlServerTransaction.

    /// <summary>The SQL Server table definition — identity only (see the file remarks).</summary>
    private sealed class SqlServerTableDefinition : ITable
    {
        private readonly SqlServerCatalog _catalog;

        internal SqlServerTableDefinition(SqlServerCatalog catalog, string schemaName, string tableName)
        {
            _catalog = catalog;
            SchemaName = schemaName;
            TableName = tableName;
        }

        public string SchemaName { get; }
        public string TableName { get; }

        /// <summary>Always a fresh thin instance — SQL Server has no per-(table × transaction) state worth
        /// memoizing (the interface permits, not demands, memoization). The AT clause is carried but does
        /// not change the SCHEMA answer on this provider: box/Azure temporal history keeps the current shape
        /// and Fabric refuses time travel across DDL (measured — docs/known-limitations.md §1.x/§1.y), which
        /// is exactly why the as-of describe is per PROVIDER.</summary>
        public ITableBinding Bind(ITransaction? transaction, TableAt? at = null) =>
            new SqlServerTableBinding(_catalog, this, transaction as SqlServerTransaction, at);
    }

    /// <summary>The THIN SQL Server bound table: borrows the transaction's connection through the catalog's
    /// ambient scan routing (SqlServerScanRoute — pinned/pooled/drained/snapshot stays the routing's
    /// business) and delegates every resolution to the catalog's typed cores. Caller-owned; holds nothing
    /// to dispose.</summary>
    private sealed class SqlServerTableBinding : ITableBinding
    {
        private readonly SqlServerCatalog _catalog;
        private readonly SqlServerTableDefinition _definition;

        // Carried for the §2.3 contract (the bound table is (definition × transaction)); the scan ROUTING
        // still resolves the transaction ambiently, which 4d aligns when the ABI carries table handles.
        private readonly SqlServerTransaction? _transaction;
        private readonly TableAt? _at;

        internal SqlServerTableBinding(SqlServerCatalog catalog, SqlServerTableDefinition definition,
                                     SqlServerTransaction? transaction, TableAt? at)
        {
            _catalog = catalog;
            _definition = definition;
            _transaction = transaction;
            _at = at;
        }

        public Schema Schema => _catalog.ColumnsSchemaCore(_definition.SchemaName, _definition.TableName);

        public IReadOnlyList<string> RowIdColumns() =>
            _catalog.RowIdColumnsCore(_definition.SchemaName, _definition.TableName);

        /// <summary>No provider virtual columns (the Delta catalog's stable row-tracking pair has no SQL
        /// analog).</summary>
        public IReadOnlyList<VirtualColumn> VirtualColumns() => System.Array.Empty<VirtualColumn>();

        public long? ApproximateRowCount() =>
            _catalog.RowCountCore(_definition.SchemaName, _definition.TableName);

        public IReadOnlyList<NdvEntry> ColumnNdv() =>
            _catalog.ColumnNdvCore(_definition.SchemaName, _definition.TableName);

        public IArrowArrayStream Scan(string? specJson, IArrowArrayStream? filterValues) =>
            _catalog.ScanTableCore(_definition.SchemaName, _definition.TableName, specJson, filterValues);

        public void Dispose()
        {
            // Nothing held: the stream returned by Scan owns its connection, and the transaction is the
            // routing's business.
        }
    }
}
