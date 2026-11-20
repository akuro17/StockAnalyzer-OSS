# `sa_calculate_reverse_watch`

- **Mode**: Read-Only

## Contract & Constraints

Calculates the Reverse Watch curve the chart draws, for the latest bars of one symbol and time frame. Each point carries its phase (Phase1 to Phase8, the eight 45-degree sectors of the price-volume plane) and where it lies in the window of the last dataCount points: pricePosition and volumePosition are High, Middle or Low relative to the window's centre. The application's own descriptions of what a phase means are inconsistent, so no meaning is attached to a phase. period is the moving-average window in bars (default 25, the chart's default). isMaBased (default true) averages the price and volume over the period; false uses the close and volume of each bar. isLogScaleVolume (default false) takes log10 of the volume. dataCount (default 100) is the number of latest points the window's centre and bounds are taken from. The calculation runs over up to lookbackBars stored bars (default and maximum 10000); the last outputTailCount points are returned (default 200, at most 5000). The tool never changes anything and never fetches data.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbol` | `string` | Yes | - | Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same). |
| `timeFrame` | `string` | Yes | enum: `["M1","M5","M15","M30","H1","H4","D1","W1","MN1"]` | Time frame of the bars. Supported: D1, W1, MN1. |
| `endDate` | `string` | No | - | Last date to include (yyyy-MM-dd). If specified, bars up to this date are evaluated. |
| `period` | `integer` | No | default: `25`, min: `1`, max: `10000` | Moving-average window in bars, 1 to 10000. Default 25. |
| `isMaBased` | `boolean` | No | - | true (default): average the price and volume over the period; false: use the close and volume of each bar. |
| `isLogScaleVolume` | `boolean` | No | - | Take log10 of the volume before the curve is built. Default false. |
| `dataCount` | `integer` | No | default: `100`, min: `2`, max: `10000` | Latest points the window's centre and bounds are taken from, 2 to 10000. Default 100. |
| `lookbackBars` | `integer` | No | default: `10000`, min: `1`, max: `10000` | Stored bars the calculation runs over, newest last. Default and maximum 10000. |
| `outputTailCount` | `integer` | No | default: `200`, min: `1`, max: `5000` | Number of trailing points returned. Default 200, at most 5000. |

## Example Call

```json
{
  "symbol": "7203-T",
  "timeFrame": "D1",
  "period": 25,
  "outputTailCount": 200
}
```

## Common Errors

- `INSUFFICIENT_MARKET_DATA`: Not enough stored bars or overlapping dates to compute the result.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `SYMBOL_NOT_FOUND`: Symbol has no stored market data (or is unregistered when allowUnregistered is false).
