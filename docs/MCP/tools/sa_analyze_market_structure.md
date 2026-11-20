# `sa_analyze_market_structure`

- **Mode**: Read-Only

## Contract & Constraints

Analyzes market structure swings and causal trend continuation/reversal shifts (BOS/CHoCH) based on confirmed pivot points. Strictly causal and prefix-invariant: historical shifts never depend on future bars. Pivots are zigzag swings of at least zigzagThresholdPercent; leftStrength and rightStrength are accepted but ignored.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbol` | `string` | Yes | - | Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same). |
| `timeFrame` | `string` | Yes | enum: `["M1","M5","M15","M30","H1","H4","D1","W1","MN1"]` | Time frame of the bars (D1, W1, MN1). |
| `endDate` | `string` | No | - | Last date to include (yyyy-MM-dd). If specified, bars up to this date are evaluated. |
| `lookbackBars` | `integer` | No | default: `10000`, min: `1`, max: `10000` | Stored bars loaded up to endDate, newest last. Default and maximum 10000. |
| `zigzagThresholdPercent` | `number` | No | - | Minimum price change percentage to confirm a swing pivot (0.1..50.0). Default 5.0%. |
| `leftStrength` | `integer` | No | default: `5`, min: `1` | Accepted for compatibility and IGNORED: pivots come from zigzagThresholdPercent only, so this value does not change the result (a warning is returned when it is given). Must be >= 1. Default 5. |
| `rightStrength` | `integer` | No | default: `5`, min: `1` | Accepted for compatibility and IGNORED: pivots come from zigzagThresholdPercent only, so this value does not change the result (a warning is returned when it is given). Must be >= 1. Default 5. |
| `outputTailCount` | `integer` | No | default: `200`, min: `1`, max: `5000` | Maximum number of trailing pivots and shifts to return (1..5000). Default 200. |
| `allowUnregistered` | `boolean` | No | - | Accept a symbol that is not in the ticker list but has stored bars. Default false. |

## Example Call

```json
{
  "symbol": "7203-T",
  "timeFrame": "D1",
  "zigzagThresholdPercent": 5.0,
  "outputTailCount": 50
}
```

## Common Errors

- `INSUFFICIENT_MARKET_DATA`: Not enough stored bars or overlapping dates to compute the result.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `LIMIT_EXCEEDED`: Requested count, window, symbol list, or estimated response tokens exceeded server bounds.
- `SYMBOL_NOT_FOUND`: Symbol has no stored market data (or is unregistered when allowUnregistered is false).
