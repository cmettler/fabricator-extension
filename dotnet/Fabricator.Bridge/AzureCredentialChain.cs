// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;

namespace Fabricator.Bridge;

/// <summary>
/// The <c>CHAIN</c> of a DuckDB azure secret (<c>CREATE SECRET … (TYPE azure, PROVIDER credential_chain,
/// CHAIN 'cli;env')</c>): the credential sources to try, in order. BCL-only so the parsing is testable on its own;
/// <see cref="FabricCredentialResolver"/> turns the names into credentials.
/// </summary>
public static class AzureCredentialChain
{
    /// <summary>The source names DuckDB's azure extension accepts.</summary>
    public static readonly IReadOnlyList<string> Sources =
        new[] { "cli", "env", "managed_identity", "workload_identity", "default" };

    /// <summary>The sources of <paramref name="chain"/>, lower-cased, in order; empty for an empty chain.
    /// An unknown name is refused, naming the ones that exist: a typo must not fall back to a different source.</summary>
    public static IReadOnlyList<string> Parse(string? chain)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(chain))
        {
            return result;
        }
        foreach (var raw in chain.Split(';'))
        {
            var part = raw.Trim().ToLowerInvariant();
            if (part.Length == 0)
            {
                continue;
            }
            if (!((IList<string>)Sources).Contains(part))
            {
                throw new ArgumentException(
                    $"azure secret: unknown CHAIN source '{raw.Trim()}' ({string.Join(", ", Sources)})");
            }
            result.Add(part);
        }
        return result;
    }
}
