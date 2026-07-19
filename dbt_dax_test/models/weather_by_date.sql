{{ config(materialized='dax_table') }}
EVALUATE
SUMMARIZECOLUMNS(
    'Weather'[DateID],
    "avg_temp_f", AVERAGE('Weather'[AvgTemperatureFahrenheit]),
    "precip_in", SUM('Weather'[PrecipitationInches])
)
