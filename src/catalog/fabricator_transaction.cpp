//===----------------------------------------------------------------------===//
//                         fabricator — transactions (impl)
//===----------------------------------------------------------------------===//

#include "catalog/fabricator_transaction.hpp"

#include "fabricator/clr_host.hpp"
#include "catalog/fabricator_catalog.hpp"
#include "duckdb/main/client_context.hpp"
#include "duckdb/main/connection.hpp"
#include "duckdb/transaction/meta_transaction.hpp"

namespace duckdb {

FabricatorTransaction::FabricatorTransaction(TransactionManager &manager, ClientContext &context)
    : Transaction(manager, context) {
}

FabricatorTransactionManager::FabricatorTransactionManager(AttachedDatabase &db, FabricatorHandle handle)
    : TransactionManager(db), handle_(handle) {
}

Transaction &FabricatorTransactionManager::StartTransaction(ClientContext &context) {
	auto transaction = make_uniq<FabricatorTransaction>(*this, context);
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
		fabricator::SetActiveTxn(handle_, result.txn_id_);
		// Explicit user BEGIN vs the implicit per-statement autocommit wrapper: a provider that buffers
		// transactional DML (the Delta provider) may only change statement-visible semantics for an
		// explicit transaction.
		fabricator::BeginTransaction(handle_, !context.transaction.IsAutoCommit());
	} catch (...) {
	}
	return result;
}

ErrorData FabricatorTransactionManager::CommitTransaction(ClientContext &context, Transaction &transaction) {
	auto txn_id = transaction.Cast<FabricatorTransaction>().txn_id_;
	{
		lock_guard<mutex> lock(transaction_lock);
		transactions.erase(transaction);
	}
	try {
		fabricator::SetActiveTxn(handle_, txn_id);
		// Host-FS opener for a Delta-catalog COMMIT: flushing the transaction's buffered changes writes the
		// _delta_log through DuckDB's FileSystem with SECRET resolution (s3:// / az:// / onelake://). The
		// CALLER's transaction is no longer active inside TransactionManager::CommitTransaction, and the
		// secret manager requires an active one (httpfs S3 fails with "ActiveTransaction called without
		// active transaction") — so the flush gets its OWN short-lived connection + transaction as the
		// opener. Local paths need no secrets; SQL Server / DAX ignore the opener entirely.
		Connection flush_conn(db.GetDatabase());
		flush_conn.BeginTransaction();
		fabricator::SetActiveOpener(reinterpret_cast<FabricatorHandle>(flush_conn.context.get()));
		try {
			fabricator::CommitTransaction(handle_);
		} catch (...) {
			fabricator::SetActiveOpener(reinterpret_cast<FabricatorHandle>(&context));
			flush_conn.Rollback();
			throw;
		}
		// The flush connection was only an opener (secret lookups + FS IO) — nothing of its own to commit.
		fabricator::SetActiveOpener(reinterpret_cast<FabricatorHandle>(&context));
		flush_conn.Rollback();
	} catch (std::exception &ex) {
		return ErrorData(ex);
	}
	return ErrorData();
}

void FabricatorTransactionManager::RollbackTransaction(Transaction &transaction) {
	auto txn_id = transaction.Cast<FabricatorTransaction>().txn_id_;
	{
		lock_guard<mutex> lock(transaction_lock);
		transactions.erase(transaction);
	}
	try {
		fabricator::SetActiveTxn(handle_, txn_id);
		fabricator::RollbackTransaction(handle_);
	} catch (...) {
		// best-effort: never throw out of rollback
	}
	// Discard any catalog entry that an ALTER's eager re-fetch cached from this now-undone (uncommitted)
	// schema, so the next access re-fetches the committed state. Best-effort; never throw out of rollback.
	try {
		db.GetCatalog().Cast<FabricatorCatalog>().InvalidateAllEntries();
	} catch (...) {
	}
}

void FabricatorTransactionManager::Checkpoint(ClientContext &context, bool force) {
	// Nothing to checkpoint for a remote catalog.
}

} // namespace duckdb
