# The catalog/table abstraction — design for retiring `get_metadata` (COMPLETE — every slice 1a/1b/2/3 + 4a–4d + 5 BUILT)

> Written 2026-08-14 after the user's review: *"I don't like the getmetadata functions … there should be no
> provider-specific function defined in C++, all must live in the providers … an abstraction similar to
> ITableFunction could be beneficial (an "ITable") … I prefer a clean refactor over overlaying code for
> backward compat."* This doc is the analysis + the proposed design. Every claim about current code was
> read from the tree on 2026-08-14, not recalled.

## 1. What `get_metadata` actually is today — the inventory

ONE ABI entry — `get_metadata(handle, int kind, const char *arg1, const char *arg2) → ArrowArrayStream` —
multiplexing **16 kinds** (`abi.h:101-133`). Each provider implements it as a `kind switch`
(`DeltaCatalog.cs:960`, `SqlServerBackend.cs:2377`, `DaxCatalog.cs:119`, `DeltaRsCatalog.cs:140`). The kinds
fall into three categories that have nothing to do with each other:

### Category A — the CATALOG PROTOCOL (host materializing entries)

| kind | what | encoding smell |
|---|---|---|
| 0 Schemas | schema names | one-column string table |
| 1 Tables | (schema, table, type) | three-column string table |
| 2 Columns | a table's column layout | **a zero-row stream whose Arrow SCHEMA is the answer** |
| 3 RowId | row-identity column names | string table |
| 4 RowCount | approximate count | **a number as text** |
| 5 ColumnNdv | per-column NDV | numbers as text |
| 6 Functions | discovered routines | 5-column string table |
| 7 ServerInfo | capability profile | ~~(property, value) strings, string-matched on BOTH sides~~ **the capability half RETIRED by slice 3 (ABI v71 `get_capabilities`)** — kind 7 is now diagnostic-only (`fabricator_server_info()`), the host never reads it |
| 12 VirtualColumns | provider virtual columns | string table |
| 15 CatalogMacros | provider-declared macros | 3-column string table |

### Category B — PROVIDER FEATURES wearing C++-registered function fronts

Kinds 8–14 exist only to serve **eight `TableFunction` registrations in `fabricator_extension.cpp:692-732`**:
`fabricator_delta_snapshots`, `fabricator_delta_changes` (×2 overloads),
`fabricator_delta_get/set_transaction_version` (×3), `fabricator_delta_tblproperties`,
`fabricator_delta_set_tblproperties`. Provider-specific surface **hardcoded in C++** — which is exactly what
the whole `ICatalogTableFunction`/`CatalogFunctionSet`/global-functions machinery was built to make
unnecessary, and *is* unnecessary for everything built after it (`fabricator_delta_scan`, `fab_delta_info`,
the 51 `fabric.*` functions are all C#-declared). These eight predate that machinery and were never migrated.

### Category C — WRITES routed through a metadata READ

`SetTxnVersion` (kind 11) and `SetTblProperties` (kind 14) **commit to the Delta log** through an entry named
`get_metadata`. The payloads are packed strings (`arg2 = "expected:value"` / a properties blob;
`Changes` packs `arg1 = 'schema.table'`, `arg2 = "from:to"`). This is the single ugliest thing in the ABI:
a mutation with transaction semantics riding a stringly-typed read channel.

### The cross-cutting defects (all previously recorded, here connected)

- **Statelessness forces side caches.** A table's entry materialization is **5 crossings**
  (Columns at bind; RowId, VirtualColumns, RowCount, NDV at first scan — visible as `kind=2` then
  `kind=3,12,4,5` in any fablog), each stateless, each a potential `_delta_log` open. The mitigations are
  the evidence: `DeltaTableCache` keyed `(txn, path)`, `_rowTrackingByPath` (a dictionary smuggling the
  row-tracking flag from kind 2's call to kind 12's), `_tableConfigCache`, `SnapshotPinning` — **four
  string-keyed side caches standing in for one object that owns the table**.
- **Unknown-kind behaviour is inconsistent**: SqlServer throws, Delta answers `EmptyStringTable("name")` —
  which is the exact shape behind the latent `ReadStringTable` OOB the macros pass had to guard
  (host asks 3 columns, provider answers its 1-column fallback).
- **Typed data crosses as text** (RowCount, NDV, the ServerInfo booleans) and is parsed back by grep.

## 2. The design

### 2.1 Category B/C → provider-declared functions — independent of everything else, pure deletion (slice 2 in §5's order)

The Delta catalog already hosts a `CatalogFunctionSet` (`fab_delta_info`, `fabric.*`). Declare a **`delta`
function namespace** on every Delta catalog (exactly the `fabric` schema mechanism, incl. its
`CatalogSchemaNames` advertisement and DDL refusal):

```sql
SELECT * FROM lake.delta.snapshots('dbo.t');
SELECT * FROM lake.delta.changes('dbo.t', starting_version := 5 [, ending_version := 9]);
SELECT * FROM lake.delta.changes('dbo.t', starting_timestamp := TIMESTAMP '…');  -- the deferred
                                                          -- timestamp-bounds item, banked C#-ONLY
SELECT * FROM lake.delta.tblproperties('dbo.t');
SELECT * FROM lake.delta.set_tblproperties('dbo.t', '…');
SELECT * FROM lake.delta.get_transaction_version('dbo.t', 'app');
SELECT * FROM lake.delta.set_transaction_version('dbo.t', 'app', 5 [, expected := 4]);
```

⚠ The bounds are NOT `from :=`/`to :=` — both are RESERVED WORDS, and a named parameter that is one is a
PARSER error that reads as a broken function (the `offset :=` lesson). The Spark option vocabulary
(`starting_version`/`ending_version`/`starting_timestamp`/`ending_timestamp`) is reserved-word-safe and
carries Delta's boundary semantics in its own names (starting = first at-or-after, ending = last at-or-before).
This doc's first draft wrote `from :=` and would not have bound.

Then DELETE: kinds 8–14, the eight C++ registrations, their seven bind functions in
`fabricator_extension.cpp`, and every provider's arms for those kinds. **Breaking, no aliases** (the
fabricator-rename precedent). What must carry over and is already available on this path:
- the ambient txn (`FabricatorSetActiveTxn` runs before catalog-function execution via the same scan
  machinery), which `set_transaction_version`'s park-on-txn-buffer semantics need;
- typed args — the version bounds become real `BIGINT`/`TIMESTAMP` parameters instead of `"from:to"`,
  and the CAS `expected` becomes an optional named parameter instead of a packed string. ⚠ Keep the
  **absent ⇒ must-not-exist** semantics (`requireAbsent`) — the #43 lesson: the default is the dangerous
  answer.
- ⚠ `fabricator_server_info(catalog)` and `fabricator_functions(catalog)` are provider-AGNOSTIC diagnostics
  (arg = any catalog/connstr/secret) and STAY host-registered — the rule is *no provider-SPECIFIC surface in
  C++*, not *no functions in C++*. Their kinds (6, 7) survive only as long as §2.3 hasn't replaced the
  transport underneath them.

### 2.2 Category A → `ITable`, an object that owns the table

The `IBackendCatalog` surface passes `(schemaName, tableName)` on **every** member (Scan, BulkInsert,
Delete, Update, InsertReturning, Alter, Create, Drop + five metadata kinds) — that name-pair-per-call is
*why* nothing can hold state. Replace the per-table slice with:

```csharp
public interface ITable : IDisposable
{
    Schema Schema { get; }                       // replaces kind 2 (typed, not zero-row-stream trickery)
    TableInfo Info { get; }                      // replaces kinds 3+12+4+5 in ONE object:
                                                 //   RowId (names + kind: KeyColumns|Virtual|Identity),
                                                 //   VirtualColumns, Statistics? (rowCount, ndv — lazy)
    IArrowArrayStream Scan(ScanSpec spec, IArrowArrayStream? filterValues);
}
// Capability interfaces, the ICatalogScalarFunction pattern — absence IS the capability answer,
// no kind ints, no vacuous default implementations:
public interface IWritableTable : ITable    { /* BulkTarget/Delete/Update/InsertReturning */ }
public interface IAlterableTable : ITable   { /* Alter(AlterSpec) */ }
```

and on the catalog: `ITable OpenTable(string schema, string table)` — throwing
`ObjectNotFoundException` exactly as `GetMetadata(Columns)` does today (the absence contract moves, it does
not weaken).

**What this buys, concretely:**
- **State lives where it belongs.** `DeltaTable`, the resolved schema, the row-tracking flag, the parquet
  tuning config, a connection affinity — instance fields on the provider's `ITable` implementation.
  `_rowTrackingByPath` and `_tableConfigCache` dissolve outright; `DeltaTableCache` shrinks to the
  per-*transaction* snapshot question (see §3).
- **5 crossings per table become 2** (`table_open` + reading `Info`), which on OneLake is the difference
  between five potential log opens and one.
- **Virtual tables become first-class.** Discovery returns table *definitions* (name, kind); a provider can
  return an `ITable` backed by anything — a DMV, a computed view, a `snapshots` table — with the same ease
  `CustomFunctions` declares a function. That is the user's stated motivation and the design's test: if a
  provider cannot ship a virtual table in ~30 lines, the abstraction failed.

### 2.3 The transaction model — `ITransaction`, and the table as (definition × transaction)

*Added after review: "Providers hold the DeltaTable/connection/config as instance state" is
provider-dependent, and the open question was where a TransactionManager fits.* The answer was in the tree:
**both providers already have an unnamed transaction manager, and C++ has a named one.**

- `DeltaTxnBuffer._byTxn` is `ConcurrentDictionary<long, ConcurrentDictionary<string, PendingAppends>>` —
  literally **txn → (path → per-table-in-txn state)**. `PendingAppends` (held table, held EW transaction,
  pending actions, identity HWM, CDF files) IS "an ITable bound to a transaction", spelled as a dictionary
  value.
- SQL Server's `TxnState` (`SqlServerBackend.cs:1027` — connection, SqlTransaction, MarsEnabled, ExecGate)
  IS "an ITransaction", spelled as a dictionary value keyed by `long` in `_txns`.
- C++'s `FabricatorTransactionManager` already owns the per-DuckDB-txn lifetime and calls
  begin/commit/rollback across the ABI. **The C# side is the only layer missing the concept.**

So the model is THREE objects with distinct lifetimes, and per-txn state stops being "keyed by ambient txn
inside the table object" (§6's hand-wave) and becomes ownership:

```csharp
ITableDefinition def = catalog.GetTable(schema, name);   // per catalog entry, txn-FREE:
                                                          //   path/identity, declared Schema, Info,
                                                          //   capabilities. What the C++ entry holds.
ITransaction txn   = catalog.Begin(bool isExplicit);      // per DuckDB transaction, HOST-managed:
                                                          //   Commit() / Rollback() / Dispose().
ITable t           = def.Bind(txn, at: null);             // the SESSION: Scan + DML live here, and so
                                                          //   does ALL per-(table × txn) state.
```

**The bind is the deliberate `ITableFunction` symmetry** — "like a table function but no args", with one
exception that falls out of DuckDB's own grammar: a table REFERENCE has exactly one bindable argument, the
**AT clause**. So `Bind(txn, at?)`, and the differences from a function bind are the design, not wrinkles:
- a function bind resolves an ARG-dependent schema; a table bind resolves STATE (the pinned snapshot, the
  txn's pending CREATE/ALTER shape override, the connection borrow) and an AT-dependent schema (the as-of
  columns across DDL — the time-travel fix's contract, §3 item 8, now with a settled home);
- a bound function lives for ONE scan; a bound table lives for the TRANSACTION and is **memoized** — a
  second reference to the table in the same txn gets the same binding, which is precisely what dissolves
  `DeltaTableCache`;
- an AT binding is a SEPARATE bound instance, never the shared one — the object-model form of the C++ fact
  that AT entries live in their own map because time travel is a property of a reference, not of the catalog;
- retiring a definition drops its bindings (a renamed table's old binding must not serve the new name) —
  today's coarse `InvalidateReadCache` rule as object identity instead of a cache sweep.

**⚠ BIND IS STATE, SCAN IS REQUEST — filters NEVER ride the binding.** Two reasons, one of them not about
timing. (1) Dynamic/join filters and the late-materialization rowid filter only EXIST at execution: under
`filter_pushdown=true` DuckDB delivers the live `TableFilterSet` (erased static filters + runtime join
filters) per scan, and `arrow_ingest` renders it into the spec at `InitGlobal` time
(`arrow_ingest.cpp:738-757`) — the identical contract table functions already have (`TableFunctionScan`
arrives at execute; bind resolves only the output schema). (2) **One binding serves N scans with DIFFERENT
specs** — a self-join is one bound table, two scans, two filter sets; a predicate baked into the binding
would make the second scan read through the first one's filter. So `ITable.Scan(ScanSpec)` is the ONLY
predicate channel (projection, static + dynamic filters, TOP/ORDER, all per execution), the bind-time
`pushdown_complex_filter` render remains a C++ bind-data artifact riding the spec, and `table_scan`'s ABI
shape (spec at exec) is already right. Consequence to state on the interface: `Scan` must tolerate
CONCURRENT invocations on one binding (parallel plans, UNION branches, self-joins under exact mode), with
per-scan state living on the RETURNED stream, never the binding — which inherits the engineered-wood
thread-safety invariant this repo already re-checks at every bump (no `_currentSnapshot` assignment on a
read path; unenforced upstream, so it stays a bump-time check).

**What each provider puts in them** (the contents are provider-dependent, the shape is not):

| | `ITransaction` | `ITable` (= def × txn) |
|---|---|---|
| Delta | IsExplicit, the flush (today's Commit/Rollback buffer drain), read-cache invalidation | the pinned snapshot, the open `DeltaTable`, the held `DeltaTransaction`, pending actions/CDF — today's `PendingAppends` + the `DeltaTableCache` entry + the `SnapshotPinning` pin, unified |
| SQL Server | pinned connection + `SqlTransaction`, `MarsEnabled` (the mode it was OPENED with — the `TxnMars` rule becomes a field read), `ExecGate`, the read-isolation pin | thin: mostly borrows the transaction's connection; external-table routing info |

**What dissolves outright**: `_txns`, `DeltaTxnBuffer`'s outer map, `SnapshotPinning`, and
`DeltaTableCache` **fully** (its key is exactly (txn, path) — it IS `txn → boundTable`, so the object model
subsumes it including `MaxTablesPerTxn`, which becomes a bound-tables cap on the ITransaction). The
**TransactionManager** the design needs is then ONE small host-side map `txnId → ITransaction` in the
Bridge — the C# twin of `FabricatorTransactionManager` — replacing four ad-hoc dictionaries. Disposal
ordering bugs of the `transactions.erase()`-destroys-before-read class become structural: the host disposes
the ITransaction, which owns and disposes its bound tables.

**ABI transport**: unchanged initially. `set_active_txn` keeps carrying the id; the manager resolves
id → `ITransaction`. That makes this slice **C#-only and behaviour-preserving** — the object model is where
the cleanliness pays, and reifying a `txn_handle` in the ABI (with `table_open(handle, txn, …)`) stays a
later option rather than a prerequisite. Autocommit needs no special case: DuckDB always opens a
transaction, so there is always an ITransaction; `txnId == 0` remains only for genuinely transaction-free
contexts (global functions), same convention as the settings' global layer.

⚠ Two §3 subtleties land here and must be stated on the interfaces: the COMMIT flush must not read through
its own bound tables' cached state (fresh base — §3 item 1), and `ITransaction.Rollback` must capture
whatever it needs (session, IsExplicit) **before** the manager unregisters it — the class of bug that has
now been found twice (`txn_id_` hoist; the IsExplicit-read-after-Remove flag loss).

### 2.4 The ABI shape — a `table_*` session, mirroring `tablefn_*`

The 2026-08-14 rename (`table_bind/execute/close` → `tablefn_*`) freed exactly this prefix, and the
convention it documented (session entries = `<noun>_<verb>`) is what this follows:

```
table_open(handle, schema, table, *out_table)      // resolves ITable; NOT_FOUND status = absence
table_schema(table, ArrowSchema *out)              // replaces META_COLUMNS
table_info(table, char **out_json)                 // ONE typed JSON doc: rowid, virtual columns, stats
table_scan(table, spec_json, filters, *out)        // scan_table minus the name pair
table_delete / table_update / begin_bulk(table, …) // DML minus the name pair
table_alter(table, alter_json)                     // one struct instead of (kind,arg1,arg2,Field,flags)
table_close(table)
```

Catalog-level discovery keeps Arrow streams (right tool for LISTS) but as **dedicated typed entries** —
`catalog_schemas`, `catalog_tables` — with declared schemas, not kind ints. `ServerInfo`'s capability half
(`exact_filter_pushdown`, `is_binary_collation`) moves into **`open_catalog`'s result** as one JSON doc read
once at ATTACH, killing the grep-a-string-table pattern; the diagnostic table function keeps its own path.
*(⚠ As built (slice 3, ABI v71) the carrier is a dedicated `get_capabilities` entry called from
`LoadCatalog`, NOT `open_catalog`'s result — the letter would have forced a connection inside
`open_catalog`; see §5 item 3.)*
`get_metadata` is then **deleted** — one bump, no aliases, no compat arms.

C++-side lifetime: the table handle lives on `FabricatorTableEntry` and follows the existing
retire-don't-destroy graveyard (the entry's raw-pointer hazard is unchanged; the handle must be released in
the retire path, not the evict path).

**Refresh / invalidation — discovery survives, the multiplexer does not.** `fabricator_refresh_cache` and
the self-healing/rollback invalidation keep their exact shape; only what they call changes:
- A refresh re-runs the LIST entries (`catalog_schemas`, `catalog_tables`, the functions/macros discovery) —
  the same crossings today's kinds 0/1/6/15 make, now with declared schemas.
- Invalidation of an entry RELEASES its table handle (retire → graveyard → `table_close` at teardown) and
  re-derives NOTHING eagerly — the next touch pays `table_open`, which re-reads. That preserves today's
  deliberate laziness (`InvalidateAllEntries` drops materialized entries, keeps name lists) as a structural
  property instead of a discipline: "invalidate" IS "release the object".
- The self-heal absence signal moves from a failed Columns fetch to `table_open`'s NOT_FOUND status — same
  contract (§3 item 2), one call earlier.

## 3. What must NOT be lost — the load-bearing subtleties a clean rewrite would re-discover expensively

1. **The commit paths must NEVER be served from cached table state.** `verify_delta_catalog_transactions`
   §41 pins it: a flush served a stale base makes every append racing a metadata edit start FAILING
   (Delta conflicts on concurrent `metaData` unconditionally over the base→attempt range; fresh base ⇒ empty
   range). In this design that becomes an explicit contract — the WRITE members open fresh internally — which
   is *better* than today's invalidation-list discipline, but only if stated on the interface.
2. **Absence is established, never inferred** (`ObjectNotFoundException`; SQL Server = error 208, Delta =
   no commit in the log; unknown ≠ absent). Moves onto `table_open`.
3. **A METADATA read is exempt from the MARS gate and MUST run read-your-writes** — the self-healing cache
   depends on a just-created table's metadata being visible on the transaction's own connection
   (`FabricatorSchemaEntry::CreateTable`). `OpenTable` inside a txn inherits this requirement.
4. **A buffered transaction's pending CREATE/ALTER shape wins over storage** in the Columns answer
   (`ColumnsSchema`). `ITable.Schema` must keep consulting the txn buffer.
5. **Enumeration is bounded** (`MaxTablesPerTxn = 32`, decline-don't-evict) because
   `information_schema.tables` materializes every table. `OpenTable` during enumeration must stay cheap —
   the `Info` fields are lazy for exactly this reason.
6. **`schema_filter`/`table_filter` are applied provider-side** (discovery only, never targeted access).
7. **Warehouse engines never issue swallowable statements** (stats gated on `Profile.IsWarehouse` — a failed
   probe aborts an open Fabric transaction). `Statistics` stays optional/lazy per provider.
8. **The AT-clause (time travel) entry** sources its columns from the scan's own as-of describe — `OpenTable`
   needs the AT clause at bind — settled in §2.3: `Bind(txn, at?)`, with an AT binding always a separate
   bound instance.

## 4. The connection router (SQL Server) — encapsulating pinned/pooled/drained/snapshot

Today the decision is spread across `ExecuteQuery`'s 130-line preamble (five booleans:
`readYourWrites`/`materialize`/`snapshotRead`/`readIsolationPin`/MARS), `EnsureTxnConnection`, `BeginWrite`,
`SinkRequiresDrainedScan`, `PooledScanSelfBlockReason`, `EnsureScanCannotSelfBlock`, `TxnMars` — 91
references. The *outcomes* are only four:

| route | connection | isolation | reader | today's spelling |
|---|---|---|---|---|
| PinnedStreaming | txn's | txn's | held open | MARS on, in txn |
| PinnedDrained | txn's | txn's | drained + closed before return | `materialize`, or read_isolation∧¬MARS (+ ExecGate) |
| Pooled | fresh | READ COMMITTED | streaming | default |
| PooledSnapshot | fresh | SNAPSHOT | streaming | `snapshotRead` |

Proposal: ONE resolver — `ScanRoute Route(ScanIntent intent)` returning a record
`{ Kind, Drain, Gate, Reason }` — with the current guards as **named rules evaluated in order** (the
contradiction refusal, the self-block refusal, the read-isolation pin, the MARS gate, the sink drain), each
carrying the one-line *why* that today lives in comments. The `Reason` goes into the existing
`query [pinned…]` Debug line, so the log finally says *why* a scan routed, not just where.
**Behaviour-preserving by construction** — the rules are transcriptions, the gates are the existing suites
(`verify_read_isolation` 47, `verify_mars_dynamic` 44, `verify_mars_off_same_catalog` 95,
`verify_read_write_same_catalog` 68), and the transcription is checkable against the
[docs/transactions.md](transactions.md) §5.6a matrix — which stops being prose about the code and becomes
the same table the code evaluates.

## 5. Migration order (each slice green before the next; clean breaks, no compat layers)

1. **The connection router + the `ITransaction` extraction** — both C#-only, behaviour-preserving, and they
   compose: the router's inputs (pinned connection, MarsEnabled, ExecGate, the read-isolation pin) are
   exactly the SQL Server `ITransaction`'s fields, so extracting the object first makes the router a reader
   of one type instead of three dictionaries. Delta's half retires `SnapshotPinning` + `DeltaTableCache`
   into the bound-table objects. De-risks everything after (every later slice touches scan paths; better to
   touch them once they are legible). Gates: the existing routing/transaction suites, unchanged counts.
   - **1a (the router) is BUILT — 2026-08-14.** `SqlServerScanRoute.cs` (a partial half of
     `SqlServerCatalog` — ⚠ note `SqlServerBackend.cs` holds TWO classes and the routing lives on the
     CATALOG one), rules transcribed with their measured whys, `ExecuteQuery` 115 lines → a `RouteScan`
     call, and the Debug line gained `route=<reason>` (verified live: `route=pooled`, `route=pin (MARS)`).
   - **1b (incremental) is BUILT — 2026-08-14.** `DeltaTxnScope` merges `SnapshotPinning` +
     `DeltaTableCache` into one per-transaction object behind static accessors (both statics DELETED); one
     commit/rollback `Release` replaces the paired calls. Two subtleties the merge had to preserve, both
     found by transcription discipline rather than tests: the pin INSTANT is captured lazily at the first
     `PinVersion` (a scope created by a table publish must not move the point-in-time, nor put a clock read
     on a path that had none), and **the two releases are DIFFERENT — `InvalidateTables` (the mutating entry
     points) keeps the PINS**, because the pin is the repeatable-read contract. Gates: hermetic
     **69/69 — 7152 identical** + service tier.
   - **⚠ THE RELEASE ASYMMETRY IS DEFENSIVE, NOT GATED — the collapse-both mutant SURVIVES, and the reason
     is a finding in its own right: THE PIN IS DOUBLE-STORED.** `PendingAppends.PinnedVersion` on the txn
     buffer (untouched by `InvalidateTables`) carries the version for every explicit-txn read and every
     buffered DML, so wherever a sequential suite could observe a re-pin, the buffer answers first; the
     exposed shape needs a concurrent commit mid-transaction. ⇒ the full ITransaction slice must UNIFY the
     pin into one owner — until then any "the pin survives X" claim has to be checked against BOTH stores.
     **CLOSED BY 4b (2026-08-14): `PinnedVersion` is now a delegating property over the scope's one pin
     store, and the identical mutant dies at verify_delta_autocommit_pin §12 — see item 4's 4b notes.**
   - **1b scoping facts kept for the full slice:** `DeltaReader` is STATIC, so the bound object must be
     PASSED into its entry points rather than resolved there (the write-spec-saga threading shape); and
     ⚠ the natural name `DeltaTransaction` COLLIDES with engineered-wood's own class — hence `DeltaTxnScope`.
2. **The `delta.*` namespace + delete kinds 8–14 and the eight C++ registrations** — breaking SQL surface;
   rewrite the consuming suites (`verify_delta_catalog_snapshots`, `verify_delta_txn_version`,
   `verify_delta_tblproperties`, `verify_delta_catalog_changes`) to the new spellings. Also banks the
   timestamp-bounds feature as C#-only.
   - **BUILT — 2026-08-14 (ABI v70).** As-built notes that differ from or sharpen the design:
     - **Kind 12 (VirtualColumns) is NOT deleted** — §1's "kinds 8–14" was imprecise: 12 is category A
       (entry materialization) and lives until slice 4's `table_info`. Deleted: 8, 9, 10, 11, 13, 14, with
       the gaps left unassigned so a stale peer's kind cannot silently alias a new one.
     - **The bump to v70 is deliberate although no signature changed**: kind REMOVAL is the inverse of the
       additive no-bump rule — a stale loadable would send kind 8 and get the provider's empty-table
       fallback, i.e. silently wrong rather than loudly mismatched.
     - **The function classes are SHARED and delegate-parameterized** (`Fabricator.Bridge/DeltaFunctions.cs`),
       so DeltaRs declares `delta.snapshots`/`delta.changes` from the same six classes — which forced them
       `public` (DeltaRs is a separate assembly; no InternalsVisibleTo). DeltaRs gained function hosting for
       the first time (a `CatalogFunctionSet` + three ABI members off it).
     - **Two binding shapes, and the split is the §2.1 side-effect rule made concrete**
       (`StreamTableBinding`): FIXED schema + execution-deferred core for anything side-effecting or
       fixed-shape (both `set_*`, `tblproperties`, `get_transaction_version` — matching the old C++'s
       deliberate no-probe binds), and a bind-time PEEKED stream only for the two whose output schema
       depends on the table (`snapshots`, `changes` — the cost the old schema probe already paid).
     - **⚠ The bounds are `starting_version`/`ending_version`/`starting_timestamp`/`ending_timestamp`, NOT
       `from`/`to`** — both reserved words; a named parameter that is one is a PARSER error (caught before
       shipping; the doc's own §2.1 first draft had it wrong).
     - Timestamp bounds resolve in `DeltaReader.GetChangesBounded` — deliberately NOT on
       `ResolveVersionAsOf`, whose fall-back-to-latest is right for snapshot PINNING and silently wrong for
       a feed bound. Out-of-history bounds yield an EMPTY feed, never an error.
     - The `delta` schema is advertised by `CatalogSchemaNames` on EVERY Delta attach (vs `fabric`'s
       OneLake gate) and refused for DDL by the same `RejectFunctionSchemaDdl`; the fixed row schemas are
       single instances shared by declaration and batches (`AppTxnSchema`/`PropertiesSchema`) so they
       cannot drift.
     - Gates: hermetic tier (all 22 consuming suites rewritten; `verify_delta_catalog_changes` +16 for the
       timestamp bounds incl. the version-equivalence assertion, `verify_delta_catalog_functions` §8 +17
       for the namespace advertisement / declarations / DDL refusal) + service tier.
3. **`open_catalog` capability JSON** — kills the ServerInfo grep; small ABI bump.
   - **BUILT — 2026-08-14 (ABI v71), with ONE deviation from this doc's letter: the JSON does NOT ride
     `open_catalog`'s result — it is a dedicated appended entry `get_capabilities(handle, out_json, err)`,
     called from `LoadCatalog` exactly where the two greps sat.** The letter would have forced SQL Server
     to CONNECT inside `open_catalog` (it cannot answer `is_binary_collation` without detecting the
     collation), breaking a MEASURED invariant the mutant note in `fabricator_storage.cpp` depends on
     (open_catalog is connection-free; the settings/opener ambients are established only by the calls
     after it). At `LoadCatalog` the ambients are up and the first connection was always paid on this
     path — the old `FetchBinaryCollation` triggered the identical profile detection. Same substance
     (one typed doc, read once at ATTACH, grep dead), safer carrier. The kind-12 imprecision in slice 2
     and this are the same lesson: re-derive the design's letter against the tree before building it.
   - Contract: ONE flat JSON object of booleans; an ABSENT key means false, so a provider emits only what
     it can assert and the `IBackendCatalog.CapabilitiesJson` DIM (`"{}"`) is the correct answer for
     DAX/DeltaRs/Stub with no per-provider code. SqlServer answers `is_binary_collation` from
     `Profile.IsBinaryCollation`, Delta answers `exact_filter_pushdown` from `_pushdownMode == Exact` —
     each the SAME field its diagnostic ServerInfo rows read, so the two surfaces cannot drift.
   - Kind 7 STAYS, diagnostic-only: `fabricator_server_info()` keeps its (property, value) rows untouched
     (`verify_server_profile` still pins all 15), and the host never reads them again. C++ deleted:
     `FetchBinaryCollation` + `FetchExactFilterPushdown` (the two extra kind-7 stream reads per ATTACH);
     added: `FetchCapabilities` → `FabricatorCapabilities{string_order_pushable, exact_filter_pushdown}`,
     best-effort at the call site as before (a failed crossing leaves both off).
   - Gates: no new suite — the capability EFFECTS were already pinned and now run through the new path:
     `verify_delta_catalog_dynamic_filter` (the pushed join filter must appear in the per-file
     `read_parquet` SQL via duckdb_logs) and `verify_collation_pushdown` (the pushed `TOP + ORDER BY
     [name]` must appear in `dm_exec_query_stats`). **Mutation-tested**: forcing Delta's doc to `{}` dies
     at exactly the dynamic-filter log assertion (line 66) after the 18 correctness assertions pass —
     correctness is indistinguishable either way, which is why the mechanism assertion is the gate.
4. **The `table_*` session** — the big one: `ITable` in Abstractions, four providers, the C++ entry/DML
   operators re-pointed at table handles, `get_metadata` deleted. One ABI bump for the whole slice.
   - **SUB-SLICED 2026-08-14 (scoped against the tree, not the sketch), each green + committed before the
     next. §2.3's "C#-only and behaviour-preserving initially" is the through-line: 4a–4c change no ABI.**
     - **4a — `ITransaction` + the host-side manager, SQL SERVER ONLY (C#-only, behaviour-preserving).
       BUILT — 2026-08-14.** As-built: both types live in `Fabricator.Abstractions` (namespace
       `Fabricator.Bridge`, like the rest of the contract assembly — `ProviderSettingsStore` set the
       precedent for mechanism classes there); `Complete` nulls its fields after disposal so the
       teardown `Drain` sweep cannot double-commit; the manager's factory rides its constructor so
       `GetOrCreate(id)` stays one argument. Original scope, all of it as planned:
       `ITransaction { Id, IsExplicit, Complete(commit), Dispose }` in Abstractions; a small
       `TransactionManager<T>` in the Bridge (txnId → T; GetOrCreate/TryGet/Remove/Drain);
       `TxnState` → `SqlServerTransaction : ITransaction`, ABSORBING `_explicitTxns` (its 7 sites become
       an `IsExplicit` field read — one dictionary instead of two, and the explicit mark finally lives ON
       the transaction). Lazy-creation semantics preserved exactly: autocommit statements still allocate
       NOTHING (Begin creates the object only for an explicit txn; the first write/read-pin still
       GetOrCreates). Delta is deliberately NOT converted here — see 4b's reason. Gates: the routing
       suites (verify_read_isolation/mars_dynamic/mars_off_same_catalog/read_write_same_catalog) at
       identical counts; both tiers identical.
     - **4b — Delta's conversion + the read-scope threading. BUILT — 2026-08-14 (C#-only,
       behaviour-preserving; both tiers identical + verify_delta_autocommit_pin 67 → 75).** As built:
       `DeltaTransaction : ITransaction` (new file) owns the (path → `PendingAppends`) map, the
       explicit-BEGIN mark, AND a per-transaction `DeltaTxnScope` INSTANCE (pins + open-table reuse — the
       static registry is GONE, its 4096 panic-clear bound with it); `DeltaCatalog` holds a
       `TransactionManager<DeltaTransaction>` like SqlServer's. `DeltaTxnBuffer` shrank to the
       `PendingAppends` shape + the static stream helpers (and gained `DisposeHeld`, moved from the
       catalog so `DeltaTransaction.Complete` and the teardown `Drain` sweep can reach it —
       `DeltaCatalog.Dispose` now sweeps, where a detach with a transaction in flight used to leak into
       the static registry).
       - **The THREADING, as predicted (the write-spec-saga shape):** the five `DeltaReader` read entry
         points (`GetSchema`/`GetSchemaAndVersion`/`GetSchemaAndRowTracking`/`GetSchemaAt`/
         `ListNativeScanFiles`) and `DeltaNativeReader.Read` take an optional `DeltaTxnScope? scope`;
         the catalog passes `ReadScope()` (GetOrCreate on the ambient id — the old registry's `For()`
         creation semantics, without which an autocommit schema open would never cache and the
         195-of-291-seconds shape would return). ⚠ Defaulted null ON PURPOSE for the catalog-less
         callers (`DeltaGlobalTableFunction`, `ExternalTableRouting`'s schema probe): they open
         untracked and dispose their own — which also FIXES a leak, since the old registry cached their
         opens under an ambient id no catalog ever released. Cost: a global-function statement no longer
         shares one open across its bind/execute crossings (bind ran with txn 0 and never cached anyway,
         so the loss is marginal).
       - **THE PIN UNIFICATION, one owner without rewriting 25 sites:** `PendingAppends.PinnedVersion`
         became a DELEGATING PROPERTY over `Owner.Scope` (get = `TryGetPinned(Path)`, set = first-wins
         `SetPinIfAbsent` — sound because every historical assignment was `??=`). The entry carries
         `Owner` + a mutable `Path`; `DeltaTransaction.RenameTable` re-keys BOTH the entry and the pin
         (`Scope.RenamePin`) — without the pin re-key the dbt tmp-swap's flush would lose its rebase
         base. Post-`Remove` flush code keeps working because the property reads the REMOVED (alive)
         object's scope, not the manager.
       - **CommitTransaction's three-step ordering dance is GONE**: one `_txns.Remove` drops pins +
         tables + buffers atomically, and `wasExplicit` is read off the removed OBJECT — the
         read-state-after-remove hazard is unrepresentable, which is what ITransaction is for.
       - **The releases' asymmetry (InvalidateTables keeps pins) is now GATED** —
         verify_delta_autocommit_pin §12: explicit txn, scan, DROP of an unrelated table, re-scan ⇒
         exactly ONE `delta % pin` line for the table. **Mutation-tested: the exact mutant that SURVIVED
         the whole tier in 1b (pins cleared with the tables) now dies at §12's count after 72 pass.**
         Sequentially the VALUE cannot discriminate (a re-pin lands on the same version); the line count
         is the property, the suite's own long-standing pattern.
     - **⚠ THE CROSS-CATALOG HAZARD: FIXED STRUCTURALLY, AND ITS RECORDED SHAPE WAS WRONG — corrected
       2026-08-14 while building 4b.** The entry below claimed the exposure was "an external-table INSERT
       inside an explicit transaction". **That shape is UNREACHABLE through SQL**: slice 4a's
       `IsExplicitTxn` guards REFUSE every external-table storage write inside an explicit transaction
       (`ExternalTableInsert`, `GuardExternalDml`, CETAS-with-location — three throw sites, read in the
       code, not reasoned). What the static registry actually exposed was narrower: (a) the AUTOCOMMIT
       shape — the transient catalog's `Release` dropped the STATEMENT's pins mid-statement, but by then
       the source scans have finished and nothing re-reads, so no wrong answer was constructible; and
       (b) cross-catalog OPEN-TABLE sharing by path (a transient catalog reusing an attached catalog's
       open — harmless, since neither disposes). ⇒ the fix is real (per-catalog state makes the whole
       class unrepresentable — a transient `CommitTransaction()` now touches only its OWN manager) but
       there is NO old-vs-new behavioural kill to gate; the gate that carries 4b is the pin-unification
       one above. **Lesson, same as slice 3's open_catalog deviation: a hazard write-up must name the
       GUARDS between the mechanism and the user before claiming a user-visible exposure.**
     - **4c — `ITableDefinition`/`ITable.Bind(txn, at?)` in C#. BUILT — 2026-08-14 (C#-only,
       behaviour-preserving; both tiers identical).** The §2.2/§2.3 object model, with the letter
       re-derived against the tree (the standing lesson) in three places:
       - **No `TableInfo` bag — the info members are LAZY METHODS on `ITable`** (`RowIdColumns()`,
         `VirtualColumns()`, `ApproximateRowCount()`, `ColumnNdv()`). The kinds are SEPARATE crossings in
         the current transport, so an eagerly-computed Info object would MOVE cost between them (kind 3
         paying for the stats queries), and the lazy alternative — a bag of `Func<>`s — is a worse spelling
         of methods. 4d's `table_info` JSON composes from the calls when the host asks once.
       - **Definitions are TRANSIENT and stateless in this transport** — one per metadata/scan crossing,
         nothing cached on them (a definition cache would ADD staleness surface that does not exist today).
         4d gives them the C++ entry's lifetime, which is when caching there becomes sound. The Delta
         definition's `Bind(txn, null)` memoizes on the transaction — `DeltaTransaction.GetOrCreate` IS the
         memoization; SqlServer's binds are always fresh (nothing worth memoizing — the interface permits,
         not demands). An AT bind is always a fresh caller-owned instance.
       - **Schema resolution is deliberately UN-MEMOIZED on the binding** (stated on the interface): it
         consults the transaction's pending CREATE/ALTER shape first, then storage via the shared open —
         memoizing would serve a same-transaction mutation a stale shape.
       - **Delta as built**: `PendingAppends` MOVED verbatim out of `DeltaTxnBuffer` and became
         `DeltaBoundTable : ITable`, absorbing the PIN (4b's delegating property collapsed into a plain
         field — one store, no delegation) and the shared OPEN (a field + `TryGetOpen`/`PublishOpen`/
         `DropOpen`). `DeltaTxnScope` is DELETED: the pin instant and the open-retention cap
         (`MaxOpenTablesPerTxn` = 32, still decline-don't-evict) moved to `DeltaTransaction`, and
         `RenamePin` died STRUCTURALLY — the pin rides the re-keyed object. `DeltaTxnBuffer` is now only
         the static stream/disposal helpers. The reader threading retyped:
         `DeltaTxnScope? scope` → `DeltaBoundTable? bound` on the five read entry points +
         `DeltaNativeReader.Read`; a transient binding declines retention itself (`CanRetainOpen`) and
         behaves exactly like null.
       - **The per-transaction map now holds READ-ONLY bindings** (every read's `ReadBound(path)` creates
         one where the old scope kept them in side maps) ⇒ the commit/rollback loops skip footprint-less
         entries (`HasTxnFootprint` — deliberately GENEROUS, naming every pre-4c field incl. the
         `Serializable`/`CdfEnabled` probe caches, so only entries the read cache alone created can skip;
         keeps the rollback log line meaning "a table this transaction touched with WORK").
       - **⚠ THE dbt TMP-SWAP CAUGHT THREE DEFECTS IN THE FIRST FULL GATE RUN
         (`verify_delta_catalog_transactions` §38) — all three are consequences of unifying the read cache
         into the buffer map, and none was visible to the compiler:**
         1. The created-table RENAME re-key COLLIDES with the departed table's read-cache entry at the
            target path (the swap READS the target name earlier in the same transaction — its entry
            materialization binds it). Fix: `RenameTable` EVICTS a footprint-less target — its pin/open
            describe the table just renamed AWAY from the path — and still refuses (buffer restored) when
            the target has pending work.
         2. The moved binding's own shared OPEN was opened over the OLD path's filesystem — carrying it to
            the new key serves reads "No Delta table found". 4b never re-keyed the open map (the open was
            silently orphaned there); `RenameTable` now drops it explicitly. The PIN stays: a version
            number resolved against the log that now lives AT the new path.
         3. `SetNames` was FIRST-WINS, so the post-swap scan of `m` ran as `m__dbt_tmp` — the ABI passed
            the right name and the binding re-derived the wrong one, opening a folder the swap had renamed
            away. Overwrite is consistent by construction (Bind derives the path FROM the names it sets).
            **Found by instrumenting, not reasoning**: one Debug fablog showed `delta scan main.m__dbt_tmp`
            for a query naming `m` — the third speculation-free diagnosis this migration owes to a log line.
       - **SqlServer as built**: typed cores (`ColumnsSchemaCore`/`RowIdColumnsCore`/`RowCountCore`/
         `ColumnNdvCore`) in a new `SqlServerTable.cs` partial; the definition/bound-table classes are
         NESTED in the catalog because `SqlServerTransaction` is a private nested class. Kinds 2/3/4/5
         re-encode the typed answers (`NameStream`/`RowCountStream`/`NdvStream`) — the substitution of a
         live SQL stream by a rebuilt string table is exactly what `SchemasMetadata`/`FilteredTables`
         already do, so both shapes were shipping before. The warehouse stats gating moved INSIDE the
         typed cores: the never-issue-a-swallowable-statement rule now lives on the ANSWER (null/empty),
         not on the transport arm. Kind 2 materializes the schema and releases the zero-row probe's
         connection immediately (was: held until the host released the stream) — invisible from SQL.
       - **`_rowTrackingByPath` dissolved into a per-(txn, table) `RowTracking` field on the binding.** The
         catalog-wide cache was NEVER invalidated — a `delta.enableRowTracking` change made it silently
         stale for the catalog's lifetime. Per-transaction re-resolution is normally free (kind 2 fills it
         through the same binding kind 12 reads, and a fresh transaction's resolve rides the shared open).
       - The kind-2 ABSENCE contract (§3 item 2) moved onto the typed members unchanged: Delta's
         `DeltaBoundTable.Schema` establishes no-commit-in-log, SqlServer's `ColumnsSchemaCore` classifies
         error 208 — both still `ObjectNotFoundException`.
       - DAX/DeltaRs deliberately untouched: `IBackendCatalog.GetTable` is a throwing DIM until their
         conversion (they stay on plain `GetMetadata` arms, which 4c does not change for them).
       - Gates: `verify_delta_catalog_transactions` 1040 (the suite that caught all three swap defects),
         pin 75 / rename 27 / txn_version 65 / row_level_concurrency 93 / time_travel 98 / alter 116 /
         row_tracking_virtual 299 / column_mapping 251 all identical; hermetic + service tiers identical
         to baseline (the behaviour-preservation claim).
     - **4d — the `table_*` ABI session + DELETE `get_metadata` + `scan_table`. BUILT — 2026-08-15
       (ABI v72, breaking, no aliases — the one C++-touching bump, taken last as planned, when the C#
       objects it transports already existed and were gated).** `get_metadata`, `scan_table` and the
       `FabricatorMetadataKind` enum are GONE from the vtable (mid-struct removal, the v30/v31/v47
       precedent — the version check makes a stale pair loud); managed, `IBackendCatalog.GetMetadata` and
       the C# `MetadataKind` are gone with them. As built, with the letter re-derived against the tree in
       FOUR places (the standing lesson, applied a fourth time):
       - **`table_open` is LAZY and absence stays at the first read.** §2.4's sketch put NOT_FOUND on
         `table_open`; §6's lazy-bind option ("the handle is the DEFINITION, each call resolves the
         ambient txn's binding") is what was built, and it decides the rest: the handle wraps
         (definition, AT) with NO IO and NO probe — cheap opening is load-bearing because enumeration
         materializes every table — so absence classifies at `table_schema`, exactly where the old kind-2
         classified it (same contract, same call count). It also makes the handle's LIFETIME trivial: a
         definition is stateless (4c), so an entry keeping its handle through the retire-don't-destroy
         graveyard cannot serve anything stale — staleness is governed by the binding layer, which
         per-transaction invalidation already owns. The new `IBackendCatalog.ResolveTransaction(txnId)`
         DIM is the one question the session asks per call, and its per-provider semantics are the
         providers' own ambient rules (SqlServer TryGet — lazy creation preserved; Delta GetOrCreate —
         without it an autocommit schema open would never share its pin/open and the
         195-of-291-seconds shape would return; DAX/DeltaRs/Stub null).
       - **JSON after all — `table_info`/`table_stats` are the design's ONE-typed-doc letter, via OUR OWN
         vcpkg yyjson (v73, same day). ⚠ The v72 intermediate shipped them as Arrow streams on a
         justification that did NOT survive review, and the correction is worth recording verbatim.** The
         recorded reason was "yyjson is vendored in duckdb_static but not `DUCKDB_API`-exported, so a
         loadable cannot link it" — TRUE of the VENDORED copy and an overclaim about JSON: the user
         pointed out vcpkg, and the extension simply carries its own yyjson (plain C `yyjson_*` symbols;
         DuckDB's copy is C++-namespaced `duckdb_yyjson`, so the static build gets no clash either). What
         the impossibility claim actually was: a dependency-cost choice wearing a link-surface argument.
         As built (ABI v73): `table_info` = `{"rowid":[...],"virtual":[{"name":..,"type":..}]}`,
         `table_stats` = `{"row_count":N,"ndv":{...}}` (absent row_count = unknown; typed JSON numbers
         where kinds 4/5 crossed text), both `char**` owned-UTF-8 crossings on the `get_capabilities`
         convention, written with `Utf8JsonWriter` (proper escaping for user-controlled identifiers) and
         parsed with yyjson. **The rework's biggest win is elsewhere: `ReadCapabilityFlag` — the
         `get_capabilities` string-find hack, safe only by a producer-side bare-booleans argument — is
         RETIRED for real parsing**, removing the caveat class rather than the instance. Cost accepted:
         `yyjson` joins the vcpkg install line — ONE line, because all three build tiers share
         `.github/actions/build-extension` — plus the quickstart/WSL docs; a platform missing it fails at
         `find_package(yyjson CONFIG REQUIRED)`, loudly, at configure.
       - **Stats are a SEPARATE lazy entry, not part of the info doc** — bundling them would move the
         stats queries onto entry materialization, i.e. the enumeration path (§3 items 5/7). Entry
         materialization = open + schema + info (2 IO crossings, was 3); stats = 1 at first scan (was 2),
         one `stats_fetched_` flag on the entry filling row count + NDV together.
         - **⚠ THAT LAZINESS IS WHAT MADE THE DELTA ROW COUNT AFFORDABLE — filled in 2026-08-17, and until
           then the Delta provider returned NULL for it.** Its comment justified the null with "nothing
           consumes it yet and enumeration must stay cheap — §3 item 5", and **both halves were wrong**:
           the consumer had existed all along (`FabricatorScanCardinality` → `NodeStatistics`, which the
           SQL Server provider had been feeding since the callback was written, so every Delta table was
           planned with NO cardinality at all), and item 5 is about ENUMERATION, which never asks for
           stats — this entry is precisely what keeps it off that path. It now sums `numRecords` per
           active file minus each deletion vector's stated cardinality, so it reads no data file and no DV
           file, and it is EXACT rather than approximate because the log is the authority on live rows.
           NDV stays empty and that IS a genuine absence: a Delta `add` records min/max/nullCount per
           column but no distinct count. Gate `verify_delta_statistics` (27), two mutants.
           **The reusable lesson: a stub whose comment says "nothing consumes this yet" is a claim about a
           CONSUMER, and it ages the moment someone wires one — check the consumer, not the comment.**
       - **`table_schema` keeps the zero-row-stream carrier** (the design mocked it as "trickery"):
         PopulateReturnSchema is the ONE proven import path incl. VARIANT extension types, and a bare
         ArrowSchema would fork the type conversion for zero gain. The AT clause is part of the handle's
         identity (the C++ AT entries' own-map fact, object-model form), so an AT handle's schema is the
         provider's as-of describe — Delta's `GetSchemaAt` (the SAME call `ScanCore`'s schema-only probe
         makes, so the entry's ColumnList and the scan's return schema still come from one resolution —
         the §1.x contract); SqlServer's definition deliberately ignores it (4c's recorded decision:
         box/Azure temporal history keeps the current shape, Fabric refuses time travel across DDL — the
         refusal now surfaces at the scan's own schema probe in the same bind, one call later). The
         bind-time schema PROBE stays on the SCAN path untouched (`table_scan` with the schema-only
         spec) — the pin-seeding/native-vs-codec note in `fabricator_table_entry.cpp` demands it.
       - **Catalog discovery**: five dedicated entries (`catalog_schemas`/`catalog_tables`/
         `catalog_functions`/`catalog_macros`/`catalog_server_info`), each keeping its old kind's column
         layout; every provider now implements all five with DECLARED shapes, so the per-provider
         unknown-kind fallbacks (the 1-column empty table behind the `ReadStringTable` OOB hazard) are
         gone. `catalog_server_info` stays diagnostic-only (the host consumes v71's `get_capabilities`).
       - **DAX / DeltaRs / Stub gained thin `ITable` implementations** (read-only: schema + scan, empty
         rowid/virtual/stats) — each an afternoon's worth, which was the design's stated test. ⚠ DeltaRs
         needed a `using DrsTable = DeltaLake.Interfaces.ITable;` alias — delta-dotnet's own per-table
         handle collides by NAME with the object model's `ITable`.
       - **`IBackendCatalog.ScanTable` deliberately SURVIVES as a C#-internal member** (the ABI entry
         died): it is the one-line ambient-bind convenience the external-table DML routing's
         identity-resolution scan needs, and each provider's implementation is an adapter over the
         object model. What 4d did NOT do — deliberately, and recorded rather than implied: the DML
         entries (`execute_delete`/`execute_update`/`begin_bulk`/`alter_table`/`create_table`…) keep
         their name-pair transport. §2.4's sketch lists `table_delete`/`table_update`/`table_alter`;
         nothing forces them (they never rode `get_metadata`, and the C# side already resolves per-txn
         state ambiently), so re-pointing them at table handles is follow-on work with its own risk
         budget, not part of retiring the multiplexer.
       - Gates: hermetic **69/69 — 7193** and service **50/50 — 2028**, both IDENTICAL to the 4c baseline
         (the behaviour-preservation claim — the transport changed, no answer may move); smoke incl. a
         rowid UPDATE and `AT (VERSION => 1)` through the new session. DeltaRs + DAX compile-verified
         (their suites are outside CI / manual, as always).
5. Sweep: delete `ReadStringTable`'s multi-column string protocol where nothing uses it any more.
   - **DONE (2026-08-15) — and the deletion component turned out to be ZERO, so the sweep is guard/comment
     hygiene rather than code removal.** Re-derived against the post-v73 tree: `ReadStringTable` has SEVEN
     consumers and every one is legitimate — the four discovery lists (`DiscoverSchemas` 1 col /
     `DiscoverTables` 3 / `DiscoverFunctions` 3 of 5 / `DiscoverCatalogMacros` 3) plus three load-time
     Bridge streams that never rode the multiplexer at all (`ListSettings` 6, `ListSecretFields` 5,
     `ListGlobalFunctions` 4 of 6). Two of those are DELIBERATELY wider than the host reads, which is why
     the width check is `>=`, not `==`.
   - **What WAS dead: the width guard's justification, not the guard.** The per-batch check's comment cited
     the deleted unknown-kind fallback arms verbatim ("the Delta/DAX catalogs' `_ =>` arm is a ONE-column
     empty table … a Delta catalog answers with 1 and no rows") — since 4d every provider implements all
     five list entries with DECLARED full-width shapes even when empty (verified across SqlServer / Delta /
     DAX / DeltaRs / Stub: Macros is 3 columns everywhere, ServerInfo 2, Functions 5). Rewritten to the
     current truth: the check is the OOB protection for a mis-shaped provider batch; the zero-row leniency
     is no longer exercised in-tree.
   - **The zero-row leniency STAYS, deliberately — tightening it would be an UNTESTABLE behaviour change.**
     No in-tree producer can trip a schema-width check any more, so no gate could distinguish tightened
     from not; the only party it could ever affect is an out-of-tree plugin backend answering an optional
     surface (macros) with a minimal empty stream, and failing that ATTACH over rows that do not exist buys
     nothing. Same verdict as the macros pass recorded ("that leniency is load-bearing"), now for the
     narrower reason that survives v72.
   - Also swept: the two "their own metadata kind" phrasings in `fabricator_catalog.cpp` (now "their own
     dedicated entry (catalog_macros)") and three pre-provider-era "SQL Server" doc comments on the
     provider-generic discovery helpers in `fabricator_metadata.hpp`. The `get_metadata` references left in
     `abi.h`/`clr_host.cpp` are deliberate historical tombstones, not staleness.
   - **Comment-only by proof, not by belief**: `git diff -U0 -- src/` filtered to changed lines that are
     not `//` comments returns EMPTY, so no gate can move — the masking check, plus a full rebuild and the
     hermetic tier at identical counts anyway (the standing C++-change gate).

### The `ITable`/`ITableBinding` rename (2026-08-15, user-directed — C#-only, no ABI, breaking for plugin authors)

The object model shipped with the short name on the WRONG half: `ITableDefinition` → `ITable`(bound),
although `ITable.cs`'s own doc claimed "the deliberate `ITableFunction` symmetry" and the user's original
framing coined "an ITable" for the `ITableFunction`-analog — the DEFINITION. Renamed to complete the
symmetry the doc asserted:

| role | table functions | tables (before) | tables (now) |
|---|---|---|---|
| shared definition | `ITableFunction` | `ITableDefinition` | **`ITable`** |
| bound object | `ITableFunctionBinding` | `ITable` | **`ITableBinding`** |

Concrete classes rode along (`DeltaBoundTable` → `DeltaTableBinding` + its file, same for
SqlServer/DAX/DeltaRs/Stub) — leaving `*BoundTable` classes implementing `ITableBinding` would have
reproduced the half-applied-convention state the `IArrow*` sweep existed to fix. The `*TableDefinition`
concrete names deliberately STAY (`DeltaTable : ITable` would collide with engineered-wood's `DeltaTable`).
`IBoundTable` was deliberately NOT used for the bound half — that name existed until the `tablefn_*` rename
(it became `IBoundTableFunction`), and resurrecting it for a different concept would poison history greps.
⚠ `DeltaRsCatalog.cs` was HAND-edited, excluded from the mechanical pass: it contains
`DeltaLake.Interfaces.ITable` (delta-dotnet's OWN type), which a word-boundary substitution would have
corrupted; the `DrsTable` alias + our fully-qualified `Fabricator.Bridge.ITable` (now the definition) stay.
Mechanical-change proof: the diff outside that one file, with the three rename rules applied to its removed
lines, is byte-identical to its added lines. §5 items above this note predate the rename and keep the old
names — they are as-built records of their own moment.

**Settled in the same pass (user question): `Bind(txn, at?)` must NOT take a scan spec / filter values,
and the reason is DuckDB's own timeline, read from source rather than recalled.** (1) At BIND
(`duckdb/src/planner/binder/tableref/bind_basetableref.cpp:160-268`) the `LogicalGet` is constructed with
ALL columns and NO filters — the WHERE clause has not even been resolved against the table reference yet,
so there is nothing to pass. (2) STATIC filters + projection arrive at OPTIMIZATION
(`optimizer/pushdown/pushdown_get.cpp:61-70`, `pushdown_complex_filter` mutating the bind data — where our
C++ already stashes them — plus RemoveUnusedColumns). (3) DYNAMIC/JOIN filters arrive only at EXECUTION:
the optimizer attaches an EMPTY `DynamicTableFilterSet` (`join_filter_pushdown_optimizer.cpp:262-267`) that
the hash join's build side fills at runtime, merged into the final filter set at scan GLOBAL-INIT
(`physical_table_scan.cpp:35-36`). Our `table_scan` crossing fires from `ArrowStreamInitGlobal` — stage
(3) — so `spec_json`+`filter_values` already carry the complete spec at the EARLIEST moment it exists
anywhere. And structurally the binding is memoized per (transaction × table), serving N scans with
DIFFERENT specs (a self-join is one binding, two scans, two filter sets; a prepared statement re-executes
one plan with fresh dynamic filters each run) — the interface's "BIND IS STATE, SCAN IS REQUEST" remark is
the contract. A provider that wants to act on the spec "at bind" already does: `Scan(spec, filterValues)`
on the binding IS the per-scan bind, and Delta prunes its file list there today. The one genuinely earlier
stage a provider could ever exploit is (2)'s static filters at plan time — the only plausible use is
filter-aware CARDINALITY, which would be an extension of the `table_stats` crossing, not of `Bind`; noted
so the option is not lost, nothing motivates it today.

## 6. Honest costs and open questions

- **Scale**: slice 4 touches every provider, every DML operator, the entry materialization, and the
  graveyard. It is the largest refactor since the rename. Slices 1–3 are each days, not weeks, and stand alone.
- **Per-txn vs per-entry table state**: RESOLVED by §2.3 (this bullet's first draft said "keyed by ambient
  txn inside the table object", which was the nested-dictionary status quo wearing the new name). The
  definition is per-entry and txn-free; the bound `ITable` is per-(entry × txn) and OWNED by the
  `ITransaction`, so release is disposal, not a sweep.
- **C++ table-handle identity under the split**: the C++ entry is shared across transactions while the bound
  `ITable` is not. Either the `table_*` session binds lazily (the handle is the DEFINITION, and each call
  resolves the ambient txn's binding — ambient stays the transport), or `table_open` takes a txn handle and
  the entry holds one C# handle per live txn. The first keeps the ABI slice small and is the default; the
  second is the "reify txn handles" follow-on and should only happen if the ambient resolution measurably
  shows up.
- **DAX/DeltaRs**: both implement `GetMetadata` minimally; the `ITable` surface must stay implementable in
  an afternoon for a read-only provider (default-refuse capability interfaces do that).
- **What deliberately does NOT move**: `fabricator_query`/`fabricator_exec`/`fabricator_refresh_cache`/
  `fabricator_version`/`fabricator_managed_dir`, the storage/secret/COPY/settings registrations — provider-
  agnostic host surface, correctly in C++.
