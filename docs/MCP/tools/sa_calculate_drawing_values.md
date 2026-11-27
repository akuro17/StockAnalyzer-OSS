# `sa_calculate_drawing_values`

- **Mode**: Read-Only

## Contract & Constraints

Calculates the values the chart's Data tab shows for a drawing tool (for example Fibonacci levels, trend line price, channel lines) from virtual control points, without drawing anything or saving anything. Read-only. The values are produced by the same code as the chart's drawing objects. Control points are chosen by anchorMode: RangeExtremes (two-point tools: the lowest low and the highest high inside the last anchorBars bars, the earlier bar is the first point), RangeEndpoints (the first and the last bar of that window, price taken from priceField; one- and two-point tools), AutoPivots (confirmed swing highs and lows of the last anchorBars bars: a two- or three-point tool takes the latest alternating pivots in time order, a one-point tool the latest pivot or, with pivotPreference High or Low, the latest pivot of that kind; pivotLeftStrength and pivotRightStrength are the bars each side a swing must dominate; not for LongPosition and ShortPosition), or Explicit (points: one {date, price or priceField} per control point; the only mode for LongPosition, ShortPosition and the other three-point tools). LongPosition points are Entry, Stop, Target with Stop < Entry < Target; ShortPosition points are Entry, Stop, Target with Target < Entry < Stop. RegressionTrend, AnchoredVWAP and ClassicPivotPoints are calculated from the loaded bars with the default options of the chart tool (RegressionTrend: 2 standard deviations, not extended; AnchoredVWAP: no bands, typical price; ClassicPivotPoints: daily, no extension); RegressionTrend ignores the prices of its two points (only their dates matter) and AnchoredVWAP and ClassicPivotPoints use only the date of their point. DatePriceRange (2 points) is calculated from the loaded bars between its two dates (price change, bar count, volume, highest high, lowest low, maximum drawdown, volatility; volatility is in percent per bar and is not annualised); DistributionRange (2 points) computes closed-range empirical distribution statistics (`SampleCount`, `Mean`, `StdDev`, `Median`, `IQR`, `Skewness`, `ExcessKurtosis`, `ReferenceDistribution`, `BinCount`) over `[t_start, t_end]` without inspecting bars prior to `t_start`; CrossLine (1 point) gives the values of a horizontal line and a vertical line at its control point. anchorBars counts bars of the chosen timeFrame, not calendar days; left out, it is every loaded bar. Values are evaluated at evaluationDates (default: the last loaded bar); a date without a stored bar is allowed and only extends the geometry (hasBar false, no current price). Time math is calendar time (price change per 24 hours), not bar count. Causal as of endDate (bars after it are never read). FibonacciRetracement: level 0 is the second point and level 1 the first point. GannFan: AxB means A time units to B price units (slope factor B/A); 1x1 runs through both points. SineLine: the first point is the centre line and the start of the wave; the second price sets the amplitude and the second date the period; the curve reaches the second price a quarter period after the first date, not at the second date. TimeCycles: a date on a cycle boundary starts a new cycle (phase 0%); NextCycle is the first boundary strictly after the date. Weekly and monthly bars are dated by the first day of their period; an evaluation date is matched to a bar only by exact date. For W1 and MN1 droppedLeadingBar says whether the oldest, partial period was left out; completeBarsOnly adds the partial-bar keys of sa_get_candles. controlPoints in the answer lists the points that were used.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbol` | `string` | Yes | - | Ticker symbol ('.' == '-', e.g. 7203-T). |
| `timeFrame` | `string` | Yes | enum: `["M1","M5","M15","M30","H1","H4","D1","W1","MN1"]` | Bar timeframe (D1, W1, MN1). |
| `endDate` | `string` | No | - | Inclusive end date (yyyy-MM-dd). |
| `lookbackBars` | `integer` | No | default: `10000`, min: `1`, max: `10000` | Stored bars loaded up to endDate (1..10000, newest last). |
| `tool` | `string` | Yes | enum: `["TrendLine","Ray","HorizontalLine","VerticalLine","Arrow","PriceLabel","FibonacciRetracement","FibonacciFan","FibonacciExpansion","FibonacciChannel","ParallelChannel","GannFan","GannBox","SineLine","TimeCycles","Pitchfork","AngleTool","GannSquareOfNine","TargetPriceProjection","LongPosition","ShortPosition","RegressionTrend","AnchoredVWAP","ClassicPivotPoints","CrossLine","DatePriceRange","DistributionRange","GarchVolatilityCone","RangeRegimeDiagnosticCard"]` | Drawing tool name. |
| `anchorMode` | `string` | Yes | enum: `["Explicit","RangeExtremes","RangeEndpoints","AutoPivots"]` | Anchor mode. |
| `anchorBars` | `integer` | No | default: `10000`, min: `1`, max: `10000` | Lookback bars for non-Explicit modes (1..10000). Default: all loaded. |
| `pivotLeftStrength` | `integer` | No | default: `5`, min: `1`, max: `50` | AutoPivots: left swing bars (1..50). Default 5. |
| `pivotRightStrength` | `integer` | No | default: `5`, min: `1`, max: `50` | AutoPivots: right swing bars (1..50). Default 5. |
| `pivotPreference` | `string` | No | enum: `["Latest","High","Low"]` | AutoPivots 1-point tools: pivot choice. Default Latest. |
| `priceField` | `string` | No | enum: `["Open","High","Low","Close"]` | Bar price for RangeEndpoints and Explicit points without price. Default Close. |
| `points` | `array` | No | - | Explicit: [{date, price?, priceField?}] per control point. |
| `evaluationDates` | `array` | No | - | Evaluation dates (yyyy-MM-dd, 1..20). Default: last bar. |
| `showExtensions` | `boolean` | No | - | FibonacciRetracement: use extension levels. |
| `pitchforkKind` | `string` | No | enum: `["Andrews","Schiff","ModifiedSchiff"]` | Pitchfork: kind. Default Andrews. |
| `completeBarsOnly` | `boolean` | No | - | W1/MN1 only: true drops incomplete last period; false keeps it; both add partial-bar keys. |
| `allowUnregistered` | `boolean` | No | - | Allow unregistered ticker with stored bars. Default false. |

## Example Call

```json
{
  "symbol": "7203-T",
  "timeFrame": "D1",
  "tool": "FibonacciRetracement",
  "anchorMode": "RangeExtremes",
  "anchorBars": 120
}
```

## Common Errors

- `INSUFFICIENT_MARKET_DATA`: Not enough stored bars or overlapping dates to compute the result.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `LIMIT_EXCEEDED`: Requested count, window, symbol list, or estimated response tokens exceeded server bounds.
- `SYMBOL_NOT_FOUND`: Symbol has no stored market data (or is unregistered when allowUnregistered is false).
