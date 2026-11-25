// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow.Adbc;
using Fabricator.Bridge;

namespace Fabricator.Adbc;

/// <summary>
/// One DuckDB transaction's driver connection: opened, with autocommit off, by the transaction's first write — or, on a
/// DuckDB server, by the first read of an explicit transaction (so the whole transaction reads one server snapshot,
/// like DuckDB itself) — and committed or rolled back with the DuckDB transaction (also the implicit one around a single
/// statement). Every later use goes through it, so the transaction sees its own writes.
/// </summary>
/// <remarks>
/// ⚠ A driver connection runs one statement at a time (quack: a new query supersedes the running one). So every use
/// enters <see cref="Use"/>, which makes the scan still open on the connection (<see cref="Active"/>) read the rest of
/// its result into memory first: a scan streams until something else needs the connection, and only then buffers.
/// ⚠ Never hold the connection while waiting for the HOST: a load whose input comes from a scan the host has not
/// opened yet would wait for it forever (the scan waits for the connection). Loads read their input in chunks outside
/// <see cref="Use"/> and hold the connection only to send each chunk.
/// </remarks>
internal sealed class AdbcTransaction : ITransaction
{
    private readonly object _gate = new();

    public AdbcTransaction(long id) => Id = id;

    public long Id { get; }

    public bool IsExplicit { get; set; }

    /// <summary>The pinned connection; null until the transaction pins it.</summary>
    public AdbcConnection? Connection { get; set; }

    /// <summary>The scan streaming from the connection, if one is open.</summary>
    internal PinnedStream? Active { get; private set; }

    /// <summary>Exclusive use of the connection: the open scan is read whole first. Dispose to release.</summary>
    public Lease Use()
    {
        Monitor.Enter(_gate);
        try
        {
            Active?.Buffer();
            Active = null;
            return new Lease(this);
        }
        catch
        {
            Monitor.Exit(_gate);
            throw;
        }
    }

    /// <summary>Opens the transaction's connection with <paramref name="open"/> if it has none yet.</summary>
    public void Pin(Func<AdbcConnection> open)
    {
        lock (_gate)
            Connection ??= open();
    }

    /// <summary>Tries for the connection for a while, WITHOUT making the open scan step aside (a scan reading its
    /// next batch).</summary>
    internal bool TryEnter(int milliseconds) => Monitor.TryEnter(_gate, milliseconds);

    internal void Exit() => Monitor.Exit(_gate);

    /// <summary>Under the connection: <paramref name="stream"/> is now the scan streaming from it.</summary>
    internal void SetActive(PinnedStream stream) => Active = stream;

    /// <summary>Under the connection: <paramref name="stream"/> finished or was closed.</summary>
    internal void Release(PinnedStream stream)
    {
        if (ReferenceEquals(Active, stream))
            Active = null;
    }

    public void Complete(bool commit)
    {
        lock (_gate)
        {
            // a scan still open when its transaction ends is closed, not read: its statement is over
            Active?.Abandon();
            Active = null;
            var connection = Connection;
            Connection = null;
            if (connection is null)
                return;
            try
            {
                if (commit)
                    connection.Commit();
                else
                    connection.Rollback();
            }
            finally
            {
                connection.Dispose();
            }
        }
    }

    public void Dispose() => Complete(commit: false);

    /// <summary>Exclusive use of the connection, released on dispose.</summary>
    public readonly struct Lease : IDisposable
    {
        private readonly AdbcTransaction _txn;

        public Lease(AdbcTransaction txn) => _txn = txn;

        public AdbcConnection Connection => _txn.Connection!;

        public void Dispose() => Monitor.Exit(_txn._gate);
    }
}
