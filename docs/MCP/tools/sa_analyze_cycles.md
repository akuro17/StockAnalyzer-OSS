# `sa_analyze_cycles`

- **Mode**: Read-Only

## Contract & Constraints

Analyzes cyclical price components and dominant frequencies using pure Fast Fourier Transform (FFT) spectrum analysis. Output periods are in bars. Causal as of endDate: the spectrum is computed only from bars dated on or before endDate, so later bars never change it.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbol` | `string` | Yes | - | Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same). |
| `timeFrame` | `string` | Yes | enum: `["M1","M5","M15","M30","H1","H4","D1","W1","MN1"]` | Time frame of the bars (D1, W1, MN1). |
| `endDate` | `string` | No | - | Last date to include (yyyy-MM-dd). If specified, bars up to this date are evaluated. |
| `lookbackBars` | `integer` | No | default: `10000`, min: `1`, max: `10000` | Stored bars loaded up to endDate, newest last. Default and maximum 10000. |
| `topBins` | `integer` | No | default: `10`, min: `1`, max: `50` | Number of dominant frequency bins to return (1..50). Default 10. |
| `rangeBars` | `integer` | No | default: `10000`, min: `2`, max: `10000` | Number of trailing bars to analyze from the loaded window (2..10000). Default: all loaded bars. |
| `allowUnregistered` | `boolean` | No | - | Accept a symbol that is not in the ticker list but has stored bars. Default false. |

## Example Call

```json
{
  "symbol": "7203-T",
  "timeFrame": "D1",
  "topBins": 10
}
```

## Common Errors

- `INSUFFICIENT_MARKET_DATA`: Not enough stored bars or overlapping dates to compute the result.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `SYMBOL_NOT_FOUND`: Symbol has no stored market data (or is unregistered when allowUnregistered is false).
