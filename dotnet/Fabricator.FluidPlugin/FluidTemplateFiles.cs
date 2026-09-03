// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Types;
using Fabricator.Bridge;
using Fluid;

namespace Fabricator.FluidPlugin;

/// <summary>
/// Resolves <c>{% include %}</c> / <c>{% render %}</c> against any storage the host can reach — <c>s3://</c>,
/// <c>abfss://</c>, <c>onelake://</c>, a local directory — so a template library need not live inside a SQL
/// string literal. Slice 4 of docs/fluid-templating.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>⚠⚠ IT READS THROUGH <see cref="IHostQuery"/> AND <c>read_blob</c>, NOT THROUGH A FILESYSTEM
/// SEAM — and that is a MEASURED correction of the plan, not a shortcut.</b> §2 of the plan predicted this
/// slice would need a host filesystem service (there is one now — <see cref="IHostFileSystem"/>); one was built, and it
/// CANNOT WORK from here. Every host filesystem callback takes the calling operator's <c>ClientContext</c>
/// as its opener, and a GLOBAL function — which both <c>fluid_render</c> and <c>fluid_query</c> are —
/// has no ambient opener established: measured, the read reached <c>fs_open_read</c> with a null handle and
/// the process died with an access violation inside <c>HostFs.OpenRead</c>. The blocker was never that the
/// TYPE lived in the bridge; it is that the AMBIENT the seam needs is not established for global functions.
/// </para>
/// <para>
/// <b>And the query route is better on its own merits, which is what settles it rather than mere
/// availability.</b> MEASURED, all four: <c>read_blob</c> on a missing file returns ZERO ROWS rather than
/// throwing, so absence is ESTABLISHED by the engine instead of guessed from a message — the host has no
/// <c>fs_exists</c> and a failed open there is equally a missing file, a denied credential or an unreachable
/// endpoint; it reports <c>size</c>, so the ceiling below is checked against the file rather than hoped for;
/// it reports <c>last_modified</c>, so <see cref="TemplateSourceInfo.LastModified"/> carries a REAL time
/// instead of the invented one a filesystem seam would have forced (this repo has shipped that mistake once
/// already — <c>DuckDbTableFileSystem</c> reported a hardcoded epoch as every file's mtime); and the path
/// crosses as a BOUND PARAMETER (<c>read_blob($path)</c> binds), so it never becomes SQL text.
/// </para>
/// <para>
/// <b>⚠ The cost, stated rather than hidden: the read inherits every limitation of <c>query()</c></b>
/// (docs/fluid-templating.md §8.2, §9). It runs on a connection of its own, so a template stored in a
/// location whose credential is a TEMPORARY secret of the calling session is not readable — a persistent
/// secret is. One rule for both, which is better than two.
/// </para>
/// <para>
/// <b>⚠⚠ FLUID PROBES TWICE PER INCLUDE.</b> MEASURED on 3.0.0-beta.7: <c>{% include 'a' %}</c> asks for
/// <c>a</c> and then for <c>a.liquid</c>, so an author who omits the extension pays TWO reads where
/// <c>{% include 'a.liquid' %}</c> pays one — on remote storage, two round trips. The per-render cache below
/// removes the repeat within one render but cannot remove the first miss.
/// </para>
/// <para>
/// <b>⚠ ONE instance lives on the shared static <see cref="FluidValueModel.Options"/>, and unlike the
/// <c>query</c> FILTER that is SAFE</b> — Fluid hands the provider the <see cref="TemplateContext"/>
/// (measured: a value put in <c>ctx.AmbientValues</c> before <c>Render</c> is visible here), so everything
/// per-call travels on the context and nothing is stored on this object.
/// </para>
/// <para>
/// <b>⚠ The provider is called at RENDER, never at PARSE</b> (measured: zero calls during
/// <c>TryParse</c>), so <see cref="FluidEngine"/>'s parse-once cache is unaffected.
/// </para>
/// </remarks>
internal sealed class HostTemplateFileProvider : ITemplateFileProvider
{
    /// <summary>The DuckDB setting naming the directory or URI prefix a RELATIVE include resolves against.</summary>
    /// <remarks>
    /// <para>
    /// A plain session <c>SET</c> works, and so does <c>SET GLOBAL</c>. ⚠ That is true only since <b>ABI
    /// v82</b>: a global scalar's crossing had NO settings session at all, so a session-scoped write landed
    /// in a layer this plugin could not read, and the shipped answer was "use <c>SET GLOBAL</c>". v82 hands
    /// <c>scalarfn_bind</c>/<c>scalarfn_execute</c> the caller's context and restores it afterwards
    /// (<c>CallScope</c>), which closed the gap for every plugin, not just this one.
    /// </para>
    /// <para>
    /// ⚠ It is a SETTING rather than a per-call argument because a template library has one root per
    /// project, and repeating it on every call would be noise. Session-scoped like every provider setting,
    /// so a dbt pre-hook can set it for one model without leaking to a model building concurrently on
    /// another connection. An ABSOLUTE include path needs no root at all.
    /// </para>
    /// </remarks>
    internal const string RootSetting = "fluid_template_root";

    /// <summary>The provider bucket <see cref="RootSetting"/> is filed under — this plugin's backend name.</summary>
    internal const string Provider = "fluid";

    /// <summary>The root in force right now, or null when none is set — for error messages only.</summary>
    internal static string? CurrentRoot
    {
        get
        {
            var root = ProviderSettingsStore.Instance.GetString(Provider, RootSetting);
            return string.IsNullOrWhiteSpace(root) ? null : root;
        }
    }

    /// <summary>
    /// A ceiling on one template, checked against the size <c>read_blob</c> reports. A Liquid template above
    /// a megabyte is pathological; the ceiling exists so that a root pointed at a directory of parquet turns
    /// a typo into a message rather than into a template nobody can parse.
    /// </summary>
    internal const long MaxTemplateBytes = 1L << 20;

    // ⚠ $path, not ?: the params batch's column is NAMED, and host_query binds by name when the batch's
    // names are all parameters the statement declares (docs/fluid-templating.md §9.10). A `?` here would
    // fail with "Values were not provided for ... parameters: 1" — which is exactly how the named-binding
    // change announced itself when it first landed.
    private const string ReadSql = "SELECT content, size, last_modified FROM read_blob($path)";

    private const string CacheKey = "fabricator.templatecache";
    private const string TriedKey = "fabricator.templatetried";

    public ValueTask<TemplateSourceInfo> GetFileInfoAsync(string subpath, TemplateContext context,
                                                          CancellationToken cancellationToken)
    {
        var resolved = Resolve(subpath);
        var cache = Cache(context);
        if (!cache.TryGetValue(resolved, out Loaded? loaded))
        {
            loaded = Load(resolved);
            cache[resolved] = loaded;
        }

        if (loaded is null)
        {
            // ⚠ Recorded HERE, on the miss only, and it is what makes the not-found message actionable:
            // Fluid's own exception carries the include's ARGUMENT, never the path that was actually asked
            // for, so without this an author whose ROOT is wrong is told only that `nope` is missing. Misses
            // only, so the message lists the probes that failed rather than every file the render read.
            // ⚠ Keyed on the include's ARGUMENT, not a flat list: Fluid probes `a` and then `a.liquid`, so
            // a SUCCESSFUL include contributes a miss of its own bare form. A flat list would put that in a
            // later include's failure message, naming a file that was found.
            var tried = Tried(context);
            if (!tried.TryGetValue(subpath, out var probes))
            {
                probes = new List<string>();
                tried[subpath] = probes;
            }
            if (!probes.Contains(resolved))
            {
                probes.Add(resolved);
            }
            // ⚠ NULL is Fluid's own not-found signal — MEASURED: returning it makes {% include %} fall
            // through to the `.liquid` probe and then raise FileNotFoundException. `null!` because
            // ValueTask<T> declares T non-nullable and TemplateSourceInfo is a CLASS, so the annotation says
            // this is impossible while the engine requires it.
            return new ValueTask<TemplateSourceInfo>((TemplateSourceInfo)null!);
        }

        var bytes = loaded.Bytes;
        return new ValueTask<TemplateSourceInfo>(new TemplateSourceInfo(
            loaded.LastModified,
            _ => new ValueTask<Stream>(new MemoryStream(bytes, writable: false)),
            resolved));
    }

    /// <summary>The file's bytes plus the modification time storage reported for it.</summary>
    /// <remarks>
    /// ⚠ BYTES, not text, and not merely to save a copy: <see cref="TemplateSourceInfo"/> takes a STREAM
    /// factory and Fluid reads it with a <c>StreamReader</c>, which detects and strips a UTF-8 byte-order
    /// mark itself. Decoding here would have meant handing Fluid a string it then re-encodes — and it is how
    /// a BOM-stripping branch came to be written here and MEASURED inert: a mutant that removed it changed
    /// nothing, because the BOM never survives to us. (Fluid does NOT strip one from a template passed as a
    /// STRING — measured — so <c>fluid_render</c> on a BOM-prefixed literal keeps it. That is the
    /// caller's own text, and not ours to edit.)
    /// </remarks>
    private sealed record Loaded(byte[] Bytes, DateTimeOffset LastModified);

    /// <summary>Reads one template, or null when storage says there is no such file.</summary>
    private static Loaded? Load(string resolved)
    {
        var host = FabricatorServices.Get<IHostQuery>()
            ?? throw new InvalidOperationException(
                "a template include needs the IHostQuery service, which is not published here.");

        // ⚠ The batch is NOT disposed here, matching FluidHostQuery: Host.Query exports it into an Arrow
        // stream that the HOST consumes and releases, so disposing it on this side would be a second
        // free of buffers the exporter already owns.
        var parameters = PathBatch(resolved);
        using var stream = host.Query(ReadSql, parameters);

        Loaded? result = null;
        int rows = 0;
        while (true)
        {
            var batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
            if (batch is null)
            {
                break;
            }
            using (batch)
            {
                for (int r = 0; r < batch.Length; r++)
                {
                    if (++rows > 1)
                    {
                        // ⚠ read_blob GLOBS, so a metacharacter that slipped past Resolve could match
                        // several files. Refusing beats picking one: a template that silently rendered a
                        // different partial on a directory listing change would be very hard to see.
                        // ⚠ DEFENSIVE AND UNGATED, and a mutant proved it: Resolve refuses those characters
                        // in the SUBPATH first, so nothing a template can write reaches here. What could is
                        // a ROOT containing one, which is the user's own string and not ours to validate.
                        throw new InvalidOperationException(
                            $"template '{resolved}' matched more than one file; an include names ONE file.");
                    }
                    var size = ((Int64Array)batch.Column(1)).GetValue(r) ?? 0;
                    if (size > MaxTemplateBytes)
                    {
                        throw new InvalidOperationException(
                            $"template '{resolved}' is {size} bytes, above the {MaxTemplateBytes}-byte "
                            + "ceiling for one template.");
                    }
                    var bytes = ((BinaryArray)batch.Column(0)).GetBytes(r).ToArray();
                    var when = ((TimestampArray)batch.Column(2)).GetTimestamp(r) ?? DateTimeOffset.UnixEpoch;
                    result = new Loaded(bytes, when);
                }
            }
        }
        return result;
    }

    private static RecordBatch PathBatch(string resolved)
    {
        var schema = new Schema(new[] { new Field("path", StringType.Default, nullable: false) }, metadata: null);
        var column = new StringArray.Builder().Append(resolved).Build();
        return new RecordBatch(schema, new IArrowArray[] { column }, 1);
    }

    /// <summary>Turns an include's argument into a path storage can be asked for.</summary>
    /// <remarks>
    /// <para>
    /// ⚠ MEASURED: Fluid passes the argument through VERBATIM — <c>/a</c>, <c>../a</c> and a scheme all
    /// arrive unchanged — so whatever shape this feature has, it has because of this method.
    /// </para>
    /// <para>
    /// <b>⚠⚠ THE ROOT IS ERGONOMICS, NOT A SANDBOX, and saying so is the honest part.</b> A template that
    /// can <c>{% include %}</c> is being rendered by someone who can already run SQL here, and slice 3's
    /// <c>query()</c> lets that same template read any path the host can open. Confining an include would
    /// protect nothing, so an ABSOLUTE path is simply allowed and needs no root. What is refused is refused
    /// for PREDICTABILITY: <c>..</c> resolves against a root the template's author may not know, and a glob
    /// metacharacter would make one include name a set of files.
    /// </para>
    /// </remarks>
    internal static string Resolve(string subpath)
    {
        static InvalidOperationException Bad(string path, string why) =>
            new($"template include '{path}' is not allowed: {why}");

        if (string.IsNullOrWhiteSpace(subpath))
        {
            throw Bad(subpath, "the path is empty");
        }
        if (subpath.IndexOfAny(new[] { '*', '?', '[', ']' }) >= 0)
        {
            throw Bad(subpath, "an include names ONE file, and read_blob would treat this as a glob");
        }

        // Absolute in any of the three spellings storage uses: a scheme, a rooted path, a drive letter.
        if (subpath.Contains("://", StringComparison.Ordinal) || subpath[0] == '/' || subpath[0] == '\\' ||
            (subpath.Length > 1 && subpath[1] == ':'))
        {
            return subpath;
        }

        var root = ProviderSettingsStore.Instance.GetString(Provider, RootSetting);
        if (string.IsNullOrWhiteSpace(root))
        {
            // ⚠ REFUSED, not resolved against the process working directory. An unset root has no defensible
            // default — "wherever DuckDB happens to be running" is the answer that reads a file the author
            // never named — so the failure names the setting instead of guessing.
            throw new InvalidOperationException(
                $"template include '{subpath}' is relative and no root is set: " +
                $"SET {RootSetting} = '<directory or URI prefix>', or write an absolute path.");
        }

        var parts = new List<string>();
        foreach (var seg in subpath.Replace('\\', '/').Split('/'))
        {
            if (seg.Length == 0 || seg == ".")
            {
                continue;
            }
            if (seg == "..")
            {
                throw Bad(subpath, $"a relative include cannot walk out of {RootSetting}; write it absolute");
            }
            parts.Add(seg);
        }
        if (parts.Count == 0)
        {
            throw Bad(subpath, "the path names no file");
        }

        return root!.TrimEnd('/', '\\') + "/" + string.Join("/", parts);
    }

    /// <summary>Per include argument, the resolved paths this render looked for and did not find.</summary>
    private static Dictionary<string, List<string>> Tried(TemplateContext context)
    {
        if (context.AmbientValues.TryGetValue(TriedKey, out var v) && v is Dictionary<string, List<string>> d)
        {
            return d;
        }
        var fresh = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        context.AmbientValues[TriedKey] = fresh;
        return fresh;
    }

    /// <summary>What this render looked for on behalf of one include and did not find. Empty if it was
    /// found, or if no include used that argument.</summary>
    internal static IReadOnlyList<string> TriedFor(TemplateContext context, string subpath) =>
        context.AmbientValues.TryGetValue(TriedKey, out var v)
        && v is Dictionary<string, List<string>> d
        && d.TryGetValue(subpath, out var probes)
            ? probes
            : System.Array.Empty<string>();

    /// <summary>The per-RENDER read cache. Safe by construction — one render cannot coherently see two
    /// versions of one file — and it is what makes a template that includes the same partial in a loop cost
    /// one read rather than one per iteration. It also bounds a CYCLIC include, which Fluid stops at
    /// <c>MaxRecursion</c> (100) after ~200 provider calls; cached, those cost no reads at all.</summary>
    private static Dictionary<string, Loaded?> Cache(TemplateContext context)
    {
        if (context.AmbientValues.TryGetValue(CacheKey, out var v) && v is Dictionary<string, Loaded?> d)
        {
            return d;
        }
        var fresh = new Dictionary<string, Loaded?>(StringComparer.Ordinal);
        context.AmbientValues[CacheKey] = fresh;
        return fresh;
    }
}
