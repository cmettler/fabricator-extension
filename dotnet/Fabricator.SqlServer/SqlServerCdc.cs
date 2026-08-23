using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Apache.Arrow;
using Apache.Arrow.Types;
using Fabricator.Bridge;
using Microsoft.Data.SqlClient;

namespace Fabricator.SqlServer;

/// <summary>
/// The <c>db.cdc.*</c> surface over SQL Server change data capture — slice 1: the read-only inspection half
/// (<c>cdc.tables()</c>, <c>cdc.max_position()</c>, <c>cdc.min_position()</c>, <c>cdc.health()</c>). Design,
/// measurements and the remaining slices: docs/mssql-cdc.md.
/// </summary>
/// <remarks>
/// <para><b>⚠⚠ SQL SERVER ONLY.</b> Nothing here is registered when
/// <see cref="ServerProfile.SupportsCdc"/> is false — Fabric Warehouse, the Fabric Lakehouse SQL endpoint and
/// Synapse dedicated have no CDC at all. The gate is the PROFILE, never a probe: on a warehouse engine a
/// statement that errors inside an explicit transaction aborts it, so a swallowed "is CDC here?" test poisons
/// whatever the caller does next (docs/warehouse-support.md §6.5). Because the functions are not registered
/// there, we cannot issue a CDC statement on such an engine BY CONSTRUCTION rather than by a guard someone
/// could delete. What a user on those engines sees instead is <c>supports_cdc = false</c> from
/// <c>fabricator_server_info()</c>.
/// </para>
/// <para><b>⚠ Every statement here is guarded on <c>sys.databases.is_cdc_enabled</c>, and that is a
/// correctness requirement rather than politeness. MEASURED against the rig, in a database with CDC not
/// enabled:</b></para>
/// <list type="bullet">
///   <item><c>sys.fn_cdc_get_max_lsn()</c> does NOT return NULL — it raises
///     <c>208 Invalid object name 'cdc.lsn_time_mapping'</c>, naming an object the caller never mentioned.
///     That is the §2.1 misleading-error class again, and it is why <c>cdc.max_position()</c> answers NULL from
///     the guard instead of letting the function run.</item>
///   <item><c>sp_cdc_help_change_data_capture</c> and <c>sp_cdc_help_jobs</c> both raise
///     <c>22901 The database '…' is not enabled for Change Data Capture</c> — a good message, but an ERROR
///     where "no captured tables" is the honest answer, so both are guarded and answer zero rows.</item>
///   <item><c>OBJECT_ID('sys.sp_cdc_enable_db')</c> is NON-NULL even with CDC disabled, so it is NOT a usable
///     "is CDC available" test. <c>is_cdc_enabled</c> is.</item>
/// </list>
/// <para><b>Why the results are read as strings and re-typed here.</b> The SQL casts every column to
/// <c>varchar</c> and this file builds the Arrow batch itself, which is the metadata idiom this catalog
/// already uses (<c>ReadMetadataRows</c>). It costs a hex parse for the LSNs and buys a declared output schema
/// that cannot drift from whatever the type mapper does with <c>binary(10)</c> / <c>bit</c> / <c>datetime</c> —
/// and the output schema is resolved at BIND, one crossing before the rows, so a drift between the two would
/// corrupt rather than fail.</para>
/// <para><b>⚠ <c>INSERT INTO @tablevar EXEC sys.sp_cdc_help_change_data_capture</c> pins the proc's column
/// list.</b> Declared below from the MEASURED 15-column shape. A future SQL Server that adds a column breaks
/// this with a hard, self-naming error — the right direction, and the reason for using the proc rather than
/// reading <c>cdc.change_tables</c> directly: the proc applies the capture instance's <c>@role_name</c>
/// permission filtering, which is security logic not worth reimplementing.</para>
/// </remarks>
internal static class SqlServerCdcFunctions
{
    /// <summary>
    /// The namespace: <c>db.cdc.max_position()</c>. Unlike the synthetic <c>fabric</c> schema this is a REAL SQL
    /// schema once CDC is enabled — our catalog discovers it and its seven tables — and the two do not
    /// collide, because a table-function lookup and a base-table lookup hit different <c>CatalogSet</c>s.
    /// It still has to be APPENDED when absent (see <c>SchemasMetadata</c>): with CDC not yet enabled the
    /// schema does not exist, and the host silently drops a declared function whose schema it never
    /// registered — which would make the enable functions of slice 2 unreachable exactly when they are needed.
    /// </summary>
    internal const string SchemaName = "cdc";

    /// <summary>
    /// Adds the CDC functions to a catalog's function set. Called only when
    /// <see cref="ServerProfile.SupportsCdc"/> is true.
    /// </summary>
    internal static void Register(List<ICatalogScalarFunction> scalars, List<ICatalogTableFunction> tables,
                                 SqlServerCatalog catalog)
    {
        scalars.Add(new CdcPositionFunction(catalog));
        scalars.Add(new CdcMinPositionFunction(catalog));
        tables.Add(new CdcTablesFunction(catalog));
        tables.Add(new CdcHealthFunction(catalog));
        SqlServerCdcSetup.Register(tables, catalog);
    }

    // ---- shared SQL fragments -------------------------------------------------------------------------

    /// <summary>
    /// True when the CURRENT database has CDC enabled. <c>sys.databases</c> exposes a database's own row to
    /// anyone connected to it, so this needs no elevated permission.
    /// </summary>
    internal const string CdcEnabledPredicate =
        "EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND is_cdc_enabled = 1)";

    /// <summary>
    /// The MEASURED 15-column result shape of <c>sys.sp_cdc_help_change_data_capture</c>, as a table-variable
    /// declaration. A table VARIABLE rather than a temp table on purpose: it is batch-scoped and vanishes on
    /// its own, so a failed call cannot leave a <c>#temp</c> behind on a POOLED connection and make the next
    /// call fail with "there is already an object named …".
    /// </summary>
    internal const string HelpTableVar =
        "DECLARE @cdct TABLE(source_schema sysname, source_table sysname, capture_instance sysname, " +
        "object_id int, source_object_id int, start_lsn binary(10) NULL, end_lsn binary(10) NULL, " +
        "supports_net_changes bit, has_drop_pending bit, role_name sysname NULL, index_name sysname NULL, " +
        "filegroup_name sysname NULL, create_date datetime, index_column_list nvarchar(max) NULL, " +
        "captured_column_list nvarchar(max) NULL);";

    /// <summary>Fills <c>@cdct</c> — or leaves it EMPTY when CDC is not enabled, instead of raising 22901.</summary>
    internal const string FillHelpTableVar =
        "IF " + CdcEnabledPredicate + " INSERT INTO @cdct EXEC sys.sp_cdc_help_change_data_capture;";

    // ---- value conversion -----------------------------------------------------------------------------

    /// <summary>
    /// Parses SQL Server's <c>CONVERT(varchar, &lt;binary&gt;, 1)</c> rendering (<c>0x…</c>) back to bytes.
    /// Returns null for null/empty, so an absent LSN stays absent rather than becoming an empty BLOB.
    /// </summary>
    internal static byte[]? ParseHex(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        int start = text!.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        int len = text.Length - start;
        if (len <= 0 || (len & 1) != 0)
        {
            throw new FormatException($"cdc: '{text}' is not a hex-encoded binary value.");
        }
        var bytes = new byte[len / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(text.Substring(start + i * 2, 2), NumberStyles.HexNumber,
                                  CultureInfo.InvariantCulture);
        }
        return bytes;
    }

    /// <summary>
    /// Parses the ISO-8601 rendering (<c>CONVERT(…, 126)</c>) of a <c>datetime</c> into microseconds since the
    /// Unix epoch. The value carries no zone (SQL Server <c>datetime</c> never does) and CDC's own timestamps
    /// come from the server's local clock, so it is read as-is and reported as a naive instant — the same
    /// convention the rest of this provider uses for <c>datetime</c>. ⚠ <c>datetime</c> has ~3.33 ms
    /// resolution, so this is metadata and never an ordering key; the LSN is the ordering key (§1.6).
    /// </summary>
    internal static long? ParseTimestampMicros(string? text)
    {
        if (string.IsNullOrEmpty(text)
            || !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return null;
        }
        return (dt.Ticks - DateTime.UnixEpoch.Ticks) / (TimeSpan.TicksPerMillisecond / 1000);
    }
}

/// <summary>
/// <c>db.cdc.max_position()</c> — <c>sys.fn_cdc_get_max_lsn()</c>, the highest LSN the capture job has scanned, as
/// a BLOB. The upper bound of a read window, and the value a consumer stores as its cursor (§3.4).
/// </summary>
/// <remarks>
/// <para><b>⚠ NULL is a STATE, not a failure</b> (§8.3). MEASURED: it is NULL on a freshly enabled table
/// before the capture job has ever run — the DEFAULT state — and NULL again when CDC is not enabled on the
/// database at all. Neither may look like an error; <c>cdc.health()</c> is what explains which one it is.</para>
/// <para><b>⚠ It must stay VOLATILE</b> (the interface default, hence no override). A CONSISTENT
/// zero-argument scalar is folded to a literal at plan time, which for "the current log position" is a wrong
/// answer that looks like a cached one.</para>
/// <para><b>The two-step idiom, and why this is its own function.</b> Take the window END first, read a closed
/// window, then advance the cursor to the WINDOW END rather than to <c>max(_position)</c> of what came back:
/// a filtered read makes the maximum SEEN lower than the maximum READ (so the next window replays), and an
/// empty window yields NULL (so the cursor never advances and drifts toward the retention cliff of §1.9).</para>
/// </remarks>
internal sealed class CdcPositionFunction : ICatalogScalarFunction
{
    private readonly SqlServerCatalog _catalog;

    internal CdcPositionFunction(SqlServerCatalog catalog) => _catalog = catalog;

    public string SchemaName => SqlServerCdcFunctions.SchemaName;

    /// <remarks>
    /// ⚠⚠ NOT named <c>position</c>, which is what the design note proposed and which MEASURED WRONG:
    /// <c>position</c> is a DuckDB BUILT-IN scalar, and a qualified call to a nonexistent
    /// <c>&lt;cat&gt;.&lt;schema&gt;.position()</c> reports <c>Binder Error: Referenced table "&lt;cat&gt;" not
    /// found!</c> instead of "function does not exist" — pointing at the ATTACH alias rather than at the
    /// function. Isolated: <c>min_position</c> and an invented name both give the GOOD error at the same
    /// call site, and <c>&lt;cat&gt;.dbo.position()</c> gives the BAD one on a schema that DOES exist, so it
    /// is the NAME. That case is not hypothetical — it is exactly what a Fabric or Synapse user hits, since
    /// the whole surface is absent there. Renaming also fixes an asymmetry the design had:
    /// <c>min_position</c> now has the <c>max_position</c> it deserves, and the pair maps one-to-one onto
    /// <c>fn_cdc_get_min_lsn</c> / <c>fn_cdc_get_max_lsn</c>.
    /// </remarks>
    public string Name => "max_position";

    /// <summary>
    /// No arguments. The host marshals a throwaway placeholder column so the row COUNT still crosses (a
    /// zero-FIELD Arrow schema cannot be imported), which is why <see cref="Invoke"/> reads only
    /// <c>args.Length</c> and never a column.
    /// </summary>
    public Schema Parameters { get; } = new(System.Array.Empty<Field>(), metadata: null);

    public Field Result { get; } = new("position", BinaryType.Default, nullable: true);

    public IArrowArray Invoke(RecordBatch args)
    {
        byte[]? lsn = _catalog.CdcMaxLsn();
        var builder = new BinaryArray.Builder();
        for (int i = 0; i < args.Length; i++)
        {
            if (lsn is null)
            {
                builder.AppendNull();
            }
            else
            {
                builder.Append(lsn.AsSpan());
            }
        }
        return builder.Build();
    }
}

/// <summary>
/// <c>db.cdc.min_position('&lt;capture instance | schema.table&gt;')</c> — the RETENTION FLOOR:
/// <c>sys.fn_cdc_get_min_lsn(&lt;capture instance&gt;)</c>, the oldest LSN still readable. A cursor below it
/// means the pipeline has been down longer than the retention window and HAS LOST DATA (§1.9), which the
/// reader's pre-check will compare against (§2.1).
/// </summary>
/// <remarks>
/// <para><b>⚠ The underlying function takes the CAPTURE INSTANCE name, not the table's</b> (MEASURED, §1.6) —
/// and a table may have two instances (§2.2). So the argument is matched EXACTLY against both spellings at
/// once, with no heuristic and — deliberately — <b>no precedence rule</b>: exactly one matching capture
/// instance is answered for, and TWO OR MORE matches are REFUSED naming what was matched. There is no
/// "the instance name wins" tie-break, because any tie-break would silently answer for one of two instances
/// whose floors can differ.</para>
/// <para>Note a name that matches one instance under BOTH predicates (an instance deliberately named like
/// its own <c>schema.table</c>) is a single row and resolves fine — the two readings agree there, so there is
/// nothing to refuse.</para>
/// <para>VOLATILE (the default) — the floor advances every time the cleanup job runs.</para>
/// <para><b>⚠ NULL is a STATE, and the reader must not read it as "no floor"</b> — see
/// <c>SqlServerCatalog.CdcMinLsn</c>, where the measurement and its consequence for the retention pre-check
/// are recorded.</para>
/// </remarks>
internal sealed class CdcMinPositionFunction : ICatalogScalarFunction
{
    private readonly SqlServerCatalog _catalog;

    internal CdcMinPositionFunction(SqlServerCatalog catalog) => _catalog = catalog;

    public string SchemaName => SqlServerCdcFunctions.SchemaName;

    public string Name => "min_position";

    /// <summary>
    /// Positional, and deliberately NOT named: DuckDB's <c>ScalarFunction</c> has no named-parameter concept,
    /// so declaring one is a registration error rather than sugar (<c>Params.Validate</c> enforces it).
    /// </summary>
    public Schema Parameters { get; } = new(new[]
    {
        new Field("source", StringType.Default, nullable: false),
    }, metadata: null);

    public Field Result { get; } = new("min_position", BinaryType.Default, nullable: true);

    public IArrowArray Invoke(RecordBatch args)
    {
        // ⚠ Read through ArrowValueReader rather than casting to StringArray. A scalar's execute batch is
        // POST-cast, so the declared VARCHAR really should arrive as a StringArray — but `as StringArray`
        // yielding null would make an unexpected arrival type emit a column of NULLs, i.e. "no floor" for
        // every row, which is the one answer a retention pre-check must never be handed silently.
        var arg = args.ColumnCount > 0 ? args.Column(0) : null;
        var builder = new BinaryArray.Builder();
        // One resolution per DISTINCT argument in the chunk: a scalar is invoked per chunk, so
        // `SELECT cdc.min_position(name) FROM t` would otherwise be one round trip per ROW. Scoped to this
        // one Invoke — caching across calls would contradict the volatility the floor genuinely has.
        var seen = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            string? source = arg is null ? null : ArrowValueReader.ReadScalar(arg, i)?.ToString();
            if (string.IsNullOrWhiteSpace(source))
            {
                builder.AppendNull();
                continue;
            }
            if (!seen.TryGetValue(source!, out var lsn))
            {
                lsn = _catalog.CdcMinLsn(source!);
                seen[source!] = lsn;
            }
            if (lsn is null)
            {
                builder.AppendNull();
            }
            else
            {
                builder.Append(lsn.AsSpan());
            }
        }
        return builder.Build();
    }
}

/// <summary>
/// <c>SELECT * FROM db.cdc.tables()</c> — one row per CAPTURE INSTANCE (not per table: a table may have two,
/// §2.2), from <c>sys.sp_cdc_help_change_data_capture</c>. Zero rows when CDC is not enabled.
/// </summary>
/// <remarks>
/// This is how a caller discovers the <c>capture_instance</c> name that <c>min_position</c> and the reader
/// need, and <c>captured_column_list</c> is how they see which columns are captured — the column set is
/// fixed at enable time and is what the update mask's bit positions index (§1.4).
/// </remarks>
internal sealed class CdcTablesFunction : ICatalogTableFunction
{
    private readonly SqlServerCatalog _catalog;

    internal CdcTablesFunction(SqlServerCatalog catalog) => _catalog = catalog;

    public string SchemaName => SqlServerCdcFunctions.SchemaName;

    public string Name => "tables";

    public Schema Parameters { get; } = new(System.Array.Empty<Field>(), metadata: null);

    internal static Schema Columns { get; } = new(new[]
    {
        new Field("source_schema", StringType.Default, nullable: false),
        new Field("source_table", StringType.Default, nullable: false),
        new Field("capture_instance", StringType.Default, nullable: false),
        // ⚠ end_lsn is MEASURED to be NULL even when a newer capture instance has superseded this one, which
        // is why §2.2's instance boundary has to be DERIVED from the newer instance's start_lsn rather than
        // read from here. Surfaced anyway so a caller can see that for themselves.
        new Field("start_lsn", BinaryType.Default, nullable: true),
        new Field("end_lsn", BinaryType.Default, nullable: true),
        new Field("supports_net_changes", BooleanType.Default, nullable: true),
        new Field("has_drop_pending", BooleanType.Default, nullable: true),
        new Field("role_name", StringType.Default, nullable: true),
        new Field("index_name", StringType.Default, nullable: true),
        new Field("filegroup_name", StringType.Default, nullable: true),
        new Field("create_date", new TimestampType(TimeUnit.Microsecond, (string?)null), nullable: true),
        new Field("index_column_list", StringType.Default, nullable: true),
        new Field("captured_column_list", StringType.Default, nullable: true),
    }, metadata: null);

    public ITableFunctionBinding Bind(RecordBatch args) => new CdcRowsBinding(Columns, _catalog.CdcTables);
}

/// <summary>
/// <c>SELECT * FROM db.cdc.health()</c> — (property, value) rows answering the questions that a silent CDC
/// pipeline raises: is CDC enabled at all, is the agent running, how far behind is capture, how long is
/// retention.
/// </summary>
/// <remarks>
/// <para><b>Why it exists (§1.1, MEASURED).</b> <c>sp_cdc_enable_db</c> and <c>sp_cdc_enable_table</c> both
/// SUCCEED with SQL Server Agent stopped — printing only an informational notice — so "capture is enabled"
/// and "capture is happening" are independent states and the enable path reports success for both. A table
/// that looks captured and never produces a row is a real production failure mode, and this is the surface
/// that distinguishes it from "nothing has changed".</para>
/// <para><b>⚠ Unknown must stay unknown</b> (§8.4). The agent probe reads <c>sys.dm_server_services</c>, which
/// needs <c>VIEW SERVER STATE</c> — a least-privilege reader may not have it — and the DMV does not exist on
/// Azure SQL Database at all. Both are handled by asking BEFORE issuing anything (<c>HAS_PERMS_BY_NAME</c>,
/// and the engine edition from the profile), and the answer degrades to <c>unknown</c> rather than to
/// <c>Stopped</c>: reporting an agent as stopped when we merely cannot see it would send an operator to fix
/// the wrong thing.</para>
/// <para>A (property, value) shape rather than typed columns, following <c>fabricator_server_info()</c>:
/// the answers are of mixed grain (server, database, job) and mixed type, and a diagnostic that grows a row
/// is easier to extend than one that grows a column.</para>
/// </remarks>
internal sealed class CdcHealthFunction : ICatalogTableFunction
{
    private readonly SqlServerCatalog _catalog;

    internal CdcHealthFunction(SqlServerCatalog catalog) => _catalog = catalog;

    public string SchemaName => SqlServerCdcFunctions.SchemaName;

    public string Name => "health";

    public Schema Parameters { get; } = new(System.Array.Empty<Field>(), metadata: null);

    internal static Schema Columns { get; } = new(new[]
    {
        new Field("property", StringType.Default, nullable: false),
        new Field("value", StringType.Default, nullable: true),
    }, metadata: null);

    public ITableFunctionBinding Bind(RecordBatch args) => new CdcRowsBinding(Columns, _catalog.CdcHealth);
}

/// <summary>
/// The shared binding for the zero-argument CDC inspection functions: a fixed output schema and one batch
/// produced by a catalog call at execution time.
/// </summary>
/// <remarks>
/// <para>⚠ The work happens at EXECUTION, not at bind — an inspection function that ran its query during
/// binding would answer for the moment the plan was built, which for a live log position is the wrong
/// instant. (It also means a prepared statement re-reads, which is what a caller wants here.)</para>
/// <para>⚠ Two standing rules are load-bearing in this class, both recorded elsewhere in the tree at the cost
/// of real defects: <see cref="ITableFunctionBinding.Execute"/> disposes the pushed filter values in a PLAIN
/// method (an async-iterator body does not begin until the host's first <c>get_next</c>, by which time the
/// producer that owns them may already be released), and the ambient transaction id is captured HERE and
/// re-established inside the iterator, because the iterator may run on a DuckDB worker thread where the
/// <c>AsyncLocal</c> reads 0.</para>
/// </remarks>
internal sealed class CdcRowsBinding : ITableFunctionBinding
{
    private readonly Schema _schema;
    private readonly Func<RecordBatch> _produce;
    private readonly long _txnId;

    internal CdcRowsBinding(Schema schema, Func<RecordBatch> produce)
    {
        _schema = schema;
        _produce = produce;
        _txnId = AmbientTransaction.Current;
    }

    public Schema OutputSchema => _schema;

    public bool SupportsFilterPushdown => false;

    public bool SupportsProjectionPushdown => false;

    public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
    {
        scan.FilterValues?.Dispose();
        return Rows();
    }

    private async IAsyncEnumerable<RecordBatch> Rows()
    {
        await System.Threading.Tasks.Task.CompletedTask;
        if (AmbientTransaction.Current == 0 && _txnId != 0)
        {
            AmbientTransaction.Current = _txnId;
        }
        yield return _produce();
    }

    public void Dispose()
    {
    }
}
