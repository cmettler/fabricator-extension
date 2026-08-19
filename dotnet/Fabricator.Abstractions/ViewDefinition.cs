namespace Fabricator.Bridge;

/// <summary>
/// A DuckDB <b>VIEW</b> a provider ships bound to an ATTACHed CATALOG's schema, declared as one complete
/// <c>CREATE VIEW</c> statement. It resolves as an ordinary RELATION — <c>db.schema.v</c> — so it appears in
/// <c>duckdb_views()</c> / <c>information_schema.views</c>, tools that enumerate a catalog find it, and it can
/// be used anywhere a table can.
///
/// <para><b>Why this exists beside <see cref="CatalogMacroDefinition"/>, and it is not symmetry.</b> DuckDB
/// binds a view's body against the VIEW's OWN catalog and schema — its binder re-points the search path
/// before binding — so an UNQUALIFIED table reference inside <see cref="CreateSql"/> resolves against the
/// catalog this view belongs to. A MACRO body has no such anchor: it is expanded in the CALLER's context, so
/// the same unqualified reference silently resolves against whatever catalog the caller happens to be in.
/// That is why <see cref="CatalogMacroDefinition"/> tells you to reach for <c>ISqlTableFunction</c> when a
/// body must name its own catalog's tables, and why a view does not need the ATTACH alias threaded in at
/// all.</para>
///
/// <para><b>What a body may reference.</b> Everything in its own catalog: provider tables, catalog-bound
/// macros, discovered and custom scalar functions, TVFs — all of them resolve through the same retriever the
/// view binder re-points. Keep the body UNQUALIFIED; qualifying ANOTHER catalog re-introduces exactly the
/// problem this form avoids, because that catalog's alias is chosen at ATTACH time.</para>
///
/// <para><b>Declaration order does not matter.</b> The host only PARSES the statement when the view is first
/// looked up; nothing is bound until the view is USED. So a view may reference a table, macro or function
/// declared later, discovered later, or not present at all — a missing reference surfaces at first use as an
/// ordinary binder error naming it, and enumerating views never forces any of it to be resolved.</para>
///
/// <para>See docs/macros-and-sqlgen-functions.md §5.</para>
/// </summary>
/// <param name="SchemaName">
/// The schema within the attached catalog to bind into (e.g. <c>"dbo"</c>, <c>"main"</c>). A view whose schema
/// is not among those the catalog discovered is skipped — the same rule macros and discovered functions
/// follow, so an ATTACH <c>schema_filter</c> gates views too.
/// </param>
/// <param name="Name">
/// The view's name. Must match the name written in <see cref="CreateSql"/> (case-insensitively); a mismatch is
/// skipped with a warning, since two disagreeing names means one of them is unreachable.
///
/// <para>⚠ It must ALSO not collide with a discovered TABLE in the same schema. A view and a table share ONE
/// catalog lookup, so a collision would have to be resolved by silently preferring one — and either choice
/// hands somebody the object they did not ask for. The host refuses the NAME instead, with an error naming
/// both sides; every other view and table in the catalog keeps working.</para>
/// </param>
/// <param name="CreateSql">
/// The complete statement, written UNQUALIFIED — the host overwrites the catalog/schema with this catalog's
/// ATTACH alias and <see cref="SchemaName"/>, which is what anchors the body's search path here. A statement
/// that does not parse as a single <c>CREATE VIEW</c> is skipped with a warning (a broken declaration never
/// blocks an ATTACH).
/// </param>
public sealed record ViewDefinition(string SchemaName, string Name, string CreateSql);
