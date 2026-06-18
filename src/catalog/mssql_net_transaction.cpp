//===----------------------------------------------------------------------===//
//                         mssql_net — transactions (impl)
//===----------------------------------------------------------------------===//

#include "catalog/mssql_net_transaction.hpp"

#include "arrownet/clr_host.hpp"

namespace duckdb {

MssqlNetTransaction::MssqlNetTransaction(TransactionManager &manager, ClientContext &context)
    : Transaction(manager, context) {
}

MssqlNetTransactionManager::MssqlNetTransactionManager(AttachedDatabase &db, ArrowNetHandle handle)
    : TransactionManager(db), handle_(handle) {
}

Transaction &MssqlNetTransactionManager::StartTransaction(ClientContext &context) {
	auto transaction = make_uniq<MssqlNetTransaction>(*this, context);
	auto &result = *transaction;
	{
		lock_guard<mutex> lock(transaction_lock);
		transactions[result] = std::move(transaction);
	}
	// Enter transaction mode on the backend; the provider transaction is pinned
	// lazily on the first write. Best-effort: a failure here must not abort the
	// statement (writes would then fail loudly on their own).
	try {
		arrownet::BeginTransaction(handle_);
	} catch (...) {
	}
	return result;
}

ErrorData MssqlNetTransactionManager::CommitTransaction(ClientContext &context, Transaction &transaction) {
	{
		lock_guard<mutex> lock(transaction_lock);
		transactions.erase(transaction);
	}
	try {
		arrownet::CommitTransaction(handle_);
	} catch (std::exception &ex) {
		return ErrorData(ex);
	}
	return ErrorData();
}

void MssqlNetTransactionManager::RollbackTransaction(Transaction &transaction) {
	{
		lock_guard<mutex> lock(transaction_lock);
		transactions.erase(transaction);
	}
	try {
		arrownet::RollbackTransaction(handle_);
	} catch (...) {
		// best-effort: never throw out of rollback
	}
}

void MssqlNetTransactionManager::Checkpoint(ClientContext &context, bool force) {
	// Nothing to checkpoint for a remote catalog.
}

} // namespace duckdb
