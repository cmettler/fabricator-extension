//===----------------------------------------------------------------------===//
//                         fabricator — a pure-DuckDB WAIT source (diagnostic)
//
// `fabricator_wait(rows, millis)` emits `rows` BIGINTs in STANDARD_VECTOR_SIZE
// chunks, sleeping `millis` before each chunk. It exists for ONE purpose: to be a
// CONTROL that contains none of our machinery. No Arrow, no ArrowArrayStream, no
// CoreCLR bridge, no managed plugin, no pull mutex of ours — just a DuckDB
// TableFunction with a global claim counter and a sleep.
//
// It is the instrument that answers "is the serialization ours or DuckDB's?" for
// any scheduling question, and it was written to settle exactly one: a UNION ALL of
// two scans does not overlap its branches. Our own instruments could not settle
// that, because every one of them runs through the Arrow scan whose pull is
// serialized by design (docs/scan-concurrency.md §1) — so a flat measurement there
// is always explainable by our own mutex. This function has no such excuse.
//
// ⚠ THE SLEEP IS OUTSIDE THE CLAIM LOCK, and that is the whole validity of the
// control: holding the lock across the sleep would reproduce precisely the
// serialization it exists to rule out, and the result would look identical.
//===----------------------------------------------------------------------===//

#pragma once

#include "duckdb/main/extension/extension_loader.hpp"

namespace duckdb {

//! Registers `fabricator_wait(rows BIGINT, millis BIGINT)`.
void RegisterFabricatorWait(ExtensionLoader &loader);

} // namespace duckdb
