// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

//===----------------------------------------------------------------------===//
//                fabricator — row-mapped (correlated LATERAL) table functions
//
// fabricator_lateral.hpp
//
// An `ILateralFunction` is registered so that its POSITIONAL parameters are real value types and its
// input relation is whatever DuckDB synthesises from the argument EXPRESSIONS — which is what makes the
// idiomatic correlated spelling bind:
//
//     SELECT * FROM inputs i, f(i.a, i.b);       -- correlated (LATERAL); one child, projected_input set
//     SELECT * FROM f(1, 2);                     -- the same bind, literal args, no child
//
// Contrast a table-IN-OUT, whose input is a declared {TABLE} parameter: it can only be called on a relation
// the caller can name, so `f(i.a)` does not bind at all.
//
// TWO execution paths share this one bind:
//   * ROW-BY-ROW  — DuckDB's PhysicalTableInOutFunction (it slices the child chunk to cardinality 1 and
//                   stamps the correlated columns itself). One managed call per OUTER ROW.
//   * BATCHED     — our own PhysicalOperator, installed over the correlated shape by the optimizer
//                   extension registered here. One managed call per INPUT CHUNK; the correlated columns are
//                   stamped from the provenance the callee returns.
//
// The batched path is a pure post-binding rewrite, so the two are binding-identical — which is what makes
// `fabricator_batched_lateral` a reference oracle rather than merely an escape hatch.
//===----------------------------------------------------------------------===//

#pragma once

#include "catalog/fabricator_metadata.hpp"
#include "fabricator/clr_host.hpp"
#include "duckdb/function/table_function.hpp"
#include "duckdb/planner/operator/logical_get.hpp"

namespace duckdb {

class DBConfig;

//! The DuckDB setting that flips between the batched and the stock row-by-row path. Declared by the managed
//! HostSettings (provider "fabricator") and read here through DuckDB's own setting store, so the optimizer
//! costs no crossing to consult it. Absent (bridge never booted) => batched, i.e. the shipped default.
extern const char *const FabricatorBatchedLateralSetting;

//! Build the registered TableFunction for one row-mapped provider function. Used by BOTH the load-time global
//! registrar and the attach-time catalog path — they differ only in `handle`/`schema_name`.
TableFunction FabricatorMakeLateralFunction(FabricatorHandle handle, const vector<string> &schema_path,
                                           const string &func_name, vector<string> arg_names,
                                           vector<LogicalType> arg_types, vector<FabricatorParamStyle> arg_styles);

//! Register the optimizer extension that rewrites an eligible correlated LogicalGet onto the batched operator.
void RegisterFabricatorLateralOptimizer(DBConfig &config);

//! The CORRELATED form of a function whose literal calls stay an ordinary scan — a discovered SQL Server TVF:
//! `FROM t, db.s.tvf(t.a)` binds as an in-out function (DuckDB picks that form only when an argument is not a
//! constant), `db.s.tvf(1)` keeps its scan with projection and filter pushdown. One TableFunction carries both;
//! its bind / init callbacks dispatch on the call shape and these run the lateral half.
shared_ptr<TableFunctionInfo> FabricatorMakeLateralInfo(FabricatorHandle handle, const vector<string> &schema_path,
                                                       const string &func_name, vector<string> arg_names,
                                                       vector<LogicalType> arg_types,
                                                       vector<FabricatorParamStyle> arg_styles);
//! Whether a call binds correlated: DuckDB synthesized an input relation from the argument expressions (named
//! columns). A literal call reports its parameter types with EMPTY names.
bool FabricatorIsCorrelatedCall(const TableFunctionBindInput &input);
//! `catalog`: the attached catalog whose server runs the function (a discovered TVF), so a pushed query can call it
//! per row of a relation the server reads too (FabricatorLateralApply); null for a function that runs here.
unique_ptr<FunctionData> FabricatorLateralBind(ClientContext &context, TableFunctionBindInput &input,
                                               TableFunctionInfo &lateral_info, vector<LogicalType> &return_types,
                                               vector<Identifier> &names, optional_ptr<Catalog> catalog = nullptr);
//! A correlated call of a server function the pushdown can send as an APPLY: its catalog, its name (schema path +
//! name) and how many leading columns of its input relation are its arguments. The input relation's remaining
//! columns are the ones the call passes through (`projected_input`).
bool FabricatorLateralApply(LogicalGet &get, vector<string> &path, optional_ptr<Catalog> &catalog, idx_t &arg_count);
bool FabricatorIsLateralBindData(optional_ptr<const FunctionData> bind_data);
unique_ptr<LocalTableFunctionState> FabricatorLateralInitLocal(ExecutionContext &context, TableFunctionInitInput &input,
                                                              GlobalTableFunctionState *global);
OperatorResultType FabricatorLateralInOut(ExecutionContext &context, TableFunctionInput &data, DataChunk &input,
                                          DataChunk &output);

} // namespace duckdb
