# The catalog/table abstraction — design for retiring `get_metadata` (slices 1a/1b/2 BUILT, 3–5 open)

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
| 7 ServerInfo | capability profile | **(property, value) strings, string-matched on BOTH sides** (`FetchExactFilterPushdown` greps for `"exact_filter_pushdown"`, `FetchBinaryCollation` for `"is_binary_collation"`) |
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
4. **The `table_*` session** — the big one: `ITable` in Abstractions, four providers, the C++ entry/DML
   operators re-pointed at table handles, `get_metadata` deleted. One ABI bump for the whole slice.
5. Sweep: delete `ReadStringTable`'s multi-column string protocol where nothing uses it any more.

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
