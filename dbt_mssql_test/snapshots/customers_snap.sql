{% snapshot customers_snap %}
{{ config(
    unique_key='id',
    strategy='check',
    check_cols='all',
    database='mssql',
    schema='main'
) }}
select * from {{ ref('customers') }}
{% endsnapshot %}
