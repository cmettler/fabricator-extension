//===----------------------------------------------------------------------===//
//                         DuckDB mssql_net Extension
//
// mssql_net_extension.hpp
//
// DuckDB extension that connects to Microsoft SQL Server through a
// CoreCLR-hosted C# bridge, exchanging data as Apache Arrow.
//===----------------------------------------------------------------------===//

#pragma once

#include "duckdb.hpp"

namespace duckdb {

class MssqlNetExtension : public Extension {
public:
	void Load(ExtensionLoader &loader) override;
	std::string Name() override;
	std::string Version() const override;
};

} // namespace duckdb
