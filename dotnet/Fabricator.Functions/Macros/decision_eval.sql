-- decision_eval(def, input [, hit_policy := NULL]) -> JSON
--   Evaluates a DMN-like DECISION TABLE that arrives AS DATA, one per row, and answers JSON.
--   Where decision_render hands back SQL TEXT for a decision table you know at authoring time, this
--   one takes the table's DEFINITION as a per-row value — so a thousand rows may carry a thousand
--   different rule sets and each is decided by its own.
--
--     SELECT r.id, decision_eval(r.rules_json, {'region': r.region, 'age': r.age})
--     FROM records r;
--     -- {"single":{"decisiontable_hk":"…","rulepos":"1","decision":"approve",…}}
--
--   `def`    — the decision table as JSON TEXT: an array of objects, one per row, exactly the shape
--              decision_render's JSON source takes. NULL ⇒ the answer is NULL.
--   `input`  — a STRUCT carrying one field per INPUT the table declares.
--   `hit_policy` — per row, and NULL (the default) means the table's own default: `RuleOrder` in
--              aggregate mode, `First` otherwise.
--
-- ⚠⚠ THE RESULT IS ALWAYS `JSON`, WHATEVER THE DEFINITION SAYS — AND THAT IS WHAT MAKES THE FUNCTION
--   POSSIBLE AT ALL. A scalar's result type is fixed at BIND, where no row value exists, so a
--   function whose answer depended on a per-row definition could not be typed. JSON is the one type
--   that describes every decision table at once. `to_json` over the generated macro's UNION carries
--   the hit policy's shape in the KEY, so the caller can still tell them apart:
--       {"single": {decisiontable_hk, rulepos, <your outputs>, _total_hits_, …}}   First/Unique/Any
--       {"many":  [{…}, {…}]}                                                      RuleOrder
--   Read it with DuckDB's JSON operators: `decision_eval(…)->'single'->>'decision'`.
--
-- ⚠⚠ THE INPUT STRUCT MUST CARRY EVERY INPUT THE DEFINITION NAMES, and it is ONE struct type for the
--   whole call site — a column has one type. So the defs varying per row must agree on their INPUTS;
--   what varies is the RULES. That is the shape this serves (per-tenant or per-version rule sets over
--   one record shape), and a def naming an input the struct lacks fails loudly at that chunk.
--
-- ⚠⚠ HOW IT WORKS, AND THE COST — because the cost is per BATCH and is worth knowing before you reach
--   for this on a large table. For each chunk of rows DuckDB hands it, the template:
--       1. reads the DISTINCT definitions out of the chunk,
--       2. renders each one (decision_render) into a TEMPORARY macro named by the definition's md5,
--       3. emits ONE SELECT that dispatches each row to its own macro and casts the result to JSON.
--   ⚠ Step 2 REPEATS PER CHUNK. A `fluid_scalar` session opens a FRESH CONNECTION per chunk (its
--     binding is shared across pipeline threads and a DuckDB connection is single-threaded), so the
--     temporary macros die with it and nothing carries from one chunk to the next.
--   MEASURED on one distinct definition, 2048-row chunks, warm: **~54 ms per chunk**, i.e. ~26 us a
--   row — 20480 rows in 0.65 s. The split, measured separately over 20 calls each, is what decides
--   every optimisation anyone will propose for this:
--       the RENDER      **32 ms**    per distinct definition per chunk
--       the CREATE      **0.75 ms**  — essentially free
--       a host round trip **0.35 ms** — so the render's 32 ms is the render, not the plumbing
--       the rest (~21 ms) is binding and running the generated SELECT, of which ~14 ms is the
--       decision evaluation itself and is irreducible
--   ⚠⚠ SO INLINING THE DECISIONS INTO ONE BIG SELECT INSTEAD OF CREATING MACROS SAVES 0.75 ms OF 54 —
--     about 1.4%. It is the obvious idea and the measurement says no: the DDL was never the cost.
--   ⚠⚠ AND CACHING THE RENDER IS THE ONLY LEVER THAT MATTERS, worth up to ~32 ms of 54 (≈2.5x) — but
--     it needs a store that SURVIVES A CHUNK, which is exactly what this surface does not have. The
--     obvious store nearly cancels itself: creating the macros PERSISTENTLY and skipping the render
--     when `duckdb_functions()` already has the name moves the slope only 53.6 -> 45.4 ms (~15%),
--     because that catalog lookup costs ~19 ms on its own — a cache whose lookup costs most of what
--     it saves. It also makes a SELECT write database objects, which is a surprise nobody asked for.
--     A real in-memory cache belongs in a C# scalar function, where the store is an ordinary static
--     dictionary and the lookup is free; that is the version to build if 26 us a row is too slow.
--   ⚠⚠ SINCE 2026-09-20 EACH RENDER ALSO RUNS decision_render's STRICT AUDIT, and it is ON here on
--     purpose: a definition arriving as DATA is exactly the one nobody rendered by hand, so it is the
--     one most likely to carry a cell the parser misreads. MEASURED cost of leaving it on: 20480 rows
--     0.647 -> **0.817 s**, i.e. ~17 ms per chunk (+26%), because the audit re-answers the same
--     question once per chunk. ⇒ if that bites, the obvious next step is to let this macro pass
--     `strict` through so a caller who has already validated a definition can turn it off.
--   => **If every row shares ONE decision table, do not use this.** Render it once with
--     decision_render and call the macro — `form := 'table'` decides a whole relation in one pass and
--     was measured at ~7 us a row, roughly four times faster.
CREATE OR REPLACE MACRO decision_eval(def, input, hit_policy := NULL::VARCHAR) AS
fluid_scalar($$
{%- comment -%}
  ⚠ THE BIND RENDER IS TRIVIAL AND THAT IS THE WHOLE TRICK: the result type is JSON for every
  definition, so nothing here has to look at data that does not exist yet. `input_table` IS bound at
  bind — with the real argument TYPES and zero rows — so a template that queried it would render an
  empty dispatch and silently type itself correctly; this one does not need to.
{%- endcomment -%}
{%- if is_bind -%}
SELECT NULL::JSON
{%- else -%}
{%- comment -%}
  The DISTINCT definitions in this chunk. ⚠ Keyed by md5 because the key becomes an IDENTIFIER — a
  definition is arbitrary JSON text and cannot name a macro, while its hash always can, and it is
  content-addressed, so two rows carrying the same rules share one macro.
{%- endcomment -%}
{%- query defs -%}
    SELECT DISTINCT dmn_def AS def, md5(dmn_def) AS h
    FROM input_table
    WHERE dmn_def IS NOT NULL
{%- endquery -%}

{%- comment -%}
  One render + one CREATE per distinct definition. ⚠ The macro is TEMPORARY, which is right here and
  is the one place that flag's usual trap does not apply: `exec()` runs on THIS render session's own
  connection, which is also the connection that will run the SELECT below — so the macro is visible
  exactly where it is needed and is gone afterwards, leaving no catalog behind.
  ⚠ The rendered text is passed to `exec()` as a VALUE rather than pasted into an `{% exec %}` body,
  so Liquid never re-reads it. A decision cell containing `{{` or `{%` would otherwise be interpreted
  as template syntax.
{%- endcomment -%}
{%- for d in defs -%}
    {%- capture nm -%}fab_dmn_{{ d.h }}{%- endcapture -%}
    {%- query r j: d.def, n: nm -%}
        SELECT decision_render($j, name := $n, is_temporary := true) AS s
    {%- endquery -%}
    {%- assign _created = exec(r[0].s) -%}
{%- endfor -%}

{%- comment -%}
  ⚠ THE EMPTY CASE IS NOT DEAD CODE: a chunk in which every definition is NULL produces no groups, and
  `CASE x END` with no WHEN is a syntax error. It also carries the NULL-def contract — the answer is
  NULL, not a failure.
{%- endcomment -%}
{%- if defs.size == 0 -%}
SELECT NULL::JSON FROM input_table
{%- else -%}
SELECT CASE md5(dmn_def)
{%- for d in defs %}
    WHEN '{{ d.h }}' THEN to_json(fab_dmn_{{ d.h }}(dmn_in, hit_policy := dmn_hp))
{%- endfor %}
END::JSON
FROM input_table
{%- endif -%}
{%- endif -%}
$$, NULL, dmn_def := def, dmn_in := input, dmn_hp := hit_policy)::JSON;
