using Microsoft.Extensions.Logging;

namespace Fabricator.Bridge;

/// <summary>
/// Process-memory marks for the HEAVY paths — the row-scaling DML and maintenance operations whose cost is
/// invisible from SQL. Off by default; enable with <c>FABRICATOR_LOG_LEVEL=Debug</c> (optionally with
/// <c>FABRICATOR_LOG_FILE</c>) and grep the <c>Fabricator.Memory</c> category.
/// </summary>
/// <remarks>
/// <para><b>Why this exists as shipped code rather than a temporary probe.</b> The UPDATE post-image grouping
/// (see <c>DeltaReader.UpdateGroupBytes</c>) was built against a plausible story about where a large UPDATE's
/// memory went, and the story was wrong: it halved the MANAGED HEAP and moved the process peak by ~11%,
/// because the dominant term was upstream of the code being changed. That was only findable by marking the
/// working set at points along the path. A memory claim about a row-scaling path is worth exactly as much as
/// the marks behind it, so the marks stay.</para>
/// <para><b>Read WS and HEAP together — they answer different questions.</b> <c>heap</c> is what OUR
/// allocations control and it responds immediately to dropping references; <c>ws</c> includes DuckDB's own
/// side of the statement and lags, because the OS does not take pages back when the GC frees objects. A
/// change that halves <c>heap</c> and barely moves <c>ws</c> has bounded our share and not the statement's —
/// which is a real result, just not the one it is tempting to report. <c>alloc</c> is cumulative
/// allocation, so its DELTA between two marks is the churn a stage caused whether or not it retained
/// anything: a stage with a small heap footprint and a huge alloc delta is a copying problem, not a
/// retention one.</para>
/// <para><b>⚠ It is gated on <see cref="ILogger.IsEnabled"/> and must stay that way.</b>
/// <c>Environment.WorkingSet</c> queries the OS for process counters — cheap next to a DML statement, not
/// cheap inside a per-batch loop, and these marks sit in per-batch loops. Never compute the values before
/// the check.</para>
/// </remarks>
public static class MemoryProbe
{
    private static readonly ILogger Log = FabricatorLog.CreateLogger("Fabricator.Memory");

    /// <summary>True when marks are being recorded. Call this before doing any work that exists only to
    /// produce a mark (counting rows, summing sizes) — a mark must never cost anything when disabled.</summary>
    public static bool Enabled => Log.IsEnabled(LogLevel.Debug);

    /// <summary>
    /// Records one mark. <paramref name="where"/> names the point in the path — keep it stable and
    /// grep-friendly, prefixed by the operation (e.g. <c>"delta update: set values parsed"</c>), because the
    /// value of a mark is comparing it against the same mark in another run.
    /// </summary>
    /// <param name="rows">Rows processed so far where the caller knows it, else negative to omit. Memory per
    /// row is the number that generalises; a raw total says nothing without the scale it was measured at.</param>
    public static void Mark(string where, long rows = -1)
    {
        if (!Log.IsEnabled(LogLevel.Debug))
        {
            return;
        }
        long ws = System.Environment.WorkingSet / (1024 * 1024);
        long heap = System.GC.GetTotalMemory(false) / (1024 * 1024);
        long alloc = System.GC.GetTotalAllocatedBytes(precise: false) / (1024 * 1024);
        if (rows >= 0)
        {
            Log.LogDebug("mem {Where}: ws={Ws}MB heap={Heap}MB alloc={Alloc}MB rows={Rows}",
                         where, ws, heap, alloc, rows);
        }
        else
        {
            Log.LogDebug("mem {Where}: ws={Ws}MB heap={Heap}MB alloc={Alloc}MB", where, ws, heap, alloc);
        }
    }
}
