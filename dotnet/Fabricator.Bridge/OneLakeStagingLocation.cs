using System;

namespace Fabricator.Bridge;

/// <summary>
/// One staging root, in the TWO spellings the two engines demand of it.
/// </summary>
/// <param name="ClientRoot">
/// The <c>abfss://</c> form, which is what OUR side writes parquet through (duckdb-azure / the host
/// filesystem). Never ends in a slash.
/// </param>
/// <param name="LoadRoot">
/// The <c>https://</c> form, which is the only spelling a SQL Server / Fabric Warehouse
/// <c>COPY INTO … FROM</c> accepts. Never ends in a slash.
/// </param>
public readonly record struct StagingLocation(string ClientRoot, string LoadRoot);

/// <summary>
/// Parses and normalises the staging location a Fabric Warehouse <c>COPY INTO</c> load writes its
/// intermediate parquet to.
///
/// <para><b>Why this exists as its own BCL-only file:</b> it is the third time in this codebase that one
/// storage account has to be named differently by us and by the SQL engine — after
/// <c>s3://</c>-vs-the-secret's-endpoint and <c>adls://</c>-vs-<c>abfss://</c>
/// (<see cref="ExternalTableRouting.ComposeStorageUri"/>) — and getting it wrong writes bytes somewhere
/// nobody looks. Keeping it free of the Arrow/SDK boundary is what lets it be tested offline.</para>
///
/// <para>The mapping itself is mechanical and total, in both directions:
/// <c>abfss://&lt;fs&gt;@&lt;host&gt;/&lt;path&gt;</c> ⇄ <c>https://&lt;host&gt;/&lt;fs&gt;/&lt;path&gt;</c>.
/// For OneLake that reads as workspace ⇄ first path segment, which is exactly the form the COPY INTO
/// documentation gives (<c>https://onelake.dfs.fabric.microsoft.com/&lt;workspace&gt;/&lt;item&gt;/Files/…</c>).</para>
/// </summary>
public static class OneLakeStagingLocation
{
    private const string OneLakeHost = "onelake.";

    /// <summary>
    /// Normalises a user-supplied staging root into both spellings, or throws with a message naming what
    /// was expected. Accepts either spelling as input, so a user who copied the location out of the Fabric
    /// portal (https) and one who copied it out of an ATTACH (abfss) both get the same result.
    /// </summary>
    public static StagingLocation Parse(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            throw new ArgumentException("mssql_copy_into_staging: the staging location is empty.");
        }
        var raw = location.Trim().Replace('\\', '/').TrimEnd('/');
        string host, fileSystem, path;

        if (raw.StartsWith("abfss://", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("abfs://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = raw.Substring(raw.IndexOf("://", StringComparison.Ordinal) + 3);
            int at = rest.IndexOf('@');
            int slash = rest.IndexOf('/');
            if (at < 0 || slash < 0 || at > slash)
            {
                throw new ArgumentException(
                    "mssql_copy_into_staging: expected abfss://<filesystem>@<host>/<path>, got '" + location + "'.");
            }
            fileSystem = rest.Substring(0, at);
            host = rest.Substring(at + 1, slash - at - 1);
            path = rest.Substring(slash + 1);
        }
        else if (raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = raw.Substring(8);
            int slash = rest.IndexOf('/');
            if (slash < 0)
            {
                throw new ArgumentException(
                    "mssql_copy_into_staging: expected https://<host>/<filesystem>/<path>, got '" + location + "'.");
            }
            host = rest.Substring(0, slash);
            var after = rest.Substring(slash + 1);
            int seg = after.IndexOf('/');
            if (seg < 0)
            {
                throw new ArgumentException(
                    "mssql_copy_into_staging: expected https://<host>/<filesystem>/<path> — '" + location +
                    "' names a filesystem but no path inside it.");
            }
            fileSystem = after.Substring(0, seg);
            path = after.Substring(seg + 1);
        }
        else
        {
            throw new ArgumentException(
                "mssql_copy_into_staging: expected abfss://<filesystem>@<host>/<path> or " +
                "https://<host>/<filesystem>/<path>, got '" + location + "'.");
        }

        if (host.Length == 0 || fileSystem.Length == 0 || path.Length == 0)
        {
            throw new ArgumentException(
                "mssql_copy_into_staging: could not parse host / filesystem / path from '" + location + "'.");
        }

        // ⚠ A HIDDEN SEGMENT FAILS SILENTLY, WHICH IS WHY IT IS REFUSED HERE RATHER THAN LEFT TO THE ENGINE.
        // COPY INTO IGNORES files whose name begins with '_' or '.' unless they are named explicitly — so a
        // staging root under, say, `Files/_stage` stages parquet perfectly, the COPY INTO succeeds, and it
        // loads NOTHING. A load that reports success and moves no rows is the worst outcome available here.
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length > 0 && (segment[0] == '_' || segment[0] == '.'))
            {
                throw new ArgumentException(
                    "mssql_copy_into_staging: path segment '" + segment + "' begins with '" + segment[0] +
                    "'. COPY INTO ignores such names, so staged files there would load zero rows without an " +
                    "error. Choose a staging path with no '_'- or '.'-prefixed segment.");
            }
        }

        // OneLake only: `Tables/` is the lakehouse's managed-table area, and dropping loose parquet in it
        // makes the lakehouse advertise a broken table to every engine that browses it. `Files/` is the
        // unmanaged area and is what the COPY INTO documentation's own example uses. A plain ADLS account has
        // no such convention, so the check is deliberately scoped to the OneLake host.
        //
        // ⚠ THE AREA IS THE SECOND PATH SEGMENT, NOT THE FIRST — on OneLake the FILESYSTEM is the workspace,
        // so the path reads <item>/<area>/…  (the first version of this check tested segment 0 and never
        // fired; its own test is what caught that).
        if (host.StartsWith(OneLakeHost, StringComparison.OrdinalIgnoreCase))
        {
            var segments = path.Split('/');
            if (segments.Length > 1 && string.Equals(segments[1], "Tables", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "mssql_copy_into_staging: '" + location + "' stages into a lakehouse's Tables/ area, " +
                    "where loose parquet files surface as a broken managed table. Stage under Files/ instead.");
            }

            // ⚠ ON ONELAKE THE WORKSPACE AND ITEM MUST BE GUIDs — MEASURED LIVE 2026-08-10, and this is the
            // asymmetry that makes it worth refusing rather than passing through. OUR writer accepts display
            // NAMES perfectly well (`abfss://Test@…/LH.Lakehouse/Files/…` stages parquet without complaint),
            // so the location looks entirely correct right up until the warehouse reads it — and then fails
            // with `13840: Access token couldn't be fetched for storage path '…' as it's an unsupported URL
            // or cause of a transient error`, which names neither the workspace, the names, nor GUIDs, and
            // reads like a permissions or outage problem. The same path with both segments as GUIDs loads
            // 50 000 rows. So the two spellings differ ONLY at the far end, which is exactly where an error
            // is least diagnosable.
            //
            // Resolving names to GUIDs is possible (FabricApiClient does it for the `fabric.*` functions) and
            // is deliberately NOT done here: it would cost a REST listing at ATTACH and a Fabric credential
            // on a path that has neither today. A one-line error naming the GUID form is the better trade,
            // and the ids are one `fabric.workspaces()` / `fabric.items()` call away.
            // No length guard, unlike the Tables/ check above: `path` is non-empty by here, so `segments[0]`
            // always exists, and a root sitting directly at the item (`…@onelake…/LH.Lakehouse`) must be
            // caught too.
            if (!(IsGuid(fileSystem) && IsGuid(segments[0])))
            {
                throw new ArgumentException(
                    "mssql_copy_into_staging: '" + location + "' names its OneLake workspace and/or item by " +
                    "DISPLAY NAME. Writing there works, but a warehouse COPY INTO reading it fails with " +
                    "'13840 … unsupported URL'. Use the GUID form — abfss://<workspaceId>@" +
                    "onelake.dfs.fabric.microsoft.com/<itemId>/Files/<path> — the ids are available from " +
                    "fabric.workspaces() and fabric.items().");
            }
        }

        // A bare 8-4-4-4-12; deliberately NOT Guid.TryParse, which also accepts the braced, parenthesised and
        // 32-digit forms — none of which is what a OneLake URL carries, so accepting them here would wave
        // through a spelling the warehouse would then reject.
        static bool IsGuid(string s) =>
            s.Length == 36 && Guid.TryParseExact(s, "D", out _);

        return new StagingLocation(
            ClientRoot: "abfss://" + fileSystem + "@" + host + "/" + path,
            LoadRoot: "https://" + host + "/" + fileSystem + "/" + path);
    }
}
