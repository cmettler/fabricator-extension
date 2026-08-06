-- The user's exact config: the default dbt-duckdb merge strategy.
--
-- dbt renders this to ONE row-addressing action:
--     WHEN MATCHED THEN UPDATE BY NAME
--     WHEN NOT MATCHED THEN INSERT BY NAME
-- so fabricator's ">= 2 UPDATE/DELETE actions" forcing rule does NOT fire here. What DOES apply is
-- whichever transaction mode dbt runs it in — which is the whole question this project exists to answer.
--
-- Batch selection is a var rather than is_incremental() so each run is deterministic and re-runnable:
--   batch 1 -> ids 1..5   (creates the table)
--   batch 2 -> ids 4..8   (updates 4,5 ; inserts 6,7,8)
{{ config(
    materialized='incremental',
    incremental_strategy='merge',
    unique_key='id'
) }}

{% set batch = var('batch', 1) | int %}

{% if batch == 1 %}
select i::BIGINT as id, (i * 10)::BIGINT as v, 'b1' as tag from range(1, 6) t(i)
{% else %}
select i::BIGINT as id, (i * 100)::BIGINT as v, 'b2' as tag from range(4, 9) t(i)
{% endif %}
