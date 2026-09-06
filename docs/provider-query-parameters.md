# Parameter binding for `fabricator_query` / `fabricator_exec`, and the `{% provider_query %}` tags

**Status: DESIGN ONLY, nothing built (2026-09-06).** User-raised, in two parts: *"instead of building fluid
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
| **A1** | ABI v88 + the two DIMs + SQL Server override + the `params :=` surface | `verify_raw_query`: a parameterised SELECT, an injection pair with its control (`"eu' OR 1=1 --"` answering 0 beside `"eu"` answering N), and the describe/execute schema agreement |
| **A2** | DAX override; `daxeval` delegates to the shared bag | `verify_dax` (manual — needs Power BI Desktop) |
| **A3** | Delta/deltars refusal by name | one row asserting the refusal names the provider |
| **B** | the two tags | `verify_plugin_fluid`: both paths, the multi-line body, and whichever §3.1 decision was taken, asserted |

⚠ A1's gate must include the **describe/execute agreement** explicitly — a parameterised statement whose
described schema is compared against what the scan delivers. Everything else in `verify_raw_query` passes
whether or not the two agree, which is how the original double-execution defect survived.

⚠ **A1 is the whole value.** B without A is a tag that can only carry constant SQL, and whose only way to
pass a value is a filter that renders the wrong dialect (§1.2).
