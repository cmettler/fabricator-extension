// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using Azure.Core;
using Azure.Storage;
using Azure.Storage.Files.DataLake;

namespace Fabricator.Bridge;

/// <summary>
/// A credential for <b>ADLS Gen2</b> storage IO (the <c>abfss://</c> transport), independent of which CATALOG
/// sits on top of it. Fabric OneLake is one such account — with a Fabric REST catalog bolted on — and a plain
/// storage account is another; both speak the same DFS endpoint, so both use this.
///
/// <para>Two shapes: an Entra <see cref="TokenCredential"/> (service principal / managed identity / a
/// pre-minted access token) or a <see cref="StorageSharedKeyCredential"/> (account key, or a storage
/// connection string carrying one). <b>A plain ADLS Gen2 account accepts BOTH</b> — Entra via RBAC role
/// assignments is fully supported there and is the better practice; shared key is simply the form an account
/// is often handed out as, and the form nothing in this codebase could consume before. The asymmetry runs the
/// other way: <b>OneLake is Entra-ONLY</b>, and Entra is additionally the only shape that can authenticate the
/// Fabric REST + Unity-Catalog endpoints. So the shape follows the SECRET, not the kind of account — with the
/// one restriction that OneLake refuses a key (see <paramref name="entraOnly"/> on
/// <see cref="FromFields"/>).</para>
///
/// <para><b>The URI is authoritative, not the credential.</b> A connection string carries its own
/// <c>AccountName</c>/<c>EndpointSuffix</c> and the SDK would happily derive an endpoint from them; we
/// deliberately parse out only the key material and build the client against the host in the <c>abfss://</c>
/// path instead. One catalog may span accounts, and a credential silently redirecting IO to the account named
/// in the connection string would read as data loss. A genuine mismatch then fails loudly at the signature
/// check, which is the outcome we want.</para>
/// </summary>
public sealed class AdlsCredential
{
    private readonly TokenCredential? _token;
    private readonly StorageSharedKeyCredential? _sharedKey;

    private AdlsCredential(TokenCredential? token, StorageSharedKeyCredential? sharedKey)
    {
        _token = token;
        _sharedKey = sharedKey;
    }

    /// <summary>The Entra credential, when this is one. Null for a shared-key account — the Fabric REST /
    /// Unity-Catalog surfaces require a token and simply do not exist for a plain storage account.</summary>
    public TokenCredential? Token => _token;

    public static AdlsCredential FromToken(TokenCredential token) => new(token, null);

    /// <summary>Builds the storage credential from the reused <c>azure</c>-secret fields. Prefers explicit key
    /// material (<c>connection_string</c>, or <c>account_name</c> + <c>account_key</c>) and otherwise defers to
    /// <see cref="FabricCredentialResolver"/>, which never returns null — so this never returns null either, and
    /// a fields-less secret still yields the ambient chain (a Fabric notebook's workspace identity, an az login).
    /// </summary>
    /// <param name="entraOnly">
    /// Set for a target that CANNOT authenticate with a shared key — <b>OneLake is Entra-only</b>. Without this,
    /// an azure secret that happens to carry a <c>connection_string</c> (perfectly normal if the same secret also
    /// serves a plain storage account) would silently downgrade a Fabric attach to key auth that OneLake rejects.
    /// Key material is then ignored rather than preferred, so the Entra chain is reached exactly as before.
    /// </param>
    public static AdlsCredential FromFields(IReadOnlyDictionary<string, string> fields, bool entraOnly = false)
    {
        string F(string key) => fields.TryGetValue(key, out var v) ? v ?? string.Empty : string.Empty;

        // An EXPLICITLY configured service principal outranks key material, mirroring
        // FabricCredentialResolver.Build's "the fields are authoritative, SP first" rule. The two are normally
        // mutually exclusive (DuckDB's azure secret picks one PROVIDER), but a secret reused across targets can
        // carry both — and silently preferring the key would downgrade a deliberate RBAC setup to key auth,
        // which is the opposite of what the author asked for and defeats any audit built on the principal.
        bool explicitServicePrincipal =
            F("tenant_id").Length > 0 && F("client_id").Length > 0 && F("client_secret").Length > 0;

        if (!entraOnly && !explicitServicePrincipal)
        {
            var connectionString = F("connection_string");
            if (connectionString.Length > 0 && TryParseConnectionString(connectionString, out var fromConn))
            {
                return new AdlsCredential(null, fromConn);
            }
            var accountName = F("account_name");
            var accountKey = F("account_key");
            if (accountName.Length > 0 && accountKey.Length > 0)
            {
                return new AdlsCredential(null, new StorageSharedKeyCredential(accountName, accountKey));
            }
        }
        return new AdlsCredential(FabricCredentialResolver.Resolve(fields), null);
    }

    /// <summary>A <c>DataLakeFileSystemClient</c> for <paramref name="fileSystem"/> on <paramref name="host"/>
    /// (the host parsed out of the <c>abfss://</c> path — see the class remarks on why it wins).</summary>
    public DataLakeFileSystemClient CreateFileSystemClient(string host, string fileSystem)
    {
        var uri = new Uri($"https://{host}/{fileSystem}");
        return _sharedKey is not null
            ? new DataLakeFileSystemClient(uri, _sharedKey)
            : new DataLakeFileSystemClient(uri, _token ?? FabricCredentialResolver.AmbientChain());
    }

    /// <summary>A <c>DataLakeDirectoryClient</c> for a directory under <paramref name="fileSystem"/> — the
    /// DFS-native recursive delete and atomic directory rename that DuckDB's azure FileSystem does not
    /// implement at all.</summary>
    public DataLakeDirectoryClient CreateDirectoryClient(string host, string fileSystem, string pathUnderFs)
    {
        var uri = new Uri($"https://{host}/{fileSystem}/{pathUnderFs}");
        return _sharedKey is not null
            ? new DataLakeDirectoryClient(uri, _sharedKey)
            : new DataLakeDirectoryClient(uri, _token ?? FabricCredentialResolver.AmbientChain());
    }

    /// <summary>Parses <c>AccountName</c>/<c>AccountKey</c> out of a storage connection string. Returns false
    /// for any other shape (a SAS-only connection string carries no key) so the caller can fall through to the
    /// Entra chain rather than fail — an unparseable connection string is a reason to try something else, not
    /// to abort the attach.</summary>
    private static bool TryParseConnectionString(string connectionString, out StorageSharedKeyCredential credential)
    {
        credential = null!;
        string? name = null;
        string? key = null;
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            // AccountKey is base64 and CONTAINS '=' padding — split on the FIRST '=' only.
            var k = part.Substring(0, eq).Trim();
            var v = part.Substring(eq + 1).Trim();
            if (k.Equals("AccountName", StringComparison.OrdinalIgnoreCase)) { name = v; }
            else if (k.Equals("AccountKey", StringComparison.OrdinalIgnoreCase)) { key = v; }
        }
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(key))
        {
            return false;
        }
        credential = new StorageSharedKeyCredential(name, key);
        return true;
    }
}

/// <summary>Scheme-level classification of a catalog root. Deliberately separate from
/// <see cref="FabricLakehouse.IsOneLake"/>: this answers <i>how do we do IO</i> (the ADLS Gen2 DFS transport),
/// while <c>IsOneLake</c> answers <i>what kind of catalog is this</i> (a Fabric lakehouse, with a REST +
/// Unity-Catalog surface for enumerating tables). Every OneLake root is an ADLS Gen2 root; the converse is
/// false, and conflating the two is what made a plain storage account fall back to the host filesystem.
/// </summary>
public static class AdlsPath
{
    /// <summary>True for an <c>abfss://</c> (ADLS Gen2) root — OneLake included.</summary>
    public static bool IsAdlsGen2(string path) =>
        path.TrimStart().StartsWith("abfss://", StringComparison.OrdinalIgnoreCase);
}
