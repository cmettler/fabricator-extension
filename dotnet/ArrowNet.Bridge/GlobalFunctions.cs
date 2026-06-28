using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace ArrowNet.Bridge;

/// <summary>
/// The process-wide registry of connection-free GLOBAL functions — the union, across all registered providers,
/// of <c>IBackend.GlobalScalarFunctions</c>. Built once (lazily) and keyed by name (case-insensitive). Used for
/// the <b>handle-0</b> path of the scalar ABI entries (<c>get_function_param_schema</c> /
/// <c>get_function_return_schema</c> / <c>execute_scalar</c>, where a 0 handle means "global, by name") and
/// enumerated by <c>list_global_functions</c> at extension load. A duplicate name across providers is a fatal
/// config error. See docs/global-functions.md.
/// </summary>
public static class GlobalFunctions
{
    private static readonly Lazy<IReadOnlyDictionary<string, IScalarFunction>> ScalarMap =
        new(BuildScalars, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<IReadOnlyDictionary<string, IInOutFunction>> InOutMap =
        new(() => Build<IInOutFunction>(b => b.GlobalInOutFunctions, f => f.Name, "in-out"),
            LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<IReadOnlyDictionary<string, ICollectorTableFunction>> CollectorMap =
        new(() => Build<ICollectorTableFunction>(b => b.GlobalCollectorFunctions, f => f.Name, "collector"),
            LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>All declared global scalar functions (the provider union), for <c>list_global_functions</c>.</summary>
    public static IReadOnlyCollection<IScalarFunction> AllScalars() => (IReadOnlyCollection<IScalarFunction>)ScalarMap.Value.Values;

    /// <summary>All declared global in-out functions, for <c>list_global_functions</c>.</summary>
    public static IReadOnlyCollection<IInOutFunction> AllInOut() => (IReadOnlyCollection<IInOutFunction>)InOutMap.Value.Values;

    /// <summary>All declared global collector functions, for <c>list_global_functions</c>.</summary>
    public static IReadOnlyCollection<ICollectorTableFunction> AllCollectors() => (IReadOnlyCollection<ICollectorTableFunction>)CollectorMap.Value.Values;

    /// <summary>Resolve a global scalar by name (case-insensitive); throws if none is registered.</summary>
    public static IScalarFunction ResolveScalar(string name) =>
        ScalarMap.Value.TryGetValue(name, out var fn)
            ? fn
            : throw new ArgumentException($"arrownet: no global scalar function '{name}'");

    /// <summary>Bind a global in-out OR collector by name (the handle-0 inout_bind path). A collector is wrapped
    /// in a <see cref="CollectorInOutBinding"/> so it flows through the shared exchange marshaling.</summary>
    public static IArrowInOutBinding ResolveInOut(string name, RecordBatch? args, Schema inputSchema)
    {
        if (InOutMap.Value.TryGetValue(name, out var io))
        {
            return io.Bind(args, inputSchema);
        }
        if (CollectorMap.Value.TryGetValue(name, out var co))
        {
            return new CollectorInOutBinding(co.Bind(args, inputSchema));
        }
        throw new ArgumentException($"arrownet: no global in-out/collector function '{name}'");
    }

    private static IReadOnlyDictionary<string, T> Build<T>(Func<IBackend, IEnumerable<T>> select,
                                                           Func<T, string> nameOf, string kind)
    {
        var map = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var backend in BackendRegistry.All())
        {
            foreach (var fn in select(backend))
            {
                if (!map.TryAdd(nameOf(fn), fn))
                {
                    throw new InvalidOperationException(
                        $"arrownet: duplicate global {kind} function name '{nameOf(fn)}' across providers");
                }
            }
        }
        return map;
    }

    private static IReadOnlyDictionary<string, IScalarFunction> BuildScalars() =>
        Build<IScalarFunction>(b => b.GlobalScalarFunctions, f => f.Name, "scalar");

    /// <summary>
    /// Runs a scalar function over an Arrow argument stream — one output batch per non-empty input batch, the
    /// result column typed by the function's <see cref="IScalarFunction.Result"/>. Shared by the global
    /// execute_scalar (handle-0) path; mirrors the catalog scalar loop. Consumes + disposes <paramref name="args"/>.
    /// </summary>
    public static IArrowArrayStream ExecuteScalar(IScalarFunction fn, IArrowArrayStream args)
    {
        using var input = args;
        var batches = new List<RecordBatch>();
        Schema? resultSchema = null;
        RecordBatch? inBatch;
        while ((inBatch = input.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()) is not null)
        {
            if (inBatch.Length == 0)
            {
                continue;
            }
            var resultArray = fn.Invoke(inBatch);
            resultSchema ??= new Schema(new[] { new Field("result", resultArray.Data.DataType, nullable: true) }, null);
            batches.Add(new RecordBatch(resultSchema, new[] { resultArray }, resultArray.Length));
        }
        resultSchema ??= new Schema(new[] { new Field("result", fn.Result.DataType, nullable: true) }, null);
        return new InMemoryArrayStream(resultSchema, batches);
    }
}
