//===----------------------------------------------------------------------===//
//                         fabricator — a pure-DuckDB WAIT source (diagnostic)
//
// See fabricator_wait.hpp for why this exists. Nothing here touches Arrow, the
// CoreCLR bridge or any provider: it is deliberately the plainest well-behaved
// DuckDB table function that can cost a controlled amount of time.
//===----------------------------------------------------------------------===//

#include "fabricator_wait.hpp"

#include "duckdb/common/mutex.hpp"
#include "duckdb/function/table_function.hpp"
#include "fabricator/scan_wait.hpp"

#include <chrono>
#include <thread>

namespace duckdb {

namespace {

struct WaitBindData : public TableFunctionData {
	idx_t rows = 0;
	idx_t millis = 0;
	//! Optional third argument: what MaxThreads() reports. 0 = the honest chunk count. It exists because the
	//! declared thread count is the ONE plan-level difference between this control and our Arrow scan (which
	//! reports NumberOfThreads()), and a control has to be able to vary the variable under test.
	idx_t declared_threads = 0;
	//! Hold the claim lock ACROSS the sleep, inverting this control's defining property. It exists to TEST a
	//! mechanism rather than to be useful: our Arrow scan held gstate.main_mutex across the managed pull while
	//! declaring MaxThreads() == NumberOfThreads(), so a branch launched one task per thread — one blocking
	//! with the lock, the rest piling up on it — which occupied every worker and starved a sibling union
	//! branch. Setting this + `threads` reproduces that shape in pure C++; leaving it off is the control.
	bool hold_lock = false;
	//! Hand the worker BACK when the claim lock is taken, instead of parking on it: the fix ArrowStreamScan
	//! now ships (src/include/fabricator/scan_wait.hpp). Meaningful only together with hold_lock — without it
	//! nothing is held long enough to contend for. So `hold_lock := true` is the broken shape and
	//! `hold_lock := true, async_wait := true` the fixed one, in ONE binary with no provider, no plugin and
	//! no network anywhere in the measurement.
	bool async_wait = false;
};

struct WaitGlobalState : public GlobalTableFunctionState {
	//! Row claiming. With hold_lock the lock spans the sleep (the shape under test); otherwise it is
	//! released first — see the header: holding it over the sleep would make this control reproduce the
	//! serialization it exists to rule out, and the numbers would look the same.
	mutex claim_lock;
	idx_t next_row = 0;
	idx_t threads = 1;
	//! Shared with every parked AsyncTask, because one may outlive this state (a satisfied LIMIT tears the
	//! query down while a wait is in flight).
	shared_ptr<fabricator::ScanWaitState> wait_state = make_shared_ptr<fabricator::ScanWaitState>();

	~WaitGlobalState() override {
		wait_state->Shutdown();
	}

	idx_t MaxThreads() const override {
		return threads;
	}
};

struct WaitLocalState : public LocalTableFunctionState {
	//! The chunk this thread most recently emitted, reported as the batch index. Monotonic and
	//! unique by construction (it is the claimed start divided by the chunk size).
	idx_t batch_index = 0;
	//! How long this thread waits before asking again, when it finds the claim lock taken.
	fabricator::ScanWaitBackoff backoff;
};

unique_ptr<FunctionData> WaitBind(ClientContext &context, TableFunctionBindInput &input,
                                  vector<LogicalType> &return_types, vector<string> &names) {
	auto result = make_uniq<WaitBindData>();
	auto rows = input.inputs[0].GetValue<int64_t>();
	auto millis = input.inputs[1].GetValue<int64_t>();
	if (rows < 0 || millis < 0) {
		throw BinderException("fabricator_wait: rows and millis must both be >= 0");
	}
	result->rows = NumericCast<idx_t>(rows);
	result->millis = NumericCast<idx_t>(millis);
	auto entry = input.named_parameters.find("threads");
	if (entry != input.named_parameters.end() && !entry->second.IsNull()) {
		auto declared = entry->second.GetValue<int64_t>();
		if (declared < 0) {
			throw BinderException("fabricator_wait: threads must be >= 0");
		}
		result->declared_threads = NumericCast<idx_t>(declared);
	}
	auto hold = input.named_parameters.find("hold_lock");
	if (hold != input.named_parameters.end() && !hold->second.IsNull()) {
		result->hold_lock = hold->second.GetValue<bool>();
	}
	auto async_wait = input.named_parameters.find("async_wait");
	if (async_wait != input.named_parameters.end() && !async_wait->second.IsNull()) {
		result->async_wait = async_wait->second.GetValue<bool>();
	}
	return_types.emplace_back(LogicalType::BIGINT);
	names.emplace_back("id");
	return std::move(result);
}

unique_ptr<GlobalTableFunctionState> WaitInitGlobal(ClientContext &context, TableFunctionInitInput &input) {
	auto &bind_data = input.bind_data->Cast<WaitBindData>();
	auto result = make_uniq<WaitGlobalState>();
	// One chunk is one unit of work, so the honest thread count is the chunk count. Reporting more
	// would leave threads with nothing to claim; reporting 1 would make this function unable to
	// demonstrate intra-pipeline parallelism, which is the control's own validity check.
	auto chunks = (bind_data.rows + STANDARD_VECTOR_SIZE - 1) / STANDARD_VECTOR_SIZE;
	result->threads = bind_data.declared_threads > 0 ? bind_data.declared_threads : MaxValue<idx_t>(chunks, 1);
	return std::move(result);
}

unique_ptr<LocalTableFunctionState> WaitInitLocal(ExecutionContext &context, TableFunctionInitInput &input,
                                                  GlobalTableFunctionState *global_state) {
	return make_uniq<WaitLocalState>();
}

void WaitFunc(ClientContext &context, TableFunctionInput &data, DataChunk &output) {
	auto &bind_data = data.bind_data->Cast<WaitBindData>();
	auto &gstate = data.global_state->Cast<WaitGlobalState>();
	auto &lstate = data.local_state->Cast<WaitLocalState>();

	// Read the progress counter BEFORE trying to claim: a holder that finishes in between then makes the
	// waiter's predicate already true, so a wakeup cannot be lost.
	auto seen = gstate.wait_state->Generation();

	// The scope of this lock IS the experiment: normally it is released before the sleep (the control), and
	// with hold_lock it spans it (the shape our Arrow scan had).
	std::unique_lock<mutex> guard(gstate.claim_lock, std::defer_lock);
	if (bind_data.async_wait) {
		if (!guard.try_lock()) {
			output.SetCardinality(0);
			if (fabricator::BlockUntilProgress(data, gstate.wait_state, seen, lstate.backoff)) {
				return;
			}
			// This call may not block-and-reschedule (the SYNCHRONOUS strategy), so park as before.
			guard.lock();
		}
	} else {
		guard.lock();
	}

	if (gstate.next_row >= bind_data.rows) {
		output.SetCardinality(0);
		guard.unlock();
		gstate.wait_state->Advance();
		return;
	}
	auto start = gstate.next_row;
	gstate.next_row += STANDARD_VECTOR_SIZE;
	lstate.batch_index = start / STANDARD_VECTOR_SIZE;
	lstate.backoff.Reset();

	if (!bind_data.hold_lock) {
		guard.unlock();
		gstate.wait_state->Advance();
	}

	if (bind_data.millis > 0) {
		// OUTSIDE the lock unless hold_lock was asked for. This line is the control's validity.
		std::this_thread::sleep_for(std::chrono::milliseconds(bind_data.millis));
	}

	auto count = MinValue<idx_t>(STANDARD_VECTOR_SIZE, bind_data.rows - start);
	output.SetCardinality(count);
	auto ids = FlatVector::GetData<int64_t>(output.data[0]);
	for (idx_t i = 0; i < count; i++) {
		ids[i] = NumericCast<int64_t>(start + i);
	}

	if (guard.owns_lock()) {
		guard.unlock();
		gstate.wait_state->Advance();
	}
}

OperatorPartitionData WaitGetPartitionData(ClientContext &context, TableFunctionGetPartitionInput &input) {
	if (input.partition_info.RequiresPartitionColumns()) {
		throw InternalException("fabricator_wait: GetPartitionData does not support partition columns");
	}
	return OperatorPartitionData(input.local_state->Cast<WaitLocalState>().batch_index);
}

} // namespace

void RegisterFabricatorWait(ExtensionLoader &loader) {
	TableFunction fn("fabricator_wait", {LogicalType::BIGINT, LogicalType::BIGINT}, WaitFunc, WaitBind,
	                 WaitInitGlobal, WaitInitLocal);
	// Declared so this control can exercise EVERY result-collector route, not just the one a
	// source without it reaches: with a batch index an order-preserving plan gets
	// PhysicalBufferedBatchCollector, without it PhysicalBufferedCollector(parallel=false), and
	// `SET preserve_insertion_order=false` reaches PhysicalBufferedCollector(parallel=true).
	// See docs/scan-concurrency.md §5.
	fn.get_partition_data = WaitGetPartitionData;
	fn.named_parameters["hold_lock"] = LogicalType::BOOLEAN; // see WaitBindData: inverts the control
	fn.named_parameters["threads"] = LogicalType::BIGINT; // override what MaxThreads() reports; see WaitBindData
	fn.named_parameters["async_wait"] = LogicalType::BOOLEAN; // hand the worker back; see WaitBindData
	loader.RegisterFunction(fn);
}

} // namespace duckdb
