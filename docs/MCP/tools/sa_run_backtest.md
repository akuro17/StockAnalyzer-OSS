# `sa_run_backtest`

- **Mode**: Read-Only

## Contract & Constraints

Runs one backtest on the stored bars of one symbol (daily D1, weekly W1 or monthly MN1) with the application's own backtest engine (the Backtest window's Legacy execution model) and returns the performance report, the trades and the run fingerprint. Call sa_get_capabilities first for indicator types, parameters and series; indicators that look ahead are not allowed. Give either conditions or templateId: templateId runs a saved backtest-condition template (ids from sa_list_templates with templateType backtestCondition) with its own conditions and risk settings; riskManagement must then be omitted, and the template's sides must use the evaluated time frame or a coarser one. conditions has up to six roots: entryLong, entryShort, exitLong, exitShort, reverseLong, reverseShort. entryLong/entryShort open a long/short position, exitLong/exitShort close it, reverseLong flips a short position to long and reverseShort flips a long position to short (a reverse root acts only while a position is held). Give at least one root, normally an entry. A root is a group {operator: And|Or, children: [...]} of groups and comparisons, or one comparison. A comparison is {left: side, comparison: GreaterThan|GreaterThanOrEqual|LessThan|LessThanOrEqual|Equal|NotEqual, right: number | side}. Besides those, CrossesAbove, CrossesBelow, Rising, Falling are accepted as comparison and are expanded exactly into the comparisons above, joined by And (the result shows the expansion): CrossesAbove = left > right now and left <= right one bar earlier; CrossesBelow = left < right now and left >= right one bar earlier (a number as right stays that number); Rising and Falling take bars N (1 up to the largest offset) instead of right: every one of the last N bar-to-bar steps of left goes up (Rising) or down (Falling), that is left[0] > left[1] and left[1] > left[2] ... and left[N-1] > left[N] for Rising. The expanded comparisons count against the tree-size limits; an offset that the expansion would push past the largest offset is INVALID_ARGUMENT. A side names exactly one of indicator (with optional parameters and outputSeriesName) or price (Close, Open, High, Low, ...), plus an optional offset (bars back from the evaluated bar, default 0) and an optional frame (D1|W1|MN1, the evaluated frame or a coarser one; a finer one is UNSUPPORTED_TIMEFRAME) that reads the side on that frame's bars: the value shown at a bar is that of the previous CLOSED period containing the bar (a W1 side on a daily bar is last week's value, never the running week's), and a bar with no closed period yet does not match. Example: {"entryLong":{"left":{"indicator":"RSI","parameters":{"Period":14}},"comparison":"LessThan","right":30},"exitLong":{"left":{"indicator":"RSI","parameters":{"Period":14}},"comparison":"GreaterThan","right":70}}. startDate and endDate (yyyy-MM-dd, inclusive) default to the stored history (at most the latest 10000 bars); bars before startDate warm the indicators up. account sets initialCapital, commissionFlat, commissionPerUnit, slippageRatio, sizingModel (FixedQuantity or PercentOfEquity), sizingParameter, initialMarginRatio, maintenanceMarginRatio and liquidationPenaltyRatio, and the execution-realism keys maxVolumeParticipationRatio (number in (0, 1], the most of a bar's volume an order may take; omitted means no limit), volumeConstraintMode (RejectIfExceeds, ClampAndCancelRemainder, ClampAndCarryOverRemainder; has no effect without maxVolumeParticipationRatio), shortBorrowAnnualRate (simple annual borrow rate of a short position, 0 to 1, 0.02 = 2%; default 0), borrowFeeBasisMode (EntryPriceFixed, MarkToMarketDaily) and priceLimitFillMode (AlwaysFill, RejectIfAtLimitPrice); the result echoes these five only when the call names them, and naming one with its default value gives the same reproducibilityHash as leaving it out, and the same runFingerprint when the number is written with the same number of decimals (the fingerprint encodes the decimal scale: 0 and 0.0, or 0.02 and 0.020, differ); riskManagement sets stopLossRatio and takeProfitRatio of the entry price (0.05 = 5%), and dynamicExits (maxHoldingBars, atrStop, atrTarget, trailingStop); report sets annualRiskFreeRate, annualMar, annualPeriodsD1 (daily bars per year, for the annualised metrics), bootstrapSeed and bootstrapIterations (the report's bootstrap confidence intervals). Omitted values are the Backtest window's factory defaults, not the values saved in the window; the result echoes every effective value. The supported time frames are D1, W1, MN1; the Legacy execution model is used. The result lists the first maxReturnedTrades trades in the order the engine closed them (default and maximum 200; totalTrades is the true count) and engine warnings by code. Each trade carries mfePrice and maePrice (largest favourable and adverse excursion from the entry price, in price units, never negative, measured on bar highs and lows so the order inside a bar is unknown, from the actual fill prices so slippage is already inside them and commissions are not included; prices that are certain to come after the exit are not counted) and mfePercent and maePercent (the same as percent of the entry price; null when the entry price is not positive), and stopLevelAtExit (stop price threshold at exit when exited via a stop; null otherwise). Optional diagnostics, all off by default: includeEquityCurve (None|Bar|Weekly|Monthly) adds equityCurve (equity in account currency per evaluated bar, or the last bar of each ISO week or calendar month, with drawdownRatio = equity / running high - 1, at most 5000 points, more is LIMIT_EXCEEDED); includeDrawdownEpisodes (0 to 5) adds the deepest drawdown episodes (depthRatio, dates, bar counts; recovery fields are null while still underwater); includePeriodicReturns adds monthly and yearly returnRatio (period-end equity / previous period-end equity - 1; isPartial marks a period the evaluated dates cut). includePeriodicReturns and includeEquityCurve Monthly are refused for timeFrame W1 (UNSUPPORTED_TIMEFRAME) because a weekly bar cannot be cut at a month end. A part whose arithmetic fails is left out and named in diagnosticsUnavailable with a reason. allowUnregistered true also accepts a symbol that is not in the ticker list but has stored bars; it is named in warnings, and a symbol without stored bars is SYMBOL_NOT_FOUND. Metrics keep status/value/unit/reason: a value is present only when status is Valid. compactReport true writes each report metric as its bare number instead (null when it is not Valid), lists the metrics that are not Valid in the report's unavailableMetrics [{name, status, reason}] and the unit of each Valid one in metricUnits; the numbers are the same, nothing is rounded. compactRows true returns trades.items (and equityCurve.points when requested) as [] and writes columns plus rows (one array per trade/point) to save tokens; default false. Loaded bars are pre-checked by BacktestBarIntegrityValidator (invalid OHLCV fails with INVALID_ARGUMENT naming symbol, date, O/H/L/C/V, and CandleInvariant violation code without repair or skipping). warnings[] emits SIZING_NOTIONAL_SMALL when SizingModel is FixedQuantity, totalTrades >= 1, initialCapital > 0, and sizingParameter * windowHighestClose / initialCapital < BacktestSizingWarnRatio (default 0.01), and SAMPLE_TOO_SMALL when evaluatedBarCount (= equity return samples m = equity points - 1) is below BacktestMinBarsForRatioMetrics (default 20 = BacktestReportGenerator.MinReturnSamplesForRatioMetrics). The tool never changes anything and never fetches data.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbol` | `string` | Yes | - | Symbol to test (from sa_list_tickers). |
| `conditions` | `object` | No | - | Entry/exit/reverse conditions tree. Mutually exclusive with templateId (one required). |
| `templateId` | `string` | No | - | Saved backtestCondition template GUID. Mutually exclusive with conditions (one required). |
| `startDate` | `string` | No | - | First evaluated date (yyyy-MM-dd). Default: history start. |
| `endDate` | `string` | No | - | Last evaluated date (yyyy-MM-dd). Default: latest bar. |
| `timeFrame` | `string` | No | enum: `["D1","W1","MN1"]` | Bar timeframe (D1, W1, MN1). Default D1. |
| `account` | `object` | No | - | Account capital, cost, sizing, margin, and execution-realism settings. Default: factory defaults. |
| `riskManagement` | `object` | No | - | Stop-loss, take-profit, and exits: {stopLossRatio, takeProfitRatio, dynamicExits{maxHoldingBars, atrStop, atrTarget, trailingStop}}. |
| `report` | `object` | No | - | Report config: {annualRiskFreeRate, annualMar, annualPeriodsD1, bootstrapSeed, bootstrapIterations}. Default: factory defaults. |
| `maxReturnedTrades` | `integer` | No | default: `200`, min: `0`, max: `200` | Max trades returned (0..200; 0 lists none). Default 200. |
| `allowUnregistered` | `boolean` | No | - | Allow unregistered symbol with stored bars. Default false. |
| `includeEquityCurve` | `string` | No | enum: `["None","Bar","Weekly","Monthly"]` | Equity curve resolution (Bar LIMIT_EXCEEDED >5000 pts). Default None. |
| `includeDrawdownEpisodes` | `integer` | No | default: `0`, min: `0`, max: `5` | Deepest drawdown episodes count (0..5). Default 0. |
| `includePeriodicReturns` | `boolean` | No | - | Include monthly and yearly equity returns. Default false. |
| `compactReport` | `boolean` | No | - | Write each metric as a bare number (null unless Valid), plus unavailableMetrics and metricUnits. Default false. |
| `compactRows` | `boolean` | No | - | true empties trades.items/equityCurve.points and emits columns/rows arrays. Default false. |

## Example Call

```json
{
  "symbol": "7203-T",
  "timeFrame": "D1",
  "conditions": {
    "entryLong": {
      "left": {
        "indicator": "RSI",
        "parameters": {
          "Period": 14
        }
      },
      "comparison": "LessThan",
      "right": 30
    },
    "exitLong": {
      "left": {
        "indicator": "RSI",
        "parameters": {
          "Period": 14
        }
      },
      "comparison": "GreaterThan",
      "right": 70
    }
  },
  "maxReturnedTrades": 50
}
```

## Common Errors

- `INSUFFICIENT_MARKET_DATA`: Not enough stored bars or overlapping dates to compute the result.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `LIMIT_EXCEEDED`: Requested count, window, symbol list, or estimated response tokens exceeded server bounds.
- `SYMBOL_NOT_FOUND`: Symbol has no stored market data (or is unregistered when allowUnregistered is false).
- `TEMPLATE_NOT_FOUND`: No saved template matches the given templateType and templateId.
- `UNSUPPORTED_TIMEFRAME`: Requested timeFrame is not supported for stored bars or this tool.
