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
    /// <para>⚠ NO <c>max_rows</c> in slice 3, though §3.2 lists it. A truncated read breaks the cursor idiom:
    /// the caller must then advance to <c>max(_position)</c> rather than to the window end, which is exactly
    /// the trap §3.4 exists to warn about. It belongs with a story about resuming a partial window, not with
    /// the smallest correct reader.</para>
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

        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException(
                "cdc.changes: a source is required, e.g. cdc.changes('dbo.orders'). It may be a "
                + "<schema>.<table> name or a capture-instance name; SELECT * FROM <catalog>.cdc.tables() "
                + "lists what is captured.");
        }
        ValidateImages(images);
        return new CdcChangesBinding(
            _catalog,
            _catalog.CdcBindChanges(source!, instance, commitTimestamp, enable,
                                    CdcChangesPlan.ValidatePosition(startingPosition, "starting_position"),
                                    CdcChangesPlan.ValidatePosition(endingPosition, "ending_position")));
    }

    /// <summary>
    /// Accepts <c>'after'</c> and refuses everything else — distinguishing "not built yet" from "not a mode".
    /// </summary>
    /// <remarks>
    /// ⚠ There is deliberately NO <c>'net'</c> here and there never will be (§1.7d): the collapse is lossy,
    /// schedule-dependent, unresumable, and reproducible in one line of DuckDB with a MEASURED-identical
    /// outcome. Naming it in a refusal would advertise a mode we have decided not to have.
    /// </remarks>
    private static void ValidateImages(string images)
    {
        if (string.Equals(images, CdcChangesPlan.ImagesAfter, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        throw new ArgumentException(
            string.Equals(images, "both", StringComparison.OrdinalIgnoreCase)
                ? "cdc.changes: images := 'both' is not implemented yet - this release reads after-images "
                  + "only. It needs the update mask to tell an unrecorded MAX column from a genuine NULL, "
                  + "which is a later slice."
                : $"cdc.changes: images := '{images}' is not a value - the only value this release accepts is "
                  + "'after' (one row per change: insert, update_postimage, delete).");
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

    /// <summary>The TVF's <c>@row_filter_option</c> for <see cref="ImagesAfter"/>. Excludes op 3 for free.</summary>
    internal const string RowFilterAll = "all";

    /// <summary>An LSN is 10 bytes; a <c>_position</c> is <c>start_lsn ‖ seqval ‖ operation</c> = 21 (§2.4).</summary>
    internal const int LsnBytes = 10;

    internal const int PositionBytes = 21;

    internal CdcChangesPlan(string source, string? explicitInstance, bool commitTimestamp, Schema output,
                            byte[]? startingPosition, byte[]? endingPosition,
                            string? captureInstance = null, string? sourceSchema = null,
                            string? sourceTable = null, string? sql = null)
    {
        Source = source;
        ExplicitInstance = explicitInstance;
        CommitTimestamp = commitTimestamp;
        CaptureInstance = captureInstance;
        SourceSchema = sourceSchema;
        SourceTable = sourceTable;
        Output = output;
        Sql = sql;
        StartingPosition = startingPosition;
        EndingPosition = endingPosition;
    }

    /// <summary>What the caller named — kept so a DEFERRED plan can re-resolve itself at execute.</summary>
    internal string Source { get; }

    internal string? ExplicitInstance { get; }

    internal bool CommitTimestamp { get; }

    /// <summary>
    /// Null on a DEFERRED plan — <c>enable := true</c> over a table that is not captured yet, where the
    /// declared schema came from the SOURCE and the instance does not exist until execute (§15.7).
    /// </summary>
    internal string? CaptureInstance { get; }

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
    internal CdcWindow(byte[] fromLsn, byte[] toLsn, byte[]? startingPosition, byte[]? endingPosition)
    {
        FromLsn = fromLsn;
        ToLsn = toLsn;
        StartingPosition = startingPosition;
        EndingPosition = endingPosition;
    }

    internal byte[] FromLsn { get; }

    internal byte[] ToLsn { get; }

    internal byte[]? StartingPosition { get; }

    internal byte[]? EndingPosition { get; }

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
        bool justCreated = false;
        if (!_plan.IsResolved)
        {
            (_plan, justCreated) = _catalog.CdcEnableAndResolve(_plan);
            _enabled |= justCreated;
        }
        // EAGERLY: the pre-check's whole job is to replace the unattributable 313 with a sentence, and an
        // error raised here fails the statement instead of arriving mid-scan.
        var window = _catalog.CdcResolveWindow(_plan, justCreated);
        if (window.IsEmpty)
        {
            return Empty();
        }
        var stream = _catalog.CdcExecuteChanges(_plan, window);
        try
        {
            // Also EAGERLY: a type that moved under us must fail the STATEMENT, not arrive as a mid-scan
            // "failed to read next batch from stream" wrapping our sentence.
            CheckArrivedSchema(_declared, stream.Schema);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
        // ⚠ THE BINDING OWNS IT, and that is not belt-and-braces. The stream is opened HERE, eagerly, while
        // the `using` that releases it lives in an async ITERATOR — and an iterator that is never enumerated
        // never runs its finally at all. A host that binds, executes and then releases without pulling a
        // single batch (a plan discarded, a statement cancelled) would leave a SQL Server reader and its
        // connection open until the GC finalized them. The iterator still disposes on the ordinary path;
        // Release() is idempotent, so both firing is correct.
        _stream = stream;
        return Stream(stream);
    }

    private static async IAsyncEnumerable<RecordBatch> Empty()
    {
        await System.Threading.Tasks.Task.CompletedTask;
        yield break;
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
        var stream = _stream;
        _stream = null;
        stream?.Dispose();
    }
}
