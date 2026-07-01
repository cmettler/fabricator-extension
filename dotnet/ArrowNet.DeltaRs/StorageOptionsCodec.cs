using System.Collections.Generic;
using System.Text.Json;

namespace ArrowNet.DeltaRs;

/// <summary>
/// Encodes delta-rs <c>object_store</c> storage options onto the connection string (there is no ABI slot for
/// them) so <see cref="DeltaRsCatalog"/> can recover them. The ATTACH target (a path/URI) is kept as the head;
/// options ride after a marker as a JSON object. Mirrors how the DAX/Delta providers carry secret-derived
/// state across <c>BuildConnectionString</c> → <c>OpenCatalog</c>.
/// </summary>
internal static class StorageOptionsCodec
{
    private const string Marker = "\n#delta_rs_storage_options#\n";

    /// <summary>Maps a foreign secret's fields to object_store keys and appends them to the ATTACH target.
    /// Returns the target verbatim when there is nothing to add.</summary>
    public static string Encode(string secretType, IReadOnlyDictionary<string, string> fields, string baseConnString)
    {
        var options = MapSecret(secretType, fields);
        if (options.Count == 0)
        {
            return baseConnString;
        }
        return baseConnString + Marker + JsonSerializer.Serialize(options);
    }

    /// <summary>Splits an encoded connection string back into (target, storageOptions).</summary>
    public static (string Target, Dictionary<string, string> Options) Decode(string connectionString)
    {
        int i = connectionString.IndexOf(Marker, System.StringComparison.Ordinal);
        if (i < 0)
        {
            return (connectionString, new Dictionary<string, string>());
        }
        string target = connectionString.Substring(0, i);
        string json = connectionString.Substring(i + Marker.Length);
        var opts = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        return (target, opts);
    }

    // Best-effort v1 mapping. delta-rs/object_store accept these well-known keys; the exact set is refined when
    // cloud is validated (local FS needs none). See docs/delta-rs-provider.md.
    private static Dictionary<string, string> MapSecret(string secretType, IReadOnlyDictionary<string, string> f)
    {
        var o = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        string? Get(params string[] keys)
        {
            foreach (var k in keys)
            {
                if (f.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v)) return v;
            }
            return null;
        }

        switch (secretType.ToLowerInvariant())
        {
            case "azure":
            {
                if (Get("account_name", "azure_storage_account_name") is { } acct) o["azure_storage_account_name"] = acct;
                if (Get("tenant_id") is { } t) o["azure_storage_tenant_id"] = t;
                if (Get("client_id") is { } c) o["azure_storage_client_id"] = c;
                if (Get("client_secret") is { } s) o["azure_storage_client_secret"] = s;
                if (Get("account_key") is { } ak) o["azure_storage_account_key"] = ak;
                break;
            }
            case "s3":
            {
                if (Get("key_id", "aws_access_key_id") is { } id) o["aws_access_key_id"] = id;
                if (Get("secret", "aws_secret_access_key") is { } sk) o["aws_secret_access_key"] = sk;
                if (Get("region", "aws_region") is { } r) o["aws_region"] = r;
                if (Get("session_token") is { } st) o["aws_session_token"] = st;
                if (Get("endpoint") is { } e) o["aws_endpoint"] = e;
                break;
            }
            case "gcs":
            {
                if (Get("key_id") is { } k) o["service_account_key"] = k;
                break;
            }
        }
        return o;
    }
}
