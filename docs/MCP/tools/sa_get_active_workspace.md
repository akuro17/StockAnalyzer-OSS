# `sa_get_active_workspace`

- **Mode**: Read-Only

## Contract & Constraints

Returns the chart set-up the user last SAVED in StockAnalyzer: the symbol and time frame of the chart, the candle count, the chart type, and the active indicators with their parameters, plus 'request', the same indicators as arguments of sa_calculate_indicators (symbol, timeFrame, indicators) ready to send. An indicator that reads another indicator (sourceIndicatorId, dynamicPeriodIndicatorId) brings the registered source indicator or dynamic period driver it reads into the request, resolving sourceOutputSeriesName / dynamicPeriodOutputSeriesName from the per-edge setting when present or falling back to the upstream OutputSeriesName. Only the saved file is read: changes in the running application that are not saved yet are not included (savedAt says when the file was written). The saved time frames Daily, Weekly and Monthly map to D1, W1 and MN1; another saved time frame has no request (timeFrame null). More than 16 indicators cannot go into one call: the request is then null and the indicators are listed one by one. No saved workspace: workspaceFound false. Read-only. The same document is the resource stockanalyzer://workspace/active.

## Arguments (`inputSchema`)

None (pass `{}`).

## Example Call

```json
{}
```

## Common Errors

- `DATA_UNAVAILABLE`: Required workspace, portfolio, notes, model, or drawing file is unavailable.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
