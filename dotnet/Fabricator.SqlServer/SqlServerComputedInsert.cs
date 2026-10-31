// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Text;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Fabricator.Bridge;
using Fabricator.Bridge.Conversion;

namespace Fabricator.SqlServer;

/// <summary>
/// INSERT … RETURNING on Fabric Warehouse, which has no OUTPUT clause. The returned rows are COMPUTED from the
/// input: each value converted the way the server converts it on store — measured on Fabric (docs/
/// warehouse-support.md §6.8a): <c>datetime2(p)</c> / <c>datetime2offset(p)</c> / <c>time(p)</c> TRUNCATE to p
/// fractional digits (box SQL Server rounds instead, which is why this path is Fabric-only), <c>char(n)</c> pads
/// with blanks to n BYTES of UTF-8; DECIMAL and REAL need nothing, DuckDB has already cast to the column's type.
/// The declared types come from the table schema's <see cref="SqlTypeMetadata"/>. What is INSERTED is the
/// converted values themselves — the input's own columns and types, so the bulk writer sees what a plain INSERT
/// hands it — and the server stores exactly what is returned (truncating or padding a converted value changes
/// nothing), through the regular bulk path, SqlBulkCopy or the staged COPY INTO alike. A table with
/// an IDENTITY column is refused (its values are assigned on store); a warehouse has no DEFAULT constraints, so an
/// omitted column is NULL.
/// </summary>
public sealed partial class SqlServerCatalog
{
    private const int ComputedInsertBatchRows = 16384;

    private IArrowArrayStream InsertReturningComputed(string schemaName, string tableName, IArrowArrayStream rows)
    {
        RecordTouch(schemaName, tableName, schemaChanged: false);
        string qualified = Quote(schemaName) + "." + Quote(tableName);
        Schema table;
        var (connection, transaction, owns) = BeginWrite();
        try
        {
            table = ReadTableSchema(connection, transaction, qualified);
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT TOP (1) name FROM sys.columns WHERE object_id = OBJECT_ID(@t) AND is_identity = 1";
            cmd.Parameters.AddWithValue("@t", qualified);
            if (cmd.ExecuteScalar() is string identity)
            {
                rows.Dispose();
                throw new NotSupportedException(
                    $"INSERT ... RETURNING into {schemaName}.{tableName} is not supported on a warehouse engine: its "
                    + $"IDENTITY column '{identity}' is assigned on store, and a warehouse has no OUTPUT clause.");
            }
        }
        finally
        {
            if (owns)
            {
                connection.Dispose();
            }
        }

        var source = new int[table.FieldsList.Count];
        var convert = new Func<object, object>?[table.FieldsList.Count];
        for (int c = 0; c < source.Length; c++)
        {
            var field = table.FieldsList[c];
            source[c] = -1;
            for (int i = 0; i < rows.Schema.FieldsList.Count; i++)
            {
                if (string.Equals(rows.Schema.FieldsList[i].Name, field.Name, StringComparison.OrdinalIgnoreCase))
                {
                    source[c] = i;
                    break;
                }
            }
            convert[c] = StoreConversion(field);
        }

        // the same conversion on the way in, per input column
        var inputConvert = new Func<object, object>?[rows.Schema.FieldsList.Count];
        for (int c = 0; c < source.Length; c++)
        {
            if (source[c] >= 0)
            {
                inputConvert[source[c]] = convert[c];
            }
        }
        var returned = new List<RecordBatch>();
        var stream = new ComputedInsertStream(table, rows.Schema, new ArrowDataReader(rows), source, inputConvert,
                                              returned);
        try
        {
            BulkInsert(schemaName, tableName, stream, createTable: false, replace: false, checkConstraints: true,
                       txnId: AmbientTransaction.Current, partitionColumns: null, sortColumns: null, schemaMode: null,
                       partitionOverwrite: false, optionsJson: null);
        }
        catch
        {
            foreach (var b in returned)
            {
                b.Dispose();
            }
            throw;
        }
        finally
        {
            stream.Dispose();
        }
        return new InMemoryArrayStream(WithoutHiddenRowId(table), returned.Select(WithoutHiddenRowId).ToList());
    }

    /// <summary>What Fabric does to a value of this column on store, or null when it stores it as given.</summary>
    internal static Func<object, object>? StoreConversion(Field field)
    {
        var md = field.HasMetadata ? field.Metadata : null;
        switch (SqlTypeMetadata.Type(md))
        {
            case "datetime2":
            case "datetimeoffset":
            case "time":
                if (SqlTypeMetadata.Scale(md) is { } s && s < 7)
                {
                    long per = 1;
                    for (int k = s; k < 7; k++)
                    {
                        per *= 10; // 100 ns ticks per unit of the last kept digit
                    }
                    return v => v switch
                    {
                        DateTime d => new DateTime(d.Ticks - d.Ticks % per, d.Kind),
                        DateTimeOffset o => new DateTimeOffset(o.Ticks - o.Ticks % per, o.Offset),
                        TimeSpan t => new TimeSpan(t.Ticks - t.Ticks % per),
                        _ => v,
                    };
                }
                return null;
            case "char":
                if (SqlTypeMetadata.Length(md) is { } n && n > 0)
                {
                    return v => v is string str && Encoding.UTF8.GetByteCount(str) < n
                        ? str + new string(' ', n - Encoding.UTF8.GetByteCount(str))
                        : v;
                }
                return null;
            case "nchar":
                if (SqlTypeMetadata.Length(md) is { } m && m > 0)
                {
                    return v => v is string str && str.Length < m ? str.PadRight(m) : v;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The input with its values converted, for the bulk path; beside it, the same rows in the TABLE's shape (every
    /// column, the table's types, omitted ones NULL), kept for RETURNING. Two builds of each value, because the
    /// bulk path disposes what it reads.
    /// </summary>
    private sealed class ComputedInsertStream : IArrowArrayStream
    {
        private readonly Schema _table;
        private readonly ArrowDataReader _reader;
        private readonly int[] _source;
        private readonly Func<object, object>?[] _convert;
        private readonly List<RecordBatch> _returned;
        private bool _disposed;

        public ComputedInsertStream(Schema table, Schema input, ArrowDataReader reader, int[] source,
                                    Func<object, object>?[] convert, List<RecordBatch> returned)
        {
            _table = table;
            Schema = input;
            _reader = reader;
            _source = source;
            _convert = convert;
            _returned = returned;
        }

        public Schema Schema { get; }

        public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
        {
            var keep = _table.FieldsList.Select(f => ColumnAppender.Create(f.DataType)).ToArray();
            var send = Schema.FieldsList.Select(f => ColumnAppender.Create(f.DataType)).ToArray();
            var values = new object?[send.Length];
            int n = 0;
            while (n < ComputedInsertBatchRows && !_disposed && _reader.Read())
            {
                for (int i = 0; i < send.Length; i++)
                {
                    values[i] = _reader.IsDBNull(i) ? null
                        : _convert[i] is { } f ? f(_reader.GetValue(i)) : _reader.GetValue(i);
                    if (values[i] is { } v)
                    {
                        send[i].Append(v);
                    }
                    else
                    {
                        send[i].AppendNull();
                    }
                }
                for (int c = 0; c < keep.Length; c++)
                {
                    if (_source[c] >= 0 && values[_source[c]] is { } v)
                    {
                        keep[c].Append(v);
                    }
                    else
                    {
                        keep[c].AppendNull();
                    }
                }
                n++;
            }
            if (n == 0)
            {
                return new((RecordBatch?)null);
            }
            _returned.Add(new RecordBatch(_table, keep.Select(a => a.Build()), n));
            return new(new RecordBatch(Schema, send.Select(a => a.Build()), n));
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _reader.Dispose();
            }
        }
    }
}
