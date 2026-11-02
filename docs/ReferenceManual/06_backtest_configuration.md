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
- **`ExecutionModel`** (`integer` or `string`, enum `ExecutionModel`): Simulation mode (`0` = `Legacy`, `1` = `Vectorized`, `2` = `EventDriven`).
- **`Symbol`** (`string`, required): Ticker symbol evaluated (e.g. `"AAPL"`).
- **`Frame`** (`integer` or `string`, enum `TimeFrame`): Candle timeframe interval (`1` / `"D1"`, `7` / `"W1"`, `8` / `"MN1"`).
- **`EvaluationStartUtc`** / **`EvaluationEndUtc`** (`string (ISO 8601)`): Evaluation start and end timestamps. MUST have zero UTC offset (`Z` suffix) and `EvaluationStartUtc <= EvaluationEndUtc`.

### Group B: Capital & Cost Model
- **`InitialCapital`** (`decimal`, e.g. `100000.0`): Starting cash balance.
- **`CommissionFlat`** (`decimal`, e.g. `0.0`): Fixed fee per order.
- **`CommissionPerUnit`** (`decimal`, e.g. `0.005`): Fee per traded share.
- **`SlippageRatio`** (`decimal`, e.g. `0.0005` [0.05%]): Proportional execution slippage.
- **`SizingModel`** (`integer` or `string`, enum `PositionSizingModel`): `0` = `FixedQuantity`, `1` = `FixedCash`, `2` = `PercentOfEquity`, `3` = `VolatilityTarget`.
- **`SizingParameter`** (`decimal`): Numerical parameter for chosen sizing model (e.g. `0.20` for 20% equity).

### Group C: Margin Model
- **`InitialMarginRatio`** (`decimal`, e.g. `0.50` [2x leverage]): Required margin to open a position.
- **`MaintenanceMarginRatio`** (`decimal`, e.g. `0.25`): Liquidation threshold ratio.
- **`LiquidationPenaltyRatio`** (`decimal`, e.g. `0.05`): Penalty ratio assessed on liquidation.

### Group D: Statistical Resampling
- **`BootstrapSeed`** (`integer`, default `42`): Seed for Sortino/Sharpe bootstrap confidence intervals.
- **`BootstrapIterations`** (`integer`, default `1000`): Resampling iterations count.

### Tab 2: Selected Indicators (`BacktestSelectedIndicatorDto`)

Stored in `SelectedIndicators`:
- **`Key`** (`string`, required): Lookup handle referenced by strategy rules (e.g. `"fast_sma"`).
- **`Type`** (`integer` or `string`, enum `IndicatorType`): Registered indicator type (e.g. `0` or `"SMA"`).
- **`Frame`** (`integer?` or `string?`, optional `TimeFrame?`): Indicator timeframe override. `null` inherits run's `Frame`.
- **`Parameters`** (`object?`, polymorphic with `"$type"`): Concrete indicator parameter object (e.g. `"$type": "CoreSmaParameter"`, `"Period": 20`). See `09_indicator_catalog.md`.

### Comparison Condition Side (`BacktestConditionSideDto`)

Specifies one operand (Left or Right) of a condition:
- **`IndicatorType`** (`integer` or `string`, enum `IndicatorType`): Target indicator type.
- **`OutputName`** (`string`, default `"Main"`): Output series name (e.g. `"Main"`, `"Signal"`, `"UpperBand"`).
- **`Offset`** (`integer`, default `0`): Historical bar lookback offset ($[-n]$ bars).
- **`Frame`** (`integer?` or `string?`): Timeframe override (`null` = run's `Frame`).
- **`PriceSource`** (`integer?`): Price extraction source (`0`=Close, `1`=Open, `2`=High, `3`=Low).
- **`Parameters`** (`object?`, polymorphic with `"$type"`): Concrete indicator parameter object.

### Flat Condition Entry (`BacktestConditionEntryDto`)

Stored in `ConditionEntries` (Schema 1):
- **`Left`** (`BacktestConditionSideDto`, required): Left-hand side operand.
- **`Operator`** (`integer` or `string`, enum `ComparisonOperator`):
  `0` = `GreaterThan` ($>$), `1` = `LessThan` ($<$), `2` = `GreaterThanOrEqual` ($\ge$), `3` = `LessThanOrEqual` ($\le$), `4` = `Equal` ($==$), `5` = `NotEqual` ($!=$), `6` = `CrossAbove`, `7` = `CrossBelow`.
- **`TargetMode`** (`integer` or `string`, enum `RightHandTargetMode`): `0` = `NumericValue`, `1` = `Indicator`.
- **`RightNumericValue`** (`decimal`): Threshold constant used when `TargetMode` is `NumericValue`.
- **`Right`** (`BacktestConditionSideDto?`): Right-hand side operand used when `TargetMode` is `Indicator`.
- **`LogicalOperator`** (`integer` or `string`, enum `LogicalOperator`): Combines with next entry (`0` = `And`, `1` = `Or`).
- **`Role`** (`integer` or `string`, enum `BacktestConditionRole`): `0` = `Entry`, `1` = `Exit`, `2` = `Both`, `3` = `Filter`.
- **`Position`** (`integer` or `string`, enum `TradeSide`): Target trade side (`0` = `Long`, `1` = `Short`).

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
- **`StopLossPercent`** (`decimal?`, e.g. `3.0`): Maximum loss percentage from entry price before stop-loss liquidation.
- **`TakeProfitPercent`** (`decimal?`, e.g. `10.0`): Profit target percentage from entry price before take-profit liquidation.
*(Note: Trailing stop percentage is not part of this configuration model).*

---

## 3. Reference Examples

### Example A: Schema Version 1 (Flat Condition Entries)
```json
{
  "SchemaVersion": 1,
  "ExecutionModel": 0,
  "Symbol": "AAPL",
  "Frame": 1,
  "EvaluationStartUtc": "2024-01-01T00:00:00Z",
  "EvaluationEndUtc": "2025-12-31T23:59:59Z",
  "InitialCapital": 100000.0,
  "CommissionFlat": 0.0,
  "CommissionPerUnit": 0.005,
  "SlippageRatio": 0.0005,
  "SizingModel": 2,
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
      "Operator": 6,
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
    "StopLossPercent": 3.0,
    "TakeProfitPercent": 10.0
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
  "Frame": 1,
  "EvaluationStartUtc": "2024-01-01T00:00:00Z",
  "EvaluationEndUtc": "2025-12-31T23:59:59Z",
  "InitialCapital": 100000.0,
  "CommissionFlat": 0.0,
  "CommissionPerUnit": 0.005,
  "SlippageRatio": 0.0005,
  "SizingModel": 2,
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
    "StopLossPercent": 2.5,
    "TakeProfitPercent": 8.0
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
            "Operator": 1,
            "TargetMode": 0,
            "RightNumericValue": 30.0,
            "Right": null,
            "LogicalOperator": 0,
            "Role": 0,
            "Position": 0
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

Stores defaults populated into the Backtest UI when launching new strategy runs.

```json
{
  "DefaultInitialCapital": 100000.0,
  "DefaultCommissionFlat": 0.0,
  "DefaultCommissionPerUnit": 0.005,
  "DefaultSlippageRatio": 0.0005,
  "DefaultSizingModel": 2,
  "DefaultSizingParameter": 0.20,
  "DefaultInitialMarginRatio": 0.50,
  "DefaultMaintenanceMarginRatio": 0.25,
  "DefaultLiquidationPenaltyRatio": 0.05
}
```
