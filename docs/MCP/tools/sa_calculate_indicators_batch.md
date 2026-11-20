# `sa_calculate_indicators_batch`

- **Mode**: Read-Only

## Contract & Constraints

Calculates the same technical indicators on many symbols in one call, with exactly the pipeline, arguments and per-indicator results of sa_calculate_indicators (call sa_get_capabilities first for type names, parameters and output series). Name the symbols (1 to 50), one saved ticker group (groupId), or a stored handle (symbolsFromResultId); not more than one. The answer is compact by default: per symbol only the latest values (latestValues, leadingNullCount) are returned; includeSeries true adds the last outputTailCount bars (default 1, at most 5) per series. The calculation runs over lookbackBars stored bars per symbol (default 500, at most 10000). Items come in the order of the request. A symbol that is not available or has no stored bars is reported in its own item (isSuccessful false, errorCode, errorMessage) and does not fail the call; an indicator that cannot be calculated is reported inside its result, as in sa_calculate_indicators. A malformed request (arguments, indicator parameters, a missing comparison symbol) fails the whole call before any symbol is evaluated. Cross-ticker indicators (ComparisonSymbol): at most 4 different comparison symbols per call; each is read once and shared by all symbols. Indicators with their own timeFrame, derived series, summary, trendBars, lookbackOffsets, decimalPlaces, endDate, completeBarsOnly and allowUnregistered work as in sa_calculate_indicators. Read-only; heavy (it uses the exclusive tool turn).

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbols` | `array` | No | - | Symbols (1..50; '.' == '-'). Exclusive with groupId/symbolsFromResultId. |
| `groupId` | `string` | No | - | Ticker group GUID. Exclusive with symbols/symbolsFromResultId. |
| `symbolsFromResultId` | `string` | No | - | Result handle id (from storeResult: true). Exclusive with symbols/groupId. |
| `resultOffset` | `integer` | No | default: `0`, min: `0` | 0-based handle slice offset. Default 0. |
| `resultLimit` | `integer` | No | min: `1`, max: `50` | Max handle symbols to evaluate (1..50). |
| `timeFrame` | `string` | Yes | enum: `["M1","M5","M15","M30","H1","H4","D1","W1","MN1"]` | Bar timeframe (D1, W1, MN1). |
| `indicators` | `array` | Yes | - | Indicators per symbol (1..16; see sa_calculate_indicators). |
| `derived` | `array` | No | - | Derived series (max 16; see sa_calculate_indicators). |
| `lookbackBars` | `integer` | No | default: `500`, min: `1`, max: `10000` | Stored bars per symbol (1..10000). Default 500. |
| `outputTailCount` | `integer` | No | default: `1`, min: `1`, max: `5` | Trailing bars returned when includeSeries is true (1..5). Default 1. |
| `includeSeries` | `boolean` | No | - | true adds series/projectedValues/timestamps. Default false (latestValues only). |
| `summary` | `boolean` | No | - | true adds per-series summary. Default false. |
| `trendBars` | `integer` | No | default: `20`, min: `2`, max: `10000` | Summary trendBars (2..10000; needs summary true). Default 20. |
| `summaryBars` | `integer` | No | min: `2`, max: `10000` | Trailing bars for summary (2..10000); needs summary true. Default: all calculated bars. |
| `summaryDistribution` | `boolean` | No | - | Add median, iqr, mad, skewness, excessKurtosis, robustTrendSlope (null, not an error, when trendBars>30); needs summary true. Default false. |
| `lookbackOffsets` | `array` | No | - | Non-negative bar offsets back from last bar for pastValues (max 32). |
| `decimalPlaces` | `integer` | No | min: `0`, max: `10` | Round output decimals (0..10, output only). Default: unrounded. |
| `endDate` | `string` | No | - | Inclusive end date (yyyy-MM-dd). Default: latest bar. |
| `completeBarsOnly` | `boolean` | No | - | W1/MN1 only: true drops incomplete last period; both values add partial-bar keys. |
| `allowUnregistered` | `boolean` | No | - | Allow unregistered symbols with stored daily bars. Default false. |

## Example Call

```json
{
  "symbols": [
    "MSFT",
    "7203-T"
  ],
  "timeFrame": "D1",
  "indicators": [
    {
      "id": "rsi",
      "type": "RSI",
      "parameters": {
        "Period": 14
      }
    }
  ]
}
```

## Common Errors

- `INSUFFICIENT_MARKET_DATA`: Not enough stored bars or overlapping dates to compute the result.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `LIMIT_EXCEEDED`: Requested count, window, symbol list, or estimated response tokens exceeded server bounds.
- `SYMBOL_NOT_FOUND`: Symbol has no stored market data (or is unregistered when allowUnregistered is false).
