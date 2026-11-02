# 06. Backtest Configuration & Reporting Settings

This document specifies configuration files governing the Backtest execution engine, strategy runner parameters, condition trees, and risk management settings.

---

## 1. Saved Backtest Execution Configuration (`backtest_configuration.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/Config/backtest_configuration.json` |
| **Path Resolver** | `PathDiscovery.ResolveConfigPath("backtest_configuration.json")` |
| **Managing Class** | `StockAnalyzer.Core.Services.Backtest.Configuration.BacktestConfigurationManager` |
| **Model Class** | `StockAnalyzer.Core.Models.Backtest.Configuration.BacktestConfigurationDto` |
| **Schema Versions** | `1` (flat condition list) or `2` (hierarchical condition tree) |
| **Serialization Options**| `WorkspacePolymorphicResolver` (polymorphic parameter resolution via `"$type"`) |

Captures the complete state of a configured backtest run (symbol, timeframe, evaluation dates, capital, commission, margin, indicators, and strategy signal rules).

### Mutual Exclusivity Invariant: Schema Version 1 vs. 2
- **`SchemaVersion: 1`**: Strategy rules are stored in `ConditionEntries` (flat array). `ConditionTree` MUST be `null` (or omitted).
- **`SchemaVersion: 2`**: Strategy rules are stored in `ConditionTree` (6-root boolean tree). `ConditionEntries` MUST be an empty array `[]`.
- Storing conditions in both at the same time is strictly rejected during loading and validation.

---

## 2. DTO Specifications

### Group A: Target Data & Evaluation Period
- **`ExecutionModel`** (`integer`, enum `ExecutionModel`): Execution contract (`0` = `Legacy`, `1` = `StrictEvidence`, `2` = `YFinanceApproximate`). Values outside `0..2` are rejected on load.
- **`Symbol`** (`string`, required): Ticker symbol evaluated (e.g. `"AAPL"`).
- **`Frame`** (`integer`, enum `TimeFrame`): Candle timeframe. Integer ordinals ONLY (this file has no string-enum converter): `0`=`M1`, `1`=`M5`, `2`=`M15`, `3`=`M30`, `4`=`H1`, `5`=`H4`, `6`=`D1` (daily), `7`=`W1` (weekly), `8`=`MN1` (monthly). Market data is served only for `6`, `7`, `8`; `1` is **5-minute**, not daily.
- **`EvaluationStartUtc`** / **`EvaluationEndUtc`** (`string (ISO 8601)`): Evaluation start and end timestamps. MUST have zero UTC offset (`Z` suffix) and `EvaluationStartUtc <= EvaluationEndUtc`.

### Group B: Capital & Cost Model
- **`InitialCapital`** (`decimal`, `> 0`, e.g. `100000.0`): Starting cash balance.
- **`CommissionFlat`** (`decimal`, `>= 0`, e.g. `0.0`): Fixed fee per order.
- **`CommissionPerUnit`** (`decimal`, `>= 0`, e.g. `0.005`): Fee per traded share.
- **`SlippageRatio`** (`decimal`, `0 <= s < 1`, e.g. `0.0005` [0.05%]): Proportional execution slippage.
- **`SizingModel`** (`integer`, enum `PositionSizingModel`): `0` = `FixedQuantity`, `1` = `PercentOfEquity`. These are the only two members; any other value is rejected on load.
- **`SizingParameter`** (`decimal`): Depends on `SizingModel`. `FixedQuantity`: integer `>= 1` (shares per entry). `PercentOfEquity`: ratio with `0 < value <= 1` (`0.20` = 20% of equity).

### Group C: Margin Model
- **`InitialMarginRatio`** (`decimal`, `0 < r <= 1`, e.g. `0.50` [2x leverage]): Required margin to open a position.
- **`MaintenanceMarginRatio`** (`decimal`, `> 0` and strictly `< InitialMarginRatio`, e.g. `0.25`): Liquidation threshold ratio.
- **`LiquidationPenaltyRatio`** (`decimal`, `0 <= p < 1`, e.g. `0.05`): Penalty ratio assessed on liquidation.

### Group D: Statistical Resampling
- **`BootstrapSeed`** (`integer`, default `42`): Seed for Sortino/Sharpe bootstrap confidence intervals.
- **`BootstrapIterations`** (`integer`, default `2000`): Resampling iterations count (validated by `BacktestReportOptions`; use a positive value).

### Tab 2: Selected Indicators (`BacktestSelectedIndicatorDto`)

Stored in `SelectedIndicators`:
- **`Key`** (`string`, required): Lookup handle referenced by strategy rules (e.g. `"fast_sma"`).
- **`Type`** (`integer`, enum `IndicatorType`): Registered indicator type, integer ordinal only (e.g. `0` = `SMA`; see `09_indicator_catalog.md`). Must be a backtest-eligible indicator (see "Backtest-Eligible Indicators" below).
- **`Frame`** (`integer?`, optional `TimeFrame?`): Indicator timeframe override (`6`/`7`/`8`). `null` inherits run's `Frame`.
- **`Parameters`** (`object?`, polymorphic with `"$type"`): Concrete indicator parameter object (e.g. `"$type": "CoreSmaParameter"`, `"Period": 20`). See `09_indicator_catalog.md`.

### Comparison Condition Side (`BacktestConditionSideDto`)

Specifies one operand (Left or Right) of a condition:
- **`IndicatorType`** (`integer`, enum `IndicatorType`): Target indicator type (ordinal, see `09_indicator_catalog.md`).
- **`OutputName`** (`string`, default `"Main"`): Output series name (e.g. `"Main"`, `"Signal"`, `"UpperBand"`).
- **`Offset`** (`integer`, default `0`, range `0..500` [`Backtest:MaxConditionOffset`]): Number of bars to look back (`0` = current bar, `1` = previous bar). Negative values are rejected.
- **`Frame`** (`integer?`): Timeframe override (`6`/`7`/`8`; `null` = run's `Frame`).
- **`PriceSource`** (`integer?`, enum `PriceType`): Price extraction source for the `Price` indicator: `0`=Close, `1`=Open, `2`=High, `3`=Low, `4`=Median, `5`=Midpoint, `6`=Typical, `7`=Weighted, `8`=Average, `9`=HeikinAshiOpen, `10`=HeikinAshiHigh, `11`=HeikinAshiLow, `12`=HeikinAshiClose, `13`=TrueHigh, `14`=TrueLow.
- **`Parameters`** (`object?`, polymorphic with `"$type"`): Concrete indicator parameter object.

### Flat Condition Entry (`BacktestConditionEntryDto`)

Stored in `ConditionEntries` (Schema 1):
- **`Left`** (`BacktestConditionSideDto`, required): Left-hand side operand.
- **`Operator`** (`integer`, enum `ComparisonOperator`): Only `0`..`5` are valid in a backtest:
  `0` = `GreaterThan` (>), `1` = `GreaterThanOrEqual` (>=), `2` = `LessThan` (<), `3` = `LessThanOrEqual` (<=), `4` = `Equal` (==), `5` = `NotEqual` (!=).
  `6` = `Contains` and `7` = `DoesNotContain` exist in the enum (Screener string matching) but are **rejected** by the backtest validator. There is **no** cross-above / cross-below operator: express a crossover with two conditions on different `Offset` values (e.g. `Close[0] > SMA[0]` AND `Close[1] <= SMA[1]`).
- **`TargetMode`** (`integer`, enum `RightHandTargetMode`): `0` = `NumericValue`, `1` = `Indicator`. (`2` = `StringValue` is Screener-only and rejected here.)
- **`RightNumericValue`** (`decimal`): Threshold constant used when `TargetMode` is `0`.
- **`Right`** (`BacktestConditionSideDto?`): Right-hand side operand. REQUIRED when `TargetMode` is `1`; omit or `null` when `0`.
- **`LogicalOperator`** (`integer`, enum `LogicalOperator`): Combines this entry with the NEXT entry (`0` = `And`, `1` = `Or`). Default `0`.
- **`Role`** (`integer`, enum `BacktestConditionRole`): `0` = `EntryOnly`, `1` = `ExitOnly`, `2` = `Both` (default), `3` = `Reversal`.
- **`Position`** (`integer`, enum `TradeSide`): Direction an entry opens (`0` = `Long` [default], `1` = `Short`).

> **Condition-tree leaves MUST NOT specify `Role`, `Position` or `LogicalOperator`** (omit all three, or use exactly the defaults `Role: 2`, `Position: 0`, `LogicalOperator: 0`). Any other value fails loading with "a condition-tree leaf must not carry Role/Position/LogicalOperator" because the leaf''s tree location and parent group already define them. Only flat `ConditionEntries` (Schema 1) use these three fields.

### Condition Tree (`BacktestConditionTreeDto`)

Stored in `ConditionTree` (Schema 2):
Comprises 6 canonical root groups evaluated independently:
1. **`EntryLong`**: Conditions triggering new Long positions.
2. **`EntryShort`**: Conditions triggering new Short positions.
3. **`ExitLong`**: Conditions closing active Long positions.
4. **`ExitShort`**: Conditions closing active Short positions.
5. **`ReverseLong`**: Conditions reversing Long positions to Short.
6. **`ReverseShort`**: Conditions reversing Short positions to Long.

#### Condition Node Structure (`BacktestConditionNodeDto`)
- **`Kind`** (`integer` or `string`, enum `BacktestConditionNodeKind`):
  - **`0` = `Leaf`**: Represents a single condition comparison. `Comparison` (`BacktestConditionEntryDto`) is required, and `Children` must be empty `[]`.
  - **`1` = `Group`**: Represents a logical grouping. `Operator` (`0` = `And`, `1` = `Or`) combines child nodes in `Children`. `Comparison` must be `null`. Optional `Name` can provide a UI label.

### Risk Management (`BacktestRiskManagementSettingsDto`)

Stored in `RiskManagement` (optional, can be `null`):
- **`StopLossPercent`** (`decimal?`, `null` or `0 <= value < 1`): Maximum loss as a **fraction** (ratio) of the entry price, e.g. `0.03` = 3%. Despite the name, the value is NOT a percentage number: `3.0` or any value `>= 1` fails loading with an out-of-range error.
- **`TakeProfitPercent`** (`decimal?`, `null` or `0 <= value < 1`): Profit target as a fraction of the entry price, e.g. `0.10` = 10%. Same range rule as above.
*(Note: Trailing stop percentage is not part of this configuration model).*

---

## 3. Reference Examples

### Example A: Schema Version 1 (Flat Condition Entries)
```json
{
  "SchemaVersion": 1,
  "ExecutionModel": 0,
  "Symbol": "AAPL",
  "Frame": 6,
  "EvaluationStartUtc": "2024-01-01T00:00:00Z",
  "EvaluationEndUtc": "2025-12-31T23:59:59Z",
  "InitialCapital": 100000.0,
  "CommissionFlat": 0.0,
  "CommissionPerUnit": 0.005,
  "SlippageRatio": 0.0005,
  "SizingModel": 1,
  "SizingParameter": 0.20,
  "InitialMarginRatio": 0.50,
  "MaintenanceMarginRatio": 0.25,
  "LiquidationPenaltyRatio": 0.05,
  "BootstrapSeed": 42,
  "BootstrapIterations": 1000,
  "SelectedIndicators": [
    {
      "Key": "fast_sma",
      "Type": 0,
      "Frame": null,
      "Parameters": {
        "$type": "CoreSmaParameter",
        "Period": 20
      }
    },
    {
      "Key": "slow_sma",
      "Type": 0,
      "Frame": null,
      "Parameters": {
        "$type": "CoreSmaParameter",
        "Period": 50
      }
    }
  ],
  "ConditionEntries": [
    {
      "Left": {
        "IndicatorType": 0,
        "OutputName": "Main",
        "Offset": 0,
        "Parameters": {
          "$type": "CoreSmaParameter",
          "Period": 20
        }
      },
      "Operator": 0,
      "TargetMode": 1,
      "RightNumericValue": 0.0,
      "Right": {
        "IndicatorType": 0,
        "OutputName": "Main",
        "Offset": 0,
        "Parameters": {
          "$type": "CoreSmaParameter",
          "Period": 50
        }
      },
      "LogicalOperator": 0,
      "Role": 0,
      "Position": 0
    }
  ],
  "RiskManagement": {
    "StopLossPercent": 0.03,
    "TakeProfitPercent": 0.10
  },
  "ConditionTree": null
}
```

### Example B: Schema Version 2 (Hierarchical Condition Tree)
```json
{
  "SchemaVersion": 2,
  "ExecutionModel": 0,
  "Symbol": "AAPL",
  "Frame": 6,
  "EvaluationStartUtc": "2024-01-01T00:00:00Z",
  "EvaluationEndUtc": "2025-12-31T23:59:59Z",
  "InitialCapital": 100000.0,
  "CommissionFlat": 0.0,
  "CommissionPerUnit": 0.005,
  "SlippageRatio": 0.0005,
  "SizingModel": 1,
  "SizingParameter": 0.20,
  "InitialMarginRatio": 0.50,
  "MaintenanceMarginRatio": 0.25,
  "LiquidationPenaltyRatio": 0.05,
  "BootstrapSeed": 42,
  "BootstrapIterations": 1000,
  "SelectedIndicators": [
    {
      "Key": "rsi_entry",
      "Type": 34,
      "Frame": null,
      "Parameters": {
        "$type": "CoreRsiParameter",
        "Period": 14
      }
    }
  ],
  "ConditionEntries": [],
  "RiskManagement": {
    "StopLossPercent": 0.025,
    "TakeProfitPercent": 0.08
  },
  "ConditionTree": {
    "EntryLong": {
      "Kind": 1,
      "Operator": 0,
      "Children": [
        {
          "Kind": 0,
          "Comparison": {
            "Left": {
              "IndicatorType": 34,
              "OutputName": "Main",
              "Offset": 0,
              "Parameters": {
                "$type": "CoreRsiParameter",
                "Period": 14
              }
            },
            "Operator": 2,
            "TargetMode": 0,
            "RightNumericValue": 30.0,
            "Right": null
          },
          "Children": []
        }
      ]
    },
    "EntryShort": { "Kind": 1, "Operator": 0, "Children": [] },
    "ExitLong": { "Kind": 1, "Operator": 0, "Children": [] },
    "ExitShort": { "Kind": 1, "Operator": 0, "Children": [] },
    "ReverseLong": { "Kind": 1, "Operator": 0, "Children": [] },
    "ReverseShort": { "Kind": 1, "Operator": 0, "Children": [] }
  }
}
```

---

## 4. Backtest Default Settings (`Data/Backtest/backtest_default_settings.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/Backtest/backtest_default_settings.json` |
| **Path Resolver** | `PathDiscovery.ResolveBacktestDefaultSettingsPath("backtest_default_settings.json")` |
| **Managing Class** | `StockAnalyzer.Core.Services.Backtest.Configuration.BacktestDefaultSettingsManager` |
| **Model Class** | `StockAnalyzer.Core.Models.Backtest.Configuration.BacktestDefaultSettings` |
| **Schema Version** | `2` (`CurrentSchemaVersion = 2`) |

Stores user preferences for initial defaults when opening the Backtest setup window.

```json
{
  "SchemaVersion": 2,
  "DefaultTimeFrame": 6,
  "EndDateMode": 0,
  "FixedEndDateUtc": null,
  "StartDateMode": 0,
  "StartDateRelativeYears": 2,
  "FixedStartDateUtc": null,
  "InitialCapital": 100000.0,
  "CommissionFlat": 1.0,
  "CommissionPerUnit": 0.005,
  "SlippageRatio": 0.0005,
  "SizingModel": 1,
  "SizingParameter": 0.20,
  "InitialMarginRatio": 0.50,
  "MaintenanceMarginRatio": 0.25,
  "LiquidationPenaltyRatio": 0.05,
  "AnnualRiskFreeRate": 0.04,
  "AnnualMAR": 0.0,
  "AnnualPeriodsByFrame": {
    "D1": 252,
    "W1": 52,
    "MN1": 12
  },
  "BootstrapSeed": 42,
  "BootstrapIterations": 1000
}
```

| Property | Type | Description |
|---|---|---|
| `SchemaVersion` | `integer` | Schema version (`2`). |
| `DefaultTimeFrame` | `integer`, enum `TimeFrame` | Default timeframe (`6` = `D1`, `7` = `W1`, `8` = `MN1`). |
| `EndDateMode` | `integer`, enum `BacktestEndDateMode` | `0` = `Today`, `1` = `FixedDate`. |
| `FixedEndDateUtc` | `string (ISO 8601)?` | Fixed end date in UTC if `EndDateMode == 1`. |
| `StartDateMode` | `integer`, enum `BacktestStartDateMode`| `0` = `RelativeYears`, `1` = `FixedDate`. |
| `StartDateRelativeYears` | `integer` | Lookback years when `StartDateMode == 0` (range `1..50`). |
| `FixedStartDateUtc` | `string (ISO 8601)?` | Fixed start date in UTC if `StartDateMode == 1`. |
| `InitialCapital` | `decimal` | Default starting cash balance (`> 0`). |
| `CommissionFlat` | `decimal` | Default flat fee per order (`>= 0`). |
| `CommissionPerUnit` | `decimal` | Default fee per traded share (`>= 0`). |
| `SlippageRatio` | `decimal` | Default proportional slippage (`0 <= s < 1`). |
| `SizingModel` | `integer`, enum `PositionSizingModel` | `0` = `FixedQuantity`, `1` = `PercentOfEquity`. |
| `SizingParameter` | `decimal` | Parameter for sizing model (`>= 1` shares or `0 < r <= 1` ratio). |
| `InitialMarginRatio` | `decimal` | Default margin requirement (`0 < r <= 1`). |
| `MaintenanceMarginRatio`| `decimal` | Default maintenance margin ratio (`> 0` and `< InitialMarginRatio`). |
| `LiquidationPenaltyRatio`| `decimal` | Default liquidation penalty (`0 <= p < 1`). |
| `AnnualRiskFreeRate` | `decimal` | Annual risk-free rate (`> -1`). |
| `AnnualMAR` | `decimal` | Annual minimum acceptable return (`> -1`). |
| `AnnualPeriodsByFrame` | `object` | Periods per year keyed by timeframe name or integer (`"D1": 252, "W1": 52, "MN1": 12`). |
| `BootstrapSeed` | `integer` | Random seed for bootstrap resampling. |
| `BootstrapIterations` | `integer` | Number of bootstrap resampling iterations. |

---

## 5. Backtest Grid Columns Settings (`Data/Backtest/backtest_column_settings.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/Backtest/backtest_column_settings.json` |
| **Path Resolver** | `PathDiscovery.ResolveBacktestDefaultSettingsPath("backtest_column_settings.json")` |
| **Managing Class** | `StockAnalyzer.Core.Services.Backtest.Configuration.BacktestColumnSettingsManager` |
| **Model Class** | `StockAnalyzer.Core.Models.Backtest.Configuration.BacktestColumnSettings` |
| **Schema Version** | `1` |

Customizes visible columns and order for Trade History and Summary Metrics grids in the Backtest results view.

```json
{
  "SchemaVersion": 1,
  "TradeVisibleColumns": [
    "TradeId",
    "Side",
    "EntryTime",
    "EntryPrice",
    "ExitTime",
    "ExitPrice",
    "Quantity",
    "ClosedNet",
    "PnLPercent",
    "HoldingBars"
  ],
  "MetricVisibleNames": [
    "TotalPnL",
    "TotalReturn",
    "CAGR",
    "MaxDrawdown",
    "AnnualizedSharpe",
    "WinRate",
    "ProfitFactor",
    "ClosedTrades"
  ]
}
```

### Recognized Column and Metric Identifiers
- **`TradeVisibleColumns`**: `"TradeId"`, `"Side"`, `"EntryTime"`, `"EntryPrice"`, `"ExitTime"`, `"ExitPrice"`, `"Quantity"`, `"EntryValue"`, `"ExitValue"`, `"Commission"`, `"Slippage"`, `"ClosedNet"`, `"PnLPercent"`, `"MfePrice"`, `"MfePercent"`, `"MaePrice"`, `"MaePercent"`, `"HoldingBars"`, `"Duration"`, `"EntrySignal"`, `"ExitReason"`, `"ForcedLiquidation"`.
- **`MetricVisibleNames`**: `"TotalPnL"`, `"TotalReturn"`, `"CAGR"`, `"GrossProfit"`, `"GrossLoss"`, `"MaxDrawdown"`, `"MaxDrawdownAmount"`, `"UlcerIndex"`, `"MaxDepthDrawdownDuration"`, `"LongestDrawdownDuration"`, `"AnnualizedSharpe"`, `"AnnualizedSharpeAutocorrelationAdjusted"`, `"AnnualizedSortino"`, `"AnnualizedSortinoAutocorrelationAdjusted"`, `"CalmarFullPeriod"`, `"RecoveryFactor"`, `"ClosedTrades"`, `"WinRate"`, `"ProfitFactor"`, `"ExpectedPayoff"`, `"PayoffRatio"`, `"AverageWin"`, `"AverageLoss"`, `"LargestWin"`, `"LargestLoss"`, `"AverageHoldingPeriod"`, `"MaxConsecutiveWins"`, `"MaxConsecutiveLosses"`, `"TimeInMarket"`, `"Exposure"`, `"BarSharpe"`, `"BarSortino"`, `"ExposureAdjustedCAGR"`, `"SQN"`.

---

## 6. Backtest Report Defaults (`Data/Backtest/backtest_report_settings.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/Backtest/backtest_report_settings.json` |
| **Path Resolver** | `PathDiscovery.ResolveBacktestReportSettingsPath("backtest_report_settings.json")` |
| **Managing Class** | `StockAnalyzer.Core.Services.Backtest.Reporting.BacktestReportSettingsManager` |
| **Model Class** | `StockAnalyzer.Core.Services.Backtest.Reporting.BacktestReportDefaults` |
| **Schema Version** | `1` |

```json
{
  "SchemaVersion": 1,
  "AnnualRiskFreeRate": 0.04,
  "AnnualMAR": 0.0,
  "AnnualPeriodsByFrame": {
    "D1": 252,
    "W1": 52,
    "MN1": 12
  }
}
```
