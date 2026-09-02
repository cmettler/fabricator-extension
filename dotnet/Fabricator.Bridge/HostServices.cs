// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace Fabricator.Bridge;

/// <summary>
/// The bridge's implementations of the plugin-facing host services, and the one place they are published.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>Every method reads the AMBIENT <c>ClientContext</c> per call and captures nothing.</b> That is the
/// rule these classes exist to keep in one place: a catalog is DATABASE-scoped and outlives the connection
/// that attached it, so a held <c>ClientContext*</c> dangles the day that connection closes — the
/// <c>table_stats</c> SIGSEGV class. It is also the more correct answer for SECRETS, which a user may create
/// after the ATTACH and per session.
/// </para>
/// <para>
/// These replace the <c>HostHttpTransport</c> / <c>HostQueryTransport</c> static-delegate seams. The seam
/// pattern was reached for three times in two weeks and a fourth was written and deleted the same day; the
/// locator is what stops there being a fifth. See docs/plugin-services.md.
/// </para>
/// </remarks>
internal static class HostServices
{
    /// <summary>
    /// Publishes the host services a plugin can resolve. Called from <c>Bootstrap.Initialize</c> once the
    /// host-services block is cached, and BEFORE any plugin is scanned — so a plugin resolving one at load
    /// time still finds it, though it should resolve lazily anyway (<see cref="FabricatorServices"/>).
    /// </summary>
    /// <remarks>
    /// ⚠ A capability the host did not register is NOT published, so <c>Get&lt;T&gt;()</c> answers null and
    /// <c>GetRequired&lt;T&gt;()</c> names the interface. Publishing an implementation that always throws
    /// would make "the host cannot do this" indistinguishable from "the call was wrong".
    /// </remarks>
    internal static void Publish()
    {
        // The host fills its whole services block at once, so these three flags move together in practice;
        // each is tested anyway so that an older host missing one capability loses only that service.
        if (HostFs.Available || HostFs.CanGlob)
        {
            FabricatorServices.Register<IHostFileSystem>(new HostFileSystemService());
        }
        if (HostFs.CanHttp)
        {
            FabricatorServices.Register<IHostHttp>(new HostHttpService());
        }
        if (HostFs.CanQuery)
        {
            FabricatorServices.Register<IHostQuery>(new HostQueryService());
        }
    }
}

/// <summary>DuckDB's FileSystem, over <see cref="HostFs"/>.</summary>
internal sealed class HostFileSystemService : IHostFileSystem
{
    public byte[] ReadAllBytes(string path, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "maxBytes must be > 0.");
        }
        return HostFs.ReadAllBytes(AmbientOpener.Current, path, maxBytes);
    }

    public IReadOnlyList<HostFileEntry> Glob(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (!HostFs.CanGlob)
        {
            throw new NotSupportedException("The host did not register fs_glob, so globbing is unavailable.");
        }
        var json = HostFs.Glob(AmbientOpener.Current, pattern);
        var result = new List<HostFileEntry>();
        using var doc = JsonDocument.Parse(json);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var path = el.GetProperty("path").GetString() ?? string.Empty;
            // -1, not 0, when absent: the local filesystem reports no size in a listing (DuckDB's FileSystem
            // has no path-stat), and a 0 there would read as "empty file".
            long size = el.TryGetProperty("size", out var s) ? s.GetInt64() : -1;
            result.Add(new HostFileEntry(path, size));
        }
        return result;
    }
}

/// <summary>DuckDB's HTTP stack, over <see cref="HostFs"/>.</summary>
internal sealed class HostHttpService : IHostHttp
{
    public (string ResponseJson, byte[]? Body) Send(string method, string url, string? headersJson, byte[]? body) =>
        HostFs.HttpRequest(AmbientOpener.Current, method, url, headersJson, body);
}

/// <summary>SQL on the hosting DuckDB, over <see cref="Host"/>.</summary>
internal sealed class HostQueryService : IHostQuery
{
    public IArrowArrayStream Query(string sql, RecordBatch? parameters = null, bool inheritSession = true) =>
        // ⚠ The ambient ClientContext is passed as the caller's SESSION (ABI v83), so a plugin's query
        // resolves names and times the way the statement that reached it does. 0 = a clean session, which is
        // also what an absent ambient degrades to.
        Host.Query(sql, parameters, null, clientSession: inheritSession ? AmbientOpener.Current : 0);

    public long ExecuteNonQuery(string sql) => Host.ExecuteNonQuery(sql);
}
