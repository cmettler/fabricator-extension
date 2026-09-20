-- decision_render_tsql(rules [, name := 'dbo.decision_eval'] [, form := 'scalar'] [, input := 'json']
--                            [, source := 'auto'] [, hit_policy := NULL])
--   Renders a DMN-like DECISION TABLE into the T-SQL of a SQL Server FUNCTION. The sibling of
--   decision_render, which renders DuckDB; the CELL PARSER is shared, only the statement differs.
--
--     SELECT decision_render_tsql('decision_rules', name := 'dbo.evaluate');
--     -- then run that text on SQL Server, e.g. with fabricator_exec('mycat', <the text>)
--
--   ⚠⚠ IT IS A SEPARATE MACRO ON PURPOSE (user-directed: *"i don't mind if we create a dedicated
--     decision_render_tsql to keep decision_render more readable"*). The two therefore DUPLICATE the
--     introspection, and they must be kept in step by hand. What is NOT duplicated is the part that
--     matters: dmn_condition / dmn_value / dmn_is_aggregate are the same C# parser, called with
--     `'tsql'`, so the CELL GRAMMAR cannot drift between the two engines.
--
-- ⚠⚠ BOTH FORMS TAKE **ONE INPUT ROW** (user-directed: *"we don't need a complete input table as json
--   here"*). That is the deliberate difference from the original engine's T-SQL template, whose
--   `@inputs` was a JSON ARRAY carrying an `inputid` per row — which is why nothing here needs that
--   column, nor the `OVER (PARTITION BY inputid, …)` it forced.
--
-- ⚠⚠ `input` — HOW THAT ROW ARRIVES, AND THE TRADE-OFF IS THE WHOLE REASON BOTH EXIST. It is
--   ORTHOGONAL to `form`, so all four combinations are renderable, and it is orthogonal because the
--   BODY IS SHARED: the only thing that changes is where the `params` derived table gets its values.
--
--   `input := 'json'` (the DEFAULT) — one `@input NVARCHAR(MAX)` document, read with `OPENJSON … WITH`.
--     ⚠⚠ ITS POINT IS THAT THE CALLER SURVIVES A RULES CHANGE (user's own reasoning): *"where you pass
--       a wide input with more fields than you need makes also sense. in such a case if business logic
--       changes only the decision rules are amended with e.g. new fields and the caller needs no
--       change."* Pass the whole record as JSON and the decision table takes what it wants; add an
--       input to the rules, re-render, and the CALL SITE IS UNTOUCHED. A field the rules never mention
--       simply rides along ignored — `OPENJSON … WITH` reads the keys it was told about.
--
--   `input := 'typed'` — ONE FUNCTION PARAMETER PER DECISION INPUT, typed from the datatypes row:
--
--       CREATE OR ALTER FUNCTION dbo.evaluate(@region VARCHAR(MAX), @age INT,
--                                             @hit_policy NVARCHAR(50) = 'First')
--
--       SELECT c.id, d.decision FROM customers c CROSS APPLY dbo.evaluate(c.region, c.age, DEFAULT) d;
--
--     Easier to call and to read, and the arguments are type-checked at CREATE time rather than at
--     OPENJSON time. ⚠ THE PRICE IS THE MIRROR OF THE ABOVE: the signature changes when the decision
--     table gains an input, so every call site must change with it.
--
--   ⚠ Whichever you pick, the ANSWER is identical — measured byte for byte on the scalar pair.
--
--   `form := 'scalar'` (the DEFAULT) — a SCALAR function: JSON in, **JSON out, always**.
--
--       CREATE OR ALTER FUNCTION dbo.evaluate(@input NVARCHAR(MAX), @hit_policy NVARCHAR(50) = 'First')
--       RETURNS NVARCHAR(MAX)
--
--       SELECT dbo.evaluate('{"region":"EU","age":30}', DEFAULT);
--       -- {"single":{"decisiontable_hk":"…","rulepos":1,"decision":"approve",…}}
--
--     ⚠ THE SHAPE MATCHES THE DuckDB SCALAR FORM EXACTLY, and that is worth the few lines it costs:
--       the same rules answer the same JSON on both engines, so a caller reads
--       `->'single'->>'decision'` either way. `{"single": {…}}` for First/Unique/Any and for EVERY
--       aggregate-mode call; `{"many": [ … ]}` for RuleOrder on a non-aggregate table.
--       ⚠ T-SQL has no UNION type, so the member is chosen at RUNTIME from @hit_policy and the array
--         `FOR JSON PATH` produced — except in aggregate mode, where it is baked to `single` at RENDER
--         time because the collapse has already happened.
--     ⚠ NO MATCH IS `NULL` — `FOR JSON PATH` over zero rows yields NULL. ⚠ It is NULL under RuleOrder
--       too, where the DuckDB form answers `{"many": null}`; a divergence, and the honest one, since
--       there is no row left to carry anything.
--
--   `form := 'table'` — an INLINE TABLE-VALUED FUNCTION over the same one input row:
--
--       CREATE OR ALTER FUNCTION dbo.evaluate(@input NVARCHAR(MAX), @hit_policy NVARCHAR(50) = 'First')
--       RETURNS TABLE
--
--       SELECT * FROM dbo.evaluate('{"region":"EU","age":30}', DEFAULT);
--       SELECT d.* FROM customers c CROSS APPLY dbo.evaluate(<json of c>, DEFAULT) d;   -- per row
--
--     It returns the preprocessed INPUT columns beside the decision, exactly as the DuckDB table form
--     does. Under RuleOrder it returns one ROW per matching rule rather than a JSON array.
--
--   `hit_policy` — the DEFAULT baked into the generated function; NULL (default) means `RuleOrder` in
--     aggregate mode and `First` otherwise. Both forms take `@hit_policy` as a parameter either way.
--
-- ⚠⚠ THE ONE THING T-SQL FORCES AND DuckDB DOES NOT: **THERE IS NO BOOLEAN TYPE**, so a CASE cannot
--   return a predicate. Every rule condition is wrapped `IIF(<cond>, 1, 0)` and the CASE is compared
--   `= 1`. Get that wrong and the generated function does not compile — loud, at CREATE time, which is
--   the one mercy of this difference.
--
-- ⚠ THE DATATYPES ROW MUST CARRY **T-SQL** TYPE NAMES — it becomes the `OPENJSON … WITH` column list
--   and the output CAST. `DOUBLE`, `BOOLEAN` and friends are DuckDB spellings and fail loudly at
--   CREATE (*"Type DOUBLE is not a defined system type"*); write `FLOAT`, `BIT`, `INT`, `DATE`.
--   ⚠⚠ ONE SPELLING IS FIXED RATHER THAN PASSED THROUGH, BECAUSE IT FAILS **SILENTLY**: a bare
--     `VARCHAR` or `NVARCHAR` in a column definition means length **ONE** in T-SQL, so a rules table
--     authored for DuckDB (where `VARCHAR` is THE string type) would truncate every input to its first
--     character with nothing failing anywhere. Both are widened to `(MAX)`. A length you wrote
--     yourself — `VARCHAR(50)` — is left exactly as written. ⚠ The ported engine passes these through
--     untouched; this is a deliberate deviation, and it is the only one in the type handling.
--
--   `rules` / `source` — identical to decision_render: a relation NAME, a FILE, JSON TEXT, or
--     `source := 'sql'` for a verbatim relation expression. See that macro's header.
--
-- ⚠ `GENERATE_SERIES` NEEDS SQL SERVER 2022+ (compatibility level 160). It is what keeps a fifty-rule
--   table from rendering fifty literals, and it is what the original engine used.
--
-- ⚠ `CREATE OR ALTER FUNCTION` must be the FIRST statement of its batch, so run the rendered text on
--   its own — not appended to another statement.
CREATE OR REPLACE MACRO decision_render_tsql(rules, name := 'dbo.decision_eval', form := 'scalar',
                                             input := 'json', source := 'auto',
                                             hit_policy := NULL::VARCHAR) AS
fluid_render($$
{%- comment -%}
  THE FORM. Same two names decision_render uses, and `batch` is NOT accepted here: there is no batch
  form in T-SQL — both of these take ONE input row.
{%- endcomment -%}
{%- assign fm = params.form | default: 'scalar' | downcase | strip -%}
{%- if fm != 'scalar' and fm != 'table' -%}
    {%- capture _m -%}decision_render_tsql: form '{{ params.form }}' is not one of 'scalar' (JSON in, JSON out) or 'table' (an inline table-valued function). Both take ONE input row as JSON.{%- endcapture -%}
    {%- query _e m: _m -%}SELECT 1 AS e WHERE error($m){%- endquery -%}
{%- endif -%}
{%- assign is_table = false -%}
{%- if fm == 'table' -%}{%- assign is_table = true -%}{%- endif -%}

{%- comment -%}
  ⚠⚠ `input` IS ORTHOGONAL TO `form`, so all four combinations exist — and it is orthogonal because the
  body is SHARED: the only thing that changes is where the `params` derived table gets its values, one
  line. Making it a third `form` value would have needed a fourth for the typed scalar.
{%- endcomment -%}
{%- assign inp = params.input | default: 'json' | downcase | strip -%}
{%- if inp != 'json' and inp != 'typed' -%}
    {%- capture _m -%}decision_render_tsql: input '{{ params.input }}' is not one of 'json' (ONE document, read with OPENJSON) or 'typed' (one function parameter per decision input).{%- endcapture -%}
    {%- query _e m: _m -%}SELECT 1 AS e WHERE error($m){%- endquery -%}
{%- endif -%}
{%- assign is_typed = false -%}
{%- if inp == 'typed' -%}{%- assign is_typed = true -%}{%- endif -%}

{%- comment -%}
  1/3 — the relation TEXT, and 2/3 the COLUMN NAMES. Identical to decision_render; see its header for
  why DESCRIBE rather than EXPLAIN, and why `rulepos` is matched case-insensitively.
{%- endcomment -%}
{%- query _rel rules: params.rules, src: params.source -%}
    SELECT dmn_relation($rules, $src) AS rel
{%- endquery -%}
{%- assign rel = _rel[0].rel -%}

{%- query _cols -%}
    SELECT column_name AS name
    FROM (DESCRIBE SELECT * FROM {{ rel }})
    WHERE lower(column_name) <> 'rulepos'
{%- endquery -%}

{%- if _cols.size == 0 -%}
    {%- capture _m -%}decision_render_tsql: {{ params.rules }} has no columns besides rulepos.{%- endcapture -%}
    {%- query _e m: _m -%}SELECT 1 AS e WHERE error($m){%- endquery -%}
{%- endif -%}

{%- comment -%}
  3/3 — the PLAN. ⚠⚠ THE INPUT REFERENCE PREFIX IS `i.` HERE, not `preprocessed.`: the inputs live in
  a derived table aliased `i`, and the rule predicates sit inside an OUTER APPLY correlated to it.
  Qualifying is load-bearing for the same reason it is in DuckDB's table form — an output column
  sharing an input's name would otherwise be reachable where the input was meant.
{%- endcomment -%}
{%- query _plan d: 'tsql' -%}
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
           nullif(trim(coalesce(dt_raw, '')), '') AS dt_raw,
           nullif(trim(coalesce(pre_raw, '')), '') AS pre,
           CASE WHEN starts_with(lower(col), 'in_') THEN substr(col, 4) ELSE col END AS param
    FROM meta
),
m1 AS (
    -- ⚠⚠ THE ONE TYPE FIX, and it exists because the failure is SILENT: a bare VARCHAR/NVARCHAR in a
    -- T-SQL column definition means length ONE, so a rules table authored for DuckDB would truncate
    -- every input to one character. A length the author wrote is left exactly as written.
    SELECT m.*,
           CASE WHEN dt_raw IS NULL THEN NULL
                WHEN lower(dt_raw) = 'varchar'  THEN 'VARCHAR(MAX)'
                WHEN lower(dt_raw) = 'nvarchar' THEN 'NVARCHAR(MAX)'
                ELSE dt_raw END AS dt
    FROM m
),
m2 AS (
    SELECT m1.*,
           CASE WHEN pre IS NULL THEN NULL
                ELSE regexp_replace(replace(pre, '?', param), '@(\w+)', '\1', 'g') END AS in_pre,
           CASE WHEN pre IS NULL THEN NULL
                ELSE regexp_replace(pre, '@(\w+)', '\1', 'g') END AS out_pre,
           dmn_is_aggregate(pre) AS agg
    FROM m1
),
m3 AS (
    SELECT m2.*,
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
           CASE WHEN m3.dir = 'in'  THEN dmn_condition(c.cell, 'i.' || m3.param, $d, 'i.')
                WHEN c.cell = ''    THEN 'NULL'
                ELSE dmn_value(c.cell, $d, 'i.') END AS sql
    FROM cells c JOIN m3 ON m3.col = c.col
    WHERE m3.dir IN ('in', 'out')
),
kept AS (
    SELECT col, ord, dt,
           CASE WHEN out_pre IS NULL AND dt IS NULL THEN NULL
                WHEN dt IS NULL THEN '(' || out_pre || ')'
                ELSE 'CAST(' || coalesce('(' || out_pre || ')', '?') || ' AS ' || dt || ')'
           END AS post
    FROM m3
    WHERE dir = 'out' AND NOT (has_aggregate AND out_pre IS NULL)
)
SELECT
    (SELECT string_agg(col, ', ' ORDER BY ord) FROM m3
     WHERE dir IN ('in','out')
       AND (NOT regexp_matches(col, '^[A-Za-z_][A-Za-z0-9_]*$')
            OR NOT regexp_matches(param, '^[A-Za-z_][A-Za-z0-9_]*$'))) AS bad_cols,
    (SELECT count(*) FROM m3 WHERE dir = 'in')  AS n_inputs,
    (SELECT count(*) FROM m3 WHERE dir = 'out') AS n_outputs,
    (SELECT count(*) FROM rp)                   AS n_rules,
    (SELECT bool_or(has_aggregate) FROM m3)     AS has_aggregate,
    (SELECT CASE WHEN count(*) = max(rulepos) AND min(rulepos) = 1 THEN NULL
                 ELSE string_agg(rulepos::VARCHAR, ', ' ORDER BY rulepos) END
     FROM rp) AS bad_rulepos,
    (SELECT string_agg(col, ', ' ORDER BY ord) FROM m3 WHERE dir = 'out')     AS out_names_csv,
    (SELECT string_agg(param, ', ' ORDER BY ord) FROM m3 WHERE dir = 'in')    AS in_names_csv,

    -- the OPENJSON WITH column list: one input per line, typed, read from its own JSON key
    (SELECT string_agg(param || ' ' || coalesce(dt, 'NVARCHAR(MAX)') || ' ''$.' || param || '''',
                       ',' || chr(10) || '            ' ORDER BY ord)
     FROM m3 WHERE dir = 'in') AS openjson_with_sql,

    -- `input := 'typed'` only: one FUNCTION PARAMETER per decision input, and the SELECT that turns
    -- them back into the same `params` relation the JSON form builds with OPENJSON.
    (SELECT string_agg('@' || param || ' ' || coalesce(dt, 'NVARCHAR(MAX)'),
                       ',' || chr(10) || '    ' ORDER BY ord)
     FROM m3 WHERE dir = 'in') AS typed_args_sql,
    (SELECT string_agg('@' || param || ' AS ' || param, ',' || chr(10) || '                ' ORDER BY ord)
     FROM m3 WHERE dir = 'in') AS typed_select_sql,

    -- the preprocessed SELECT list, and the APPLYs a SELECT-shaped preprocessing needs
    (SELECT string_agg(CASE WHEN is_lateral THEN '_cj_' || param || '.' || param
                            ELSE coalesce('(' || in_pre || ')', param) || ' AS ' || param END,
                       ',' || chr(10) || '                ' ORDER BY ord)
     FROM m3 WHERE dir = 'in') AS pre_select_sql,

    -- ⚠ OUTER, not CROSS, APPLY — parity with DuckDB's LEFT JOIN LATERAL … ON TRUE, which keeps the
    -- row when the expansion yields nothing. (The ported engine used CROSS APPLY, which drops it.)
    (SELECT string_agg('OUTER APPLY (' || in_pre || ') AS _cj_' || param || '(' || param || ')',
                       chr(10) || '            ' ORDER BY ord)
     FROM m3 WHERE dir = 'in' AND is_lateral) AS pre_lateral_sql,

    -- one CASE per OUTPUT column
    (SELECT string_agg(body, ',' || chr(10) || '                        ' ORDER BY ord)
     FROM (SELECT col, ord,
                  'CASE r.rulepos'
                  || string_agg(chr(10) || '                            WHEN ' || rulepos || ' THEN ' || sql, ''
                                ORDER BY rulepos)
                  || chr(10) || '                        END AS ' || col AS body
           FROM parsed WHERE dir = 'out' GROUP BY col, ord)) AS out_case_sql,

    -- ⚠⚠ one CASE per INPUT column, AND THIS IS THE T-SQL SHAPE: there is no boolean type, so a CASE
    -- cannot yield a predicate. Each condition is wrapped IIF(<cond>,1,0) and the CASE compared `= 1`.
    -- ⚠ Wildcard cells are omitted, so a column whose every cell is blank produces no group at all —
    -- which is what keeps `CASE … ELSE` legal and the statement free of dead predicates.
    (SELECT string_agg(body, chr(10) || '                        ' ORDER BY ord)
     FROM (SELECT col, ord,
                  'AND CASE r.rulepos'
                  || string_agg(chr(10) || '                            WHEN ' || rulepos
                                || ' THEN IIF(' || sql || ', 1, 0)', '' ORDER BY rulepos)
                  || chr(10) || '                            ELSE 1'
                  || chr(10) || '                        END = 1' AS body
           FROM parsed WHERE dir = 'in' AND NOT wild GROUP BY col, ord)) AS in_where_sql,

    -- the result's output list: postprocessing, then the datatype cast, `?` = the column, read out of
    -- the OUTER APPLY that computed it
    (SELECT string_agg(CASE WHEN post IS NULL THEN 'ranked.' || col
                            ELSE replace(post, '?', 'ranked.' || col) || ' AS ' || col END,
                       ',' || chr(10) || '            ' ORDER BY ord)
     FROM kept) AS post_select_sql,

    -- the table's identity, byte for byte the same hash decision_render computes
    (SELECT md5(string_agg(t, chr(10) ORDER BY t)) FROM (
        SELECT concat_ws(chr(31), coalesce("rulepos"::VARCHAR, '')
               {%- for c in _cols %}, coalesce({{ c.name | sql_ident }}::VARCHAR, ''){% endfor %}) AS t
        FROM r)) AS decisiontable_hk
{%- endquery -%}
{%- assign p = _plan[0] -%}

{%- comment -%}
  The refusals, word for word decision_render's where the cause is the same — a decision table is
  wrong in the same ways whichever engine it is rendered for.
{%- endcomment -%}
{%- if p.bad_cols != blank -%}
    {%- capture _m -%}decision_render_tsql: the decision columns must be bare identifiers to be JSON keys and T-SQL column names, but these are not: {{ p.bad_cols }}.{%- endcapture -%}
    {%- query _e m: _m -%}SELECT 1 AS e WHERE error($m){%- endquery -%}
{%- elsif p.n_inputs == 0 -%}
    {%- capture _m -%}decision_render_tsql: no column is marked 'in' by the direction row (rulepos = -2) of {{ params.rules }}.{%- endcapture -%}
    {%- query _e m: _m -%}SELECT 1 AS e WHERE error($m){%- endquery -%}
{%- elsif p.n_outputs == 0 -%}
    {%- capture _m -%}decision_render_tsql: no column is marked 'out' by the direction row (rulepos = -2) of {{ params.rules }}.{%- endcapture -%}
    {%- query _e m: _m -%}SELECT 1 AS e WHERE error($m){%- endquery -%}
{%- elsif p.bad_rulepos != blank -%}
    {%- capture _m -%}decision_render_tsql: rulepos is the rule ORDERING and must run 1..N with no gaps, but {{ params.rules }} has {{ p.bad_rulepos }}. A gap would generate a rule slot matching every input and returning NULLs.{%- endcapture -%}
    {%- query _e m: _m -%}SELECT 1 AS e WHERE error($m){%- endquery -%}
{%- elsif p.n_rules == 0 -%}
    {%- capture _m -%}decision_render_tsql: {{ params.rules }} has metadata rows but no rules (no row with rulepos >= 1).{%- endcapture -%}
    {%- query _e m: _m -%}SELECT 1 AS e WHERE error($m){%- endquery -%}
{%- endif -%}

{%- assign hp = params.hit_policy -%}
{%- if hp == nil or hp == blank -%}
    {%- if p.has_aggregate -%}{%- assign hp = 'RuleOrder' -%}{%- else -%}{%- assign hp = 'First' -%}{%- endif -%}
{%- endif -%}

{%- comment -%}
  ⚠⚠ THE BODY IS BUILT FROM DERIVED TABLES, NOT CTEs, AND THAT IS FORCED: the scalar form needs it
  inside `RETURN ( … FOR JSON PATH )`, and T-SQL does not allow a WITH clause inside a scalar
  subquery. Written this way ONE body serves both forms — the table form simply returns it.
  ⚠ The input columns ride through only in the TABLE form, matching the DuckDB pair: its table form
  passes them through and its scalar form does not.
{%- endcomment -%}
{%- capture body -%}
    SELECT
        '{{ p.decisiontable_hk }}' AS decisiontable_hk,
        *,
        CAST(CASE
            WHEN @hit_policy = 'Unique' AND _total_hits_ <> 1 THEN 'Model Error: More than one rule hit for this input!'
            WHEN @hit_policy = 'Any' AND _any_hitpolicy_violations_ > 0 THEN 'Model Error: Overlapping rules hit with different outputs!'
            WHEN _total_hits_ IS NULL THEN 'No default rule defined'
            ELSE NULL
        END AS VARCHAR(100)) AS _model_error_
    FROM (
        SELECT
{%- if is_table %}
            i.*,
{%- endif %}
{%- if p.has_aggregate %}
            STRING_AGG(CAST(ranked.rulepos AS VARCHAR(MAX)), ',') WITHIN GROUP (ORDER BY ranked.rulepos) AS rulepos,
            {{ p.post_select_sql }},
            MAX(ranked._total_hits_) AS _total_hits_,
            MAX(ranked._any_hitpolicy_violations_) AS _any_hitpolicy_violations_
{%- else %}
{%- comment -%}
  ⚠ rulepos is a STRING in the scalar form and an INTEGER in the table form, which is not an accident:
  it mirrors the DuckDB pair exactly. There the scalar's UNION struct must declare ONE type for it, and
  aggregate mode makes that a comma-joined list, so VARCHAR wins; the table form has no such constraint.
{%- endcomment -%}
{%- if is_table %}
            ranked.rulepos,
{%- else %}
            CAST(ranked.rulepos AS VARCHAR(MAX)) AS rulepos,
{%- endif %}
            {{ p.post_select_sql }},
            ranked._total_hits_,
            ranked._any_hitpolicy_violations_
{%- endif %}
        FROM (
            SELECT
                {{ p.pre_select_sql }}
{%- if is_typed %}
            FROM (SELECT
                {{ p.typed_select_sql }}
            ) AS params
{%- else %}
            FROM OPENJSON(@input)
            WITH (
            {{ p.openjson_with_sql }}
            ) AS params
{%- endif %}
{%- if p.pre_lateral_sql != blank %}
            {{ p.pre_lateral_sql }}
{%- endif %}
        ) AS i
        OUTER APPLY (
            SELECT
                *,
                MAX(_hit_) OVER () AS _total_hits_,
                MAX(_hits_rank_ - 1) OVER () AS _any_hitpolicy_violations_
            FROM (
                SELECT
                    *,
                    ROW_NUMBER() OVER (ORDER BY rulepos) AS _hit_,
                    DENSE_RANK() OVER (ORDER BY {{ p.out_names_csv }}) AS _hits_rank_
                FROM (
                    SELECT
                        {{ p.out_case_sql }},
                        r.rulepos
                    FROM (SELECT value AS rulepos FROM GENERATE_SERIES(1, {{ p.n_rules }})) AS r
                    WHERE
                        1=1
                        {{ p.in_where_sql }}
                ) AS _matched
            ) AS _ranked
        ) AS ranked
        WHERE
            COALESCE(@hit_policy, '{{ hp }}') = 'RuleOrder' OR ranked._hit_ = 1
{%- if p.has_aggregate and is_table %}
        GROUP BY {{ p.in_names_csv }}
{%- endif %}
    ) AS result
{%- endcapture -%}

{%- if is_table -%}
CREATE OR ALTER FUNCTION {{ params.name }}(
{%- if is_typed %}
    {{ p.typed_args_sql }},
{%- else %}
    @input NVARCHAR(MAX),
{%- endif %}
    @hit_policy NVARCHAR(50) = '{{ hp }}'
)
RETURNS TABLE
AS
RETURN
(
{{ body }}
)
{%- else -%}
CREATE OR ALTER FUNCTION {{ params.name }}(
{%- if is_typed %}
    {{ p.typed_args_sql }},
{%- else %}
    @input NVARCHAR(MAX),
{%- endif %}
    @hit_policy NVARCHAR(50) = '{{ hp }}'
)
RETURNS NVARCHAR(MAX)
AS
BEGIN
    DECLARE @rows NVARCHAR(MAX) = (
{{ body }}
{%- comment -%}
  ⚠⚠ THE ORDER BY IS LOAD-BEARING AND THE PORTED ENGINE HAS NO EQUIVALENT: `RuleOrder` means every
  matching rule IN rulepos ORDER, and `FOR JSON PATH` has no inherent order. MEASURED without it on
  SQL Server 2025: rules came back 1, 3, 2. ⚠ It is legal HERE only because FOR JSON is present —
  T-SQL forbids ORDER BY in a subquery otherwise, which is exactly why the TABLE form below cannot
  carry one and its caller must order (a relation has no inherent order anyway).
  ⚠ It CASTS because rulepos is a VARCHAR in this form (parity with DuckDB's UNION struct) — ordering
  it as text would put rule 10 before rule 2.
  ⚠ Omitted in aggregate mode, where the collapse has already left exactly one row.
{%- endcomment -%}
{%- unless p.has_aggregate %}
    ORDER BY CAST(rulepos AS INT)
{%- endunless %}
{%- comment -%}
  ⚠⚠ INCLUDE_NULL_VALUES, because FOR JSON PATH DROPS NULL KEYS BY DEFAULT — measured: without it
  `_model_error_` simply vanishes from the object whenever there is no error, i.e. almost always, and
  a blank output cell vanishes with it. DuckDB's to_json keeps both as `null`, so this is what makes
  the two engines answer the SAME JSON rather than merely similar JSON.
{%- endcomment %}
    FOR JSON PATH, INCLUDE_NULL_VALUES
    );
{%- comment -%}
  ⚠⚠ THE MEMBER NAME IS CHOSEN THE SAME WAY DuckDB's UNION TAG IS, so the two engines answer the same
  JSON. In aggregate mode every match has already been collapsed into one row, so it is `single`
  whatever the policy says — baked here at RENDER time, exactly as the DuckDB form bakes it.
  ⚠ JSON_QUERY(@rows, '$[0]') takes the first element AS JSON rather than as a string, which is what
  keeps the object from being re-escaped inside the wrapper.
{%- endcomment -%}
{%- if p.has_aggregate %}
    RETURN CASE WHEN @rows IS NULL THEN NULL
                ELSE CONCAT('{"single":', JSON_QUERY(@rows, '$[0]'), '}') END;
{%- else %}
    RETURN CASE WHEN @rows IS NULL THEN NULL
                WHEN COALESCE(@hit_policy, '{{ hp }}') = 'RuleOrder'
                     THEN CONCAT('{"many":', @rows, '}')
                ELSE CONCAT('{"single":', JSON_QUERY(@rows, '$[0]'), '}') END;
{%- endif %}
END
{%- endif -%}
$$, params := {'rules': rules, 'name': name, 'form': form, 'input': input, 'source': source,
               'hit_policy': hit_policy});
