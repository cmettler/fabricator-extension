using Apache.Arrow;

namespace ArrowNet.Bridge;

/// <summary>
/// A provider-authored custom scalar function, implemented in C# over Arrow — the scope-INDEPENDENT contract
/// shared by catalog-bound functions (<see cref="ICatalogScalarFunction"/>, resolved as
/// <c>db.schema.fn(args)</c>) and connection-free load-time globals (resolved as a bare <c>fn(args)</c>, no
/// ATTACH). <see cref="SchemaName"/> is the only catalog-specific bit, so it lives on the derived interface;
/// everything here (signature + the <see cref="Invoke"/> body) is identical across both scopes, which lets the
/// schema/execute dispatch operate on this base regardless of how the function was resolved.
/// </summary>
public interface IScalarFunction
{
    /// <summary>Function name. Catalog: <c>db.schema.Name(args)</c>; global: the bare registered name.</summary>
    string Name { get; }

    /// <summary>The argument fields, in positional order (names + Arrow types) — the function's signature.</summary>
    Schema Parameters { get; }

    /// <summary>The single result field (name + Arrow type).</summary>
    Field Result { get; }

    /// <summary>
    /// Computes the result column for a batch of argument rows. <paramref name="args"/> carries the
    /// columns described by <see cref="Parameters"/> (positional, same order); the returned array must
    /// have the same length as the batch and the Arrow type of <see cref="Result"/>. The function sees
    /// NULL argument rows (it is registered VOLATILE + SPECIAL_HANDLING, like discovered UDFs).
    /// </summary>
    IArrowArray Invoke(RecordBatch args);
}

/// <summary>
/// A catalog-bound custom scalar function (the attach-time scope). A backend exposes these (the SqlServer
/// provider lists them in its <c>CustomFunctions</c> registry); they are surfaced into every attached catalog's
/// schema at discovery time alongside the provider's discovered functions, so a custom function resolves as
/// <c>db.SchemaName.Name(args)</c> and runs through the very same scalar-function path as a discovered UDF — the
/// catalog simply dispatches to <see cref="IScalarFunction.Invoke"/> instead of generating SQL. No
/// load-time/global registration, no extra ABI: it reuses the existing <c>get_function_param_schema</c> /
/// <c>get_function_return_schema</c> / <c>execute_scalar</c>. (For a connection-free, ATTACH-free function,
/// implement the base <see cref="IScalarFunction"/> and declare it as a global instead.)
/// </summary>
public interface ICatalogScalarFunction : IScalarFunction
{
    /// <summary>Target catalog schema (e.g. "dbo"); created on attach if it isn't already present.</summary>
    string SchemaName { get; }
}
