// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Globalization;

namespace Fabricator.Bridge;

/// <summary>
/// What our object-store files and filesystems report to engineered-wood as <c>IReadConcurrencyHint</c>: how many
/// requests they serve at once.
/// </summary>
/// <remarks>
/// <para>engineered-wood reads that hint to decide two things. Without it a file counts as LOCAL: a scan reads at
/// most <c>min(4, cores)</c> files and row groups at once, and a filtered read (an exact filter or row masks) decodes
/// in ONE pass, so the output columns of rejected rows are decoded anyway. With it, the read fans out to the hint and
/// a selected read goes in two passes: the predicate's columns first, the others only over the blocks that hold a
/// selected row (EW #502, phase 1b). Its own S3, Azure and GCS readers report 16.</para>
/// <para>We report 4, which is what engineered-wood used for us before (<c>min(4, cores)</c>), so the hint changes
/// only the two-pass decision. MEASURED on TPC-H SF10 lineitem against local RustFS: 8 and 16 made full and
/// scattered scans 10-40% SLOWER, while 4 with two passes took a join with clustered matches from 3.55 s to 2.10 s
/// (CPU 9.4 s to 3.1 s). A real remote store may want more; measure before raising it.</para>
/// <para><c>FABRICATOR_EW_READ_CONCURRENCY</c> sets the number; 0 reports nothing (the old behaviour).</para>
/// </remarks>
internal static class RemoteReadConcurrency
{
    /// <summary>
    /// Merge nearby ranges of one read into fewer, larger requests and fetch them concurrently (engineered-wood's
    /// <c>CoalescingFileReader</c>, which its own Azure reader uses), instead of one request after another. On by
    /// default; <c>FABRICATOR_COALESCE_RANGES=0</c> turns it off.
    /// </summary>
    internal static bool CoalesceRanges { get; } =
        Environment.GetEnvironmentVariable("FABRICATOR_COALESCE_RANGES") != "0";

    internal static int? Hint { get; } = Read();

    private static int? Read()
    {
        var text = Environment.GetEnvironmentVariable("FABRICATOR_EW_READ_CONCURRENCY");
        if (string.IsNullOrEmpty(text))
        {
            return 4;
        }
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 ? n : null;
    }
}
