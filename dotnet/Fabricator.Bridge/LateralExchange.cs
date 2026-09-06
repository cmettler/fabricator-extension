// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Microsoft.Extensions.Logging;

namespace Fabricator.Bridge;

/// <summary>
/// The marshaling layer for row-mapped (correlated LATERAL) functions: it turns one
/// <see cref="ILateralSession.Call"/> into the host's wire format, and it is where the provenance CONTRACT is
/// enforced rather than merely documented.
/// </summary>
/// <remarks>
/// <para>
/// The wire is the function's own output columns plus ONE TRAILING <see cref="Int32Type"/> column giving, per
/// output row, the 0-based index of the input row that produced it. It rides in the batch rather than in a
/// second out-parameter so there is exactly one wire format for both execution paths (the row-by-row driver
/// ignores the column; the batched operator stamps the correlated columns from it).
/// </para>
/// <para>
/// ⚠ The ABSENT case is STRICT. An author who returns no provenance is asserting a 1:1 map, so an output row
/// count that differs from the input row count is an ERROR here, never a guess — a fan-out or filtering map
/// must say which parent each row has, and inventing one would silently pair rows with the wrong correlated
/// values.
/// </para>
/// <para>
/// The split of validation with the host is deliberate: this side checks what it alone knows cheaply (the
/// column count, the provenance LENGTH, the absent-strict rule) and the HOST checks the RANGE, because the
/// host is what INDEXES with these values. Checking the range on both sides would make one of the two dead
/// code — and it would be the host's, i.e. the one guarding the memory access.
/// </para>
/// </remarks>
internal sealed class LateralSessionRunner : IDisposable
{
    /// <summary>The wire's trailing provenance column. Read by POSITION on the host side (it is always last),
    /// so the name is documentation — but a stable one, because it shows up in error messages.</summary>
    public const string OriginColumn = "__fab_lateral_origin";

    // One line per CROSSING, which is what makes batching CHECKABLE from SQL: result equivalence between the
    // two paths proves correctness and says nothing about whether anything was batched, so the gate counts
    // these and asserts ceil(rows / 2048) rather than `rows`. Debug-gated like every other probe.
    private static readonly ILogger Log = FabricatorLog.CreateLogger("Fabricator.Lateral");

    private readonly ILateralSession _session;
    private readonly string _func;
    private readonly int _outputColumns;
    private readonly Schema _fullWire;
    private readonly Schema? _projectedWire;
    private readonly int _projectedColumns;

    public LateralSessionRunner(ILateralFunctionBinding binding, string func, Schema inputSchema,
                               IReadOnlyList<int>? projected)
    {
        _session = binding.Open(projected);
        _func = func;
        InputSchema = inputSchema;
        _outputColumns = binding.OutputSchema.FieldsList.Count;
        _fullWire = WireSchemaFor(binding.OutputSchema);
        // ⚠ BOTH wire schemas are precomputed because which one applies is not known until the author has
        // answered: honouring the projection hint is optional, so a call may legitimately come back in either
        // shape and the schema must match what it produced.
        if (projected is { Count: > 0 } && projected.Count != _outputColumns)
        {
            var fields = new List<Field>(projected.Count);
            foreach (var i in projected)
            {
                fields.Add(binding.OutputSchema.FieldsList[i]);
            }
            _projectedColumns = projected.Count;
            _projectedWire = WireSchemaFor(new Schema(fields, metadata: null));
        }
    }

    /// <summary>The per-row input columns, needed to import the host's array.</summary>
    public Schema InputSchema { get; }

    /// <summary>The output columns plus the trailing provenance column — the PROJECTED set when the caller
    /// narrowed it, since that is what a projection-aware author returns.</summary>
    public Schema WireSchema => _projectedWire ?? _fullWire;

    /// <summary>The wire schema for a given output schema: its fields, then the provenance column.</summary>
    public static Schema WireSchemaFor(Schema output)
    {
        var fields = new List<Field>(output.FieldsList.Count + 1);
        fields.AddRange(output.FieldsList);
        fields.Add(new Field(OriginColumn, Int32Type.Default, nullable: false));
        return new Schema(fields, metadata: null);
    }

    /// <summary>
    /// One batched call. Takes ownership of <paramref name="input"/> and of the batch the author returns; the
    /// returned stream carries at most one batch (empty = every input row was filtered out, which is a
    /// legitimate answer and NOT end-of-stream).
    /// </summary>
    public IArrowArrayStream Call(RecordBatch input)
    {
        int inputRows = input.Length;
        LateralResult result;
        using (input)
        {
            result = _session.Call(input);
        }
        var rows = result.Rows;
        int m = rows?.Length ?? 0;
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("lateral {Func}: call rows={In} out={Out} origin={Origin}", _func, inputRows, m,
                         result.Origin is null ? "identity" : "explicit");
        }
        if (rows is null || m == 0)
        {
            return new InMemoryArrayStream(WireSchema, System.Array.Empty<RecordBatch>());
        }
        // ⚠ TWO LEGAL SHAPES: the author honoured the projection hint, or ignored it and returned everything.
        // The count discriminates them and the host validates types on whichever arrives, so neither can be
        // read as the other.
        Schema wire;
        if (rows.ColumnCount == _outputColumns)
        {
            wire = _fullWire;
        }
        else if (_projectedWire is not null && rows.ColumnCount == _projectedColumns)
        {
            wire = _projectedWire;
        }
        else
        {
            throw new InvalidOperationException(
                $"fabricator: lateral function '{_func}' returned {rows.ColumnCount} columns but declared " +
                $"{_outputColumns}" +
                (_projectedWire is null ? "" : $" (or {_projectedColumns}, the projected subset)"));
        }
        int wireColumns = rows.ColumnCount;
        var origin = result.Origin;
        if (origin is null)
        {
            if (m != inputRows)
            {
                throw new InvalidOperationException(
                    $"fabricator: lateral function '{_func}' returned {m} rows for {inputRows} input rows but no " +
                    "provenance. A map that fans out or filters must return, per output row, the index of the " +
                    "input row that produced it (LateralResult.Origin); omitting it asserts a 1:1 mapping.");
            }
        }
        else if (origin.Length != m)
        {
            throw new InvalidOperationException(
                $"fabricator: lateral function '{_func}' returned {m} rows but {origin.Length} provenance " +
                "indices; there must be exactly one per output row");
        }

        var builder = new Int32Array.Builder().Reserve(m);
        for (int r = 0; r < m; r++)
        {
            // Range NOT checked here — see the class remarks: the host validates it, because the host is what
            // indexes the input chunk with it.
            builder.Append(origin is null ? r : origin[r]);
        }

        // The wire batch takes over the author's columns; the RecordBatch it came in is left undisposed on
        // purpose — disposing both would release the same buffers twice.
        var columns = new IArrowArray[wireColumns + 1];
        for (int c = 0; c < wireColumns; c++)
        {
            columns[c] = rows.Column(c);
        }
        columns[wireColumns] = builder.Build();
        return new InMemoryArrayStream(wire, new[] { new RecordBatch(wire, columns, m) });
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// What sits behind a <c>lateral_bind</c> handle: the bound binding plus everything a later
/// <c>lateral_open</c> needs, since that entry is handed only this handle.
/// </summary>
internal sealed class LateralBindingHandle : IDisposable
{
    public LateralBindingHandle(ILateralFunctionBinding binding, string func, Schema inputSchema)
    {
        Binding = binding;
        Func = func;
        InputSchema = inputSchema;
    }

    public ILateralFunctionBinding Binding { get; }
    public string Func { get; }
    public Schema InputSchema { get; }

    /// <summary>The output schema the HOST binds its return types from — the function's own columns, WITHOUT
    /// the provenance column (that is transport, not a result column).</summary>
    public Schema OutputSchema => Binding.OutputSchema;

    /// <summary>Open one per-thread session, told which output columns the caller reads (null = all).
    /// Several may be open at once — see <see cref="ILateralSession"/>.</summary>
    public LateralSessionRunner Open(IReadOnlyList<int>? projected) =>
        new(Binding, Func, InputSchema, projected);

    public void Dispose() => Binding.Dispose();
}
