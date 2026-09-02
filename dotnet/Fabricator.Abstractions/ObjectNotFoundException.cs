// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

namespace Fabricator.Bridge;

/// <summary>
/// The named object genuinely DOES NOT EXIST — as distinct from existing and being unreadable. Crosses the
/// ABI as <c>FABRICATOR_NOT_FOUND</c> (3) rather than the generic <c>FABRICATOR_ERROR</c> (1), which is what
/// lets the host tell "absent" from "broken" without matching on message text.
///
/// <para><b>Why the distinction is load-bearing.</b> The host's catalog treats a failed column fetch as
/// proof the table is gone: it drops the entry AND removes the name from enumeration, so
/// <c>CREATE TABLE IF NOT EXISTS</c> and <c>CREATE OR REPLACE</c> behave correctly after an out-of-band
/// DROP. That is right for absence and wrong for everything else — a corrupt log, an expired credential, a
/// transient network failure all used to make a table with intact data VANISH from the catalog, reported as
/// "Table with name t does not exist!". Throw this ONLY when absence is established; let every other
/// failure propagate with its own message.</para>
///
/// <para>Do not throw it for "I could not determine whether it exists". Uncertainty is not absence, and
/// reporting it as absence is the failure mode this type exists to remove.</para>
/// </summary>
public sealed class ObjectNotFoundException : Exception
{
    /// <param name="kind">What was looked for — "table", "schema", "function". Used only in the message.</param>
    /// <param name="name">The object's name as the caller spelled it.</param>
    public ObjectNotFoundException(string kind, string name)
        : base($"{kind} '{name}' does not exist.")
    {
        Kind = kind;
        Name = name;
    }

    /// <param name="inner">The provider error that ESTABLISHED absence (a 404, a missing directory). Kept so
    /// a host logging the chain can still see why, even though the status alone reaches the ABI.</param>
    public ObjectNotFoundException(string kind, string name, Exception? inner)
        : base($"{kind} '{name}' does not exist.", inner)
    {
        Kind = kind;
        Name = name;
    }

    /// <summary>What was looked for — "table", "schema", "function".</summary>
    public string Kind { get; }

    /// <summary>The object's name as the caller spelled it.</summary>
    public string Name { get; }
}
