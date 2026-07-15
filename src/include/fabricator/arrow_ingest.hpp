//===----------------------------------------------------------------------===//
//                         Fabricator — Arrow stream ingestion
//
// arrow_ingest.hpp
//
// Reusable bridge from an Arrow C Stream (produced by the managed bridge) into
// DuckDB DataChunks. Mirrors DuckDB's own ArrowTableFunction scan loop
// (see duckdb/src/function/table/arrow.cpp) but driven by a caller-supplied
// stream factory instead of arrow_scan's pointer factory.
//
// A concrete table function provides a `factory` that (re)produces a fresh
// ArrowArrayStream; this module owns schema discovery, global/local state, and
// the per-chunk ArrowToDuckDB conversion.
//===----------------------------------------------------------------------===//

#pragma once

// DuckDB arrow headers MUST precede any include that pulls in fabricator/abi.h, so
// DuckDB's richer ArrowSchema definition (with Init()) wins the include guard.
#include "duckdb/common/arrow/arrow_wrapper.hpp"
#include "duckdb/common/types/value.hpp"
#include "duckdb/function/table/arrow.hpp"
#include "duckdb/function/table/arrow/arrow_duck_schema.hpp"
#include "duckdb/function/table_function.hpp"

#include <functional>

namespace duckdb {
class TableCatalogEntry;
}

namespace fabricator {

// A pushdown request for one scan: projection + filter, serialized as a small
// JSON spec, plus the typed constant values the filter tree references. Empty
// spec_json + null filter_values => a plain full scan.
struct ArrowScanRequest {
	duckdb::string spec_json;          // {"columns":[...],"filter":<tree>}; empty => SELECT *
	ArrowArrayStream *filter_values = nullptr; // typed constants for the filter tree (nullable)
};

// Produces a fresh, owned ArrowArrayStream into `out` for the given request. Must
// throw a duckdb::Exception on failure (it will propagate to the SQL caller).
using StreamFactory = std::function<void(const ArrowScanRequest &request, ArrowArrayStream &out)>;

// The virtual-column-id base for PROVIDER-declared virtual columns (queryable-by-name columns the
// provider serves on request but that are not part of the user schema / SELECT * — e.g. the Delta
// catalog's stable __delta_row_id / __delta_row_commit_version). Id = base + index into
// ArrowStreamBindData::provider_virtual_columns. Must be >= VIRTUAL_COLUMN_START (2^63; enforced by
// TableBinding); offset past the MultiFileReader identifiers (2^63..2^63+2) and well below
// COLUMN_IDENTIFIER_ROW_ID/EMPTY (2^64-1/-2).
inline duckdb::column_t ProviderVirtualBase() {
	return duckdb::VIRTUAL_COLUMN_START + 0x100;
}

// Bind data for any table function that streams Arrow from the bridge.
struct ArrowStreamBindData : public duckdb::TableFunctionData {
	//! Owned copy of the result schema (populated during bind).
	duckdb::ArrowSchemaWrapper schema_root;
	//! DuckDB's parsed view of the Arrow schema (column converters).
	duckdb::ArrowTableSchema arrow_table;
	//! Resolved DuckDB return types (column order matches the Arrow schema).
	duckdb::vector<duckdb::LogicalType> return_types;
	//! Column names (parallel to return_types) — used to map a projected/filtered
	//! column index back to its provider name when pushing projection/filters.
	duckdb::vector<duckdb::string> names;
	//! Column nullability (parallel to return_types); used to decide whether NULL
	//! ordering matters when pushing ORDER BY. Empty/true => assume nullable.
	duckdb::vector<bool> column_nullable;
	//! Per-column distinct-value estimate (NDV) for the optimizer's selectivity
	//! (parallel to return_types); <= 0 => unknown (no DistinctStats reported). Only
	//! used for cardinality estimation, never pruning, so approximate/stale is safe.
	duckdb::vector<int64_t> column_ndv;
	//! When true (catalog table scans), the projected column list (and later, the
	//! filter) is pushed to the provider; the result is the projected subset and the
	//! scan maps output columns by NAME. When false (raw queries), the full result is
	//! fetched and projected positionally.
	bool push_projection = false;
	//! Re-creates the data stream for each scan.
	StreamFactory factory;

	//! Filter pushdown (Phase 2). Set by the catalog scan's pushdown_complex_filter
	//! callback: `filter_json` is a predicate-tree (FilterNode) whose constants are
	//! referenced by index into `filter_constants`; the scan ships them to the
	//! provider (value batch) so C# builds a parameterized WHERE. Empty => no filter.
	duckdb::string filter_json;
	duckdb::vector<duckdb::Value> filter_constants;

	//! A 1:1 SQL rendering of the SAME superset-safe predicates as `filter_json`, with literals INLINED
	//! (via `Value::ToSQLString()`) so it is self-contained. Consumed ONLY by a provider whose scan target
	//! is DuckDB itself (the native Delta `read_parquet` path), where DuckDB's own SQL semantics make the
	//! push exactly 1:1 — no dialect/collation risk. Providers targeting a foreign engine (SQL Server, DAX)
	//! ignore it and use `filter_json` + `filter_constants` instead. Empty => no filter. Emitted into
	//! `spec_json` as `"native_filter"`; see BuildScanSpec.
	duckdb::string native_filter_sql;

	//! LIMIT pushdown (Phase 3): a constant row limit to push as `SELECT TOP (n)`.
	//! -1 => none. Only applied when there is no pushed filter (a best-effort filter
	//! returns a superset, so TOP before exact filtering could drop rows). Set by the
	//! optimizer extension; DuckDB keeps its own LIMIT, so this is purely a hint.
	int64_t top_n = -1;
	//! ORDER BY pushdown: a JSON array `[{"col":"c","desc":bool}]`. Set by the
	//! optimizer only when ALL order keys have SQL-Server-compatible NULL ordering, there is no pushed
	//! filter, and every key is either non-string OR string under a binary collation
	//! (`string_order_pushable`); paired with top_n (TopN). Empty => none. DuckDB keeps its TopN, so this
	//! is a hint.
	duckdb::string order_by_json;

	//! Whether string-keyed ORDER BY may be pushed: true only when the catalog's database collation is
	//! binary (_BIN/_BIN2), so SQL Server's byte-order string sort matches DuckDB. Set at scan bind from
	//! the catalog (FabricatorCatalog::StringOrderPushable). Read by the optimizer's TopN pushdown.
	bool string_order_pushable = false;

	//! Time travel (DuckDB `FROM t AT (...)`), set at bind for a catalog table reference. `at_unit` is the
	//! unit ("timestamp"/"version"); `at_value` is the constant rendered as a string. Empty `at_unit` => no
	//! AT clause. Carried into the scan spec so the provider applies it (SQL Server: FOR SYSTEM_TIME AS OF
	//! for "timestamp"; "version" is rejected). A bind-time constant — the same for every scan of this plan.
	duckdb::string at_unit;
	duckdb::string at_value;

	//! Row-identity (rowid) support for catalog tables. When non-empty, these are
	//! the indices (in the result column order) of the PK / unique-index columns,
	//! and rowid_type is the rowid's DuckDB type (a scalar type for a single
	//! column, a STRUCT for a compound key). Empty => no rowid (table functions).
	duckdb::vector<duckdb::idx_t> rowid_source_columns;
	duckdb::LogicalType rowid_type;

	//! Virtual rowid source columns: rowid columns the provider supplies on request but that are NOT part of
	//! the user-visible schema (so they have no index into `names`) — e.g. the Delta catalog's transient
	//! `_metadata.row_id`. When non-empty (rowid_source_columns is then empty), these names are added to the
	//! scan's fetch list when a rowid is requested, and `arrow_ingest` resolves their result positions BY NAME
	//! for BuildRowId. SQL Server uses real columns (rowid_source_columns); this is the lakehouse path.
	duckdb::vector<duckdb::string> virtual_rowid_columns;

	//! Provider-declared virtual columns beyond the rowid (name, DuckDB type): queryable by name, excluded
	//! from SELECT *, served by the provider as ordinary result columns when fetched — e.g. the Delta
	//! catalog's stable __delta_row_id / __delta_row_commit_version (row-tracking tables under native_read).
	//! Column id = fabricator::ProviderVirtualBase() + index. Resolved BY NAME in the result, 1:1.
	duckdb::vector<std::pair<duckdb::string, duckdb::LogicalType>> provider_virtual_columns;

	//! Approximate table row count for the optimizer's cardinality estimate; -1 =>
	//! unknown (no NodeStatistics reported). Set for catalog table scans.
	int64_t row_count = -1;

	//! For catalog tables: the backing table entry, so LogicalGet::GetTable()
	//! resolves (required for UPDATE/DELETE). Null for raw table functions.
	duckdb::optional_ptr<duckdb::TableCatalogEntry> table;

	//! Required by DuckDB's late-materialization rewrite (LateMaterializationHelper::CreateLHSGet clones
	//! the scan's bind data for the fetch-side get). Copies every post-bind member; `schema_root` /
	//! `arrow_table` are deliberately NOT copied — they are bind-time-only artifacts (PopulateReturnSchema
	//! fills return_types/names from them and nothing reads them afterwards: scans build their own
	//! per-scan converters from the live stream in ArrowStreamInitGlobal).
	duckdb::unique_ptr<duckdb::FunctionData> Copy() const override;
};

// get_bind_info callback so DuckDB can recover the table entry from a scan
// (LogicalGet::GetTable()); enables UPDATE/DELETE on catalog tables.
duckdb::BindInfo ArrowStreamGetBindInfo(const duckdb::optional_ptr<duckdb::FunctionData> bind_data);

// Discovers the result schema by producing a stream, reading its schema, and
// releasing it. Fills `return_types`/`names` and bind_data.arrow_table.
void PopulateReturnSchema(duckdb::ClientContext &context, ArrowStreamBindData &bind_data,
                          duckdb::vector<duckdb::LogicalType> &return_types,
                          duckdb::vector<duckdb::string> &names);

// Reads a bare Arrow schema (e.g. filled by get_function_*_schema) into DuckDB return types + names.
// Consumes/releases `arrow_schema` (moves it into an ArrowSchemaWrapper). For schema-only metadata fetches.
void ReadArrowSchema(duckdb::ClientContext &context, ArrowSchema &arrow_schema,
                     duckdb::vector<duckdb::LogicalType> &return_types, duckdb::vector<duckdb::string> &names);

// Table-function callbacks (wire these into a duckdb::TableFunction).
duckdb::unique_ptr<duckdb::GlobalTableFunctionState>
ArrowStreamInitGlobal(duckdb::ClientContext &context, duckdb::TableFunctionInitInput &input);

duckdb::unique_ptr<duckdb::LocalTableFunctionState>
ArrowStreamInitLocal(duckdb::ExecutionContext &context, duckdb::TableFunctionInitInput &input,
                     duckdb::GlobalTableFunctionState *global_state);

void ArrowStreamScan(duckdb::ClientContext &context, duckdb::TableFunctionInput &data, duckdb::DataChunk &output);

// Ingests an owned ArrowArrayStream into DuckDB DataChunks (identity column map,
// no projection/rowid). Used to drive an operator's source from a one-off Arrow
// result, e.g. the OUTPUT rows of INSERT ... RETURNING. Owns + releases the stream.
class ArrowStreamReader {
public:
	ArrowStreamReader(duckdb::ClientContext &context, ArrowArrayStream stream);
	~ArrowStreamReader();

	//! DuckDB types of the stream's columns (in Arrow schema order).
	const duckdb::vector<duckdb::LogicalType> &Types() const {
		return types_;
	}

	//! Reads the next batch into `output` (which must be initialized to Types());
	//! sets cardinality 0 at end of stream.
	void Read(duckdb::DataChunk &output);

	// ---- sentinel-aware streaming, for the table-in-out exchange (Phase 6) ----
	// The exchange output stream interleaves real batches with length-0 SENTINEL batches
	// (NEED_MORE_INPUT) and ends with a released array (FINISHED). Read() can't be used here — it
	// skips empty batches. Instead Pull() one array at a time, then Drain() it in vector-sized slices.
	enum class PullResult { DATA, SENTINEL, END };

	//! Pull the next array. DATA => a (possibly multi-vector) batch is pending; drain it with Drain()
	//! while HasPending(). SENTINEL => a length-0 batch (released here). END => stream exhausted.
	PullResult Pull();

	//! True while the pending DATA array has rows left to drain.
	bool HasPending() const;

	//! Import the next <=STANDARD_VECTOR_SIZE slice of the pending array into `output`.
	void Drain(duckdb::DataChunk &output);

private:
	duckdb::ClientContext &context_;
	ArrowArrayStream stream_ {};
	duckdb::ArrowSchemaWrapper schema_root_;
	duckdb::ArrowTableSchema arrow_table_;
	duckdb::vector<duckdb::LogicalType> types_;
	duckdb::unique_ptr<duckdb::ArrowScanLocalState> lstate_;
	bool done_ = false;
};

} // namespace fabricator
