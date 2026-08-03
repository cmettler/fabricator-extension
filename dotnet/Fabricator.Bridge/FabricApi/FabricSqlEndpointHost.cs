using System;

namespace Fabricator.Bridge;

/// <summary>
/// Derives the Fabric <b>workspace id</b> and <b>item name</b> from a Fabric SQL connection string, so a SQL
/// attach needs no Fabric-specific ATTACH options in the common case.
/// </summary>
/// <remarks>
/// <para><b>What a Fabric SQL endpoint host looks like</b> (measured 2026-08-03):</para>
/// <code>
/// dr2gzgsxhu2evij6vtiwxf2bby-m7ro23kcvdietgkwhxgj67hm54.datawarehouse.fabric.microsoft.com
/// └─ base32(cluster GUID) ──┘ └─ base32(WORKSPACE GUID) ┘
/// </code>
/// <para>Each segment is 26 characters of lower-case RFC-4648 base32 with the padding stripped — 26 × 5 = 130
/// bits, holding a 16-byte GUID. The SECOND segment decodes to the workspace id in <b>little-endian</b> byte
/// order, i.e. the layout .NET's <c>Guid.ToByteArray()</c> produces.</para>
/// <para><b>How that was established, and why it is an INFERENCE rather than a contract.</b> All three
/// lakehouses AND a warehouse in one workspace returned a byte-identical host, while their individual
/// <c>sql_endpoint_id</c>s matched neither segment — so segment 2 identifies the workspace and segment 1 a
/// workspace-level SQL cluster, not the item. The decode reproduced the workspace GUID exactly. But the
/// encoding is <b>undocumented</b> and this is <b>one tenant in one region</b>, so every failure mode falls
/// back rather than guessing: a host that does not match the shape, or a segment that does not decode, yields
/// null and the caller must then be told to pass the option explicitly. A WRONG workspace id would send REST
/// calls to someone else's workspace, so "no answer" is the only acceptable failure.</para>
/// <para><b>The item is much simpler:</b> on a Fabric SQL endpoint the <c>Database</c> IS the item — a
/// lakehouse or warehouse name. No decoding involved.</para>
/// <para>Kept free of Arrow, the Fabric SDK and SqlClient on purpose: its closure is the BCL, which is what
/// admits it to <c>Fabricator.Bridge.Tests</c> (tier 0). The undocumented base32 layout is exactly the part
/// that deserves offline tests rather than a live tenant.</para>
/// </remarks>
public static class FabricSqlEndpointHost
{
    /// <summary>The host suffix a Fabric SQL endpoint always carries.</summary>
    private const string FabricHostSuffix = ".fabric.microsoft.com";

    /// <summary>Base32 segments are a GUID: 16 bytes ⇒ ceil(128/5) = 26 characters, unpadded.</summary>
    private const int Base32GuidLength = 26;

    /// <summary>
    /// The workspace id encoded in a Fabric SQL endpoint host, or null when it cannot be established.
    /// </summary>
    /// <remarks>
    /// Null on ANY doubt — wrong host shape, wrong segment count, wrong segment length, a character outside the
    /// base32 alphabet, or too few decoded bytes. The caller must treat null as "ask the user", never as a
    /// reason to guess.
    /// </remarks>
    public static Guid? WorkspaceIdFromHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }
        var h = host!.Trim();
        // Strip a port if one is present ("host,1433" is SqlClient's form, "host:1433" the common one).
        int cut = h.IndexOfAny(new[] { ',', ':' });
        if (cut >= 0)
        {
            h = h.Substring(0, cut);
        }
        if (!h.EndsWith(FabricHostSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        // What remains is "<cluster>-<workspace>.datawarehouse" (or ".datamart", or no middle label at all).
        // Take the FIRST label — the encoded pair is the leftmost one. Taking the LAST label instead yields
        // "datawarehouse", which is how this was written first and what the offline tests caught immediately.
        var rest = h.Substring(0, h.Length - FabricHostSuffix.Length);
        int firstDot = rest.IndexOf('.');
        var label = firstDot >= 0 ? rest.Substring(0, firstDot) : rest;
        var parts = label.Split('-');
        if (parts.Length != 2 || parts[1].Length != Base32GuidLength)
        {
            return null;
        }
        return DecodeBase32Guid(parts[1]);
    }

    /// <summary>
    /// A 26-character unpadded lower/upper-case base32 string as a GUID, or null if it is not one.
    /// </summary>
    /// <remarks>
    /// Hand-rolled rather than via <c>Convert.FromBase64String</c>-style helpers because the BCL has no base32
    /// decoder at all. Little-endian (<c>bytes_le</c>) because that is the order .NET writes a GUID's first
    /// three fields, and it is what reproduced the measured workspace id.
    /// </remarks>
    internal static Guid? DecodeBase32Guid(string segment)
    {
        if (segment.Length != Base32GuidLength)
        {
            return null;
        }
        var bytes = new byte[16];
        int bitBuffer = 0;
        int bitCount = 0;
        int written = 0;
        foreach (char raw in segment)
        {
            int v = Base32Value(raw);
            if (v < 0)
            {
                return null;
            }
            bitBuffer = (bitBuffer << 5) | v;
            bitCount += 5;
            if (bitCount >= 8)
            {
                bitCount -= 8;
                if (written == bytes.Length)
                {
                    // 26 chars carry 130 bits; the trailing 2 are padding and must not produce a 17th byte.
                    break;
                }
                bytes[written++] = (byte)((bitBuffer >> bitCount) & 0xFF);
            }
        }
        return written == bytes.Length ? new Guid(bytes) : null;
    }

    /// <summary>RFC 4648 base32 alphabet (A–Z, 2–7), case-insensitive. Negative for anything else.</summary>
    private static int Base32Value(char c)
    {
        if (c >= 'A' && c <= 'Z') { return c - 'A'; }
        if (c >= 'a' && c <= 'z') { return c - 'a'; }
        if (c >= '2' && c <= '7') { return c - '2' + 26; }
        return -1;
    }

    /// <summary>
    /// The item name from a connection string's <c>Database</c> / <c>Initial Catalog</c> keyword, or null.
    /// </summary>
    /// <remarks>
    /// Deliberately a hand parse rather than <c>SqlConnectionStringBuilder</c>: this type must stay BCL-only to
    /// remain testable in tier 0. Handles the quoting SqlClient permits — a value may be wrapped in single or
    /// double quotes, and a doubled quote inside is an escaped one.
    /// </remarks>
    public static string? DatabaseFromConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }
        foreach (var part in SplitKeywords(connectionString!))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            var key = part.Substring(0, eq).Trim().Replace(" ", string.Empty);
            if (!key.Equals("database", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("initialcatalog", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var val = Unquote(part.Substring(eq + 1).Trim());
            return val.Length == 0 ? null : val;
        }
        return null;
    }

    /// <summary>
    /// The host from a connection string's <c>Server</c> / <c>Data Source</c> keyword, or null.
    /// </summary>
    public static string? ServerFromConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }
        foreach (var part in SplitKeywords(connectionString!))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            var key = part.Substring(0, eq).Trim().Replace(" ", string.Empty);
            if (!key.Equals("server", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("datasource", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("addr", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("address", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("networkaddress", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var val = Unquote(part.Substring(eq + 1).Trim());
            // "tcp:host,1433" — strip the protocol prefix; the port is handled by WorkspaceIdFromHost.
            int proto = val.IndexOf(':');
            if (proto > 0 && val.IndexOf('.') > proto)
            {
                var prefix = val.Substring(0, proto);
                if (prefix.Equals("tcp", StringComparison.OrdinalIgnoreCase)
                    || prefix.Equals("np", StringComparison.OrdinalIgnoreCase))
                {
                    val = val.Substring(proto + 1);
                }
            }
            return val.Length == 0 ? null : val;
        }
        return null;
    }

    /// <summary>
    /// Splits a connection string on <c>;</c> while respecting quoted values (a password or a database name may
    /// legitimately contain a semicolon inside quotes).
    /// </summary>
    private static System.Collections.Generic.List<string> SplitKeywords(string connectionString)
    {
        var parts = new System.Collections.Generic.List<string>();
        var current = new System.Text.StringBuilder();
        char quote = '\0';
        foreach (char c in connectionString)
        {
            if (quote != '\0')
            {
                current.Append(c);
                if (c == quote)
                {
                    quote = '\0';
                }
                continue;
            }
            if (c == '\'' || c == '"')
            {
                quote = c;
                current.Append(c);
                continue;
            }
            if (c == ';')
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }
        return parts;
    }

    /// <summary>Removes one layer of matching quotes, un-doubling any escaped quote inside.</summary>
    private static string Unquote(string value)
    {
        if (value.Length >= 2 && (value[0] == '\'' || value[0] == '"') && value[value.Length - 1] == value[0])
        {
            char q = value[0];
            return value.Substring(1, value.Length - 2).Replace(new string(q, 2), q.ToString());
        }
        return value;
    }
}
