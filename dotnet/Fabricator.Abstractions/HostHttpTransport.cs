// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;

namespace Fabricator.Bridge;

/// <summary>
/// The seam through which <see cref="DuckDbHttpHandler"/> reaches DuckDB's HTTP stack. Declared HERE, in the
/// contract assembly, and FILLED IN by the bridge at boot — so a plugin gets the handler by referencing
/// <c>Fabricator.Abstractions</c> alone, without a reference to <c>Fabricator.Bridge</c> and without ever
/// touching an opener.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>The delegate carries no opener, deliberately.</b> The bridge's implementation reads the AMBIENT
/// ClientContext at call time, which is the only correct moment: a catalog is DATABASE-scoped and outlives
/// the connection that attached it, so anything holding an ATTACH-time <c>ClientContext*</c> would be a
/// dangling pointer the day that connection closes — the same class as the <c>table_stats</c> SIGSEGV. It is
/// also the more correct answer for SECRETS, which the user may create after the ATTACH and per session.
/// </para>
/// <para>
/// ⚠ It follows that the handler is only usable from INSIDE an ABI crossing (a provider's
/// <c>OpenCatalog</c>, a scan, a function's <c>Execute</c>) or anywhere the ambient still flows from one.
/// The ambient is an <c>AsyncLocal</c>, so it survives <c>await</c> and <c>Task.Run</c>; it does NOT survive
/// a thread parked before the crossing began. Called with no ambient, the request fails with a message
/// saying so rather than silently sending an unauthenticated one.
/// </para>
/// </remarks>
public static class HostHttpTransport
{
    /// <summary>
    /// Performs one request through DuckDB's HTTP stack, BLOCKING (DuckDB's layer is synchronous).
    /// Installed by the bridge; null until then. Returns the response envelope JSON plus the body bytes.
    /// </summary>
    /// <remarks>
    /// A TRANSPORT failure (DNS, connect, TLS) is reported inside the envelope's <c>"error"</c> rather than
    /// as an exception, so that a 404 and an unreachable host stay distinguishable — <see cref="DuckDbHttpHandler"/>
    /// is what turns that split into .NET's (a status is a response; a transport failure is an exception).
    /// </remarks>
    public static Func<string, string, string?, byte[]?, (string ResponseJson, byte[]? Body)>? Send { get; set; }

    /// <summary>True once the bridge has installed the transport and the host supports it.</summary>
    public static bool IsAvailable => Send is not null;
}
