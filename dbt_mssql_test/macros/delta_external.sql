{#
  delta_external: a dbt materialization that writes the model via the extension's path-targeted
  `COPY … TO '<location>' (FORMAT delta, MODE …)` — a standalone Delta table at any location (s3://,
  onelake://, abfss://, local), NO ATTACH needed — and registers a VIEW over fabricator_delta_scan() so
  downstream refs read it back. dbt-duckdb's built-in `external` materialization whitelists
  csv/parquet/json, hence this custom one. Configure the model with database='<the local duckdb db>'
  so the view lands in a writable catalog (the Delta catalog itself has no CREATE VIEW).

  config: location (required), mode ('overwrite' default | 'append' | 'error' | 'ignore' |
          'overwrite_partitions' | 'error_if_not_exists'), delta_options (raw option tail, e.g.
          "PARTITION_COLUMNS 'region'").
#}
{% materialization delta_external, adapter="duckdb" %}

  {%- set location = render(config.get('location')) -%}
  {%- if not location -%}
    {{ exceptions.raise_compiler_error("delta_external requires a `location` config.") }}
  {%- endif -%}
  {%- set mode = config.get('mode', 'overwrite') -%}
  {%- set extra = config.get('delta_options', '') -%}
  {%- set target_relation = this.incorporate(type='view') -%}

  {{ run_hooks(pre_hooks, inside_transaction=False) }}
  -- `BEGIN` happens here:
  {{ run_hooks(pre_hooks, inside_transaction=True) }}

  -- write the model as a Delta table at the location (the COPY is its own atomic Delta commit)
  {% call statement('main') -%}
    copy ({{ compiled_code }}) to '{{ location }}' (format delta, mode '{{ mode }}'{{ ", " ~ extra if extra else "" }})
  {%- endcall %}

  -- downstream refs read the Delta table back through the connection-free global reader
  {% call statement('create_view') -%}
    create or replace view {{ target_relation }} as
    select * from fabricator_delta_scan('{{ location }}')
  {%- endcall %}

  {{ run_hooks(post_hooks, inside_transaction=True) }}
  {{ adapter.commit() }}
  {{ run_hooks(post_hooks, inside_transaction=False) }}

  {{ return({'relations': [target_relation]}) }}

{% endmaterialization %}
