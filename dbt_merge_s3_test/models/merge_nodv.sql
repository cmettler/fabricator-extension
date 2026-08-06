-- THE ONE REACHABLE FAILURE CASE, and it is not hypothetical: a Delta table with deletion vectors OFF.
--
-- fabricator's buffered (explicit-transaction) UPDATE/DELETE path requires deletion vectors; the autocommit
-- path does not (it rewrites copy-on-write). Since dbt runs every model statement inside a real
-- BEGIN..COMMIT, a merge against a non-DV table is REFUSED.
--
-- Why anyone would have such a table: SQL Server's and Fabric's Delta readers are protocol 1.0 only, so a
-- table meant to be read by T-SQL/OPENROWSET MUST be written `deletion_vectors false, column_mapping none`.
-- That is exactly the shape a dbt project publishing to a lakehouse-for-Power-BI ends up with.
--
-- The target table is created OUTSIDE dbt (see README) with DV off, so dbt finds an existing relation on
-- its very first run and takes the merge path immediately — which is also how the real scenario arises.
{{ config(
    materialized='incremental',
    incremental_strategy='merge',
    unique_key='id'
) }}

{% set batch = var('batch', 1) | int %}

{% if batch == 1 %}
select i::BIGINT as id, (i * 10)::BIGINT as v from range(1, 6) t(i)
{% else %}
select i::BIGINT as id, (i * 100)::BIGINT as v from range(4, 9) t(i)
{% endif %}
