# `sa_detect_divergences`

- **Mode**: Read-Only

## Contract & Constraints

Detects technical divergences between price swings and an indicator (e.g. RSI, MACD), and moving-average crossovers (Golden Cross / Dead Cross) using the application's DivergenceCrossDetector. Outputs exact pivot timestamps, indices, price values, and indicator values without inventing ad-hoc strength scores. A divergence event's triggerIndex/timestamp is the bar of its second price pivot; a swing pivot is only known after pivotOrder further bars, so the divergence is available from confirmedAtIndex/confirmedAtTime (the later of the two second pivots plus pivotOrder), not from the trigger bar.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbol` | `string` | Yes | - | The symbol to test (a ticker from sa_list_tickers). |
| `timeFrame` | `string` | No | enum: `["M1","M5","M15","M30","H1","H4","D1","W1","MN1"]` | Time frame of the bars (D1, W1, MN1). Default D1. |
| `startDate` | `string` | No | - | First evaluated day, yyyy-MM-dd. Default: start of stored history. |
| `endDate` | `string` | No | - | Last evaluated day, yyyy-MM-dd. Default: latest stored bar. |
| `indicator` | `string` | No | - | Indicator type to compare price against for divergence (e.g. RSI, MACD, Stochastics). Default RSI. |
| `indicatorParameters` | `object` | No | - | Optional parameters for the divergence indicator (e.g. {"Period": 14}). |
| `pivotOrder` | `integer` | No | default: `5`, min: `2`, max: `50` | Number of bars on each side to confirm a swing high/low (2..50). Default 5. |
| `detectCrosses` | `boolean` | No | - | Also detect moving average crossovers (Golden Cross / Dead Cross). Default false. |
| `shortMaPeriod` | `integer` | No | default: `25`, min: `1`, max: `200` | Short-term moving average period for GC/DC (1..200). Default 25. |
| `longMaPeriod` | `integer` | No | default: `75`, min: `2`, max: `500` | Long-term moving average period for GC/DC (2..500). Default 75. |
| `maxEvents` | `integer` | No | default: `200`, min: `1`, max: `200` | Most events to return. Default and maximum 200. |
| `allowUnregistered` | `boolean` | No | - | Accept a symbol that is not in the ticker list but has stored bars. Default false. |

## Example Call

```json
{
  "symbol": "7203-T",
  "timeFrame": "D1",
  "indicator": "RSI",
  "pivotOrder": 5,
  "detectCrosses": true,
  "shortMaPeriod": 25,
  "longMaPeriod": 75
}
```

## Common Errors

- `INSUFFICIENT_MARKET_DATA`: Not enough stored bars or overlapping dates to compute the result.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `UNKNOWN_INDICATOR`: Unknown indicator type name; check sa_get_capabilities.
- `UNSUPPORTED_TIMEFRAME`: Requested timeFrame is not supported for stored bars or this tool.
