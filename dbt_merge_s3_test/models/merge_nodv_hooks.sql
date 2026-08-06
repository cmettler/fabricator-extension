-- PER-MODEL AUTOCOMMIT, without a second target and without touching dbt-duckdb.
--
-- dbt has no model-level transaction switch (`disable_transactions` is a PROFILE/target field,
-- dbt/adapters/duckdb/credentials.py:214), but the materialization's hook ordering can be used to hand the
-- transaction back before the model statement runs:
--
--   run_hooks(pre_hooks,  inside_transaction=True)   <- statement(auto_begin=True) => BEGIN, then 'COMMIT'
--   ... the model's MERGE ...                        <- now runs in AUTOCOMMIT
--   run_hooks(post_hooks, inside_transaction=True)   <- 'BEGIN' re-opens one
--   adapter.commit()                                 <- ...so dbt's own COMMIT still has a transaction
--
-- The post-hook BEGIN is not cosmetic: dbt tracks transaction_open itself, so without it dbt's final COMMIT
-- is issued against a connection with no active transaction and the model fails after its data landed.
--
-- COST OF THIS TRICK, stated plainly: the merge is no longer atomic with anything else dbt does for the
-- model, and on a table WITH deletion vectors it is strictly worse than leaving dbt alone (a 1-action merge
-- in autocommit splits across two Delta commits instead of fusing into one). Use it only where the buffered
-- path is refused, i.e. deletion_vectors=false.
{{ config(
    materialized='incremental',
    incremental_strategy='merge',
    unique_key='id',
    pre_hook="COMMIT",
    post_hook="BEGIN"
) }}

{% set batch = var('batch', 1) | int %}

{% if batch == 1 %}
select i::BIGINT as id, (i * 10)::BIGINT as v from range(1, 6) t(i)
{% else %}
select i::BIGINT as id, (i * 100)::BIGINT as v from range(4, 9) t(i)
{% endif %}
