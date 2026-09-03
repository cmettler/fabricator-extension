// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Fabricator.SqlServer;

/// <summary>
/// Carries a <b>Fabric REST</b> credential from the ATTACH secret to <see cref="SqlServerCatalog"/>, so the
/// catalog-bound <c>fabric_*</c> functions work on a Fabric SQL attach and not only on a OneLake Delta one
/// (docs/fabric-api-functions.md §9h).
/// </summary>
/// <remarks>
/// <para><b>Why a connection-string marker.</b> <c>IProvider.OpenCatalog</c> receives only the assembled
/// connection string, so secret fields are consumed by <see cref="SqlServerBackend.BuildConnectionString"/> and
/// then lost. The same problem was already solved twice this way — <c>SqlServerCatalog.AccessTokenKeyword</c>
/// here and <c>FabricatorDeltaCred</c> on the Delta side — so this reuses the proven shape rather than widening
/// the ABI for it.</para>
/// <para><b>⚠ Ordering is load-bearing.</b> The access-token marker is defined as "everything after it is the
/// token", so this marker is appended <i>after</i> it and must be stripped <i>before</i> it. Base64 contains no
/// <c>;</c>, so the split is unambiguous. Appending in the other order would silently fold this marker into the
/// access token and break authentication.</para>
/// <para><b>⚠ A pre-minted <c>access_token</c> is deliberately NOT forwarded.</b> Its audience is
/// <c>database.windows.net</c> (SQL), while Fabric REST needs <c>api.fabric.microsoft.com</c> — the same
/// audience mismatch CLAUDE.md already records for a static storage token on an abfss ATTACH. Forwarding it
/// would guarantee a 401 from the first Fabric call; emitting nothing instead falls through to the ambient
/// chain, which genuinely works both on Fabric compute (the token service) and off it (az CLI / env / managed
/// identity). Only a <i>renewable principal</i> — one that can mint a token per audience — is carried.</para>
/// <para>Note the Delta path does forward a static token today and has the same hazard; that is pre-existing
/// behaviour on a live-validated path and is left alone here rather than changed as a side effect.</para>
/// </remarks>
internal static class SqlServerFabricCredential
{
    /// <summary>Trailing marker holding base64 JSON of the normalized credential fields.</summary>
    internal const string Marker = ";FabricatorFabricCred=";

    /// <summary>
    /// Appends the marker when <paramref name="fields"/> describe a renewable Entra principal. Returns
    /// <paramref name="connectionString"/> unchanged otherwise — an attach with no usable principal is not an
    /// error, it just falls back to the ambient credential chain.
    /// </summary>
    internal static string Append(string connectionString, string secretType,
                                  IReadOnlyDictionary<string, string> fields)
    {
        var carried = Normalize(secretType, fields);
        if (carried is null)
        {
            return connectionString;
        }
        var json = JsonSerializer.Serialize(carried);
        return connectionString + Marker + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// Splits a connection string into the bare string and the carried Fabric credential fields (null when the
    /// marker is absent). Must run BEFORE the access-token split — see the ordering note on the class.
    /// </summary>
    internal static (string ConnectionString, IReadOnlyDictionary<string, string>? Fields) Extract(
        string connectionString)
    {
        int idx = connectionString.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return (connectionString, null);
        }
        var b64 = connectionString.Substring(idx + Marker.Length);
        var bare = connectionString.Substring(0, idx);
        try
        {
            var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(
                Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
            return (bare, fields);
        }
        catch (Exception)
        {
            // A corrupt marker must not fail the ATTACH: the SQL half of the catalog is fully functional
            // without a Fabric credential, and the fabric_* functions degrade to the ambient chain.
            return (bare, null);
        }
    }

    /// <summary>
    /// Normalizes a secret's fields into the shape <c>FabricCredentialResolver.Resolve</c> reads
    /// (<c>provider</c> / <c>tenant_id</c> / <c>client_id</c> / <c>client_secret</c>), or null when the secret
    /// carries no renewable principal.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? Normalize(string secretType,
                                                                  IReadOnlyDictionary<string, string> fields)
    {
        if (fields is null || fields.Count == 0)
        {
            return null;
        }
        string F(string key) => fields.TryGetValue(key, out var v) ? v ?? string.Empty : string.Empty;

        // An `azure` secret already speaks this vocabulary — it is the same secret the Delta/OneLake and DAX
        // paths consume, so a user who has one for either already has one that works here.
        if (secretType.Equals("azure", StringComparison.OrdinalIgnoreCase))
        {
            var provider = F("provider").ToLowerInvariant();
            if (provider == "managed_identity")
            {
                return Fields("managed_identity", clientId: F("client_id"));
            }
            if (F("tenant_id").Length > 0 && F("client_id").Length > 0 && F("client_secret").Length > 0)
            {
                return Fields("service_principal", F("tenant_id"), F("client_id"), F("client_secret"));
            }
            return null; // access_token / credential_chain: see the class remarks
        }

        // A `fabricator` (mssql) secret expresses Entra auth the way SqlClient wants it — authentication mode
        // plus user/password — so the principal has to be recovered from that shape. `azure_tenant_id` was
        // DECLARED in SecretFields from the beginning and never consumed by anything (it exists for parity
        // with the C++ mssql secret); it is load-bearing here, because SqlClient infers the tenant from the
        // server it is connecting to and ClientSecretCredential cannot.
        var auth = NormalizeAuth(F("authentication"));
        if (auth is "managedidentity" or "msi" or "activedirectorymanagedidentity")
        {
            return Fields("managed_identity", clientId: F("user"));
        }
        if (auth is "serviceprincipal" or "spn" or "activedirectoryserviceprincipal")
        {
            var tenant = F("azure_tenant_id");
            if (tenant.Length == 0 || F("user").Length == 0 || F("password").Length == 0)
            {
                // A service-principal SQL login without a tenant cannot mint a Fabric token. Falling back to
                // the ambient chain is the useful answer; the fabric_* functions say so if it finds nothing.
                return null;
            }
            return Fields("service_principal", tenant, F("user"), F("password"));
        }
        return null;
    }

    private static Dictionary<string, string> Fields(string provider, string tenantId = "",
                                                     string clientId = "", string clientSecret = "")
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["provider"] = provider };
        if (tenantId.Length > 0) d["tenant_id"] = tenantId;
        if (clientId.Length > 0) d["client_id"] = clientId;
        if (clientSecret.Length > 0) d["client_secret"] = clientSecret;
        return d;
    }

    // Mirrors SqlServerBackend.NormalizeAuth (strips spaces/underscores/hyphens, lowercases) so the same
    // spellings the connection-string mapper accepts are recognized here.
    private static string NormalizeAuth(string raw)
    {
        var sb = new StringBuilder();
        foreach (var c in raw.ToLowerInvariant())
        {
            if (c != ' ' && c != '_' && c != '-')
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
