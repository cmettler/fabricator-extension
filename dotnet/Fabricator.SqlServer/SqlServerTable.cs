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
            return probe.Schema;
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

    // ── COMMENT ON read-back ─────────────────────────────────────────────────────────────────────────────
    //
    // ⚠⚠ ONE query for the WHOLE database, cached, rather than one per table — and that is a correctness-
    // adjacent decision, not a micro-optimisation. A comment must be on the CatalogEntry when the host
    // CONSTRUCTS it, so it rides table_info, which is the ENTRY MATERIALIZATION crossing. Entry
    // materialization is what catalog ENUMERATION does to EVERY table (duckdb_tables(), and dbt introspects
    // before it builds anything), so a per-table query here would add one round trip per table to every
    // enumeration — the cost that this provider has already been burned by twice (the OneLake enumeration
    // slowness, and the 15871 discovery defect). sys.extended_properties is database-wide, so asking once
    // costs one query no matter how many tables materialize afterwards.
    //
    // ⚠ STALENESS: a comment or default written THROUGH this catalog invalidates the cache at the write
    // site (every ALTER, and every COMMENT ON), so read-your-own-writes holds. One changed OUT OF BAND
    // shows up on re-ATTACH or after fabricator_refresh_cache — the latter because GetTables drops this
    // cache, which is the ONLY thing that makes the refresh_cache half of that sentence true. It was NOT
    // true when this cache was first written: the claim was made by appealing to a contract _externalInfo
    // was said to document, and _externalInfo neither documents it nor lives in this file. Fixed and the
    // reasoning replaced 2026-09-09, after a surviving mutant showed the gap.
    private System.Collections.Generic.Dictionary<string, TableMetadata>? _meta;
    private readonly object _metaLock = new();

    // ⚠ Comments and DEFAULTS share one cache and one load although they are two catalog surfaces behind
    // two capability flags, because they are wanted at the SAME moment (entry materialization) and by the
    // same caller. The cost of sharing is that either kind of write reloads both, which is a re-read rather
    // than a wrong answer; the benefit is one lock, one lazy load, and no way for the two to disagree about
    // which tables exist.
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
SELECT s.name, o.name,
       CASE WHEN ep.minor_id = 0 THEN NULL ELSE c.name END,
       CAST(ep.value AS nvarchar(4000))
  FROM sys.extended_properties ep
  JOIN sys.objects o ON o.object_id = ep.major_id
  JOIN sys.schemas s ON s.schema_id = o.schema_id
  LEFT JOIN sys.columns c ON c.object_id = ep.major_id AND c.column_id = ep.minor_id
 WHERE ep.class = 1 AND ep.name = N'MS_Description'";

    // The column DEFAULTS, database-wide and in ONE query for the same reason the comments are. `definition`
    // is SQL Server's own rendering and always arrives PARENTHESISED — ((10)), (N'2024-01-01'), (getdate()) —
    // which is what NormaliseDefault strips before the host tries to parse it.
    private const string DefaultsSql = @"
SELECT s.name, o.name, c.name, dc.definition
  FROM sys.default_constraints dc
  JOIN sys.objects o ON o.object_id = dc.parent_object_id
  JOIN sys.schemas s ON s.schema_id = o.schema_id
  JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id";

    private Dictionary<string, TableMetadata> MetadataCache()
    {
        if (_meta is { } cached)
        {
            return cached;
        }
        lock (_metaLock)
        {
            if (_meta is { } raced)
            {
                return raced;
            }
            var loaded = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
            TableMetadata EntryFor(string schemaName, string tableName)
            {
                var cacheKey = ExternalKey(schemaName, tableName);
                if (!loaded.TryGetValue(cacheKey, out var found))
                {
                    found = new TableMetadata();
                    loaded[cacheKey] = found;
                }
                return found;
            }
            // ⚠ CAPABILITY-GATED, never probed: extended properties are not part of the warehouse surface,
            // and on those engines a statement that errors inside an explicit transaction ABORTS the
            // transaction — so a swallowed "are they here?" query would poison whatever the caller does
            // next (docs/warehouse-support.md §6.5). An empty cache is the honest answer there: no comments,
            // no statement issued. Same gate as the WRITE side, so the two cannot disagree.
            if (Profile.SupportsExtendedProperties)
            {
                foreach (var row in ReadMetadataRows(CommentsSql, 4))
                {
                    if (row[0] is not { } schemaName || row[1] is not { } tableName || row[3] is not { } descr)
                    {
                        continue;
                    }
                    var entry = EntryFor(schemaName, tableName);
                    if (row[2] is { } columnName)
                    {
                        (entry.Columns ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                            [columnName] = descr;
                    }
                    else
                    {
                        entry.Table = descr;
                    }
                }
            }
            // ⚠ Gated on its OWN capability, not on the comments' one: they are separate catalog surfaces
            // and a warehouse has no DEFAULT constraint at all, so there is nothing to ask for. Same rule
            // as above — never issue a statement on a warehouse whose failure you would have to swallow.
            if (Profile.SupportsDefaultConstraints)
            {
                foreach (var row in ReadMetadataRows(DefaultsSql, 4))
                {
                    if (row[0] is not { } schemaName || row[1] is not { } tableName ||
                        row[2] is not { } columnName || row[3] is not { } definition)
                    {
                        continue;
                    }
                    if (NormaliseDefault(definition) is { } normalised)
                    {
                        (EntryFor(schemaName, tableName).Defaults ??=
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))[columnName] =
                            normalised;
                    }
                }
            }
            _meta = loaded;
            return loaded;
        }
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
    internal void InvalidateMetadata() => _meta = null;

    /// <summary>The table's own comment (the MS_Description extended property), or null.</summary>
    internal string? TableCommentCore(string schemaName, string tableName) =>
        MetadataCache().TryGetValue(ExternalKey(schemaName, tableName), out var entry) ? entry.Table : null;

    /// <summary>Per-column comments for the table, or null when it has none.</summary>
    internal IReadOnlyDictionary<string, string>? ColumnCommentsCore(string schemaName, string tableName) =>
        MetadataCache().TryGetValue(ExternalKey(schemaName, tableName), out var entry) ? entry.Columns : null;

    /// <summary>Per-column DEFAULT expressions for the table, normalised out of T-SQL, or null when it has
    /// none. What the HOST does with them is much narrower — it keeps only constants.</summary>
    internal IReadOnlyDictionary<string, string>? ColumnDefaultsCore(string schemaName, string tableName) =>
        MetadataCache().TryGetValue(ExternalKey(schemaName, tableName), out var entry) ? entry.Defaults : null;

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

        public void Dispose()
        {
            // Nothing held: the stream returned by Scan owns its connection, and the transaction is the
            // routing's business.
        }
    }
}
