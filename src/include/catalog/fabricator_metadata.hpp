//===----------------------------------------------------------------------===//
//                         fabricator — catalog metadata helpers
//===----------------------------------------------------------------------===//

#pragma once

// arrow_ingest pulls DuckDB's Arrow C headers first; keep it ahead of abi.h.
#include "fabricator/arrow_ingest.hpp"

#include "fabricator/abi.h"
#include "duckdb/main/client_context.hpp"

#include <unordered_map>

namespace duckdb {

//! A discovered SQL Server table (or view).
struct FabricatorTableInfo {
	string schema_name;
	string table_name;
	string table_type; // "BASE TABLE" | "VIEW"
};

//! Reads every row of an Arrow stream whose columns are all UTF-8 strings.
//! Consumes and releases the stream. Returns rows[r][c].
vector<vector<string>> ReadStringTable(ArrowArrayStream &stream, idx_t expected_cols);

//! Discovers user schemas in the attached SQL Server database.
vector<string> DiscoverSchemas(FabricatorHandle handle);

//! The host-consumed capability flags for an open catalog (ABI v71, `get_capabilities`) — the typed
//! replacement for grepping the diagnostic catalog_server_info (property, value) stream. An
//! absent key in the provider's JSON means false, so every flag defaults to the safe direction.
struct FabricatorCapabilities {
	//! The database collation sorts strings by byte value (_BIN/_BIN2), matching DuckDB, so string-keyed
	//! ORDER BY+LIMIT can be pushed down safely. SQL Server only today.
	bool string_order_pushable = false;
	//! The provider applies pushed filters EXACTLY (never a superset) => the host may set
	//! `filter_pushdown = true` on the scan (so DuckDB delivers runtime dynamic/join filters and stops
	//! re-applying the pushed ones). Currently true only for the Delta catalog in Exact pushdown mode.
	bool exact_filter_pushdown = false;
};

//! Reads the catalog's capability doc (one flat JSON object of booleans). Called once at ATTACH
//! (LoadCatalog). Throws on a failed crossing — the caller keeps the old best-effort behaviour by
//! catching and leaving every capability off.
FabricatorCapabilities FetchCapabilities(FabricatorHandle handle);

//! Discovers user tables + views across all schemas.
vector<FabricatorTableInfo> DiscoverTables(FabricatorHandle handle);

//! A discovered SQL Server routine (function or procedure).
struct FabricatorFunctionInfo {
	string schema_name;
	string name;
	string kind; // "scalar" | "table" | "proc" | "other"
};

//! Discovers user functions/procedures across all schemas (kind per FabricatorFunctionInfo).
vector<FabricatorFunctionInfo> DiscoverFunctions(FabricatorHandle handle);

//! A provider-declared CATALOG-BOUND DuckDB macro: one complete CREATE MACRO statement to bind into
//! `schema_name` of the attached catalog. The host parses `create_sql` with DuckDB's OWN parser (so the full
//! macro grammar works) and OVERWRITES the parsed catalog/schema with this catalog's alias + schema_name — the
//! opposite of the global registration, which rejects a qualified body outright.
struct FabricatorMacroInfo {
	string schema_name;
	string name;
	string create_sql;
};

//! Discovers provider-declared catalog-bound macros (catalog_macros). Never throws: a provider
//! that does not serve the kind simply declares none, so the caller does not need its own guard.
vector<FabricatorMacroInfo> DiscoverCatalogMacros(FabricatorHandle handle);

//! How a declared parameter is passed at the call site. Read from the parameter FIELD's metadata
//! (`fabricator.param_style`); ABSENT => POSITIONAL, so an unflagged schema behaves as it always did.
//! Mirrors the managed `ParamStyle` (dotnet/Fabricator.Abstractions/ParamStyle.cs) — keep the two in step.
enum class FabricatorParamStyle : uint8_t {
	//! An ordinary positional argument.
	POSITIONAL,
	//! A DuckDB named parameter — `f(x := 1)`. Must follow every positional/table parameter (DuckDB's own
	//! rule: "Unnamed parameters cannot come after named parameters").
	NAMED,
	//! The input TABLE of a table-in-out function. At most one, positional; DuckDB gives the subquery its own
	//! argument slot (bind_table_function.cpp pushes a placeholder value), so following positions keep their
	//! natural index.
	TABLE_INPUT,
};

//! Resolves a scalar function's parameter names + DuckDB types from the Arrow schema of
//! its (zero-row) param-schema stream — reuses the C# type mapping, no duplicate logic.
//! When out_styles is given, it also reports each parameter's style (see FabricatorParamStyle).
void FetchFunctionParamSchema(ClientContext &context, FabricatorHandle handle, const string &schema_name,
                              const string &func_name, vector<string> &names, vector<LogicalType> &types,
                              vector<FabricatorParamStyle> *out_styles = nullptr);

//! Resolves a scalar function's return type from its (single-field) return-schema stream. When
//! out_volatile is given it also reads the volatility signal riding the result FIELD's metadata
//! (fabricator.volatile = "0" => CONSISTENT/pure, constant-foldable; absent => VOLATILE, the default).
LogicalType FetchFunctionReturnType(ClientContext &context, FabricatorHandle handle, const string &schema_name,
                                    const string &func_name, bool *out_volatile = nullptr);

//! Resolves a table-valued function's output column names + DuckDB types from the Arrow
//! schema of its (zero-row) output-schema stream — reuses the C# type mapping.
void FetchFunctionOutputSchema(ClientContext &context, FabricatorHandle handle, const string &schema_name,
                               const string &func_name, vector<string> &names, vector<LogicalType> &types);

//! Resolves a table's column names + DuckDB types from the table session's `table_schema` stream (a
//! zero-row result whose Arrow schema is the answer; reuses the C# type mapping, no duplicate type logic
//! in C++). `catalog_handle` keys the read to the ACTIVE transaction (read-your-writes: a just-created
//! table's columns must be visible inside its own transaction); `table_handle` is the session from
//! fabricator::TableOpen — when it was opened WITH an AT clause this describes the table AS OF that
//! version (the column set a `FROM t AT (VERSION => n)` reference expands `SELECT *` against; the
//! provider's own as-of describe, per docs/known-limitations.md §1.x).
//! Throws fabricator::ObjectNotFoundException when the provider ESTABLISHES the table as absent.
void FetchTableSchema(ClientContext &context, FabricatorHandle catalog_handle, FabricatorHandle table_handle,
                      vector<string> &names, vector<LogicalType> &types);

//! The row-identity + provider-virtual-column halves of the `table_info` crossing (ONE crossing — the old
//! kinds 3 + 12).
struct FabricatorTableRowIdentity {
	//! Row-identity column names in key order (PK / smallest unique index / IDENTITY / a provider virtual
	//! rowid). Empty => no rowid, UPDATE/DELETE unavailable.
	vector<string> rowid_columns;
	//! Provider-declared VIRTUAL columns (name, DuckDB-type-text) — queryable by name, not in SELECT *.
	vector<std::pair<string, string>> virtual_columns;
};

//! Reads the table's row identity + virtual columns from the session's `table_info` stream. Errors bubble
//! (the rowid half was always load-bearing for entry materialization); note this means a provider failure
//! in the VIRTUAL half now fails materialization too, where the old kind-12 fetch was silently best-effort —
//! acceptable because the reachable set is empty (the providers resolve the flag from state the schema
//! fetch on the same ambient binding already cached).
FabricatorTableRowIdentity FetchTableInfo(FabricatorHandle table_handle);

//! The optimizer-statistics half of the table session (`table_stats` — the old kinds 4 + 5, ONE crossing,
//! typed int64 values). Lazy by contract: called at first scan, never at entry materialization.
struct FabricatorTableStats {
	//! Approximate row count; -1 = unknown (a view, no stats, or a provider that surfaces none).
	int64_t row_count = -1;
	//! Per-column distinct-value estimates keyed by column name; absent = unknown. Costing only (never
	//! pruning), so approximate/stale values are safe.
	std::unordered_map<string, int64_t> column_ndv;
};

//! Reads both statistics from the session's `table_stats` stream. Errors bubble; the caller treats the
//! whole fetch as best-effort (a stats failure must not break the scan).
FabricatorTableStats FetchTableStats(FabricatorHandle table_handle);

} // namespace duckdb
