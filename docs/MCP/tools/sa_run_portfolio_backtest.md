# `sa_run_portfolio_backtest`

- **Mode**: Read-Only

## Contract & Constraints

Runs an independent-sleeves portfolio backtest across multiple symbols with equal capital allocation, reporting individual sleeve results and composite portfolio performance. Give either symbols (1 to 50 tickers), groupId, or symbolsFromResultId (mutually exclusive). Give either conditions or templateId (mutually exclusive). Capital is equally divided across sleeves: sleeveCapital = initialCapital / N (decimal precision). Each sleeve runs independently through the core backtest engine. Symbols with insufficient data remain as cash. Outputs individual sleeve metrics and composite portfolio equity, return, drawdown and pooled trade statistics. allocation defaults to 'Equal'; maxConcurrentPositions is not accepted in the independent sleeves model. compactReport true writes each symbol's five metrics as bare numbers (null when not Valid) and adds unavailableMetrics [{name, status, reason}] and metricUnits to every symbol result; the numbers are the same.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbols` | `array` | No | - | Symbols to test (1 to 50). Mutually exclusive with groupId and symbolsFromResultId. |
| `groupId` | `string` | No | - | Guid of a saved ticker group (from sa_list_ticker_groups) to test. Mutually exclusive with symbols and symbolsFromResultId. |
| `symbolsFromResultId` | `string` | No | - | Handle id from sa_run_screener or sa_filter_tickers (storeResult true). Mutually exclusive with symbols and groupId. |
| `resultOffset` | `integer` | No | default: `0`, min: `0` | 0-based start index within symbolsFromResultId. Default 0. |
| `resultLimit` | `integer` | No | min: `1`, max: `50` | Maximum symbols to read from symbolsFromResultId (1..50). |
| `conditions` | `object` | No | - | The entry, exit and reverse conditions. Exactly one of conditions and templateId. |
| `templateId` | `string` | No | - | Id of a saved backtest-condition template (sa_list_templates with templateType backtestCondition). Exactly one of conditions and templateId. |
| `allocation` | `string` | No | enum: `["Equal"]` | Capital allocation model across symbols. Only 'Equal' is supported. |
| `startDate` | `string` | No | - | First evaluated day, yyyy-MM-dd. Default: start of stored history. |
| `endDate` | `string` | No | - | Last evaluated day, yyyy-MM-dd. Default: latest stored bar. |
| `timeFrame` | `string` | No | enum: `["D1","W1","MN1"]` | Time frame of the bars (D1, W1, MN1). Default D1. |
| `account` | `object` | No | - | Account capital, cost, sizing, margin and execution-realism settings (applied equally per sleeve). |
| `riskManagement` | `object` | No | - | Optional stop-loss, take-profit and dynamic exits: {stopLossRatio, takeProfitRatio, dynamicExits}. |
| `report` | `object` | No | - | Report settings: {annualRiskFreeRate, annualMar, annualPeriodsD1, bootstrapSeed, bootstrapIterations}. |
| `maxReturnedTradesPerSymbol` | `integer` | No | default: `0`, min: `0`, max: `200` | Most trades to list per symbol (default 0; maximum 200). |
| `includeEquityCurve` | `string` | No | enum: `["None","Weekly","Monthly"]` | Add composite equity curve: None (default), Weekly or Monthly. |
| `allowUnregistered` | `boolean` | No | - | Accept symbols that are not in the ticker list but have stored bars. Default false. |
| `compactReport` | `boolean` | No | - | Write each metric as a bare number (null unless Valid), plus unavailableMetrics and metricUnits. Default false. |

## Example Call

```json
{
  "symbols": [
    "AAPL",
    "MSFT"
  ],
  "timeFrame": "D1",
  "conditions": {
    "entryLong": {
      "left": {
        "price": "Close"
      },
      "comparison": "GreaterThan",
      "right": {
        "price": "Close",
        "offset": 1
      }
    },
    "exitLong": {
      "left": {
        "price": "Close"
      },
      "comparison": "LessThan",
      "right": {
        "price": "Close",
        "offset": 1
      }
    }
  }
}
```

## Common Errors

- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `LIMIT_EXCEEDED`: Requested count, window, symbol list, or estimated response tokens exceeded server bounds.
- `TEMPLATE_NOT_FOUND`: No saved template matches the given templateType and templateId.
- `UNSUPPORTED_TIMEFRAME`: Requested timeFrame is not supported for stored bars or this tool.
