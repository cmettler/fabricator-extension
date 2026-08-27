// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

//===----------------------------------------------------------------------===//
//                         fabricator — COPY TO (bulk load)
//===----------------------------------------------------------------------===//

#pragma once

namespace duckdb {

class ExtensionLoader;

//! Registers the `fabricator` COPY format:
//!   COPY (query) TO 'mssql://catalog/schema/table' (FORMAT mssql)
//!   COPY tbl     TO 'catalog.schema.table'         (FORMAT mssql)
//! Reuses the generic Arrow bulk-load path (provider does CREATE TABLE + copy).
void RegisterFabricatorCopyFunction(ExtensionLoader &loader);

} // namespace duckdb
