using System.Collections.Generic;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// The scalar-function volatility signal riding the return-schema FIELD metadata (the same C-ABI metadata
/// channel the variant marker uses — no ABI change): a CONSISTENT (pure, constant-foldable) function's
/// <c>Result</c> field is tagged <c>fabricator.volatile = "0"</c>; ABSENT means VOLATILE (the historical
/// default — old bridges/plugins keep their behavior). The C++ side reads it in FetchFunctionReturnType and
/// sets <c>FunctionStability::CONSISTENT</c> on the registered ScalarFunction, which is what lets
/// <c>bucket(8, 'alice')</c> fold to a literal and reach a scan as a partition-pruning constant filter.
/// Applied wherever a return schema is produced for an <see cref="IScalarFunction"/> (the global handle-0
/// path and the catalog custom-scalar path); discovered SQL UDFs never tag (remote bodies stay VOLATILE).
/// </summary>
public static class ScalarFunctionMetadata
{
    public const string VolatileKey = "fabricator.volatile";

    /// <summary>Tags <paramref name="result"/> per the function's <see cref="IScalarFunction.IsVolatile"/>;
    /// a VOLATILE function's field passes through untouched (absent = volatile).</summary>
    /// <summary>
    /// The field to report for a function's DECLARED return type: its <see cref="IScalarFunction.Result"/>
    /// tagged with the volatility marker, or the UNRESOLVED SENTINEL (an Arrow <c>null</c>-typed field) when
    /// the function declares none. The host registers that sentinel as <c>ANY</c> and then requires the bind
    /// to resolve a type per call site.
    /// </summary>
    public static Field DeclaredReturnField(IScalarFunction fn) =>
        TagVolatility(fn.Result ?? new Field("result", NullType.Default, nullable: true), fn);

    public static Field TagVolatility(Field result, IScalarFunction fn)
    {
        if (fn.IsVolatile)
        {
            return result;
        }
        var metadata = new Dictionary<string, string>();
        if (result.Metadata is not null)
        {
            foreach (var kv in result.Metadata)
            {
                metadata[kv.Key] = kv.Value;
            }
        }
        metadata[VolatileKey] = "0";
        return new Field(result.Name, result.DataType, result.IsNullable, metadata);
    }
}
