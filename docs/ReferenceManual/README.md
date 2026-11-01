# StockAnalyzer Configuration & Template Reference Manual

## 1. Overview
This directory contains the definitive reference manuals and structural schemas for all configuration files, user settings, and reusable domain templates used by **StockAnalyzer** (`StockAnalyzer.Avalonia` and `StockAnalyzer.Core`).

This documentation is designed to enable users and AI agents to directly inspect, author, validate, and edit StockAnalyzer configuration files without relying on UI dialogs or internal APIs.

---

## 2. Directory Architecture & Path Resolution

StockAnalyzer resolves all configuration and data paths dynamically via the Single Source of Truth (SSoT) class `StockAnalyzer.Core.Common.PathDiscovery`.

### Canonical Storage Roots

| Functional Category | Canonical Relative Path | `PathDiscovery` Resolver | Typical File Formats |
|---|---|---|---|
| **Global Application Configuration** | `<AppRoot>/appsettings.json` | `AppContext.BaseDirectory` | JSON |
| **User Settings & State** | `Data/Config/` | `PathDiscovery.ResolveConfigPath(fileName)` | JSON |
| **Domain Templates** | `Data/Templates/{TemplateType}/` | `PathDiscovery.ResolveTemplatePath(type, id)` | JSON (`{guid}.json`) |
| **Backtest Configurations & Defaults** | `Data/Backtest/` | `PathDiscovery.ResolveBacktestDefaultSettingsPath(...)` | JSON |
| **Backtest Exported Reports** | `Data/Backtest/Reports/` | `PathDiscovery.ResolveBacktestReportExportPath(...)` | JSON |
| **Indicator Default Parameters** | `Data/IndicatorDefaults/` | `PathDiscovery.ResolveIndicatorDefaultsPath(...)` | JSON |
| **Multi-Stage Source Indicators** | `Data/SourceIndicators/` | `PathDiscovery.ResolveSourceIndicatorsPath(...)` | JSON |
| **Dynamic Period Drivers** | `Data/DynamicPeriodDrivers/` | `PathDiscovery.ResolveDynamicPeriodDriversPath(...)` | JSON |
| **Chart Drawing Documents** | `Data/Drawings/` | `PathDiscovery.ResolveDataPath(null, "Data/Drawings")` | JSON (`{ticker}.{timeframe}.json`) |
| **User Portfolios & Ledgers** | `Data/Portfolios/` | `PathDiscovery.ResolvePortfolioPath(...)` | Parquet |
| **Ticker Notes & Attachments** | `Data/Notes/` | `NoteDatabaseConnectionManager` / `AttachmentRepository` | SQLite (`notes.db`), Images |
| **AI Prediction Models & Cache** | `Data/Models/` | `PathDiscovery.ResolvePredictionModelPath(...)` | ONNX, JSON |
| **Prediction Monitoring & Drift** | `Data/PredictionMonitoring/` | `SqliteMonitoringStore` / `PredictionMonitoringPaths` | SQLite (`monitoring.db`), JSON |
| **Market Data Time Series** | `Data/Daily/`, `Weekly/`, `Monthly/` | `PathDiscovery.ResolveDataPath(null, "Data/{Frame}")` | Parquet (`{ticker}.parquet`) |

### Three-Tier Directory Fallback
When running on read-only filesystems or unprivileged environments, `PathDiscovery` degrades safely through the following priority tiers:
1. **Target Preferred Path**: e.g., `<ProjectRoot>/Data/Config/`
2. **System Temp Directory**: `%TEMP%/StockAnalyzer/<SubDir>`
3. **AppDomain Base Directory**: `<BaseDirectory>/<SubDir>`

---

## 3. Resilience & Persistence Guarantees

Every user settings manager implements strict atomic persistence semantics via `StockAnalyzer.Core.Common.AtomicJsonFile`:
- **Atomic Replace**: Data is first written to a temporary sibling file (`<file>.tmp`), flushed to disk, verified via deserialization round-trip, and then atomically swapped into place via `File.Replace` (or `File.Move`).
- **Corruption Quarantine**: If an on-disk JSON file cannot be deserialized due to syntax corruption or invalid schema, the system never overwrites it with blank defaults. Instead, it creates a diagnostic copy (`<file>.corrupted`) and preserves default values in memory.
- **Backup Retention**: Previous working generations are preserved as `<file>.bak` where supported.
- **Concurrency Serialization**: Writes are guarded by dedicated `SemaphoreSlim(1, 1)` gates per path to eliminate overlapping I/O races.

---

## 4. Documentation Index

The specification is divided into dedicated, topic-specific documents:

| Document | Focus & Target Files |
|---|---|
| [**01. Global Appsettings**](01_appsettings_configuration.md) | `appsettings.json` runtime infrastructure, limits, and system parameters |
| [**02. Workspace Configuration**](02_workspace_configuration.md) | `Data/Config/workspace.json` window layout, active charts, and watchlists |
| [**03. Chart & Visual Settings**](03_chart_and_visual_settings.md) | `user_chart_settings.json`, `user_theme.json`, `user_font_settings.json`, `user_notes_settings.json` |
| [**04. Ticker & Screener Settings**](04_ticker_and_screener_settings.md) | `user_tickers_settings.json`, `scatter_metrics.json` |
| [**05. AI Prediction & ML Settings**](05_ai_prediction_and_ml_settings.md) | `user_prediction_settings.json`, `user_python_settings.json`, `user_ensemble_settings.json`, `user_training_resource_settings.json`, `user_rl_artifact_settings.json` |
| [**06. Backtest Configuration**](06_backtest_configuration.md) | `backtest_configuration.json`, `backtest_default_settings.json`, `backtest_column_settings.json`, `backtest_report_settings.json` |
| [**07. Indicator Defaults & Drivers**](07_indicator_defaults_and_drivers.md) | `user_indicator_defaults.json`, `source_indicators.json`, `dynamic_period_drivers.json` |
| [**08. Domain Templates Schema**](08_templates_reference.md) | `Data/Templates/{TemplateType}/{guid}.json` (Indicator, Column, Filter, Screener, Theme, Feature, BacktestCondition) |
