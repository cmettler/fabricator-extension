-- query_zip(queries [, as_structs := false])
--   The QUERY sibling of table_zip: POSITIONAL JOIN of several SUBQUERIES, left to right — i.e. side
--   by side, pairing row i of each. (Not a UNION: that stacks rows, this adds columns.)
--
--   queries — either
--       · a LIST of SQL strings          → aliases are p1, p2, … by list order, columns unchanged; or
--       · a MAP of alias -> SQL string   → aliases are YOUR keys, and in FLAT mode each query's
--                                          columns are PREFIXED with `<alias>_`.
--       ⚠ The two forms are told apart PER ITEM, not by inspecting the argument: a MAP iterates as
--         pairs (x[0]/x[1]) while a list item has neither — MEASURED, `{% if x[1] %}` separates them
--         cleanly. So there is no type probe to get wrong on an empty or one-element input.
--       ⚠ Neither form is deduplicated. Two identical queries are two positions, and dropping one
--         would silently renumber every p-name after it.
--
--   as_structs := false (default) — flat output, one column per source column.
--       · LIST form: names are left alone. Collisions are fine — fluid_replacement_query's output is
--         bound as a SUBQUERY and DuckDB uniquifies a subquery's names itself (id, id_1, id_2 …).
--       · MAP form: names are prefixed, so a collision becomes `sales_id` / `returns_id` rather than
--         `id` / `id_1` — meaningfully named instead of merely distinct.
--         ⚠⚠ THE PREFIX IS APPLIED INSIDE EACH SUBQUERY, WHICH IS WHY NO INTROSPECTION IS NEEDED:
--           `SELECT COLUMNS('(.*)') AS "<alias>_\1" FROM (<query>)`. Unqualified COLUMNS applies to
--           that subquery's own columns. ⚠ The table-qualified form does NOT exist — `p1.COLUMNS(...)`
--           is parsed as a scalar function and fails with `Scalar Function with name columns does not
--           exist!`, so the prefix cannot be applied from the outer SELECT.
--
--   as_structs := true — ONE column per query, each a STRUCT of that query's columns, named by the
--       alias (the MAP key, or pN for a list).
--       ⚠ NOT prefixed inside the struct: the struct column already carries the alias, so `sales_id`
--         within a struct called `sales` would say it twice.
--
--   ⚠ THE QUERIES ARE YOURS AND ARE SPLICED AS WRITTEN. Nothing here parses or validates them; a
--     syntax error surfaces when the generated statement is bound, naming your text. The ALIASES are
--     not spliced raw: as a subquery alias they go through `sql_ident`, and inside the prefix they are
--     escaped by hand (see the note at that line, which explains why `sql_ident` cannot be used there).
--
--   ⚠ No catalog is read and no SQL runs at render time. A query says for itself what it reads.
--
--   ⚠ EVERY TAG OPENS `{%-`, which is what makes the indentation free: `{%-` strips the preceding
--     newline AND the indent. Keep the dash when you add a tag.
--
--   ⚠⚠ THE REFUSAL IS `SELECT 1 AS zip_error WHERE error(...)`, NOT `SELECT error(...)`. DuckDB
--     PRUNES a projected column nobody reads, so in the SELECT list the error is SWALLOWED by
--     `SELECT count(*) FROM query_zip([])`, which then answers 1. A filter must be evaluated.
--     See table_zip.sql for the measurement.
CREATE OR REPLACE MACRO query_zip(queries, as_structs := false) AS TABLE
SELECT * FROM fluid_replacement_query($$
{%- assign qs = params.queries -%}
{%- if qs.size == 0 -%}
    SELECT 1 AS zip_error WHERE error('query_zip: no queries given')
{%- else -%}
    SELECT
    {%- if params.as_structs -%}
        {%- for x in qs %}{% unless forloop.first %},{% endunless -%}
            {%- if x[1] %}{{ ' ' }}{{ x[0] | sql_ident }}
            {%- else %}{{ ' ' }}"p{{ forloop.index }}"
            {%- endif -%}
        {%- endfor %}
    {%- else %} *
    {%- endif %} FROM
    {%- for x in qs %}{% unless forloop.first %} POSITIONAL JOIN{% endunless -%}
        {%- if x[1] -%}
            {%- comment -%}
              MAP: the alias is the key. Prefix the columns unless we are building structs.
              ⚠⚠ TWO LIQUID ESCAPING RULES ARE LOAD-BEARING IN THE PREFIX LINE BELOW, and each one is
                a PARSE error rather than a wrong result — so a change here fails loudly, but the fix
                is not obvious:
                · Liquid does NOT escape a quote by doubling it the way SQL does, so the COLUMNS
                  fragment must be a DOUBLE-quoted Liquid string. `' … COLUMNS(''(.*)'') … '` ends the
                  string early.
                · `` may NOT appear inside ANY Liquid string — the backslash is an escape and ``
                  is not a valid one. It is therefore plain TEMPLATE TEXT here, which is also why the
                  alias is escaped with `replace` rather than `sql_ident`: sql_ident would wrap it in
                  its own quotes, and the prefix needs the alias INSIDE one pair with `_` appended.
                  ⚠ `{% raw %}` does NOT solve this — MEASURED: inside raw the alias is not
                  interpolated either, so the fragment loses the very thing it is there to insert.
            {%- endcomment -%}
            {%- if params.as_structs %}
                {{- ' (' }}{{ x[1] }}) AS {{ x[0] | sql_ident }}
            {%- else %}
                {{- " (SELECT COLUMNS('(.*)') AS " }}"{{ x[0] | replace: '"', '""' }}_\1"
                {{- " FROM (" }}{{ x[1] }})) AS {{ x[0] | sql_ident }}
            {%- endif -%}
        {%- else %}
            {{- ' (' }}{{ x }}) AS "p{{ forloop.index }}"
        {%- endif -%}
    {%- endfor -%}
{%- endif -%}
$$, params := {'queries': queries, 'as_structs': as_structs});
