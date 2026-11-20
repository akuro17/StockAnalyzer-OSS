# `sa_calculate_volume_profile`

- **Mode**: Read-Only

## Contract & Constraints

Calculates price-level volume distribution (Volume Profile) or residence time distribution (Time-at-Price) over a specified lookback range, deriving Point of Control (POC), Value Area High (VAH), and Value Area Low (VAL). The volume of a bar is split equally over the bins its range touches, in whole units, so the bins can add up to slightly less than the bars: totalBarVolume (the bars), profileVolume (the bins) and truncatedVolume (the difference) show by how much; with basis TimeAtPrice the unit is bars. decimalPlaces (0 to 10, optional) rounds the written prices (priceMin, priceMax, the bin bounds, poc, valueArea high and low; away from zero at the midpoint; output only); volumes and counts are never rounded, and valueArea.percent is the value you sent, as sent. includeBins false leaves out the bins array only (default true); poc, valueArea, totalBarVolume, profileVolume, truncatedVolume and every other field stay. compactRows true returns bins as [] and writes binColumns plus binRows ([priceLow, priceHigh, volume] rows; cannot be combined with includeBins false); default false. Operates strictly read-only and respects causal as-of dates.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbol` | `string` | Yes | - | Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same). |
| `timeFrame` | `string` | Yes | enum: `["M1","M5","M15","M30","H1","H4","D1","W1","MN1"]` | Time frame of the bars (D1, W1, MN1). |
| `endDate` | `string` | No | - | Last date to include (yyyy-MM-dd). If specified, bars up to this date are evaluated. |
| `lookbackBars` | `integer` | No | default: `10000`, min: `1`, max: `10000` | Stored bars loaded up to endDate, newest last. Default and maximum 10000. |
| `basis` | `string` | No | enum: `["Volume","TimeAtPrice"]` | Calculation basis: 'Volume' (distributes traded volume across touched bins) or 'TimeAtPrice' (distributes bar residence time). Default Volume. |
| `rangeBars` | `integer` | No | default: `10000`, min: `1`, max: `10000` | Number of trailing bars from the loaded window to calculate profile over. Default uses all loaded bars (at most 10000). |
| `binCount` | `integer` | No | default: `24`, min: `2`, max: `200` | Number of vertical price bins (2..200). Default 24. |
| `valueAreaPercent` | `number` | No | - | Target fraction of total profile volume inside the value area (0.0 < valueAreaPercent <= 1.0). Default 0.7. |
| `allowUnregistered` | `boolean` | No | - | Accept a symbol that is not in the ticker list but has stored bars. Default false. |
| `decimalPlaces` | `integer` | No | min: `0`, max: `10` | Round priceMin, priceMax, the bin bounds, poc and the value area bounds (volumes are never rounded) (0..10, output only). Default: unrounded. |
| `includeBins` | `boolean` | No | - | false leaves out the bins array only (poc, valueArea and the volume totals stay). Default true. |
| `compactRows` | `boolean` | No | - | true returns 'bins' as [] and writes 'binColumns' plus 'binRows' ([priceLow, priceHigh, volume] rows; cannot be combined with includeBins false). Default false. |

## Example Call

```json
{
  "symbol": "7203-T",
  "timeFrame": "D1",
  "basis": "Volume",
  "binCount": 24,
  "valueAreaPercent": 0.7
}
```

## Common Errors

- `INSUFFICIENT_MARKET_DATA`: Not enough stored bars or overlapping dates to compute the result.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `SYMBOL_NOT_FOUND`: Symbol has no stored market data (or is unregistered when allowUnregistered is false).
