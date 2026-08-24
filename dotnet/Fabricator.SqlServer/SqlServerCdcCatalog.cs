using System;
using System.Collections.Generic;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Fabricator.SqlServer;

/// <summary>
/// The catalog side of the <c>db.cdc.*</c> surface: the T-SQL, and the conversion of its all-<c>varchar</c>
/// results into the declared Arrow shapes. The function objects themselves are in <c>SqlServerCdc.cs</c>.
/// </summary>
/// <remarks>
/// Every query here is a single BATCH guarded on <c>is_cdc_enabled</c> (see
/// <see cref="SqlServerCdcFunctions"/> for the three measured reasons), and it goes through
/// <c>ReadMetadataRows</c>, which routes onto the transaction's pinned connection when one exists — so a
/// capture instance enabled inside an explicit transaction is visible to a <c>cdc.tables()</c> in the same
/// transaction.
/// </remarks>
public sealed partial class SqlServerCatalog
{
    /// <summary>
    /// <c>sys.fn_cdc_get_max_lsn()</c>, or null. NULL means one of two STATES, never a failure: CDC is not
    /// enabled on this database, or the capture job has not yet run (§8.3). <c>cdc.health()</c> distinguishes
    /// them.
    /// </summary>
    /// <remarks>
    /// ⚠ The <c>IF/ELSE</c> is deliberate rather than a <c>CASE</c>: SQL Server does not guarantee that a
    /// <c>CASE</c> branch's function call goes unevaluated, and evaluating <c>fn_cdc_get_max_lsn()</c> with CDC
    /// disabled raises <c>208 Invalid object name 'cdc.lsn_time_mapping'</c> (MEASURED). Both branches project
    /// the same single <c>varchar(30)</c> column, so the result shape does not depend on which one ran.
    /// </remarks>
    internal byte[]? CdcMaxLsn()
    {
        const string sql =
            "SET NOCOUNT ON; " +
            "IF " + SqlServerCdcFunctions.CdcEnabledPredicate + " " +
            "SELECT CONVERT(varchar(30), sys.fn_cdc_get_max_lsn(), 1) AS max_lsn; " +
            "ELSE SELECT CAST(NULL AS varchar(30)) AS max_lsn;";
        var rows = ReadMetadataRows(sql, 1);
        return rows.Count == 0 ? null : SqlServerCdcFunctions.ParseHex(rows[0][0]);
    }

    /// <summary>
    /// The retention floor for one capture instance, resolved from either a capture-instance name or a
    /// <c>schema.table</c> name. Refuses rather than guesses when the name is ambiguous or the table has two
    /// capture instances (§2.2), and names what it matched.
    /// </summary>
    /// <remarks>
    /// <para>⚠ Both matches are EXACT — there is no prefix or fuzzy rule — so the only ambiguity possible is a
    /// capture instance whose name is literally spelled like some table's <c>schema.table</c>, which is
    /// refused rather than resolved by precedence. Silently picking one would answer for the wrong instance,
    /// and two instances of one table can have DIFFERENT floors.</para>
    /// <para>The "not captured" message costs one extra round trip (<see cref="CdcDatabaseState"/>) and only
    /// on the failure path, because "this database has no CDC" and "this table is not captured" send a reader
    /// to completely different places.</para>
    /// <para><b>⚠⚠ NULL IS A STATE HERE TOO, AND IT IS THE DANGEROUS ONE. MEASURED 2026-08-23, reproduced
    /// with a discriminator:</b> for a capture instance enabled in a database whose capture job is ALREADY
    /// running, <c>fn_cdc_get_min_lsn</c> returns NULL for up to one polling interval — while
    /// <c>cdc.change_tables.start_lsn</c> for that very instance is ALREADY SET
    /// (<c>0x0000002E000009100034</c>, with the function answering NULL beside it; ~8 s later both agree).
    /// So the function is not simply projecting <c>start_lsn</c>, and the floor is briefly UNKNOWABLE rather
    /// than absent.</para>
    /// <para>⚠ <b>Consequence for the reader's pre-check (§2.1), which is the highest-value line in the whole
    /// feature: a NULL floor must NOT be read as "no lower bound".</b> Treating it that way passes the window
    /// through to the TVF, which answers with the misleading 313. The honest answer is "the retention floor is
    /// not established yet — retry", and it is why this returns NULL rather than substituting
    /// <c>start_lsn</c>: substituting would ASSERT a floor the engine declined to state.</para>
    /// <para>⚠ Do NOT generalise from a fresh database: there the FIRST enable in a newly CDC-enabled database
    /// returned a non-NULL floor immediately (while <c>max_lsn</c> was still NULL), which is the measurement
    /// that refuted the simple "it is NULL until the job runs" story and forced the discriminator above.</para>
    /// </remarks>
    internal byte[]? CdcMinLsn(string source)
    {
        const string sql =
            "SET NOCOUNT ON; " +
            SqlServerCdcFunctions.HelpTableVar + " " +
            SqlServerCdcFunctions.FillHelpTableVar + " " +
            "SELECT CAST(capture_instance AS varchar(128)) AS capture_instance, " +
            "CASE WHEN capture_instance = @source THEN '1' ELSE '0' END AS by_instance, " +
            "CONVERT(varchar(30), sys.fn_cdc_get_min_lsn(capture_instance), 1) AS min_lsn " +
            "FROM @cdct " +
            "WHERE capture_instance = @source OR (source_schema + '.' + source_table) = @source " +
            "ORDER BY capture_instance;";
        var rows = ReadMetadataRows(sql, 3, new[] { new SqlParameter("@source", source) });
        if (rows.Count == 0)
        {
            // ⚠ Which of the two it is decides where the reader goes next, so the message must not conflate
            // them: "this database has no CDC" and "this table is not captured" have different remedies.
            // One extra round trip, only on the failure path.
            var (enabled, database) = CdcDatabaseState();
            throw new ArgumentException(
                enabled
                    ? $"cdc.min_position: '{source}' is not a captured table or capture instance in database "
                      + $"'{database}'. SELECT * FROM <catalog>.cdc.tables() lists what is captured."
                    : "cdc.min_position: change data capture is not enabled on database "
                      + $"'{database}'. Enable it first, then enable the table.");
        }
        if (rows.Count > 1)
        {
            bool mixed = false;
            for (int i = 1; i < rows.Count; i++)
            {
                mixed |= !string.Equals(rows[i][1], rows[0][1], StringComparison.Ordinal);
            }
            var names = new List<string>(rows.Count);
            foreach (var row in rows)
            {
                names.Add(row[0] ?? "?");
            }
            throw new ArgumentException(
                $"cdc.min_position: '{source}' matches {rows.Count} capture instances "
                + $"({string.Join(", ", names)})"
                + (mixed ? " — as both a capture-instance name and a source table name" : string.Empty)
                + ". Two capture instances of one table can have different retention floors, so name the "
                + "instance explicitly; SELECT * FROM <catalog>.cdc.tables() lists them.");
        }
        return SqlServerCdcFunctions.ParseHex(rows[0][2]);
    }

    /// <summary>
    /// Whether the connected database has CDC enabled, and its name. Used only to word an error precisely —
    /// the name comes from <c>DB_NAME()</c> rather than from a connection-string parse, so it is the database
    /// the statement actually ran against.
    /// </summary>
    private (bool Enabled, string? Database) CdcDatabaseState()
    {
        const string sql = "SET NOCOUNT ON; SELECT CASE WHEN " + SqlServerCdcFunctions.CdcEnabledPredicate
                           + " THEN '1' ELSE '0' END AS enabled, CAST(DB_NAME() AS varchar(128)) AS db;";
        var rows = ReadMetadataRows(sql, 2);
        return rows.Count > 0 ? (rows[0][0] == "1", rows[0][1]) : (false, null);
    }

    /// <summary>One row per capture INSTANCE, from <c>sys.sp_cdc_help_change_data_capture</c>; empty when CDC
    /// is not enabled.</summary>
    internal RecordBatch CdcTables()
    {
        const string sql =
            "SET NOCOUNT ON; " +
            SqlServerCdcFunctions.HelpTableVar + " " +
            SqlServerCdcFunctions.FillHelpTableVar + " " +
            "SELECT CAST(source_schema AS varchar(128)), CAST(source_table AS varchar(128)), " +
            "CAST(capture_instance AS varchar(128)), " +
            "CONVERT(varchar(30), start_lsn, 1), CONVERT(varchar(30), end_lsn, 1), " +
            // CAST(bit AS varchar) rather than a CASE: it preserves NULL, and has_drop_pending is MEASURED
            // to be NULL on an ordinary instance. Turning that into 'false' would assert something the
            // server did not say.
            "CAST(supports_net_changes AS varchar(1)), CAST(has_drop_pending AS varchar(1)), " +
            "CAST(role_name AS varchar(128)), CAST(index_name AS varchar(128)), " +
            "CAST(filegroup_name AS varchar(128)), " +
            "CONVERT(varchar(27), create_date, 126), " +
            // varchar(max), not varchar(8000): the column list of a wide table would otherwise be TRUNCATED,
            // which is a silently wrong answer about which columns are captured.
            //
            // ⚠⚠ THE CASE IS A BUG FIX, NOT DEFENSIVENESS (docs §15.14). MEASURED through this exact
            // table-variable path: the ALL-TABLES form of sp_cdc_help_change_data_capture LEAKS the previous
            // row's index_column_list onto a row whose index_name is NULL, so a capture instance with no
            // index was reported as having one - `dbo_plain | [id] | <NULL>`, where the [id] belongs to the
            // row above. Called for that one table the proc answers NULL correctly, which is what attributes
            // it to the proc rather than to us. index_name is the discriminator SQL Server does report
            // correctly.
            "CASE WHEN index_name IS NULL THEN NULL ELSE CAST(index_column_list AS varchar(max)) END, " +
            "CAST(captured_column_list AS varchar(max)) " +
            "FROM @cdct ORDER BY source_schema, source_table, capture_instance;";
        var rows = ReadMetadataRows(sql, 13);

        var schemas = new StringArray.Builder();
        var tables = new StringArray.Builder();
        var instances = new StringArray.Builder();
        var startLsn = new BinaryArray.Builder();
        var endLsn = new BinaryArray.Builder();
        var net = new BooleanArray.Builder();
        var dropPending = new BooleanArray.Builder();
        var role = new StringArray.Builder();
        var index = new StringArray.Builder();
        var filegroup = new StringArray.Builder();
        var created = new TimestampArray.Builder((TimestampType)CdcTablesFunction.Columns.GetFieldByName("create_date").DataType);
        var indexCols = new StringArray.Builder();
        var capturedCols = new StringArray.Builder();

        foreach (var row in rows)
        {
            schemas.Append(row[0]);
            tables.Append(row[1]);
            instances.Append(row[2]);
            AppendBlob(startLsn, row[3]);
            AppendBlob(endLsn, row[4]);
            AppendBit(net, row[5]);
            AppendBit(dropPending, row[6]);
            role.Append(row[7]);
            index.Append(row[8]);
            filegroup.Append(row[9]);
            AppendMicros(created, row[10]);
            indexCols.Append(row[11]);
            capturedCols.Append(row[12]);
        }
        return new RecordBatch(CdcTablesFunction.Columns, new IArrowArray[]
        {
            schemas.Build(), tables.Build(), instances.Build(), startLsn.Build(), endLsn.Build(),
            net.Build(), dropPending.Build(), role.Build(), index.Build(), filegroup.Build(),
            created.Build(), indexCols.Build(), capturedCols.Build(),
        }, rows.Count);
    }

    /// <summary>
    /// (property, value) diagnostic rows: whether CDC is enabled, the agent's state, the capture/cleanup job
    /// configuration, and how far behind capture is.
    /// </summary>
    /// <remarks>
    /// <para>Two round trips, and the split is the point. The main batch must not so much as MENTION
    /// <c>sys.dm_server_services</c>: a batch referencing a nonexistent object fails at COMPILE, so on Azure
    /// SQL Database — where that DMV does not exist but CDC does — a single batch would make the whole health
    /// surface unavailable. The agent probe is therefore its own statement, skipped by EDITION rather than
    /// attempted and caught.</para>
    /// <para>⚠ The permission is asked about, not tried: <c>HAS_PERMS_BY_NAME</c> is a pure function, so a
    /// reader without <c>VIEW SERVER STATE</c> gets <c>unknown</c> without a failed statement (§8.4 — unknown
    /// must stay unknown, because reporting an agent as stopped when we merely cannot see it sends an operator
    /// to fix the wrong thing).</para>
    /// </remarks>
    internal RecordBatch CdcHealth()
    {
        const string sql =
            "SET NOCOUNT ON; " +
            SqlServerCdcFunctions.HelpTableVar + " " +
            SqlServerCdcFunctions.FillHelpTableVar + " " +
            "DECLARE @jobs TABLE(job_id uniqueidentifier, job_type nvarchar(20), job_name sysname NULL, " +
            "maxtrans int, maxscans int, continuous bit, pollinginterval bigint, retention bigint, " +
            "threshold bigint); " +
            "DECLARE @enabled bit = CASE WHEN " + SqlServerCdcFunctions.CdcEnabledPredicate
            + " THEN 1 ELSE 0 END; " +
            "IF @enabled = 1 INSERT INTO @jobs EXEC sys.sp_cdc_help_jobs; " +
            "DECLARE @maxlsn varchar(30) = NULL, @maxtime varchar(27) = NULL, @lag varchar(32) = NULL; " +
            "IF @enabled = 1 BEGIN " +
            "  SET @maxlsn = CONVERT(varchar(30), sys.fn_cdc_get_max_lsn(), 1); " +
            "  IF @maxlsn IS NOT NULL BEGIN " +
            "    SET @maxtime = CONVERT(varchar(27), sys.fn_cdc_map_lsn_to_time(sys.fn_cdc_get_max_lsn()), 126); " +
            "    SET @lag = CAST(DATEDIFF(second, sys.fn_cdc_map_lsn_to_time(sys.fn_cdc_get_max_lsn()), " +
            "                             GETDATE()) AS varchar(32)); " +
            "  END " +
            "END " +
            // Every value CAST to one width: a VALUES constructor unifies its column type across all rows, and
            // an unstated width can truncate the longest of them.
            "SELECT CAST(p AS varchar(64)) AS property, CAST(v AS varchar(256)) AS value FROM (VALUES " +
            "('database', CAST(DB_NAME() AS varchar(256))), " +
            "('cdc_enabled', CAST(CASE WHEN @enabled = 1 THEN 'true' ELSE 'false' END AS varchar(256))), " +
            "('captured_instances', CAST((SELECT COUNT(*) FROM @cdct) AS varchar(256))), " +
            "('capture_job', CAST((SELECT MAX(job_name) FROM @jobs WHERE job_type = 'capture') AS varchar(256))), " +
            "('capture_polling_interval_seconds', " +
            " CAST((SELECT MAX(pollinginterval) FROM @jobs WHERE job_type = 'capture') AS varchar(256))), " +
            "('cleanup_job', CAST((SELECT MAX(job_name) FROM @jobs WHERE job_type = 'cleanup') AS varchar(256))), " +
            "('cleanup_retention_minutes', " +
            " CAST((SELECT MAX(retention) FROM @jobs WHERE job_type = 'cleanup') AS varchar(256))), " +
            "('max_lsn', CAST(@maxlsn AS varchar(256))), " +
            "('max_lsn_time', CAST(@maxtime AS varchar(256))), " +
            // ⚠ NOT called `capture_lag_seconds`, which is what the design sketch asked for and would be a
            // misleading name: this is the AGE of the newest CAPTURED transaction, so on an idle database it
            // grows without bound while capture is perfectly current. It is an UPPER BOUND on lag and a
            // signal only when read beside known write traffic — a name promising "lag" would send an
            // operator to chase a healthy system.
            "('max_lsn_age_seconds', CAST(@lag AS varchar(256)))" +
            ") AS t(p, v);";

        var rows = ReadMetadataRows(sql, 2);
        var properties = new StringArray.Builder();
        var values = new StringArray.Builder();
        int n = 0;
        properties.Append("supports_cdc");
        values.Append("true");   // this function does not exist on an engine where it is false
        n++;
        foreach (var row in rows)
        {
            properties.Append(row[0]);
            values.Append(row[1]);
            n++;
        }
        properties.Append("agent_status");
        values.Append(CdcAgentStatus());
        n++;
        return new RecordBatch(CdcHealthFunction.Columns,
                               new IArrowArray[] { properties.Build(), values.Build() }, n);
    }

    /// <summary>
    /// The SQL Server Agent's state, or <c>"unknown"</c> — never <c>"Stopped"</c> on a guess. Skipped by
    /// EDITION on Azure SQL Database, where the DMV does not exist and there is no agent to report on.
    /// </summary>
    private string CdcAgentStatus()
    {
        if (Profile.EngineEdition == ServerProfile.EditionAzureSqlDatabase)
        {
            return "not applicable (Azure SQL Database has no SQL Server Agent)";
        }
        const string sql =
            "SET NOCOUNT ON; " +
            "IF HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE') = 1 " +
            "  SELECT CAST(ISNULL(MAX(status_desc), 'unknown') AS varchar(64)) AS agent " +
            "  FROM sys.dm_server_services WHERE servicename LIKE 'SQL Server Agent%'; " +
            "ELSE SELECT CAST('unknown (needs VIEW SERVER STATE)' AS varchar(64)) AS agent;";
        var rows = ReadMetadataRows(sql, 1);
        return rows.Count > 0 ? rows[0][0] ?? "unknown" : "unknown";
    }

    private static void AppendBlob(BinaryArray.Builder builder, string? hex)
    {
        var bytes = SqlServerCdcFunctions.ParseHex(hex);
        if (bytes is null)
        {
            builder.AppendNull();
        }
        else
        {
            builder.Append(bytes.AsSpan());
        }
    }

    private static void AppendBit(BooleanArray.Builder builder, string? bit)
    {
        if (string.IsNullOrEmpty(bit))
        {
            builder.AppendNull();
        }
        else
        {
            builder.Append(bit == "1");
        }
    }

    // ⚠ Built from the DECLARED field's TimestampType, never `new TimestampArray.Builder()`: that default is
    // MILLISECOND while our column declares MICROSECOND, and the mismatch reads back as January 1970 — the
    // defect that hit 15 hand-rolled Fabric API sites and went unnoticed because nobody looked at the times.
    private static void AppendMicros(TimestampArray.Builder builder, string? iso)
    {
        long? micros = SqlServerCdcFunctions.ParseTimestampMicros(iso);
        if (micros is null)
        {
            builder.AppendNull();
        }
        else
        {
            builder.Append(DateTimeOffset.UnixEpoch.AddTicks(micros.Value * 10));
        }
    }

    // ---- slice 2: the SETUP half ----------------------------------------------------------------------

    /// <summary>
    /// <c>sys.sp_cdc_enable_db</c>, idempotently. Returns one report row; <c>changed = false</c> when the
    /// database was already enabled.
    /// </summary>
    /// <remarks>
    /// The guard is not politeness: a setup script that cannot be re-run is a setup script people stop
    /// trusting. The check and the call are ONE batch, so nothing can change between them.
    /// </remarks>
    internal RecordBatch CdcEnableDatabase()
    {
        const string sql =
            "SET NOCOUNT ON; " +
            "DECLARE @db varchar(128) = CAST(DB_NAME() AS varchar(128)); " +
            "IF " + SqlServerCdcFunctions.CdcEnabledPredicate + " " +
            "  SELECT @db AS target, '0' AS changed, " +
            "         CAST('change data capture was already enabled on this database' AS varchar(400)) AS detail; " +
            "ELSE BEGIN " +
            "  EXEC sys.sp_cdc_enable_db; " +
            "  SELECT @db AS target, '1' AS changed, " +
            "         CAST('enabled; capture and cleanup jobs created. Enabling is not the same as capturing" +
            " - use cdc.health() to confirm the agent is running' AS varchar(400)) AS detail; " +
            "END";
        return CdcReportFrom(ReadMetadataRows(sql, 3), "cdc.enable_database");
    }

    /// <summary>
    /// <c>sys.sp_cdc_enable_table</c> for one source table, idempotently per capture INSTANCE.
    /// </summary>
    /// <remarks>
    /// <para>Every caller-supplied value crosses as a PARAMETER, never spliced text — these are identifiers
    /// and a column list a user types, which is the one place a wrapper like this must not get clever.</para>
    /// <para>The idempotence check keys on the capture INSTANCE, not the table, because a table legitimately
    /// has two of them and "this table is already captured" would wrongly refuse the second. With no
    /// <c>capture_instance</c> given, SQL Server's default is <c>&lt;schema&gt;_&lt;table&gt;</c>, so the check
    /// resolves that name first rather than skipping the guard.</para>
    /// </remarks>
    internal RecordBatch CdcEnableTable(string schema, string table, string? captureInstance,
                                       string? columns, string? role, string? index, string? filegroup,
                                       bool net)
    {
        const string sql =
            "SET NOCOUNT ON; " +
            "DECLARE @inst sysname = ISNULL(@capture_instance, @schema + N'_' + @table); " +
            "DECLARE @target varchar(400) = CAST(@schema + N'.' + @table + N' (' + @inst + N')' AS varchar(400)); " +
            "IF NOT " + SqlServerCdcFunctions.CdcEnabledPredicate + " " +
            "  THROW 50001, 'cdc.enable: change data capture is not enabled on this database - call " +
            "cdc.enable_database() first', 1; " +
            SqlServerCdcFunctions.HelpTableVar + " " +
            SqlServerCdcFunctions.FillHelpTableVar + " " +
            "IF EXISTS (SELECT 1 FROM @cdct WHERE capture_instance = @inst) " +
            "  SELECT @target AS target, '0' AS changed, " +
            "         CAST('this capture instance already exists' AS varchar(400)) AS detail; " +
            "ELSE BEGIN " +
            "  EXEC sys.sp_cdc_enable_table @source_schema = @schema, @source_name = @table, " +
            "       @capture_instance = @inst, @captured_column_list = @columns, @role_name = @role, " +
            "       @index_name = @index, @filegroup_name = @filegroup, @supports_net_changes = @net; " +
            "  SELECT @target AS target, '1' AS changed, " +
            "         CAST('capture instance created; a change table and two table-valued functions now " +
            "exist' AS varchar(400)) AS detail; " +
            "END";
        return CdcReportFrom(ReadMetadataRows(sql, 3, new[]
        {
            new SqlParameter("@schema", schema),
            new SqlParameter("@table", table),
            NullableParam("@capture_instance", captureInstance),
            NullableParam("@columns", columns),
            NullableParam("@role", role),
            NullableParam("@index", index),
            NullableParam("@filegroup", filegroup),
            new SqlParameter("@net", net ? 1 : 0),
        }), "cdc.enable");
    }

    /// <summary>
    /// <c>sys.sp_cdc_disable_table</c>. With no instance named it passes the procedure's own
    /// every-instance spelling rather than an invention here.
    /// </summary>
    internal RecordBatch CdcDisableTable(string schema, string table, string? captureInstance)
    {
        const string sql =
            "SET NOCOUNT ON; " +
            "DECLARE @inst sysname = ISNULL(@capture_instance, N'all'); " +
            "DECLARE @target varchar(400) = CAST(@schema + N'.' + @table + N' (' + @inst + N')' AS varchar(400)); " +
            SqlServerCdcFunctions.HelpTableVar + " " +
            SqlServerCdcFunctions.FillHelpTableVar + " " +
            "IF NOT EXISTS (SELECT 1 FROM @cdct WHERE source_schema = @schema AND source_table = @table " +
            "               AND (@capture_instance IS NULL OR capture_instance = @capture_instance)) " +
            "  SELECT @target AS target, '0' AS changed, " +
            "         CAST('no such capture instance - nothing to disable' AS varchar(400)) AS detail; " +
            "ELSE BEGIN " +
            "  EXEC sys.sp_cdc_disable_table @source_schema = @schema, @source_name = @table, " +
            "       @capture_instance = @inst; " +
            "  SELECT @target AS target, '1' AS changed, " +
            "         CAST('capture instance disabled; its change table and recorded history are gone' " +
            "AS varchar(400)) AS detail; " +
            "END";
        return CdcReportFrom(ReadMetadataRows(sql, 3, new[]
        {
            new SqlParameter("@schema", schema),
            new SqlParameter("@table", table),
            NullableParam("@capture_instance", captureInstance),
        }), "cdc.disable");
    }

    /// <summary>
    /// <c>sys.sp_cdc_scan</c> — force the capture job's log scan now.
    /// </summary>
    /// <remarks>
    /// TRANSLATES THE RACE, because the raw error names nothing a reader would connect to CDC. There is ONE
    /// log-scan session per database, so a running capture job makes this fail with
    /// <c>22903 ... already running 'sp_replcmds' ...</c> — MEASURED at roughly 1 attempt in 57, and MEASURED
    /// to be unfixable by retrying (20 attempts with backoff, all lost, the job confirmed running throughout).
    /// So no retry is attempted; the message says what to do instead.
    /// </remarks>
    internal RecordBatch CdcCaptureNow()
    {
        const string sql =
            "SET NOCOUNT ON; " +
            "IF NOT " + SqlServerCdcFunctions.CdcEnabledPredicate + " " +
            "  THROW 50002, 'cdc.capture_now: change data capture is not enabled on this database', 1; " +
            "EXEC sys.sp_cdc_scan; " +
            "SELECT CAST(DB_NAME() AS varchar(128)) AS target, '1' AS changed, " +
            "       CAST('log scan completed' AS varchar(400)) AS detail;";
        try
        {
            return CdcReportFrom(ReadMetadataRows(sql, 3), "cdc.capture_now");
        }
        catch (SqlException ex) when (ex.Number == 22903)
        {
            throw new InvalidOperationException(
                "cdc.capture_now: the capture job currently holds this database's single log-scan session, so a "
                + "manual scan cannot run (SQL Server reports it as sp_replcmds already running). Retrying "
                + "does not help - an actively scanning job can hold that session right through a retry "
                + "budget. Either stop the capture job for the duration "
                + "(EXEC sys.sp_cdc_stop_job @job_type='capture'), or wait one polling interval and let the "
                + "job do it; cdc.health() reports that interval.", ex);
        }
    }

    // A nullable string parameter: DBNull rather than null, which SqlClient rejects.
    private static SqlParameter NullableParam(string name, string? value) =>
        new(name, (object?)value ?? DBNull.Value);

    // The report row, re-typed from the all-varchar result. A missing row means the batch took a path that
    // projected nothing, which is a bug HERE rather than a server state — say so, instead of returning an
    // empty batch the caller would read as "nothing happened".
    private static RecordBatch CdcReportFrom(List<string?[]> rows, string fn)
    {
        if (rows.Count == 0)
        {
            throw new InvalidOperationException($"{fn}: the server returned no report row.");
        }
        return SqlServerCdcSetup.Report(rows[0][0], rows[0][1] == "1", rows[0][2] ?? string.Empty);
    }

    // ---- slice 3: the READER --------------------------------------------------------------------------

    /// <summary>The five metadata columns every read emits, plus the opt-in sixth.</summary>
    private const string CdcChangeTypeSql =
        "CASE c.[__$operation] WHEN 1 THEN 'delete' WHEN 2 THEN 'insert' WHEN 3 THEN 'update_preimage' "
        + "WHEN 4 THEN 'update_postimage' WHEN 5 THEN 'upsert' "
        // ⚠ An ELSE rather than an implicit NULL. The five codes are SQL Server's documented set, so this
        // branch is unreachable today — but an unknown operation surfacing as its own NUMBER is recoverable,
        // while one surfacing as a NULL _change_type is a row a consumer would silently mis-handle.
        + "ELSE CONVERT(varchar(16), c.[__$operation]) END AS [_change_type]";

    /// <summary>
    /// <c>start_lsn ‖ seqval ‖ operation</c> as ONE 21-byte value (§2.4), whose lexicographic order IS the
    /// change order — so <c>ORDER BY _position</c> replays correctly and <c>max(_position)</c> resumes
    /// correctly, with the consumer's cursor one BLOB rather than three columns and the §1.3 predicate.
    /// </summary>
    /// <remarks>
    /// MEASURED against the rig: the concatenation is 21 bytes and the operation byte is the low byte of the
    /// <c>int</c> (<c>0x…02</c> for an insert, <c>0x…04</c> for an update after-image, <c>0x…01</c> for a
    /// delete). ⚠ <c>__$command_id</c> is deliberately NOT in it: the TVF does not return that column at all
    /// (§11 item 5, MEASURED — exactly 8 columns), and within one <c>__$start_lsn</c> the seqvals already
    /// order statements the way command_id does. The tuple is COMPLETE without it.
    /// </remarks>
    private const string CdcPositionSql =
        "c.[__$start_lsn] + c.[__$seqval] + CONVERT(binary(1), c.[__$operation]) AS [_position]";

    /// <summary>
    /// Resolves the capture instance, describes the statement it will run, and declares the output schema.
    /// </summary>
    /// <remarks>
    /// <para><b>Two round trips, and each buys something the other cannot.</b> The first resolves the capture
    /// instance THROUGH <c>sp_cdc_help_change_data_capture</c> — so the instance's <c>@role_name</c>
    /// permission filtering applies, which is security logic not worth reimplementing — and brings back the
    /// SOURCE table's nullability. The second DESCRIBES the reader's own statement, which is where the TYPES
    /// come from.</para>
    /// <para><b>⚠ Nullability from the SOURCE, types from the CHANGE TABLE, and the split is MEASURED.</b>
    /// §1.2: <c>id INT NOT NULL PRIMARY KEY</c> is reported NULLABLE in the change table, so a reader that
    /// took nullability from there would report every column as optional. The TYPES must come from the change
    /// table because that is what the TVF actually returns.</para>
    /// <para><b>⚠ A captured column DROPPED from the source has no nullability row, and unknown becomes
    /// NULLABLE.</b> That is the safe direction: the change table keeps the column and new rows read NULL
    /// (§15.6's matrix), so claiming NOT NULL for it would be a claim the data violates.</para>
    /// <para><b>⚠ The captured set is authoritative, not the source's column list</b> (§15.7). A column ADDED
    /// to the source after capture began is NOT captured, so the TVF never returns it; describing the TVF
    /// rather than reading <c>sys.columns</c> is what keeps the declaration equal to what arrives.</para>
    /// </remarks>
    internal CdcChangesPlan CdcBindChanges(string source, string? captureInstance, bool commitTimestamp,
                                           byte[]? startingPosition, byte[]? endingPosition)
    {
        const string sql =
            "SET NOCOUNT ON; " +
            SqlServerCdcFunctions.HelpTableVar + " " +
            SqlServerCdcFunctions.FillHelpTableVar + " " +
            "SELECT CAST(m.capture_instance AS varchar(128)) AS capture_instance, " +
            "CAST(m.source_schema AS varchar(128)) AS source_schema, " +
            "CAST(m.source_table AS varchar(128)) AS source_table, " +
            "CAST(c.name AS varchar(128)) AS column_name, " +
            "CAST(c.is_nullable AS varchar(1)) AS is_nullable " +
            // A LEFT JOIN so a matched instance still yields a row when sys.columns answers nothing (the
            // source table dropped out from under a capture instance): the resolution must not vanish with
            // the nullability.
            "FROM @cdct m LEFT JOIN sys.columns c ON c.object_id = m.source_object_id " +
            "WHERE (m.capture_instance = @source OR (m.source_schema + '.' + m.source_table) = @source) " +
            "AND (@capture_instance IS NULL OR m.capture_instance = @capture_instance) " +
            "ORDER BY m.capture_instance, c.column_id;";

        var rows = ReadMetadataRows(sql, 5, new[]
        {
            new SqlParameter("@source", source),
            NullableParam("@capture_instance", captureInstance),
        });
        if (rows.Count == 0)
        {
            // ⚠ Which of the two it is decides where the reader goes next, so the message must not conflate
            // them. One extra round trip, only on the failure path — the same split cdc.min_position makes.
            var (enabled, database) = CdcDatabaseState();
            throw new ArgumentException(
                enabled
                    ? $"cdc.changes: '{source}' is not a captured table or capture instance in database "
                      + $"'{database}'"
                      + (captureInstance is null ? string.Empty : $" under capture instance '{captureInstance}'")
                      + ". SELECT * FROM <catalog>.cdc.tables() lists what is captured; "
                      + "<catalog>.cdc.enable('<schema>.<table>') captures it."
                    : "cdc.changes: change data capture is not enabled on database "
                      + $"'{database}'. Call <catalog>.cdc.enable_database() first, then cdc.enable(...).");
        }

        string instance = rows[0][0] ?? string.Empty;
        var nullability = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var matched = new List<string>();
        foreach (var row in rows)
        {
            string name = row[0] ?? string.Empty;
            if (!string.Equals(name, instance, StringComparison.Ordinal) && !matched.Contains(name))
            {
                matched.Add(name);
            }
            if (row[3] is { } column)
            {
                nullability[column] = row[4] != "0";
            }
        }
        if (matched.Count > 0)
        {
            // ⚠ REFUSED, not resolved by precedence. A table has at most TWO capture instances (§2.2) and
            // BOTH capture every change in the overlap window, so answering for one of them silently
            // DOUBLE-COUNTS or silently drops, depending which. Picking the newer would also be wrong at the
            // boundary, which is a whole slice (§15.12 item 7) and not a tie-break.
            matched.Insert(0, instance);
            throw new ArgumentException(
                $"cdc.changes: '{source}' matches {matched.Count} capture instances "
                + $"({string.Join(", ", matched)}). Both capture every change in their overlap window, so "
                + "reading one is not a default this function may pick for you - name it with "
                + "capture_instance := '<name>'. SELECT * FROM <catalog>.cdc.tables() lists them.");
        }

        // The DESCRIBE, over the statement this reader is about to run. `c.*` rather than a column list
        // because the captured column NAMES are exactly what is being learned here — and the TVF's own four
        // metadata columns come first, which the check below asserts rather than assumes.
        string tvf = "cdc." + Quote("fn_cdc_get_all_changes_" + instance);
        string describeSql = "SELECT " + CdcChangesSelectList(commitTimestamp, sourceColumns: null)
                             + " FROM " + tvf + "(@from_lsn, @to_lsn, @row_filter) AS c"
                             + (commitTimestamp ? CdcCommitTimeJoinSql : string.Empty);
        var described = DescribeQuery(describeSql, CdcDescribeParameters());
        if (described is null)
        {
            throw new InvalidOperationException(
                $"cdc.changes: SQL Server could not describe the change-table function for capture instance "
                + $"'{instance}' ({tvf}). If cdc.enable(...) ran inside an explicit transaction that has not "
                + "COMMITted, the function does not exist outside it yet - and a change captured by that same "
                + "transaction would not be readable either, because the capture job reads COMMITTED log "
                + "records. Commit first, then read.");
        }
        return CdcDeclare(described, instance, rows[0][1] ?? "dbo", rows[0][2] ?? source, nullability,
                          commitTimestamp, tvf, startingPosition, endingPosition);
    }

    /// <summary>
    /// Turns the described statement into the declared output schema plus the statement to execute.
    /// </summary>
    /// <remarks>
    /// ⚠ The four TVF metadata columns are asserted BY NAME rather than assumed. They are MEASURED to come
    /// first and in this order (§11 item 5 — exactly 8 columns, no <c>__$end_lsn</c> and no
    /// <c>__$command_id</c>), and everything after them is a captured source column. If a future SQL Server
    /// changed that, the alternative to this check is reading four metadata columns as DATA and shifting
    /// every source column by one — silently.
    /// </remarks>
    private static CdcChangesPlan CdcDeclare(Schema described, string instance, string sourceSchema,
                                             string sourceTable, IReadOnlyDictionary<string, bool> nullability,
                                             bool commitTimestamp, string tvf,
                                             byte[]? startingPosition, byte[]? endingPosition)
    {
        int meta = commitTimestamp ? 6 : 5;
        string[] expected = { "__$start_lsn", "__$seqval", "__$operation", "__$update_mask" };
        if (described.FieldsList.Count < meta + expected.Length)
        {
            throw new InvalidOperationException(
                $"cdc.changes: {tvf} described {described.FieldsList.Count} columns, too few to be a change "
                + "table function. Change data capture may be mid-reconfiguration; re-run the statement.");
        }
        for (int i = 0; i < expected.Length; i++)
        {
            string actual = described.FieldsList[meta + i].Name;
            if (!string.Equals(actual, expected[i], StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"cdc.changes: {tvf} column {meta + i + 1} is '{actual}' where SQL Server's change-table "
                    + $"function is documented to return '{expected[i]}'. Refusing rather than reading the "
                    + "columns at fixed offsets, which would shift every captured column silently.");
            }
        }

        var fields = new List<Field>(described.FieldsList.Count - expected.Length);
        for (int i = 0; i < meta; i++)
        {
            var f = described.FieldsList[i];
            // The five derived columns cannot be null: they come from __$start_lsn / __$seqval /
            // __$operation, which §1.2 MEASURED as NOT NULL in the change table. _commit_timestamp can be,
            // and is - it is a LEFT JOIN onto cdc.lsn_time_mapping.
            bool isCommitTime = commitTimestamp && i == meta - 1;
            fields.Add(new Field(f.Name, f.DataType, nullable: isCommitTime));
        }
        var sourceColumns = new List<string>(described.FieldsList.Count - meta - expected.Length);
        for (int i = meta + expected.Length; i < described.FieldsList.Count; i++)
        {
            var f = described.FieldsList[i];
            sourceColumns.Add(f.Name);
            // Unknown ⇒ NULLABLE. A captured column DROPPED from the source has no row here, and the safe
            // direction is the permissive one: the change table keeps such a column and new rows read NULL.
            fields.Add(new Field(f.Name, f.DataType,
                                 nullable: !nullability.TryGetValue(f.Name, out bool isNullable) || isNullable));
        }

        string sql = "SELECT " + CdcChangesSelectList(commitTimestamp, sourceColumns)
                     + " FROM " + tvf + "(@from_lsn, @to_lsn, @row_filter) AS c"
                     + (commitTimestamp ? CdcCommitTimeJoinSql : string.Empty)
                     + CdcCursorPredicateSql(startingPosition, endingPosition);
        return new CdcChangesPlan(instance, sourceSchema, sourceTable, new Schema(fields, metadata: null), sql,
                                  startingPosition, endingPosition);
    }

    /// <summary>
    /// ⚠ Emitted ONLY when <c>commit_timestamp := true</c>, and §11 item 2 is why. It serves exactly ONE
    /// output column, and MEASURED: DuckDB does NOT eliminate an unused LEFT JOIN — not even against a
    /// PRIMARY KEY — so emitting it unconditionally would make every caller pay a second full scan for a
    /// column most of them never select.
    /// </summary>
    private const string CdcCommitTimeJoinSql =
        " LEFT JOIN cdc.[lsn_time_mapping] AS m ON m.[start_lsn] = c.[__$start_lsn]";

    private static string CdcChangesSelectList(bool commitTimestamp, IReadOnlyList<string>? sourceColumns)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(CdcChangeTypeSql).Append(", ").Append(CdcPositionSql)
          .Append(", c.[__$start_lsn] AS [_commit_lsn]")
          .Append(", c.[__$seqval] AS [_seq_val]")
          .Append(", c.[__$operation] AS [_operation]");
        if (commitTimestamp)
        {
            sb.Append(", m.[tran_end_time] AS [_commit_timestamp]");
        }
        if (sourceColumns is null)
        {
            sb.Append(", c.*");
            return sb.ToString();
        }
        foreach (string column in sourceColumns)
        {
            sb.Append(", c.").Append(Quote(column));
        }
        return sb.ToString();
    }

    /// <summary>
    /// The strictly-after lower bound and the inclusive upper bound, as a WHERE clause over the TVF.
    /// </summary>
    /// <remarks>
    /// <para><b>⚠⚠ THIS IS WHY THE CURSOR CANNOT BE THE TVF'S OWN ARGUMENTS.</b> MEASURED from
    /// <c>OBJECT_DEFINITION</c> (§15.2): the function's window predicate is
    /// <c>__$start_lsn BETWEEN @from_lsn AND @to_lsn</c> — LSN granularity only, no seqval and no operation.
    /// So resuming exactly after one ROW has to be an extra predicate, and it is applied here rather than in
    /// C# because the change table's clustered index makes it a seek and because the rows then never cross
    /// the wire.</para>
    /// <para>⚠ A 10-byte bound is an LSN and a 21-byte bound is a full <c>_position</c>; both are legal on
    /// both sides. The 10-byte lower bound is EXCLUSIVE AT LSN GRANULARITY, which is exactly right for §3.4's
    /// documented idiom: the previous window ended at that LSN INCLUSIVE, so the next must start strictly
    /// after it. MEASURED, from a row's own 21-byte position: the three later rows come back and that row
    /// does not.</para>
    /// </remarks>
    private static string CdcCursorPredicateSql(byte[]? startingPosition, byte[]? endingPosition)
    {
        var terms = new List<string>(2);
        if (startingPosition is { Length: CdcChangesPlan.PositionBytes })
        {
            terms.Add("(c.[__$start_lsn] > @cur_lsn OR (c.[__$start_lsn] = @cur_lsn AND "
                      + "(c.[__$seqval] > @cur_seq OR (c.[__$seqval] = @cur_seq AND "
                      + "c.[__$operation] > @cur_op))))");
        }
        else if (startingPosition is not null)
        {
            terms.Add("c.[__$start_lsn] > @cur_lsn");
        }
        if (endingPosition is { Length: CdcChangesPlan.PositionBytes })
        {
            terms.Add("(c.[__$start_lsn] < @end_lsn OR (c.[__$start_lsn] = @end_lsn AND "
                      + "(c.[__$seqval] < @end_seq OR (c.[__$seqval] = @end_seq AND "
                      + "c.[__$operation] <= @end_op))))");
        }
        // ⚠ No ORDER BY, deliberately. The change table's clustered index is
        // (__$start_lsn, __$command_id, __$seqval, __$operation) — MEASURED, §15.2 — so ordering by our
        // 3-tuple would insert a real SORT rather than ride the index, and DuckDB does not promise to
        // preserve a table function's row order through its pipeline anyway. Every row carries its own
        // _position; `ORDER BY _position` is the documented and correct way to ask for order (§2.4).
        return terms.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", terms);
    }

    /// <summary>
    /// Placeholder values for the DESCRIBE. ⚠ They are never evaluated — <c>CommandBehavior.SchemaOnly</c>
    /// does not execute the statement — but the parameters must be DECLARED or SQL Server cannot compile it.
    /// (Which is also why they may be absurd: real bounds here would be a lie about a window nobody read.)
    /// </summary>
    private static SqlParameter[] CdcDescribeParameters() => new[]
    {
        CdcBinaryParam("@from_lsn", new byte[CdcChangesPlan.LsnBytes]),
        CdcBinaryParam("@to_lsn", new byte[CdcChangesPlan.LsnBytes]),
        new SqlParameter("@row_filter", System.Data.SqlDbType.NVarChar, 30)
        {
            Value = CdcChangesPlan.RowFilterAll,
        },
    };

    // binary(10), matching the TVF's own parameter types and the change table's columns. Declared rather
    // than inferred: SqlClient would infer varbinary sized to the value, and an equality predicate against
    // binary(10) is one implicit conversion away from a comparison nobody measured.
    private static SqlParameter CdcBinaryParam(string name, byte[] value) =>
        new(name, System.Data.SqlDbType.Binary, CdcChangesPlan.LsnBytes) { Value = value };

    /// <summary>
    /// Resolves the read window and runs THE PRE-CHECK — the highest-value line in this feature (§2.1).
    /// </summary>
    /// <remarks>
    /// <para><b>⚠⚠ WHAT IT EXISTS TO PREVENT.</b> MEASURED, three ways, all IDENTICAL: a <c>from_lsn</c>
    /// below the retention floor, a <c>to_lsn</c> above the capture watermark, and an inverted window all
    /// raise <c>Msg 313: An insufficient number of arguments were supplied for the procedure or function
    /// cdc.fn_cdc_get_all_changes_ ... .</c> — with THREE arguments supplied. And the <c>" ... "</c> is the
    /// LITERAL name of a placeholder function SQL Server calls deliberately to force that error (§15.3), so
    /// the message is the same for every capture instance and every cause and can NEVER be attributed. A
    /// pipeline that has lost data is told to look at its call site.</para>
    /// <para><b>⚠⚠ A NULL FLOOR IS NOT "NO LOWER BOUND".</b> MEASURED (see <see cref="CdcMinLsn"/>): for an
    /// instance enabled in a database whose capture job is already running, <c>fn_cdc_get_min_lsn</c> returns
    /// NULL for up to a polling interval while <c>cdc.change_tables.start_lsn</c> is already set. Passing the
    /// window through on a NULL floor is exactly how the misleading 313 gets reached; and substituting
    /// <c>start_lsn</c> would ASSERT a floor the engine declined to state.</para>
    /// <para><b>⚠ A NULL watermark with no explicit upper bound is ZERO ROWS, not an error</b> — it is the
    /// ordinary state of a freshly enabled instance whose capture job has not scanned yet (§15.7's "priming
    /// the pump"). But when the CALLER supplied an upper bound it becomes an error, because a bound that
    /// cannot exist is a question worth answering rather than silently emptying.</para>
    /// </remarks>
    internal CdcWindow CdcResolveWindow(CdcChangesPlan plan)
    {
        const string sql =
            "SET NOCOUNT ON; " +
            "DECLARE @en varchar(1) = CASE WHEN " + SqlServerCdcFunctions.CdcEnabledPredicate
            + " THEN '1' ELSE '0' END; " +
            // ⚠ IF/ELSE rather than CASE, for the reason CdcMaxLsn records: with CDC disabled these functions
            // raise 208 naming cdc.lsn_time_mapping, and SQL Server does not guarantee an unevaluated CASE
            // branch. Both branches project the same three varchar columns.
            "IF @en = '1' " +
            "  SELECT @en AS enabled, CONVERT(varchar(30), sys.fn_cdc_get_min_lsn(@inst), 1) AS min_lsn, " +
            "         CONVERT(varchar(30), sys.fn_cdc_get_max_lsn(), 1) AS max_lsn; " +
            "ELSE SELECT @en AS enabled, CAST(NULL AS varchar(30)) AS min_lsn, " +
            "            CAST(NULL AS varchar(30)) AS max_lsn;";

        // readYourWrites: a capture instance enabled earlier in THIS transaction must be visible here, and
        // this is a short metadata read that holds no reader open. ⚠ The streaming read below deliberately
        // does NOT do that - see CdcExecuteChanges.
        var rows = ReadMetadataRows(sql, 3, new[] { new SqlParameter("@inst", plan.CaptureInstance) });
        if (rows.Count == 0 || rows[0][0] != "1")
        {
            throw new InvalidOperationException(
                "cdc.changes: change data capture is no longer enabled on this database. It was when this "
                + "statement was bound, so something disabled it in between - cdc.health() reports the "
                + "current state.");
        }
        byte[]? minLsn = SqlServerCdcFunctions.ParseHex(rows[0][1]);
        byte[]? maxLsn = SqlServerCdcFunctions.ParseHex(rows[0][2]);
        if (minLsn is null)
        {
            throw new InvalidOperationException(
                $"cdc.changes: the retention floor for capture instance '{plan.CaptureInstance}' is not "
                + "established yet - SQL Server answered NULL. Either the capture job has not scanned this "
                + "instance since it was enabled (retry in one polling interval; cdc.health() reports it), or "
                + "the instance no longer exists. It is deliberately NOT treated as 'no lower bound': that "
                + "would hand the window to SQL Server and get back an error naming neither.");
        }

        byte[] toLsn;
        if (plan.EndingPosition is { } ending)
        {
            if (maxLsn is null)
            {
                throw new InvalidOperationException(
                    "cdc.changes: ending_position was supplied but nothing has been captured yet - "
                    + "sys.fn_cdc_get_max_lsn() is NULL, which means the capture job has not scanned this "
                    + "database since capture was enabled. cdc.health() reports the polling interval; "
                    + "cdc.capture_now() forces a scan.");
            }
            toLsn = CdcChangesPlan.LsnOf(ending);
            if (CdcChangesPlan.CompareLsn(toLsn, maxLsn) > 0)
            {
                throw new InvalidOperationException(
                    $"cdc.changes: ending_position {CdcChangesPlan.Hex(toLsn)} is above the capture "
                    + $"watermark {CdcChangesPlan.Hex(maxLsn)} - those changes have not been captured yet. "
                    + "Take the window end from cdc.max_position() rather than from a clock or a guess, and "
                    + "take it BEFORE the read (docs: the two-step cursor idiom).");
            }
        }
        else if (maxLsn is null)
        {
            // The ordinary state of a freshly enabled instance. Zero rows, no error, no round trip.
            Log.LogDebug("cdc changes {Source} ({Instance}): no capture watermark yet - empty window",
                         plan.SourceName, plan.CaptureInstance);
            return CdcWindow.Empty;
        }
        else
        {
            toLsn = maxLsn;
        }

        byte[] fromLsn = plan.StartingPosition is { } starting ? CdcChangesPlan.LsnOf(starting) : minLsn;
        if (CdcChangesPlan.CompareLsn(fromLsn, minLsn) < 0)
        {
            throw new InvalidOperationException(
                $"cdc.changes: starting_position {CdcChangesPlan.Hex(fromLsn)} is BELOW the retention floor "
                + $"{CdcChangesPlan.Hex(minLsn)} of capture instance '{plan.CaptureInstance}'. The changes "
                + "between them have been removed by the cleanup job, so THIS READ WOULD HAVE SILENTLY "
                + "SKIPPED THEM. Re-snapshot the table and restart from cdc.max_position(); cdc.health() "
                + "reports the retention setting, and cdc.min_position('" + plan.CaptureInstance
                + "') the current floor.");
        }
        if (CdcChangesPlan.CompareLsn(fromLsn, toLsn) > 0)
        {
            // ⚠ NOT an error: an inverted window is EMPTY, and the TVF answers one with the unattributable
            // 313 (MEASURED), so declining to call it is what keeps that error off a caller who merely asked
            // for a window with nothing in it.
            //
            // ⚠⚠ AND THE CASE IS NARROWER THAN IT LOOKS - the first version of this comment called it the
            // ordinary "polling consumer has caught up" path, and a SURVIVING MUTANT is what showed
            // otherwise. A caught-up consumer passes its cursor with the end DEFAULTED, so from == to (the
            // watermark) rather than from > to, and those rows are excluded by the exclusive predicate in
            // the SQL - never by this branch. Reaching here needs an EXPLICIT ending_position BELOW the
            // cursor: a stale stored watermark, or a backfill window already behind. It is gated with
            // exactly that shape, because nothing else produces it.
            Log.LogDebug("cdc changes {Source} ({Instance}): window is inverted ({From} > {To}) - empty",
                         plan.SourceName, plan.CaptureInstance, CdcChangesPlan.Hex(fromLsn),
                         CdcChangesPlan.Hex(toLsn));
            return CdcWindow.Empty;
        }
        Log.LogDebug("cdc changes {Source} ({Instance}): window {From} .. {To}", plan.SourceName,
                     plan.CaptureInstance, CdcChangesPlan.Hex(fromLsn), CdcChangesPlan.Hex(toLsn));
        return new CdcWindow(fromLsn, toLsn, plan.StartingPosition, plan.EndingPosition);
    }

    /// <summary>Runs the change read and streams it.</summary>
    /// <remarks>
    /// <para><b>⚠ POOLED, not the transaction's pinned connection — and unlike every other read on this
    /// surface that is the CORRECT answer rather than a compromise.</b> Read-your-writes buys a change reader
    /// nothing: the capture job populates the change table ASYNCHRONOUSLY from COMMITTED log records, so a
    /// transaction's own uncommitted writes are not there to be seen, on any connection. What routing onto
    /// the pinned connection WOULD do is hold a long streaming reader open on the write connection, which is
    /// the outstanding-result-set hazard (error 595 on a no-MARS engine). The window resolution above
    /// deliberately goes the other way, because a capture instance enabled in this transaction IS visible to
    /// it.</para>
    /// <para>⚠ Fresh <see cref="SqlParameter"/> objects on every call: a SqlParameter belongs to at most one
    /// command's collection, and this plan may be executed more than once (a prepared statement).</para>
    /// <para>⚠ THE PARAMETER SET HERE AND THE PREDICATE IN <see cref="CdcCursorPredicateSql"/> ARE ONE
    /// DECISION SPLIT ACROSS TWO PLACES — bind writes the WHERE clause, execute binds the values — and they
    /// key on the same bound LENGTHS. They agree today because the window carries the plan's own positions
    /// through unchanged; a future slice that CLAMPS or rewrites a bound must move both. It fails loudly if
    /// they diverge ("must declare the scalar variable @cur_seq"), not silently.</para>
    /// </remarks>
    internal IArrowArrayStream CdcExecuteChanges(CdcChangesPlan plan, CdcWindow window)
    {
        var parameters = new List<SqlParameter>(8)
        {
            CdcBinaryParam("@from_lsn", window.FromLsn),
            CdcBinaryParam("@to_lsn", window.ToLsn),
            new("@row_filter", System.Data.SqlDbType.NVarChar, 30) { Value = CdcChangesPlan.RowFilterAll },
        };
        if (window.StartingPosition is { } starting)
        {
            parameters.Add(CdcBinaryParam("@cur_lsn", CdcChangesPlan.LsnOf(starting)));
            if (starting.Length == CdcChangesPlan.PositionBytes)
            {
                parameters.Add(CdcBinaryParam("@cur_seq", CdcChangesPlan.SeqOf(starting)));
                parameters.Add(new SqlParameter("@cur_op", System.Data.SqlDbType.Int)
                {
                    Value = CdcChangesPlan.OpOf(starting),
                });
            }
        }
        if (window.EndingPosition is { Length: CdcChangesPlan.PositionBytes } ending)
        {
            parameters.Add(CdcBinaryParam("@end_lsn", CdcChangesPlan.LsnOf(ending)));
            parameters.Add(CdcBinaryParam("@end_seq", CdcChangesPlan.SeqOf(ending)));
            parameters.Add(new SqlParameter("@end_op", System.Data.SqlDbType.Int)
            {
                Value = CdcChangesPlan.OpOf(ending),
            });
        }
        return ExecuteQuery(plan.Sql, parameters, readYourWrites: false);
    }
}
