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

## Appendix — records moved verbatim from CLAUDE.md (2026-11-16)

These were open-thread or reference records in CLAUDE.md. They are closed, so they moved here word for word in the 2026-11-16 cleanup; links inside them are written relative to the repo root, as they were in CLAUDE.md.

### From CLAUDE.md §Next up — the decision-table build record (slices 1–4, the checks, coverage)

- **⚠⚠ DMN-LIKE DECISION TABLES IN `Fabricator.Functions` — SLICE 1 (the cell parser) IS BUILT AND
  COMMITTED; SLICES 2-4 ARE DESIGNED AND NOT STARTED. User-directed, 2026-09-15/16. ⚠ THE SOURCE PROJECT IS
  `D:\repos\decision-rules-py` AND ITS OWN `CLAUDE.md` IS EXCELLENT — read it before touching this; it
  documents every cell form, the rulepos conventions, the four hit policies and the aggregate mode far
  better than this entry restates them.**
  - **WHAT IT IS.** A decision table is a RELATION: one row per rule, columns split in/out, plus three
    METADATA rows — `rulepos = -2` direction (`in`/`out`), `-1` datatypes, `0` pre/post-processing, `>= 1`
    the rules. Each CELL is a little expression language (`> 18`, `EU, US`, `[21 .. 65]`, `not RU`,
    `between min(1) and max(100)`, `@age*2 > min(1)+1`, `contains(?, 'x')`). The engine turns the table into
    ONE generated SQL artifact — a DuckDB `TABLE MACRO` or a T-SQL inline TVF — with hit policies
    (First / RuleOrder / Unique / Any), `_total_hits_`, `_any_hitpolicy_violations_` and `_model_error_`.
  - **⚠⚠ THE ARCHITECTURAL SPLIT, AND IT IS THE WHOLE DESIGN: THREE LANGUAGES, EACH DOING WHAT IT IS GOOD
    AT.**
    - **C# — the CELL PARSER ONLY, because it RECURSES.** `not X` re-parses its operand, a FEEL range
      re-parses EACH BOUND as its own comparison, and a leading `?` delegates the rest of the cell back to
      the top. As SQL that is a recursive CTE over strings; in Liquid it is a recursive-descent parser in a
      template language. ⇒ **BUILT: `Fabricator.Functions/DecisionRuleParser.cs`, gated in TIER 0**
      (278 → **332**, floor bumped in `installer-core.yml`) — offline, no DuckDB, no server, two minutes.
    - **SQL — the INTROSPECTION.** The metadata rows, the pivot, the hash, and APPLYING the parser across
      every cell at once are set-based work over a relation, so they live in a `{% query %}` block.
    - **Fluid — the SQL SHAPE.** The Jinja2 templates port to Liquid almost line for line.
  - **⚠⚠ THE DIALECT PARAMETER DOES FAR LESS THAN IT LOOKS LIKE IT SHOULD, and that is the finding worth
    keeping.** Exactly ONE construct differs: `IN [a,b]` is a DuckDB LIST literal and T-SQL has none, so it
    is rewritten to parentheses. Everything else the parser emits is STRUCTURE (operators, IN, BETWEEN,
    range expansion) which is near-ANSI, while the CELL carries the target's own function vocabulary and is
    passed through UNTOUCHED — `CHARINDEX` and `contains()` both survive, neither is translated. ⇒ **a
    T-SQL or DAX target is mostly a second TEMPLATE, not a second parser.** (The source project has three
    Jinja2 templates that coexist happily; duplicating the shape beats branching one template across
    dialects.)
  - **⚠⚠ A DECISION TABLE IS CODE, NOT DATA, and the user states it as the FEATURE** (*"yes that is the
    feature, pure code so it is also fast as hell and flexible by using builtin duckdb or sql server
    functions"*). An expression cell is resolved and passed through; only a bare non-expression string
    becomes a quoted literal. The rules table is a TRUSTED AUTHORING SURFACE, exactly like a Fluid template
    — never user input.
  - **THE SLICE PLAN. ⚠ The user's sequencing is explicit: RENDER THE TARGET FUNCTION FIRST and get it
    working; decide how to embed it afterwards.**
    1. **✅ DONE — the cell parser** (`ParseCondition` / `ParseValue` / `IsAggregate`), dialect-aware, with
       the ported engine's OWN documented examples as the tier-0 assertions.
    2. **✅ DONE — `decision_render(rules [, name] [, form] [, is_temporary] [, dialect] [, source] [, hit_policy])` → the
       target macro's SQL AS TEXT.** See the as-built sub-entries below — the 2026-09-16 one for the
       introspection, and the 2026-09-20 one (FIRST) for the two forms that replaced its output.
    3. **✅ DONE (2026-09-20) — the T-SQL template**, as its own macro `decision_render_tsql`. Same
       parser, second shape. ⚠ My pre-build note here said the scalar form had "no T-SQL analogue worth
       having"; that was WRONG — a T-SQL scalar UDF returning NVARCHAR(MAX) holding JSON is exactly the
       analogue, and it is the form the user asked for first. DAX later if it earns it.
    4. **✅ DONE for the scalar half (2026-09-20)** — the EMBEDDING question answered itself once the
       row-eval form was removed: a decision's input has to be DATA, so the two forms ARE the embedding.
       ⚠ The user's *"i think both scalar and lateral are usefull"* is only half-served: the `table` form
       is a BATCH over a named relation, NOT a correlated lateral. A true lateral form
       (`FROM t, decide(t.a, t.b)`) would need the ABI v79 `lateral_*` session, not a macro — worth
       revisiting only if the scalar's per-row call turns out to cost too much.
  - **⚠⚠ `decision_check(rules)` — WHAT EVERY CELL COMPILES TO, AS A RELATION. BUILT 2026-09-21, the
    third of the three checks. A NEW `Macros/*.sql` on `fluid_query`: NO ABI, NO C++, NO parser change.
    Gate: a NEW hermetic suite `verify_decision_check` (**21**), floors 9974 → **9995** and MIN_SUITES
    81 → **82**; one mutant. Full record: [docs/decision-tables.md](docs/decision-tables.md) §14.**
    - **⚠ ITS VALUE IS THE `sql` COLUMN, NOT THE VERDICTS** — *"what does this decision table actually
      compile to"* cannot be answered by reading the rules, and as a RELATION it can be reviewed, diffed
      between two versions of a rules table, or stored beside the decision it produced. ⚠ It covers
      OUTPUT cells, which the strict audit does not.
    - **⚠⚠⚠ IT BINDS — AND I SHIPPED IT ONCE SAYING THAT WAS IMPOSSIBLE. THE CORRECTION IS THE MOST
      USEFUL THING IN THIS ENTRY.** I wrote, in four places, that a bind layer *"cannot be done per
      cell because a bind error THROWS and cannot be caught"*, with a measurement behind it: `PREPARE p
      AS SELECT (age > aege) …` raises AT PREPARE rather than deferring. **The measurement was right
      and the conclusion was wrong.** The user pushed (*"i did not understand the problem with a
      prepare … couldn't the checker execute this per cell"*) and they were correct: a throw only needs
      somewhere to be CAUGHT, and a C# scalar is somewhere. `dmn_bind_check(sql)` (Fabricator.Functions,
      via `FabricatorServices.Get<IHostQuery>()` — Abstractions was ALREADY referenced) wraps the
      attempt in a `try` and returns NULL or the message.
      - ⇒ **THE LESSON, and this codebase keeps relearning it: "it cannot be done" usually means "it
        cannot be done IN THIS LANGUAGE".** I had correctly established that Liquid cannot catch a
        failing `{% query %}` and that `json_serialize_sql` only parses, then generalised both into an
        impossibility without asking which OTHER language was available.
      - **IT ALSO MADE THE DESIGN SIMPLER, NOT MORE COMPLEX.** `dmn_bind_check` takes a PER-ROW value,
        unlike `json_serialize_sql`'s constant — so the whole generate-probes-in-Liquid dance collapsed
        into ONE set-based query, and the parse probe DISAPPEARED entirely: a statement that does not
        parse does not bind either, so binding subsumes it.
      - **IT CATCHES WHAT PARSE PROVABLY COULD NOT**, including the case recorded as unreachable:
        `MADE IN USA` → *"Referenced column USA not found"*. Also a mistyped `@aege` and a misspelled
        function (DuckDB even suggests `contains`). Measured, all three.
      - ⚠ It binds with **EXPLAIN**, so a cell calling a side-effecting function cannot fire one during
        a check; ~0.35 ms per non-wildcard cell, so it is a review tool, not a hot path.
      - ⚠ The host wraps failures in a JSON envelope, so the message is UNWRAPPED — a verdict column
        reading `{"exception_type":"IO",…` would bury the one sentence the reader needs.
    - **⚠⚠ THE CLEAN-TABLE CONTROL EARNED ITS PLACE ON THE FIRST RUN.** The first verdict chain reported
      every ORDINARY literal (`EU`) as *"does not parse"*: a quoted literal is EXPECTED not to parse as
      a condition — that is what makes it a literal — so the fallback branch has to answer for BOTH
      outcomes before the malformed test runs. **A control asserting "nothing is wrong here, AND N cells
      were still examined" is what caught it**; without the second half it would have passed a build
      that had stopped looking. Mutant P (every cell reported ok) dies at §2.
    - ⚠ §3 asserts the two surfaces AGREE — the cell `decision_check` calls `reads as a condition` is
      the cell `decision_render` refuses. If they ever disagree, one of them is lying about what the
      generated macro will do.
    - **⚠⚠ IT COVERS THE METADATA ROWS NOW — 2026-09-21, user-raised (*"we could check the outputs as
      well, as these can also be expressions?"*). Gate 29 → **49**, floor 10003 → **10023**, FOUR
      mutants each at its own row. Hermetic **82/82 — 10023**, i.e. 10003 + exactly 20, which is the
      behaviour-neutrality claim.**
      - **⚠ THE ANSWER TO THE QUESTION AS ASKED WAS ALREADY YES** — output CELLS bind like any other
        (a typo'd `@regionn` and an `rnd(1)` both report, the latter with DuckDB's *"Did you mean
        random?"*). **ASKING IS WHAT FOUND THE ROWS THAT WERE NOT COVERED**, and those are worse: a
        `rulepos = -1` datatype becomes a CAST in EVERY rule of its column and a `rulepos = 0`
        pre/postprocessing expression wraps EVERY rule of its column. MEASURED before: `uppr(?)` in a
        postprocessing row produced **no row here at all** and surfaced later at CREATE.
      - **⚠ `?` MEANS DIFFERENT THINGS BY DIRECTION** — the INPUT column in a preprocessing expression,
        the OUTPUT column in a postprocessing one — so the two render separately and the postprocessing
        probe binds against a SECOND context carrying the outputs. Bound against the inputs alone,
        `round(?, 1)` on an output reports a phantom *"column score does not exist"* on a clean table.
        ⚠ The gate needed a CORRECT output postprocess added for that to be load-bearing; with only the
        broken `uppr(?)` present, mutant S passed vacuously (a wrong context still says "does not
        bind").
      - **⚠⚠ AND IT CRIED WOLF, WHICH TOOK A SECOND MECHANISM — the datatypes row builds the very
        context everything else binds against.** MEASURED naive: one `INTEGERR` made **FIVE of six
        rows** report it, and the single row that ESCAPED did so ARBITRARILY (a fallback-literal cell
        binds against its own inline context). ⇒ the type is bind-checked AS THE CONTEXT IS BUILT and
        an unusable one falls back to VARCHAR, so every OTHER column is still checked; and that
        column's own cells read `not checked: …` rather than carrying a verdict the substitution
        invented (`Cannot compare values of type VARCHAR and INTEGER` is about the SUBSTITUTE).
      - ⚠ TWO of the four mutants first died at the SAME row, which is weaker than it looks — the
        first §6 assertion was "exactly one does-not-bind row", which BOTH break. Narrowing it to
        `rulepos = -1` and putting the fallback's own justification BEFORE the `not checked` row gave
        each its own: R (metadata rows dropped) §5, S (wrong context) the correct-output row, U (no
        VARCHAR fallback) 248, T (no `not checked`) 256.
      - ⚠ Cost is one round trip per non-wildcard cell, plus one per COLUMN for the datatype probe.
  - **⚠⚠ `decision_coverage(rules)` — THE COMBINATIONS A DECISION TABLE FORGETS. BUILT 2026-09-20, and
    it is the half the strict audit cannot reach (user: *"it is still a problem if some input
    combination has been forgotten which is in general problematic with dmn"*). A NEW `Macros/*.sql` on
    `fluid_query` (a TABLE macro): NO ABI, NO C++, NO parser change. Gate: a NEW hermetic suite
    `verify_decision_coverage` (**29**), floors 9945 → **9974** and MIN_SUITES 80 → **81**; one mutant.
    Full record: [docs/decision-tables.md](docs/decision-tables.md) §13.**
    - **THE METHOD: THE RULES MINE THEMSELVES.** Each input cell is rendered to SQL by the same parser
      the macro uses, and the LITERALS are read back out of the rendered text — so a FEEL range
      `[0 .. 17]` yields 0 and 17 without the generator knowing anything about ranges. Values per input:
      every string literal, every number AND number±1, NULL, and one string matching nothing. Cross
      product → evaluated in ONE pass through the table form (~7 µs a row), so the cost is the render.
    - **⚠⚠ THE ±1 IS THE POINT, AND IT IS WHAT NO PARSER CAN DO: `[0 .. 17]` and `[21 .. 65]` LOOK
      complete until you try 18.** No cell is malformed and nothing in the rules says bands must tile.
      Measured on a planted table: found 18 and 20 in exactly that hole, plus −1, 66 and NULL.
      ⚠ NOT a completeness proof — 19 is not generated, because it is adjacent to no literal. Say so.
    - **⚠⚠ A CATCH-ALL IS NOT AN OVERLAP, AND FINDING THAT OUT IS WHAT MADE THE TOOL USABLE.** A final
      unconditional rule overlaps EVERY other rule and disagrees with it — which is what `First` MEANS.
      MEASURED before it was handled: an ordinary two-band table plus an `otherwise` reported **THREE**
      overlaps, i.e. the commonest idiom in the language flagged itself. **A checker that cries wolf on
      the normal case trains you to ignore it.** ⇒ rules whose every input cell is blank are excluded
      from the overlap test, and an overlap counts only when TWO SPECIFIC rules match. Mutant O (the
      exclusion removed) dies at that row. ⚠ Its CONTROL is the same table without the catch-all, which
      must still report its one real gap — otherwise "reports nothing" would pass for a dead checker.
    - **⚠ IT REPORTS `matched_rules`, NOT A HIT COUNT** — *"rules 2 and 3 collide"* is actionable where
      *"something overlaps"* is not. That came out of the catch-all fix: excluding wildcards needed the
      per-rule matches anyway, so it is evaluated under `RuleOrder` and aggregated per case.
    - ⚠ A CROSS PRODUCT, so `max_cases` (default 100000) refuses AND NAMES THE COUNT rather than
      hanging — the failure mode otherwise is a query that looks stuck. Past four or five inputs the
      right answer is pairwise coverage, which this does NOT do.
    - ⚠ `function_type` for a table macro is **`table_macro`**, not `macro` — the registration
      assertion's first version was wrong about that.
  - **⚠⚠ THE STRICT AUDIT — `decision_render(strict := true)`, THE DEFAULT. BUILT 2026-09-20
    (user-directed: *"in a strict mode wouldn't you need to parse exact … couldn't we make a kind
    context with typed nulls and verify every cell? duckdb has this sql to json parser"*). For every
    DISTINCT input cell the parser QUOTED AS A LITERAL, ask DuckDB whether `<column> <cell>` would have
    parsed as a CONDITION; refuse BY NAME if it would. Gate `verify_decision_render` 411 → **426**,
    tier-0 433 → **440**, hermetic floor 9930 → **9945**; one mutant. Full record:
    [docs/decision-tables.md](docs/decision-tables.md) §2.**
    - **⚠⚠ IT FOUND A LIVE GAP ON ITS FIRST REAL TABLE, WHICH IS THE ARGUMENT FOR IT: `IS TRUE`,
      `IS FALSE` and `IS UNKNOWN` WERE MISSING FROM THE KEYWORD LIST.** `IS NULL` and
      `IS [NOT] DISTINCT FROM` were there; these were not. So a BOOLEAN input whose cell read `IS TRUE`
      rendered **`flag = 'IS TRUE'`** — always false, silent, exactly the shape §15 was written against.
      Fixed in the parser (both regexes) with tier-0 rows for the written-out AND natural spellings.
      **A keyword list can always have a gap; the engine's parser cannot.**
    - **⚠⚠ IT AUDITS RATHER THAN PROMOTES, AND THAT IS A DELIBERATE PIVOT FROM WHAT I FIRST PROPOSED.**
      I told the user the parse check would REPLACE the keyword list. Three things said otherwise once
      measured: the parser is pure C# and tier-0 testable offline (worth keeping); `json_serialize_sql`
      takes a CONSTANT, so it cannot run set-based over the cells; and — the deciding one — **letting
      the probe PROMOTE a quoted cell to a condition would create the mirror-image SILENT failure**, a
      genuine literal that happens to parse becoming a predicate. Refusing cannot invent a wrong answer.
    - **⚠ THE DETECTOR IS A RECONSTRUCTION, NOT A GUESS**: the fallback renders `<col> = '<cell, quotes
      doubled>'`, so rebuilding that string and comparing identifies the branch the parser took.
      MEASURED to separate `EU` and `Investment Grade` (fallback) from `= 5`, `> 18`, `EU, US`,
      `not RU`, `IS NULL` and `42` (other branches).
    - **⚠ THE TAIL, MEASURED AND ACCEPTED**: a cell that parses as a NON-BOOLEAN EXPRESSION is flagged
      too — `COLLATE "C"` parses AND binds in that position. Contrived as a band label; the escape is
      the same one every other ambiguity here has, QUOTE THE CELL, and `strict := false` renders anyway.
    - **⚠⚠ PARSE IS NOT ENOUGH FOR THE OTHER DIRECTION, so it was left out.** A cell the list WRONGLY
      detects renders malformed SQL — but MEASURED, `SELECT CASE WHEN MADE IN USA THEN 1 END` **parses**
      (bare, without the column) and fails only at BIND. Naming those cells would need bind probes,
      which are NOT batchable (one failure aborts the query), so that direction stays loud-but-unnamed.
      ⚠ Do not "improve" this with a parse probe; it does not work, and this is the measurement.
    - **⚠ IT COSTS `decision_eval` 26% AND IS LEFT ON THERE ANYWAY** — 20480 rows 0.647 → **0.817 s**,
      ~17 ms per chunk, because that surface re-renders per chunk and so re-audits. Left on because a
      definition arriving AS DATA is the one nobody rendered by hand. ⇒ if it bites, pass `strict`
      through from decision_eval so a caller who has validated can turn it off.
    - ⚠ `json_serialize_sql` RETURNS its errors rather than throwing (measured), which is what lets ONE
      query judge every cell; and the probes are generated from a `list(DISTINCT cell)` the plan hands
      out — Fluid CAN iterate a LIST returned by `{% query %}` (measured), which is what avoided
      duplicating the introspection CTEs. ~0.1 ms per probe.
    - ⚠ `decision_render_tsql` does NOT have the audit: DuckDB's parser is the wrong oracle for a T-SQL
      cell. It would want SQL Server's, which is not available at render time.
  - **⚠⚠ SLICE 3 IS BUILT — `decision_render_tsql`, THE SAME DECISION TABLE AS A SQL SERVER FUNCTION.
    2026-09-20, user-directed (*"let us create the t-sql versions … build for t-sql a scalar version
    which accepts json as input and always returns json and a tvf which also accepts one input row as
    json. we don't need a complete input table as json here"*). A NEW `Macros/*.sql`, DELIBERATELY
    SEPARATE from decision_render (user: *"i don't mind if we create a dedicated decision_render_tsql to
    keep decision_render more readable"*): NO ABI change, NO C++ change, NO parser change — the cell
    parser already answered for `'tsql'`, so what was missing was a statement template, exactly as this
    file predicted. Gates: a NEW **SERVICE** suite `verify_decision_render_tsql` (**72**, every section
    DEPLOYED TO AND RUN ON the docker SQL Server) + `verify_decision_render` 406 → **412**. FOUR mutants.
    Full record: [docs/decision-tables.md](docs/decision-tables.md) §12.**
    - **⚠⚠⚠ IT FOUND A REAL BUG IN THE DuckDB SIDE — ALREADY COMMITTED — AND THAT IS THE MOST VALUABLE
      THING IN THIS ENTRY. `RuleOrder`'s `many` LIST WAS UNORDERED.** `list()` is an unordered aggregate;
      measured returning **1, 3, 2**. RuleOrder's entire contract is *every matching rule IN rulepos
      ORDER*.
      - **⚠⚠ NOTHING IN `verify_decision_render` COULD SEE IT, BECAUSE EVERY ASSERTION THAT READS `many`
        SORTS IT FIRST** — §3's unnest is `ORDER BY u.rulepos`, §6's likewise. **A test that sorts the
        thing it is checking the order of is vacuous for order**, and this is the worked example. The new
        gate does NOT sort.
      - **⚠⚠ AND THREE RULES CANNOT SHOW THE SECOND HALF OF IT: `rulepos` is a VARCHAR inside the struct**
        (the UNION declares ONE type for it and aggregate mode makes that a comma-joined list), so
        ordering it as TEXT puts rule 10 before rule 2. The fix is `list(… ORDER BY CAST(rulepos AS
        INTEGER))` and the gate uses **TWELVE** rules. Mutant I (the unordered list restored) dies there
        printing `1,10,11,12,2,3,4,5,6,7,8,9`.
      - ⇒ **CROSS-ENGINE COMPARISON IS THE TEST WITH NO BLIND SPOT**, and it is why §5 of the new suite
        compares the two engines' JSON as STRINGS rather than field by field: string equality pins the
        key ORDER, the null handling and the value TYPES all at once, none of which a hand-written
        field comparison would have covered.
    - **BOTH FORMS TAKE ONE INPUT ROW AS JSON, which is the user's own deviation from their original
      template** — that one took `@inputs` as a JSON ARRAY with an `inputid` per row, which is why
      nothing here needs that column nor the `OVER (PARTITION BY inputid, …)` it forced.
      `form := 'scalar'` ⇒ `RETURNS NVARCHAR(MAX)`, JSON in JSON out; `form := 'table'` ⇒ an inline TVF.
    - **⚠⚠ THE JSON MATCHES THE DuckDB SCALAR FORM BYTE FOR BYTE, INCLUDING `decisiontable_hk`** — same
      md5 over the same cells, on both engines. Three things were needed and each is easy to miss:
      - **`FOR JSON PATH, INCLUDE_NULL_VALUES`** — FOR JSON DROPS NULL KEYS by default, so `_model_error_`
        vanished from the object whenever there was no error, i.e. almost always. Mutant L dies at the
        parity row.
      - **`ORDER BY CAST(rulepos AS INT)` before `FOR JSON`** — see the bug above; T-SQL needed the same
        fix as DuckDB, and it is legal HERE only because FOR JSON is present.
      - **`rulepos` cast to VARCHAR in the scalar form** and left INT in the table form, mirroring the
        DuckDB pair exactly.
    - **⚠⚠ THE ONE THING T-SQL FORCES: THERE IS NO BOOLEAN TYPE**, so a CASE cannot return a predicate.
      Every rule condition renders `IIF(<cond>, 1, 0)` and the CASE is compared `= 1`. Mutant J (DuckDB's
      shape) dies at the structural row. Also: `OPENJSON(@input) WITH (…)` for the input, `OUTER APPLY`
      (not CROSS — parity with LEFT JOIN LATERAL, which keeps the row), `GENERATE_SERIES` (SQL Server
      2022+), and **an inline TVF CANNOT carry ORDER BY**, so the table form does not order under
      RuleOrder and its caller must.
    - **⚠⚠ A BARE `VARCHAR` MEANS LENGTH *ONE* IN T-SQL, AND THAT FAILS SILENTLY — the one type spelling
      that is CORRECTED rather than passed through.** A rules table authored for DuckDB declares
      `VARCHAR` (THE string type there); rendered unchanged into an `OPENJSON … WITH` column definition
      it truncates every input to its first character, with nothing failing and every multi-character
      rule quietly not matching. Both bare spellings are widened to `(MAX)`; a length the author wrote is
      left alone. ⚠ The ported engine passes these through. **Mutant K is the instructive one: the
      function still DEPLOYS, and dies three assertions later on a WRONG ANSWER** — which is the whole
      reason this one is fixed rather than documented.
    - **⚠⚠ `input := 'json' | 'typed'` ADDED THE SAME DAY (user: *"we could add another tvf version
      where the inputs are typed i.e. each input as a typed param"*), AND THE USER'S REASONING FOR WHY
      BOTH MUST EXIST IS THE PART TO KEEP: *"for some cases this is easier but for others where you pass
      a wide input with more field than you need makes also sense. in such a case if business logik
      changes only the decision rules are amended with e.g. new fields and the caller needs no change.
      that is the idea about json input"*.** Gate 72 → **89**, one mutant.
      - **⇒ JSON INPUT SURVIVES A RULES CHANGE** — `OPENJSON … WITH` reads only the keys it was told
        about, so a caller may pass a record WIDER than the decision table needs, and adding an input to
        the rules leaves every CALL SITE UNTOUCHED. Gated by equality against the same call without the
        extra fields, with a changed-field row as the control.
      - **⇒ TYPED INPUT IS EASIER TO CALL** (`CROSS APPLY dbo.eval(c.region, c.age, DEFAULT)`) and type
        checks at CREATE rather than at OPENJSON time — at the price of a signature that moves whenever
        the decision table gains an input.
      - **⚠ IT IS ORTHOGONAL TO `form` RATHER THAN A THIRD `form` VALUE, and that was the cheaper design
        as well as the cleaner one**: the BODY is shared, so the only difference is where the `params`
        derived table gets its values — one line. A third enum value would have needed a fourth for the
        typed scalar, which comes free this way.
      - ⚠ The answer must NOT depend on how the row arrived, and that is asserted BYTE FOR BYTE on the
        scalar pair — which is what makes `input` a delivery choice rather than a second semantics.
      - ⚠ A gate assertion I first wrote PASTED the expected JSON including its `decisiontable_hk`, and
        the hash was from a different fixture. Rewritten as an EQUALITY against the same call without the
        extra fields: the claim is *"the extra fields make no difference"*, and a pasted literal would
        also have been pinning the hash and every other field for no reason.
    - **⚠ `decision_render` LOST ITS `dialect` PARAMETER the same day (user: *"we can remove the dialect
      ='duckdb' parameter from the decision_render if not already done"*), and REMOVING IT CANNOT BREAK
      A CALL THAT EVER WORKED** — the only value it accepted was `duckdb`; every other one was refused.
      It rendered DuckDB by construction and the parameter existed only to say no. The PARSER keeps its
      dialect argument, which is what decision_render_tsql passes `'tsql'` to.
      - ⚠ The gate rows that refused `oracle` and `tsql` are DELETED rather than edited — which is what
        the second one's own note instructed (*"this row is what must be DELETED when slice 3 lands"*).
        One row replaces them, asserting the parameter is GONE.
      - **⚠ IT ALSO VINDICATES A RECORDED PREDICTION.** This file said *"THE DIALECT PARAMETER DOES FAR
        LESS THAN IT LOOKS LIKE IT SHOULD … a T-SQL or DAX target is mostly a second TEMPLATE, not a
        second parser"*. Building it confirmed exactly that: the parser needed NO change at all, and the
        whole T-SQL slice is one statement template.
    - ⚠ The two macros DUPLICATE the introspection and must be kept in step by hand; what is NOT
      duplicated is the cell parser, so the GRAMMAR cannot drift. That was the user's call and it is the
      right one — the statement templates share almost nothing.
    - ⚠ The suite DROPS its functions at the end. They are catalog objects on the SHARED TestDB, and a
      later suite that enumerates this catalog's functions would otherwise see them — the cheap version
      of the recorded MinIO/external-table lesson.
  - **⚠⚠ `decision_eval(def, input [, hit_policy]) -> JSON` — THE DECISION TABLE ARRIVES AS DATA, ONE PER
    ROW. BUILT 2026-09-20 (user-directed: *"create a scalar function decision_eval which takes the decision
    table def as json and the input as a struct. the return type is always json for all decision table
    defs"*). C#-only — in fact ONE new `Macros/*.sql` built on `fluid_scalar`: NO ABI change, NO C++ change,
    NO C# code at all. Gate: a NEW hermetic suite `verify_decision_eval` (**56**), floors 9869 → **9925**
    and MIN_SUITES 79 → **80**; THREE mutants each killed at its own row. Full record:
    [docs/decision-tables.md](docs/decision-tables.md) §11.**
    - **⚠⚠ THE USER'S OWN CONSTRAINT IS WHAT MAKES IT POSSIBLE, AND IT IS WORTH SEEING WHY: "the return
      type is always json for all decision table defs".** A scalar's result type is fixed at BIND, where no
      row value exists — so a function whose answer depended on a PER-ROW definition could not be typed at
      all. JSON is the one type that describes every decision table at once, which also makes the
      `fluid_scalar` bind render trivial (`SELECT NULL::JSON`) instead of something that would have to
      inspect data that does not exist yet. ⚠ `to_json` over the generated macro's UNION keeps the hit
      policy in the KEY (`{"single":{…}}` / `{"many":[…]}`), so nothing is lost by flattening to JSON.
    - **THE MECHANISM, and it is the user's own sketch**: per CHUNK, read the DISTINCT defs out of
      `input_table`, render each into a TEMPORARY macro named `fab_dmn_<md5 of the def>`, then emit ONE
      SELECT dispatching each row to its own macro. ⚠ md5 because the key becomes an IDENTIFIER — a def is
      arbitrary JSON and cannot name a macro; its hash always can, and it is content-addressed, so two rows
      carrying the same rules share one macro.
      - ⚠ **This is the ONE place `is_temporary`'s recorded trap does not apply**: `exec()` runs on the
        render session's OWN connection, which is also the connection that runs the generated SELECT — so
        the macro is visible exactly where it is needed and gone afterwards. Gated (`fab_dmn_%` count = 0).
      - ⚠ The rendered text goes to `exec()` as a VALUE, never pasted into an `{% exec %}` body: a decision
        cell containing `{{` or `{%` would otherwise be read as template syntax.
    - **⚠⚠ THE COST, MEASURED PROPERLY, AND IT KILLED BOTH OPTIMISATIONS THE USER AND I PROPOSED.** Per
      2048-row chunk, warm, one distinct def: **~54 ms** (~26 µs a row). The split, measured separately
      over 20 calls each: **the RENDER 32 ms**, the CREATE **0.75 ms**, a host round trip **0.35 ms**, the
      rest ~21 ms (binding + running the generated SELECT, of which ~14 ms is the decision evaluation and
      is irreducible).
      - **⇒ INLINING the decisions into one big SELECT instead of creating macros (user's proposal) SAVES
        0.75 ms OF 54 — 1.4%.** The DDL was never the cost. Rejected on the number.
      - **⇒ CACHING THE RENDER is the only lever (up to ~32 ms, ≈2.5x) and it needs a store that SURVIVES
        A CHUNK, which this surface does not have** — `fluid_scalar` opens a FRESH CONNECTION PER CHUNK
        (its binding is shared across pipeline threads and a DuckDB connection is single-threaded), so the
        temp macros die between chunks. **⚠⚠ THE OBVIOUS STORE NEARLY CANCELS ITSELF: creating the macros
        PERSISTENTLY and skipping the render when `duckdb_functions()` already has the name moves the
        slope only 53.6 → 45.4 ms (~15%), because that catalog lookup costs ~19 ms on its own** — a cache
        whose LOOKUP costs most of what it saves. It also makes an ordinary SELECT write database objects.
        ⇒ the real in-memory cache belongs in a C# scalar, where the store is a static dictionary and the
        lookup is free. NOT built; the numbers say what it would buy.
      - ⚠ **If every row shares ONE decision table, decision_eval is the wrong tool**: `form := 'table'`
        was measured at ~7 µs a row, roughly 4x faster, and renders once.
    - **⚠⚠ A TRAP FOR ANYONE BUILDING ON `fluid_scalar`, AND ITS OWN CLASS DOC IS MISLEADING ABOUT IT: THE
      STAGED `input_table` COLUMN TAKES THE CALLER'S NAME, NOT `arg_0`.** The doc says the tail arrives as
      `arg_0`, `arg_1`, …; `ArgColumnName` actually returns `args.Schema.FieldsList[index].Name`, i.e. what
      the HOST marshalled it under — for a bare column reference `def` that is `def`. MEASURED: the first
      prototype died with *"Referenced column \"arg_0\" not found … Candidate bindings: \"def\""*. ⇒ a
      MACRO wrapping fluid_scalar must pass NAMED tail arguments (`dmn_def := def`) to pin the names, or
      the template breaks depending on how the caller spells the call.
    - **⚠ THE `JSON` ALIAS DOES NOT SURVIVE THE ARROW BOUNDARY**: the template's own `SELECT NULL::JSON`
      binds as JSON, crosses as VARCHAR, and `typeof()` then says VARCHAR. Fixed by casting in the MACRO
      body (`fluid_scalar(…)::JSON`) rather than in the template. Gated — without the macro-level cast that
      row reads VARCHAR. ⚠ Every JSON operator accepts the VARCHAR anyway, so this was invisible until
      `typeof` was asserted.
    - ⚠ `hit_policy` is PER ROW, not a render option — it reaches the generated macro as an ARGUMENT, so
      one statement can use different policies for different rows (gated, and mutant H dies there).
    - Mutants: **F** the empty-dispatch guard removed (dies at the all-NULL-chunk row with DuckDB's own
      *"syntax error at or near END"*, 15 assertions — and that row exists because writing the mutant list
      is what showed the fixture was missing); **G** the dispatch ignoring the per-row def (dies at §2's
      first table, 8); **H** `hit_policy` not reaching the generated macro (dies at §3's first row with
      `single NULL` where `many 2` is expected, 25).
  - **⚠⚠ THE RENDERED MACRO IS NOW TWO FORMS AND THE ORIGINAL ONE IS GONE — 2026-09-20, USER-DIRECTED AND
    THE USER IS RIGHT ABOUT THE PREMISE: *"the macro rendered with current decision_render is the least
    usefull one as the input values are static. actually it is not usefull at all. as stated at beginning
    of this impl only a scalar macro and a table macro which takes the input values as a table makes
    sense!"* `form := 'scalar'` (DEFAULT) | `'table'` (alias `'batch'`). C#-only, in fact ONE `Macros/*.sql`:
    NO ABI change, NO C++ change, NO parser change. BREAKING — the old signature is not renderable at all.
    Gate `verify_decision_render` 209 → **396**, hermetic floor 9672 → **9859** (79/79 — 9859, i.e. 9672 - 209 + 396 exactly, which is what shows no other suite moved); THREE mutants, each killed
    at its own row. Full record: [docs/decision-tables.md](docs/decision-tables.md) §2.**
    - **⚠⚠ WHY THE OLD FORM WAS DEAD ON ARRIVAL, and it is the reusable fact: A DuckDB MACRO ARGUMENT IS A
      BIND-TIME CONSTANT.** A macro whose parameters are the decision's INPUTS can only ever be called with
      LITERALS — one decision per call site — so `eval(region := t.region)` over a table is not expressible.
      Everything else about that form was correct, which is exactly why it survived four passes of work and
      a 209-assertion gate: **every one of those assertions called it with constants, so none of them could
      see that the surface was unusable.** ⚠ The same trap is live for any future macro surface — ask what
      a CALLER's varying value is before designing the parameters.
    - **THE PORT IS `MACRO_TEMPLATE_BATCH_EVAL` AND `SCALAR_MACRO_TEMPLATE_ROW_EVAL`** (the user named
      both), and the ported engine keeps its row-eval macro alongside; we do not, because the two that
      take data strictly dominate it.
    - **ONE INTROSPECTION, TWO STATEMENTS — which is what makes the agreement testable.** The `{% query %}`
      plan is form-INDEPENDENT except for two parameters: `$opfx` (where the OUTPUT values are read from —
      bare in the scalar's `filtered` CTE, `ranked.` out of the table form's LATERAL) and `$ind` (cosmetic
      indentation). Input references are `preprocessed.` in BOTH, which is what lets one plan serve both.
    - **⚠⚠ THE QUALIFICATION IS STILL LOAD-BEARING BUT THE ORIGINAL REASON IS GONE, AND THE NEW ONE IS
      SHARPER.** It was written for macro-parameter shadowing, which CANNOT ARISE now that the inputs are
      data. It stays because of the TABLE form: the rule predicates sit in a LATERAL beside the output
      CASEs, and **A LOCAL SELECT ALIAS BEATS A CORRELATED OUTER COLUMN OF THE SAME NAME** — MEASURED both
      ways on a 4-line repro: unqualified, `region = 'EU'` compares against the OUTPUT EXPRESSION and the
      rule never fires (NULL row); qualified, it matches. ⚠ In the PLAIN table case the COLUMN wins over
      the alias, which is why this looks safe until a lateral is involved — a probe without the lateral
      would have "proved" the qualification unnecessary.
    - **⚠⚠ A DELIBERATE DEVIATION FROM THE PORT, and it was found by the two forms DISAGREEING: the
      datatypes row must type the INPUT in BOTH forms.** MEASURED on the ported engine's own example,
      which declares `amount FLOAT` and `risk DOUBLE`: the scalar form answered **0.20000000298023224**
      (`input['amount']::FLOAT` ⇒ the CASE unifies to FLOAT ⇒ widened) and the table form **0.2**, because
      there the inputs kept the input relation's own DOUBLE columns. Same rules, same values, two answers.
      Fixed by giving the table form a `casted` CTE — the batch analogue of the scalar's `params`. The
      ported engine casts in NEITHER batch form; one more reason than it knew is that a rules table read
      from CSV arrives all-VARCHAR, so only the cast makes `age > 18` numeric rather than lexicographic.
      ⚠ Mutant A (drop the cast) dies at exactly the row that pins it, after 43 assertions.
    - **⚠ ONE DIVERGENCE IS KEPT, because it is inherent: SELECT-STYLE (LATERAL) PREPROCESSING MEANS
      SOMETHING DIFFERENT IN EACH FORM.** Scalar: the expansion is INSIDE one evaluation, so all five hits
      belong to one call (`_total_hits_` 5). Table: it happens in `preprocessed`, i.e. BEFORE the per-row
      lateral, so one input row becomes THREE INPUT ROWS decided separately (2/2/1). The ported engine
      splits the same way; what would be a defect is a form that silently did the other one. Both pinned.
    - **⚠ THE SCALAR FORM MUST SPELL ITS RESULT TYPE OUT**, so an output with NO declared datatype is
      VARCHAR there and its value is CAST, where the table form keeps the cell expression's own type.
      Inherent — a UNION type is written, and nothing short of executing the generated SQL says what the
      cells produce. The escape is the datatypes row. Both halves pinned (§18).
    - **⚠ THE NO-MATCH ANSWER IS NOW THREE-WAY, and the old entry's "two modes" line below is superseded**:
      non-aggregate SCALAR ⇒ the whole value is NULL (First/Unique/Any) or a `many` whose LIST is NULL
      (RuleOrder); TABLE ⇒ the input row comes back with NULL decision columns, so `_model_error_ = 'No
      default rule defined'` IS reachable there; aggregate ⇒ one row of NULLs. Only the scalar cannot
      report it.
    - **MEASURED BEFORE ANYTHING WAS WRITTEN — five DuckDB shapes the whole design rests on, none assumed**:
      a scalar macro body may be `(WITH … SELECT …)`; `union_value(single := …)::UNION(…)` casts; a table
      macro's `query_table(input_table)` accepts a STRING, a QUALIFIED string, a TEMP table's name AND a
      BARE IDENTIFIER (all four); `preprocessed.*` + a correlated `preprocessed.x` inside a LEFT JOIN
      LATERAL works, which is what removed the need for a second reference prefix; and `GROUP BY ALL` over
      `preprocessed.*` collapses aggregate mode correctly.
    - Mutants: **A** the table form's input cast (dies at §3's raw-value row, 43 assertions); **B** the
      scalar's `as_single` no longer yielding to RuleOrder (dies at §3's RuleOrder unnest with ZERO rows,
      44); **C** `form` validated and then IGNORED (dies at §3's first table-form row — `dr_t` was created
      as a SCALAR macro, so a FROM-position call fails — 38).
    - **⚠⚠ `is_temporary := false` ADDED THE SAME DAY (user: *"add an optional flag 'is_temporary' to
      decision_render which creates a temporary macro when set"*) — it renders `CREATE OR REPLACE
      TEMPORARY MACRO`, for EITHER form, and changes THAT ONE TOKEN. Gate 396 → **406**, floor 9859 →
      **9869**; TWO more mutants. AND IT CARRIES A SILENT TRAP THAT IS THE REAL CONTENT OF THIS ENTRY.**
      - **⚠⚠ A TEMPORARY MACRO CREATED THROUGH `fabricator_host_exec` IS INVISIBLE TO THE CALLER AND IS
        THEN GONE, WITH NO ERROR — MEASURED BEFORE BUILDING ANYTHING, which is why the flag shipped with a
        documented escape rather than as a footgun.** `SELECT fabricator_host_exec(decision_render(...,
        is_temporary := true))` returns **0**, the SUCCESS code, and `duckdb_functions()` then shows
        nothing. That function runs on its OWN connection, whose `temp` catalog dies with it — and the
        same is true of a plain `CREATE TEMP TABLE` issued that way (measured too, which is what shows it
        is the CONNECTION and not something about macros). ⇒ a temporary macro must be executed BY THE
        CALLER on the caller's own connection, which is this function's documented primary use anyway;
        `fabricator_host_exec` is the convenience path and is the one path this flag cannot take.
      - **⚠ THE GATE'S TEMPORARY PATH IS STRUCTURAL BY NECESSITY, and §19 says so rather than implying
        coverage it does not have: sqllogictest cannot execute rendered text on its OWN connection**,
        which is exactly the condition a temp macro needs. What §19 asserts behaviourally is the TRAP
        (host_exec succeeds, the macro is absent) **with the non-temporary call beside it as the
        CONTROL** — without that, "it does not exist" would be equally true of a build where
        `fabricator_host_exec` had stopped working altogether. The end-to-end half was measured by hand:
        both forms rendered with the flag, written out and `.read` back in ONE shell session, land in
        `temp.main` and answer correctly.
      - **⚠ THE ONE-TOKEN CLAIM IS ASSERTED, NOT ASSUMED**: stripping `TEMPORARY ` back out of the
        temporary render must give a BYTE-IDENTICAL string to the plain render, in both forms. Cheaper
        than re-deriving it, and it also catches a second occurrence appearing anywhere in the body.
      - **⚠⚠ MUTANT E IS THE INTERESTING ONE BECAUSE IT DIED SOMEWHERE ELSE: rendering TEMPORARY
        UNCONDITIONALLY kills the suite at §3 assertion 35 — `dr_s does not exist` — not at §19's
        default-is-off control.** Every macro in the file is created through `fabricator_host_exec`, so
        under that mutant every one of them vanishes. The trap kills the mutant, which is the strongest
        available evidence that the trap is real and that §19 describes actual behaviour rather than a
        story about it. (Mutant **D**, the flag accepted and IGNORED, dies at §19's first row, 397.)
      - ⚠ A QUALIFIED `name` and `is_temporary` do not combine — DuckDB resolves the schema inside the
        `temp` catalog, so `name := 's1.m'` fails with *"Schema with name s1 does not exist!"*. Its error,
        loud, and deliberately NOT given a refusal of ours on top.
  - **⚠⚠ SLICE 2 AS BUILT (2026-09-16) — `decision_render` + four global scalars (`dmn_condition` /
    `dmn_value` / `dmn_is_aggregate` / `dmn_relation`). C#-only + one `Macros/*.sql`: NO ABI change, NO
    C++ change. Gates: a NEW hermetic suite `verify_decision_render` (**134**), floors 9463 → **9597** and
    MIN_SUITES 78 → **79**; tier-0 332 → **389**. TWO mutants, each killed at its own row.**
    - **⚠⚠ THE FINDING, AND IT IS A DEFECT THE SOURCE ENGINE STILL HAS: A DuckDB MACRO PARAMETER SHADOWS A
      COLUMN OF THE SAME NAME ANYWHERE IN THE BODY.** ⚠ **SUPERSEDED AS A REASON (2026-09-20) — the DuckDB
      fact is unchanged and still worth knowing, but it can no longer bite THIS surface: neither form takes
      the inputs as parameters. The qualification stays for the correlated-vs-alias reason recorded in the
      entry above.** The whole `preprocessed` CTE pattern — compute
      `floor(age) AS age`, then reference `age` downstream — therefore reads the RAW ARGUMENT, so **INPUT
      PREPROCESSING IS SILENTLY INERT**: computed, never read, every rule matching the unpreprocessed
      value, nothing failing anywhere. MEASURED on a two-line macro, both directions: with a CTE aliasing
      `upper(x) AS x`, a bare `x` downstream yields `ab` and `p.x` yields `AB`. ⇒ the renderer QUALIFIES
      every input reference (`preprocessed.age`), which is the ONE escape — a qualified reference to a real
      TABLE column escapes it too (`tt.x` ⇒ the column), a bare one does not.
      - **⚠⚠ THREE PROBES PASSED WHILE IT WAS BROKEN, INCLUDING ONE I READ AS PROOF THE PREPROCESSING RAN.**
        The doc example's preprocessing is value-PRESERVING (`floor(40)` is 40, `upper('JP')` is 'JP'), so
        none of it could discriminate — and I recorded *"the lowercase 'jp' input gives the same answer,
        proving the upper() preprocessing ran"*, which proves nothing: rule 3 is `not RU`, true either way.
        **The discriminating shape is preprocessing that CHANGES the value**, and it is now §4 of the gate.
      - **⚠ IT NEEDED A PARSER CHANGE, because half the references are not ours to qualify at the call
        site.** The cell's OWN column is an argument we pass, but a cross-column `@ref` is resolved INSIDE
        the parser — so `ParseCondition`/`ParseValue` gained an optional `refPrefix`, and the SQL scalars a
        `ref_prefix` argument. Mutant B is exactly the fix that qualifies only the column: it passes §4's
        first row and dies at the cross-column row. ⚠ The two DETECTORS (`IsValueExpression` /
        `IsFullCondition`) call `ResolveRefs` with an EMPTY prefix on purpose — they strip refs only to
        decide WHICH branch a cell takes, and that must not depend on how references are spelled.
      - ⚠ A `MatchEvaluator` rather than a `"$1"` replacement string: the prefix is caller-supplied text and
        `$` is a substitution metacharacter there.
    - **⚠⚠ THE INTROSPECTION RETURNS ONE ROW OF FINISHED STRING FRAGMENTS, AND THAT IS THE USER'S OWN
      CORRECTION MID-BUILD** (*"the jinja2 templates use dicts or arrays which are not in fluid … better
      prepare the data outside of fluid so these types are not needed"*). Liquid has no dict or array
      LITERAL, so the Python engine's `inputs[].cases[]` shape is inexpressible in a template — but it does
      not need to be: the per-rule `CASE … WHEN` blocks, the parameter list and the WHERE blocks are all
      `string_agg` over every cell at once. ⇒ **the template has NO nested loops; it substitutes flat
      values.** Keep it that way — flattening in SQL costs nothing and flattening in Liquid is not
      available.
    - **⚠ THREE ROUND TRIPS PER RENDER AND THEY CANNOT BE FEWER**: the relation TEXT must exist before a
      DESCRIBE can name the columns, and the columns must be named before the introspection can be
      generated. All three are tiny. ⚠ Binds REPEAT, so a `decision_render` behind a view renders per use.
    - **THE RULES ARRIVE FOUR WAYS (user-directed: *"rules arrive as a table name but could also come from
      a file or passed as string"*), classified in C# so every branch is pinned OFFLINE.** A relation NAME,
      a FILE, JSON TEXT, or — REQUEST-ONLY — a verbatim relation EXPRESSION.
      - **⚠⚠ AUTO NEVER GUESSES `sql`.** A relation expression is not separable from a relation name by any
        cheap test that lacks DuckDB's grammar — "contains a parenthesis" would splice a table called
        `rules(2024)` — so that mode is opt-in. The other three cannot collide: JSON starts with a bracket,
        a path carries a separator or a data extension, a name is what is left.
      - **⚠ `all_varchar = true` ON CSV IS MEASURED, NOT TIDINESS**: without it the sniffer types a column
        of bare numbers as BIGINT and the cells stop being text. Every other format is the BARE literal
        (DuckDB's replacement scan) — deliberately NOT a hand-written reader per format, since `read_xlsx`
        is not even registered in this build and a signature we cannot test fails at the call site.
      - **⚠ JSON NEEDS NO SCHEMA FROM US**: `from_json(lit, json_structure(lit))` + two unnests is an
        ordinary wide relation, MEASURED on the shipped build, and a column mixing numbers and text unifies
        to VARCHAR. That is what keeps everything DOWNSTREAM identical — the introspection never learns
        where its rows came from. ⚠ It needs the json extension.
      - **⚠ `source := 'table'` DOES NOT MAKE ANY NAME REACHABLE, and the plausible reading is false**: a
        DOTTED name stays QUALIFIED (`rules.csv` ⇒ `"rules"."csv"`), which is the standard SQL reading. A
        relation literally NAMED `rules.csv` needs `source := 'sql'` with the name pre-quoted. Caught by a
        tier-0 expectation of mine being wrong.
    - **⚠ WHEN NOTHING MATCHES, THE TWO MODES DIVERGE** (⚠ SUPERSEDED 2026-09-20 — it is THREE-WAY now; see the entry above) — a NON-aggregate macro yields ZERO ROWS, an
      aggregate one yields ONE row of NULLs. So `_model_error_ = 'No default rule defined'` is
      UNREACHABLE in a non-aggregate macro, by construction. Both pinned.
    - ⚠ Two of my §3 expectations were wrong and the suite caught them: that table's rule 5 is an
      UNCONDITIONAL catch-all, so no input has one hit or zero. **Compute an expectation from the rules,
      never from the shape the assertion wants.**
    - **⚠⚠ `dialect := 'tsql'` IS REFUSED BY THE RENDERER THOUGH THE PARSER ANSWERS FOR IT, and that
      split is the honest state while one template exists** (⚠ SUPERSEDED 2026-09-20: slice 3 landed as
      `decision_render_tsql` and this macro's `dialect` parameter is GONE — it renders DuckDB, full stop): the cell vocabulary is dialect-aware, the
      SHAPE is a DuckDB `CREATE MACRO`. Rendering it would return a statement no SQL Server can run while
      LOOKING like the argument was honoured. ⚠ The gate row is what slice 3 DELETES, not edits.
    - ⚠ The refusals RAISE rather than returning a diagnostic string, because the answer is SQL a caller
      is about to EXECUTE — and each uses `SELECT 1 AS e WHERE error(…)`, never `SELECT error(…)`, for the
      recorded pruning reason.
    - **⚠⚠ THE COVERAGE AUDIT (user-asked, same day: *"have you also created tests to cover the
      parser/regex scenarios and other functionalities like different hit tests, input functions + output
      functions tests"*) — AND THE ANSWER WAS NO FOR SEVEN OF THEM. `verify_decision_render` 136 → **179**,
      tier-0 389 → **401**, floors 9599 → **9642** and 389 → **401**. One further mutant.**
      - **⚠⚠ THE SHAPE OF THE GAP IS THE REUSABLE PART: A SUITE BUILT BY WALKING A PORTED EXAMPLE INHERITS
        THAT EXAMPLE'S BLIND SPOTS.** §3's table gives its outputs NO preprocessing and §5's gives them ALL
        aggregates — so the ORDINARY scalar output postprocess (`cast((upper(label)) as VARCHAR)`) was
        reached by NEITHER, and the aggregate-mode EXCLUSION branch was unreachable because every output in
        §5 has a preprocessing expression. Both looked covered from the section headings.
      - **WHAT WAS MISSING, each now its own section**: the `Any` hit policy and
        `_any_hitpolicy_violations_` (**zero** rows — a whole policy and an output column); aggregate-mode
        output EXCLUSION; non-aggregate output postprocessing; the `In_` parameter-name stripping; a
        blank-direction column being IGNORED; the `rulepos` contiguity rule; and `decisiontable_hk`. In the parser: the
        `<` / `<=` / `!=` / `=` leading operators and the KEYWORD path into `IsFullCondition`
        (LIKE / ILIKE / REGEXP / BETWEEN), which is how a cell with no comparison sign passes through.
      - **⚠⚠ THE BLANK-DIRECTION COLUMN IS THE ONE NOTHING COULD HAVE CAUGHT, and it is the reason §9
        asserts the macro's SHAPE rather than an answer.** A `Comment` column that wrongly became an INPUT
        would still pass every row in the file: its rules cells are blank, and a blank cell is a WILDCARD,
        so the extra parameter simply defaults and matches everything.
      - **⚠ A MACRO'S PARAMETER LIST IS DETERMINISTIC AND A FUNCTION'S IS NOT — the recorded
        `unordered_map` trap does NOT apply here.** It is about `TableFunction::named_parameters`; a macro's
        `parameters` is a `vector` and its `default_parameters` an `InsertionOrderPreservingMap`. Read out
        of `duckdb_functions.cpp` rather than assumed, which is what makes §9's ordered assertion safe.
      - **⚠⚠ `rulepos` IS THE ORDERING, 1..N WITH NO GAPS — USER-CORRECTED (*"you invented the rulenumber.
        the orig just assumed the rule ordering as rule number on purpose"*), AND THEY ARE RIGHT.** I had
        built SPARSE support the model does not have, emitting the rule numbers as an explicit list. Two
        things were wrong with that: it is a capability the ported engine deliberately lacks (the rule
        NUMBER is the rule ORDER), and — as the user then pointed out — an explicit list is UGLY at fifty
        rules for no gain.
        - **WHAT IS NOT INHERITED IS ASSUMING IT SILENTLY.** `generate_series(1, <count>)` over a table
          numbered 1, 5, 9 generates slots 1, 2, 3, and a slot corresponding to NO rule is a **PHANTOM**:
          every input CASE falls through to `ELSE 1=1`, so it matches EVERY input, while the output CASE
          has no branch and answers all-NULL. Under `First` a phantom WINS, and rules 5 and 9 are never
          reached. ⇒ the assumption is CHECKED and a gap is refused BY NAME, naming the numbers (the fix
          is to renumber, and a message that only said "invalid" would not say what to renumber).
        - ⇒ **ONE spelling, always `generate_series(1, N)`** — compact at any size, which is the whole
          reason the assumption is worth keeping. ⚠ A table not starting at 1 is the same fault (slot 1
          would be the phantom) and §13 asserts it separately, or the check could be a bare count
          comparison and still pass.
        - ⚠ It also retired a cast I had added: with two spellings the `rulepos` output column changed
          TYPE with whether the numbers happened to be contiguous (generate_series is BIGINT, unnest and
          VALUES are INTEGER). One spelling, no cast, no divergence.
      - **⚠ MUTANT C: removing the aggregate-mode exclusion dies at §11 after 156 assertions with DuckDB's
        OWN binder error** — *"column \\"risk\\" must appear in the GROUP BY clause"* — which is exactly the
        reason the rule exists, and shows it is not cosmetic: without it the generated macro cannot even be
        created. ⚠ §11's absence assertion has to REFERENCE the excluded column (`SELECT risk FROM …` must
        fail); written as "select the columns I expect" it would pass vacuously.
      - ⚠ One characterization worth knowing before anyone writes it into a rules table: the numeric test
        has NO EXPONENT form, so `1e5` is not a number and falls to the string fallback — a cell reading
        `= 1e5` compares against the TEXT. FAITHFUL (the ported engine's regex is identical), not a defect;
        write `100000` or a cast, which IS detected as an expression.
    - **⚠⚠ THE CELL PARSER'S OPERATOR-KEYWORD LIST WAS MISSING `SIMILAR TO`, `GLOB` AND `IS [NOT] NULL` /
      `IS [NOT] DISTINCT FROM` — user-asked 2026-09-16 (*"duck has this 'similar to' would it fit here,
      too?"*), and the answer is yes plus three more in the same hole. Gate `verify_decision_render`
      179 → **194** (§15), tier-0 401 → **412**, floors 9642 → **9657** and 401 → **412**. One mutant.**
      - **⚠⚠ IT WAS THE DANGEROUS DIRECTION.** An undetected keyword means the cell is not recognised as a
        CONDITION, so it falls to the string fallback and renders an equality against its own TEXT —
        MEASURED: `@region SIMILAR TO 'E.*'` became `region = '@region SIMILAR TO ''E.*'''`, which is valid
        SQL that is **ALWAYS FALSE**, with nothing failing anywhere.
      - **⚠⚠ THE ASYMMETRY IS WHAT DECIDES HOW GENEROUS THE LIST SHOULD BE, and it points one way.** A
        MISSING keyword is a silent wrong answer; a SPURIOUS one is a LOUD error — the cell passes through
        as raw SQL and the target refuses it at CREATE time — and it has an escape: QUOTE the cell.
        MEASURED, and it is PRE-EXISTING rather than introduced: `MADE IN USA` already passed through
        because of the `IN` entry, while `'MADE IN USA'` renders `origin = 'MADE IN USA'`. ⇒ be generous.
        ⚠ The word boundaries do their job — `LIKED` and `GLOBAL` are still values.
      - **⚠ THE LIST IS A UNION ACROSS DIALECTS AND THAT IS CORRECT, because it DETECTS rather than
        TRANSLATES**: a keyword the target lacks just means the cell passes through and the target
        complains about the author's own SQL. MEASURED on both shipped engines: `SIMILAR TO` / `GLOB` /
        `ILIKE` are DuckDB's and not T-SQL's; **`IS [NOT] DISTINCT FROM` is in BOTH** (SQL Server 2022+),
        which makes it the portable one; and **`REGEXP` is in NEITHER** — DuckDB rejects the infix form and
        this SQL Server 2025 rig has no `REGEXP` operator *and* no `REGEXP_LIKE` function. It came in with
        the ported engine, which targeted MySQL/SQLite syntax. ⚠ It STAYS: removing it would turn a cell
        naming it from a loud error back into a silently-false comparison.
      - ⚠ `ILIKE` needs its own entry rather than riding `LIKE` — there is no word boundary between `I` and
        `LIKE`. The NEGATED forms need none: `NOT LIKE` / `NOT SIMILAR TO` / `NOT GLOB` all contain their
        base keyword. The `IS …` forms are matched as PHRASES, never as a bare `IS`.
      - **⚠⚠ AND THE KEYWORD LIST ALONE WAS THE WRONG FIX — USER-CORRECTED MINUTES LATER (*"IS NULL doesn't
        work in a cell? the '?' is assumed to the left as a default"*), AND THEY ARE RIGHT ABOUT THE WHOLE
        MODEL.** A decision-table cell is a predicate FRAGMENT about its own column, with the column
        IMPLICIT ON THE LEFT: `> 18` means `age > 18`. So a bare `IS NULL` cell must mean `age IS NULL` —
        and it did not. The keyword list only taught the FULL-CONDITION detector, which serves a cell whose
        left-hand side is written out (`@region IS NULL`); a BARE keyword cell still rendered the
        column-less fragment `IS NULL`. MEASURED: `IS NULL` → `IS NULL`, `LIKE 'E%'` → `LIKE 'E%'`.
        - **THE FIX WAS ALREADY A FAMILY, WHICH IS WHAT MAKES IT SMALL: `LeadingOperator`, `BetweenAnd` and
          `InList` have prepended the column all along.** `LIKE` / `ILIKE` / `GLOB` / `REGEXP` /
          `SIMILAR TO` / `IS [NOT] NULL` / `IS [NOT] DISTINCT FROM` were simply missing from it. One regex
          and one branch beside the leading-operator one. ⚠ The right-hand side goes through `ParseValue`
          exactly as a leading operator's does, so `LIKE E5` quotes to `region LIKE 'E5'`.
        - **⚠⚠ IT ALSO DISSOLVED THE `? IS NULL` CHARACTERIZATION I HAD COMMITTED ONE COMMIT EARLIER —
          which is the change announcing itself, and the tests were REPLACED rather than deleted.** That
          entry said `?`-at-start LOSES the column for a keyword condition and that fixing it would break
          the T-SQL bracket rewrite. Both halves are now moot: `?`-at-start DELEGATES the remainder, the
          remainder is now a LEADING KEYWORD, and the column arrives through the new rule — so
          `? IS NULL` → `region IS NULL` with **no change to the delegation at all**, and `? in [1,2,3]`
          still reaches `ListBrackets`. ⇒ **the obstacle was never the delegation; it was the missing
          rule one branch below it.**
        - ⚠ `NOT` needs no entry: `NotPrefix` runs first and WRAPS — `not like 'E%'` → `NOT (region like
          'E%')`, equivalent for NULL and non-NULL alike. `IS NOT NULL` does need its own, since it does
          not start with `NOT`.
        - **⚠ THE TRAILING `\\b` IS LOAD-BEARING**: without it `REGEXP_MATCHES(?, 'x')` and `LIKELY(?)`
          would be read as operators and mangled into `col REGEXP _MATCHES(…)`. Both pinned.
        - ⚠ BOTH mechanisms are kept and both are needed — the LEADING rule for the natural cell, the
          full-condition DETECTOR for a cell whose left side is written out. §2's equivalence row asserts
          they agree, and **that row is the cheap kill**: mutant E (the branch disabled) dies there after
          16 assertions, long before the end-to-end section.
        - ⚠ The gate's §15 fixture was rewritten into the NATURAL form for the same reason — gating
          `@region IS NULL` would have left the spelling everybody actually writes untested.
      - ⚠ MUTANT D (the old list) dies at §15's FIRST row after 182 assertions with the wildcard rule
        answering instead — `5 other` where `1 missing` is expected, i.e. the silent-fallthrough signature.
        ⚠ ONE FIXTURE discriminates all four operators at once, because with the old list every one of its
        five inputs answered 'other'.
    - **⚠⚠ THE PARSER VERIFICATION PASS (user-directed 2026-09-16: *"this decision rule impl will be a
      fundamental peace in prod … verify the parser and add comments what fragment this parser should parse
      in what context, add a sample fragment and cite the tests"*). IT FOUND A REAL DEFECT.
      `verify_decision_render` 196 → **205**, tier-0 427 → **433**, floors 9659 → **9668** and 427 →
      **433**. NEW: [docs/decision-tables.md](docs/decision-tables.md).**
      - **⚠⚠ THE DEFECT: THE `?` SUBSTITUTION WAS A BLIND `string.Replace` AND CORRUPTED A `?` INSIDE A
        QUOTED LITERAL — and `GLOB` is what made it reachable.** MEASURED before the fix:
        `GLOB 'US?'` rendered `region GLOB 'USregion'`, and **`?` is GLOB's single-character WILDCARD**, so
        that is a silently DIFFERENT pattern rather than a syntax error; `LIKE 'why?%'` and
        `contains(?, 'a?b')` went the same way. Now quote-aware (`SubstitutePlaceholder`), with the
        outside-substitution rows kept as the CONTROL. ⚠ The ported engine still does the blind replace.
      - **⚠ EVERY BRANCH NOW CARRIES `CONTEXT / PARSES / SAMPLE / TEST`**, and both public entry points
        carry a DISPATCH TABLE of the whole order — so "what does this fragment do and where is that
        pinned" is one grep rather than a re-derivation. ParseValue additionally lists the FOUR value
        POSITIONS it is called from (output cell, operator RHS, BETWEEN bound, list item, keyword RHS),
        because assuming it only serves output cells is how a rule ends up comparing against the TEXT of an
        expression.
      - **⚠⚠ THE CITATIONS ARE GUARDED, because a stale one is worse than none — nothing compiles against a
        comment.** `DecisionRuleParserCitationTests` reads the parser source (located from its own
        `[CallerFilePath]`, so no working-directory assumption) and asserts every cited name is a real test
        method. It FAILS rather than skips when the source is absent, and carries a LOWER BOUND on the
        citation count so the extraction cannot rot into finding nothing and passing. Mutation-tested: one
        renamed citation fails it, restoring passes.
      - **⚠ THE ANNOTATION IS COMMENT-ONLY AND THAT IS PROVEN, not asserted** — the masking check over
        `git diff -U0` leaves exactly the `SubstitutePlaceholder` change and nothing else.
      - **CROSS-CHECKED AGAINST THE PORTED ENGINE, so the "not built" list is complete rather than
        impressionistic** (docs/decision-tables.md §10 since the 2026-09-20 renumber). Missing here: the BATCH-eval macro (a whole input
        TABLE, `GROUP BY ALL`), the scalar-returning row variant, and the T-SQL TVF (slice 3). The
        AGGREGATE_FUNCTIONS sets are IDENTICAL, checked side by side.
        - **⚠ ONE GAP MEASURED AND DELIBERATELY NOT CLOSED: SQL-Server type spellings in the DATATYPES
          row.** The ported engine normalises `varchar(max)` / `nvarchar(N)` / `char(N)` → `varchar` when
          targeting DuckDB. MEASURED: DuckDB accepts `varchar(50)`, `nvarchar(50)` and `char(3)` and
          rejects ONLY the `(max)` spelling (*"Expected a constant as type modifier"*) — so the gap is far
          narrower than their normaliser implies, it bites only a rules table authored against SQL Server,
          and it belongs with slice 3, where dialect-aware type handling has to be designed anyway.
      - ⚠ Things that exist HERE and not in the ported engine, now stated in one place: the `rulepos`
        contiguity CHECK (same assumption, verified rather than assumed),
        `SIMILAR TO` / `GLOB` / the `IS` operators, a blank output cell rendering `NULL` (theirs RAISES —
        §16), four rules sources, and the qualification that makes input preprocessing reach the rules.
      - ⚠ Two behaviours verified and recorded as the SAFE direction rather than fixed: `not applicable` is
        read as a negation (`not` + whitespace), and a data value containing an operator keyword passes
        through as raw SQL. Both are LOUD, and quoting the cell is the escape — pinned.
    - ⚠ Validated against the SOURCE ENGINE'S OWN DOCUMENTED ANSWERS rather than against what this
      implementation produces: `('JP', 40, 500)` ⇒ First `3/approve/0.2/low/3 hits`, aggregate RuleOrder ⇒
      `'3,4,5'/flag/12.5/low`. Both exact.
  - **⚠⚠ THREE SHAPES WERE CONSIDERED AND REJECTED WITH REASONS — do NOT re-propose them.**
    - **`fluid_replacement_query` as the EVALUATION surface — REJECTED (user-corrected, and they are
      right).** Its arguments are BIND-TIME CONSTANTS, so it can only take the inputs as a NAMED RELATION —
      which is exactly what the Python batch macro does with `query_table(input_table)`, and it forces a
      caller to materialise a table before evaluating. Per-row values need a per-row surface. ⚠ It is still
      the right vehicle for the RENDER (slice 2) — rendering takes no per-row input.
    - **A scalar that CREATES TEMP FUNCTIONS and evaluates — REJECTED.** A temp object is invisible outside
      the function and would be recreated per input record batch; and a `fluid_replacement_query` renders at
      BIND, where binds REPEAT (measured in this repo: a view over one writes on EVERY use, 1 → 2 → 3 → 4).
      A render that performs DDL is a write per bind.
    - **The parser in Liquid or in a recursive CTE — REJECTED**, see the recursion note above.
  - **THE EMBEDDING TRADE, settled by the user and corroborated here.** A **SCALAR** (`fluid_scalar`) takes
    per-row values and must return ONE value ⇒ a STRUCT the caller unnests, so every output is computed and
    there is NO column pruning — but the template DECLARES its return type at bind (`is_bind`), so the
    struct is properly typed. A **LATERAL** (`fluid_query_lateral`) returns real COLUMNS and **has
    projection pushdown since ABI v86** (the template is even handed `projected`), so pruning genuinely
    works. ⇒ scalar for the simple case, lateral for the composable one.
  - **⚠ OPEN QUESTIONS FOR SLICE 2, both worth answering before building far:**
    - **How the rules reach the renderer.** A `{% query %}` at bind runs on the RENDER's own pinned
      connection — the split `table_zip` measured — so a **TEMP** rules table would be INVISIBLE while a
      regular one works. Either state that in the surface or pass the rules as JSON instead.
    - **Bind-repetition cost.** The rules are read per bind and binds repeat. Fine for a small decision
      table; worth measuring before anyone puts one behind a view.
  - ⚠ OUT OF SCOPE, user-confirmed: the Excel loader and `grist.py` — those are AUTHORING, not evaluation.
  - ⚠ One porting trap already paid for, recorded in the suite: the source doc writes an example as
    `5 >= col` where the engine really emits `5 >=col` (substitution is a literal replace, so the cell's own
    spacing survives). The doc is showing MEANING, not the output string — and Python produces the same
    thing. **When porting, disbelieve a doc's rendering of an example before disbelieving the port.**


