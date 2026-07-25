using System.Runtime.InteropServices;
using System.Text;
using DuckDB.ExtensionKit;
using DuckDB.ExtensionKit.Native;
using DuckDB.ExtensionKit.NativeMethods;

namespace Fabricator.Installer;

/// <summary>
/// The minimum SQL surface the installer needs on the loading connection: run a statement, and read
/// back small VARCHAR results (pragmas and settings).
/// </summary>
/// <remarks>
/// Reading goes through the STABLE chunk interface (<c>duckdb_fetch_chunk</c> + vector accessors),
/// not the far shorter <c>duckdb_value_varchar</c>/<c>duckdb_row_count</c> pair: those are marked
/// "scheduled for removal" in duckdb.h. The installer is the deliberately version-PORTABLE half of
/// the distribution (C_STRUCT ABI, C API minor &lt;= host), so it is the one component that may well
/// outlive them — and their removal would leave null slots in the API struct, i.e. a crash rather
/// than an error.
/// </remarks>
internal static unsafe class DuckDbSql
{
    /// <summary>
    /// Size of the caller-allocated <c>duckdb_result</c>. The real struct is six fields / 48 bytes
    /// (duckdb.h:517-529), but the kit's API mirror types the out-parameter as <c>nint*</c>, so
    /// over-allocating and zeroing is what keeps <c>duckdb_query</c> from writing past our buffer.
    /// </summary>
    private const int ResultSize = 256;

    /// <summary>A VARCHAR vector entry is a 16-byte <c>duckdb_string_t</c> (duckdb.h:428-440).</summary>
    private const int StringEntrySize = 16;

    /// <summary>Strings up to this length are stored inline in the entry instead of behind a pointer.</summary>
    private const uint InlineStringLimit = 12;

    /// <summary>Escapes a value for a single-quoted SQL literal.</summary>
    internal static string Literal(string value) => value.Replace("'", "''");

    /// <summary>Runs a statement, throwing with DuckDB's own message on failure.</summary>
    internal static void Execute(DuckDBConnection connection, string sql)
    {
        byte* result = stackalloc byte[ResultSize];
        Run(connection, sql, result);
        NativeMethods.DuckDBApi.duckdb_destroy_result((nint*)result);
    }

    /// <summary>Reads row 0 of a query, as strings (SQL NULL becomes null).</summary>
    internal static string?[] QueryFirstRow(DuckDBConnection connection, string sql, int columnCount)
    {
        List<string?[]> rows = Query(connection, sql, columnCount, maxRows: 1);
        return rows.Count > 0
            ? rows[0]
            : throw new InstallerException($"DuckDB returned no rows for \"{sql}\".");
    }

    /// <summary>Reads every row of column 0; an empty result yields an empty list.</summary>
    internal static List<string> QueryColumn(DuckDBConnection connection, string sql)
    {
        List<string?[]> rows = Query(connection, sql, columnCount: 1, maxRows: int.MaxValue);
        var values = new List<string>(rows.Count);
        foreach (string?[] row in rows)
        {
            if (row[0] is { } value)
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static List<string?[]> Query(DuckDBConnection connection, string sql, int columnCount, int maxRows)
    {
        byte* result = stackalloc byte[ResultSize];
        Run(connection, sql, result);

        try
        {
            ref readonly DuckDBExtApiV1 api = ref NativeMethods.DuckDBApi;
            if (api.duckdb_fetch_chunk is null)
            {
                throw new InstallerException(
                    "This DuckDB build does not provide duckdb_fetch_chunk, so the fabricator installer cannot " +
                    "read its configuration. Use the two-piece distribution and LOAD the core directly.");
            }

            // duckdb_fetch_chunk takes the result BY VALUE (duckdb.h:5394) — a 48-byte struct — but the
            // kit's mirror types it as a single pointer. That happens to be ABI-correct where large
            // structs are passed indirectly (Windows x64, AArch64) and WRONG on x64 SysV
            // (linux/macOS-x64), where a 48-byte argument is copied onto the stack. Re-typing the
            // function pointer lets the compiler emit the right convention on every platform.
            var fetchChunk = (delegate* unmanaged[Cdecl]<DuckDbResultValue, nint>)api.duckdb_fetch_chunk;

            var rows = new List<string?[]>();
            while (rows.Count < maxRows)
            {
                nint chunk = fetchChunk(*(DuckDbResultValue*)result);
                if (chunk == 0)
                {
                    break; // exhausted (or an error, which duckdb_result_error would carry)
                }

                try
                {
                    ReadChunk(in api, chunk, columnCount, maxRows, rows);
                }
                finally
                {
                    nint toDestroy = chunk;
                    api.duckdb_destroy_data_chunk(&toDestroy);
                }
            }

            return rows;
        }
        finally
        {
            NativeMethods.DuckDBApi.duckdb_destroy_result((nint*)result);
        }
    }

    private static void ReadChunk(
        ref readonly DuckDBExtApiV1 api,
        nint chunk,
        int columnCount,
        int maxRows,
        List<string?[]> rows)
    {
        ulong size = api.duckdb_data_chunk_get_size(chunk);

        var vectors = new nint[columnCount];
        for (int column = 0; column < columnCount; column++)
        {
            vectors[column] = api.duckdb_data_chunk_get_vector(chunk, (ulong)column);
        }

        for (ulong row = 0; row < size && rows.Count < maxRows; row++)
        {
            var values = new string?[columnCount];
            for (int column = 0; column < columnCount; column++)
            {
                values[column] = ReadString(in api, vectors[column], row);
            }

            rows.Add(values);
        }
    }

    /// <summary>
    /// Decodes one VARCHAR. The bytes belong to the chunk, so they are copied into a managed string
    /// before the chunk is destroyed.
    /// </summary>
    private static string? ReadString(ref readonly DuckDBExtApiV1 api, nint vector, ulong row)
    {
        ulong* validity = api.duckdb_vector_get_validity(vector);
        if (validity is not null && (validity[row >> 6] & (1UL << (int)(row & 63))) == 0)
        {
            return null; // a null mask pointer means "all valid"
        }

        byte* entry = (byte*)api.duckdb_vector_get_data(vector) + (row * StringEntrySize);
        uint length = *(uint*)entry;
        byte* bytes = length <= InlineStringLimit ? entry + sizeof(uint) : *(byte**)(entry + (2 * sizeof(uint)));
        return Encoding.UTF8.GetString(bytes, (int)length);
    }

    private static void Run(DuckDBConnection connection, string sql, byte* result)
    {
        new Span<byte>(result, ResultSize).Clear();

        byte[] utf8 = Encoding.UTF8.GetBytes(sql + '\0');
        DuckDBState state;
        fixed (byte* text = utf8)
        {
            state = NativeMethods.DuckDBApi.duckdb_query(connection.Connection, text, (nint*)result);
        }

        if (state == DuckDBState.Success)
        {
            return;
        }

        // Take DuckDB's message before destroying the result, and surface it verbatim: for the chain
        // LOAD it is the core's own diagnostic (a version mismatch, a missing .NET runtime), which is
        // exactly what the user needs to see.
        byte* error = NativeMethods.DuckDBApi.duckdb_result_error((nint*)result);
        string message = error is null ? "unknown error" : Marshal.PtrToStringUTF8((nint)error) ?? "unknown error";
        NativeMethods.DuckDBApi.duckdb_destroy_result((nint*)result);

        throw new InstallerException($"{message} (while running: {sql})");
    }

    /// <summary>
    /// By-value layout of <c>duckdb_result</c> (duckdb.h:517-529): three <c>idx_t</c> then three
    /// pointers. Declared explicitly so the calling convention for the by-value argument is derived
    /// from the true size rather than from a pointer-shaped stand-in.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DuckDbResultValue
    {
        public ulong DeprecatedColumnCount;
        public ulong DeprecatedRowCount;
        public ulong DeprecatedRowsChanged;
        public nint DeprecatedColumns;
        public nint DeprecatedErrorMessage;
        public nint InternalData;
    }
}
