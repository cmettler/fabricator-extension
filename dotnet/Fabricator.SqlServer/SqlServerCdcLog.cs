// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using Apache.Arrow;
using Apache.Arrow.Types;
using Fabricator.Bridge;

namespace Fabricator.SqlServer;

/// <summary>
/// <c>SELECT * FROM db.cdc.ddl_history([source] [, starting_position] [, ending_position])</c> — the DDL
/// statements CDC recorded against captured tables, resolved to names and bounded by LSN.
/// </summary>
/// <remarks>
/// <para><b>Why a function when the table is already reachable.</b> An ATTACH surfaces <c>cdc.ddl_history</c>
/// itself, so this is not about access — it is about the three things a raw read makes the caller do by
/// hand: joining <c>source_object_id</c> and <c>object_id</c> back to names (the raw table has only ids),
/// bounding by an LSN window with the same exclusivity the reader uses, and reading without taking locks on
/// a table the capture job is writing.</para>
/// <para><b>⚠ THE BOUNDS MEAN WHAT THEY MEAN IN <c>cdc.changes</c> — <c>starting_position</c> EXCLUSIVE,
/// <c>ending_position</c> INCLUSIVE</b> (§17). Inclusive-both would have been the obvious choice for an
/// inspection function and it would have been a trap: the point of these bounds is that a caller can hand
/// them the SAME cursor they hand the reader and get the DDLs for exactly the window they are about to read.
/// A second convention on one surface is how an off-by-one becomes somebody's afternoon.</para>
/// <para>⚠ A bound may be a 10-byte LSN or a 21-byte <c>_position</c>; only the LSN part is compared, because
/// <c>ddl_lsn</c> is an LSN. So the exclusivity is at LSN granularity — a DDL sharing an LSN with the
/// cursor's row is excluded.</para>
/// <para><b>⚠ ONE ROW PER (DDL × CAPTURE INSTANCE), and it is NOT de-duplicated here.</b> MEASURED (§19): a
/// table with two instances records every DDL TWICE — including DDLs that predate the newer instance, which
/// SQL Server back-fills onto it — so a <c>COUNT(*)</c> over this reports "2 schema changes" for one
/// <c>ALTER</c>. The drift check inside the reader uses <c>DISTINCT</c> for exactly that reason. It is left
/// raw here because <c>capture_instance</c> is on every row: the caller can see the duplication and decide,
/// where collapsing it would hide which instances a DDL was applied to.</para>
/// <para><b>⚠ READ WITH <c>NOLOCK</c>, deliberately and at a stated cost.</b> The capture job writes this
/// table, and an inspection query has no business blocking it or being blocked by it. The cost is the usual
/// one: a dirty read can see a DDL row the recording transaction later rolls back. That is acceptable HERE
/// — this is a diagnostic, its answer is advisory, and nothing branches on it — and it is precisely why the
/// reader's own drift check does NOT use this function but issues its own guarded query.</para>
/// </remarks>
internal sealed class CdcDdlHistoryFunction : ICatalogTableFunction
{
    private readonly SqlServerCatalog _catalog;

    internal CdcDdlHistoryFunction(SqlServerCatalog catalog) => _catalog = catalog;

    public string SchemaName => SqlServerCdcFunctions.SchemaName;

    public string Name => "ddl_history";

    public Schema Parameters { get; } = new(new[]
    {
        Params.Named("source", StringType.Default),
        Params.Named("starting_position", BinaryType.Default),
        Params.Named("ending_position", BinaryType.Default),
    }, metadata: null);

    internal static Schema Columns { get; } = new(new[]
    {
        new Field("source_schema", StringType.Default, nullable: true),
        new Field("source_table", StringType.Default, nullable: true),
        new Field("capture_instance", StringType.Default, nullable: true),
        new Field("ddl_lsn", BinaryType.Default, nullable: true),
        new Field("ddl_time", new TimestampType(TimeUnit.Microsecond, (string?)null), nullable: true),
        // ⚠ 1 for exactly an ALTER COLUMN <type> (MEASURED, §18.3) - the one DDL kind the capture job
        // PROPAGATES to the change table, asynchronously. An ADD and a DROP both report 0.
        new Field("required_column_update", BooleanType.Default, nullable: true),
        new Field("ddl_command", StringType.Default, nullable: true),
    }, metadata: null);

    public ITableFunctionBinding Bind(RecordBatch args)
    {
        // Read every argument HERE: the stream they came from is disposed when the bind returns.
        string? source = CdcEnableFunction.Str(args, 0);
        byte[]? from = CdcChangesPlan.ValidatePosition(CdcEnableFunction.Blob(args, 1), "starting_position");
        byte[]? to = CdcChangesPlan.ValidatePosition(CdcEnableFunction.Blob(args, 2), "ending_position");
        return new CdcRowsBinding(Columns, () => _catalog.CdcDdlHistory(source, from, to));
    }
}

/// <summary>
/// <c>SELECT * FROM db.cdc.lsn_time_mapping([starting_position] [, ending_position])</c> — the LSN ↔ time
/// bridge, one row per captured transaction.
/// </summary>
/// <remarks>
/// <para>This is where a commit timestamp comes from, and the only place an LSN can be turned back into a
/// wall-clock instant. <c>cdc.changes(commit_timestamp := true)</c> joins it for one output column; this
/// exposes it directly, so a caller can answer "when was this cursor?" without a read.</para>
/// <para><b>⚠ <c>tran_end_time</c> is a <c>datetime</c> — ~3.33 ms resolution (§1.6).</b> Two transactions
/// inside one tick carry the same timestamp. It is metadata, never an ordering key; <c>start_lsn</c> is the
/// ordering key, and its unsigned bytewise order IS the change order.</para>
/// <para>⚠ Bounds are the reader's: <c>starting_position</c> EXCLUSIVE, <c>ending_position</c> INCLUSIVE,
/// compared on the LSN part of either length.</para>
/// <para>⚠ <c>NOLOCK</c>, for the same reason and at the same cost as <c>ddl_history</c>: the capture job
/// writes this table continuously, and a diagnostic must not contend with it.</para>
/// <para>⚠ DATABASE-wide, not per capture instance — every captured transaction in the database appears
/// here, whichever table it touched. That is what makes it usable for "what was happening at this LSN" and
/// also why a bound resolved through it can sit BELOW a particular instance's retention floor (§21.2).</para>
/// </remarks>
internal sealed class CdcLsnTimeMappingFunction : ICatalogTableFunction
{
    private readonly SqlServerCatalog _catalog;

    internal CdcLsnTimeMappingFunction(SqlServerCatalog catalog) => _catalog = catalog;

    public string SchemaName => SqlServerCdcFunctions.SchemaName;

    public string Name => "lsn_time_mapping";

    public Schema Parameters { get; } = new(new[]
    {
        Params.Named("starting_position", BinaryType.Default),
        Params.Named("ending_position", BinaryType.Default),
    }, metadata: null);

    internal static Schema Columns { get; } = new(new[]
    {
        new Field("start_lsn", BinaryType.Default, nullable: true),
        new Field("tran_begin_time", new TimestampType(TimeUnit.Microsecond, (string?)null), nullable: true),
        new Field("tran_end_time", new TimestampType(TimeUnit.Microsecond, (string?)null), nullable: true),
        new Field("tran_id", BinaryType.Default, nullable: true),
        new Field("tran_begin_lsn", BinaryType.Default, nullable: true),
    }, metadata: null);

    public ITableFunctionBinding Bind(RecordBatch args)
    {
        byte[]? from = CdcChangesPlan.ValidatePosition(CdcEnableFunction.Blob(args, 0), "starting_position");
        byte[]? to = CdcChangesPlan.ValidatePosition(CdcEnableFunction.Blob(args, 1), "ending_position");
        return new CdcRowsBinding(Columns, () => _catalog.CdcLsnTimeMapping(from, to));
    }
}
