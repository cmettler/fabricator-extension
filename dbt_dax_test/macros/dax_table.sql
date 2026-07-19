{#
  dax_table: a dbt materialization whose MODEL BODY IS PLAIN DAX. dbt never parses model SQL — it only
  renders jinja and hands the text to the materialization — so the compiled body here is the raw
  EVALUATE/DEFINE…EVALUATE statement. The materialization wraps it in the DAX catalog's daxeval() table
  function (single quotes doubled for the SQL string literal) and CTASes the result into the model's
  target relation (the OneLake Delta lakehouse).

  config: dax_catalog (default 'dax')  — the ATTACHed semantic-model catalog alias
          dax_model   (default 'Model') — the model schema under that catalog
  Jinja caveat: raw DAX passes through jinja first, so literal {{ or {% in a DAX string would need
  escaping ({% raw %}…{% endraw %}) — single braces (table constructors, EVALUATE {1}) are fine.
#}
{% materialization dax_table, adapter="duckdb" %}

  {%- set dax_catalog = config.get('dax_catalog', 'dax') -%}
  {%- set dax_model = config.get('dax_model', 'Model') -%}
  {%- set target_relation = this.incorporate(type='table') -%}
  {%- set dax_body = compiled_code | trim -%}

  {{ run_hooks(pre_hooks, inside_transaction=False) }}
  -- `BEGIN` happens here:
  {{ run_hooks(pre_hooks, inside_transaction=True) }}

  {% call statement('main') -%}
    create or replace table {{ target_relation }} as
    select * from {{ dax_catalog }}."{{ dax_model }}".daxeval(expression := '{{ dax_body | replace("'", "''") }}')
  {%- endcall %}

  {{ run_hooks(post_hooks, inside_transaction=True) }}
  {{ adapter.commit() }}
  {{ run_hooks(post_hooks, inside_transaction=False) }}

  {{ return({'relations': [target_relation]}) }}

{% endmaterialization %}
