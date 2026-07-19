{{ config(
    materialized='table',
    post_hook="select fabricator_exec('{{ this.database }}', 'CREATE INDEX [ix_{{ this.identifier }}] ON [{{ this.schema }}].[{{ this.identifier }}] ([id])')"
) }}
-- Post-hook INSIDE the model's transaction (default transaction:true): the model's CREATE is
-- still UNCOMMITTED on dbt's per-transaction connection when the hook runs. fabricator_exec
-- autocommits on a SEPARATE connection, which cannot see the uncommitted table -> the
-- CREATE INDEX fails ("Invalid object name"). Demonstrates the limitation: you cannot modify
-- the just-created model from an in-transaction hook via fabricator_exec.
select 1 as id, 'a' as name
union all select 2, 'b'
union all select 3, 'c'
