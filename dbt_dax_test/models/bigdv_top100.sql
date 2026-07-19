{{ config(materialized='dax_table', dax_catalog='daxlh') }}
EVALUATE
    TOPN(100, 'arrownet_bigdv')
