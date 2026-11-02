# 01. Global Application Configuration (`appsettings.json`)

## 1. File Specification

| Attribute | Detail |
|---|---|
| **File Location** | `<AppRoot>/appsettings.json` (shipped default: `StockAnalyzer.Avalonia/appsettings.example.json`) |
| **Loading Mechanism** | `Microsoft.Extensions.Configuration` / `IConfiguration` via `App.axaml.cs` |
| **Bound SSoT Classes** | `StockAnalyzer.Core.Models.Settings.*` (registered in `StockAnalyzerOptionsServiceCollectionExtensions.cs`) |
| **Serialization Format** | JSON (UTF-8) |
| **Hot Reload** | `reloadOnChange: false` (Application restart required for updates) |

---

## 2. Configuration Sections

### `ThemeSettings`
Global UI and default chart coloring palette.
- **`ChartBackground`** (`string`, Hex `#RRGGBB` or `#AARRGGBB`): Default background color of chart canvases (e.g. `"#FAEABB"`).
- **`GridLineColor`** (`string`, Hex): Chart coordinate grid line color (e.g. `"#D3D3D3"`).
- **`AxisTextColor`** (`string`, Hex): Price and date axis label text color (e.g. `"#000000"`).
- **`CrosshairColor`** (`string`, Hex): Interactive crosshair guide color (e.g. `"#808080"`).
- **`BullishColor`** (`string`, Hex): Default upward/bullish bar color (e.g. `"#FF0000"`).
- **`BearishColor`** (`string`, Hex): Default downward/bearish bar color (e.g. `"#008000"`).
- **`NeutralColor`** (`string`, Hex): Neutral/unchanged bar color (e.g. `"#808080"`).
- **`VolumeUpColor`** (`string`, Hex): Upward volume bar color with alpha (e.g. `"#80FF0000"`).
- **`VolumeDownColor`** (`string`, Hex): Downward volume bar color with alpha (e.g. `"#80008000"`).
- **`RulerFillColor`** (`string`, Hex): Chart measurement ruler area fill (e.g. `"#1E0000FF"`).
- **`RulerStrokeColor`** (`string`, Hex): Chart measurement ruler border stroke (e.g. `"#0000FF"`).

### `Chart`
- **`DefaultSymbol`** (`string`): Fallback symbol loaded if no active ticker exists in `workspace.json` (e.g. `""` or `"AAPL"`).

### `Layout`
Dock and tab panel limits (`LayoutSettings`).
- **`MaxPanelTabs`** (`integer`, default `16`): Maximum allowed open tabs per dock panel.
- **`MaxTabReorderDistance`** (`integer`, default `100`): Maximum pixel distance threshold for tab drag-and-drop reordering.

### `Prediction`
AI inference and model runtime parameters (`PredictionSettings`).
- **`ModelPath`** (`string`, default `"trend_predictor.onnx"`): Relative or absolute path to ONNX model.
- **`WindowSize`** (`integer`, default `60`): Number of historical lookback bars required for model input.
- **`FeatureMode`** (`string`, enum: `"OhlcvMinMax"`, `"ComposedFeatures"`): Input tensor normalization mode.
- **`ConfidenceThreshold`** (`double`, default `0.5`, range `[0.0, 1.0]`): Minimum confidence for signal triggers.
- **`InputNodeName`** (`string?`, default `null`): Explicit ONNX input tensor name (null auto-detects first tensor).
- **`OutputNodeName`** (`string?`, default `null`): Explicit ONNX output tensor name (null auto-detects).
- **`ClassLabels`** (`array of string`): Output classification labels (default `["Up", "Down", "Neutral"]`).
- **`RetryMaxAttempts`** (`integer`, default `3`): Max attempts for model inference retries.
- **`RetryBaseDelayMs`** (`integer`, default `50`): Exponential backoff base delay in milliseconds.
- **`RetryMaxDelayMs`** (`integer`, default `500`): Exponential backoff max delay in milliseconds.

### `PredictionMonitoring`
Health checking and audit database settings (`PredictionMonitoringSettings`).
- **`Backend`** (`string`, enum: `"Json"`, `"Sqlite"`): Active storage backend (`"Sqlite"` selects `monitoring.db`).
- **`BusyTimeoutSeconds`** (`integer`, default `5`): SQLite busy handler timeout.
- **`OutboxPollIntervalSeconds`** (`integer`, default `60`): Notification outbox flush interval.
- **`SchedulerPollIntervalSeconds`** (`integer`, default `60`): Background retraining scheduler poll interval.
- **`LeaseRetryIntervalMilliseconds`** (`integer`, default `250`): Lock lease retry interval.
- **`HealthEvaluationIntervalSeconds`** (`integer`, default `60`): Model drift and health re-evaluation cadence.
- **`AttestedManifestMaxBytes`** (`integer`, default `67108864` [64 MiB]): Maximum allowed size for attested evidence manifests.
- **`AttestedWorkingCopyRetentionMinutes`** (`integer`, default `60`): Retention lifespan for working verification copies.
- **`AttestedSources`** (`array of AttestedSourceEnrollment`): Enrolled forecast symbols for producer-attested verification streams (empty `[]` disables attested monitoring). Each element contains:
  - `Symbol` (`string`, required): Forecast ticker symbol (e.g. `"AAPL"`).
  - `Timeframe` (`string (Enum)`: `"Daily"`, `"Weekly"`, `"Monthly"`, `"H1"`, `"H4"`): Evaluation interval.
  - `ProviderId` (`string`, required): Identifier of external forecast provider.
  - `InstrumentId` (`string`, required): Producer instrument ID (matching `<InstrumentId>.attest.json` sidecar).

### `Python`
Python IPC and virtual environment configuration (`PythonSettings`).
- **`PythonPath`** (`string?`, default `null`): Explicit path to `python.exe` (null triggers auto-discovery in virtual environments and PATH).
- **`MaxRetries`** (`integer`, default `3`): Maximum initialization retry attempts.
- **`BackoffMs`** (`integer`, default `1000`): Retry backoff in milliseconds.
- **`HealthCheckIntervalMs`** (`integer`, default `5000`): Keep-alive ping interval.
- **`PipeConnectPollIntervalMs`** (`integer`, default `100`): Named pipe connection poll delay.
- **`DisposeWaitMs`** (`integer`, default `1000`): Graceful process shutdown wait time.
- **`SyncTimeoutMinutes`** (`integer`, default `2`): Timeout for synchronous Python batch tasks.
- **`EssentialPackages`** (`array of string`): Mandatory packages verified on startup (e.g. `["setuptools", "wheel", "polars", "pandas", "yfinance", "pyarrow"]`).

### `ChartOptions`
Geometric rendering parameters and hit-test tolerances (`ChartOptions`).
- **`ChartMarginHorizontal`** (`double`, default `20`): Left/right chart canvas padding in DIPs.
- **`ChartMarginTop`** (`double`, default `35`): Top padding for price headers in DIPs.
- **`ChartMarginBottom`** (`double`, default `30`): Bottom padding for time axis in DIPs.
- **`LabelPadding`** (`double`, default `4`): Internal padding for price bubble labels.
- **`LabelHeight`** (`double`, default `18`): Height of axis price tags.
- **`ReverseWatchMargin`** (`double`, default `10`): Inset margin for reverse watch overlays.
- **`PointRadius`** (`double`, default `4`): Anchor handle visual radius.
- **`HoverPointRadius`** (`double`, default `6`): Expanded radius on cursor hover.
- **`HoverThresholdDistance`** (`double`, default `15`): Hit-testing pixel proximity distance.
- **`CurveWindowMargin`** (`double`, default `60`): Bounding window margin for polynomial spline fit curves.
- **`TimeAxisHeight`** (`double`, default `25`): Dedicated height for date-time ruler axis.

### `SmartScreener` & `Screener`
Filtering rules and limits (`ScreenerSettings`).
- **`ScreeningDataPath`** (`string`, default `"Data/Screening/latest_screening_data.json"`): Cached screening results file.
- **`DefaultSymbols`** (`array of string`): Default universe candidate symbols (e.g. `["AAPL", "MSFT", "GOOGL"]`).
- **`MaxFilterTreeDepth`** (`integer`, default `8`): Maximum nesting depth of boolean condition groups.
- **`MaxFilterTreeNodes`** (`integer`, default `64`): Maximum total nodes in a single screener query.
- **`MaxFilterGroupNameLength`** (`integer`, default `32`): Maximum characters for custom group titles.
- **`ConditionDragStartDistance`** (`double`, default `4`): Pointer drag threshold for condition reordering.
- **`MaxConditionOffset`** (`integer`, default `500`): Maximum allowed historical bar lookback offset ($[-n]$).

### `ScatterMetrics`
Scatter plot analysis limits (`ScatterMetricSettings`).
- **`MaxRegisteredMetrics`** (`integer`, default `32`): Maximum registered metric definitions.
- **`MaxOffset`** (`integer`, default `500`): Maximum bar offset allowed for scatter metrics.
- **`MaxParallelism`** (`integer`, default `10`): Maximum concurrent calculation tasks.
- **`InputDebounceMilliseconds`** (`integer`, default `300`): UI input debounce duration.

### `Tickers`
Ticker list autoplay and scope settings (`TickersSettings`).
- **`AutoPlayDefaultIntervalSeconds`** (`integer`, default `5`, range `[1, 3600]`): Default timer cadence for stepping through symbols.
- **`AutoPlayMaxIntervalSeconds`** (`integer`, default `3600`): Upper bound for autoplay cadence.
- **`AutoPlayDefaultStopAtListEnd`** (`boolean`, default `true`): If true, stops playback when reaching the final ticker.
- **`AutoPlayDefaultStopOnListChange`** (`boolean`, default `true`): If true, halts playback if the ticker filter/list changes.

### `PythonIpc` & `PythonPackageCheck`
Low-level process communication parameters.
- **`RequestTimeoutMs`** (`integer`, default `60000`): IPC command timeout.
- **`MaxResponseBytes`** (`integer`, default `16777216` [16 MiB]): Max pipe buffer read limit.
- **`MaxTimeoutRetries`** (`integer`, default `1`): Number of pipe reconnect attempts after a timeout.
- **`ImportProbeTimeoutSeconds`** (`integer`, default `60`): Timeout for package import validation script.
- **`UpdateCheckTimeoutSeconds`** (`integer`, default `5`): Timeout for checking PyPI updates.
- **`UpdateCheckRequestTimeoutSeconds`** (`integer`, default `3`): HTTP request timeout for update metadata.

### `Backtest`
Backtest validation rules and tree constraints (`BacktestSettings`).
- **`MaxConditionOffset`** (`integer`, default `500`): Max historical offset for condition indicators.
- **`MaxConditionTreeDepth`** (`integer`, default `8`): Nesting depth limit for entry/exit condition trees.
- **`MaxConditionTreeNodes`** (`integer`, default `64`): Maximum nodes per strategy tree.
- **`MaxConditionGroupNameLength`** (`integer`, default `32`): Group title character length cap.
- **`ConditionDragStartDistance`** (`double`, default `4`): Minimum pointer displacement for drag operations.

### `DrawingInteraction`
Undo/Redo memory and performance management (`DrawingInteractionSettings`).
- **`MaxHistoryEntriesPerContext`** (`integer`, default `128`): Max undo steps per chart window.
- **`MaxHistoryPayloadBytesPerContext`** (`integer`, default `67108864` [64 MiB]): Max memory quota for single context history.
- **`MaxSessionHistoryPayloadBytes`** (`integer`, default `268435456` [256 MiB]): Total global undo history budget.
- **`MaxStrokeSamples`** (`integer`, default `65536`): Maximum coordinate vertices for freehand/brush curves.

### `PatternRecognition` & `MarketStructure`
- **`PatternRecognition.MinWindow`** (`integer`, default `20`): Minimum candle window for pattern scan.
- **`PatternRecognition.MaxWindow`** (`integer`, default `60`): Maximum candle window for pattern scan.
- **`PatternRecognition.WindowStep`** (`integer`, default `5`): Window increment step.
- **`PatternRecognition.DefaultThreshold`** (`double`, default `0.5`): Minimum correlation / confidence score.
- **`MarketStructure.ZigzagThresholdPercent`** (`double`, default `5.0`): Minimum price change percentage for ZigZag pivot swings.

### `Infrastructure`
IPC and processing bounds (`InfrastructureSettings`).
- **`PipeName`** (`string`, default `"StockAnalyzerPipe"`): Windows named pipe / Unix domain socket name.
- **`PipeConnectionTimeoutMs`** (`integer`, default `5000`): Client pipe connection timeout.
- **`ScreenerMaxParallelism`** (`integer`, default `10`): Maximum parallel CPU workers for screening scans.

### `Localization`
- **`ResourcePath`** (`string`, default `"Resources/Locales"`): Relative folder containing `{locale}.json` translation files.

### `Resilience`
Polly-style circuit breaker rules for external services (`ResilienceSettings`).
- **`MinimumThroughput`** (`integer`, default `3`): Minimum calls required before evaluating failure ratio.
- **`FailureRatio`** (`double`, default `0.5`, range `[0.0, 1.0]`): Failure threshold to trip circuit.
- **`BreakDurationMs`** (`integer`, default `30000`): Duration circuit remains open before half-open probe.
- **`SamplingDurationMs`** (`integer`, default `60000`): Rolling evaluation window.

### `Logging`
Serilog file and console logging sinks.
- **`LogDirectory`** (`string`, default `"logs"`): Relative folder for rolling log files.
- **`MinimumLevel`** (`string`, default `"Information"`): Serilog level (`"Verbose"`, `"Debug"`, `"Information"`, `"Warning"`, `"Error"`, `"Fatal"`).

### `MarketDataSettings`
Canonical paths for time-series parquet data and symbol coverage.
- **`DailyDataPath`** (`string`, default `"Data/Daily"`): Folder holding daily candle parquet files.
- **`WeeklyDataPath`** (`string`, default `"Data/Weekly"`): Folder holding weekly candle parquet files.
- **`MonthlyDataPath`** (`string`, default `"Data/Monthly"`): Folder holding monthly candle parquet files.
- **`MetadataPath`** (`string`, default `"Data/Metadata"`): Folder holding strategy metadata (`{ticker}.meta.parquet`) and signal bundles.
- **`TickerListPath`** (`string`, default `"StockAnalyzer.Python/tickers.json"`): S&P 500 / universe ticker definitions.

### `RlPortfolio`
Reinforcement learning multi-asset portfolio training defaults (`RlPortfolioSettings`).
- **`MaxSymbols`** (`integer`, default `50`, max `100`): Maximum concurrent symbols in portfolio environment.
- **`HoldoutPercent`** (`integer`, default `25`, range `[10, 50]`): Out-of-sample evaluation split.
- **`MaxWeightPercent`** (`integer`, default `100`, range `[1, 100]`): Maximum allocation per asset.
- **`PartitionMode`** (`string`, enum: `"Ratio"` [0], `"BlockCount"` [1]): Dataset splitting strategy. `"Ratio"` uses 3:1:1 calendar split; `"BlockCount"` aligns contiguous blocks back from recent history.
- **`MinimumTrainBars`** (`integer`, default `192`): Minimum bars required for training segment.
- **`MinimumValidationBars`** (`integer`, default `192`): Minimum bars required for validation segment.
- **`MinimumTestBars`** (`integer`, default `192`): Minimum bars required for testing segment.

---

## 3. Cross-Field Constraints & Validation Invariants

When creating or modifying `appsettings.json`, the configuration validator checks the following relational constraints at startup:
- **`PythonPackageCheck`**: `UpdateCheckRequestTimeoutSeconds` MUST be strictly less than `UpdateCheckTimeoutSeconds` (`UpdateCheckRequestTimeoutSeconds < UpdateCheckTimeoutSeconds`).
- **`Backtest`**: `MaxConditionTreeNodes` MUST be greater than or equal to `MaxConditionTreeDepth` (`MaxConditionTreeNodes >= MaxConditionTreeDepth`).
- **`Screener`**: `MaxFilterTreeNodes` MUST be greater than or equal to `MaxFilterTreeDepth` (`MaxFilterTreeNodes >= MaxFilterTreeDepth`).
- **`PatternRecognition`**: `MaxWindow` MUST be strictly greater than `MinWindow` (`MaxWindow > MinWindow`), and `DefaultThreshold` MUST satisfy $0.0 < \text{DefaultThreshold} \le 1.0$.

---

## 4. Complete Reference Example

```json
{
  "ThemeSettings": {
    "ChartBackground": "#FAEABB",
    "GridLineColor": "#D3D3D3",
    "AxisTextColor": "#000000",
    "CrosshairColor": "#808080",
    "BullishColor": "#FF0000",
    "BearishColor": "#008000",
    "NeutralColor": "#808080",
    "VolumeUpColor": "#80FF0000",
    "VolumeDownColor": "#80008000",
    "RulerFillColor": "#1E0000FF",
    "RulerStrokeColor": "#0000FF"
  },
  "Chart": {
    "DefaultSymbol": "AAPL"
  },
  "Layout": {
    "MaxPanelTabs": 16,
    "MaxTabReorderDistance": 100
  },
  "Prediction": {
    "ModelPath": "trend_predictor.onnx",
    "WindowSize": 60,
    "FeatureMode": "OhlcvMinMax",
    "ConfidenceThreshold": 0.5,
    "InputNodeName": null,
    "OutputNodeName": null,
    "ClassLabels": [ "Up", "Down", "Neutral" ],
    "RetryMaxAttempts": 3,
    "RetryBaseDelayMs": 50,
    "RetryMaxDelayMs": 500
  },
  "PredictionMonitoring": {
    "Backend": "Sqlite",
    "BusyTimeoutSeconds": 5,
    "OutboxPollIntervalSeconds": 60,
    "SchedulerPollIntervalSeconds": 60,
    "LeaseRetryIntervalMilliseconds": 250,
    "HealthEvaluationIntervalSeconds": 60,
    "AttestedManifestMaxBytes": 67108864,
    "AttestedWorkingCopyRetentionMinutes": 60,
    "AttestedSources": [
      {
        "Symbol": "AAPL",
        "Timeframe": "Daily",
        "ProviderId": "provider_prod_1",
        "InstrumentId": "NASDAQ_AAPL"
      }
    ]
  },
  "Python": {
    "PythonPath": null,
    "MaxRetries": 3,
    "BackoffMs": 1000,
    "HealthCheckIntervalMs": 5000,
    "PipeConnectPollIntervalMs": 100,
    "DisposeWaitMs": 1000,
    "SyncTimeoutMinutes": 2,
    "EssentialPackages": [
      "setuptools",
      "wheel",
      "polars",
      "pandas",
      "yfinance",
      "pyarrow"
    ]
  },
  "ChartOptions": {
    "ChartMarginHorizontal": 20,
    "ChartMarginTop": 35,
    "ChartMarginBottom": 30,
    "LabelPadding": 4,
    "LabelHeight": 18,
    "ReverseWatchMargin": 10,
    "PointRadius": 4,
    "HoverPointRadius": 6,
    "HoverThresholdDistance": 15,
    "CurveWindowMargin": 60,
    "TimeAxisHeight": 25
  },
  "SmartScreener": {
    "ScreeningDataPath": "Data/Screening/latest_screening_data.json"
  },
  "Screener": {
    "DefaultSymbols": [ "AAPL", "MSFT", "GOOGL" ],
    "MaxFilterTreeDepth": 8,
    "MaxFilterTreeNodes": 64,
    "MaxFilterGroupNameLength": 32,
    "ConditionDragStartDistance": 4,
    "MaxConditionOffset": 500
  },
  "ScatterMetrics": {
    "MaxRegisteredMetrics": 32,
    "MaxOffset": 500,
    "MaxParallelism": 10,
    "InputDebounceMilliseconds": 300
  },
  "Tickers": {
    "AutoPlayDefaultIntervalSeconds": 5,
    "AutoPlayMaxIntervalSeconds": 3600,
    "AutoPlayDefaultStopAtListEnd": true,
    "AutoPlayDefaultStopOnListChange": true
  },
  "PythonIpc": {
    "RequestTimeoutMs": 60000,
    "MaxResponseBytes": 16777216,
    "MaxTimeoutRetries": 1
  },
  "PythonPackageCheck": {
    "ImportProbeTimeoutSeconds": 60,
    "UpdateCheckTimeoutSeconds": 5,
    "UpdateCheckRequestTimeoutSeconds": 3
  },
  "Backtest": {
    "MaxConditionOffset": 500,
    "MaxConditionTreeDepth": 8,
    "MaxConditionTreeNodes": 64,
    "MaxConditionGroupNameLength": 32,
    "ConditionDragStartDistance": 4
  },
  "DrawingInteraction": {
    "MaxHistoryEntriesPerContext": 128,
    "MaxHistoryPayloadBytesPerContext": 67108864,
    "MaxSessionHistoryPayloadBytes": 268435456,
    "MaxStrokeSamples": 65536
  },
  "PatternRecognition": {
    "MinWindow": 20,
    "MaxWindow": 60,
    "WindowStep": 5,
    "DefaultThreshold": 0.5
  },
  "MarketStructure": {
    "ZigzagThresholdPercent": 5.0
  },
  "Infrastructure": {
    "PipeName": "StockAnalyzerPipe",
    "PipeConnectionTimeoutMs": 5000,
    "ScreenerMaxParallelism": 10
  },
  "Localization": {
    "ResourcePath": "Resources/Locales"
  },
  "Resilience": {
    "CircuitBreaker": {
      "MinimumThroughput": 3,
      "FailureRatio": 0.5,
      "BreakDurationMs": 30000,
      "SamplingDurationMs": 60000
    }
  },
  "Logging": {
    "LogDirectory": "logs",
    "MinimumLevel": "Information"
  },
  "MarketDataSettings": {
    "DailyDataPath": "Data/Daily",
    "WeeklyDataPath": "Data/Weekly",
    "MonthlyDataPath": "Data/Monthly",
    "MetadataPath": "Data/Metadata",
    "TickerListPath": "StockAnalyzer.Python/tickers.json"
  },
  "RlPortfolio": {
    "MaxSymbols": 50,
    "HoldoutPercent": 25,
    "MaxWeightPercent": 100,
    "PartitionMode": "Ratio",
    "MinimumTrainBars": 192,
    "MinimumValidationBars": 192,
    "MinimumTestBars": 192
  }
}
```
