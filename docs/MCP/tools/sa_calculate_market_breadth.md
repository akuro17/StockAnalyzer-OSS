# `sa_calculate_market_breadth`

- **Mode**: Read-Only

## Contract & Constraints

Calculates market breadth and internal market structure across a universe of up to 500 symbols: Advancers, Decliners, Unchanged, A/D Line, A/D Ratio, McClellan Oscillator, Summation Index, % Above SMA, New Highs/Lows, TRIN, and CVI. Aligns bars on the union of trading days. With timeFrame W1 or MN1 the bars are matched by period. With timeFrame W1 or MN1 the dates of this tool are calendar period starts (the Monday of the ISO week, the 1st of the month), the same label a W1 or MN1 bar has in every tool, so that holidays of different markets do not change them. The first and last trading day of a period are not part of the date. Read-only (heavy).

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbols` | `array` | No | - | Universe ticker symbols to analyze (up to 500). Mutually exclusive with groupId and symbolsFromResultId. |
| `groupId` | `string` | No | - | Guid of a saved ticker group (from sa_list_ticker_groups) to analyze. Mutually exclusive with symbols and symbolsFromResultId. |
| `symbolsFromResultId` | `string` | No | - | Handle id from sa_run_screener or sa_filter_tickers (storeResult true). Mutually exclusive with symbols and groupId. |
| `resultOffset` | `integer` | No | default: `0`, min: `0` | 0-based start index within symbolsFromResultId. Default 0. |
| `resultLimit` | `integer` | No | min: `1`, max: `500` | Maximum symbols to read from symbolsFromResultId (1..500). |
| `timeFrame` | `string` | No | enum: `["M1","M5","M15","M30","H1","H4","D1","W1","MN1"]` | Time frame of the bars. Supported: D1, W1, MN1. Default D1. |
| `lookbackBars` | `integer` | No | default: `200`, min: `1`, max: `10000` | Stored bars each symbol evaluates, newest last. Default 200, maximum 10000. |
| `outputTailCount` | `integer` | No | default: `200`, min: `1`, max: `5000` | Number of trailing bars returned per series. Default 200, at most 5000. |
| `endDate` | `string` | No | - | Last date to include (yyyy-MM-dd). If specified, bars up to this date are evaluated. |
| `adRatioBars` | `integer` | No | default: `20`, min: `1`, max: `10000` | Lookback window in days for A/D Ratio. Default 20. |
| `mcClellanFast` | `integer` | No | default: `19`, min: `1`, max: `10000` | Fast EMA period for McClellan Oscillator. Default 19. |
| `mcClellanSlow` | `integer` | No | default: `39`, min: `1`, max: `10000` | Slow EMA period for McClellan Oscillator. Default 39. |
| `maBars` | `integer` | No | default: `50`, min: `1`, max: `10000` | Lookback period in bars for % Above SMA. Default 50. |
| `highLowBars` | `integer` | No | default: `20`, min: `1`, max: `10000` | Lookback window in bars for New Highs / New Lows. Default 20. |
| `include` | `array` | No | - | Optional array of series names to include in output. Allowed: advancers, decliners, unchanged, adLine, adRatio, mcClellan, summationIndex, pctAboveMa, newHighs, newLows, netNewHighs, trin, cvi. Omit for all series. |

## Example Call

```json
{
  "symbols": [
    "MSFT",
    "AAPL"
  ],
  "timeFrame": "D1",
  "outputTailCount": 50
}
```

## Common Errors

- `INSUFFICIENT_MARKET_DATA`: Not enough stored bars or overlapping dates to compute the result.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `LIMIT_EXCEEDED`: Requested count, window, symbol list, or estimated response tokens exceeded server bounds.
- `SYMBOL_NOT_FOUND`: Symbol has no stored market data (or is unregistered when allowUnregistered is false).
