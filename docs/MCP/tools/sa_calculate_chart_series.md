# `sa_calculate_chart_series`

- **Mode**: Read-Only

## Contract & Constraints

Calculates the converted numerical series, effective sizing parameters, DataWindow metrics, and optional pattern analysis for Heikin-Ashi and Non-Time-Series charts (HeikinAshi, Renko, Kagi, PointAndFigure, ThreeLineBreak). Supports all GUI calculation settings (sizing modes, manual sizes, percentages, ATR period/multiplier, rounding and fallback modes, reversals, initial column, line count, minimum move, tolerance, multi-wave and P&F patterns). decimalPlaces (0 to 10, optional) rounds every written price, size and score (away from zero at the midpoint; output only, the series is calculated unrounded); volume, counts, indexes and dates are never rounded. compactRows true returns elements as an empty array and writes elementColumns plus elementRows (one array per element, in column order) to save tokens; default false. Never mutates data and runs read-only.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbol` | `string` | Yes | - | Ticker symbol ('.' == '-', e.g. 7203-T). |
| `timeFrame` | `string` | Yes | enum: `["M1","M5","M15","M30","H1","H4","D1","W1","MN1"]` | Bar timeframe (D1, W1, MN1). |
| `chartType` | `string` | Yes | enum: `["HeikinAshi","Renko","Kagi","PointAndFigure","ThreeLineBreak"]` | Target chart type (HeikinAshi, Renko, Kagi, PointAndFigure, ThreeLineBreak). |
| `endDate` | `string` | No | - | Inclusive end date (yyyy-MM-dd). |
| `lookbackBars` | `integer` | No | default: `10000`, min: `1`, max: `10000` | Stored bars to load (1..10000, newest last). |
| `outputTailCount` | `integer` | No | default: `200`, min: `1`, max: `5000` | Trailing converted elements returned (1..5000). Default 200. |
| `sizingMode` | `string` | No | enum: `["Fixed","AutoAtr","Percentage"]` | Sizing mode for Renko/Kagi/P&F. Default AutoAtr. |
| `reversalMode` | `string` | No | enum: `["Fixed","AutoAtr","Percentage"]` | Alias for sizingMode (Kagi). |
| `manualSize` | `number` | No | - | Manual box/brick/reversal size (>0; SizingMode Fixed). Default 1.0. |
| `brickSize` | `number` | No | - | Alias for manualSize (Renko, >0). |
| `boxSize` | `number` | No | - | Alias for manualSize (PointAndFigure, >0). |
| `reversalAmount` | `number` | No | - | Alias for manualSize (Kagi, >0). |
| `percentage` | `number` | No | - | Percentage size (0.01..100; SizingMode Percentage). Default 1.0. |
| `brickPercent` | `number` | No | - | Alias for percentage (Renko, 0.01..100). |
| `boxPercent` | `number` | No | - | Alias for percentage (PointAndFigure, 0.01..100). |
| `reversalPercent` | `number` | No | - | Alias for percentage (Kagi, 0.01..100). |
| `atrPeriod` | `integer` | No | default: `14`, min: `1`, max: `500` | ATR period for AutoAtr (1..500). Default 14. |
| `atrMultiplier` | `number` | No | - | ATR multiplier for AutoAtr (>0). Default 1.0. |
| `roundingMode` | `string` | No | enum: `["None","NiceNumbers","TickSize","Floor","Ceiling","Round"]` | Rounding mode for Renko/Kagi/P&F. Default None. |
| `fallbackMode` | `string` | No | enum: `["Percentage","Fixed"]` | ATR=0 fallback mode for Renko/Kagi/P&F. Default Percentage. |
| `reversal` | `integer` | No | default: `2`, min: `1`, max: `10` | Reversal bricks/boxes (1..10; Renko default 2, P&F default 3). |
| `initialColumn` | `integer` | No | default: `0`, min: `0`, max: `10000` | Initial column index offset (Kagi, 0..10000). Default 0. |
| `lineCount` | `integer` | No | default: `3`, min: `1`, max: `20` | Line count (ThreeLineBreak, 1..20). Default 3. |
| `minimumMove` | `number` | No | - | Minimum move threshold (ThreeLineBreak, >=0). Default 0. |
| `tolerance` | `number` | No | - | Trend detection tolerance (ThreeLineBreak, >=0). Default 0.0001. |
| `includeMultiWave` | `boolean` | No | - | Include Multi-Wave patterns (Renko/Kagi/P&F/ThreeLineBreak). Default false. |
| `includePatterns` | `boolean` | No | - | Include Point and Figure patterns (P&F only). Default false. |
| `showDoubleBreakout` | `boolean` | No | - | Include Double Top/Bottom P&F signals. Default true. |
| `showTripleBreakout` | `boolean` | No | - | Include Triple Top/Bottom P&F signals. Default true. |
| `showTrendlineBreakout` | `boolean` | No | - | Include Trendline P&F signals. Default true. |
| `showTriangleBreakout` | `boolean` | No | - | Include Triangle P&F signals. Default true. |
| `showCatapultBreakout` | `boolean` | No | - | Include Catapult P&F signals. Default true. |
| `decimalPlaces` | `integer` | No | min: `0`, max: `10` | Round prices/sizes/scores (volume unrounded) (0..10, output only). Default: unrounded. |
| `compactRows` | `boolean` | No | - | true empties 'elements' and emits 'elementColumns'/'elementRows'. Default false. |

## Example Call

```json
{
  "symbol": "7203-T",
  "timeFrame": "D1",
  "chartType": "HeikinAshi",
  "outputTailCount": 200
}
```

## Common Errors

- `INSUFFICIENT_MARKET_DATA`: Not enough stored bars or overlapping dates to compute the result.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `SYMBOL_NOT_FOUND`: Symbol has no stored market data (or is unregistered when allowUnregistered is false).
