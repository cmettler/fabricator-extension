using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using EngineeredWood.DeltaLake.Table;
using Microsoft.Extensions.Logging;

namespace Fabricator.Bridge;

/// <summary>
/// The native Delta copy-on-write <b>rewrite reader</b> (docs/native-delta-write.md §5): for a DELETE/UPDATE that
/// rewrites a file, DuckDB's own <c>read_parquet(..., file_row_number =&gt; true)</c> reads the source and applies
/// the row-level transform entirely in SQL — the deleted physical positions (and, for UPDATE, the file's existing
/// deletion-vector positions) are dropped with a <c>WHERE file_row_number NOT IN (…)</c>, and an UPDATE's SET
/// columns are substituted via a <c>LEFT JOIN</c> against the (position → new value) rows bound as an Arrow view.
/// This <b>retires the in-process typed value substitution</b> (<c>DeltaCatalog.BuildArray</c>) and the
/// engineered-wood parquet reader from the rewrite path. The returned batches carry the logical user schema;
/// engineered-wood keeps stats (<c>StatsCollector</c> — exact, no float-string rounding), the data-file writer
/// (<see cref="NativeParquetDataFileWriter"/>), row tracking, and the commit.
///
/// <para>Schema-evolution backfill is handled here (a column ADDed after the source file was written is absent
/// from its parquet → emitted as a typed <c>NULL</c>), so the seam needs no schema-evolution gate. Column
/// mapping / partitions / type widening / CDF are gated OFF in engineered-wood (it falls back to its own reader).</para>
/// </summary>
internal sealed class NativeParquetDataFileRewriter : IDataFileRewriter
{
    private const int RowIdPositionBits = 40;
    private const long PosMask = (1L << RowIdPositionBits) - 1;
    private const string PosColumn = "__fabricator_pos";
    private const string UpdInput = "__fabricator_upd";
    private const string ExclInput = "__fabricator_excl";
    private static readonly ILogger Log = FabricatorLog.CreateLogger("Fabricator.Delta.Native");

    private readonly string _root; // table root as a DuckDB-openable URI (onelake:// for OneLake)
    private readonly Schema _userSchema; // logical user columns (no partitions/rowid), in output order
    private readonly IReadOnlyList<string>? _setColumns; // UPDATE: SET column names; null => DELETE (no substitution)
    private readonly IReadOnlyDictionary<int, RecordBatch>? _updatesByOrdinal; // UPDATE: ordinal -> [__pos ++ SET vals]

    /// <summary>DELETE rewriter — drops the excluded positions, no value substitution.</summary>
    internal NativeParquetDataFileRewriter(string tablePath, Schema userSchema)
        : this(tablePath, userSchema, null, null)
    {
    }

    /// <summary>UPDATE rewriter — <paramref name="updatesByOrdinal"/> maps a file ordinal to a 1-batch Arrow view
    /// <c>[__fabricator_pos:int64 ++ &lt;set columns, in setColumns order&gt;]</c> of the rows to update in that file.</summary>
    internal NativeParquetDataFileRewriter(string tablePath, Schema userSchema,
        IReadOnlyList<string>? setColumns, IReadOnlyDictionary<int, RecordBatch>? updatesByOrdinal)
    {
        _root = DeltaReader.ToReadableRoot(tablePath);
        _userSchema = userSchema;
        _setColumns = setColumns is { Count: > 0 } ? setColumns : null;
        _updatesByOrdinal = updatesByOrdinal;
    }

    internal static bool Available => Host.CanQuery;

    public async IAsyncEnumerable<RecordBatch> ReadRewriteAsync(
        int fileOrdinal, string sourceRelativePath, IReadOnlyCollection<long> excludePositions,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        RowTrackingRewrite? rowTracking = null)
    {
        var rel = sourceRelativePath.Replace('\\', '/').TrimStart('/');
        var uri = _root + "/" + rel;

        // Backfill: a column ADDed after this file was written is absent from its parquet → probe the file's
        // columns and emit an absent one as a typed NULL (matches engineered-wood's ReadFileAsync backfill).
        var present = ProbeColumns(uri);

        RecordBatch? updates = null;
        if (_setColumns is not null && _updatesByOrdinal is not null &&
            _updatesByOrdinal.TryGetValue(fileOrdinal, out var b))
        {
            updates = b;
        }

        var inputs = new List<(string, IArrowArrayStream)>(2);
        if (updates is not null)
        {
            inputs.Add((UpdInput, new InMemoryArrayStream(updates.Schema, new[] { updates })));
        }
        if (excludePositions.Count > 0)
        {
            inputs.Add((ExclInput, ExcludeStream(excludePositions)));
        }

        var sql = BuildSql(uri, present, updates is not null, excludePositions.Count > 0, rowTracking);
        Log.LogDebug("delta native rewrite: {Sql}", sql);

        using var stream = inputs.Count > 0 ? Host.Query(sql, inputs) : Host.Query(sql);
        while (true)
        {
            var rb = await stream.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
            if (rb is null)
            {
                break;
            }
            yield return rb;
        }
    }

    // SELECT the user columns (aliased to their logical names, in schema order); a SET column is substituted from
    // the bound update view when its row matched (CASE on the join key), an absent column is a typed NULL.
    // With rowTracking, two trailing columns materialize each row's ORIGINAL stable id + commit version
    // (the source file's materialized value where present, else baseRowId + file_row_number / the source
    // default; an update-matched row's version = the new commit) — the IDataFileRewriter contract.
    private string BuildSql(string uri, HashSet<string> present, bool hasUpdates, bool hasExclude,
                            RowTrackingRewrite? rowTracking)
    {
        var setSet = _setColumns is null
            ? null
            : new HashSet<string>(_setColumns, StringComparer.OrdinalIgnoreCase);

        var projections = new List<string>(_userSchema.FieldsList.Count + 2);
        foreach (var f in _userSchema.FieldsList)
        {
            string q = Quote(f.Name);
            string src = present.Contains(f.Name) ? "p." + q : "CAST(NULL AS " + DuckType(f.DataType) + ")";
            if (hasUpdates && setSet!.Contains(f.Name))
            {
                // Matched (u.__pos non-null after the LEFT JOIN) → the new value; else the source value.
                projections.Add($"CASE WHEN u.{Quote(PosColumn)} IS NOT NULL THEN u.{q} ELSE {src} END AS {q}");
            }
            else
            {
                projections.Add($"{src} AS {q}");
            }
        }

        if (rowTracking is not null)
        {
            const string IdCol = "\"__delta_row_id\"";
            const string VerCol = "\"__delta_row_commit_version\"";
            string idExpr = present.Contains("__delta_row_id")
                ? rowTracking.SourceBaseRowId is { } b1
                    ? $"COALESCE(p.{IdCol}, CAST({b1.ToString(CultureInfo.InvariantCulture)} AS BIGINT) + p.file_row_number)"
                    : $"p.{IdCol}"
                : rowTracking.SourceBaseRowId is { } b2
                    ? $"(CAST({b2.ToString(CultureInfo.InvariantCulture)} AS BIGINT) + p.file_row_number)"
                    : "CAST(NULL AS BIGINT)";
            projections.Add($"{idExpr} AS {IdCol}");
            string verExpr = present.Contains("__delta_row_commit_version")
                ? rowTracking.SourceDefaultCommitVersion is { } d1
                    ? $"COALESCE(p.{VerCol}, CAST({d1.ToString(CultureInfo.InvariantCulture)} AS BIGINT))"
                    : $"p.{VerCol}"
                : rowTracking.SourceDefaultCommitVersion is { } d2
                    ? $"CAST({d2.ToString(CultureInfo.InvariantCulture)} AS BIGINT)"
                    : "CAST(NULL AS BIGINT)";
            if (hasUpdates)
            {
                verExpr = $"CASE WHEN u.{Quote(PosColumn)} IS NOT NULL THEN "
                    + $"CAST({rowTracking.NewCommitVersion.ToString(CultureInfo.InvariantCulture)} AS BIGINT) "
                    + $"ELSE {verExpr} END";
            }
            projections.Add($"{verExpr} AS {VerCol}");
        }

        var sb = new StringBuilder("SELECT ");
        sb.Append(string.Join(", ", projections));
        sb.Append($" FROM read_parquet('{uri.Replace("'", "''")}', file_row_number => true) p");
        if (hasUpdates)
        {
            sb.Append($" LEFT JOIN {UpdInput} u ON p.file_row_number = u.{Quote(PosColumn)}");
        }
        if (hasExclude)
        {
            sb.Append($" WHERE p.file_row_number NOT IN (SELECT {Quote(PosColumn)} FROM {ExclInput})");
        }
        return sb.ToString();
    }

    // The file's actual column set (LIMIT 0 = footer only) so a schema-evolved missing column is backfilled NULL.
    private static HashSet<string> ProbeColumns(string uri)
    {
        using var s = Host.Query($"SELECT * FROM read_parquet('{uri.Replace("'", "''")}') LIMIT 0");
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in s.Schema.FieldsList)
        {
            set.Add(f.Name);
        }
        return set;
    }

    private static IArrowArrayStream ExcludeStream(IReadOnlyCollection<long> positions)
    {
        var b = new Int64Array.Builder();
        foreach (var p in positions)
        {
            b.Append(p);
        }
        var schema = new Schema(new[] { new Field(PosColumn, Int64Type.Default, nullable: false) }, null);
        return new InMemoryArrayStream(schema,
            new[] { new RecordBatch(schema, new IArrowArray[] { b.Build() }, positions.Count) });
    }

    // The physical file position of a transient rowid (fileOrdinal is already known per file).
    internal static long PositionOf(long rowId) => rowId & PosMask;

    private static string Quote(string col) => "\"" + col.Replace("\"", "\"\"") + "\"";

    // Arrow -> DuckDB type string, used only for typed-NULL backfill of a schema-evolved absent column.
    private static string DuckType(IArrowType type) => type switch
    {
        BooleanType => "BOOLEAN",
        Int8Type => "TINYINT",
        Int16Type => "SMALLINT",
        Int32Type => "INTEGER",
        Int64Type => "BIGINT",
        UInt8Type => "UTINYINT",
        UInt16Type => "USMALLINT",
        UInt32Type => "UINTEGER",
        UInt64Type => "UBIGINT",
        FloatType => "FLOAT",
        DoubleType => "DOUBLE",
        Decimal128Type d => $"DECIMAL({d.Precision},{d.Scale})",
        StringType => "VARCHAR",
        Date32Type => "DATE",
        TimestampType t => t.Timezone is null ? "TIMESTAMP" : "TIMESTAMPTZ",
        BinaryType => "BLOB",
        _ => throw new NotSupportedException(
            $"delta native rewrite: no DuckDB type mapping for backfill of {type.TypeId}"),
    };
}
