// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Data;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Fabricator.Bridge;
using Microsoft.AnalysisServices.AdomdClient;

namespace Fabricator.AnalysisServices;

/// <summary>
/// An opened ADOMD connection to one semantic-model database. Slice 1: connects, sets the current catalog
/// (model database), and serves metadata so <c>ATTACH</c> validates — schemas = the model name(s) from the
/// <c>$SYSTEM.TMSCHEMA_MODEL</c> DMV. Table/column discovery, scans, and the DAX functions land in later
/// slices (the read/scan/function methods throw <see cref="NotSupportedException"/> for now). See
/// docs/dax-provider.md.
/// </summary>
internal sealed class DaxCatalog : IBackendCatalog
{
    private readonly string _connectionString;
    private readonly Azure.Core.TokenCredential? _credential; // Entra cred for Fabric/AAS XMLA (null = no token auth)
    private readonly AdomdConnection _conn;
    private readonly string? _catalog; // the ADOMD database (model db) this connection is bound to
    private readonly string _modelName; // the single Tabular model in this database = the DuckDB schema name

    public DaxCatalog(string connectionString, Azure.Core.TokenCredential? credential = null)
    {
        _connectionString = connectionString;
        _credential = credential;
        _conn = new AdomdConnection(_connectionString);
        ApplyAuth(_conn);
        _conn.Open();
        // Honor an explicit Initial Catalog (the model the user asked for). A workspace XMLA endpoint hosts
        // MANY models, so DBSCHEMA_CATALOGS lists several and its first row may be the wrong one — only
        // auto-discover a default when the connection carries no current catalog.
        _catalog = string.IsNullOrEmpty(_conn.Database) ? DiscoverDefaultCatalog(_conn) : _conn.Database;
        if (!string.IsNullOrEmpty(_catalog) && !string.Equals(_catalog, _conn.Database, StringComparison.Ordinal))
        {
            _conn.ChangeDatabase(_catalog!);
        }
        _modelName = DiscoverModelNames()[0];
    }

    // Sets a freshly-acquired Power BI access token on the connection before Open (Fabric/AAS). Azure.Identity
    // caches + refreshes the token internally, so calling this per connection is cheap and always yields a
    // valid token. No-op when no credential (local Power BI Desktop / an explicit connstr with its own auth).
    private void ApplyAuth(AdomdConnection conn)
    {
        if (_credential is null)
        {
            return;
        }
        conn.AccessToken = AcquireToken();
        // Refresh callback for a connection that outlives the token (~1 h for an SP): ADOMD calls this when
        // the token expires; Azure.Identity returns a cached-or-refreshed token, so it's cheap.
        conn.OnAccessTokenExpired = _ => AcquireToken();
    }

    private Microsoft.AnalysisServices.AccessToken AcquireToken()
    {
        try
        {
            var t = DaxTokenAuth.GetToken(_credential!);
            return new Microsoft.AnalysisServices.AccessToken(t.Token, t.ExpiresOn, null);
        }
        catch (Exception ex)
        {
            // Azure.Identity nests the real AADSTS reason in inner exceptions — flatten them so the cause
            // (invalid secret / app not in tenant / no consent / XMLA disabled) reaches the user.
            var detail = "";
            for (var e = ex; e != null; e = e.InnerException)
            {
                detail += (detail.Length > 0 ? " <- " : "") + e.GetType().Name + ": " + e.Message;
            }
            throw new InvalidOperationException(
                $"dax: Entra token acquisition failed for scope {DaxTokenAuth.PowerBiScope}: {detail}");
        }
    }


    // ---- metadata --------------------------------------------------------------------------------------

    // The DuckDB schema that exposes the curated VertiPaq/$SYSTEM DMVs as tables, in the same catalog.
    private const string SystemSchema = "system";

    // Curated $SYSTEM DMVs surfaced under the "system" schema. Each is queryable with a bare
    // `SELECT * FROM $SYSTEM.<name>` (the DMV SQL is very limited; DuckDB applies projection/filter above the
    // scan — no pushdown). Only DMVs that work WITHOUT a restriction WHERE belong here; extend as needed.
    private static readonly string[] SystemTables =
    {
        "TMSCHEMA_MODEL", "TMSCHEMA_TABLES", "TMSCHEMA_COLUMNS", "TMSCHEMA_MEASURES",
        "TMSCHEMA_RELATIONSHIPS", "TMSCHEMA_PARTITIONS", "TMSCHEMA_HIERARCHIES",
        // Storage/source provenance — what a table's data is actually connected to (DirectLake on OneLake
        // vs on SQL): partitions carry Mode/SourceType + an ExpressionSource, expressions hold the M source,
        // data sources hold structured-source connection details.
        "TMSCHEMA_EXPRESSIONS", "TMSCHEMA_DATA_SOURCES", "TMSCHEMA_PARTITION_SOURCES",
        "DBSCHEMA_CATALOGS", "DBSCHEMA_TABLES", "DBSCHEMA_COLUMNS",
        "DISCOVER_STORAGE_TABLES", "DISCOVER_STORAGE_TABLE_COLUMNS",
        "DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS", "DISCOVER_CALC_DEPENDENCY",
        "DISCOVER_OBJECT_MEMORY_USAGE",
    };

    private static bool IsSystem(string? schema) =>
        string.Equals(schema, SystemSchema, StringComparison.OrdinalIgnoreCase);

    // Resolves a system table name to its $SYSTEM DMV (validated against the curated list — DuckDB only scans
    // tables we declared, but guard defensively so a stray name can't reach the DMV endpoint).
    private static string SystemDmv(string table)
    {
        foreach (var t in SystemTables)
        {
            if (string.Equals(t, table, StringComparison.OrdinalIgnoreCase))
            {
                return t;
            }
        }
        throw new NotSupportedException($"dax provider: unknown system table '{table}'");
    }

    // ── catalog discovery (the five dedicated list members — ABI v72). The per-TABLE questions live on the
    // typed ITableBinding members (the definition/bound table at the end of this file), reached through the host's
    // table_* session.

    // Schemas = the model name(s) + the "system" schema (curated $SYSTEM DMVs).
    public IArrowArrayStream GetSchemas() => SingleColumn("schema_name", new[] { _modelName, SystemSchema });

    // Tables = the model's tables (TMSCHEMA_TABLES) + the curated system DMVs.
    public IArrowArrayStream GetTables() => DiscoverTables();

    // Functions (under the model schema): the three BESPOKE ones, plus whatever the catalog's
    // CatalogFunctionSet holds (the XMLA/TMSL refresh functions). The bespoke three keep their hand-written
    // declarations because their kinds — 'proc', 'collector', 'inout' — are dispatched by name below rather
    // than through the set; everything registered in the set declares itself.
    public IArrowArrayStream GetFunctions() => FunctionsMetadata.Stream(BespokeDeclarations());

    // No catalog-bound macros and no capability profile on this provider — empty streams, declared shapes.
    public IArrowArrayStream GetMacros() => EmptyStringTable("schema", "name", "create_sql");
    public IArrowArrayStream GetViews() => EmptyStringTable("schema", "name", "create_sql");

    public IArrowArrayStream GetServerInfo() => EmptyStringTable("property", "value");

    /// <summary>The DAX <see cref="ITable"/> — read-only, transaction-free (the default
    /// <c>ResolveTransaction</c> answers null, so every bind is transient and caller-owned).</summary>
    public ITable GetTable(string schemaName, string tableName) =>
        new DaxTableDefinition(this, schemaName, tableName);

    private sealed class DaxTableDefinition : ITable
    {
        private readonly DaxCatalog _catalog;

        internal DaxTableDefinition(DaxCatalog catalog, string schemaName, string tableName)
        {
            _catalog = catalog;
            SchemaName = schemaName;
            TableName = tableName;
        }

        public string SchemaName { get; }
        public string TableName { get; }

        // The AT clause is carried per the interface but has no DAX meaning; the scan spec's own AT
        // handling (rejected provider-side) is what surfaces the refusal.
        public ITableBinding Bind(ITransaction? transaction, TableAt? at = null) => new DaxTableBinding(_catalog, this);
    }

    /// <summary>The THIN DAX bound table: no rowid (read-only provider), no virtual columns, no stats —
    /// schema + scan delegate to the catalog's existing cores.</summary>
    private sealed class DaxTableBinding : ITableBinding
    {
        private readonly DaxCatalog _catalog;
        private readonly DaxTableDefinition _definition;

        internal DaxTableBinding(DaxCatalog catalog, DaxTableDefinition definition)
        {
            _catalog = catalog;
            _definition = definition;
        }

        public Schema Schema => _catalog.ColumnsSchemaCore(_definition.SchemaName, _definition.TableName);

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

    // The three bespoke function declarations ++ the catalog function set's. daxeval is a 'proc' (not 'table')
    // so its args register as NAMED parameters — it takes an optional `params` arg alongside `expression`, which
    // a positional table fn cannot express; daxevaltable is a 'collector' (whole-table); daxeach is a streaming
    // 'inout' (per-row).
    private IEnumerable<FunctionsMetadata.Declaration> BespokeDeclarations()
    {
        yield return new FunctionsMetadata.Declaration(_modelName, DaxEvalName, "proc", 2);
        yield return new FunctionsMetadata.Declaration(_modelName, DaxEvalTableName, "collector", 1);
        yield return new FunctionsMetadata.Declaration(_modelName, DaxEachName, "inout", 1);
        // The model schema is already known here, so nothing needs the __all__ sentinel — these declare
        // themselves into the model's own schema, not into `system` (a DMV namespace).
        foreach (var d in Functions.Declarations(() => new[] { _modelName }))
        {
            yield return d;
        }
    }

    private IArrowArrayStream DiscoverTables()
    {
        var schemaCol = new List<string>();
        var nameCol = new List<string>();
        var typeCol = new List<string>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT [Name] FROM $SYSTEM.TMSCHEMA_TABLES";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var name = r.IsDBNull(0) ? null : r.GetValue(0)?.ToString();
                if (string.IsNullOrEmpty(name) || IsSystemDateTable(name!))
                {
                    continue; // skip Power BI's auto-generated date tables (noise)
                }
                schemaCol.Add(_modelName);
                nameCol.Add(name!);
                typeCol.Add("BASE TABLE");
            }
        }
        // Curated $SYSTEM DMVs under the "system" schema (same catalog).
        foreach (var sysTable in SystemTables)
        {
            schemaCol.Add(SystemSchema);
            nameCol.Add(sysTable);
            typeCol.Add("SYSTEM TABLE");
        }
        return ThreeColumn("schema_name", schemaCol, "table_name", nameCol, "table_type", typeCol);
    }

    private static bool IsSystemDateTable(string name)
        => name.StartsWith("LocalDateTable_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("DateTableTemplate_", StringComparison.OrdinalIgnoreCase);

    private Schema ColumnsSchemaCore(string? schema, string table)
    {
        // Model: EVALUATE TOPN(0,'T') returns the data columns (no internal RowNumber) with engine types.
        // System: a bare SELECT * FROM $SYSTEM.<dmv> — GetSchemaTable reads NO rows, so it's cheap even
        // without a TOPN-style cap (the DMV SQL has no TOPN/EVALUATE). The schema table gives name + CLR type.
        string cmdText = IsSystem(schema)
            ? $"SELECT * FROM $SYSTEM.{SystemDmv(table)}"
            : $"EVALUATE TOPN(0, {QuoteDaxTable(table)})";
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = cmdText;
        using var r = cmd.ExecuteReader();
        return ArrowSchemaFromReader(r);
    }

    /// <summary>Builds the Arrow schema for a DAX result set from the reader's schema table — de-bracketed
    /// column names + <see cref="DaxTypeMap"/> types. Shared by column discovery and table scans so a scan's
    /// column names/types match what was discovered.</summary>
    internal static Schema ArrowSchemaFromReader(IDataReader reader)
    {
        var fields = new List<Field>();
        var st = reader.GetSchemaTable();
        bool hasPrecision = st!.Columns.Contains("NumericPrecision");
        bool hasScale = st.Columns.Contains("NumericScale");
        foreach (System.Data.DataRow row in st.Rows)
        {
            var rawName = (string)row["ColumnName"];
            var clr = (Type)row["DataType"];
            bool nullable = row["AllowDBNull"] is not bool b || b;
            int? precision = hasPrecision && row["NumericPrecision"] is int p ? p : null;
            int? scale = hasScale && row["NumericScale"] is int s ? s : null;
            fields.Add(new Field(DaxTypeMap.DebracketColumn(rawName), DaxTypeMap.MapClr(clr, precision, scale), nullable));
        }
        return new Schema(fields, null);
    }

    /// <summary>Quotes a table name for DAX (single quotes; embedded quotes doubled).</summary>
    internal static string QuoteDaxTable(string table) => "'" + table.Replace("'", "''") + "'";

    private List<string> DiscoverModelNames()
    {
        var names = new List<string>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT [Name] FROM $SYSTEM.TMSCHEMA_MODEL";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var name = r.IsDBNull(0) ? null : r.GetValue(0)?.ToString();
            names.Add(string.IsNullOrEmpty(name) ? "Model" : name!);
        }
        if (names.Count == 0)
        {
            names.Add("Model"); // a model with no TMSCHEMA_MODEL row still gets a usable schema name
        }
        return names;
    }

    private static string? DiscoverDefaultCatalog(AdomdConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT [CATALOG_NAME] FROM $SYSTEM.DBSCHEMA_CATALOGS";
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.IsDBNull(0) ? null : r.GetValue(0)?.ToString()) : null;
    }

    // ---- read / scan (later slices) -------------------------------------------------------------------

    public IArrowArrayStream ExecuteQuery(string sql)
        => throw new NotSupportedException("dax provider: raw query not supported yet (slice 1).");

    /// <summary>
    /// Scans a model table: projects the requested columns via <c>EVALUATE SELECTCOLUMNS('T', "Col",
    /// 'T'[Col], …)</c> (no projection => <c>EVALUATE 'T'</c>), runs it on a fresh connection, and streams
    /// the <see cref="AdomdDataReader"/> as Arrow. Filter pushdown is not done — DAX has no general SQL WHERE
    /// here — so any pushed filter is ignored and DuckDB re-applies it (never-erase: a superset is safe).
    /// </summary>
    public IArrowArrayStream ScanTable(string schemaName, string tableName, string? specJson,
                                       IArrowArrayStream? filterValues)
    {
        // $SYSTEM DMVs: a bare SELECT (the DMV SQL is very limited; no pushdown — DuckDB projects/filters
        // above the scan). Ignore + dispose any pushed filter values.
        if (IsSystem(schemaName))
        {
            filterValues?.Dispose();
            return ScanTableCore($"SELECT * FROM $SYSTEM.{SystemDmv(tableName)}");
        }

        var spec = ScanSpec.Parse(specJson);
        string tableRef = QuoteDaxTable(tableName);

        // Filter pushdown (best-effort, superset-safe — DuckDB re-applies every predicate). Wrap the table in
        // FILTER('T', <pred>); VertiPaq pushes the storage-engine-friendly parts down and iterates the rest.
        // If nothing safe is pushable (or anything fails), scan unfiltered and let DuckDB filter.
        string tableExpr = tableRef;
        if (spec?.Filter is not null && filterValues is not null)
        {
            try
            {
                var values = ReadFilterValues(filterValues);
                filterValues = null; // consumed + disposed by ReadFilterValues
                var pred = new DaxFilterBuilder(values, tableRef).Render(spec.Filter);
                if (!string.IsNullOrEmpty(pred))
                {
                    tableExpr = $"FILTER({tableRef}, {pred})";
                }
            }
            catch
            {
                // Fall through to an unfiltered scan; correctness preserved by DuckDB.
            }
        }
        filterValues?.Dispose(); // dispose if not consumed above (host hands us ownership)

        // Projection (absent/empty => whole table). Column refs stay qualified by the base table so they
        // resolve in FILTER's row context.
        string innerExpr = spec?.Columns is { Count: > 0 } cols
            ? $"SELECTCOLUMNS({tableExpr}, " +
              string.Join(", ", cols.Select(c =>
                  $"\"{c.Replace("\"", "\"\"")}\", {tableRef}[{c}]")) + ")"
            : tableExpr;
        return ScanTableCore($"EVALUATE {innerExpr}");
    }

    // Reads the one-row filter value batch (column i == value i) into CLR values; disposes the stream.
    private static List<object?> ReadFilterValues(IArrowArrayStream stream)
    {
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

    /// <summary>
    /// Lazy, true-streaming scan (one batch per host pull, ≤1 batch buffered) — open one connection, one
    /// ExecuteReader, hand the open <see cref="AdomdDataReader"/> to <see cref="DaxArrowStream"/>. The schema
    /// comes from the data reader itself (one query per connection). Streams arbitrarily large results
    /// (validated to 10.5M rows). See <see cref="DaxArrowStream"/> for the one subtlety that makes it work:
    /// never call <c>Read()</c> past end-of-data (ADOMD throws on it).
    /// </summary>
    private IArrowArrayStream ScanTableCore(string commandText) => StreamCommand(commandText, null);

    /// <summary>
    /// The Analysis Services DATABASE this catalog is bound to — what a TMSL command's <c>database</c> member
    /// names. Note this is not always the model name: a workspace XMLA endpoint hosts many databases and the
    /// model inside one can be named differently, so TMSL must use the database.
    /// </summary>
    internal string DatabaseName => string.IsNullOrEmpty(_catalog) ? _modelName : _catalog!;

    /// <summary>
    /// Runs a TMSL command (a JSON script) on a fresh connection. SYNCHRONOUS by nature — the XMLA endpoint does
    /// not return until the operation completes, which is why the refresh functions need no polling.
    /// </summary>
    /// <remarks>
    /// Cancellable through the same tier-3 mechanism as a scan: ADOMD has no async API, so a registration on the
    /// calling operator's interrupt token trips <c>AdomdCommand.Cancel()</c> from a pool thread. That matters far
    /// more here than for a scan — a full refresh can run for many minutes, and without this Ctrl+C would leave
    /// the statement blocked until the engine finished.
    /// </remarks>
    internal void ExecuteTmsl(string tmsl, CancellationToken ct)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = tmsl;
        using var interrupt = new InterruptScope(AmbientOpener.Current);
        using var reg = interrupt.Token.Register(static state =>
        {
            try { ((System.Data.IDbCommand)state!).Cancel(); } catch { /* cancellation must never fault */ }
        }, cmd);
        using var ctReg = ct.Register(static state =>
        {
            try { ((System.Data.IDbCommand)state!).Cancel(); } catch { }
        }, cmd);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Opens a fresh ADOMD connection bound to this catalog's model database.</summary>
    internal AdomdConnection OpenConnection()
    {
        var conn = new AdomdConnection(_connectionString);
        ApplyAuth(conn);
        conn.Open();
        if (_catalog != null)
        {
            conn.ChangeDatabase(_catalog);
        }
        return conn;
    }

    /// <summary>Runs <paramref name="commandText"/> (DAX or a $SYSTEM DMV SELECT) on a fresh connection and
    /// returns its rows as a lazy Arrow stream (the stream owns conn/cmd/reader). The output schema is taken
    /// from <paramref name="knownSchema"/> when given (e.g. resolved at bind), else from the data reader.</summary>
    internal IArrowArrayStream StreamCommand(string commandText, Schema? knownSchema,
                                             IReadOnlyList<KeyValuePair<string, object?>>? daxParams = null)
    {
        var conn = OpenConnection();
        InterruptScope? interrupt = null;
        CancellationTokenRegistration interruptReg = default;
        try
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = commandText;
            BindDaxParams(cmd, daxParams);
            // Tier 3 cancellation: ADOMD has no async, so a poller on the calling operator's interrupt flag
            // trips AdomdCommand.Cancel() from a pool thread — covering BOTH the initial server-side DAX
            // evaluation (ExecuteReader below can run long) and mid-stream Read()s. Ownership transfers to
            // the DaxArrowStream (disposed with it). See docs/cancellation.md.
            interrupt = new InterruptScope(AmbientOpener.Current);
            interruptReg = interrupt.Token.Register(static state =>
            {
                try { ((System.Data.IDbCommand)state!).Cancel(); } catch { /* cancellation must never fault */ }
            }, cmd);
            var reader = cmd.ExecuteReader();
            var schema = knownSchema ?? ArrowSchemaFromReader(reader);
            return new DaxArrowStream(conn, cmd, reader, schema, batchSize: 2048, interrupt, interruptReg);
        }
        catch
        {
            interruptReg.Dispose();
            interrupt?.Dispose();
            conn.Dispose();
            throw;
        }
    }

    /// <summary>Resolves a command's output schema without fetching rows (ExecuteReader + GetSchemaTable) —
    /// the no-describe approach used to bind a daxeval call's output columns.</summary>
    internal Schema ProbeSchema(string commandText,
                                IReadOnlyList<KeyValuePair<string, object?>>? daxParams = null)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = commandText;
        BindDaxParams(cmd, daxParams);
        // The bind-time probe EXECUTES the query (ADOMD has no describe) — a heavy daxeval binds slowly, so
        // it is cancellable like the scan (Tier 3).
        using var interrupt = new InterruptScope(AmbientOpener.Current);
        using var reg = interrupt.Token.Register(static state =>
        {
            try { ((System.Data.IDbCommand)state!).Cancel(); } catch { }
        }, cmd);
        using var reader = cmd.ExecuteReader();
        return ArrowSchemaFromReader(reader);
    }

    // ---- functions -----------------------------------------------------------------------------------
    // daxeval(expression) — a table function that evaluates an arbitrary DAX query (a complete EVALUATE /
    // DEFINE…EVALUATE statement) against the model and returns its result table. ADOMD has no result-set
    // describe, so the output schema is resolved at bind by executing the query + reading GetSchemaTable (no
    // rows fetched); the scan re-executes + streams. Registered under the model schema.

    private const string DaxEvalName = "daxeval";
    private const string DaxEvalTableName = "daxevaltable";
    private const string DaxEachName = "daxeach";

    private static bool IsDaxEval(string functionName) =>
        string.Equals(functionName, DaxEvalName, StringComparison.OrdinalIgnoreCase);

    private static bool IsDaxEvalTable(string functionName) =>
        string.Equals(functionName, DaxEvalTableName, StringComparison.OrdinalIgnoreCase);

    private static bool IsDaxEach(string functionName) =>
        string.Equals(functionName, DaxEachName, StringComparison.OrdinalIgnoreCase);

    // Reads the named arg <paramref name="name"/> (case-insensitive) from the 1-row args batch, or null if
    // absent. Args arrive as a batch whose columns are the SUPPLIED named parameters (order not guaranteed),
    // so read by field name rather than position.
    private static object? ReadArgByName(RecordBatch? args, string name)
    {
        if (args is null)
        {
            return null;
        }
        var fields = args.Schema.FieldsList;
        for (int i = 0; i < fields.Count; i++)
        {
            if (string.Equals(fields[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return ArrowValueReader.ReadScalar(args.Column(i), 0);
            }
        }
        return null;
    }

    private static string DaxEvalExpression(RecordBatch? args)
    {
        var expr = ReadArgByName(args, "expression")?.ToString();
        if (string.IsNullOrWhiteSpace(expr))
        {
            throw new ArgumentException(
                "dax provider: a non-empty 'expression' is required, e.g. daxeval(expression := 'EVALUATE …')");
        }
        return expr!;
    }

    // daxeval's optional `params` arg = a bag of DAX parameter values; each becomes an ADOMD parameter the
    // expression references as @<name>. Two accepted shapes (the param is declared ANY — see the NullType
    // sentinel above): a DuckDB STRUCT (params := {'p': 5, 'q': 'x'}) read field-by-field, OR a JSON string
    // (params := '{"p": 5}') parsed below. STRUCT is type-safe + needs no quoting; JSON suits programmatic callers.
    private static IReadOnlyList<KeyValuePair<string, object?>> DaxParams(RecordBatch? args)
    {
        if (args is null)
        {
            return System.Array.Empty<KeyValuePair<string, object?>>();
        }
        var fields = args.Schema.FieldsList;
        for (int i = 0; i < fields.Count; i++)
        {
            if (!string.Equals(fields[i].Name, "params", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (args.Column(i) is StructArray sa)
            {
                return ReadStructParams(sa);
            }
            // scalar: a JSON string, or a NULL literal (-> empty).
            return ParseDaxParams(ArrowValueReader.ReadScalar(args.Column(i), 0)?.ToString());
        }
        return System.Array.Empty<KeyValuePair<string, object?>>();
    }

    private static IReadOnlyList<KeyValuePair<string, object?>> ReadStructParams(StructArray sa)
    {
        var result = new List<KeyValuePair<string, object?>>();
        if (sa.Length == 0 || sa.IsNull(0))
        {
            return result;
        }
        var st = (StructType)sa.Data.DataType;
        for (int f = 0; f < st.Fields.Count; f++)
        {
            // row 0 of each child = that field's value (the args batch is always 1 row).
            result.Add(new KeyValuePair<string, object?>(st.Fields[f].Name, ArrowValueReader.ReadScalar(sa.Fields[f], 0)));
        }
        return result;
    }

    private static IReadOnlyList<KeyValuePair<string, object?>> ParseDaxParams(string? json)
    {
        var result = new List<KeyValuePair<string, object?>>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            throw new ArgumentException("dax provider: daxeval 'params' must be a JSON object, e.g. '{\"p\": 5}'");
        }
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            result.Add(new KeyValuePair<string, object?>(p.Name, JsonScalar(p.Value)));
        }
        return result;
    }

    private static object? JsonScalar(System.Text.Json.JsonElement e) => e.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        System.Text.Json.JsonValueKind.String => e.GetString(),
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        System.Text.Json.JsonValueKind.Null => null,
        _ => e.GetRawText(), // array/object — pass the raw JSON text (unusual for a DAX scalar param)
    };

    private static void BindDaxParams(AdomdCommand cmd, IReadOnlyList<KeyValuePair<string, object?>>? daxParams)
    {
        if (daxParams is null)
        {
            return;
        }
        foreach (var kv in daxParams)
        {
            cmd.Parameters.Add(new AdomdParameter(kv.Key, kv.Value ?? DBNull.Value));
        }
    }

    // ---- catalog-bound custom functions ---------------------------------------------------------------

    /// <summary>
    /// This catalog's C#-authored catalog-bound functions — currently the XMLA/TMSL refresh set. Built lazily
    /// and per CATALOG (not static) because each function captures this catalog: the ADOMD connection and the
    /// database it is bound to are what let <c>dax_refresh()</c> take no arguments at all.
    /// </summary>
    private CatalogFunctionSet Functions => _functions ??= new CatalogFunctionSet(tables: BuildTableFunctions());
    private CatalogFunctionSet? _functions;

    private List<ICatalogTableFunction> BuildTableFunctions()
    {
        var tables = new List<ICatalogTableFunction>();
        // Declared in the MODEL schema explicitly rather than via the __all__ sentinel: the model name is
        // already known here, and a refresh function has no business appearing in the `system` DMV namespace.
        DaxRefreshFunctions.Register(tables, this, _modelName);
        return tables;
    }

    public Schema GetFunctionParamSchema(string schemaName, string functionName)
    {
        // daxeval takes `expression` (required) + an optional `params` JSON arg — both NAMED parameters
        // (daxeval registers as a 'proc'). daxevaltable / daxeach take only `expression` (a named param
        // that coexists with their {TABLE} input): daxevaltable(<input>, expression := '…').
        if (IsDaxEval(functionName))
        {
            // `params` is declared with the NullType sentinel = "accept any value" (the host registers it
            // as DuckDB ANY), so a caller may pass EITHER a STRUCT bag (params := {'a': 40, 'b': 2}) OR a
            // JSON string (params := '{"a": 40}'); both are read below (DaxParams). A fixed VARCHAR here
            // would force DuckDB to stringify a struct before we ever saw it.
            return new Schema(
                new[]
                {
                    new Field("expression", StringType.Default, nullable: false),
                    new Field("params", NullType.Default, nullable: true),
                }, null);
        }
        if (IsDaxEvalTable(functionName) || IsDaxEach(functionName))
        {
            return new Schema(new[] { new Field("expression", StringType.Default, nullable: false) }, null);
        }
        return Functions.ParamSchema(schemaName, functionName)
               ?? throw NoFunction(schemaName, functionName);
    }

    public Schema GetFunctionOutputSchema(string schemaName, string functionName, RecordBatch? args = null)
    {
        if (IsDaxEval(functionName))
        {
            // arg-dependent: the DAX result's columns (resolved with any params bound).
            return ProbeSchema(DaxEvalExpression(args), DaxParams(args));
        }
        return Functions.OutputSchema(schemaName, functionName, args)
               ?? throw NoFunction(schemaName, functionName);
    }

    public IBoundTableFunction TableFnBind(string schemaName, string functionName, RecordBatch? args)
    {
        if (IsDaxEval(functionName))
        {
            return new DaxEvalBoundTableFunction(this, DaxEvalExpression(args), DaxParams(args));
        }
        return Functions.TableFnBind(schemaName, functionName, args)
               ?? throw NoFunction(schemaName, functionName);
    }

    public Schema GetFunctionReturnSchema(string schemaName, string functionName)
        => Functions.ReturnSchema(schemaName, functionName) ?? throw NoScalar();

    public ScalarBindingHandle ScalarFnBind(string schemaName, string functionName, ScalarBindArgs args)
        => Functions.BindScalar(schemaName, functionName, args) ?? throw NoScalar();

    private static NotSupportedException NoScalar() =>
        new("dax provider: no scalar functions.");

    // Names the function, because a miss here means the host registered a declaration this catalog cannot
    // serve — the declaration list and the registry disagree, which is a bug in OUR wiring, not user error.
    private static NotSupportedException NoFunction(string schema, string func) =>
        new($"dax provider: no catalog function '{schema}.{func}'.");

    public IInOutBinding InOutBind(string schemaName, string functionName, RecordBatch? args, Schema inputSchema)
    {
        // args carries the named "expression" param (1-row, column 0); inputSchema = the input table.
        if (IsDaxEvalTable(functionName))
        {
            // daxevaltable is a COLLECTOR (registered kind='collector'): the C++ Sink+Source operator buffers
            // ALL input then calls inout_exchange_open once. Wrap the collector binding as an IInOutBinding
            // (CollectorInOutBinding adapter) so it flows through the shared exchange marshaling. No single-chunk
            // cap — DaxEvalTableBinding now reads the whole input into one DATATABLE.
            return new CollectorInOutBinding(new DaxEvalTableBinding(this, DaxEvalExpression(args), inputSchema));
        }
        if (IsDaxEach(functionName))
        {
            return new DaxEachBinding(this, DaxEvalExpression(args), inputSchema);
        }
        return Functions.InOutBind(schemaName, functionName, args, inputSchema)
               ?? throw NoFunction(schemaName, functionName);
    }

    /// <summary>A bound <c>daxeval(expression)</c> call: the output schema is resolved once (at bind, via a
    /// row-less GetSchemaTable probe), and each <see cref="Execute"/> re-runs the DAX and streams the result.
    /// No pushdown (an arbitrary DAX query can't be wrapped) — DuckDB projects/filters above the scan.</summary>
    private sealed class DaxEvalBoundTableFunction : IBoundTableFunction
    {
        private readonly DaxCatalog _catalog;
        private readonly string _dax;
        private readonly IReadOnlyList<KeyValuePair<string, object?>> _params;

        public DaxEvalBoundTableFunction(DaxCatalog catalog, string dax,
                                 IReadOnlyList<KeyValuePair<string, object?>> daxParams)
        {
            _catalog = catalog;
            _dax = dax;
            _params = daxParams;
            OutputSchema = catalog.ProbeSchema(dax, daxParams);
        }

        public Schema OutputSchema { get; }
        // IBoundTableFunction, not a binding: this is the host's projection MAPPING. False = map positionally, which
        // is right here — daxeval returns whatever columns the DAX query produced, so there is no declared
        // schema to match names against.
        public bool MapResultByName => false;

        public IArrowArrayStream Execute(string? specJson, IArrowArrayStream? filterValues)
        {
            filterValues?.Dispose(); // no pushdown — DuckDB re-applies; just release the (usually null) batch
            return _catalog.StreamCommand(_dax, OutputSchema, _params);
        }

        public void Dispose() { }
    }

    public IAggregateSession AggOpen(string schemaName, string functionName)
        => Functions.AggOpen(schemaName, functionName)
           ?? throw new NotSupportedException("dax provider: no aggregate functions.");

    // ---- write paths: read-only provider --------------------------------------------------------------

    public long ExecuteNonQuery(string sql) => throw ReadOnly();
    public long BulkInsert(string schemaName, string tableName, IArrowArrayStream data, bool createTable,
                           bool replace, bool checkConstraints, long txnId, IReadOnlyList<string>? partitionColumns,
                           IReadOnlyList<string>? sortColumns, string? schemaMode, bool partitionOverwrite,
                           string? optionsJson) => throw ReadOnly();
    public long ExecuteDelete(string schemaName, string tableName, IArrowArrayStream keys) => throw ReadOnly();
    public long ExecuteUpdate(string schemaName, string tableName, int setColumnCount, IArrowArrayStream data) => throw ReadOnly();
    public IArrowArrayStream InsertReturning(string schemaName, string tableName, IArrowArrayStream rows) => throw ReadOnly();
    public void CreateTable(string schemaName, string tableName, Schema columns, bool ifNotExists,
                            string? primaryKey, string? uniques, string? defaults, IReadOnlyList<string>? partitionColumns,
                            IReadOnlyList<string>? sortColumns, IReadOnlyList<string>? identityColumns,
                            string? optionsJson) => throw ReadOnly();
    public void DropTable(string schemaName, string tableName, bool ifExists) => throw ReadOnly();
    public void CreateSchema(string schemaName, bool ifNotExists) => throw ReadOnly();
    public void DropSchema(string schemaName, bool ifExists) => throw ReadOnly();
    public void AlterTable(AlterTableSpec spec, string schemaName, string tableName, Field? column) => throw ReadOnly();

    // Transactions: ADOMD/DAX is read-only — accept BEGIN/COMMIT/ROLLBACK as no-ops so a wrapping
    // DuckDB transaction over read-only DAX queries doesn't fail.
    public void BeginTransaction(bool isExplicit) { }
    public void CommitTransaction() { }
    public void RollbackTransaction() { }

    public void Dispose()
    {
        try { _conn.Dispose(); } catch { /* best-effort close */ }
    }

    private static NotSupportedException ReadOnly()
        => new("dax provider is read-only: semantic models cannot be modified through DAX.");

    // ---- Arrow helpers ---------------------------------------------------------------------------------

    private static IArrowArrayStream SingleColumn(string name, IReadOnlyList<string> values)
    {
        var schema = new Schema(new[] { new Field(name, StringType.Default, nullable: true) }, null);
        var builder = new StringArray.Builder();
        foreach (var v in values)
        {
            builder.Append(v);
        }
        var batch = new RecordBatch(schema, new IArrowArray[] { builder.Build() }, values.Count);
        return new InMemoryArrayStream(schema, new[] { batch });
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
        IArrowArray Build(IReadOnlyList<string> vals)
        {
            var b = new StringArray.Builder();
            foreach (var v in vals) b.Append(v);
            return b.Build();
        }
        var batch = new RecordBatch(schema, new[] { Build(c0), Build(c1), Build(c2) }, c0.Count);
        return new InMemoryArrayStream(schema, new[] { batch });
    }

    private static IArrowArrayStream EmptyStringTable(params string[] columns)
    {
        var builder = new Schema.Builder();
        foreach (var c in columns)
        {
            builder.Field(new Field(c, StringType.Default, nullable: true));
        }
        return new InMemoryArrayStream(builder.Build(), System.Array.Empty<RecordBatch>());
    }
}
