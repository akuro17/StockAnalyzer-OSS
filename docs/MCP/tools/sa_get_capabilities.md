# `sa_get_capabilities`

- **Mode**: Read-Only

## Contract & Constraints

Describes what this server can calculate: every indicator type with its display name, category and backtest eligibility, and the supported time frames. Call it before sa_calculate_indicators to learn exact type names, parameter names with their default and allowed range, and output series names. Without arguments the list is compact (type, displayName, category, backtestEligibility, isRegistered); with indicatorTypeFilter (or includeDetails true) each indicator also lists parameters, outputSeriesNames, isCrossTicker and nonCausalSeries. A parameter has min and max from the indicator's hard range (CoreParameterRange) and, where the chart input is narrower, guiMin and guiMax. isCrossTicker marks an indicator that compares two symbols: it needs its ComparisonSymbol parameter and is accepted by sa_calculate_indicators and sa_run_screener, not by sa_run_backtest. backtestEligibility is Allowed, NonCausal (the value at a bar depends on later bars) or SynchronousCalculationUnsupported; nonCausalSeries lists the series that look ahead. With details each indicator also has screenerEligibility, the same three values for the screener (sa_run_screener): the indicators that compare two symbols or compute universe breadth are Allowed there although the backtest refuses them. Unregistered types are defined in the product but cannot be calculated (isRegistered false). unit is null because no indicator output unit is stored in the application; unitUndefined lists those types. includeScreenerCatalog adds the names sa_run_screener accepts besides indicators: columns (isText true for the text columns; unavailableColumns lists table columns no condition can read reliably), criteria (patterns) and price sources, with the comparison and group operator names, timeFrame (the default frame of a scan) and sideTimeFrames (the frames a side may name with its own timeFrame; a column read from the stored metadata has none). execution says whether the training tools are offered (enabled) and, when they are, lists what sa_start_training accepts: frameworks, architectures, hyperparameters, the fixed values and the limits, and what sa_start_rl_training does (reinforcementLearning). Period parameters and lengths are always in bars of the requested time frame.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `category` | `string` | No | enum: `["Trend","Oscillator","Volume","Volatility","Math","Chart","Other"]` | Only indicators of this category. |
| `indicatorTypeFilter` | `array` | No | - | Only these indicator types (case-insensitive names, for example RSI). Turns the details on unless includeDetails is false. |
| `includeDetails` | `boolean` | No | - | Add parameters, output series and flags to each indicator. Default: true when indicatorTypeFilter is given, otherwise false. |
| `includeScreenerCatalog` | `boolean` | No | - | Add the screener catalogue (columns, criteria, price sources, operators). Default false. |

## Output Schema (`outputSchema`)

```json
{"type":"object","properties":{"indicatorCount":{"type":"integer","description":"Number of indicators matching the filter."},"periodUnit":{"type":"string","description":"Unit of period parameters."},"supportedTimeFrames":{"type":"array","description":"Supported bar time frames.","items":{"type":"string"}},"supportedChartSeriesTypes":{"type":"array","description":"Supported chart series types.","items":{"type":"string"}},"indicators":{"type":"array","description":"Available indicators.","items":{"type":"object","properties":{"type":{"type":"string","description":"Indicator type name."},"isRegistered":{"type":"boolean","description":"True when indicator is registered for calculation."},"displayName":{"type":"string","description":"Display name."},"category":{"type":"string","description":"Indicator category."},"backtestEligibility":{"type":"string","description":"Backtest eligibility."},"screenerEligibility":{"type":"string","description":"Screener eligibility (details only): Allowed, NonCausal or SynchronousCalculationUnsupported; differs from backtestEligibility for the indicators that compare two symbols or compute universe breadth."},"unit":{"type":["string","null"],"description":"Unit of measurement."},"isCrossTicker":{"type":"boolean","description":"True when indicator compares two symbols."},"outputSeriesNames":{"type":"array","description":"Output series names.","items":{"type":"string"}},"nonCausalSeries":{"type":"array","description":"Series that look ahead.","items":{"type":"string"}},"parameters":{"type":"array","description":"Parameters list.","items":{"type":"object","properties":{"name":{"type":"string","description":"Parameter name."},"displayName":{"type":"string","description":"Parameter display name."},"type":{"type":"string","description":"Type name of the parameter."}},"required":["name","displayName","type"],"additionalProperties":true}}},"required":["type","isRegistered","displayName"],"additionalProperties":false}},"unitUndefined":{"type":"array","description":"Indicators with undefined output unit.","items":{"type":"string"}},"backtestOptimization":{"description":"Backtest optimization settings.","type":"object","properties":{"objectives":{"type":"array","description":"Available optimization objective names.","items":{"type":"string"}},"excludedObjectives":{"type":"array","description":"Excluded optimization objectives.","items":{"type":"object","properties":{"name":{"type":"string","description":"Metric name."},"reason":{"type":"string","description":"Reason excluded."}},"required":["name","reason"],"additionalProperties":false}}},"required":["objectives","excludedObjectives"],"additionalProperties":false}},"required":["indicatorCount","periodUnit","supportedTimeFrames","supportedChartSeriesTypes","indicators","unitUndefined","backtestOptimization"],"additionalProperties":true}
```

## Example Call

```json
{
  "category": "Oscillator",
  "includeDetails": true
}
```

## Common Errors

- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `UNKNOWN_INDICATOR`: Unknown indicator type name; check sa_get_capabilities.
