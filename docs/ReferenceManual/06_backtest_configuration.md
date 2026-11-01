# 06. Backtest Configuration & Reporting Settings

This document specifies configuration files governing the Backtest execution engine, strategy runner parameters, result column customization, and performance metrics aggregation.

---

## 1. Saved Backtest Execution Configuration (`backtest_configuration.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/Config/backtest_configuration.json` |
| **Path Resolver** | `PathDiscovery.ResolveConfigPath("backtest_configuration.json")` |
| **Managing Class** | `StockAnalyzer.Core.Services.Backtest.Configuration.BacktestConfigurationManager` |
| **Model Class** | `StockAnalyzer.Core.Models.Backtest.Configuration.BacktestConfigurationDto` |
| **Schema Version** | `1` (legacy flat conditions) or `2` (hierarchical condition trees) |

Captures the complete state of a configured backtest run (symbol, timeframe, dates, capital, commission, margin, indicators, and entry/exit strategy rules).

### Field Specification

#### Group A: Target Data & Evaluation Period
- **`ExecutionModel`** (`string (Enum)`): Backtest simulation engine (`"Legacy"`, `"Vectorized"`, `"EventDriven"`).
- **`Symbol`** (`string`): Ticker symbol being evaluated (e.g. `"AAPL"`).
- **`Frame`** (`string (Enum)`): Candle interval (`"D1"`, `"W1"`, `"MN1"`).
- **`EvaluationStartUtc`** / **`EvaluationEndUtc`** (`string (ISO 8601)`): Backtest evaluation window.

#### Group B: Capital & Cost Model
- **`InitialCapital`** (`decimal`, e.g. `100000.0`): Starting cash balance.
- **`CommissionFlat`** (`decimal`, e.g. `0.0`): Fixed fee per executed order.
- **`CommissionPerUnit`** (`decimal`, e.g. `0.005`): Per-share or per-contract fee.
- **`SlippageRatio`** (`decimal`, e.g. `0.0005` [0.05%]): Execution slippage model.
- **`SizingModel`** (`string (Enum)`): Position sizing algorithm (`"FixedQuantity"`, `"FixedCash"`, `"PercentOfEquity"`, `"VolatilityTarget"`).
- **`SizingParameter`** (`decimal`): Numerical parameter for chosen sizing model (e.g. `0.10` for 10% equity).

#### Group C: Margin Model
- **`InitialMarginRatio`** (`decimal`, e.g. `0.50` [2x leverage]): Required margin to open a position.
- **`MaintenanceMarginRatio`** (`decimal`, e.g. `0.25`): Liquidation margin threshold.
- **`LiquidationPenaltyRatio`** (`decimal`, e.g. `0.05`): Margin call liquidation penalty.

#### Group D: Statistical Resampling
- **`BootstrapSeed`** (`integer`, default `42`): Random seed for Sortino/Sharpe bootstrap confidence intervals.
- **`BootstrapIterations`** (`integer`, default `1000`): Resampling iterations.

#### Strategy Conditions & Indicators
- **`SelectedIndicators`** (`array of BacktestSelectedIndicatorDto`): Indicators referenced by trading rules.
- **`ConditionEntries`** (`array of BacktestConditionEntryDto`): Flat condition rule list.
- **`ConditionTree`** (`BacktestConditionTreeDto`): Nested boolean expression tree (Schema 2).

### Reference Example
```json
{
  "SchemaVersion": 1,
  "ExecutionModel": "Legacy",
  "Symbol": "AAPL",
  "Frame": "D1",
  "EvaluationStartUtc": "2024-01-01T00:00:00Z",
  "EvaluationEndUtc": "2025-12-31T23:59:59Z",
  "InitialCapital": 100000.0,
  "CommissionFlat": 0.0,
  "CommissionPerUnit": 0.005,
  "SlippageRatio": 0.0005,
  "SizingModel": "PercentOfEquity",
  "SizingParameter": 0.20,
  "InitialMarginRatio": 0.50,
  "MaintenanceMarginRatio": 0.25,
  "LiquidationPenaltyRatio": 0.05,
  "BootstrapSeed": 42,
  "BootstrapIterations": 1000,
  "SelectedIndicators": [
    {
      "InstanceId": "e1f2a3b4-5678-90ab-cdef-1234567890ab",
      "Type": "SimpleMovingAverage",
      "Parameters": { "Period": 50 }
    },
    {
      "InstanceId": "f2a3b4c5-6789-01bc-def1-234567890abc",
      "Type": "SimpleMovingAverage",
      "Parameters": { "Period": 200 }
    }
  ],
  "ConditionEntries": [
    {
      "EntryType": "EntryLong",
      "LeftIndicatorId": "e1f2a3b4-5678-90ab-cdef-1234567890ab",
      "Operator": "CrossesAbove",
      "RightIndicatorId": "f2a3b4c5-6789-01bc-def1-234567890abc"
    }
  ]
}
```

---

## 2. Backtest Default Settings (`Data/Backtest/backtest_default_settings.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/Backtest/backtest_default_settings.json` |
| **Path Resolver** | `PathDiscovery.ResolveBacktestDefaultSettingsPath("backtest_default_settings.json")` |
| **Managing Class** | `StockAnalyzer.Core.Services.Backtest.Configuration.BacktestDefaultSettingsManager` |
| **Schema Version** | `2` (`CurrentSchemaVersion = 2`) |

Stores user preferences for initial defaults when opening the Backtest setup window.

```json
{
  "SchemaVersion": 2,
  "DefaultTimeFrame": "D1",
  "EndDateMode": "Today",
  "FixedEndDateUtc": null,
  "StartDateMode": "RelativeYears",
  "StartDateRelativeYears": 2,
  "FixedStartDateUtc": null,
  "InitialCapital": 100000.0,
  "CommissionFlat": 1.0,
  "CommissionPerUnit": 0.005,
  "SlippageRatio": 0.0005,
  "SizingModel": "FixedCash",
  "SizingParameter": 10000.0,
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

---

## 3. Backtest Grid Columns Settings (`Data/Backtest/backtest_column_settings.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/Backtest/backtest_column_settings.json` |
| **Path Resolver** | `PathDiscovery.ResolveBacktestDefaultSettingsPath("backtest_column_settings.json")` |
| **Managing Class** | `StockAnalyzer.Core.Services.Backtest.Configuration.BacktestColumnSettingsManager` |
| **Schema Version** | `1` |

Customizes visible columns and order for Trade History and Summary Metrics grids.

```json
{
  "SchemaVersion": 1,
  "TradeVisibleColumns": [
    "TradeId",
    "Side",
    "EntryDate",
    "EntryPrice",
    "ExitDate",
    "ExitPrice",
    "Quantity",
    "PnL",
    "ReturnPercent",
    "HoldingBars"
  ],
  "MetricVisibleNames": [
    "TotalReturnPercent",
    "AnnualizedReturn",
    "SharpeRatio",
    "SortinoRatio",
    "MaxDrawdownPercent",
    "WinRate",
    "ProfitFactor",
    "TotalTrades"
  ]
}
```

---

## 4. Backtest Report Defaults (`Data/Backtest/backtest_report_settings.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/Backtest/backtest_report_settings.json` |
| **Path Resolver** | `PathDiscovery.ResolveBacktestReportSettingsPath("backtest_report_settings.json")` |
| **Managing Class** | `StockAnalyzer.Core.Services.Backtest.Reporting.BacktestReportSettingsManager` |
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
