# Fluid templating — shipped state and the follow-on plan

> **Status: slice 0 SHIPPED (`7f1940c`), slice 1 SHIPPED (`58aa6b4`, §7), slice 2 MEASURED (§8 — the answer is
> PERMISSIVE), slice 3 SHIPPED (§9), slice 4 SHIPPED (§10); slice 5 PLANNED, and §10.8 says re-derive it.**
> The shipped part is `fluid_render` as a bundled plugin; its record lives in
> [plugin-system.md](plugin-system.md) §The FLUID plugin. This document is the FOLLOW-ON plan, written
> down because the architectural finding in §2 is what makes slices 2–5 possible without undoing the move.

## 0. What is already there (do not re-derive it)

- **`Fabricator.FluidPlugin`** — an `IBackend` named `fluid`, contributing ONE global scalar,
  `fluid_render(template, params)`. Params is a DuckDB `STRUCT` **or** a JSON string.
- **Pinned at `Fluid.Core 3.0.0-beta.7`** — a PRERELEASE. A bump there is a code-compatibility question,
  not a routine one, and `verify_plugin_fluid.test` is what answers it.
- **It ships**: `pack-distribution.ps1` step 2b stages it under `<managed>/plugins/`, which
  `PluginPaths.BundledRelativeRoot` makes a default search root (user root first, bundled second —
  first-root-wins).
- **Gate**: `test/verify_plugin_fluid.test` (23, service tier, its own plugin root).
- It references **`Fabricator.Abstractions` + `Fabricator.Common`, and never `Fabricator.Bridge`** —
  which is the property §2 is about. (Common since 2026-09-02; docs/plugin-services.md §9.)

## 1. The four follow-ons, as the user specified them

Preserved close to verbatim, because the sketches are design input and the reasoning behind them is not
recoverable from the code.

### 1.1 `fluid_query` — a global **sqlgen** table function

Accepts a template and a JSON argument; the rendered text IS the SQL. Fluid can reach a
`System.Text.Json` `JsonNode` directly, so `JsonToClr` (the hand-rolled mapper `fluid_render` uses)
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

- If bind-time `host_query` is safe ⇒ `query` works in both `fluid_render` (execute time) and
  `fluid_query` (bind time).
- If it is not ⇒ **`query` is available at execute time and REFUSED at bind time**, which is a real
  asymmetry the surface has to state rather than hide.

## 4. The slice order (agreed 2026-09-01)

| slice | contents | why here |
|---|---|---|
| **1** ✅ | `fluid_query` + **the value model**: the `ValueConverters` mapping, and the Arrow→`FluidValue` wrapping (`ArrowStruct : IFluidIndexable`, name AND index access, nested list → enumerable). BUILT — see §7 | Slices 3 and 5 both reuse the value model, so it was built ONCE here. Needed no new ABI and no new seam, exactly as §2 predicted |
| **2** ✅ | **Probe** bind-time `host_query` (§3) — DONE, see §8 | One measurement; it came back PERMISSIVE, and added a SELECT-only refusal to slice 3's scope |
| **3** ✅ | `HostQueryTransport` seam in Abstractions + the Fluid `query` function. BUILT — see §9 | Needs 1's row wrapping and 2's verdict |
| **4** ✅ | `ITemplateFileProvider` — BUILT, see §10. ⚠ **NOT over a host-FS seam**: one was built and MEASURED unusable (a global function has no ambient opener), so it reads `read_blob` over slice 3's `HostQueryTransport` | Predicted independent of 3; it turned out to DEPEND on 3 |
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
same bag `fluid_render` does — a STRUCT, a MAP, or a JSON string — because a params bag has to mean the
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

The user-visible consequence: `fluid_render('{{ n }}', '{"n":9007199254740993}')` returned
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
its type), but it IS a change to `fluid_render`'s output.

⚠ **STILL TRUE AND NOT FIXED:** a genuine CLR `double` (a DuckDB `DOUBLE` column) has ~17 significant digits
and Fluid keeps 15. That is Fluid's model, not something the ladder can route around; a `DOUBLE` params value
with more than 15 significant digits renders rounded.

### 7.4 The Arrow half — one unboxing, shared with slice 3

`FluidValueModel.ReadCell` is a deliberate SUPERSET of `ArrowValueReader.ReadScalar`, extended with the
nested cases the bridge's reader has no counterpart for, since that one exists for FILTER values and those
are scalars by construction:

⚠ **`ArrowValueReader` IS REACHABLE FROM A PLUGIN AS OF 2026-09-02** — it moved to `Fabricator.Common`, which
this plugin now references (docs/plugin-services.md §9). That does NOT make `ReadCell` redundant: it differs
from `ReadScalar` on floats (the decimal ladder), blobs (hex), dates (the `DateTimeKind.Utc` stamp) and every
nested type, and three of those four are gated — substituting it would revert §7.4a's date fix. What the
reference DID remove is the `ReadTimestamp` copy, which was character-for-character identical including the
load-bearing `(object)` casts (§9.4).


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

**A DATE RENDERED THE PREVIOUS DAY.** On a UTC+2 box, `fluid_render('{{ d }}', {'d': DATE '2026-09-01'})`
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
execute, so there is no asymmetry for the surface to state. `query` can exist in BOTH `fluid_render`
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

  **⚠ WHAT THE host_query PASS CHANGED FOR IT (2026-09-01, after slice 2 — read this before starting):**
  - **`fabricator_host_query` no longer executes its SQL twice.** It did when slice 2's probe ran, so a
    Fluid `query` built then would have run every template's SQL twice. Fixed; see
    [host-query.md](host-query.md). ⚠ The residue is multi-statement SQL, which still double-executes and
    therefore FAILS on a non-idempotent prefix — one more reason for the SELECT-only refusal below.
  - **§8.3's SELECT-only refusal now has somewhere to point.** `fabricator_host_exec(sql)` exists as the
    honest home for DDL/DML on the host, so `query`'s refusal is not "you cannot do this" but "use exec" —
    a better surface than a bare error. The refusal itself is unchanged and still mandatory: it must key on
    the STATEMENT KIND before execution, because a bind-time write fires on `EXPLAIN`.
  - **⚠ The seam should carry the SCHEMA question too.** `Host.RegisterSource` grew a declared-schema
    overload the same day for exactly the reason slice 3 will meet: a bind must learn columns, and the only
    alternative to declaring them is producing data. Whatever `HostQueryTransport` ends up looking like,
    decide up front how a caller says "these are my columns" without running anything.
- **Slice 5** (dynamic DuckDB function resolution) inherits the same verdict — it is the same re-entry.
- ⚠ Slice 3 still needs the `HostQueryTransport` seam of §2 regardless: this probe reached `Host.Query`
  because it lived in a first-party assembly. The plugin still cannot.

## 9. Slice 3 AS BUILT (2026-09-01) — `HostQueryTransport` + the Fluid `query` function

C#-only. **NO ABI change, NO C++ change** — because the seam is a delegate in the contract assembly that
the bridge fills in at boot, which is §2's prediction paying out a second time. Gate
`verify_plugin_fluid` **93 → 131**, four mutants each killed at its own assertion.

```liquid
{% assign rs = query("SELECT id, nm FROM people ORDER BY id") %}
{% for r in rs %}{{ r.nm }} is {{ r.id }} / {{ r[0] }}{% endfor %} ({{ rs.size }} rows)
```

### 9.1 The seam — `Fabricator.Abstractions/HostQueryTransport.cs`

`HostHttpTransport`'s shape, copied deliberately including its rules: one static delegate
`Func<string, RecordBatch?, IArrowArrayStream>? Query`, declared in Abstractions and assigned by
`Bootstrap` beside the HTTP one. A plugin reaches `host_query` with the reference it already has.

- **⚠ It carries no opener**, for the reason that seam records: `Host.Query` reads the AMBIENT
  `ClientContext` per call, and a captured one dangles the day its connection closes.
- **It carries the PARAMETERISED overload, and that is not a convenience.** The refusal below classifies
  arbitrary user SQL; passing it as a bound VALUE rather than concatenating it into SQL text is the whole
  defence. A seam offering only `Query(sql)` would have forced the security-critical path to hand-escape.
- **⚠ THAT OVERLOAD HAD NO IN-TREE CALLER UNTIL NOW** (user-raised, 2026-09-01) — every existing caller uses
  the bare form or the named-Arrow-`inputs` form — so slice 3's classifier is its first consumer and the
  first thing to gate it. What it does, read from `fabricator_host_query.cpp`: with `params` the host runs
  `conn->Prepare(sql)` then `prepared->Execute(values)`, i.e. a REAL prepared statement, binding **one Arrow
  COLUMN per parameter, positionally**, against `?` / `$1`. Four edges worth knowing:
  - only **row 0** is read (`GetValue(c, 0)`); a batch with more rows is silently ignored, not refused;
  - an **empty batch binds all-NULL** rather than erroring (`chunk.size() > 0 ? … : Value()`);
  - ⚠ **passing parameters restricts you to ONE statement**, because the no-params branch is `SendQuery`
    (which accepts several) and this one is `Prepare` (which does not) — the same asymmetry that forced
    `fabricator_host_query`'s documented fallback and motivated `fabricator_host_exec`;
  - ⚠ an **untyped `?` may fail overload resolution** — see the cast note in §9.2.

### 9.2 ⚠⚠ THE SELECT-ONLY REFUSAL — the classifier is DuckDB's OWN PARSER, and the two obvious mechanisms are BOTH BROKEN

§8.3 required the refusal and said it must key on the STATEMENT KIND before execution. What it did not say
is how, and the two mechanisms anyone would reach for first were MEASURED and both fail:

| candidate | verdict |
|---|---|
| prefix check (`starts with SELECT`/`WITH`) | **admits writes** — `WITH x AS (SELECT 1) INSERT INTO t SELECT * FROM x` begins with `WITH` |
| wrap as `SELECT * FROM (<sql>)` | **injectable** — it is string concatenation, so it has an escape by construction |

**⚠⚠ The wrap deserves its own line because it LOOKS airtight and its failure is silent.** Measured, it
refuses every honest non-SELECT (INSERT/DELETE/UPDATE/CREATE/DROP/ATTACH/COPY/PRAGMA/SET all raise a
Parser Error with the target table's row count unchanged) — and then:

- `SELECT 1) ; INSERT INTO aud VALUES (99); SELECT * FROM (SELECT 2` — **not refused, and the row landed.**
- `SELECT 1) ; DROP TABLE aud; --` — **the table was dropped.**

A mechanism that refuses the accident and admits the attack is worse than none, because it reads as a
defence.

**What ships is `json_serialize_sql`, with the SQL as a BOUND PARAMETER.** DuckDB's own parser decides, and
it announces the rule itself: *"Only SELECT statements can be serialized to json!"*. Measured, it refuses
both escapes above (⚠ but NOT a write performed by a FUNCTION inside a SELECT — measured in §11.1a; the rule is about the statement KIND), refuses any multi-statement input CONTAINING A WRITE (⚠ NOT every multi-statement
input — an all-`SELECT` sequence is accepted; corrected and measured in §11.5), refuses the
`WITH …INSERT` shape — and **parses only**:
a target table's row count is unchanged by classifying a `DELETE` against it.

- **⚠ The cast is REQUIRED**: `json_serialize_sql(?)` cannot resolve its overload from an untyped
  parameter, and answers *"first argument must be a VARCHAR"*. It is `?::VARCHAR`. Found by running it —
  and it arrived as a REFUSAL rather than as a crash, which is the fail-closed rule working before it was
  ever deliberately tested.
- **⚠ An EMPTY string classifies as NO ERROR**, because it parses to zero statements — so the classifier
  ALONE would wave it through. Guarded separately, before the classifier, where the cause can be named.
- **⚠ The engine's own message is surfaced verbatim**, because the check conflates two causes otherwise: a
  non-SELECT and a real syntax error (`syntax error at or near "SELEC"`). Reporting "not a SELECT" for a
  typo sends the author looking in the wrong place.
- **⚠ It FAILS CLOSED.** If the classification itself cannot run — `json` unavailable, host unreachable —
  the statement is REFUSED, not run. An unenforceable check must fail closed.

**⚠ THE COST, stated rather than hidden: some READ-ONLY statements are refused too.** `PIVOT` and `EXPLAIN`
are not serializable — and for `PIVOT` that holds even wrapped in a subquery, where it would otherwise
execute, so it is unreachable through `query()` in any spelling. `DESCRIBE`, `SUMMARIZE`, `VALUES`,
`TABLE t`, `FROM t`, CTEs and set operations all pass. Being conservative in this direction is the correct
trade: the alternative admits writes. The workaround is a view defined outside the template.

### 9.3 The payoff, and it is the thing slice 2 was run to permit

A template can ask the database what SQL to generate, **at bind time** — MEASURED, the output SCHEMA of a
statement decided by rows read during `bind_replace`:

```sql
SELECT * FROM fluid_query('SELECT {% assign rs = query("SELECT nm FROM cols ORDER BY ord") %}
  {% for r in rs %}{{ r.nm | sql }} AS {{ r.nm | sql_ident }}{% unless forloop.last %}, {% endunless %}{% endfor %}');
-- columns: alpha, beta  — names that exist only in `cols`
```

### 9.4 ⚠ THE GATE IS A PAIR, AND NEITHER HALF ALONE SAYS ANYTHING

The refusal is asserted at the sharpest point §8.3 identified — `EXPLAIN` of a statement that never
executes — and "the write did not happen" is **equally true of a build where bind-time `query()` had
stopped working altogether**. So a POSITIVE CONTROL sits immediately above it: an `EXPLAIN` whose plan
carries column names that could only have come from a bind-time read. Measured, both fire.

⚠ It uses the `<REGEX>:` form on the `physical_plan` row, because **`EXPLAIN` cannot be a subquery source**
— the convention this suite already used a few sections up, and which I re-derived the hard way.

### 9.5 Rows reuse slice 1's value model, as §6 required — one type, one lookup rule

`ArrowStruct` gained a `RecordBatch` constructor rather than growing a sibling, so a result row and a
STRUCT cell resolve members through the same rule: name first, then an int-parse ORDINAL (Fluid asks for
the key `"0"` when it sees `r[0]`), and FALSE for an unknown member so Fluid can still answer `.size`.
Arithmetic and comparison work on query results because it is the same model §7.1 was built around.

- **⚠ Cells are read EAGERLY into a row, not lazily from the batch.** The batches are disposed as the
  result is consumed while the rows live for the whole render. ⚠ MEASURED, and it is **NOT** the silent
  native use-after-free class this project usually warns about: Apache.Arrow nulls a disposed
  `RecordBatch`'s arrays, so holding one fails LOUDLY with a `NullReferenceException` on the first cell
  read, deterministically. My first code comment claimed the silent class and was wrong — the mutant is
  what corrected it.
- **⚠ A row cap that ERRORS, at 1,000,000.** Rows are fully materialised because a template may iterate
  them any number of times, so there is no streaming form to fall back to. It errors rather than
  truncating: a silent truncation is a wrong ANSWER, where the cap only turns an out-of-memory into a
  sentence. Not knob-controlled yet; make it one if anyone hits it.

### 9.6 ⚠ `AllowFunctions` is OFF in Fluid by default, and it is a PARSER-level gate

Without `new FluidParser(new FluidParserOptions { AllowFunctions = true })`, `query('…')` is a PARSE error
(*"Functions are not allowed"*) rather than a missing function at render — so the failure appears one layer
away from its cause. Enabled for the whole plugin, since `query` is the only function it ships.

### 9.7 ⚠ `require json` is LOAD-BEARING in the suite, not hygiene

The classifier is `json_serialize_sql`, and `unittest` does NOT auto-load extensions the way the shell
does. Without the directive every `query()` call is refused — the check fails closed — and the section
would read as a broken feature rather than a missing REQUIRE. That is this file's own recorded trap in
both directions, and it is why the directive carries a comment saying so.

### 9.8 Transaction visibility, gated with its control

§8.2's measurement is now pinned: inside `BEGIN; INSERT …;` the statement's own scan sees the uncommitted
row and `query()` does not; after `COMMIT` it does. The post-commit assertion is what makes the middle one
a visibility result rather than a broken read.

### 9.10 NAMED PARAMETER BINDING — `sql | query: a: 1` (2026-09-01, user-raised)

C# **and C++**, still **NO ABI change**. Gate `verify_plugin_fluid` **131 → 147**, three further mutants.

```liquid
{% assign rs = "SELECT id FROM orders WHERE region = $region AND amt > $min"
   | query: region: "eu", min: 6 %}
```

**⚠⚠ IT IS A FILTER BECAUSE FLUID'S GRAMMAR PUTS NAMED ARGUMENTS THERE AND NOWHERE ELSE — measured, and it
is the finding that decided the surface.** The user asked how a template creates a params object, and
whether dictionary support had to be added to Fluid. Neither: Fluid 3.0.0-beta.7 already has named
arguments, on filters.

| construct | |
|---|---|
| `query('s', a: 5)` — function, named | ❌ parse error, *"End of tag was expected"* |
| `{'a': 1}` / `{a: 1}` — hash literal | ❌ *"A value was expected"* |
| `{% assign h.a = 1 %}` — subscript assign | ❌ |
| `(1..3)` — range literal | ✅ |
| **`'s' \| q: a: 5`** — filter, named | ✅ |
| `'s' \| q: 5, b: 'x'` — filter, mixed | ✅ (both arrive) |

⇒ `FunctionArguments.Names`/`HasNamed` — the members that make named function arguments *look* supported —
are populated by the FILTER grammar only. **Nothing was added to Fluid**, and the reading is natural: the
statement is the filter's input, the parameters modify it.

#### The host half: the names were already crossing

Both pieces existed. DuckDB has `$name` parameters and
`PreparedStatement::Execute(case_insensitive_map_t<BoundParameterData>&, bool)`; and the params
`RecordBatch` carries its column names in the Arrow schema, which `ArrowStreamReader` walked for types
while never capturing `children[i]->name`. So `MakeHostQueryStream` bound positionally for the whole life
of that overload because **nothing had read the names**, not because they were absent — the same shape as
the `fabricator_functions()` finding, where the data crossed and only the consumer was missing.

- **⚠⚠ THE STATEMENT SELECTS THE BINDING, NOT THE BATCH — and the first rule tried here was WRONG in a way
  no local run showed.** It read *"every column has a non-empty name ⇒ bind by name"*. An Arrow field
  practically always HAS a name, so that test is nearly always true, and it silently switched **every
  existing caller** to name-binding: `cf_host_param` sends columns `p0`/`p1` against positional `?`
  placeholders and broke with *"Values were not provided for the following prepared statement parameters:
  1, 2"*. **The SERVICE TIER caught it, review did not** — and the claim it falsified was my own
  "keeps the original positional behaviour byte-for-byte".
  The rule now matches the batch's column names against the statement's OWN `named_param_map`: a `?`
  statement names its parameters `"1"`, `"2"`, … so `p0`/`p1` cannot match and it stays positional.
  ⚠ A SUBSET is enough, deliberately — requiring equal sizes sends a call supplying only *some* parameters
  down the positional path, where DuckDB then reports every parameter as missing including the one that
  WAS supplied.
- **⚠ Duplicate names are REFUSED, not collapsed** — `case_insensitive_map_t` would keep the LAST value and
  bind a parameter the caller never intended.
- **⚠ THE POSITIONAL BRANCH IS NOW UNREACHABLE FROM ANY IN-TREE CALLER and is therefore UNGATED.** Every
  in-tree producer of a params batch names its columns. It is kept for an out-of-tree plugin using `?`, and
  the suite does not pretend to cover it.

#### ⚠⚠ THE CHANGE BROKE THE CLASSIFIER, AND THAT IS THE MECHANISM ANNOUNCING ITSELF

§9.2's classifier read `json_serialize_sql(?::VARCHAR)` and sent a batch whose single column is named
`sql`. The instant named binding landed, that column bound to a name while the statement wanted parameter
**1** — *"Values were not provided for the following prepared statement parameters: 1"*. Fixed by naming the
placeholder `$sql`. **The field name in a params batch is now load-bearing**, which it never was before.

#### Values, and why they are an allow-list

Number → `BIGINT` when integral and in range, else `DECIMAL(38, scale)`; String → VARCHAR; Boolean → BOOLEAN;
DateTime → `TIMESTAMP` stamped UTC (§7.4a's rule on the way in applies on the way out); Nil → a NULL VARCHAR.
**Lists, structs and any nesting of them bind recursively** (see below).

##### ⚠⚠ A LIST parameter — added 2026-09-04, and it REPLACES a refusal this document used to state

The line above read *"A LIST/STRUCT/MAP is refused by name — DuckDB has no parameter form for them here"*.
The second half was **false, and it was ours**: MEASURED, `PREPARE p AS SELECT a: unnest($1); EXECUTE
p([1,2,3,4,5])` yields five rows, and DuckDB does not even need the parameter typed. So

```liquid
{% query r xs: v %}SELECT count(*) FROM t WHERE region IN (SELECT unnest($xs)){% endquery %}
```

crosses the list as VALUES instead of forcing the `{{ v | json }}::json[]` splice it used to.

- **⚠⚠ ONE element kind per list, because an Arrow list is TYPED.** A mixed list is REFUSED by name rather
  than coerced: the only common representation is text, and turning `5` into `'5'` silently changes what the
  statement compares. NULL elements carry no kind, so they mix with anything. ⚠ The mixed case is reachable
  ONLY through the JSON parameter form — a DuckDB LIST is homogeneous, so `{v: [1,'a']}` fails in DuckDB's
  own struct construction first.
- The element type uses the SAME int64 → decimal ladder as the scalar path, decided over the WHOLE list: one
  non-integral element makes every element a decimal, and the widest scale wins so `19.99` stays `19.99`.
- **⚠ An EMPTY or all-NULL list has no element kind to read off** ⇒ VARCHAR, which is the choice the scalar
  NULL case already makes and for the same reason (it is what DuckDB casts most freely FROM, so
  `$xs::BIGINT[]` still works, and `unnest` of it yields no rows either way).
- **NESTED arrays and structs bind too** — see §Nesting below. (This line read *"nested arrays and structs
  are refused — the same one-level rule the scalar ladder has"* for a few hours on 2026-09-04, and the gate
  is what announced that it had stopped being true.)

##### Nesting — lists of structs, structs of lists, any depth

A parameter is converted RECURSIVELY, in two passes: infer the Arrow type over the whole value, then build
it. Two passes because Arrow (and DuckDB) need a concrete type at every level where Fluid has none.

```liquid
{% query rows xs: people %}SELECT string_agg(u.label, ',') AS s FROM (SELECT unnest($xs) AS u){% endquery %}
```

⚠ **Nesting was measured to be possible before any of it was built** — DuckDB binds a STRUCT
(`SELECT ($1).a` ⇒ 42), a nested LIST (`len(unnest($1))` ⇒ 2, 1), a LIST of STRUCT (`unnest($1).label` ⇒
a, b) and a MAP (`($1)['k']` ⇒ 7).

- **The shape that earns it is LIST-of-STRUCT** — a small table as one parameter, expanded with
  `unnest($xs).*`. A bare struct is marginal (pass the fields separately) and nested lists are rare.
- **⚠⚠ STRUCTS IN ONE LIST MUST SHARE A SHAPE** — same field names, same order. Unioning the field sets
  with NULLs would invent members the author never wrote, so it is refused by name. ⚠ Reachable only
  through the JSON form: a DuckDB LIST of structs is already homogeneous.
- **⚠⚠ A DuckDB MAP ROUND-TRIPS AS A STRUCT, and that asymmetry is deliberate.** Read in, a MAP becomes an
  `IFluidIndexable` exactly like a struct, so nothing at the Fluid level distinguishes them and a struct is
  the only honest thing to write back. Refusing would block a shape that otherwise works end to end.
- Members are read through `EnumerateAsync` rather than `IFluidIndexable.Keys`, because that is the ONE
  spelling that works for both sources — a DuckDB struct arrives as a `DictionaryValue` over our
  `ArrowStruct`, a JSON object through Fluid's own JsonNode support, and **both yield `[key, value]` pairs**
  (measured; it is what `{% for kv in v %}` uses).
- A depth guard at 16 levels turns a cyclic value into a sentence rather than a stack overflow.
- ⚠ Refusals speak the AUTHOR's vocabulary, not Arrow's: mixing `1` and `"a"` reports *"its elements mix
  Number and String"*, never *"int64 and utf8"* — they wrote the former and never see the latter.

##### ⚠⚠ A SHIPPED BUG THE NESTING WORK EXPOSED: a nested value out of a RESULT was LAZY

User-found 2026-09-04, by passing one query's rows into the next as a parameter and reading the struct back.
It needed **no parameters at all** to reproduce:

```sql
SELECT fluid_render('{% query r %}SELECT {''a'':1} AS s{% endquery %}{{ r[0].s.a }}', NULL);
-- Object reference not set to an instance of an object.
```

`FluidHostQuery.Run` has always materialised a row's cells EAGERLY, *because* the batches are disposed as
the result is consumed — the code says exactly that. But `ReadCell` returned a **lazy `ArrowStruct`** for a
STRUCT cell, which reads its members when the template asks. By then the `RecordBatch` is gone, and
Apache.Arrow nulls a disposed batch's buffers, so it died in `SharedMemoryHandle.get_Memory()`.
**The eagerness stopped ONE LEVEL DOWN.**

- Fixed by making that case eager too, through the type that already means "cells read eagerly" —
  `EagerRow` became **`EagerStruct`**, since it now serves both a query-result ROW and a nested STRUCT cell.
  `ArrowStruct` remains as the lazy SOURCE it materialises from, so the two cannot drift on the lookup rule.
- **⚠ WHY NOTHING CAUGHT IT: every earlier nested read came from the PARAMS bag**, whose batch lives for the
  whole call, so laziness was harmless there. Only a nested value from a RESULT dies — and until a template
  passed one query's rows into another, nothing produced one.
- ⚠ `ArrowMap` was already eager and `ReadList` materialises its items, so MAP and LIST cells were never
  affected; the gate pins them anyway, because that is now a property to keep rather than an accident.
- **⚠ CHARACTERIZATION, not ours:** `SELECT unnest($arg)` yields ONE struct-typed column *named*
  `unnest($arg)`, so `{{ r.a }}` finds nothing and renders empty — which looks like a bug and is DuckDB's
  naming. `AS s` or `unnest($arg, recursive := true)` are what flatten it. Both are pinned.

**⚠ The READ direction was already recursive and is untouched** — `{{ v.kids[1].scores[2] }}` resolves
through struct → list → struct → list, and has for as long as `ReadCell` has existed. The gap this closes
was one-directional.

**⚠⚠ THE BUILD HAD THE JANUARY-1970 BUG, IN A NEW PLACE, AND ONLY A DATE ELEMENT SHOWED IT.** The first
version appended through `ListArray.Builder.ValueBuilder`, which does **not** carry a `TimestampType`'s UNIT
into the builder it creates: values were stored as MILLISECONDS under a field declaring MICROSECONDS, so
`DATE '2023-01-02'` read back as `1970-01-20 09:36:57.6`. That is the exact signature this repo already
records for hand-rolled Arrow timestamp sites — and numbers, strings and booleans have nothing to get wrong,
so a battery without a date would have shipped it. The values array is now built with an explicitly typed
builder and the list assembled by hand.

- **⚠ Fluid's number model IS decimal (§7.3), so even `10` arrives as one.** The integral case is narrowed
  back to BIGINT so the ordinary spelling behaves like the literal it replaced; a fractional value keeps its
  own scale, so `19.99` stays `19.99`.
- **⚠ POSITIONAL filter arguments are REFUSED rather than ignored.** Fluid permits `sql | query: 5, b: 'x'`,
  and a positional value could only mean `?` / `$1`, which cannot coexist with the by-name binding a named
  batch selects. Dropping it silently would run the statement with a parameter the author believed they had
  supplied. Mutation-tested.
- **⚠ A MISSING parameter is DuckDB's own error and it NAMES the parameter** (*"Values were not provided …:
  b"*), which is better than anything we could invent — and the reason nothing here pre-validates the
  placeholder set against the statement.

#### The gate

- **The load-bearing assertion is the INJECTION one, with its control.** `region: "eu' OR 1=1 --"` answers
  **0**; spliced it would have answered 3. Beside it the same statement with `"eu"` answers 2 — without
  which "0" would be equally true of a build where the parameter never reached the statement.
- A DISCRIMINATING pair on `min` (6 ⇒ one row, 1 ⇒ two), so a build ignoring the parameters cannot pass by
  luck.
- The SELECT-only refusal is re-asserted on the filter form: **parameters buy no exemption.** A
  parameterised write is still a write and still fires at bind.
- ⚠ MUTANT A (never bind by name) dies at the FIRST `query()` rather than at a parameter assertion, because
  the classifier itself now binds by name — so every call exercises the path. That is strong coverage but a
  broad kill, which is why MUTANT B exists: names in order, VALUES reversed, so a one-parameter call is
  unaffected and a two-parameter one binds wrongly. It dies at the first named-parameter row with
  *"Could not convert string 'eu' to INT32"*.
- ⚠ My first attempt at MUTANT B was NOT the mutation I intended: I reversed the name at the INSERT but not
  at the duplicate CHECK, so it tripped my own duplicate guard instead of demonstrating a wrong binding. It
  killed at the right line for the wrong reason — the same trap as a mutant that dies in the right place by
  accident.

### 9.11 ✅ `query()` INHERITS THE CALLER'S SESSION — ABI v83 (2026-09-02)

A template's `query()` ran on a connection with DuckDB's DEFAULTS: a statement could `SET TimeZone` and the
template it rendered still read the machine's zone, and an unqualified name did not resolve at all. It now
inherits the caller's **TimeZone** and **catalog search path** — `HostQueryTransport` passes the ambient
`ClientContext` as the caller's session, so a plugin gets it without asking. Full record:
[abi-history.md](abi-history.md) §v83; the gap's own record is [host-query.md](host-query.md), now closed.

⚠ It does NOT inherit the TRANSACTION: §8.2's rule is unchanged, `query()` still reads COMMITTED state.
Name and time RESOLUTION are what cross.

⚠ **This supersedes the `SET GLOBAL TimeZone` advice** measured the same day. That worked — a fresh
connection inherits the GLOBAL layer while it cannot see another connection's SESSION layer — and a plain
`SET` now works, so the README's own `SET TimeZone = 'UTC'` convention reaches template queries too. ⚠ §7.4a's
note that a DATE renders through the .NET side's `TimeZoneInfo.Local` is a DIFFERENT clock and is unaffected;
do not let this entry be cited as covering it.

### 9.9 What slice 3 leaves

- **§1.4 (DuckDB functions callable from inside Fluid) should be RE-DERIVED, not inherited.** §6 already
  flagged this, and slice 3 strengthens it: a template that can run SQL can already call any DuckDB
  function through `query()`. The remaining case for §1.4 is ergonomic, not capability.
- **Slice 4 (`ITemplateFileProvider`) needs the same seam shape for `HostFs`** — `HostQueryTransport` is
  now a second worked example of it, and the two should look alike.
- ⚠ The seam is deliberately NOT exposed as a SQL function; it is a plugin-facing capability.
  `fabricator_host_query` remains the SQL surface and is unchanged by this slice.

## 10. Slice 4 AS BUILT (2026-09-02) — `{% include %}` / `{% render %}` from any storage the host can reach

C#-only, in the plugin. **NO ABI change, NO C++ change, NO bridge change** — which is not what §4 predicted,
and the correction is the substance of this slice. Gate `verify_plugin_fluid` **147 → 174**, four mutants
each killed at its own assertion.

```sql
SET GLOBAL fluid_template_root = 's3://analytics/templates';
SELECT * FROM fluid_query('{% include ''dims/customer'' %}', params := {'region': 'eu'});
```

### 10.1 ⚠⚠ THE PLAN SAID "A `HostFs` SEAM". ONE WAS BUILT, AND IT CANNOT WORK FROM HERE

§2's table says `HostFs` is unreachable from a plugin *because the type lives in the bridge*, and §4 scheduled
slice 4 as "`ITemplateFileProvider` over a host-FS seam — independent of 3; same seam pattern". A
`HostFileTransport` was written to exactly the `HostHttpTransport` shape, the bridge filled it in at boot, and
the first include **killed the process**:

```
Fatal error. 0xC0000005
   at Fabricator.Bridge.HostFs.OpenRead(IntPtr, System.String)
   at Fabricator.Bridge.HostFs.ReadAllBytes(IntPtr, System.String, Int64)
   at Fabricator.FluidPlugin.HostTemplateFileProvider.GetFileInfoAsync(...)
   ...
   at Fabricator.Bridge.Bootstrap.ScalarFnExecute(...)
```

**Every `fs_*` host callback takes the calling operator's `ClientContext` as its opener and dereferences it
(`auto *ctx = reinterpret_cast<ClientContext *>(opener); FileSystem::GetFileSystem(*ctx)`), and a GLOBAL
function has no ambient opener established.** Both `fluid_render` (a global scalar) and `fluid_query`
(a global sqlgen table function) are exactly that. So the blocker was never the assembly the type lives in —
**it is that the AMBIENT the seam needs is not established for global functions**, which §2 could not see
because it was reasoning about references rather than about call context.

⚠ §2's own corollary already contained the answer and neither of us read it that way: *"such a seam is usable
only from INSIDE an ABI crossing, or where the ambient still flows from one."* A global function's crossing is
a crossing — it just does not carry that ambient.

**The seam was deleted rather than shipped unreachable.** It would have been a public contract in the contract
assembly with no in-tree caller and no way to reach it from the surface that motivated it.

### 10.2 ⚠⚠ AND THE SAME MISSING AMBIENT MAKES A PLAIN `SET` UNRELIABLE — hence `SET GLOBAL`

The root is the DuckDB setting `fluid_template_root`, the first setting any PLUGIN declares (a plugin's
`IBackend.Settings` go through the same `BackendRegistry.All()` path a backend's do — measured, it appears in
`duckdb_settings()`).

Provider settings register SESSION-scoped (ABI v69) and `ProviderSettingsStore` resolves *session ?? global*
from `ProviderSettingsStore.CurrentSession` — **an `AsyncLocal` set by `set_active_opener`, i.e. the same
ambient the opener rides on, and equally absent for a global function.** So a plain `SET` writes a layer keyed
on a session id the plugin never receives.

**⚠⚠ THIS IS NOW FIXED — ABI v82, the same day, and the requirement to write `SET GLOBAL` IS GONE.** A
plain session `SET fluid_template_root` works. What follows is the record of the gap, kept because the
reasoning about it went wrong twice and because the fix is what closed it: `scalarfn_bind`/`scalarfn_execute`
now carry the caller's context and RESTORE it (abi-history.md §v82).

**The chain, read from source:** `SET x = v` on an extension option resolves `SetScope::AUTOMATIC` against
`FABRICATOR_SETTING_DEFAULT_SCOPE`, which is `SESSION`, so the trampoline writes under
`SessionKeyFor(&context)` — the ClientContext ADDRESS. `GetString` consults that layer only when
`CurrentSession != 0` and otherwise falls through to the global bucket, which `SET GLOBAL` writes under key
0. `CurrentSession` is an `AsyncLocal<long>` assigned only by `set_active_opener` — which C++ called from
catalog and scan crossings and **not from a global scalar's execute**.

**⚠⚠ AND IT WAS NON-DETERMINISTIC, WHICH IS WHERE I WENT WRONG TWICE — WORTH MORE THAN THE FIX.**

1. **First I claimed non-determinism and invented a mechanism for it** — `set_active_opener` assigns and
   never clears, so an earlier crossing on the same thread leaves `CurrentSession` set. Written into four
   places off ONE observation, untested.
2. **Then, asked to explain it, I tested the mechanism with the wrong probe and RETRACTED a true claim.**
   The probe was `SELECT count(*) FROM fabricator_plugins()` before the `SET` — chosen by reading
   `arrow_ingest.cpp`, where a table function's bind and scan DO call `set_active_opener`. It came back
   negative, so I recorded the whole thing as an invention. ⚠ **A single negative probe of a plausible
   candidate is not a refutation of the class.**
3. **Then the right probe found it in one statement.** `fluid_query` — whose sqlgen `bind_replace` runs on
   the BINDER's thread, the same thread that later evaluates the scalar — leaks where a table function's
   scan (a worker thread) does not:

   | between `SET` and `fluid_render('{% include … %}')` | result |
   |---|---|
   | nothing | **fails** — "no root is set" |
   | `SELECT * FROM fluid_query('SELECT 1 AS x')` | **renders** |

   ⇒ the original claim was RIGHT, the retraction was WRONG, and only the third attempt had a
   DISCRIMINATOR. The lesson is not "trust the first instinct": it is that steps 1 and 2 were both
   reasoning where a one-line A/B was available.

**⚠ The leaked OPENER was the sharper half of the same defect**, and it is why the fix matters beyond
ergonomics: a leaked opener is a raw `ClientContext *` whose connection may already be gone, so a global
scalar doing host-FS IO could dereference a dangling pointer — the `table_stats` use-after-free class, which
the fs_* null guard added the same day cannot catch because the pointer is not null.

**✅ THE FIX IS ABI v82 rather than anything Fluid-shaped**: `scalarfn_bind`/`scalarfn_execute` take the
caller's `opener`/`session`/`txn_id` and the managed handlers wrap the call in `CallScope`, which puts the
previous ambients back on the way out. The restore is what v80's record demanded, and it is mutation-proven:
with `Dispose` emptied, `verify_delta_clustered_optimize` dies at `OPTIMIZE main.c1` with **exit 127 and no
output**, v80's exact signature; with it, 147 assertions on both engine legs. ⚠ The other crossings still
assign without restoring — correct for one that binds its statement's OWN source, and the scalar no longer
depends on it either way. ⚠ It does NOT make `Host.Query` inherit DuckDB's own session settings; that is a
different mechanism and still open ([host-query.md](host-query.md) §OPEN). Full record: abi-history.md §v82.

**⚠ AND A SEPARATE, PRE-EXISTING CRASH IT EXPOSED: none of the nine `fs_*` host callbacks null-check the
opener, while their sibling `HostHttpRequest` does** (*"http_request requires a client context (no ambient
opener)"*). So any managed caller reaching the filesystem without an ambient gets an access violation instead
of a message. Fixed separately; ungated, because nothing in tree can currently reach it.

### 10.3 WHAT SHIPS: `read_blob` over slice 3's `HostQueryTransport`

`HostTemplateFileProvider : ITemplateFileProvider` resolves a path and reads it with

```sql
SELECT content, size, last_modified FROM read_blob($path)
```

through `HostQueryTransport` — the seam slice 3 already built, gated and measured at bind time. It needs no
ambient because `Host.Query` opens its own connection on the captured `DatabaseInstance`.

**And it is better on its own merits, which is what settles it rather than mere availability.** Measured, all
four:

- **`read_blob` on a missing file returns ZERO ROWS rather than throwing**, so **absence is ESTABLISHED by the
  engine instead of guessed from a message.** That matters here more than anywhere: the host has no
  `fs_exists`, so a failed open is equally a missing file, a denied credential and an unreachable endpoint —
  and Fluid's normal behaviour is to probe a path that is *supposed* to be missing (see 10.4). A filesystem
  seam would have forced this repo's own "never infer absence from a failure" rule to be broken on the hot
  path.
- **It reports `size`**, so the per-template ceiling is checked against the file rather than hoped for.
- **It reports `last_modified`**, so `TemplateSourceInfo.LastModified` carries a REAL time. A filesystem seam
  has no mtime at all, and this repo has already shipped the alternative once: `DuckDbTableFileSystem`
  reported a hardcoded epoch as every file's mtime, which nothing read until a retention pass did.
- **The path crosses as a BOUND PARAMETER** — `read_blob($path)` binds, measured — so it never becomes SQL
  text. That is slice 3's named-parameter work paying out immediately: the params batch column is named
  `path`, and host_query binds by name when the batch's names are all parameters the statement declares.

**⚠ The cost, stated rather than hidden: the read inherits every limitation of `query()`** (§8.2, §9). It runs
on a connection of its own, so a template whose location is authorised by a TEMPORARY secret of the calling
session is unreadable; a persistent secret works. One rule for both surfaces, which is better than two.

### 10.4 What Fluid's file-provider contract actually is (measured on 3.0.0-beta.7)

`ITemplateFileProvider` is **Fluid's own**, not `Microsoft.Extensions.FileProviders.IFileProvider` (that is
what the DEFAULT `FileProviderTemplateFileProvider` wraps):

```csharp
ValueTask<TemplateSourceInfo> GetFileInfoAsync(string subpath, TemplateContext context, CancellationToken ct)
```

- **It receives the `TemplateContext`.** MEASURED: a value put in `ctx.AmbientValues` before `Render` is
  visible inside the provider. **⚠ That is what makes ONE instance on the shared static `TemplateOptions`
  safe, where the `query` FILTER registration needed a warning** — the root, the read cache and the tried-path
  record all travel per call. The difference is the context parameter, not care.
- **It is called at RENDER, never at PARSE** (zero calls during `TryParse`), so `FluidEngine`'s parse-once
  cache is unaffected.
- **⚠⚠ IT PROBES TWICE PER INCLUDE**: `{% include 'a' %}` asks for `a` and *then* for `a.liquid`. An author
  who omits the extension pays TWO reads where `{% include 'a.liquid' %}` pays one — on remote storage, two
  round trips. **And the bare probe WINS when both files exist**, which is the opposite of what a `.liquid`
  convention would suggest; gated as a discriminating pair.
- **Not-found is `null`** (the type is a CLASS), after which Fluid raises `FileNotFoundException` carrying
  only the include's ARGUMENT.
- Repeated includes of one file are NOT cached by Fluid: two includes made four provider calls. Caching is
  ours.
- `{% render %}` goes through the same provider, and in this beta it is **not** scope-isolated — the outer
  variables are visible inside it, unlike standard Liquid.
- A cyclic include is stopped by `TemplateOptions.MaxRecursion` (default 100), after ~200 provider calls.

### 10.5 What the provider does with that

- **A per-RENDER read cache**, keyed on the resolved path, in `ctx.AmbientValues`. Safe by construction — one
  render cannot coherently see two versions of a file — and it is what makes an include inside a `{% for %}`
  cost one read, and a cyclic include cost no reads at all. ⚠ NOT GATED: it changes how many times a file is
  read and changes no answer, and nothing in SQL can observe a read count. The suite says so.
- **Every MISSED path is recorded, keyed by the include's argument**, so the not-found message names what
  was asked for. Fluid's own exception says only `nope`, which tells an author whose ROOT is wrong nothing
  at all. ⚠ Two details, both from getting it wrong first: record on the MISS only, or a successful include
  contributes its own bare-form probe to a later failure's message and names a file that was found; and key
  it PER ARGUMENT, then look under both `a` and `a.liquid` at the point of failure, because Fluid's two
  probes arrive as two different subpaths while its exception names only the first.
  **⚠ Claiming ABSENCE in that message is legitimate here, unlike almost everywhere else in this repo**: a
  credential or transport failure never reaches it, because `read_blob` throws for those and returns zero rows
  only for a genuinely missing file.
- **A 1 MiB ceiling per template**, checked against the reported `size`, so a root pointed at a directory of
  parquet turns a typo into a message.
- **Bytes are streamed to Fluid undecoded.** ⚠ A BOM-stripping branch was written here and **a mutant proved
  it INERT**: `TemplateSourceInfo` takes a stream factory and Fluid reads it with a `StreamReader`, which
  detects and strips a UTF-8 byte-order mark itself. Deleted. ⚠ Fluid does NOT strip a BOM from a template
  passed as a STRING (measured), so `fluid_render` on a BOM-prefixed literal keeps it — that is the
  caller's own text.

### 10.6 ⚠⚠ THE ROOT IS ERGONOMICS, NOT A SANDBOX — and saying so is the point

An absolute path is simply allowed and needs no root. Confining an include would protect nothing: a template
that can `{% include %}` is being rendered by someone who can already run SQL here, and slice 3's `query()`
lets that same template read any path the host can open. Dressing a convenience as a boundary is how a
non-boundary comes to be relied on.

What IS refused is refused for PREDICTABILITY, and each has its own reason:

| refused | why |
|---|---|
| `..` in a relative path | it resolves against a root the template's author may never see; the absolute form says the same thing unambiguously |
| `*` `?` `[` `]` | **`read_blob` GLOBS.** `he*` matches `hello.liquid` today and something else the day a file is added — an include silently rendering a different partial on a directory change is very hard to see |
| a relative path with no root | fail-closed; the alternative is resolving against the process working directory, i.e. reading a file the author never named |

⚠ The multi-match refusal inside the reader is DEFENSIVE and UNGATED — a mutant survives it, because `Resolve`
refuses glob metacharacters in the subpath first. Only a ROOT containing one could reach it, and that is the
user's own string.

### 10.7 Gate and mutants

`verify_plugin_fluid` **147 → 174**. The fixtures are a small template library written by DuckDB itself.
⚠ The first `COPY` uses `PER_THREAD_OUTPUT` **because that is what CREATES the directory** — a plain COPY to a
file path does not create its parent, and the runner's scratch directory need not exist from DuckDB's point of
view. ⚠ `rtrim(x, chr(10))`, not `trim(x)`: DuckDB's one-argument `trim` removes SPACES and leaves the newline
COPY appends.

| mutant | dies at |
|---|---|
| the absolute-path branch never fires | the absolute include asserted **with no root set** |
| no glob refusal | `{% include 'he*' %}` renders instead of refusing |
| no size ceiling | the >1 MiB template renders |
| missed paths not recorded | the not-found message no longer says what it tried |

**⚠⚠ TWO MUTANTS SURVIVED FIRST AND BOTH WERE INSTRUCTIVE.** The BOM one was a REAL survivor and the code was
deleted. The absolute-path one survived TWICE for two different wrong reasons, and only the second is about
the code:

1. **The mutation never applied** — the anchor had a `\\` in it and did not match. The build succeeded and the
   suite passed *identically*, which is exactly what a no-op mutation looks like. A control mutation that
   makes `Resolve` always throw is what proved the harness sound.
2. **The condition was only half disabled.** `if (false && A || B || C)` still fires on `B` and `C`;
   precedence, not the code.
3. **And then it survived legitimately, because of a Windows path quirk**: with the absolute branch off, the
   join produces `<root>/C:/Users/.../hello.liquid` — and **that opens on Windows**. Measured. So the
   assertion had to move to where no root exists at all; on Linux the join would have failed and the mutant
   would have died in place. ⇒ **an assertion that depends on a path NOT resolving is platform-dependent;
   assert the refusal instead.**

### 10.8 What slice 4 leaves

- **Slice 5 (§1.4) should still be RE-DERIVED, not inherited** — §9.9's reasoning is unchanged.
- **The ambient gap (10.2) is the real follow-on**, and it is not Fluid's: until a global function can reach
  the host filesystem and its own session's settings, every plugin has the same two limitations.
- ⚠ A per-call `template_root` argument was considered and not built. It is clean for `fluid_query` (a named
  table-function parameter) and awkward for `fluid_render` (a scalar, so a third parameter means a second
  arity), and the global setting plus absolute paths covers the cases. Revisit if the process-wide scope
  becomes a real complaint rather than an aesthetic one.

## 11. `exec()` AS BUILT (2026-09-02) — the write-side twin of `query()`

User-asked: *"i want a exec() in fluid as well."* C#-only, in the PLUGIN. **NO ABI change, NO C++ change, NO
bridge change.** Gate `verify_plugin_fluid` **188 → 234**, three mutants each killed at its own assertion.
Service tier **54/54 — 3318** = 3302 + exactly this suite's 16, which is what shows no other suite moved.
Tiers: hermetic **74/74 — 8259** (unchanged — no hermetic suite loads this plugin) and service
**54/54 — 3302** = 3272 + exactly this suite's 30, which is what shows no other suite moved.

```sql
SELECT fluid_render('inserted={{ exec("INSERT INTO audit VALUES (1),(2),(3)") }}', NULL);  -- inserted=3
SELECT fluid_render('deleted={{ "DELETE FROM t WHERE g = $g" | exec: g: "eu" }}', NULL);   -- deleted=2
```

It also gives `IHostQuery.ExecuteNonQuery` its first caller — the member §8.2a of docs/plugin-services.md
recorded as ungated hours earlier.

### 11.1 ⚠⚠ IT IS AVAILABLE ON BOTH SURFACES (user decision) — AND IN `fluid_query` A WRITE MULTIPLIES

**User, 2026-09-02: *"no problem to have a exec() in render or query."*** The first build refused `exec()` in
`fluid_query` behind a fail-closed opt-in; that mechanism is DELETED. What replaces it is not silence — the
gate now PINS the cost as asserted behaviour, which is a stronger record than a refusal plus prose.

A `fluid_query` template renders inside `bind_replace`, and a bind REPEATS and happens WITHOUT execution.
**MEASURED, one counter through four steps that execute nothing the caller wrote:**

| step | rows written |
|---|---|
| `EXPLAIN SELECT * FROM fluid_query('… {{ exec("INSERT …") }} …')` | **1** |
| merely `CREATE VIEW v AS SELECT * FROM fluid_query(…)` | **2** |
| one `SELECT count(*) FROM v` | **3** |
| a second `SELECT count(*) FROM v` | **4** |

⇒ **the consequence to carry is the last two rows, not the first.** A writing template behind a view writes
ON EVERY USE — and it works in testing, where the statement runs once. `EXPLAIN` writing is startling;
a view that writes per use is what actually bites.

`fluid_render` behaves differently for a reason worth keeping straight: it is a **VOLATILE** scalar (the
`IScalarFunction` default, which the plugin does not override), so DuckDB never folds it into the PLAN —
`EXPLAIN` of a render containing `exec()` leaves the table unchanged (measured), and the plan shows the
un-folded call. Its multiplier is ROWS, not binds.

**⚠ WHY THE MECHANISM WAS DELETED RATHER THAN DEFAULTED ON.** With both surfaces permitting exec, an
`allowExec` parameter that every caller passes `true` is vestigial machinery that READS as a restriction
while restricting nothing — the worst of both. And the refusal never made bind-time writes impossible, only
inconvenient: see §11.1a, where a write reached bind time through `query()` before `exec()` existed.

**To restore a restriction**, the design is here rather than in git history: a per-render permission carried
as a `TemplateContext.AmbientValues` flag (it cannot be a captured variable — the FILTER form is registered
once on the shared `TemplateOptions`), **fail-closed**, set by each surface. ⚠ And do NOT derive it from the
caller's NAME: an unrecognised name reads as "not `fluid_query`" and would be ALLOWED, so a surface added
later would default to the dangerous answer.

### 11.1b ⚠⚠ A STATEMENT CANNOT SEE THE WRITE ITS OWN TEMPLATE MADE — so "prepare then select" DOES NOT WORK

**Found by asking what `exec()` in `fluid_query` is actually FOR, once it was permitted — i.e. by trying the
pattern a user would try first, rather than only testing the hazard.** MEASURED 2026-09-02:

```sql
CREATE TABLE ex_prep(c INTEGER); INSERT INTO ex_prep VALUES (1);

SELECT c FROM fluid_query('{% assign _ = exec("UPDATE ex_prep SET c = 42") %}SELECT c FROM ex_prep');
--> 1     the generated SQL reads the OLD state
SELECT c FROM ex_prep;
--> 42    the write was real
```

⇒ **the write happens and the statement that triggered it observes the state before it.** `exec()` runs on
its own connection; the outer statement's snapshot predates the commit. It is the exact mirror of §9's
documented `query()` rule — *a template cannot observe the writes of the transaction running it* — in the
other direction, and it follows from the same one-connection-per-host-query fact rather than being a second
thing to remember.

**And therefore a template cannot create a table the same statement selects from:**

```sql
SELECT * FROM fluid_query('{% assign _ = exec("CREATE OR REPLACE TABLE t AS SELECT 1 AS c") %}SELECT c FROM t');
--> Catalog Error: Table with name t does not exist!  Did you mean "memory.t"?
```

⚠ **The table DOES exist afterwards** (asserted in the gate) — so the error is a VISIBILITY result, not a
failed CREATE, and the "Did you mean" hint naming the very table it says is absent is the tell. Pinned
rather than described, precisely because the message points away from the cause.

⚠ **What works is a SEPARATE statement**: `exec()` in one, the read in the next. Gated, so the workaround is
not folklore.

⇒ **so what is `exec()` in `fluid_query` good for?** Side effects the statement does not itself read —
audit rows, logging, staging for a LATER statement — plus the ordinary case of a template that writes and
returns a count. Not for preparing data the generated SQL consumes. ⚠ That is a real narrowing of the
capability, and it is nobody's fault: it is the connection model, and it would be there whether or not
`exec()` had ever been refused at bind.

⚠ **`{{ exec(…) }}` vs `{% assign _ = exec(…) %}` matters here**: the first INTERPOLATES the count into the
generated SQL (measured: `1SELECT c FROM t` ⇒ a parser error), so a template that writes for effect must
swallow the value.

#### 11.1b-i ⚠⚠ THE RULE IS NARROWER THAN §11.1b STATES, AND THE EXCEPTION IS AN ACCIDENT OF DuckDB'S LAZY PER-CATALOG TRANSACTION START — DO NOT BUILD ON IT

MEASURED 2026-09-04, while answering whether a `fluid_table(session, name)` function could read a table a
template staged. §11.1b's conclusion is right for the DEFAULT catalog and **false for an ATTACHed catalog the
outer transaction has not yet touched**:

```sql
ATTACH ':memory:' AS scratch;
SELECT * FROM fluid_query('
{% exec %}CREATE OR REPLACE TABLE scratch.st AS SELECT 42 AS a{% endexec %}
SELECT * FROM scratch.st');
--> a = 42        the SAME statement reads what its own template just created
```

**And it is not qualification that decides it** — the obvious explanation, and it is wrong. Three legs all
FAIL, so the difference is not bare-vs-qualified and not default-vs-attached either:

| leg | result |
|---|---|
| `memory.qt` — QUALIFIED, default catalog | `Table with name qt does not exist!  Did you mean "memory.qt"?` |
| `bt` — bare, default catalog | fails, same shape |
| `ct` — bare, attached catalog made current with `USE scratch` | `Did you mean "main.ct"?` |
| `scratch.st` — attached catalog, **NOT** the current one | **works** |

**The discriminator is whether the OUTER TRANSACTION HAS ALREADY TOUCHED THAT CATALOG**, which is
`MetaTransaction`'s lazy per-`AttachedDatabase` transaction start: the default (or `USE`d) catalog is joined
to the transaction before `bind_replace` runs, so its catalog snapshot predates the `{% exec %}`; a catalog
first named in the GENERATED SQL is joined at bind time, i.e. AFTER. Proven with the pair that isolates it —
one explicit transaction, same table shape, the only difference being one preceding read:

```sql
BEGIN;
SELECT count(*) FROM scratch.seed;      -- touch the catalog FIRST
SELECT * FROM fluid_query('{% exec %}CREATE OR REPLACE TABLE scratch.st2 …{% endexec %}
                           SELECT * FROM scratch.st2');
--> Catalog Error: Table with name st2 does not exist!
-- CONTROL: the identical transaction WITHOUT that first read --> a = 8
```

⚠⚠ **So it is a TIMING artefact, not a supported route, and it must not be recommended or gated as a
feature.** It breaks on anything that touches the staging catalog earlier in the same transaction — a
preceding statement, a second `fluid_query` reading the same scratch catalog, a view whose body references
it — and it breaks by raising a catalog error that names the table it just created, i.e. §11.1b's own
misleading message. What it is good for is understanding WHY the sound route has to be a TABLE FUNCTION
(§17.12): a marshaled scan reads through its own connection and asks the caller's catalog nothing, so the
snapshot rule cannot reach it.

### 11.1a ⚠⚠ THE REFUSAL STOPS THE ACCIDENT, NOT A DETERMINED CALLER — and the hole PRE-DATES `exec()`

Found by asking whether the boundary can be nested around, rather than assuming it cannot. **MEASURED
2026-09-02, and it is a property of the SHIPPED `query()` from slice 3, independent of anything added here:**

```sql
-- the classifier is asked about a SELECT that CONTAINS a writing scalar
SELECT json_serialize_sql('SELECT fabricator_host_exec(''INSERT INTO aud VALUES (1)'')');
--> error = false, i.e. it IS a SELECT, which is CORRECT

-- so a fluid_query template reaches a write through query(), AT BIND TIME
SELECT * FROM fluid_query(
  'SELECT {{ query("SELECT fabricator_host_exec(''INSERT INTO aud VALUES (1)'') AS c")[0].c }} AS n');
--> aud goes 0 -> 1
```

⇒ **`query()`'s SELECT-only rule prevents a statement-level write; it does not prevent a write performed by
a FUNCTION inside a SELECT.** DuckDB's parser is being asked what KIND of statement this is, and it answers
correctly — there is no question one could ask it that would catch a volatile writing scalar buried in a
projection.

**What that establishes:**

- **It is the reason the `exec()` refusal was worth deleting rather than defending** (§11.1). A refusal that
  can be walked around by anyone willing to nest a scalar was never a boundary; it was a speed bump for the
  accident. With it gone, the accident is instead PINNED as asserted behaviour, which is at least honest
  about what happens.
- It is the measured form of *"exec grants no authority a caller did not already have"* — the authority was
  reachable before `exec()` existed.
- Same conclusion §10.4 reached one level down about the template ROOT — *ergonomics, not a sandbox* — for
  the same reason: **the renderer can already run SQL.** Anyone who can call `fluid_render` or
  `fluid_query` can call `fabricator_exec` directly.
- ⚠ **Do NOT "fix" it by blacklisting function names in the classified SQL.** That is the prefix-check
  anti-pattern in a new costume: an allow-list of safe functions is unmaintainable, and a deny-list is
  defeated by a macro, a view, or a name we do not ship.

⚠ Deliberately NOT gated. A test asserting "this bypass works" would pin a behaviour we would happily lose
if DuckDB ever grew a read-only execution mode, and it is the absence of a defence rather than a defence.
The measurement is recorded here instead.

### 11.2 ⚠ IT REFUSES A `SELECT`, AND THE REASON IS A WRONG NUMBER RATHER THAN A HAZARD

`query()` refuses everything that is not a SELECT; `exec()` refuses everything that is. **One mechanism, two
opposite policies** (`FluidHostQuery.Classify`), so they cannot drift on what "a SELECT" means.

The motivation is concrete: managed code cannot ask DuckDB for a statement's `StatementReturnType::
CHANGED_ROWS` (that lives C++-side), so `Host.ExecuteNonQuery` INFERS the count from the first column when it
is an `Int64`. **MEASURED with the refusal removed:**

| statement | reported "affected" |
|---|---|
| `SELECT count(*) FROM range(99)` | **99** ← a number that looks right and is not one |
| `SELECT 42::BIGINT` | **42** |
| `SELECT 42` | 0 — an INT32 literal fails the `Int64` test |
| `SELECT 'x'` | 0 |

**⚠ THE TRAP IS NARROWER THAN "ANY SELECT" AND THE NARROW VERSION IS THE LIKELY ONE, which is why the gate
asserts BOTH.** My first write-up claimed `exec('SELECT 42')` would render 42; it renders **0**. An aggregate
`count(*)` is what a template author would actually reach for, and that one does misreport — so pinning only
the `SELECT 42` case would have motivated the refusal with a harmless example.

### 11.3 ⚠⚠ A MEASURED DIVERGENCE BETWEEN THE TWO `exec` SURFACES, and a doc that was WRONG on both sides

MEASURED side by side, same statement:

| `CREATE TABLE c AS SELECT * FROM range(7)` | reports |
|---|---|
| Fluid `exec()` (managed, infers from the result shape) | **7** |
| `fabricator_host_exec` (C++, asks `CHANGED_ROWS`) | **0** |

Pure DDL (`CREATE TABLE z(a INTEGER)`) is **0** on both. The divergence cannot be closed from managed code
without the engine's classification, and it must NOT be closed by matching a leading keyword — that is the
prefix-check anti-pattern §9.2 measures as broken. It is ASSERTED in the gate as a triple rather than left
latent.

**⚠ Both `ExecuteNonQuery` docs said "DDL → 0", including the one I had written the same day, and both were
wrong for a CTAS.** Mine was copied from `fabricator_host_exec`'s recorded behaviour instead of being
measured on the path it documents — the same "described it by analogy rather than reading the second
implementation" error this repo keeps recording. Both are corrected.

### 11.4 Two paths, one count rule

Without parameters `exec()` calls `IHostQuery.ExecuteNonQuery`. With them it cannot — the host's
parameterised route is `Prepare`, which takes ONE statement, and `ExecuteNonQuery` has no parameter overload
— so the count is read locally by the SAME rule. **A rule written twice can drift, so the gate puts one
statement through both and asserts they agree (2 / 2).** Mutant C, which makes the parameterised path report
0, dies at the filter-form assertion.

⚠ The asymmetry buys something real: the no-parameter path takes **several statements in one call**
(`CREATE …; INSERT …`, count = the LAST one's), which the parameterised path cannot. Measured, and the
classifier permits it — see §11.5.

### 11.5 ⚠ MULTI-STATEMENT: A RECORDED CLAIM CORRECTED

§9.2 said the classifier "refuses multi-statement input". MEASURED 2026-09-02, that is imprecise in a way
that matters for both functions:

| input | classifier verdict |
|---|---|
| `SELECT 1; SELECT 2` | **a SELECT** (accepted) |
| `SELECT 1; INSERT INTO t VALUES (1)` | refused |
| `SELECT 1; DROP TABLE t` | refused |
| `CREATE TABLE t AS SELECT 1; INSERT INTO t VALUES (2)` | refused |

⇒ **the SAFETY property is intact in both directions and is better than the old description**: an all-SELECT
sequence is harmless to `query()`, and any sequence containing a write is refused by `query()` and therefore
reaches `exec()` — which is precisely the several-statements case exec exists for. What was wrong was the
description, not the behaviour.

### 11.6 What is pinned, and the honest gaps

Gated: the write and its count; **the four-step bind-time multiplication in `fluid_query`** (each step
against a fresh counter, so it reads as three facts rather than one total) with a `query()`-still-works
POSITIVE CONTROL beside it and an assertion that the *other* table was untouched; `EXPLAIN` not writing on
the `fluid_render` side; both SELECT refusals; the syntax-error path; the DDL/CTAS/host_exec triple;
several statements; the filter form with a named parameter; the INJECTION pair (`0` deleted, table intact —
`0` alone would also be true of a parameter that never arrived); the two-path agreement; per-row evaluation
(3 rows ⇒ 3 writes); and the empty-statement guard, which must come BEFORE the classifier because an empty
string reports no error from it.

⚠ **The multiplication block is a CHARACTERIZATION test and the suite says so**: it pins DuckDB's bind
repetition, which is not ours to implement, so no mutant of ours can kill it. Its value is that a change in
that behaviour — ours or upstream's — arrives as a failed assertion naming the step, instead of as a
surprise in someone's audit table. The two mutants that DO belong to our code (the SELECT refusal, the
parameterised count) were established on the previous commit and this change touches neither path — the
permission check it removed sat ABOVE both, and all 227 assertions still pass.

⚠ **NOT gated:** that `exec()` grants no authority a caller lacked (that is an argument about the surface,
not an observable), and the refusal's behaviour under `{% include %}` from remote storage (no hermetic
fixture has a remote root — the same gap §10 records).

## 12. ONE PINNED CONNECTION PER RENDER — `exec()` and `query()` now see each other (2026-09-03)

User-asked: *"if fluid template uses query/exec a duckdb connection should be pinned for a rendered
template … this way a exec() could create a temporary table on this connection which could be queried in
the same render session"*. Built on ABI **v84** (host-query.md §Pinned connections). Gate
`verify_plugin_fluid` **238 → 248**.

### 12.1 What it fixes

§11.1b measured that a template could not see the write its own `exec()` made, and treated that as one
fact. It is really two:

| | before | now |
|---|---|---|
| the template's own later `query()` | could not see it | **sees it** |
| the SURROUNDING DuckDB statement | cannot see it | cannot see it (unchanged) |

The first was an artefact of every call opening its own connection; the second is the snapshot the outer
statement already holds, and no connection change touches it. §11.1b's conclusion — *"exec() in
fluid_query is for side effects the statement does not itself read"* — therefore still holds for the
OUTER statement and no longer holds within the template.

MEASURED, one render:

```
{% assign _    = exec("CREATE TEMP TABLE scratch AS SELECT 7 AS v") %}
{% assign rows = query("SELECT v FROM scratch") %}v={{ rows[0].v }}      ->  v=7
```

and the same `query()` in the NEXT render: `Catalog Error: Table with name scratch does not exist!`

**⚠ A TEMP table is the discriminator, and a plain table would not be one.** A plain table is COMMITTED by
`exec()`, so a fresh-connection `query()` would see it too and the assertion would pass on the old
behaviour. Only a temporary catalog is provably the same connection — which is also why a temp table is the
right scratch space here: the outer statement cannot see it (gated: `duckdb_tables()` reports 0), and
there is nothing to clean up.

### 12.2 The scope is ONE RENDER, and three separate reasons say so

`FluidEngine.Render` creates a `FluidRenderSession`, puts it in `ctx.AmbientValues`, and disposes it in a
`using`. Both `query()` and `exec()` — and the CLASSIFIER, so one connection per render rather than one
per classification — go through it.

1. **Semantics**: "a rendered template" is what was asked for. For `fluid_render` that is per ROW;
   for `fluid_query`, one bind.
2. **Thread safety, by construction**: a DuckDB connection is single-threaded by contract and
   `fluid_render` is a VOLATILE scalar that may be evaluated on several threads at once. Each render
   builds its own `TemplateContext`, hence its own session, so nothing is shared. ⚠ Do NOT hoist it to a
   static or onto the shared `TemplateOptions` — the same trap the `query` FILTER registration documents.
3. **⚠⚠ Correctness, which is the one I had not anticipated.** `OpenConnection` applies the caller's
   TimeZone and search path ONCE, at open. So a connection outliving its render would hand every later
   render the FIRST one's session — MEASURED with a process-wide mutant: a render under `Asia/Kolkata`
   reported the zone the first render had seen, failing a PRE-EXISTING v83 assertion. A wrong VALUE, not
   stale scratch state.

**⚠ LAZY, and that is load-bearing rather than an optimisation.** `fluid_render` is evaluated per
ROW, so an eagerly-opened connection would cost one open per row for every template — including the
overwhelming majority that run no SQL at all. Nothing is opened until the first `query()` or `exec()`.

The sharpest gate assertion is the per-row one: three rows each create the SAME temp-table name
`perrow` with different values and each reads its own back (10 / 20 / 30). On one shared connection the
second row would fail with "table already exists".

### 12.3 What it does NOT widen

Every statement still goes through the same classifier — `query()` refuses anything that is not a SELECT,
`exec()` refuses SELECTs — and both refusals are re-asserted in §12 of the suite, because a mechanism that
MOVED (the classifier now runs on the pinned connection) is a mechanism that could have been dropped. The
connection still reads COMMITTED state, so §9's transaction-visibility rule is unchanged. And §11.1a's
measured hole is unchanged too: a determined caller can still reach a write through `query()` by nesting
`fabricator_host_exec` inside a SELECT — pinning neither opens nor closes that.

### 12.4 Mutation testing, including the one that SURVIVED

- **A — never pin** (fall back to a fresh connection per call): dies at the FIRST §12 assertion after 238
  pass. That is the feature's own claim, killed by its own gate.
- **C — one session for the whole process**: dies at line 1304, a **pre-existing** assertion (§12.2 reason
  3), which is stronger evidence than dying at mine — it means per-render scoping was already
  correctness-bearing. Independently MEASURED to be caught by §12's own "next render" assertion: under
  that mutant the second render read `leaked=7` where the shipped build errors.
- **B — never dispose: SURVIVED, and it was the wrong mutant.** Isolation comes from building a NEW
  session per render, not from disposing one; a `Dispose()` that does nothing still passes every
  assertion, because each render opens its own connection either way. What `Dispose()` prevents is a
  native connection LEAK — one per render for the process's life — which no SQL assertion can observe.
  Recorded in the suite rather than left to be re-derived, and it is why §12 says it pins the SCOPE and
  not the disposal.

### 12.5 ⚠⚠ Staging into a TEMP table is IDEMPOTENT under bind repetition; a real table is not

§11 measured that a writing template behind a VIEW writes on EVERY use (1 → 2 → 3 → 4), which is a footgun
for a template that writes to the catalog. For one that STAGES it is a non-issue, and that is what makes
the temp-table idiom the right one here rather than merely the tidy one: each bind gets its own connection
and therefore its own temporary catalog, so the same `CREATE TEMP TABLE` simply runs again.

MEASURED, a view over such a template used twice — and the contrast:

| staged into | first use | second use |
|---|---|---|
| `CREATE TEMP TABLE st` | `staged = 5` | **`staged = 5`** |
| `CREATE TABLE realst` | `Table with name "realst" already exists!` | — (it fails at the first SELECT, because `CREATE VIEW` already bound once) |

Both are gated. ⚠ The real-table row is a CHARACTERIZATION test of DuckDB's bind repetition, not of our
code, so no mutant of ours can kill it — it is pinned because it is the REASON to reach for a temp table,
and because a change in bind repetition should arrive as a failed assertion naming this shape.

### 12.6 ⚠⚠ It pins UNCONDITIONALLY — the fallback was removed, and why

The first build consulted `IHostQuery.CanPinConnection` and degraded to a fresh connection per call when it
was false. User-questioned (*"i actual thought fluids render would pin the connection?"*) — and the
question was right about the code reading as conditional. It is wrong twice over:

1. **Unreachable.** The Fluid provider is a BUILT-IN, published beside the bridge by
   `publish-managed.ps1`, so it cannot meet a host older than its own contract. The branch was dead, and
   its own comment said so.
2. **⚠⚠ And if it ever DID fire it would be a silent wrong answer.** `exec()` and `query()` would quietly
   stop sharing a connection — the single guarantee this class exists to provide — so a template would run
   and MEAN SOMETHING DIFFERENT with nothing failing. That is the failure class this repo keeps recording,
   arrived at by defensive coding.

⇒ **degrading here is never right, so nothing offers it.** User-decided the same day — *"we don't need any
fallbacks with CanPinConnection"* — and the probe went with the fallback: `IHostQuery.CanPinConnection`,
`Host.CanPinConnection` and `HostFs.CanPinConnection` are all DELETED. A probe exists only so a caller can
degrade; with no legitimate way to degrade it is machinery that reads as an option and offers none.

⚠ **What remains is a null GUARD, not a probe**: `HostFs.OpenConnection` tests the function pointer inline
so a zeroed host-services block yields a sentence rather than a null-pointer call. Nothing branches on it.
And it cannot fire for our own host — the C++ side refuses a bridge whose declared ABI version differs, so
a running bridge implies a host of exactly its version.

⚠ The same question exposed a wrong MESSAGE on the interface's default implementation. It read *"this host
does not support pinned connections (needs ABI v84)"*, which describes a case the throw cannot handle at
all: an old HOST never reaches managed code, because the C++ side refuses a version-mismatched bridge at
boot. The default fires only when an IMPLEMENTATION did not override the member — realistically a plugin
author's test double, which is the only reason it exists (the `IProviderCatalog.NotHosted` precedent).
Corrected to say that.

⚠⚠ **The contrast that makes the decision principled rather than a preference: `Host.CanQuery` STAYS, and
has a dozen real callers.** Reaching the host engine at all genuinely IS optional — a provider legitimately
falls back to its own parquet reader — so branching there produces a correct, merely slower answer.
Branching on pinning produces a DIFFERENT answer. A capability probe is worth having exactly when the
degraded path is still right.

## 13. THE `{% exec %}` BLOCK — a real statement, not an escaped string (2026-09-03)

User-asked, with the shape given: a custom Fluid block on the `RegisterEmptyBlock` pattern that renders its
body **to a separate output**, executes the captured text as SQL, and writes **nothing** to the caller's
output. Gate `verify_plugin_fluid` **256 → 275**, two mutants.

```sql
SELECT fluid_render('{% exec %}
INSERT INTO t VALUES
{% for r in rows %}({{ r.id }}, {{ r.name | sql }}){% unless forloop.last %},{% endunless %}
{% endfor %}
{% endexec %}done', {'rows': [{'id': 1, 'name': 'a'}, {'id': 2, 'name': 'O''Brien'}]});
-- done          (the block contributes no text; both rows land, the quote escaped by | sql)
```

### 13.1 Why it is better than `exec("…")`, concretely

The function form takes the whole statement as ONE string argument, so every quote inside it must be
escaped through SQL's literal syntax *and* Liquid's, and the statement has to be assembled by
concatenation. The block form makes the statement ordinary template text: multi-line, with `{% for %}` /
`{% if %}` inside it, and no escaping at all. It is also naturally CONDITIONAL — an unreached
`{% exec %}` runs nothing, because the tag is a statement in the tree rather than an argument that had to
be evaluated to build a call (gated).

**⚠ That paragraph used to read *"The function form is not superseded: it RETURNS the affected-row count,
and the block deliberately does not."* Since 2026-09-04 the block returns it too** — see §13.8. The
function form remains the right spelling inside an expression; the block is the right one for a real
statement.

### 13.8 ✅ `{% exec name %}` binds the affected-row count (2026-09-04, user-asked)

```liquid
{% exec n %}DELETE FROM staging WHERE loaded{% endexec %}removed {{ n }} rows
```

The same shape as `{% query name %}` — an optional identifier, then optional named arguments — and
deliberately the same VALUE the `exec()` function and the `| exec:` filter yield, because all three go
through one `Run`. **Gated as a triple** (`block=2 fn=2 filter=2`): a count computed in more than one place
can drift, and that assertion is what stops it.

**⚠⚠ THE IDENTIFIER IS OPTIONAL, AND THAT IS THE WHOLE DIFFICULTY.** `{% exec %}` and `{% exec x: 7 %}`
are shipped spellings that must keep working, and a bare optional `Ident` cannot coexist with the second:
on `{% exec x: 7 %}` it matches `x`, `ZeroOrOne` then SUCCEEDS having consumed it, and the `: 7` left over
is a parse error — **`ZeroOrOne` does not retry its empty branch once the sequence fails downstream.**

The fix is a negative lookahead, which consumes nothing and fails the identifier branch exactly when the
token is really the first named argument:

```csharp
ZeroOrOne(parser.Ident.AndSkip(Not(Terms.Char(':')))).And(ZeroOrOne(parser.NamedArguments))
```

With it, `{% exec n x: 8 %}` reads like `{% query t x: 8 %}` — identifier, space, comma-separated
arguments, no comma after the name. ⚠ **Mutation-tested, and the mutant dies at a PRE-EXISTING §16
assertion** (`{% exec x: 7, y: 8 %}`) after 290 pass, not at one of the new ones — which is what shows the
lookahead is protecting a shipped spelling rather than only enabling a new one.

⚠ Without a name the value is DISCARDED, exactly as before. A block renders nothing and most callers want
nothing back; binding is opt-in by writing the name.

⚠ A CTAS reports the ROW COUNT here (`n=7` for `CREATE TABLE c AS SELECT * FROM range(7)`), where
`fabricator_host_exec` reports 0 — §11's measured divergence between the two exec surfaces, pinned so the
block cannot quietly pick the other one.

### 13.9 ✅ The `{% print %}` block (2026-09-04, user-asked)

`{% query %}` with the destination changed: the rows are RENDERED instead of bound to a name.

```liquid
{% print delim: ", " %}SELECT name, amt FROM orders ORDER BY amt{% endprint %}
```

- **It routes through the SAME `FluidHostQuery.RunCaptured`**, so the classifier (SELECT only), the
  per-render pinned connection, the row cap and the value model are ONE mechanism rather than a second copy
  free to drift.
- **⚠⚠ Each cell is written with `WriteToAsync` — the call `{{ r.a }}` itself makes — not `ToStringValue`.**
  That is what makes printed text identical to interpolated text; a second formatting path would be free to
  disagree about numbers, dates and nulls, and would do so silently.
- `delim` joins the VALUES of a row (default a space), `rowdelim` joins the ROWS (default a newline). Both
  are **JOINERS, not terminators** — nothing before the first row or after the last, so composing the block
  into a larger string does not leave a stray separator. A caller who wants a trailing one writes it.
- `sql_literal` (default false) renders each value as a DuckDB **SQL literal** instead of as text — the
  SAME `FluidValueModel.SqlLiteral` the `{{ v | sql }}` filter uses, so the two cannot disagree about
  quoting, about the invariant number format, or about which values are refused. What it is for:

  ```liquid
  INSERT INTO t VALUES ({% print sql_literal: true, delim: ", ", rowdelim: "), (" %}
  SELECT id, name FROM staging
  {% endprint %});
  ```

  ⚠ It is an **ALLOW-LIST, not an escaper**: a cell with no provably safe rendering (a LIST, a STRUCT) is
  refused BY NAME rather than stringified, and the refusal names `{% print sql_literal %}` rather than the
  `sql` filter the author never wrote. ⚠ It inherits the filter's temporal rule — every date renders as a
  `TIMESTAMPTZ` literal, so the INSTANT survives and the TYPE does not — and it writes RAW, bypassing the
  encoder, because an encoder would turn the quotes it exists to produce into `&#39;`.
- No IDENTIFIER, unlike `{% query name %}` and `{% exec name %}`: there is nothing to bind, so the header is
  arguments-only and needs none of §13.8's lookahead.

**⚠⚠ `delim`, `rowdelim` and `sql_literal` are RESERVED ARGUMENT NAMES**, so a statement wanting a parameter
of any of those names cannot get one. Accepted because it fails LOUDLY — DuckDB reports the parameter it was not given, BY NAME —
rather than silently binding nothing. ⚠ The request's `delim := " "` spelling is not expressible: Fluid's
grammar is `name: value`, and inventing one would be a grammar only this plugin speaks, which is the reason
`ArgumentsList` was reused for the other blocks in the first place.

#### ⚠⚠ It corrected a message that had been naming the wrong surface

The SELECT-only refusal said `query() runs SELECT statements only` whatever refused — so a `{% print %}`
author was told to look at `query()`. `Run` now takes the surface name (defaulting to `query()`, so the
function and filter forms are untouched) and each spelling names itself:

| refused in | says |
|---|---|
| `query(…)` / `\| query:` | `query() runs SELECT statements only` |
| `{% query r %}` | `{% query %} runs SELECT statements only` |
| `{% print %}` | `{% print %} runs SELECT statements only` |

⚠ **The `{% query %}` block had been mis-naming itself all along and its own assertion is what caught it** —
the row is corrected in the suite rather than the wording reverted, because the advice a refusal gives is
only useful if it points at the tag the author wrote.

### 13.2 ⚠ Everything downstream of the capture is SHARED with the function form

The block routes into `FluidHostExec.ExecuteCaptured`, which uses the same empty-body guard, the same
classifier (`RefuseIfSelect`), the same per-render pinned connection (§12) and the same messages. One
mechanism, two spellings — so the block cannot drift from `exec()` on what counts as a write, and a
`{% exec %}` staging a TEMP table is readable by a later `query()` in the same template (gated, and that
assertion needs both features at once).

### 13.3 ⚠⚠ What building it against beta.7 established

- **The signature is `IFluidOutput`, not `TextWriter`** — the request's sketch used the 2.x shape.
  `RegisterEmptyBlock(string, Func<IReadOnlyList<Statement>, IFluidOutput, TextEncoder, TemplateContext,
  ValueTask<Completion>>)`. ⚠ The local Fluid clone is at `main`, which is AHEAD of our pinned
  `3.0.0-beta.7`; the signature was read from `git show v3.0.0-beta.7:…` rather than from the working tree,
  because a clone at a different revision is exactly the "CI gates a different Fluid than the developer
  runs" hazard this repo already records about referencing a local clone.
- **`BufferFluidOutput` is `internal`; `TextWriterFluidOutput` is public** — so the capture is a
  `StringWriter` wrapped in the latter, which is also what Fluid's own `{% capture %}` source generator
  emits.
- **⚠⚠ The capture output BUFFERS, so the body must be flushed before it is read** — otherwise a statement
  shorter than the buffer has not reached the `StringWriter` at all and the block "executes" the empty
  string. See §13.4: my first arrangement made this claim and could not back it.
- **`Render(template, context)` passes `NullEncoder.Default`**, read from
  `FluidTemplateExtensions.Sync.cs`. The block nonetheless passes `NullEncoder.Default` EXPLICITLY rather
  than the ambient encoder: the body is SQL and must never be HTML-escaped (an encoder turns `'` into
  `&#39;` and corrupts every literal). Fluid's `{% capture %}` passes the ambient encoder through, which is
  right for HTML and wrong here. ⚠ This changes nothing today and is therefore NOT gated — it is
  future-proofing against a caller rendering through an encoding overload, and saying so beats implying a
  test covers it.

### 13.4 ⚠⚠ A mutant survived and the CODE changed, not the comment

The first version read the captured text AFTER the `await using` block. A mutant dropping the explicit
`FlushAsync()` **survived**, because `TextWriterFluidOutput.DisposeAsync` flushes — so the flush was
redundant and the comment calling it "MANDATORY" was wrong.

Fixed by restructuring rather than by softening the comment: the text is now read INSIDE the scope,
immediately after the flush, which is what upstream's generated capture code does and makes the dependency
explicit and local instead of resting on disposal order. Re-run, the same mutant now dies at the FIRST
block assertion after 257 pass.

⇒ the general form of it, which this file has recorded before: **a defensive step justified by a hazard
nobody measured is indistinguishable from a necessary one until you delete it** — and when the mutant
survives, the honest fix is sometimes to make the step necessary rather than to remove it.

### 13.5 ⚠ A partially rendered body is NOT executed

A `{% break %}` inside the block (belonging to an enclosing `{% for %}`) leaves a HALF-RENDERED statement,
and running half a statement is a different statement. The completion is propagated instead — which is
what the author asked for by breaking. MEASURED: zero rows written. Mutant E (execute anyway) dies at
exactly that assertion after 267 pass.

### 13.6 ⚠ Interpolation inside the block is RAW

Same rule as `fluid_query`, for the same reason: a template must be able to emit object names and whole
fragments, so `{{ x }}` cannot escape. Use `{{ v | sql }}` for a VALUE and `{{ n | sql_ident }}` for an
identifier. The failure mode without it is a PARSER ERROR rather than a silent injection (gated:
`O'Brien` spliced raw gives *"unterminated quoted string"*), which is the safe direction but not a
substitute.

### 13.7 ⚠ Registration is on the shared parser, and it must precede any parse

`FluidEngine.Parser` is built by a METHOD now rather than an object initializer, so the tag is registered
before anything can be parsed — templates are cached by text, so one parsed before registration would be
cached with `{% exec %}` unrecognised and stay that way for the process's life.

⚠ The three spellings of `exec` coexist — the BLOCK, the `exec()` function and the `| exec` filter — because
tags and expressions are different grammars in Fluid. Pinned, because it is not obvious and a change
would silently break one of them.

## 14. `fabricator_render` IS NOW `fluid_render` (2026-09-03, BREAKING, no alias)

User-asked. The function is contributed by the Fluid provider and its sibling was already `fluid_query`, so
the `fluid_` prefix is the one that describes it; `fabricator_*` is the core/host namespace
(`fabricator_query`, `fabricator_exec`, `fabricator_host_query`, `fabricator_plugins`). Per this repo's
standing convention for renames — the `fabricator` rename, `IArrow*`, `ITable`, `IProvider` — **no alias is
kept**: the old spelling now answers *"Scalar Function with name fabricator_render does not exist!"*.

⚠ **THE CODE CHANGE IS ONE LINE.** `FluidRenderFunction.Name`; everything else in the plugin was doc
comments. The bulk of the work was the 133 occurrences in `verify_plugin_fluid.test` and 20 in the README.

⚠ **It silently changed an ORDER BY, which is the one thing a mechanical rename can break.**
`verify_plugin_fluid`'s registration check does
`… WHERE function_name IN ('fluid_render','fluid_query') GROUP BY 1 ORDER BY 1` — and
`fabricator_render` sorted BEFORE `fluid_query` while `fluid_render` sorts AFTER it, so the expected rows
had to swap. Caught by running the suite; a rename that only compiles is not a rename that passes.

### 14.1 ⚠ Older dated records deliberately keep the old spelling

`docs/abi-history.md` (the v80/v82 entries), `docs/feature-history.md`, `docs/plugin-system.md` §The FLUID
plugin, and the floor-bump comments in `scripts/run-suites.sh` still say `fabricator_render`. That is the
convention every previous rename here followed: a passage that RECORDS what was measured on a given day is
not made truer by rewriting the name it was measured under. **Every `fabricator_render` in a dated record
is this function under its former name** — said here once so the connection is findable, rather than
annotating each site.

## 15. THE `{% query name %}` BLOCK — the body is SQL, the RESULT IS A RESULT SET (2026-09-03)

User-asked, and the requirement was stated sharply: *"where result is the result set and not some rendered
as a single varchar, i.e. like a function call result"*. Gate `verify_plugin_fluid` **275 → 285**, one
mutant aimed at exactly that requirement.

```liquid
{% query result %}
SELECT 1 AS a, 'two' AS b, 3.5 AS c
{% endquery %}
{{ result[0].a }} {{ result[0].b }} {{ result.size }}
```

### 15.1 ⚠ The sketch's spelling is not expressible, and this is the nearest thing

The request wrote `{% assign result = query %}…{% endquery %}`. Liquid cannot express that: `assign` parses
`identifier = EXPRESSION` and terminates at `%}`, so a block body can never be its operand. What ships is
an IDENTIFIER block — `{% query result %}` — which is **`{% capture %}`'s own shape**, i.e. Liquid's
established precedent for *"run this block and bind the result to a name"*. One tag instead of two, and it
reads the same way.

`FluidParser.RegisterIdentifierBlock` exists at our pinned `3.0.0-beta.7` and hands the delegate the
identifier; `ctx.SetValue(identifier, value)` is what binds it.

### 15.2 It is the SAME value the function returns, by construction

`RunCaptured` calls the same `FluidHostQuery.Run` the `query()` function and the `| query:` filter call, so
the result is the same `ArrayValue` of `DictionaryValue`-wrapped rows. One mechanism, three spellings —
the classifier, the 1,000,000-row cap, the value model and the per-render pinned connection cannot drift
between them.

MEASURED, and these are the assertions that separate a result set from a string:

| | |
|---|---|
| `r.size` → 1, `r[0].a` → 1, `r[0].b` → `two`, `r[0].c` → 3.5 | addressed BY COLUMN NAME, per row |
| `r[0].a \| plus: 1` → **2** | a NUMBER — arithmetic, not concatenation |
| `{% if r[0].a > 0 %}` → **yes** | it COMPARES as a number |
| `{% for x in rs %}` over 3 rows → `sum=6` | a real iterable array |

⚠ The comparison row is the one that matters most, and §7 is why: a broken value model **renders correctly
while computing wrong**, so a render-only assertion cannot tell the two apart.

**Mutant F — bind the captured TEXT instead of the rows** — dies at the FIRST assertion of §14 after 275
pass. That is the user's requirement expressed as a test.

### 15.3 ⚠ Optional NAMED ARGUMENTS, bound as parameters (added the same day — see §16)

This section first read *"no parameters — an identifier block has nowhere to put named arguments"*, and
that was true of an IDENTIFIER block and false of what Fluid can express. Both blocks are PARSER blocks
now and take optional named arguments that become BOUND parameters:

```liquid
{% query t region: 'eu', min: 10 %}SELECT … WHERE region = $region AND n >= $min{% endquery %}
{% exec x: 7, y: 8 %}INSERT INTO ab VALUES ($x, $y){% endexec %}
```

⚠ The body is still raw-interpolated, and `{{ v | sql }}` is still what carries an object NAME or a
fragment — a parameter cannot. Full record: §16.

### 15.4 The capture is now ONE helper, shared with `{% exec %}`

`FluidEngine.CaptureBodyAsync` renders a block body to text and is used by both blocks. That is not
tidiness: it is where §13.4's flush subtlety and the partial-body rule live, and a second copy is where
they would come back. Both blocks therefore inherit the flush-before-read arrangement and the rule that a
body which did not complete normally yields NO text and runs nothing.

⚠ All THREE spellings of `query` coexist — block, function, filter — because tags and expressions are
different grammars in Fluid. Pinned, since a change would silently break one.

## 16. OPTIONAL NAMED ARGUMENTS ON BOTH BLOCKS (2026-09-03)

User-asked — *"could we eventually allow optional named args e.g. `{% query t arg1: 1 arg2: 2 %}` which
could be used for parameter binding?"* — with a pointer to
[deanebarker.net/tech/fluid/parser-tags-blocks](https://deanebarker.net/tech/fluid/parser-tags-blocks/) and
the caveat that it might be out of date. Gate `verify_plugin_fluid` **285 → 296**, one mutant.

```liquid
{% query t region: 'eu', min: 10 %}SELECT … WHERE region = $region AND n >= $min{% endquery %}
{% exec x: 7, y: 8 %}INSERT INTO ab VALUES ($x, $y){% endexec %}
```

### 16.1 The article's trick works; two of its details do not

Its key move is real and is what this rests on: `Identifier` and `ArgumentsList` are **`protected readonly`**
on `FluidParser`, so a SUBCLASS is the only way to compose them into a custom block's header
(`FabricatorFluidParser`). Checked against `git show v3.0.0-beta.7:` rather than the local clone, which sits
at `main` and is ahead of our pin. Two things in it are stale:

| the article | our pin |
|---|---|
| `RegisterTagBlock()` | **does not exist** — it is `RegisterParserBlock` |
| `List<FilterArgument>` | `IReadOnlyList<FilterArgument>` |

### 16.2 ⚠ Fluid's OWN grammar, not one invented here — and that decides the comma

`ArgumentsList` is `Separated(Comma, OneOf(Identifier ':' Primary, Primary))`, so arguments are
**comma-separated** and there must be at least one — `ZeroOrOne` around it is what makes the whole list
optional, and without that every bare `{% query t %}` would stop parsing.

**⚠ MEASURED: the comma-free form in the request does NOT parse** — `{% query t arg1: 1 arg2: 2 %}` gives
*"Invalid query tag at (1:9)"*. Pinned as a CHARACTERIZATION test, because it is the form people write
first and the parse error does not mention commas.

A separator-free grammar IS buildable — `LogicalExpression` is also `protected`, so
`ZeroOrMany(Identifier ':' LogicalExpression)` would accept it — and it was **deliberately not built**: it
would be a grammar only this plugin speaks, where `name: value, name: value` is what every other named
argument site in Liquid uses (`| filter: a: 1, b: 2`, `{% render 'x', a: 1 %}`). Consistency beat matching
the sketch keystroke for keystroke.

### 16.3 One conversion table, three spellings

A tag's arguments arrive as unevaluated `FilterArgument`s (name + expression) where a filter's arrive
already evaluated, so `BuildBlockParametersAsync` evaluates each and hands it to the SAME `ToParameter`
the filter form uses. The int64→decimal ladder, the UTC stamp on dates, the LIST element rules and the refusal of STRUCT/MAP
therefore cannot drift between a filter and a block.

⚠ POSITIONAL arguments are REFUSED rather than ignored (the statement references `$name`, so an unnamed one
cannot be bound and dropping it would run the statement with a parameter the author believed they
supplied), and a DUPLICATE name is refused HERE as well as by the host — the host's refusal is correct but
names the crossing rather than the tag.

### 16.4 ⚠⚠ The load-bearing gate assertion is the injection PAIR

`region: "eu' OR 1=1 --"` answers **0** where splicing would answer 3, and the same statement with `"eu"`
answers **2**. The first row alone is worthless — it is equally true of a build where the parameter never
arrived — so the control is what makes it a binding result. **Mutant G (ignore the tag's arguments) dies at
the first assertion of §15 after 285 pass.**

### 16.5 ⚠ It made a documented limitation false, which is the thing to watch

§15.3 read *"No parameters — an identifier block has nowhere to put named arguments"*. That was true of an
IDENTIFIER block and false of what Fluid can express, so it was a limitation of the CHOICE rather than of
the library — the kind of sentence that hardens into a fact if nobody re-reads it. Corrected in place, in
the README, in the suite's own comment and in CLAUDE.md, rather than left to contradict the feature.

## 17. ⚠ OPEN — referencing a previous `{% query %}` result BY NAME in a later one (ANALYSED, NOT BUILT)

User-proposed 2026-09-03: *"we also have this replacement scan where we register a name and an
arrowstream. i guess this is on a connection level. we could try to use the replacement scan feature with
fluid … we could register the query result as arrow after execution with name = variablename. but how do
we get the replacement scan feature into fluidplugins rendersession?"*

The goal:

```liquid
{% query t %}SELECT region, n FROM big{% endquery %}
{% query u %}SELECT region, sum(n) AS s FROM t GROUP BY region{% endquery %}
```

### 17.1 ⚠⚠ The guess is wrong in the load-bearing place: BOTH halves are GLOBAL, not per connection

- `fabricator_host_query.cpp:910` — `DBConfig::GetConfig(loader.GetDatabaseInstance())
  .replacement_scans.emplace_back(NamedSourceReplacement)`. A replacement scan is registered on the
  **DatabaseInstance**, so it fires for every connection in the process.
- `Host.cs:244` — `private static readonly ConcurrentDictionary<string, NamedSource> Sources`. The name
  registry is **process-static**.

⇒ built as sketched, `{% query t %}` would publish `t` process-wide. `fluid_render` is a VOLATILE scalar
evaluated PER ROW and may run on several threads at once, so two concurrent renders both binding `t` is
not a hypothetical collision — it is the ordinary case. And a name would outlive its render unless
explicitly removed, so a later unrelated statement could resolve `t` to a dead result. **Both are silent
wrong answers, not errors.**

### 17.2 ⇒ The pinned connection already gives render-scoped naming, and the refusal I wrote IS the feature

> ⚠⚠ **THE FIRST SENTENCE BELOW IS FALSE — corrected in §17.6, measured in §17.9.** `duckdb_arrow_scan`
> registered a CATALOG view, not a connection-scoped one. The SECTION'S CONCLUSION SURVIVES, because the
> fix made the premise true: bound inputs are TEMPORARY views since 2026-09-03. Kept as written because
> the reasoning it led to is what got built. The rest of §17.2 reads correctly today.

`duckdb_arrow_scan` registers a **CONNECTION-scoped view**. §13/abi-history §v84 refuse named Arrow inputs
on a pinned connection with the reason *"a connection-scoped view would outlive the call and collide with
the next one"* — and for a RENDER SESSION, outliving the call is exactly the requirement. Same fact,
opposite sign.

Registering the result as a view on the render's own pinned connection gives, by construction:

| | |
|---|---|
| scope | the render — the connection dies with it, taking the view |
| collisions | impossible between renders; each has its own connection |
| cleanup | none; no global registry to unregister from |
| resolution | a real relation, so `FROM t` binds normally — **no replacement scan needed at all** |

So the answer to the user's question is that the replacement-scan machinery is the wrong half to reach
for; the right one is the v84 connection, which the plugin already owns.

### 17.3 The hard part is OWNERSHIP, not naming

`duckdb_arrow_scan` keeps a **raw pointer** to the stream, so the stream must outlive the view — i.e. the
whole render. Today `FluidHostQuery.Run` reads cells EAGERLY and disposes each batch, deliberately: a
disposed `RecordBatch` has its arrays nulled and fails loudly on first read (§9 records that a mutant
proved this is a NullReferenceException, not a silent use-after-free).

To expose the same result as Arrow, `FluidRenderSession` would have to OWN the batches for its life. Two
consequences, one of them a genuine improvement:

- ⚠ **Memory doubles unless the rows become lazy.** Rows are already held for the render (the 1,000,000-row
  cap exists for that), so retaining batches as well is double-holding — *unless* the Fluid rows become
  lazy views over the retained batches, which is possible **only** because the session would then own
  them. The eagerness exists precisely because nothing owned them.
- ⚠ A second `{% query t %}` must REPLACE the view, so re-assignment needs a defined drop/replace step
  rather than a second registration under the same name.

### 17.4 The plugin-reachability question, which is what was actually asked

`Host.RegisterSource` lives in `Fabricator.Bridge`, which a plugin does **not** reference (§plugin-services
§1: the constraint is dependency WEIGHT, not visibility). So this cannot be reached by adding a static —
it needs a host SERVICE, following the `IHostFileSystem` / `IHostQuery` pattern. The natural shape is a
member on the object that already owns the lifetime:

```csharp
public interface IHostConnection : IDisposable
{
    // …
    void Bind(string name, IReadOnlyList<RecordBatch> rows);   // a connection-scoped Arrow view
}
```

That keeps the scope and the lifetime in one object, so "the view dies with the render" is true by
construction rather than by discipline. It needs the ABI's refusal of inputs on a pinned connection to be
lifted for this path.

### 17.5 ⚠ Two hazards to settle BEFORE building

1. **A bound name SHADOWS a real table on that connection.** `{% query orders %}` would make a later
   `FROM orders` in the same render read the template's rows rather than the catalog's — silently. That is
   either the feature or a serious trap depending on who is writing the template; it needs a deliberate
   answer (a required prefix? refuse a name the catalog already resolves?).
2. **Bind repetition on the sqlgen surface.** A `fluid_query` template renders per BIND, so the
   registration repeats — harmless if each bind has its own connection (it does), but it means the cost is
   paid per bind, not per execution.

Not built. The measurement that would justify it is a real multi-step template where the intermediate is
large enough that round-tripping it through `| sql` interpolation is the wrong shape.

### 17.6 ⚠⚠ CORRECTION (user-caught): `duckdb_arrow_scan`'s view is NOT temporary, and NOT collision-prone

User-asked: *"isn't the connection scoped view here created as a temporary view which should not
collide?"* — and reading the source settles it against me on BOTH counts.
`duckdb/src/main/capi/arrow-c.cpp:425` ends in

```cpp
->CreateView(table_name, /*replace=*/true, /*temporary=*/false);
```

| what I wrote | what the source says |
|---|---|
| the view "would collide with the next one" (abi-history §v84, and the refusal's own message) | **`replace = true`** — re-registering a name REPLACES it. There is no collision |
| "`duckdb_arrow_scan` registers a CONNECTION-scoped view" (§17.2, §v84) | **`temporary = false`** — it is an ordinary CATALOG view, visible to every connection |

⇒ **§17.2's argument is void as written.** It rested on "connection-scoped ⇒ render scope, dies with the
render", and a non-temp catalog view does neither: it would be visible to other connections and outlive
the render, i.e. exactly the global-namespace problem §17.1 rejects the replacement-scan route for.

**The design survives, but it must ASK for what I assumed.** `Relation::CreateView` has a 3-arg overload
taking `temporary`, so a **`CREATE OR REPLACE TEMP VIEW`** over `arrow_scan(...)` gives real
connection scope — and with `replace` it is also re-assignable, which is what `{% query t %}` twice needs
(§17.3's "defined drop/replace" is then free). That is a different call from `duckdb_arrow_scan`, so the
host would issue the SQL itself rather than reuse the C-API helper.

⚠ **UNMEASURED, and not claimed either way: whether a view created by today's `inputs` path actually
persists after its fresh connection closes.** A probe against a small Delta table showed `memory` views
0 → 0, but that shape took the plain `read_parquet` form and bound no inputs, so it never reached a
view-creating path. It is a real question for the EXISTING code, independent of §17: a non-temp view named
by the caller, created on a throwaway connection, has no obvious cleanup.

⚠ **And the v84 refusal now has no stated reason.** Refusing named inputs on a pinned connection may still
be right — a caller-named CATALOG view created on a long-lived connection is worse than on a throwaway one,
and the semantics of "persists until you replace it" were never designed — but the message and the doc
give a justification that is false. Fix the reason before relying on the refusal.

### 17.7 ⚠ Where else do we create an arrow-scan view? EXACTLY ONE PLACE — and the answer is TEMP, not a flag

User-asked: *"so wherever we use this arrow scan with a view should be analysed if we should use temp view
instead or add a flag to functions for temp or not?"*

**The surface is one line.** `grep duckdb_arrow_scan( src/` → `fabricator_host_query.cpp:617`, the named
`inputs` loop in `MakeHostQueryStream`. Nothing else in the tree creates an arrow-scan view.
(`FabricatorSchemaEntry::GetOrCreateView` is the ABI v77 provider-declared catalog views — a different
mechanism, real entries in the ATTACHed catalog, correctly non-temp.) One site makes this cheap to fix and
cheap to get right.

**⚠ The hazard is sharper than "a leak", because of v83.** The fresh connection inherits the CALLER'S
search path, so the view's default schema is whatever the caller last `USE`d — i.e. a view named by a
managed caller can land in the USER'S OWN schema, under a name the user did not choose.

**⇒ Use a TEMP view, unconditionally. Do NOT add a flag.** Four reasons, in order of weight:

1. **No current consumer wants a catalog object.** Every use is data-in for one query; the view exists to
   give that statement a name to read.
2. **A flag would be a choice nobody can make correctly**, because making it correctly requires knowing
   everything in §17.6. And the non-temp branch is not a capability worth offering: "persists in your
   catalog under a name the extension chose" has no caller asking for it.
3. **TEMP + `replace` gives re-assignment for free**, which is exactly what §17.3 listed as an open
   problem (`{% query t %}` twice).
4. **⚠ It removes the hazard class WITHOUT settling the measurement I could not complete.** Whether the
   view persists today is UNKNOWN — three probes failed to provably reach an input-binding path. A temp
   view is correct whether or not it does, which is why this recommendation does not wait on that answer.

**The change:** issue the SQL ourselves — `CREATE OR REPLACE TEMP VIEW <name> AS SELECT * FROM
arrow_scan(…)` — rather than calling `duckdb_arrow_scan`, whose `temporary: false` is hardcoded. The
3-arg `Relation::CreateView(schema, name, replace, temporary)` overload is the other route.

⇒ **It also lets the v84 refusal be LIFTED rather than re-justified.** With a temp view the reason to
refuse named inputs on a pinned connection disappears: scope becomes the connection (= the render),
re-registration replaces, and nothing reaches the catalog. That is the prerequisite §17.2 needs.

⚠ **Still owed before the change lands:** establish whether the current non-temp view persists after its
connection closes. Not because the fix depends on it, but because if it DOES, that is a shipped defect on
the `inputs` path and it should be recorded as one rather than quietly fixed. A reliable probe needs a
path that provably binds inputs — three attempts via Delta reads did not, and the absence of any
`delta native batch:` log line is what shows they missed rather than passed.

### 17.8 ⚠ "Could there be a REASON for `temporary: false`?" — user-asked, and the fix was untested when proposed

Two fair objections: DuckDB may hardcode it deliberately, and §17.7 recommended
`CREATE OR REPLACE TEMP VIEW … AS SELECT * FROM arrow_scan(…)` **without running it**. Both addressed.

**MEASURED — the shape works, and re-assignment with it:**

```
CREATE OR REPLACE TEMP VIEW v AS SELECT * FROM fabricator_scan('fabricator_demo_lazy');  -> scans, 1 row
CREATE OR REPLACE TEMP VIEW v AS SELECT * FROM fabricator_scan('fabricator_demo_eager'); -> scans, 1 row
SELECT database_name, schema_name FROM duckdb_views() WHERE view_name='v';               -> temp | main
… WHERE database_name='memory' AND view_name='v'                                          -> 0
```

⇒ a temp view over an Arrow-producing table function binds, scans, REPLACES cleanly, and lands in
`temp.main` — out of the user's catalog entirely.

⚠ **What that probe does NOT cover, said rather than glossed:** it used our own `fabricator_scan(name)`,
not `arrow_scan(POINTER, POINTER, POINTER)`. The untested part is the pointer form specifically.

**⚠ But the pointer hazard is ORTHOGONAL to temp-ness, which is why the gap is narrow.** A view of EITHER
kind stores the `POINTER` constants and dereferences them on every scan — that is exactly what
`MakeHostQueryStream`'s existing comment is about ("the view keeps the RAW POINTER and the query below is
LAZY, so the caller's own allocation must not be what it points at", hence `OwnedArrowInputs`). Making the
view temporary changes its SCOPE, not its pointer lifetime, so nothing about the adoption discipline
changes.

**Why might DuckDB hardcode `temporary: false`? UNKNOWN, and not guessable from here.** The `duckdb`
submodule is a SHALLOW clone (`shallow = true`), so `git log -S` and `git log -L` attribute every line to
the single fetched commit — there is no history to read. ⚠ The plausible candidate, offered as REASONING
not evidence: a non-temp view is visible to OTHER connections, which a C-API consumer registering a stream
on one connection and querying it from another would need. That is precisely the property we do not want,
which is consistent with the recommendation but is not a substitute for asking upstream.

⇒ **The recommendation stands, with its basis corrected:** do not change DuckDB's helper — stop using it,
and issue our own temp view. The remaining work before the change lands is (a) the same probe against the
real `arrow_scan(POINTER…)` form, and (b) §17.7's outstanding question of whether today's non-temp view
persists.

### 17.9 ✅ RESOLVED — both measurements taken, and the fix is BUILT (2026-09-03)

**The `inputs` path registers TEMPORARY views now.** Full record, with the probes and their positive
controls: [host-query.md](host-query.md) §Named Arrow inputs are TEMPORARY views. In summary:

| what §17.9 asked | answer |
|---|---|
| does today's non-temp view PERSIST after its connection closes? | **YES** — and it is worse than persistence: it accumulates one per statement, and `SELECT * FROM <name>` afterwards **SEGFAULTS**, because the stream it points at was released when its query finished. A shipped defect, recorded as one. |
| does `arrow_scan(POINTER…)` work under a TEMP view? | **YES** — `cf_host_sum` still answers 10, and nothing is left in `memory.main`. |

The change is `RegisterArrowInputView` in `src/fabricator_host_query.cpp`: upstream's
`duckdb_arrow_scan` body with `temporary` flipped, because its factory pair lives in an anonymous namespace
and is unlinkable from an extension. Gate `verify_delta_catalog_filter_modes` **39 → 55**, mutation-tested.

⚠ **The probe discipline §17.9 demanded is what made this work, and it earned its keep twice.** The three
earlier probes were inconclusive *and looked like passes*; the two that settled it each carry a control that
proves the path ran (a value only the bound input can produce; a Debug line naming the exact-filter mode).
And the gate's FIRST assertion was itself vacuous — a mangled `LIKE … ESCAPE` matched nothing and passed on
both builds — caught only by the mutation test.

### 17.9a ⛔ DEFERRED BY DECISION (user, 2026-09-04): "the query + automatic CTAS is not needed for now,
maybe revisit later"

**Nothing below is pending work.** The sugar — `{% query t %}` issuing its own
`CREATE TEMP TABLE t AS (body)` so a later block can say `FROM t` — is deliberately NOT built.

⚠ **Deferring it costs nothing, which is why it was cheap to decide.** The MECHANISM already ships and is
gated: `{% exec %}CREATE TEMP TABLE t AS …{% endexec %}` followed by `{% query u %}… FROM t{% endquery %}`
works today on the per-render pinned connection (measured, §17.9's own probe: `n=4 s=100`, and a staged
table read twice answers `a=3 b=6`). So a template that needs this can express it in one extra tag; what is
deferred is only who writes the CTAS.

⚠ **The one thing that would still need settling if it is revisited** — and the reason it is not a
five-minute change — is §17.5's naming hazard in its sharper form: `t` becomes a TEMP TABLE name, and a temp
table SHADOWS a catalog table of the same name on that connection. Silently. Decide that deliberately
(namespace the name? refuse one the catalog already resolves?) rather than defaulting to it.

⚠ Everything from §17.10 down is therefore BACKGROUND, kept because it was measured and because the option
analysis applies to a DIFFERENT case that is still open: data originating in C# with no SQL of ours
producing it. Read it as a record, not a queue.

### 17.10 ⇒ THE v84 REFUSAL, still unlifted (and no longer needed for Fluid)

The prerequisite §17.2 needs is now in place — a bound input on a pinned connection would be a TEMP view
scoped to that connection, i.e. to the render — but **the refusal has not been lifted**, and it is not a
one-line deletion. The ownership question §17.3 raised is the real work and it is unchanged:

- Today `OwnedArrowInputs` is owned by the **result stream**, and the view is dropped with the connection.
  On a fresh connection those coincide. On a **pinned** one they do not: the view would outlive the result
  stream, so the stream's storage must be re-homed onto the connection (or the pin) or the view points at
  freed memory again — the very defect just fixed, in a new place.
- Only then does the naming question of §17.5 matter (a bound name SHADOWS a real table on that connection),
  and the re-assignment question dissolves on its own, since `replace: true` already replaces.

### 17.11 ⚠⚠ A BOUND INPUT IS SINGLE-USE, so "reference `t` later" needs MATERIALIZING, not just scoping

User-raised 2026-09-03: *"is such a temp view + arrow_scan one single scan or can such a view be queried
several times?"* — and it is the question that decides §17.10's design, which had the wrong answer in it.

**The VIEW is re-queryable; the DATA is not**, and the chain is three links, each read rather than recalled:

1. `ProduceArrowScan` is called once per scan's global init (`duckdb/src/function/table/arrow.cpp:142`), so
   a second reference to the view is a second scan with its own global state — it binds and plans normally.
2. Our factory (like upstream's) hands back a wrapper around **the same `ArrowArrayStream *`**. It is a
   cursor, not a snapshot.
3. Behind it, `InMemoryArrayStream` is a `Queue<RecordBatch>` that **dequeues** — once drained it returns
   `null` forever.

⇒ the first scan consumes it and the second sees end-of-stream ⇒ **zero rows, silently**. Nothing to do
with temp-vs-catalog; it is the stream's property and today's fix does not touch it.

The tree already knows this and carries two mitigations: `HostBatchFilter` wraps its query in
`WITH b AS MATERIALIZED (…)` to force exactly one scan, and `SingleScanArrowStream` turns a second
end-of-stream read into a THROW — its comment says why in the sharpest available terms, that for a
deletion-vector anti-join zero rows is *deleted rows coming back*.

⚠ Source-read plus the tree's own recorded measurement; **not re-measured on 2026-09-03**. It needs a query
referencing one input twice and no in-tree one does — `cf_host_sum`'s SQL is fixed at
`SELECT sum(v) FROM in0`. A C#-only edit to that demo would measure it.

#### ⚠⚠ FIRST — FOR THE FLUID CASE, NEITHER OPTION IS NEEDED. MEASURED 2026-09-03.

User-asked, *"who and when is `IHostConnection.Bind` called?"* — and tracing it showed the whole A/B choice
below rests on a premise that was never checked: that a `{% query t %}` result has to be shipped BACK INTO
DuckDB as Arrow. **It does not, because for that block WE OWN THE SQL TEXT.** We can tell DuckDB to keep
the result instead of handing it back:

```
{% query t %}SELECT …{% endquery %}
   1. classify the body as a SELECT       (the guard that already exists)
   2. CREATE TEMP TABLE "t" AS (body)     on the per-render pinned connection
   3. SELECT * FROM "t"                   to fill the Fluid variable
```

**C#-only in the plugin: no ABI change, no C++, no lifetime machinery, and no v84 refusal to lift.**

⚠ **AND THE MECHANISM ALREADY SHIPS — measured with what is in the tree today**, not with a prototype:

```sql
SELECT fluid_render('{% exec %}
CREATE TEMP TABLE t AS SELECT i AS id, i * 10 AS v FROM range(1, 5) r(i)
{% endexec %}{% query u %}SELECT count(*) AS n, sum(v) AS s FROM t{% endquery %}n={{ u[0].n }} s={{ u[0].s }}', NULL);
-- n=4 s=100
```

and the staged table is genuinely re-scannable — two separate blocks reading one `t2` answer `a=3 b=6`,
which is the property a bound Arrow input does NOT have (§17.11). The enabling piece is **v84's per-render
pinned connection**, already shipped and already gated by `verify_plugin_fluid` §12. What is missing is
therefore only ERGONOMICS: `{% query t %}` issuing the CTAS itself instead of the author hand-writing an
`{% exec %}`.

⚠ It also inherits §17.5's naming hazard in a sharper form — `t` is an author-chosen identifier that
becomes a TEMP TABLE name, and a temp table SHADOWS a catalog table of the same name on that connection.
Quote it, and settle whether to namespace it.

#### ⇒ WHAT A AND B ARE STILL FOR

They apply where the data originates in **C#** and we do NOT own a SQL query producing it — a plugin
pushing a computed table in, i.e. the original replacement-scan use case. They do not apply to
`{% query t %}`.

#### ⇒ It CHANGES §17.10: prefer MATERIALIZING over owning the batches

§17.10 says the hard part is making the session own the batches so the view can outlive the result stream.
For `{% query t %}` … `{% query u %}SELECT … FROM t{% endquery %}` that is the **wrong fix**: a second
reference to `t` returns zero rows however long the stream lives.

| option | what it costs |
|---|---|
| **A. bind a REPLAYABLE Arrow source** — the factory builds a FRESH reader over retained batches per scan instead of re-wrapping one cursor | the batches must be owned for the render; no copy into DuckDB storage, no DDL, data stays Arrow |
| **B. MATERIALIZE into DuckDB** — `CREATE TEMP TABLE t AS SELECT * FROM <bound view>` on the pinned connection | the stream is released immediately and `t` is an ordinary re-scannable relation that dies with the render ⇒ no new lifetime machinery at all; costs a copy into DuckDB's storage format and a DDL per binding |

Neither was considered when §17.3/§17.10 were written; §17.10's "own the batches so the view outlives the
result stream" is not on this list because it does not make a second reference work.

**⚠⚠ WHAT MAKES (A) NEWLY CHEAP is today's commit, and it is the non-obvious part.** `RegisterArrowInputView`
is OURS now, and `FabricatorArrowStreamProduce` is called **once per scan**
(`duckdb/src/function/table/arrow.cpp:142`). Today it re-wraps one cursor. If the bound object were a
retained *list* of batches rather than a cursor, that same function could hand out a fresh reader each time
— and the view becomes replayable with no temp table, no CTAS and no copy. The replayability seam is
already in our hands; before today it was upstream's.

**⚠ There is no "materialize" option on `host_query` to reach for** — checked, not recalled: it returns a
lazy `ArrowArrayStream` and the ABI has no such flag (the only `materialize` in the tree is
`mssql_materialize`, an unrelated SQL Server scan-routing switch). But **the Fluid path does not need one**:
`FluidHostQuery.Run` already reads every cell eagerly into an `ArrayValue` of rows — that is why `MaxRows`
exists — so the result is materialized in managed memory before any of this starts.

#### ⚠⚠ THE TRUE REASON THE v84 REFUSAL EXISTS — found while asking how A's lifetime would work

The refusal's stated reason is false (§17.6) but it is **not** protecting nothing, and the real reason is the
thing A and B each have to solve. `HostQueryStream` declares `inputs` FIRST so they are destroyed LAST —
after `conn` — and its own comment says why. On a FRESH connection that is airtight: `conn` is the only
reference, so the Connection dies first, taking the temporary catalog and the view with it, and only then
are the input streams released.

**On a PINNED connection those two lifetimes diverge.** `conn` is a `shared_ptr` COPY (the pin holds
another), so releasing the result stream destroys `OwnedArrowInputs` while the Connection lives on — leaving
a temp view in the pin's catalog pointing at released streams. That is today's defect again, scoped to the
render instead of the process.

⇒ **rewrite the refusal's message and comments when it is lifted, do not just delete them.** A future reader
who sees only "the stated reason was measured false" would conclude there was nothing there.

#### What A's lifetime machinery would be, concretely

1. **A replay store**, built by draining the adopted stream once:
   `struct ReplayableArrowInput { ArrowSchema schema{}; vector<ArrowArray> arrays; };`
2. **A per-scan cursor.** `FabricatorArrowStreamProduce` builds a wrapper whose stream carries
   `private_data = new Cursor{store, 0}`; `get_next` copies `arrays[i++]` out with a **no-op release** so the
   consumer cannot free the store's array; `release` deletes the cursor only. ⚠ This is upstream's own
   pattern — `duckdb_arrow_array_scan` does exactly it with `EmptyArrayRelease` — but only for ONE scan, so
   whether a re-handed array survives REPEATED scans is the first thing to measure.
3. **The owner is `HostConnection`, using the reverse-declaration-order trick already in this file:**
   `vector<unique_ptr<ReplayableArrowInput>> bound;` declared BEFORE `conn`, so the Connection (and its temp
   view) is destroyed before the store it points into.
4. **One ABI entry** — `host_connection_bind(connection, name, stream, err)` (drain, store, create the temp
   view) — plus a managed `IHostConnection.Bind`.

⚠ Also unmeasured: whether a PARALLEL scan needs per-thread cursors. `arrow_scan` parallelises
(`ArrowScanMaxThreads` returns `NumberOfThreads()`), but the wrapper is produced once per scan and
`GetNextChunk` runs under the parallel state's lock, so one cursor per scan looks right — looks, not is.

#### ⚠ B is far less machinery but is NOT free of the same hazard

After `CREATE TEMP TABLE t AS SELECT * FROM <bound view>`, the bound view ALSO outlives the result stream on
a pin — so B must DROP it in the same call. A rule rather than a structure, but forgetting it recreates the
crash inside a render, which is precisely the failure this whole section exists to have found once.

**⚠⚠ WHICHEVER IS BUILT, IT MUST BE OPT-IN PER BINDING AND MUST NOT CHANGE THE EXISTING PATH.** Two reasons,
and the second is the one that bites:

1. Every current caller depends on **streaming, bounded memory** — a Delta scan binding a deletion vector,
   a bulk write binding its source. Retaining batches to make them replayable would defeat that.
2. Replayability would quietly dissolve the premise of two correctness-bearing mechanisms:
   `HostBatchFilter`'s `WITH … AS MATERIALIZED` and `SingleScanArrowStream`, which THROWS on a second
   end-of-stream *because for a DV anti-join zero rows means deleted rows coming back*. A change that makes
   those guards look unnecessary is exactly the kind that gets them deleted later.

---

## 18. ✅ AS BUILT (2026-09-04) — `publish(name)`: handing a table the template STAGED to the SQL it generated

> **WHAT SHIPPED**, and the spelling is the user's own choice from the analysis below —
> `SELECT * FROM {{ publish('_result') }}`. **C#-only: no ABI change, no C++ change, and no new SQL
> function** (it renders a call to the `fabricator_scan` that already existed). Gate
> `verify_plugin_fluid` **344 → 386**, hermetic floor 8620 → **8662**, THREE mutants each killed at its
> own assertion.
>
> ⚠⚠ **IT IS LAZY: the relation STREAMS at scan time, under no row cap, and the scan's own disposal
> releases it** (user-directed). A first build BUFFERED; **§18.9 is what shipped, and it CORRECTS §18.8's
> four reasons for buffering — one of which was plainly wrong, with a measurement that did not
> discriminate.** Read §18.9 first; §18.4, §18.5 and §18.8 are kept because they are what it corrects.

### 18.0 The original ask (kept, because the shape it was reaching for is what got built)


**User-asked 2026-09-04, with the design questions raised in the same breath** — *"as the first arg is a
pointer we would need to adjust our parameter types or use a string handle which points to a gc pinned fluid
session. then there s question about owndership/lifetime management and not leaking memory."* The shape:

```sql
SELECT * FROM fluid_query('
{% exec arg1: 1, arg2: 5 %}
create temporary table _result as
SELECT i AS n, i * i AS sq FROM range($arg1, $arg2) t(i)
{% endexec %}
select * from fluid_table(this, ''_result'')
');
```

### 18.1 ⚠⚠ THE HANDLE QUESTION IS A NON-ISSUE, AND MEASURING IT FIRST IS WHAT SHRINKS THE FEATURE

The transport the sketch reaches for **already exists, already takes a STRING, and already binds from inside
`fluid_query`'s generated SQL.** Measured 2026-09-04:

```sql
SELECT count(*) FROM fluid_query('SELECT * FROM fabricator_scan(''fabricator_demo_numbers'')');
--> 3                     a named-source scan inside GENERATED SQL binds and runs

SELECT * FROM fabricator_scan('fabricator_demo_lazy');   --> prior_invocations = 0
SELECT * FROM fabricator_scan('fabricator_demo_lazy');   --> prior_invocations = 1
```

That leading **0** is the whole property: `Host.RegisterSource(name, factory, schema)` answers the BIND from
the declaration, so the factory runs **exactly once, at the scan** — never at `EXPLAIN`, never on a re-bind
that is not executed. ⇒ **no new ABI entry, no new parameter type, no GC pinning, no `LogicalType::POINTER`.**
A registry key IS the handle.

⚠ **A POINTER argument would be wrong even if it were convenient.** `duckdb_arrow_scan` uses
`{LogicalType::POINTER}` but builds its relation PROGRAMMATICALLY with `Value::POINTER(…)`; a template emits
SQL **TEXT**, so the argument must be writable as a literal — and a raw address in SQL text is copyable,
editable and re-runnable by anyone, i.e. the use-after-free class §17.6 was written about, reachable from
ordinary SQL. The token must be opaque and unguessable, not an address.

⚠ **There is also a replacement scan** (`NamedSourceReplacement`, `fabricator_host_query.cpp`): an
unresolved BARE name matching a registered source is rewritten to `fabricator_scan('<name>')`. So
`select * from _result` — the sketch without any function call — would resolve. **Do not reach for that
spelling:** §17.1 measured the registry to be process-static, so two concurrent renders staging `_result`
collide on the name. The token is what makes it per-render.

⇒ **the better spelling needs no new SQL function at all** — one Fluid function returning the scan text:

```
{% exec %}CREATE TEMP TABLE _result AS …{% endexec %}
SELECT * FROM {{ publish('_result') }}
```

`publish` registers a lazily-opened named source over this render's pin and renders
`fabricator_scan('<token>')`. Reusing a measured-working mechanism beats adding a parallel one.

### 18.2 ⚠⚠ THE ONE FACT THAT DECIDES IT: THE SESSION IS ALREADY DEAD BY THE TIME THE SCAN RUNS

`FluidEngine.Render` holds the session in a `using var`, and `fluid_query` renders inside `GenerateSql`,
i.e. in `bind_replace`. So:

| # | event | session |
|---|---|---|
| 1 | binder calls `GenerateSql` → render → `{% exec %}` creates the temp table on the pin | alive |
| 2 | `Render` returns → **`using` disposes → pin CLOSED → temp catalog destroyed** | **gone** |
| 3 | DuckDB parses + binds the generated SQL; `fabricator_scan`'s bind runs | gone |
| 4 | DuckDB executes; the scan pulls → the factory needs the pin | gone |

⇒ **the entire feature is the lifetime rework.** Nothing else in it is unbuilt.

### 18.3 Why it MUST be a table function, and not a name in the caller's catalog

The tempting alternative — stage a REAL table and let the generated SQL name it — is measured
**fragile, not merely unsupported**: see §11.1b-i. An ATTACHed catalog the outer transaction has not yet
touched really does see the `{% exec %}`'s CREATE (`a = 42`), and one preceding read of that same catalog
makes the identical statement fail. A marshaled scan reads through its OWN connection and asks the caller's
catalog nothing, so `MetaTransaction`'s snapshot rule cannot reach it. That is the argument FOR the design,
and it is the only one that survives measurement.

### 18.4 ⚠ SUPERSEDED BY §18.8 — the ownership design a LAZY publication would have needed

Two owners, because one is not enough:

* **A refcount held by each `fabricator_scan` bind data** — prompt release on the happy path.
* **A backstop with a bounded, longer lifetime**, because if binding the generated SQL FAILS (a typo, an
  absent column) no bind data is ever constructed and nothing would release the session. A
  `ClientContextState` on the CALLER's connection is the natural home — v82 hands the global function its
  caller's `ClientContext`, and v69's scoped settings already use that destructor as the connection-close
  signal. This is the `InOutSessionHolder` pattern: an RAII backstop on every teardown path.

⚠⚠ **AND THE RECORDED OBJECTIONS TO A LONGER-LIVED SESSION DO NOT APPLY IF THE CHANGE IS SCOPED TO
`fluid_query`.** `FluidRenderSession`'s remarks give two, and both are about `fluid_render`:

1. *the session is captured at OPEN, so a connection outliving its unit of work hands every later user the
   FIRST one's session* — measured as a WRONG VALUE (a render under `Asia/Kolkata` reporting the first
   render's zone). Scoping the session to ONE BIND of ONE `fluid_query` call keeps the capture correct: it is
   captured at that bind, for that bind's statement, and shared with nobody.
2. *thread safety, since a volatile scalar may be evaluated on several threads* — `fluid_render` is that
   scalar; `GenerateSql` is called once per bind, single-threaded.

⇒ **extend the lifetime for `fluid_query` only; leave `fluid_render` per-render.** That is what turns a
correctness-bearing rework into a contained one.

### 18.5 ⚠ PARTLY SUPERSEDED BY §18.8 — three costs, and buffering removed the first two

1. **⚠⚠ ONE LIVE STREAM PER PIN.** §12 measured that a second statement on a pinned connection with a live
   result stream makes the first stream report end-of-stream — a SILENT short read — which is why the host
   REFUSES it. Two `fluid_table`/`publish` references in one generated SQL are exactly that shape, scanned
   in an order nobody controls. So either **one pin per published table**, or accept the refusal. It fails
   loudly rather than truncating, which is the tolerable half; it is still the sharpest constraint in the
   design and must be settled deliberately, not discovered.
2. **The rows ROUND-TRIP** DuckDB(pin) → Arrow → managed → Arrow → DuckDB(caller), for data already sitting
   in DuckDB's own memory in the same process. This tree's own number for that boundary is ~3x against a
   native read (0.203 s vs 0.592 s on a 6M-row aggregate), so it is a real cost on a large `_result`.
3. **Binds REPEAT.** §11 measured a view over a writing `fluid_query` writing on EVERY use (1 → 2 → 3 → 4).
   Each bind would open its own pin, re-run the DDL and register its own token — self-consistent, but N
   concurrent pinned DuckDB connections in the worst case.

### 18.6 ⚠ What already covers most of the example, measured — so the feature is narrower than it looks

**The sketch's own body is a single SELECT, and a MATERIALIZED CTE does it better** — no session, no handle,
no round trip, full pushdown, one statement (measured 2026-09-04):

```sql
SELECT count(*), sum(sq) FROM fluid_query('
{% assign lo = 1 %}{% assign hi = 5 %}
WITH _result AS MATERIALIZED (
  SELECT i AS n, i * i AS sq FROM range({{ lo }}, {{ hi }}) t(i)
)
SELECT * FROM _result');
--> 4, 30
```

And for a SMALL staged result there is now a text channel that did not exist a day ago: `{% query r %}` reads
the staged table through the pin at render time, and **`{% print sql_literal: true %}`** (§13.9) renders its
rows as a SQL `VALUES` list in one block.

⇒ **`publish` earns its keep for a genuinely MULTI-STEP staged computation.** That case is real — it is the
whole reason to stage — but it is not the case the sketch shows, and saying so is what keeps the feature from
being built to serve an example a CTE already answers. ⚠ §18.9 narrows this in one direction and widens it in
another: the shipped design is LAZY, so SIZE is no longer the discriminator (there is no row cap), while a
single statement can scan only ONE publication per template.

### 18.7 The pre-build recommendation (kept — it held, and §18.8 is what it became)

Buildable, and cheaper than it first appears: **no ABI change, no C++ change, no new SQL function** — one
Fluid `publish()` function, a token registry entry, and the §18.4 lifetime rework in the plugin. The
prerequisites are (a) deciding §18.5 item 1 (one pin per published table, or the refusal), and (b) a gate
that pins the release path, since a leaked pinned connection is invisible to every row assertion — the same
gap `FluidRenderSession`'s surviving `Dispose` mutant already documents.

### 18.8 ⚠ SUPERSEDED BY §18.9 — the FIRST build, which buffered (kept: its reasons are what §18.9 corrects)

The analysis proposed *"a lazily-opened named source over this render's pin"*. What shipped reads the rows
**at publish time**, on the pinned connection, buffers them, and registers a replay-free in-memory source.
Four reasons, and the first is structural rather than a preference:

1. **⚠⚠ A LAZY PUBLICATION WOULD HOLD THE PIN'S ONE LIVE RESULT STREAM.** §12 measured that a second
   statement on a pinned connection with a live stream makes the first report end-of-stream, which is why
   the host refuses it — so a lazy publication would poison every later `query()` and `exec()` in the same
   render, and two publications would collide with each other. Buffering opens, drains and disposes inside
   the call. **MEASURED: a `{% query %}` AND an `{% exec %}` after a publish both still work**, which is
   the assertion that pins it (§23's multi-step row).
2. **A DuckDB connection is single-threaded by contract**, and a lazy factory runs at SCAN time on whatever
   worker pulls it — possibly two at once for two publications. A buffered publication touches no
   connection at scan time at all, so the question does not arise.
3. **It would need the pin to outlive the render**, i.e. a refcount inside `IHostConnection` and a change to
   its disposal contract — against a class whose per-render scoping this repo documents as
   correctness-bearing (the `Asia/Kolkata` session-capture measurement).
4. **`Host.RegisterSource` is in `Fabricator.Bridge`, which a plugin does not reference** (§17.4), so a host
   service was needed either way. Buffering makes that service a single call — `IHostConnection.Publish(sql)`
   → a token — instead of a lifetime protocol.

**The cost is memory, and it is stated rather than hidden**: a host-side cap of 1,000,000 rows that ERRORS
rather than truncating, naming the CTE as the cheaper route. ⇒ **§18.6 stands unchanged and now cuts
harder**: for a single query a `WITH … AS MATERIALIZED` CTE is better on every axis, and `publish` is for a
relation computed in SEVERAL steps.

#### What it is made of

| piece | where |
|---|---|
| `IHostConnection.Publish(string sql)` → token | `Fabricator.Abstractions`, **default-implemented** (the v84 precedent — a published contract gained a member without breaking a plugin) |
| buffer + token registry + eviction | `Fabricator.Bridge/PublishedSources.cs` |
| `publish(name)` → `fabricator_scan('<token>')` | `Fabricator.FluidPlugin/FluidHostPublish.cs` |

#### ⚠⚠ THE DECLARED SCHEMA IS MANDATORY, NOT AN OPTIMISATION — and that is the design's keystone

`Host.RegisterSource`'s **schema overload** is what makes a bind answer from the declaration instead of
opening a stream to learn the columns. Since a publication is SINGLE-USE, a bind that opened one would
CONSUME it and the scan would then find it already taken. **Mutant A — drop the declared schema — dies at
the FIRST §23 assertion after exactly 344 pass**, i.e. at the boundary where the section begins. The
registry's own instruments say the same thing from the other side: `fabricator_demo_lazy` reports
`prior_invocations = 0` on its first scan where `fabricator_demo_eager` reports 1.

#### ⚠ SINGLE-USE, failing LOUDLY — the silent-short-read class avoided by construction

One publication is handed to exactly one stream; the entry keeps only a marker, so the buffer is released
deterministically by the scan that consumes it. A second scan says so and names the fix. MEASURED — one
token, two references:

```
publish: a publication can be scanned ONCE and this one has been scanned already. Call publish() again
for a second reference — each call is an independent publication.
```

⚠ Its POSITIVE CONTROL is load-bearing: `{{ publish('t') }} x JOIN {{ publish('t') }} y` — two
publications — **works** (2 rows). Without it the refusal would be equally true of a build where `publish`
had stopped working altogether. **Mutant B (replayable: do not take the batches) dies at the single-use
assertion after 370 pass.**

#### ⚠⚠ THE EVICTION CAP IS A ROUTINE PATH, NOT AN ERROR PATH — and its first version named the token but not the cause

Nothing in managed code can observe *"the caller's statement finished"*, and a bind that is never executed
publishes and never scans — **an `EXPLAIN` of a generated statement is exactly that**. So an unscanned
publication is reclaimed when it becomes the oldest of 32. **Mutant C (never evict) dies at the eviction
assertion after 372 pass.**

⚠⚠ **The first build unregistered the name on eviction, and MEASURING the path is what showed that was
wrong**: the factory is then never reached, so the scan failed with the registry's generic
*"no named source registered as '__fabpub_…'"* — which names the token and not the cause. Evicted tokens now
stay REGISTERED as tombstones (a bounded second ring; a tombstone holds a schema and a closure and no rows),
so the recent ones answer properly:

```
publish: the publication '__fabpub_…' was reclaimed — more than 32 publications have been made since,
and an unscanned publication is only held that long. Publish it in the statement that scans it, rather
than keeping a token across statements.
```

#### ⚠⚠ THE MEASUREMENT THAT JUSTIFIES THE FEATURE OVER `{% print sql_literal %}`: TYPES SURVIVE

The rows never become SQL text, so nothing is collapsed and nothing is refused. MEASURED and gated:

| staged column | through `publish` | through `{% print sql_literal %}` |
|---|---|---|
| `DATE '2023-01-02'` | `DATE` | `'2023-01-02 00:00:00.000000+00:00'::TIMESTAMPTZ` — the instant survives, the TYPE does not |
| `[1,2,3]` | `INTEGER[]` | **refused by name** |
| `{'a': 7}` | `STRUCT(a INTEGER)` | **refused by name** |

⚠ Gated as a CONTRAST — the `sql_literal` refusal is asserted immediately below the `publish` row — so the
choice between the two surfaces is a pinned fact rather than folklore.

#### Smaller decisions, each with its reason

* **ONE identifier, quoted** through `DuckSql.QuoteIdent`, so `publish('pub odd')` works (gated) and
  injection is not expressible. ⚠ A DOTTED name is one identifier, not a qualified one, and fails as
  "table does not exist". A general `publish`-a-query surface is the follow-on.
* **The token is an opaque `__fabpub_<32 hex>`**, never an address — §18.1's reason, unchanged: it goes into
  SQL text, where anything copyable and re-runnable becomes the use-after-free class §17 exists to have
  closed.
* **Registered on BOTH surfaces**, and inert on one: in `fluid_render` the rendered scan is text nobody
  binds, and being a per-row scalar it would publish per row. Not refused — branching on the caller's name
  is what the `exec()` decision rejected (§11.1) — but documented.
* **A missing table fails naming BOTH the table and the SELECT we built** (`SELECT * FROM "x"`), which is
  what makes the quoting visible when a name is wrong.

#### ⚠ Two traps paid for while building it

1. **`dotnet build dotnet/Fabricator.FluidPlugin` DOES NOT COMPILE THE BRIDGE**, because the plugin
   references `Fabricator.Abstractions` and `Fabricator.Common` and deliberately not the Bridge (§2). A
   missing `using Apache.Arrow.Ipc;` in `PublishedSources.cs` therefore reported **"Build succeeded"** and
   failed at `publish-managed.ps1` instead. Build `Fabricator.Bridge` too, or publish, before believing a
   green plugin build.
2. **`EXPLAIN` CANNOT BE A SUBQUERY SOURCE** — a recorded trap walked into anyway. The gate's `EXPLAIN`
   assertion uses the `<REGEX>:` form on the `physical_plan` row, which is both required and stronger: it
   asserts that `publish`'s rendered scan reached the PLAN.

#### Still open, deliberately

* **A per-statement release** would make the eviction cap unnecessary. It needs the C++ `fabricator_scan`
  bind data's destructor to report through the ABI — an ABI change, hence not in this one.
* **A `publish`-a-query form** (`publish_query('SELECT …')`) would cover qualified and computed sources.
  Declined for now: the pairing with `{% exec %}` is the intended one, and one surface is easier to keep
  honest than two.
* **The row cap is UNGATED** — reaching it needs a million staged rows, which no hermetic suite should
  build. Asserted by reading, not by running.

### 18.9 ⚠⚠ IT IS LAZY, AND §18.8's FOUR REASONS FOR BUFFERING WERE ONE AND A HALF (user-directed, 2026-09-04)

The user, reading §18.8: *"it is nice but i actually would have prefered a lazy approach without buffering
and automatic release of resources after scan."* They were right, and the correction matters more than the
rebuild — **my lead argument was simply wrong, and the measurement I offered for it did not discriminate.**

#### What each reason was worth

| §18.8's reason | verdict |
|---|---|
| 1. a lazy publication would hold the pin's ONE live stream and *"poison every later `query()`/`exec()` in the same render"* | **WRONG.** A lazy publication opens NOTHING at publish time; the stream opens at SCAN time, by which point the render is over and there are no later `query()`/`exec()` calls to poison. |
| 2. a DuckDB connection is single-threaded, and a lazy factory runs on a worker | **collapses into the same case as 1.** MEASURED: a 500,000-row publication at `threads=8` invokes the factory exactly ONCE. |
| 3. the pin must outlive the render | **REAL, and it is the whole job** — but much smaller than priced, see below. |
| 4. `Host.RegisterSource` is Bridge-only | **NEUTRAL.** True, and true of either design; the service was needed either way. |

**⚠⚠ AND THE MEASUREMENT I CITED FOR REASON 1 WAS VACUOUS.** §18.8 offers *"MEASURED that buffering removes
the hazard: a `{% query %}` AND an `{% exec %}` after a publish both still work"*. That passes on the LAZY
build too (re-measured), because a lazy publish leaves no live stream either — its schema probe is disposed
before it returns. **A measurement that both designs satisfy is not evidence for one of them**, and
presenting it as such is the error this file keeps recording in other forms.

#### ⚠⚠ Reason 3 was much cheaper than priced, because the refcount ALREADY EXISTS — in C++, from v84

`Host.HostConnection.Dispose`'s own remark: *"safe with result streams still outstanding: each holds its own
reference to the underlying connection, so it dies with the last of them rather than under a live stream."*
⇒ **once `Query` has RETURNED, nothing managed needs to keep the connection alive** — the stream does, and
the temporary catalog holding the staged table lives exactly as long as the stream does. So the only thing
that must survive the render is the HANDLE, so a scan can still ISSUE its query.

That makes the managed side a plain reference count on `PinnedHostConnection`: the render holds one
(given back by `Dispose`, idempotently), each unscanned publication holds one more, and a publication gives
its reference back **the moment `Query` returns** — not when the stream ends. `Open` is ~15 lines and there
is **no wrapper stream at all**.

- **MUTANT D — the publication takes no reference — dies at the FIRST §23 assertion after exactly 344 pass**,
  with `Cannot access a disposed object. Object name: 'HostConnection'`: the pin closes at end of render and
  the scan cannot issue its query. That is the mechanism, named by its own failure.
- ⚠ `Dispose` must be IDEMPOTENT (`_renderReleased`), because `FluidRenderSession` disposes from a `using`
  and an over-decrement would close the connection under a publication that has not been scanned.

#### What the user gets, measured

* **NO ROW CAP.** `MaxRows` is gone. MEASURED: **3,000,000 rows** through one publication, checksum exact
  (`4499998500000`) — where the buffered design refused above 1,000,000. Gated at 1.2M, which runs in ~0.5 s.
* **RELEASE BY THE SCAN.** The rows are the stream's; disposing it releases everything, and the token is
  claimed at open so nothing is held afterwards.

#### ⚠⚠ WHAT IT COSTS, AND IT IS A REAL CAPABILITY THE BUFFERED BUILD HAD

**Two publications from ONE template, scanned in one statement, FAIL.** Both stream from the render's single
pinned connection, and a pinned connection tolerates only one live result stream. MEASURED, and the message
depends on the PLAN:

| shape | outcome |
|---|---|
| `{{ publish('t') }} x JOIN {{ publish('t') }} y` | our v84 refusal — *"this pinned host connection still has an open result stream"* |
| `SELECT * FROM {{ publish('u') }} UNION ALL SELECT * FROM {{ publish('u') }}` | DuckDB's — *"Attempting to execute an unsuccessful or closed pending query result"* |

⚠ **Neither is silent**, which is what makes the trade acceptable — and the gate's expected text is an
ALTERNATION over both, because which one wins is DuckDB's plan choice and not ours.

**TWO WORKAROUNDS, both measured, and the second is better than what it replaces:**

1. **Two separate renders are two separate pins** — two `fluid_query` calls in one statement each publish
   and scan happily (gated, joins to 2).
2. **Do the join inside `{% exec %}` and publish ONE relation** (gated, joins to 2). This keeps the work in
   DuckDB rather than shipping two relations out through Arrow and back, so it is the answer to reach for.

⚠ **A serializing lock was considered and REJECTED**: the measured JOIN opens both streams before draining
either, so blocking the second would deadlock whenever the executor needs it before the first — and this
repo's standing rule is that a hang is worse than an error. **A hybrid (lazy for the first publication on a
pin, buffered for the rest) is buildable and was declined**: it makes the memory characteristics depend on
the ORDER of `publish()` calls, which is the "runs and means something different" shape this file keeps
warning about. One code path with one set of properties is easier to keep honest.

#### The other cost, restated because it inverted

An unscanned publication now holds a **DuckDB connection and its staged table** open until the eviction cap
reclaims it, where the buffered design held **rows in managed memory** under a row cap. Same cap, different
resource. `EXPLAIN` is the routine path that produces one.

### 18.10 ⚠⚠ THE BOUNDARY, NOT THE PUSHDOWN, WAS THE COST — and it took an ABI parameter to fix (2026-09-04)

**User-measured**: their billion-row publication took ~20 s where the same relation left in the generated
SQL took 2.15 s, *and dropping to one column barely helped* — which correctly rejected my first
explanation (the missing projection pushdown). Decomposed at 100M rows, threads=8:

| | time | vs no boundary |
|---|---|---|
| (a) relation left in the generated SQL — no boundary at all | **0.308 s** | — |
| (d) same rows via `fabricator_host_query`, 1 column | 1.861 s | 6.0x |
| (c) publication, 1 column | 2.381 s | 7.7x |
| (b) publication, 2 columns | 3.906 s | 12.7x |

⇒ **projection was only the (c)→(b) gap; ~6x was the boundary itself.** (a) is fast because `fluid_query`'s
call DISAPPEARS at bind — the physical plan is `RANGE → PROJECTION(n) → UNGROUPED_AGGREGATE`, so `sq` is
pruned and `i*i` is never computed for a single row.

**THE CAUSE: the default was ONE DuckDB `DataChunk` per exported Arrow batch — 2048 rows.** A billion rows
therefore crossed as ~488,000 batches, each paying a mutex acquisition, an `ArrowAppender` copy, an import,
an export and converter setup, *because the exported batch IS the morsel of a parallel Arrow scan*. Fixed
by ABI **v85**, which lets the CALLER pick; a publication asks for a row group (122880), and
`fabricator_host_query.cpp`'s own comment had already recorded the win as deferred, with the reason.

**Result on the user's query: ~20 s → 7.89 s** (one column). ⚠ Their two-column form went 27 s → 20.3 s —
less, because with the per-batch overhead gone the un-pruned second column is now the dominant cost. So
§18.9's ordering of the two costs was right about their existence and wrong about their size.

⚠ **Why it could not just be the default is the sharpest part, and it was already measured**: a batch is
also a FILE to engineered-wood, and this same service feeds writers — a row-group default made
`verify_delta_clustered_optimize` collapse 80,000 rows into one file (147 passed at one chunk, 1 failed at
122880). A publication is the consumer for which a big batch is unambiguously safe, its stream being scanned
into the caller's DuckDB and never written. Full record: [abi-history.md](abi-history.md) §v85.

⚠ **Projection pushdown remains unfixed and is now the biggest remaining gap for a wide publication** — the
obstacle is the declared schema, which is the same mechanism that makes laziness work (§18.9). Filter
pushdown has no such obstacle.

## 19. ✅ AS BUILT (2026-09-05) — `fluid_query_batch`: a template rendered WITH A RELATION

`fluid_query_batch(template, <input> [, params := …] [, batchsize := …])`. Where `fluid_query` renders from
CONSTANTS at bind time and hands the result to DuckDB (`bind_replace`), this renders from DATA at execution
time and runs the result itself. **User-designed**, over two rounds: the first sketch was a LATERAL
`fluid_query_each(template, params, vararg …)`; the user re-cut it as an in-out taking a TABLE, then
corrected themselves again — *"whole table does not work, we have a special collector function for this"* —
which is exactly right and is what shipped.

C# in the plugin + two host members + one C++ marshal fix. **No ABI change.** Gate
`verify_plugin_fluid` **397 → 443**, hermetic floor 8673 → **8719**, three mutants each killed at its own
assertion.

### 19.1 Why a COLLECTOR, and why that was forced

The default — no `batchsize`, one render over the whole input — emits nothing until input EOF. The
streaming in-out operator **cannot express that**: its only all-input-done hook is the injected
`OperatorFinalize`, which is handed no `DataChunk`, so output held back until EOF is drained and discarded
([inout-collector-mode.md](inout-collector-mode.md)). A collector buffers its input and emits afterwards,
which is this shape exactly.

⚠ The price is inherent and is stated in the README: **the whole input is buffered before the first
render**, even with a small `batchsize`. So `batchsize` is about how many rows each RENDER sees, never
about memory. A bounded-memory batched variant is a SECOND registration of the same body on the streaming
in-out — the author surface is identical (`CollectorInOutBinding` adapts `ICollectorFunctionBinding` to
`IInOutFunctionBinding` with `DoExchange = Collect`), so the choice is reversible.

⚠ **`kind` is fixed at REGISTRATION**, so one function name cannot switch operators by parameter. That is
why `batchsize` had to live on the collector rather than selecting between the two.

### 19.2 ⚠⚠ THE MEASUREMENT THAT SHAPED IT: the user's own sketch HANGS

The sketch ended `select * from {{ publish('_result') }}`. MEASURED: **2 minutes, killed, 13.5 s CPU**,
while the identical template through `fluid_query` returns in seconds as the control.

The mechanism is re-entrancy. In `fluid_query` the caller's plan scans the publication on a DIFFERENT
connection; here **we** run the generated statement on the render's own pinned connection, so the
publication's factory opens a second query on the connection that is mid-query. It deadlocks rather than
raising the one-live-result refusal.

⇒ **`publish()` is refused by name in this surface**, and the refusal names the alternative. Nothing is
lost: a publication carries a staged relation ACROSS a connection boundary and here there is none —
`SELECT * FROM my_staged_table` just works, because the statement runs where the table lives.

⚠ The mechanism is carried on `FluidHostPublish.RefusalKey`, an ambient each surface sets for itself, and
`FluidEngine.Render`'s `publishRefusal` parameter is **REQUIRED with no default**. That is §11.1's rule
applied: a policy defaulting to "allowed" makes a surface added later inherit the permissive answer
silently, which is the same trap as branching on the caller's NAME.

⚠ A test that reproduced the hang would HANG the tier rather than fail it, so what §25 pins is the
REFUSAL, with the still-allowed `fluid_query` publish beside it as the positive control.

### 19.3 The input table needs NO ABI change — measured

`fabricator_scan` **resolves on a pinned connection** (measured: a `{% query %}` on the render's pin read
`fabricator_demo_numbers`' 3 rows). The named-source registry is process-global and the replacement scan
lives on the DatabaseInstance, so a batch can go managed → `Host.RegisterSource` →
`CREATE TEMP TABLE … AS SELECT * FROM fabricator_scan('<token>')` on the pin.

That sidesteps the obvious route rather than negotiating with it: named Arrow INPUTS are **refused** on a
pinned connection (`fabricator_host_query.cpp`), and lifting that refusal re-opens the lifetime hazard
[host-query.md](host-query.md) §17.6 documents. Two new default-implemented members on `IHostQuery`:

- `RegisterRows(RecordBatch)` → a token. ⚠ **BORROWED, not adopted** — nothing copies or disposes the
  batch, which is what makes it usable from a collector whose input chunks it does not own. Register, run
  the statement, release, in one synchronous stretch.
- `RegisterRows(Schema)` → a token for an EMPTY relation. It exists because the columns cannot be spelled
  in SQL from managed code: rendering `CREATE TABLE t(a VARCHAR, …)` means an Arrow→DuckDB type-name table
  by hand, the second type mapping this codebase keeps refusing to maintain.

⚠ The Bridge's stream is deliberately **not** `InMemoryArrayStream`, whose `Dispose` disposes the batches
it was given — handing it a borrowed batch would free the caller's Arrow buffers when the scan released
the stream.

### 19.4 `is_bind` selects what to RENDER; the schema comes from BINDING what was rendered

The user proposed `is_bind` as a way for the template to DECLARE its columns. What ships uses it to choose
what to render, and then takes the schema from `SELECT * FROM (<generated>) LIMIT 0` — the same probe
`Publish` uses, which both binds without scanning and requires the statement to be a SELECT usable as a
subquery.

⚠ **A declaration written twice is a declaration that drifts**, and the drift would be read as DATA (the
host builds its Arrow→DuckDB converters from the declared schema). So the columns are never taken on
trust. What `is_bind` genuinely buys is skipping expensive or side-effecting setup — binds REPEAT, once per
view use and once per prepared re-execution.

⚠ It is defined ONLY in this surface's renders. In `fluid_query` there is no second kind of render to tell
it apart from, so leaving it undefined (falsy) is the honest answer rather than picking a value.

⚠ Every group's arriving schema is verified against the declaration (count, names, type IDs — the same
three the host's own declared-source check compares, with the same limit: a change of type PARAMETERS
alone passes). NOT defensive: the template renders anew per group and may legitimately render different
SQL, so drift is reachable from an ordinary template — §25's row branches on its own row count.

### 19.5 Exact slicing, and why it needed a staging table

Every input row is copied into `__fab_input` with a `__fab_seq` row number as it arrives, and `input_table`
is a temp **VIEW** over a range of it. That buys three things:

- **exactness**: `batchsize := 2` over 5 rows gives 2, 2, 1 — not "at least 2, rounded up to an input
  chunk". Batch-aligned grouping would make `batchsize := 1` mean 2048.
- **no second copy**: a view, not a table, so the whole-input case does not duplicate the staged relation.
- **the rows leave managed memory immediately**, which the collector contract requires — it frees a
  chunk's Arrow buffers once consumed, so accumulating batches to form a group would be a use-after-free.
  DuckDB is also the better owner: it can spill.

⚠ `row_number() OVER ()` numbers each batch in scan order. WHICH row gets which number does not matter —
they only have to be unique and contiguous, so every row lands in exactly one group.

⚠ An input column named `__fab*` is refused at bind, by name.

⚠ **An empty input still renders ONCE** — the template is a statement generator and its output need not
depend on the rows. It is also the only case with no batch to build the staging table from, hence the
schema-only `RegisterRows`.

### 19.6 ⚠⚠ A CLAIM I WROTE DOWN AND THE PROBE FALSIFIED

The first version of this feature's doc comments said one shared `TemplateContext` makes a Liquid
`{% assign %}` carry from group to group. **It does not.** Fluid renders into a CHILD SCOPE and pops it,
so every group starts from the variables the context was built with. MEASURED in one run: a counter
assigned per group read **1, 1, 1** where a temp-table counter read **1, 2, 3**.

⇒ **SQL state carries; Liquid state does not.** The shared context is worth having for what it does do
(the params bag and the three host functions are bound once), and the sequential-state story the user
asked for — *"keeping state e.g. in temp tables"* — is the half that works. Both are now pinned in §25,
the Liquid one as a CHARACTERIZATION test of Fluid's scoping that no mutant of ours can kill.

⚠ The context is built per EXECUTION, not per binding: a binding is reused across prepared re-executions,
so a context or session built at bind would carry one execution's state into the next. That is also why
the params bag is CAPTURED (`FluidValueModel.Capture`) rather than retained — the args batch belongs to
the framework and its lifetime ends with the bind. Capture is safe because `ReadCell` is eager all the way
down, which was checked rather than assumed: `EagerStruct`, `ArrowMap`'s copying constructor, `ReadList`.

### 19.7 ⚠⚠ TWO PRE-EXISTING DEFECTS IT EXPOSED, both latent until now

**(a) An ANY-declared NAMED parameter was unusable on the in-out/collector path.**
`FabricatorMarshalInOutArgs`'s NAMED branch pushed the DECLARED type unconditionally while the POSITIONAL
branch two lines below already resolved the SQLNULL sentinel against the value that arrived. So `params`
failed in BOTH directions: supplied, *"Failed to cast value … -> NULL"*; omitted, an untyped NULL that
Apache.Arrow refuses (*"Length must equal null count"*, the v80-recorded hostility). Fixed with a shared
`FabricatorResolveAnyArg` applied to both branches. **Latent rather than shipped-broken:
`fluid_query_batch` is the first in-tree in-out or collector to declare one** (`fluid_query` is sqlgen, a
different marshal).

**(b) ⚠⚠ The in-out and collector BINDS established no ambients — a dangling `ClientContext *`.**
`FabricatorExchangeBind` and `FabricatorCollectorBind` never called `FabricatorSetActiveTxn`, while their
`InitGlobal`s did. So managed code running in the author's `Bind()` read whatever the LAST crossing left.
MEASURED as **`host_connection_open failed: vector too long`** — `CaptureSession` dereferencing a stale
pointer, NON-ZERO so the null guard waved it through, and reproducible only with an earlier statement in
the same session to leave one behind.

⚠ `FabricatorSetActiveTxn`'s own comment already named *"a global collector/in-out"* as the case it exists
for; the bind sites were simply missed. Latent until a binding did host work in `Bind` — which the schema
probe is.

⚠ The mutant for (b) kills reliably but at a VARYING line, which is the signature of the bug it guards
rather than a weak gate: a dangling pointer breaks wherever the garbage lands. Both observed kills were
inside §25 with the same message.

⚠ The exchange (streaming in-out) half of the fix is **UNGATED** — no in-tree in-out binding does host
work in `Bind`, so no statement reaches it. Said here rather than implying coverage.

### 19.8 ⚠ A CI FLAKE FOUND ON THE WAY, pre-existing, in §23

§23's two-publications-in-one-statement row pinned an alternation over TWO plan-dependent messages. A
THIRD is reachable: the SINGLE-USE refusal, i.e. one token scanned twice rather than two scanned at once.
MEASURED **~1 run in 6**, and attributed rather than guessed — it reproduces against the **unmodified**
suite file, while the same statement run ALONE gives "open result stream" 10 times out of 10, so it needs
preceding suite state and is not the new section's doing. The alternation is widened; all three ARE the
property under test (a loud refusal), and pinning which one arrives would be pinning DuckDB's plan choice.

⚠ **A separate harness trap, worth knowing for any local re-run:** `verify_plugin_fluid` is NOT
re-runnable against the same `FABRICATOR_DELTA_WRITE_DIR`. First run passes, every later one with the same
directory fails at the no-root include refusal. Invisible in CI because `run-suites.sh` gives each suite a
fresh scratch dir — and it VOIDED a mutation run here until a fresh-dir control separated it.

### 19.9 What is NOT built

- **The LATERAL form** (`FROM t, fluid_query_each(…, t.a, t.b)`) — parallel, and the host stamps the
  correlated columns. It needs two things this shape does not: a mandatory row id in every generated
  statement (`LateralResult.Origin` is required, and absent-with-a-different-length is a hard error), and
  a rule for naming input columns, since a lateral's wire columns are named by their rendered EXPRESSION
  TEXT (`t.a`, but also `(t.a + 1)` and `CAST(5 AS SMALLINT)`). A STRUCT argument would dissolve the
  naming half.
- **A bounded-memory batched variant** on the streaming in-out — same body, different registration.
- **The input rows as a Fluid VALUE** (`input`, via the `{% query %}` value model), which would let a
  template loop over rows without a round trip. Today `{% query rows %}SELECT … FROM input_table{% endquery %}`
  does it in one statement, which is why this was left out.

## 20. ✅ AS BUILT (2026-09-05) — the params bag is bound WHOLE, under the name `params`

User-asked: *"the fluid `params` parameter we assume a struct with named members or json so these member
names are used as templatecontext variable names. let us lift this … assign params to variable name
`params` so the params members will be accessed via `params.` or e.g. `params[0]`"*.

C#-only in the plugin, ONE function (`FluidValueModel.Capture`). Gate `verify_plugin_fluid` 443 → **455**,
hermetic floor 8719 → **8731**, two mutants each killed at its own assertion.

### 20.1 What the assumption cost, and it was worse than an ergonomic gap

The bag was readable ONLY through its members, so it had to HAVE members. Everything else fell off the walk:

| bag | before | after |
|---|---|---|
| `STRUCT`, `MAP`, JSON object | members spread | members spread **and** `params.x` / `params[0]` / `params.size` |
| JSON array `'[1,2]'` | **REFUSED** — *"params JSON must be an OBJECT"* | `params[0]`, `params.size`, `{% for %}` |
| DuckDB `LIST` `[10,20,30]` | **bound NOTHING, silently** — matched no case at all | as above |
| a scalar `41` | bound nothing, silently | `{{ params }}`, `{{ params | plus: 1 }}` |

⚠⚠ **The LIST row is the one that makes this a fix rather than sugar.** A JSON array at least said no; a
DuckDB LIST matched no `case` in the switch and bound nothing, so a template reading it rendered EMPTY with
no error anywhere — the silent-wrong-answer class. It is also the shape a caller reaches for most naturally
from SQL (`params := ['a','b']`), and `fluid_query` splices what it renders into a STATEMENT.

⚠⚠ **The member spread is GONE (§20.5)** — this section's table describes what the bag binding ADDED, and
the spread's removal is the second half of the same change. `{{ n }}` renders empty; `{{ params.n }}` is
the only spelling.

### 20.2 Ordinal access is not a second mechanism

`params[0]` works because Fluid resolves an index by asking `TryGetValue` for the KEY `"0"`, and
`EagerStruct` (what a STRUCT bag materialises into) already has the int-parse fallback that `ArrowStruct`
documents. So the bag binding buys ordinals for free wherever the underlying indexable has that rule.

⚠ `ArrowMap` deliberately does NOT have it, so `params[0]` on a MAP bag does not resolve — a MAP being
unordered in principle. Pinned in §26 as an asymmetry rather than papered over.

### 20.3 Precedence stopped being a question

While the spread survived, a member literally named `params` had to lose to the bag (appended last), with
the member still reachable as `params.params`. With the spread gone there is nothing to order: `params` is
the only variable the bag binds, and a member of any name is reachable only through it.

### 20.4 What did NOT change, deliberately

- **Invalid JSON is still an error.** A VARCHAR bag IS JSON, so binding unparseable text as a plain string
  would hide a typo in the caller's own JSON. Only the *object-only* half of the refusal was lifted.
- **A NULL bag binds nothing at all**, `params` included, so `{% if params %}` is how a template asks
  whether one was passed. Gated.
- **One walk, three surfaces.** `fluid_render`, `fluid_query` and `fluid_query_batch` all go through
  `Capture`, so the bag cannot mean different things depending on which function you called — the reason
  this file's header gives for having one value model at all. `fluid_query_batch` gets it through the same
  CAPTURE that makes its params outlive the bind, which is only safe because the walk is eager (§19.6).

### 20.5 ✅ DECIDED — the member spread is GONE (user, 2026-09-05), BREAKING, no alias

The request read two ways — bind the bag *as well*, or *instead*. The additive reading shipped first because
it was the literal ask and the reversible one; the user then chose **instead**: `{{ params.n }}` is the only
spelling and `{{ n }}` renders empty.

**What it buys.** One spelling, so a member can no longer shadow — or be shadowed by — anything else in the
template's namespace: an ambient like `is_bind`, an `{% assign %}`, or a variable a future version adds. It
also removes the "which of two spellings am I reading" question from every template.

⚠ **`is_bind` stays TOP LEVEL** (user preference, and it is what `fluid_query_batch` already did). It is an
ambient the host sets, not a member of anything, so it is not affected by the bag's shape — and with the
spread gone it can no longer be shadowed by a member of the same name. MEASURED before the change that the
ambient already won that collision and the member remained reachable as `params.is_bind`.

⚠⚠ **THE COST, MEASURED RATHER THAN ESTIMATED, and my first estimate was wrong by ~8x.** I wrote "~a dozen
gate rows"; `verify_plugin_fluid` in fact passes a params bag at **~98 call sites** (48 positional
struct/MAP literals, 29 `params := …`, 21 JSON-string bags) out of 278 `fluid_*` calls, plus 8 README
examples. **Correcting that number is what made the decision an informed one**, and it is why the estimate
is recorded here rather than quietly replaced.

**How the rewrite was done, because "mechanical" needed proving.** A member-DRIVEN script, not an
identifier-driven one: for each sqllogictest block, the member names come from that block's OWN bag
literals, and only those names are rewritten. That is what keeps loop variables out of it — `{% for c in
cols %}{{ c }}` rewrites `cols` and leaves `c`, because `c` is not a member. Two passes (the second for
bags written `{v: …}` rather than `{'v': …}`) covered 98 regions; the suite then found the rest, and each
was a case the script could not have known:

- five CROSS-BLOCK sites, where the bag arrives as a prepared parameter (`params := ?`) or belongs to the
  *including* render, so the template's own block contains no bag literal at all;
- one FALSE POSITIVE — `{{ d | date: "%Y-%m-%d" }}` became `%Y-%m-%params.d`, because the lookbehind
  guarded `.` and word characters but not `%`.

⇒ **the suite was the oracle, and it had to be**: a rewrite this size cannot be eyeballed, and every
failure it produced named its own line.

### 20.6 Gate

Assertion count UNCHANGED at 455 across the removal, which is the honest outcome: templates were rewritten,
not assertions added — and it is also why the removal needed an assertion of its OWN. §26's first row is
now `bare=[{{ n }}] dot={{ params.n }}` expecting `bare=[] dot=7`. **Without it nothing in the suite would
notice the spread coming back**, since every other row passes just as happily with it.

Mutants, each dying at its own assertion: restoring the STRUCT spread dies at that first row after 442
pass; never binding the bag at all dies there too; keeping the old named-members-only walk dies at the
DuckDB LIST row after 445 — the silent case, which is the one worth pinning precisely.

⚠⚠ **`.size` and `params[0]` alone would NOT have caught a broken build.** A `JsonNode` bound with no
converter renders correctly while comparing and computing as nothing (this file's header), so the
JSON-array row asserts a `{% for %}` **SUM** and the scalar row asserts `| plus: 1`. Arithmetic is the only
thing that separates a real value from one that merely renders like one.

⚠ The row that used to pin *"params JSON must be an OBJECT"* is REPLACED, not deleted — that refusal WAS
the assumption being lifted, so falsifying it is the change announcing itself, and a note at the old site
points at the row that replaces it.
