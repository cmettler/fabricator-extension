// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using Apache.Arrow.Ipc;
using Microsoft.Extensions.Logging;

namespace Fabricator.Bridge;

/// <summary>
/// The Delta side of an explicit DuckDB transaction that writes SQL Server external tables. One transient delta
/// catalog per storage root, each inside its own explicit Delta transaction: an INSERT writes its data files at
/// statement time and a DELETE or UPDATE buffers its rows, <see cref="Commit"/> turns each table's pending work
/// into one Delta commit, and <see cref="Rollback"/> discards it and deletes the files.
/// </summary>
/// <remarks>
/// <para>A buffered DELETE or UPDATE on a table without deletion vectors is staged copy-on-write at COMMIT, so a
/// protocol-1.0 table stays readable by SQL Server, whose Delta reader refuses a materialized deletion vector
/// (19726).</para>
/// <para>SQL Server reads the COMMITTED table, so it does not see what this session has written. Statements
/// whose rows or values come from SQL Server's read (the identity-keyed DELETE and UPDATE) are therefore only
/// correct before this session has written to that table; <see cref="HasWrites"/> lets the caller refuse them.
/// </para>
/// <para>Delta has no transaction across tables, so several tables commit one after the other: when a later
/// one fails, the earlier ones stay committed.</para>
/// <para>Thread-safe: an INSERT arrives on the bulk session's consumer thread.</para>
/// </remarks>
public sealed class ExternalDeltaSession
{
    private static readonly ILogger Log = FabricatorLog.CreateLogger("Fabricator.Sql.External");

    private readonly long _txnId;
    private readonly Dictionary<string, IProviderCatalog> _catalogs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _written = new(StringComparer.Ordinal);
    private bool _done;

    public ExternalDeltaSession(long txnId) => _txnId = txnId;

    internal long Append(string root, string leaf, IArrowArrayStream data, bool checkConstraints, long txnId) =>
        Run(root, leaf, txnId, catalog => catalog.BulkInsert("main", leaf, data, createTable: false, replace: false,
            checkConstraints, _txnId, partitionColumns: null, sortColumns: null, schemaMode: null,
            partitionOverwrite: false, optionsJson: null));

    /// <summary>Runs one write on <paramref name="leaf"/> through this transaction's catalog for
    /// <paramref name="root"/>, opening it on first use. A write that changed rows marks the table as written.
    /// </summary>
    internal long Run(string root, string leaf, long txnId, Func<IProviderCatalog, long> write)
    {
        if (txnId != _txnId)
        {
            throw new InvalidOperationException(
                $"external delta write for transaction {txnId} reached the session of transaction {_txnId}.");
        }
        IProviderCatalog catalog;
        lock (_catalogs)
        {
            if (_done)
            {
                throw new InvalidOperationException("external delta session is already completed.");
            }
            if (!_catalogs.TryGetValue(root, out catalog!))
            {
                catalog = ProviderRegistry.Resolve("delta").OpenCatalog(root, "{\"native_write\":\"true\"}");
                WithAmbient(() => catalog.BeginTransaction(isExplicit: true));
                _catalogs[root] = catalog;
            }
        }
        long rows = 0;
        WithAmbient(() => rows = write(catalog));
        if (rows > 0)
        {
            lock (_catalogs)
            {
                _written.Add(Key(root, leaf));
            }
        }
        return rows;
    }

    /// <summary>Whether this session has changed rows of <paramref name="leaf"/> under <paramref name="root"/>.
    /// </summary>
    internal bool HasWrites(string root, string leaf)
    {
        lock (_catalogs)
        {
            return _written.Contains(Key(root, leaf));
        }
    }

    private static string Key(string root, string leaf) => root + "/" + leaf;

    /// <summary>Commits every root's parked appends. On a failure the roots not yet committed are rolled back and
    /// the error is rethrown.</summary>
    public void Commit()
    {
        var catalogs = Take();
        for (int i = 0; i < catalogs.Count; i++)
        {
            try
            {
                WithAmbient(catalogs[i].CommitTransaction);
            }
            catch
            {
                for (int j = i; j < catalogs.Count; j++)
                {
                    RollbackOne(catalogs[j]);
                }
                for (int j = i + 1; j < catalogs.Count; j++)
                {
                    catalogs[j].Dispose();
                }
                catalogs[i].Dispose();
                throw;
            }
            catalogs[i].Dispose();
        }
    }

    /// <summary>Discards every root's parked appends and deletes their data files. Never throws.</summary>
    public void Rollback()
    {
        foreach (var catalog in Take())
        {
            RollbackOne(catalog);
            catalog.Dispose();
        }
    }

    private List<IProviderCatalog> Take()
    {
        lock (_catalogs)
        {
            _done = true;
            var list = new List<IProviderCatalog>(_catalogs.Values);
            _catalogs.Clear();
            _written.Clear();
            return list;
        }
    }

    private void RollbackOne(IProviderCatalog catalog)
    {
        try
        {
            WithAmbient(catalog.RollbackTransaction);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "external delta rollback of transaction {Txn} failed; its data files stay as orphans",
                _txnId);
        }
    }

    // The delta catalog keys its transaction by the ambient id; a teardown rollback may run where none is set.
    private void WithAmbient(Action action)
    {
        long saved = AmbientTransaction.Current;
        AmbientTransaction.Current = _txnId;
        try
        {
            action();
        }
        finally
        {
            AmbientTransaction.Current = saved;
        }
    }
}
