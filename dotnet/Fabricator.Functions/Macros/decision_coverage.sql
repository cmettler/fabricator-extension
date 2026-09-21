-- decision_coverage(rules [, source := 'auto'] [, max_cases := 100000]) -> TABLE
--   Finds the input combinations a decision table FORGETS or DOUBLE-COVERS, by generating boundary
--   cases from the rules themselves and evaluating every one.
--
--     SELECT * FROM decision_coverage('decision_rules');
--     -- issue   | region | age | _total_hits_ | _any_hitpolicy_violations_
--     -- gap     | EU     | 18  | NULL         | NULL
--     -- overlap | EU     | 21  | 2            | 1
--
--   `issue` is `gap` (NO rule matches — the decision is NULL and nobody asked for that) or `overlap`
--   (several rules match and DISAGREE, so which one wins depends on rule order).
--   An empty result means every generated case is covered exactly once, or covered consistently.
--
-- ⚠⚠ WHY THIS EXISTS, AND WHAT IT IS NOT. The strict audit answers *"is this cell understood"*. It says
--   nothing about *"is every input covered"*, which is the harder half and the one DMN is generally bad
--   at: a forgotten combination fails SILENTLY, as a NULL decision or as whichever rule happened to be
--   first. This is not a proof of completeness — it is a finite, mechanical search of the places errors
--   actually live.
--
-- ⚠⚠ HOW THE CASES ARE CHOSEN, because that is the whole method: THE RULES MINE THEMSELVES. Each input
--   cell is rendered to SQL and its LITERALS are read back out — so the values tried are exactly the
--   ones the rules talk about, plus the points just either side of them:
--       every string literal appearing in that column's cells        'EU', 'US'
--       every number, AND number-1 AND number+1                      18 → 17, 18, 19
--       NULL                                                          (the case everyone forgets)
--       one value matching no literal, for string columns             '__dmn_no_match__'
--   ⚠ THE ±1 IS THE POINT FOR BANDS: `[0 .. 17]` and `[21 .. 65]` look complete until you try 18. The
--     measured example found 18 and 20 in exactly that hole, plus -1, 66 and NULL outside it.
--   ⚠ NULL is included deliberately and will often be reported as a gap. That is usually TRUE and worth
--     seeing: a decision table whose rules are all comparisons decides nothing for a missing input.
--
-- ⚠ IT IS A CROSS PRODUCT, so the case count is the product of the per-column counts and it grows fast.
--   `max_cases` (default 100000) refuses rather than hanging, naming the count — past four or five
--   inputs you want pairwise coverage instead, which this does not do.
--
-- ⚠ It renders the TABLE form into a TEMPORARY macro named by the rules' own md5 and evaluates all the
--   cases in ONE pass (~7 us a row), so the cost is dominated by the render, not the search.
CREATE OR REPLACE MACRO decision_coverage(rules, source := 'auto', max_cases := 100000) AS TABLE
-- ⚠⚠ THIS ONE MUST STAY ON `fluid_query`, unlike `decision_check`, and the reason is the temp
--   macro below: the render `exec()`s `fab_cov_<md5>` and the generated SQL CALLS it. Under
--   `fluid_replacement_query` (bind_replace) the render runs on its own connection while the
--   spliced SQL runs on the CALLER's, so the macro is invisible — MEASURED: *"Table Function with
--   name fab_cov_<md5> does not exist!"* at the first real query.
--   ⚠ The cost of staying here is REAL: this function is therefore exposed to the pushdown crash in
--     known-limitations.md §1.27 (a pushable filter beside a differently-typed `OR` invalidates the
--     database). Filter its output in an outer query, or in two steps, until that is fixed.
--   ⚠ Escaping it needs decision_render to emit the table form's BODY as an inlinable subquery
--     rather than a CREATE MACRO — a new render mode, not a substitution.
SELECT * FROM fluid_query($$
{%- comment -%}
  1/3 — the relation, and the column names. Same two steps decision_render takes, for the same reasons.
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

{%- comment -%}
  2/3 — THE CASE VALUES, MINED OUT OF THE RULES THEMSELVES. Each cell is rendered to SQL by the same
  parser the generated macro uses, and the literals are read back out of the RENDERED text — which is
  why a FEEL range `[0 .. 17]` yields 0 and 17 without this query knowing anything about ranges.
  ⚠ The cell is rendered against the dummy column `c` so the column's own name cannot contribute digits
  to the numeric match.
{%- endcomment -%}
{%- query _vals -%}
WITH r AS (SELECT * FROM {{ rel }}),
meta AS (
    {%- for c in _cols %}
    {% unless forloop.first %}UNION ALL {% endunless %}SELECT {{ c.name | sql }} AS col, {{ forloop.index }} AS ord,
        max(CASE WHEN "rulepos"::INTEGER = -2 THEN {{ c.name | sql_ident }}::VARCHAR END) AS dir_raw,
        max(CASE WHEN "rulepos"::INTEGER = -1 THEN {{ c.name | sql_ident }}::VARCHAR END) AS dt_raw
    FROM r
    {%- endfor %}
),
m AS (
    SELECT col, ord,
           lower(coalesce(trim(dir_raw), '')) AS dir,
           coalesce(nullif(trim(coalesce(dt_raw, '')), ''), 'VARCHAR') AS dt,
           CASE WHEN starts_with(lower(col), 'in_') THEN substr(col, 4) ELSE col END AS param
    FROM meta
),
cells AS (
    {%- for c in _cols %}
    {% unless forloop.first %}UNION ALL {% endunless %}SELECT {{ c.name | sql }} AS col,
        coalesce(trim({{ c.name | sql_ident }}::VARCHAR), '') AS cell
    FROM r WHERE "rulepos"::INTEGER >= 1
    {%- endfor %}
),
rendered AS (
    SELECT m.col, m.ord, m.param, m.dt,
           dmn_condition(c.cell, 'c', 'duckdb', '') AS sql
    FROM cells c JOIN m ON m.col = c.col
    WHERE m.dir = 'in' AND c.cell NOT IN ('', '*')
),
mined AS (
    SELECT col, ord, param, dt,
           list_distinct(flatten(list(regexp_extract_all(sql, '''([^'']*)''', 1)))) AS strs,
           list_distinct(flatten(list(regexp_extract_all(sql, '(-?\d+\.?\d*)', 1)))) AS nums
    FROM rendered GROUP BY col, ord, param, dt
),
vals AS (
    SELECT col, ord, param, dt,
           -- strings as SQL literals; numbers with their neighbours; NULL always; and a never-matching
           -- string ONLY for columns that actually carry string literals (it would not cast otherwise)
           list_distinct(
               list_transform(strs, lambda s: '''' || replace(s, '''', '''''') || '''')
               || flatten(list_transform(nums, lambda n: [n,
                                                     (n::DOUBLE - 1)::VARCHAR,
                                                     (n::DOUBLE + 1)::VARCHAR]))
               || CASE WHEN len(strs) > 0 THEN ['''__dmn_no_match__'''] ELSE [] END
               || ['NULL']) AS toks
    FROM mined
)
SELECT param, dt, ord,
       '[' || list_aggregate(toks, 'string_agg', ', ') || ']' AS vals_sql,
       len(toks) AS n
FROM vals ORDER BY ord
{%- endquery -%}

{%- comment -%}
  ⚠⚠ WHICH RULES ARE CATCH-ALLS — every input cell blank or `*`. MEASURED NECESSARY: a catch-all
  overlaps EVERY other rule and disagrees with it, so without this a perfectly ordinary table (bands
  plus a final `otherwise`) reports an "overlap" for every case it covers. Under `First` that overlap is
  the INTENT, and a checker that flags the commonest idiom in the language trains you to ignore it.
  ⇒ an overlap counts only when TWO SPECIFIC rules match.
{%- endcomment -%}
{%- query _wild -%}
WITH r AS (SELECT * FROM {{ rel }}),
dirs AS (
    {%- for c in _cols %}
    {% unless forloop.first %}UNION ALL {% endunless %}SELECT {{ c.name | sql }} AS col,
        max(CASE WHEN "rulepos"::INTEGER = -2 THEN lower(trim({{ c.name | sql_ident }}::VARCHAR)) END) AS dir
    FROM r
    {%- endfor %}
),
cells AS (
    {%- for c in _cols %}
    {% unless forloop.first %}UNION ALL {% endunless %}SELECT {{ c.name | sql }} AS col, "rulepos"::INTEGER AS rulepos,
        coalesce(trim({{ c.name | sql_ident }}::VARCHAR), '') AS cell
    FROM r WHERE "rulepos"::INTEGER >= 1
    {%- endfor %}
)
SELECT string_agg(rulepos::VARCHAR, ', ' ORDER BY rulepos) AS csv
FROM (SELECT c.rulepos FROM cells c JOIN dirs d ON d.col = c.col
      WHERE d.dir = 'in' GROUP BY c.rulepos
      HAVING bool_and(c.cell IN ('', '*')))
{%- endquery -%}
{%- assign wild_csv = _wild[0].csv -%}

{%- if _vals.size == 0 -%}
    {%- capture _m -%}decision_coverage: {{ params.rules }} has no column marked 'in' by the direction row (rulepos = -2), so there is nothing to generate cases for.{%- endcapture -%}
    {%- query _e m: _m -%}SELECT 1 AS e WHERE error($m){%- endquery -%}
{%- endif -%}

{%- comment -%}
  The size guard. ⚠ A CROSS PRODUCT, so this is the product of the per-column counts — it is refused
  rather than run, because the failure mode otherwise is a query that looks like it hung.
{%- endcomment -%}
{%- assign total = 1 -%}
{%- for v in _vals -%}{%- assign total = total | times: v.n -%}{%- endfor -%}
{%- if total > params.max_cases -%}
    {%- capture _m -%}decision_coverage: the {{ _vals.size }} inputs of {{ params.rules }} generate {{ total }} cases, over the max_cases limit of {{ params.max_cases }}. Raise max_cases, or cover fewer inputs at a time — past four or five inputs a full cross product is the wrong tool.{%- endcapture -%}
    {%- query _e m: _m -%}SELECT 1 AS e WHERE error($m){%- endquery -%}
{%- endif -%}

{%- comment -%}
  3/3 — render the TABLE form, stage the cases, evaluate them all in one pass.
  ⚠ Both the macro and the cases table are TEMPORARY and live on THIS render session's own connection —
  which is also the connection that runs the statement below, so they are visible exactly where they
  are needed and gone afterwards. The same property decision_eval relies on.
  ⚠ The macro is named by the rules' md5 so two coverage runs in one session cannot collide.
{%- endcomment -%}
{%- query _hk -%}
    SELECT md5(string_agg(t, chr(10) ORDER BY t)) AS hk FROM (
        SELECT concat_ws(chr(31), coalesce("rulepos"::VARCHAR, '')
               {%- for c in _cols %}, coalesce({{ c.name | sql_ident }}::VARCHAR, ''){% endfor %}) AS t
        FROM {{ rel }})
{%- endquery -%}
{%- capture mac -%}fab_cov_{{ _hk[0].hk }}{%- endcapture -%}
{%- capture tbl -%}fab_cov_cases_{{ _hk[0].hk }}{%- endcapture -%}

{%- query _r rr: params.rules, src: params.source, n: mac -%}
    SELECT decision_render($rr, name := $n, form := 'table', is_temporary := true, source := $src) AS s
{%- endquery -%}
{%- assign _created = exec(_r[0].s) -%}

{%- capture _cases -%}
CREATE OR REPLACE TEMPORARY TABLE {{ tbl }} AS
SELECT
{%- for v in _vals %}
    a{{ forloop.index }}.v::{{ v.dt }} AS {{ v.param }}{% unless forloop.last %},{% endunless %}
{%- endfor %}
FROM
{%- for v in _vals %}
    (SELECT unnest({{ v.vals_sql }}::VARCHAR[]) AS v) AS a{{ forloop.index }}{% unless forloop.last %},{% endunless %}
{%- endfor %}
{%- endcapture -%}
{%- assign _staged = exec(_cases) -%}

WITH ev AS (
    SELECT
{%- for v in _vals %}
        {{ v.param }},
{%- endfor %}
        rulepos
    FROM {{ mac }}('{{ tbl }}', hit_policy := 'RuleOrder')
), agg AS (
    SELECT
{%- for v in _vals %}
        {{ v.param }},
{%- endfor %}
        list(rulepos ORDER BY rulepos) FILTER (WHERE rulepos IS NOT NULL) AS matched_rules,
        count(*) FILTER (WHERE rulepos IS NOT NULL
{%- if wild_csv != blank %}
                         AND rulepos NOT IN ({{ wild_csv }})
{%- endif %}
                        ) AS n_specific
    FROM ev GROUP BY ALL
)
SELECT
    CASE WHEN matched_rules IS NULL OR len(matched_rules) = 0 THEN 'gap' ELSE 'overlap' END AS issue,
{%- for v in _vals %}
    {{ v.param }},
{%- endfor %}
    coalesce(matched_rules, []) AS matched_rules
FROM agg
WHERE matched_rules IS NULL OR len(matched_rules) = 0 OR n_specific > 1
ORDER BY 1{% for v in _vals %}, {{ forloop.index | plus: 1 }}{% endfor %}
$$, {'rules': rules, 'source': source, 'max_cases': max_cases});
