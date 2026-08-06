-- The shape that DOES trip fabricator's forcing rule: TWO row-addressing actions in one merge.
--
-- dbt-duckdb's merge_clauses config renders:
--     WHEN MATCHED AND DBT_INTERNAL_SOURCE.is_deleted THEN DELETE
--     WHEN MATCHED THEN UPDATE BY NAME
--     WHEN NOT MATCHED THEN INSERT BY NAME
-- => 2 UPDATE/DELETE actions on a table whose rowid is POSITIONAL (Delta's virtual (file, position)),
-- which is exactly the condition under which fabricator forces the statement to buffer so both actions
-- stage against one pinned snapshot. Without that forcing a copy-on-write DELETE renumbers the rows the
-- UPDATE already addressed — measured as a destroyed row (verify_merge_into.test 11).
--
-- This is also the soft-delete/CDC shape a real dbt project reaches for, and it is strictly more than
-- DuckLake serves: DuckLake refuses more than one UPDATE/DELETE action outright.
--
--   batch 1 -> ids 1..5, none deleted
--   batch 2 -> ids 3..7: id 3 and 4 flagged deleted, 5 updated, 6,7 inserted
{{ config(
    materialized='incremental',
    incremental_strategy='merge',
    unique_key='id',
    merge_clauses={
      'when_matched': [
        {'action': 'delete', 'condition': 'DBT_INTERNAL_SOURCE.is_deleted'},
        {'action': 'update', 'mode': 'by_name'}
      ],
      'when_not_matched': [
        {'action': 'insert', 'mode': 'by_name'}
      ]
    }
) }}

{% set batch = var('batch', 1) | int %}

{% if batch == 1 %}
select i::BIGINT as id, (i * 10)::BIGINT as v, false as is_deleted from range(1, 6) t(i)
{% else %}
select i::BIGINT as id, (i * 100)::BIGINT as v, (i < 5) as is_deleted from range(3, 8) t(i)
{% endif %}
