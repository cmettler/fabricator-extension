// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;

namespace Fabricator.Bridge;

/// <summary>
/// The single Fabric / Azure credential resolver shared by every provider that needs an Entra
/// <see cref="TokenCredential"/> — DAX (ADOMD <c>AccessToken</c>), OneLake IO (Azure DataLake SDK +
/// the Fabric REST / Unity-Catalog endpoints), and any future storage-scoped call. It resolves from the
/// reused <c>azure</c>-secret fields (keys <c>provider</c> / <c>tenant_id</c> / <c>client_id</c> /
/// <c>client_secret</c>):
/// <list type="bullet">
///   <item>service principal (tenant + client + secret) → <see cref="ClientSecretCredential"/> — local / CI;</item>
///   <item><c>managed_identity</c> → <see cref="ManagedIdentityCredential"/> (user-assigned when a client id
///     is present, else system-assigned);</item>
///   <item>otherwise / no secret → <see cref="DefaultAzureCredential"/> — the ambient chain (env incl.
///     <c>AZURE_TENANT_ID/CLIENT_ID/CLIENT_SECRET</c> → managed identity → Visual Studio → VS Code → Azure
///     CLI → …), which is what a <b>Fabric notebook's workspace / managed identity</b> is picked up by and
///     what SqlClient's "Active Directory Default" runs.</item>
/// </list>
/// This is what lets the extension run seamlessly BOTH locally (SP secret) and inside a Fabric notebook
/// (managed / workspace identity, no secret) — one credential feeding DAX / OneLake at their own scopes.
///
/// <para>NOTE: SQL Server uses SqlClient's native connstr-level Entra auth
/// (<c>Authentication=ActiveDirectoryServicePrincipal / ActiveDirectoryManagedIdentity</c>), not an explicit
/// <see cref="TokenCredential"/>, so it consumes the same azure-secret <i>fields</i> but does not call
/// <see cref="Resolve"/>. Kept that way deliberately (native SqlClient auth + token cache).</para>
/// </summary>
public static class FabricCredentialResolver
{
    /// <summary>Power BI / AAS XMLA token audience (DAX via ADOMD).</summary>
    public const string PowerBiScope = "https://analysis.windows.net/powerbi/api/.default";

    /// <summary>ADLS Gen2 / OneLake storage audience (Azure DataLake SDK + the OneLake REST endpoints).</summary>
    public const string StorageScope = "https://storage.azure.com/.default";

    /// <summary>Azure SQL / Fabric SQL endpoint audience — only if a raw token is ever needed; SQL normally
    /// uses SqlClient's connstr-level auth instead of an explicit token.</summary>
    public const string SqlScope = "https://database.windows.net/.default";

    /// <summary>Resolve a <see cref="TokenCredential"/> from reused azure-secret fields. Never returns null —
    /// falls back to <see cref="DefaultAzureCredential"/> when the fields carry no explicit principal.</summary>
    public static TokenCredential Resolve(IReadOnlyDictionary<string, string> fields)
    {
        string F(string key) => fields.TryGetValue(key, out var v) ? v ?? string.Empty : string.Empty;
        // An azure access_token secret (PROVIDER access_token — the common Fabric-notebook pattern:
        // ACCESS_TOKEN = notebookutils.credentials.getToken('storage')) IS the credential: serve it as-is,
        // expiry from the JWT. NOT auto-refreshed (the token source lives outside this process) —
        // re-create the secret to refresh.
        var accessToken = F("access_token");
        if (accessToken.Length > 0)
        {
            return new StaticTokenCredential(accessToken);
        }
        return Build(F("provider"), F("tenant_id"), F("client_id"), F("client_secret"));
    }

    /// <summary>A fixed, pre-minted token served for every scope request (the token's audience must match
    /// what the caller needs — e.g. storage for OneLake). Expiry parsed from the JWT <c>exp</c> claim.</summary>
    private sealed class StaticTokenCredential : TokenCredential
    {
        private readonly AccessToken _token;

        public StaticTokenCredential(string token)
        {
            _token = new AccessToken(token, JwtExpiry(token));
        }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => _token;

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(_token);

        private static DateTimeOffset JwtExpiry(string token)
        {
            try
            {
                var parts = token.Split('.');
                if (parts.Length == 3)
                {
                    var payload = parts[1].Replace('-', '+').Replace('_', '/');
                    payload += new string('=', (4 - payload.Length % 4) % 4);
                    var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds))
                    {
                        return DateTimeOffset.FromUnixTimeSeconds(seconds);
                    }
                }
            }
            catch
            {
                // opaque token — fall through to a conservative fixed lifetime
            }
            return DateTimeOffset.UtcNow.AddHours(1);
        }
    }

    /// <summary>Resolve from explicit SP strings (for callers that hold the fields as plain strings, e.g. the
    /// delta-rs provider): a service principal when all three are present, else
    /// <see cref="DefaultAzureCredential"/>.</summary>
    public static TokenCredential MintCredential(string? tenantId, string? clientId, string? clientSecret)
        => Build(provider: string.Empty, tenantId ?? string.Empty, clientId ?? string.Empty, clientSecret ?? string.Empty);

    private static TokenCredential Build(string provider, string tenantId, string clientId, string clientSecret)
    {
        // Service principal first — the truest "same principal" as the warehouse's AD service-principal auth,
        // and it wins even if provider says something else (the fields are authoritative).
        if (clientSecret.Length > 0 && clientId.Length > 0 && tenantId.Length > 0)
        {
            return new ClientSecretCredential(tenantId, clientId, clientSecret);
        }
        // Managed identity (user-assigned when a client id is present, else system-assigned).
        if (provider.Equals("managed_identity", StringComparison.OrdinalIgnoreCase))
        {
            return clientId.Length > 0
                ? new ManagedIdentityCredential(clientId)
                : new ManagedIdentityCredential();
        }
        // credential_chain / default / no secret → the ambient chain.
        return AmbientChain();
    }

    /// <summary>The ambient credential: on Fabric notebook/Spark compute the token-service-backed
    /// <see cref="FabricNotebookCredential"/> (DefaultAzureCredential has NO source there — no IMDS, no env;
    /// validated live 2026-07-14), everywhere else <see cref="DefaultAzureCredential"/> (env / managed
    /// identity / VS / az CLI).</summary>
    public static TokenCredential AmbientChain()
        => FabricNotebookCredential.IsAvailable
            ? new FabricNotebookCredential()
            : new DefaultAzureCredential();

    /// <summary>"Active Directory Default"-style fallback for a target with no attached secret: a remote
    /// (<c>scheme://</c>) endpoint with no inline password / user id gets a <see cref="DefaultAzureCredential"/>;
    /// a local / on-prem target, or one carrying its own inline auth, gets <c>null</c> (no token).</summary>
    public static TokenCredential? ResolveForRemoteTarget(string connectionString)
    {
        var lower = connectionString.ToLowerInvariant();
        bool remote = lower.Contains("://"); // cloud endpoints use a scheme:// data source; on-prem/localhost don't
        bool hasInlineAuth = lower.Contains("password=") || lower.Contains("pwd=") || lower.Contains("user id=");
        return remote && !hasInlineAuth ? AmbientChain() : null;
    }

    /// <summary>Acquire a scoped access token (Azure.Identity caches + refreshes internally, so per-call use is
    /// cheap and always valid).</summary>
    public static AccessToken GetToken(TokenCredential credential, string scope)
        => credential.GetToken(new TokenRequestContext(new[] { scope }), default);

    /// <summary>Async token acquisition (use on IO paths; sync <c>HttpClient</c> transport hangs under the
    /// hostfxr-hosted CLR, so the OneLake callers await this).</summary>
    public static ValueTask<AccessToken> GetTokenAsync(
        TokenCredential credential, string scope, CancellationToken ct = default)
        => credential.GetTokenAsync(new TokenRequestContext(new[] { scope }), ct);
}
