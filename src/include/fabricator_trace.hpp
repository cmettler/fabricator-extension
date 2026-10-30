//===----------------------------------------------------------------------===//
//                         fabricator
//
// fabricator_trace.hpp
//
// Routing decisions of the pushdowns, written to DuckDB's own log.
//===----------------------------------------------------------------------===//

#pragma once

#include "duckdb/common/printer.hpp"
#include "duckdb/logging/log_manager.hpp"
#include "duckdb/logging/log_type.hpp"
#include "duckdb/logging/logger.hpp"
#include "duckdb/main/database.hpp"
#include "duckdb/main/client_context.hpp"

#include <cstdlib>
#include <cstring>

namespace duckdb {

//! Log types of the routing decisions. `duckdb_logs.type` carries them, and `CALL enable_logging('Fabricator.Remote')`
//! selects one alone (registered at load, see FabricatorRegisterLogTypes).
static constexpr const char *FABRICATOR_LOG_REMOTE = "Fabricator.Remote"; // statement / subtree pushdown
static constexpr const char *FABRICATOR_LOG_DML = "Fabricator.Dml";       // the DELETE/UPDATE … WHERE shortcut

//! Why a statement (or a part of one) ran on the provider or stayed local. EXPLAIN shows WHERE it ran, never why,
//! and a fallback costs performance without saying so — so every decision goes to `duckdb_logs` at DEBUG. The
//! environment variable `env` still prints it to stderr for the harnesses that read it.
inline void FabricatorRoutingTrace(ClientContext &context, const char *log_type, const char *env,
                                   const string &message) {
	auto &logger = Logger::Get(context);
	if (logger.ShouldLog(log_type, LogLevel::LOG_DEBUG)) {
		logger.WriteLog(log_type, LogLevel::LOG_DEBUG, message);
	}
	auto v = std::getenv(env);
	if (v && *v && string(v) != "0") {
		Printer::Print(string(std::strcmp(log_type, "Fabricator.Dml") == 0 ? "[fabricator dml] " : "[fabricator remote] ") +
		               message);
	}
}

//! Registers our log types so `enable_logging` can select them by name (it refuses an unknown type). DEBUG, so
//! selecting one also lowers the level to where its messages are written. `Fabricator.Sql` is the managed category
//! that records every T-SQL statement a SQL Server catalog sends (forwarded through host_log).
inline void FabricatorRegisterLogTypes(DatabaseInstance &db) {
	auto &manager = db.GetLogManager();
	for (auto name : {FABRICATOR_LOG_REMOTE, FABRICATOR_LOG_DML, "Fabricator.Sql"}) {
		if (!manager.LookupLogType(name)) {
			manager.RegisterLogType(make_uniq<LogType>(name, LogLevel::LOG_DEBUG));
		}
	}
}

} // namespace duckdb
