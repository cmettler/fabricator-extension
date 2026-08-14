namespace Fabricator.Bridge;

/// <summary>
/// The host FileSystem opener (the calling operator's <c>ClientContext</c>, as an opaque handle) currently in
/// effect on this thread — used by a connection-free GLOBAL <b>host-FS</b> table function (a lakehouse reader:
/// Delta/Iceberg/…) to resolve DuckDB secrets while reading through the host <c>FileSystem</c> callbacks
/// (<see cref="HostFs"/>). az://, s3://, https:// + DuckDB secrets all resolve off this opener.
///
/// The generic table-function path (<c>tablefn_bind</c> / <c>tablefn_execute</c>) carries no <c>ClientContext</c>
/// argument (SQL/compute functions don't need one), so — mirroring <see cref="AmbientTransaction"/> — the host
/// records the opener via the <c>set_active_opener</c> ABI entry IMMEDIATELY before each table-function bind +
/// execution, on the SAME thread (the calls are synchronous). The host-FS binding reads it in
/// <c>Bind</c> (schema) and <c>Execute</c> (data); a SQL/compute binding never touches it.
///
/// It is an <see cref="System.Threading.AsyncLocal{T}"/> so concurrent scans on different threads carry
/// independent openers AND the value flows across <c>await</c> points / into threadpool continuations — so a
/// sync ABI-facing method may delegate to an <c>async</c> core (with <c>ConfigureAwait(false)</c>) without
/// losing the opener when the continuation resumes on a different pool thread. For the current all-sync code
/// this behaves exactly like the former <c>[ThreadStatic]</c> (set + read on the same synchronous flow; the
/// value set by one ABI call persists to the next call on the same thread). The opener is valid only for the
/// duration of the call it precedes, so a host-FS reader must do its IO (or materialize) synchronously within
/// <c>Bind</c>/<c>Execute</c> — it must not capture the opener for a later, lazy read.
/// </summary>
public static class AmbientOpener
{
    private static readonly System.Threading.AsyncLocal<nint> _current = new();

    /// <summary>The active host-FS opener on this flow (0 = none).</summary>
    public static nint Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
