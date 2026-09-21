-- decision_check(rules [, source := 'auto']) -> TABLE
--   Shows what every cell of a decision table COMPILES TO, and flags the ones that look wrong.
--   The reviewable artifact: one row per cell, with the SQL it becomes.
--
--     SELECT * FROM decision_check('decision_rules');
--     -- rulepos | col    | direction | cell        | sql                      | verdict
--     --      -1 | age    | in        | INTEGER     | CAST(NULL AS INTEGER)    | ok
--     --       0 | label  | out       | upper(?)    | (upper(label))           | ok
--     --       1 | region | in        | EU, US      | preprocessed.region I... | ok
--     --       2 | region | in        | COLLATE "C" | preprocessed.region =... | reads as a condition
--
--   `verdict`:
--     `ok`                    the cell compiles to what it looks like
--     `reads as a condition`  ⚠ the parser QUOTED this cell as a literal, but the engine would have
--                             parsed `<column> <cell>` as a CONDITION — so it renders an equality
--                             against the cell's own TEXT, which is valid SQL that is ALWAYS FALSE.
--                             This is the one SILENT failure mode a decision table has. `decision_render`
--                             REFUSES on it (strict := true); here it is reported so a whole table can be
--                             reviewed at once.
--     `does not bind: <msg>`  the rendered SQL is not something the engine can compile — an unknown
--                             column, an unknown function, a type mismatch — carrying the engine's own
--                             sentence. The target would refuse it at CREATE time.
--
-- ⚠⚠ ITS REAL VALUE IS THE `sql` COLUMN, NOT THE VERDICTS. "What does this decision table actually
--   compile to" is the question nobody can answer by reading the rules, and it is the one that matters
--   when the rules are a production artifact: this is a relation, so it can be reviewed, diffed between
--   two versions of a rules table, or stored beside the decision it produced.
--   ⚠ It covers OUTPUT cells too, which the strict audit does not.
--
-- ⚠⚠ IT ALSO COVERS THE METADATA ROWS, and those matter MORE than a cell (user-raised: *"we could check
--   the outputs as well, as these can also be expressions?"* — the answer for output CELLS was already
--   yes, and asking is what found the rows that were not).
--   • `rulepos = -1`, the DATATYPES row, becomes a CAST in every rule of that column.
--   • `rulepos = 0`, PRE/POSTPROCESSING, is an expression applied to every rule of that column.
--   MEASURED unreported before this existed: `uppr(?)` in a postprocessing row produced no row here at
--   all and failed later at CREATE. One wrong cell breaks one rule; one wrong metadata row breaks the
--   column.
--   ⚠ `?` means DIFFERENT THINGS by direction — the INPUT column in a preprocessing expression, the
--     OUTPUT column in a postprocessing one — so the two are rendered separately and the postprocessing
--     probe binds against a context that carries the outputs as well.
--
-- ⚠⚠ IT BINDS. Every cell's SQL is bound against a TYPED-NULL context built from the datatypes row —
--   `FROM (SELECT NULL::VARCHAR AS region, NULL::INTEGER AS age) AS preprocessed` — so a mistyped
--   column, an unknown function or a type mismatch is reported PER CELL, with the engine's own
--   sentence, instead of surfacing as one binder error against generated SQL at CREATE time.
--   ⚠⚠ THIS FILE PREVIOUSLY SAID THE BIND LAYER WAS IMPOSSIBLE, AND THAT WAS WRONG (user-challenged:
--     *"i did not understand the problem with a prepare … couldn't the checker execute this per cell"*).
--     The measurement behind it was right — a bind error DOES throw, and `PREPARE` raises rather than
--     deferring — but the conclusion was not: a throw only needs somewhere to be CAUGHT, and a C#
--     scalar is somewhere. `dmn_bind_check(sql)` wraps the attempt in a try and returns NULL or the
--     message, which makes the check an ordinary per-row expression and therefore set-based here.
--     ⇒ the lesson is the one this codebase keeps relearning: "it cannot be done" usually means "it
--       cannot be done IN THIS LANGUAGE".
--   ⚠ It binds via `EXPLAIN`, so a cell calling a function with side effects cannot fire one.
--   ⚠ ONE host round trip per non-wildcard cell (~0.35 ms), which is why this is a review tool for a
--     decision table's worth of cells rather than something to put in a hot path.
--
-- ⚠ IT IS BUILT ON `fluid_replacement_query`, NOT `fluid_query` (user-suggested 2026-09-21:
--   *"maybe you can use the replacement query instead"*). It was adopted to dodge the projection
--   defect in known-limitations.md §1.27, which is now FIXED — so the reason it STAYS is the
--   remaining merit: `fluid_replacement_query` is `bind_replace`, the call DISAPPEARS at bind into a
--   subquery, and DuckDB then pushes the caller's predicate into the generated SQL itself.
--   ⚠ It is available here ONLY because this function's arguments are BIND-TIME CONSTANTS (a rules
--     relation and a source mode). A per-row surface cannot take this path — the recorded rejection
--     of `fluid_replacement_query` as the EVALUATION surface still stands.
--   ⚠⚠ AND IT IS NOT AVAILABLE TO `decision_coverage`, which is why that one still carries the
--     hazard: it stages a TEMP MACRO through `exec()` and the generated SQL calls it. Under
--     bind_replace the render runs on its OWN connection while the spliced SQL runs on the
--     CALLER's, so the macro is invisible — MEASURED: *"Table Function with name
--     fab_cov_<md5> does not exist!"*. This function stages NOTHING on the connection (its
--     `{% query %}` blocks substitute their results as TEXT), which is exactly what makes it
--     eligible. ⇒ the test for eligibility is "does the render leave anything on the connection",
--     not "are the arguments constant".
--
CREATE OR REPLACE MACRO decision_check(rules, source := 'auto') AS TABLE
SELECT * FROM fluid_replacement_query($$
{%- query _rel rules: params.rules, src: params.source -%}
    SELECT dmn_relation($rules, $src) AS rel
{%- endquery -%}
{%- assign rel = _rel[0].rel -%}

{%- query _cols -%}
    SELECT column_name AS name
    FROM (DESCRIBE SELECT * FROM {{ rel }})
    WHERE lower(column_name) <> 'rulepos'
{%- endquery -%}

{%- comment -%}
  ⚠⚠ THE TYPED-NULL CONTEXT the cells are bound against, built from the datatypes row. Aliased
  `preprocessed` because that is what the rendered references are qualified with.

  ⚠⚠ A DATATYPE THAT DOES NOT BIND FALLS BACK TO VARCHAR *HERE*, AND THAT IS WHAT KEEPS THE REPORT
  READABLE. MEASURED without it: one `INTEGERR` in the datatypes row put `NULL::INTEGERR` into the
  context itself, so FIVE of the six rows of a four-cell table reported "Type with name INTEGERR does
  not exist" — one fault, five rows, and the one row that escaped did so ARBITRARILY (a fallback-literal
  cell binds against its own inline context, not this one). A checker that reports five things for one
  mistake is the cry-wolf failure `decision_coverage` §4 exists to avoid.
  ⇒ the datatype is bind-checked as it is collected; an unusable one is replaced by VARCHAR so every
    OTHER cell is still checked, and the `rulepos = -1` row still reports the real fault, once.
  ⚠ It costs one round trip per COLUMN, not per cell.
  ⚠ RESIDUAL, stated rather than chased: a cell in a GOOD column that references a BAD one
    (`@age` where `age` carries the bad type) binds against the SUBSTITUTE, so it can still report a
    spurious type complaint. Per-column is the right granularity — suppressing every column that any
    other column mentions would blind the report again — and the root-cause row sorts first.
{%- endcomment -%}
{%- query _ctx -%}
WITH r AS (SELECT * FROM {{ rel }}),
meta AS (
    {%- for c in _cols %}
    {% unless forloop.first %}UNION ALL {% endunless %}SELECT {{ c.name | sql }} AS col, {{ forloop.index }} AS ord,
        max(CASE WHEN "rulepos"::INTEGER = -2 THEN {{ c.name | sql_ident }}::VARCHAR END) AS dir_raw,
        max(CASE WHEN "rulepos"::INTEGER = -1 THEN {{ c.name | sql_ident }}::VARCHAR END) AS dt_raw
    FROM r
    {%- endfor %}
)
SELECT coalesce(string_agg('NULL::' || dt || ' AS ' || param, ', ' ORDER BY ord)
                FILTER (WHERE dir = 'in'), 'NULL AS dmn_none') AS ctx,
       coalesce(string_agg('NULL::' || dt || ' AS ' || param, ', ' ORDER BY ord), 'NULL AS dmn_none') AS ctx_all
FROM (SELECT ord, dir, param,
             CASE WHEN dmn_bind_check('SELECT CAST(NULL AS ' || dt0 || ')') IS NULL
                  THEN dt0 ELSE 'VARCHAR' END AS dt
      FROM (SELECT ord, lower(coalesce(trim(dir_raw), '')) AS dir,
                   coalesce(nullif(trim(coalesce(dt_raw,'')),''), 'VARCHAR') AS dt0,
                   CASE WHEN starts_with(lower(col), 'in_') THEN substr(col, 4) ELSE col END AS param
            FROM meta))
WHERE dir IN ('in', 'out')
{%- endquery -%}
{%- capture ctx -%}(SELECT {{ _ctx[0].ctx }}) AS preprocessed{%- endcapture -%}
{%- comment -%}
  ⚠ A SECOND context carrying the OUTPUT columns as well, because a POSTPROCESSING expression's `?` is
  the OUTPUT column — `upper(?)` on `label` becomes `upper(label)`, which cannot bind against the inputs
  alone. Kept separate so an INPUT cell's probe cannot accidentally resolve a typo against an output
  column's name.
{%- endcomment -%}
{%- capture ctx_all -%}(SELECT {{ _ctx[0].ctx_all }}) AS preprocessed{%- endcapture -%}

{%- comment -%}
  Every cell, rendered by the SAME parser the generated macro uses, and BOUND — one query, because
  `dmn_bind_check` takes a per-row value. (The parse-only probe this used to generate per cell is gone:
  a statement that does not parse does not bind either, so binding subsumes it.)
{%- endcomment -%}{%- comment -%}
  ⚠ The FALLBACK branch is tested first and answers for BOTH outcomes: a quoted literal is EXPECTED not
  to read as a condition, so folding it into the bind test below reports every ordinary cell (`EU`) as
  broken. Caught by the clean-table control the first time this was written.
{%- endcomment -%}
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
    SELECT col, ord, lower(coalesce(trim(dir_raw), '')) AS dir,
           nullif(trim(coalesce(dt_raw, '')), '') AS dt,
           nullif(trim(coalesce(pre_raw, '')), '') AS pre,
           CASE WHEN starts_with(lower(col), 'in_') THEN substr(col, 4) ELSE col END AS param,
           (nullif(trim(coalesce(dt_raw, '')), '') IS NOT NULL
            AND dmn_bind_check('SELECT CAST(NULL AS ' || trim(dt_raw) || ')') IS NOT NULL) AS dt_bad
    FROM meta
),
cells AS (
    {%- for c in _cols %}
    {% unless forloop.first %}UNION ALL {% endunless %}SELECT {{ c.name | sql }} AS col, "rulepos"::INTEGER AS rulepos,
        coalesce(trim({{ c.name | sql_ident }}::VARCHAR), '') AS cell
    FROM r WHERE "rulepos"::INTEGER >= 1
    {%- endfor %}
),
rule_rows AS (
    SELECT c.rulepos, m.col, m.ord, m.dir, c.cell,
           (c.cell IN ('', '*')) AS wild,
           CASE WHEN m.dir = 'in' THEN dmn_condition(c.cell, 'preprocessed.' || m.param, 'duckdb', 'preprocessed.')
                WHEN c.cell = ''  THEN 'NULL'
                ELSE dmn_value(c.cell, 'duckdb', 'preprocessed.') END AS sql,
           m.param, m.dt_bad
    FROM cells c JOIN m ON m.col = c.col
    WHERE m.dir IN ('in', 'out')
),
meta_rows AS (
    SELECT -1 AS rulepos, col, ord, dir, dt AS cell, false AS wild,
           'CAST(NULL AS ' || dt || ')' AS sql, param, dt_bad
    FROM m WHERE dir IN ('in', 'out') AND dt IS NOT NULL
    UNION ALL
    SELECT 0, col, ord, dir, pre, false,
           CASE WHEN dir = 'in'
                THEN '(' || regexp_replace(replace(pre, '?', param), '@(\w+)', '\1', 'g') || ')'
                ELSE '(' || replace(regexp_replace(pre, '@(\w+)', '\1', 'g'), '?', param) || ')' END,
           param, dt_bad
    FROM m WHERE dir IN ('in', 'out') AND pre IS NOT NULL
),
all_rows AS (
    SELECT rulepos, col, ord, dir, cell, wild, sql, param, dt_bad,
           (dir = 'in' AND NOT wild AND rulepos >= 1
            AND sql = 'preprocessed.' || param || ' = ''' || replace(cell, '''', '''''') || '''') AS is_fb
    FROM (SELECT * FROM rule_rows UNION ALL SELECT * FROM meta_rows)
)
SELECT rulepos, col, dir AS direction, cell, sql,
       CASE
         WHEN rulepos = -1 THEN
              coalesce('does not bind: ' || dmn_bind_check('SELECT ' || sql), 'ok')
         WHEN wild THEN 'ok'
         WHEN dt_bad THEN 'not checked: the datatypes row for ' || col || ' does not bind'
         WHEN is_fb THEN
              CASE WHEN dmn_bind_check('SELECT CASE WHEN dmn_c ' || cell
                                       || ' THEN 1 END FROM (SELECT NULL::VARCHAR AS dmn_c)') IS NULL
                   THEN 'reads as a condition' ELSE 'ok' END
         WHEN rulepos >= 1 AND dir = 'in' THEN
              coalesce('does not bind: ' || dmn_bind_check('SELECT CASE WHEN ' || sql
                                                           || ' THEN 1 END FROM {{ ctx }}'), 'ok')
         WHEN rulepos = 0 AND dir = 'out' THEN
              coalesce('does not bind: ' || dmn_bind_check('SELECT ' || sql || ' FROM {{ ctx_all }}'), 'ok')
         ELSE coalesce('does not bind: ' || dmn_bind_check('SELECT ' || sql || ' FROM {{ ctx }}'), 'ok')
       END AS verdict
FROM all_rows
ORDER BY rulepos, col
$$, params := {'rules': rules, 'source': source});
