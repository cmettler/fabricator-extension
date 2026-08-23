# The Delta native read path — as built, with every measurement

> **Moved verbatim out of `CLAUDE.md` (2026-08-23) to bound that file's size.** Nothing here was
> rewritten: these are the working records of the Delta read-path performance work, in the order it
> happened, with the wrong turns kept because most of them cost a wrong conclusion first.
>
> `CLAUDE.md` keeps a short entry pointing here plus the STANDING RULES this work produced — read
> those first; come here for the numbers, the mechanism and the traps.
>
> **The four things this doc is the authority on:**
> 1. the FOUR batched forms (partition-only / plain `schema`-map / union / full `union_by_name`) and
>    which shape routes to which;
> 2. every measured figure for a remote (Fabric/OneLake) scan — and which of them are STALE, because
>    several were re-taken after later fixes collected the cost they were measuring;
> 3. the DuckDB limits the forms are shaped by (the `schema`-map semantics, the field-id assertion,
>    the non-prefix-projection SIGSEGV);
> 4. the measurement RECIPE for the profiled query, so a future number is comparable.


- **THE NATIVE READER PREFERRED THE EXPENSIVE OF ITS THREE `read_parquet` PATHS — FIXED 2026-08-14 (C#-only,
  no ABI). MEASURED LIVE: the profiled query 77.19 → 46.72 s (−39%), the targeted span 34.4 → 0.50 s.**
  After the 291 → 77 s work (entry below), the biggest single span left was **34 s of 77** and it was NOT a
  `_delta_log` cost — it is O(active files) remote parquet FOOTER reads, paid because the scan took the
  wrong one of three paths.
  - **⚠ THE TOTALS ARE ONLY COMPARABLE BECAUSE THE OTHER TERM HAPPENED TO MATCH, AND THE FIRST RUN PROVED
    WHY THAT MUST BE CHECKED.** The after-run's log replay came in at **26.85 s** against the baseline's
    26 s, so 77.19 → 46.72 attributes cleanly. An EARLIER after-run gave 59.15 s — and its replay was
    **39.4 s**, so that pair differs in TWO variables and says nothing. ⚠ `ATTACH` varies even more wildly
    on this table (**3 / 4.7 / 22.3 s** across three runs, all Fabric-side), which is why the SELECT's own
    `Run Time` is the number and the wall clock is not. **Always read the replay span out of the log before
    comparing two totals.**

  **THE THREE PATHS** (`DeltaNativeReader.BatchPlan`: `Build` ~`:319`, `TryPlainForm` ~`:401`,
  `TryFullForm` ~`:555`):
  1. **FULL batch — `TryFullForm`.** ONE `read_parquet([EVERY file], union_by_name => true, filename => true,
     file_row_number => true)`, DVs bound as one `(filename, pos)` anti-join input and the per-file constants
     (global ordinal, partition values, `baseRowId`) as a second, both in `WITH … AS MATERIALIZED` CTEs. No
     loop left. **Declines on**: no columns / no table schema; **column mapping `id`** (a file's stored child
     names are its own vintage's and `union_by_name` matches struct interiors BY NAME — the measured
     silent-loss case); an unresolvable column; **any column containing a STRUCT**; an untypable partition
     value; and — easy to miss — **every requested column being a partition column** ("nothing to read from
     the files"), which is residual 2 below.
  2. **PLAIN batch — the `schema`-map form.** ONE `read_parquet([DV-FREE files], schema = map {…})`. **No
     virtual columns ⇒ no footer reads — the schema is DECLARED.** Requires `!wantRowId && !wantsTracking`,
     no PROJECTED partition column (it `return null`s itself — `schema` is refused with `hive_partitioning`),
     ≥ `min` DV-free files, every column resolvable and renderable as a CAST target. **PARTIAL BY DESIGN**:
     DV-carrying files go to the loop, so paths 2 and 3 run together on a mixed table.
  3. **PER-FILE loop — `StreamFiles(loopFiles, …)`.** Both the fallback and the remainder under (2). The ONLY
     path that can carry a per-file rowid/tracking fast-path filter, since one WHERE cannot express a
     per-file bound.
     - **⚠ SAY WHAT IT COSTS PRECISELY — it is TWO host queries per file, not one, and this file said "one"
       in two places (user-corrected 2026-08-17).** It is a C# `foreach (var f in loopFiles)`, each iteration
       in its own `Task.Run` gated by `SemaphoreSlim(prefetch)` — **default `prefetch = 1`, so STRICTLY
       SEQUENTIAL** — and each iteration issues (a) `ResolveFileMapping` → `ProbeFileNodes` →
       `parquet_schema('<that one file>')`, a footer probe that is **UNCACHED** (no memoization anywhere),
       then (b) `QueryFile` → the `FileSql` data read naming that single file, plus a bound DV input when its
       vector exceeds `DvLiteralMax`. So N looped files = **2N host queries, serial by default**.
     - ⚠ WHICH files: `loopFiles = batch?.LoopFiles ?? listing.Files` — ALL of them when there is no batch
       plan at all (id-mode column mapping, a partition-ONLY projection, a rowid or row-tracking FILTER, or
       `MIN_FILES=0`); only the DV-carrying ones when a partial plain plan covered the clean files; none when
       the union or full form covered everything.

  **⚠ THE FINDING: `TryFullForm` WAS TRIED FIRST, UNCONDITIONALLY** (`if (listing.Files.Count >= min)`), and
  the plain form was reached only if it DECLINED. So a plain projection over DV-free files with no rowid and
  no tracking — which path 2 handles perfectly and *cheaply* — got path 1 anyway, because path 1 succeeds.
  That is why the profiled query emitted `filename` + `file_row_number` it never used (`rowid=False`,
  `pruned=0`, no DV) and paid 34 s to unify 89 footers under `union_by_name`. **The cheap form was
  unreachable exactly on the shape it wins most** — which is the general lesson: an ordering that tries the
  MORE CAPABLE form first silently makes the cheaper one dead code wherever both apply.

  **⚠ AND THAT 34 s IS *TWO* O(files) FOOTER SWEEPS, NOT ONE — MEASURED 2026-08-14, correcting this entry's
  own first attribution.** `TryFullForm` calls **`PresentNames`** (`SELECT DISTINCT name FROM
  parquet_schema([every file])`, `DeltaNativeReader.cs:584`) to build its SQL, and `ProbeSchema` then runs
  that SQL with `LIMIT 0`; the log line the span is measured against (`delta native scan …`) is emitted
  AFTER BOTH. Timed on the 89 files themselves, each variant in its OWN process so no footer cache is
  shared:

  | probe | cold (2026-08-14) | **cold, RE-MEASURED 2026-08-17** |
  |---|---|---|
  | `SELECT DISTINCT name FROM parquet_schema([89])` — the `PresentNames` cost | **20.50 s** | **3.85 s** (1.49 s warm) |
  | full-form SQL + `LIMIT 0` — the `ProbeSchema` cost | **20.66 s** | ~1 s (reasoned — see below) |
  | plain-form `schema`-map SQL + `LIMIT 0` | **0.49 s** | — |
  | plain-form SQL + `LIMIT 1` | 15.15 s | — |
  | full-form SQL + `LIMIT 1` | 20.52 s | — |

  **⚠⚠ THE 2026-08-14 COLUMN IS SUPERSEDED — DO NOT QUOTE IT.** Re-taken live against the same 89 `his`
  files, the sweep is **3.85 s, not 20.50 s** (one file: 0.367 s), because the literal-glob echo and the
  size-seed collected most of that cost in between. The ⚠ two paragraphs down predicted exactly this and was
  never acted on, so BOTH the fusion and the `PresentNames` commits were argued from these stale figures and
  overstate their remote wins by roughly 6×. See their own entries for the corrected numbers.

  ⇒ ~230 ms per remote footer, and the two sweeps together (41 s cold) overrun the 34 s seen in-process, so
  DuckDB caches some footers between statements but nothing like all of them. **The "no footer reads — the
  schema is DECLARED" claim for the plain form is now MEASURED (0.49 vs 20.66, 42×), not reasoned.**
  ⚠ ~~The `LIMIT 1` rows are a puzzle deliberately left open~~ — **SOLVED 2026-08-15, and the hypothesis
  recorded here was WRONG**: the plain form's 0.49 → 15.15 s was not deferred FOOTER opens but our own
  `onelake://` VFS running a full recursive directory LIST per LITERAL path in Glob — DuckDB globs every
  input path when the lazy multi-file list expands at scan init (`LIMIT 0` never expands it). Fixed by
  echoing literal paths with zero IO (see the ✅ glob entry under the LIMIT fix below); ⚠ which also means
  these sweep figures OVERSTATE the footer share — `parquet_schema([all])` paid ~89 globs TOO, so re-measure
  before quoting the 41 s as footer cost. A full-table aggregate still costs about the SAME on both forms
  (21.99 s plain vs 18.86 s full, in-session).

  **CORRECTNESS ON THIS SHAPE IS MEASURED TOO, and one hazard I flagged does not exist.** Both forms over the
  same 89 files return **n = 659278 with all three column hashes byte-identical**. And although the plain
  form passes NO `hive_partitioning` flag where the full form explicitly disables it — on a table whose
  files really do live under `edwYear=2012/edwMonth=10/` — `DESCRIBE SELECT *` under the schema map returns
  **exactly the 3 declared columns**: a declared `schema` SUPPRESSES hive auto-detection, so no phantom
  column is injected. ⚠ The plain form's **zero-column (`COUNT(*)`) branch declares no map at all**
  (`DeltaNativeReader.cs:425`) and is therefore still exposed to auto-detection; it selects `1`, so an
  injected column is unreferenced — untested rather than safe.

  **THE CHANGE, AS BUILT**: the plain-form body was extracted out of `Build` into `TryPlainForm`, which is
  now attempted FIRST and preferred iff `plain.LoopFiles.Count == 0` — deriving "it serves everything" from
  the plan instead of restating the split rule, so the two cannot drift. Otherwise the full form as before,
  with the partial plain plan as the last fallback (exactly what `Build` returned before). Measured on the
  profiled query: 34.4 → 0.50 s, i.e. **77.19 → 46.72 s**; the dominant term is now the 26 s log replay.
  - **⚠ Trying plain FIRST is not merely a preference, it is strictly cheaper to ATTEMPT**: `TryPlainForm`
    is pure string work, while `TryFullForm` issues `PresentNames`. Before this the expensive-to-attempt
    form was the one tried first.
  - **⚠ THE WIN IS ALL AT BIND, AND AT EXECUTION THE PLAIN FORM MAY BE MARGINALLY SLOWER** — say so rather
    than calling the swap free. It CASTs each column to the DECLARED type where the full form takes the
    file's STORED type, and a full-table aggregate measured **21.99 s plain vs 18.86 s full** (in-session,
    so within noise, but the sign is consistent). It does not change the ranking: the full form pays ~41 s
    of bind before it reads anything, which dwarfs a ~3 s execution difference on this table.
  - **The MIXED case (some files DV-carrying) deliberately keeps the full form, and the reason is that the
    answer is STORAGE-DEPENDENT and unmeasured** — so the fix above does NOT reach a table with a single
    deletion vector on it. That is the largest thing left open here; see item 0 of the follow-on list below
    for the arithmetic and the proposed rule. Do not generalise the remote numbers above into it without
    measuring.
  - **⚠ PREFERRING A PATH MAKES ITS LATENT GAPS EVERYONE'S.** The two forms are not merely fast/slow
    variants: the plain form CASTs to the DECLARED Delta type while the full form takes each file's STORED
    type. On a type-widened table they would advertise different types — the tail note in `BatchPlan`'s
    remarks — and `typeWidening` is untested. Conversely the plain form HANDLES structs where the full form
    declines, so the swap is not uniformly a narrowing.
  - **The gate can only pin the ROUTING, not the answer** — both forms are correct, so a suite asserting
    rows would pass either way (and the mutant would survive). The observable is the `delta native batch:
    <sql>` Debug line through `duckdb_logs`: `schema = map` for a plain-eligible scan, `union_by_name` for a
    DV/rowid one. Same shape as `verify_delta_rename`'s engine assertion and `verify_delta_autocommit_pin`'s
    pin assertion. Gate `verify_delta_batched_read` 169 → **194** (§10a/b/c), **mutation-tested with two
    mutants, each killed at its OWN section**: restoring the old preference dies at §10a after 179 pass,
    and gating partitioned tables off the plain form dies at §10c after 189. Tier: hermetic
    **69/69 — 7152** (7127 + exactly the 25 new assertions ⇒ every other suite's count unchanged, which is
    the behaviour-preservation claim — both forms are correct, so a routing change must move NO answer).
    - **§10b is the load-bearing control** — a DV table must still route to `union_by_name`. Without it,
      "§10a took the plain form" would pass equally if the full form had stopped being reachable at all,
      i.e. if the batch had silently become plain-or-nothing.
    - ⚠ **§10c needed its OWN partitioned table, not §6's `parts`** — that one is UPDATEd and DELETEd from
      earlier in the suite, so by §10 it carries a DELETION VECTOR and routes to the full form for a reason
      that has nothing to do with partitioning. Written against it, the section would have asserted the
      opposite of what it says. Caught by the row count (59, not 60), not by review.

  **⚠ PARTITIONED-TABLE RESIDUALS — BOTH CLOSED 2026-08-17, so a partitioned table no longer has a shape
  that falls to the per-file loop for being partitioned. There are now FOUR batched forms, not three
  (partition-only / plain / union / full), and the routing turns on the PROJECTION rather than on the
  table:**
  1. **✅ A PROJECTION NAMING A PARTITION COLUMN NOW TAKES THE PLAIN FORM — BUILT 2026-08-17 (C#-only, no
     ABI).** It used to keep the FULL form, so `SELECT *` on a partitioned table paid the `union_by_name`
     bind — an O(N) footer sweep. The value is absent from the data files, so it comes from the log's
     per-file constants joined on `filename`: **the SAME fragment the full form already emitted**
     (`CAST(__fab_f."p0" AS <declared>) AS "p"`, same `TypeText`), lifted into the schema-map form, which
     `filename => true` permits. (`file_row_number` is what a VARCHAR-keyed map refuses — that is why
     deletion vectors still need their own branches, and why `schema` + `hive_partitioning` stay refused.)
     - **⚠ THE REAL WORK WAS THE CTE MERGE, not the join.** `TryUnionForm` appends the plain SELECT after a
       `WITH` of its OWN (the bound deletion vectors), and a second `WITH` nested inside a union does not
       parse. So the plain form can no longer own its prefix: `TryPlainParts` returns the pieces
       (`CoreSql` / `MetaCte` / `MetaView` / `PartCols`) and each consumer assembles — `PlainPlan` for the
       standalone form, the union merging both CTEs into ONE `WITH`.
     - **⚠ THE TWO BRANCHES PRODUCE THE PARTITION VALUE BY DIFFERENT MEANS AND MUST STILL AGREE**: the plain
       branch JOINS, each DV branch INLINES its own file's literal (`CAST('q0' AS VARCHAR)`) because a
       per-file constant needs no join. They share `TypeText`, which is what makes the `UNION ALL` line up.
     - **⚠ The bound-input invariant is why `MetaStream` is called with `withFileOrdinal: false` here** —
       it emits `fn` plus one column per PROJECTED partition column, every one of which the SQL reads. An
       unread bound column is the `duckdb_arrow_scan` non-prefix crash.
     - MEASURED: `SELECT p, count(*), sum(id) … GROUP BY p` ⇒ `schema = map` with the join (was
       `union_by_name`); a partitioned table carrying a BOUND deletion vector ⇒ ONE `WITH __fab_f …,
       __fab_d …` over a `UNION ALL`, right per-partition answers in both.
     - Gate `verify_delta_batched_read` 316 → **332**: §10c REWRITTEN (its second half pinned the exact
       OPPOSITE — "projecting a partition column keeps the full form" — so the two had to change together,
       and it now also asserts ZERO `union_by_name` for that table), plus a new §14 for the merged-CTE
       union. **Mutation-tested**: reverting the join dies at §10c after 213 pass; not merging the plain
       branch's CTE dies at **§6's `parts`** after 98 — that table is partitioned, DV-carrying and projects
       `p`, so the merge was already load-bearing for a PRE-EXISTING section, which §14 now documents.
     - ⚠ **THIS ITEM WAS MIS-PRICED TWICE IN ONE SITTING AND THE SEQUENCE IS THE LESSON.** After the fusion
       + one-file presence probe I wrote it down as "nearly worthless, ~230 ms" — having read *"the
       redundant sweeps are gone"* as *"the sweep is gone"*. The user's one question ("it uses
       union_by_name, which touches all files?") exposed it: the REAL query still binds `union_by_name`
       over every file, which is the O(N) footer read, and removing the `LIMIT 0` probe removed only a
       DUPLICATE of it. Re-measured then: bind-only `LIMIT 0`, remote 89 files — full form **20.66 s** vs
       schema-map **0.49 s**; local 150 page-cached files — **0.017 s vs 0.002 s**, linear in file count.
       Then a second correction, also the user's: the plain form's CAST caveat is about the DATA columns
       and is NOT partition-specific (the partition fragment is identical in both forms), and it is the
       same trade already accepted for every ordinary scan by the 2026-08-14 preference reversal.
     - **⚠ AND A THIRD MIS-PRICING, found by the live re-measurement 2026-08-17: the 20.66 s that finally
       justified this item is itself STALE.** Measured live that day, the FULL form's `delta table open` →
       scan-line span on those same 89 files is 11.13 s and is ~8–9 s LOG REPLAY, with the bind proper on
       the order of 1 s — the 89 footer opens now cost milliseconds each and happen after the bind. So what
       this item saves against the full form is roughly a second per scan plus the `PresentNames` probe,
       not 20. ⚠ Not separately A/B'd (both routes were measured, but not the same query through each), and
       the change stands on its own merits regardless: the plain form is strictly cheaper to ATTEMPT.
       Confirmed firing live — `cols=[edwYear,ISIN] batched=89` on the schema-map form with the join.
  2. **✅ A PROJECTION OF ONLY PARTITION COLUMNS NOW OPENS NO DATA FILE AT ALL — THE PARTITION-ONLY FORM,
     BUILT 2026-08-17 (C#-only, no ABI). MEASURED on an 8-file table: `batched=0` and 16 host queries →
     `batched=8`, ONE query, ZERO parquet IO.** `SELECT p, count(*)` used to decline at BOTH batched forms'
     matching "nothing to read from the files" guards (`TryFullForm`'s `entries.Count == 0`; and since
     residual 1, the plain form BUILDS the partition expression and then finds nothing left to declare in
     the `schema` map) and fall to the per-file loop — **2 host queries per file, serial at the default
     prefetch**: an UNCACHED `parquet_schema` footer probe it needs nothing from, then a data query whose
     only job is to produce the right NUMBER of rows. Both guards were refusing for the honest reason (there
     is no file to read) and then routing to the one path that reads every file anyway.
     - **`BatchPlan.TryPartitionOnlyForm`, tried FIRST in `Build`** — cheapest of the four and its
       applicability is DISJOINT from the others, so the order is about not doing the work twice rather than
       precedence. The plan is ONE bound input of one row per file (the partition values as raw VARCHAR plus
       `row_count`), expanded by a correlated **`range(__fab_f."row_count")`** — verified against DuckDB
       (`FROM t, range(t.n)` is a lateral table function; `n=0` emits nothing, and it streams rather than
       materialising a list per file: 3 × 2M expanded to 6M rows with no explosion). The generated SQL names
       **no parquet file and no table**, which is the feature and also the reason the gate cannot key its log
       assertions on the table name the way every other section here does.
     - **Live rows = `stats.numRecords` − `Dv.Length`, through ONE shared `LiveRowCount`** used by both the
       gate that decides WHICH files can be synthesized and the `MetaStream` that emits the counts — a
       disagreement between those two is a silently short or long answer, so they cannot be allowed to drift.
     - **⚠ DELETION VECTORS NEED NO BRANCH HERE — structurally simpler than the union form, not merely
       cheaper.** Every surviving row of a file carries the SAME partition values, so a DV changes only HOW
       MANY rows to emit: a SUBTRACTION, not a per-row anti-join. And the subtraction needs no trust — `Dv`
       is the materialized set of deleted POSITIONS (unique by construction, decoded from the roaring
       bitmap), so its length IS the cardinality rather than a second declared number. What IS trusted is
       `numRecords`, the writer's declared count — the same contract Delta's own `count(*)` optimization
       rests on.
     - **⚠ ID-MODE COLUMN MAPPING IS SERVED, and that is a genuine capability gain rather than a gate this
       form forgot.** Both other forms decline id mode because a name-keyed `schema` map may silently miss a
       column in a file we did not write; this form resolves no stored name and opens no file. Partition
       values come from the log's own dual physical/logical key match, which id mode satisfies
       (`physicalName` metadata is present there exactly as in name mode). MEASURED against ground truth.
     - **⚠ THE NO-STATS SPLIT IS REACHABLE, NOT THEORETICAL — and finding that is what turned a "reasoned,
       not measured" note into a gate.** A PENDING (uncommitted) file inside an explicit transaction carries
       no stats at all (`WithPendingFiles` constructs with only ordinal/uri/dv/partitionValues), so its count
       cannot come from the log and it keeps the per-file loop while the committed files synthesize —
       MEASURED `files=7 batched=6`, right answers. The split is per file, exactly like the plain form's DV
       split; a form that batched such a file anyway would emit ZERO rows for it, so the group VANISHES
       rather than being short.
     - **⚠ MY FIRST GROUND-TRUTH CONTROLS WERE VACUOUS, and it is the §12f trap in a new costume: DuckDB
       PRUNES an unused projected column.** `SELECT p, count(*) FROM (SELECT p, id FROM t)` drops `id`, so
       the "control that reads the files" took the partition-only form too and agreed with itself. The real
       control is a SECOND PROCESS with `FABRICATOR_DELTA_BATCH_MIN_FILES=0` (batching off ⇒ the loop reads
       the actual parquet), diffed byte-for-byte: **identical across DV / multi-column / NULL partition value
       / WHERE / id-mode / DISTINCT**, with the routing confirmed on both legs (0 vs 18 per-file queries).
     - **⚠ `MinFiles` still applies, so a partition-pruned query surviving to ONE file keeps the loop**
       (measured: `files=1 batched=0`). Consistent with the sibling forms and keeps
       `FABRICATOR_DELTA_BATCH_MIN_FILES=0` meaning "no batching at all", which suites rely on — but note
       this form is cheaper than the loop even at one file (1 query, no IO, vs 2 queries and a footer read),
       so the threshold is conservatism inherited rather than a measured break-even.
     - **⚠ Deliberately NOT extended to the zero-column `COUNT(*)` shape though the same synthesis serves
       it**: that already batches into ONE query (`SELECT 1 FROM read_parquet([…])`), so the win there is N
       footer reads rather than 2N host queries, on the single most-travelled shape in the reader. Worth
       measuring on its own; not smuggled in here.
     - **⚠⚠ MEASURED LIVE ON FABRIC 2026-08-17 — a same-window A/B against the exact pre-change route
       (`FABRICATOR_DELTA_BATCH_MIN_FILES=0` reproduces it, since this shape simply fell to the loop), with
       BYTE-IDENTICAL answers both legs. This is the one of the four read-path commits whose remote claim
       is measured rather than inherited.**

       | table | pre-change (loop) | partition-only | of which SCAN | per-file host queries |
       |---|---|---|---|---|
       | `his` — 89 files, 8 partitions | **33.11 s** | **9.35 s** | 24.49 s → **0.04 s** | 178 → 0 |
       | `frag` — 200 files, 2 DV | **44.82 s** | **1.60 s** | 42.71 s → **0.00 s** | 400 → 0 |

       - **The totals ARE comparable, and that was checked rather than assumed** (the standing rule): the
         `delta table open` → scan-line span is essentially EQUAL in both legs (his 9.30 vs 8.61 s; frag
         1.59 vs 2.10 s — network variance), so the entire difference sits in the scan term. What is left
         on `his` afterwards is the log replay, which this change does not touch.
       - **CORRECTNESS ON LIVE DATA, which is the half that matters most since the counts come from the LOG
         rather than the files**: `his`'s eight per-year counts sum to **659,278**, exactly the row count
         two file-READING queries returned in the same session; `frag` gives 2479+2500+2489+2500 =
         **9,968**, the recorded fixture total — i.e. **the deletion-vector subtraction is right on a real
         table with real DVs**, not just in the suite.
       - ⚠ `frag` is the bigger win (28×) because the loop is per-FILE: 200 files, and its 2 DV files cost
         the synthesis nothing extra.
     - Gate `verify_delta_batched_read` 332 → **399** (§15a–e), green at `MIN_FILES=1` AND the shipped
       default. **Mutation-tested with three mutants, each killed at its OWN section**: dropping the DV
       subtraction dies at §15b after 357 pass with the resurrection itself (**20 <> 13**, the 7 deleted rows
       back); batching a file the log cannot count dies at §15e after 387 with a wrong ROW COUNT (the pending
       group gone); and removing the form dies at §15a's routing after 340 — every row assertion before it
       passing, which is the right kill for a form whose ANSWERS are form-independent.
     - En route: **two doc sites residual 1 left STALE** were corrected — the class remarks' partition case
       and the plain form's summary both still said a projected partition column declines, which that commit
       had made false and did not touch.

  **THREE FURTHER OPPORTUNITIES, all INDEPENDENT of the change above and all aimed at the
  shapes the plain form can NEVER serve (rowid / DML / deletion vectors). ⚠ ALL THREE ARE NOW BUILT
  (2026-08-16/17) — read them as the record of how each was argued and measured, not as work outstanding;
  the header said "NOTHING BUILT" for a day after the last of them landed.** ⚠ Note the fix above NARROWED
  who pays: an ordinary read now takes the cheap form, so what is left is the DML path and
  any table carrying a deletion vector. That makes these worth LESS than the 34 s headline and still worth
  ~41 s of bind on every such scan against remote storage.

  0. **✅ THE MIXED CASE IS BUILT — THE UNION FORM, 2026-08-16 (C#-only, no ABI) — AND MEASURING IT ON frag
     EXPOSED A BIGGER FINDING THAT GATES IT REMOTELY.** `BatchPlan.TryUnionForm` composes the partial plain
     plan's own `schema`-map SQL over the clean files `UNION ALL` one `FileSql` branch per DV file (the
     loop's machinery verbatim) — ONE query, D single-file footer probes instead of the full form's `2 × N`,
     each branch KEEPING its `DvRangeCondition` prunable bound (a per-file predicate is inexpressible in one
     SELECT and perfectly expressible in one BRANCH). Routing: **LOCAL roots always; REMOTE roots only when
     a bare LIMIT was pushed** (`Build`'s `hasTop`); remote unlimited mixed keeps the full form.
     - **⚠ THE FINDING THAT FORCED THE REMOTE GATE — a per-op EXECUTION anomaly of our host-query path, not
       of the form (all same-window, live OneLake frag, 200 files / 2 DV):** union full-scan **120.4 s**
       (85.1 s cache-warm) vs the full form's **17.7 s** — while the BYTE-EQUIVALENT plain-branch SQL pasted
       RAW into duckdb.exe runs in **6.3 s** (and union_by_name raw 5.7 s). The per-IO instruments attribute
       it exactly: per-file opens at EXECUTION through a host query cost **~120–212 ms each, SERIAL** (fablog
       gap analysis: 808 evenly spaced ops ≈ the whole window; ~2 threads of CPU busy throughout), where the
       full form's ops cost **~29 ms** — same VFS, same op class, same minutes. **⚠ SPLIT THE SAME DAY into
       TWO terms by the size-seed + immutability work (see its ✅ entry below): the props round trips are
       RETIRED (120.4 → 44.7 s), and the residue is ATTRIBUTED — a DuckDB SCHEDULING pathology, streaming
       result × PhysicalUnion × threads>1 × slow FS.** Bisected through `fabricator_host_query` (the
       identical inner-query machinery, nothing of ours above it): the 198-file plain branch ALONE = 7.0 s;
       adding ONE CONSTANT branch (`UNION ALL SELECT CAST(0 AS BIGINT)` — no CTE, no second file) =
       **111.2 s / 102.7 s user CPU**; the same at `SET threads=1` = **6.0 s**; and `EXPLAIN ANALYZE` of
       the slow shape reports **Total Time: 3.38 s of operator work inside a 114 s statement** — ~110 s
       spent BETWEEN tasks in the scheduler, observed both as spin (102 s user) and as idle wait (4.5 s
       user) on different runs. Locally every op is instant so the gap never opens (0.17 s — the identical
       200-file mixed table reproduced locally scans in 0.17 s through the union). The union SQL, our VFS
       and the Arrow hand-back are all exonerated (the raw CLI run of the same SQL: 6.3 s — materialized
       results never show it); the full form dodges it by being a single source operator.
       - **⚠ `external_threads` MEASURED (user-asked, 2026-08-16) — oversubscription is ruled OUT and the
         mechanism sharpened to the PARALLELISM SPLIT.** This box: threads=20, external_threads=1.
         `external_threads=2` (right-sizing for our pumping drain thread): **124.3 s / 171 s user — no
         change**. `threads=16, external_threads=16` (**ZERO internal workers** — the fetch thread must
         execute everything itself): **97.0 s wall / 0.72 s user CPU, pure IDLE** — while `threads=1`
         (also zero workers, but split=1) runs the same shape in **6.0 s**. The only difference between
         those two zero-worker configurations is `NumberOfThreads()` = the pipeline SPLIT (16-way vs
         1-way) ⇒ the lost ~100 s tracks the PARALLEL SPLIT of the union's pipelines under a streaming
         result, independent of who executes the tasks: with workers it shows as spin, without them as
         the pumping thread idling on hand-offs nothing will pick up promptly.
       - **✅ LARGELY DEFUSED BY `SET SESSION preserve_insertion_order=false` — the user's pointer, BUILT
         + MEASURED 2026-08-16, and it is CORRECTNESS-NEUTRAL here rather than a tuning knob.** Every
         batched statement now carries it (`BatchPlan.Statement`). MEASURED live: the minimal union
         through `fabricator_host_query` **94.5 s → 7.5 s** (control in the SAME process: 94.5 s), and the
         real remote union scan **44.7 s → 13.7 s cold**. Mechanism from DuckDB's source:
         `PhysicalResultCollector::GetResultCollector` branches on
         `PhysicalPlanGenerator::PreserveInsertionOrder`, which for a plan with no `ORDER BY` is
         `INSERTION_ORDER` ⇒ decided by the setting (`plan_insert.cpp:47`); FALSE takes
         `PhysicalBufferedCollector(parallel=true)` and the stall does not occur.
         - **⚠ SESSION SCOPE IS LOAD-BEARING AND WAS VERIFIED, NOT ASSUMED.** The setting's declared
           target is `GLOBAL_DEFAULT` (`settings.hpp:1377`), so a BARE `SET` would change the whole
           database including the user's own connections — but `SET SESSION` is ACCEPTED and CONFINED:
           after the inner query ran with it, the outer connection still reported `true`. Our host query
           opens a FRESH connection per call, so the blast radius is one statement.
         - **⚠ It rides on the BATCH PLAN's statement, deliberately NOT in `MakeHostQueryStream`** — that
           is shared with the user-facing `fabricator_host_query` table function (whose result order is
           the caller's business) and the sort/COPY paths. Those are immune anyway (an explicit `ORDER BY`
           ⇒ `FIXED_ORDER`, which short-circuits before the setting is read), but narrow is the point.
         - **It is free for THIS reader specifically**: the contract is already that order across files is
           not preserved (DuckDB re-applies `ORDER BY` above the scan), and a bare `LIMIT` is pushed only
           when the spec carries NO order.
         - **⚠ THE GATE STILL STANDS, for a much smaller reason — say ranking, not pathology.** With the
           SET on BOTH forms, one window per order: union **13.7 s cold / ~1.05 s warm** vs full form
           **3.9 s cold / ~1.53 s warm**. The union now WINS warm and still loses cold ~3.5x; a
           first-touch scan pays cold. The cold residue is NOT props fetches (zero on both forms) and is
           unattributed.
       - **⚠ THE SYNC PROTOCOL, READ FROM SOURCE (2026-08-16, user-prompted) — and the buffer hypothesis
         MEASURED OUT.** The chain: producer pipelines → `PhysicalBufferedCollector::Sink` (one GLOBAL
         mutex per chunk) → `SimpleBufferedData` (bounded by `streaming_buffer_size`, default 1 MB,
         counted by ALLOCATION size — a 50-row chunk still allocates 2048-capacity vectors, so 200 tiny
         chunks ≈ 3 MB "fill" a 1 MB buffer holding 80 KB of data); a full-buffer producer parks in
         `blocked_sinks`, woken ONLY by the consumer's `UnblockSinks()`. The consumer (`Fetch` →
         `ReplenishBuffer`) executes tasks itself when its producer queue yields one; otherwise
         `Executor::ExecuteTask` returns BLOCKED (or RESULT_READY when the collector is blocked — mapped
         to BLOCKED too) and the fetch thread enters `Executor::WaitForTask`: a **20 ms timed poll**
         (`WAIT_TIME=20`) on `task_reschedule` — a CV that is **signaled only by task RESCHEDULES, never
         by a chunk arriving in the buffer** — with an IMMEDIATE-return (spin) branch when the collector
         is blocked, on top of `RescheduleTask`'s documented literal spinlock. ⇒ the two observed
         manifestations are one protocol: spin (immediate-return + lock churn) or idle (20 ms polls),
         both losing wall-clock per cycle because nothing wakes the consumer on data arrival.
         **`SET streaming_buffer_size='64MB'` inside the host query: 117.4 s — NO change** (measured;
         embedding a SET works — SendQuery accepts the multi-statement text), so back-pressure/buffer-full
         is NOT the driver; the wake-up protocol is. **The upstream patch shape this suggests:**
         `SimpleBufferedData::Append` (or the collector's sink) should signal the consumer's wait — a
         chunk-arrival notification — which would fix idle-mode outright; the collector-blocked spin
         branch needs its own look. Offer-sized, in DuckDB's own vocabulary.
       - **⚠ THE PRE-UNION ROUTE WAS PROBED AS A BETTER REMOTE FALLBACK AND IS REJECTED — MEASURED
         2026-08-16.** "Plain batch + per-file loop for the DV files" is the one alternative that keeps the
         cheap declared-schema bind AND structurally cannot hit the pathology (each of its 1+D queries has
         a SINGLE source), so it looked like the obvious remote answer. Probed by making `TryFullForm`
         decline whenever a partial plain plan exists; same-window A/B on frag in BOTH orders, with a
         rowid projection as the in-process discriminator that forces the full form: pre-union
         **5.91 s cold / ~0.87 s warm** vs the full form's **4.71 s cold / ~0.97 s warm** — ~25% WORSE cold
         (3 host queries + D per-file footer probes, serialized at prefetch=1) and identical warm. ⚠ An
         EARLIER cross-process reading suggested the opposite (pre-union 1.1 s vs full 3.2 s "repeat");
         those were hours apart on a variable network and are NOT comparable — the same-window control is
         what settled it, and it is the trap this file keeps recording. ⚠ One shape only (200 tiny files,
         D=2); a table with many DV files pays D queries here and the balance could move.
       **Upstream-shaped**: a stock repro needs a streaming result (python `record_batch()` / C API) over
       `UNION ALL` with a slow-per-op FS, reporting threads/external_threads (+ the protocol reading
       above). Until it is fixed or the host query can side-step it (a materialized inner result would
       trade away bounded memory — not acceptable as a blanket change), the gate stands.
     - **⚠ IT ALSO RE-PRICES THE SHIPPED PLAIN-FORM PREFERENCE, unmeasured and worth a check**: a CLEAN
       many-tiny-file remote table routes to the plain form (2026-08-14) and pays the same per-op execution
       cost N times — `his` (89 big files) measured a clear win (bind sweeps dominated), but a frag-like
       200-tiny-file CLEAN table may be slower plain than full. Untested — frag always has its 2 DVs.
     - **The LIMIT exception is measured, not hoped**: frag `LIMIT 1` via union = **3.65 s / 10 opens**,
       where the full form pays BOTH O(N) sweeps before its first row — the profiled-query shape.
     - **Bound DVs cross ONCE**: vectors above `DvLiteralMax` ride one shared `(fn, pos)` input behind a
       `WITH __fab_d AS MATERIALIZED` CTE (materialization is what makes several branches scanning one
       single-use stream sound), each branch anti-joining its own filename SLICE with the fn as a LITERAL —
       a branch knows its file, so no `filename => true` is needed and the VARCHAR map is untouched. Small
       vectors stay inline; both CTE columns are read by every referencing branch (the
       non-prefix-projection invariant).
     - **Eligibility = the plain form's** (TryUnionForm is only called on its PARTIAL result), any
       unbuildable branch falls back to the full form; ⚠ a table where EVERY file carries a DV still takes
       the full form — the plain form needs ≥ MIN_FILES clean files to exist at all. The rowid/DML and
       row-tracking-FILTER shapes keep their existing routes.
     - Gate `verify_delta_batched_read` 219 → **251, green at MIN_FILES=1 AND the shipped default** (local
       FS, so the union fires there) — the rdvx over-exclusion section needed its OWN 4-file table for
       exactly that duality (a third DV file on route_dv leaves one clean file and re-routes everything
       after it at the default). **Mutation-tested, both killed at value-bearing assertions**: reversing
       the preference dies at §10b's routing (line 675) after 186 pass; dropping the filename slice dies at
       §8's post-UPDATE row check (line 500) after 149 — a genuine cross-file over-exclusion, caught even
       before the purpose-built section.
     - ⚠ **`OPTIMIZE` remains the user-side remedy remotely** (fewer files, no DV branches at all). ⚠ The
       `COUNT(*)` observable caveat stands: the zero-column union branch emits `SELECT 1`, no map — use a
       real projection when probing routing.
  1. **✅ `ProbeSchema` IS FUSED WITH THE REAL QUERY — BUILT 2026-08-16 (C#-only, no ABI).** The batch SQL was
     bound TWICE: a throwaway `LIMIT 0` in `ProbeSchema` to learn the schema, then the real query inside the
     pump. It is now opened ONCE in `Read` — `Host.Query` returns a lazily-streaming result whose `.Schema` is
     available before a row is fetched — and the OPEN stream is handed to `StreamFiles` to drain. The plan's
     other two predictions held: `BatchPlan.Inputs`' factory reason is DELETED (its doc now says the factory
     is kept because it is harmless, not because anything needs it), and the bound Arrow inputs are built once
     instead of twice.
     - **⚠ THE PLAN'S ONE WARNING — "mind the double-dispose against `DrainAsync`'s `using`" — NAMED THE RIGHT
       SEAM AND THE WRONG FAILURE, and the wrong one is far worse. It is not double-dispose (an `Interlocked`
       exchange handles that); it is DISPOSING WHILE ANOTHER THREAD READS.** `StreamFiles` does NOT join its
       pump on teardown: it awaits the pump only after its `await foreach` runs to completion, so a consumer
       that stops EARLY — which is exactly what a pushed `LIMIT` produces — abandons a pump that keeps
       draining in the background. Releasing the pre-opened query from `AsyncEnumerableArrowStream`'s `owner`
       then frees a DuckDB result the pump is inside `ReadNextRecordBatchAsync` on.
     - **MEASURED, and it presents as nothing at all**: `verify_delta_batched_read` died at §11b — the union
       form under a pushed `LIMIT 5` — with **no assertion output, no error, no stack**, exit
       `0xC0000374 STATUS_HEAP_CORRUPTION`. Diagnosed by BISECTING THE SUITE BY TRUNCATION (head -N; OK at
       884, crash at 891) rather than by reading, then CONFIRMED by a one-line experiment before fixing
       anything: passing `owner: null` made it 295/295 green, which is what proved the consumer-side release
       was the killer. ⚠ It did NOT reproduce standalone — the same table and query outside the suite passed
       — so a "works on my repro" check would have shipped it.
     - **The fix is a HAND-OFF, not a lock**: `BatchQueryOwner.Claim()` is taken by the pump, after which the
       consumer-side `Dispose` is a NO-OP; the owner then only covers the one case the pump cannot — nothing
       ever enumerated the stream, so the iterator body never ran and no `finally` of its exists either.
       ⚠ **`Claim()` must be called in the ITERATOR BODY, not inside the pump task**: the body runs
       synchronously within the first `MoveNextAsync`, so it strictly precedes any teardown, whereas claiming
       inside `Task.Run` races a consumer that abandons before the pump is scheduled.
     - ⚠ **What it deliberately does NOT fix: the orphaned pump.** On early abandonment the pump keeps
       draining and releases the query whenever it finishes — and if the bounded channel fills first, never.
       That leak is PRE-EXISTING and shared with every per-file query on this path; fixing it means joining
       the pump on teardown (cancel, drain, await), a teardown redesign, not something to smuggle into a
       bind-count optimization. The rule enforced here is only that it must not be a CRASH.
     - **⚠ THE WIN IS REMOTE-ONLY, and the local A/B says so rather than hiding it.** 150 local files, full
       form, 6 scans each build: real medians ~0.355 s (fused) vs ~0.338 s (before), user CPU ~0.35 vs ~0.34
       — INDISTINGUISHABLE, exactly as theory predicts, since the second bind's footers came from the OS page
       cache. ⚠ An earlier 3-run sample looked like CPU had HALVED (0.30 vs 0.61); repeating it showed the
       pre-fusion figures swinging 0.31–0.78 on their own. **A CPU "improvement" from three samples is
       noise.** The prize was stated as the already-measured remote bind — 20.66 s for an 89-file full-form
       scan at ~230 ms per remote footer.
     - **⚠⚠ RE-MEASURED LIVE 2026-08-17, AND THE 20.66 s IS STALE FOR THE SAME REASON AS ITEM 2's 20.50 s
       (the glob echo + size-seed collected most of it first). The duplicate bind was worth ~1 s, not ~20.**
       A real full-form scan (`his`, 89 files, rowid projected AND the data column genuinely used — see the
       trap below) totals **11.48 s**, of which `delta table open` → the scan line is **11.13 s** and the
       actual read is ~0.35 s. Decomposing that 11.13 s from the log: **the DELTA LOG REPLAY is ~8–9 s of
       it** (a 3.00 s `_delta_log` LIST, 1.34 s `_last_checkpoint`, 1.96 s checkpoint open+read, then 51
       commit JSONs), the PresentNames one-file probe 0.33 s, and the `union_by_name` bind proper on the
       order of 1 s — because the 89 file opens now cost ~milliseconds each (the size-seed's zero-IO opens)
       and happen lazily AFTER the bind, not during it.
       ⇒ **the dominant remaining cost of a full-form remote scan is the log replay, not the bind**, which
       is what `delta.logRetentionDuration` + `cat.delta.checkpoint()` address and no batch-plan work can.
       ⚠ The ~1 s is REASONED from that decomposition, not measured directly: the removed probe was a
       `LIMIT 0` of SQL that references bound-input views, so it cannot be replayed standalone.
     - **⚠ THE MEASUREMENT ITSELF NEEDED TWO ATTEMPTS, and the first was VOID in the now-familiar way.**
       `SELECT count(*), max(r) FROM (SELECT rowid AS r, isin FROM his)` does NOT reach the full form:
       DuckDB PRUNES `isin` (neither aggregate needs it), leaving `cols=[] rowid=True` — the count-via-rowid
       plan, which the full form declines, so it looped and I nearly recorded 5.3 s as "the full form".
       The data column must be CONSUMED (`count(DISTINCT isin)`), which is the §12f/§13a trap for the third
       time. **Always read `cols=`/`batched=` off the scan line before believing a form was measured.**
     - Gates: hermetic **69/69 — 7314** and service **50/50 — 2028**, both identical to the TopN commit ⇒
       behaviour-preserving. **Mutation-tested**: removing `Claim()` reproduces the heap corruption at §11b
       instantly. §11b now carries a comment saying it is that gate — nothing in what it ASSERTS reveals it,
       and a bound deletion vector is what makes the crash reproduce reliably there rather than at §11a.
  2. **✅ `PresentNames` ASKS ONE FILE — BUILT 2026-08-17 (C#-only, no ABI).** The full form must know which
     stored names exist in SOME file (`union_by_name` can only produce a column at least one file carries),
     and that was a `parquet_schema([EVERY file])` — an O(files) FOOTER sweep, MEASURED 20.50 s over 89 remote
     files and the larger half of that form's bind cost. Now the highest-ordinal file is probed first and the
     sweep runs only when it does not carry everything the caller will look up. Sound because a name found in
     ONE file IS present, full stop; a name ABSENT from one file proves nothing, so the early exit requires
     EVERY queried name to be present.
     - **⚠ THE PLAN SAID "if it carries every wanted column, done" AND *wanted* WAS THE WRONG SET — the full
       form has TWO consumers of the presence set and the second is read 50 lines further down.** Besides the
       data columns' NULL-backfill, `present` decides whether a materialized `__delta_row_id` is READ or a new
       id is DERIVED from `baseRowId + file_row_number`. A materialized column is written by a REWRITE and not
       by a plain append, so on a row-tracking table it lives in some files and not others. **MEASURED with a
       one-file answer allowed there: the 5 rewritten rows came back as ids `[20,21,22,23,24]` instead of
       their original `[0,1,2,3,4]`, range `0..34` → `5..34`** — a rewritten row losing exactly the identity
       row tracking exists to preserve.
     - **⚠ AND THE OBVIOUS FIX (query the tracking names too) IS SOUND BUT WAS REJECTED, on a fact worth
       keeping: `Ordinal` IS NOT COMMIT RECENCY.** It is engineered-wood's active-file ordering, and MEASURED,
       the SAME three-file fixture put the rewritten file last in one session and the appended file last in
       another — so that spelling leaves the routing to luck in exactly the case where being wrong is a wrong
       ANSWER, and makes the behaviour untestable (the gate's kill would fire only on the unlucky ordinal). So
       a scan projecting a row-tracking virtual column **DECLINES the fast path outright** (`queried = null`).
       Deterministic, gateable, and it costs nothing real: the full form's usual callers — rowid/DML,
       deletion vectors, partitions — project no tracking column at all.
     - Gate `verify_delta_batched_read` 295 → **316** (§13a uniform ⇒ one file answers / §13b a column ADDED
       after every file ⇒ sweep + NULL backfill / §13c row tracking ⇒ always sweeps, plus id stability), green
       at `MIN_FILES=1` AND the default, and repeat-stable. **Mutation-tested, each killed at its own
       section**: allowing the fast path for tracking scans dies at §13c's routing after 314 pass; never
       offering it dies at §13a after 297.
       - ⚠ **§13c needs BOTH its assertions and neither is redundant.** The ROUTING one is the deterministic
         kill; the id-stability one is the semantic backstop — under the mutant it PASSED here, because this
         session's ordinal happened to pick a file that did carry the materialized column. Which is the whole
         argument for declining, restated as evidence.
       - ⚠ **Two gate-writing traps, both found by the section failing against them**, and both are about
         reaching the full form at all: the rowid must be PROJECTED and never FILTERED (a rowid predicate
         makes `BatchPlan` decline both batch forms and loop), and a DATA column must be genuinely USED —
         a rowid-only projection is the count-via-rowid plan (`cols=[] rowid=True`), which the full form
         declines, so the first draft asserted a presence probe that never ran.
     - ⚠ Like item 1 the win is REMOTE-ONLY: locally the swept footers come from the OS page cache.
     - **⚠⚠ RE-MEASURED LIVE 2026-08-17, AND IT CORRECTS THIS ENTRY'S OWN HEADLINE BY 6×. The sweep costs
       3.85 s cold, not 20.50 s — so the saving is ~3.5 s per full-form scan, not ~20.** Measured directly
       against the very 89 `his` files this reader lists (`SELECT count(DISTINCT name) FROM
       parquet_schema([…])`, fresh process): **all 89 = 3.849 s cold / 1.487 s warm; ONE file = 0.367 s.**
       The fast path is visible in the log as `delta native present: one file of 89 answered 1 names`
       (0.33 s in situ).
       - **The 20.50 s was never wrong — it was STALE, and CLAUDE.md had already warned exactly this** (the
         literal-glob entry: *"these sweep figures OVERSTATE the footer share — `parquet_schema([all])` paid
         ~89 globs TOO, so re-measure before quoting the 41 s as footer cost"*). The glob echo and the
         size-seed removed most of that cost BEFORE this commit was written, so this commit was argued
         against a baseline two earlier fixes had already collected. **The standing lesson: a figure quoted
         from an earlier entry is a measurement with an expiry date — re-take it in the same session you
         argue from it.**
       - It remains a real win and a strictly cheaper probe; only the SIZE of the claim was wrong.
     - ⚠ **The rejected alternative STILL stands, and is worth restating because it stays tempting:** do NOT
       build the SQL optimistically and recover from the binder error. That is error-text matching — the
       wrong instinct this file already records (the `errno` lesson) — and it doubles the latency of a
       genuinely failing scan.

  **✅ THE "LOG REPLAY" WAS NEVER THE COMMIT JSONs — IT WAS THE CHECKPOINT PARQUET READ AS ~63 MICRO-GETs,
  FIXED 2026-08-16 (C#-only): `OpenReadAsync` files ≤ 16 MB download WHOLE on first read. THE PROFILED
  QUERY IS NOW 8.8 s (was 46.72 baseline, 291 at the start).** Every OneLake open's 15–50 s span — recorded
  since 2026-08-13 as "the one log replay", priced PER COMMIT, and the basis of checkpoint-interval
  advice — was engineered-wood's parquet reader consuming the checkpoint parquet through
  `OneLakeRandomAccessFile` as SEQUENTIAL ranged GETs of 8–7096 bytes at ~180 ms each (footer length,
  footer, then every tiny column chunk its own HTTPS round trip; a 25 KB checkpoint = 63 GETs ≈ 12 s).
  Commit JSONs read at **2–6 ms each** on the kept-alive connection — never the term. Found in two
  instrument steps, each killing the previous attribution: the new `Fabricator.Adls.Fs` per-IO lines
  showed a 205-commit open doing 2 LISTs + `_last_checkpoint` + ONE checkpoint open + ZERO JSONs and then
  ~13 s of silence, until the one uninstrumented method (`ReadAsync`) got its line. MEASURED live A/B:
  fixture open 15.3 → **2.4 s** / statement 30.6 → **13.6 s** (adls reads 63 → 2, results identical); the
  `his` LIMIT 1 = **8.8 s**, its open now 2 LISTs (~2.2 s over a ~1900-file log dir) + one 1.7 s
  checkpoint download + 51 JSONs ≈ 0.3 s. ⚠ Consequences for the record: every "replay span" number in
  the entries above is really checkpoint-read cost and does NOT scale with commit count the way the prose
  implies; the remaining open cost on `his` is the LIST (log-dir file count ⇒ `delta.logRetentionDuration`
  + cleanup is what shrinks it) + the checkpoint download (size-proportional). ⚠ The scan side's residual
  is now the echo'd-glob props-opens (measured **1000 opens for a 198-file scan**, ~5/file — the known
  echo trade); a per-path props cache or a size side-channel is the recorded follow-on.
  **⚠ THE USER'S MECHANISM HYPOTHESIS (2026-08-16, plausible, UNVERIFIED): those ~5 props-opens per file
  are DuckDB's ExternalFileCache VALIDATION** — the cache keys cached ranges on etag/last_modified, which
  rode the glob's `extended_info` for free BEFORE the echo fix (opens then cost nothing) and now must be
  re-established per open. If true, two design consequences: a per-path props CACHE must not break
  invalidation for a genuinely overwritten file (Delta DATA files are immutable — UUID names — but the
  VFS is generic); and the first step is to MEASURE whether an open with extended_info already present
  skips the props fetch (the pre-echo behaviour says yes) and whether DuckDB validates on size alone —
  the Delta snapshot's AddFile carries size, not etag, so a snapshot side-channel covers size only.
  - **✅ BUILT + MEASURED THE SAME DAY — form (a), the size seed + the iceberg flag (2026-08-16, C# +
    one C++ Glob key, no ABI).** The contract, found by the user in duckdb-iceberg's multi-file reader:
    per `OpenFileInfo.extended_info`, `validate_external_file_cache = false` + dummy identity — justified
    by *"files managed by Iceberg are never modified"*, which holds identically for Delta DATA files.
    As built: `DeltaNativeReader.Read` seeds a bounded (uri → size) side table
    (`OneLakeForwardFs.SeedKnownSize`) from the snapshot's AddFiles; the literal-glob ECHO consults it and
    emits `size` + `"immutable":true`; the C++ `Glob` turns that into `extended_info["file_size"]` (the
    existing `OpenFileExtended` then skips the props fetch — the managed known-size open does NO IO) plus
    `options["validate_external_file_cache"] = false`.
    - **⚠ THE FLAG IS LOAD-BEARING BESIDE THE SIZE, NOT DECORATION — from DuckDB's own source**:
      `ExternalFileCache::IsValid` (external_file_cache.cpp:116) compares VERSION TAGS whenever EITHER
      side has one, and our opens have MIXED identity (listing-fed/bare opens carry the real etag, seeded
      echo opens an empty one) — so the size seeding ALONE would have made cached ranges get judged
      invalid and silently dropped + re-read. NO_VALIDATION removes the comparison. The per-file override
      is read by `ExternalFileCacheUtil::GetCacheValidationMode`.
    - **MEASURED on frag**: props fetches per scan **~1000 → 0**; full mixed scan **4.7 s cold / 3.2 s
      repeat with ZERO re-opens** (was 13.6 s at yesterday's best, 17.7–29.9 s in today's A/Bs);
      LIMIT 1 = 3.8 s. The frag table has now gone 30.6 → 13.6 → 4.7 s across three days' fixes.
    - **⚠ IT DOES NOT LIFT THE UNION FORM'S REMOTE GATE — measured by disabling the gate on this build**:
      cold union 44.7 s wall / 42.6 s user CPU vs the full form's 4.7 s / 2.9 s on IDENTICAL IO (200
      zero-IO opens + 200 reads each). The anomaly's props term is retired (120.4 → 44.7 s); the residue
      is ATTRIBUTED the same day — the streaming-result × PhysicalUnion scheduling pathology (see the
      mixed-case item 0 for the full bisection: one constant branch 7.0 → 111.2 s, threads=1 → 6.0 s,
      EXPLAIN ANALYZE Total Time 3.38 s inside a 114 s statement). DuckDB-side; supersedes the
      SDK-asymmetry items as the lead for lifting the gate.
    - (b) — the C++ `MultiFileList` carrying OpenFileInfos straight from the snapshot, the architecture
      of the never-adopted `fabricator_delta_mfr.cpp` spike and of duckdb-iceberg itself — remains the <!-- check-docs:ignore (REMOVED at ABI v75; naming it IS the point) -->
      durable form and would retire the SQL-string file lists + glob round trip entirely. Unbuilt.
  **⚠ USER IDEA, RECORDED WITH ITS CHEAPER SIBLINGS (2026-08-16, "just an idea we could measure"): expose
  DuckDB's HTTP stack to C# (wrap as an HttpMessageHandler under the Azure SDK transport) so managed FS
  requests share DuckDB's HTTP settings/caching.** Honest pricing: DuckDB's range caching is
  `ExternalFileCache` at the VFS layer (an HTTP shim would NOT inherit it), the HTTP util is C++-internal
  (new vtable entries + streaming/header marshaling), and it is SYNC-only (a thread pinned per in-flight
  request under the async SDK). Measure these FIRST, in order: (1) the UNEXPLAINED 180 ms-vs-3 ms
  asymmetry — sequential ranged `ReadAsync` on ONE DataLakeFileClient cost ~180 ms/request while
  `ReadContentAsync` on fresh clients cost 2–6 ms; if ranged reads drop/renegotiate the connection
  (response-stream disposal?), an SDK transport tweak wins with no shim; (2) the EXISTING host-FS bridge
  already routes READS through DuckDB's stack (`DuckDbTableFileSystem` over abfss ⇒ ExternalFileCache +
  DuckDB's TLS/proxy settings, zero new ABI) — a hybrid (reads via host FS, commit primitives via the
  direct SDK for atomicity) is most of the shim's benefit at a fraction of the cost; (3) only then the
  shim. The settings-parity argument is the strongest half (the MinIO self-signed saga:
  `enable_curl_server_cert_verification` never reaches the C# SDK's trust).
  ⚠ The buffered
  read deliberately keeps TRUE ranged reads above 16 MB (a column-pruned read of a big file must not
  download all of it); the codec engine's DATA-file reads ride the same class only on non-native attaches.
  ⚠ Upstream-offer candidate: a buffering/prefetching `IRandomAccessFile` wrapper in EW itself — their
  `LocalTableFileSystem` is free locally, so upstream never felt the remote chattiness.
  - **THE `frag` FIXTURE (OneLake `lake.dbo.frag`, persists)**: partitioned (4 × `p`), 205 commits, schema
    changepoints at v~82 (`extra INT`) and v~153 (`extra2 VARCHAR`), 2 DV files (DELETEs of id 120–130,
    5000–5020), 9968 rows / sum(id) 49888415 / 200 data files. Built for the union-fragment work; already
    measured: mixed-DV routes to `union_by_name` (full form), a pruned scan whose DV files fall out routes
    to `schema = map` LIVE, and the day-old `cat.delta.checkpoint()` was validated END TO END on it —
    written (v204) and CONSUMED (later opens replay from `204.checkpoint.parquet`). ⚠ Its automatic
    interval-10 checkpoints mean it CANNOT demonstrate the manual checkpoint's replay win — that story
    needs a table with a large interval and a long uncheckpointed tail.

  **THE 2026-08-15 RE-MEASUREMENT + THE UNION-FRAGMENT DESIGN REVIEW (user-driven; LIMIT fix built the
  same day — see its ✅ above; the rest is assessed, NOTHING ELSE BUILT).**
  - **Live re-measure of the profiled query: 71.0 s vs the 46.72 s baseline — NOT COMPARABLE, and the span
    rule is what says so.** The one log replay was 45.0 s vs the baseline's 26 s and the read 25.5 vs
    ~14 s, on an IDENTICAL table (still v1850, 89 active files, `pruned=0`) ⇒ pure Fabric/network variance,
    consistent with the recorded 3/4.7/22.3 s ATTACH spread. Routing HELD: plain form (`schema = map`),
    `batched=89`, `grep -c "delta fs"` = 1, `colmap=none` (the table is not column-mapped at all). The
    extension-side spans (probes + plan build) total ~0.6 s of 71 — for this query shape nothing is left
    to win inside `BatchPlan`; the money was the replay and the unlimited read (the latter now fixed).
  - **EXPLAIN probes (local, all measured): filter pushdown SURVIVES rename and the schema map, and I had
    claimed otherwise.** An alias rename AND a schema-map rename both land as a plain table filter in the
    scan (`Filters: c=5`); a TYPE-CHANGING map (int32 file, BIGINT declared) STILL yields the plain filter —
    the cast is internal to `read_parquet`, invisible to the planner; filters penetrate `UNION ALL` into
    every branch; an explicit SQL-level `CAST` in a projection becomes an EXPRESSION filter pushed into the
    scan (`Filters: (CAST(a AS BIGINT) = 5)`) — scan-level, but whether an expression filter zonemap-prunes
    row groups is UNVERIFIED.
  - **The union-per-SCHEMA-EPOCH algorithm (user-proposed), assessed — right direction, two corrections
    that make it SIMPLER:** (a) the epoch axis buys nothing — `physicalName` is assigned once and never
    changes, the map casts per file, `default_value` backfills added columns, extra stored columns are
    ignored, so ONE schema map already collapses every schema vintage (and deriving changepoints would
    cost a metaData replay the snapshot does not retain); (b) **only a MULTI-file `read_parquet` with a
    DECLARED schema avoids the bind-time footer sweep** — per-file/small fragments re-pay it (~230 ms each
    remotely; the 0.4 ms/branch union number was LOCAL), and a bare multi-file call without a map takes the
    FIRST file's schema and errors on the legal missing-nullable-column case. Useful fragment axes are
    therefore DV-carrying files (need `file_row_number`; D footers by construction — which RESOLVES open
    item 0's storage dependence) and, only until the `filename` join lands, partition constants. **✅ THAT
    CLAUSE IS NOW SPENT — the `filename` join landed 2026-08-17 (residual 1 above), so partition constants
    are NO LONGER a fragment axis at all: they ride the schema map through the join, and the only surviving
    axis is the DV-carrying file. The proposal is therefore fully realised in its corrected form.** Net target:
    plain-map branch (all clean files, one call) UNION ALL per-DV-file branches (anti-join + the prunable
    bound the full form forfeits). ⚠ Fragment-per-partition is OFF the table (user + me agreeing: explodes
    on high-cardinality partitioning; the `filename` join under the map supersedes it). **✅ THE NET TARGET
    IS BUILT — 2026-08-16, `BatchPlan.TryUnionForm` (local always; remote gated to pushed-LIMIT scans by the
    per-op execution anomaly the frag measurement exposed — the mixed-case item 0 above carries the full
    record, numbers, gates and mutants); the `frag` fixture served as planned and then some.**
  - **✅ `cat.delta.checkpoint('schema.table')` — BUILT 2026-08-16 (user-queued the day before; C#-only, no
    ABI).** The SEVENTH `delta.*` catalog-bound function: writes a checkpoint for the table's CURRENT
    version NOW (EW's public `DeltaTable.CheckpointAsync`, `DeltaTable.cs:4281` — the table's own
    ParquetWriteOptions/CheckpointFormat, concurrent-writer safe, and LOG CLEANUP runs after it exactly as
    on an automatic checkpoint; `delta.enableExpiredLogCleanup='false'` opts out), returns the version
    checkpointed. The direct lever on the log-replay span that now dominates the profiled query (~45-50 s of
    1851 commits at interval 100): checkpoint after a bulk load / OPTIMIZE instead of waiting for a commit
    to land on an interval multiple. `DeltaReader.Checkpoint` opens FRESH deliberately — checkpointing a
    cached snapshot would silently checkpoint an old version. NOT registered on DeltaRs (EW-specific API).
    Gates: `verify_delta_tblproperties` §9 132 → **150** (no-auto-checkpoint control → call returns v3 →
    file appears → **a FRESH ATTACH still reads through it** — the dangerous direction, since a malformed
    checkpoint fails the NEXT reader, not the call → idempotent re-call → post-checkpoint write);
    `verify_delta_catalog_functions` §8 45 → **47** (seven declared). Mutation-tested: the registration
    removed dies at BOTH suites' own sections (tblproperties:637, functions:204). README §delta functions
    updated same commit.
  - **✅ DELTA TopN PUSHDOWN — BUILT 2026-08-16 (C++ + C#, NO ABI bump; user-queued 2026-08-15 behind the
    union/DV work, taken as soon as that shipped).** `DeltaNativeReader` now renders `ORDER BY … LIMIT n`
    into the generated SQL of ALL FOUR forms (plain / union / full / per-file loop), where before it
    DECLINED the limit whenever an order came with it. The reasoning recorded in the original entry held up
    in full — **the string gate really is a SQL SERVER gate wearing provider-agnostic clothes**
    (`is_binary_collation` asks "does this source order strings as DuckDB does"; for Delta the answer is
    unconditionally yes for every type, because the generated SQL is executed BY DuckDB, so the comparator
    picking the top-n IS the kept TopN's comparator) — and the build added one thing the entry only gestured
    at: `NullOrderCompatible` needed its OWN capability, not just a note that its restriction "dissolves".
    - **TWO capabilities, and the SECOND is what makes the feature reach real queries.** `DeltaCatalog
      .CapabilitiesJson` declares `is_binary_collation` (string keys, previously refused outright) AND the
      new **`null_order_expressible`** (absent ⇒ false, so SQL Server is untouched; C++
      `FabricatorCapabilities` → `FabricatorCatalog::NullOrderExpressible()` →
      `ArrowStreamBindData::null_order_expressible`). Without the second, `NullOrderCompatible` — which
      encodes SQL Server's FIXED convention because T-SQL cannot spell `NULLS FIRST/LAST` — rejects
      ASC + NULLS LAST, **which is DuckDB's DEFAULT**, so a bare `ORDER BY x LIMIT n` on a NULLABLE column
      never pushed at all. ⚠ And a Delta CTAS column IS nullable (verified in the `metaData` schemaString),
      so that is the ordinary shape, not a corner. The optimizer now emits the **RESOLVED** `nulls_first`
      per key (`DBConfig::ResolveNullOrder`), never the parsed modifier — a bare `ORDER BY x` has none.
    - **⚠ THE OWED COLLATION PROBE IS ANSWERED FROM DuckDB'S SOURCE, and the answer is structural rather
      than the "MOST LIKELY" the entry hoped for.** `bind_select_node.cpp:371` runs every ORDER BY key
      through `ExpressionBinder::PushCollation`, and `PushVarcharCollation` (`collation_binding.cpp:13`)
      reads the SESSION `default_collation`, returns false for empty/`binary`/`c`/`posix`, and otherwise
      REPLACES the key with a bound function — which `ResolveOrderColumn` then declines for not being a
      plain column ref. So an explicit `COLLATE` and a session `default_collation` are the same mechanism.
      **It also covers two key TYPES for free**: `PushTimeTZCollation` / `PushIntervalCollation` wrap
      TIME_TZ and INTERVAL keys (`timetz_byte_comparable`, `normalized_interval`), so those decline too
      without this file enumerating them. ⇒ `ResolveOrderColumn`'s "must be a plain column reference" test
      is doing far more work than it looks, and is load-bearing rather than merely conservative.
    - **MEASURED CORRECTNESS against ground truth (a DuckDB-local copy of the same table) on all four
      forms**: plain (clean multi-file), union (mixed-DV), full (rowid projected), and the per-file loop at
      `MIN_FILES=0` — every leg byte-identical to truth across ASC/DESC/NULLS FIRST/string/multi-key. The
      per-file and per-branch cases are supersets by construction (each file's local top-n contains that
      file's members of the global top-n) and DuckDB re-selects above.
    - **⚠ `A UNION ALL B ORDER BY x LIMIT n` binds to the WHOLE union — probed directly** (`SELECT 1 UNION
      ALL SELECT 2 ORDER BY x DESC LIMIT 1` → one row, `2`), not inferred from the SQL standard. Worth
      knowing that the union form is safe under EITHER binding, though: a per-branch cap would still be a
      superset, so the row assertions alone could not have settled it.
    - **⚠ `hasTop` (the union form's REMOTE gate) deliberately still means a BARE limit.** That gate's
      measurement (frag `LIMIT 1` = 3.65 s / 10 opens) rests on the limit bounding how much each branch
      READS — true of a bare limit, false once a TopN sits above the union, since a TopN must see every row
      before emitting one. Letting TopN scans widen the gate would have extended a remote route to a shape
      it was never measured on; they keep the full form.
    - **⚠ It also silently changes the FILTER surface, which the original entry did not mention**: the same
      `is_binary_collation` declaration gates the C++ `FilterSerializer`/`LiveFilterNode` string RANGE
      comparisons, so Delta now pushes those for file/row-group pruning too. That is what
      `DeltaFilterBuilder`'s own doc comment has ASSUMED since it was written (*"the encoder is told the
      source is byte-ordered"*) while the flag making it true was withheld — a doc describing an intent as
      a fact, found only by reading the producer.
    - New scan-line field **`sort=[…]`**, three-valued on purpose: the rendered clause / `declined` (an
      order arrived, no clause could be built, so the LIMIT was dropped with it) / `-` (no order — a bare
      limit). Collapsing the middle into `-` would make a declined TopN indistinguishable from a bare
      LIMIT, which is the one confusion a gate here must resolve.
    - **✅ CONFIRMED LIVE ON FABRIC 2026-08-17**: `SELECT isin FROM lake.dbo.his ORDER BY isin ASC NULLS
      FIRST LIMIT 3` logs `top=3 sort=[ORDER BY "ISIN" ASC NULLS FIRST] batched=89` on the plain form —
      i.e. a bare `ORDER BY` on a nullable STRING column, the shape that needed BOTH new capabilities,
      really does push against a remote 89-file table. Statement 4.91 s. ⚠ No same-window control (the
      capabilities cannot be turned off without a rebuild), so this pins the MECHANISM live, not a speedup.
    - Gate `verify_delta_batched_read` 251 → **295** (§12a–g), green at `MIN_FILES=1` AND the shipped
      default. **Mutation-tested with FOUR mutants, each killed**: dropping `null_order_expressible` dies at
      §12a after 250 pass; dropping `is_binary_collation` dies at §12c (whose second key is a string) after
      264; not rendering the NULL placement dies at §12a after 250 — **and its DATA consequence was verified
      separately, because a kill on log text alone would mean the gate pins spelling**: `ORDER BY id ASC
      NULLS FIRST LIMIT 5` returned non-NULL ids, i.e. the NULLs are silently lost; and rendering the LIMIT
      WITHOUT the order dies at §12a's ROW assertion after 245, returning **51, 52, 53** where 1, 2, 3
      belong — the exact silent-wrong-rows failure the whole design guards against.
    - ⚠ §11e (which pinned the OPPOSITE — "TopN must not become a bare LIMIT") is DELETED and §11's header
      now says so; the two sections must be read and edited together.
    - ⚠ **The per-file loop's TopN is probe-verified, NOT pinned**, and the suite says so rather than
      implying coverage: at this suite's thresholds no TopN shape reaches the loop (it is reached by a
      rowid-only projection or a per-file predicate, and a predicate suppresses `top` at the host). It
      cannot regress alone — the loop appends the SAME `topSuffix` string the batch legs assert.
    - ⚠ **A gate trap worth remembering: the optimizer PRUNES an unused projected column.** §12f reaches
      the full form by projecting the rowid, and written as `SELECT id FROM (SELECT rowid AS r, id …)` the
      `r` was pruned, the scan took the union form, and the section passed its row assertion while pinning
      the wrong form. It must REFERENCE the rowid in the output (as §11b already did).
    - Delta/Spark collation context, unchanged and still the corroboration rather than the argument: the
      Delta protocol carries no per-column collation (Spark 4.0's collations are a preview table feature;
      stats stay binary-truncated UTF-8, which is what EW's pruner compares), and Spark's default
      `UTF8_BINARY` = DuckDB's default VARCHAR order anyway.

  **⚠ AND THE FULL FORM CANNOT SIMPLY USE THE `schema` MAP INSTEAD** — that is settled and committed, do not
  re-derive it: a `MAP` is homogeneous in its key type AND the key type IS the matching mode (VARCHAR ⇒ by
  name, INTEGER ⇒ `BY_FIELD_ID`), so the call is forced to the HARDEST requirement in it. Virtual columns
  force INTEGER keys; a field-id map + `file_row_number` then hits DuckDB's `No default expression in FieldId
  Map` assertion, which INVALIDATES THE DATABASE. Full record incl. the root cause in DuckDB's source:
  [docs/duckdb-upstream-issues.md](docs/duckdb-upstream-issues.md) §1.

  **THE TEST QUERY + MEASUREMENT RECIPE** (live Fabric lakehouse `LH`, credentials via `.read dax_secret.sql`
  — NEVER print that file):

  ```sql
  .read dax_secret.sql
  .timer on
  ATTACH or replace 'abfss://Test@onelake.dfs.fabric.microsoft.com/LH.Lakehouse/Tables' AS lake
    (TYPE fabricator, PROVIDER 'delta', schemas true, read_only true, secret fabric_sp);
  SELECT c_fund, d_nav, isin FROM lake.dbo.his limit 1;
  ```
  ```bash
  export FABRICATOR_MANAGED_DIR=build/release/extension/fabricator/fabricator
  export FABRICATOR_LOG_LEVEL=Debug FABRICATOR_LOG_FILE=<path>.fablog
  build/release/duckdb.exe -unsigned -batch < slowq.sql
  ```
  - **`grep -c "delta fs" <log>` IS THE SNAPSHOT-BUILD COUNT** — one line per `TableFileSystems.Create`, i.e.
    per `DeltaTable.OpenAsync`. It was **5** before this work and is **1** now; that number is the primary
    instrument, not wall clock.
  - `delta table open … (txn=N) — cache miss` names every open that did NOT hit `DeltaTableCache`. Two misses
    for one table in one statement would mean the crossings ran under different transaction ids.
  - Span-by-span: take consecutive log timestamps as deltas. The shape as of 77.19 s is **3 s ATTACH
    discovery / 26 s the one log replay / 0.05 s bind probe + scan schema + listing (all cache hits) / 34 s
    the `LIMIT 0` footer probe / ~14 s the actual read**.
  - ⚠ The table is `v1850`, 89 active files, 1851 commit JSONs, 18 checkpoints at interval 100, 49 columns.
    Its shape is the measurement — re-check it before comparing against an old number.
  - ⚠ **Run it in the BACKGROUND** (`run_in_background: true`): it takes minutes, and a foreground timeout
    kills the shell while leaving the process alive.



- **A `LIMIT 1` OVER A FABRIC LAKEHOUSE TABLE TOOK 291 s — NOW 77, MEASURED. THREE FIXES, C#-ONLY, no ABI
  and no engineered-wood change (2026-08-13). Full record + the profile:
  [docs/delta-snapshot-caching.md](docs/delta-snapshot-caching.md) §0.** `SELECT c_fund,d_nav,isin FROM
  lake.dbo.his LIMIT 1` on a table at **v1850** (89 active files, 1851 commit JSONs, 18 checkpoints at
  interval 100, 49 columns): **5 snapshot builds ≈ 195 s (67%)**, 2 schema probes ≈ 76 s (26%), the read
  ≈ 19 s. **MEASURED, three fixes: 291.06 → 146.58 → 101.26 → 77.19 s (−73%), same row, and the log is now
  replayed ONCE instead of five times.** (1) and (2) below take it to 146.58 with 4 builds; (3), the
  per-transaction table cache, takes it to 77.19 with **1**.
  - **(1) AN AT-VERSION OPEN WAS *TWO* SNAPSHOT BUILDS, so the snapshot PIN was roughly DOUBLING the cost of
    every open it exists to make consistent.** `DeltaReader.ResolveSnapshotAsync` did
    `DeltaTable.OpenAsync` (builds at LATEST) then `GetSnapshotAtVersionAsync(v)` (builds it AGAIN at v) —
    and on the ordinary read path **v IS the latest**, because the pin is SEEDED from exactly such an open.
    MEASURED: an open at latest ~26 s, an "at v1850" open ~47–48 s. Fixed by returning `CurrentSnapshot`
    when its `Version` equals the requested one.
    - **Provably equivalent, not merely usually right**: `SnapshotBuilder.BuildAsync` computes
      `targetVersion = atVersion ?? listing.LatestVersion`, so with `atVersion == LatestVersion` both calls
      select the same checkpoint and replay the same range; a Delta version is immutable, so a concurrent
      commit changes the fresh listing's LATEST but not the replay up to v.
    - **⚠ AND IT IS UPSTREAM'S OWN RULE** — `DeltaTable.ResolveReadSnapshot` is literally
      `options.AtVersion is { } v && v != CurrentSnapshot.Version ? null : CurrentSnapshot`. Our `Stream*At`
      paths already got it for free by passing `AtVersion` into `ReadAsync`; `ResolveSnapshotAsync` was the
      ONE place that duplicated the resolution without it. **When a helper of ours mirrors a library one,
      diff them — the divergence is the bug.**
  - **(2) THE BIND-TIME SCHEMA PROBE WAS RUNNING A FULL SCAN SETUP — 85 s for a stream nobody reads.** It
    LISTED every active file and BUILT a `read_parquet` over all 49 columns purely to answer "what are the
    column types"; `PopulateReturnSchema` calls `get_schema` and releases without pulling a batch.
    `DeltaCatalog.ScanTable` now short-circuits a `schema_only` spec into `SchemaProbe`, which resolves the
    schema by the same precedence both read paths use and returns an EMPTY stream.
    - **⚠ IT STILL SEEDS THE PIN — load-bearing, not leftover.** The probe runs in the statement's own
      transaction; describing the table at latest while seeding nothing would let a concurrent ALTER between
      bind and execute give the plan one schema and the scan another. That is what
      `fabricator_table_entry.cpp`'s "⚠ IT MUST STAY ON THE SCAN PATH" comment is really protecting, and it
      still holds — what is skipped is the listing and the query build, never the pin.
    - **⚠ SOUNDNESS HAD TO BE MEASURED, because the native reader does NOT trust the Delta schema.**
      `DeltaNativeReader.ProbeSchema` runs a `LIMIT 0` against a real data file and advertises DuckDB's own
      types, falling back to the userSchema-derived form only when there is no file to probe. A TYPE
      disagreement would be the read-past-the-end (SIGSEGV) class. Env-gated self-check
      (`FABRICATOR_DELTA_PROBE_CHECK=1`, shipped like the `Fabricator.Memory` marks) compared them on every
      real scan across the hermetic tier: **0 type differences.** The only divergences are `ProjectFor`'s
      documented "requested set resolves to no user column" fallback (a `count(*)`-via-rowid plan, or the
      row-tracking virtual columns, which are not in the user schema) — **projection, never type**, and the
      probe carries no projection. Re-run on the SERVICE tier (local + `s3://`): **50/50 — 2028
      green, 62 more scans, 61 of them on `s3://`, again 0 type differences.** ⚠ `abfss://` is covered by
      NO tier (`verify_delta_catalog_adls` is manual), which is why the check ships behind an env var.
    - **⚠ `spec.SchemaOnly` ONLY, deliberately NOT `spec is null`** although the pin/read-set block treats a
      null spec as a probe too. Different claims: skipping the read set is harmless for a caller that turns
      out to want rows; returning an EMPTY stream to one silently loses them.
  - Gate: `verify_delta_autocommit_pin` 65 → **69**. Its six pin assertions were re-pointed from
    `delta codec pin` to the wide `delta % pin` — the narrow pattern named the SITE standing in for the
    property, and the seeding site moved to the probe. The wide pattern still separates all three outcomes
    (0 = never pinned / 1 = shared / 4 = per-reference), so nothing is given up.
    - **⚠ AND THAT EXPOSED A CONTROL THAT WAS ALREADY VACUOUS.** §8/§11 asserted `delta codec pin %/nat` = 0
      on a NATIVE catalog to prove it was not served by the codec reader — but since the probe seeds the pin
      before either read path runs, `ScanCodec`'s seeding branch is unreachable there **whether or not the
      routing is right**. Replaced with a DIRECT control on the reader's own line
      (`delta native scan %/nat:%` > 0), which no pin refactor can make vacuous.
  - **⚠ METHOD TRAP THAT COST TWO SUITE RUNS: `FABRICATOR_LOG_LEVEL=Information` BREAKS suites that assert
    on DEBUG log lines through `duckdb_logs`.** `verify_bucket` (`delta native list … pruned=2`) and
    `verify_delta_autocommit_pin` (`delta % pin`) both failed under my own instrumentation env and passed at
    `Debug` — a failure caused by the measuring apparatus, indistinguishable at first glance from a
    regression in the change under test. **Run the tier at `Debug` or with logging off; never in between.**
  - **(3) THE PER-TRANSACTION `DeltaTable` CACHE — 146.58 s → 77.19 s, and the statement now replays the
    log ONCE instead of five times.** New `DeltaTableCache` keyed (txn, path); five READ entry points
    (`GetSchema`/`GetSchemaAndVersion`/`GetSchemaAndRowTracking`/`GetSchemaAt`/`ListNativeScanFiles`, all
    of which pass the identical `DeltaWriter.Options()`) share one open.
    - **⚠ IT CACHES THE TABLE, NOT THE IMMUTABLE `Snapshot` — REVERSING what
      [docs/delta-snapshot-caching.md](docs/delta-snapshot-caching.md) §5/§6 recommended, for a factor §5
      never weighed: a `Snapshot` cannot be turned back into a `DeltaTable` without an additive EW
      `FromSnapshot`, i.e. a fork branch we deliberately do not have.** §6's objections were re-checked
      on the current pin and only ONE survived: thread safety holds (all 10 `_currentSnapshot`
      assignments are ctor/`RefreshAsync`/commit paths, none on a read path — an unenforced invariant, so
      RE-CHECK IT AT EVERY BUMP); disposal is a boolean, not a lease (the codebase already has helpers
      borrow and never dispose) and getting it wrong throws LOUDLY; a leaked table is GC-able because
      `Dispose` only sets a flag.
    - **⚠ THE SURVIVOR IS THE OPENER, and `DuckDbTableFileSystem`'s own comment predicted it**: it keeps
      the constructed `ClientContext*` as a fallback behind the `AmbientOpener`, safe *"because no object
      outlives its call today … load-bearing the moment something is cached"*. A cached table's
      filesystem is therefore built with **opener 0**, so the ambient is the only source and its absence
      fails loudly instead of dereferencing a dangling pointer.
    - **⚠ THE FIFTH SITE WAS FOUND BY INSTRUMENTING, NOT READING.** Four sites got the query to 2 opens
      and the last would not collapse; a Debug line recording the txn id on every cache MISS showed ONE
      miss against TWO opens, so the other never reached the cache — `GetSchemaAndRowTracking`, serving
      `MetadataKind.Columns`. The four had been listed FROM MEMORY instead of grepped from the callee.
      **11 further read-only opens share the shape and are deliberately NOT wired** (unmeasured gain, more
      verification surface); `SetTablePropertiesAsync`/`ComputeSchemaChangeAsync` must NEVER be — sharing
      is sound only for readers.
    - **⚠ CATALOG ENUMERATION IS PURE COST, and the first version got it wrong (user-caught).**
      `information_schema.tables` materialises EVERY table, one `MetadataKind.Columns` crossing each, so
      wiring that crossing retained a `Snapshot` per table — and a snapshot holds `ActiveFiles`, an
      `AddFile` per data file with its `Stats` JSON. Enumeration touches each table ONCE, so the
      retention buys no reuse, on the path this file already records as the expensive one on OneLake.
      Bounded by `MaxTablesPerTxn = 32`, which DECLINES rather than evicts. ⚠ `Publish` therefore returns
      whether it cached and the caller derives "shared" from THAT, not from `txn != 0` — otherwise a
      declined table is never disposed.
    - Invalidation is COARSE by design: `InvalidateReadCache()` at the head of the nine mutating entry
      points drops the whole transaction's cache. Over-invalidating costs one re-open; under-invalidating
      is a silent stale read, and keying on "this call can change something" avoids keeping the
      immediate-commit list in sync forever. The ordinary staleness question is ALREADY answered by the
      pin; this guards the paths that do not consult it.
    - **MUTATION-TESTED, both killed.** Disabling the invalidation dies at
      `verify_delta_catalog_transactions` §2917 and `verify_delta_catalog_column_mapping` §142 — the SAME
      shape and the one it exists for, a DDL rename then a read in the same transaction; the first is the
      **dbt table-swap**, where the new `m` resolves to the old `m`'s path. ⚠ Seven other mutating suites
      SURVIVED, so it is load-bearing for the RENAME shapes specifically, not for mutation generally.
      Disposing a SHARED table dies at the FIRST read of all four suites tried
      (*Cannot access a disposed object*) ⇒ sharing is the normal path, not an edge case.
  - **⚠ A FOURTH FINDING — FOUND, FIXED, AND THE FIX TOOK TWO LAYERS (2026-08-13, C++). `AT (VERSION => n)`
    ACROSS A SCHEMA CHANGE.** `SELECT * FROM t AT (VERSION => n)` over a table ALTERed after n raised
    `Binder Error: Referenced column "extra" not found`. (1) the bind PROBE sent a hardcoded
    `{"schema_only":true}` with no AT, built ~20 lines BEFORE the clause was recorded ⇒ the plan typed at
    LATEST; (2) `SELECT *` expands from the CATALOG ENTRY, whose `ColumnList` also came from latest.
    `LookupEntry` now passes `GetAtClause()` to `GetOrCreateEntry`, which sources the as-of columns from the
    SCAN (schema-only + AT) so the entry and that scan's return schema come from ONE describe.
    - **⚠ LAYER 1 ALONE IS WORSE THAN THE BUG — BUILT, MEASURED, REVERTED, THEN REDONE WITH LAYER 2.** A
      3-column expansion meeting a 2-column scan gives `INTERNAL Error: Vector::Reference used on vector of
      different type` and then **`FATAL Error: database has been invalidated`**. A recoverable error is worth
      more than a partial fix.
    - **⚠ AT ENTRIES LIVE IN THEIR OWN MAP.** The context-taking `Scan()` walks `table_types_` (names) and
      cannot see them; the **context-free `Scan(CatalogType, callback)` walks `entries_` DIRECTLY** and would
      put a second row per time-travelled table into `duckdb_tables()`. Time travel is a property of a
      REFERENCE, not of the catalog. Gated.
    - **⚠ `ADD COLUMN` CANNOT TEST LAYER 1 — mutation-testing showed the probe-less build passing the whole
      ADD-COLUMN gate.** It APPENDS, so the as-of schema is a PREFIX of latest and they agree positionally.
      `RENAME COLUMN` separates them (differs by NAME, not length). The suite pins **ADD → layer 2,
      RENAME → layer 1**, each killed at its own section. `verify_delta_catalog_time_travel` 49 → **98**.
    - **Write paths cannot reach an AT entry, and that is DuckDB's GRAMMAR not a check of ours**: `ALTER` /
      `INSERT` / `UPDATE` / `DELETE` with `AT` on the target are all PARSER errors. It matters — an AT entry
      carries a historical `ColumnList` against a live table handle. `CREATE TABLE x AS SELECT * FROM t AT
      (VERSION => 1)` works, which is the shape people want.
    - **⚠ IT IS DELTA-ONLY, established by MEASUREMENT after I wrongly re-scoped it to two providers.**
      **Fabric Warehouse REFUSES** time travel across DDL (`12516: The TIMESTAMP … is before the object was
      last changed (with ALTER)`, live 2026-08-13) so it never returns a differing as-of schema; **box/Azure
      SQL temporal** keeps the history table's schema identical to the current one, so `FOR SYSTEM_TIME AS OF`
      can only return the CURRENT shape. Three providers, three behaviours — which is why the entry takes its
      columns from the PROVIDER's own as-of describe rather than from a rule in the host. Full record:
      [docs/known-limitations.md](docs/known-limitations.md) §1.x + §1.y.
  - **⚠ THE MATERIALIZED ROW-TRACKING COLUMNS CARRY NO PARQUET FIELD ID — AND OSS DELTA IS THE SAME
    (MEASURED live on Fabric Spark 4.1.1.5.5, 2026-08-13).** The contrast is INSIDE ONE FILE: after an
    UPDATE on a row-tracking + `column_mapping id` table, the post-image parquet has the two user columns at
    `field_id 1`/`2` and `_row-id-col-<guid>` / `_row-commit-version-col-<guid>` at **null**. Column mapping
    stamps ids into each SCHEMA FIELD's metadata; the materialized columns are not schema fields at all —
    they are hidden physical columns named by the table PROPERTIES
    `delta.rowTracking.materializedRowIdColumnName` / `…RowCommitVersionColumnName` and resolved BY NAME, and
    in id mode there is nowhere outside the schema for a `maxColumnId`-allocated id to live.
    - ⚠ **Only 1 of 3 files carried them** — a rewrite materialises, a plain append does not. **The UPDATE is
      load-bearing**: probe an append-only table and you measure an absence, not an answer.
    - ⇒ `read_parquet(schema = map {<field id>: …})` is unusable on ANY row-tracking Delta table, whoever
      wrote it, so `union_by_name` in the full batched form is the only correct choice rather than a
      workaround for our own writer — and the `No default expression in FieldId Map` assertion in
      [docs/duckdb-upstream-issues.md](docs/duckdb-upstream-issues.md) §1 is reachable from a SPARK-written
      table, which strengthens the report.
    - **⚠ AND §1 IS NOW ROOT-CAUSED IN DuckDB's SOURCE, which explains all four of its controls.** Virtual
      columns carry an ICEBERG-RESERVED field id (`MultiFileReader::ORDINAL_FIELD_ID = 2147483645`,
      `FILENAME_FIELD_ID = 2147483646`) — so the `"2147483645"` in the name-keyed error is that constant,
      not a stray sentinel. And `FieldIdMapper` (`multi_file_column_mapper.cpp`) **`break`s out of map
      construction at the first `identifier.IsNull()` column**, then `Find` carries only a `D_ASSERT` for
      that case (debug-only, so release falls through to not-found), then `GetDefault` throws because the
      column has no `default_expression`. **`filename` escapes it** because
      `GetConstantVirtualColumn` answers it with a CONSTANT (it is per-file), so it never enters that
      path — `file_row_number` varies per row, gets `nullptr`, and does. ⇒ the two symptoms are ONE gap
      seen from both ends.
    - **⚠ WHICH column fails had to be MEASURED — reading the source alone, I attributed it to a FILE**
      **column lacking a field id, and that is WRONG.** Four probes: the same map is fine WITHOUT
      `file_row_number => true` and throws WITH it, and a declared-but-absent column resolves cleanly
      through its own `default_value`. So the failing column is the VIRTUAL one the OPTION appends, which
      no `schema` entry can give a default to. **And NO, you cannot declare it**: passing the reserved id
      `2147483645` just treats it as an ordinary field id (found nowhere ⇒ a NULL column), and declaring
      it under the option's own name is `Binder Error: duplicate column name "file_row_number"`. ⇒ the
      fix is to give the VIRTUAL column a default; falling through to name matching would not help,
      because it exists in no file under any name.
    - **⚠ DuckLake — DuckDB's OWN format — does NOT trip it, and saying otherwise would be wrong.**
      MEASURED locally: a DuckLake UPDATE's post-image parquet carries `_ducklake_internal_row_id` with
      field_id **2147483540** = `MultiFileReader::ROW_ID_FIELD_ID`, and its delete file carries
      `file_path`/`pos` at 2147483646/2147483645. So the reserved-id range is DuckDB's INTENDED
      mechanism for non-schema columns and DuckLake uses it consistently. ⇒ the honest report is that
      the field-id path assumes a property **only DuckLake guarantees**, which Delta cannot provide
      (property-named columns are not schema fields). It also CONFIRMS the attribution: DuckLake's row
      id is a REAL column IN the file with a reserved id, so `Find` succeeds and no default is needed —
      a different mechanism from the option-appended virtual column that fails.
    - ⇒ **a workaround considered and REJECTED**: stamping a reserved id on the materialized column in
      OUR writer would be legal and would fix OUR tables, and would do nothing for Spark-written ones —
      which is the interop case that matters.
    - **⚠ NOR DOES DELTA'S ICEBERG COMPAT — checked because Iceberg is where the reserved id comes from,
      and it is the one condition that could have made the claim too strong.** MEASURED: with
      `icebergCompatV1` or `V2` + row tracking, the user columns carry field ids 1/2 (so the machinery
      IS active) and `_row-id-col-<guid>` still carries **null**. ⚠ Two earlier attempts measured
      NOTHING and only the controls showed it: icebergCompat REFUSES any table carrying the
      `deletionVectors` FEATURE, DVs are on by default, and setting the TBLPROPERTY is not enough — it
      must be off at the SESSION DEFAULT
      (`spark.databricks.delta.properties.defaults.enableDeletionVectors`) so the feature is never
      added. Without the "icebergCompat ALONE" control the refusal read as "row tracking and
      icebergCompat are incompatible", which is FALSE.
    - ⇒ scope note: icebergCompat and DVs are mutually exclusive and DVs are on by default, so even the
      opposite answer would have exempted only tables we never read through the batched form anyway.
    - ⚠ **METHOD, and it cost two wasted Spark runs: `scratchpad/fabricnb` reads a SHARED `result.json` from
      the lakehouse, NOT stdout.** Run 1 silently fell back to the built-in probe because `FABRICNB_SCRIPT`
      was a Git Bash path (`/d/repos/…`) and `File.Exists` is false on Windows; run 2 applied the override
      but the script only PRINTED, so the driver reported run 1's file. Both produced clean, plausible,
      IDENTICAL output. A custom script must DELETE `result.json` first and write it at every exit — then a
      run that dies shows the file's absence instead of the previous answer. Same trap as the
      notebook-parameter work (§9c): *a shared side-channel read after N experiments measures only the last.*
  - **✅ THE THIRD FINDING — THE `LIMIT` NEVER REACHED THE SCAN — IS FIXED (2026-08-15, C#-only, no ABI;
    user-approved "yes we should fix this").** `ScanSpec.Top` was consumed by `SqlServerBackend` alone;
    `DeltaNativeReader.Read` now appends ` LIMIT n` to the batch query AND to each per-file query (per file
    it is a SUPERSET — each file capped at n, DuckDB re-limits above).
    - **⚠ THE HEADLINE WIN I FIRST PREDICTED ("~25 s → ~1 s on the profiled LIMIT 1") IS RETRACTED — the
      user asked the right question ("shouldn't streaming have stopped the pipeline at the first batch?")
      and the answer is that it DOES, and data already on record contradicted the prediction.** The whole
      path streams (host query = `SendQuery` lazy fetch; bounded channel cap 2; teardown cancels), so the
      outer `STREAMING_LIMIT` stopped after the first ~2048-row batch all along. The 25 s was
      time-to-FIRST-row inside DuckDB's own `read_parquet([89 files])` — and the 2026-08-14 probe table
      shows an INNER LIMIT does not collapse it either: **plain form + `LIMIT 1` cold = 15.15 s vs
      `LIMIT 0` = 0.49 s** (× the re-measured day's 1.7× network slowness ≈ the observed 25 s). The first
      batch was LATE, not large. So this fix's real value: a DETERMINISTIC inner stop instead of
      teardown-cancel racing in-flight parallel IO, the per-file loop shapes, and consumers whose outer
      pipeline does not stop early.
    - **✅ AND THE OPEN PUZZLE IS SOLVED — IT WAS NEVER FOOTERS. IT WAS OUR OWN LITERAL-PATH GLOB
      (2026-08-15, fixed the same evening, both candidate mechanisms above were WRONG).** The per-IO
      instrument (Debug lines in `OneLakeForwardFs` Open/Read/Glob/Exists, `Fabricator.OneLake.Fs`,
      IsEnabled-gated) showed the LIMIT 1 scan doing **2 opens + 2 reads TOTAL** — after a **21.4 s
      IO-SILENT gap** between the batch query starting and the first byte. The gap:
      `OneLakeForwardFs.GlobAsync` ran a full **recursive `GetPathsAsync` directory LIST for a LITERAL
      path** (no `*`), filtering `name == p` — and DuckDB's multi-file scan globs EVERY input path when
      its lazy file list expands at scan init ⇒ 89 sequential remote LISTs ≈ 21.4 s of CPU+network before
      any parquet IO. **This retro-explains the whole probe table**: `LIMIT 0` = 0.49 s never expands the
      list (no globs); `LIMIT 1` = 15.15 s does — the "execution-phase footer sweep" hypothesis recorded
      there ("the plain form defers the opens") was a plausible story about the wrong subsystem.
    - **The fix: a literal path is ECHOED with zero IO** — httpfs's own literal-glob behaviour (the
      recorded S3 finding: "httpfs' S3 glob ECHOES literal paths back without checking the store").
      MEASURED live, same query minutes apart: **91 globs echoed / 0 LISTs, read span 21.9 s → 0.93 s,
      statement user CPU 26.9 s → 4.4 s** — the statement is now log replay + ~1 s. Trades stated in the
      code comment: a missing file errors at OPEN (404) instead of globbing empty — honest for paths a
      Delta snapshot listed; and the echo carries no extended_info, so an OPENED file pays one properties
      fetch (the round trip the LIST used to cost, now paid only for files the scan touches).
      - **⚠ FULL scans are expected ROUGHLY NEUTRAL from the echo alone — say so, it is unmeasured**: 89
        init-time LISTs become 89 open-time properties fetches (possibly overlapped with reads). Their win
        needs the snapshot's KNOWN sizes reaching the opens (the AddFile carries size; `read_parquet` args
        cannot — would need a side channel), or simply falls out wherever LIMIT/pruning shrinks the opened
        set. ⚠ `PresentNames`/`parquet_schema([all])` should ALSO get ~15-21 s cheaper (its cost was
        globs + footers, not footers alone) — re-measure before re-quoting the "two O(files) footer
        sweeps ≈ 41 s" figure anywhere.
      - **THE CLASS SWEEP (user-asked "does this exist on the S3 commit filesystem too?") — onelake was
        the only one of OURS, and one smaller UPSTREAM sibling exists.** `S3CommitFileSystem` /
        `AdlsGen2TableFileSystem` / `DuckDbTableFileSystem` structurally cannot have it: `ITableFileSystem
        .ListAsync` is a prefix-listing API and every EW caller (LogListing / TransactionLog / LogCleanup /
        LogCompaction / VacuumExecutor) passes a genuine prefix; per-file ops are GET/HEAD, and the host
        FS's `ExistsAsync` already probes via OpenRead (the 2026-07-10 fix). `s3://` native scans ride
        httpfs, which ECHOES literals (the recorded finding) — never paid. **⚠ duckdb-azure's DFS glob
        HEAD-checks each literal path** (`if (no wildcard) { if (FileExists(path)) … }`, read from its
        source 2026-08-15) — so PLAIN-ADLS abfss native reads pay one HEAD per file at scan-init
        expansion, ~N × 200 ms remote, TODAY. Upstream code, unmeasured live (the adls suite is manual);
        a candidate duckdb-azure observation — DuckDB's own two remote filesystems disagree (httpfs echoes,
        azure checks) and the HEAD buys nothing a failed open would not say better.
        - **⚠⚠ AND THE HEAD IS THE SMALL HALF — user-prompted 2026-08-17 ("but in attach we replace adls
          with our onelake:// and use our filesystem"), which is TRUE FOR ONELAKE and surfaced the bigger
          gap. The distinction is LOG vs DATA, not attach vs not:** `TableFileSystems.Create` selects
          `AdlsGen2TableFileSystem` for ANY credentialed `abfss://` root — plain ADLS included — so our own
          filesystem always carries the LOG (listings, commit JSONs, checkpoint); but `ToReadableRoot`
          rewrites to `onelake://` **only when the path contains `onelake`**, so on a plain ADLS account the
          DATA files are read by duckdb-azure.
        - **⚠ IT IS STRUCTURAL, NOT A GATE — do not "fix" it by widening the condition.**
          `OneLakeForwardFs` hardcodes `OneLakeHost = "onelake.dfs.fabric.microsoft.com"`, so the
          `onelake://<filesystem>/<path>` URI has nowhere to put another ACCOUNT. Contrast the log side,
          whose own doc records the same realisation: *"the class served any DFS account and only the
          SELECTOR said otherwise"* — it was renamed from `OneLakeDataLakeFileSystem` for exactly that.
        - **⇒ WHAT PLAIN ADLS MISSES IS EXACTLY THE `OneLakeForwardFs` WINS, AND NOTHING ELSE**: the
          literal-glob echo (91 globs → 0 LISTs, read span 21.9 → 0.93 s) and the size seed +
          `validate_external_file_cache=false` (~1000 props fetches → 0), plus it pays the duckdb-azure HEAD
          on top. **⚠ THE ≤16 MB WHOLE-FILE READ IS *NOT* IN THAT SET — a CREDENTIALED plain-ADLS root HAS
          had it since 2026-08-16, and I claimed the opposite out loud on 2026-08-20 (user-corrected).**
          `TableFileSystems.Create` selects `AdlsGen2TableFileSystem` for ANY credentialed `abfss://`
          (verified in the selector, not recalled), that class carries `BufferedReadMax = 16 MB`, and the
          checkpoint parquet — the file the whole micro-GET story is about — is a LOG file, so it rides that
          filesystem. The same holds for `COPY … TO 'abfss://…' (FORMAT delta)` with a scoped secret, and on
          the CODEC engine for DATA files too (EW reads those through the same `ITableFileSystem`).
          ⚠ **This bullet's own earlier phrasing is what produced the wrong claim** — it read *"PLAIN ADLS
          MISSES EVERY READ-PATH WIN OF THIS MONTH"* with the exception in a trailing parenthesis, so the
          headline said one thing and the qualifier the opposite. **A headline that its own footnote
          contradicts will be quoted without the footnote.** The original decision is recorded above and was
          defensible when made (*"duckdb-azure handles abfss:// parquet READ and WRITE, so
          native_read/native_write need no VFS of ours"*); the wins that have since landed on OUR side of
          that boundary are the two named here.
        - **THE FIX IS A SPLIT ALREADY DONE ONCE, FOR THE OTHER PATH.** `AdlsPath.IsAdlsGen2` (transport) vs
          `FabricLakehouse.IsOneLake` (catalog) was introduced 2026-08-02 for the LOG filesystem and NEVER
          applied to the DATA path. Making the VFS account-generic (a scheme that carries the account, or
          binding it per ATTACH) and flipping `ToReadableRoot` to the TRANSPORT predicate would hand plain
          ADLS the whole set. Unmeasured; the adls suite is manual/live-account and in NO CI tier, so this
          needs a real account before and after.
    - **⚠ TopN pushdown (queued behind the union/DV work) is where the wall-clock actually lives, and the
      streaming early-stop is exactly why**: `TOP_N` is a BLOCKING operator — it cannot emit until it has
      seen the LAST input row — so `ORDER BY x LIMIT 10` drains the whole table across the Arrow boundary
      today with no early stop possible. Pushing it inside also puts DuckDB's own TopN directly over
      `read_parquet`, whose dynamic threshold filter zonemap-skips row groups — a mechanism the OUTER TopN
      cannot reach through our opaque scan boundary.
    - **⚠ TWO GATES, BOTH HOST-VERIFIED END TO END, and the second is the dangerous direction.**
      (a) A FILTERED limit never arrives: `arrow_ingest.cpp:760` emits `"top"` only when the scan carries
      NO filter, static or dynamic — a best-effort filter's superset plus an early limit could starve real
      matches. (b) **TopN must not become a bare LIMIT**: `TryPushTopN` pushes `top`+`order_by` TOGETHER
      for non-string keys, this reader applies no ORDER BY, and an unordered LIMIT is an arbitrary subset
      that DuckDB's kept TopN above cannot repair — silently WRONG rows, not slow ones. The reader declines
      the suffix whenever `spec.OrderBy` is non-empty. (Applying the ORDER BY too would be TopN pushdown
      for this reader — a separate enhancement, deliberately not smuggled in.)
    - **⚠ THE FIRST GATE DRAFT ASSERTED A PLAN THAT DOES NOT EXIST — probe every shape before pinning it.**
      `count(*) FROM (SELECT id FROM t LIMIT n)` is NOT a batched projection scan: the optimizer rewrites
      it into the count-via-rowid plan (`cols=[] rowid=True`), which LOOPS per file — the accident now
      serves as the gate's loop-leg fixture. And the TopN negative is VACUOUS with a default-order key:
      `ORDER BY id LIMIT 6` arrives with `top=-` because `NullOrderCompatible` declines ASC+NULLS-LAST —
      only `ORDER BY id ASC NULLS FIRST` actually delivers `top=6`+`order_by` to the reader (probed both
      ways). The scan Info line gained a `top=` field, which is what lets both negatives carry POSITIVE
      witnesses (`top=-` beside the filter; `top=6` beside zero LIMIT-6 SQL).
    - Gate `verify_delta_batched_read` 194 → **219** (§11a–e: plain form / full form / per-file loop /
      the two negatives), **mutation-tested with two mutants, each killed at its own section**: removing
      the OrderBy gate dies at §11e's zero-LIMIT-6 assertion after 210 pass; never appending the suffix
      dies at §11a after 196. Suite green at MIN_FILES=1 (the tier's setting) AND the standalone default.
  - **⚠ WHAT IS LEFT, and what it is NOT.** The residual `ProbeSchema` `LIMIT 0` is ~38 s and is **O(active
    files) remote parquet FOOTER reads** (89 here) — no amount of log caching touches it. The snapshot cache
    ([docs/delta-snapshot-caching.md](docs/delta-snapshot-caching.md) §5) would collapse the four remaining
    opens to one; its decision gate is now PASSED, but its prize is **~146.6 s → ~85 s, not 291 → 85**, and it
    needs an engineered-wood `FromSnapshot` factory for the LISTING and STREAM opens — which now means
    re-opening a fork branch, since we deliberately run on zero-patch upstream. A **schema-only** cache needs
    no patch and takes two of the four.
  - User-side levers, unchanged and unrelated to us: `delta.checkpointInterval` (100 → 10 shortens the
    replay tail), `OPTIMIZE` (fewer active files ⇒ a cheaper `LIMIT 0`), `delta.logRetentionDuration`.

- **PROJECTION PUSHDOWN FOR BOTH PATH-BASED DELTA READERS — DONE 2026-08-13 (C#-only), after splitting one
  flag into two.** `fabricator_delta_scan` / `fabricator_delta_native_scan` read EVERY column of every file
  whatever the query asked for. Both readers could already prune (EW's `Stream` takes `columns`;
  `DeltaNativeReader` names them in its SQL) and both were handed nothing, because `BindingBoundTable`
  declared the stream with the binding's FULL `OutputSchema` and a subset would have mismatched it.
  - **THE BLOCKER WAS ONE FLAG MEANING THREE THINGS.** `SupportsPushdown` on the binding conflated "the rows
    are already filtered" with "only the requested columns are here"; both readers must answer FALSE to the
    first (EW prunes files/row-groups then never re-checks per row ⇒ superset), so the projection was
    switched off with it — one axis hostage to the other. Split into **`SupportsFilterPushdown`** and
    **`SupportsProjectionPushdown`**, both defined as guarantees about the RESULT. ⚠ And `IBoundTable`'s flag
    was a THIRD question — the host's by-name vs positional MAPPING, which is what the ABI comment always
    said — now **`MapResultByName`**; it is the ENABLING CONDITION for projection, not the same question.
  - **MEASURED, 40 cols × 200k rows, warm, 3 trials**: native **0.176–0.199** (1 of 40) vs **0.297–0.318**
    (all 40) ⇒ ~40% faster; codec 0.180–0.219 vs 0.220–0.225 ⇒ ~17%. ⚠ A ~0.17 s fixed floor dominates at
    this size, so the ratios UNDERSTATE it — the column term scales with rows × columns and the floor does not.
  - **⚠ MY `COUNT(*)` PREDICTION WAS WRONG, and the log settled it.** I expected an EMPTY pushed column list
    (a zero-field Arrow schema is unrepresentable ⇒ full-schema fallback ⇒ no gain) and said so. DuckDB
    pushes **ONE** column: `cols=[id]` for `count(*)` beside `cols=[id,v,name]` for `SELECT *`. So `COUNT(*)`
    went from 40 columns to 1. The empty-list fallback stays as a guard and is NOT load-bearing.
  - **⚠ THE PROJECTED ORDER IS THE DECLARED SCHEMA'S, NOT THE REQUEST'S — REQUIRED, NOT TIDY, AND MEASURED.**
    engineered-wood emits in SCHEMA order whatever order it is asked in, so ordering by the request makes the
    declaration disagree with the batches for any out-of-schema-order query — and that failure is **SIGSEGV**
    (`arrow_ingest` reads a VARCHAR where the batch holds an INT), not a wrong answer. Mutation-tested: the
    request-order mutant crashes at exactly the reverse-order assertion after 67 pass. `DeltaNativeReader`
    emits in the order it is handed and would have agreed either way ⇒ **one reader's convention decides the
    contract for both**, which is also why the wrapper and the bindings share ONE `ProjectionPlan` instead of
    deriving the list twice.
  - `ProjectionPlan` returns null ("read everything") for the three shapes that must not be guessed: nothing
    pushed, an EMPTY list, or a requested name the binding does not declare.
  - Gate `verify_delta_native_scan` 59 → **89** — both functions × {one column, REVERSE order, a MIDDLE
    column alone (an off-by-one then shows as wrong VALUES, not a missing column), projection + filter,
    `SELECT *`}, all on a DV-carrying table so each also proves the projection did not cost the DV. ⚠ It
    cannot catch "too many columns were read" (invisible from SQL, a performance question); it catches the
    declaration going out of step, which is the half that crashes.
  - ⚠ **STILL NOT CLAIMED: the FILTER.** Exactness there is a property of the filter SOURCE, not of the
    reader — the CATALOG scan gets DuckDB's erased, per-column-complete `TableFilterSet` (that is what
    `exact_filter_pushdown` / `filter_pushdown=true` buys, and why dynamic join filters are delivered there),
    while a global table function only ever receives the bind-time best-effort tree. Reinterpreting that tree
    as exact would be a wrong answer; the honest route is the one the catalog took.
  - Incidental, measured while checking completeness: `fabricator_delta_native_scan` prunes PARTITIONS
    (4 files → **1** for `WHERE part = 'p1'`) and prunes by column STATISTICS (4 → **2** for `id > 38`), both
    from the log before any file is opened — because it is the catalog's reader.

- **⚠ `fabricator_delta_native_scan` WAS SERVING DELETED ROWS — FIXED 2026-08-13 (C#-only). A SHIPPED
  SPIKE IS STILL SHIPPED.** It ran `SELECT * FROM read_parquet([<active files>])` over a file list resolved
  at bind. That reads a plain table correctly and nothing else: a **deletion vector** records the deletion in
  the LOG and leaves the parquet untouched. MEASURED on the DEFAULT table shape (DVs are on by default) —
  10 rows, a DV delete of 3, and it returned **all ten** (ids 1..10, sum 55) where the catalog and
  `fabricator_delta_scan` both returned **7 / 49**. Silent, no error.
  - **It is REGISTERED IN `CustomFunctions.GlobalTable`**, so any user could call it. Its own doc comment
    said "First slice — plain tables: no deletion vectors…", i.e. the limitation was known and written
    down — but a caveat in a comment is not a guard, and nothing stopped the call. **The lesson is about the
    REGISTRY, not the reader: a spike that is registered is a shipped feature.** ⚠ Its neighbour
    `fabricator_delta_write_demo` (writes a fixed 5-row table "to prove the write bridge") is still there.
  - Fixed by delegating to **`DeltaNativeReader`** — the reader an ATTACH catalog uses under `native_read`,
    where every follow-up slice actually landed — so DVs, partition columns, column mapping and schema
    evolution are applied, and the file list + its DVs are resolved together from ONE snapshot at execute
    time rather than as bare URIs at bind.
  - **⚠ ITS OWN DOC WAS ALSO WRONG IN THE OTHER DIRECTION**: "no partition columns" was false — a partitioned
    table worked, because the spike passed no `hive_partitioning` flag and DuckDB auto-detected the `part=a/`
    layout. Accidental, and the opposite of what `DeltaNativeReader` does deliberately (auto-detection OFF,
    partition values from the log, because an `x=y` directory anywhere in a table's path would otherwise
    inject a phantom column). Both directions are now pinned.
  - **⚠ NEITHER GLOBAL DELTA READER PUSHES THE PROJECTION, and the blocker is the SEAM not the reader.**
    `BindingBoundTable.OutputSchema` is the binding's FULL schema, fixed at bind before DuckDB says what it
    wants, so emitting a projected subset mismatches it — `arrow_ingest` reads past the end (SIGSEGV).
    `fabricator_delta_scan` carries the identical limitation and says so in its own comment. Both DO push the
    FILTER (file / row-group skipping). Lifting it needs a bound table that declares the PROJECTED schema —
    the catalog path has no such problem, which is why `DeltaNativeReader` projects in all four SQL shapes.
  - ⚠ **Pushdown reaches a global table function at EXECUTE time, not bind**: `Execute(TableFunctionScan)`
    carries `{columns, filter}` + the typed constants. Bind only resolves the output schema — which is
    exactly why the projection cannot be honoured there.
  - Gate: `verify_delta_native_scan` 36 → **59**, mutation-tested (restoring the old query survives **44**
    assertions and dies at the DV one). ⚠ **The pre-existing fixture could not catch this and that is why it
    did not** — `delta_simple` is a plain table, so all 36 original assertions pass with the bug fully
    present. The DV / partitioned / column-mapped shapes have to be BUILT, and each is asserted three ways
    (catalog, `delta_scan`, `native_scan`) so "they agree" cannot be satisfied by a reader that has stopped
    returning rows.


- **THE BATCHED NATIVE DELTA READ — DONE 2026-08-06 (C#-only, no ABI). One `read_parquet([f1, f2, …],
  schema = map {…})` replaces the per-file host query for the files it can cover** (`DeltaNativeReader.BatchPlan`);
  everything else keeps the existing loop, file by file. Threshold `FABRICATOR_DELTA_BATCH_MIN_FILES`, default 2,
  `0` disables. Gates: hermetic **67/67 — 6513** (the pre-change tier was 66/6403 and every shared suite kept its
  exact assertion count ⇒ behaviour-preserving), new suite `verify_delta_batched_read` **110**, and
  service **45/45 — 1583** — which matters beyond regression coverage: `verify_delta_catalog_s3`
  (177 × 2 engine legs) is the only leg that puts the batched `read_parquet([…])` on **`s3://`** URIs
  rather than local paths.
  - **MEASURED, both legs run through our own scan with the env var flipped** (the only honest A/B, see below):
    **200 files × 100 rows 0.464 s → 0.090 s (5.2x)**; 200 files × 20k rows 0.794 → 0.493; 50 files × 20k rows
    0.211 → 0.123. That is **~1.5–1.9 ms of overhead removed per file**, consistent across all three, so the
    RELATIVE win tracks how FRAGMENTED the table is rather than how big — i.e. it lands on the dbt-incremental
    shape (every run appends a file) that motivated it.
  - **⚠ A 13x FIGURE THIS FILE'S PREDECESSOR NOTE CARRIED IS WITHDRAWN — IT WAS CONFOUNDED, and the confound is
    architectural rather than a slip.** It put our scan (412 ms) against DuckDB reading the same files in ONE
    plan (31 ms). That plan AGGREGATES IN PLACE; our scan must hand every row back across the Arrow boundary for
    DuckDB to aggregate above it. So 31 ms was a FLOOR no batching can reach, not an alternative — and the
    residual after batching is exactly that hand-back, which is inherent to the native-read design. **Lesson in
    one line: a comparison against a plan that does not carry your data is not a comparison.**
    - ⚠ **The replacement measurement nearly repeated the mistake in a new disguise.** The first batched-vs-plain
      timing said `schema` cost 10x (21 ms vs 2 ms) — because the probe's `count(s)` was answered from parquet
      NULL COUNTS without decoding the column at all. With a real decode forced (`sum(length(s))`), `schema`
      costs **nothing measurable**: 18–27 ms with the map vs 21–33 ms hand-aliased. **A parquet aggregate that
      the footer can answer is not a read.**
    - ⚠ **And an attribution I nearly wrote up was wrong too**: `SELECT … LIMIT 0` costs 19 ms where the same
      scan costs 274 ms, which I read as "the Delta log replay is cheap, so the residual is elsewhere". `LIMIT 0`
      never executes the scan, so it measures nothing about it. The snapshot-construction cost is still
      unmeasured here — do NOT cite this work as evidence either way for
      [delta-snapshot-caching](docs/delta-snapshot-caching.md).
  - **THE `schema` PARAMETER'S SEMANTICS, pinned by experiment because the docs are thin and several plausible
    readings are wrong.** `schema = map { <key>: {'name': …, 'type': …, 'default_value': …} }`: the **MAP KEY is
    the identifier** (VARCHAR ⇒ match by name, INTEGER ⇒ `BY_FIELD_ID`), **`'name'` is the OUTPUT name** — so it
    performs the physical→logical rename for us — `'type'` casts per file, a column ABSENT from a file arrives as
    `default_value` (**that is the schema-evolution backfill, and it is what makes the per-file footer probe
    unnecessary** — the probe was ~1.6 ms of the per-file cost), and a column present in the file but absent from
    the map is IGNORED (the post-`DROP COLUMN` read). A non-NULL default really lands, not just NULL.
  - **⚠ FOUR MEASURED LIMITS, and they are what shape the gates. Two fail LOUDLY and two SILENTLY.**
    1. **`filename` / `file_row_number` compose with an INTEGER-keyed map and FAIL with a VARCHAR-keyed one** —
       `Invalid Input Error: … column "2147483645" … could not be found`, i.e. the virtual column's sentinel id
       resolved by NAME. Since every shape needing a row position (the transient rowid, a deletion vector, a
       derived row-tracking id) needs `file_row_number`, **that single incompatibility is why the batch covers
       plain scans only.**
    2. **An INTEGER-keyed map over a file with NO parquet field ids is `INTERNAL Error: No default expression in
       FieldId Map`** (a DuckDB assertion, so also a candidate to report upstream). Name mode does not require a
       writer to stamp field ids, so field-id keys are not a free substitute for case 1.
    3. **`schema` is REFUSED together with `hive_partitioning`** (`Binder Error`), so partition literals cannot
       ride along — they need a `filename` join, which needs case 1's field-id keys.
    4. **⚠ STRUCT INTERIORS ARE MATCHED BY NAME AND THEN CAST — the silent one.** Children `(a, b)` where one
       file renamed only `b`: the batch returned **`{'a': 20, 'b': NULL}`**, the value DROPPED, exit 0. FULLY
       disjoint children DO error (*STRUCT to STRUCT cast must have at least one matching member*), so **partial
       overlap is the dangerous shape and partial overlap is exactly what one rename produces.**
  - **⚠ ITEM 3 OF THE STANDING LIST IS NOW MEASURED, AND IT IS A DIFFERENT HAZARD FROM CASE 4 ABOVE — worse.**
    A `UNION ALL` route (which `FullTableSql` uses for the clustered-OPTIMIZE rewrite) merges struct interiors BY
    NAME and NULL-fills what a branch lacks, so the same two files yield ONE struct carrying **both** names with
    half the values NULL: `{'a':…,'b':…,'col-b':NULL}` / `{'a':…,'b':NULL,'col-b':…}`. The output TYPE is wrong
    too, not just the values. So the two routes are unsafe in different ways and neither can be fixed by the
    per-batch `ArrowColumnMappingRename`, which runs after the SQL. `FullTableSql`'s doc now records this at the
    gate that protects it.
  - **THE GATES, and one of them must NOT be described as tested.** Batched: plain scans, incl. the zero-column
    `COUNT(*)` shape (its own branch, no map at all — nothing is read, so mapping/evolution/types cannot matter).
    Per-file: the transient rowid / DML, any file carrying a **deletion vector** (decided PER FILE, so a
    merge-heavy table still collapses its clean files and the DV file keeps its prunable position bound),
    partition columns, the row-tracking virtual columns, a rowid/tracking fast-path filter, variant, and
    `column_mapping 'id'`.
    - **⚠ THE ID-MODE GATE IS A *CONTRACT* GATE AND NO TEST CAN KILL IT — a mutant with it removed passes the
      whole suite, and I nearly wrote it up as mutation-tested.** The batch resolves by NAME while id mode's
      contract is that a reader matches by FIELD ID and the stored name is not authoritative (a legacy
      engineered-wood id file stores LOGICAL names under its field ids; an external writer may do either) — so a
      name-keyed map meeting such a file silently yields an ALL-NULL column, or case 4's dropped members.
      **MEASURED why no test reproduces it: an id-mode table taken through a nested RENAME *and* a top-level
      RENAME has all four of its files storing BYTE-IDENTICAL physical names** — in id mode too, `physicalName` is
      assigned once at column creation. Keep the gate because name-matching a table whose contract is id-matching
      is unsound for files WE DID NOT WRITE, and say exactly that rather than implying a bug was reproduced.
    - **⚠ AND THE DEFAULT COLUMN-MAPPING MODE IS `name`, NOT `id`** — measured off the `metaData` of a plain
      `PROVIDER 'delta'` create. `verify_delta_catalog_column_mapping`'s own header says *"DEFAULT = 'id'"* and is
      STALE. It matters here and not academically: were `id` the default, the gate above would disable batching
      for nearly every table and this feature would be inert out of the box.
  - **WHAT RETIRES THE ID-MODE GATE, concretely: duckdb/duckdb #24407** — *"extend the `schema` option to support
    NESTED schema definitions"* (Tishj), **OPEN against `main`** as of 2026-08-06. Declaring a struct's children
    with their own identifiers is precisely what lets one declared type describe files of two vintages. ⚠ It
    targets `main`, so it lands on the FUTURE line, not `v1.5-variegata` — the gate stands here regardless.
  - **⚠ ONE REAL CODE REQUIREMENT WAS FOUND BY RUNNING THE SUITES, NOT BY READING.** `schema` renames the TOP
    level only, so a mapped struct's interior arrives PHYSICAL — and a pushed struct-member predicate then fails
    to bind (`Binder Error: Could not find key "b" in struct`, caught by `verify_delta_catalog_nested_alter`,
    which is also the reason the per-file path has `RebuildExpr`). Fixed by `LogicalStructExpr`: ONE `struct_pack`
    rebuild serves every file, because name mode's physical names are file-independent. Note the two suites that
    caught it reported PARTIAL assertion counts while broken (26 and 156, vs 100 and 251 when passing) — an
    aborted suite's count is not a coverage number.
  - **Mutation-tested, each mutant killed at its own section**: removing the struct rebuild dies at the
    struct-member predicate (§3, the binder error above); removing the DV split dies at §4 with the exact
    resurrection (**300 rows where 290 survive** — the 10 deleted rows back). The id-mode mutant SURVIVES, per
    the note above.
  - **⚠ The suite is run at `FABRICATOR_DELTA_BATCH_MIN_FILES=1` by `run-suites.sh`, and the reason is the MIRROR
    IMAGE of the UPDATE-grouping case.** Here the shipped default (2) IS exercised by every other delta suite, so
    what would go untested is the batched path on a ONE-file scan — the shape most suites actually build. The
    `unset` for every other suite is load-bearing in an extra direction too: a stray `0` in a developer's shell
    DISABLES batching everywhere, so a green tier would be testing only the old loop while looking complete.
  - **THE FULL FORM — DONE, and it is what the original ask actually was.** The first pass shipped a NARROW
    version that batched only plain scans and gated off the rowid and the deletion vectors — i.e. it gated off
    the substance ("composes with filename+file_row_number so the DV becomes ONE input keyed (filename,pos)")
    and reported the easy half as the feature. The user caught it: *"you actually implemented nothing I thought
    you were implementing."* **Do not narrow a deliverable and report it as done** — the facts needed for the
    real form were already measured in the same session and I took the small branch anyway.
    - Now: ONE query covers EVERY file, with the deletion vectors of all files bound as a single
      `(filename, pos)` Arrow input anti-joined once, and the per-file global ordinal bound as a second input
      joined on `filename`, so `_metadata.row_id` = `(ord << 40) | file_row_number` is expressible ⇒ **UPDATE /
      DELETE scans batch too.** Both inputs sit in `WITH … AS MATERIALIZED` CTEs. MEASURED on 100 files that ALL
      carry a deletion vector: **0.416 s → 0.145 s (~2.8x)**, identical answers.
    - **⚠ IT DOES NOT USE THE `schema` MAP, AND THAT IS FORCED BY A DuckDB ASSERTION BUG.** Field-id keys are the
      only kind that composes with the virtual columns — but a field-id-keyed map plus a virtual column raises
      **`INTERNAL Error: No default expression in FieldId Map`** whenever the FILE contains a column with no field
      id, and a materialized `__delta_row_id` is exactly that (row-tracking columns are not column-mapped). Row
      tracking is ON by default and every merge-on-read post-image file has that column, so the field-id route is
      unusable on the DEFAULT table shape. The same file reads fine with the map and no virtual columns, and with
      `filename` instead of `file_row_number` ⇒ an upstream assertion. **REPRODUCED on the stock 1.5.5 wheel with
      four controls, and it INVALIDATES THE DATABASE** (the next unrelated query returns *"FATAL Error: … database
      has been invalidated"*), so it is not a containable error — write-up ready to file in
      [docs/duckdb-upstream-issues.md](docs/duckdb-upstream-issues.md) §1. So the full form uses **`union_by_name => true`** plus an explicit
      physical→logical alias projection, which needs no field ids at all.
    - **⚠ `union_by_name` CANNOT PRODUCE A COLUMN NO FILE IN THE LIST CARRIES** — binder error, and PRUNING makes
      it routine (Delta-log pruning dropped the only file holding a newly-ADDed column, so `WHERE extra IS NULL`
      broke). Fixed by ONE `parquet_schema([… whole list …])` query per scan resolving which stored names exist,
      with the rest emitted as `CAST(NULL AS …)`. One query per SCAN, never per file.
    - **PARTITIONED TABLES ARE BATCHED (2026-08-07) — the gate is gone, replaced by an INVARIANT.** They were
      gated on a **confirmed upstream DuckDB bug**: `duckdb_arrow_scan` + a projection that is NOT A PREFIX of
      the bound stream's columns SEGFAULTS (`SELECT a0, a2` over a 3-column input dies; `SELECT a0, a1` is
      fine), or non-deterministically corrupts a string length into the `4294967296` assertion and INVALIDATES
      THE DATABASE. **Reproduced on plain v1.5.5 with no extensions and no fabricator code** —
      `test/repro/duckdb_arrow_scan_nonprefix.c`, ~110 lines of pure C with two passing positive controls,
      ready to file. Full record: [docs/duckdb-upstream-issues.md](docs/duckdb-upstream-issues.md) §2.
      - **THE FIX IS STRUCTURAL, and it is now a standing rule for every bound host-query input: EVERY COLUMN
        MUST BE READ BY THE GENERATED SQL.** `MetaStream` takes `withFileOrdinal` — the per-file ordinal is
        emitted only when the rowid expression reads it — so the consumed set always equals the produced set and
        the bug is unreachable BY CONSTRUCTION. A partitioned scan tripped it because that ordinal is dead
        weight when no rowid is wanted. **Add a bound column only together with the SQL that reads it.**
      - **⚠ `file_ord` IS NOT `WITH ORDINALITY`, and the old names (`withOrdinal` / `ord`) invited exactly that
        reading — renamed 2026-08-07 after they did.** `rowid = (file_ord << 40) | file_row_number`, and the two
        halves have DIFFERENT granularity: `file_ord` is the FILE's index in the scan's list, **one value per
        file**, attached by a JOIN on `filename` (never zipped positionally); `file_row_number` is the row's
        physical position inside its own parquet file, which DuckDB derives from the FOOTER's row-group offsets.
        So neither half depends on emission order — which matters because **DuckDB guarantees no row order**, and
        a `WITH ORDINALITY`-style running counter WOULD be nondeterministic under a parallel multi-row-group
        scan. MEASURED on 2 files × 10 row groups / 40k rows: the id→rowid checksum is identical at threads 1, 8
        and 4, identical between the batched path and the per-file loop, and a threaded UPDATE hit exactly its 9
        predicate rows with 0 mismatches. Gate: `verify_delta_batched_read` §8.
      - **⚠ AND THE ROWID IS *TRANSIENT ACROSS CREATIONS*, which that gate had to be rewritten to respect.** A
        first version pinned a literal checksum and was FLAKY: `file_ord` follows listing order over UUID-named
        data files, so two creations of the same logical table legitimately produce different rowids (measured:
        two distinct checksums over three runs). Stable WITHIN a scan — all DML needs, since the rowid is
        captured and consumed inside one statement — but never pin one across runs.
      - **⚠ WRAPPING THE QUERY DOES NOT WORK — measured, because it is the obvious first guess (and was the
        user's).** A subquery, a plain CTE and even a **MATERIALIZED** CTE all still crash: projection pushdown
        goes straight through every one. That is exactly why the real query still died despite already being
        `WITH … AS MATERIALIZED (SELECT * FROM <view>)`. The only variants that survive are the ones where the
        skipped column stays REFERENCED (`ORDER BY a1` inside, `WHERE a1 IS NOT NULL`) — i.e. the scan is asked
        for the full set. Do not rely on a stray reference either: constant folding or a provably-true predicate
        can drop it again.
      - **⚠ THE GATE IS THE *FILTERED* QUERY, and mutation-testing proves it: an unfiltered partitioned scan
        reads every bound column anyway and passed happily while the bug was live.** With `withOrdinal` forced
        true, `verify_delta_batched_read` survives **89 assertions** and dies at exactly
        `WHERE p = 'p1'`. A partition test without a WHERE tests nothing about this.
      - **⚠ THE ERROR TEXT NAMES THE WRONG SUBSYSTEM.** `2^32` comes from `ColumnDataAllocator`'s CHECKED
        `NumericCast<uint32_t>` on an allocation SIZE, because `SetVectorString`'s `UnsafeNumericCast` is
        UNCHECKED in Release and lets a garbage string length through silently. Read it as "something made a
        string length absurd", never as an allocator or cast bug.
      - **⚠ HOW OWNERSHIP WAS ESTABLISHED, because the obvious control got it BACKWARDS.** A pyarrow
        `RecordBatchReader` on the stock wheel survives the same pruning — which looks like it convicts OUR
        export and does not: **Python's `register` never goes through `duckdb_arrow_scan`**. What settled it was
        **swapping the PRODUCER while holding the CALL SITE fixed**: feeding `duckdb_arrow_scan` a
        DuckDB-produced stream (`ArrowAppender` output) crashes identically to an Apache.Arrow C#-produced one.
        Producer-independent ⇒ the consumer owns it. **A control that changes the mechanism under test is not a
        control** — same shape as the `count(*)`-is-not-a-read trap.
      - **THREE HYPOTHESES DIED FIRST, all from bisecting the QUERY rather than the mechanism**, and the third
        had been recorded HERE AS THE ANSWER: hive-column collision (`hive_partitioning => false` does not fix
        it — kept anyway as a real guard against an `x=y` directory injecting a phantom column); a
        `union_by_name` + virtual-column assertion (does not reproduce on the stock wheel); and **"a bound input
        with a second VARCHAR column breaks"** — REFUTED, `(utf8, int64, utf8)` round-trips perfectly when every
        column is read, and the evidence for it ("with `p0` as `Int64` the query runs") was timing luck.
      - **A SEPARATE REAL BUG OF OURS WAS FOUND ON THE WAY AND FIXED (C++-only, no ABI) — and it is NOT this
        one.** `MakeHostQueryStream` gave `duckdb_arrow_scan` the CALLER's `ArrowArrayStream *` — which DuckDB
        stores as a RAW POINTER in the view — then ran `conn->SendQuery`, a STREAMING result, so the managed
        `finally` released and freed that storage before a row was fetched. The comment above the loop said the
        stream was "consumed + released during the (materializing) query" while the next LINE OF CODE said
        `// streaming (lazy Fetch)`. Fixed by `OwnedArrowInputs`: each input is MOVED (struct copy + zero the
        source, the C-data-interface move) into storage owned by the `HostQueryStream` and declared as its
        FIRST member. ⚠ **The upstream crash is unchanged with it in** — a latent hazard the investigation
        surfaced, hidden the same way (a `WITH … AS MATERIALIZED` CTE drains the input during `SendQuery`'s
        first chunk push, so almost every shape was safe by PLAN ACCIDENT).
      - **`BoundInput.Drop` is therefore CORRECTNESS, not tidiness**: `duckdb_arrow_scan` creates a
        CATALOG-level (non-temporary) view, so it outlives both the connection and the stream owning the
        input's storage. The one lazy-stream site, `DeltaCatalog.SortStream`, now defers its drop to Dispose via
        the new `BoundInput.WrapDrop` — closing the leak this entry previously recorded as owed.
      - **⚠ METHOD, and it is the transferable part: STOP BISECTING THE QUERY, GET A STACK TRACE.** Running the
        statement outside sqllogictest printed `0xC0000005 at Interop+Kernel32.LocalAlloc ← HostFs.Query`, and
        four checkpoint log lines then placed the death inside `host_query`. Three throwaway tools did the rest
        and are worth rebuilding next time: an env-var UN-GATE so the failure could be provoked on demand; a
        temporary global table function `fabricator_probe_input(sql, shape, vals)` binding an arbitrary
        hand-built Arrow batch under a name substituted into arbitrary SQL; and a temporary `__PROBE__<sql>`
        marker on `fabricator_host_query` that NESTS `MakeHostQueryStream` — giving a DuckDB-produced input
        through the identical call site for free. **Build the probe that isolates ONE variable instead of
        re-running the composite.**
      - ⚠ And one control that looked decisive was worthless: "the byte-identical statement with the metadata
        as an inline `VALUES` CTE returns the right rows" — an inline CTE has NO bound input, so it changed the
        very variable under test while appearing to hold everything constant.
    - It gives up the DV's PRUNABLE BOUND (one WHERE cannot carry a per-file range). Deliberate, and the evidence
      is already in `DvRangeCondition`: its own A/B found that bound "demonstrably works and does not show up in
      wall time". ⚠ Re-measure on REMOTE storage with a mostly-deleted file before calling it free there.
    - Still per-file: `column_mapping 'id'`, nested STRUCT columns in the full form (the plain `schema`-map form
      still handles those), and any scan with a rowid/tracking fast-path filter (its whole value is per-file
      row-group pruning, and one call has one WHERE). **Partitioned tables AND the row-tracking virtual columns
      both came OFF this list on 2026-08-07.**
      - **THE ROW-TRACKING LIFT WAS PURE STALENESS — no new mechanism, just a gate nobody re-derived.** Its
        stated reason (*"a materialized `__delta_row_id` is not column-mapped, so it has no field id to key by,
        and a DuckDB `map` cannot mix INTEGER and VARCHAR keys"*) is entirely about the field-id `schema` map
        that this form ABANDONED for `union_by_name`. Under name resolution the materialized column is just
        another column, and `baseRowId`/`defaultRowCommitVersion` are per-FILE constants that ride the metadata
        input like `file_ord` ⇒ `COALESCE(materialized, baseRowId + file_row_number)` in one query.
      - **⚠ THE CASE THAT MATTERS IS A *MIXED* TABLE, and a test on a uniform one proves nothing.** An UPDATE's
        post-image file MATERIALIZES ids while the untouched files derive them, so the scan spans both;
        `union_by_name` NULL-fills the files that lack the column, which is exactly the COALESCE's fallthrough.
        MEASURED: 200 rows / 200 distinct ids / 0 NULLs, the 6 rewritten rows KEEPING their original ids
        (max 185, range still 0–199), with `batched=4` in the log as the positive control that the batched path
        was actually taken. Gate: `verify_delta_batched_read` §6b.
    - **⚠ A `count(*)` CONTROL PRODUCED A FALSE "IT WORKS" FOR THE THIRD TIME IN ONE SESSION.** The field-id +
      virtual-column combination was pronounced fine on a multi-file `count(*)` — which DuckDB answers without
      building the full mapping. Forcing a real decode reproduced the assertion immediately. **A parquet
      aggregate the footer can answer is not a read; never use one as a control here.**
    - **Gates for the full form: hermetic 67/67 — 6513 AND service 45/45 — 1583, both IDENTICAL to the narrow
      version's counts** ⇒ every answer unchanged while the rowid and DV shapes moved onto the batched path. The
      service leg is the load-bearing one here: `verify_delta_catalog_s3` (177 × 2 engine legs) is the only place
      the batched `read_parquet([…])` — now with two bound Arrow inputs and MATERIALIZED CTEs — runs against
      **`s3://`** URIs rather than local paths.
    - New: `SingleScanArrowStream` wraps each bound input so a SECOND scan THROWS. Without it, a re-scan of the
      single-use DV view returns zero rows and silently resurrects deleted rows; `MATERIALIZED` is what makes the
      single scan true today, and this makes a future planner change fail instead of corrupting an answer.
  - **⚠ THE BOUND-INPUT VIEW WAS A GLOBAL, CATALOG-LEVEL NAME — FIXED 2026-08-06, and it was a SHIPPED bug
    reachable from two documented settings.** Found by the user asking whether the DV view could collide when
    joining several Delta tables, or whether it is session-scoped. It is not session-scoped: DuckDB's
    `duckdb_arrow_scan` registers the input with **`CreateView(name, replace: true, temporary: FALSE)`**
    (`duckdb/src/main/capi/arrow-c.cpp` → `Ingest`), i.e. a CATALOG-level view shared by every connection on the
    database, silently replacing any existing one — and it must stay alive until the STREAMING result is fully
    fetched, so two host queries binding one name race over the whole fetch.
    - **MEASURED with `FABRICATOR_DELTA_PREFETCH=8` + `FABRICATOR_DELTA_BATCH_MIN_FILES=0`** (both shipped and
      documented, so each DV file gets its own concurrent query): **every scan of a deletion-vector table failed**
      with *"failed to register input view '__fab_dv'"*. That is the LOUD outcome; the same race can instead let
      one query's view be REPLACED by another's stream, which is silent wrong rows.
    - **It also LEAKED**: because the view is not temporary it outlived its connection and showed up in the
      user's own `duckdb_views()` (measured — `__fab_dv` sitting there after a plain DELETE + SELECT, next to a
      pre-existing `__fabricator_delta_write_src` from the write path, which has the same shape and is NOT fixed
      here).
    - Fix: **per-query unique names** (`__fab_dv_<n>` / `__fab_files_<n>`, an interlocked counter) plus an
      explicit `DROP VIEW IF EXISTS` once the query has been drained. The drop is what makes uniqueness
      affordable — without it the catalog would accumulate one view per scan instead of one stale one. Verified:
      the failing configuration returns correct results, and the leaked-view count is **0**.
    - **⚠ AND THE WRITE PATH HAD THE SAME BUG, WORSE — `NativeParquetDataFileWriter` (fixed in the same pass).**
      Its `__fabricator_delta_write_src` is the identical fixed-name binding, and it is hit by CONCURRENT WRITERS
      rather than a tuning knob. **MEASURED: six concurrent Delta writers in ONE process — exactly the
      `dbt run --threads N` shape, on `PROVIDER 'delta'` where `native_write` is the DEFAULT — and FIVE OF THE
      SIX FAILED** (*"failed to register input view '__fabricator_delta_write_src'"*), leaving their tables
      absent. After the fix: 0 errors, all six at their full row count, 0 leaked views. ⚠ Note CLAUDE.md records
      a dbt `--threads 4` lakehouse run as PASS=4/4 — that predates the native-write default flip, so do not
      read it as coverage of this.
    - **⚠ THEN SWEPT, and there were THREE MORE fixed names — five sites in total, not two.** The two found by
      tripping over them were not the whole class: `HostBatchFilter` (`__fabricator_scan_batch`, every pushed
      batch filter), `ExternalTableRouting` (`__fabricator_external_insert`, every routed external-table
      INSERT) and `DeltaCatalog.SortStream` (`__fabricator_sort_input`, every SORTED BY write) had the same
      shape. All now take per-call names. **Do the grep, do not fix only what bit you** —
      `grep -rn "(string, IArrowArrayStream)\[\]" dotnet/` finds them all.
    - **⚠ `SortStream`'s DROP IS OWED and deliberately NOT faked**: it returns a LAZY stream, so the view must
      outlive the call and no point in that method knows the caller has finished draining. It needs a stream
      wrapper that drops on Dispose, unlike the COPY/filter sites whose queries materialize before returning.
      Until then a sorted write leaves one view per call in the catalog — **the RACE is fixed there, the LEAK is
      not.**
    - The shared plumbing is now `BoundInput.NextName` / `BoundInput.Drop`
      (`dotnet/Fabricator.Bridge/SingleScanArrowStream.cs`), so any future bound input gets both halves by
      construction rather than by remembering.
    - **⚠ TWO OF MY THREE TESTS HERE PROVED NOTHING, and the user named the second.** A join of two DV tables
      returned right answers — but a hash join materialises one side before probing, so the scans never overlap.
      A `UNION ALL` with `threads=8` also passed — and the user pointed out a union CAN produce concurrent
      queries, which is right: **`PhysicalUnion::BuildPipelines` may run branch pipelines SEQUENTIALLY**, a fact
      already recorded in this file from the 4g premature-finish bug. So a green union test is a scheduling
      accident. Only forcing concurrency through our own prefetch knob reproduced it. **When testing a race, do
      not accept a passing shape until you have shown that shape actually overlaps.**
  - **⚠ THE GATED SHAPES SHOULD GO TO A `UNION ALL`, NOT TO THE LOOP — MEASURED 2026-08-06 after the user asked
    "why didn't you choose the union instead of the looping part?", and the answer is that they were right and I
    had not measured it.** Marginal cost per file on one 200-file table: **single `read_parquet([…], schema=…)`
    ~0.2 ms / `UNION ALL` of per-file SELECTs ~0.4 ms / the per-file LOOP ~1.9 ms** (40 files: 0.012 s vs
    0.025 s; 200 files: 0.042 s vs 0.124 s — the union−single gap is ~0.4 ms per branch, roughly linear; all
    three return the identical checksum). So the loop is ~4x worse than a union for shapes the single call cannot
    express, and only the DV one was actually BLOCKED (on the `FullTableSql` literal-inlining problem below).
    Rowid, partition literals and row tracking were not blocked at all.
    - **A union does NOT lose per-file pruning, so `BatchPlan`'s case 4 is true of the single call and FALSE of a
      union.** Established from the PLAN, not from timing (the trap `DvRangeCondition` records): with one branch
      carrying `file_row_number >= 4000`, its `READ_PARQUET` shows `Filters: file_row_number>=4000` and emits
      **1,000 of 5,000** rows while the sibling branch emits all 5,000.
    - **⚠ Two things this raised, one settled by the 2026-08-16 build, one still open.** (a) A union keeps the
      per-file FOOTER PROBE (`ResolveFileMapping`) — SETTLED by construction in `TryUnionForm`: only the D DV
      branches pay it (the same probe the loop or the full form would have paid for those files), the clean
      majority pays nothing, and the zero-column COUNT(*) shape skips it entirely (fm is per-data-column).
      (b) `FullTableSql`'s "NOT usable for nested MAPPED columns" gate looks OVER-BROAD on inspection: in
      name mode `StructShapeDiffers` is true for every file (stored != logical for every mapped child), so every
      branch rebuilds to LOGICAL names and they agree; where names stay physical they do so in every branch. The
      hazard needs branches to DISAGREE, which a shared table schema makes hard to arrange. Establish that rather
      than assume it — the measured union hazard above is real, it just may not be reachable here.
  - **STILL OPEN, in the order the win would arrive:** (0) ⚠ **partitioned tables are DONE — off the loop since
    2026-08-07**; (0b) ⚠ **the mixed-DV union is BUILT — 2026-08-16, `BatchPlan.TryUnionForm` (local always,
    remote gated to pushed-LIMIT scans — the per-op execution anomaly, see the entry under the profiled-query
    work above)**, which took the SCAN-path half of item (1) with it; (1) what remains
    "gated to the loop" is only the rowid/tracking fast-path FILTERS (whose whole value is per-file row-group
    pruning) — a union of those is expressible the same way and unbuilt; (2) id mode,
    once #24407 lands; (3) `FullTableSql`'s inlined DV literals — still the documented `WITH … AS MATERIALIZED`
    fix (the scan path's TryUnionForm now demonstrates the exact shape: one `(fn, pos)` input, per-branch
    filename slices), and still open because that method serves the clustered-OPTIMIZE rewrite, a different
    caller. The `filename`-echo finding stands: **`filename` echoes the EXACT string passed in the list**, so
    it is a stable join key — with the caveat that a mismatch would silently DROP that file's rows, so any
    such join needs a `LEFT JOIN` plus an `error()` on an unmatched file. And the residual per-row cost is
    the Arrow hand-back, which no batching of any shape touches.
