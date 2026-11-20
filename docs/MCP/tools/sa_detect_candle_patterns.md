# `sa_detect_candle_patterns`

- **Mode**: Read-Only

## Contract & Constraints

Detects classical single and multi-candle price action patterns (e.g. Doji, Hammer, Engulfing, Morning/Evening Star, Three Soldiers/Crows, Tasuki Gap, Three-Line Strike, Rising/Falling Three Methods) using CandlePatternDetector. Evaluates the latest bar or a trailing bar window. Strictly causal and read-only.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbol` | `string` | Yes | - | Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same). |
| `timeFrame` | `string` | Yes | enum: `["M1","M5","M15","M30","H1","H4","D1","W1","MN1"]` | Time frame of the bars (D1, W1, MN1). |
| `endDate` | `string` | No | - | Last date to include (yyyy-MM-dd). If specified, bars up to this date are evaluated. |
| `lookbackBars` | `integer` | No | default: `10000`, min: `1`, max: `10000` | Stored bars loaded up to endDate, newest last. Default and maximum 10000. |
| `windowBars` | `integer` | No | default: `1`, min: `1`, max: `5000` | Number of trailing bars to scan for candlestick patterns (1..5000). Default 1 (evaluates latest bar only). |
| `excludePatternBarsFromContext` | `boolean` | No | - | Exclude pattern constituent bars when computing local ATR and average body size volatility context. Default false. |
| `requireTrendContext` | `boolean` | No | - | Require confirmed prior trend for reversal candlestick patterns to trigger. Default false. |
| `allowUnregistered` | `boolean` | No | - | Accept a symbol that is not in the ticker list but has stored bars. Default false. |

## Example Call

```json
{
  "symbol": "7203-T",
  "timeFrame": "D1",
  "windowBars": 5,
  "requireTrendContext": true
}
```

## Common Errors

- `INSUFFICIENT_MARKET_DATA`: Not enough stored bars or overlapping dates to compute the result.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `SYMBOL_NOT_FOUND`: Symbol has no stored market data (or is unregistered when allowUnregistered is false).
