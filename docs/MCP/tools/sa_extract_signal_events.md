# `sa_extract_signal_events`

- **Mode**: Read-Only

## Contract & Constraints

Extracts bars where a user-defined condition evaluates to true (same syntax as sa_run_backtest conditions root). Returns the matching bars with timestamps, close prices, leaf-level comparison breakdowns, elapsed bars since previous event, forward return diagnostics (close-to-close and next-open-to-close), and optional full boolean series across the evaluation window. includeEventStudy true (needs forwardBars) adds eventStudy: for every horizon and both bases, over ALL events of the call (also those maxEvents cuts off), count, missingCount (events without that horizon), mean, median, stdDev (population, divisor n), min, max and positiveRatio (share of returns above 0); the figures are ratios (0.05 = 5%) and null when there is no value. compactRows true returns events as [] and writes eventColumns plus eventRows (one row per event, with comparisons as [path, left, right, result] tuples) to save tokens; default false. Events of EveryTrueBar overlap, so the figures are descriptive, not a significance test.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbol` | `string` | Yes | - | The symbol to test (a ticker from sa_list_tickers). |
| `timeFrame` | `string` | Yes | enum: `["M1","M5","M15","M30","H1","H4","D1","W1","MN1"]` | Time frame of the bars (D1, W1, MN1). |
| `condition` | `object` | Yes | - | A condition group {operator: And\|Or, children: [...]} or a single comparison {left, comparison, right}. |
| `startDate` | `string` | No | - | First evaluated day, yyyy-MM-dd. Default: start of stored history. |
| `endDate` | `string` | No | - | Last evaluated day, yyyy-MM-dd. Default: latest stored bar. |
| `eventMode` | `string` | No | enum: `["EveryTrueBar","FirstTrueBarOfRun"]` | Event filter mode: EveryTrueBar (default, every bar matching condition) or FirstTrueBarOfRun (only the initial true bar of consecutive matches). |
| `forwardBars` | `array` | No | - | Forward bar horizons (each 1..60, at most 8 horizons). |
| `includeEventStudy` | `boolean` | No | - | true adds eventStudy: count, missingCount, mean, median, stdDev, min, max and positiveRatio of the forward returns per horizon and basis over all events (before maxEvents). Needs forwardBars. Default false. |
| `maxEvents` | `integer` | No | default: `200`, min: `1`, max: `200` | Most events to return. Default and maximum 200. |
| `order` | `string` | No | enum: `["OldestFirst","NewestFirst"]` | Output ordering: OldestFirst (default) or NewestFirst. |
| `includeBooleanSeries` | `boolean` | No | - | Include boolean series for every bar in the evaluation window. Default false. |
| `compactRows` | `boolean` | No | - | true returns 'events' as [] and writes 'eventColumns' plus 'eventRows' (one row per event, comparisons as [path, left, right, result]). Default false. |
| `allowUnregistered` | `boolean` | No | - | Accept a symbol that is not in the ticker list but has stored bars. Default false. |

## Example Call

```json
{
  "symbol": "7203-T",
  "timeFrame": "D1",
  "condition": {
    "left": {
      "indicator": "RSI",
      "parameters": {
        "Period": 14
      }
    },
    "comparison": "LessThan",
    "right": 30
  },
  "eventMode": "FirstTrueBarOfRun",
  "forwardBars": [
    5,
    10
  ],
  "maxEvents": 50
}
```

## Common Errors

- `INSUFFICIENT_MARKET_DATA`: Not enough stored bars or overlapping dates to compute the result.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `LIMIT_EXCEEDED`: Requested count, window, symbol list, or estimated response tokens exceeded server bounds.
- `UNSUPPORTED_TIMEFRAME`: Requested timeFrame is not supported for stored bars or this tool.
