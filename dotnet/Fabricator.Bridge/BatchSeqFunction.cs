// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// <c>fabricator_batch_seq()</c> — the 1-based position of each row within the Arrow batch the bridge
/// received. A demo, and the gate for <b>ZERO-ARGUMENT SCALAR FUNCTIONS</b>.
/// </summary>
/// <remarks>
/// <para>Zero-argument scalars were long recorded as impossible "because a scalar's arg batch is also how the
/// row COUNT crosses". That reason was wrong, and this function is the proof: a 0-column Arrow array carries
/// its length perfectly well. The real obstacle was narrower — Apache.Arrow (23.0.0) cannot represent a
/// zero-FIELD <i>schema</i> across the C interface in either direction. The host therefore marshals one
/// throwaway column (see the zero-argument note in <c>BuildFabricatorScalarFunction</c>); nothing here has to
/// know about it, because a zero-argument function reads only <see cref="RecordBatch.Length"/>.</para>
/// <para>It returns a VARYING value per row on purpose. A constant would still prove the row count crossed
/// (the host fills N result rows), but a varying one additionally pins that the function is invoked
/// <i>per row</i> rather than once — the property being claimed. Volatile by default, so it is never folded
/// to a literal.</para>
/// <para>⚠ The sequence restarts per batch, so it is only stable below one vector (2048 rows). It is a
/// diagnostic, not a row-numbering function — use the SQL <c>row_number()</c> window function for that.</para>
/// </remarks>
public sealed class BatchSeqFunction : IScalarFunction
{
    public string Name => "fabricator_batch_seq";

    /// <summary>No parameters — the entire point of this function.</summary>
    public Schema Parameters => new(System.Array.Empty<Field>(), metadata: null);

    public Field Result => new("result", Int64Type.Default, nullable: false);

    public IArrowArray Invoke(RecordBatch args)
    {
        var b = new Int64Array.Builder();
        // Length, NOT ColumnCount: the batch carries one throwaway column from the host, and a zero-argument
        // function must ignore whatever arrives and answer for each ROW.
        for (int i = 0; i < args.Length; i++)
        {
            b.Append(i + 1);
        }
        return b.Build();
    }
}
