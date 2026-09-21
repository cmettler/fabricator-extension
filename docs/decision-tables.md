# Decision tables

A **decision table** is a relation: one row per rule, columns split into inputs and outputs, plus three
metadata rows. `decision_render` turns one into the SQL text of a DuckDB **macro** that evaluates it —
DMN-like, with hit policies, preprocessing and aggregate collapse. It renders in **two forms**: a *scalar*
macro that decides one row at a time, and a *table* macro that decides a whole relation (§2). When the
**definition itself is data** — one per row — `decision_eval` evaluates it directly and answers JSON (§11).
For **SQL Server**, `decision_render_tsql` renders the same table as a T-SQL function (§12).

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

### ⚠⚠ The strict audit — `strict := true`

An unrecognised cell is the one **silent** failure this engine has: it falls to the string fallback and
renders an equality against its own *text*, which is valid SQL that is always false. The keyword list
below is correct today, but **a list can always have a gap and the engine's parser cannot**. So for
every distinct input cell the parser quoted as a literal, `decision_render` asks DuckDB whether
`<column> <cell>` would have parsed as a *condition* — and refuses by name if it would.

```
decision_render: these input cells were read as LITERALS but are valid CONDITIONS in DuckDB, which
would render an equality against the cell's own text — always false, and silent: `COLLATE "C"`.
Write the condition in a form the cell parser knows, or QUOTE the cell to mean it literally.
Pass strict := false to render anyway.
```

> **⚠ It audits rather than promotes**, deliberately. Letting the probe turn such a cell *into* a
> condition would create the mirror-image silent failure — a genuine literal that happens to parse
> would quietly become a predicate. Refusing cannot invent a wrong answer.
>
> ⚠ **The tail, measured and accepted**: a cell that parses as a non-boolean *expression* is flagged too
> (`COLLATE "C"` parses, and even binds, in that position). Contrived as a band label, and the escape is
> the usual one — quote the cell.
>
> ⚠⚠ **It found a live gap on its first real table**: `IS TRUE` / `IS FALSE` / `IS UNKNOWN` were missing
> from the keyword list — `IS NULL` and `IS [NOT] DISTINCT FROM` were there, these were not — so a
> BOOLEAN input whose cell read `IS TRUE` rendered `flag = 'IS TRUE'`. Now parsed properly.

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
| ~~T-SQL~~ | **BUILT** (2026-09-20) as `decision_render_tsql` — §12. `decision_render` has no `dialect` parameter at all now: it renders DuckDB, and the T-SQL statement template lives in its own macro. |
| Excel / Grist loading | out of scope — that is *authoring*, not evaluation |
| `varchar(max)` in the datatypes row, when targeting **DuckDB** | **not normalised.** Measured: DuckDB accepts `varchar(50)`, `nvarchar(50)` and `char(3)`, and rejects only the `(max)` spelling (`Parser Error: Expected a constant as type modifier`). The ported engine normalises SQL-Server types when targeting DuckDB; we do not, which matters only for a rules table authored against SQL Server. ⚠ The *reverse* direction IS handled — see §12 on the bare `VARCHAR` that means length one in T-SQL. |

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

## 12. T-SQL: `decision_render_tsql`

The same decision table, rendered for **SQL Server** instead of DuckDB. It is a separate macro from
`decision_render` — the statement templates have almost nothing in common, and keeping them apart keeps
both readable. What is *not* separate is the part that matters: `dmn_condition` / `dmn_value` /
`dmn_is_aggregate` are the same C# parser called with `'tsql'`, so the **cell grammar cannot drift**
between the two engines.

```sql
SELECT decision_render_tsql('decision_rules', name := 'dbo.evaluate');
-- then run that text on SQL Server
SELECT fabricator_exec('mycat', decision_render_tsql('decision_rules', name := 'dbo.evaluate'));
```

**Both forms take one input row** — there is no batch form here.

| `form := 'scalar'` (default) | `form := 'table'` |
|---|---|
| `RETURNS NVARCHAR(MAX)` — JSON out | `RETURNS TABLE` — an inline TVF |
| one value, composable per row | the preprocessed input columns beside the decision |
| `RuleOrder` ⇒ a JSON array | `RuleOrder` ⇒ one row per matching rule |

### `input` — how that row arrives, and why both ways exist

`input` is **orthogonal** to `form`, so all four combinations render; the body is shared and only the
`params` derived table differs.

| | |
|---|---|
| `input := 'json'` (default) | one `@input NVARCHAR(MAX)` document, read with `OPENJSON … WITH` |
| `input := 'typed'` | one function parameter per decision input, typed from the datatypes row |

```sql
-- json
SELECT * FROM dbo.evaluate('{"region":"EU","age":30}', DEFAULT);

-- typed  →  CREATE OR ALTER FUNCTION dbo.evaluate(@region VARCHAR(MAX), @age INT, @hit_policy …)
SELECT c.id, d.decision FROM customers c CROSS APPLY dbo.evaluate(c.region, c.age, DEFAULT) d;
```

> **⚠⚠ THE TRADE-OFF RUNS IN BOTH DIRECTIONS, AND IT IS THE WHOLE REASON BOTH EXIST.**
>
> **JSON survives a rules change.** Pass the whole record — wider than the decision table needs — and
> `OPENJSON … WITH` reads only the keys it was told about. Add an input to the rules, re-render, and
> **the call site is untouched**. A field the rules never mention rides along ignored (measured).
>
> **Typed is easier to call** and type-checks at CREATE time rather than at OPENJSON time — but its
> signature changes when the decision table gains an input, so every call site changes with it.
>
> ⚠ Whichever you pick the **answer is identical** — measured byte for byte on the scalar pair.

### ⚠⚠ The JSON matches the DuckDB scalar form byte for byte

That is the property worth having, and it is gated: the same rules answer the same JSON on both engines,
so a caller reads `->'single'->>'decision'` either way, and `decisiontable_hk` is the same md5 on both
sides. Getting there needed three things that are easy to miss:

- **`FOR JSON PATH, INCLUDE_NULL_VALUES`.** `FOR JSON` drops NULL keys by default, so `_model_error_`
  simply vanished from the object whenever there was no error — which is almost always.
- **`ORDER BY CAST(rulepos AS INT)` before `FOR JSON`.** `RuleOrder` means every matching rule *in
  rulepos order*, and `FOR JSON PATH` has no inherent order. The cast matters past rule 9.
- **`rulepos` as a string.** DuckDB's UNION struct must declare one type for it and aggregate mode makes
  that a comma-joined list, so the T-SQL scalar casts to match. (The table form keeps it an integer on
  both engines.)

> ⚠ Running that comparison for the first time found a **bug in the DuckDB side**: its `many` list was
> unordered. Every assertion that read the list had sorted it first, so nothing in the DuckDB suite could
> see it. Cross-engine comparison is the test that had no blind spot.

### What T-SQL forces

- **⚠⚠ There is no boolean type**, so a `CASE` cannot return a predicate. Every rule condition is
  rendered `IIF(<cond>, 1, 0)` and the `CASE` compared `= 1`. This is *the* structural difference from
  the DuckDB template; get it wrong and the function does not compile.
- `OPENJSON(@input) WITH (…)` replaces the struct unpacking, `OUTER APPLY` replaces `LEFT JOIN LATERAL`,
  and `GENERATE_SERIES(1, N)` enumerates the rules — which needs **SQL Server 2022+** (compat 160).
- **An inline TVF cannot carry `ORDER BY`**, so the table form does not order its rows under `RuleOrder`
  — the caller orders. (The scalar form can, because `FOR JSON` is present.)
- `CREATE OR ALTER FUNCTION` must be the **first statement of its batch**, so run the rendered text on
  its own.

### ⚠⚠ The datatypes row must carry T-SQL type names

It becomes the `OPENJSON … WITH` column list and the output `CAST`. `DOUBLE` and `BOOLEAN` are DuckDB
spellings and fail loudly at CREATE (*"Type DOUBLE is not a defined system type"*) — write `FLOAT`,
`BIT`, `INT`, `DATE`.

> **⚠⚠ One spelling is fixed rather than passed through, because it fails SILENTLY.** A bare `VARCHAR`
> or `NVARCHAR` in a T-SQL column definition means length **one**. A rules table authored for DuckDB —
> where `VARCHAR` *is* the string type — would therefore truncate every input to its first character,
> with nothing failing anywhere and every multi-character rule quietly not matching. Both bare spellings
> are widened to `(MAX)`. A length you wrote yourself (`VARCHAR(50)`) is left exactly as written. The
> ported engine passes these through untouched; this is a deliberate deviation.

### Divergences from the DuckDB pair

| | |
|---|---|
| no match, `RuleOrder` | T-SQL answers `NULL`; DuckDB answers `{"many": null}` — `FOR JSON` over zero rows is NULL, and there is no row left to carry anything |
| `RuleOrder` row order, table form | unordered in T-SQL (an inline TVF cannot `ORDER BY`); the caller orders |

---

## 13. `decision_coverage` — the combinations you forgot

The strict audit (§2) answers *"is this cell understood"*. This answers *"is every input covered"* —
the harder half, and where DMN is generally weakest, because a forgotten combination fails **silently**:
a NULL decision, or whichever rule happened to be first.

```sql
SELECT * FROM decision_coverage('decision_rules');
-- issue   | region | age | matched_rules
-- gap     | EU     | 18  | []
-- overlap | EU     | 21  | [2, 3]
```

| `issue` | |
|---|---|
| `gap` | **no** rule matches — the decision is NULL and nobody asked for that |
| `overlap` | two **specific** rules match and disagree, so which wins depends on rule order |

`matched_rules` names them, which is what makes a row actionable. An empty result means every generated
case is covered, once or consistently.

### The cases are mined out of the rules themselves

Each input cell is rendered to SQL and its **literals are read back out**, so the values tried are
exactly the ones the rules talk about, plus the points either side:

| | |
|---|---|
| every string literal in that column's cells | `'EU'`, `'US'` |
| every number, **and ±1** | `18` → `17, 18, 19` |
| `NULL` | the case everyone forgets |
| one string matching no literal | `'__dmn_no_match__'` |

> **⚠⚠ The ±1 is the whole point for bands.** `[0 .. 17]` and `[21 .. 65]` *look* complete until you try
> 18. No cell is malformed and nothing in the rules says the bands must tile, so no parser can see it —
> only evaluating the boundary can.
>
> **⚠⚠ A catch-all is not an overlap.** A final unconditional rule overlaps every other rule and
> disagrees with it — that is what `First` *means*. Measured before this was handled: an ordinary
> two-band table plus an `otherwise` reported three overlaps, i.e. the commonest idiom in the language
> flagged itself. Rules whose every input cell is blank are therefore excluded from the overlap test.
>
> ⚠ **It is not a completeness proof** — it is a finite, mechanical search of the places errors live.
> The hole `18..20` is found at 18 and 20 because those are adjacent to literals; 19 is not generated.
>
> ⚠ **It is a cross product**, so the case count is the product of the per-column counts. `max_cases`
> (default 100000) refuses and names the count rather than hanging. Past four or five inputs you want
> pairwise coverage, which this does not do.

---

> ⚠⚠ **Filter its output in an OUTER query.** A pushable filter beside a differently-typed `OR` in the
> same `WHERE` trips a DuckDB internal error that invalidates the connection
> ([known-limitations.md](known-limitations.md) §1.27). `decision_check` escapes it by being built on
> `fluid_replacement_query`; this one cannot, because it stages a temp macro the generated SQL calls.

---

## 14. `decision_check` — what the table compiles to

One row per cell, with the SQL it becomes and a verdict.

```sql
SELECT * FROM decision_check('decision_rules');
-- rulepos | col    | direction | cell        | sql                                 | verdict
--      -1 | age    | in        | INTEGER     | CAST(NULL AS INTEGER)               | ok
--       0 | label  | out       | upper(?)    | (upper(label))                      | ok
--       1 | region | in        | EU, US      | preprocessed.region IN ('EU', 'US') | ok
--       2 | region | in        | COLLATE "C" | preprocessed.region = 'COLLATE "C"' | reads as a condition
```

| `verdict` | |
|---|---|
| `ok` | the cell compiles to what it looks like |
| `reads as a condition` | quoted as a literal, but the engine would have parsed `<column> <cell>` as a **condition** — the silent failure `decision_render` refuses on |
| `does not bind:` … | the cell's SQL does not resolve — a mistyped column, an unknown function, a type mismatch — followed by the engine's own message |
| `not checked:` … | this column's **datatypes** row does not bind, so nothing about its cells can be judged — see the cry-wolf note below |

> **⚠⚠ Its real value is the `sql` column, not the verdicts.** "What does this decision table actually
> compile to" cannot be answered by reading the rules — and being a **relation**, this can be reviewed,
> diffed between two versions of a rules table, or stored beside the decision it produced. It covers
> **output** cells too, which the strict audit does not.
>
> **⚠⚠ It binds.** Every cell's SQL is bound against a typed-NULL context built from the datatypes row
> — `FROM (SELECT NULL::VARCHAR AS region, NULL::INTEGER AS age) AS preprocessed` — so a mistyped
> column, an unknown function or a type mismatch is reported **per cell**, with the engine's own
> sentence, instead of arriving as one binder error against generated SQL at CREATE time.
>
> **⚠⚠ This page previously said the bind layer was impossible, and that was wrong.** The measurement
> behind it was right — a bind error does throw, and `PREPARE` raises rather than deferring — but the
> conclusion was not: a throw only needs somewhere to be *caught*, and a C# scalar is somewhere.
> `dmn_bind_check(sql)` wraps the attempt in a `try` and returns NULL or the message, which makes it an
> ordinary per-row expression and therefore set-based here. *"It cannot be done"* meant *"it cannot be
> done in Liquid"*.
>
> ⚠ It binds via `EXPLAIN`, so a cell calling a function with side effects cannot fire one — and it
> costs one host round trip per non-wildcard cell (~0.35 ms), which is why this is a review tool rather
> than something for a hot path.
>
> ⚠ `reads as a condition` still has its tail: a cell that binds as a non-boolean *expression* is
> reported too (`COLLATE "C"` does). Read the `sql` column — that is what it is for.

### ⚠⚠ The metadata rows are checked too, and a fault in one is worse

User-raised: *"we could check the outputs as well, as these can also be expressions?"* Output **cells**
already were — §4's rendering is what `decision_check` reports, and a typo in one binds like any other.
What was **not** covered is the two rows that apply to a whole **column**:

| row | what a fault there costs |
|---|---|
| `rulepos = -1`, the datatype | becomes a `CAST` in **every rule** of that column |
| `rulepos = 0`, pre/postprocessing | an expression wrapped around **every rule** of that column |

Measured before this existed: `uppr(?)` in a postprocessing row produced **no row here at all** and
surfaced later as one binder error at CREATE. One wrong cell breaks one rule; one wrong metadata row
breaks the column.

> ⚠ `?` means different things by direction — the **input** column in a preprocessing expression, the
> **output** column in a postprocessing one — so the two are rendered separately and the postprocessing
> probe binds against a context that carries the outputs as well. Bound against the inputs alone,
> `round(?, 1)` on an output would report a phantom *"column score does not exist"* on a table with
> nothing wrong with it.

### ⚠⚠ One bad datatype reports once — the cry-wolf case

The datatypes row builds the very context everything else is bound against, so a bad type poisons the
check itself. Measured with the naive version: one `INTEGERR` made **five of six rows** report *"Type
with name INTEGERR does not exist"* — one mistake, five rows — and the single row that escaped did so
**arbitrarily** (a fallback-literal cell binds against its own inline context, not this one). A checker
that reports five things for one mistake trains you to ignore it, which is the same failure
[`decision_coverage`](#13-decision_coverage--the-combinations-you-forgot) avoids for catch-all rules.

So the type is bind-checked **as the context is built**, and an unusable one falls back to `VARCHAR`:

- every **other** column is still checked, rather than being blinded by a fault in a third column;
- the `rulepos = -1` row still reports the real fault, **once**, with the type's own name;
- that column's own cells read `not checked: the datatypes row for <col> does not bind`, rather than
  carrying a verdict the substitution invented (`Cannot compare values of type VARCHAR and INTEGER` is
  a sentence about the substitute, not about the rules).

⚠ It costs one round trip per **column**, not per cell.

> ⚠ **Residual, stated rather than chased:** a cell in a *good* column that references a *bad* one
> (`@age` where `age` carries the unusable type) binds against the substitute, so it can still report a
> spurious type complaint. Per-column is the right granularity — suppressing every column that any
> other column mentions would blind the report again — and the row naming the root cause sorts first.

---

## 15. Where the tests are

| what | where |
|---|---|
| the cell grammar, every branch, offline | `dotnet/Fabricator.Bridge.Tests/DecisionRuleParserTests.cs` |
| the rules-source classifier, offline | `dotnet/Fabricator.Bridge.Tests/DecisionRuleSourceTests.cs` |
| the renderer end to end | `test/verify_decision_render.test` |
| `decision_eval` end to end | `test/verify_decision_eval.test` |
| the T-SQL renderer, deployed to a real SQL Server | `test/verify_decision_render_tsql.test` (service tier) |
| `decision_coverage` end to end | `test/verify_decision_coverage.test` |
| `decision_check` end to end | `test/verify_decision_check.test` |

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
