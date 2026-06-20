//===----------------------------------------------------------------------===//
//                         ArrowNet — Arrow stream producer (impl)
//===----------------------------------------------------------------------===//

#include "arrownet/arrow_produce.hpp"

#include "duckdb/common/arrow/arrow_converter.hpp"

#include <cstring>

namespace arrownet {

using namespace duckdb;

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
	return 0;
}

const char *ArrowProducer::GetLastError(ArrowArrayStream *) {
	return nullptr;
}

void ArrowProducer::Release(ArrowArrayStream *stream) {
	auto *self = static_cast<ArrowProducer *>(stream->private_data);
	if (self) {
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

} // namespace arrownet
