# Multi-edition support — Synapse, Fabric Warehouse, Lakehouse SQL endpoint

> Design for connecting `mssql_net` to engines beyond box SQL Server / Azure SQL Database:
> Azure **Synapse** dedicated pools, **Fabric Warehouse**, and the **Lakehouse SQL analytics
> endpoint**. These differ from box SQL Server in connection capabilities (no MARS, snapshot
> isolation) **and** in their type systems and collation. The strategy: **detect a server
> capability profile once at ATTACH and adapt** — be collation-*adaptive and always correct*,
> never collation-*prescriptive*. Status: **slices 1–2 implemented** — `ServerProfile` detection
> (`dotnet/ArrowNet.SqlServer/ServerProfile.cs`) + the `mssql_server_info(catalog)` diagnostic that
> surfaces it; profile-driven type mapping, connection mode, and settings are the remaining slices
> (§6). File references are repo-root-relative. Connection/transaction behavior is in
> [transactions.md](transactions.md).

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

`EngineEdition` (well-known values; **confirm Fabric's empirically — do not hardcode brittle
assumptions**, derive capabilities from edition + version + collation together):

| Value | Engine |
|------:|--------|
| 3 | Enterprise / Developer (box SQL Server) |
| 5 | Azure SQL Database |
| 6 | Azure Synapse Analytics — dedicated SQL pool |
| 8 | Azure SQL Managed Instance |
| 11 | Azure Synapse serverless / **Fabric Warehouse + Lakehouse SQL endpoint** (verify) |

`ProductMajorVersion`: SQL Server 2022 = 16, **2025 = 17**; Azure SQL DB returns a rolling high
version.

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

Covered in [transactions.md](transactions.md) — summarized here because the profile drives it:

- `mars` ATTACH option is tri-state (`auto` | `true` | `false`); `auto` = `profile.SupportsMars`.
  `false` (pooled reads, no read-your-writes) is selectable on box SQL Server too, intentionally.
- Warehouse mode → **pin only for writes, pooled connections for reads**, write transaction at
  **snapshot** isolation. Reads in a write-transaction don't see uncommitted writes — acceptable for
  warehouse workloads, documented. The in-out exchange is already MARS-free (gate-serialized).

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
- **Open decision — the only timezone choice to pin:** keep naive↔naive (recommended; no session-tz
  dependency), or adopt the SqlServerFlights semantic "treat a stored naive `datetime2` as wall-clock
  in the session zone and convert to UTC." The latter needs the DuckDB `TimeZone` setting plumbed
  from the C++ `ClientContext` (`TryGetCurrentSetting("TimeZone", …)`) across to C#, and is only
  meaningful with the ICU extension loaded. It ripples through both the value reader and the
  read-back path, so decide before building.

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
confirmed). When true, map the Arrow JSON canonical extension type (and DuckDB's `JSON` logical type)
→ SQL `json`, and read a `json` column back as the Arrow JSON extension type so it lands as DuckDB
`JSON`. Otherwise fall back to `nvarchar(max)` / `varchar(max)`. One profile-gated branch, symmetric
both directions.

## 4. Collation — the cross-stack problem

**Collation is chosen at warehouse/lakehouse creation time** (Fabric defaults to
`Latin1_General_100_BIN2_UTF8` — UTF-8, binary, case-sensitive — but offers a case-insensitive
`..._CI_AS_..._UTF8` option; Lakehouse endpoints have their own). **Do not assume — detect it.**

### Why it matters to us

- **`VARCHAR` vs `NVARCHAR` (principled driver).** A `_UTF8` collation means `VARCHAR` stores UTF-8 →
  it holds full Unicode → DuckDB `VARCHAR` → SQL `VARCHAR` is lossless (and on Fabric it's the only
  option). A non-UTF-8 collation makes `VARCHAR` a legacy single-byte codepage → DuckDB's UTF-8
  strings **must** go to `NVARCHAR`. So the rule is `IsUtf8Collation ? VARCHAR(n) : NVARCHAR(n)` —
  which also correctly handles a *box* 2019+ database that opted into a UTF-8 collation.
- **Pushdown case-sensitivity.** A case-*insensitive* server collation makes a pushed equality/`LIKE`
  filter match a *superset* of what DuckDB would — **safe**, because our pushdown never erases (DuckDB
  re-applies every predicate). A `BIN2` collation matches DuckDB's case-sensitive bytewise semantics.
- **String `ORDER BY` pushdown.** Today we never push `ORDER BY` on string keys (collation/sort-order
  mismatch risk). A **binary** collation sorts by byte value — identical to DuckDB — so the profile
  can *safely re-enable* string `ORDER BY`+`LIMIT` pushdown when `IsBinaryCollation`. A real
  optimization the profile unlocks.

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
2. **Profile-driven `MapArrowToSqlType`** (`VARCHAR`/`NVARCHAR` by collation, `datetime2(6)`, UTC
   `datetime2` for tz, `time(6)`) + the matching value-reader branch.
3. **Connection mode** (`mars` tri-state, pooled reads, snapshot default) — see
   [transactions.md](transactions.md).
4. **`mssql_default_varchar_length`** + length-aware `VARCHAR` (also unblocks string PK/UNIQUE keys
   generally, not just Fabric).
5. **JSON type gate** (smallest, independent).
6. **Collation-aware pushdown relaxation** (string `ORDER BY` on binary collations) — optimization.

## 7. Open decisions

- **Naive-`datetime2` semantic:** keep naive↔naive (recommended) vs reinterpret stored naive as
  session-local (needs `TimeZone` + ICU). §3.1.
- **Auto-detect vs explicit-only** for the connection mode: recommend auto from the profile with an
  explicit override.
- **Collation guidance** to publish in the README for OneLake users (the §4 trade-off).
