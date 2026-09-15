// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Fabricator.Bridge;

namespace Fabricator.Functions;

/// <summary>
/// A connection-free provider whose only contribution is a set of global DuckDB MACROS, each shipped as a
/// real <c>Macros/*.sql</c> file rather than a C# string literal.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ SQL FILES, NOT STRING LITERALS, and the difference is editability: a macro here is syntax-highlighted,
/// diffable, and runnable on its own by pasting it into a shell. The bodies are long enough (table_zip is
/// well over a hundred lines of Liquid-in-SQL) that a C# literal would make them unreadable and would put
/// quote-escaping between the author and the SQL.
/// </para>
/// <para>
/// ⚠⚠ THE MACROS ARE BUILT ON <c>fluid_replacement_query</c>, so this provider needs Fabricator.FluidPlugin
/// to be loaded — at CALL time, not at load time. DuckDB binds a macro BODY lazily, so registration order
/// is irrelevant and a payload without the Fluid provider registers these happily and fails at first use.
/// </para>
/// </remarks>
public sealed class FunctionsProvider : IProvider
{
    public string Name => "functions";

    /// <summary>
    /// FALSE: this contributes macros and hosts nothing to ATTACH, so the registry refuses
    /// <c>PROVIDER 'functions'</c> by name rather than failing inside a catalog open.
    /// </summary>
    public bool HostsCatalog => false;

    /// <inheritdoc cref="LoadMacros"/>
    internal static readonly IReadOnlyList<MacroDefinition> Declared = LoadMacros();

    public IEnumerable<MacroDefinition> GlobalMacros => Declared;

    /// <summary>
    /// The decision-table parser and source classifier, callable from SQL — see <see cref="DecisionFunctions"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ Same non-throwing rule as <see cref="LoadMacros"/> and for the same measured reason: the host reads
    /// this inside <c>list_global_functions</c>, where ONE throw drops every global function in the process.
    /// Nothing here can throw — it constructs four objects and returns them — and it must stay that way.
    /// </remarks>
    public IEnumerable<IScalarFunction> GlobalScalarFunctions => DecisionFunctions.All;

    public IProviderCatalog OpenCatalog(string connectionString, string optionsJson) =>
        throw new NotSupportedException(
            "functions: a macro library, not a catalog (global macros only).");

    public string BuildConnectionString(string secretType, IReadOnlyDictionary<string, string> fields,
                                        string baseConnString) =>
        throw new NotSupportedException(
            "functions: a macro library, not a catalog (global macros only).");

    /// <summary>Reads every embedded <c>Macros/*.sql</c> into a <see cref="MacroDefinition"/>.</summary>
    /// <remarks>
    /// <para>
    /// <b>⚠⚠ IT MUST NEVER THROW, AND THAT IS MEASURED RATHER THAN CAUTIOUS.</b> This runs from
    /// <c>GlobalMacros</c>, which the host reads inside <c>list_global_functions</c> — and a single throw
    /// there drops <b>EVERY GLOBAL FUNCTION IN THE PROCESS</b>, not just this provider's. MEASURED with one
    /// macro file deliberately renamed: <c>table_zip</c> and <c>query_zip</c> disappeared <i>and so did
    /// <c>fluid_render</c> / <c>fluid_scalar</c></i>. That is the same trap the v80 and v89 ABI records
    /// describe, reached from a new direction — and it is strictly worse than the quiet failure a throwing
    /// guard was meant to prevent, because the blast radius is everything and the cause is named only in a
    /// log. So a bad file is SKIPPED with a WARNING: made visible, not made fatal.
    /// </para>
    /// <para>
    /// ⚠ The delivery path is quiet by contract — <see cref="MacroDefinition"/> says a statement that does
    /// not parse "is skipped with a warning at load" — so a macro that never registered surfaces at a CALL
    /// SITE as <i>"… does not exist"</i>. Three things must all have happened for one to exist: the
    /// <c>EmbeddedResource</c> item matched, <c>publish-managed.ps1</c> put this assembly in the payload
    /// (the glob discovers what was PUBLISHED, so that line IS the declaration), and the body parsed.
    /// <b>What catches all three in CI is <c>test/verify_functions_zip.test</c> §1</b>, which asserts the
    /// registration itself; the warnings below are what NAME the cause once it has.
    /// </para>
    /// <para>
    /// ⚠ The FILE NAME must equal the <c>CREATE MACRO</c> name. The host takes the authoritative name from
    /// parsing the body, so a mismatch is not an error anywhere downstream — it would just register
    /// something other than what the file on disk claims, which is how a rename edits one without the other.
    /// </para>
    /// <para>
    /// ⚠ Ordered by resource name, so registration order is deterministic rather than whatever the manifest
    /// happens to hold.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<MacroDefinition> LoadMacros()
    {
        const string folder = ".Macros.";
        var assembly = typeof(FunctionsProvider).Assembly;
        var macros = new List<MacroDefinition>();

        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.Contains(folder, StringComparison.Ordinal) &&
                                 n.EndsWith(".sql", StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            var file = resource[(resource.IndexOf(folder, StringComparison.Ordinal) + folder.Length)..^4];
            string sql;
            try
            {
                using var stream = assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                sql = reader.ReadToEnd().Trim();
            }
            catch (Exception ex)
            {
                Warn($"Macros/{file}.sql could not be read ({ex.GetType().Name}: {ex.Message}); skipped.");
                continue;
            }

            // ⚠ `CREATE OR REPLACE MACRO` as well as the bare form — every shipped file uses the former, and
            // a regex that only knew `CREATE MACRO` would reject all of them.
            var declared = Regex.Match(sql, @"CREATE\s+(?:OR\s+REPLACE\s+)?MACRO\s+([A-Za-z_][A-Za-z0-9_]*)",
                                       RegexOptions.IgnoreCase);
            if (!declared.Success || !string.Equals(declared.Groups[1].Value, file, StringComparison.Ordinal))
            {
                Warn($"Macros/{file}.sql declares CREATE MACRO "
                     + $"'{(declared.Success ? declared.Groups[1].Value : "<none found>")}' — the FILE NAME is "
                     + "the macro name and the two must agree, so this file was SKIPPED and that macro is "
                     + "absent. Rename one to match the other.");
                continue;
            }

            macros.Add(new MacroDefinition(file, sql));
        }

        if (macros.Count == 0)
        {
            Warn("no Macros/*.sql were embedded, so this provider contributes NOTHING. The "
                 + "<EmbeddedResource Include=\"Macros/*.sql\" /> item is missing or matched nothing.");
        }

        return macros;
    }

    /// <summary>Reports a delivery problem through the host's log, never by throwing.</summary>
    /// <remarks>
    /// <para>
    /// MEASURED to reach the sink at this point in startup: with one macro file renamed, the file sink
    /// carries <c>WARN [Fabricator.Functions] functions: Macros/tablezip.sql declares CREATE MACRO
    /// 'table_zip' …</c>, i.e. the host services block IS already published during global-function
    /// registration.
    /// </para>
    /// <para>
    /// ⚠ Resolved through <see cref="FabricatorServices"/> rather than held, and WRAPPED anyway: a throw
    /// escaping here would re-create exactly the blast radius the non-throwing rule exists to avoid. No log
    /// is a worse outcome than a log; it is not a worse outcome than every global function vanishing.
    /// </para>
    /// </remarks>
    private static void Warn(string message)
    {
        try
        {
            FabricatorServices.Get<IHostLog>()?
                .GetLogger("Fabricator.Functions")
                .Log(HostLogLevel.Warning, "functions: " + message);
        }
        catch
        {
            // Deliberately swallowed — see the remark above.
        }
    }
}
