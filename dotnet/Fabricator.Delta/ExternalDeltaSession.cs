// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using Apache.Arrow.Ipc;
using Microsoft.Extensions.Logging;

namespace Fabricator.Bridge;

/// <summary>
/// The Delta side of an explicit DuckDB transaction that INSERTs into SQL Server external tables. One transient
/// delta catalog per storage root, each inside its own explicit Delta transaction: an INSERT writes its data
/// files at statement time and parks the actions, <see cref="Commit"/> turns them into one Delta commit per
/// table, and <see cref="Rollback"/> discards them and deletes the files.
/// </summary>
/// <remarks>
/// <para>Only appends. A buffered DELETE or UPDATE needs deletion vectors, and an external table must not carry
/// one: SQL Server's Delta reader refuses a table with a materialized deletion vector (19726).</para>
/// <para>Delta has no transaction across tables, so several tables commit one after the other: when a later
/// one fails, the earlier ones stay committed.</para>
/// <para>Thread-safe: an INSERT arrives on the bulk session's consumer thread.</para>
/// </remarks>
public sealed class ExternalDeltaSession
{
    private static readonly ILogger Log = FabricatorLog.CreateLogger("Fabricator.Sql.External");

    private readonly long _txnId;
    private readonly Dictionary<string, IProviderCatalog> _catalogs = new(StringComparer.Ordinal);
    private bool _done;

    public ExternalDeltaSession(long txnId) => _txnId = txnId;

    internal long Append(string root, string leaf, IArrowArrayStream data, bool checkConstraints, long txnId)
    {
        if (txnId != _txnId)
        {
            throw new InvalidOperationException(
                $"external delta append for transaction {txnId} reached the session of transaction {_txnId}.");
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
        WithAmbient(() => rows = catalog.BulkInsert("main", leaf, data, createTable: false, replace: false,
            checkConstraints, _txnId, partitionColumns: null, sortColumns: null, schemaMode: null,
            partitionOverwrite: false, optionsJson: null));
        return rows;
    }

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
