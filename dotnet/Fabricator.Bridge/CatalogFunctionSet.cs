using System;
using System.Collections.Generic;
using System.Linq;
using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace Fabricator.Bridge;

/// <summary>
/// A provider-agnostic holder + dispatcher for CATALOG-BOUND custom (C#-authored) functions.
///
/// <para>Every backend that hosts catalog functions has to answer the same five ABI calls by looking a
/// <c>"schema.name"</c> key up in a dictionary and delegating to Bridge helpers. The SQL-Server catalog does that
/// inline, interleaved with its discovered-routine fallbacks (which are genuinely SqlServer-specific). A provider
/// with NO discovered routines — Delta, DAX — needs only the lookup half, so it lives here instead of being
/// hand-copied a third and fourth time.</para>
///
/// <para><b>The <c>__all__</c> schema sentinel.</b> A Delta root's schema names are folder names, unknown until
/// ATTACH, so a static declaration cannot name them (the same problem <see cref="CatalogMacroDefinition"/> has).
/// A function declared in schema <c>__all__</c> is therefore advertised once per discovered schema, and resolves
/// in any of them. Lookup tries the exact schema first so a schema-specific declaration always wins over a
/// sentinel one of the same name.</para>
/// </summary>
public sealed class CatalogFunctionSet
{
    /// <summary>Declare a function in every discovered schema of the catalog.</summary>
    public const string AllSchemas = "__all__";

    private readonly Dictionary<string, ICatalogScalarFunction> _scalars;
    private readonly Dictionary<string, ICatalogTableFunction> _tables;

    public CatalogFunctionSet(
        IEnumerable<ICatalogScalarFunction>? scalars = null,
        IEnumerable<ICatalogTableFunction>? tables = null)
    {
        _scalars = (scalars ?? Enumerable.Empty<ICatalogScalarFunction>())
            .ToDictionary(f => Key(f.SchemaName, f.Name), StringComparer.OrdinalIgnoreCase);
        _tables = (tables ?? Enumerable.Empty<ICatalogTableFunction>())
            .ToDictionary(f => Key(f.SchemaName, f.Name), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True when nothing is registered — lets a caller keep its old "no functions" behaviour cheaply.</summary>
    public bool IsEmpty => _scalars.Count == 0 && _tables.Count == 0;

    private static string Key(string schema, string name) => $"{schema}.{name}";

    /// <summary>
    /// The kind-6 declaration rows for this set, with <see cref="AllSchemas"/> expanded across
    /// <paramref name="schemaNames"/> (evaluated lazily — a catalog that declares nothing must not pay for a
    /// schema enumeration, which on OneLake is a storage round-trip).
    /// </summary>
    public IEnumerable<FunctionsMetadata.Declaration> Declarations(Func<IReadOnlyList<string>> schemaNames)
    {
        if (IsEmpty)
        {
            return System.Array.Empty<FunctionsMetadata.Declaration>();
        }
        var rows = new List<FunctionsMetadata.Declaration>();
        IReadOnlyList<string>? schemas = null;
        void Emit(string declaredSchema, string name, string kind)
        {
            if (!string.Equals(declaredSchema, AllSchemas, StringComparison.Ordinal))
            {
                rows.Add(new FunctionsMetadata.Declaration(declaredSchema, name, kind));
                return;
            }
            schemas ??= schemaNames();
            foreach (var s in schemas)
            {
                rows.Add(new FunctionsMetadata.Declaration(s, name, kind));
            }
        }
        foreach (var f in _scalars.Values) { Emit(f.SchemaName, f.Name, "scalar"); }
        foreach (var f in _tables.Values) { Emit(f.SchemaName, f.Name, "table"); }
        return rows;
    }

    private bool TryGet<T>(Dictionary<string, T> map, string schema, string func, out T fn) =>
        map.TryGetValue(Key(schema, func), out fn!) || map.TryGetValue(Key(AllSchemas, func), out fn!);

    public bool TryScalar(string schema, string func, out ICatalogScalarFunction fn) =>
        TryGet(_scalars, schema, func, out fn);

    public bool TryTable(string schema, string func, out ICatalogTableFunction fn) =>
        TryGet(_tables, schema, func, out fn);

    // ---- the five ABI members, as one-liners a hosting catalog can forward to -------------------------

    /// <summary>Argument schema for either kind. Returns null when the name is not ours (caller decides).</summary>
    public Schema? ParamSchema(string schema, string func)
    {
        if (TryScalar(schema, func, out var s)) { return s.Parameters; }
        if (TryTable(schema, func, out var t)) { return t.Parameters; }
        return null;
    }

    /// <summary>Scalar return field, carrying the volatility tag the host reads to allow constant folding.</summary>
    public Schema? ReturnSchema(string schema, string func) =>
        TryScalar(schema, func, out var fn)
            ? new Schema(new[] { ScalarFunctionMetadata.TagVolatility(fn.Result, fn) }, null)
            : null;

    /// <summary>Runs a scalar over the argument stream. Consumes/disposes <paramref name="args"/>.</summary>
    public IArrowArrayStream? ExecuteScalar(string schema, string func, IArrowArrayStream args) =>
        TryScalar(schema, func, out var fn) ? GlobalFunctions.ExecuteScalar(fn, args) : null;

    /// <summary>
    /// Output schema of a table function, resolved from its constant args — this is the whole reason the
    /// Bind→Binding model exists, so the binding is built and immediately disposed rather than cached.
    /// </summary>
    public Schema? OutputSchema(string schema, string func, RecordBatch? args)
    {
        if (!TryTable(schema, func, out var fn))
        {
            return null;
        }
        using var binding = fn.Bind(args!);
        return binding.OutputSchema;
    }

    /// <summary>
    /// Binds a table function for one execution. <c>supportsPushdown: true</c> means the host maps the full
    /// result BY NAME (the flag is projection mapping, NOT SQL pushdown — see <see cref="BindingBoundTable"/>).
    /// </summary>
    public IBoundTable? TableBind(string schema, string func, RecordBatch? args) =>
        TryTable(schema, func, out var fn)
            ? new BindingBoundTable(fn.Bind(args!), supportsPushdown: true)
            : null;
}
