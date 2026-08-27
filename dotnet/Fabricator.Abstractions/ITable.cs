// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace Fabricator.Bridge;

/// <summary>A time-travel reference (the <c>AT (VERSION =&gt; …)</c> / <c>AT (TIMESTAMP =&gt; …)</c> clause):
/// <paramref name="Unit"/> is <c>"version"</c> or <c>"timestamp"</c>, <paramref name="Value"/> the literal
/// rendered by the host. Carried on the BINDING (not the scan) because time travel is a property of a table
/// REFERENCE — the object-model form of the C++ fact that AT entries live in their own map.</summary>
public readonly record struct TableAt(string Unit, string Value);

/// <summary>A provider VIRTUAL column (queryable by name, excluded from <c>SELECT *</c>), e.g. the Delta
/// row-tracking pair. <paramref name="DuckDbType"/> is the DuckDB type NAME the host parses.</summary>
public readonly record struct VirtualColumn(string Name, string DuckDbType);

/// <summary>One column's approximate distinct-value count (optimizer costing only, never pruning).</summary>
public readonly record struct NdvEntry(string ColumnName, long Ndv);

/// <summary>
/// A table's per-catalog-entry DEFINITION — identity plus the bind factory; deliberately TRANSACTION-FREE
/// (slice 4c of docs/catalog-table-abstraction.md §2.2/§2.3). The deliberate <c>ITableFunction</c> symmetry:
/// "like a table function but binding a TRANSACTION instead of args", with the one argument DuckDB's own
/// grammar gives a table reference — the AT clause.
/// </summary>
/// <remarks>
/// Since the <c>table_*</c> session (ABI v72) a definition lives for the C++ catalog entry's lifetime behind
/// a <c>table_open</c> handle (retire-don't-destroy graveyard), and every session call re-binds it against
/// the CURRENT ambient transaction. It still holds NO cached state — that is what makes the long-lived
/// handle trivially safe: statefulness with a lifetime lives on the BOUND table (per transaction) or on the
/// provider's existing entry caches, both of which the per-transaction invalidation already owns.
/// </remarks>
public interface ITable
{
    string SchemaName { get; }
    string TableName { get; }

    /// <summary>
    /// Binds this definition to a transaction, resolving STATE rather than args: the pinned snapshot, the
    /// transaction's pending CREATE/ALTER shape override, the connection borrow, and an AT-dependent schema.
    /// </summary>
    /// <remarks>
    /// <para><b>Ownership:</b> a plain bind (<paramref name="at"/> null) against a live transaction MAY be
    /// memoized — the returned table is then OWNED BY THE TRANSACTION and disposed with it; the caller must
    /// not dispose it. A transaction-free bind (<paramref name="transaction"/> null) and an AT bind are
    /// always fresh instances the CALLER owns. An AT binding is NEVER the shared instance — time travel is a
    /// property of a reference, not of the catalog entry.</para>
    /// <para><paramref name="transaction"/> is the provider's own <see cref="ITransaction"/> (from its
    /// <see cref="TransactionManager{T}"/>); a provider downcasts. Null = a genuinely transaction-free
    /// context (a global function), matching the ambient-id-0 convention.</para>
    /// </remarks>
    ITableBinding Bind(ITransaction? transaction, TableAt? at = null);
}

/// <summary>
/// A table BOUND to a transaction — the session where Schema/Info resolution and the scan live, and where
/// ALL per-(table × transaction) state belongs (docs/catalog-table-abstraction.md §2.3). Replaces the
/// (schema, table) name pair the <see cref="IBackendCatalog"/> surface passes on every member — the
/// name-pair-per-call convention is why nothing could hold state.
/// </summary>
/// <remarks>
/// <para><b>BIND IS STATE, SCAN IS REQUEST.</b> Filters/projection NEVER ride the binding: one binding
/// serves N scans with DIFFERENT specs (a self-join is one bound table, two scans, two filter sets), and
/// dynamic/join filters only exist at execution. <see cref="Scan"/> must therefore tolerate CONCURRENT
/// invocations on one binding, with per-scan state living on the RETURNED stream.</para>
/// <para><b>Schema/Info resolution is deliberately UN-MEMOIZED</b>: it consults the transaction's pending
/// (CREATE/ALTER) shape first and then storage — memoizing on the binding would serve a same-transaction
/// mutation a stale shape. Providers keep resolution cheap through their per-transaction open reuse
/// instead.</para>
/// <para><b>The write paths never read through bound-table caches</b> — a commit flush opens FRESH so its
/// conflict range stays empty (<c>verify_delta_catalog_transactions</c> §41). That contract predates this
/// interface and moves onto it unchanged.</para>
/// <para>The statistics members are LAZY BY SHAPE (methods, not eagerly-computed properties): they may
/// issue a provider query, they are costing-only, and a provider that surfaces none answers null/empty —
/// the warehouse engines MUST answer null/empty rather than probe (a failed best-effort statement aborts an
/// open Fabric transaction).</para>
/// </remarks>
public interface ITableBinding : IDisposable
{
    /// <summary>The table's columns as an Arrow schema — typed (the <c>table_schema</c> entry re-encodes it
    /// as a zero-row stream only at the ABI edge, for the host's proven import path). A buffered
    /// transaction's pending CREATE/ALTER shape wins over storage; absence throws
    /// <see cref="ObjectNotFoundException"/> (established, never inferred). May do IO.</summary>
    Schema Schema { get; }

    /// <summary>The row-identity column names (PK / unique index / IDENTITY / a provider virtual rowid),
    /// empty when the table has none (UPDATE/DELETE then unavailable). May do IO.</summary>
    /// <remarks>
    /// <para><b>Resolution rules</b> — the host resolves each name CASE-INSENSITIVELY against
    /// <see cref="Schema"/>, and <see cref="VirtualColumns"/> plays no part. The names must either ALL
    /// resolve to schema columns — a REAL rowid, typed FROM the schema (the column's own type, or a STRUCT
    /// of the key columns when compound; the scan fetches them as ordinary columns and packs the struct
    /// host-side, so the provider's result set never carries a composite) — or ALL be absent from it — a
    /// provider-VIRTUAL rowid, whose name the scan spec forwards verbatim for the provider's OWN reader to
    /// recognize and synthesize. A MIXED resolution silently DISABLES the rowid (a partial key addresses
    /// the wrong rows, which is worse than no key); the provider gets no signal, so treat a mixed list as a
    /// declaration bug, never a fallback.</para>
    /// <para>⚠ <b>The host types a virtual rowid BIGINT per component.</b> That is a host-side assumption
    /// (<c>fabricator_schema_entry.cpp</c>) matching the one existing virtual-rowid provider — Delta's
    /// packed <c>(fileOrdinal &lt;&lt; 40) | position</c> — not a declared type: <c>table_info</c> carries
    /// no type channel for it. A non-BIGINT virtual identity would be silently mistyped (the
    /// ingestion-mismatch failure class, not a clean error); the day one is needed, extend the rowid
    /// entries from <c>"name"</c> to <c>{"name","type"}</c> — additive JSON, unknown-key-safe.</para>
    /// </remarks>
    IReadOnlyList<string> RowIdColumns();

    /// <summary>Provider virtual columns (queryable by name, excluded from <c>SELECT *</c>); empty when
    /// none. May do IO.</summary>
    IReadOnlyList<VirtualColumn> VirtualColumns();

    /// <summary>Approximate row count for optimizer cardinality, or null when the provider surfaces none
    /// (Delta; warehouse engines, where the stats DMVs are unsupported and probing would poison an open
    /// transaction). May do IO.</summary>
    long? ApproximateRowCount();

    /// <summary>Per-column approximate NDV (leading-column histogram; costing only), empty when the
    /// provider surfaces none. May do IO.</summary>
    IReadOnlyList<NdvEntry> ColumnNdv();

    /// <summary>
    /// Scans the table. <paramref name="specJson"/> (null =&gt; SELECT *) + <paramref name="filterValues"/>
    /// carry projection + best-effort filter pushdown per EXECUTION, exactly as
    /// <see cref="IBackendCatalog.ScanTable"/> — including the <c>schema_only</c> bind probe and, in the
    /// current transport, the AT clause (which for an AT-bound instance matches the binding's own).
    /// </summary>
    IArrowArrayStream Scan(string? specJson, IArrowArrayStream? filterValues);
}
