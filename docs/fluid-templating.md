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

### 1.1 `fluid_replacement_query` — a global **sqlgen** table function

> ⚠ Named `fluid_query` when this was written; renamed 2026-09-13 (§41), and the
> historical sections were swept with it because the old name is REUSED for a different
> function (§42). See §41.2 for why that inverts this file's usual convention.

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

**A sqlgen function renders at BIND.** So a Fluid `query` function used inside `fluid_replacement_query` would run
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
  `fluid_replacement_query` (bind time).
- If it is not ⇒ **`query` is available at execute time and REFUSED at bind time**, which is a real
  asymmetry the surface has to state rather than hide.

## 4. The slice order (agreed 2026-09-01)

| slice | contents | why here |
|---|---|---|
| **1** ✅ | `fluid_replacement_query` + **the value model**: the `ValueConverters` mapping, and the Arrow→`FluidValue` wrapping (`ArrowStruct : IFluidIndexable`, name AND index access, nested list → enumerable). BUILT — see §7 | Slices 3 and 5 both reuse the value model, so it was built ONCE here. Needed no new ABI and no new seam, exactly as §2 predicted |
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

## 7. Slice 1 AS BUILT (2026-09-01) — `fluid_replacement_query` + the shared value model

> ⚠ Written as `fluid_query`; renamed 2026-09-13 — see §41.2.

C#-only, NO ABI change, NO C++ change, no bridge change: `IBackend.GlobalSqlTableFunctions` already existed,
which is the §2 finding paying out immediately. Three new files in the plugin
(`FluidValueModel.cs`, `FluidEngine.cs`, `FluidReplacementQueryFunction.cs`); `FluidPlugin.cs` keeps only the two
`IBackend` members and the render function. Gate `verify_plugin_fluid` **23 → 89**, seven mutants each killed
at its own assertion.

```sql
SELECT * FROM fluid_replacement_query('SELECT {{ n }} AS n', params := {'n': 7});
```

`template` is positional and NON-nullable; **`params` is NAMED and optional** (the `fabricator_sql_seq(2,
cols := 3)` precedent), so a template with no variables is simply `fluid_replacement_query('SELECT 1')`. It takes the
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
justifies the file. For `fluid_replacement_query` a wrong `{% if %}` branch is a wrong SQL STATEMENT, not a formatting
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
this slice: `fluid_replacement_query` would splice that wrong date into a statement. Fixed by stamping the Kind
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

MEASURED via `EXPLAIN`: `SELECT id FROM fluid_replacement_query('SELECT * FROM fq_t WHERE g = {{ g }}', params := {'g': 3})`
plans as a bare `SEQ_SCAN` on `fq_t` with `Projections: id` and `Filters: g=3`. The call is GONE and both
pushdowns reached the base table — the property that separates sqlgen from a marshaled table function, and
one no row assertion can see. ⚠ `EXPLAIN` cannot be a subquery source, so the gate uses the sqllogictest
`<REGEX>:` form on the `physical_plan` row.

A VIEW over `fluid_replacement_query` re-binds on every use, which is why `GenerateSql` must stay deterministic and
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
- **`fluid_replacement_query` is service-tier only**, because the hermetic tier's plugin root is empty by design — so a
  hermetic run says nothing about any of this.

### 7.8 What slice 1 leaves for the rest

- The value model is DONE and is what slices 3 and 5 were told to reuse. `ArrowStruct` already wraps a row;
  slice 3 adds only the list-of-rows and the transport.
- §3's bind-time hazard is UNCHANGED and still unmeasured — nothing in slice 1 executes SQL, it only
  generates text. Slice 2 is still the next thing.
- ⚠ `fluid_replacement_query` gives §3 a sharper edge than the plan anticipated: a Fluid `query` inside `fluid_replacement_query`
  would execute SQL inside `bind_replace`, i.e. during the binder's own walk, not merely "during bind".

## 8. Slice 2 — the bind-time `host_query` PROBE: MEASURED SAFE (2026-09-01)

**§3's hazard is CLOSED, and the answer is the permissive one.** Bind-time `host_query` neither deadlocks nor
crashes, and — the part §3 did not anticipate — its transaction semantics are the SAME at bind and at
execute, so there is no asymmetry for the surface to state. `query` can exist in BOTH `fluid_render`
(execute time) and `fluid_replacement_query` (bind time). **Slice 3 is unblocked in its simple form.**

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
SELECT * FROM fluid_replacement_query('SELECT {% assign rs = query("SELECT nm FROM cols ORDER BY ord") %}
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
SELECT * FROM fluid_replacement_query('{% include ''dims/customer'' %}', params := {'region': 'eu'});
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
function has no ambient opener established.** Both `fluid_render` (a global scalar) and `fluid_replacement_query`
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
3. **Then the right probe found it in one statement.** `fluid_replacement_query` — whose sqlgen `bind_replace` runs on
   the BINDER's thread, the same thread that later evaluates the scalar — leaks where a table function's
   scan (a worker thread) does not:

   | between `SET` and `fluid_render('{% include … %}')` | result |
   |---|---|
   | nothing | **fails** — "no root is set" |
   | `SELECT * FROM fluid_replacement_query('SELECT 1 AS x')` | **renders** |

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
- ⚠ A per-call `template_root` argument was considered and not built. It is clean for `fluid_replacement_query` (a named
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

### 11.1 ⚠⚠ IT IS AVAILABLE ON BOTH SURFACES (user decision) — AND IN `fluid_replacement_query` A WRITE MULTIPLIES

**User, 2026-09-02: *"no problem to have a exec() in render or query."*** The first build refused `exec()` in
`fluid_replacement_query` behind a fail-closed opt-in; that mechanism is DELETED. What replaces it is not silence — the
gate now PINS the cost as asserted behaviour, which is a stronger record than a refusal plus prose.

A `fluid_replacement_query` template renders inside `bind_replace`, and a bind REPEATS and happens WITHOUT execution.
**MEASURED, one counter through four steps that execute nothing the caller wrote:**

| step | rows written |
|---|---|
| `EXPLAIN SELECT * FROM fluid_replacement_query('… {{ exec("INSERT …") }} …')` | **1** |
| merely `CREATE VIEW v AS SELECT * FROM fluid_replacement_query(…)` | **2** |
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
caller's NAME: an unrecognised name reads as "not `fluid_replacement_query`" and would be ALLOWED, so a surface added
later would default to the dangerous answer.

### 11.1b ⚠⚠ A STATEMENT CANNOT SEE THE WRITE ITS OWN TEMPLATE MADE — so "prepare then select" DOES NOT WORK

**Found by asking what `exec()` in `fluid_replacement_query` is actually FOR, once it was permitted — i.e. by trying the
pattern a user would try first, rather than only testing the hazard.** MEASURED 2026-09-02:

```sql
CREATE TABLE ex_prep(c INTEGER); INSERT INTO ex_prep VALUES (1);

SELECT c FROM fluid_replacement_query('{% assign _ = exec("UPDATE ex_prep SET c = 42") %}SELECT c FROM ex_prep');
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
SELECT * FROM fluid_replacement_query('{% assign _ = exec("CREATE OR REPLACE TABLE t AS SELECT 1 AS c") %}SELECT c FROM t');
--> Catalog Error: Table with name t does not exist!  Did you mean "memory.t"?
```

⚠ **The table DOES exist afterwards** (asserted in the gate) — so the error is a VISIBILITY result, not a
failed CREATE, and the "Did you mean" hint naming the very table it says is absent is the tell. Pinned
rather than described, precisely because the message points away from the cause.

⚠ **What works is a SEPARATE statement**: `exec()` in one, the read in the next. Gated, so the workaround is
not folklore.

⇒ **so what is `exec()` in `fluid_replacement_query` good for?** Side effects the statement does not itself read —
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
SELECT * FROM fluid_replacement_query('
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
SELECT * FROM fluid_replacement_query('{% exec %}CREATE OR REPLACE TABLE scratch.st2 …{% endexec %}
                           SELECT * FROM scratch.st2');
--> Catalog Error: Table with name st2 does not exist!
-- CONTROL: the identical transaction WITHOUT that first read --> a = 8
```

⚠⚠ **So it is a TIMING artefact, not a supported route, and it must not be recommended or gated as a
feature.** It breaks on anything that touches the staging catalog earlier in the same transaction — a
preceding statement, a second `fluid_replacement_query` reading the same scratch catalog, a view whose body references
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

-- so a fluid_replacement_query template reaches a write through query(), AT BIND TIME
SELECT * FROM fluid_replacement_query(
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
  `fluid_replacement_query` can call `fabricator_exec` directly.
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

Gated: the write and its count; **the four-step bind-time multiplication in `fluid_replacement_query`** (each step
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
fluid_replacement_query is for side effects the statement does not itself read"* — therefore still holds for the
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
   for `fluid_replacement_query`, one bind.
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

Same rule as `fluid_replacement_query`, for the same reason: a template must be able to emit object names and whole
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

User-asked. The function is contributed by the Fluid provider and its sibling was already `fluid_replacement_query`, so
the `fluid_` prefix is the one that describes it; `fabricator_*` is the core/host namespace
(`fabricator_query`, `fabricator_exec`, `fabricator_host_query`, `fabricator_plugins`). Per this repo's
standing convention for renames — the `fabricator` rename, `IArrow*`, `ITable`, `IProvider` — **no alias is
kept**: the old spelling now answers *"Scalar Function with name fabricator_render does not exist!"*.

⚠ **THE CODE CHANGE IS ONE LINE.** `FluidRenderFunction.Name`; everything else in the plugin was doc
comments. The bulk of the work was the 133 occurrences in `verify_plugin_fluid.test` and 20 in the README.

⚠ **It silently changed an ORDER BY, which is the one thing a mechanical rename can break.**
`verify_plugin_fluid`'s registration check does
`… WHERE function_name IN ('fluid_render','fluid_replacement_query') GROUP BY 1 ORDER BY 1` — and
`fabricator_render` sorted BEFORE `fluid_replacement_query` while `fluid_render` sorts AFTER it, so the expected rows
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
2. **Bind repetition on the sqlgen surface.** A `fluid_replacement_query` template renders per BIND, so the
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
SELECT * FROM fluid_replacement_query('
{% exec arg1: 1, arg2: 5 %}
create temporary table _result as
SELECT i AS n, i * i AS sq FROM range($arg1, $arg2) t(i)
{% endexec %}
select * from fluid_table(this, ''_result'')
');
```

### 18.1 ⚠⚠ THE HANDLE QUESTION IS A NON-ISSUE, AND MEASURING IT FIRST IS WHAT SHRINKS THE FEATURE

The transport the sketch reaches for **already exists, already takes a STRING, and already binds from inside
`fluid_replacement_query`'s generated SQL.** Measured 2026-09-04:

```sql
SELECT count(*) FROM fluid_replacement_query('SELECT * FROM fabricator_scan(''fabricator_demo_numbers'')');
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

`FluidEngine.Render` holds the session in a `using var`, and `fluid_replacement_query` renders inside `GenerateSql`,
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
`fluid_replacement_query`.** `FluidRenderSession`'s remarks give two, and both are about `fluid_render`:

1. *the session is captured at OPEN, so a connection outliving its unit of work hands every later user the
   FIRST one's session* — measured as a WRONG VALUE (a render under `Asia/Kolkata` reporting the first
   render's zone). Scoping the session to ONE BIND of ONE `fluid_replacement_query` call keeps the capture correct: it is
   captured at that bind, for that bind's statement, and shared with nobody.
2. *thread safety, since a volatile scalar may be evaluated on several threads* — `fluid_render` is that
   scalar; `GenerateSql` is called once per bind, single-threaded.

⇒ **extend the lifetime for `fluid_replacement_query` only; leave `fluid_render` per-render.** That is what turns a
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
3. **Binds REPEAT.** §11 measured a view over a writing `fluid_replacement_query` writing on EVERY use (1 → 2 → 3 → 4).
   Each bind would open its own pin, re-run the DDL and register its own token — self-consistent, but N
   concurrent pinned DuckDB connections in the worst case.

### 18.6 ⚠ What already covers most of the example, measured — so the feature is narrower than it looks

**The sketch's own body is a single SELECT, and a MATERIALIZED CTE does it better** — no session, no handle,
no round trip, full pushdown, one statement (measured 2026-09-04):

```sql
SELECT count(*), sum(sq) FROM fluid_replacement_query('
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

1. **Two separate renders are two separate pins** — two `fluid_replacement_query` calls in one statement each publish
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

⇒ **projection was only the (c)→(b) gap; ~6x was the boundary itself.** (a) is fast because `fluid_replacement_query`'s
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

`fluid_query_batch(template, <input> [, params := …] [, batchsize := …])`. Where `fluid_replacement_query` renders from
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
while the identical template through `fluid_replacement_query` returns in seconds as the control.

The mechanism is re-entrancy. In `fluid_replacement_query` the caller's plan scans the publication on a DIFFERENT
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
REFUSAL, with the still-allowed `fluid_replacement_query` publish beside it as the positive control.

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

⚠ It is defined ONLY in this surface's renders. In `fluid_replacement_query` there is no second kind of render to tell
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
`fluid_query_batch` is the first in-tree in-out or collector to declare one** (`fluid_replacement_query` is sqlgen, a
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

- ~~**The LATERAL form**~~ — **BUILT the same day, §22.** Its one genuine obstacle was the one named here
  (a mandatory row id in every generated statement, because `LateralResult.Origin` is required and
  absent-with-a-different-length is a hard error), and it survived contact unchanged.
  ⚠ The naming half did NOT. This entry said a caller wanting clean names "passes a struct and addresses
  its fields in the template" — MEASURED, that struct's column is named
  `main.struct_pack(a := t.id, b := t.n)` and has to be quoted verbatim, which is worse than the answer
  §22.3 found: a positional column alias (`FROM (SELECT * FROM input_table) AS q(r, a, b)`), which is
  ordinary SQL and needs no knowledge of what anything rendered as.
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
from SQL (`params := ['a','b']`), and `fluid_replacement_query` splices what it renders into a STATEMENT.

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
- **One walk, three surfaces.** `fluid_render`, `fluid_replacement_query` and `fluid_query_batch` all go through
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

## 21. ✅ AS BUILT (2026-09-05) — parentheses enabled on the parser

User-asked. `FluidParserOptions.AllowParentheses = true`, set beside `AllowFunctions` in
`FluidEngine.CreateParser`. One line; gate `verify_plugin_fluid` 455 → **459**, hermetic floor 8731 →
**8735**, one mutant.

⚠ It is a PARSER option, not a `TemplateContext` one, so it must be set where the parser is built —
templates are cached by TEXT, and one parsed before the option was set would stay cached, rejected, for the
process's life. Fluid names the option in its own parse error, which is how the need surfaces.

⚠⚠ **Why it is worth having rather than a convenience: LIQUID HAS NO OPERATOR PRECEDENCE.** It evaluates
strictly RIGHT TO LEFT, so `a or b and c` is `a or (b and c)`. MEASURED on one bag (a true, b false, c
false): ungrouped answers **yes**, `(a or b) and c` answers **no**. The grouped condition is therefore
INEXPRESSIBLE without this, not merely clumsy — and a template that generates SQL is exactly where a mixed
and/or condition turns up. §27 pins the pair, with the ungrouped row as the control: the grouped `no`
alone would be equally true of a build where the condition had stopped evaluating.

⚠ Both parser options are now on at once and a function call is itself parenthesised, so §27 also pins that
`query(...)` still parses — enabling grouping did not disturb the call syntax it depends on.

## 22. ✅ AS BUILT (2026-09-05) — `fluid_query_lateral`: the CORRELATED sibling

```sql
SELECT t.id, f.*
FROM   orders t,
       fluid_query_lateral(
         'SELECT __fab_row, upper(name) AS u FROM input_table',
         'null', t.name) f;
```

User-designed (the original sketch was `fluid_query_each`, "template, params, vararg lateral1, vararg
lateral2 .."), with the signature settled by their later hint: *"laterals currently don't support named
args, so params must be positional"*. C#-only in the plugin — **NO ABI change, NO C++ change**, because
`Params.Constant` and the lateral variadic tail were both already built. Gate `verify_plugin_fluid`
459 → **563**, hermetic floor 8735 → **8839**, two mutants each killed at its own assertion — and the
second of them (§22.8) is the most useful thing in this section.

### 22.1 What it is, against `fluid_query_batch`

Both render a template with DATA in hand and run the result. The difference is the operator:

| | `fluid_query_batch` (collector) | `fluid_query_lateral` |
|---|---|---|
| input | a TABLE argument | ordinary per-row COLUMNS |
| composition | a standalone relation | correlated — the caller's own columns are stamped on |
| parallelism | sequential, one session | **PARALLEL**, one session per pipeline thread |
| cross-chunk state | temp tables carry between groups | **nothing carries** |
| memory | the whole input is buffered before the first render | one chunk at a time |
| provenance | none needed | **mandatory** (§22.2) |

⇒ reach for the lateral when the generated statement is *per row* and the result belongs beside the outer
row; reach for the collector when the template needs the whole relation, or needs to accumulate.

### 22.2 ⚠⚠ PROVENANCE IS THE CONTRACT, and it is the whole cost

One call sees up to 2048 input rows and may return any number of output rows, so `LateralResult.Origin` —
"which INPUT row did this OUTPUT row come from" — is what lets the host stamp the correlated columns.
Absent-with-a-different-length is a hard error by design, so it cannot be inferred.

So `input_table` carries **`__fab_row`** as its first column and **the generated statement must project
it**. It is stripped from the result. Three consequences:

- `SELECT * FROM input_table` is the identity template — the id rides along and disappears again.
- 1→N is "repeat the id", 1→0 is "omit the row". Both are gated in one result, because only the PAIRING
  shows them: a row count would be equally true of a build that had lost the correlation.
- Omitting it is refused **at BIND**, naming the column and the fix. Not at execute, and never silently:
  a mis-attributed row is a wrong answer with nothing failing.

⚠ A value outside `[0, chunk)` is refused too, with the value in the message. The host checks this as well
and precisely (`ReadOriginColumn`); the managed check exists for the MESSAGE, since only this side knows
the number came from a projected `__fab_row`. The mistake it catches is computing the id instead of
carrying it through.

### 22.3 ⚠⚠ The input column NAMES, measured — and the answer is better than the design assumed

DuckDB synthesises the input relation from the argument EXPRESSIONS, so a column is named by its rendered
text. MEASURED:

| argument | column name |
|---|---|
| `t.n` | `n` |
| `t.n + 1` | `(t.n + 1)` |
| `upper('x')` | `upper('x')` |
| `{'a': t.id, 'b': t.n}` | `main.struct_pack(a := t.id, b := t.n)` |
| a LITERAL call — no expression text | `col<SLOT>` |

⚠⚠ **`col<SLOT>` is the ARGUMENT position, not the input position.** With the two constants ahead of it,
the first input column of `f('tpl', 'null', 7, 'x')` is **`col2`**, not `col0`. Defensible (it names the
argument), and surprising enough to gate — the two naming regimes are pinned as a PAIR, since either alone
says nothing about the other.

⚠ **The naming-free spelling is a positional column alias, and it is ordinary SQL:**

```sql
SELECT r AS __fab_row, a * b AS s FROM (SELECT * FROM input_table) AS q(r, a, b)
```

The columns are positional — row id first, then the arguments in call order — so this needs no knowledge of
what anything rendered as and is identical in both call shapes. Note the id is re-aliased BACK to
`__fab_row`: the requirement is on the OUTPUT column's name, not the input's.

⇒ this **supersedes the struct advice in §19.9**. Passing one struct does give the template a single
value, but its rendered name (`main.struct_pack(a := t.id, b := t.n)`) has to be quoted verbatim to address
it, which is worse than the alias in every respect. The struct is still the right shape when you want the
fields *together*; the alias is what makes either shape addressable.

### 22.4 ⚠⚠ `params` CANNOT be `NULL` — the no-bag spelling is `'null'`

Both cost arguments are `Params.Constant`, forced: a NAMED argument is unusable in the correlated shape
(measured — DuckDB drops the name and matches positionally, so `f(t.n, params := …)` is a Binder Error
while the literal call works). A constant occupies a positional slot and is recovered in both shapes.

But a constant that arrives NULL is REFUSED by the host, and rightly: in the correlated shape the value is
recovered from the synthesized column's rendered expression text, where an explicit `NULL` is
indistinguishable from a fold that FAILED (a column, a volatile) — which is the one thing that refusal
exists to catch. So:

| written | result |
|---|---|
| `NULL` | **refused** — *"is a bind-time CONSTANT, and this argument has no bind-time value"* |
| `'null'` | binds; the bag is NIL, so `{% if params %}` is FALSE |
| `'{}'` | binds; the bag is an empty object, so `{% if params %}` is TRUE |
| `{'mul': 10}` | binds — a STRUCT constant survives the correlated shape |

All four are gated, and the last three are the control: the refusal alone would be equally true of a build
where constants had stopped arriving at all.

### 22.5 ⚠⚠ A shipped defect the first run found: the result's ARRAYS were freed under the caller

The first build read the drained batches, built the output over `parts[0]`'s columns, and disposed every
part in a `finally`. That is a use-after-free, and it faulted where such things fault rather than where
they are written: **`0xC0000005` inside `Apache.Arrow.C.CArrowArrayExporter.ReleaseArray`**, two frames
deep, with nothing pointing at the plugin.

The fix is an ownership rule, stated at the seam:

- **several parts** ⇒ every output column is freshly allocated by `ArrowArrayConcatenator`, so all the
  parts are released;
- **one part** ⇒ the output columns ARE that part's, so the batch is handed on undisposed and only the
  provenance column — the one array not handed on — is released.

⚠ Disposing a single column is not a trick: `RecordBatch.Dispose` is *defined* as disposing its columns,
so this is that operation performed selectively, and every array is still disposed exactly once. ⚠ And it
is a `catch`, not a `finally`: only the failure path may free blindly.

⚠ The gate for it is the **6000-row checksum** over a fan-out that exceeds one Arrow batch — the branch
that would otherwise truncate silently. A small-result test exercises only the single-part branch.

### 22.6 What is NOT gated, and why

- **"No state carries between chunks."** The operator is parallel, so which rows reach which session is the
  scheduler's business; asserting it would be flaky in one direction and vacuous in the other. It is a
  property of the design — one `FluidRenderSession`, one DuckDB connection and one temporary catalog per
  session — and it is documented on the class instead.
  ⚠ What IS measured is that running several sessions at once is CORRECT, which is the half that could
  break: every session issues `CREATE OR REPLACE TEMP TABLE input_table` under the same name, so a shared
  catalog would have them overwriting each other. **50,000 distinct correlated values at `threads = 8`:
  50,000 rows out, `sum` exactly 2,499,950,000.** Not in the suite — a row count that large is a poor gate
  and the property it proves is structural — but worth having taken once.
- **The `publish()` HANG the refusal replaces.** A test reproducing it would hang the tier. The refusal is
  pinned; the hang is the same one §19.2 measured for `fluid_query_batch`, from the same cause (the
  generated statement runs on the render's own pin).

### 22.7 Still open — and TWO of the three it first listed were not open at all

- ~~A `batchsize`-like control~~ — **NOT WANTED (user, 2026-09-05).** A lateral's chunk is DuckDB's, up to
  2048 rows. Do not build it.
- ~~Projection pushdown~~ — **BUILT the next day, §24** (user-directed). The correction below stands as the
  record of why it was not an open item in the form this entry described; what changed is the decision, once
  the cost was re-priced against the two optimizer facts §24.1 records.** It said "the declared output is fixed at bind … the same obstacle `publish` has". Neither half
  holds:
  - **DuckDB DOES support it for this shape** (user-questioned, then read at the pin). It is one flag,
    `TableFunction::projection_pushdown`, and `RemoveUnusedColumns` gates on exactly that
    (`remove_unused_columns.cpp:720`) — for ANY `LOGICAL_GET`, a lateral's included: the visitor has a
    branch that recurses into a get's child with the comment *"Some LOGICAL_GET operators (e.g., table in
    out functions) may have a child operator"*.
  - **The real reason it is off is OURS and was recorded in the C++ months ago** — the header of
    `fabricator_lateral.cpp` says so: advertising it narrows the get, which would require capturing the
    callee-original column indices at rewrite time and threading them through as the wire projection,
    *"where an off-by-one reads a callee column into a correlated column's slot: wrong data, no error."*
    DuckDB projects above the operator instead, which costs a projection and cannot be wrong.
  - ⚠ And there is a second cost the note does not mention: `LateralIsEligible` BAILS OUT when
    `projection_ids` is non-empty (`fabricator_lateral.cpp:757`), so turning the flag on today would
    silently drop every projected lateral onto the ROW-BY-ROW path — losing the batching the whole feature
    exists for. Enabling it is therefore two changes, not one.
  - ⚠ It has nothing to do with `publish`'s obstacle (§18), which is that a lazily projected stream
    delivers fewer columns than its DECLARED schema and trips the mismatch check. Different mechanism,
    different surface; the two were conflated here and should not be again.
- **The input rows as a Fluid VALUE**, as for the collector (§19.9): `{% query %}` over `input_table` does
  it in one statement today. **This one is genuinely open.**

### 22.8 ⚠⚠ THE MUTANT THAT SHOWED THE PROVENANCE ROWS WERE VACUOUS

Two mutants, both killed — but the second one only after the section had been rewritten around what it
found, and that is the part worth carrying.

| mutant | dies |
|---|---|
| **A** — `sawOrigin` starts `true`, i.e. never refuse a generated statement that omits `__fab_row` | at the "must project `__fab_row`" refusal, after 529 pass |
| **C** — `origin[i] = 0`, i.e. ignore the projected `__fab_row` entirely | at the multi-row fan-out, after 484 pass |

**C's FIRST run died at the LAST assertion in the section, after 510 of 511 passed** — a kill, and a
worthless one. Every provenance row above it passed with the mapping thrown away.

**The reason is that a lateral call with ONE input column is delivered ONE OUTER ROW AT A TIME.** With a
chunk of one row, the only valid provenance index is 0, so a constant 0 is *correct*. MEASURED with a
template that reports its own chunk size (`(SELECT count(*) FROM input_table)`):

| call shape | chunk sizes over 3 rows |
|---|---|
| one input column (`f(…, t.n)`) | 1, 1, 1 |
| one input column, no outer column projected | 1, 1, 1 |
| **two input columns (`f(…, t.n, t.id)`)** | **1, 2, 2** |

⇒ every provenance assertion now runs on a two-column call, over six rows, with **`max(chunk) > 1` as the
control** — because without it the section would be asserting a property it never reaches. ⚠ Only `> 1` is
pinned, never the sizes: the grouping is DuckDB's scheduling, and asserting it would be a test of the
scheduler.

⚠ The sharpest row is a template that REORDERS its own output (`ORDER BY n DESC`), so within a chunk the
output order deliberately differs from the input order. A mapping that took position in the OUTPUT — the
plausible wrong implementation, and one a constant-0 mutant does not model — pairs the wrong outer row with
the wrong value there and nowhere else.

⚠ The one-input-column rows are KEPT, labelled as the row-by-row path: both paths exist and both should
work. What changed is that the section no longer *claims* they test provenance.

**The transferable rule: a gate over a batched interface is only as good as the batch it actually gets.**
Nothing in the SQL says how many rows reach one call, so "this obviously exercises the mapping" was an
assumption — and the mutant is what turned it into a measurement.

⚠ A third mutant was not re-run because it was already observed during the build: reverting §22.5's
ownership rule (dispose every part in a `finally`) does not fail an assertion, it takes the process down
with `0xC0000005` inside Arrow's release callback. A crash is a kill; it is just not one that names itself.

## 23. ✅ AS BUILT (2026-09-05) — `{% ret %}`: end the render here, keep what was written

```liquid
{% if params.dry_run %}SELECT 0 AS rows{% ret %}{% endif %}
INSERT INTO … SELECT … FROM …
```

User-asked, after the earlier measurement that Fluid has no equivalent of Scriban's `ret`. C#-only in the
plugin — **NO ABI change, NO C++ change, NO bridge change**. Gate `verify_plugin_fluid` 563 → **599**,
hermetic floor 8839 → **8875**, three mutants.

### 23.1 ⚠⚠ Why it had to be an exception, and the two candidates that fail

**A completion cannot work.** Liquid's `Completion` has three values — `Normal | Break | Continue` — and
`FluidTemplate.RenderAsync` (the ROOT) awaits each statement's completion and **never inspects it**. Read at
the pin and MEASURED:

| template | renders |
|---|---|
| `A{% break %}B` | **AB** |
| `A{% if go %}{% break %}{% endif %}B` | **AB** |
| `A{% for i in (1..3) %}{{ i }}{% break %}{% endfor %}B` | A1B |

Only a `{% for %}` consumes a Break at all. So a completion-based `{% ret %}` would be silently ignored at
the top level — precisely where it is wanted. §29 pins the `ret`/`break` pair side by side; the `break` row
is a CHARACTERIZATION test of Fluid that no mutant of ours can kill, and it is there because it is the whole
reason the tag exists.

**An output wrapper that discards writes after the tag was rejected on the merits.** It produces the same
TEXT while every statement after the tag still RUNS — an `{% exec %}` or a `{% query %}` below a `{% ret %}`
would still hit the database, and a `{% for %}` would still spin. "Stop" has to mean stop, and §29 pins it:
a render whose `{% exec %}` sits after the tag leaves the audit table at **0**.

⇒ `{% ret %}` throws a private `FluidEarlyReturn`, and `RenderOn` catches it.

⚠ Nothing in Fluid swallows it on the way out: the pinned source has exactly two `catch (Exception)` sites,
both in `FilterExpression`, and both re-fault the task rather than absorbing it — and a tag is a statement,
so it is never evaluated inside a filter anyway.

### 23.2 It forced us to own the output, and that brought the child scope with it

Fluid's `Render(ctx)` extension builds the `StringWriter` and the `TextWriterFluidOutput` INSIDE itself, so
a `FluidEarlyReturn` caught around it would leave the text where nothing can reach it. `RenderOn` therefore
renders into its own output — which means reproducing what that extension does, not merely calling it.

⚠⚠ **The child scope is the part that would have been easy to drop.** `EnterChildScope`/`ReleaseScope` is
what makes a `{% assign %}` NOT survive a render, which §19 measured (three groups, a per-group counter
reading 1, 1, 1) and §25 pins. **Mutant G — remove both — dies after 423 assertions, inside §25**, i.e. at a
PRE-EXISTING assertion rather than one written for this change. That is the stronger kill: the property was
already correctness-bearing, and this change would have quietly broken it.

### 23.3 ⚠⚠ The flush, and a correction to the design note

The note this was built from said the tag "must flush before throwing, because the catch site cannot".
**FALSE at this pin, and for a reason this repo has already recorded once**: `TextWriterFluidOutput
.DisposeAsync` flushes, which is exactly what let an earlier `{% exec %}` flush-mutant SURVIVE. The tag
flushes nothing; the catch site does.

⚠ And the flush is narrower than "the output buffers": `FluidTemplate.RenderAsync` ends with a flush of its
own, so an ordinary render needs nothing from us — **the exception is what skips it**. MEASURED, mutant D
(drop the flush) passes **563** assertions and dies at the FIRST `{% ret %}` one, which is what shows the
flush serves that path and only that path.

### 23.4 ⚠⚠ THE ONE DIVERGENCE FROM SCRIBAN: `ret` inside an include ends the WHOLE render

Scriban's `ret` "exits a top-level/include page", i.e. the include only. Ours cannot. MEASURED, with the
no-`ret` control beside it so the row reads as a truncation rather than a broken include:

| template | renders |
|---|---|
| `before-{% include 'stop' %}-after` where the partial is `P{% ret %}Q` | **`before-P`** |
| the same with a partial of `PQ` | `before-PQ\n-after` |
| `{% render 'stop' %}` — scope-ISOLATED in standard Liquid | **`before-P`**, identically |

**Why it cannot be scoped to the include:** Fluid renders an include as a NESTED `FluidTemplate`, whose root
discards completions in exactly the same way as the outer one. Catching the sentinel at the include boundary
would mean re-implementing `{% include %}` and its whole grammar (`with`, `for`, named parameters) — a large
surface to buy a semantic our templates have no functions to need.

⚠ It is also arguably the better reading HERE: an include in this plugin is a FRAGMENT of the statement being
built, not a function call, so "stop the whole statement" is coherent. But it is a divergence from the thing
it emulates, so it is pinned rather than described.

### 23.5 The scope stack stays balanced when the exception unwinds

⚠ An unwinding exception passes through `{% for %}`, `{% include %}` and `{% render %}`, all of which enter a
child scope. Read at the pin: all three release it in a `finally`, so nothing leaks. MEASURED where it would
actually bite — a SHARED context across renders (`fluid_query_batch`, three groups, each unwinding out of a
loop): the per-render counter reads **1, 1, 1**. Had a scope leaked, `RenderOn`'s own `ReleaseScope` would
have released the include's in its place and Liquid state would have started carrying between groups.

### 23.6 Where it works

Every surface, because they all go through `RenderOn`: `fluid_render`, `fluid_replacement_query` (where it TRUNCATES the
generated statement — `'SELECT 7 AS v{% ret %} WHERE 1=0'` runs `SELECT 7 AS v`), `fluid_query_batch` and
`fluid_query_lateral`. Inside an `{% exec %}` or `{% query %}` body it discards the body, which is the rule a
`{% break %}` in those blocks already followed: half a statement is a different statement.

⚠ `{% ret %}` takes no arguments — our templates have no functions, so a return VALUE would mean nothing —
and Fluid says so at PARSE (*"Unexpected arguments in ret tag"*).

### 23.7 ⚠⚠ A SEPARATE, PRE-EXISTING FINDING IT SURFACED: a SESSION-scoped `fluid_template_root` does not
reach every surface

Not caused by `{% ret %}` and not fixed here, but measured precisely while gating it. With the root set by a
plain `SET` (session scope) and a relative `{% include %}`:

| surface | resolves? |
|---|---|
| `fluid_render` | ✓ |
| `fluid_replacement_query` | ✓ |
| `fluid_query_batch` — at BIND | ✗ *"no root is set"* |
| `fluid_query_batch` — at SCAN | ✓ |
| `fluid_query_lateral` — at CALL | ✗ |

⚠ The bind/scan SPLIT is what makes it diagnosable rather than mysterious, and it took a three-way A/B to
see: with BOTH layers set the collector renders the SESSION value (so its scan has the ambient), while with
the session layer alone its BIND fails (so its bind reads the global layer and finds nothing). The
discriminating probe is an `{% if is_bind %}` branch that avoids the include at bind — under a session-only
root that renders correctly, which no other explanation predicts.

**Workaround, and it is complete: `SET GLOBAL fluid_template_root = …`** — measured working on every surface.

⚠ This is the same class as the slice-4 finding (§10) that a plain `SET` was invisible to `fluid_render`,
which ABI v82 fixed for scalar crossings. The in-out/collector BIND and the lateral CALL both call
`FabricatorSetActiveTxn`, which sets the settings session as well as the opener — so the mechanism is there
and something about these two crossings defeats it. Chasing it means touching the ambient plumbing, which
the v80 record warns is delicate (a bare `set_active_opener` without a restore was a SIGSEGV), so it is its
own change.

### 23.8 Mutants

| mutant | dies |
|---|---|
| **D** — no flush at the catch site | at the FIRST `{% ret %}` assertion, after 563 pass — and nowhere else, because Fluid's own root flushes the normal path |
| **F** — return `Completion.Break` instead of throwing | at the same first assertion, after 563 pass |
| **G** — no `EnterChildScope`/`ReleaseScope` | after 423, at a PRE-EXISTING `fluid_query_batch` assertion |

## 24. ✅ AS BUILT (2026-09-06) — PROJECTION PUSHDOWN through a lateral (ABI v86)

User-directed after §22.7's correction: *"build 1+2+(a). couldn't we just include a fluid `projected` as well
and the template is free to use it or not?"* — and that question is what made (b) safe to include, see §24.4.
C++ + ABI + C#. Gate `verify_plugin_fluid` 599 → **677**, hermetic floor 8875 → **8953**, one mutant.

### 24.1 The shape of the problem: the projection arrives AFTER bind

DuckDB decides it in `RemoveUnusedColumns`, which gates on `TableFunction::projection_pushdown`
(`remove_unused_columns.cpp:720`) and applies to any `LOGICAL_GET` — a lateral's included, since the visitor
recurses into a get's child with the comment *"Some LOGICAL_GET operators (e.g., table in out functions) may
have a child operator"*. Two facts read at the pin decided the design:

- **`UNUSED_COLUMNS` runs at `optimizer.cpp:222`, BEFORE optimizer extensions at `:331`** — so our own
  `RewriteLateralNodes` already sees the narrowed `column_ids` and no new plan pass is needed.
- **`GetAnyColumn()` falls through to `return 0`** for a get with no virtual columns, so the all-pruned case
  (`SELECT count(*) FROM t, f(…)`) hands us column 0 rather than an empty list or a rowid sentinel. There is
  no zero-column edge case to guard.

The only crossing between bind and execution is `lateral_open`, so that is where it rides.

### 24.2 ⚠⚠ It is a HINT, and that is what let it ship without touching one existing lateral

`lateral_open(binding, projected, count, …)`. A callee may honour it — returning exactly those columns, in
that order — or ignore it and return its full declared schema. **The host discriminates by COLUMN COUNT and
validates types either way**, in the wire check that already existed as the trust boundary
(`LateralSession::Call`). Neither shape can be silently read as the other, so:

- `ILateralFunctionBinding.Open(IReadOnlyList<int>? projected)` is a **default implementation** calling
  `Open()`. Nothing already written changes, in-tree or out.
- The alternative — making it a demand — would have meant a signature change plus real narrowing logic in
  seven in-tree demos and three out-of-tree plugins, to buy nothing they need.

### 24.3 ⚠⚠ THE WIRE MAP IS THE WHOLE HAZARD, and the mutant showed which row tests it

`wire_map_[k]` says which WIRE column carries OUTPUT column k. When the callee ignored the hint the wire is
WIDER than the output chunk, and referencing wire column `c` into output slot `c` lands a callee column in a
correlated column's slot — plausibly the same type, wrong data, no error. It is built ONCE per call, beside
the type check that validates it, rather than recomputed at each of the three emit sites.

**MEASURED, and it inverts which assertions matter:** mutant H (emit by position instead of through the map)
passes **630** assertions — including every `fluid_query_lateral` projection row — and dies at the
`fabricator_lat_span` row. Because *`fluid_query_lateral` honours the hint*, its wire map is the IDENTITY,
so its own rows cannot catch the off-by-one at all. **The row that tests the map is the one where the callee
IGNORES the hint**, and without a demo that does so this feature would have shipped with a vacuous gate.

⚠ Both operators map independently — the batched rewrite and DuckDB's own row-by-row driver — so §30 runs
its rows through the `fabricator_batched_lateral` kill switch as well.

### 24.4 (a) The wrapper narrows, and the payoff is the INNER statement

`Wrap()` now emits `SELECT <projected idents>, CAST(__fab_row AS BIGINT) AS __fab_row FROM (<generated>)`
instead of `SELECT * EXCLUDE (__fab_row), …`. That is the whole of (a), and it works because **DuckDB prunes
an unreferenced expression inside a subquery** — MEASURED before building anything:
`SELECT a FROM (SELECT i AS a, error('NOT PRUNED') AS b FROM range(3))` returns three rows, and referencing
`b` raises. So the TEMPLATE'S OWN statement stops computing what nobody reads; the bytes saved on the wire
are the lesser half. Gated as a pair: an unread `error()` column does not fire, reading it does.

### 24.5 (b) `projected` as a Fluid value — and why the user's question corrected my objection

I had argued (§22.7) that exposing the projection to the template would *invert the schema contract*: a
template branching on it renders fewer columns than the probe declared, and `Verify` would refuse. **That is
true of (b) WITHOUT (a), and false with it.** The wrapper normalises the shape whatever the template did:

- template ignores `projected`, renders everything ⇒ the wrapper drops the rest. ✓
- template uses it, renders exactly the projected set ⇒ the wrapper selects what is there. ✓
- template renders FEWER than projected ⇒ DuckDB's binder fails at the inner statement, naming the column. ✓

So it is genuinely optional, which is what the user asked for. `projected` is bound as the column NAMES, in
output order. ⚠ It is NOT bound during the schema probe — there is no projection yet, the bind is what the
planner narrows — so a template reading it branches on `is_bind`, as §30 does.

⚠ What it buys over (a) is work SQL pruning cannot reach: an `{% exec %}`, a `{% query %}`, a join or an
include the template would otherwise write.

### 24.6 ⚠⚠ A behaviour change it forced, and the gate is stronger for it

Selecting BY NAME instead of `* EXCLUDE` changed what a schema DRIFT does. Measured, all four:

| a chunk that renders… | before | now |
|---|---|---|
| an EXTRA column | refused | **dropped** |
| a MISSING column | refused | refused — DuckDB's binder, naming it |
| a RENAMED column | refused | refused — DuckDB's binder, naming it |
| a RETYPED column | refused | refused — our `Verify`, naming the type |

Only the harmless case relaxed, and it is harmless BY CONSTRUCTION: a column absent from the declared schema
is one nobody can read. The hazard the old check existed for — *"the host builds its converters from the
DECLARED schema, so a mismatch is read as DATA"* — is now structurally impossible rather than merely caught,
because by-name selection means the batch reaching the host always has exactly the declared columns in the
declared order. A reordering cannot be misread either. §28's one drift row became four.

### 24.7 What is not done

- **Filter pushdown.** Untouched; `filter_prune` stays off, which is also why `projection_ids` stays empty
  and the eligibility check can keep bailing on it.
- **A same-width REORDERING** would defeat the managed side's "projection or not" test, which keys on the
  count. It cannot arise — `RemoveColumnsFromLogicalGet` preserves column order, so a projection is always
  an ordered SUBSET — but that is an assumption about DuckDB, recorded here rather than guarded.

## 25. ✅ AS BUILT (2026-09-06) — the same PROJECTION PUSHDOWN for a COLLECTOR (ABI v87)

User-asked immediately after §24: *"should be possible to add `projected` to fluid_query_batch?"* — yes, and
structurally simpler, because a collector has no correlated columns. Gate `verify_plugin_fluid`
677 → **702**, hermetic floor 8953 → **8978**, one mutant.

### 25.1 What is the same, and the one thing that is not

`inout_exchange_open` gains `(projected, count)` — the only crossing between `inout_bind` and the first
output pull, exactly as `lateral_open` was for §24. Still a HINT; still discriminated so a collector that
knows nothing about it keeps working. Advertised for COLLECTORS ONLY at the time: the streaming exchange
passed an empty projection, so the two paths stayed distinguishable at the call site. **⚠ That last sentence
is SUPERSEDED — see §37, which gave the streaming exchange the same hint with no ABI change.**

⚠⚠ **The difference is WHERE the shape is declared.** A lateral's result is a fresh stream per `Call`, so the
host could tell the two shapes apart by looking at a batch. A collector's output crosses as ONE stream whose
schema is read BEFORE the first batch — so an empty result must be classifiable too, and a callee that
narrows has to SAY SO. Hence `ProjectedOutputSchema(projected)`, a DIM returning the full `OutputSchema` by
default: overriding `Collect` alone is not enough, and would have the host read narrow batches through wide
converters.

### 25.2 ⚠⚠ THE PROBE THAT PROVED NOTHING, and it is the most useful thing here

`SELECT b FROM fluid_query_batch(…)` returns one column **whether or not the get was narrowed**, because
DuckDB projects above the operator either way. That row passed happily while the projection was reaching
NOTHING AT ALL — `collector.projection_pushdown = true` had been set on the CATALOG registration, and
`fluid_query_batch` is a GLOBAL collector, registered on a different path a hundred lines away.

What exposed it was asking the template what it saw (`projected` reported all three columns) and the payoff
row (an unread `error()` column still fired). ⇒ **the only evidence of pushdown is work not happening, or
the projection itself; the shape of the result is not evidence.** §31 says so at the top and asserts only
those two.

⚠ A wrong explanation was reached for first, and the gate is what killed it: the same probe under
`SELECT DISTINCT` also showed all columns, which fitted DuckDB's `everything_referenced = true` rule for a
plain distinct (`remove_unused_columns.cpp`). That rule is real, but it was NOT the cause — re-measured after
the registration fix, `DISTINCT` narrows fine. The row asserting otherwise failed and was deleted rather than
adjusted.

### 25.3 The payoff row has a trap of its own

It must not aggregate. The schema probe renders against an EMPTY `input_table`, and a `count(*)` produces a
row — and evaluates the `error()` — at BIND, which fails the row for a reason unrelated to projection. A
row-producing template renders nothing at the probe and everything at the group, which is what makes the
assertion mean what it says.

### 25.4 Mutant, and the same lesson as §24.3

Emitting by position instead of through the wire map passes **690** assertions and dies at the
`fabricator_collect_sum` row — the collector that ignores the hint. `fluid_query_batch` honours it, so its
map is the identity and its own rows cannot catch the off-by-one. Both surfaces now depend on having one
in-tree callee that deliberately does NOT honour the hint.

### 25.5 The same drift relaxation

`fluid_query_batch` had no wrapper at all before this; it now wraps every group in a SELECT naming the
declared columns, for the same reason and with the same consequence as §24.6 — an EXTRA column is dropped
where it used to be refused, while MISSING, RENAMED and RETYPED stay refused. Its one drift row became four.
⚠ The wrapper is applied even with NO projection, deliberately: otherwise the drift behaviour would depend on
the caller's SELECT list, which is the "runs and means something different" shape this file keeps warning
about.

## 26. ✅ ALREADY TRUE, now stated and pinned (2026-09-06) — the output schema may depend on the INPUT schema

User-asked: *"could we make the input_table available as an empty table at bind time (is_bind)? This enables
building the outputschema not only dependent on params but also on the input_table schema"*. It already is,
on both surfaces — but as a CONSEQUENCE rather than a stated feature, and nothing pinned it. Gate
`verify_plugin_fluid` 702 → **716**, hermetic floor 8978 → **8992**. No code change.

Both binds call `CreateEmptyInput` BEFORE the `is_bind` render, so `input_table` exists during the probe with
its real columns and no rows. A template can therefore DESCRIBE it and build its SELECT list from the answer:

```liquid
{% query c %}SELECT column_name FROM (DESCRIBE SELECT * FROM input_table){% endquery %}
SELECT {% for col in c %}{{ col.column_name }} AS out_{{ col.column_name }}{% unless forloop.last %}, {% endunless %}{% endfor %}
FROM input_table
```

MEASURED: over `(SELECT 1 AS alpha, 'x' AS beta)` that yields `out_alpha`/`out_beta`; over `(SELECT 7 AS
gamma)` the SAME template yields `out_gamma`. That second row is the discriminator — a single-input
assertion would pass equally on a build with the names hardcoded.

⚠ It works on `fluid_query_lateral` too, where the input column names are the rendered EXPRESSION TEXT
(§22.3), so a schema-derived template there names its outputs after `n`, `(t.n + 1)` and so on.
`input_table` carries `__fab_row` first, so such a template excludes it from its outputs and projects it.

⚠ Why it was worth pinning rather than just documenting: `CreateEmptyInput`'s stated purpose is letting the
probe BIND the generated statement. That the schema is DERIVABLE from it is a second-order effect of the
ordering, and a refactor moving the create past the render would take it away. The gate is what stops that.

⚠ It composes with `projected` (§24.5, §25) — the probe has the input schema but NOT the projection, which
does not exist until after the bind. So a template may derive its FULL shape from the input at bind and then
narrow that shape per call.

## 27. ✅ AS BUILT (2026-09-06) — `{% query name materialize: … %}`, and it binds the name LAZILY

User-asked: *"with {% query result %} i would like to have an optional result materialize types. fluid: true,
materialize: 'view' or 'table' where both are temp"*, then *"materialize: null would be default"* — and then,
hours later, a second round that changed the shape (§27.6). C#-only in the plugin — no ABI, no C++. Gate
`verify_plugin_fluid` 716 → 729 → **737**, hermetic floor 8992 → 9005 → **9013**, two mutants each killed at
its own assertion.

```liquid
{% query r %}                             rows in Liquid (unchanged)
{% query r materialize: null %}           the same, explicitly
{% query v materialize: 'view' %}         TEMP VIEW v  — AND v is bound, lazily
{% query t materialize: 'table', x: 5 %}  TEMP TABLE t — AND t is bound, lazily; $x bound
```

**Both access paths, one name.** With `materialize:` set the rows are left on the render's own connection as
a TEMP object a later `{% query %}` or `{% exec %}` can read, **and** the identifier is still bound in Liquid
— as a `LazyRowsValue`, which runs `SELECT * FROM "name"` on the pin at FIRST ACCESS and caches. A template
that never reads the variable never pays for it, so there is nothing to opt out of and no `fluid` option.

### 27.1 ⚠⚠ Why the rows stay in DuckDB — the trade this section exists to record

The obvious alternative is the mirror image: pull every row into managed memory, keep the `RecordBatch`es
alive for the render, register them back as a scannable source, and let the template reach them either way.
It is mechanically feasible — `IHostQuery.RegisterRows` documents that *"scanning the same token twice is
fine: each scan gets a fresh reader over the same rows"*, which a bound Arrow INPUT cannot promise — and it
was proposed and declined. Three costs settle it:

1. **It is two crossings and a full buffer to reach a place the rows already were.** `materialize:` is a
   CTAS (or a view): nothing crosses the ABI, nothing is capped, and DuckDB spills. The Arrow round trip
   would reintroduce the 1,000,000-row cap `query()` carries, on a path whose whole point is a relation too
   big to want in Liquid. This is the same trade `publish()` measured and reversed — the buffered build
   refused above 1,000,000 rows, the lazy one did 3,000,000.
2. **The temp object would become unconditional, so the shadowing in §27.4 would too.** Today a
   `{% query customers %}` with no `materialize:` binds a Liquid variable and touches no catalog; unconditional
   registration would silently change what `FROM customers` means in the generated SQL for the rest of the
   render.
3. Retained Arrow buffers live in NATIVE memory, where `MemoryProbe`'s `heap` is blind to them (measured
   elsewhere at `ws=471MB heap=1MB`) — so the cost would also be invisible to our own instrument.

⇒ **keep the rows in DuckDB and make the LIQUID side lazy over them**, which is what shipped.

⚠ One premise of the proposal was right and is worth keeping: the earlier attempt at a lazy row wrapper
failed because the batches were disposed under it, and that failure is **LOUD** — Apache.Arrow nulls a
disposed `RecordBatch`'s arrays, so it throws a `NullReferenceException` on the first cell read,
deterministically and on every platform. Not the silent native use-after-free class this repo usually warns
about. So a retained-batch design is safe to ATTEMPT; it is the cost, not the safety, that decided it.

### 27.2 ⚠⚠ A VIEW is evaluated at the ACCESS, a TABLE at the BLOCK

A consequence of laziness, MEASURED, and the pair that proves the read is really deferred: a
`materialize: 'view'` over a table, an `{% exec %}` that updates the table, then the first Liquid access ⇒
the **new** value (7 where the block saw 1). The identical template with `'table'` reads **1** — the
snapshot the block took. That is what those two words mean in SQL, and it is the reason both are offered.

⚠ The `'table'` row is a CONTROL, not a second proof: an eager bind would report 1 there too. Only the
`'view'` row can catch an eager bind, and mutant A (bind eagerly) dies exactly on it after 721 pass.

⚠ **The read is CACHED** — a second access after another `{% exec %}` still reports the first read, so a
template sees one consistent value and pays one round trip per name per render. Mutant B (drop the cache)
dies at that row after 725 pass.

⚠ **Each gate row sets its own starting value and renders ONCE.** The first version put the `'view'` and
`'table'` legs in one `SELECT` as two `fluid_render` calls, which does not pin an order — whether the second
saw the first's `UPDATE` would have been DuckDB's business. It passed, and it was a flake; the shape is
already recorded in CLAUDE.md and was walked into anyway.

### 27.3 ⚠⚠ A TABLE can carry the block's named arguments and a VIEW cannot

MEASURED, and it is DuckDB's rule rather than ours: a CTAS with a bound parameter works, while the same body
as a view is refused with *"Unexpected prepared parameter. This type of statement can't be prepared!"* — a
view STORES its body, so a parameter has no meaning at scan time. The combination is therefore refused at the
tag, naming the mode and pointing at `'table'`, rather than surfacing an engine message that names neither.

⚠ Refused whenever named args are supplied with `'view'`, not only when the body references them: the
narrower rule would depend on the body and be unpredictable, and the fix is one word.

### 27.4 ⚠⚠ The shadowing hazard, settled

CLAUDE.md flagged it when the CTAS idea was deferred: *"`t` becomes a TEMP TABLE name and a temp table
SHADOWS a catalog table of that name on the connection, silently. Settle that deliberately."* Settled, and
MEASURED both halves: a materialized `mat_shadowed` DOES shadow a catalog table of that name for the rest of
the render (99 over a table holding 1), and the catalog table is UNTOUCHED afterwards, because the temp
object dies with the render's connection.

⇒ accepted, because the name is the author's own identifier rather than something generated, the blast radius
ends with the render, and — the half that matters after §27.1 — it happens **only when the author asks for it
with `materialize:`**. Asserted rather than described, with the "untouched" row as what makes it acceptable.

### 27.5 ⚠⚠ A bug my own shortcut created, and the row that pins it

`materialize: null` failed with DuckDB's *"excess parameters"*. `ReadQueryOptionsAsync` returned the argument
list UNCHANGED when nothing was taken — which cannot tell an ABSENT option from one PRESENT AND NULL, and
`null` is the documented default. The list is always rebuilt now; §33's second row is the discriminator, and
it would pass on a build with no options support at all if the first row were not beside it.

### 27.6 ⚠⚠ `fluid:` IS GONE — the option the lazy bind made vestigial

The first build shipped hours earlier with a second option. `fluid` defaulted to *"no materialize"*, so with
`materialize:` set the identifier named a SQL object and was **not** bound in Liquid (`{{ v }}` rendered
empty); `fluid: true` asked for both and **ran the body TWICE**. It is removed, BREAKING, with no alias.

Once binding is lazy there is nothing left to opt out of: an unread variable costs zero, and the body runs
ONCE in every spelling. ⚠ `fluid` is no longer a reserved name, so it falls through to the bound parameters
and fails LOUDLY — *"excess parameters: 1"* on a statement that references no `$fluid`. §33 pins that, so the
removal announces itself rather than being silently ignored, and the comment there records why the message
names the parameter positionally rather than by name (the host binds by NAME only when the batch's names
match the statement's own named parameters, and a body with none falls back to positional).

⚠ **§33's row asserting the OPPOSITE was REPLACED, not deleted** — it pinned *"materializing means the rows
went to SQL instead of to Liquid"*, which was true of the first build and is precisely the assumption this
change lifts. Falsifying it is the change announcing itself; the replacement asserts both paths under one
name, and a note at the site says so.

### 27.7 ⚠ It is ergonomics over something that already shipped, and that is fine

`{% exec %}CREATE TEMP TABLE t AS …{% endexec %}` then `{% query u %}… FROM t{% endquery %}` has worked since
the pinned connection (§12). What `materialize:` adds is that the body stays a `{% query %}` body — still
classified as a SELECT, still parameterised the same way — so choosing the destination does not mean
rewriting the block as DDL, and after §27.6 it does not mean choosing between SQL and Liquid either. This is
the *"query + automatic CTAS"* idea deferred on 2026-09-04, in the explicit form rather than the automatic one.

### 27.8 Reserved names, and what stays true

`materialize` is now the ONE reserved argument name, joining `{% print %}`'s `delim`/`rowdelim` as something
a statement cannot use for a parameter. Accepted for the same reason: it fails LOUDLY, with DuckDB naming the
parameter it was not given. ⚠ The option is EVALUATED rather than matched on source text, so
`materialize: params.mode` works. ⚠ And the body is still classified as a SELECT — `materialize:` is a
destination for rows, never a way to smuggle a write past the rule. ⚠ The lazy read itself is NOT classified
and does not need to be: it is `SELECT * FROM` a quoted identifier, composed by `ReadMaterialized`, with no
template text in it.

## 28. ✅ AS BUILT (2026-09-06) — `input_table` is a lazy Fluid value too

User-asked, as the second half of §27's inversion: *"yes lazy fluid makes also sense for input_table"*.
C#-only in the plugin — no ABI, no C++, and no new mechanism: it is `LazyRowsValue` pointed at the temp
object each surface already creates. Gate `verify_plugin_fluid` 737 → **758**, hermetic floor 9013 →
**9034**, one mutant.

```liquid
{{ input_table.size }}                                     the Liquid side
{% for r in input_table %}{{ r.i }}{% endfor %}
SELECT (SELECT count(*) FROM input_table) AS n            the SQL side, unchanged
```

Both surfaces: `fluid_query_batch` (per GROUP) and `fluid_query_lateral` (per CHUNK), plus the schema probe
on each, where the relation is EMPTY.

### 28.1 ⚠⚠ The load-bearing property is FRESHNESS PER RENDER, not laziness

`LazyRowsValue` caches — which is what keeps one render internally consistent, and is exactly why the value
must be rebuilt before every render that repoints the object. One bound per session would serve the FIRST
group's rows to every later group: right column names, right shape, wrong rows, no error anywhere.

`FluidHostQuery.BindLazyRelation` is called immediately after `DefineGroupView` (collector) and after
`StageInput` (lateral), so the SQL view and the Liquid value are repointed together and the two access paths
cannot disagree about which group or chunk they are in. §34's first row is the discriminator — the SQL count
and the Liquid `.size` in every group, and the numbers CHANGE (2, 2, 1 over five rows at `batchsize := 2`)
where a session-scoped value reports 2, 2, 2. The mutant dies there after 742 pass.

⚠ The lateral is PARALLEL, and this needs nothing extra: one session, one connection and one
`TemplateContext` per pipeline thread, so a per-call `SetValue` on that thread's context is thread-confined
by construction.

### 28.2 ⚠ It costs nothing unless the template reads it

The rows were already staged in DuckDB — `__fab_input` for the collector, a temp table for the lateral — so
this adds no copy and no crossing. A template that only writes SQL over `input_table` pays exactly what it
paid before; a template that reads it in Liquid pays one round trip per render, cached.

⚠ The alternative — retaining the input `RecordBatch`es and mapping cells over them — would have been a
genuine use-after-free rather than merely expensive: `IHostQuery.RegisterRows` documents that the rows are
**borrowed, not adopted**, and the framework frees a collector's input chunk once consumed. Keeping them
alive would have meant changing the ownership contract at that seam. §27.1's cost argument and this one
point the same way for different reasons.

### 28.3 ⚠ It is bound at BIND time too, EMPTY

The schema probe renders against an empty `input_table` (§26), and the value is bound there as well, so
`{{ input_table.size }}` answers 0 rather than failing. A name that resolves at scan and not at bind is the
split this plugin already records as a trap for `fluid_template_root`; `is_bind` stays the way to branch.

⚠ §34's `bind_saw_0` row pins the VALUE — the probe renders the size into the output COLUMN NAME, so a bind
reading anything but 0 would declare `bind_saw_<n>` while the groups produce `bind_saw_0` and the drift check
would refuse. It is a CHARACTERIZATION row: removing the bind-time binding does not reach it, because an
unbound `{{ input_table.size }}` renders EMPTY and the probe's own SQL then fails to parse, which the
section's first row catches. What it pins is that the probe's input is empty rather than sampled.

### 28.4 ⚠ A template's own variable shadows it, and only on the Liquid side

We bind before the render, so an `{% assign input_table = … %}` in the template wins — the safe direction.
The SQL object is a different namespace and still holds the rows, which §34 asserts as a pair.

⚠ The name is now meaningful in Liquid where it previously resolved to nothing. A template that rendered
`{{ input_table }}` and got an empty string will now render the row set. Low risk (it referenced a name that
meant nothing), but it is a behaviour change rather than a pure addition.

## 29. ✅ AS BUILT (2026-09-06) — `{% provider_query %}` / `{% provider_exec %}`: a body in ANOTHER engine's dialect

User-designed (*"seperatur tags `{% provider_query cat r %}` / `{% provider_exec cat n %}` is good"*), built as
slice B of [provider-query-parameters.md](provider-query-parameters.md). C#-only IN THE PLUGIN: no ABI, no
C++, no bridge. Gate: a NEW suite `verify_plugin_fluid_provider` (**22**, SERVICE tier), two mutants each
killed at its own row.

```liquid
{%- provider_query 'mssql' r region: 'eu' -%}
  SELECT id, region
  FROM dbo.people
  WHERE region = @region
{%- endprovider_query -%}
{{ r.size }} rows, first is {{ r[0].id }}

{%- provider_exec 'mssql' n old: 'us', new: 'uk' -%}
  UPDATE dbo.people SET region = @new WHERE region = @old
{%- endprovider_exec -%}
```

### 29.1 WRAP AND DELEGATE — there is no new mechanism

The rendered body is embedded in a `fabricator_query` / `fabricator_exec` call which the existing
`{% query %}` path runs. So the value model, the row cap, the per-render pinned connection and `materialize:`'
siblings compose for free, and the tags cannot drift from the function forms on what a result or a count
means. `RunCaptured` is the single entry point for both.

⚠ **What the tag buys is that the body stops being a quoted string argument** — multi-line, with
`{% for %}`/`{% if %}` inside, no escaping. `{% query r %}SELECT * FROM fabricator_query('mssql', '…')
{% endquery %}` has always been legal. Exactly the argument that justified `{% exec %}` over `exec("…")`.

⚠ **A distinct NAME is a feature, not readability.** An option on the existing block
(`{% query r catalog: 'mssql' %}`) was considered and rejected: `materialize:` changes where the rows GO,
while a catalog option would change which engine PARSES AND RUNS the body. An option that silently switches
dialect is the runs-and-means-something-different shape.

### 29.2 ⚠⚠ The tag's named arguments are the PROVIDER's parameters, bound the whole way

`params := struct_pack("region" := $region)` — never a rendered literal. MEASURED before building that this
binds and keeps its types: a prepared `fabricator_query($cat, $sql, params := struct_pack(a := $a, b := $b))`
returns `int32`/`varchar` for 41 and `'hi'`. It matters because the only interpolation available is
`DuckSql.Literal`, which is DuckDB DIALECT — it coincides with T-SQL for strings and integers and diverges
for booleans, blobs and temporals (§7.4a's `| sql` finding, one layer out).

⚠ The CATALOG and the BODY are embedded as DuckDB string LITERALS, and that is correct rather than a
shortcut: they are arguments to a DuckDB function, so DuckDB's dialect is the right one. Nothing of the body
is parsed here — it is opaque text on its way to another engine.

⚠ `fabricator_query` takes the bag NAMED and `fabricator_exec` POSITIONALLY, because the latter is a SCALAR
and a DuckDB scalar carries no named parameters at all. **Mutant A** (send the query bag positionally) dies
at the first parameter row after 4 assertions.

⚠ With no named arguments the bag is OMITTED rather than sent empty: `struct_pack()` with no fields is a
zero-field struct, which Apache.Arrow refuses in both directions.

### 29.3 ⚠⚠ `{% provider_exec %}` goes through the **QUERY** path, and it must

The wrapper is `SELECT fabricator_exec(…)` — a SELECT by construction — so `{% exec %}`'s classifier would
REFUSE it. The provider write happens inside that scalar; what DuckDB runs is a read.

⚠ That is also the sharpest illustration of §29.5: **the statement DuckDB classifies and the statement the
provider executes are not the same statement.**

⚠ The count is read back from an ALIASED column (`AS affected`). Without the alias the column is named by its
own expression text — the whole `fabricator_exec(...)` call, quotes and all — which is what the value would
have to be addressed by. **Mutant C** (drop the alias) dies at the count row after 6 assertions.

### 29.4 ⚠ The catalog is an EXPRESSION, and a bare word is refused BY NAME

`{% provider_query 'mssql' r %}` and `{% provider_query params.cat r %}` both work, because the first token is
an INPUT and a bare identifier in an input position reads as a Liquid variable to anyone who has written
Liquid. The cost is that `{% provider_query mssql r %}` — the spelling someone will try first — evaluates to
nil, so it is refused HERE naming the quoted form. Without that it would reach the host as an empty catalog
and fail somewhere that names neither the tag nor the word.

⚠ The result name is REQUIRED on `provider_query` and OPTIONAL on `provider_exec`, matching `{% query %}` vs
`{% exec %}`: a write's count is often not wanted, while a read that binds nothing has done nothing. Same
negative lookahead as `{% exec %}` — without it `{% provider_exec 'c' x: 7 %}` would take `x` as the name and
then fail on the `: 7`, because `ZeroOrOne` does not retry its empty branch once the sequence fails.

### 29.5 ⚠⚠ The SELECT-only guard does NOT transfer — ACCEPTED and PINNED, not overlooked

`{% query %}` refuses a non-SELECT using DuckDB's OWN parser, because a bind REPEATS and happens WITHOUT
execution. For provider SQL there is no parser we can ask, and `fabricator_query` runs writes happily — so a
provider tag inside `fluid_replacement_query` writes at BIND time, repeatedly.

**MEASURED**: an `EXPLAIN` of a `fluid_replacement_query` containing `{% provider_exec %}INSERT …{% endprovider_exec %}`
takes the target table 0 → **1**, and the statement that DOES execute takes it 1 → **2**.

**Accepted (user decision, 2026-09-06)**, for the reason the `exec()` refusal was DELETED: §11.1a MEASURED
that a host-side refusal was already walk-aroundable by nesting a writing scalar inside a SELECT, and a
refusal anyone can nest around is a speed bump for the accident that READS as a defence. So the cost is
PINNED as asserted behaviour (§5 of the suite), exactly as `{% exec %}`'s own bind-repetition is — a change
then arrives as a failed assertion naming the step rather than as a surprise in someone's database.

⛔ Do NOT "fix" it by matching a leading keyword on the body: §9.2's measured-broken prefix check, defeated by
`WITH x AS (…) INSERT …`, a view, or a name we do not ship.

### 29.6 ⚠ Two suite mechanics that cost real time

* **`require json` is LOAD-BEARING**, and its absence reads as a broken FEATURE. The classifier is
  `json_serialize_sql`; `unittest` does not auto-load extensions, so without the directive EVERY provider tag
  is refused (fail-closed) with a message about the json extension. ⚠ It cost half an hour because the
  runner's terse failure (`explicitly with message: 0`) hid it — **the full output had the whole error all
  along, and a narrow `grep` was discarding it.** Same rule this repo already records for `run-suites.sh`.
* **sqllogictest splits a result ROW on WHITESPACE**, so a single-column value containing spaces is read as
  several values and never matches. Every rendered assertion here is deliberately `|`-separated.
* ⚠ `rtrim(x, chr(10))`, not `trim(x)`: DuckDB's one-argument `trim` removes SPACES and leaves the newline a
  `$$`-quoted template ends with.

### 29.7 Why the gate is its OWN suite

`verify_plugin_fluid` is HERMETIC. The tags need a real provider catalog to mean anything, so adding a
`require-env` there would move **759** assertions out of the hermetic tier to gate 22.

## 30. ✅ AS BUILT (2026-09-11) — `fluid_query_inout`, the STREAMING sibling of the collector

User-asked: *"implement a fluid_query_inout similar to the collector version fluid_query_batch but not as a
collector this time as a streaming table in/out function."* It is the item §19 recorded as deliberately
still open — *"a bounded-memory batched variant is the SAME body registered on the streaming in-out"*.

C#-only, **NO ABI change and NO C++ change**: `IProvider.GlobalInOutFunctions` already existed as a DIM and
the global in-out kind has been supported since ABI v46/v47. Gate: `verify_plugin_fluid` 759 → **794** (§35),
hermetic floor 9088 → **9123**, THREE mutants each killed at its own row.

```sql
SELECT * FROM fluid_query_inout('SELECT n, n * 2 AS dbl FROM input_table',
                                (SELECT * FROM (VALUES (1), (2), (3)) t(n)));
```

### 30.1 What it buys, and what it cannot do — both forced by the operator

**BOUNDED MEMORY is the whole point.** A collector must buffer its ENTIRE input before the first render;
that is inherent to a collector and is true even at a small `batchsize`, which is why §19 records that
parameter as being about how many rows each render SEES and never about memory. This surface holds one
chunk at a time.

**And it cannot render over the whole input, which is why there is no `batchsize`.** The streaming in-out's
all-input-done hook is handed no `DataChunk`, so output held back until input EOF is DRAINED AND DISCARDED
(docs/inout-collector-mode.md) — so a group spanning chunks is inexpressible, and **the chunk IS the batch**.
How the input divides into chunks is DuckDB's business.

⚠ It is a SEPARATE registration rather than a mode of `fluid_query_batch`, and that is not a style choice:
`kind` is fixed at REGISTRATION, so one name cannot switch operators by parameter. §19 records the same
constraint for the lateral.

**MEASURED, the contrast that defines the pair** — the template reports its own input size, so each output
row IS one render:

| over `range(5000)` | renders | rows | biggest render |
|---|---|---|---|
| `fluid_query_inout` | **3** | 5000 | 2048 |
| `fluid_query_batch` | **1** | 5000 | 5000 |

### 30.2 ⚠⚠ AN EMPTY INPUT DIVERGES, and it is a real semantic difference rather than an oversight

`fluid_query_inout` over an empty relation renders **NOTHING**; `fluid_query_batch` renders **ONCE**
(measured, and gated as a pair so neither half reads as an accident). With no rows there are no chunks, and
a render is per chunk — while the collector renders once because a template is a statement GENERATOR whose
output need not depend on the rows. **A caller who needs the generator to fire regardless wants the
collector.**

### 30.3 What is identical, and why that is structural rather than careful

`params`, `input_table` as both a temp table and a lazy Liquid value, `is_bind`, the projection hint and
wrapper, the `publish()` refusal, and the SQL-state-carries / Liquid-state-does-not split (measured here
too: a temp-table counter reads 1, 2, 3 across chunks while a `{% assign %}` counter reads 1, 1, 1).

They are identical **by construction**: the second surface arrived with `FluidRelationInput`, into which the
schema probe, the projection wrapper, the drift check, the publish refusal and the empty-input creation were
EXTRACTED rather than copied. Each of those is load-bearing in a way a reader would not guess — the
`LIMIT 0` wrapper is also what REQUIRES the generated statement to be a subquery-usable SELECT, `Wrap` IS
the projection pushdown — so a divergence between the two would be a silent behaviour difference, not a
compile error.

⚠ It also required moving `InOutExchange.EmptyBatch` (the length-0 sentinel) from `Fabricator.Bridge` to
`Fabricator.Common`, and that was a PRE-EXISTING gap rather than a new need: `StaticInOutFunction` already
lived in Common and its own documentation told authors to yield `InOutExchange.EmptyBatch`, while the helper
sat in an assembly a plugin deliberately does not reference. **A plugin deriving from that base could not
write the one thing the base requires of it.** The namespace is unchanged, so not one call site moved.

### 30.4 ⚠ DRIFT IS CAUGHT BY TWO MECHANISMS, and which fires depends on HOW the render drifted

Found by writing the gate: the expected message was wrong the first time.

- A **RENAMED** column is caught by the **WRAPPER** — it selects the declared names from the generated
  statement, so the inner bind fails naming the column, before any row is produced.
- A **RETYPED** column keeps its name, so the wrapper binds happily and only **`Verify`** can see it.

Both are gated. Without the second row `Verify` would have been untested here, and a reader would assume one
check covered both — mutant 3 (drop the drift check) survives the first row and dies on the second.

### 30.5 The mutants

| mutant | dies at | actual |
|---|---|---|
| stage the input only once | the defining-contrast row | `1  6144  1` — 3 renders each seeing the FIRST chunk's 2048 rows |
| bind the lazy Liquid value once | the both-paths-agree row | `0  1  5000` — SQL correct, Liquid stale |
| drop the drift check | the RETYPED-drift row | "Query unexpectedly succeeded" |

⚠ The first two die on DIFFERENT rows and for different reasons, which is what separates "the temp table is
stale" from "the Liquid value is stale" — the agreement row alone could not, because a build where BOTH are
stale still reports them as agreeing. The `sum` and the `max(sql_n) > 1` control are what catch that.

---

## 37. Projection pushdown through the STREAMING IN-OUT (2026-09-12)

User-asked after reading §30 back and finding the hint listed among what `fluid_query_inout` shares with the
collector: **it was wired and inert.** C++-only, **no ABI change** — the entry has carried the argument since
v87 and `InOutExchangeStream` has always declared `ProjectedOutputSchema(projected)` and forwarded `projected`
to `DoExchange`. What was missing was one host-side flag.

Gate `verify_plugin_fluid` 794 → **810** (§36) and `verify_global_functions` 164 → **178**; two mutants, each
killed at its own SUITE.

### 37.1 Why it was inert, and how little was missing

`tf.projection_pushdown` was `is_collector`, so `RemoveColumnsFromLogicalGet` — which gates on exactly that
flag and on nothing about `in_out_function` — never narrowed a streaming in-out's get, and
`InOutExchangeOpen` was handed `vector<int32_t>()` at the call site. Both exchange registrations set the flag
now: the GLOBAL one (`fluid_query_inout`, `fabricator_inout_va`) and the CATALOG-BOUND one, which is where
every provider-declared `_each` resolves.

⚠ The two registrations are separate call sites and could have been enabled independently. They were enabled
together deliberately: the mechanism is identical, the ignore-the-hint fallback makes it safe for a callee
that has never heard of it, and two exchange registrations disagreeing about their own optimizer contract is
the kind of inconsistency someone trips over later.

### 37.2 The rule now exists once

`FabricatorProjectionHint(column_ids, output_width)` is shared by the exchange and the collector, which had
carried its own copy. EMPTY deliberately means two things — the caller reads every column in order, or the
get carries a column we do not declare (a virtual column, a correlated passthrough) that cannot be indexed
into the callee's schema — because both want the same answer: send the full width.

### 37.3 ⚠⚠ The gate that matters is the callee that IGNORES the hint

`fluid_query_inout` HONOURS it, so its wire map is the IDENTITY and **its own rows cannot catch an
off-by-one** — §24's lesson, reproduced exactly. The row that tests the map is in `verify_global_functions`,
over `fabricator_inout_va`, which overrides neither DIM: two columns of DIFFERENT types (`BIGINT n`,
`VARCHAR tag`), so reading `tag` alone narrows to wire column 1 and a build that drained straight through
would put `n` into the single VARCHAR slot.

MEASURED, and the split is the point:

| mutant | `verify_plugin_fluid` | `verify_global_functions` |
|---|---|---|
| the global exchange stops advertising the flag | **dies** at the payoff row, 800 passed | passes 178 — no hint ⇒ full width ⇒ still correct |
| drain by position, wire map ignored | passes **810** — identity map | **dies** at line 474, 141 passed |

⚠ Mutant 2 is killed by a **PRE-EXISTING** assertion (`SELECT count(*), min(tag) FROM fabricator_inout_va`),
because enabling pushdown made an existing row depend on the new map. The rows added here kill it too — and
they do so with `INTERNAL Error: Attempted to dereference unique_ptr that is NULL`, so getting the map wrong
is a CRASH rather than a wrong value.

### 37.4 ⚠ What the payoff row measures, and what no row can

Selecting one column proves nothing — §25's lesson, unchanged: DuckDB projects above the operator either way.
What is asserted is the PAYOFF (an unread column holding `error()` is never evaluated, with the read-both case
beside it as the control) and the `projected` variable itself.

⚠ And the projection is **what is READ, not what the inner SELECT list says** — measured while writing §36,
where `min(q) FROM (SELECT p AS q, a FROM …)` still narrowed to `p` alone because DuckDB prunes `a` straight
through the subquery. Both columns have to be CONSUMED for a row to exercise the unnarrowed path.

### 37.5 ⚠⚠ A pre-existing deadlock this work found, and an attempted fix that was measured WRONG

A `LIMIT` above ANY streaming in-out — no projection involved — fails with **`Invalid Error: resource
deadlock would occur`**. MEASURED on `fluid_query_inout` and on `fabricator_inout_va`, and **reproduced on a
binary built from HEAD with this change absent**, which is what attributes it elsewhere.

The operator returns `HAVE_MORE_OUTPUT` while holding `FabricatorExchangeGlobalState::gate` and releases it
only on its NEXT invocation (SENTINEL or END), so a pipeline that stops early ABANDONS the tenure;
`OperatorFinalize` → `ExchangeHolder::Finish` → `FinishEof` then tries to take that same gate.

⚠⚠ **NOT "any bare `LIMIT`", which is how this was first written down.** The trigger is a limit satisfied
BEFORE the operator is pulled again. MEASURED over a 4-row input: `LIMIT` 1, 3 and 4 all fail, and
**`LIMIT 10` is FINE** — the operator is pulled again, hits END, and releases the tenure itself.
`ORDER BY … LIMIT` is fine for the same reason: a pipeline breaker drains the in-out to END before the limit
applies, which is the spelling every suite happens to use and why none of them caught this.

### 37.6 ⚠⚠ The owner-tracking fix: BUILT, MEASURED INSUFFICIENT, REVERTED

The obvious fix is to record which thread holds the gate so `FinishEof` can ADOPT its own tenure instead of
re-locking. It was built (2026-09-12) and reverted, and the reason is worth more than the code was.

**There are TWO shapes, and the first write-up asserted only one.**

| who runs `FinishEof` | symptom | measured on |
|---|---|---|
| the thread HOLDING the tenure | `EDEADLK` — MSVC detects the same-thread re-lock and throws | single-branch `fabricator_inout_va … LIMIT 1`, thread-id probe: `op LOCK tid=N`, no unlock, `FinishEof WANTS gate tid=N` |
| a DIFFERENT thread | blocks forever on a tenure nobody will release — a silent **HANG** | the whole of `verify_plugin_fluid`: **15 `FinishEof` calls, `owns=1` ZERO times** |

⚠⚠ So owner tracking fixes the loud case and converts the silent one from an error into a hang — measured,
the fluid suite hung at 400 s where it had merely failed. **Strictly worse than the bug.**

⚠⚠ **THE PRIMITIVE IS THE PROBLEM, NOT THE TRACKING.** `std::mutex` may only be unlocked by the thread that
locked it, and this gate is not a critical section — it is a **TOKEN acquired in one `Execute` call and
released in a LATER one**, whose holder may never run again. No amount of owner tracking fixes an abandoned
token, because the only thread permitted to release it is the one that will not run.

⇒ the conclusion drawn at the time was that a real fix needs a different PRIMITIVE — `mutex` +
`condition_variable` + a `taken` flag, releasable by ANY thread. **That turned out to be unnecessary; see
§37.7, where two further measurements produced a smaller fix.** What survives from this section is the
diagnosis, not the prescription.

⚠⚠ On glibc the same-thread re-lock is UNDEFINED BEHAVIOUR rather than a thrown error, so the Linux symptom
is likely worse than the Windows one — the same asymmetry recorded for `entry_lock_`.

### 37.7 ✅ THE FIX: release the abandoned tenure from the LOCAL STATE DESTRUCTOR

No new primitive, no thread-identity assumption, and `FinishEof` untouched. `FabricatorExchangeLocalState`
gained a pointer to its global state and a destructor that unlocks the gate when `owns_gate` is still set.

**THREE MEASUREMENTS MAKE IT THE RIGHT RELEASE POINT, and none of them had been taken before:**

| measured | why it matters |
|---|---|
| a local state is destroyed **BEFORE** `OperatorFinalize` | the gate is already free when `FinishEof` arrives, so nothing has to reclaim it |
| it is destroyed **ON THE THREAD THAT TOOK ITS OWN LOCK** (3/3 single-branch, 3/3 parallel) | this is what makes `unlock()` there LEGAL — a `std::mutex` may only be unlocked by its owner |
| `FinishEof` runs on a **FOREIGN** thread | the reason §37.6's owner-adoption could never work, restated as a positive result |

⚠⚠ **THE SECOND ROW IS THE ONE THAT DECIDES BETWEEN THE TWO DESIGNS.** Had the destructor run on a foreign
thread, `std::mutex` would have been unusable there too and the condvar token of §37.6 would have been
required. It does not, so the token change — a concurrency-primitive swap in the exchange operator — was
avoided entirely. **Measure the release point before designing the release mechanism.**

**⚠⚠ A STRUCTURAL FACT THE PARALLEL TRACE EXPOSED, worth having before reasoning about this operator again:
each `my_inout(…)` call in a UNION is a SEPARATE table function instance with its OWN gate.** The trace
showed three threads each holding `owns_gate = 1` at once, which is impossible for one mutex — so branches of
a union do not contend with each other at all, and the gate serializes WITHIN one call. Three separate
finalizes, three separate gates.

**Gate:** `verify_global_functions` 178 → **189**. One mutant (empty the destructor body) dies at the
`LIMIT 1` repro row after 178 pass, reproducing the original `resource deadlock would occur`.

⚠ **One load-bearing row is the FOREIGN-THREAD one** — a `LIMIT` over a `UNION ALL` of separate calls —
because it is the row §37.6's attempt would have failed. Every single-branch probe was green while that
attempt hung the fluid suite, so a gate without this row would certify the broken fix again.

⚠⚠ **BUT THAT ROW DOES NOT TEST CONTENTION, AND THE FIRST VERSION OF THIS GATE CLAIMED IT DID.** A `UNION`
of SEPARATE in-out calls gives each call its OWN gate — MEASURED by instrumenting the lock with (gate
address, thread id): **THREE gates, one thread each**. So those branches never compete, and a row described
as "the gate serializing parallel branches" was asserting something it could not observe.

⚠⚠ **THE CONTENDED SHAPE IS A MULTI-BRANCH UNION AS THE *INPUT* TO ONE CALL** — one gate, several branch
pipelines. Same instrument: **ONE gate, THREE distinct threads.** That is also the shape the original
table-in-out work found problematic, and it is where the sharpest case lives: a `LIMIT` cutting a contended
input short abandons a tenure **while other branch threads are blocked inside `gate.lock()`**, which is
precisely what could deadlock teardown against a blocked branch. MEASURED: 20/20 clean at six branches and
`threads = 8`, plus 10/10 on the three-branch form.

⚠ **AND THE CONTENTION MUST BE VERIFIED, NEVER ASSUMED**, because `PhysicalUnion::BuildPipelines` may run
branches SEQUENTIALLY — a passing test over a union that happened to run on one thread proves nothing. The
(gate, tid) instrument is the cheap way to establish it; without it this gate would have been the vacuous
kind twice over.

⚠ The two controls (`LIMIT` past the end, and `ORDER BY … LIMIT`) are not decoration: they are the paths that
ALWAYS worked, so a build that released only on the END path passes them happily and fails the repro rows.

---

## 38. ✅ AS BUILT (2026-09-13) — the params bag is a DuckDB VARIABLE too

**User-asked:** *"could we additionally expose/set the `params` as a duckdb variable `params` on the fluid
render pinned duckdb session so it is easier to access it via a sql query?"* — yes. C#-only IN THE PLUGIN:
**no ABI change, no C++ change, no bridge change.** Gate `verify_plugin_fluid` 810 → **828** (the suite's §37), hermetic
floor 9166 → **9184**, three mutants each killed at its own row.

The same bag a template reads as `{{ params.x }}` is now staged onto the render's pinned connection, so its
SQL can read it as a VALUE:

```sql
SELECT fluid_render(
  '{% query r %}SELECT * FROM sales WHERE region = getvariable(''params'').region{% endquery %}{{ r.size }}',
  {'region': 'eu'});
```

It is available on **all five surfaces** inside `{% query %}` / `{% exec %}` bodies, and additionally to the
GENERATED statement on `fluid_query_batch`, `fluid_query_lateral` and `fluid_query_inout` — those three run
that statement on the same pin.

### 38.1 Why a named Arrow source and not rendered SQL

⚠⚠ **`SET VARIABLE params = (SELECT "params" FROM fabricator_scan('<token>'))`, never a rendered literal —
and that is what makes it TYPE-EXACT.** MEASURED: a bag of `{'d': DATE '2024-03-05', 'm': 19.99::DECIMAL(9,2)}`
comes back as `STRUCT(d DATE, m DECIMAL(9,2))` and `m * 2` is exactly `39.98`. Rendering instead would mean a
second SQL type ladder — and `DuckSql.Literal`, the one we have, collapses every temporal to TIMESTAMPTZ and
REFUSES a LIST or a STRUCT by name. It is the same argument that produced `publish()` (§18).

⚠ **The bound-parameter route the provider tags use is CLOSED here, measured:** `SET VARIABLE` **cannot be
prepared** (`Parser Error: syntax error at or near "SET"`), and the host's parameterised `host_query` path is
`Prepare` + `Execute`. The named-source staging is the only faithful route, and it is the one
`FluidRelationInput.CreateEmptyInput` already uses for `input_table`.

### 38.2 Scope: it cannot collide with anything, and it dies with the render

⚠ DuckDB keeps variables in **`ClientConfig::user_variables`**, a per-`ClientContext` map — so the variable is
scoped to this render's connection. MEASURED in both directions and both pinned in the suite's §37: a variable set on the
pin is invisible to the NEXT render, and one set by the CALLER's session is invisible on the pin. That is why
the fixed name `params` needs no escape hatch.

⚠ `getvariable` resolves at BIND time into a `BoundConstantExpression`, taking its return type from the
value — which is what makes `getvariable('params').region` bind at all. The variable is staged when the
connection opens, i.e. before any statement of the render is prepared.

### 38.3 Lazy, and the lifetime rule that forced a copy

⚠⚠ **`FluidRenderSession.BindVariable` takes a FACTORY, and nothing is read or staged until the render's
connection is opened.** `fluid_render` is a per-ROW scalar, so binding eagerly would copy a bag for every row
of every template — including the overwhelming majority that run no SQL at all.

⚠⚠ **THE FACTORY MUST NOT CLOSE OVER ANYTHING SHORTER-LIVED THAN THE SESSION, and the surfaces split on
exactly this.** `fluid_render` and `fluid_replacement_query` render INSIDE the call that owns their arguments, so they may
slice live Arrow. The three deferred surfaces create their EXECUTION session long after `Bind`'s arguments are
freed — the same fact that makes `CaptureBag` eager — so they must close over a COPY. That is
`FluidValueModel.CopyBagRow`, a one-row slice put through an IPC round trip.

⚠ The round trip also NORMALISES the slice: a sliced Arrow array carries an offset its children do not, so an
IPC message — self-contained by construction — is what keeps the result from depending on how Apache.Arrow
happens to represent a view. **The per-row gate row is what proves it picks the right cell**; mutant 2, which
always slices row 0, passes every other row in the suite's §37 and dies exactly there.

⚠ The sliced view is deliberately NOT disposed: its buffers are the caller's, and disposing a view of an
imported array is the double-release that surfaces as an access violation inside Apache.Arrow's own release
callback (§22's measured crash).

### 38.4 The boundary, which is by design

⚠⚠ **In `fluid_replacement_query` the variable is readable from an explicit `{% query %}` / `{% exec %}` block and NOT
from the generated statement** (user-confirmed 2026-09-13: *"in fluid_replacement_query the getvariable should only work
in an explicit {%query/exec %}"*). That statement is returned as TEXT and bound by the CALLER's connection — a
different `ClientContext`, therefore a different variable map. MEASURED: `typeof` reports `"NULL"` there.
Nothing is lost — a template that GENERATES SQL interpolates its params into it directly, which is what the
template language is for. The three deferred surfaces differ precisely because WE run their generated
statement, on the same pin the blocks use.

⚠ And there IS a route through it when the params must reach a RELATION rather than a literal: stage inside
`{% exec %}` — which does see the variable — and `publish()` the result. MEASURED:

```sql
SELECT * FROM fluid_replacement_query(
  '{% exec %}CREATE TEMP TABLE p AS SELECT getvariable(''params'').region AS g{% endexec %}'
  || 'SELECT * FROM {{ publish(''p'') }}', params := {'region': 'eu'});
-- eu
```

⚠ Not separately gated: it is a composition of two mechanisms each pinned on its own (the suite's §37 block row and
its publish rows), so there is nothing here that could break without one of those failing first.

### 38.5 The JSON asymmetry, stated rather than normalised

⚠ A **JSON-string** bag stays a `VARCHAR` variable, while Liquid PARSES it so `{{ params.region }}` works in
both spellings. The bag is staged as the caller wrote it. **Prefer the STRUCT spelling** — the same advice
`fabricator_query`'s bag already carries. Pinned as a characterization in the suite's §37.

⚠⚠ **CASTING IT TO DuckDB'S `JSON` TYPE WAS CONSIDERED AND IS NOT TAKEN — and the first write-up of this gave
the WRONG reason** (*"normalising would mean inventing DuckDB types for JSON's four scalar kinds"*). That
argument is about JSON → **STRUCT**, where `1` could be INTEGER or BIGINT and `1.5` DOUBLE or DECIMAL; it says
nothing about the `JSON` type, which is VARCHAR-backed and invents nothing. Three MEASURED reasons stand in
its place, and the first is the one that decides it:

1. **It would not unify the two spellings — it would make them differ SILENTLY.** Dot access DOES work on a
   JSON value, but `getvariable('p').region` yields **`"eu"`** — a quoted JSON scalar — where the STRUCT
   spelling yields **`eu`**. So auto-casting would replace today's LOUD binder error with a value that is
   subtly wrong in string comparisons and concatenation. `->>'region'` is the unquoted accessor.
2. **`::JSON` needs the json extension**, measured: `Catalog Error: Type with name "JSON" is not in the
   catalog, but it exists in the json extension.` We cannot guarantee it on the render's pin, so the cast
   would have to be conditional — making the variable's TYPE depend on the environment, and on a `LOAD json`
   that may land BETWEEN two renders.
3. **"Preserve what the caller declared" is not available either**: DuckDB's own Arrow export DROPS
   JSON-ness. MEASURED — `fabricator_host_query` over a `::JSON` column reports `VARCHAR`, while the same
   value never leaving DuckDB reports `JSON`. A `::JSON` bag reaches us as a plain string whatever the caller
   wrote, so there is no declaration left to honour.

⚠ **The capability already exists and needs no code from us** — the author opts in, knowing their own
environment. Both MEASURED: `getvariable('params')::JSON->>'region'` inline, or one
`SET VARIABLE pj = getvariable('params')::JSON` in an `exec` block and dot access on `pj` throughout.

⚠ The bag needs no MEMBERS: a LIST is indexable as `getvariable('params')[1]` and a bare scalar arrives as a
scalar, matching the lifting the Liquid side gained when the member spread was removed (§20).

⚠ **No bag ⇒ no statement.** An unset DuckDB variable already reads as NULL, so writing one would buy nothing
and cost a round trip on every render that passed no params. `getvariable('params') IS NULL` is how SQL asks
what `{% if params %}` asks in Liquid.

---

## 39. ✅ AS BUILT (2026-09-13) — `fluid_scalar`: a template whose RETURN TYPE it declares itself

**User-designed.** The opening question was whether `fluid_query_lateral`'s constant/literal argument split
could work for a custom SCALAR — `fluid_scalar(const template, const params, arg1 … argN)` — with `is_bind`
returning the result type. It can, and the user's own refinement is what made it clean: *"in a is_bind block
i would just like to return the return type with e.g. `select null::struct<a integer>`"*.

C#-only IN THE PLUGIN: **no ABI change, no C++ change, no bridge change.** Gate `verify_plugin_fluid`
828 → **852** (the suite's §38), hermetic floor 9184 → **9208**, three mutants — two killed at their own
rows, **one deliberately recorded as a survivor**.

```sql
SELECT n, fluid_scalar(
  '{% if is_bind %}select NULL::STRUCT(a INTEGER)'
  '{% else %}select {''a'': arg_0 + 1} from input_table{% endif %}', NULL, n) FROM t;
-- {'a': 2}, {'a': 3}, …   typed STRUCT(a INTEGER), not text
```

⚠ **The template writes its own `select` and nothing is prepended for it** (user directive, 2026-09-13:
*"actually change this to use explicit selects … don't add the select part yourself"*). The `is_bind` render
is therefore a statement that can be pasted into a shell and run, which is the point of the spelling.

### 39.1 The machinery was already there — ABI v80's scalar bind session

Nothing new was needed at the ABI. `IScalarFunction.Result` is `Field?`: **null registers the function as
`ANY` and requires `Bind` to resolve a type per call site.** `ScalarBindArgs` carries the argument values
plus an `IsConstant` mask. `fabricator_parse` has shipped as the demonstration of exactly this since v80.
What `fluid_scalar` adds is that the type comes from **binding the template's own SQL** rather than from a
name the caller passes.

⚠ A scalar needs no `Params.Constant`. That style exists because a LATERAL's arguments become an input
relation, so a constant needs a channel of its own; a scalar's constants arrive for free in the mask.

### 39.2 No type ladder anywhere — the reason to prefer this design

⚠⚠ The `is_bind` render is an EXPRESSION of the result type, wrapped and described exactly the way the three
relation surfaces describe their generated statement. So the type is **whatever DuckDB binds that expression
to**: `STRUCT(a INTEGER, b VARCHAR)`, `DECIMAL(9,2)` with its scale, `INTEGER[]`, `MAP(VARCHAR, INTEGER)` —
all MEASURED intact. The rejected alternative was rendering a type NAME and parsing it, which is a second SQL
type ladder; this codebase has declined to maintain one three times.

⚠ DuckDB also names its own type back in a form that re-parses (`DESCRIBE` → `STRUCT(a INTEGER, b VARCHAR)`),
which is what the execute cast uses. It is never the template's own text.

### 39.3 Cardinality and order are the template's contract — the cost of the statement form

⚠⚠ A scalar owes exactly ONE value per input row IN INPUT ORDER, and a rendered SELECT can change both: a
join that fans out or an aggregate MISALIGNS every row, which is a wrong answer with nothing failing.

**Enforced here**: exactly ONE output column (at bind, where it is cheap to say so) and a row count equal to
the chunk's — a statement that changes cardinality is refused by name rather than producing a silent short
read. **Not enforced**: ORDER.

⚠ A plain projection over `input_table` preserves order — MEASURED, including for a correlated-subquery
expression over 3000 rows and under `SET GLOBAL preserve_insertion_order = false`, both ZERO misaligned. So
the ordinary shape is safe, and a statement that can genuinely reorder must order by a key of its OWN, passed
as an ordinary argument (`rn := row_number() over ()`, then `order by rn` — measured, 3000 rows, zero
misaligned).

⚠ An earlier build wrapped a bare EXPRESSION instead, which made 1:1 structural rather than policed. It was
changed on the user's directive; what is lost is the ordering guarantee, and what is gained is that the
template reads as SQL.

### 39.4 The cast is what makes `is_bind` a DECLARATION

⚠⚠ Without it, a template declaring `NULL::BIGINT` and rendering the literal `42` is **REFUSED**, because
`42` is INTEGER — so the author would have to write every literal's type twice and keep the two in step.
The execute wrap casts to the declared type instead, using DuckDB's own cast, which makes the declaration
authoritative. Mutant B (drop the cast) dies at exactly that row after 844 pass.

⚠ A conversion that genuinely cannot happen still fails, in DuckDB's words:
`Could not convert string 'not a number' to INT64`. ⚠ The cost is that a LOSSY but legal conversion is
silent — a DOUBLE expression under a declared BIGINT truncates — which is what a declared column type does
everywhere in SQL.

### 39.5 One render per CHUNK, and a fresh connection with it

⚠ **Per chunk, not per row** (user decision). Liquid decides the SHAPE of the computation from the constant
params; DuckDB computes every row natively. That is what makes it cheaper than `fluid_render` for per-row
work, and also why the template cannot branch on a row VALUE in Liquid — at render time there is no row. It
emits SQL that branches instead.

⚠⚠ **A pinned connection is NOT available here.** The binding is shared across pipeline threads — this
plugin already records that a volatile scalar may be evaluated on several threads at once, which is why
`FluidRenderSession` is per-render — and a DuckDB connection is single-threaded by contract. So the session
is built inside `Invoke` and disposed with it. MEASURED: 200,000 rows at `threads = 8`, zero wrong. The cost
is one connection per 2048 rows, far cheaper than the per-CALL connections every `query()` paid before v84.

### 39.6 ⚠⚠ What the alignment row does and does not prove

The wrap no longer orders — the template owns its statement — so the 5000-row alignment row passing rests on
DuckDB preserving a projection's order. That was MEASURED separately, against a build with the ordering
removed: a correlated-subquery expression over 3000 rows, and the same under
`SET GLOBAL preserve_insertion_order = false`, both ZERO misaligned.

**So do not read that row as proving ordering is enforced.** What it enforces is the ROW COUNT; order is the
template's contract, and the ordinary shape happens to be safe.

### 39.7 A bug the build found, fixed at the root

⚠⚠ `getvariable('params')` came back **NULL** inside `fluid_scalar`. `FluidRenderSession.BindVariable` only
applied its binding when `Pin()` OPENED the connection, and this function staged its input relation before
building the render context — so the connection was already open and the variable was never set. A silent
wrong value, not an error.

Fixed in `BindVariable` rather than only at the call site: it now stages immediately when the connection is
already open. The implicit "declare before you run anything" ordering contract is gone, which is the kind
that bites the next caller rather than the one who wrote it.

### 39.8 Two limitations worth knowing

⚠ The template's SQL runs on its OWN connection, so it **cannot see the caller's TEMP tables** — measured
(`Table with name lookup does not exist!`). Same rule `query()` already follows: the render's SQL reads
committed state on a separate connection. Use a regular table.

⚠ The function is VOLATILE (the default), so it is never constant-folded. That is correct — a template may
call `query()` or `exec()` — but it means DuckDB will not hoist a call whose arguments are all constant.

### 39.9 ✅ Named arguments name their `input_table` column (2026-09-13, user-asked)

*"when such a name parameter is used, can we use this name as col name in the input_table?"* — yes, and it
needed one C++ change plus its managed half. No ABI change.

```sql
SELECT i, fluid_scalar(
  '{% if is_bind %}select NULL::BIGINT'
  '{% else %}select counter * getvariable(''params'') from input_table{% endif %}',
  2, counter := i) FROM range(5000) t(i);
```

⚠⚠ **THE NAME SURVIVES AS THE ARGUMENT'S ALIAS, which is the only reason this is possible.** DuckDB's
`Transformer::TransformNamedArg` rewrites `name := expr` into `expr` with `SetAlias(name)`, so for a SCALAR
the argument stays POSITIONAL and the name rides the expression. DuckDB's own `struct_pack(a := 1)` reads it
the same way — that is the existence proof that a scalar's bind can see it.

⚠ **TAIL SLOTS ONLY**, deliberately: a declared parameter must keep its declared name because the managed
side reads those BY NAME (`ArgColumn(args, "template")`), so letting `foo := '…'` rename the template slot
would break that read at a distance.

⚠ **Bind and execute must agree**, so the resolved names ride the bind data
(`FabricatorScalarBindData.arg_names`) rather than being recomputed in the execute lambda from the
registration-time declaration — which could not know a call site's alias. A disagreement there is a wrong
COLUMN, not an error.

#### The naming rule has four cases, and only measuring showed where the line falls

| argument | staged column |
|---|---|
| `counter := i` | `counter` |
| `n`, `fs_t.n` (a column reference) | `n` |
| `n + 1`, `upper('x')`, a literal | `arg_<k>` |

⚠⚠ **DuckDB aliases a genuine COLUMN REFERENCE too** — which is why a bare `n` names itself. That was a
surprise mid-build (it broke a gate row asserting `arg_0`) and it is the better behaviour: **this rule is
strictly nicer than `fluid_query_lateral`'s**, whose wire columns are named by the rendered EXPRESSION TEXT
and so produce names like `(t.n + 1)` that must be quoted to reference. Here an argument is either
self-naming or positional; there is no unusable third case.

⚠ POSITION IS COUNTED OVER THE WHOLE TAIL, not over the unnamed ones: in
`f(tpl, NULL, counter := 1, n, n + 1, 9)` the literal is `arg_3`, not `arg_1`.

⚠ `__fab_row` is refused as an argument name — it carries the staged row number.

#### ⚠⚠ A named argument does NOT reorder, and that trap is DuckDB's

`fluid_scalar(tpl, counter := 7, 2)` binds **7 as the params bag** and 2 as the first tail argument: DuckDB
discards the name for dispatch and binds positionally. A built-in behaves identically — `upper(zzz := 'a')`
returns `A`, and `upper(any_nonsense := 'a')` does too. So naming an argument DOCUMENTS it; it never moves
it, and it is not validated against anything. Pinned as a characterization.

### 39.10 ✅ `input_table` is available at BIND, empty and fully typed (2026-09-13, user-asked)

*"similar to the fluid table functions could we make an empty input_table available at bind with the
vargs?"* — it already was, with the right column NAMES; what it lacked was the right TYPES. One managed
line. So a template can now derive its SELECT list **and its result type** from the input schema, which is
the capability §26 pins for the two relation surfaces.

```sql
-- the result type comes from the INPUT type; nothing about DECIMAL(9,2) is written in the template,
-- and there is no is_bind branch at all
SELECT typeof(fluid_scalar(
  '{% query q %}describe select * from input_table{% endquery %}'
  'select NULL::{{ q[3].column_type }} from input_table', NULL, n, d, m)) FROM fs_typed;
-- DECIMAL(9,2)
```

⚠⚠ **THE TYPES ARE REAL AT BIND, AND THAT IS SPECIFIC TO THE TAIL.** `fluid_scalar` declares an `ANY`
varargs tail, so DuckDB inserts **no cast** and the host marshals each tail argument as the EXPRESSION'S OWN
type (`fabricator_schema_entry.cpp`: *"an ANY tail gets no cast, so the expression's own type is what execute
will see"*). A concrete-typed parameter would NOT have this property — DuckDB casts it after the bind
returns, so bind and execute would legitimately differ.

⚠ A NON-CONSTANT argument has a real TYPE at bind even though its VALUE is a placeholder: the type comes from
the expression, the value from folding. Only the value is missing.

⚠⚠ **A MUTANT CORRECTED THE GATE'S OWN COMMENT, which is the useful part.** The "shape" row renders the same
text on both sides and I wrote that its passing therefore proved the two views AGREE. It does not: the OUTPUT
comes from the EXECUTE render alone, and a build with placeholder bind-time types passes it unchanged.
Mutant E (placeholder types) survives that row and dies at the one BELOW it — the derived-result-type row —
because a declared type can only come from the bind render. The comments now say which row proves what.

### 39.11 ✅ `input_table` holds the arguments and nothing else (2026-09-13, user-asked)

*"i think the __fab_row is not that useful in fluid_scalar?"* — correct, and the follow-up is the reason
why: *"it is only needed in fluid lateral"*. **A row key is a LATERAL concept.** A lateral is 1→N, so every
output row must say which input produced it or the correlated columns cannot be stamped; a scalar is 1:1 by
construction and needs no provenance at all.

What the column was doing here was narrower than it looked. The wrap stopped ordering when the template took
over writing its own `select` (§39.3), so nothing in the code used it — its only remaining purpose was as an
ordering key a re-sorting template could use. And that is self-servable: **the caller passes their own key as
an ordinary argument**, which is MEASURED to work (`rn := row_number() over ()` then `order by rn`, 3000
rows, zero misaligned) and is better than an injected one — explicit, named by the author, and absent when
unneeded. Against that it cost a reserved name, a refusal, and a column visible in every
`describe input_table`.

⚠⚠ **REMOVING IT EXPOSED A SECOND PURPOSE IT HAD BEEN SERVING SILENTLY.** With no tail arguments the staged
relation has ZERO columns, and Apache.Arrow (23.0.0) cannot represent a zero-FIELD schema across the C
interface in either direction — the same `ArgumentNullException('fields')` the zero-argument SCALAR case
already records. `input_table` also carries the chunk's ROW COUNT, so it cannot simply be omitted either: a
bare `select 42` would yield one row whatever the chunk size and fail the cardinality check.

So a `__fab_rows` placeholder now appears **only when the call has no per-row arguments** — structural, not a
row key, invisible in every call that has any. The naming-rule row is what pins that: it lists
`counter,n,arg_2,arg_3` and nothing else.

## 40. ✅ AS BUILT (2026-09-13) — a VARCHAR params bag is JSON only when it IS JSON

**User-raised**, from a real failure: `fluid_render('…', 'result')` died with *"params is not valid JSON:
'r' is an invalid start of a value"*. Their framing was the right one — *"i can pass a simple 5 or a
current_timestamp as params but a varchar is assumed to be json … it is better to allow a varchar as a
varchar"*. C#-only in the plugin; it changes every Fluid surface at once, because they share one `CaptureBag`.

### 40.1 The type cannot settle it — measured, and it was the first thing tried

The principled version of the ask is *"detect the JSON type on the arrow extension schema"*, i.e. let
`'{"x":5}'::JSON` mean JSON and a bare VARCHAR mean a string. **That is not available.** DuckDB registers
`arrow.json` as an Arrow extension type — but exports it only under `arrow_lossless_conversion`
(`arrow_converter.cpp:120`: *"we only export it as json if arrow_lossless_conversion = True"*), and this
boundary forces that OFF in `BoundaryClientProperties` for an unrelated, load-bearing reason: with it on
DuckDB exports BOOLEAN as Arrow `Int8`, and the SQL Server mapper then emits `SMALLINT` (1/0) instead of
`BIT`. That is pinned by `verify_arrow_lossless`.

⚠ MEASURED twice: a `::JSON` argument reaches the managed side indistinguishable from a plain VARCHAR. ⚠ And
the obvious suspect — our own staging rebuilding each `Field` from its type and dropping the metadata — was
tested and is NOT the cause; carrying the metadata through changed nothing.

⚠ One of those measurements was VOID and caught: the publish had failed on a file lock, so it read the stale
payload. **Verify the publish, not the exit code.**

### 40.2 So the CONTENT decides — with one exception that keeps the typo loud

A VARCHAR bag parses as JSON if it can, and is a plain string otherwise. **Except** that text beginning `{`
or `[` is plainly an object or array attempt, so a parse failure there stays an ERROR naming the rule.

That exception is the whole safety of the change. The commonest bag mistake is a malformed object — a
missing brace — and binding `'{"x": 5'` as a STRING would leave every `{{ params.x }}` rendering empty with
nothing failing. Pure sniffing would have lost that.

| bag | before | after |
|---|---|---|
| `'{"x":5}'` | object | object |
| `'{"x": 5'` | error | **error**, naming the rule |
| `'5'`, `'true'`, `'"result"'` | JSON scalar | unchanged |
| `'result'` | error | **plain string** |

⚠ Two gate rows asserting the old refusal were REPLACED rather than deleted — falsifying them is the change
announcing itself.

### 40.3 What it does not fix

⚠ The report also contained a second, unrelated thing: `getvariable('params')` in the text `fluid_render`
RETURNS is not resolvable, because `query()` runs that text on its own connection and DuckDB keeps variables
per-`ClientContext`. That is §39.4's boundary and is inherent to returning text — the variable works inside
the render's own `{% query %}` / `{% exec %}` blocks, which is what the reporter actually meant.

---

## 41. ✅ AS BUILT (2026-09-13) — the rename: `fluid_query` → `fluid_replacement_query`

BREAKING, no alias, following this repo's convention (the `IArrow*` and `fabricator_render` → `fluid_render`
precedents). The name `fluid_query` is then free for the new TABLE function of §42.

### 41.1 Why the new name is the accurate one

Today's function is an `ISqlTableFunction`, and **its call DISAPPEARS at bind**: `generate_table_sql` →
`bind_replace` → a `SubqueryRef`, so DuckDB binds the generated statement DIRECTLY and nothing crosses
Arrow at execution. Measured long ago and still true — a `fluid_replacement_query` over a table plans as a
bare `SEQ_SCAN` carrying `Projections: id` and `Filters: g=3`, the call itself gone from the plan.

"Replacement" is DuckDB's own word for that mechanism, so the name now says which of the five surfaces this
is. It also removes a standing confusion: `fluid_query_batch` / `_inout` / `_lateral` read as variants of
`fluid_query` and are nothing of the kind — they run their statement themselves, where this one hands it to
the caller's planner.

### 41.2 ⚠⚠ THE DATED RECORDS WERE UPDATED, WHICH INVERTS THIS FILE'S OWN CONVENTION — because the name is REUSED

Every previous rename here left older dated records spelling the OLD name, with one note saying so
(`fabricator_render`, the `IArrow*` set). **That is right only when the old name then denotes NOTHING.**
Here `fluid_query` immediately becomes a DIFFERENT function, so a dated record left saying `fluid_query`
would not read as "this function under its former name" — it would read as a statement about the new table
function, which is false in almost every particular (bind-time vs execute-time, no Arrow vs every row
crossing, no declared schema vs a probed one).

⇒ **when a renamed name is REUSED, sweep the historical records too.** §1.1 and §7 above therefore describe
what is now `fluid_replacement_query`, under that name, although they are dated before it existed.

### 41.3 What it cost, and the one thing that broke

~300 sites, of which exactly ONE is code (`Name => "fluid_replacement_query"`); the rest are doc comments,
the gate and the docs. Proven a pure substitution by the masking check — strip the renamed tokens out of
`git diff -U0` and every removed line is byte-identical to its added counterpart — **zero unpaired lines**.

⚠ **It silently changed an `ORDER BY`, exactly as the `fluid_render` rename did**, and the gate caught it:
the registration check selects `… IN ('fluid_render', 'fluid_replacement_query') … ORDER BY 1`, and where
`fluid_query` sorted BEFORE `fluid_render` (`q` < `r`), `fluid_replacement_query` sorts AFTER it
(`fluid_re**n**` < `fluid_re**p**`). The expected rows had to swap. A rename that merely compiles is not a
rename that passes.

⚠ The masking check needs its diff-header filter written as `^--- a/` / `^+++ b/`, not as a bare `^---`: a
sqllogictest comment line begins `--`, so in a unified diff a removed one renders as `--- …` and a lazy
filter discards it, reporting a false unpaired line.

Gate: `verify_plugin_fluid` **873, unchanged** — which is the honest outcome for a rename, and is why §41.3
rather than an assertion count is the evidence here.

---

## 42. ✅ AS BUILT (2026-09-13) — `fluid_query`: the table function that HANDS the template its filter

`fluid_query(template, params, arg0, arg1, …)` — the signature is `fluid_query_lateral`'s (user's ask). It
renders, runs the statement itself, and gives the template the scan's pushed FILTER and PROJECTION.

```sql
SELECT * FROM fluid_query(
  '{% if is_bind %}SELECT 0::BIGINT AS k, ''''::VARCHAR AS note
   {% else %}SELECT k, {{ filter_sql | sql }} AS note FROM t{% endif %}', NULL)
WHERE k > 7;
-- note = "k" > 7
```

**C#-only in the plugin plus three small host fixes** (below). NO ABI change: the global `table` kind has
carried `projection_pushdown` AND `pushdown_complex_filter` since it was written.

### 42.1 ⚠⚠ IT DOES NOT BUY "PUSHDOWN" — say so, or the feature reads as redundant

`fluid_replacement_query` DISAPPEARS at bind, so DuckDB binds the generated statement directly and its
pushdown is already FULL AND FREE — better than any hint can be. What this one buys is that **the template
SEES the predicate**. DuckDB can only push a filter through what it can see through; an aggregate, a
volatile call, a remote read or an opaque function in the generated statement all stop it. A template that
is HANDED the predicate can fold it into the thing the optimiser cannot reach into — a remote `WHERE`, a
partition choice, a narrower scan.

⇒ **reach for `fluid_replacement_query` unless the template needs to ACT on the predicate.** It is cheaper on
every axis (no Arrow crossing, no declared schema, no drift check).

### 42.2 ⚠⚠ THE MECHANISM WAS ALREADY THERE, AND MEASURING THAT FIRST IS WHAT KEPT THIS SMALL

Before writing anything, a probe against the existing `fabricator_seq` global table function with
`Fabricator.Pushdown` logging on:

```
pushdown_complex_filter: 2 expr in [(value > 3) ; (squared < 200)] -> 2 pushed;
  native_filter=["value" > 3 AND "squared" < 200]
```

So a global table function ALREADY receives the projection and the filter, and `native_filter` is a
**DuckDB-dialect predicate with quoted identifiers and inlined literals, rendered by DuckDB itself**
(`Value::ToSQLString`). Nothing had to be built at the ABI, in C++, or in the pushdown serializer. ⚠ The
callback fires MORE THAN ONCE per plan (it clears and rebuilds each time) — expected, and harmless.

### 42.3 What the template gets

| name | Fluid value | DuckDB variable |
|---|---|---|
| `filters` | list of `{ column, op, value }`, value TYPED | ✅ `STRUCT("column" VARCHAR, "op" VARCHAR, "value" VARCHAR)[]` |
| `filter_sql` | the whole predicate as DuckDB SQL | ❌ deliberately not |
| `projected` | list of column names | ✅ `VARCHAR[]` |
| `params` | as everywhere | ✅ (unchanged, §38) |

`filter_sql` is **not** a variable on purpose: it is TEXT to splice into the generated statement, which is a
Liquid job, and as a variable it could not be used as a predicate anyway (`WHERE getvariable('filter_sql')`
is a boolean test OF THE STRING). `filters` and `projected` ARE variables because they are DATA a SQL
expression can transform — which is what the user asked for ("sometimes transformation via sql is usefull").

None of the three exists at BIND: the planner has not run. A template reading them branches on `is_bind`,
the same split `projected` already had on the other relation surfaces.

### 42.4 ⚠⚠ ONLY THE TOP-LEVEL `AND` CONJUNCTS ARE OFFERED — the one correctness rule in the feature

A conjunct of the whole predicate is true of every row the query wants, so a template may narrow by it. **A
branch of an `OR` is not.** Flattening `a = 1 OR b = 2` into two entries and letting a template "restrict to
the partition a = 1" DROPS every row where `b = 2` — and the host cannot catch it, because re-applying a
predicate cannot bring back rows the template never read.

So a disjunction contributes NOTHING to `filters`, while `filter_sql` still carries it WHOLE, where splicing
it is safe. **That pairing is the design, not the empty list on its own**, and the gate asserts both halves
in one row (`n=0 sql=("k" = 1 OR "k" = 9)`). Mutant A — recurse into `or` children — dies exactly there,
after 879 pass.

A node whose constant could not be read is dropped for the same reason: a missing constant is not a
predicate against NULL.

### 42.5 ⚠⚠ THREE HOST DEFECTS IT EXPOSED, ALL LATENT, ALL FIXED HERE

Each was unreachable until a function declared the shape that reaches it.

1. **An ANY-declared parameter given a bare `NULL` could not cross at ALL — at FIVE of seven args marshals.**
   An Arrow null-typed child must report `null_count == length` and DuckDB does not set it, so Apache.Arrow
   refuses the batch with *"Length must equal null count"*. The scalar bind and the scalar execute each
   carried an INLINE copy of the patch; the table-function bind, the sqlgen bind, the in-out bind, the
   collector bind and the lateral bind did not. `fluid_query('…', NULL)` tripped the table one on its first
   call. ⇒ one shared `fabricator::FixNullTypedChildren`, called at **every** args marshal (a no-op when no
   type is SQLNULL, which is what makes "every marshal" a rule somebody can follow).

2. **The SQLNULL→ANY sentinel was honoured for NAMED table parameters and not for POSITIONAL ones.** A slot
   declared "accept any value" registered as `LogicalType::SQLNULL`, which accepts a NULL LITERAL and nothing
   else: `fluid_query(t, NULL)` bound and `fluid_query(t, {'a': 1})` was refused, with the signature printed
   back as `fluid_query(VARCHAR, "NULL")`. Purely additive to fix — `fluid_query` is the only function in the
   tree that declares one.

3. **`FabricatorExpandVarArgs` resolved ANY against the value in the TAIL and not in the PREFIX**, so even
   with (2) fixed the marshal cast to the sentinel: *"Unimplemented type for cast (STRUCT(who VARCHAR) ->
   NULL)"*. The prefix now resolves the same way the tail always has.

⚠ (2) and (3) are the same sentinel read in two places and a third — the lesson is the recorded one:
**a protocol constant honoured in some positions and not others registers a DIFFERENT function, silently.**

### 42.6 ⚠⚠ A PLUGIN CANNOT CAPTURE THE AMBIENT, SO ITS HOST WORK MUST BE EAGER

The first build created the render session inside the async iterator, as `fluid_query_batch` does, and
SIGSEGV'd at `HostFs.OpenConnection`: an iterator's body does not begin until the first batch PULL, which is
a **different ABI crossing on a different thread**, and the ambients are `AsyncLocal` per crossing. That is
the recorded rule ("a global table function must read every ambient in `Execute()`, never in the iterator")
in its third instance.

⚠ The Bridge's own readers solve it by CAPTURING — `var opener = AmbientOpener.Current` — and handing the
value to their lazy stream. **A plugin cannot: `AmbientOpener` lives in `Fabricator.Bridge`, which a plugin
deliberately does not reference.** So doing the work eagerly is not a workaround, it is the available correct
shape. ⚠ `fluid_query_batch` and `fluid_query_inout` are unaffected because their operators establish the
ambients differently; do not "harmonise" this into an iterator.

Two consequences worth keeping:

- The session is DISPOSED before a single row is read, which is safe because a host connection is
  REFERENCE-COUNTED and the result stream holds its own reference — the same property `publish()` is built
  on. The temporary catalog holding `input_table` therefore outlives the handle.
- The returned sequence is a HAND-WRITTEN enumerable, not an `async` iterator, so its `DisposeAsync` runs
  even when it was never MOVED. A compiler-generated iterator that is never pulled runs no `finally`, and a
  scan that binds without pulling (a `LIMIT 0`, a short-circuited join) would leak the open result.

### 42.7 ⚠⚠ `ProjectionPlan` MOVED TO `Fabricator.Common` — one resolver, or a SIGSEGV one edit away

`TableFunctionBindingAdapter` narrows the stream's DECLARED schema with `ProjectionPlan`, and this binding
must emit exactly that. A second derivation of "which columns, in what order" is not a wrong answer, it is
`arrow_ingest` reading past the end. It was `internal` to the Bridge, which a plugin does not reference, so
it moved to Common (it needs no host state — the membership rule).

⚠ `FilterConstants` was moved too and then **moved back**: the plugin needs its OWN value reader
(`FluidValueModel.ReadCell`, not `ArrowValueReader.ReadScalar` — §42.8), so sharing it would have been motion
with no user.

### 42.8 ⚠⚠ A SURVIVING MUTANT SHOWED THE DATE ROW WAS ASSERTING THE WRONG HALF

The values go through `FluidValueModel.ReadCell`, this plugin's documented superset — it stamps a date
`DateTimeKind.Utc`, without which a DATE renders as the PREVIOUS DAY east of UTC. Mutant B swapped in the
Bridge's `ArrowValueReader.ReadScalar` and **SURVIVED**, because the row asserted
`{{ f.value | date: "%Y-%m-%d" }}`.

MEASURED side by side on a UTC+2 box:

| reader | `{{ f.value }}` | `{{ f.value \| date: "%Y-%m-%d" }}` |
|---|---|---|
| `ReadScalar` (mutant) | `2026-09-12 22:00:00Z` ← previous day | `2026-09-13` |
| `ReadCell` (shipped) | `2026-09-13 00:00:00Z` | `2026-09-13` |

**The `date:` filter converts the error BACK through the context zone**, so the formatted half is identical
under both readers and pins nothing. Asserting the RAW value kills the mutant after 883 pass. ⚠ The correct
value is machine-INDEPENDENT (Kind=Utc renders the instant); it is the MUTANT whose output moves with the
box's zone, so this row kills east of UTC and may not west of it — stated in the suite rather than left to
be discovered.

⇒ the general form, third time in this file: **a row that renders a value through a normalising filter is
not testing the value.**

### 42.9 ⚠⚠ A MUTANT THAT SURVIVED FOR A GOOD REASON, AND THE COMMENT IT CORRECTED

Mutant C set `SupportsFilterPushdown => true` — the flag that claims "my rows are already filtered, stop
re-applying" — and **passed all 896 assertions**. Its own contract doc says why: *"NOTHING READS THIS FLAG
TODAY"*.

So the class comment claiming `false` was what kept §39.1 correct was WRONG. What keeps it correct is the
HOST: `FabricatorComplexFilterPushdown` leaves every predicate in the plan. The `false` is a statement of a
guarantee we do not make, and it becomes dropped rows the day the flag is honoured — which is a reason to
get it right by READING rather than by testing. Both the code comment and the gate's §39.1 comment were
rewritten to say that.

### 42.10 Smaller things, each measured

- **The tail arguments are bind-time constants**, staged as `input_table` under `arg_0`, `arg_1`, … —
  deterministic, unlike `fluid_query_lateral`'s wire names (rendered expression text). ⚠⚠ **A caller cannot
  rename them with `name := expr` the way `fluid_scalar` allows**: MEASURED, DuckDB reads `name := v` on a
  TABLE function as a NAMED PARAMETER and removes the value from the positional list entirely. The alias
  never reaches a table function's bind. That asymmetry is DuckDB's.
- **`params` is POSITIONAL here**, matching the lateral (the user's ask) rather than
  `fluid_replacement_query`'s `params :=`. So `fluid_query('SELECT 1')` does not bind; write
  `fluid_query('SELECT 1', NULL)`.
- **The template cannot see the caller's TEMP tables** — it runs on the render's own pinned connection, the
  same boundary `query()` and `fluid_scalar` have. Pinned, because the error ("Table with name … does not
  exist") points at the template rather than at the connection.
- **The variable's `value` is rendered BY DuckDB**, from the staged typed constant (`CAST(v<i> AS VARCHAR)`),
  not by a renderer of ours: the one we have (`DuckSql.Literal`) collapses every temporal to TIMESTAMPTZ and
  refuses a LIST or STRUCT by name. ⚠ It is the value's TEXT, not a SQL literal — a string arrives UNQUOTED.
- **An empty filter set is an empty LIST, not an unset variable** (`[]::STRUCT(…)[]`), so `len(...)` answers
  0 for every scan. The params bag goes the other way — absent means absent — because a bag has no natural
  empty value.
- A struct-member conjunct IS offered in `filters` although `filter_sql` omits it (the host emits no SQL
  twin for one, because a column-mapped table's nested children carry PHYSICAL names in storage). Safe, and
  the one place the two views disagree.

### 42.11 Two properties worth knowing, both measured

- **The filter is delivered PER EXECUTION, not frozen at bind.** ONE binding re-executed as a prepared
  statement sees the predicate follow the parameter (`"k" > 7`, then `"k" > 3`). A view re-binds and gets the
  right one each time.
- **⚠⚠ An `EXPLAIN` costs ZERO scan renders, which is a real divergence from `fluid_replacement_query` and
  the better side of it.** There every render IS the bind, so §11 measures an `EXPLAIN` of a writing template
  performing the write; here the bind render resolves the schema and the SCAN render — where a template's own
  side effects live — never happens. **So this surface has a dry run.** ⚠ The bind render still runs, so a
  side effect placed in the `is_bind` branch still fires.

Gate: `verify_plugin_fluid` 873 → **905** (§39), hermetic floor 9229 → **9261**. Four mutants: A (OR
flattened) dies after 879, B (the Bridge's value reader) after 883, D (never declare the variables) after
884; C (claim filter pushdown) SURVIVES, for the reason in §42.9.

---

## 43. ✅ AS BUILT (2026-09-14) — `fluid_aggregate`: a template rendered once per GROUP, typing itself

`fluid_aggregate(template, params, value)` — the group's rows reach the template as `rows`, and the
`is_bind` render declares the result type.

```sql
SELECT g, fluid_aggregate(
  '{% if is_bind %}select NULL::VARCHAR
   {% else %}{% capture s %}{% for r in rows %}{{ r.a }}|{{ r.b }};{% endfor %}{% endcapture %}
   select md5({{ s | sql }}){% endif %}',
  NULL, struct_pack(a := a, b := b) ORDER BY a) AS h
FROM t GROUP BY g;
```

Needed **ABI v89** — see [abi-history.md](abi-history.md) §v89 for the host half, which is most of the work.

### 43.1 ⚠⚠ THE NON-BIND RENDER IS A SELECT THAT IS EXECUTED — as on every other Fluid surface

**User-directed, and it replaced a first build that rendered TEXT and cast it** (*"the return value should be
an explicit select again which is executed like in fluid scalar, batch, inout, query … this way no cast of
the return value is needed"*). What the group's rows are FOR is the reduction, and the reduction is now
DuckDB's:

```liquid
{{ s | md5 }}        →  select md5({{ s | sql }})
```

Three things fall out, and they are why this is the better shape rather than merely the consistent one:

- **Nothing is parsed back out of text.** A STRUCT, LIST or MAP result is just a value DuckDB produced, so
  there is no intermediate representation to get wrong.
- **An empty render becomes an ERROR rather than NULL.** Under the text form those were the same thing, which
  silently conflated *"this group has nothing to say"* with a template bug. A template that wants a different
  answer for an empty group now says so: `{% if rows.size == 0 %}select NULL{% else %}…{% endif %}`.
- **The reduction may use any DuckDB function**, not only what Liquid can express.

⚠ **The CAST that remains is not the one that was removed.** `WrapExecute` still casts the statement's single
column to the declared type — that is what makes the `is_bind` render a DECLARATION rather than a thing to
match, so a template declaring `NULL::BIGINT` may render `select 42` without also writing INTEGER. What went
away is parsing a rendered STRING into the declared type.

⚠⚠ **The cost is ONE STATEMENT PER GROUP**, since the statements differ per group. MEASURED locally: ~14 µs
for a trivial statement (10 000 groups in 0.14 s) and **~0.5 ms for a real hashing template** (2 000 groups in
1.0 s, with 2 000 distinct hashes — so every statement ran). A few thousand groups is unremarkable; a hundred
thousand is not this function's shape, which its memory already said.

### 43.2 The type really does come from the template

MEASURED, one function, three call sites:

| template declares | `typeof(...)` |
|---|---|
| `select NULL::BIGINT` | `BIGINT` |
| `select NULL::DECIMAL(9,2)` | `DECIMAL(9,2)` — scale intact |
| `select NULL::STRUCT(n INTEGER, s VARCHAR)` | `STRUCT(n INTEGER, s VARCHAR)`, value `{'n': 9, 's': x}` |

No type ladder anywhere: the `is_bind` render is a SELECT DuckDB binds, and the type name the execute-time
cast splices comes from `DESCRIBE` — DuckDB rendering its own type, so it re-parses by construction. The
helper is literally `fluid_scalar`'s (`FluidScalarBinding.DescribeTypeName`, shared rather than copied).

⚠ The cast is **one statement per FINALIZE CALL** — a whole vector of groups, not one per group — and is
skipped entirely when the declared type is VARCHAR, which is the common case. So the ordinary hash template
runs with no SQL at all.

### 43.3 ⚠⚠ ORDER IS THE CALLER'S, AND FOR HASHING THAT IS THE WHOLE USABILITY QUESTION

An aggregate is unordered by definition, so a hash over `rows` repeats between runs only if the caller says
`fluid_aggregate(tpl, NULL, v ORDER BY k)`. **MEASURED that DuckDB's aggregate `ORDER BY` reaches `Update`
in order** — `ORDER BY a DESC` arrives `7,4,1` where ASC arrives `1,4,7` — which is what makes the feature
usable for the md5 case at all. Without it, parallel aggregation and combine order decide the value, and it
changes between runs with nothing failing. §40.4 is the gate row.

### 43.4 It works with GROUP BY *and* windows

The user's second question. MEASURED: `GROUP BY`, `OVER (PARTITION BY g)`, and a moving frame
(`ROWS BETWEEN 1 PRECEDING AND CURRENT ROW` reports 1, 2, 2, 2). The moving frame also pins that the binding
survives many finalizes and that `rows` is REBOUND per group — a value bound once would report the whole
partition every time.

⚠ Both of those paths go through the host's multi-group GATHER, which is where two type ladders had to be
retired (§v89). Before that, a STRUCT argument worked *ungrouped* and failed the moment a chunk held two
groups — which is the shape almost every real query has.

### 43.5 ⚠⚠ ONE per-row argument, and it is DuckDB's limit rather than a choice

A variadic tail is REFUSED on aggregates alone (`FabricatorRefuseVarArgs`): the update crossing sends a bare
`ArrowArray` with no schema and the managed side rebuilds it from the DECLARATION, so there is no
per-call-site width — and `agg_open` carries no arity. Lifting it is an ABI question and a live OOB trap in
`BuildUpdateBatch` (it loops the actual arity while indexing the declared vector).

**So several columns go in ONE `struct_pack(a := a, b := b)`, and the template reads `r.a`, `r.b`.** That is
the user's own suggestion and it is the right shape: MEASURED, a STRUCT parameter registers as
`STRUCT(a BIGINT, b VARCHAR)` and marshals correctly in both directions.

⚠ The declared STRUCT is a CONCRETE type, so a given call site is tied to one row shape —
`fluid_aggregate(tpl, NULL, t)` over a whole row fails to bind when `t`'s shape differs. The declaration is
`ANY`, so each call site's shape is resolved at bind; what cannot vary is *within* one call site.

### 43.6 Smaller things, each measured

- **A group that was never updated still renders**, with an empty `rows` — DuckDB finalizes states it never
  updated, and a template may legitimately have something to say about an empty group. It must still render a
  SELECT (§43.1).
- **The accumulator holds the group's rows**, so this is NOT spillable and a high-cardinality `GROUP BY`
  holds every group's rows in managed memory. Inherent: a reduction expressed in Liquid cannot be folded
  incrementally, because the template runs once and only at the end.
- **The template must be a CONSTANT** — the result type is part of the PLAN, so it cannot depend on a row
  value. Refused by name rather than resolved from whichever row came first.
- **The values go through `FluidValueModel.ReadCell`**, this plugin's value model, so a DATE in `rows`
  renders like a DATE in `params` rather than as the previous day east of UTC.
- ⚠ **A lock guards the render**, and it is required rather than defensive: one binding serves the whole call
  site, a `TemplateContext` is not thread-safe and a DuckDB connection is single-threaded, while DuckDB may
  finalize different vectors on different threads. UPDATE deliberately touches none of it, so the parallel
  half of aggregation stays parallel.

### 43.7 ⚠ What it buys over what already worked — state it, or the function looks redundant

`md5(string_agg(fluid_render(...), ';' ORDER BY a))` already does the headline example, measured, in both
`GROUP BY` and window form. What `fluid_aggregate` adds is that the template sees the **whole group at
once** — so it can branch on the group, emit something that is not a concatenation, and **declare a result
type that is not VARCHAR**. Where the reduction really is "render per row, then join", the existing spelling
is cheaper and should be preferred.

Gate: `verify_plugin_fluid` 905 → **946** (§40), hermetic floor 9261 → **9302**. ⚠ §40.9 REPLACES a
row asserting that an empty render was NULL — falsifying it is the change announcing itself.

### 43.8 ✅ AS BUILT (2026-09-14) — `rows` is a SQL RELATION too, so the reduction can happen in DuckDB

**User-asked** (*"can we make the rows table available at bind? then this would work: `select max(t) from
rows t`"*). It can, and their example is now the shape the function is best at:

```sql
SELECT g, fluid_aggregate('select max(t) from rows t',
                          NULL, struct_pack(a := a, b := b) ORDER BY a) AS h
FROM t GROUP BY g;
```

**⚠⚠ NOTE WHAT IS ABSENT: there is no `is_bind` BRANCH.** The result type is derived by binding that very
statement against an **EMPTY, fully typed `rows`** staged at bind — MEASURED that `DESCRIBE` over it answers
`STRUCT(a BIGINT, b VARCHAR)`. That is the property `fluid_scalar`'s empty `input_table` has (§26), arriving
here; a template only owes a `{% if is_bind %}` when its statement's type cannot be derived from the input.

**⚠⚠ IT FALSIFIES §43's OWN OPENING CLAIM, which called the text rendering FORCED.** That paragraph read:
*"an AGGREGATE's rows live in the accumulator, so there is nothing for DuckDB to evaluate them WITH"*. The
premise was true and the conclusion was not — the accumulator's rows can be handed BACK to DuckDB, which is
all staging is. It was never forced; it was unimplemented, and describing it as forced is how an
unimplemented thing becomes a permanent limitation.

#### 43.8.1 ⚠⚠ A VIEW over the registered batch, NOT a materialized copy — user-corrected, twice

The first build wrote `CREATE OR REPLACE TEMP TABLE rows AS SELECT …`, matching what **every other relation
surface does** (`fluid_query_batch`, `_inout`, `_lateral`, `fluid_query`, and
`FluidRelationInput.CreateEmptyInput` all materialize, then release the token in a `finally`). The user
pushed back twice — *"we have the rows in memory as arrow/recordbatch anyway, so mapping as a table costs
nothing"*, then *"i thought we have some factories to work around this"* — and both times they were right.

- **`RegisterRows` registers a FACTORY**: `Host.RegisterSource(token, () => new BorrowedBatchStream(schema,
  rows), schema)`. Each scan calls it and gets a FRESH cursor over the same retained Arrow, so a view over
  `fabricator_scan(token)` is **re-scannable**. MEASURED: a statement reading `rows` twice answers 4006
  (`count(*) * 1000 + sum`), where a single-use source would answer 4000.
- **⚠⚠ THE RULE I INVOKED AGAINST IT WAS ABOUT A DIFFERENT MECHANISM.** This repo records "a bound input is
  SINGLE-USE" — that is `host_query`'s `inputs`, which wrap ONE raw `ArrowArrayStream *`, a cursor. Reading
  it as covering `fabricator_scan` named sources is what sends you back to copying. **Two mechanisms, one
  sentence, opposite answers.**
- **MEASURED, same query, same data**: a 300 000-row group costs **0.002 s** through the view and **0.034 s**
  through the temp table; 2000 small groups cost 0.003 s vs 0.004 s. So the copy is pure loss on big groups
  and noise on small ones.
- **⚠ The price is a LIFETIME obligation the table did not have**: the view holds the TOKEN, not the data, so
  the registration must outlive every statement that reads it. `StagedRows` is that scope (`using`), and
  releasing early is mutation-tested — it dies at the first row that reads `rows`.

⇒ **the four sibling surfaces could plausibly drop their copies too**, and this is the evidence for it.
**✅ TAKEN for the three call-scoped ones — see §44**, which also records why `fluid_query_batch` must keep
both copies (its chunks are borrowed and freed, and `__fab_seq` needs a materialized relation) and why the
Liquid side turned out to be the bigger half.

#### 43.8.1a ⚠ What converting the siblings would and would NOT cost — and I had one of them backwards

The per-chunk surfaces divide by **when the result is pulled**, not by what the template selects. A
pass-through `select * from input_table` is fine under a view in every one of them, and so is reading the
relation twice (§43.8.1's 4006 row).

- **Call-scoped** — `fluid_scalar`, `fluid_query_lateral`, the collector — read the generated statement to
  completion inside the call, so a view needs only a `using` scope.
- **Lazily pulled** — `fluid_query_inout` (an async iterator) and `fluid_query` (it returns a live result
  stream the host pulls after `Execute` returns) — need the release tied to the STREAM's disposal rather
  than to the end of the method. That is a solved shape here, not a blocker: it is what `publish()` already
  does, holding a reference so the connection dies with the last result stream. ⚠ `fluid_query` is not worth
  converting anyway — its staged `input_table` is the tail ARGUMENTS, one row of bind-time constants.

**⚠⚠ AND THE ONE BEHAVIOUR CHANGE IS A FIX, NOT A COST — I first listed it as the price of converting, and
measuring it reversed the sign.** The case is a template doing `{% exec %}CREATE VIEW v AS SELECT * FROM
input_table{% endexec %}` and then generating `select * from v`. Under a temp table that does not error — and
it does not work either: **MEASURED that DuckDB re-binds a view at scan time, so a view created against
`input_table` in one chunk reads the NEXT chunk's rows after the `CREATE OR REPLACE`** (1, then 2). So the
capability being "lost" is one that silently answers about the wrong chunk; under a view-backed
`input_table` the same template fails loudly with an unregistered source instead. A template that really
wants the rows to outlive the chunk should ask for a copy explicitly
(`CREATE TEMP TABLE snapshot AS SELECT * FROM input_table`), which is honest about what it is doing.

#### 43.8.2 The shape: a STRUCT argument becomes COLUMNS

`UNNEST(value)`, so the SQL relation and the Liquid value agree — `{{ r.a }}` in Liquid is `a` in SQL. That
is what makes `struct_pack(a := a, b := b)`, the documented way to pass several columns to an aggregate
(§43.5), arrive as a two-column relation rather than one column of structs. A non-struct argument stays ONE
column named `value`, the parameter's own name.

⚠ MEASURED that `UNNEST` keeps the column TYPES on an EMPTY relation (which is what makes bind-time type
derivation work at all) and turns a NULL struct row into a row of NULL fields rather than dropping it.

#### 43.8.3 ⚠⚠ The staging is PER GROUP and happens at FINALIZE, not at render

Every group RENDERS before any group RUNS — DuckDB finalizes a whole vector of states, then the host asks for
the column — so a relation staged at render time would be overwritten by the next group and **every statement
would read the LAST group's rows: a wrong answer with nothing failing**. The rows therefore travel with the
statement (`GroupPlan`) and are staged immediately before it runs. Mutation-tested.

#### 43.8.4 The Liquid `rows` value is LAZY now, and the two readings cost very differently

User-asked (*"is the rows fluidvalue lazy as input_table in the other fluid functions?"*) — it was not, and a
code comment already claimed it was, which is the worse half. It is a `LazyRowsValue` over the retained
batches, so a template that reads the group only in SQL pays **no per-row boxing at all**.

⚠ It is lazy over the ARROW BATCHES rather than over the staged relation, unlike `input_table` elsewhere
(§28), and that is forced by the ordering above: at render time there is no relation yet.

**MEASURED on one 300 000-row group, the template being the only variable:**

| the template reads | time |
|---|---|
| neither `rows` in Liquid nor in SQL | **0.020 s** |
| `rows` in SQL (stages the view) | **0.034 s** → **0.002 s** once it became a view |
| `rows` in Liquid (forces the boxing) | **0.123 s** |

**⚠⚠ AND THE HEADLINE USE CASE IS TWO TO THREE ORDERS OF MAGNITUDE FASTER IN SQL.** The same md5 over 2000
groups, interleaved L/S/L/S so warm-up cannot explain it: **2.901 s / 2.591 s in Liquid** versus **0.014 s /
0.005 s in SQL** — and the two spellings agree hash-for-hash on all 50 groups of a control run. So
`select md5(string_agg(a || '|' || b, ';' ORDER BY a) || ';') from rows` is not merely the tidier way to
write §43's example, it is the way to write it.

⚠ §43.7 said the cheaper spelling for "render per row, then join" is
`md5(string_agg(fluid_render(...), ';' ORDER BY a))`. That still holds against the LIQUID form; the SQL form
inside `fluid_aggregate` is now in the same class, while keeping the whole-group view and the declared type.

Gate: `verify_plugin_fluid` 946 → **981** (§40.10), hermetic floor 9302 → **9337**. Four mutants, each killed
at its own point: no bind-time staging (947), stale group rows (955), no `UNNEST` (948), token released
before the statement (947).

## 44. ✅ AS BUILT (2026-09-14) — the call-scoped surfaces stage a VIEW and read LIVE Arrow

§43.8.1a's follow-up, taken. `fluid_scalar`, `fluid_query_lateral` and `fluid_query_inout` no longer copy
their input twice: the relation is a **view over the registered batch** instead of a materialized temp
table, and the Liquid `input_table` reads **that same batch** instead of re-querying DuckDB.

**User-directed** (*"we have the rows in memory as arrow/recordbatch anyway"* … *"the idea of arrow is to
avoid copying data.. analyse all fluid functions where you unnecessarily duplicate data"*). One shared
helper, `FluidRelationInput.StageLive`, so the rule exists ONCE rather than in three copies.

### 44.1 What each surface was paying, and what the collector is NOT

| surface | staged | Liquid `input_table` | converted |
|---|---|---|---|
| `fluid_scalar` | chunk → temp table | re-query + eager per-cell copy | **yes** |
| `fluid_query_lateral` | chunk + row id → temp table | same | **yes** |
| `fluid_query_inout` | chunk → temp table | same | **yes** |
| `fluid_query_batch` | chunk → `__fab_input` | reads the staged table | **no — and it must not be** |
| `fluid_query` (table fn) | tail ARGS → temp table | same | no: one row of constants |

**⚠⚠ THE COLLECTOR IS THE ONE THAT MUST KEEP BOTH, and the reason is not performance.** Its chunks are
BORROWED and freed as they are consumed, so the data has to leave managed memory — the copy into
`__fab_input` IS that move, not a duplicate of it — and `__fab_seq` row numbering plus exact `batchsize`
range slicing need a materialized relation. Its `input_table` was already a view over a range of it.

### 44.2 ⚠⚠ The release point is the whole difficulty, and it differs per surface

The view holds the TOKEN, not the data, so the registration must outlive every statement that reads it.
That is a lifetime the temp-table form did not have — it could release immediately, because the copy was
already made.

- **`fluid_scalar` needed NOTHING**: it already released in a `finally` after the statement, because its
  staged batch borrows the framework's columns. The conversion there is genuinely a one-word change plus
  returning the batch.
- **`fluid_query_lateral`** released eagerly inside its staging helper; the release moved to a `finally`
  around `Call`, which is safe because that method drains its result stream before returning.
- **⚠⚠ `fluid_query_inout` needed a `finally` around a `yield return` loop**, because it hands batches to
  the consumer lazily AND the consumer may ABANDON the iteration — a `LIMIT` above a streaming in-out is a
  recorded path (limitation 1.25). Releasing after the loop would leak a named source per chunk on that
  path; C# allows `yield return` inside a `try`/`finally`, which is what makes the correct shape available.

### 44.3 Measured, same data, identical answers

200 000 rows for the scalar and in-out, 100 000 correlated values for the lateral. ⚠ Only LIKE-FOR-LIKE
slots are compared: within one script the first query of each kind pays plan warm-up, which is why the
lateral's own sql-vs-liquid pair must not be read against each other.

| | old | new |
|---|---|---|
| scalar, SQL-only | 0.098 / 0.091 s | 0.085 / 0.090 s |
| scalar, touches `input_table` in Liquid | 0.241 / 0.286 s | **0.117 / 0.120 s** (~2.2×) |
| lateral, touches it in Liquid | 0.086 / 0.093 s | **0.033 s** (~2.7×) |
| in-out, touches it in Liquid | 0.402 / 0.262 s | **0.110 / 0.112 s** (~2.4–3.6×) |

⇒ **the Liquid side was the bigger half by far**, as §43.8.1a predicted: removing the round trip and the
per-cell `EagerStruct` copy beats removing the Arrow→DuckDB staging copy. The SQL-only rows move by ~5-15%.

### 44.4 ⚠ It adds no assertions, and the mutant is what establishes coverage

The change is behaviour-neutral by construction, so there is nothing new to assert about an ANSWER — the
claim is that both tiers are IDENTICAL (hermetic **76/76 — 9337**, unchanged). What needed covering is the
new LIFETIME, and it already is: a mutant releasing the token eagerly — exactly what the temp-table form
could afford — dies at an EXISTING row, `verify_plugin_fluid` line 3178 after 476 assertions.

⚠ A second property now holds that did not before and is gated on the aggregate rather than here: the
Liquid value and the SQL relation are the SAME batch, so they cannot disagree. `BindLazyRelation` re-queried
DuckDB and could in principle have answered about something else.

## Appendix — records moved verbatim from CLAUDE.md (2026-09-18)

CLAUDE.md carried these as-built records inline until it grew to 10,776 lines — a file loaded into every
session's context. They are moved here VERBATIM; CLAUDE.md keeps each entry's summary head plus a pointer to
this section. The one edit made on the way: a link that pointed into the docs directory is rewritten relative
to this directory, so it still resolves from here.

- **⚠⚠ THE CALL-SCOPED FLUID SURFACES STOPPED COPYING THEIR INPUT TWICE — BUILT 2026-09-14 (user-directed:
  *"we have the rows in memory as arrow/recordbatch anyway"* … *"the idea of arrow is to avoid copying
  data.. analyse all fluid functions where you unnecessarily duplicate data"*). C#-only in the plugin, NO
  ABI change. `fluid_scalar`, `fluid_query_lateral` and `fluid_query_inout` now stage a VIEW over the
  registered batch instead of a temp table, and bind the Liquid `input_table` over THAT SAME batch instead
  of re-querying DuckDB. ONE shared helper (`FluidRelationInput.StageLive`). Full record:
  [docs/fluid-templating.md](fluid-templating.md) §44.**
  - **⚠⚠ THE LIQUID SIDE WAS THE BIGGER HALF, which is not where I would have looked.** `BindLazyRelation`
    ran `SELECT * FROM input_table` and built an `EagerStruct` PER ROW whose constructor copies EVERY CELL
    into a `FluidValue` — a round trip plus a full CLR materialization, for data still alive in Arrow one
    frame up. MEASURED (identical answers, like-for-like slots): a template touching `input_table` in Liquid
    goes **0.241/0.286 → 0.117/0.120 s** (scalar), **0.086/0.093 → 0.033 s** (lateral), **0.402/0.262 →
    0.110/0.112 s** (in-out). SQL-only rows move ~5-15%.
  - **⚠ `EagerStruct` IS RIGHT WHERE IT LIVES** — `FluidHostQuery.ReadRows` disposes each batch as it
    consumes it, so a row there CANNOT hold the arrays. `ArrowStruct` (3 refs + an int, reading members from
    the live Arrow on access) is available only where the batch outlives the render, which is exactly the
    call-scoped surfaces' own chunk. Do not "unify" them.
  - **⚠⚠ `fluid_query_batch` MUST KEEP BOTH COPIES and it is not a perf trade**: its chunks are BORROWED and
    freed as consumed, so the copy into `__fab_input` IS the move out of managed memory rather than a
    duplicate of it, and `__fab_seq` + exact `batchsize` slicing need a materialized relation. ⇒ **the
    premise that the collector "collects all input and copies it again" is wrong** — it never collects into
    managed memory at all, and its `input_table` was already a view.
  - **⚠⚠ THE RELEASE POINT IS THE WHOLE DIFFICULTY and it differs per surface.** A view holds the TOKEN, not
    the data, so the registration must outlive every statement reading it — a lifetime the temp-table form
    did not have. `fluid_scalar` needed NOTHING (it already released in a `finally` after its statement);
    the lateral's release moved out of its staging helper into a `finally` around `Call`; and **the in-out
    needed a `finally` around a `yield return` LOOP**, because it yields lazily and a consumer may ABANDON
    the iteration (a `LIMIT` above a streaming in-out — limitation 1.25). Releasing after the loop would
    leak a named source per chunk on that path.
  - **⚠ IT ADDS NO ASSERTIONS, AND A MUTANT IS WHAT ESTABLISHES COVERAGE.** Behaviour-neutral by
    construction, so the claim is that both tiers are IDENTICAL (hermetic **76/76 — 9337**, unchanged). The
    new LIFETIME is already covered: releasing the token eagerly dies at an EXISTING row
    (`verify_plugin_fluid` line 3178, after 476).
  - ⚠ `fluid_query` (the table function) was deliberately NOT converted — its staged `input_table` is the
    tail ARGUMENTS, one row of bind-time constants, so there is nothing to save.

- **⚠⚠ `fluid_aggregate` — A TEMPLATE RENDERED ONCE PER GROUP, WHOSE RESULT TYPE IT DECLARES. BUILT
  2026-09-14 (user-asked, after an analysis pass: *"a fluid_aggregate must at bind be able to supply the
  return_type! If it is possible then lets build it"*). It IS possible, for one structural reason:
  **`agg_open` is already called from DuckDB's aggregate BIND**, so a session exists per CALL SITE. ABI
  **v89** teaches that entry to carry the call. Gate `verify_plugin_fluid` 905 → **946** (§40), hermetic
  floor 9261 → **9302**, four mutants each killed at its own row. Full records:
  [docs/abi-history.md](abi-history.md) §v89 + [docs/fluid-templating.md](fluid-templating.md)
  §43.**
  - **⚠⚠ THE NON-BIND RENDER IS A SELECT THAT IS EXECUTED, like every other Fluid surface — and this
    REPLACED a first build that rendered TEXT and cast it (user-directed the same day: *"the return value
    should be an explicit select again which is executed like in fluid scalar, batch, inout, query … this
    way no cast of the return value is needed"*).** `{{ s | md5 }}` became `select md5({{ s | sql }})`.
    Three things fall out and they are why it is BETTER rather than merely consistent: nothing is parsed
    back out of text (a STRUCT/LIST/MAP result is just a value DuckDB produced); an EMPTY render becomes an
    ERROR rather than NULL, which un-conflates *"this group has nothing to say"* from a template bug; and
    the reduction may use any DuckDB function rather than only what Liquid expresses.
    - **⚠ THE CAST THAT REMAINS IS NOT THE ONE THAT WENT.** `WrapExecute` still casts the statement's single
      column to the declared type — `fluid_scalar`'s, and its own comment records why: it is what makes the
      `is_bind` render a DECLARATION rather than a thing to MATCH, so a template declaring `NULL::BIGINT`
      may render `select 42` without also writing INTEGER. What went is parsing a rendered STRING.
    - **⚠⚠ THE COST IS ONE STATEMENT PER GROUP and it is MEASURED rather than guessed — I first wrote
      "~1 ms" from a guess and had to correct it.** Locally: ~14 µs for a trivial statement (10 000 groups
      in 0.14 s) and **~0.5 ms for a real hashing template** (2 000 groups in 1.0 s, with 2 000 DISTINCT
      hashes, which is what proves every statement ran rather than being deduplicated). ⇒ a few thousand
      groups is unremarkable; a hundred thousand is not this function's shape — which its MEMORY already
      said, so the two limits agree.
  - **⚠⚠ `rows` IS A SQL RELATION TOO, SO THE REDUCTION CAN HAPPEN IN DuckDB — user-asked 2026-09-14
    (*"can we make the rows table available at bind? then this would work: `select max(t) from rows t`"*).
    Gate `verify_plugin_fluid` 946 → **981** (§40.10), floor 9302 → **9337**, four mutants. Full record:
    [docs/fluid-templating.md](fluid-templating.md) §43.8.**
    - **⚠⚠ IT FALSIFIES THIS ENTRY'S OWN FIRST CLAIM, which called the text rendering FORCED**: *"an
      aggregate's rows live in the accumulator, so there is nothing for DuckDB to evaluate them WITH."* The
      premise was true, the conclusion was not — the accumulator's rows can be handed BACK to DuckDB, which
      is all staging is. It was never forced, it was unimplemented; **describing an unimplemented thing as
      forced is how it becomes a permanent limitation.**
    - **⚠⚠ THE USER'S EXAMPLE NEEDS NO `is_bind` BRANCH AT ALL**, which is the point: an EMPTY, fully typed
      `rows` is staged at BIND, so `DESCRIBE` over `select max(t) from rows t` answers
      `STRUCT(a BIGINT, b VARCHAR)` — `fluid_scalar`'s empty-`input_table` property (§26) arriving here.
    - **⚠⚠ A VIEW OVER THE REGISTERED BATCH, NOT A MATERIALIZED COPY — and I built the copy first, matching
      four sibling surfaces, then was corrected TWICE.** `RegisterRows` registers a **FACTORY**
      (`() => new BorrowedBatchStream(schema, rows)`), so each scan gets a FRESH cursor over the same
      retained Arrow ⇒ a view is re-scannable. **MEASURED: a statement reading `rows` twice answers 4006
      where a single-use source answers 4000**; and the same query costs **0.002 s (view) vs 0.034 s
      (table)** on a 300k-row group. ⚠ **The rule I invoked against it is about a DIFFERENT MECHANISM** —
      "a bound input is SINGLE-USE" is `host_query`'s `inputs`, one raw stream; `fabricator_scan` named
      sources are factories. One sentence, two mechanisms, opposite answers.
      - ⇒ **the four sibling surfaces could plausibly drop their copies too** (`fluid_query_batch` cannot —
        it row-numbers and range-slices), and this is the evidence. Deliberately NOT done here.
      - ⚠ The price is a LIFETIME the table did not have: the view holds the TOKEN, not the data, so the
        registration must outlive every statement reading it (`StagedRows`). Releasing early is mutated.
    - **⚠⚠ STAGED PER GROUP AT FINALIZE, NOT AT RENDER — every group renders BEFORE any group runs**, so a
      relation staged at render time leaves every statement reading the LAST group's rows: a wrong answer
      with nothing failing. The rows travel with the statement (`GroupPlan`). Mutation-tested.
    - **A STRUCT argument is EXPANDED into COLUMNS** (`UNNEST`), so `{{ r.a }}` in Liquid is `a` in SQL —
      which is what makes `struct_pack` (the documented multi-column idiom) arrive as a real relation. A
      non-struct argument stays ONE column named `value`. ⚠ MEASURED that `UNNEST` keeps column TYPES on an
      EMPTY relation (what makes the bind probe work) and turns a NULL struct into NULL fields.
    - **⚠⚠ AND THE HEADLINE USE CASE IS TWO TO THREE ORDERS OF MAGNITUDE FASTER IN SQL.** The same md5 over
      2000 groups, interleaved L/S/L/S: **2.901 / 2.591 s in Liquid vs 0.014 / 0.005 s in SQL**, agreeing
      hash-for-hash on a 50-group control. ⇒ the SQL spelling is not tidier, it is the one to write.
    - **⚠ The Liquid `rows` value is LAZY now** (user-asked: *"is the rows fluidvalue lazy as input_table in
      the other fluid functions?"* — it was not, **and a code comment already claimed it was**, which is the
      worse half). MEASURED on one 300k group, the template the only variable: reads NEITHER **0.020 s**,
      reads it in SQL **0.002 s**, reads it in LIQUID **0.123 s**. ⚠ Lazy over the ARROW BATCHES, not over
      the staged relation as `input_table` is (§28) — forced by the ordering above.
  - **⚠⚠ DuckDB PASSES `AggregateFunction` BY VALUE INTO THE BIND and moves the mutated copy into the
    `BoundAggregateExpression`** (`FunctionBinder::BindAggregateFunction`), so `SetReturnType` there really
    becomes the expression's type. Our bind had been receiving `vector<unique_ptr<Expression>> &arguments`
    all along and IGNORING it — the capability was one parameter away the whole time.
  - **MEASURED, one function, three call sites**: `select NULL::BIGINT` ⇒ BIGINT, `select
    NULL::DECIMAL(9,2)` ⇒ DECIMAL(9,2) with its scale, `select NULL::STRUCT(n INTEGER, s VARCHAR)` ⇒ that
    struct, value `{'n': 9, 's': x}`. No type ladder anywhere: the `is_bind` render is a SELECT DuckDB
    binds, and the cast's type NAME comes from `DESCRIBE` (DuckDB rendering its own type, so it re-parses by
    construction). The helper is literally `fluid_scalar`'s, SHARED rather than copied.
  - **⚠⚠ ONE PER-ROW ARGUMENT, AND THE USER'S OWN SUGGESTION IS THE RIGHT SHAPE.** A variadic tail is
    REFUSED on aggregates alone: the update crossing sends a bare `ArrowArray` whose schema the managed side
    rebuilds from the DECLARATION, so there is no per-call-site width, and `agg_open` carries no arity.
    Several columns go in ONE `struct_pack(a := a, b := b)`; MEASURED that a STRUCT parameter registers as
    `STRUCT(a BIGINT, b VARCHAR)` and marshals in both directions. ⚠ The declared STRUCT is CONCRETE per
    call site (the declaration is ANY, resolved at bind), so one call site is tied to one row shape.
  - **⚠⚠ ORDER IS THE CALLER'S, AND FOR HASHING THAT IS THE WHOLE USABILITY QUESTION.** An aggregate is
    unordered by definition, so a hash over `rows` repeats between runs only with
    `fluid_aggregate(tpl, NULL, v ORDER BY k)`. **MEASURED that DuckDB's aggregate `ORDER BY` reaches
    `Update` IN ORDER** — DESC arrives `7,4,1` where ASC arrives `1,4,7` — which is what makes the md5 case
    work at all. Without it, parallel aggregation and combine order decide the value, silently. §40.4.
  - **BOTH of the user's questions answered YES, measured**: `GROUP BY`, `OVER (PARTITION BY g)`, and a
    moving frame (`ROWS BETWEEN 1 PRECEDING AND CURRENT ROW` ⇒ 1, 2, 2, 2). ⚠ Both go through the host's
    MULTI-GROUP gather, which is where the ladders below had to go — before that a STRUCT argument worked
    UNGROUPED and failed the moment a chunk held two groups, i.e. on the shape almost every real query has.
  - **⚠⚠ THE THIRD PLACE THE SQLNULL SENTINEL HAD TO BE TAUGHT, one day after the first two.** An
    "accept any value" PARAMETER registered as SQLNULL takes a NULL LITERAL and nothing else — printed back
    as `fluid_aggregate(VARCHAR, "NULL", "NULL")` refusing a `struct_pack`. ⇒ **a protocol constant honoured
    in some positions and not others registers a DIFFERENT function, silently.** And the bind must hand the
    RESOLVED types on: `BuildUpdateBatch` types the update columns from them while `AggregateSession`
    rebuilt its batch schema from the declaration, so an ANY parameter had the two sides disagreeing — a
    STRUCT argument marshalled as a NULL-typed column. Both now read the same resolved types.
  - **⚠⚠ `agg_open` NEEDED THE CALL CONTEXT, NOT JUST THE ARGUMENTS — and the symptom names it: THE FIRST
    AGGREGATE IN A STATEMENT WORKED AND THE SECOND DID NOT.** A bind that touches the host (to resolve a
    type, which is the point) opens a connection, and the aggregate path establishes no ambients anywhere:
    `host_connection_open failed: Attempted to dereference unique_ptr that is NULL`. That
    first-works-second-fails shape IS the signature of inheriting whatever the last crossing left. The
    managed handler now establishes and RESTORES the scope (`CallScope`), as `scalarfn_bind` does.
  - **⚠⚠ UPDATE / COMBINE / FINALIZE STILL CARRY NO CONTEXT, and that is load-bearing rather than a gap to
    close casually: a binding needing host access must acquire it AT BIND and keep it.** `fluid_aggregate`
    creates its render session there and holds it for the call site (one connection per call site, and NONE
    is opened for a pure-Liquid template since the session is lazy about its connection);
    `AggregateSession.Close` disposes the binding at `agg_close`, the only teardown signal there is. Same
    conclusion `fluid_query` reached hours earlier — **a plugin cannot capture the ambient, because
    `AmbientOpener` lives in `Fabricator.Bridge`, which a plugin deliberately does not reference.**
  - **TWO HAND-WRITTEN TYPE LADDERS RETIRED, and an ANY-declared argument is why neither could ever be
    right**: the type is whatever the CALL SITE passed. `GatherRows` (splitting one update batch across
    groups) refused everything outside eight primitives and now falls back to slice-each-row +
    `ArrowArrayConcatenator.Concatenate`; ⚠ `NullArray` keeps an explicit case because the concatenator
    refuses it (*"Concatenation for null is not supported yet"*) and a NULL-typed column is ORDINARY here.
    `BuildResultColumn` could not build a STRUCT/LIST/MAP at all, so `IAggregateBinding.FinalizeColumn` lets
    a binding hand over the column DuckDB's own CAST produced.
  - **⚠⚠ THE `Field` → `Field?` TRAP FIRED AGAIN, IN THE SAME CROSSING AS v80.** Making
    `IAggregateFunction.Result` nullable produced CS8602 at two inventory sites, one of them inside
    `list_global_functions` — where a single throw drops **EVERY** global function. The symptom was
    `fluid_aggregate does not exist` with `fluid_render` gone too, three layers from the cause. ⚠ And the
    warnings are invisible on an up-to-date project: **`dotnet build --no-incremental` is what shows them**,
    which is exactly what v80's record says and what I had to rediscover.
  - **⚠ WHAT IT BUYS OVER WHAT ALREADY WORKED — say it, or the function reads as redundant.**
    `md5(string_agg(fluid_render(...), ';' ORDER BY a))` already does the headline example, MEASURED, in
    both GROUP BY and window form. `fluid_aggregate` adds that the template sees the WHOLE GROUP at once —
    so it can branch on the group, emit something that is not a concatenation, and declare a result type
    that is not VARCHAR. Where the reduction really is "render per row, then join", the existing spelling is
    cheaper and should be preferred.
  - ⚠ Smaller measured things: a group that was never updated still renders, with an empty `rows` (DuckDB
    finalizes states it never updated) and must still render a SELECT — `{% if rows.size == 0 %}select
    NULL{% else %}…{% endif %}` is how it gets its own answer; the accumulator HOLDS the
    group's rows, so this is NOT spillable and a high-cardinality GROUP BY keeps every group's rows in
    managed memory (inherent — a Liquid reduction cannot be folded incrementally); the template must be a
    CONSTANT, since the result type is part of the PLAN; values go through `FluidValueModel.ReadCell`, so a
    DATE in `rows` renders like a DATE in `params`; and a LOCK guards the render because one binding serves
    the whole call site while DuckDB may finalize different vectors on different threads — UPDATE
    deliberately touches none of it, so the parallel half of aggregation stays parallel.

- **⚠⚠ `fluid_query` IS NOW AN ORDINARY TABLE FUNCTION AND THE OLD ONE IS `fluid_replacement_query` —
  BUILT 2026-09-13 (user-designed), TWO commits: the BREAKING rename, then the new function. The new one
  runs its own statement and HANDS THE TEMPLATE the scan's pushed FILTER and PROJECTION, as Fluid values and
  as DuckDB variables. C#-only in the plugin apart from THREE latent host defects it exposed (below); NO ABI
  change. Gate `verify_plugin_fluid` 873 → **905**, hermetic floor 9229 → **9261**, four mutants — and TWO
  of them are the useful ones because one SURVIVED and one killed the wrong thing. Full record:
  [docs/fluid-templating.md](fluid-templating.md) §41 (the rename) + §42 (as built).**
  - **⚠⚠ IT DOES NOT BUY "PUSHDOWN", AND SAYING SO IS THE FIRST THING THE DOCS HAD TO DO.**
    `fluid_replacement_query` DISAPPEARS at bind (`generate_table_sql` → `bind_replace` → a `SubqueryRef`),
    so DuckDB binds the generated statement DIRECTLY and its pushdown is already FULL AND FREE — better than
    any hint can be. What the new one buys is that the TEMPLATE SEES the predicate: DuckDB can only push
    through what it can see through, and an aggregate, a volatile call, a remote read or an opaque function
    in the generated statement all stop it. ⇒ **prefer the replacement form unless the template needs to ACT
    on the predicate** (a remote WHERE, a partition choice, a narrower scan).
  - **⚠⚠ THE MECHANISM WAS ALREADY THERE, AND PROBING IT FIRST IS WHAT KEPT THIS SMALL.** Before writing
    anything, `fabricator_seq` under `Fabricator.Pushdown` logging: *"2 expr in [(value > 3) ; (squared <
    200)] -> 2 pushed; native_filter=["value" > 3 AND "squared" < 200]"*. So a GLOBAL `table`-kind function
    already receives both channels (`fabricator_schema_entry.cpp:3457,3479`), and `native_filter` is a
    **DuckDB-dialect predicate with quoted identifiers and inlined literals rendered by DuckDB itself**
    (`Value::ToSQLString`). Nothing was needed at the ABI, in the serializer, or in the C++ scan.
  - **THE SURFACE**: `filters` (top-level conjuncts as `{column, op, value}`, value TYPED) and `projected`
    are BOTH a Fluid value and a DuckDB variable; `filter_sql` (the whole predicate) is a Fluid value ONLY —
    deliberately, since it is TEXT to splice and `WHERE getvariable('filter_sql')` is a boolean test OF THE
    STRING. None exists at BIND (the planner has not run), so a template reading them branches on `is_bind`.
  - **⚠⚠ ONLY THE TOP-LEVEL `AND` CONJUNCTS ARE OFFERED — the one correctness rule, and mutant A's row.**
    A branch of an `OR` is NOT true of every row, so flattening `a = 1 OR b = 2` and letting a template
    "restrict to the partition a = 1" DROPS the `b = 2` rows — and the host CANNOT catch it, because
    re-applying a predicate cannot bring back rows the template never read. A disjunction contributes
    NOTHING to `filters` while `filter_sql` carries it WHOLE; **the pairing is the design, not the empty
    list**, and the gate asserts both halves in one row.
  - **⚠⚠ THREE LATENT HOST DEFECTS IT EXPOSED, all fixed here, each unreachable until a function declared
    the shape that reaches it:**
    1. **An ANY-declared parameter given a bare `NULL` could not cross AT ALL, at FIVE of seven args
       marshals.** An Arrow null-typed child must report `null_count == length` and DuckDB does not set it
       (*"Length must equal null count"*). The scalar bind and scalar execute each carried an INLINE copy of
       the patch; the table-function bind, the sqlgen bind, the in-out bind, the collector bind and the
       lateral bind did not. ⇒ ONE shared `fabricator::FixNullTypedChildren` called at EVERY args marshal —
       a no-op when no type is SQLNULL, which is what makes "every marshal" a rule somebody can follow.
    2. **The SQLNULL→ANY sentinel was honoured for NAMED table parameters and not for POSITIONAL ones**, so
       a slot declared "accept any value" registered as `LogicalType::SQLNULL` — accepting a NULL LITERAL and
       nothing else (`fluid_query(t, {'a':1})` refused, signature printed back as `fluid_query(VARCHAR,
       "NULL")`). Purely additive: `fluid_query` is the ONLY function in the tree declaring one.
    3. **`FabricatorExpandVarArgs` resolved ANY against the value in the TAIL and not in the PREFIX**, so
       even with (2) fixed the marshal cast to the sentinel (*"Unimplemented type for cast (STRUCT(who
       VARCHAR) -> NULL)"*).
    ⇒ (2) and (3) are one sentinel read in two places: **a protocol constant honoured in some positions and
    not others registers a DIFFERENT function, silently** — the rule `FabricatorRefuseVarArgs` already states
    for a missing `switch` case.
  - **⚠⚠ A PLUGIN CANNOT CAPTURE THE AMBIENT, SO ITS HOST WORK MUST BE EAGER — and the first build SIGSEGV'd
    proving it.** Creating the render session inside the async iterator (as `fluid_query_batch` does) died at
    `HostFs.OpenConnection`: an iterator's body begins at the first batch PULL, a DIFFERENT ABI crossing on a
    different thread, and the ambients are `AsyncLocal` per crossing. That is the recorded rule in its THIRD
    instance. ⚠ The Bridge's own readers capture (`var opener = AmbientOpener.Current`) and hand the value to
    their lazy stream; **`AmbientOpener` lives in `Fabricator.Bridge`, which a plugin deliberately does not
    reference**, so eager is not a workaround but the available correct shape. Do not "harmonise" it back.
    - ⚠ Two consequences kept: the session is DISPOSED before a row is read (safe — a host connection is
      REFERENCE-COUNTED and the result stream holds its own reference, the property `publish()` rests on), and
      the returned sequence is a HAND-WRITTEN enumerable so its `DisposeAsync` runs even when never MOVED (a
      compiler-generated iterator that is never pulled runs no `finally`, and a `LIMIT 0` above it would leak
      the open result).
  - **⚠ `ProjectionPlan` MOVED TO `Fabricator.Common` (public).** The host narrows the DECLARED stream schema
    with it and this binding must emit exactly that; a second derivation of "which columns, in what order" is
    not a wrong answer but `arrow_ingest` reading past the end. ⚠ `FilterConstants` was moved too and MOVED
    BACK — the plugin needs its OWN value reader, so sharing it would have been motion with no user.
  - **⚠⚠ MUTANT B SURVIVED FIRST AND SHOWED THE DATE ROW WAS ASSERTING THE WRONG HALF.** Values go through
    `FluidValueModel.ReadCell` (this plugin's superset — it stamps `DateTimeKind.Utc`), not the Bridge's
    `ArrowValueReader.ReadScalar`. Swapping them passed, because the row asserted
    `{{ f.value | date: "%Y-%m-%d" }}`. MEASURED side by side on a UTC+2 box: the Bridge reader gives
    **`raw=[2026-09-12 22:00:00Z]`** — the PREVIOUS DAY — and the `date:` filter converts it BACK through the
    context zone to `2026-09-13`, identical under both. ⇒ **a row that renders a value through a NORMALISING
    filter is not testing the value.** Asserting the RAW value kills it after 883. ⚠ The correct value is
    machine-INDEPENDENT; it is the MUTANT whose output moves with the box's zone, so that row kills east of
    UTC and may not west of it — stated in the suite rather than left to be discovered.
  - **⚠⚠ MUTANT C SURVIVED FOR A GOOD REASON AND CORRECTED A COMMENT I HAD JUST WRITTEN.** Setting
    `SupportsFilterPushdown => true` — the flag claiming "my rows are already filtered, stop re-applying" —
    passes all 896 assertions, because **nothing reads it** (its own contract doc says so, and I had written
    the opposite one file away). What keeps the safety row correct is the HOST leaving every predicate in the
    plan. ⇒ the `false` is a statement of a guarantee we do not make, and it becomes dropped rows the day the
    flag is honoured — **a reason to get it right by READING rather than by testing.** Both the code comment
    and the gate's §39.1 comment were rewritten to say that.
  - **⚠ THE RENAME (§41) SWEPT THE DATED RECORDS, WHICH INVERTS THIS REPO'S CONVENTION — because the name is
    REUSED.** Every previous rename here left older dated records spelling the OLD name; that is right only
    when the old name then denotes NOTHING. Here `fluid_query` immediately became a DIFFERENT function, so a
    dated record left saying `fluid_query` would read as a statement about the table function and be false in
    almost every particular. ⚠ It also silently changed an `ORDER BY` (the `fluid_render` rename's trap,
    exactly): `fluid_query` sorted BEFORE `fluid_render`, `fluid_replacement_query` sorts AFTER it.
  - ⚠ Smaller measured things: the tail args are bind-time constants staged as `arg_0`, `arg_1`, … and
    **CANNOT be renamed with `name := v`** (DuckDB reads that as a NAMED PARAMETER on a table function and
    removes the value from the positional list — so `fluid_scalar`'s alias rule does NOT transfer); `params`
    is POSITIONAL here, matching the lateral, so `fluid_query('SELECT 1')` does not bind; the template cannot
    see the caller's TEMP tables (its own pinned connection — pinned in the gate, because the error names the
    template rather than the boundary); the variable's `value` is rendered BY DuckDB from the staged typed
    constant (`CAST(v<i> AS VARCHAR)`) rather than by `DuckSql.Literal`, which collapses temporals and
    refuses LIST/STRUCT; and an empty filter set is an empty LIST (`[]::STRUCT(…)[]`), not an unset variable,
    so `len(...)` answers 0 for every scan.

- **⚠⚠ A VARCHAR PARAMS BAG IS JSON ONLY WHEN IT *IS* JSON — BUILT 2026-09-13 (user-raised from a real
  failure: `fluid_render('…','result')` died with "params is not valid JSON"). C#-only in the plugin, and it
  changes EVERY Fluid surface at once because they share one `CaptureBag`. Gate `verify_plugin_fluid`
  867 → **873**, floor 9223 → **9229**. Full record:
  [docs/fluid-templating.md](fluid-templating.md) §40.**
  - **THE INCONSISTENCY IT REMOVES, and the user's framing was the right one**: `5`, `current_timestamp` and
    a BLOB all worked as bare scalar bags while VARCHAR ALONE was reinterpreted as JSON.
  - **⚠⚠ THE PRINCIPLED VERSION — "detect the json type on the arrow extension schema" — IS NOT AVAILABLE,
    and that is worth knowing before anyone proposes it again.** DuckDB registers `arrow.json`, but exports
    it ONLY under `arrow_lossless_conversion` (`arrow_converter.cpp:120`: *"we only export it as json if
    arrow_lossless_conversion = True"*), and `BoundaryClientProperties` forces that OFF for an unrelated,
    LOAD-BEARING reason — with it on, DuckDB exports BOOLEAN as Arrow `Int8` and the SQL Server mapper emits
    `SMALLINT` (1/0) instead of `BIT`, which `verify_arrow_lossless` pins. **MEASURED: a `::JSON` bag reaches
    the managed side indistinguishable from VARCHAR.**
    - ⚠ The obvious suspect — our own staging rebuilding each `Field` from its TYPE and dropping the metadata
      where `ARROW:extension:name` lives — was TESTED and is not the cause: carrying the metadata through
      changed nothing. It is DuckDB's export.
    - ⚠ One of those measurements was VOID and caught: the publish had FAILED on a file lock (a leftover
      `duckdb.exe`), so it read the stale payload and "confirmed" the result. **Verify the publish succeeded,
      never the exit code of the shell around it.**
  - **⚠⚠ SO THE CONTENT DECIDES — WITH ONE EXCEPTION THAT IS THE WHOLE SAFETY OF THE CHANGE.** Text beginning
    `{` or `[` is plainly an object/array attempt, so a parse failure THERE stays an ERROR naming the rule.
    The commonest bag mistake is a malformed object (a missing brace), and binding `'{"x": 5'` as a STRING
    would leave every `{{ params.x }}` rendering empty with nothing failing. **Pure sniffing — which is what
    was proposed — would have lost that**; the guard is one condition.
  - ⚠ Every spelling that worked still means what it did (`'{"x":5}'`, `'[7,8]'`, `'5'`, `'true'`,
    `'"result"'`), and `'5'` is gated with `| plus: 1` because only arithmetic separates a NUMBER from the
    string "5". TWO gate rows asserting the old refusal were REPLACED, not deleted.
  - ⚠ It does NOT fix the OTHER half of the same report: `getvariable('params')` in the text `fluid_render`
    RETURNS is unresolvable, because `query()` runs that text on its own connection (§39.4's boundary,
    inherent to returning text). The variable works inside the render's own blocks, which is what the
    reporter actually meant.

- **⚠⚠ `fluid_scalar` — A TEMPLATE RENDERED TO A SQL SELECT, WHOSE RETURN TYPE THE TEMPLATE DECLARES.
  BUILT 2026-09-13 (user-designed). C#-only IN THE PLUGIN: NO ABI change, NO C++ change, NO bridge change.
  Gate `verify_plugin_fluid` 828 → **865** (§38), hermetic floor 9184 → **9221**, three mutants run against
  the earlier expression form (two killed at their own rows; the ordering one SURVIVED, which is what showed
  the ORDER BY was never load-bearing and made the statement form a cheaper trade than it looked). Full record:
  [docs/fluid-templating.md](fluid-templating.md) §39.**
  `fluid_scalar(template, params, arg_0 … arg_N)`; under `is_bind` the template renders a SELECT of the
  result type (`select NULL::STRUCT(a INTEGER)`), otherwise the SELECT that computes it over `input_table`.
  - **⚠⚠ IT NEEDED NOTHING NEW AT THE ABI — v80's scalar bind session already does this, and `fabricator_parse`
    is its shipped demonstration.** `IScalarFunction.Result` is `Field?` (null ⇒ registered as ANY, the bind
    must supply a type) and `ScalarBindArgs` carries the values plus an `IsConstant` MASK. ⚠ A scalar needs no
    `Params.Constant` — that style exists because a LATERAL's args become an input relation; a scalar's
    constants arrive in the mask for free.
  - **⚠⚠ NO TYPE LADDER ANYWHERE, and that is what the user's design bought.** My first scoping had the
    template render a TYPE NAME which we would parse — a second SQL type ladder. Theirs renders SQL that
    DuckDB binds, so the type is whatever DuckDB says: `STRUCT(a INTEGER, b VARCHAR)`, `DECIMAL(9,2)`
    with its scale, `INTEGER[]`, `MAP(VARCHAR, INTEGER)` — all MEASURED intact. ⚠ DuckDB also names its own
    type back re-parseably (`DESCRIBE` ⇒ `STRUCT(a INTEGER, b VARCHAR)`), which is what the execute cast
    splices; it is never the template's own text.
  - **⚠⚠ THE TEMPLATE WRITES ITS OWN `select` AND NOTHING IS PREPENDED — user directive mid-build
    (*"actually change this to use explicit selects … don't add the select part yourself"*), REVERSING an
    expression-only form I had built first.** I chose the bare expression because it makes 1:1 STRUCTURAL (a
    projection over N staged rows yields N rows); the statement form gives that up and reads as SQL you can
    paste and run, which is what the `is_bind` render is for.
  - **⚠⚠ SO CARDINALITY AND ORDER ARE THE TEMPLATE'S CONTRACT NOW, and only one of them is enforced.**
    ENFORCED: exactly ONE output column (at bind) and a row count equal to the chunk's — a statement that
    changes cardinality is refused by name rather than producing a silent short read. NOT ENFORCED: ORDER.
    ⚠ A plain projection over `input_table` preserves it — MEASURED against a build with the ordering
    removed, incl. a correlated-subquery expression over 3000 rows and under
    `SET GLOBAL preserve_insertion_order = false`, both ZERO misaligned — so the ordinary shape is safe and a
    reordering statement must carry `__fab_row` through and `ORDER BY` it. **Do not read the gate's 5000-row
    alignment row as proving ordering; it proves the COUNT.**
  - **⚠⚠ THE CAST IS WHAT MAKES `is_bind` A DECLARATION RATHER THAN A THING TO MATCH — found by RUNNING it.**
    Without it a template declaring `NULL::BIGINT` and rendering the literal `42` is REFUSED, because `42` is
    INTEGER, so every literal's type would have to be written twice and kept in step. The execute wrap casts
    to the declared type through DuckDB's own cast. Mutant B dies at exactly that row after 844 pass. ⚠ A
    genuinely impossible conversion still fails in DuckDB's words; a LOSSY but legal one is silent, as a
    declared column type is everywhere in SQL.
  - **⚠ ONE RENDER PER CHUNK (user decision), AND A FRESH CONNECTION WITH IT.** Liquid decides the SHAPE from
    the constant params; DuckDB computes every row — so the template cannot branch on a row VALUE in Liquid,
    it emits SQL that does. ⚠⚠ **A PINNED connection is NOT available**: the binding is shared across pipeline
    threads (this plugin already records that a volatile scalar may be evaluated on several at once, which is
    why `FluidRenderSession` is per-render) and a DuckDB connection is single-threaded. MEASURED 200,000 rows
    at `threads = 8`, zero wrong.
  - **⚠⚠ A BUG THE BUILD FOUND AND FIXED AT THE ROOT: `getvariable('params')` WAS NULL INSIDE IT.**
    `FluidRenderSession.BindVariable` applied its binding only when `Pin()` OPENED the connection, and this
    function staged its input relation BEFORE building the render context — so the connection was already open
    and the variable was never set. A silent wrong value, not an error. Fixed in `BindVariable` (stage
    immediately when the connection is already open) rather than only at the call site, because the implicit
    "declare before you run anything" ordering contract is the kind that bites the NEXT caller.
  - ⚠ Two limitations, both measured: the template's SQL runs on its OWN connection so it **cannot see the
    caller's TEMP tables** (`Table with name lookup does not exist!` — same rule `query()` follows; use a
    regular table), and the function is VOLATILE so it is never constant-folded (correct — a template may call
    `query()`/`exec()`).
  - **⚠⚠ A NAMED ARGUMENT NAMES ITS `input_table` COLUMN — added 2026-09-13 the same day (user-asked: "when
    such a name parameter is used, can we use this name as col name in the input_table?"). C++ + C#, NO ABI
    change.** `counter := i` is readable as `counter` instead of `arg_0`. Gate `verify_plugin_fluid`
    854 → **865**, floor 9210 → **9221**.
    - **⚠⚠ THE NAME SURVIVES AS THE ARGUMENT'S ALIAS, AND MY FIRST TWO PROBES SAID THE OPPOSITE.** They
      showed an ARBITRARY name is accepted (`zzz_nonsense := 7` works) and that `counter := 7, 2` binds
      POSITIONALLY — from which I nearly concluded the name is discarded. It is not: DuckDB's
      `Transformer::TransformNamedArg` rewrites `name := expr` into `expr` with **`SetAlias(name)`**, so the
      argument stays positional and the NAME rides the expression. **`struct_pack(a := 1)` is the existence
      proof** that a scalar's bind can read it — ⇒ when a probe says "the name does nothing", check whether
      it is merely doing nothing *for dispatch*.
    - **⚠ TAIL SLOTS ONLY** (`FabricatorCallArgName`): a declared parameter must keep its DECLARED name
      because the managed side reads those BY NAME (`ArgColumn(args, "template")`), so letting
      `foo := '…'` rename the template slot would break that read at a distance.
    - **⚠⚠ BIND AND EXECUTE MUST AGREE, so the resolved names ride the BIND DATA**
      (`FabricatorScalarBindData.arg_names`) instead of being recomputed in the execute lambda from the
      registration-time declaration — which cannot know a call site's alias. A disagreement there is a wrong
      COLUMN, not an error. The execute lambda now reads its bind data FIRST, before naming anything.
    - **⚠⚠ DuckDB ALIASES A GENUINE COLUMN REFERENCE TOO, which I did not expect and which BROKE AN EXISTING
      GATE ROW (`arg_0` became `n`).** Measured rule, four cases: `counter := i` ⇒ `counter`; `n` / `fs_t.n`
      ⇒ `n`; `n + 1`, `upper('x')`, a literal ⇒ `arg_<k>`. **User-confirmed as the right behaviour** ("this
      is actually fine when n is used"), and it is **strictly nicer than `fluid_query_lateral`'s rule**,
      which names wire columns by rendered EXPRESSION TEXT and so yields unquotable names like `(t.n + 1)`.
      Here an argument is either self-naming or positional — there is no unusable third case.
      ⚠ POSITION IS COUNTED OVER THE WHOLE TAIL: in `f(tpl, NULL, counter := 1, n, n + 1, 9)` the literal is
      `arg_3`, not `arg_1`. Pinned in one assertion, because the rule has four cases and only measuring
      showed where the line falls.
    - **⚠⚠ A NAMED ARGUMENT DOES NOT REORDER, AND THAT TRAP IS DuckDB'S — pinned as a characterization.**
      `fluid_scalar(tpl, counter := 7, 2)` binds **7 as the params bag**: the name is discarded for dispatch
      and binding is positional. A built-in is identical (`upper(zzz := 'a')` returns `A`). So naming
      DOCUMENTS an argument, never moves it, and is validated against nothing.
    - ⚠ `__fab_row` is refused as an argument name (it carries the staged row number), and
      `fabricator_va_concat` is unaffected — it reads its tail positionally, so an alias changes nothing
      there (checked, both spellings).
  - **⚠⚠ `input_table` IS AVAILABLE AT BIND, EMPTY AND FULLY TYPED — added 2026-09-13 (user-asked: "could we
    make an empty input_table available at bind with the vargs?"). It ALREADY WAS, with the right NAMES; what
    it lacked was the right TYPES.** One managed line. So a template can derive its SELECT list AND its
    RESULT TYPE from the input schema — §26's capability for this surface. Gate 865 → **868**, floor 9221 →
    **9224**.
    - **⚠ THE TYPES ARE REAL AT BIND ONLY BECAUSE THE TAIL IS `ANY`**: DuckDB inserts NO CAST there, so the
      host marshals each tail argument as the EXPRESSION'S OWN type — which is exactly what execute
      delivers. A CONCRETE-typed parameter would not have this property (DuckDB casts it after the bind
      returns), so do not generalise it. ⚠ A non-constant argument has a real TYPE at bind though its VALUE
      is a placeholder — type from the expression, value from folding.
    - **⚠⚠ A MUTANT CORRECTED THE GATE'S OWN COMMENT.** The "shape" row renders the same text on both sides
      and I wrote that its passing proved the two views AGREE. It does not — the OUTPUT comes from the
      EXECUTE render alone, so a placeholder-typed bind passes it unchanged. Mutant E SURVIVES that row and
      dies at the derived-result-type row below it, because a declared type can only come from the BIND
      render. **A row that renders at both times does not thereby test both times.**
  - **⚠⚠ `input_table` HOLDS THE ARGUMENTS AND NOTHING ELSE — the always-present `__fab_row` staging column
    was DROPPED 2026-09-13 (user: "i think the __fab_row is not that useful in fluid_scalar?" … "it is only
    needed in fluid lateral").** They are right on both counts. **A ROW KEY IS A LATERAL CONCEPT**: a lateral
    is 1→N so every output row must say which input produced it, while a scalar is 1:1 by construction and
    needs no provenance. Gate 868 → **867**, floor 9224 → **9223**.
    - ⚠ Nothing used it: the wrap stopped ordering when the template took over writing its own `select`, so
      its only remaining purpose was as an ordering key for a re-sorting template — and that is
      SELF-SERVABLE. The caller passes their own (`rn := row_number() over ()`, then `order by rn`),
      MEASURED over 3000 rows with zero misaligned, which is better than an injected key: explicit, named by
      the author, and absent when unneeded. Against that it cost a reserved name, a refusal, and a column in
      every `describe input_table`.
    - **⚠⚠ REMOVING IT EXPOSED A SECOND PURPOSE IT HAD BEEN SERVING SILENTLY, and the zero-argument gate row
      is what caught it.** With NO tail arguments the staged relation has ZERO columns, and Apache.Arrow
      cannot represent a zero-FIELD schema across the C interface in either direction — the same
      `ArgumentNullException('fields')` the zero-argument SCALAR case already records. ⚠ And `input_table`
      also carries the chunk's ROW COUNT, so omitting it is not an option either: a bare `select 42` yields
      ONE row whatever the chunk size and fails the cardinality check. ⇒ a `__fab_rows` placeholder appears
      ONLY when there are no per-row arguments — structural, not a row key. **When removing a column that
      "nothing uses", check what its mere PRESENCE was guaranteeing.**
  - **⚠ WHAT IT ADDS OVER WHAT ALREADY WORKED, measured before building so the value was known rather than
    assumed**: per-row args were ALREADY expressible through the params bag (it is a COLUMN, evaluated per
    row), and `fabricator_parse(fluid_render(…), 'bigint')` ALREADY produced a typed result. The genuinely new
    thing is that the TEMPLATE declares its type, so an `{% include %}`d template file carries its own output
    type and the call site cannot drift from it.

- **⚠⚠ THE PARAMS BAG IS A DuckDB VARIABLE TOO — `getvariable('params')` in the render's own SQL. BUILT
  2026-09-13 (user-asked). C#-only IN THE PLUGIN: NO ABI change, NO C++ change, NO bridge change. Gate
  `verify_plugin_fluid` 810 → **828** (§37), hermetic floor 9166 → **9184**, THREE mutants each killed at its
  own row. Full record: [docs/fluid-templating.md](fluid-templating.md) §38.**
  - **⚠⚠ STAGED THROUGH A NAMED ARROW SOURCE, NEVER RENDERED AS TEXT, and that is the whole design.**
    `SET VARIABLE params = (SELECT "params" FROM fabricator_scan('<tok>'))` — the shape
    `FluidRelationInput.CreateEmptyInput` already uses for `input_table`. MEASURED type-exact: a bag of
    `{'d': DATE …, 'm': 19.99::DECIMAL(9,2)}` reads back as `STRUCT(d DATE, m DECIMAL(9,2))` with `m * 2` =
    39.98. Rendering would mean a SECOND SQL type ladder, and the one we have (`DuckSql.Literal`) collapses
    every temporal to TIMESTAMPTZ and REFUSES a LIST or a STRUCT by name.
  - **⚠⚠ THE BOUND-PARAMETER ROUTE IS CLOSED, and measuring it first is what avoided building the wrong
    thing: `SET VARIABLE` CANNOT BE PREPARED** (`Parser Error: syntax error at or near "SET"`), while the
    host's parameterised `host_query` path is `Prepare` + `Execute`. So the provider tags' `struct_pack($a)`
    trick does not transfer here.
  - **⚠⚠ SCOPE IS FREE AND IT IS WHAT MAKES THE FIXED NAME SAFE: DuckDB keeps variables in
    `ClientConfig::user_variables`, a per-`ClientContext` map** — read out of the source, then MEASURED IN
    BOTH DIRECTIONS: a variable set on the pin is invisible to the NEXT render, and one set by the CALLER's
    session is invisible on the pin. So `params` can collide with nothing of the user's, and it dies with the
    render. Both pinned in §37.
  - ⚠ `getvariable` resolves at BIND time into a `BoundConstantExpression` and takes its return type from the
    VALUE — which is what makes `getvariable('params').region` bind at all. The variable is staged when the
    connection opens, i.e. before any statement of the render is prepared.
  - **⚠⚠ LAZY VIA A FACTORY, AND THE LIFETIME RULE SPLITS THE SURFACES — this is the part to not get wrong.**
    `FluidRenderSession.BindVariable(name, Func<RecordBatch?>)` reads nothing until the pin opens, because
    `fluid_render` is a per-ROW scalar and an eager copy would run for every row of every template, including
    the majority that run no SQL. **The factory must not close over anything shorter-lived than the session**:
    `fluid_render`/`fluid_replacement_query` render INSIDE the call that owns their args (so they may slice LIVE Arrow),
    while the three deferred surfaces create their EXECUTION session long after `Bind`'s args are freed — the
    same fact that makes `CaptureBag` eager — so they close over a COPY (`FluidValueModel.CopyBagRow`).
  - **⚠ THE COPY IS A SLICE + AN IPC ROUND TRIP, and the round trip is not just for the copy**: it NORMALISES
    the slice, since a sliced Arrow array carries an offset its children do not. **The PER-ROW gate row is
    what proves the right cell is picked** — mutant 2 (always slice row 0) passes every other row in §37 and
    dies exactly there, after 813. ⚠ The sliced VIEW is deliberately not disposed — its buffers are the
    caller's, and disposing a view of an imported array is the double-release that faults inside
    Apache.Arrow's own release callback.
  - **⚠⚠ THE BOUNDARY IS BY DESIGN, user-confirmed mid-build (*"in fluid_replacement_query the getvariable should only
    work in an explicit {%query/exec %}"*).** In `fluid_replacement_query` the variable is readable from a `{% query %}` /
    `{% exec %}` block and NOT from the GENERATED statement — that statement is returned as TEXT and bound by
    the CALLER's connection, a different `ClientContext` and therefore a different variable map (MEASURED:
    `typeof` = `"NULL"`). The three deferred surfaces DO see it from their generated statement because WE run
    it on the pin. Both halves pinned, the second as a characterization.
    - ⚠ **There IS a route through it, MEASURED**: stage inside `{% exec %}` (which DOES see the variable)
      and `publish()` the staged table. Documented rather than separately gated — it composes two mechanisms
      each pinned on its own, so nothing there can break without one of those failing first.
  - ⚠ A **JSON-string** bag stays a VARCHAR while Liquid PARSES it, so `getvariable('params').region` works
    only for the STRUCT spelling — the documented "prefer STRUCT" asymmetry. A LIST and a bare SCALAR bag
    both work. **No bag ⇒ NO statement**: an unset variable already reads as NULL, so writing one buys
    nothing.
  - **⚠⚠ MY FIRST JUSTIFICATION FOR NOT CASTING A JSON BAG TO DuckDB'S `JSON` TYPE WAS WRONG, user-challenged
    (*"I did not understand why we need 'inventing DuckDB types'"*) — and they were right.** "Inventing
    DuckDB types for JSON's four scalar kinds" is the JSON → **STRUCT** argument (is `1` INTEGER or BIGINT?);
    the `JSON` TYPE is VARCHAR-backed and invents nothing. THREE real reasons stand in its place, all
    measured, and the FIRST is what decides it:
    1. **It would not unify the spellings, it would make them differ SILENTLY.** Dot access DOES work on a
       JSON value — but `getvariable('p').region` yields `"eu"` (a QUOTED JSON scalar) where a STRUCT yields
       `eu`. So auto-casting swaps today's LOUD binder error for a value that is subtly wrong in any string
       comparison or concatenation. `->>'region'` is the unquoted accessor.
    2. **`::JSON` NEEDS THE json EXTENSION** — `Catalog Error: Type with name "JSON" is not in the catalog,
       but it exists in the json extension.` So the cast would have to be CONDITIONAL, making the variable's
       TYPE depend on the environment — and on a `LOAD json` landing BETWEEN two renders.
    3. **"Preserve what the caller declared" is UNAVAILABLE**: DuckDB's OWN Arrow export drops JSON-ness.
       MEASURED — `fabricator_host_query` over a `::JSON` column reports VARCHAR while the direct value
       reports JSON — so a `::JSON` bag reaches us as a plain string whatever the caller wrote.
    ⚠ The capability already exists with NO code from us, and both spellings are measured:
    `getvariable('params')::JSON->>'region'` inline, or one `SET VARIABLE pj = getvariable('params')::JSON`
    in an exec block and dot access on `pj` thereafter.
  - **⚠ THE SECOND HALF OF THE SAME QUESTION — "make the fluid params lazy like input_table" — IS ALREADY
    SATISFIED ON THE LIQUID SIDE AND MUST NOT BE MADE LAZY.** `{{ params.region }}` already works for a JSON
    bag (`CaptureBag` parses it), so there is nothing to gain; and `CaptureBag`'s EAGERNESS is load-bearing —
    its own doc records that the three deferred surfaces' bind args are freed before their renders run, so a
    lazy leaf would read a disposed batch. `input_table`'s laziness is a different thing: it is lazy over a
    DuckDB RELATION that lives in the database, not over borrowed Arrow.
  - **⚠ MUTANTS: 1** (`Bind` never declares it) dies at the FIRST §37 row after exactly 810 — the section
    boundary; **2** (always slice row 0) at the per-row row after 813; **3** (the shared deferred-surface
    context never declares it) at the batch row after 819, leaving the `fluid_render` rows alone — which is
    what shows the lateral's own wiring is a SEPARATE line.
  - **⚠⚠ A TRAP RE-EARNED: `nohup … &` COMBINED WITH THE HARNESS'S OWN BACKGROUNDING PRODUCES A FAKE
    COMPLETION.** The task reported "completed (exit code 0)" seconds after launch while the tier was still
    running (`Get-CimInstance Win32_Process` showed the `run-suites` bash alive, log 0 bytes from block
    buffering). CLAUDE.md already records the rule — launch a tier with `run_in_background` ALONE — and I
    used both anyway. **Acting on that notification would have started a second concurrent tier**, which is
    the recorded way to void two runs at once.

- **⚠⚠ `fluid_query_inout` — THE STREAMING SIBLING OF THE COLLECTOR. BUILT 2026-09-11 (user-asked). C#-only,
  NO ABI change and NO C++ change. Gate `verify_plugin_fluid` 759 → **794** (§35), hermetic floor
  9088 → **9123**, THREE mutants each killed at its own row. Full record:
  [docs/fluid-templating.md](fluid-templating.md) §30.** It is the item §19 recorded as deliberately
  still open — *"a bounded-memory batched variant is the SAME body registered on the streaming in-out"*.
  - **IT NEEDED NOTHING NEW**: `IProvider.GlobalInOutFunctions` is already a DIM and the global in-out kind
    has been supported since ABI v46/v47, so the whole feature is one class plus one registration line.
  - **⚠⚠ WHAT IT BUYS IS BOUNDED MEMORY, AND WHAT IT CANNOT DO IS FORCED BY THE OPERATOR.** A collector
    buffers its ENTIRE input before the first render (true even at a small `batchsize` — that parameter is
    about how many rows each render SEES, never about memory); this holds ONE CHUNK at a time. The price is
    that there is **no `batchsize` and no whole-input render**: the all-input-done hook is handed no
    `DataChunk`, so output held back until EOF is DRAINED AND DISCARDED, and **the chunk IS the batch**.
    ⚠ A SEPARATE registration rather than a mode, because `kind` is fixed at REGISTRATION — the same
    constraint the lateral records.
  - **MEASURED, the contrast that defines the pair** (the template reports its own input size, so each
    output row IS one render): over `range(5000)`, `fluid_query_inout` = **3 renders** / 5000 rows /
    biggest 2048, `fluid_query_batch` = **1 render** / 5000 rows.
  - **⚠⚠ AN EMPTY INPUT RENDERS NOTHING HERE AND ONCE ON THE COLLECTOR** — a real divergence, gated as a
    PAIR so neither half reads as an accident. No rows ⇒ no chunks ⇒ no renders; the collector renders once
    because a template is a statement GENERATOR whose output need not depend on the rows.
  - **⚠⚠ THE SHARED RULES WERE EXTRACTED, NOT COPIED — `FluidRelationInput`.** The schema probe, the
    projection wrapper, the drift check, the publish refusal and the empty-input creation now exist ONCE.
    Each is load-bearing in a way a reader would not guess (the `LIMIT 0` wrapper is also what REQUIRES the
    generated statement to be a subquery-usable SELECT; `Wrap` IS the projection pushdown), so a second copy
    would drift into a SILENT behaviour difference rather than a compile error.
  - **⚠ IT MOVED `InOutExchange.EmptyBatch` FROM Bridge TO Common, AND THAT WAS A PRE-EXISTING GAP.**
    `StaticInOutFunction` already lived in Common and its own docs told authors to yield
    `InOutExchange.EmptyBatch` — while the helper sat in Bridge, which a plugin deliberately does not
    reference. **A plugin deriving from that base could not write the one thing the base requires of it.**
    Namespace unchanged (the Abstractions/Common convention), so not one call site moved.
  - **⚠⚠ DRIFT IS CAUGHT BY TWO DIFFERENT MECHANISMS, found by writing the gate and getting the expected
    message WRONG.** A RENAMED column is caught by the WRAPPER (it selects the declared names, so the inner
    bind fails naming the column) and never reaches `Verify`; a RETYPED column keeps its name, binds
    happily, and only `Verify` sees it. Both are gated — without the second row `Verify` would be untested
    here and a reader would assume one check covered both.
  - ⚠ The two stale-state mutants die on DIFFERENT rows, which is what separates "the temp TABLE is stale"
    (dies on the contrast row at `1 6144 1`) from "the LIQUID value is stale" (dies on the agreement row at
    `0 1 5000`). The agreement row ALONE could not: a build where both are stale still reports them as
    agreeing, which is why the `sum` and the `max(sql_n) > 1` control are in that row.
  - **⚠⚠ PROJECTION PUSHDOWN — BUILT 2026-09-12 (user-directed, after asking whether it was implemented and
    being told it was wired and INERT here). C++-ONLY, NO ABI change. Gates `verify_plugin_fluid` 794 →
    **810** (§36) and `verify_global_functions` 164 → **178**; TWO mutants, each killed at its own SUITE.
    Full record: [docs/fluid-templating.md](fluid-templating.md) §37.**
    - **THE WHOLE GAP WAS ONE HOST-SIDE FLAG.** The ABI entry has carried the argument since v87,
      `InOutExchangeStream` has always declared `ProjectedOutputSchema(projected)` and forwarded `projected`
      to `DoExchange`, and `IInOutFunctionBinding` has always declared both DIMs — but
      `tf.projection_pushdown` was `is_collector`, so the get was never narrowed and the call site passed an
      empty list. ⚠ Verified by READING the two gates rather than assuming: `RemoveColumnsFromLogicalGet`
      keys on that flag and on nothing about `in_out_function`, and the `LOGICAL_GET` case narrows the get
      BEFORE recursing into a table-in-out's child.
    - ⚠ BOTH exchange registrations, deliberately: the GLOBAL one and the CATALOG-BOUND one every
      provider-declared `_each` resolves through. They are separate call sites and could have been split;
      the ignore-the-hint fallback makes the second safe, and two registrations disagreeing about their own
      optimizer contract is what someone trips over later.
    - **⚠⚠ THE GATE THAT MATTERS IS THE CALLEE THAT IGNORES THE HINT — §24's lesson, reproduced exactly.**
      `fluid_query_inout` HONOURS it, so its wire map is the IDENTITY and its own rows CANNOT catch an
      off-by-one: mutant 2 (drain by position) passes `verify_plugin_fluid` at **810** and dies in
      `verify_global_functions`. The discriminating callee is `fabricator_inout_va`, whose two columns are
      of DIFFERENT types. ⚠ It is killed there by a **PRE-EXISTING** row (`count(*), min(tag)`), because
      enabling pushdown made an existing assertion depend on the new map; the added rows kill it too, with
      an `INTERNAL Error: … unique_ptr that is NULL` — so a wrong map is a CRASH, not a wrong value.
    - ⚠ Mutant 1 (the flag reverted) dies at fluid's PAYOFF row after 800 pass and leaves
      `verify_global_functions` GREEN at 178 — the honest split, since with no hint everything is full width
      and still correct.
    - **⚠ THE PROJECTION IS WHAT IS *READ*, NOT WHAT THE INNER SELECT LIST SAYS** — measured while writing
      §36, where `min(q) FROM (SELECT p AS q, a FROM …)` still narrowed to `p` alone because DuckDB prunes
      `a` straight through the subquery. A row meaning to exercise the UNNARROWED path must CONSUME both.
    - **⚠⚠ IT FOUND A PRE-EXISTING DEADLOCK — limitation 1.25 — AND THE OBVIOUS FIX FOR IT IS MEASURED
      WRONG. Full record: [docs/fluid-templating.md](fluid-templating.md) §37.5 + §37.6.** A `LIMIT`
      above ANY streaming in-out gives `Invalid Error: resource deadlock would occur`; MEASURED on
      `fluid_query_inout` and `fabricator_inout_va`, and **reproduced on a binary built from HEAD with the
      projection change absent**, which is what attributes it elsewhere. The operator returns
      `HAVE_MORE_OUTPUT` holding the gate and releases it only on its NEXT invocation, so an early-stopped
      pipeline ABANDONS the tenure and `FinishEof` then tries to take it.
      - **⚠⚠ NOT "a bare LIMIT" — that was the first write-up and it is too coarse.** MEASURED over 4 rows:
        `LIMIT` 1/3/4 fail, **`LIMIT 10` is FINE** (the operator is pulled again, hits END, releases).
        `ORDER BY … LIMIT` is fine for the same reason, which is why no suite caught it.
      - **⚠⚠ TWO SHAPES, AND THE SAME-THREAD ONE IS THE MINORITY.** Same thread ⇒ `EDEADLK`, which MSVC
        throws (measured on single-branch `fabricator_inout_va` with a thread-id probe). FOREIGN thread ⇒ it
        BLOCKS on a tenure nobody will release, i.e. a silent HANG — and instrumenting the whole of
        `verify_plugin_fluid` gave **15 `FinishEof` calls, `owns=1` ZERO times**.
      - **⚠⚠ SO OWNER TRACKING IS INSUFFICIENT — BUILT, MEASURED, REVERTED (2026-09-12).** Making
        `FinishEof` ADOPT its own tenure fixes the loud case and turns the silent one into a HANG (the fluid
        suite hung at 400 s where it had merely failed). **`std::mutex` may only be unlocked by its owner**,
        and the gate is a TOKEN acquired in one `Execute` and released in a LATER one whose holder may never
        run again. ⇒ the fix needs a different PRIMITIVE (mutex + condvar + a `taken` flag, releasable by
        any thread), not more tracking. ⚠ A gate MUST cover the FOREIGN-thread case (a `LIMIT` over a
        parallel `UNION ALL`) — a single-branch probe cannot see it, which is exactly what made the first
        attempt look correct while the fluid suite hung.
      - ⚠ On glibc the same-thread re-lock is UB rather than an error, so Linux is likely worse.

- **⚠⚠ `fluid_query_lateral(template, params, <per-row columns…>)` — A TEMPLATE RENDERED ONCE PER INPUT
  CHUNK, CORRELATED, PARALLEL. BUILT 2026-09-05 (user-designed; their hint "laterals currently don't
  support named args, so params must be positional" settled the signature). C#-only IN THE PLUGIN: NO ABI
  change, NO C++ change, NO bridge change. Gate `verify_plugin_fluid` 459 → **563**, hermetic floor
  8735 → **8839**, two mutants each killed at its own assertion. Full record:
  [docs/fluid-templating.md](fluid-templating.md) §22.**
  - **⚠⚠ THE MOST USEFUL RESULT IS A MUTANT THAT KILLED WORTHLESSLY FIRST — 50 of the gate's 104 new
    assertions exist because of it (§22.8).** `origin[i] = 0` (ignore the projected `__fab_row` entirely)
    passed every provenance row and died only at the LAST assertion in the section. **A lateral call with
    ONE input column is delivered ONE OUTER ROW AT A TIME**, and with a chunk of one row the only valid
    provenance index IS 0 — so a constant was correct. MEASURED with a template reporting its own chunk
    size: one input column ⇒ 1,1,1; **TWO input columns ⇒ 1,2,2**. Every provenance row now runs on the
    two-column shape over six rows, with **`max(chunk) > 1` as the control** (only `> 1`, never the sizes —
    the grouping is DuckDB's scheduling), and the sharpest row is a template that REORDERS its own output,
    which is the one shape where "position in the output" and "the real mapping" disagree. ⇒ **a gate over
    a batched interface is only as good as the batch it actually gets, and nothing in the SQL says what
    that is.**
  - **IT NEEDED NOTHING NEW, which is the payoff of two earlier slices**: `Params.Constant` (2026-08-29)
    and the LATERAL variadic tail (2026-08-31) compose exactly as predicted — `[Constant template]
    [Constant params][VarArgs input]` registers as `[ANY, ANY] varargs ANY`, the constants are stripped BY
    INDEX so the tail has nothing to contend with, and `RegisterRows` / the pinned-connection
    `fabricator_scan` / the `is_bind` + `LIMIT 0` probe / the `publish()` refusal all transferred verbatim
    from `fluid_query_batch`.
  - **⚠⚠ PROVENANCE IS THE CONTRACT AND IT COST WHAT WAS PREDICTED.** `input_table` carries `__fab_row`
    first and **the generated statement must project it**; it is stripped from the result, so
    `SELECT * FROM input_table` is the identity template. Refused AT BIND, naming the column — a
    mis-attributed row is a wrong answer with nothing failing. An index outside `[0, chunk)` is refused
    with the VALUE at call time (the host checks this too and precisely; the managed check exists for the
    MESSAGE, since only this side knows the number came from a projected `__fab_row`).
  - **⚠⚠ THE NAMING ANSWER IS BETTER THAN THE DESIGN'S, AND THE DESIGN'S ADVICE WAS MEASURABLY WORSE.**
    The recorded plan said a caller wanting clean names "passes ONE struct … whose FIELD names are carried
    in the Arrow schema". MEASURED: that struct arrives as ONE column named
    **`main.struct_pack(a := t.id, b := t.n)`**, which has to be quoted verbatim to address. The real
    answer is a **positional column alias**, ordinary SQL, identical in both call shapes:
    `SELECT r AS __fab_row, a * b AS s FROM (SELECT * FROM input_table) AS q(r, a, b)`. ⚠ Note the id is
    re-aliased BACK — the requirement is on the OUTPUT column's name, not the input's.
  - **⚠⚠ AND THE `col<N>` FALLBACK IS THE ARGUMENT POSITION, NOT THE INPUT POSITION.** MEASURED: `t.n`
    arrives as `n`, `t.n + 1` as `(t.n + 1)`, `upper('x')` as `upper('x')`; a LITERAL call has no
    expression text and falls back to `col<SLOT>`, so with the two constants ahead of it the FIRST input
    column of `f('tpl','null',7,'x')` is **`col2`**. Gated as a PAIR (correlated vs literal) because
    neither half says anything about the other.
  - **⚠⚠ `params` CANNOT BE `NULL` — the no-bag spelling is the JSON `'null'`.** A bind-time constant that
    arrives NULL is REFUSED by the host, and rightly: in the correlated shape an explicit NULL is
    indistinguishable from a fold that FAILED, which is the one thing that refusal exists to catch.
    MEASURED: `'null'` binds and the bag is NIL (`{% if params %}` false), `'{}'` binds and is TRUTHY, a
    STRUCT `{'mul': 10}` survives the correlated shape. All four gated, the last three as the control —
    the refusal alone would be equally true of a build where constants had stopped arriving.
  - **⚠⚠ THE FIRST BUILD SHIPPED A USE-AFTER-FREE AND IT FAULTED NOWHERE NEAR ITS CAUSE:
    `0xC0000005` inside `Apache.Arrow.C.CArrowArrayExporter.ReleaseArray`, two frames, nothing naming the
    plugin.** The result was built over `parts[0]`'s columns and a `finally` then disposed every part. Fix
    is an ownership rule at the seam: SEVERAL parts ⇒ the concatenator allocated every output column, so
    release them all; ONE part ⇒ the output columns ARE that part's, so hand the batch on undisposed and
    release only the provenance column. ⚠ Disposing a single column is not a trick —
    `RecordBatch.Dispose` IS "dispose the columns", so this is that performed selectively, each array once.
    ⚠ And it is a `catch`, not a `finally`: only the failure path may free blindly.
  - **⚠ ITS GATE IS THE 6000-ROW CHECKSUM**, because a small result exercises only the single-part branch.
    A fan-out exceeding one Arrow batch is the path that would truncate silently.
  - **⚠ NO STATE CARRIES BETWEEN CHUNKS, unlike the collector — and it is NOT gated.** The operator is
    PARALLEL (one `FluidRenderSession`, one DuckDB connection and one temporary catalog per pipeline
    thread), so which rows reach which session is the scheduler's business; asserting it would be flaky in
    one direction and vacuous in the other. Documented on the class instead. Use `fluid_query_batch` to
    accumulate — it is sequential by construction. ⚠ What IS measured is that several sessions at once are
    CORRECT, which is the half that could break (each issues `CREATE OR REPLACE TEMP TABLE input_table`
    under the SAME name, so a shared catalog would have them overwriting one another): **50,000 distinct
    correlated values at `threads = 8` ⇒ 50,000 rows, `sum` exactly 2,499,950,000.** Not in the suite.
  - ⚠ Incidental cleanup in the same pass: `ArgColumn` (read a bind arg BY NAME) was a private copy in the
    collector and is now `FluidValueModel.ArgColumn`, shared — the rule cannot exist in two versions. And
    the two STALE doc comments flagged on 2026-08-31 (`ParamStyle.VarArgs`'s remark and `Params.Validate`'s
    refusal, both saying varargs are "deferred for lateral and in-out") are CORRECTED: every kind but
    AGGREGATES takes a tail, and an aggregate cannot because its update crossing rebuilds the batch schema
    from the declaration.
  - **⚠⚠ PROJECTION PUSHDOWN IS BUILT — ABI v86, 2026-09-06 (user-directed: "build 1+2+(a). couldn't we
    just include a fluid `projected` as well and the template is free to use it or not?"). C++ + ABI + C#.
    Gate `verify_plugin_fluid` 599 → **677**, hermetic floor 8875 → **8953**, one mutant. Full record:
    [docs/fluid-templating.md](fluid-templating.md) §24 + [docs/abi-history.md](abi-history.md)
    §v86.**
    - **TWO OPTIMIZER FACTS MADE IT CHEAP, both read at the pin.** `UNUSED_COLUMNS` runs at
      `optimizer.cpp:222`, BEFORE optimizer extensions at `:331`, so our own rewrite already sees the
      narrowed `column_ids` and needs no new plan pass; and `GetAnyColumn()` falls through to `return 0` for
      a get with no virtual columns, so the all-pruned case (`SELECT count(*) FROM t, f(…)`) can never hand
      us an empty list or a rowid sentinel. `lateral_open` is the ONLY crossing in the window between bind
      and execution, which is what fixes where it rides.
    - **⚠⚠ IT IS A HINT, AND THAT IS WHAT LET IT SHIP WITHOUT TOUCHING ONE EXISTING LATERAL.** A callee may
      honour it or return its full declared schema; the host discriminates by COLUMN COUNT and validates
      types either way, in the wire check that was ALREADY the trust boundary. So
      `ILateralFunctionBinding.Open(IReadOnlyList<int>?)` is a DIM, not a signature change — the alternative
      would have meant real narrowing logic in seven in-tree demos and three out-of-tree plugins to buy
      nothing they need.
    - **⚠⚠ THE MUTANT INVERTED WHICH ASSERTION MATTERS.** Emitting by position instead of through the wire
      map passes **630** assertions — including EVERY `fluid_query_lateral` projection row — and dies at the
      `fabricator_lat_span` row. Because fluid HONOURS the hint its map is the IDENTITY, so its own rows
      cannot catch the off-by-one at all. **The row that tests the map is the one where the callee IGNORES
      the hint**, and without a demo that does so this would have shipped with a vacuous gate.
    - **THE PAYOFF IS THE INNER STATEMENT, not the wire.** MEASURED before building: DuckDB prunes an
      unreferenced expression inside a subquery (`SELECT a FROM (SELECT i AS a, error('X') AS b FROM
      range(3))` returns three rows; referencing `b` raises), so a wrapper naming only the projected columns
      makes the TEMPLATE'S OWN statement stop computing what nobody reads. Gated as that pair.
    - **⚠⚠ THE USER'S QUESTION CORRECTED MY OBJECTION TO (b).** I had argued exposing `projected` to the
      template inverts the schema contract. TRUE without (a), FALSE with it: the wrapper NORMALISES the
      shape whatever the template did — ignore it and the extras are dropped, use it and the wrapper selects
      what is there, render fewer and DuckDB's binder fails naming the column. So it is genuinely optional,
      which is what was asked. ⚠ Not bound during the schema probe (no projection exists yet), so a template
      reading it branches on `is_bind`.
    - **⚠ A BEHAVIOUR CHANGE IT FORCED, and the gate is stronger for it**: selecting BY NAME instead of
      `* EXCLUDE` means a chunk rendering an EXTRA column now has it DROPPED where it used to be refused.
      Harmless by construction (nobody can read a column absent from the declared schema), and the hazard
      that check existed for is now structurally impossible rather than caught — by-name selection means the
      batch reaching the host always has exactly the declared columns in the declared order. MISSING,
      RENAMED and RETYPED are all still refused; §28's one drift row became four.
    - ⚠ NOT done: filter pushdown (`filter_prune` stays off, which is also why `projection_ids` stays empty
      and the eligibility check can keep bailing on it). ⚠ A same-width REORDERING would defeat the managed
      side's count-based test; it cannot arise because `RemoveColumnsFromLogicalGet` preserves order, but
      that is an assumption about DuckDB, recorded rather than guarded.
  - **⚠ ALREADY TRUE, NOW STATED AND PINNED (2026-09-06, user-asked: "could we make the input_table
    available as an empty table at bind time (is_bind)? This enables building the outputschema not only
    dependent on params but also on the input_table schema"): IT ALREADY IS, on both surfaces.** Both binds
    call `CreateEmptyInput` BEFORE the `is_bind` render, so a template can `DESCRIBE` the empty
    `input_table` and build its SELECT list from the answer. MEASURED: one template over
    `(SELECT 1 AS alpha, 'x' AS beta)` yields `out_alpha`/`out_beta`, and the SAME template over
    `(SELECT 7 AS gamma)` yields `out_gamma`. Gate `verify_plugin_fluid` 702 → **716**, floor 8978 →
    **8992**; NO code change. Full record: [docs/fluid-templating.md](fluid-templating.md) §26.
    - ⚠ **Worth PINNING rather than merely documenting**: `CreateEmptyInput`'s stated purpose is letting the
      probe BIND the generated statement, so the schema being DERIVABLE from it is a second-order effect of
      the ORDERING — a refactor moving the create past the render would take it away silently.
    - ⚠ The discriminating row is the SAME template over a DIFFERENT input: a single-input assertion passes
      equally on a build with the names hardcoded.
    - ⚠ It composes with `projected`: the probe has the INPUT schema but NOT the projection, which does not
      exist until after the bind. So a template may derive its FULL shape from the input at bind and narrow
      that shape per call.
  - **⚠⚠ AND THE SAME FOR THE COLLECTOR — ABI v87, 2026-09-06 (user-asked: "should be possible to add
    `projected` to fluid_query_batch?"). Gate `verify_plugin_fluid` 677 → **702**, floor 8953 → **8978**,
    one mutant. Full record: [docs/fluid-templating.md](fluid-templating.md) §25.**
    - `inout_exchange_open` gains the hint, for COLLECTORS ONLY **at the time** (the streaming exchange
      passed an empty projection) — ⚠ **SUPERSEDED 2026-09-12: the exchange advertises it too now, with NO
      ABI change; see the `fluid_query_inout` entry's projection sub-entry.** ⚠⚠ **One thing differs from
      the lateral and needed an extra member**: a collector's output
      crosses as ONE stream whose schema is read BEFORE the first batch — an EMPTY result must be
      classifiable too — so a callee that narrows must DECLARE it via
      `ICollectorFunctionBinding.ProjectedOutputSchema(projected)`, a DIM returning the full schema by
      default. Overriding `Collect` alone would have the host read narrow batches through wide converters.
    - **⚠⚠ THE PROBE THAT PROVED NOTHING, and it is the most useful thing in the pass.**
      `SELECT b FROM fluid_query_batch(…)` returns one column WHETHER OR NOT the get was narrowed, because
      DuckDB projects above the operator either way — so it passed happily while the projection reached
      NOTHING: `projection_pushdown` had been set on the CATALOG registration and `fluid_query_batch` is a
      GLOBAL collector, registered on a different path. ⇒ **the only evidence of pushdown is work NOT
      HAPPENING, or the projection itself; the shape of the result is not evidence.**
    - **⚠⚠ AND THE FIRST EXPLANATION FOR IT WAS WRONG, killed by the gate.** The same probe under
      `SELECT DISTINCT` also showed all columns, which fits DuckDB's `everything_referenced = true` rule for
      a plain distinct — a real rule that was NOT the cause. Re-measured after the registration fix, DISTINCT
      narrows fine; the row asserting otherwise FAILED and was deleted rather than adjusted. **A plausible
      mechanism that fits one observation is not a measurement** — this file's own recurring error.
    - ⚠ The payoff row must NOT aggregate: the schema probe renders against an EMPTY `input_table`, and a
      `count(*)` produces a row — and evaluates the `error()` — at BIND, failing the row for a reason
      unrelated to projection.
    - ⚠ Same mutant lesson (dies at `fabricator_collect_sum` after 690 pass) and same drift relaxation:
      `fluid_query_batch` had NO wrapper before and now always wraps, so an EXTRA column is dropped where it
      was refused. **The wrapper is applied even with no projection, deliberately** — otherwise the drift
      behaviour would depend on the caller's SELECT list.
  - **⚠⚠ MY "STILL OPEN" LIST WAS WRONG ON TWO OF THREE — user-corrected 2026-09-05 ("batchsize-like
    control not needed and i don't know if projection pushdown is even supported by duckdb for laterals").**
    ⛔ **A `batchsize`-like control is NOT WANTED — do not build it.** And **projection pushdown is a
    SETTLED DECISION, not an open item**: DuckDB DOES support it for this shape (one flag,
    `TableFunction::projection_pushdown`; `RemoveUnusedColumns` gates on exactly that at
    `remove_unused_columns.cpp:720`, for ANY `LOGICAL_GET` — the visitor even has a branch recursing into a
    get's child *"e.g., table in out functions"*), and the reason it is OFF is OURS and was already written
    into `fabricator_lateral.cpp`'s header: narrowing the get would need the callee-original column indices
    captured at rewrite time and threaded through as the wire projection, *"where an off-by-one reads a
    callee column into a correlated column's slot: wrong data, no error"*. ⚠ Plus a second cost that note
    omits: `LateralIsEligible` BAILS when `projection_ids` is non-empty (`:757`), so setting the flag today
    would silently drop every projected lateral onto the ROW-BY-ROW path — two changes, not one. ⚠ It has
    NOTHING to do with `publish`'s obstacle (a lazily projected stream delivering fewer columns than its
    DECLARED schema); I conflated them. **The one genuinely open item is the input rows as a Fluid VALUE**,
    which `{% query %}` over `input_table` already does in one statement.

- **⚠⚠ `{% query name materialize: 'view'|'table' %}` — LEAVE THE RESULT ON THE CONNECTION **AND** BIND THE
  NAME LAZILY. BUILT 2026-09-06 in two rounds (user-asked, then user-directed after they proposed the
  opposite mechanism and agreed the inversion was better). C#-only IN THE PLUGIN: no ABI, no C++. Gate
  `verify_plugin_fluid` 716 → 729 → **737**, hermetic floor 8992 → 9005 → **9013**, two mutants each killed
  at its own assertion. Full record: [docs/fluid-templating.md](fluid-templating.md) §27.**
  - With `materialize:` set the rows are left on the render's pin as a TEMP object a later block can read AND
    the identifier is still bound in Liquid — as a `LazyRowsValue` that runs `SELECT * FROM "name"` on the
    pin at FIRST ACCESS and caches. `materialize: null` stays the default and is unchanged.
  - **⚠⚠ THE USER'S PROPOSED MECHANISM WAS THE MIRROR IMAGE AND WAS DECLINED ON COST, NOT ON SAFETY — and
    one premise of it was RIGHT in a way worth keeping.** They proposed retaining the `RecordBatch`es for the
    render, registering them back as a scannable temp view, and mapping cells lazily. It is mechanically
    feasible (`IHostQuery.RegisterRows` documents that scanning one token twice is fine, which a bound Arrow
    INPUT cannot promise), and the earlier failure of a lazy wrapper really is LOUD rather than silent
    (Apache.Arrow nulls a disposed batch's arrays ⇒ `NullReferenceException` on the first cell read, every
    platform). What kills it is that it is **two crossings and a full buffer to reach a place the rows
    already were**: `materialize:` is a CTAS, so nothing crosses the ABI, nothing is capped and DuckDB
    spills, while the round trip would reintroduce `query()`'s 1,000,000-row cap on the one path whose point
    is a relation too big for Liquid — the same trade `publish()` measured and reversed. It would also make
    the temp object UNCONDITIONAL, so §27.4's shadowing would stop being opt-in. ⇒ **keep the rows in DuckDB
    and make the LIQUID side lazy over them.**
  - **⚠⚠ `fluid:` IS GONE, BREAKING, NO ALIAS — the lazy bind made it vestigial.** It defaulted to "no
    materialize" and `fluid: true` asked for both while RUNNING THE BODY TWICE; with binding free there is
    nothing left to opt out of and the body runs ONCE in every spelling. ⚠ It is no longer a reserved name,
    so it falls through to the bound parameters and fails LOUDLY (*"excess parameters: 1"*, named
    POSITIONALLY because a body with no `$named` params falls back to positional binding). §33 pins that.
  - **⚠⚠ §33'S ROW ASSERTING THE OPPOSITE WAS REPLACED, NOT DELETED** — it pinned *"materializing means the
    rows went to SQL instead of to Liquid"* (`{{ v }}` empty), which was true of the first build and IS the
    assumption this change lifts. Falsifying it is the change announcing itself.
  - **⚠⚠ A VIEW IS EVALUATED AT THE ACCESS AND A TABLE AT THE BLOCK — MEASURED, and it is the row that proves
    the read is deferred**: a view over a table, an `{% exec %}` updating it, then the first Liquid access ⇒
    the NEW value (7 where the block saw 1); the identical template with `'table'` reads 1. ⚠ The `'table'`
    leg is a CONTROL, not a second proof — an eager bind reports 1 there too. Mutant A (eager bind) dies at
    the view row after 721 pass; mutant B (drop the cache) at the cache row after 725.
  - **⚠ I WROTE A FLAKE INTO THE GATE AND CAUGHT IT BY READING THE COMMENT AGAINST THE EXPECTED VALUE**: the
    two legs were two `fluid_render` calls in ONE `SELECT`, which does not pin an order, so whether the second
    saw the first's UPDATE was DuckDB's business. It PASSED. Each row sets its own starting value and renders
    once now. The shape is already recorded in this file and was walked into anyway.
  - **⚠⚠ A TABLE CAN CARRY THE BLOCK'S NAMED ARGUMENTS AND A VIEW CANNOT — MEASURED, DuckDB's rule.** A CTAS
    with a bound parameter works; the same body as a view is refused with *"Unexpected prepared parameter.
    This type of statement can't be prepared!"*, because a view STORES its body. Refused at the tag naming
    the mode and pointing at `'table'` — and refused whenever named args are supplied with `'view'`, not only
    when the body references them, since the narrower rule would depend on the body.
  - **⚠⚠ A BUG MY OWN SHORTCUT CREATED**: `materialize: null` failed with *"excess parameters"* because
    `ReadQueryOptionsAsync` returned the argument list UNCHANGED when nothing was taken — which cannot tell an
    ABSENT option from one PRESENT AND NULL, and `null` is the documented default. Always rebuilt now; §33's
    second row is the discriminator.
  - **⚠⚠ THE SHADOWING HAZARD THIS FILE SAID TO "SETTLE DELIBERATELY" IS SETTLED, and both halves are
    MEASURED**: a materialized name DOES shadow a catalog table of that name for the rest of the render (99
    over a table holding 1), and the catalog table is UNTOUCHED afterwards because the temp object dies with
    the render's connection. ⇒ accepted: the name is the author's own identifier, the blast radius ends with
    the render, and it happens ONLY when the author asks for it — which is the half the declined mechanism
    would have lost.
  - ⚠ It is ERGONOMICS over something that already shipped (`{% exec %}CREATE TEMP TABLE …{% endexec %}` then
    `{% query %}`); what it adds is that the body stays a `{% query %}` body — still SELECT-classified, still
    parameterised the same way — and, after the second round, that choosing the destination no longer means
    choosing between SQL and Liquid. This is the deferred "query + automatic CTAS" in its EXPLICIT form.
  - ⚠ `materialize` is now the ONE reserved argument name, joining `{% print %}`'s `delim`/`rowdelim`. The
    option is EVALUATED, so `materialize: params.mode` works. ⚠ The lazy read is deliberately NOT classified:
    it is `SELECT * FROM` a quoted identifier composed by `ReadMaterialized`, with no template text in it.

- **⚠⚠ `input_table` IS A LAZY FLUID VALUE TOO — the same inversion, second surface. BUILT 2026-09-06
  (user-asked: "yes lazy fluid makes also sense for input_table"). C#-only IN THE PLUGIN: no ABI, no C++,
  and NO new mechanism — it is `LazyRowsValue` pointed at the temp object each surface already creates.
  Gate `verify_plugin_fluid` 737 → **758**, hermetic floor 9013 → **9034**, one mutant. Full record:
  [docs/fluid-templating.md](fluid-templating.md) §28.**
  - Both surfaces and both probes: `fluid_query_batch` per GROUP, `fluid_query_lateral` per CHUNK, and the
    schema probe on each (where the relation is EMPTY). `{{ input_table.size }}`,
    `{% for r in input_table %}` and `SELECT … FROM input_table` all name the same rows.
  - **⚠⚠ THE LOAD-BEARING PROPERTY IS FRESHNESS PER RENDER, NOT LAZINESS.** The value CACHES — which is what
    keeps one render consistent, and is exactly why it must be REBUILT before every render that repoints the
    object. One bound per session serves the FIRST group's rows to every later group: right column names,
    right shape, wrong rows, no error anywhere. `BindLazyRelation` is called immediately after
    `DefineGroupView` / `StageInput`, so the SQL view and the Liquid value are repointed together and the two
    paths cannot disagree about which group they are in. §34's first row is the discriminator — SQL count and
    Liquid `.size` per group, and the numbers CHANGE (2, 2, 1 at `batchsize := 2` over five rows) where a
    session-scoped value reports 2, 2, 2. The mutant dies there after 742 pass.
  - **⚠ THE LATERAL NEEDED NOTHING EXTRA DESPITE BEING PARALLEL**: one session, one connection and one
    `TemplateContext` per pipeline thread, so a per-call `SetValue` on that thread's context is
    thread-confined by construction.
  - **⚠⚠ RETAINING THE INPUT BATCHES INSTEAD WOULD HAVE BEEN A USE-AFTER-FREE, not merely expensive** —
    `IHostQuery.RegisterRows` documents the rows as **borrowed, not adopted**, and the framework frees a
    collector's input chunk once consumed. So §27.1's cost argument and this one point the same way for
    different reasons, and this half is the stronger of the two.
  - ⚠ It costs NOTHING unless the template reads it: the rows were already staged in DuckDB (`__fab_input`
    for the collector, a temp table for the lateral), so there is no copy and no crossing added.
  - **⚠ THE `bind_saw_0` ROW IS A CHARACTERIZATION and the suite says so** — it pins that the probe's
    `input_table` is EMPTY rather than sampled, by rendering the size into the output COLUMN NAME so a wrong
    value would be refused by the drift check. No mutant of ours reaches it: removing the bind-time binding
    makes `{{ input_table.size }}` render EMPTY, so the probe's own SQL fails to parse and §34's FIRST row
    catches it. **A mutant dying in the right section is not the same as a mutant dying at the row you aimed
    it at** — check which, and relabel rather than claim coverage.
  - ⚠ A template's own `{% assign input_table = … %}` SHADOWS ours (we bind before the render — the safe
    direction) and only on the Liquid side; the SQL object is a different namespace and keeps the rows.
    Asserted as a pair. ⚠ It IS a behaviour change, not a pure addition: the name previously resolved to
    nothing in Liquid, so a template rendering `{{ input_table }}` used to get an empty string.

- **⚠⚠ `{% ret %}` — END THE RENDER HERE, KEEP WHAT WAS WRITTEN. BUILT 2026-09-05 (user-asked). C#-only IN
  THE PLUGIN: NO ABI change, NO C++ change, NO bridge change. Gate `verify_plugin_fluid` 563 → **599**,
  hermetic floor 8839 → **8875**, three mutants. Full record:
  [docs/fluid-templating.md](fluid-templating.md) §23.**
  - **⚠⚠ IT HAD TO BE AN EXCEPTION, AND BOTH ALTERNATIVES ARE MEASURED WRONG.** Liquid's `Completion` has
    three values and `FluidTemplate.RenderAsync` — the ROOT — awaits each statement's completion and NEVER
    INSPECTS IT, so `A{% break %}B` renders **AB** and only a `{% for %}` consumes a Break at all; a
    completion-based `ret` is silently ignored exactly where it is wanted. The other candidate — an
    IFluidOutput wrapper that discards writes after the tag — produces the same TEXT while every statement
    after it still RUNS, so an `{% exec %}` below a `{% ret %}` would still write. **"Stop" has to mean
    stop**, and the gate pins it (audit table at 0). ⇒ a private `FluidEarlyReturn`, caught in `RenderOn`.
  - **⚠⚠ CATCHING IT MEANT OWNING THE OUTPUT, WHICH DRAGGED IN THE CHILD SCOPE — the part that would have
    been easy to lose.** Fluid's `Render(ctx)` builds the StringWriter INSIDE itself, so `RenderOn` now
    renders into its own `TextWriterFluidOutput` — i.e. reproduces that extension rather than calling it,
    `EnterChildScope`/`ReleaseScope` included. That pair is what makes a `{% assign %}` NOT survive a
    render, which `fluid_query_batch` measures and §25 pins. **Mutant G (drop both) dies after 423
    assertions at a PRE-EXISTING §25 assertion** — the stronger kill, since the property was already
    correctness-bearing and this change would have quietly broken it.
  - **⚠⚠ THE DESIGN NOTE'S "THE TAG MUST FLUSH, BECAUSE THE CATCH SITE CANNOT" IS FALSE AT THIS PIN, for a
    reason this file already records once**: `TextWriterFluidOutput.DisposeAsync` flushes, which is what let
    an earlier `{% exec %}` flush-mutant SURVIVE. The tag flushes nothing; the catch site does. ⚠ And the
    flush is NARROWER than "the output buffers" — `FluidTemplate.RenderAsync` ends with a flush of its own,
    so an ordinary render needs nothing from us and **the EXCEPTION is what skips it**. Mutant D (drop the
    flush) passes **563** and dies at the FIRST `{% ret %}` assertion, which is what shows it serves that
    path and only that path.
  - **⚠⚠ THE ONE DIVERGENCE FROM SCRIBAN, PINNED RATHER THAN DESCRIBED: inside an `{% include %}` it ends
    the WHOLE render, not just the included page** (measured, with a no-`ret` control beside it;
    `{% render %}` behaves identically although standard Liquid isolates its scope). Fluid renders an
    include as a NESTED `FluidTemplate` whose root discards completions the same way, so stopping just the
    include would mean re-implementing `{% include %}` and its whole grammar. ⚠ Arguably the better reading
    HERE — an include is a FRAGMENT of the statement being built, not a function call — but it is a
    divergence from the thing it emulates, so it is asserted.
  - ⚠ The scope stack stays BALANCED when the exception unwinds: `{% for %}`, `{% include %}` and
    `{% render %}` all release their child scope in a `finally` (read at the pin), and it is MEASURED where
    it would bite — three `fluid_query_batch` groups on one shared context, each unwinding out of a loop,
    counter reading 1, 1, 1.
  - ⚠ Works on every surface (all four go through `RenderOn`); in `fluid_replacement_query` it TRUNCATES the generated
    statement. Takes no arguments — Fluid says so at PARSE.
  - **⚠⚠ A SEPARATE, PRE-EXISTING FINDING IT SURFACED AND DID NOT FIX: a SESSION-scoped
    `fluid_template_root` does not reach every surface.** MEASURED with a relative `{% include %}`:
    `fluid_render` ✓, `fluid_replacement_query` ✓, **`fluid_query_batch` FAILS AT BIND and works at SCAN**,
    `fluid_query_lateral` FAILS AT CALL. **Workaround, complete and measured: `SET GLOBAL`.** ⚠ The
    bind/scan SPLIT is what makes it diagnosable, and it took a three-way A/B: with BOTH layers set the
    collector renders the SESSION value, while with the session layer alone its BIND fails — so the bind
    reads the global layer. Confirmed by an `{% if is_bind %}` branch that avoids the include at bind, which
    renders correctly under a session-only root and which no other explanation predicts. Same class as the
    slice-4 finding that a plain `SET` was invisible to `fluid_render` (fixed for scalars by ABI v82); both
    crossings DO call `FabricatorSetActiveTxn`, which sets the settings session, so the mechanism is there
    and something defeats it. ⚠ Its own change — the v80 record warns the ambient plumbing is delicate.

- **⚠ PARENTHESES ARE ENABLED ON THE FLUID PARSER (2026-09-05, user-asked): `AllowParentheses = true`
  beside `AllowFunctions` in `FluidEngine.CreateParser`. One line. Gate `verify_plugin_fluid` 455 →
  **459**, hermetic floor 8731 → **8735**, one mutant. Full record:
  [docs/fluid-templating.md](fluid-templating.md) §21.**
  - **⚠⚠ IT IS NOT A CONVENIENCE — LIQUID HAS NO OPERATOR PRECEDENCE and evaluates strictly RIGHT TO LEFT,
    so `a or b and c` is `a or (b and c)`.** MEASURED on one bag (a true, b false, c false): ungrouped
    answers **yes**, `(a or b) and c` answers **no**. ⇒ the grouped condition was INEXPRESSIBLE, not merely
    clumsy — and a template that generates SQL is exactly where a mixed and/or condition turns up. §27 pins
    the pair WITH the ungrouped row as the control (the grouped `no` alone would be equally true of a build
    where the condition had stopped evaluating at all).
  - ⚠ A PARSER option, not a `TemplateContext` one (the user's phrasing was "on the templatecontexts"), so
    it must be set where the parser is BUILT: templates are cached by TEXT, and one parsed before the option
    was set would stay cached — rejected — for the process's life. Same rule the `{% exec %}` registration
    records. ⚠ Fluid names the option in its own parse error, which is how the need surfaces at all.
  - ⚠ Both parser options are on at once and a function call is itself parenthesised, so §27 also pins that
    `query(...)` still parses — enabling grouping did not disturb the call syntax it depends on.

- **⚠⚠ THE FLUID PARAMS BAG IS NOW BOUND WHOLE TOO, under the name `params` — `params.x`, `params[0]`,
  `params.size`. BUILT 2026-09-05 (user-asked: "let us lift this … assign params to variable name
  `params`"). C#-only in the plugin, ONE function (`FluidValueModel.Capture`); NO ABI, NO C++. Gate
  `verify_plugin_fluid` 443 → **455**, hermetic floor 8719 → **8731**, two mutants. Full record:
  [docs/fluid-templating.md](fluid-templating.md) §20.**
  - **⚠⚠ THE ASSUMPTION IT LIFTS COST MORE THAN ERGONOMICS, and the LIST row is why it is a FIX.** The bag
    was readable only through its MEMBERS, so it had to HAVE members: a JSON array was REFUSED outright
    (*"params JSON must be an OBJECT"*) and **a DuckDB `LIST` matched no case in the walk and bound NOTHING,
    SILENTLY** — a template reading it rendered empty with no error anywhere. That is the silent-wrong-answer
    class, on the shape a caller reaches for most naturally from SQL (`params := ['a','b']`), and in
    `fluid_replacement_query` what renders empty is spliced into a STATEMENT. A bare scalar was equally silent.
  - **⚠⚠ THE MEMBER SPREAD IS GONE — user decision the same day, BREAKING, NO ALIAS.** `{{ n }}` renders
    EMPTY; `{{ params.n }}` is the only spelling. Shipped ADDITIVE first (the literal ask and the reversible
    half), then removed on the user's "yes, only this spelling". ⚠ **`is_bind` STAYS TOP LEVEL** (user
    preference, and what `fluid_query_batch` already did) — it is a host AMBIENT, not a member, and with the
    spread gone it can no longer be shadowed by a member of the same name (MEASURED beforehand that the
    ambient already won and the member stayed reachable as `params.is_bind`).
  - **⚠⚠ MY BLAST-RADIUS ESTIMATE WAS WRONG BY ~8x AND CORRECTING IT IS WHAT MADE THE DECISION INFORMED.**
    I said "~a dozen gate rows"; MEASURED, `verify_plugin_fluid` passes a bag at **~98 call sites** (48
    positional struct/MAP literals, 29 `params := …`, 21 JSON-string bags) of 278 `fluid_*` calls, plus 8
    README examples. The wrong figure had already reached the summary, a commit message and the doc.
  - **⚠⚠ HOW A ~100-SITE REWRITE WAS MADE SAFE, worth reusing: MEMBER-DRIVEN, NOT IDENTIFIER-DRIVEN.** For
    each sqllogictest block the member names come from that block's OWN bag literals, and only those names
    are rewritten — which is what keeps LOOP VARIABLES out of it (`{% for c in cols %}{{ c }}` rewrites
    `cols`, leaves `c`, because `c` is not a member). Two passes (the second for bags written `{v: …}`
    rather than `{'v': …}`) covered 98 regions; **the SUITE then found the rest, and each was a case the
    script could not have known**: five CROSS-BLOCK sites where the bag arrives as `params := ?` or belongs
    to the INCLUDING render, and one FALSE POSITIVE — `{{ d | date: "%Y-%m-%d" }}` became `%Y-%m-%params.d`,
    the lookbehind having guarded `.` and word chars but not `%`. ⇒ **a rewrite this size cannot be
    eyeballed; the suite has to be the oracle**, and every failure named its own line.
  - **⚠ THE ASSERTION COUNT DID NOT MOVE (455) ACROSS THE REMOVAL, which is why the removal needed a row of
    its OWN.** Templates were rewritten, not assertions added — so §26's first row is now
    `bare=[{{ n }}] dot={{ params.n }}` expecting `bare=[] dot=7`. Without it nothing would notice the
    spread coming back; every other row passes just as happily with it. Mutation-tested (restoring the
    STRUCT spread dies exactly there after 442 pass).
  - ⚠ `params[0]` is FREE and not a second mechanism — Fluid resolves an index by asking `TryGetValue` for
    the KEY `"0"`, and `EagerStruct` already has the int-parse fallback `ArrowStruct` documents. ⚠ `ArrowMap`
    deliberately does NOT, so `params[0]` on a MAP does not resolve; pinned as an asymmetry rather than
    papered over.
  - ⚠ UNCHANGED on purpose: invalid JSON is still an ERROR (a VARCHAR bag IS JSON, and binding unparseable
    text as a string would hide a typo in the caller's own JSON — only the *object-only* half was lifted);
    a NULL bag binds nothing at all, `params` included, so `{% if params %}` is how to ask.
  - **⚠⚠ `.size` AND `params[0]` ALONE WOULD NOT HAVE CAUGHT A BROKEN BUILD** — the header of
    `FluidValueModel` records that a `JsonNode` bound with no converter RENDERS CORRECTLY WHILE COMPUTING
    WRONG. So §26's JSON-array row asserts a `{% for %}` **SUM** and the scalar row asserts `| plus: 1`.
    Arithmetic is the only thing separating a real value from one that merely renders like one.
  - ⚠ The row pinning *"params JSON must be an OBJECT"* is REPLACED, not deleted — that refusal WAS the
    assumption being lifted, so falsifying it is the change announcing itself; a note at the old site points
    at its replacement. ⚠ And my first expected value for the shadowing row was WRONG (copied from a
    one-member probe into a two-member row) — compute or RUN an expectation, never transcribe one.

- **⚠⚠ `fluid_query_batch(template, <input> [, params :=] [, batchsize :=])` — A TEMPLATE RENDERED WITH A
  RELATION IN HAND, and its statement run. BUILT 2026-09-05 (user-designed over two rounds; the user's own
  correction — "whole table does not work, we have a special collector function for this" — settled the
  mechanism). C# in the plugin + two host members + ONE C++ marshal fix; **NO ABI change**. Gate
  `verify_plugin_fluid` 397 → **443**, hermetic **75/75 — 8719** (8673 + exactly 46, so no other suite
  moved), THREE mutants each killed at its own assertion. Full record:
  [docs/fluid-templating.md](fluid-templating.md) §19.**
  - **⚠⚠ IT IS A COLLECTOR AND THAT IS FORCED.** The default — no `batchsize`, ONE render over the whole
    input — emits nothing until input EOF, and the streaming in-out operator CANNOT express that: its only
    all-input-done hook is handed no `DataChunk`, so output held back until EOF is DRAINED AND DISCARDED.
    ⚠ `kind` is fixed at REGISTRATION, so one name cannot switch operators by parameter — which is why
    `batchsize` lives on the collector rather than selecting between the two. ⚠ The price is inherent:
    **the whole input is buffered before the first render**, even at a small `batchsize`, so `batchsize` is
    about how many rows each RENDER sees and NEVER about memory.
  - **⚠⚠ THE USER'S OWN SKETCH HANGS, AND MEASURING IT SHAPED THE SURFACE.** A `publish()` of a staged
    table inside it deadlocks — 2 min, killed, 13.5 s CPU — while the identical template through
    `fluid_replacement_query` returns in seconds as the control. `fluid_replacement_query`'s publication is scanned by the CALLER's
    plan on a DIFFERENT connection; here WE run the generated statement on the render's own pin, so the
    publication's factory opens a second query on the connection already mid-query. ⇒ **`publish()` is
    REFUSED by name here**, and nothing is lost — a publication carries a relation ACROSS a connection
    boundary and there is none: selecting the staged table directly just works. ⚠ A test reproducing the
    hang would HANG the tier, so what is pinned is the refusal, with the still-allowed `fluid_replacement_query`
    publish beside it as the positive control.
  - **⚠⚠ THE INPUT TABLE NEEDED NO ABI CHANGE, and one measurement is why: `fabricator_scan` RESOLVES ON A
    PINNED CONNECTION** (a `{% query %}` on a render's pin read `fabricator_demo_numbers`' 3 rows). The
    named-source registry is process-global and the replacement scan lives on the DatabaseInstance, so rows
    go managed → `Host.RegisterSource` → a `CREATE TEMP TABLE … AS SELECT * FROM fabricator_scan(<tok>)` on
    the pin. That SIDESTEPS the obvious route rather than negotiating with it — named Arrow INPUTS are
    refused on a pinned connection, and lifting that re-opens host-query.md §17.6's lifetime hazard. Two
    new DIMs on `IHostQuery`: `RegisterRows(RecordBatch)` (⚠ **BORROWED, not adopted** — nothing copies or
    disposes it, which is what makes it usable from a collector whose chunks it does not own) and
    `RegisterRows(Schema)` for an EMPTY relation, which exists because spelling the columns in SQL would
    mean an Arrow→DuckDB type-name table by hand. ⚠ The Bridge's stream is deliberately NOT
    `InMemoryArrayStream`, whose `Dispose` disposes the batches it was given. **It also closes
    host-query.md §17.11's open case** (data originating in C# with no SQL of ours producing it).
  - **⚠ `is_bind` SELECTS WHAT TO RENDER; THE SCHEMA COMES FROM BINDING WHAT WAS RENDERED.** The user
    proposed it as a way to DECLARE the columns; what ships takes them from wrapping the generated
    statement in a `LIMIT 0` subquery — Publish's own probe, which binds without scanning AND requires a
    subquery-usable SELECT. A declaration written twice drifts, and the drift would be read as DATA. What
    the flag genuinely buys is skipping expensive setup, since binds REPEAT. ⚠ Defined ONLY here: in
    `fluid_replacement_query` there is no second kind of render to tell it apart from.
  - **⚠ EXACT SLICING NEEDED A STAGING TABLE.** Rows are copied into `__fab_input` with a `__fab_seq` row
    number as they arrive and `input_table` is a temp VIEW over a RANGE of it — so `batchsize := 2` over 5
    rows is 2, 2, 1, not "at least 2 rounded up to an input chunk" (batch-aligned grouping would make
    `batchsize := 1` mean 2048). A view, so no second copy. And the rows must leave managed memory
    immediately anyway: the collector frees a chunk's Arrow buffers once consumed, so accumulating batches
    to form a group would be a use-after-free. ⚠ An input column named `__fab*` is refused at bind; an
    EMPTY input still renders ONCE (the template is a generator and its output need not depend on the rows).
  - **⚠⚠ A CLAIM I WROTE INTO THE CODE COMMENTS AND THE PROBE FALSIFIED:** one shared `TemplateContext` does
    NOT make a Liquid `{% assign %}` carry between groups — Fluid renders into a CHILD SCOPE and pops it.
    MEASURED in one run: a per-group counter read **1, 1, 1** where a temp-table counter read **1, 2, 3**.
    ⇒ **SQL state carries, Liquid state does not**, and the half the user asked for ("keeping state e.g. in
    temp tables") is the half that works. Both are pinned, the Liquid one as a CHARACTERIZATION test no
    mutant of ours can kill. ⚠ The context is per EXECUTION, not per binding (a binding is reused across
    prepared re-executions), which is also why the params bag is CAPTURED rather than retained — safe
    because `ReadCell` is eager all the way down, checked rather than assumed.
  - **⚠⚠ TWO PRE-EXISTING DEFECTS IT EXPOSED, BOTH LATENT UNTIL THE FIRST CALLER.**
    (a) `FabricatorMarshalInOutArgs`'s NAMED branch pushed the DECLARED type unconditionally while the
    POSITIONAL branch two lines below already resolved the SQLNULL/ANY sentinel — so an ANY-declared NAMED
    parameter was unusable in BOTH directions (supplied: *"Failed to cast value … -> NULL"*; omitted: the
    untyped NULL Apache.Arrow refuses). `fluid_query_batch` is the FIRST in-tree in-out/collector to declare
    one. (b) **`FabricatorExchangeBind` and `FabricatorCollectorBind` established NO AMBIENTS** while their
    `InitGlobal`s did, so managed code in the author's `Bind()` read whatever the last crossing left — a
    dangling `ClientContext *`, MEASURED as `host_connection_open failed: vector too long` and NON-ZERO, so
    the null guard waved it through. ⚠ `FabricatorSetActiveTxn`'s own comment already named "a global
    collector/in-out" as its case; the bind sites were simply missed. ⚠ Its mutant kills reliably but at a
    VARYING line — the signature of the bug it guards, not a weak gate. ⚠ The EXCHANGE half of the fix is
    UNGATED: no in-tree in-out binding does host work in `Bind`.
  - **⚠⚠ A PRE-EXISTING CI FLAKE FOUND ON THE WAY, in `verify_plugin_fluid` §23, now fixed.** The
    two-publications-in-one-statement row pinned an alternation over TWO plan-dependent messages; a THIRD is
    reachable (the SINGLE-USE refusal — one token scanned twice rather than two at once). MEASURED **~1 run
    in 6**, and ATTRIBUTED rather than guessed: it reproduces against the UNMODIFIED suite file, while the
    same statement run ALONE gives "open result stream" 10/10. Alternation widened; all three ARE the
    property under test (a loud refusal).
  - **⚠⚠ A HARNESS TRAP THAT VOIDED A MUTATION RUN: `verify_plugin_fluid` is NOT RE-RUNNABLE against the
    same `FABRICATOR_DELTA_WRITE_DIR`.** First run passes, every later one with the SAME directory fails at
    the no-root include refusal. Invisible in CI because `run-suites.sh` gives each suite a fresh scratch
    dir — and my first mutant loop reused one, so three runs of ONE mutant died at three DIFFERENT lines.
    **A fresh-dir control is what separated the mutant from the harness.** Use a fresh `mktemp -d` per run.
  - **STILL OPEN, deliberately:** the LATERAL form (parallel, host-stamped correlated columns) needs a
    mandatory row id in every generated statement plus a naming rule, since a lateral's wire columns are
    named by their rendered EXPRESSION TEXT (`(t.a + 1)`, `CAST(5 AS SMALLINT)`) — a STRUCT argument would
    dissolve the naming half; a bounded-memory batched variant is the SAME body registered on the streaming
    in-out; and exposing the rows as a Fluid VALUE would save a round trip that a `{% query %}` over
    `input_table` already does in one statement.
  - ⚠ Two STALE doc comments found and NOT fixed (they belong to a separate pass): `ParamStyle.VarArgs`'s
    remark and `Params.Validate`'s refusal message both say varargs are "deferred for lateral and in-out",
    which the `allowVarArgs` doc six lines above contradicts and both lateral registration sites disprove
    (`allowVarArgs: true`).

- **⚠⚠ `publish(name)` — A TABLE THE TEMPLATE STAGED BECOMES THE RELATION THE GENERATED SQL SCANS. BUILT
  2026-09-04 (user-asked, and the user chose this spelling over their own `fluid_table(this,'_result')`
  sketch). C#-only: NO ABI change, NO C++ change, and NO new SQL function — it renders a call to the
  `fabricator_scan` that already existed. Gate `verify_plugin_fluid` 344 → **386** (→ **397** with ABI v85's
  §24), hermetic floor 8620 → **8662** → **8673**, THREE mutants each killed at its own assertion. Full record:
  [docs/fluid-templating.md](fluid-templating.md) §18 (§18.8 as built).**
  `SELECT * FROM {{ publish('_result') }}` after an `{% exec %}CREATE TEMP TABLE _result AS …{% endexec %}`.
  - **⚠⚠ NOTHING ELSE CAN CARRY A STAGED RELATION, AND CHASING THE ALTERNATIVE CORRECTED A SHIPPED DOC.** A
    TEMP table belongs to the ClientContext that made it; a REAL table created during `bind_replace` is
    invisible to the statement being bound. **But §11.1b's "a template cannot create a table the same
    statement selects from" is NARROWER than it says** — MEASURED, an ATTACHed catalog the outer transaction
    has not yet touched DOES see it (`a = 42`), and it is **not** qualification that decides it (qualified
    `memory.qt` fails, bare fails, `USE scratch` + bare fails). The discriminator is **whether the
    transaction has already touched that catalog** — `MetaTransaction`'s lazy per-`AttachedDatabase`
    transaction start — proven with the pair that isolates it: one explicit transaction, one preceding
    `SELECT count(*) FROM scratch.seed` ⇒ the identical statement FAILS; without it ⇒ `a = 8`. ⇒ **a timing
    artefact, never a route** (docs §11.1b-i), and it is the argument FOR a table function: a marshaled scan
    asks the caller's catalog nothing, so the snapshot rule cannot reach it.
  - **⚠⚠ IT IS LAZY (user-directed after reading the first build: "i actually would have prefered a lazy
    approach without buffering and automatic release of resources after scan") — AND MY FOUR REASONS FOR
    BUFFERING WERE ONE AND A HALF. Full correction: docs §18.9.** The lead argument — *a lazy publication
    would hold the pin's ONE live stream and poison every later `query()`/`exec()` in the same render* — is
    **plainly WRONG**: a lazy publication opens nothing at publish time, and the stream opens at SCAN time,
    by which point the render is over and there are no later calls to poison. The single-threaded-connection
    reason collapses into the same case (MEASURED: 500k rows at `threads=8` invokes the factory ONCE), and
    the Bridge-only-service reason was NEUTRAL (true of either design).
    - **⚠⚠ AND THE MEASUREMENT I CITED FOR THE LEAD REASON WAS VACUOUS** — *"a `{% query %}` AND an
      `{% exec %}` after a publish both still work"* passes on the LAZY build too (re-measured), because a
      lazy publish leaves no live stream either. **A measurement both designs satisfy is not evidence for
      one of them.**
    - **⚠⚠ THE ONE REAL REASON — the pin must outlive the render — WAS MUCH CHEAPER THAN PRICED, BECAUSE
      THE REFCOUNT ALREADY EXISTS IN C++ FROM v84.** `Host.HostConnection.Dispose`'s own remark: *"safe with
      result streams still outstanding: each holds its own reference to the underlying connection, so it
      dies with the last of them"*. ⇒ once `Query` has RETURNED, nothing managed need keep the connection
      alive — the stream does, and the staged temp table's catalog lives exactly as long. So only the HANDLE
      must survive the render, which is a plain refcount on `PinnedHostConnection` released **the moment
      `Query` returns**, and there is NO wrapper stream at all. ⚠ `Dispose` must be IDEMPOTENT or an
      over-decrement closes the connection under an unscanned publication.
    - **THE WIN, MEASURED: NO ROW CAP.** 3,000,000 rows through one publication, checksum exact, where the
      buffered build refused above 1,000,000. Gated at 1.2M (~0.5 s).
    - **⚠⚠ THE COST, AND IT IS A CAPABILITY THE BUFFERED BUILD HAD: two publications from ONE template
      cannot be scanned in one statement** — both stream from the render's single pin, which allows one live
      result. MEASURED, and the message is PLAN-DEPENDENT: the JOIN spelling gives our v84 refusal, the
      `UNION ALL` spelling gives DuckDB's *"closed pending query result"*. **Neither is silent**, which is
      what makes the trade acceptable, and the gate's expected text is an ALTERNATION over both because
      which one wins is DuckDB's plan choice. TWO workarounds, both gated: two separate `fluid_replacement_query` calls
      are two pins; or do the join in `{% exec %}` and publish ONE relation — **better anyway**, since the
      work stays inside DuckDB instead of crossing Arrow twice.
    - ⚠ **A serializing lock was REJECTED** (the measured JOIN opens both streams before draining either, so
      blocking the second deadlocks whenever the executor needs it first — and a hang is worse than an
      error), and **a hybrid — lazy for the first publication, buffered for the rest — was DECLINED**: it
      makes memory behaviour depend on the ORDER of `publish()` calls, the "runs and means something
      different" shape this file keeps warning about.
    - ⚠ The other cost INVERTED rather than disappearing: an unscanned publication now holds a **DuckDB
      connection and its staged table** until the eviction cap reclaims it, where buffering held **rows in
      managed memory** under a row cap. Same cap, different resource; `EXPLAIN` is the routine producer.
  - **⚠⚠ THE DECLARED-SCHEMA OVERLOAD OF `Host.RegisterSource` IS THE KEYSTONE, not an optimisation.** It is
    what makes a BIND answer from the declaration instead of opening a stream to learn the columns — and
    since a publication is SINGLE-USE, a bind that opened one would CONSUME it. **Mutant A (drop the schema)
    dies at the FIRST §23 assertion after exactly 344 pass**, i.e. at the section boundary.
  - **⚠ SINGLE-USE, failing LOUDLY** — one token, two references is an ERROR naming the fix, never zero rows
    (the silent-short-read class). ⚠ The fix its message names (publish again) is itself bounded by the
    two-publications limitation above, and the gate says so.
  - **⚠⚠ MUTANT D IS THE ONE THAT PINS LAZINESS: the publication takes no pin reference ⇒ dies at the FIRST
    §23 assertion after exactly 344 pass, with `Cannot access a disposed object. Object name:
    'HostConnection'`** — the pin closes at end of render and the scan cannot issue its query. The mechanism
    named by its own failure. (Mutant A, dropping the declared schema, dies at the same boundary; mutant C,
    never evicting, at 376.)
  - **⚠⚠ THE EVICTION CAP IS A ROUTINE PATH, NOT A DEFENSIVE ONE, and its first version named the token but
    not the cause.** Managed code cannot observe "the caller's statement finished", and **an `EXPLAIN`
    renders the template and never scans**, so an unscanned publication is reclaimed as the oldest of 32.
    The first build UNREGISTERED on eviction ⇒ the factory was never reached ⇒ the generic *"no named source
    registered as '__fabpub_…'"*. **Measuring that path is what showed it was wrong**; evicted tokens now
    stay registered as bounded TOMBSTONES so they explain themselves. Mutant C (never evict) dies there
    after 372 pass.
  - **⚠⚠ WHAT JUSTIFIES IT OVER `{% print sql_literal %}`, MEASURED AND GATED AS A CONTRAST: TYPES SURVIVE**,
    because the rows never become SQL text. `DATE` → `DATE`, `[1,2,3]` → `INTEGER[]`, `{'a':7}` →
    `STRUCT(a INTEGER)` — where the literal renderer collapses every temporal to TIMESTAMPTZ and REFUSES a
    list or struct by name (the refusal is asserted immediately below the publish row).
  - **⚠ AND IT IS NARROWER THAN THE EXAMPLE SUGGESTS — the sketch's own body is a single SELECT, which a
    `WITH … AS MATERIALIZED` CTE does better on every axis** (no buffer, no Arrow round trip, full pushdown;
    measured 4 rows / sum 30, and gated so the choice is not folklore). `publish` earns its keep for a
    relation computed in SEVERAL steps.
  - ⚠ ONE identifier, quoted (`publish('pub odd')` works; a DOTTED name is one identifier and fails as
    "table does not exist"). Token is an opaque `__fabpub_<32 hex>`, never an address — it goes into SQL
    TEXT, where a copyable re-runnable address is the use-after-free class §17 exists to have closed.
    Registered on BOTH surfaces and inert in `fluid_render` (text nobody binds, per row); not refused,
    because branching on the caller's name is what the `exec()` decision rejected.
  - **⚠ TWO TRAPS PAID FOR.** `dotnet build dotnet/Fabricator.FluidPlugin` **DOES NOT COMPILE THE BRIDGE**
    (the plugin deliberately references only Abstractions + Common), so a missing `using Apache.Arrow.Ipc;`
    reported *"Build succeeded"* and failed at `publish-managed.ps1` — build the Bridge or publish before
    believing a green plugin build. And **`EXPLAIN` cannot be a subquery source**, a recorded trap walked
    into anyway; the gate uses the `<REGEX>:` form on `physical_plan`, which is also stronger (it asserts
    the rendered scan reached the PLAN).
  - **⚠⚠ AND THE USER THEN MEASURED IT SLOW, WHICH FOUND THE REAL COST AND CORRECTED MY DIAGNOSIS —
    ABI v85 (2026-09-04). Their billion-row publication took ~20 s where the same relation left in the
    generated SQL took 2.15 s, and they pointed out that dropping to ONE COLUMN barely helped, which
    rejected my first explanation (the missing projection pushdown).** Decomposed at 100M rows: no boundary
    **0.308 s**, `fabricator_host_query` 1 col 1.861 s, publication 1 col 2.381 s, publication 2 cols
    3.906 s ⇒ **projection was only the last gap; ~6x was the BOUNDARY.**
    - **THE CAUSE: the exported Arrow batch was ONE DuckDB `DataChunk` — 2048 rows** — so a billion rows
      crossed as ~488,000 batches, each paying a mutex acquisition, an `ArrowAppender` copy, an import, an
      export and converter setup, **because the exported batch IS the morsel of a parallel Arrow scan**.
      `HostQueryGetNext`'s own comment had recorded the win as DEFERRED, with the reason, and an env hook
      that produced the numbers.
    - **FIXED by ABI v85** — `host_query` takes `batch_rows`; a publication asks for a row group (122880).
      **MEASURED: the user's query ~20 s → 7.89 s.** ⚠ Their TWO-column form went 27 s → 20.3 s, less,
      because with the per-batch overhead gone the un-pruned column is now dominant.
    - **⚠⚠ IT CAN NEVER BE A BETTER DEFAULT AND THAT IS THE DESIGN: A BATCH IS ALSO A FILE.** EW writes one
      parquet file per input batch and this service feeds WRITERS (the OPTIMIZE recluster's ORDER BY,
      sorted-by writes) — a row-group default made `verify_delta_clustered_optimize` collapse 80000 rows
      into ONE file, `delta.targetFileSize` silently unhonoured (147 passed at one chunk, 1 failed at
      122880). ⇒ only the CALLER knows; **a publication is the consumer for which a big batch is
      unambiguously safe**, its stream being scanned into the caller's DuckDB and never written.
    - ⚠ `FABRICATOR_HOST_QUERY_BATCH_ROWS` still OVERRIDES the caller when SET, **including to 0**, and
      "unset" is distinguished from "set to 0" for exactly that — an experiment hook code can silently
      outvote is not one.
    - ⚠ **The batch size is NOT observable from SQL**, so the gate pins what is: the accumulation loop's
      boundaries (exactly one batch / one over / one under / several plus a tail / empty / a multi-column
      boundary, since a per-column appender bug keeps the counts right and the pairing wrong). **Before v85
      that loop was reachable only through the env var, which no suite sets** — so it had never run here.
  - **⚠ STILL OPEN, deliberately:** a per-statement release would retire the eviction cap but needs the C++
    bind data's destructor to report through the ABI (an ABI change); a `publish`-a-query form would cover
    qualified and computed sources; and **projection pushdown through a publication is now the biggest
    remaining cost for a WIDE relation** — the obstacle is the DECLARED SCHEMA, the same mechanism that
    makes laziness work (a projected stream delivers fewer columns than declared and trips the
    mismatch check). Filter pushdown has no such obstacle and is the cheaper half.

- **⚠⚠ THE FLUID `exec()` — BUILT 2026-09-02 (user-asked: "i want a exec() in fluid as well"). C#-only, in
  the PLUGIN: NO ABI change, NO C++ change, NO bridge change. Full record:
  [docs/fluid-templating.md](fluid-templating.md) §11.** Gate `verify_plugin_fluid` **188 → 234** (service **54/54 — 3318**),
  THREE mutants each killed at its own assertion; hermetic **74/74 — 8259** (unchanged — no hermetic suite
  loads this plugin) and service **54/54 — 3302** = 3272 + exactly this suite's 30, which is what shows no
  other suite moved. It also gives `IHostQuery.ExecuteNonQuery` its first caller, closing the gap recorded
  hours earlier.
  - **⚠⚠ IT IS AVAILABLE ON BOTH SURFACES — USER DECISION, 2026-09-02: "no problem to have a exec() in
    render or query". THE FIRST BUILD REFUSED IT IN `fluid_replacement_query` AND THAT MECHANISM IS DELETED.** What
    replaces it is not silence: the gate PINS the cost as asserted behaviour, which is a stronger record
    than a refusal plus prose.
    - **A `fluid_replacement_query` write MULTIPLIES, and the last two rows are the ones that bite.** MEASURED, one
      counter through four steps that execute nothing the caller wrote: `EXPLAIN` of a never-run statement
      ⇒ **1**; merely `CREATE VIEW` over it ⇒ **2**; one `SELECT` from that view ⇒ **3**; a SECOND select
      ⇒ **4**. ⇒ **a writing template behind a view writes ON EVERY USE** — and it looks fine in testing,
      where the statement runs once. `EXPLAIN` writing is merely startling.
    - **`fluid_render` differs for a reason worth keeping straight**: it is a VOLATILE scalar (the
      `IScalarFunction` default, not overridden), so DuckDB never folds it into the PLAN — `EXPLAIN` of a
      render containing `exec()` leaves the table unchanged (measured). **Its multiplier is ROWS, not
      binds**: 3 rows ⇒ 3 writes, gated.
    - **⚠ WHY THE MECHANISM WAS DELETED RATHER THAN DEFAULTED ON.** With both surfaces permitting exec, an
      `allowExec` every caller passes `true` is vestigial machinery that READS as a restriction while
      restricting nothing. And the refusal never made bind-time writes impossible, only inconvenient — see
      the §11.1a entry below, where a write reached bind time through `query()` before `exec()` existed.
    - **TO RESTORE A RESTRICTION the design is recorded in §11.1 rather than left in git history**: a
      per-render permission as a `TemplateContext.AmbientValues` flag (it cannot be a captured variable —
      the FILTER form is registered once on the shared `TemplateOptions`), **fail-closed**, set by each
      surface. ⚠ Do NOT derive it from the caller's NAME: an unrecognised name reads as "not fluid_replacement_query"
      and would be ALLOWED, so a surface added later would default to the dangerous answer.
    - **⚠⚠ AND A STATEMENT CANNOT SEE THE WRITE ITS OWN TEMPLATE MADE — "prepare then select" DOES NOT
      WORK (§11.1b, found by asking what exec() in `fluid_replacement_query` is FOR once it was permitted, i.e. by
      trying the pattern a user would try first rather than only the hazard).** MEASURED: a template's
      `UPDATE … SET c = 42` leaves the generated SQL reading **1** while the table holds **42** afterwards.
      `exec()` runs on its own connection and the outer statement's snapshot predates the commit — the
      MIRROR of §9's documented `query()` rule, from the same one-connection fact rather than a second rule.
      - ⇒ **a template CANNOT create a table the same statement selects from**: the CREATE commits and the
        statement still says *"Table with name t does not exist!"*, helpfully adding *"Did you mean
        memory.t"* — it exists, just not for that statement. ⚠ Gated WITH an assertion that it really was
        created, so the refusal reads as a VISIBILITY result rather than a failed CREATE; the message points
        away from the cause, which is why it is pinned rather than described.
      - ⇒ **`exec()` in `fluid_replacement_query` is for side effects the statement does not itself read** — audit rows,
        logging, staging for a LATER statement. The workaround (two statements) is gated so it is not
        folklore. ⚠ A real narrowing of the capability, and nobody's fault: it is the connection model, and
        it would be there whether or not exec had ever been refused at bind.
      - ⚠ `{{ exec(…) }}` INTERPOLATES the count into the generated SQL (measured: `1SELECT c FROM t`, a
        parser error), so a template writing for effect must use `{% assign _ = exec(…) %}`.
    - ⚠ **The multiplication block is a CHARACTERIZATION test and the suite says so** — it pins DuckDB's
      bind repetition, which is not ours to implement, so no mutant of ours can kill it. Its value is that a
      change there arrives as a failed assertion naming the step rather than as a surprise in someone's
      audit table.
  - **⚠⚠ THE REFUSAL STOPS THE ACCIDENT, NOT A DETERMINED CALLER — AND THE HOLE PRE-DATES `exec()`
    (measured 2026-09-02, §11.1a; found by asking whether the boundary can be nested around).** The
    classifier calls `SELECT fabricator_host_exec('INSERT …')` a SELECT — CORRECTLY, it is one — so a
    `fluid_replacement_query` template reaches a write through `query()` **at BIND time** (measured: an audit table went
    0 → 1). ⇒ `query()`'s rule prevents a statement-level write, NOT a write performed by a FUNCTION inside a
    SELECT, and no question one could put to DuckDB's parser would catch a volatile writing scalar in a
    projection.
    - ⇒ **it is the reason the refusal was worth DELETING rather than defending.** A refusal anyone could
      walk around by nesting a scalar was never a boundary, only a speed bump for the accident — and with it
      gone the accident is PINNED as asserted behaviour instead, which is at least honest about what happens.
    - It does NOT weaken the case for `exec()` — it is the MEASURED form of "exec grants no authority a
      caller did not already have". Same conclusion §10.4 reached about the template ROOT one level down,
      for the same reason: the renderer can already run SQL.
    - ⛔ **Do NOT "fix" it by blacklisting function names in the classified SQL** — the prefix-check
      anti-pattern in a new costume, defeated by a macro, a view, or a name we do not ship. Deliberately
      UNGATED: a test asserting the bypass works would pin a behaviour we would happily lose.
  - **⚠ IT REFUSES A `SELECT`, and the reason is a WRONG NUMBER rather than a hazard.** `query()` refuses
    everything that is not a SELECT, `exec()` everything that is — ONE mechanism (`FluidHostQuery.Classify`,
    DuckDB's own parser), two opposite policies, so they cannot drift on what "a SELECT" means. Managed code
    cannot ask for `StatementReturnType::CHANGED_ROWS`, so the count is INFERRED from the first column when
    it is an Int64.
    - **⚠⚠ MEASURED with the refusal removed, and the trap is NARROWER than "any SELECT" while the narrow
      version is the LIKELY one:** `SELECT count(*) FROM range(99)` reports **99**, `SELECT 42::BIGINT`
      reports 42, and **`SELECT 42` reports 0** (an INT32 literal fails the Int64 test), as does
      `SELECT 'x'`. My first write-up claimed `exec('SELECT 42')` would render 42 — it renders 0, so pinning
      only that case would have motivated the refusal with a HARMLESS example. Both are asserted.
  - **⚠⚠ A MEASURED DIVERGENCE BETWEEN THE TWO `exec` SURFACES, and a doc that was WRONG on both sides.**
    Same statement, side by side: `CREATE TABLE c AS SELECT * FROM range(7)` reports **7** through the Fluid
    `exec()` and **0** through `fabricator_host_exec` (which asks the engine); pure DDL is 0 on both.
    Unclosable from managed code, and it must NOT be closed by matching a leading keyword — that is §9.2's
    measured-broken prefix check. **Both `ExecuteNonQuery` docs said "DDL → 0", INCLUDING the one I wrote
    the same day, and both were wrong for a CTAS** — mine was copied from the C++ surface's recorded
    behaviour instead of measured on the path it documents. Asserted as a triple in the gate.
  - **⚠ MULTI-STATEMENT: §9.2's recorded claim that the classifier "refuses multi-statement input" is
    IMPRECISE, corrected in §11.5.** MEASURED: `SELECT 1; SELECT 2` classifies as a SELECT (accepted), while
    `SELECT 1; INSERT …`, `SELECT 1; DROP TABLE t` and `CREATE …; INSERT …` are all refused. ⇒ the SAFETY
    property is intact in BOTH directions and is better than the old description — an all-SELECT sequence is
    harmless to `query()`, and any sequence containing a write reaches `exec()`, which is exactly the
    several-statements case exec exists for (the parameterised path cannot do it: `Prepare` takes one
    statement).
  - **⚠ TWO COUNT PATHS, ONE RULE, and the gate asserts they AGREE (2 / 2)** — bare form via
    `ExecuteNonQuery`, parameterised form via `Query` + a local read, because `ExecuteNonQuery` has no
    parameter overload. A rule written twice can drift; mutant C makes the parameterised path report 0 and
    dies at the filter-form assertion.
  - ⚠ NOT gated, and §11.6 says so: that `exec()` grants no authority a caller lacked (an argument about the
    surface, not an observable), and the refusal under `{% include %}` from REMOTE storage (no hermetic
    fixture has a remote root — §10's standing gap).

- **⚠⚠ A FLUID ARRAY BINDS AS A SQL LIST — BUILT 2026-09-04 (user-asked, after hitting the refusal with
  `{% query result3 arg: input %}SELECT a: unnest($arg){% endquery %}`). C#-only IN THE PLUGIN: no ABI, no
  C++, no bridge. Gate `verify_plugin_fluid` 296 → **307**, hermetic floor 8572 → **8583**, two mutants.
  Full record: [docs/fluid-templating.md](fluid-templating.md) §Values.**
  - **⚠⚠ THE REFUSAL WAS OURS, NOT DuckDB'S, AND ONE MEASUREMENT SETTLED IT**: `PREPARE p AS SELECT a:
    unnest($1); EXECUTE p([1,2,3,4,5])` yields five rows — and DuckDB does not even need the parameter
    typed. The docs had asserted *"DuckDB has no parameter form for them here"*, which was simply false.
  - ⚠ **The READ direction already supported arrays** (`FluidValueModel.ReadList` handles `ListArray` /
    `LargeListArray` / `FixedSizeListArray`); the gap was one-directional, in `ToParameter` alone.
  - **⚠⚠ ONE ELEMENT KIND PER LIST, because an Arrow list is TYPED.** A mixed list is REFUSED by name
    rather than coerced: the only common representation is text, and turning `5` into `'5'` silently
    changes what the statement compares. NULLs carry no kind, so they mix with anything. ⚠ The mixed case
    is reachable ONLY through the JSON parameter form — a DuckDB LIST is homogeneous, so `{v: [1,'a']}`
    fails in DuckDB's own struct construction first. Nested arrays/structs refused (the scalar ladder's
    one-level rule). EMPTY or all-NULL ⇒ VARCHAR, the same choice the scalar NULL case makes.
  - **⚠⚠ THE BUILD REPRODUCED THIS FILE'S OWN JANUARY-1970 TRAP IN A NEW PLACE, AND ONLY A DATE ELEMENT
    SHOWED IT.** `ListArray.Builder.ValueBuilder` does NOT carry a `TimestampType`'s UNIT into the builder
    it creates, so values were stored as MILLISECONDS under a field declaring MICROSECONDS and
    `DATE '2023-01-02'` read back as **1970-01-20 09:36:57.6**. Numbers, strings and booleans have nothing
    to get wrong, so a battery without a date would have shipped it. Fixed by building the values array
    with an explicitly typed builder and assembling the `ListArray` by hand. ⚠ The gate asserts the
    INSTANT (`epoch(...)::BIGINT`), never rendered text — a TIMESTAMPTZ renders in the session's zone, so
    pinning the string would assert the runner's locale.
  - **⚠⚠ THE GATE CAUGHT THAT I HAD FALSIFIED A SHIPPED ASSERTION, which is the change announcing itself.**
    `verify_plugin_fluid` pinned *"has no SQL parameter form"* for the FILTER spelling. It is REPLACED (the
    list now binds and `len($a)` answers 3), not deleted — the row's purpose is that the filter and block
    forms share ONE conversion table, so whatever a list does in one it must do in the other.
  - Mutants: dropping the `Array` case dies at that REPLACED assertion after 145 pass (before §17 runs at
    all, which is what shows both spellings share the mechanism); using the builder's default timestamp
    unit dies at the date assertion after 301 pass.

- **⚠⚠ THE `{% query name %}` BLOCK — the body is SQL, and the RESULT IS A RESULT SET. BUILT 2026-09-03
  (user-asked, and the requirement stated sharply: "where result is the result set and not some rendered
  as a single varchar, i.e. like a function call result"). C#-only IN THE PLUGIN: NO ABI, NO C++, NO
  bridge. Gate `verify_plugin_fluid` 275 → **285**, hermetic floor 8534 → **8544**, ONE mutant aimed at
  exactly that requirement. Full record: [docs/fluid-templating.md](fluid-templating.md) §15.**
  - **⚠ THE SKETCH'S SPELLING IS NOT EXPRESSIBLE, and what ships is the nearest Liquid idiom.** The request
    wrote `{% assign result = query %}…{% endquery %}`; Liquid's `assign` parses `identifier = EXPRESSION`
    and terminates at `%}`, so a block body can never be its operand. `{% query result %}` is an IDENTIFIER
    block — **`{% capture %}`'s own shape**, i.e. Liquid's established precedent for "run this block and
    bind the result to a name". `RegisterIdentifierBlock` exists at our pinned 3.0.0-beta.7.
  - **IT IS THE SAME VALUE THE FUNCTION RETURNS, BY CONSTRUCTION** — `RunCaptured` calls the same
    `FluidHostQuery.Run` as `query()` and `| query:`, so all three yield one `ArrayValue` of indexable rows
    and cannot drift on the classifier, the row cap, the value model or the pinned connection.
  - **⚠⚠ THE ASSERTIONS THAT SEPARATE A RESULT SET FROM A STRING, and only some of them do:**
    `r[0].a`/`r[0].b`/`r.size` address BY COLUMN NAME; `r[0].a | plus: 1` → **2** proves it is a NUMBER;
    `{% if r[0].a > 0 %}` → **yes** proves it COMPARES as one; `{% for %}` over 3 rows sums to 6.
    ⚠ **The comparison is the load-bearing one and §7 says why: a broken value model RENDERS CORRECTLY
    WHILE COMPUTING WRONG**, so a render-only assertion cannot tell the two apart. Mutant F (bind the
    captured TEXT instead of the rows) dies at the FIRST §14 assertion after 275 pass — the user's
    requirement expressed as a test.
  - **⚠⚠ AND BOTH BLOCKS NOW TAKE OPTIONAL NAMED ARGUMENTS, BOUND AS PARAMETERS (user-asked the same
    day: "could we eventually allow optional named args … which could be used for parameter binding?").**
    `{% query t region: 'eu', min: 10 %}` binds `$region`/`$min`; `{% exec x: 7, y: 8 %}` likewise. Gate
    285 → **296**, floor 8544 → **8555**, one mutant. Full record: docs/fluid-templating.md §16.
    - **THE MECHANISM IS THE ARTICLE'S** (deanebarker.net/tech/fluid/parser-tags-blocks, which the user
      supplied): `Identifier` and `ArgumentsList` are **`protected readonly`** on `FluidParser`, so a
      SUBCLASS is the only way to compose them into a block header. ⚠ TWO of its details are STALE against
      our pin and were checked with `git show v3.0.0-beta.7:` rather than against the local clone (which is
      at `main`, AHEAD of us): the registration is `RegisterParserBlock`, NOT `RegisterTagBlock`, and the
      list is `IReadOnlyList<FilterArgument>`, not `List`.
    - **⚠⚠ FLUID'S OWN GRAMMAR, WHICH DECIDES THE COMMA — and the request's exact syntax does NOT parse.**
      `ArgumentsList` is `Separated(Comma, …)`, so args are comma-separated and there must be at least one
      (`ZeroOrOne` is what makes the list optional; without it every bare `{% query t %}` stops parsing).
      MEASURED: `{% query t arg1: 1 arg2: 2 %}` gives *"Invalid query tag at (1:9)"*. A separator-free
      grammar IS buildable (`LogicalExpression` is also protected) and was DELIBERATELY not built — it
      would be a grammar only this plugin speaks, where `a: 1, b: 2` is what every other named-argument
      site in Liquid uses. Pinned as a CHARACTERIZATION test, since it is the form people write first.
    - **ONE CONVERSION TABLE, THREE SPELLINGS**: a tag's args arrive UNEVALUATED (name + expression) where
      a filter's arrive evaluated, so `BuildBlockParametersAsync` evaluates each and hands it to the SAME
      `ToParameter` — the int64/decimal ladder, the UTC date stamp and the LIST/STRUCT/MAP refusal cannot
      drift. ⚠ POSITIONAL args REFUSED, duplicates refused locally as well as by the host.
    - **⚠ THE LOAD-BEARING ASSERTION IS THE INJECTION PAIR**: `region: "eu' OR 1=1 --"` answers **0**
      where splicing answers 3, with `"eu"` → **2** beside it — without the control the 0 is equally true
      of a build where the parameter never arrived. Mutant G (ignore the args) dies at §15's first
      assertion after 285 pass.
    - **⚠⚠ IT MADE A DOCUMENTED LIMITATION FALSE, which is the thing to watch.** §15.3 read *"No
      parameters — an identifier block has nowhere to put named arguments"*: true of an IDENTIFIER block,
      false of what Fluid can express, i.e. a limitation of the CHOICE rather than of the library — the
      kind of sentence that hardens into a fact if nobody re-reads it. Corrected in the doc, the README,
      the suite's own comment and here.
  - **THE CAPTURE IS NOW ONE HELPER** (`FluidEngine.CaptureBodyAsync`) shared by both blocks — not tidiness:
    it is where §13.4's flush-before-read subtlety and the partial-body rule live, and a second copy is
    where they come back. ⚠ All THREE spellings of `query` coexist (block, function, filter), as for `exec`.

- **⚠⚠ THE `{% exec %}` BLOCK + THE `fabricator_render` → `fluid_render` RENAME — BOTH 2026-09-03,
  user-asked. C#-only IN THE PLUGIN: NO ABI change, NO C++ change, NO bridge change. The rename is
  BREAKING with NO ALIAS. Gate `verify_plugin_fluid` 256 → **275**, hermetic floor 8515 → **8534**, two
  mutants. Full record: [docs/fluid-templating.md](fluid-templating.md) §13 (block) + §14 (rename).**
  - **THE BLOCK renders its body to a SEPARATE output, runs the captured text as SQL, and emits NOTHING** —
    the shape the user specified. It is what makes a REAL statement writable from a template: multi-line,
    with `{% for %}`/`{% if %}` inside it, and **no quote-escaping**, where `exec("…")` needs the whole
    statement as one escaped string argument. It is also naturally CONDITIONAL — an unreached `{% exec %}`
    runs nothing, because the tag is a statement in the tree rather than an argument that had to be
    evaluated to build a call.
  - **⚠ EVERYTHING DOWNSTREAM OF THE CAPTURE IS SHARED WITH THE FUNCTION FORM** (`ExecuteCaptured` → the
    same empty-body guard, the same classifier, the same per-render pinned connection), so the two
    spellings cannot drift on what counts as a write. A `{% exec %}` staging a TEMP table is readable by a
    later `query()` in the same template — gated, and that assertion needs BOTH features at once.
    ⚠ The count is DISCARDED (a block renders nothing); the function form is still how you get the number.
  - **⚠⚠ THREE FACTS READ OUT OF THE PINNED FLUID, not the clone.** The signature is `IFluidOutput`, not
    `TextWriter` (the user's sketch was the 2.x shape); `BufferFluidOutput` is `internal` while
    `TextWriterFluidOutput` is public; and `Render(template, context)` passes `NullEncoder.Default`.
    ⚠ **The local clone at `D:\repos\fluid` is at `main`, AHEAD of our `3.0.0-beta.7` pin**, so every
    signature was read via `git show v3.0.0-beta.7:…` — the same "CI gates a different Fluid than the
    developer runs" hazard this file already records about referencing a local clone.
  - **⚠⚠ A MUTANT SURVIVED AND THE CODE CHANGED, NOT THE COMMENT — the most useful result here.** The first
    version read the captured text AFTER the `await using`, and a mutant dropping `FlushAsync()` SURVIVED
    because `TextWriterFluidOutput.DisposeAsync` flushes; the comment calling the flush "MANDATORY" was
    simply wrong. Fixed by RESTRUCTURING — the text is read INSIDE the scope right after the flush, which is
    what Fluid's own `{% capture %}` source generator does — so the dependency is explicit and local rather
    than resting on disposal order. Re-run, the same mutant dies at the first block assertion after 257
    pass. ⇒ **when a mutant survives, the honest fix is sometimes to make the step NECESSARY rather than to
    delete it.**
  - **⚠ A PARTIALLY RENDERED BODY IS NOT EXECUTED**: a `{% break %}` inside the block (of an enclosing
    `{% for %}`) leaves half a statement, and half a statement is a different statement — the completion is
    propagated instead. MEASURED zero rows; mutant E (run it anyway) dies at exactly that assertion.
  - **⚠ INTERPOLATION INSIDE THE BLOCK IS RAW**, same rule and same reason as `fluid_replacement_query` (a template must
    be able to emit object names and whole fragments). `{{ v | sql }}` for a value. Gated: `O'Brien` spliced
    raw gives *"unterminated quoted string"* — the safe direction, and not a substitute.
  - ⚠ The parser is built by a METHOD now, so the tag is registered BEFORE anything can be parsed: templates
    are cached by TEXT, so one parsed before registration would be cached with `{% exec %}` unrecognised for
    the process's life. ⚠ All THREE spellings of `exec` coexist (block, function, filter) because tags and
    expressions are different grammars in Fluid — pinned, since a change would silently break one.
  - **THE RENAME: `fabricator_render` → `fluid_render`, no alias.** The function is contributed by the Fluid
    provider and its sibling was already `fluid_replacement_query`; `fabricator_*` is the core/host namespace. **⚠ The
    code change is ONE LINE** (`FluidRenderFunction.Name`) — everything else in the plugin was doc comments;
    the bulk was 133 occurrences in the suite and 20 in the README.
    - **⚠⚠ IT SILENTLY CHANGED AN `ORDER BY`, which is the one thing a mechanical rename can break.** The
      registration check does `… IN ('fluid_render','fluid_replacement_query') GROUP BY 1 ORDER BY 1`, and
      `fabricator_render` sorted BEFORE `fluid_replacement_query` while `fluid_render` sorts AFTER it — so the expected
      rows had to swap. Caught by RUNNING the suite; a rename that only compiles is not a rename that passes.
    - ⚠ **Older dated records deliberately KEEP the old spelling** (`docs/abi-history.md` §v80/§v82,
      `docs/feature-history.md`, `docs/plugin-system.md` §The FLUID plugin, and the floor-bump comments in
      `run-suites.sh`) — the convention every previous rename here followed. Every `fabricator_render` in a
      dated record is this function under its former name; said once in fluid-templating.md §14.1 rather
      than annotated at each site.
  - ⚠ Twice in this pass a leftover `duckdb.exe` from my own `-batch -c` probe held the payload DLLs: once
    it failed the C++ LINK (`LNK1104: cannot open file 'duckdb.exe'`) and once it failed
    `publish-managed.ps1` while the suite then measured the STALE payload and "passed". **Check
    `Get-Process duckdb` before a build or a publish**, and never read a suite result from a run whose
    publish you did not verify.

- **FLUID TEMPLATING — SLICES 1, 2, 3 AND 4 DONE (`fluid_replacement_query` + the shared value model; the bind-time
  probe; `HostQueryTransport` + the Fluid `query`; `{% include %}` from any storage — 2026-09-01/02);
  SLICE 5 PLANNED, and §10.8 says RE-DERIVE it rather than inherit it.
  Full plan, the user's own code sketches, and the as-built record with every measurement:
  [docs/fluid-templating.md](fluid-templating.md) §7.** C#-only, NO ABI change, NO C++ change — because
  `IBackend.GlobalSqlTableFunctions` already existed, which is §2's finding paying out immediately. Gate
  `verify_plugin_fluid` **23 → 89**, seven mutants each killed at its own assertion.
  `SELECT * FROM fluid_replacement_query('SELECT {{ n }} AS n', params := {'n': 7})` — `template` positional, **`params`
  NAMED and optional** (the `fabricator_sql_seq(2, cols := 3)` precedent), taking the same STRUCT / MAP / JSON
  bag `fluid_render` does.
  - **⚠⚠ THE FINDING THE SLICE TURNS ON, and it inverts the obvious reading of the first probe: Fluid
    3.0.0-beta.7 understands `System.Text.Json`'s `JsonNode` NATIVELY, and that support RENDERS CORRECTLY
    WHILE COMPUTING WRONG.** Bound with no `ValueConverter`, MEASURED: `{{ d.i }}` → `3`, `{{ d.big }}` exact,
    `{{ d.o.a.b }}` / `d.arr[1]` / `d.arr.size` / `{% for %}` all right — and `{% if d.i > 1 %}` with `i = 3`
    takes the **ELSE** branch, `{% if d.s == 'x' %}` with `s = "x"` is **FALSE**, `{{ d.money | plus: 1 }}`
    with 19.99 renders **`1`**, and summing an array in a loop gives **`0`**. The leaves arrive as opaque
    nodes: they format faithfully and compare as nothing. ⇒ **a render-only suite passes 100% against that**,
    so `verify_plugin_fluid` now asserts COMPARISON and ARITHMETIC on both the JSON and the Arrow path.
    For `fluid_replacement_query` a wrong `{% if %}` branch is a wrong SQL STATEMENT.
    - ⚠ The control that makes it a measurement: `d.Root`/`d.Parent`/`d.Options` do NOT resolve, so it is
      real `JsonNode` support and not reflection over its CLR members.
    - **⚠ THE TRANSFERABLE RULE: a probe that only RENDERS values cannot distinguish a working value model
      from a broken one.** My first read of that probe was "the converter may be unnecessary" — the exact
      opposite of the truth, and it took one line of arithmetic to overturn.
  - **⚠⚠ A SHIPPED BUG IT FOUND, and it is THIS REPO'S OWN DOCUMENTED TRAP one method away from where the
    trap is already written down.** `JsonToClr` read `e.TryGetInt64(out var l) ? l : e.GetDouble()`, whose
    branches C# unifies to **double** (long→double is implicit, not the reverse), so **the int64 branch had
    never had any effect** and every JSON integer went through a double. MEASURED:
    `fluid_render('{{ n }}', '{"n":9007199254740993}')` returned **9007199254740990** while the same
    value as a DuckDB BIGINT returned it exactly — two losses compounding, the silent widening plus Fluid's
    `Convert.ToDecimal(double)`, which keeps 15 significant digits. **`ReadTimestamp`, in the same file,
    carries a comment explaining exactly this hazard** ("the explicit `(object)` casts are load-bearing").
    Invisible for every integer under 2^53, i.e. every integer a test happens to use.
  - **THE NUMBER LADDER IS `int64` → `decimal` → REFUSE, and the user's `GetDecimal()` sketch is the middle
    rung rather than the whole answer.** int64 first keeps big integers off the double path; decimal second
    is what keeps `19.99` at `19.99`; double last only reaches the refusal. **⚠ Fluid's number model IS
    decimal** (`Convert.ToDecimal` is literally what it calls), so a magnitude outside it cannot be
    represented at all: `1e100` used to raise Fluid's own *"Value was either too large or too small for a
    Decimal"* naming neither parameter nor value, and **`1e-30` rendered as `0`** — a silently wrong number
    spliced into a SQL statement. Both are refused now, naming the value and its JSON path.
    - **⚠ `TryGetDecimal` SUCCEEDS for `1e-30` and returns ZERO — decimal's RANGE is not its RESOLUTION.**
      Hence an explicit underflow check (mutant M4), with a real `0` asserted beside it as the positive
      control, without which the check would pass equally on a build refusing every zero.
    - **⚠ A CONSEQUENCE PINNED AS A DECISION: `3.0` now renders `3.0` where the double path rendered `3`.**
      Better for a function emitting SQL (the literal keeps its type), but it IS a change to
      `fluid_render`'s output.
    - ⚠ NOT fixed and not fixable here: a genuine CLR `double` has ~17 significant digits and Fluid keeps 15.
  - **THE ARROW HALF IS THE ROW WRAPPER SLICE 3 NEEDS, built once as the plan required.**
    `FluidValueModel.ReadCell` is a deliberate SUPERSET of `ArrowValueReader.ReadScalar` (Bridge-only, so
    unreachable from a plugin) with the nested cases the bridge's reader has no counterpart for — it exists
    for FILTER values, which are scalars by construction. `ArrowStruct : IFluidIndexable`, `ArrowMap`, lists.
    - **⚠ ORDINAL ACCESS IS FREE AND IS NOT A SECOND MECHANISM — MEASURED: Fluid resolves `r[0]` by asking
      `TryGetValue` for the KEY `"0"`**, so an int-parse fallback IS index access. A member genuinely named
      `0` wins, which is the right precedence.
    - **⚠ `TryGetValue` must return FALSE for an unknown member, never a nil value** — false is what lets
      Fluid answer `.size` itself, and a real member then shadows it, which is again the right way round.
    - **⚠⚠ `MapArray` DERIVES FROM `ListArray` in Apache.Arrow**, so a `case ListArray` arm matches a MAP
      first. The compiler caught it here (CS8120) — but the other order is not an error, it is a silently
      WRONG SHAPE: a MAP arrives as a list of key/value structs, which renders and iterates happily while
      every lookup by key fails. Mutant M3.
    - ⚠ `DictionaryValue` exposes neither `Keys` nor `TryGetValue` publicly, so spreading a bag's members
      means holding the `IFluidIndexable`, not unwrapping the `FluidValue`.
  - **`{{ x }}` INTERPOLATES RAW, DELIBERATELY** — a template must be able to emit object names, predicates
    and whole fragments, which is the only reason to generate SQL from a template. Data goes through two
    allow-list filters following the `fabricator_va_values` precedent: `{{ v | sql }}` → `DuckSql.Literal`,
    `{{ n | sql_ident }}` → `DuckSql.QuoteIdent`. ⚠ `DuckSql` is in `Fabricator.Abstractions`, so the plugin
    reaches it with the reference it already has — the same property §2 is about.
  - **THE sqlgen PROPERTY IS GATED, and no row assertion can see it**: MEASURED via `EXPLAIN`, a
    `fluid_replacement_query` over a table plans as a bare `SEQ_SCAN` with `Projections: id` and `Filters: g=3` — the
    call is GONE and both pushdowns reached the base table. ⚠ `EXPLAIN` cannot be a subquery source, so the
    gate uses the `<REGEX>:` form on the `physical_plan` row.
  - **⚠⚠ TEMPORALS: ONE WRONG VALUE FIXED, ONE WRONG COMPARISON SURFACED — found by probing edge cases
    after the slice was otherwise finished, which is the only reason they were found at all (nothing in the
    plan mentions dates). Full record: docs/fluid-templating.md §7.4a.** A **DATE RENDERED THE PREVIOUS
    DAY** (`2026-09-01` → `2026-08-31 22:00:00Z` on a UTC+2 box): `Date32Array.GetDateTime` returns
    `Kind = Unspecified`, which Fluid resolves against the machine's LOCAL zone. Pre-existing, and made
    worse by this slice, since `fluid_replacement_query` would splice the wrong date into a statement. Fixed by stamping
    `DateTimeKind.Utc`. **⚠ BOTH OBVIOUS FIXES WERE MEASURED AND BOTH ARE WRONG:**
    `TemplateOptions.TimeZone = Utc` changes NOTHING (the conversion happens where the DateTime becomes a
    DateTimeOffset), and returning `DateOnly` is WORSE (Fluid has no support for it — a culture-dependent
    `09/01/2026` string). A BLOB rendered as `9798`, the concatenated decimal bytes; it is lowercase hex now.
    - **⚠⚠ FLUID DOES NOT ORDER TEMPORALS AT ALL and this is NOT fixed — gated and documented instead.**
      MEASURED with controls: `>` and `<` are BOTH false for two different dates while `==`/`!=` behave, and
      numbers/strings compare fine with the same operators. So `{% if d > cutoff %}` silently takes the ELSE
      branch. Workaround gated: format to ISO, compare strings. **⚠ An ISO-STRING value model WOULD fix it
      and was deliberately NOT taken** — measured to fix comparison AND improve rendering with `| date:`
      still working, but a date would stop BEING a date, which is a user-visible semantic change to put to
      the user rather than smuggle into this slice. The measurement is done; the choice is one edit away.
      - **⚠ I BRIEFLY CALLED THE TIMEZONE TRAP "the strongest argument" FOR SWITCHING AND THAT WAS AN
        OVERSTATEMENT**, corrected once the user stated the UTC-session convention: at `TimeZone = 'UTC'`
        the trap does not fire at all, so it supports the case only for a deviating session. **What remains
        decisive is the ORDERING gap**, which no timezone setting touches.
      - **⚠⚠ AND THERE IS NOW A REASON TO WAIT: FLUID'S MAIN BRANCH CARRIES TIMEZONE WORK THAT
        3.0.0-beta.7 DOES NOT** (user-reported 2026-09-01; not verified here). If it lands and touches
        ordering or the date model, switching to ISO strings now could be work undone, or a divergence from
        an upstream fix. **Take the next Fluid bump FIRST, re-run `verify_plugin_fluid`'s temporal
        assertions, and re-derive from what they then say** — they pin today's behaviour exactly, so a
        change arrives as a failed assertion naming the value rather than as a silently different rendering.
        ⚠ If they move, re-derive §7.4a rather than editing the expected values to match.
      - ⚠ Re-derive before slice 3 either way: it puts whole query ROWS through the same value model, which
        multiplies whichever choice is made.
    - **⚠⚠ `| sql` collapses every temporal to TIMESTAMPTZ, AND GETTING A DATE BACK OUT IS A SILENT
      TIMEZONE TRAP.** Anything that reads a TIMESTAMPTZ without NAMING a timezone reads it in the SESSION's
      timezone — `::DATE`, `::TIMESTAMP::DATE`, `date_trunc`, `strftime`, `extract`/`date_part` — so in a
      session west of UTC every one yields the PREVIOUS DAY with no error (MEASURED under
      `America/New_York`; `Australia/Sydney` agrees with UTC, which is why one timezone is not a test).
      That is DuckDB behaving correctly; our TIMESTAMPTZ representation is what makes it a trap. TWO SAFE
      ROUTES, both gated: name the zone (`(… AT TIME ZONE 'UTC')::DATE`, which also serves a genuine
      TIMESTAMP and preserves the instant) or never build a TIMESTAMPTZ
      (`{{ d | date: "%Y-%m-%d" | sql }}::DATE`, which additionally needs no ICU).
      - **⚠⚠ THE STANDING CONVENTION, user-stated 2026-09-01 and now in the README's Quick Start:
        A CLIENT SHOULD ALWAYS `SET TimeZone = 'UTC'` IN A DuckDB SESSION**, because that is what the Delta
        protocol stores and accepts. ⚠ It is NOT DuckDB's default — with ICU loaded the default is the
        SYSTEM zone (MEASURED `Europe/Berlin` on this box). Under UTC every route above agrees (measured:
        `{{ d | sql }}::DATE` answers 2026-09-01), so the trap belongs to a session that DEVIATES from the
        recommended configuration. It is gated anyway — "the normal path is safe" is exactly the reasoning
        under which a trap survives unrecorded. ⚠ The convention is BROADER than Fluid:
        `fabricator_host_query` inherits the session zone, and every TIMESTAMPTZ surface reads it, which is
        why the README states it once in Quick Start rather than per-feature.
      - **⚠⚠ TWO DIFFERENT CLOCKS, AND ONLY ONE IS NEUTRALISED BY THAT CONVENTION.** The DATE-renders-
        previous-day bug is the **.NET** side reading `TimeZoneInfo.Local`, i.e. the OS zone of the machine
        running the extension — setting DuckDB's `TimeZone` does NOT affect it, so the `AsUtc` fix stands
        regardless of deployment. This trap is the **DuckDB session** zone. Same symptom, different clocks,
        different fixes; do not let one be cited as covering the other.
      - **⚠⚠ A CORRECTION WORTH CARRYING, because the wrong version reached FIVE places before the USER
        caught it — no test did.** This was first recorded as *"DuckDB has NO `TIMESTAMPTZ -> DATE` cast"*,
        from a suite failure reading *"Conversion Error: Unimplemented type for cast"*. **The cast exists;
        it needs ICU, and `unittest` does not auto-load extensions** — the suite had no `require icu`, so a
        missing REQUIRE presented as a missing FEATURE. This file already records the opposite direction (a
        `require` for something NOT compiled in SKIPS silently, so the suite passes vacuously); this is the
        same hazard the other way round and it is worse, because it reads as a definite negative result
        about the engine and gets written down as one. ⇒ **before recording "DuckDB cannot do X" from a
        suite failure, check what the suite LOADED.**
      - ⚠ The raw `| sql` output is asserted by TYPE and INSTANT rather than rendered text (a TIMESTAMPTZ's
        display depends on session timezone, so pinning the string would report the runner's locale), and
        the timezone section SETS `America/New_York` — under the runner's default UTC every route agrees
        and the section would pass while saying nothing.
  - **DRIVING THE BAG FROM SQL WORKS BOTH WAYS, and it is the first thing anyone will ask** (a sqlgen
    generator sees constant VALUES, never expressions): **`params := ?`** in a prepared statement re-binds
    per EXECUTE, so ⚠ **the OUTPUT SCHEMA may differ between two EXECUTEs of ONE prepared statement** —
    measured, one column vs three, which is surprising enough to pin since a prepared statement normally has
    a fixed result shape (the same property the lateral bind-time constants recorded for `f(t.n, ?)`); and
    **`getvariable()`** reads the bag from a session variable, the idiom the CDC reader documents for
    carrying a cursor — so a template-driven pipeline needs no client and no spliced literal.
  - **✅ SLICE 4 IS DONE (2026-09-02) — `{% include %}` / `{% render %}` from any storage the host can reach.
    C#-only IN THE PLUGIN: NO ABI change, NO C++ change, NO bridge change. Full record:
    [docs/fluid-templating.md](fluid-templating.md) §10.** Gate `verify_plugin_fluid` **147 → 174**,
    four mutants each killed at its own assertion.
    `SET GLOBAL fluid_template_root = 's3://analytics/templates';` then
    `SELECT * FROM fluid_replacement_query('{% include ''dims/customer'' %}', params := {'region': 'eu'})`.
    - **⚠⚠ THE PLAN SAID "A `HostFs` SEAM". ONE WAS BUILT AND IT KILLED THE PROCESS.** §2's table blames the
      ASSEMBLY (`HostFs` lives in the Bridge), and §4 scheduled slice 4 as "the same seam pattern". A
      `HostFileTransport` was written to exactly the `HostHttpTransport` shape, the bridge filled it in at
      boot, and the first include died: **`0xC0000005` at `HostFs.OpenRead` ← `GetFileInfoAsync` ←
      `ScalarFnExecute`.** Every `fs_*` host callback dereferences the calling operator's `ClientContext`, and
      **a GLOBAL function has no ambient opener** — which both `fluid_render` (global scalar) and
      `fluid_replacement_query` (global sqlgen) are. **The blocker was never the assembly; it is the AMBIENT**, which §2
      could not see because it reasoned about references rather than about call context. Its own corollary
      already said it (*"usable only … where the ambient still flows from one"*) and neither of us read it
      that way. **The seam was DELETED rather than shipped unreachable.**
    - **⚠⚠ THE SAME MISSING AMBIENT MAKES A PLAIN `SET` UNRELIABLE, AND IT IS NON-DETERMINISTIC.** The root is
      the setting `fluid_template_root` — **the first setting any PLUGIN declares** (a plugin's
      `IBackend.Settings` ride the same `BackendRegistry.All()` path a backend's do; measured present in
      `duckdb_settings()`). Provider settings register SESSION-scoped (v69) and `ProviderSettingsStore`
      resolves session-then-global from `CurrentSession`, **the same absent ambient**. The chain, read from
      source: `SET x = v` resolves AUTOMATIC against `FABRICATOR_SETTING_DEFAULT_SCOPE` = SESSION, so the
      trampoline writes under `SessionKeyFor(&context)` (the ClientContext ADDRESS); `GetString` consults
      that layer only when `CurrentSession != 0` and otherwise reads the global bucket, which `SET GLOBAL`
      writes under key 0; and `CurrentSession` is an `AsyncLocal<long>` assigned ONLY by `set_active_opener`,
      which C++ calls from catalog and scan crossings, never from a global scalar's execute. **MEASURED
      reproducibly — a two-statement suite, the shell, and the full suite in two shapes: a plain `SET` is
      INVISIBLE, while `current_setting()` reports it throughout**, which is what makes it a trap rather
      than an error. ⇒ the refusal names **`SET GLOBAL`** explicitly.
      - **⚠⚠ A CORRECTION, AND THE WRONG VERSION REACHED FOUR PLACES BEFORE IT WAS CHECKED (user-caught,
        2026-09-02, by asking me to explain the mechanism).** This entry first claimed the behaviour was
        NON-DETERMINISTIC — invisible in a small session, visible after unrelated statements — and explained
        it by `SetActiveOpener` assigning the ambients and never clearing them, so an earlier crossing on the
        same thread would leave `CurrentSession` set. **That story was invented to fit ONE observation and it
        does not survive testing.** The observation was real (an intermediate build's includes rendered after
        a plain `SET`) and it DOES NOT REPRODUCE: the direct test — a `fabricator_plugins()` call immediately
        before the `SET`, i.e. a fabricator table function whose bind AND scan really do call
        `set_active_opener` (`arrow_ingest.cpp:265`, `:1005`) — came back NEGATIVE, and so did reconstructing
        the original suite shape on a clean bridge. The one run is UNEXPLAINED and nothing rests on it.
        ⚠ The trap it illustrates is this file's own recurring one: **an anomaly seen once, explained by a
        plausible mechanism, and written down as measured.** The tell was available immediately — I had TWO
        negative shell probes in hand when I wrote it and read them as "weak tests" rather than as evidence.
      - **⚠ THE UNDERLYING GAP IS NOT FIXED AND IS BIGGER THAN FLUID: a global function reaches neither the
        host filesystem nor its own session's settings.** Fixing it means establishing the ambients around
        `scalarfn_execute` and the sqlgen bind **with SAVE/RESTORE** — and the v80 record is the warning, not
        the recipe: pushing them at a scalar BIND crashed under `OPTIMIZE` because a scalar binds inside
        whatever an outer operation is running, so it CLOBBERED the outer ambient. C++ cannot read the managed
        `AsyncLocal` back today, so a correct version needs a paired push/pop. Its own change.
    - **✅ AND A PRE-EXISTING CRASH IT EXPOSED, FIXED IN ITS OWN COMMIT (C++-only, no ABI): none of the nine
      `fs_*` host callbacks null-checked the opener, while their sibling `HostHttpRequest` always has**
      (*"http_request requires a client context (no ambient opener)"*). So any managed caller reaching the
      filesystem without an ambient got an access violation instead of a message. ⚠ UNGATED and the code says
      so — nothing in tree currently calls an `fs_*` callback without an ambient, so no statement reaches it.
    - **WHAT SHIPS: `read_blob` over slice 3's `HostQueryTransport`** — `SELECT content, size, last_modified
      FROM read_blob($path)`. It needs no ambient (`Host.Query` opens its own connection) **and it is better
      on the merits, all four MEASURED**: `read_blob` on a missing file returns **ZERO ROWS** rather than
      throwing, so **absence is ESTABLISHED by the engine** instead of guessed from a message — decisive here,
      because the host has no `fs_exists` and Fluid's normal behaviour is to probe a path that is *supposed*
      to be missing; it reports **`size`**, so the 1 MiB ceiling is checked against the file; it reports
      **`last_modified`**, so `TemplateSourceInfo.LastModified` carries a REAL time where a filesystem seam
      has none (this repo shipped the alternative once — `DuckDbTableFileSystem`'s hardcoded epoch); and the
      path crosses as a **BOUND PARAMETER** (`read_blob($path)` binds), so it never becomes SQL text — slice
      3's named-parameter work paying out immediately. ⚠ The cost, stated: the read inherits every `query()`
      limitation, so a location authorised by a TEMPORARY secret of the calling session is unreadable.
    - **⚠⚠ FLUID'S FILE-PROVIDER CONTRACT, measured on 3.0.0-beta.7 — it is `Fluid.ITemplateFileProvider`,
      NOT `Microsoft.Extensions.FileProviders.IFileProvider`** (that is what the DEFAULT
      `FileProviderTemplateFileProvider` wraps). **It receives the `TemplateContext`**, which is what makes
      ONE instance on the shared static `TemplateOptions` SAFE where the `query` FILTER needed a warning — the
      root, the cache and the tried-path record travel per call. Called at **RENDER, never at PARSE** (zero
      calls during `TryParse`), so the parse-once cache is unaffected. **⚠⚠ IT PROBES TWICE PER INCLUDE**
      (`a`, then `a.liquid`) — two round trips on remote storage for an author who omits the extension — **and
      the BARE probe WINS when both files exist**, the opposite of what a `.liquid` convention suggests
      (gated as a discriminating pair). Not-found is `null`. Two includes of one file made FOUR provider calls,
      so caching is OURS. `{% render %}` uses the same provider and, unlike standard Liquid, is **not**
      scope-isolated in this beta. A cyclic include stops at `MaxRecursion` (100) after ~200 calls.
    - **⚠⚠ THE ROOT IS ERGONOMICS, NOT A SANDBOX, and the suite says so rather than letting the refusals imply
      otherwise.** An ABSOLUTE path is allowed and needs no root. Confining an include would protect nothing:
      the template's renderer can already run SQL, and slice 3's `query()` reads any path the host can open.
      What is refused is refused for PREDICTABILITY — `..` (resolves against a root the author may not see),
      and `* ? [ ]` (**`read_blob` GLOBS**: `he*` matches one file today and another the day one is added).
    - **⚠⚠ TWO MUTANTS SURVIVED FIRST AND BOTH TAUGHT SOMETHING.** (1) The BOM-stripping branch was a REAL
      survivor — `TemplateSourceInfo` takes a STREAM factory and Fluid reads it with a `StreamReader`, which
      strips a UTF-8 BOM itself, so our branch was inert; **DELETED**, and the provider now streams the raw
      bytes (one fewer decode/re-encode round trip). ⚠ Fluid does NOT strip a BOM from a template passed as a
      STRING (measured), so `fluid_render` on a BOM-prefixed literal keeps it. (2) The absolute-path
      mutant survived **three times for three different reasons**, and only the last is about the code: the
      anchor had a `\\` and **never applied** (the build succeeded and the suite passed IDENTICALLY — exactly
      what a no-op mutation looks like; a control mutation that makes `Resolve` always throw is what proved
      the harness sound); then `if (false && A || B || C)` **still fires on B and C** — precedence, not code;
      and then it survived legitimately because **on Windows the join `<root>/C:/Users/…/hello.liquid`
      OPENS** (measured), so the assertion had to move to where NO ROOT EXISTS. ⇒ **an assertion that depends
      on a path NOT resolving is platform-dependent; assert the refusal instead.**
    - ⚠ Two gate mechanics worth reusing: the first `COPY` uses **`PER_THREAD_OUTPUT` because that is what
      CREATES the directory** (a plain COPY to a file path does not create its parent, and the runner's
      scratch dir need not exist from DuckDB's point of view), and it is **`rtrim(x, chr(10))`, not
      `trim(x)`** — DuckDB's one-argument `trim` removes SPACES and leaves the newline COPY appends.
    - ⚠ NOT GATED, and the suite says so: the per-render read cache (no answer changes; no read count is
      observable from SQL) and the reader's multi-match refusal (a mutant survives — `Resolve` refuses glob
      metacharacters first, so only a ROOT containing one could reach it).
    - ⚠ A per-call `template_root` argument was considered and NOT built: clean for `fluid_replacement_query` (a named
      table-function parameter), awkward for `fluid_render` (a scalar, so a third parameter means a
      second arity). Revisit if the process-wide scope becomes a real complaint.
  - **✅ SLICE 3 IS DONE (2026-09-01) — `HostQueryTransport` + the Fluid `query(sql)` function. C#-only,
    NO ABI change, NO C++ change. Full record: [docs/fluid-templating.md](fluid-templating.md) §9.**
    Gate `verify_plugin_fluid` **93 → 131**, four mutants each killed at its own assertion; service floor
    3162 → **3200** (3162 + 38 exactly). The seam is `HostHttpTransport`'s shape copied deliberately — one
    static delegate in `Fabricator.Abstractions`, filled in by `Bootstrap` at boot — so a plugin reaches
    `host_query` with the reference it already has, and §2's prediction pays out a second time.
    - **⚠⚠ THE SELECT-ONLY REFUSAL §8.3 REQUIRED IS NOT THE EASY PART, AND THE TWO MECHANISMS ANYONE WOULD
      REACH FOR FIRST ARE BOTH BROKEN — MEASURED.** A PREFIX CHECK (`starts with SELECT`/`WITH`) admits
      `WITH x AS (SELECT 1) INSERT INTO t SELECT * FROM x`, a write beginning with `WITH`. Wrapping as
      `SELECT * FROM (<sql>)` is worse than useless: it refuses every HONEST non-SELECT
      (INSERT/DELETE/UPDATE/CREATE/DROP/ATTACH/COPY/PRAGMA/SET, all Parser Errors with the row count
      unchanged) and is defeated by the ADVERSARIAL one —
      **`SELECT 1) ; INSERT INTO aud VALUES (99); SELECT * FROM (SELECT 2` performed the insert, and a
      `DROP TABLE` variant DROPPED THE TABLE.** It is string concatenation, so it has an escape by
      construction. **A mechanism that refuses the accident and admits the attack is worse than none,
      because it reads as a defence.**
    - **WHAT SHIPS IS `json_serialize_sql` — DuckDB's OWN PARSER — WITH THE SQL AS A BOUND PARAMETER**, and
      it announces the rule itself: *"Only SELECT statements can be serialized to json!"*. MEASURED: it
      refuses both escapes, refuses multi-statement input, refuses the `WITH … INSERT` shape, and **PARSES
      ONLY** — a target table's row count is unchanged by classifying a `DELETE` against it.
      - ⚠ **The cast is REQUIRED**: `json_serialize_sql(?)` cannot resolve its overload from an untyped
        parameter (*"first argument must be a VARCHAR"*). It is `?::VARCHAR`. Found by RUNNING it — and it
        arrived as a REFUSAL rather than a crash, i.e. the fail-closed rule working before it was
        deliberately tested.
      - ⚠ **An EMPTY string classifies as NO ERROR** (it parses to zero statements), so the classifier
        ALONE would wave it through. Guarded separately, before the classifier.
      - ⚠ **The engine's own message is surfaced verbatim**, because the check conflates two causes
        otherwise: a non-SELECT and a real syntax error (`syntax error at or near "SELEC"`). Reporting "not
        a SELECT" for a typo sends the author to the wrong place.
      - ⚠ **It FAILS CLOSED** — `json` unavailable or host unreachable ⇒ REFUSED, never run. An
        unenforceable check must fail closed.
      - **⚠ THE COST, stated rather than hidden: some READ-ONLY statements are refused too.** `PIVOT` and
        `EXPLAIN` are not serializable — and for `PIVOT` that holds even wrapped in a subquery, where it
        would otherwise EXECUTE, so it is unreachable in any spelling. `DESCRIBE`/`SUMMARIZE`/`VALUES`/
        `TABLE t`/`FROM t`/CTEs/set ops all pass. Conservative in this direction is the correct trade.
    - **THE PAYOFF, and it is what slice 2 was run to permit: a template asks the database what SQL to
      generate, AT BIND TIME** — measured, a `fluid_replacement_query` whose output SCHEMA is decided by rows read
      during `bind_replace` (columns named `alpha`, `beta`, names existing only in the queried table).
    - **⚠⚠ THE GATE IS A PAIR AND NEITHER HALF ALONE SAYS ANYTHING.** The refusal is asserted at §8.3's
      sharpest point — `EXPLAIN` of a statement that never executes — and *"the write did not happen"* is
      **equally true of a build where bind-time `query()` had stopped working altogether**. So a POSITIVE
      CONTROL sits immediately above it: an `EXPLAIN` whose plan carries column names that could only come
      from a bind-time read. ⚠ It uses the `<REGEX>:` form on the `physical_plan` row because **`EXPLAIN`
      cannot be a subquery source** — the convention this very suite already used a few sections up, and
      which I re-derived the hard way.
    - **ROWS REUSE SLICE 1's VALUE MODEL, as §6 required — ONE type, ONE lookup rule.** `ArrowStruct` gained
      a `RecordBatch` constructor rather than growing a sibling, so a result row and a STRUCT cell resolve
      members identically (name, then an int-parse ORDINAL, then FALSE so Fluid can answer `.size`).
      - **⚠⚠ A MUTANT CORRECTED MY OWN CODE COMMENT, which is the most useful thing in the slice.** Cells
        are read EAGERLY because the batches are disposed as the result is consumed; I wrote that holding
        one would be "a use-after-free … invisible on the platform you develop on". **It is NOT that
        class** — Apache.Arrow nulls a disposed `RecordBatch`'s arrays, so it fails LOUDLY with a
        `NullReferenceException` on the first cell read, deterministically, and the mutant died at the very
        FIRST `query()` assertion. Right line, wrong reason, and only running it showed which.
      - ⚠ A row cap that **ERRORS** at 1,000,000 rather than truncating — a silent truncation is a wrong
        ANSWER, where the cap only turns an out-of-memory into a sentence. No knob yet.
    - **⚠ `AllowFunctions` IS OFF IN FLUID BY DEFAULT AND IS A PARSER-LEVEL GATE**: without
      `new FluidParser(new FluidParserOptions { AllowFunctions = true })`, `query('…')` is a PARSE error
      (*"Functions are not allowed"*) rather than a missing function at render — the failure appears one
      layer away from its cause.
    - **⚠ `require json` IS LOAD-BEARING IN THE SUITE, not hygiene.** The classifier is
      `json_serialize_sql` and `unittest` does NOT auto-load extensions, so without the directive every
      `query()` call is refused (fail-closed) and the section reads as a broken FEATURE rather than a
      missing REQUIRE — this file's own recorded trap, and the directive says so.
    - §8.2's transaction visibility is now GATED with its control: inside `BEGIN; INSERT …;` the statement's
      own scan sees the uncommitted row and `query()` does not; after `COMMIT` it does, which is what makes
      the middle assertion a visibility result rather than a broken read.
    - **⚠⚠ NAMED PARAMETER BINDING — `sql | query: a: 1` binding `$a` — ADDED THE SAME DAY (user-raised:
      "how do we create a params object where the name members are used for param binding by name? or we
      need to add dictionary creation support into fluid"). C# AND C++, still NO ABI change. Gate 131 →
      **147**, three further mutants. THE ANSWER IS THAT FLUID ALREADY HAS NAMED ARGUMENTS — ON FILTERS —
      so NOTHING was added to Fluid. Full record: docs/fluid-templating.md §9.10.** MEASURED on beta.7:
      `query('s', a: 5)` is a PARSE error and `{'a':1}`/`{a:1}` do not parse either (no dict literal), while
      `'s' | q: a: 5` parses AND arrives with its names. ⇒ `FunctionArguments.Names`/`HasNamed` — the
      members that make named FUNCTION arguments look supported — are populated by the FILTER grammar only.
      - **⚠⚠ THE HOST HALF NEEDED NO ABI CHANGE BECAUSE THE NAMES WERE ALREADY CROSSING.** DuckDB has
        `$name` and `PreparedStatement::Execute(case_insensitive_map_t<BoundParameterData>&, bool)`; the
        params `RecordBatch` carries its column names in the Arrow schema; and `ArrowStreamReader` walked
        that schema for TYPES while never capturing `children[i]->name`. So host_query bound positionally
        for the whole life of that overload because **nothing had read the names**, not because they were
        absent — the same shape as the `fabricator_functions()` finding.
      - **⚠⚠ THE STATEMENT SELECTS THE BINDING, NOT THE BATCH, AND MY FIRST RULE WAS WRONG IN A WAY ONLY
        THE TIER SHOWED.** It read "every column has a non-empty name ⇒ bind by name" — but an Arrow field
        practically ALWAYS has a name, so that test is nearly always true and it silently switched EVERY
        existing caller to name-binding. `cf_host_param` sends columns `p0`/`p1` against positional `?` and
        broke (*"Values were not provided for … parameters: 1, 2"*), falsifying my own "keeps the original
        positional behaviour byte-for-byte". ⚠ **I had also concluded there were NO in-tree callers of the
        params overload — from a grep I truncated with `head -10`.** The rule now matches the batch's names
        against the statement's own `named_param_map` (a `?` statement names its parameters "1", "2", … so
        `p0`/`p1` cannot match); a SUBSET is enough, because requiring equal sizes falls back to positional
        and makes DuckDB report every parameter missing including the supplied one. Duplicate names are
        REFUSED, not collapsed.
      - **⚠ THE POSITIONAL BRANCH IS NOW UNREACHABLE FROM ANY IN-TREE CALLER and is therefore UNGATED** —
        every in-tree producer names its columns. Kept for an out-of-tree plugin using `?`; the suite says
        so rather than implying coverage.
      - **⚠⚠ THE CHANGE BROKE THE CLASSIFIER, AND THAT IS THE MECHANISM ANNOUNCING ITSELF.** §9.2's
        classifier read `json_serialize_sql(?::VARCHAR)` while sending a batch whose column is named `sql`,
        so the instant named binding landed it failed with *"Values were not provided for the following
        prepared statement parameters: 1"*. Fixed by naming the placeholder `$sql`. **A params batch's field
        names are now LOAD-BEARING**, which they never were before.
      - Values are an ALLOW-LIST crossing as VALUES rather than text: number → BIGINT when integral (Fluid's
        number model IS decimal, so even `10` arrives as one) else DECIMAL(38, scale) keeping `19.99`
        exact; string/bool/date (stamped UTC, §7.4a's rule on the way out) / nil → NULL VARCHAR; a
        LIST/STRUCT/MAP is REFUSED BY NAME (⚠ **the LIST half stopped being true on 2026-09-04** — see the
        list-parameter entry under "Next up"). ⚠ POSITIONAL filter arguments are REFUSED rather than ignored —
        Fluid permits mixing them, and dropping one silently would run the statement with a parameter the
        author believed they had supplied.
      - **THE LOAD-BEARING GATE ASSERTION IS THE INJECTION ONE, WITH ITS CONTROL**: `region: "eu' OR 1=1
        --"` answers **0** where splicing would answer 3, and the same statement with `"eu"` answers 2 —
        without which the 0 would be equally true of a build where the parameter never arrived.
      - **⚠ MUTANT A (never bind by name) dies at the FIRST query() rather than at a parameter assertion**,
        because the classifier itself now binds by name — strong coverage, broad kill. Hence MUTANT B:
        names in order, VALUES reversed, so a one-parameter call is unaffected and a two-parameter one
        binds wrongly (*"Could not convert string 'eu' to INT32"*). ⚠ My FIRST attempt at B reversed the
        name at the INSERT but not at the duplicate CHECK, so it tripped my own guard instead of
        demonstrating a wrong binding — it killed at the right line for the wrong reason.
    - **⚠⚠ `Host.Query`'s PARAMETERISED overload HAD NO IN-TREE CALLER until slice 3's classifier
      (user-raised) — every other caller uses the bare form or the named-Arrow-`inputs` form.** Read from
      `fabricator_host_query.cpp`: with `params` the host runs `conn->Prepare(sql)` then
      `prepared->Execute(values)` — a REAL prepared statement, **one Arrow COLUMN per parameter, bound
      positionally** against `?` / `$1`, so the value never becomes SQL text. ⚠ Only **row 0** is read, an
      **empty batch binds all-NULL** rather than erroring, an **untyped `?` may fail overload resolution**
      (hence `?::VARCHAR`), and — the one that bites — **passing parameters restricts you to ONE
      statement**, because the no-params branch is `SendQuery` and this one is `Prepare`. That is the same
      asymmetry that forced `fabricator_host_query`'s fallback and motivated `fabricator_host_exec`.
    - **⚠ §1.4 (DuckDB functions callable from inside Fluid) SHOULD BE RE-DERIVED, NOT INHERITED** — §6
      already flagged it and slice 3 strengthens it: a template that can run SQL can already call any DuckDB
      function through `query()`, so the remaining case is ergonomic rather than capability.
    - Slice 4 (`ITemplateFileProvider`) needs the same seam shape for `HostFs`; `HostQueryTransport` is now
      a second worked example and the two should look alike.
  - **✅ SLICE 2 IS DONE (2026-09-01) — THE BIND-TIME `host_query` HAZARD IS MEASURED AND THE ANSWER IS
    PERMISSIVE. Full record: [docs/fluid-templating.md](fluid-templating.md) §8.** A THROWAWAY
    `ISqlTableFunction` calling `Host.Query` from `GenerateSql` (i.e. a real host query while DuckDB binds),
    run under timeouts so a deadlock would surface as one. **FOURTEEN shapes, none failed**: constant, a
    DuckDB table, inside a VIEW (bind repeats), inside a transaction, `EXPLAIN`/`DESCRIBE` (bind without
    execute), NESTED (a bind-time host query calling the probe again), a prepared statement re-bound per
    EXECUTE, our OWN attached catalog, bind-read + outer scan of the same table, and CTAS into that catalog.
    ⚠ The catalog cases are THREE levels deep by construction — `PROVIDER 'delta'` defaults `native_read` on,
    so the Delta scan issues its own `Host.Query` per file. Probe REMOVED afterwards
    (`CustomFunctions.cs` byte-identical to HEAD; `duckdb_functions()` reports 0).
    - **⚠ IT DOES NOT REPEAL THE CLASS.** The two incidents §3 cited (the ABI v80 scalar-bind ambient
      SIGSEGV under OPTIMIZE; the `entry_lock_` rule) are about holding an AMBIENT or a LOCK across a
      re-entry, and `Host.Query` opens its own connection and scope. The probe answered the specific
      question, not the general one.
    - **⚠ TRANSACTION VISIBILITY IS A REAL LIMITATION AND IS *NOT* BIND-SPECIFIC — measured with a control.**
      Inside `BEGIN; INSERT …;` the statement's own scan sees **109** while BOTH `fabricator_host_query` at
      execute time AND the bind probe see **10**; after COMMIT, 109. Identical on a PLAIN DuckDB table ⇒ it
      is `host_query` opening its own connection, not something bind introduces. That is what makes the
      surface symmetric: ONE rule to document, not two. A template cannot observe the writes of the
      transaction running it.
    - **⚠⚠ THE HAZARD IT FOUND, WHICH NEITHER BRANCH OF §3 ANTICIPATED, AND IT CONSTRAINS SLICE 3'S SURFACE:
      A BIND-TIME WRITE FIRES ON `EXPLAIN`.** Counting rows in an audit table: `EXPLAIN` of a
      never-executed statement ⇒ **1**; merely `CREATE VIEW` over it ⇒ **2**; each USE of that view ⇒ **3**.
      ⇒ **slice 3's Fluid `query` must REFUSE anything that is not a SELECT**, decided on the STATEMENT KIND
      BEFORE execution — a catch afterwards is too late, the write has happened. Not a new rule:
      `ISqlTableFunction`'s own contract already requires `GenerateSql` to be deterministic and
      side-effect-free BECAUSE binds repeat and happen without execution.
    - ⚠ Slice 3 still needs §2's `HostQueryTransport` seam regardless — the probe reached `Host.Query` only
      because it lived in a FIRST-PARTY assembly. The plugin still cannot.
  - **⚠ SLICE 2's ORIGINAL FRAMING, now superseded by the result above:** — nothing in slice 1 executes SQL, it only
    generates text. If anything `fluid_replacement_query` sharpens §3: a Fluid `query` inside it would execute SQL
    *inside `bind_replace`*, i.e. during the binder's own walk, not merely "during bind".
