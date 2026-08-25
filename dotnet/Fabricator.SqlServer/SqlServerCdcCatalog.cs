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
            // ⚠⚠ TWO INSTANCES OF ONE TABLE NOW HAVE AN ANSWER, and it is the answer cdc.changes acts on
            // (slice 7): the union's readable range starts at the OLDER instance's floor, because the older
            // instance is what covers everything below the boundary. Reporting the MINIMUM says exactly that.
            // ⚠ It was a refusal until slice 7, and rightly so — while cdc.changes refused the same source,
            // "the floor of what?" had no answer. Now it does, and leaving the refusal here would mean a
            // caller who follows the retention error's own advice with the string they passed to
            // cdc.changes gets told to go away.
            if (!mixed && rows.Count == 2 && rows[0][1] == "0")
            {
                byte[]? first = SqlServerCdcFunctions.ParseHex(rows[0][2]);
                byte[]? second = SqlServerCdcFunctions.ParseHex(rows[1][2]);
                // ⚠ UNKNOWN WINS OVER MIN. A NULL floor is transiently unknowable (§1.6a), not absent, and
                // reporting the other instance's floor would ASSERT a lower bound above the true one — the
                // same substitution CdcMinLsn's own remarks refuse to make for one instance.
                if (first is null || second is null)
                {
                    return null;
                }
                return CdcChangesPlan.CompareLsn(first, second) <= 0 ? first : second;
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
                + ". Name the instance explicitly; SELECT * FROM <catalog>.cdc.tables() lists them.");
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
            "CAST(captured_column_list AS varchar(max)), " +
            SqlServerCdcFunctions.OwnerLookupSql + " " +
            // Aliased `c` because the owner lookup correlates on c.capture_instance.
            "FROM @cdct AS c ORDER BY source_schema, source_table, capture_instance;";
        var rows = ReadMetadataRows(sql, 14);

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
        var owner = new StringArray.Builder();

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
            owner.Append(row[13]);
        }
        return new RecordBatch(CdcTablesFunction.Columns, new IArrowArray[]
        {
            schemas.Build(), tables.Build(), instances.Build(), startLsn.Build(), endLsn.Build(),
            net.Build(), dropPending.Build(), role.Build(), index.Build(), filegroup.Build(),
            created.Build(), indexCols.Build(), capturedCols.Build(), owner.Build(),
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
    /// <c>sys.sp_cdc_enable_table</c> for one source table, with a GENERATED capture-instance name and an
    /// extended property marking the instance as ours.
    /// </summary>
    /// <remarks>
    /// <para>Every caller-supplied value crosses as a PARAMETER, never spliced text — these are identifiers
    /// and a column list a user types, which is the one place a wrapper like this must not get clever.</para>
    /// <para><b>⚠⚠ THE IDEMPOTENCE CHECK MOVED FROM THE INSTANCE TO THE TABLE, and that is a correctness fix
    /// rather than ergonomics.</b> It used to key on the capture INSTANCE, on the reasoning that a table
    /// legitimately has two and refusing the second would be wrong. True of an EXPLICITLY named second
    /// instance — which is still how you ask for one — but fatal as a DEFAULT: a bare <c>cdc.enable</c> that
    /// silently added a second instance would make <c>cdc.changes('&lt;that table&gt;')</c> AMBIGUOUS, and the
    /// reader refuses an ambiguous source rather than picking one (§2.2: both instances capture every change
    /// in their overlap window, so either answer is wrong). So the default enable's question is "is this
    /// table captured?", and the explicit one's is still "does this instance exist?".</para>
    /// <para><b>⚠ It reports what EXISTS, not what it would have created.</b> With opaque generated names a
    /// bare "already captured" would leave the caller with no way to name the instance they now have, so the
    /// report row carries the existing instance — whoever created it.</para>
    /// <para><b>⚠⚠ THE MARKER AND THE ENABLE ARE ONE TRANSACTION, and all three states are MEASURED
    /// (docs §16b).</b> <c>sp_cdc_enable_table</c> and <c>sp_addextendedproperty</c> are two statements, and
    /// §15.5's warning — a failed marker leaves an instance we own but cannot recognise — is real:</para>
    /// <list type="bullet">
    ///   <item>autocommit, plain batch: the enable <b>SURVIVES</b> a failed marker write, unmarked. The
    ///     outcome to avoid.</item>
    ///   <item>autocommit + our own <c>BEGIN/COMMIT</c>: the enable is <b>ROLLED BACK</b>. Atomic — what
    ///     this does.</item>
    ///   <item>inside an AMBIENT transaction, via a savepoint: <b>unusable</b>. The marker's error kills the
    ///     whole transaction before a <c>CATCH</c> can act (<c>XACT_STATE() = 0</c>, <c>@@TRANCOUNT = 0</c>),
    ///     so there is nothing left to roll back TO. Hence <c>IF @@TRANCOUNT = 0</c>: we open a transaction
    ///     only when we are the outermost, and when nested we inherit one whose destruction is at least
    ///     LOUD rather than leaving an unmarked instance behind.</item>
    /// </list>
    /// <para>⚠ The marker's VALUE is the resolved <c>schema.table</c> rather than the raw argument the caller
    /// typed. §15.5 says "the name the user typed"; a caller may type a bare <c>orders</c>, and a listing
    /// wants the qualified name. It is PROVENANCE either way — <b>never the resolution</b>, because a table
    /// rename leaves it stale while <c>source_object_id</c> follows (MEASURED, §15.6).</para>
    /// </remarks>
    internal RecordBatch CdcEnableTable(string schema, string table, string? captureInstance,
                                       string? columns, string? role, string? index, string? filegroup,
                                       bool net)
    {
        const string sql =
            "SET NOCOUNT ON; " +
            "DECLARE @inst sysname = ISNULL(@capture_instance, @generated); " +
            "IF NOT " + SqlServerCdcFunctions.CdcEnabledPredicate + " " +
            "  THROW 50001, 'cdc.enable: change data capture is not enabled on this database - call " +
            "cdc.enable_database() first', 1; " +
            SqlServerCdcFunctions.HelpTableVar + " " +
            SqlServerCdcFunctions.FillHelpTableVar + " " +
            // The existing instance, if any: keyed on the TABLE for a default enable and on the NAME for an
            // explicit one. See the remarks - the difference is what keeps cdc.changes unambiguous.
            "DECLARE @existing sysname = NULL; " +
            "IF @capture_instance IS NULL " +
            "  SELECT TOP 1 @existing = capture_instance FROM @cdct " +
            "   WHERE source_schema = @schema AND source_table = @table ORDER BY capture_instance; " +
            "ELSE " +
            "  SELECT TOP 1 @existing = capture_instance FROM @cdct WHERE capture_instance = @inst; " +
            "IF @existing IS NOT NULL " +
            "  SELECT CAST(@schema + N'.' + @table + N' (' + @existing + N')' AS varchar(400)) AS target, " +
            "         '0' AS changed, " +
            "         CAST(CASE WHEN @capture_instance IS NULL " +
            "                   THEN 'this table is already captured by that instance' " +
            "                   ELSE 'this capture instance already exists' END AS varchar(400)) AS detail; " +
            "ELSE BEGIN " +
            "  DECLARE @own bit = CASE WHEN @@TRANCOUNT = 0 THEN 1 ELSE 0 END; " +
            "  IF @own = 1 BEGIN TRANSACTION; " +
            "  EXEC sys.sp_cdc_enable_table @source_schema = @schema, @source_name = @table, " +
            "       @capture_instance = @inst, @captured_column_list = @columns, @role_name = @role, " +
            "       @index_name = @index, @filegroup_name = @filegroup, @supports_net_changes = @net; " +
            // The ownership marker. On the TVF rather than the change table because the TVF is the object the
            // reader uses and the only is_ms_shipped = 0 object the enable creates (§15.2).
            "  DECLARE @tvf sysname = N'fn_cdc_get_all_changes_' + @inst; " +
            "  EXEC sys.sp_addextendedproperty @name = N'" + SqlServerCdcFunctions.OwnerProperty + "', " +
            "       @value = @label, @level0type = N'SCHEMA', @level0name = N'cdc', " +
            "       @level1type = N'FUNCTION', @level1name = @tvf; " +
            "  IF @own = 1 COMMIT TRANSACTION; " +
            "  SELECT CAST(@schema + N'.' + @table + N' (' + @inst + N')' AS varchar(400)) AS target, " +
            "         '1' AS changed, " +
            "         CAST('capture instance created; a change table and two table-valued functions now " +
            "exist' AS varchar(400)) AS detail; " +
            "END";
        string generated = SqlServerCdcSetup.GenerateCaptureInstance(schema, table);
        return CdcReportFrom(ReadMetadataRows(sql, 3, new[]
        {
            new SqlParameter("@schema", schema),
            new SqlParameter("@table", table),
            NullableParam("@capture_instance", captureInstance),
            new SqlParameter("@generated", generated),
            new SqlParameter("@label", schema + "." + table),
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

    /// <summary>
    /// The metadata select list every read emits, written ONCE over three caller-supplied expressions.
    /// </summary>
    /// <remarks>
    /// <para><b>⚠⚠ THE PARAMETERISATION IS THE SAFETY PROPERTY, not a tidy-up.</b> Two statements are
    /// described: the REAL one over a capture instance's TVF, and — for <c>enable := true</c> over a table
    /// that is not captured yet (§15.7) — a SYNTHETIC one over the SOURCE table, whose metadata columns must
    /// declare EXACTLY the same Arrow types or the deferred declaration would be a lie DuckDB acts on.
    /// Writing the CASE and the concatenation once makes them the same expression rather than two that agree
    /// today. MEASURED: both describe as <c>varchar(16)</c>, <c>binary(21)</c>, <c>binary(10)</c>,
    /// <c>binary(10)</c>, <c>int</c>, <c>datetime</c>.</para>
    /// <para><b><c>_position</c> is <c>start_lsn ‖ seqval ‖ operation</c> as ONE 21-byte value</b> (§2.4),
    /// whose lexicographic order IS the change order — so <c>ORDER BY _position</c> replays correctly and
    /// <c>max(_position)</c> resumes correctly, with the consumer's cursor one BLOB rather than three columns
    /// and the §1.3 predicate. MEASURED: the concatenation is 21 bytes and the operation byte is the LOW byte
    /// of the <c>int</c> (<c>0x…02</c> insert, <c>0x…04</c> update after-image, <c>0x…01</c> delete).
    /// ⚠ <c>__$command_id</c> is deliberately NOT in it: the TVF does not return that column at all (§11
    /// item 5, MEASURED — exactly 8 columns), and within one <c>__$start_lsn</c> the seqvals already order
    /// statements the way command_id does. The tuple is COMPLETE without it.</para>
    /// <para>⚠ The CASE has an ELSE rather than an implicit NULL. The five codes are SQL Server's documented
    /// set, so the branch is unreachable today — but an unknown operation surfacing as its own NUMBER is
    /// recoverable, while one surfacing as a NULL <c>_change_type</c> is a row a consumer mis-handles
    /// silently.</para>
    /// </remarks>
    private static string CdcMetadataSelectList(string op, string lsn, string seq, string? commitTime,
                                               string? updateMask, string instParam = "@inst_name",
                                               string? changeType = null, string? position = null)
    {
        var sb = new System.Text.StringBuilder();
        if (changeType is not null)
        {
            sb.Append(changeType).Append(" AS [_change_type]");
        }
        else
        {
            sb.Append("CASE ").Append(op)
              .Append(" WHEN 1 THEN 'delete' WHEN 2 THEN 'insert' WHEN 3 THEN 'update_preimage' ")
              .Append("WHEN 4 THEN 'update_postimage' WHEN 5 THEN 'upsert' ELSE CONVERT(varchar(16), ")
              .Append(op).Append(") END AS [_change_type]");
        }
        if (position is not null)
        {
            sb.Append(", ").Append(position).Append(" AS [_position]");
        }
        else
        {
            sb.Append(", ").Append(lsn).Append(" + ").Append(seq).Append(" + CONVERT(binary(1), ").Append(op)
              .Append(") AS [_position]");
        }
        sb.Append(", ").Append(lsn).Append(" AS [_commit_lsn]")
          .Append(", ").Append(seq).Append(" AS [_seq_val]")
          .Append(", ").Append(op).Append(" AS [_operation]")
          // ⚠ A PARAMETER, not the instance name spliced in — it is a sysname from server metadata, so
          // splicing would be safe in practice and wrong in principle, and this costs nothing.
          .Append(", CONVERT(varchar(128), ").Append(instParam).Append(") AS [_capture_instance]");
        if (commitTime is not null)
        {
            sb.Append(", ").Append(commitTime).Append(" AS [_commit_timestamp]");
        }
        if (updateMask is not null)
        {
            sb.Append(", ").Append(updateMask).Append(" AS [_update_mask]");
        }
        return sb.ToString();
    }

    /// <summary>The number of metadata columns a read emits before the captured source columns.</summary>
    /// <remarks>
    /// ⚠ Two OPTIONAL columns now, and they are appended in a fixed order — <c>_commit_timestamp</c> then
    /// <c>_update_mask</c> — so an index into the block is a function of which options are on rather than of
    /// the order the caller wrote them. <see cref="CdcMetadataNullable"/> is the one place that has to know.
    /// </remarks>
    private static int CdcMetadataColumnCount(bool commitTimestamp, bool updateMask) =>
        6 + (commitTimestamp ? 1 : 0) + (updateMask ? 1 : 0);

    /// <summary>The metadata list over a capture instance's TVF, aliased <c>c</c> (and <c>m</c> for the join).</summary>
    private static string CdcRealMetadataSelectList(bool commitTimestamp, bool updateMask,
                                                    string instParam = "@inst_name") =>
        CdcMetadataSelectList("c.[__$operation]", "c.[__$start_lsn]", "c.[__$seqval]",
                              commitTimestamp ? "m.[tran_end_time]" : null,
                              updateMask ? "c.[__$update_mask]" : null, instParam);

    /// <summary>
    /// The metadata list over NOTHING — literals of the same types — for describing a not-yet-captured
    /// table's output schema (§15.7).
    /// </summary>
    private static string CdcSyntheticMetadataSelectList(bool commitTimestamp, bool updateMask) =>
        CdcMetadataSelectList("CONVERT(int, 0)", "CONVERT(binary(10), 0)", "CONVERT(binary(10), 0)",
                              commitTimestamp ? "CONVERT(datetime, NULL)" : null,
                              updateMask ? UpdateMaskNullLiteral : null);

    /// <summary>
    /// The <c>_update_mask</c> placeholder for a row that has no update mask: a snapshot row, and the
    /// literals a not-yet-captured table is described from.
    /// </summary>
    /// <remarks>
    /// <b>⚠ THE WIDTH IS LOAD-BEARING and it is the same trap slice 8 measured twice.</b> The change table's
    /// column is <c>varbinary(128)</c>; a bare <c>NULL</c> here would describe as <c>int</c> and a
    /// differently-sized <c>varbinary</c> would describe as itself, and either makes the DECLARED schema
    /// differ from the change read's — at which point the arrival check refuses every row of the leg. It is
    /// restated as an explicit CONVERT for exactly that reason, verified column by column through
    /// <c>sp_describe_first_result_set</c> rather than assumed from the documentation.
    /// </remarks>
    private const string UpdateMaskNullLiteral = "CONVERT(varbinary(128), NULL)";

    /// <summary>
    /// The metadata list of a SNAPSHOT row: literals, over the SOURCE table rather than a change table.
    /// </summary>
    /// <remarks>
    /// <para><b>The decision table, and every column is individually true rather than convenient:</b></para>
    /// <list type="bullet">
    ///   <item><c>_change_type</c> = <b><c>insert</c></b> and <c>_operation</c> = <b>2</b>. §3.3 chose the
    ///     Delta change-feed vocabulary so a consumer that already handles a Delta CDF handles this one
    ///     unchanged — and Delta's own answer to "how do you spell the baseline" is <c>insert</c> (reading a
    ///     CDF from version 0 returns exactly that). <c>_operation</c> is the raw form of the same claim, so
    ///     saying 2 is consistency rather than a second vocabulary to learn.</item>
    ///   <item><c>_commit_lsn</c> and <c>_seq_val</c> = <b>NULL</b>. They are per-CHANGE facts and a snapshot
    ///     row is STATE, not an event: it has no commit and no sequence within one. Ordering still works,
    ///     because <c>ORDER BY _position</c> is the documented key (§2.4) and every snapshot row's position
    ///     sorts below every change row's.</item>
    ///   <item><c>_capture_instance</c> = <b>NULL</b>, and <b>that is the DISCRIMINATOR</b>: this row came
    ///     from the source table, not from a capture instance. A change row's value is a parameter that is
    ///     never null, so <c>_capture_instance IS NULL</c> is exactly "this is a baseline row". One rule, no
    ///     new column, and it re-uses the column §18.2 shipped to make provenance readable.</item>
    ///   <item><c>_commit_timestamp</c> = <b>NULL</b> — no commit, so no commit time.</item>
    /// </list>
    /// <para><b>⚠⚠ THE TWO CONVERTS ARE LOAD-BEARING, and MEASURED: without them the schema DIFFERS from the
    /// change read's and the arrival check refuses every snapshot.</b> A CASE over a CONSTANT operation is
    /// folded, so it describes as <c>varchar(6)</c> (the width of <c>'insert'</c>) where the real list gives
    /// <c>varchar(16)</c>; and <c>binary + binary</c> describes as <c>varbinary(21)</c>, not
    /// <c>binary(21)</c>. Both are re-stated explicitly so the two lists describe identically — verified
    /// column by column through <c>sp_describe_first_result_set</c>.</para>
    /// <para>⚠ It goes through <see cref="CdcMetadataSelectList"/> rather than being written out, so the
    /// column NAMES, ORDER and COUNT still come from one place. Only two of the seven expressions differ,
    /// which is the part that genuinely cannot be shared.</para>
    /// </remarks>
    /// <summary>
    /// Whether metadata column <paramref name="index"/> can be NULL in this read's output.
    /// </summary>
    /// <remarks>
    /// <para>⚠ The five derived columns cannot be null in a CHANGE row: they come from
    /// <c>__$start_lsn</c> / <c>__$seqval</c> / <c>__$operation</c>, which §1.2 MEASURED as NOT NULL in the
    /// change table. <c>_commit_timestamp</c> can be, and is — it is a LEFT JOIN onto
    /// <c>cdc.lsn_time_mapping</c>.</para>
    /// <para><b>⚠⚠ A SNAPSHOT LEG WIDENS THREE OF THEM, and the declaration has to say so.</b> A snapshot row
    /// carries NULL in <c>_commit_lsn</c>, <c>_seq_val</c> and <c>_capture_instance</c> — it is STATE rather
    /// than an event, so it has no commit, no sequence and no capture instance. Declaring them NOT NULL and
    /// then emitting NULLs would be a false contract at the Arrow boundary. It survives today only because
    /// DuckDB DROPS a table function's declared nullability (§16.4 item 2), which makes it invisible rather
    /// than harmless — exactly the kind of claim this file exists to keep honest.</para>
    /// </remarks>
    private static bool CdcMetadataNullable(int index, int meta, bool commitTimestamp, bool updateMask,
                                            bool hasSnapshot)
    {
        // ⚠ Indexed from the FRONT, never from `meta - 1`. The block used to have one optional trailing
        // column, so "the last one is the timestamp" held; with two, that shortcut silently declares the
        // MASK's nullability for the TIMESTAMP whenever both are on.
        if (commitTimestamp && index == 6)
        {
            return true;
        }
        // ⚠⚠ NOT NULL IN A CHANGE ROW, and this is MEASURED rather than reasoned — my first version of this
        // line declared it nullable always, on the plausible story that an insert and a delete have no
        // "columns changed" to report. SQL Server's answer is the opposite: it reports ALL of them. Measured
        // on a four-column table, `all update old`, one row per operation: insert 0x0F, delete 0x0F, and the
        // two update images carrying exactly the touched column's bit (0x02 = ordinal 2, 0x04 = ordinal 3).
        // Never NULL. So the mask behaves like its three neighbours below — a per-CHANGE fact that only a
        // SNAPSHOT row lacks — and it is declared the same way.
        // 2 = _commit_lsn, 3 = _seq_val, 5 = _capture_instance, then the mask when both options are on.
        return hasSnapshot
               && (index == 2 || index == 3 || index == 5
                   || (updateMask && index == 6 + (commitTimestamp ? 1 : 0)));
    }

    private static string CdcSnapshotMetadataSelectList(bool commitTimestamp, bool updateMask) =>
        CdcMetadataSelectList("CONVERT(int, 2)", "CONVERT(binary(10), NULL)", "CONVERT(binary(10), NULL)",
                              commitTimestamp ? "CONVERT(datetime, NULL)" : null,
                              // ⚠ NULL rather than an all-bits-set mask, and the two are NOT interchangeable:
                              // a mask says WHICH COLUMNS AN UPDATE TOUCHED, and a baseline row is not an
                              // update. All-bits-set would read as "this update changed every column", which
                              // is a claim about an event that never happened.
                              updateMask ? UpdateMaskNullLiteral : null,
                              instParam: "NULL",
                              changeType: "CONVERT(varchar(16), 'insert')",
                              position: "CONVERT(binary(21), @handoff)");

    /// <summary>
    /// The snapshot leg's statement: the metadata literals plus the DECLARED source columns, read from the
    /// source table itself.
    /// </summary>
    /// <remarks>
    /// <para>⚠ No locking hint here. The <c>TABLOCK, HOLDLOCK</c> of §5.1 belongs to the OTHER connection's
    /// pin statement and is released before this one runs; this read is the one that takes minutes, and
    /// holding a table lock across it is precisely what §5.2 says the protocol exists to avoid.</para>
    /// <para>⚠ The columns are named rather than <c>s.*</c>: the declaration is the CAPTURED set (§15.7), and
    /// a column ADDED to the source after capture began must not appear here or the snapshot half would be
    /// one column wider than the changes half.</para>
    /// </remarks>
    private static string CdcSnapshotSql(string schema, string table, IReadOnlyList<string> columns,
                                         bool commitTimestamp, bool updateMask)
    {
        var sb = new System.Text.StringBuilder("SELECT ");
        sb.Append(CdcSnapshotMetadataSelectList(commitTimestamp, updateMask));
        foreach (string column in columns)
        {
            sb.Append(", s.").Append(Quote(column));
        }
        sb.Append(" FROM ").Append(Quote(schema)).Append('.').Append(Quote(table)).Append(" AS s");
        return sb.ToString();
    }

    /// <summary>
    /// Refuses a snapshot of a table one of whose CAPTURED columns no longer exists on the source.
    /// </summary>
    /// <remarks>
    /// <para><b>⚠ The case is real, not defensive: a DROP COLUMN on a captured table is permitted and leaves
    /// the change table's column in place</b> (§15.6's matrix), reading NULL from that point. So the
    /// declaration — which is the CAPTURED set — can name a column <c>SELECT</c> cannot resolve, and the
    /// snapshot statement would fail with <c>Msg 207 Invalid column name</c> at execute, naming a column the
    /// caller never wrote.</para>
    /// <para><b>⚠ Refusing beats NULL-filling here, and the reason is the one §19.3 measured.</b> A bare
    /// <c>NULL</c> takes its type from the other branch of a UNION — the snapshot statement has no other
    /// branch, so a NULL-filled column would describe as <c>int</c> and fail the arrival check anyway. Filling
    /// it properly would mean rendering the captured column's SQL type by hand, which is the type table this
    /// feature has twice avoided building. A sentence naming the column is worth more than either.</para>
    /// <para>⚠ It also fires for the OLD-only columns of a two-instance read, which is the same situation
    /// arriving by another route: the older instance captures a column the newer one does not, and the usual
    /// reason is that it was dropped.</para>
    /// </remarks>
    private static void CdcEnsureSnapshotColumnsExist(string include, string sourceName,
                                                      IReadOnlyList<string> columns,
                                                      IReadOnlyDictionary<string, bool> presentOnSource)
    {
        var missing = new List<string>();
        foreach (string column in columns)
        {
            if (!presentOnSource.ContainsKey(column))
            {
                missing.Add(column);
            }
        }
        if (missing.Count == 0)
        {
            return;
        }
        throw new ArgumentException(
            $"cdc.changes: include := '{include}' cannot snapshot '{sourceName}' - "
            + (missing.Count == 1 ? $"the captured column '{missing[0]}' no longer exists on the source table"
                                  : $"{missing.Count} captured columns no longer exist on the source table "
                                    + $"({string.Join(", ", missing)})")
            + ". A snapshot reads the SOURCE, so a column that was dropped from it has no value to read, "
            + "while the change table keeps the column and reads NULL for it. Read include := 'changes' "
            + "instead, or capture the table afresh so the captured set matches the source.");
    }

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
                                           bool enable, string onSchemaChange, string include, string images,
                                           byte[]? startingPosition, byte[]? endingPosition,
                                           DateTime? startingTimestamp = null,
                                           DateTime? endingTimestamp = null)
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
            // ⚠ ORDERED BY start_lsn, NOT by name. When two instances match, the FIRST is the older one and
            // that ordering is the contract the union read rests on (§19): the boundary IS the newer
            // instance's start_lsn. create_date breaks the tie the cleanup job can produce by raising both
            // floors onto the same value; the name breaks a tie nothing can produce, so the order is total.
            "ORDER BY m.start_lsn, m.create_date, m.capture_instance, c.column_id;";

        var rows = ReadMetadataRows(sql, 5, new[]
        {
            new SqlParameter("@source", source),
            NullableParam("@capture_instance", captureInstance),
        });
        if (rows.Count == 0 && enable && captureInstance is null)
        {
            // enable := true over a table nobody has captured. Declare from the SOURCE and defer the DDL to
            // execute (§15.7) — bind must stay side-effect-free, or an EXPLAIN would capture a table.
            return CdcDeclareDeferred(source, commitTimestamp, onSchemaChange, include, images,
                                      startingPosition, endingPosition, startingTimestamp,
                                      endingTimestamp);
        }
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
                      + "<catalog>.cdc.enable('<schema>.<table>') captures it, or pass enable := true to "
                      + "capture it on first read."
                    : "cdc.changes: change data capture is not enabled on database "
                      + $"'{database}'. Call <catalog>.cdc.enable_database() first, then cdc.enable(...).");
        }

        var nullability = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var matched = new List<string>();
        foreach (var row in rows)
        {
            string name = row[0] ?? string.Empty;
            if (!matched.Contains(name))
            {
                matched.Add(name);
            }
            if (row[3] is { } column)
            {
                nullability[column] = row[4] != "0";
            }
        }
        string instance = matched[0];
        if (matched.Count > 2)
        {
            // ⚠ UNREACHABLE through SQL Server, which caps a table at TWO capture instances (Msg 22962,
            // MEASURED §2.2) — so this can only mean the source string matched as an instance NAME and as a
            // table name at once, which is an ambiguous QUESTION rather than a boundary to resolve.
            throw new ArgumentException(
                $"cdc.changes: '{source}' matches {matched.Count} capture instances "
                + $"({string.Join(", ", matched)}), which SQL Server does not allow for one table - so the "
                + "source string must be matching both as a capture-instance name and as a table name. Name "
                + "the instance with capture_instance := '<name>'; SELECT * FROM <catalog>.cdc.tables() "
                + "lists them.");
        }
        if (matched.Count == 2)
        {
            return CdcDeclareUnion(source, captureInstance, matched, rows[0][1] ?? "dbo",
                                   rows[0][2] ?? source, nullability, commitTimestamp, onSchemaChange,
                                   include, images, startingPosition, endingPosition, startingTimestamp,
                                   endingTimestamp);
        }

        // The DESCRIBE, over the statement this reader is about to run. `c.*` rather than a column list
        // because the captured column NAMES are exactly what is being learned here — and the TVF's own four
        // metadata columns come first, which the check below asserts rather than assumes.
        string tvf = "cdc." + Quote("fn_cdc_get_all_changes_" + instance);
        bool updateMask = string.Equals(images, CdcChangesPlan.ImagesBoth, StringComparison.Ordinal);
        string describeSql = "SELECT " + CdcChangesSelectList(commitTimestamp, updateMask,
                                                             sourceColumns: null)
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
        return CdcDeclare(described, source, captureInstance, instance, rows[0][1] ?? "dbo",
                          rows[0][2] ?? source, nullability, commitTimestamp, onSchemaChange, include, images,
                          tvf, startingPosition, endingPosition, startingTimestamp, endingTimestamp);
    }


    /// <summary>
    /// The DEFERRED declaration: <c>enable := true</c> over a table that is not captured yet. The output
    /// schema comes from the SOURCE table; the capture instance does not exist until
    /// <see cref="CdcEnableAndResolve"/> runs at execute.
    /// </summary>
    /// <remarks>
    /// <para><b>⚠⚠ THIS IS WHAT MAKES <c>enable := true</c> AFFORDABLE AT ALL (§15.7).</b> Earlier analysis
    /// concluded the enable had to happen at BIND, because the output schema comes from the change table and
    /// the change table does not exist until the enable — which would have made <c>EXPLAIN</c>,
    /// <c>DESCRIBE</c> and <c>CREATE VIEW</c> perform DDL. Deriving the declaration from the SOURCE dissolves
    /// that, and it is correct BY CONSTRUCTION for a fresh enable: a default <c>sp_cdc_enable_table</c>
    /// captures every source column, so at the instant we enable, captured == source (MEASURED).</para>
    /// <para><b>⚠ The metadata columns are described, not assumed</b>, from literals of the same types
    /// through <see cref="CdcMetadataSelectList"/> — the SAME expression shape the real statement uses, so
    /// the two cannot drift. MEASURED identical: <c>varchar(16)</c>, <c>binary(21)</c>, <c>binary(10)</c>,
    /// <c>binary(10)</c>, <c>int</c>, <c>datetime</c>.</para>
    /// <para>⚠ EVERY source column is declared NULLABLE here, and that is not laziness: at bind we do not
    /// know which columns the enable will capture (a caller can reach this path on a table someone captures
    /// PARTIALLY between our bind and our execute), and a NOT NULL claim we cannot keep is the one direction
    /// that turns into a wrong answer. The arrival check at execute is what pins the rest.</para>
    /// <para>⚠ It refuses a source that is not a TABLE. <c>enable := true</c> cannot conjure a capture
    /// instance from an instance NAME, and reporting "not captured" for a typo would send the caller looking
    /// in the wrong place.</para>
    /// </remarks>
    private CdcChangesPlan CdcDeclareDeferred(string source, bool commitTimestamp, string onSchemaChange,
                                              string include, string images, byte[]? startingPosition,
                                              byte[]? endingPosition, DateTime? startingTimestamp,
                                              DateTime? endingTimestamp)
    {
        bool updateMask = string.Equals(images, CdcChangesPlan.ImagesBoth, StringComparison.Ordinal);
        var (schema, table) = SqlServerCdcSetup.SplitSource(source, "cdc.changes");
        string qualified = Quote(schema) + "." + Quote(table);
        string describeSql = "SELECT " + CdcSyntheticMetadataSelectList(commitTimestamp, updateMask)
                             + ", s.* FROM " + qualified + " AS s";
        // ⚠ The same parameter list as the real describe: the synthetic statement uses only @inst_name, and
        // the rest are declared-but-unused, which sp_executesql accepts. Passing one list keeps the two
        // describes from drifting in the one place they could.
        var described = DescribeQuery(describeSql, CdcDescribeParameters());
        if (described is null)
        {
            throw new ArgumentException(
                $"cdc.changes: enable := true was asked for, but '{schema}.{table}' could not be described - "
                + "it is not a table this connection can read. Change data capture is enabled per TABLE, so "
                + "the source has to be one; a capture-instance name cannot be enabled.");
        }
        int meta = CdcMetadataColumnCount(commitTimestamp, updateMask);
        // ⚠ The DECLARED schema is fixed at BIND and never re-derived, even though this plan re-resolves
        // itself at execute - so the metadata nullability a snapshot leg widens has to be right HERE too,
        // not only on the resolved path.
        bool hasSnapshot = !string.Equals(include, CdcChangesPlan.IncludeChanges, StringComparison.Ordinal);
        var fields = new List<Field>(described.FieldsList.Count);
        for (int i = 0; i < described.FieldsList.Count; i++)
        {
            var f = described.FieldsList[i];
            fields.Add(new Field(f.Name, f.DataType,
                                 nullable: i >= meta
                                           || CdcMetadataNullable(i, meta, commitTimestamp, updateMask,
                                                                  hasSnapshot)));
        }
        // ⚠ NO snapshot statement here, deliberately: this plan's capture instance does not exist yet, so
        // neither does the boundary a snapshot hands off at. CdcEnableAndResolve re-binds at execute and the
        // RESOLVED plan is the one that carries it.
        return new CdcChangesPlan(source, explicitInstance: null, commitTimestamp, onSchemaChange, include,
                                  images, new Schema(fields, metadata: null), startingPosition,
                                  endingPosition, startingTimestamp, endingTimestamp);
    }

    /// <summary>
    /// Declares and builds a read that spans a TWO-CAPTURE-INSTANCE boundary (slice 7, §19): the older
    /// instance below the boundary, the newer one at or above it, aligned by NAME into one output schema.
    /// </summary>
    /// <remarks>
    /// <para><b>⚠⚠ THE UNION IS IN T-SQL, AND THAT DISSOLVED THE <c>WidenArrowType</c> HELPER §15.8
    /// RECOMMENDED — whose stated rule was WRONG.</b> §15.8 weighed two shapes, "align in DuckDB SQL and get
    /// the widening free" against "align in C# and widen ourselves", and recommended the second because the
    /// reader is marshaled. There is a THIRD it did not consider: we already GENERATE T-SQL, so the alignment
    /// can happen on the server, where SQL Server's own type-precedence rules do the widening. MEASURED
    /// 2026-08-25: <c>decimal(9,2) ∪ decimal(18,4) → decimal(18,4)</c>, as §15.8 expected — but
    /// <c>decimal(9,0) ∪ decimal(5,4) → decimal(13,4)</c>, NOT the <c>decimal(9,4)</c> that §15.8's
    /// "max precision, max scale" gives. Thirteen is <c>max(integral digits) + max(scale)</c>, and it is the
    /// correct answer: at <c>decimal(9,4)</c> a nine-integral-digit value from the first branch OVERFLOWS. So
    /// the helper this slice inherited would have been built to a rule that silently loses data, and the
    /// server does it right for free.</para>
    /// <para><b>⚠⚠ A BARE <c>NULL</c> IN ONE BRANCH TAKES THE OTHER BRANCH'S TYPE — MEASURED, and it is what
    /// removes the last reason to render SQL type names.</b> <c>SELECT NULL AS b UNION ALL SELECT
    /// CAST('x' AS varchar(50))</c> describes <c>b</c> as <c>varchar(50)</c>, not as <c>int</c> (which is what
    /// a bare <c>SELECT NULL</c> outside a union gives, and which would have made the OTHER branch fail to
    /// convert). So a column only one instance captures is filled with the literal <c>NULL</c> and still
    /// arrives correctly typed.</para>
    /// <para><b>⚠ ONE SILENT CASE IS ACCEPTED AND SAID OUT LOUD:</b> where SQL Server's own rules cannot
    /// represent the union of two decimals within 38 digits it TRUNCATES the scale, and where two branches
    /// have unrelated types (a column dropped and re-added with a different type, §15.11) it CONVERTS by
    /// precedence rather than refusing — an unconvertible value then fails at read time with SQL Server's own
    /// conversion error, loudly. That is the same answer any T-SQL user gets from a <c>UNION ALL</c>, which is
    /// what makes it defensible; a hand-rolled rule would have been ours to get wrong.</para>
    /// <para><b>⚠ COLUMN ORDER: the NEWER instance's captured columns first, then columns only the OLDER one
    /// has.</b> The newer set is the table's CURRENT shape and the one a consumer keeps seeing once the older
    /// instance is gone, so putting it first makes <c>SELECT *</c> stable in the direction that matters; a
    /// dropped column is history and is appended.</para>
    /// <para><b>⚠ A column captured by only ONE instance is declared NULLABLE whatever the source says.</b> It
    /// is NULL-filled for the other leg's rows by construction, so a NOT NULL claim taken from
    /// <c>sys.columns</c> would be a claim the result violates on every pre-boundary row.</para>
    /// </remarks>
    private CdcChangesPlan CdcDeclareUnion(string source, string? explicitInstance,
                                           IReadOnlyList<string> instances, string sourceSchema,
                                           string sourceTable, IReadOnlyDictionary<string, bool> nullability,
                                           bool commitTimestamp, string onSchemaChange, string include,
                                           string images, byte[]? startingPosition, byte[]? endingPosition,
                                           DateTime? startingTimestamp, DateTime? endingTimestamp)
    {
        bool updateMask = string.Equals(images, CdcChangesPlan.ImagesBoth, StringComparison.Ordinal);
        string older = instances[0];
        string newer = instances[1];
        var capturedTypes = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var captured = CdcCapturedColumns(older, newer, capturedTypes);
        if (!captured.TryGetValue(older, out var olderColumns) || olderColumns.Count == 0
            || !captured.TryGetValue(newer, out var newerColumns) || newerColumns.Count == 0)
        {
            throw new InvalidOperationException(
                $"cdc.changes: '{source}' has two capture instances ('{older}', '{newer}') but "
                + "cdc.captured_columns does not list the captured set of both. Change data capture may be "
                + "mid-reconfiguration; re-run the statement, or name one instance with "
                + "capture_instance := '<name>'.");
        }

        var newerSet = new HashSet<string>(newerColumns, StringComparer.Ordinal);
        var olderSet = new HashSet<string>(olderColumns, StringComparer.Ordinal);
        var aligned = new List<string>(newerColumns);
        foreach (string column in olderColumns)
        {
            if (!newerSet.Contains(column))
            {
                aligned.Add(column);
            }
        }

        // ⚠⚠ A GENUINE TYPE CONFLICT IS REFUSED HERE, AT BIND, AND IT COSTS NOTHING EXTRA — the captured type
        // NAMES came back with the column names above. MEASURED 2026-08-25: a column DROPPED and RE-ADDED
        // with a different type (§15.11's pathological case, and the only way two instances of one table can
        // disagree — an ALTER COLUMN type is propagated to BOTH change tables) really does produce
        // varchar(20) in the older instance and int in the newer, the UNION describes it as int by SQL
        // Server's precedence, and the read then dies MID-SCAN with
        // "Conversion failed when converting the varchar value 'text-value' to data type int".
        // ⚠ THE REASON THAT IS NOT GOOD ENOUGH IS THE SILENT HALF: the conversion error fires on an
        // unconvertible VALUE, not on the conflict, so a column whose historical text happens to be numeric
        // converts quietly and the two eras silently stop meaning the same thing. Comparing type NAMES
        // catches the conflict itself; a difference WITHIN one type name (varchar(20) vs varchar(50),
        // decimal(9,2) vs decimal(18,4)) is a widening SQL Server performs correctly and is deliberately
        // allowed through.
        foreach (string column in aligned)
        {
            if (!olderSet.Contains(column) || !newerSet.Contains(column))
            {
                continue;
            }
            string olderType = capturedTypes[older][column];
            string newerType = capturedTypes[newer][column];
            if (!string.Equals(olderType, newerType, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"cdc.changes: '{source}' cannot be read across its capture-instance boundary because "
                    + $"column '{column}' is captured as {olderType} by '{older}' and as {newerType} by "
                    + $"'{newer}'. One column cannot be both, so a union of the two would coerce one era to "
                    + "the other's type - silently where the values happen to convert. Read each instance on "
                    + "its own with capture_instance := '<name>'.");
            }
        }

        // ⚠ The DESCRIBE form carries NO WHERE clause at all — the same shortcut the one-instance path takes.
        // A predicate cannot change a column's type, and leaving it out means the describe needs neither the
        // cursor parameters nor the split, which do not exist until execute.
        string describeSql = CdcUnionSql(older, newer, aligned, olderSet, newerSet, commitTimestamp,
                                         updateMask, startingPosition: null, endingPosition: null,
                                         executable: false);
        var described = DescribeQuery(describeSql, CdcUnionDescribeParameters());
        if (described is null)
        {
            throw new InvalidOperationException(
                $"cdc.changes: SQL Server could not describe the union of capture instances '{older}' and "
                + $"'{newer}' for '{source}'. Name one of them with capture_instance := '<name>' to read it "
                + "alone; SELECT * FROM <catalog>.cdc.tables() lists them.");
        }
        int meta = CdcMetadataColumnCount(commitTimestamp, updateMask);
        if (described.FieldsList.Count != meta + aligned.Count)
        {
            throw new InvalidOperationException(
                $"cdc.changes: the two-instance read of '{source}' described "
                + $"{described.FieldsList.Count} columns where {meta + aligned.Count} were composed. Change "
                + "data capture may be mid-reconfiguration; re-run the statement.");
        }

        var fields = new List<Field>(described.FieldsList.Count);
        bool hasSnapshot = !string.Equals(include, CdcChangesPlan.IncludeChanges, StringComparison.Ordinal);
        for (int i = 0; i < meta; i++)
        {
            var f = described.FieldsList[i];
            fields.Add(new Field(f.Name, f.DataType,
                                 nullable: CdcMetadataNullable(i, meta, commitTimestamp, updateMask,
                                                               hasSnapshot)));
        }
        for (int i = meta; i < described.FieldsList.Count; i++)
        {
            var f = described.FieldsList[i];
            string column = aligned[i - meta];
            bool both = olderSet.Contains(column) && newerSet.Contains(column);
            fields.Add(new Field(f.Name, f.DataType,
                                 nullable: !both
                                           || !nullability.TryGetValue(column, out bool isNullable)
                                           || isNullable));
        }

        string sql = CdcUnionSql(older, newer, aligned, olderSet, newerSet, commitTimestamp, updateMask,
                                 CdcCursorShape(include, startingPosition), endingPosition, executable: true);
        string? snapshotSql = null;
        if (!string.Equals(include, CdcChangesPlan.IncludeChanges, StringComparison.Ordinal))
        {
            CdcEnsureSnapshotColumnsExist(include, sourceSchema + "." + sourceTable, aligned, nullability);
            snapshotSql = CdcSnapshotSql(sourceSchema, sourceTable, aligned, commitTimestamp, updateMask);
        }
        Log.LogDebug("cdc changes {Source}: two capture instances - {Older} below the boundary, {Newer} at "
                     + "or above it, {Columns} aligned columns", source, older, newer, aligned.Count);
        return new CdcChangesPlan(source, explicitInstance, commitTimestamp, onSchemaChange, include,
                                  images, new Schema(fields, metadata: null), startingPosition,
                                  endingPosition, startingTimestamp, endingTimestamp,
                                  captureInstance: older, sourceSchema: sourceSchema,
                                  sourceTable: sourceTable, sql: sql, secondInstance: newer,
                                  snapshotSql: snapshotSql);
    }

    /// <summary>
    /// The captured column names of two capture instances, each in change-table order.
    /// </summary>
    /// <remarks>
    /// ⚠ From <c>cdc.captured_columns</c> rather than by parsing <c>captured_column_list</c>, which
    /// <c>sp_cdc_help_change_data_capture</c> already returns as <c>[id], [v], [extra]</c>: that string
    /// escapes a <c>]</c> in a column name by doubling it, so parsing it is a quoting problem with a silent
    /// wrong answer at the end of it. ⚠ And <c>column_ordinal</c> is the CHANGE TABLE's order (MEASURED),
    /// which is what the TVF returns after its four metadata columns — not the source table's
    /// <c>column_id</c>.
    /// </remarks>
    private Dictionary<string, List<string>> CdcCapturedColumns(
        string older, string newer, Dictionary<string, Dictionary<string, string>> types)
    {
        const string sql =
            "SET NOCOUNT ON; " +
            "SELECT CAST(ct.capture_instance AS varchar(128)) AS capture_instance, " +
            "CAST(cc.column_name AS varchar(128)) AS column_name, " +
            "CAST(cc.column_type AS varchar(128)) AS column_type " +
            "FROM cdc.captured_columns AS cc " +
            "JOIN cdc.change_tables AS ct ON ct.object_id = cc.object_id " +
            "WHERE ct.capture_instance = @older OR ct.capture_instance = @newer " +
            "ORDER BY ct.capture_instance, cc.column_ordinal;";
        var rows = ReadMetadataRows(sql, 3, new[]
        {
            new SqlParameter("@older", older),
            new SqlParameter("@newer", newer),
        });
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row[0] is not { } instance || row[1] is not { } column)
            {
                continue;
            }
            if (!result.TryGetValue(instance, out var columns))
            {
                columns = new List<string>();
                result[instance] = columns;
                types[instance] = new Dictionary<string, string>(StringComparer.Ordinal);
            }
            columns.Add(column);
            types[instance][column] = row[2] ?? "?";
        }
        return result;
    }

    /// <summary>
    /// The two-leg <c>UNION ALL</c>: the older instance strictly BELOW the boundary, the newer one AT OR
    /// ABOVE it.
    /// </summary>
    /// <remarks>
    /// <para><b>⚠⚠ THE TWO PREDICATES ARE THE PARTITION, and they are what makes double-counting
    /// UNREPRESENTABLE.</b> MEASURED (§2.2, re-confirmed 2026-08-25): both instances capture EVERY change in
    /// their overlap window — one INSERT produced a row in each — so a reader that let both legs see one LSN
    /// would return it twice. <c>&lt; @split</c> and <c>&gt;= @split</c> cover every LSN exactly once
    /// whatever the TVF arguments are, which is why they are stated explicitly rather than left implicit in
    /// those arguments: an argument is a performance bound, a predicate is the correctness one.</para>
    /// <para><b>⚠ <c>&lt;= @win_to</c> ON THE NEWER LEG IS NOT REDUNDANT.</b> The TVF arguments are CLAMPED so
    /// they are always a legal window (see <see cref="CdcExecuteChanges"/>), and the clamp can only widen the
    /// newer leg — when the caller's whole window sits BELOW the boundary that leg is handed
    /// <c>(split, split)</c>, and would otherwise return rows the caller did not ask for. The older leg needs
    /// no such term: its clamp can only ever hand back rows at or above the split, which <c>&lt; @split</c>
    /// removes.</para>
    /// <para>⚠ The <c>_commit_timestamp</c> LEFT JOIN is per LEG, each with its own <c>m</c> alias — a join in
    /// a <c>UNION ALL</c> belongs to one branch, so there is nothing shared to hoist.</para>
    /// </remarks>
    private static string CdcUnionSql(string older, string newer, IReadOnlyList<string> aligned,
                                      ISet<string> olderSet, ISet<string> newerSet, bool commitTimestamp,
                                      bool updateMask, byte[]? startingPosition, byte[]? endingPosition,
                                      bool executable)
    {
        var cursor = executable ? CdcCursorTerms(startingPosition, endingPosition) : new List<string>();
        var olderTerms = new List<string>(cursor);
        var newerTerms = new List<string>(cursor);
        if (executable)
        {
            olderTerms.Add("c.[__$start_lsn] < @split");
            newerTerms.Add("c.[__$start_lsn] >= @split");
            newerTerms.Add("c.[__$start_lsn] <= @win_to");
        }
        return CdcUnionLegSql(older, "@inst_a", "@from_a", "@to_a", aligned, olderSet, commitTimestamp,
                              updateMask, olderTerms)
               + " UNION ALL "
               + CdcUnionLegSql(newer, "@inst_b", "@from_b", "@to_b", aligned, newerSet, commitTimestamp,
                                updateMask, newerTerms);
    }

    private static string CdcUnionLegSql(string instance, string instParam, string fromParam, string toParam,
                                         IReadOnlyList<string> aligned, ISet<string> captures,
                                         bool commitTimestamp, bool updateMask,
                                         IReadOnlyList<string> terms)
    {
        var sb = new System.Text.StringBuilder("SELECT ");
        // ⚠ The mask comes from EACH LEG'S OWN change table, so its bit positions are that INSTANCE'S column
        // ordinals - which the two instances need not agree on. That is why _capture_instance ships beside
        // it: a mask is only decodable against the instance that produced it.
        sb.Append(CdcRealMetadataSelectList(commitTimestamp, updateMask, instParam));
        foreach (string column in aligned)
        {
            // ⚠ A BARE `NULL`, never a CONVERT: MEASURED that it adopts the other branch's type through the
            // UNION ALL, which is what keeps this path free of SQL type-name rendering entirely.
            sb.Append(", ").Append(captures.Contains(column) ? "c." + Quote(column) : "NULL")
              .Append(" AS ").Append(Quote(column));
        }
        sb.Append(" FROM cdc.").Append(Quote("fn_cdc_get_all_changes_" + instance))
          .Append('(').Append(fromParam).Append(", ").Append(toParam).Append(", @row_filter) AS c");
        if (commitTimestamp)
        {
            sb.Append(CdcCommitTimeJoinSql);
        }
        if (terms.Count > 0)
        {
            sb.Append(" WHERE ").Append(string.Join(" AND ", terms));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Placeholder values for the two-leg DESCRIBE. ⚠ Never evaluated, but every parameter the statement
    /// mentions must be DECLARED or SQL Server cannot compile what it is describing.
    /// </summary>
    private static SqlParameter[] CdcUnionDescribeParameters() => new[]
    {
        CdcBinaryParam("@from_a", new byte[CdcChangesPlan.LsnBytes]),
        CdcBinaryParam("@to_a", new byte[CdcChangesPlan.LsnBytes]),
        CdcBinaryParam("@from_b", new byte[CdcChangesPlan.LsnBytes]),
        CdcBinaryParam("@to_b", new byte[CdcChangesPlan.LsnBytes]),
        new SqlParameter("@row_filter", System.Data.SqlDbType.NVarChar, 30)
        {
            Value = CdcChangesPlan.RowFilterAll,
        },
        new SqlParameter("@inst_a", System.Data.SqlDbType.VarChar, 128) { Value = string.Empty },
        new SqlParameter("@inst_b", System.Data.SqlDbType.VarChar, 128) { Value = string.Empty },
    };

    /// <summary>
    /// Runs the deferred <c>enable := true</c> and resolves the plan. Returns the resolved plan and whether
    /// THIS call created the capture instance.
    /// </summary>
    /// <remarks>
    /// <para>⚠ It goes through <see cref="CdcEnableTable"/> rather than issuing its own DDL, so it inherits
    /// the generated capture-instance name, the ownership marker and the atomic transaction — and, crucially,
    /// the idempotence: a table someone captured between our bind and our execute is reported
    /// <c>changed = false</c> and simply read.</para>
    /// <para>⚠ The DECLARED schema is NOT replaced by the resolved one. Bind already told DuckDB what the
    /// columns are; the resolved plan supplies only the capture instance and the statement. If the two
    /// disagree — a table captured PARTIALLY by someone else in that window — the arrival check fails
    /// loudly, which is the honest outcome and the only one that cannot corrupt a result.</para>
    /// </remarks>
    internal (CdcChangesPlan Plan, bool Created) CdcEnableAndResolve(CdcChangesPlan plan)
    {
        var (schema, table) = SqlServerCdcSetup.SplitSource(plan.Source, "cdc.changes");
        using var report = CdcEnableTable(schema, table, captureInstance: null, columns: null, role: null,
                                          index: null, filegroup: null, net: false);
        bool created = report.Length > 0 && ((BooleanArray)report.Column(1)).GetValue(0) == true;
        Log.LogDebug("cdc changes {Source}: enable := true {Outcome}", plan.Source,
                     created ? "created a capture instance" : "found the table already captured");
        var resolved = CdcBindChanges(plan.Source, plan.ExplicitInstance, plan.CommitTimestamp, enable: false,
                                      plan.OnSchemaChange, plan.Include, plan.Images, plan.StartingPosition,
                                      plan.EndingPosition, plan.StartingTimestamp, plan.EndingTimestamp);
        return (resolved, created);
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
    private static CdcChangesPlan CdcDeclare(Schema described, string source, string? explicitInstance,
                                             string instance, string sourceSchema, string sourceTable,
                                             IReadOnlyDictionary<string, bool> nullability,
                                             bool commitTimestamp, string onSchemaChange, string include,
                                             string images, string tvf, byte[]? startingPosition,
                                             byte[]? endingPosition, DateTime? startingTimestamp,
                                             DateTime? endingTimestamp)
    {
        bool updateMask = string.Equals(images, CdcChangesPlan.ImagesBoth, StringComparison.Ordinal);
        int meta = CdcMetadataColumnCount(commitTimestamp, updateMask);
        // ⚠ `__$update_mask` appears TWICE in the described statement when images := 'both', and that is not
        // a duplicate to collapse: the metadata block's [_update_mask] is our OUTPUT column while this one is
        // the TVF's own, still where it always was in the `c.*` expansion. Different names, so nothing
        // collides — but the offset check below counts the TVF's four, and losing that distinction would
        // shift every captured column by one.
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
        bool hasSnapshot = !string.Equals(include, CdcChangesPlan.IncludeChanges, StringComparison.Ordinal);
        for (int i = 0; i < meta; i++)
        {
            var f = described.FieldsList[i];
            fields.Add(new Field(f.Name, f.DataType,
                                 nullable: CdcMetadataNullable(i, meta, commitTimestamp, updateMask,
                                                               hasSnapshot)));
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

        string sql = "SELECT " + CdcChangesSelectList(commitTimestamp, updateMask, sourceColumns)
                     + " FROM " + tvf + "(@from_lsn, @to_lsn, @row_filter) AS c"
                     + (commitTimestamp ? CdcCommitTimeJoinSql : string.Empty)
                     + CdcCursorPredicateSql(CdcCursorShape(include, startingPosition), endingPosition);
        string? snapshotSql = null;
        if (!string.Equals(include, CdcChangesPlan.IncludeChanges, StringComparison.Ordinal))
        {
            CdcEnsureSnapshotColumnsExist(include, sourceSchema + "." + sourceTable, sourceColumns, nullability);
            snapshotSql = CdcSnapshotSql(sourceSchema, sourceTable, sourceColumns, commitTimestamp,
                                         updateMask);
        }
        return new CdcChangesPlan(source, explicitInstance, commitTimestamp, onSchemaChange, include,
                                  images, new Schema(fields, metadata: null), startingPosition,
                                  endingPosition, startingTimestamp, endingTimestamp,
                                  captureInstance: instance, sourceSchema: sourceSchema,
                                  sourceTable: sourceTable, sql: sql, snapshotSql: snapshotSql);
    }

    /// <summary>
    /// ⚠ Emitted ONLY when <c>commit_timestamp := true</c>, and §11 item 2 is why. It serves exactly ONE
    /// output column, and MEASURED: DuckDB does NOT eliminate an unused LEFT JOIN — not even against a
    /// PRIMARY KEY — so emitting it unconditionally would make every caller pay a second full scan for a
    /// column most of them never select.
    /// </summary>
    private const string CdcCommitTimeJoinSql =
        " LEFT JOIN cdc.[lsn_time_mapping] AS m ON m.[start_lsn] = c.[__$start_lsn]";

    private static string CdcChangesSelectList(bool commitTimestamp, bool updateMask,
                                              IReadOnlyList<string>? sourceColumns)
    {
        var sb = new System.Text.StringBuilder(CdcRealMetadataSelectList(commitTimestamp, updateMask));
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
        var terms = CdcCursorTerms(startingPosition, endingPosition);
        // ⚠ No ORDER BY, deliberately. The change table's clustered index is
        // (__$start_lsn, __$command_id, __$seqval, __$operation) — MEASURED, §15.2 — so ordering by our
        // 3-tuple would insert a real SORT rather than ride the index, and DuckDB does not promise to
        // preserve a table function's row order through its pipeline anyway. Every row carries its own
        // _position; `ORDER BY _position` is the documented and correct way to ask for order (§2.4).
        return terms.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", terms);
    }

    /// <summary>
    /// The cursor bounds as WHERE terms, so the two-leg union read can put them on BOTH legs.
    /// </summary>
    /// <remarks>
    /// ⚠ A leg of the union carries the SAME cursor terms as the other one. They are a filter on the
    /// caller's window, not on which instance answers for it — the split is what does that — so omitting them
    /// from either leg would return rows before the caller's cursor.
    /// </remarks>
    /// <summary>
    /// The lower bound the STATEMENT is built against, which is not always the one the caller supplied.
    /// </summary>
    /// <remarks>
    /// <para><b>⚠⚠ THE STATEMENT IS COMPOSED AT BIND AND THE HANDOFF IS ONLY KNOWN AT EXECUTE.</b> An
    /// <c>include := 'snapshot+changes'</c> read has no <c>starting_position</c> — it is refused, because a
    /// snapshot IS the starting point — and yet its changes half MUST carry the exclusive cursor predicate,
    /// or every change at the handoff LSN would be delivered a second time on top of the snapshot that
    /// already contains it. So the SQL is built against a 21-byte PLACEHOLDER whose only job is to select the
    /// three-clause predicate shape; the VALUE is bound at execute from the position the snapshot handed
    /// over.</para>
    /// <para>⚠ The placeholder's contents are never used — <see cref="CdcExecuteChanges"/> binds
    /// <c>@cur_lsn</c>/<c>@cur_seq</c>/<c>@cur_op</c> from the window — so a zeroed array is honest rather
    /// than a value pretending to mean something.</para>
    /// </remarks>
    private static byte[]? CdcCursorShape(string include, byte[]? startingPosition) =>
        startingPosition
        ?? (string.Equals(include, CdcChangesPlan.IncludeSnapshotChanges, StringComparison.Ordinal)
                ? new byte[CdcChangesPlan.PositionBytes]
                : null);

    private static List<string> CdcCursorTerms(byte[]? startingPosition, byte[]? endingPosition)
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
        return terms;
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
        new SqlParameter("@inst_name", System.Data.SqlDbType.VarChar, 128) { Value = string.Empty },
    };

    // binary(10), matching the TVF's own parameter types and the change table's columns. Declared rather
    // than inferred: SqlClient would infer varbinary sized to the value, and an equality predicate against
    // binary(10) is one implicit conversion away from a comparison nobody measured.
    private static SqlParameter CdcBinaryParam(string name, byte[] value) =>
        new(name, System.Data.SqlDbType.Binary, CdcChangesPlan.LsnBytes) { Value = value };

    /// <summary>
    /// The window-resolution batch: the retention floor, the capture watermark, and — on a two-instance read
    /// — the boundary the window is split on.
    /// </summary>
    /// <remarks>
    /// <para>⚠ IF/ELSE rather than CASE, for the reason <see cref="CdcMaxLsn"/> records: with CDC disabled
    /// these functions raise 208 naming <c>cdc.lsn_time_mapping</c>, and SQL Server does not guarantee an
    /// unevaluated CASE branch. Both branches project the same varchar columns.</para>
    /// <para>⚠ The split column is APPENDED rather than always present, so the one-instance statement is
    /// byte-identical to what it was before slice 7. The alternative — one shape, passing NULL for the second
    /// instance — reads back <c>0x0000000000000000000</c> rather than NULL (MEASURED), i.e. a value that must
    /// be ignored on pain of silently dropping every pre-boundary row.</para>
    /// </remarks>
    private static string CdcWindowSql(bool union, bool mapStart, bool mapEnd)
    {
        // ⚠⚠ THE TWO RELATIONAL OPERATORS ARE THE SEMANTICS, not a detail of the call. A lower bound uses
        // 'smallest greater than or equal' and an upper bound 'largest less than or equal', so BOTH are
        // INCLUSIVE — which is what a wall-clock instant means, and deliberately NOT what a _position means
        // (a resume token names a row already read, so it is exclusive). Getting the operator wrong on the
        // lower bound would silently DROP whatever committed exactly at the named instant.
        // ⚠ CONVERT(datetime, …) is EXPLICIT rather than left to an implicit datetime2 → datetime widening:
        // cdc.lsn_time_mapping stores `datetime`, so the comparison happens at ~3.33 ms resolution whatever
        // we do, and doing the narrowing ourselves is what makes that a documented property of the bound
        // instead of a conversion nobody chose.
        string mapped =
            (mapStart
                 ? ", CONVERT(varchar(30), sys.fn_cdc_map_time_to_lsn('smallest greater than or equal', "
                   + "CONVERT(datetime, @ts_from)), 1) AS ts_from"
                 : string.Empty)
            + (mapEnd
                   ? ", CONVERT(varchar(30), sys.fn_cdc_map_time_to_lsn('largest less than or equal', "
                     + "CONVERT(datetime, @ts_to)), 1) AS ts_to"
                   : string.Empty);
        string nulls = (mapStart ? ", CAST(NULL AS varchar(30)) AS ts_from" : string.Empty)
                       + (mapEnd ? ", CAST(NULL AS varchar(30)) AS ts_to" : string.Empty);
        return
            "SET NOCOUNT ON; " +
            "DECLARE @en varchar(1) = CASE WHEN " + SqlServerCdcFunctions.CdcEnabledPredicate
            + " THEN '1' ELSE '0' END; " +
            "IF @en = '1' " +
            "  SELECT @en AS enabled, CONVERT(varchar(30), sys.fn_cdc_get_min_lsn(@inst), 1) AS min_lsn, " +
            "         CONVERT(varchar(30), sys.fn_cdc_get_max_lsn(), 1) AS max_lsn"
            + (union ? ", CONVERT(varchar(30), sys.fn_cdc_get_min_lsn(@inst2), 1) AS split" : string.Empty)
            + mapped + "; " +
            "ELSE SELECT @en AS enabled, CAST(NULL AS varchar(30)) AS min_lsn, " +
            "            CAST(NULL AS varchar(30)) AS max_lsn"
            + (union ? ", CAST(NULL AS varchar(30)) AS split" : string.Empty) + nulls + ";";
    }

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
    internal CdcWindow CdcResolveWindow(CdcChangesPlan plan, bool justCreated = false,
                                        byte[]? handoff = null)
    {
        string instance = CdcRequireResolved(plan).Instance;
        // ⚠ A timestamp bound is resolved HERE, at execute, not at bind. The bound is a wall-clock instant
        // and the mapping table only grows, so a bound naming an instant the capture job has not reached yet
        // resolves to NULL at bind and to a real LSN moments later. Resolving with the window is also free —
        // it rides the batch that was going to run anyway.
        bool mapStart = plan.StartingTimestamp is not null;
        bool mapEnd = plan.EndingTimestamp is not null;
        string sql = CdcWindowSql(plan.IsUnion, mapStart, mapEnd);
        var windowParameters = new List<SqlParameter> { new("@inst", instance) };
        if (plan.SecondInstance is { } secondInstance)
        {
            windowParameters.Add(new SqlParameter("@inst2", secondInstance));
        }
        if (plan.StartingTimestamp is { } tsFrom)
        {
            windowParameters.Add(new SqlParameter("@ts_from", System.Data.SqlDbType.DateTime2)
            {
                Value = tsFrom,
            });
        }
        if (plan.EndingTimestamp is { } tsTo)
        {
            windowParameters.Add(new SqlParameter("@ts_to", System.Data.SqlDbType.DateTime2)
            {
                Value = tsTo,
            });
        }
        int columns = (plan.IsUnion ? 4 : 3) + (mapStart ? 1 : 0) + (mapEnd ? 1 : 0);
        int tsFromColumn = plan.IsUnion ? 4 : 3;
        int tsToColumn = tsFromColumn + (mapStart ? 1 : 0);

        // readYourWrites: a capture instance enabled earlier in THIS transaction must be visible here, and
        // this is a short metadata read that holds no reader open. ⚠ The streaming read below deliberately
        // does NOT do that - see CdcExecuteChanges.
        var rows = ReadMetadataRows(sql, columns, windowParameters);
        if (rows.Count == 0 || rows[0][0] != "1")
        {
            throw new InvalidOperationException(
                "cdc.changes: change data capture is no longer enabled on this database. It was when this "
                + "statement was bound, so something disabled it in between - cdc.health() reports the "
                + "current state.");
        }
        byte[]? minLsn = SqlServerCdcFunctions.ParseHex(rows[0][1]);
        byte[]? maxLsn = SqlServerCdcFunctions.ParseHex(rows[0][2]);
        // ⚠⚠ A ZERO FLOOR IS "SQL SERVER DOES NOT KNOW THIS CAPTURE INSTANCE", NOT A LOW BOUND — MEASURED
        // 2026-08-25: fn_cdc_get_min_lsn returns 0x0000000000000000000 for an unknown name (and for NULL),
        // never NULL. Zero compares below every real LSN, so it passes the retention check below trivially
        // and hands the window to the TVF: the unattributable 313 again, for an instance that was DISABLED
        // between this statement's bind and its execute. It is distinguishable from the transient NULL floor
        // of §1.6a, so the two get different answers.
        if (minLsn is not null && CdcChangesPlan.IsZeroLsn(minLsn))
        {
            throw new InvalidOperationException(
                $"cdc.changes: SQL Server no longer knows capture instance '{plan.CaptureInstance}' - it "
                + "reports a retention floor of zero, which is what it answers for an instance that does not "
                + "exist. It did exist when this statement was bound, so something disabled it in between. "
                + "SELECT * FROM <catalog>.cdc.tables() reports what is captured now.");
        }
        byte[]? split = null;
        if (plan.IsUnion)
        {
            split = SqlServerCdcFunctions.ParseHex(rows[0][3]);
            if (split is null)
            {
                throw new InvalidOperationException(
                    $"cdc.changes: the boundary between capture instances '{plan.CaptureInstance}' and "
                    + $"'{plan.SecondInstance}' is not established yet - SQL Server answered NULL for the "
                    + "newer instance's retention floor, which is the value this read splits the window on "
                    + "(it is transiently NULL for up to one polling interval after an instance is enabled). "
                    + "Retry, or name one instance with capture_instance := '<name>'.");
            }
            if (CdcChangesPlan.IsZeroLsn(split))
            {
                // ⚠⚠ THE DANGEROUS ONE. A zero split puts EVERY row in the newer leg, and the newer
                // instance does not have the pre-boundary changes - so the read would come back SHORT with
                // nothing failing. Refusing is the only answer that cannot lose rows silently.
                throw new InvalidOperationException(
                    $"cdc.changes: SQL Server no longer knows capture instance '{plan.SecondInstance}' - it "
                    + "reports a retention floor of zero, which is what it answers for an instance that does "
                    + "not exist. That value is the boundary this read splits on, so continuing would return "
                    + "only the newer instance's rows and silently omit everything before the boundary.");
            }
            // ⚠⚠ THE ONE REMAINING WAY THIS READ COULD REACH THE 313 IT EXISTS TO REPLACE, found by walking
            // the clamp rather than by a failure. The newer leg's TVF only accepts a window inside
            // [split, max_lsn], because split IS that instance's floor - so when the BOUNDARY sits above the
            // capture watermark there is no legal window to hand it at all, and every clamp that keeps the
            // two legs partitioned produces an inverted or below-floor call. It means the capture job has
            // not scanned since the newer instance was enabled.
            // ⚠ REASONED UNREACHABLE, and guarded anyway: §1.6a MEASURED that a just-enabled instance
            // answers a NULL floor in exactly that window, so the branch above fires first. "Reasoned
            // unreachable" is precisely the argument that has been wrong before in this feature, and the
            // cost of being wrong here is the unattributable message the whole pre-check exists to prevent.
            if (maxLsn is not null && CdcChangesPlan.CompareLsn(split, maxLsn) > 0)
            {
                throw new InvalidOperationException(
                    $"cdc.changes: the boundary between capture instances '{plan.CaptureInstance}' and "
                    + $"'{plan.SecondInstance}' is {CdcChangesPlan.Hex(split)}, above the capture watermark "
                    + $"{CdcChangesPlan.Hex(maxLsn)} - the capture job has not scanned since the newer "
                    + "instance was enabled, so there is nothing on its side of the boundary to read yet. "
                    + "Retry in one polling interval (cdc.health() reports it), or read the older instance "
                    + $"alone with capture_instance := '{plan.CaptureInstance}'.");
            }
        }
        if (minLsn is null && justCreated)
        {
            // ⚠ NOT the retry error, and the difference is a FACT rather than a kindness: we created this
            // capture instance microseconds ago, so its start_lsn is now and nothing before it was captured.
            // The readable set is EMPTY, which is an answer we can give — where for an instance we did NOT
            // create, a NULL floor is genuinely unknowable and must say so.
            Log.LogDebug("cdc changes {Source} ({Instance}): capture instance just created - empty window",
                         plan.SourceName, plan.CaptureInstance);
            return CdcWindow.Empty;
        }
        if (minLsn is null)
        {
            throw new InvalidOperationException(
                $"cdc.changes: the retention floor for capture instance '{plan.CaptureInstance}' is not "
                + "established yet - SQL Server answered NULL. Either the capture job has not scanned this "
                + "instance since it was enabled (retry in one polling interval; cdc.health() reports it), or "
                + "the instance no longer exists. It is deliberately NOT treated as 'no lower bound': that "
                + "would hand the window to SQL Server and get back an error naming neither.");
        }

        // ⚠⚠ A TIMESTAMP THAT MAPS TO NOTHING IS AN EMPTY WINDOW, NOT AN ERROR — in BOTH directions, and
        // for the same reason each time: the answer to "what committed at or after next Tuesday" is
        // "nothing yet", and the answer to "what committed at or before the day before this table existed"
        // is "nothing". Raising there would turn a legitimate poll into a failure the caller has to
        // special-case. ⚠ It is NOT the same as a below-the-floor bound, which IS refused a few lines down:
        // that one means the rows EXISTED and have been purged, so continuing would skip them silently.
        byte[]? tsFromLsn = mapStart ? SqlServerCdcFunctions.ParseHex(rows[0][tsFromColumn]) : null;
        byte[]? tsToLsn = mapEnd ? SqlServerCdcFunctions.ParseHex(rows[0][tsToColumn]) : null;
        if (mapStart && tsFromLsn is null)
        {
            Log.LogDebug("cdc changes {Source} ({Instance}): starting_timestamp {Ts} is after every captured "
                         + "transaction - empty window", plan.SourceName, plan.CaptureInstance,
                         plan.StartingTimestamp);
            return CdcWindow.Empty;
        }
        if (mapEnd && tsToLsn is null)
        {
            Log.LogDebug("cdc changes {Source} ({Instance}): ending_timestamp {Ts} is before every captured "
                         + "transaction - empty window", plan.SourceName, plan.CaptureInstance,
                         plan.EndingTimestamp);
            return CdcWindow.Empty;
        }

        byte[] toLsn;
        if (tsToLsn is { } endingByTime)
        {
            // ⚠ No watermark check, and it is unnecessary rather than skipped: this LSN came OUT of
            // cdc.lsn_time_mapping, which the capture job writes as it scans, so it cannot exceed the
            // watermark that same job publishes. (maxLsn is NULL only when nothing has been captured, and
            // then the mapping is empty and the branch above has already returned.)
            toLsn = endingByTime;
        }
        else if (plan.EndingPosition is { } ending)
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

        // ⚠ The HANDOFF outranks the caller's cursor, and they cannot both be present: an
        // include := 'snapshot+changes' read refuses starting_position at bind, precisely so that the
        // question "which lower bound governs" has one answer.
        byte[]? cursor = handoff ?? plan.StartingPosition;
        // ⚠ A timestamp lower bound leaves the CURSOR null on purpose, so no cursor predicate is emitted and
        // the TVF's own INCLUSIVE from_lsn is what governs. Synthesising a 21-byte position from the mapped
        // LSN would push the bound through the exclusive three-clause predicate instead and drop whatever
        // committed exactly at that instant — the one thing an inclusive bound promises not to do.
        byte[] fromLsn = tsFromLsn
                         ?? (cursor is { } starting ? CdcChangesPlan.LsnOf(starting) : minLsn);
        if (handoff is not null && CdcChangesPlan.CompareLsn(fromLsn, minLsn) < 0)
        {
            // ⚠⚠ CLAMPED UP TO THE FLOOR RATHER THAN REFUSED, and ONLY on the handoff path. The handoff is
            // the DATABASE-wide capture watermark at the pin, while the floor is THIS capture instance's
            // earliest readable position - so a freshly enabled instance legitimately starts ABOVE the
            // watermark, and refusing there would refuse the very shape
            // `enable := true, include := 'snapshot+changes'` exists to serve. Nothing is skipped by the
            // clamp: what lies between them is history this instance never captured, and the SNAPSHOT
            // already carries the state it produced. The cursor predicate then excludes nothing (every row
            // the TVF can return is above the handoff), so the TVF's own inclusive floor governs.
            Log.LogDebug("cdc changes {Source} ({Instance}): handoff {Handoff} is below the retention floor "
                         + "{Floor} - clamped, the snapshot covers what lies between",
                         plan.SourceName, plan.CaptureInstance, CdcChangesPlan.Hex(fromLsn),
                         CdcChangesPlan.Hex(minLsn));
            fromLsn = minLsn;
        }
        else if (CdcChangesPlan.CompareLsn(fromLsn, minLsn) < 0)
        {
            // ⚠⚠ THE MESSAGE NAMES BOTH CAUSES, because this read cannot tell them apart and MEASURING
            // it is what showed that. fn_cdc_map_time_to_lsn resolves against cdc.lsn_time_mapping, which is
            // DATABASE-wide, while the floor is this INSTANCE's — so an old timestamp on a recently captured
            // table maps below the floor with nothing having been lost at all. (Measured: an instance
            // enabled minutes earlier, starting_timestamp := 2020-01-01, mapping floor 0x2C… against an
            // instance floor 0x30….) An earlier wording asserted "removed by the cleanup job", which is
            // true of one cause and a fabrication about the other.
            // ⚠ It still REFUSES rather than clamping, and that is the safe half of an honest uncertainty:
            // if changes WERE purged, proceeding delivers a short read with nothing failing — the exact
            // failure the retention check exists for. Discriminating properly needs the instance's
            // start_lsn beside its floor (nothing was purged iff min_lsn <= start_lsn), which is one more
            // column in a batch that already runs; deliberately NOT taken here, so that a false alarm stays
            // a false alarm and never becomes a silent short read.
            // ⚠ And it names the parameter the CALLER actually passed: a timestamp bound arrives at this
            // branch as an LSN they have never seen, so reporting it as "starting_position" would send them
            // looking for a cursor they did not write.
            if (plan.StartingTimestamp is { } purgedTs)
            {
                throw new InvalidOperationException(
                    $"cdc.changes: starting_timestamp {purgedTs:yyyy-MM-dd HH:mm:ss.fff} maps to "
                    + $"{CdcChangesPlan.Hex(fromLsn)}, BELOW the retention floor "
                    + $"{CdcChangesPlan.Hex(minLsn)} of capture instance '{plan.CaptureInstance}' - so this "
                    + "read cannot answer for the range between them. Either the cleanup job removed those "
                    + "changes (in which case reading on WOULD HAVE SILENTLY SKIPPED THEM), or the capture "
                    + "instance simply did not exist that early; this read cannot tell which, so it refuses "
                    + "rather than guess. Drop the lower bound to take everything still retained, start "
                    + "from cdc.min_position('" + plan.CaptureInstance + "'), or re-baseline with "
                    + "include := 'snapshot+changes'. cdc.health() reports the retention setting.");
            }
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
        Log.LogDebug("cdc changes {Source} ({Instance}): window {From} .. {To}{Split}", plan.SourceName,
                     plan.CaptureInstance, CdcChangesPlan.Hex(fromLsn), CdcChangesPlan.Hex(toLsn),
                     split is null ? string.Empty : " split " + CdcChangesPlan.Hex(split));
        return new CdcWindow(fromLsn, toLsn, cursor, plan.EndingPosition, split);
    }


    /// <summary>
    /// Refuses the read when a DDL landed INSIDE the window — <c>on_schema_change := 'error'</c>, the
    /// default. Does nothing under <c>'ignore'</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>⚠⚠ WHAT IT CATCHES, and why silence is the worse failure (§15.11).</b> MEASURED against the
    /// rig, all three DDL kinds land in <c>cdc.ddl_history</c> with an <c>ddl_lsn</c> directly comparable to
    /// the window bounds, and <c>required_column_update = 1</c> for exactly one of them:</para>
    /// <list type="bullet">
    ///   <item><b>ADD COLUMN</b> (<c>required_column_update = 0</c>) — NOT captured by this instance, so the
    ///     read simply OMITS it. A pipeline loses a field and nothing fails. This is the case the check
    ///     exists for.</item>
    ///   <item><b>ALTER COLUMN &lt;type&gt;</b> (<c>required_column_update = 1</c>) — propagated to the
    ///     change table ASYNCHRONOUSLY (§15.6), so the type declared at bind may already be stale. The
    ///     arrival check catches it once the capture job has acted; this catches it BEFORE.</item>
    ///   <item><b>DROP COLUMN</b> (<c>0</c>) — the column stays in the change table and reads NULL from
    ///     that point, which is the mildest of the three and still a shape change worth naming.</item>
    /// </list>
    /// <para><b>⚠ IT COSTS ITS OWN ROUND TRIP, and that is forced rather than lazy.</b> It cannot join the
    /// window-resolution batch: that batch has to survive CDC being DISABLED between bind and execute (it
    /// carries the guard for exactly that), and a batch REFERENCING <c>cdc.ddl_history</c> fails at COMPILE
    /// when the schema is gone — turning a precise message into <c>Invalid object name</c>. So the check is
    /// its own statement, taken only when the window is non-empty and the mode is not <c>'ignore'</c>.</para>
    /// <para>⚠ The range is <c>(from, to]</c>: strictly after the cursor, through the window end — the same
    /// half-open span the rows themselves come from. A DDL at exactly <c>from</c> was already reflected in
    /// whatever the previous read declared.</para>
    /// <para>⚠ It reports the FIRST DDL and the COUNT rather than all of them. The remedy is the same
    /// whichever one it is, and a message that grows with the table's history stops being read.</para>
    /// </remarks>
    internal void CdcCheckSchemaDrift(CdcChangesPlan plan, CdcWindow window)
    {
        if (!plan.ChecksSchemaDrift || window.IsEmpty)
        {
            return;
        }
        // ⚠⚠ A DDL AT OR BELOW THE BOUNDARY IS ABSORBED, and skipping it is what makes a two-instance read
        // usable at all. The second capture instance exists BECAUSE of that DDL, so the union already carries
        // both shapes and _capture_instance tells the two apart — refusing there would refuse precisely the
        // window slice 7 was built to serve. A DDL AFTER the newest instance was created is a different
        // thing: nobody re-captured for it, so it is exactly what this check exists to name. MEASURED
        // 2026-08-25: the ADD and the DROP that motivated the second instance land BELOW its start_lsn, and
        // an ADD issued afterwards lands ABOVE it.
        byte[] fromLsn = window.Split is { } boundary
                         && CdcChangesPlan.CompareLsn(boundary, window.FromLsn) > 0
            ? boundary
            : window.FromLsn;
        // ⚠ DISTINCT, because cdc.ddl_history holds ONE ROW PER (DDL x capture instance) — MEASURED: with two
        // instances every DDL appears twice, INCLUDING DDLs that predate the newer instance, which SQL Server
        // back-fills onto it. Counting rows would report "2 schema changes" for one ALTER.
        // ⚠ TOP 1 ORDER BY ddl_lsn for the command, not MIN(ddl_command): MIN picks the ALPHABETICALLY first
        // statement, which need not be the one at MIN(ddl_lsn) — so the message could name an LSN and a
        // command belonging to different DDLs. Harmless while a window held one; slice 7's windows span
        // several by construction.
        const string sql =
            "SET NOCOUNT ON; " +
            "WITH d AS (SELECT DISTINCT h.ddl_lsn, CAST(h.required_column_update AS int) AS rcu, " +
            "                  CONVERT(nvarchar(300), LEFT(h.ddl_command, 300)) AS cmd " +
            "           FROM cdc.ddl_history AS h " +
            "           JOIN cdc.change_tables AS ct ON ct.source_object_id = h.source_object_id " +
            "           WHERE (ct.capture_instance = @inst OR ct.capture_instance = @inst2) " +
            "             AND h.ddl_lsn > @from_lsn AND h.ddl_lsn <= @to_lsn) " +
            "SELECT CAST((SELECT COUNT(*) FROM d) AS varchar(16)) AS ddl_count, " +
            "       (SELECT CONVERT(varchar(30), MIN(ddl_lsn), 1) FROM d) AS first_lsn, " +
            "       (SELECT CAST(MAX(rcu) AS varchar(1)) FROM d) AS any_type_change, " +
            "       (SELECT TOP 1 CAST(cmd AS varchar(300)) FROM d ORDER BY ddl_lsn) AS first_command;";
        var rows = ReadMetadataRows(sql, 4, new[]
        {
            new SqlParameter("@inst", CdcRequireResolved(plan).Instance),
            NullableParam("@inst2", plan.SecondInstance),
            CdcBinaryParam("@from_lsn", fromLsn),
            CdcBinaryParam("@to_lsn", window.ToLsn),
        });
        if (rows.Count == 0 || rows[0][0] == "0")
        {
            return;
        }
        int count = int.TryParse(rows[0][0], System.Globalization.NumberStyles.Integer,
                                 System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : 1;
        bool typeChange = rows[0][2] == "1";
        throw new InvalidOperationException(
            $"cdc.changes: {count} schema change{(count == 1 ? string.Empty : "s")} landed INSIDE this "
            + $"window on '{plan.SourceName}' - the columns this read declared may not describe all of it. "
            + $"The first is at {rows[0][1] ?? "?"}: {rows[0][3] ?? "(command not recorded)"}. "
            + (typeChange
                ? "At least one changed a CAPTURED column's TYPE, which SQL Server propagates to the change "
                  + "table asynchronously, so the declared types may already be stale. "
                : "A column ADDED after capture began is NOT captured, so this read would simply omit it; a "
                  + "DROPPED one stays in the change table and reads NULL from that point. ")
            + "Read up to the change and re-bind (a fresh statement re-reads the schema), or pass "
            + "on_schema_change := 'ignore' to read anyway."
            + (plan.IsUnion
                ? " This read already spans the boundary between capture instances "
                  + $"'{plan.CaptureInstance}' and '{plan.SecondInstance}', so the changes that produced the "
                  + "newer instance are NOT what is being reported here - these landed after it."
                : string.Empty));
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
        var parameters = new List<SqlParameter>(14);
        if (window.Split is { } split)
        {
            // ⚠⚠ THE TVF ARGUMENTS ARE CLAMPED INTO A LEGAL WINDOW, and that is what lets ONE statement,
            // built at bind, serve every position of the caller's window relative to the boundary. The TVF
            // refuses an inverted window with the unattributable 313 (§2.1), so a leg that has nothing to
            // read cannot simply be handed (split, from-below-it): it is handed a one-LSN window instead, and
            // the WHERE predicates remove what that returns. MEASURED both degenerate directions.
            byte[] toOlder = CdcChangesPlan.CompareLsn(window.ToLsn, split) < 0 ? window.ToLsn : split;
            if (CdcChangesPlan.CompareLsn(toOlder, window.FromLsn) < 0)
            {
                toOlder = window.FromLsn;
            }
            byte[] fromNewer = CdcChangesPlan.CompareLsn(window.FromLsn, split) > 0 ? window.FromLsn : split;
            byte[] toNewer = CdcChangesPlan.CompareLsn(window.ToLsn, fromNewer) < 0 ? fromNewer : window.ToLsn;
            parameters.Add(CdcBinaryParam("@from_a", window.FromLsn));
            parameters.Add(CdcBinaryParam("@to_a", toOlder));
            parameters.Add(CdcBinaryParam("@from_b", fromNewer));
            parameters.Add(CdcBinaryParam("@to_b", toNewer));
            parameters.Add(CdcBinaryParam("@split", split));
            parameters.Add(CdcBinaryParam("@win_to", window.ToLsn));
            parameters.Add(new SqlParameter("@inst_a", System.Data.SqlDbType.VarChar, 128)
            {
                Value = CdcRequireResolved(plan).Instance,
            });
            parameters.Add(new SqlParameter("@inst_b", System.Data.SqlDbType.VarChar, 128)
            {
                Value = plan.SecondInstance,
            });
            parameters.Add(new SqlParameter("@row_filter", System.Data.SqlDbType.NVarChar, 30)
            {
                Value = plan.RowFilterOption,
            });
        }
        else
        {
            parameters.Add(CdcBinaryParam("@from_lsn", window.FromLsn));
            parameters.Add(CdcBinaryParam("@to_lsn", window.ToLsn));
            parameters.Add(new SqlParameter("@row_filter", System.Data.SqlDbType.NVarChar, 30)
            {
                Value = plan.RowFilterOption,
            });
            parameters.Add(new SqlParameter("@inst_name", System.Data.SqlDbType.VarChar, 128)
            {
                Value = CdcRequireResolved(plan).Instance,
            });
        }
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
        // ⚠⚠ POOLED, ALWAYS — see RouteScan's rule 1b. It is not "we happen not to need the pin": a change
        // table is populated ASYNCHRONOUSLY by the capture job from COMMITTED log records, so a change this
        // transaction just made is not there yet on any connection, and pinning a long streaming change read
        // onto the transaction's WRITE connection is the 595 hazard for a benefit that cannot exist.
        // MEASURED before the flag: inside a transaction that had written the source this read logged
        // `route=pin (MARS)`.
        return ExecuteQuery(CdcRequireResolved(plan).Sql, parameters, readYourWrites: false,
                            materialize: false, snapshotRead: false, pooledOnly: true);
    }

    /// <summary>
    /// The capture instance and statement of a RESOLVED plan. A deferred plan reaching here is a bug in this
    /// file — <c>CdcEnableAndResolve</c> runs before either caller — so it says so rather than dereferencing
    /// a null and reporting something further away.
    /// </summary>
    private static (string Instance, string Sql) CdcRequireResolved(CdcChangesPlan plan)
    {
        if (plan.CaptureInstance is not { } instance || plan.Sql is not { } sql)
        {
            throw new InvalidOperationException(
                $"cdc.changes: internal - the plan for '{plan.Source}' was never resolved to a capture "
                + "instance. enable := true defers that to execution; reaching a read without it is a bug.");
        }
        return (instance, sql);
    }
}
