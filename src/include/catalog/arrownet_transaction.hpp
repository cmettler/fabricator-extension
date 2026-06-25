//===----------------------------------------------------------------------===//
//                         arrownet — transactions (minimal, read-only)
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
class ArrowNetTransaction : public Transaction {
public:
	ArrowNetTransaction(TransactionManager &manager, ClientContext &context);

	// The DuckDB transaction id (global_transaction_id), captured at StartTransaction. Used to key the
	// managed backend's per-transaction provider connection, and to tell the backend which transaction to
	// commit/roll back (RollbackTransaction has no ClientContext to re-derive it from).
	int64_t txn_id_ = 0;
};

class ArrowNetTransactionManager : public TransactionManager {
public:
	ArrowNetTransactionManager(AttachedDatabase &db, ArrowNetHandle handle);

	Transaction &StartTransaction(ClientContext &context) override;
	ErrorData CommitTransaction(ClientContext &context, Transaction &transaction) override;
	void RollbackTransaction(Transaction &transaction) override;
	void Checkpoint(ClientContext &context, bool force = false) override;

private:
	ArrowNetHandle handle_;
	mutex transaction_lock;
	reference_map_t<Transaction, unique_ptr<ArrowNetTransaction>> transactions;
};

} // namespace duckdb
