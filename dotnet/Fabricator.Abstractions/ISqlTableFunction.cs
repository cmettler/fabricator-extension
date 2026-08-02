using Apache.Arrow;

namespace Fabricator.Bridge;

/// <summary>
/// A table function implemented as a <b>SQL REWRITE</b> rather than a marshaled data source. At BIND time
/// DuckDB hands the function's constant arguments to <see cref="GenerateSql"/>; the returned statement is
/// parsed and SUBSTITUTED for the call in the plan (DuckDB's own <c>bind_replace</c> mechanism — the one
/// behind <c>query_table()</c>). Consequences:
///
/// <list type="bullet">
/// <item><b>No data crosses the bridge at execution.</b> What runs is a native DuckDB plan over whatever the
/// generated SQL references — including this extension's own catalog scans, which then get their full
/// pushdown (projection/filter/TopN into the provider, Delta file + row-group pruning), parallelism and
/// join reordering. Nothing streams through C#.</item>
/// <item><b>The output schema is free.</b> It falls out of binding the generated SQL, so — unlike
/// <see cref="ITableFunction"/> — there is no <c>OutputSchema</c> to declare, and it may differ per call.</item>
/// </list>
///
/// <para>Use this when the SQL <b>TEXT</b> must depend on the arguments — object names, an IN-list or UNION
/// fan-out, a metadata lookup at bind time. When only VALUES vary and the statement's shape is fixed, ship a
/// <see cref="MacroDefinition"/> instead (cheaper, and injection-free by construction).</para>
///
/// <para><b>Authoring contract.</b> <see cref="GenerateSql"/> must be deterministic for a given argument set
/// and free of side effects: binds happen WITHOUT execution (<c>EXPLAIN</c>, <c>DESCRIBE</c>, creating a view)
/// and REPEAT (every re-bind of a view or prepared statement regenerates). It must also quote what it splices —
/// see <see cref="DuckSql"/>. Exactly one SELECT statement may be returned.</para>
///
/// See docs/macros-and-sqlgen-functions.md §2.
/// </summary>
public interface ISqlTableFunction
{
    /// <summary>Function name. Global: the bare name; catalog-bound: see <see cref="ICatalogSqlTableFunction"/>.</summary>
    string Name { get; }

    /// <summary>
    /// The call signature: ONE schema whose fields are the parameters in declared order, each carrying its
    /// style (positional by default, or <see cref="Params.Named"/>) in metadata. A <c>NullType</c>-typed
    /// field is the ANY sentinel: DuckDB passes the value UNCAST and its runtime type is preserved (the
    /// convention shared with the table/in-out bind paths — e.g. an "accept a STRUCT or a JSON string"
    /// parameter). Only SUPPLIED named parameters reach <see cref="GenerateSql"/>.
    /// </summary>
    Schema Parameters { get; }

    /// <summary>
    /// Generates the replacement SQL for one call. <paramref name="args"/> is a single (1-row) batch: the
    /// positional arguments first (in <see cref="Parameters"/> order), then the SUPPLIED named parameters —
    /// each identified BY FIELD NAME, so read named values by name rather than by index. A NULL argument
    /// arrives as a null-valued column (the host rejects NULLs up front unless the parameter is nullable).
    /// Must return EXACTLY ONE <c>SELECT</c> statement.
    /// </summary>
    string GenerateSql(RecordBatch args);
}

/// <summary>
/// What a CATALOG-BOUND generator is given beyond its arguments: the catalog's DuckDB ATTACH alias (which only
/// the host knows) and the live provider catalog, so the generator may LOOK THINGS UP at bind time — e.g. list
/// the tables matching a pattern via <see cref="IBackendCatalog.ExecuteQuery"/> — and then emit SQL that names
/// them. Read-only use please: a generator must stay deterministic and side-effect-free (binds repeat).
/// </summary>
/// <param name="CatalogName">The DuckDB ATTACH alias (e.g. <c>db</c>) — quote it with
/// <see cref="DuckSql.QuoteName"/> when emitting references back into this catalog.</param>
/// <param name="Catalog">The live provider catalog for bind-time lookups (its connection is the catalog's own).</param>
public sealed record SqlGenContext(string CatalogName, IBackendCatalog Catalog);

/// <summary>
/// A catalog-bound SQL-generating table function (attach-time scope) — resolved as
/// <c>SELECT * FROM db.SchemaName.Name(args)</c>. Unlike the global form, the generator gets a
/// <see cref="SqlGenContext"/>: the catalog's ATTACH ALIAS (so it can emit fully-qualified references back into
/// its own catalog — e.g. <c>FROM "db"."dbo"."sales_2024"</c> — and let those scans keep their pushdown) plus
/// the provider catalog for bind-time lookups. See docs/macros-and-sqlgen-functions.md §2.
/// </summary>
public interface ICatalogSqlTableFunction
{
    /// <summary>Target catalog schema (e.g. "dbo"); created on attach if it isn't already present.</summary>
    string SchemaName { get; }

    /// <summary>Function name, resolved as <c>db.SchemaName.Name(args)</c>.</summary>
    string Name { get; }

    /// <summary>The call signature — see <see cref="ISqlTableFunction.Parameters"/>.</summary>
    Schema Parameters { get; }

    /// <summary>
    /// Generates the replacement SQL for one call. <paramref name="ctx"/> carries the catalog's ATTACH alias and
    /// the provider catalog (for bind-time lookups); <paramref name="args"/> follows
    /// <see cref="ISqlTableFunction.GenerateSql"/>'s shape. Must return exactly one SELECT statement,
    /// deterministically and without side effects.
    /// </summary>
    string GenerateSql(SqlGenContext ctx, RecordBatch args);
}
