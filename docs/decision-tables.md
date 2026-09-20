# Decision tables

A **decision table** is a relation: one row per rule, columns split into inputs and outputs, plus three
metadata rows. `decision_render` turns one into the SQL text of a DuckDB **macro** that evaluates it —
DMN-like, with hit policies, preprocessing and aggregate collapse. It renders in **two forms**: a *scalar*
macro that decides one row at a time, and a *table* macro that decides a whole relation (§2). When the
**definition itself is data** — one per row — `decision_eval` evaluates it directly and answers JSON (§11).

It is a port of `D:\repos\decision-rules-py`. That project's own `CLAUDE.md` remains the best description of
the *model*; this page is the description of what **ships here**, with every behaviour below measured against
the shipped build.

> **⚠⚠ A DECISION TABLE IS CODE, NOT DATA, AND THAT IS THE FEATURE.** An expression cell is resolved and
> passed *through* into the generated SQL, so a rule may call any function the engine has. Only a bare
> non-expression string becomes a quoted literal. It follows that the rules relation is a **trusted authoring
> surface**, exactly like a template — never build one from user input.

---

## 1. The shape

| `rulepos` | row | example |
|---|---|---|
| `-2` | **direction** — `in`, `out`, or blank (column ignored) | `in`, `in`, `out` |
| `-1` | **datatypes** — the input cast, and the output cast | `VARCHAR`, `FLOAT`, `DOUBLE` |
| `0` | **pre/post-processing** (optional) | `floor(?)`, `max(round(?,2))` |
| `>= 1` | **the rules**, in evaluation order | `EU, US`, `> 18`, `approve` |

A blank direction means the column is ignored entirely — a `Comment` column contributes no input and no
output.

> **⚠ `rulepos` IS THE ORDERING, so it must run `1..N` with no gaps** — the rule *number* is the rule
> *order*. A gap is refused by name rather than assumed away: a generated slot that corresponds to no rule
> is a **phantom rule** — every input condition falls through to `ELSE 1=1`, so it matches *every* input,
> while the outputs answer all-NULL. Under `First` a phantom would win. (The ported engine makes the same
> assumption but does not check it.)

```sql
CREATE TABLE decision_rules (rulepos INT, region VARCHAR, age VARCHAR, amount VARCHAR,
                             decision VARCHAR, risk VARCHAR, label VARCHAR, Comment VARCHAR);
INSERT INTO decision_rules VALUES
    (-2, 'in',      'in',         'in',      'out',     'out',              'out',              NULL),
    (-1, 'VARCHAR', 'FLOAT',      'FLOAT',   'VARCHAR', 'DOUBLE',           'VARCHAR',          NULL),
    ( 0,  NULL,     'floor(?)',   '?',       NULL,      NULL,               NULL,               NULL),
    ( 1, 'EU, US',  '> 18',       '< 10000', 'approve', '@amount * 0.0001', '@region',          NULL),
    ( 2, 'US',      '[21 .. 65]', '> 5000',  'review',  '@age * 0.01',      '@age::varchar',    NULL),
    ( 3, 'not RU',  '',           '<= 1000', 'approve', '0.2',              'low',              NULL),
    ( 4, '',        '',           '> @age',  'flag',    '@amount / @age',   '@amount::varchar', NULL),
    ( 5, '',        '',           '',        'reject',  '1.0',              'default',          NULL);
```

---

## 2. The two forms

> **⚠⚠ THERE ARE ONLY TWO BECAUSE ONLY TWO CAN TAKE VARYING INPUT.** A DuckDB **macro argument is a
> bind-time constant**, so a macro whose parameters are the decision's *inputs* can only ever be called with
> literals — one decision per call site, which is not what anybody evaluates a decision table for. Both forms
> below take the input as **data** instead: one value per row, or one whole relation. (A third form, whose
> parameters were the inputs, shipped until 2026-09-20 and was removed for exactly this reason.)

Both forms are generated from **one** introspection of the rules and differ only in the statement wrapped
around it, so for the same input they answer identically — apart from the two divergences noted below, each
of which is inherent rather than incidental.

### `form := 'scalar'` — the default

A **scalar** macro `f(input [, hit_policy])`: a STRUCT in, a UNION out. It is an ordinary expression, so it
evaluates per row anywhere a scalar can appear.

```sql
SELECT fabricator_host_exec(decision_render('decision_rules', name := 'evaluate'));

SELECT c.id,
       union_extract(evaluate({'region': c.region, 'age': c.age, 'amount': c.amount}), 'single').decision
FROM customers c;
```

The UNION has two members and the **hit policy** chooses which one is returned:

| member | returned for |
|---|---|
| `single STRUCT(…)` | `First`, `Unique`, `Any` — and **every** aggregate-mode call |
| `many STRUCT(…)[]` | `RuleOrder` on a non-aggregate table (all matching rules) |

```sql
SELECT union_tag(evaluate({'region':'JP','age':40,'amount':500}));                  -- single
SELECT union_extract(evaluate(…), 'single').decision;                               -- approve
SELECT u.rulepos, u.decision                                                        -- all matches
FROM (SELECT evaluate(…, hit_policy := 'RuleOrder') AS v), unnest(union_extract(v,'many')) t(u);
```

- **No match at all is `NULL`** under `First`/`Unique`/`Any`, and a `many` whose *list* is NULL under
  `RuleOrder`. That is why `_model_error_ = 'No default rule defined'` is unreachable in a non-aggregate
  scalar: no row survives to carry it.
- **⚠⚠ An output with no declared datatype is `VARCHAR` here and its value is cast**, where the table form
  keeps whatever the cell expression produced. That is inherent — a UNION type has to be *spelled out*, and
  nothing short of executing the generated SQL would say what the cells produce. The escape is the datatypes
  row: declare `INTEGER` and you get an `INTEGER`.

### `form := 'table'` (or `'batch'`)

A **table** macro `f(input_table [, hit_policy])` that decides a whole relation in one pass and returns the
input's own columns beside the decision.

```sql
SELECT fabricator_host_exec(decision_render('decision_rules', name := 'evaluate_batch', form := 'table'));

SELECT * FROM evaluate_batch('customers');
--  region | age | amount | id | … | decisiontable_hk | rulepos | decision | risk | label | _total_hits_ | …
```

- `input_table` is a **bind-time constant naming a relation** — `'customers'`, `'main.customers'`, or the
  bare identifier `customers` (all three measured). It reaches `query_table()`, so it **cannot be a
  subquery**; stage one first if that is what you have.
- **⚠ The input relation must carry a column per input, named as the input is.** The generated macro
  replaces each of them and passes `* EXCLUDE (<the inputs>)` through, so a missing one is a binder error
  naming it. Every *other* column rides through untouched, which is what lets a key column come back beside
  the decision.
- The passthrough carries the **preprocessed** value, not the raw one — the row says which value the rules
  actually saw.
- **⚠ In aggregate mode the outer select is `GROUP BY ALL`** over those passed-through columns, so two input
  rows equal in *every* column collapse into one. Carry a key if that matters.
- When nothing matches, the row is still returned with NULL decision columns (it is a `LEFT JOIN` that found
  nothing), and `_model_error_` *does* read `'No default rule defined'` — the opposite of the scalar form.

### Which to use

| | scalar | table |
|---|---|---|
| input | a STRUCT per row | a named relation |
| composes into | any `SELECT`, any expression position | a `FROM` |
| result | one UNION value, read with `union_extract` | real columns, plus the input's own |
| passthrough | none — carry your own keys | every non-input column of the relation |
| untyped output | `VARCHAR` (cast) | the cell expression's own type |
| evaluations | one per row | one per input row, in one pass |

### `is_temporary` — and the one path it cannot take

`is_temporary := true` renders `CREATE OR REPLACE **TEMPORARY** MACRO`, so the macro lives in the `temp`
catalog and dies with the session instead of being written into the database. It works for both forms and
changes exactly one token — the body is byte-identical either way.

> **⚠⚠ A TEMPORARY MACRO CREATED THROUGH `fabricator_host_exec` IS INVISIBLE TO THE CALLER AND IS THEN
> GONE, WITH NO ERROR.** Measured: the call returns **0**, the success code, and the macro does not exist
> afterwards — not in `duckdb_functions()`, not anywhere. `fabricator_host_exec` runs on its *own*
> connection, whose `temp` catalog dies with it. (The same is true of a `CREATE TEMP TABLE` issued that
> way.)
>
> ⇒ **a temporary macro must be executed by the caller, on the caller's own connection**: read the text
> back and run it — `con.execute(con.sql(render).fetchone()[0])`, the shell's `.read`, a dbt pre-hook.
> That is this function's documented primary use anyway; `fabricator_host_exec` is the convenience path,
> and it is the one path `is_temporary` cannot take.

```sql
-- python/JDBC/CLI: render, then execute on your own connection
SELECT decision_render('decision_rules', name := 'evaluate', is_temporary := true);
-- CREATE OR REPLACE TEMPORARY MACRO evaluate(input, hit_policy := 'First') AS ( ... )
```

A **qualified** `name` and `is_temporary` do not combine: DuckDB resolves the schema inside the `temp`
catalog, so `name := 's1.m'` fails with *"Schema with name s1 does not exist!"*.

---

## 3. Input condition cells

An input cell is a **predicate fragment about its own column**, with the column implicit on the left:
`> 18` means `age > 18`. Parsed by `DecisionRuleParser.ParseCondition`.

| form | cell | renders (column `region` / `age`) |
|---|---|---|
| blank or `*` | `` | **wildcard** — the rule ignores this column |
| leading operator | `> 18`, `<= 1000`, `!= 3` | `age > 18` |
| bare value | `EU` | `region = 'EU'` |
| comma list | `EU, US` | `region IN ('EU', 'US')` |
| bracket list | `in [1,2,3]` | `age IN (1, 2, 3)` |
| FEEL range | `[21 .. 65]`, `(21 .. 65]` | `age >= 21 AND age <= 65` |
| negation | `not RU` | `NOT (region = 'RU')` |
| `BETWEEN` | `between min(1) and max(100)` | `age BETWEEN min(1) AND max(100)` |
| leading keyword | `IS NULL`, `LIKE 'E%'`, `GLOB 'US*'`, `SIMILAR TO 'E.*'`, `IS NOT DISTINCT FROM @r` | `region IS NULL`, `region LIKE 'E%'`, … |
| full condition | `@age * 2 > 30` | `age * 2 > 30` |
| `?` placeholder | `contains(?, 'x')` | `contains(region, 'x')` |
| cross-column ref | `> @age` | `amount > age` |

### Things worth knowing before you author a table

- **A missing operator keyword is the dangerous direction.** An unrecognised keyword falls to the string
  fallback and renders an equality against the cell's own *text* — valid SQL that is always false, with
  nothing failing anywhere. A *spurious* keyword is loud (the target refuses the SQL) and escapable by
  quoting the cell. That asymmetry is why the keyword list is generous and spans dialects.
- `REGEXP` is in the list and is in **neither** DuckDB nor SQL Server; it came in with the ported engine,
  which targeted MySQL/SQLite. It stays, because removing it would turn a cell naming it from a loud error
  back into a silently-false comparison.
- `not applicable` is read as a **negation** (`not` + whitespace), and a data value containing an operator
  keyword passes through as raw SQL (`MADE IN USA` — the `IN`). Both are loud; **quote the cell** to escape.
- The numeric test has **no exponent form**, so `= 1e5` compares against the *text* `1e5`. Faithful to the
  ported engine; write `100000`.
- `?` substitution is **quote-aware** — a `?` inside a string literal is the literal's own character. This
  matters for `GLOB 'US?'`, where `?` is GLOB's single-character wildcard, so a blind replace would produce a
  silently different pattern rather than an error. (The ported engine still does the blind replace.)

---

## 4. Output cells

An output cell is a **value expression**, parsed by `DecisionRuleParser.ParseValue`.

| form | cell | renders |
|---|---|---|
| bare column reference | `@col` | `col` |
| quoted string | `'approve'` | `'approve'` |
| number | `42`, `-3`, `0.5` | unchanged |
| keyword literal | `true`, `null` | `TRUE`, `NULL` |
| arithmetic | `@amount * 0.01` | `amount * 0.01` |
| cast | `@age::varchar`, `'20010101'::date` | unchanged, `@refs` resolved |
| parenthesised | `(func(@x))` | `(func(x))` |
| anything else | `approve`, `O'Brien` | `'approve'`, `'O''Brien'` |

A **blank** output cell renders SQL `NULL`. (The ported engine raises instead; this is a deliberate
improvement, decided in the introspection because "what does an empty cell mean" is a question about the
data.)

The same value parser runs in four other positions, which is why they behave identically: the right-hand side
of a comparison, each `BETWEEN` bound, each `IN`/comma list item, and the right-hand side of a leading
keyword.

---

## 5. Preprocessing and postprocessing (`rulepos = 0`)

### Inputs

`?` is the column, `@other` is another column. Two modes, chosen by shape:

| the cell | mode | effect |
|---|---|---|
| `floor(?)` | scalar | `(floor(age)) AS age` in the `preprocessed` CTE |
| `(select * from (select upper(?) a) as x)` | scalar subselect | parenthesised, so still a scalar |
| `select unnest(str_split(?, ',')) a` | **SELECT-style** | `LEFT JOIN LATERAL … ON TRUE` — **expands one input into N** |

The declared **datatype** is applied before preprocessing, in both forms: the scalar macro's `params` CTE
casts out of the input STRUCT, and the table macro's `casted` CTE casts the input relation's columns.

> **⚠⚠ THE EXPANSION MEANS SOMETHING DIFFERENT IN EACH FORM, AND BOTH ARE FAITHFUL.** In the **scalar** form
> the expansion happens inside one evaluation, so all the hits belong to a single call and `_total_hits_`
> counts them together. In the **table** form it happens in `preprocessed`, i.e. *before* the per-row
> evaluation — so one input row becomes N **input rows**, each decided on its own. The ported engine splits
> the same way; what would be a defect is a form that silently did the other one.

```sql
-- scalar: one call, five hits
SELECT u.rulepos, u.decision, u._total_hits_
FROM (SELECT eval({'region':'EU,US,JP'}, hit_policy := 'RuleOrder') v), unnest(union_extract(v,'many')) t(u);
--  1 | euro | 5     2 | dollar | 5     3 | other | 5     3 | other | 5     3 | other | 5

-- table: three input rows, decided separately
SELECT region, _total_hits_ FROM eval_t('inputs');
--  EU | 2     JP | 1     US | 2
```

> **⚠⚠ Every reference to an input is rendered qualified (`preprocessed.age`), and it is still
> load-bearing though the original reason is gone.** It was written for macro-parameter shadowing, which
> cannot arise now that the inputs are data. It stays because of the **table** form: the rule predicates sit
> in a LATERAL beside the output CASEs, and a **local SELECT alias beats a correlated outer column of the
> same name** — measured. With an output column named like an input, an unqualified predicate would compare
> against that *output expression* and the rule would silently never fire. (In the plain table case the
> column wins over the alias, which is why this looks safe until a lateral is involved.)

### Outputs

Composed from two sources and applied in the outer `SELECT`: the `rulepos = 0` expression (where `?` is the
**output** column), then the datatype cast.

```
'upper(?)' + datatype VARCHAR   ⇒   cast((upper(label)) as VARCHAR) AS label
no expression + datatype INTEGER ⇒   cast(n as INTEGER) AS n
```

---

## 6. Aggregate mode

When **any** output's `rulepos = 0` expression contains an aggregate, the whole table switches to aggregate
mode: every matching rule collapses into one row per input, and the default hit policy becomes `RuleOrder`.

Detection scans **every** call, not the outermost — `EXP(SUM(LOG(?)))` counts. Recognised:
`min max sum product string_agg array_agg list json_group_array argmax argmin arg_max arg_min count`
(identical to the ported engine's set).

```sql
-- decision: arg_max(?,risk)   risk: max(round(?,2))   label: max(?)
SELECT rulepos, decision, risk, label FROM eval_agg('inputs');
--   3,4,5 | flag | 12.5 | low
```

- `rulepos` becomes `string_agg(rulepos, ',' ORDER BY rulepos)` — a comma-separated list of matched rules.
- `_total_hits_` becomes `max(_total_hits_)`.
- The **scalar** form always answers `single` here, whatever the hit policy: there is nothing left for
  `many` to carry once every match has been collapsed.
- The **table** form adds `GROUP BY ALL`.
- **An output with no preprocessing is excluded from the result** — it cannot be collapsed, and a bare column
  beside an aggregate is a binder error. The inner `CASE` still *computes* it, which is the only reason
  `arg_max(decision, risk)` can reference a `risk` that never appears in the output.

---

## 7. Hit policies

Passed to the **generated** macro, not to `decision_render`; `decision_render(hit_policy := …)` only changes
the default baked in.

| policy | returns | reports |
|---|---|---|
| `First` (default) | the first matching rule by `rulepos` | — |
| `RuleOrder` | every matching rule, in `rulepos` order | — |
| `Unique` | the first match | `_model_error_` when `_total_hits_ <> 1` |
| `Any` | the first match | `_model_error_` when overlapping rules produce **different** outputs |

Alongside your outputs, both forms return `decisiontable_hk` (an MD5 identity of the rules),
`rulepos`, `_total_hits_`, `_any_hitpolicy_violations_` and `_model_error_`.
`_any_hitpolicy_violations_` is computed for *every* policy — only the message is policy-dependent.

> **⚠ When nothing matches, the three shapes differ**, and the difference is surprising enough to be worth
> stating: the **scalar** form returns `NULL` (or a `many` whose list is NULL under `RuleOrder`); the
> **table** form returns the input row with NULL decision columns; an **aggregate** table returns one row of
> NULLs. Only the last two can ever report `'No default rule defined'`.

---

## 8. Where the rules come from

```sql
SELECT decision_render('decision_rules');                  -- a relation name, or db.main.rules
SELECT decision_render('/data/rules.csv');                 -- a file: csv, parquet, json, …
SELECT decision_render('[{"rulepos":-2,"region":"in"}]');  -- JSON text (array of objects)
SELECT decision_render('read_xlsx(''r.xlsx'')', source := 'sql');   -- any relation expression
```

`source := 'auto'|'table'|'file'|'json'|'sql'`. **Auto never guesses `sql`** — a relation expression is not
separable from a relation name by any cheap test, so that mode is request-only. The other three cannot
collide: JSON starts with a bracket, a path carries a separator or a data extension, a name is what is left.

- A **csv** is read with `all_varchar = true`; without it DuckDB's sniffer types a column of bare numbers as
  `BIGINT` and the cells stop being text.
- **JSON** infers its own columns via `from_json(…, json_structure(…))`; needs the `json` extension.
- `source := 'table'` still reads a **dotted** name as qualified — `rules.csv` becomes `"rules"."csv"`. A
  relation literally *named* `rules.csv` needs `source := 'sql'` with the name pre-quoted.

The rules source and the rendered **form** are independent: any source renders either form.

> **⚠ Rules held in a TEMP table are invisible**: the introspection runs on the render's own connection.

> **⚠ Rendering costs three small queries and DuckDB repeats binds**, so do not put a `decision_render`
> behind a view and expect it to run once. Render once, create the macro, and call the macro.

---

## 9. The parser, callable on its own

The quickest way to see what a cell means:

```sql
SELECT dmn_condition('[21 .. 65]', 'age', 'duckdb', '');   -- age >= 21 AND age <= 65
SELECT dmn_value('@amount * 0.01', 'duckdb', '');          -- amount * 0.01
SELECT dmn_is_aggregate('EXP(SUM(LOG(?)))');               -- true
SELECT dmn_relation('rules.csv', 'auto');                  -- read_csv('rules.csv', all_varchar = true)
```

The fourth argument of `dmn_condition` / third of `dmn_value` is the **reference prefix** — what every
`@ref` resolves against. The renderer passes `preprocessed.`; pass `''` when asking a question by hand.

---

## 10. Not built yet

Measured against the ported engine, so the list is complete rather than impressionistic.

| | status |
|---|---|
| **T-SQL inline table-valued function** | **not built**. `decision_render(dialect := 'tsql')` is refused by name rather than returning a DuckDB macro that pretends to be T-SQL. The *cell parser* already answers for T-SQL, so what is missing is a second statement template, not a second parser. |
| Excel / Grist loading | out of scope — that is *authoring*, not evaluation |
| `varchar(max)` in the datatypes row | **not normalised.** Measured: DuckDB accepts `varchar(50)`, `nvarchar(50)` and `char(3)`, and rejects only the `(max)` spelling (`Parser Error: Expected a constant as type modifier`). The ported engine normalises SQL-Server types when targeting DuckDB; we do not, which matters only for a rules table authored against SQL Server. Belongs with the T-SQL work. |

Things that exist **here** and not in the ported engine: `SIMILAR TO`, `GLOB` and the `IS` operators; a
blank output cell rendering `NULL`; four rules sources; the qualification that makes input preprocessing
actually reach the rules; the declared input datatype being applied in the batch form too (the ported engine
casts in neither batch form, so the same table can answer differently depending on how it is evaluated); and
the `rulepos` contiguity CHECK — the same assumption, but verified instead of assumed, so a mis-numbered
table is refused rather than quietly evaluating a phantom rule.

The ported engine's own **row-eval** macro — one macro parameter per input — is deliberately **not** ported;
see §2 for why a bind-time constant cannot be a decision's input.

---

## 11. `decision_eval` — when the definition itself is data

Everything above renders a decision table you know at authoring time. `decision_eval` takes the table's
**definition as a per-row value**, so a thousand rows may carry a thousand different rule sets and each is
decided by its own.

```sql
SELECT r.id, decision_eval(r.rules_json, {'region': r.region, 'age': r.age})
FROM records r;
-- {"single":{"decisiontable_hk":"…","rulepos":"1","decision":"approve",…}}
```

| argument | |
|---|---|
| `def` | the decision table as **JSON text** — the same shape §8's JSON source takes. NULL ⇒ NULL |
| `input` | a **STRUCT** carrying one field per input the table declares |
| `hit_policy` | **per row**; NULL (the default) means the table's own default |

> **⚠⚠ THE RESULT IS ALWAYS `JSON`, AND THAT IS WHAT MAKES THE FUNCTION POSSIBLE AT ALL.** A scalar's
> result type is fixed at **bind**, where no row value exists — so a function whose answer depended on a
> per-row definition could not be typed at all. JSON is the one type that describes every decision table at
> once. `to_json` over the generated macro's UNION keeps the hit policy's shape in the **key**:
>
> ```
> {"single": {decisiontable_hk, rulepos, <your outputs>, _total_hits_, …}}   First / Unique / Any
> {"many":  [{…}, {…}]}                                                      RuleOrder
> ```
>
> Read it with DuckDB's JSON operators: `decision_eval(…)->'single'->>'decision'`.

> **⚠⚠ The input struct must carry every input the definition names**, and it is one struct type for the
> whole call site — a column has one type. So definitions that vary per row must agree on their *inputs*;
> what varies is the *rules*. That is the shape this serves (per-tenant or per-version rule sets over one
> record shape). A definition naming an input the struct lacks fails loudly: *"Could not find key …"*.

### How it works, and what it costs

It is a `fluid_scalar`. For each chunk of rows DuckDB hands it, the template reads the **distinct**
definitions out of the chunk, renders each into a **temporary** macro named by the definition's md5, and
emits one `SELECT` that dispatches every row to its own macro. Nothing is left behind: those macros live on
the chunk's own connection and die with it.

Measured on one distinct definition, 2048-row chunks, warm: **~54 ms per chunk**, i.e. ~26 µs a row —
20480 rows in 0.65 s. The split, measured separately over 20 calls each, is what decides every optimisation
anyone will propose here:

| | |
|---|---|
| the **render** | **32 ms** per distinct definition per chunk |
| the `CREATE` | **0.75 ms** — essentially free |
| one host round trip | 0.35 ms — so the render's 32 ms is the render, not the plumbing |
| the rest (~21 ms) | binding and running the generated SELECT, of which ~14 ms is the decision evaluation itself and is irreducible |

> **⚠⚠ Two optimisations look obvious and the measurements say no to both.**
>
> **Inlining** the decisions into one big SELECT instead of creating macros saves the `CREATE` — **0.75 ms
> of 54, about 1.4%**. The DDL was never the cost.
>
> **Caching the render** is the only lever that matters (up to ~32 ms of 54, ≈2.5×), but it needs a store
> that survives a chunk, which is exactly what this surface does not have: a `fluid_scalar` session opens a
> **fresh connection per chunk** (its binding is shared across pipeline threads, and a DuckDB connection is
> single-threaded). The obvious store nearly cancels itself — creating the macros *persistently* and
> skipping the render when `duckdb_functions()` already has the name moves the slope only
> **53.6 → 45.4 ms (~15%)**, because that catalog lookup costs ~19 ms on its own. It also makes an ordinary
> SELECT write database objects. A real in-memory cache belongs in a **C# scalar function**, where the
> store is a static dictionary and the lookup is free.

> **⇒ If every row shares one decision table, do not use this.** Render it once with `decision_render` and
> call the macro: `form := 'table'` decides a whole relation in one pass and was measured at **~7 µs a
> row**, roughly four times faster.

---

## 12. Where the tests are

| what | where |
|---|---|
| the cell grammar, every branch, offline | `dotnet/Fabricator.Bridge.Tests/DecisionRuleParserTests.cs` |
| the rules-source classifier, offline | `dotnet/Fabricator.Bridge.Tests/DecisionRuleSourceTests.cs` |
| the renderer end to end | `test/verify_decision_render.test` |
| `decision_eval` end to end | `test/verify_decision_eval.test` |

The suite's sections map to this page: §3 the ported engine's own documented answers in both forms, §4
preprocessing reaching the rules, §5 aggregate mode, §6 the LATERAL expansion and how the two forms diverge
on it, §7 the four rules sources, §8 the refusals, §9 the two shapes, §10 the `Any` policy, §11 aggregate
exclusion, §12 output postprocessing, §13 the `rulepos` contiguity rule, §14 `decisiontable_hk`, §15 the
pattern and NULL operators, §16 a blank output cell, §17 the table form's contract, §18 the scalar form's,
§19 `is_temporary`.

> ⚠ §19's coverage of the *temporary* path is structural — the rendered token, and the proof that it is the
> only difference — because **sqllogictest cannot execute rendered text on its own connection**, which is
> precisely the condition a temporary macro needs. What §19 *does* assert behaviourally is the trap: the
> `fabricator_host_exec` route reports success and creates nothing, with the non-temporary call beside it
> as the control. The end-to-end half was measured by hand: rendering both forms with `is_temporary := true`,
> writing them out and `.read`-ing them back in one shell session creates both in `temp.main`, and both
> answer correctly.
