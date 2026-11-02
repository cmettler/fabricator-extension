// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EngineeredWood.IO;

namespace Fabricator.Bridge;

/// <summary>
/// A read-only <see cref="ITableFileSystem"/> (engineered-wood) whose IO is delegated to DuckDB's
/// <c>FileSystem</c> via the host reverse-callbacks (<see cref="HostFs"/>). So a managed lakehouse reader
/// (Delta, …) reads <c>az://</c>/<c>s3://</c>/<c>https://</c>/local paths with DuckDB's secrets + backends —
/// one auth config shared with native DuckDB reads, no cloud SDK duplication.
///
/// Paths are resolved relative to the table root (matching <c>LocalTableFileSystem</c>): <see cref="ListAsync"/>
/// returns paths relative to the root, and they are passed back to <see cref="OpenReadAsync"/> /
/// <see cref="ReadAllBytesAsync"/> which re-resolve against the root. The <paramref name="opener"/> (the calling
/// operator's ClientContext) carries secret resolution and is valid for the duration of the synchronous call
/// that drives the read (a table-function execution). Write operations are not supported.
/// </summary>
internal sealed unsafe class DuckDbTableFileSystem : ITableFileSystem
{
    private readonly nint _capturedOpener;
    private readonly string _root; // normalized (forward slashes), no trailing slash

    public DuckDbTableFileSystem(nint opener, string root)
    {
        _capturedOpener = opener;
        _root = Normalize(root).TrimEnd('/');
    }

    /// <summary>
    /// <inheritdoc cref="ITableFileSystem.PathConstraints"/>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one implementation here whose answer genuinely varies, and it varies by ROOT rather than by
    /// process: DuckDB's <c>FileSystem</c> is a dispatcher, so this object fronts a local volume or an
    /// object store depending on the path it was constructed with. A <c>scheme://</c> root is a network
    /// store on any machine; a bare path is a local volume, whose rules are the running OS's.
    /// </para>
    /// <para>
    /// <b>The local-Windows case is the one with teeth.</b> Under the default
    /// <c>PartitionPathSpelling.SparkCompatible</c> only
    /// <see cref="PathNameConstraints.Win32ReservedCharacters"/> is consulted, and it is what makes a
    /// partition value containing <c>&lt; &gt; |</c> or a TRAILING SPACE writable at all: Win32 silently
    /// strips a trailing space from a component, so <c>region=a b </c> creates <c>region=a b</c> and then
    /// fails to open the file underneath it. Under-reporting is therefore a stray directory and a lost
    /// write, not an ugly name.
    /// </para>
    /// <para>
    /// For a scheme root we report the union of what the object stores constrain rather than
    /// <see cref="PathNameConstraints.None"/>, because we do NOT know which backend DuckDB will resolve
    /// the path to and over-reporting is the safe direction. It costs nothing today: none of these flags
    /// is read under <c>SparkCompatible</c>, so this differs from <c>None</c> only for a caller asking
    /// for a portable spelling — which is exactly the caller that should get the conservative answer.
    /// </para>
    /// </remarks>
    public PathNameConstraints PathConstraints =>
        _root.Contains("://")
            ? PathNameConstraints.NoControlCharacters | PathNameConstraints.NoTrailingDot
                | PathNameConstraints.NoDotOnlySegments
            : OperatingSystem.IsWindows()
                ? PathNameConstraints.Win32
                : PathNameConstraints.None;

    /// <summary>
    /// The host-FS opener to use for THIS call, preferring the one currently in scope over the one captured
    /// at construction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The opener is a <c>ClientContext*</c> that the host hands us per ABI call and that is only valid for
    /// the duration of that call. Capturing it — which is what this class did — is therefore the reason a
    /// <c>DeltaTable</c> cannot be held open ACROSS calls: the second call would drive IO through a dangling
    /// pointer. That is a use-after-free rather than a staleness bug, and neither Windows nor glibc would
    /// necessarily fault on it (the same asymmetry that let the late <c>ArrowProducer</c> release hide
    /// everywhere except macOS).
    /// </para>
    /// <para>
    /// Reading <see cref="AmbientOpener"/> first fixes that, because the host sets it on every crossing.
    /// The captured value is kept as a FALLBACK rather than deleted: the ambient is an
    /// <see cref="System.Threading.AsyncLocal{T}"/>, so it flows across <c>await</c> and pool-thread hops
    /// but would read 0 in any context where the execution context did not flow, and there the captured
    /// pointer is still the correct one because no object outlives its call today. So this is
    /// behaviour-preserving now (within one call the two values are identical — every construction site
    /// passes <c>Opener()</c>, which just returns the ambient) and becomes load-bearing the moment
    /// something is cached.
    /// </para>
    /// <para>
    /// ⚠⚠ <b>The ambient stays first, and a pull is now a crossing (2026-10-27).</b> A streaming read runs
    /// <c>get_next</c> on a WORKER thread, whose ambient held whatever the last crossing ON IT left there — after
    /// a nested host query, a destroyed <c>ClientContext</c> (measured: after <c>ducklake_delta_sync</c> every
    /// <c>fabricator_delta_scan</c> in the session failed in <c>fs_open_read</c>). The host now sets the scan's
    /// own context before every pull (<c>GetNextBatch</c> in arrow_ingest.cpp). ⚠ Preferring the CAPTURED
    /// opener instead was tried and is WRONG: a buffered Delta transaction keeps a filesystem built at statement
    /// time until COMMIT, and the commit flush deliberately makes its own short-lived connection the ambient —
    /// captured-first sent the flush through the user's context, whose transaction had ended
    /// (<i>"ActiveTransaction called without active transaction"</i> from the S3 secret lookup,
    /// verify_delta_catalog_s3).
    /// </para>
    /// </remarks>
    private nint Opener
    {
        get
        {
            var current = AmbientOpener.Current;
            return current != 0 ? current : _capturedOpener;
        }
    }

    private static string Normalize(string p) => p.Replace('\\', '/');

    private string Resolve(string path)
    {
        var p = Normalize(path);
        if (p.StartsWith(_root, StringComparison.Ordinal))
        {
            return p;
        }
        return _root + "/" + p.TrimStart('/');
    }

    private string ToRelative(string absolute)
    {
        var p = Normalize(absolute);
        var prefix = _root + "/";
        return p.StartsWith(prefix, StringComparison.Ordinal) ? p.Substring(prefix.Length) : p;
    }

    // Per-IO Debug lines, the log-side twin of AdlsGen2TableFileSystem's (Fabricator.Adls.Fs) and
    // OneLakeForwardFs's (Fabricator.OneLake.Fs). This filesystem was the ONE storage backend with no per-IO
    // instrument, which is why an `s3://` suite that went from ~30 to ~70 minutes could not be attributed:
    // the ADLS instrument is exactly what turned the 2026-08-16 OneLake mystery (a ~15 s table open) into a
    // checkpoint read of ~63 ranged micro-GETs, and nothing equivalent existed here.
    //
    // ⚠ IT IS NOT REDUNDANT WITH DuckDB'S OWN `FileSystem` LOG TYPE, which is richer at the byte level —
    // `SET enabled_log_types='FileSystem'` emits {"fs":"S3FileSystem","path":…,"op":"READ","bytes":…,"pos":…}
    // per operation and is the better micro-GET detector. What it CANNOT say is which DELTA operation caused
    // the IO: a log listing, a `_last_checkpoint` probe, a commit replay, a checkpoint read, a data scan or a
    // commit write all look alike to it. These lines carry that attribution, so the two are read TOGETHER —
    // DuckDB's for what the IO was, ours for why it happened.
    //
    // Debug-gated; never compute log arguments before the IsEnabled check on a per-IO path.
    internal static readonly Microsoft.Extensions.Logging.ILogger IoLog =
        FabricatorLog.CreateLogger("Fabricator.Host.Fs");

    public async IAsyncEnumerable<TableFileInfo> ListAsync(
        string prefix, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // DuckDB glob: <root>/<prefix>** — RECURSIVE, and it has to be.
        //
        // ⚠ This read `+ "*"` with the comment "the Delta log is flat", which is true of the log and false of
        // the OTHER caller: VacuumExecutor lists the whole TABLE ROOT to find files no version references. A
        // one-level glob made VACUUM collect only what sits at the root, so on a PARTITIONED table it
        // reclaimed nothing at all — measured: a backdated orphan in `p=a/` survived `VACUUM RETAIN 0 HOURS`
        // while an identical one at the root was collected. Storage below the root grew forever.
        //
        // engineered-wood's own LocalTableFileSystem enumerates with SearchOption.AllDirectories, so RECURSIVE
        // is the contract its consumers are written against and its suite runs them that way — this was our
        // implementation silently narrowing an interface, not upstream assuming too much.
        //
        // `**` also matches files at the level of the prefix itself (measured), so the root's own files still
        // come back; and for a prefix that is a FILENAME fragment rather than a directory, `pre**` behaves
        // exactly like `pre*` (also measured) — so this cannot change the shape no Delta caller uses.
        var pattern = _root + "/" + Normalize(prefix) + "**";
        var json = HostFs.Glob(Opener, pattern);
        var globbed = ParseGlob(json);
        // The COUNT is the point, not just the call: a Delta open's cost is dominated by how many objects the
        // log listing returns (CLAUDE.md measures ~10 ms per dead commit on S3), and a listing that returns
        // hundreds is the difference between a fast and a slow open. Logged AFTER the parse so the number is
        // real rather than a promise.
        if (IoLog.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(
                IoLog, "host list {Pattern} -> {Count} entries", pattern, globbed.Count);
        }
        foreach (var entry in globbed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The host glob no longer opens each match for its size (an open can DOWNLOAD the blob on a
            // fuse mount — the old open-per-commit-json made a lakehouse ATTACH take minutes); object-store
            // listings carry the size, local files get it here via a cheap metadata stat. Unknown stays 0 —
            // the only size consumer is vacuum's bytes-to-delete metric.
            long size = entry.Size;
            if (size < 0)
            {
                try
                {
                    var fi = new System.IO.FileInfo(entry.Path);
                    size = fi.Exists ? fi.Length : 0;
                }
                catch
                {
                    size = 0;
                }
            }
            // ⚠ THIS WAS A HARDCODED DateTimeOffset.UnixEpoch until 2026-08-08, i.e. every file claimed to
            // be from 1970. Harmless while nothing read it and a live hazard the moment something did:
            // Delta log cleanup deletes by AGE, so a fake 56-year-old timestamp makes every commit
            // eligible however recently it was written. UNKNOWN must therefore stay UNKNOWN — a caller
            // that cannot establish a file's age has to decline rather than assume one.
            var modified = entry.ModifiedMs >= 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(entry.ModifiedMs)
                : UnknownModified;
            yield return new TableFileInfo(ToRelative(entry.Path), size, modified);
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public ValueTask<IRandomAccessFile> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IoLog.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(IoLog, "host open {Path}", path);
        }

        // ⚠ BUFFER SMALL FILES ON REMOTE ROOTS ONLY — see DuckDbRandomAccessFile. The ADLS twin needed no
        // such test because it is remote by construction; this filesystem also serves LOCAL roots (the whole
        // hermetic tier), where a per-call round trip costs nothing and downloading a whole file to answer a
        // small ranged read would be pure waste. Same predicate the PathConstraints property uses.
        return new ValueTask<IRandomAccessFile>(
            new DuckDbRandomAccessFile(Opener, Resolve(path), bufferSmallFiles: _root.Contains("://")));
    }

    public ValueTask<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IoLog.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(IoLog, "host exists {Path}", path);
        }
        return new ValueTask<bool>(ResolvedExists(Resolve(path)));
    }

    /// <summary>
    /// Existence of an ALREADY-RESOLVED path. Shared by <see cref="ExistsAsync"/> and the commit primitive,
    /// which holds a resolved path already and must not resolve it twice.
    ///
    /// <para>NOT a glob probe: DuckDB's S3 (httpfs) glob ECHOES a wildcard-free path back without checking
    /// the object store, so a literal glob reported every path as existing — which made engineered-wood's
    /// commit-0 existence pre-check throw a phantom DeltaConflictException on S3/MinIO. Probing by opening
    /// for read (a HEAD on object stores) is existence-accurate on every backend.</para>
    /// </summary>
    private bool ResolvedExists(string resolved)
    {
        try
        {
            nint file = HostFs.OpenRead(Opener, resolved);
            HostFs.Close(file);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>How often a zero-length commit file is re-read before it is reported (about 2 s in all).</summary>
    private const int EmptyCommitRetries = 120;

    private static bool IsCommitFile(string path)
    {
        // <20 digits>.json under _delta_log — a version's commit, never a checksum or checkpoint
        int slash = path.LastIndexOf('/');
        var name = path.AsSpan(slash + 1);
        if (name.Length != 25 || !name.EndsWith(".json", StringComparison.Ordinal))
        {
            return false;
        }
        foreach (char c in name[..20])
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }
        return path.Contains("_delta_log", StringComparison.Ordinal);
    }

    public ValueTask<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        if (IoLog.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(IoLog, "host read-all {Path}", path);
        }

        // Bounded reopen-retry for MUTABLE small files (this method's callers: _last_checkpoint +
        // commit JSONs; only _last_checkpoint is overwritten in place). On an object store, DuckDB's
        // httpfs validates the etag recorded at open against the range read — a CONCURRENT writer's
        // checkpoint changes it mid-read and the read throws ("ETag on reading file ... has changed").
        // Reopening reads a consistent NEWER copy, which is always valid for a checkpoint pointer.
        // Data files are immutable (a new commit writes a new file), so they never hit this.
        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                nint file = HostFs.OpenRead(Opener, Resolve(path));
                try
                {
                    long size = HostFs.Size(file);
                    if (size == 0 && IsCommitFile(path) && attempt < EmptyCommitRetries)
                    {
                        // A commit is never empty: a zero-length one is being written right now (a POSIX publish by
                        // O_EXCL creates the name before the content). Read as it is, it has NO actions, so a
                        // conflict check sees nothing and a rebased commit replaces the winner's deletion vector —
                        // MEASURED 2026-11-02, Linux, 8 threads deleting their own rows of one file: 7800 of 8000
                        // rows came back, every statement succeeding. Wait for the content instead.
                        if (attempt == 1 && IoLog.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                        {
                            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(IoLog, "empty commit {Path}: waiting for its content", path);
                        }
                        Thread.Sleep(Math.Min(attempt, 20));
                        continue;
                    }
                    if (size == 0 && IsCommitFile(path))
                    {
                        throw new System.IO.IOException(
                            $"DuckDbTableFileSystem: commit file {path} is still empty after {EmptyCommitRetries} reads; "
                            + "a writer may have stopped between creating and writing it.");
                    }
                    if (attempt > 1 && IoLog.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                    {
                        Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(IoLog, "empty commit {Path}: content after {Reads} reads", path, attempt);
                    }
                    var buffer = new byte[size];
                    if (size > 0)
                    {
                        fixed (byte* bp = buffer)
                        {
                            HostFs.Read(file, bp, size, 0);
                        }
                    }
                    return new ValueTask<byte[]>(buffer);
                }
                finally
                {
                    HostFs.Close(file);
                }
            }
            catch (Exception ex) when (attempt < 4
                                       && ex.Message.Contains("ETag on reading file",
                                                              StringComparison.OrdinalIgnoreCase))
            {
                // concurrent in-place overwrite — reopen for a consistent newer copy
            }
        }
    }

    // ---- write surface: over the host fs_* write callbacks (Delta write-back) ----

    /// <summary>The parent directory of a resolved path (for materializing e.g. <c>_delta_log/</c> on a local FS;
    /// a no-op marker on object stores). Empty if there is no parent.</summary>
    private string ParentDir(string resolved)
    {
        int slash = resolved.LastIndexOf('/');
        return slash <= 0 ? string.Empty : resolved.Substring(0, slash);
    }

    private void EnsureParentDir(string resolved)
    {
        // On object stores directories are implicit (the write creates the path); on a local FS they are not.
        // fs_create_dir is recursive (mkdir -p) on the host side, so one call on the immediate parent
        // materializes the whole chain (the table root + `_delta_log/`). Idempotent.
        var parent = ParentDir(resolved);
        if (parent.Length > 0)
        {
            HostFs.CreateDir(Opener, parent);
        }
    }

    public ValueTask<ISequentialFile> CreateAsync(
        string path, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = Resolve(path);
        EnsureParentDir(resolved);
        // A file is published only by CompleteAsync, and disposing it uncompleted abandons it (engineered-wood
        // #501). On a local WINDOWS root that is engineered-wood's own LocalSequentialFile: written under a temporary
        // name and moved onto the real one at completion, no-replace when overwrite is false — the same primitive our
        // commit publish uses (TryPublishLocalWindows). The early existence check is the contract's "fails early";
        // the move is the atomic one. NOT on POSIX: a no-replace move is a link() there, which a FUSE mount (Fabric's
        // /lakehouse/default) does not have — O_EXCL at open is the measured-safe primitive on that mount.
        if (OperatingSystem.IsWindows() && !_root.Contains("://"))
        {
            if (!overwrite && System.IO.File.Exists(resolved))
            {
                throw new System.IO.IOException($"DuckDbTableFileSystem: file already exists: {path}");
            }
            return new ValueTask<ISequentialFile>(new EngineeredWood.IO.Local.LocalSequentialFile(resolved, overwrite));
        }
        nint file;
        if (overwrite)
        {
            file = HostFs.OpenWrite(Opener, resolved);
        }
        else if (!HostFs.TryOpenWriteExclusive(Opener, resolved, out file))
        {
            // Contract: CreateAsync fails when the file exists and overwrite is false.
            throw new System.IO.IOException($"DuckDbTableFileSystem: file already exists: {path}");
        }
        return new ValueTask<ISequentialFile>(new DuckDbSequentialFile(file, Opener, resolved));
    }

    /// <summary>
    /// The put-if-absent commit primitive, via DuckDB's <c>EXCLUSIVE_CREATE</c>. False = the target exists,
    /// which is the commit-conflict signal engineered-wood maps to <c>DeltaConflictException</c>.
    ///
    /// <para>Replaced <c>RenameAsync</c> at engineered-wood 0.3.0, which removed it from
    /// <c>ITableFileSystem</c> in favour of this. Strictly simpler here: the old shape wrote a temp file,
    /// read it back, exclusively created the target, copied the bytes in and deleted the temp — this writes
    /// the bytes it was handed. Behaviour is otherwise UNCHANGED, deliberately: a bump is the wrong place to
    /// change behaviour.</para>
    ///
    /// <para>⚠ <b>THIS IMPLEMENTATION DOES NOT MEET THE CONTRACT ON EVERY BACKEND, AND THAT IS A KNOWN,
    /// MEASURED GAP RATHER THAN AN OVERSIGHT.</b> Upstream now states the requirement in
    /// <c>ITableFileSystem.TryWriteAllBytesAsync</c>: the create must be atomic, because "an implementation
    /// that writes unconditionally, or that checks existence and then writes, lets two concurrent writers
    /// both believe they won; the loser's commit silently overwrites the winner's". <c>EXCLUSIVE_CREATE</c>
    /// honours that on POSIX and on OneLake/ADLS, and MEASURABLY DOES NOT on a local <b>Windows</b> root
    /// (<c>fabricator_fs_write_probe</c> reports it succeeding on an existing file) nor on <c>s3://</c>
    /// through httpfs. Consequences measured and recorded in docs/delta-transactions.md §8.5 and §8.3:
    /// 6 writers × 3 INSERTs on a Windows root landed 400 of 900 rows with every writer exiting 0; a
    /// secretless s3 attach landed 8 of 48 commits.</para>
    ///
    /// <para>Those two roots are why <see cref="S3CommitFileSystem"/> and
    /// <see cref="AdlsGen2TableFileSystem"/> exist — a SECRET-named s3 or abfss attach never reaches this
    /// method for a commit. What remains exposed is a local Windows root and a secretless s3 attach (the
    /// latter already warns at ATTACH). Making this method REFUSE rather than silently approximate is the
    /// right end state and is deliberately not done here: it is a behaviour change, it needs a per-backend
    /// capability probe rather than a platform guess, and it belongs in its own slice with its own gate.</para>
    /// </summary>
    public ValueTask<bool> TryWriteAllBytesAsync(
        string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dst = Resolve(path);
        EnsureParentDir(dst);
        // ⚠ THE EXISTENCE PROBE IS LOAD-BEARING AND IT IS NOT ATOMIC — see the remarks above.
        //
        // It is here because engineered-wood 0.3.0 DELETED the equivalent probe from its own
        // TransactionLog.WriteCommitAsync (which read `if (await _fs.ExistsAsync(targetPath)) throw` before
        // renaming) on the correct grounds that a check-then-write is not a commit guarantee. That left the
        // whole guarantee resting on TryOpenWriteExclusive, which a local WINDOWS root does not honour — so
        // the bump turned a latent unsafety into an immediate wrong ANSWER: verify_delta_row_level_concurrency
        // §1 had a buffered DELETE commit v2 ON TOP of a concurrent autocommit DELETE's v2, silently
        // resurrecting the row the other statement removed.
        //
        // So this restores the PRE-BUMP semantics exactly, at the layer that can still see the path: correct
        // whenever the two writers are ordered (which is every sqllogictest, every single-process host, and
        // the overwhelming majority of real use), racy when they are not. It is an APPROXIMATION and is
        // labelled as one; it must not be read as satisfying the contract.
        //
        // Cost is one probe per commit. It is not paid where it would hurt: a SECRET-named s3 or abfss attach
        // commits through S3CommitFileSystem / AdlsGen2TableFileSystem, which have a real conditional write
        // and never reach this method. What pays is a local root (a stat) and a secretless remote attach —
        // which already warns at ATTACH that it is not multi-writer safe.
        //
        // ⚠ The probe is an OPEN-FOR-READ, matching ExistsAsync above, NOT a glob: DuckDB's S3 glob echoes a
        // wildcard-free path back without consulting the store, which would report every commit path as
        // already existing and turn every commit into a phantom conflict.
        if (ResolvedExists(dst))
        {
            return new ValueTask<bool>(false); // target exists => commit conflict
        }
        if (OperatingSystem.IsWindows() && !_root.Contains("://"))
        {
            bool won = TryPublishLocalWindows(dst, data.Span);
            if (IoLog.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
            {
                Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(IoLog, "local publish {Path} -> {Outcome}", path,
                    won ? "won" : "LOST (exists)");
            }
            return new ValueTask<bool>(won);
        }
        if (!_root.Contains("://") && !s_noLinkRoots.ContainsKey(_root) && TryPublishLocalPosix(dst, data.Span) is bool linked)
        {
            if (IoLog.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
            {
                Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(IoLog, "local link publish {Path} -> {Outcome}", path,
                    linked ? "won" : "LOST (exists)");
            }
            return new ValueTask<bool>(linked);
        }
        bool created = HostFs.TryOpenWriteExclusive(Opener, dst, out nint target);
        if (IoLog.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
        {
            // The same line the Windows publish writes, so a race is countable on every platform.
            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(IoLog, "exclusive publish {Path} -> {Outcome}", path,
                created ? "won" : "LOST (exists)");
        }
        if (!created)
        {
            return new ValueTask<bool>(false); // target exists => commit conflict
        }
        try
        {
            if (!data.IsEmpty)
            {
                fixed (byte* bp = data.Span)
                {
                    HostFs.WriteBytes(target, bp, data.Length);
                }
            }
        }
        finally
        {
            HostFs.CloseWrite(target);
        }
        return new ValueTask<bool>(true);
    }

    /// <summary>
    /// The commit on a local WINDOWS root: the whole file is written under a temporary name and then moved onto the
    /// commit name WITHOUT replacing (MoveFileEx without MOVEFILE_REPLACE_EXISTING — atomic, and it fails when the
    /// target exists). False = the target exists, the commit-conflict signal.
    /// </summary>
    /// <remarks>
    /// Create-then-write publishes the name BEFORE the content, and a concurrent writer re-reading the log sees an
    /// empty or half-written commit — or, on Windows, cannot open it at all ("being used by another process", what
    /// failed verify_ducklake_delta_sync in CI). MEASURED 2026-10-31 on DuckDB 2.0, whose EXCLUSIVE_CREATE does hold
    /// now: 8 processes × (an INSERT + 5 UPDATEs of their own rows) on one local table lost whole INSERTs and UPDATEs
    /// with no error (300 of 400 rows, v 1200 of 2000), because a snapshot missing a commit is what the next
    /// checkpoint records. ⚠ Windows only: on POSIX .NET's no-replace move is a link(), which a FUSE mount (Fabric's
    /// /lakehouse/default) does not support — it falls back to check-then-rename there, a race; the O_EXCL create
    /// below is the measured-safe primitive on that mount.
    /// </remarks>
    private static bool TryPublishLocalWindows(string path, ReadOnlySpan<byte> data)
    {
        string temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!,
            "." + System.IO.Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new System.IO.FileStream(temp, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write,
                       System.IO.FileShare.None))
            {
                file.Write(data);
                file.Flush(flushToDisk: true);
            }
            try
            {
                System.IO.File.Move(temp, path, overwrite: false);
                return true;
            }
            catch (System.IO.IOException) when (System.IO.File.Exists(path))
            {
                return false; // another writer published this version first
            }
        }
        finally
        {
            if (System.IO.File.Exists(temp))
            {
                System.IO.File.Delete(temp);
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int PosixLink(string oldPath, string newPath);

    private const int EEXIST = 17; // the same on Linux and macOS

    /// <summary>
    /// Table roots where link() failed for another reason than EEXIST: that filesystem has none. PROCESS-wide, because
    /// an instance of this class lives for one table open — about one statement — and a per-instance flag would make
    /// every commit on such a mount pay the temporary file and the failed link() again.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> s_noLinkRoots =
        new(StringComparer.Ordinal);

    /// <summary>
    /// The commit on a local POSIX root: the whole file is written under a temporary name and then hard-linked onto
    /// the commit name. link() fails with EEXIST when the name exists, so it is put-if-absent, and the name appears
    /// WITH its content — no reader can meet the commit half-written. True = published, false = the target exists,
    /// null = this filesystem has no link() (Fabric's FUSE mount /lakehouse/default), where the caller falls back to
    /// O_EXCL at open, the primitive measured safe on that mount (the reader then waits out an empty commit).
    /// </summary>
    private bool? TryPublishLocalPosix(string path, ReadOnlySpan<byte> data)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }
        string temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!,
            "." + System.IO.Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            try
            {
                using var file = new System.IO.FileStream(temp, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write,
                    System.IO.FileShare.None);
                file.Write(data);
                file.Flush(flushToDisk: true);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                // Not a path System.IO can write: leave this root to the host filesystem for good. REMEMBERED like a
                // missing link(): on Fabric's FUSE mount one writer in 16 kept failing here, and paying the slow
                // temporary write again on every attempt made it lose every race (16 attempts, 2 of 2 runs).
                return NoLink(ex.GetType().Name + ": " + ex.Message);
            }
            if (PosixLink(temp, path) == 0)
            {
                return true;
            }
            int errno = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            return errno == EEXIST ? false : NoLink("errno " + errno);
        }
        finally
        {
            try
            {
                System.IO.File.Delete(temp);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                // an orphaned temp file is litter, not a fault — and must never fail a commit that was published
            }
        }
    }

    /// <summary>Remembers that this root has no usable link() publish; the commit falls back to O_EXCL.</summary>
    private bool? NoLink(string reason)
    {
        if (s_noLinkRoots.TryAdd(_root, true) && IoLog.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(IoLog,
                "link() publish unusable under {Root} ({Reason}); commits there use O_EXCL", _root, reason);
        }
        return null;
    }

    public ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        if (IoLog.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(IoLog, "host delete {Path}", path);
        }

        cancellationToken.ThrowIfCancellationRequested();
        HostFs.Remove(Opener, Resolve(path));
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAllBytesAsync(
        string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = Resolve(path);
        EnsureParentDir(resolved);
        nint file = HostFs.OpenWrite(Opener, resolved);
        try
        {
            if (data.Length > 0)
            {
                using var pin = data.Pin();
                HostFs.WriteBytes(file, pin.Pointer, data.Length);
            }
        }
        finally
        {
            HostFs.CloseWrite(file);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The sentinel a listing reports when the host could not supply a modification time. Callers that act
    /// on file AGE must treat it as "unknown" and decline — see <c>LogCleanup</c>, which refuses to delete
    /// anything it cannot date.
    /// </summary>
    internal static readonly DateTimeOffset UnknownModified = DateTimeOffset.MinValue;

    private readonly record struct GlobEntry(string Path, long Size, long ModifiedMs);

    private static List<GlobEntry> ParseGlob(string json)
    {
        var result = new List<GlobEntry>();
        using var doc = JsonDocument.Parse(json);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var path = el.GetProperty("path").GetString() ?? string.Empty;
            long size = el.TryGetProperty("size", out var s) ? s.GetInt64() : -1;
            long modified = el.TryGetProperty("modified_ms", out var m) ? m.GetInt64() : -1;
            result.Add(new GlobEntry(path, size, modified));
        }
        return result;
    }
}

/// <summary>
/// Offset-addressed read handle over a single host <c>FileSystem</c> file. Opened eagerly; all reads are
/// synchronous host calls wrapped in completed <see cref="ValueTask"/>s (no real async — the hostfxr CLR has
/// no SynchronizationContext, so sync-over-async upstream cannot deadlock).
/// </summary>
internal sealed unsafe class DuckDbRandomAccessFile : IRandomAccessFile
{
    // Same per-IO Debug category as the owning filesystem (Fabricator.Host.Fs). ⚠ THESE RANGED READS ARE THE
    // ONES THAT MATTER: on the ADLS twin the equivalent method was the LAST uninstrumented one, and it is
    // where the 2026-08-16 OneLake finding actually lived — a checkpoint parquet consumed as ~63 sequential
    // ranged GETs of 8-7096 bytes at ~180 ms each, which looked like "13 s of silence" until this line
    // existed. A burst of small reads at climbing offsets on ONE path is that signature.
    private static readonly Microsoft.Extensions.Logging.ILogger IoLog = DuckDbTableFileSystem.IoLog;

    /// <summary>Files at or below this are downloaded WHOLE on the first ranged read and served from memory,
    /// on REMOTE roots only.
    /// <para>⚠ MEASURED here, not inherited: the per-IO instrument showed a scan of a 4-row table with a
    /// 218-commit log doing 57 ranged reads of 8-55 bytes each — 17,236 bytes in 57 requests — all on ONE
    /// ~17 KB checkpoint parquet. That is engineered-wood's parquet reader taking the footer length, the
    /// footer, then every column chunk as its own round trip. It is the SAME defect, and the same fix, that
    /// AdlsGen2TableFileSystem got on 2026-08-16 for OneLake (where it was ~63 GETs at ~180 ms ≈ 12 s, the
    /// whole "log replay" span of a table open) — the fix simply never reached this filesystem, so every
    /// `s3://` root, and every `abfss://` root WITHOUT a named credential, still paid it.</para>
    /// <para>⚠ Above the cap, TRUE ranged reads are kept deliberately: a column-pruned read of a big DATA
    /// file must not download all of it. Below it the round trip dominates the bytes. Note the codec engine
    /// DOES read data files through this handle, so the cap is a real trade and not only a checkpoint
    /// optimisation.</para></summary>
    private const long BufferedReadMax = 16 * 1024 * 1024;

    private readonly nint _file;
    private readonly string _path;
    private readonly bool _bufferSmallFiles;
    private byte[]? _buffer; // the whole file, downloaded lazily on the first read when eligible
    private long _length = -1;
    private bool _closed;

    public DuckDbRandomAccessFile(nint opener, string resolvedPath, bool bufferSmallFiles = false)
    {
        _file = HostFs.OpenRead(opener, resolvedPath);
        _path = resolvedPath;
        _bufferSmallFiles = bufferSmallFiles;
    }

    public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken = default)
    {
        if (_length < 0)
        {
            _length = HostFs.Size(_file);
        }
        return new ValueTask<long>(_length);
    }

    public ValueTask<IMemoryOwner<byte>> ReadAsync(FileRange range, CancellationToken cancellationToken = default)
    {
        if (_bufferSmallFiles && _buffer is null && Eligible())
        {
            // A benign race in principle (two first reads would download twice, last assignment wins, both
            // correct) — and unreachable in practice: a handle is used by one reader. The objects read through
            // this handle are Delta log/checkpoint/data files, which are never overwritten in place;
            // `_last_checkpoint`, the one exception, is read via ReadAllBytesAsync rather than a handle.
            if (IoLog.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
            {
                Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(
                    IoLog, "host read {Path} (whole file, {Len} bytes, buffered)", _path, _length);
            }
            var whole = new byte[_length];
            if (_length > 0)
            {
                fixed (byte* wp = whole)
                {
                    HostFs.Read(_file, wp, _length, 0);
                }
            }
            _buffer = whole;
        }
        if (_buffer is not null)
        {
            // Served from memory: no host call, no log line — the buffered download above is the one IO, and
            // logging these too would make an IO timeline show traffic that never happened.
            int avail = (int)Math.Min(range.Length, Math.Max(0, _buffer.Length - range.Offset));
            var hit = new ExactMemoryOwner(avail);
            if (avail > 0)
            {
                Array.Copy(_buffer, range.Offset, hit.Array, 0, avail);
            }
            return new ValueTask<IMemoryOwner<byte>>(hit);
        }
        if (IoLog.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(
                IoLog, "host read {Path} @{Offset}+{Length}", _path, range.Offset, range.Length);
        }
        var owner = new ExactMemoryOwner((int)range.Length);
        if (range.Length > 0)
        {
            fixed (byte* bp = owner.Array)
            {
                HostFs.Read(_file, bp, range.Length, range.Offset);
            }
        }
        return new ValueTask<IMemoryOwner<byte>>(owner);
    }

    /// <summary>Whether this file is small enough to buffer. Sizing needs no IO — HostFs.Size is
    /// GetFileSize on the ALREADY-OPEN handle, which httpfs answers from the open, so the check is free.</summary>
    private bool Eligible()
    {
        if (_length < 0)
        {
            _length = HostFs.Size(_file);
        }
        return _length >= 0 && _length <= BufferedReadMax;
    }

    public ValueTask<IReadOnlyList<IMemoryOwner<byte>>> ReadRangesAsync(
        IReadOnlyList<FileRange> ranges, CancellationToken cancellationToken = default)
    {
        // Logged as ONE line for the batch as well as per range below: a reader that asks for many ranges in
        // one call is doing something structurally different from one that loops, and the distinction is
        // invisible if only the individual reads are visible.
        if (IoLog.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(
                IoLog, "host read-ranges {Path} x{Count}", _path, ranges.Count);
        }
        var result = new IMemoryOwner<byte>[ranges.Count];
        for (int i = 0; i < ranges.Count; i++)
        {
            // Reads complete synchronously (host calls block) — no real await needed.
            result[i] = ReadAsync(ranges[i], cancellationToken).GetAwaiter().GetResult();
        }
        return new ValueTask<IReadOnlyList<IMemoryOwner<byte>>>(result);
    }

    public void Dispose()
    {
        if (!_closed)
        {
            _closed = true;
            HostFs.Close(_file);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Sequential (append-only) write handle over a host <c>FileSystem</c> file (opened via fs_open_write). Writes
/// are synchronous host calls in completed <see cref="ValueTask"/>s. Azure DFS only accepts sequential writes,
/// which is exactly this contract.
/// </summary>
/// <remarks>
/// <see cref="CompleteAsync"/> publishes by closing the handle, which is where an object store makes the object
/// visible. Disposing without it ABANDONS the write (engineered-wood #501): the handle is closed and the file
/// removed, so a write that failed part-way leaves nothing at the name. ⚠ The name is written directly, not a
/// temporary one: on a local POSIX root a concurrent reader can see the file while it is being written (a reader
/// falls back from an unreadable newest checkpoint, #500). Windows local roots take LocalSequentialFile instead.
/// </remarks>
internal sealed unsafe class DuckDbSequentialFile : ISequentialFile
{
    private readonly nint _file;
    private readonly nint _opener;
    private readonly string _path;
    private long _position;
    private bool _closed;
    private bool _completed;

    public DuckDbSequentialFile(nint file, nint opener, string path)
    {
        _file = file;
        _opener = opener;
        _path = path;
    }

    public long Position => _position;

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (_closed)
        {
            throw new InvalidOperationException("DuckDbSequentialFile: the file has already been completed");
        }
        if (data.Length > 0)
        {
            using var pin = data.Pin();
            HostFs.WriteBytes(_file, pin.Pointer, data.Length);
            _position += data.Length;
        }
        return ValueTask.CompletedTask;
    }

    // The host flushes on close (fs_close_write); there is no separate flush callback. Sequential writes are
    // already handed straight to the FileHandle, so this is a no-op until Dispose.
    public ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_completed)
        {
            return ValueTask.CompletedTask;
        }
        if (_closed)
        {
            throw new InvalidOperationException("DuckDbSequentialFile: the write was abandoned");
        }
        _closed = true;
        HostFs.CloseWrite(_file);
        _completed = true;
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }
        _closed = true;
        HostFs.CloseWrite(_file);
        // Abandoned: never completed, so whatever the close published must not stay at the name. Best effort — a
        // failed removal leaves an unreferenced file for VACUUM, which is what an aborted write left before.
        try
        {
            HostFs.Remove(_opener, _path);
        }
        catch (Exception)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>An <see cref="IMemoryOwner{T}"/> backed by an exact-sized array (so <c>Memory.Length</c> equals the
/// requested range length — the parquet reader relies on it).</summary>
internal sealed class ExactMemoryOwner : IMemoryOwner<byte>
{
    public byte[] Array { get; }
    public ExactMemoryOwner(int size) => Array = size == 0 ? System.Array.Empty<byte>() : new byte[size];
    public Memory<byte> Memory => Array;
    public void Dispose() { }
}
