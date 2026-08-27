// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using Apache.Arrow;
using Apache.Arrow.Types;
using Fabricator.Bridge;

namespace Fabricator.SqlServer;

/// <summary>
/// <c>db.cdc.inc_position(&lt;lsn&gt;)</c> / <c>db.cdc.dec_position(&lt;lsn&gt;)</c> — the NEXT and PREVIOUS
/// representable log sequence numbers, mirroring <c>sys.fn_cdc_increment_lsn</c> and
/// <c>sys.fn_cdc_decrement_lsn</c>.
/// </summary>
/// <remarks>
/// <para><b>⚠ COMPUTED IN C#, NOT BY THE SERVER — and that is a performance requirement rather than a
/// shortcut.</b> A scalar is invoked per BATCH over N rows, so a round trip inside <c>Invoke</c> would put a
/// server call on every chunk of every query that uses one, and would need a connection where none is
/// otherwise required. An LSN is a 10-byte BIG-ENDIAN integer and these are ±1 on it. What makes that safe
/// is not the reasoning but the check: the results are MEASURED equal to SQL Server's own functions across
/// carries, borrows and both wrap boundaries (<c>verify_mssql_cdc</c> §33 compares the two side by side in
/// one statement).</para>
/// <para><b>⚠⚠ BOTH SQL SERVER FUNCTIONS WRAP AROUND SILENTLY, AND THESE REFUSE INSTEAD.</b> MEASURED:
/// <c>fn_cdc_decrement_lsn(0x0000000000000000000)</c> returns <c>0xFFFFFFFFFFFFFFFFFFFF</c> and
/// <c>fn_cdc_increment_lsn(0xFFFFFFFFFFFFFFFFFFFF)</c> returns <c>0x00000000000000000000</c>. A wrapped
/// value is not a neighbouring position, it is the OPPOSITE END of the range — as a window bound it either
/// sits far above the capture watermark or lands on the zero LSN, which §19 records is exactly what
/// <c>fn_cdc_get_min_lsn</c> answers for an instance that does not exist. Either way the reader's pre-check
/// would report something that has nothing to do with what the caller asked. Throwing is the only answer
/// that cannot mislead.</para>
/// <para><b>⚠⚠ A 21-BYTE <c>_position</c> IS REFUSED, and this is the sharp edge of the whole pair.</b>
/// Incrementing the LSN of a row's position would step PAST every remaining row at that LSN — a transaction
/// commonly has several — so using the result as an exclusive lower bound would SKIP them silently. And it
/// is not needed: <c>starting_position</c> is already exclusive at full 21-byte granularity (§17), so
/// "changes strictly after this row" is what passing the row's own position already means. These operate on
/// LSNs, take one, and return one.</para>
/// <para>⚠ NULL in, NULL out — matching both the SQL functions (MEASURED) and this surface's convention that
/// an absent bound is a legitimate state rather than an error.</para>
/// <para>⚠ CONSISTENT rather than VOLATILE: same input, same output, no side effects and no server contact.
/// That also lets a call over constant arguments FOLD at plan time, which is what makes
/// <c>starting_position := cdc.inc_position(...)</c> usable as a table-function argument at all — those must
/// be constant.</para>
/// </remarks>
internal sealed class CdcIncPositionFunction : CdcPositionMathFunction
{
    public override string Name => "inc_position";

    private protected override bool Increment => true;
}

/// <summary>The decrementing half — see <see cref="CdcIncPositionFunction"/>.</summary>
internal sealed class CdcDecPositionFunction : CdcPositionMathFunction
{
    public override string Name => "dec_position";

    private protected override bool Increment => false;
}

/// <summary>Shared body of <c>inc_position</c> / <c>dec_position</c>.</summary>
internal abstract class CdcPositionMathFunction : ICatalogScalarFunction
{
    public string SchemaName => SqlServerCdcFunctions.SchemaName;

    public abstract string Name { get; }

    private protected abstract bool Increment { get; }

    /// <summary>
    /// Positional, and deliberately NOT named: DuckDB's <c>ScalarFunction</c> has no named-parameter concept,
    /// so declaring one is a registration error rather than sugar.
    /// </summary>
    public Schema Parameters { get; } = new(new[]
    {
        new Field("lsn", BinaryType.Default, nullable: true),
    }, metadata: null);

    public Field Result => new(Name, BinaryType.Default, nullable: true);

    /// <summary>Pure: no server contact, no state, foldable over constant arguments.</summary>
    public bool IsVolatile => false;

    public IArrowArray Invoke(RecordBatch args)
    {
        var builder = new BinaryArray.Builder();
        for (int row = 0; row < args.Length; row++)
        {
            // ⚠ Through ArrowValueReader rather than `as BinaryArray`, for the reason min_position records:
            // a failed cast would silently emit NULLs, and here NULL means "no bound".
            if (ArrowValueReader.ReadScalar(args.Column(0), row) is not byte[] value)
            {
                builder.AppendNull();
                continue;
            }
            builder.Append(Step(value, Increment, Name).AsSpan());
        }
        return builder.Build();
    }

    /// <summary>±1 on a 10-byte big-endian LSN, refusing both the wrong length and the wrap.</summary>
    internal static byte[] Step(byte[] value, bool increment, string name)
    {
        if (value.Length == CdcChangesPlan.PositionBytes)
        {
            throw new ArgumentException(
                $"cdc.{name}: a 21-byte _position is not accepted, only a 10-byte log sequence number. "
                + "Stepping the LSN of a row's position would move PAST the other rows at that same LSN - a "
                + "transaction usually has several - so using the result as a lower bound would skip them "
                + "silently. You almost certainly do not need it: starting_position is already EXCLUSIVE at "
                + "full 21-byte granularity, so passing a row's own _position already means 'the changes "
                + "strictly after this row'. cdc.max_position() and cdc.min_position() return the 10-byte "
                + "form this takes.");
        }
        if (value.Length != CdcChangesPlan.LsnBytes)
        {
            throw new ArgumentException(
                $"cdc.{name}: expected a 10-byte log sequence number, got {value.Length} bytes. "
                + "cdc.max_position() and cdc.min_position() return that form.");
        }
        var result = (byte[])value.Clone();
        if (increment)
        {
            for (int i = result.Length - 1; i >= 0; i--)
            {
                if (++result[i] != 0)
                {
                    return result;
                }
            }
        }
        else
        {
            for (int i = result.Length - 1; i >= 0; i--)
            {
                if (result[i]-- != 0)
                {
                    return result;
                }
            }
        }
        // ⚠⚠ EVERY byte carried or borrowed, i.e. the input was all-0xFF (incrementing) or all-zero
        // (decrementing) and the result WRAPPED to the opposite end of the range. SQL Server returns that
        // wrapped value; we refuse it, because as a window bound it is not a neighbouring position at all.
        throw new ArgumentException(
            $"cdc.{name}: {CdcChangesPlan.Hex(value)} is the "
            + (increment ? "HIGHEST" : "LOWEST")
            + " representable log sequence number, so there is no "
            + (increment ? "next" : "previous")
            + " one. SQL Server's sys.fn_cdc_" + (increment ? "increment" : "decrement")
            + "_lsn WRAPS AROUND here and answers "
            + (increment ? "0x00000000000000000000" : "0xFFFFFFFFFFFFFFFFFFFF")
            + " - the opposite end of the range - which as a window bound would silently mean something "
            + "entirely different from what you asked for. "
            + (increment
                   ? "Use cdc.max_position() for the current watermark."
                   : "A zero LSN is also what SQL Server answers for a capture instance that does not "
                     + "exist, so check where this value came from."));
    }
}
