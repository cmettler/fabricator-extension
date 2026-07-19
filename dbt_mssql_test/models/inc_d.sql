{{ config(
    materialized='incremental',
    incremental_strategy='append',
    on_schema_change='append_new_columns'
) }}
-- Incremental model (d): run 1 creates it (CTAS); re-runs append only new ids
-- (is_incremental filter); when var extra_col is set the SELECT gains a 'note' column,
-- so on_schema_change='append_new_columns' makes dbt ALTER the target table ADD COLUMN.
-- Run 4 of these at --threads 4 to stress concurrent incremental writes + ALTER.
select
    i                          as id,
    i * 2                      as doubled,
    'd'                       as grp
    {% if var('extra_col', false) %}, cast('note_' || i as varchar) as note{% endif %}
from range({{ var('rows', 1000) }}) t(i)
{% if is_incremental() %}
where i > (select coalesce(max(id), -1) from {{ this }})
{% endif %}
