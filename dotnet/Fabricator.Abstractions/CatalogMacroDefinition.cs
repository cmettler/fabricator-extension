namespace Fabricator.Bridge;

/// <summary>
/// A DuckDB <b>MACRO</b> a provider ships bound to an ATTACHed CATALOG's schema, declared as one complete
/// <c>CREATE MACRO</c> statement. Resolved as <c>db.schema.m(...)</c> (or <c>FROM db.schema.m(...)</c> for a
/// table macro) — the catalog-scoped counterpart of <see cref="MacroDefinition"/>, which lands in the SYSTEM
/// catalog under a bare name.
///
/// <para>Like the global form, the host does NOT parse the body itself: it hands <see cref="CreateSql"/> to
/// DuckDB's own parser and binds the resulting <c>CreateMacroInfo</c> into this catalog's schema, so the FULL
/// macro grammar works (named-parameter defaults, overload sets, <c>AS TABLE &lt;query&gt;</c>). The body
/// travels over its own metadata kind and is never embedded in provider SQL, so declaring macros costs no
/// server round-trip and works on a provider with no SQL engine at all.</para>
///
/// <para><b>A schema gives NAMESPACING, not resolution scope.</b> DuckDB captures no search path when it
/// expands a macro — the body is bound in the CALLER's context. So an UNQUALIFIED table reference inside
/// <see cref="CreateSql"/> resolves against whatever catalog/schema the caller currently has, NOT against the
/// catalog this macro is bound to, and that is a silently-wrong-table hazard rather than an error. Two
/// consequences worth internalising:</para>
/// <list type="bullet">
/// <item>Use a catalog macro for a self-contained <b>expression or query template</b> — the case where
/// namespacing per catalog is the whole point, and where nothing else in the stack is cheaper (a catalog
/// scalar function marshals every call across the ABI; a macro crosses nothing at runtime).</item>
/// <item>When the body must reference its OWN catalog's tables, prefer a SQL-generating table function
/// (<c>ISqlTableFunction</c>): it is handed the catalog's ATTACH alias, which is the only way to qualify
/// such a reference — the alias is chosen at ATTACH time and a statically-declared body cannot know it.</item>
/// </list>
/// <para>See docs/macros-and-sqlgen-functions.md §1.4.</para>
/// </summary>
/// <param name="SchemaName">
/// The schema within the attached catalog to bind into (e.g. <c>"dbo"</c>, <c>"main"</c>). A macro whose
/// schema is not among those the catalog discovered is skipped — the same rule discovered functions follow, so
/// an ATTACH <c>schema_filter</c> gates macros too.
/// </param>
/// <param name="Name">
/// The macro's name. Must match the name written in <see cref="CreateSql"/> (case-insensitively); a mismatch
/// is skipped with a warning, since the two names disagreeing means one of them is unreachable.
/// </param>
/// <param name="CreateSql">
/// The complete statement, written UNQUALIFIED — the host overwrites the catalog/schema with this catalog's
/// ATTACH alias and <see cref="SchemaName"/>. Note this is the OPPOSITE of the global form, which rejects a
/// qualified body outright. A statement that does not parse as a single <c>CREATE MACRO</c> is skipped with a
/// warning (a broken provider macro never blocks an ATTACH).
/// </param>
public sealed record CatalogMacroDefinition(string SchemaName, string Name, string CreateSql);
