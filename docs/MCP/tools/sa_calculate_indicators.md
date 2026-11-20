# `sa_calculate_indicators`

- **Mode**: Read-Only

## Contract & Constraints

Calculates technical indicators on the stored bars of one symbol with the application's own indicator pipeline (the same values the chart shows). Call sa_get_capabilities with indicatorTypeFilter first to learn the exact type names, parameter names with their ranges, and output series names. Up to 16 indicators per call; each has a client-chosen unique id and the results are keyed by that id. Omitted parameters use the application defaults. All periods are in bars of the indicator's own time frame (the call's time frame unless the indicator carries its own timeFrame, then bars of that time frame, for example 5 weekly bars for W1; periodUnit is always bars). The calculation runs over up to lookbackBars stored bars (default and maximum 10000) so that long-period indicators are warmed up; only the last outputTailCount bars (default 200, at most 5000) are returned, aligned with the timestamps array. endDate (yyyy-MM-dd, inclusive) ends the calculation at the last bar dated on or before it, so the latest bar and every value are as they were known on that date (for W1 and MN1 the daily bars are cut at endDate before the period bars are built; the window is then the latest lookbackBars bars up to endDate); comparison symbols are cut at the same date, no bar after it is read, and endDate is echoed in the result only when given. completeBarsOnly (W1 and MN1 only, ignored for D1; not given = the running last period is calculated as before and nothing about it is reported): true calculates on complete periods only, leaving out a running last period (droppedPartialBar); true and false both add referenceDate, lastBarPeriodEnd, lastBarPartial and droppedPartialBar, which work as in sa_get_candles. A leading null means the indicator has not accumulated enough bars yet (leadingNullCount counts them over the whole calculated history). Some indicators (for example Ichimoku SenkouSpanA and SenkouSpanB) also project values beyond the last bar; those are returned in projectedValues and belong to no bar yet. nonCausalSeries lists series whose value at a bar depends on later bars. An indicator with its own timeFrame that lists nonCausalSeries still looks ahead by the indicator's own look-ahead after the alignment, because the alignment only steps back one closed period. Indicator on indicator: sourceIndicatorId feeds one output series (sourceOutputSeriesName) of another indicator of the same call as the price input, dynamicPeriodIndicatorId feeds one as adaptive periods; one upstream indicator can be asked for one series only. Cross-ticker indicators need the ComparisonSymbol parameter. An indicator may carry its own timeFrame (D1, W1, MN1; the call's time frame or a coarser one, a finer one is UNSUPPORTED_TIMEFRAME): it is then calculated on the bars of that time frame (up to the last day of the call's window, at most the maximum number of bars; lookbackBars applies to the call's bars only) and aligned to the call's timestamps, so the value shown at a bar is that of the previous CLOSED period containing the bar (a W1 value at a daily bar is last week's, never the running week's; before the first closed period it is null). One call can therefore return D1, W1 and MN1 values of an indicator on the daily timestamps. alignment PreviousClosedPeriod and the indicator's timeFrame are echoed for such an indicator; it has no projectedValues. An indicator and the indicators it reads (sourceIndicatorId, dynamicPeriodIndicatorId) must have the same timeFrame, and a cross-ticker indicator keeps the call's time frame. allowUnregistered true also accepts a symbol (and comparison symbols) that is not in the ticker list but has stored daily bars; it is named in warnings, and a symbol without stored bars is SYMBOL_NOT_FOUND. derived (optional, at most 16) adds series computed bar by bar from two series of the same call: {id, op, left, right} with op one of Add, Subtract, Multiply, Divide, Max, Min (exact names) and each side {"ref":"<id>"} (an indicator id, or a derived id declared before; {"ref":"<id>","outputSeriesName":"<name>"} picks another output series of an indicator, default its main series) or {"constant":<number>}. A bar has no value (null) when either side has none, and Divide by zero gives null and is counted in divisionByZeroCount (never an infinite value). A derived series is returned in results under its id with type Derived, one series Main, latestValues, leadingNullCount, nonCausalSeries (Main when an input looks ahead) and unit null (the unit of a derived series is not defined); one whose input did not succeed reports isSuccessful false and names that input, the call itself still succeeds. Derived series are output only: they cannot be used in screener or backtest conditions. summary true adds, per series, count (non-null values), latest (the last bar), mean, stdDev (population), zScoreOfLatest ((latest - mean) / stdDev; null when stdDev is below 1E-10, an absolute value, or latest is null), percentileRankOfLatest (percent, 0-100: earlier non-null values strictly smaller than latest / earlier non-null values; null without earlier values), trendSlope (least-squares slope per bar over the last trendBars bars, each value at its own bar position, null below two values) and trendBars; mean, stdDev, latest and trendSlope are in the series' own unit, zScoreOfLatest has none and percentileRankOfLatest is a percentage; they are computed over the whole calculated window, not only the returned tail, unless summaryBars limits them to the last bars (the summary then also reports summaryBars and summaryWindowBars; pastValues still read the whole window). summaryDistribution true (needs summary true) adds median, iqr, mad, skewness, excessKurtosis and robustTrendSlope to each summary; robustTrendSlope is null when trendBars is above 30 (cost protection, not an error). trendBars and the offsets count bars of the call's time frame (an indicator with its own timeFrame is already aligned to those bars); a series listed in nonCausalSeries keeps its look-ahead in its summary and pastValues. lookbackOffsets adds pastValues (value k bars before the last calculated bar), includeSeries false returns only the compact figures (latestValues, summary, pastValues) to save tokens. A call that runs out of time starts no further indicator, but the indicator running at that moment is not interrupted and keeps the server busy until it ends (a heavy type with a large Period can take a long time on its own): ask for few heavy indicators per call. The one exception is HiddenMarkovModel, which checks the cancellation in every window and stops within about 0.1 s (not when it takes another indicator as its source). decimalPlaces (0 to 10, optional) rounds every decimal number of the result (series, projectedValues, latestValues, summary, pastValues; away from zero at the midpoint) to that many decimal places to save tokens; counts (count, trendBars, leadingNullCount, divisionByZeroCount) are never rounded, the calculation itself is not rounded, and without it the numbers are as calculated. Prices are in the instrument's units; the application stores no output unit per indicator (see unitUndefined in sa_get_capabilities).

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbol` | `string` | Yes | - | Ticker symbol ('.' == '-', e.g. 7203-T). |
| `timeFrame` | `string` | Yes | enum: `["M1","M5","M15","M30","H1","H4","D1","W1","MN1"]` | Bar timeframe (D1, W1, MN1). |
| `indicators` | `array` | Yes | - | Indicators to calculate (1..16). |
| `derived` | `array` | No | - | Derived series computed from earlier series (max 16). |
| `lookbackBars` | `integer` | No | default: `10000`, min: `1`, max: `10000` | Stored bars loaded (1..10000, newest last). |
| `outputTailCount` | `integer` | No | default: `200`, min: `1`, max: `5000` | Trailing bars returned per series (1..5000). Default 200. |
| `endDate` | `string` | No | - | Inclusive end date (yyyy-MM-dd). Default: latest bar. |
| `completeBarsOnly` | `boolean` | No | - | W1/MN1 only: true drops incomplete last period, false keeps it; both add partial-bar keys. |
| `summary` | `boolean` | No | - | Add per-series stats (count, latest, mean, stdDev, zScoreOfLatest, percentileRankOfLatest, trendSlope, trendBars). Default false. |
| `trendBars` | `integer` | No | default: `20`, min: `2`, max: `10000` | Summary trendBars window (2..10000; needs summary true). Default 20. |
| `summaryBars` | `integer` | No | min: `2`, max: `10000` | Trailing bars for summary (2..10000); needs summary true. Default: all calculated bars. |
| `summaryDistribution` | `boolean` | No | - | Add median, iqr, mad, skewness, excessKurtosis, robustTrendSlope (null, not an error, when trendBars>30); needs summary true. Default false. |
| `lookbackOffsets` | `array` | No | - | Non-negative bar offsets back from last bar for pastValues (max 32). |
| `includeSeries` | `boolean` | No | - | false omits series/projectedValues/timestamps (needs summary true or lookbackOffsets; excludes outputTailCount). Default true. |
| `allowUnregistered` | `boolean` | No | - | Allow unregistered symbols with stored daily bars. Default false. |
| `decimalPlaces` | `integer` | No | min: `0`, max: `10` | Round output decimals (0..10, output only). Default: unrounded. |

## Example Call

```json
{
  "symbol": "7203-T",
  "timeFrame": "D1",
  "indicators": [
    {
      "id": "sma25",
      "type": "SMA",
      "parameters": {
        "Period": 25
      }
    }
  ],
  "outputTailCount": 200
}
```

## Common Errors

- `INSUFFICIENT_MARKET_DATA`: Not enough stored bars or overlapping dates to compute the result.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `INVALID_INDICATOR_PARAMETER`: Indicator parameter name is unknown or value is outside allowed bounds.
- `LIMIT_EXCEEDED`: Requested count, window, symbol list, or estimated response tokens exceeded server bounds.
- `SYMBOL_NOT_FOUND`: Symbol has no stored market data (or is unregistered when allowUnregistered is false).
- `UNSUPPORTED_TIMEFRAME`: Requested timeFrame is not supported for stored bars or this tool.
