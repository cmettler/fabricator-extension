// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

#include "fabricator/scan_wait.hpp"

#include <chrono>

namespace fabricator {

using namespace duckdb;

uint64_t ScanWaitState::Generation() {
	lock_guard<mutex> guard(m);
	return generation;
}

void ScanWaitState::Advance() {
	{
		lock_guard<mutex> guard(m);
		generation++;
	}
	cv.notify_all();
}

void ScanWaitState::Shutdown() {
	{
		lock_guard<mutex> guard(m);
		shutdown = true;
	}
	cv.notify_all();
}

void ScanWaitState::WaitFor(uint64_t seen, uint64_t timeout_micros) {
	std::unique_lock<mutex> guard(m);
	cv.wait_for(guard, std::chrono::microseconds(timeout_micros),
	            [&]() { return shutdown || generation != seen; });
}

namespace {

//! The task DuckDB runs while our scan task is descheduled. It does NOTHING but wait, on
//! purpose: the pull itself stays on the scan task that won the lock, so this file adds no
//! new owner of the Arrow stream and therefore no new teardown hazard.
class ScanWaitTask : public AsyncTask {
public:
	ScanWaitTask(shared_ptr<ScanWaitState> state, uint64_t seen, uint64_t timeout_micros)
	    : state(std::move(state)), seen(seen), timeout_micros(timeout_micros) {
	}

	void Execute() override {
		state->WaitFor(seen, timeout_micros);
	}

private:
	shared_ptr<ScanWaitState> state;
	uint64_t seen;
	uint64_t timeout_micros;
};

} // namespace

bool CanReturnBlocked(const TableFunctionInput &data) {
	return data.results_execution_mode == AsyncResultsExecutionMode::TASK_EXECUTOR;
}

bool BlockUntilProgress(TableFunctionInput &data, shared_ptr<ScanWaitState> state, uint64_t seen,
                        ScanWaitBackoff &backoff) {
	if (!CanReturnBlocked(data)) {
		return false;
	}
	vector<unique_ptr<AsyncTask>> tasks;
	tasks.push_back(make_uniq<ScanWaitTask>(std::move(state), seen, backoff.micros));
	backoff.Advance();
	data.async_result = AsyncResult(std::move(tasks));
	return true;
}

} // namespace fabricator
