# Decision tables

A **decision table** is a relation: one row per rule, columns split into inputs and outputs, plus three
metadata rows. `decision_render` turns one into the SQL text of a DuckDB `TABLE MACRO` that evaluates it —
DMN-like, with hit policies, preprocessing and aggregate collapse.

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
| `-1` | **datatypes** — the macro parameter type, and the output cast | `VARCHAR`, `FLOAT`, `DOUBLE` |
| `0` | **pre/post-processing** (optional) | `floor(?)`, `max(round(?,2))` |
| `>= 1` | **the rules**, in evaluation order | `EU, US`, `> 18`, `approve` |

A blank direction means the column is ignored entirely — a `Comment` column contributes no parameter and no
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

```sql
-- read the generated macro ...
SELECT decision_render('decision_rules', name := 'evaluate_row');

-- ... then create it and evaluate
SELECT fabricator_host_exec(decision_render('decision_rules', name := 'evaluate_row'));

SELECT rulepos, decision, risk, label FROM evaluate_row('JP', 40, 500);
--   3 | approve | 0.2 | low
SELECT rulepos, decision FROM evaluate_row('JP', 40, 500, hit_policy := 'RuleOrder');
--   3 | approve     4 | flag     5 | reject
```

---

## 2. Input condition cells

**The column is implicit on the LEFT.** A cell is a predicate *fragment* about its own column, so `> 18` on
column `age` means `age > 18`. Writing the left-hand side out (`@age > 18`) means the same thing and is what
you need when a condition spans two columns.

The parser dispatches in this order; the first branch that matches wins.

| # | form | cell | on column | renders |
|---|---|---|---|---|
| 1 | wildcard | *(blank)*, `*` | `age` | `TRUE` |
| 2 | placeholder, leading | `? >=a` | `col` | `col >= 'a'` |
| 2 | placeholder, embedded | `5 >=?` | `col` | `5 >=col` |
| 3 | negation | `not RU` | `region` | `NOT (region = 'RU')` |
| 4 | explicit IN | `in (1,2,3)` | `age` | `age IN (1, 2, 3)` |
| 4 | explicit IN, list literal | `in [1,2,3]` | `age` | `age IN [1, 2, 3]` |
| 5 | FEEL range | `[21 .. 65]` | `age` | `age >= 21 AND age <= 65` |
| 5 | FEEL range, exclusive | `(0 .. 100]` | `age` | `age > 0 AND age <= 100` |
| 5 | FEEL range, expression bound | `[0 .. @age]` | `amount` | `amount >= 0 AND amount <= age` |
| 6 | comma list | `EU, US` | `region` | `region IN ('EU', 'US')` |
| 7 | BETWEEN | `between min(1) and max(100)` | `age` | `age BETWEEN min(1) AND max(100)` |
| 8 | comparison | `> 18`, `<> 'x'`, `<= 5`, `!= 5` | `age` | `age > 18` … |
| 8 | cross-column | `> @age` | `amount` | `amount > age` |
| 9 | NULL test | `IS NULL`, `IS NOT NULL` | `region` | `region IS NULL` |
| 9 | null-safe equality | `IS NOT DISTINCT FROM @other` | `region` | `region IS NOT DISTINCT FROM other` |
| 9 | pattern | `LIKE 'E%'`, `ILIKE 'e%'` | `region` | `region LIKE 'E%'` |
| 9 | pattern | `SIMILAR TO 'E.*'`, `GLOB 'US*'` | `region` | `region SIMILAR TO 'E.*'` |
| 10 | function call | `contains(?, 'foo')` | `col` | `contains(col, 'foo')` |
| 11 | complete condition | `@age*2 > min(1)+1` | `age` | `age*2 > min(1)+1` |
| 12 | fallback: equality | `EU` | `region` | `region = 'EU'` |

Every row above is pinned in `dotnet/Fabricator.Bridge.Tests/DecisionRuleParserTests.cs`, and each branch of
`DecisionRuleParser.ParseCondition` carries the same CONTEXT / PARSES / SAMPLE / TEST block in a comment.

### Things worth knowing before you author a table

> **⚠ Quoting a cell forces it to be a literal**, past every keyword and comma rule. `'a,b,c'` is one value,
> not a list; `'MADE IN USA'` is a value, not an `IN` condition.
>
> **⚠ A bare data value containing an operator keyword is read as an operator** and passes through as raw
> SQL, which the engine refuses when the macro is created. That is the safe direction — the alternative is a
> silently false comparison — and quoting is the escape. `MADE IN USA` breaks; `'MADE IN USA'` does not.
> Word boundaries hold, so `LIKED` and `GLOBAL` are still values.
>
> **⚠ `not applicable` is read as a negation** (`NOT (status = 'applicable')`) because `not` is followed by
> whitespace. `NOTEBOOK` is not. Quote it if you mean the text.
>
> **⚠ A `%` or `*` in a pattern must be quoted by the author** — those are value operators, so an unquoted
> `E%` reads as an expression and passes through raw. `LIKE 'E%'` is right; `LIKE E%` is not.
>
> **⚠ `?` inside a quoted literal is left alone**, so a GLOB single-character wildcard survives:
> `GLOB 'US?'` renders `region GLOB 'US?'`. (This was a blind string replace until 2026-09-16 and rendered
> `region GLOB 'USregion'` — a silently different pattern.)
>
> **⚠ `1e5` is not a number.** The numeric test has no exponent form, so `= 1e5` compares against the *text*.
> Write `100000` or `1e5::DOUBLE`. This is faithful to the ported engine, which has the identical regex.
>
> **⚠ A FEEL range is matched before the comma check**, so `[0 .. func(1,2,?)]` is a range and not a
> three-item list. Reversing those two produces valid SQL that means something else.

---

## 3. Output cells

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

## 4. Preprocessing and postprocessing (`rulepos = 0`)

### Inputs

`?` is the column, `@other` is another column. Two modes, chosen by shape:

| the cell | mode | effect |
|---|---|---|
| `floor(?)` | scalar | `(floor(age)) AS age` in the `preprocessed` CTE |
| `(select * from (select upper(?) a) as x)` | scalar subselect | parenthesised, so still a scalar |
| `select unnest(str_split(?, ',')) a` | **SELECT-style** | `LEFT JOIN LATERAL … ON TRUE` — **expands one input row into N** |

```sql
-- 'EU,US,JP' becomes three rows, each evaluated independently
SELECT rulepos, decision FROM eval_lat('EU,US,JP', hit_policy := 'RuleOrder');
--  1 | euro     2 | dollar     3 | other     3 | other     3 | other
```

> **⚠⚠ Every reference to an input is rendered qualified (`preprocessed.age`), and that is load-bearing.** A
> DuckDB macro *parameter shadows a column of the same name anywhere in the body*, so an unqualified
> reference reads the raw argument and preprocessing becomes **silently inert** — computed, never read, every
> rule matching the unpreprocessed value, nothing failing. Measured both ways.

### Outputs

Composed from two sources and applied in the outer `SELECT`: the `rulepos = 0` expression (where `?` is the
**output** column), then the datatype cast.

```
'upper(?)' + datatype VARCHAR   ⇒   cast((upper(label)) as VARCHAR) AS label
no expression + datatype INTEGER ⇒   cast(n as INTEGER) AS n
```

---

## 5. Aggregate mode

When **any** output's `rulepos = 0` expression contains an aggregate, the whole table switches to aggregate
mode: every matching rule collapses into one row per input, and the default hit policy becomes `RuleOrder`.

Detection scans **every** call, not the outermost — `EXP(SUM(LOG(?)))` counts. Recognised:
`min max sum product string_agg array_agg list json_group_array argmax argmin arg_max arg_min count`
(identical to the ported engine's set).

```sql
-- decision: arg_max(?,risk)   risk: max(round(?,2))   label: max(?)
SELECT rulepos, decision, risk, label FROM eval_agg('JP', 40, 500);
--   3,4,5 | flag | 12.5 | low
```

- `rulepos` becomes `string_agg(rulepos, ',' ORDER BY rulepos)` — a comma-separated list of matched rules.
- `_total_hits_` becomes `max(_total_hits_)`.
- **An output with no preprocessing is excluded from the result** — it cannot be collapsed, and a bare column
  beside an aggregate is a binder error. The inner `CASE` still *computes* it, which is the only reason
  `arg_max(decision, risk)` can reference a `risk` that never appears in the output.

---

## 6. Hit policies

Passed to the **generated** macro, not to `decision_render`; `decision_render(hit_policy := …)` only changes
the default baked in.

| policy | returns | reports |
|---|---|---|
| `First` (default) | the first matching rule by `rulepos` | — |
| `RuleOrder` | every matching rule, in `rulepos` order | — |
| `Unique` | the first match | `_model_error_` when `_total_hits_ <> 1` |
| `Any` | the first match | `_model_error_` when overlapping rules produce **different** outputs |

Alongside your outputs the macro returns `decisiontable_hk` (an MD5 identity of the rules),
`rulepos`, `_total_hits_`, `_any_hitpolicy_violations_` and `_model_error_`.
`_any_hitpolicy_violations_` is computed for *every* policy — only the message is policy-dependent.

> **⚠ When nothing matches, the two modes diverge.** A non-aggregate macro returns **zero rows**; an
> aggregate one returns **one row of NULLs**, which is the only way `_model_error_` can ever read
> `'No default rule defined'`. That branch is unreachable in a non-aggregate macro by construction.

---

## 7. Where the rules come from

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

> **⚠ Rules held in a TEMP table are invisible**: the introspection runs on the render's own connection.

---

## 8. The parser, callable on its own

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

## 9. Not built yet

Measured against the ported engine, so the list is complete rather than impressionistic.

| | status |
|---|---|
| **batch evaluation** — a macro that evaluates a whole input *table* (`query_table(input)`, `GROUP BY ALL`) | **not built**. The generated macro is the row form only. |
| **T-SQL inline TVF** | **not built**. `decision_render(dialect := 'tsql')` is refused by name rather than returning a DuckDB macro that pretends to be T-SQL. The *cell parser* already answers for T-SQL. |
| a scalar-returning variant of the row macro | not built |
| Excel / Grist loading | out of scope — that is *authoring*, not evaluation |
| `varchar(max)` in the datatypes row | **not normalised.** Measured: DuckDB accepts `varchar(50)`, `nvarchar(50)` and `char(3)`, and rejects only the `(max)` spelling (`Parser Error: Expected a constant as type modifier`). The ported engine normalises SQL-Server types when targeting DuckDB; we do not, which matters only for a rules table authored against SQL Server. Belongs with the T-SQL work. |

Things that exist **here** and not in the ported engine: `SIMILAR TO`, `GLOB` and the `IS` operators; a
blank output cell rendering `NULL`; four rules sources; the qualification that makes input preprocessing
actually reach the rules; and the `rulepos` contiguity CHECK — the same assumption, but verified instead of
assumed, so a mis-numbered table is refused rather than quietly evaluating a phantom rule.

---

## 10. Where the tests are

| what | where |
|---|---|
| the cell grammar, every branch, offline | `dotnet/Fabricator.Bridge.Tests/DecisionRuleParserTests.cs` |
| the rules-source classifier, offline | `dotnet/Fabricator.Bridge.Tests/DecisionRuleSourceTests.cs` |
| the renderer end to end | `test/verify_decision_render.test` |

The suite's sections map to this page: §3 the ported engine's own documented answers, §4 preprocessing
reaching the rules, §5 aggregate mode, §6 the LATERAL expansion, §7 the four rules sources, §8 the refusals,
§9 the macro's shape, §10 the `Any` policy, §11 aggregate exclusion, §12 output postprocessing, §13 the
`rulepos` contiguity rule, §14 `decisiontable_hk`, §15 the pattern and NULL operators, §16 a blank output
cell.
