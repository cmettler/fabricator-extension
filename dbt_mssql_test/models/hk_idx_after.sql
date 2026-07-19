{{ config(
    materialized='table',
    post_hook={
      "sql": "select fabricator_exec('{{ this.database }}', 'CREATE INDEX [ix_{{ this.identifier }}] ON [{{ this.schema }}].[{{ this.identifier }}] ([id])')",
      "transaction": false
    }
) }}
-- Post-hook OUTSIDE the model's transaction (transaction:false): the model has already
-- COMMITTED, so fabricator_exec (which runs on its own autocommit connection) can see the
-- table and add a nonclustered index. This is the working pattern for SQL-Server-specific
-- DDL in a hook. NOTE: it is NOT atomic with the model (a hook failure won't roll it back).
select 1 as id, 'a' as name
union all select 2, 'b'
union all select 3, 'c'
