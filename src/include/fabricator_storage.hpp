//===----------------------------------------------------------------------===//
//                         fabricator — storage extension registration
//===----------------------------------------------------------------------===//

#pragma once

namespace duckdb {

class ExtensionLoader;

//! Registers the "fabricator" storage extension so that
//! `ATTACH '<connstr>' AS db (TYPE fabricator)` works.
void RegisterFabricatorStorageExtension(ExtensionLoader &loader);

} // namespace duckdb
