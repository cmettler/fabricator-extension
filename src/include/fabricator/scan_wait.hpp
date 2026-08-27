// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

//===----------------------------------------------------------------------===//
//                         fabricator — handing a worker back instead of parking on a mutex
//
// A fabricator scan's PULL is serialized (one Arrow stream cannot be pulled from two
// threads) and BLOCKING (it is a sync-over-async call into managed code, which may be
// waiting on a network read). Those two together used to mean: with N threads declared,
// one worker blocks INSIDE the pull holding the mutex and the other N-1 block ON that
// mutex — so a single scan occupies EVERY DuckDB worker for the pull's whole duration,
// and any sibling pipeline in the plan (a UNION ALL branch, most visibly) is starved
// until it finishes. That was measured, and reproduced in pure C++ with
// `fabricator_wait(…, hold_lock := true, threads := 4)`: docs/scan-concurrency.md §5c.
//
// The fix is DuckDB's own vocabulary: a source that cannot make progress returns
// SourceResultType::BLOCKED, which deschedules the scan task and reschedules it when an
// AsyncTask completes. This file is that mechanism, shared by the Arrow scan and by the
// `fabricator_wait` control that prototyped it.
//
// ⚠⚠ THE TIMEOUT IS THE MECHANISM, NOT A SAFETY NET, and getting that backwards makes
// the whole thing pointless. `AsyncExecutionTask::ExecuteTask` runs `Execute()` and only
// THEN fires the interrupt, and DuckDB's worker pool is a fixed set of OS threads that
// nothing compensates for — so an AsyncTask that waits until the pull lands occupies a
// worker for exactly as long as parking on the mutex did. The worker is handed back
// because the wait RETURNS EARLY; the condition-variable notify exists only so that the
// fast case (a pull that completes in microseconds, i.e. every local scan) pays no
// latency for it. Read docs/scan-concurrency.md §5f before changing either.
//
// ⚠ The state is refcounted because an AsyncTask parked on it may outlive the scan's
// global state: the query can tear down (a satisfied LIMIT) while a wait is in flight.
// Shutdown() lets the scan's destructor wake every waiter it leaves behind.
//===----------------------------------------------------------------------===//

#pragma once

#include "duckdb/common/mutex.hpp"
#include "duckdb/common/shared_ptr.hpp"
#include "duckdb/function/table_function.hpp"
#include "duckdb/parallel/async_result.hpp"

#include <condition_variable>

namespace fabricator {

//! Progress signal shared by a scan's global state and every AsyncTask waiting on it.
//! GENERATION-BASED rather than a bare flag, so a waiter cannot lose a wakeup: it reads
//! the generation BEFORE it discovers it cannot proceed, and waits for that number to
//! move — a producer that finishes in between makes the predicate already true.
class ScanWaitState {
public:
	//! The current progress counter. Read this BEFORE the try-lock that may fail.
	uint64_t Generation();
	//! Something changed (a batch landed, the stream ended, the lock was released): wake every waiter.
	void Advance();
	//! The owner is going away. Every waiter returns promptly and none may touch the owner again.
	void Shutdown();
	//! Park until the generation moves past `seen`, or shutdown, or `timeout_micros` elapses.
	void WaitFor(uint64_t seen, uint64_t timeout_micros);

private:
	duckdb::mutex m;
	std::condition_variable cv;
	uint64_t generation = 0;
	bool shutdown = false;
};

//! Per-scan-thread wait length. Doubles while a thread keeps finding the pull busy and
//! resets the moment it gets a batch, so a slow remote pull costs a bounded number of
//! wake-ups (~90 for a 200 ms pull) while a fast one is never waited on at all.
struct ScanWaitBackoff {
	static constexpr uint64_t MIN_MICROS = 1000;
	static constexpr uint64_t MAX_MICROS = 16000;

	uint64_t micros = MIN_MICROS;

	void Reset() {
		micros = MIN_MICROS;
	}
	void Advance() {
		micros = micros >= MAX_MICROS ? MAX_MICROS : micros * 2;
	}
};

//! Install a BLOCKED result carrying one bounded wait on `state`. The caller MUST have
//! left the output chunk empty (DuckDB throws on BLOCKED with rows) and MUST return
//! immediately afterwards. Returns false when the caller's execution mode forbids
//! blocking, in which case the caller has to block inline as it did before.
bool BlockUntilProgress(duckdb::TableFunctionInput &data, duckdb::shared_ptr<ScanWaitState> state, uint64_t seen,
                        ScanWaitBackoff &backoff);

//! Whether this call is allowed to return BLOCKED at all. False under the SYNCHRONOUS
//! table-scan execution strategy, where PhysicalTableScan::ValidateAsyncStrategyResult
//! throws on a BLOCKED result — so a caller that gets false must block inline instead,
//! which is what the scan did before this file existed.
//!
//! ⚠ `debug_physical_table_scan_execution_strategy='SYNCHRONOUS'` therefore makes the
//! PRE-FIX code path reachable from SQL, which is what both gates use as their A/B lever
//! (verify_wait, verify_plugin): two legs, one process, one binary, no remembered number.
//!
//! ⚠ It CANNOT see TASK_EXECUTOR_BUT_FORCE_SYNC_CHECKS, which reports the same execution
//! mode as DEFAULT and then throws on any BLOCKED result. Under that debug setting a
//! contended fabricator scan raises an InternalException — accepted, because the setting's
//! own stated purpose is to throw on exactly this class of workflow.
bool CanReturnBlocked(const duckdb::TableFunctionInput &data);

} // namespace fabricator
