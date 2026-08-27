// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

namespace Fabricator.Bridge;

/// <summary>
/// Composes the Fabric token-service URL that <see cref="FabricNotebookCredential"/> mints against.
/// </summary>
/// <remarks>
/// <para>⚠ THIS EXISTS BECAUSE THE URL IS ONLY HANDED TO US ON *PYTHON* COMPUTE. Fabric's Python runtime
/// sets <c>AZURE_FABRIC_TOKEN_SERVICE_URL</c>; a PySpark session sets no <c>AZURE_FABRIC_*</c> variable at
/// all (measured live 2026-08-13 — all four missing, and <c>notebookutils.configs</c> does not even exist
/// there). That made the whole ambient credential Python-only, so a secretless <c>abfss://</c> ATTACH in a
/// PySpark notebook fell through to <c>DefaultAzureCredential</c> and failed with its entire
/// "no credential source" chain.</para>
/// <para>Both halves ARE present on Spark, in files the credential already reads:</para>
/// <list type="bullet">
///   <item><c>/opt/token-service/tokenservice.config.json</c> → <c>tokenServiceEndpoint</c>, the bare
///   ORIGIN (<c>https://tokenservice1.&lt;region&gt;.trident.azuresynapse.net:443</c>) — no path.</item>
///   <item><c>.trident-context</c> → <c>trident.lakehouse.tokenservice.endpoint</c>, the workload URL whose
///   PATH names this capacity's SparkCore service.</item>
/// </list>
/// <para>⚠ THE BARE ORIGIN IS NOT THE ENDPOINT, and assuming it was cost a void experiment: minting
/// against it returned <b>404 on every request including the control</b>, which reads like a rejected
/// token and is really a wrong route. The env var's value is origin + <c>/api/v1/proxy</c> + that path +
/// <c>/access</c>, read off a Python kernel on the same capacity and pinned in the tests.</para>
/// <para>⚠ THE PATH IS SLICED, NEVER PARSED. It contains a DOUBLED slash
/// (<c>.../automatic//token</c>) which is load-bearing — the service 404s without it — and
/// <see cref="System.Uri"/> is entitled to normalise a path. Hand-slicing keeps the bytes we were given.</para>
/// </remarks>
internal static class FabricTokenServiceUrl
{
    private const string ProxyPrefix = "/api/v1/proxy";
    private const string AccessSuffix = "/access";

    /// <summary>Builds the mint URL from the two file-sourced values, or null when either is missing or
    /// malformed. Never throws: an unusable input must read as "no ambient credential here", not as a
    /// crash on a machine that simply is not Fabric.</summary>
    /// <param name="tokenServiceOrigin">tokenservice.config.json → tokenServiceEndpoint.</param>
    /// <param name="workloadEndpoint">.trident-context → trident.lakehouse.tokenservice.endpoint.</param>
    public static string? Compose(string? tokenServiceOrigin, string? workloadEndpoint)
    {
        if (string.IsNullOrWhiteSpace(tokenServiceOrigin) || string.IsNullOrWhiteSpace(workloadEndpoint))
        {
            return null;
        }
        var path = PathOf(workloadEndpoint!);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        return tokenServiceOrigin!.TrimEnd('/') + ProxyPrefix + path + AccessSuffix;
    }

    /// <summary>The path component of an absolute URL, by slicing — see the doubled-slash note above.
    /// Returns null when there is no authority or no path.</summary>
    internal static string? PathOf(string url)
    {
        int scheme = url.IndexOf("://", System.StringComparison.Ordinal);
        if (scheme < 0)
        {
            return null;
        }
        int pathStart = url.IndexOf('/', scheme + 3);
        if (pathStart < 0)
        {
            return null;
        }
        var rest = url.Substring(pathStart);
        // A query or fragment would belong to the workload URL, not to the route we are building.
        int cut = rest.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0)
        {
            rest = rest.Substring(0, cut);
        }
        rest = rest.TrimEnd('/');
        return rest.Length == 0 ? null : rest;
    }
}
