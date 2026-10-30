// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

//===----------------------------------------------------------------------===//
//                         fabricator — optimizer extension
//
// Pushes a bare LIMIT (no ORDER BY) on a fabricator catalog scan down to SQL
// Server as `SELECT TOP (n)`. Safe because an unordered LIMIT may return any n
// rows, and DuckDB keeps its own LIMIT operator (the pushdown only reduces the
// rows that cross the wire). ORDER BY / TopN pushdown is intentionally not done
// here yet (it needs column-nullability + collation handling to stay correct).
//===----------------------------------------------------------------------===//

#pragma once

namespace duckdb {

class DBConfig;

void RegisterFabricatorOptimizer(DBConfig &config);

//! Pushes each subtree that reads one SQL Server catalog alone (remote_pushdown) to that server as one query —
//! catalog/fabricator_remote_select.cpp, docs/duckdb-2.0-remote-execution.md §4.10.
void RegisterFabricatorSubtreePushdown(DBConfig &config);

} // namespace duckdb
