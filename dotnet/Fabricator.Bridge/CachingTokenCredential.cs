// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;

namespace Fabricator.Bridge;

/// <summary>
/// A token per scope set, reused until five minutes before it expires. Azure SDK clients cache their token, but
/// only per client instance, and we build a client per file opened (the OneLake forwarder) and per catalog call —
/// so every one asked the credential again. With <c>AzureCliCredential</c> that is an <c>az</c> process each time,
/// one to two seconds on Windows: measured 2026-11-10, a file scan of six OneLake files took 15.6 s through the
/// forwarder against 2.2 s through duckdb-azure with a static token.
/// </summary>
internal sealed class CachingTokenCredential : TokenCredential
{
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);
    private readonly TokenCredential _inner;
    private readonly ConcurrentDictionary<string, AccessToken> _tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public CachingTokenCredential(TokenCredential inner) => _inner = inner;

    private static string Key(TokenRequestContext context) =>
        string.Join(" ", context.Scopes) + "|" + context.TenantId + "|" + context.Claims;

    private bool TryFresh(string key, out AccessToken token) =>
        _tokens.TryGetValue(key, out token) && token.ExpiresOn - DateTimeOffset.UtcNow > Margin;

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        var key = Key(requestContext);
        if (TryFresh(key, out var token))
        {
            return token;
        }
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        gate.Wait(cancellationToken);
        try
        {
            // another caller may have refreshed it while this one waited
            if (TryFresh(key, out token))
            {
                return token;
            }
            token = _inner.GetToken(requestContext, cancellationToken);
            _tokens[key] = token;
            return token;
        }
        finally
        {
            gate.Release();
        }
    }

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext,
                                                               CancellationToken cancellationToken)
    {
        var key = Key(requestContext);
        if (TryFresh(key, out var token))
        {
            return token;
        }
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryFresh(key, out token))
            {
                return token;
            }
            token = await _inner.GetTokenAsync(requestContext, cancellationToken).ConfigureAwait(false);
            _tokens[key] = token;
            return token;
        }
        finally
        {
            gate.Release();
        }
    }
}
