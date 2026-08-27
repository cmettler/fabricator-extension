// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

//===----------------------------------------------------------------------===//
//                         Fabricator — Arrow stream producer (impl)
//===----------------------------------------------------------------------===//

#include "fabricator/arrow_produce.hpp"

#include "duckdb/common/arrow/arrow_converter.hpp"
#include "duckdb/main/client_context.hpp"

#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace fabricator {

using namespace duckdb;

//===----------------------------------------------------------------------===//
// ArrowLiveness
//===----------------------------------------------------------------------===//

namespace {

std::atomic<int64_t> g_handed_out {0};
std::atomic<int64_t> g_released {0};
std::atomic<int64_t> g_released_by_producer {0};
std::atomic<int64_t> g_double_released {0};

// Depth, not a bool: `ArrowProducer::Release` and the destructor can both be on the stack (the destructor
// runs after a release on some teardown paths), and a bool would clear the attribution on the inner exit.
thread_local int t_producer_free_depth = 0;

//! The interposed release's state. `private_data` on the tracked ArrowArray points here; the trampoline
//! restores the original pair before delegating, so the real callback sees exactly what it stored.
struct TrackedRelease {
	void (*original_release)(ArrowArray *) = nullptr;
	void *original_private_data = nullptr;
	int64_t id = 0;
	const char *origin = nullptr;
	std::atomic<int> released {0};
};

void TrampolineRelease(ArrowArray *array) {
	auto *t = static_cast<TrackedRelease *>(array->private_data);
	if (!t) {
		return;
	}
	int prior = t->released.fetch_add(1);
	g_released.fetch_add(1);
	if (ArrowLiveness::Verbose()) {
		// ORDER is the point: at level 2 the CONSUMER prints its own markers to this same stream, so a
		// release landing between "write BEGIN" and "write END" is visible in the interleaving. Counting
		// alone cannot separate "released after the write read it" from the use-after-free.
		std::fprintf(stderr, "FABRICATOR-LIVENESS: release id=%lld origin=%s\n", (long long)t->id,
		             t->origin ? t->origin : "?");
	}
	bool by_producer = t_producer_free_depth > 0;
	if (by_producer) {
		g_released_by_producer.fetch_add(1);
		// THE FINDING THIS EXISTS TO CATCH: our side freeing a batch it already handed out. Printed
		// immediately (not just counted) so the failing statement is identifiable in the suite log.
		std::fprintf(stderr, "FABRICATOR-LIVENESS: PRODUCER-SIDE RELEASE of handed-out batch id=%lld origin=%s\n",
		             (long long)t->id, t->origin ? t->origin : "?");
	}
	if (prior > 0) {
		g_double_released.fetch_add(1);
		std::fprintf(stderr, "FABRICATOR-LIVENESS: DOUBLE RELEASE of batch id=%lld origin=%s (release #%d)\n",
		             (long long)t->id, t->origin ? t->origin : "?", prior + 1);
	}
	// Restore and delegate. After this the array is released; the consumer must not touch it again, so the
	// tracking record can go with it.
	array->release = t->original_release;
	array->private_data = t->original_private_data;
	if (array->release) {
		array->release(array);
	}
	delete t;
}

} // namespace

bool ArrowLiveness::Verbose() {
	static const bool verbose = []() {
		const char *v = std::getenv("FABRICATOR_ARROW_LIVENESS");
		return v && v[0] == '2' && v[1] == '\0';
	}();
	return verbose;
}

bool ArrowLiveness::Armed() {
	static const bool armed = []() {
		const char *v = std::getenv("FABRICATOR_ARROW_LIVENESS");
		bool on = v && (v[0] == '1' || v[0] == '2') && v[1] == '\0';
		if (on) {
			// Report at process exit rather than from a destructor of ours: the interesting counters are
			// process-wide, and a suite runs many statements. atexit is enough — nothing here allocates at
			// report time.
			std::atexit(&ArrowLiveness::Report);
		}
		return on;
	}();
	return armed;
}

void ArrowLiveness::Track(ArrowArray &array, const char *origin) {
	if (!Armed() || !array.release) {
		return;
	}
	auto *t = new TrackedRelease();
	t->original_release = array.release;
	t->original_private_data = array.private_data;
	t->id = g_handed_out.fetch_add(1);
	t->origin = origin;
	array.release = TrampolineRelease;
	array.private_data = t;
	if (Verbose()) {
		std::fprintf(stderr, "FABRICATOR-LIVENESS: handout id=%lld origin=%s\n", (long long)t->id, origin);
	}
}

ArrowLiveness::ProducerFreeScope::ProducerFreeScope() {
	if (Armed()) {
		t_producer_free_depth++;
	}
}

ArrowLiveness::ProducerFreeScope::~ProducerFreeScope() {
	if (Armed()) {
		t_producer_free_depth--;
	}
}

void ArrowLiveness::Snapshot(int64_t &handed_out, int64_t &released, int64_t &released_by_producer,
                             int64_t &double_released) {
	handed_out = g_handed_out.load();
	released = g_released.load();
	released_by_producer = g_released_by_producer.load();
	double_released = g_double_released.load();
}

void ArrowLiveness::Report() {
	if (!Armed()) {
		return;
	}
	int64_t handed_out, released, by_producer, doubled;
	Snapshot(handed_out, released, by_producer, doubled);
	std::fprintf(stderr,
	             "FABRICATOR-LIVENESS: handed_out=%lld released=%lld released_by_producer=%lld "
	             "double_released=%lld verdict=%s\n",
	             (long long)handed_out, (long long)released, (long long)by_producer, (long long)doubled,
	             (by_producer == 0 && doubled == 0) ? "CONSUMER-OWNED" : "PRODUCER-FREED-A-HANDED-OUT-BATCH");
}

ClientProperties BoundaryClientProperties(ClientContext &context) {
	auto p = context.GetClientProperties();
	// Keep time zone + Arrow output version; force the encoding-robustness settings to standard so the
	// managed side always sees plain Arrow (see the header for why — the lossless BOOLEAN->Int8 trap).
	return ClientProperties(p.time_zone, ArrowOffsetSize::REGULAR, /*arrow_use_list_view=*/false,
	                        /*produce_arrow_string_view=*/false, /*lossless_conversion=*/false,
	                        p.arrow_output_version, p.client_context);
}

ArrowProducer::ArrowProducer(vector<LogicalType> types, vector<string> names, ClientProperties properties)
    : types_(std::move(types)), names_(std::move(names)), properties_(std::move(properties)) {
	std::memset(&stream_, 0, sizeof(stream_));
	stream_.get_schema = GetSchema;
	stream_.get_next = GetNext;
	stream_.get_last_error = GetLastError;
	stream_.release = Release;
	stream_.private_data = this;
}

ArrowProducer::~ArrowProducer() {
	ArrowLiveness::ProducerFreeScope liveness_scope;
	std::lock_guard<std::mutex> guard(lock_);
	while (!batches_.empty()) {
		auto &array = batches_.front();
		if (array.release) {
			array.release(&array);
		}
		batches_.pop();
	}
}

void ArrowProducer::AddBatch(ArrowArray array) {
	std::lock_guard<std::mutex> guard(lock_);
	batches_.push(array);
}

void ArrowProducer::Finish() {
	std::lock_guard<std::mutex> guard(lock_);
	finished_ = true;
}

int ArrowProducer::GetSchema(ArrowArrayStream *stream, ArrowSchema *out) {
	auto *self = static_cast<ArrowProducer *>(stream->private_data);
	// Regenerate a fresh, consumer-owned schema each call.
	ArrowConverter::ToArrowSchema(out, self->types_, self->names_, self->properties_);
	// Honor explicit NOT NULL (DDL): clear the nullable flag on those fields.
	for (idx_t i = 0; i < self->nullable_.size() && (int64_t)i < out->n_children; i++) {
		if (!self->nullable_[i]) {
			out->children[i]->flags &= ~ARROW_FLAG_NULLABLE;
		}
	}
	return 0;
}

int ArrowProducer::GetNext(ArrowArrayStream *stream, ArrowArray *out) {
	auto *self = static_cast<ArrowProducer *>(stream->private_data);
	std::lock_guard<std::mutex> guard(self->lock_);
	if (self->batches_.empty()) {
		// End of stream: release marker.
		std::memset(out, 0, sizeof(*out));
		return 0;
	}
	*out = self->batches_.front(); // ownership transfers to the consumer
	self->batches_.pop();
	// Record the handover so a later free can be attributed. No-op unless FABRICATOR_ARROW_LIVENESS=1.
	ArrowLiveness::Track(*out, "ArrowProducer::GetNext");
	return 0;
}

const char *ArrowProducer::GetLastError(ArrowArrayStream *) {
	return nullptr;
}

void ArrowProducer::Release(ArrowArrayStream *stream) {
	auto *self = static_cast<ArrowProducer *>(stream->private_data);
	if (self) {
		ArrowLiveness::ProducerFreeScope liveness_scope;
		std::lock_guard<std::mutex> guard(self->lock_);
		while (!self->batches_.empty()) {
			auto &array = self->batches_.front();
			if (array.release) {
				array.release(&array);
			}
			self->batches_.pop();
		}
	}
	stream->release = nullptr;
}

} // namespace fabricator
