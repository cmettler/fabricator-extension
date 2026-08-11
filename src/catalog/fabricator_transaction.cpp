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
		//
		// ⚠ THE SESSION IS THE USER'S CONTEXT, NOT THE FLUSH CONNECTION'S — this is the one place where the
		// opener and the settings session can differ, and it is why the session is a parameter rather than
		// something the managed side derives from the opener. The settings that govern a flush-time write
		// (delta_write_options, copy_into_staging, the parquet tuning) were SET on the user's connection;
		// keying them by the flush connection would resolve against one that has set nothing.
		//
		// ⚠ REASONED, NOT MEASURED — say so rather than implying a gate exists. Passing the flush
		// connection's key here instead is a mutant that SURVIVES every shape that could be constructed for
		// it: the eager-write buffer and the transaction hoist moved essentially every tuning-sensitive
		// write to STATEMENT time, where the session is trivially correct. Even the one branch that still
		// retains batches until COMMIT (identity/iceberg, or a pending ALTER) resolved correctly under the
		// mutant. So this is correct-by-construction insurance against a write moving back onto the flush
		// path, not a fix for an observed defect — and `verify_setting_scope` §5 pins the flush path's write
		// tuning WITHOUT being able to distinguish which session supplied it.
		Connection flush_conn(db.GetDatabase());
		flush_conn.BeginTransaction();
		const int64_t user_session = fabricator::SessionKeyFor(&context);
		fabricator::SetActiveOpener(reinterpret_cast<FabricatorHandle>(flush_conn.context.get()), user_session);
		try {
			fabricator::CommitTransaction(handle_);
		} catch (...) {
			fabricator::SetActiveOpener(reinterpret_cast<FabricatorHandle>(&context), user_session);
			flush_conn.Rollback();
			throw;
		}
		// The flush connection was only an opener (secret lookups + FS IO) — nothing of its own to commit.
		fabricator::SetActiveOpener(reinterpret_cast<FabricatorHandle>(&context), user_session);
		flush_conn.Rollback();
	} catch (std::exception &ex) {
		return ErrorData(ex);
	}
	return ErrorData();
}

void FabricatorTransactionManager::RollbackTransaction(Transaction &transaction) {
	auto txn_id = transaction.Cast<FabricatorTransaction>().txn_id_;
	// ⚠ BOTH of these must be read BEFORE the erase: `transactions` OWNS the FabricatorTransaction
	// (unique_ptr), so erasing DESTROYS it and every later use of `transaction` is a use-after-free. That is
	// why txn_id was already hoisted; the settings session has to come along for the same reason.
	// `Transaction::context` is DuckDB's weak_ptr to the connection that opened the transaction — the one
	// whose session-scoped settings govern this rollback. A connection already torn down yields 0, i.e. the
	// global layer: the safe fallback, since a discard reads no write tuning today and resolving against the
	// WRONG session would be worse than resolving against none.
	int64_t user_session = 0;
	if (auto originating = transaction.context.lock()) {
		user_session = fabricator::SessionKeyFor(originating.get());
	}
	{
		lock_guard<mutex> lock(transaction_lock);
		transactions.erase(transaction);
	}
	try {
		fabricator::SetActiveTxn(handle_, txn_id);
		// Host-FS opener for a Delta-catalog ROLLBACK. Rollback USED TO DO NO IO, so it never set one — and
		// that was not merely a gap: whatever `AmbientOpener.Current` held here belonged to an earlier call,
		// i.e. a STALE ClientContext*, which is a use-after-free rather than staleness if anything dereferences
		// it. Since the buffered rollback now DISCARDS its eagerly-written data files (EW #52's
		// DiscardDataFilesAsync) it needs a live one, and for the same reason the COMMIT path does: deleting
		// through DuckDB's FileSystem resolves SECRETs (s3:// / az:// / onelake://), and the secret manager
		// requires an ACTIVE transaction. The caller's is already gone by the time TransactionManager gets
		// here — and unlike CommitTransaction, this override is handed NO ClientContext at all, so there is
		// nothing to restore to afterwards and the opener is cleared to 0 instead of left dangling.
		// (`user_session` — the originating connection's settings scope — was captured above, before the
		// erase destroyed the transaction.)
		Connection rollback_conn(db.GetDatabase());
		rollback_conn.BeginTransaction();
		fabricator::SetActiveOpener(reinterpret_cast<FabricatorHandle>(rollback_conn.context.get()), user_session);
		try {
			fabricator::RollbackTransaction(handle_);
		} catch (...) {
		}
		// Clear BEFORE the connection dies: a handle to a destroyed context is the very hazard above.
		fabricator::SetActiveOpener(0, 0);
		rollback_conn.Rollback();
	} catch (...) {
		// best-effort: never throw out of rollback
		try {
			fabricator::SetActiveOpener(0, 0);
		} catch (...) {
		}
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
