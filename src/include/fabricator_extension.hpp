// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

//===----------------------------------------------------------------------===//
//                         DuckDB fabricator Extension
//
// fabricator_extension.hpp
//
// DuckDB extension that connects to Microsoft SQL Server through a
// CoreCLR-hosted C# bridge, exchanging data as Apache Arrow.
//===----------------------------------------------------------------------===//

#pragma once

#include "duckdb.hpp"

namespace duckdb {

class FabricatorExtension : public Extension {
public:
	void Load(ExtensionLoader &loader) override;
	std::string Name() override;
	std::string Version() const override;
};

} // namespace duckdb
