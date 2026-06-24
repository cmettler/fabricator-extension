# Multi-edition support — Synapse, Fabric Warehouse, Lakehouse SQL endpoint

> Design for connecting `mssql_net` to engines beyond box SQL Server / Azure SQL Database:
> Azure **Synapse** dedicated pools, **Fabric Warehouse**, and the **Lakehouse SQL analytics
> endpoint**. These differ from box SQL Server in connection capabilities (no MARS, snapshot
> isolation) **and** in their type systems and collation. The strategy: **detect a server
> capability profile once at ATTACH and adapt** — be collation-*adaptive and always correct*,
> never collation-*prescriptive*. Status: **slices 1–3 implemented + validated end-to-end against a
> real Fabric Warehouse** (edition 11, `Latin1_General_100_BIN2_UTF8`; ATTACH + catalog discovery work,
> the connection succeeds MARS-free) — `ServerProfile` detection
> (`dotnet/ArrowNet.SqlServer/ServerProfile.cs`) + the `mssql_server_info(catalog)` diagnostic + profile-
> driven type mapping (§3) + connection mode (`mssql_mars` tri-state, pooled reads, SNAPSHOT writes — §2,
> [transactions.md](transactions.md) §5.1) + collation-aware string `ORDER BY` pushdown (§4/§6.6) + the
> JSON read-side gate (§3.3) + granular read-side types (§3.4: `time(7)`/`datetime2(7)`→ns,
> `uniqueidentifier`→`UUID`, decimal `(p,s)`) + tz validated naive↔naive under a non-UTC zone (§3.1, ICU
> embedded); box test DB now on SQL Server 2025. Remaining: the **write-side** rich types (DuckDB
> `JSON`/`UUID` → native, blocked on the lossless-boundary decision, §3.4.1). File references are
> repo-root-relative. Connection/transaction behavior is in [transactions.md](transactions.md).
>
> **Re-validated end-to-end on a real Fabric Warehouse (2026-06-24, edition 11, `BIN2_UTF8`, via a service
> principal):** profile flags; CTAS write mapping → `varchar(max)` (no NVARCHAR), `BOOLEAN`→`bit` (the
> lossless-boundary fix), `datetime2(6)`, `TIMESTAMPTZ`→UTC `datetime2(6)` (no DATETIMEOFFSET); read-back →
> `datetime2(6)`→`TIMESTAMP` (µs, correctly NOT ns since Fabric caps scale at 6), tz instant preserved
> (`12:00-04`→`16:00`Z, read naive); and string `ORDER BY`+`LIMIT` correct under the binary collation. No
> regressions.
>
> **Function stack on Fabric (also 2026-06-24):** discovered **scalar UDFs**, inline **TVFs** (projection +
> filter pushdown), **stored procs** as table functions, **custom C#** scalar/table functions, and the
> **in-out exchange `_each`** (per-row `CROSS APPLY`, MARS-free / gate-serialized) all work. **Fix applied:**
> proc result-set detection used `sys.dm_exec_describe_first_result_set_for_object`, which Fabric does not
> support (error 15871); switched `ProcResultColumns` to `sys.sp_describe_first_result_set` (over `EXEC [s].[p]
> @a=@a,…` with the input params declared) — supported on Fabric AND box, one path for both. (Fabric does
> support scalar UDFs / TVFs / procs / `datetime2(6)`; only `datetime2(7)` and native `json` are absent.)

## TL;DR

- One `ServerProfile` (EngineEdition + product version + database collation) is detected at
  `OpenCatalog`, folded into the existing ATTACH connection validation (no extra round-trip), and
  drives both **connection behavior** and **type mapping**. Explicit ATTACH options override it.
- Type differences that matter: **no `NVARCHAR`** (Fabric — `VARCHAR` is UTF-8), **no
  `DATETIMEOFFSET`**, **`datetime2` capped at scale 6**, native **`json`** only on SQL Server 2025+
  / Azure SQL DB.
- The **collation's UTF-8-ness** is the principled driver of `VARCHAR` vs `NVARCHAR` (more precise
  than edition alone). The collation's **binary-ness** gates string `ORDER BY` pushdown.
- **Collation is chosen at warehouse/lakehouse creation and there is no value that's ideal across
  the whole stack** (DuckDB + this extension + Lakehouse/Warehouse + a DAX/Power BI model on
  OneLake). We detect it and stay correct under any collation; we do not prescribe one.

## 1. The `ServerProfile` (detected once at OpenCatalog)

We already validate the connection at ATTACH, so add columns to that probe — no new round-trip:

```sql
SELECT SERVERPROPERTY('EngineEdition'),
       SERVERPROPERTY('ProductMajorVersion'),
       SERVERPROPERTY('ProductVersion'),
       DATABASEPROPERTYEX(DB_NAME(), 'Collation');   -- the collation new columns inherit
```

`EngineEdition` (capabilities derive from edition + version + collation together, never a single
brittle number):

| Value | Engine |
|------:|--------|
| 3 | Enterprise / Developer (box SQL Server) |
| 5 | Azure SQL Database |
| 6 | Azure Synapse Analytics — dedicated SQL pool |
| 8 | Azure SQL Managed Instance |
| 11 | Azure Synapse serverless / **Fabric Warehouse + Lakehouse SQL endpoint** — CONFIRMED against a Fabric Warehouse |

`ProductMajorVersion`: SQL Server 2022 = 16, **2025 = 17**; Azure SQL DB returns a rolling high
version. A **Fabric Warehouse reports 12** (and `ProductVersion` 12.0.x, collation
`Latin1_General_100_BIN2_UTF8`) — so `has_native_json` keys off `IsWarehouse`, not the low version
(verified live: edition 11 → `supports_mars`/`has_nvarchar`/`has_datetimeoffset` all false,
`max_datetime2_scale` 6, collation UTF-8 + binary + case-sensitive).

The profile stores **derived capabilities**, not raw numbers, so call sites read intent:

```
ServerProfile {
  EngineEdition, ProductMajorVersion, Collation;
  SupportsMars;          // false for Synapse/Fabric
  HasNVarchar;           // false for Fabric Warehouse/Lakehouse
  HasDatetimeOffset;     // false for Fabric
  MaxDateTime2Scale;     // 6 (Fabric) vs 7 (box/Azure SQL DB)
  HasNativeJson;         // ProductMajorVersion >= 17 || Azure SQL DB
  IsUtf8Collation;       // collation name ends in _UTF8
  IsBinaryCollation;     // _BIN / _BIN2
  IsCaseSensitive;       // _CS_ or _BIN2
  DefaultIsolation;      // snapshot for warehouse
}
```

It lives in C# on the catalog (provider-specific — C++ stays agnostic), cached for the connection
lifetime (edition/collation don't change). Explicit ATTACH options override the derived defaults.

## 2. Connection behavior

Covered in [transactions.md](transactions.md) §5.1 — summarized here because the profile drives it.
**DONE** (slice 3):

- `mssql_mars` is tri-state (`auto` | `true` | `false`); `auto` = `profile.SupportsMars`. `false`
  (pooled reads, no read-your-writes) is selectable on box SQL Server too, intentionally. Resolved once
  at first connection (a **global** setting today — `SET mssql_mars=…` **before** ATTACH; a per-catalog
  ATTACH option waits for the ATTACH-options→C# refactor).
- Warehouse mode → **pin only for writes, pooled connections for reads** (`ExecuteQuery` routes reads to a
  fresh pooled connection whenever MARS is off), write transaction at **snapshot** isolation
  (`ServerProfile.DefaultWriteIsolation`, Fabric only). Reads in a write-transaction don't see uncommitted
  writes — acceptable for warehouse workloads, documented. The in-out exchange is already MARS-free
  (gate-serialized). Verified: `test/verify_connection_mode.test`.

## 3. Type mapping (profile-driven `MapArrowToSqlType`)

Today `MapArrowToSqlType` ([SqlServerBackend.cs:2044-2079](../dotnet/ArrowNet.SqlServer/SqlServerBackend.cs#L2044-L2079))
hardcodes the box answers. It becomes profile-parameterized:

| DuckDB / Arrow | Box SQL Server / Azure SQL DB | Fabric Warehouse / Lakehouse |
|---|---|---|
| `VARCHAR` | `NVARCHAR(n)` (non-UTF8) / `VARCHAR(n)` (UTF8 collation) | `VARCHAR(n)` (UTF-8) |
| `TIMESTAMP` (naive) | `datetime2(7)` | `datetime2(6)` |
| `TIMESTAMPTZ` (instant) | `datetimeoffset(7)` | UTC `datetime2(6)` |
| `TIME` | `time(7)` | `time(6)` |
| `JSON` | `json` (2025+/Azure SQL DB) else `nvarchar(max)` | `varchar(max)` (no native json yet) |
| others | unchanged (BIT, SMALLINT, INT, BIGINT, DECIMAL, REAL, FLOAT, VARBINARY(MAX)) | same |

### 3.1 Timestamps & timezone — the rules

- **`TIMESTAMP` without time zone → `datetime2(scale)`, verbatim. No conversion, no DuckDB
  timezone involved.** Naive wall-clock both sides; DuckDB `TIMESTAMP` is µs = `datetime2(6)`
  exactly (so `datetime2(6)` is lossless on Fabric; box may use 7). The value path already hands SQL
  a plain `DateTime` for the no-tz case ([ArrowValueReader.cs:49-51](../dotnet/ArrowNet.SqlServer/ArrowValueReader.cs#L49-L51)).
- **`TIMESTAMPTZ` is an *instant*** — internally UTC µs, and it crosses Arrow as an instant. So
  `.UtcDateTime` is the stored value: `datetimeoffset(7)` where it exists, else UTC `datetime2(6)` on
  Fabric. **No session timezone is needed to store** (the Arrow `int64` is already the UTC epoch; the
  tz string is only metadata). On read, marking the Arrow column `timestamp[us, "UTC"]` lets **DuckDB
  apply the session `TimeZone` for display itself** — we don't replicate it.
- **Fabric ambiguity (document loudly):** without `datetimeoffset`, a stored `datetime2` can't record
  "this is an instant." A `TIMESTAMPTZ` written to Fabric reads back as a naive `TIMESTAMP` holding
  the UTC value — instant preserved, tz-aware marker lost. Default to naive on read.
- **Decision — naive↔naive (validated, DONE).** We keep the naive↔naive semantic (no session-tz
  dependency); we do NOT reinterpret a stored naive `datetime2` as session-local. Validated end-to-end
  under a non-UTC session zone (`America/New_York`) with the ICU extension now statically embedded
  (`extension_config.cmake`): a `TIMESTAMPTZ` preserves its instant (stored UTC `datetimeoffset` /
  re-displayed in the session zone), and a naive `TIMESTAMP` round-trips its wall-clock **unshifted** by
  the session zone. No code change was needed — the value path was already correct (a `TIMESTAMPTZ` Arrow
  value is a UTC instant, so storing `.UtcDateTime` needs no session zone). Verified:
  `test/verify_timezone.test`. The alternative SqlServerFlights "reinterpret stored naive as session-local"
  semantic is deliberately NOT adopted (it would surprise; it would need `TimeZone` plumbed C++→C# and
  ripple through the value + read-back paths).

### 3.2 VARCHAR length & the text-type settings

Three *separable* concerns — today they're conflated into one blunt knob:

- **Existing — `mssql_ctas_text_type`** ([registered at mssql_net_extension.cpp:99](../src/mssql_net_extension.cpp#L99),
  read into the CREATE path at [arrownet_schema_entry.cpp:1654-1667](../src/catalog/arrownet_schema_entry.cpp#L1654-L1667),
  default `NVARCHAR(MAX)`): a **whole-type-string** override applied **uniformly to every text column**
  in CTAS/CREATE — a native-compat knob, not a policy. `SET mssql_ctas_text_type='VARCHAR(8000)'`
  works but is all-or-nothing. (`mssql_convert_varchar_max` is a registered compat boolean, read-side,
  not the write choice.)
- **The varchar-vs-nvarchar *choice*** → driven by the **profile** (collation/edition), §4.
- **New — `mssql_default_varchar_length`** (int, or the literal `max`): the **length policy**,
  independent of the varchar/nvarchar choice. Needed for: usable CTAS/CREATE column types, **string
  PK/UNIQUE keys** (SQL Server caps key length ~900/1700 bytes, so `MAX` columns can't be keys — the
  blocker noted in CLAUDE.md's out-of-scope list), and Fabric's `varchar(n)` limits. It's a DuckDB
  `ClientContext` setting, so C++ reads it and passes it across the same channel `mssql_ctas_text_type`
  already uses.

**Precedence:** explicit `mssql_ctas_text_type` (whole string) > profile varchar/nvarchar choice +
`mssql_default_varchar_length` > `NVARCHAR(MAX)` default.

### 3.3 JSON (SQL Server 2025)

`HasNativeJson` = `ProductMajorVersion >= 17` or Azure SQL DB (Fabric: none yet — assume false until
confirmed).

- **Read-side — DONE.** A SQL Server `json` column is tagged with the canonical **`arrow.json`** Arrow
  extension in `SqlArrowMapping.ToArrowField` (storage type stays `Utf8`); DuckDB's Arrow **import** reads
  the extension and lands it as the **`JSON`** logical type. Independent of the boundary's produce-direction
  flags (import reads extension metadata regardless), and graceful: an engine/build without the json
  extension registered falls back to the `Utf8` storage type = `VARCHAR` (DuckDB
  `GetArrowExtensionInternal` falls back to the format type — no throw), so the value round-trips either
  way. The core `json` extension is **statically embedded** in our build (`extension_config.cmake`) so the
  `JSON` type + functions exist in the test binaries (this build reports v0.0.1, so json can't be autoloaded
  from the repo). Verified: `test/verify_json.test`.
- **Write-side — DEFERRED to §3.4.** A DuckDB `JSON` column still maps to `nvarchar`/`varchar`, NOT native
  `json`. Reason (confirmed in the DuckDB source, `arrow_converter.cpp:120`): DuckDB exports `JSON` as the
  `arrow.json` extension **only when `arrow_lossless_conversion = true`**, which our C++↔C# boundary
  deliberately forces **off** (the BOOLEAN→Int8 fix — see CLAUDE.md "boundary uses STANDARD encoding"). So a
  DuckDB `JSON` column arrives at C# as plain `Utf8`, indistinguishable from `VARCHAR`. Mapping it to SQL
  `json` requires the §3.4 granular-types work (lossless boundary + honor `arrow.json`/`arrow.bool8`/… on
  the produce side), the same prerequisite as UUID and lossless ALTER/CTAS.

### 3.4 Granular type conversions (future refinement) — reuse the SqlServerFlights pattern

Today `MapArrowToSqlType` switches on the Arrow `TypeId` alone — coarse (e.g. every string → `*(MAX)`,
no UUID, no precise round-trip). The sibling repo's `D:\repos\SqlServerFlights\SqlServerFlights\Airport\Data`
(`ArrowTypeConverter.cs`, `FlightField.cs`) is the granular reference (the user notes it's "ugly" but
thorough — adapt the idea, not the code). Two techniques worth lifting:

1. **Carry the original SQL type + precision/scale/length on the Arrow field METADATA** (`OrigDataType`,
   `NumericPrecision`, `NumericScale`, `CharacterMaximumLength`, `DateTimePrecision`, `OrdinalPosition`).
   On the read path the DuckDB type is then precise (e.g. `datetime2(p)` → the right `Timestamp` unit,
   `decimal(p,s)`), and on write-back the exact SQL type can be regenerated **losslessly** (`OrigDataTypeSql`
   rebuilds `decimal(p,s)` / `char(n)` / …). This is "put the original source datatype on the field metadata
   in case it's needed later."
2. **Use Arrow extension-type names to disambiguate same-storage types.** `FlightField.ToSql` matches on
   `(ArrowType, extensionName)`: `arrow.bool8` (an `Int8` that is really a BIT) → `BIT` vs a plain `Int8` →
   `TINYINT`; `arrow.uuid` (`FixedSizeBinary(16)`) → `UNIQUEIDENTIFIER`; `arrow.json` → the JSON path (§3.3).
   Notably **`arrow.bool8` is exactly how you'd distinguish a bool-as-`Int8` from a real `TINYINT`** — the
   ambiguity the lossless-boundary fix sidesteps by forcing standard Arrow (see CLAUDE.md "boundary uses
   STANDARD encoding"); a marker is the alternative if rich types are ever wanted at the boundary.

Relevance: this refines the warehouse type mapping (precise types), the §3.3 JSON gate (`arrow.json`), UUID
support, and lossless round-trip for `ALTER`/CTAS — and would carry over to the DAX provider's own mapping.

### 3.4.1 Findings (investigated 2026-06-24) — write-side deferred, read-side is the principled half

Ground truth from `duckdb/src/common/arrow/arrow_converter.cpp` `SetArrowFormat`: `arrow_lossless_conversion`
toggles an Arrow **extension** representation for exactly six types — `BOOLEAN`→`arrow.bool8` (Int8 storage),
`HUGEINT`/`UHUGEINT`→`arrow.hugeint`, `UUID`→`arrow.uuid` (`w:16`), `TIME_TZ`→`arrow.time_tz`, `BIT`→`arrow.bit`,
and `JSON` (alias)→`arrow.json`. Everything else is unaffected.

**The two directions are NOT symmetric in cost:**

- **Read path (C#→Arrow→DuckDB) — the principled half, low-risk, no boundary change.** C# *authors* the Arrow,
  so it can emit any extension type directly; DuckDB's import honors registered extensions
  (`arrow.uuid`/`arrow.json` are registered → `UUID`/`JSON`; unregistered → graceful fallback to the storage
  type). This is how **JSON read-side** and now **UUID read-side** work. **UUID read-side — DONE:**
  `uniqueidentifier` → `FixedSizeBinary(16)`+`arrow.uuid` → DuckDB `UUID`, replacing the prior surface-as-text
  behavior. The appender writes `Guid.ToByteArray(bigEndian:true)` (canonical RFC-4122 order — verified against a
  distinct-per-group GUID that a wrong byte order would scramble); the builder is a
  `FixedSizeBinaryArray.BuilderBase` subclass (Apache.Arrow 23.0.0 ships only the abstract base; the array type
  lives in the `Apache.Arrow.Arrays` namespace and the enum value is `ArrowTypeId.FixedSizedBinary`). Verified:
  `test/verify_granular_types.test`. **Write-side UUID stays deferred** (DuckDB `UUID` → `uniqueidentifier`):
  unlike JSON, the standard-boundary storage (string) differs from `arrow.uuid` (FixedSizeBinary(16)), so
  metadata-injection can't bridge it — it needs the lossless flip. Writing a DuckDB UUID into an *existing*
  `uniqueidentifier` column still works (the GUID string coerces); only the CTAS type choice would be `varchar`.

- **Write path (DuckDB→Arrow→C#) — high cost, low warehouse value → DEFERRED.** Detecting a DuckDB `JSON`/`UUID`
  on write needs the extension name on the produced schema, which only appears under `arrow_lossless_conversion =
  true`. Options: **(a)** flip the global boundary to lossless — but that also changes the *data* encoding of
  `BOOLEAN`→Int8, `HUGEINT`, `UUID`, `BIT`, `TIME_TZ` across **every** produce path (CTAS schema, `SqlBulkCopy`
  values via `ColumnAppender` — which has **no `Int8` case** today, filter constants, UPDATE/DELETE values), so
  every value reader must learn the extension storage; `BOOLEAN` is the common hazard. High blast radius,
  reverses the documented "boundary uses STANDARD encoding" decision. **(b)** inject `arrow.json` field metadata
  into just the JSON columns of the begin_bulk/create schema (C-ABI metadata-buffer encoding in `ArrowProducer`)
  — surgical but fiddly, data encoding untouched (JSON data is `Utf8` either way, so only the schema name
  changes). **(c)** pass JSON column indices to `begin_bulk`/`create_table` as an ABI arg — clean-ish but an ABI
  bump for incremental value.
  - **Why low value for the warehouse:** **Fabric has no native `json` type** (`has_native_json=false`, edition
    11), so DuckDB `JSON` → `varchar(MAX)` is already correct **and the only option** on the warehouse target.
    Native-`json` write only helps box SQL Server 2025 / Azure SQL DB, where `nvarchar(max)` already stores the
    JSON text fine (it just isn't the validated/indexable native type). So the write-side flip's payoff is
    incremental and does **not** apply to Fabric.

**Recommendation:** keep the STANDARD-encoding boundary (don't pursue option (a)). The read-side (JSON done; UUID
optional) is the principled, low-risk half. Revisit write-side native `json`/UUID via option (b)/(c) only if a
box-2025/Azure target makes it worthwhile, or fold it into the DAX provider's type-mapping work.

### 3.4.2 Scale-aware temporal precision — DONE (read-side)

`MapArrowToSqlType`'s read counterpart (`SqlArrowMapping`) used to map every `time`/`datetime2` to
**microsecond**, silently truncating the 7th fractional digit of `time(7)`/`datetime2(7)` (and `datetime2(7)`
is the *default*). Now scale-aware via `DbColumn.NumericScale`: **scale 7 → nanosecond** (DuckDB
`TIME_NS`/`TIMESTAMP_NS`, the 100ns digit preserved), **scale ≤6 → microsecond** (DuckDB `TIME`/`TIMESTAMP`,
the common types, full date range). The `Time64` value appender branches on the unit (`Ticks*100` for ns vs
`Ticks/10` for µs). `decimal`/`numeric` were already granular (`Decimal128(precision, scale)` from
`NumericPrecision`/`NumericScale`). **`datetimeoffset` stays microsecond `TIMESTAMPTZ`** — DuckDB has no ns+tz
type, so the tz instant keeps the correct type (the 7th digit is dropped, the prior behavior). Caveat: DuckDB
`TIMESTAMP_NS` spans only ~1677..2262, so a `datetime2(7)` value outside that errors **loudly** on read (a
Conversion Error, never silent corruption — an extreme edge for 100ns-precision timestamps). Verified:
`test/verify_granular_types.test`.

### 3.5 Constraints (PK / UNIQUE) + UPDATE/DELETE on warehouse

Fabric Warehouse / Synapse support `PRIMARY KEY` / `UNIQUE` / `FOREIGN KEY` **only** as `NONCLUSTERED NOT
ENFORCED` metadata hints, and **only added via `ALTER TABLE ADD CONSTRAINT`** — declaring a key **inline in
`CREATE TABLE` is rejected** (error 24584). So on a warehouse profile, `CreateTable` emits a plain column-only
`CREATE TABLE` followed by one `ALTER TABLE … ADD CONSTRAINT … PRIMARY KEY|UNIQUE NONCLUSTERED (…) NOT
ENFORCED` per key (named `PK_<table>` / `UQ_<table>_<n>`), run as separate statements; box SQL Server keeps the
inline single-statement form. The hints are **not enforced** (Fabric never checks uniqueness) but **do appear
in `sys.indexes`** (`is_primary_key` / `is_unique`), which is exactly what our rowid discovery
([`RowIdSql`](../dotnet/ArrowNet.SqlServer/SqlServerBackend.cs)) reads — so a keyed warehouse table gets a
**rowid**, enabling **UPDATE / DELETE**. Validated end-to-end on Fabric (2026-06-24): `CREATE TABLE … PRIMARY
KEY` via the extension produced a `NONCLUSTERED NOT ENFORCED` PK, and rowid-based UPDATE/DELETE worked.
**Caveat:** because the key is NOT ENFORCED, Fabric permits duplicate "key" values; a rowid-based UPDATE/DELETE
then trusts the declared key and could touch more than one row — the user's data-integrity responsibility, same
as any unenforced constraint. (Same-session `CREATE TABLE` then DML now works on Fabric without a refresh —
see the metadata read-your-writes note in [transactions.md](transactions.md) §5.1; the catalog re-fetches the
new table's columns on the pinned write connection so it's visible despite MARS-off pooled scans.)

## 4. Collation — the cross-stack problem

**Collation is chosen at warehouse/lakehouse creation time** (Fabric defaults to
`Latin1_General_100_BIN2_UTF8` — UTF-8, binary, case-sensitive — but offers a case-insensitive
`..._CI_AS_..._UTF8` option; Lakehouse endpoints have their own). **Do not assume — detect it.**

### Why it matters to us

- **`VARCHAR` vs `NVARCHAR` (principled driver) — DONE.** A `_UTF8` collation means `VARCHAR` stores
  UTF-8 → it holds full Unicode → DuckDB `VARCHAR` → SQL `VARCHAR` is lossless (and on Fabric it's the
  only option). A non-UTF-8 collation makes `VARCHAR` a legacy single-byte codepage → DuckDB's UTF-8
  strings **must** go to `NVARCHAR`. `MapArrowToSqlType` now keys off the **collation**, not the edition:
  `IsUtf8Collation ? VARCHAR : (HasNVarchar ? NVARCHAR : VARCHAR)` — so a *box* SQL Server DB that opted
  into a UTF-8 collation correctly gets `VARCHAR` (previously it wrongly got `NVARCHAR` because the choice
  was edition-driven via `HasNVarchar`). Fabric (edition 11, always UTF-8) and box non-UTF-8 DBs are
  unchanged. Verified: `test/verify_collation_pushdown.test` (a `BIN2_UTF8` box DB → a created text column
  is `varchar`).
- **Pushdown case-sensitivity.** A case-*insensitive* server collation makes a pushed equality/`LIKE`
  filter match a *superset* of what DuckDB would — **safe**, because our pushdown never erases (DuckDB
  re-applies every predicate). A `BIN2` collation matches DuckDB's case-sensitive bytewise semantics.
- **String `ORDER BY` pushdown** — **DONE** (slice 6). We used to never push `ORDER BY` on string keys
  (collation/sort-order mismatch risk). A **binary** collation sorts by byte value — identical to DuckDB —
  so the profile now *safely re-enables* string `ORDER BY`+`LIMIT` (TopN) pushdown when `IsBinaryCollation`.
  Wiring (no ABI): `ArrowNetCatalog` caches the flag at `LoadCatalog` via `FetchBinaryCollation` (reads the
  existing `ARROWNET_META_SERVER_INFO` profile), the scan bind threads it onto
  `ArrowStreamBindData::string_order_pushable`, and `arrownet_optimizer.cpp`'s `TryPushTopN` gate becomes
  `is_string && !string_order_pushable`. Verified: `test/verify_collation_pushdown.test` (binary DB →
  pushed) + `test/verify_orderby_pushdown.test` (CI_AS box → not pushed, both correct).

### The no-universal-winner reality

There is **no single collation that is ideal across the whole OneLake stack** — this is a known pain
point, not something we can fix by picking a default:

| Engine | String comparison |
|---|---|
| **DuckDB** | case-sensitive, bytewise/binary (default) |
| **Fabric Warehouse / Lakehouse SQL endpoint** | `BIN2_UTF8` default → **case-sensitive, binary** |
| **DAX / Vertipaq** (Power BI semantic model, incl. Direct Lake on OneLake) | **case-insensitive** (the engine even collapses casing in its string dictionary) |
| **Spark / Delta** (Lakehouse) | case-preserving names; comparison semantics nuanced |

- `BIN2` (case-sensitive) is **ideal for DuckDB ↔ this extension ↔ SQL endpoint**: semantics align,
  so string equality/comparison and even `ORDER BY` pushdown are sound.
- But `BIN2` **conflicts with DAX** (case-insensitive): a Power BI model over the same data treats
  `'Apple' = 'apple'`, while the warehouse treats them as distinct → relationship/join/filter
  mismatches and "missing/ambiguous row" surprises. This is the classic CS-vs-CI clash people hit.
- A **case-insensitive** warehouse collation aligns with DAX but then DuckDB pushdown over-matches
  (still safe for us via never-erase, just less selective) and string `ORDER BY` pushdown is unsafe.

### Our stance: adaptive, not prescriptive

1. **Detect** the collation → `IsUtf8Collation` / `IsBinaryCollation` / `IsCaseSensitive` flags.
2. **Stay correct under any collation:** never-erase keeps string-predicate pushdown a correct
   superset under case-insensitive collations.
3. **Enable the optimization only when provably safe:** push string `ORDER BY`+`LIMIT` only when
   `IsBinaryCollation`; otherwise keep it off (current behavior).
4. **Choose `VARCHAR`/`NVARCHAR`** from `IsUtf8Collation` (§3, §4).
5. **Guidance, not enforcement (for the docs/README):** if your stack is DuckDB + the SQL endpoint
   only, the `BIN2_UTF8` default is ideal. If you also serve a Power BI/DAX model from the same
   OneLake data, you will face the CS/CI tension regardless of us — choose the collation by your
   dominant consumer; we are correct under either, but no collation makes a case-sensitive store and
   a case-insensitive DAX engine agree.

## 5. Architecture & ABI impact

- **Profile + type mapping = C#** (provider-specific; C++ stays agnostic, per the project's standing
  rule). The profile is queried in `OpenCatalog` and cached on the catalog.
- **DuckDB-side settings cross C++→C#:** `mssql_default_varchar_length` and (if the reinterpret
  semantic is chosen) `TimeZone` are read from the `ClientContext` in C++ and shipped on the
  create/bulk/scan requests — the same pattern `mssql_ctas_text_type` already uses, so likely **no new
  ABI vtable entry**, just added fields on existing request payloads (a signature change → an ABI
  version bump, but no slot shift). Confirm when implementing.

## 6. Sequencing

1. **`ServerProfile` detection** at OpenCatalog (the foundation everything keys off). **DONE** — slice 1
   (`ServerProfile.cs`, lazy detection via a non-MARS probe, MARS gated on `SupportsMars`); slice 2 added
   the `mssql_server_info(catalog)` diagnostic + `test/verify_server_profile.test`.
2. **Profile-driven `MapArrowToSqlType`** (`VARCHAR`/`NVARCHAR` by `HasNVarchar`, `datetime2`/`time` scale
   by `MaxDateTime2Scale`, tz → `datetimeoffset`-or-UTC-`datetime2` by `HasDatetimeOffset`). **DONE** — slice 3,
   box-preserving (edition 3 → identical output); **validated live on Fabric**: a CTAS of a
   `VARCHAR`/`TIMESTAMP`/`DATE`/`INT` table produced `varchar(MAX)` / `datetime2(6)` / `date` / `int` and
   round-tripped with µs fidelity. **Fabric accepts `varchar(MAX)`** (`CHARACTER_MAXIMUM_LENGTH = -1`), so the
   length setting (step 4) is NOT required for CTAS — only for string keys. **3b (tz) DONE:** validated under
   a non-UTC session zone with ICU embedded — `TIMESTAMPTZ` preserves its instant, naive `TIMESTAMP` is
   unshifted (naive↔naive); no code change needed (the value path was already correct). Granular read-side
   refinements (§3.4) also done: `time(7)`/`datetime2(7)`→ns, `uniqueidentifier`→`UUID`.
   `test/verify_timezone.test`, `test/verify_granular_types.test`.
3. **Connection mode** (`mssql_mars` tri-state, pooled reads, snapshot default) — see
   [transactions.md](transactions.md) §5.1. **DONE** — slice 3, C#-only (no ABI): `mssql_mars` provider
   setting resolved at first connection; MARS-off reads take a fresh pooled connection (no read-your-writes
   in a write txn); Fabric write transactions run at SNAPSHOT (`ServerProfile.DefaultWriteIsolation`).
   `test/verify_connection_mode.test`. *Remaining:* a per-catalog `mars` ATTACH option (with the
   ATTACH-options→C# refactor).
4. **`mssql_default_varchar_length`** + length-aware `VARCHAR` (unblocks string PK/UNIQUE keys; NOT needed
   for plain CTAS since Fabric takes `varchar(MAX)`).
5. **JSON type gate** (smallest, independent). **Read-side DONE** (§3.3: SQL `json` → DuckDB `JSON` via
   `arrow.json`; json statically embedded; `test/verify_json.test`). **Write-side deferred** to §3.4 (the
   boundary forces `arrow_lossless_conversion` off, so DuckDB `JSON` reaches C# as plain `Utf8`).
6. **Collation-aware pushdown relaxation** (string `ORDER BY` on binary collations) — optimization.
   **DONE** — no ABI: `FetchBinaryCollation` (reuses `ARROWNET_META_SERVER_INFO`) caches the flag on the
   catalog at `LoadCatalog`; scan bind → `ArrowStreamBindData::string_order_pushable`; optimizer gate
   relaxed to `is_string && !string_order_pushable`. `test/verify_collation_pushdown.test` (binary) +
   `test/verify_orderby_pushdown.test` (non-binary, explicit not-pushed proof).

## 7. Open decisions

- **Naive-`datetime2` semantic:** RESOLVED — keep naive↔naive (validated under a non-UTC session zone, §3.1;
  the reinterpret-as-session-local alternative is deliberately not adopted).
- **Auto-detect vs explicit-only** for the connection mode: recommend auto from the profile with an
  explicit override.
- **Collation guidance** to publish in the README for OneLake users (the §4 trade-off).
