// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

namespace Fabricator.Bridge;

/// <summary>
/// Which session a <see cref="Host"/> query runs in. Every <see cref="Host"/> entry point REQUIRES one, with no
/// default, so each caller decides rather than inheriting whichever the overload happened to pick.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ The two answer opposite needs. Work the extension does for ITSELF — reading or writing Delta files,
/// the DuckLake sync, staging parquet — must mean the same thing whoever triggered it, so a user's
/// <c>SET TimeZone</c> or <c>default_collation</c> must not reach it: <see cref="Neutral"/>. A query issued ON
/// BEHALF of the user — a template, a plugin — should resolve names and values the way the user's own
/// statement would: <see cref="Inherit"/>.
/// </para>
/// <para>
/// ⚠ Neither joins the caller's TRANSACTION: the query still runs on a fresh connection and reads committed
/// state. See docs/host-query.md, "Session state, reworked".
/// </para>
/// </remarks>
public enum HostSession
{
    /// <summary>
    /// DuckDB's default settings with <c>TimeZone = 'UTC'</c> instead of the machine's zone, so the result does
    /// not depend on the machine or on the caller.
    /// </summary>
    Neutral,

    /// <summary>
    /// The CALLING session: every setting the caller SET, its search path and its variables are copied onto
    /// the fresh connection. Taken from the ambient client context of the current crossing; with no ambient
    /// (a call outside any crossing) it degrades to <see cref="Neutral"/>.
    /// </summary>
    Inherit,
}
