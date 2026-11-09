// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// The parts of a <see cref="ScanPlan"/> as ONE Arrow batch (ABI v101 <c>table_file_plan</c>), from the columns
/// <see cref="ScanPlanWire.PartColumns"/> decided: deleted rows travel as a <c>LIST&lt;BIGINT&gt;</c>, not as text.
/// </summary>
internal static class ScanPartsArrow
{
    public static IArrowArrayStream ToStream(ScanPlan plan)
    {
        var columns = ScanPlanWire.PartColumns(plan);
        var fields = new List<Field>
        {
            new("path", StringType.Default, nullable: false),
            new("stream", BooleanType.Default, nullable: false),
            new("size", Int64Type.Default, nullable: true),
            new("deleted", new ListType(Int64Type.Default), nullable: true),
        };
        var arrays = new List<IArrowArray>
        {
            new StringArray.Builder().AppendRange(columns.Paths).Build(),
            new BooleanArray.Builder().AppendRange(columns.Streams).Build(),
            Int64s(columns.Sizes),
            Deleted(columns.Deleted),
        };
        for (int c = 0; c < columns.ConstantNames.Count; c++)
        {
            fields.Add(new Field("constant:" + columns.ConstantNames[c], StringType.Default, nullable: true));
            var builder = new StringArray.Builder();
            foreach (var value in columns.ConstantValues[c])
            {
                if (value is null)
                {
                    builder.AppendNull();
                }
                else
                {
                    builder.Append(value);
                }
            }
            arrays.Add(builder.Build());
        }
        for (int v = 0; v < columns.VirtualNames.Count; v++)
        {
            fields.Add(new Field("virtual:" + columns.VirtualNames[v], Int64Type.Default, nullable: true));
            arrays.Add(Int64s(columns.VirtualValues[v]));
        }
        var schema = new Schema(fields, null);
        return new OneBatch(schema, new RecordBatch(schema, arrays, columns.Paths.Count));
    }

    private static Int64Array Int64s(List<long?> values)
    {
        var builder = new Int64Array.Builder();
        foreach (var value in values)
        {
            if (value is { } v)
            {
                builder.Append(v);
            }
            else
            {
                builder.AppendNull();
            }
        }
        return builder.Build();
    }

    private static ListArray Deleted(List<IReadOnlyList<long>?> deleted)
    {
        var builder = new ListArray.Builder(Int64Type.Default);
        var positions = (Int64Array.Builder)builder.ValueBuilder;
        foreach (var rows in deleted)
        {
            if (rows is null)
            {
                builder.AppendNull();
                continue;
            }
            builder.Append();
            foreach (var row in rows)
            {
                positions.Append(row);
            }
        }
        return builder.Build();
    }

    /// <summary>A stream of one batch. The batch is the consumer's once read; only an unread one is ours to drop.</summary>
    private sealed class OneBatch : IArrowArrayStream
    {
        private RecordBatch? _batch;

        public OneBatch(Schema schema, RecordBatch batch)
        {
            Schema = schema;
            _batch = batch;
        }

        public Schema Schema { get; }

        public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken ct = default)
        {
            var batch = _batch;
            _batch = null;
            return new ValueTask<RecordBatch?>(batch);
        }

        public void Dispose()
        {
            _batch?.Dispose();
            _batch = null;
        }
    }
}
