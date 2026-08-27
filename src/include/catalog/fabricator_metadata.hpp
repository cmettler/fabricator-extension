// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

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

//! A discovered table (or view) — any provider, via catalog_tables.
struct FabricatorTableInfo {
	string schema_name;
	string table_name;
	string table_type; // "BASE TABLE" | "VIEW"
};

//! Reads every row of an Arrow stream whose columns are all UTF-8 strings.
//! Consumes and releases the stream. Returns rows[r][c].
vector<vector<string>> ReadStringTable(ArrowArrayStream &stream, idx_t expected_cols);

//! Discovers the attached catalog's schemas (catalog_schemas) — every provider, not just SQL Server.
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
	//! The provider can RENDER an arbitrary NULL placement for a pushed ORDER BY key, so the host may hand
	//! it the resolved `nulls_first` per key instead of checking the key against a fixed server convention.
	//! False (the default) means the provider has ONE built-in convention — T-SQL has no NULLS FIRST/LAST,
	//! and SQL Server orders NULLs first for ASC / last for DESC — so `NullOrderCompatible` must gate the
	//! push. True for the Delta reader, whose ORDER BY is executed BY DuckDB, which spells both.
	bool null_order_expressible = false;
};

//! Reads the catalog's capability doc (one flat JSON object of booleans). Called once at ATTACH
//! (LoadCatalog). Throws on a failed crossing — the caller keeps the old best-effort behaviour by
//! catching and leaving every capability off.
FabricatorCapabilities FetchCapabilities(FabricatorHandle handle);

//! Discovers user tables + views across all schemas.
vector<FabricatorTableInfo> DiscoverTables(FabricatorHandle handle);

//! A discovered routine (function or procedure) — any provider, via catalog_functions.
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

//! A provider-declared CATALOG-BOUND DuckDB view: one complete CREATE VIEW statement to bind into
//! `schema_name` of the attached catalog. Parsed with DuckDB's OWN parser and re-qualified onto this
//! catalog's alias + schema_name, exactly like a macro — the DIFFERENCE is what happens at BIND time:
//! DuckDB anchors a view body's search path to the VIEW's own catalog and schema, so an unqualified table
//! reference inside `create_sql` resolves against THIS catalog. A macro body does not (it binds in the
//! CALLER's context), which is why a view is the declaration form for a body that names provider tables.
struct FabricatorViewInfo {
	string schema_name;
	string name;
	string create_sql;
};

//! Discovers provider-declared catalog-bound views (catalog_views). Never throws, for the same reason
//! DiscoverCatalogMacros does not: declaring views is optional and a broken declaration must never block
//! an ATTACH.
vector<FabricatorViewInfo> DiscoverCatalogViews(FabricatorHandle handle);

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

//! One ALTER TABLE request for the `table_alter` crossing (ABI v74) — the C++ mirror of the managed
//! `AlterTableSpec`. Deliberately one struct with a `kind` rather than a variant hierarchy: each kind fills
//! only its own fields, and the renderer emits only what was filled, so the doc on the wire names its
//! variant and carries nothing else (the whole point of retiring alter_kind + arg1/arg2/flags, where every
//! carrier meant something different per kind). Keep the field set in step with AlterTableSpec.cs.
struct FabricatorAlterRequest {
	//! The doc's "kind" — one of the names listed on table_alter in abi.h.
	string kind;
	//! Target column name (top-level kinds). Empty => the key is omitted.
	string column;
	//! Rename target: the new table / column / field name. Empty => omitted.
	string new_name;
	//! Nested-field path as SEGMENTS — a field name may contain dots, so a joined string is ambiguous.
	//! Empty => omitted (a field path always has at least one segment).
	vector<string> path;
	//! SET SORTED BY / SET PARTITIONED BY column list. Guarded by `has_columns` because an EMPTY list is
	//! meaningful there (it is the RESET spelling) and must be told apart from "this kind has no list".
	vector<string> columns;
	bool has_columns = false;
	//! The statement's if-(not-)exists guard. Rendered under the key its kind defines — "if_not_exists" for
	//! the ADD kinds, "if_exists" for the DROP kinds — and omitted entirely when false.
	bool guard = false;
	//! SET DEFAULT only: emit the (required) "default" key. `default_is_null` distinguishes DEFAULT NULL
	//! from a literal, which is the distinction the old arg2 encoded as "-" vs "b"+base64(text).
	bool has_default = false;
	bool default_is_null = false;
	string default_literal;
};

//! Renders a `FabricatorAlterRequest` as the `table_alter` JSON doc. Uses yyjson's mutable API rather than
//! string concatenation so identifiers are escaped CORRECTLY: the hand-rolled builder this replaced escaped
//! only `"` and `\`, so a legal DuckDB identifier containing a control character (e.g. a tab, via a quoted
//! name) produced invalid JSON and the ALTER failed with a parser message about byte positions.
string FabricatorRenderAlterJson(const FabricatorAlterRequest &request);

} // namespace duckdb
