// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Generic;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Files.DataLake;

namespace Fabricator.Bridge;

/// <summary>
/// Table discovery for a plain (non-OneLake) ADLS Gen2 root, by walking DFS <b>directories</b> through the
/// Azure DataLake SDK.
///
/// <para><b>Why not the Unity Catalog REST API.</b> That is a Fabric service: it answers for a lakehouse ITEM
/// inside a workspace and has no meaning for a storage account, so a plain ADLS root has no catalog to ask and
/// the table list can only come from the filesystem. That is the whole OneLake-vs-ADLS split — same transport,
/// different way of learning what tables exist — and it is why <see cref="FabricLakehouse.IsOneLake"/> gates
/// discovery while <see cref="AdlsPath.IsAdlsGen2"/> gates IO.</para>
///
/// <para><b>Why not the host glob</b> (which does work — measured — on plain ADLS, unlike OneLake where
/// duckdb-azure's mid-path wildcard is broken): <c>&lt;root&gt;/*/_delta_log/*.json</c> matches every COMMIT
/// FILE of every table, so its result grows with table HISTORY rather than table COUNT — a single table with a
/// thousand commits returns a thousand entries to answer "does this table exist". This walk lists directories
/// only: one listing per level plus one existence check per candidate, i.e. O(tables). It also drops
/// discovery's dependency on duckdb-azure for an attach that already carries its own credential.</para>
/// </summary>
internal static class AdlsTableDiscovery
{
    /// <summary>Discovers (schema, table) pairs under <paramref name="root"/>. Flat layout ⇒ every child
    /// directory holding a <c>_delta_log</c> is a table in <paramref name="mainSchema"/>; <paramref name="schemas"/>
    /// ⇒ the two-level <c>&lt;root&gt;/&lt;schema&gt;/&lt;table&gt;</c> shape.</summary>
    public static SortedSet<(string Schema, string Table)> Discover(
        string root, AdlsCredential credential, bool schemas, string mainSchema)
        => DiscoverAsync(root, credential, schemas, mainSchema).GetAwaiter().GetResult();

    private static async Task<SortedSet<(string, string)>> DiscoverAsync(
        string root, AdlsCredential credential, bool schemas, string mainSchema)
    {
        var pairs = new SortedSet<(string, string)>();
        var (host, fileSystem, rootUnderFs) = AdlsGen2TableFileSystem.ParseAbfss(root);
        var fsClient = credential.CreateFileSystemClient(host, fileSystem);

        var schemaDirs = new List<(string Schema, string Path)>();
        if (schemas)
        {
            foreach (var dir in await ChildDirectoriesAsync(fsClient, rootUnderFs).ConfigureAwait(false))
            {
                schemaDirs.Add((LeafName(dir), dir));
            }
        }
        else
        {
            schemaDirs.Add((mainSchema, rootUnderFs));
        }

        foreach (var (schemaName, schemaPath) in schemaDirs)
        {
            foreach (var tableDir in await ChildDirectoriesAsync(fsClient, schemaPath).ConfigureAwait(false))
            {
                var log = fsClient.GetDirectoryClient(tableDir + "/_delta_log");
                if (await log.ExistsAsync().ConfigureAwait(false))
                {
                    pairs.Add((schemaName, LeafName(tableDir)));
                }
            }
        }
        return pairs;
    }

    /// <summary>Immediate child directories of <paramref name="pathUnderFs"/>, as filesystem-relative paths.
    /// A missing directory ⇒ empty: a root that does not exist yet is a legitimate state (nothing has been
    /// written), not an error, and an attach must survive it so the first CREATE can materialize it.</summary>
    private static async Task<List<string>> ChildDirectoriesAsync(DataLakeFileSystemClient fsClient, string pathUnderFs)
    {
        var dirs = new List<string>();
        try
        {
            await foreach (var item in fsClient.GetPathsAsync(path: pathUnderFs, recursive: false).ConfigureAwait(false))
            {
                if (item.IsDirectory == true && !string.IsNullOrEmpty(item.Name))
                {
                    dirs.Add(item.Name);
                }
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
        return dirs;
    }

    private static string LeafName(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? path : path.Substring(slash + 1);
    }
}
