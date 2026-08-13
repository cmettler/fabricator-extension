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

//! Whether the connected database's collation is binary (_BIN/_BIN2), read from the detected server
//! profile (FABRICATOR_META_SERVER_INFO). A binary collation sorts strings by byte value, matching DuckDB,
//! so string-keyed ORDER BY+LIMIT can be pushed down safely. Best-effort: false on any failure.
bool FetchBinaryCollation(FabricatorHandle handle);

//! Whether the provider applies pushed filters EXACTLY (read from FABRICATOR_META_SERVER_INFO's
//! `exact_filter_pushdown` property). True => the host may set `filter_pushdown = true` on the scan (so
//! DuckDB delivers runtime dynamic/join filters and stops re-applying the pushed ones). Currently true only
//! for the Delta native_read catalog. Best-effort: false on any failure (the safe default).
bool FetchExactFilterPushdown(FabricatorHandle handle);

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

//! Discovers provider-declared catalog-bound macros (FABRICATOR_META_CATALOG_MACROS). Never throws: a provider
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

//! Resolves a table's column names + DuckDB types from the Arrow schema of the
//! COLUMNS metadata stream (a zero-row result; reuses the C# type mapping, no
//! duplicate type logic in C++).
void FetchTableColumns(ClientContext &context, FabricatorHandle handle, const string &schema_name,
                       const string &table_name, vector<string> &names, vector<LogicalType> &types);

//! The same, AS OF a time-travel reference's version/timestamp — the column set a
//! `FROM t AT (VERSION => n)` reference must expand `SELECT *` against.
//!
//! ⚠ It asks the SCAN (schema-only spec + the AT clause), not the COLUMNS metadata stream, for two reasons.
//! The metadata stream has nowhere to carry an AT; and asking the scan means the catalog entry's ColumnList
//! and that scan's own return schema come from ONE describe, so they cannot disagree — which is the exact
//! failure documented in docs/known-limitations.md §1.x.
//!
//! ⚠ ABSENCE IS NOT CLASSIFIED ON THIS PATH. `GetMetadata` maps the provider's NOT_FOUND status to
//! ObjectNotFoundException; `ScanTable` does not, so a missing table surfaces as the provider's own error
//! rather than "table does not exist". The caller checks the discovered NAME list first, which is what
//! answers the ordinary case; the gap is reachable only under an ATTACH object filter.
void FetchTableColumnsAt(ClientContext &context, FabricatorHandle handle, const string &schema_name,
                         const string &table_name, const string &at_unit, const string &at_value,
                         vector<string> &names, vector<LogicalType> &types);

//! Discovers the row-identity columns for a table, in key order: the primary
//! key if present, else the unique index with the fewest columns. Returns empty
//! if the table has no PK or unique index.
vector<string> FetchRowIdColumns(FabricatorHandle handle, const string &schema_name, const string &table_name);

//! Provider-declared VIRTUAL columns for a table: (name, type-text) pairs the provider serves as
//! queryable-by-name virtual columns (not part of SELECT *) — e.g. the Delta catalog's stable
//! row-tracking __delta_row_id / __delta_row_commit_version. Best-effort: any failure (a provider
//! without the metadata kind) returns empty.
vector<std::pair<string, string>> FetchVirtualColumns(FabricatorHandle handle, const string &schema_name,
                                                      const string &table_name);

//! Approximate table row count (from partition stats) for the optimizer's
//! cardinality estimate. Returns -1 if unknown (e.g. a view or no stats).
int64_t FetchRowCount(FabricatorHandle handle, const string &schema_name, const string &table_name);

//! Per-column distinct-value estimate (NDV) from existing statistics, keyed by
//! column name. Only columns that are a leading stat key appear; others are absent
//! (=> unknown). Used solely for selectivity estimation (never pruning), so an
//! approximate/stale value is safe.
std::unordered_map<string, int64_t> FetchColumnNdv(FabricatorHandle handle, const string &schema_name,
                                                   const string &table_name);

} // namespace duckdb
