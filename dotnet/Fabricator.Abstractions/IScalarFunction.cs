// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;

namespace Fabricator.Bridge;

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

    /// <summary>
    /// The single result field (name + Arrow type), or <c>null</c> when the result type is not fixed and
    /// <see cref="Bind"/> resolves it per call site.
    /// </summary>
    /// <remarks>
    /// <para>A declared field is what the DuckDB catalog entry is REGISTERED with, so it is what
    /// <c>duckdb_functions()</c> and overload displays show; <c>null</c> registers as <c>ANY</c> and the bind
    /// must then supply a type or the call is refused by name at bind. Declare it whenever the type really is
    /// fixed — which is every discovered SQL Server scalar UDF, since its return type is metadata.</para>
    /// <para>⚠ The BIND's answer is what each call site actually uses. For the default binding those are the
    /// same object by construction; a provider that overrides <see cref="Bind"/> and returns something else
    /// is taken at its word.</para>
    /// </remarks>
    Field? Result { get; }

    /// <summary>
    /// Whether the function is VOLATILE (default) — non-deterministic or side-effecting, never
    /// constant-folded — or CONSISTENT (override to <c>false</c> for a PURE function: same inputs =&gt; same
    /// output, no side effects). A CONSISTENT function over constant args folds to a literal at plan time,
    /// which is what lets a predicate like <c>WHERE bucket_col = bucket(8, 'alice')</c> reach the scan as an
    /// ordinary constant filter (partition/file pruning). Discovered SQL UDFs stay VOLATILE (a remote body
    /// may read data or use nondeterminism); the flag rides the return-schema field metadata
    /// (<c>fabricator.volatile</c>) — no ABI change.
    /// </summary>
    bool IsVolatile => true;

    /// <summary>
    /// Computes the result column for a batch of argument rows. <paramref name="args"/> carries the
    /// columns described by <see cref="Parameters"/> (positional, same order); the returned array must
    /// have the same length as the batch and the Arrow type of the bound result field. The function sees
    /// NULL argument rows (it is registered VOLATILE + SPECIAL_HANDLING, like discovered UDFs).
    /// </summary>
    IArrowArray Invoke(RecordBatch args);

    /// <summary>
    /// Binds one CALL SITE, mirroring <see cref="ITableFunction.Bind"/>: returns a per-call binding carrying
    /// the resolved result field plus any state worth computing once instead of per chunk. The default
    /// implementation reports <see cref="Result"/> and forwards <see cref="Invoke"/>, so a function with a
    /// fixed result type implements nothing.
    /// </summary>
    /// <remarks>
    /// <para>⚠ <b>THE ARGUMENT VALUES ARE PARTIAL.</b> Unlike a table function — whose arguments must be
    /// constant, and arrive pre-evaluated — a scalar's need not be: <c>f(t.col)</c> is legal. Consult
    /// <see cref="ScalarBindArgs.IsConstant"/> before reading any value; a non-constant slot holds a NULL
    /// PLACEHOLDER that is NOT distinguishable from an explicit NULL literal by looking at the value.</para>
    /// <para>⚠ <b>THE VALUES ARE ALSO PRE-CAST.</b> DuckDB applies argument casts AFTER the bind, so a
    /// literal <c>1.0</c> passed to a declared INTEGER parameter arrives here as DOUBLE and arrives at
    /// <see cref="IScalarFunctionBinding.Invoke"/> as INTEGER. Bind values are for DECIDING; the execute
    /// batch is the authoritative typed view for COMPUTING.</para>
    /// </remarks>
    IScalarFunctionBinding Bind(ScalarBindArgs args) => new StaticScalarBinding(this);
}

/// <summary>
/// A bound scalar-function call site (one per call site, disposed at plan teardown). Holds the resolved
/// <see cref="Result"/> — which may depend on the bound arguments — and computes the result column for every
/// chunk. The analogue of <see cref="ITableFunctionBinding"/>.
/// </summary>
public interface IScalarFunctionBinding : System.IDisposable
{
    /// <summary>
    /// The result field (name + Arrow type) resolved for this call site's arguments, or <c>null</c> to mean
    /// "the function's DECLARED <see cref="IScalarFunction.Result"/> stands".
    /// </summary>
    /// <remarks>
    /// ⚠ Returning <c>null</c> is not a fallback, it is the CHEAP ANSWER: the host resolved the declared type
    /// once when it materialized the catalog entry, so a fixed-return function need not re-derive it (nor,
    /// for a discovered SQL UDF, re-query the server) at every call site. Return a concrete field only when
    /// this call's result genuinely differs from the declaration. If the declaration was itself absent and
    /// this is <c>null</c> too, the call is refused by name at bind.
    /// </remarks>
    Field? Result { get; }

    /// <summary>
    /// Computes the result column for one batch of argument rows: <paramref name="args"/> carries the columns
    /// described by <see cref="IScalarFunction.Parameters"/> (positional, same order, POST-cast) and the
    /// returned array must have the batch's length and <see cref="Result"/>'s Arrow type.
    /// </summary>
    /// <remarks>
    /// A CONSTANT argument's column is present and fully materialised here, repeated for every row, even
    /// though the binding already saw its value. That is deliberate: which arguments are constant is a
    /// property of the CALL SITE, so omitting them would make column <c>i</c> stop meaning parameter
    /// <c>i</c> — differently per call site.
    /// </remarks>
    IArrowArray Invoke(RecordBatch args);
}

/// <summary>
/// The call site's arguments as seen at bind: one value per declared parameter, PARTIAL and PRE-CAST (see
/// <see cref="IScalarFunction.Bind"/>). <see cref="Values"/> is a single-row batch, null when the function
/// takes no arguments.
/// </summary>
public sealed class ScalarBindArgs
{
    private readonly bool[] _constant;

    public ScalarBindArgs(RecordBatch? values, bool[]? constant)
    {
        Values = values;
        _constant = constant ?? System.Array.Empty<bool>();
    }

    /// <summary>The 1-row batch of argument values, or null when the function takes no arguments.</summary>
    public RecordBatch? Values { get; }

    /// <summary>Number of arguments at this call site.</summary>
    public int Count => Values?.ColumnCount ?? 0;

    /// <summary>
    /// Whether argument <paramref name="index"/> is a FOLDED CONSTANT whose value in <see cref="Values"/> is
    /// real. False means a runtime expression whose slot holds a meaningless NULL placeholder — reading it as
    /// data is the mistake this flag exists to prevent.
    /// </summary>
    public bool IsConstant(int index) => index >= 0 && index < _constant.Length && _constant[index];

    /// <summary>
    /// The Arrow array holding argument <paramref name="index"/>'s value (length 1), or null when the index is
    /// out of range or the argument is not a folded constant. Read row 0 for the literal;
    /// <c>ArrowValueReader.ReadScalar</c> boxes it — it lives in <c>Fabricator.Common</c>, the OPTIONAL
    /// reusable-implementation assembly, so a plugin can reach it with one ProjectReference and without
    /// taking on the bridge's closure. (It used to be Bridge-only, i.e. out of reach for a plugin.)
    /// </summary>
    public IArrowArray? ConstantArray(int index) =>
        IsConstant(index) && Values is not null && index < Values.ColumnCount ? Values.Column(index) : null;
}

/// <summary>
/// The default binding: a function whose result type is fixed and whose execution needs no per-call state.
/// Defers the result type to the declaration and forwards to <see cref="IScalarFunction.Invoke"/>.
/// </summary>
/// <remarks>
/// ⚠ It deliberately reports <c>Result = null</c> rather than reading the definition's
/// <see cref="IScalarFunction.Result"/>. For a discovered SQL UDF that property is a SERVER ROUND TRIP, and
/// the host already holds the declared type — so reading it here would add one query per call site to
/// re-learn something nobody forgot. A function that declares nothing AND does not override
/// <see cref="IScalarFunction.Bind"/> is refused at bind, by name, rather than here.
/// </remarks>
public sealed class StaticScalarBinding : IScalarFunctionBinding
{
    private readonly IScalarFunction _fn;

    public StaticScalarBinding(IScalarFunction fn)
    {
        _fn = fn;
    }

    public Field? Result => null; // "the declared type stands" — see the remarks above

    public IArrowArray Invoke(RecordBatch args) => _fn.Invoke(args);

    public void Dispose()
    {
    }
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
