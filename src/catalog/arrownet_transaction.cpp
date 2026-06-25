//===----------------------------------------------------------------------===//
//                         arrownet — transactions (impl)
//===----------------------------------------------------------------------===//

#include "catalog/arrownet_transaction.hpp"

#include "arrownet/clr_host.hpp"
#include "catalog/arrownet_catalog.hpp"
#include "duckdb/transaction/meta_transaction.hpp"

namespace duckdb {

ArrowNetTransaction::ArrowNetTransaction(TransactionManager &manager, ClientContext &context)
    : Transaction(manager, context) {
}

ArrowNetTransactionManager::ArrowNetTransactionManager(AttachedDatabase &db, ArrowNetHandle handle)
    : TransactionManager(db), handle_(handle) {
}

Transaction &ArrowNetTransactionManager::StartTransaction(ClientContext &context) {
	auto transaction = make_uniq<ArrowNetTransaction>(*this, context);
	auto &result = *transaction;
	// Capture the DuckDB transaction id so all of this transaction's writes/reads on the backend key the
	// SAME per-transaction provider connection (and so commit/rollback target it). Distinct concurrent
	// transactions (e.g. dbt --threads N) get distinct ids => distinct connections.
	result.txn_id_ = (int64_t)MetaTransaction::Get(context).global_transaction_id;
	{
		lock_guard<mutex> lock(transaction_lock);
		transactions[result] = std::move(transaction);
	}
	// Enter transaction mode on the backend; the provider connection is pinned lazily on the first write,
	// keyed by the active transaction id. Best-effort: a failure here must not abort the statement.
	try {
		arrownet::SetActiveTxn(handle_, result.txn_id_);
		arrownet::BeginTransaction(handle_);
	} catch (...) {
	}
	return result;
}

ErrorData ArrowNetTransactionManager::CommitTransaction(ClientContext &context, Transaction &transaction) {
	auto txn_id = transaction.Cast<ArrowNetTransaction>().txn_id_;
	{
		lock_guard<mutex> lock(transaction_lock);
		transactions.erase(transaction);
	}
	try {
		arrownet::SetActiveTxn(handle_, txn_id);
		arrownet::CommitTransaction(handle_);
	} catch (std::exception &ex) {
		return ErrorData(ex);
	}
	return ErrorData();
}

void ArrowNetTransactionManager::RollbackTransaction(Transaction &transaction) {
	auto txn_id = transaction.Cast<ArrowNetTransaction>().txn_id_;
	{
		lock_guard<mutex> lock(transaction_lock);
		transactions.erase(transaction);
	}
	try {
		arrownet::SetActiveTxn(handle_, txn_id);
		arrownet::RollbackTransaction(handle_);
	} catch (...) {
		// best-effort: never throw out of rollback
	}
	// Discard any catalog entry that an ALTER's eager re-fetch cached from this now-undone (uncommitted)
	// schema, so the next access re-fetches the committed state. Best-effort; never throw out of rollback.
	try {
		db.GetCatalog().Cast<ArrowNetCatalog>().InvalidateAllEntries();
	} catch (...) {
	}
}

void ArrowNetTransactionManager::Checkpoint(ClientContext &context, bool force) {
	// Nothing to checkpoint for a remote catalog.
}

} // namespace duckdb
