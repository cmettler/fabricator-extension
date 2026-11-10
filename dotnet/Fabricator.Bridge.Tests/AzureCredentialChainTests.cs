// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using Fabricator.Bridge;
using Xunit;

namespace Fabricator.Bridge.Tests;

/// <summary>
/// <see cref="AzureCredentialChain.Parse"/>: the CHAIN of a DuckDB azure secret. Without it a credential_chain secret
/// is DefaultAzureCredential, which can answer from a source whose token lacks the Fabric scopes (403
/// InsufficientScopes on the lakehouse listing, measured 2026-11-10, while `az login` worked).
/// </summary>
public class AzureCredentialChainTests
{
    [Theory]
    [InlineData("cli", new[] { "cli" })]
    [InlineData("cli;env", new[] { "cli", "env" })]
    [InlineData(" CLI ; Managed_Identity ;", new[] { "cli", "managed_identity" })]
    [InlineData("workload_identity;default", new[] { "workload_identity", "default" })]
    public void Parse_keeps_the_order(string chain, string[] expected) =>
        Assert.Equal(expected, AzureCredentialChain.Parse(chain));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ;  ")]
    public void Parse_of_nothing_is_empty(string? chain) =>
        Assert.Empty(AzureCredentialChain.Parse(chain));

    [Fact]
    public void Parse_refuses_an_unknown_source_by_name()
    {
        // a typo must not fall back to another source: the whole point of naming one
        var e = Assert.Throws<ArgumentException>(() => AzureCredentialChain.Parse("cli;azcli"));
        Assert.Contains("'azcli'", e.Message);
        Assert.Contains("managed_identity", e.Message);
    }
}
