-- decision_render(rules [, name := 'decision_eval'] [, dialect := 'duckdb'] [, source := 'auto']
--                       [, hit_policy := NULL])
--   Renders a DMN-like DECISION TABLE into the SQL TEXT of a DuckDB `TABLE MACRO` that evaluates it.
--   It returns TEXT and creates nothing: run it, read it, then execute it yourself.
--
--     SELECT decision_render('decision_rules', name := 'evaluate_row');
--
--   A decision table is a RELATION: one row per rule, plus three METADATA rows.
--       rulepos = -2  direction    'in' | 'out' | blank (blank = the column is ignored, e.g. Comment)
--       rulepos = -1  datatypes    the macro parameter type, and the output cast
--       rulepos =  0  pre/post     input preprocessing, output postprocessing (optional)
--       rulepos >= 1  the rules    one row each, in evaluation order
--   Each CELL is a small expression language — `> 18`, `EU, US`, `[21 .. 65]`, `not RU`,
--   `between min(1) and max(100)`, `@age*2 > min(1)+1`, `contains(?, 'x')`. See dmn_condition /
--   dmn_value, which are the parser and are callable on their own.
--
--   ⚠⚠ A DECISION TABLE IS CODE, NOT DATA, AND THAT IS THE FEATURE. An expression cell is resolved
--     and passed THROUGH into the generated SQL, so a rule may call any function the target engine
--     has; only a bare non-expression string is quoted as a literal. It follows that the rules
--     relation is a TRUSTED AUTHORING SURFACE, exactly like a Fluid template — never user input.
--
--   `rules` — WHERE THE RULES COME FROM. Four forms, and `source := 'auto'` separates the three a
--     caller reaches for by tests that cannot collide (see dmn_relation, which decides this in C# so
--     every branch is pinned offline):
--       · a relation NAME     `decision_rules`, `db.main.decision_rules`
--       · a FILE              `rules.csv`, `C:/x/rules.parquet`, `s3://b/rules.json`
--                             ⚠ a csv is read with `all_varchar = true` — MEASURED: without it the
--                               sniffer types a column of bare numbers as BIGINT and the cells stop
--                               being text.
--       · JSON TEXT           `[{"rulepos":-2,"region":"in",...}, ...]` — an array of objects, one
--                             per row. DuckDB infers the columns from the array itself.
--                             ⚠ needs the json extension.
--       · `source := 'sql'`   a relation EXPRESSION spliced verbatim — anything that can follow FROM:
--                             a subquery, a VALUES list, `read_xlsx('x.xlsx', ...)`. REQUEST-ONLY,
--                             because no cheap test separates it from a relation name.
--     ⚠ `source :=` OVERRIDES the classifier, but a DOTTED name stays QUALIFIED whichever way it was
--       chosen — `source := 'table'` on `rules.csv` is `"rules"."csv"`, the standard SQL reading. A
--       relation literally NAMED `rules.csv` needs `source := 'sql'` with the name pre-quoted.
--     ⚠ A rules relation held in a TEMP table is INVISIBLE here: `{% query %}` runs on the render's
--       OWN pinned connection, not the caller's. Use a regular table, a file, or JSON.
--
--   `name` — the macro name to render. `dialect` — `duckdb` (default) or `tsql`.
--   `hit_policy` — the DEFAULT baked into the generated macro; NULL (default) means `RuleOrder` when
--     the table is in aggregate mode and `First` otherwise. The generated macro takes `hit_policy` as
--     a parameter either way, so this only chooses what an unqualified call does.
--
--   ⚠⚠ THE WHOLE POINT OF THE SPLIT: the CELL PARSER is C# because it RECURSES, the SQL SHAPE below is
--     Liquid because a template is a shape, and EVERYTHING BETWEEN THEM IS SET-BASED and therefore
--     SQL. The `plan` query returns ONE ROW of finished string fragments — the parameter list, the
--     CASE blocks, the WHERE blocks — built with `string_agg` over every cell at once. That is why
--     this template has no nested loops: Liquid gets FLAT values and substitutes them.
--     ⚠ Keep it that way. Liquid has no dict or array literal, so anything shaped like the Python
--       engine's `inputs[].cases[]` has to be flattened SOMEWHERE — doing it in SQL costs nothing and
--       doing it in Liquid is not expressible.
--
--   ⚠ THREE ROUND TRIPS PER RENDER, and they cannot be fewer: the relation TEXT must exist before a
--     DESCRIBE can name the columns, and the columns must be named before the introspection can be
--     generated. All three are tiny (a decision table is tens of rows), and this is a RENDER — the
--     generated macro reads the rules never again.
--     ⚠ But binds REPEAT, so do not put a decision_render behind a view and expect it to run once.
--
--   ⚠⚠ A DuckDB MACRO PARAMETER SHADOWS A COLUMN OF THE SAME NAME ANYWHERE IN THE BODY, so every
--     reference to an input is rendered QUALIFIED (`preprocessed.age`, never `age`). MEASURED both
--     ways on a two-line macro: with a CTE aliasing `upper(x) AS x`, a bare `x` downstream yields the
--     ARGUMENT and `p.x` yields the preprocessed value.
--     ⚠⚠ WITHOUT THIS, INPUT PREPROCESSING IS SILENTLY INERT: the preprocessed value is computed,
--       never read, and every rule matches against the raw argument. Nothing fails — which is why the
--       first three probes here all PASSED while it was broken. They used value-preserving
--       preprocessing (`floor(40)` is 40, `upper('JP')` is 'JP'), so they could not discriminate.
--       The discriminating shape is preprocessing that CHANGES the value.
--
--   ⚠ EVERY REFUSAL RAISES rather than returning a diagnostic STRING, because the answer here is SQL
--     TEXT: a caller is about to execute it, and text that merely describes a problem would be run.
CREATE OR REPLACE MACRO decision_render(rules, name := 'decision_eval', dialect := 'duckdb',
                                        source := 'auto', hit_policy := NULL::VARCHAR) AS
fluid_render($$
{%- comment -%}
  ⚠⚠ THE DIALECT SELECTS THE CELL VOCABULARY *AND* THE STATEMENT SHAPE, AND ONLY THE FIRST HALF IS
  DIALECT-AWARE TODAY. The parser already renders `IN (a,b)` for T-SQL, but everything below is a DuckDB
  `CREATE OR REPLACE MACRO`. Rendering that under `dialect := 'tsql'` would hand back a statement no SQL
  Server can run while LOOKING like it honoured the argument — so it is refused BY NAME until the second
  template exists. ⚠ An unrecognised dialect is NOT caught here: it falls through to the parser, whose
  message names the valid set.
{%- endcomment -%}
{%- assign _dl = params.dialect | default: 'duckdb' | downcase -%}
{%- if _dl == 'tsql' or _dl == 'mssql' or _dl == 'sqlserver' -%}
    {%- capture _m -%}decision_render: dialect '{{ params.dialect }}' is recognised by the cell parser but its statement template is not built yet — only 'duckdb' can be rendered.{%- endcapture -%}
    {%- query _e m: _m -%}SELECT 1 AS e WHERE error($m){%- endquery -%}
{%- endif -%}

{%- comment -%}
  1/3 — the relation TEXT. dmn_relation classifies the rules argument (name / file / JSON / verbatim)
  in C#, so the decision is pinned by a tier-0 test rather than by rendering something and reading it.
{%- endcomment -%}
{%- query _rel rules: params.rules, src: params.source -%}
    SELECT dmn_relation($rules, $src) AS rel
{%- endquery -%}
{%- assign rel = _rel[0].rel -%}

{%- comment -%}
  2/3 — the COLUMN NAMES. DESCRIBE is usable as a subquery source (EXPLAIN is not — they read alike
  and behave differently). `rulepos` is the one reserved name and is matched case-insensitively,
  because a table authored in Excel says `RulePos`.
{%- endcomment -%}
{%- query _cols -%}
    SELECT column_name AS name
    FROM (DESCRIBE SELECT * FROM {{ rel }})
    WHERE lower(column_name) <> 'rulepos'
{%- endquery -%}

{%- if _cols.size == 0 -%}
    {%- capture _m -%}decision_render: {{ params.rules }} has no columns besides rulepos.{%- endcapture -%}
    {%- query _e m: _m -%}SELECT 1 AS e WHERE error($m){%- endquery -%}
{%- endif -%}

{%- comment -%}
  3/3 — the PLAN: one row, every fragment finished. Generated from the column names above, which is
  what lets it name each column explicitly instead of needing an UNPIVOT.

  ⚠ Every cell is cast to VARCHAR before the parser sees it. That is what makes the four rules
    sources interchangeable: a parquet or JSON column may legitimately arrive typed, and the parser
    takes text.

  ⚠ The parser choice is a CASE rather than two filtered branches, and that is safe BY THE PARSER'S
    CONTRACT: dmn_condition cannot throw on an ordinary cell (its only throw is the depth guard), so
    even an eagerly-evaluated branch on an output cell costs nothing but the call.

  ⚠⚠ EVERY REFERENCE TO AN INPUT IS QUALIFIED `preprocessed.x`, AND THAT IS LOAD-BEARING RATHER THAN
    STYLE — see the header. Both the column's own name and the `@refs` inside a cell go through the
    same qualifier, because a rule may compare one input against another (`> @age`) and the two
    halves reading different values would be a wrong answer with nothing failing.
{%- endcomment -%}
{%- query _plan d: params.dialect -%}
WITH r AS (SELECT * FROM {{ rel }}),
meta AS (
    {%- for c in _cols %}
    {% unless forloop.first %}UNION ALL {% endunless %}SELECT {{ c.name | sql }} AS col, {{ forloop.index }} AS ord,
        max(CASE WHEN "rulepos"::INTEGER = -2 THEN {{ c.name | sql_ident }}::VARCHAR END) AS dir_raw,
        max(CASE WHEN "rulepos"::INTEGER = -1 THEN {{ c.name | sql_ident }}::VARCHAR END) AS dt_raw,
        max(CASE WHEN "rulepos"::INTEGER =  0 THEN {{ c.name | sql_ident }}::VARCHAR END) AS pre_raw
    FROM r
    {%- endfor %}
),
m AS (
    SELECT col, ord,
           lower(coalesce(trim(dir_raw), '')) AS dir,
           nullif(trim(coalesce(dt_raw, '')), '') AS dt,
           nullif(trim(coalesce(pre_raw, '')), '') AS pre,
           -- an `In_` prefix is stripped from the PARAMETER name, never from the column
           CASE WHEN starts_with(lower(col), 'in_') THEN substr(col, 4) ELSE col END AS param
    FROM meta
),
m2 AS (
    SELECT m.*,
           -- input preprocessing: `?` is the column, `@x` is another column
           CASE WHEN pre IS NULL THEN NULL
                ELSE regexp_replace(replace(pre, '?', param), '@(\w+)', '\1', 'g') END AS in_pre,
           -- output postprocessing: `@x` resolves now, `?` stays until the output column is known
           CASE WHEN pre IS NULL THEN NULL
                ELSE regexp_replace(pre, '@(\w+)', '\1', 'g') END AS out_pre,
           dmn_is_aggregate(pre) AS agg
    FROM m
),
m3 AS (
    SELECT m2.*,
           -- a SELECT-shaped preprocessing expression expands rows, so it becomes a LATERAL
           (in_pre IS NOT NULL AND starts_with(ltrim(lower(in_pre)), 'select')) AS is_lateral,
           bool_or(CASE WHEN dir = 'out' THEN agg ELSE false END) OVER () AS has_aggregate
    FROM m2
),
rp AS (SELECT DISTINCT "rulepos"::INTEGER AS rulepos FROM r WHERE "rulepos"::INTEGER >= 1),
cells AS (
    {%- for c in _cols %}
    {% unless forloop.first %}UNION ALL {% endunless %}SELECT {{ c.name | sql }} AS col, "rulepos"::INTEGER AS rulepos,
        coalesce(trim({{ c.name | sql_ident }}::VARCHAR), '') AS cell
    FROM r WHERE "rulepos"::INTEGER >= 1
    {%- endfor %}
),
parsed AS (
    SELECT c.col, m3.ord, c.rulepos, m3.dir,
           (c.cell IN ('', '*')) AS wild,
           CASE WHEN m3.dir = 'in'  THEN dmn_condition(c.cell, 'preprocessed.' || m3.param, $d, 'preprocessed.')
                WHEN c.cell = ''    THEN 'NULL'
                ELSE dmn_value(c.cell, $d, 'preprocessed.') END AS sql
    FROM cells c JOIN m3 ON m3.col = c.col
    WHERE m3.dir IN ('in', 'out')
)
SELECT
    (SELECT string_agg(col, ', ' ORDER BY ord) FROM m3
     WHERE dir IN ('in','out') AND NOT regexp_matches(col, '^[A-Za-z_][A-Za-z0-9_]*$')) AS bad_cols,
    (SELECT count(*) FROM m3 WHERE dir = 'in')  AS n_inputs,
    (SELECT count(*) FROM m3 WHERE dir = 'out') AS n_outputs,
    (SELECT count(*) FROM rp)                   AS n_rules,
    (SELECT bool_or(has_aggregate) FROM m3)     AS has_aggregate,
    (SELECT string_agg('(' || rulepos || ')', ', ' ORDER BY rulepos) FROM rp) AS rulepos_values,
    (SELECT string_agg(col, ', ' ORDER BY ord) FROM m3 WHERE dir = 'out')     AS out_names_csv,

    -- the macro's own parameter list
    (SELECT string_agg(param || ' := NULL' || coalesce('::' || dt, ''), ',' || chr(10) || '    ' ORDER BY ord)
     FROM m3 WHERE dir = 'in') AS params_sql,

    -- the preprocessed CTE: a scalar expression in the SELECT list, a LATERAL's column by reference
    (SELECT string_agg(CASE WHEN is_lateral THEN '_cj_' || param || '.' || param
                            ELSE coalesce('(' || in_pre || ')', param) || ' AS ' || param END,
                       ',' || chr(10) || '        ' ORDER BY ord)
     FROM m3 WHERE dir = 'in') AS pre_select_sql,

    -- ... and its FROM, which exists only when some input preprocesses with a SELECT
    (SELECT string_agg(CASE WHEN rn = 1
                            THEN 'FROM (' || in_pre || ') AS _cj_' || param || '(' || param || ')'
                            ELSE 'LEFT JOIN LATERAL (' || in_pre || ') AS _cj_' || param
                                 || '(' || param || ') ON TRUE' END,
                       chr(10) || '    ' ORDER BY ord)
     FROM (SELECT *, row_number() OVER (ORDER BY ord) AS rn
           FROM m3 WHERE dir = 'in' AND is_lateral)) AS pre_from_sql,

    -- one CASE per OUTPUT column, carrying that column's cell for every rule
    (SELECT string_agg(body, ',' || chr(10) || '        ' ORDER BY ord)
     FROM (SELECT col, ord,
                  'CASE r.rulepos'
                  || string_agg(chr(10) || '            WHEN ' || rulepos || ' THEN ' || sql, ''
                                ORDER BY rulepos)
                  || chr(10) || '        END AS ' || col AS body
           FROM parsed WHERE dir = 'out' GROUP BY col, ord)) AS out_case_sql,

    -- one CASE per INPUT column. ⚠ WILDCARD CELLS ARE OMITTED, not rendered as TRUE: a column whose
    -- every cell is blank then produces NO group at all, which is what keeps `CASE ... ELSE` legal
    -- (DuckDB needs at least one WHEN) and the generated statement free of dead predicates.
    (SELECT string_agg(body, chr(10) || '        ' ORDER BY ord)
     FROM (SELECT col, ord,
                  'AND CASE r.rulepos'
                  || string_agg(chr(10) || '            WHEN ' || rulepos || ' THEN ' || sql, ''
                                ORDER BY rulepos)
                  || chr(10) || '        ELSE 1=1 END' AS body
           FROM parsed WHERE dir = 'in' AND NOT wild GROUP BY col, ord)) AS in_where_sql,

    -- the outer SELECT's output list: postprocessing, then the datatype cast, `?` = the column
    (SELECT string_agg(CASE WHEN post IS NULL THEN col
                            ELSE replace(post, '?', col) || ' AS ' || col END,
                       ',' || chr(10) || '    ' ORDER BY ord)
     FROM (SELECT col, ord,
                  CASE WHEN out_pre IS NULL AND dt IS NULL THEN NULL
                       WHEN dt IS NULL THEN '(' || out_pre || ')'
                       ELSE 'cast(' || coalesce('(' || out_pre || ')', '?') || ' as ' || dt || ')'
                  END AS post,
                  -- in aggregate mode an output with NO preprocessing cannot be collapsed, so it is
                  -- dropped from the result — the inner CASE still computes it, so an aggregate over
                  -- another output column can still reference it.
                  -- ⚠ The ported engine carves out a column named `rule_hk` here and keeps it. That is
                  -- NOT reproduced: with no GROUP BY, a bare column beside an aggregate is a binder
                  -- error, so the carve-out would render a macro that cannot be created. One rule, no
                  -- name-specific exception.
                  (has_aggregate AND out_pre IS NULL) AS excluded
           FROM m3 WHERE dir = 'out')
     WHERE NOT excluded) AS post_select_sql,

    -- the table's identity: MD5 over every cell of every row, separator-delimited so two different
    -- tables cannot collide by concatenation alone
    (SELECT md5(string_agg(t, chr(10) ORDER BY t)) FROM (
        SELECT concat_ws(chr(31), coalesce("rulepos"::VARCHAR, '')
               {%- for c in _cols %}, coalesce({{ c.name | sql_ident }}::VARCHAR, ''){% endfor %}) AS t
        FROM r)) AS decisiontable_hk
{%- endquery -%}
{%- assign p = _plan[0] -%}

{%- comment -%}
  The refusals. Each raises through the render's own connection rather than rendering a diagnostic,
  because what this function returns is SQL a caller is about to EXECUTE.
  ⚠ `SELECT 1 AS e WHERE error(...)`, never `SELECT error(...)`: DuckDB PRUNES a projected column
    nobody reads, so the projection form can be optimised away. A filter must be evaluated.
{%- endcomment -%}
{%- if p.bad_cols != blank -%}
    {%- capture _m -%}decision_render: the decision columns must be bare identifiers to be macro parameters, but these are not: {{ p.bad_cols }}.{%- endcapture -%}
    {%- query _e m: _m -%}SELECT 1 AS e WHERE error($m){%- endquery -%}
{%- elsif p.n_inputs == 0 -%}
    {%- capture _m -%}decision_render: no column is marked 'in' by the direction row (rulepos = -2) of {{ params.rules }}.{%- endcapture -%}
    {%- query _e m: _m -%}SELECT 1 AS e WHERE error($m){%- endquery -%}
{%- elsif p.n_outputs == 0 -%}
    {%- capture _m -%}decision_render: no column is marked 'out' by the direction row (rulepos = -2) of {{ params.rules }}.{%- endcapture -%}
    {%- query _e m: _m -%}SELECT 1 AS e WHERE error($m){%- endquery -%}
{%- elsif p.n_rules == 0 -%}
    {%- capture _m -%}decision_render: {{ params.rules }} has metadata rows but no rules (no row with rulepos >= 1).{%- endcapture -%}
    {%- query _e m: _m -%}SELECT 1 AS e WHERE error($m){%- endquery -%}
{%- endif -%}

{%- comment -%}
  The default hit policy baked into the generated macro. Aggregate mode collapses every matching rule
  into one row, so RuleOrder is the only default that makes the aggregates see more than one.
{%- endcomment -%}
{%- assign hp = params.hit_policy -%}
{%- if hp == nil or hp == blank -%}
    {%- if p.has_aggregate -%}{%- assign hp = 'RuleOrder' -%}{%- else -%}{%- assign hp = 'First' -%}{%- endif -%}
{%- endif -%}
CREATE OR REPLACE MACRO {{ params.name }}(
    {{ p.params_sql }},
    hit_policy := '{{ hp }}'
) AS TABLE
WITH preprocessed AS (
    SELECT
        {{ p.pre_select_sql }}
{%- if p.pre_from_sql != blank %}
    {{ p.pre_from_sql }}
{%- endif %}
), rules AS (
    SELECT * FROM (VALUES {{ p.rulepos_values }}) r(rulepos)
), matched AS (
    SELECT
        {{ p.out_case_sql }},
        r.rulepos
    FROM
        rules r, preprocessed
    WHERE
        1=1
        {{ p.in_where_sql }}
), ranked AS (
    SELECT
        *,
        row_number() OVER (ORDER BY rulepos) AS _hit_,
        count(*) OVER () AS _total_hits_,
        dense_rank() OVER (ORDER BY {{ p.out_names_csv }}) AS _hits_rank_
    FROM matched
), result AS (
    SELECT
        *,
        max(_hits_rank_ - 1) OVER () AS _any_hitpolicy_violations_
    FROM ranked
)
SELECT
    '{{ p.decisiontable_hk }}' AS decisiontable_hk,
{%- if p.has_aggregate %}
    string_agg(rulepos::VARCHAR, ',' ORDER BY rulepos) AS rulepos,
    {{ p.post_select_sql }},
    max(_total_hits_) AS _total_hits_,
    max(_any_hitpolicy_violations_) AS _any_hitpolicy_violations_,
    CASE
        WHEN hit_policy = 'Unique' AND max(_total_hits_) <> 1 THEN 'Model Error: More than one rule hit for this input!'
        WHEN hit_policy = 'Any' AND max(_any_hitpolicy_violations_) > 0 THEN 'Model Error: Overlapping rules hit with different outputs!'
        WHEN max(_total_hits_) IS NULL THEN 'No default rule defined'
        ELSE NULL
    END AS _model_error_
{%- else %}
    rulepos,
    {{ p.post_select_sql }},
    _total_hits_,
    _any_hitpolicy_violations_,
    CASE
        WHEN hit_policy = 'Unique' AND _total_hits_ <> 1 THEN 'Model Error: More than one rule hit for this input!'
        WHEN hit_policy = 'Any' AND _any_hitpolicy_violations_ > 0 THEN 'Model Error: Overlapping rules hit with different outputs!'
        WHEN _total_hits_ IS NULL THEN 'No default rule defined'
        ELSE NULL
    END AS _model_error_
{%- endif %}
FROM
    result
WHERE
    COALESCE(hit_policy, '{{ hp }}') = 'RuleOrder' OR _hit_ = 1
$$, params := {'rules': rules, 'name': name, 'dialect': dialect, 'source': source,
               'hit_policy': hit_policy});
