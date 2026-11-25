// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow.Adbc;
using Fabricator.Bridge;

namespace Fabricator.Adbc;

/// <summary>
/// One DuckDB transaction's driver connection: opened, with autocommit off, by the transaction's first write, and
/// committed or rolled back with the DuckDB transaction (also the implicit one around a single statement). Reads after
/// that write go through it too, so the transaction sees its own writes.
/// </summary>
/// <remarks>
/// ⚠ A driver connection runs one statement at a time (quack: a new query supersedes the running one), so every use
/// holds <see cref="Gate"/> and a read drains its whole result before the next statement starts.
/// </remarks>
internal sealed class AdbcTransaction : ITransaction
{
    public AdbcTransaction(long id) => Id = id;

    public long Id { get; }

    public bool IsExplicit { get; set; }

    /// <summary>The pinned connection; null until the first write.</summary>
    public AdbcConnection? Connection { get; set; }

    public object Gate { get; } = new();

    public void Complete(bool commit)
    {
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

    public void Dispose() => Complete(commit: false);
}
