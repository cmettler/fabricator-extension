# Fluid templating — shipped state and the follow-on plan

> **Status: slice 0 SHIPPED (`7f1940c`), slice 1 SHIPPED (`58aa6b4`, §7), slice 2 MEASURED (§8 — the answer is PERMISSIVE); slices 3–5 PLANNED.**
> The shipped part is `fabricator_render` as a bundled plugin; its record lives in
> [plugin-system.md](plugin-system.md) §The FLUID plugin. This document is the FOLLOW-ON plan, written
> down because the architectural finding in §2 is what makes slices 2–5 possible without undoing the move.

## 0. What is already there (do not re-derive it)

- **`Fabricator.FluidPlugin`** — an `IBackend` named `fluid`, contributing ONE global scalar,
  `fabricator_render(template, params)`. Params is a DuckDB `STRUCT` **or** a JSON string.
- **Pinned at `Fluid.Core 3.0.0-beta.7`** — a PRERELEASE. A bump there is a code-compatibility question,
  not a routine one, and `verify_plugin_fluid.test` is what answers it.
- **It ships**: `pack-distribution.ps1` step 2b stages it under `<managed>/plugins/`, which
  `PluginPaths.BundledRelativeRoot` makes a default search root (user root first, bundled second —
  first-root-wins).
- **Gate**: `test/verify_plugin_fluid.test` (23, service tier, its own plugin root).
- It references **`Fabricator.Abstractions` only**, which is the property §2 is about.

## 1. The four follow-ons, as the user specified them

Preserved close to verbatim, because the sketches are design input and the reasoning behind them is not
recoverable from the code.

### 1.1 `fluid_query` — a global **sqlgen** table function

Accepts a template and a JSON argument; the rendered text IS the SQL. Fluid can reach a
`System.Text.Json` `JsonNode` directly, so `JsonToClr` (the hand-rolled mapper `fabricator_render` uses)
may be unnecessary:

```csharp
var model = JsonNode.Parse(json);

options.ValueConverters.Add(value =>
{
    if (value is not JsonValue jv) return null;      // null = not handled

    var raw = jv.GetValue<object>();                 // JsonElement when parsed from text
    if (raw is not JsonElement e) return raw;        // already a CLR value

    return e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetDecimal(),
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        _ => null
    };
});
```

It should also accept a **STRUCT or MAP**, wrapping the Arrow value into a `FluidValue` with dynamic
member access.

### 1.2 A Fluid `query` function, executing SQL through `host_query`

The result set (an Arrow `Table`, or the `RecordBatch`es directly) is wrapped for the template:

```csharp
class ArrowRow(Table table, int row, TemplateOptions options) : IFluidIndexable
// or
class ArrowRow(IReadOnlyList<RecordBatch> batches, int row, TemplateOptions options) : IFluidIndexable

var rows = Enumerable.Range(0, (int)table.RowCount)
                     .Select(r => new DictionaryValue(new ArrowRow(table, r, options)))
                     .ToList();
```

**The cell unboxing here is the SAME unboxing a STRUCT/MAP argument needs in §1.1** — build it once.
Access by NAME via the dictionary, and **by INDEX too**, for the case where no name is available. A nested
list should map back to an enumerable.

### 1.3 `ITemplateFileProvider` over the DuckDB VFS

Enables importing macros from non-local filesystems. ⚠ The user's own flag: the VFS lives in the bridge, so
this risks an ugly dependency affecting bundling/install — §2 is the answer.

### 1.4 DuckDB functions/macros callable from inside Fluid

Via dynamic resolution and execution through `host_query`. The largest and least-specified item.

## 2. ⚠⚠ THE ARCHITECTURAL FINDING — what a PLUGIN can reach, and the seam that fixes it

Established by reading, 2026-09-01, and it is what keeps all of this out of the Bridge:

| capability | where it lives | reachable from a plugin? |
|---|---|---|
| `IBackend.GlobalSqlTableFunctions` | `Fabricator.Abstractions` | **YES** — §1.1 needs no new plumbing |
| `Host.Query` (host_query) | `Fabricator.Bridge` | **NO** — blocks §1.2 and §1.4 |
| `HostFs` (the DuckDB VFS) | `Fabricator.Bridge` | **NO** — blocks §1.3 |

**The precedent to copy is `Fabricator.Abstractions/HostHttpTransport.cs`**: a capability DECLARED in the
contract assembly and FILLED IN by the bridge at boot, so a plugin uses it with the Abstractions reference
alone. Its own doc carries the rule that must be copied with it:

> ⚠ **The delegate carries no opener, deliberately** — the bridge's implementation reads the AMBIENT
> `ClientContext` at call time. Anything holding an ATTACH-time `ClientContext*` is a dangling pointer the
> day that connection closes (the `table_stats` SIGSEGV class), and reading the ambient is also the correct
> answer for SECRETS, which the user may create after the ATTACH.

⇒ **Slices 2–5 add `HostQueryTransport` and a file-provider seam in Abstractions, mirroring that shape.**
They do NOT move Fluid back into the Bridge, and they do NOT widen the plugin's reference to the Bridge.

⚠ The corollary from `HostHttpTransport`'s remarks applies verbatim: such a seam is usable only from INSIDE
an ABI crossing, or where the ambient still flows from one. `AsyncLocal`, so it survives `await` and
`Task.Run`; it does NOT survive a thread parked before the crossing began.

## 3. ⚠⚠ THE HAZARD TO SETTLE BEFORE BUILDING §1.2 OR §1.4 — **MEASURED AND CLOSED, see §8**

**A sqlgen function renders at BIND.** So a Fluid `query` function used inside `fluid_query` would run
`host_query` *while DuckDB is binding a statement* — re-entrant query execution during bind. This project
has been bitten by that class twice: the ABI v80 scalar-bind ambient SIGSEGV under `OPTIMIZE`, and the
standing rule *never call anything that BINDS while holding `entry_lock_`*.

**✅ ANSWERED IN §8 (2026-09-01): it IS fine, and the transaction semantics are the same at bind and at
execute, so there is no asymmetry to state.** The rest of this section is the reasoning that motivated the
probe, kept because the two incidents it cites are real and NOT repealed by the result. It also under-called
one thing: the probe found a hazard neither branch below anticipated — a bind-time WRITE fires on `EXPLAIN`
(§8.3), which constrains slice 3's surface.

The original framing was:

- If bind-time `host_query` is safe ⇒ `query` works in both `fabricator_render` (execute time) and
  `fluid_query` (bind time).
- If it is not ⇒ **`query` is available at execute time and REFUSED at bind time**, which is a real
  asymmetry the surface has to state rather than hide.

## 4. The slice order (agreed 2026-09-01)

| slice | contents | why here |
|---|---|---|
| **1** ✅ | `fluid_query` + **the value model**: the `ValueConverters` mapping, and the Arrow→`FluidValue` wrapping (`ArrowStruct : IFluidIndexable`, name AND index access, nested list → enumerable). BUILT — see §7 | Slices 3 and 5 both reuse the value model, so it was built ONCE here. Needed no new ABI and no new seam, exactly as §2 predicted |
| **2** ✅ | **Probe** bind-time `host_query` (§3) — DONE, see §8 | One measurement; it came back PERMISSIVE, and added a SELECT-only refusal to slice 3's scope |
| **3** | `HostQueryTransport` seam in Abstractions + the Fluid `query` function | Needs 1's row wrapping and 2's verdict |
| **4** | `ITemplateFileProvider` over a host-FS seam (§1.3) | Independent of 3; same seam pattern |
| **5** | Dynamic DuckDB function/macro resolution from Fluid (§1.4) | Largest and least specified; scope it DOWN once 1–3 exist |

## 5. Things to get right in slice 1 (so they are not rediscovered)

- **⚠ MEASURE BOTH ARMS OF THE CONVERTER.** `jv.GetValue<object>()` returns a `JsonElement` when the node
  was parsed from TEXT and a CLR value when it was built in memory — which is why the sketch has both arms.
  Assert each, rather than assuming which one fires.
- **⚠ `GetDecimal()` for every number is a SEMANTIC choice, not a detail.** It removes the int/float split,
  so `{{ n }}` may render `3` where the old path rendered `3` and `3.0` differently. In a function whose
  output is SQL TEXT that changes the generated statement — pin it.
- **⚠ RENDERED TEXT BECOMES SQL, so a JSON value can become SQL.** That is inherent to sqlgen and it is what
  the feature is for, but the in-tree precedent is an ALLOW-LIST literal renderer (see
  `GfVaValuesFunction` in `Fabricator.SqlServer/CustomFunctions.cs`). Follow it, and say in the docs what is
  and is not escaped.
- A sqlgen function receives only **constant VALUES, never expressions** — so the STRUCT/MAP argument must
  be a constant. That is a binder property, not ours (see macros-and-sqlgen-functions.md §2).
- The gate is `test/verify_plugin_fluid.test`; the service floor moves with it
  (`scripts/run-suites.sh`, currently **3092**).

## 6. Open, and deliberately not decided

- ~~Whether `query` returns rows as `DictionaryValue(ArrowRow)` (name access) with index access layered on,
  or a single value type serving both.~~ **DECIDED in slice 1 and already built: ONE type.**
  `DictionaryValue(ArrowStruct)`, where `ArrowStruct.TryGetValue` tries the name and then an int-parse
  ordinal — because Fluid asks for the key `"0"` when it sees `r[0]`, so index access needs no second
  mechanism (§7.4). Slice 3 reuses it as-is.
- Whether §1.4 is worth building at all once §1.2 exists — a template that can run SQL can already call any
  DuckDB function through it. Re-derive the case for it AFTER slice 3, rather than inheriting it from this
  list.

## 7. Slice 1 AS BUILT (2026-09-01) — `fluid_query` + the shared value model

C#-only, NO ABI change, NO C++ change, no bridge change: `IBackend.GlobalSqlTableFunctions` already existed,
which is the §2 finding paying out immediately. Three new files in the plugin
(`FluidValueModel.cs`, `FluidEngine.cs`, `FluidQueryFunction.cs`); `FluidPlugin.cs` keeps only the two
`IBackend` members and the render function. Gate `verify_plugin_fluid` **23 → 89**, seven mutants each killed
at its own assertion.

```sql
SELECT * FROM fluid_query('SELECT {{ n }} AS n', params := {'n': 7});
```

`template` is positional and NON-nullable; **`params` is NAMED and optional** (the `fabricator_sql_seq(2,
cols := 3)` precedent), so a template with no variables is simply `fluid_query('SELECT 1')`. It takes the
same bag `fabricator_render` does — a STRUCT, a MAP, or a JSON string — because a params bag has to mean the
same thing in both.

### 7.1 ⚠⚠ THE FINDING THE SLICE TURNS ON: Fluid's native `JsonNode` support RENDERS CORRECTLY AND COMPUTES WRONG

Fluid 3.0.0-beta.7 understands `System.Text.Json`'s `JsonNode` with no help from us, and the first probe of
that looked like a clean simplification — `JsonToClr` could be deleted outright. **It is a trap, and only a
probe that COMPARES and does ARITHMETIC can see it.** Bound with no value converter, MEASURED:

| template | expected | bare `JsonNode` | with the converter |
|---|---|---|---|
| `{{ d.i }}` (i = 3) | `3` | `3` ✓ | `3` ✓ |
| `{{ d.big }}` (2^53+1) | exact | `9007199254740993` ✓ | `9007199254740993` ✓ |
| `{{ d.o.a.b }}`, `d.arr[1]`, `d.arr.size`, `{% for %}` | — | all ✓ | all ✓ |
| `{% if d.i > 1 %}` | `big` | **`small`** ✗ | `big` ✓ |
| `{% if d.s == 'x' %}` (s = "x") | `y` | **`n`** ✗ | `y` ✓ |
| `{{ d.money \| plus: 1 }}` (19.99) | `20.99` | **`1`** ✗ | `20.99` ✓ |
| sum an array in a loop | `60` | **`0`** ✗ | `60` ✓ |

The leaves arrive as opaque nodes: they format faithfully and compare as nothing. ⇒ **every render assertion
in `verify_plugin_fluid` passes on a build with no converter**, which is why the suite now asserts comparison
and arithmetic on BOTH the JSON and the Arrow path, and why mutant M1 (drop the converter) is the one that
justifies the file. For `fluid_query` a wrong `{% if %}` branch is a wrong SQL STATEMENT, not a formatting
nit.

⚠ The control that makes this a measurement rather than a guess: `d.Root` / `d.Parent` / `d.Options` do NOT
resolve, so the native handling is real `JsonNode` support and not reflection over its CLR members.

### 7.2 ⚠⚠ A SHIPPED BUG IT FOUND — and it is this repo's own recorded trap, one method away from where the trap is already documented

`JsonToClr` read:

```csharp
JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
```

C# unifies a conditional operator's branches, and `long` converts to `double` implicitly (not the reverse),
so **the whole expression is `double` and the int64 branch has never had any effect.** MEASURED directly:
`TryGetInt64` returns true, and the boxed value's runtime type is `Double`; with explicit `(object)` casts it
is `Int64`.

The user-visible consequence: `fabricator_render('{{ n }}', '{"n":9007199254740993}')` returned
**`9007199254740990`**, while the identical value as a DuckDB `BIGINT` returned it exactly. Two independent
losses compounded — the silent widening to double, then Fluid's `Convert.ToDecimal(double)`, which keeps only
15 significant digits.

⚠ **`ReadTimestamp`, in the same file, carries a comment explaining this exact hazard** ("the explicit
`(object)` casts are load-bearing — without them C#'s conditional operator unifies both branches"). A
documented trap in one method did not stop it being written in the next. Nothing caught it because it is
invisible for every integer under 2^53, which is every integer a test happens to use.

### 7.3 The number ladder — `int64` → `decimal` → refuse

Each rung was measured, and the reason the user's `GetDecimal()` sketch is not taken alone is the first one:

- **int64 first.** Exact for every integer a JSON document can hold in one. `GetDecimal()` is also exact
  here, so this rung is about keeping `9007199254740993` away from the double path, not about beating decimal.
- **decimal second.** Exact for ordinary fractional values, and it is what keeps `19.99` at `19.99`.
- **double last, and only to reach the refusal.**

⚠ **Fluid's number model IS decimal**, so a magnitude outside decimal cannot be represented at all —
`Convert.ToDecimal` is literally what Fluid calls to build a `NumberValue`. Both ends are refused with a
message naming the value and its JSON path, because the alternatives are worse:

| input | before | now |
|---|---|---|
| `1e100` | Fluid's `OverflowException: Value was either too large or too small for a Decimal` — naming neither the parameter nor the value | refused, naming both |
| `1e-30` | rendered **`0`** | refused |

⚠ `TryGetDecimal` SUCCEEDS for `1e-30` and returns zero: **decimal's RANGE is not its RESOLUTION.** Hence the
explicit `m == 0m && !IsJsonZero(e)` check (mutant M4), with a real `0` asserted beside it as the positive
control — without which the check would pass equally on a build that refused every zero.

⚠ **A CONSEQUENCE, pinned as a decision:** taking the decimal rung preserves a JSON number's written form, so
`3.0` renders `3.0` where the double path rendered `3`. Better for a function emitting SQL (the literal keeps
its type), but it IS a change to `fabricator_render`'s output.

⚠ **STILL TRUE AND NOT FIXED:** a genuine CLR `double` (a DuckDB `DOUBLE` column) has ~17 significant digits
and Fluid keeps 15. That is Fluid's model, not something the ladder can route around; a `DOUBLE` params value
with more than 15 significant digits renders rounded.

### 7.4 The Arrow half — one unboxing, shared with slice 3

`FluidValueModel.ReadCell` is a deliberate SUPERSET of `ArrowValueReader.ReadScalar` (Bridge-only, so
unreachable from a plugin — §2), extended with the nested cases the bridge's reader has no counterpart for,
since that one exists for FILTER values and those are scalars by construction:

- `StructArray` → `DictionaryValue(ArrowStruct)`, `MapArray` → `DictionaryValue(ArrowMap)`,
  `ListArray`/`LargeListArray`/`FixedSizeListArray` → `List<object?>`, recursively.
- **`ArrowStruct : IFluidIndexable` is the row wrapper slice 3 needs**, built here as the plan requires.

⚠ **ORDINAL ACCESS IS FREE AND IS NOT A SECOND MECHANISM.** MEASURED: Fluid resolves `r[0]` by asking
`TryGetValue` for the KEY `"0"`, so an int-parse fallback IS index access. A member genuinely named `0` wins
over the ordinal, which is the right precedence — the data's own names come first.

⚠ **`TryGetValue` must return FALSE for an unknown member, not a nil value**: returning false is what lets
Fluid answer `.size` and friends itself. A member the struct really has then shadows those, which is again
the right way round.

⚠ **`MapArray` derives from `ListArray` in Apache.Arrow**, so the `case ListArray` arm matches a MAP first.
The compiler caught it here (CS8120, unreachable case) — but ordered the other way it is not an error, it is
a silently WRONG SHAPE: a MAP arrives as a list of key/value structs, which renders and iterates happily
while every lookup by key fails. Mutant M3.

⚠ `DictionaryValue` exposes neither `Keys` nor `TryGetValue` publicly, so spreading a bag's members means
holding the `IFluidIndexable` rather than unwrapping the `FluidValue`.

### 7.4a ⚠⚠ TEMPORAL AND BINARY VALUES — one wrong VALUE fixed, one wrong COMPARISON surfaced

Found by probing edge cases after the slice was otherwise finished, which is the only reason they were found
at all: nothing in the plan mentions dates.

**A DATE RENDERED THE PREVIOUS DAY.** On a UTC+2 box, `fabricator_render('{{ d }}', {'d': DATE '2026-09-01'})`
returned **`2026-08-31 22:00:00Z`**. `Date32Array.GetDateTime` returns a `DateTime` with
`Kind = Unspecified`, which Fluid resolves against the machine's LOCAL zone. Pre-existing, and made worse by
this slice: `fluid_query` would splice that wrong date into a statement. Fixed by stamping the Kind
(`DateTime.SpecifyKind(..., DateTimeKind.Utc)`); mutant M6.

⚠ **BOTH OBVIOUS FIXES WERE MEASURED AND BOTH ARE WRONG** — worth recording, because each looks like the
answer:

| candidate | measured result |
|---|---|
| `TemplateOptions.TimeZone = TimeZoneInfo.Utc` | **changes NOTHING.** An Unspecified midnight renders identically under UTC and local — the conversion happens where the DateTime becomes a DateTimeOffset, not against that option |
| return `DateOnly` instead | **worse.** Fluid has no `DateOnly` support: it degrades to a `StringValue` rendering `09/01/2026` (culture-dependent), and reaches the `sql` filter as a quoted STRING |
| stamp `DateTimeKind.Utc` | correct — `2026-09-01 00:00:00Z`, and the wall-clock TIMESTAMP path (already `Kind = Utc`) is unaffected, which is the control |

**A BLOB CAME OUT AS `9798`.** `byte[]` reaches Fluid as an array, which renders as concatenated decimal
bytes — looks like a number, useless. Now a lowercase HEX string (`6162`): lossless, readable, and it
compares as a string. Mutant M7.

**⚠⚠ FLUID DOES NOT ORDER TEMPORAL VALUES AT ALL, and this is NOT fixed — it is gated and documented.**
MEASURED with controls: `>` and `<` are BOTH false for two different dates, as `DateTime` and as
`DateTimeOffset` alike, while `==`/`!=` behave; numbers and strings compare correctly with the same
operators, so it is specific to temporals. **So `{% if d > cutoff %}` silently takes the ELSE branch** — the
same failure class as §7.1, except this one belongs to Fluid.

- The workaround works and is gated: format to ISO first, then compare strings
  (`{% assign x = a | date: "%Y-%m-%d" %}`), ISO-8601 sorting being lexicographic — verified across a year
  boundary as the negative control.
- **⚠ AN ISO-STRING VALUE MODEL WOULD FIX IT AND WAS DELIBERATELY NOT TAKEN.** Measured: as ISO strings,
  comparison is correct, rendering IMPROVES (`2026-09-01` rather than `2026-09-01 00:00:00Z`), and
  `| date:` still works because Fluid parses the string. What stopped it is that a date would stop BEING a
  date — a user-visible semantic change to a shipped function, and `| sql` would emit a quoted string rather
  than a temporal literal. That is a decision to put to the user, not one to smuggle into a slice about
  something else. **The measurement is done, so the choice is one edit away.**
  - **⚠ I FIRST CALLED THE TIMEZONE TRAP "the strongest argument on the table" FOR SWITCHING, AND THAT WAS
    AN OVERSTATEMENT — corrected once the expected deployment was stated.** At `TimeZone = 'UTC'`, which is
    what a Delta-oriented deployment runs, every route agrees and the trap does not fire at all. So it is a
    supporting argument for a deviating session, not the decisive one.
  - **What actually remains decisive is the ORDERING gap**, which is timezone-independent:
    `{% if d > cutoff %}` is false for ANY two dates in ANY session, and an ISO string fixes it.
  - **⚠⚠ AND THERE IS NOW A REASON TO WAIT: Fluid's main branch carries TIMEZONE WORK that 3.0.0-beta.7 does
    not** (reported by the user; not verified here). If that lands and touches ordering or the date model,
    switching to ISO strings now could be work undone — or worse, a divergence from an upstream fix. **Take
    the next Fluid bump first, re-run `verify_plugin_fluid`'s temporal assertions, and re-derive this
    decision from what they then say.** ⚠ Re-derive it before slice 3 either way: slice 3 puts whole query
    ROWS through the same value model, which multiplies whichever choice is made.

**⚠ `| sql` COLLAPSES EVERY TEMPORAL TO `TIMESTAMPTZ`**, whatever it started as, because Fluid's date model
is one `DateTimeOffset` — the INSTANT survives, the TYPE does not.

⚠⚠ **AND GETTING A DATE BACK OUT IS A SILENT TIMEZONE TRAP.** Anything that reads a TIMESTAMPTZ without
NAMING a timezone reads it in the SESSION's timezone, so in any session west of UTC it yields the PREVIOUS
DAY with no error. MEASURED on the literal `| sql` produces for `DATE '2026-09-01'`:

| route | UTC | America/New_York | Australia/Sydney |
|---|---|---|---|
| `::DATE` | 2026-09-01 | **2026-08-31** | 2026-09-01 |
| `::TIMESTAMP::DATE` | 2026-09-01 | **2026-08-31** | |
| `date_trunc('day', …)` | 2026-09-01 | **2026-08-31** | |
| `strftime(…, '%Y-%m-%d')` | 2026-09-01 | **2026-08-31** | |
| `extract('day' FROM …)` / `date_part` | 1 | **31** | |
| `(… AT TIME ZONE 'UTC')::DATE` | 2026-09-01 | 2026-09-01 | ok |
| `{{ d | date: '%Y-%m-%d' | sql }}::DATE` | 2026-09-01 | 2026-09-01 | ok |

That is DuckDB behaving correctly — it is our TIMESTAMPTZ representation that makes it a trap. Two routes
are safe and both are gated: **name the zone** (`AT TIME ZONE 'UTC'`, which also works for a genuine
TIMESTAMP and preserves the instant), or **never build a TIMESTAMPTZ** (`{{ d | date: '%Y-%m-%d' | sql }}::DATE`,
which additionally needs no ICU). The README teaches the second for a DATE.

**⚠⚠ WHAT IT COSTS IN PRACTICE, AND THE EXPECTED DEPLOYMENT IS SAFE.** DuckDB's `TimeZone` defaults to the
SYSTEM zone once ICU is loaded — measured `Europe/Berlin` on this box, **not** UTC — but a fabricator
deployment is expected to run at `SET TimeZone = 'UTC'`, because that is what the Delta protocol stores and
accepts, and there every route agrees. So this is a trap for a session that DEVIATES from the expected
configuration, not something the normal path walks into. It is gated anyway, because "the normal path is
safe" is exactly the reasoning under which a trap survives unrecorded.

**⚠⚠ AND THERE ARE TWO DIFFERENT CLOCKS HERE — do not conflate them.** The DATE-renders-previous-day bug
above is the **.NET** side reading `TimeZoneInfo.Local`, i.e. the OS zone of the machine running the
extension; setting DuckDB's `TimeZone` to UTC does NOT affect it, so the `AsUtc` fix stands regardless of
deployment. This section's trap is the **DuckDB session** zone, and that one IS neutralised by running at
UTC. Same symptom, different clocks, different fixes.

⚠⚠ **A CORRECTION WORTH KEEPING, because the wrong version was written into five places before it was
caught — by the user pushing back, not by a test.** This section first asserted that `::DATE` DID NOT EXIST
(*"Conversion Error: Unimplemented type for cast"*). **That was an artifact of the suite, not a fact about
DuckDB**: the cast needs ICU, `unittest` does not auto-load extensions, and the suite had no `require icu` —
so a missing REQUIRE presented as a missing FEATURE. The repo already records the opposite direction (a
`require` for something not compiled in SKIPS silently); this is the same hazard the other way round and it
is worse, because it reads as a definite negative result about the engine. ⇒ **before recording "DuckDB
cannot do X" from a suite failure, check what the suite loaded.**

⚠ The raw `| sql` output is asserted **by TYPE and by INSTANT, not by rendered text**: a TIMESTAMPTZ's
display form depends on the session timezone, so pinning the string would make the suite report the runner's
locale. The timezone section sets `America/New_York` explicitly, because under the runner's default (UTC)
every route above agrees and the section would pass while saying nothing.

⚠ **THE TIMEZONE HALF IS NOT PROVABLE BY THIS SUITE ALONE** and it says so: the date assertions are correct
on a UTC box whether or not the Kind fix is present. What makes them discriminating is running where local
time is not UTC — which is where the bug was found, and where mutant M6 dies.

### 7.5 What is and is not quoted

`{{ x }}` interpolates **RAW, deliberately** — a template must be able to emit object names, predicates and
whole SQL fragments, which is the only reason to generate SQL from a template. Values that are DATA get an
explicit filter, both allow-lists following the `fabricator_va_values` precedent:

- `{{ v | sql }}` → `DuckSql.Literal` (quoted string, invariant number, typed date/time, `NULL`)
- `{{ n | sql_ident }}` → `DuckSql.QuoteIdent`

A value with no provably safe rendering is refused BY NAME rather than interpolated — a list reaching `| sql`
is an error, not a `ToString()`. ⚠ `DuckSql` lives in `Fabricator.Abstractions`, so the plugin reaches it
with the reference it already has; that is not luck, it is the same property §2 is about.

### 7.6 The sqlgen properties, gated

MEASURED via `EXPLAIN`: `SELECT id FROM fluid_query('SELECT * FROM fq_t WHERE g = {{ g }}', params := {'g': 3})`
plans as a bare `SEQ_SCAN` on `fq_t` with `Projections: id` and `Filters: g=3`. The call is GONE and both
pushdowns reached the base table — the property that separates sqlgen from a marshaled table function, and
one no row assertion can see. ⚠ `EXPLAIN` cannot be a subquery source, so the gate uses the sqllogictest
`<REGEX>:` form on the `physical_plan` row.

A VIEW over `fluid_query` re-binds on every use, which is why `GenerateSql` must stay deterministic and
side-effect-free — and it is exactly why slice 3's Fluid `query` function is a separate question (§3) rather
than a free addition.

### 7.7 Driving the template from SQL — measured, because "where does the bag come from" is the first thing anyone asks

A sqlgen generator receives constant VALUES, never expressions, so a literal bag is not the only interesting
case. Both of these work and both are gated:

- **`params := ?` in a prepared statement.** DuckDB re-binds every `EXECUTE`, so the generator runs again and
  the substituted SQL differs per execution. ⚠ **The OUTPUT SCHEMA may therefore differ between two
  EXECUTEs of ONE prepared statement** — measured: the same `fq_pc` returns one column for
  `{'cols': ['a']}` and three for `{'cols': ['a','b','c']}`. Surprising enough to pin, since a prepared
  statement is normally a fixed plan with a fixed result shape. (Same property the lateral bind-time
  constants recorded for `f(t.n, ?)`.)
- **`getvariable()`.** `params := {'cols': getvariable('fq_cols')}` reads the bag from a session variable —
  the idiom the CDC reader documents for carrying a cursor, and what lets a template-driven pipeline need no
  client and no spliced literal.

### 7.7a What the gate does NOT cover, said plainly

- **The converter's second arm is unexercised.** `GetValue<object>()` returns a `JsonElement` for a node
  parsed from TEXT and a boxed CLR value for one built IN MEMORY; both were MEASURED, but every call through
  the plugin today parses from text, so only the first arm has a test. The second is kept because it is two
  lines and because slice 3 — a Fluid `query` assembling a model in memory — is exactly the caller that
  reaches it. Do not read its presence as covered.
- **Nothing asserts thread safety.** `TemplateOptions`, the `FluidParser` and the parsed-template cache are
  shared across a batch and across concurrent scans; the reasoning is that all three are read-only after
  construction (`ConcurrentDictionary` for the cache), not that a test proved it.
- **`fluid_query` is service-tier only**, because the hermetic tier's plugin root is empty by design — so a
  hermetic run says nothing about any of this.

### 7.8 What slice 1 leaves for the rest

- The value model is DONE and is what slices 3 and 5 were told to reuse. `ArrowStruct` already wraps a row;
  slice 3 adds only the list-of-rows and the transport.
- §3's bind-time hazard is UNCHANGED and still unmeasured — nothing in slice 1 executes SQL, it only
  generates text. Slice 2 is still the next thing.
- ⚠ `fluid_query` gives §3 a sharper edge than the plan anticipated: a Fluid `query` inside `fluid_query`
  would execute SQL inside `bind_replace`, i.e. during the binder's own walk, not merely "during bind".

## 8. Slice 2 — the bind-time `host_query` PROBE: MEASURED SAFE (2026-09-01)

**§3's hazard is CLOSED, and the answer is the permissive one.** Bind-time `host_query` neither deadlocks nor
crashes, and — the part §3 did not anticipate — its transaction semantics are the SAME at bind and at
execute, so there is no asymmetry for the surface to state. `query` can exist in BOTH `fabricator_render`
(execute time) and `fluid_query` (bind time). **Slice 3 is unblocked in its simple form.**

Method: a THROWAWAY `ISqlTableFunction` (`fabricator_bind_probe(sql)`) in `Fabricator.SqlServer` whose
`GenerateSql` calls `Host.Query` and splices the scalar result into the generated SQL — i.e. a real
host query executed while DuckDB is binding a statement. It lived only for the measurement and was removed
(`CustomFunctions.cs` is byte-identical to its pre-probe state; `duckdb_functions()` reports 0). It had to go
in a first-party assembly rather than the plugin, because `Host.Query` is Bridge-only — §2's whole point.

### 8.1 Safety: fourteen shapes, none of them failed

Each run under a 30–60 s timeout, so a deadlock would surface as a timeout rather than a hang:

| shape | result |
|---|---|
| constant query at bind | ok |
| reads a DuckDB table at bind | ok |
| inside a `CREATE VIEW`, then using the view (bind REPEATS) | ok |
| inside an explicit transaction | ok |
| `EXPLAIN` / `DESCRIBE` (bind WITHOUT execute) | ok |
| **NESTED** — a bind-time host query whose SQL calls the probe again | ok |
| prepared statement, two `EXECUTE`s (re-bind each time) | ok |
| reads OUR OWN attached fabricator catalog at bind | ok, correct value |
| bind-read AND outer scan of the SAME table in one statement | ok, both 10 |
| `CTAS` into the same catalog it read at bind | ok |

⚠ The catalog cases are three levels deep by construction, not two: `PROVIDER 'delta'` defaults
`native_read` on, so a Delta scan issues its own `Host.Query` per file — bind → host query → Delta scan →
host query. That is the re-entrancy §3 was worried about, and it holds.

⚠ **This does not retroactively make the §3 worry unfounded.** The two incidents it cited are real
(the ABI v80 scalar-bind ambient SIGSEGV under OPTIMIZE; the `entry_lock_` rule) and neither is contradicted:
both are about holding an AMBIENT or a LOCK across a re-entry, and `Host.Query` opens its own connection and
establishes its own scope. The probe measured the specific question and answered it; it did not repeal the
class.

### 8.2 ⚠ TRANSACTION VISIBILITY — a real limitation, and NOT bind-specific

MEASURED with a control, inside `BEGIN; INSERT …;`:

| reader | sees the uncommitted row? |
|---|---|
| the statement's own scan | **yes** — 109 |
| `fabricator_host_query` at EXECUTE time | no — 10 |
| the bind-time probe | no — 10 |
| after `COMMIT` | 109 |

**Identical at bind and at execute, and identical on a plain DuckDB table** — so this is a property of
`host_query` opening its own connection, not something bind introduces. That is what makes the surface
symmetric: a Fluid `query` reads COMMITTED state wherever it is called, which is one rule to document rather
than two. ⚠ It is still a limitation a template author must know: a template cannot observe the writes of the
transaction that is running it.

### 8.3 ⚠⚠ THE HAZARD THE PROBE FOUND, AND IT CONSTRAINS SLICE 3's SURFACE: A BIND-TIME WRITE FIRES ON `EXPLAIN`

A bind-time host query can perform DML, and because binding is neither once nor tied to execution, it fires
where nobody asked for it. MEASURED, counting rows in an audit table:

| statement | rows after |
|---|---|
| `EXPLAIN SELECT … probe('INSERT INTO audit …')` — **never executed** | **1** |
| `CREATE VIEW v AS SELECT … probe('INSERT …')` — merely DEFINING the view | **2** |
| `SELECT count(*) FROM v` — every use re-binds | **3** |

⇒ **Slice 3's Fluid `query` must REFUSE anything that is not a SELECT.** That is not a new rule invented
here: `ISqlTableFunction`'s own authoring contract already requires `GenerateSql` to be deterministic and
side-effect-free *because* binds repeat and happen without execution. A `query` that writes would violate the
contract its host runs under, and the failure is invisible — an `EXPLAIN` that mutates.

⚠ The refusal has to be on the STATEMENT KIND, decided before execution — not a catch afterwards, because by
then the write has happened.

### 8.4 What this settles for the remaining slices

- **Slice 3** (`HostQueryTransport` + a Fluid `query`) is unblocked, with §8.3's SELECT-only refusal added to
  its scope and §8.2 as a documented semantic. No bind/execute asymmetry to build around.
- **Slice 5** (dynamic DuckDB function resolution) inherits the same verdict — it is the same re-entry.
- ⚠ Slice 3 still needs the `HostQueryTransport` seam of §2 regardless: this probe reached `Host.Query`
  because it lived in a first-party assembly. The plugin still cannot.
