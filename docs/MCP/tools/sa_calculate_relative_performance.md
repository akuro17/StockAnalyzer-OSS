# `sa_calculate_relative_performance`

- **Mode**: Read-Only

## Contract & Constraints

Calculates the values the Relative Performance chart plots for a primary symbol and its comparison symbols. Up to 10 comparison symbols are aligned to the primary symbol's bars (gaps carried forward, badly fragmented symbols excluded, as the chart does). mode Performance (default) is the percentage change of the close from the first returned bar (the chart's left edge, rebased at 0 %); Ratio is the close divided by the primary symbol's close; Spread is the close minus the primary symbol's close (price units); ZScore is the close's distance from its mean over the last zScorePeriod bars in population standard deviations. A value that cannot be defined is null. The unit of the values is in the result (unit). The last outputTailCount bars (default 200, at most 5000) of up to lookbackBars stored bars (default and maximum 10000) are returned. decimalPlaces (0 to 10, optional) rounds the written values and basePrice (away from zero at the midpoint); the calculation itself is never rounded, and without it the numbers are as calculated. The tool never changes anything and never fetches data; it does not read the chart's saved symbol list or presets.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbol` | `string` | Yes | - | Primary ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same). |
| `timeFrame` | `string` | Yes | enum: `["M1","M5","M15","M30","H1","H4","D1","W1","MN1"]` | Time frame of the bars. Supported: D1, W1, MN1. |
| `comparisonSymbols` | `array` | No | - | Comparison symbols (stored tickers). The primary symbol and repeats are ignored and listed in ignoredSymbols. At most 10 different ones; default none. |
| `mode` | `string` | No | enum: `["Performance","Ratio","ZScore","Spread"]` | Comparison mode. Supported: Performance, Ratio, ZScore, Spread. Default Performance. |
| `zScorePeriod` | `integer` | No | default: `20`, min: `2`, max: `500` | Z-Score window in bars, 2 to 500. Default 20. Used by mode ZScore only. |
| `endDate` | `string` | No | - | Last date to include (yyyy-MM-dd). If specified, bars up to this date are evaluated. |
| `lookbackBars` | `integer` | No | default: `10000`, min: `1`, max: `10000` | Stored bars the alignment runs over, newest last. Default and maximum 10000. |
| `outputTailCount` | `integer` | No | default: `200`, min: `1`, max: `5000` | Number of trailing bars returned; the first of them is the Performance base. Default 200, at most 5000. |
| `decimalPlaces` | `integer` | No | min: `0`, max: `10` | Round the series values and basePrice (0..10, output only). Default: unrounded. |

## Example Call

```json
{
  "symbol": "7203-T",
  "timeFrame": "D1",
  "comparisonSymbols": [
    "6758-T"
  ],
  "mode": "Performance",
  "outputTailCount": 120
}
```

## Common Errors

- `INSUFFICIENT_MARKET_DATA`: Not enough stored bars or overlapping dates to compute the result.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `LIMIT_EXCEEDED`: Requested count, window, symbol list, or estimated response tokens exceeded server bounds.
- `SYMBOL_NOT_FOUND`: Symbol has no stored market data (or is unregistered when allowUnregistered is false).
