-- table_zip(patterns [, similar_to := true] [, as_structs := false]
--                 [, rowid_begin := NULL::BIGINT] [, rowid_end := NULL::BIGINT])
--   POSITIONAL JOIN of several relations, left to right.
--
--   similar_to := true  (default) — `patterns` are DuckDB `SIMILAR TO` patterns, i.e. FULL-match
--       regexes (`_` and `%` are LITERAL; use `.*foo.*` for a substring match). Tables AND views are
--       matched, system objects excluded. Ordered by position in the list, then by name within a
--       pattern; a relation matching several patterns appears ONCE at its earliest position.
--       ⚠ The catalog is read on the render's OWN connection, so the caller's TEMP relations are
--       invisible.
--
--   similar_to := false — `patterns` are relation names used AS IS, rendered verbatim into the FROM
--       clause (so `db.schema.t` and pre-quoted `"odd name"` both work). No catalog lookup at all.
--       Duplicates removed, order kept.
--
--   as_structs := false (default) — flat output, one column per source column. Duplicate column names
--       need no handling: fluid_replacement_query's output is bound as a SUBQUERY and DuckDB
--       uniquifies a subquery's names itself (id, id_1, id_2 …).
--
--   as_structs := true — ONE column per relation, each a STRUCT of that relation's columns, named
--       after the relation.
--
--   rowid_begin / rowid_end — restrict every relation to a RANGE of DuckDB `rowid`s. Independent
--       and both optional: begin only, end only, both, or neither.
--       ⚠⚠ INCLUSIVE, i.e. BETWEEN semantics: `rowid >= begin AND rowid <= end`. Both ends are IN the
--         range, so `rowid_begin := 3, rowid_end := 6` yields rowids 3,4,5,6.
--         ⚠ THE COST, so nobody rediscovers it: adjacent ranges do NOT compose. [0,1000] and
--         [1000,2000] both contain rowid 1000, so chunking must step the next begin past the previous
--         end (1001), not reuse it. Change both `<=` to `<` for half-open instead.
--       ⚠⚠ THE DEFAULTS ARE `NULL::BIGINT`, NOT `NULL`, AND THE CASTS ARE LOAD-BEARING. An untyped
--         NULL from a DuckDB macro default reaches the params bag as a null-TYPED Arrow child and the
--         call fails with `Length must equal null count (Parameter 'data')` — a message about Arrow
--         that names nothing you wrote. MEASURED on the earlier set-based form: bare NULL fails, a
--         cast NULL works, a real value works.
--       ⚠ Each bound is cast before it reaches the generated SQL (`$rb::BIGINT` on the catalog branch,
--         `| sql` on the as-is branch), so a non-numeric argument fails as a cast or arrives quoted
--         rather than being spliced into the statement as text.
--       ⚠⚠ THE FILTER GOES ON EVERY RELATION, NOT ONE. POSITIONAL JOIN has no join condition, so there
--         is nothing for a predicate to propagate through — MEASURED on the set form: filtering only
--         the first relation of three returned 5,000,000 rows where filtering all three returned
--         5,000.
--       ⚠ IT ASSUMES THE RELATIONS ARE ROW-ALIGNED, which is positional join's own premise. A
--         CONTIGUOUS range is much safer here than an arbitrary set: if the relations were built
--         together, [b, e) selects the same POSITIONS in each.
--       ⚠ A VIEW is allowed: it filters iff it EXPOSES a `rowid` column (`CREATE VIEW v AS SELECT
--         rowid AS rowid, * FROM t`) — MEASURED working. One that does not fails in DuckDB's own
--         words, which name the column AND the candidates: `Referenced column "rowid" not found in
--         FROM clause! Candidate bindings: "a"`. No check of ours would say it better.
--       ⚠⚠ ALIGNMENT IS STRUCTURAL, NOT LUCK, and the mechanism is worth knowing before anyone
--         "optimises" it: PhysicalPositionalJoin does NOT override ParallelSink(), which defaults to
--         FALSE (physical_operator.hpp), and Pipeline::ScheduleParallel bails on that into
--         ScheduleSequentialTask — so the pipeline feeding it is SINGLE-THREADED and chunks reach its
--         ColumnDataCollection in scan order. That is why `preserve_insertion_order=false` changes
--         nothing: measured 5/5 clean at 428k rows on 8 threads with the setting verified off.
--         ⚠ It is a property of the PINNED DuckDB. If PhysicalPositionalJoin ever declares
--         ParallelSink() = true, alignment breaks SILENTLY and this option needs re-deriving.
--
--   Every relation is aliased t1, t2, … so nothing depends on implicit aliasing, which a verbatim
--   `db.schema.t` or `"odd name"` would not give reliably.
--
--   ⚠ THE QUOTING HAPPENS TWICE, IN TWO LANGUAGES, AND THAT IS NOT DUPLICATION:
--       · the similar_to branch quotes CATALOG ROWS inside DuckDB, so it needs a SQL macro — a Liquid
--         macro cannot be called from SQL executing in the database;
--       · the as-is branch quotes LIQUID VALUES at render time, so it uses the plugin's own filters,
--         `unquote_ident` then `sql_ident`, which normalise a name however the caller wrote it.
--         ⚠ NOT `| json`: it escapes as "od"d", a DIFFERENT identifier, and it looks correct for
--         every name that happens to contain no quote.
--
--   ⚠⚠ EVERY REFUSAL IS `SELECT 1 AS zip_error WHERE error(...)`, NOT `SELECT error(...)`, AND THE
--     WHERE IS LOAD-BEARING. DuckDB PRUNES a projected column nobody reads, so with the error in the
--     SELECT list a typo'd pattern answers `SELECT count(*) FROM table_zip(['typo'])` with **1** --
--     the one row of the degenerate statement -- instead of raising. MEASURED both ways:
--     `SELECT count(*) FROM (SELECT error('boom'))` returns 1, `... FROM (SELECT 1 AS x WHERE
--     error('boom'))` raises. A filter must be evaluated, so the WHERE form cannot be pruned away.
--     A silent 1 on a typo is the exact silent-wrong-answer class the refusal exists to prevent.
--
--   ⚠ The catalog query reaches the database ONLY through BOUND parameters ($pats, $rids) — nothing is
--     interpolated into it. Keep it that way: a value spliced in as text would be an injection vector,
--     and qi() is for IDENTIFIERS, not for values.
--
--   ⚠ EVERY TAG OPENS `{%-`. That is what makes the indentation below free: `{%-` strips the preceding
--     newline AND the indent, so nesting the branches costs the rendered SQL nothing. Keep the dash
--     when you add a tag, or the indent starts appearing in the generated statement.
CREATE OR REPLACE MACRO table_zip(patterns, similar_to := true, as_structs := false,
                                        rowid_begin := NULL::BIGINT,
                                        rowid_end := NULL::BIGINT) AS TABLE
SELECT * FROM fluid_replacement_query($$
{%- if params.similar_to -%}

    {%- comment -%}
      DuckDB has no quote_ident. A TEMP macro on the render's OWN pinned connection: {% exec %} and
      {% query %} share it, so the helper is in scope below — and nothing lands in the caller's catalog.
    {%- endcomment -%}
    {%- exec -%}
        CREATE OR REPLACE TEMP MACRO qi(s) AS '"' || replace(s, '"', '""') || '"'
    {%- endexec -%}

    {%- query plan pats: params.pats, rb: params.rowid_begin, re: params.rowid_end -%}
        WITH rel AS (                            -- tables AND views, as one namespace
          SELECT database_name, schema_name, table_name AS name
          FROM duckdb_tables() WHERE internal = false
          UNION ALL
          SELECT database_name, schema_name, view_name AS name
          FROM duckdb_views() WHERE internal = false
        ),
        p AS (                                   -- the patterns, with their position in the list
          SELECT i AS ord, $pats[i] AS pat FROM range(1, len($pats) + 1) r(i)
        ),
        m0 AS (                                  -- every (pattern, relation) match
          SELECT rel.database_name, rel.schema_name, rel.name, p.ord,
                 row_number() OVER (PARTITION BY rel.name
                                    ORDER BY p.ord, rel.database_name, rel.schema_name) AS k
          FROM p JOIN rel ON rel.name SIMILAR TO p.pat
        ),
        m AS (SELECT * EXCLUDE (k) FROM m0 WHERE k = 1),        -- dedupe: earliest position wins
        o AS (SELECT *, row_number() OVER (ORDER BY ord, name) AS pos FROM m),
        w AS (                                   -- the rowid predicate, or NULL when unbounded
          SELECT nullif(concat_ws(' AND ',
                     CASE WHEN $rb IS NOT NULL THEN 'rowid >= ' || $rb::BIGINT::VARCHAR END,
                     CASE WHEN $re IS NOT NULL THEN 'rowid <= ' || $re::BIGINT::VARCHAR END), '') AS pred
        ),
        q AS (                                   -- each relation, restricted if a bound was given
          SELECT o.pos, o.name,
                 CASE WHEN w.pred IS NULL
                      THEN qi(o.database_name) || '.' || qi(o.schema_name) || '.' || qi(o.name)
                      ELSE '(SELECT * FROM ' || qi(o.database_name) || '.' || qi(o.schema_name) || '.'
                           || qi(o.name) || ' WHERE ' || w.pred || ')'
                 END AS src
          FROM o, w
        )
        SELECT
          string_agg(src || ' AS ' || qi('t' || pos), ' POSITIONAL JOIN ' ORDER BY pos) AS frm,
          string_agg(qi('t' || pos) || ' AS ' || qi(name), ', ' ORDER BY pos) AS sel
        FROM q
    {%- endquery -%}

    {%- assign r = plan[0] -%}
    {%- if r.frm == blank -%}
        SELECT 1 AS zip_error WHERE error('table_zip: no table or view matched any of '
                     || {{ params.pats | join: ', ' | sql }})
    {%- else -%}
        SELECT
        {%- if params.as_structs %} {{ r.sel }}
        {%- else %} *
        {%- endif %} FROM {{ r.frm }}
    {%- endif -%}

{%- else -%}

    {%- comment -%}
      The struct LABEL is the relation's own name, so a name passed pre-quoted (`"odd name"`) goes
      through `unquote_ident` first. ⚠ Only a WHOLE-string quoted identifier is unquoted: `"a"."b"` is
      two parts and is left alone, and a qualified `db.schema.t` keeps its full name, because taking
      "the last part" needs a real identifier parser (a dot can live inside quotes).
      ⚠ The FROM clause always renders verbatim — that is what `as is` means, and it is why a name
      needing quotes must be passed pre-quoted. With a bound set, a relation exposing no `rowid`
      fails in DuckDB's own words, which name the column and the candidate bindings.
    {%- endcomment -%}

    {%- comment -%}
      The predicate is built ONCE, before the loop, rather than per relation. ⚠ Each bound goes through
      `| sql`, so a non-numeric argument is QUOTED rather than spliced into the statement as text — the
      catalog branch gets the same protection from `$rb::BIGINT`.
    {%- endcomment -%}
    {%- assign pred = '' -%}
    {%- if params.rowid_begin != nil -%}
        {%- capture pred -%}rowid >= {{ params.rowid_begin | sql }}{%- endcapture -%}
    {%- endif -%}
    {%- if params.rowid_end != nil -%}
        {%- if pred != '' -%}
            {%- capture pred -%}{{ pred }} AND rowid <= {{ params.rowid_end | sql }}{%- endcapture -%}
        {%- else -%}
            {%- capture pred -%}rowid <= {{ params.rowid_end | sql }}{%- endcapture -%}
        {%- endif -%}
    {%- endif -%}
    {%- assign bounded = pred != '' -%}

    {%- assign names = params.pats | uniq -%}
    {%- if names.size == 0 -%}
        SELECT 1 AS zip_error WHERE error('table_zip: no relation names given')
    {%- else -%}
        SELECT
        {%- if params.as_structs -%}
            {%- for n in names %}{% unless forloop.first %},{% endunless %}
                {{- ' ' }}"t{{ forloop.index }}" AS {{ n | unquote_ident | sql_ident }}
            {%- endfor %}
        {%- else %} *
        {%- endif %} FROM
        {%- for n in names %}{% unless forloop.first %} POSITIONAL JOIN{% endunless %}
            {%- if bounded %}
                {{- ' (SELECT * FROM ' }}{{ n }} WHERE {{ pred }})
            {%- else %}
                {{- ' ' }}{{ n }}
            {%- endif %} AS "t{{ forloop.index }}"
        {%- endfor -%}
    {%- endif -%}

{%- endif -%}
$$, params := {'pats': patterns, 'similar_to': similar_to, 'as_structs': as_structs,
               'rowid_begin': rowid_begin, 'rowid_end': rowid_end});
