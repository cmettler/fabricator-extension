// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Concurrent;

namespace Fabricator.Bridge;

/// <summary>
/// The last snapshot each table's read path saw, kept across transactions, so a new transaction's first open of a
/// table replays only the commits after it: one <c>_delta_log</c> listing when nothing is new, instead of the
/// listing, <c>_last_checkpoint</c>, the checkpoint and every commit since (engineered-wood #740,
/// <c>DeltaTable.OpenAsync(fs, Snapshot startFrom, …)</c>).
/// </summary>
/// <remarks>
/// <para>Freshness is unchanged: every open still lists the log and reaches its latest version. Whether continuing
/// from the stored snapshot is right is engineered-wood's decision, and it rebuilds wherever it cannot show that:
/// log cleanup past the snapshot, or a table dropped and re-created at the same path, recognised by the identity of
/// the log files at the snapshot's version (#746). That identity is the ETag where the listing has one (ADLS,
/// OneLake, S3), else size and modification time, which is why our listings report both.</para>
/// <para>Keyed by table location, as engineered-wood's host guide asks: a snapshot does not know which table it
/// came from.</para>
/// <para>Used by the read path (<c>DeltaReader.OpenForReadAsync</c>), the streaming write
/// (<c>DeltaWriter.TryWriteStreaming</c>) and the commit of deferred files (<c>FlushDeferredFilesAsync</c>): each
/// opens with one log listing instead of two or three, and still at the latest version, which is what the commit's
/// conflict check needs. A transaction still reads one version per table: the first open publishes its table into
/// the transaction's binding and later references are hits there.</para>
/// <para>On by default; the ATTACH option <c>snapshot_cache false</c> turns it off.</para>
/// </remarks>
public sealed partial class DeltaCatalog
{
    private readonly ConcurrentDictionary<string, EngineeredWood.DeltaLake.Snapshot.Snapshot> _snapshots =
        new(StringComparer.Ordinal);

    internal bool SnapshotCacheEnabled => _snapshotCache;

    internal EngineeredWood.DeltaLake.Snapshot.Snapshot? StoredSnapshot(string path)
        => _snapshotCache && _snapshots.TryGetValue(path, out var s) ? s : null;

    /// <summary>Keeps <paramref name="snapshot"/> unless a newer one of the same table is already stored. A
    /// different table id (a re-created table) always replaces.</summary>
    internal void StoreSnapshot(string path, EngineeredWood.DeltaLake.Snapshot.Snapshot snapshot)
    {
        if (!_snapshotCache)
        {
            return;
        }
        _snapshots.AddOrUpdate(path, snapshot, (_, held) =>
            held.Metadata.Id != snapshot.Metadata.Id || snapshot.Version >= held.Version ? snapshot : held);
    }

    /// <summary>Forgets a table's snapshot: our own DROP of that path. Not needed for correctness (a replaced log is
    /// recognised), but there is nothing left to continue from.</summary>
    internal void ForgetSnapshot(string path) => _snapshots.TryRemove(path, out _);
}
