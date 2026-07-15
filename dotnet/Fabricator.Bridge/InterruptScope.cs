using System;
using System.Threading;

namespace Fabricator.Bridge;

/// <summary>
/// Bridges DuckDB's query interrupt (Ctrl+C via <c>Connection::Interrupt()</c>, or a query timeout) to a
/// <see cref="CancellationToken"/> so long-running C# I/O is cancelled instead of hanging the shell.
///
/// <para>DuckDB only checks <c>ClientContext::interrupted</c> BETWEEN operator calls, so a single blocking
/// <c>get_next</c> (a large OneLake/S3/SQL read) is invisible to it — the task thread is parked inside our
/// C# I/O. This scope runs a lightweight <see cref="Timer"/> (pool-scheduled, no dedicated thread) that reads
/// the interrupt flag via the host <c>is_interrupted(opener)</c> callback every <see cref="PollMs"/> ms and,
/// on interrupt, cancels <see cref="Token"/>. The in-flight (or next) engineered-wood read observes the token
/// and throws <see cref="OperationCanceledException"/>, unblocking the task thread into DuckDB's normal error
/// path. See docs/cancellation.md.</para>
///
/// <para>Lifetime contract: <paramref name="opener"/> is a <c>ClientContext*</c> that must stay valid while the
/// scope is alive — it is (the ClientContext lives for the whole table-function execution, and the scope wraps
/// exactly that execution). <see cref="Dispose"/> stops the timer so no read outlives the context. When the
/// host provides no <c>is_interrupted</c> callback, or the opener is null, the timer does not start and
/// <see cref="Token"/> is driven only by an optional linked token — behavior-neutral (a never-tripped token is
/// identical to passing <c>default</c>).</para>
/// </summary>
internal sealed class InterruptScope : IDisposable
{
    // ~50 ms: imperceptible to a human Ctrl+C, and the poll is one atomic-read P/Invoke — negligible overhead.
    private const int PollMs = 50;

    private readonly CancellationTokenSource _cts;
    private readonly Timer? _timer;

    /// <param name="opener">The calling operator's ClientContext handle (from the scan/op). 0 disables polling.</param>
    /// <param name="linked">An optional caller token folded into <see cref="Token"/> (cancelling either cancels it).</param>
    public InterruptScope(nint opener, CancellationToken linked = default)
    {
        _cts = linked.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(linked)
            : new CancellationTokenSource();

        if (opener != 0 && HostFs.CanInterrupt)
        {
            var cts = _cts;
            // Pool-scheduled callback (no dedicated thread); each fire is a single cheap P/Invoke atomic read.
            // Cancel() is idempotent, so we needn't stop the timer on the first trip — Dispose stops it.
            _timer = new Timer(_ =>
            {
                if (HostFs.IsInterrupted(opener))
                {
                    try { cts.Cancel(); }
                    catch { /* raced with Dispose disposing the CTS — harmless */ }
                }
            }, null, PollMs, PollMs);
        }
    }

    /// <summary>The token to hand to cancellable C# I/O (engineered-wood reads/writes, async SqlClient, …).</summary>
    public CancellationToken Token => _cts.Token;

    public void Dispose()
    {
        // Dispose the timer and WAIT for any in-flight callback to finish (the WaitHandle overload) before
        // returning — so no poll reads the opener (ClientContext*) after this scope closes, and none touches
        // the CTS after it is disposed. Guarantees the lifetime contract in the class summary.
        if (_timer is not null)
        {
            using var done = new ManualResetEvent(false);
            if (_timer.Dispose(done))
            {
                done.WaitOne();
            }
        }
        _cts.Dispose();
    }
}
