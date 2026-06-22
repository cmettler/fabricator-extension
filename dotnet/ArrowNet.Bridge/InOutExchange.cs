using System.Runtime.CompilerServices;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace ArrowNet.Bridge;

/// <summary>
/// A bound table-in-out call for the Phase 6 streaming exchange. Produced by
/// <c>IBackendCatalog.InOutBind</c> (resolving cost args + the input-table schema) and consumed by the
/// framework pump (<see cref="InOutExchangeStream"/>). <see cref="OutputSchema"/> is the FULL output
/// (input echo ++ the function's own columns). <see cref="DoExchange"/> is the streaming transform:
/// <paramref name="input"/> yields one <see cref="RecordBatch"/> per DuckDB input chunk (ends at EOF), and
/// the returned enumerable is the output the framework maps onto DuckDB's operator contract — a non-empty
/// batch = HAVE_MORE_OUTPUT, a <b>length-0 batch</b> = NEED_MORE_INPUT (the per-input-chunk sentinel the
/// author yields), end-of-enumerable = FINISHED. One binding may run one exchange at a time; it is reused
/// across prepared re-executions and disposed when the bind is torn down.
/// </summary>
public interface IArrowInOutBinding : IDisposable
{
    Schema OutputSchema { get; }

    IAsyncEnumerable<RecordBatch> DoExchange(IAsyncEnumerable<RecordBatch> input, CancellationToken ct = default);
}

/// <summary>
/// Optional capability for a binding that runs against a SQL connection: the framework sets the configured
/// transaction isolation level for the exchange before <see cref="IArrowInOutBinding.DoExchange"/> runs, so
/// the call's one transaction sees a consistent snapshot. Pure-C# bindings need not implement it.
/// </summary>
public interface IArrowInOutIsolation
{
    string IsolationLevel { set; }
}

/// <summary>
/// The framework "pump": exposes an <see cref="IArrowInOutBinding"/>'s <c>DoExchange</c> as a pull-based Arrow
/// output stream for the C++ exchange operator. The host pulls this stream synchronously (the Arrow C-stream
/// exporter blocks on <see cref="ReadNextRecordBatchAsync"/>); each pull drives <c>DoExchange</c> one step. The
/// input stream is the host-exported input (one chunk per gate tenure, null at EOF), wrapped as an
/// <see cref="IAsyncEnumerable{T}"/>. Sentinels (length-0 batches) pass through verbatim.
/// </summary>
internal sealed class InOutExchangeStream : IArrowArrayStream
{
    private readonly IArrowInOutBinding _binding;
    private readonly IArrowArrayStream _input;   // imported from C++; owned + released here
    private readonly IAsyncEnumerator<RecordBatch> _out;
    private bool _disposed;

    public InOutExchangeStream(IArrowInOutBinding binding, IArrowArrayStream input, string isolation)
    {
        _binding = binding;
        _input = input;
        if (!string.IsNullOrEmpty(isolation) && binding is IArrowInOutIsolation iso)
        {
            iso.IsolationLevel = isolation;
        }
        _out = binding.DoExchange(ReadInput(), CancellationToken.None).GetAsyncEnumerator();
    }

    public Schema Schema => _binding.OutputSchema;

    public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
    {
        // Sync-over-async at the boundary: the C++ gate-holder blocks here while this chunk's work runs. The
        // hostfxr CLR has no SynchronizationContext, so GetResult cannot deadlock (proven by the 6.0 spike).
        bool has = _out.MoveNextAsync().AsTask().GetAwaiter().GetResult();
        return new ValueTask<RecordBatch?>(has ? _out.Current : null);
    }

    // The C++ input stream's get_next yields one chunk per gate tenure (a released/null array at end).
    private async IAsyncEnumerable<RecordBatch> ReadInput()
    {
        while (true)
        {
            var b = await _input.ReadNextRecordBatchAsync().ConfigureAwait(false);
            if (b is null)
            {
                yield break;
            }
            yield return b;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            // Runs DoExchange's finally (commit / connection close).
            _out.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // best-effort teardown
        }
        _input.Dispose();
        // The binding is reused across re-executions; it is freed by inout_bind_close, not here.
    }
}

/// <summary>
/// Adapts a custom (provider-authored, pure-C#) <see cref="IArrowTableInOutFunction"/> — whose contract is a
/// per-chunk <c>Process</c> — onto the streaming <see cref="IArrowInOutBinding"/>. A fresh function instance is
/// created per exchange (it may keep state across its input stream); the binding yields the per-chunk sentinel.
/// Custom functions run entirely in C#, so there is no connection and no isolation.
/// </summary>
public sealed class CustomInOutBinding : IArrowInOutBinding
{
    private readonly Func<IArrowTableInOutFunction> _factory;

    public CustomInOutBinding(Func<IArrowTableInOutFunction> factory)
    {
        _factory = factory;
        OutputSchema = factory().OutputSchema; // declared output schema (static); a throwaway instance reads it
    }

    public Schema OutputSchema { get; }

    public async IAsyncEnumerable<RecordBatch> DoExchange(IAsyncEnumerable<RecordBatch> input,
                                                          [EnumeratorCancellation] CancellationToken ct = default)
    {
        var fn = _factory(); // fresh instance per exchange (Process may accumulate state across chunks)
        await foreach (var chunk in input.WithCancellation(ct))
        {
            using (chunk)
            {
                foreach (var outBatch in fn.Process(chunk))
                {
                    yield return outBatch;
                }
            }
            yield return InOutExchange.EmptyBatch(OutputSchema); // per-chunk sentinel (NEED_MORE_INPUT)
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>Helpers shared by the in-out exchange path.</summary>
public static class InOutExchange
{
    /// <summary>A length-0 <see cref="RecordBatch"/> matching <paramref name="schema"/> — the exchange sentinel
    /// the host reads as NEED_MORE_INPUT.</summary>
    public static RecordBatch EmptyBatch(Schema schema)
    {
        var arrays = new IArrowArray[schema.FieldsList.Count];
        for (int i = 0; i < arrays.Length; i++)
        {
            arrays[i] = BuildEmptyArray(schema.FieldsList[i].DataType);
        }
        return new RecordBatch(schema, arrays, 0);
    }

    private static IArrowArray BuildEmptyArray(IArrowType type) => type.TypeId switch
    {
        ArrowTypeId.Boolean => new BooleanArray.Builder().Build(),
        ArrowTypeId.Int8 => new Int8Array.Builder().Build(),
        ArrowTypeId.Int16 => new Int16Array.Builder().Build(),
        ArrowTypeId.Int32 => new Int32Array.Builder().Build(),
        ArrowTypeId.Int64 => new Int64Array.Builder().Build(),
        ArrowTypeId.UInt8 => new UInt8Array.Builder().Build(),
        ArrowTypeId.UInt16 => new UInt16Array.Builder().Build(),
        ArrowTypeId.UInt32 => new UInt32Array.Builder().Build(),
        ArrowTypeId.UInt64 => new UInt64Array.Builder().Build(),
        ArrowTypeId.Float => new FloatArray.Builder().Build(),
        ArrowTypeId.Double => new DoubleArray.Builder().Build(),
        ArrowTypeId.String => new StringArray.Builder().Build(),
        ArrowTypeId.Binary => new BinaryArray.Builder().Build(),
        ArrowTypeId.Decimal128 => new Decimal128Array.Builder((Decimal128Type)type).Build(),
        ArrowTypeId.Date32 => new Date32Array.Builder().Build(),
        ArrowTypeId.Date64 => new Date64Array.Builder().Build(),
        ArrowTypeId.Timestamp => new TimestampArray.Builder((TimestampType)type).Build(),
        ArrowTypeId.Time32 => new Time32Array.Builder((Time32Type)type).Build(),
        ArrowTypeId.Time64 => new Time64Array.Builder((Time64Type)type).Build(),
        _ => throw new NotSupportedException(
            $"mssql_net: in-out exchange sentinel does not support output column type {type.Name}"),
    };
}
