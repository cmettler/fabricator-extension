// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Fabricator.Bridge;

/// <summary>
/// The wait between two attempts of a Delta commit that lost the race for a version: a random time between 0 and
/// <c>min(1 s, 10 ms · 2^(attempt − 1))</c> ("full jitter").
/// </summary>
/// <remarks>
/// Without it every loser reopens the table at once, so on a slow store the same writers collide again and some lose
/// every round: on Fabric's FUSE mount, 16 writers × 20 INSERTs, a writer needed 11–15 of 16 attempts for one commit
/// in two of three runs, and in one of them gave up (MEASURED 2026-11-02, docs/delta-transactions.md §8.5b).
/// engineered-wood's own commit retry does not wait either.
/// </remarks>
internal static class CommitBackoff
{
    private const int BaseMs = 10;
    private const int CapMs = 1000;

    public static Task DelayAsync(int attempt, CancellationToken cancellationToken)
    {
        int ceiling = attempt >= 8 ? CapMs : Math.Min(CapMs, BaseMs << Math.Max(0, attempt - 1));
        return Task.Delay(Random.Shared.Next(0, ceiling + 1), cancellationToken);
    }
}
