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
    /// transaction).</summary>
    /// <remarks>
    /// <para><b>⚠⚠ ERROR 208 DOES NOT MEAN "THIS OBJECT IS MISSING" — it means SOME object named in the
    /// statement is, and for a VIEW that is usually a different one.</b> An INVALID view (one whose
    /// underlying table or column has been dropped) raises 208 naming the REFERENT, plus a second line
    /// <c>"Could not use view or function 'x' because of binding errors"</c>. Attributing that to the view
    /// itself is a wrong answer about an object that plainly exists — and a destructive one, because the
    /// host treats <see cref="ObjectNotFoundException"/> as established absence and ERASES the name from
    /// the catalog.</para>
    /// <para><b>⚠⚠ MEASURED, and it crashed the process rather than merely misreporting.</b> A production
    /// database with a handful of invalid views segfaulted <c>duckdb_tables()</c> — 3 runs in 4 on a
    /// 20-invalid-view reproduction — because the erase fired while the enumeration was iterating the very
    /// map it erases from. The iterator bug is fixed separately in <c>FabricatorSchemaEntry::Scan</c>; this
    /// is the half that stops a live object being reported as absent in the first place.</para>
    /// <para><b>⚠ SO ABSENCE IS ESTABLISHED, NOT INFERRED</b> — the same rule the 2026-08-01 fix wrote down
    /// and this site then broke by proxy: ask the server whether the object exists. ONE extra round trip,
    /// on the failure path only.</para>
    /// <para><b>⚠ AND WHEN THE PROBE ITSELF FAILS WE CLAIM NOTHING.</b> Unknown is not absence, so the
    /// original 208 is surfaced with SQL Server's own message rather than converted into a
    /// "does not exist" the caller would act on.</para>
    /// </remarks>
    internal Schema ColumnsSchemaCore(string schemaName, string tableName)
    {
        try
        {
            using var probe = ExecuteMetadataQuery(
                $"SELECT * FROM {Quote(schemaName)}.{Quote(tableName)} WHERE 1 = 0");
            return WithoutHiddenRowId(probe.Schema);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == InvalidObjectNameError)
        {
            if (ObjectStillExists(schemaName, tableName) != false)
            {
                // It exists (or we could not find out): report what the SERVER said. The message names the
                // object that is really missing, which is the one piece of information a user needs and the
                // one the old "table does not exist" threw away.
                throw new InvalidOperationException(
                    $"fabricator: '{schemaName}.{tableName}' exists but cannot be described — it is most " +
                    $"likely an INVALID view (a table or column it references has been dropped or renamed). " +
                    $"SQL Server said: {ex.Message}", ex);
            }
            throw new ObjectNotFoundException("table", $"{schemaName}.{tableName}", ex);
        }
    }

    private static int HiddenRowIdIndex(Schema schema)
    {
        for (int k = 0; k < schema.FieldsList.Count; k++)
        {
            if (string.Equals(schema.FieldsList[k].Name, HiddenRowIdColumn, StringComparison.OrdinalIgnoreCase))
            {
                return k;
            }
        }
        return -1;
    }

    /// <summary>The schema without the hidden row-id column (see <see cref="HiddenRowIdColumn"/>). Matched by
    /// exact name, case-insensitively as SQL Server resolves it.</summary>
    internal static Schema WithoutHiddenRowId(Schema schema)
    {
        int i = HiddenRowIdIndex(schema);
        if (i < 0)
        {
            return schema;
        }
        var fields = schema.FieldsList.Where((_, k) => k != i).ToList();
        return new Schema(fields, schema.Metadata);
    }

    /// <summary>The batch without the hidden row-id column, for results a statement returns to DuckDB
    /// (INSERT … RETURNING reads OUTPUT INSERTED.*).</summary>
    internal static RecordBatch WithoutHiddenRowId(RecordBatch batch)
    {
        int i = HiddenRowIdIndex(batch.Schema);
        if (i < 0)
        {
            return batch;
        }
        var columns = Enumerable.Range(0, batch.ColumnCount).Where(k => k != i).Select(batch.Column).ToList();
        (batch.Column(i) as IDisposable)?.Dispose(); // the new batch owns the rest; nothing else holds this one
        return new RecordBatch(WithoutHiddenRowId(batch.Schema), columns, batch.Length);
    }

    /// <summary>TRUE if the object is present, FALSE if it is provably absent, NULL if the question could
    /// not be answered. Used only to classify a 208; the three-way answer is the point, because treating an
    /// unanswerable probe as "absent" is exactly the inference this exists to stop.</summary>
    /// <remarks>⚠ The identifiers are BRACKETED inside the literal: <c>OBJECT_ID</c> parses a multi-part
    /// NAME, so an object whose name contains a dot would otherwise resolve to nothing and be reported as
    /// absent — the failure mode this whole method exists to prevent, arriving by a different route.</remarks>
    private bool? ObjectStillExists(string schemaName, string tableName)
    {
        var literal = "N'" + (Quote(schemaName) + "." + Quote(tableName)).Replace("'", "''") + "'";
        try
        {
            foreach (var row in ReadMetadataRows(
                         $"SELECT CASE WHEN OBJECT_ID({literal}) IS NULL THEN '0' ELSE '1' END", 1))
            {
                return row[0] == "1";
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ── COMMENT ON + DEFAULT read-back ───────────────────────────────────────────────────────────────────
    //
    // A comment and the column defaults must be on the CatalogEntry when the host CONSTRUCTS it, so they ride
    // table_info, the entry MATERIALIZATION crossing — once per table, one round trip for both.
    //
    // ⚠⚠ PER TABLE, and every system view read WITH (NOLOCK). This used to be ONE query for the whole
    // database, and that deadlocked parallel transactions doing DDL (dbt's table swap at --threads > 1,
    // 2026-11-03). Every write through the catalog drops the cache, so the next materialization reloaded it
    // inside its transaction — and a database-wide read of sys.default_constraints waits for every OTHER
    // transaction's uncommitted CREATE/RENAME, while those transactions waited for this one in the client
    // (behind the cache lock, or for their own statement). SQL Server saw only an idle session holding locks,
    // so it detected nothing and the run hung forever. MEASURED against a session holding an uncommitted
    // CREATE TABLE with a default and a description: the database-wide reads block under READ COMMITTED,
    // and the defaults block even under READ UNCOMMITTED (dc.definition takes a lock on the pending object);
    // reading ONE committed object WITH (NOLOCK) returns at once. Per table the only object read is the one
    // being materialized, whose own describe runs in the same statement anyway.
    //
    // STALENESS: a comment or default written THROUGH this catalog drops the cache at the write site (every
    // ALTER, every COMMENT ON), so read-your-own-writes holds; one changed OUT OF BAND shows up on re-ATTACH
    // or after fabricator_refresh_cache (GetTables drops this cache).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TableMetadata> _meta =
        new(StringComparer.OrdinalIgnoreCase);

    // Bumped by InvalidateMetadata: a load that STARTED before a write must not cache what it read.
    private int _metaGeneration;

    // ⚠ Comments and DEFAULTS are two catalog surfaces behind two capability flags, read together because
    // they are wanted at the SAME moment by the same caller.
    private sealed class TableMetadata
    {
        internal string? Table;
        internal Dictionary<string, string>? Columns;
        internal Dictionary<string, string>? Defaults;
    }

    // class 1 = OBJECT_OR_COLUMN; minor_id 0 addresses the OBJECT and non-zero the column at that ordinal,
    // resolved to a NAME here so the host never has to know about ordinals. MS_Description is the property
    // SSMS's "Description" box uses, which is what makes this interoperable in both directions.
    private const string CommentsSql = @"
SELECT N'c', CASE WHEN ep.minor_id = 0 THEN NULL ELSE c.name END, CAST(ep.value AS nvarchar(4000))
  FROM sys.extended_properties ep WITH (NOLOCK)
  LEFT JOIN sys.columns c WITH (NOLOCK) ON c.object_id = ep.major_id AND c.column_id = ep.minor_id
 WHERE ep.class = 1 AND ep.name = N'MS_Description' AND ep.major_id = @oid";

    // `definition` is SQL Server's own rendering and always arrives PARENTHESISED — ((10)), (N'2024-01-01'),
    // (getdate()) — which is what NormaliseDefault strips before the host tries to parse it.
    private const string DefaultsSql = @"
SELECT N'd', c.name, dc.definition
  FROM sys.default_constraints dc WITH (NOLOCK)
  JOIN sys.columns c WITH (NOLOCK) ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
 WHERE dc.parent_object_id = @oid";

    private TableMetadata? MetadataFor(string schemaName, string tableName)
    {
        var key = ExternalKey(schemaName, tableName);
        if (_meta.TryGetValue(key, out var cached))
        {
            return cached;
        }
        // ⚠ CAPABILITY-GATED, never probed: extended properties and DEFAULT constraints are not part of the
        // warehouse surface, and there a statement that errors inside an explicit transaction ABORTS the
        // transaction (docs/warehouse-support.md §6.5). Same gates as the WRITE side.
        var parts = new List<string>(2);
        if (Profile.SupportsExtendedProperties)
        {
            parts.Add(CommentsSql);
        }
        if (Profile.SupportsDefaultConstraints)
        {
            parts.Add(DefaultsSql);
        }
        var generation = System.Threading.Volatile.Read(ref _metaGeneration);
        var loaded = new TableMetadata();
        if (parts.Count > 0)
        {
            // OBJECT_ID BRACKETS the parts: it parses a multi-part NAME, so a dot inside a name would
            // otherwise resolve to nothing.
            var sql = "DECLARE @oid int = OBJECT_ID(@name);" + string.Join("\nUNION ALL", parts);
            var name = new Microsoft.Data.SqlClient.SqlParameter("@name", System.Data.SqlDbType.NVarChar, 600)
            {
                Value = Quote(schemaName) + "." + Quote(tableName),
            };
            foreach (var row in ReadMetadataRows(sql, 3, new[] { name }))
            {
                if (row[2] is not { } value)
                {
                    continue;
                }
                if (row[0] == "c")
                {
                    if (row[1] is { } columnName)
                    {
                        (loaded.Columns ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                            [columnName] = value;
                    }
                    else
                    {
                        loaded.Table = value;
                    }
                }
                else if (row[1] is { } columnName && NormaliseDefault(value) is { } normalised)
                {
                    (loaded.Defaults ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                        [columnName] = normalised;
                }
            }
        }
        if (System.Threading.Volatile.Read(ref _metaGeneration) == generation)
        {
            _meta[key] = loaded;
        }
        return loaded;
    }

    /// <summary>Turns SQL Server's own rendering of a DEFAULT into something DuckDB's parser can read, or
    /// null when there is nothing left. Dialect normalisation ONLY — it does not decide what is a literal,
    /// because that rule lives host-side where one policy point serves every provider.</summary>
    /// <remarks>SQL Server stores a default already parenthesised — <c>((10))</c>, <c>(N'2024-01-01')</c>,
    /// <c>(getdate())</c> — and uses the <c>N</c> prefix for a national-character literal, which DuckDB
    /// would read as a type-prefixed literal for a type called N. Both are stripped; everything else is
    /// passed through verbatim for the host to accept or drop.</remarks>
    internal static string? NormaliseDefault(string definition)
    {
        var text = definition.Trim();
        // Peel BALANCED outer parens only. A test for "starts with ( and ends with )" would mangle
        // `(a)+(b)` into `a)+(b`, which could then parse as something else entirely rather than fail.
        while (text.Length >= 2 && text[0] == '(' && text[text.Length - 1] == ')')
        {
            var depth = 0;
            var balanced = true;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '(')
                {
                    depth++;
                }
                else if (text[i] == ')')
                {
                    depth--;
                    if (depth == 0 && i != text.Length - 1)
                    {
                        balanced = false;
                        break;
                    }
                }
            }
            if (!balanced)
            {
                break;
            }
            text = text.Substring(1, text.Length - 2).Trim();
        }
        if (text.Length >= 2 && (text[0] == 'N' || text[0] == 'n') && text[1] == '\'')
        {
            text = text.Substring(1);
        }
        return text.Length == 0 ? null : text;
    }

    /// <summary>Drops the cached comments so the next read reloads them. Called wherever a comment is
    /// written through this catalog, which is what makes read-your-own-writes hold.</summary>
    internal void InvalidateMetadata()
    {
        System.Threading.Interlocked.Increment(ref _metaGeneration);
        _meta.Clear();
        _uncollatable = null;
        _boundedStrings = null;
    }

    private System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>? _uncollatable;

    /// <summary>
    /// Columns of this database whose server type takes no COLLATE clause (json, text, ntext, xml,
    /// sql_variant, CLR types), keyed by <see cref="ExternalKey"/>. The remote pushdown reads them: they surface
    /// as DuckDB strings, and the T-SQL emitter gives every string its collation. Loaded once, dropped with the
    /// other database-wide metadata (fabricator_refresh_cache / fabricator_invalidate_cache).
    /// </summary>
    internal System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>> UncollatableColumns()
    {
        if (_uncollatable is { } cached)
        {
            return cached;
        }
        var loaded = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        const string sql =
            "SELECT s.name, o.name, c.name, TYPE_NAME(c.system_type_id) " +
            "FROM sys.columns c WITH (NOLOCK) JOIN sys.objects o WITH (NOLOCK) ON o.object_id = c.object_id " +
            "JOIN sys.schemas s WITH (NOLOCK) ON s.schema_id = o.schema_id " +
            "WHERE o.type IN ('U', 'V') AND (c.system_type_id IN (35, 98, 99, 240, 241) " +
            "OR TYPE_NAME(c.system_type_id) = 'json')";
        foreach (var row in ReadMetadataRows(sql, 4))
        {
            if (row[0] is null || row[1] is null || row[2] is null)
            {
                continue;
            }
            string key = ExternalKey(row[0]!, row[1]!);
            if (!loaded.TryGetValue(key, out var columns))
            {
                columns = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                loaded[key] = columns;
            }
            columns[row[2]!] = row[3] ?? "clr";
        }
        _uncollatable = loaded;
        return loaded;
    }

    /// <summary>
    /// [FABRICATOR-PATCH: OURS-BY-DESIGN] The string columns (of tables, views and table functions) whose values fit an
    /// exact equality key of varchar(8000) — at most 2666 characters, three UTF-8 bytes each plus the key's sentinel —
    /// with their declared length in bytes and whether they are nvarchar/nchar, keyed by <see cref="ExternalKey"/>. The emitter checks every value against
    /// that length, so a column widened since this was loaded fails the query instead of being cut. Loaded once, dropped
    /// with the other database-wide metadata.
    /// </summary>
    internal System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, (int Bytes, bool Unicode)>> BoundedStringColumns()
    {
        if (_boundedStrings is { } cached)
        {
            return cached;
        }
        var loaded = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, (int Bytes, bool Unicode)>>(StringComparer.OrdinalIgnoreCase);
        const string sql =
            "SELECT s.name, o.name, c.name, CAST(c.max_length AS VARCHAR(10)), " +
            "CASE WHEN c.system_type_id IN (231, 239) THEN '1' ELSE '0' END " +
            "FROM sys.columns c WITH (NOLOCK) JOIN sys.objects o WITH (NOLOCK) ON o.object_id = c.object_id " +
            "JOIN sys.schemas s WITH (NOLOCK) ON s.schema_id = o.schema_id " +
            "WHERE o.type IN ('U', 'V', 'IF', 'TF') AND c.max_length > 0 AND (" +
            "(c.system_type_id IN (167, 175) AND c.max_length <= 2666) OR " +
            "(c.system_type_id IN (231, 239) AND c.max_length <= 5332))";
        foreach (var row in ReadMetadataRows(sql, 5))
        {
            if (row[0] is null || row[1] is null || row[2] is null ||
                !int.TryParse(row[3], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int bytes))
            {
                continue;
            }
            string key = ExternalKey(row[0]!, row[1]!);
            if (!loaded.TryGetValue(key, out var columns))
            {
                columns = new System.Collections.Generic.Dictionary<string, (int Bytes, bool Unicode)>(StringComparer.OrdinalIgnoreCase);
                loaded[key] = columns;
            }
            columns[row[2]!] = (bytes, row[4] == "1");
        }
        _boundedStrings = loaded;
        return loaded;
    }

    private System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, (int Bytes, bool Unicode)>>? _boundedStrings;

    /// <summary>The table's own comment (the MS_Description extended property), or null.</summary>
    internal string? TableCommentCore(string schemaName, string tableName) =>
        MetadataFor(schemaName, tableName)?.Table;

    /// <summary>Per-column comments for the table, or null when it has none.</summary>
    internal IReadOnlyDictionary<string, string>? ColumnCommentsCore(string schemaName, string tableName) =>
        MetadataFor(schemaName, tableName)?.Columns;

    /// <summary>Per-column DEFAULT expressions for the table, normalised out of T-SQL, or null when it has
    /// none. What the HOST does with them is much narrower — it keeps only constants.</summary>
    internal IReadOnlyDictionary<string, string>? ColumnDefaultsCore(string schemaName, string tableName) =>
        MetadataFor(schemaName, tableName)?.Defaults;

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

    /// <summary>Approximate row count from partition stats (a cheap metadata read, not COUNT(*)). On a warehouse
    /// engine, where neither sys.partitions nor the stats DMV answers, an exact COUNT_BIG(*) that Fabric serves from
    /// metadata (<see cref="WarehouseRowCountSql"/>) — written so it cannot fail, since there a failed statement
    /// ABORTS an open transaction (the standing rule).</summary>
    internal long? RowCountCore(string schemaName, string tableName)
    {
        if (Profile.IsWarehouse && !Profile.IsFabricWarehouse)
        {
            return null; // Synapse dedicated: unmeasured, and it has neither the DMV nor (here) a counting path
        }
        string sql = Profile.IsFabricWarehouse ? WarehouseRowCountSql(schemaName, tableName) : RowCountSql(schemaName, tableName);
        foreach (var row in ReadMetadataRows(sql, 1))
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
    private sealed class SqlServerTableBinding : ITableBinding, IDeleteWhereShape
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

        /// <inheritdoc/>
        public string? TableComment() =>
            _catalog.TableCommentCore(_definition.SchemaName, _definition.TableName);

        /// <inheritdoc/>
        public IReadOnlyDictionary<string, string>? ColumnComments() =>
            _catalog.ColumnCommentsCore(_definition.SchemaName, _definition.TableName);

        /// <inheritdoc/>
        public IReadOnlyDictionary<string, string>? ColumnDefaults() =>
            _catalog.ColumnDefaultsCore(_definition.SchemaName, _definition.TableName);

        public long? ApproximateRowCount() =>
            _catalog.RowCountCore(_definition.SchemaName, _definition.TableName);

        public IReadOnlyList<NdvEntry> ColumnNdv() =>
            _catalog.ColumnNdvCore(_definition.SchemaName, _definition.TableName);

        public IArrowArrayStream Scan(string? specJson, IArrowArrayStream? filterValues) =>
            _catalog.ScanTableCore(_definition.SchemaName, _definition.TableName, specJson, filterValues);

        /// <inheritdoc/>
        public bool SupportsDeleteWhere =>
            _catalog.SupportsDeleteWhereCore(_definition.SchemaName, _definition.TableName);

        /// <inheritdoc/>
        public long DeleteWhere(FilterNode? filter, IArrowArrayStream? filterValues) =>
            _catalog.DeleteWhereCore(_definition.SchemaName, _definition.TableName, filter, filterValues);

        /// <inheritdoc/>
        public bool SupportsUpdateWhere =>
            _catalog.SupportsUpdateWhereCore(_definition.SchemaName, _definition.TableName);

        // An explicit transaction still plans the shortcut: an ordinary table's statement runs on the transaction's
        // pinned connection, and an external Delta table's DeleteWhere reads through the transaction's Delta
        // session, so both see its own writes.
        bool IDeleteWhereShape.DeleteWhereAutocommitOnly => false;

        IReadOnlyList<string>? IDeleteWhereShape.DeleteWhereColumns => null;

        // An external Delta table's DELETE is evaluated by the Delta engine (bytes, no collation), so its strings
        // are exact; an ordinary table's are compared by SQL Server's collation and are not.
        bool IDeleteWhereShape.ExactStrings =>
            _catalog.IsExternalDeltaCore(_definition.SchemaName, _definition.TableName);

        /// <inheritdoc/>
        public long UpdateWhere(IReadOnlyList<SetAssignment> set, FilterNode? filter, IArrowArrayStream? filterValues) =>
            _catalog.UpdateWhereCore(_definition.SchemaName, _definition.TableName, set, filter, filterValues);

        public void Dispose()
        {
            // Nothing held: the stream returned by Scan owns its connection, and the transaction is the
            // routing's business.
        }
    }
}

/// <summary>A stream with the hidden row-id column removed from its schema and from every batch.</summary>
internal sealed class HiddenRowIdStream(IArrowArrayStream inner) : IArrowArrayStream
{
    public Schema Schema { get; } = SqlServerCatalog.WithoutHiddenRowId(inner.Schema);

    public async System.Threading.Tasks.ValueTask<RecordBatch?> ReadNextRecordBatchAsync(
        System.Threading.CancellationToken cancellationToken = default)
    {
        var batch = await inner.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
        return batch is null ? null : SqlServerCatalog.WithoutHiddenRowId(batch);
    }

    public void Dispose() => inner.Dispose();
}
