using System;
using System.Collections.Generic;
using Apache.Arrow;
using Apache.Arrow.Types;
using Microsoft.Data.SqlClient;

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
            "CAST(index_column_list AS varchar(max)), CAST(captured_column_list AS varchar(max)) " +
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
}
