//===----------------------------------------------------------------------===//
//                         ArrowNet — CoreCLR host
//
// clr_host.hpp
//
// Bootstraps an in-process .NET runtime (self-contained, shipped beside the
// extension) via hostfxr, loads the managed ArrowNet.Bridge assembly, and
// returns the C-ABI vtable it fills. Loading is idempotent and thread-safe.
//===----------------------------------------------------------------------===//

#pragma once

// Pull DuckDB's Arrow C struct definitions first (they are richer than abi.h's
// and share the same include guards), so the whole project uses one definition.
#include "duckdb/common/arrow/arrow.hpp"

#include "arrownet/abi.h"

#include <string>

namespace arrownet {

// Loads the managed bridge on first call (idempotent) and returns the vtable it
// populated. Throws duckdb::IOException with a descriptive message on failure.
//
// The managed assemblies + bundled runtime are located, in order of preference:
//   1. the ARROWNET_MANAGED_DIR environment variable, if set;
//   2. an "arrownet" subdirectory next to the loaded extension binary.
const ArrowNetVTable &GetBridge();

// Returns the resolved managed directory (for diagnostics).
const std::string &GetManagedDirectory();

// -----------------------------------------------------------------------------
// Convenience wrappers over the vtable. Each throws duckdb::IOException carrying
// the managed error message (and releases it via free_error) on failure.
// -----------------------------------------------------------------------------

// Open a backend catalog/connection. Returns an opaque handle to close later.
ArrowNetHandle OpenCatalog(const std::string &connection_string);

// Close a handle previously returned by OpenCatalog. Safe with nullptr.
void CloseCatalog(ArrowNetHandle handle);

// Execute a query and populate `out` with the resulting Arrow stream.
void ExecuteQuery(ArrowNetHandle handle, const std::string &sql, ArrowArrayStream &out);

// Execute a non-query statement (DML/DDL); returns the number of rows affected.
// `schema_may_change` (out, nullable): set true if the statement may have changed
// schema (DDL heuristic decided in C#), so the caller can invalidate its cache.
int64_t ExecuteDml(ArrowNetHandle handle, const std::string &sql, bool *schema_may_change = nullptr);

// Bulk-load an Arrow stream into a table; the managed side consumes/releases
// `in`. Returns rows written. (Generic: provider does type mapping + DDL + copy.)
int64_t BulkInsert(ArrowNetHandle handle, const std::string &schema, const std::string &table, bool create_table,
                   bool replace, ArrowArrayStream &in);

// rowid-based DELETE: `keys` carries the key column values (Arrow). The managed
// side generates + runs the provider DELETE. Consumes/releases `keys`.
int64_t ExecuteDelete(ArrowNetHandle handle, const std::string &schema, const std::string &table,
                      ArrowArrayStream &keys);

// rowid-based UPDATE: `data` carries [set values..., key values...] (Arrow);
// the first `set_count` columns are SET values. Consumes/releases `data`.
int64_t ExecuteUpdate(ArrowNetHandle handle, const std::string &schema, const std::string &table, int32_t set_count,
                      ArrowArrayStream &data);

// Discover provider metadata. `kind` is an ArrowNetMetadataKind; `arg1`/`arg2`
// are the schema/table name when the kind needs them (empty otherwise). Fills
// `out` with the resulting Arrow stream. All provider catalog SQL lives in C#.
void GetMetadata(ArrowNetHandle handle, int32_t kind, const std::string &arg1, const std::string &arg2,
                 ArrowArrayStream &out);

// Scan a table; fills `out` with the rows as an Arrow stream. `spec_json` (empty
// => none) carries projection + filter pushdown; `filter_values` (nullable) is a
// one-batch Arrow stream of the typed constants the filter tree references.
void ScanTable(ArrowNetHandle handle, const std::string &schema, const std::string &table, const std::string &spec_json,
               ArrowArrayStream *filter_values, ArrowArrayStream &out);

// DDL: create a table whose columns are described by the (zero-row) `columns`
// Arrow stream — a non-nullable field becomes NOT NULL. `pk_columns` /
// `unique_columns` carry key constraints as 0-based field indices: `pk_columns`
// is one comma-separated group ("0,1", empty if none); `unique_columns` is
// ';'-separated groups of comma-separated indices ("2;3,4"). The managed side
// maps Arrow types to provider types and runs CREATE TABLE. `defaults` carries
// literal column DEFAULTs as space-separated "<index> <payload>" pairs (payload
// = base64(value-text) or "-" for DEFAULT NULL). Consumes `columns`.
void CreateTable(ArrowNetHandle handle, const std::string &schema, const std::string &table, ArrowArrayStream &columns,
                 bool if_not_exists, const std::string &pk_columns, const std::string &unique_columns,
                 const std::string &defaults, const std::string &text_type);

// DDL: drop a table (`if_exists` suppresses the missing-table error).
void DropTable(ArrowNetHandle handle, const std::string &schema, const std::string &table, bool if_exists);

// DDL: create a schema (`if_not_exists` guards creation).
void CreateSchema(ArrowNetHandle handle, const std::string &schema, bool if_not_exists);

// DDL: drop a schema (`if_exists` suppresses the missing-schema error).
void DropSchema(ArrowNetHandle handle, const std::string &schema, bool if_exists);

// DDL: alter a table. `alter_kind` is an ArrowNetAlterKind; `arg1`/`arg2` carry
// names per kind (empty when unused). For ADD_COLUMN / COLUMN_TYPE pass the new
// column's type as a zero-row Arrow stream in `column` (nullptr otherwise; the
// managed side consumes it when present). `flags` bit 0 is the if-(not-)exists guard.
void AlterTable(ArrowNetHandle handle, const std::string &schema, const std::string &table, int32_t alter_kind,
                const std::string &arg1, const std::string &arg2, ArrowArrayStream *column, int32_t flags);

// Transaction boundaries (see abi.h). Begin enters transaction mode; the managed
// side pins a connection + provider transaction on the first write. Commit/Rollback
// finish it. All throw duckdb::IOException with the managed message on failure.
void BeginTransaction(ArrowNetHandle handle);
void CommitTransaction(ArrowNetHandle handle);
void RollbackTransaction(ArrowNetHandle handle);

// INSERT ... RETURNING: `in` carries the rows to insert (Arrow, field names = the
// target columns; consumed by the managed side). Fills `out` with the inserted
// rows (all table columns) from OUTPUT INSERTED.*.
void InsertReturning(ArrowNetHandle handle, const std::string &schema, const std::string &table, ArrowArrayStream &in,
                     ArrowArrayStream &out);

// -----------------------------------------------------------------------------
// Streaming bulk-load (begin/push/complete). Unlike BulkInsert (which hands over a
// whole stream), this streams batches so the host never buffers the full dataset;
// the managed side drains them on a background task with bounded-channel
// backpressure. See abi.h.
// -----------------------------------------------------------------------------

// Begin a streaming bulk-load session. `schema_in` describes the columns (the
// pushed batches must match it); the managed side consumes it (imports + releases).
// `check_constraints` validates CHECK/FOREIGN KEY constraints during load (INSERT
// semantics; SqlBulkCopy skips them by default — pass false for COPY/CTAS bulk speed).
// Returns an opaque session handle to push batches into and complete later.
ArrowNetHandle BeginBulk(ArrowNetHandle handle, const std::string &schema, const std::string &table, bool create_table,
                         bool replace, bool check_constraints, ArrowSchema &schema_in);

// Push one record batch into the session; the managed side imports + releases it
// (the caller never releases it). Blocks while the channel is full (backpressure).
void PushBatch(ArrowNetHandle session, ArrowArray &batch);

// Finish the session: wait for the background load to drain and return rows
// written. `abort` cancels the load (errors swallowed) for cleanup on a failed
// query. Frees the session; the handle is invalid afterwards.
int64_t CompleteBulk(ArrowNetHandle session, bool abort);

} // namespace arrownet
