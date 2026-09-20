-- decision_render(rules [, name := 'decision_eval'] [, form := 'scalar'] [, is_temporary := false]
--                       [, source := 'auto'] [, hit_policy := NULL])
--   Renders a DMN-like DECISION TABLE into the SQL TEXT of a DuckDB MACRO that evaluates it.
--   It returns TEXT and creates nothing: run it, read it, then execute it yourself.
--
--     SELECT decision_render('decision_rules', name := 'evaluate');
--
--   A decision table is a RELATION: one row per rule, plus three METADATA rows.
--       rulepos = -2  direction    'in' | 'out' | blank (blank = the column is ignored, e.g. Comment)
--       rulepos = -1  datatypes    the input cast, and the output cast
--       rulepos =  0  pre/post     input preprocessing, output postprocessing (optional)
--       rulepos >= 1  the rules    one row each, in evaluation order
--   Each CELL is a small expression language — `> 18`, `EU, US`, `[21 .. 65]`, `not RU`,
--   `between min(1) and max(100)`, `@age*2 > min(1)+1`, `contains(?, 'x')`. See dmn_condition /
--   dmn_value, which are the parser and are callable on their own.
--
-- ⚠⚠ `form` — THE TWO SHAPES, AND THERE ARE ONLY TWO BECAUSE ONLY TWO CAN TAKE VARYING INPUT.
--   A DuckDB MACRO ARGUMENT IS A BIND-TIME CONSTANT, so a table macro whose parameters are the
--   decision's inputs can only ever be called with LITERALS — one decision per call site, which is
--   not what anybody evaluates a decision table for. Both forms below take their input as DATA
--   instead: one value per row, or one whole relation.
--
--   `form := 'scalar'` (the DEFAULT) — a SCALAR macro `f(input [, hit_policy])` taking a STRUCT and
--     returning a UNION. It is an ordinary expression, so it evaluates PER ROW anywhere:
--
--       SELECT decision_render('decision_rules', name := 'evaluate');   -- then run the text
--       SELECT c.id,
--              union_extract(evaluate({'region': c.region, 'age': c.age}), 'single').decision
--       FROM customers c;
--
--     The UNION has two members, and the HIT POLICY chooses which one is returned:
--       `single STRUCT(...)`   — First / Unique / Any, and EVERY aggregate-mode call
--       `many STRUCT(...)[]`   — RuleOrder on a non-aggregate table (all matching rules)
--     Read it with `union_tag(...)`, `union_extract(..., 'single')`, or
--     `unnest(union_extract(..., 'many'))`. ⚠ NO MATCH returns NULL under First/Unique/Any, and a
--     `many` whose list is NULL under RuleOrder — measured, and it is why `_model_error_ = 'No
--     default rule defined'` is reachable only in aggregate mode.
--
--   `form := 'table'` (or `'batch'`, the ported engine's name for it) — a TABLE macro
--     `f(input_table [, hit_policy])` that evaluates a WHOLE RELATION in one pass and returns the
--     input's own columns alongside the decision:
--
--       SELECT decision_render('decision_rules', name := 'evaluate_batch', form := 'table');
--       SELECT * FROM evaluate_batch('customers');
--
--     ⚠ `input_table` is a bind-time constant naming a relation — `'customers'`, `'main.customers'`,
--       or the bare identifier `customers`; it reaches `query_table()`, so it cannot be a subquery.
--       Stage one first (`CREATE TEMP TABLE`, a view) if that is what you have.
--     ⚠ THE INPUT RELATION MUST CARRY A COLUMN PER INPUT, NAMED AS THE INPUT IS: the preprocessed
--       CTE replaces each of them and passes `* EXCLUDE (<the inputs>)` through, so a missing one is
--       a binder error naming it. Every OTHER column rides through untouched, which is what lets a
--       key column come back beside the decision.
--     ⚠ In AGGREGATE mode the outer select is `GROUP BY ALL` over those passed-through columns, so
--       two input rows that are equal in EVERY column collapse into one. Carry a key if that matters.
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
--   `name` — the macro name to render.
--   ⚠ THERE IS NO `dialect` PARAMETER: this macro renders DuckDB, full stop. It had one until
--     2026-09-20, and the only thing it ever did was REFUSE every value but `duckdb` — so removing
--     it cannot break a call that worked. SQL Server is `decision_render_tsql`, which is a separate
--     macro because the statement templates share almost nothing.
--   `hit_policy` — the DEFAULT baked into the generated macro; NULL (default) means `RuleOrder` when
--     the table is in aggregate mode and `First` otherwise. The generated macro takes `hit_policy` as
--     a parameter either way, so this only chooses what an unqualified call does.
--
--   `is_temporary` — render `CREATE OR REPLACE **TEMPORARY** MACRO`, so the macro lives in the `temp`
--     catalog and dies with the session instead of being written into the database. It changes ONE
--     token and nothing else; the body is byte-identical either way.
--     ⚠⚠ AND IT COMES WITH A TRAP THAT IS SILENT, SO READ THIS BEFORE USING IT: A TEMPORARY MACRO
--       CREATED THROUGH `fabricator_host_exec` IS INVISIBLE TO THE CALLER AND IS THEN GONE. MEASURED:
--       `SELECT fabricator_host_exec(decision_render(..., is_temporary := true))` returns **0** — the
--       success code — and the macro does not exist afterwards, in `duckdb_functions()` or anywhere
--       else. `fabricator_host_exec` runs on its OWN connection, whose `temp` catalog dies with it
--       (the same is true of a `CREATE TEMP TABLE` issued that way). There is no error to notice.
--     ⇒ **a temporary macro has to be executed BY THE CALLER, on the caller's own connection** — read
--       the text back and run it (a client's `execute(fetchone()[0])`, the shell's `.read`, a dbt
--       pre-hook). That is the documented primary use of this function anyway; `fabricator_host_exec`
--       is the convenience path, and it is the one path `is_temporary` cannot take.
--     ⚠ A QUALIFIED `name` and `is_temporary` do not combine: DuckDB resolves the schema inside the
--       `temp` catalog, so `name := 's1.m'` fails with *"Schema with name s1 does not exist!"* — its
--       error, loud, and not worth a refusal of ours on top.
--
--   ⚠⚠ THE SCALAR FORM MUST WRITE ITS RESULT TYPE OUT, so an output column with NO DECLARED DATATYPE
--     is typed VARCHAR there and its value is CAST — where the table form keeps whatever the cell
--     expression produced. That is inherent (a UNION type has to be spelled, and the cell types are
--     not known without executing the generated SQL), and the escape is the datatypes row: declare
--     `DOUBLE` and you get a DOUBLE. The table form is unaffected.
--
--   ⚠⚠ THE WHOLE POINT OF THE SPLIT: the CELL PARSER is C# because it RECURSES, the SQL SHAPE below is
--     Liquid because a template is a shape, and EVERYTHING BETWEEN THEM IS SET-BASED and therefore
--     SQL. The `plan` query returns ONE ROW of finished string fragments — the CASE blocks, the WHERE
--     blocks, the struct type — built with `string_agg` over every cell at once. That is why this
--     template has no nested loops: Liquid gets FLAT values and substitutes them. BOTH forms are
--     rendered from that ONE plan; only the statement around it differs.
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
--   ⚠⚠ EVERY REFERENCE TO AN INPUT IS RENDERED QUALIFIED (`preprocessed.age`, never `age`), AND IT IS
--     STILL LOAD-BEARING THOUGH THE ORIGINAL REASON IS GONE. It was written for macro-parameter
--     shadowing, which cannot arise now that the inputs are DATA rather than parameters. The reason it
--     stays is the TABLE form: the rule predicates sit in a LATERAL beside the output CASEs, and a
--     LOCAL SELECT ALIAS BEATS A CORRELATED OUTER COLUMN OF THE SAME NAME. MEASURED — with an output
--     column named `region`, an unqualified `region = 'EU'` compares against that output EXPRESSION
--     and the rule silently never fires; qualified, it matches. (In the plain table case the COLUMN
--     wins over the alias, which is why this looks safe until a lateral is involved.)
--
--   ⚠ EVERY REFUSAL RAISES rather than returning a diagnostic STRING, because the answer here is SQL
--     TEXT: a caller is about to execute it, and text that merely describes a problem would be run.
CREATE OR REPLACE MACRO decision_render(rules, name := 'decision_eval', form := 'scalar',
                                        is_temporary := false, source := 'auto',
                                        hit_policy := NULL::VARCHAR) AS
fluid_render($$
{%- comment -%}
  `is_temporary` renders `CREATE OR REPLACE TEMPORARY MACRO`, which is the ONLY thing it changes — the
  body is byte-identical either way, and the gate asserts exactly that. See the header for the one trap
  that comes with it.
{%- endcomment -%}
{%- if params.is_temporary -%}{%- assign tkw = 'TEMPORARY ' -%}{%- else -%}{%- assign tkw = '' -%}{%- endif -%}
{%- comment -%}
  THE FORM. `batch` is accepted for `table` because that is what the ported engine calls it, and a
  caller reading that project will type it. Anything else is refused BY NAME — the alternative is a
  silent default, which would hand back the other shape entirely.
{%- endcomment -%}
{%- assign fm = params.form | default: 'scalar' | downcase | strip -%}
{%- if fm == 'batch' -%}{%- assign fm = 'table' -%}{%- endif -%}
{%- if fm != 'scalar' and fm != 'table' -%}
    {%- capture _m -%}decision_render: form '{{ params.form }}' is not one of 'scalar' (a scalar macro over a STRUCT, evaluated per row) or 'table' (a table macro over a whole relation).{%- endcapture -%}
    {%- query _e m: _m -%}SELECT 1 AS e WHERE error($m){%- endquery -%}
{%- endif -%}
{%- assign is_table = false -%}
{%- if fm == 'table' -%}{%- assign is_table = true -%}{%- endif -%}

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
  The OUTPUT reference prefix — the one fragment of the plan that differs between the two forms. The
  scalar form reads its outputs from a CTE (`filtered`) so they are bare; the table form reads them
  out of the LATERAL it joined, so they are `ranked.<col>`. Input references are `preprocessed.` in
  BOTH forms, which is what lets one plan serve both — see the qualification note in the header.
{%- endcomment -%}
{%- if is_table -%}{%- assign opfx = 'ranked.' -%}{%- else -%}{%- assign opfx = '' -%}{%- endif -%}

{%- comment -%}
  ... and the INDENT the two CASE blocks are built at, for the same reason: the shared fragments land
  four CTEs deep in the scalar form and inside three nested subqueries in the table form. Cosmetic
  only — but this function's whole output is SQL TEXT a person reads before running it.
{%- endcomment -%}
{%- if is_table -%}{%- assign ind = '                    ' -%}{%- else -%}{%- assign ind = '        ' -%}{%- endif -%}

{%- comment -%}
  3/3 — the PLAN: one row, every fragment finished. Generated from the column names above, which is
  what lets it name each column explicitly instead of needing an UNPIVOT.

  ⚠ Every cell is cast to VARCHAR before the parser sees it. That is what makes the four rules
    sources interchangeable: a parquet or JSON column may legitimately arrive typed, and the parser
    takes text.

  ⚠ The parser choice is a CASE rather than two filtered branches, and that is safe BY THE PARSER'S
    CONTRACT: dmn_condition cannot throw on an ordinary cell (its only throw is the depth guard), so
    even an eagerly-evaluated branch on an output cell costs nothing but the call.
{%- endcomment -%}
{%- query _plan d: 'duckdb', opfx: opfx, ind: ind -%}
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
           -- an `In_` prefix is stripped from the INPUT name, never from the column
           CASE WHEN starts_with(lower(col), 'in_') THEN substr(col, 4) ELSE col END AS param
    FROM meta
),
m2 AS (
    SELECT m.*,
           -- input preprocessing: `?` is the column, `@x` is another column. Both stay BARE: this
           -- expression lives INSIDE the preprocessed CTE, where a bare name is the raw input, which
           -- is exactly what preprocessing consumes.
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
),
-- the outputs that SURVIVE into the result. In aggregate mode an output with NO preprocessing cannot
-- be collapsed, so it is dropped — the inner CASE still computes it, so an aggregate over another
-- output column can still reference it.
-- ⚠ The ported engine carves out a column named `rule_hk` here and keeps it. That is NOT reproduced:
-- with no GROUP BY, a bare column beside an aggregate is a binder error, so the carve-out would
-- render a macro that cannot be created. One rule, no name-specific exception.
kept AS (
    SELECT col, ord, dt,
           CASE WHEN out_pre IS NULL AND dt IS NULL THEN NULL
                WHEN dt IS NULL THEN '(' || out_pre || ')'
                ELSE 'cast(' || coalesce('(' || out_pre || ')', '?') || ' as ' || dt || ')'
           END AS post
    FROM m3
    WHERE dir = 'out' AND NOT (has_aggregate AND out_pre IS NULL)
)
SELECT
    (SELECT string_agg(col, ', ' ORDER BY ord) FROM m3
     WHERE dir IN ('in','out')
       AND (NOT regexp_matches(col, '^[A-Za-z_][A-Za-z0-9_]*$')
            -- the STRIPPED name has to be an identifier too, or `In_` alone would render `input['']`
            OR NOT regexp_matches(param, '^[A-Za-z_][A-Za-z0-9_]*$'))) AS bad_cols,
    (SELECT count(*) FROM m3 WHERE dir = 'in')  AS n_inputs,
    (SELECT count(*) FROM m3 WHERE dir = 'out') AS n_outputs,
    (SELECT count(*) FROM rp)                   AS n_rules,
    (SELECT bool_or(has_aggregate) FROM m3)     AS has_aggregate,
    -- ⚠⚠ `rulepos` IS THE ORDERING, so it must be 1..N WITH NO GAPS — the ported engine's assumption,
    -- and a deliberate one. What is NOT inherited is assuming it SILENTLY: `generate_series(1, <count>)`
    -- over a table numbered 1, 5, 9 generates slots 1, 2, 3, and a slot that corresponds to no rule is a
    -- PHANTOM — every input CASE falls through to `ELSE 1=1`, so it matches EVERYTHING, while the output
    -- CASE has no branch for it and answers all-NULL. Under `First` a phantom can therefore WIN, and
    -- rules 5 and 9 are never reached at all. So the assumption is CHECKED and a gap is refused by name.
    (SELECT CASE WHEN count(*) = max(rulepos) AND min(rulepos) = 1 THEN NULL
                 ELSE string_agg(rulepos::VARCHAR, ', ' ORDER BY rulepos) END
     FROM rp) AS bad_rulepos,
    (SELECT string_agg(col, ', ' ORDER BY ord) FROM m3 WHERE dir = 'out')     AS out_names_csv,

    -- SCALAR form only: the `params` CTE that unpacks the input STRUCT into named, cast columns
    (SELECT string_agg('input[''' || param || ''']' || coalesce('::' || dt, '') || ' AS ' || param,
                       ',' || chr(10) || '        ' ORDER BY ord)
     FROM m3 WHERE dir = 'in') AS input_cast_sql,

    -- TABLE form only: the input names, which are what `* EXCLUDE (…)` must drop from the input
    -- relation so the PREPROCESSED value takes their place
    (SELECT string_agg(param, ', ' ORDER BY ord) FROM m3 WHERE dir = 'in') AS in_names_csv,

    -- TABLE form only: the `casted` CTE — the batch analogue of the scalar form's `params`, and it
    -- exists for the same reason. ⚠⚠ THE DATATYPES ROW MUST TYPE THE INPUT IN BOTH FORMS OR THE SAME
    -- DECISION TABLE ANSWERS DIFFERENTLY DEPENDING ON HOW IT IS EVALUATED. MEASURED on the ported
    -- engine's own example, whose `risk` declares DOUBLE while `amount` declares FLOAT: the scalar
    -- form answered 0.20000000298023224 (the CASE unifies to FLOAT, then widens) and the table form
    -- 0.2, because there the inputs kept the input relation's own DOUBLE columns. ⚠ This is a
    -- DELIBERATE DEVIATION from the ported engine, which casts in neither batch form — one more
    -- reason than it knew: a rules table read from CSV arrives all-VARCHAR, and only the cast makes
    -- `age > 18` a numeric comparison rather than a lexicographic one.
    (SELECT string_agg(CASE WHEN dt IS NULL THEN param ELSE param || '::' || dt || ' AS ' || param END,
                       ',' || chr(10) || '        ' ORDER BY ord)
     FROM m3 WHERE dir = 'in') AS in_cast_sql,

    -- the preprocessed CTE: a scalar expression in the SELECT list, a LATERAL's column by reference
    (SELECT string_agg(CASE WHEN is_lateral THEN '_cj_' || param || '.' || param
                            ELSE coalesce('(' || in_pre || ')', param) || ' AS ' || param END,
                       ',' || chr(10) || '        ' ORDER BY ord)
     FROM m3 WHERE dir = 'in') AS pre_select_sql,

    -- ... and the joins it needs, which exist only when some input preprocesses with a SELECT. Both
    -- forms have a base relation (`params` / `query_table(...)`), so EVERY one is a LEFT JOIN LATERAL.
    (SELECT string_agg('LEFT JOIN LATERAL (' || in_pre || ') AS _cj_' || param
                       || '(' || param || ') ON TRUE', chr(10) || '    ' ORDER BY ord)
     FROM m3 WHERE dir = 'in' AND is_lateral) AS pre_lateral_sql,

    -- one CASE per OUTPUT column, carrying that column's cell for every rule
    (SELECT string_agg(body, ',' || chr(10) || $ind ORDER BY ord)
     FROM (SELECT col, ord,
                  'CASE r.rulepos'
                  || string_agg(chr(10) || $ind || '    WHEN ' || rulepos || ' THEN ' || sql, ''
                                ORDER BY rulepos)
                  || chr(10) || $ind || 'END AS ' || col AS body
           FROM parsed WHERE dir = 'out' GROUP BY col, ord)) AS out_case_sql,

    -- one CASE per INPUT column. ⚠ WILDCARD CELLS ARE OMITTED, not rendered as TRUE: a column whose
    -- every cell is blank then produces NO group at all, which is what keeps `CASE ... ELSE` legal
    -- (DuckDB needs at least one WHEN) and the generated statement free of dead predicates.
    (SELECT string_agg(body, chr(10) || $ind ORDER BY ord)
     FROM (SELECT col, ord,
                  'AND CASE r.rulepos'
                  || string_agg(chr(10) || $ind || '    WHEN ' || rulepos || ' THEN ' || sql, ''
                                ORDER BY rulepos)
                  || chr(10) || $ind || 'ELSE 1=1 END' AS body
           FROM parsed WHERE dir = 'in' AND NOT wild GROUP BY col, ord)) AS in_where_sql,

    -- the result's output list: postprocessing, then the datatype cast, `?` = the column. $opfx is
    -- where the value is read FROM, and it is the only form-dependent fragment in this plan.
    (SELECT string_agg(CASE WHEN post IS NULL THEN $opfx || col
                            ELSE replace(post, '?', $opfx || col) || ' AS ' || col END,
                       ',' || chr(10) || '    ' ORDER BY ord)
     FROM kept) AS post_select_sql,

    -- SCALAR form only: the result STRUCT's fields, and the literal that fills them.
    -- ⚠ An output with no declared datatype is VARCHAR here and its value is CAST — a UNION type has
    -- to be spelled out, and nothing short of executing the generated SQL would tell us what the cell
    -- expressions produce. The datatypes row is the escape. See the header.
    (SELECT string_agg(col || ' ' || coalesce(dt, 'VARCHAR'), ', ' ORDER BY ord) FROM kept) AS out_struct_sql,
    (SELECT string_agg('''' || col || ''': ' || col, ', ' ORDER BY ord) FROM kept) AS out_structlit_sql,

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
{%- elsif p.bad_rulepos != blank -%}
    {%- capture _m -%}decision_render: rulepos is the rule ORDERING and must run 1..N with no gaps, but {{ params.rules }} has {{ p.bad_rulepos }}. A gap would generate a rule slot matching every input and returning NULLs.{%- endcapture -%}
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

{%- comment -%}
  The result STRUCT of the scalar form, spelled once and used three times.
{%- endcomment -%}
{%- capture st -%}STRUCT(decisiontable_hk VARCHAR, rulepos VARCHAR, {{ p.out_struct_sql }}, _total_hits_ BIGINT, _any_hitpolicy_violations_ BIGINT, _model_error_ VARCHAR){%- endcapture -%}
{%- capture ut -%}UNION(single {{ st }}, many {{ st }}[]){%- endcapture -%}
{%- capture lit -%}{ 'decisiontable_hk': decisiontable_hk, 'rulepos': rulepos, {{ p.out_structlit_sql }}, '_total_hits_': _total_hits_, '_any_hitpolicy_violations_': _any_hitpolicy_violations_, '_model_error_': _model_error_ }{%- endcapture -%}

{%- if is_table -%}
CREATE OR REPLACE {{ tkw }}MACRO {{ params.name }}(input_table, hit_policy := '{{ hp }}') AS TABLE
WITH casted AS (
    SELECT
        {{ p.in_cast_sql }},
        * EXCLUDE ({{ p.in_names_csv }})
    FROM query_table(input_table)
), preprocessed AS (
    SELECT
        {{ p.pre_select_sql }},
        * EXCLUDE ({{ p.in_names_csv }})
    FROM casted
{%- if p.pre_lateral_sql != blank %}
    {{ p.pre_lateral_sql }}
{%- endif %}
), rules AS (
    SELECT rulepos FROM generate_series(1, {{ p.n_rules }}) r(rulepos)
)
SELECT
    preprocessed.*,
    '{{ p.decisiontable_hk }}' AS decisiontable_hk,
{%- if p.has_aggregate %}
    string_agg(ranked.rulepos::VARCHAR, ',' ORDER BY ranked.rulepos) AS rulepos,
    {{ p.post_select_sql }},
    max(ranked._total_hits_) AS _total_hits_,
    max(ranked._any_hitpolicy_violations_) AS _any_hitpolicy_violations_,
    CASE
        WHEN hit_policy = 'Unique' AND max(ranked._total_hits_) <> 1 THEN 'Model Error: More than one rule hit for this input!'
        WHEN hit_policy = 'Any' AND max(ranked._any_hitpolicy_violations_) > 0 THEN 'Model Error: Overlapping rules hit with different outputs!'
        WHEN max(ranked._total_hits_) IS NULL THEN 'No default rule defined'
        ELSE NULL
    END AS _model_error_
{%- else %}
    ranked.rulepos,
    {{ p.post_select_sql }},
    ranked._total_hits_,
    ranked._any_hitpolicy_violations_,
    CASE
        WHEN hit_policy = 'Unique' AND ranked._total_hits_ <> 1 THEN 'Model Error: More than one rule hit for this input!'
        WHEN hit_policy = 'Any' AND ranked._any_hitpolicy_violations_ > 0 THEN 'Model Error: Overlapping rules hit with different outputs!'
        WHEN ranked._total_hits_ IS NULL THEN 'No default rule defined'
        ELSE NULL
    END AS _model_error_
{%- endif %}
FROM
    preprocessed
    LEFT JOIN LATERAL (
        SELECT
            *,
            max(_hits_rank_ - 1) OVER () AS _any_hitpolicy_violations_
        FROM (
            SELECT
                *,
                row_number() OVER (ORDER BY rulepos) AS _hit_,
                count(*) OVER () AS _total_hits_,
                dense_rank() OVER (ORDER BY {{ p.out_names_csv }}) AS _hits_rank_
            FROM (
                SELECT
                    {{ p.out_case_sql }},
                    r.rulepos
                FROM
                    rules r
                WHERE
                    1=1
                    {{ p.in_where_sql }}
            ) _matched
        ) _ranked
        ORDER BY rulepos
    ) ranked ON COALESCE(hit_policy, '{{ hp }}') = 'RuleOrder' OR ranked._hit_ = 1
{%- if p.has_aggregate %}
GROUP BY ALL
{%- endif %}
{%- else -%}
CREATE OR REPLACE {{ tkw }}MACRO {{ params.name }}(input, hit_policy := '{{ hp }}') AS (
WITH params AS (
    SELECT
        {{ p.input_cast_sql }}
), preprocessed AS (
    SELECT
        {{ p.pre_select_sql }}
    FROM params
{%- if p.pre_lateral_sql != blank %}
    {{ p.pre_lateral_sql }}
{%- endif %}
), rules AS (
    SELECT rulepos FROM generate_series(1, {{ p.n_rules }}) r(rulepos)
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
), filtered AS (
    SELECT * FROM result
    WHERE COALESCE(hit_policy, '{{ hp }}') = 'RuleOrder' OR _hit_ = 1
), final_rows AS (
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
    rulepos::VARCHAR AS rulepos,
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
    FROM filtered
)
{%- if p.has_aggregate %}
{%- comment -%}
  Aggregate mode collapses every match into ONE row, so there is nothing for `many` to carry and the
  answer is always `single`.
{%- endcomment %}
SELECT union_value(single := {{ lit }})::{{ ut }}
FROM final_rows
{%- else %}
{%- comment -%}
  ⚠ BOTH branches are rendered and the HIT POLICY — a macro ARGUMENT, so not known until the call —
  picks one at runtime, which is why this is a UNION rather than two macros. `as_single` takes the
  first surviving row; under RuleOrder `filtered` kept them all and `as_many` lists them.
  ⚠⚠ THE `ORDER BY` INSIDE `list()` IS LOAD-BEARING: RuleOrder means every matching rule IN rulepos
  ORDER, and an unordered aggregate does not promise that — MEASURED returning 1, 3, 2. ⚠ And it casts,
  because `rulepos` is a VARCHAR by then (the UNION struct declares one type for it, and aggregate mode
  makes that a comma-joined list) — ordering it as text would put rule 10 before rule 2.
  ⚠ NO MATCH: `filtered` is empty, so under First the whole subquery yields no row and the macro
  returns NULL; under RuleOrder it returns a `many` whose list is NULL.
{%- endcomment %}
, as_single AS (
    SELECT {{ lit }} AS val FROM final_rows LIMIT 1
), as_many AS (
    SELECT list({{ lit }} ORDER BY CAST(rulepos AS INTEGER)) AS val FROM final_rows
)
SELECT (
    SELECT union_value(single := val)::{{ ut }}
    FROM as_single
    WHERE COALESCE(hit_policy, '{{ hp }}') != 'RuleOrder'
    UNION ALL
    SELECT union_value(many := val)::{{ ut }}
    FROM as_many
    WHERE COALESCE(hit_policy, '{{ hp }}') = 'RuleOrder'
    LIMIT 1
)
{%- endif %}
)
{%- endif -%}
$$, params := {'rules': rules, 'name': name, 'form': form, 'is_temporary': is_temporary,
               'source': source,
               'hit_policy': hit_policy});
