using Apache.Arrow;

namespace ArrowNet.Bridge;

/// <summary>
/// A provider-authored custom scalar function, implemented in C# over Arrow. A backend exposes
/// these (see <c>IBackend.CustomScalarFunctions</c>); they are surfaced into every attached
/// catalog's schema at discovery time alongside the provider's discovered functions, so a custom
/// function resolves as <c>db.SchemaName.Name(args)</c> and runs through the very same
/// scalar-function path as a discovered UDF — the catalog simply dispatches to <see cref="Invoke"/>
/// instead of generating SQL. No load-time/global registration, no extra ABI: it reuses the
/// existing <c>get_function_param_schema</c> / <c>get_function_return_schema</c> / <c>execute_scalar</c>.
/// </summary>
public abstract class ArrowScalarFunction
{
    /// <summary>Target catalog schema (e.g. "dbo"); created on attach if it isn't already present.</summary>
    public abstract string SchemaName { get; }

    /// <summary>Function name, as called: <c>db.SchemaName.Name(args)</c>.</summary>
    public abstract string Name { get; }

    /// <summary>The argument fields, in positional order (names + Arrow types) — the function's signature.</summary>
    public abstract Schema Parameters { get; }

    /// <summary>The single result field (name + Arrow type).</summary>
    public abstract Field Result { get; }

    /// <summary>
    /// Computes the result column for a batch of argument rows. <paramref name="args"/> carries the
    /// columns described by <see cref="Parameters"/> (positional, same order); the returned array must
    /// have the same length as the batch and the Arrow type of <see cref="Result"/>. The function sees
    /// NULL argument rows (it is registered VOLATILE + SPECIAL_HANDLING, like discovered UDFs).
    /// </summary>
    public abstract IArrowArray Invoke(RecordBatch args);
}
