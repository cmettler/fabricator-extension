{{ config(materialized='table') }}
-- ~200k-row CTAS; 4 of these run concurrently (threads=4) to stress the provider's
-- bulk-load / connection / catalog-cache concurrency.
select
    i                              as id,
    i * 2                          as doubled,
    'c' || '_' || i::varchar      as label,
    (i % 7 = 0)                    as flag,
    (i % 100)::decimal(10,2)/3.0   as ratio
from range({{ var("rows", 200000) }}) t(i)
