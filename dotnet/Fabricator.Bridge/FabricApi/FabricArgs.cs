using Apache.Arrow;

namespace Fabricator.Bridge;

/// <summary>
/// Reads positional arguments out of the marshaled Arrow argument batch, treating "absent column" and "NULL
/// value" identically as <c>null</c>.
/// </summary>
/// <remarks>
/// Custom scalar/table functions are POSITIONAL-only with no default mechanism (docs/fabric-api-functions.md §2
/// Gap 2), so an optional argument is a trailing NULL-able one and every reader here must tolerate both a short
/// batch and a null slot. Deliberately forgiving about the arrival type as well: a DuckDB literal can reach us
/// as a different width than declared (an integer literal for a BIGINT parameter), and failing on that would be
/// a confusing SQL-level error for something the caller wrote correctly.
/// </remarks>
internal static class FabricArgs
{
    internal static string? Str(RecordBatch? args, int col, int row = 0)
    {
        if (args is null || col >= args.ColumnCount || row >= args.Length)
        {
            return null;
        }
        return args.Column(col) switch
        {
            StringArray s => s.IsNull(row) ? null : s.GetString(row),
            LargeStringArray ls => ls.IsNull(row) ? null : ls.GetString(row),
            StringViewArray sv => sv.IsNull(row) ? null : sv.GetString(row),
            _ => null,
        };
    }

    internal static bool? Bool(RecordBatch? args, int col, int row = 0)
    {
        if (args is null || col >= args.ColumnCount || row >= args.Length)
        {
            return null;
        }
        return args.Column(col) switch
        {
            BooleanArray b => b.GetValue(row),
            // A caller may write 1/0; accept it rather than silently ignoring the argument.
            Int32Array i32 => i32.IsNull(row) ? null : i32.GetValue(row) != 0,
            Int64Array i64 => i64.IsNull(row) ? null : i64.GetValue(row) != 0,
            _ => null,
        };
    }

    internal static long? Int(RecordBatch? args, int col, int row = 0)
    {
        if (args is null || col >= args.ColumnCount || row >= args.Length)
        {
            return null;
        }
        return args.Column(col) switch
        {
            Int64Array i64 => i64.IsNull(row) ? null : i64.GetValue(row),
            Int32Array i32 => i32.IsNull(row) ? null : i32.GetValue(row),
            Int16Array i16 => i16.IsNull(row) ? null : i16.GetValue(row),
            UInt32Array u32 => u32.IsNull(row) ? null : (long?)u32.GetValue(row),
            UInt64Array u64 => u64.IsNull(row) ? null : (long?)u64.GetValue(row),
            DoubleArray d => d.IsNull(row) ? null : (long?)d.GetValue(row),
            _ => null,
        };
    }
}
