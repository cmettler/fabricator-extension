{{ config(
    materialized='delta_external',
    database=target.database,
    location='s3://fabricator/dbtlake/ext_delta',
    mode='overwrite'
) }}
-- aggregate over a Delta-catalog model, written back out as a standalone Delta table
select
    flag,
    count(*)      as n,
    sum(doubled)  as total_doubled
from {{ ref('load_a') }}
group by flag
