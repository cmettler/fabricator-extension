//===----------------------------------------------------------------------===//
//                         ArrowNet — Arrow stream producer
//
// arrow_produce.hpp
//
// Generic push-based Arrow C stream: the host fills it with a schema (derived
// from DuckDB types) plus a queue of ArrowArray batches, and the managed
// consumer reads them. No provider specifics here — used by CTAS/COPY/bulk DML.
//===----------------------------------------------------------------------===//

#pragma once

#include "duckdb/common/arrow/arrow_wrapper.hpp"
#include "duckdb/main/client_properties.hpp"

#include <mutex>
#include <queue>

namespace duckdb {
class ClientContext;
}

namespace arrownet {

// ClientProperties for the C++<->C# Arrow boundary: keeps the user's time zone and Arrow output version, but
// forces the type-encoding-robustness settings to their standard form (lossless conversion OFF, regular
// offsets, no string-view, no list-view). Our bridge maps Arrow to provider types itself, so it must always
// receive plain, standard Arrow regardless of the user's GLOBAL Arrow settings — e.g. a global
// `SET arrow_lossless_conversion = true` otherwise exports BOOLEAN as Arrow Int8, which our type mapping then
// turns into SQL SMALLINT (1/0) instead of BIT (true/false). Use this wherever an ArrowProducer is built.
duckdb::ClientProperties BoundaryClientProperties(duckdb::ClientContext &context);

class ArrowProducer {
public:
	ArrowProducer(duckdb::vector<duckdb::LogicalType> types, duckdb::vector<duckdb::string> names,
	              duckdb::ClientProperties properties);
	~ArrowProducer();

	//! Enqueue a finalized ArrowArray (ownership transfers to the producer).
	void AddBatch(ArrowArray array);
	//! Mark that no more batches will be added.
	void Finish();

	//! Marks individual fields NOT NULL in the exported schema (clears the Arrow
	//! nullable flag). `nullable[i] == false` => column i is NOT NULL. Used for
	//! DDL, where a schema-only producer carries the table's column definitions.
	void SetNullability(duckdb::vector<bool> nullable) {
		nullable_ = std::move(nullable);
	}

	//! The C stream to hand to the managed consumer (which takes ownership).
	ArrowArrayStream *Stream() {
		return &stream_;
	}

private:
	static int GetSchema(ArrowArrayStream *stream, ArrowSchema *out);
	static int GetNext(ArrowArrayStream *stream, ArrowArray *out);
	static const char *GetLastError(ArrowArrayStream *stream);
	static void Release(ArrowArrayStream *stream);

	ArrowArrayStream stream_ {};
	duckdb::vector<duckdb::LogicalType> types_;
	duckdb::vector<duckdb::string> names_;
	duckdb::ClientProperties properties_;
	duckdb::vector<bool> nullable_; // empty => all nullable (default Arrow behavior)
	std::queue<ArrowArray> batches_;
	std::mutex lock_;
	bool finished_ = false;
};

} // namespace arrownet
