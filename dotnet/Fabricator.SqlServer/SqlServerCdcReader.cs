// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Fabricator.Bridge;

namespace Fabricator.SqlServer;

/// <summary>
/// <c>db.cdc.changes(...)</c> — slice 3 of docs/mssql-cdc.md §15: THE READER, at its smallest correct size.
/// One capture instance, <c>images := 'after'</c>, explicit bounds, the §2.1 retention pre-check, and the
/// 21-byte resume position of §2.4.
/// </summary>
/// <remarks>
/// <para><b>⚠⚠ IT READS THE TVF, NEVER THE CHANGE TABLE, AND SQL SERVER SAYS SO IN METADATA</b> (§15.2).
/// The seven <c>cdc</c> metadata tables, the four placeholder functions AND the change table itself are
/// <c>is_ms_shipped = 1</c>; the generated per-instance TVFs are the ONLY <c>is_ms_shipped = 0</c> objects in
/// that schema. The TVF also VALIDATES its window where a direct table read silently returns whatever
/// survived — which for a pipeline that must not lose rows is the worse failure.</para>
/// <para><b>⚠⚠ AND IT IS MARSHALED C#, NOT A SQL REWRITE</b> (§15.1, user-directed: <i>"don't rely on the
/// duckdb catalog to access the sql server cdc functions"</i>). Routing through DISCOVERED objects would
/// fail when an ATTACH <c>table_filter</c>/<c>schema_filter</c> hides them, and would make every
/// <c>cdc.enable</c> require a catalog rebuild before the reader could see what it just created. ⚠ A THIRD
/// option nearly won and is recorded in §15.1 — emitting <c>FROM db.cdc.fn_cdc_get_all_changes_x(...)</c>
/// MEASURED as binding, pushing projection AND filters down, and excluding op 3 for free. What kills it is
/// §5's snapshot protocol, which needs TWO connections at different isolation levels with a lock spanning a
/// specific window; a single generated statement cannot express that, because locks are held to end of
/// transaction.</para>
/// <para><b>What that costs, stated rather than buried: projection and filter pushdown into the change
/// read.</b> Acceptable HERE because the window IS the filter and the TVF is already bounded by its
/// arguments — a caller's extra <c>WHERE customer = 'acme'</c> is a secondary filter over an
/// already-bounded window.</para>
/// <para><b>⚠ The declared schema comes from a DESCRIBE of the very statement it is about to run</b>
/// (<c>SqlServerCatalog.DescribeQuery</c>, i.e. <c>CommandBehavior.SchemaOnly</c> through the same
/// <c>SqlArrowMapping.ToArrowField</c> the reader itself uses). So bind and execute cannot disagree through a
/// hand-written type table — and the execute path re-checks the arrival types anyway, because §15.6 MEASURED
/// that a change table's schema is NOT frozen: an <c>ALTER COLUMN &lt;type&gt;</c> IS propagated,
/// asynchronously, by the capture job.</para>
/// </remarks>
internal sealed class CdcChangesFunction : ICatalogTableFunction
{
    private readonly SqlServerCatalog _catalog;

    internal CdcChangesFunction(SqlServerCatalog catalog) => _catalog = catalog;

    public string SchemaName => SqlServerCdcFunctions.SchemaName;

    public string Name => "changes";

    /// <summary>
    /// One positional source, everything else NAMED — DuckDB positional table arguments have no defaults.
    /// </summary>
    /// <remarks>
    /// <para>⚠ <c>starting_position</c>/<c>ending_position</c>, never <c>from</c>/<c>to</c>: a named parameter
    /// that is a RESERVED WORD is a PARSER error, which reads as a broken function. The tree has paid for that
    /// twice already (<c>offset :=</c>, then <c>delta.changes</c>).</para>
    /// <para>⚠ <c>images</c> is declared although slice 3 implements only its default. A caller who writes
    /// <c>images := 'both'</c> gets a sentence saying it is not built yet, rather than DuckDB's
    /// "invalid named parameter" — and the vocabulary (§3.2's table) is pinned now rather than invented later.
    /// </para>
    /// <para>⚠ <c>commit_timestamp</c> is a PARAMETER rather than a projected column, and that is forced
    /// (§11 item 2). <c>_commit_timestamp</c> is the ONE output column needing
    /// <c>LEFT JOIN cdc.lsn_time_mapping</c>, and MEASURED: DuckDB does NOT eliminate an unused LEFT JOIN —
    /// not even with a PRIMARY KEY on the right side — so "emit it always and let projection pushdown prune
    /// it" would make every caller pay two scans. The emitter runs at bind; a projection is applied after it.
    /// </para>
    /// <para><b>⚠⚠ <c>enable := true</c> CAPTURES THE TABLE IF IT IS NOT CAPTURED, and the DDL happens at
    /// EXECUTE, not at bind (§15.7).</b> That is what keeps <c>EXPLAIN</c>, <c>DESCRIBE</c> and
    /// <c>CREATE VIEW</c> side-effect-free, and it is affordable only because the declared schema can be
    /// derived from the SOURCE table: a default <c>sp_cdc_enable_table</c> captures every source column, so
    /// at the instant we enable, captured == source (MEASURED). ⚠ It is a real DDL — it creates a change
    /// table and two table-valued functions — so a call that performs it reports
    /// <see cref="ITableFunctionBinding.SchemaMayChange"/> like the setup functions do.</para>
    /// <para><b>⚠ NO <c>max_rows</c> — WITHDRAWN 2026-08-25 rather than deferred (§3.2).</b> It adds no
    /// capability: "stop early" is DuckDB's own <c>LIMIT</c>, and bounded RESUMABLE pagination is
    /// <c>LIMIT</c> + <c>ORDER BY _position</c> + a cursor from <c>max(_position)</c>, MEASURED end to end.
    /// It would also be worse than that recipe — a row count can SPLIT A TRANSACTION (§11 item 5), so it
    /// would have to round down to a transaction boundary and then would not return the number of rows its
    /// name promises. The recipe has the same caveat, visibly and in the caller's hands.</para>
    /// </remarks>
    public Schema Parameters { get; } = new(new[]
    {
        Params.Positional("source", StringType.Default, nullable: false),
        Params.Named("starting_position", BinaryType.Default),
        Params.Named("ending_position", BinaryType.Default),
        Params.Named("capture_instance", StringType.Default),
        Params.Named("images", StringType.Default),
        Params.Named("commit_timestamp", BooleanType.Default),
        Params.Named("enable", BooleanType.Default),
        Params.Named("on_schema_change", StringType.Default),
        Params.Named("include", StringType.Default),
        Params.Named("starting_timestamp", new TimestampType(TimeUnit.Microsecond, (string?)null)),
        Params.Named("ending_timestamp", new TimestampType(TimeUnit.Microsecond, (string?)null)),
    }, metadata: null);

    public ITableFunctionBinding Bind(RecordBatch args)
    {
        // Read every argument HERE: the stream they were imported from is disposed when tablefn_bind returns.
        string? source = CdcEnableFunction.Str(args, 0);
        byte[]? startingPosition = Blob(args, 1);
        byte[]? endingPosition = Blob(args, 2);
        string? instance = CdcEnableFunction.Str(args, 3);
        string images = CdcEnableFunction.Str(args, 4) ?? CdcChangesPlan.ImagesAfter;
        bool commitTimestamp = CdcEnableFunction.Bool(args, 5) ?? false;
        bool enable = CdcEnableFunction.Bool(args, 6) ?? false;
        string onSchemaChange = CdcEnableFunction.Str(args, 7) ?? CdcChangesPlan.OnSchemaChangeError;
        string include = CdcEnableFunction.Str(args, 8) ?? CdcChangesPlan.IncludeChanges;
        DateTime? startingTimestamp = Ts(args, 9);
        DateTime? endingTimestamp = Ts(args, 10);

        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException(
                "cdc.changes: a source is required, e.g. cdc.changes('dbo.orders'). It may be a "
                + "<schema>.<table> name or a capture-instance name; SELECT * FROM <catalog>.cdc.tables() "
                + "lists what is captured.");
        }
        images = NormalizeImages(images);
        onSchemaChange = NormalizeOnSchemaChange(onSchemaChange);
        include = NormalizeInclude(include);
        ValidateOneBoundPerSide(startingPosition, endingPosition, startingTimestamp, endingTimestamp);
        ValidateBoundsAgainstInclude(include, startingPosition, endingPosition, startingTimestamp,
                                     endingTimestamp);
        return new CdcChangesBinding(
            _catalog,
            _catalog.CdcBindChanges(source!, instance, commitTimestamp, enable, onSchemaChange, include,
                                    images,
                                    CdcChangesPlan.ValidatePosition(startingPosition, "starting_position"),
                                    CdcChangesPlan.ValidatePosition(endingPosition, "ending_position"),
                                    startingTimestamp, endingTimestamp));
    }

    /// <summary>
    /// Accepts the three <c>include</c> shapes case-insensitively and returns the canonical spelling.
    /// </summary>
    private static string NormalizeInclude(string include)
    {
        foreach (string known in new[]
                 {
                     CdcChangesPlan.IncludeChanges,
                     CdcChangesPlan.IncludeSnapshot,
                     CdcChangesPlan.IncludeSnapshotChanges,
                 })
        {
            if (string.Equals(include, known, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }
        throw new ArgumentException(
            $"cdc.changes: include := '{include}' is not a value - this release accepts 'changes' (the "
            + "default: the captured change rows in a window), 'snapshot' (the whole table as of a "
            + "consistent instant, with one handoff position) and 'snapshot+changes' (both, in that order, "
            + "with no gap and no duplicate between them).");
    }

    /// <summary>
    /// ⚠ A snapshot IS the starting point, so asking for one AND a cursor is a contradiction rather than a
    /// combination — and it is refused rather than resolved.
    /// </summary>
    /// <remarks>
    /// <para>Reading the two together could only mean one of two things, and neither is what anybody wants:
    /// a snapshot of the CURRENT table paired with changes from an OLD cursor delivers every change between
    /// them TWICE, and a snapshot paired with a cursor AHEAD of it silently loses the changes in between.
    /// The caller who has a cursor wants <c>include := 'changes'</c>; the caller who wants to start over
    /// wants the snapshot and takes its handoff position from the rows it returns.</para>
    /// <para>⚠ <c>ending_position</c> is refused for <c>'snapshot'</c> alone for a different reason: a
    /// snapshot has no window to bound. It IS accepted with <c>'snapshot+changes'</c>, where it bounds the
    /// changes half.</para>
    /// </remarks>
    private static void ValidateBoundsAgainstInclude(string include, byte[]? startingPosition,
                                                     byte[]? endingPosition,
                                                     DateTime? startingTimestamp, DateTime? endingTimestamp)
    {
        if (string.Equals(include, CdcChangesPlan.IncludeChanges, StringComparison.Ordinal))
        {
            return;
        }
        if (startingTimestamp is not null)
        {
            throw new ArgumentException(
                $"cdc.changes: include := '{include}' cannot be combined with starting_timestamp - a "
                + "snapshot IS the starting point, and it is taken at an instant this read chooses (the "
                + "handoff), not at one the caller names. Read the snapshot without a lower bound and resume "
                + "from the _position its rows carry, or drop the snapshot and read include := 'changes' "
                + "from your timestamp.");
        }
        if (endingTimestamp is not null
            && string.Equals(include, CdcChangesPlan.IncludeSnapshot, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "cdc.changes: include := 'snapshot' cannot be combined with ending_timestamp - a snapshot is "
                + "the table as of one instant, not a window, so there is no upper bound to apply. Use "
                + "include := 'snapshot+changes', where ending_timestamp bounds the changes half.");
        }
        if (startingPosition is not null)
        {
            throw new ArgumentException(
                $"cdc.changes: include := '{include}' cannot be combined with starting_position - a snapshot "
                + "IS the starting point, so a cursor beside it would either replay every change since that "
                + "cursor (it is already in the snapshot) or skip the ones before it. Read the snapshot "
                + "without a cursor and resume from the _position its rows carry, or drop the snapshot and "
                + "read include := 'changes' from your cursor.");
        }
        if (endingPosition is not null
            && string.Equals(include, CdcChangesPlan.IncludeSnapshot, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "cdc.changes: include := 'snapshot' cannot be combined with ending_position - a snapshot is "
                + "the table as of one instant, not a window, so there is no upper bound to apply. Use "
                + "include := 'snapshot+changes', where ending_position bounds the changes half.");
        }
    }

    /// <summary>
    /// Accepts <c>'after'</c> and <c>'both'</c> case-insensitively and returns the canonical spelling.
    /// </summary>
    /// <remarks>
    /// <para>⚠ There is deliberately NO <c>'net'</c> here and there never will be (§1.7d): the collapse is
    /// lossy, schedule-dependent, unresumable, and reproducible in one line of DuckDB with a
    /// MEASURED-identical outcome. Naming it in a refusal would advertise a mode we have decided not to
    /// have.</para>
    /// <para>⚠ <c>'both'</c> is the only way to see a row's BEFORE image, and it carries §1.5's MAX-column
    /// trap with it: an <c>UPDATE</c> that did not touch a <c>varchar(max)</c> / <c>nvarchar(max)</c> /
    /// <c>varbinary(max)</c> column does not record that column in the BEFORE image, so it reads NULL there
    /// whatever the row held. That is why the mode also emits <c>_update_mask</c> — the mask bit is what
    /// separates "not recorded" from "genuinely NULL", and no substituted placeholder could.</para>
    /// </remarks>
    private static string NormalizeImages(string images)
    {
        foreach (string known in new[] { CdcChangesPlan.ImagesAfter, CdcChangesPlan.ImagesBoth })
        {
            if (string.Equals(images, known, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }
        throw new ArgumentException(
            $"cdc.changes: images := '{images}' is not a value - this release accepts 'after' (the default: "
            + "one row per change - insert, update_postimage, delete) and 'both' (which adds the "
            + "update_preimage row of every update, plus an _update_mask column).");
    }

    /// <summary>
    /// Accepts <c>'error'</c> (the default) and <c>'ignore'</c>; names the modes that are designed but not
    /// built rather than calling them invalid.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>resync</c> is §15.9's PREFERRED answer to schema drift and cannot exist before the snapshot leg
    /// — it re-captures and re-snapshots, which is a heavy privileged act. <c>fill</c> and <c>null</c> both
    /// need the two-instance alignment of §15.8. Refusing them BY NAME is what stops a caller reading the
    /// design note and concluding the reader is broken.
    /// </remarks>
    private static string NormalizeOnSchemaChange(string mode)
    {
        foreach (string known in new[]
                 {
                     CdcChangesPlan.OnSchemaChangeError,
                     CdcChangesPlan.OnSchemaChangeIgnore,
                     CdcChangesPlan.OnSchemaChangeResync,
                     CdcChangesPlan.OnSchemaChangeNull,
                     CdcChangesPlan.OnSchemaChangeFill,
                 })
        {
            if (string.Equals(mode, known, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }
        const bool designed = false;
        _ = designed;
        throw new ArgumentException(
            $"cdc.changes: on_schema_change := '{mode}' is not a value - it accepts 'error' (refuse when a "
            + "DDL landed inside the window - the default), 'ignore' (read anyway), 'resync' (re-capture and "
            + "re-baseline), 'null' (project an uncaptured column as NULL) and 'fill' (look it up from the "
            + "live source by key).");
    }

    /// <summary>
    /// Refuses two lower bounds, or two upper bounds, in one call.
    /// </summary>
    /// <remarks>
    /// <para><b>⚠ A POSITION AND A TIMESTAMP ARE NOT THE SAME KIND OF BOUND, which is exactly why holding
    /// both is a question rather than a combination.</b> A <c>_position</c> is a RESUME TOKEN — the caller
    /// has already seen that row, so it is EXCLUSIVE — while a timestamp is a WALL-CLOCK INSTANT the caller
    /// has seen nothing of, so it is INCLUSIVE. Silently preferring either one would give a window whose
    /// edge means something the caller did not ask for, and "the tighter of the two" is not a rule anybody
    /// could predict.</para>
    /// <para>⚠ Refused per SIDE, not per call: <c>starting_position</c> with <c>ending_timestamp</c> is a
    /// perfectly good window (resume from a cursor, stop at an hour) and is deliberately allowed.</para>
    /// </remarks>
    private static void ValidateOneBoundPerSide(byte[]? startingPosition, byte[]? endingPosition,
                                                DateTime? startingTimestamp, DateTime? endingTimestamp)
    {
        if (startingPosition is not null && startingTimestamp is not null)
        {
            throw new ArgumentException(
                "cdc.changes: starting_position and starting_timestamp are two lower bounds and only one "
                + "can govern. They are not interchangeable either: a position is a resume token, so it is "
                + "EXCLUSIVE (the row it names has been read), while a timestamp is an instant, so it is "
                + "INCLUSIVE (changes committed at or after it). Pass whichever one you mean.");
        }
        if (endingPosition is not null && endingTimestamp is not null)
        {
            throw new ArgumentException(
                "cdc.changes: ending_position and ending_timestamp are two upper bounds and only one can "
                + "govern. Both are INCLUSIVE; pass whichever one you mean.");
        }
    }

    /// <summary>
    /// A TIMESTAMP argument as a wall-clock <see cref="DateTime"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ NAIVE on both sides, and that is the contract this bound has to state out loud: DuckDB's
    /// <c>TIMESTAMP</c> carries no zone, and the <c>tran_end_time</c> it is compared against is the SQL
    /// Server host's LOCAL clock. So the bound means "that instant, on the server" — a caller passing
    /// <c>now()</c> from a machine in another zone names a different moment than they think.
    /// </remarks>
    private static DateTime? Ts(RecordBatch? args, int col)
    {
        if (args is null || col >= args.ColumnCount || args.Length == 0)
        {
            return null;
        }
        // ⚠⚠ BOTH SHAPES, and the second one is not defensive — it is what actually arrives. MEASURED:
        // ArrowValueReader.ReadScalar hands back a DateTimeOffset for a TIMESTAMP whose Arrow type carries NO
        // timezone, even though its own comment says a DateTime. C#'s conditional operator unifies its two
        // branches, and there IS an implicit DateTime -> DateTimeOffset conversion (not the reverse), so
        // `cond ? ts.UtcDateTime : ts` has the natural type DateTimeOffset and the DateTime branch is
        // converted straight back. A plain `as DateTime?` therefore yields NULL — and the bound was then
        // SILENTLY IGNORED rather than refused, which is how this was found: the parameter bound, the query
        // ran, and every window came back unbounded.
        return ArrowValueReader.ReadScalar(args.Column(col), 0) switch
        {
            // ⚠ `.DateTime`, never `.UtcDateTime`: the caller wrote a NAIVE wall-clock and it is compared
            // against tran_end_time, which is the server's local clock. They are the same value today
            // (DuckDB stamps +00:00 on a tz-less TIMESTAMP, MEASURED across three session TimeZones), and
            // `.DateTime` is the one that stays right if that ever stops being true.
            DateTimeOffset offset => offset.DateTime,
            DateTime naive => naive,
            _ => null,
        };
    }

    private static byte[]? Blob(RecordBatch? args, int col)
    {
        if (args is null || col >= args.ColumnCount || args.Length == 0)
        {
            return null;
        }
        return ArrowValueReader.ReadScalar(args.Column(col), 0) as byte[];
    }
}

/// <summary>
/// Everything <c>cdc.changes</c> resolved at BIND: the capture instance, the declared output schema, and the
/// SQL text to run. Immutable — one plan may serve several executions of one prepared statement.
/// </summary>
internal sealed class CdcChangesPlan
{
    internal const string ImagesAfter = "after";

    /// <summary>Emit the BEFORE image of every update too, plus the <c>_update_mask</c> that decodes it.</summary>
    /// <remarks>
    /// <b>⚠⚠ IT CARRIES §1.5's MAX-COLUMN TRAP, and the mask is the whole answer to it.</b> A
    /// <c>varchar(max)</c> / <c>nvarchar(max)</c> / <c>varbinary(max)</c> column an <c>UPDATE</c> did not
    /// touch is NOT STORED in that update's before image — MEASURED, and it is the BEFORE image that loses
    /// the value while the AFTER image keeps it, which is the opposite of the obvious guess. From the value
    /// alone "SQL Server did not record it" and "the row held NULL" are indistinguishable. We do NOT
    /// substitute a placeholder: a placeholder is a value, so it is itself indistinguishable from a row that
    /// genuinely holds it, and inventing data to signal missing data is the failure this file keeps
    /// recording. Emitting the mask makes it DECIDABLE instead — the same answer <c>_capture_instance</c>
    /// gives for a snapshot row, by the same rule.
    /// </remarks>
    internal const string ImagesBoth = "both";

    /// <summary>Refuse the read when a DDL landed inside the window — the DEFAULT.</summary>
    /// <remarks>
    /// <b>⚠ LOUD BEFORE CLEVER (§15.11), and it is a deliberate trade.</b> The check costs ONE extra round
    /// trip per non-empty read, and a window containing a DDL is uncommon for a polling consumer and normal
    /// for a first read over a long retention window. Silence is the worse failure here: a column ADDED
    /// mid-window is not captured, so the read simply omits it and a pipeline loses a field without anything
    /// failing. A caller who has decided they do not care passes <c>'ignore'</c>, which also buys the round
    /// trip back.
    /// </remarks>
    internal const string OnSchemaChangeError = "error";

    /// <summary>Read anyway, and skip the check (and its round trip).</summary>
    internal const string OnSchemaChangeIgnore = "ignore";

    /// <summary>
    /// Re-capture and re-baseline when the capture instance no longer matches the source (slice 8b, §22).
    /// </summary>
    /// <remarks>
    /// <para><b>⚠⚠ IT KEYS ON A DIFFERENT SIGNAL FROM THE OTHER TWO MODES, and that is the design rather
    /// than an inconsistency.</b> <c>'error'</c> and <c>'ignore'</c> answer "did a DDL land INSIDE this
    /// window?", which is a question about a range of LSNs. <c>'resync'</c> answers "does this capture
    /// instance still describe the source?", which is a question about METADATA and needs no window at all —
    /// so it is decided at BIND, where the declared output schema can still be the new one. Deciding it at
    /// execute would mean widening a schema mid-statement, which the arrival check correctly refuses.</para>
    /// <para><b>⚠ Only an ADDED column is fixable by a new instance</b> (§15.11). A type change propagates
    /// to the change table on its own and a DROP leaves the column reading NULL; neither is repaired by
    /// re-capturing. So when nothing is stale, <c>'resync'</c> falls through to exactly what
    /// <c>'error'</c> does — loud about what it cannot fix rather than silently doing nothing.</para>
    /// <para><b>⚠ It FORCES the snapshot leg.</b> A new instance starts capturing NOW, so reading its
    /// changes alone would silently begin at the resync instant and lose everything before it. Re-baselining
    /// is the whole point: the snapshot carries the state, and its handoff joins the new instance's stream
    /// with no gap.</para>
    /// </remarks>
    internal const string OnSchemaChangeResync = "resync";

    /// <summary>Project an uncaptured source column as NULL — §15.9's "floor": honest, decidable, never silent.</summary>
    /// <remarks>
    /// ⚠ It is decidable ONLY because <c>_capture_instance</c> is on every row: the NULL says "this
    /// instance did not capture this column", which a consumer can tell from a genuine NULL by asking
    /// which instance produced the row. Without that column §15.8 would have refused the whole idea.
    /// </remarks>
    internal const string OnSchemaChangeNull = "null";

    /// <summary>Look an uncaptured column up from the LIVE source by key — a TORN ROW, by design.</summary>
    /// <remarks>
    /// <b>⚠⚠ THE VALUE IS AS OF NOW, NOT AS OF THE CHANGE, AND NO LATER EVENT EVER CORRECTS IT</b>
    /// (§15.9). The column is not captured, so nothing will ever emit a change for it. This is not
    /// eventual consistency; it is a value from a different instant, permanently. It also needs a key
    /// and adds a read of the live source to a read that was capture-layer-only.
    /// </remarks>
    internal const string OnSchemaChangeFill = "fill";

    /// <summary>The TVF's <c>@row_filter_option</c> for <see cref="ImagesAfter"/>. Excludes op 3 for free.</summary>
    internal const string RowFilterAll = "all";

    /// <summary>The TVF's <c>@row_filter_option</c> for <see cref="ImagesBoth"/> — op 3 rows included.</summary>
    internal const string RowFilterAllUpdateOld = "all update old";

    /// <summary>The captured change rows in a window — the default, and everything before slice 8.</summary>
    internal const string IncludeChanges = "changes";

    /// <summary>The whole table as of one consistent instant, with the handoff position on every row.</summary>
    internal const string IncludeSnapshot = "snapshot";

    /// <summary>The snapshot, then the changes after it — §5's two-connection protocol.</summary>
    internal const string IncludeSnapshotChanges = "snapshot+changes";

    /// <summary>An LSN is 10 bytes; a <c>_position</c> is <c>start_lsn ‖ seqval ‖ operation</c> = 21 (§2.4).</summary>
    internal const int LsnBytes = 10;

    internal const int PositionBytes = 21;

    internal CdcChangesPlan(string source, string? explicitInstance, bool commitTimestamp,
                            string onSchemaChange, string include, string images, Schema output,
                            byte[]? startingPosition, byte[]? endingPosition,
                            DateTime? startingTimestamp = null, DateTime? endingTimestamp = null,
                            string? captureInstance = null, string? sourceSchema = null,
                            string? sourceTable = null, string? sql = null,
                            string? secondInstance = null, string? snapshotSql = null,
                            string? resyncFrom = null, string? resyncTo = null,
                            IReadOnlyDictionary<string, IReadOnlyList<string>>? maskColumns = null)
    {
        ResyncFrom = resyncFrom;
        ResyncTo = resyncTo;
        MaskColumns = maskColumns;
        Source = source;
        ExplicitInstance = explicitInstance;
        CommitTimestamp = commitTimestamp;
        OnSchemaChange = onSchemaChange;
        Include = include;
        Images = images;
        StartingTimestamp = startingTimestamp;
        EndingTimestamp = endingTimestamp;
        CaptureInstance = captureInstance;
        SecondInstance = secondInstance;
        SourceSchema = sourceSchema;
        SourceTable = sourceTable;
        Output = output;
        Sql = sql;
        SnapshotSql = snapshotSql;
        StartingPosition = startingPosition;
        EndingPosition = endingPosition;
    }

    /// <summary>Which halves this read delivers — <c>changes</c>, <c>snapshot</c> or both (§5).</summary>
    internal string Include { get; }

    /// <summary>Which images a change row set carries — <c>after</c> (the default) or <c>both</c>.</summary>
    internal string Images { get; }

    /// <summary>True when this read emits <c>update_preimage</c> rows and the <c>_update_mask</c> column.</summary>
    internal bool HasUpdateMask => string.Equals(Images, ImagesBoth, StringComparison.Ordinal);

    /// <summary>The TVF's <c>@row_filter_option</c> for this read's <see cref="Images"/>.</summary>
    internal string RowFilterOption => HasUpdateMask ? RowFilterAllUpdateOld : RowFilterAll;

    /// <summary>
    /// An INCLUSIVE lower bound in wall-clock time, resolved to an LSN at execute by
    /// <c>sys.fn_cdc_map_time_to_lsn</c>. Null unless the caller passed one.
    /// </summary>
    /// <remarks>
    /// <b>⚠ INCLUSIVE where <see cref="StartingPosition"/> is EXCLUSIVE, and the asymmetry is the point.</b>
    /// A position is a resume token — the caller has read the row it names — so the next window must start
    /// strictly after it. A timestamp is an instant the caller has read nothing of, so "changes at or after
    /// it" is the only reading that does not silently drop whatever committed exactly then. Treating the two
    /// alike would be the bug, which is why holding both on one side is refused rather than reconciled.
    /// </remarks>
    internal DateTime? StartingTimestamp { get; }

    /// <summary>An INCLUSIVE upper bound in wall-clock time. Null unless the caller passed one.</summary>
    internal DateTime? EndingTimestamp { get; }

    /// <summary>True when this read takes an initial snapshot of the SOURCE table.</summary>
    internal bool HasSnapshot => !string.Equals(Include, IncludeChanges, StringComparison.Ordinal);

    /// <summary>True when this read delivers captured change rows.</summary>
    internal bool HasChanges => !string.Equals(Include, IncludeSnapshot, StringComparison.Ordinal);

    /// <summary>
    /// The SELECT over the SOURCE table that the snapshot leg streams, with the metadata columns rendered as
    /// literals so it produces the SAME declared schema as the change read. Null unless
    /// <see cref="HasSnapshot"/>.
    /// </summary>
    internal string? SnapshotSql { get; }

    /// <summary>
    /// The 21-byte <c>_position</c> a snapshot row carries: the handoff LSN with every following byte
    /// <c>0xFF</c>, i.e. "past everything at this LSN".
    /// </summary>
    /// <remarks>
    /// <para><b>⚠⚠ THE PADDING IS 0xFF AND IT MUST NOT BE ZERO.</b> The handoff LSN is the capture
    /// watermark at the pin, so every change AT it is already in the snapshot and must NOT be re-delivered.
    /// Our cursor predicate is <c>lsn &gt; cur OR (lsn = cur AND (seq &gt; cur_seq OR (seq = cur_seq AND op
    /// &gt; cur_op)))</c>, so a zero-padded position would ADMIT every row at that LSN — a duplicate per
    /// transaction in the handoff instant. Padding with <c>0xFF</c> makes both tails unsatisfiable, which is
    /// exactly "everything at or below this LSN has been delivered".</para>
    /// <para>⚠ It is a SYNTHETIC position: no change row has <c>seqval = 0xFF…FF</c>. That is fine and it is
    /// what a handoff needs — it is a CURSOR, not a row identity, and §2.3 records that the cursor must be
    /// DATA the caller can store and hand back. It round-trips through <c>starting_position</c> unchanged.
    /// </para>
    /// </remarks>
    internal static byte[] HandoffPosition(byte[] lsn)
    {
        var position = new byte[PositionBytes];
        System.Array.Copy(lsn, position, LsnBytes);
        for (int i = LsnBytes; i < PositionBytes; i++)
        {
            position[i] = 0xFF;
        }
        return position;
    }

    /// <summary>What the caller named — kept so a DEFERRED plan can re-resolve itself at execute.</summary>
    internal string Source { get; }

    internal string? ExplicitInstance { get; }

    internal bool CommitTimestamp { get; }

    internal string OnSchemaChange { get; }

    /// <summary>Whether this read checks for a DDL inside its window before returning rows.</summary>
    /// <remarks>
    /// ⚠ TRUE under <c>'resync'</c> too, deliberately. A resync repairs exactly one thing — a column the
    /// capture instance never captured — and a window may contain a DDL it cannot repair (a type change, a
    /// drop). Falling through to the <c>'error'</c> check is what keeps the mode from silently swallowing
    /// those. ⚠ On a plan that DID resync the check runs against the NEW instance's window, where by
    /// construction nothing has drifted yet.
    /// </remarks>
    internal bool ChecksSchemaDrift =>
        !string.Equals(OnSchemaChange, OnSchemaChangeIgnore, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this read EXPRESSES a change to the captured column SET, so the in-window check need only
    /// refuse for a drift it cannot express.
    /// </summary>
    /// <remarks>
    /// <para><b>⚠⚠ WITHOUT THIS, <c>'null'</c> AND <c>'fill'</c> ARE USELESS — they project the added column
    /// and then the drift check refuses the read anyway.</b> Measured the moment they first ran: the
    /// declaration was correct, <c>region</c> was there and typed, and the statement failed with "1 schema
    /// change landed INSIDE this window". The check keys on <c>OnSchemaChange != 'ignore'</c>, which was
    /// right while every mode either refused or ignored, and stopped being right the moment a mode ANSWERED
    /// the drift instead.</para>
    /// <para><b>⚠ It absorbs a column-SET change and nothing else.</b> <c>required_column_update = 1</c> — a
    /// captured column's TYPE changed — is still refused, because the projection says nothing about it: the
    /// declared type came from a describe taken BEFORE the capture job propagated the change, and §15.13
    /// records that window as the single most useful unmeasured number in this feature. An ADD and a DROP
    /// are both expressed (the added column is projected; a dropped one is appended from the change table,
    /// which still answers for it), so both are absorbed.</para>
    /// </remarks>
    internal bool AbsorbsColumnSetChanges =>
        string.Equals(OnSchemaChange, OnSchemaChangeNull, StringComparison.Ordinal)
        || string.Equals(OnSchemaChange, OnSchemaChangeFill, StringComparison.Ordinal);

    /// <summary>
    /// The capture instance a <c>resync</c> is superseding — null unless this bind decided to resync.
    /// </summary>
    /// <remarks>
    /// <b>⚠ ITS PRESENCE IS THE DECISION, and the decision is made at BIND.</b> The declared output schema
    /// comes from the SOURCE on such a plan (the shape the NEW instance will capture), so by the time
    /// execute runs there is nothing left to decide — only the DDL to perform. That is the same split
    /// <c>enable := true</c> uses (§17): bind stays side-effect-free, so an <c>EXPLAIN</c> over a resync
    /// plan captures nothing.
    /// </remarks>
    internal string? ResyncFrom { get; }

    /// <summary>The capture-instance name the resync will CREATE at execute.</summary>
    /// <remarks>
    /// ⚠ Fixed at BIND rather than derived at execute, so the statement's effect is fully determined by the
    /// plan. <see cref="SqlServerCdcSetup.ResyncCaptureInstance"/> picks whichever of the table's two
    /// canonical names the surviving instance is not using.
    /// </remarks>
    internal string? ResyncTo { get; }

    /// <summary>True when this bind decided to re-capture and re-baseline.</summary>
    internal bool IsResync => ResyncFrom is not null;

    /// <summary>
    /// Capture instance → its captured column names in <c>column_ordinal</c> order, which is what decodes
    /// <c>_update_mask</c> into <c>_changed_columns</c>. Null unless <see cref="HasUpdateMask"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>⚠ KEYED BY INSTANCE, because a mask is only decodable against the one that produced it.</b>
    /// A two-instance read unions two change tables whose ordinals need not agree, and the aligned output
    /// order is neither instance's — so a single list would name the WRONG columns for one of the two legs.
    /// <c>_capture_instance</c> is what picks the map per row.</para>
    /// <para><b>⚠ ORDINAL <c>i</c> IS THE <c>i</c>-th CAPTURED COLUMN, MEASURED rather than assumed.</b> An
    /// explicit <c>@captured_column_list = 'd,b,a'</c> on a four-column table comes back as ordinals
    /// 1=a, 2=b, 3=d — SQL Server normalises to the source's own column order — and the TVF emits a, b, d in
    /// that same order. So the declared source columns ARE the map for a one-instance read, with no extra
    /// round trip; the union path takes it from <c>cdc.captured_columns</c>, already ordered.</para>
    /// </remarks>
    internal IReadOnlyDictionary<string, IReadOnlyList<string>>? MaskColumns { get; }

    /// <summary>
    /// Null on a DEFERRED plan — <c>enable := true</c> over a table that is not captured yet, where the
    /// declared schema came from the SOURCE and the instance does not exist until execute (§15.7).
    /// </summary>
    internal string? CaptureInstance { get; }

    /// <summary>
    /// The NEWER capture instance when this read spans a two-instance boundary (slice 7, §19); null for the
    /// ordinary one-instance read. <see cref="CaptureInstance"/> is then the OLDER one.
    /// </summary>
    /// <remarks>
    /// <para><b>⚠⚠ THE ORDER IS THE CONTRACT, not a label.</b> The split is the NEWER instance's retention
    /// floor and the partition is "older strictly below it, newer at or above it", so swapping the two would
    /// read the newer instance for the range only the older one covers — and the newer one does not have those
    /// rows, so the result would be SHORT rather than wrong-looking. They are ordered by <c>start_lsn</c>
    /// (the semantic discriminator: a second instance's <c>start_lsn</c> IS the boundary) with
    /// <c>create_date</c> as the tiebreak, because the cleanup job raises BOTH floors and they converge.</para>
    /// </remarks>
    internal string? SecondInstance { get; }

    /// <summary>True when this read unions two capture instances across their boundary.</summary>
    internal bool IsUnion => SecondInstance is not null;

    internal string? SourceSchema { get; }

    internal string? SourceTable { get; }

    /// <summary>True once a capture instance is known and <see cref="Sql"/> is built.</summary>
    internal bool IsResolved => CaptureInstance is not null;

    /// <summary>
    /// <c>schema.table</c> for the diagnostic log. ⚠ Worth keeping beside the capture instance rather than
    /// logging the instance alone: a default enable now generates an OPAQUE name (<c>fab_&lt;hash&gt;</c>),
    /// at which point a line naming only the instance tells an operator nothing.
    /// </summary>
    internal string SourceName => SourceSchema is null ? Source : SourceSchema + "." + SourceTable;

    internal Schema Output { get; }

    /// <summary>
    /// The statement to execute, with the cursor predicate already folded in. Null while
    /// <see cref="IsResolved"/> is false.
    /// </summary>
    internal string? Sql { get; }

    internal byte[]? StartingPosition { get; }

    internal byte[]? EndingPosition { get; }

    /// <summary>
    /// A bound is either a 10-byte LSN or a 21-byte <c>_position</c>; anything else is refused AT BIND.
    /// </summary>
    /// <remarks>
    /// <para><b>⚠ BOTH LENGTHS ARE REQUIRED, not a convenience.</b> §3.4's documented cursor idiom stores
    /// <c>cdc.max_position()</c> — a 10-byte LSN — and passes it back as <c>starting_position</c>, while a
    /// row's own <c>_position</c> is 21. Accepting only one of them would break the idiom the docs teach.</para>
    /// <para>Refusing at BIND rather than at execute is the earliest point the value exists: these are
    /// constant arguments, so a typo fails before any server round trip.</para>
    /// </remarks>
    internal static byte[]? ValidatePosition(byte[]? value, string parameter)
    {
        if (value is null)
        {
            return null;
        }
        if (value.Length != LsnBytes && value.Length != PositionBytes)
        {
            throw new ArgumentException(
                $"cdc.changes: {parameter} is {value.Length} bytes; it must be either a 10-byte log sequence "
                + "number (what cdc.max_position() and cdc.min_position() return) or a 21-byte _position from "
                + "a previous row of this function.");
        }
        return value;
    }

    /// <summary>The 10-byte LSN part of a bound of either length.</summary>
    internal static byte[] LsnOf(byte[] position)
    {
        if (position.Length == LsnBytes)
        {
            return position;
        }
        var lsn = new byte[LsnBytes];
        System.Array.Copy(position, 0, lsn, 0, LsnBytes);
        return lsn;
    }

    /// <summary>The seqval part of a 21-byte position.</summary>
    internal static byte[] SeqOf(byte[] position)
    {
        var seq = new byte[LsnBytes];
        System.Array.Copy(position, LsnBytes, seq, 0, LsnBytes);
        return seq;
    }

    /// <summary>The operation part of a 21-byte position.</summary>
    internal static int OpOf(byte[] position) => position[PositionBytes - 1];

    /// <summary>
    /// Unsigned bytewise comparison of two LSNs — the order SQL Server assigns and the order DuckDB gives a
    /// BLOB (MEASURED, §2.4, including across the <c>0x7F</c>/<c>0x80</c> boundary where a SIGNED comparison
    /// would invert). ⚠ Never use <c>sbyte</c> semantics here; that inversion is silent.
    /// </summary>
    internal static int CompareLsn(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i])
            {
                return a[i] < b[i] ? -1 : 1;
            }
        }
        return a.Length.CompareTo(b.Length);
    }

    /// <summary>
    /// Whether an LSN is all zero bytes — which is what SQL Server answers for a capture instance it does not
    /// know, and it is NOT a harmless sentinel.
    /// </summary>
    /// <remarks>
    /// <b>⚠⚠ MEASURED 2026-08-25: <c>sys.fn_cdc_get_min_lsn(&lt;unknown&gt;)</c> and
    /// <c>sys.fn_cdc_get_min_lsn(NULL)</c> both return <c>0x0000000000000000000</c>, NOT NULL.</b> Zero is a
    /// well-formed LSN that compares BELOW every real one, so it passes the retention pre-check trivially and
    /// hands the window to the TVF — the misleading 313 that §2.1 exists to prevent. On the two-instance path
    /// it is worse than misleading: a zero SPLIT puts every row in the newer leg and SILENTLY DROPS every
    /// pre-boundary change. It is distinguishable from the genuinely transient NULL floor of §1.6a, so the two
    /// get different answers.
    /// </remarks>
    internal static bool IsZeroLsn(byte[] value)
    {
        foreach (byte b in value)
        {
            if (b != 0)
            {
                return false;
            }
        }
        return true;
    }

    internal static string Hex(byte[] value)
    {
        var sb = new StringBuilder(2 + (value.Length * 2));
        sb.Append("0x");
        foreach (byte b in value)
        {
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}

/// <summary>The window one execution reads, after the §2.1 pre-check has passed.</summary>
internal sealed class CdcWindow
{
    internal CdcWindow(byte[] fromLsn, byte[] toLsn, byte[]? startingPosition, byte[]? endingPosition,
                       byte[]? split = null)
    {
        FromLsn = fromLsn;
        ToLsn = toLsn;
        StartingPosition = startingPosition;
        EndingPosition = endingPosition;
        Split = split;
    }

    internal byte[] FromLsn { get; }

    internal byte[] ToLsn { get; }

    internal byte[]? StartingPosition { get; }

    internal byte[]? EndingPosition { get; }

    /// <summary>
    /// The boundary between two capture instances — the NEWER one's retention floor — on a union read; null
    /// on the ordinary one-instance read.
    /// </summary>
    /// <remarks>
    /// <b>⚠⚠ IT IS DERIVED, because SQL Server does not record it.</b> MEASURED (§2.2, re-confirmed
    /// 2026-08-25): <c>cdc.change_tables.end_lsn</c> is NULL for BOTH instances, so the older one's stop
    /// position exists nowhere and has to be computed as the newer one's start. Using
    /// <c>fn_cdc_get_min_lsn</c> rather than <c>change_tables.start_lsn</c> is deliberate and buys retention
    /// correctness for free: the cleanup job RAISES that floor as it purges, and the purged range is exactly
    /// the range the newer instance can no longer answer for — which the older instance still covers.
    /// </remarks>
    internal byte[]? Split { get; }

    /// <summary>
    /// The window that reads nothing, without touching the server. ⚠ It is a legitimate STATE, not a failure:
    /// a polling consumer whose cursor sits at the window end reaches it on every quiet tick, and the TVF
    /// itself answers an inverted window with the unattributable 313 (MEASURED, §2.1).
    /// </summary>
    internal static CdcWindow Empty { get; } = new(System.Array.Empty<byte>(), System.Array.Empty<byte>(), null, null);

    internal bool IsEmpty => FromLsn.Length == 0;
}

/// <summary>
/// The binding: it resolves the window and runs the §2.1 pre-check EAGERLY in <see cref="Execute"/>, then
/// streams the TVF read.
/// </summary>
/// <remarks>
/// <para>⚠ The window is resolved at EXECUTE, not at bind, and §15.7 records why: bind must be
/// side-effect-free and must not answer for the moment a plan was built. It also fixes §3.4's determinism
/// complaint about defaulting <c>ending_position</c> at bind.</para>
/// <para>⚠ Two standing rules are load-bearing here, both paid for elsewhere in this tree with real defects:
/// the pushed filter values are disposed in the PLAIN method (an async-iterator body does not begin until the
/// host's first <c>get_next</c>, by which time the producer that owns them may be released), and the ambient
/// transaction id is captured at BIND and re-established in <see cref="Execute"/>, because a binding may be
/// executed on a thread where the <c>AsyncLocal</c> reads 0.</para>
/// </remarks>
internal sealed class CdcChangesBinding : ITableFunctionBinding
{
    private readonly SqlServerCatalog _catalog;
    private readonly Schema _declared;
    private readonly long _txnId;
    private CdcChangesPlan _plan;
    private IArrowArrayStream? _stream;
    private IArrowArrayStream? _snapshot;
    private bool _enabled;

    internal CdcChangesBinding(SqlServerCatalog catalog, CdcChangesPlan plan)
    {
        _catalog = catalog;
        _plan = plan;
        // ⚠ The DECLARED schema is fixed at BIND and never re-derived, even when the capture instance is
        // created at execute. It is the contract with arrow_ingest, and the arrival check below is what
        // proves the source-derived declaration matched what the TVF really returns.
        _declared = plan.Output;
        _txnId = AmbientTransaction.Current;
    }

    public Schema OutputSchema => _declared;

    /// <summary>
    /// True only when THIS execution created a capture instance. ⚠ It is set in the EAGER part of
    /// <see cref="Execute"/>, because the host reads it the moment <c>tablefn_execute</c> returns — an
    /// async-iterator body has not begun by then, so a DDL placed there would happen with the flag already
    /// read as false and the catalog never rebuilt.
    /// </summary>
    public bool SchemaMayChange => _enabled;

    /// <summary>
    /// False, and it is the honest answer rather than a gap: this binding hands DuckDB every column of the
    /// window and re-applies nothing. §15.1 names it as the price of the marshaled reader.
    /// </summary>
    public bool SupportsFilterPushdown => false;

    public bool SupportsProjectionPushdown => false;

    public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
    {
        scan.FilterValues?.Dispose();
        // A binding can be executed more than once (a prepared statement re-run), so release the previous
        // execution's reader before opening another - otherwise the first one is orphaned until GC.
        Dispose();
        if (AmbientTransaction.Current == 0 && _txnId != 0)
        {
            AmbientTransaction.Current = _txnId;
        }
        // ⚠ THE DEFERRED ENABLE, and it belongs HERE for two independent reasons: bind must stay
        // side-effect-free (an EXPLAIN must not capture a table), and SchemaMayChange is read the moment
        // this method returns.
        // ⚠ The resync branch is FIRST: a resync plan is also unresolved (its instance does not exist yet),
        // so falling through to the ordinary enable would run a DEFAULT enable — which keys on the TABLE,
        // finds the stale instance, creates nothing, and silently reads exactly what the resync was asked to
        // replace.
        bool justCreated = false;
        if (_plan.IsResync)
        {
            (_plan, justCreated) = _catalog.CdcResyncAndResolve(_plan);
            _enabled |= justCreated;
        }
        else if (!_plan.IsResolved)
        {
            (_plan, justCreated) = _catalog.CdcEnableAndResolve(_plan);
            _enabled |= justCreated;
        }
        // ⚠⚠ THE SNAPSHOT LEG RUNS FIRST AND EVERYTHING AFTER IT DEPENDS ON ITS RESULT — the handoff
        // position is chosen INSIDE the lock window, so the change half's lower bound does not exist until
        // this has run. It is also the only part of this method that takes a lock on the source table, and
        // it has released it by the time it returns (§5.2).
        byte[]? handoff = null;
        if (_plan.HasSnapshot)
        {
            (handoff, var rawSnapshot) = _catalog.CdcOpenSnapshot(_plan);
            _snapshot = Decorate(rawSnapshot);
            try
            {
                CheckArrivedSchema(_declared, _snapshot.Schema);
            }
            catch
            {
                Dispose();
                throw;
            }
            if (!_plan.HasChanges)
            {
                var only = _snapshot;
                return Stream(only);
            }
        }
        // EAGERLY: the pre-check's whole job is to replace the unattributable 313 with a sentence, and an
        // error raised here fails the statement instead of arriving mid-scan.
        CdcWindow window;
        try
        {
            window = _catalog.CdcResolveWindow(_plan, justCreated, handoff);
            if (!window.IsEmpty)
            {
                // EAGERLY too, and BEFORE the read: refusing after rows have started arriving would leave a
                // consumer holding a partial window it has no way to distinguish from a complete one.
                _catalog.CdcCheckSchemaDrift(_plan, window);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
        if (window.IsEmpty)
        {
            // ⚠ On a snapshot+changes read this is the ORDINARY quiet case, not a dead end: nothing has
            // changed since the pin, so the snapshot alone IS the answer.
            return _snapshot is { } snapshotOnly ? Stream(snapshotOnly) : Empty();
        }
        IArrowArrayStream stream;
        try
        {
            stream = Decorate(_catalog.CdcExecuteChanges(_plan, window));
        }
        catch
        {
            Dispose();
            throw;
        }
        try
        {
            // Also EAGERLY: a type that moved under us must fail the STATEMENT, not arrive as a mid-scan
            // "failed to read next batch from stream" wrapping our sentence.
            CheckArrivedSchema(_declared, stream.Schema);
        }
        catch
        {
            stream.Dispose();
            Dispose();
            throw;
        }
        // ⚠ THE BINDING OWNS IT, and that is not belt-and-braces. The stream is opened HERE, eagerly, while
        // the `using` that releases it lives in an async ITERATOR — and an iterator that is never enumerated
        // never runs its finally at all. A host that binds, executes and then releases without pulling a
        // single batch (a plan discarded, a statement cancelled) would leave a SQL Server reader and its
        // connection open until the GC finalized them. The iterator still disposes on the ordinary path;
        // Release() is idempotent, so both firing is correct.
        _stream = stream;
        return _snapshot is { } first ? Stream(first, stream) : Stream(stream);
    }

    /// <summary>
    /// Wraps a raw read so it also carries <c>_changed_columns</c>, when <c>images := 'both'</c> asked for
    /// the mask.
    /// </summary>
    /// <remarks>
    /// <para>⚠ BOTH legs go through it — the snapshot's rows have no mask, so they get a NULL list, which is
    /// what keeps the two halves of a <c>snapshot+changes</c> read one shape.</para>
    /// <para>⚠ The decorator derives its schema from the INNER stream, so the arrival check below still
    /// compares the declaration against what SQL Server actually returned rather than against itself.</para>
    /// </remarks>
    private IArrowArrayStream Decorate(IArrowArrayStream inner) =>
        _plan.HasUpdateMask && _plan.MaskColumns is { Count: > 0 } maskColumns
            ? new CdcChangedColumnsStream(inner, maskColumns)
            : inner;

    private static async IAsyncEnumerable<RecordBatch> Empty()
    {
        await System.Threading.Tasks.Task.CompletedTask;
        yield break;
    }

    /// <summary>
    /// The two legs of an <c>include := 'snapshot+changes'</c> read, in order and as ONE stream.
    /// </summary>
    /// <remarks>
    /// <para>⚠ ORDER IS SEMANTIC, not presentation. The snapshot is the state as of the handoff and the
    /// changes are what happened after it, so a consumer applying the rows in the order they arrive converges
    /// on the current state. Reversed, a delete followed by its own baseline row would resurrect it.</para>
    /// <para>⚠ Both legs are already open when this begins: every check that can refuse the read has run, so
    /// nothing here can fail after the first batch has been handed over.</para>
    /// </remarks>
    private static async IAsyncEnumerable<RecordBatch> Stream(IArrowArrayStream first,
                                                              IArrowArrayStream second)
    {
        await foreach (var batch in Stream(first).ConfigureAwait(false))
        {
            yield return batch;
        }
        await foreach (var batch in Stream(second).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <summary>
    /// Streams the TVF read, after checking that what ARRIVED still matches what was DECLARED at bind.
    /// </summary>
    /// <remarks>
    /// <para><b>⚠⚠ THE TYPE CHECK IS REQUIRED BY A MEASUREMENT, not by caution (§15.6).</b> The change
    /// table's schema is NOT frozen at capture-instance creation — an <c>ALTER COLUMN &lt;type&gt;</c> on the
    /// source IS propagated to it, ASYNCHRONOUSLY, by the capture job
    /// (<c>cdc.ddl_history.required_column_update = 1</c>). So a column declared <c>decimal(9,2)</c> at bind
    /// can be <c>decimal(18,4)</c> by execute, and <c>123456789.1234</c> would then be a conversion failure or
    /// a silent corruption. Failing loudly is the rule; converting is not.</para>
    /// <para>⚠ NAMES AND TYPES ONLY — never nullability. The declared schema takes each source column's
    /// nullability from the SOURCE table while the change table reports everything as optional (§1.2, where
    /// <c>id INT NOT NULL PK</c> is MEASURED nullable in the change table), so a nullability comparison would
    /// fail on every well-formed read. It is invisible at the boundary anyway: the Arrow C stream exports the
    /// DECLARED schema and the batches carry only arrays, so only the array TYPES have to agree.</para>
    /// </remarks>
    private static async IAsyncEnumerable<RecordBatch> Stream(IArrowArrayStream stream)
    {
        using (stream)
        {
            while (true)
            {
                var batch = await stream.ReadNextRecordBatchAsync().ConfigureAwait(false);
                if (batch is null)
                {
                    yield break;
                }
                yield return batch;
            }
        }
    }

    private static void CheckArrivedSchema(Schema declared, Schema arrived)
    {
        if (declared.FieldsList.Count != arrived.FieldsList.Count)
        {
            throw new InvalidOperationException(
                $"cdc.changes: the read returned {arrived.FieldsList.Count} columns where bind declared "
                + $"{declared.FieldsList.Count}. The capture instance changed between BIND and EXECUTE - "
                + "re-run the statement.");
        }
        for (int i = 0; i < declared.FieldsList.Count; i++)
        {
            var d = declared.FieldsList[i];
            var a = arrived.FieldsList[i];
            if (!string.Equals(d.Name, a.Name, StringComparison.Ordinal) || !SameType(d.DataType, a.DataType))
            {
                throw new InvalidOperationException(
                    $"cdc.changes: column {i + 1} was declared '{d.Name}' {Describe(d.DataType)} at bind and "
                    + $"arrived as '{a.Name}' {Describe(a.DataType)}. A captured column's TYPE changed while "
                    + "this statement was running - SQL Server's capture job propagates an ALTER COLUMN to "
                    + "the change table asynchronously. Re-run the statement; the new type is read at bind.");
            }
        }
    }

    /// <summary>
    /// STRUCTURAL Arrow type equality.
    /// </summary>
    /// <remarks>
    /// <para><b>⚠⚠ <c>IArrowType.Equals</c> IS REFERENCE EQUALITY, and using it made the check fire on every
    /// well-formed read.</b> Apache.Arrow does not override <c>Equals</c> on its type classes, so two
    /// separately constructed <c>Decimal128Type(18, 4)</c> instances are unequal — which is exactly what the
    /// describe and the execute produce, one per crossing. Caught by RUNNING it: the first smoke test refused
    /// its own correct read with <i>"declared 'amount' decimal128 … arrived as 'amount' decimal128"</i>,
    /// a message comparing two renderings that were identical. Singletons such as
    /// <c>StringType.Default</c> would have masked it, which is why a decimal column in the probe mattered.
    /// </para>
    /// <para>⚠ A NESTED type compares its children rather than falling through to "same TypeId ⇒ same type".
    /// SQL Server's mapping produces none today, so that arm is unreachable — and leaving it as a blanket
    /// <c>true</c> would make the one case where a silent mismatch is most likely the one case not checked.
    /// </para>
    /// </remarks>
    private static bool SameType(IArrowType a, IArrowType b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a.TypeId != b.TypeId)
        {
            return false;
        }
        switch (a, b)
        {
            case (Decimal128Type x, Decimal128Type y):
                return x.Precision == y.Precision && x.Scale == y.Scale;
            case (Decimal256Type x, Decimal256Type y):
                return x.Precision == y.Precision && x.Scale == y.Scale;
            case (TimestampType x, TimestampType y):
                return x.Unit == y.Unit && string.Equals(x.Timezone, y.Timezone, StringComparison.Ordinal);
            case (Time32Type x, Time32Type y):
                return x.Unit == y.Unit;
            case (Time64Type x, Time64Type y):
                return x.Unit == y.Unit;
            case (FixedSizeBinaryType x, FixedSizeBinaryType y):
                return x.ByteWidth == y.ByteWidth;
            case (NestedType x, NestedType y):
                if (x.Fields.Count != y.Fields.Count)
                {
                    return false;
                }
                for (int i = 0; i < x.Fields.Count; i++)
                {
                    if (!string.Equals(x.Fields[i].Name, y.Fields[i].Name, StringComparison.Ordinal)
                        || !SameType(x.Fields[i].DataType, y.Fields[i].DataType))
                    {
                        return false;
                    }
                }
                return true;
            default:
                return true;
        }
    }

    /// <summary>
    /// A type rendered WITH its parameters. ⚠ <c>IArrowType.Name</c> alone renders <c>decimal(9,2)</c> and
    /// <c>decimal(18,4)</c> identically as <c>decimal128</c> — which is precisely the difference this check
    /// exists to report, so the message has to say more than the name.
    /// </summary>
    private static string Describe(IArrowType type) => type switch
    {
        Decimal128Type d => $"decimal128({d.Precision},{d.Scale})",
        Decimal256Type d => $"decimal256({d.Precision},{d.Scale})",
        TimestampType t => $"timestamp[{t.Unit}{(string.IsNullOrEmpty(t.Timezone) ? string.Empty : ", " + t.Timezone)}]",
        Time32Type t => $"time32[{t.Unit}]",
        Time64Type t => $"time64[{t.Unit}]",
        FixedSizeBinaryType f => $"fixed_size_binary({f.ByteWidth})",
        _ => type.Name,
    };

    /// <summary>
    /// ⚠ A binding may be EXECUTED more than once (a prepared statement), so this is also called from the
    /// top of <see cref="Execute"/> — a second execution must not orphan the first one's reader.
    /// </summary>
    public void Dispose()
    {
        // ⚠ The SNAPSHOT leg is released too, and it matters more than the change leg does: it holds an open
        // SNAPSHOT transaction, so leaving it to the GC would keep a tempdb version store alive for a read
        // nobody is consuming.
        var snapshot = _snapshot;
        _snapshot = null;
        var stream = _stream;
        _stream = null;
        try
        {
            snapshot?.Dispose();
        }
        finally
        {
            stream?.Dispose();
        }
    }
}
