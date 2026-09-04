#!/usr/bin/env bash
#
# Runs a TIER of suites, and fails loudly unless every selected suite actually ran.
#
#   run-suites.sh hermetic   (default) needs nothing but a scratch dir and the in-repo fixtures
#   run-suites.sh service              needs the docker/docker-compose.yml stack: SQL Server + MinIO
#
# One runner for both so the assertions below Ã¢ÂÂ the part with the actual value Ã¢ÂÂ exist once. Used by
# CI and usable as-is on a dev machine.
#
# ONE PROCESS PER SUITE, with a FRESH scratch directory each. Both halves are load-bearing, and
# were established by watching the alternative fail:
#
#   * Batching the whole set into one `unittest -f <list>` invocation SIGSEGVs partway through
#     (observed at suite 41/53, in Apache.Arrow's ImportedArrowArrayStream finalizer during
#     GC.RunFinalizers) and reports spurious failures. One CLR is booted for the process, so state
#     and finalizers from earlier suites outlive them and run during later ones. Every suite passes
#     in isolation.
#   * Sharing one scratch directory across suites makes them collide on table paths; CLAUDE.md's
#     documented recipe gives each file its own.
#
# The per-suite cost is a CLR boot (~1-2s), which is the price of isolation.
#
# ASSERTIONS THIS MAKES, beyond a zero exit status Ã¢ÂÂ none of which `unittest` gives you for free:
#   * every selected suite reports "All tests passed"
#   * NOTHING skips. A skip here means an environment variable CI thought it set is missing, or a
#     `require`d extension is not linked; either way the suite silently tested nothing.
#   * the runner never says "No tests ran". A filter that matches no test case exits ZERO, so a
#     mistyped suite path would otherwise be a permanently green no-op.
#
# Environment:
#   UNITTEST   path to the unittest binary   (default: build/release/test/unittest[.exe])
#   MANAGED    path to the published bridge  (default: build/release/extension/fabricator/fabricator)
set -uo pipefail   # deliberately NOT -e: collect every failure, then report them together
cd "$(dirname "$0")/.."

TIER=${1:-hermetic}
case "$TIER" in
    hermetic)
        SELECT_CMD=scripts/list-hermetic-suites.sh
        # Floors measured 2026-07-25: 53 suites / 4152 assertions, all green.
        # 54 / 4199 since 2026-07-29: verify_delta_autocommit_pin (34) pins that an autocommit CODEC statement
        # establishes ONE snapshot pin per (statement, table) however many times it names the table. Raised in
        # the same commit, per the error text below. Note 4165 - 4152 = 13 of the gap predates this: the floor
        # had not been raised for verify_delta_catalog_variant's +13 (the CAST(NULL AS VARIANT) backfill fix).
        # 4197 since 2026-07-29: verify_delta_rename lost 2 (the redundant PROVIDER alias 'deltalake' was
        # REMOVED; its two positive assertions became one negative pin plus a no-catalog-left-behind pin).
        # A floor LOWERED for a deliberate removal, which is the one legitimate reason to lower it.
        # 4206 since 2026-07-29: verify_delta_clustered_optimize gained 9 (ÃÂ§8 pins that a clustering-declared
        # table on a catalog WITHOUT the native writer WARNS instead of silently bin-packing).
        # 4208 since 2026-07-29: verify_delta_catalog_transactions gained 2 (the ROLLBACK atomicity pin for
        # the buffered identity append the native-write default exposed).
        # 58 RUNS / 5290 since 2026-07-29, all measured: the four engine-parameterized suites each run a
        # SECOND time on the codec engine (+1065 Ã¢ÂÂ write 31, transactions 943, update 63, delete 28, the
        # counts identical to their native leg, which is the point), and verify_delta_rename gained 17
        # (10 -> 27) becoming the pin that each PROVIDER spelling selects its documented engine.
        # NOTE the floor is on RUNS, not distinct suites: 54 suites, 58 runs. A suite dropping out of the
        # tier still trips it, which is what the floor is for.
        # 59 runs / 5326 since 2026-07-29: verify_delta_subplan_dedup (36) pins that the catalog scan
        # SERIALIZES its table identity, so DuckDB's common-subplan optimizer cannot conflate two scans of
        # different same-shaped tables and silently return one table's rows for both.
        # 5357 since 2026-07-29: verify_delta_autocommit_pin 34 -> 65. Sections 9-10 pin that an explicit AT
        # stays INDEPENDENT of the shared pin (it must neither consume nor seed it) on both engines, and
        # section 11 pins the NATIVE path's pin as shared-and-free after the redundant ResolveVersionAsOf
        # open was removed.
        # 60 runs / 5459 since 2026-07-30: verify_delta_mixed_engines (102) pins CROSS-ENGINE parquet
        # compatibility Ã¢ÂÂ native_read/native_write are INDEPENDENT options, so besides the two symmetric
        # profiles the provider names select there are two MIXED ones a user can ask for. Written by one
        # engine, read back by the other, over the same table path.
        # 5471 since 2026-07-30: verify_delta_row_level_concurrency gained 12 (ÃÂ§10 pins that a row-level
        # DELETE under `serializable` still lands across a concurrent COMPACTION Ã¢ÂÂ it ABORTED before the
        # engineered-wood bump, and nothing covered it on either side).
        # 61 runs / 5521 since 2026-07-30: verify_macros_catalog (50) pins CATALOG-BOUND provider macros Ã¢ÂÂ
        # resolution of both kinds through db.schema.m(...), that the BARE name does NOT resolve (the one
        # assertion separating catalog-bound from the load-time global form), and KIND FILTERING in both
        # directions (the binder Cast<>s on the entry type without checking, so the wrong kind is an unchecked
        # bad cast rather than a clean error).
        # 5537 since 2026-07-30: verify_host_query gained 16 (15 -> 31). The table function now adopts the
        # CALLER's search path + TimeZone, so `USE s; SELECT Ã¢ÂÂ¦ FROM fabricator_host_query('Ã¢ÂÂ¦ FROM t')` resolves
        # instead of failing against the fresh connection's default memory.main. TimeZone is asserted as a
        # rendered VALUE, not just current_setting(), since the label alone would pass without reaching the
        # computation. Suite count unchanged Ã¢ÂÂ the suite already existed.
        # 62 runs / 5558 since 2026-07-30: verify_delta_catalog_functions (21) pins CATALOG-BOUND CUSTOM
        # FUNCTIONS on the Delta catalog Ã¢ÂÂ until then all seven of its function ABI members threw and the
        # FUNCTIONS metadata kind fell to a 1-column empty fallback. It also pins the ZERO-ARGUMENT shape, which
        # needs BOTH halves of a real fix: an empty parameter schema is unrepresentable in Apache.Arrow's C
        # interface (export AND import throw on 'fields'), so the host passes no args stream for an
        # argument-less function and the bridge exports the empty schema itself. A regression in either half
        # makes such a function silently VANISH rather than error, which is why it is asserted here.
        # 5564 since 2026-07-31: verify_delta_catalog_functions gained 6 (21 -> 27) for NAMED parameters on a
        # custom table function Ã¢ÂÂ both `:=` and `=>` spellings, that the argument really crosses the ABI (an
        # unknown value yields NO rows rather than all of them), that a misspelled name is a clean BINDER error
        # rather than silently ignored, and that a named parameter is NOT positionally callable.
        # 5573 since 2026-07-31: verify_global_functions gained 9 (63 -> 72) Ã¢ÂÂ the demo global fabricator_seq
        # now has a MIXED signature (positional n + named start), which is the combination that can fail
        # SILENTLY: the host marshals every declared parameter, substituting a typed NULL for an omitted named
        # one, so an off-by-one there corrupts the POSITIONAL value rather than erroring.
        # 63 suites / 5607 since 2026-07-31: NEW verify_delta_last_checkpoint (34) Ã¢ÂÂ an empty/corrupt/
        # field-less _last_checkpoint must fall back to listing _delta_log instead of failing the read.
        # Regression for a LIVE OneLake multi-writer failure (the hint file is updated by non-atomic
        # OVERWRITE, so a concurrent reader could see it at ZERO bytes and die in JsonDocument.Parse).
        # Pinned hermetically by writing the corrupt states directly Ã¢ÂÂ a live race only sometimes collides,
        # so it cannot serve as the gate.
        # 5623 since 2026-08-01: verify_delta_tblproperties 42 -> 58, for the isolation-default FLIP
        # (catalog default write_serializable -> serializable, matching Fabric Spark) and the removal of the
        # automatic create-time delta.isolationLevel stamp. The added sections pin the DEFAULT itself Ã¢ÂÂ every
        # other isolation assertion in the tree now states its level explicitly, so without this a regression
        # in the default would fail nothing Ã¢ÂÂ plus that neither level auto-stamps and that an explicit
        # WITH ("delta.isolationLevel"=...) still does.
        # 5639 since 2026-08-01: verify_delta_row_level_concurrency 82 -> 93 (ÃÂ§11). The EW bump made
        # DeltaTransaction's whole-table-read exemption an explicit opt-in
        # (ExemptRowLevelFromWholeTableRead) instead of unconditional behaviour, and NOTHING failed when it
        # was left unset Ã¢ÂÂ ÃÂ§11 is the section that fails. It drives a non-pushable DELETE (so the scan
        # declares the whole table) against a concurrent DELETE of a DIFFERENT row of a DIFFERENT file: with
        # the opt-in they compose, without it the declaration meets the concurrent commit and aborts.
        # 5640 since 2026-08-01: verify_delta_catalog_time_travel 48 -> 49. Upstream EW #36 made an
        # INCOMPLETE LOG REPLAY an error instead of a silence, which changes what AT (VERSION => n) does past
        # the end of the log: it used to return the NEWEST snapshot under the requested label, so a stale pin
        # or an off-by-one silently got real rows for a version that does not exist. Nothing pinned either
        # answer before, so the behaviour could have flipped back unnoticed in whichever direction.
        # 5654 since 2026-08-02: verify_delta_txn_version 51 -> 65, ÃÂ§9 Ã¢ÂÂ a REFUSED flush now takes back the
        # deletion vector it staged (EW #46's ledger + #49's fix). MEASURED before the change: the same shape
        # left a stray deletion_vector_*.bin forever. The section needs a delete LARGER than the 1 KB roaring
        # inline threshold, or the vector rides inside the commit json and there is no file to leak.
        # 66 runs since 2026-08-06: verify_delta_update_grouped (72) pins the UPDATE post-image GROUPED
        # FLUSH. It is the one suite the runner gives a forced env var (see the case below), because the
        # grouping's threshold is 64 MiB of Arrow data and nothing else here comes near it Ã¢ÂÂ without this
        # suite the grouped path has NO coverage at all, at either tier.
        # 68 runs since 2026-08-11: verify_setting_scope (30) pins that provider settings honour DuckDB's
        # SetScope (ABI v69) — a `SET` in one connection must not reach another. Before v69 that leak was a
        # DATA bug, not a config wart: `SET mssql_mars='false'` in one connection changed the row count a
        # CTAS in ANOTHER connection produced (10 vs 15).
        # 69 runs since 2026-08-11: verify_delta_partition_escaping (56) pins that a partition VALUE the
        # target storage cannot hold literally still round-trips AND that the data file under it still
        # OPENS. It exists because the engineered-wood bump onto upstream/main made partition-directory
        # escaping depend on what the filesystem declares it cannot hold, and nothing here covered that in
        # either direction — no partition value in any other suite contains a Win32-sensitive character.
        # 70 runs since 2026-08-17: verify_delta_ctas_ordering (60) pins that an autocommit CTAS writes its
        # DATA FILES BEFORE it touches the _delta_log, so a failed data write leaves a folder with no
        # `_delta_log` instead of an EMPTY COMMITTED TABLE behind a statement the user saw fail. Its §3 gate
        # injects the failure with `error()` mid-stream — which is what turned limitation 1.5's residue from
        # "reasoned, not measured" into measured, in both directions.
        # 71 runs since 2026-08-17: verify_delta_statistics (27) pins that the Delta catalog reports a ROW
        # COUNT to the optimizer at all. It returned null until then, so every Delta table was planned with
        # no cardinality while the consumer (FabricatorScanCardinality -> NodeStatistics) had existed all
        # along and SQL Server had been feeding it — the mutant's EXPLAIN carries no estimate line whatever.
        # 69 runs since 2026-08-18 — a DELIBERATE DECREASE, the only kind this floor accepts: the
        # fabricator_delta_mfr_scan spike was REMOVED at ABI v75, taking verify_delta_mfr_scan (36) and
        # verify_delta_mfr_dv (23) with it. It was NOT dead code — registered, deletion-vector correct and
        # green in both tiers — but absent from the README, i.e. a spike that shipped by accident. Removing
        # it also deletes the last core->Delta coupling in the C++ layer.
        # 70 runs since 2026-08-18: verify_delta_clustered_optimize gains a SECOND leg at an accumulated
        # host-query batch size (see ACCUMULATED below) — the only gate on batch size no longer dictating
        # Delta file size, which is what the BudgetedStream boundary split delivers.
        # 71 runs since 2026-08-19: verify_views_catalog — provider-declared catalog-bound VIEWS (ABI v77).
        # Hermetic on purpose although SQL Server declares one too: a view body is a LOCAL declaration on its
        # own metadata entry, never assembled into provider SQL, so it needs no server.
        # 72 runs since 2026-08-20: verify_catalog_init — the provider init hook (ABI v78). Hermetic on the
        # DELTA provider deliberately, i.e. the one whose Initialize() is a no-op DIM: that is what proves the
        # hook is wired in the HOST rather than in one provider.
        # 73 runs since 2026-08-22: verify_lateral — row-mapped (correlated LATERAL) functions, both
        # execution paths. Hermetic because the demos are GLOBAL functions: no ATTACH, no connection, so the
        # host machinery (the optimizer rewrite, the operator, the provenance contract) is provable without a
        # server. The catalog-bound half rides verify_functions in the service tier.
        # 75 since 2026-09-02: verify_plugin_fluid moved here from the service tier. It is not a NEW
        # suite -- Fluid became a BUILT-IN provider assembly rather than a bundled plugin, so it needs no
        # plugin root, and the derived lists reclassified it on their own. The service tier loses the same
        # run. Its 234 assertions now run on all THREE platforms instead of linux-only, which matters here
        # more than usual: its temporal assertions read TimeZoneInfo.Local.
        : "${MIN_SUITES:=75}"
        # 5656 since 2026-08-02: verify_delta_catalog_transactions 943 -> 944 Ã¢ÂÂ ROLLBACK now RECLAIMS the
        # data files the transaction eagerly wrote (EW #52's DiscardDataFilesAsync) instead of leaving them
        # for VACUUM. +2, not +1: that suite is one of the DOUBLED ones below, so an assertion added to it
        # counts once per engine. The section had asserted the parquet count only BEFORE the rollback for a
        # year, which is exactly why the behaviour could change under it in silence.
        # 5710 since 2026-08-04: verify_delta_catalog_write 31 -> 43 for the CREATE-over-an-existing-table
        # refusal Ã¢ÂÂ the shared C++ CreateTable never checked ERROR_ON_CONFLICT, so a plain CTAS wrote no rows
        # and kept the OLD data (exit 0, no error) and a plain CREATE silently ignored its DECLARED SCHEMA.
        # Ã¢ÂÂ  +24, not +12: verify_delta_catalog_write is one of the four DOUBLED suites, so each assertion
        # counts once per engine Ã¢ÂÂ a floor of 5668 from the standalone delta would have tolerated a
        # 12-assertion regression, the same trap the s3 note records on the service tier.
        # Ã¢ÂÂ  5686 - 5656 = 30 of this gap PREDATES the change: the floor was not raised for the unified
        # parameter protocol's coverage (verify_global_functions + the signature/mixed-arg pins). Closed here
        # rather than left, since a floor 30 below the actual silently tolerates a regression that large.
        # 65 runs / 6032 since 2026-08-05, taken from a green tier run (never computed): MERGE INTO landed, so
        # verify_merge_into (130) joins the DOUBLED list below Ã¢ÂÂ it is composed of exactly the update/delete/
        # insert paths those suites double, so a divergence must fail in one leg and name the engine. That is
        # +2 RUNS and +260 assertions; the remaining +20 is verify_delta_catalog_update 63 -> 73 (also doubled),
        # which gained the `SET col = DEFAULT` refusal. That last one is a CORRUPTION regression gate, not a
        # feature note: the operator used to read SET values by ORDINAL, and a DEFAULT contributes no
        # projection column, so it committed a shifted row (measured a=5,b=<rowid> for a correct a=99,b=5) or
        # fatally invalidated the database when the shifted types differed.
        # 6172 since 2026-08-05, from a green tier run: verify_merge_into 130 -> 200 (+70, doubled = +140).
        # The additions are the two places autocommit and an explicit transaction genuinely DIVERGE, both
        # measured: a merge carrying an UPDATE/DELETE action WORKS in autocommit on a table with deletion
        # vectors DISABLED and is REFUSED inside a transaction (the buffered path requires them Ã¢ÂÂ so reaching
        # for BEGIN to get atomicity can cost the statement, which is the opposite direction from the
        # atomicity trade-off and is pinned in BOTH directions with a positive control); and the change feed
        # of an autocommit merge is SPLIT across versions where the fused one reports a single version.
        # Plus the commitInfo.operation labels, which are an INTEROP contract: a fused merge commits as
        # TRANSACTION, never MERGE, so a consumer keying on the operation string will not match us.
        # 6190 since 2026-08-05, from a green tier run: verify_merge_into 200 -> 209 (+9, doubled = +18) when a
        # MULTI-ACTION merge became FORCED-BUFFERED. That was not a feature Ã¢ÂÂ it fixed SILENT DATA DESTRUCTION.
        # A merge's actions all address rows located by ONE join scan, and while they committed separately a
        # copy-on-write DELETE renumbered the rows a later action had already addressed: measured on a ONE-FILE
        # non-DV table, two conditional deletes left the wrong survivors and DESTROYED a row. ÃÂ§11 is that
        # regression gate (refusal + table intact + a single-action positive control), ÃÂ§11b the same shape on a
        # DV table asserting the answer AND the fusion. Ã¢ÂÂ  Both tiers were GREEN THROUGH the bug, because every
        # earlier test put the affected rows in SEPARATE FILES where a rewrite renumbers nothing Ã¢ÂÂ so a floor
        # rise here is worth little unless the new assertions are single-file with the delete FIRST.
        # 6250 since 2026-08-05, from a green tier run: verify_merge_into 209 -> 239 after the forcing rule was
        # NARROWED TWICE. It now fires only when a merge carries >= 2 UPDATE/DELETE actions AND the table's row
        # identity is POSITIONAL (HasVirtualRowId()). Both narrowings removed a REFUSED CAPABILITY that bought no
        # safety: counting INSERT too refused the commonest shape (UPDATE+INSERT) on a non-DV table, and forcing
        # on SQL Server refused a 2-action merge into an identity EXTERNAL table. The added assertions pin BOTH
        # sides of each boundary, which is the point Ã¢ÂÂ a guard wider than its hazard fails as a lost capability,
        # which nothing complains about.
        # 6295 since 2026-08-06, from a green tier run: verify_delta_catalog_dv_default 58 -> 103 for the
        # deletion-vector read path. The vector used to be inlined into the generated SQL as one integer
        # literal per deleted row Ã¢ÂÂ MEASURED ~0.4ms and ~1.2KB PER DELETED ROW (a 200k-row table with 199k
        # deleted took 68.3s/301MB to scan; the same rows deleted copy-on-write took 1s/66MB), paid by EVERY
        # read until an OPTIMIZE, so an incrementally-merged table got slower every run. It is now bound as an
        # Arrow input and excluded with NOT EXISTS: 1.1s/93MB, flat in vector size.
        # The added assertions exist because the pre-existing DV coverage COULD NOT SEE ANY OF IT: every other
        # DV assertion in the repo deletes a handful of rows, i.e. stays below the inline threshold, and would
        # pass whether or not the bound path works. So they use deliberately LARGE deletes, a MULTI-FILE
        # section (each file binds its own single-use stream Ã¢ÂÂ a regression to one shared stream is invisible
        # with a single file, and would silently resurrect the later files' deleted rows), and a
        # FULLY-DELETED-FILE section (such a file is now skipped from the listing; skipping one that still has
        # live rows loses them silently, so every surviving row is asserted).
        # 6367 since 2026-08-06: verify_delta_update_grouped (72) Ã¢ÂÂ a large UPDATE now writes its post-images
        # in GROUPS as the read-back streams instead of accumulating every batch first (managed-heap peak
        # 327 -> 171 MB on 600k x 16 cols; the process peak barely moves, because the UPDATE's dominant term
        # is the BOXED SET-value dictionary built before any provider work Ã¢ÂÂ see DeltaReader.UpdateGroupBytes).
        # The suite updates 6000 rows on purpose: the read-back yields ~2048 rows per batch, so it spans three
        # groups. Mutation-tested Ã¢ÂÂ not clearing the per-group id list dies at the FIRST grouped UPDATE
        # ("materializedRowIds must carry one entry per row"), and not clearing the per-group pre-images
        # survives 51 assertions before the CDF section catches 12144 pre-images for 6000 rows. That second
        # mutant is why the CDF section exists: sections 1 and 2 pass with it in place.
        # 6389 since 2026-08-06: verify_delta_catalog_update 73 -> 84 (x2 legs), Â§"a rowid matched more than
        # once". `UPDATE â¦ FROM src` whose join matches one target row several times sends that rowid down
        # the seam once per match, and the seam keeps ONE value per rowid. That had NO coverage at all, and
        # the Arrow-native parse had to preserve it (a dictionary assignment deduplicated implicitly; the new
        # shape appends every match and compacts to each rowid's LAST ordinal). Mutation-tested by disabling
        # the compaction: the no-duplication assertion still PASSED and the membership one caught it â one
        # update silently lost. That is why the section asserts three things and not just the row count.
        # 6403 since 2026-08-06: verify_with_options 68 -> 82 — write tuning must reach the files a
        # TRANSACTION writes, not only the bulk path. Every engineered-wood table open in the Bridge passed NO
        # write spec except the eager-write helper, so the held table (CDF change files + parked batches)
        # silently fell back to snappy/122880/no-bloom while the SAME table's CTAS files honoured the setting.
        # The section pins the codec engine deliberately: under native_write DuckDB's COPY writes the data
        # files, so EW's ParquetWriteOptions never apply and the assertion would pass for the wrong reason.
        # Mutation-tested — reverting the spec on EnsureHeldTableAsync fails at exactly the CDF assertion.
        # 6801 since 2026-08-07: verify_delta_catalog_transactions 965 -> 1021 (x2 legs), §41 — a concurrent
        # METADATA change vs an open buffered transaction. The two buffered paths answer differently and the
        # difference is where the commit's base snapshot comes from: the APPEND flush opens the table fresh at
        # COMMIT so the concurrent range is empty and it commits; the DML path holds a transaction pinned at
        # STATEMENT time so the range is non-empty and it conflicts. The append half is the one worth a gate —
        # docs/delta-snapshot-caching.md proposes caching the Snapshot per (txn, path, version), and serving
        # the flush's open from such a cache would give it a stale base and start conflicting every append
        # that races a property edit. Mutation-tested with two mutants, each killed at its own assertion:
        # dropping the second property edit makes the COMMIT unexpectedly succeed, and moving the first one
        # outside con1's window breaks the version ladder that proves the window was open at all.
        # 6843 since 2026-08-08: verify_delta_catalog_optimize 40 -> 56 (VACUUM on a PARTITIONED table) and
        # verify_delta_tblproperties 58 -> 84 (delta.checkpointInterval is HONOURED, not merely stored).
        # The first pins a fixed bug with a wide blast radius: ITableFileSystem.ListAsync globbed one level,
        # so VacuumExecutor — which lists the whole table ROOT — only ever collected orphans AT the root and
        # reclaimed nothing under col=value/. ⚠ The assertion must be on a file INSIDE a partition directory;
        # an unpartitioned VACUUM test passes with the bug fully present. Both mutation-tested.
        # 6853 since 2026-08-08: verify_delta_tblproperties 84 -> 94 — delta.logRetentionDuration. Log
        # cleanup is implemented now, and this pins the DANGEROUS direction (a commit inside the window is
        # never collected) because that is what a tier can assert deterministically; the positive lives in
        # engineered-wood's LogCleanupTests, which injects a clock. ⚠ It guards a real hazard: our host
        # filesystem reported a HARDCODED Unix epoch as every file's mtime until this date, under which
        # every commit looks 56 years old and a 30-day retention collects one written a second ago.
        # 6891 since 2026-08-08: verify_delta_catalog_transactions 1021 -> 1042 (x2 legs), §42 — we now emit
        # commitInfo.isBlindAppend, the declaration a CONCURRENT engine reads to exempt our append from its
        # predicate check. ⚠ The `false` row is the load-bearing one: Delta's definition is CONJUNCTIVE
        # (onlyAddFiles && !dependsOnFiles), so `INSERT INTO t SELECT ... FROM t` is NOT blind despite
        # emitting only AddFiles — the dbt anti-join shape, where a wrong `true` makes another engine skip a
        # check it owes. Mutation-tested by claiming blind regardless of reads; dies at exactly that row.
        # 6921 since 2026-08-11: verify_setting_scope (+30) — see the MIN_SUITES note above.
        # 6929 since 2026-08-11: verify_with_options (+8) — the held table's NATIVE writer now gets the write
        # spec too. The pre-existing section next to it REQUIRES the codec engine (under native_write DuckDB's
        # COPY writes the bytes, so engineered-wood's options never apply), which is exactly why the native
        # half was asserted nowhere and was broken.
        # 6981 since 2026-08-11: verify_delta_partition_escaping (+56) minus verify_delta_catalog_transactions
        # (-2 per leg, x2 legs). §42 was restructured because the EW bump made the two ENGINES legitimately
        # disagree on a version neither of them declares for — a CTAS's data write records `false` on the
        # codec engine and nothing on the native one, both safe. Pinning the old exact five-row table pinned
        # an ENGINE rather than a behaviour, which an engine-doubled suite cannot do; it now pins the
        # declarations plus the SAFETY PROPERTY (no version except the genuinely blind one claims blind).
        # 7023 since 2026-08-12: verify_delta_tblproperties (+30) — the §8 constraint gate. A Delta table
        # declaring delta.constraints.* turned out to be INSERT-ONLY here (the INSERT is enforced by
        # evaluation; UPDATE and DELETE are refused), which arrived SILENTLY with the engineered-wood pin
        # onto upstream — nothing on our side asked for it, so nothing on our side would have noticed it
        # regressing. Its unset assertion doubles as the regression gate for the ONE-WAY DOOR that pass
        # closed: a constrained table used to reject every property edit, including the one removing the
        # constraint. ⚠ The unconstrained twin taking identical statements is the load-bearing control.
        # 7046 since 2026-08-13: verify_delta_native_scan (+23) — the DV / partitioned / column-mapped
        # shapes. fabricator_delta_native_scan was SERVING DELETED ROWS (measured: 10 rows where the catalog
        # and fabricator_delta_scan both returned 7), and the pre-existing fixture could not have caught it —
        # delta_simple is a plain table, so all 36 original assertions passed with the bug fully present.
        # Each new shape is asserted THREE ways (catalog / delta_scan / native_scan) so "they agree" cannot be
        # satisfied by a reader that has stopped returning rows.
        # 7076 since 2026-08-13: verify_delta_native_scan (+30) — projection pushdown for both path-based
        # Delta readers. ⚠ The load-bearing case is the REVERSE-ORDER projection: wrapper and binding resolve
        # the column list through ONE ProjectionPlan which orders by the DECLARED schema, because
        # engineered-wood emits in schema order whatever order it is asked in. Ordering by the request agrees
        # with the declaration for almost every query and disagrees exactly out of schema order — where the
        # failure is SIGSEGV, not a wrong answer. Mutation-tested: the request-order mutant crashes there
        # after 67 assertions pass.
        # 7185 since 2026-08-14, from a green tier run (⚠ 7152 - 7076 = 76 of the gap predates this — the
        # batched-read routing gate (+25), verify_delta_catalog_time_travel 49 -> 98 and the DeltaTxnScope
        # autocommit-pin repoints raised the actual without raising the floor; closed here like the earlier
        # lags). The +33 of this change: verify_delta_catalog_changes 73 -> 89 — the delta.changes TIMESTAMP
        # bounds (ABI v70, the delta.* namespace), pinned as ts≡version EQUIVALENCE plus both out-of-history
        # directions as EMPTY feeds plus the three mutual-exclusion refusals, because an off-by-one in
        # "first version at-or-after" is invisible without the equivalence assertion — and
        # verify_delta_catalog_functions 28 -> 45, §8: the `delta` FUNCTION schema is ADVERTISED on a LOCAL
        # attach (the host silently drops functions in an unadvertised schema — the measured fabric-schema
        # failure shape), all six functions declared as table functions, and DDL into the namespace refused.
        # 7478 since 2026-08-17, from a green tier run of 7475 plus the 3 assertions added to the new suite
        # right after it (⚠ 7418 - 7185 = 233 of the gap PREDATES this — the partition-only batched form
        # (+67) and the PresentNames / TopN / union-form gates raised the actual without raising the floor;
        # closed here like the earlier lags, since a floor 233 below the actual tolerates a regression that
        # large in silence). The +60 of this change is verify_delta_ctas_ordering, and NOTHING ELSE MOVED:
        # every other suite reported its exact prior count, which is the behaviour-preservation claim for a
        # change that reorders two writes without altering any answer.
        # 7505 since 2026-08-17, from a green tier run: 7478 + exactly verify_delta_statistics' 27, so NO
        # other suite moved. That is the load-bearing number for a change that hands the optimizer a
        # cardinality it never had — it alters PLANS, and the plan-sensitive suites (the batched-read
        # routing gates, merge_into, subplan_dedup) reporting their exact prior counts is what says no
        # answer and no routing followed the estimates.
        # 7558 since 2026-08-17, from a green tier run: 7505 + exactly the 53 assertions ABI v74 added
        # (verify_delta_catalog_alter 116 -> 132, verify_delta_sorted_by 30 -> 40,
        # verify_delta_catalog_nested_alter 100 -> 127), so NO other suite moved. That is the claim that
        # matters for replacing alter_table's kind int + arg1/arg2/flags with one typed JSON doc: it is a
        # TRANSPORT change, so every ALTER answer must be identical.
        # 7499 since 2026-08-18: 7558 - exactly the 59 assertions of the two deleted MFR suites (36 + 23),
        # so NO surviving suite moved. That equality is the whole claim for a removal — the production Delta
        # read path is the managed DeltaNativeReader and never crossed delta_list_files, so deleting the
        # spike must change no other answer.
        # 7646 since 2026-08-18: 7499 + exactly the 147 of the clustered-optimize accumulated leg, so no
        # other suite moved — the claim for a change that alters how the clustered rewrite CUTS FILES.
        # 7705 since 2026-08-19: 7646 + exactly the 59 of verify_views_catalog, so no other suite moved —
        # the claim for a change that adds a new CatalogType to the schema entry's lookup and BOTH Scan
        # overloads. ⚠ Reporting views under the TABLE_ENTRY scan (which duckdb_columns() needs) is the part
        # that could have disturbed table enumeration, and the unchanged remainder is what says it did not.
        # 7719 since 2026-08-20: 7705 + exactly the 14 of verify_catalog_init, so no other suite moved —
        # which is the whole claim for this change: it moves WHERE work happens (SQL Server's first CONNECT
        # leaves get_capabilities for catalog_init), not WHAT any answer is.
        # 7736 since 2026-08-21: verify_wait — fabricator_wait(rows, millis [, threads]), the pure-DuckDB
        # WAIT source. Hermetic by construction (no Arrow, no bridge, no provider), and gated because a
        # REGISTERED function is a shipped feature: this tree has already removed one spike that shipped
        # by accident. ⚠ It pins the CONTRACT, not parallelism — asserting that would need an UPPER bound
        # on elapsed time, the flaky direction.
        # 7750 since 2026-08-21: verify_wait 20 -> 31 for the `async_wait` A/B — the one HERMETIC gate for
        # the mechanism ArrowStreamScan now ships (a scan that cannot pull hands its worker BACK instead of
        # parking on the pull lock, so a sibling union branch is no longer starved). ⚠ It is a TIMING
        # assertion, which this tier otherwise refuses: what makes it acceptable is that it is a RATIO
        # between two legs of ONE statement shape in ONE process differing in ONE named parameter, with a
        # LOWER-bound positive control in front of it so it cannot pass vacuously.
        # 7772 since 2026-08-22: verify_delta_catalog_write 43 -> 54, ENGINE-DOUBLED so +11 twice — a
        # 40000-row (~20 morsel) CTAS and INSERT read THROUGH the catalog, now that the write sinks declare
        # ParallelSink() true and several DuckDB tasks push into ONE bulk session at once
        # (docs/scan-concurrency.md §7c). What that can break is the BOOKKEEPING, not a type or a name, so it
        # is asserted by count + sum(id) + a hash checksum: a batch enqueued twice, dropped, or mis-paired
        # moves one of the three. ⚠ Its source is a fabricator TABLE and not range(), which is a
        # single-threaded source — off range the sink gets ONE task however many threads are set, and the
        # section would pass while exercising nothing.
        # 7818 since 2026-08-22: the rowid DML sinks joined the parallel-write work, so
        # verify_delta_catalog_delete 28 -> 39 and verify_delta_catalog_update 84 -> 96, both
        # ENGINE-DOUBLED (+23 twice). A DELETE appends rowids and an UPDATE appends (SET values ++ rowid)
        # into ONE producer from several tasks now, so the sections are ~20 morsels rather than the single
        # chunk the rest of those suites touch. The UPDATE one asserts a DERIVED value per row
        # (s = 'u' || id): a concurrency bug there writes a value to the WRONG row, which no count sees.
        # 7986 since 2026-08-22: verify_lateral (168) — the correlated-LATERAL function kind. Its
        # load-bearing assertions are the REFERENCE ORACLE (both paths' full result sets, EXCEPT in both
        # directions, at threads=4 over duplicate correlated values) and §5's `batch_rows` proof that the
        # batched path really batched: result equivalence alone would pass on a build that silently reverted
        # to one call per row. ⚠ §5's fixture needs ALL-DISTINCT correlated values — decorrelation puts a
        # DISTINCT aggregate under the operator, so a 97-distinct-value table can never hand it more than 97
        # rows and the first version of that assertion was unreachable by construction.
        # rows and the first version of that assertion was unreachable by construction. §13 covers the gather's
        # real risk surface — a correlated VARCHAR long enough to be HEAP-allocated (an inlined string would
        # survive a dangling buffer), plus a STRUCT and a LIST, all replicated across drain slices by a 1→N
        # fan-out; §14 executes a PREPARED statement twice, since the managed binding is reused across
        # executions while a session is per operator state.
        # 8197 since 2026-08-31 (same day, fourth bump): a NAMED parameter AFTER the variadic tail --
        # verify_global_functions 160 -> 178 (+18). The one combination the two marshals had never been asked
        # for: the tail branch sets positional_index to the input width, so a named lookup that depended on it
        # would read the wrong slot. It worked unchanged; the gate is what stops it being "wired and never
        # exercised". From a green 74/74 run; 8179 + 18 = 8197 exactly. Supersedes:
        # 8179 since 2026-08-31 (same day, third bump): the VARIADIC tail extended to TABLE-IN-OUT --
        # verify_global_functions 145 -> 160 (+15). There the tail widens the ARGS BATCH, not the input
        # stream, so the load-bearing assertion is the ROW COUNT: 3 input rows in, 3 out. From a green 74/74
        # run; 8164 + 15 = 8179 exactly. Supersedes:
        # 8164 since 2026-08-31 (same day, second bump): the VARIADIC tail extended to LATERAL functions --
        # verify_lateral 247 -> 285 (+38: a bind-time constant declared FIRST, which needed no code at all,
        # and [CONSTANT][POSITIONAL][TAIL] in one declaration). From a green 74/74 run; 8126 + 38 = 8164
        # exactly. Supersedes:
        # 8126 since 2026-08-31: VARIADIC parameters (Params.VarArgs) -- verify_global_functions 118 -> 145
        # (+27, the scalar and table tails) and verify_sqlgen 59 -> 76 (+17, the variadic generator), from a
        # green 74/74 run. 8082 + 27 + 17 = 8126 exactly, which is what shows no other suite moved -- the
        # behaviour-neutrality claim for a change that touches every function kind's signature build.
        # Supersedes:
        # 8082 since 2026-08-29 (same day, fifth bump): verify_lateral 235 -> 247 (const_arg REMOVED -- the
        # text channel measured complete; +prepared-parameter, getvariable and NULL-refusal pins), from a
        # green 74/74 run. Supersedes:
        # 8070 since 2026-08-29 (same day, fourth bump): verify_lateral 227 -> 235 (+8, struct configs through
        # all three channels), from a green 74/74 run. Supersedes:
        # 8062 since 2026-08-29 (same day, third bump): verify_lateral 221 -> 227 (+6, the weird-name guards
        # and the pinned text-channel residual), from a green 74/74 run. Supersedes:
        # 8056 since 2026-08-29 (same day, second bump): verify_lateral 211 -> 221 (+10, the bare-constant
        # fold in the correlated shape — TryFoldRenderedConstant), from a green 74/74 run. Supersedes:
        # 8046 since 2026-08-29: verify_lateral 168 -> 211 (+43, the bind-time constant channel:
        # Params.Constant + const_arg), from a green 74/74 run. The 8003 note it supersedes:
        # 8003 since 2026-08-23: NOT from the CDC slice (that is SqlServer-only and adds nothing here) — the
        # ABI v80 scalar-bind commit took verify_global_functions 101 -> 118 and did not bump this floor, so
        # the tree ran 17 assertions above it for a day. Surfaced by the CDC slice's hermetic run coming out
        # at 8003 against a floor of 7986 and the delta not being attributable to the change under test.
        # 8250 since 2026-09-01 (same day, THIRD bump): verify_host_query 31 -> 98 across the whole
        # host_query pass -- the double-execution fix, fabricator_host_exec (the DDL/DML sibling, table form
        # AND scalar under one name), and the NAMED-SOURCE factory fix, whose two instruments are the only
        # way its cost is visible from SQL. 8183 + 67 = 8250 exactly, from a green 74/74 run, which is what
        # shows no other suite moved. The 8205 note it supersedes:
        # 8205 since 2026-09-01 (same day, second bump): +22 on verify_host_query (31 -> 53) for the
        # DOUBLE-EXECUTION fix -- fabricator_host_query ran its SQL twice (PopulateReturnSchema ran the bind
        # factory for the schema, the scan ran it again for the data), so one call of an INSERT left TWO
        # rows. The bind now DESCRIBES via a prepared statement. 8183 + 22 = 8205 exactly, from a green
        # 74/74 run, which is what shows no other suite moved. /!\ Only a COUNTING assertion can see this:
        # every "the rows are right" test in that file passed with the defect fully present. The 8183 note
        # it supersedes:
        # 8183 since 2026-09-01: fabricator_render MOVED OUT to Fabricator.FluidPlugin, taking
        # verify_global_functions 178 -> 164 (-14). A DOWNWARD bump, which is the rare one: the assertions
        # were not deleted, they moved to verify_plugin_fluid (service tier, 20) because a plugin cannot be
        # loaded in a tier that points FABRICATOR_PLUGIN_DIR at an empty directory on purpose. The one thing
        # only render covered here -- an untyped NULL in an ANY-declared position -- was re-homed onto the
        # VARARGS tail in the same suite rather than dropped. The 8197 note it supersedes:
        # 8259 since 2026-09-02: verify_host_query 98 -> 107 for ABI v83 -- fabricator_host_query's replay
        # of the caller's SEARCH PATH raised an INTERNAL error (SET_WITHOUT_VERIFICATION requires a fully
        # qualified set path) on a shape as ordinary as "set a schema, then call host_query". /!\ The RESET
        # in that section is load-bearing and a mutant proved it: an earlier `USE memory.hq_s` leaves a
        # qualified entry, against which the bug does not fire, so the section PASSED with it fully present.
        # 8250 + 9 = 8259 exactly, from a green run.
        # 8662 since 2026-09-04 (same day, seventh bump): verify_plugin_fluid 344 -> 386 -- publish(name),
        # which hands a table the template STAGED to the SQL the template generated. The one route that
        # works: a TEMP table is invisible to the caller and a REAL table created at bind time is invisible
        # to the statement being bound, so only a TABLE FUNCTION can carry it. LAZY (user-directed): the
        # relation streams at scan time under no row cap, and an unscanned publication is bounded by an
        # eviction cap instead.
        # 8620 since 2026-09-04 (same day, sixth bump): verify_plugin_fluid 337 -> 344 -- the print block's
        #   `sql_literal` option, which renders each value as a DuckDB SQL literal through the SAME
        #   FluidValueModel.SqlLiteral the {{ v | sql }} filter uses. One assertion states that agreement
        #   directly, because a second conversion could drift on quoting, number format and refusals.
        # 8613 since 2026-09-04 (same day, fifth bump): verify_plugin_fluid 327 -> 337 -- the {% print %}
        #   BLOCK, which is {% query %} with the destination changed (rows RENDERED, not bound to a name).
        #   ⚠ One of the 10 is a CORRECTED assertion, not a new one: the shared SELECT-only refusal used to
        #   say "query()" whatever surface refused, so a {% print %} author was sent to the wrong tag. Each
        #   surface names itself now, and the {% query %} block's own assertion is what caught it.
        # 8603 since 2026-09-04 (same day, fourth bump): verify_plugin_fluid 318 -> 327 -- {% exec name %}
        #   binds the affected-row count, the same shape as {% query name %}. ⚠ The identifier is OPTIONAL
        #   and that is the whole difficulty: {% exec %} and {% exec x: 7 %} are shipped spellings, and a
        #   bare optional Ident consumes the `x` of `x: 7` and cannot back out. A negative lookahead
        #   Not(Terms.Char(':')) separates them; the mutant without it dies at a PRE-EXISTING §16 assertion.
        # 8594 since 2026-09-04 (same day, third bump): verify_plugin_fluid 313 -> 318 -- a SHIPPED BUG,
        #   user-found: a STRUCT cell coming OUT of a query result was a LAZY wrapper over a RecordBatch
        #   that is disposed as the result is consumed, so reading it at render time threw a
        #   NullReferenceException out of Apache.Arrow. The eagerness a ROW always had stopped ONE LEVEL
        #   DOWN. ⚠ Needed no parameters to reproduce, and nothing above could see it: every earlier
        #   nested read came from the PARAMS bag, whose batch lives for the whole call.
        # 8589 since 2026-09-04 (same day, second bump): verify_plugin_fluid 307 -> 313 -- parameters
        #   NEST (lists of structs, structs of lists, any depth), converted recursively in two passes.
        #   ⚠ TWO of the 6 are REPLACED assertions: §17 pinned "a nested value has no SQL list form"
        #   hours earlier, and falsifying that is how the change announced itself.
        # 8583 since 2026-09-04: verify_plugin_fluid 296 -> 307 -- a Fluid ARRAY binds as a SQL LIST
        #   ({% query r xs: v %}… unnest($xs) …), replacing a refusal that was OURS and not DuckDB's
        #   (measured: PREPARE p AS SELECT a: unnest($1); EXECUTE p([1,2,3,4,5]) yields five rows).
        #   ⚠ One of the 11 is a REPLACED assertion, not a new one: the suite used to pin "has no SQL
        #   parameter form" for the filter spelling, and falsifying it is how the change announced itself.
        # 8572 since 2026-09-03 (same day, seventh bump): verify_delta_catalog_native_write 147 -> 148 --
        #   the NATIVE-path half of the bound-input leak gate. Both COPY sites in
        #   NativeParquetDataFileWriter used to take a unique view name and DROP it afterwards; the views are
        #   TEMPORARY now, so the names are fixed constants and nothing drops anything. No row assertion in
        #   that suite can see the difference, which is how the original defect survived. Mutation-tested
        #   together with the codec half: restoring temporary:false kills BOTH, each at its own line.
        # 8571 since 2026-09-03 (same day, sixth bump): verify_delta_catalog_filter_modes 39 -> 55 --
        #   the codec exact-filter path binds an Arrow view per batch, and until today it was registered
        #   NON-TEMPORARY (duckdb_arrow_scan hardcodes it), so each filtered SELECT left one behind in the
        #   user's own memory.main -- unbounded, and scanning one SEGFAULTED. Temp views now; the section's
        #   EXPLAIN control proves exact mode is in force, which is what makes the row count evidence that
        #   HostBatchFilter ran at all.
        # 8555 since 2026-09-03 (same day, fifth bump): verify_plugin_fluid 285 -> 296 -- both blocks
        # take OPTIONAL NAMED ARGUMENTS now, bound as parameters ({% query t a: 1, b: 2 %}), through the
        # same ToParameter table the filter form uses.
        # 8544 since 2026-09-03 (same day, fourth bump): verify_plugin_fluid 275 -> 285 for the
        # {% query name %} BLOCK -- the body is SQL written as template text and the RESULT SET is bound
        # to `name`, the same ArrayValue of indexable rows the query() function returns.
        # 8534 since 2026-09-03 (same day, third bump): verify_plugin_fluid 256 -> 275 for the
        # {% exec %} BLOCK -- the body is rendered to a separate output and run as SQL, so a real
        # multi-line statement with Liquid inside it needs no quote-escaping and the block emits nothing.
        # 8515 since 2026-09-03 (same day, second bump): verify_plugin_fluid 248 -> 256 -- staging into
        # a TEMP table is IDEMPOTENT under bind repetition (a view over such a template works on every
        # use) while a REAL table collides with itself, which is the property that makes the temp-table
        # idiom the right one on the fluid_query surface rather than merely the tidy one.
        # 8507 since 2026-09-03: verify_plugin_fluid 238 -> 248 for the PINNED per-render host
        # connection (ABI v84) -- exec() and query() in one template now share one DuckDB connection,
        # so a TEMP table staged by one is readable by the other. 8497 + exactly that suite's 10, which
        # is what shows no other suite moved.
        # 8497 since 2026-09-02: 8259 + verify_plugin_fluid's 238, moved in from the service tier with Fluid
        # becoming a built-in. The suite went 234 -> 238 in the same change: its two plugin-row assertions
        # ("loaded | fluid", nothing rejected) became a pair saying Fluid contributes NO plugin row and its
        # two functions have exactly ONE registration each, plus the HostsCatalog refusal and its
        # unknown-provider control. 3105 + 238 + 8259 - 3339 arithmetic aside, both numbers come from green
        # runs.
        : "${MIN_ASSERTIONS:=8662}"
        ;;
    service)
        SELECT_CMD=scripts/list-service-suites.sh
        # Floors measured 2026-07-25 against the compose stack: 42 suites / 1221 assertions, all green.
        # 1227 since 2026-07-26: verify_granular_types gained 6 (the SQL datetime2(7) -> Delta refusal and
        # the microsecond-cast workaround). Raised deliberately in the same commit, per the error text below.
        # 43 RUNS / 1388 since 2026-07-29: verify_delta_catalog_s3 runs a SECOND time on the codec engine
        # (+161, the same count as its native leg). 42 suites, 43 runs Ã¢ÂÂ the floor is on RUNS.
        # 44 RUNS / 1413 since 2026-07-31: verify_session_tag (+25) Ã¢ÂÂ fabricator_session_tag, which needs a
        # real server (it pins a provider connection and reads the session's own monitoring ids).
        # 46 RUNS since 2026-08-08: verify_read_write_same_catalog — INSERT INTO t SELECT ... FROM t, the
        # MARS outstanding-result-set collision (error 595). Service-only: it needs a real SQL Server, and
        # its seed size is load-bearing (the bug does not reproduce below ~30k rows).
        # 47 RUNS since 2026-08-09: verify_read_isolation — the mssql_read_isolation OPT-IN (reads join the
        # DuckDB transaction, so successive statements share one view). Service-only: it needs a real SQL
        # Server, and its load-bearing observable is a REFUSAL that only exists against one (with MARS off,
        # a self-written table is unreadable unless the read joined the transaction).
        # 50 runs since 2026-08-11: verify_mars_dynamic (44) pins that `mssql_mars` is resolved PER
        # CONNECTION at open time rather than once per catalog — so a `SET` after the ATTACH takes effect,
        # and two DuckDB connections sharing ONE attached catalog can use different modes.
        # 51 runs since 2026-08-18: verify_plugin_install - fabricator_install_plugin() end to end. It is
        # here rather than in the hermetic tier only because its fixture (the plugin archive) comes from the
        # same build step as FABRICATOR_PLUGIN_DIR, not because it needs a service; and it is run against its
        # OWN empty plugin root, because every assertion in it is of the form "this changed".
        # 52 runs since 2026-08-18: verify_http_transport — a managed HTTP call routed through DuckDB's OWN
        # HTTP stack (ABI v76). Service tier rather than hermetic ON PURPOSE and not merely because it needs
        # a server: its two load-bearing sections are A/Bs against MinIO's SELF-SIGNED cert, which is what
        # turns "DuckDB's TLS configuration and secrets reach the call" from a claim into a measurement.
        # 53 runs since 2026-08-23: verify_mssql_cdc — the db.cdc.* surface (slice 1 of docs/mssql-cdc.md).
        # Service tier by necessity, and it is the ONE suite in either tier that also needs SQL SERVER AGENT
        # (docker-compose sets MSSQL_AGENT_ENABLED=true): its agent_status assertion is the only thing that
        # would notice a revert of that compose change, because sp_cdc_enable_db/_table both SUCCEED with the
        # agent stopped and every other assertion in the suite passes with it off.
        # 54 runs since 2026-08-24: verify_raw_query — fabricator_query / fabricator_exec semantics. It exists
        # because NOTHING owned those two functions: 29 suites USE fabricator_query and none was about it,
        # which is how it shipped executing every statement TWICE with no assertion noticing. Only a COUNTING
        # assertion can see that; every "the rows are right" test passes either way.
        # 53 runs since 2026-08-31: verify_session_tag was moved OUT of this tier (user-directed) behind a
        # require-env FABRICATOR_RUN_SESSION_TAG the tier does not provide. It is INTERMITTENT — its section 5
        # sometimes refuses with "must be called inside an explicit transaction" on the statement right after
        # a bare BEGIN, i.e. a transaction-state propagation race, not a wrong answer. It reddened two
        # scheduled CI runs on commits that cannot reach it and was first reproduced LOCALLY on 2026-08-31
        # (1 in 4, then green in the very next full tier).
        # /!\ The exclusion is a LOSS: the session-tagging feature now has NO gate. The suite is unchanged and
        # runs on demand -- FABRICATOR_RUN_SESSION_TAG=1 unittest --test-dir . test/verify_session_tag.test.
        # Deleting that one require-env line puts it back.
        # 54 since 2026-09-01: + verify_plugin_fluid (the Fluid/Liquid template engine as a plugin).
        # 53 since 2026-09-02: verify_plugin_fluid LEFT for the hermetic tier (Fluid is a built-in
        # provider assembly now, so it needs no plugin root). A DOWNWARD bump -- the run moved, it was not
        # lost.
        : "${MIN_SUITES:=53}"
        # 1424 since 2026-08-01: verify_exec_invalidate_cache 10 -> 21, for the OUT-OF-BAND DROP path Ã¢ÂÂ the
        # catalog's self-heal, documented in CLAUDE.md and until now covered by NOTHING. The service tier ran
        # 44/44 green while that path was broken, which is why the section exists. It must run with
        # mssql_exec_invalidate_cache OFF: with the auto-invalidate ON (as the rest of that suite needs) the
        # DROP refreshes the whole cache and the name leaves the discovered list, so the lookup answers
        # "does not exist" WITHOUT ever fetching columns Ã¢ÂÂ the section then passes with the provider's
        # absence detection disabled, which is exactly what mutation-testing caught it doing.
        # 1444 since 2026-08-02: verify_delta_catalog_s3 161 -> 171, ÃÂ§11 Ã¢ÂÂ the attach-time warning for an
        # s3:// root opened READ_WRITE with no NAMED secret. MEASURED first: that shape loses 40 of 48
        # concurrent commits SILENTLY (ÃÂ§8.3). Two mutants, killed in opposite directions by ONE assertion Ã¢ÂÂ
        # suppressing the warning gives 0, ignoring access_mode gives 2 (the AUTOMATIC attach warns too),
        # which is what proves the new C++ access_mode plumbing is read rather than merely forwarded.
        # Ã¢ÂÂ  +20, not +10: verify_delta_catalog_s3 is THE DOUBLED SUITE of this tier (see DOUBLED below), so
        # every assertion added to it counts ONCE PER ENGINE. A floor of 1434 was set first from the
        # standalone 161 -> 171 delta and would have silently tolerated a 10-assertion regression.
        # 1465 since 2026-08-04: verify_ctas_text_type 8 -> 15 Ã¢ÂÂ the SQL Server half of the CREATE-conflict
        # fix. SQL Server was never in the DANGEROUS half of that defect (its own CREATE TABLE rejects a
        # duplicate, so no write was lost); what changed is that the user gets the ordinary catalog error
        # instead of the raw provider 2714. Asserted on this tier because SQL Server SHARES the fixed C++
        # path with Delta, where the same gap discarded the write silently.
        # Ã¢ÂÂ  1458 - 1444 = 14 of this gap PREDATES the change (the unified parameter protocol pass raised the
        # actual and not the floor). Closed here for the same reason as the hermetic one.
        # Ã¢ÂÂ  The floor is the MEASURED tier total (1465), not 1458 + the standalone delta. Both numbers in the
        # first draft of this note were ARITHMETIC rather than measured and both were wrong: the suite was 8
        # assertions, not 6 (I counted the statements I had added instead of running the suite before changing
        # it), so a floor of 1467 tripped the tripwire on a perfectly green 44/44 run and cost a re-run to
        # explain. Measure the BEFORE count while you still can, or take the floor from a green tier run.
        # 45 runs / 1640 since 2026-08-07, from a green tier run: verify_merge_into_mssql (106) is the
        # SQL Server half of MERGE INTO. It is the companion to the hermetic verify_merge_into and covers the
        # two things Delta structurally cannot: a COMPOUND rowid (a composite PK arrives as ONE struct-typed
        # column that ReferenceKeyColumns destructures Ã¢ÂÂ a Delta rowid is always a single virtual BIGINT), and
        # a table with NO row identity at all, which is the shape the !HasRowId() guard exists for (without it
        # an insert-only merge reads one past the chunk's width and fatally invalidates the database).
        # 45 runs / 1678 since 2026-08-07: verify_delta_catalog_s3 gained a 19-assertion TEARDOWN (both engine
        # legs, so +38) that OPTIMIZEs and VACUUMs the tables it leaves in the persistent MinIO bucket. It is
        # cleanup with assertions, not coverage: the added queries prove the tables are still readable and
        # INTACT after the VACUUM, so a vacuum that removed a live file fails HERE rather than in some later
        # run. See the section header in that suite for what it bounds (data files) and what it does not
        # (the log, which engineered-wood never cleans and which this makes grow slightly FASTER).
        # 1779 since 2026-08-09: verify_read_write_same_catalog 68 -> 101 for the mssql_mars=false
        # self-deadlock precheck (section 7). Its two POSITIVE CONTROLS are the load-bearing half — an
        # UNTOUCHED table still reads (so the refusal is precise, not blanket) and the same shape works with
        # MARS on (so nothing is broken in the shipped default). Mutation-tested: disabling RecordTouch kills
        # it at the refusal assertion. That section also SETs a finite mssql_command_timeout on purpose --
        # a regression there does not fail, it HANGS unbounded, which would stall this tier instead of
        # failing it; the mutant run confirmed the timeout converts it into a loud error.
        # 1746 since 2026-08-08 (same day, second pass): verify_read_write_same_catalog 36 -> 68 for the
        # mssql_materialize opt-out — the streaming pooled+SNAPSHOT path (§5) and the per-catalog ATTACH
        # option (§6, whose second catalog is the control proving the option is not process-global).
        # 1714 was the count before that: verify_read_write_same_catalog (+36). The tier went 45/1678 -> 46/1714, i.e.
        # the gap is EXACTLY the new suite and every pre-existing suite kept its own count — which is the
        # behaviour-preservation evidence for the scan-materialisation change (FabricatorCatalog::
        # MaterializeOwnScans + ScanSpec.Materialize) that landed with it. Raised in the same commit.
        # 1826 since 2026-08-09: verify_read_isolation (+47). The tier went 46/1779 -> 47/1826, i.e. the gap
        # is EXACTLY the new suite and every pre-existing suite kept its own count — the behaviour-preservation
        # evidence that routing reads into the transaction changed nothing with the option UNSET (its default).
        # 1837 since 2026-08-10: verify_mssql_s3_polybase 252 -> 263 — an INSERT ... SELECT into an EXTERNAL
        # table from the same catalog, the shape whose scan the host marks. Every earlier external INSERT in
        # that suite is INSERT ... VALUES, which has no scan, so the mark was entirely uncovered there.
        # 1961 since 2026-08-11: verify_mars_off_same_catalog 96 -> 98 (a §0 that asserts the SET actually
        # reached each catalog, via the new fabricator_server_info `mars_enabled` row — every other section
        # of that suite passes VACUOUSLY on a MARS-ON catalog, and nothing in SQL could tell before).
        # 2005 since 2026-08-11: verify_mars_dynamic (+44) — see the MIN_SUITES note above.
        # 2024 since 2026-08-11: verify_mars_off_same_catalog 95 -> 104 and verify_mars_dynamic 44 -> 57,
        # both re-pinned when `mssql_materialize` went back to a flat `true`. ⚠ NEITHER WAS A NUMBER BUMP.
        # Each suite had a section whose A/B rested on `materialize` FOLLOWING MARS, so with the flat default
        # both legs take the same route and the section stops varying with MARS at all — mars_dynamic §3 was
        # the load-bearing one, and re-pinning its 200 to 400 would have left it asserting nothing. Both now
        # discriminate on observables `materialize` cannot reach.
        # 2028 since 2026-08-14, from a green tier run: the 4-assertion lag predates this change (the tier
        # measured 2028 for both slice-1 gates already; the floor was not raised then). The delta.*
        # namespace rewrite (ABI v70) changed only SPELLINGS on this tier — verify_delta_catalog_s3 and
        # friends kept their exact counts, which is the behaviour-preservation claim.
        # 2035 since 2026-08-18: verify_plugin 10 -> 17 for fabricator_plugins(), the diagnostic that makes
        # the plugin scan report what it looked at and why it rejected something. The scan ends every
        # candidate in a catch, so an incompatible plugin used to be skipped with NO signal — the state a
        # plugin INSTALLER would turn into the normal failure mode. Mutation-tested: a scan that records
        # nothing dies at assertion 11, i.e. AFTER all ten pre-existing plugin assertions pass, which is the
        # right kill — the plugin still works, only the report is silent.
        # 2066 since 2026-08-18: verify_plugin_install (31). The load-bearing pair is that after an install
        # the ATTACH error CHANGES from "unknown provider" to the plugin's own refusal - which only a
        # re-discovery can produce - while its global function is STILL absent, which is the documented half
        # of the split (loader.RegisterFunction is permitted only during Extension::Load()). Mutation-tested
        # with two mutants, each killed at its own section.
        # 2080 since 2026-08-18: verify_plugin_install 31 -> 45 for UNINSTALL and the provider-name
        # collision REFUSAL. The collision needed a SECOND assembly (Fabricator.CollidingPlugin, pointed at
        # by a second plugin root) because no manifest or install argument can make two assemblies claim one
        # name. Its load-bearing assertion is the positive control - mssql_mars still registered, i.e. the
        # first-party provider still holds the name; "the plugin was rejected" alone would pass on a build
        # where BOTH were broken.
        # 2101 since 2026-08-18: 2080 + exactly the 21 of verify_http_transport, so no surviving suite moved.
        # 2108 since 2026-08-19: verify_functions 27 -> 34, the SQL Server half of catalog-bound VIEWS. The
        # mechanism is gated hermetically (verify_views_catalog); what is added here is the one thing that
        # suite structurally cannot show — a SECOND backend's declarations arriving, into a DISCOVERED schema
        # rather than through Delta's `__all__` expansion. ⚠ It re-ATTACHes first: the section above it
        # DETACHes, and without a live catalog the bare-name refusal would assert only that nothing is
        # attached. Mutation-tested (GetViews serving nothing dies at the first view assertion, after all 28
        # pre-existing ones pass).
        # 2116 since 2026-08-21: verify_plugin 17 -> 25, the sample plugin's plug_sleep — a test INSTRUMENT
        # rather than a feature (it makes a query's cost a number the test chose; docs/scan-concurrency.md
        # §6 turns effective parallelism into arithmetic with it). Two of the eight are the ones that could
        # not be inferred: a NEGATIVE argument is REFUSED, because Thread.Sleep(-1) is Timeout.Infinite and a
        # hanging suite is worse than a failing one; and the side effect really happens PER ROW, asserted as
        # a LOWER BOUND on elapsed time, which is the direction a loaded machine can only make more true.
        # 2129 since 2026-08-21: verify_plugin 25 -> 38, the sample plugin's plug_slow_range — the SOURCE-side
        # twin of plug_sleep, whose cost sits INSIDE get_next. The pair is what makes the pull-serialized /
        # convert-parallel split a measurement rather than a reading of the source, and it is the control that
        # verified the get_partition_data fix reaches a global TABLE-FUNCTION scan and not only the catalog
        # scan (docs/scan-concurrency.md §5/§6). Its own assertions: registered as a TABLE function, a
        # MULTI-batch yield (a single-batch test cannot tell a working loop from one that stops after its
        # first batch), both negative refusals, and the sleep really happening — a LOWER bound on elapsed
        # time, the direction a loaded machine can only make more true.
        # 2140 since 2026-08-21: verify_plugin 38 -> 49 for the union-overlap gate on the PRODUCTION scan
        # path. Service tier because it needs the sample plugin's blocking-pull table functions; the
        # mechanism itself is gated hermetically in verify_wait. Its A/B lever is DuckDB's own
        # debug_physical_table_scan_execution_strategy, under which BLOCKED is forbidden and our scan falls
        # back to the pre-fix code path — so both legs run in one process off one binary.
        # 2157 since 2026-08-22: verify_plugin 49 -> 66 for the parallel WRITE sink - a `SET threads=1` vs
        # `threads=4` ratio over four plug_sleep morsels feeding a CTAS into a fabricator table
        # (docs/scan-concurrency.md 7c). The threads=1 leg IS the pre-change path (the plan-time gate
        # returns false at one thread), so this is a same-binary A/B rather than a remembered number, with
        # the serial leg own duration as the positive control and both legs row counts asserted - a speed
        # claim is worthless if the fast leg dropped a batch. It also made this the first plugin suite to
        # WRITE, which is why it now needs `require parquet`: the sqllogictest runner does not auto-load a
        # statically linked extension, and without it the Delta write fails inside DuckDB COPY.
        # 2170 since 2026-08-22: verify_plugin 66 -> 79 for the rowid DELETE leg of the same ratio gate.
        # Worth its own leg because a DELETE is the LARGEST of the write ratios (3.22 -> 1.36 s on 2 M
        # rows): its whole cost is scan + filter + rowid append, i.e. exactly what the flag unblocks.
        # Mutation-tested: forcing the modify flag false dies at that ratio after 74 assertions pass,
        # while BOTH correctness suites stay green - which is the right kill, since a parallel DELETE
        # and a serial one produce the same rows.
        # 2203 since 2026-08-22: verify_functions 34 -> 67 for CATALOG-BOUND row-mapped (correlated LATERAL)
        # functions. The host machinery is gated hermetically (verify_lateral); this is the only place that
        # proves a CATALOG's `kind='lateral'` declaration arrives and RESOLVES — and it earned its keep
        # immediately: the declaration was listed by fabricator_functions() and the function still did not
        # exist, because CatalogFunctionSet.ParamSchema did not answer for the kind and a lateral function
        # cannot be registered without its positional parameter types.
        # 2221 since 2026-08-22: verify_plugin 79 -> 97 for a PLUGIN-authored correlated LATERAL function. It
        # is the only place the batching win is MEASURED rather than argued: plug_lat_slow sleeps ONCE PER
        # CALL, so 8 distinct outer rows cost 8 sleeps on DuckDB's row-by-row driver and one on the batched
        # operator (0.870 s vs 0.154 s), with the serial leg's own duration as the positive control and
        # `max(batch_rows)` (1 vs 8) as the mechanism assertion. ⚠ threads=1 is required — the delim scan
        # emits one chunk per radix partition, so several threads split the 8 rows and move the ratio.
        # 2295 since 2026-08-23: verify_mssql_cdc 73 (new) + verify_server_profile 15 -> 16 (the supports_cdc
        # capability row) = exactly +74 over 2221, i.e. NO other suite moved — which is the CDC slice's
        # behaviour-preservation claim, and it is exact rather than approximate.
        # 2327 since 2026-08-24: verify_mssql_cdc 73 -> 105 for the CDC SETUP functions and ABI v81's
        # tablefn_execute schema_may_change out-flag. ⚠ Those 32 assertions are the ONLY coverage of that ABI
        # entry in either tier — nothing else sets the flag — and §11 is the load-bearing one: it reads a
        # change table `cdc.enable` created moments earlier in the SAME session, which is impossible without
        # the deferred catalog rebuild the flag triggers. Two mutants (never report the DDL; move the DDL out
        # of Execute() into the iterator body, where the host has already read the flag) both die there.
        # 2361 since 2026-08-24: verify_raw_query 34, gating the describe-then-execute fix. Mutation-tested —
        # restoring the eager stream dies at §1 with "Expected 1, Actual 2", i.e. the doubled INSERT itself.
        # +7 later the same day for §8, which gates the AMBIENT CAPTURE the deferral needs: the pre-fix build
        # reports @@TRANCOUNT = 0 inside a transaction (pooled connection) where the fixed one reports 1.
        # 2438 since 2026-08-24: verify_mssql_cdc 105 -> 182 for THE READER, cdc.changes (slice 3) - exactly
        # +77 over 2361, i.e. NO other suite moved. Its section 18 is the load-bearing part, the retention
        # pre-check, and the mutant that disables it does not merely fail: it fails WITH the raw
        # `Msg 313 ... cdc.fn_cdc_get_all_changes_ ... .` on a call that supplied three arguments, which is
        # the unattributable message the whole function exists to replace.
        # /!\ Section 15 WAITS up to 60 s for the capture job rather than forcing a scan - cdc.capture_now()
        # contends for the database's single log-scan session (~1 failure in 57, MEASURED) and section 14
        # records why stopping the job first is not available there. It waits for the WATERMARK as well as
        # the rows, because a change row is not readable until fn_cdc_get_max_lsn() covers it: waiting only
        # for COUNT leaves a race whose symptom is a SHORT READ, i.e. a wrong answer rather than a failure.
        # /!\ A third mutant SURVIVED at first (drop the inverted-window short-circuit) and is what narrowed
        # that branch: a caught-up consumer produces from == to, not from > to. It is now gated with the only
        # shape that reaches it - an explicit ending_position BELOW the cursor.
        # 2495 since 2026-08-24: verify_mssql_cdc 182 -> 239 for slice 4 - the GENERATED capture-instance
        # name, the ownership marker and enable := true. Exactly +57 over 2438, i.e. NO other suite moved.
        # /!\ The section that matters most is the one whose row looks redundant: enabling the SAME table
        # twice reports changed = false on BOTH a table-keyed and a name-keyed build, so the discriminating
        # assertion is a DIFFERENT SPELLING of it (DBO.CDC_GEN after dbo.cdc_gen). A name-keyed build hashes
        # that differently and creates the table's SECOND capture instance, which makes cdc.changes refuse
        # that table outright - the reason the default had to move off the name in the first place.
        # /!\ The 100-character-table rows are the DEFECT the generated name removes (Msg 22927 naming a
        # capture instance the user never chose), and the table's own LENGTH beside them is the positive
        # control: without it they would pass on any table at all.
        # 2524 since 2026-08-25: verify_mssql_cdc 239 -> 268 for slice 5 - _capture_instance on every row and
        # the in-window DDL check (on_schema_change). Exactly +29 over 2495, i.e. NO other suite moved.
        # /!\ The POSITIVE CONTROL is the load-bearing row: a window with NO DDL in it reads normally under
        # the DEFAULT mode. Without it, "a DDL inside the window is refused" would pass equally on a build
        # that refused every read, or on one where cdc.ddl_history could not be queried at all.
        # /!\ And the mutant that drops the `ddl_lsn > @from AND ddl_lsn <= @to` predicate - checking the
        # table's whole DDL history instead of the WINDOW - dies at "a read that STARTS after the DDL is
        # clean again", which is the row that keeps the check from reading as "this table is poisoned
        # forever".
        # /!\ §19's positive control needed on_schema_change := 'ignore' when this landed: §7 alters
        # dbo.cdc_probe while dbo_cdc_probe is capturing, so a DDL genuinely sits inside that window. That
        # is the check working, not a workaround.
        # 2607 since 2026-08-25: verify_mssql_cdc 268 -> 351 for slice 7 - the TWO-INSTANCE BOUNDARY read as
        # ONE stream. Exactly +83 over 2524, i.e. NO other suite moved.
        # /!\ A ROW COUNT CANNOT DISCRIMINATE THIS ONE, and that is the trap the section is built around: the
        # OLDER capture instance never stops capturing, so reading it alone returns EVERY row - the same four -
        # just without the column only the newer one has. The load-bearing assertion is on VALUES that no
        # single instance can produce together (`v` from the older, `region` from the newer, in one result).
        # /!\ The mutant that removes BOTH halves of the partition - the `< @split` predicate and the clamp on
        # the older leg's TVF window - dies with "Expected 4 rows, but got 6": the DOUBLE COUNT that §2.2
        # MEASURED both instances make possible. Removing either half ALONE survives, because in this fixture
        # each covers the other; that redundancy is deliberate, since the failure it guards is a silent wrong
        # answer.
        # /!\ Deriving the boundary wrong (the older floor instead of the newer's) and never excusing an
        # absorbed DDL BOTH die at §19's `cdc.changes('dbo.cdc_probe')` = 0, 161 assertions in - a two-instance
        # read whose boundary is wrong stops absorbing the very DDL the second instance exists for.
        # /!\ The floor waits are not politeness: fn_cdc_get_min_lsn is transiently NULL for a newly enabled
        # instance (§1.6a) and that value IS the boundary, so without them the section intermittently gets
        # "the boundary ... is not established yet" instead of rows.
        # 3056 since 2026-08-31 (same day, second bump): the ATTACH-TIME variadic coverage --
        # verify_custom_functions 101 -> 134 (+33), covering all five catalog-bound kinds that take a tail
        # (scalar / table / sqlgen / table-in-out / lateral). From a GREEN 54/54 run; 3023 + 33 = 3056
        # exactly, which is the neutrality claim for the CatalogFunctionSet.ParamSchema change -- it now
        # answers for in-out and collector, so EVERY catalog in-out's signature is built from its declaration
        # instead of the bare {TABLE} fallback. verify_table_inout (63) and verify_collector (40) unmoved is
        # what shows that was byte-neutral for the functions that declare only their table input.
        # /!\ The coverage found TWO defects, which is why it was worth doing: the ParamSchema omission
        # (pre-existing, silently dropped a catalog in-out's whole declaration) and an ANY tail registering as
        # varargs = SQLNULL on the catalog path only (ours, hidden by the global path passing a flag).
        # The 3023 note it supersedes:
        # 3023 since 2026-08-31: VARIADIC parameters (Params.VarArgs) -- verify_custom_functions 89 -> 101
        # (+12, the CATALOG-BOUND variadic scalar cf_va_sum, which is the attach-time registration site the
        # three global demos do not reach), from a GREEN 54/54 run. 3011 + 12 = 3023 exactly, so no other
        # suite moved. /!\ Its own commit, not the feature's: that tier was still running when the feature
        # was committed, and a total from a run with any failure in it is not a floor candidate at all.
        # The 3011 note it supersedes:
        # /!\ From a GREEN RUN (54/54 - 2992), never from arithmetic - the arithmetic only CORROBORATES it
        # afterwards, and that is what shows no OTHER suite moved (2897 + 95 = 2992, the CDC suite's whole
        # delta). verify_mssql_cdc went 476 -> 559 -> 604 -> 630 -> 725 across slice 9, slice 8b,
        # _changed_columns and the four user-requested items. A floor left below the actual silently
        # tolerates a regression that size.
        # 3069 since 2026-08-31 (same day, third bump): fabricator_functions() reports variadicity --
        # verify_functions 67 -> 105 (+38) -- MINUS verify_session_tag's 25, which left the tier (see the
        # MIN_SUITES note above). Both halves come from ONE green 54/54 -- 3094 run: 3056 + 38 = 3094 exactly,
        # and that run's own per-suite line reports verify_session_tag at 25, so 3094 - 25 = 3069 is a
        # subtraction of a measured value rather than an inference about a suite that did not finish.
        # The 3056 note it supersedes:
        # 3162 since 2026-09-01 (same day, FOURTH bump): +4 on verify_plugin_fluid (89 -> 93) for the
        # TIMESTAMPTZ-to-DATE TIMEZONE TRAP. The section it replaced asserted that DuckDB had no
        # TIMESTAMPTZ -> DATE cast at all, which was FALSE and was an artifact of this suite: the cast needs
        # ICU, unittest does not auto-load extensions, and the suite had no `require icu` -- so a missing
        # REQUIRE presented as a missing FEATURE. The real hazard is bigger and silent: anything reading a
        # TIMESTAMPTZ without NAMING a timezone reads it in the SESSION's, so west of UTC five natural
        # spellings give the PREVIOUS DAY with no error. The section now SETS America/New_York (under the
        # runner's default UTC every route agrees and it would pass saying nothing), asserts the wrong-day
        # values as the truth, and asserts the two safe routes with those rows as their positive control.
        # 3158 + 4 = 3162 exactly, from a green 54/54 run. The 3158 note it supersedes:
        # 3158 since 2026-09-01 (same day, THIRD bump): fluid_query -- a Liquid template that renders to SQL
        # -- plus the shared Arrow/JSON value model behind it. verify_plugin_fluid 23 -> 89 (+66), and
        # 3092 + 66 = 3158 EXACTLY, which is what shows no other suite moved. Taken from a green 54/54 run,
        # not from that arithmetic: the arithmetic only corroborated it afterwards. The bulk of the +66 is
        # assertions no render test can make -- COMPARISON and ARITHMETIC on both the JSON and the Arrow
        # path, because Fluid's native JsonNode support renders correctly while computing wrong, so every
        # render assertion in that file passes on a build with no value converter. The 3092 note it
        # supersedes:
        # 3092 since 2026-09-01 (same day, second bump): verify_plugin_fluid 20 -> 23, three NULL-VALUE
        # assertions added when the Fluid pin moved to 3.0.0-beta.7 and its CS8604 exposed that a NULL
        # inside the params bag was covered by nothing. The 3089 note it supersedes:
        # 3089 since 2026-09-01: + verify_plugin_fluid's 20 (fabricator_render moved out of
        # Fabricator.SqlServer into Fabricator.FluidPlugin -- nine render assertions moved verbatim from
        # verify_global_functions plus the plugin-loaded, no-rejection, single-registration and parse-error
        # ones the move itself makes assertable). 3069 + 20 = 3089 exactly, which is what shows no other
        # suite moved. The 3069 note it supersedes:
        # 3200 since 2026-09-01: verify_plugin_fluid 93 -> 131 for SLICE 3 -- the Fluid `query(sql)`
        # function over the new HostQueryTransport seam, and above all its SELECT-only refusal, which is a
        # CORRECTNESS requirement rather than a policy (a template may render at BIND, and a bind-time write
        # fires on EXPLAIN of a statement that never runs). 3162 + 38 = 3200 exactly, from a green run,
        # which is what shows no other suite moved. The 3162 note it supersedes:
        # 3246 since 2026-09-02 (same day): verify_plugin_fluid 174 -> 177 for ABI v82 -- the scalar
        # crossings now carry the caller's context and RESTORE it, so a plain session SET reaches a plugin's
        # global scalar. Slice 4 had shipped hours earlier telling users to write SET GLOBAL; that
        # requirement is gone, and the +3 is the plain-SET assertion plus a SET GLOBAL leg kept beside it so
        # a fix repairing only one layer cannot pass. 3243 + 3 = 3246 exactly, from a green run.
        # The 3243 note it supersedes:
        # 3243 since 2026-09-02: verify_plugin_fluid 147 -> 174 for SLICE 4 -- {% include %} / {% render %}
        # resolved against fluid_template_root and read with read_blob over slice 3's HostQueryTransport.
        # /!\ NOT over a host-FS seam: one was built and MEASURED unusable, because a GLOBAL function has no
        # ambient opener and every fs_* callback dereferences it (the read took the process down). The same
        # missing ambient is why the setting must be SET GLOBAL. 3216 + 27 = 3243 exactly, from a green run,
        # which is what shows no other suite moved. The 3216 note it supersedes:
        # 3216 since 2026-09-01 (same day): verify_plugin_fluid 131 -> 147 for NAMED PARAMETER BINDING --
        # `sql | query: a: 1` binding $a. It is a FILTER because Fluid's grammar puts named arguments there
        # and nowhere else (a function call with them is a PARSE error, and Fluid has no dict literal).
        # 3200 + 16 = 3216 exactly, from a green run, which is what shows no other suite moved.
        # The 3200 note it supersedes:
        # 3257 since 2026-09-02 (same day): verify_plugin_fluid 177 -> 188 for ABI v83 -- a template's
        # query() now inherits the caller's TimeZone and catalog search path, so it resolves names and times
        # the way the statement that rendered it does. The discriminator is a SECOND, deliberately unusual
        # zone: one row alone would pass on a runner whose machine zone happens to be UTC.
        # 3246 + 11 = 3257 exactly, from a green run.
        # 3272 since 2026-09-02 (same day, second bump): verify_plugin 97 -> 112 for the HOST SERVICE
        # LOCATOR -- a plugin resolving IHostFileSystem with a Fabricator.Abstractions reference alone. It is
        # also the first in-tree proof that the ABI v82 ambient reaches a PLUGIN's global scalar: every fs_*
        # host callback dereferences the caller's ClientContext, and a global scalar had none until v82 (the
        # earlier attempt at this seam died with 0xC0000005). The load-bearing pair is the glob COUNT with a
        # zero-match negative control beside it -- 2 alone would pass on a build that returned everything.
        # 3257 + 15 = 3272 exactly, from a green run, which is what shows no other suite moved.
        # 3302 since 2026-09-02 (same day, third bump): verify_plugin_fluid 188 -> 218 for the Fluid
        # exec() -- a template that WRITES, permitted in fabricator_render and REFUSED in fluid_query. That
        # split is a CORRECTNESS boundary: a fluid_query template renders during bind_replace, and the mutant
        # removing the refusal re-measured section 8.3 exactly (an audit table 1 -> 2 -> 3 through EXPLAIN,
        # then merely CREATE VIEW, then one use of that view -- none of which executes the statement).
        # The load-bearing pairs are the refusal's "and the write did NOT happen" count, a query()-still-works
        # positive control beside it, and the injection pair (0 deleted with the table intact).
        # 3272 + 30 = 3302 exactly, from a green run, which is what shows no other suite moved.
        # 3318 since 2026-09-02 (same day, fourth bump): verify_plugin_fluid 218 -> 234. exec() is now
        # permitted in fluid_query too (user decision), so the refusal assertions became a PINNED
        # characterization of what a bind-time write costs -- EXPLAIN 1, CREATE VIEW 2, each view USE 3 then
        # 4 -- plus the finding that a statement cannot see the write its own template made (generated SQL
        # reads 1 while the table holds 42 afterwards), which is why "prepare then select" does not work.
        # ⚠ That block pins DuckDB's bind repetition, not our code, so no mutant of ours can kill it; its
        # value is that a change there fails an assertion naming the step.
        # 3302 + 16 = 3318 exactly, from a green run, which is what shows no other suite moved.
        # 3339 since 2026-09-02 (same day, fifth bump): verify_plugin 112 -> 133 for IHostLog, the host
        # LOGGING service -- the only in-tree proof that a PLUGIN can reach duckdb_logs, which it cannot do
        # on its own because FabricatorLog's whole surface is Microsoft.Extensions.Logging and the route runs
        # through the host_log reverse callback. Three assertions carry it and none implies another: the
        # BOOLEAN is IsEnabled (Trace false while Debug is true, so it is a real answer and not a constant),
        # `log_level` is the level MAPPING after two hops, and `type` is the plugin's own CATEGORY. Three
        # mutants, each dying at its own line: the mapping off by one at the log_level pair (116 pass first),
        # IsEnabled always true at the Trace assertion (124), and a Log that records nothing at the first
        # count (115).
        # ⚠ The braces assertion is a CHARACTERIZATION test of Microsoft.Extensions.Logging, not of our code:
        # a fourth mutant removing our "{Message}" indirection SURVIVED the whole gate, which is what showed
        # the indirection was unnecessary (MEL parses the format only when the argument array is non-empty).
        # The code was simplified to match the measurement rather than the theory.
        # 3318 + 21 = 3339 exactly, from a green run, which is what shows no other suite moved.
        # 3105 since 2026-09-02: 3339 - verify_plugin_fluid's 234, which moved to the hermetic tier with
        # Fluid becoming a built-in. A DOWNWARD bump for a suite that MOVED, not one that shrank.
        # ... and +3 on verify_plugin (133 -> 136) for the closure fixture that replaces what Fluid used to
        # prove: the sample plugin now has a PRIVATE DEPENDENCY, so a plugin's own assembly shipping beside
        # it and being reachable is still gated somewhere. 3339 - 234 + 3 = 3108.
        # 3109 since 2026-09-03: verify_custom_functions +1 -- dbo.cf_host_sum binds an Arrow input and
        #   must leave NO `in0` view behind. Until today that input was a CATALOG view (duckdb_arrow_scan
        #   hardcodes temporary:false), so this very call left one in the caller's memory.main pointing at
        #   a released stream, and scanning it SEGFAULTED. The 10 asserted just above is its positive
        #   control: a 0 is equally true of a build where the input was never registered at all.
        : "${MIN_ASSERTIONS:=3109}"
        ;;
    *)
        echo "usage: $0 [hermetic|service]" >&2
        exit 2
        ;;
esac

if [ -z "${UNITTEST:-}" ]; then
    if [ -x build/release/test/unittest.exe ]; then
        UNITTEST=build/release/test/unittest.exe
    else
        UNITTEST=build/release/test/unittest
    fi
fi
MANAGED=${MANAGED:-build/release/extension/fabricator/fabricator}

if [ ! -e "$UNITTEST" ]; then
    echo "ERROR: unittest binary not found at '$UNITTEST' (build it, or set UNITTEST=)" >&2
    exit 1
fi
if [ ! -d "$MANAGED" ]; then
    echo "ERROR: managed bridge not found at '$MANAGED' (run scripts/publish-managed.ps1, or set MANAGED=)" >&2
    exit 1
fi

export FABRICATOR_MANAGED_DIR="$MANAGED"

# The fixture path is interpolated straight into SQL, so it has to be a path DuckDB understands: a
# Windows drive path rather than the MSYS /d/... form that $PWD carries under Git Bash.
if [ -n "${RUNNER_OS:-}" ] && [ "${RUNNER_OS}" = 'Windows' ] || [ "${OS:-}" = 'Windows_NT' ]; then
    export FABRICATOR_DELTA_DIR="$(pwd -W)/test/fixtures/delta_simple"
else
    export FABRICATOR_DELTA_DIR="$PWD/test/fixtures/delta_simple"
fi

if [ "$TIER" = 'hermetic' ]; then
    # Anything that would let a service-dependent suite run half-configured is cleared, so this set
    # is PROVABLY the hermetic one rather than hermetic by assumption.
    unset MSSQL_TESTDB_DSN MSSQL_TEST_SERVER MSSQL_TEST_CONNECTION_STRING MSSQL_BINCOLL_DSN \
          MSSQL_TESTDB_URI MSSQL_TEST_PASS FABRICATOR_S3_ENDPOINT FABRICATOR_S3_SQL_ENDPOINT \
          FABRICATOR_DAX_DSN 2>/dev/null || true
    # ⚠⚠ FABRICATOR_PLUGIN_DIR IS POINTED AT AN EMPTY DIRECTORY, NOT UNSET, AND THE DIFFERENCE IS THIS
    # TIER'S DEFINING PROPERTY. Since 2026-08-18 an unset variable falls through to the DEFAULT plugin root
    # ~/.duckdb/fabricator/plugins, which is MACHINE STATE: a developer (or a CI runner with a cached home)
    # who happens to have a plugin there would have it loaded into every hermetic suite, and a plugin
    # contributing a global function changes what duckdb_functions() returns — which several suites count.
    # Unsetting it would make the tier hermetic BY ASSUMPTION, the exact thing the block above exists to
    # prevent. The override REPLACES the default rather than extending it (PluginPaths.ResolveRoots), so an
    # empty directory provably excludes the machine's own plugins.
    hermetic_plugin_dir=$(mktemp -d)
    export FABRICATOR_PLUGIN_DIR="$hermetic_plugin_dir"
    # Captured in BOTH tiers: the per-suite case below restores from this unconditionally, and in the
    # hermetic tier an unset (or empty) value would fall through to the DEFAULT root, i.e. straight back to
    # the machine state the empty directory above exists to exclude.
    FABRICATOR_PLUGIN_DIR_REAL="$hermetic_plugin_dir"
    trap 'rm -rf "$hermetic_plugin_dir" 2>/dev/null || true' EXIT
else
    # Fail fast on a missing variable. Without this the affected suite would merely SKIP, the run
    # would still be green, and the coverage would be silently gone Ã¢ÂÂ the exact failure mode this
    # script exists to prevent.
    missing=''
    for v in MSSQL_TESTDB_DSN MSSQL_TEST_SERVER MSSQL_TEST_CONNECTION_STRING MSSQL_TESTDB_URI \
             MSSQL_TEST_PASS MSSQL_BINCOLL_DSN FABRICATOR_S3_ENDPOINT FABRICATOR_S3_SQL_ENDPOINT \
             FABRICATOR_PLUGIN_DIR FABRICATOR_PLUGIN_ZIP; do
        eval "val=\${$v:-}"
        if [ -z "$val" ]; then
            missing="$missing $v"
        fi
    done
    if [ -n "$missing" ]; then
        echo "ERROR: the service tier needs these environment variables:$missing" >&2
        echo "       bring up docker/docker-compose.yml and run docker/provision.ps1 first." >&2
        exit 1
    fi
    # Kept aside because ONE suite (verify_plugin_install) is run against an EMPTY root instead; the loop
    # below restores this for every other suite. See the case there for why it cannot share.
    FABRICATOR_PLUGIN_DIR_REAL="$FABRICATOR_PLUGIN_DIR"
fi

suites=$("$SELECT_CMD")
if [ -z "$suites" ]; then
    echo "ERROR: no $TIER suites selected Ã¢ÂÂ $SELECT_CMD is broken." >&2
    exit 1
fi

# THE DOUBLED LEG. Since 2026-07-29 the PROVIDER NAME picks a default engine Ã¢ÂÂ 'delta' means DuckDB
# reads and writes the parquet bytes while engineered-wood owns the log, 'engineeredwooddelta' means
# EW's own codec does both. Most suites are pinned to whichever engine they are ABOUT. The ones listed
# below are not about an engine at all, so they interpolate ${DELTA_PROVIDER} and run once per engine.
# A divergence shows up as a failure in exactly one leg, which names the engine for you.
#
#   hermetic Ã¢ÂÂ write / transaction / update / delete semantics must come out IDENTICAL either way.
#   service  Ã¢ÂÂ the S3 suite, because object storage is where the two engines' file handling has
#              diverged most historically, and the flip moved the whole s3:// path onto the native
#              engine. Without this leg the codec path over object storage has NO coverage at all.
#
# Each entry below is "<suite><TAB><provider>". Suites that do not interpolate the variable simply
# ignore it, so the first leg can set it unconditionally.
case "$TIER" in
    hermetic)
        DOUBLED='test/verify_delta_catalog_write.test
test/verify_delta_catalog_transactions.test
test/verify_delta_catalog_update.test
test/verify_delta_catalog_delete.test
test/verify_merge_into.test'
        ;;
    service)
        DOUBLED='test/verify_delta_catalog_s3.test'
        ;;
esac

# A THIRD field: the host-query batch size that leg runs at (empty = the shipped default, one DataChunk).
# ONE suite gets a second leg at an ACCUMULATED batch size, and it is the only gate on the property that
# BudgetedStream splitting exists to deliver: batch size must no longer dictate Delta file size. Before the
# split this exact configuration FAILED verify_delta_clustered_optimize after 70 assertions — an oversized
# batch could only be cut BETWEEN batches, so delta.targetFileSize became unenforceable and an OPTIMIZE
# collapsed 80000 rows into ONE file. Both legs must pass: the default leg is the control, since "the
# accumulated leg passes" would be equally true of a build where accumulation had stopped working.
case "$TIER" in
    hermetic) ACCUMULATED='test/verify_delta_clustered_optimize.test' ;;
    *)        ACCUMULATED='' ;;
esac

entries=$(printf '%s\n' "$suites" | sed 's/$/\tdelta\t/')
while read -r d; do
    [ -z "$d" ] && continue
    # Guard against the doubled list drifting away from the selected set: a rename or a
    # reclassification would otherwise silently drop the second leg while the run stayed green.
    if ! printf '%s\n' "$suites" | grep -qxF "$d"; then
        echo "ERROR: doubled-leg suite '$d' is not in the $TIER set (renamed or reclassified?)." >&2
        exit 1
    fi
    entries="$entries
$(printf '%s\tengineeredwooddelta\t' "$d")"
done <<EOF
$DOUBLED
EOF
while read -r a; do
    [ -z "$a" ] && continue
    if ! printf '%s\n' "$suites" | grep -qxF "$a"; then
        echo "ERROR: accumulated-leg suite '$a' is not in the $TIER set (renamed or reclassified?)." >&2
        exit 1
    fi
    entries="$entries
$(printf '%s\tdelta\t122880' "$a")"
done <<EOF
$ACCUMULATED
EOF

expected=$(printf '%s\n' "$entries" | wc -l | tr -d ' ')
echo "Running $expected $TIER suite runs, one process each."
echo "  unittest: $UNITTEST"
echo "  managed : $MANAGED"
echo "  fixture : $FABRICATOR_DELTA_DIR"
echo

ran=0
failed=0
assertions=0
failures=''

# Snapshot the fixtures' git state so the check at the end can attribute a change to THIS RUN rather
# than to whatever the developer already had in progress. See the check for what it is guarding.
fixtures_before=''
have_git=0
if command -v git >/dev/null 2>&1 && git rev-parse --git-dir >/dev/null 2>&1; then
    have_git=1
    fixtures_before=$(git status --porcelain --untracked-files=all -- test/fixtures 2>/dev/null)
fi

while IFS="$(printf '\t')" read -r suite provider batchrows; do
    [ -z "$suite" ] && continue
    scratch=$(mktemp -d)
    export FABRICATOR_DELTA_WRITE_DIR="$scratch"
    export DELTA_PROVIDER="${provider:-delta}"

    # The accumulated leg (see ACCUMULATED above). The unset is load-bearing the same way as the two
    # thresholds below: a value left in the developer's shell would otherwise accumulate for EVERY suite,
    # so the run would not be testing the shipped default anywhere.
    if [ -n "$batchrows" ]; then
        export FABRICATOR_HOST_QUERY_BATCH_ROWS="$batchrows"
    else
        unset FABRICATOR_HOST_QUERY_BATCH_ROWS
    fi

    # Only label the non-default leg, so the common output stays as it was.
    label="$suite"
    [ "$DELTA_PROVIDER" != 'delta' ] && label="$suite [$DELTA_PROVIDER]"
    [ -n "$batchrows" ] && label="$suite [batch=$batchrows]"

    # THE UPDATE POST-IMAGE GROUPING NEEDS ITS THRESHOLD FORCED, or it ships with ZERO coverage.
    # DeltaReader.UpdateGroupBytes defaults to 64 MiB of Arrow data before an UPDATE's post-images are
    # written and dropped; no hermetic suite comes within two orders of magnitude of that, so every
    # statement here takes the single-group path and the grouped one is never entered. This suite Ã¢ÂÂ and
    # ONLY this suite Ã¢ÂÂ runs with the threshold at a single byte, making each read-back batch its own
    # group; its assertions are properties the grouping must not change, and it passes identically with
    # the default threshold (that equivalence is the point).
    # The unset is load-bearing in the other direction: without it a value already exported in the
    # developer's shell would silently group EVERY suite, so a run would not be testing the shipped
    # default.
    case "$suite" in
        *verify_delta_update_grouped.test) export FABRICATOR_DELTA_UPDATE_GROUP_BYTES=1 ;;
        *) unset FABRICATOR_DELTA_UPDATE_GROUP_BYTES ;;
    esac

    # THE BATCHED NATIVE DELTA READ likewise needs its threshold forced, for the mirror-image reason.
    # DeltaNativeReader.BatchPlan collapses a scan's deletion-vector-free files into ONE read_parquet from
    # 2 files up, so the shipped default IS exercised by every other delta suite here; what would go
    # untested is the batched path on a ONE-file scan, which is the shape most suites actually build. This
    # suite Ã¢ÂÂ and only this one Ã¢ÂÂ runs at 1, so every scan in it batches whatever is expressible, and
    # its gated sections (deletion vectors, rowid DML, partition columns, row tracking, id-mode mapping)
    # exercise the fallback in the same process. The unset is load-bearing the same way as above, with an
    # extra edge: a stray 0 in the developer's shell DISABLES batching everywhere, so a green run would be
    # testing only the old per-file loop while looking like full coverage.
    case "$suite" in
        *verify_delta_batched_read.test) export FABRICATOR_DELTA_BATCH_MIN_FILES=1 ;;
        *) unset FABRICATOR_DELTA_BATCH_MIN_FILES ;;
    esac

    # ⚠ verify_plugin_install NEEDS AN EMPTY PLUGIN ROOT, and every assertion in it is vacuous without
    # one. What it proves is that installing an archive makes a provider resolvable IN THAT SESSION, which
    # is only observable as a CHANGE: "unknown provider" before, the plugin's own error after. Pointed at
    # the tier's normal FABRICATOR_PLUGIN_DIR the plugin is ALREADY loaded, so the before-state assertions
    # fail and the after-state ones would have passed with the install doing nothing whatsoever.
    # The restore is an unconditional else-arm rather than an unset, because every OTHER suite in the tier
    # - verify_plugin above all - needs the real directory.
    case "$suite" in
        # /!\ verify_plugin_fluid USED TO BE HERE, given `build/plugins/fluid` and nothing else so that its
        # "exactly one loaded provider" assertion meant something. Fluid became a BUILT-IN provider assembly
        # on 2026-09-02 (published into the managed dir, discovered by name), so it needs no plugin root and
        # moved to the HERMETIC tier -- which the derived suite lists picked up on their own, since they
        # classify by the require-env directives a suite declares.
        *verify_plugin_install.test)
            # TWO roots: an empty one to install into, plus the COLLIDING TEST FIXTURE
            # (dotnet/Fabricator.CollidingPlugin, an IBackend claiming the first-party name 'sqlserver').
            # The refusal needs two assemblies claiming one name, which no manifest or install argument can
            # manufacture - so without this second root that behaviour is unreachable from SQL. Its output
            # path is FIXED by its csproj precisely so this line needs no TFM or RID in it.
            install_plugin_dir=$(mktemp -d)
            # RELATIVE on purpose: the managed side resolves a root with Path.GetFullPath against the PROCESS
            # working directory, and this script cd's to the repo root. An absolute "$PWD/..." is wrong under
            # Git Bash, where $PWD is an MSYS path (/d/repos/...) that .NET turns into a path under D:\d - which
            # reports as root_missing rather than failing, i.e. it looks like the fixture simply is not there.
            export FABRICATOR_PLUGIN_DIR="$install_plugin_dir,build/test-plugins/collide"
            ;;
        *)
            export FABRICATOR_PLUGIN_DIR="$FABRICATOR_PLUGIN_DIR_REAL"
            ;;
    esac

    output=$("$UNITTEST" --test-dir . "$suite" 2>&1)
    status=$?

    count=$(printf '%s' "$output" | grep -oE '[0-9]+ assertion' | grep -oE '[0-9]+' | head -1)
    [ -z "$count" ] && count=0

    if [ "$status" -ne 0 ]; then
        reason="exit status $status"
    elif printf '%s' "$output" | grep -q 'No tests ran'; then
        reason='the filter matched no test case (exits zero Ã¢ÂÂ would be a silent no-op)'
    elif printf '%s' "$output" | grep -qE 'skipped test'; then
        reason="skipped: $(printf '%s' "$output" | grep -A3 'Skipped tests' | tail -n +2 | tr '\n' ' ')"
    elif ! printf '%s' "$output" | grep -q 'All tests passed'; then
        reason='did not report "All tests passed"'
    else
        reason=''
    fi

    if [ -n "$reason" ]; then
        failed=$((failed + 1))
        failures="${failures}
  $label Ã¢ÂÂ $reason"
        printf 'FAIL  %-58s %s\n' "$label" "$reason"
        printf '%s\n' "$output" | tail -25
        echo "      (scratch kept for inspection: $scratch)"
    else
        ran=$((ran + 1))
        assertions=$((assertions + count))
        printf 'ok    %-58s %6s assertions\n' "$label" "$count"
        rm -rf "$scratch"
    fi
done <<EOF
$entries
EOF

echo
echo "================================================================"
echo "suite runs      : $expected"
echo "suites passed   : $ran"
echo "suites failed   : $failed"
echo "assertions      : $assertions"

if [ "$failed" -ne 0 ]; then
    echo "FAILURES:$failures"
    exit 1
fi
if [ "$ran" -ne "$expected" ]; then
    echo "ERROR: $ran of $expected suites ran." >&2
    exit 1
fi

# The in-repo fixtures are read-only reference data; a suite that writes into them dirties the working
# tree and Ã¢ÂÂ as happened on 2026-07-29 Ã¢ÂÂ gets its droppings COMMITTED by the next `git add`. That leak
# was invisible for a whole session: `verify_delta_catalog_constraints` attaches test/fixtures and
# attempts INSERTs that must fail, which wrote nothing while the codec was the default and started
# leaving one orphan .parquet per failed INSERT the moment `native_write` became it. Cheap to assert,
# so assert it. Compared against the pre-run snapshot, so a developer's own in-progress fixture edit
# is not reported as this run's doing. Skipped when git is unavailable or this is not a checkout.
if [ "$have_git" -eq 1 ]; then
    fixtures_after=$(git status --porcelain --untracked-files=all -- test/fixtures 2>/dev/null)
    if [ "$fixtures_after" != "$fixtures_before" ]; then
        echo "ERROR: this run modified test/fixtures Ã¢ÂÂ those are committed, read-only inputs." >&2
        echo "  before:${fixtures_before:+$(printf '\n%s' "$fixtures_before")}" >&2
        echo "  after :${fixtures_after:+$(printf '\n%s' "$fixtures_after")}" >&2
        echo "       find the suite that writes there and point it at FABRICATOR_DELTA_WRITE_DIR" >&2
        echo "       (or pin it to the codec engine, which writes nothing on a refused statement)." >&2
        exit 1
    fi
fi

# Floors on the SELECTED set (values set per tier at the top). Without these, a suite that quietly
# gains a `require-env` drops out of its tier and CI stays green with less coverage: `ran == expected`
# would still hold, because `expected` shrank too. Floors rather than equalities, so adding suites and
# assertions never breaks the build.
if [ "$expected" -lt "$MIN_SUITES" ]; then
    echo "ERROR: only $expected suite RUNS were selected, floor is $MIN_SUITES. A suite likely gained a" >&2
    echo "       require-env and silently left the $TIER set Ã¢ÂÂ or a doubled leg stopped being added." >&2
    echo "       Check $SELECT_CMD and the DOUBLED list above," >&2
    echo "       and if the drop is intended, lower MIN_SUITES here in the same commit." >&2
    exit 1
fi
if [ "$assertions" -lt "$MIN_ASSERTIONS" ]; then
    echo "ERROR: $assertions assertions ran, floor is $MIN_ASSERTIONS Ã¢ÂÂ coverage went backwards." >&2
    exit 1
fi

echo "ALL GREEN"
