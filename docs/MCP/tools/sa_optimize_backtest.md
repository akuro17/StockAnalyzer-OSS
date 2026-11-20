# `sa_optimize_backtest`

- **Mode**: Read-Only

## Contract & Constraints

Grid search over the numbers of one backtest: runs every combination of parameterRanges with the application's own backtest engine (the same one as sa_run_backtest) and returns the best combinations by one metric of the backtest report. Give conditions exactly as for sa_run_backtest, but put {"$param": "<name>"} where a number goes: an indicator parameter value, the right-hand constant of a comparison, an offset, or a number of riskManagement, account or report. parameterRanges lists {name, start, end, step}, one per name used in $param (an unused name or an undefined $param is INVALID_ARGUMENT); a parameter takes start, start + step, ... up to the largest value not above end. An integer parameter (Period, offset) that meets a fractional value is INVALID_ARGUMENT. templateId is not accepted because a template cannot carry placeholders. The combinations are numbered from 0 in the order of parameterRanges, the first range changing slowest. At most 4 ranges and 500 combinations (the product of the value counts); more is LIMIT_EXCEEDED, the grid is never thinned. objective is the name of a report metric as sa_run_backtest writes it (call sa_get_capabilities for the list: totalPnL, maxDrawdownAmount, expectedPayoff, totalReturn, cagr, winRate, profitFactor, maxDrawdown, ulcerIndex, barSharpe, annualizedSharpe, annualizedSharpeAutocorrelationAdjusted, barSortino, annualizedSortino, annualizedSortinoAutocorrelationAdjusted, calmarFullPeriod, recoveryFactor, grossProfit, grossLoss, averageWin, averageLoss, payoffRatio, largestWin, largestLoss, averageHoldingPeriod, maxConsecutiveWins, maxConsecutiveLosses, maxDepthDrawdownDuration, longestDrawdownDuration, timeInMarket, exposure, exposureAdjustedCAGR); no new formula is defined. Only combinations whose objective has status Valid are ranked: the best value first (objectiveDirection Maximize, the default, or Minimize, for example for maxDrawdown), equal values in combination order; the others are counted in invalidCombinationCount. If none is Valid the ranking is empty (not an error). minTrades (default 1) is the fewest trades a combination must make in the ranked period to be eligible: a combination without trades has an objective (for example totalReturn 0) that says nothing, so it is not ranked; the ones left out are counted in belowMinTradesCombinationCount and in invalidCombinationCount, and minTrades is echoed. 0 ranks them. topK (1 to 10) is the number of ranked combinations returned. sensitivity of a ranked combination looks at the neighbours that differ in one parameter by one step (up to two per parameter): neighborCount is how many of them have a Valid objective, neighborMean, neighborMin and neighborMax are over those; it is null when none has. positiveInfinityCombinationCount is how many combinations had an infinite objective (for example a profit factor without a losing trade): they are not ranked and are part of invalidCombinationCount. outOfSampleStartDate (yyyy-MM-dd) splits the period: the combinations are ranked on startDate to the day before it, and each returned combination is also run on that date to endDate (outOfSample: objectiveValue and the metric status). symbol, timeFrame (D1, W1 or MN1), startDate, endDate, account, riskManagement, report and allowUnregistered mean what they mean for sa_run_backtest. compactReport true writes each metric in ranking[].metrics as its bare number (null when not Valid) and adds unavailableMetrics [{name, status}] when any are non-Valid; default false. A call that runs out of time or is cancelled returns an error, never a partial ranking. The tool never changes anything and never fetches data.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbol` | `string` | Yes | - | The symbol to test (a ticker from sa_list_tickers). |
| `conditions` | `object` | Yes | - | The conditions of sa_run_backtest with {"$param": name} placeholders where numbers go. |
| `parameterRanges` | `array` | Yes | - | The values of every placeholder: {name, start, end, step}. start, start + step, ... up to end. |
| `objective` | `string` | Yes | - | The report metric to rank by (a name from sa_get_capabilities backtestOptimization.objectives, for example annualizedSharpe). |
| `objectiveDirection` | `string` | No | enum: `["Maximize","Minimize"]` | Maximize (default: the highest value is best) or Minimize. |
| `topK` | `integer` | No | default: `10`, min: `1`, max: `10` | How many ranked combinations to return. Default and maximum 10. |
| `minTrades` | `integer` | No | default: `1`, min: `0` | The fewest trades a combination must make (in the ranked period) to be ranked. Default 1; 0 also ranks a combination that never traded. |
| `startDate` | `string` | No | - | First evaluated day, yyyy-MM-dd. Default: the start of the stored history. |
| `endDate` | `string` | No | - | Last evaluated day, yyyy-MM-dd. Default: the latest stored bar. |
| `outOfSampleStartDate` | `string` | No | - | Optional yyyy-MM-dd: rank on the days before it and also run the returned combinations from it to the end. |
| `timeFrame` | `string` | No | enum: `["D1","W1","MN1"]` | Time frame of the bars (D1, W1, MN1). Default D1. |
| `account` | `object` | No | - | Capital, cost, sizing and margin settings, as for sa_run_backtest; numbers may be placeholders. |
| `riskManagement` | `object` | No | - | {stopLossRatio, takeProfitRatio, dynamicExits}, as for sa_run_backtest; numbers may be placeholders. |
| `report` | `object` | No | - | Report settings, as for sa_run_backtest; numbers may be placeholders. |
| `allowUnregistered` | `boolean` | No | - | Accept a symbol that is not in the ticker list but has stored bars. Default false. |
| `compactReport` | `boolean` | No | - | Write every metric in ranking[].metrics as its bare number (null when its status is not Valid) and list non-Valid ones in unavailableMetrics [{name, status}] (only when any exist). Default false. |

## Example Call

```json
{
  "symbol": "7203-T",
  "timeFrame": "D1",
  "conditions": {
    "entryLong": {
      "left": {
        "indicator": "RSI",
        "parameters": {
          "Period": {
            "$param": "period"
          }
        }
      },
      "comparison": "LessThan",
      "right": 30
    },
    "exitLong": {
      "left": {
        "indicator": "RSI",
        "parameters": {
          "Period": {
            "$param": "period"
          }
        }
      },
      "comparison": "GreaterThan",
      "right": 70
    }
  },
  "parameterRanges": [
    {
      "name": "period",
      "start": 5,
      "end": 21,
      "step": 2
    }
  ],
  "objective": "annualizedSharpe",
  "topK": 5
}
```

## Common Errors

- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `LIMIT_EXCEEDED`: Requested count, window, symbol list, or estimated response tokens exceeded server bounds.
