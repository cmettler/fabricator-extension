namespace Fabricator.Bridge;

/// <summary>
/// A table binding whose rows are a set of data files DuckDB can read itself (ABI v101 <c>table_file_plan</c>):
/// the host then plans the scan as DuckDB's own parquet reader over exactly these files, so no row crosses into
/// managed code. The plan is METADATA only — which files, which rows of them are deleted, and the per-file
/// ordinal the row id is built from.
/// </summary>
/// <remarks>
/// <para>The answer is ONE JSON doc: <c>{"files":[{"ord":N,"path":"…","size":S,"dv":[p,…]}…]}</c>, or
/// <c>{"declined":"…"}</c> when the table cannot be read this way, which makes the host fall back to the
/// ordinary scan. A plan must describe exactly what the ordinary scan would return — the same snapshot (and pin),
/// nothing pending in the transaction — because the host reads it INSTEAD of that scan.</para>
/// <para>Internal on purpose: an experiment until the shape settles (docs/delta-read-path.md).</para>
/// </remarks>
internal interface IFileScanPlan
{
    /// <summary>The plan as a JSON doc (see the remarks).</summary>
    string FilePlanJson();

    /// <summary>The rows of a part the plan named as a <c>"stream"</c>: the given columns (the table's names), in that
    /// order, with the part's deleted rows already removed.</summary>
    Apache.Arrow.Ipc.IArrowArrayStream ScanPart(string part, IReadOnlyList<string> columns);
}
