-- Phase 1: DAX inside plain model SQL — the EVALUATE rides in the daxeval() table function of the
-- ATTACHed semantic-model catalog; dbt's ordinary `table` materialization CTASes the result into the
-- OneLake Delta lakehouse (lake.dbt.trip_by_date). DAX string literals use doubled single quotes.
SELECT
    "DateID" AS date_id,
    trips
FROM dax."Model".daxeval(expression := 'EVALUATE SUMMARIZECOLUMNS(''Trip''[DateID], "trips", COUNTROWS(''Trip''))')
