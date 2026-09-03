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
A LIST/STRUCT/MAP is **refused by name** — DuckDB has no parameter form for them here, and rendering one into
the SQL is precisely what parameters exist to avoid.

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

The function form is not superseded: it RETURNS the affected-row count, and the block deliberately does
not (it renders nothing, which is the whole point). Use the function when you want the number.

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
the filter form uses. The int64→decimal ladder, the UTC stamp on dates and the refusal of LIST/STRUCT/MAP
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
