//===----------------------------------------------------------------------===//
//                fabricator — row-mapped (correlated LATERAL) table functions
//
// fabricator_lateral.hpp
//
// An `ILateralTableFunction` is registered so that its POSITIONAL parameters are real value types and its
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

namespace duckdb {

class DBConfig;

//! The DuckDB setting that flips between the batched and the stock row-by-row path. Declared by the managed
//! HostSettings (provider "fabricator") and read here through DuckDB's own setting store, so the optimizer
//! costs no crossing to consult it. Absent (bridge never booted) => batched, i.e. the shipped default.
extern const char *const FabricatorBatchedLateralSetting;

//! Build the registered TableFunction for one row-mapped provider function. Used by BOTH the load-time global
//! registrar and the attach-time catalog path — they differ only in `handle`/`schema_name`.
TableFunction FabricatorMakeLateralFunction(FabricatorHandle handle, const string &schema_name,
                                           const string &func_name, vector<string> arg_names,
                                           vector<LogicalType> arg_types, vector<FabricatorParamStyle> arg_styles);

//! Register the optimizer extension that rewrites an eligible correlated LogicalGet onto the batched operator.
void RegisterFabricatorLateralOptimizer(DBConfig &config);

} // namespace duckdb
