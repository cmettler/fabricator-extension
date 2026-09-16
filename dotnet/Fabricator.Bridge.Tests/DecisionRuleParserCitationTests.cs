// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Fabricator.Bridge.Tests;

/// <summary>
/// Every <c>// TEST    :</c> citation in <c>DecisionRuleParser.cs</c> names a test that exists.
/// </summary>
/// <remarks>
/// <para>
/// <b>⚠⚠ THIS EXISTS BECAUSE THE CITATIONS ARE LOAD-BEARING DOCUMENTATION, AND A STALE ONE IS WORSE THAN
/// NONE.</b> Each branch of the cell parser carries a CONTEXT / PARSES / SAMPLE / TEST block so that the
/// facts about a fragment are one grep away — which only works while the named test still exists. Renaming
/// or deleting a test is otherwise completely silent: nothing compiles against a comment.
/// </para>
/// <para>
/// ⚠ It FAILS rather than skips when the source file cannot be found. A "cannot check" that passes is the
/// vacuous-pass shape this repo keeps recording; tier 0 always runs from a checkout, so absence is a real
/// problem rather than an environment quirk.
/// </para>
/// <para>
/// ⚠ The parser's path is derived from THIS FILE's compile-time path, so it needs no working-directory
/// assumption — the compile and the run happen in one checkout in every tier that runs this.
/// </para>
/// </remarks>
public class DecisionRuleParserCitationTests
{
    [Fact]
    public void Every_cited_test_exists()
    {
        var source = File.ReadAllLines(ParserSourcePath());
        var cited = CitedTestNames(source);

        // ⚠ A LOWER BOUND, so the extraction itself cannot rot into finding nothing and passing. The
        // parser has a dozen branches and most cite more than one test.
        Assert.True(cited.Count >= 15,
            $"only {cited.Count} citations found — the `// TEST    :` convention has drifted.");

        var have = typeof(DecisionRuleParserTests)
            .GetMethods()
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = cited.Where(n => !have.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            "DecisionRuleParser.cs cites tests that no longer exist: " + string.Join(", ", missing));
    }

    /// <summary>Names from a <c>// TEST    :</c> line and the bare-identifier comment lines under it.</summary>
    private static HashSet<string> CitedTestNames(IReadOnlyList<string> source)
    {
        var continuation = new Regex(@"^//\s*[A-Za-z_][A-Za-z_0-9]*\s*,?\s*(\(.*\))?$");
        var identifier = new Regex(@"[A-Za-z_][A-Za-z_0-9]{6,}");
        var cited = new HashSet<string>(StringComparer.Ordinal);
        bool collecting = false;

        foreach (var raw in source)
        {
            var line = raw.Trim();
            string body;
            if (line.StartsWith("// TEST    :", StringComparison.Ordinal))
            {
                collecting = true;
                body = line["// TEST    :".Length..];
            }
            else if (collecting && continuation.IsMatch(line))
            {
                body = line[2..];
            }
            else
            {
                collecting = false;
                continue;
            }

            foreach (Match m in identifier.Matches(body))
            {
                cited.Add(m.Value);
            }
        }
        return cited;
    }

    private static string ParserSourcePath([CallerFilePath] string here = "")
    {
        var testDir = Path.GetDirectoryName(here)!;
        var path = Path.GetFullPath(
            Path.Combine(testDir, "..", "Fabricator.Functions", "DecisionRuleParser.cs"));
        Assert.True(File.Exists(path), $"the parser source was not found at {path}");
        return path;
    }
}
