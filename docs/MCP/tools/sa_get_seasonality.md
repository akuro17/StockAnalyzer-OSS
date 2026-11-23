# `sa_get_seasonality`

- **Mode**: Read-Only

## Contract & Constraints

Extracts the historical calendar seasonality matrix and monthly fractional return statistics for a symbol. Output contains raw monthly return ratios and averages; no advice or win-rate is synthesized. Also emits `referenceDate` (`min(endDate, latest stored daily bar)`), `lastBarPeriodEnd`, and `lastBarPartial`. Unfinished month and annual periods remain included in Core `average` without altering Core numbers; MCP writes `partial: true` only on non-null cells whose period ends after `referenceDate` (both the unfinished month cell and that cycle's Annual cell), omitting `partial` everywhere else. decimalPlaces (0 to 10, optional) rounds returnRatio and average (away from zero at the midpoint; output only); without it the ratios are as calculated.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbol` | `string` | Yes | - | Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same). |
| `timeFrame` | `string` | Yes | enum: `["M1","M5","M15","M30","H1","H4","D1","W1","MN1"]` | Time frame of the bars (D1, W1, MN1). |
| `endDate` | `string` | No | - | Last date to include (yyyy-MM-dd). If specified, bars up to this date are evaluated. |
| `lookbackBars` | `integer` | No | default: `10000`, min: `1`, max: `10000` | Stored bars loaded up to endDate, newest last. Default and maximum 10000. |
| `startMonth` | `integer` | No | default: `1`, min: `1`, max: `12` | Starting calendar month for the 1-year cycle (1..12). Default 1. |
| `years` | `integer` | No | default: `10`, min: `1`, max: `50` | Number of overlaid calendar years to analyze (1..50). Default 10. |
| `allowUnregistered` | `boolean` | No | - | Accept a symbol that is not in the ticker list but has stored bars. Default false. |
| `decimalPlaces` | `integer` | No | min: `0`, max: `10` | Round returnRatio and average (0..10, output only). Default: unrounded. |

## Example Call

```json
{
  "symbol": "7203-T",
  "timeFrame": "D1",
  "years": 10,
  "startMonth": 1
}
```

## Common Errors

- `INSUFFICIENT_MARKET_DATA`: Not enough stored bars or overlapping dates to compute the result.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `SYMBOL_NOT_FOUND`: Symbol has no stored market data (or is unregistered when allowUnregistered is false).
