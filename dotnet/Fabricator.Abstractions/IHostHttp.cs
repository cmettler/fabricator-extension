// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

namespace Fabricator.Bridge;

/// <summary>
/// DuckDB's own HTTP stack, as a host service (<see cref="FabricatorServices"/>). A request made through it
/// inherits the <c>TYPE http</c> secret whose SCOPE covers the URL, plus <c>ca_cert_file</c>,
/// <c>http_proxy*</c>, <c>http_timeout</c> and the retry knobs — so a plugin calling a REST API needs no
/// credential surface of its own. <see cref="DuckDbHttpHandler"/> wraps this as an ordinary .NET
/// <c>HttpMessageHandler</c> and is what most callers should use.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>No opener is passed, deliberately</b> — the bridge's implementation reads the AMBIENT
/// <c>ClientContext</c> at call time. See <see cref="FabricatorServices"/> for why capturing one would
/// dangle, and for the crossing rule that follows from it. Called with no ambient, the request fails with a
/// message saying so rather than silently sending an unauthenticated one.
/// </para>
/// <para>
/// ⚠ <b>httpfs is a hard prerequisite and its absence is not obvious.</b> Only httpfs' <c>Load</c> installs
/// DuckDB's real HTTP implementation; the built-in fallback does GET alone and reads NO SECRETS AT ALL, so a
/// half-configured host would apply no credential while looking fine. The host auto-loads httpfs at REQUEST
/// time and otherwise refuses by name.
/// </para>
/// </remarks>
public interface IHostHttp
{
    /// <summary>
    /// Performs one request, BLOCKING (DuckDB's layer is synchronous). Returns the response envelope JSON
    /// plus the body bytes.
    /// </summary>
    /// <param name="method">GET, PUT, HEAD, DELETE or POST. PATCH is refused BY NAME — sending it as a POST
    /// would corrupt a write while looking like it worked.</param>
    /// <param name="url">The absolute request URL; the matching <c>TYPE http</c> secret is selected by it.</param>
    /// <param name="headersJson">Request headers as a JSON object, or <see langword="null"/>. ⚠ ONE VALUE PER
    /// HEADER NAME, in both directions — that is DuckDB's model, and it is why <c>Set-Cookie</c> is
    /// unrepresentable.</param>
    /// <param name="body">The request body, FULLY BUFFERED (as is the response). A paging REST reader must
    /// page; it cannot stream.</param>
    /// <remarks>
    /// A TRANSPORT failure (DNS, connect, TLS) is reported inside the envelope's <c>"error"</c> rather than as
    /// an exception, so that a 404 and an unreachable host stay distinguishable —
    /// <see cref="DuckDbHttpHandler"/> is what turns that split into .NET's (a status is a response; a
    /// transport failure is an exception).
    /// </remarks>
    (string ResponseJson, byte[]? Body) Send(string method, string url, string? headersJson, byte[]? body);
}
