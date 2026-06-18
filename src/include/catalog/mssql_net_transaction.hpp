//===----------------------------------------------------------------------===//
//                         mssql_net — transactions (minimal, read-only)
//===----------------------------------------------------------------------===//

#pragma once

#include "arrownet/abi.h"
#include "duckdb/common/mutex.hpp"
#include "duckdb/common/reference_map.hpp"
#include "duckdb/transaction/transaction.hpp"
#include "duckdb/transaction/transaction_manager.hpp"

namespace duckdb {

// A DuckDB transaction over the attached SQL Server catalog. The actual provider
// transaction is pinned lazily in the managed backend on the first write.
class MssqlNetTransaction : public Transaction {
public:
	MssqlNetTransaction(TransactionManager &manager, ClientContext &context);
};

class MssqlNetTransactionManager : public TransactionManager {
public:
	MssqlNetTransactionManager(AttachedDatabase &db, ArrowNetHandle handle);

	Transaction &StartTransaction(ClientContext &context) override;
	ErrorData CommitTransaction(ClientContext &context, Transaction &transaction) override;
	void RollbackTransaction(Transaction &transaction) override;
	void Checkpoint(ClientContext &context, bool force = false) override;

private:
	ArrowNetHandle handle_;
	mutex transaction_lock;
	reference_map_t<Transaction, unique_ptr<MssqlNetTransaction>> transactions;
};

} // namespace duckdb
