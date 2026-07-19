{#
  Snapshot staging in the SAME database as the snapshot target. DuckDB allows only ONE write database
  per transaction; dbt-duckdb's default staging (make_temp_relation nulls database/schema) lands in the
  LOCAL db, so the merge's UPDATE/INSERT against an ATTACHED catalog (the Delta lakehouse / SQL Server)
  dies with "a single transaction can only write to a single attached database". Staging beside the
  target keeps the whole snapshot merge single-database — and on the Delta catalog the staging CTAS is
  a buffered pending-create that the post-snapshot DROP simply cancels.
#}
{% macro build_snapshot_staging_table(strategy, sql, target_relation) %}
    {% set tmp_identifier = target_relation.identifier ~ '__dbt_stg' ~ py_current_timestring() %}
    {% set temp_relation = target_relation.incorporate(path={"identifier": tmp_identifier}) %}

    {% set select = snapshot_staging_table(strategy, sql, target_relation) %}

    {% call statement('build_snapshot_staging_relation') %}
        {{ create_table_as(False, temp_relation, select) }}
    {% endcall %}

    {% do return(temp_relation) %}
{% endmacro %}
