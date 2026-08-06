-- THE LIMIT OF THE HOOKS TRICK, and the reason it is a workaround rather than a fix.
--
-- Two row-addressing actions on a table whose rowid is positional are FORCED to buffer by fabricator
-- regardless of the ambient transaction mode — that forcing is what stops a copy-on-write DELETE from
-- renumbering the rows the UPDATE already addressed. So handing the transaction back in a pre-hook does not
-- help: the statement re-opens a buffered transaction of its own, and on a non-DV table that is refused.
--
-- => deletion_vectors=false and a multi-action merge are mutually exclusive, in every transaction mode.
--    A non-DV table can serve at most ONE UPDATE/DELETE action per merge.
--
-- This model is EXPECTED TO FAIL. It also measures what a dbt user sees when a model fails while dbt's
-- transaction has been handed back by the pre-hook (does the real error survive, or does a rollback against
-- a closed transaction mask it?).
{{ config(
    materialized='incremental',
    incremental_strategy='merge',
    unique_key='id',
    pre_hook="COMMIT",
    post_hook="BEGIN",
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

select i::BIGINT as id, (i * 100)::BIGINT as v, (i < 5) as is_deleted from range(3, 8) t(i)
