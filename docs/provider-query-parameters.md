# Parameter binding for `fabricator_query` / `fabricator_exec`, and the `{% provider_query %}` tags

**Status: slice A1 BUILT (ABI v88, 2026-09-06); A2 (DAX) and B (the Fluid tags) not built.**
⚠⚠ **§5 is the AS-BUILT record and it CORRECTS §2 and §3 in six places — read it first.**
The original framing: User-raised, in two parts: *"instead of building fluid
versions of fabricator_query + fabricator_exec we could add … tags to the existing fluid plugin"*, and
*"i think fabricator_query/fabricator_execute ABI version with parameter binding would be beneficial?"*

Two changes that are separable and ordered: **(A) parameters at the ABI**, which is the substance, and
**(B) two Fluid tags**, which is ergonomics on top and is nearly free once (A) exists.

---

## 1. What ships today, measured rather than recalled

| | |
|---|---|
| `fabricator_query(cat, sql)` | `{VARCHAR, VARCHAR}` — two positional args, **no parameters** |
| ABI `execute_query(handle, const char *sql, out, err)` | SQL crosses as a bare `char *` |
| ABI `execute_dml(handle, const char *sql, affected, schema_may_change, err)` | same |
| `IProvider.DescribeQuery(string sql) => null` | a DIM; **SQL Server overrides it**, Delta/DAX/deltars do not |
| `SqlServerBackend.DescribeQuery(string, IReadOnlyList<SqlParameter>?)` | **already exists**, five CDC call sites |

⚠ **The "you only learn the schema by executing" premise is provider-specific now.** `0acd679` added the
describe seam and `Bootstrap.ExecuteQuery` builds a `DescribedArrowStream(describe, execute)`: `get_schema`
describes, `get_next` executes. So `fabricator_query` runs the caller's SQL **once** on SQL Server and
**twice** (bind + scan) on every provider that returns `null` — which the interface doc states outright.

### 1.1 The DAX precedent, which is the argument for doing this at all

`daxeval` grew its own `params` bag **because `fabricator_query` had none**. So the provider with the
weakest schema story (ADOMD cannot describe at all) has parameter binding, and the provider with real
`SqlParameter` support does not. That is a capability re-implemented per provider to route around the ABI.

Its bag shape is the house convention and is **both** forms, not one — declared `NullType.Default` (the
sentinel the host registers as DuckDB `ANY`):

```sql
daxeval(expression := '…', params := {'a': 40, 'b': 2})   -- a DuckDB STRUCT, read field-by-field
daxeval(expression := '…', params := '{"a": 40}')         -- a JSON string, parsed
```

with the reason recorded at the declaration: *"a fixed VARCHAR here would force DuckDB to stringify a struct
before we ever saw it."* `fluid_render` / `fluid_query` take the same shape. ⇒ **the surface shape is already
decided; only the ABI is missing.**

⚠ **Two DAX facts are easy to conflate and are not the same mechanism.** `EVALUATE TOPN(0, {table})` is how a
**model table's** columns are discovered (`DaxCatalog.cs:259`); it cannot serve `daxeval`, whose body is an
arbitrary `DEFINE…EVALUATE`. `daxeval` instead uses `ProbeSchema` — `ExecuteReader` + `GetSchemaTable`, no
rows fetched — and the scan re-executes. Its own comment concedes the cost: *"a heavy daxeval binds slowly."*

### 1.2 Why interpolation is not an acceptable substitute

`{{ v | sql }}` is `DuckSql.Literal`, which is **DuckDB dialect**: `true`/`false`, `'\x…'::BLOB`,
`'…'::TIMESTAMP`. Strings (`''` doubling) and integers coincide with T-SQL; booleans, blobs and temporals do
not. So without (A), a provider-facing template is safe only for constant SQL and string/integer values —
and the failure is a *wrong statement*, not a refusal.

---

## 2. (A) The ABI change

**Bump to v88**, touching two entries:

```c
int32_t (*execute_query)(FabricatorHandle handle, const char *sql,
                         struct ArrowArray *params,          /* nullable, 1 row, may be NULL */
                         struct ArrowArrayStream *out, char **err);
int32_t (*execute_dml)(FabricatorHandle handle, const char *sql,
                       struct ArrowArray *params,            /* nullable */
                       int64_t *affected, int32_t *schema_may_change, char **err);
```

⚠ A 1-row `RecordBatch` is deliberately the wire form rather than a third bag encoding: `host_query` already
carries parameters exactly this way, including the bind-by-NAME rule, and the column names ride in the Arrow
schema. Reusing it means the naming rule cannot drift between the host and provider surfaces.

### 2.1 The contract, and why it cannot take `SqlParameter`

`Fabricator.Abstractions` does not reference `Microsoft.Data.SqlClient` — deliberately (the Bridge dropped
that package, and `FormatError` duck-types `Number` rather than take it back). So the contract members are
Arrow, as **DIMs chaining to the existing one-argument forms**, which is what keeps every provider compiling
unchanged:

```csharp
IArrowArrayStream ExecuteQuery(string sql, RecordBatch? parameters) => ExecuteQuery(sql);
Schema?          DescribeQuery(string sql, RecordBatch? parameters) => DescribeQuery(sql);
```

`SqlServerBackend` overrides both, converts the batch to `SqlParameter[]` once, and delegates to the 2-arg
`DescribeQuery` **it already has**. So the SQL Server side is one converter plus two overrides.

### 2.2 ⚠⚠ ONE batch, BOTH lambdas — the hazard this closes structurally

```csharp
IArrowArrayStream stream = new DescribedArrowStream(
    () => { Restore(); return catalog.DescribeQuery(query, parameters); },
    () => { Restore(); return catalog.ExecuteQuery(query, parameters); });
```

If the describe saw different parameters from the execution, the declared and delivered schemas could
disagree — and `IProvider.DescribeQuery`'s own doc names that as the serious class: *"a disagreement is a
schema mismatch, not a wrong estimate — the `duckdb_arrow_scan` class."* Handing one object to both is what
makes it airtight rather than careful.

⚠ **The describe consumes the batch's SCHEMA, not its row**, and `SqlServerBackend` says why: *"the parameter
VALUES are irrelevant — nothing executes — but the parameters must still be DECLARED, or SQL Server cannot
compile the statement it is being asked to describe."* So the load-bearing agreement is on the parameter
*declaration*; a 1-row batch carries names, types and values together, so no separate declaration concept is
needed. The existing precedent is the CDC reader, which already *"describes the very TVF call it is about to
run, bounds and all, so the schema it reports at bind and the columns it reads at execute come from one SQL
text through one mapping."* (A) generalises exactly that.

⚠ The describe keeps its **own short-lived connection**, never the transaction's pinned one — deliberately,
because materialising the write connection during a *bind* changes a later scan's routing.

### 2.3 ⚠ The C++ lifetime detail that will bite

`QueryBind` builds a factory closure and `PopulateReturnSchema` invokes it at BIND; the scan invokes it
again. So `fabricator::ExecuteQuery` — and therefore the ABI entry — is called **twice**, and the ABI's
standing ownership rule is that the managed side **consumes and releases** every `ArrowArray` passed in.

⇒ **the bind data must hold the DuckDB `Value` and export a FRESH `ArrowArray` per call**, never hold one
exported array and pass it twice. `FabricatorQueryBindData` already owns the handle and is the natural home.
This is the same class as the recorded `BuildFilterValues` bug, where a producer local to
`ArrowStreamInitGlobal` was released after the scope died — invisible on Windows and Linux, an abort on
macOS. **Whatever owns the export must outlive both calls, and each call must get its own.**

### 2.4 Per-provider mapping

| provider | `DescribeQuery(sql, params)` | `ExecuteQuery(sql, params)` |
|---|---|---|
| SQL Server | override → `SqlParameter[]` → the existing 2-arg | override → same converter |
| DAX | stays `null` (ADOMD cannot describe) | override → ADOMD `@name`; `daxeval`'s bag delegates here |
| Delta / deltars | `null` | refuse by name — there is no SQL, so a parameter is meaningless |

⚠ **Refuse, do not ignore**, on Delta: silently dropping parameters would run a statement the caller believes
was parameterised. (It has no `ExecuteQuery` worth the name either — this is the honest direction.)

### 2.5 The SQL surface

`fabricator_query(cat, sql, params := <STRUCT | JSON string>)` — a new optional NAMED parameter, declared
`LogicalType::ANY`, read from `input.named_parameters` in `QueryBind`. Same for `fabricator_exec`, on **both**
its registrations (it ships as a table function AND a scalar under one name, and the two must not drift).

⚠ `params` becomes a reserved argument name on those functions, exactly as `materialize` is on `{% query %}`.

---

## 3. (B) The Fluid tags

`{% provider_query cat r %}` … `{% endprovider_query %}` and `{% provider_exec cat n %}`, binding the rows
and the affected-count respectively.

**Naming**, settled with the user: `provider_query` over `fabricator_query`. Every other tag is short and
unprefixed (`query`, `exec`, `print`, `ret`), so the `fabricator_` stem is noise in the tag namespace — and
more importantly **a distinct name is a feature, not just readability**: it tells the reader the body is a
*different engine's dialect*.

⚠ An option on the existing block (`{% query r catalog: 'mssql' %}`) was considered and **rejected**:
`materialize:` changes where the rows *go*, while a catalog option would change which engine *parses and runs
the body*. An option that silently switches dialect is the runs-and-means-something-different shape.

**Implementation is wrap-and-delegate, C#-only in the plugin, no ABI of its own**: render the body, build
`SELECT * FROM fabricator_query(<cat>, <body>, params := …)`, hand it to the existing `Run`. The value model,
the row cap, the per-render pinned connection and `materialize:` then all compose for free.

**What the tag buys over what already works today** — `{% query r %}SELECT * FROM fabricator_query('mssql',
'…'){% endquery %}` is legal right now — is that the body stops being a quoted string argument: multi-line,
`{% for %}`/`{% if %}` inside, no escaping. That is precisely the argument that justified `{% exec %}` over
`exec("…")`.

### 3.1 ⚠⚠ The SELECT-only guard does NOT transfer, and the consequence is remote

`{% query %}` refuses a non-SELECT using **DuckDB's own parser** (`json_serialize_sql`), because a bind
REPEATS and happens WITHOUT execution — measured: `EXPLAIN` fires it, defining a view fires it, each use of
that view fires it again. For provider SQL there is no parser we can ask, and `fabricator_query` runs writes
happily (that is how the double-execution bug was measured: one call left two rows).

⇒ **`{% provider_query %}` inside `fluid_query` could perform a write on someone else's database at bind
time, repeatedly, with nothing to stop it.** Three options, and this must be a decision rather than a
discovery:

1. **Accept and document**, as `exec()` was — the two tag names are an author *assertion*. Honest, unenforced,
   and consistent with §11.1a's finding that the host-side refusal was already walk-aroundable by nesting.
2. **Refuse `{% provider_query %}` in `fluid_query`** (bind-time surface) and permit it only in
   `fluid_render`. ⚠ Does not make it safe — a volatile scalar is evaluated PER ROW instead.
3. **Ask the provider to classify.** No provider has a parser surface today; adding one is a bigger change
   than (A).

⛔ Do **not** classify by matching a leading keyword on the body — that is the measured-broken prefix check,
which admits `WITH x AS (…) INSERT …`.

### 3.2 Open, not established

**Which provider transaction does `{% provider_exec %}` run in?** A Fluid render owns its own pinned *DuckDB*
connection, distinct from the caller's, and `fabricator_exec`'s documented semantics are join-only. What that
means for a call issued from a render's pin has **not been traced**. Settle it before promising anything
about atomicity.

---

## 4. Slices and gates

| | | |
|---|---|---|
| **A1** ✅ | ABI v88 + the DIMs + SQL Server override + the `params :=` surface — **BUILT, §5** | `verify_raw_query`: a parameterised SELECT, an injection pair with its control (`"eu' OR 1=1 --"` answering 0 beside `"eu"` answering N), and the describe/execute schema agreement |
| **A2** | DAX override; `daxeval` delegates to the shared bag | `verify_dax` (manual — needs Power BI Desktop) |
| ~~**A3**~~ | ~~Delta/deltars refusal by name~~ — **NOT NEEDED**: the contract DIM refuses, so every non-overriding provider does (§5.3) | §15 asserts both refusals against a real Delta attach, with an unparameterised CTAS as the control |
| **B** | the two tags | `verify_plugin_fluid`: both paths, the multi-line body, and whichever §3.1 decision was taken, asserted |

⚠ A1's gate must include the **describe/execute agreement** explicitly — a parameterised statement whose
described schema is compared against what the scan delivers. Everything else in `verify_raw_query` passes
whether or not the two agree, which is how the original double-execution defect survived.

⚠ **A1 is the whole value.** B without A is a tag that can only carry constant SQL, and whose only way to
pass a value is a filter that renders the wrong dialect (§1.2).
---

## 5. ✅ AS BUILT — slice A1, ABI v88 (2026-09-06)

C++ + C#. Gate `verify_raw_query` **34 → 85** (service tier), §9–§16, two mutants each killed at its own
row. **Read this section before §2 and §3** — building it corrected six things the design got wrong, and two
of them would have shipped a silent defect.

```sql
SELECT * FROM fabricator_query('q', 'SELECT * FROM dbo.t WHERE region = @r', params := {'r': 'eu'});
SELECT * FROM fabricator_query('q', 'SELECT @a AS a', params := '{"a": 7}');
SELECT fabricator_exec('q', 'UPDATE dbo.t SET region = @new WHERE region = @old', {'new': 'uk', 'old': 'us'});
```

### 5.1 ⚠⚠ The WIRE and the CONTRACT are different shapes, and §2 conflated them

§2 said the wire carries "a 1-row batch with one column per parameter", reusing `host_query`'s form. That is
the right shape for the **contract** and the wrong one for the **wire**: getting there from a `params :=`
argument means deciding STRUCT-vs-JSON and, for JSON, inventing Arrow types from JSON kinds — in C++.

What ships instead is the `daxeval` shape on the wire — ONE row, ONE column named `params`, holding the
argument exactly as written — with `Fabricator.Bridge.ProviderParameters` normalising it host-side into the
contract's per-parameter form. So:

* the STRUCT/JSON branch exists in ONE language, and cannot drift between `fabricator_query` and
  `fabricator_exec`;
* C++ exports one DuckDB `Value` and knows nothing about bags;
* a provider receives a plain named batch and never sees the question.

⚠ **The two spellings are not equally faithful, and that is JSON's property rather than a shortcut.** A
STRUCT's children ARE the output columns — no conversion at all, so precision, scale, unit and time zone
survive — while JSON has four scalar kinds, so a JSON bag can only produce BIGINT / DOUBLE / VARCHAR /
BOOLEAN. MEASURED and pinned: `{'a': 41}` binds INTEGER, `'{"a": 41}'` binds BIGINT. A caller who needs a
DECIMAL or a typed temporal must use the STRUCT form.

### 5.2 ⚠ It is an `ArrowArrayStream *`, not the `ArrowArray *` §2 wrote — and that dissolves §2.3

The house form for a 1-row args batch is a STREAM: `scalarfn_bind` and `tablefn_bind` both take one, built
from a stack `ArrowProducer` the callee consumes before the call returns. Adopting it turns §2.3's lifetime
warning from a thing to be careful about into the pattern those two already use.

§2.3's substance stands and is honoured: `QueryBind` holds the DuckDB `Value` and `MakeParamsStream`
re-exports **per invocation**, because `PopulateReturnSchema` runs the factory at BIND and the scan runs it
again, and the managed side consumes what it is handed.

### 5.3 ⚠⚠ The default DIM must REFUSE, not chain — §2.1's version drops parameters silently

§2.1 proposed `IArrowArrayStream ExecuteQuery(string sql, RecordBatch? parameters) => ExecuteQuery(sql);`.
That runs the statement with the parameters **discarded**: parameterised as far as the caller knows,
unparameterised as far as the server is concerned, with nothing failing. On Delta the SQL is not even
provider SQL, so the value would vanish into a statement that never referenced it.

The default therefore throws by name when the bag is non-null:

```
DeltaCatalog does not support query parameters (the 'params' argument); it has no parameterised statement form
```

⇒ **§4's separate slice A3 ("Delta/deltars refusal by name") is unnecessary and is not being built** — every
provider that does not override refuses, structurally, and the refusal names the concrete catalog class.
§15 of the gate asserts both halves against a real Delta attach, with an unparameterised CTAS beside them as
the control.

⚠ `DescribeQuery(string, RecordBatch?)` is the deliberate exception and answers `null`. Null already means
"I cannot describe this"; the caller then executes, and the execution is where the refusal lives. Refusing
in the describe would convert a working fallback into a failure.

### 5.4 ⚠⚠ §2.2's hazard is narrower than stated — and the observable for it DOES exist

§2.2 says handing one bag to both lambdas closes a schema-mismatch hazard. One object for both is right and
is what ships, but the failure it prevents is not the one described: **a describe that LACKS the parameters
does not mismatch.** SQL Server cannot compile `SELECT @a` without a declaration, `DescribeQuery` catches and
returns null, and the caller falls back to EXECUTING to learn the schema — same schema, same rows. The
mismatch would need a describe that SUCCEEDS with DIFFERENT parameters, which one shared object makes
impossible.

⚠⚠ **I first concluded that made the property unobservable from SQL, and that was wrong.** The observable is
a SIDE EFFECT AT BIND:

```sql
EXPLAIN SELECT * FROM fabricator_query('q','INSERT INTO t VALUES (@v); SELECT 1 AS x', params := {'v': 3});
-- describe succeeded (parameters declared) => nothing runs at bind  => 0 rows
-- describe fell back  (parameters missing) => the fallback executes => 1 row
```

MEASURED both ways: **mutant A** (hand the describe a null bag while the execution keeps the real one) passes
§9 and §10 in full and dies at exactly that row, after 46 assertions. ⚠ `EXPLAIN` must be a STANDALONE
statement — it cannot be a subquery source — a recorded trap walked into while looking for this very
observable, whose failure quietly made a first attempt VOID.

Independently corroborated before the mutant existed: with `FABRICATOR_LOG_LEVEL=Debug`, a parameterised
`fabricator_query` produces a 15-line log with **zero** `describe_query: could not describe without
executing` lines.

### 5.5 ⚠ `fabricator_exec` takes the bag POSITIONALLY, and §2.5 is wrong about it twice

§2.5 said to add `params :=` to `fabricator_exec` "on both its registrations (it ships as a table function
AND a scalar under one name)". Both halves are wrong:

1. **It has ONE registration**, a scalar. The dual table+scalar registration under one name belongs to
   `fabricator_host_exec` — a different function.
2. **A DuckDB scalar has no named parameters at all**, so `params :=` is not expressible there. It is a
   `ScalarFunctionSet` with `{VARCHAR,VARCHAR}` and `{VARCHAR,VARCHAR,ANY}` overloads sharing one body, so
   the two arities cannot drift on what a bag means. §14 pins both, the 2-arity form being the control that
   adding an overload disturbed nothing.

### 5.5a ⚠ A bag key is a NAME, and the SIGIL belongs to the provider

`{'d': …}` binds `@d`; `SqlServerCatalog.ToSqlParameters` adds the `@`. That is deliberate rather than
incidental: the bag is provider-AGNOSTIC, and `@` is T-SQL's (and DAX's) sigil, not a universal one — baking
it into the key would make the contract speak one dialect. A caller who writes `{'@d': …}` gets `@@d` and SQL
Server's own *"Must declare the scalar variable @d"*, which is loud rather than silent, so it is DOCUMENTED
rather than stripped: stripping would put dialect knowledge in the host-side normaliser, which is the one
place that must not have any.

### 5.5b The bag is refused by TYPE before it can reach the JSON path

MEASURED before the guard existed: `params := MAP{...}` and `params := [1,2]` both died inside
`ArrowValueReader.ReadScalar` as *"unsupported filter value type Map"* — a message naming a subsystem the
caller never touched — and a TIMESTAMP was stringified and then reported as invalid JSON. Anything that is
neither a STRUCT nor a STRING is now refused naming its own type and both accepted shapes.

⚠ A MAP is refused rather than supported, and not only for the message: a DuckDB MAP's values are ONE type,
so a heterogeneous bag — the whole point of a bag — cannot be written as one.

### 5.6 The datetime2 pin, and why it is now one helper

A caller-supplied `DateTime` is pinned to `SqlDbType.DateTime2`, exactly as `FilterWhereBuilder` already
pinned a pushed filter value: SqlClient infers the LEGACY `datetime` (~3.33 ms) and ROUNDS the value before
the server sees it. There it cost a pushed predicate its never-erases property; **here it is simply a WRONG
VALUE, which is worse.** Both surfaces now go through `SqlServerCatalog.MakeParameter`, so the rule cannot be
fixed in one and missed in the other. **Mutant C** (drop the pin) passes 49 assertions and dies at §12's
temporal row.

### 5.7 ⚠ Two traps paid for while building it

* **`Move-Item` PRESERVES the file's mtime**, so restoring a mutated source can leave it looking OLDER than
  the DLL built from the mutant and MSBuild skips the rebuild. Mutant C's first run silently re-measured
  mutant A — and the tell was that it produced mutant A's numbers EXACTLY (same line, same 46 passed).
  Touch the file after restoring, or publish with a clean build.
* **A Delta CTAS control needs `require parquet`.** Under the default `native_write` engine DuckDB's own COPY
  writes the parquet, and `unittest` does not auto-load extensions — so the control failed as a missing
  FEATURE rather than a missing REQUIRE, this repo's recorded trap in its usual costume.

### 5.8 What is NOT built

* **A2 (DAX)** — `daxeval` keeps its own bag; `fabricator_query` against a DAX catalog refuses through the
  default. Wiring `DaxCatalog.ExecuteQuery(sql, params)` to ADOMD `@name` parameters and having `daxeval`
  delegate to the shared bag is the remaining half, gated only by `verify_dax` (manual).
* **B (the two Fluid tags)** — unchanged from §3, including the §3.1 decision, which is still a decision and
  not a discovery.
* ⚠ **No tier-0 test for `ProviderParameters`, and the reason is the ADMISSION RULE rather than effort.**
  It is pure (no pointers, no I/O) but its closure is **Apache.Arrow**, and `Fabricator.Bridge.Tests` admits a
  file only when its closure is the BCL. Widening that rule is a decision about the tier, not something to
  smuggle into this slice — and the decidable half here (which JSON kind becomes which type) is meaningless
  without Arrow types, so there is no clean split either. The gate reaches all four refusals through SQL
  instead, at one provider round trip each.
