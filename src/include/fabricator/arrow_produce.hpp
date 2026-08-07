//===----------------------------------------------------------------------===//
//                         Fabricator — Arrow stream producer
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

namespace fabricator {

// ClientProperties for the C++<->C# Arrow boundary: keeps the user's time zone and Arrow output version, but
// forces the type-encoding-robustness settings to their standard form (lossless conversion OFF, regular
// offsets, no string-view, no list-view). Our bridge maps Arrow to provider types itself, so it must always
// receive plain, standard Arrow regardless of the user's GLOBAL Arrow settings — e.g. a global
// `SET arrow_lossless_conversion = true` otherwise exports BOOLEAN as Arrow Int8, which our type mapping then
// turns into SQL SMALLINT (1/0) instead of BIT (true/false). Use this wherever an ArrowProducer is built.
duckdb::ClientProperties BoundaryClientProperties(duckdb::ClientContext &context);

//===----------------------------------------------------------------------===//
// ArrowLiveness — an OUT-OF-BAND registry that answers "who freed this batch, and when?"
//
// WHY IT EXISTS. `DeltaWriter.Materialize` copies every batch through an Arrow IPC round trip (two passes
// plus serialization, with the serialized MemoryStream and the decoded batches alive at once) and documents
// the copy as necessary because "the source batches may be freed after consumption". Reading the code says
// otherwise: `ArrowProducer::GetNext` moves the batch out of the queue ("ownership transfers to the
// consumer") and `Release` frees only what is STILL QUEUED, so nothing on this side can free a batch the
// managed consumer already took.
//
// ⚠ BUT READING THE CODE IS EXACTLY WHAT MUST NOT SETTLE IT. A use-after-free here is SILENT on Windows and
// Linux — that is precisely how the `ArrowProducer::Release` mutex bug hid until macOS CI ran it — so
// "the suites are green with the copy removed" is not evidence. And the doc line predates the rename, so it
// may record a real incident with a stream that is not an ArrowProducer.
//
// WHAT IT MEASURES. Every batch handed out by `GetNext` gets an id and its release callback is INTERPOSED
// (the standard C-data-interface wrap: stash the original callback + private_data, restore them before
// delegating). Each release is then recorded WITH ITS ATTRIBUTION — whether it fired from inside
// `ArrowProducer::Release`/the destructor (i.e. OUR side freed a batch it had already given away — the bug)
// or from anywhere else (i.e. the managed consumer disposing what it owns — correct). A double release shows
// up as a second record for one id.
//
// Off unless `FABRICATOR_ARROW_LIVENESS=1`, and the interposition itself is skipped when disarmed, so a
// normal run keeps the plain callback and pays nothing.
//===----------------------------------------------------------------------===//
class ArrowLiveness {
public:
	//! True when FABRICATOR_ARROW_LIVENESS is 1 or 2 (read once).
	static bool Armed();

	//! True at level 2: additionally print every handout and every release, so their ORDER against the
	//! consumer's own markers is observable rather than merely counted. Counting alone cannot distinguish
	//! "released after the write read it" (correct) from "released before" (the use-after-free), and that
	//! distinction is the entire question.
	static bool Verbose();

	//! Interpose `array`'s release callback so the free is recorded. No-op when disarmed.
	static void Track(ArrowArray &array, const char *origin);

	//! Marks the calling scope as "the producer is freeing its own queue", so a release fired from within it
	//! is attributed to US rather than to the consumer. RAII; nests safely.
	struct ProducerFreeScope {
		ProducerFreeScope();
		~ProducerFreeScope();
	};

	//! Handed out / released / released-by-us / released-more-than-once, since process start.
	static void Snapshot(int64_t &handed_out, int64_t &released, int64_t &released_by_producer,
	                     int64_t &double_released);

	//! One line per counter to stderr, plus a verdict. Called at teardown when armed.
	static void Report();
};

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

} // namespace fabricator
