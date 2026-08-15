using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// The <c>delta</c> catalog-bound function namespace — <c>cat.delta.snapshots('schema.table')</c> and friends,
/// declared by every Delta-provider catalog (engineered-wood AND delta-rs) the way the <c>fabric</c> schema is
/// declared on a OneLake root: a synthetic FUNCTION schema advertised by the provider, refused for DDL.
/// </summary>
/// <remarks>
/// <para>These replace the eight <c>fabricator_delta_*</c> TableFunction registrations that used to live in
/// <c>fabricator_extension.cpp</c> and ride metadata kinds 8–14 — provider-specific surface hardcoded in C++,
/// with the version bounds packed as a <c>"from:to"</c> string and the CAS payload as <c>"app\nversion\nexpected"</c>.
/// Now the args are TYPED (real BIGINT/TIMESTAMP parameters), the functions live where the provider does, and
/// the catalog is implicit — the function is resolved THROUGH the attached catalog, so the old first argument
/// (the catalog name) is gone: <c>fabricator_delta_snapshots('lake','dbo.t')</c> →
/// <c>lake.delta.snapshots('dbo.t')</c>. BREAKING, no aliases (the fabricator-rename precedent).</para>
/// <para><b>Side effects run at EXECUTION, never at bind.</b> The bind runs inside the host's schema probe
/// (opener ambient set, transaction ambient NOT set — <c>arrow_ingest.cpp</c> establishes the txn only at
/// <c>InitGlobal</c>), and <see cref="CatalogFunctionSet.OutputSchema"/> binds-and-disposes without executing.
/// That is the same reason the old C++ binds for <c>get/set_transaction_version</c> deliberately skipped
/// <c>PopulateReturnSchema</c>. Hence the two binding shapes below: a FIXED schema with the core deferred to
/// each execution, and (for the two whose output schema depends on the table) a pre-opened stream whose schema
/// is peeked at bind — the exact cost the old registrations paid, since their schema probe ran the factory.</para>
/// </remarks>
public static class DeltaFunctions
{
    /// <summary>The synthetic function schema every Delta catalog advertises.</summary>
    public const string SchemaName = "delta";

    /// <summary>The fixed (app_id, version) row both transaction-version functions return. Shared with the
    /// providers' row builders so the DECLARED schema and the ACTUAL batches cannot drift.</summary>
    public static readonly Schema AppTxnSchema = new(new[]
    {
        new Field("app_id", StringType.Default, nullable: false),
        new Field("version", Int64Type.Default, nullable: true),
    }, null);

    /// <summary>The fixed (property, value) row of tblproperties/set_tblproperties. Nullabilities match the
    /// providers' TwoColumn builder (both nullable).</summary>
    public static readonly Schema PropertiesSchema = new(new[]
    {
        new Field("property", StringType.Default, nullable: true),
        new Field("value", StringType.Default, nullable: true),
    }, null);

    /// <summary>The fixed one-column row <c>delta.checkpoint</c> returns: the version checkpointed.</summary>
    public static readonly Schema CheckpointSchema = new(new[]
    {
        new Field("version", Int64Type.Default, nullable: false),
    }, null);

    /// <summary>One (app_id, version) batch against <see cref="AppTxnSchema"/>.</summary>
    public static RecordBatch AppTxnRow(string appId, long? version)
    {
        var apps = new StringArray.Builder();
        apps.Append(appId);
        var versions = new Int64Array.Builder();
        if (version is { } v)
        {
            versions.Append(v);
        }
        else
        {
            versions.AppendNull();
        }
        return new RecordBatch(AppTxnSchema, new IArrowArray[] { apps.Build(), versions.Build() }, 1);
    }
}

/// <summary>
/// <c>cat.delta.snapshots('schema.table')</c> — the table's commit history
/// (version, timestamp, operation, operation_parameters).
/// </summary>
public sealed class DeltaSnapshotsFunction : ICatalogTableFunction
{
    private readonly Func<string, IArrowArrayStream> _open;

    public DeltaSnapshotsFunction(Func<string, IArrowArrayStream> open) => _open = open;

    public string SchemaName => DeltaFunctions.SchemaName;
    public string Name => "snapshots";

    public Schema Parameters { get; } = new(new[]
    {
        Params.Positional("table", StringType.Default, nullable: false),
    }, null);

    // Schema from the pre-opened stream (both providers hand-build it, but peeking is what the retired C++
    // registration's schema probe did, and it keeps this class provider-agnostic).
    public ITableFunctionBinding Bind(RecordBatch args)
    {
        var table = FabricArgs.Str(args, 0)
            ?? throw new ArgumentException("delta.snapshots: a table is required ('schema.table').");
        return new StreamTableBinding(() => _open(table));
    }
}

/// <summary>
/// <c>cat.delta.changes('schema.table', starting_version := 0 [, ending_version := 9])</c> — the Change Data
/// Feed. The bounds are NAMED and TYPED: <c>starting_version</c>/<c>ending_version</c> BIGINTs, or
/// <c>starting_timestamp</c>/<c>ending_timestamp</c> TIMESTAMPs (<c>starting_timestamp</c> = the first version
/// committed AT OR AFTER that instant, <c>ending_timestamp</c> = the last version committed AT OR BEFORE it —
/// Delta's own semantics, and the Spark option vocabulary; UTC). Exactly one starting bound is required; the
/// ending bounds are optional (absent ⇒ latest). ⚠ NOT <c>from</c>/<c>to</c>: both are RESERVED WORDS, and a
/// named parameter that is one is a PARSER error that reads as a broken function (the <c>offset :=</c> lesson).
/// </summary>
/// <remarks>
/// ⚠ Deliberately NOT Delta's dual-typed single argument (where <c>table_changes('t','0')</c> means "since the
/// epoch", not "since version 0" — the literal's TYPE silently selects the meaning). Distinct named parameters
/// make the version/timestamp choice explicit at the call site.
/// </remarks>
public sealed class DeltaChangesFunction : ICatalogTableFunction
{
    private readonly Func<string, long?, long?, DateTime?, DateTime?, IArrowArrayStream> _open;

    public DeltaChangesFunction(Func<string, long?, long?, DateTime?, DateTime?, IArrowArrayStream> open)
        => _open = open;

    public string SchemaName => DeltaFunctions.SchemaName;
    public string Name => "changes";

    private static readonly TimestampType TsType = new(TimeUnit.Microsecond, (string?)null);

    public Schema Parameters { get; } = new(new[]
    {
        Params.Positional("table", StringType.Default, nullable: false),
        Params.Named("starting_version", Int64Type.Default),
        Params.Named("ending_version", Int64Type.Default),
        Params.Named("starting_timestamp", TsType),
        Params.Named("ending_timestamp", TsType),
    }, null);

    public ITableFunctionBinding Bind(RecordBatch args)
    {
        var table = FabricArgs.Str(args, 0)
            ?? throw new ArgumentException("delta.changes: a table is required ('schema.table').");
        long? from = FabricArgs.Int(args, 1);
        long? to = FabricArgs.Int(args, 2);
        DateTime? fromTs = FabricArgs.Ts(args, 3);
        DateTime? toTs = FabricArgs.Ts(args, 4);
        if (from is not null && fromTs is not null)
        {
            throw new ArgumentException(
                "delta.changes: give either starting_version := <version> or starting_timestamp := <timestamp>, not both.");
        }
        if (from is null && fromTs is null)
        {
            throw new ArgumentException(
                "delta.changes: a starting bound is required — starting_version := <version> or starting_timestamp := <timestamp>.");
        }
        if (to is not null && toTs is not null)
        {
            throw new ArgumentException(
                "delta.changes: give either ending_version := <version> or ending_timestamp := <timestamp>, not both.");
        }
        return new StreamTableBinding(() => _open(table, from, to, fromTs, toTs));
    }
}

/// <summary><c>cat.delta.tblproperties('schema.table')</c> — the table's <c>delta.*</c> properties.</summary>
public sealed class DeltaTblPropertiesFunction : ICatalogTableFunction
{
    private readonly Func<string, IArrowArrayStream> _read;

    public DeltaTblPropertiesFunction(Func<string, IArrowArrayStream> read) => _read = read;

    public string SchemaName => DeltaFunctions.SchemaName;
    public string Name => "tblproperties";

    public Schema Parameters { get; } = new(new[]
    {
        Params.Positional("table", StringType.Default, nullable: false),
    }, null);

    public ITableFunctionBinding Bind(RecordBatch args)
    {
        var table = FabricArgs.Str(args, 0)
            ?? throw new ArgumentException("delta.tblproperties: a table is required ('schema.table').");
        return new StreamTableBinding(DeltaFunctions.PropertiesSchema, () => _read(table));
    }
}

/// <summary><c>cat.delta.set_tblproperties('schema.table', '{"delta.isolationLevel":"Serializable"}')</c> —
/// SET/UNSET <c>delta.*</c> properties via one metaData commit (a JSON null value UNSETs). Commits at
/// EXECUTION, immediately (an administrative metadata change, like OPTIMIZE/VACUUM).</summary>
public sealed class DeltaSetTblPropertiesFunction : ICatalogTableFunction
{
    private readonly Func<string, string, IArrowArrayStream> _set;

    public DeltaSetTblPropertiesFunction(Func<string, string, IArrowArrayStream> set) => _set = set;

    public string SchemaName => DeltaFunctions.SchemaName;
    public string Name => "set_tblproperties";

    public Schema Parameters { get; } = new(new[]
    {
        Params.Positional("table", StringType.Default, nullable: false),
        Params.Positional("properties", StringType.Default, nullable: false),
    }, null);

    public ITableFunctionBinding Bind(RecordBatch args)
    {
        var table = FabricArgs.Str(args, 0)
            ?? throw new ArgumentException("delta.set_tblproperties: a table is required ('schema.table').");
        var properties = FabricArgs.Str(args, 1)
            ?? throw new ArgumentException(
                "delta.set_tblproperties: a JSON object of property->value is required, e.g. "
                + "'{\"delta.isolationLevel\":\"Serializable\"}'.");
        return new StreamTableBinding(DeltaFunctions.PropertiesSchema, () => _set(table, properties));
    }
}

/// <summary><c>cat.delta.checkpoint('schema.table')</c> — write a checkpoint for the table's CURRENT
/// version NOW, instead of waiting for a commit to land on a <c>delta.checkpointInterval</c> multiple.
/// Returns the version checkpointed. Runs at EXECUTION, immediately (an administrative action, like
/// OPTIMIZE/VACUUM — never part of a surrounding DuckDB transaction).</summary>
/// <remarks>
/// The use cases are engineered-wood's own (<c>DeltaTable.CheckpointAsync</c>): a checkpoint at a moment the
/// caller knows to be good — after a bulk load, after an OPTIMIZE, before handing the table to another
/// engine — which is what bounds the next reader's log replay. ⚠ It ALSO runs log cleanup, deleting commits
/// the new checkpoint covers that are older than the table's <c>delta.logRetentionDuration</c> (exactly as an
/// automatic checkpoint does; <c>delta.enableExpiredLogCleanup = 'false'</c> opts a table out). Not free —
/// it materialises the whole active-file set — and idempotent in effect but not in cost.
/// </remarks>
public sealed class DeltaCheckpointFunction : ICatalogTableFunction
{
    private readonly Func<string, IArrowArrayStream> _checkpoint;

    public DeltaCheckpointFunction(Func<string, IArrowArrayStream> checkpoint) => _checkpoint = checkpoint;

    public string SchemaName => DeltaFunctions.SchemaName;
    public string Name => "checkpoint";

    public Schema Parameters { get; } = new(new[]
    {
        Params.Positional("table", StringType.Default, nullable: false),
    }, null);

    public ITableFunctionBinding Bind(RecordBatch args)
    {
        var table = FabricArgs.Str(args, 0)
            ?? throw new ArgumentException("delta.checkpoint: a table is required ('schema.table').");
        return new StreamTableBinding(DeltaFunctions.CheckpointSchema, () => _checkpoint(table));
    }
}

/// <summary><c>cat.delta.get_transaction_version('schema.table', 'app')</c> — the app's committed
/// application-transaction high-water mark (the Delta <c>txn</c> action; NULL when never set).</summary>
public sealed class DeltaGetTxnVersionFunction : ICatalogTableFunction
{
    private readonly Func<string, string, IArrowArrayStream> _get;

    public DeltaGetTxnVersionFunction(Func<string, string, IArrowArrayStream> get) => _get = get;

    public string SchemaName => DeltaFunctions.SchemaName;
    public string Name => "get_transaction_version";

    public Schema Parameters { get; } = new(new[]
    {
        Params.Positional("table", StringType.Default, nullable: false),
        Params.Positional("app_id", StringType.Default, nullable: false),
    }, null);

    public ITableFunctionBinding Bind(RecordBatch args)
    {
        var table = FabricArgs.Str(args, 0)
            ?? throw new ArgumentException("delta.get_transaction_version: a table is required ('schema.table').");
        var appId = FabricArgs.Str(args, 1)
            ?? throw new ArgumentException("delta.get_transaction_version: an app_id is required.");
        return new StreamTableBinding(DeltaFunctions.AppTxnSchema, () => _get(table, appId));
    }
}

/// <summary>
/// <c>cat.delta.set_transaction_version('schema.table', 'app', 5 [, expected := 4])</c> — PARK an
/// application-transaction version on the current explicit transaction; at COMMIT it is compared-and-swapped
/// against the latest snapshot and the <c>txn</c> action commits atomically with the transaction's fused commit.
/// </summary>
/// <remarks>
/// ⚠ <c>expected</c> ABSENT means <b>must-not-exist-yet</b> (the app's FIRST batch), never "do not check" —
/// mapping absent to an unconditional write would let a replayed first batch commit twice, which is precisely
/// the duplication this mechanism exists to prevent (the engineered-wood #43 lesson: the default is the
/// dangerous answer).
/// </remarks>
public sealed class DeltaSetTxnVersionFunction : ICatalogTableFunction
{
    private readonly Func<string, string, long, long?, IArrowArrayStream> _set;

    public DeltaSetTxnVersionFunction(Func<string, string, long, long?, IArrowArrayStream> set) => _set = set;

    public string SchemaName => DeltaFunctions.SchemaName;
    public string Name => "set_transaction_version";

    public Schema Parameters { get; } = new(new[]
    {
        Params.Positional("table", StringType.Default, nullable: false),
        Params.Positional("app_id", StringType.Default, nullable: false),
        Params.Positional("version", Int64Type.Default, nullable: false),
        Params.Named("expected", Int64Type.Default),
    }, null);

    public ITableFunctionBinding Bind(RecordBatch args)
    {
        var table = FabricArgs.Str(args, 0)
            ?? throw new ArgumentException("delta.set_transaction_version: a table is required ('schema.table').");
        var appId = FabricArgs.Str(args, 1)
            ?? throw new ArgumentException("delta.set_transaction_version: an app_id is required.");
        var version = FabricArgs.Int(args, 2)
            ?? throw new ArgumentException("delta.set_transaction_version: a version is required.");
        long? expected = FabricArgs.Int(args, 3);
        return new StreamTableBinding(DeltaFunctions.AppTxnSchema, () => _set(table, appId, version, expected));
    }
}

/// <summary>
/// A table-function binding over a provider core that returns an <see cref="IArrowArrayStream"/>.
/// Two shapes:
/// <list type="bullet">
/// <item>FIXED schema (the two-arg ctor): the core runs fresh at EVERY execution and never at bind — required
/// for the side-effecting functions (<c>set_*</c>), whose bind runs inside the host's schema probe where the
/// transaction ambient is not established (and <see cref="CatalogFunctionSet.OutputSchema"/> binds-and-disposes
/// without executing at all).</item>
/// <item>PEEKED schema (the one-arg ctor): the core is opened at bind purely to read its schema — for
/// <c>snapshots</c>/<c>changes</c>, whose output columns depend on the table. The pre-opened stream is handed
/// to the FIRST execution (so the bind-time read is not wasted); a re-execution (a prepared statement) opens
/// fresh. Read-only cores only.</item>
/// </list>
/// </summary>
/// <remarks>
/// <c>Execute</c> is a PLAIN method that disposes the pushed filter values and takes the hand-off BEFORE
/// returning the iterator — an async-iterator body does not begin until the first <c>MoveNextAsync</c>, long
/// after <c>InitGlobal</c> returned, and deferring either into the iterator is the documented late-release
/// use-after-free (see <see cref="FabricTableBinding"/>).
/// </remarks>
internal sealed class StreamTableBinding : ITableFunctionBinding
{
    private readonly Func<IArrowArrayStream> _open;
    private IArrowArrayStream? _peeked;

    /// <summary>Fixed-schema, execution-deferred core (side effects allowed).</summary>
    internal StreamTableBinding(Schema schema, Func<IArrowArrayStream> open)
    {
        _open = open;
        OutputSchema = schema;
    }

    /// <summary>Schema peeked from a bind-time open (read-only cores).</summary>
    internal StreamTableBinding(Func<IArrowArrayStream> open)
    {
        _open = open;
        _peeked = open();
        OutputSchema = _peeked.Schema;
    }

    public Schema OutputSchema { get; }

    public bool SupportsFilterPushdown => false;
    public bool SupportsProjectionPushdown => false;

    public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
    {
        scan.FilterValues?.Dispose();
        var stream = Interlocked.Exchange(ref _peeked, null) ?? _open();
        return Drain(stream, ct);
    }

    private static async IAsyncEnumerable<RecordBatch> Drain(
        IArrowArrayStream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var batch = await stream.ReadNextRecordBatchAsync(ct).ConfigureAwait(false);
                if (batch is null)
                {
                    yield break;
                }
                yield return batch;
            }
        }
        finally
        {
            stream.Dispose();
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _peeked, null)?.Dispose();
    }
}
