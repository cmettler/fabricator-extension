// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// Accumulates rows for a fixed <see cref="Schema"/>, creating the right Arrow builder per column so a function
/// body can write <c>row.Str(0, name); row.Int(1, bytes); row.Iso(2, timestamp)</c> without repeating the builder
/// plumbing.
/// </summary>
/// <remarks>
/// <para>Exists because the D4 output rule (typed flat columns, no STRUCT wrapping) means most of these
/// functions mix strings with counts and timestamps, and hand-rolling six parallel builder variables per
/// function is where an off-by-one column slip hides. Column identity here is the INDEX into the declared
/// schema, and <see cref="Build"/> emits them in that order, so the schema is the single source of truth for
/// both.</para>
/// <para>Deliberately strict about type: writing a string into a timestamp column throws rather than silently
/// dropping the value, because the failure mode otherwise is a column of NULLs that looks like "the service
/// returned nothing".</para>
/// </remarks>
internal sealed class FabricRowBuilder
{
    private readonly Schema _schema;
    private readonly IArrowArrayBuilder<IArrowArray>[] _builders;
    private readonly int[] _appended;

    internal FabricRowBuilder(Schema schema)
    {
        _schema = schema;
        _builders = new IArrowArrayBuilder<IArrowArray>[schema.FieldsList.Count];
        _appended = new int[schema.FieldsList.Count];
        for (int i = 0; i < _builders.Length; i++)
        {
            _builders[i] = Create(schema.FieldsList[i]);
        }
    }

    /// <summary>How many rows have been appended (each column must receive exactly one value per row).</summary>
    internal int Rows { get; private set; }

    private static IArrowArrayBuilder<IArrowArray> Create(Field f) => f.DataType.TypeId switch
    {
        ArrowTypeId.String => new StringArray.Builder(),
        ArrowTypeId.Int64 => new Int64Array.Builder(),
        ArrowTypeId.Int32 => new Int32Array.Builder(),
        ArrowTypeId.Boolean => new BooleanArray.Builder(),
        ArrowTypeId.Double => new DoubleArray.Builder(),
        ArrowTypeId.Timestamp => new TimestampArray.Builder((TimestampType)f.DataType),
        _ => throw new NotSupportedException(
            $"fabric: column '{f.Name}' has type {f.DataType.Name}, which FabricRowBuilder does not build."),
    };

    /// <summary>Marks one row complete. Call after writing every column.</summary>
    /// <remarks>
    /// Verifies that EVERY column received exactly one value for this row, and names the ones that did not.
    /// Without this a skipped column is nearly silent: its builder ends up shorter than the others, and what
    /// surfaces is a length mismatch deep in <see cref="RecordBatch"/> construction — or, if two columns are
    /// skipped in different rows, a batch that builds fine with values shifted into the wrong rows. Since
    /// column identity here is a bare INDEX, that off-by-one is the mistake this class exists to prevent, so
    /// it is worth catching at the row that caused it rather than at Build.
    /// </remarks>
    internal FabricRowBuilder EndRow()
    {
        for (int i = 0; i < _appended.Length; i++)
        {
            if (_appended[i] != Rows + 1)
            {
                throw new InvalidOperationException(
                    $"fabric: row {Rows} wrote {_appended[i] - Rows} values to column {i} " +
                    $"('{_schema.FieldsList[i].Name}'); each column takes exactly one value per row.");
            }
        }
        Rows++;
        return this;
    }

    internal FabricRowBuilder Str(int col, string? value)
    {
        ((StringArray.Builder)Expect(col, ArrowTypeId.String)).Append(value);
        return this;
    }

    internal FabricRowBuilder Int(int col, long? value)
    {
        var b = Expect(col, ArrowTypeId.Int64, ArrowTypeId.Int32);
        if (b is Int64Array.Builder i64)
        {
            if (value.HasValue) { i64.Append(value.Value); } else { i64.AppendNull(); }
        }
        else
        {
            var i32 = (Int32Array.Builder)b;
            if (value.HasValue) { i32.Append((int)value.Value); } else { i32.AppendNull(); }
        }
        return this;
    }

    /// <summary>A DOUBLE column — for a measure the service reports as a real number (a duration in seconds).</summary>
    internal FabricRowBuilder Dbl(int col, double? value)
    {
        var b = (DoubleArray.Builder)Expect(col, ArrowTypeId.Double);
        if (value.HasValue) { b.Append(value.Value); } else { b.AppendNull(); }
        return this;
    }

    internal FabricRowBuilder Bool(int col, bool? value)
    {
        var b = (BooleanArray.Builder)Expect(col, ArrowTypeId.Boolean);
        if (value.HasValue) { b.Append(value.Value); } else { b.AppendNull(); }
        return this;
    }

    internal FabricRowBuilder Ts(int col, DateTimeOffset? value)
    {
        var b = (TimestampArray.Builder)Expect(col, ArrowTypeId.Timestamp);
        if (value.HasValue) { b.Append(value.Value); } else { b.AppendNull(); }
        return this;
    }

    /// <summary>
    /// A timestamp the service reported as an ISO STRING. An unparseable or absent value becomes NULL, which is
    /// correct here: several of these fields are genuinely absent (a job that never started has no end time).
    /// </summary>
    internal FabricRowBuilder Iso(int col, string? iso)
    {
        return DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
                                       System.Globalization.DateTimeStyles.AdjustToUniversal, out var dto)
            ? Ts(col, dto)
            : Ts(col, null);
    }

    private IArrowArrayBuilder<IArrowArray> Expect(int col, params ArrowTypeId[] allowed)
    {
        if (col < 0 || col >= _builders.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(col), $"fabric: column {col} is outside the {_builders.Length}-column result.");
        }
        var actual = _schema.FieldsList[col].DataType.TypeId;
        if (System.Array.IndexOf(allowed, actual) < 0)
        {
            throw new InvalidOperationException(
                $"fabric: column '{_schema.FieldsList[col].Name}' is {actual}, written as {allowed[0]}.");
        }
        // Every append routes through here exactly once (Iso delegates to Ts), so this is the one place the
        // per-column count can be maintained for EndRow's check.
        _appended[col]++;
        return _builders[col];
    }

    /// <summary>The accumulated rows as one batch, or null when nothing was appended.</summary>
    internal RecordBatch? Build()
    {
        if (Rows == 0)
        {
            return null;
        }
        var arrays = new IArrowArray[_builders.Length];
        for (int i = 0; i < _builders.Length; i++)
        {
            arrays[i] = _builders[i].Build(null);
        }
        return new RecordBatch(_schema, arrays, Rows);
    }
}
