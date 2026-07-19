{{ config(
    materialized='table',
    post_hook="select fabricator_exec('{{ this.database }}', 'ALTER TABLE [dbo].[__no_such_table_xyz__] ADD c int')"
) }}
-- Post-hook INSIDE the transaction that deliberately ERRORS (ALTER a non-existent table ->
-- SQL Server error 4902 surfaced by fabricator_exec). The error propagates to dbt, which rolls
-- back the model's transaction -> the model table's CREATE is undone. After this run fails, the
-- table [dbo].[hk_rollback] must NOT exist on the server (rollback-of-resource behavior).
select 1 as id, 'a' as name
union all select 2, 'b'
