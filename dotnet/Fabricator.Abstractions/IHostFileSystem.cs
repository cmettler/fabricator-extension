// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Generic;

namespace Fabricator.Bridge;

/// <summary>One entry from <see cref="IHostFileSystem.Glob"/>.</summary>
/// <param name="Path">The resolved path, in the scheme the pattern was written in.</param>
/// <param name="Size">
/// The size in bytes, or <c>-1</c> when the host's listing did not carry one. ⚠ Object stores report it in
/// the listing; a local filesystem does not, because DuckDB's <c>FileSystem</c> has no path-stat — a size
/// there costs an OPEN. Treat it as best-effort: never branch on <c>Size == 0</c> meaning "empty".
/// </param>
public readonly record struct HostFileEntry(string Path, long Size);

/// <summary>
/// DuckDB's own <c>FileSystem</c>, as a host service (<see cref="FabricatorServices"/>). Reads go through
/// whatever the host can reach — local paths, <c>s3://</c>, <c>abfss://</c>, <c>onelake://</c>, anything a
/// loaded filesystem extension registers — and resolve SECRETS from the calling session, so a plugin reading
/// a file needs no credential surface of its own.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>No opener is passed, deliberately</b> — the bridge's implementation reads the AMBIENT
/// <c>ClientContext</c> at call time. See <see cref="FabricatorServices"/> for why capturing one would dangle
/// and for the crossing rule that follows.
/// </para>
/// <para>
/// ⚠⚠ <b>THE AMBIENT IS THE WHOLE STORY HERE, AND IT WAS MISSING FOR GLOBAL FUNCTIONS UNTIL ABI v82.</b> An
/// earlier attempt at this seam died with an access violation on its first call, because every <c>fs_*</c>
/// host callback dereferences the calling operator's <c>ClientContext</c> and a GLOBAL function had none.
/// The scalar crossings now carry <c>(opener, session, txn)</c>, so this is reachable from a plugin's global
/// scalar — which is exactly what <c>plug_read_file</c> in the sample plugin exists to prove. A caller with
/// no ambient gets a refusal naming the missing context, not a crash.
/// </para>
/// <para>
/// ⚠ <b>The surface is deliberately small: read-all and glob.</b> It is what is demonstrably needed, and a
/// filesystem interface is easy to widen and impossible to narrow. Streaming, writing and directory
/// manipulation are all reachable inside the bridge and are NOT exposed until something needs them — an
/// unused member on a plugin-facing contract is a compatibility obligation bought for nothing.
/// </para>
/// <para>
/// ⚠ <b>For reading a file whose ABSENCE is an ordinary outcome, prefer <c>read_blob</c> through
/// <see cref="IHostQuery"/>.</b> It returns ZERO ROWS for a missing path, so absence is ESTABLISHED by the
/// engine rather than guessed from an exception message, and it reports <c>size</c> and <c>last_modified</c>
/// besides. That is why the Fluid template provider uses it and not this (docs/fluid-templating.md §10).
/// This interface is the right one when you have a path you expect to exist.
/// </para>
/// </remarks>
public interface IHostFileSystem
{
    /// <summary>
    /// Reads <paramref name="path"/> in full.
    /// </summary>
    /// <param name="path">Any path the host can open, including remote schemes.</param>
    /// <param name="maxBytes">
    /// A hard ceiling. The read FAILS rather than truncating when the file is larger — a silent truncation is
    /// a wrong ANSWER, where the ceiling only turns an out-of-memory into a sentence naming the file and both
    /// sizes. It is what stops a mistyped path buffering a multi-gigabyte object.
    /// </param>
    byte[] ReadAllBytes(string path, long maxBytes);

    /// <summary>
    /// Expands a DuckDB glob pattern. Returns an EMPTY list when nothing matches — that is an answer, not a
    /// failure.
    /// </summary>
    /// <remarks>
    /// ⚠ The pattern is DuckDB's, so <c>* ? [ ]</c> are metacharacters: a path containing one is a pattern,
    /// not a literal. A caller holding a user-supplied path that must match itself should read it directly
    /// rather than glob it.
    /// </remarks>
    IReadOnlyList<HostFileEntry> Glob(string pattern);
}
