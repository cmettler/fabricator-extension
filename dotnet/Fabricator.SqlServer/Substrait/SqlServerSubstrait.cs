// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Drivers.SqlServer.Substrait;

/// <summary>
/// Translates Substrait plans to T-SQL, and publishes the driver's Substrait
/// dialect: a YAML document in the Substrait dialect format
/// (<c>text/dialect_schema.yaml</c> in the Substrait repository) listing the
/// types, relations, expressions and functions, with their implementations
/// and option behaviors, that the translation supports.
/// </summary>
/// <remarks>
/// A statement given a plan through <c>SubstraitPlan</c> runs its translation.
/// Constructs that can't be translated with the same semantics are rejected
/// with <see cref="SubstraitTranslationException"/> rather than approximated,
/// with three exceptions that are accepted because they affect only some
/// values of common operations (string and binary comparison), and which the
/// dialect records under <c>metadata.semantics</c>:
/// <list type="bullet">
/// <item>Strings are UTF-16: a character outside the Basic Multilingual Plane
/// counts as two in lengths and positions, and sorts below U+E000.</item>
/// <item>Comparisons ignore trailing spaces: <c>'a' = 'a '</c>, and the two
/// group together.</item>
/// <item>Binary comparisons ignore trailing zero bytes: <c>0x01 = 0x0100</c>.</item>
/// </list>
/// </remarks>
public static class SqlServerSubstrait
{
    /// <summary>
    /// The collation string values get by default
    /// (<see cref="SqlServerParameters.SubstraitCollation"/>): binary, so that
    /// comparisons, sorting and grouping are case-sensitive and by UTF-16 code
    /// unit, except that trailing spaces are ignored (see the remarks above).
    /// </summary>
    public const string DefaultCollation = TSqlEmitter.BinaryCollation;

    /// <summary>The Substrait release whose protobuf messages and extensions the translation follows.</summary>
    public const string SubstraitVersion = "0.107.0";

    // [FABRICATOR-PATCH: OURS-BY-DESIGN] the dialect YAML resource is not vendored (only the translation is)

    /// <summary>
    /// Translates a Substrait plan (a serialized <c>substrait.Plan</c>) to a
    /// T-SQL query.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="collation">
    /// The collation for string values; null or empty to use the database's
    /// collations instead (see <see cref="SqlServerParameters.SubstraitCollation"/>).
    /// </param>
    public static string ToSql(byte[] plan, string? collation = DefaultCollation) => Translate(plan, collation, nested: false);

    /// <param name="nested">
    /// The query will be used as a derived table (partitioned reads), where
    /// T-SQL allows no final ORDER BY.
    /// </param>
    /// <param name="tableName">Writes a named table's name in place of the default (tests read temporary tables with it).</param>
    internal static string Translate(byte[] plan, string? collation, bool nested, Func<IReadOnlyList<string>, string>? tableName = null)
    {
        if (plan == null || plan.Length == 0)
        {
            throw new SubstraitTranslationException("The Substrait plan is empty.");
        }
        var decoder = new SubstraitPlanDecoder(plan);
        RootRel root = decoder.DecodePlan();
        var emitter = new TSqlEmitter(decoder.PerRecordEvaluation, string.IsNullOrEmpty(collation) ? null : CheckCollation(collation!), tableName);
        return emitter.EmitRoot(root, nested, out _);
    }

    /// <summary>
    /// [FABRICATOR-PATCH: OURS-BY-DESIGN] Translates a plan for the remote pushdown (see
    /// <see cref="TSqlEmitter.EmitPushdown"/>).
    /// </summary>
    internal static string TranslatePushdown(byte[] plan, bool ignoreNulls, out int columnCount, out List<bool> orderDescending,
                                             Func<IReadOnlyList<string>, IReadOnlyDictionary<string, string>?>? columnSqlTypes = null)
        => TranslatePushdown(plan, ignoreNulls, out columnCount, out orderDescending, out _, columnSqlTypes);

    /// <param name="write">For a plan that writes (INSERT … SELECT, DELETE, UPDATE), the operation, the target table
    /// and the columns it writes; the query is then a statement with no result, and <paramref name="columnCount"/>
    /// is 1 — the affected-row count the host reports; Source names the table a moved-rows INSERT deletes from
    /// (<c>DELETE … OUTPUT DELETED.* INTO</c>). Null for a query.</param>
    internal static string TranslatePushdown(byte[] plan, bool ignoreNulls, out int columnCount, out List<bool> orderDescending,
                                             out (int Op, IReadOnlyList<string> Table, IReadOnlyList<string> Columns, IReadOnlyList<string>? Source)? write,
                                             Func<IReadOnlyList<string>, IReadOnlyDictionary<string, string>?>? columnSqlTypes = null)
    {
        if (plan == null || plan.Length == 0)
        {
            throw new SubstraitTranslationException("The Substrait plan is empty.");
        }
        var decoder = new SubstraitPlanDecoder(plan);
        RootRel root = decoder.DecodePlan();
        // IGNORE NULLS and DATETRUNC arrived together, with SQL Server 2022
        var emitter = new TSqlEmitter(decoder.PerRecordEvaluation, DefaultCollation, columnSqlTypes: columnSqlTypes) { SupportsIgnoreNulls = ignoreNulls, SupportsDateTrunc = ignoreNulls };
        if (root.Input is WriteRel writeRel)
        {
            string sql = writeRel.Op == SubstraitFields.WriteOp_Insert
                ? emitter.EmitInsert(writeRel)
                : emitter.EmitDeleteOrUpdate(writeRel);
            // a moved-rows INSERT (EmitMovedDelete) also names the table it deletes from
            var source = writeRel.Input is WriteRel { Op: SubstraitFields.WriteOp_Delete } moved ? moved.TableNames : null;
            write = (writeRel.Op, writeRel.TableNames, writeRel.ColumnNames, source);
            columnCount = 1;
            orderDescending = new List<bool>();
            return sql;
        }
        write = null;
        return emitter.EmitPushdown(root, out columnCount, out orderDescending);
    }

    /// <summary>Checks that a collation name is a plain identifier, since it becomes part of the query.</summary>
    internal static string CheckCollation(string collation)
    {
        if (collation.Length > 128 || !collation.All(ch => ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
        {
            throw new SubstraitTranslationException($"'{collation}' is not a collation name."); // [FABRICATOR-PATCH: OURS-BY-DESIGN] no ADBC
        }
        return collation;
    }
}
