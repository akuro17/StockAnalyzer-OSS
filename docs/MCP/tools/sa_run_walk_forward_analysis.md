# `sa_run_walk_forward_analysis`

- **Mode**: Read-Only

## Contract & Constraints

Walk-forward analysis of a backtest: for every window it runs the grid of sa_optimize_backtest (conditions with {"$param": name} placeholders, parameterRanges, objective, objectiveDirection, account, riskManagement, report: exactly the same meaning and checks) on the in-sample part, takes the best combination (best objective, ties by combination number) and runs only that one on the out-of-sample part. It uses the application's own backtest engine; no formula is defined here. windows is exactly one of generator and list. generator {mode, inSampleBars, outOfSampleBars, stepBars} cuts the evaluated period (startDate to endDate, default the stored history) into windows measured in bars of the time frame, never in calendar time: Rolling keeps the in-sample length and moves the window by stepBars, Anchored keeps the first bar and lets the in-sample part grow by stepBars; the out-of-sample part is the next outOfSampleBars bars, and a window whose out-of-sample part would run past the last bar is not made. list [{inSampleStart, inSampleEnd, outOfSampleStart, outOfSampleEnd}] (yyyy-MM-dd, inclusive) gives the windows yourself; inSampleEnd must be before outOfSampleStart, and startDate and endDate must then be omitted. At most 8 windows (more is LIMIT_EXCEEDED, never cut) and, per window, the limits of sa_optimize_backtest (500 combinations, 4 ranges). A generator that gives no window is INVALID_ARGUMENT. Each window lists bestParameters, and for the in-sample (is...) and out-of-sample (oos...) part the objective, barSharpe, totalReturn and maxDrawdown as {status, value} (value null unless Valid) and the total trades. minInSampleTrades (default 1) is the fewest trades a combination must make in the in-sample part to be eligible as the best one: a combination without trades is never chosen, because its objective (for example totalReturn 0) says nothing. A window has a status: Evaluated, or isInsufficientData with NoInSampleBars (the in-sample part holds no stored bar), NoInSampleTrades (no combination made minInSampleTrades trades there, for example near the start of the stored history, where an indicator has no earlier bars to start from) or NoValidInSampleObjective (some combination traded enough but none has a Valid objective); such a window has no best combination, is not run out of sample and is left out of the aggregate. NoOutOfSampleBars means the best combination exists but the out-of-sample part holds no stored bar: its oos values are null and the window is not used for the out-of-sample means. Every run lets its indicators start on the stored bars before its first day, so a window is too short for its indicators only where the stored history itself begins. The tool cannot see how many bars an indicator needs before it is defined, so that case shows up only as few or no trades. aggregate (null when no window was evaluated) has meanIsObjective and meanOosObjective (simple means over the evaluated windows whose value is Valid; isObjectiveWindowCount and oosObjectiveWindowCount say how many windows each mean has, they can differ), meanIsBarSharpe, meanOosBarSharpe and barSharpeEfficiency = meanOosBarSharpe / meanIsBarSharpe, taken over the windows in which both BarSharpe values are Valid (barSharpeWindowCount); it is null, with barSharpeEfficiencyReason, when no window qualifies or meanIsBarSharpe is not greater than 0. There is no threshold on the denominator. Each window also has positiveInfinityCombinationCount (in-sample runs whose objective is infinite, for example a profit factor without a losing trade: not eligible, counted only); a warning names windows whose out-of-sample ranges overlap, because those bars count more than once in the aggregate. No deflated Sharpe ratio, probability of backtest overfitting or shuffled confidence interval is computed. compactReport true writes each window metric (is*/oos*) as its bare number (null when not Valid) and adds unavailableMetrics [{name, status}] on evaluated windows when any are non-Valid; default false. The cost grows with windows x combinations x in-sample bars; a call that runs out of time (180 s) or is cancelled returns an error, never a partial result. The tool never changes anything and never fetches data.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbol` | `string` | Yes | - | Symbol to test (from sa_list_tickers). |
| `conditions` | `object` | Yes | - | sa_run_backtest conditions with {"$param": name} placeholders. |
| `parameterRanges` | `array` | Yes | - | The values of every placeholder: {name, start, end, step}. start, start + step, ... up to end. |
| `objective` | `string` | Yes | - | The report metric to rank by (a name from sa_get_capabilities backtestOptimization.objectives, for example annualizedSharpe). |
| `objectiveDirection` | `string` | No | enum: `["Maximize","Minimize"]` | Maximize (default: the highest value is best) or Minimize. |
| `windows` | `object` | Yes | - | Exactly one of generator or list. |
| `minInSampleTrades` | `integer` | No | default: `1`, min: `0` | Min in-sample trades for eligibility. Default 1; 0 allows zero-trade. |
| `startDate` | `string` | No | - | Generator start date (yyyy-MM-dd). Default: history start. |
| `endDate` | `string` | No | - | Generator end date (yyyy-MM-dd). Default: latest bar. |
| `timeFrame` | `string` | No | enum: `["D1","W1","MN1"]` | Bar timeframe (D1, W1, MN1). Default D1. |
| `account` | `object` | No | - | Account settings (sa_run_backtest syntax; placeholders allowed). |
| `riskManagement` | `object` | No | - | {stopLossRatio, takeProfitRatio, dynamicExits} (placeholders allowed). |
| `report` | `object` | No | - | Report settings (sa_run_backtest syntax; placeholders allowed). |
| `allowUnregistered` | `boolean` | No | - | Allow unregistered symbol with stored bars. Default false. |
| `compactReport` | `boolean` | No | - | Emit bare window metrics (null if non-Valid) + unavailableMetrics [{name, status}]. Default false. |

## Example Call

```json
{
  "symbol": "7203-T",
  "timeFrame": "D1",
  "conditions": {
    "entryLong": {
      "left": {
        "price": "Close"
      },
      "comparison": "GreaterThan",
      "right": {
        "indicator": "SMA",
        "parameters": {
          "Period": {
            "$param": "period"
          }
        }
      }
    },
    "exitLong": {
      "left": {
        "price": "Close"
      },
      "comparison": "LessThan",
      "right": {
        "indicator": "SMA",
        "parameters": {
          "Period": {
            "$param": "period"
          }
        }
      }
    }
  },
  "parameterRanges": [
    {
      "name": "period",
      "start": 10,
      "end": 60,
      "step": 10
    }
  ],
  "objective": "annualizedSharpe",
  "windows": {
    "generator": {
      "mode": "Rolling",
      "inSampleBars": 500,
      "outOfSampleBars": 125,
      "stepBars": 125
    }
  }
}
```

## Common Errors

- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `LIMIT_EXCEEDED`: Requested count, window, symbol list, or estimated response tokens exceeded server bounds.
