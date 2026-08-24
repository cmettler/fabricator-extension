using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Apache.Arrow;
using Apache.Arrow.Types;
using Fabricator.Bridge;
using Microsoft.Data.SqlClient;

namespace Fabricator.SqlServer;

/// <summary>
/// The <c>db.cdc.*</c> SETUP functions — slice 2 of docs/mssql-cdc.md: <c>enable_database()</c>,
/// <c>enable(...)</c>, <c>disable(...)</c> and <c>capture_now()</c>. Each does its work at EXECUTION and returns one
/// report row.
/// </summary>
/// <remarks>
/// <para><b>⚠⚠ EVERY ONE OF THESE PERFORMS DDL, AND THAT IS WHY ABI v81 EXISTS.</b> Enabling capture creates a
/// change table and two table-valued functions; disabling drops them. MEASURED: without a cache rebuild those
/// objects are not merely un-enumerated, they are <i>unreachable</i> for the rest of the session — reading the
/// change table `cdc.enable` just created gives <c>Catalog Error: Table with name dbo_x_CT does not exist!</c>,
/// because with no ATTACH object filter a name missing from the discovered list is treated as genuinely absent.
/// So each of these reports <see cref="ITableFunctionBinding.SchemaMayChange"/>, and the host rebuilds at the
/// next transaction start. A user should not have to know that enabling capture is a DDL.</para>
/// <para><b>⚠⚠ THE WORK HAPPENS IN <c>Execute()</c>, NOT IN AN ITERATOR, AND THAT IS LOAD-BEARING RATHER THAN
/// STYLE.</b> The host reads the flag the moment <c>tablefn_execute</c> returns — before a single row is
/// pulled. An async-iterator body does not begin until the first batch pull, a different ABI crossing, so a
/// side effect placed there would run AFTER the host had already read the flag as false: the DDL would happen
/// and the cache would never be rebuilt. Same shape as the ambient-capture bug recorded for
/// <c>fabricator_install_plugin</c>, and it fails just as silently.</para>
/// <para><b>What is deliberately NOT here.</b> There is no <c>disable_database()</c>: <c>sp_cdc_disable_db</c>
/// drops EVERY capture instance in the database at once, which is a bigger hammer than anything else on this
/// surface and destroys history no other statement here can. An operator who means it has
/// <c>fabricator_exec</c>; making it one word away from <c>cdc.disable('t')</c> would invite the wrong one.
/// Likewise no automatic second-instance creation on schema change — elevated privileges, and an operator
/// decision (docs §9).</para>
/// </remarks>
internal static class SqlServerCdcSetup
{
    internal static void Register(List<ICatalogTableFunction> tables, SqlServerCatalog catalog)
    {
        tables.Add(new CdcEnableDatabaseFunction(catalog));
        tables.Add(new CdcEnableFunction(catalog));
        tables.Add(new CdcDisableFunction(catalog));
        tables.Add(new CdcCaptureNowFunction(catalog));
    }

    /// <summary>
    /// The shared report shape. One row, three columns: what was asked for, whether the server changed
    /// anything, and a sentence. <c>changed</c> is separate from success on purpose — an idempotent call that
    /// found the work already done SUCCEEDS and reports <c>changed = false</c>, and collapsing the two would
    /// make the ordinary "already enabled" outcome look like a failure. (The same distinction the plugin
    /// uninstaller draws between <c>removed</c> and <c>purged</c>.)
    /// </summary>
    internal static Schema ReportColumns { get; } = new(new[]
    {
        new Field("target", StringType.Default, nullable: true),
        new Field("changed", BooleanType.Default, nullable: false),
        new Field("detail", StringType.Default, nullable: true),
    }, metadata: null);

    internal static RecordBatch Report(string? target, bool changed, string detail)
    {
        var t = new StringArray.Builder();
        var c = new BooleanArray.Builder();
        var d = new StringArray.Builder();
        t.Append(target);
        c.Append(changed);
        d.Append(detail);
        return new RecordBatch(ReportColumns, new IArrowArray[] { t.Build(), c.Build(), d.Build() }, 1);
    }

    /// <summary>
    /// Splits <c>schema.table</c> (or a bare <c>table</c>, which means <c>dbo</c>). Refuses anything else
    /// rather than guessing — a three-part name here would silently address the wrong database, since these
    /// statements run against the ATTACHed connection's database and cannot cross to another.
    /// </summary>
    internal static (string Schema, string Table) SplitSource(string source, string fn)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException($"{fn}: a table name is required, e.g. {fn}('dbo.orders').");
        }
        var parts = source.Split('.');
        if (parts.Length == 1)
        {
            return ("dbo", Unquote(parts[0]));
        }
        if (parts.Length == 2)
        {
            return (Unquote(parts[0]), Unquote(parts[1]));
        }
        throw new ArgumentException(
            $"{fn}: '{source}' is not a <schema>.<table> name. A three-part name is refused rather than "
            + "guessed — change data capture is enabled per DATABASE, and these statements run against the "
            + "one this catalog is attached to.");
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && s[0] == '[' && s[^1] == ']' ? s.Substring(1, s.Length - 2) : s;
    }

    /// <summary>
    /// A T-SQL string literal. These values reach <c>sp_cdc_enable_table</c> as PARAMETERS wherever possible;
    /// this exists for the places a parameter cannot go.
    /// </summary>
    internal static string Lit(string? value) =>
        value is null ? "NULL" : "N'" + value.Replace("'", "''") + "'";
}

/// <summary>
/// <c>db.cdc.enable_database()</c> — <c>sys.sp_cdc_enable_db</c>. Idempotent: a database already enabled
/// reports <c>changed = false</c> rather than raising.
/// </summary>
/// <remarks>
/// ⚠ It SUCCEEDS with SQL Server Agent stopped, printing only an informational notice — so "enabled" and
/// "capturing" are independent states and this call cannot tell you about the second. That is what
/// <c>cdc.health()</c> is for, and the detail line says so rather than implying capture has started.
/// </remarks>
internal sealed class CdcEnableDatabaseFunction : ICatalogTableFunction
{
    private readonly SqlServerCatalog _catalog;

    internal CdcEnableDatabaseFunction(SqlServerCatalog catalog) => _catalog = catalog;

    public string SchemaName => SqlServerCdcFunctions.SchemaName;

    public string Name => "enable_database";

    public Schema Parameters { get; } = new(System.Array.Empty<Field>(), metadata: null);

    public ITableFunctionBinding Bind(RecordBatch args) =>
        new CdcSetupBinding(() => _catalog.CdcEnableDatabase());
}

/// <summary>
/// <c>db.cdc.enable('dbo.orders' [, capture_instance :=] [, columns :=] [, role :=] [, index :=]
/// [, filegroup :=] [, net :=])</c> — <c>sys.sp_cdc_enable_table</c>.
/// </summary>
/// <remarks>
/// <para><b>⚠ <c>net</c> defaults to FALSE, matching SQL Server, and it is a ONE-WAY DOOR.</b>
/// <c>@supports_net_changes</c> is fixed when the capture instance is created and cannot be changed without
/// dropping the instance (and with it its history) or spending the table's second instance. We do not wrap the
/// net-changes reader at all (docs §1.7d — the collapse is a business-layer transformation, reproducible in one
/// line of DuckDB with a measured-identical outcome), so this flag exists only for a caller who wants to call
/// SQL Server's net TVF directly. It also needs a PRIMARY KEY or an explicit <c>index :=</c>, else the server
/// raises <c>22939</c>.</para>
/// <para>⚠ Enabling capture on a table is DDL that creates a change table and two TVFs, so this reports
/// <c>SchemaMayChange</c>. Idempotent-ish: a capture instance that already exists is reported as
/// <c>changed = false</c> rather than raising, because re-running a setup script should not fail.</para>
/// </remarks>
internal sealed class CdcEnableFunction : ICatalogTableFunction
{
    private readonly SqlServerCatalog _catalog;

    internal CdcEnableFunction(SqlServerCatalog catalog) => _catalog = catalog;

    public string SchemaName => SqlServerCdcFunctions.SchemaName;

    public string Name => "enable";

    /// <summary>
    /// One positional source table; everything else NAMED, which is how an optional argument is expressed
    /// (DuckDB positional table arguments have no defaults, so without this a caller would have to write
    /// <c>enable('t', NULL, NULL, NULL, NULL, NULL)</c>). Named parameters must come last — DuckDB's own rule,
    /// checked at declaration time by <c>Params.Validate</c>.
    /// </summary>
    public Schema Parameters { get; } = new(new[]
    {
        Params.Positional("source", StringType.Default, nullable: false),
        Params.Named("capture_instance", StringType.Default),
        Params.Named("columns", StringType.Default),
        Params.Named("role", StringType.Default),
        Params.Named("index", StringType.Default),
        Params.Named("filegroup", StringType.Default),
        Params.Named("net", BooleanType.Default),
    }, metadata: null);

    public ITableFunctionBinding Bind(RecordBatch args)
    {
        // Read every argument HERE: the stream they were imported from is disposed when tablefn_bind returns,
        // so a binding that kept the batch would read freed Arrow buffers at execution time.
        string? source = Str(args, 0);
        string? instance = Str(args, 1);
        string? columns = Str(args, 2);
        string? role = Str(args, 3);
        string? index = Str(args, 4);
        string? filegroup = Str(args, 5);
        bool net = Bool(args, 6) ?? false;
        var (schema, table) = SqlServerCdcSetup.SplitSource(source ?? string.Empty, "cdc.enable");
        return new CdcSetupBinding(
            () => _catalog.CdcEnableTable(schema, table, instance, columns, role, index, filegroup, net));
    }

    internal static string? Str(RecordBatch? args, int col) =>
        args is null || col >= args.ColumnCount || args.Length == 0
            ? null
            : ArrowValueReader.ReadScalar(args.Column(col), 0)?.ToString();

    internal static bool? Bool(RecordBatch? args, int col)
    {
        if (args is null || col >= args.ColumnCount || args.Length == 0)
        {
            return null;
        }
        var v = ArrowValueReader.ReadScalar(args.Column(col), 0);
        return v is null ? null : Convert.ToBoolean(v);
    }
}

/// <summary>
/// <c>db.cdc.disable('dbo.orders' [, capture_instance :=])</c> — <c>sys.sp_cdc_disable_table</c>. With no
/// instance named it disables <c>all</c> of the table's instances, which is <c>sp_cdc_disable_table</c>'s own
/// default spelling.
/// </summary>
/// <remarks>
/// ⚠ This DESTROYS the capture instance and its recorded history. It is offered (unlike a
/// <c>disable_database</c>) because it is per-TABLE and named explicitly by the caller, which is the same
/// consent line <c>DROP TABLE</c> sits on.
/// </remarks>
internal sealed class CdcDisableFunction : ICatalogTableFunction
{
    private readonly SqlServerCatalog _catalog;

    internal CdcDisableFunction(SqlServerCatalog catalog) => _catalog = catalog;

    public string SchemaName => SqlServerCdcFunctions.SchemaName;

    public string Name => "disable";

    public Schema Parameters { get; } = new(new[]
    {
        Params.Positional("source", StringType.Default, nullable: false),
        Params.Named("capture_instance", StringType.Default),
    }, metadata: null);

    public ITableFunctionBinding Bind(RecordBatch args)
    {
        string? source = CdcEnableFunction.Str(args, 0);
        string? instance = CdcEnableFunction.Str(args, 1);
        var (schema, table) = SqlServerCdcSetup.SplitSource(source ?? string.Empty, "cdc.disable");
        return new CdcSetupBinding(() => _catalog.CdcDisableTable(schema, table, instance));
    }
}

/// <summary>
/// <c>db.cdc.capture_now()</c> — <c>sys.sp_cdc_scan</c>, forcing the capture job's log scan to run now instead of
/// waiting a polling interval.
/// </summary>
/// <remarks>
/// <para><b>⚠ This is a maintenance action, and shipping it is a judgement call rather than an oversight
/// (docs §3.5).</b> Forcing a log scan costs CPU that belongs to the DBA's budget, and a caller who reaches
/// for it PER QUERY is making a load decision they probably do not intend. It is here because it is what makes
/// a test deterministic and what unblocks a container with no agent — and the reader never calls it.</para>
/// <para><b>⚠⚠ IT RACES THE CAPTURE JOB, and the failure is a hard error naming something unrecognisable.</b>
/// There is ONE log-scan session per database, so if the capture job holds it this raises
/// <c>22903: Another connection with session ID N is already running 'sp_replcmds' for Change Data Capture in
/// the current database.</c> MEASURED at roughly 1 failure in 57 attempts against a live job — and MEASURED
/// that retrying does NOT fix it (a 20-attempt loop with backoff lost all 20 while the job was confirmed
/// running). So the error is TRANSLATED to name the cause and the remedy rather than passed through:
/// stop the capture job for the duration, or wait a polling interval instead of forcing one.</para>
/// <para>⚠ It reports <c>SchemaMayChange = false</c>: a log scan moves data into existing change tables and
/// creates nothing. Rebuilding the catalog after it would be pure waste on the one function here most likely
/// to be called in a loop.</para>
/// <para>⚠ <b>DO NOT RENAME IT BACK TO <c>scan</c> to match <c>sp_cdc_scan</c>.</b> It shipped as
/// <c>cdc.scan()</c> for one day and the first person to read the function list — the user — took it for the
/// READER ("so scan will be the one to get a snapshot leg + the changes?"). That is the obvious reading in a
/// namespace whose whole point is reading changes, and it is the reading that gets expensive once
/// <c>cdc.changes()</c> sits beside it: a caller who wants rows and finds a function called <c>scan</c> will
/// call it in a loop, which is exactly the load decision the ⚠ above says they probably do not intend. The
/// underlying proc keeps its own name in the SQL and in every comment, so "scan" in this file means SQL
/// Server's procedure and nothing else.</para>
/// </remarks>
internal sealed class CdcCaptureNowFunction : ICatalogTableFunction
{
    private readonly SqlServerCatalog _catalog;

    internal CdcCaptureNowFunction(SqlServerCatalog catalog) => _catalog = catalog;

    public string SchemaName => SqlServerCdcFunctions.SchemaName;

    public string Name => "capture_now";

    public Schema Parameters { get; } = new(System.Array.Empty<Field>(), metadata: null);

    public ITableFunctionBinding Bind(RecordBatch args) =>
        new CdcSetupBinding(() => _catalog.CdcCaptureNow(), schemaMayChange: false);
}

/// <summary>
/// The shared binding for the CDC setup functions: runs the work EAGERLY in <see cref="Execute"/> and reports
/// <see cref="SchemaMayChange"/> so the host rebuilds its metadata cache (ABI v81).
/// </summary>
/// <remarks>
/// <para>⚠⚠ <b>The <c>_work()</c> call is in <see cref="Execute"/> — the plain method — and moving it into the
/// iterator would break the whole mechanism silently.</b> The host reads <c>SchemaMayChange</c> when
/// <c>tablefn_execute</c> returns; an iterator body has not started at that point (it begins at the first
/// batch PULL, a different crossing), so the DDL would run and the cache would never be rebuilt. The rows are
/// therefore computed here and merely handed over by the iterator.</para>
/// <para>⚠ The same placement independently fixes the ambient problem: <c>AmbientTransaction</c> is an
/// <c>AsyncLocal</c> established per crossing, and an iterator may run on whatever thread DuckDB pulls from.
/// Doing the work in <c>Execute</c> means it runs on the thread the host set the ambients on. The bind-time
/// capture below is belt-and-braces for the same reason the session-tag function keeps one.</para>
/// </remarks>
internal sealed class CdcSetupBinding : ITableFunctionBinding
{
    private readonly Func<RecordBatch> _work;
    private readonly bool _schemaMayChange;
    private readonly long _txnId;
    private bool _ran;

    internal CdcSetupBinding(Func<RecordBatch> work, bool schemaMayChange = true)
    {
        _work = work;
        _schemaMayChange = schemaMayChange;
        _txnId = AmbientTransaction.Current;
    }

    public Schema OutputSchema => SqlServerCdcSetup.ReportColumns;

    public bool SupportsFilterPushdown => false;

    public bool SupportsProjectionPushdown => false;

    /// <summary>
    /// True once the work has actually RUN and only for the functions that change the catalog. Gating on
    /// <c>_ran</c> rather than answering unconditionally keeps a bind that was never executed — an
    /// <c>EXPLAIN</c>, or a plan DuckDB discarded — from triggering a pointless rebuild.
    /// </summary>
    public bool SchemaMayChange => _ran && _schemaMayChange;

    public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
    {
        // Dispose the pushed filter values in this PLAIN method — an async-iterator body does not begin until
        // the host's first get_next, long after InitGlobal returned, and the producer is owned by the scan's
        // global state (the documented late-release use-after-free).
        scan.FilterValues?.Dispose();
        if (AmbientTransaction.Current == 0 && _txnId != 0)
        {
            AmbientTransaction.Current = _txnId;
        }
        // THE WORK, EAGERLY. See the class remarks: the host reads SchemaMayChange as soon as this returns.
        var row = _work();
        _ran = true;
        return Rows(row);
    }

    private static async IAsyncEnumerable<RecordBatch> Rows(RecordBatch row)
    {
        await System.Threading.Tasks.Task.CompletedTask;
        yield return row;
    }

    public void Dispose()
    {
    }
}
